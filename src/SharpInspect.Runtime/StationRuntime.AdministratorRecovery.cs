using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    string? IAdministratorRecoveryRuntimeGate.GetBlocker()
    {
        lock (_sync) return AdministratorRecoveryBlockerLocked();
    }

    async ValueTask<AdministratorRecoveryRuntimeLease> IAdministratorRecoveryRuntimeGate.EnterAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var blocker = AdministratorRecoveryBlockerLocked();
            if (blocker is not null)
                return new AdministratorRecoveryRuntimeLease(_snapshot.RuntimeEpoch, () => blocker);
        }
        if (!await _commandGate.WaitAsync(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false))
            return new AdministratorRecoveryRuntimeLease(Guid.Empty, () => "RecoveryRuntimeBusy");
        lock (_sync)
        {
            var blocker = AdministratorRecoveryBlockerLocked();
            if (blocker is not null)
            {
                _commandGate.Release();
                return new AdministratorRecoveryRuntimeLease(_snapshot.RuntimeEpoch, () => blocker);
            }
            // The command gate remains owned until the recovery transaction settles.
            // Future operational providers must integrate here; no public safety setter exists.
            return new AdministratorRecoveryRuntimeLease(_snapshot.RuntimeEpoch,
                () => { lock (_sync) return AdministratorRecoveryBlockerLocked(); },
                () => _commandGate.Release());
        }
    }

    private string? AdministratorRecoveryBlockerLocked()
    {
        if (_disposed || _shutdownRequested) return "RecoveryRuntimeStopped";
        if (RecipeActivationConfigurationBlockedLocked) return "RecipeActivationInProgress";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed) return "RecoveryRequiresDisarmedStation";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null) return "RecoveryInspectionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "RecoveryDeliveryConflict";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _snapshot.LastCommand?.State == OperationState.Pending)
            return "RecoveryWorkflowConflict";
        if (!_storeReady || _auditFault) return "RecoveryAuditUnavailable";
        // Ready=false and LocallyDisarmed only prove software disarming. This development
        // Runtime has no PLC stop/reconciliation witness yet. Unknown evidence must never
        // become recovery authority, even when all other projections happen to look idle.
        return "SafetyStopUnverified";
    }
}
