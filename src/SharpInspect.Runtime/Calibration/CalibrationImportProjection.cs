using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

internal static class CalibrationImportProjection
{
    internal static CalibrationImportDecision Decide(CalibrationImportCommand command,
        IReadOnlyList<CalibrationImportRecord> records, IReadOnlyList<object> governance,
        CameraBindingRevision? binding, ImagingSetupRevision? imaging, CalibrationImportPreparation preparation,
        CalibrationGovernanceActor actor, DateTimeOffset now)
    {
        var position = records.Count + 1L;
        CalibrationImportDecision Reject(string reason) => new(null, reason);
        if (preparation.Failure is { } preparationFailure) return Reject(preparationFailure);
        if (command is ImportCalibrationPackageCommand import)
        {
            var manifest = preparation.Package?.Manifest;
            if (manifest is null || preparation.Artifact?.Sha256 != import.PackageHash ||
                preparation.Artifact.Length != import.Package.Length)
                return Reject("CalibrationImportPackageVerificationMissing");
            return new(new ImportedCalibrationCandidate(position, command.CorrelationId, actor, now,
                Guid.NewGuid(), import.PackageHash, import.Package.Length, manifest.PackageId,
                manifest.SourceStationId, manifest.ContentHash, import.Reason, import.AuthorizationTarget), "CalibrationImportCandidateRetained");
        }
        var candidateRef = CommandCandidate(command);
        var candidate = records.OfType<ImportedCalibrationCandidate>().SingleOrDefault(value => value.Reference == candidateRef);
        if (candidate is null) return Reject("CalibrationImportedCandidateUnavailable");
        if (now < candidate.RecordedAtUtc) return Reject("CalibrationImportTimeRegressed");
        if (preparation.VerifiedCandidate != candidate.Reference || preparation.Package is null ||
            preparation.Package.Manifest.ContentHash != candidate.SourceManifestHash)
            return Reject("CalibrationImportSourceVerificationMissing");
        if (command is RevalidateImportedCalibrationCommand revalidate)
        {
            var computation = preparation.Recomputation;
            var policy = CurrentPolicy(governance, revalidate.Requirement.AcceptancePolicy, now);
            if (policy is null) return Reject("CalibrationImportLocalPolicyHeadChanged");
            if (computation is null || computation.Candidate != candidate.Reference ||
                computation.Requirement != revalidate.Requirement || computation.Binding != revalidate.Binding ||
                computation.ImagingSetup != revalidate.ImagingSetup)
                return Reject("CalibrationImportLocalRecomputationMissing");
            if (CheckCurrent(computation.Binding, computation.ImagingSetup, computation.RequestedGeometry,
                computation.EffectiveGeometry, binding, imaging, preparation.Camera) is { } currentFailure)
                return Reject(currentFailure);
            var content = new CalibrationProfileContent(computation.Requirement, computation.Binding.Target,
                computation.ImagingSetup, computation.RequestedGeometry, computation.EffectiveGeometry,
                computation.Coefficients, computation.Procedure, computation.Evidence.ContentHash,
                actor.PrincipalId, now);
            var evaluation = new ImportedCalibrationEvaluation(position, command.CorrelationId, actor, now,
                candidate.Reference, binding!, content, computation.Sections, computation.Failures, computation.Evidence,
                revalidate.Reason, revalidate.AuthorizationTarget);
            return new(evaluation, evaluation.Passed ? "CalibrationImportLocalPolicyPassed" : "CalibrationImportLocalPolicyFailed");
        }
        var evaluationRef = command switch
        {
            VerifyImportedCalibrationCommand verify => verify.Evaluation,
            PublishImportedCalibrationCommand publish => publish.Evaluation,
            _ => null
        };
        var evaluationRecord = records.OfType<ImportedCalibrationEvaluation>()
            .LastOrDefault(value => value.Candidate == candidate.Reference);
        if (evaluationRecord is null || evaluationRecord.Reference != evaluationRef)
            return Reject("CalibrationImportEvaluationHeadChanged");
        if (!evaluationRecord.Passed) return Reject("CalibrationImportFailedCandidateCannotPublish");
        if (now < evaluationRecord.RecordedAtUtc) return Reject("CalibrationImportTimeRegressed");
        var currentPolicy = CurrentPolicy(governance, evaluationRecord.Content.Requirement.AcceptancePolicy, now);
        if (currentPolicy is null) return Reject("CalibrationImportLocalPolicyHeadChanged");
        if (CheckCurrent(evaluationRecord.Binding, evaluationRecord.Content.ImagingSetup,
            evaluationRecord.Content.RequestedGeometry, evaluationRecord.Content.EffectiveGeometry,
            binding, imaging, preparation.Camera) is { } currentReason) return Reject(currentReason);
        if (command is VerifyImportedCalibrationCommand physicalCommand)
        {
            var requirement = currentPolicy.PhysicalVerification;
            if (requirement.Applicability != CalibrationPolicyApplicability.Required)
                return Reject("CalibrationImportPhysicalVerificationNotApplicable");
            var submission = physicalCommand.Submission;
            if (submission.PerformedAtUtc < evaluationRecord.RecordedAtUtc || submission.PerformedAtUtc > now)
                return Reject("CalibrationImportLocalPhysicalEvidenceTimeInvalid");
            if (records.OfType<ImportedCalibrationPhysicalVerification>().Any(value =>
                value.Submission.Evidence.ContentHash == submission.Evidence.ContentHash) ||
                governance.OfType<PhysicalCalibrationVerificationRecord>().Any(value =>
                    value.Submission.Evidence.ContentHash == submission.Evidence.ContentHash))
                return Reject("CalibrationImportPhysicalEvidenceAlreadyRecorded");
            var failures = CalibrationGovernanceProjection.PhysicalBindingFailures(requirement, submission).ToList();
            var computed = preparation.PhysicalVerification;
            if (computed?.Witness is not { } witness || witness.OperationId != command.CorrelationId ||
                witness.Binding != binding || witness.ImagingSetup != evaluationRecord.Content.ImagingSetup ||
                witness.RequestedGeometry != evaluationRecord.Content.RequestedGeometry ||
                witness.EffectiveGeometry != evaluationRecord.Content.EffectiveGeometry ||
                witness.StartedAtUtc < evaluationRecord.RecordedAtUtc || witness.CompletedAtUtc > now)
                return Reject("CalibrationImportLocalPhysicalWitnessMissing");
            if (computed is null || computed.CandidateHash != candidate.ContentHash ||
                computed.EvaluationHash != evaluationRecord.ContentHash || computed.SubmissionHash != submission.ContentHash)
                failures.Add("CalibrationImportPhysicalComputationMissing");
            else if (computed.Failure is not null) failures.Add(computed.Failure);
            var gates = requirement.Gates.Select(value => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(value,
                computed?.Metrics ?? Array.Empty<CalibrationQualityMetric>())).ToArray();
            DateTimeOffset? due = null;
            if (failures.Count == 0 && gates.All(value => value.Outcome == CalibrationGateOutcome.Passed))
            {
                try { due = submission.PerformedAtUtc.Add(requirement.ValidityInterval!.Value); }
                catch (ArgumentOutOfRangeException) { failures.Add("CalibrationImportPhysicalValidityOutOfRange"); }
            }
            var record = new ImportedCalibrationPhysicalVerification(position, command.CorrelationId, actor, now,
                candidate.Reference, evaluationRecord.Reference, submission, gates, failures, due,
                physicalCommand.Reason, physicalCommand.AuthorizationTarget, witness);
            return new(record, record.Passed ? "CalibrationImportPhysicalVerificationPassed" : "CalibrationImportPhysicalVerificationFailed");
        }
        if (command is PublishImportedCalibrationCommand publishProfile)
        {
            if (records.OfType<PublishedImportedCalibrationProfile>().Any(value => value.ProfileId == publishProfile.ProfileId) ||
                governance.OfType<PublishedCalibrationProfileVersion>().Any(value => value.ProfileId == publishProfile.ProfileId) ||
                preparation.Package.Manifest.Source.Profile?.ProfileId == publishProfile.ProfileId)
                return Reject("CalibrationImportFreshLocalProfileIdentityRequired");
            ImportedCalibrationPhysicalVerification? physical = null;
            if (currentPolicy.PhysicalVerification.Applicability == CalibrationPolicyApplicability.Required)
            {
                physical = records.OfType<ImportedCalibrationPhysicalVerification>().LastOrDefault(value =>
                    value.Candidate == candidate.Reference && value.Evaluation == evaluationRecord.Reference);
                if (physical is null || physical.Reference != publishProfile.PhysicalVerification)
                    return Reject("CalibrationImportLocalPhysicalVerificationRequired");
                if (!physical.Passed) return Reject("CalibrationImportLocalPhysicalVerificationFailed");
                if (physical.RecordedAtUtc > now || physical.ValidUntilUtc is not { } due || now >= due)
                    return Reject("CalibrationImportLocalPhysicalVerificationExpired");
            }
            else if (publishProfile.PhysicalVerification is not null)
                return Reject("CalibrationImportUnexpectedPhysicalVerification");
            var evidenceHash = AlgorithmContractValidation.HashParts(new[]
            {
                "sharpinspect-imported-calibration-publication-evidence-v1", candidate.ContentHash,
                evaluationRecord.ContentHash, physical?.ContentHash
            });
            var evaluated = evaluationRecord.Content;
            var content = new CalibrationProfileContent(evaluated.Requirement, evaluated.Device, evaluated.ImagingSetup,
                evaluated.RequestedGeometry, evaluated.EffectiveGeometry, evaluated.Coefficients,
                evaluated.Procedure, evidenceHash, actor.PrincipalId, now);
            return new(new PublishedImportedCalibrationProfile(position, command.CorrelationId, actor, now,
                publishProfile.ProfileId, candidate.Reference, evaluationRecord.Reference, physical?.Reference, content,
                publishProfile.Reason, publishProfile.AuthorizationTarget),
                "CalibrationImportedDevelopmentProfilePublished");
        }
        return Reject("CalibrationImportCommandUnsupported");
    }

