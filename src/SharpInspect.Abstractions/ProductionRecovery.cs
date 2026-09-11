using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Disposition recorded by an operator after a production recovery.</summary>
public enum PartDisposition : byte
{
    Isolated = 1,
    Reworked = 2,
    Scrapped = 3,
    ManuallyAccepted = 4
}

/// <summary>Last durable production delivery phase known to the recovery writer.</summary>
public enum ProductionRecoveryDeliveryPhase : byte
{
    Unknown = 0,
    Admitted = 1,
    CoreCommitted = 2,
    PublicationPrepared = 3,
    ResultValidRaised = 4,
    ResultAcknowledged = 5,
    ResultValidCleared = 6,
    FaultTerminated = 7,
    RecoveryRequired = 8
}

/// <summary>Why the final delivery state cannot be treated as a normal result.</summary>
public enum ProductionRecoveryUncertaintyKind : byte
{
    Unknown = 0,
    ResultAcknowledgementTimeout = 1,
    CommunicationLost = 2,
    ProcessRestart = 3,
    ControllerEpochChanged = 4,
    AuditUnavailable = 5,
    PhysicalStopRequired = 6
}

/// <summary>Observed PLC result acknowledgement state at recovery admission.</summary>
public enum ProductionRecoveryAcknowledgementObservation : byte
{
    Unknown = 0,
    Low = 1,
    High = 2
}

public enum ProductionRecoveryOutcome : byte
{
    Pending = 1,
    Completed = 2,
    Blocked = 3
}

/// <summary>
/// An operator recovery request.  Its target is derived from every mutable input;
/// callers cannot supply a target for a different inspection or disposition.
/// </summary>
public sealed record ManualProductionRecoveryCommand : RuntimeCommand
{
    public ManualProductionRecoveryCommand(Guid correlationId, CommandInvocation invocation,
        Guid inspectionId, string expectedEventHash, string reasonCode, PartDisposition disposition,
        string? dispositionNote = null) : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty || inspectionId == Guid.Empty)
            throw new ArgumentException("ProductionRecoveryIdentityInvalid");
        ArgumentNullException.ThrowIfNull(invocation);
        if (!Enum.IsDefined(typeof(PartDisposition), disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        InspectionId = inspectionId;
        ExpectedEventHash = RecipeActivationValidation.Hash(expectedEventHash,
            nameof(expectedEventHash));
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        Disposition = disposition;
        DispositionNote = dispositionNote is null ? null :
            AlgorithmContractValidation.BoundedText(dispositionNote, nameof(dispositionNote), 512);
    }

    public Guid InspectionId { get; }
    public string ExpectedEventHash { get; }
    public string ReasonCode { get; }
    public PartDisposition Disposition { get; }
    public string? DispositionNote { get; }
    /// <summary>
    /// Computes from the current record correlation.  RuntimeCommand is a
    /// record, so callers may create a retry with <c>with</c>; keeping this
    /// expression derived prevents the authorization target from retaining
    /// the constructor correlation id.
    /// </summary>
    public string AuthorizationTarget => ComputeAuthorizationTarget(CorrelationId,
        InspectionId, ExpectedEventHash, ReasonCode, Disposition, DispositionNote);

    public static string ComputeAuthorizationTarget(Guid correlationId, Guid inspectionId,
        string expectedEventHash, string reasonCode, PartDisposition disposition,
        string? dispositionNote) => AlgorithmContractValidation.HashParts(new string?[]
    {
        "sharpinspect-production-manual-recovery-target-v1", correlationId.ToString("D"),
        inspectionId.ToString("D"), RecipeActivationValidation.Hash(expectedEventHash,
            nameof(expectedEventHash)), RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode)),
        disposition.ToString(), dispositionNote
    });
}

