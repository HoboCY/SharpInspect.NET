using System.Buffers.Binary;
using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.PartIdentity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async ValueTask<RuntimeCommandOutcome> SubmitPartIdentityCorrectionAsync(
        CorrectProductionPartIdentityCommand command, CancellationToken token)
    {
        Guid epoch;
        lock (_sync)
        {
            if (_disposed || _shutdownRequested)
                return new(command.CorrelationId, CommandDisposition.Rejected, "RuntimeStopped", AuditPersistence.NotAttempted);
            if (_authorization is null || _audit is not SqliteCommandStore { PartIdentityEnabled: true })
                return new(command.CorrelationId, CommandDisposition.Rejected,
                    "PartIdentityCorrectionConfigurationRequired", AuditPersistence.NotAttempted);
            epoch = _snapshot.RuntimeEpoch;
        }
        await _storeInitialization.WaitAsync(_audit.CommitTimeout, token).ConfigureAwait(false);
        var result = await _authorization.CorrectProductionPartIdentityAsync(command, epoch, token).ConfigureAwait(false);
        if (result.Outcome.Audit == AuditPersistence.Unavailable)
            MarkAuditFault("PartIdentityCorrectionAuditUnavailable");
        return result.Outcome;
    }

    private async Task<PartIdentityLatchAttempt?> LatchProductionPartIdentityAsync(ProductionInspectionOwner owner,
        ModbusControllerSignals signals, RecipeActivationSnapshot baseline)
    {
        var requirement = baseline.Release.Source.Content.PartIdentityRequirement;
        if (requirement?.Mode == PartIdentityRequirementMode.None) return null;
        var attempt = new PartIdentityLatchAttempt(requirement, signals);
        owner.PartIdentityAttempt = attempt;
        if (requirement is null || _partIdentityRegistry is null)
            throw new PartIdentityAdmissionRejectedException("PartIdentityBindingUnavailable", attempt);
        PartIdentityCycleBinding cycle;
        lock (_sync)
        {
            if (!CanAcceptProductionTriggerLocked(owner))
                throw new PartIdentityAdmissionRejectedException("ProductionInspectionTriggerPermitRevoked", attempt);
            cycle = new(owner.RuntimeEpoch, _productionInspectionOptions!.Profile.EndpointBindingHash,
                owner.Health!.ConnectionGeneration, signals.ControllerEpoch, signals.CycleSequence);
            attempt.AdmissionGeneration = _admissionGeneration;
        }
        var resolved = await _partIdentityRegistry.CaptureAsync(requirement, owner.Cancellation.Token).ConfigureAwait(false);
        if (resolved.Observation is not { } source || !source.Capabilities.Ready)
            throw new PartIdentityAdmissionRejectedException(resolved.Available ?
                "PartIdentitySourceNotReady" : resolved.ReasonCode, attempt);
        attempt.Source = source;
        lock (_sync)
        {
            if (!CanAcceptProductionTriggerLocked(owner) || _admissionGeneration != attempt.AdmissionGeneration ||
                _productionDeploymentObservation?.PartIdentity?.MaterialHash != source.MaterialHash)
                throw new PartIdentityAdmissionRejectedException("PartIdentitySourceChangedBeforeLatch", attempt);
        }
        PartIdentityStablePlcSnapshot? proof = null;
        if (source.Binding.SourceKind == PartIdentityProviderSourceKind.StablePlc)
        {
            if (signals.PartIdentity is not { } snapshot || snapshot.FailureReason is not null ||
                snapshot.Plan.ContentHash != source.Binding.SourceContractHash)
                throw new PartIdentityAdmissionRejectedException(signals.PartIdentity?.FailureReason ??
                    "PartIdentityPlcObservationUnavailable", attempt);
            proof = CreatePartIdentityPlcProof(snapshot, cycle);
        }
        var request = attempt.Request = new(source.Binding, cycle, 1, source.Capabilities.SourceEpoch,
            source.Capabilities.SourceGeneration, stablePlcSnapshot: proof);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(owner.Cancellation.Token);
        stop.CancelAfter(source.Binding.LatchTimeout);
        var providerToken = stop.Token;
        // Provider code runs without the runtime lock, command gate, session lease, or SQLite transaction.
        // Preserve the actual task until retirement even if it ignores its cancellation deadline.
        var operation = owner.PartIdentityOperation = Task.Run(async () =>
            await source.Provider.TryLatchAsync(request, providerToken).ConfigureAwait(false));
        _ = operation.ContinueWith(_ => stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        PartIdentityProviderObservation observation;
        try
        {
            observation = await operation.WaitAsync(source.Binding.LatchTimeout, owner.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new PartIdentityAdmissionRejectedException(exception is TimeoutException ? "PartIdentityLatchTimeout" :
                exception is OperationCanceledException ? "PartIdentityLatchCancelled" : "PartIdentityLatchFailed", attempt);
        }
        attempt.Observation = observation;
        var validation = PartIdentityEvidenceValidator.Validate(requirement, source.Binding, source.Capabilities, request, observation,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        if (!validation.Accepted)
            throw new PartIdentityAdmissionRejectedException(validation.ReasonCode, attempt);
        attempt.Evidence = validation.Evidence;
        var current = await _partIdentityRegistry.CaptureAsync(requirement, owner.Cancellation.Token).ConfigureAwait(false);
        if (current.Observation is not { } after || after.MaterialHash != source.MaterialHash ||
            after.RegistryRevision != source.RegistryRevision)
            throw new PartIdentityAdmissionRejectedException("PartIdentitySourceChangedAfterLatch", attempt);
        return attempt;
    }

    private bool PartIdentityAdmissionStillCurrentLocked(ProductionInspectionOwner owner, PartIdentityLatchAttempt? attempt)
    {
        if (attempt is null) return true;
        var latest = owner.Observer?.Latest.Signals;
        return attempt.Evidence is not null && attempt.Source is { } source && attempt.Request is { } request &&
            _partIdentityRegistry?.RevisionFor(source.Provider) == source.RegistryRevision &&
            _admissionGeneration == attempt.AdmissionGeneration &&
            owner.Health?.ConnectionGeneration == request.Cycle.ConnectionGeneration &&
            latest?.ControllerEpoch == request.Cycle.ControllerEpoch && latest.CycleSequence == request.Cycle.CycleSequence &&
            !latest.ResultAck && _productionDeploymentObservation?.PartIdentity?.MaterialHash == source.MaterialHash;
    }

    private static string? PartIdentityAllocationFreshnessFailure(PartIdentityLatchAttempt? attempt)
    {
        if (attempt?.Observation is not { } observation || attempt.Source is not { } source) return null;
        var now = Stopwatch.GetTimestamp();
        return observation.MonotonicFrequency != Stopwatch.Frequency || observation.MonotonicTimestamp > now ||
            (now - observation.MonotonicTimestamp) / (double)Stopwatch.Frequency > source.Binding.FreshnessLimit.TotalSeconds
                ? "PartIdentityObservationStale" : null;
    }

    private sealed class PartIdentityLatchAttempt
    {
        internal PartIdentityLatchAttempt(PartIdentityRequirement? requirement, ModbusControllerSignals signals)
        { Requirement = requirement; Signals = signals; }
        internal PartIdentityRequirement? Requirement { get; }
        internal ModbusControllerSignals Signals { get; }
        internal long AdmissionGeneration { get; set; }
        internal PartIdentityBindingObservation? Source { get; set; }
        internal PartIdentityLatchRequest? Request { get; set; }
        internal PartIdentityProviderObservation? Observation { get; set; }
        internal PartIdentityEvidence? Evidence { get; set; }
        internal bool RejectionRecorded { get; set; }
    }

    private sealed class PartIdentityAdmissionRejectedException : Exception
    {
        internal PartIdentityAdmissionRejectedException(string reason, PartIdentityLatchAttempt attempt) : base(reason) => Attempt = attempt;
        internal PartIdentityLatchAttempt Attempt { get; }
    }

    private async Task RecordRejectedProductionTriggerAsync(ProductionInspectionOwner owner,
        ModbusControllerSignals signals, string reason, PartIdentityLatchAttempt? attempt = null)
    {
        if (attempt?.RejectionRecorded == true) return;
        if (_audit is not SqliteCommandStore { PartIdentityEnabled: true } store)
        {
            // Preserve the pre-T43 None deployment. A non-None recipe cannot arm
            // unless this writer is configured; reaching here for it is a fault.
            if (attempt is not null) throw new InvalidOperationException("PartIdentityRejectionWriterUnavailable");
            return;
        }
        PartIdentityRequirement? requirement;
        long generation;
        lock (_sync)
        {
            requirement = attempt?.Requirement ?? _activeActivation?.Snapshot.Release.Source.Content.PartIdentityRequirement;
            generation = attempt?.Request?.Cycle.ConnectionGeneration ?? owner.Health?.ConnectionGeneration ?? 0;
        }
        var raw = signals.PartIdentity;
        var context = new PartIdentityRejectionContext(requirement, attempt?.Source?.Binding,
            attempt?.Observation, generation, raw?.RawBlock, readEvidenceHash: raw?.ContentHash);
        var id = Guid.NewGuid();
        var rejected = new PartIdentityHistoryEvent(1, null, id, PartIdentityHistoryEventKind.RejectedTrigger,
            id, id, owner.RuntimeEpoch, _productionInspectionOptions!.StationId,
            signals.ControllerEpoch, signals.CycleSequence, _productionInspectionOptions.Profile.EndpointBindingHash,
            null, reason, recordedAtUtc: DateTimeOffset.UtcNow, rejectionContext: context);
        // An uncertain/late writer result must never enqueue a second event for the same attempt.
        if (attempt is not null) attempt.RejectionRecorded = true;
        var deadline = new StoreDeadline(_audit.CommitTimeout);
        var committed = await store.AppendPartIdentityEventAsync(new(rejected), deadline,
            CancellationToken.None).AsTask().WaitAsync(PositiveRemaining(deadline)).ConfigureAwait(false);
        if (!committed.Committed)
        {
            MarkAuditFault("PartIdentityRejectionJournalUnavailable", alarmAuthorityUnavailable: true);
            throw new InvalidOperationException(committed.ReasonCode);
        }
    }

    private static async Task RetirePartIdentityOperationAsync(ProductionInspectionOwner owner)
    {
        if (owner.PartIdentityOperation is not { } operation) return;
        try { await operation.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        owner.PartIdentityOperation = null;
    }

    private void PartIdentitySourceChanged(IPartIdentityProvider provider)
    {
        lock (_sync)
        {
            if (_disposed || _shutdownRequested ||
                _activeActivation?.Snapshot.Release.Source.Content.PartIdentityRequirement?.Mode is
                    null or PartIdentityRequirementMode.None ||
                !ReferenceEquals(_productionDeploymentObservation?.PartIdentity?.Provider, provider)) return;
            _admissionGeneration = checked(_admissionGeneration + 1);
            PublishUnavailableProductionAdmissionLocked("PartIdentitySourceChanged");
        }
    }

    private bool ProductionPartIdentityReadyLocked(PartIdentityRequirement? requirement)
    {
        if (requirement?.Mode == PartIdentityRequirementMode.None) return true;
        return requirement is not null && _partIdentityRegistry is not null &&
            _audit is SqliteCommandStore { PartIdentityEnabled: true } &&
            _productionInspectionStoreOptions?.PartIdentities is not null &&
            _productionDeploymentObservation is { PartIdentity: { } observed } deployment &&
            deployment.ActivationHash == _activeActivation?.Snapshot.ContentHash &&
            observed.RequirementHash == requirement.ContentHash &&
            observed.RegistryRevision == _partIdentityRegistry.RevisionFor(observed.Provider) && observed.Capabilities.Ready &&
            DateTimeOffset.UtcNow - deployment.ObservedAtUtc < TimeSpan.FromSeconds(10);
    }

    private bool ShouldReadProductionPartIdentity()
    {
        lock (_sync)
            return _activeActivation?.Snapshot.Release.Source.Content.PartIdentityRequirement?.Mode is
                    PartIdentityRequirementMode.Required or PartIdentityRequirementMode.Optional &&
                _productionDeploymentObservation?.PartIdentity?.Binding.SourceKind == PartIdentityProviderSourceKind.StablePlc;
    }

    private static PartIdentityStablePlcSnapshot CreatePartIdentityPlcProof(
        ModbusPartIdentitySnapshot snapshot, PartIdentityCycleBinding cycle)
    {
        var block = snapshot.RawBlock.ToArray();
        var state = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(12, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(14, 2));
        return new(cycle, snapshot.Plan.ContentHash, snapshot.Revision, state,
            snapshot.Missing ? PartIdentityObservationStatus.Missing : PartIdentityObservationStatus.Present,
            block.Skip(16).Take(length), snapshot.Revision, snapshot.ReceivedAtUtc,
            snapshot.ReceivedMonotonicTimestamp, Stopwatch.Frequency, snapshot.ContentHash);
    }
}
