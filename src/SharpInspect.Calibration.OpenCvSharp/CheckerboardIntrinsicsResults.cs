using System.Collections.ObjectModel;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Calibration.OpenCvSharp;

/// <summary>Pinhole K = [Fx,0,Cx; 0,Fy,Cy; 0,0,1], with ROI-local pixel centers.
/// Brown-Conrady coefficients follow k1,k2,p1,p2,k3 order.</summary>
public sealed class CheckerboardIntrinsicsCoefficients
{
    public CheckerboardIntrinsicsCoefficients(int imageWidth, int imageHeight,
        double fx, double fy, double cx, double cy, double k1, double k2, double p1, double p2, double k3)
    {
        if (imageWidth is < 1 or > 32768 || imageHeight is < 1 or > 32768)
            throw new ArgumentException("CheckerboardImageGeometryInvalid");
        if (new[] { fx, fy, cx, cy, k1, k2, p1, p2, k3 }.Any(value => !double.IsFinite(value)) || fx <= 0 || fy <= 0)
            throw new ArgumentException("CheckerboardCoefficientsInvalid");
        ImageWidth = imageWidth; ImageHeight = imageHeight;
        Fx = fx; Fy = fy; Cx = cx; Cy = cy; K1 = k1; K2 = k2; P1 = p1; P2 = p2; K3 = k3;
    }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public double Fx { get; }
    public double Fy { get; }
    public double Cx { get; }
    public double Cy { get; }
    public double K1 { get; }
    public double K2 { get; }
    public double P1 { get; }
    public double P2 { get; }
    public double K3 { get; }
    public double[,] GetCameraMatrix() => new[,] { { Fx, 0d, Cx }, { 0d, Fy, Cy }, { 0d, 0d, 1d } };
    public double[] GetDistortionCoefficients() => new[] { K1, K2, P1, P2, K3 };
}

/// <summary>Residual = detected minus projected pixel coordinates. The stable ID joins
/// the original immutable observation; it is never an editable correspondence.</summary>
public sealed class CheckerboardPointResidual
{
    internal CheckerboardPointResidual(int cornerIndex, double deltaX, double deltaY)
    {
        if (cornerIndex is < 0 or >= 1024 || !double.IsFinite(deltaX) || !double.IsFinite(deltaY) ||
            !double.IsFinite(deltaX * deltaX + deltaY * deltaY))
            throw new ArgumentException("CheckerboardResidualInvalid");
        CornerIndex = cornerIndex; DeltaX = deltaX; DeltaY = deltaY;
    }
    public int CornerIndex { get; }
    public string StableFeatureId => CheckerboardIntrinsicsProcedure.FeatureId(CornerIndex);
    public double DeltaX { get; }
    public double DeltaY { get; }
    public double MagnitudePixels => Math.Sqrt(DeltaX * DeltaX + DeltaY * DeltaY);
}

