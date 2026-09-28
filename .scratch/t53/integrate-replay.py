from pathlib import Path

path = Path('src/SharpInspect.Runtime/Storage/SqliteCommandStore.ProductionOutboxSchema.cs')
source = path.read_text(encoding='utf-8-sig')
start = source.index('    internal static ProductionOutboxWorkState DeriveProductionOutboxState(')
end = source.index('    private static void ValidateProductionOutboxProjection(', start)
source = source[:start] + '''    internal static ProductionOutboxWorkState DeriveProductionOutboxState(
        OutboxDelivery delivery, IReadOnlyList<ProductionOutboxEvent> events,
        IReadOnlyList<ProductionOutboxRecoveryGrant>? grants = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(events);
        var budget = ReplayOutboxBudget(delivery, events, grants);
        var failure = events.LastOrDefault(x => x.Kind == OutboxEventKind.AttemptFailed);
        var last = events.LastOrDefault();
        var latestGrant = grants?.Where(x => x.DeliveryId == delivery.DeliveryId)
            .OrderBy(x => x.AuthorizationAuditSequence).LastOrDefault();
        var state = budget.Succeeded ? OutboxDeliveryState.Succeeded : budget.ActiveAttempt.HasValue
            ? OutboxDeliveryState.Pending : failure is not null || delivery.Payload is null
                ? OutboxDeliveryState.Failed : OutboxDeliveryState.Pending;
        var retryAfter = latestGrant is not null && (failure is null || latestGrant.AuthorizationAuditSequence > failure.AuditSequence)
            ? null : failure?.RetryAfterUtc;
        return new ProductionOutboxWorkState(delivery, state, budget.AttemptCount,
            checked(budget.AttemptCount + 1), budget.RetryEligible, budget.Permanent, budget.ActiveAttempt,
            budget.ActiveEpoch, failure?.ReasonCode ?? (delivery.Payload is null ?
                events.FirstOrDefault()?.ReasonCode ?? "ProductionOutboxPreparationFailed" : null),
            failure?.FailureCategory ?? (delivery.Payload is null ? OutboxFailureCategory.Permanent : null),
            retryAfter, last?.Position ?? 0, last?.ContentHash);
    }

''' + source[end:]
start = source.index('        var events = recovery\n')
end = source.index('            : AuditChainDatabase.Scalar(database, @"', start)
source = source[:start] + '        var events = recovery\n            ? ReadRecoveredOutboxReserve(database, deadline)\n' + source[end:]
path.write_text(source, encoding='utf-8', newline='\n')
