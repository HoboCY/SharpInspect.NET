using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;

namespace SharpInspect.Runtime.Outbox;

internal static class ProductionOutboxBinding
{
    internal static OutboxBacklogSnapshot CompleteBacklog(ProductionOutboxStoreOptions options,
        OutboxBacklogSnapshot verified)
    {
        if (verified.Routes.Select(value => value.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != verified.Routes.Count ||
            verified.Routes.Any(row => !options.Routes.Any(route => row.RouteId == route.RouteId &&
                row.RouteHash == route.ContentHash && row.Criticality == route.Criticality)))
            throw new InvalidOperationException("OutboxBacklogRouteBindingMismatch");
        return new(verified.ThroughAuditSequence, options.Routes.Select(route =>
            verified.Routes.SingleOrDefault(value => value.RouteId == route.RouteId) ??
                new OutboxRouteBacklog(route.RouteId, route.ContentHash, route.Criticality, 0, 0, null, false)).ToArray());
    }

    internal static bool RoutesMatch(ProductionStoreOptions? store, TraceStoragePolicySnapshot? policy)
    {
        if (policy is null || store?.TraceStoragePolicies?.DeploymentScope is not { } scope ||
            !TraceStoragePolicyValidator.RoutesMatch(policy.Policy, scope)) return false;
        if (store.Outbox is not { } outbox) return policy.Policy.RequiredRoutes.Count == 0 && scope.RequiredRoutes.Count == 0;
        var required = outbox.Routes.Where(route => route.Criticality == OutboxRouteCriticality.Required).ToArray();
        return required.Length == policy.Policy.RequiredRoutes.Count &&
            required.All(route => policy.Policy.RequiredRoutes.Any(limit => limit.RouteId == route.RouteId &&
                limit.ContractVersion == route.Version && limit.ContractHash == route.ContentHash)) &&
            outbox.Routes.All(route => OutboxValidation.SameContract(route.PayloadContract, OutboxReceiverProtocol.CorePayloadContract) &&
                OutboxValidation.SameContract(route.ReceiverContract, OutboxReceiverProtocol.ReceiverContract) &&
                route.ContentType == OutboxReceiverProtocol.ContentType);
    }

    internal static string? RequiredBacklogFailure(TraceStoragePolicySnapshot policy, OutboxBacklogSnapshot backlog,
        DateTimeOffset now)
    {
        foreach (var limit in policy.Policy.RequiredRoutes)
        {
            var route = backlog.Routes.SingleOrDefault(value => value.RouteId == limit.RouteId &&
                value.RouteHash == limit.ContractHash && value.Criticality == OutboxRouteCriticality.Required);
            if (route is null) return "OutboxRequiredBacklogUnobserved";
            if (route.PermanentBlock) return "OutboxRequiredPermanentBlock";
            if (route.PendingCount >= limit.Limits.MaximumItems || route.PendingBytes >= limit.Limits.MaximumBytes ||
                route.OldestCreatedAtUtc is { } oldest && now - oldest >= limit.Limits.MaximumOldestAge)
                return "OutboxRequiredBacklogLimitExceeded";
        }
        return null;
    }
}
