namespace SharpInspect.Abstractions;

/// <summary>
/// Controls that are available only while a camera is in the development
/// preview path.  These values are never part of a production acquisition
/// request.
/// </summary>
public enum PreviewAutomaticControlMode
{
    Off,
    Once,
    Continuous
}

/// <summary>Capabilities reported by a preview device.</summary>
public sealed record CameraPreviewCapabilities
{
    public CameraPreviewCapabilities(bool supported, bool automaticExposure,
        bool automaticGain, bool automaticWhiteBalance)
    {
        Supported = supported;
        AutomaticExposure = automaticExposure;
        AutomaticGain = automaticGain;
        AutomaticWhiteBalance = automaticWhiteBalance;
    }

    public bool Supported { get; }
    public bool AutomaticExposure { get; }
    public bool AutomaticGain { get; }
    public bool AutomaticWhiteBalance { get; }
}

/// <summary>
/// The process settings that can be tuned for preview.  Triggering, timeout,
/// and production acquisition settings deliberately have no representation.
/// </summary>
public sealed record PreviewCameraProcessSettings
{
    public PreviewCameraProcessSettings(double exposureTimeUs, double gainDb,
        RegionOfInterest regionOfInterest, VisionPixelFormat pixelFormat,
        int? validBits, WhiteBalanceRgb? whiteBalanceRgb = null)
    {
        if (!double.IsFinite(exposureTimeUs) || exposureTimeUs <= 0 || exposureTimeUs > 60_000_000)
            throw new ArgumentOutOfRangeException(nameof(exposureTimeUs));
        if (!double.IsFinite(gainDb) || gainDb < -1_000 || gainDb > 1_000)
            throw new ArgumentOutOfRangeException(nameof(gainDb));
        RegionOfInterest = regionOfInterest ?? throw new ArgumentNullException(nameof(regionOfInterest));
        if (!Enum.IsDefined(typeof(VisionPixelFormat), pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        if (pixelFormat == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(nameof(validBits));
        }
        else if (validBits.HasValue)
        {
            throw new ArgumentException("PreviewValidBitsAbsentForPixelFormat", nameof(validBits));
        }
        if (pixelFormat != VisionPixelFormat.Bgr24 && whiteBalanceRgb is not null)
            throw new ArgumentException("PreviewWhiteBalanceAbsentForPixelFormat",
                nameof(whiteBalanceRgb));

        ExposureTimeUs = exposureTimeUs;
        GainDb = gainDb;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        WhiteBalanceRgb = whiteBalanceRgb;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-camera-preview-process-settings-v1",
            AlgorithmContractValidation.Invariant(exposureTimeUs),
            AlgorithmContractValidation.Invariant(gainDb),
            regionOfInterest.OffsetX.ToString(System.Globalization.CultureInfo.InvariantCulture),
            regionOfInterest.OffsetY.ToString(System.Globalization.CultureInfo.InvariantCulture),
            regionOfInterest.Width.ToString(System.Globalization.CultureInfo.InvariantCulture),
            regionOfInterest.Height.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pixelFormat.ToString(),
            validBits?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            whiteBalanceRgb?.Red is { } red ? AlgorithmContractValidation.Invariant(red) : null,
            whiteBalanceRgb?.Green is { } green ? AlgorithmContractValidation.Invariant(green) : null,
            whiteBalanceRgb?.Blue is { } blue ? AlgorithmContractValidation.Invariant(blue) : null
        });
    }

    public double ExposureTimeUs { get; }
    public double GainDb { get; }
    public RegionOfInterest RegionOfInterest { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public WhiteBalanceRgb? WhiteBalanceRgb { get; }
    public string ContentHash { get; }
}

/// <summary>Preview process settings plus explicitly scoped automatic controls.</summary>
public sealed record PreviewTuningConfiguration
{
    public PreviewTuningConfiguration(PreviewCameraProcessSettings processSettings,
        PreviewAutomaticControlMode exposureMode = PreviewAutomaticControlMode.Off,
        PreviewAutomaticControlMode gainMode = PreviewAutomaticControlMode.Off,
        PreviewAutomaticControlMode whiteBalanceMode = PreviewAutomaticControlMode.Off)
    {
        ProcessSettings = processSettings ?? throw new ArgumentNullException(nameof(processSettings));
        ExposureMode = ValidateMode(exposureMode, nameof(exposureMode));
        GainMode = ValidateMode(gainMode, nameof(gainMode));
        WhiteBalanceMode = ValidateMode(whiteBalanceMode, nameof(whiteBalanceMode));
        if (WhiteBalanceMode != PreviewAutomaticControlMode.Off &&
            ProcessSettings.PixelFormat != VisionPixelFormat.Bgr24)
            throw new ArgumentException("PreviewWhiteBalanceAutomaticUnsupported",
                nameof(whiteBalanceMode));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-camera-preview-tuning-configuration-v1",
            ProcessSettings.ContentHash,
            ExposureMode.ToString(),
            GainMode.ToString(),
            WhiteBalanceMode.ToString()
        });
    }

    public PreviewCameraProcessSettings ProcessSettings { get; }
    public PreviewAutomaticControlMode ExposureMode { get; }
    public PreviewAutomaticControlMode GainMode { get; }
    public PreviewAutomaticControlMode WhiteBalanceMode { get; }
    public string ContentHash { get; }

    private static PreviewAutomaticControlMode ValidateMode(
        PreviewAutomaticControlMode mode, string parameterName) =>
        Enum.IsDefined(typeof(PreviewAutomaticControlMode), mode)
            ? mode : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>Immutable preview pixels with no production frame identity or lease.</summary>
