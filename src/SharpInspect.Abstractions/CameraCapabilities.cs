using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

public enum CameraQuantizationMode
{
    Exact,
    Nearest,
    Floor,
    Ceiling
}

/// <summary>Bounded floating-point camera setting capability.</summary>
public sealed record CameraDoubleCapability
{
    public CameraDoubleCapability(double minimum, double maximum, double increment,
        CameraQuantizationMode quantizationMode, double quantizationTolerance = 0,
        bool readable = true, bool writable = true)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum ||
            !double.IsFinite(maximum - minimum))
            throw new ArgumentOutOfRangeException(nameof(minimum));
        if (!double.IsFinite(increment) || increment <= 0)
            throw new ArgumentOutOfRangeException(nameof(increment));
        if (!double.IsFinite(quantizationTolerance) || quantizationTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(quantizationTolerance));
        if (minimum < maximum)
        {
            var endpointUlp = CameraCapabilityMath.Ulp(Math.Max(Math.Abs(minimum),
                Math.Abs(maximum)));
            if (!double.IsFinite(endpointUlp) || endpointUlp <= 0 ||
                increment / endpointUlp < 16)
                throw new ArgumentException("CameraCapabilityResolutionUnsupported",
                    nameof(increment));
        }

        Minimum = minimum;
        Maximum = maximum;
        Increment = increment;
        QuantizationMode = CameraContractValidation.Enum(quantizationMode, nameof(quantizationMode));
        QuantizationTolerance = quantizationTolerance;
        Readable = readable;
        Writable = writable;
    }

    public double Minimum { get; }
    public double Maximum { get; }
    public double Increment { get; }
    public CameraQuantizationMode QuantizationMode { get; }
    public double QuantizationTolerance { get; }
    public bool Readable { get; }
    public bool Writable { get; }
}

/// <summary>Bounded integer capability used by sensor-pixel ROI coordinates and sizes.</summary>
public sealed record CameraIntCapability
{
    public CameraIntCapability(int minimum, int maximum, int increment,
        bool readable = true, bool writable = true)
    {
        if (minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        if (increment < 1)
            throw new ArgumentOutOfRangeException(nameof(increment));
        Minimum = minimum;
        Maximum = maximum;
        Increment = increment;
        Readable = readable;
        Writable = writable;
    }

    public int Minimum { get; }
    public int Maximum { get; }
    public int Increment { get; }
    public bool Readable { get; }
    public bool Writable { get; }
}

public sealed record CameraRoiCapabilities
{
    public CameraRoiCapabilities(int sensorWidth, int sensorHeight,
        CameraIntCapability offsetX, CameraIntCapability offsetY,
        CameraIntCapability width, CameraIntCapability height)
    {
        if (sensorWidth is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(sensorWidth));
        if (sensorHeight is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(sensorHeight));
        OffsetX = offsetX ?? throw new ArgumentNullException(nameof(offsetX));
        OffsetY = offsetY ?? throw new ArgumentNullException(nameof(offsetY));
        Width = width ?? throw new ArgumentNullException(nameof(width));
        Height = height ?? throw new ArgumentNullException(nameof(height));
        if (OffsetX.Minimum < 0 || OffsetX.Maximum >= sensorWidth ||
            OffsetY.Minimum < 0 || OffsetY.Maximum >= sensorHeight ||
            Width.Minimum < 1 || Width.Maximum > sensorWidth ||
            Height.Minimum < 1 || Height.Maximum > sensorHeight)
            throw new ArgumentException("CameraRoiCapabilityBoundsInvalid");

        SensorWidth = sensorWidth;
        SensorHeight = sensorHeight;
    }

    public int SensorWidth { get; }
    public int SensorHeight { get; }
    public CameraIntCapability OffsetX { get; }
    public CameraIntCapability OffsetY { get; }
    public CameraIntCapability Width { get; }
    public CameraIntCapability Height { get; }
}

public sealed record CameraWhiteBalanceCapabilities
{
    public CameraWhiteBalanceCapabilities(CameraDoubleCapability red,
        CameraDoubleCapability green, CameraDoubleCapability blue)
    {
        Red = red ?? throw new ArgumentNullException(nameof(red));
        Green = green ?? throw new ArgumentNullException(nameof(green));
        Blue = blue ?? throw new ArgumentNullException(nameof(blue));
    }