/// <summary>
/// Runtime observation captured before the recovery authorization transaction.
/// The PLC/core hashes are references, never replacement payloads.
/// </summary>
public sealed class ProductionRecoveryObservation
{
    public ProductionRecoveryObservation(ProductionRecoveryDeliveryPhase deliveryPhase,
        ProductionRecoveryUncertaintyKind uncertainty,
        ProductionRecoveryAcknowledgementObservation acknowledgement,
        Guid runtimeEpoch, uint controllerEpoch, uint cycleSequence,
        long connectionGeneration, string endpointBindingHash, string plcProfileHash,
        string plcPolicyHash, string? coreContentHash, string? payloadContentHash,
        string? payloadWireContentHash)
    {
        if (!Enum.IsDefined(typeof(ProductionRecoveryDeliveryPhase), deliveryPhase) ||
            !Enum.IsDefined(typeof(ProductionRecoveryUncertaintyKind), uncertainty) ||
            !Enum.IsDefined(typeof(ProductionRecoveryAcknowledgementObservation), acknowledgement))
            throw new ArgumentOutOfRangeException(nameof(deliveryPhase));
        if (runtimeEpoch == Guid.Empty || connectionGeneration < 0)
            throw new ArgumentException("ProductionRecoveryObservationIdentityInvalid");
        DeliveryPhase = deliveryPhase;
        Uncertainty = uncertainty;
        Acknowledgement = acknowledgement;
        RuntimeEpoch = runtimeEpoch;
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        ConnectionGeneration = connectionGeneration;
        EndpointBindingHash = RecipeActivationValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        PlcProfileHash = RecipeActivationValidation.Hash(plcProfileHash, nameof(plcProfileHash));
        PlcPolicyHash = RecipeActivationValidation.Hash(plcPolicyHash, nameof(plcPolicyHash));
        CoreContentHash = OptionalHash(coreContentHash, nameof(coreContentHash));
        PayloadContentHash = OptionalHash(payloadContentHash, nameof(payloadContentHash));
        PayloadWireContentHash = OptionalHash(payloadWireContentHash, nameof(payloadWireContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-observation-v1", deliveryPhase.ToString(),
            uncertainty.ToString(), acknowledgement.ToString(), runtimeEpoch.ToString("D"),
            controllerEpoch.ToString(CultureInfo.InvariantCulture), cycleSequence.ToString(CultureInfo.InvariantCulture),
            connectionGeneration.ToString(CultureInfo.InvariantCulture), EndpointBindingHash,
            PlcProfileHash, PlcPolicyHash, CoreContentHash, PayloadContentHash, PayloadWireContentHash
        });
    }

    public ProductionRecoveryDeliveryPhase DeliveryPhase { get; }
    public ProductionRecoveryUncertaintyKind Uncertainty { get; }
    public ProductionRecoveryAcknowledgementObservation Acknowledgement { get; }
    public Guid RuntimeEpoch { get; }
    public uint ControllerEpoch { get; }
    public uint CycleSequence { get; }
    public long ConnectionGeneration { get; }
    public string EndpointBindingHash { get; }
    public string PlcProfileHash { get; }
    public string PlcPolicyHash { get; }
    public string? CoreContentHash { get; }
    public string? PayloadContentHash { get; }
    public string? PayloadWireContentHash { get; }
    public string ContentHash { get; }

    private static string? OptionalHash(string? value, string parameterName) => value is null ? null :
        RecipeActivationValidation.Hash(value, parameterName);
}

/// <summary>Independent physical safe-stop evidence supplied by the safety provider.</summary>
public sealed class ProductionRecoverySafetyEvidence
{
    public ProductionRecoverySafetyEvidence(ProductionRecoverySafetyObservation observation)
    {
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        if (!observation.IsSafeLineStopped)
            throw new ArgumentException("ProductionRecoverySafetyStopRequired", nameof(observation));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-safety-evidence-v1", observation.ContentHash,
            observation.Binding.ContentHash, observation.SourceEpoch.ToString("D"),
            observation.SourceGeneration.ToString(CultureInfo.InvariantCulture)
        });
    }

    public ProductionRecoverySafetyObservation Observation { get; }
    public string ContentHash { get; }
}

