using System.Globalization;

namespace SharpInspect.Abstractions;

public enum EvidenceRetentionOwnerKind : byte { ImageManifest = 1, QuarantinedFile = 2 }
public enum EvidenceRetentionEventKind : byte
{
    ObligationEstablished = 1, Extended = 2, HoldPlaced = 3, HoldReleased = 4,
    DeleteIntent = 5, DeleteFailed = 6, DeleteOutcomeUnknown = 7, Tombstone = 8
}
public enum EvidenceRetentionDisposition : byte { Retained = 1, Held = 2, Deleting = 3, DeleteUnknown = 4, Deleted = 5 }
public enum EvidenceRetentionChange : byte { PlaceHold = 1, ReleaseHold = 2, Extend = 3 }

/// <summary>An exact internal-owner identity. There is no caller-supplied path or production-reference inference.</summary>
public sealed record EvidenceRetentionOwner(EvidenceRetentionOwnerKind Kind, Guid OwnerId);

/// <summary>The immutable, source-verified retention rule. Policy changes cannot shorten this value.</summary>
public sealed record EvidenceRetentionObligation(EvidenceRetentionOwner Owner,
    TraceRetentionClass EvidenceClass, Guid? InspectionId, string SourceContentHash, long SourceAuditSequence,
    string ArtifactContentHash, string PolicySnapshotHash, string PublicationHash, string RuleHash,
    RetentionStartEvent StartsAt, DateTimeOffset StartedAtUtc, DateTimeOffset RetainUntilUtc,
    string RootBindingHash, string FileName, long ByteLength, string ContentHash);

/// <summary>Identity captured from the exact opened file before committing a deletion intent.</summary>
public sealed record EvidenceDeletionFile(string RootBindingHash, string FileName,
    string FileIdentityHash, long ByteLength, string RawContentHash);

public sealed record EvidenceRetentionRecord(long Position, Guid EventId, Guid OperationId,
    Guid RuntimeEpoch, EvidenceRetentionEventKind Kind, EvidenceRetentionOwner Owner,
    long AggregateSequence, string? PreviousContentHash, string ObligationHash, DateTimeOffset RecordedAtUtc,
    string ReasonCode, string SystemPrincipalId, Guid? HumanPrincipalId, Guid? HoldId,
    DateTimeOffset? ExtendedUntilUtc, EvidenceDeletionFile? File, long AuditSequence, string ContentHash,
    string? Reason, Guid? SessionId, Guid? StepUpGrantId);

public sealed record EvidenceRetentionStatus(EvidenceRetentionObligation Obligation,
    long Revision, string RevisionHash, EvidenceRetentionDisposition Disposition,
    DateTimeOffset EffectiveUntilUtc, IReadOnlyList<Guid> ActiveHolds,
    Guid? DeleteOperationId, string? TombstoneHash, string ReasonCode, int DeleteAttempts);

public sealed record EvidenceRetentionFilter(EvidenceRetentionOwner? Owner = null, long AfterPosition = 0,
    long? ThroughPosition = null, int PageSize = 50);

public sealed record EvidenceRetentionSnapshot(bool Available, string ReasonCode,
    IReadOnlyList<EvidenceRetentionRecord> Records, IReadOnlyList<EvidenceRetentionStatus> Subjects,
    long ThroughPosition, long? NextAfterPosition, long ActiveHolds, long PendingDeletes,
    long UnknownDeletes, long DeletedFiles, long DeletedLogicalBytes, TraceRetentionExecutionPolicy? ExecutionPolicy = null,
    TraceStorageRecoveryBudget? RecoveryBudget = null, long? ControlFactsUsed = null);

public interface IEvidenceRetentionQuery
{
    ValueTask<EvidenceRetentionSnapshot> ReadAsync(EvidenceRetentionFilter filter,
        CancellationToken cancellationToken = default);
}

public sealed record ChangeEvidenceRetentionCommand : RuntimeCommand
{
    public ChangeEvidenceRetentionCommand(Guid correlationId, CommandInvocation invocation,
        EvidenceRetentionOwner owner, long expectedRevision, EvidenceRetentionChange change,
        Guid? holdId, DateTimeOffset? extendedUntilUtc, string reason) : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("RetentionCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(owner);
        if (!Enum.IsDefined(owner.Kind) || owner.OwnerId == Guid.Empty)
            throw new ArgumentException("RetentionOwnerInvalid", nameof(owner));
        if (expectedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!Enum.IsDefined(change)) throw new ArgumentOutOfRangeException(nameof(change));
        if ((change is EvidenceRetentionChange.PlaceHold or EvidenceRetentionChange.ReleaseHold) !=
                (holdId is not null) || holdId == Guid.Empty ||
            (change == EvidenceRetentionChange.Extend) != (extendedUntilUtc is not null) ||
            extendedUntilUtc is { } until && (until == default || until.Offset != TimeSpan.Zero))
            throw new ArgumentException("RetentionChangePayloadInvalid");
        Owner = owner;
        ExpectedRevision = expectedRevision;
        Change = change;
        HoldId = holdId;
        ExtendedUntilUtc = extendedUntilUtc;
        Reason = TraceStoragePolicyValidation.Text(reason, nameof(reason), 512);
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-retention-command-v1", Owner.Kind.ToString(), Owner.OwnerId.ToString("D"),
            ExpectedRevision.ToString(CultureInfo.InvariantCulture), Change.ToString(),
            HoldId?.ToString("D"), ExtendedUntilUtc?.ToString("O", CultureInfo.InvariantCulture), Reason
        });
    }

    public EvidenceRetentionOwner Owner { get; }
    public long ExpectedRevision { get; }
    public EvidenceRetentionChange Change { get; }
    public Guid? HoldId { get; }
    public DateTimeOffset? ExtendedUntilUtc { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
    public override string ToString() => nameof(ChangeEvidenceRetentionCommand);
}

public sealed record EvidenceRetentionAccess(bool Allowed, string ReasonCode, bool RequiresStepUp);
public sealed record EvidenceRetentionResult(RuntimeCommandOutcome Outcome, EvidenceRetentionStatus? Status)
{
    public bool Succeeded => Outcome.Disposition == CommandDisposition.Accepted &&
        Outcome.Audit == AuditPersistence.Persisted && Status is not null;
}

public interface IEvidenceRetentionService : IEvidenceRetentionQuery
{
    ValueTask<EvidenceRetentionAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<EvidenceRetentionResult> ChangeAsync(ChangeEvidenceRetentionCommand command,
        CancellationToken cancellationToken = default);
}
