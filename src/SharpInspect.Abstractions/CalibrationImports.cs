using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>An exact local import identity. It is never a Calibration Session identity.</summary>
public sealed record ImportedCalibrationCandidateReference
{
    public ImportedCalibrationCandidateReference(Guid candidateId, string contentHash)
    {
        CandidateId = CalibrationEvidenceValidation.Id(candidateId);
        ContentHash = CameraSetupValidation.Hash(contentHash, nameof(contentHash));
    }
    public Guid CandidateId { get; }
    public string ContentHash { get; }
}

/// <summary>An exact immutable local revalidation or physical-verification record.</summary>
public sealed record CalibrationImportEvidenceReference
{
    public CalibrationImportEvidenceReference(Guid operationId, string contentHash)
    {
        OperationId = CalibrationEvidenceValidation.Id(operationId);
        ContentHash = CameraSetupValidation.Hash(contentHash, nameof(contentHash));
    }
    public Guid OperationId { get; }
    public string ContentHash { get; }
}

/// <summary>Explicit local governance of untrusted calibration data; no command activates a Recipe.</summary>
public abstract record CalibrationImportCommand : RuntimeCommand
{
    private protected CalibrationImportCommand(Guid correlationId, CommandInvocation invocation)
        : base(CalibrationEvidenceValidation.Id(correlationId), invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
    }
    public abstract string AuthorizationTarget { get; }
    private protected static string Hash(params string?[] fields) => AlgorithmContractValidation.HashParts(fields);
    private protected static string Text(string value) => AlgorithmContractValidation.BoundedText(value, nameof(value), 512);
}

