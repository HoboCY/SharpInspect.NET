using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal ValueTask<OutboxWriteResult> BlockOutboxHandlerAsync(OutboxHandlerBlockRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default) =>
        EnqueueProductionOutboxAsync(new ProductionOutboxWork(request), deadline, cancellationToken);

    private static bool IsProductionOutboxHandlerBlockReason(string reason) => reason is
        "OutboxRequiredHistoricalHandlerMissing" or "OutboxHistoricalHandlerUnavailable" or
        "OutboxHistoricalHandlerContractMismatch";

    private StoreWriteResult AppendProductionOutboxHandlerBlock(sqlite3 database, ProductionOutboxWork work,
        StoreDeadline deadline)
    {
        var request = work.HandlerBlock!;
        var options = _options.Outbox!;
        if (options.ManualRecovery is not { } recovery)
            return ProductionOutboxRejected(work, "ProductionOutboxRecoveryGovernedMigrationRequired");
        var transactionStarted = false;
        var committed = false;
        try
        {
            AuditChainDatabase.Require(request.DeliveryId != Guid.Empty && request.RecordedAtUtc != default &&
                request.RecordedAtUtc.Offset == TimeSpan.Zero && IsProductionOutboxHandlerBlockReason(request.ReasonCode),
                "ProductionOutboxHandlerBlockInvalid");
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            AuditChainDatabase.RequireFullProductionOutboxVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadProductionOutboxDeliveryFacts(database, options, request.DeliveryId, deadline);
            var latestGrant = facts.Grants.Where(x => x.DeliveryId == request.DeliveryId)
                .Select(x => x.AuthorizationAuditSequence).DefaultIfEmpty(0).Max();
            var existing = facts.Events.LastOrDefault(x => x.Kind == OutboxEventKind.HandlerBlocked &&
                x.AuditSequence > latestGrant);
            if (existing is not null) return ProductionOutboxIdempotent(work, existing, database, deadline);
            var delivery = facts.Stored.Delivery;
            if (facts.State.State == OutboxDeliveryState.Succeeded || delivery.Payload is null ||
                facts.State.ActiveAttemptId is not null)
                return ProductionOutboxRejected(work, "ProductionOutboxHandlerBlockStateInvalid");
            var recoveries = ReadProductionOutboxRecoveryRows(database, options, recovery, deadline);
            var corrections = ReadProductionOutboxCorrectionRows(database, options, recovery, deadline);
            if (request.ExpectedStateRevisionHash != ProductionOutboxStateRevision(delivery,
                facts.State.LastEventContentHash, recoveries, corrections))
                return ProductionOutboxRejected(work, "ProductionOutboxStateChanged");
            var reserve = ReadProductionOutboxReserveRows(database, deadline).Events;
            var future = checked(reserve - RemainingProductionOutboxReserve(delivery, facts.Events, facts.Grants));
            EnsureProductionOutboxEventCapacity(database, options, future, deadline);
            var bindings = ProductionOutboxBindings(NextProductionOutboxEventPosition(database, deadline),
                facts.Stored, options.RouteSetHash, facts.Events[^1].AggregateSequence + 1,
                OutboxEventKind.HandlerBlocked, null, null, null, request.RecordedAtUtc, request.ReasonCode,
                OutboxFailureCategory.Permanent, null, null);
            var audit = AuditChainDatabase.AppendProductionOutboxEvent(database, _policy!, _signingKey!,
                OutboxEventKind.HandlerBlocked, ProductionOutboxStorageCodec.EncodeAuditBinding(bindings),
                options, future, deadline);
            var persisted = PersistedProductionOutboxEvent(bindings, null, audit.Sequence, audit.Hash);
            InsertProductionOutboxEventRow(database, persisted, null, deadline);
            UpsertProductionOutboxWork(database, delivery, facts.Events.Append(persisted).ToArray(), deadline);
            AuditChainDatabase.Require(ReadProductionOutboxReserveRows(database, deadline).Events == future,
                "ProductionOutboxHandlerBlockReserveMismatch");
            var backlog = ReadProductionOutboxBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            PublishProductionOutboxIntegrity(_policy!, audit.Sequence);
            work.Completion.TrySetResult(new(true, "ProductionOutboxHandlerBlocked", persisted, backlog));
            return new(true, "ProductionOutboxHandlerBlocked");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return ProductionOutboxRejected(work, SqliteAuditIntegrityQuery.FaultReason(error,
                "ProductionOutboxHandlerBlockFailed"));
        }
        finally { if (transactionStarted && !committed) Rollback(database); }
    }
}