    internal static ImportedCalibrationCandidateReference? CommandCandidate(CalibrationImportCommand command) => command switch
    {
        RevalidateImportedCalibrationCommand value => value.Candidate,
        VerifyImportedCalibrationCommand value => value.Candidate,
        PublishImportedCalibrationCommand value => value.Candidate,
        _ => null
    };
    internal static CalibrationAcceptancePolicy? CurrentPolicy(IEnumerable<object> records,
        RecipeContractReference expected, DateTimeOffset now)
    {
        var current = records.OfType<CalibrationAcceptancePolicyRevision>()
            .LastOrDefault(value => value.Policy.Reference.Id == expected.Id);
        return current?.Policy.Reference == expected && current.RecordedAtUtc <= now ? current.Policy : null;
    }
    private static string? CheckCurrent(CameraBindingRevision expectedBinding, ImagingSetupRevisionReference expectedImaging,
        CalibrationFrameGeometry requested, CalibrationFrameGeometry effective, CameraBindingRevision? binding,
        ImagingSetupRevision? imaging, CameraSetupSnapshot? camera)
    {
        if (binding != expectedBinding || camera?.Binding != binding) return "CalibrationImportCameraBindingChanged";
        if (imaging is null || imaging.Binding != binding || ImagingSetupRevisionReference.FromRevision(imaging) != expectedImaging)
            return "CalibrationImportImagingRevisionChanged";
        if (camera?.Requested is null || CalibrationFrameGeometry.FromRequested(camera.Requested) != requested)
            return "CalibrationImportRequestedGeometryChanged";
        if (camera.Effective is null || CalibrationFrameGeometry.FromEffective(camera.Effective) != effective ||
            camera.Health.ProviderAvailability != CameraProviderAvailability.Available ||
            camera.Health.Connection != CameraConnectionState.Open || camera.Health.Configuration != CameraConfigurationState.Applied ||
            camera.Health.Acquisition != CameraAcquisitionState.Stopped)
            return "CalibrationImportEffectiveGeometryChanged";
        return null;
    }
}

internal sealed record CalibrationImportDecision(CalibrationImportRecord? Record, string ReasonCode);
internal sealed record CalibrationImportPreparation(
    CalibrationExportPackageCodec.CalibrationExportPackageContents? Package = null,
    CalibrationTransferArtifactReceipt? Artifact = null,
    ImportedCalibrationCandidateReference? VerifiedCandidate = null,
    CalibrationImportRecomputation? Recomputation = null,
    ImportedPhysicalVerificationComputation? PhysicalVerification = null,
    CameraSetupSnapshot? Camera = null, string? Failure = null);
