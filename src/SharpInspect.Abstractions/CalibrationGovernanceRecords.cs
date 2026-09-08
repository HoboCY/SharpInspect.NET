using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The outcome of one explicitly configured calibration gate.</summary>
public enum CalibrationGateOutcome : byte
{
    Passed = 1,
    Failed = 2,
    NotApplicable = 3
}

/// <summary>One category-free gate result shared by candidate and physical evidence.</summary>
public sealed class CalibrationMetricGateResult
{
    internal CalibrationMetricGateResult(string gateId, CalibrationGateOutcome outcome,
        double? actualValue, string? actualUnit, string reasonCode)
    {
        GateId = AlgorithmConfigurationValidation.Identifier(gateId, nameof(gateId));
        Outcome = AlgorithmConfigurationValidation.Enum(outcome, nameof(outcome));
        if (actualValue is { } value)
        {
            AlgorithmContractValidation.Finite(value, nameof(actualValue));
            actualValue = value == 0d ? 0d : value;
        }
        ActualValue = actualValue;
        ActualUnit = actualUnit is null
            ? null
            : AlgorithmContractValidation.BoundedText(actualUnit, nameof(actualUnit), 64);
        ReasonCode = AlgorithmContractValidation.BoundedText(reasonCode, nameof(reasonCode), 256);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-metric-gate-result-v1", GateId, Outcome.ToString(),
            ActualValue?.ToString("R", CultureInfo.InvariantCulture), ActualUnit, ReasonCode
        });
    }

    public string GateId { get; }
    public CalibrationGateOutcome Outcome { get; }
    public double? ActualValue { get; }
    public string? ActualUnit { get; }
    public string ReasonCode { get; }
    public string ContentHash { get; }
    public bool Passed => Outcome == CalibrationGateOutcome.Passed;
}

/// <summary>Results for one policy section; the category is carried by the section.</summary>
public sealed class CalibrationGateSectionResult
{
    internal CalibrationGateSectionResult(CalibrationAcceptanceGateCategory category,
        CalibrationPolicyApplicability applicability, IEnumerable<CalibrationMetricGateResult> gates,
        string? notApplicableReason = null)
    {
        Category = AlgorithmConfigurationValidation.Enum(category, nameof(category));
        Applicability = AlgorithmConfigurationValidation.Enum(applicability, nameof(applicability));
        Gates = new ReadOnlyCollection<CalibrationMetricGateResult>(
            AlgorithmContractValidation.Copy(gates, nameof(gates), 8).ToArray());

        if (Applicability == CalibrationPolicyApplicability.Required)
        {
            if (Gates.Count is < 1 or > 8)
                throw new ArgumentException("CalibrationPolicyRequiredResultsInvalid", nameof(gates));
            if (notApplicableReason is not null)
                throw new ArgumentException("CalibrationPolicyNotApplicableReasonUnexpected",
                    nameof(notApplicableReason));
        }
        else
        {
            if (Category is not CalibrationAcceptanceGateCategory.PoseDiversity and
                not CalibrationAcceptanceGateCategory.MaximumPerImageResidual)
                throw new ArgumentException("CalibrationPolicyMandatorySectionCannotBeNotApplicable",
                    nameof(category));
            if (Gates.Count != 0)
                throw new ArgumentException("CalibrationPolicyNotApplicableResultsUnexpected",
                    nameof(gates));
            if (string.IsNullOrWhiteSpace(notApplicableReason))
                throw new ArgumentException("CalibrationPolicyNotApplicableReasonRequired",
                    nameof(notApplicableReason));
        }

        if (Gates.Select(gate => gate.GateId).Distinct(StringComparer.Ordinal).Count() != Gates.Count)
            throw new ArgumentException("CalibrationPolicyGateIdDuplicate", nameof(gates));
        NotApplicableReason = notApplicableReason is null
            ? null
            : AlgorithmContractValidation.BoundedText(notApplicableReason,
                nameof(notApplicableReason), 512);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-gate-section-result-v1", Category.ToString(),
            Applicability.ToString(), NotApplicableReason,
            Gates.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Gates.Select(gate => gate.ContentHash)));
    }

    public CalibrationAcceptanceGateCategory Category { get; }
    public CalibrationPolicyApplicability Applicability { get; }
    public ReadOnlyCollection<CalibrationMetricGateResult> Gates { get; }
    public string? NotApplicableReason { get; }
    public string ContentHash { get; }
    public bool Passed => Applicability == CalibrationPolicyApplicability.NotApplicable ||
        Gates.Count != 0 && Gates.All(gate => gate.Outcome == CalibrationGateOutcome.Passed);
}

