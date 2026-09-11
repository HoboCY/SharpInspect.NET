using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The closed set of irreversible Recipe lifecycle transitions. Both kinds append one
/// immutable fact and neither deletes, reopens or rewrites a Draft revision, Released
/// Recipe, Recipe Release Record, Recipe Selection Map or contribution.
/// </summary>
public enum RecipeLifecycleKind : byte
{
    /// <summary>Closes one exact Draft head; every preserved revision stays readable history.</summary>
    DraftAbandoned = 1,

    /// <summary>Makes one exact Released Recipe permanently ineligible for every future use.</summary>
    ReleasedRetired = 2
}

/// <summary>
/// The lifecycle state of one Recipe Draft identity. Only an Open Draft may be edited,
/// released or migrated; an Abandoned Draft is read-only history and never reopens.
/// </summary>
public enum RecipeDraftLifecycleState : byte
{
    /// <summary>No abandonment transition exists for this Draft identity.</summary>
    Open = 1,

    /// <summary>An abandonment transition exists; the Draft is read-only history.</summary>
    Abandoned = 2
}

/// <summary>
/// The lifecycle state of one exact Released Recipe version. Availability here grants no
/// activation authority: every ordinary activation gate still applies in full.
/// </summary>
public enum ReleasedRecipeLifecycleState : byte
{
    /// <summary>No retirement transition exists for this exact release.</summary>
    Available = 1,

    /// <summary>A retirement transition exists; the release is history, export material or a derive source only.</summary>
    Retired = 2
}

/// <summary>
/// An exact immutable lifecycle transition identity. The content hash belongs to the
/// complete transition record and therefore cannot be reused as a mutable lookup key.
/// </summary>
public sealed record RecipeLifecycleReference
{
    public RecipeLifecycleReference(long position, Guid transitionId, string contentHash)
    {
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
        TransitionId = RecipeActivationValidation.RequiredGuid(transitionId, nameof(transitionId));
        ContentHash = RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
    }

    public long Position { get; }
    public Guid TransitionId { get; }
    public string ContentHash { get; }
}

