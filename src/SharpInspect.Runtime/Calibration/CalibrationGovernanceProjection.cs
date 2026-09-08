using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>Reconstructs governed decisions from exact persisted inputs. No mutable current-profile or pass flag exists.</summary>
internal static partial class CalibrationGovernanceProjection
{
    internal static CalibrationGovernanceDecision Decide(CalibrationGovernanceCommand command,
        IReadOnlyList<object> records, CalibrationSessionEvidence? session, CalibrationGovernanceActor actor,
        DateTimeOffset nowUtc, string? sourceImagesFailure, PhysicalVerificationComputation? computation)
    {
        var position = checked(records.Count + 1L);
        switch (command)
        {
            case PublishCalibrationAcceptancePolicyCommand publish:
            {
                var policies = records.OfType<CalibrationAcceptancePolicyRevision>().ToArray();
                var head = policies.LastOrDefault(value => value.Policy.Reference.Id == publish.Policy.Reference.Id);
                if (head?.Policy.Reference != publish.ExpectedPrevious) return Reject("CalibrationPolicyHeadChanged");
                if (policies.Any(value => value.Policy.Reference.Id == publish.Policy.Reference.Id &&
                    value.Policy.Reference.Version == publish.Policy.Reference.Version))
                    return Reject("CalibrationPolicyVersionAlreadyExists");
                return new(new CalibrationAcceptancePolicyRevision(position, command.CorrelationId, publish.Policy,
                    publish.ExpectedPrevious, actor, nowUtc), "CalibrationAcceptancePolicyPublished");
            }
            case EvaluateCalibrationCandidateCommand evaluate:
            {
                var policy = Policy(records, evaluate.Policy);
                if (policy is null) return Reject("CalibrationAcceptancePolicyUnavailable");
                if (session?.Candidate is null) return Reject("CalibrationCandidateUnavailable");
                if (nowUtc < session.Candidate.ComputedAtUtc) return Reject("CalibrationEvaluationTimeInvalid");
                var decision = new CalibrationAcceptancePolicyEvaluator().Evaluate(policy, session, evaluate.Candidate);
                var failures = decision.BindingFailures.Concat(sourceImagesFailure is null ? Array.Empty<string>() :
                    new[] { sourceImagesFailure }).Distinct(StringComparer.Ordinal).ToArray();
                var record = new CalibrationPolicyEvaluationRecord(position, command.CorrelationId, command.CorrelationId,
                    evaluate.Candidate, evaluate.Policy, decision.Sections, failures, actor, nowUtc);
                return new(record, record.Passed ? "CalibrationDevelopmentPolicyPassed" : "CalibrationPolicyEvaluationFailed");
            }
            case PublishCalibrationProfileCommand publish:
            {
                var evaluation = records.OfType<CalibrationPolicyEvaluationRecord>()
                    .SingleOrDefault(value => value.Reference == publish.Evaluation);
                if (evaluation is null) return Reject("CalibrationEvaluationUnavailable");
                if (!evaluation.Passed) return Reject("CalibrationFailedEvaluationCannotPublish");
                if (evaluation.Candidate != publish.Candidate) return Reject("CalibrationEvaluationCandidateMismatch");
                if (nowUtc < evaluation.RecordedAtUtc) return Reject("CalibrationPublicationTimeInvalid");
                var policy = Policy(records, evaluation.Policy);
                if (policy is null) return Reject("CalibrationAcceptancePolicyUnavailable");
                if (session is null) return Reject("CalibrationCandidateUnavailable");
                if (sourceImagesFailure is not null) return Reject(sourceImagesFailure);
                var repeated = new CalibrationAcceptancePolicyEvaluator().Evaluate(policy, session, publish.Candidate);
                if (!repeated.Passed || !repeated.Sections.Select(value => value.ContentHash)
                    .SequenceEqual(evaluation.Sections.Select(value => value.ContentHash)))
                    return Reject("CalibrationEvaluationEvidenceChanged");
                var head = records.OfType<PublishedCalibrationProfileVersion>()
                    .LastOrDefault(value => value.Reference.ProfileId == publish.ProfileId);
                if (head?.Reference != publish.ExpectedHead) return Reject("CalibrationProfileHeadChanged");
                var content = CreateProfileContent(session, actor.PrincipalId, nowUtc);
                return new(new PublishedCalibrationProfileVersion(position, command.CorrelationId, publish.ProfileId,
                    checked((head?.Reference.Version ?? 0) + 1), publish.ExpectedHead, content, publish.Candidate,
                    evaluation.Reference, evaluation.Policy, actor, nowUtc), "CalibrationDevelopmentProfilePublished");
            }
            case RecordPhysicalCalibrationVerificationCommand verify:
            {
                var profile = Profile(records, verify.Profile);
                if (profile is null) return Reject("CalibrationProfileUnavailable");
                if (nowUtc < profile.RecordedAtUtc) return Reject("PhysicalVerificationRecordingTimeInvalid");
                var policy = Policy(records, profile.AcceptancePolicy);
                if (policy is null) return Reject("CalibrationAcceptancePolicyUnavailable");
                var requirement = policy.PhysicalVerification;
                if (requirement.Applicability != CalibrationPolicyApplicability.Required)
                    return Reject("PhysicalVerificationNotApplicable");
                if (verify.Submission.PerformedAtUtc > nowUtc)
                    return Reject("PhysicalVerificationFutureTimeRejected");
                var previous = records.OfType<PhysicalCalibrationVerificationRecord>().ToArray();
                if (previous.Any(value => value.Submission.Evidence.ContentHash == verify.Submission.Evidence.ContentHash &&
                    value.Submission.IndependentReference.ContentHash == verify.Submission.IndependentReference.ContentHash))
                    return Reject("PhysicalVerificationEvidenceAlreadyRecorded");
                if (previous.LastOrDefault(value => value.Profile == verify.Profile) is { } latest &&
                    verify.Submission.PerformedAtUtc < latest.Submission.PerformedAtUtc)
                    return Reject("PhysicalVerificationTimePrecedesLatest");
                DateTimeOffset due;
                try { due = verify.Submission.PerformedAtUtc.Add(requirement.ValidityInterval!.Value); }
                catch (ArgumentOutOfRangeException) { return Reject("PhysicalVerificationValidityOutOfRange"); }
                var failures = PhysicalBindingFailures(requirement, verify.Submission).ToList();
                if (computation is null || computation.ProfileContentHash != profile.ContentHash ||
                    computation.SubmissionContentHash != verify.Submission.ContentHash)
                    failures.Add("PhysicalVerificationComputationUnavailable");
                else if (computation.Failure is not null) failures.Add(computation.Failure);
                var actual = computation?.Metrics ?? Array.Empty<CalibrationQualityMetric>();
                if (!SameMetrics(actual, verify.Submission.Metrics)) failures.Add("PhysicalVerificationSubmittedMetricsMismatch");
                var gates = requirement.Gates.Select(gate => CalibrationAcceptancePolicyEvaluator.EvaluateMetric(gate, actual)).ToArray();
                var passed = failures.Count == 0 && gates.Length != 0 && gates.All(value => value.Outcome == CalibrationGateOutcome.Passed);
                var record = new PhysicalCalibrationVerificationRecord(position, command.CorrelationId, command.CorrelationId,
                    verify.Profile, profile.AcceptancePolicy, verify.Submission, gates, failures, actor, nowUtc, passed ? due : null);
                return new(record, record.Passed ? "PhysicalCalibrationVerificationPassed" : "PhysicalCalibrationVerificationFailed");
            }
            default: return Reject("CalibrationGovernanceCommandUnsupported");
        }
    }

