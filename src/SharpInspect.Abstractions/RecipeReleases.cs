using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>One persisted release gate, bound to its exact local contract when applicable.</summary>
public sealed class RecipeReleaseValidationCheck
{
    internal RecipeReleaseValidationCheck(string gateId, string subject, bool passed,
        string reasonCode, RecipeContractReference? contract = null)
    {
        GateId = AlgorithmConfigurationValidation.Identifier(gateId, nameof(gateId));
        Subject = AlgorithmContractValidation.BoundedText(subject, nameof(subject), 512);
        Passed = passed;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        Contract = contract;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-release-check-v1", GateId, Subject, Passed ? "1" : "0",
            ReasonCode, Contract?.Id, Contract?.Version, Contract?.ContentHash
        });
    }
    public string GateId { get; }
    public string Subject { get; }
    public bool Passed { get; }
    public string ReasonCode { get; }
    public RecipeContractReference? Contract { get; }
    public string ContentHash { get; }
}

/// <summary>A current contribution, including a retained deletion; attribution comes from the audited draft history.</summary>
public sealed class RecipeReleaseChange
{
    internal RecipeReleaseChange(string path, string? beforeValue, string? afterValue,
        Guid authorPrincipalId, Guid sourceDraftId, long sourceRevision)
    {
        Path = AlgorithmContractValidation.BoundedText(path, nameof(path), 512);
        if (beforeValue is { Length: > 131072 } || afterValue is { Length: > 131072 })
            throw new ArgumentException("RecipeReleaseChangeValueCapacityExceeded");
        if (beforeValue == afterValue) throw new ArgumentException("RecipeReleaseChangeRequired");
        if (authorPrincipalId == Guid.Empty || sourceDraftId == Guid.Empty || sourceRevision < 1)
            throw new ArgumentException("RecipeReleaseChangeAttributionRequired");
        BeforeValue = beforeValue; AfterValue = afterValue; AuthorPrincipalId = authorPrincipalId;
        SourceDraftId = sourceDraftId; SourceRevision = sourceRevision;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-release-change-v1", Path, BeforeValue, AfterValue,
            AuthorPrincipalId.ToString("D"), SourceDraftId.ToString("D"),
            SourceRevision.ToString(CultureInfo.InvariantCulture)
        });
    }
    public string Path { get; }
    public string? BeforeValue { get; }
    public string? AfterValue { get; }
    public Guid AuthorPrincipalId { get; }
    public Guid SourceDraftId { get; }
    public long SourceRevision { get; }
    public string HumanReadableChange => $"{Path}: {BeforeValue ?? "(absent)"} -> {AfterValue ?? "(absent)"}";
    public string ContentHash { get; }
}

