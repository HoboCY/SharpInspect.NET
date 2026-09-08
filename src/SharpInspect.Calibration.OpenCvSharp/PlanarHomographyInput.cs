using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp;

/// <summary>Coordinates have the units and axes declared by their containing contract.</summary>
public readonly record struct PlanarPoint(double X, double Y);

public enum PlanarPixelDomain
{
    Unknown = 0,
    RawRoiPixelCentersCorrectionDeclaredNotRequired = 1,
    ExactIntrinsicCorrectionRequired = 2
}

/// <summary>Frozen target-design coordinates in millimeters, in decoded marker-local
/// top-left, top-right, bottom-right, bottom-left order. These are not editable image observations.</summary>
public sealed class ArucoPlanarMarkerDefinition
{
    public ArucoPlanarMarkerDefinition(int markerId, IEnumerable<PlanarPoint> physicalCornersMillimeters)
    {
        if (markerId is < 0 or > 49) throw new ArgumentException("PlanarMarkerIdInvalid");
        ArgumentNullException.ThrowIfNull(physicalCornersMillimeters);
        var points = physicalCornersMillimeters.Take(5).ToArray();
        if (points.Length != 4 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                Math.Abs(point.X) > 1_000_000 || Math.Abs(point.Y) > 1_000_000) || points.Distinct().Count() != 4)
            throw new ArgumentException("PlanarMarkerCoordinatesInvalid");
        // A decoded square must map to a strictly convex quadrilateral. Either
        // orientation is valid: a physical coordinate frame may have Y pointing up.
        var signs = Enumerable.Range(0, 4).Select(index => Cross(points[index], points[(index + 1) % 4],
            points[(index + 2) % 4])).ToArray();
        if (signs.Any(value => !double.IsFinite(value) || value == 0) ||
            signs.Any(value => Math.Sign(value) != Math.Sign(signs[0])))
            throw new ArgumentException("PlanarMarkerGeometryDegenerate");
        MarkerId = markerId; PhysicalCornersMillimeters = Array.AsReadOnly(points);
    }
    public int MarkerId { get; }
    public ReadOnlyCollection<PlanarPoint> PhysicalCornersMillimeters { get; }
    private static double Cross(PlanarPoint a, PlanarPoint b, PlanarPoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}

/// <summary>DICT_4X4_50, one black border cell. Marker IDs and their decoded
/// orientations define a physical plane coordinate frame before acquisition.</summary>
public sealed class ArucoPlanarTargetDefinition
{
    public ArucoPlanarTargetDefinition(string targetDefinitionId, string planeCoordinateFrameId,
        IEnumerable<ArucoPlanarMarkerDefinition> markers)
    {
        TargetDefinitionId = PlanarInputValidation.Identifier(targetDefinitionId, "PlanarTargetIdInvalid");
        PlaneCoordinateFrameId = PlanarInputValidation.Identifier(planeCoordinateFrameId, "PlanarCoordinateFrameIdInvalid");
        ArgumentNullException.ThrowIfNull(markers);
        var copied = markers.Take(51).ToArray();
        if (copied.Length is < 1 or > 50 || copied.Any(marker => marker is null) ||
            copied.Select(marker => marker.MarkerId).Distinct().Count() != copied.Length)
            throw new ArgumentException("PlanarTargetMarkersInvalid");
        var points = copied.SelectMany(marker => marker.PhysicalCornersMillimeters).ToArray();
        if (points.Distinct().Count() != points.Length) throw new ArgumentException("PlanarTargetDuplicateCoordinates");
        Markers = Array.AsReadOnly(copied.OrderBy(marker => marker.MarkerId).ToArray());
    }
    public string TargetDefinitionId { get; }
    public string PlaneCoordinateFrameId { get; }
    public string PhysicalUnit => "Millimeter";
    public string DictionaryName => "DICT_4X4_50";
    public int BorderBits => 1;
    public ReadOnlyCollection<ArucoPlanarMarkerDefinition> Markers { get; }
}

