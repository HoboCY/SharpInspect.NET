using System.Text;

namespace SharpInspect.Abstractions;

public enum ProductionAcquisitionMode
{
    SoftwareTrigger,
    HardwareTrigger
}

public enum DeviceClockSynchronization
{
    Unknown,
    Unsynchronized,
    Synchronized
}

public sealed record RegionOfInterest
{
    public RegionOfInterest(int offsetX, int offsetY, int width, int height)
    {
        if (offsetX is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(offsetX));
        if (offsetY is < 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(offsetY));
        if (width is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(height));
        if ((long)offsetX + width > 1_000_000 || (long)offsetY + height > 1_000_000)
            throw new ArgumentException("RegionOfInterestBoundsInvalid");

        OffsetX = offsetX;
        OffsetY = offsetY;
        Width = width;
        Height = height;
    }

    public int OffsetX { get; }
    public int OffsetY { get; }
    public int Width { get; }
    public int Height { get; }
}

public sealed record WhiteBalanceRgb
{
    public WhiteBalanceRgb(double red, double green, double blue)
    {
        RequireChannel(red, nameof(red));
        RequireChannel(green, nameof(green));
        RequireChannel(blue, nameof(blue));
        Red = red;
        Green = green;
        Blue = blue;
    }

    public double Red { get; }
    public double Green { get; }
    public double Blue { get; }

    private static void RequireChannel(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0 || value > 1_000)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// Vendor-neutral camera process settings read back after Runtime applies a complete
/// Recipe configuration. It contains no provider, device, SDK or deployment identity.
/// </summary>
public sealed record EffectiveCameraConfiguration
{
    public EffectiveCameraConfiguration(ProductionAcquisitionMode productionAcquisitionMode,
        double exposureTimeUs, double gainDb, RegionOfInterest regionOfInterest,
        VisionPixelFormat pixelFormat, int? validBits, int acquisitionTimeoutMs,
        double triggerDelayUs, WhiteBalanceRgb? whiteBalanceRgb)
    {
        if (!Enum.IsDefined(typeof(ProductionAcquisitionMode), productionAcquisitionMode))
            throw new ArgumentOutOfRangeException(nameof(productionAcquisitionMode));
        RequireFinitePositive(exposureTimeUs, 60_000_000, nameof(exposureTimeUs));
        RequireFinite(gainDb, -1_000, 1_000, nameof(gainDb));
        RegionOfInterest = regionOfInterest ?? throw new ArgumentNullException(nameof(regionOfInterest));
        ValidatePixelFormat(pixelFormat, validBits, whiteBalanceRgb);
        if (acquisitionTimeoutMs is < 1 or > 600_000)
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeoutMs));
        RequireFinite(triggerDelayUs, 0, 60_000_000, nameof(triggerDelayUs));

