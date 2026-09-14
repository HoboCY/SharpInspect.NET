using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Only committed, hash-bound checkpoints advance a run. An interrupted page is replayable.</summary>
internal sealed class EvidenceReconciliationReplay
{
    private readonly Dictionary<Guid, Run> _runs = new();
    private readonly Dictionary<Guid, EvidenceReconciliationStoredRow> _intents = new();
    private readonly HashSet<Guid> _eventIds = new();
    private readonly HashSet<Guid> _quarantineFaults = new();
    private long _position;
    private long _auditSequence;

    internal bool IntegrityFault { get; private set; }
    internal IReadOnlyDictionary<Guid, EvidenceReconciliationStoredRow> PendingQuarantines => _intents;
    internal IEnumerable<Run> Runs => _runs.Values;
    internal long QuarantineReserveAfter(EvidenceReconciliationPayload fact)
    {
        var reserve = _intents.Keys.Sum(id => _quarantineFaults.Contains(id) ? 1L : 2L);
        if (fact.Subject?.OrphanId is not { } id) return reserve;
        return reserve + (fact.Kind == EvidenceReconciliationEventKind.QuarantineIntent ? 2 :
            fact.Kind == EvidenceReconciliationEventKind.Quarantined ? (_quarantineFaults.Contains(id) ? -1 : -2) :
            fact.Kind == EvidenceReconciliationEventKind.IntegrityFault && _intents.ContainsKey(id) &&
                !_quarantineFaults.Contains(id) ? -1 : 0);
    }

    internal void Apply(EvidenceReconciliationStoredRow row)
    {
        var fact = row.Payload;
        EvidenceReconciliationStorageCodec.Validate(fact);
        Require(row.Position == _position + 1 && row.AuditSequence > _auditSequence &&
            EvidenceReconciliationStorageCodec.IsHash(row.ContentHash) &&
            EvidenceReconciliationStorageCodec.IsHash(row.AuditHash) && _eventIds.Add(fact.EventId),
            "SequenceInvalid");
        _position = row.Position;
        _auditSequence = row.AuditSequence;
        if (fact.Kind == EvidenceReconciliationEventKind.RunStarted)
        {
            Require(!_runs.ContainsKey(fact.RunId), "RunAlreadyStarted");
            // A new runtime epoch may replace a crashed startup, but a historical stream
            // resumes its existing unfinished pass instead of silently skipping its cursor.
            Require(!_runs.Values.Any(x => !x.Completed && x.Phase == fact.Phase &&
                (fact.Phase == EvidenceReconciliationPhase.HistoricalScrub
                    ? x.Stream == fact.Stream : x.RuntimeEpoch == fact.RuntimeEpoch)), "RunAlreadyActive");
            _runs.Add(fact.RunId, new Run(row));
            return;
        }
        Require(_runs.TryGetValue(fact.RunId, out var run), "RunMissing");
        Require(!run!.Completed && fact.Phase == run.Phase && fact.Stream == run.Stream &&
            fact.ThroughSourcePosition == run.ThroughSourcePosition &&
            (run.Phase != EvidenceReconciliationPhase.Startup || fact.RuntimeEpoch == run.RuntimeEpoch),
            "RunBindingInvalid");
        Require(fact.RecordedAtUtc >= run.LastRecordedAtUtc, "ClockRegression");
        if (fact.Kind is EvidenceReconciliationEventKind.PageCompleted or EvidenceReconciliationEventKind.RunCompleted)
        {
            Require(fact.PreviousCheckpointHash == run.CheckpointHash &&
                fact.AfterSourcePosition >= run.AfterSourcePosition &&
                fact.ScannedItems == run.PageScanned && fact.VerifiedItems == run.PageVerified &&
                fact.DeferredItems == run.PageDeferred && fact.VerifiedBytes == run.PageBytes,
                "CheckpointMismatch");
            if (run.Phase == EvidenceReconciliationPhase.HistoricalScrub)
            {
                Require(run.PageMaximumSourcePosition <= fact.AfterSourcePosition &&
                    (fact.AfterSourcePosition > run.AfterSourcePosition ||
                     fact.Kind == EvidenceReconciliationEventKind.RunCompleted &&
                     fact.AfterSourcePosition == run.ThroughSourcePosition), "CursorDidNotAdvance");
                if (fact.Kind == EvidenceReconciliationEventKind.RunCompleted)
                    Require(fact.AfterSourcePosition == run.ThroughSourcePosition, "RunIncomplete");
            }
            if (fact.Kind == EvidenceReconciliationEventKind.RunCompleted &&
                run.Phase == EvidenceReconciliationPhase.Startup)
                Require(_intents.Count == 0, "QuarantineCompletionMissing");
            run.Checkpoint(row);
            return;
        }

        var subject = fact.Subject!;
        Require(fact.AfterSourcePosition == run.AfterSourcePosition, "ObservationCursorMismatch");
        if (subject.Kind == EvidenceReconciliationSubjectKind.Orphan)
        {
            Require(run.Phase == EvidenceReconciliationPhase.Startup, "QuarantinePhaseInvalid");
            var orphanId = subject.OrphanId!.Value;
            if (fact.Kind == EvidenceReconciliationEventKind.QuarantineIntent)
            {
                Require(!_intents.ContainsKey(orphanId) && !_runs.Values.Any(x => x.CompletedOrphans.Contains(orphanId)),
                    "QuarantineIntentDuplicate");
                _intents.Add(orphanId, row);
            }
            else if (fact.Kind == EvidenceReconciliationEventKind.Quarantined)
            {
                Require(_intents.TryGetValue(orphanId, out var intent) && intent.Payload.Subject == subject,
                    "QuarantineCompletionBindingInvalid");
                _intents.Remove(orphanId);
                run.CompletedOrphans.Add(orphanId);
            }
            else if (fact.Kind == EvidenceReconciliationEventKind.IntegrityFault && _intents.ContainsKey(orphanId))
                Require(_quarantineFaults.Add(orphanId), "QuarantineFaultAlreadyRecorded");
        }
        else
        {
            if (run.Phase == EvidenceReconciliationPhase.HistoricalScrub)
                Require(subject.SourcePosition > run.AfterSourcePosition &&
                    subject.SourcePosition <= run.ThroughSourcePosition &&
                    (run.Stream == EvidenceReconciliationStream.Images
                        ? subject.Kind == EvidenceReconciliationSubjectKind.Image
                        : subject.Kind == EvidenceReconciliationSubjectKind.Outbox), "ObservationOutsidePage");
            var identity = subject.WorkId ?? subject.DeliveryId!.Value;
            Require(run.PageSubjects.Add(identity), "PageSubjectDuplicate");
            run.PageMaximumSourcePosition = Math.Max(run.PageMaximumSourcePosition, subject.SourcePosition!.Value);
            run.PageScanned++;
            if (fact.Kind is EvidenceReconciliationEventKind.ImageVerified or
                EvidenceReconciliationEventKind.ImageFinalRecovered or EvidenceReconciliationEventKind.OutboxVerified)
            {
                run.PageVerified++;
                run.PageBytes = checked(run.PageBytes + (subject.ByteLength ?? 0));
            }
            else if (fact.Kind == EvidenceReconciliationEventKind.WorkDeferred) run.PageDeferred++;
        }
        if (fact.Kind == EvidenceReconciliationEventKind.IntegrityFault)
        {
            IntegrityFault = true;
            run.IntegrityFault = true;
        }
        run.Observe(row);
    }

