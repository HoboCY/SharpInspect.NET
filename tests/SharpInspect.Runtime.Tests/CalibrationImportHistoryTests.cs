using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationImportHistoryTests
{
    [Fact]
    public void V134_H01_CanonicalCandidateEvaluationPhysicalAndPublicationHistoryIsAccepted()
    {
        var fixture = CalibrationImportTestDataFactory.Create();

        CalibrationImportHistoryValidator.Validate(fixture.Records, fixture.Governance);
    }

    [Fact]
    public void V134_H02_PositionAndOperationIdentityAreContinuousAndUnique()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var candidate = fixture.Candidate;
        var shifted = new ImportedCalibrationCandidate(2, Guid.NewGuid(), candidate.Actor,
            candidate.RecordedAtUtc, candidate.CandidateId, candidate.PackageHash, candidate.PackageLength,
            candidate.SourcePackageId, candidate.SourceStationId, candidate.SourceManifestHash,
            candidate.Reason, candidate.AuthorizationTarget);

        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { shifted }, fixture.Governance));

        var duplicateOperation = new ImportedCalibrationCandidate(1, candidate.OperationId,
            candidate.Actor, candidate.RecordedAtUtc, Guid.Parse("12121212-1212-1212-1212-121212121212"),
            candidate.PackageHash, candidate.PackageLength, candidate.SourcePackageId,
            candidate.SourceStationId, candidate.SourceManifestHash, candidate.Reason,
            candidate.AuthorizationTarget);
        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { candidate, duplicateOperation }, fixture.Governance));
    }

    [Fact]
    public void V134_H03_ReasonAndAuthorizationTargetAreReconstructedFromExactRecordFields()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var candidate = fixture.Candidate;
        var tamperedTarget = new ImportedCalibrationCandidate(1, candidate.OperationId,
            candidate.Actor, candidate.RecordedAtUtc, candidate.CandidateId, candidate.PackageHash,
            candidate.PackageLength, candidate.SourcePackageId, candidate.SourceStationId,
            candidate.SourceManifestHash, candidate.Reason, CalibrationImportTestDataFactory.HashD);

        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { tamperedTarget }, fixture.Governance));
    }

    [Fact]
    public void V134_H04_EvaluationMustRetainExactCandidateAndPolicyEvidenceBindings()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var missingCandidate = new ImportedCalibrationCandidateReference(
            Guid.Parse("13131313-1313-1313-1313-131313131313"), CalibrationImportTestDataFactory.HashA);
        var evaluation = new ImportedCalibrationEvaluation(2, fixture.Evaluation.OperationId,
            fixture.Evaluation.Actor, fixture.Evaluation.RecordedAtUtc, missingCandidate,
            fixture.Evaluation.Binding, fixture.Evaluation.Content, fixture.Evaluation.Sections,
            fixture.Evaluation.Failures, fixture.Evaluation.ComputationEvidence,
            fixture.Evaluation.Reason, fixture.Evaluation.AuthorizationTarget);

        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { fixture.Candidate, evaluation }, fixture.Governance));
    }

    [Fact]
    public void V134_H05_PhysicalEvidenceCannotBeReplayedAndPublicationNeedsFreshIdentity()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var duplicatePhysicalReason = "Replay independent evidence";
        var duplicatePhysicalTarget = CalibrationImportTestDataFactory.HashParts(
            "sharpinspect-calibration-import-verify-command-v1",
            fixture.Candidate.Reference.CandidateId.ToString("D"), fixture.Candidate.Reference.ContentHash,
            fixture.Evaluation.Reference.OperationId.ToString("D"), fixture.Evaluation.Reference.ContentHash,
            fixture.Physical.Submission.ContentHash, duplicatePhysicalReason);
        var duplicatePhysical = new ImportedCalibrationPhysicalVerification(4,
            Guid.Parse("71717171-7171-7171-7171-717171717171"), fixture.Physical.Actor,
            fixture.Physical.RecordedAtUtc.AddMinutes(1), fixture.Physical.Candidate,
            fixture.Physical.Evaluation, fixture.Physical.Submission, fixture.Physical.Gates,
            fixture.Physical.Failures, fixture.Physical.ValidUntilUtc, duplicatePhysicalReason,
            duplicatePhysicalTarget, fixture.Physical.Witness);

        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { fixture.Candidate, fixture.Evaluation, fixture.Physical,
                duplicatePhysical }, fixture.Governance));

        var legacyId = CalibrationGovernanceCodecTests.CreateFixture().Profile.ProfileId;
        var publicationReason = "Reuse an existing profile identity";
        var publicationEvidence = CalibrationImportTestDataFactory.HashParts(
            "sharpinspect-imported-calibration-publication-evidence-v1", fixture.Candidate.ContentHash,
            fixture.Evaluation.ContentHash, fixture.Physical.ContentHash);
        var content = new CalibrationProfileContent(fixture.Evaluation.Content.Requirement,
            fixture.Evaluation.Content.Device, fixture.Evaluation.Content.ImagingSetup,
            fixture.Evaluation.Content.RequestedGeometry, fixture.Evaluation.Content.EffectiveGeometry,
            fixture.Evaluation.Content.Coefficients, fixture.Evaluation.Content.Procedure,
            publicationEvidence, fixture.Publication.Actor.PrincipalId, fixture.Publication.RecordedAtUtc);
        var target = CalibrationImportTestDataFactory.HashParts(
            "sharpinspect-calibration-import-publish-command-v1", legacyId.ToString("D"),
            fixture.Candidate.Reference.CandidateId.ToString("D"), fixture.Candidate.Reference.ContentHash,
            fixture.Evaluation.Reference.OperationId.ToString("D"), fixture.Evaluation.Reference.ContentHash,
            fixture.Physical.Reference.OperationId.ToString("D"), fixture.Physical.Reference.ContentHash,
            publicationReason);
        var colliding = new PublishedImportedCalibrationProfile(4,
            Guid.Parse("91919191-9191-9191-9191-919191919191"), fixture.Publication.Actor,
            fixture.Publication.RecordedAtUtc, legacyId, fixture.Candidate.Reference,
            fixture.Evaluation.Reference, fixture.Physical.Reference, content, publicationReason, target);

        var legacyFixture = CalibrationGovernanceCodecTests.CreateFixture();
        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { fixture.Candidate, fixture.Evaluation, fixture.Physical,
                colliding }, new object[] { fixture.PolicyRevision, legacyFixture.Profile }));
    }

    [Fact]
    public void V134_H06_GovernancePrefixCallbackIsEvaluatedOncePerImportRow()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var calls = 0;

        CalibrationImportHistoryValidator.Validate(fixture.Records, _ =>
        {
            calls++;
            return fixture.Governance;
        });

        Assert.Equal(fixture.Records.Count, calls);
    }

    [Fact]
    public void V134_H07_LaterGovernanceCannotAuthorizeAnEarlierImportEvaluation()
    {
        var fixture = CalibrationImportTestDataFactory.Create();

        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            fixture.Records, record => record.Position < fixture.Evaluation.Position
                ? fixture.Governance
                : Array.Empty<object>()));
    }

    [Fact]
    public void V134_H08_PhysicalHistoryRequiresWitnessBoundToOperationAndEvaluationSnapshot()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var invalidWitness = new CalibrationImportPhysicalWitness(
            Guid.Parse("72727272-7272-7272-7272-727272727272"),
            fixture.Physical.Witness!.RuntimeEpoch, fixture.Physical.Witness.Binding,
            fixture.Physical.Witness.ImagingSetup, fixture.Physical.Witness.RequestedGeometry,
            fixture.Physical.Witness.EffectiveGeometry, fixture.Physical.Witness.StartedAtUtc,
            fixture.Physical.Witness.CompletedAtUtc);
        var reason = "Record independent physical verification with mismatched operation";
        var target = CalibrationImportTestDataFactory.HashParts(
            "sharpinspect-calibration-import-verify-command-v1",
            fixture.Candidate.Reference.CandidateId.ToString("D"), fixture.Candidate.Reference.ContentHash,
            fixture.Evaluation.Reference.OperationId.ToString("D"), fixture.Evaluation.Reference.ContentHash,
            fixture.Physical.Submission.ContentHash, reason);
        var physical = new ImportedCalibrationPhysicalVerification(3,
            Guid.Parse("73737373-7373-7373-7373-737373737373"), fixture.Physical.Actor,
            fixture.Physical.RecordedAtUtc, fixture.Physical.Candidate, fixture.Physical.Evaluation,
            fixture.Physical.Submission, fixture.Physical.Gates, fixture.Physical.Failures,
            fixture.Physical.ValidUntilUtc, reason, target, invalidWitness);

        Assert.Throws<InvalidOperationException>(() => CalibrationImportHistoryValidator.Validate(
            new CalibrationImportRecord[] { fixture.Candidate, fixture.Evaluation, physical },
            fixture.Governance));
    }

    [Fact]
    public void V134_H09_GovernancePrefixIsSnapshotBeforeLaterListMutation()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var reusedPrefix = new List<object>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CalibrationImportHistoryValidator.Validate(fixture.Records, record =>
            {
                if (record.Position >= fixture.Physical.Position)
                    reusedPrefix.Add(fixture.PolicyRevision);

                return reusedPrefix;
            }));

        Assert.Contains("EvaluationPolicyTimeInvalid", exception.Message, StringComparison.Ordinal);
    }
}
