using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static IReadOnlyList<EvidenceReconciliationSubject> ReadReconciliationSources(sqlite3 database,
        EvidenceReconciliationConfiguration configuration, EvidenceReconciliationSubjectKind kind,
        long after, long through, long beforeAuditSequence, int limit, StoreDeadline deadline, long? startupAuditSequence = null)
    {
        var image = kind == EvidenceReconciliationSubjectKind.Image;
        if (image ? configuration.FinalizationHash is null : configuration.OutboxHash is null)
            return Array.Empty<EvidenceReconciliationSubject>();
        var sql = image ? @"SELECT c.Position,m.InspectionId,m.ManifestId,w.WorkId,m.ContentHash
            FROM pending_image_manifests m JOIN pending_image_work w ON w.ManifestId=m.ManifestId
            JOIN production_inspection_events c ON c.InspectionId=m.InspectionId
            WHERE c.Kind=? AND c.Position>? AND c.Position<=? AND c.AuditSequence<?
            ORDER BY c.Position,w.WorkId LIMIT ?;"
            : @"SELECT Position,InspectionId,NULL,DeliveryId,ContentHash
            FROM production_outbox_deliveries WHERE Position>? AND Position<=? AND CreatedAuditSequence<?
            ORDER BY Position LIMIT ?;";
        var args = new List<string?>();
        if (image) args.Add(ReconciliationNumber((long)ProductionInspectionEventKind.CoreCommitted));
        args.AddRange(new[] { ReconciliationNumber(after), ReconciliationNumber(through),
            ReconciliationNumber(beforeAuditSequence) });
        if (startupAuditSequence is { } startSequence)
        {
            var created = image ? "c.AuditSequence" : "CreatedAuditSequence";
            var ended = image
                ? "SELECT 1 FROM image_finalization_events f WHERE f.WorkId=w.WorkId AND f.Kind='StageReleased' AND f.AuditSequence<?"
                : "SELECT 1 FROM production_outbox_events f WHERE f.DeliveryId=production_outbox_deliveries.DeliveryId AND f.Kind='Succeeded' AND f.AuditSequence<?";
            var order = image ? "ORDER BY c.Position" : "ORDER BY Position";
            sql = sql.Replace(order, "AND (" + created + ">? OR NOT EXISTS(" + ended +
                ") OR NOT EXISTS(" + ended + ")) " + order, StringComparison.Ordinal);
            args.AddRange(new[] { ReconciliationNumber(startSequence), ReconciliationNumber(startSequence),
                ReconciliationNumber(beforeAuditSequence) });
        }
        args.Add(ReconciliationNumber(limit));
        return AuditChainDatabase.Read(database, sql, deadline, row =>
        {
            var id = Guid.Parse(SqliteNative.ColumnText(row, 3)!);
            return new EvidenceReconciliationSubject(kind, SqliteNative.ColumnInt64(row, 0),
                Guid.Parse(SqliteNative.ColumnText(row, 1)!),
                image ? Guid.Parse(SqliteNative.ColumnText(row, 2)!) : null,
                image ? id : null, image ? null : id, null, SqliteNative.ColumnText(row, 4),
                ReadEvidenceReconciliationSourceRevision(database, configuration, kind, id, deadline, beforeAuditSequence),
                null, null, null, null, null, null, null, null);
        }, args.ToArray());
    }

    internal static long ReconciliationSourceTail(sqlite3 database, EvidenceReconciliationConfiguration configuration,
        EvidenceReconciliationSubjectKind kind, long beforeAuditSequence, StoreDeadline deadline)
    {
        if (kind == EvidenceReconciliationSubjectKind.Image)
        {
            if (configuration.FinalizationHash is null) return 0;
            return AuditChainDatabase.Scalar(database, @"SELECT COALESCE(MAX(c.Position),0)
                FROM production_inspection_events c JOIN pending_image_work w ON w.InspectionId=c.InspectionId
                WHERE c.Kind=? AND c.AuditSequence<?;", deadline,
                ReconciliationNumber((long)ProductionInspectionEventKind.CoreCommitted), ReconciliationNumber(beforeAuditSequence));
        }
        if (configuration.OutboxHash is null) return 0;
        return AuditChainDatabase.Scalar(database, @"SELECT COALESCE(MAX(Position),0)
            FROM production_outbox_deliveries WHERE CreatedAuditSequence<?;", deadline, ReconciliationNumber(beforeAuditSequence));
    }

    private static Guid ReconciliationSubjectId(EvidenceReconciliationSubject subject) =>
        subject.WorkId ?? subject.DeliveryId!.Value;

    // Both the writer and a cold read prove exact source membership. A signed cursor alone
    // is not coverage, and the observation's historical CAS is not a current startup CAS.
    internal static void RequireReconciliationCoverage(sqlite3 database,
        EvidenceReconciliationConfiguration configuration, IReadOnlyList<EvidenceReconciliationStoredRow> rows,
        StoreDeadline deadline, long verifiedThroughPosition = 0, bool writerBatch = false)
    {
        var affected = rows.Where(x => x.Position > verifiedThroughPosition).Select(x => x.Payload.RunId).ToHashSet();
        foreach (var group in rows.Where(x => affected.Contains(x.Payload.RunId)).GroupBy(x => x.Payload.RunId))
        {
            var run = group.ToArray();
            var start = run[0];
            var historical = start.Payload.Phase == EvidenceReconciliationPhase.HistoricalScrub;
            var kind = start.Payload.Stream == EvidenceReconciliationStream.Images
                ? EvidenceReconciliationSubjectKind.Image : EvidenceReconciliationSubjectKind.Outbox;
            if (historical && start.Position > verifiedThroughPosition)
                AuditChainDatabase.Require(start.Payload.ThroughSourcePosition ==
                    ReconciliationSourceTail(database, configuration, kind, start.AuditSequence, deadline),
                    writerBatch ? "EvidenceReconciliationSourceTailChangedBeforeStart" : "EvidenceReconciliationFrozenTailInvalid");
            long previousAfter = 0;
            var page = new Dictionary<Guid, EvidenceReconciliationSubject>();
            var startupLatest = new Dictionary<Guid, EvidenceReconciliationSubject>();
            foreach (var row in run.Skip(1))
            {
                var fact = row.Payload;
                if (fact.Subject is { Kind: not EvidenceReconciliationSubjectKind.Orphan } subject)
                {
                    var id = ReconciliationSubjectId(subject);
                    page.Add(id, subject);
                    startupLatest[id] = subject;
                }
                if (fact.Kind is not (EvidenceReconciliationEventKind.PageCompleted or EvidenceReconciliationEventKind.RunCompleted))
                    continue;
                if (historical && row.Position > verifiedThroughPosition)
                {
                    var expected = ReadReconciliationSources(database, configuration, kind, previousAfter,
                        fact.AfterSourcePosition, start.AuditSequence, 513, deadline);
                    AuditChainDatabase.Require(expected.Count <= 512 && expected.Count == page.Count &&
                        expected.All(x => page.ContainsKey(ReconciliationSubjectId(x))),
                        "EvidenceReconciliationSourceCoverageIncomplete");
                    previousAfter = fact.AfterSourcePosition;
                }
                else if (!historical && fact.Kind == EvidenceReconciliationEventKind.RunCompleted && row.Position > verifiedThroughPosition)
                {
                    foreach (var sourceKind in new[] { EvidenceReconciliationSubjectKind.Image, EvidenceReconciliationSubjectKind.Outbox })
                    {
                        long after = 0;
                        while (true)
                        {
                            var sources = ReadReconciliationSources(database, configuration, sourceKind, after,
                                long.MaxValue, row.AuditSequence, 512, deadline, start.AuditSequence);
                            foreach (var source in sources)
                                AuditChainDatabase.Require(startupLatest.TryGetValue(ReconciliationSubjectId(source), out var seen) &&
                                    seen.Kind == source.Kind && seen.ObservedLifecycleHash == source.ObservedLifecycleHash,
                                    "EvidenceReconciliationStartupCoverageIncomplete");
                            if (sources.Count < 512) break;
                            after = sources[^1].SourcePosition!.Value;
                        }
                    }
                    if (configuration.OutboxHash is not null)
                        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, @"
                            SELECT COUNT(*) FROM production_outbox_events e WHERE e.Kind='AttemptStarted'
                            AND e.AuditSequence<? AND NOT EXISTS(SELECT 1 FROM production_outbox_events later
                                WHERE later.DeliveryId=e.DeliveryId AND later.AuditSequence>e.AuditSequence
                                AND later.AuditSequence<?);", deadline, ReconciliationNumber(row.AuditSequence),
                                ReconciliationNumber(row.AuditSequence)) == 0,
                            "EvidenceReconciliationStartupCoverageIncomplete");
                    AuditChainDatabase.Require(!rows.Any(x => x.AuditSequence < row.AuditSequence &&
                        x.Payload.Kind == EvidenceReconciliationEventKind.IntegrityFault),
                        "EvidenceReconciliationFaultCannotCompleteStartup");
                }
                if (historical) previousAfter = fact.AfterSourcePosition;
                page.Clear();
            }
        }
    }

    private static void RequireReconciliationOrphanUnreferenced(sqlite3 database,
        EvidenceReconciliationSubject subject, long? beforeAuditSequence, StoreDeadline deadline)
    {
        var upper = ReconciliationNumber(beforeAuditSequence ?? long.MaxValue);
        if (subject.FileArea == EvidenceReconciliationFileArea.Stage)
        {
            var manifests = AuditChainDatabase.Read(database, @"SELECT m.Payload FROM pending_image_manifests m
                JOIN production_inspection_events c ON c.InspectionId=m.InspectionId
                WHERE c.Kind=? AND c.AuditSequence<?;", deadline,
                row => ProductionInspectionStorageCodec.DecodeImageManifest(Convert.FromBase64String(SqliteNative.ColumnText(row, 0)!)),
                ReconciliationNumber((long)ProductionInspectionEventKind.CoreCommitted), upper);
            AuditChainDatabase.Require(!manifests.Any(m => string.Equals(m.StageFileName,
                subject.SourceFileName, StringComparison.OrdinalIgnoreCase)), "EvidenceReconciliationOrphanAlreadyReferenced");
        }
        else
        {
            var references = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM image_finalization_events
                WHERE Kind='AttemptStarted' AND AuditSequence<? AND
                (lower(replace(ManifestId,'-','')) || '.png' = ? COLLATE NOCASE OR
                 lower(replace(ManifestId,'-','')) || '.' || lower(replace(AttemptId,'-','')) || '.tmp' = ? COLLATE NOCASE);",
                deadline, upper, subject.SourceFileName, subject.SourceFileName);
            AuditChainDatabase.Require(references == 0, "EvidenceReconciliationOrphanAlreadyReferenced");
        }
    }
}
