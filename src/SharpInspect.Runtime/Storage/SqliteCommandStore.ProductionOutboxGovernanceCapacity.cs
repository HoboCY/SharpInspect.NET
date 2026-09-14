using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private sealed record OutboxGovernanceCapacity(long AuditSequence, int Writes, long FutureReserve);

    private OutboxGovernanceCapacity PrepareOutboxGovernanceCapacity(sqlite3 database, IdentityUpdate update,
        OutboxGovernanceCommandState state, StoreDeadline deadline)
    {
        var options = state.Options!;
        var future = ReadProductionOutboxReserveRows(database, deadline).Events;
        var addedRows = 0;
        long addedBytes = 0;
        var writes = update.Events.Count + (update.CommandFacts?.Count ?? 0);
        if (update.OutboxRecovery is { } recovery)
            future = checked(future + 2L * recovery.GrantedAttempts);
        else if (update.OutboxCorrection is { } correction)
        {
            var source = state.Deliveries.Single(x => x.Delivery.DeliveryId == correction.SourceDeliveryId);
            future = checked(future + 2L * source.Delivery.MaximumAttempts);
            addedRows = 2;
            addedBytes = ProductionInspectionStoredPayloadBytes(correction.Payload.Length);
            writes++;
        }
        else throw new InvalidOperationException("OutboxGovernanceMutationMissing");
        AuditChainDatabase.EnsureProductionOutboxTransactionCapacity(database, _policy!, writes, future, deadline);
        EnsureOutboxGovernanceLocalCapacity(database, options, future, addedRows, addedBytes, deadline);
        return new(AuditChainDatabase.Tail(database, deadline).Sequence, writes, future);
    }

    private static void EnsureOutboxGovernanceLocalCapacity(sqlite3 database, ProductionOutboxStoreOptions options,
        long future, int addedRows, long addedBytes, StoreDeadline deadline)
    {
        var preCore = ReadProductionOutboxPreCoreReserve(database, options, deadline);
        var rows = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM production_outbox_deliveries;", deadline) +
            AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM production_outbox_events;", deadline);
        var bytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(PayloadBase64)),0) FROM production_outbox_deliveries;", deadline) +
            AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(SUM(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        AuditChainDatabase.Require(checked(rows + addedRows + future + preCore.Rows) <= options.MaximumEvents,
            "ProductionOutboxEntryCapacityExceeded");
        AuditChainDatabase.Require(checked(bytes + addedBytes + future * ProductionOutboxStoreOptions.MaximumStoredReceiptBytes +
            preCore.PayloadBytes) <= options.MaximumTotalBytes, "ProductionOutboxTotalCapacityExceeded");
    }

    private static IdentityUpdate RejectOutboxGovernanceCapacity(IdentityUpdate update, string reason)
    {
        var outcome = (RuntimeCommandOutcome)update.Result!;
        return new IdentityUpdate(outcome with { Disposition = CommandDisposition.Rejected, ReasonCode = reason },
            update.Events.Select(x => x with { Kind = IdentityEventKind.ManagementRejected, ReasonCode = reason,
                OperationId = null, RecoverySafetyEvidence = null }).ToArray(),
            update.CommandFacts!.Select(x => x with { Disposition = CommandDisposition.Rejected, ReasonCode = reason }).ToArray());
    }

    private void VerifyOutboxGovernanceCapacity(sqlite3 database, OutboxGovernanceCapacity capacity,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(AuditChainDatabase.Tail(database, deadline).Sequence == capacity.AuditSequence + capacity.Writes &&
            ReadProductionOutboxReserveRows(database, deadline).Events == capacity.FutureReserve,
            "ProductionOutboxGovernanceReserveMismatch");
        EnsureOutboxGovernanceLocalCapacity(database, _options.Outbox!, capacity.FutureReserve, 0, 0, deadline);
    }
}
