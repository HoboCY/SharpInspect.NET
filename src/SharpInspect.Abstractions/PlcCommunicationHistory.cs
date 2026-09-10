using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable communication facts emitted by the single PLC adapter owner.
/// These facts describe transport and synchronization only; they never carry
/// production authority or a result payload.
/// </summary>
public enum PlcCommunicationEventKind : byte
{
    ConnectionEstablished = 1,
    HeartbeatStale = 2,
    ControllerEpochChanged = 3,
    RecoveryCycleStarted = 4,
    ReconnectAttempt = 5,
    SynchronizationWindowObserved = 6,
    RecoveryExhausted = 7,
    CommunicationStopped = 8,
    CommunicationLost = 9,
    ReconnectFailed = 10,
    RecoveryCompleted = 11,
    ControllerEpochObserved = 12
}

/// <summary>One append-only, hash-bound PLC communication observation.</summary>
public sealed record PlcCommunicationEvent
{
    internal PlcCommunicationEvent(long position, string? previousHash, Guid eventId,
        Guid runtimeEpoch, string endpointBindingHash, string profileHash, string policyHash,
        long generation, int attempt, PlcCommunicationEventKind kind, string reasonCode,
        uint? controllerEpoch, DateTimeOffset observedAtUtc, long monotonicTimestamp,
        Guid? recoveryCycleId = null, Guid? qualificationSessionId = null, Guid? runId = null,
        uint? cycleSequence = null, long auditSequence = 0, string? auditHash = null,
        string? contentHash = null)
    {
        if (position < 1 || eventId == Guid.Empty || runtimeEpoch == Guid.Empty ||
            generation < 0 || attempt < 0 || monotonicTimestamp <= 0)
            throw new ArgumentException("PlcCommunicationEventIdentityInvalid");
        if (!Enum.IsDefined(typeof(PlcCommunicationEventKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("PlcCommunicationTimestampInvalid", nameof(observedAtUtc));
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("PlcCommunicationAuditReferenceInvalid");

        Position = position;
        PreviousHash = OptionalHash(previousHash, nameof(previousHash));
        SystemPrincipalId = SharpInspect.Abstractions.SystemPrincipalId.PlcAdapter;
        EventId = eventId;
        RuntimeEpoch = runtimeEpoch;
        EndpointBindingHash = Hash(endpointBindingHash, nameof(endpointBindingHash));
        ProfileHash = Hash(profileHash, nameof(profileHash));
        PolicyHash = Hash(policyHash, nameof(policyHash));
        Generation = generation;
        Attempt = attempt;
        Kind = kind;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        ControllerEpoch = controllerEpoch;
        ObservedAtUtc = observedAtUtc;
        MonotonicTimestamp = monotonicTimestamp;
        RecoveryCycleId = recoveryCycleId;
        QualificationSessionId = qualificationSessionId;
        RunId = runId;
        CycleSequence = cycleSequence;
        AuditSequence = auditSequence;
        AuditHash = OptionalHash(auditHash, nameof(auditHash));
        ContentHash = contentHash is null ? ComputeContentHash() : Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(), StringComparison.Ordinal))
            throw new ArgumentException("PlcCommunicationContentHashMismatch", nameof(contentHash));
    }

    public long Position { get; }
    public string? PreviousHash { get; }
    /// <summary>Fixed system identity; callers cannot supply or override it.</summary>
    public string SystemPrincipalId { get; }
    public Guid EventId { get; }
    public Guid RuntimeEpoch { get; }
    public string EndpointBindingHash { get; }
    public string ProfileHash { get; }
    public string PolicyHash { get; }
    public long Generation { get; }
    public int Attempt { get; }
    public PlcCommunicationEventKind Kind { get; }
    public string ReasonCode { get; }
    public uint? ControllerEpoch { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public Guid? RecoveryCycleId { get; }
    public Guid? QualificationSessionId { get; }
    public Guid? RunId { get; }
    public uint? CycleSequence { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }

    private string ComputeContentHash() => AlgorithmContractValidation.HashParts(new string?[]
    {
        "sharpinspect-plc-communication-event-v1",
        SystemPrincipalId, Position.ToString(CultureInfo.InvariantCulture), PreviousHash,
        EventId.ToString("D"), RuntimeEpoch.ToString("D"), EndpointBindingHash,
        ProfileHash, PolicyHash, Generation.ToString(CultureInfo.InvariantCulture),
        Attempt.ToString(CultureInfo.InvariantCulture), Kind.ToString(), ReasonCode,
        ControllerEpoch?.ToString(CultureInfo.InvariantCulture),
        ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        MonotonicTimestamp.ToString(CultureInfo.InvariantCulture), RecoveryCycleId?.ToString("D"),
        QualificationSessionId?.ToString("D"), RunId?.ToString("D"),
        CycleSequence?.ToString(CultureInfo.InvariantCulture),
        AuditSequence.ToString(CultureInfo.InvariantCulture), AuditHash
    });

    private static string Hash(string value, string parameterName) =>
        QualificationContractValidation.Hash(value, parameterName);

    private static string? OptionalHash(string? value, string parameterName) => value is null
        ? null : Hash(value, parameterName);
}

public sealed record PlcCommunicationHistoryFilter(
    string? EndpointBindingHash = null,
    Guid? RecoveryCycleId = null,
    Guid? QualificationSessionId = null,
    Guid? RunId = null,
    long AfterPosition = 0,
    long? ThroughPosition = null,
    int PageSize = 20);

public sealed record PlcCommunicationHistoryReadResult(bool Available, string ReasonCode,
    PlcCommunicationEvent? Latest = null, bool RecoveryRequired = false);

public sealed record PlcCommunicationHistoryPage
{
    public PlcCommunicationHistoryPage(bool available, string reasonCode,
        IEnumerable<PlcCommunicationEvent>? events, long throughPosition,
        long? nextAfterPosition, bool recoveryRequired = false)
    {
        Available = available;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<PlcCommunicationEvent>(
            (events ?? Array.Empty<PlcCommunicationEvent>()).ToArray());
        if (throughPosition < 0 || nextAfterPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        RecoveryRequired = recoveryRequired;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<PlcCommunicationEvent> Events { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public bool RecoveryRequired { get; }
}

/// <summary>Read-only access to the schema-27 PLC communication ledger.</summary>
public interface IPlcCommunicationHistoryQuery
{
    ValueTask<PlcCommunicationHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<PlcCommunicationHistoryPage> QueryAsync(PlcCommunicationHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