/// <summary>All points from one selected view; pose is Rodrigues rotation in radians and
/// board-to-camera translation in millimeters. The origin and corner indexes retain
/// the detector's row-major symmetric-board orientation. Coverage is normalized to image area.</summary>
public sealed class CheckerboardViewEvidence
{
    internal CheckerboardViewEvidence(Guid frameId, string sourceHash, double[] rotation, double[] translation,
        double minimumX, double minimumY, double maximumX, double maximumY, double hullAreaFraction,
        double rmsPixels, IEnumerable<CheckerboardPointResidual> residuals)
    {
        if (frameId == Guid.Empty || sourceHash.Length != 64 ||
            sourceHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("CheckerboardViewIdentityInvalid");
        if (rotation.Length != 3 || translation.Length != 3 || rotation.Concat(translation).Any(v => !double.IsFinite(v)))
            throw new ArgumentException("CheckerboardPoseInvalid");
        if (new[] { minimumX, minimumY, maximumX, maximumY, hullAreaFraction, rmsPixels }.Any(v => !double.IsFinite(v)) ||
            minimumX < 0 || minimumY < 0 || maximumX > 1 || maximumY > 1 || minimumX >= maximumX || minimumY >= maximumY ||
            hullAreaFraction is <= 0 or > 1 || rmsPixels < 0)
            throw new ArgumentException("CheckerboardCoverageInvalid");
        var points = residuals.Take(1025).ToArray();
        if (points.Length is < 9 or > 1024 || points.Where((p, i) => p is null || p.CornerIndex != i).Any())
            throw new ArgumentException("CheckerboardResidualOrderInvalid");
        FrameId = frameId; SourceHash = sourceHash;
        RotationRadians = Array.AsReadOnly((double[])rotation.Clone());
        TranslationMillimeters = Array.AsReadOnly((double[])translation.Clone());
        MinimumX = minimumX; MinimumY = minimumY; MaximumX = maximumX; MaximumY = maximumY;
        HullAreaFraction = hullAreaFraction; RmsPixels = rmsPixels; Residuals = Array.AsReadOnly(points);
    }
    public Guid FrameId { get; }
    public string SourceHash { get; }
    public ReadOnlyCollection<double> RotationRadians { get; }
    public ReadOnlyCollection<double> TranslationMillimeters { get; }
    public double MinimumX { get; }
    public double MinimumY { get; }
    public double MaximumX { get; }
    public double MaximumY { get; }
    public double HullAreaFraction { get; }
    public double RmsPixels { get; }
    public ReadOnlyCollection<CheckerboardPointResidual> Residuals { get; }
}

/// <summary>Mathematical observations only. Invalid extractions and whole-frame exclusions
/// remain in the parent session and its selection hash; this object conveys no acceptance.</summary>
public sealed class CheckerboardIntrinsicsEvidence
{
    internal CheckerboardIntrinsicsEvidence(string inputPayloadHash, double rmsPixels, double constraintRankRatio,
        IEnumerable<CheckerboardViewEvidence> views)
    {
        if (inputPayloadHash.Length != 64 || inputPayloadHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("CheckerboardInputIdentityInvalid");
        if (!double.IsFinite(rmsPixels) || rmsPixels < 0 || !double.IsFinite(constraintRankRatio) || constraintRankRatio is <= 0 or > 1)
            throw new ArgumentException("CheckerboardAggregateEvidenceInvalid");
        var copied = views.Take(65).ToArray();
        if (copied.Length is < 3 or > 64 || copied.Any(v => v is null) ||
            copied.Select(v => v.FrameId).Distinct().Count() != copied.Length ||
            copied.Select(v => v.Residuals.Count).Distinct().Count() != 1)
            throw new ArgumentException("CheckerboardViewEvidenceInvalid");
        InputPayloadHash = inputPayloadHash; RmsPixels = rmsPixels; ConstraintRankRatio = constraintRankRatio;
        Views = Array.AsReadOnly(copied);
    }
    public string InputPayloadHash { get; }
    public double RmsPixels { get; }
    /// <summary>Second-smallest / largest singular value of normalized planar intrinsic constraints.</summary>
    public double ConstraintRankRatio { get; }
    public ReadOnlyCollection<CheckerboardViewEvidence> Views { get; }
    public int PointCount => Views.Sum(view => view.Residuals.Count);
}

/// <summary>Public typed decoders for independently reviewing retained candidates. Binary
/// layouts are little-endian v1 and must roundtrip byte-for-byte; unknown contracts fail.</summary>
public static class CheckerboardIntrinsicsResultCodec
{
    public static CalibrationCoefficientPayload EncodeCoefficients(CheckerboardIntrinsicsCoefficients value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(CheckerboardIntrinsicsContracts.Coefficients, Write(writer =>
        {
            writer.Write(1); writer.Write(value.ImageWidth); writer.Write(value.ImageHeight);
            foreach (var number in new[] { value.Fx, value.Fy, value.Cx, value.Cy }.Concat(value.GetDistortionCoefficients()))
                writer.Write(number);
        }));
    }
    public static CheckerboardIntrinsicsCoefficients DecodeCoefficients(CalibrationCoefficientPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Format != CheckerboardIntrinsicsContracts.Coefficients)
            throw new ArgumentException("CheckerboardCoefficientContractMismatch");
        try
        {
            using var reader = Reader(payload.GetBytes());
            if (reader.ReadInt32() != 1) throw new ArgumentException("CheckerboardCoefficientVersionUnsupported");
            var result = new CheckerboardIntrinsicsCoefficients(reader.ReadInt32(), reader.ReadInt32(),
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            RequireCanonical(payload.GetBytes(), EncodeCoefficients(result).GetBytes());
            return result;
        }
        catch (IOException ex) { throw new ArgumentException("CheckerboardCoefficientPayloadInvalid", ex); }
    }

