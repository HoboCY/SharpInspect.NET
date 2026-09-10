using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable header for one admitted, non-production station qualification
/// session.  The header is deliberately independent from the session phase:
/// every event in the ledger points to this exact value.
/// </summary>
public sealed record StationQualificationSessionHeader
{
    internal StationQualificationSessionHeader(Guid sessionId, Guid runtimeEpoch, Guid leaseNonce,
        Guid startCorrelationId, Guid startAttemptId, StationQualificationPlan plan,
        RecipeActivationRecord targetBaseline, Guid actorPrincipalId, Guid actorSessionId,
        long actorAuthorizationRevision, RecipeContractReference authorizationPolicy,
        Guid? stepUpGrantId, string authorizationTarget, string changeReason,
        DateTimeOffset startedAtUtc)
    {
        if (sessionId == Guid.Empty || runtimeEpoch == Guid.Empty || leaseNonce == Guid.Empty ||
            startCorrelationId == Guid.Empty || startAttemptId == Guid.Empty)
            throw new ArgumentException("StationQualificationHeaderIdentityInvalid");
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        TargetBaseline = targetBaseline ?? throw new ArgumentNullException(nameof(targetBaseline));
        if (!TargetBaseline.Outcome.Succeeded ||
            TargetBaseline.Reference != Plan.TargetActivation)
            throw new ArgumentException("StationQualificationTargetBaselineInvalid", nameof(targetBaseline));
        if (actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            actorAuthorizationRevision < 0)
            throw new ArgumentException("StationQualificationActorInvalid");
        AuthorizationPolicy = authorizationPolicy ??
            throw new ArgumentNullException(nameof(authorizationPolicy));
        // An admitted session always carries the exact grant that authorized the
        // Start command. A policy that does not require step-up still records a
        // runtime-issued grant id so recovery can bind the durable responsibility
        // to its original decision.
        if (stepUpGrantId is not Guid grant || grant == Guid.Empty)
            throw new ArgumentException("StationQualificationStepUpRequired", nameof(stepUpGrantId));
        AuthorizationTarget = QualificationContractValidation.Hash(authorizationTarget,
            nameof(authorizationTarget));
        ChangeReason = QualificationContractValidation.Reason(changeReason, nameof(changeReason));
        if (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("StationQualificationTimestampInvalid", nameof(startedAtUtc));

        SessionId = sessionId;
        RuntimeEpoch = runtimeEpoch;
        LeaseNonce = leaseNonce;
        StartCorrelationId = startCorrelationId;
        StartAttemptId = startAttemptId;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision;
        StepUpGrantId = stepUpGrantId;
        StartedAtUtc = startedAtUtc;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-station-qualification-session-header-v1",
            sessionId.ToString("D"), runtimeEpoch.ToString("D"), leaseNonce.ToString("D"),
            startCorrelationId.ToString("D"), startAttemptId.ToString("D"), Plan.ContentHash,
            TargetBaseline.ContentHash, actorPrincipalId.ToString("D"), actorSessionId.ToString("D"),
            actorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            AuthorizationPolicy.Id, AuthorizationPolicy.Version, AuthorizationPolicy.ContentHash,
            stepUpGrantId?.ToString("D"), AuthorizationTarget, ChangeReason,
            startedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public Guid SessionId { get; }
    public Guid RuntimeEpoch { get; }
    public Guid LeaseNonce { get; }
    public Guid StartCorrelationId { get; }
    public Guid StartAttemptId { get; }
    public StationQualificationPlan Plan { get; }
    public RecipeActivationRecord TargetBaseline { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public Guid? StepUpGrantId { get; }
    public string AuthorizationTarget { get; }
    public string ChangeReason { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable identity for one normal-exit or post-restart restoration attempt.</summary>
public sealed record StationQualificationRecoveryAttempt
{
    internal StationQualificationRecoveryAttempt(Guid runtimeEpoch, Guid leaseNonce)
    {
        if (runtimeEpoch == Guid.Empty || leaseNonce == Guid.Empty)
            throw new ArgumentException("StationQualificationRecoveryAttemptIdentityInvalid");
        RuntimeEpoch = runtimeEpoch;
        LeaseNonce = leaseNonce;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-station-qualification-recovery-attempt-v1",
            runtimeEpoch.ToString("D"), leaseNonce.ToString("D")
        });
    }

    public Guid RuntimeEpoch { get; }
    public Guid LeaseNonce { get; }
    public string ContentHash { get; }
}

/// <summary>One immutable qualification phase or terminal event.</summary>
public sealed record StationQualificationSessionEvent
{
    internal StationQualificationSessionEvent(long position, string? previousHash,
        StationQualificationSessionHeader header, Guid commandCorrelationId, Guid attemptId,
        AuditedCommandKind commandKind, StationQualificationSessionPhase phase,
        StationQualificationRestorationState restoration, string reasonCode, bool terminal,
        DateTimeOffset recordedAtUtc, QualificationFacilityObservation? observation = null,
        StationQualificationRunRecord? run = null, long? commandAuditSequence = null,
        string? commandAuditHash = null, long? authorizationAuditSequence = null,
        string? authorizationAuditHash = null, long auditSequence = 0, string? auditHash = null,
        StationQualificationRecoveryAttempt? recoveryAttempt = null,
        string? commandAuthorizationTarget = null)
    {
        if (position < 1 || commandCorrelationId == Guid.Empty || attemptId == Guid.Empty)
            throw new ArgumentException("StationQualificationEventIdentityInvalid");
        Header = header ?? throw new ArgumentNullException(nameof(header));
        if (!Enum.IsDefined(commandKind) || commandKind is not
                (AuditedCommandKind.StartStationQualificationSession or
                 AuditedCommandKind.ExitStationQualificationSession) ||
            !Enum.IsDefined(phase) || !Enum.IsDefined(restoration))
            throw new ArgumentException("StationQualificationEventValueInvalid");
        if (previousHash is not null)
            previousHash = QualificationContractValidation.Hash(previousHash, nameof(previousHash));
        if (terminal && (phase is not StationQualificationSessionPhase.Closed ||
            restoration is not StationQualificationRestorationState.Restored))
            throw new ArgumentException("StationQualificationTerminalPhaseInvalid", nameof(phase));
        if (!terminal && phase is StationQualificationSessionPhase.Closed)
            throw new ArgumentException("StationQualificationTerminalFlagInvalid", nameof(terminal));
        if (recoveryAttempt is not null &&
            (recoveryAttempt.RuntimeEpoch == Guid.Empty || recoveryAttempt.LeaseNonce == Guid.Empty))
            throw new ArgumentException("StationQualificationRecoveryAttemptInvalid",
                nameof(recoveryAttempt));
        if (phase is not (StationQualificationSessionPhase.Restoring or
            StationQualificationSessionPhase.RecoveryBlocked or StationQualificationSessionPhase.Closed) &&
            recoveryAttempt is not null)
            throw new ArgumentException("StationQualificationRecoveryAttemptUnexpected",
                nameof(recoveryAttempt));
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("StationQualificationTimestampInvalid", nameof(recordedAtUtc));
        if (observation is not null && (observation.SessionId != Header.SessionId ||
            observation.LeaseNonce != (recoveryAttempt?.LeaseNonce ?? Header.LeaseNonce) ||
            observation.RuntimeEpoch != (recoveryAttempt?.RuntimeEpoch ?? Header.RuntimeEpoch) ||
            observation.Identity != Header.Plan.QualificationHarnessIdentity))
            throw new ArgumentException("StationQualificationObservationBindingMismatch", nameof(observation));
        if (run is not null && (run.SessionId != Header.SessionId ||
            run.RunId.Correlation.Kind != ExecutionKind.Qualification ||
            run.ContextHash != Header.Plan.QualificationContextHash))
            throw new ArgumentException("StationQualificationRunBindingMismatch", nameof(run));
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("StationQualificationAuditReferenceInvalid");
        if (auditSequence > 0 && auditHash is null)
            throw new ArgumentException("StationQualificationAuditReferenceInvalid");
        if ((commandAuditSequence.HasValue != (commandAuditHash is not null)) ||
            (authorizationAuditSequence.HasValue != (authorizationAuditHash is not null)) ||
            commandAuditSequence is < 1 || authorizationAuditSequence is < 1)
            throw new ArgumentException("StationQualificationAuditReferenceInvalid");

        Position = position;
        PreviousHash = previousHash;
        CommandCorrelationId = commandCorrelationId;
        AttemptId = attemptId;
        CommandKind = commandKind;
        CommandAuthorizationTarget = QualificationContractValidation.Hash(
            commandAuthorizationTarget ?? Header.AuthorizationTarget,
            nameof(commandAuthorizationTarget));
        Phase = phase;
        Restoration = restoration;
        Terminal = terminal;
        RecordedAtUtc = recordedAtUtc;
        Observation = observation;
        Run = run;
        RecoveryAttempt = recoveryAttempt;
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = commandAuditHash is null ? null :
            QualificationContractValidation.Hash(commandAuditHash, nameof(commandAuditHash));
        AuthorizationAuditSequence = authorizationAuditSequence;
        AuthorizationAuditHash = authorizationAuditHash is null ? null :
            QualificationContractValidation.Hash(authorizationAuditHash, nameof(authorizationAuditHash));
        AuditSequence = auditSequence;
        AuditHash = auditHash is null ? null : QualificationContractValidation.Hash(auditHash, nameof(auditHash));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-station-qualification-session-event-v2",
            position.ToString(CultureInfo.InvariantCulture), previousHash, Header.ContentHash,
            commandCorrelationId.ToString("D"), attemptId.ToString("D"), commandKind.ToString(),
            CommandAuthorizationTarget,
            phase.ToString(), restoration.ToString(), ReasonCode, terminal ? "1" : "0",
            recordedAtUtc.ToString("O", CultureInfo.InvariantCulture), observation?.ContentHash,
            run?.ContentHash, recoveryAttempt?.ContentHash
        });
    }

    public long Position { get; }
    public string? PreviousHash { get; }
    public StationQualificationSessionHeader Header { get; }
    public Guid SessionId => Header.SessionId;
    public Guid RuntimeEpoch => Header.RuntimeEpoch;
    public Guid CommandCorrelationId { get; }
    public Guid AttemptId { get; }
    public AuditedCommandKind CommandKind { get; }
    /// <summary>
    /// Exact target bound to the command authorization for this event.  Start
    /// and recovery events normally equal the header target; an Exit event has
    /// its own target and must not be reduced to the session admission target.
    /// </summary>
    public string CommandAuthorizationTarget { get; }
    public StationQualificationSessionPhase Phase { get; }
    public StationQualificationRestorationState Restoration { get; }
    public string ReasonCode { get; }
    public bool Terminal { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public QualificationFacilityObservation? Observation { get; }
    public StationQualificationRunRecord? Run { get; }
    public StationQualificationRecoveryAttempt? RecoveryAttempt { get; }
    public long? CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long? AuthorizationAuditSequence { get; }
    public string? AuthorizationAuditHash { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable typed run evidence from the isolated qualification facility.</summary>
public sealed record StationQualificationRunRecord
{
    internal StationQualificationRunRecord(long position, QualificationRunId runId, Guid sessionId,
        long stimulusSequence, string scenarioId, string contextHash, uint controllerEpoch,
        uint cycleSequence, DateTimeOffset admittedAtUtc, DateTimeOffset? completedAtUtc,
        ExecutionStatus? executionStatus, InspectionDecision decision, string reasonCode,
        FrameMetadata? frameMetadata, FrameProvenance? frameProvenance, string? resultPayloadJson,
        string? resultPayloadHash, StationQualificationPayload? qualificationPayload,
        AlgorithmExecutionTimingSnapshot? timing)
    {
        if (position < 1 || runId is null || runId.Value == Guid.Empty || sessionId == Guid.Empty ||
            stimulusSequence < 1)
            throw new ArgumentException("StationQualificationRunIdentityInvalid");
        if (!Enum.IsDefined(decision) ||
            (executionStatus is { } status && !Enum.IsDefined(status)))
            throw new ArgumentException("StationQualificationRunValueInvalid");
        ScenarioId = QualificationContractValidation.Identifier(scenarioId, nameof(scenarioId));
        ContextHash = QualificationContractValidation.Hash(contextHash, nameof(contextHash));
        if (admittedAtUtc == default || admittedAtUtc.Offset != TimeSpan.Zero ||
            (completedAtUtc is { } completed && (completed == default || completed.Offset != TimeSpan.Zero)))
            throw new ArgumentException("StationQualificationRunTimestampInvalid");
        if (completedAtUtc is { } completedAt && completedAt < admittedAtUtc)
            throw new ArgumentException("StationQualificationRunTimestampOrderInvalid");
        if (executionStatus is null && (completedAtUtc is not null || decision != InspectionDecision.Unknown ||
            resultPayloadJson is not null || resultPayloadHash is not null || qualificationPayload is not null))
            throw new ArgumentException("StationQualificationRunInFlightValueInvalid");
        if (executionStatus is not null && completedAtUtc is null)
            throw new ArgumentException("StationQualificationRunCompletionTimestampRequired");
        if (resultPayloadJson is null != (resultPayloadHash is null))
            throw new ArgumentException("StationQualificationRunPayloadHashMismatch");
        if (resultPayloadJson is not null)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(resultPayloadJson);
            if (bytes.Length is < 1 or > 1024 * 1024)
                throw new ArgumentException("StationQualificationRunPayloadCapacityExceeded", nameof(resultPayloadJson));
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, resultPayloadHash, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("StationQualificationRunPayloadHashMismatch", nameof(resultPayloadHash));
            resultPayloadHash = actualHash;
        }
        if (qualificationPayload is not null &&
            (qualificationPayload.SessionId != sessionId || qualificationPayload.RunId.Value != runId.Value ||
             qualificationPayload.ContextHash != ContextHash))
            throw new ArgumentException("StationQualificationPayloadBindingMismatch", nameof(qualificationPayload));
        if (frameMetadata is not null && (frameMetadata.Correlation.Kind != ExecutionKind.Qualification ||
            frameMetadata.Correlation.Value != runId.Value))
            throw new ArgumentException("StationQualificationFrameCorrelationInvalid", nameof(frameMetadata));
        if (frameProvenance is not null && (frameProvenance.Correlation.Kind != ExecutionKind.Qualification ||
            frameProvenance.Correlation.Value != runId.Value))
            throw new ArgumentException("StationQualificationFrameCorrelationInvalid", nameof(frameProvenance));

        Position = position;
        RunId = runId;
        SessionId = sessionId;
        StimulusSequence = stimulusSequence;
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        AdmittedAtUtc = admittedAtUtc;
        CompletedAtUtc = completedAtUtc;
        ExecutionStatus = executionStatus;
        Decision = decision;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        FrameMetadata = frameMetadata;
        FrameProvenance = frameProvenance;
        ResultPayloadJson = resultPayloadJson;
        ResultPayloadHash = resultPayloadHash is null ? null :
            QualificationContractValidation.Hash(resultPayloadHash, nameof(resultPayloadHash));
        QualificationPayload = qualificationPayload;
        Timing = timing;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-station-qualification-run-v1", position.ToString(CultureInfo.InvariantCulture),
            runId.Value.ToString("D"), sessionId.ToString("D"), stimulusSequence.ToString(CultureInfo.InvariantCulture),
            ScenarioId, ContextHash, controllerEpoch.ToString(CultureInfo.InvariantCulture),
            cycleSequence.ToString(CultureInfo.InvariantCulture), admittedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            completedAtUtc?.ToString("O", CultureInfo.InvariantCulture), executionStatus?.ToString(),
            decision.ToString(), ReasonCode, FrameFingerprint(frameMetadata),
            ProvenanceFingerprint(frameProvenance), ResultPayloadHash, qualificationPayload?.ContentHash,
            TimingFingerprint(Timing)
        });
    }

    public long Position { get; }
    public QualificationRunId RunId { get; }
    public Guid SessionId { get; }
    public long StimulusSequence { get; }
    public string ScenarioId { get; }
    public string ContextHash { get; }
    public uint ControllerEpoch { get; }
    public uint CycleSequence { get; }
    public DateTimeOffset AdmittedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public ExecutionStatus? ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public string ReasonCode { get; }
    public FrameMetadata? FrameMetadata { get; }
    public FrameProvenance? FrameProvenance { get; }
    public string? ResultPayloadJson { get; }
    public string? ResultPayloadHash { get; }
    public StationQualificationPayload? QualificationPayload { get; }
    public StationQualificationPayload? Payload => QualificationPayload;
    public AlgorithmExecutionTimingSnapshot? Timing { get; }
    public bool Completed => CompletedAtUtc.HasValue;
    public bool Terminal => Completed;
    public string ContentHash { get; }

    private static string? FrameFingerprint(FrameMetadata? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "station-qualification-frame-v2", value.Correlation.Kind.ToString(),
            value.Correlation.Value.ToString("D"), value.LogicalCameraRole,
            value.Width.ToString(CultureInfo.InvariantCulture), value.Height.ToString(CultureInfo.InvariantCulture),
            value.StrideBytes.ToString(CultureInfo.InvariantCulture),
            value.ValidRowBytes.ToString(CultureInfo.InvariantCulture),
            value.RequiredBufferLength.ToString(CultureInfo.InvariantCulture),
            value.FullBufferLayoutLength.ToString(CultureInfo.InvariantCulture), value.PixelFormat.ToString(),
            value.ValidBits?.ToString(CultureInfo.InvariantCulture), value.HostCaptureUtc.ToString("O", CultureInfo.InvariantCulture),
            CameraConfigurationFingerprint(value.EffectiveCameraConfiguration)
        });

