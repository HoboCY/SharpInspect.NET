using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Plc;

/// <summary>
/// Consumes an identity snapshot already read by the production Modbus owner.
/// This adapter owns no connection and performs no additional register reads.
/// </summary>
public sealed class ModbusPartIdentityProvider : IPartIdentityProvider
{
    private readonly object _gate = new();
    private readonly HashSet<(uint Epoch, uint Revision)> _consumed = new();

    public ModbusPartIdentityProvider(PartIdentityProviderBinding binding, ModbusPartIdentityReadPlan plan)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ArgumentNullException.ThrowIfNull(plan);
        if (binding.SourceKind != PartIdentityProviderSourceKind.StablePlc || binding.SourceContractHash != plan.ContentHash)
            throw new ArgumentException("PartIdentityPlcReadPlanMismatch", nameof(binding));
        Capabilities = new(true, true, binding.SourceKind, binding.MaximumCallsPerCycle,
            binding.ContentHash, Guid.NewGuid(), 1);
    }

    public PartIdentityProviderBinding Binding { get; }
    public PartIdentityProviderCapabilities Capabilities { get; }
    // This instance's binding and source generation cannot change. PLC epochs are
    // observed and independently fenced by the existing communication owner.
    public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged { add { } remove { } }

    public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Capabilities);
    }

    public ValueTask<PartIdentityProviderObservation> TryLatchAsync(PartIdentityLatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var proof = request.StablePlcSnapshot ?? throw new ArgumentException("PartIdentityPlcProofRequired", nameof(request));
        var status = proof.Status;
        var reason = "PartIdentityPlcSnapshotConsumed";
        if (request.Binding.ContentHash != Binding.ContentHash ||
            request.SourceEpoch != Capabilities.SourceEpoch || request.SourceGeneration != Capabilities.SourceGeneration ||
            proof.SourceContractHash != Binding.SourceContractHash || !proof.Cycle.Matches(request.Cycle) ||
            proof.Revision == 0 || (proof.Revision & 1) != 0)
        { status = PartIdentityObservationStatus.Invalid; reason = "PartIdentityPlcBindingMismatch"; }
        string? value = null;
        if (status == PartIdentityObservationStatus.Present && !proof.TryGetUtf8Value(out value))
        { status = PartIdentityObservationStatus.Invalid; reason = "PartIdentityPlcUtf8Invalid"; }
        if (status is PartIdentityObservationStatus.Present or PartIdentityObservationStatus.Missing)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_consumed.Count >= 10000)
                { status = PartIdentityObservationStatus.Error; reason = "PartIdentitySourceTokenCapacityExceeded"; }
                else if (!_consumed.Add((proof.Cycle.ControllerEpoch, proof.Revision)))
                { status = PartIdentityObservationStatus.Stale; reason = "PartIdentitySourceTokenAlreadyConsumed"; }
            }
        }
        return ValueTask.FromResult(new PartIdentityProviderObservation(Binding, request.Cycle, status,
            status == PartIdentityObservationStatus.Present ? value : null, reason, proof.SourceSequence,
            proof.ObservedAtUtc, proof.MonotonicTimestamp, proof.MonotonicFrequency,
            Capabilities.SourceEpoch, Capabilities.SourceGeneration, stablePlcSnapshot: proof));
    }
}
