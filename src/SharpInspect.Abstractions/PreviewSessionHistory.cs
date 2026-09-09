using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The durable identity of one non-production Preview session.  A header is an
/// immutable admission snapshot; later phase changes are represented by
/// <see cref="PreviewSessionEvent"/> values.
/// </summary>
public sealed record PreviewSessionHeader
{
    public PreviewSessionHeader(long position, Guid sessionId, Guid runtimeEpoch,
        Guid startCorrelationId, Guid attemptId, PreviewDraftReference draft,
        string logicalCameraRole, CameraBindingRevision currentBinding,
        RecipeActivationReference? activeActivation, string? activeSnapshotContentHash,
        string? activeCameraContentHash, Guid actorPrincipalId, Guid actorSessionId,
        long actorAuthorizationRevision, RecipeContractReference authorizationPolicy,
        string authorizationTarget, string changeReason, DateTimeOffset startedAtUtc,
        PreviewSessionPhase phase = PreviewSessionPhase.Admitted,
        PreviewRestorationState restoration = PreviewRestorationState.NotRequired,
        string reasonCode = "PreviewSessionAdmitted", bool recoveryRequired = false,
        string? frozenSettingsContentHash = null, PreviewDraftReference? lastSavedDraft = null)
    {
        if (position < 1 || sessionId == Guid.Empty || runtimeEpoch == Guid.Empty ||
            startCorrelationId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("PreviewSessionHeaderIdentityInvalid");
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        LogicalCameraRole = ValidateIdentifier(logicalCameraRole, nameof(logicalCameraRole));
        CurrentBinding = currentBinding ?? throw new ArgumentNullException(nameof(currentBinding));
        if (CurrentBinding.LogicalRole != LogicalCameraRole)
            throw new ArgumentException("PreviewSessionBindingRoleMismatch", nameof(currentBinding));
        ActiveActivation = RecipeActivationValidation.Reference(activeActivation);
        ActiveSnapshotContentHash = ValidateOptionalHash(activeSnapshotContentHash,
            nameof(activeSnapshotContentHash));
        ActiveCameraContentHash = ValidateOptionalHash(activeCameraContentHash,
            nameof(activeCameraContentHash));
        if (ActiveActivation is null && (ActiveSnapshotContentHash is not null ||
            ActiveCameraContentHash is not null))
            throw new ArgumentException("PreviewSessionActiveBaselineMismatch");
        if (ActiveActivation is not null && (ActiveSnapshotContentHash is null ||
            ActiveCameraContentHash is null))
            throw new ArgumentException("PreviewSessionActiveBaselineRequired");
        if (actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            actorAuthorizationRevision < 0)
            throw new ArgumentException("PreviewSessionActorInvalid");
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(restoration)) throw new ArgumentOutOfRangeException(nameof(restoration));
        if (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("PreviewSessionTimestampInvalid", nameof(startedAtUtc));
        FrozenSettingsContentHash = ValidateOptionalHash(frozenSettingsContentHash,
            nameof(frozenSettingsContentHash));
        LastSavedDraft = lastSavedDraft;

        Position = position;
        SessionId = sessionId;
        RuntimeEpoch = runtimeEpoch;
        StartCorrelationId = startCorrelationId;
        AttemptId = attemptId;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision;
        StartedAtUtc = startedAtUtc;
        Phase = phase;
        Restoration = restoration;
        RecoveryRequired = recoveryRequired;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-preview-session-header-v1",
            position.ToString(CultureInfo.InvariantCulture), sessionId.ToString("D"),
            runtimeEpoch.ToString("D"), startCorrelationId.ToString("D"), attemptId.ToString("D"),
            draft.DraftId.ToString("D"), draft.Revision.ToString(CultureInfo.InvariantCulture),
            draft.RevisionContentHash, LogicalCameraRole,
            CurrentBinding.Position.ToString(CultureInfo.InvariantCulture),
            CurrentBinding.Revision.ToString(CultureInfo.InvariantCulture), CurrentBinding.RevisionHash,
            ActiveActivation?.Position.ToString(CultureInfo.InvariantCulture),
            ActiveActivation?.ActivationId.ToString("D"), ActiveActivation?.ContentHash,
            ActiveSnapshotContentHash, ActiveCameraContentHash, actorPrincipalId.ToString("D"),
            actorSessionId.ToString("D"), actorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            authorizationPolicy.Id, authorizationPolicy.Version, authorizationPolicy.ContentHash,
            AuthorizationTarget, ChangeReason, startedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            phase.ToString(), restoration.ToString(), ReasonCode, recoveryRequired ? "1" : "0",
            FrozenSettingsContentHash, lastSavedDraft?.DraftId.ToString("D"),
            lastSavedDraft?.Revision.ToString(CultureInfo.InvariantCulture), lastSavedDraft?.RevisionContentHash
        });
    }

    public long Position { get; }
    public Guid SessionId { get; }
    public Guid RuntimeEpoch { get; }
    public Guid StartCorrelationId { get; }
    public Guid AttemptId { get; }
    public PreviewDraftReference Draft { get; }
    public string LogicalCameraRole { get; }
    public CameraBindingRevision CurrentBinding { get; }
    public RecipeActivationReference? ActiveActivation { get; }
    public string? ActiveSnapshotContentHash { get; }
    public string? ActiveCameraContentHash { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public string AuthorizationTarget { get; }
    public string ChangeReason { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public PreviewSessionPhase Phase { get; }
    public PreviewRestorationState Restoration { get; }
    public string ReasonCode { get; }
    public bool RecoveryRequired { get; }
    public string? FrozenSettingsContentHash { get; }
    public PreviewDraftReference? LastSavedDraft { get; }
    public string ContentHash { get; }

    public bool IsActive => Phase is not PreviewSessionPhase.Closed and
        not PreviewSessionPhase.RecoveryBlocked;

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("PreviewSessionIdentifierInvalid", parameterName);
        return value;
    }

    private static string? ValidateOptionalHash(string? value, string parameterName) => value is null
        ? null : RecipeActivationValidation.Hash(value, parameterName);
}

