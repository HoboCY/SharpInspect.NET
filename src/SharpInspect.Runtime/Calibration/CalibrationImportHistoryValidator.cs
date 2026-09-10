using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Cross-record validation for the local quarantine ledger used by calibration imports.
///
/// The package bytes are deliberately outside this validator.  The import store verifies
/// those bytes independently; this class verifies the durable local facts and the exact
/// local recomputation proof retained in each evaluation.
/// </summary>
internal static class CalibrationImportHistoryValidator
{
    private const string LocalEvidenceFormat = "sharpinspect-local-import-recomputation-v1";
    private const string MissingComputationEvidence = "CalibrationImportLocalComputationEvidenceMissing";
    private const string InvalidExtractionEvidence = "CalibrationImportLocalExtractionEvidenceInvalid";

    private static readonly string[] LocalEvidenceProperties =
    {
        "Format", "Candidate", "PackageHash", "SourceManifestHash", "LocalRequirementHash",
        "LocalPolicy", "BindingHash", "ImagingSetup", "Procedure", "Input", "Observations",
        "Selection", "Coefficients", "QualityMetrics", "Diagnostics", "Evidence"
    };

    internal static void Validate(IReadOnlyList<CalibrationImportRecord> records,
        IReadOnlyList<object> governance)
    {
        ArgumentNullException.ThrowIfNull(governance);
        Validate(records, _ => governance);
    }

    /// <summary>
    /// Validates a history against the governance prefix visible before each import row.
    /// The callback is evaluated once per row, so callers can bind a row to the audit
    /// sequence prefix without making the cross-record pass quadratic.
    /// </summary>
    internal static void Validate(IReadOnlyList<CalibrationImportRecord> records,
        Func<CalibrationImportRecord, IReadOnlyList<object>> governanceBeforeRecord)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(governanceBeforeRecord);

