using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The closed reason one production-arm attempt exists. The value is evidence
/// vocabulary only; it never widens who may request an arm.
/// </summary>
public enum ProductionArmCause : byte
{
    /// <summary>Runtime start-up re-evaluated the station and requested an arm.</summary>
    Startup = 1,
    /// <summary>The exact signed PLC recipe-change handshake resolved this arm.</summary>
    PlcActivation = 2,
    /// <summary>An authenticated human requested a maintenance arm after evidence.</summary>
    ManualMaintenanceArm = 3
}

/// <summary>
/// The immutable event kinds in the schema-32 production-arm ledger.
/// PlcStatusDelivered/PlcStatusUndelivered are observations of the optional
/// status write; they never change the attempt state.
/// </summary>
public enum ProductionArmEventKind : byte
{
    Attempted = 1,
    Authorized = 2,
    ReadyConfirmed = 3,
    Rejected = 4,
    Failed = 5,
    PlcStatusDelivered = 6,
    PlcStatusUndelivered = 7
}

/// <summary>
/// The closed, bounded outcome reasons shared by the runtime actor and the
/// central audit projection. None is the only value permitted for a delivered
/// status observation.
/// </summary>
public enum ProductionArmReason : byte
{
    None = 0,
    PolicyManual = 1,
    MaintenanceEvidenceUnavailable = 2,
    ManualArmAfterMaintenanceRequired = 3,
    HandshakeIncomplete = 4,
    InputsNotStable = 5,
    AdmissionGateBlocked = 6,
    AuthorityChanged = 7,
    AuditUnavailable = 8,
    Cancelled = 9,
    ConfigurationUnavailable = 10,
    RuntimeBusy = 11,
    ReadyWriteUncertain = 12,
    Interrupted = 13
}

