using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>Immutable session admission provenance. Only Runtime creates an admitted header.</summary>
public sealed class CalibrationSessionHeader
{
    internal CalibrationSessionHeader(Guid sessionId, Guid runtimeEpoch, Guid attemptId,
        Guid actorPrincipalId, Guid interactiveSessionId, long authorizationRevision,
        DateTimeOffset admittedAtUtc, StartCalibrationSessionCommand command,
        CameraBindingRevision binding, RequestedCameraConfiguration baselineRequested,
        EffectiveCameraConfiguration baselineEffective, string developmentFixtureHash)
    {
        SessionId = CalibrationEvidenceValidation.Id(sessionId);
        RuntimeEpoch = CalibrationEvidenceValidation.Id(runtimeEpoch);
        AdmissionAttemptId = CalibrationEvidenceValidation.Id(attemptId);
        ActorPrincipalId = CalibrationEvidenceValidation.Id(actorPrincipalId);
        InteractiveSessionId = CalibrationEvidenceValidation.Id(interactiveSessionId);
        if (authorizationRevision < 0) throw new ArgumentOutOfRangeException(nameof(authorizationRevision));
        AuthorizationRevision = authorizationRevision;
        AdmittedAtUtc = admittedAtUtc.ToUniversalTime();
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        BaselineRequested = baselineRequested ?? throw new ArgumentNullException(nameof(baselineRequested));
        BaselineEffective = baselineEffective ?? throw new ArgumentNullException(nameof(baselineEffective));
        DevelopmentFixtureHash = CameraSetupValidation.Hash(developmentFixtureHash, nameof(developmentFixtureHash));
        if (binding.LogicalRole != command.Plan.Requirement.LogicalCameraRole ||
            binding.Revision != command.ExpectedBindingRevision ||
            binding.RevisionHash != command.ExpectedBindingRevisionHash)
            throw new ArgumentException("CalibrationAdmissionBindingMismatch");
        if (command.Invocation.SessionId != interactiveSessionId ||
            command.Invocation.StepUpGrantId is not { } grant || grant == Guid.Empty ||
            command.CorrelationId == Guid.Empty)
            throw new ArgumentException("CalibrationAdmissionAuthorizationInvalid");
        BaselineRequestedHash = CalibrationSessionContractHash.Configuration(baselineRequested);
        BaselineEffectiveHash = CalibrationSessionContractHash.Configuration(baselineEffective);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-admission-v1", sessionId.ToString("D"), runtimeEpoch.ToString("D"),
            attemptId.ToString("D"), actorPrincipalId.ToString("D"), interactiveSessionId.ToString("D"),
            authorizationRevision.ToString(CultureInfo.InvariantCulture), AdmittedAtUtc.ToString("O"),
            command.CorrelationId.ToString("D"), command.Invocation.StepUpGrantId.Value.ToString("D"),
            command.AuthorizationTarget, binding.RevisionHash, binding.Target.ContentHash,
            BaselineRequestedHash, BaselineEffectiveHash, DevelopmentFixtureHash
        });
    }
    public Guid SessionId { get; }
    public Guid RuntimeEpoch { get; }
    public Guid AdmissionAttemptId { get; }
    public Guid ActorPrincipalId { get; }
    public Guid InteractiveSessionId { get; }
    public long AuthorizationRevision { get; }
    public DateTimeOffset AdmittedAtUtc { get; }
    public StartCalibrationSessionCommand Command { get; }
    public CameraBindingRevision Binding { get; }
    public RequestedCameraConfiguration BaselineRequested { get; }
    public EffectiveCameraConfiguration BaselineEffective { get; }
    public string BaselineRequestedHash { get; }
    public string BaselineEffectiveHash { get; }
    public string DevelopmentFixtureHash { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => true;
}

