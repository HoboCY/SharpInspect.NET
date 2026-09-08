using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

internal static partial class CalibrationGovernanceProjection
{
    /// <summary>Cross-record validation supplements signatures and each record's canonical self-hash.</summary>
    internal static void ValidateRecords(IReadOnlyList<object> records, Func<Guid, CalibrationSessionEvidence?> sessionLoader)
    {
        var prior = new List<object>();
        var operations = new HashSet<Guid>();
        foreach (var record in records)
        {
            var metadata = Metadata(record);
            Require(metadata.Position == prior.Count + 1L && operations.Add(metadata.OperationId), "PositionOrOperationInvalid");
            switch (record)
            {
                case CalibrationAcceptancePolicyRevision revision:
                {
                    var policies = prior.OfType<CalibrationAcceptancePolicyRevision>().ToArray();
                    Require(policies.LastOrDefault(value => value.Policy.Reference.Id == revision.Policy.Reference.Id)?.Policy.Reference ==
                        revision.Previous, "PolicyPreviousMismatch");
                    Require(!policies.Any(value => value.Policy.Reference.Id == revision.Policy.Reference.Id &&
                        value.Policy.Reference.Version == revision.Policy.Reference.Version), "PolicyVersionDuplicate");
                    break;
                }
                case CalibrationPolicyEvaluationRecord evaluation:
                {
                    var policy = Policy(prior, evaluation.Policy);
                    Require(policy is not null, "EvaluationPolicyMissing");
                    var session = sessionLoader(evaluation.Candidate.SessionId);
                    Require(session is not null, "EvaluationSessionMissing");
                    var decision = new CalibrationAcceptancePolicyEvaluator().Evaluate(policy!, session!, evaluation.Candidate);
                    Require(decision.Sections.Select(value => value.ContentHash).SequenceEqual(
                        evaluation.Sections.Select(value => value.ContentHash)), "EvaluationSectionsMismatch");
                    Require(evaluation.BindingFailures.Where(value => value != "CalibrationSourceImagesUnavailable")
                        .OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(decision.BindingFailures
                            .OrderBy(value => value, StringComparer.Ordinal)), "EvaluationBindingMismatch");
                    Require(evaluation.RecordedAtUtc >= session!.Candidate!.ComputedAtUtc, "EvaluationTimeInvalid");
                    break;
                }
                case PublishedCalibrationProfileVersion profile:
                {
                    var evaluation = prior.OfType<CalibrationPolicyEvaluationRecord>().SingleOrDefault(value => value.Reference == profile.Evaluation);
                    Require(evaluation is { Passed: true } && evaluation.Candidate == profile.SourceCandidate &&
                        evaluation.Policy == profile.AcceptancePolicy, "ProfileEvaluationMismatch");
                    var previous = prior.OfType<PublishedCalibrationProfileVersion>()
                        .LastOrDefault(value => value.Reference.ProfileId == profile.Reference.ProfileId);
                    Require(profile.Previous == previous?.Reference &&
                        profile.Reference.Version == (previous?.Reference.Version ?? 0) + 1, "ProfilePreviousMismatch");
                    var session = sessionLoader(profile.SourceCandidate.SessionId);
                    Require(session?.Candidate?.ContentHash == profile.SourceCandidate.CandidateContentHash,
                        "ProfileCandidateMismatch");
                    var content = CreateProfileContent(session!, profile.Actor.PrincipalId, profile.RecordedAtUtc);
                    Require(content.ContentHash == profile.Content.ContentHash && profile.DevelopmentOnly &&
                        !profile.ProductionAuthority && !profile.CanActivate, "ProfileContentMismatch");
                    Require(profile.RecordedAtUtc >= evaluation!.RecordedAtUtc, "ProfileTimeInvalid");
                    break;
                }
                case PhysicalCalibrationVerificationRecord verification:
                {
                    var profile = Profile(prior, verification.Profile);
                    Require(profile is not null && profile.AcceptancePolicy == verification.Policy, "VerificationProfileMismatch");
                    var policy = Policy(prior, verification.Policy);
                    Require(policy?.PhysicalVerification.Applicability == CalibrationPolicyApplicability.Required,
                        "VerificationPolicyMismatch");
                    var requirement = policy!.PhysicalVerification;
                    Require(requirement.Gates.Select(value => value.GateId).OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(verification.GateResults.Select(value => value.GateId).OrderBy(value => value, StringComparer.Ordinal)),
                        "VerificationGatesMismatch");
                    Require(verification.Submission.PerformedAtUtc <= verification.RecordedAtUtc &&
                        verification.RecordedAtUtc >= profile!.RecordedAtUtc, "VerificationTimeInvalid");
                    var previous = prior.OfType<PhysicalCalibrationVerificationRecord>().ToArray();
                    Require(!previous.Any(value => value.Submission.Evidence.ContentHash == verification.Submission.Evidence.ContentHash &&
                        value.Submission.IndependentReference.ContentHash == verification.Submission.IndependentReference.ContentHash), "VerificationEvidenceReplay");
                    Require(previous.LastOrDefault(value => value.Profile == verification.Profile) is not { } latest ||
                        latest.Submission.PerformedAtUtc <= verification.Submission.PerformedAtUtc, "VerificationTimeRegressed");
                    if (verification.Passed)
                    {
                        Require(!PhysicalBindingFailures(requirement, verification.Submission).Any(), "VerificationBindingMismatch");
                        var expected = requirement.Gates.Select(gate => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(
                            gate, verification.Submission.Metrics)).ToArray();
                        Require(expected.All(value => value.Outcome == CalibrationGateOutcome.Passed) &&
                            expected.Select(value => value.ContentHash).SequenceEqual(verification.GateResults.Select(value => value.ContentHash)),
                            "VerificationMetricMismatch");
                        Require(verification.ValidUntilUtc == verification.Submission.PerformedAtUtc.Add(requirement.ValidityInterval!.Value),
                            "VerificationValidityMismatch");
                    }
                    else Require(verification.ValidUntilUtc is null, "FailedVerificationHasValidity");
                    break;
                }
                default: throw new InvalidOperationException("CalibrationGovernanceRecordKindInvalid");
            }
            prior.Add(record);
        }
    }

    internal static (long Position, Guid OperationId, CalibrationGovernanceActor Actor, DateTimeOffset RecordedAtUtc) Metadata(object value) => value switch
    {
        CalibrationAcceptancePolicyRevision record => (record.Position, record.OperationId, record.Actor, record.RecordedAtUtc),
        CalibrationPolicyEvaluationRecord record => (record.Position, record.OperationId, record.Actor, record.RecordedAtUtc),
        PublishedCalibrationProfileVersion record => (record.Position, record.OperationId, record.Actor, record.RecordedAtUtc),
        PhysicalCalibrationVerificationRecord record => (record.Position, record.OperationId, record.Actor, record.RecordedAtUtc),
        _ => throw new InvalidOperationException("CalibrationGovernanceRecordKindInvalid")
    };

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException("CalibrationGovernance" + reason); }
}