/// <summary>
/// One durable, read-only production-arm event. The event binds the immutable
/// attempt context (policies, deployment, source) and, for Authorized and later
/// kinds, the exact fixed-gate report, durable heads and actor attribution.
/// A Runtime-caused attempt is attributed to <see cref="SystemPrincipalId.Runtime"/>
/// and carries no human identity; only ManualMaintenanceArm is attributed to the
/// actual authenticated human, and that attribution can never be fabricated.
/// </summary>
public sealed record ProductionArmHistoryEvent
{
    internal ProductionArmHistoryEvent(long position, Guid eventId, Guid attemptId, Guid runtimeEpoch,
        ProductionArmCause cause, ProductionArmEventKind kind, string stationId,
        RecipeContractReference startupPolicy, RecipeContractReference postActivationPolicy, string deploymentHash,
        RecipeChangeRequestEvidence? plcRequest, RecipeActivationReference? activation, string? maintenanceHeadHash,
        long admissionGeneration, ProductionAdmissionReport? report,
        IReadOnlyDictionary<string, string> expectedDurableHeads,
        IReadOnlyDictionary<string, string> currentDurableHeads, ProductionArmReason reason, string reasonCode,
        Guid? humanCommandId, Guid? humanPrincipalId, Guid? humanSessionId, DateTimeOffset recordedAtUtc,
        string? actorPrincipalId = null, Guid? actorSessionId = null, string? contentHash = null,
        ProductionArmReadyReceipt? readyReceipt = null, ProductionArmInputStabilityEvidence? inputStability = null)
    {
        if (position < 1 || eventId == Guid.Empty || attemptId == Guid.Empty || runtimeEpoch == Guid.Empty ||
            admissionGeneration < 0)
            throw new ArgumentException("ProductionArmHistoryIdentityInvalid");
        if (!Enum.IsDefined(cause)) throw new ArgumentOutOfRangeException(nameof(cause), "ProductionArmCauseInvalid");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), "ProductionArmEventKindInvalid");
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason), "ProductionArmReasonInvalid");
        Position = position;
        EventId = eventId;
        AttemptId = attemptId;
        RuntimeEpoch = runtimeEpoch;
        Cause = cause;
        Kind = kind;
        StationId = AlgorithmConfigurationValidation.Identifier(stationId, nameof(stationId));
        StartupPolicy = startupPolicy ?? throw new ArgumentNullException(nameof(startupPolicy));
        PostActivationPolicy = postActivationPolicy ?? throw new ArgumentNullException(nameof(postActivationPolicy));
        DeploymentHash = RecipeActivationValidation.Hash(deploymentHash, nameof(deploymentHash));
        MaintenanceHeadHash = RecipeActivationValidation.OptionalHash(maintenanceHeadHash, nameof(maintenanceHeadHash));
        AdmissionGeneration = admissionGeneration;
        Report = report;
        ReadyReceipt = readyReceipt;
        InputStability = inputStability;
        ExpectedDurableHeads = CopyHeads(expectedDurableHeads, nameof(expectedDurableHeads));
        CurrentDurableHeads = CopyHeads(currentDurableHeads, nameof(currentDurableHeads));
        Reason = reason;
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        // The three human fields are all-or-nothing and exist exactly for the
        // manual maintenance cause. Any other combination is a conflicting
        // actor claim and never silently becomes a Runtime attribution.
        var manual = cause == ProductionArmCause.ManualMaintenanceArm;
        if (manual)
        {
            if (humanCommandId is null || humanPrincipalId is null || humanSessionId is null ||
                humanCommandId == Guid.Empty || humanPrincipalId == Guid.Empty || humanSessionId == Guid.Empty)
                throw new ArgumentException("ProductionArmHumanActorRequired", nameof(humanCommandId));
        }
        else if (humanCommandId is not null || humanPrincipalId is not null || humanSessionId is not null)
        {
            throw new ArgumentException("ProductionArmHumanActorConflict", nameof(humanCommandId));
        }
        var expectedPrincipal = manual
            ? humanPrincipalId!.Value.ToString("D") : SystemPrincipalId.Runtime;
        var expectedSession = manual ? humanSessionId : null;
        if (actorPrincipalId is not null && !string.Equals(actorPrincipalId, expectedPrincipal, StringComparison.Ordinal) ||
            actorSessionId is not null && actorSessionId != expectedSession)
            throw new ArgumentException("ProductionArmActorConflict", nameof(actorPrincipalId));
        HumanCommandId = humanCommandId;
        HumanPrincipalId = humanPrincipalId;
        HumanSessionId = humanSessionId;
        ActorPrincipalId = expectedPrincipal;
        ActorSessionId = expectedSession;
        // The source evidence is closed by cause: only a PLC-caused attempt may
        // carry the schema-31 request evidence, and a PLC-caused
        // Authorized/ReadyConfirmed attempt must carry both.
        if (cause == ProductionArmCause.PlcActivation)
        {
            if (plcRequest is null) throw new ArgumentException("ProductionArmPlcRequestRequired", nameof(plcRequest));
            if ((kind is ProductionArmEventKind.Authorized or ProductionArmEventKind.ReadyConfirmed) &&
                activation is null)
                throw new ArgumentException("ProductionArmPlcActivationRequired", nameof(activation));
        }
        else if (plcRequest is not null)
        {
            throw new ArgumentException("ProductionArmSourceConflict", nameof(plcRequest));
        }
        if (kind is ProductionArmEventKind.Authorized or ProductionArmEventKind.ReadyConfirmed)
        {
            if (report is null || maintenanceHeadHash is null ||
                !ProductionArmHeads.Equal(expectedDurableHeads, currentDurableHeads))
                throw new ArgumentException("ProductionArmAuthorizationEvidenceInvalid", nameof(report));
        }
        if (kind == ProductionArmEventKind.PlcStatusDelivered && reason != ProductionArmReason.None ||
            kind == ProductionArmEventKind.PlcStatusUndelivered && reason == ProductionArmReason.None)
            throw new ArgumentException("ProductionArmStatusOutcomeInvalid", nameof(reason));
        PlcRequest = plcRequest;
        Activation = activation;
        RecordedAtUtc = RecipeActivationValidation.Utc(recordedAtUtc, nameof(recordedAtUtc));
        ContentHash = contentHash is null ? ComputeContentHash() :
            RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(), StringComparison.Ordinal))
            throw new ArgumentException("ProductionArmHistoryContentHashMismatch", nameof(contentHash));
    }

    public long Position { get; }
    public Guid EventId { get; }
    public Guid AttemptId { get; }
    public Guid RuntimeEpoch { get; }
    public ProductionArmCause Cause { get; }
    public ProductionArmEventKind Kind { get; }
    public string StationId { get; }
    public RecipeContractReference StartupPolicy { get; }
    public RecipeContractReference PostActivationPolicy { get; }
    public string DeploymentHash { get; }
    public RecipeChangeRequestEvidence? PlcRequest { get; }
    public RecipeActivationReference? Activation { get; }
    public string? MaintenanceHeadHash { get; }
    public long AdmissionGeneration { get; }
    public ProductionAdmissionReport? Report { get; }
    public ReadOnlyDictionary<string, string> ExpectedDurableHeads { get; }
    public ReadOnlyDictionary<string, string> CurrentDurableHeads { get; }
    public ProductionArmReason Reason { get; }
    public string ReasonCode { get; }
    public Guid? HumanCommandId { get; }
    public Guid? HumanPrincipalId { get; }
    public Guid? HumanSessionId { get; }
    /// <summary>SystemPrincipalId.Runtime, or the actual human principal for a manual arm.</summary>
    public string ActorPrincipalId { get; }
    /// <summary>The authenticated human session for a manual arm; null for the Runtime actor.</summary>
    public Guid? ActorSessionId { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
    public ProductionArmReadyReceipt? ReadyReceipt { get; }
    public ProductionArmInputStabilityEvidence? InputStability { get; }

    public bool RuntimeActor => string.Equals(ActorPrincipalId, SystemPrincipalId.Runtime, StringComparison.Ordinal);
    public bool StatusDelivery => Kind is ProductionArmEventKind.PlcStatusDelivered or
        ProductionArmEventKind.PlcStatusUndelivered;
    /// <summary>A state-bearing end of the attempt; only one optional status
    /// delivery observation may follow it and it never changes the state.</summary>
    public bool Terminal => Kind is ProductionArmEventKind.ReadyConfirmed or
        ProductionArmEventKind.Rejected or ProductionArmEventKind.Failed;

    private string ComputeContentHash()
    {
        var fields = new List<string?>(48 +
            (ExpectedDurableHeads.Count + CurrentDurableHeads.Count) * 2 + (Report?.Gates.Count ?? 0) * 8)
        {
            "sharpinspect-production-arm-history-event-v1",
            Position.ToString(CultureInfo.InvariantCulture), EventId.ToString("D"), AttemptId.ToString("D"),
            RuntimeEpoch.ToString("D"), Cause.ToString(), Kind.ToString(), StationId,
            StartupPolicy.Id, StartupPolicy.Version, StartupPolicy.ContentHash,
            PostActivationPolicy.Id, PostActivationPolicy.Version, PostActivationPolicy.ContentHash,
            DeploymentHash, MaintenanceHeadHash, InputStability?.ContentHash,
            AdmissionGeneration.ToString(CultureInfo.InvariantCulture), Report?.ContentHash,
            Reason.ToString(), ReasonCode,
            HumanCommandId?.ToString("D"), HumanPrincipalId?.ToString("D"), HumanSessionId?.ToString("D"),
            ActorPrincipalId, ActorSessionId?.ToString("D"),
            PlcRequest?.ContentHash, PlcRequest?.RequestIdentityHash, PlcRequest?.OperationId.ToString("D"),
            Activation?.Position.ToString(CultureInfo.InvariantCulture), Activation?.ActivationId.ToString("D"),
            Activation?.ContentHash,
            RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), ReadyReceipt?.ContentHash
        };
        fields.Add("expected-heads");
        foreach (var pair in ExpectedDurableHeads) fields.AddRange(new[] { pair.Key, pair.Value });
        fields.Add("current-heads");
        foreach (var pair in CurrentDurableHeads) fields.AddRange(new[] { pair.Key, pair.Value });
        return AlgorithmContractValidation.HashParts(fields);
    }

    private static ReadOnlyDictionary<string, string> CopyHeads(
        IReadOnlyDictionary<string, string> heads, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(heads, parameterName);
        if (heads.Count > 64) throw new ArgumentException("ProductionArmDurableHeadLimitExceeded", parameterName);
        var copied = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in heads)
        {
            ArgumentNullException.ThrowIfNull(pair.Key, parameterName);
            if (pair.Key.Length is < 1 or > 128 || pair.Key.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
                throw new ArgumentException("ProductionArmDurableHeadInvalid", parameterName);
            copied.Add(pair.Key, RecipeActivationValidation.Hash(pair.Value, parameterName));
        }
        return new ReadOnlyDictionary<string, string>(copied);
    }
}

