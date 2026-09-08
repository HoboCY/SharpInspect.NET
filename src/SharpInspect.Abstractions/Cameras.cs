using System.Collections.ObjectModel;
using System.Text;

namespace SharpInspect.Abstractions;

/// <summary>Vendor-neutral identity of one camera provider package.</summary>
public sealed record CameraProviderIdentity
{
    public CameraProviderIdentity(string id, string version, string adapterPackageId,
        string adapterVersion)
    {
        Id = FrameMetadataValidation.Identifier(id, nameof(id));
        Version = CameraContractValidation.Identifier(version, nameof(version), 128);
        AdapterPackageId = FrameMetadataValidation.Identifier(adapterPackageId,
            nameof(adapterPackageId));
        AdapterVersion = CameraContractValidation.Identifier(adapterVersion,
            nameof(adapterVersion), 128);
    }

    public string Id { get; }
    public string Version { get; }
    public string AdapterPackageId { get; }
    public string AdapterVersion { get; }
}

/// <summary>Stable provider-owned identity for one discovered camera.</summary>
public sealed record CameraDeviceDescriptor
{
    public CameraDeviceDescriptor(CameraProviderIdentity provider, string stableDeviceIdentity,
        string displayName, string? reportedModel = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        StableDeviceIdentity = CameraContractValidation.Identifier(stableDeviceIdentity,
            nameof(stableDeviceIdentity), 128);
        DisplayName = CameraContractValidation.Text(displayName, nameof(displayName), 256);
        ReportedModel = reportedModel is null ? null :
            CameraContractValidation.Text(reportedModel, nameof(reportedModel), 128);
    }

    public CameraProviderIdentity Provider { get; }
    public string StableDeviceIdentity { get; }
    public string DisplayName { get; }
    public string? ReportedModel { get; }
}

/// <summary>Exactly one logical frame request for one inspection correlation.</summary>
public sealed record FrameAcquisitionRequest
{
    public FrameAcquisitionRequest(ExecutionCorrelationId correlation, string logicalCameraRole)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        Correlation = correlation;
        LogicalCameraRole = FrameMetadataValidation.Identifier(logicalCameraRole,
            nameof(logicalCameraRole));
    }

    public ExecutionCorrelationId Correlation { get; }
    public string LogicalCameraRole { get; }
}

public enum CameraProviderAvailability
{
    Available,
    DependencyMissing,
    Incompatible,
    Faulted
}

public enum CameraConnectionState
{
    Closed,
    Opening,
    Open,
    Disconnected
}

public enum CameraConfigurationState
{
    Unconfigured,
    Applying,
    Applied,
    Unknown
}

public enum CameraAcquisitionState
{
    Stopped,
    Armed,
    WaitingForFrame,
    Previewing
}

public enum CameraFaultClassification
{
    DependencyUnavailable,
    DeviceMissing,
    ConnectionLost,
    ConfigurationRejected,
    AcquisitionFailed,
    ProtocolViolation,
    BufferFault,
    DeviceFault
}

/// <summary>Typed, redacted camera fault evidence. It carries no exception or free-form diagnostic.</summary>
public sealed record CameraFault
{
    public CameraFault(CameraFaultClassification classification, string reasonCode,
        string? diagnosticCode = null)
    {
        Classification = CameraContractValidation.Enum(classification, nameof(classification));
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        DiagnosticCode = diagnosticCode is null ? null :
            CameraContractValidation.Reason(diagnosticCode, nameof(diagnosticCode));
    }

    public CameraFaultClassification Classification { get; }
    public string ReasonCode { get; }
    public string? DiagnosticCode { get; }
}

/// <summary>Camera facts on independent provider, connection, configuration and acquisition axes.</summary>
public sealed record CameraHealthSnapshot
{
    public CameraHealthSnapshot(CameraProviderAvailability providerAvailability,
        CameraConnectionState connection, CameraConfigurationState configuration,
        CameraAcquisitionState acquisition, FrameTimePoint observedAt,
        CameraFault? lastFault = null)
    {
        ProviderAvailability = CameraContractValidation.Enum(providerAvailability,
            nameof(providerAvailability));
        Connection = CameraContractValidation.Enum(connection, nameof(connection));
        Configuration = CameraContractValidation.Enum(configuration, nameof(configuration));
        Acquisition = CameraContractValidation.Enum(acquisition, nameof(acquisition));
        ObservedAt = observedAt ?? throw new ArgumentNullException(nameof(observedAt));
        LastFault = lastFault;
        ValidateAxes();
    }

    public CameraProviderAvailability ProviderAvailability { get; }
    public CameraConnectionState Connection { get; }
    public CameraConfigurationState Configuration { get; }
    public CameraAcquisitionState Acquisition { get; }
    public FrameTimePoint ObservedAt { get; }
    public CameraFault? LastFault { get; }

