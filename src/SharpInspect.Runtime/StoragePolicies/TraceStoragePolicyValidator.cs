using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.StoragePolicies;

internal static class TraceStoragePolicyValidator
{
    internal static long RequiredReserve(TraceStoragePolicyDefinition policy, long totalBytes)
    {
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes), "TraceStorageVolumeCapacityInvalid");
        var proportional = decimal.Ceiling(totalBytes * policy.MinimumReservePercent / 100m);
        return Math.Max(policy.MinimumReserveBytes, checked((long)proportional));
    }

    internal static IReadOnlyList<string> Validate(TraceStoragePolicyDefinition policy,
        TraceStorageDeploymentScope? scope, long? totalBytes)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var errors = new List<string>();
        if (scope is null) errors.Add("TraceStorageRequiredRouteInventoryMissing");
        else if (!RoutesMatch(policy, scope)) errors.Add("TraceStorageRequiredRouteSetMismatch");
        if (totalBytes is not > 0) errors.Add("TraceStorageVolumeCapacityUnavailable");
        else
        {
            var reserve = RequiredReserve(policy, totalBytes.Value);
            if (reserve >= totalBytes.Value) errors.Add("TraceStorageReservePhysicallyImpossible");
            else
            {
                var usable = totalBytes.Value - reserve;
                if (policy.MaximumWalBytes > usable) errors.Add("TraceStorageWalCapacityPhysicallyImpossible");
                if (policy.ImageBacklog.MaximumBytes > usable) errors.Add("TraceStorageImageBacklogPhysicallyImpossible");
                if (policy.RequiredRoutes.Any(route => route.Limits.MaximumBytes > usable))
                    errors.Add("TraceStorageRouteBacklogPhysicallyImpossible");
                if (policy.Scrubber.MaximumBytes > totalBytes.Value || policy.Checkpoint.MaximumBytes > totalBytes.Value)
                    errors.Add("TraceStorageMaintenanceBudgetPhysicallyImpossible");
            }
        }
        return errors.AsReadOnly();
    }

    internal static bool RoutesMatch(TraceStoragePolicyDefinition policy, TraceStorageDeploymentScope scope) =>
        policy.RequiredRoutes.Count == scope.RequiredRoutes.Count && policy.RequiredRoutes.All(route =>
            scope.RequiredRoutes.Any(expected => expected.RouteId == route.RouteId &&
                expected.ContractVersion == route.ContractVersion && expected.ContractHash == route.ContractHash));
}
