#pragma warning disable CA1416

using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Runtime governance integration tests.  The existing V124 fixture supplies the real
/// schema-14 session, camera, identity and Step-Up path; the opt-in governance option
/// upgrades that same database to the schema-15 ledger.
/// </summary>
public sealed class CalibrationGovernanceRuntimeTests
{
    [Fact]
    public async Task V127_R01_RuntimeRetainsEvidencePublishesDevelopmentProfileAndVerifiesPhysicalRecord()
    {
        // The fixture performs real SQLite/audit/device startup before the first candidate.
        // Keep the injected governance clock ahead of that bounded setup so a valid
        // candidate cannot be rejected merely because wall-clock work elapsed.
        var now = DateTimeOffset.UtcNow.AddMinutes(5);
        var verifier = new TestPhysicalVerifier();
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, withCalibrationGovernance: true,
            utcNow: () => now, withGovernanceEvidence: true,
            physicalCalibrationVerificationRegistry: new PhysicalCalibrationVerificationRegistry(
                new[] { verifier }));

        await fixture.WaitForHealthySourceAsync();
        var policy = fixture.GovernancePolicy!;
        var publishedPolicy = await PublishPolicyAsync(fixture, policy);
        Assert.True(publishedPolicy.Outcome.Disposition == CommandDisposition.Accepted, publishedPolicy.Outcome.ReasonCode);
        Assert.Equal("CalibrationAcceptancePolicyPublished", publishedPolicy.Outcome.ReasonCode);
        Assert.NotNull(publishedPolicy.Revision);
        Assert.Equal(policy.Reference, publishedPolicy.Revision!.Policy.Reference);

        var retained = await CaptureCandidateAsync(fixture);
        var session = retained.Evidence;
        var candidate = session.Candidate!;
        Assert.Equal(CalibrationSessionPhase.CandidateRetained, session.State.Phase);
        Assert.Equal(policy.Reference, session.Header.Command.Plan.Requirement.AcceptancePolicy);
        Assert.NotNull(candidate.Result.Evidence);
        Assert.Equal(fixture.Procedure.ComputationEvidenceContract,
            candidate.Result.Evidence!.Format);
        Assert.All(session.Observations, observation =>
        {
            Assert.NotNull(observation.Result.Receipt);
            Assert.Equal(fixture.Procedure.ExtractionReceiptContract,
                observation.Result.Receipt!.Format);
            Assert.Equal(16, observation.Result.Receipt!.Length);
        });

        var evaluation = await EvaluateAsync(fixture, policy, retained.Candidate);
        Assert.True(evaluation.Outcome.Disposition == CommandDisposition.Accepted, evaluation.Outcome.ReasonCode);
        Assert.Equal("CalibrationDevelopmentPolicyPassed", evaluation.Outcome.ReasonCode);
        Assert.NotNull(evaluation.Evaluation);
        Assert.True(evaluation.Evaluation!.Passed);
        Assert.Equal(policy.Reference, evaluation.Evaluation.Policy);
        Assert.Equal(6, evaluation.Evaluation.Sections.Count);

        var profileResult = await PublishProfileAsync(fixture, retained.Candidate,
            evaluation.Evaluation!.Reference);
        Assert.True(profileResult.Outcome.Disposition == CommandDisposition.Accepted, profileResult.Outcome.ReasonCode);
        Assert.Equal("CalibrationDevelopmentProfilePublished", profileResult.Outcome.ReasonCode);
        Assert.NotNull(profileResult.Profile);
        var profile = profileResult.Profile!;
        Assert.True(profile.DevelopmentOnly);
        Assert.False(profile.ProductionAuthority);
        Assert.False(profile.CanActivate);
        Assert.Equal(retained.Candidate.CandidateContentHash, profile.Content.SourceEvidenceHash);