    public CameraDoubleCapability Red { get; }
    public CameraDoubleCapability Green { get; }
    public CameraDoubleCapability Blue { get; }
}

/// <summary>
/// Explicit common camera capability contract. It contains no vendor dictionary
/// and is immutable after construction.
/// </summary>
public sealed class CameraCapabilities
{
    private const double MaximumExactGridUlps = 4;
    private readonly ReadOnlyCollection<ProductionAcquisitionMode> _acquisitionModes;
    private readonly ReadOnlyCollection<VisionPixelFormat> _pixelFormats;
    private readonly ReadOnlyCollection<int> _mono16ValidBits;

    public CameraCapabilities(IEnumerable<ProductionAcquisitionMode> acquisitionModes,
        IEnumerable<VisionPixelFormat> pixelFormats, IEnumerable<int> mono16ValidBits,
        CameraDoubleCapability exposureTimeUs, CameraDoubleCapability gainDb,
        CameraDoubleCapability triggerDelayUs, CameraRoiCapabilities regionOfInterest,
        CameraWhiteBalanceCapabilities? whiteBalanceRgb = null)
    {
        ArgumentNullException.ThrowIfNull(acquisitionModes);
        ArgumentNullException.ThrowIfNull(pixelFormats);
        ArgumentNullException.ThrowIfNull(mono16ValidBits);
        var modes = CameraContractValidation.CopyValues(acquisitionModes,
            nameof(acquisitionModes), 2).ToArray();
        var formats = CameraContractValidation.CopyValues(pixelFormats,
            nameof(pixelFormats), 3).ToArray();
        var validBits = CameraContractValidation.CopyValues(mono16ValidBits,
            nameof(mono16ValidBits), 3).ToArray();
        if (modes.Length is < 1 or > 2 || modes.Distinct().Count() != modes.Length ||
            modes.Any(mode => !Enum.IsDefined(typeof(ProductionAcquisitionMode), mode)))
            throw new ArgumentException("CameraAcquisitionModesInvalid", nameof(acquisitionModes));
        if (formats.Length is < 1 or > 3 || formats.Distinct().Count() != formats.Length ||
            formats.Any(format => !Enum.IsDefined(typeof(VisionPixelFormat), format)))
            throw new ArgumentException("CameraPixelFormatsInvalid", nameof(pixelFormats));
        if (validBits.Length > 3 || validBits.Distinct().Count() != validBits.Length ||
            validBits.Any(bits => bits is not (10 or 12 or 16)))
            throw new ArgumentException("CameraMono16ValidBitsInvalid", nameof(mono16ValidBits));
        if (formats.Contains(VisionPixelFormat.Mono16) != (validBits.Length != 0))
            throw new ArgumentException("CameraMono16ValidBitsBindingInvalid", nameof(mono16ValidBits));
        if (whiteBalanceRgb is not null && !formats.Contains(VisionPixelFormat.Bgr24))
            throw new ArgumentException("CameraWhiteBalanceCapabilityBindingInvalid",
                nameof(whiteBalanceRgb));

        AcquisitionModes = _acquisitionModes = new ReadOnlyCollection<ProductionAcquisitionMode>(
            modes.OrderBy(mode => mode).ToArray());
        PixelFormats = _pixelFormats = new ReadOnlyCollection<VisionPixelFormat>(
            formats.OrderBy(format => format).ToArray());
        Mono16ValidBits = _mono16ValidBits = new ReadOnlyCollection<int>(
            validBits.OrderBy(bits => bits).ToArray());
        ExposureTimeUs = exposureTimeUs ?? throw new ArgumentNullException(nameof(exposureTimeUs));
        GainDb = gainDb ?? throw new ArgumentNullException(nameof(gainDb));
        TriggerDelayUs = triggerDelayUs ?? throw new ArgumentNullException(nameof(triggerDelayUs));
        RegionOfInterest = regionOfInterest ?? throw new ArgumentNullException(nameof(regionOfInterest));
        WhiteBalanceRgb = whiteBalanceRgb;
        ContentHash = ComputeContentHash();
    }