/// <summary>Stable identity of a candidate retained by the calibration session store.</summary>
public sealed record CalibrationCandidateReference
{
    public CalibrationCandidateReference(Guid sessionId, Guid candidateId, string candidateContentHash)
    {
        if (sessionId == Guid.Empty || candidateId == Guid.Empty)
            throw new ArgumentException("CalibrationCandidateReferenceInvalid");
        SessionId = sessionId;
        CandidateId = candidateId;
        CandidateContentHash = CalibrationGovernanceRecordValidation.Hash(candidateContentHash,
            nameof(candidateContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-candidate-reference-v1", SessionId.ToString("D"),
            CandidateId.ToString("D"), CandidateContentHash
        });
    }

    public Guid SessionId { get; }
    public Guid CandidateId { get; }
    public string CandidateContentHash { get; }
    public string ContentHash { get; }
}

/// <summary>Stable identity of one policy evaluation record.</summary>
public sealed record CalibrationPolicyEvaluationReference
{
    public CalibrationPolicyEvaluationReference(Guid evaluationId, string contentHash)
    {
        if (evaluationId == Guid.Empty)
            throw new ArgumentException("CalibrationPolicyEvaluationReferenceInvalid", nameof(evaluationId));
        EvaluationId = evaluationId;
        ContentHash = CalibrationGovernanceRecordValidation.Hash(contentHash, nameof(contentHash));
    }

    public Guid EvaluationId { get; }
    public string ContentHash { get; }
}

/// <summary>Stable identity of one physical verification record.</summary>
public sealed record PhysicalCalibrationVerificationReference
{
    public PhysicalCalibrationVerificationReference(Guid verificationId, string contentHash)
    {
        if (verificationId == Guid.Empty)
            throw new ArgumentException("PhysicalCalibrationVerificationReferenceInvalid",
                nameof(verificationId));
        VerificationId = verificationId;
        ContentHash = CalibrationGovernanceRecordValidation.Hash(contentHash, nameof(contentHash));
    }

    public Guid VerificationId { get; }
    public string ContentHash { get; }
}

