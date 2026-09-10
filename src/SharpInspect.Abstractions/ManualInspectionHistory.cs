using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Terminal or in-flight state of one explicitly admitted Manual run.</summary>
public enum ManualInspectionRunStatus : byte
{
    Admitted = 1,
    Acquiring = 2,
    Executing = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
    TimedOut = 7
}

/// <summary>Source of an optional manual part identity. It is attribution only.</summary>
public enum ManualInspectionPartIdentitySource : byte
{
    NotProduction = 1,
    HumanEntered = 2
}

/// <summary>
/// A bounded reference to evidence belonging to a Manual run.  The reference is
/// descriptive only; it never grants access to a production evidence store.
/// </summary>
public sealed record ManualInspectionEvidenceReference
{
    public ManualInspectionEvidenceReference(string kind, string contentHash, string? reference = null)
    {
        Kind = AlgorithmContractValidation.Identifier(kind, nameof(kind), 64);
        ContentHash = RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        Reference = reference is null ? null :
            AlgorithmContractValidation.BoundedText(reference, nameof(reference), 256);
    }

    public string Kind { get; }
    public string ContentHash { get; }
    public string? Reference { get; }
}

/// <summary>
/// Immutable source and pre-session baseline captured when a Manual session is
/// admitted.  The source identity remains a Draft or Released identity; it is
/// never an Active recipe and carries no production authority.
/// </summary>
public sealed record ManualInspectionSessionHeader
{
    public ManualInspectionSessionHeader(long position, Guid sessionId, Guid runtimeEpoch,
        Guid startCorrelationId, Guid attemptId, ManualRecipeSelection selection,
        string sourceContentHash, CameraBindingRevision currentBinding,
        RecipeActivationReference? activeActivation, string? activeSnapshotContentHash,
        string? activeCameraContentHash, Guid actorPrincipalId, Guid actorSessionId,
        long actorAuthorizationRevision, RecipeContractReference authorizationPolicy,
        string authorizationTarget, string changeReason, DateTimeOffset startedAtUtc,
        ManualInspectionSessionPhase phase = ManualInspectionSessionPhase.Admitted,
        ManualInspectionRestorationState restoration = ManualInspectionRestorationState.NotRequired,
        string reasonCode = "ManualInspectionSessionAdmitted", bool recoveryRequired = false)
    {
        if (position < 1 || sessionId == Guid.Empty || runtimeEpoch == Guid.Empty ||
            startCorrelationId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("ManualInspectionHeaderIdentityInvalid");
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        SourceContentHash = RecipeActivationValidation.Hash(sourceContentHash, nameof(sourceContentHash));
        CurrentBinding = currentBinding ?? throw new ArgumentNullException(nameof(currentBinding));
        ActiveActivation = RecipeActivationValidation.Reference(activeActivation);
        ActiveSnapshotContentHash = ValidateOptionalHash(activeSnapshotContentHash,
            nameof(activeSnapshotContentHash));
        ActiveCameraContentHash = ValidateOptionalHash(activeCameraContentHash,
            nameof(activeCameraContentHash));
        if (ActiveActivation is null && (ActiveSnapshotContentHash is not null ||
            ActiveCameraContentHash is not null))
            throw new ArgumentException("ManualInspectionActiveBaselineMismatch");
        if (ActiveActivation is not null && (ActiveSnapshotContentHash is null ||
            ActiveCameraContentHash is null))
            throw new ArgumentException("ManualInspectionActiveBaselineRequired");
        if (actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            actorAuthorizationRevision < 0)
            throw new ArgumentException("ManualInspectionActorInvalid");
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget,
            nameof(authorizationTarget));
        ChangeReason = RecipeActivationValidation.Reason(changeReason, nameof(changeReason));
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(restoration)) throw new ArgumentOutOfRangeException(nameof(restoration));
        if (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ManualInspectionTimestampInvalid", nameof(startedAtUtc));

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
            "sharpinspect-manual-session-header-v1", Number(position), sessionId.ToString("D"),
            runtimeEpoch.ToString("D"), startCorrelationId.ToString("D"), attemptId.ToString("D"),
            selection.ContentHash, SourceContentHash, CurrentBinding.Position.ToString(CultureInfo.InvariantCulture),
            CurrentBinding.Revision.ToString(CultureInfo.InvariantCulture), CurrentBinding.RevisionHash,
            ActiveActivation?.Position.ToString(CultureInfo.InvariantCulture),
            ActiveActivation?.ActivationId.ToString("D"), ActiveActivation?.ContentHash,
            ActiveSnapshotContentHash, ActiveCameraContentHash, actorPrincipalId.ToString("D"),
            actorSessionId.ToString("D"), actorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            authorizationPolicy.Id, authorizationPolicy.Version, authorizationPolicy.ContentHash,
            AuthorizationTarget, ChangeReason, startedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            phase.ToString(), restoration.ToString(), ReasonCode, recoveryRequired ? "1" : "0"
        });
    }

    public long Position { get; }
    public Guid SessionId { get; }
    public Guid RuntimeEpoch { get; }
    public Guid StartCorrelationId { get; }
    public Guid AttemptId { get; }
    public ManualRecipeSelection Selection { get; }
    public string SourceContentHash { get; }
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
    public ManualInspectionSessionPhase Phase { get; }
    public ManualInspectionRestorationState Restoration { get; }
    public string ReasonCode { get; }
    public bool RecoveryRequired { get; }
    public string ContentHash { get; }

    public bool IsActive => Phase is not ManualInspectionSessionPhase.Closed and
        not ManualInspectionSessionPhase.RecoveryBlocked;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? ValidateOptionalHash(string? value, string parameterName) => value is null
        ? null : RecipeActivationValidation.Hash(value, parameterName);
}

