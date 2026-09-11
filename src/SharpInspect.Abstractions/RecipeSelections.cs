using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

public enum RecipeSelectionMode { LocalOperatorOnly = 1, PlcRequestedActivation = 2 }

/// <summary>An immutable deployment choice. Missing configuration never enables PLC activation.</summary>
public sealed class RecipeSelectionPolicy
{
    public static RecipeSelectionPolicy Default { get; } = new("SharpInspect.DefaultRecipeSelection", "1",
        RecipeSelectionMode.LocalOperatorOnly);
    public RecipeSelectionPolicy(string id, string version, RecipeSelectionMode mode)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        Mode = AlgorithmConfigurationValidation.Enum(mode, nameof(mode));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
            { "sharpinspect-recipe-selection-policy-v1", Id, Version, Mode.ToString() });
    }
    public string Id { get; }
    public string Version { get; }
    public RecipeSelectionMode Mode { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference => new(Id, Version, ContentHash);
}

/// <summary>A numeric code resolves one exact immutable local release, with no name or fallback resolution.</summary>
public sealed class RecipeSelectionMapEntry
{
    public RecipeSelectionMapEntry(uint selectionCode, RecipeReference recipe, Guid releaseId,
        string releaseRecordContentHash)
    {
        if (selectionCode == 0) throw new ArgumentOutOfRangeException(nameof(selectionCode),
            "RecipeSelectionZeroCodeReservedForReset");
        SelectionCode = selectionCode;
        Recipe = RecipeActivationValidation.Recipe(recipe, nameof(recipe));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-selection-map-entry-v1", SelectionCode.ToString(CultureInfo.InvariantCulture),
            Recipe.Id, Recipe.Version, Recipe.ContentHash, ReleaseId.ToString("D"), ReleaseRecordContentHash
        });
    }
    public uint SelectionCode { get; }
    public RecipeReference Recipe { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public string ContentHash { get; }
}

public sealed class RecipeSelectionMap
{
    public const int MaximumEntries = 4096;

    public RecipeSelectionMap(string id, string version, IEnumerable<RecipeSelectionMapEntry> entries)
    {
        Id = AlgorithmConfigurationValidation.Identifier(id, nameof(id));
        Version = AlgorithmConfigurationValidation.Identifier(version, nameof(version));
        var copy = AlgorithmContractValidation.Copy(entries, nameof(entries), MaximumEntries);
        if (copy.Count == 0 || copy.Select(value => value.SelectionCode).Distinct().Count() != copy.Count)
            throw new ArgumentException("RecipeSelectionMapCodesInvalid", nameof(entries));
        Entries = Array.AsReadOnly(copy.OrderBy(value => value.SelectionCode).ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-selection-map-v1", Id, Version,
            Entries.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Entries.Select(value => value.ContentHash)));
    }
    public string Id { get; }
    public string Version { get; }
    public ReadOnlyCollection<RecipeSelectionMapEntry> Entries { get; }
    public string ContentHash { get; }
    public RecipeContractReference Reference => new(Id, Version, ContentHash);
}

public sealed record RecipeSelectionReference
{
    public RecipeSelectionReference(long position, Guid revisionId, string contentHash)
    {
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
        RevisionId = RecipeActivationValidation.RequiredGuid(revisionId, nameof(revisionId));
        ContentHash = RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
    }
    public long Position { get; }
    public Guid RevisionId { get; }
    public string ContentHash { get; }
}