/// <summary>Verified actor/session binding captured by a governance ledger row.</summary>
public sealed class CalibrationGovernanceActor
{
    internal CalibrationGovernanceActor(Guid principalId, Guid sessionId, long authorizationRevision)
    {
        if (principalId == Guid.Empty || sessionId == Guid.Empty || authorizationRevision < 0)
            throw new ArgumentException("CalibrationGovernanceActorInvalid");
        PrincipalId = principalId;
        SessionId = sessionId;
        AuthorizationRevision = authorizationRevision;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-governance-actor-v1", PrincipalId.ToString("D"),
            SessionId.ToString("D"), AuthorizationRevision.ToString(CultureInfo.InvariantCulture)
        });
    }

    public Guid PrincipalId { get; }
    public Guid SessionId { get; }
    public long AuthorizationRevision { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable policy publication history row.</summary>
public sealed class CalibrationAcceptancePolicyRevision
{
    internal CalibrationAcceptancePolicyRevision(long position, Guid operationId,
        CalibrationAcceptancePolicy policy, RecipeContractReference? previous,
        CalibrationGovernanceActor actor, DateTimeOffset recordedAtUtc)
    {
        CalibrationGovernanceRecordValidation.Identity(position, operationId, actor);
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Previous = previous;
        Actor = actor;
        Position = position;
        OperationId = operationId;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-acceptance-policy-revision-v1",
            Position.ToString(CultureInfo.InvariantCulture), OperationId.ToString("D"),
            Policy.ContentHash, Previous?.Id, Previous?.Version, Previous?.ContentHash,
            Actor.ContentHash, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public long Position { get; }
    public Guid OperationId { get; }
    public CalibrationAcceptancePolicy Policy { get; }
    public RecipeContractReference? Previous { get; }
    public CalibrationGovernanceActor Actor { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable policy evaluation history row for one retained candidate.</summary>
public sealed class CalibrationPolicyEvaluationRecord
{
    internal CalibrationPolicyEvaluationRecord(long position, Guid evaluationId, Guid operationId,
        CalibrationCandidateReference candidate, RecipeContractReference policy,
        IEnumerable<CalibrationGateSectionResult> sections, IEnumerable<string> bindingFailures,
        CalibrationGovernanceActor actor, DateTimeOffset recordedAtUtc)
    {
        CalibrationGovernanceRecordValidation.Identity(position, operationId, actor);
        if (evaluationId == Guid.Empty)
            throw new ArgumentException("CalibrationPolicyEvaluationIdInvalid", nameof(evaluationId));
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Sections = CalibrationGovernanceRecordValidation.CopySections(sections);
        BindingFailures = CalibrationGovernanceRecordValidation.CopyReasons(bindingFailures,
            nameof(bindingFailures), 32);
        Actor = actor;
        Position = position;
        EvaluationId = evaluationId;
        OperationId = operationId;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-policy-evaluation-record-v1",
            Position.ToString(CultureInfo.InvariantCulture), EvaluationId.ToString("D"),
            OperationId.ToString("D"), Candidate.SessionId.ToString("D"),
            Candidate.CandidateId.ToString("D"), Candidate.CandidateContentHash,
            Policy.Id, Policy.Version, Policy.ContentHash,
            Sections.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Sections.Select(section => section.ContentHash)).Concat(new[]
        {
            BindingFailures.Count.ToString(CultureInfo.InvariantCulture)
        }).Concat(BindingFailures).Concat(new[]
        {
            Actor.ContentHash, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        }));
        Reference = new CalibrationPolicyEvaluationReference(EvaluationId, ContentHash);
    }

    public long Position { get; }
    public Guid EvaluationId { get; }
    public Guid OperationId { get; }
    public CalibrationCandidateReference Candidate { get; }
    public RecipeContractReference Policy { get; }
    public ReadOnlyCollection<CalibrationGateSectionResult> Sections { get; }
    public ReadOnlyCollection<string> BindingFailures { get; }
    public CalibrationGovernanceActor Actor { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public CalibrationPolicyEvaluationReference Reference { get; }
    public string ContentHash { get; }
    public bool Passed => BindingFailures.Count == 0 && Sections.All(section => section.Passed);
    public bool CanCreateDevelopmentProfile => Passed;
    public bool ProductionAuthority => false;
}

/// <summary>Immutable, development-only profile publication history row.</summary>
public sealed class PublishedCalibrationProfileVersion
{
    internal PublishedCalibrationProfileVersion(long position, Guid operationId, Guid profileId,
        long version, CalibrationProfileReference? previous, CalibrationProfileContent content,
        CalibrationCandidateReference sourceCandidate, CalibrationPolicyEvaluationReference evaluation,
        RecipeContractReference acceptancePolicy, CalibrationGovernanceActor actor,
        DateTimeOffset recordedAtUtc)
    {
        CalibrationGovernanceRecordValidation.Identity(position, operationId, actor);
        if (profileId == Guid.Empty || version < 1)
            throw new ArgumentException("CalibrationProfilePublicationIdentityInvalid");
        if (previous is not null && (previous.ProfileId != profileId || previous.Version >= version))
            throw new ArgumentException("CalibrationProfilePublicationPredecessorInvalid", nameof(previous));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        SourceCandidate = sourceCandidate ?? throw new ArgumentNullException(nameof(sourceCandidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        AcceptancePolicy = acceptancePolicy ?? throw new ArgumentNullException(nameof(acceptancePolicy));
        if (Content.SourceEvidenceHash != SourceCandidate.CandidateContentHash)
            throw new ArgumentException("CalibrationProfileSourceCandidateMismatch", nameof(sourceCandidate));
        if (Content.Requirement.AcceptancePolicy != AcceptancePolicy)
            throw new ArgumentException("CalibrationProfileAcceptancePolicyMismatch", nameof(acceptancePolicy));

        Position = position;
        OperationId = operationId;
        ProfileId = profileId;
        Version = version;
        Previous = previous;
        Actor = actor;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-profile-publication-v1",
            Position.ToString(CultureInfo.InvariantCulture), OperationId.ToString("D"),
            ProfileId.ToString("D"), Version.ToString(CultureInfo.InvariantCulture),
            Previous?.ProfileId.ToString("D"), Previous?.Version.ToString(CultureInfo.InvariantCulture),
            Previous?.ContentHash, Content.ContentHash,
            SourceCandidate.ContentHash, Evaluation.ContentHash,
            AcceptancePolicy.Id, AcceptancePolicy.Version, AcceptancePolicy.ContentHash,
            Actor.ContentHash, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            "DevelopmentOnly"
        });
        Reference = new CalibrationProfileReference(ProfileId, Version, ContentHash);
    }

    public long Position { get; }
    public Guid OperationId { get; }
    public Guid ProfileId { get; }
    public long Version { get; }
    public CalibrationProfileReference? Previous { get; }
    public CalibrationProfileContent Content { get; }
    public CalibrationCandidateReference SourceCandidate { get; }
    public CalibrationPolicyEvaluationReference Evaluation { get; }
    public RecipeContractReference AcceptancePolicy { get; }
    public CalibrationGovernanceActor Actor { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public CalibrationProfileReference Reference { get; }
    public string ContentHash { get; }
    public bool DevelopmentOnly => true;
    public string EvidencePurpose => "DevelopmentOnly";
    public bool ProductionAuthority => false;
    public bool CanActivate => false;
}

/// <summary>Bounded opaque evidence bytes from an independent physical verifier.</summary>
public sealed class PhysicalCalibrationVerificationEvidencePayload
{
    public const int MaximumBytes = 64 * 1024;
    private readonly byte[] _canonicalBytes;

    public PhysicalCalibrationVerificationEvidencePayload(RecipeContractReference format,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        Format = format ?? throw new ArgumentNullException(nameof(format));
        if (canonicalBytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("PhysicalCalibrationVerificationEvidencePayloadSizeInvalid",
                nameof(canonicalBytes));
        _canonicalBytes = canonicalBytes.ToArray();
        CanonicalBytesHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_canonicalBytes));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-physical-calibration-verification-evidence-payload-v1",
            Format.Id, Format.Version, Format.ContentHash, CanonicalBytesHash
        });
    }

    public RecipeContractReference Format { get; }
    public int Length => _canonicalBytes.Length;
    public string CanonicalBytesHash { get; }
    public string ContentHash { get; }
    public byte[] GetBytes() => (byte[])_canonicalBytes.Clone();
}

/// <summary>Exact physical-verification submission retained beside its opaque evidence.</summary>
public sealed class PhysicalCalibrationVerificationSubmission
{
    internal const int MaximumMetricCount = 32;

    public PhysicalCalibrationVerificationSubmission(RecipeContractReference procedureContract,
        RecipeContractReference independentReference, DateTimeOffset performedAtUtc,
        IEnumerable<CalibrationQualityMetric> metrics,
        PhysicalCalibrationVerificationEvidencePayload evidence)
    {
        ProcedureContract = procedureContract ?? throw new ArgumentNullException(nameof(procedureContract));
        IndependentReference = independentReference ?? throw new ArgumentNullException(nameof(independentReference));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        var copied = AlgorithmContractValidation.Copy(metrics, nameof(metrics), MaximumMetricCount).ToArray();
        if (copied.Length is < 1 or > MaximumMetricCount)
            throw new ArgumentException("PhysicalCalibrationVerificationMetricsInvalid", nameof(metrics));
        if (copied.Select(metric => metric.Key).Distinct(StringComparer.Ordinal).Count() != copied.Length)
            throw new ArgumentException("CalibrationMetricDuplicate", nameof(metrics));
        Metrics = new ReadOnlyCollection<CalibrationQualityMetric>(copied
            .OrderBy(metric => metric.Key, StringComparer.Ordinal).ToArray());
        PerformedAtUtc = performedAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-physical-calibration-verification-submission-v1",
            ProcedureContract.Id, ProcedureContract.Version, ProcedureContract.ContentHash,
            IndependentReference.Id, IndependentReference.Version, IndependentReference.ContentHash,
            PerformedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Metrics.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Metrics.Select(CalibrationGovernanceRecordValidation.MetricHash)).Concat(new[]
        {
            Evidence.ContentHash
        }));
    }

    public RecipeContractReference ProcedureContract { get; }
    public RecipeContractReference IndependentReference { get; }
    public DateTimeOffset PerformedAtUtc { get; }
    public ReadOnlyCollection<CalibrationQualityMetric> Metrics { get; }
    public PhysicalCalibrationVerificationEvidencePayload Evidence { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable physical verification history row; validity is derived from its gates.</summary>
public sealed class PhysicalCalibrationVerificationRecord
{
    internal PhysicalCalibrationVerificationRecord(long position, Guid verificationId, Guid operationId,
        CalibrationProfileReference profile, RecipeContractReference policy,
        PhysicalCalibrationVerificationSubmission submission,
        IEnumerable<CalibrationMetricGateResult> gateResults, IEnumerable<string> bindingFailures,
        CalibrationGovernanceActor actor, DateTimeOffset recordedAtUtc, DateTimeOffset? validUntilUtc)
    {
        CalibrationGovernanceRecordValidation.Identity(position, operationId, actor);
        if (verificationId == Guid.Empty)
            throw new ArgumentException("PhysicalCalibrationVerificationIdInvalid", nameof(verificationId));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        GateResults = new ReadOnlyCollection<CalibrationMetricGateResult>(
            AlgorithmContractValidation.Copy(gateResults, nameof(gateResults), 32).ToArray());
        if (GateResults.Count is < 1 or > 32)
            throw new ArgumentException("PhysicalCalibrationVerificationGateResultsInvalid",
                nameof(gateResults));
        if (GateResults.Select(gate => gate.GateId).Distinct(StringComparer.Ordinal).Count() != GateResults.Count)
            throw new ArgumentException("CalibrationPolicyGateIdDuplicate", nameof(gateResults));
        BindingFailures = CalibrationGovernanceRecordValidation.CopyReasons(bindingFailures,
            nameof(bindingFailures), 32);
        Actor = actor;
        Position = position;
        VerificationId = verificationId;
        OperationId = operationId;
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        ValidUntilUtc = validUntilUtc?.ToUniversalTime();
        if (Passed)
        {
            if (ValidUntilUtc is null || ValidUntilUtc <= Submission.PerformedAtUtc)
                throw new ArgumentException("PhysicalCalibrationVerificationValidityInvalid",
                    nameof(validUntilUtc));
        }
        else if (ValidUntilUtc is not null)
        {
            throw new ArgumentException("PhysicalCalibrationVerificationFailedValidityUnexpected",
                nameof(validUntilUtc));
        }

        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-physical-calibration-verification-record-v1",
            Position.ToString(CultureInfo.InvariantCulture), VerificationId.ToString("D"),
            OperationId.ToString("D"), Profile.ProfileId.ToString("D"),
            Profile.Version.ToString(CultureInfo.InvariantCulture), Profile.ContentHash,
            Policy.Id, Policy.Version, Policy.ContentHash, Submission.ContentHash,
            GateResults.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(GateResults.Select(gate => gate.ContentHash)).Concat(new[]
        {
            BindingFailures.Count.ToString(CultureInfo.InvariantCulture)
        }).Concat(BindingFailures).Concat(new[]
        {
            Actor.ContentHash, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ValidUntilUtc?.ToString("O", CultureInfo.InvariantCulture)
        }));
        Reference = new PhysicalCalibrationVerificationReference(VerificationId, ContentHash);
    }

    public long Position { get; }
    public Guid VerificationId { get; }
    public Guid OperationId { get; }
    public CalibrationProfileReference Profile { get; }
    public RecipeContractReference Policy { get; }
    public PhysicalCalibrationVerificationSubmission Submission { get; }
    public ReadOnlyCollection<CalibrationMetricGateResult> GateResults { get; }
    public ReadOnlyCollection<string> BindingFailures { get; }
    public CalibrationGovernanceActor Actor { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public DateTimeOffset? ValidUntilUtc { get; }
    public PhysicalCalibrationVerificationReference Reference { get; }
    public string ContentHash { get; }
    public bool Passed => BindingFailures.Count == 0 && GateResults.Count != 0 &&
        GateResults.All(gate => gate.Outcome == CalibrationGateOutcome.Passed);
}

internal static class CalibrationGovernanceRecordValidation
{
    internal static void Identity(long position, Guid operationId, CalibrationGovernanceActor actor)
    {
        if (position < 1 || operationId == Guid.Empty)
            throw new ArgumentException("CalibrationGovernanceRecordIdentityInvalid");
        ArgumentNullException.ThrowIfNull(actor);
    }

    internal static string Hash(string value, string parameterName) =>
        CameraSetupValidation.Hash(value, parameterName);

    internal static ReadOnlyCollection<CalibrationGateSectionResult> CopySections(
        IEnumerable<CalibrationGateSectionResult> values)
    {
        var copied = AlgorithmContractValidation.Copy(values, nameof(values), 6).ToArray();
        if (copied.Length != 6 || copied.Select(section => section.Category)
                .Distinct().Count() != 6 ||
            Enum.GetValues<CalibrationAcceptanceGateCategory>().Any(category =>
                copied.All(section => section.Category != category)))
            throw new ArgumentException("CalibrationPolicyEvaluationSectionsInvalid", nameof(values));
        var expectedOrder = new[]
        {
            CalibrationAcceptanceGateCategory.Sample,
            CalibrationAcceptanceGateCategory.Coverage,
            CalibrationAcceptanceGateCategory.PoseDiversity,
            CalibrationAcceptanceGateCategory.MaximumPerImageResidual,
            CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
            CalibrationAcceptanceGateCategory.InvalidObservation
        };
        if (copied.Select(section => section.Category).SequenceEqual(expectedOrder) is false)
            throw new ArgumentException("CalibrationPolicyEvaluationSectionsNonCanonical", nameof(values));
        var required = copied.Where(section => section.Applicability == CalibrationPolicyApplicability.Required);
        if (required.Any(section => section.Gates.Count == 0))
            throw new ArgumentException("CalibrationPolicyEvaluationRequiredResultsInvalid", nameof(values));
        return new ReadOnlyCollection<CalibrationGateSectionResult>(copied);
    }

    internal static ReadOnlyCollection<string> CopyReasons(IEnumerable<string> values,
        string parameterName, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copied = AlgorithmContractValidation.Copy(values, parameterName, maximumCount)
            .Select(value => AlgorithmContractValidation.BoundedText(value, parameterName, 512))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new ReadOnlyCollection<string>(copied);
    }

    internal static string MetricHash(CalibrationQualityMetric metric) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-quality-metric-v1", metric.Key,
            metric.Value.ToString("R", CultureInfo.InvariantCulture), metric.Unit
        });
}
