using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Evaluates a retained calibration candidate against one immutable project policy.
/// This class only consumes persisted DTOs; it never decodes images or invokes a
/// calibration procedure.
/// </summary>
internal sealed class CalibrationAcceptancePolicyEvaluator
{
    private const string PolicyReferenceMismatch = "CalibrationPolicyReferenceMismatch";
    private const string PolicyKindMismatch = "CalibrationPolicyKindMismatch";
    private const string PolicyPurposeMismatch = "CalibrationPolicyLogicalPurposeMismatch";
    private const string PolicyProcedureMismatch = "CalibrationPolicyProcedureContractMismatch";
    private const string PolicyInputMismatch = "CalibrationPolicyInputContractMismatch";
    private const string PolicyCoefficientMismatch = "CalibrationPolicyCoefficientContractMismatch";
    private const string PolicyEvidenceMismatch = "CalibrationPolicyEvidenceContractMismatch";
    private const string PolicyEvidenceMissing = "CalibrationPolicyEvidenceMissing";
    private const string PolicyReceiptMismatch = "CalibrationPolicyReceiptContractMismatch";
    private const string PolicyReceiptMissing = "CalibrationPolicyReceiptMissing";
    private const string CandidateMissing = "CalibrationCandidateMissing";
    private const string CandidateReferenceMismatch = "CalibrationCandidateReferenceMismatch";
    private const string CandidateSessionMismatch = "CalibrationCandidateSessionMismatch";
    private const string CandidateHeaderMismatch = "CalibrationCandidateHeaderMismatch";
    private const string CandidateSelectionMismatch = "CalibrationCandidateSelectionMismatch";
    private const string SelectionInvalid = "CalibrationSelectionInvalid";
    private const string SelectionInsufficient = "CalibrationSelectionInsufficient";
    private const string TemporaryConfigurationMissing = "CalibrationTemporaryConfigurationMissing";
    private const string TemporaryConfigurationMismatch = "CalibrationTemporaryConfigurationMismatch";
    private const string FrameIdentityMismatch = "CalibrationFrameIdentityMismatch";
    private const string ObservationFrameMismatch = "CalibrationObservationFrameMismatch";
    private const string ObservationProcedureMismatch = "CalibrationObservationProcedureMismatch";
    private const string ObservationInputMismatch = "CalibrationObservationInputMismatch";
    private const string ExclusionInvalid = "CalibrationExclusionInvalid";
    private const string IncludedObservationInvalid = "CalibrationIncludedObservationInvalid";

    internal CalibrationPolicyEvaluationDecision Evaluate(
        CalibrationAcceptancePolicy policy, CalibrationSessionEvidence session,
        CalibrationCandidateReference candidate)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(candidate);

        var failures = new List<string>();
        var metrics = Array.Empty<CalibrationQualityMetric>();
        var invalidIncludedObservation = ValidateBindings(policy, session, candidate, failures, out metrics);
        var sessionMetrics = SessionMetrics(session, failures);
        var sections = policy.Sections
            .Select(section => EvaluateSection(section, metrics, sessionMetrics,
                invalidIncludedObservation, failures))
            .ToArray();

