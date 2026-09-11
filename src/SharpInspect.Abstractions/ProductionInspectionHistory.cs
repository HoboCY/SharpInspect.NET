using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// One immutable lifecycle row. The admission is repeated by reference in the
/// in-memory projection so a reader cannot accidentally detach a Core from the
/// exact accepted trigger.
/// </summary>
public sealed record ProductionInspectionHistoryEvent
{
    internal ProductionInspectionHistoryEvent(long position, ProductionInspectionEventKind kind,
        ProductionInspectionAdmission admission, ProductionInspectionCore? core,
        string reasonCode, DateTimeOffset recordedAtUtc, long monotonicTimestamp,
        long auditSequence = 0, string? auditHash = null, string? contentHash = null,
        ProductionRecoveryRecord? recovery = null)
    {
        if (position < 1 || !Enum.IsDefined(kind) || admission is null ||
            recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero ||
            monotonicTimestamp <= 0)
            throw new ArgumentException("ProductionInspectionHistoryIdentityInvalid");
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("ProductionInspectionHistoryAuditReferenceInvalid");
        if (kind == ProductionInspectionEventKind.CoreCommitted && core is null)
            throw new ArgumentException("ProductionInspectionCoreRequired");
        if (recovery is not null && kind is not (ProductionInspectionEventKind.RecoveryRequired or
            ProductionInspectionEventKind.RecoveryCompleted))
            throw new ArgumentException("ProductionRecoveryEventKindRequired", nameof(recovery));
        if (recovery is not null && recovery.InspectionId != admission.InspectionId)
            throw new ArgumentException("ProductionRecoveryInspectionMismatch", nameof(recovery));
        // Legacy v1/v2 recovery rows have no typed recovery metadata. Their
        // original content hash and envelope remain readable; schema 30's
        // writer and history verifier require the new authorized record.
        if (kind == ProductionInspectionEventKind.RecoveryCompleted && recovery is not null &&
            recovery.Outcome != ProductionRecoveryOutcome.Completed)
            throw new ArgumentException("ProductionRecoveryCompletionRecordRequired", nameof(recovery));
        if (core is not null && core.Admission.ContentHash != admission.ContentHash)
            throw new ArgumentException("ProductionInspectionHistoryAdmissionMismatch");

        Position = position;
        Kind = kind;
        Admission = admission;
        Core = core;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        RecordedAtUtc = recordedAtUtc;
        MonotonicTimestamp = monotonicTimestamp;
        AuditSequence = auditSequence;
        AuditHash = auditHash is null ? null : RecipeActivationValidation.Hash(auditHash, nameof(auditHash));
        Recovery = recovery;
        ContentHash = contentHash is null ? ComputeContentHash() :
            RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(),
                StringComparison.Ordinal))
            throw new ArgumentException("ProductionInspectionHistoryContentHashMismatch",
                nameof(contentHash));
    }

    public long Position { get; }
    public ProductionInspectionEventKind Kind { get; }
    public ProductionInspectionAdmission Admission { get; }
    public ProductionInspectionCore? Core { get; }
    public Guid InspectionId => Admission.InspectionId;
    public Guid CorrelationId => Admission.CorrelationId;
    public Guid RuntimeEpoch => Admission.RuntimeEpoch;
    public string ReasonCode { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public ProductionRecoveryRecord? Recovery { get; }
    public string ContentHash { get; }

    private string ComputeContentHash()
    {
        var fields = new List<string?>
        {
            Recovery is null ? "sharpinspect-production-inspection-history-v1" :
                "sharpinspect-production-inspection-history-v2",
            Position.ToString(CultureInfo.InvariantCulture), Kind.ToString(), Admission.ContentHash,
            Core?.ContentHash, ReasonCode, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            AuditSequence.ToString(CultureInfo.InvariantCulture), AuditHash
        };
        if (Recovery is not null) fields.Add(Recovery.ContentHash);
        return AlgorithmContractValidation.HashParts(fields);
    }
}

public sealed record ProductionInspectionHistoryFilter(
    Guid? InspectionId = null,
    Guid? CorrelationId = null,
    Guid? RuntimeEpoch = null,
    long AfterPosition = 0,
    long? ThroughPosition = null,
    int PageSize = 20);

public sealed record ProductionInspectionHistoryReadResult(bool Available, string ReasonCode,
    ProductionInspectionHistoryEvent? Latest = null, bool RecoveryRequired = false);

public sealed record ProductionInspectionHistoryPage
{
    public ProductionInspectionHistoryPage(bool available, string reasonCode,
        IEnumerable<ProductionInspectionHistoryEvent>? events, long throughPosition,
        long? nextAfterPosition, bool recoveryRequired = false)
    {
        Available = available;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<ProductionInspectionHistoryEvent>(
            (events ?? Array.Empty<ProductionInspectionHistoryEvent>()).ToArray());
        if (throughPosition < 0 || nextAfterPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
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

/// <summary>Read-only access to the schema-28 production Core ledger.</summary>
public interface IProductionInspectionHistoryQuery
{
    ValueTask<ProductionInspectionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<ProductionInspectionHistoryReadResult> ReadAsync(Guid inspectionId,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionInspectionHistoryPage> QueryAsync(
        ProductionInspectionHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