    private void ValidateAxes()
    {
        var activeAcquisition = Acquisition is CameraAcquisitionState.Armed or
            CameraAcquisitionState.WaitingForFrame or CameraAcquisitionState.Previewing;

        if (Connection is CameraConnectionState.Closed or CameraConnectionState.Opening or
            CameraConnectionState.Disconnected)
        {
            if (Acquisition != CameraAcquisitionState.Stopped)
                throw new ArgumentException("CameraHealthAcquisitionConnectionMismatch");
            if (Configuration is CameraConfigurationState.Applied or CameraConfigurationState.Applying)
                throw new ArgumentException("CameraHealthConfigurationConnectionMismatch");
        }

        if (Configuration == CameraConfigurationState.Applied && Connection != CameraConnectionState.Open)
            throw new ArgumentException("CameraHealthAppliedConnectionMismatch");

        if (Configuration == CameraConfigurationState.Applying &&
            Acquisition != CameraAcquisitionState.Stopped)
            throw new ArgumentException("CameraHealthApplyingAcquisitionMismatch");

        if (activeAcquisition && (ProviderAvailability != CameraProviderAvailability.Available ||
            Connection != CameraConnectionState.Open ||
            Configuration != CameraConfigurationState.Applied))
            throw new ArgumentException("CameraHealthAcquisitionPrerequisiteMismatch");

        if (ProviderAvailability != CameraProviderAvailability.Available && activeAcquisition)
            throw new ArgumentException("CameraHealthUnavailableProviderAcquisitionMismatch");
    }
}

/// <summary>One numeric field that a camera capability explicitly quantized.</summary>
public enum CameraNumericSetting
{
    ExposureTimeUs,
    GainDb,
    TriggerDelayUs,
    WhiteBalanceRed,
    WhiteBalanceGreen,
    WhiteBalanceBlue
}

public sealed record CameraConfigurationDifference
{
    public CameraConfigurationDifference(CameraNumericSetting setting, double requested, double effective)
    {
        Setting = CameraContractValidation.Enum(setting, nameof(setting));
        if (!double.IsFinite(requested) || !double.IsFinite(effective))
            throw new ArgumentOutOfRangeException(nameof(requested));
        if (requested == effective)
            throw new ArgumentException("CameraConfigurationDifferenceUnchanged");
        Requested = requested;
        Effective = effective;
    }

    public CameraNumericSetting Setting { get; }
    public double Requested { get; }
    public double Effective { get; }
}

/// <summary>Discovery result with a bounded, defensive device list.</summary>
public sealed class CameraDiscoveryResult
{
    public CameraDiscoveryResult(bool succeeded, string reasonCode,
        IEnumerable<CameraDeviceDescriptor>? devices = null)
    {
        Succeeded = succeeded;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        var copied = CameraContractValidation.Copy(devices, nameof(devices), 64);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in copied)
        {
            if (!identities.Add(device.StableDeviceIdentity))
                throw new ArgumentException("CameraDiscoveryDuplicateDevice", nameof(devices));
        }

        if (!succeeded && copied.Count != 0)
            throw new ArgumentException("CameraDiscoveryFailureContainsDevices", nameof(devices));
        Devices = copied;
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }
    public IReadOnlyList<CameraDeviceDescriptor> Devices { get; }

    public static CameraDiscoveryResult Success(IEnumerable<CameraDeviceDescriptor> devices) =>
        new(true, "CameraDiscoverySucceeded", devices);

    public static CameraDiscoveryResult Failure(string reasonCode) =>
        new(false, reasonCode, Array.Empty<CameraDeviceDescriptor>());
}

/// <summary>Open result; a failed result never exposes a partially opened device.</summary>
public sealed class CameraOpenResult
{
    public CameraOpenResult(bool succeeded, string reasonCode, ICameraDevice? device = null)
    {
        Succeeded = succeeded;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        if (succeeded != (device is not null))
            throw new ArgumentException("CameraOpenResultStateInvalid", nameof(device));
        Device = device;
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }
    public ICameraDevice? Device { get; }

    public static CameraOpenResult Success(ICameraDevice device) =>
        new(true, "CameraOpenSucceeded", device ?? throw new ArgumentNullException(nameof(device)));

    public static CameraOpenResult Failure(string reasonCode) => new(false, reasonCode);
}

/// <summary>Atomic complete configuration result with no half-effective failure state.</summary>
public sealed class CameraConfigurationResult
{
    public CameraConfigurationResult(bool succeeded, string reasonCode,
        EffectiveCameraConfiguration? effective,
        IEnumerable<CameraConfigurationDifference>? differences = null)
    {
        Succeeded = succeeded;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        if (succeeded != (effective is not null))
            throw new ArgumentException("CameraConfigurationResultStateInvalid", nameof(effective));
        var copied = CameraContractValidation.Copy(differences, nameof(differences), 6);
        if (copied.Select(difference => difference.Setting).Distinct().Count() != copied.Count)
            throw new ArgumentException("CameraConfigurationDuplicateDifference", nameof(differences));
        if (!succeeded && copied.Count != 0)
            throw new ArgumentException("CameraConfigurationFailureContainsDifferences", nameof(differences));
        Effective = effective;
        Differences = copied;
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }
    public EffectiveCameraConfiguration? Effective { get; }
    public IReadOnlyList<CameraConfigurationDifference> Differences { get; }