/// <summary>One immutable Manual phase, terminal fact, or restart recovery marker.</summary>
public sealed record ManualInspectionSessionEvent
{
    public ManualInspectionSessionEvent(long position, ManualInspectionSessionHeader header,
        Guid attemptId, Guid commandCorrelationId, AuditedCommandKind commandKind,
        ManualInspectionSessionPhase phase, ManualInspectionRestorationState restoration,
        string reasonCode, bool terminal, Guid actorPrincipalId, Guid actorSessionId,
        long actorAuthorizationRevision, string authorizationTarget,
        DateTimeOffset recordedAtUtc, Guid? manualRunId = null,
        string? outcomeContentHash = null, long? commandAuditSequence = null,
        string? commandAuditHash = null, long? authorizationAuditSequence = null,
        string? authorizationAuditHash = null, string? payloadHash = null,
        long auditSequence = 0, string? auditHash = null)
    {
        if (position < 1 || attemptId == Guid.Empty || commandCorrelationId == Guid.Empty ||
            actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            actorAuthorizationRevision < 0)
            throw new ArgumentException("ManualInspectionEventIdentityInvalid");
        Header = header ?? throw new ArgumentNullException(nameof(header));
        if (Header.SessionId == Guid.Empty || !Enum.IsDefined(commandKind) ||
            !Enum.IsDefined(phase) || !Enum.IsDefined(restoration))
            throw new ArgumentException("ManualInspectionEventValueInvalid");
        if (manualRunId is Guid run && run == Guid.Empty)
            throw new ArgumentException("ManualInspectionRunIdentityInvalid", nameof(manualRunId));
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget,
            nameof(authorizationTarget));
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ManualInspectionTimestampInvalid", nameof(recordedAtUtc));
        OutcomeContentHash = outcomeContentHash is null ? null :
            RecipeActivationValidation.Hash(outcomeContentHash, nameof(outcomeContentHash));
        PayloadHash = payloadHash is null ? null : RecipeActivationValidation.Hash(payloadHash,
            nameof(payloadHash));
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("ManualInspectionAuditReferenceInvalid");
        AuditHash = auditHash is null ? null : RecipeActivationValidation.Hash(auditHash,
            nameof(auditHash));
        if ((commandAuditSequence.HasValue != (commandAuditHash is not null)) ||
            (authorizationAuditSequence.HasValue != (authorizationAuditHash is not null)))
            throw new ArgumentException("ManualInspectionAuditReferenceInvalid");
        if (commandAuditSequence is < 1 || authorizationAuditSequence is < 1)
            throw new ArgumentException("ManualInspectionAuditReferenceInvalid");

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
        ManualRunId = manualRunId;
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = commandAuditHash;
        AuthorizationAuditSequence = authorizationAuditSequence;
        AuthorizationAuditHash = authorizationAuditHash;
        AuditSequence = auditSequence;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-manual-session-event-v1", Number(position), Header.ContentHash,
            attemptId.ToString("D"), commandCorrelationId.ToString("D"), commandKind.ToString(),
            phase.ToString(), restoration.ToString(), ReasonCode, terminal ? "1" : "0",
            actorPrincipalId.ToString("D"), actorSessionId.ToString("D"),
            actorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), AuthorizationTarget,
            recordedAtUtc.ToString("O", CultureInfo.InvariantCulture), manualRunId?.ToString("D"),
            OutcomeContentHash
        });
    }

    public long Position { get; }
    public ManualInspectionSessionHeader Header { get; }
    public Guid SessionId => Header.SessionId;
    public Guid RuntimeEpoch => Header.RuntimeEpoch;
    public Guid AttemptId { get; }
    public Guid CommandCorrelationId { get; }
    public AuditedCommandKind CommandKind { get; }
    public ManualInspectionSessionPhase Phase { get; }
    public ManualInspectionRestorationState Restoration { get; }
    public string ReasonCode { get; }
    public bool Terminal { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public string AuthorizationTarget { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public Guid? ManualRunId { get; }
    public string? OutcomeContentHash { get; }
    public long? CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long? AuthorizationAuditSequence { get; }
    public string? AuthorizationAuditHash { get; }
    public string? PayloadHash { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Immutable result and provenance for one Manual frame execution.</summary>
public sealed record ManualInspectionRunRecord
{
    public ManualInspectionRunRecord(long position, Guid runId, Guid sessionId, Guid runtimeEpoch,
        Guid commandCorrelationId, Guid attemptId, ManualPartIdentityInput? partIdentity,
        ManualInspectionRunStatus status, InspectionDecision decision, ExecutionStatus? executionStatus,
        string reasonCode, DateTimeOffset admittedAtUtc, DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc, FrameMetadata? frameMetadata,
        FrameProvenance? frameProvenance, string? algorithmResultPayload,
        string? algorithmResultContentHash, IEnumerable<ManualInspectionEvidenceReference>? evidence = null,
        long? commandAuditSequence = null, string? commandAuditHash = null,
        long auditSequence = 0, string? auditHash = null,
        ManualInspectionPartIdentitySource? partIdentitySource = null,
        Guid? partIdentityActorPrincipalId = null, Guid? partIdentityActorSessionId = null,
        Guid? preparedInstanceId = null, AlgorithmIdentity? algorithm = null,
        AlgorithmConfigurationSnapshot? configuration = null,
        string? configurationContentHash = null, string? configurationSchemaId = null,
        string? configurationSchemaVersion = null, string? configurationSchemaContentHash = null,
        AlgorithmResultSchema? resultSchema = null, AlgorithmResult? result = null,
        FrameOverlaySnapshot? frameOverlay = null, AlgorithmExecutionTimingSnapshot? timing = null,
        long? admittedMonotonicTimestamp = null, long? monotonicFrequency = null,
        long droppedDiagnosticCount = 0, string? resultProjectionContentHash = null,
        string? frameOverlayContentHash = null, string? resultSchemaContentHash = null)
    {
        if (position < 1 || runId == Guid.Empty || sessionId == Guid.Empty || runtimeEpoch == Guid.Empty ||
            commandCorrelationId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("ManualInspectionRunIdentityInvalid");
        if (!Enum.IsDefined(status) || !Enum.IsDefined(decision) ||
            (executionStatus is { } execution && !Enum.IsDefined(execution)))
            throw new ArgumentException("ManualInspectionRunValueInvalid");
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        if (admittedAtUtc == default || admittedAtUtc.Offset != TimeSpan.Zero ||
            (startedAtUtc is { } started && (started == default || started.Offset != TimeSpan.Zero)) ||
            (completedAtUtc is { } completed && (completed == default || completed.Offset != TimeSpan.Zero)))
            throw new ArgumentException("ManualInspectionRunTimestampInvalid");
        if (startedAtUtc is { } startedAt && startedAt < admittedAtUtc)
            throw new ArgumentException("ManualInspectionRunTimestampOrderInvalid");
        if (completedAtUtc is { } completedAt &&
            (completedAt < admittedAtUtc || (startedAtUtc is { } startedAt2 && completedAt < startedAt2)))
            throw new ArgumentException("ManualInspectionRunTimestampOrderInvalid");
        if (algorithmResultPayload is { Length: > 16 * 1024 * 1024 })
            throw new ArgumentException("ManualInspectionResultCapacityExceeded", nameof(algorithmResultPayload));
        AlgorithmResultPayload = algorithmResultPayload;
        AlgorithmResultContentHash = algorithmResultContentHash is null ? null :
            RecipeActivationValidation.Hash(algorithmResultContentHash, nameof(algorithmResultContentHash));
        if (AlgorithmResultPayload is null && AlgorithmResultContentHash is not null)
            throw new ArgumentException("ManualInspectionResultHashMismatch");
        if (AlgorithmResultPayload is not null && AlgorithmResultContentHash is null)
            throw new ArgumentException("ManualInspectionResultHashRequired");
        if (executionStatus is null && (algorithmResultPayload is not null || result is not null ||
            resultSchema is not null || frameOverlay is not null))
            throw new ArgumentException("ManualInspectionInFlightResultForbidden");
        if (executionStatus is null && decision != InspectionDecision.Unknown)
            throw new ArgumentException("ManualInspectionInFlightDecisionInvalid");
        if (executionStatus is not global::SharpInspect.Abstractions.ExecutionStatus.Success && algorithmResultPayload is not null)
            throw new ArgumentException("ManualInspectionFailureResultForbidden");
        if (executionStatus is not global::SharpInspect.Abstractions.ExecutionStatus.Success && decision != InspectionDecision.Unknown)
            throw new ArgumentException("ManualInspectionFailureDecisionInvalid");
        if (status == ManualInspectionRunStatus.Completed && executionStatus != global::SharpInspect.Abstractions.ExecutionStatus.Success)
            throw new ArgumentException("ManualInspectionCompletedStatusMismatch");
        if (status == ManualInspectionRunStatus.Failed && executionStatus != global::SharpInspect.Abstractions.ExecutionStatus.Error)
            throw new ArgumentException("ManualInspectionFailedStatusMismatch");
        if (status == ManualInspectionRunStatus.Cancelled && executionStatus != global::SharpInspect.Abstractions.ExecutionStatus.Cancelled)
            throw new ArgumentException("ManualInspectionCancelledStatusMismatch");
        if (status == ManualInspectionRunStatus.TimedOut && executionStatus != global::SharpInspect.Abstractions.ExecutionStatus.Timeout)
            throw new ArgumentException("ManualInspectionTimedOutStatusMismatch");
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null) ||
            commandAuditSequence is < 1 || (commandAuditSequence.HasValue != (commandAuditHash is not null)))
            throw new ArgumentException("ManualInspectionAuditReferenceInvalid");
        if (droppedDiagnosticCount < 0)
            throw new ArgumentOutOfRangeException(nameof(droppedDiagnosticCount));
        if (partIdentitySource is { } source && !Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(partIdentitySource));
        if (partIdentity is null && partIdentitySource is ManualInspectionPartIdentitySource.HumanEntered)
            throw new ArgumentException("ManualInspectionPartIdentitySourceMismatch");
        if (partIdentity is not null && partIdentitySource is ManualInspectionPartIdentitySource.NotProduction)
            throw new ArgumentException("ManualInspectionPartIdentitySourceMismatch");
        if (partIdentityActorPrincipalId is Guid partPrincipal && partPrincipal == Guid.Empty)
            throw new ArgumentException("ManualInspectionPartIdentityAttributionInvalid");
        if (partIdentityActorSessionId is Guid partSession && partSession == Guid.Empty)
            throw new ArgumentException("ManualInspectionPartIdentityAttributionInvalid");
        if (partIdentity is not null && (partIdentityActorPrincipalId is null ||
            partIdentityActorSessionId is null))
        {
            // Older in-process writers may only have the session header at this
            // seam. The read projection binds attribution from that header; new
            // writers should pass both explicit actor references.
        }
        if (partIdentity is null && (partIdentityActorPrincipalId is not null ||
            partIdentityActorSessionId is not null))
            throw new ArgumentException("ManualInspectionPartIdentityAttributionMismatch");
        if (preparedInstanceId is Guid prepared && prepared == Guid.Empty)
            throw new ArgumentException("ManualInspectionPreparedInstanceInvalid");
        if (algorithm is null && (configuration is not null || configurationContentHash is not null ||
            configurationSchemaId is not null || configurationSchemaVersion is not null ||
            configurationSchemaContentHash is not null))
            throw new ArgumentException("ManualInspectionAlgorithmIdentityRequired");
        if (configuration is not null)
        {
            if (configurationContentHash is not null && configurationContentHash != configuration.ContentHash)
                throw new ArgumentException("ManualInspectionConfigurationHashMismatch");
            if (configurationSchemaId is not null && configurationSchemaId != configuration.SchemaId)
                throw new ArgumentException("ManualInspectionConfigurationSchemaMismatch");
            if (configurationSchemaVersion is not null && configurationSchemaVersion != configuration.SchemaVersion)
                throw new ArgumentException("ManualInspectionConfigurationSchemaMismatch");
            if (configurationSchemaContentHash is not null && configurationSchemaContentHash != configuration.SchemaContentHash)
                throw new ArgumentException("ManualInspectionConfigurationSchemaMismatch");
        }
        if (result is not null && executionStatus != global::SharpInspect.Abstractions.ExecutionStatus.Success)
            throw new ArgumentException("ManualInspectionResultProjectionStatusMismatch");
        if (result is not null && resultSchema is null)
            throw new ArgumentException("ManualInspectionResultSchemaRequired");
        if (frameOverlay is not null && (frameMetadata is null || resultSchema is null ||
            frameOverlay.FrameMetadata != frameMetadata || frameOverlay.ResultSchema != resultSchema))
            throw new ArgumentException("ManualInspectionOverlayProjectionMismatch");
        if (timing is not null && (preparedInstanceId is null || algorithm is null))
            throw new ArgumentException("ManualInspectionTimingIdentityRequired");
        if (admittedMonotonicTimestamp is < 0 || monotonicFrequency is < 1)
            throw new ArgumentException("ManualInspectionMonotonicEvidenceInvalid");
        if (resultProjectionContentHash is not null)
            resultProjectionContentHash = RecipeActivationValidation.Hash(resultProjectionContentHash,
                nameof(resultProjectionContentHash));
        if (frameOverlayContentHash is not null)
            frameOverlayContentHash = RecipeActivationValidation.Hash(frameOverlayContentHash,
                nameof(frameOverlayContentHash));
        var resultProjectionHash = resultProjectionContentHash ?? ResultProjectionFingerprint(result);
        var overlayProjectionHash = frameOverlayContentHash ?? frameOverlay?.ContentHash;

        Position = position;
        RunId = runId;
        SessionId = sessionId;
        RuntimeEpoch = runtimeEpoch;
        CommandCorrelationId = commandCorrelationId;
        AttemptId = attemptId;
        PartIdentity = partIdentity;
        Status = status;
        Decision = decision;
        ExecutionStatus = executionStatus;
        AdmittedAtUtc = admittedAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FrameMetadata = frameMetadata;
        FrameProvenance = frameProvenance;
        Evidence = new ReadOnlyCollection<ManualInspectionEvidenceReference>(
            (evidence ?? Array.Empty<ManualInspectionEvidenceReference>()).ToArray());
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = commandAuditHash is null ? null : RecipeActivationValidation.Hash(commandAuditHash,
            nameof(commandAuditHash));
        PartIdentitySource = partIdentitySource ?? (partIdentity is null
            ? ManualInspectionPartIdentitySource.NotProduction : ManualInspectionPartIdentitySource.HumanEntered);
        PartIdentityActorPrincipalId = partIdentityActorPrincipalId;
        PartIdentityActorSessionId = partIdentityActorSessionId;
        PreparedInstanceId = preparedInstanceId;
        Algorithm = algorithm;
        Configuration = configuration;
        ConfigurationContentHash = configuration is not null ? configuration.ContentHash :
            configurationContentHash is null ? null : RecipeActivationValidation.Hash(configurationContentHash,
                nameof(configurationContentHash));
        ConfigurationSchemaId = configuration?.SchemaId ?? configurationSchemaId;
        ConfigurationSchemaVersion = configuration?.SchemaVersion ?? configurationSchemaVersion;
        ConfigurationSchemaContentHash = configuration is not null ? configuration.SchemaContentHash :
            configurationSchemaContentHash is null ? null : RecipeActivationValidation.Hash(configurationSchemaContentHash,
                nameof(configurationSchemaContentHash));
        ResultSchema = resultSchema;
        ResultSchemaContentHash = resultSchema is not null ? resultSchema.ContentHash :
            resultSchemaContentHash is null ? null : RecipeActivationValidation.Hash(resultSchemaContentHash,
                nameof(resultSchemaContentHash));
        Result = result;
        FrameOverlay = frameOverlay;
        ResultProjectionContentHash = resultProjectionHash;
        FrameOverlayContentHash = overlayProjectionHash;
        Timing = timing;
        AdmittedMonotonicTimestamp = admittedMonotonicTimestamp;
        MonotonicFrequency = monotonicFrequency;
        DroppedDiagnosticCount = droppedDiagnosticCount;
        AuditSequence = auditSequence;
        AuditHash = auditHash is null ? null : RecipeActivationValidation.Hash(auditHash, nameof(auditHash));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-manual-run-v1", Number(position), runId.ToString("D"), sessionId.ToString("D"),
            runtimeEpoch.ToString("D"), commandCorrelationId.ToString("D"), attemptId.ToString("D"),
            partIdentity?.Value, PartIdentitySource.ToString(), PartIdentityActorPrincipalId?.ToString("D"),
            PartIdentityActorSessionId?.ToString("D"), status.ToString(), decision.ToString(), executionStatus?.ToString(), ReasonCode,
            admittedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            startedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            completedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            FrameFingerprint(frameMetadata), FrameFingerprint(frameProvenance), PreparedInstanceId?.ToString("D"),
            Algorithm?.Id, Algorithm?.Version, Configuration?.ContentHash ?? ConfigurationContentHash,
            ConfigurationSchemaId, ConfigurationSchemaVersion, ConfigurationSchemaContentHash,
            ResultSchema?.ContentHash ?? ResultSchemaContentHash, ResultProjectionContentHash,
            FrameOverlayContentHash, TimingFingerprint(Timing),
            admittedMonotonicTimestamp?.ToString(CultureInfo.InvariantCulture),
            monotonicFrequency?.ToString(CultureInfo.InvariantCulture),
            droppedDiagnosticCount.ToString(CultureInfo.InvariantCulture), AlgorithmResultContentHash,
            string.Join("|", Evidence.Select(item => item.Kind + ":" + item.ContentHash)),
            commandAuditSequence?.ToString(CultureInfo.InvariantCulture), commandAuditHash,
            auditSequence.ToString(CultureInfo.InvariantCulture), AuditHash
        });
    }

    public long Position { get; }
    public Guid RunId { get; }
    public Guid SessionId { get; }
    public Guid RuntimeEpoch { get; }
    public Guid CommandCorrelationId { get; }
    public Guid AttemptId { get; }
    public ManualPartIdentityInput? PartIdentity { get; }
    public ManualInspectionPartIdentitySource PartIdentitySource { get; }
    public Guid? PartIdentityActorPrincipalId { get; }
    public Guid? PartIdentityActorSessionId { get; }
    public ManualInspectionRunStatus Status { get; }
    public InspectionDecision Decision { get; }
    public ExecutionStatus? ExecutionStatus { get; }
    public string ReasonCode { get; }
    public DateTimeOffset AdmittedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public FrameMetadata? FrameMetadata { get; }
    public FrameProvenance? FrameProvenance { get; }
    public Guid? PreparedInstanceId { get; }
    public AlgorithmIdentity? Algorithm { get; }
    public AlgorithmConfigurationSnapshot? Configuration { get; }
    public string? ConfigurationContentHash { get; }
    public string? ConfigurationSchemaId { get; }
    public string? ConfigurationSchemaVersion { get; }
    public string? ConfigurationSchemaContentHash { get; }
    public AlgorithmResultSchema? ResultSchema { get; }
    public string? ResultSchemaContentHash { get; }
    public AlgorithmResult? Result { get; }
    public FrameOverlaySnapshot? FrameOverlay { get; }
    public string? ResultProjectionContentHash { get; }
    public string? FrameOverlayContentHash { get; }
    public AlgorithmExecutionTimingSnapshot? Timing { get; }
    public long? AdmittedMonotonicTimestamp { get; }
    public long? MonotonicFrequency { get; }
    public long DroppedDiagnosticCount { get; }
    public string? AlgorithmResultPayload { get; }
    public string? ResultPayload => AlgorithmResultPayload;
    public string? AlgorithmResultContentHash { get; }
    public string? ResultContentHash => AlgorithmResultContentHash;
    public ReadOnlyCollection<ManualInspectionEvidenceReference> Evidence { get; }
    public long? CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }
    public bool Terminal => Status is ManualInspectionRunStatus.Completed or ManualInspectionRunStatus.Failed or
        ManualInspectionRunStatus.Cancelled or ManualInspectionRunStatus.TimedOut;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? FrameFingerprint(FrameMetadata? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "manual-frame-metadata-v2", value.Correlation.Kind.ToString(), value.Correlation.Value.ToString("D"),
            value.LogicalCameraRole, value.Width.ToString(CultureInfo.InvariantCulture),
            value.Height.ToString(CultureInfo.InvariantCulture), value.StrideBytes.ToString(CultureInfo.InvariantCulture),
            value.ValidRowBytes.ToString(CultureInfo.InvariantCulture), value.RequiredBufferLength.ToString(CultureInfo.InvariantCulture),
            value.PixelFormat.ToString(), value.ValidBits?.ToString(CultureInfo.InvariantCulture),
            value.HostCaptureUtc.ToString("O", CultureInfo.InvariantCulture),
            CameraFingerprint(value.EffectiveCameraConfiguration)
        });
    private static string? FrameFingerprint(FrameProvenance? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "manual-frame-provenance-v2", value.Correlation.Kind.ToString(), value.Correlation.Value.ToString("D"),
            value.ProviderId, value.ProviderVersion, value.AdapterId, value.AdapterVersion,
            value.SdkId, value.SdkVersion, value.NativeRuntimeVersion, value.StableDeviceIdentity,
            value.ReportedModel, value.FirmwareVersion, value.NativePixelFormatDescription,
            value.NormalizationDetails, value.NormalizationAllocated ? "1" : "0",
            value.NormalizationTransformed ? "1" : "0", value.FrameCounter?.ToString(CultureInfo.InvariantCulture),
            DeviceTimestampFingerprint(value.DeviceTimestamp), MilestonesFingerprint(value.Milestones),
            value.PoolCopyEvidence is null ? null : AlgorithmContractValidation.HashParts(new string?[]
            {
                "manual-pool-copy-v1", value.PoolCopyEvidence.SourceStrideBytes.ToString(CultureInfo.InvariantCulture),
                value.PoolCopyEvidence.DestinationStrideBytes.ToString(CultureInfo.InvariantCulture),
                value.PoolCopyEvidence.InputNormalizationTransformed ? "1" : "0",
                value.PoolCopyEvidence.IdentityPixelCopy ? "1" : "0", value.PoolCopyEvidence.PaddingZeroed ? "1" : "0"
            })
        });

    private static string CameraFingerprint(EffectiveCameraConfiguration value) =>
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "manual-effective-camera-v1", value.ProductionAcquisitionMode.ToString(),
            value.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture),
            value.GainDb.ToString("R", CultureInfo.InvariantCulture),
            value.RegionOfInterest.OffsetX.ToString(CultureInfo.InvariantCulture),
            value.RegionOfInterest.OffsetY.ToString(CultureInfo.InvariantCulture),
            value.RegionOfInterest.Width.ToString(CultureInfo.InvariantCulture),
            value.RegionOfInterest.Height.ToString(CultureInfo.InvariantCulture), value.PixelFormat.ToString(),
            value.ValidBits?.ToString(CultureInfo.InvariantCulture),
            value.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture),
            value.TriggerDelayUs.ToString("R", CultureInfo.InvariantCulture),
            value.WhiteBalanceRgb?.Red.ToString("R", CultureInfo.InvariantCulture),
            value.WhiteBalanceRgb?.Green.ToString("R", CultureInfo.InvariantCulture),
            value.WhiteBalanceRgb?.Blue.ToString("R", CultureInfo.InvariantCulture)
        });

    private static string? DeviceTimestampFingerprint(DeviceTimestamp? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[] { "manual-device-time-v1",
            value.Value.ToString(CultureInfo.InvariantCulture), value.TickFrequency?.ToString(CultureInfo.InvariantCulture),
            value.Unit, value.ClockDomain, value.CounterRollover?.ToString(CultureInfo.InvariantCulture),
            value.Synchronization.ToString() });

    private static string MilestonesFingerprint(FrameAcquisitionMilestones value) =>
        AlgorithmContractValidation.HashParts(new string?[] { "manual-milestones-v1",
            value.MonotonicFrequency.ToString(CultureInfo.InvariantCulture), TimePoint(value.TriggerAccepted),
            TimePoint(value.AcquisitionStarted), TimePoint(value.NativeFrameReceived),
            TimePoint(value.NormalizedFrameReady) });

    private static string? TimePoint(FrameTimePoint? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[] { "manual-frame-time-v1",
            value.HostObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture) });

    private static string? ResultProjectionFingerprint(AlgorithmResult? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[] { "manual-result-projection-v1",
            value.Decision.ToString(), value.ReasonCode, value.Measurements.Count.ToString(CultureInfo.InvariantCulture),
            string.Join("|", value.Measurements.Select(item => item.Key + ":" + item.Unit + ":" + item.Value)),
            value.OverlaySet.ContractId, value.OverlaySet.ContractVersion });

    private static string? TimingFingerprint(AlgorithmExecutionTimingSnapshot? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[] { "manual-timing-v1", value.Recipe.ContentHash,
            value.PolicyId, value.PolicyVersion, value.PolicyContentHash,
            value.AlgorithmExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            value.CancellationGracePeriod.Ticks.ToString(CultureInfo.InvariantCulture) });
}

