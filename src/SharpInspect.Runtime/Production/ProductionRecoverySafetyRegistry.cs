using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;

namespace SharpInspect.Runtime.Production;

/// <summary>
/// Resolves one explicitly bound recovery safety provider and validates each physical
/// stop observation.  This registry never supplies a safe default and never starts
/// overlapping observer calls.
/// </summary>
public sealed class ProductionRecoverySafetyRegistry : IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly ProductionRecoveryBinding _binding;
    private readonly IProductionRecoverySafetyProvider _provider;
    private readonly EventHandler<ProductionRecoverySafetySourceChangedEventArgs> _sourceChangedHandler;
    private ProductionRecoverySafetySourceState _source;
    private PendingObservation? _pending;
    private long _revision;
    private bool _disposed;
    private bool _disposing;

    public ProductionRecoverySafetyRegistry(
        ProductionRecoveryBinding binding,
        IProductionRecoverySafetyProvider provider)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ValidateProviderIdentity(binding.SafetySource, provider);
        _source = provider.Source ?? throw new ArgumentException(
            "ProductionRecoverySafetySourceMissing", nameof(provider));
        _sourceChangedHandler = (_, args) => OnSourceChanged(args);
        provider.SourceChanged += _sourceChangedHandler;
        // Read once more after subscribing so a source transition between the first
        // read and the subscription cannot be silently accepted.
        if (!TryReadSource(out _))
        {
            provider.SourceChanged -= _sourceChangedHandler;
            throw new ArgumentException("ProductionRecoverySafetySourceUnavailable", nameof(provider));
        }
    }

    public ProductionRecoveryBinding Binding => _binding;
    public IProductionRecoverySafetyProvider Provider => _provider;
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Raised after a provider reports source invalidation.</summary>
    public event EventHandler<ProductionRecoverySafetySourceChangedEventArgs>? SourceInvalidated;

    /// <summary>
    /// Returns the binary hash used by the existing PartIdentity deployment registry.
    /// Hosts use it when constructing the explicit provider binding.
    /// </summary>
    public static string ComputeProviderAssemblyHash(Type providerType)
    {
        ArgumentNullException.ThrowIfNull(providerType);
        return ProductionConfigurationBuilder.AssemblyHash(providerType.Assembly);
    }

    /// <summary>
    /// Captures one physical safety observation for the supplied Runtime epoch.
    /// The operation timeout cancels the provider request, but an uncooperative
    /// provider remains in the single in-flight slot until its actual task retires.
    /// </summary>
    public ValueTask<ProductionRecoverySafetyCapture> CaptureAsync(
        Guid runtimeEpoch, CancellationToken cancellationToken = default)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("ProductionRecoverySafetyRuntimeEpochRequired", nameof(runtimeEpoch));
        return CaptureCoreAsync(runtimeEpoch, cancellationToken);
    }

    /// <summary>Convenience overload which verifies the same frozen binding.</summary>
    public ValueTask<ProductionRecoverySafetyCapture> CaptureAsync(
        ProductionRecoveryBinding binding, Guid runtimeEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("ProductionRecoverySafetyRuntimeEpochRequired", nameof(runtimeEpoch));
        if (!string.Equals(binding.ContentHash, _binding.ContentHash, StringComparison.Ordinal))
            return ValueTask.FromResult(Failure(runtimeEpoch, "ProductionRecoverySafetyBindingChanged"));
        return CaptureCoreAsync(runtimeEpoch, cancellationToken);
    }

    /// <summary>
    /// Returns true only while the exact capture is still fresh, bound to the current
    /// source generation and at the same registry revision.
    /// </summary>
    public bool IsCurrent(ProductionRecoverySafetyCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_gate)
        {
            return IsCurrentLocked(capture, _source);
        }
    }

    /// <summary>
    /// Gives the concrete reason a capture cannot cross the final physical-send fence.
    /// A null result means the capture is still current.
    /// </summary>
    public string? FinalFailure(ProductionRecoverySafetyCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_gate)
        {
            if (!capture.Available || capture.Observation is null)
                return capture.ReasonCode;
            if (capture.Revision != _revision)
                return "ProductionRecoverySafetySourceChanged";
            if (!_source.Available)
                return "ProductionRecoverySafetySourceUnavailable";
            if (!capture.Source.Matches(_source))
                return "ProductionRecoverySafetySourceChanged";
            if (!IsCurrentLocked(capture, _source))
                return "ProductionRecoverySafetyCaptureStale";
            return null;
        }
    }

    /// <summary>
    /// Atomically checks the final capture fence and invokes the caller's physical
    /// send starter while the registry gate is held. The callback must only start
    /// the already prepared operation and return its task; it must not await it.
    /// </summary>
    public bool StartIfCurrent(
        ProductionRecoverySafetyCapture capture,
        Func<CancellationToken, Task> start,
        out Task? operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(start);
        operation = null;
        lock (_gate)
        {
            if (!IsCurrentLocked(capture, _source)) return false;
            operation = start(cancellationToken) ??
                throw new InvalidOperationException("ProductionRecoverySafetyOperationMissing");
            return true;
        }
    }

    private async ValueTask<ProductionRecoverySafetyCapture> CaptureCoreAsync(
        Guid runtimeEpoch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PendingObservation pending;
        long revision;
        ProductionRecoverySafetySourceState sourceBefore;
        lock (_gate)
        {
            if (_disposed) return FailureLocked(runtimeEpoch, "ProductionRecoverySafetyRegistryDisposed");
            if (_disposing) return FailureLocked(runtimeEpoch, "ProductionRecoverySafetyRegistryDisposing");
            if (!_source.Available)
                return FailureLocked(runtimeEpoch, "ProductionRecoverySafetySourceUnavailable");
            if (_pending is not null)
            {
                if (!_pending.IsRetired)
                    return FailureLocked(runtimeEpoch, "ProductionRecoverySafetyObservationInProgress");
                RemovePendingLocked(_pending);
            }

            revision = _revision;
            sourceBefore = _source;
            var stop = new CancellationTokenSource();
            pending = new PendingObservation(stop);
            _pending = pending;
        }
        // A provider is allowed to do synchronous setup before returning its
        // ValueTask. Never execute that setup while holding the registry gate.
        _ = Task.Run(() => RunObservationAsync(pending));

        ObservationExecution? execution = null;
        string? failure = null;
        try
        {
            execution = await pending.Completion.Task
                .WaitAsync(_binding.OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            failure = "ProductionRecoverySafetyObservationTimeout";
            _ = pending.RequestCancellation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = pending.RequestCancellation();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = "ProductionRecoverySafetyObservationCancelled";
        }
        catch (ObservationFailureException exception)
        {
            failure = exception.ReasonCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = "ProductionRecoverySafetyObservationUnavailable";
        }
        finally
        {
            RetireCompletedPending(pending);
        }

        if (failure is not null)
            return Failure(runtimeEpoch, failure, sourceBefore, revision);
        if (execution is null)
            return Failure(runtimeEpoch, "ProductionRecoverySafetyObservationMissing", sourceBefore, revision);

        CacheObservedSource(execution.Source);
        return ValidateCapture(runtimeEpoch, revision, sourceBefore, execution.Source,
            execution.ProviderBinding, execution.Observation);
    }

    private async Task RunObservationAsync(PendingObservation pending)
    {
        try
        {
            var observation = await _provider.ObserveAsync(pending.Stop.Token).ConfigureAwait(false);
            if (observation is null)
                throw new ObservationFailureException(
                    "ProductionRecoverySafetyObservationMissing");

            // Source and binding are deliberately read in the worker.  A provider
            // is allowed to expose a blocking hardware snapshot; the operation
            // timeout and pending-slot retirement must cover those reads as well.
            ProductionRecoverySafetySourceState source;
            try
            {
                source = _provider.Source ?? throw new InvalidOperationException();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new ObservationFailureException(
                    "ProductionRecoverySafetySourceUnavailable", exception);
            }

            ProductionRecoverySafetyProviderBinding providerBinding;
            try
            {
                providerBinding = _provider.Binding ?? throw new InvalidOperationException();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new ObservationFailureException(
                    "ProductionRecoverySafetyProviderUnavailable", exception);
            }

            pending.Completion.TrySetResult(new ObservationExecution(
                observation, source, providerBinding));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            pending.Completion.TrySetException(exception);
        }
        finally
        {
            RetireCompletedPending(pending);
        }
    }

    private ProductionRecoverySafetyCapture ValidateCapture(
        Guid runtimeEpoch,
        long revision,
        ProductionRecoverySafetySourceState sourceBefore,
        ProductionRecoverySafetySourceState sourceAfter,
        ProductionRecoverySafetyProviderBinding providerBinding,
        ProductionRecoverySafetyObservation observation)
    {
        lock (_gate)
        {
            if (_disposed) return FailureLocked(runtimeEpoch, "ProductionRecoverySafetyRegistryDisposed");
            if (revision != _revision || !sourceBefore.Matches(sourceAfter))
                return FailureLocked(runtimeEpoch, "ProductionRecoverySafetySourceChanged");
            if (!BindingMatches(_binding.SafetySource, observation.Binding,
                    out var bindingReason))
                return FailureLocked(runtimeEpoch, bindingReason);
            if (!_binding.SafetySource.ContentHash.Equals(providerBinding.ContentHash,
                    StringComparison.Ordinal))
                return FailureLocked(runtimeEpoch, "ProductionRecoverySafetyProviderBindingMismatch");
            if (observation.SourceEpoch != sourceAfter.SourceEpoch ||
                observation.SourceGeneration != sourceAfter.SourceGeneration)
                return FailureLocked(runtimeEpoch, "ProductionRecoverySafetySourceGenerationMismatch");
            if (!sourceAfter.Available)
                return FailureLocked(runtimeEpoch, "ProductionRecoverySafetySourceUnavailable");

            var statusReason = StatusReason(observation.Status);
            if (statusReason is not null)
                return FailureLocked(runtimeEpoch, statusReason);
            var freshnessReason = FreshnessReason(observation);
            if (freshnessReason is not null)
                return FailureLocked(runtimeEpoch, freshnessReason);

            return new ProductionRecoverySafetyCapture(runtimeEpoch, true,
                "ProductionRecoverySafetyLineStopped", revision, observation, sourceAfter);
        }
    }

    private bool IsCurrentLocked(ProductionRecoverySafetyCapture capture,
        ProductionRecoverySafetySourceState current)
    {
        if (_disposed || _disposing || !capture.Available || capture.Observation is null)
            return false;
        if (!current.Available) return false;
        if (capture.Revision != _revision || !capture.Source.Matches(current)) return false;
        if (!BindingMatches(_binding.SafetySource, capture.Observation.Binding, out _)) return false;
        var observation = capture.Observation;
        if (observation.SourceEpoch != current.SourceEpoch ||
            observation.SourceGeneration != current.SourceGeneration ||
            observation.Status != ProductionRecoverySafetyObservationStatus.SafeLineStopped)
            return false;
        return FreshnessReason(observation) is null;
    }

    private void CacheObservedSource(ProductionRecoverySafetySourceState source)
    {
        lock (_gate)
        {
            if (_source.Matches(source)) return;
            _source = source;
            if (!_disposed) Interlocked.Increment(ref _revision);
        }
    }

    private string? FreshnessReason(ProductionRecoverySafetyObservation observation)
    {
        if (observation.MonotonicFrequency != Stopwatch.Frequency)
            return "ProductionRecoverySafetyMonotonicFrequencyMismatch";
        var now = Stopwatch.GetTimestamp();
        if (observation.MonotonicTimestamp > now)
            return "ProductionRecoverySafetyObservationFromFuture";
        var ageTicks = now - observation.MonotonicTimestamp;
        var age = TimeSpan.FromSeconds(ageTicks / (double)observation.MonotonicFrequency);
        return age > _binding.SafetySource.FreshnessLimit
            ? "ProductionRecoverySafetyObservationStale" : null;
    }

    private static string? StatusReason(ProductionRecoverySafetyObservationStatus status) => status switch
    {
        ProductionRecoverySafetyObservationStatus.SafeLineStopped => null,
        ProductionRecoverySafetyObservationStatus.LineNotStopped =>
            "ProductionRecoverySafetyLineNotStopped",
        ProductionRecoverySafetyObservationStatus.Unavailable =>
            "ProductionRecoverySafetySourceUnavailable",
        ProductionRecoverySafetyObservationStatus.Stale =>
            "ProductionRecoverySafetyObservationStale",
        ProductionRecoverySafetyObservationStatus.Invalid =>
            "ProductionRecoverySafetyObservationInvalid",
        _ => "ProductionRecoverySafetyObservationStatusInvalid"
    };

    private static bool BindingMatches(
        ProductionRecoverySafetyProviderBinding expected,
        ProductionRecoverySafetyProviderBinding actual,
        out string reason)
    {
        if (!string.Equals(expected.StationId, actual.StationId, StringComparison.Ordinal))
        { reason = "ProductionRecoverySafetyStationMismatch"; return false; }
        if (!string.Equals(expected.EndpointBindingHash, actual.EndpointBindingHash,
                StringComparison.Ordinal))
        { reason = "ProductionRecoverySafetyEndpointBindingMismatch"; return false; }
        if (!string.Equals(expected.SourceId, actual.SourceId, StringComparison.Ordinal) ||
            !string.Equals(expected.SourceVersion, actual.SourceVersion, StringComparison.Ordinal) ||
            !string.Equals(expected.SourceConfigurationHash, actual.SourceConfigurationHash,
                StringComparison.Ordinal))
        { reason = "ProductionRecoverySafetySourceBindingMismatch"; return false; }
        if (!string.Equals(expected.ProviderTypeName, actual.ProviderTypeName,
                StringComparison.Ordinal) ||
            !string.Equals(expected.ProviderAssemblyHash, actual.ProviderAssemblyHash,
                StringComparison.Ordinal))
        { reason = "ProductionRecoverySafetyProviderIdentityMismatch"; return false; }
        if (!string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal))
        { reason = "ProductionRecoverySafetyBindingMismatch"; return false; }
        reason = string.Empty;
        return true;
    }

    private static void ValidateProviderIdentity(
        ProductionRecoverySafetyProviderBinding expected,
        IProductionRecoverySafetyProvider provider)
    {
        if (!BindingMatches(expected, provider.Binding, out var reason))
            throw new ArgumentException(reason, nameof(provider));
        var actualType = provider.GetType().AssemblyQualifiedName ?? provider.GetType().FullName;
        if (!string.Equals(expected.ProviderTypeName, actualType, StringComparison.Ordinal))
            throw new ArgumentException("ProductionRecoverySafetyProviderTypeMismatch", nameof(provider));
        var actualAssemblyHash = ComputeProviderAssemblyHash(provider.GetType());
        if (!string.Equals(expected.ProviderAssemblyHash, actualAssemblyHash,
                StringComparison.Ordinal))
            throw new ArgumentException("ProductionRecoverySafetyProviderAssemblyMismatch", nameof(provider));
    }

    private bool TryReadSource(out ProductionRecoverySafetySourceState source)
    {
        try
        {
            source = _provider.Source ?? throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_gate)
            {
                source = _source;
            }
            return false;
        }

        lock (_gate)
        {
            if (!_source.Matches(source))
            {
                _source = source;
                if (!_disposed) Interlocked.Increment(ref _revision);
            }
        }
        return true;
    }

    private void OnSourceChanged(ProductionRecoverySafetySourceChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        lock (_gate)
        {
            if (_disposed) return;
            _source = args.Source;
            Interlocked.Increment(ref _revision);
        }
        SourceInvalidated?.Invoke(this, args);
    }

    private void RetireCompletedPending(PendingObservation pending)
    {
        lock (_gate)
        {
            if (!pending.IsRetired) return;
            if (ReferenceEquals(_pending, pending)) RemovePendingLocked(pending);
        }
    }

    private void RemovePendingLocked(PendingObservation pending)
    {
        if (ReferenceEquals(_pending, pending)) _pending = null;
        pending.DisposeOnce();
    }

    private ProductionRecoverySafetyCapture Failure(Guid runtimeEpoch, string reasonCode,
        ProductionRecoverySafetySourceState? source = null, long? revision = null)
    {
        lock (_gate)
        {
            return FailureLocked(runtimeEpoch, reasonCode, source, revision);
        }
    }

    private ProductionRecoverySafetyCapture FailureLocked(Guid runtimeEpoch, string reasonCode,
        ProductionRecoverySafetySourceState? source = null, long? revision = null) =>
        new(runtimeEpoch, false, reasonCode, revision ?? _revision, null, source ?? _source);

    public async ValueTask DisposeAsync()
    {
        PendingObservation? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposing = true;
            pending = _pending;
        }

        if (pending is not null && !pending.IsRetired)
        {
            // Cancellation callbacks are provider code. Request cancellation outside
            // the registry gate and retain the request task until every callback has
            // actually returned; observation completion alone is insufficient.
            var cancellationTask = pending.RequestCancellation();
            try
            {
                await Task.WhenAll(pending.Completion.Task, cancellationTask)
                    .WaitAsync(_binding.RetirementTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("ProductionRecoverySafetyRetirementTimeout");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The provider task has retired; an unavailable observation is safe
                // to dispose and is not converted into a safe result.
            }
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (_pending is not null && !_pending.IsRetired)
                throw new TimeoutException("ProductionRecoverySafetyRetirementTimeout");
            _disposed = true;
            if (_pending is not null) RemovePendingLocked(_pending);
        }

        // Event accessors are provider code.  Unsubscribe only after the registry
        // state is sealed, never while holding the registry gate.
        _provider.SourceChanged -= _sourceChangedHandler;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_disposing)
                throw new InvalidOperationException("ProductionRecoverySafetyRetirementInProgress");
            if (_pending is not null && !_pending.IsRetired)
                throw new InvalidOperationException("ProductionRecoverySafetyRetirementRequired");
            _disposed = true;
            if (_pending is not null) RemovePendingLocked(_pending);
        }
        _provider.SourceChanged -= _sourceChangedHandler;
    }

    private sealed class PendingObservation
    {
        private readonly object _gate = new();
        private int _disposed;
        private Task? _cancellationTask;

        internal PendingObservation(CancellationTokenSource stop)
        {
            Stop = stop;
            Completion = new TaskCompletionSource<ObservationExecution>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal CancellationTokenSource Stop { get; }
        internal TaskCompletionSource<ObservationExecution> Completion { get; }

        internal bool IsRetired
        {
            get
            {
                lock (_gate)
                {
                    return Completion.Task.IsCompleted &&
                        (_cancellationTask is null || _cancellationTask.IsCompleted);
                }
            }
        }

        internal Task RequestCancellation()
        {
            lock (_gate)
            {
                if (_cancellationTask is not null) return _cancellationTask;
                _cancellationTask = Task.Run(() =>
                {
                    try { Stop.Cancel(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                });
                return _cancellationTask;
            }
        }

        internal void DisposeOnce()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Stop.Dispose();
        }
    }

    private sealed record ObservationExecution(
        ProductionRecoverySafetyObservation Observation,
        ProductionRecoverySafetySourceState Source,
        ProductionRecoverySafetyProviderBinding ProviderBinding);

    private sealed class ObservationFailureException : Exception
    {
        internal ObservationFailureException(string reasonCode, Exception? inner = null)
            : base(reasonCode, inner) => ReasonCode = reasonCode;

        internal string ReasonCode { get; }
    }
}