public sealed class CameraPreviewFrame
{
    public const long MaximumBytes = 16L * 1024 * 1024;

    private readonly byte[] _pixels;

    public CameraPreviewFrame(Guid sessionId, long sequence, FrameTimePoint capturedAt,
        int width, int height, int strideBytes, VisionPixelFormat pixelFormat,
        int? validBits, ReadOnlySpan<byte> pixels)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("PreviewSessionIdInvalid", nameof(sessionId));
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        CapturedAt = capturedAt ?? throw new ArgumentNullException(nameof(capturedAt));
        if (width is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(height));
        var bytesPerPixel = BytesPerPixel(pixelFormat, nameof(pixelFormat));
        var minimumStride = checked(width * bytesPerPixel);
        if (strideBytes < minimumStride)
            throw new ArgumentOutOfRangeException(nameof(strideBytes));
        var fullLength = checked((long)strideBytes * height);
        if (fullLength > MaximumBytes || fullLength > int.MaxValue)
            throw new ArgumentException("PreviewFrameCapacityExceeded", nameof(pixels));
        if (pixels.Length != fullLength)
            throw new ArgumentException("PreviewFrameLayoutLengthInvalid", nameof(pixels));
        ValidateValidBits(pixelFormat, validBits, nameof(validBits));

        SessionId = sessionId;
        Sequence = sequence;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        _pixels = pixels.ToArray();
    }

    public Guid SessionId { get; }
    public long Sequence { get; }
    public FrameTimePoint CapturedAt { get; }
    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public int ValidRowBytes => checked(Width * BytesPerPixel(PixelFormat, nameof(PixelFormat)));
    public long BufferLength => checked((long)StrideBytes * Height);

    /// <summary>Returns a read-only view over exactly one copied row.</summary>
    public ReadOnlySpan<byte> GetRowSpan(int row)
    {
        if (row < 0 || row >= Height)
            throw new ArgumentOutOfRangeException(nameof(row));
        return new ReadOnlySpan<byte>(_pixels, checked(row * StrideBytes), StrideBytes);
    }

    private static int BytesPerPixel(VisionPixelFormat format, string parameterName) => format switch
    {
        VisionPixelFormat.Mono8 => 1,
        VisionPixelFormat.Mono16 => 2,
        VisionPixelFormat.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(parameterName)
    };

    private static void ValidateValidBits(VisionPixelFormat format, int? validBits,
        string parameterName)
    {
        if (format == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(parameterName);
        }
        else if (validBits.HasValue)
        {
            throw new ArgumentException("PreviewValidBitsAbsentForPixelFormat", parameterName);
        }
    }
}

/// <summary>Result of reading the latest preview frame.</summary>
public sealed class CameraPreviewFrameResult
{
    public CameraPreviewFrameResult(bool succeeded, string reasonCode,
        CameraPreviewFrame? frame = null)
    {
        Succeeded = succeeded;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        if (succeeded != (frame is not null))
            throw new ArgumentException("CameraPreviewFrameResultStateInvalid", nameof(frame));
        Frame = frame;
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }
    public CameraPreviewFrame? Frame { get; }

    public static CameraPreviewFrameResult Success(CameraPreviewFrame frame) =>
        new(true, "CameraPreviewFrameRead", frame ?? throw new ArgumentNullException(nameof(frame)));

    public static CameraPreviewFrameResult Failure(string reasonCode) =>
        new(false, reasonCode);
}

/// <summary>Result of starting, tuning, or freezing preview settings.</summary>
public sealed class CameraPreviewConfigurationResult
{
    public CameraPreviewConfigurationResult(bool succeeded, string reasonCode,
        PreviewTuningConfiguration? configuration = null)
    {
        Succeeded = succeeded;
        ReasonCode = CameraContractValidation.Reason(reasonCode, nameof(reasonCode));
        if (succeeded != (configuration is not null))
            throw new ArgumentException("CameraPreviewConfigurationResultStateInvalid",
                nameof(configuration));
        Configuration = configuration;
    }

    public bool Succeeded { get; }
    public string ReasonCode { get; }
    public PreviewTuningConfiguration? Configuration { get; }

    public static CameraPreviewConfigurationResult Success(PreviewTuningConfiguration configuration) =>
        new(true, "CameraPreviewConfigurationApplied",
            configuration ?? throw new ArgumentNullException(nameof(configuration)));

    public static CameraPreviewConfigurationResult Failure(string reasonCode) =>
        new(false, reasonCode);
}

/// <summary>
/// Optional preview-only camera device. It has no production acquisition or
/// inspection identity surface.
/// </summary>
public interface ICameraPreviewDevice : IAsyncDisposable
{
    CameraPreviewCapabilities PreviewCapabilities { get; }

    /// <summary>
    /// Starts continuous preview on an open, configured and stopped device.
    /// A successful result must also report Applied / Previewing through
    /// <see cref="ICameraDevice.GetHealthSnapshot"/>.
    /// </summary>
    ValueTask<CameraPreviewConfigurationResult> StartPreviewAsync(Guid sessionId,
        PreviewTuningConfiguration configuration, CancellationToken cancellationToken = default);

    ValueTask<CameraPreviewConfigurationResult> ApplyPreviewTuningAsync(
        PreviewTuningConfiguration configuration, CancellationToken cancellationToken = default);

    ValueTask<CameraPreviewConfigurationResult> FreezeFixedPreviewConfigurationAsync(
        CancellationToken cancellationToken = default);

    ValueTask<CameraPreviewFrameResult> ReadLatestPreviewFrameAsync(long afterSequence,
        CancellationToken cancellationToken = default);

    ValueTask<CameraOperationResult> StopPreviewAsync(
        CancellationToken cancellationToken = default);
}