/// <summary>Raw ROI-local pixel centers: origin (0,0), X right, Y down. The caller
/// explicitly declares that intrinsic correction is not required. The governed
/// session header binds the exact imaging revision; this input confers no validity authority.</summary>
public sealed class PlanarHomographyInput
{
    public PlanarHomographyInput(string logicalCameraRole, EffectiveCameraConfiguration expectedConfiguration,
        PlanarPixelDomain pixelDomain, ArucoPlanarTargetDefinition target)
    {
        LogicalCameraRole = PlanarInputValidation.Identifier(logicalCameraRole, "PlanarCameraRoleInvalid");
        ExpectedConfiguration = expectedConfiguration ?? throw new ArgumentNullException(nameof(expectedConfiguration));
        if (pixelDomain == PlanarPixelDomain.ExactIntrinsicCorrectionRequired)
            throw new CalibrationProcedureException("PlanarIntrinsicCorrectionUnsupported");
        if (pixelDomain != PlanarPixelDomain.RawRoiPixelCentersCorrectionDeclaredNotRequired)
            throw new ArgumentException("PlanarPixelDomainInvalid");
        PixelDomain = pixelDomain; Target = target ?? throw new ArgumentNullException(nameof(target));
    }
    public string LogicalCameraRole { get; }
    public EffectiveCameraConfiguration ExpectedConfiguration { get; }
    public PlanarPixelDomain PixelDomain { get; }
    public ArucoPlanarTargetDefinition Target { get; }
    public int ImageWidth => ExpectedConfiguration.RegionOfInterest.Width;
    public int ImageHeight => ExpectedConfiguration.RegionOfInterest.Height;
}

public static class PlanarHomographyContracts
{
    public static RecipeContractReference Input { get; } = Contract("sharpinspect.planar-homography-input",
        "v1:LE-binary;role;full-effective-config;explicit-raw-ROI-domain-no-required-intrinsic-correction;DICT_4X4_50-border1;target-id,plane-frame-id;sorted-marker-id,decoded-LT-RT-RB-LB-physical-mm;immutable-target-design");
    public static RecipeContractReference Coefficients { get; } = Contract("sharpinspect.planar-homography-raw-roi-coefficients",
        "v1:LE-binary;input-content-hash,complete-input;ROI-local-pixel-center-coordinates;top-left-center-0,0;subpixel-doubles;X-right,Y-down;plane-mm;image-to-plane-H,plane-to-image-H;matrix-maxabs1-first-max-positive;adjugate-inverse;determinant-six-product-expansion-and-horizon-relative-guard-64x2.2204460492503131e-16;actual-image-and-physical-convex-hulls;canonical-CCW;independent-source-hull-inclusive-relative-cross-roundoff-64eps;no-profile-authority;local-Jacobian-mm-per-pixel");
    public static RecipeContractReference Evidence { get; } = Contract("sharpinspect.planar-homography-evidence",
        "v1:LE-binary;input-hash;frame,source;normalized-DLT-rank-ratio;inverse-closure-max-roundtrip-distance-over-max1-source-norm-in-each-domain;ordered-marker-corners;raw-pixels,declared-physical-mm;observed-physical-minus-image-to-plane-mm,observed-pixel-minus-plane-to-image-px;missing-marker-ids;no-production-acceptance");
    public static RecipeContractReference ExtractionReceipt { get; } = Contract("sharpinspect.planar-extraction-receipt",
        "v1:HMAC-SHA256-32bytes;process-random-256bit-key;domain:sharpinspect-planar-extraction-receipt-v1;LE-int32-length-prefixed-UTF8;procedure-and-input-contracts;input-canonical-and-content-hashes,bytes;session,frame-Guid.ToByteArray;source,valid-row-pixel-SHA256;ordered-feature-hashes;no-restart-computation-authority");
    // The pinned detector parameters are part of this procedure's implementation.
    // Any change to those parameters requires a new descriptor contract.
    public static RecipeContractReference Procedure { get; } = Contract("sharpinspect.planar-homography",
        "v1:OpenCvSharp4-4.11.0.20250506;DICT_4X4_50-border1;native-decoded-canonical-corners;one-selected-frame;process-HMAC-no-reextraction;normalized-DLT-eighth-largest-rank-ratio-floor-1e-5;all-points-nonrobust-FindHomography-None;raw-ROI-no-intrinsic-correction;actual-hulls;no-production-acceptance;" +
        "AdaptiveThreshWinSizeMin=3;AdaptiveThreshWinSizeMax=23;AdaptiveThreshWinSizeStep=10;AdaptiveThreshConstant=7;MinMarkerPerimeterRate=0.03;MaxMarkerPerimeterRate=4;PolygonalApproxAccuracyRate=0.03;MinCornerDistanceRate=0.05;MinDistanceToBorder=3;MinMarkerDistanceRate=0.05;CornerRefinementMethod=Subpix;CornerRefinementWinSize=5;CornerRefinementMaxIterations=30;CornerRefinementMinAccuracy=0.001;MarkerBorderBits=1;PerspectiveRemovePixelPerCell=8;PerspectiveRemoveIgnoredMarginPerCell=0.13;MaxErroneousBitsInBorderRate=0.35;MinOtsuStdDev=5;ErrorCorrectionRate=0.6;AprilTagQuadDecimate=0;AprilTagQuadSigma=0;AprilTagMinClusterPixels=5;AprilTagMaxNmaxima=10;AprilTagCriticalRad=0.17453292;AprilTagMaxLineFitMse=10;AprilTagMinWhiteBlackDiff=5;AprilTagDeglitch=0;DetectInvertedMarker=false;UseAruco3Detection=false;MinSideLengthCanonicalImg=32;MinMarkerLengthRatioOriginalImg=0");
    private static RecipeContractReference Contract(string id, string specification) =>
        new(id, "1", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(specification))));
}

