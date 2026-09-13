using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task ExecuteProductionRecoveryAsync(ProductionRecoveryOwner owner,
        SqliteCommandStore store, CancellationToken token)
    {
        var authorized = owner.Authorization!;
        var session = authorized.Event!.Recovery!;
        var profile = _productionInspectionOptions!.Profile;
        var attempt = authorized.RecoveryAttemptId ?? session.RecoveryAttemptId;
        var completed = false;
        var reason = "ProductionRecoveryInterrupted";
        ProductionRecoveryTransport? transport = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (ProductionRecoveryAuthorityFailure(owner) is { } revoked)
                throw new InvalidOperationException(revoked);
            transport = new(profile, owner.RuntimeEpoch, start => StartProductionRecoveryRequest(owner, start),
                () => ProductionRecoveryAuthorityFailure(owner), health =>
                {
                    lock (_sync)
                    {
                        if (!ReferenceEquals(_productionRecoveryOwner, owner) || _disposed) return;
                        PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                            PlcCommunication = health,
                            Plc = new(health.TransportReachable ? HealthState.Healthy : HealthState.Faulted,
                                health.ControllerHeartbeatFresh && health.RuntimeHeartbeatObserved
                                    ? HealthState.Healthy : HealthState.Faulted,
                                health.Synchronized ? HealthState.Healthy : HealthState.Degraded) });
                    }
                }, transition => RecordProductionRecoveryCommunicationAsync(owner, transition),
                () => Interlocked.Increment(ref _plcCommunicationGeneration),
                completionAuthorityFailure: () => ProductionRecoveryFinalFailure(owner));
            var observed = await transport.ClearAndSynchronizeAsync(token).ConfigureAwait(false);
            var receipt = new ProductionRecoveryCleanupReceipt(owner.RuntimeEpoch,
                observed.Communication.ConnectionGeneration, observed.ObservedAtUtc,
                observed.ObservedMonotonicTimestamp, true, "ProductionRecoveryCleanupSynchronized",
                profile.EndpointBindingHash, profile.ContentHash, profile.CommunicationBinding.Policy.ContentHash,
                observed.ControllerEpoch, observed.Communication, triggerLowObserved: true,
                ackLowObserved: true, runtimeOutputsClear: true);
            token.ThrowIfCancellationRequested();
            var completion = new ProductionRecoveryCompletionWriteRequest(owner.Command.CorrelationId,
                attempt, owner.RuntimeEpoch, session.ContentHash, receipt, SafetyCapture: owner.Safety)
            { FinalGuard = () => token.IsCancellationRequested ? "ProductionRecoveryCancelled" : transport.CompletionFailure() };
            var deadline = new StoreDeadline(_audit!.CommitTimeout);
            ProductionRecoveryWriteResult result;
            while (true)
            {
                result = await store.CompleteProductionRecoveryAsync(completion, deadline,
                    CancellationToken.None).ConfigureAwait(false);
                if (result.Committed || result.ReasonCode != "ProductionRecoveryCommitFenceBusy" ||
                    deadline.Remaining <= TimeSpan.Zero) break;
                // 写入器在返回前已回滚事务；在 SQLite 外让出后，只用同一收据和期限重试持久化，绝不重复物理清理。
                var remaining = deadline.Remaining;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10,
                    remaining.TotalMilliseconds)), token).ConfigureAwait(false);
            }
            if (!result.Committed) throw new InvalidOperationException(result.ReasonCode);
            completed = true;
            reason = "ProductionRecoveryCompletedArmRequired";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reason = RecoveryAttemptFailure(exception);
            try
            {
                var failed = await store.FailProductionRecoveryAsync(authorized.CommandFact!, attempt,
                    session.ContentHash, reason, new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None)
                    .ConfigureAwait(false);
                if (!failed.Committed) MarkAuditFault("ProductionRecoveryFailureAuditUnavailable");
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { MarkAuditFault("ProductionRecoveryFailureAuditUnavailable"); }
        }
        finally
        {
            if (transport is not null)
            {
                try { await transport.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    reason = "ProductionRecoveryTransportRetirementFailed";
                    MarkAuditFault(reason);
                }
            }
        }

        var pending = await RefreshProductionRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            var clear = completed && pending.Available && pending.PendingCount == 0 &&
                !_auditFault && !_disposed && !_shutdownRequested;
            _productionInspectionRecoveryBlocked = !clear;
            PublishLocked(_snapshot with { Ready = false, Busy = false,
                ArmState = ProductionArmState.Disarmed, Mode = ExclusiveMode.None,
                Recovery = clear ? RecoveryState.None : RecoveryState.Required,
                Handshake = clear ? HandshakePhase.Idle : HandshakePhase.Unknown,
                CurrentExecution = clear ? null : _snapshot.CurrentExecution,
                LastCommand = new(owner.Command.CorrelationId,
                    clear ? OperationState.Completed : OperationState.Failed, reason) });
            if (clear)
            {
                // 旧生产任务和手动恢复传输均已退休后，才恢复普通的已撤防启动/监视路径。
                _productionInspectionOwner = null;
                _productionInspectionStartupVerified = false;
                _productionInspectionTask = Task.Run(RunProductionInspectionsAsync);
            }
        }
    }

    private async Task RecordProductionRecoveryCommunicationAsync(ProductionRecoveryOwner owner,
        PlcCommunicationTransition transition)
    {
        var profile = _productionInspectionOptions!.Profile;
        var request = new PlcCommunicationWriteRequest(owner.RuntimeEpoch, profile.EndpointBindingHash,
            profile.ContentHash, profile.CommunicationBinding.Policy.ContentHash, transition.Generation,
            transition.Attempt, Enum.Parse<PlcCommunicationEventKind>(transition.Kind), transition.ReasonCode,
            transition.ControllerEpoch, DateTimeOffset.UtcNow, transition.ObservedAt,
            // 一次生产恢复会话可包含多个已授权重试，但通信事实始终精确归属于本次命令尝试。
            RecoveryCycleId: owner.Authorization!.CommandFact!.AttemptId,
            RunId: owner.Command.InspectionId,
            CycleSequence: owner.Authorization!.Event!.Admission.ControllerCycle.CycleSequence);
        var result = await ((SqliteCommandStore)_audit!).AppendPlcCommunicationEventAsync(request,
            new StoreDeadline(_audit!.CommitTimeout), CancellationToken.None).ConfigureAwait(false);
        if (!result.Committed) throw new InvalidOperationException(result.ReasonCode);
    }

    private async Task ShutdownProductionRecoveryAsync()
    {
        Task? pending;
        lock (_sync) pending = _productionRecoveryOwner?.Retired.Task;
        if (pending is not null)
            await pending.WaitAsync(_productionInspectionOptions!.Recovery!.RetirementTimeout).ConfigureAwait(false);
        if (_productionRecoverySafety is not null)
            await _productionRecoverySafety.DisposeAsync().ConfigureAwait(false);
    }
}
