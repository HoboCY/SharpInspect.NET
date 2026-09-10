using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable lifecycle facts for one qualification run.  The ledger is an
/// isolated coordination record; it never grants production authority and it
/// never replaces the StationQualification event that contains the typed run.
/// </summary>
public enum QualificationCycleEventKind : byte
{
    Admitted = 1,
    CoreCommitted = 2,
    PublicationPrepared = 3,
    ResultValidPublished = 4,
    ResultAckObserved = 5,
    ResultValidCleared = 6,
    AckReset = 7,
    ProtocolRequestRejected = 8,
    AckTimeout = 9,
    CycleFaultTerminated = 10
}

/// <summary>One immutable, hash-bound qualification-cycle ledger event.</summary>
public sealed record QualificationCycleEvent
{
    internal QualificationCycleEvent(long position, string? previousHash, Guid sessionId,
        QualificationRunId? runId, string endpointBindingHash, string profileHash,
        QualificationCycleEventKind kind, TraceStoragePolicySnapshot? policySnapshot,
        long qualificationPosition, string qualificationHash, string? runContentHash,
        string reasonCode, DateTimeOffset recordedAtUtc, bool terminal,
        long auditSequence = 0, string? auditHash = null,
        uint? controllerEpoch = null, uint? cycleSequence = null)
    {
        if (position < 1 || sessionId == Guid.Empty)
            throw new ArgumentException("QualificationCycleEventIdentityInvalid");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (qualificationPosition < 1 || string.IsNullOrWhiteSpace(qualificationHash))
            throw new ArgumentException("QualificationCycleQualificationReferenceInvalid");
        EndpointBindingHash = QualificationContractValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        ProfileHash = QualificationContractValidation.Hash(profileHash, nameof(profileHash));
        if (runContentHash is not null)
            runContentHash = QualificationContractValidation.Hash(runContentHash, nameof(runContentHash));
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("QualificationCycleTimestampInvalid", nameof(recordedAtUtc));
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("QualificationCycleAuditReferenceInvalid");
        if (auditHash is not null)
            auditHash = QualificationContractValidation.Hash(auditHash, nameof(auditHash));
        if (terminal && kind is not (QualificationCycleEventKind.AckReset or
            QualificationCycleEventKind.CycleFaultTerminated or QualificationCycleEventKind.AckTimeout))
            throw new ArgumentException("QualificationCycleTerminalKindInvalid", nameof(kind));
        Position = position;
        PreviousHash = previousHash is null ? null :
            QualificationContractValidation.Hash(previousHash, nameof(previousHash));
        SessionId = sessionId;
        RunId = runId;
        Kind = kind;
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        PolicySnapshot = policySnapshot;
        QualificationPosition = qualificationPosition;
        QualificationHash = QualificationContractValidation.Hash(qualificationHash,
            nameof(qualificationHash));
        RunContentHash = runContentHash;
        Terminal = terminal;
        RecordedAtUtc = recordedAtUtc;
        AuditSequence = auditSequence;
        AuditHash = auditHash;
        // ContentHash is always derived from the immutable fields.  A decoded
        // payload must compare its stored hash to this value; callers cannot
        // supply an alternate hash and thereby turn an untrusted row into a
        // self-consistent event.
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-qualification-cycle-event-v1",
            position.ToString(CultureInfo.InvariantCulture), PreviousHash,
            sessionId.ToString("D"), runId?.Value.ToString("D"), EndpointBindingHash,
            ProfileHash, kind.ToString(), policySnapshot?.ContentHash,
            qualificationPosition.ToString(CultureInfo.InvariantCulture), QualificationHash,
            RunContentHash, ReasonCode, recordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            terminal ? "1" : "0", auditSequence.ToString(CultureInfo.InvariantCulture), auditHash,
            controllerEpoch?.ToString(CultureInfo.InvariantCulture),
            cycleSequence?.ToString(CultureInfo.InvariantCulture), EvidenceMode.ToString()
        });
    }

    public long Position { get; }
    public string? PreviousHash { get; }
    public Guid SessionId { get; }
    public QualificationRunId? RunId { get; }
    public string EndpointBindingHash { get; }
    public string ProfileHash { get; }
    public uint? ControllerEpoch { get; }
    public uint? CycleSequence { get; }
    public QualificationCycleEventKind Kind { get; }
    public TraceStoragePolicySnapshot? PolicySnapshot { get; }
    public long QualificationPosition { get; }
    public string QualificationHash { get; }
    public string? RunContentHash { get; }
    public string ReasonCode { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public bool Terminal { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }
    public QualificationEvidenceCaptureMode EvidenceMode => QualificationEvidenceCaptureMode.None;
}

public sealed record QualificationCycleHistoryFilter(Guid? SessionId = null,
    Guid? RunId = null, long AfterPosition = 0, long? ThroughPosition = null,
    int PageSize = 20);

public sealed record QualificationCycleHistoryReadResult(bool Available, string ReasonCode,
    QualificationCycleEvent? LastEvent = null, bool RecoveryRequired = false,
    bool RecoverablePending = false);

public sealed record QualificationCycleHistoryPage
{
    public QualificationCycleHistoryPage(bool available, string reasonCode,
        IEnumerable<QualificationCycleEvent>? events, long throughPosition,
        long? nextAfterPosition, QualificationCycleEvent? pendingEvent = null,
        bool recoveryRequired = false, bool recoverablePending = false)
    {
        Available = available;
        ReasonCode = QualificationContractValidation.Reason(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<QualificationCycleEvent>(
            (events ?? Array.Empty<QualificationCycleEvent>()).ToArray());
        if (throughPosition < 0 || nextAfterPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        PendingEvent = pendingEvent;
        RecoveryRequired = recoveryRequired;
        RecoverablePending = recoverablePending;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<QualificationCycleEvent> Events { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public QualificationCycleEvent? PendingEvent { get; }
    public bool RecoveryRequired { get; }
    public bool RecoverablePending { get; }
}

/// <summary>Read-only bounded access to schema-26 qualification-cycle history.</summary>
public interface IQualificationCycleHistoryQuery
{
    ValueTask<QualificationCycleHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<QualificationCycleHistoryReadResult> ReadAsync(Guid sessionId,
        CancellationToken cancellationToken = default);
    ValueTask<QualificationCycleHistoryPage> QueryAsync(QualificationCycleHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
