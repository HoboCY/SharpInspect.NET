using System.Collections.Concurrent;
using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;

namespace SharpInspect.Runtime;

/// <summary>One immutable observation of an actual registered source, not a caller supplied gate.</summary>
internal sealed class PartIdentityBindingObservation
{
    internal PartIdentityBindingObservation(PartIdentityRequirement requirement, IPartIdentityProvider provider,
        PartIdentityProviderBinding binding, PartIdentityProviderCapabilities capabilities, long registryRevision)
    {
        RequirementHash = requirement.ContentHash;
        Provider = provider;
        Binding = binding;
        Capabilities = capabilities;
        RegistryRevision = registryRevision;
        ProviderBinaryHash = ProductionConfigurationBuilder.AssemblyHash(provider.GetType().Assembly);
        // Readiness and source epochs change at runtime. They must not replace a deployment's
        // static capability contract or require requalification for each scanner restart.
        StaticHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-part-identity-deployment-v1", RequirementHash, binding.ContentHash,
            capabilities.CanLatch.ToString(), capabilities.SourceKind.ToString(),
            capabilities.MaximumCallsPerCycle.ToString(CultureInfo.InvariantCulture),
            capabilities.BindingHash, ProviderBinaryHash
        });
        MaterialHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-part-identity-source-material-v1", StaticHash,
            capabilities.SourceEpoch.ToString("D"),
            capabilities.SourceGeneration.ToString(CultureInfo.InvariantCulture), capabilities.Ready.ToString()
        });
    }

    internal string RequirementHash { get; }
    internal IPartIdentityProvider Provider { get; }
    internal PartIdentityProviderBinding Binding { get; }
    internal PartIdentityProviderCapabilities Capabilities { get; }
    internal long RegistryRevision { get; }
    internal string ProviderBinaryHash { get; }
    internal string StaticHash { get; }
    internal string MaterialHash { get; }
}

internal sealed record PartIdentityBindingReadResult(string ReasonCode,
    PartIdentityBindingObservation? Observation = null)
{
    internal bool Available => Observation is not null;
}

/// <summary>Resolves exactly one provider; no competing reads, fallback, or caller asserted hashes.</summary>
internal sealed class PartIdentityBindingRegistry : IDisposable
{
    private readonly IPartIdentityProvider[] _providers;
    private readonly string? _plcSourceContractHash;
    private readonly ConcurrentDictionary<IPartIdentityProvider, Lazy<EventHandler<PartIdentitySourceChangedEventArgs>>> _subscriptions = new();
    private readonly ConcurrentDictionary<IPartIdentityProvider, long> _sourceRevisions = new();
    private readonly ConcurrentDictionary<IPartIdentityProvider, CapabilityRead> _reads = new();
    private long _revision;

    internal PartIdentityBindingRegistry(IEnumerable<IPartIdentityProvider> providers, string? plcSourceContractHash = null)
    { _providers = providers.ToArray(); _plcSourceContractHash = plcSourceContractHash; }

    internal event Action<IPartIdentityProvider>? SourceChanged;
    internal long Revision => Interlocked.Read(ref _revision);
    internal long RevisionFor(IPartIdentityProvider provider) => _sourceRevisions.GetOrAdd(provider, 0);

    internal async ValueTask<PartIdentityBindingReadResult> CaptureAsync(PartIdentityRequirement? requirement,
        CancellationToken token)
    {
        if (requirement is null) return new("PartIdentityDeclarationMissing");
        if (requirement.Mode == PartIdentityRequirementMode.None) return new("PartIdentityExplicitNone");
        try
        {
            // Binding metadata is immutable, and is never obtained under a Runtime/SQLite lock.
            var matches = _providers.Select(provider => (Provider: provider, Binding: provider.Binding))
                .Where(item => item.Binding.LogicalRole == requirement.LogicalRole).ToArray();
            if (matches.Length != 1) return new(matches.Length == 0 ?
                "PartIdentityBindingUnavailable" : "PartIdentityBindingAmbiguous");
            var (provider, binding) = matches[0];
            if (binding.Format.ToContractReference() != requirement.Format)
                return new("PartIdentityFormatContractMismatch");
            if (binding.SourceKind == PartIdentityProviderSourceKind.StablePlc &&
                binding.SourceContractHash != _plcSourceContractHash)
                return new("PartIdentityPlcReadPlanMismatch");
            _ = _subscriptions.GetOrAdd(provider, value => new Lazy<EventHandler<PartIdentitySourceChangedEventArgs>>(() =>
            {
                // Bind the notification to the registration, even when an adapter
                // forwards an event whose sender is an inner source object.
                EventHandler<PartIdentitySourceChangedEventArgs> handler = (_, _) => OnSourceChanged(value);
                value.SourceChanged += handler;
                return handler;
            })).Value;
            var revision = RevisionFor(provider);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            stop.CancelAfter(binding.LatchTimeout);
            var capabilityToken = stop.Token;
            if (_reads.TryGetValue(provider, out var completed) && completed.Operation.IsValueCreated &&
                completed.Operation.Value.IsCompleted) RemoveRead(provider, completed);
            var candidate = new CapabilityRead(revision, stop,
                new Lazy<Task<PartIdentityProviderCapabilities>>(() => Task.Run(async () =>
                    await provider.GetCapabilitiesAsync(capabilityToken).ConfigureAwait(false))));
            var pending = _reads.GetOrAdd(provider, candidate);
            if (!ReferenceEquals(candidate, pending)) stop.Dispose();
            var operation = pending.Operation.Value;
            PartIdentityProviderCapabilities capabilities;
            try { capabilities = await operation.WaitAsync(binding.LatchTimeout, token).ConfigureAwait(false); }
            finally
            {
                // A provider which ignores cancellation keeps its single slot. Subsequent
                // captures cannot launch overlapping calls while that operation is unretired.
                if (operation.IsCompleted) RemoveRead(provider, pending);
                else _ = operation.ContinueWith(task =>
                {
                    _ = task.Exception;
                    RemoveRead(provider, pending);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            if (pending.Revision != revision || revision != RevisionFor(provider) || provider.Binding.ContentHash != binding.ContentHash)
                return new("PartIdentitySourceChangedDuringObservation");
            if (!capabilities.CanLatch || capabilities.BindingHash != binding.ContentHash ||
                capabilities.SourceKind != binding.SourceKind ||
                capabilities.MaximumCallsPerCycle != binding.MaximumCallsPerCycle)
                return new("PartIdentityProviderCapabilityMismatch");
            return new("PartIdentityBindingVerified", new(requirement, provider, binding, capabilities, revision));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(exception is TimeoutException or OperationCanceledException ?
            "PartIdentityCapabilityTimeout" : "PartIdentityCapabilityUnavailable"); }
    }

    private void OnSourceChanged(IPartIdentityProvider provider)
    {
        Interlocked.Increment(ref _revision);
        _sourceRevisions.AddOrUpdate(provider, 1, (_, current) => checked(current + 1));
        SourceChanged?.Invoke(provider);
    }

    private void RemoveRead(IPartIdentityProvider provider, CapabilityRead expected)
    {
        if (((ICollection<KeyValuePair<IPartIdentityProvider, CapabilityRead>>)_reads).Remove(new(provider, expected)))
            expected.Stop.Dispose();
    }

    private sealed record CapabilityRead(long Revision, CancellationTokenSource Stop,
        Lazy<Task<PartIdentityProviderCapabilities>> Operation);

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
            if (subscription.Value.IsValueCreated) subscription.Key.SourceChanged -= subscription.Value.Value;
    }
}
