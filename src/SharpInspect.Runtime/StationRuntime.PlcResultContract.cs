using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IPlcResultContractService? _plcResultContracts;

    internal void ConfigurePlcResultContractService(IPlcResultContractService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_plcResultContracts is not null) throw new InvalidOperationException("PlcResultContractAlreadyConfigured");
            _plcResultContracts = service;
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitPlcResultContractAsync(ChangePlcResultContractCommand command,
        CancellationToken cancellationToken)
    {
        IPlcResultContractService? service;
        lock (_sync) service = _plcResultContracts;
        return service is null ? new(command.CorrelationId, CommandDisposition.Rejected,
            "PlcResultContractConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid()) :
            (await service.ChangeAsync(command, cancellationToken).ConfigureAwait(false)).Outcome;
    }

    internal async ValueTask<PlcResultContractRuntimeLease> EnterPlcResultContractChangeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _commandGate.WaitAsync(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false))
        {
            lock (_sync) return new(_snapshot.RuntimeEpoch, () => "PlcResultContractRuntimeBusy");
        }
        lock (_sync)
        {
            return new(_snapshot.RuntimeEpoch,
                () => { lock (_sync) return PlcResultContractBlockerLocked(); }, () => _commandGate.Release());
        }
    }

    private string? PlcResultContractBlockerLocked()
    {
        if (_disposed || _shutdownRequested || _snapshot.Lifecycle == RuntimeLifecycle.Stopped) return "RuntimeStopped";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed) return "PlcResultContractRequiresDisarmedNotReady";
        if (_snapshot.Busy || _snapshot.CurrentExecution is not null) return "PlcResultContractExecutionConflict";
        if (_snapshot.Evidence.PendingDeliveries != 0 ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset)
            return "PlcResultContractDeliveryConflict";
        if (_snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _snapshot.LastCommand?.State == OperationState.Pending) return "PlcResultContractWorkflowConflict";
        if (!_storeReady || _auditFault) return "PlcResultContractAuditUnavailable";
        return null;
    }
}