    public static CameraConfigurationResult Success(EffectiveCameraConfiguration effective,
        IEnumerable<CameraConfigurationDifference>? differences = null) =>
        new(true, "CameraConfigurationApplied", effective ?? throw new ArgumentNullException(nameof(effective)),
            differences);

    public static CameraConfigurationResult Failure(string reasonCode) =>
        new(false, reasonCode, null, Array.Empty<CameraConfigurationDifference>());
}

public sealed class CameraOperationResult
{
    public CameraOperationResult(bool succeeded, string reasonCode)
    {
        Succeeded = succeeded;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }

    public static CameraOperationResult Success(string reasonCode = "CameraOperationSucceeded") =>
        new(true, reasonCode);

    public static CameraOperationResult Failure(string reasonCode) => new(false, reasonCode);
}

public enum CameraAcquisitionFailureKind
{
    Cancelled,
    TimedOut,
    Disconnected,
    NotConfigured,
    NotStarted,
    AlreadyPending,
    ProtocolViolation,
    BufferUnavailable,
    DeviceFault
}

public sealed record CameraAcquisitionFailure
{
    public CameraAcquisitionFailure(CameraAcquisitionFailureKind kind, string reasonCode)
    {
        Kind = CameraContractValidation.Enum(kind, nameof(kind));
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
    }

    public CameraAcquisitionFailureKind Kind { get; }
    public string ReasonCode { get; }
}

/// <summary>Frame acquisition result: exactly one of Lease and Failure is present.</summary>
public sealed class FrameAcquisitionResult
{
    public FrameAcquisitionResult(IFrameBufferLease? lease, CameraAcquisitionFailure? failure)
    {
        if ((lease is null) == (failure is null))
            throw new ArgumentException("CameraAcquisitionResultStateInvalid");
        Lease = lease;
        Failure = failure;
    }

    public bool Succeeded => Lease is not null;
    public string ReasonCode => Lease is not null ? "CameraFrameAcquired" : Failure!.ReasonCode;
    public IFrameBufferLease? Lease { get; }
    public CameraAcquisitionFailure? Failure { get; }

    public static FrameAcquisitionResult Success(IFrameBufferLease lease) =>
        new(lease ?? throw new ArgumentNullException(nameof(lease)), null);

    public static FrameAcquisitionResult FailureResult(CameraAcquisitionFailure failure) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)));
}

/// <summary>Vendor-neutral camera seam used by Runtime and independent adapters.</summary>
public interface ICameraProvider : IAsyncDisposable
{
    CameraProviderIdentity Identity { get; }
    ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
    ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>One opened camera device and its typed common capabilities.</summary>
public interface ICameraDevice : IAsyncDisposable
{
    CameraDeviceDescriptor Descriptor { get; }
    CameraCapabilities Capabilities { get; }
    CameraHealthSnapshot GetHealthSnapshot();
    ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
        RequestedCameraConfiguration requested, CancellationToken cancellationToken = default);
    ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default);
    ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default);
}

internal static class CameraContractValidation
{
    internal static string Identifier(string value, string parameterName, int maximumLength)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length < 1 || value.Length > maximumLength)
            throw new ArgumentException("CameraIdentifierBoundsInvalid", parameterName);
        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-' or ':'))
                throw new ArgumentException("CameraIdentifierInvalid", parameterName);
        }

        return value;
    }

    internal static string Text(string value, string parameterName, int maximumBytes)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length == 0 || value.Any(char.IsControl) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("CameraTextInvalid", parameterName);
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes)
                throw new ArgumentException("CameraTextBoundsInvalid", parameterName);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("CameraTextInvalid", parameterName, exception);
        }

        return value;
    }

    internal static string Reason(string value, string parameterName) =>
        Identifier(value, parameterName, 128);

    internal static T Enum<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!System.Enum.IsDefined(typeof(T), value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T>? values, string parameterName,
        int maximumCount) where T : class
    {
        if (values is null)
            return new ReadOnlyCollection<T>(Array.Empty<T>());
        var copied = new List<T>(Math.Min(maximumCount, 16));
        foreach (var item in values)
        {
            if (copied.Count == maximumCount)
                throw new ArgumentException("CameraCollectionCapacityExceeded", parameterName);
            if (item is null)
                throw new ArgumentException("CameraCollectionContainsNull", parameterName);
            copied.Add(item);
        }

        return new ReadOnlyCollection<T>(copied);
    }

    internal static ReadOnlyCollection<T> CopyValues<T>(IEnumerable<T> values,
        string parameterName, int maximumCount)
    {
        if (values is null) throw new ArgumentNullException(parameterName);
        var copied = new List<T>(Math.Min(maximumCount, 16));
        foreach (var item in values)
        {
            if (copied.Count == maximumCount)
                throw new ArgumentException("CameraCollectionCapacityExceeded", parameterName);
            copied.Add(item);
        }

        return new ReadOnlyCollection<T>(copied);
    }
}