    public IReadOnlyList<ProductionAcquisitionMode> AcquisitionModes { get; }
    public IReadOnlyList<VisionPixelFormat> PixelFormats { get; }
    public IReadOnlyList<int> Mono16ValidBits { get; }
    public CameraDoubleCapability ExposureTimeUs { get; }
    public CameraDoubleCapability GainDb { get; }
    public CameraDoubleCapability TriggerDelayUs { get; }
    public CameraRoiCapabilities RegionOfInterest { get; }
    public CameraWhiteBalanceCapabilities? WhiteBalanceRgb { get; }
    public string ContentHash { get; }

    /// <summary>
    /// Validates a complete requested camera configuration and creates its
    /// effective read-back values. No clamping or implicit quantization occurs.
    /// </summary>
    public CameraConfigurationResult ValidateConfiguration(RequestedCameraConfiguration requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!AcquisitionModes.Contains(requested.ProductionAcquisitionMode))
            return CameraConfigurationResult.Failure("CameraConfigurationAcquisitionModeUnsupported");
        if (!PixelFormats.Contains(requested.PixelFormat))
            return CameraConfigurationResult.Failure("CameraConfigurationPixelFormatUnsupported");

        if (requested.PixelFormat == VisionPixelFormat.Mono16)
        {
            if (requested.ValidBits is not { } validBits || !Mono16ValidBits.Contains(validBits))
                return CameraConfigurationResult.Failure("CameraConfigurationMono16ValidBitsUnsupported");
        }
        else if (requested.ValidBits.HasValue)
        {
            return CameraConfigurationResult.Failure("CameraConfigurationValidBitsInvalid");
        }

        var differences = new List<CameraConfigurationDifference>();
        if (!TryResolveDouble(ExposureTimeUs, requested.ExposureTimeUs,
                CameraNumericSetting.ExposureTimeUs, out var exposure, out var reason))
            return CameraConfigurationResult.Failure(reason);
        if (!TryResolveDouble(GainDb, requested.GainDb,
                CameraNumericSetting.GainDb, out var gain, out reason))
            return CameraConfigurationResult.Failure(reason);
        if (!TryResolveDouble(TriggerDelayUs, requested.TriggerDelayUs,
                CameraNumericSetting.TriggerDelayUs, out var triggerDelay, out reason))
            return CameraConfigurationResult.Failure(reason);
        AddDifference(differences, CameraNumericSetting.ExposureTimeUs,
            requested.ExposureTimeUs, exposure);
        AddDifference(differences, CameraNumericSetting.GainDb, requested.GainDb, gain);
        AddDifference(differences, CameraNumericSetting.TriggerDelayUs,
            requested.TriggerDelayUs, triggerDelay);

        var roi = requested.RegionOfInterest;
        if (!TryValidateRoi(roi, out reason))
            return CameraConfigurationResult.Failure(reason);

        WhiteBalanceRgb? whiteBalance = requested.WhiteBalanceRgb;
        if (whiteBalance is not null)
        {
            if (requested.PixelFormat != VisionPixelFormat.Bgr24)
                return CameraConfigurationResult.Failure("CameraConfigurationWhiteBalanceInvalid");
            if (WhiteBalanceRgb is null)
                return CameraConfigurationResult.Failure("CameraConfigurationWhiteBalanceUnsupported");
            if (!TryResolveDouble(WhiteBalanceRgb.Red, whiteBalance.Red,
                    CameraNumericSetting.WhiteBalanceRed, out var red, out reason) ||
                !TryResolveDouble(WhiteBalanceRgb.Green, whiteBalance.Green,
                    CameraNumericSetting.WhiteBalanceGreen, out var green, out reason) ||
                !TryResolveDouble(WhiteBalanceRgb.Blue, whiteBalance.Blue,
                    CameraNumericSetting.WhiteBalanceBlue, out var blue, out reason))
                return CameraConfigurationResult.Failure(reason);
            AddDifference(differences, CameraNumericSetting.WhiteBalanceRed, whiteBalance.Red, red);
            AddDifference(differences, CameraNumericSetting.WhiteBalanceGreen, whiteBalance.Green, green);
            AddDifference(differences, CameraNumericSetting.WhiteBalanceBlue, whiteBalance.Blue, blue);
            whiteBalance = new WhiteBalanceRgb(red, green, blue);
        }