        var operations = new HashSet<Guid>();
        var governanceByRecord = new IReadOnlyList<object>[records.Count];
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index] ?? throw Failure("RecordMissing");
            Require(record.Position == index + 1L, "PositionNonContiguous");
            Require(operations.Add(record.OperationId), "OperationNotUnique");
            Require(record.ContentHash.Length == 64 && IsUpperHex(record.ContentHash),
                "RecordContentHashInvalid");
            Require(record.AuthorizationTarget == ExpectedAuthorizationTarget(record),
                "AuthorizationTargetMismatch");
            var governancePrefix = governanceBeforeRecord(record) ??
                throw Failure("GovernancePrefixMissing");
            // Storage readers commonly reuse one append-only prefix list while walking
            // the audit stream. Capture each row's prefix now so a later append cannot
            // retroactively authorize an earlier import record.
            governanceByRecord[index] = governancePrefix.ToArray();
        }

        var candidates = records.OfType<ImportedCalibrationCandidate>().ToArray();
        Require(candidates.Select(value => value.CandidateId).Distinct().Count() == candidates.Length,
            "CandidateNotUnique");
        Require(candidates.Select(value => value.Reference).Distinct().Count() == candidates.Length,
            "CandidateReferenceNotUnique");

        var candidateByReference = candidates.ToDictionary(value => value.Reference);
        var evaluations = records.OfType<ImportedCalibrationEvaluation>().ToArray();
        var evaluationsByReference = new Dictionary<CalibrationImportEvidenceReference,
            ImportedCalibrationEvaluation>();
        foreach (var evaluation in evaluations)
        {
            ValidateEvaluation(evaluation, candidateByReference,
                governanceByRecord[(int)evaluation.Position - 1]);
            Require(evaluationsByReference.TryAdd(evaluation.Reference, evaluation),
                "EvaluationReferenceNotUnique");
        }

        var physical = records.OfType<ImportedCalibrationPhysicalVerification>().ToArray();
        var physicalByReference = new Dictionary<CalibrationImportEvidenceReference,
            ImportedCalibrationPhysicalVerification>();
        var usedPhysicalEvidence = new HashSet<string>(StringComparer.Ordinal);
        foreach (var verification in physical)
        {
            foreach (var value in governanceByRecord[(int)verification.Position - 1]
                .OfType<PhysicalCalibrationVerificationRecord>())
                usedPhysicalEvidence.Add(value.Submission.Evidence.ContentHash);
            ValidatePhysical(verification, candidateByReference, evaluationsByReference,
                governanceByRecord[(int)verification.Position - 1], usedPhysicalEvidence);
            Require(physicalByReference.TryAdd(verification.Reference, verification),
                "PhysicalReferenceNotUnique");
        }

        var importedProfileIds = new HashSet<Guid>();
        foreach (var publication in records.OfType<PublishedImportedCalibrationProfile>())
            Require(importedProfileIds.Add(publication.ProfileId), "PublicationProfileIdentityNotUnique");

        foreach (var publication in records.OfType<PublishedImportedCalibrationProfile>())
        {
            var legacyProfileIds = new HashSet<Guid>(governanceByRecord[(int)publication.Position - 1]
                .OfType<PublishedCalibrationProfileVersion>().Select(value => value.ProfileId));
            ValidatePublication(publication, candidateByReference, evaluationsByReference,
                physicalByReference, legacyProfileIds,
                governanceByRecord[(int)publication.Position - 1]);
        }
    }

    private static void ValidateEvaluation(ImportedCalibrationEvaluation evaluation,
        IReadOnlyDictionary<ImportedCalibrationCandidateReference, ImportedCalibrationCandidate> candidateByReference,
        IReadOnlyList<object> governance)
    {
        Require(candidateByReference.TryGetValue(evaluation.Candidate, out var candidate),
            "EvaluationCandidateMissing");
        var candidateValue = candidate ?? throw Failure("EvaluationCandidateMissing");
        Require(candidateValue.Position < evaluation.Position && candidateValue.RecordedAtUtc <= evaluation.RecordedAtUtc,
            "EvaluationCandidateTimeInvalid");
        Require(candidateValue.Reference == evaluation.Candidate, "EvaluationCandidateReferenceMismatch");

        var requirement = evaluation.Content.Requirement;
        var policyRevision = FindPolicyRevision(governance, requirement.AcceptancePolicy);
        Require(policyRevision is not null && policyRevision.RecordedAtUtc <= evaluation.RecordedAtUtc,
            "EvaluationPolicyTimeInvalid");
        var policy = policyRevision?.Policy;
        Require(policy is not null, "EvaluationPolicyMissing");
        Require(requirement.Kind == policy!.Kind &&
            requirement.LogicalPurpose == policy.LogicalPurpose &&
            requirement.CoefficientContract == policy.CoefficientContract,
            "EvaluationRequirementPolicyMismatch");
        Require(evaluation.Content.Procedure == policy.ProcedureContract,
            "EvaluationProcedurePolicyMismatch");
        Require(evaluation.Content.Device == evaluation.Binding.Target &&
            evaluation.Content.Requirement.LogicalCameraRole == evaluation.Binding.LogicalRole &&
            evaluation.Content.ImagingSetup.LogicalCameraRole == requirement.LogicalCameraRole,
            "EvaluationBindingMismatch");
        Require(IsBindingRevisionHashValid(evaluation.Binding), "EvaluationBindingHashMismatch");
        Require(evaluation.Content.CreatedBy == evaluation.Actor.PrincipalId &&
            evaluation.Content.CreatedAtUtc == evaluation.RecordedAtUtc,
            "EvaluationContentAuthorMismatch");
        Require(evaluation.Content.SourceEvidenceHash == evaluation.ComputationEvidenceHash,
            "EvaluationSourceEvidenceMismatch");
        Require(evaluation.Failures.Count <= 64, "EvaluationFailureCapacityExceeded");

        ValidateLocalEvidence(evaluation, candidateValue, policy!);
    }

    private static void ValidateLocalEvidence(ImportedCalibrationEvaluation evaluation,
        ImportedCalibrationCandidate candidate, CalibrationAcceptancePolicy policy)
    {
        var bytes = evaluation.ComputationEvidence.GetBytes();
        Require(bytes.Length is > 0 and <= CalibrationImportComputationEvidence.MaximumBytes,
            "ComputationEvidenceSizeInvalid");
        Require(Convert.ToHexString(SHA256.HashData(bytes)) == evaluation.ComputationEvidenceHash,
            "ComputationEvidenceHashMismatch");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw Failure("ComputationEvidenceJsonInvalid");
        }

        using (document)
        {
            var root = document.RootElement;
            RequireObjectProperties(root, LocalEvidenceProperties, "ComputationEvidenceShapeInvalid");
            Require(CanonicalJson(root).AsSpan().SequenceEqual(bytes),
                "ComputationEvidenceNonCanonical");
            Require(ReadString(root, "Format", "ComputationEvidenceFormatInvalid") == LocalEvidenceFormat,
                "ComputationEvidenceFormatInvalid");

            var candidateReference = ReadCandidateReference(
                Property(root, "Candidate", "ComputationEvidenceCandidateMissing"),
                "ComputationEvidenceCandidateInvalid");
            Require(candidateReference == candidate.Reference, "ComputationEvidenceCandidateMismatch");
            Require(ReadHash(root, "PackageHash", "ComputationEvidencePackageHashInvalid") == candidate.PackageHash,
                "ComputationEvidencePackageMismatch");
            Require(ReadHash(root, "SourceManifestHash", "ComputationEvidenceManifestHashInvalid") ==
                candidate.SourceManifestHash, "ComputationEvidenceManifestMismatch");
            Require(ReadHash(root, "LocalRequirementHash", "ComputationEvidenceRequirementHashInvalid") ==
                evaluation.Content.Requirement.ContentHash, "ComputationEvidenceRequirementMismatch");

            var localPolicy = ReadContract(Property(root, "LocalPolicy", "ComputationEvidencePolicyMissing"),
                "ComputationEvidencePolicyInvalid");
            Require(localPolicy == policy.Reference, "ComputationEvidencePolicyMismatch");
            Require(ReadHash(root, "BindingHash", "ComputationEvidenceBindingHashInvalid") ==
                evaluation.Binding.RevisionHash, "ComputationEvidenceBindingMismatch");
            RequireJsonEquals(Property(root, "ImagingSetup", "ComputationEvidenceImagingMissing"),
                evaluation.Content.ImagingSetup, "ComputationEvidenceImagingMismatch");

            var procedure = new CalibrationProcedureDescriptor(policy.ProcedureContract,
                policy.InputContract, policy.Kind);
            RequireJsonEquals(Property(root, "Procedure", "ComputationEvidenceProcedureMissing"), procedure,
                "ComputationEvidenceProcedureMismatch");

            var input = ReadInput(Property(root, "Input", "ComputationEvidenceInputMissing"), policy.InputContract);
            var observations = ReadObservations(
                Property(root, "Observations", "ComputationEvidenceObservationsMissing"), input.ContentHash,
                policy);
            var selection = ReadSelection(Property(root, "Selection", "ComputationEvidenceSelectionMissing"));
            Require(selection.Sufficient, "ComputationEvidenceSelectionInsufficient");
            Require(selection.IncludedFrameCount >= 1 &&
                selection.SufficientFeatureFrameCount >= 0 &&
                selection.SufficientFeatureFrameCount <= selection.IncludedFrameCount,
                "ComputationEvidenceSelectionCountsInvalid");
            Require(selection.ImageCoverage is >= 0d and <= 1d,
                "ComputationEvidenceSelectionCoverageInvalid");
            Require(selection.IncludedFrameCount <= observations.Count,
                "ComputationEvidenceSelectionObservationMismatch");
            var excludedCount = observations.Count - selection.IncludedFrameCount;

            var coefficients = ReadCoefficients(
                Property(root, "Coefficients", "ComputationEvidenceCoefficientsMissing"),
                evaluation.Content.Coefficients);
            _ = coefficients;
            var qualityMetrics = ReadQualityMetrics(
                Property(root, "QualityMetrics", "ComputationEvidenceMetricsMissing"));
            _ = ReadDiagnostics(Property(root, "Diagnostics", "ComputationEvidenceDiagnosticsMissing"));
            ReadComputationEvidence(Property(root, "Evidence", "ComputationEvidencePayloadMissing"),
                policy, evaluation.Failures);

            var invalidObservation = observations.Any(value => value.InvalidReceipt || value.FeatureCount == 0) ||
                selection.SufficientFeatureFrameCount != selection.IncludedFrameCount;
            var expectedSections = BuildSections(policy, selection, qualityMetrics, excludedCount,
                invalidObservation: false);
            var invalidSections = BuildSections(policy, selection, qualityMetrics, excludedCount,
                invalidObservation);

            // The compact local proof intentionally does not repeat the source exclusion
            // list.  An invalid receipt may belong to an excluded source frame, so accept
            // the exact normal or invalid-observation projection while retaining all
            // stored gate actuals and reasons.
            Require(SectionsEqual(expectedSections, evaluation.Sections) ||
                invalidObservation && SectionsEqual(invalidSections, evaluation.Sections),
                "EvaluationSectionsMismatch");
        }
    }

    private static IReadOnlyList<CalibrationGateSectionResult> BuildSections(
        CalibrationAcceptancePolicy policy, CalibrationSelectionEvaluation selection,
        IReadOnlyList<CalibrationQualityMetric> qualityMetrics, int excludedCount,
        bool invalidObservation)
    {
        var selectionMetrics = new[]
        {
            Metric(CalibrationPolicyFactReference.IncludedFrameCount, selection.IncludedFrameCount),
            Metric(CalibrationPolicyFactReference.SufficientFeatureFrameCount,
                selection.SufficientFeatureFrameCount),
            Metric(CalibrationPolicyFactReference.SelectionImageCoverage, selection.ImageCoverage),
            Metric(CalibrationPolicyFactReference.ExcludedFrameCount, excludedCount)
        };
        return policy.Sections.Select(section =>
            {
                if (section.Applicability == CalibrationPolicyApplicability.NotApplicable)
                    return new CalibrationGateSectionResult(section.Category, section.Applicability,
                        Array.Empty<CalibrationMetricGateResult>(), section.NotApplicableReason);

                var gates = section.Gates.Select(gate => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(
                    gate, IsSelectionFact(gate.Fact.Kind) ? selectionMetrics : qualityMetrics)).ToArray();
                if (section.Category == CalibrationAcceptanceGateCategory.InvalidObservation && invalidObservation)
                    gates = gates.Select(value => new CalibrationMetricGateResult(value.GateId,
                        CalibrationGateOutcome.Failed, value.ActualValue, value.ActualUnit,
                        InvalidExtractionEvidence)).ToArray();
                return new CalibrationGateSectionResult(section.Category, section.Applicability, gates);
            }).ToArray();
    }

    private static void ValidatePhysical(ImportedCalibrationPhysicalVerification verification,
        IReadOnlyDictionary<ImportedCalibrationCandidateReference, ImportedCalibrationCandidate> candidateByReference,
        IReadOnlyDictionary<CalibrationImportEvidenceReference, ImportedCalibrationEvaluation> evaluationsByReference,
        IReadOnlyList<object> governance, ISet<string> usedPhysicalEvidence)
    {
        Require(candidateByReference.TryGetValue(verification.Candidate, out var candidate),
            "PhysicalCandidateMissing");
        Require(evaluationsByReference.TryGetValue(verification.Evaluation, out var evaluation),
            "PhysicalEvaluationMissing");
        var candidateValue = candidate ?? throw Failure("PhysicalCandidateMissing");
        var evaluationValue = evaluation ?? throw Failure("PhysicalEvaluationMissing");
        Require(candidateValue.Reference == verification.Candidate && evaluationValue.Candidate == candidateValue.Reference,
            "PhysicalCandidateMismatch");
        Require(candidateValue.Position < evaluationValue.Position && evaluationValue.Position < verification.Position,
            "PhysicalHistoryOrderInvalid");
        Require(evaluationValue.Passed, "PhysicalEvaluationNotPassed");
        Require(evaluationValue.RecordedAtUtc <= verification.Submission.PerformedAtUtc &&
            verification.Submission.PerformedAtUtc <= verification.RecordedAtUtc,
            "PhysicalEvidenceTimeInvalid");

        var witness = verification.Witness ?? throw Failure("PhysicalWitnessMissing");
        Require(witness.OperationId == verification.OperationId,
            "PhysicalWitnessOperationMismatch");
        Require(witness.RuntimeEpoch != Guid.Empty, "PhysicalWitnessRuntimeEpochInvalid");
        Require(witness.Binding == evaluationValue.Binding && IsBindingRevisionHashValid(witness.Binding),
            "PhysicalWitnessBindingMismatch");
        Require(witness.ImagingSetup == evaluationValue.Content.ImagingSetup,
            "PhysicalWitnessImagingSetupMismatch");
        Require(witness.RequestedGeometry == evaluationValue.Content.RequestedGeometry &&
            witness.EffectiveGeometry == evaluationValue.Content.EffectiveGeometry,
            "PhysicalWitnessGeometryMismatch");
        Require(witness.StartedAtUtc >= evaluationValue.RecordedAtUtc &&
            witness.CompletedAtUtc >= witness.StartedAtUtc &&
            witness.CompletedAtUtc <= verification.RecordedAtUtc,
            "PhysicalWitnessTimeInvalid");
        Require(IsWitnessHashValid(witness), "PhysicalWitnessHashMismatch");
        Require(verification.ContentHash == ExpectedPhysicalContentHash(verification),
            "PhysicalContentHashMismatch");

        var policyRevision = FindPolicyRevision(governance, evaluationValue.Content.Requirement.AcceptancePolicy);
        Require(policyRevision is not null && policyRevision.RecordedAtUtc <= verification.RecordedAtUtc,
            "PhysicalPolicyTimeInvalid");
        var policy = policyRevision?.Policy;
        Require(policy is not null, "PhysicalPolicyMissing");
        var requirement = policy!.PhysicalVerification;
        Require(requirement.Applicability == CalibrationPolicyApplicability.Required,
            "PhysicalPolicyNotApplicable");
        var bindingFailures = CalibrationGovernanceProjection.PhysicalBindingFailures(requirement,
            verification.Submission).ToArray();
        if (verification.Passed)
            Require(bindingFailures.Length == 0, "PhysicalSubmissionBindingMismatch");
        else
            Require(bindingFailures.All(value => verification.Failures.Contains(value,
                StringComparer.Ordinal)), "PhysicalFailureBindingDiagnosticMissing");
        Require(usedPhysicalEvidence.Add(verification.Submission.Evidence.ContentHash),
            "PhysicalEvidenceReplay");
        Require(GateIds(verification.Gates).SequenceEqual(GateIds(requirement.Gates)),
            "PhysicalGateSetMismatch");

        if (verification.Passed)
        {
            var expected = requirement.Gates.Select(gate => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(
                gate, verification.Submission.Metrics)).ToArray();
            Require(expected.All(value => value.Outcome == CalibrationGateOutcome.Passed) &&
                GateResultsEqual(expected, verification.Gates), "PhysicalMetricsMismatch");
            DateTimeOffset due;
            try
            {
                due = verification.Submission.PerformedAtUtc.Add(requirement.ValidityInterval!.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw Failure("PhysicalValidityOutOfRange");
            }
            Require(verification.ValidUntilUtc == due, "PhysicalValidityMismatch");
        }
        else
        {
            Require(verification.ValidUntilUtc is null, "FailedPhysicalValidityUnexpected");
        }
    }

    private static void ValidatePublication(PublishedImportedCalibrationProfile publication,
        IReadOnlyDictionary<ImportedCalibrationCandidateReference, ImportedCalibrationCandidate> candidateByReference,
        IReadOnlyDictionary<CalibrationImportEvidenceReference, ImportedCalibrationEvaluation> evaluationsByReference,
        IReadOnlyDictionary<CalibrationImportEvidenceReference, ImportedCalibrationPhysicalVerification> physicalByReference,
        IReadOnlySet<Guid> legacyProfileIds, IReadOnlyList<object> governance)
    {
        Require(candidateByReference.TryGetValue(publication.Candidate, out var candidate),
            "PublicationCandidateMissing");
        Require(evaluationsByReference.TryGetValue(publication.Evaluation, out var evaluation),
            "PublicationEvaluationMissing");
        var candidateValue = candidate ?? throw Failure("PublicationCandidateMissing");
        var evaluationValue = evaluation ?? throw Failure("PublicationEvaluationMissing");
        Require(candidateValue.Reference == publication.Candidate && evaluationValue.Candidate == candidateValue.Reference,
            "PublicationLineageMismatch");
        Require(evaluationValue.Position < publication.Position && evaluationValue.Passed,
            "PublicationEvaluationNotPassed");
        Require(evaluationValue.RecordedAtUtc <= publication.RecordedAtUtc,
            "PublicationTimeInvalid");

        var latestEvaluation = evaluationsByReference.Values
            .Where(value => value.Candidate == candidateValue.Reference && value.Position < publication.Position)
            .OrderBy(value => value.Position).LastOrDefault();
        Require(latestEvaluation is not null && latestEvaluation.Reference == publication.Evaluation &&
            latestEvaluation.Passed, "PublicationEvaluationHeadMismatch");

        var policyRevision = FindPolicyRevision(governance, evaluationValue.Content.Requirement.AcceptancePolicy);
        Require(policyRevision is not null && policyRevision.RecordedAtUtc <= publication.RecordedAtUtc,
            "PublicationPolicyTimeInvalid");
        var policy = policyRevision?.Policy;
        Require(policy is not null, "PublicationPolicyMissing");
        ImportedCalibrationPhysicalVerification? physical = null;
        if (policy!.PhysicalVerification.Applicability == CalibrationPolicyApplicability.Required)
        {
            Require(publication.PhysicalVerification is not null &&
                physicalByReference.TryGetValue(publication.PhysicalVerification, out physical),
                "PublicationPhysicalMissing");
            var physicalValue = physical ?? throw Failure("PublicationPhysicalMissing");
            Require(physicalValue.Candidate == candidateValue.Reference && physicalValue.Evaluation == evaluationValue.Reference &&
                physicalValue.Position < publication.Position && physicalValue.Passed,
                "PublicationPhysicalMismatch");
            Require(physicalValue.RecordedAtUtc <= publication.RecordedAtUtc &&
                physicalValue.ValidUntilUtc is { } due && publication.RecordedAtUtc < due,
                "PublicationPhysicalExpired");
            var latestPhysical = physicalByReference.Values
                .Where(value => value.Candidate == candidateValue.Reference && value.Evaluation == evaluationValue.Reference &&
                    value.Position < publication.Position)
                .OrderBy(value => value.Position).LastOrDefault();
            Require(latestPhysical is not null && latestPhysical.Reference == physicalValue.Reference &&
                latestPhysical.Passed, "PublicationPhysicalHeadMismatch");
        }
        else
        {
            Require(publication.PhysicalVerification is null, "PublicationUnexpectedPhysical");
        }

        // The profile identity must be fresh in both governance ledgers.  Repeated
        // imported ids are checked before this method is called.
        Require(!legacyProfileIds.Contains(publication.ProfileId), "PublicationProfileIdentityNotFresh");
        Require(publication.DevelopmentOnly && !publication.ProductionAuthority && !publication.CanActivate,
            "PublicationProductionAuthorityLeak");
        Require(publication.Content.CreatedBy == publication.Actor.PrincipalId &&
            publication.Content.CreatedAtUtc == publication.RecordedAtUtc,
            "PublicationContentAuthorMismatch");
        Require(ContentMaterialEqual(publication.Content, evaluationValue.Content), "PublicationContentMismatch");
        var evidenceHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imported-calibration-publication-evidence-v1", candidateValue.ContentHash,
            evaluationValue.ContentHash, physical?.ContentHash
        });
        Require(publication.Content.SourceEvidenceHash == evidenceHash,
            "PublicationSourceEvidenceMismatch");
    }

    private static string ExpectedAuthorizationTarget(CalibrationImportRecord record) => record switch
    {
        ImportedCalibrationCandidate value => AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-import-command-v1", value.PackageHash, value.Reason
        }),
        ImportedCalibrationEvaluation value => AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-import-revalidate-command-v1", value.Candidate.CandidateId.ToString("D"),
            value.Candidate.ContentHash, value.Content.Requirement.ContentHash, value.Binding.RevisionHash,
            value.Content.ImagingSetup.RevisionHash, value.Reason
        }),
        ImportedCalibrationPhysicalVerification value => AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-import-verify-command-v1", value.Candidate.CandidateId.ToString("D"),
            value.Candidate.ContentHash, value.Evaluation.OperationId.ToString("D"),
            value.Evaluation.ContentHash, value.Submission.ContentHash, value.Reason
        }),
        PublishedImportedCalibrationProfile value => AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-import-publish-command-v1", value.ProfileId.ToString("D"),
            value.Candidate.CandidateId.ToString("D"), value.Candidate.ContentHash,
            value.Evaluation.OperationId.ToString("D"), value.Evaluation.ContentHash,
            value.PhysicalVerification?.OperationId.ToString("D"), value.PhysicalVerification?.ContentHash,
            value.Reason
        }),
        _ => throw Failure("RecordKindInvalid")
    };

    private static CalibrationAcceptancePolicyRevision? FindPolicyRevision(IReadOnlyList<object> governance,
        RecipeContractReference reference)
    {
        var matches = governance.OfType<CalibrationAcceptancePolicyRevision>()
            .Where(value => value.Policy.Reference == reference).ToArray();
        Require(matches.Length <= 1, "PolicyReferenceNotUnique");
        return matches.SingleOrDefault();
    }

    private static bool IsBindingRevisionHashValid(CameraBindingRevision value) =>
        value.RevisionHash == CameraBindingHash(new string?[]
        {
            "camera-binding-revision-v1", value.LogicalRole,
            value.Revision.ToString(CultureInfo.InvariantCulture), value.OperationId.ToString("D"),
            value.PreviousRevisionHash, value.Target.ContentHash, value.AuthorPrincipalId.ToString("D"),
            value.AuthorSessionId.ToString("D"),
            value.AuthorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), value.ChangeReason,
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });

    private static string CameraBindingHash(params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsWitnessHashValid(CalibrationImportPhysicalWitness value) =>
        value.ContentHash == AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-import-physical-witness-v1",
            value.OperationId.ToString("D"), value.RuntimeEpoch.ToString("D"),
            value.Binding.LogicalRole, value.Binding.Revision.ToString(CultureInfo.InvariantCulture),
            value.Binding.OperationId.ToString("D"), value.Binding.RevisionHash,
            value.Binding.Target.ContentHash, value.ImagingSetup.LogicalCameraRole,
            value.ImagingSetup.RevisionId.ToString("D"),
            value.ImagingSetup.Revision.ToString(CultureInfo.InvariantCulture),
            value.ImagingSetup.RevisionHash, value.RequestedGeometry.ContentHash,
            value.EffectiveGeometry.ContentHash,
            value.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });

    private static string ExpectedPhysicalContentHash(ImportedCalibrationPhysicalVerification value) =>
        AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imported-calibration-physical-verification-v1",
            value.Position.ToString(CultureInfo.InvariantCulture), value.OperationId.ToString("D"),
            value.Actor.ContentHash, value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.Reason, value.AuthorizationTarget, value.Candidate.CandidateId.ToString("D"),
            value.Candidate.ContentHash, value.Evaluation.OperationId.ToString("D"),
            value.Evaluation.ContentHash, value.Submission.ContentHash, value.Witness?.ContentHash,
            value.Gates.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(value.Gates.Select(gate => gate.ContentHash)).Concat(new[]
        {
            value.Failures.Count.ToString(CultureInfo.InvariantCulture)
        }).Concat(value.Failures).Append(value.ValidUntilUtc?.ToString("O", CultureInfo.InvariantCulture)));

    private static CalibrationImportInput ReadInput(JsonElement element, RecipeContractReference contract)
    {
        RequireObjectProperties(element, new[] { "ContentHash", "Bytes" }, "ComputationEvidenceInputInvalid");
        var bytes = ReadBase64(Property(element, "Bytes", "ComputationEvidenceInputBytesMissing"),
            CalibrationProcedureInputPayload.MaximumBytes, "ComputationEvidenceInputBytesInvalid");
        var input = new CalibrationProcedureInputPayload(contract, bytes);
        Require(ReadHash(element, "ContentHash", "ComputationEvidenceInputHashInvalid") == input.ContentHash,
            "ComputationEvidenceInputHashMismatch");
        return new(input.ContentHash, bytes);
    }

    private static CalibrationCoefficientPayload ReadCoefficients(JsonElement element,
        CalibrationCoefficientPayload expected)
    {
        RequireObjectProperties(element, new[] { "Format", "ContentHash", "Bytes" },
            "ComputationEvidenceCoefficientsInvalid");
        var format = ReadContract(Property(element, "Format", "ComputationEvidenceCoefficientFormatMissing"),
            "ComputationEvidenceCoefficientFormatInvalid");
        var bytes = ReadBase64(Property(element, "Bytes", "ComputationEvidenceCoefficientBytesMissing"),
            CalibrationCoefficientPayload.MaximumBytes, "ComputationEvidenceCoefficientBytesInvalid");
        var actual = new CalibrationCoefficientPayload(format, bytes);
        Require(actual.ContentHash == ReadHash(element, "ContentHash", "ComputationEvidenceCoefficientHashInvalid"),
            "ComputationEvidenceCoefficientHashMismatch");
        Require(ContentEqual(actual, expected), "ComputationEvidenceCoefficientMismatch");
        return actual;
    }

    private static void ReadComputationEvidence(JsonElement element, CalibrationAcceptancePolicy policy,
        IReadOnlyList<string> failures)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            Require(failures.Contains(MissingComputationEvidence, StringComparer.Ordinal),
                "ComputationEvidencePayloadMissing");
            return;
        }
        RequireObjectProperties(element, new[] { "Format", "ContentHash", "Bytes" },
            "ComputationEvidencePayloadInvalid");
        var format = ReadContract(Property(element, "Format", "ComputationEvidencePayloadFormatMissing"),
            "ComputationEvidencePayloadFormatInvalid");
        var bytes = ReadBase64(Property(element, "Bytes", "ComputationEvidencePayloadBytesMissing"),
            CalibrationComputationEvidencePayload.MaximumBytes, "ComputationEvidencePayloadBytesInvalid");
        var payload = new CalibrationComputationEvidencePayload(format, bytes);
        Require(payload.ContentHash == ReadHash(element, "ContentHash", "ComputationEvidencePayloadHashInvalid"),
            "ComputationEvidencePayloadHashMismatch");
        if (format != policy.ComputationEvidenceContract)
            Require(failures.Contains(MissingComputationEvidence, StringComparer.Ordinal),
                "ComputationEvidencePayloadPolicyMismatch");
    }

    private static IReadOnlyList<CalibrationQualityMetric> ReadQualityMetrics(JsonElement element)
    {
        Require(element.ValueKind == JsonValueKind.Array, "ComputationEvidenceMetricsInvalid");
        Require(element.GetArrayLength() <= CalibrationProcedureComputationResult.MaximumMetricCount,
            "ComputationEvidenceMetricCapacityExceeded");
        var metrics = new List<CalibrationQualityMetric>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObjectProperties(item, new[] { "Key", "Value", "Unit" }, "ComputationEvidenceMetricInvalid");
            var key = ReadString(item, "Key", "ComputationEvidenceMetricKeyInvalid");
            var value = ReadDouble(item, "Value", "ComputationEvidenceMetricValueInvalid");
            string? unit = ReadNullableString(item, "Unit", "ComputationEvidenceMetricUnitInvalid");
            metrics.Add(new CalibrationQualityMetric(key, value, unit));
        }
        Require(metrics.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() == metrics.Count,
            "ComputationEvidenceMetricDuplicate");
        return metrics;
    }

    private static IReadOnlyList<CalibrationProcedureDiagnostic> ReadDiagnostics(JsonElement element)
    {
        Require(element.ValueKind == JsonValueKind.Array, "ComputationEvidenceDiagnosticsInvalid");
        Require(element.GetArrayLength() <= CalibrationProcedureComputationResult.MaximumDiagnosticCount,
            "ComputationEvidenceDiagnosticCapacityExceeded");
        var diagnostics = new List<CalibrationProcedureDiagnostic>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObjectProperties(item, new[] { "Key", "Value" }, "ComputationEvidenceDiagnosticInvalid");
            diagnostics.Add(new CalibrationProcedureDiagnostic(ReadString(item, "Key",
                "ComputationEvidenceDiagnosticKeyInvalid"), ReadNullableString(item, "Value",
                "ComputationEvidenceDiagnosticValueInvalid")));
        }
        Require(diagnostics.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() == diagnostics.Count,
            "ComputationEvidenceDiagnosticDuplicate");
        return diagnostics;
    }

    private static IReadOnlyList<LocalObservation> ReadObservations(JsonElement element, string inputHash,
        CalibrationAcceptancePolicy policy)
    {
        Require(element.ValueKind == JsonValueKind.Array, "ComputationEvidenceObservationsInvalid");
        Require(element.GetArrayLength() <= CalibrationComputationContext<object>.MaximumObservationCount,
            "ComputationEvidenceObservationCapacityExceeded");
        var observations = new List<LocalObservation>();
        var frameIds = new HashSet<Guid>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObjectProperties(item, new[]
            {
                "FrameId", "SourceHash", "ObservationId", "InputHash", "Features", "Diagnostics", "Receipt"
            }, "ComputationEvidenceObservationInvalid");
            var frameId = ReadGuid(item, "FrameId", "ComputationEvidenceFrameIdInvalid");
            var observationId = ReadGuid(item, "ObservationId", "ComputationEvidenceObservationIdInvalid");
            Require(frameIds.Add(frameId), "ComputationEvidenceFrameDuplicate");
            var sourceHash = ReadHash(item, "SourceHash", "ComputationEvidenceSourceHashInvalid");
            Require(ReadHash(item, "InputHash", "ComputationEvidenceObservationInputHashInvalid") == inputHash,
                "ComputationEvidenceObservationInputMismatch");

            var features = ReadFeatures(Property(item, "Features", "ComputationEvidenceFeaturesMissing"));
            _ = ReadDiagnostics(Property(item, "Diagnostics", "ComputationEvidenceObservationDiagnosticsMissing"));
            var receiptElement = Property(item, "Receipt", "ComputationEvidenceReceiptMissing");
            var invalidReceipt = receiptElement.ValueKind == JsonValueKind.Null;
            if (!invalidReceipt)
            {
                RequireObjectProperties(receiptElement, new[] { "Format", "ContentHash", "Bytes" },
                    "ComputationEvidenceReceiptInvalid");
                var format = ReadContract(Property(receiptElement, "Format",
                    "ComputationEvidenceReceiptFormatMissing"), "ComputationEvidenceReceiptFormatInvalid");
                var receiptBytes = ReadBase64(Property(receiptElement, "Bytes",
                    "ComputationEvidenceReceiptBytesMissing"), CalibrationExtractionReceipt.MaximumBytes,
                    "ComputationEvidenceReceiptBytesInvalid");
                var receipt = new CalibrationExtractionReceipt(format, receiptBytes);
                Require(receipt.ContentHash == ReadHash(receiptElement, "ContentHash",
                    "ComputationEvidenceReceiptHashInvalid"), "ComputationEvidenceReceiptHashMismatch");
                invalidReceipt = format != policy.ExtractionReceiptContract;
            }
            observations.Add(new LocalObservation(frameId, sourceHash, observationId, features.Count,
                invalidReceipt));
        }
        Require(observations.Select(value => value.FrameId).SequenceEqual(observations
            .OrderBy(value => value.FrameId).Select(value => value.FrameId)),
            "ComputationEvidenceObservationOrderInvalid");
        return observations;
    }

    private static IReadOnlyList<CalibrationImageFeature> ReadFeatures(JsonElement element)
    {
        Require(element.ValueKind == JsonValueKind.Array, "ComputationEvidenceFeaturesInvalid");
        Require(element.GetArrayLength() <= CalibrationExtractionResult.MaximumFeatureCount,
            "ComputationEvidenceFeatureCapacityExceeded");
        var features = new List<CalibrationImageFeature>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObjectProperties(item, new[] { "StableFeatureId", "PixelX", "PixelY", "ContentHash" },
                "ComputationEvidenceFeatureInvalid");
            var feature = new CalibrationImageFeature(ReadString(item, "StableFeatureId",
                "ComputationEvidenceFeatureIdInvalid"), ReadDouble(item, "PixelX",
                "ComputationEvidenceFeatureXInvalid"), ReadDouble(item, "PixelY",
                "ComputationEvidenceFeatureYInvalid"));
            Require(feature.ContentHash == ReadHash(item, "ContentHash", "ComputationEvidenceFeatureHashInvalid"),
                "ComputationEvidenceFeatureHashMismatch");
            features.Add(feature);
        }
        Require(features.Select(value => value.StableFeatureId).Distinct(StringComparer.Ordinal).Count() ==
            features.Count, "ComputationEvidenceFeatureDuplicate");
        return features;
    }

    private static CalibrationSelectionEvaluation ReadSelection(JsonElement element)
    {
        RequireObjectProperties(element, new[]
        {
            "Sufficient", "ReasonCode", "IncludedFrameCount", "SufficientFeatureFrameCount", "ImageCoverage",
            "SelectionHash"
        }, "ComputationEvidenceSelectionInvalid");
        return new(ReadBoolean(element, "Sufficient", "ComputationEvidenceSelectionSufficientInvalid"),
            ReadString(element, "ReasonCode", "ComputationEvidenceSelectionReasonInvalid"),
            ReadInt(element, "IncludedFrameCount", "ComputationEvidenceSelectionIncludedInvalid"),
            ReadInt(element, "SufficientFeatureFrameCount", "ComputationEvidenceSelectionFeatureInvalid"),
            ReadDouble(element, "ImageCoverage", "ComputationEvidenceSelectionCoverageInvalid"),
            ReadHash(element, "SelectionHash", "ComputationEvidenceSelectionHashInvalid"));
    }

    private static ImportedCalibrationCandidateReference ReadCandidateReference(JsonElement element, string reason)
    {
        RequireObjectProperties(element, new[] { "CandidateId", "ContentHash" }, reason);
        return new(ReadGuid(element, "CandidateId", reason), ReadHash(element, "ContentHash", reason));
    }

    private static RecipeContractReference ReadContract(JsonElement element, string reason)
    {
        RequireObjectProperties(element, new[] { "Id", "Version", "ContentHash" }, reason);
        try
        {
            return new(ReadString(element, "Id", reason), ReadString(element, "Version", reason),
                ReadHash(element, "ContentHash", reason));
        }
        catch (ArgumentException exception)
        {
            throw Failure(reason, exception);
        }
    }

    private static byte[] ReadBase64(JsonElement element, int maximumBytes, string reason)
    {
        Require(element.ValueKind == JsonValueKind.String, reason);
        var text = element.GetString()!;
        Require(text.Length > 0 && text.Length <= ((maximumBytes + 2L) / 3) * 4, reason);
        try
        {
            var bytes = Convert.FromBase64String(text);
            Require(bytes.Length > 0 && bytes.Length <= maximumBytes &&
                Convert.ToBase64String(bytes) == text, reason);
            return bytes;
        }
        catch (FormatException exception)
        {
            throw Failure(reason, exception);
        }
    }

    private static JsonElement Property(JsonElement element, string name, string reason)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            throw Failure(reason);
        return value;
    }

    private static string ReadString(JsonElement element, string name, string reason)
    {
        var value = Property(element, name, reason);
        Require(value.ValueKind == JsonValueKind.String, reason);
        return value.GetString() ?? throw Failure(reason);
    }

    private static string? ReadNullableString(JsonElement element, string name, string reason)
    {
        var value = Property(element, name, reason);
        if (value.ValueKind == JsonValueKind.Null) return null;
        Require(value.ValueKind == JsonValueKind.String, reason);
        return value.GetString();
    }

    private static string ReadHash(JsonElement element, string name, string reason)
    {
        var value = ReadString(element, name, reason);
        Require(IsUpperHex(value), reason);
        return value;
    }

    private static Guid ReadGuid(JsonElement element, string name, string reason)
    {
        var text = ReadString(element, name, reason);
        Require(Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty, reason);
        return value;
    }

    private static int ReadInt(JsonElement element, string name, string reason)
    {
        var value = Property(element, name, reason);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw Failure(reason);
        return result;
    }

    private static double ReadDouble(JsonElement element, string name, string reason)
    {
        var value = Property(element, name, reason);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) ||
            !double.IsFinite(result))
            throw Failure(reason);
        return result == 0d ? 0d : result;
    }

    private static bool ReadBoolean(JsonElement element, string name, string reason)
    {
        var value = Property(element, name, reason);
        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, reason);
        return value.GetBoolean();
    }

    private static void RequireObjectProperties(JsonElement element, IReadOnlyList<string> expected,
        string reason)
    {
        Require(element.ValueKind == JsonValueKind.Object, reason);
        var actual = element.EnumerateObject().Select(value => value.Name).ToArray();
        Require(actual.Length == expected.Count && actual.SequenceEqual(expected) &&
            actual.Distinct(StringComparer.Ordinal).Count() == actual.Length, reason);
    }

    private static byte[] CanonicalJson(JsonElement element) => JsonSerializer.SerializeToUtf8Bytes(element);

    private static void RequireJsonEquals(JsonElement actual, object expected, string reason)
    {
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected);
        Require(CanonicalJson(actual).AsSpan().SequenceEqual(expectedBytes), reason);
    }

    private static CalibrationQualityMetric Metric(CalibrationPolicyFactReference fact, double value) =>
        new(fact.Key, value, fact.Unit);

    private static bool IsSelectionFact(CalibrationPolicyFactKind kind) => kind is
        CalibrationPolicyFactKind.IncludedFrameCount or CalibrationPolicyFactKind.SufficientFeatureFrameCount or
        CalibrationPolicyFactKind.SelectionImageCoverage or CalibrationPolicyFactKind.ExcludedFrameCount;

    private static IReadOnlyList<string> GateIds(IEnumerable<CalibrationMetricGate> gates) =>
        gates.Select(value => value.GateId).ToArray();

    private static IReadOnlyList<string> GateIds(IEnumerable<CalibrationMetricGateResult> gates) =>
        gates.Select(value => value.GateId).ToArray();

    private static bool GateResultsEqual(IEnumerable<CalibrationMetricGateResult> first,
        IEnumerable<CalibrationMetricGateResult> second) => first.Zip(second, (left, right) =>
        left.GateId == right.GateId && left.Outcome == right.Outcome && left.ActualValue == right.ActualValue &&
        left.ActualUnit == right.ActualUnit && left.ReasonCode == right.ReasonCode &&
        left.ContentHash == right.ContentHash).All(value => value) &&
        first.Count() == second.Count();

    private static bool SectionsEqual(IEnumerable<CalibrationGateSectionResult> first,
        IEnumerable<CalibrationGateSectionResult> second) => first.Zip(second, SectionEqual).All(value => value) &&
        first.Count() == second.Count();

    private static bool SectionEqual(CalibrationGateSectionResult left, CalibrationGateSectionResult right) =>
        left.Category == right.Category && left.Applicability == right.Applicability &&
        left.NotApplicableReason == right.NotApplicableReason && left.ContentHash == right.ContentHash &&
        GateResultsEqual(left.Gates, right.Gates);

    private static bool ContentEqual(CalibrationCoefficientPayload left, CalibrationCoefficientPayload right) =>
        left.Format == right.Format && left.ContentHash == right.ContentHash &&
        left.GetBytes().AsSpan().SequenceEqual(right.GetBytes());

    private static bool ContentMaterialEqual(CalibrationProfileContent left, CalibrationProfileContent right) =>
        left.Requirement == right.Requirement && left.Device == right.Device &&
        left.ImagingSetup == right.ImagingSetup && left.RequestedGeometry == right.RequestedGeometry &&
        left.EffectiveGeometry == right.EffectiveGeometry && ContentEqual(left.Coefficients, right.Coefficients) &&
        left.Procedure == right.Procedure;

    private static bool IsUpperHex(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static InvalidOperationException Failure(string reason, Exception? inner = null) =>
        new("CalibrationImportHistory" + reason, inner);

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw Failure(reason);
    }

    private sealed record CalibrationImportInput(string ContentHash, byte[] Bytes);
    private sealed record LocalObservation(Guid FrameId, string SourceHash, Guid ObservationId,
        int FeatureCount, bool InvalidReceipt);
}
