using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task ProcessAutomaticProductionArmAsync(ProductionInspectionOwner owner,
        ModbusQualificationChannel channel, InspectionCycleOutputLatch output, CancellationToken token)
    {
        AutomaticProductionArmCapability? attempt;
        lock (_sync) attempt = _automaticProductionArm;
        if (attempt is null || attempt.Terminal || !ReferenceEquals(attempt.Owner, owner)) return;
        if (attempt.Authorized)
        {
            bool valid;
            lock (_sync) valid = AutomaticProductionArmReadyPermitLocked(owner) &&
                attempt.ReadyDeadline is { Expired: false };
            if (!valid) await RejectAutomaticProductionArmAsync(attempt, channel, output,
                ProductionArmReason.AuthorityChanged, "ProductionArmReadyPermitRevoked").ConfigureAwait(false);
            return;
        }
        if (!attempt.TryClaim()) return;
        var entered = false;
        try
        {
            lock (_sync)
            {
                attempt.Report = _snapshot.ProductionAdmission;
                attempt.Capture = new(_snapshot.RuntimeEpoch, _snapshot.Revision, _admissionGeneration, _admissionStateHash);
                attempt.Heads = _lastAdmissionFacts?.DurableHeads ?? new Dictionary<string, string>();
            }
            var started = await WriteAutomaticProductionArmAsync(attempt, ProductionArmEventKind.Attempted,
                ProductionArmReason.None, "ProductionArmAttemptStarted").ConfigureAwait(false);
            attempt.AttemptCommitted = started.Committed;
            lock (_sync) ProjectAutomaticProductionArmLocked(attempt, ProductionArmAttemptOutcome.Waiting,
                ProductionArmReason.None, "ProductionArmAttemptStarted", started.Committed);
            if (!started.Committed)
            {
                await RejectAutomaticProductionArmAsync(attempt, channel, output,
                    ProductionArmReason.AuditUnavailable, started.ReasonCode).ConfigureAwait(false);
                return;
            }
            var maintenance = attempt.Maintenance;
            if (maintenance is null || maintenance.State is ProductionArmMaintenanceState.Unavailable or
                    ProductionArmMaintenanceState.InProgress || maintenance.StationId != attempt.StationId ||
                maintenance.DeploymentHash != attempt.Deployment.ContentHash)
            {
                await RejectAutomaticProductionArmAsync(attempt, channel, output,
                    ProductionArmReason.MaintenanceEvidenceUnavailable, "ProductionArmMaintenanceEvidenceUnavailable")
                    .ConfigureAwait(false);
                return;
            }
            if (!await HasVerifiedManualMaintenanceArmAsync(maintenance, token).ConfigureAwait(false))
            {
                await RejectAutomaticProductionArmAsync(attempt, channel, output,
                    ProductionArmReason.ManualArmAfterMaintenanceRequired, "ProductionArmManualAfterMaintenanceRequired")
                    .ConfigureAwait(false);
                return;
            }
            if (attempt.Cause == ProductionArmCause.PlcActivation &&
                _productionInspectionOptions!.Profile.ProductionArmStatus is null)
            {
                await RejectAutomaticProductionArmAsync(attempt, channel, output,
                    ProductionArmReason.ConfigurationUnavailable, "ProductionArmPlcStatusBindingRequired").ConfigureAwait(false);
                return;
            }

            var policy = _productionInspectionOptions!.Profile.CommunicationBinding.Policy;
            var windowDeadline = new StoreDeadline(policy.SynchronizationTimeout);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                ProductionArmReason sourceFailure;
                bool stable;
                lock (_sync)
                {
                    sourceFailure = AutomaticProductionArmSourceFailureLocked(attempt);
                    stable = attempt.Stability.IsStable(Stopwatch.GetTimestamp());
                }
                if (sourceFailure != ProductionArmReason.None)
                {
                    await RejectAutomaticProductionArmAsync(attempt, channel, output, sourceFailure,
                        "ProductionArmSourceChanged").ConfigureAwait(false);
                    return;
                }
                if (stable) break;
                if (windowDeadline.Expired)
                {
                    await RejectAutomaticProductionArmAsync(attempt, channel, output,
                        ProductionArmReason.InputsNotStable, "ProductionArmFreshInputsNotStable").ConfigureAwait(false);
                    return;
                }
                owner.Observer!.RequireHealthy();
                await Task.Delay(policy.PollInterval, token).ConfigureAwait(false);
            }

            var deadline = new StoreDeadline(_audit!.CommitTimeout);
            entered = await _commandGate.WaitAsync(PositiveRemaining(deadline), token).ConfigureAwait(false);
            if (!entered)
            {
                await RejectAutomaticProductionArmAsync(attempt, channel, output,
                    ProductionArmReason.RuntimeBusy, "ProductionArmCommandBoundaryBusy").ConfigureAwait(false);
                return;
            }
            AdmissionCapture initial;
            lock (_sync) initial = new(_snapshot.RuntimeEpoch, _snapshot.Revision, _admissionGeneration, _admissionStateHash);
            var barrier = await _cameraSetupRuntime.CheckNetworkBarrierAsync(token).AsTask()
                .WaitAsync(PositiveRemaining(deadline), token).ConfigureAwait(false);
            // 系统路径刻意使用内置事实源；公开事实源不能替 Runtime 断言运行时准入门。
            var factsTask = new CurrentStationFactsSource(this).CaptureAsync(token).AsTask();
            _ = factsTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var facts = await factsTask.WaitAsync(PositiveRemaining(deadline), token).ConfigureAwait(false);
            ProductionArmReason captureFailure;
            lock (_sync)
            {
                captureFailure = AutomaticProductionArmSourceFailureLocked(attempt);
                if (captureFailure == ProductionArmReason.None && (barrier is not null ||
                    initial.RuntimeEpoch != _snapshot.RuntimeEpoch || initial.Generation != _admissionGeneration ||
                    initial.StateHash != _admissionStateHash)) captureFailure = ProductionArmReason.AuthorityChanged;
                if (captureFailure == ProductionArmReason.None)
                {
                    _productionAdmissionFactsFailure = null;
                    _lastAdmissionFacts = facts;
                    var observed = ProductionAdmissionEngine.Evaluate(initial.RuntimeEpoch,
                        checked(_snapshot.Revision + 1), initial.Generation, DateTimeOffset.UtcNow, facts);
                    PublishLocked(_snapshot with { ProductionAdmission = observed });
                    attempt.Capture = new(_snapshot.RuntimeEpoch, _snapshot.Revision, _admissionGeneration, _admissionStateHash);
                    attempt.Report = _snapshot.ProductionAdmission;
                    attempt.Heads = facts.DurableHeads;
                    attempt.InputStability = attempt.Stability.TryCapture(Stopwatch.GetTimestamp());
                    if (attempt.Report?.CanArm != true) captureFailure = ProductionArmReason.AdmissionGateBlocked;
                    else if (attempt.InputStability is null) captureFailure = ProductionArmReason.InputsNotStable;
                }
            }
            if (captureFailure != ProductionArmReason.None)
            {
                await RejectAutomaticProductionArmAsync(attempt, channel, output, captureFailure,
                    barrier ?? "ProductionArmAdmissionRejected").ConfigureAwait(false);
                return;
            }
            var authorized = await WriteAutomaticProductionArmAsync(attempt, ProductionArmEventKind.Authorized,
                ProductionArmReason.None, "ProductionArmAuthorized").ConfigureAwait(false);
            attempt.AuthorizationEvent = authorized.Event;
            var verified = authorized.Committed && await WaitForProductionAuditVerifiedAsync(deadline).ConfigureAwait(false) &&
                await VerifyProductionAdmissionHeadsAsync(attempt.Heads, deadline).ConfigureAwait(false);
            bool allowed;
            lock (_sync)
            {
                if (!_disposed) PublishLocked(_snapshot);
                allowed = verified && authorized.Event is not null && attempt.Capture is { } capture && ProductionArmLiveFenceLocked(capture) &&
                    AutomaticProductionArmSourceFailureLocked(attempt) == ProductionArmReason.None &&
                    attempt.Stability.IsStable(Stopwatch.GetTimestamp());
                attempt.Authorized = authorized.Committed;
                if (allowed)
                {
                    attempt.ReadyDeadline = new StoreDeadline(policy.OperationTimeout + _audit.CommitTimeout);
                    PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Armed },
                        completingProductionArm: true);
                    ProjectAutomaticProductionArmLocked(attempt, ProductionArmAttemptOutcome.Authorized,
                        ProductionArmReason.None, "ProductionArmAuthorizedAwaitingReady", true);
                }
            }
            if (!allowed) await RejectAutomaticProductionArmAsync(attempt, channel, output,
                authorized.Committed ? ProductionArmReason.AuthorityChanged : ProductionArmReason.AuditUnavailable,
                authorized.Committed ? "ProductionArmPostAuthorizationChanged" : authorized.ReasonCode).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await RejectAutomaticProductionArmAsync(attempt, channel, output,
                token.IsCancellationRequested ? ProductionArmReason.Cancelled : ProductionArmReason.AuthorityChanged,
                ProductionFailureReason(exception)).ConfigureAwait(false);
        }
        finally { if (entered) _commandGate.Release(); }
    }

    private async Task ConfirmAutomaticProductionArmReadyAsync(ProductionInspectionOwner owner,
        ModbusQualificationChannel channel, InspectionCycleOutputLatch output)
    {
        AutomaticProductionArmCapability? attempt;
        bool valid;
        lock (_sync)
        {
            attempt = _automaticProductionArm;
            if (attempt is not { Terminal: false, Authorized: true } || !ReferenceEquals(attempt.Owner, owner)) return;
            valid = attempt.PhysicalReadyObserved && attempt.ReadyReceipt is not null;
        }
        if (!valid)
        {
            await RejectAutomaticProductionArmAsync(attempt, channel, output,
                ProductionArmReason.AuthorityChanged, "ProductionArmReadyConfirmationRevoked").ConfigureAwait(false);
            return;
        }
        var written = await WriteAutomaticProductionArmAsync(attempt, ProductionArmEventKind.ReadyConfirmed,
            ProductionArmReason.None, "ProductionArmPhysicalReadyConfirmed").ConfigureAwait(false);
        attempt.ReadyFactCommitted = written.Committed;
        var deadline = new StoreDeadline(_audit!.CommitTimeout);
        var verified = written.Committed && await WaitForProductionAuditVerifiedAsync(deadline).ConfigureAwait(false);
        lock (_sync)
        {
            if (!_disposed) PublishLocked(_snapshot);
            // Ready 收据在开始观察前已冻结；之后的 Stop/资格变化撤销当前 Ready，不改写这段历史。
            valid = verified;
            if (valid)
            {
                attempt.Terminal = true;
                ProjectAutomaticProductionArmLocked(attempt, ProductionArmAttemptOutcome.ReadyConfirmed,
                    ProductionArmReason.None, "ProductionArmPhysicalReadyConfirmed", true);
            }
        }
        if (!valid)
            await RejectAutomaticProductionArmAsync(attempt, channel, output,
                ProductionArmReason.AuditUnavailable, "ProductionArmReadyConfirmationUnavailable").ConfigureAwait(false);
        else
        {
            await DeliverAutomaticProductionArmStatusAsync(attempt, channel).ConfigureAwait(false);
            var deliveryVerified = await WaitForProductionAuditVerifiedAsync(new StoreDeadline(_audit.CommitTimeout)).ConfigureAwait(false);
            bool scopeValid;
            lock (_sync)
            {
                if (!_disposed) PublishLocked(_snapshot);
                scopeValid = deliveryVerified && attempt.Capture is { } capture && ProductionArmLiveFenceLocked(capture) &&
                    AutomaticProductionArmSourceFailureLocked(attempt) == ProductionArmReason.None;
                attempt.ReadyAuditPending = false;
                if (!_disposed) PublishLocked(_snapshot with { Ready = scopeValid,
                    ArmState = scopeValid ? ProductionArmState.Armed : ProductionArmState.Disarmed });
                if (!scopeValid) owner.Observer?.RejectPendingAdmission("ProductionArmReadyAuditUnavailable");
            }
            if (!scopeValid)
                await ClearConfirmedProductionArmReadyAsync(output, auditUnavailable: !deliveryVerified).ConfigureAwait(false);
        }
    }

    private async Task ClearConfirmedProductionArmReadyAsync(InspectionCycleOutputLatch output, bool auditUnavailable)
    {
        // 拒绝观察器会清除 accepting 标志，主循环无法据此判断已确认的物理位是否仍为高电平；显式关闭它，但不改写旧收据。
        var recoveryRequired = auditUnavailable;
        if (auditUnavailable) MarkAuditFault("ProductionArmReadyStatusAuditUnavailable");
        try
        {
            using var cleanup = new CancellationTokenSource(_productionInspectionOptions!.Profile.TransportTimeout);
            await output.ChangeAsync(cleanup.Token, ready: false, requireOwner: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { recoveryRequired = true; }
        lock (_sync)
        {
            if (recoveryRequired) _productionInspectionRecoveryBlocked = true;
            if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                Recovery = recoveryRequired ? RecoveryState.Required : _snapshot.Recovery });
        }
    }

    private async Task RejectAutomaticProductionArmAsync(AutomaticProductionArmCapability attempt,
        ModbusQualificationChannel channel, InspectionCycleOutputLatch output, ProductionArmReason reason, string reasonCode)
    {
        if (attempt.PhysicalReadyObserved)
        {
            reason = ProductionArmReason.ReadyWriteUncertain;
            reasonCode = "ProductionArmPhysicalReadyAuditUnavailable";
        }
        lock (_sync)
        {
            if (attempt.Terminal) return;
            attempt.Terminal = true;
            attempt.ReadyAuditPending = false;
            attempt.Owner.Observer?.StopAccepting();
            attempt.Owner.Observer?.RejectPendingAdmission("ProductionArmAttemptRejected");
            if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed });
        }
        var persisted = false;
        if (attempt.AttemptCommitted && !attempt.ReadyFactCommitted)
        {
            var result = await WriteAutomaticProductionArmAsync(attempt,
                attempt.Authorized ? ProductionArmEventKind.Failed : ProductionArmEventKind.Rejected,
                reason, reasonCode).ConfigureAwait(false);
            persisted = result.Committed;
        }
        // 未验证的成功记录不能被重新解释成持久失败。
        try
        {
            using var cleanup = new CancellationTokenSource(_productionInspectionOptions!.Profile.TransportTimeout);
            await output.ChangeAsync(cleanup.Token, ready: false, requireOwner: true).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // 未获确认的撤销只能保留为恢复要求，不能形成 Ready。
            lock (_sync)
            {
                _productionInspectionRecoveryBlocked = true;
                if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.Required });
            }
        }
        if (!persisted) MarkAuditFault("ProductionArmTerminalAuditUnavailable");
        if (attempt.PhysicalReadyObserved)
        {
            MarkAuditFault("ProductionArmPhysicalReadyAuditUnavailable");
            lock (_sync)
            {
                _productionInspectionRecoveryBlocked = true;
                if (!_disposed) PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.Required });
            }
        }
        lock (_sync) ProjectAutomaticProductionArmLocked(attempt,
            attempt.PhysicalReadyObserved ? ProductionArmAttemptOutcome.Failed :
            attempt.Cause == ProductionArmCause.PlcActivation ? ProductionArmAttemptOutcome.ActivatedButNotReady :
                attempt.Authorized ? ProductionArmAttemptOutcome.Failed : ProductionArmAttemptOutcome.Rejected,
            reason, reasonCode, persisted);
        if (persisted) await DeliverAutomaticProductionArmStatusAsync(attempt, channel).ConfigureAwait(false);
    }

    private async Task DeliverAutomaticProductionArmStatusAsync(AutomaticProductionArmCapability attempt,
        ModbusQualificationChannel channel)
    {
        var hasBinding = _productionInspectionOptions?.Profile.ProductionArmStatus is not null;
        ProductionArmPolicyStatus? status;
        lock (_sync) status = _snapshot.ProductionArming;
        if (status?.AttemptId != attempt.AttemptId || !status.AuditCommitted) return;
        var delivered = false;
        try
        {
            if (!hasBinding) throw new InvalidOperationException("ProductionArmStatusBindingAbsent");
            using var deadline = new CancellationTokenSource(_productionInspectionOptions!.Profile.TransportTimeout);
            lock (_sync)
            {
                if (!ReferenceEquals(_productionInspectionOwner, attempt.Owner) ||
                    attempt.Owner.Health is not { Healthy: true } health ||
                    health.ControllerEpoch != attempt.ControllerEpoch || health.ConnectionGeneration != attempt.ConnectionGeneration)
                    throw new InvalidOperationException("ProductionArmStatusOwnerChanged");
                attempt.Owner.ArmStatusWrite = attempt;
            }
            await channel.WriteProductionArmStatusAsync(new(true, (ushort)status.Cause, (ushort)status.Outcome,
                (ushort)status.Reason, (ushort)(status.BlockedGate ?? 0), status.RuntimeEpoch,
                status.ControllerEpoch, status.RequestSequence, status.SelectionCode, status.AttemptId), deadline.Token)
                .ConfigureAwait(false);
            delivered = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally
        {
            lock (_sync)
                if (ReferenceEquals(attempt.Owner.ArmStatusWrite, attempt)) attempt.Owner.ArmStatusWrite = null;
        }
        var recorded = await WriteAutomaticProductionArmAsync(attempt,
            delivered ? ProductionArmEventKind.PlcStatusDelivered : ProductionArmEventKind.PlcStatusUndelivered,
            delivered ? ProductionArmReason.None : hasBinding ? ProductionArmReason.AuthorityChanged : ProductionArmReason.ConfigurationUnavailable,
            delivered ? "ProductionArmPlcStatusWriteConfirmed" : hasBinding ? "ProductionArmPlcStatusWriteUnconfirmed" : "ProductionArmPlcStatusNotConfigured")
            .ConfigureAwait(false);
        if (!recorded.Committed) MarkAuditFault("ProductionArmStatusDeliveryAuditUnavailable");
        lock (_sync)
            if (_snapshot.ProductionArming?.AttemptId == attempt.AttemptId)
                ProjectAutomaticProductionArmLocked(attempt, status.Outcome, status.Reason, status.ReasonCode,
                    status.AuditCommitted && recorded.Committed, delivered && recorded.Committed);
    }

    private ValueTask<ProductionArmWriteResult> WriteAutomaticProductionArmAsync(AutomaticProductionArmCapability attempt,
        ProductionArmEventKind kind, ProductionArmReason reason, string reasonCode)
    {
        if (_audit is not SqliteCommandStore store || _productionInspectionStoreOptions?.ProductionArming is null)
            return ValueTask.FromResult(new ProductionArmWriteResult(false, "ProductionArmStoreConfigurationRequired"));
        var request = new ProductionArmWriteRequest(attempt.AttemptId, attempt.Owner.RuntimeEpoch, attempt.Cause, kind,
            attempt.StationId, attempt.Deployment.StartupProduction.Reference, attempt.Deployment.PostActivationArm.Reference,
            attempt.Deployment.ContentHash, attempt.Request, attempt.Activation, attempt.Maintenance?.JournalHeadHash,
            attempt.Capture?.Generation ?? 0, attempt.Report, attempt.Heads, attempt.Heads, reason, reasonCode,
            ReadyReceipt: attempt.ReadyReceipt, InputStability: attempt.InputStability);
        return store.AppendProductionArmEventAsync(request, new StoreDeadline(_audit.CommitTimeout), CancellationToken.None);
    }
}
