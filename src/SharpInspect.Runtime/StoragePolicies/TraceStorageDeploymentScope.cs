using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.StoragePolicies;

/// <summary>An explicit host deployment inventory. Empty routes are deliberate; null is unknown.</summary>
public sealed class TraceStorageDeploymentScope
{
    public TraceStorageDeploymentScope(string id, string version, IEnumerable<TraceStorageRouteIdentity> requiredRoutes)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        var copied = AlgorithmContractValidation.Copy(requiredRoutes, nameof(requiredRoutes), 64)
            .OrderBy(value => value.RouteId, StringComparer.Ordinal).ToArray();
        if (copied.Select(value => value.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copied.Length)
            throw new ArgumentException("TraceStorageDeploymentRouteDuplicate", nameof(requiredRoutes));
        RequiredRoutes = Array.AsReadOnly(copied);
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-trace-storage-deployment-v1", Id, Version }
            .Concat(copied.SelectMany(value => new[] { value.RouteId, value.ContractVersion, value.ContractHash })));
    }

    public string Id { get; }
    public string Version { get; }
    public ReadOnlyCollection<TraceStorageRouteIdentity> RequiredRoutes { get; }
    public string ContentHash { get; }
}

public sealed class TraceStorageRouteIdentity
{
    public TraceStorageRouteIdentity(string routeId, string contractVersion, string contractHash)
    {
        RouteId = AlgorithmConfigurationValidation.Identifier(routeId, nameof(routeId));
        ContractVersion = AlgorithmConfigurationValidation.Identifier(contractVersion, nameof(contractVersion));
        ContractHash = AlgorithmConfigurationValidation.Hash(contractHash, nameof(contractHash)).ToUpperInvariant();
    }
    public string RouteId { get; }
    public string ContractVersion { get; }
    public string ContractHash { get; }
}
