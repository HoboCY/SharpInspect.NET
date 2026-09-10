using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Cycles;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task RecordStationQualificationProgressAsync(StationQualificationOwner owner,
        StationQualificationSessionPhase phase, string reason, QualificationFacilityObservation? observation = null,
        StationQualificationRunRecord? run = null, bool terminal = false,
        QualificationCycleWriteRequest? cycle = null)
    {
        if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
            throw cycle is null ? new InvalidOperationException("StationQualificationProgressRuntimeBusy") :
                new InspectionCyclePersistenceException("QualificationCycleProgressPersistenceBusy");
        try
        {
            await AppendStationQualificationProgressLockedAsync(owner, phase, reason, observation, run, terminal, cycle: cycle).ConfigureAwait(false);
        }
        finally { _commandGate.Release(); }
    }

    private async Task<StationQualificationTransactionResult> AppendStationQualificationProgressLockedAsync(StationQualificationOwner owner,
        StationQualificationSessionPhase phase, string reason, QualificationFacilityObservation? observation = null,
        StationQualificationRunRecord? run = null, bool terminal = false, StoreDeadline? deadline = null,
        QualificationCycleWriteRequest? cycle = null)
    {
        bool AllowCycleDrain() => cycle is not null && !owner.Aborted &&
            (cycle.Kind == QualificationCycleEventKind.ProtocolRequestRejected || owner.CycleExecuting &&
                owner.CurrentRunId?.Value == cycle.RunId?.Value && cycle.Kind != QualificationCycleEventKind.Admitted);
        lock (_sync)
        {
            if (!ReferenceEquals(_stationQualificationOwner, owner))
                throw new OperationCanceledException("StationQualificationOwnerChanged");
            if (!terminal && phase is not (StationQualificationSessionPhase.Restoring or StationQualificationSessionPhase.RecoveryBlocked) && run is not { Terminal: true } &&
                (owner.ExitRequested || owner.Aborted) && !AllowCycleDrain())
                throw new OperationCanceledException("StationQualificationProgressRevoked");
            // This serialized progress admission precedes a later local Stop.
            // Stop can revoke physical work while the writer finishes recording
            // that admission; the post-commit check suppresses any stale phase.
        }
        var fact = terminal && owner.ExitFact is not null ? owner.ExitFact : owner.StartFact;
        var request = new StationQualificationProgressRequest(owner.Header, owner.LastEvent.Position, owner.LastEvent.ContentHash,
            fact.CorrelationId, fact.AttemptId, fact.CommandKind, phase, owner.Restoration, reason, terminal,
            observation, run, fact, CompleteOriginalStart: terminal, CompleteCommand: terminal && owner.ExitFact is not null,
            FinalizeRun: run is { Terminal: true } ? proposed => FinalizeStationQualificationRun(owner, proposed) : null,
            RecoveryAttempt: owner.RecoveryAttempt,
            CommandAuthorizationTarget: fact.CommandKind == AuditedCommandKind.StartStationQualificationSession
                ? owner.Header.AuthorizationTarget : owner.ExitAuthorizationTarget,
            AuthorizeProgress: _authorization!.AuthorizeStationQualificationProgress, Cycle: cycle);
        var result = await ((SqliteCommandStore)_audit!).AppendStationQualificationProgressAsync(request,
            deadline ?? new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
        if (cycle is not null && result.Outcome.Audit != AuditPersistence.Persisted)
            throw new InspectionCyclePersistenceException(result.Outcome.ReasonCode);
        if (result.Outcome.Audit != AuditPersistence.Persisted || result.Event is null || !result.Accepted)
            throw new InvalidOperationException(result.Outcome.ReasonCode);
        lock (_sync)
        {
            owner.LastEvent = result.Event;
            if (cycle is not null)
            {
                owner.LastCycleEvent = result.CycleEvent ?? throw new InvalidOperationException("QualificationCycleCommitReceiptMissing");
                if (cycle.Kind == QualificationCycleEventKind.Admitted) owner.ModbusRecoveryRequired = true;
                if (cycle.Kind == QualificationCycleEventKind.AckReset) owner.ModbusRecoveryRequired = false;
            }
            if (run is { Terminal: false })
            {
                owner.CurrentRun = result.Event.Run ?? run;
                owner.CurrentRunId = run.RunId;
            }
            if (!ReferenceEquals(_stationQualificationOwner, owner) ||
                (!terminal && phase is not (StationQualificationSessionPhase.Restoring or StationQualificationSessionPhase.RecoveryBlocked) && run is not { Terminal: true } &&
                    (owner.ExitRequested || owner.Aborted) && !AllowCycleDrain()))
                throw new OperationCanceledException("StationQualificationProgressRevokedAfterCommit");
            PublishStationQualificationLocked(owner, phase, result.Event.Run?.ReasonCode ?? reason);
        }
        return result;
    }

    private async Task AdmitStationQualificationRunAsync(StationQualificationOwner owner, QualificationFacilityStimulus stimulus,
        QualificationFacilityObservation observation)
    {
        if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
            throw new InvalidOperationException("StationQualificationRunAdmissionBusy");
        try
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_stationQualificationOwner, owner) || owner.ExitRequested || owner.Aborted ||
                    owner.CurrentRunId is not null || _shutdownRequested)
                    throw new OperationCanceledException("StationQualificationRunAdmissionEnded");
            }
            var now = DateTimeOffset.UtcNow;
            if (now < owner.LastEvent.RecordedAtUtc) now = owner.LastEvent.RecordedAtUtc;
            var run = new StationQualificationRunRecord(owner.LastEvent.Position + 1,
                new QualificationRunId(Guid.NewGuid()), owner.Header.SessionId, stimulus.Sequence,
                stimulus.ScenarioId, stimulus.QualificationContextHash, stimulus.ControllerEpoch, stimulus.CycleSequence,
                now, null, null, InspectionDecision.Unknown, "StationQualificationRunAdmitted",
                null, null, null, null, null, null);
            await AppendStationQualificationProgressLockedAsync(owner, StationQualificationSessionPhase.Running,
                "StationQualificationRunAdmitted", observation: observation, run: run,
                cycle: owner.CycleStoragePolicy is null ? null : new(QualificationCycleEventKind.Admitted,
                    run.RunId, owner.Header.Plan.ProfileHash, owner.CycleStoragePolicy)).ConfigureAwait(false);
            lock (_sync)
            {
                owner.CurrentRun = run;
                owner.CurrentRunId = run.RunId;
                owner.StimulusSequence = stimulus.Sequence;
                if (owner.ExitRequested || owner.Aborted || _shutdownRequested)
                    throw new OperationCanceledException("StationQualificationRunRevokedAfterAdmission");
                PublishStationQualificationLocked(owner, StationQualificationSessionPhase.Running, "StationQualificationRunning");
            }
        }
        finally { _commandGate.Release(); }
    }

    private async Task<(StationQualificationRunRecord Run, QualificationCycleEvent? Cycle)> CompleteStationQualificationRunAsync(StationQualificationOwner owner, AlgorithmExecutionOutcome? outcome,
        ExecutionStatus status, string reason, FrameMetadata? metadata, FrameProvenance? provenance,
        StationQualificationPayload? payload)
    {
        var timeout = owner.CycleStoragePolicy?.Policy.TraceCommitTimeout ?? _audit!.CommitTimeout;
        if (timeout > _audit!.CommitTimeout) timeout = _audit.CommitTimeout;
        var deadline = new StoreDeadline(timeout);
        if (!await _commandGate.WaitAsync(PositiveRemaining(deadline)).ConfigureAwait(false))
            throw new InvalidOperationException("StationQualificationRunTerminalBusy");
        try
        {
            var admitted = owner.CurrentRun ?? throw new InvalidOperationException("StationQualificationRunAdmissionMissing");
            lock (_sync)
                if (owner.Aborted)
                { status = ExecutionStatus.Cancelled; reason = QualificationAbortReason(owner); payload = null; }
            string? json = null;
            string? hash = null;
            if (status == ExecutionStatus.Success)
            {
                if (outcome is null || payload is null ||
                    !AlgorithmResultStorageCodec.TryEncode(admitted.RunId.Value, outcome, out var document, out var encodeReason) || document is null)
                    throw new InvalidOperationException("StationQualificationRunResultUnavailable");
                json = document.PayloadJson;
                hash = document.PayloadHash;
            }
            var completedAt = DateTimeOffset.UtcNow;
            if (completedAt < admitted.AdmittedAtUtc) completedAt = admitted.AdmittedAtUtc;
            var run = new StationQualificationRunRecord(admitted.Position, admitted.RunId, admitted.SessionId,
                admitted.StimulusSequence, admitted.ScenarioId, admitted.ContextHash, admitted.ControllerEpoch,
                admitted.CycleSequence, admitted.AdmittedAtUtc, completedAt, status,
                status == ExecutionStatus.Success ? outcome!.Decision : InspectionDecision.Unknown, reason,
                metadata, provenance, json, hash, payload, outcome?.Timing);
            var terminalTransaction = await AppendStationQualificationProgressLockedAsync(owner, owner.RecoveryAttempt is not null ?
                StationQualificationSessionPhase.Restoring : StationQualificationSessionPhase.Running,
                reason, run: run, deadline: deadline,
                cycle: owner.CycleStoragePolicy is null ? null : new(QualificationCycleEventKind.CoreCommitted,
                    run.RunId, owner.Header.Plan.ProfileHash)).ConfigureAwait(false);
            lock (_sync)
            {
                owner.CurrentRun = terminalTransaction.Event?.Run ??
                    throw new InvalidOperationException("StationQualificationRunTerminalMissing");
                owner.LastRunId = owner.CurrentRun.RunId;
                var committed = owner.CurrentRun;
                if (owner.CycleStoragePolicy is not null && deadline.Expired)
                    throw new TimeoutException("QualificationTraceCommitTimeout");
                if (!owner.CycleExecuting && owner.Execution is (null or { ActiveExecutionCount: 0 }))
                { owner.CurrentRun = null; owner.CurrentRunId = null; }
                return (committed, terminalTransaction.CycleEvent);
            }
        }
        finally { _commandGate.Release(); }
    }

    private StationQualificationRunRecord FinalizeStationQualificationRun(StationQualificationOwner owner,
        StationQualificationRunRecord proposed)
    {
        if (proposed.QualificationPayload is null) return proposed;
        // The serialized writer calls this after its fresh cursor check. Abort
        // fixes the terminal result here; graceful draining preserves success.
        bool aborted;
        lock (_sync)
            aborted = owner.Aborted || !ReferenceEquals(_stationQualificationOwner, owner);
        if (!aborted) return proposed;
        return new(proposed.Position, proposed.RunId, proposed.SessionId, proposed.StimulusSequence,
            proposed.ScenarioId, proposed.ContextHash, proposed.ControllerEpoch, proposed.CycleSequence,
            proposed.AdmittedAtUtc, proposed.CompletedAtUtc, ExecutionStatus.Cancelled,
            InspectionDecision.Unknown, QualificationAbortReason(owner),
            proposed.FrameMetadata, proposed.FrameProvenance, null, null, null, proposed.Timing);
    }

    private static string QualificationAbortReason(StationQualificationOwner owner) => owner.ExitReason is
        "QualificationControllerEpochChanged" or "PlcControllerHeartbeatStale" or "PlcRuntimeHeartbeatUnobserved" or
        "PlcCommunicationTransportLost" ? owner.ExitReason : "StationQualificationAbortedBeforeTerminal";

    private async Task FinishStationQualificationAsync(StationQualificationOwner owner, bool restored, string reason)
    {
        var persisted = false;
        try
        {
            lock (_sync)
            {
                restored &= !_stationQualificationRecoveryBlocked;
                owner.Restoration = restored ? StationQualificationRestorationState.Restored : StationQualificationRestorationState.RecoveryBlocked;
            }
            await RecordStationQualificationProgressAsync(owner, restored ? StationQualificationSessionPhase.Closed :
                StationQualificationSessionPhase.RecoveryBlocked, reason, terminal: restored).ConfigureAwait(false);
            persisted = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("StationQualificationTerminalAuditUnavailable"); }
        finally
        {
            lock (_sync)
            {
                var blocked = !restored || !persisted;
                _stationQualificationRecoveryBlocked |= blocked;
                owner.Restoration = blocked ? StationQualificationRestorationState.RecoveryBlocked : StationQualificationRestorationState.Restored;
                PublishStationQualificationLocked(owner, blocked ? StationQualificationSessionPhase.RecoveryBlocked :
                    StationQualificationSessionPhase.Closed, persisted ? reason : "StationQualificationTerminalAuditUnavailable");
                if (!blocked && ReferenceEquals(_stationQualificationOwner, owner))
                {
                    _stationQualificationOwner = null;
                    ClearStationQualificationProjectionLocked();
                }
            }
            // A failed readback can leave the camera or isolation lease owned.
            // RecoveryBlocked is durable failure evidence, not resource retirement.
            if (owner.ResourcesRetired && persisted) owner.Retired.TrySetResult(true);
        }
    }
}
