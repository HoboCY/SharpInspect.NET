namespace SharpInspect.Abstractions;

/// <summary>Deployment identity only; contains no camera process settings.</summary>
public sealed record CameraBindingTarget
{
    public CameraBindingTarget(CameraProviderIdentity provider, string stableDeviceIdentity)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        StableDeviceIdentity = CameraContractValidation.Identifier(stableDeviceIdentity,
            nameof(stableDeviceIdentity), 128);
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-camera-binding-target-v1",
            provider.Id, provider.Version, provider.AdapterPackageId, provider.AdapterVersion, StableDeviceIdentity });
    }
    public CameraProviderIdentity Provider { get; }
    public string StableDeviceIdentity { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Explicit dependency on provider-owned typed configuration. The common contract
/// never embeds feature dictionaries or vendor SDK values. An unavailable exact
/// extension contract must be rejected, never dropped.
/// </summary>
public sealed record CameraProviderExtensionRequirement
{
    public CameraProviderExtensionRequirement(CameraProviderIdentity provider,
        string contractId, string contractVersion, string configurationContentHash)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ContractId = CameraContractValidation.Identifier(contractId, nameof(contractId), 128);
        ContractVersion = CameraContractValidation.Identifier(contractVersion, nameof(contractVersion), 128);
        ConfigurationContentHash = CameraSetupValidation.Hash(configurationContentHash,
            nameof(configurationContentHash));
    }
    public CameraProviderIdentity Provider { get; }
    public string ContractId { get; }
    public string ContractVersion { get; }
    public string ConfigurationContentHash { get; }
    public bool IsPortable => false;
}

/// <summary>A durable binding revision. Returned identity is restricted to authorized setup queries.</summary>
public sealed record CameraBindingRevision(long Position, string LogicalRole, long Revision,
    Guid OperationId, string? PreviousRevisionHash, string RevisionHash, CameraBindingTarget Target,
    Guid AuthorPrincipalId, Guid AuthorSessionId, long AuthorAuthorizationRevision,
    string ChangeReason, DateTimeOffset RecordedAtUtc);

/// <summary>Safe projection for the ordinary station snapshot. No device serial or network identity.</summary>
public sealed record CameraSetupState(string LogicalRole, long BindingRevision,
    CameraProviderAvailability ProviderAvailability, CameraConnectionState Connection,
    CameraConfigurationState Configuration, CameraAcquisitionState Acquisition,
    HealthState Buffers, CameraFaultClassification? FaultClassification, string? FaultReasonCode,
    bool RequiresRecipeActivation = true);

/// <summary>Immutable, authorized setup view; historical read-back is not current production authority.</summary>
public sealed class CameraSetupSnapshot
{
    public CameraSetupSnapshot(string logicalRole, CameraBindingRevision? binding,
        CameraHealthSnapshot health, RequestedCameraConfiguration? requested = null,
        EffectiveCameraConfiguration? effective = null,
        IEnumerable<CameraConfigurationDifference>? differences = null,
        CameraProviderExtensionRequirement? extension = null,
        string reasonCode = "CameraSetupUnconfigured", CameraCapabilities? capabilities = null)
    {
        LogicalRole = FrameMetadataValidation.Identifier(logicalRole, nameof(logicalRole));
        if (binding is not null && binding.LogicalRole != LogicalRole)
            throw new ArgumentException("CameraSetupBindingRoleMismatch", nameof(binding));
        Binding = binding;
        Health = health ?? throw new ArgumentNullException(nameof(health));
        Requested = requested;
        Effective = effective;
        Differences = CameraContractValidation.Copy(differences, nameof(differences), 6);
        if (effective is null && Differences.Count != 0)
            throw new ArgumentException("CameraSetupDifferencesWithoutEffective", nameof(differences));
        if (effective is not null && requested is null)
            throw new ArgumentException("CameraSetupEffectiveWithoutRequested", nameof(effective));
        Extension = extension;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        Capabilities = capabilities;
    }
    public string LogicalRole { get; }
    public CameraBindingRevision? Binding { get; }
    public CameraHealthSnapshot Health { get; }
    public RequestedCameraConfiguration? Requested { get; }
    public EffectiveCameraConfiguration? Effective { get; }
    public IReadOnlyList<CameraConfigurationDifference> Differences { get; }
    public CameraProviderExtensionRequirement? Extension { get; }
    public CameraCapabilities? Capabilities { get; }
    public string ReasonCode { get; }
    public bool IsPortable => Extension is null;
    public bool RequiresRecipeActivation => true;
    public bool ProductionReady => false;
}

public sealed record CameraSetupQueryResult(bool Available, string ReasonCode, CameraSetupSnapshot? Snapshot = null);

/// <summary>Final operation result. A persisted setup success grants neither Active nor Ready.</summary>
public sealed record CameraSetupOperationResult(bool Succeeded, string ReasonCode,
    AuditPersistence Audit, CameraSetupSnapshot? Snapshot = null);

public sealed record CameraRebindRequest(Guid OperationId, CommandInvocation Invocation,
    string LogicalRole, long ExpectedBindingRevision, string? ExpectedBindingRevisionHash,
    CameraBindingTarget Target, string ChangeReason);

public sealed record CameraDebugConfigurationRequest(Guid OperationId, CommandInvocation Invocation,
    string LogicalRole, long ExpectedBindingRevision, string? ExpectedBindingRevisionHash,
    RequestedCameraConfiguration Requested, string ChangeReason,
    CameraProviderExtensionRequirement? Extension = null);

/// <summary>
/// Restricted non-production capability implemented by the station Runtime.
/// Presentation code receives no provider, device, store, or production-arming capability here.
/// </summary>
public interface ICameraSetupRuntime
{
    IReadOnlyList<CameraProviderIdentity> Providers { get; }
    ValueTask<CameraDiscoveryResult> DiscoverAsync(CameraProviderIdentity provider,
        CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole,
        CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<CameraSetupOperationResult> RebindAsync(CameraRebindRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(CameraDebugConfigurationRequest request,
        CancellationToken cancellationToken = default);
}

internal static class CameraSetupValidation
{
    internal static string Hash(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')))
            throw new ArgumentException("CameraSetupHashInvalid", parameterName);
        return value;
    }
}
