using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private void ScheduleStationQualificationSessionExitLocked(InteractiveSession session)
    {
        if (_stationQualificationOwner is not { RestartRecovery: false } owner) return;
        if (session.State != InteractiveSessionState.Authenticated || session.SessionId != owner.Header.ActorSessionId ||
            session.PrincipalId != owner.Header.ActorPrincipalId.ToString("D"))
            RequestStationQualificationExitLocked(owner, "StationQualificationInteractiveAuthorityEnded", abort: true);
    }

    private void RequestStationQualificationStop(string reason, bool abort)
    {
        lock (_sync)
        {
            if (_stationQualificationAdmissionPending) _stationQualificationAdmissionStopRequested = true;
            if (_stationQualificationOwner is { } owner) RequestStationQualificationExitLocked(owner, reason, abort);
        }
    }

    private void RequestStationQualificationExitLocked(StationQualificationOwner owner, string reason, bool abort)
    {
        if (!ReferenceEquals(_stationQualificationOwner, owner) || owner.Retired.Task.IsCompleted) return;
        owner.ExitRequested = true;
        owner.Aborted |= abort;
        owner.CycleObserver?.RevokeAdmission();
        if (owner.CurrentRunId is null) owner.CycleObserver?.StopObserving();
        owner.ExitReason = reason;
        CancelStationQualification(owner, abort);
        PublishStationQualificationLocked(owner, _stationQualificationSnapshot?.Phase ?? StationQualificationSessionPhase.Restoring, reason);
        // The one operation worker owns restoration in its finally block. No
        // second worker can race a still-running capture, algorithm or output.
    }

    private static void CancelStationQualification(StationQualificationOwner owner, bool abort)
    {
        CancelOutsideLocks(owner.StimulusCancellation);
        if (abort) CancelOutsideLocks(owner.Cancellation);

        static void CancelOutsideLocks(CancellationTokenSource source) => _ = Task.Run(() =>
        {
            try { source.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        });
    }

    private async Task RequireStationQualificationAuthorityAsync(StationQualificationOwner owner)
    {
        var header = owner.Header;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(owner.Cancellation.Token);
        budget.CancelAfter(_stationQualificationOptions!.OperationTimeout);
        try
        {
            while (true)
            {
                RequireCurrentOwner();
                var access = await _authorization!.GetStationQualificationOwnerAccessAsync(header.ActorPrincipalId,
                    header.ActorSessionId, header.ActorAuthorizationRevision, header.AuthorizationPolicy,
                    budget.Token).ConfigureAwait(false);
                RequireCurrentOwner();
                budget.Token.ThrowIfCancellationRequested();
                if (access.CanRun) return;
                if (access.ReasonCode != "AuthorizationLeaseBusy")
                {
                    lock (_sync) RequestStationQualificationExitLocked(owner, access.ReasonCode, abort: true);
                    throw new OperationCanceledException("StationQualificationAuthorityEnded");
                }
                // A synchronous authorization COMMIT may temporarily own the
                // session monitor. No next physical phase starts while waiting.
                await Task.Delay(25, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested &&
            !owner.Cancellation.IsCancellationRequested)
        {
            lock (_sync)
                if (!owner.Aborted)
                    RequestStationQualificationExitLocked(owner, "StationQualificationAuthorizationWaitTimeout", abort: true);
            throw;
        }

        void RequireCurrentOwner()
        {
            owner.Cancellation.Token.ThrowIfCancellationRequested();
            lock (_sync)
                if (owner.Aborted || (owner.ExitRequested && owner.CurrentRunId is null) ||
                    _shutdownRequested || _disposed || !ReferenceEquals(_stationQualificationOwner, owner))
                    throw new OperationCanceledException("StationQualificationAuthorityEnded");
        }
    }

    private async Task ShutdownStationQualificationAsync()
    {
        if (_stationQualificationOptions is null && !_stationQualificationStartupPending) return;
        var timeout = _stationQualificationOptions?.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
        RequestStationQualificationStop("StationQualificationRuntimeShutdown", abort: true);
        if (!await _commandGate.WaitAsync(timeout).ConfigureAwait(false))
        {
            lock (_sync)
            {
                _stationQualificationRecoveryBlocked = true;
                if (_stationQualificationOwner is { } blockedOwner)
                {
                    blockedOwner.Restoration = StationQualificationRestorationState.RecoveryBlocked;
                    PublishStationQualificationLocked(blockedOwner, StationQualificationSessionPhase.RecoveryBlocked,
                        "StationQualificationShutdownAdmissionIncomplete");
                }
            }
            throw new InvalidOperationException("StationQualificationShutdownAdmissionIncomplete");
        }
        Task[] pending;
        try
        {
            RequestStationQualificationStop("StationQualificationRuntimeShutdown", abort: true);
            lock (_sync)
                pending = new[] { _stationQualificationStartupTask, _stationQualificationOwner?.Retired.Task }
                    .Where(task => task is not null).Select(task => task!).ToArray();
        }
        finally { _commandGate.Release(); }
        try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                _stationQualificationRecoveryBlocked = true;
                if (_stationQualificationOwner is { } owner)
                {
                    owner.Restoration = StationQualificationRestorationState.RecoveryBlocked;
                    PublishStationQualificationLocked(owner, StationQualificationSessionPhase.RecoveryBlocked,
                        "StationQualificationShutdownIncomplete");
                }
            }
            throw new InvalidOperationException("StationQualificationShutdownIncomplete");
        }
    }
}
