using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Canonical IPv4 deployment settings. These are never Recipe process settings.</summary>
public sealed record CameraIpv4Configuration
{
    public CameraIpv4Configuration(string address, int prefixLength, string? gateway = null)
    {
        var numeric = ParseAddress(address, nameof(address));
        if (prefixLength is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "CameraNetworkPrefixInvalid");
        var mask = uint.MaxValue << (32 - prefixLength);
        if ((numeric & ~mask) == 0 || (numeric & ~mask) == ~mask)
            throw new ArgumentException("CameraNetworkHostAddressInvalid", nameof(address));
        if (gateway is not null)
        {
            var gatewayValue = ParseAddress(gateway, nameof(gateway));
            if ((gatewayValue & mask) != (numeric & mask) || gatewayValue == numeric ||
                (gatewayValue & ~mask) == 0 || (gatewayValue & ~mask) == ~mask)
                throw new ArgumentException("CameraNetworkGatewayInvalid", nameof(gateway));
        }
        Address = address;
        PrefixLength = prefixLength;
        Gateway = gateway;
    }

    public string Address { get; }
    public int PrefixLength { get; }
    public string? Gateway { get; }

    public bool IsInSameSubnet(CameraIpv4Configuration other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (PrefixLength != other.PrefixLength) return false;
        var mask = uint.MaxValue << (32 - PrefixLength);
        return (ParseAddress(Address, nameof(Address)) & mask) ==
            (ParseAddress(other.Address, nameof(other)) & mask);
    }

    private static uint ParseAddress(string value, string name)
    {
        if (value is null) throw new ArgumentNullException(name);
        if (value.Length is < 7 or > 15)
            throw new ArgumentException("CameraNetworkAddressInvalid", name);
        var parts = value.Split('.');
        if (parts.Length != 4) throw new ArgumentException("CameraNetworkAddressInvalid", name);
        uint numeric = 0;
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3 || (part.Length > 1 && part[0] == '0') ||
                part.Any(c => c is < '0' or > '9') ||
                !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet))
                throw new ArgumentException("CameraNetworkAddressInvalid", name);
            numeric = (numeric << 8) | octet;
        }
        if ((numeric >> 24) is 0 or 127 or >= 224)
            throw new ArgumentException("CameraNetworkUnicastRequired", name);
        return numeric;
    }
}

/// <summary>Host-declared station interface. A maintenance request cannot choose a different network.</summary>
public sealed record CameraStationNetwork
{
    public CameraStationNetwork(string interfaceId, CameraIpv4Configuration configuration)
    {
        InterfaceId = CameraContractValidation.Identifier(interfaceId, nameof(interfaceId), 128);
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }
    public string InterfaceId { get; }
    public CameraIpv4Configuration Configuration { get; }
}

public sealed record CameraNetworkDeviceState(CameraBindingTarget Target,
    CameraIpv4Configuration Configuration);

public sealed record CameraNetworkReadResult(bool Succeeded, string ReasonCode,
    CameraNetworkDeviceState? State = null);

public enum CameraNetworkConflictState { Clear, Conflict, Unknown }

/// <summary>Provider-observable address conflicts, scoped to the declared station interface.</summary>
public sealed record CameraNetworkConflictResult(CameraNetworkConflictState State, string ReasonCode);

public sealed record CameraNetworkApplyResult(bool Succeeded, string ReasonCode);

/// <summary>
/// Optional provider maintenance extension. The core ICameraProvider contract has no IP mutation.
/// A successful lease excludes Open for this device until actual Dispose completion.
/// Begin must reject an already opened device, and must never close it implicitly.
/// </summary>
public interface ICameraNetworkConfigurator
{
    ValueTask<CameraNetworkMaintenanceLeaseResult> TryBeginMaintenanceAsync(string stableDeviceIdentity,
        CancellationToken cancellationToken = default);
}

public sealed class CameraNetworkMaintenanceLeaseResult
{
    private CameraNetworkMaintenanceLeaseResult(ICameraNetworkMaintenanceSession? session, string reasonCode)
    { Session = session; ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode)); }
    public ICameraNetworkMaintenanceSession? Session { get; }
    public bool Succeeded => Session is not null;
    public string ReasonCode { get; }
    public static CameraNetworkMaintenanceLeaseResult Success(ICameraNetworkMaintenanceSession session) =>
        new(session ?? throw new ArgumentNullException(nameof(session)), "CameraNetworkMaintenanceReserved");
    public static CameraNetworkMaintenanceLeaseResult Failure(string reasonCode) => new(null, reasonCode);
}

/// <summary>
/// Exclusive ownership of one closed device. Discovery remains read-only while the lease is held.
/// Apply compares the complete previous settings before changing anything; it never silently repairs
/// another device or address. Cancellation is not proof that a physical operation has ended.
/// </summary>
public interface ICameraNetworkMaintenanceSession : IAsyncDisposable
{
    CameraBindingTarget Target { get; }
    ValueTask<CameraNetworkReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
    ValueTask<CameraNetworkConflictResult> DetectConflictAsync(CameraIpv4Configuration requested,
        CameraStationNetwork stationNetwork, CancellationToken cancellationToken = default);
    ValueTask<CameraNetworkApplyResult> ApplyAsync(CameraIpv4Configuration expectedPrevious,
        CameraIpv4Configuration requested, CancellationToken cancellationToken = default);
}

public sealed record CameraNetworkChangeRequest(Guid OperationId, CommandInvocation Invocation,
    CameraBindingTarget Target, CameraIpv4Configuration Requested, string ChangeReason)
{
    /// <summary>Exact provider/device binding for the one-time ManageCameraBindings Step-Up grant.</summary>
    public string AuthorizationTargetId => Target.ContentHash;
}

public enum CameraNetworkMaintenanceState { Pending, Succeeded, Failed, Unknown }

/// <summary>Protected maintenance evidence. Current connectivity and production authority are separate facts.</summary>
public sealed record CameraNetworkSnapshot(Guid OperationId, CameraBindingTarget Target,
    CameraNetworkMaintenanceState State, CameraIpv4Configuration? Previous,
    CameraIpv4Configuration Requested, CameraIpv4Configuration? Observed,
    bool IdentityVerified, string ReasonCode, DateTimeOffset RecordedAtUtc)
{
    public bool RequiresRecipeActivation => true;
    public bool ProductionReady => false;
}

public sealed record CameraNetworkOperationResult(bool Succeeded, string ReasonCode,
    AuditPersistence Audit, CameraNetworkSnapshot? Snapshot = null);

public sealed record CameraNetworkQueryResult(bool Available, string ReasonCode,
    CameraNetworkSnapshot? Snapshot = null);

/// <summary>Authorized maintenance only; neither provider handles nor production arming are exposed.</summary>
public interface ICameraNetworkMaintenanceRuntime
{
    ValueTask<CameraNetworkOperationResult> ChangeNetworkConfigurationAsync(CameraNetworkChangeRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<CameraNetworkQueryResult> GetNetworkMaintenanceAsync(CameraBindingTarget target,
        CommandInvocation invocation, CancellationToken cancellationToken = default);
}
