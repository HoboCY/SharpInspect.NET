using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>
/// The canonical, typed input supplied to a calibration procedure. The contract is
/// carried beside the bytes so a procedure can reject an unknown input format before
/// decoding it.
/// </summary>
public sealed class CalibrationProcedureInputPayload
{
    public const int MaximumBytes = 64 * 1024;

    private readonly byte[] _canonicalBytes;

    public CalibrationProcedureInputPayload(RecipeContractReference inputContract,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        InputContract = inputContract ?? throw new ArgumentNullException(nameof(inputContract));
        if (canonicalBytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("CalibrationProcedureInputPayloadSizeInvalid", nameof(canonicalBytes));

        _canonicalBytes = canonicalBytes.ToArray();
        CanonicalBytesHash = Convert.ToHexString(SHA256.HashData(_canonicalBytes));
        // The bytes identify the encoded value; the complete identity also binds the
        // exact versioned input contract which defines how those bytes are interpreted.
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-procedure-input-payload-v1",
            InputContract.Id, InputContract.Version, InputContract.ContentHash, CanonicalBytesHash
        });
    }

    public RecipeContractReference InputContract { get; }
    public int Length => _canonicalBytes.Length;

    /// <summary>SHA-256 of the exact canonical bytes, independent of their contract.</summary>
    public string CanonicalBytesHash { get; }

    /// <summary>SHA-256 identity of the exact input contract and canonical bytes.</summary>
    public string ContentHash { get; }

    /// <summary>Returns a fresh defensive copy of the exact canonical bytes.</summary>
    public byte[] GetBytes() => (byte[])_canonicalBytes.Clone();
}

/// <summary>Maps one exact versioned input contract to and from typed procedure input.</summary>
public interface ICalibrationInputCodec<TInput>
{
    RecipeContractReference InputContract { get; }

    TInput Decode(ReadOnlyMemory<byte> canonicalBytes);
    ReadOnlyMemory<byte> Encode(TInput input);
}

/// <summary>Helpers that keep codec contract matching at the typed payload boundary.</summary>
public static class CalibrationInputCodecExtensions
{
    public static CalibrationProcedureInputPayload EncodePayload<TInput>(
        this ICalibrationInputCodec<TInput> codec, TInput input)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new CalibrationProcedureInputPayload(codec.InputContract, codec.Encode(input));
    }

    public static TInput Decode<TInput>(this ICalibrationInputCodec<TInput> codec,
        CalibrationProcedureInputPayload payload)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.InputContract != codec.InputContract)
            throw new ArgumentException("CalibrationInputContractMismatch", nameof(payload));

        return codec.Decode(payload.GetBytes());
    }
}

/// <summary>Exact registration identity for one replaceable calibration procedure.</summary>
public sealed class CalibrationProcedureDescriptor
{
    public CalibrationProcedureDescriptor(RecipeContractReference procedure,
        RecipeContractReference inputContract, CalibrationKind calibrationKind)
    {
        Procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
        InputContract = inputContract ?? throw new ArgumentNullException(nameof(inputContract));
        CalibrationKind = AlgorithmConfigurationValidation.Enum(calibrationKind, nameof(calibrationKind));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-procedure-descriptor-v1",
            Procedure.Id, Procedure.Version, Procedure.ContentHash,
            InputContract.Id, InputContract.Version, InputContract.ContentHash,
            CalibrationKind.ToString()
        });
    }

    public RecipeContractReference Procedure { get; }
    public RecipeContractReference InputContract { get; }
    public CalibrationKind CalibrationKind { get; }
    public string ContentHash { get; }
}

/// <summary>One automatically detected calibration feature in frame pixel coordinates.</summary>
public sealed class CalibrationImageFeature
{
    public CalibrationImageFeature(string stableFeatureId, double pixelX, double pixelY)
    {
        StableFeatureId = AlgorithmContractValidation.Identifier(stableFeatureId,
            nameof(stableFeatureId));
        AlgorithmContractValidation.Finite(pixelX, nameof(pixelX));
        AlgorithmContractValidation.Finite(pixelY, nameof(pixelY));
        PixelX = pixelX == 0d ? 0d : pixelX;
        PixelY = pixelY == 0d ? 0d : pixelY;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-image-feature-v1", StableFeatureId,
            PixelX.ToString("R", CultureInfo.InvariantCulture),
            PixelY.ToString("R", CultureInfo.InvariantCulture)
        });
    }

    public string StableFeatureId { get; }
    public double PixelX { get; }
    public double PixelY { get; }
    public string ContentHash { get; }
}