    private static string? ProvenanceFingerprint(FrameProvenance? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "station-qualification-provenance-v2", value.Correlation.Kind.ToString(),
            value.Correlation.Value.ToString("D"), value.ProviderId, value.ProviderVersion,
            value.AdapterId, value.AdapterVersion, value.SdkId, value.SdkVersion,
            value.NativeRuntimeVersion, value.StableDeviceIdentity, value.ReportedModel,
            value.FirmwareVersion, value.NativePixelFormatDescription, value.NormalizationDetails,
            value.NormalizationAllocated ? "1" : "0", value.NormalizationTransformed ? "1" : "0",
            value.FrameCounter?.ToString(CultureInfo.InvariantCulture),
            DeviceTimestampFingerprint(value.DeviceTimestamp), MilestonesFingerprint(value.Milestones),
            value.PoolCopyEvidence is null ? null : AlgorithmContractValidation.HashParts(new string?[]
            {
                "station-qualification-pool-copy-v1",
                value.PoolCopyEvidence.SourceStrideBytes.ToString(CultureInfo.InvariantCulture),
                value.PoolCopyEvidence.DestinationStrideBytes.ToString(CultureInfo.InvariantCulture),
                value.PoolCopyEvidence.InputNormalizationTransformed ? "1" : "0",
                value.PoolCopyEvidence.IdentityPixelCopy ? "1" : "0",
                value.PoolCopyEvidence.PaddingZeroed ? "1" : "0"
            })
        });

    private static string CameraConfigurationFingerprint(EffectiveCameraConfiguration value) =>
        AlgorithmContractValidation.HashParts(new string?[]
        {
            "station-qualification-effective-camera-v1", value.ProductionAcquisitionMode.ToString(),
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
        AlgorithmContractValidation.HashParts(new string?[] { "station-qualification-device-time-v1",
            value.Value.ToString(CultureInfo.InvariantCulture),
            value.TickFrequency?.ToString(CultureInfo.InvariantCulture), value.Unit, value.ClockDomain,
            value.CounterRollover?.ToString(CultureInfo.InvariantCulture), value.Synchronization.ToString() });

    private static string MilestonesFingerprint(FrameAcquisitionMilestones value) =>
        AlgorithmContractValidation.HashParts(new string?[] { "station-qualification-milestones-v1",
            value.MonotonicFrequency.ToString(CultureInfo.InvariantCulture), TimePoint(value.TriggerAccepted),
            TimePoint(value.AcquisitionStarted), TimePoint(value.NativeFrameReceived),
            TimePoint(value.NormalizedFrameReady) });

    private static string? TimePoint(FrameTimePoint? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[] { "station-qualification-timepoint-v1",
            value.HostObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture) });

    private static string? TimingFingerprint(AlgorithmExecutionTimingSnapshot? value) => value is null ? null :
        AlgorithmContractValidation.HashParts(new string?[] { "station-qualification-timing-v1",
            value.Recipe.Id, value.Recipe.Version, value.Recipe.ContentHash, value.PolicyId,
            value.PolicyVersion, value.PolicyContentHash,
            value.AlgorithmExecutionTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            value.CancellationGracePeriod.Ticks.ToString(CultureInfo.InvariantCulture) });
}

