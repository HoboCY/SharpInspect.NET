using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Approves and releases one exact saved draft revision. No reusable approval is issued:
/// changing any draft revision or policy requires a new review and a newly bound Step-Up.
/// </summary>
public sealed record ReleaseRecipeCommand : RuntimeCommand
{
    public ReleaseRecipeCommand(Guid correlationId, CommandInvocation invocation, Guid draftId,
        long expectedRevision, string expectedRevisionContentHash,
        RecipeContractReference governancePolicy, string releaseReason) : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("RecipeReleaseCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        if (draftId == Guid.Empty) throw new ArgumentException("RecipeReleaseDraftIdRequired");
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        DraftId = draftId;
        ExpectedRevision = expectedRevision;
        ExpectedRevisionContentHash = AlgorithmConfigurationValidation.Hash(expectedRevisionContentHash,
            nameof(expectedRevisionContentHash)).ToUpperInvariant();
        GovernancePolicy = governancePolicy ?? throw new ArgumentNullException(nameof(governancePolicy));
        ReleaseReason = AlgorithmContractValidation.BoundedText(releaseReason, nameof(releaseReason), 256);
        if (string.IsNullOrWhiteSpace(ReleaseReason)) throw new ArgumentException("RecipeReleaseReasonRequired");
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-release-recipe-command-v1", DraftId.ToString("D"),
            ExpectedRevision.ToString(CultureInfo.InvariantCulture), ExpectedRevisionContentHash,
            GovernancePolicy.Id, GovernancePolicy.Version, GovernancePolicy.ContentHash, ReleaseReason
        });
    }

    public Guid DraftId { get; }
    public long ExpectedRevision { get; }
    public string ExpectedRevisionContentHash { get; }
    public RecipeContractReference GovernancePolicy { get; }
    public string ReleaseReason { get; }
    public string AuthorizationTarget { get; }
}