/// <summary>Exact multi-set comparison for the bounded durable-head maps.</summary>
public static class ProductionArmHeads
{
    public static bool Equal(IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> current) => expected.Count == current.Count &&
        expected.All(pair => current.TryGetValue(pair.Key, out var value) &&
            string.Equals(value, pair.Value, StringComparison.Ordinal));
}

public sealed record ProductionArmHistoryFilter(Guid? AttemptId = null, long AfterPosition = 0,
    long? ThroughPosition = null, int PageSize = 20);

/// <summary>The newest durable arm event, plus the open attempt it belongs to when
/// that attempt has no terminal event yet. A pending attempt is never re-authorized.</summary>
public sealed record ProductionArmHistoryReadResult(bool Available, string ReasonCode,
    ProductionArmHistoryEvent? Current = null, ProductionArmHistoryEvent? Pending = null,
    bool RecoveryRequired = false);

public sealed record ProductionArmHistoryPage(bool Available, string ReasonCode,
    ReadOnlyCollection<ProductionArmHistoryEvent> Events, long ThroughPosition, long? NextAfterPosition);

/// <summary>Bounded read-only access to the schema-32 production-arm ledger.</summary>
public interface IProductionArmHistoryQuery
{
    ValueTask<ProductionArmHistoryPage> QueryAsync(ProductionArmHistoryFilter filter,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionArmHistoryReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
}