        var exactPolicy = await fixture.Runtime.ReadPolicyAsync(policy.Reference, fixture.User.Invocation);
        var exactEvaluation = await fixture.Runtime.ReadEvaluationAsync(
            evaluation.Evaluation!.Reference, fixture.User.Invocation);
        var exactProfile = await fixture.Runtime.ReadProfileAsync(
            profile.Reference, fixture.User.Invocation);
        Assert.True(exactPolicy.Available, exactPolicy.ReasonCode);
        Assert.True(exactEvaluation.Available, exactEvaluation.ReasonCode);
        Assert.True(exactProfile.Available, exactProfile.ReasonCode);
        Assert.Equal(policy.ContentHash, exactPolicy.Value!.Policy.ContentHash);
        Assert.Equal(evaluation.Evaluation!.ContentHash, exactEvaluation.Value!.ContentHash);
        Assert.Equal(profile.ContentHash, exactProfile.Value!.ContentHash);

        var independent = new RecipeContractReference("v127-independent-reference", "1", Hash);
        var physicalEvidence = new PhysicalCalibrationVerificationEvidencePayload(
            verifier.EvidenceContract, new byte[] { 1, 2, 3 });
        var submission = new PhysicalCalibrationVerificationSubmission(
            verifier.ProcedureContract, independent, now,
            new[] { new CalibrationQualityMetric("physical-fit", 6, "unit") }, physicalEvidence);
        var verification = await RecordPhysicalAsync(fixture, profile, submission);
        Assert.True(verification.Outcome.Disposition == CommandDisposition.Accepted, verification.Outcome.ReasonCode);
        Assert.Equal("PhysicalCalibrationVerificationPassed", verification.Outcome.ReasonCode);
        Assert.NotNull(verification.Verification);
        Assert.True(verification.Verification!.Passed);
        Assert.NotNull(verification.Verification!.ValidUntilUtc);
        Assert.Equal(1, verifier.CallCount);

        var validity = await fixture.Runtime.GetValidityAsync(profile.Reference, fixture.User.Invocation);
        Assert.True(validity.Available, validity.ReasonCode);
        var validityValue = validity.Value!;
        Assert.Equal(CalibrationVerificationState.Current, validityValue.Verification);
        // This fixture supplies the recovery acquisition path but does not open a
        // live CameraSetupRuntime slot. Validity must not invent current geometry.
        Assert.Equal(CalibrationCompatibilityState.Unavailable, validityValue.Compatibility);
        Assert.Equal(verification.Verification!.Reference, validityValue.LatestVerification);
        Assert.False(validityValue.CanAdmitNewProductionTrigger);
        Assert.False(validityValue.ProductionAuthority);

        // A later policy revision does not rewrite the exact policy bound to this Profile.
        var nextPolicy = fixture.CreateGovernancePolicy("2", physicalValidity: TimeSpan.FromHours(2));
        var nextPublished = await PublishPolicyAsync(fixture, nextPolicy, policy.Reference);
        Assert.True(nextPublished.Outcome.Disposition == CommandDisposition.Accepted, nextPublished.Outcome.ReasonCode);
        Assert.Equal(nextPolicy.Reference, nextPublished.Revision!.Policy.Reference);
        var stillCurrent = await fixture.Runtime.GetValidityAsync(profile.Reference, fixture.User.Invocation);
        Assert.True(stillCurrent.Available, stillCurrent.ReasonCode);
        var stillCurrentValue = stillCurrent.Value!;
        Assert.Equal(policy.Reference, stillCurrentValue.Policy);
        Assert.Equal(verification.Verification!.ValidUntilUtc, stillCurrentValue.ValidUntilUtc);

        now = verification.Verification!.ValidUntilUtc!.Value.AddTicks(1);
        var expired = await fixture.Runtime.GetValidityAsync(profile.Reference, fixture.User.Invocation);
        Assert.True(expired.Available, expired.ReasonCode);
        var expiredValue = expired.Value!;
        Assert.Equal(CalibrationVerificationState.Expired, expiredValue.Verification);
        Assert.Contains("CalibrationVerificationOverdue", expiredValue.ReasonCodes);

