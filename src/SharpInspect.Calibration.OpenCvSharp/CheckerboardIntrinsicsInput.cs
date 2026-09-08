using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp;

/// <summary>Board dimensions count inner corners. Board-space units are millimeters;
/// frame coordinates are ROI-local pixels. A symmetric board has no marked absolute origin.</summary>
public sealed class CheckerboardIntrinsicsInput
{
    public CheckerboardIntrinsicsInput(int innerColumns, int innerRows, double squareSizeMillimeters,
        string logicalCameraRole, EffectiveCameraConfiguration expectedConfiguration)
    {
        if (innerColumns is < 3 or > 32 || innerRows is < 3 or > 32)
            throw new ArgumentException("CheckerboardGeometryInvalid");
        if (!double.IsFinite(squareSizeMillimeters) || squareSizeMillimeters is < 0.001 or > 100_000)
            throw new ArgumentException("CheckerboardSquareSizeInvalid");
        // Match the public frame identifier grammar, so every canonical input role
        // can identify a frame without encoding replacement or normalization.
        if (string.IsNullOrEmpty(logicalCameraRole) || logicalCameraRole.Length > 64 ||
            logicalCameraRole.Any(character => character is not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw new ArgumentException("CheckerboardCameraRoleInvalid");
        InnerColumns = innerColumns; InnerRows = innerRows; SquareSizeMillimeters = squareSizeMillimeters;
        LogicalCameraRole = logicalCameraRole;
        ExpectedConfiguration = expectedConfiguration ?? throw new ArgumentNullException(nameof(expectedConfiguration));
    }
    public int InnerColumns { get; }
    public int InnerRows { get; }
    public int CornerCount => InnerColumns * InnerRows;
    public double SquareSizeMillimeters { get; }
    public string LogicalCameraRole { get; }
    public EffectiveCameraConfiguration ExpectedConfiguration { get; }
    public int ImageWidth => ExpectedConfiguration.RegionOfInterest.Width;
    public int ImageHeight => ExpectedConfiguration.RegionOfInterest.Height;
}

public static class CheckerboardIntrinsicsContracts
{
    public static RecipeContractReference Input { get; } = Contract("sharpinspect.checkerboard-intrinsics-input",
        "v1:LE-binary;cols,rows:int32;square-mm:float64;role:utf8;complete-effective-configuration;ROI-local-pixels");
    public static RecipeContractReference Coefficients { get; } = Contract("sharpinspect.checkerboard-intrinsics-coefficients",
        "v1:LE-binary;image-width,height:int32;fx,fy,cx,cy,k1,k2,p1,p2,k3:float64;Brown-Conrady;ROI-local-pixels");
    public static RecipeContractReference Evidence { get; } = Contract("sharpinspect.checkerboard-intrinsics-evidence",
        "v1:LE-binary;input-payload-hash;global-RMS;normalized-constraint-rank-ratio;views;frame-id,source-hash;rotation-radians,translation-mm;coverage;retained-detector-row-major-symmetric-origin;corner-index,observed-minus-projected-dx-dy-pixels");
    public static RecipeContractReference ExtractionReceipt { get; } = Contract("sharpinspect.checkerboard-extraction-receipt",
        "v1:HMAC-SHA256-32bytes;process-random-256bit-key;ASCII-domain:sharpinspect-checkerboard-extraction-receipt-v1;LE-int32-length-prefixed-UTF8-text;procedure-contract-id-version-hash;input-contract-id-version-hash;input-canonical-hash,content-hash;LE-int32-input-byte-length,input-bytes;session,frame:Guid.ToByteArray;source-hash,valid-row-pixel-SHA256;LE-int32-feature-count,ordered-feature-content-hashes;no-restart-computation-authority");
    public static RecipeContractReference Procedure { get; } = Contract("sharpinspect.checkerboard-intrinsics",
        "v1:OpenCvSharp4-4.11.0.20250506;SB-Accuracy-Exhaustive-NormalizeImage;process-HMAC-extraction-receipt;no-compute-reextraction;Brown5-free;CalibrateCamera-100-1e-10;all-selected-views-and-corners;no-production-acceptance");

    private static RecipeContractReference Contract(string id, string specification) =>
        new(id, "1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specification))));
}

/// <summary>Strict canonical binary encoding; unknown/trailing/noncanonical bytes are rejected.</summary>
public sealed class CheckerboardIntrinsicsInputCodec : ICalibrationInputCodec<CheckerboardIntrinsicsInput>
{
    public RecipeContractReference InputContract => CheckerboardIntrinsicsContracts.Input;
    public ReadOnlyMemory<byte> Encode(CheckerboardIntrinsicsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(1); writer.Write(input.InnerColumns); writer.Write(input.InnerRows);
        writer.Write(input.SquareSizeMillimeters); writer.Write(input.LogicalCameraRole);
        var c = input.ExpectedConfiguration;
        writer.Write((int)c.ProductionAcquisitionMode); writer.Write(c.ExposureTimeUs); writer.Write(c.GainDb);
        writer.Write(c.RegionOfInterest.OffsetX); writer.Write(c.RegionOfInterest.OffsetY);
        writer.Write(c.RegionOfInterest.Width); writer.Write(c.RegionOfInterest.Height);
        writer.Write((int)c.PixelFormat); writer.Write(c.ValidBits ?? 0);
        writer.Write(c.AcquisitionTimeoutMs); writer.Write(c.TriggerDelayUs);
        writer.Write(c.WhiteBalanceRgb is not null);
        if (c.WhiteBalanceRgb is { } wb) { writer.Write(wb.Red); writer.Write(wb.Green); writer.Write(wb.Blue); }
        writer.Flush(); return stream.ToArray();
    }

    public CheckerboardIntrinsicsInput Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length is < 1 or > CalibrationProcedureInputPayload.MaximumBytes)
            throw new ArgumentException("CheckerboardInputPayloadInvalid");
        try
        {
            using var reader = new BinaryReader(new MemoryStream(canonicalBytes.ToArray()), Encoding.UTF8);
            if (reader.ReadInt32() != 1) throw new ArgumentException("CheckerboardInputVersionUnsupported");
            var columns = reader.ReadInt32(); var rows = reader.ReadInt32();
            var square = reader.ReadDouble(); var role = reader.ReadString();
            var mode = (ProductionAcquisitionMode)reader.ReadInt32();
            var exposure = reader.ReadDouble(); var gain = reader.ReadDouble();
            var roi = new RegionOfInterest(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var pixelFormat = (VisionPixelFormat)reader.ReadInt32(); var bits = reader.ReadInt32();
            var timeout = reader.ReadInt32(); var delay = reader.ReadDouble();
            var wb = reader.ReadBoolean() ? new WhiteBalanceRgb(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()) : null;
            var result = new CheckerboardIntrinsicsInput(columns, rows, square, role,
                new EffectiveCameraConfiguration(mode, exposure, gain, roi, pixelFormat, bits == 0 ? null : bits,
                    timeout, delay, wb));
            if (!Encode(result).Span.SequenceEqual(canonicalBytes.Span))
                throw new ArgumentException("CheckerboardInputNoncanonical");
            return result;
        }
        catch (IOException ex) { throw new ArgumentException("CheckerboardInputPayloadInvalid", ex); }
    }
}
