using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IProductionOutboxRecoveryService? _outboxRecoveryService;

    internal void ConfigureOutboxRecoveryService(IProductionOutboxRecoveryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_outboxRecoveryService is not null)
                throw new InvalidOperationException("OutboxRecoveryAlreadyConfigured");
            _outboxRecoveryService = service;
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitOutboxGovernanceAsync(RuntimeCommand command,
        CancellationToken token)
    {
        IProductionOutboxRecoveryService? service;
        lock (_sync)
        {
            if (_shutdownRequested || _disposed)
                return new(command.CorrelationId, CommandDisposition.Rejected, "RuntimeStopped",
                    AuditPersistence.Unavailable, Guid.NewGuid());
            service = _outboxRecoveryService;
        }
        if (service is null)
            return new(command.CorrelationId, CommandDisposition.Rejected,
                "OutboxGovernanceConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid());
        return command is RecoverOutboxDeliveryCommand recovery
            ? (await service.RecoverAsync(recovery, token).ConfigureAwait(false)).Outcome
            : (await service.CreateCorrectiveDeliveryAsync((CreateCorrectiveOutboxDeliveryCommand)command,
                token).ConfigureAwait(false)).Outcome;
    }
}