        try
        {
            var effective = new EffectiveCameraConfiguration(requested.ProductionAcquisitionMode,
                exposure, gain, roi, requested.PixelFormat, requested.ValidBits,
                requested.AcquisitionTimeoutMs, triggerDelay, whiteBalance);
            return CameraConfigurationResult.Success(effective, differences);
        }
        catch (ArgumentException)
        {
            return CameraConfigurationResult.Failure("CameraConfigurationInvalid");
        }
    }

    private bool TryValidateRoi(RegionOfInterest roi, out string reason)
    {
        if (!TryValidateInt(RegionOfInterest.OffsetX, roi.OffsetX, "CameraConfigurationRoiOffsetX", out reason) ||
            !TryValidateInt(RegionOfInterest.OffsetY, roi.OffsetY, "CameraConfigurationRoiOffsetY", out reason) ||
            !TryValidateInt(RegionOfInterest.Width, roi.Width, "CameraConfigurationRoiWidth", out reason) ||
            !TryValidateInt(RegionOfInterest.Height, roi.Height, "CameraConfigurationRoiHeight", out reason))
            return false;
        if ((long)roi.OffsetX + roi.Width > RegionOfInterest.SensorWidth ||
            (long)roi.OffsetY + roi.Height > RegionOfInterest.SensorHeight)
        {
            reason = "CameraConfigurationRoiBoundsInvalid";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateInt(CameraIntCapability capability, int value,
        string reasonPrefix, out string reason)
    {
        if (!capability.Readable || !capability.Writable)
        {
            reason = reasonPrefix + "NotReadWrite";
            return false;
        }
        if (value < capability.Minimum || value > capability.Maximum ||
            (value - (long)capability.Minimum) % capability.Increment != 0)
        {
            reason = reasonPrefix + "Unsupported";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryResolveDouble(CameraDoubleCapability capability, double requested,
        CameraNumericSetting setting, out double effective, out string reason)
    {
        var label = setting.ToString();
        if (!capability.Readable || !capability.Writable)
        {
            effective = default;
            reason = "CameraConfiguration" + label + "NotReadWrite";
            return false;
        }
        if (!double.IsFinite(requested) || requested < capability.Minimum || requested > capability.Maximum)
        {
            effective = default;
            reason = "CameraConfiguration" + label + "OutOfRange";
            return false;
        }

        var position = (requested - capability.Minimum) / capability.Increment;
        if (!double.IsFinite(position))
        {
            effective = default;
            reason = "CameraConfiguration" + label + "Unsupported";
            return false;
        }

        if (capability.QuantizationMode == CameraQuantizationMode.Exact)
        {
            var nearest = Math.Round(position, MidpointRounding.ToEven);
            var candidate = capability.Minimum + nearest * capability.Increment;
            if (!double.IsFinite(candidate) || candidate < capability.Minimum ||
                candidate > capability.Maximum)
            {
                effective = default;
                reason = "CameraConfiguration" + label + "QuantizationOutOfRange";
                return false;
            }

            var difference = Math.Abs(requested - candidate);
            if (requested != candidate && (!WithinExactRoundingBound(requested, candidate,
                    capability.Increment, difference)))
            {
                effective = default;
                reason = "CameraConfiguration" + label + "QuantizationRequired";
                return false;
            }

            effective = requested;
            reason = string.Empty;
            return true;
        }

        var quantizedPosition = capability.QuantizationMode switch
        {
            CameraQuantizationMode.Nearest => Math.Round(position, MidpointRounding.ToEven),
            CameraQuantizationMode.Floor => Math.Floor(position),
            CameraQuantizationMode.Ceiling => Math.Ceiling(position),
            _ => throw new InvalidOperationException("CameraQuantizationModeInvalid")
        };
        effective = capability.Minimum + quantizedPosition * capability.Increment;
        if (!double.IsFinite(effective) || effective < capability.Minimum || effective > capability.Maximum)
        {
            reason = "CameraConfiguration" + label + "QuantizationOutOfRange";
            return false;
        }
        if (Math.Abs(effective - requested) > capability.QuantizationTolerance)
        {
            reason = "CameraConfiguration" + label + "QuantizationToleranceExceeded";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool WithinExactRoundingBound(double requested, double candidate,
        double increment, double difference)
    {
        var ulp = Math.Max(CameraCapabilityMath.Ulp(requested),
            CameraCapabilityMath.Ulp(candidate));
        if (!double.IsFinite(ulp) || ulp <= 0 ||
            !double.IsFinite(difference) || difference >= increment / 4)
            return false;
        return difference <= 2 * ulp;
    }

    private static void AddDifference(ICollection<CameraConfigurationDifference> differences,
        CameraNumericSetting setting, double requested, double effective)
    {
        if (requested != effective)
            differences.Add(new CameraConfigurationDifference(setting, requested, effective));
    }

    private string ComputeContentHash()
    {
        var parts = new List<string?> { "sharpinspect-camera-capabilities-v1", "modes" };
        parts.AddRange(_acquisitionModes.Select(mode => ((int)mode).ToString(CultureInfo.InvariantCulture)));
        parts.Add("pixel-formats");
        parts.AddRange(_pixelFormats.Select(format => ((int)format).ToString(CultureInfo.InvariantCulture)));
        parts.Add("mono16-valid-bits");
        parts.AddRange(_mono16ValidBits.Select(bits => bits.ToString(CultureInfo.InvariantCulture)));
        AddDoubleCapability(parts, "exposure-time-us", ExposureTimeUs);
        AddDoubleCapability(parts, "gain-db", GainDb);
        AddDoubleCapability(parts, "trigger-delay-us", TriggerDelayUs);
        parts.AddRange(new[] { "roi", RegionOfInterest.SensorWidth.ToString(CultureInfo.InvariantCulture),
            RegionOfInterest.SensorHeight.ToString(CultureInfo.InvariantCulture) });
        AddIntCapability(parts, "roi-offset-x", RegionOfInterest.OffsetX);
        AddIntCapability(parts, "roi-offset-y", RegionOfInterest.OffsetY);
        AddIntCapability(parts, "roi-width", RegionOfInterest.Width);
        AddIntCapability(parts, "roi-height", RegionOfInterest.Height);
        parts.Add("white-balance");
        if (WhiteBalanceRgb is null)
        {
            parts.Add(null);
        }
        else
        {
            AddDoubleCapability(parts, "red", WhiteBalanceRgb.Red);
            AddDoubleCapability(parts, "green", WhiteBalanceRgb.Green);
            AddDoubleCapability(parts, "blue", WhiteBalanceRgb.Blue);
        }

        return AlgorithmContractValidation.HashParts(parts);
    }

    private static void AddDoubleCapability(ICollection<string?> parts, string name,
        CameraDoubleCapability capability)
    {
        parts.Add(name);
        parts.Add(capability.Minimum.ToString("R", CultureInfo.InvariantCulture));
        parts.Add(capability.Maximum.ToString("R", CultureInfo.InvariantCulture));
        parts.Add(capability.Increment.ToString("R", CultureInfo.InvariantCulture));
        parts.Add(((int)capability.QuantizationMode).ToString(CultureInfo.InvariantCulture));
        parts.Add(capability.QuantizationTolerance.ToString("R", CultureInfo.InvariantCulture));
        parts.Add(capability.Readable ? "1" : "0");
        parts.Add(capability.Writable ? "1" : "0");
    }

    private static void AddIntCapability(ICollection<string?> parts, string name,
        CameraIntCapability capability)
    {
        parts.Add(name);
        parts.Add(capability.Minimum.ToString(CultureInfo.InvariantCulture));
        parts.Add(capability.Maximum.ToString(CultureInfo.InvariantCulture));
        parts.Add(capability.Increment.ToString(CultureInfo.InvariantCulture));
        parts.Add(capability.Readable ? "1" : "0");
        parts.Add(capability.Writable ? "1" : "0");
    }
}

internal static class CameraCapabilityMath
{
    internal static double Ulp(double value)
    {
        if (!double.IsFinite(value))
            return double.NaN;

        var magnitude = Math.Abs(value);
        var next = Math.BitIncrement(magnitude);
        var ulp = next - magnitude;
        if (double.IsFinite(ulp) && ulp > 0)
            return ulp;

        var previous = Math.BitDecrement(magnitude);
        return magnitude - previous;
    }
}