        ProductionAcquisitionMode = productionAcquisitionMode;
        ExposureTimeUs = exposureTimeUs;
        GainDb = gainDb;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        AcquisitionTimeoutMs = acquisitionTimeoutMs;
        TriggerDelayUs = triggerDelayUs;
        WhiteBalanceRgb = whiteBalanceRgb;
    }

    public ProductionAcquisitionMode ProductionAcquisitionMode { get; }
    public double ExposureTimeUs { get; }
    public double GainDb { get; }
    public RegionOfInterest RegionOfInterest { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public int AcquisitionTimeoutMs { get; }
    public double TriggerDelayUs { get; }
    public WhiteBalanceRgb? WhiteBalanceRgb { get; }

    private static void ValidatePixelFormat(VisionPixelFormat pixelFormat, int? validBits,
        WhiteBalanceRgb? whiteBalanceRgb)
    {
        if (!Enum.IsDefined(typeof(VisionPixelFormat), pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        if (pixelFormat == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(nameof(validBits));
        }
        else if (validBits.HasValue)
        {
            throw new ArgumentException("EffectiveCameraValidBitsAbsentForPixelFormat", nameof(validBits));
        }

        if (pixelFormat != VisionPixelFormat.Bgr24 && whiteBalanceRgb is not null)
            throw new ArgumentException("EffectiveCameraWhiteBalanceAbsentForPixelFormat",
                nameof(whiteBalanceRgb));
    }

    private static void RequireFinitePositive(double value, double maximum, string parameterName)
    {
        RequireFinite(value, 0, maximum, parameterName);
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void RequireFinite(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// The vendor-neutral metadata presented to an algorithm with a borrowed Vision Frame.
/// Vendor, physical-device and SDK facts belong to FrameProvenance instead.
/// </summary>
public sealed record FrameMetadata
{
    public FrameMetadata(ExecutionCorrelationId correlation, string logicalCameraRole,
        int width, int height, int strideBytes, VisionPixelFormat pixelFormat,
        int? validBits, DateTimeOffset hostCaptureUtc,
        EffectiveCameraConfiguration effectiveCameraConfiguration)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        LogicalCameraRole = FrameMetadataValidation.Identifier(logicalCameraRole, nameof(logicalCameraRole));
        if (width is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 32_768) throw new ArgumentOutOfRangeException(nameof(height));
        var bytesPerPixel = FrameMetadataValidation.BytesPerPixel(pixelFormat, nameof(pixelFormat));
        var minimumStride = checked(width * bytesPerPixel);
        if (strideBytes < minimumStride || strideBytes > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(strideBytes));
        var validRowBytes = checked(width * bytesPerPixel);
        var requiredBufferLength = checked((long)strideBytes * (height - 1) + validRowBytes);
        FrameMetadataValidation.ValidateValidBits(pixelFormat, validBits, nameof(validBits));
        FrameMetadataValidation.Utc(hostCaptureUtc, nameof(hostCaptureUtc));
        EffectiveCameraConfiguration = effectiveCameraConfiguration ??
            throw new ArgumentNullException(nameof(effectiveCameraConfiguration));
        if (effectiveCameraConfiguration.PixelFormat != pixelFormat ||
            effectiveCameraConfiguration.ValidBits != validBits ||
            effectiveCameraConfiguration.RegionOfInterest.Width != width ||
            effectiveCameraConfiguration.RegionOfInterest.Height != height)
            throw new ArgumentException("FrameMetadataCameraConfigurationMismatch",
                nameof(effectiveCameraConfiguration));

        Correlation = correlation;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        ValidRowBytes = validRowBytes;
        RequiredBufferLength = requiredBufferLength;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        HostCaptureUtc = hostCaptureUtc;
    }

    public ExecutionCorrelationId Correlation { get; }
    public ExecutionCorrelationId ExecutionCorrelation => Correlation;
    public string LogicalCameraRole { get; }
    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
    public int ValidRowBytes { get; }
    public long RequiredBufferLength { get; }
    /// <summary>Backing allocation required by native views, including the final row's padding.</summary>
    public long FullBufferLayoutLength => checked((long)StrideBytes * Height);
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public DateTimeOffset HostCaptureUtc { get; }
    public EffectiveCameraConfiguration EffectiveCameraConfiguration { get; }
}

public sealed record DeviceTimestamp
{
    public DeviceTimestamp(long value, long? tickFrequency, string? unit, string clockDomain,
        long? counterRollover, DeviceClockSynchronization synchronization)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (tickFrequency is <= 0) throw new ArgumentOutOfRangeException(nameof(tickFrequency));
        if (counterRollover is <= 0) throw new ArgumentOutOfRangeException(nameof(counterRollover));
        if (tickFrequency is null && string.IsNullOrWhiteSpace(unit))
            throw new ArgumentException("DeviceTimestampUnitRequired", nameof(unit));
        if (!Enum.IsDefined(typeof(DeviceClockSynchronization), synchronization))
            throw new ArgumentOutOfRangeException(nameof(synchronization));

        Value = value;
        TickFrequency = tickFrequency;
        Unit = unit is null ? null : FrameMetadataValidation.Text(unit, nameof(unit), 64);
        ClockDomain = FrameMetadataValidation.Identifier(clockDomain, nameof(clockDomain));
        CounterRollover = counterRollover;
        Synchronization = synchronization;
    }

    public long Value { get; }
    public long? TickFrequency { get; }
    public string? Unit { get; }
    public string ClockDomain { get; }
    public long? CounterRollover { get; }
    public DeviceClockSynchronization Synchronization { get; }
}

public sealed record FrameTimePoint
{
    public FrameTimePoint(DateTimeOffset hostObservedAtUtc, long monotonicTimestamp)
    {
        FrameMetadataValidation.Utc(hostObservedAtUtc, nameof(hostObservedAtUtc));
        if (monotonicTimestamp < 0)
            throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
        HostObservedAtUtc = hostObservedAtUtc;
        MonotonicTimestamp = monotonicTimestamp;
    }

    public DateTimeOffset HostObservedAtUtc { get; }
    public long MonotonicTimestamp { get; }
}

public sealed record FrameAcquisitionMilestones
{
    public FrameAcquisitionMilestones(long monotonicFrequency, FrameTimePoint? triggerAccepted,
        FrameTimePoint? acquisitionStarted, FrameTimePoint? nativeFrameReceived,
        FrameTimePoint? normalizedFrameReady)
    {
        if (monotonicFrequency is < 1 or > 10_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));
        ValidateOrder(triggerAccepted, acquisitionStarted, nativeFrameReceived, normalizedFrameReady);

        MonotonicFrequency = monotonicFrequency;
        TriggerAccepted = triggerAccepted;
        AcquisitionStarted = acquisitionStarted;
        NativeFrameReceived = nativeFrameReceived;
        NormalizedFrameReady = normalizedFrameReady;
    }

    public long MonotonicFrequency { get; }
    public FrameTimePoint? TriggerAccepted { get; }
    public FrameTimePoint? AcquisitionStarted { get; }
    public FrameTimePoint? NativeFrameReceived { get; }
    public FrameTimePoint? NormalizedFrameReady { get; }

    private static void ValidateOrder(params FrameTimePoint?[] milestones)
    {
        FrameTimePoint? previous = null;
        foreach (var current in milestones)
        {
            if (current is null) continue;
            if (previous is not null && current.MonotonicTimestamp < previous.MonotonicTimestamp)
                throw new ArgumentException("FrameMilestoneOrderInvalid", nameof(milestones));
            previous = current;
        }
    }
}

/// <summary>
/// Acquisition evidence retained for audit and trace. It is never supplied to an
/// Inspection Algorithm as control input.
/// </summary>
public sealed record PoolCopyEvidence
{
    internal PoolCopyEvidence(int sourceStrideBytes, int destinationStrideBytes,
        bool inputNormalizationTransformed)
    {
        if (sourceStrideBytes is < 1 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(sourceStrideBytes));
        if (destinationStrideBytes is < 1 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(destinationStrideBytes));

        SourceStrideBytes = sourceStrideBytes;
        DestinationStrideBytes = destinationStrideBytes;
        InputNormalizationTransformed = inputNormalizationTransformed;
    }

    public int SourceStrideBytes { get; }
    public int DestinationStrideBytes { get; }
    public bool UsedPreallocatedPixelBuffer => true;
    public bool StrideChanged => SourceStrideBytes != DestinationStrideBytes;
    public bool InputNormalizationTransformed { get; }
    public bool IdentityPixelCopy => true;
    public bool PaddingZeroed => true;
}

public sealed record FrameProvenance
{
    public FrameProvenance(ExecutionCorrelationId correlation, string providerId,
        string providerVersion, string adapterId, string adapterVersion, string sdkId,
        string sdkVersion, string? nativeRuntimeVersion, string stableDeviceIdentity, string? reportedModel,
        string? firmwareVersion, string nativePixelFormatDescription,
        string normalizationDetails, bool normalizationAllocated, bool normalizationTransformed,
        DeviceTimestamp? deviceTimestamp,
        ulong? frameCounter, FrameAcquisitionMilestones milestones)
        : this(correlation, providerId, providerVersion, adapterId, adapterVersion, sdkId,
            sdkVersion, nativeRuntimeVersion, stableDeviceIdentity, reportedModel, firmwareVersion,
            nativePixelFormatDescription, normalizationDetails, normalizationAllocated,
            normalizationTransformed, deviceTimestamp, frameCounter, milestones, null)
    {
    }

    private FrameProvenance(ExecutionCorrelationId correlation, string providerId,
        string providerVersion, string adapterId, string adapterVersion, string sdkId,
        string sdkVersion, string? nativeRuntimeVersion, string stableDeviceIdentity, string? reportedModel,
        string? firmwareVersion, string nativePixelFormatDescription,
        string normalizationDetails, bool normalizationAllocated, bool normalizationTransformed,
        DeviceTimestamp? deviceTimestamp, ulong? frameCounter, FrameAcquisitionMilestones milestones,
        PoolCopyEvidence? poolCopyEvidence)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        AlgorithmContractValidation.Correlation(correlation, nameof(correlation));
        Correlation = correlation;
        ProviderId = FrameMetadataValidation.Identifier(providerId, nameof(providerId));
        ProviderVersion = FrameMetadataValidation.Text(providerVersion, nameof(providerVersion), 128);
        AdapterId = FrameMetadataValidation.Identifier(adapterId, nameof(adapterId));
        AdapterVersion = FrameMetadataValidation.Text(adapterVersion, nameof(adapterVersion), 128);
        SdkId = FrameMetadataValidation.Identifier(sdkId, nameof(sdkId));
        SdkVersion = FrameMetadataValidation.Text(sdkVersion, nameof(sdkVersion), 128);
        NativeRuntimeVersion = nativeRuntimeVersion is null ? null :
            FrameMetadataValidation.Text(nativeRuntimeVersion, nameof(nativeRuntimeVersion), 128);
        StableDeviceIdentity = FrameMetadataValidation.Text(stableDeviceIdentity,
            nameof(stableDeviceIdentity), 128);
        ReportedModel = reportedModel is null ? null :
            FrameMetadataValidation.Text(reportedModel, nameof(reportedModel), 128);
        FirmwareVersion = firmwareVersion is null ? null :
            FrameMetadataValidation.Text(firmwareVersion, nameof(firmwareVersion), 128);
        NativePixelFormatDescription = FrameMetadataValidation.Text(nativePixelFormatDescription,
            nameof(nativePixelFormatDescription), 256);
        NormalizationDetails = FrameMetadataValidation.Text(normalizationDetails,
            nameof(normalizationDetails), 2048);
        NormalizationAllocated = normalizationAllocated;
        NormalizationTransformed = normalizationTransformed;
        DeviceTimestamp = deviceTimestamp;
        FrameCounter = frameCounter;
        Milestones = milestones ?? throw new ArgumentNullException(nameof(milestones));
        PoolCopyEvidence = poolCopyEvidence;
    }

    public ExecutionCorrelationId Correlation { get; }
    public string ProviderId { get; }
    public string ProviderVersion { get; }
    public string AdapterId { get; }
    public string AdapterVersion { get; }
    public string SdkId { get; }
    public string SdkVersion { get; }
    public string? NativeRuntimeVersion { get; }
    public string StableDeviceIdentity { get; }
    public string? ReportedModel { get; }
    public string? FirmwareVersion { get; }
    public string NativePixelFormatDescription { get; }
    public string NormalizationDetails { get; }
    public bool NormalizationAllocated { get; }
    public bool NormalizationTransformed { get; }
    public DeviceTimestamp? DeviceTimestamp { get; }
    public ulong? FrameCounter { get; }
    public FrameAcquisitionMilestones Milestones { get; }
    public PoolCopyEvidence? PoolCopyEvidence { get; }

    /// <summary>
    /// Adds bounded evidence produced by the trusted preallocated pool. The source
    /// provenance remains unchanged and no free-form normalization text is appended.
    /// </summary>
    internal FrameProvenance WithPoolCopyEvidence(int sourceStrideBytes, int destinationStrideBytes)
    {
        var evidence = new PoolCopyEvidence(sourceStrideBytes, destinationStrideBytes,
            NormalizationTransformed);
        return new FrameProvenance(Correlation, ProviderId, ProviderVersion, AdapterId, AdapterVersion,
            SdkId, SdkVersion, NativeRuntimeVersion, StableDeviceIdentity, ReportedModel,
            FirmwareVersion, NativePixelFormatDescription, NormalizationDetails,
            NormalizationAllocated, NormalizationTransformed || evidence.StrideChanged,
            DeviceTimestamp, FrameCounter, Milestones, evidence);
    }
}

internal static class FrameMetadataValidation
{
    internal static string Identifier(string value, string parameterName)
    {
        var result = Text(value, parameterName, 64);
        foreach (var character in result)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException("FrameMetadataIdentifierInvalid", parameterName);
        }
        return result;
    }

    internal static string Text(string value, string parameterName, int maximumBytes)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length == 0 || value.Length > maximumBytes)
            throw new ArgumentException("FrameMetadataTextBoundsInvalid", parameterName);
        try
        {
            var encoding = new UTF8Encoding(false, true);
            if (encoding.GetByteCount(value) > maximumBytes)
                throw new ArgumentException("FrameMetadataTextBoundsInvalid", parameterName);
            if (value.Any(char.IsControl))
                throw new ArgumentException("FrameMetadataTextControlInvalid", parameterName);
            return value;
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("FrameMetadataUtf8Invalid", parameterName, exception);
        }
    }

    internal static void Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("FrameMetadataUtcRequired", parameterName);
    }

    internal static int BytesPerPixel(VisionPixelFormat format, string parameterName)
    {
        return format switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new ArgumentOutOfRangeException(parameterName)
        };
    }

    internal static void ValidateValidBits(VisionPixelFormat format, int? validBits,
        string parameterName)
    {
        if (format == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(parameterName);
        }
        else if (validBits.HasValue)
        {
            throw new ArgumentException("FrameMetadataValidBitsAbsentForPixelFormat", parameterName);
        }
    }
}