    internal Run? Latest(EvidenceReconciliationPhase phase) => _runs.Values.Where(x => x.Phase == phase)
        .OrderByDescending(x => x.LastEventPosition).FirstOrDefault();

    internal sealed class Run
    {
        internal Run(EvidenceReconciliationStoredRow start)
        {
            var fact = start.Payload;
            RunId = fact.RunId; RuntimeEpoch = fact.RuntimeEpoch; Phase = fact.Phase; Stream = fact.Stream;
            ThroughSourcePosition = fact.ThroughSourcePosition;
            CheckpointHash = start.ContentHash;
            Observe(start);
        }
        internal Guid RunId { get; }
        internal Guid RuntimeEpoch { get; }
        internal EvidenceReconciliationPhase Phase { get; }
        internal EvidenceReconciliationStream? Stream { get; }
        internal long ThroughSourcePosition { get; }
        internal long AfterSourcePosition { get; private set; }
        internal long ScannedItems { get; private set; }
        internal long VerifiedItems { get; private set; }
        internal long DeferredItems { get; private set; }
        internal long VerifiedBytes { get; private set; }
        internal string CheckpointHash { get; private set; }
        internal bool Completed { get; private set; }
        internal bool IntegrityFault { get; set; }
        internal long LastEventPosition { get; private set; }
        internal string LastEventContentHash { get; private set; } = string.Empty;
        internal DateTimeOffset LastRecordedAtUtc { get; private set; }
        internal HashSet<Guid> CompletedOrphans { get; } = new();
        internal HashSet<Guid> PageSubjects { get; } = new();
        internal long PageScanned { get; set; }
        internal long PageVerified { get; set; }
        internal long PageDeferred { get; set; }
        internal long PageBytes { get; set; }
        internal long PageMaximumSourcePosition { get; set; }

        internal void Observe(EvidenceReconciliationStoredRow row)
        {
            LastEventPosition = row.Position; LastEventContentHash = row.ContentHash;
            LastRecordedAtUtc = row.Payload.RecordedAtUtc;
        }
        internal void Checkpoint(EvidenceReconciliationStoredRow row)
        {
            ScannedItems = checked(ScannedItems + PageScanned);
            VerifiedItems = checked(VerifiedItems + PageVerified);
            DeferredItems = checked(DeferredItems + PageDeferred);
            VerifiedBytes = checked(VerifiedBytes + PageBytes);
            PageScanned = PageVerified = PageDeferred = PageBytes = PageMaximumSourcePosition = 0;
            PageSubjects.Clear();
            AfterSourcePosition = row.Payload.AfterSourcePosition;
            CheckpointHash = row.ContentHash;
            Completed = row.Payload.Kind == EvidenceReconciliationEventKind.RunCompleted;
            Observe(row);
        }
        internal EvidenceReconciliationProgress Project() => new(RunId, Phase, ThroughSourcePosition,
            AfterSourcePosition, ScannedItems, VerifiedItems, DeferredItems, VerifiedBytes,
            Completed, IntegrityFault, LastEventPosition, LastEventContentHash, LastRecordedAtUtc);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("EvidenceReconciliation" + reason);
    }
}
