using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private void ScheduleManualInspectionSessionExitLocked(InteractiveSession session)
    {
        if (_manualOwner is not { } owner) return;
        if (session.State != InteractiveSessionState.Authenticated || session.SessionId != owner.ActorSessionId ||
            session.PrincipalId != owner.ActorPrincipalId.ToString("D"))
            RequestManualInspectionExitLocked(owner, "ManualInspectionInteractiveAuthorityEnded", abort: true);
    }

    private void RequestManualInspectionStop(string reason, bool abort)
    {
        lock (_sync)
        {
            if (_manualAdmissionPending)
            {
                _manualAdmissionStopRequested = true;
                _manualAdmissionAbortRequested |= abort;
            }
            if (_manualOwner is { } owner) RequestManualInspectionExitLocked(owner, reason, abort);
        }
    }

    private void RequestManualInspectionExit(ManualInspectionOwner owner, string reason, bool abort)
    { lock (_sync) RequestManualInspectionExitLocked(owner, reason, abort); }

    private void RequestManualInspectionExitLocked(ManualInspectionOwner owner, string reason, bool abort)
    {
        if (!ReferenceEquals(_manualOwner, owner) || owner.Retired.Task.IsCompleted) return;
        owner.ExitRequested = true;
        owner.ExitReason = reason;
        if (abort)
        {
            owner.Aborted = true;
            // A plugin's cancellation callback may block synchronously. It must
            // never execute under the station projection or command lock.
            _ = Task.Run(() =>
            {
                try { owner.AbortCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            });
        }
        PublishManualInspectionLocked(owner,
            owner.CurrentRunId.HasValue ? _manualSnapshot!.Phase : ManualInspectionSessionPhase.Restoring,
            owner.CurrentRunId.HasValue && !abort ? "ManualInspectionDraining" : reason);
        if (owner.ExitWorker is not null) return;
        owner.ExitWorker = Task.Run(async () =>
        {
            try
            {
                // An accepted Run may still be publishing its owner after the
                // writer commit. Wait for that admission before choosing what to drain.
                await _commandGate.WaitAsync().ConfigureAwait(false);
                Task? preceding;
                try { lock (_sync) preceding = owner.Operation; }
                finally { _commandGate.Release(); }
                if (preceding is not null) await preceding.ConfigureAwait(false);
                await RestoreManualInspectionAsync(owner).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                await FinishManualInspectionAsync(owner, false, "ManualInspectionExitUnavailable",
                    ManualInspectionRestorationState.RecoveryBlocked).ConfigureAwait(false);
            }
        });
    }

    private async Task<bool> CheckManualInspectionAuthorityAsync(ManualInspectionOwner owner)
    {
        lock (_sync) if (owner.Aborted) return false;
        var access = await _authorization!.GetManualInspectionOwnerAccessAsync(owner.ActorPrincipalId,
            owner.ActorSessionId, owner.ActorAuthorizationRevision, owner.AuthorizationPolicy,
            owner.AbortCancellation.Token).ConfigureAwait(false);
        lock (_sync) if (access.CanRun && !owner.Aborted) return true;
        RequestManualInspectionExit(owner, access.ReasonCode, abort: true);
        return false;
    }

    private async Task ShutdownManualInspectionAsync()
    {
        if (_manualOptions is null && !_manualStartupPending) return;
        var timeout = _manualOptions?.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
        if (!await _commandGate.WaitAsync(timeout).ConfigureAwait(false))
            throw new InvalidOperationException("ManualInspectionShutdownAdmissionIncomplete");
        Task[] pending;
        try
        {
            RequestManualInspectionStop("ManualInspectionRuntimeShutdown", abort: true);
            lock (_sync)
                pending = new[] { _manualStartupTask, _manualOwner?.Retired.Task }
                    .Where(task => task is not null).Select(task => task!).ToArray();
        }
        finally { _commandGate.Release(); }
        try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Keep the camera/algorithm owner alive. The caller's shutdown budget
            // is not evidence that ignored cancellation physically completed.
            lock (_sync)
            {
                _manualRecoveryBlocked = true;
                if (_manualOwner is { } owner)
                {
                    owner.Restoration = ManualInspectionRestorationState.RecoveryBlocked;
                    PublishManualInspectionLocked(owner, ManualInspectionSessionPhase.RecoveryBlocked,
                        "ManualInspectionShutdownIncomplete");
                }
            }
            throw new InvalidOperationException("ManualInspectionShutdownIncomplete");
        }
    }
}