/// <summary>One immutable Preview phase or terminal fact. Frames are deliberately absent.</summary>
public sealed record PreviewSessionEvent
{
    public PreviewSessionEvent(long position, PreviewSessionHeader header, Guid attemptId,
        Guid commandCorrelationId, AuditedCommandKind commandKind, PreviewSessionPhase phase,
        PreviewRestorationState restoration, string reasonCode, bool terminal,
        Guid actorPrincipalId, Guid actorSessionId, long actorAuthorizationRevision,
        string authorizationTarget, DateTimeOffset recordedAtUtc,
        PreviewTuningConfiguration? configuration = null,
        PreviewDraftReference? savedDraft = null, string? frozenSettingsContentHash = null,
        long? commandAuditSequence = null, string? commandAuditHash = null,
        long? authorizationAuditSequence = null, string? authorizationAuditHash = null,
        string? payloadHash = null, long auditSequence = 0, string? auditHash = null)
    {
        if (position < 1 || attemptId == Guid.Empty || commandCorrelationId == Guid.Empty ||
            actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            actorAuthorizationRevision < 0)
            throw new ArgumentException("PreviewSessionEventIdentityInvalid");
        Header = header ?? throw new ArgumentNullException(nameof(header));
        if (Header.SessionId == Guid.Empty || !Enum.IsDefined(commandKind) ||
            !Enum.IsDefined(phase) || !Enum.IsDefined(restoration))
            throw new ArgumentException("PreviewSessionEventValueInvalid");
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget,
            nameof(authorizationTarget));
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("PreviewSessionTimestampInvalid", nameof(recordedAtUtc));
        FrozenSettingsContentHash = frozenSettingsContentHash is null ? null :
            RecipeActivationValidation.Hash(frozenSettingsContentHash, nameof(frozenSettingsContentHash));
        PayloadHash = payloadHash is null ? null : RecipeActivationValidation.Hash(payloadHash, nameof(payloadHash));
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("PreviewSessionAuditReferenceInvalid");
        AuditHash = auditHash is null ? null : RecipeActivationValidation.Hash(auditHash, nameof(auditHash));
        if (commandAuditSequence is < 1 || authorizationAuditSequence is < 1)
            throw new ArgumentException("PreviewSessionAuditReferenceInvalid");
        CommandAuditHash = commandAuditHash is null ? null : RecipeActivationValidation.Hash(commandAuditHash, nameof(commandAuditHash));
        AuthorizationAuditHash = authorizationAuditHash is null ? null : RecipeActivationValidation.Hash(authorizationAuditHash, nameof(authorizationAuditHash));
        if ((commandAuditSequence.HasValue != (commandAuditHash is not null)) ||
            (authorizationAuditSequence.HasValue != (authorizationAuditHash is not null)))
            throw new ArgumentException("PreviewSessionAuditReferenceInvalid");

