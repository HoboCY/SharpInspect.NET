using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async ValueTask<RuntimeCommandOutcome> HandleCameraRecoveryCommandAsync(
        StartCameraRecoveryCycleCommand command, Guid epoch, Guid attemptId, string? forcedRejection,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var service = _cameraRecoveryService;
        var reason = forcedRejection;
        if (reason is null && service is null) reason = "CameraRecoveryUnavailable";
        if (reason is null && (command.ExpectedCycleId == Guid.Empty ||
            !ValidCameraRecoveryIdentifier(command.LogicalRole, 64) ||
            !ValidCameraRecoveryIdentifier(command.ReasonCode, 128)))
            reason = "CameraRecoveryCommandInvalid";
        if (reason is null && command.LogicalRole != service!.GetSnapshot().LogicalCameraRole)
            reason = "CameraRecoveryRoleMismatch";
        var reservation = reason is null
            ? service!.TryReserveRestart(command.ExpectedCycleId, out reason)
            : null;
        // A successful reservation returns a diagnostic success code. Only a
        // failed reservation supplies a rejection to the authorization writer.
        if (reservation is not null) reason = null;
        using (reservation)
        {
            var authorization = await _authorization!.HandleCameraRecoveryCycleStartAsync(command,
                epoch, attemptId, reason, deadline, cancellationToken).ConfigureAwait(false);
            if (authorization.Outcome.Disposition != CommandDisposition.Accepted ||
                authorization.Outcome.Audit != AuditPersistence.Persisted || authorization.Admission is null)
                return authorization.Outcome;

            // Reservation has no device side effect. Its commit is deliberately
            // after the immutable authorization transaction and grant consumption.
            var started = false;
            var startReason = "CameraRecoveryReservationUnavailable";
            StoreWriteResult terminal;
            try
            {
                lock (_sync)
                {
                    if (_shutdownRequested || _disposed) startReason = "RuntimeStopped";
                    else if (reservation is not null)
                        started = reservation.Commit(out startReason);
                    if (!_disposed && !_shutdownRequested)
                        PublishLocked(_snapshot with { LastCommand = new CommandProgress(command.CorrelationId,
                            OperationState.Pending, "CameraRecoveryCycleStartAdmitted") });
                }
                var newCycleId = started ? service!.GetSnapshot().CycleId : null;
                terminal = await _authorization.CompleteCameraRecoveryCycleStartAsync(
                    authorization.Admission, newCycleId, started, startReason, deadline,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Admission has committed: an unexpected continuation failure
                // leaves it pending for governed recovery, never a fake terminal.
                terminal = new StoreWriteResult(false, "CameraRecoveryStartAuditUnavailable");
            }
            if (!terminal.Committed)
            {
                MarkAuditFault("CameraRecoveryStartAuditUnavailable", alarmAuthorityUnavailable: true);
                // A missing start terminal must never leave an unaccounted camera
                // owner available for further acquisition or retries.
                if (service is not null)
                {
                    var stopping = service.DisposeAsync().AsTask();
                    _ = stopping.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                return authorization.Outcome with { ReasonCode = "CameraRecoveryStartAuditUnavailable",
                    Audit = AuditPersistence.Unavailable };
            }
            lock (_sync)
                if (!_disposed && !_shutdownRequested)
                    PublishLocked(_snapshot with { LastCommand = new CommandProgress(command.CorrelationId,
                        started ? OperationState.Completed : OperationState.Failed, startReason) });
            return authorization.Outcome with { ReasonCode = startReason };
        }
    }

    private static bool ValidCameraRecoveryIdentifier(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
}