/// <summary>A bounded, stable diagnostic emitted by a calibration procedure.</summary>
public sealed class CalibrationProcedureDiagnostic
{
    public CalibrationProcedureDiagnostic(string key, string? value = null)
    {
        Key = AlgorithmContractValidation.Identifier(key, nameof(key));
        Value = value is null ? null : AlgorithmContractValidation.BoundedText(value, nameof(value), 256);
    }

    public string Key { get; }
    public string? Value { get; }
}

/// <summary>A finite, bounded quality value with an optional unit label.</summary>
public sealed class CalibrationQualityMetric
{
    public CalibrationQualityMetric(string key, double value, string? unit = null)
    {
        Key = AlgorithmContractValidation.Identifier(key, nameof(key));
        AlgorithmContractValidation.Finite(value, nameof(value));
        Value = value == 0d ? 0d : value;
        Unit = unit is null ? null : AlgorithmContractValidation.BoundedText(unit, nameof(unit), 64);
    }

    public string Key { get; }
    public double Value { get; }
    public string? Unit { get; }
}

/// <summary>
/// The only inputs available during automatic feature extraction. The frame remains a
/// framework-owned borrowed frame for the duration of the procedure call.
/// </summary>
public sealed class CalibrationExtractionContext<TInput>
{
    public CalibrationExtractionContext(VisionFrame frame, TInput input, Guid sessionId,
        Guid frameId, string sourceHash)
    {
        Frame = ValidateFrame(frame);
        Input = input is null ? throw new ArgumentNullException(nameof(input)) : input;
        SessionId = ValidateId(sessionId, nameof(sessionId));
        FrameId = ValidateId(frameId, nameof(frameId));
        SourceHash = ValidateHash(sourceHash, nameof(sourceHash));
    }

    public VisionFrame Frame { get; }
    public TInput Input { get; }
    public Guid SessionId { get; }
    public Guid FrameId { get; }
    public string SourceHash { get; }

    private static VisionFrame ValidateFrame(VisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsLoanActive)
            throw new InvalidOperationException("CalibrationFrameLoanInactive");
        return frame;
    }

    private static Guid ValidateId(Guid value, string parameterName) =>
        value == Guid.Empty
            ? throw new ArgumentException("CalibrationIdentityInvalid", parameterName)
            : value;

    private static string ValidateHash(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')))
            throw new ArgumentException("CalibrationSourceHashInvalid", parameterName);
        return value;
    }
}

/// <summary>Automatic feature extraction output. Runtime assigns observation identity later.</summary>
public sealed class CalibrationExtractionResult
{
    public const int MaximumFeatureCount = 4096;
    public const int MaximumDiagnosticCount = 32;

    public CalibrationExtractionResult(IEnumerable<CalibrationImageFeature>? features,
        IEnumerable<CalibrationProcedureDiagnostic>? diagnostics = null)
    {
        Features = CopyFeatures(features, nameof(features));
        Diagnostics = CopyDiagnostics(diagnostics, nameof(diagnostics));
    }

    public ReadOnlyCollection<CalibrationImageFeature> Features { get; }
    public ReadOnlyCollection<CalibrationProcedureDiagnostic> Diagnostics { get; }

    private static ReadOnlyCollection<CalibrationImageFeature> CopyFeatures(
        IEnumerable<CalibrationImageFeature>? values, string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName, MaximumFeatureCount);
        if (copied.Select(feature => feature.StableFeatureId)
            .Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("CalibrationFeatureDuplicate", parameterName);
        return copied;
    }

    private static ReadOnlyCollection<CalibrationProcedureDiagnostic> CopyDiagnostics(
        IEnumerable<CalibrationProcedureDiagnostic>? values, string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName, MaximumDiagnosticCount);
        if (copied.Select(diagnostic => diagnostic.Key)
            .Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("CalibrationDiagnosticDuplicate", parameterName);
        return copied;
    }
}

/// <summary>
/// One immutable observation input selected by Runtime for computation. ObservationId is
/// deliberately absent: Runtime allocates and binds that durable identity.
/// </summary>
public sealed class CalibrationObservationInput
{
    public CalibrationObservationInput(VisionFrame frame,
        IEnumerable<CalibrationImageFeature>? features, Guid sessionId, Guid frameId,
        string sourceHash)
    {
        Frame = ValidateFrame(frame);
        Features = CopyFeatures(features, nameof(features));
        SessionId = ValidateId(sessionId, nameof(sessionId));
        FrameId = ValidateId(frameId, nameof(frameId));
        SourceHash = ValidateHash(sourceHash, nameof(sourceHash));
    }

