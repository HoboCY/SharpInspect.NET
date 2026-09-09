using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>An exact immutable authoring revision, never a production selection.</summary>
public sealed record PreviewDraftReference
{
    public PreviewDraftReference(Guid draftId, long revision, string revisionContentHash)
    {
        if (draftId == Guid.Empty || revision < 1) throw new ArgumentException("PreviewDraftReferenceInvalid");
        DraftId = draftId;
        Revision = revision;
        RevisionContentHash = RecipeActivationValidation.Hash(revisionContentHash, nameof(revisionContentHash));
    }
    public Guid DraftId { get; }
    public long Revision { get; }
    public string RevisionContentHash { get; }
    public static PreviewDraftReference FromRevision(RecipeDraftRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new(revision.DraftId, revision.Revision, revision.RevisionContentHash);
    }
}

public enum PreviewSessionPhase : byte
{
    Idle = 1, Admitted = 2, Starting = 3, Streaming = 4, Tuning = 5,
    Freezing = 6, SavingDraft = 7, Restoring = 8, Closed = 9, RecoveryBlocked = 10
}

public enum PreviewRestorationState : byte
{
    NotRequired = 1, Pending = 2, Restored = 3, NoActiveBaselineClosed = 4, RecoveryBlocked = 5
}

/// <summary>All Preview transitions are explicit physical-console Runtime commands.</summary>
public abstract record PreviewSessionCommand : RuntimeCommand
{
    protected PreviewSessionCommand(Guid correlationId, CommandInvocation invocation, Guid previewSessionId,
        string changeReason) : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty || previewSessionId == Guid.Empty)
            throw new ArgumentException("PreviewCommandIdentityRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        PreviewSessionId = previewSessionId;
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
    }
    public Guid PreviewSessionId { get; }
    public string ChangeReason { get; }
    public abstract string AuthorizationTarget { get; }
    protected string Target(string operation, params string?[] values) =>
        AlgorithmContractValidation.HashParts(new[] { "sharpinspect-preview-command-v1", operation,
            PreviewSessionId.ToString("D"), ChangeReason }.Concat(values));
}

public sealed record StartPreviewSessionCommand : PreviewSessionCommand
{
    public StartPreviewSessionCommand(Guid correlationId, CommandInvocation invocation, Guid previewSessionId,
        PreviewDraftReference draft, RecipeActivationReference? expectedActive, string changeReason)
        : base(correlationId, invocation, previewSessionId, changeReason)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        ExpectedActive = RecipeActivationValidation.Reference(expectedActive);
        AuthorizationTarget = Target("Start", draft.DraftId.ToString("D"),
            draft.Revision.ToString(CultureInfo.InvariantCulture), draft.RevisionContentHash,
            expectedActive?.Position.ToString(CultureInfo.InvariantCulture),
            expectedActive?.ActivationId.ToString("D"), expectedActive?.ContentHash);
    }
    public PreviewDraftReference Draft { get; }
    public RecipeActivationReference? ExpectedActive { get; }
    public override string AuthorizationTarget { get; }
}

public sealed record ApplyPreviewTuningCommand : PreviewSessionCommand
{
    public ApplyPreviewTuningCommand(Guid correlationId, CommandInvocation invocation, Guid previewSessionId,
        PreviewTuningConfiguration configuration, string changeReason)
        : base(correlationId, invocation, previewSessionId, changeReason)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        AuthorizationTarget = Target("Tune", configuration.ContentHash);
    }
    public PreviewTuningConfiguration Configuration { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>Disables automatic controls and verifies fixed values; it does not save a Draft.</summary>
public sealed record FreezePreviewSettingsCommand : PreviewSessionCommand
{
    public FreezePreviewSettingsCommand(Guid correlationId, CommandInvocation invocation, Guid previewSessionId,
        string changeReason) : base(correlationId, invocation, previewSessionId, changeReason)
    { AuthorizationTarget = Target("Freeze"); }
    public override string AuthorizationTarget { get; }
}

/// <summary>A pure Draft save of a previously frozen Runtime snapshot, using the normal Draft authority.</summary>
public sealed record SavePreviewToDraftCommand : PreviewSessionCommand
{
    public SavePreviewToDraftCommand(Guid correlationId, CommandInvocation invocation, Guid previewSessionId,
        PreviewDraftReference expectedDraft, string frozenSettingsContentHash, string changeReason)
        : base(correlationId, invocation, previewSessionId, changeReason)
    {
        ExpectedDraft = expectedDraft ?? throw new ArgumentNullException(nameof(expectedDraft));
        FrozenSettingsContentHash = RecipeActivationValidation.Hash(frozenSettingsContentHash,
            nameof(frozenSettingsContentHash));
        AuthorizationTarget = Target("SaveDraft", expectedDraft.DraftId.ToString("D"),
            expectedDraft.Revision.ToString(CultureInfo.InvariantCulture), expectedDraft.RevisionContentHash,
            FrozenSettingsContentHash);
    }
    public PreviewDraftReference ExpectedDraft { get; }
    public string FrozenSettingsContentHash { get; }
    /// <summary>The Preview intent hash recorded in session history. Draft save
    /// reauthentication uses <see cref="CreateDraftSaveStepUpBinding"/>.</summary>
    public override string AuthorizationTarget { get; }

    /// <summary>Uses the existing Draft-save authority, with the same operation
    /// correlation and exact Draft identity as this command.</summary>
    public StepUpBinding CreateDraftSaveStepUpBinding() => new(Permission.EditRecipeDraft,
        CorrelationId, ExpectedDraft.DraftId.ToString("D"), AuditedCommandKind.SaveRecipeDraft);
}

public sealed record ExitPreviewSessionCommand : PreviewSessionCommand
{
    public ExitPreviewSessionCommand(Guid correlationId, CommandInvocation invocation, Guid previewSessionId,
        bool cancel, string changeReason) : base(correlationId, invocation, previewSessionId, changeReason)
    {
        Cancel = cancel;
        AuthorizationTarget = Target("Exit", cancel ? "Cancelled" : "Completed");
    }
    public bool Cancel { get; }
    public override string AuthorizationTarget { get; }
}

/// <summary>A complete non-authoritative projection. Frames have no inspection or execution identity.</summary>
public sealed record PreviewSessionSnapshot(Guid RuntimeEpoch, long Revision, Guid? PreviewSessionId,
    PreviewSessionPhase Phase, string ReasonCode, PreviewDraftReference? Draft,
    PreviewTuningConfiguration? Configuration, PreviewCameraProcessSettings? FrozenSettings,
    CameraPreviewFrame? LatestFrame, PreviewRestorationState Restoration, bool RecoveryRequired,
    Guid? LastCommandCorrelationId, PreviewDraftReference? LastSavedDraft)
{
    public bool IsSessionActive => PreviewSessionId.HasValue && Phase is not PreviewSessionPhase.Closed;
    public bool Ready => false;
    public string? FrozenSettingsContentHash => FrozenSettings?.ContentHash;
}

public sealed record PreviewSessionAccess(bool CanRun, string ReasonCode, bool RequiresStepUp);
public sealed record PreviewSessionReadResult(bool Available, string ReasonCode, PreviewSessionSnapshot? Snapshot);

/// <summary>Read-only session capability. Submit commands through IStationRuntime.</summary>
public interface IPreviewSessionService
{
    ValueTask<PreviewSessionAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<PreviewSessionReadResult> GetSnapshotAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
}
