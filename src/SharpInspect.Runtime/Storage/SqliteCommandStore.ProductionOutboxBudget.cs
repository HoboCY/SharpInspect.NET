using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private sealed record OutboxBudgetFact(long Sequence, OutboxEventKind? Kind, int? AttemptNumber,
        Guid? AttemptId, Guid? Epoch, bool Permanent, int GrantedAttempts = 0);

    private sealed class OutboxBudgetState
    {
        internal int AttemptCount;
        internal int Limit;
        internal bool Permanent;
        internal bool Succeeded;
        internal Guid? ActiveAttempt;
        internal Guid? ActiveEpoch;
        internal bool RetryEligible => !Succeeded && !Permanent && (ActiveAttempt.HasValue || AttemptCount < Limit);
        internal long Remaining => Succeeded || Permanent ? 0 :
            checked(2L * Math.Max(0, Limit - AttemptCount) + (ActiveAttempt.HasValue ? 1 : 0));
    }

    private static OutboxBudgetState ReplayOutboxBudget(int maximumAttempts, bool hasPayload,
        IEnumerable<OutboxBudgetFact> facts)
    {
        var state = new OutboxBudgetState { Limit = maximumAttempts, Permanent = !hasPayload };
        var created = false;
        var totalGrants = 0;
        long sequence = 0;
        Guid? latestAttempt = null, latestEpoch = null;
        foreach (var fact in facts.OrderBy(x => x.Sequence))
        {
            AuditChainDatabase.Require(fact.Sequence > sequence, "ProductionOutboxBudgetOrderInvalid");
            sequence = fact.Sequence;
            if (fact.GrantedAttempts > 0)
            {
                totalGrants = checked(totalGrants + fact.GrantedAttempts);
                AuditChainDatabase.Require(created && hasPayload && !state.Succeeded && state.ActiveAttempt is null &&
                    (state.Permanent || !state.RetryEligible) && fact.GrantedAttempts <= 16 &&
                    maximumAttempts + totalGrants <= ProductionOutboxStoreOptions.MaximumAttemptsHardLimit,
                    "ProductionOutboxRecoveryStateInvalid");
                state.Limit = checked(state.AttemptCount + fact.GrantedAttempts);
                state.Permanent = false;
                latestAttempt = latestEpoch = null;
                continue;
            }
            switch (fact.Kind)
            {
                case OutboxEventKind.Created:
                    AuditChainDatabase.Require(!created, "ProductionOutboxCreatedBindingMismatch");
                    created = true;
                    break;
                case OutboxEventKind.AttemptStarted:
                    AuditChainDatabase.Require(created && hasPayload && state.RetryEligible &&
                        state.ActiveAttempt is null && fact.AttemptId is not null && fact.Epoch is not null &&
                        fact.AttemptNumber == state.AttemptCount + 1 && fact.AttemptNumber <= state.Limit,
                        "ProductionOutboxAttemptBindingMismatch");
                    state.AttemptCount = fact.AttemptNumber!.Value;
                    latestAttempt = state.ActiveAttempt = fact.AttemptId;
                    latestEpoch = state.ActiveEpoch = fact.Epoch;
                    break;
                case OutboxEventKind.HandlerBlocked:
                    AuditChainDatabase.Require(created && hasPayload && !state.Succeeded &&
                        state.ActiveAttempt is null && fact.AttemptId is null && fact.AttemptNumber is null &&
                        fact.Epoch is null && fact.Permanent, "ProductionOutboxHandlerBlockInvalid");
                    state.Permanent = true;
                    latestAttempt = latestEpoch = null;
                    break;
                case OutboxEventKind.AttemptFailed:
                case OutboxEventKind.Succeeded:
                    AuditChainDatabase.Require(created && !state.Succeeded && latestAttempt is not null &&
                        fact.AttemptId == latestAttempt && fact.Epoch == latestEpoch,
                        "ProductionOutboxAttemptBindingMismatch");
                    state.ActiveAttempt = state.ActiveEpoch = null;
                    if (fact.Kind == OutboxEventKind.Succeeded) { state.Succeeded = true; state.Permanent = false; }
                    else state.Permanent |= fact.Permanent;
                    break;
                default: throw new InvalidOperationException("ProductionOutboxBudgetFactInvalid");
            }
        }
        return state;
    }

    private static OutboxBudgetState ReplayOutboxBudget(OutboxDelivery delivery,
        IReadOnlyList<ProductionOutboxEvent> events, IReadOnlyList<ProductionOutboxRecoveryGrant>? grants)
    {
        var facts = events.Select(x => new OutboxBudgetFact(x.AuditSequence, x.Kind, x.AttemptNumber,
            x.AttemptId, x.RuntimeEpoch, x.FailureCategory == OutboxFailureCategory.Permanent));
        if (grants is not null)
            facts = facts.Concat(grants.Where(x => x.DeliveryId == delivery.DeliveryId).Select(x =>
                new OutboxBudgetFact(x.AuthorizationAuditSequence, null, null, null, null, false, x.GrantedAttempts)));
        return ReplayOutboxBudget(delivery.MaximumAttempts, delivery.Payload is not null, facts);
    }

    // Shared audit writers have no caller-supplied Outbox options. Read only the bounded budget
    // fields here and feed the same replay used by authoritative mutations and cold projections.
    private static long ReadRecoveredOutboxReserve(sqlite3 database, StoreDeadline deadline)
    {
        var bounds = ProductionOutboxStoreOptions.MaximumEventsHardLimit + 1;
        var deliveries = AuditChainDatabase.Read(database,
            "SELECT DeliveryId,MaximumAttempts,PayloadBase64 IS NOT NULL FROM production_outbox_deliveries LIMIT ?;",
            deadline, s => (Id: ParseProductionOutboxGuid(SqliteNative.ColumnText(s, 0)),
                Budget: checked((int)SqliteNative.ColumnInt64(s, 1)), HasPayload: SqliteNative.ColumnInt64(s, 2) == 1),
            ProductionOutboxNumber(bounds));
        var events = AuditChainDatabase.Read(database, @"SELECT DeliveryId,AuditSequence,Kind,AttemptNumber,
            AttemptId,RuntimeEpoch,FailureCategory FROM production_outbox_events LIMIT ?;", deadline, s =>
            (Id: ParseProductionOutboxGuid(SqliteNative.ColumnText(s, 0)), Fact: new OutboxBudgetFact(
                SqliteNative.ColumnInt64(s, 1), ParseProductionOutboxKind(SqliteNative.ColumnText(s, 2)),
                SqliteNative.ColumnInt64Nullable(s, 3) is { } number ? checked((int)number) : null,
                SqliteNative.ColumnText(s, 4) is { } attempt ? ParseProductionOutboxGuid(attempt) : null,
                SqliteNative.ColumnText(s, 5) is { } epoch ? ParseProductionOutboxGuid(epoch) : null,
                SqliteNative.ColumnText(s, 6) == nameof(OutboxFailureCategory.Permanent))), ProductionOutboxNumber(bounds));
        var grants = AuditChainDatabase.Read(database, @"SELECT DeliveryId,AuthorizationAuditSequence,GrantedAttempts
            FROM production_outbox_recovery LIMIT 257;", deadline, s =>
            (Id: ParseProductionOutboxGuid(SqliteNative.ColumnText(s, 0)), Fact: new OutboxBudgetFact(
                SqliteNative.ColumnInt64(s, 1), null, null, null, null, false, checked((int)SqliteNative.ColumnInt64(s, 2)))));
        AuditChainDatabase.Require(deliveries.Count < bounds && events.Count < bounds && grants.Count <= 256,
            "ProductionOutboxReserveCapacityExceeded");
        var byId = events.Concat(grants).ToLookup(x => x.Id, x => x.Fact);
        return deliveries.Sum(x => ReplayOutboxBudget(x.Budget, x.HasPayload, byId[x.Id]).Remaining);
    }
}