/// <summary>Changes the selection policy/map together under a fresh, exactly bound human Step-Up.</summary>
public sealed record ChangeRecipeSelectionCommand : RuntimeCommand
{
    public ChangeRecipeSelectionCommand(Guid correlationId, CommandInvocation invocation,
        RecipeSelectionPolicy policy, RecipeSelectionMap? map, RecipeSelectionReference? expectedCurrent,
        string changeReason) : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("RecipeSelectionCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (policy.Mode == RecipeSelectionMode.PlcRequestedActivation && map is null)
            throw new ArgumentException("RecipeSelectionMapRequired", nameof(map));
        Map = map;
        ExpectedCurrent = expectedCurrent;
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        AuthorizationTarget = ComputeAuthorizationTarget(Policy, Map, ExpectedCurrent, ChangeReason);
    }
    internal static string ComputeAuthorizationTarget(RecipeSelectionPolicy policy, RecipeSelectionMap? map,
        RecipeSelectionReference? expectedCurrent, string changeReason) => AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-change-recipe-selection-v1", policy.Id, policy.Version, policy.ContentHash,
        map?.Id, map?.Version, map?.ContentHash, expectedCurrent?.Position.ToString(CultureInfo.InvariantCulture),
        expectedCurrent?.RevisionId.ToString("D"), expectedCurrent?.ContentHash, changeReason
    });
    public RecipeSelectionPolicy Policy { get; }
    public RecipeSelectionMap? Map { get; }
    public RecipeSelectionReference? ExpectedCurrent { get; }
    public string ChangeReason { get; }
    public string AuthorizationTarget { get; }
}