public sealed class PlanarHomographyInputCodec : ICalibrationInputCodec<PlanarHomographyInput>
{
    public RecipeContractReference InputContract => PlanarHomographyContracts.Input;
    public ReadOnlyMemory<byte> Encode(PlanarHomographyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(1); writer.Write(input.LogicalCameraRole);
        var c = input.ExpectedConfiguration;
        writer.Write((int)c.ProductionAcquisitionMode); writer.Write(c.ExposureTimeUs); writer.Write(c.GainDb);
        writer.Write(c.RegionOfInterest.OffsetX); writer.Write(c.RegionOfInterest.OffsetY);
        writer.Write(c.RegionOfInterest.Width); writer.Write(c.RegionOfInterest.Height);
        writer.Write((int)c.PixelFormat); writer.Write(c.ValidBits ?? 0);
        writer.Write(c.AcquisitionTimeoutMs); writer.Write(c.TriggerDelayUs); writer.Write(c.WhiteBalanceRgb is not null);
        if (c.WhiteBalanceRgb is { } wb) { writer.Write(wb.Red); writer.Write(wb.Green); writer.Write(wb.Blue); }
        writer.Write((int)input.PixelDomain); writer.Write(input.Target.TargetDefinitionId);
        writer.Write(input.Target.PlaneCoordinateFrameId); writer.Write(input.Target.Markers.Count);
        foreach (var marker in input.Target.Markers)
        {
            writer.Write(marker.MarkerId);
            foreach (var point in marker.PhysicalCornersMillimeters) { writer.Write(point.X); writer.Write(point.Y); }
        }
        writer.Flush(); return stream.ToArray();
    }
    public PlanarHomographyInput Decode(ReadOnlyMemory<byte> canonicalBytes)
    {
        if (canonicalBytes.Length is < 1 or > CalibrationProcedureInputPayload.MaximumBytes)
            throw new ArgumentException("PlanarInputPayloadInvalid");
        try
        {
            using var reader = new BinaryReader(new MemoryStream(canonicalBytes.ToArray()), Encoding.UTF8);
            if (reader.ReadInt32() != 1) throw new ArgumentException("PlanarInputVersionUnsupported");
            var role = reader.ReadString(); var mode = (ProductionAcquisitionMode)reader.ReadInt32();
            var exposure = reader.ReadDouble(); var gain = reader.ReadDouble();
            var roi = new RegionOfInterest(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var pixelFormat = (VisionPixelFormat)reader.ReadInt32(); var bits = reader.ReadInt32();
            var timeout = reader.ReadInt32(); var delay = reader.ReadDouble();
            var wb = reader.ReadBoolean() ? new WhiteBalanceRgb(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()) : null;
            var configuration = new EffectiveCameraConfiguration(mode, exposure, gain, roi, pixelFormat,
                bits == 0 ? null : bits, timeout, delay, wb);
            var domain = (PlanarPixelDomain)reader.ReadInt32(); var targetId = reader.ReadString(); var frameId = reader.ReadString();
            var count = reader.ReadInt32();
            if (count is < 1 or > 50) throw new ArgumentException("PlanarTargetMarkersInvalid");
            var markers = new List<ArucoPlanarMarkerDefinition>(count);
            for (var index = 0; index < count; index++)
            {
                var id = reader.ReadInt32(); var points = new PlanarPoint[4];
                for (var corner = 0; corner < 4; corner++) points[corner] = new(reader.ReadDouble(), reader.ReadDouble());
                markers.Add(new(id, points));
            }
            var result = new PlanarHomographyInput(role, configuration, domain, new(targetId, frameId, markers));
            if (!Encode(result).Span.SequenceEqual(canonicalBytes.Span)) throw new ArgumentException("PlanarInputNoncanonical");
            return result;
        }
        catch (IOException ex) { throw new ArgumentException("PlanarInputPayloadInvalid", ex); }
    }
}

internal static class PlanarInputValidation
{
    internal static string Identifier(string value, string reason)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(character =>
                character is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and not '.' and not '_' and not '-'))
            throw new ArgumentException(reason);
        return value;
    }
}
