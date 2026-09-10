using System.Globalization;

namespace SharpInspect.Abstractions;

public enum ManualRecipeSourceKind : byte { Draft = 1, Released = 2 }

/// <summary>An exact authoring or released identity; selecting it grants no production authority.</summary>
public sealed record ManualRecipeSelection
{
    public ManualRecipeSelection(Guid draftId, long draftRevision, string draftRevisionContentHash)
    {
        if (draftId == Guid.Empty || draftRevision < 1) throw new ArgumentException("ManualDraftReferenceInvalid");
        Kind = ManualRecipeSourceKind.Draft;
        DraftId = draftId;
        DraftRevision = draftRevision;
        DraftRevisionContentHash = RecipeActivationValidation.Hash(draftRevisionContentHash, nameof(draftRevisionContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-manual-selection-v1", Kind.ToString(),
            draftId.ToString("D"), draftRevision.ToString(CultureInfo.InvariantCulture), DraftRevisionContentHash });
    }

    public ManualRecipeSelection(RecipeReference recipe, Guid releaseId, string releaseRecordContentHash)
    {
        Kind = ManualRecipeSourceKind.Released;
        Recipe = RecipeActivationValidation.Recipe(recipe, nameof(recipe));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash, nameof(releaseRecordContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-manual-selection-v1", Kind.ToString(),
            Recipe.Id, Recipe.Version, Recipe.ContentHash, releaseId.ToString("D"), ReleaseRecordContentHash });
    }

    public ManualRecipeSourceKind Kind { get; }
    public Guid? DraftId { get; }
    public long? DraftRevision { get; }
    public string? DraftRevisionContentHash { get; }
    public RecipeReference? Recipe { get; }
    public Guid? ReleaseId { get; }
    public string? ReleaseRecordContentHash { get; }
    public string ContentHash { get; }
    public static ManualRecipeSelection FromDraft(RecipeDraftRevision draft) =>
        new(draft.DraftId, draft.Revision, draft.RevisionContentHash);
    public static ManualRecipeSelection FromReleased(ReleasedRecipe recipe) =>
        new(recipe.Reference, recipe.Record.ReleaseId, recipe.Record.ContentHash);
}

/// <summary>Unattributed input. Runtime records the authenticated person as its manual source.</summary>
public sealed record ManualPartIdentityInput
{
    public ManualPartIdentityInput(string value)
    {
        Value = AlgorithmContractValidation.BoundedText(value, nameof(value), 256);
        if (string.IsNullOrWhiteSpace(Value)) throw new ArgumentException("ManualPartIdentityRequired");
    }
    public string Value { get; }
}

public enum ManualInspectionSessionPhase : byte
{
    Idle = 1, Admitted = 2, Preparing = 3, ReadyForRun = 4, Acquiring = 5,
    Executing = 6, Restoring = 7, Closed = 8, RecoveryBlocked = 9
}

public enum ManualInspectionRestorationState : byte
{
    NotRequired = 1, Pending = 2, Restored = 3, NoActiveBaselineClosed = 4, RecoveryBlocked = 5
}

public enum ManualInspectionExitMode : byte { Graceful = 1, Abort = 2 }

public abstract record ManualInspectionCommand : RuntimeCommand
{
    protected ManualInspectionCommand(Guid correlationId, CommandInvocation invocation, string reason)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("ManualCommandCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        Reason = RecipeActivationValidation.Reason(reason, nameof(reason));
    }
    public string Reason { get; }
    public abstract string AuthorizationTarget { get; }
    protected string Target(string operation, params string?[] values) =>
        AlgorithmContractValidation.HashParts(new[] { "sharpinspect-manual-command-v1", operation, Reason }.Concat(values));
}

/// <summary>Runtime allocates the session identity only after authenticated admission.</summary>
public sealed record StartManualInspectionSessionCommand : ManualInspectionCommand
{
    public StartManualInspectionSessionCommand(Guid correlationId, CommandInvocation invocation,
        ManualRecipeSelection selection, RecipeActivationReference? expectedActive, string reason)
        : base(correlationId, invocation, reason)
    {
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        ExpectedActive = RecipeActivationValidation.Reference(expectedActive);
        AuthorizationTarget = Target("Start", selection.ContentHash,
            ExpectedActive?.Position.ToString(CultureInfo.InvariantCulture), ExpectedActive?.ActivationId.ToString("D"),
            ExpectedActive?.ContentHash);
    }
    public ManualRecipeSelection Selection { get; }
    public RecipeActivationReference? ExpectedActive { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>One explicit soft trigger. The caller cannot supply a Manual Run or Inspection identity.</summary>
public sealed record RunManualInspectionCommand : ManualInspectionCommand
{
    public RunManualInspectionCommand(Guid correlationId, CommandInvocation invocation, Guid sessionId,
        ManualPartIdentityInput? partIdentity, string reason) : base(correlationId, invocation, reason)
    {
        SessionId = RecipeActivationValidation.RequiredGuid(sessionId, nameof(sessionId));
        PartIdentity = partIdentity;
        AuthorizationTarget = Target("Run", sessionId.ToString("D"), partIdentity?.Value);
    }
    public Guid SessionId { get; }
    public ManualPartIdentityInput? PartIdentity { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record ExitManualInspectionSessionCommand : ManualInspectionCommand
{
    public ExitManualInspectionSessionCommand(Guid correlationId, CommandInvocation invocation, Guid sessionId,
        ManualInspectionExitMode mode, string reason) : base(correlationId, invocation, reason)
    {
        SessionId = RecipeActivationValidation.RequiredGuid(sessionId, nameof(sessionId));
        Mode = AlgorithmConfigurationValidation.Enum(mode, nameof(mode));
        AuthorizationTarget = Target("Exit", sessionId.ToString("D"), Mode.ToString());
    }
    public Guid SessionId { get; }
    public ManualInspectionExitMode Mode { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record ManualInspectionSessionSnapshot(Guid RuntimeEpoch, long Revision, Guid? SessionId,
    ManualInspectionSessionPhase Phase, string ReasonCode, DateTimeOffset ObservedAtUtc,
    Guid? ActorPrincipalId, Guid? ActorSessionId, ManualRecipeSelection? Selection,
    Guid? CurrentManualRunId, Guid? LastManualRunId, ManualInspectionRestorationState Restoration,
    bool RecoveryRequired, bool ExitRequested, Guid? LastCommandCorrelationId)
{
    public bool IsSessionActive => SessionId.HasValue && Phase != ManualInspectionSessionPhase.Closed;
    public bool Ready => false;
    public bool ProductionAuthority => false;
    public bool CanIssueQualification => false;
}

public sealed record ManualInspectionAccess(bool CanRun, string ReasonCode, bool RequiresStepUp);
public sealed record ManualInspectionSessionReadResult(bool Available, string ReasonCode,
    ManualInspectionSessionSnapshot? Snapshot);

/// <summary>Observational capability. Commands are submitted through IStationRuntime.</summary>
public interface IManualInspectionSessionService
{
    ValueTask<ManualInspectionAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<ManualInspectionSessionReadResult> GetSnapshotAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
}