/// <summary>Immutable evidence of a complete portable validation of one affected local release.</summary>
public sealed class RecipeSelectionValidatedRelease
{
    internal RecipeSelectionValidatedRelease(RecipeReference recipe, Guid releaseId,
        string releaseRecordContentHash, string validationContentHash)
    {
        Recipe = RecipeActivationValidation.Recipe(recipe, nameof(recipe));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ValidationContentHash = RecipeActivationValidation.Hash(validationContentHash, nameof(validationContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-selection-validated-release-v1", Recipe.Id, Recipe.Version, Recipe.ContentHash,
            ReleaseId.ToString("D"), ReleaseRecordContentHash, ValidationContentHash
        });
    }
    public RecipeReference Recipe { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public string ValidationContentHash { get; }
    public string ContentHash { get; }
}

public sealed class RecipeSelectionRevision
{
    internal RecipeSelectionRevision(long position, Guid revisionId, Guid operationId,
        RecipeSelectionPolicy policy, RecipeSelectionMap? map, RecipeSelectionReference? previous,
        long releaseHighWatermark, IEnumerable<RecipeSelectionValidatedRelease> validations,
        Guid actorPrincipalId, Guid actorSessionId, long actorAuthorizationRevision, Guid stepUpGrantId,
        RecipeContractReference authorizationPolicy, string changeReason, string authorizationTarget,
        DateTimeOffset recordedAtUtc)
    {
        if (position < 1 || releaseHighWatermark < 0 || actorAuthorizationRevision < 0)
            throw new ArgumentException("RecipeSelectionRevisionIdentityInvalid");
        Position = position;
        RevisionId = RecipeActivationValidation.RequiredGuid(revisionId, nameof(revisionId));
        OperationId = RecipeActivationValidation.RequiredGuid(operationId, nameof(operationId));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        if (policy.Mode == RecipeSelectionMode.PlcRequestedActivation && map is null)
            throw new ArgumentException("RecipeSelectionMapRequired", nameof(map));
        Map = map;
        Previous = previous;
        if ((position == 1) != (previous is null) || previous is not null && previous.Position != position - 1)
            throw new ArgumentException("RecipeSelectionPreviousRevisionInvalid", nameof(previous));
        ReleaseHighWatermark = releaseHighWatermark;
        var proof = AlgorithmContractValidation.Copy(validations, nameof(validations), RecipeSelectionMap.MaximumEntries * 2);
        Validations = Array.AsReadOnly(proof.OrderBy(value => value.ReleaseId).ToArray());
        if (Validations.Select(value => value.ReleaseId).Distinct().Count() != Validations.Count ||
            Validations.Count > releaseHighWatermark ||
            map is not null && map.Entries.Any(entry => !Validations.Any(value =>
                value.Recipe == entry.Recipe && value.ReleaseId == entry.ReleaseId &&
                value.ReleaseRecordContentHash == entry.ReleaseRecordContentHash)))
            throw new ArgumentException("RecipeSelectionValidationCoverageInvalid", nameof(validations));
        ActorPrincipalId = RecipeActivationValidation.RequiredGuid(actorPrincipalId, nameof(actorPrincipalId));
        ActorSessionId = RecipeActivationValidation.RequiredGuid(actorSessionId, nameof(actorSessionId));
        ActorAuthorizationRevision = actorAuthorizationRevision;
        StepUpGrantId = RecipeActivationValidation.RequiredGuid(stepUpGrantId, nameof(stepUpGrantId));
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        if (AuthorizationTarget != ChangeRecipeSelectionCommand.ComputeAuthorizationTarget(policy, map, previous, ChangeReason))
            throw new ArgumentException("RecipeSelectionAuthorizationTargetMismatch", nameof(authorizationTarget));
        RecordedAtUtc = RecipeActivationValidation.Utc(recordedAtUtc, nameof(recordedAtUtc));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-selection-revision-v1", Position.ToString(CultureInfo.InvariantCulture),
            RevisionId.ToString("D"), OperationId.ToString("D"), Policy.ContentHash, Map?.ContentHash,
            Previous?.Position.ToString(CultureInfo.InvariantCulture), Previous?.RevisionId.ToString("D"),
            Previous?.ContentHash, ReleaseHighWatermark.ToString(CultureInfo.InvariantCulture),
            ActorPrincipalId.ToString("D"), ActorSessionId.ToString("D"),
            ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId.ToString("D"),
            AuthorizationPolicy.Id, AuthorizationPolicy.Version, AuthorizationPolicy.ContentHash,
            ChangeReason, AuthorizationTarget, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Validations.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(Validations.Select(value => value.ContentHash)));
    }
    public long Position { get; }
    public Guid RevisionId { get; }
    public Guid OperationId { get; }
    public RecipeSelectionPolicy Policy { get; }
    public RecipeSelectionMap? Map { get; }
    public RecipeSelectionReference? Previous { get; }
    public long ReleaseHighWatermark { get; }
    public ReadOnlyCollection<RecipeSelectionValidatedRelease> Validations { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public Guid StepUpGrantId { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public string ChangeReason { get; }
    public string AuthorizationTarget { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
    public RecipeSelectionReference Reference => new(Position, RevisionId, ContentHash);
}

public sealed record RecipeSelectionAccess(bool CanChange, string ReasonCode)
{
    public bool RequiresStepUp => true;
}
public sealed record RecipeSelectionChangeResult(RuntimeCommandOutcome Outcome, RecipeSelectionRevision? Revision = null);
public sealed record RecipeSelectionReadResult(bool Available, string ReasonCode, RecipeSelectionRevision? Revision = null)
{
    public RecipeSelectionMode Mode => Revision?.Policy.Mode ?? RecipeSelectionMode.LocalOperatorOnly;
}
public sealed record RecipeSelectionFilter(long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);
public sealed record RecipeSelectionPage(bool Available, string ReasonCode, IReadOnlyList<RecipeSelectionRevision> Revisions,
    long ThroughPosition, long? NextAfterPosition);

public interface IRecipeSelectionQuery
{
    ValueTask<RecipeSelectionReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
    ValueTask<RecipeSelectionReadResult> ReadAsync(RecipeSelectionReference reference, CancellationToken cancellationToken = default);
    ValueTask<RecipeSelectionPage> QueryAsync(RecipeSelectionFilter filter, CancellationToken cancellationToken = default);
}
public interface IRecipeSelectionService : IRecipeSelectionQuery
{
    ValueTask<RecipeSelectionAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<RecipeSelectionChangeResult> ChangeAsync(ChangeRecipeSelectionCommand command, CancellationToken cancellationToken = default);
}
