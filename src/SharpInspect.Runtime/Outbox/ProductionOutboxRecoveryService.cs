using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Outbox;

/// <summary>
/// The governed recovery surface of the schema-37 outbox extension. Every request runs through
/// the one authoritative writer transaction: the fresh actor/session/permission/policy/target/
/// reason binding, the fresh Step-Up consumption, the command fact and the immutable operation
/// record commit together, or nothing does. The service never rebuilds a historical payload from
/// current state and never edits an existing delivery.
/// </summary>
internal sealed class ProductionOutboxRecoveryService : IProductionOutboxRecoveryService
{
    private readonly SqliteCommandStore _store;
    private readonly LocalAuthorizationService _authorization;
    private readonly ProductionStoreOptions _options;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private readonly ProductionOutboxOptions _transports;

    internal ProductionOutboxRecoveryService(SqliteCommandStore store,
        LocalAuthorizationService authorization, ProductionStoreOptions options,
        Func<ValueTask<StationStateSnapshot>> readStation, ProductionOutboxOptions? transports = null)
    {
        _store = store;
        _authorization = authorization;
        _options = options;
        _readStation = readStation;
        _transports = transports ?? new ProductionOutboxOptions(Array.Empty<OutboxTransportBinding>());
    }

    public async ValueTask<ProductionOutboxRecoveryResult> RecoverAsync(
        RecoverOutboxDeliveryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var outcome = Rejected(command.CorrelationId, "OutboxGovernanceUnavailable");
        if (_options.Outbox?.ManualRecovery is null)
            return new(outcome with { ReasonCode = "OutboxGovernanceConfigurationRequired" },
                Guid.Empty, command.DeliveryId, 0, 0, "OutboxGovernanceConfigurationRequired");
        var result = await AuthorizeAsync(command, cancellationToken).ConfigureAwait(false);
        return result.Recovery ?? new(result.Outcome, Guid.Empty, command.DeliveryId, 0, 0,
            result.Outcome.ReasonCode);
    }

    public async ValueTask<ProductionOutboxCorrectionResult> CreateCorrectiveDeliveryAsync(
        CreateCorrectiveOutboxDeliveryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var outcome = Rejected(command.CorrelationId, "OutboxGovernanceUnavailable");
        if (_options.Outbox?.ManualRecovery is null)
            return new(outcome with { ReasonCode = "OutboxGovernanceConfigurationRequired" },
                Guid.Empty, command.SourceDeliveryId, string.Empty, string.Empty);
        var result = await AuthorizeAsync(command, cancellationToken).ConfigureAwait(false);
        return result.Correction ?? new(result.Outcome, Guid.Empty, command.SourceDeliveryId,
            string.Empty, string.Empty);
    }

    private async ValueTask<OutboxGovernanceAuthorizationResult> AuthorizeAsync(RuntimeCommand command,
        CancellationToken cancellationToken)
    {
        var outcome = Rejected(command.CorrelationId, "OutboxGovernanceUnavailable");
        RuntimeEpochOutcome epoch;
        try
        {
            var station = await _readStation().ConfigureAwait(false);
            epoch = new(station.RuntimeEpoch, null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { epoch = new(Guid.Empty, "OutboxGovernanceRuntimeUnavailable"); }
        if (epoch.Failure is { } failure) return new(outcome with { ReasonCode = failure }, null, null);
        var deadline = new StoreDeadline(_options.CommitTimeout);
        return await _authorization.AuthorizeOutboxGovernanceAsync(command, epoch.Epoch, deadline,
            cancellationToken, delivery => ProductionOutboxWorker.HistoricalHandlerFailure(delivery, _transports))
            .ConfigureAwait(false);
    }

    private static RuntimeCommandOutcome Rejected(Guid correlationId, string reason) => new(correlationId,
        CommandDisposition.Rejected, reason, AuditPersistence.NotAttempted);

    private sealed record RuntimeEpochOutcome(Guid Epoch, string? Failure);
}