/// <summary>A preserved source image identity, independent of any production overlay.</summary>
public sealed class CalibrationFrameEvidence
{
    internal CalibrationFrameEvidence(Guid sessionId, Guid frameId, FrameMetadata metadata,
        FrameProvenance provenance, string pixelHash, long byteLength, string relativePath,
        int? sourceStrideBytes = null)
    {
        SessionId = CalibrationEvidenceValidation.Id(sessionId);
        FrameId = CalibrationEvidenceValidation.Id(frameId);
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        if (metadata.Correlation != new ExecutionCorrelationId(ExecutionKind.Calibration, frameId))
            throw new ArgumentException("CalibrationFrameCorrelationMismatch");
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (provenance.Correlation != metadata.Correlation)
            throw new ArgumentException("CalibrationFrameProvenanceCorrelationMismatch");
        SourceStrideBytes = sourceStrideBytes ?? metadata.StrideBytes;
        if (SourceStrideBytes < metadata.ValidRowBytes || SourceStrideBytes > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(sourceStrideBytes));
        PixelHash = CameraSetupValidation.Hash(pixelHash, nameof(pixelHash));
        if (byteLength < 1 || byteLength > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        if (byteLength != checked((long)metadata.ValidRowBytes * metadata.Height) ||
            metadata.StrideBytes != metadata.ValidRowBytes)
            throw new ArgumentException("CalibrationFrameCanonicalLayoutRequired");
        ByteLength = byteLength;
        if (relativePath != PixelHash + ".bin") throw new ArgumentException("CalibrationFramePathInvalid");
        RelativePath = relativePath;
        SourceHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-source-frame-v1", sessionId.ToString("D"), frameId.ToString("D"),
            metadata.LogicalCameraRole, metadata.Width.ToString(CultureInfo.InvariantCulture),
            metadata.Height.ToString(CultureInfo.InvariantCulture), metadata.PixelFormat.ToString(),
            metadata.ValidBits?.ToString(CultureInfo.InvariantCulture),
            metadata.HostCaptureUtc.ToUniversalTime().ToString("O"),
            CalibrationSessionContractHash.Provenance(provenance), SourceStrideBytes.ToString(CultureInfo.InvariantCulture),
            CalibrationSessionContractHash.Configuration(metadata.EffectiveCameraConfiguration),
            PixelHash, byteLength.ToString(CultureInfo.InvariantCulture), RelativePath
        });
    }
    public Guid SessionId { get; }
    public Guid FrameId { get; }
    public FrameMetadata Metadata { get; }
    public FrameProvenance Provenance { get; }
    public int SourceStrideBytes { get; }
    public string PixelHash { get; }
    public long ByteLength { get; }
    public string RelativePath { get; }
    public string SourceHash { get; }
}

/// <summary>Automatically extracted features. No command accepts replacement coordinates.</summary>
public sealed class CalibrationObservationEvidence
{
    internal CalibrationObservationEvidence(Guid observationId, CalibrationFrameEvidence frame,
        CalibrationProcedureDescriptor procedure, string inputHash, CalibrationExtractionResult result)
    {
        ObservationId = CalibrationEvidenceValidation.Id(observationId);
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        Procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
        InputHash = CameraSetupValidation.Hash(inputHash, nameof(inputHash));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        foreach (var feature in result.Features)
            if (feature.PixelX < 0 || feature.PixelY < 0 || feature.PixelX >= frame.Metadata.Width ||
                feature.PixelY >= frame.Metadata.Height)
                throw new ArgumentException("CalibrationObservationOutsideFrame");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-observation-v1", observationId.ToString("D"), frame.SourceHash,
            procedure.ContentHash, InputHash, result.Features.Count.ToString(CultureInfo.InvariantCulture),
            result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(result.Features.OrderBy(value => value.StableFeatureId, StringComparer.Ordinal)
            .Select(value => value.ContentHash)).Concat(result.Diagnostics.OrderBy(value => value.Key, StringComparer.Ordinal)
            .SelectMany(value => new[] { value.Key, value.Value })));
    }
    public Guid ObservationId { get; }
    public CalibrationFrameEvidence Frame { get; }
    public CalibrationProcedureDescriptor Procedure { get; }
    public string InputHash { get; }
    public CalibrationExtractionResult Result { get; }
    public string ContentHash { get; }
}

public sealed record CalibrationEvidenceExclusion(Guid FrameId, Guid ActorPrincipalId,
    Guid InteractiveSessionId, string Reason, DateTimeOffset RecordedAtUtc);
public sealed record CalibrationSelectionEvaluation(bool Sufficient, string ReasonCode,
    int IncludedFrameCount, int SufficientFeatureFrameCount, double ImageCoverage, string SelectionHash);

/// <summary>A retained computation result with no policy acceptance or publication authority.</summary>
public sealed class CalibrationCandidateEvidence
{
    internal CalibrationCandidateEvidence(Guid candidateId, Guid sessionId, string sessionHeaderHash,
        string selectionHash, CalibrationProcedureComputationResult result, DateTimeOffset computedAtUtc)
    {
        CandidateId = CalibrationEvidenceValidation.Id(candidateId);
        SessionId = CalibrationEvidenceValidation.Id(sessionId);
        SessionHeaderHash = CameraSetupValidation.Hash(sessionHeaderHash, nameof(sessionHeaderHash));
        SelectionHash = CameraSetupValidation.Hash(selectionHash, nameof(selectionHash));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        ComputedAtUtc = computedAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-candidate-v1", candidateId.ToString("D"), sessionId.ToString("D"),
            SessionHeaderHash, SelectionHash, ComputedAtUtc.ToString("O"), result.Coefficients.ContentHash,
            result.Coefficients.Format.Id, result.Coefficients.Format.Version, result.Coefficients.Format.ContentHash,
            result.QualityMetrics.Count.ToString(CultureInfo.InvariantCulture),
            result.Diagnostics.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(result.QualityMetrics.OrderBy(value => value.Key, StringComparer.Ordinal)
            .SelectMany(value => new[] { value.Key, value.Value.ToString("R", CultureInfo.InvariantCulture), value.Unit }))
            .Concat(result.Diagnostics.OrderBy(value => value.Key, StringComparer.Ordinal)
                .SelectMany(value => new[] { value.Key, value.Value })));
    }
    public Guid CandidateId { get; }
    public Guid SessionId { get; }
    public string SessionHeaderHash { get; }
    public string SelectionHash { get; }
    public CalibrationProcedureComputationResult Result { get; }
    public DateTimeOffset ComputedAtUtc { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => true;
    public bool CanPublish => false;
    public bool CanActivate => false;
    public string AcceptanceReasonCode => "CalibrationAcceptanceAuthorityUnavailable";
}