public sealed class ProductionRecoveryCleanupReceipt
{
    public ProductionRecoveryCleanupReceipt(Guid runtimeEpoch, long connectionGeneration,
        DateTimeOffset completedAtUtc, long monotonicTimestamp, bool cleanupCompleted,
        string reasonCode, string endpointBindingHash, string plcProfileHash,
        string plcPolicyHash, uint controllerEpoch, PlcCommunicationHealth health,
        bool triggerLowObserved, bool ackLowObserved, bool runtimeOutputsClear)
    {
        if (runtimeEpoch == Guid.Empty || connectionGeneration < 0 ||
            completedAtUtc == default || completedAtUtc.Offset != TimeSpan.Zero ||
            monotonicTimestamp <= 0)
            throw new ArgumentException("ProductionRecoveryCleanupReceiptInvalid");
        ArgumentNullException.ThrowIfNull(health);
        if (controllerEpoch == 0 || !health.Healthy || health.ControllerEpoch != controllerEpoch ||
            health.RuntimeEpoch != runtimeEpoch || health.ConnectionGeneration != connectionGeneration ||
            !triggerLowObserved || !ackLowObserved || !runtimeOutputsClear)
            throw new ArgumentException("ProductionRecoveryCleanupEvidenceInvalid");
        RuntimeEpoch = runtimeEpoch;
        ConnectionGeneration = connectionGeneration;
        CompletedAtUtc = completedAtUtc;
        MonotonicTimestamp = monotonicTimestamp;
        CleanupCompleted = cleanupCompleted;
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        EndpointBindingHash = RecipeActivationValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        PlcProfileHash = RecipeActivationValidation.Hash(plcProfileHash, nameof(plcProfileHash));
        PlcPolicyHash = RecipeActivationValidation.Hash(plcPolicyHash, nameof(plcPolicyHash));
        ControllerEpoch = controllerEpoch;
        Health = health;
        TriggerLowObserved = triggerLowObserved;
        AckLowObserved = ackLowObserved;
        RuntimeOutputsClear = runtimeOutputsClear;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-cleanup-v2", runtimeEpoch.ToString("D"),
            connectionGeneration.ToString(CultureInfo.InvariantCulture),
            completedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            monotonicTimestamp.ToString(CultureInfo.InvariantCulture), cleanupCompleted ? "1" : "0",
            ReasonCode, EndpointBindingHash, PlcProfileHash, PlcPolicyHash,
            controllerEpoch.ToString(CultureInfo.InvariantCulture), health.PolicyHash,
            health.RuntimeEpoch.ToString("D"), health.ConnectionGeneration.ToString(CultureInfo.InvariantCulture),
            health.Healthy ? "1" : "0", triggerLowObserved ? "1" : "0",
            ackLowObserved ? "1" : "0",
            runtimeOutputsClear ? "1" : "0"
        });
    }

    public Guid RuntimeEpoch { get; }
    public long ConnectionGeneration { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public bool CleanupCompleted { get; }
    public string ReasonCode { get; }
    public string EndpointBindingHash { get; }
    public string PlcProfileHash { get; }
    public string PlcPolicyHash { get; }
    public uint ControllerEpoch { get; }
    public PlcCommunicationHealth Health { get; }
    public bool TriggerLowObserved { get; }
    public bool AckLowObserved { get; }
    public bool RuntimeOutputsClear { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable recovery metadata attached to RecoveryRequired/Completed.</summary>
public sealed class ProductionRecoveryRecord
{
    internal ProductionRecoveryRecord(Guid recoveryAttemptId, Guid recoveryRuntimeEpoch, Guid inspectionId,
        long previousEventPosition, string previousEventHash, ProductionRecoveryObservation observation,
        ProductionRecoverySafetyEvidence safetyEvidence, Guid actorPrincipalId, Guid actorSessionId,
        long authorizationRevision, Guid stepUpGrantId, RecipeContractReference authorizationPolicy,
        Guid commandCorrelationId, Guid commandAttemptId, string authorizationTarget,
        PartDisposition disposition, string? dispositionNote, ProductionRecoveryOutcome outcome,
        ProductionRecoveryCleanupReceipt? completionReceipt = null, long commandAuditSequence = 0,
        string? commandAuditHash = null, long authorizationAuditSequence = 0,
        string? authorizationAuditHash = null, string? authorizationSafetyEvidence = null,
        string? contentHash = null)
    {
        if (recoveryAttemptId == Guid.Empty || recoveryRuntimeEpoch == Guid.Empty ||
            inspectionId == Guid.Empty || previousEventPosition < 1 ||
            actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty || stepUpGrantId == Guid.Empty ||
            commandCorrelationId == Guid.Empty || commandAttemptId == Guid.Empty ||
            authorizationRevision < 0 || observation is null || safetyEvidence is null || authorizationPolicy is null)
            throw new ArgumentException("ProductionRecoveryRecordIdentityInvalid");
        if (!Enum.IsDefined(typeof(ProductionRecoveryOutcome), outcome) ||
            !Enum.IsDefined(typeof(PartDisposition), disposition))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        PreviousEventHash = RecipeActivationValidation.Hash(previousEventHash, nameof(previousEventHash));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        DispositionNote = dispositionNote is null ? null :
            AlgorithmContractValidation.BoundedText(dispositionNote, nameof(dispositionNote), 512);
        if (commandAuditSequence < 0 || (commandAuditSequence == 0) != (commandAuditHash is null) ||
            authorizationAuditSequence < 0 ||
            (authorizationAuditSequence == 0) != (authorizationAuditHash is null) ||
            (authorizationAuditSequence > 0 && string.IsNullOrWhiteSpace(authorizationSafetyEvidence)))
            throw new ArgumentException("ProductionRecoveryAuditReferenceInvalid");
        if (outcome == ProductionRecoveryOutcome.Completed &&
            (completionReceipt is null || !completionReceipt.CleanupCompleted))
            throw new ArgumentException("ProductionRecoveryCompletionReceiptRequired");
        if (outcome != ProductionRecoveryOutcome.Completed && completionReceipt is not null)
            throw new ArgumentException("ProductionRecoveryCompletionReceiptUnexpected");

        RecoveryAttemptId = recoveryAttemptId;
        RecoveryRuntimeEpoch = recoveryRuntimeEpoch;
        InspectionId = inspectionId;
        PreviousEventPosition = previousEventPosition;
        Observation = observation;
        SafetyEvidence = safetyEvidence;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        AuthorizationRevision = authorizationRevision;
        StepUpGrantId = stepUpGrantId;
        AuthorizationPolicy = authorizationPolicy;
        CommandCorrelationId = commandCorrelationId;
        CommandAttemptId = commandAttemptId;
        Disposition = disposition;
        Outcome = outcome;
        CompletionReceipt = completionReceipt;
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = commandAuditHash is null ? null :
            RecipeActivationValidation.Hash(commandAuditHash, nameof(commandAuditHash));
        AuthorizationAuditSequence = authorizationAuditSequence;
        AuthorizationAuditHash = authorizationAuditHash is null ? null :
            RecipeActivationValidation.Hash(authorizationAuditHash, nameof(authorizationAuditHash));
        AuthorizationSafetyEvidence = authorizationSafetyEvidence is null ? null :
            AlgorithmContractValidation.BoundedText(authorizationSafetyEvidence,
                nameof(authorizationSafetyEvidence), 2048);
        ContentHash = contentHash is null ? ComputeContentHash() :
            RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(), StringComparison.Ordinal))
            throw new ArgumentException("ProductionRecoveryContentHashMismatch", nameof(contentHash));
    }

    public Guid RecoveryAttemptId { get; }
    public Guid RecoveryRuntimeEpoch { get; }
    public Guid InspectionId { get; }
    public long PreviousEventPosition { get; }
    public string PreviousEventHash { get; }
    public ProductionRecoveryObservation Observation { get; }
    public ProductionRecoverySafetyEvidence SafetyEvidence { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long AuthorizationRevision { get; }
    public Guid StepUpGrantId { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public Guid CommandCorrelationId { get; }
    public Guid CommandAttemptId { get; }
    public string AuthorizationTarget { get; }
    public PartDisposition Disposition { get; }
    public string? DispositionNote { get; }
    public ProductionRecoveryOutcome Outcome { get; }
    public ProductionRecoveryCleanupReceipt? CompletionReceipt { get; }
    public long CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long AuthorizationAuditSequence { get; }
    public string? AuthorizationAuditHash { get; }
    /// <summary>
    /// The canonical safety envelope from the authorization which produced this
    /// exact record projection.  <see cref="SafetyEvidence"/> remains the
    /// immutable physical stop anchor captured when the recovery session was
    /// first admitted; this field binds a retry/completion to its current
    /// authorization audit without replacing that original anchor.
    /// </summary>
    public string? AuthorizationSafetyEvidence { get; }
    public string ContentHash { get; }

    private string ComputeContentHash() => AlgorithmContractValidation.HashParts(new string?[]
    {
        "sharpinspect-production-recovery-record-v2", RecoveryAttemptId.ToString("D"),
        RecoveryRuntimeEpoch.ToString("D"), InspectionId.ToString("D"),
        PreviousEventPosition.ToString(CultureInfo.InvariantCulture),
        PreviousEventHash, Observation.ContentHash, SafetyEvidence.ContentHash,
        ActorPrincipalId.ToString("D"), ActorSessionId.ToString("D"),
        AuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId.ToString("D"),
        AuthorizationPolicy.ContentHash, CommandCorrelationId.ToString("D"), CommandAttemptId.ToString("D"),
        AuthorizationTarget, Disposition.ToString(), DispositionNote, Outcome.ToString(),
        CompletionReceipt?.ContentHash, CommandAuditSequence.ToString(CultureInfo.InvariantCulture),
        CommandAuditHash, AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture),
        AuthorizationAuditHash, AuthorizationSafetyEvidence
    });
}

public sealed record ProductionRecoveryCompletionRequest(Guid CorrelationId, Guid RecoveryAttemptId,
    Guid RuntimeEpoch, string ExpectedRecoveryContentHash, ProductionRecoveryCleanupReceipt Receipt)
{
    public string ExpectedRecoveryContentHash { get; init; } =
        RecipeActivationValidation.Hash(ExpectedRecoveryContentHash, nameof(ExpectedRecoveryContentHash));
}

public sealed record ProductionRecoveryResult(RuntimeCommandOutcome Outcome,
    ProductionInspectionHistoryEvent? Event = null, Guid? RecoveryAttemptId = null);

public sealed record ProductionRecoveryHistoryReadResult(bool Available, string ReasonCode,
    ProductionInspectionHistoryEvent? Latest = null, bool RecoveryRequired = false);

public sealed record ProductionRecoveryHistoryPage
{
    public ProductionRecoveryHistoryPage(bool available, string reasonCode,
        IEnumerable<ProductionInspectionHistoryEvent>? events, long throughPosition,
        long? nextAfterPosition, bool recoveryRequired = false)
    {
        Available = available;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<ProductionInspectionHistoryEvent>(
            (events ?? Array.Empty<ProductionInspectionHistoryEvent>()).ToArray());
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        RecoveryRequired = recoveryRequired;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<ProductionInspectionHistoryEvent> Events { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public bool RecoveryRequired { get; }
}

public sealed record ProductionRecoveryPendingItem(
    Guid InspectionId, long Position, string EventHash,
    ProductionRecoveryDeliveryPhase DeliveryPhase,
    ProductionRecoveryUncertaintyKind Uncertainty,
    ProductionInspectionHistoryEvent Event);

public sealed record ProductionRecoveryPendingPage(bool Available, string ReasonCode,
    IEnumerable<ProductionRecoveryPendingItem>? items, int PendingCount,
    long? NextAfterPosition)
{
    public ReadOnlyCollection<ProductionRecoveryPendingItem> Items { get; } =
        new((items ?? Array.Empty<ProductionRecoveryPendingItem>()).ToArray());
}

public interface IProductionRecoveryHistoryQuery
{
    ValueTask<ProductionRecoveryHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<ProductionRecoveryHistoryReadResult> ReadAsync(Guid inspectionId,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionRecoveryHistoryPage> QueryAsync(ProductionInspectionHistoryFilter filter,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionRecoveryPendingPage> QueryPendingAsync(int pageSize = 128,
        long afterPosition = 0, CancellationToken cancellationToken = default);
}