        return new CalibrationPolicyEvaluationDecision(sections, failures.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Evaluates one exact metric key/unit against one threshold.</summary>
    internal static CalibrationMetricGateResult EvaluateMetric(
        CalibrationMetricGate gate, IEnumerable<CalibrationQualityMetric> metrics)
    {
        // 接受策略按唯一指标键和单位取值；缺失、重复或单位不符都闭合失败，不把近似值当作同一事实。
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(metrics);

        var matches = metrics.Where(metric => string.Equals(metric.Key, gate.Fact.Key,
            StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            return new CalibrationMetricGateResult(gate.GateId, CalibrationGateOutcome.Failed,
                null, null, "CalibrationPolicyMetricMissing");
        if (matches.Length > 1)
            return new CalibrationMetricGateResult(gate.GateId, CalibrationGateOutcome.Failed,
                null, null, "CalibrationPolicyMetricDuplicate");

        var metric = matches[0];
        if (!string.Equals(metric.Unit, gate.Fact.Unit, StringComparison.Ordinal))
            return new CalibrationMetricGateResult(gate.GateId, CalibrationGateOutcome.Failed,
                metric.Value, metric.Unit, "CalibrationPolicyMetricUnitMismatch");

        var passed = gate.Comparison == CalibrationGateComparison.MinimumInclusive
            ? metric.Value >= gate.Threshold
            : metric.Value <= gate.Threshold;
        return new CalibrationMetricGateResult(gate.GateId,
            passed ? CalibrationGateOutcome.Passed : CalibrationGateOutcome.Failed,
            metric.Value, metric.Unit, passed ? "CalibrationPolicyGatePassed" : "CalibrationPolicyGateFailed");
    }

    private static CalibrationGateSectionResult EvaluateSection(
        CalibrationGateSection section, IReadOnlyList<CalibrationQualityMetric> procedureMetrics,
        IReadOnlyList<CalibrationQualityMetric> sessionMetrics, bool invalidIncludedObservation,
        ICollection<string> failures)
    {
        if (section.Applicability == CalibrationPolicyApplicability.NotApplicable)
        {
            return new CalibrationGateSectionResult(section.Category, section.Applicability,
                Array.Empty<CalibrationMetricGateResult>(), section.NotApplicableReason);
        }

        var results = new List<CalibrationMetricGateResult>(section.Gates.Count);
        foreach (var gate in section.Gates)
        {
            var source = IsSessionFact(gate.Fact.Kind) ? sessionMetrics : procedureMetrics;
            results.Add(EvaluateMetric(gate, source));
        }

        if (section.Category == CalibrationAcceptanceGateCategory.InvalidObservation &&
            invalidIncludedObservation)
        {
            failures.Add(IncludedObservationInvalid);
            results = results.Select(result => new CalibrationMetricGateResult(
                result.GateId, CalibrationGateOutcome.Failed, result.ActualValue,
                result.ActualUnit, IncludedObservationInvalid)).ToList();
        }

        return new CalibrationGateSectionResult(section.Category, section.Applicability,
            results, null);
    }

    private static IReadOnlyList<CalibrationQualityMetric> SessionMetrics(
        CalibrationSessionEvidence session, ICollection<string> failures)
    {
        // Selection 会从帧、观察和排除记录重新计算并比对哈希；持久化的 Sufficiency 结果本身不是信任根。
        var selection = session.Selection;
        var coverage = selection.ImageCoverage;
        if (!double.IsFinite(coverage))
        {
            failures.Add(SelectionInvalid);
            coverage = 0d;
        }
        return new[]
        {
            new CalibrationQualityMetric(CalibrationPolicyFactReference.IncludedFrameCount.Key,
                selection.IncludedFrameCount, CalibrationPolicyFactReference.IncludedFrameCount.Unit),
            new CalibrationQualityMetric(CalibrationPolicyFactReference.SufficientFeatureFrameCount.Key,
                selection.SufficientFeatureFrameCount,
                CalibrationPolicyFactReference.SufficientFeatureFrameCount.Unit),
            new CalibrationQualityMetric(CalibrationPolicyFactReference.SelectionImageCoverage.Key,
                coverage, CalibrationPolicyFactReference.SelectionImageCoverage.Unit),
            new CalibrationQualityMetric(CalibrationPolicyFactReference.ExcludedFrameCount.Key,
                session.Exclusions.Count, CalibrationPolicyFactReference.ExcludedFrameCount.Unit)
        };
    }

    private static bool IsSessionFact(CalibrationPolicyFactKind kind) => kind is
        CalibrationPolicyFactKind.IncludedFrameCount or
        CalibrationPolicyFactKind.SufficientFeatureFrameCount or
        CalibrationPolicyFactKind.SelectionImageCoverage or
        CalibrationPolicyFactKind.ExcludedFrameCount;

    private static bool ValidateBindings(CalibrationAcceptancePolicy policy,
        CalibrationSessionEvidence session, CalibrationCandidateReference candidate,
        ICollection<string> failures, out CalibrationQualityMetric[] metrics)
    {
        var invalidIncludedObservation = false;
        var header = session.Header;
        var plan = header.Command.Plan;
        var requirement = plan.Requirement;

        if (!SameContract(requirement.AcceptancePolicy, policy.Reference))
            failures.Add(PolicyReferenceMismatch);
        if (requirement.Kind != policy.Kind)
            failures.Add(PolicyKindMismatch);
        if (!string.Equals(requirement.LogicalPurpose, policy.LogicalPurpose, StringComparison.Ordinal))
            failures.Add(PolicyPurposeMismatch);
        if (!SameContract(plan.Procedure.Procedure, policy.ProcedureContract))
            failures.Add(PolicyProcedureMismatch);
        if (!SameContract(plan.Procedure.InputContract, policy.InputContract) ||
            !SameContract(plan.Input.InputContract, policy.InputContract))
            failures.Add(PolicyInputMismatch);
        if (!SameContract(requirement.CoefficientContract, policy.CoefficientContract))
            failures.Add(PolicyCoefficientMismatch);

        if (session.State.SessionId != header.SessionId)
            failures.Add(CandidateSessionMismatch);
        if (!ValidateTemporaryConfiguration(header, session, failures))
            invalidIncludedObservation = true;
        if (ValidateFramesAndExclusions(header, requirement, session, failures))
            invalidIncludedObservation = true;

        var candidateEvidence = session.Candidate;
        if (candidateEvidence is null)
        {
            failures.Add(CandidateMissing);
            metrics = Array.Empty<CalibrationQualityMetric>();
        }
        else
        {
            metrics = candidateEvidence.Result.QualityMetrics.ToArray();
            if (candidate.SessionId != header.SessionId || candidateEvidence.SessionId != header.SessionId)
                failures.Add(CandidateSessionMismatch);
            if (candidate.CandidateId != candidateEvidence.CandidateId)
                failures.Add(CandidateReferenceMismatch);
            if (session.State.CandidateId != candidateEvidence.CandidateId)
                failures.Add(CandidateReferenceMismatch);
            if (candidateEvidence.SessionHeaderHash != header.ContentHash)
                failures.Add(CandidateHeaderMismatch);
            if (candidateEvidence.SelectionHash != session.Selection.SelectionHash)
                failures.Add(CandidateSelectionMismatch);

            var expectedReference = new CalibrationCandidateReference(candidateEvidence.SessionId,
                candidateEvidence.CandidateId, candidateEvidence.ContentHash);
            if (candidate.ContentHash != expectedReference.ContentHash)
                failures.Add(CandidateReferenceMismatch);

            if (!SameContract(candidateEvidence.Result.Coefficients.Format, policy.CoefficientContract) ||
                !SameContract(candidateEvidence.Result.Coefficients.Format, requirement.CoefficientContract))
                failures.Add(PolicyCoefficientMismatch);

            if (candidateEvidence.Result.Evidence is null)
            {
                failures.Add(PolicyEvidenceMissing);
                invalidIncludedObservation = true;
            }
            else if (!SameContract(candidateEvidence.Result.Evidence.Format,
                policy.ComputationEvidenceContract))
            {
                failures.Add(PolicyEvidenceMismatch);
                invalidIncludedObservation = true;
            }
        }

        if (!ValidateSelection(session, failures))
            invalidIncludedObservation = true;

        var excluded = session.Exclusions.Select(item => item.FrameId).ToHashSet();
        var frameIds = session.Frames.Select(frame => frame.FrameId).ToHashSet();
        if (session.Frames.Any(frame => !excluded.Contains(frame.FrameId) &&
            session.Observations.Count(observation => observation.Frame.FrameId == frame.FrameId) != 1))
        {
            failures.Add(IncludedObservationInvalid);
            invalidIncludedObservation = true;
        }
        foreach (var observation in session.Observations)
        {
            if (excluded.Contains(observation.Frame.FrameId))
                continue;
            if (!frameIds.Contains(observation.Frame.FrameId) ||
                observation.Result.Features.Count < plan.SelectionPolicy.MinimumFeaturesPerFrame)
            {
                failures.Add(IncludedObservationInvalid);
                invalidIncludedObservation = true;
            }

            if (observation.Result.Receipt is null)
            {
                failures.Add(PolicyReceiptMissing);
                invalidIncludedObservation = true;
            }
            else if (!SameContract(observation.Result.Receipt.Format,
                policy.ExtractionReceiptContract))
            {
                failures.Add(PolicyReceiptMismatch);
                invalidIncludedObservation = true;
            }
        }

        return invalidIncludedObservation;
    }

    private static bool ValidateTemporaryConfiguration(CalibrationSessionHeader header,
        CalibrationSessionEvidence session, ICollection<string> failures)
    {
        var temporary = session.TemporaryConfiguration;
        if (temporary is null)
        {
            failures.Add(TemporaryConfigurationMissing);
            return false;
        }

        var planConfigurationHash = CalibrationSessionContractHash.Configuration(
            header.Command.Plan.TemporaryConfiguration);
        var requestedHash = CalibrationSessionContractHash.Configuration(temporary.Requested);
        var effectiveHash = CalibrationSessionContractHash.Configuration(temporary.Effective);
        if (requestedHash != planConfigurationHash || effectiveHash != temporary.EffectiveHash ||
            temporary.RequestedHash != planConfigurationHash)
        {
            failures.Add(TemporaryConfigurationMismatch);
            return false;
        }
        return true;
    }

    private static bool ValidateFramesAndExclusions(CalibrationSessionHeader header,
        CalibrationRequirement requirement, CalibrationSessionEvidence session,
        ICollection<string> failures)
    {
        var invalid = false;
        var frameIds = new HashSet<Guid>();
        var frameById = new Dictionary<Guid, CalibrationFrameEvidence>();
        var expectedEffectiveHash = session.TemporaryConfiguration?.EffectiveHash;
        foreach (var frame in session.Frames)
        {
            if (!frameIds.Add(frame.FrameId) || frame.SessionId != header.SessionId ||
                frame.Metadata.LogicalCameraRole != requirement.LogicalCameraRole ||
                expectedEffectiveHash is not null &&
                CalibrationSessionContractHash.Configuration(frame.Metadata.EffectiveCameraConfiguration) !=
                expectedEffectiveHash ||
                expectedEffectiveHash is not null &&
                (frame.Metadata.Width != frame.Metadata.EffectiveCameraConfiguration.RegionOfInterest.Width ||
                 frame.Metadata.Height != frame.Metadata.EffectiveCameraConfiguration.RegionOfInterest.Height))
            {
                failures.Add(FrameIdentityMismatch);
                invalid = true;
            }
            else
                frameById.Add(frame.FrameId, frame);
        }

        foreach (var observation in session.Observations)
        {
            if (!frameById.TryGetValue(observation.Frame.FrameId, out var frame) ||
                observation.Frame.SourceHash != frame.SourceHash)
            {
                failures.Add(ObservationFrameMismatch);
                invalid = true;
            }
            if (observation.Procedure.ContentHash != header.Command.Plan.Procedure.ContentHash)
            {
                failures.Add(ObservationProcedureMismatch);
                invalid = true;
            }
            if (observation.InputHash != header.Command.Plan.Input.ContentHash)
            {
                failures.Add(ObservationInputMismatch);
                invalid = true;
            }
        }

        var excludedIds = new HashSet<Guid>();
        foreach (var exclusion in session.Exclusions)
        {
            if (!excludedIds.Add(exclusion.FrameId) || !frameById.ContainsKey(exclusion.FrameId) ||
                exclusion.ActorPrincipalId != header.ActorPrincipalId ||
                exclusion.InteractiveSessionId != header.InteractiveSessionId ||
                string.IsNullOrWhiteSpace(exclusion.Reason))
            {
                failures.Add(ExclusionInvalid);
                invalid = true;
            }
        }
        return invalid;
    }

    private static bool ValidateSelection(CalibrationSessionEvidence session,
        ICollection<string> failures)
    {
        CalibrationSelectionEvaluation recalculated;
        try
        {
            recalculated = CalibrationEvidenceSelection.Evaluate(session.Header, session.Frames,
                session.Observations, session.Exclusions);
        }
        catch (ArgumentException)
        {
            failures.Add(SelectionInvalid);
            return false;
        }
        catch (InvalidOperationException)
        {
            failures.Add(SelectionInvalid);
            return false;
        }

        var selected = session.Selection;
        if (selected.Sufficient != recalculated.Sufficient ||
            selected.ReasonCode != recalculated.ReasonCode ||
            selected.IncludedFrameCount != recalculated.IncludedFrameCount ||
            selected.SufficientFeatureFrameCount != recalculated.SufficientFeatureFrameCount ||
            selected.ImageCoverage != recalculated.ImageCoverage ||
            selected.SelectionHash != recalculated.SelectionHash)
            failures.Add(CandidateSelectionMismatch);
        if (!selected.Sufficient)
            failures.Add(SelectionInsufficient);
        return selected.Sufficient;
    }

    private static bool SameContract(RecipeContractReference left, RecipeContractReference right) =>
        left is not null && right is not null &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);
}

internal sealed record CalibrationPolicyEvaluationDecision(
    IReadOnlyList<CalibrationGateSectionResult> Sections,
    IReadOnlyList<string> BindingFailures)
{
    public bool Passed => BindingFailures.Count == 0 && Sections.Count == 6 &&
        Sections.All(section => section.Passed);
}
