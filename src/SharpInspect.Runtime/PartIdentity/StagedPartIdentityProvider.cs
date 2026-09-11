using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.PartIdentity;

/// <summary>
/// A development-controlled in-memory staged/scanner provider.  Stage tokens are
/// associated with one exact cycle and are atomically compare-and-consumed.  A null
/// token in a latch request means "the unique value for this cycle"; it never means
/// "take the newest value".
/// </summary>
public sealed class StagedPartIdentityProvider : IPartIdentityProvider
{
    private const int MaximumPendingEntries = 256;

    private sealed record StagedValue(PartIdentityCycleBinding Cycle, string Value,
        long SourceSequence, DateTimeOffset ObservedAtUtc, long MonotonicTimestamp,
        long MonotonicFrequency, Guid SourceEpoch, long SourceGeneration);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, StagedValue> _values = new();
    private readonly HashSet<Guid> _consumedTokens = new();
    private readonly PartIdentityProviderBinding _binding;
    private Guid _sourceEpoch;
    private long _sourceGeneration;
    private long _nextSourceSequence;
    private PartIdentityProviderCapabilities _capabilities;

    public StagedPartIdentityProvider(PartIdentityProviderBinding binding)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (binding.SourceKind != PartIdentityProviderSourceKind.Staged)
            throw new ArgumentException("PartIdentityStagedProviderBindingRequired", nameof(binding));
        _sourceEpoch = Guid.NewGuid();
        _sourceGeneration = 1;
        _capabilities = BuildCapabilities();
    }

    public PartIdentityProviderBinding Binding => _binding;

    public PartIdentityProviderCapabilities Capabilities
    {
        get { lock (_gate) return _capabilities; }
    }

    public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged;

    public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Capabilities);
    }

    /// <summary>Stages one scanner value for the exact runtime/controller cycle.</summary>
    public PartIdentityStageResult Stage(PartIdentityStageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_values.ContainsKey(request.StageToken) || _consumedTokens.Contains(request.StageToken))
                return new PartIdentityStageResult(false, "PartIdentityStageTokenAlreadyUsed", request.StageToken);
            if (_values.Count >= MaximumPendingEntries)
                return new PartIdentityStageResult(false, "PartIdentityStageCapacityExceeded", request.StageToken);

            var sequence = Interlocked.Increment(ref _nextSourceSequence);
            var timestamp = Stopwatch.GetTimestamp();
            _values.Add(request.StageToken, new StagedValue(request.Cycle, request.Value, sequence,
                DateTimeOffset.UtcNow, timestamp, Stopwatch.Frequency, _sourceEpoch, _sourceGeneration));
            return new PartIdentityStageResult(true, "PartIdentityStaged", request.StageToken);
        }
    }

    /// <summary>
    /// Revokes pending values and advances source identity.  Existing capabilities and
    /// requests cannot be reused after this call.
    /// </summary>
    public PartIdentityProviderCapabilities RestartSource()
    {
        PartIdentityProviderCapabilities capabilities;
        lock (_gate)
        {
            foreach (var token in _values.Keys) _consumedTokens.Add(token);
            _values.Clear();
            if (_sourceGeneration == long.MaxValue)
                throw new InvalidOperationException("PartIdentitySourceGenerationExhausted");
            _sourceGeneration++;
            _sourceEpoch = Guid.NewGuid();
            _capabilities = BuildCapabilities();
            capabilities = _capabilities;
        }
        SourceChanged?.Invoke(this, new PartIdentitySourceChangedEventArgs(capabilities));
        return capabilities;
    }

    public ValueTask<PartIdentityProviderObservation> TryLatchAsync(
        PartIdentityLatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!string.Equals(request.Binding.ContentHash, Binding.ContentHash, StringComparison.Ordinal))
                return ValueTask.FromResult(ErrorObservation(request.Cycle,
                    "PartIdentityProviderBindingMismatch", request.StageToken));
            if (request.SourceEpoch != _sourceEpoch || request.SourceGeneration != _sourceGeneration)
                return ValueTask.FromResult(ErrorObservation(request.Cycle,
                    "PartIdentitySourceGenerationMismatch", request.StageToken));

            Guid? token = request.StageToken;
            StagedValue? staged = null;
            if (token is Guid explicitToken)
            {
                if (!_values.TryGetValue(explicitToken, out staged))
                    return ValueTask.FromResult(MissingObservation(request.Cycle, explicitToken));
                if (!staged.Cycle.Matches(request.Cycle))
                    return ValueTask.FromResult(InvalidObservation(request.Cycle, explicitToken,
                        "PartIdentityStageAssociationMismatch"));
            }
            else
            {
                var matches = _values.Where(item => item.Value.Cycle.Matches(request.Cycle)).ToArray();
                if (matches.Length == 0)
                    return ValueTask.FromResult(MissingObservation(request.Cycle, null));
                if (matches.Length != 1)
                    return ValueTask.FromResult(new PartIdentityProviderObservation(Binding, request.Cycle,
                        PartIdentityObservationStatus.Ambiguous, null, "PartIdentityStageAssociationAmbiguous",
                        NextObservationSequence(), DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
                        Stopwatch.Frequency, _sourceEpoch, _sourceGeneration));
                token = matches[0].Key;
                staged = matches[0].Value;
            }

            if (staged is null)
                return ValueTask.FromResult(InvalidObservation(request.Cycle, token,
                    "PartIdentityStageAssociationMissing"));
            if (staged.SourceEpoch != _sourceEpoch || staged.SourceGeneration != _sourceGeneration)
                return ValueTask.FromResult(InvalidObservation(request.Cycle, token,
                    "PartIdentitySourceGenerationMismatch"));

            // Remove while holding the same lock used to select the value.  A concurrent
            // caller can therefore observe only one Present result.
            _values.Remove(token!.Value);
            _consumedTokens.Add(token.Value);
            return ValueTask.FromResult(new PartIdentityProviderObservation(Binding, staged.Cycle,
                PartIdentityObservationStatus.Present, staged.Value, "PartIdentityStaged",
                staged.SourceSequence, staged.ObservedAtUtc, staged.MonotonicTimestamp,
                staged.MonotonicFrequency, _sourceEpoch, _sourceGeneration, token));
        }
    }

    private PartIdentityProviderCapabilities BuildCapabilities() =>
        new(true, true, Binding.SourceKind, Binding.MaximumCallsPerCycle,
            Binding.ContentHash, _sourceEpoch, _sourceGeneration);

    private PartIdentityProviderObservation MissingObservation(PartIdentityCycleBinding cycle,
        Guid? stageToken) => new(Binding, cycle, PartIdentityObservationStatus.Missing, null,
        "PartIdentityStageTokenMissing", NextObservationSequence(), DateTimeOffset.UtcNow,
        Stopwatch.GetTimestamp(), Stopwatch.Frequency, _sourceEpoch, _sourceGeneration, stageToken);

    private PartIdentityProviderObservation InvalidObservation(PartIdentityCycleBinding cycle,
        Guid? stageToken, string reasonCode) => new(Binding, cycle, PartIdentityObservationStatus.Invalid,
        null, reasonCode, NextObservationSequence(), DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
        Stopwatch.Frequency, _sourceEpoch, _sourceGeneration, stageToken);

    private PartIdentityProviderObservation ErrorObservation(PartIdentityCycleBinding cycle,
        string reasonCode, Guid? stageToken) => new(Binding, cycle, PartIdentityObservationStatus.Error,
        null, reasonCode, NextObservationSequence(), DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
        Stopwatch.Frequency, _sourceEpoch, _sourceGeneration, stageToken);

    private long NextObservationSequence() => Interlocked.Increment(ref _nextSourceSequence);
}