    public static CalibrationComputationEvidencePayload EncodeEvidence(CheckerboardIntrinsicsEvidence value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(CheckerboardIntrinsicsContracts.Evidence, Write(writer =>
        {
            writer.Write(1); writer.Write(Convert.FromHexString(value.InputPayloadHash));
            writer.Write(value.RmsPixels); writer.Write(value.ConstraintRankRatio); writer.Write(value.Views.Count);
            foreach (var view in value.Views)
            {
                writer.Write(view.FrameId.ToByteArray()); writer.Write(Convert.FromHexString(view.SourceHash));
                foreach (var number in view.RotationRadians.Concat(view.TranslationMillimeters)) writer.Write(number);
                writer.Write(view.MinimumX); writer.Write(view.MinimumY); writer.Write(view.MaximumX); writer.Write(view.MaximumY);
                writer.Write(view.HullAreaFraction); writer.Write(view.RmsPixels); writer.Write(view.Residuals.Count);
                foreach (var point in view.Residuals)
                { writer.Write((ushort)point.CornerIndex); writer.Write(point.DeltaX); writer.Write(point.DeltaY); }
            }
        }));
    }

    public static CheckerboardIntrinsicsEvidence DecodeEvidence(CalibrationComputationEvidencePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Format != CheckerboardIntrinsicsContracts.Evidence)
            throw new ArgumentException("CheckerboardEvidenceContractMismatch");
        try
        {
            using var reader = Reader(payload.GetBytes());
            if (reader.ReadInt32() != 1) throw new ArgumentException("CheckerboardEvidenceVersionUnsupported");
            var inputHash = Convert.ToHexString(ReadExactly(reader, 32));
            var rms = reader.ReadDouble(); var rank = reader.ReadDouble(); var count = reader.ReadInt32();
            if (count is < 3 or > 64) throw new ArgumentException("CheckerboardViewEvidenceInvalid");
            var views = new List<CheckerboardViewEvidence>(count);
            for (var i = 0; i < count; i++)
            {
                var frame = new Guid(ReadExactly(reader, 16)); var source = Convert.ToHexString(ReadExactly(reader, 32));
                var rotation = new[] { reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble() };
                var translation = new[] { reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble() };
                var minX = reader.ReadDouble(); var minY = reader.ReadDouble();
                var maxX = reader.ReadDouble(); var maxY = reader.ReadDouble();
                var area = reader.ReadDouble(); var perViewRms = reader.ReadDouble(); var pointCount = reader.ReadInt32();
                if (pointCount is < 9 or > 1024) throw new ArgumentException("CheckerboardResidualCountInvalid");
                var points = new List<CheckerboardPointResidual>(pointCount);
                for (var j = 0; j < pointCount; j++) points.Add(new(reader.ReadUInt16(), reader.ReadDouble(), reader.ReadDouble()));
                views.Add(new(frame, source, rotation, translation, minX, minY, maxX, maxY, area, perViewRms, points));
            }
            var result = new CheckerboardIntrinsicsEvidence(inputHash, rms, rank, views);
            RequireCanonical(payload.GetBytes(), EncodeEvidence(result).GetBytes());
            return result;
        }
        catch (IOException ex) { throw new ArgumentException("CheckerboardEvidencePayloadInvalid", ex); }
    }
    private static byte[] Write(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        write(writer); writer.Flush(); return stream.ToArray();
    }
    private static BinaryReader Reader(byte[] bytes) => new(new MemoryStream(bytes), Encoding.UTF8);
    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new EndOfStreamException();
    }
    private static void RequireCanonical(byte[] supplied, byte[] canonical)
    {
        if (!supplied.AsSpan().SequenceEqual(canonical)) throw new ArgumentException("CheckerboardResultNoncanonical");
    }
}