/// <summary>The temporary request and the actual camera read-back retained before collection begins.</summary>
public sealed class CalibrationTemporaryConfigurationEvidence
{
    internal CalibrationTemporaryConfigurationEvidence(RequestedCameraConfiguration requested,
        EffectiveCameraConfiguration effective)
    {
        Requested = requested ?? throw new ArgumentNullException(nameof(requested));
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        RequestedHash = CalibrationSessionContractHash.Configuration(requested);
        EffectiveHash = CalibrationSessionContractHash.Configuration(effective);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-temporary-configuration-v1", RequestedHash, EffectiveHash
        });
    }
    public RequestedCameraConfiguration Requested { get; }
    public EffectiveCameraConfiguration Effective { get; }
    public string RequestedHash { get; }
    public string EffectiveHash { get; }
    public string ContentHash { get; }
}

public sealed class CalibrationSessionEvidence
{
    internal CalibrationSessionEvidence(CalibrationSessionHeader header, CalibrationSessionState state,
        IEnumerable<CalibrationFrameEvidence> frames, IEnumerable<CalibrationObservationEvidence> observations,
        IEnumerable<CalibrationEvidenceExclusion> exclusions, CalibrationCandidateEvidence? candidate,
        CalibrationSelectionEvaluation selection,
        CalibrationTemporaryConfigurationEvidence? temporaryConfiguration = null)
    {
        Header = header; State = state;
        Frames = AlgorithmContractValidation.Copy(frames, nameof(frames), 64);
        Observations = AlgorithmContractValidation.Copy(observations, nameof(observations), 64);
        Exclusions = AlgorithmContractValidation.Copy(exclusions, nameof(exclusions), 64);
        Candidate = candidate; Selection = selection;
        TemporaryConfiguration = temporaryConfiguration;
    }
    public CalibrationSessionHeader Header { get; }
    public CalibrationSessionState State { get; }
    public ReadOnlyCollection<CalibrationFrameEvidence> Frames { get; }
    public ReadOnlyCollection<CalibrationObservationEvidence> Observations { get; }
    public ReadOnlyCollection<CalibrationEvidenceExclusion> Exclusions { get; }
    public CalibrationCandidateEvidence? Candidate { get; }
    public CalibrationSelectionEvaluation Selection { get; }
    public CalibrationTemporaryConfigurationEvidence? TemporaryConfiguration { get; }
}

public sealed record CalibrationSessionQueryResult(bool Available, string ReasonCode,
    CalibrationSessionEvidence? Evidence = null);

/// <summary>Verified, bounded image bytes for review. Obtaining a copy does not change source evidence.</summary>
public sealed class CalibrationFrameImage
{
    private readonly byte[] _pixels;
    internal CalibrationFrameImage(CalibrationFrameEvidence frame, ReadOnlyMemory<byte> pixels)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        if (pixels.Length != frame.ByteLength ||
            Convert.ToHexString(SHA256.HashData(pixels.Span)) != frame.PixelHash)
            throw new ArgumentException("CalibrationFrameContentHashMismatch");
        _pixels = pixels.ToArray();
    }
    public CalibrationFrameEvidence Frame { get; }
    public byte[] GetBytes() => (byte[])_pixels.Clone();
}

public sealed record CalibrationFrameQueryResult(bool Available, string ReasonCode,
    CalibrationFrameImage? Image = null);

public interface ICalibrationSessionQuery
{
    ValueTask<CalibrationSessionQueryResult> QueryCalibrationSessionAsync(Guid sessionId,
        CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<CalibrationFrameQueryResult> ReadCalibrationFrameAsync(Guid sessionId, Guid frameId,
        string expectedSourceHash, CommandInvocation invocation, CancellationToken cancellationToken = default);
}

internal static class CalibrationEvidenceValidation
{
    internal static Guid Id(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("CalibrationIdentityInvalid") : value;
}