/// <summary>Immutable governance evidence, separate from the Recipe's production content hash.</summary>
public sealed class RecipeReleaseRecord
{
    public const string ContributionProjectionVersion = "sharpinspect-recipe-contributions-v1";
    internal RecipeReleaseRecord(long position, Guid releaseId, Guid operationId, long recipeVersion,
        RecipeDraftRevision source, RecipeGovernancePolicy policy,
        IEnumerable<RecipeReleaseValidationCheck> checks, IEnumerable<RecipeReleaseChange> changes,
        Guid approverPrincipalId, Guid approverSessionId, long approverAuthorizationRevision,
        Guid stepUpGrantId, RecipeContractReference authorizationPolicy, string releaseReason,
        string authorizationTarget, DateTimeOffset releasedAtUtc)
    {
        if (position < 1 || recipeVersion < 1 || releaseId == Guid.Empty || operationId == Guid.Empty ||
            approverPrincipalId == Guid.Empty || approverSessionId == Guid.Empty ||
            approverAuthorizationRevision < 0 || stepUpGrantId == Guid.Empty)
            throw new ArgumentException("RecipeReleaseIdentityInvalid");
        Position = position; ReleaseId = releaseId; OperationId = operationId;
        RecipeVersion = recipeVersion;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        GovernancePolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        Checks = AlgorithmContractValidation.Copy(checks, nameof(checks), 256);
        Changes = AlgorithmContractValidation.Copy(changes, nameof(changes), 4096);
        if (Checks.Count == 0 || Checks.Any(value => !value.Passed) || Changes.Count == 0 ||
            Changes.Select(value => value.Path).Distinct(StringComparer.Ordinal).Count() != Changes.Count)
            throw new ArgumentException("RecipeReleaseEvidenceInvalid");
        ContributingAuthors = new ReadOnlyCollection<Guid>(Changes.Select(value => value.AuthorPrincipalId)
            .Distinct().OrderBy(value => value).ToArray());
        if (policy.Mode == RecipeGovernanceMode.MakerCheckerRelease &&
            ContributingAuthors.Contains(approverPrincipalId))
            throw new ArgumentException("RecipeReleaseMakerCheckerConflict");
        ApproverPrincipalId = approverPrincipalId; ApproverSessionId = approverSessionId;
        ApproverAuthorizationRevision = approverAuthorizationRevision; StepUpGrantId = stepUpGrantId;
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        ReleaseReason = AlgorithmContractValidation.BoundedText(releaseReason, nameof(releaseReason), 256);
        if (string.IsNullOrWhiteSpace(ReleaseReason)) throw new ArgumentException("RecipeReleaseReasonRequired");
        AuthorizationTarget = AlgorithmConfigurationValidation.Hash(authorizationTarget,
            nameof(authorizationTarget)).ToUpperInvariant();
        ReleasedAtUtc = releasedAtUtc.ToUniversalTime();
        Recipe = new(Source.Content.RecipeKey, RecipeVersion.ToString(CultureInfo.InvariantCulture),
            Source.Content.ContentHash);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-release-record-v1", ContributionProjectionVersion, Position.ToString(CultureInfo.InvariantCulture),
            ReleaseId.ToString("D"), OperationId.ToString("D"), Recipe.Id, Recipe.Version, Recipe.ContentHash,
            Source.DraftId.ToString("D"), Source.Revision.ToString(CultureInfo.InvariantCulture),
            Source.RevisionContentHash, GovernancePolicy.Id, GovernancePolicy.Version, GovernancePolicy.ContentHash,
            ApproverPrincipalId.ToString("D"), ApproverSessionId.ToString("D"),
            ApproverAuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId.ToString("D"),
            AuthorizationPolicy.Id, AuthorizationPolicy.Version, AuthorizationPolicy.ContentHash,
            ReleaseReason, AuthorizationTarget, ReleasedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Checks.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Checks.Select(value => value.ContentHash))
            .Concat(new[] { Changes.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(Changes.Select(value => value.ContentHash)));
    }

    public long Position { get; }
    public Guid ReleaseId { get; }
    public Guid OperationId { get; }
    public long RecipeVersion { get; }
    public RecipeReference Recipe { get; }
    public RecipeDraftRevision Source { get; }
    public RecipeGovernancePolicy GovernancePolicy { get; }
    public ReadOnlyCollection<RecipeReleaseValidationCheck> Checks { get; }
    public ReadOnlyCollection<RecipeReleaseChange> Changes { get; }
    public ReadOnlyCollection<Guid> ContributingAuthors { get; }
    public Guid ApproverPrincipalId { get; }
    public Guid ApproverSessionId { get; }
    public long ApproverAuthorizationRevision { get; }
    public Guid StepUpGrantId { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public string ReleaseReason { get; }
    public string AuthorizationTarget { get; }
    public DateTimeOffset ReleasedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>A released content version and its immutable approval. Release does not select or arm it.</summary>
public sealed class ReleasedRecipe
{
    internal ReleasedRecipe(RecipeReleaseRecord record) => Record = record ?? throw new ArgumentNullException(nameof(record));
    public RecipeReleaseRecord Record { get; }
    public RecipeReference Reference => Record.Recipe;
    public RecipeDraftContent Content => Record.Source.Content;
    public bool Available => true;
}

public sealed record RecipeReleaseAccess(bool CanRelease, string ReasonCode, RecipeGovernancePolicy? Policy)
{
    public bool RequiresStepUp => true;
}
public sealed record RecipeReleaseResult(RuntimeCommandOutcome Outcome, ReleasedRecipe? Recipe = null);
public sealed record ReleasedRecipeReadResult(bool Available, string ReasonCode, ReleasedRecipe? Recipe = null);
public sealed record ReleasedRecipeFilter(string? RecipeKey = null, long AfterPosition = 0,
    long? ThroughPosition = null, int PageSize = 20);
public sealed record ReleasedRecipePage(bool Available, string ReasonCode, IReadOnlyList<ReleasedRecipe> Recipes,
    long ThroughPosition, long? NextAfterPosition);

/// <summary>Bounded independent history; resolving this capability does not start a writer or an algorithm.</summary>
public interface IReleasedRecipeQuery
{
    ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference, CancellationToken cancellationToken = default);
    ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>Local human approval of one exact saved draft. No activation or production capability.</summary>
public interface IRecipeReleaseService : IReleasedRecipeQuery
{
    ValueTask<RecipeReleaseAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<RecipeReleaseResult> ReleaseAsync(ReleaseRecipeCommand command, CancellationToken cancellationToken = default);
}
