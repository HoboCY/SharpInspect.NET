namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable provenance for a newly identified Draft derived from abandoned or retired
/// content. It records the source transition; it cannot reopen or unretire that source.
/// </summary>
public sealed class RecipeDraftLifecycleLineage
{
    internal RecipeDraftLifecycleLineage(RecipeLifecycleReference transition, RecipeLifecycleKind kind,
        RecipeDraftRevisionReference sourceDraft, string sourceContentHash, RecipeReference? sourceRecipe,
        Guid? sourceReleaseId, string? sourceReleaseRecordContentHash)
    {
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        SourceDraft = sourceDraft ?? throw new ArgumentNullException(nameof(sourceDraft));
        SourceContentHash = RecipeActivationValidation.Hash(sourceContentHash, nameof(sourceContentHash));
        SourceRecipe = sourceRecipe is null ? null : RecipeActivationValidation.Recipe(sourceRecipe, nameof(sourceRecipe));
        SourceReleaseId = sourceReleaseId is { } release
            ? RecipeActivationValidation.RequiredGuid(release, nameof(sourceReleaseId)) : null;
        SourceReleaseRecordContentHash = RecipeActivationValidation.OptionalHash(sourceReleaseRecordContentHash,
            nameof(sourceReleaseRecordContentHash));
        if (kind == RecipeLifecycleKind.DraftAbandoned &&
            (SourceRecipe is not null || SourceReleaseId is not null || SourceReleaseRecordContentHash is not null) ||
            kind == RecipeLifecycleKind.ReleasedRetired &&
            (SourceRecipe is null || SourceReleaseId is null || SourceReleaseRecordContentHash is null))
            throw new ArgumentException("RecipeDraftLifecycleSourceInvalid");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-draft-lifecycle-lineage-v1", Transition.Position.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Transition.TransitionId.ToString("D"), Transition.ContentHash, Kind.ToString(), SourceDraft.DraftId.ToString("D"),
            SourceDraft.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), SourceDraft.RevisionContentHash,
            SourceContentHash, SourceRecipe?.Id, SourceRecipe?.Version, SourceRecipe?.ContentHash,
            SourceReleaseId?.ToString("D"), SourceReleaseRecordContentHash
        });
    }

    internal RecipeDraftLifecycleLineage(RecipeLifecycleRecord record) : this(record.Reference, record.Kind,
        record.SourceDraft, record.SourceContentHash, record.Recipe, record.ReleaseId, record.ReleaseRecordContentHash) { }

    public RecipeLifecycleReference Transition { get; }
    public RecipeLifecycleKind Kind { get; }
    public RecipeDraftRevisionReference SourceDraft { get; }
    public string SourceContentHash { get; }
    public RecipeReference? SourceRecipe { get; }
    public Guid? SourceReleaseId { get; }
    public string? SourceReleaseRecordContentHash { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Creates a new local Draft through ordinary authenticated Draft Save. Step-Up uses
/// EditRecipeDraft / SaveRecipeDraft, this operation id, and the new Draft id as target.
/// </summary>
public sealed record RecipeDraftDerivationRequest
{
    public RecipeDraftDerivationRequest(Guid operationId, Guid newDraftId, RecipeLifecycleReference source,
        string reason, CommandInvocation invocation)
    {
        OperationId = RecipeActivationValidation.RequiredGuid(operationId, nameof(operationId));
        NewDraftId = RecipeActivationValidation.RequiredGuid(newDraftId, nameof(newDraftId));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Reason = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 256);
        if (string.IsNullOrWhiteSpace(Reason)) throw new ArgumentException("RecipeDraftDerivationReasonRequired");
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
    }

    public Guid OperationId { get; }
    public Guid NewDraftId { get; }
    public RecipeLifecycleReference Source { get; }
    public string Reason { get; }
    public CommandInvocation Invocation { get; }
    public string AuthorizationTarget => NewDraftId.ToString("D");
}

public interface IRecipeDraftDerivationService
{
    ValueTask<RecipeDraftSaveResult> DeriveAsync(RecipeDraftDerivationRequest request,
        CancellationToken cancellationToken = default);
}
