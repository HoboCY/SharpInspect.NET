using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime : IDiagnosticSupportQuery, ISupportBundleReader
{
    private ProductionStoreOptions? _diagnosticSupportOptions;
    private SupportOperation? _diagnosticSupportOperation;
    private DiagnosticCaptureSnapshot? _captureStatus;
    private SupportBundleSnapshot? _bundleStatus;
    private PublishedBundle? _publishedBundle;
    private TaskCompletionSource<bool>? _diagnosticDeliveryRetired;
    internal Action? AfterDiagnosticStopAuthorizationForTesting { get; set; }
    internal Action? BeforeSupportBundleDeliveryForTesting { get; set; }
    private bool DiagnosticSupportBusyLocked => _diagnosticSupportOperation is not null || _diagnosticDeliveryRetired is not null;
    private sealed record PublishedBundle(Guid Id, byte[] Bytes, string Hash, DateTimeOffset Expires, DiagnosticSupportAuthority Authority);
    private sealed class SupportOperation
    {
        internal SupportOperation(DiagnosticSupportCommand command, CancellationToken token)
        {
            Command = command; Token = token; Created = DateTimeOffset.UtcNow; Started = Stopwatch.GetTimestamp();
            Registration = token.Register(static value => ((SupportOperation)value!).RequestStop(), this);
        }
        internal CancellationTokenRegistration Registration { get; }
        internal TaskCompletionSource<bool> Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal DiagnosticSupportCommand Command { get; }
        internal CancellationToken Token { get; }
        internal DateTimeOffset Created { get; }
        internal long Started { get; }
        internal DiagnosticSupportAuthority? Authority;
        internal DiagnosticCaptureLease? Capture;
        internal Task? Physical;
        internal int StopRequested;
        internal bool StopAuthorizationPending;
        internal DiagnosticSupportAuthorization? StopAuthorization;
        internal DiagnosticSupportReason? StopReason;
        internal void RequestStop()
        {
            Interlocked.Exchange(ref StopRequested, 1);
            Volatile.Read(ref Authority)?.Revoke(); Volatile.Read(ref Capture)?.Revoke(DiagnosticCaptureEnd.Stopped);
        }
    }

    public DiagnosticCaptureSnapshot ReadCapture()
    {
        lock (_sync)
        {
            var result = _captureStatus ?? new(_diagnosticSupportOptions?.DiagnosticSupport is not null,
                _snapshot.RuntimeEpoch, null, DiagnosticCapturePhase.Baseline, false, "DiagnosticBaseline", null, 0, null, null, null);
            return _diagnosticSupportOperation?.Capture is { } capture ? result with {
                Elevated = capture.End == DiagnosticCaptureEnd.None, ObservedEvents = capture.Attempts } : result;
        }
    }
    public SupportBundleSnapshot ReadBundle()
    {
        lock (_sync) return _bundleStatus ?? new(_diagnosticSupportOptions?.DiagnosticSupport is not null,
            _snapshot.RuntimeEpoch, null, SupportBundlePhase.Idle, "SupportBundleIdle", null, null, null);
    }
    private StationStateSnapshot ProjectDiagnosticSupportLocked(StationStateSnapshot next)
    {
        if (_diagnosticSupportOptions?.DiagnosticSupport is null) return next;
        var blockers = next.AdmissionBlockers.Where(value => value != "DiagnosticSupportInProgress");
        if (DiagnosticSupportBusyLocked) blockers = blockers.Append("DiagnosticSupportInProgress");
        return next with { DiagnosticCapture = ReadCapture(), SupportBundle = ReadBundle(),
            AdmissionBlockers = new(blockers), Ready = DiagnosticSupportBusyLocked ? false : next.Ready,
            ArmState = DiagnosticSupportBusyLocked ? ProductionArmState.Disarmed : next.ArmState };
    }
    private string? DiagnosticSupportStartRejectionLocked()
    {
        if (_disposed || _shutdownRequested || _lifetime.IsCancellationRequested) return "RuntimeStopped";
        // A freshly persisted Step-Up may leave the background projection Verifying.
        // Admission still verifies the full current chain inside its identity/write transaction.
        if (!_storeReady || _auditFault || _audit?.Integrity?.State is not (AuditIntegrityState.Verified or AuditIntegrityState.Verifying))
            return "DiagnosticSupportStoreNotReady";
        if (_diagnosticSupportOptions?.DiagnosticSupport is not { } governance ||
            _diagnosticSupportOptions.LoggingDiagnostics?.SupportBundles is not { } bundles ||
            governance.Policy.ContentHash != bundles.Policy.ContentHash || governance.SupportRootBindingHash != bundles.BindingHash ||
            _diagnostics?.Pipeline is null) return "DiagnosticSupportConfigurationRequired";
        if (DiagnosticSupportBusyLocked) return "DiagnosticSupportBusy";
        // No current qualification record can attest the exact elevated/export workload.
        if (_snapshot.Ready || _snapshot.ArmState == ProductionArmState.Armed || _snapshot.CurrentExecution is not null ||
            _automaticProductionArm is { Terminal: false } || _manualMaintenanceArm is { Terminal: false })
            return "DiagnosticSupportLoadNotQualified";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Busy || _activationReservation is not null || LocalStopPendingLocked)
            return "DiagnosticSupportRuntimeBusy";
        if (_diagnostics.ReadHealth().Unhealthy) return "DiagnosticPipelineUnhealthy";
        return null;
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitDiagnosticSupportAsync(DiagnosticSupportCommand command, CancellationToken token)
    {
        if (command is StopDiagnosticCaptureCommand stop) return await StopDiagnosticCaptureAsync(stop, token).ConfigureAwait(false);
        RuntimeCommandOutcome Rejected(string reason) => new(command.CorrelationId, CommandDisposition.Rejected, reason,
            AuditPersistence.NotAttempted, Guid.NewGuid());
        if (_authorization is null || _audit is not SqliteCommandStore store) return Rejected("DiagnosticSupportConfigurationRequired");
        SupportOperation operation; string? rejection;
        lock (_sync)
        {
            rejection = DiagnosticSupportStartRejectionLocked();
            if (rejection is not null) return Rejected(rejection);
            var logging = _diagnosticSupportOptions!.LoggingDiagnostics!.Policy;
            var policy = _diagnosticSupportOptions.DiagnosticSupport!.Policy;
            if (command is StartDiagnosticCaptureCommand capture && (capture.Profile.LoggingPolicyHash != logging.ContentHash ||
                capture.Profile.Duration > logging.MaximumCaptureDuration || capture.Profile.MaximumEvents > logging.MaximumCaptureEvents ||
                capture.Profile.Components.Any(component => !logging.Contracts.Any(value => value.Component == component))))
                return Rejected("DiagnosticCaptureProfileNotApproved");
            if (command is CreateSupportBundleCommand bundle && (bundle.SupportPolicyHash != policy.ContentHash ||
                bundle.Scope.ThroughUtc - bundle.Scope.FromUtc > policy.MaximumScope ||
                bundle.Scope.RuntimeEpoch != _snapshot.RuntimeEpoch)) return Rejected("SupportBundleScopeNotApproved");
            operation = new(command, token); _diagnosticSupportOperation = operation;
            if (command is StartDiagnosticCaptureCommand start)
                _captureStatus = new(true, _snapshot.RuntimeEpoch, start.OperationId, DiagnosticCapturePhase.Admitted, false,
                    "DiagnosticCaptureAuthorizing", start.Profile.ContentHash, 0, start.Profile.MaximumEvents,
                    operation.Created, operation.Created + start.Profile.Duration);
            else _bundleStatus = new(true, _snapshot.RuntimeEpoch, null, SupportBundlePhase.Admitted, "SupportBundleAuthorizing", null, null, null);
            PublishLocked(_snapshot);
        }
        try
        {
            var policy = _diagnosticSupportOptions!.DiagnosticSupport!.Policy;
            var capture = command as StartDiagnosticCaptureCommand;
            var bundle = command as CreateSupportBundleCommand;
            var mutation = new DiagnosticOperationMutation(command.OperationId,
                capture is not null ? DiagnosticOperationKind.Capture : DiagnosticOperationKind.Bundle, _snapshot.RuntimeEpoch,
                command.Reason, capture is not null ? "DiagnosticCaptureAdmitted" : "SupportBundleAdmitted", command.AuthorizationTarget,
                capture is null ? null : DiagnosticCaptureProfileFields.From(capture.Profile),
                bundle is null ? null : SupportBundleScopeFields.From(bundle.Scope),
                operation.Created + (capture?.Profile.Duration ?? policy.ExportTimeout), capture?.Profile.MaximumEvents);
            var admitted = await _authorization.AuthorizeDiagnosticSupportAsync(command, _snapshot.RuntimeEpoch, mutation, null,
                new StoreDeadline(store.CommitTimeout), token).ConfigureAwait(false);
            operation.Authority = admitted.Authority;
            if (admitted.Outcome.Disposition != CommandDisposition.Accepted)
            { FinishDiagnosticSupport(operation, admitted.Outcome.ReasonCode, success: false); return admitted.Outcome; }
            lock (_sync)
            {
                if (_disposed || _shutdownRequested || operation.StopRequested != 0 || token.IsCancellationRequested)
                    operation.Authority!.Revoke();
                if (capture is not null && operation.Authority!.Valid)
                {
                    operation.Capture = new(capture.OperationId, capture.Profile, operation.Authority, startedTimestamp: operation.Started);
                    if (_diagnostics!.Pipeline!.BeginCapture(operation.Capture))
                        _captureStatus = _captureStatus! with { Phase = DiagnosticCapturePhase.Active, Elevated = true,
                            ReasonCode = "DiagnosticCaptureActive" };
                    else operation.Capture.Revoke(DiagnosticCaptureEnd.Fault);
                }
                operation.Physical = Task.Run(() => capture is not null ? RunDiagnosticCaptureAsync(operation, store) :
                    RunSupportBundleAsync(operation, store));
                PublishLocked(_snapshot);
            }
            return admitted.Outcome;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { operation.Authority?.Revoke(); FinishDiagnosticSupport(operation, "DiagnosticSupportAdmissionFailed", false); return Rejected("DiagnosticSupportAdmissionFailed"); }
    }

    private async ValueTask<RuntimeCommandOutcome> StopDiagnosticCaptureAsync(StopDiagnosticCaptureCommand command, CancellationToken token)
    {
        SupportOperation? operation;
        lock (_sync)
        {
            operation = _diagnosticSupportOperation;
            if (operation?.Command is not StartDiagnosticCaptureCommand || operation.Command.OperationId != command.OperationId ||
                operation.StopAuthorizationPending || operation.Capture is null || operation.Capture.End != DiagnosticCaptureEnd.None)
                return new(command.CorrelationId, CommandDisposition.Rejected, "DiagnosticCaptureNotActive", AuditPersistence.NotAttempted, Guid.NewGuid());
            operation.StopAuthorizationPending = true;
        }
        try
        {
            var authorized = await _authorization!.AuthorizeDiagnosticSupportAsync(command, _snapshot.RuntimeEpoch, null, null,
                new StoreDeadline(_audit!.CommitTimeout), token).ConfigureAwait(false);
            AfterDiagnosticStopAuthorizationForTesting?.Invoke();
            lock (_sync)
            {
                if (authorized.Outcome.Disposition == CommandDisposition.Accepted)
                { operation.StopAuthorization = authorized; operation.StopReason = command.Reason;
                    operation.Capture!.Revoke(DiagnosticCaptureEnd.Stopped); }
            }
            return authorized.Outcome;
        }
        finally { lock (_sync) operation.StopAuthorizationPending = false; }
    }

    private async Task RunDiagnosticCaptureAsync(SupportOperation operation, SqliteCommandStore store)
    {
        var capture = operation.Capture; var success = false; var reason = "DiagnosticCaptureInterrupted";
        try
        {
            if (capture is not null)
            {
                while (capture.End == DiagnosticCaptureEnd.None)
                {
                    if (operation.Token.IsCancellationRequested || Volatile.Read(ref operation.StopRequested) != 0 || _shutdownRequested)
                        capture.Revoke(DiagnosticCaptureEnd.Stopped);
                    else if (_diagnostics!.ReadHealth().Unhealthy) capture.Revoke(DiagnosticCaptureEnd.Fault);
                    else if (!await _authorization!.CheckDiagnosticAuthorityAsync(operation.Authority!, Permission.StartDiagnosticCapture,
                        CancellationToken.None).ConfigureAwait(false)) capture.Revoke(DiagnosticCaptureEnd.AuthorityLost);
                    lock (_sync)
                    {
                        _captureStatus = _captureStatus! with { ObservedEvents = capture.Attempts,
                            Elevated = capture.End == DiagnosticCaptureEnd.None };
                    }
                    if (capture.End == DiagnosticCaptureEnd.None) await Task.Delay(_diagnosticSupportOptions!.DiagnosticSupport!.Policy.AuthorizationCheckInterval).ConfigureAwait(false);
                }
                reason = "DiagnosticCapture" + capture.End;
                lock (_sync) { _captureStatus = _captureStatus! with { Phase = DiagnosticCapturePhase.Draining, Elevated = false,
                    ObservedEvents = capture.Attempts, ReasonCode = reason }; PublishLocked(_snapshot); }
                while (!capture.Drained || operation.StopAuthorizationPending) await Task.Delay(10).ConfigureAwait(false);
                success = capture.End is DiagnosticCaptureEnd.TimeLimit or DiagnosticCaptureEnd.EventLimit or DiagnosticCaptureEnd.Stopped;
                var stop = operation.StopAuthorization;
                if (success)
                {
                    var sealedResult = await store.AppendDiagnosticOperationSealedAsync(new(operation.Command.OperationId, DateTimeOffset.UtcNow,
                        reason, null, null, null, null, stop?.CommandEventId, stop?.AuthorizationEventId)
                        { ObservedEvents = capture.Attempts, StopReason = operation.StopReason },
                        new StoreDeadline(store.CommitTimeout)).ConfigureAwait(false);
                    success = sealedResult.Committed;
                    if (!success) reason = sealedResult.ReasonCode;
                }
            }
            var terminal = await store.AppendDiagnosticOperationTerminalAsync(new(operation.Command.OperationId,
                success ? DiagnosticOperationPhase.Completed : DiagnosticOperationPhase.Interrupted, DateTimeOffset.UtcNow,
                reason, null, null, null, null) { ObservedEvents = capture?.Attempts ?? 0 },
                new StoreDeadline(store.CommitTimeout)).ConfigureAwait(false);
            if (!terminal.Committed) { success = false; reason = "DiagnosticSupportTerminalAuditUnavailable"; MarkAuditFault(reason); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { reason = "DiagnosticCaptureTerminalFailed"; success = false; MarkAuditFault(reason); }
        finally { operation.Authority?.Revoke(); FinishDiagnosticSupport(operation, reason, success); }
    }

    private void FinishDiagnosticSupport(SupportOperation operation, string reason, bool success)
    {
        operation.Registration.Dispose();
        lock (_sync)
        {
            if (!ReferenceEquals(_diagnosticSupportOperation, operation)) { operation.Retired.TrySetResult(true); return; }
            if (operation.Command is StartDiagnosticCaptureCommand)
                _captureStatus = _captureStatus! with { Phase = success ? DiagnosticCapturePhase.Completed : DiagnosticCapturePhase.Interrupted,
                    Elevated = false, ReasonCode = reason, ObservedEvents = operation.Capture?.Attempts ?? 0 };
            else _bundleStatus = _bundleStatus! with { Phase = success ? SupportBundlePhase.Completed : SupportBundlePhase.Interrupted, ReasonCode = reason };
            _diagnosticSupportOperation = null;
            if (!_disposed) PublishLocked(_snapshot);
            operation.Retired.TrySetResult(true);
        }
    }
    private void RevokeDiagnosticSupportLocked()
    {
        // Completed material remains a revocable capability during physical delivery.
        _publishedBundle?.Authority.Revoke();
        if (_diagnosticSupportOperation is not { } operation) return;
        operation.RequestStop();
    }

    private async Task ShutdownDiagnosticSupportAsync()
    {
        Task retired;
        lock (_sync) retired = Task.WhenAll(_diagnosticSupportOperation?.Retired.Task ?? Task.CompletedTask,
            _diagnosticDeliveryRetired?.Task ?? Task.CompletedTask);
        var timeout = (_diagnosticSupportOptions?.DiagnosticSupport?.Policy.ExportTimeout ?? TimeSpan.Zero) +
            (_diagnosticSupportOptions?.LoggingDiagnostics?.Policy.FlushTimeout ?? TimeSpan.Zero) +
            TimeSpan.FromTicks(2 * (_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2)).Ticks);
        if (await Task.WhenAny(retired, Task.Delay(timeout)).ConfigureAwait(false) != retired)
        {
            MarkAuditFault("DiagnosticSupportPhysicalRetirementPending");
            throw new InvalidOperationException("DiagnosticSupportPhysicalRetirementPending");
        }
        await retired.ConfigureAwait(false);
    }
}
