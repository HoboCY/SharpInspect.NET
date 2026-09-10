using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task<bool> RecordManualInspectionProgressAsync(ManualInspectionOwner owner,
        ManualInspectionSessionPhase phase, string reason)
    {
        if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
        { RequestManualInspectionExit(owner, "ManualInspectionProgressRuntimeBusy", abort: true); return false; }
        try
        {
            ManualInspectionCommand command;
            CommandAuditFact fact;
            ManualInspectionRunRecord? run = null;
            lock (_sync)
            {
                if (!ReferenceEquals(_manualOwner, owner) || owner.Aborted || owner.AbortCancellation.IsCancellationRequested) return false;
                command = owner.PendingCommand ?? StartManualInspectionContinuation(owner);
                fact = owner.PendingFact ?? owner.StartFact;
                if (command is RunManualInspectionCommand && owner.CurrentRun is { } admitted)
                    run = new(admitted.Position, admitted.RunId, admitted.SessionId, admitted.RuntimeEpoch,
                        admitted.CommandCorrelationId, admitted.AttemptId, admitted.PartIdentity,
                        ManualInspectionRunStatus.Executing, InspectionDecision.Unknown, null, reason,
                        admitted.AdmittedAtUtc, MaxManualTime(DateTimeOffset.UtcNow, admitted.AdmittedAtUtc),
                        null, null, null, null, null,
                        partIdentitySource: admitted.PartIdentitySource,
                        partIdentityActorPrincipalId: admitted.PartIdentityActorPrincipalId,
                        partIdentityActorSessionId: admitted.PartIdentityActorSessionId);
            }
            var completion = new ManualInspectionCompletion(true, reason, phase, owner.Restoration, run);
            var result = await _authorization!.CompleteManualInspectionCommandAsync(command, owner.Header, fact,
                completion, new StoreDeadline(_audit.CommitTimeout), () =>
                { lock (_sync) return ReferenceEquals(_manualOwner, owner) && !owner.Aborted; }).ConfigureAwait(false);
            if (result.Outcome.Audit != AuditPersistence.Persisted || result.Header is null)
            {
                lock (_sync)
                {
                    if (owner.Aborted || result.Outcome.ReasonCode == "ManualInspectionProgressAuthorityEnded")
                    {
                        RequestManualInspectionExitLocked(owner, "ManualInspectionProgressAuthorityEnded", abort: true);
                        return false;
                    }
                }
                MarkAuditFault("ManualInspectionProgressAuditUnavailable");
                RequestManualInspectionExit(owner, "ManualInspectionProgressAuditUnavailable", abort: true);
                return false;
            }
            lock (_sync)
            {
                owner.Header = result.Header;
                if (result.Run is not null) owner.CurrentRun = result.Run;
                if (!ReferenceEquals(_manualOwner, owner) || owner.Aborted)
                {
                    RequestManualInspectionExitLocked(owner, owner.ExitReason, abort: true);
                    return false;
                }
                if (phase == ManualInspectionSessionPhase.ReadyForRun)
                { owner.PendingCommand = null; owner.PendingFact = null; }
                PublishManualInspectionLocked(owner, phase, reason);
            }
            return true;
        }
        finally { _commandGate.Release(); }
    }

    private async Task<bool> RecordManualInspectionRunTerminalAsync(ManualInspectionOwner owner,
        AlgorithmExecutionOutcome? outcome, ExecutionStatus status, string reason,
        FrameMetadata? metadata, FrameProvenance? provenance, DateTimeOffset? startedAtUtc, long droppedDiagnostics)
    {
        if (!await _commandGate.WaitAsync(_audit!.CommitTimeout).ConfigureAwait(false))
        { MarkAuditFault("ManualInspectionRunTerminalRuntimeBusy"); return false; }
        try
        {
            if (owner.CurrentRun is not { } admitted || owner.PendingCommand is not { } command ||
                owner.PendingFact is not { } fact) throw new InvalidOperationException("ManualInspectionRunAdmissionMissing");
            var completedAt = MaxManualTime(DateTimeOffset.UtcNow, admitted.AdmittedAtUtc, startedAtUtc ?? admitted.AdmittedAtUtc);
            var run = ManualInspectionRunEvidence.CreateTerminal(admitted, owner.Header, outcome, status, reason,
                metadata, provenance, owner.Prepared?.InstanceId, owner.Plan.Content,
                startedAtUtc is { } started ? MaxManualTime(started, admitted.AdmittedAtUtc) : null, completedAt, droppedDiagnostics);
            var phase = status == ExecutionStatus.Success && !owner.ExitRequested
                ? ManualInspectionSessionPhase.ReadyForRun : ManualInspectionSessionPhase.Restoring;
            var result = await _authorization!.CompleteManualInspectionCommandAsync(command, owner.Header, fact,
                new(status == ExecutionStatus.Success, reason, phase, owner.Restoration, run,
                    CompleteCommand: true), new StoreDeadline(_audit.CommitTimeout), terminalDecision: proposed =>
                {
                    // Decide at the serialized writer boundary. A prior abort fixes
                    // cancellation; graceful draining preserves the actual outcome.
                    bool aborted;
                    bool exiting;
                    lock (_sync) { aborted = owner.Aborted; exiting = owner.ExitRequested; }
                    if (!aborted || proposed.Run?.ExecutionStatus != ExecutionStatus.Success)
                        return exiting ? proposed with { Phase = ManualInspectionSessionPhase.Restoring } : proposed;
                    const string abortedReason = "ManualInspectionAbortedBeforeTerminal";
                    var cancelled = ManualInspectionRunEvidence.CreateTerminal(admitted, owner.Header, null,
                        ExecutionStatus.Cancelled, abortedReason, metadata, provenance, owner.Prepared?.InstanceId,
                        owner.Plan.Content, startedAtUtc is { } started ? MaxManualTime(started, admitted.AdmittedAtUtc) : null,
                        completedAt, droppedDiagnostics);
                    return proposed with { Succeeded = false, ReasonCode = abortedReason,
                        Phase = ManualInspectionSessionPhase.Restoring, Run = cancelled };
                }).ConfigureAwait(false);
            if (result.Outcome.Audit != AuditPersistence.Persisted || result.Header is null || result.Run is null)
                throw new InvalidOperationException("ManualInspectionRunTerminalAuditUnavailable");
            lock (_sync)
            {
                owner.Header = result.Header;
                owner.LastRunId = admitted.RunId;
                owner.PendingCommand = null;
                owner.PendingFact = null;
                // A fixed timeout may precede physical algorithm completion. Keep
                // the execution identity busy until the owner has actually retired.
                if (owner.Execution is null or { ActiveExecutionCount: 0 })
                { owner.CurrentRunId = null; owner.CurrentRun = null; }
                else owner.CurrentRun = result.Run;
                PublishManualInspectionLocked(owner, result.Header.Phase, result.Run.ReasonCode);
            }
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync) _manualRecoveryBlocked = true;
            MarkAuditFault("ManualInspectionRunTerminalAuditUnavailable");
            return false;
        }
        finally { _commandGate.Release(); }
    }

    private static DateTimeOffset MaxManualTime(params DateTimeOffset[] values) => values.Max().ToUniversalTime();

    private static ManualInspectionContinuationCommand StartManualInspectionContinuation(ManualInspectionOwner owner) =>
        new(owner.StartFact.CorrelationId, new(owner.StartFact.Source ?? CommandSource.PhysicalConsole,
            owner.StartFact.ClaimedPrincipalId, owner.StartFact.ClaimedSessionId, owner.StartFact.ClaimedStepUpGrantId),
            owner.SessionId, AuditedCommandKind.StartManualInspectionSession, owner.Header.AuthorizationTarget,
            owner.Header.ChangeReason);
}