public sealed record StationQualificationHistoryFilter(Guid? SessionId = null, Guid? RunId = null,
    long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);

public sealed record StationQualificationHistoryReadResult(bool Available, string ReasonCode,
    StationQualificationSessionHeader? Header = null,
    StationQualificationSessionEvent? LastEvent = null,
    StationQualificationRunRecord? LatestRun = null, bool RecoveryRequired = false);

public sealed record StationQualificationHistoryPage
{
    public StationQualificationHistoryPage(bool available, string reasonCode,
        IEnumerable<StationQualificationSessionEvent>? events,
        IEnumerable<StationQualificationRunRecord>? runs, long throughPosition,
        long? nextAfterPosition, StationQualificationSessionHeader? pendingHeader = null,
        bool recoveryRequired = false)
    {
        Available = available;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<StationQualificationSessionEvent>(
            (events ?? Array.Empty<StationQualificationSessionEvent>()).ToArray());
        Runs = new ReadOnlyCollection<StationQualificationRunRecord>(
            (runs ?? Array.Empty<StationQualificationRunRecord>()).ToArray());
        if (throughPosition < 0 || nextAfterPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        PendingHeader = pendingHeader;
        RecoveryRequired = recoveryRequired;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<StationQualificationSessionEvent> Events { get; }
    public ReadOnlyCollection<StationQualificationRunRecord> Runs { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public StationQualificationSessionHeader? PendingHeader { get; }
    public bool RecoveryRequired { get; }
}

/// <summary>Bounded read-only projection of the schema-23 qualification ledger.</summary>
public interface IStationQualificationHistoryQuery
{
    ValueTask<StationQualificationHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<StationQualificationHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default);
    ValueTask<StationQualificationHistoryPage> QueryAsync(StationQualificationHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