public sealed record ManualInspectionHistoryFilter(Guid? SessionId = null, Guid? RunId = null,
    long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);

public sealed record ManualInspectionHistoryReadResult(bool Available, string ReasonCode,
    ManualInspectionSessionHeader? Header = null, ManualInspectionRunRecord? LatestRun = null,
    bool RecoveryRequired = false);

public sealed record ManualInspectionHistoryPage
{
    public ManualInspectionHistoryPage(bool available, string reasonCode,
        IEnumerable<ManualInspectionSessionEvent>? events,
        IEnumerable<ManualInspectionRunRecord>? runs, long throughPosition,
        long? nextAfterPosition, ManualInspectionSessionHeader? pendingHeader = null,
        bool recoveryRequired = false)
    {
        Available = available;
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<ManualInspectionSessionEvent>(
            (events ?? Array.Empty<ManualInspectionSessionEvent>()).ToArray());
        Runs = new ReadOnlyCollection<ManualInspectionRunRecord>(
            (runs ?? Array.Empty<ManualInspectionRunRecord>()).ToArray());
        if (throughPosition < 0 || nextAfterPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        PendingHeader = pendingHeader;
        RecoveryRequired = recoveryRequired;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<ManualInspectionSessionEvent> Events { get; }
    public ReadOnlyCollection<ManualInspectionRunRecord> Runs { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public ManualInspectionSessionHeader? PendingHeader { get; }
    public bool RecoveryRequired { get; }
}

/// <summary>Bounded read-only projection of the independent schema-21 Manual ledger.</summary>
public interface IManualInspectionHistoryQuery
{
    ValueTask<ManualInspectionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<ManualInspectionHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default);
    ValueTask<ManualInspectionHistoryPage> QueryAsync(ManualInspectionHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