    public VisionFrame Frame { get; }
    public ReadOnlyCollection<CalibrationImageFeature> Features { get; }
    public Guid SessionId { get; }
    public Guid FrameId { get; }
    public string SourceHash { get; }

    private static VisionFrame ValidateFrame(VisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsLoanActive)
            throw new InvalidOperationException("CalibrationFrameLoanInactive");
        return frame;
    }

    private static Guid ValidateId(Guid value, string parameterName) =>
        value == Guid.Empty
            ? throw new ArgumentException("CalibrationIdentityInvalid", parameterName)
            : value;

    private static ReadOnlyCollection<CalibrationImageFeature> CopyFeatures(
        IEnumerable<CalibrationImageFeature>? values, string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName,
            CalibrationExtractionResult.MaximumFeatureCount);
        if (copied.Select(feature => feature.StableFeatureId)
            .Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("CalibrationFeatureDuplicate", parameterName);
        return copied;
    }

    private static string ValidateHash(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')))
            throw new ArgumentException("CalibrationSourceHashInvalid", parameterName);
        return value;
    }
}

/// <summary>Typed procedure input plus the selected immutable borrowed observations.</summary>
public sealed class CalibrationComputationContext<TInput>
{
    public const int MaximumObservationCount = 64;

    public CalibrationComputationContext(TInput input,
        IEnumerable<CalibrationObservationInput>? observations)
    {
        Input = input is null ? throw new ArgumentNullException(nameof(input)) : input;
        Observations = CopyObservations(observations, nameof(observations));
    }

    public TInput Input { get; }
    public ReadOnlyCollection<CalibrationObservationInput> Observations { get; }

    private static ReadOnlyCollection<CalibrationObservationInput> CopyObservations(
        IEnumerable<CalibrationObservationInput>? values, string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName, MaximumObservationCount);
        if (copied.Select(observation => observation.FrameId)
            .Distinct().Count() != copied.Count)
            throw new ArgumentException("CalibrationObservationDuplicate", parameterName);
        if (copied.Select(observation => observation.SessionId).Distinct().Count() > 1)
            throw new ArgumentException("CalibrationObservationSessionMismatch", parameterName);
        return copied;
    }
}

/// <summary>Procedure output retained as candidate evidence before policy evaluation.</summary>
public sealed class CalibrationProcedureComputationResult
{
    public const int MaximumMetricCount = 32;
    public const int MaximumDiagnosticCount = 32;

    public CalibrationProcedureComputationResult(CalibrationCoefficientPayload coefficients,
        IEnumerable<CalibrationQualityMetric>? qualityMetrics = null,
        IEnumerable<CalibrationProcedureDiagnostic>? diagnostics = null)
    {
        Coefficients = coefficients ?? throw new ArgumentNullException(nameof(coefficients));
        QualityMetrics = CopyMetrics(qualityMetrics, nameof(qualityMetrics));
        Diagnostics = CopyDiagnostics(diagnostics, nameof(diagnostics));
    }

    public CalibrationCoefficientPayload Coefficients { get; }
    public ReadOnlyCollection<CalibrationQualityMetric> QualityMetrics { get; }
    public ReadOnlyCollection<CalibrationProcedureDiagnostic> Diagnostics { get; }

    private static ReadOnlyCollection<CalibrationQualityMetric> CopyMetrics(
        IEnumerable<CalibrationQualityMetric>? values, string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName, MaximumMetricCount);
        if (copied.Select(metric => metric.Key)
            .Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("CalibrationMetricDuplicate", parameterName);
        return copied;
    }

    private static ReadOnlyCollection<CalibrationProcedureDiagnostic> CopyDiagnostics(
        IEnumerable<CalibrationProcedureDiagnostic>? values, string parameterName)
    {
        var copied = AlgorithmContractValidation.Copy(values, parameterName, MaximumDiagnosticCount);
        if (copied.Select(diagnostic => diagnostic.Key)
            .Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("CalibrationDiagnosticDuplicate", parameterName);
        return copied;
    }
}

/// <summary>
/// Pluggable calibration mathematics. Implementations receive only framework-owned
/// borrowed frames and immutable DTOs; Runtime owns session, evidence, policy and
/// publication decisions.
/// </summary>
public interface ICalibrationProcedure<TInput>
{
    CalibrationProcedureDescriptor Descriptor { get; }
    ICalibrationInputCodec<TInput> InputCodec { get; }

    ValueTask<CalibrationExtractionResult> ExtractAsync(
        CalibrationExtractionContext<TInput> context,
        CancellationToken cancellationToken = default);

    ValueTask<CalibrationProcedureComputationResult> ComputeAsync(
        CalibrationComputationContext<TInput> context,
        CancellationToken cancellationToken = default);
}