/// <summary>
/// The exact Recipe Selection Map revision that still names one retired release and the
/// numeric selection codes it maps to that release. The map itself is never rewritten or
/// redirected; this impact makes the now-unavailable paths visible as evidence.
/// </summary>
public sealed class RecipeRetirementMapImpact
{
    public RecipeRetirementMapImpact(RecipeSelectionReference selection, RecipeContractReference map,
        IEnumerable<uint> affectedCodes)
    {
        ArgumentNullException.ThrowIfNull(selection, nameof(selection));
        ArgumentNullException.ThrowIfNull(map, nameof(map));
        ArgumentNullException.ThrowIfNull(affectedCodes, nameof(affectedCodes));
        Selection = new RecipeSelectionReference(selection.Position, selection.RevisionId, selection.ContentHash);
        Map = new RecipeContractReference(map.Id, map.Version, map.ContentHash);
        var copied = AlgorithmContractValidation.Copy(affectedCodes, nameof(affectedCodes),
            RecipeSelectionMap.MaximumEntries);
        if (copied.Any(code => code == 0))
            throw new ArgumentOutOfRangeException(nameof(affectedCodes), "RecipeSelectionZeroCodeReservedForReset");
        var ordered = copied.Distinct().OrderBy(code => code).ToArray();
        if (ordered.Length != copied.Count)
            throw new ArgumentException("RecipeRetirementAffectedCodesDuplicate", nameof(affectedCodes));
        AffectedCodes = Array.AsReadOnly(ordered);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-retirement-map-impact-v1",
            Selection.Position.ToString(CultureInfo.InvariantCulture), Selection.RevisionId.ToString("D"),
            Selection.ContentHash, Map.Id, Map.Version, Map.ContentHash,
            AffectedCodes.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(AffectedCodes.Select(code => code.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>The exact immutable Selection revision that held this map.</summary>
    public RecipeSelectionReference Selection { get; }
    /// <summary>The exact immutable map contract that still names the retired release.</summary>
    public RecipeContractReference Map { get; }
    /// <summary>The copied, ascending, distinct, nonzero codes that resolve to the retired release.</summary>
    public ReadOnlyCollection<uint> AffectedCodes { get; }
    public string ContentHash { get; }
}

/// <summary>
/// One immutable transition of the Recipe lifecycle ledger. The record preserves the
/// exact source Draft revision and its complete content hash, the exact release identity
/// for a retirement, the observed and cleared Active selection, and every affected
/// Selection Map code, and binds the authenticated actor, authorization, Step-Up grant,
/// reason and Audit Time into one canonical hash. Only Runtime may create one; the
/// public contract deliberately exposes no constructor.
/// </summary>
public sealed class RecipeLifecycleRecord
{
    internal RecipeLifecycleRecord(long position, Guid transitionId, Guid operationId, Guid runtimeEpoch,
        RecipeLifecycleKind kind, string? previousHash, RecipeDraftRevisionReference sourceDraft,
        string sourceContentHash, RecipeReference? recipe, Guid? releaseId, string? releaseRecordContentHash,
        RecipeActivationReference? observedActive, RecipeActivationReference? clearedActive,
        IEnumerable<RecipeRetirementMapImpact>? mapImpacts, Guid actorPrincipalId, Guid actorSessionId,
        long actorAuthorizationRevision, Guid stepUpGrantId, RecipeContractReference authorizationPolicy,
        string authorizationTarget, string reason, DateTimeOffset recordedAtUtc, string? contentHash = null)
    {
        if (position < 1 || transitionId == Guid.Empty || operationId == Guid.Empty || runtimeEpoch == Guid.Empty)
            throw new ArgumentException("RecipeLifecycleIdentityInvalid");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), "RecipeLifecycleKindInvalid");
        // The ledger is one append-only chain: only its first transition has no predecessor.
        if ((position == 1) != (previousHash is null))
            throw new ArgumentException("RecipeLifecyclePreviousHashInvalid", nameof(previousHash));
        if (sourceDraft is null) throw new ArgumentNullException(nameof(sourceDraft));
        if (actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty)
            throw new ArgumentException("RecipeLifecycleActorRequired");
        if (actorAuthorizationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(actorAuthorizationRevision));
        if (stepUpGrantId == Guid.Empty)
            throw new ArgumentException("RecipeLifecycleStepUpGrantRequired", nameof(stepUpGrantId));

        Position = position;
        TransitionId = transitionId;
        OperationId = operationId;
        RuntimeEpoch = runtimeEpoch;
        Kind = kind;
        PreviousHash = RecipeActivationValidation.OptionalHash(previousHash, nameof(previousHash));
        SourceDraft = new RecipeDraftRevisionReference(sourceDraft.DraftId, sourceDraft.Revision,
            sourceDraft.RevisionContentHash);
        SourceContentHash = RecipeActivationValidation.Hash(sourceContentHash, nameof(sourceContentHash));
        Recipe = recipe is null ? null : RecipeActivationValidation.Recipe(recipe, nameof(recipe));
        ReleaseId = releaseId is { } identifier
            ? RecipeActivationValidation.RequiredGuid(identifier, nameof(releaseId)) : null;
        ReleaseRecordContentHash = RecipeActivationValidation.OptionalHash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ObservedActive = RecipeActivationValidation.Reference(observedActive);
        ClearedActive = RecipeActivationValidation.Reference(clearedActive);
        MapImpacts = CopyMapImpacts(mapImpacts);
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision;
        StepUpGrantId = stepUpGrantId;
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        var lifecycleReason = AlgorithmContractValidation.BoundedText(reason, nameof(reason), 256);
        if (string.IsNullOrWhiteSpace(lifecycleReason))
            throw new ArgumentException("RecipeLifecycleReasonRequired", nameof(reason));
        Reason = lifecycleReason;
        RecordedAtUtc = RecipeActivationValidation.Utc(recordedAtUtc, nameof(recordedAtUtc));

        // Kind-specific evidence: an abandonment carries no release or map evidence at all,
        // while a retirement must name the exact immutable release it makes unusable.
        if (kind == RecipeLifecycleKind.DraftAbandoned)
        {
            if (Recipe is not null || ReleaseId is not null || ReleaseRecordContentHash is not null ||
                ClearedActive is not null || MapImpacts.Count != 0)
                throw new ArgumentException("RecipeLifecycleAbandonReleaseEvidenceInvalid", nameof(kind));
        }
        else if (Recipe is null || ReleaseId is null || ReleaseRecordContentHash is null)
        {
            throw new ArgumentException("RecipeLifecycleReleaseIdentityRequired", nameof(kind));
        }

        // Clearing Active is exactly the observed selection it clears; a concurrent
        // activation therefore requires a new explicit request instead of silent repair.
        if (ClearedActive is not null && !ClearedActive.Equals(ObservedActive))
            throw new ArgumentException("RecipeLifecycleClearedActiveMismatch", nameof(clearedActive));

        ContentHash = contentHash is null ? ComputeContentHash() :
            RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(), StringComparison.Ordinal))
            throw new ArgumentException("RecipeLifecycleContentHashMismatch", nameof(contentHash));
    }

    public long Position { get; }
    public Guid TransitionId { get; }
    public Guid OperationId { get; }
    public Guid RuntimeEpoch { get; }
    public RecipeLifecycleKind Kind { get; }
    /// <summary>The preceding ledger content hash; null only at Position 1.</summary>
    public string? PreviousHash { get; }
    /// <summary>The exact preserved Draft revision that was abandoned or released.</summary>
    public RecipeDraftRevisionReference SourceDraft { get; }
    /// <summary>The complete content hash of the source Draft snapshot; never a partial projection.</summary>
    public string SourceContentHash { get; }
    /// <summary>The exact Released Recipe identity; required for a retirement and absent for an abandonment.</summary>
    public RecipeReference? Recipe { get; }
    public Guid? ReleaseId { get; }
    /// <summary>The exact Recipe Release Record hash of the retired release.</summary>
    public string? ReleaseRecordContentHash { get; }
    /// <summary>The effective Active selection observed under the command's exact expected state.</summary>
    public RecipeActivationReference? ObservedActive { get; }
    /// <summary>The Active selection this transition cleared; exactly ObservedActive or absent.</summary>
    public RecipeActivationReference? ClearedActive { get; }
    /// <summary>Selection maps that still name the retired release, with the codes made unavailable.</summary>
    public ReadOnlyCollection<RecipeRetirementMapImpact> MapImpacts { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public Guid StepUpGrantId { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public string AuthorizationTarget { get; }
    public string Reason { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
    public RecipeLifecycleReference Reference => new(Position, TransitionId, ContentHash);

    private static ReadOnlyCollection<RecipeRetirementMapImpact> CopyMapImpacts(
        IEnumerable<RecipeRetirementMapImpact>? values)
    {
        var copied = AlgorithmContractValidation.Copy(values, nameof(values), RecipeSelectionMap.MaximumEntries);
        if (copied.Select(impact => impact.Selection.RevisionId).Distinct().Count() != copied.Count)
            throw new ArgumentException("RecipeLifecycleMapImpactDuplicate", nameof(values));
        return Array.AsReadOnly(copied.OrderBy(impact => impact.Selection.Position)
            .ThenBy(impact => impact.Selection.RevisionId).ThenBy(impact => impact.Map.ContentHash,
                StringComparer.Ordinal).ToArray());
    }

    private string ComputeContentHash()
    {
        var fields = new List<string?>(40 + MapImpacts.Count)
        {
            "sharpinspect-recipe-lifecycle-record-v1",
            Position.ToString(CultureInfo.InvariantCulture), TransitionId.ToString("D"),
            OperationId.ToString("D"), RuntimeEpoch.ToString("D"), Kind.ToString(), PreviousHash,
            SourceDraft.DraftId.ToString("D"), SourceDraft.Revision.ToString(CultureInfo.InvariantCulture),
            SourceDraft.RevisionContentHash, SourceContentHash,
            Recipe?.Id, Recipe?.Version, Recipe?.ContentHash, ReleaseId?.ToString("D"),
            ReleaseRecordContentHash,
            ObservedActive?.Position.ToString(CultureInfo.InvariantCulture),
            ObservedActive?.ActivationId.ToString("D"), ObservedActive?.ContentHash,
            ClearedActive?.Position.ToString(CultureInfo.InvariantCulture),
            ClearedActive?.ActivationId.ToString("D"), ClearedActive?.ContentHash,
            ActorPrincipalId.ToString("D"), ActorSessionId.ToString("D"),
            ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId.ToString("D"),
            AuthorizationPolicy.Id, AuthorizationPolicy.Version, AuthorizationPolicy.ContentHash,
            AuthorizationTarget, Reason, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            MapImpacts.Count.ToString(CultureInfo.InvariantCulture)
        };
        fields.AddRange(MapImpacts.Select(impact => impact.ContentHash));
        return AlgorithmContractValidation.HashParts(fields);
    }
}

/// <summary>Applies exactly one irreversible lifecycle transition and reports its evidence.</summary>
public sealed record RecipeLifecycleResult(RuntimeCommandOutcome Outcome, RecipeLifecycleRecord? Record = null,
    bool RuntimeRecoveryRequired = false, string? CleanupReasonCode = null);

/// <summary>Never widens authority: a lifecycle transition always requires fresh Step-Up reauthentication.</summary>
public sealed record RecipeLifecycleAccess(bool CanApply, string ReasonCode, bool RequiresStepUp = true);

/// <summary>The current lifecycle state of one Draft identity with its single abandonment transition.</summary>
public sealed record RecipeDraftLifecycleReadResult(bool Available, string ReasonCode,
    RecipeDraftLifecycleState? State = null, RecipeLifecycleRecord? Transition = null);

/// <summary>The current lifecycle state of one exact Released Recipe version with its single retirement transition.</summary>
public sealed record ReleasedRecipeLifecycleReadResult(bool Available, string ReasonCode,
    ReleasedRecipeLifecycleState? State = null, RecipeLifecycleRecord? Transition = null);

public sealed record RecipeLifecycleFilter(RecipeLifecycleKind? Kind = null, Guid? DraftId = null,
    Guid? ReleaseId = null, long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 50);

public sealed record RecipeLifecyclePage(bool Available, string ReasonCode,
    IReadOnlyList<RecipeLifecycleRecord> Records, long ThroughPosition, long? NextAfterPosition);

/// <summary>Bounded read-only lifecycle history; resolving it starts no writer, device or algorithm.</summary>
public interface IRecipeLifecycleHistoryQuery
{
    ValueTask<RecipeDraftLifecycleReadResult> ReadDraftAsync(Guid draftId,
        CancellationToken cancellationToken = default);
    ValueTask<ReleasedRecipeLifecycleReadResult> ReadReleaseAsync(RecipeReference recipe, Guid releaseId,
        string releaseRecordContentHash, CancellationToken cancellationToken = default);
    ValueTask<RecipeLifecyclePage> QueryAsync(RecipeLifecycleFilter filter,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The lifecycle writer capability. It adds only access, abandonment and retirement;
/// deriving a new Draft from a preserved snapshot is a separate Runtime-owned capability.
/// </summary>
public interface IRecipeLifecycleService : IRecipeLifecycleHistoryQuery
{
    ValueTask<RecipeLifecycleAccess> GetAccessAsync(CommandInvocation invocation, RecipeLifecycleKind kind,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeLifecycleResult> AbandonAsync(AbandonRecipeDraftCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<RecipeLifecycleResult> RetireAsync(RetireReleasedRecipeCommand command,
        CancellationToken cancellationToken = default);
}
