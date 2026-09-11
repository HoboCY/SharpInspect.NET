using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Irreversibly closes one exact Draft head while preserving every revision and contribution.</summary>
public sealed record AbandonRecipeDraftCommand : RuntimeCommand
{
    public AbandonRecipeDraftCommand(Guid correlationId, CommandInvocation invocation, Guid draftId,
        long expectedRevision, string expectedRevisionContentHash, string reason) : base(correlationId, invocation)
    {
        RecipeActivationValidation.RequiredGuid(correlationId, nameof(correlationId));
        ArgumentNullException.ThrowIfNull(invocation);
        DraftId = RecipeActivationValidation.RequiredGuid(draftId, nameof(draftId));
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        ExpectedRevision = expectedRevision;
        ExpectedRevisionContentHash = RecipeActivationValidation.Hash(expectedRevisionContentHash,
            nameof(expectedRevisionContentHash));
        Reason = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 256);
        if (string.IsNullOrWhiteSpace(Reason)) throw new ArgumentException("RecipeAbandonmentReasonRequired");
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-abandon-recipe-draft-command-v1", DraftId.ToString("D"),
            ExpectedRevision.ToString(CultureInfo.InvariantCulture), ExpectedRevisionContentHash, Reason
        });
    }

    public Guid DraftId { get; }
    public long ExpectedRevision { get; }
    public string ExpectedRevisionContentHash { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
}

/// <summary>
/// Retires an exact immutable release. ExpectedActive binds the observed effective Active selection,
/// including its absence; a concurrent activation requires a new explicit request.
/// </summary>
public sealed record RetireReleasedRecipeCommand : RuntimeCommand
{
    public RetireReleasedRecipeCommand(Guid correlationId, CommandInvocation invocation, RecipeReference recipe,
        Guid releaseId, string releaseRecordContentHash, RecipeActivationReference? expectedActive,
        string reason) : base(correlationId, invocation)
    {
        RecipeActivationValidation.RequiredGuid(correlationId, nameof(correlationId));
        ArgumentNullException.ThrowIfNull(invocation);
        Recipe = RecipeActivationValidation.Recipe(recipe, nameof(recipe));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ExpectedActive = expectedActive;
        Reason = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 256);
        if (string.IsNullOrWhiteSpace(Reason)) throw new ArgumentException("RecipeRetirementReasonRequired");
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-retire-released-recipe-command-v1", Recipe.Id, Recipe.Version, Recipe.ContentHash,
            ReleaseId.ToString("D"), ReleaseRecordContentHash,
            ExpectedActive?.Position.ToString(CultureInfo.InvariantCulture), ExpectedActive?.ActivationId.ToString("D"),
            ExpectedActive?.ContentHash, Reason
        });
    }

    public RecipeReference Recipe { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public RecipeActivationReference? ExpectedActive { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
}
