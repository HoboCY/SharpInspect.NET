namespace SharpInspect.Abstractions;

/// <summary>A governed mutation. Its target binds the complete immutable intent to fresh Step-Up.</summary>
public abstract record CalibrationGovernanceCommand : RuntimeCommand
{
    private protected CalibrationGovernanceCommand(Guid correlationId, CommandInvocation invocation)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("CalibrationGovernanceCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
    }
    public abstract string AuthorizationTarget { get; }
    private protected static string Reason(string value) =>
        AlgorithmContractValidation.BoundedText(value, nameof(value), 512);
    private protected static string? ContractHash(RecipeContractReference? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new[] { value.Id, value.Version, value.ContentHash });
    private protected static string? ProfileHash(CalibrationProfileReference? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new[] { value.ProfileId.ToString("D"),
            value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ContentHash });
}

public sealed record PublishCalibrationAcceptancePolicyCommand : CalibrationGovernanceCommand
{
    public PublishCalibrationAcceptancePolicyCommand(Guid correlationId, CommandInvocation invocation,
        CalibrationAcceptancePolicy policy, RecipeContractReference? expectedPrevious, string changeReason)
        : base(correlationId, invocation)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ExpectedPrevious = expectedPrevious; ChangeReason = Reason(changeReason);
        if (expectedPrevious is not null && expectedPrevious.Id != policy.Reference.Id)
            throw new ArgumentException("CalibrationPolicyPreviousIdentityMismatch");
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-publish-policy-command-v1", policy.ContentHash,
            ContractHash(expectedPrevious), ChangeReason
        });
    }
    public CalibrationAcceptancePolicy Policy { get; }
    public RecipeContractReference? ExpectedPrevious { get; }
    public string ChangeReason { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record EvaluateCalibrationCandidateCommand : CalibrationGovernanceCommand
{
    public EvaluateCalibrationCandidateCommand(Guid correlationId, CommandInvocation invocation,
        CalibrationCandidateReference candidate, RecipeContractReference policy)
        : base(correlationId, invocation)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-evaluate-command-v1", candidate.ContentHash, ContractHash(policy)
        });
    }
    public CalibrationCandidateReference Candidate { get; }
    public RecipeContractReference Policy { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record PublishCalibrationProfileCommand : CalibrationGovernanceCommand
{
    public PublishCalibrationProfileCommand(Guid correlationId, CommandInvocation invocation,
        Guid profileId, CalibrationProfileReference? expectedHead, CalibrationCandidateReference candidate,
        CalibrationPolicyEvaluationReference evaluation, string changeReason) : base(correlationId, invocation)
    {
        if (profileId == Guid.Empty) throw new ArgumentException("CalibrationProfileIdRequired");
        if (expectedHead is not null && expectedHead.ProfileId != profileId)
            throw new ArgumentException("CalibrationProfilePreviousIdentityMismatch");
        ProfileId = profileId; ExpectedHead = expectedHead;
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
        ChangeReason = Reason(changeReason);
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-publish-profile-command-v1", profileId.ToString("D"),
            ProfileHash(expectedHead), candidate.ContentHash, evaluation.EvaluationId.ToString("D"),
            evaluation.ContentHash, ChangeReason
        });
    }
    public Guid ProfileId { get; }
    public CalibrationProfileReference? ExpectedHead { get; }
    public CalibrationCandidateReference Candidate { get; }
    public CalibrationPolicyEvaluationReference Evaluation { get; }
    public string ChangeReason { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record RecordPhysicalCalibrationVerificationCommand : CalibrationGovernanceCommand
{
    public RecordPhysicalCalibrationVerificationCommand(Guid correlationId, CommandInvocation invocation,
        CalibrationProfileReference profile, PhysicalCalibrationVerificationSubmission submission, string reason)
        : base(correlationId, invocation)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        VerificationReason = Reason(reason);
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-physical-verification-command-v1", ProfileHash(profile),
            submission.ContentHash, VerificationReason
        });
    }
    public CalibrationProfileReference Profile { get; }
    public PhysicalCalibrationVerificationSubmission Submission { get; }
    public string VerificationReason { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record CalibrationPolicyPublishResult(RuntimeCommandOutcome Outcome,
    CalibrationAcceptancePolicyRevision? Revision = null);
public sealed record CalibrationCandidateEvaluationResult(RuntimeCommandOutcome Outcome,
    CalibrationPolicyEvaluationRecord? Evaluation = null);
public sealed record CalibrationProfilePublishResult(RuntimeCommandOutcome Outcome,
    PublishedCalibrationProfileVersion? Profile = null);
public sealed record PhysicalCalibrationVerificationResult(RuntimeCommandOutcome Outcome,
    PhysicalCalibrationVerificationRecord? Verification = null);
public sealed record CalibrationGovernanceQueryResult<T>(bool Available, string ReasonCode, T? Value = null)
    where T : class;

/// <summary>Policy, assessment and development Profile governance. None of these operations activates a Recipe.</summary>
public interface ICalibrationGovernanceRuntime
{
    ValueTask<CalibrationPolicyPublishResult> PublishPolicyAsync(PublishCalibrationAcceptancePolicyCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<CalibrationCandidateEvaluationResult> EvaluateCandidateAsync(EvaluateCalibrationCandidateCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<CalibrationProfilePublishResult> PublishProfileAsync(PublishCalibrationProfileCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<PhysicalCalibrationVerificationResult> RecordPhysicalVerificationAsync(
        RecordPhysicalCalibrationVerificationCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Exact immutable reads. No lookup silently substitutes the latest policy or Profile version.</summary>
public interface ICalibrationGovernanceQuery
{
    ValueTask<CalibrationGovernanceQueryResult<CalibrationAcceptancePolicyRevision>> ReadPolicyAsync(
        RecipeContractReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<CalibrationGovernanceQueryResult<CalibrationPolicyEvaluationRecord>> ReadEvaluationAsync(
        CalibrationPolicyEvaluationReference reference, CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<CalibrationGovernanceQueryResult<PublishedCalibrationProfileVersion>> ReadProfileAsync(
        CalibrationProfileReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<CalibrationGovernanceQueryResult<PhysicalCalibrationVerificationRecord>> ReadVerificationAsync(
        PhysicalCalibrationVerificationReference reference, CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<CalibrationGovernanceQueryResult<CalibrationProfileValiditySnapshot>> GetValidityAsync(
        CalibrationProfileReference reference, CommandInvocation invocation, CancellationToken cancellationToken = default);
}
