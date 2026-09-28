using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task AdmitProductionTriggerAsync(ProductionInspectionOwner owner, ModbusControllerSignals signals)
    {
        var options = _productionInspectionOptions!;
        var policy = await ReadProductionInspectionPolicyAsync(owner.Cancellation.Token).ConfigureAwait(false);
        var active = await new SqliteRecipeActivationQuery(_productionInspectionStoreOptions!)
            .ReadCurrentAsync(owner.Cancellation.Token).ConfigureAwait(false);
        if (!active.Available || active.RecoveryRequired || active.Record is not { ProductionAuthority: true,
                SuccessfulSnapshot: { } baseline } record)
            throw new InvalidOperationException("ProductionInspectionActiveRecipeUnavailable");
        var partIdentity = await LatchProductionPartIdentityAsync(owner, signals, baseline).ConfigureAwait(false);
        var capturePolicy = ResolveProductionCapturePolicy(baseline.Release.Source.Content);
        var deadline = new StoreDeadline(_audit!.CommitTimeout);
        if (!await _commandGate.WaitAsync(PositiveRemaining(deadline), owner.Cancellation.Token).ConfigureAwait(false))
            throw new InvalidOperationException("ProductionInspectionAdmissionBusy");
        try
        {
            ProductionInspectionAdmission admission;
            lock (_sync)
            {
                if (PartIdentityAllocationFreshnessFailure(partIdentity) is { } identityFailure)
                    throw new PartIdentityAdmissionRejectedException(identityFailure, partIdentity!);
                if (!CanAcceptProductionTriggerLocked(owner) || _activeActivation!.Snapshot.ContentHash != baseline.ContentHash ||
                    owner.Health!.ControllerEpoch != signals.ControllerEpoch ||
                    !PartIdentityAdmissionStillCurrentLocked(owner, partIdentity))
                    throw new OperationCanceledException("ProductionInspectionTriggerPermitRevoked");
                var inspectionId = Guid.NewGuid();
                admission = new(capturePolicy, inspectionId, inspectionId, owner.RuntimeEpoch, options.StationId, _admissionGeneration,
                    new(signals.ControllerEpoch, signals.CycleSequence), options.EvidenceRequirement,
                    record.Reference, baseline, options.Profile.EndpointBindingHash, options.Profile.ContentHash,
                    options.Profile.CommunicationBinding.Policy.ContentHash, owner.Health.ConnectionGeneration,
                    owner.Health.RecoveryAttempt, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), policy,
                    Array.Empty<TraceRetentionObligation>(), partIdentity?.Evidence);
                owner.Current = admission;
                owner.CycleRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.Prepared = _activeActivation.Algorithm;
                owner.Coordinator.SetPhase(InspectionCyclePhase.Accepted);
                PublishLocked(_snapshot with { Ready = false, Busy = false,
                    CurrentExecution = new(ExecutionKind.Production, admission.CorrelationId) });
            }
            var committed = await WriteProductionWithFenceAsync(() => ((SqliteCommandStore)_audit).AdmitProductionInspectionAsync(
                new(admission, () => ProductionCommitFailure(owner, admissionOnly: true)), deadline,
                owner.Cancellation.Token), deadline).ConfigureAwait(false);
            if (!committed.Committed)
            {
                if (committed.ReasonCode is not ("ProductionInspectionAdmissionRevoked" or
                    "ProductionInspectionCommitFenceBusy"))
                    throw new InvalidOperationException(committed.ReasonCode);
                Task? cancellation;
                CancellationTokenSource retiredCancellation;
                lock (_sync)
                {
                    cancellation = owner.ExecutionCancellationTask;
                    retiredCancellation = owner.ExecutionCancellation;
                    owner.Current = null;
                    owner.Core = null;
                    owner.Prepared = null;
                    owner.ExecutionCancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Cancellation.Token);
                    owner.ExecutionCancellationTask = null;
                    owner.FaultAbortRequested = false;
                    owner.Coordinator.SetPhase(InspectionCyclePhase.AwaitRequest);
                    owner.CycleRetired?.TrySetResult(true);
                    ProjectProductionProgressLocked(owner);
                }
                try { if (cancellation is not null) await cancellation.ConfigureAwait(false); }
                finally { retiredCancellation.Dispose(); }
                throw new OperationCanceledException("ProductionInspectionTriggerPermitRevoked");
            }
            owner.AdmissionCommitted = true;
            if (committed.Admission?.ContentHash != admission.ContentHash)
                throw new InvalidOperationException(committed.ReasonCode);
            lock (_sync)
            {
                owner.Current = committed.Admission;
                ProjectProductionProgressLocked(owner);
            }
            if (deadline.Expired) throw new TimeoutException("ProductionInspectionAdmissionCommitTimeout");
            await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }

    private async Task<InspectionCycleCommitReceipt<PlcResultPayloadSnapshot>?> CommitProductionCoreAsync(
        ProductionInspectionOwner owner, InspectionCycleExecutionResult<PlcResultPayloadSnapshot> result)
    {
        var admission = owner.Current ?? throw new InvalidOperationException("ProductionInspectionAdmissionMissing");
        if (result.Payload is null || result.Outcome is null && result.AcquisitionFailure is null &&
            !(result.Status == ExecutionStatus.Cancelled && result.Metadata is not null && owner.FaultAbortRequested))
            return null;
        await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
        var staged = await PrepareProductionImageEvidenceAsync(owner, result).ConfigureAwait(false);
        using var imageClaim = staged.Claim;
        await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
        var timeout = admission.TracePolicySnapshot.Policy.TraceCommitTimeout;
        if (timeout > _audit!.CommitTimeout) timeout = _audit.CommitTimeout;
        var deadline = new StoreDeadline(timeout);
        if (!await _commandGate.WaitAsync(PositiveRemaining(deadline)).ConfigureAwait(false))
            throw new InvalidOperationException("ProductionInspectionCoreWriterBusy");
        try
        {
            var core = CreateProductionInspectionCore(owner, result, staged.Evidence);
            var committed = await WriteProductionWithFenceAsync(() => ((SqliteCommandStore)_audit).CommitProductionInspectionCoreAsync(
                new(core, () => ProductionCommitFailure(owner, admissionOnly: false), imageClaim), deadline,
                CancellationToken.None), deadline).ConfigureAwait(false);
            if (!committed.Committed || committed.Core is not { PlcPayload: { } payload } durable ||
                durable.Admission.ContentHash != admission.ContentHash || durable.ContentHash != core.ContentHash ||
                payload.ContentHash != result.Payload.ContentHash)
                throw new InvalidOperationException(committed.ReasonCode);
            // The record remains a fact even when the caller's publication deadline has elapsed.
            // Assign it before checking time so recovery retains that exact immutable Core.
            owner.Core = durable;
            ProjectCommittedProductionImage(durable);
            if (deadline.Expired) throw new TimeoutException("ProductionInspectionCoreCommitTimeout");
            await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
            return new(new(ExecutionKind.Production, admission.CorrelationId), durable.ContentHash, payload);
        }
        finally { _commandGate.Release(); }
    }

    private string? ProductionCommitFailure(ProductionInspectionOwner owner, bool admissionOnly)
    {
        // The writer must not block on the inverse Runtime -> SQLite lock order.
        if (!Monitor.TryEnter(_sync)) return "ProductionInspectionCommitFenceBusy";
        try
        {
            if (ProductionContinuationFailureLocked(owner) is { } failure) return failure;
            if (admissionOnly && (LocalStopPendingLocked ||
                    _snapshot.ArmState != ProductionArmState.Armed ||
                    owner.Current!.AdmissionGeneration != _admissionGeneration ||
                    !PartIdentityAdmissionStillCurrentLocked(owner, owner.PartIdentityAttempt)))
                return "ProductionInspectionAdmissionRevoked";
            return null;
        }
        finally { Monitor.Exit(_sync); }
    }

    private static async ValueTask<ProductionInspectionWriteResult> WriteProductionWithFenceAsync(
        Func<ValueTask<ProductionInspectionWriteResult>> write, StoreDeadline deadline)
    {
        while (true)
        {
            var result = await write().ConfigureAwait(false);
            if (result.Committed || result.ReasonCode != "ProductionInspectionCommitFenceBusy" || deadline.Expired)
                return result;
            // The writer has rolled this attempt back before returning. Yield the
            // SQLite connection so a runtime snapshot can retire; never retry a
            // committed transaction, a storage error, or an expired Core deadline.
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private async Task RecordProductionDeliveryAsync(ProductionInspectionOwner owner, InspectionCycleDeliveryFact fact)
    {
        await RequireProductionContinuationAsync(owner).ConfigureAwait(false);
        if (owner.Core is null) throw new InvalidOperationException("ProductionInspectionCoreReceiptRequired");
        var kind = fact switch
        {
            InspectionCycleDeliveryFact.PublicationPrepared => ProductionInspectionEventKind.PublicationPrepared,
            InspectionCycleDeliveryFact.ResultValidPublished => ProductionInspectionEventKind.ResultValidRaised,
            InspectionCycleDeliveryFact.ResultAckObserved => ProductionInspectionEventKind.ResultAcknowledged,
            InspectionCycleDeliveryFact.ResultValidCleared => ProductionInspectionEventKind.ResultValidCleared,
            InspectionCycleDeliveryFact.AckReset => ProductionInspectionEventKind.AcknowledgementReset,
            _ => throw new ArgumentOutOfRangeException(nameof(fact))
        };
        var deadline = new StoreDeadline(_audit!.CommitTimeout);
        var committed = await WriteProductionWithFenceAsync(() => ((SqliteCommandStore)_audit!).AppendProductionInspectionEventAsync(
            new(owner.Current!.InspectionId, kind, "ProductionInspection" + fact, DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp(), () => ProductionCommitFailure(owner, admissionOnly: false)),
            deadline, CancellationToken.None), deadline).ConfigureAwait(false);
        if (!committed.Committed) throw new InvalidOperationException(committed.ReasonCode);
        lock (_sync) ProjectProductionProgressLocked(owner);
    }

    private async Task RecordProductionFaultAsync(ProductionInspectionOwner owner, string reason)
    {
        if (owner.Current is null || !owner.AdmissionCommitted) return;
        var committed = await ((SqliteCommandStore)_audit!).AppendProductionInspectionEventAsync(
            new(owner.Current.InspectionId, ProductionInspectionEventKind.FaultTerminated, reason,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
            new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
        if (!committed.Committed)
            MarkAuditFault("ProductionInspectionFaultJournalUnavailable", alarmAuthorityUnavailable: true);
    }
}