        Position = position;
        AttemptId = attemptId;
        CommandCorrelationId = commandCorrelationId;
        CommandKind = commandKind;
        Phase = phase;
        Restoration = restoration;
        Terminal = terminal;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision;
        RecordedAtUtc = recordedAtUtc;
        Configuration = configuration;
        SavedDraft = savedDraft;
        AuditSequence = auditSequence;
        CommandAuditSequence = commandAuditSequence;
        AuthorizationAuditSequence = authorizationAuditSequence;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-preview-session-event-v1", position.ToString(CultureInfo.InvariantCulture),
            Header.ContentHash, attemptId.ToString("D"), commandCorrelationId.ToString("D"),
            commandKind.ToString(), phase.ToString(), restoration.ToString(), ReasonCode,
            terminal ? "1" : "0", actorPrincipalId.ToString("D"), actorSessionId.ToString("D"),
            actorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), AuthorizationTarget,
            recordedAtUtc.ToString("O", CultureInfo.InvariantCulture), configuration?.ContentHash,
            savedDraft?.DraftId.ToString("D"), savedDraft?.Revision.ToString(CultureInfo.InvariantCulture),
            savedDraft?.RevisionContentHash, FrozenSettingsContentHash
        });
    }

    public long Position { get; }
    public PreviewSessionHeader Header { get; }
    public Guid SessionId => Header.SessionId;
    public Guid RuntimeEpoch => Header.RuntimeEpoch;
    public Guid AttemptId { get; }
    public Guid CommandCorrelationId { get; }
    public AuditedCommandKind CommandKind { get; }
    public PreviewSessionPhase Phase { get; }
    public PreviewRestorationState Restoration { get; }
    public string ReasonCode { get; }
    public bool Terminal { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public string AuthorizationTarget { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public PreviewTuningConfiguration? Configuration { get; }
    public PreviewDraftReference? SavedDraft { get; }
    public string? FrozenSettingsContentHash { get; }
    public long? CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long? AuthorizationAuditSequence { get; }
    public string? AuthorizationAuditHash { get; }
    public string? PayloadHash { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }
}

public sealed record PreviewSessionHistoryFilter(Guid? SessionId = null,
    long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);

public sealed record PreviewSessionHistoryReadResult(bool Available, string ReasonCode,
    PreviewSessionHeader? Header = null, bool RecoveryRequired = false);

public sealed record PreviewSessionHistoryPage(bool Available, string ReasonCode,
    ReadOnlyCollection<PreviewSessionEvent> Events, long ThroughPosition,
    long? NextAfterPosition, PreviewSessionHeader? PendingHeader = null,
    bool RecoveryRequired = false);

/// <summary>Bounded read-only Preview history. It exposes no writer or arbitrary SQL.</summary>
public interface IPreviewSessionHistoryQuery
{
    ValueTask<PreviewSessionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<PreviewSessionHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default);
    ValueTask<PreviewSessionHistoryPage> QueryAsync(PreviewSessionHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
