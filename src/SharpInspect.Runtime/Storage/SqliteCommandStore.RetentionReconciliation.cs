using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static void RequireRetentionReconciliationFact(sqlite3 database, EvidenceReconciliationStoredRow row,
        StoreDeadline deadline)
    {
        if (row.Payload.ReasonCode != "RetainedImageTombstoneVerified") return;
        var subject = row.Payload.Subject;
        EvidenceRetentionCodec.Require(row.Payload.Kind == EvidenceReconciliationEventKind.WorkDeferred &&
            subject?.Kind == EvidenceReconciliationSubjectKind.Image &&
            AuditChainDatabase.TableExists(database, "evidence_retention_events", deadline), "ReconciliationTombstoneRequired");
        var tombstones = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM evidence_retention_events
            WHERE OwnerKind=1 AND OwnerId=? AND Kind=8 AND AuditSequence<?;", deadline,
            subject!.ManifestId!.Value.ToString("D"), RetentionNumber(row.AuditSequence));
        EvidenceRetentionCodec.Require(tombstones == 1, "ReconciliationTombstoneMissing");
    }
}