        // The same opaque evidence plus independent reference is a replay, even with a new command id.
        var replay = await RecordPhysicalAsync(fixture, profile, submission);
        Assert.Equal(CommandDisposition.Rejected, replay.Outcome.Disposition);
        Assert.Equal("PhysicalVerificationEvidenceAlreadyRecorded", replay.Outcome.ReasonCode);
        Assert.Null(replay.Verification);

        // A fresh independent evidence result can renew validity after the clock
        // advanced beyond expiry; no hidden audit-time window prevents the append.
        var renewedSubmission = new PhysicalCalibrationVerificationSubmission(
            verifier.ProcedureContract, independent, now,
            new[] { new CalibrationQualityMetric("physical-fit", 9, "unit") },
            new PhysicalCalibrationVerificationEvidencePayload(verifier.EvidenceContract, new byte[] { 2, 3, 4 }));
        var renewed = await RecordPhysicalAsync(fixture, profile, renewedSubmission);
        Assert.True(renewed.Outcome.Disposition == CommandDisposition.Accepted, renewed.Outcome.ReasonCode);
        Assert.True(renewed.Verification!.Passed);

        // Reopen the actual schema-15 store and validate the signed ledger without a new device.
        await StopFixtureForStoreRestartAsync(fixture);
        await using var reopened = new SqliteCommandStore(fixture.Options);
        var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await CalibrationSessionRuntimeTests.Fixture.WaitForVerifiedAsync(reopened);
        var ledger = await reopened.ReadCalibrationGovernanceAsync();
        Assert.True(ledger.Available, ledger.ReasonCode);
        Assert.Equal(6, ledger.Records.Count);
        Assert.Equal(6, ledger.Records.Select(value => value.Position).Distinct().Count());
        Assert.All(ledger.Records, value => Assert.NotEmpty(value.AuditHash));
        var retainedAfterRestart = await reopened.ReadCalibrationSessionAsync(retained.SessionId);
        Assert.True(retainedAfterRestart.Available, retainedAfterRestart.ReasonCode);
        var restartedEvidence = retainedAfterRestart.Evidence!;
        var restartedCandidate = restartedEvidence.Candidate!;
        Assert.Equal(candidate.ContentHash, restartedCandidate.ContentHash);
        Assert.Equal(candidate.Result.Evidence!.ContentHash,
            restartedCandidate.Result.Evidence!.ContentHash);
    }

    [Fact]
    public async Task V127_R02_MissingStepUpAndFailedEvaluationCannotPublishProfile()
    {
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, withCalibrationGovernance: true,
            withGovernanceEvidence: true, governanceSampleThreshold: 2);
        await fixture.WaitForHealthySourceAsync();
        var policy = fixture.GovernancePolicy!;

        var withoutStepUp = new PublishCalibrationAcceptancePolicyCommand(
            Guid.NewGuid(), fixture.User.Invocation, policy, null, "V127 missing Step-Up");
        var denied = await fixture.Runtime.PublishPolicyAsync(withoutStepUp);
        Assert.Equal(CommandDisposition.Rejected, denied.Outcome.Disposition);
        Assert.Equal("StepUpRequired", denied.Outcome.ReasonCode);

        var published = await PublishPolicyAsync(fixture, policy);
        Assert.True(published.Outcome.Disposition == CommandDisposition.Accepted, published.Outcome.ReasonCode);
        var retained = await CaptureCandidateAsync(fixture);
        var evaluation = await EvaluateAsync(fixture, policy, retained.Candidate);
        Assert.True(evaluation.Outcome.Disposition == CommandDisposition.Accepted, evaluation.Outcome.ReasonCode);
        Assert.Equal("CalibrationPolicyEvaluationFailed", evaluation.Outcome.ReasonCode);
        Assert.NotNull(evaluation.Evaluation);
        Assert.False(evaluation.Evaluation!.Passed);
        Assert.Contains(evaluation.Evaluation!.Sections.SelectMany(section => section.Gates),
            gate => gate.GateId == "sample-frames" && gate.Outcome == CalibrationGateOutcome.Failed);

        var profile = await PublishProfileAsync(fixture, retained.Candidate,
            evaluation.Evaluation!.Reference);
        Assert.Equal(CommandDisposition.Rejected, profile.Outcome.Disposition);
        Assert.Equal("CalibrationFailedEvaluationCannotPublish", profile.Outcome.ReasonCode);
        Assert.Null(profile.Profile);
    }

    [Fact]
    public async Task V127_R03_SourceBinMutationFailsEvaluationAndProfilePublication()
    {
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, withCalibrationGovernance: true,
            withGovernanceEvidence: true);
        await fixture.WaitForHealthySourceAsync();
        var policy = fixture.GovernancePolicy!;
        var published = await PublishPolicyAsync(fixture, policy);
        Assert.True(published.Outcome.Disposition == CommandDisposition.Accepted, published.Outcome.ReasonCode);
        var retained = await CaptureCandidateAsync(fixture);
        var frame = Assert.Single(retained.Evidence.Frames);
        var path = Path.Combine(fixture.Options.CalibrationSessions!.EvidenceRoot, frame.RelativePath);
        var original = await File.ReadAllBytesAsync(path);
        try
        {
            var mutated = (byte[])original.Clone();
            mutated[0] ^= 0x7f;
            await File.WriteAllBytesAsync(path, mutated);

            var failedEvaluation = await EvaluateAsync(fixture, policy, retained.Candidate);
            Assert.True(failedEvaluation.Outcome.Disposition == CommandDisposition.Accepted, failedEvaluation.Outcome.ReasonCode);
            Assert.Equal("CalibrationPolicyEvaluationFailed", failedEvaluation.Outcome.ReasonCode);
            Assert.Contains("CalibrationSourceImagesUnavailable",
                failedEvaluation.Evaluation!.BindingFailures);

            await File.WriteAllBytesAsync(path, original);
            var goodEvaluation = await EvaluateAsync(fixture, policy, retained.Candidate);
            Assert.True(goodEvaluation.Evaluation!.Passed,
                string.Join(",", goodEvaluation.Evaluation!.BindingFailures));

            await File.WriteAllBytesAsync(path, mutated);
            var failedPublication = await PublishProfileAsync(fixture, retained.Candidate,
                goodEvaluation.Evaluation!.Reference);
            Assert.Equal(CommandDisposition.Rejected, failedPublication.Outcome.Disposition);
            Assert.Equal("CalibrationSourceImagesUnavailable", failedPublication.Outcome.ReasonCode);
            Assert.Null(failedPublication.Profile);
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original);
        }
    }

    [Fact]
    public async Task V127_R04_FailedPhysicalReverificationIsRetainedAndMakesValidityFailed()
    {
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, withCalibrationGovernance: true,
            withGovernanceEvidence: true, physicalCalibrationVerificationRegistry:
                new PhysicalCalibrationVerificationRegistry(new[] { new TestPhysicalVerifier() }));
        await fixture.WaitForHealthySourceAsync();
        var policy = fixture.GovernancePolicy!;
        Assert.Equal(CommandDisposition.Accepted,
            (await PublishPolicyAsync(fixture, policy)).Outcome.Disposition);
        var retained = await CaptureCandidateAsync(fixture);
        var evaluation = await EvaluateAsync(fixture, policy, retained.Candidate);
        Assert.True(evaluation.Evaluation!.Passed);
        var profileResult = await PublishProfileAsync(fixture, retained.Candidate,
            evaluation.Evaluation!.Reference);
        Assert.True(profileResult.Profile is not null, profileResult.Outcome.ReasonCode);
        var profile = profileResult.Profile!;

        var verifier = new TestPhysicalVerifier();
        // The fixture verifier is intentionally not reused: this command tests the
        // runtime's submitted-metric equality against its registered implementation.
        // The registry above remains the implementation that computes the actual value.
        var evidence = new PhysicalCalibrationVerificationEvidencePayload(
            verifier.EvidenceContract, new byte[] { 9, 9 });
        var submission = new PhysicalCalibrationVerificationSubmission(
            verifier.ProcedureContract,
            new RecipeContractReference("v127-independent-reference", "1", Hash),
            DateTimeOffset.UtcNow,
            new[] { new CalibrationQualityMetric("physical-fit", 7, "unit") }, evidence);
        var failed = await RecordPhysicalAsync(fixture, profile, submission);
        Assert.True(failed.Outcome.Disposition == CommandDisposition.Accepted, failed.Outcome.ReasonCode);
        Assert.Equal("PhysicalCalibrationVerificationFailed", failed.Outcome.ReasonCode);
        Assert.NotNull(failed.Verification);
        Assert.False(failed.Verification!.Passed);
        Assert.Contains("PhysicalVerificationSubmittedMetricsMismatch",
            failed.Verification!.BindingFailures);

        var validity = await fixture.Runtime.GetValidityAsync(profile.Reference, fixture.User.Invocation);
        Assert.True(validity.Available, validity.ReasonCode);
        var validityValue = validity.Value!;
        Assert.Equal(CalibrationVerificationState.Failed, validityValue.Verification);
        Assert.Equal(failed.Verification!.Reference, validityValue.LatestVerification);
    }

    private static async Task<CalibrationPolicyPublishResult> PublishPolicyAsync(
        CalibrationSessionRuntimeTests.Fixture fixture, CalibrationAcceptancePolicy policy,
        RecipeContractReference? expectedPrevious = null)
    {
        var command = new PublishCalibrationAcceptancePolicyCommand(Guid.NewGuid(),
            fixture.User.Invocation, policy, expectedPrevious, "V127 publish acceptance policy");
        var invocation = await fixture.GrantAsync(Permission.ManageCalibrationAcceptancePolicy,
            command.CorrelationId, command.AuthorizationTarget,
            AuditedCommandKind.PublishCalibrationAcceptancePolicy);
        return await fixture.Runtime.PublishPolicyAsync(command with { Invocation = invocation });
    }

    private static async Task<CalibrationCandidateEvaluationResult> EvaluateAsync(
        CalibrationSessionRuntimeTests.Fixture fixture, CalibrationAcceptancePolicy policy,
        CalibrationCandidateReference candidate)
    {
        var command = new EvaluateCalibrationCandidateCommand(Guid.NewGuid(),
            fixture.User.Invocation, candidate, policy.Reference);
        var invocation = await fixture.GrantAsync(Permission.PublishCalibration,
            command.CorrelationId, command.AuthorizationTarget,
            AuditedCommandKind.EvaluateCalibrationCandidate);
        return await fixture.Runtime.EvaluateCandidateAsync(command with { Invocation = invocation });
    }

    private static async Task<CalibrationProfilePublishResult> PublishProfileAsync(
        CalibrationSessionRuntimeTests.Fixture fixture, CalibrationCandidateReference candidate,
        CalibrationPolicyEvaluationReference evaluation)
    {
        var command = new PublishCalibrationProfileCommand(Guid.NewGuid(),
            fixture.User.Invocation, Guid.NewGuid(), null, candidate, evaluation,
            "V127 publish development profile");
        var invocation = await fixture.GrantAsync(Permission.PublishCalibration,
            command.CorrelationId, command.AuthorizationTarget,
            AuditedCommandKind.PublishCalibrationProfile);
        return await fixture.Runtime.PublishProfileAsync(command with { Invocation = invocation });
    }

    private static async Task<PhysicalCalibrationVerificationResult> RecordPhysicalAsync(
        CalibrationSessionRuntimeTests.Fixture fixture, PublishedCalibrationProfileVersion profile,
        PhysicalCalibrationVerificationSubmission submission)
    {
        var command = new RecordPhysicalCalibrationVerificationCommand(Guid.NewGuid(),
            fixture.User.Invocation, profile.Reference, submission,
            "V127 record physical verification");
        var invocation = await fixture.GrantAsync(
            Permission.RecordPhysicalCalibrationVerification, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.RecordPhysicalCalibrationVerification);
        return await fixture.Runtime.RecordPhysicalVerificationAsync(
            command with { Invocation = invocation });
    }

    private static async Task<RetainedCandidate> CaptureCandidateAsync(
        CalibrationSessionRuntimeTests.Fixture fixture)
    {
        var start = await fixture.CreateAuthorizedStartCommandAsync();
        var started = await fixture.Runtime.SubmitAsync(start);
        Assert.Equal(CommandDisposition.Accepted, started.Disposition);
        var collecting = await WaitForSnapshotAsync(fixture,
            value => value.CalibrationSession is
                { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false });
        var sessionId = collecting.CalibrationSession!.SessionId;
        var capture = await fixture.Runtime.SubmitAsync(new CaptureCalibrationFrameCommand(
            Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.Equal(CommandDisposition.Accepted, capture.Disposition);
        await WaitForSnapshotAsync(fixture, value => value.CalibrationSession is
            { ObservationCount: 1, OperationInProgress: false });
        var compute = await fixture.Runtime.SubmitAsync(new ComputeCalibrationCandidateCommand(
            Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.Equal(CommandDisposition.Accepted, compute.Disposition);
        await WaitForSnapshotAsync(fixture, value => value.CalibrationSession is
            { Phase: CalibrationSessionPhase.CandidateRetained, OperationInProgress: false });
        var query = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(query.Available, query.ReasonCode);
        Assert.NotNull(query.Evidence);
        Assert.NotNull(query.Evidence!.Candidate);
        var candidate = query.Evidence.Candidate!;
        var reference = new CalibrationCandidateReference(sessionId, candidate.CandidateId,
            candidate.ContentHash);
        await fixture.ExitAsync(sessionId);
        return new RetainedCandidate(sessionId, query.Evidence, reference);
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(
        CalibrationSessionRuntimeTests.Fixture fixture, Func<StationStateSnapshot, bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        var current = await fixture.Runtime.GetSnapshotAsync();
        while (!condition(current))
        {
            if (current.Store.State == HealthState.Faulted)
                throw new XunitException($"V127 store fault: {current.Store.ReasonCode}; " +
                    $"integrity={current.AuditIntegrity?.ReasonCode}; " +
                    $"calibration={current.CalibrationSession?.ReasonCode}");
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new XunitException($"V127 snapshot timeout at " +
                    $"{current.CalibrationSession?.Phase}/{current.CalibrationSession?.ReasonCode}");
            await Task.Delay(10);
            current = await fixture.Runtime.GetSnapshotAsync();
        }
        return current;
    }

    private static async Task StopFixtureForStoreRestartAsync(
        CalibrationSessionRuntimeTests.Fixture fixture)
    {
        await fixture.Runtime.DisposeAsync();
        await fixture.Recovery.DisposeAsync();
        fixture.Authorization.Dispose();
        await fixture.Sessions.DisposeAsync();
        await fixture.Store.DisposeAsync();
    }

    private sealed record RetainedCandidate(Guid SessionId,
        CalibrationSessionEvidence Evidence, CalibrationCandidateReference Candidate);

    private sealed class TestPhysicalVerifier : IPhysicalCalibrationVerificationProcedure
    {
        private int _callCount;

        internal int CallCount => Volatile.Read(ref _callCount);
        public RecipeContractReference ProcedureContract { get; } =
            new("v127-physical-procedure", "1", Hash);
        public RecipeContractReference EvidenceContract { get; } =
            new("v127-physical-evidence", "1", Hash);

        public IReadOnlyList<CalibrationQualityMetric> Evaluate(
            PhysicalCalibrationVerificationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            var bytes = context.Evidence.GetBytes();
            var value = bytes.Sum(item => (int)item);
            return new[] { new CalibrationQualityMetric("physical-fit", value, "unit") };
        }
    }

    private const string Hash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
}

#pragma warning restore CA1416
