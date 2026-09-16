using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

internal sealed class EvidenceRetentionReplay
{
    internal sealed class SubjectState
    {
        internal SubjectState(EvidenceRetentionObligation obligation)
        { Obligation = obligation; EffectiveUntilUtc = obligation.RetainUntilUtc; }
        internal EvidenceRetentionObligation Obligation { get; }
        internal long Revision;
        internal string RevisionHash = string.Empty;
        internal DateTimeOffset LastRecordedAtUtc;
        internal DateTimeOffset EffectiveUntilUtc;
        internal Dictionary<Guid, EvidenceRetentionStoredRow> ActiveHolds { get; } = new();
        internal HashSet<Guid> UsedHolds { get; } = new();
        internal EvidenceRetentionStoredRow? DeleteIntent;
        internal bool DeleteUnknown;
        internal int DeleteAttempts;
        internal EvidenceRetentionStoredRow? Tombstone;
        internal string ReasonCode = "RetentionObligationEstablished";
        internal EvidenceRetentionDisposition Disposition => Tombstone is not null ? EvidenceRetentionDisposition.Deleted :
            DeleteUnknown ? EvidenceRetentionDisposition.DeleteUnknown :
            DeleteIntent is not null ? EvidenceRetentionDisposition.Deleting :
            ActiveHolds.Count != 0 ? EvidenceRetentionDisposition.Held : EvidenceRetentionDisposition.Retained;
        internal EvidenceRetentionStatus ToStatus() => new(Obligation, Revision, RevisionHash, Disposition,
            EffectiveUntilUtc, Array.AsReadOnly(ActiveHolds.Keys.OrderBy(x => x).ToArray()),
            DeleteIntent?.Payload.OperationId ?? Tombstone?.Payload.OperationId, Tombstone?.ContentHash, ReasonCode, DeleteAttempts);
    }

    internal Dictionary<EvidenceRetentionOwner, SubjectState> Subjects { get; } = new();
    private readonly HashSet<Guid> _events = new();
    private readonly HashSet<Guid> _operations = new();
    internal long LastPosition { get; private set; }
    internal long LastAuditSequence { get; private set; }
    internal DateTimeOffset LastRecordedAtUtc { get; private set; }
    internal long FutureReserve => Subjects.Values.Sum(x =>
        x.DeleteIntent is null || x.Tombstone is not null ? 0L : x.DeleteUnknown ? 1L : 2L);

    internal void Apply(EvidenceRetentionStoredRow row)
    {
        var value = row.Payload;
        EvidenceRetentionCodec.Validate(value);
        void Require(bool valid, string reason) => EvidenceRetentionCodec.Require(valid, reason);
        Require(row.Position == LastPosition + 1 && row.AuditSequence > LastAuditSequence &&
            EvidenceRetentionCodec.Hash(row.ContentHash) && EvidenceRetentionCodec.Hash(row.AuditHash) &&
            _events.Add(value.EventId), "EventSequenceInvalid");
        var establishes = value.Kind == EvidenceRetentionEventKind.ObligationEstablished;
        if (establishes)
        {
            Require(!Subjects.ContainsKey(value.Owner) && value.AggregateSequence == 1 &&
                value.PreviousContentHash is null && _operations.Add(value.OperationId),
                "ObligationAlreadyEstablished");
            var obligation = value.EstablishedObligation!;
            Require(value.RecordedAtUtc >= obligation.StartedAtUtc && row.AuditSequence > obligation.SourceAuditSequence,
                "ObligationSourceOrderInvalid");
            Subjects.Add(value.Owner, new SubjectState(obligation));
        }
        Require(Subjects.TryGetValue(value.Owner, out var state), "ObligationMissing");
        var current = state!;
        Require(value.ObligationHash == current.Obligation.ContentHash &&
            value.AggregateSequence == current.Revision + 1 &&
            value.PreviousContentHash == (current.Revision == 0 ? null : current.RevisionHash), "SubjectRevisionInvalid");
        if (value.Kind is EvidenceRetentionEventKind.Extended or EvidenceRetentionEventKind.HoldPlaced or
            EvidenceRetentionEventKind.HoldReleased)
        {
            Require(current.DeleteIntent is null && current.Tombstone is null, "DeletionAlreadyStarted");
            Require(_operations.Add(value.OperationId), "OperationAlreadyRecorded");
        }
        switch (value.Kind)
        {
            case EvidenceRetentionEventKind.ObligationEstablished: break;
            case EvidenceRetentionEventKind.Extended:
                Require(value.ExtendedUntilUtc > current.EffectiveUntilUtc, "ExtensionMustIncrease");
                current.EffectiveUntilUtc = value.ExtendedUntilUtc!.Value;
                break;
            case EvidenceRetentionEventKind.HoldPlaced:
                Require(current.UsedHolds.Add(value.HoldId!.Value), "HoldIdentityReused");
                current.ActiveHolds.Add(value.HoldId.Value, row);
                break;
            case EvidenceRetentionEventKind.HoldReleased:
                Require(current.ActiveHolds.Remove(value.HoldId!.Value), "HoldNotActive");
                break;
            case EvidenceRetentionEventKind.DeleteIntent:
                Require(current.DeleteIntent is null && current.Tombstone is null &&
                    current.ActiveHolds.Count == 0 && value.RecordedAtUtc >= LastRecordedAtUtc &&
                    value.RecordedAtUtc >= current.EffectiveUntilUtc &&
                    _operations.Add(value.OperationId), "DeletionNotEligible");
                Require(value.File!.RootBindingHash == current.Obligation.RootBindingHash &&
                    value.File.FileName == current.Obligation.FileName &&
                    value.File.ByteLength == current.Obligation.ByteLength, "DeletionFileBindingMismatch");
                current.DeleteIntent = row;
                current.DeleteAttempts = checked(current.DeleteAttempts + 1);
                current.DeleteUnknown = false;
                break;
            case EvidenceRetentionEventKind.DeleteFailed:
            case EvidenceRetentionEventKind.DeleteOutcomeUnknown:
            case EvidenceRetentionEventKind.Tombstone:
                Require(current.DeleteIntent is { } intent && intent.Payload.OperationId == value.OperationId &&
                    intent.Payload.File == value.File && current.Tombstone is null && current.ActiveHolds.Count == 0,
                    "DeletionIntentMismatch");
                if (value.Kind == EvidenceRetentionEventKind.DeleteFailed)
                { current.DeleteIntent = null; current.DeleteUnknown = false; }
                else if (value.Kind == EvidenceRetentionEventKind.DeleteOutcomeUnknown)
                { Require(!current.DeleteUnknown, "DeletionUnknownAlreadyRecorded"); current.DeleteUnknown = true; }
                else
                { current.Tombstone = row; current.DeleteIntent = null; current.DeleteUnknown = false; }
                break;
            default: throw new InvalidOperationException("RetentionKindInvalid");
        }
        current.Revision = value.AggregateSequence;
        current.RevisionHash = row.ContentHash;
        if (value.RecordedAtUtc > current.LastRecordedAtUtc) current.LastRecordedAtUtc = value.RecordedAtUtc;
        current.ReasonCode = value.ReasonCode;
        LastPosition = row.Position;
        LastAuditSequence = row.AuditSequence;
        if (value.RecordedAtUtc > LastRecordedAtUtc) LastRecordedAtUtc = value.RecordedAtUtc;
    }
}
