using System.Collections.ObjectModel;
using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Outbox;

/// <summary>
/// Explicit endpoint adapter registration. The caller manages its endpoint/credentials inside
/// the transport; only their configuration identity/version/hash are recorded with attempts.
/// Changing these connection details cannot change the logical receiver or frozen payload.
/// </summary>
public sealed class OutboxTransportBinding
{
    private readonly byte[]? _capability;
    public OutboxTransportBinding(OutboxRouteDefinition route, IOutboxRouteTransport transport,
        string connectionId, string connectionVersion, string connectionConfigurationHash,
        byte[]? receiverCapability = null)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ConnectionId = OutboxValidation.Identifier(connectionId);
        ConnectionVersion = OutboxValidation.Identifier(connectionVersion);
        ConnectionConfigurationHash = OutboxValidation.Hash(connectionConfigurationHash);
        if (!OutboxValidation.SameContract(transport.Contract, route.AdapterContract))
            throw new ArgumentException("OutboxAdapterContractMismatch", nameof(transport));
        if (!OutboxValidation.SameContract(route.PayloadContract, OutboxReceiverProtocol.CorePayloadContract) ||
            !OutboxValidation.SameContract(route.ReceiverContract, OutboxReceiverProtocol.ReceiverContract) ||
            route.ContentType != OutboxReceiverProtocol.ContentType)
            throw new ArgumentException("OutboxRouteContractUnsupported", nameof(route));
        if (receiverCapability is not null)
        {
            _capability = (byte[])receiverCapability.Clone();
            OutboxReceiverProtocol.VerifyCapability(route, _capability);
            CapabilityHash = Convert.ToHexString(SHA256.HashData(_capability));
        }
        if (route.Criticality == OutboxRouteCriticality.Required && _capability is null)
            throw new ArgumentException("OutboxRequiredReceiverCapabilityMissing", nameof(receiverCapability));
        ConnectionBindingHash = OutboxValidation.HashParts("sharpinspect-outbox-connection-v1",
            route.ContentHash, ConnectionId, ConnectionVersion, ConnectionConfigurationHash);
        ContentHash = OutboxValidation.HashParts("sharpinspect-outbox-transport-binding-v1",
            ConnectionBindingHash, CapabilityHash);
    }
    public OutboxRouteDefinition Route { get; }
    public IOutboxRouteTransport Transport { get; }
    public string ConnectionId { get; }
    public string ConnectionVersion { get; }
    public string ConnectionConfigurationHash { get; }
    public string ConnectionBindingHash { get; }
    public string? CapabilityHash { get; }
    public string ContentHash { get; }
    internal void RequireCurrentContract()
    {
        if (!OutboxValidation.SameContract(Transport.Contract, Route.AdapterContract))
            throw new InvalidOperationException("OutboxAdapterContractMismatch");
    }
}

/// <summary>An explicit finite transport registry. An empty registry starts no delivery work.</summary>
public sealed class ProductionOutboxOptions
{
    public ProductionOutboxOptions(IEnumerable<OutboxTransportBinding> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        var copied = transports.Take(65).ToArray();
        if (copied.Length > 64 || copied.Any(value => value is null) ||
            copied.Select(value => value.Route.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copied.Length)
            throw new ArgumentException("OutboxTransportRegistryInvalid", nameof(transports));
        if (copied.Select(value => value.Transport).Distinct(ReferenceEqualityComparer.Instance).Count() != copied.Length)
            throw new ArgumentException("OutboxTransportInstanceSharedAcrossRoutes", nameof(transports));
        Transports = Array.AsReadOnly(copied.OrderBy(value => value.Route.RouteId, StringComparer.Ordinal).ToArray());
        ContentHash = OutboxValidation.HashParts(new[] { "sharpinspect-outbox-transports-v1" }
            .Concat(Transports.Select(value => value.ContentHash)).ToArray());
    }
    public ReadOnlyCollection<OutboxTransportBinding> Transports { get; }
    public string ContentHash { get; }
    internal OutboxTransportBinding? Resolve(OutboxRouteDefinition route) => Transports.SingleOrDefault(value =>
        value.Route.RouteId == route.RouteId && value.Route.ContentHash == route.ContentHash);
}