/// <summary>Retain a structurally verified package as a new, unvalidated local candidate.</summary>
public sealed record ImportCalibrationPackageCommand : CalibrationImportCommand
{
    public ImportCalibrationPackageCommand(Guid correlationId, CommandInvocation invocation,
        CalibrationExportPackage package, string reason) : base(correlationId, invocation)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Reason = Text(reason);
        PackageHash = package.ContentHash;
        AuthorizationTarget = Hash("sharpinspect-calibration-import-command-v1", PackageHash, Reason);
    }
    public CalibrationExportPackage Package { get; }
    public string PackageHash { get; }
    public string Reason { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>Re-extract and recompute an imported candidate under an exact current local requirement.</summary>
public sealed record RevalidateImportedCalibrationCommand : CalibrationImportCommand
{
    public RevalidateImportedCalibrationCommand(Guid correlationId, CommandInvocation invocation,
        ImportedCalibrationCandidateReference candidate, CalibrationRequirement requirement,
        CameraBindingRevision binding, ImagingSetupRevisionReference imagingSetup, string reason)
        : base(correlationId, invocation)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Requirement = requirement ?? throw new ArgumentNullException(nameof(requirement));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ImagingSetup = imagingSetup ?? throw new ArgumentNullException(nameof(imagingSetup));
        if (requirement.LogicalCameraRole != binding.LogicalRole)
            throw new ArgumentException("CalibrationImportLogicalRoleMismatch");
        Reason = Text(reason);
        AuthorizationTarget = Hash("sharpinspect-calibration-import-revalidate-command-v1",
            candidate.CandidateId.ToString("D"), candidate.ContentHash, requirement.ContentHash,
            binding.RevisionHash, imagingSetup.RevisionHash, Reason);
    }
    public ImportedCalibrationCandidateReference Candidate { get; }
    public CalibrationRequirement Requirement { get; }
    public CameraBindingRevision Binding { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public string Reason { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>Verify an import before publication using independent, newly obtained local physical evidence.</summary>
public sealed record VerifyImportedCalibrationCommand : CalibrationImportCommand
{
    public VerifyImportedCalibrationCommand(Guid correlationId, CommandInvocation invocation,
        ImportedCalibrationCandidateReference candidate, CalibrationImportEvidenceReference evaluation,
        PhysicalCalibrationVerificationSubmission submission, string reason) : base(correlationId, invocation)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        Reason = Text(reason);
        AuthorizationTarget = Hash("sharpinspect-calibration-import-verify-command-v1",
            candidate.CandidateId.ToString("D"), candidate.ContentHash,
            evaluation.OperationId.ToString("D"), evaluation.ContentHash, submission.ContentHash, Reason);
    }
    public ImportedCalibrationCandidateReference Candidate { get; }
    public CalibrationImportEvidenceReference Evaluation { get; }
    public PhysicalCalibrationVerificationSubmission Submission { get; }
    public string Reason { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>Publish a new local Profile identity after every applicable local import gate passes.</summary>
public sealed record PublishImportedCalibrationCommand : CalibrationImportCommand
{
    public PublishImportedCalibrationCommand(Guid correlationId, CommandInvocation invocation,
        Guid profileId, ImportedCalibrationCandidateReference candidate,
        CalibrationImportEvidenceReference evaluation, CalibrationImportEvidenceReference? physicalVerification,
        string reason) : base(correlationId, invocation)
    {
        ProfileId = CalibrationEvidenceValidation.Id(profileId);
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        PhysicalVerification = physicalVerification;
        Reason = Text(reason);
        AuthorizationTarget = Hash("sharpinspect-calibration-import-publish-command-v1", profileId.ToString("D"),
            candidate.CandidateId.ToString("D"), candidate.ContentHash,
            evaluation.OperationId.ToString("D"), evaluation.ContentHash,
            physicalVerification?.OperationId.ToString("D"), physicalVerification?.ContentHash, Reason);
    }
    public Guid ProfileId { get; }
    public ImportedCalibrationCandidateReference Candidate { get; }
    public CalibrationImportEvidenceReference Evaluation { get; }
    public CalibrationImportEvidenceReference? PhysicalVerification { get; }
    public string Reason { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>A local, signed governance fact. External package claims cannot construct one.</summary>
public abstract class CalibrationImportRecord
{
    private protected CalibrationImportRecord(long position, Guid operationId,
        CalibrationGovernanceActor actor, DateTimeOffset recordedAtUtc, string reason, string authorizationTarget)
    {
        CalibrationGovernanceRecordValidation.Identity(position, operationId, actor);
        if (recordedAtUtc == default) throw new ArgumentException("CalibrationImportRecordTimeRequired");
        Position = position;
        OperationId = operationId;
        Actor = actor;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        Reason = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 512);
        AuthorizationTarget = CameraSetupValidation.Hash(authorizationTarget, nameof(authorizationTarget));
    }
    public long Position { get; }
    public Guid OperationId { get; }
    public CalibrationGovernanceActor Actor { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
    public string ContentHash { get; private protected set; } = string.Empty;
    private protected string Hash(string domain, IEnumerable<string?> fields) => AlgorithmContractValidation.HashParts(
        new[] { domain, Position.ToString(CultureInfo.InvariantCulture), OperationId.ToString("D"),
            Actor.ContentHash, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), Reason, AuthorizationTarget }.Concat(fields));
}

/// <summary>Local quarantine provenance. This record has no evaluation, publication, or activation authority.</summary>
public sealed class ImportedCalibrationCandidate : CalibrationImportRecord
{
    internal ImportedCalibrationCandidate(long position, Guid operationId, CalibrationGovernanceActor actor,
        DateTimeOffset recordedAtUtc, Guid candidateId, string packageHash, int packageLength,
        Guid sourcePackageId, string sourceStationId, string sourceManifestHash, string reason, string authorizationTarget)
        : base(position, operationId, actor, recordedAtUtc, reason, authorizationTarget)
    {
        CandidateId = CalibrationEvidenceValidation.Id(candidateId);
        PackageHash = CameraSetupValidation.Hash(packageHash, nameof(packageHash));
        if (packageLength is < 1 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(packageLength));
        PackageLength = packageLength;
        SourcePackageId = CalibrationEvidenceValidation.Id(sourcePackageId);
        SourceStationId = AlgorithmContractValidation.BoundedText(sourceStationId, nameof(sourceStationId), 256);
        SourceManifestHash = CameraSetupValidation.Hash(sourceManifestHash, nameof(sourceManifestHash));
        ContentHash = Hash("sharpinspect-imported-calibration-candidate-v1", new[]
        {
            CandidateId.ToString("D"), PackageHash, PackageLength.ToString(CultureInfo.InvariantCulture),
            SourcePackageId.ToString("D"), SourceStationId, SourceManifestHash
        });
        Reference = new(CandidateId, ContentHash);
    }
    public Guid CandidateId { get; }
    public string PackageHash { get; }
    public int PackageLength { get; }
    public Guid SourcePackageId { get; }
    public string SourceStationId { get; }
    public string SourceManifestHash { get; }
    public ImportedCalibrationCandidateReference Reference { get; }
    public bool CanPublish => false;
    public bool CanActivate => false;
}

/// <summary>Locally recomputed coefficients and policy assessment, bound to the observed current equipment.</summary>
public sealed class CalibrationImportComputationEvidence
{
    public const int MaximumBytes = 256 * 1024;
    private readonly byte[] _bytes;
    internal CalibrationImportComputationEvidence(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("CalibrationImportComputationEvidenceSizeInvalid");
        _bytes = bytes.ToArray();
        ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_bytes));
    }
    public string ContentHash { get; }
    public byte[] GetBytes() => (byte[])_bytes.Clone();
}

/// <summary>Locally recomputed coefficients and policy assessment, bound to the observed current equipment.</summary>
public sealed class ImportedCalibrationEvaluation : CalibrationImportRecord
{
    internal ImportedCalibrationEvaluation(long position, Guid operationId, CalibrationGovernanceActor actor,
        DateTimeOffset recordedAtUtc, ImportedCalibrationCandidateReference candidate,
        CameraBindingRevision binding, CalibrationProfileContent content,
        IEnumerable<CalibrationGateSectionResult> sections, IEnumerable<string> failures,
        CalibrationImportComputationEvidence computationEvidence, string reason, string authorizationTarget)
        : base(position, operationId, actor, recordedAtUtc, reason, authorizationTarget)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Sections = CalibrationGovernanceRecordValidation.CopySections(sections);
        Failures = CalibrationGovernanceRecordValidation.CopyReasons(failures, nameof(failures), 64);
        ComputationEvidence = computationEvidence ?? throw new ArgumentNullException(nameof(computationEvidence));
        if (content.Device != binding.Target || content.Requirement.LogicalCameraRole != binding.LogicalRole ||
            content.SourceEvidenceHash != ComputationEvidenceHash)
            throw new ArgumentException("CalibrationImportEvaluationBindingMismatch");
        ContentHash = Hash("sharpinspect-imported-calibration-evaluation-v1", new[]
        {
            candidate.CandidateId.ToString("D"), candidate.ContentHash, binding.RevisionHash,
            content.ContentHash, ComputationEvidenceHash, Sections.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Sections.Select(value => value.ContentHash)).Concat(new[]
        {
            Failures.Count.ToString(CultureInfo.InvariantCulture)
        }).Concat(Failures));
        Reference = new(operationId, ContentHash);
    }
    public ImportedCalibrationCandidateReference Candidate { get; }
    public CameraBindingRevision Binding { get; }
    public CalibrationProfileContent Content { get; }
    public ReadOnlyCollection<CalibrationGateSectionResult> Sections { get; }
    public ReadOnlyCollection<string> Failures { get; }
    public CalibrationImportComputationEvidence ComputationEvidence { get; }
    public string ComputationEvidenceHash => ComputationEvidence.ContentHash;
    public CalibrationImportEvidenceReference Reference { get; }
    public bool Passed => Failures.Count == 0 && Sections.All(value => value.Passed);
}

/// <summary>Pre-publication physical verification; never copied from an export package.</summary>
public sealed class ImportedCalibrationPhysicalVerification : CalibrationImportRecord
{
    internal ImportedCalibrationPhysicalVerification(long position, Guid operationId, CalibrationGovernanceActor actor,
        DateTimeOffset recordedAtUtc, ImportedCalibrationCandidateReference candidate,
        CalibrationImportEvidenceReference evaluation, PhysicalCalibrationVerificationSubmission submission,
        IEnumerable<CalibrationMetricGateResult> gates, IEnumerable<string> failures, DateTimeOffset? validUntilUtc,
        string reason, string authorizationTarget, CalibrationImportPhysicalWitness? witness = null)
        : base(position, operationId, actor, recordedAtUtc, reason, authorizationTarget)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        Witness = witness;
        Gates = AlgorithmContractValidation.Copy(gates, nameof(gates), 32);
        Failures = CalibrationGovernanceRecordValidation.CopyReasons(failures, nameof(failures), 64);
        ValidUntilUtc = validUntilUtc?.ToUniversalTime();
        if (Passed ? ValidUntilUtc is null || ValidUntilUtc <= submission.PerformedAtUtc : ValidUntilUtc is not null)
            throw new ArgumentException("CalibrationImportVerificationValidityInvalid");
        ContentHash = Hash("sharpinspect-imported-calibration-physical-verification-v1", new[]
        {
            candidate.CandidateId.ToString("D"), candidate.ContentHash,
            evaluation.OperationId.ToString("D"), evaluation.ContentHash, submission.ContentHash,
            witness?.ContentHash,
            Gates.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Gates.Select(value => value.ContentHash)).Concat(new[]
        {
            Failures.Count.ToString(CultureInfo.InvariantCulture)
        }).Concat(Failures).Append(ValidUntilUtc?.ToString("O", CultureInfo.InvariantCulture)));
        Reference = new(operationId, ContentHash);
    }
    public ImportedCalibrationCandidateReference Candidate { get; }
    public CalibrationImportEvidenceReference Evaluation { get; }
    public PhysicalCalibrationVerificationSubmission Submission { get; }
    /// <summary>Local execution witness. A null value is retained only for source-compatible legacy construction; a durable real record must carry one.</summary>
    public CalibrationImportPhysicalWitness? Witness { get; }
    public ReadOnlyCollection<CalibrationMetricGateResult> Gates { get; }
    public ReadOnlyCollection<string> Failures { get; }
    public DateTimeOffset? ValidUntilUtc { get; }
    public CalibrationImportEvidenceReference Reference { get; }
    public bool Passed => Failures.Count == 0 && Gates.Count != 0 && Gates.All(value => value.Outcome == CalibrationGateOutcome.Passed);
}

/// <summary>
/// Local execution facts captured after a camera lease confirmed the exact setup.
/// The witness carries no actor or production authority; its operation is bound to
/// the containing import record and its content hash binds every captured input.
/// </summary>
public sealed class CalibrationImportPhysicalWitness
{
    internal CalibrationImportPhysicalWitness(Guid operationId, Guid runtimeEpoch,
        CameraBindingRevision binding, ImagingSetupRevisionReference imagingSetup,
        CalibrationFrameGeometry requestedGeometry, CalibrationFrameGeometry effectiveGeometry,
        DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc)
    {
        OperationId = CalibrationEvidenceValidation.Id(operationId);
        RuntimeEpoch = CalibrationEvidenceValidation.Id(runtimeEpoch);
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ImagingSetup = imagingSetup ?? throw new ArgumentNullException(nameof(imagingSetup));
        RequestedGeometry = requestedGeometry ?? throw new ArgumentNullException(nameof(requestedGeometry));
        EffectiveGeometry = effectiveGeometry ?? throw new ArgumentNullException(nameof(effectiveGeometry));
        if (Binding.LogicalRole != ImagingSetup.LogicalCameraRole)
            throw new ArgumentException("CalibrationImportWitnessLogicalRoleMismatch");
        if (startedAtUtc == default || completedAtUtc == default)
            throw new ArgumentException("CalibrationImportWitnessTimeRequired");
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        if (CompletedAtUtc < StartedAtUtc)
            throw new ArgumentException("CalibrationImportWitnessTimeOrderInvalid");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-import-physical-witness-v1",
            OperationId.ToString("D"), RuntimeEpoch.ToString("D"),
            Binding.LogicalRole, Binding.Revision.ToString(CultureInfo.InvariantCulture),
            Binding.OperationId.ToString("D"), Binding.RevisionHash, Binding.Target.ContentHash,
            ImagingSetup.LogicalCameraRole, ImagingSetup.RevisionId.ToString("D"),
            ImagingSetup.Revision.ToString(CultureInfo.InvariantCulture), ImagingSetup.RevisionHash,
            RequestedGeometry.ContentHash, EffectiveGeometry.ContentHash,
            StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public Guid OperationId { get; }
    public Guid RuntimeEpoch { get; }
    public CameraBindingRevision Binding { get; }
    public ImagingSetupRevisionReference ImagingSetup { get; }
    public CalibrationFrameGeometry RequestedGeometry { get; }
    public CalibrationFrameGeometry EffectiveGeometry { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>A fresh local publication retaining its import lineage and local assessment authority.</summary>
public sealed class PublishedImportedCalibrationProfile : CalibrationImportRecord
{
    internal PublishedImportedCalibrationProfile(long position, Guid operationId, CalibrationGovernanceActor actor,
        DateTimeOffset recordedAtUtc, Guid profileId, ImportedCalibrationCandidateReference candidate,
        CalibrationImportEvidenceReference evaluation, CalibrationImportEvidenceReference? physicalVerification,
        CalibrationProfileContent content, string reason, string authorizationTarget)
        : base(position, operationId, actor, recordedAtUtc, reason, authorizationTarget)
    {
        ProfileId = CalibrationEvidenceValidation.Id(profileId);
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        PhysicalVerification = physicalVerification;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        var finalEvidence = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imported-calibration-publication-evidence-v1", candidate.ContentHash,
            evaluation.ContentHash, physicalVerification?.ContentHash
        });
        if (content.SourceEvidenceHash != finalEvidence)
            throw new ArgumentException("CalibrationImportPublicationEvidenceMismatch");
        ContentHash = Hash("sharpinspect-imported-calibration-publication-v1", new[]
        {
            ProfileId.ToString("D"), candidate.CandidateId.ToString("D"), candidate.ContentHash,
            evaluation.OperationId.ToString("D"), evaluation.ContentHash,
            physicalVerification?.OperationId.ToString("D"), physicalVerification?.ContentHash,
            content.ContentHash, "DevelopmentOnly"
        });
        Reference = new(profileId, 1, ContentHash);
    }
    public Guid ProfileId { get; }
    public long Version => 1;
    public ImportedCalibrationCandidateReference Candidate { get; }
    public CalibrationImportEvidenceReference Evaluation { get; }
    public CalibrationImportEvidenceReference? PhysicalVerification { get; }
    public CalibrationProfileContent Content { get; }
    public CalibrationProfileReference Reference { get; }
    public bool DevelopmentOnly => true;
    public bool ProductionAuthority => false;
    public bool CanActivate => false;
}

/// <summary>Optional explicit project verifier for an unpublished, locally recomputed imported candidate.</summary>
public interface IImportedCalibrationPhysicalVerificationProcedure
{
    RecipeContractReference ProcedureContract { get; }
    RecipeContractReference EvidenceContract { get; }
    IReadOnlyList<CalibrationQualityMetric> Evaluate(ImportedCalibrationPhysicalVerificationContext context,
        CancellationToken cancellationToken);
}

public sealed class ImportedCalibrationPhysicalVerificationContext
{
    internal ImportedCalibrationPhysicalVerificationContext(ImportedCalibrationCandidate candidate,
        ImportedCalibrationEvaluation evaluation, PhysicalCalibrationVerificationSubmission submission)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        ArgumentNullException.ThrowIfNull(submission);
        IndependentReference = submission.IndependentReference;
        PerformedAtUtc = submission.PerformedAtUtc;
        Evidence = submission.Evidence;
    }
    public ImportedCalibrationCandidate Candidate { get; }
    public ImportedCalibrationEvaluation Evaluation { get; }
    public RecipeContractReference IndependentReference { get; }
    public DateTimeOffset PerformedAtUtc { get; }
    public PhysicalCalibrationVerificationEvidencePayload Evidence { get; }
}

public sealed record CalibrationImportResult(RuntimeCommandOutcome Outcome, CalibrationImportRecord? Record = null);
public sealed record CalibrationExportResult(bool Available, string ReasonCode, CalibrationExportPackage? Package = null);

public interface ICalibrationImportRuntime
{
    ValueTask<CalibrationImportResult> ImportAsync(ImportCalibrationPackageCommand command, CancellationToken cancellationToken = default);
    ValueTask<CalibrationImportResult> RevalidateImportAsync(RevalidateImportedCalibrationCommand command, CancellationToken cancellationToken = default);
    ValueTask<CalibrationImportResult> VerifyImportAsync(VerifyImportedCalibrationCommand command, CancellationToken cancellationToken = default);
    ValueTask<CalibrationImportResult> PublishImportAsync(PublishImportedCalibrationCommand command, CancellationToken cancellationToken = default);
}

public interface ICalibrationImportQuery
{
    ValueTask<CalibrationGovernanceQueryResult<CalibrationImportRecord>> ReadImportOperationAsync(Guid operationId,
        CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<CalibrationExportResult> ExportCalibrationAsync(CalibrationCandidateReference candidate,
        RecipeContractReference policy, CommandInvocation invocation, CalibrationProfileReference? profile = null,
        CancellationToken cancellationToken = default);
}