    internal static CalibrationAcceptancePolicy? Policy(IEnumerable<object> records, RecipeContractReference reference) =>
        records.OfType<CalibrationAcceptancePolicyRevision>().SingleOrDefault(value => value.Policy.Reference == reference)?.Policy;
    internal static PublishedCalibrationProfileVersion? Profile(IEnumerable<object> records, CalibrationProfileReference reference) =>
        records.OfType<PublishedCalibrationProfileVersion>().SingleOrDefault(value => value.Reference == reference);

    internal static CalibrationProfileContent CreateProfileContent(CalibrationSessionEvidence session,
        Guid actor, DateTimeOffset nowUtc)
    {
        var temporary = session.TemporaryConfiguration ?? throw new InvalidOperationException("CalibrationTemporaryConfigurationMissing");
        return new CalibrationProfileContent(session.Header.Command.Plan.Requirement, session.Header.Binding.Target,
            session.Header.Command.ExpectedImagingSetup, CalibrationFrameGeometry.FromRequested(temporary.Requested),
            CalibrationFrameGeometry.FromEffective(temporary.Effective), session.Candidate!.Result.Coefficients,
            session.Header.Command.Plan.Procedure.Procedure, session.Candidate.ContentHash, actor, nowUtc);
    }

    internal static IEnumerable<string> PhysicalBindingFailures(PhysicalCalibrationVerificationRequirement requirement,
        PhysicalCalibrationVerificationSubmission submission)
    {
        if (requirement.ProcedureContract != submission.ProcedureContract) yield return "PhysicalVerificationProcedureMismatch";
        if (requirement.EvidenceContract != submission.Evidence.Format) yield return "PhysicalVerificationEvidenceContractMismatch";
        if (requirement.IndependentReference != submission.IndependentReference) yield return "PhysicalVerificationIndependentReferenceMismatch";
    }

    private static bool SameMetrics(IEnumerable<CalibrationQualityMetric> first, IEnumerable<CalibrationQualityMetric> second) =>
        first.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => (value.Key, value.Unit, value.Value))
            .SequenceEqual(second.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => (value.Key, value.Unit, value.Value)));

    private static CalibrationGovernanceDecision Reject(string reason) => new(null, reason);
}

internal sealed record CalibrationGovernanceDecision(object? Record, string ReasonCode);
