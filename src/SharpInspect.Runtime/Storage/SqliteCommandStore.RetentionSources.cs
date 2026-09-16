using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static EvidenceRetentionObligation? ReadImageRetentionObligation(sqlite3 database, Guid manifestId,
        StoreDeadline deadline, long beforeAuditSequence = long.MaxValue)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT m.Payload,m.ContentHash,c.AdmissionContentHash,
            a.TracePolicyHash,c.AuditSequence,s.Payload,s.ContentHash,s.AuditSequence
            FROM pending_image_manifests m
            JOIN production_inspection_cores c ON c.InspectionId=m.InspectionId
            JOIN production_inspection_admissions a ON a.InspectionId=m.InspectionId
            JOIN image_finalization_events s ON s.ManifestId=m.ManifestId AND s.Kind='Succeeded'
            WHERE m.ManifestId=? AND c.AuditSequence<? AND s.AuditSequence<? LIMIT 2;", deadline,
            statement => new
            {
                Manifest = SqliteNative.ColumnText(statement, 0)!, Hash = SqliteNative.ColumnText(statement, 1)!,
                Admission = SqliteNative.ColumnText(statement, 2)!, Policy = SqliteNative.ColumnText(statement, 3)!,
                CoreAudit = SqliteNative.ColumnInt64(statement, 4), Success = SqliteNative.ColumnText(statement, 5)!,
                SuccessHash = SqliteNative.ColumnText(statement, 6)!, SuccessAudit = SqliteNative.ColumnInt64(statement, 7)
            }, manifestId.ToString("D"), RetentionNumber(beforeAuditSequence), RetentionNumber(beforeAuditSequence));
        if (rows.Count == 0) return null;
        EvidenceRetentionCodec.Require(rows.Count == 1, "ImageSourceDuplicate");
        var row = rows[0];
        var manifest = ProductionInspectionStorageCodec.DecodeImageManifest(Convert.FromBase64String(row.Manifest));
        var succeeded = ProductionImageFinalizationStorageCodec.DecodeEvent(Convert.FromBase64String(row.Success));
        EvidenceRetentionCodec.Require(manifest.ManifestId == manifestId && manifest.ContentHash == row.Hash &&
            manifest.AdmissionContentHash == row.Admission && manifest.TracePolicySnapshotHash == row.Policy &&
            succeeded.Kind == ProductionImageFinalizationKind.Succeeded && succeeded.Success is not null &&
            succeeded.ContentHash == row.SuccessHash && succeeded.AuditSequence == row.SuccessAudit &&
            succeeded.ManifestContentHash == manifest.ContentHash && succeeded.ManifestId == manifest.ManifestId &&
            succeeded.InspectionId == manifest.InspectionId && row.CoreAudit < row.SuccessAudit,
            "ImageSourceBindingMismatch");
        var snapshot = ReadRetentionPolicy(database, row.Policy, row.CoreAudit, deadline);
        var rule = snapshot.RetentionRules.Single(x => x.EvidenceClass == TraceRetentionClass.AuthoritativeImage);
        EvidenceRetentionCodec.Require(rule.ContentHash == manifest.RetentionRuleHash, "FrozenRuleMismatch");
        var start = rule.StartsAt switch
        {
            RetentionStartEvent.ArtifactCreated => manifest.CreatedAtUtc,
            RetentionStartEvent.Finalized => succeeded.RecordedAtUtc,
            _ => throw new InvalidOperationException("RetentionImageStartUnsupported")
        };
        var success = succeeded.Success!;
        return CreateRetentionObligation(new(EvidenceRetentionOwnerKind.ImageManifest, manifest.ManifestId),
            rule.EvidenceClass, manifest.InspectionId, manifest.ContentHash, row.SuccessAudit,
            manifest.CanonicalPixelHash, snapshot, rule, start, success.FinalRootBindingHash,
            success.FinalFileName, success.EncodedByteLength);
    }

    internal static EvidenceRetentionObligation? ReadQuarantineRetentionObligation(sqlite3 database, Guid orphanId,
        StoreDeadline deadline, long beforeAuditSequence = long.MaxValue)
    {
        var source = AuditChainDatabase.Read(database, @"SELECT Payload,ContentHash,AuditSequence
            FROM evidence_reconciliation_events WHERE OrphanId=? AND Kind=6 AND AuditSequence<? LIMIT 2;",
            deadline, statement => new
            {
                Payload = SqliteNative.ColumnText(statement, 0)!, Hash = SqliteNative.ColumnText(statement, 1)!,
                Audit = SqliteNative.ColumnInt64(statement, 2)
            }, orphanId.ToString("D"), RetentionNumber(beforeAuditSequence));
        if (source.Count == 0) return null;
        EvidenceRetentionCodec.Require(source.Count == 1, "QuarantineSourceDuplicate");
        var row = source[0];
        var activated = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MIN(Sequence),9223372036854775807) FROM audit_entries WHERE Kind='EvidenceRetentionActivated';", deadline);
        // Earlier quarantines did not freeze a retention-policy selection. Preserve them.
        if (row.Audit <= activated) return null;
        var fact = EvidenceReconciliationStorageCodec.Decode(Encoding.UTF8.GetBytes(row.Payload),
            EvidenceReconciliationStoreOptions.MaximumPayloadBytesHardLimit);
        var subject = fact.Subject!;
        EvidenceRetentionCodec.Require(subject.Kind == EvidenceReconciliationSubjectKind.Orphan &&
            subject.OrphanId == orphanId && subject.QuarantineRootBindingHash is not null &&
            subject.QuarantineFileName is not null && subject.ByteLength is not null &&
            subject.ObservedContentHash is not null, "QuarantineBindingInvalid");
        var policyHash = AuditChainDatabase.Text(database, @"SELECT SnapshotHash FROM trace_storage_policy_events
            WHERE CentralSequence<? ORDER BY CentralSequence DESC LIMIT 1;", deadline, RetentionNumber(row.Audit));
        if (policyHash is null) return null;
        var snapshot = ReadRetentionPolicy(database, policyHash, row.Audit, deadline);
        var evidenceClass = subject.FileArea == EvidenceReconciliationFileArea.Stage
            ? TraceRetentionClass.OrphanImageStage : TraceRetentionClass.QuarantineEvidence;
        var rule = snapshot.RetentionRules.Single(x => x.EvidenceClass == evidenceClass);
        // An orphan's original creation instant is not established by its current file
        // timestamps. Preserve it when the frozen rule requires that unavailable fact.
        if (rule.StartsAt != RetentionStartEvent.Quarantined) return null;
        return CreateRetentionObligation(new(EvidenceRetentionOwnerKind.QuarantinedFile, orphanId), evidenceClass,
            null, row.Hash, row.Audit, subject.ObservedContentHash!, snapshot, rule, fact.RecordedAtUtc,
            subject.QuarantineRootBindingHash!, subject.QuarantineFileName!, subject.ByteLength!.Value);
    }

    private static TraceStoragePolicySnapshot ReadRetentionPolicy(sqlite3 database, string snapshotHash,
        long before, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT PublicationPayload FROM trace_storage_policy_events
            WHERE SnapshotHash=? AND CentralSequence<? LIMIT 2;", deadline,
            statement => SqliteNative.ColumnText(statement, 0)!, snapshotHash, RetentionNumber(before));
        EvidenceRetentionCodec.Require(rows.Count == 1, "FrozenPolicyMissing");
        var snapshot = new TraceStoragePolicySnapshot(
            TraceStoragePolicyStorageCodec.DecodePublication(Convert.FromBase64String(rows[0])));
        EvidenceRetentionCodec.Require(snapshot.ContentHash == snapshotHash, "FrozenPolicyHashMismatch");
        return snapshot;
    }

    private static EvidenceRetentionObligation CreateRetentionObligation(EvidenceRetentionOwner owner,
        TraceRetentionClass evidenceClass, Guid? inspectionId, string sourceHash, long sourceAudit,
        string artifactHash, TraceStoragePolicySnapshot snapshot, TraceRetentionRule rule,
        DateTimeOffset startedAt, string rootHash, string fileName, long bytes)
    {
        var baseObligation = new TraceRetentionObligation(artifactHash, snapshot, rule, startedAt);
        var value = new EvidenceRetentionObligation(owner, evidenceClass, inspectionId, sourceHash, sourceAudit,
            artifactHash, snapshot.ContentHash, snapshot.Publication.ContentHash, rule.ContentHash,
            rule.StartsAt, startedAt, baseObligation.RetainUntilUtc, rootHash, fileName, bytes, string.Empty);
        value = value with { ContentHash = EvidenceRetentionCodec.ObligationHash(value) };
        EvidenceRetentionCodec.ValidateObligation(value);
        return value;
    }

    internal static void RequireRetentionSource(sqlite3 database, RetentionConfiguration configuration,
        EvidenceRetentionStoredRow row, StoreDeadline deadline)
    {
        var fact = row.Payload;
        RequireRetentionAuthority(database, row, deadline);
        var expected = fact.Owner.Kind == EvidenceRetentionOwnerKind.ImageManifest
            ? ReadImageRetentionObligation(database, fact.Owner.OwnerId, deadline, row.AuditSequence)
            : ReadQuarantineRetentionObligation(database, fact.Owner.OwnerId, deadline, row.AuditSequence);
        EvidenceRetentionCodec.Require(expected is not null && expected.ContentHash == fact.ObligationHash &&
            (fact.EstablishedObligation is null || fact.EstablishedObligation == expected), "SourceObligationMismatch");
        if (fact.Kind == EvidenceRetentionEventKind.DeleteIntent)
        {
            EvidenceRetentionCodec.Require(configuration.DeletableClasses.Contains(expected!.EvidenceClass),
                "DeletionClassNotPermitted");
            RequireRetentionDeletionSourceComplete(database, expected, row.AuditSequence, deadline);
            if (fact.Owner.Kind == EvidenceRetentionOwnerKind.QuarantinedFile)
            {
                var original = AuditChainDatabase.Read(database, @"SELECT Payload
                    FROM evidence_reconciliation_events WHERE OrphanId=? AND Kind=6 AND AuditSequence<? LIMIT 2;",
                    deadline, statement => EvidenceReconciliationStorageCodec.Decode(
                        Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 0)!),
                        EvidenceReconciliationStoreOptions.MaximumPayloadBytesHardLimit).Subject!,
                    fact.Owner.OwnerId.ToString("D"), RetentionNumber(row.AuditSequence));
                EvidenceRetentionCodec.Require(original.Count == 1 &&
                    fact.File!.RawContentHash == original[0].ObservedContentHash &&
                    fact.File.FileIdentityHash == original[0].FileIdentityHash,
                    "QuarantineDeletionIdentityMismatch");
            }

        }
    }

    internal static void RequireRetentionDeletionSourceComplete(sqlite3 database, EvidenceRetentionObligation obligation,
        long before, StoreDeadline deadline)
    {
        if (obligation.Owner.Kind == EvidenceRetentionOwnerKind.QuarantinedFile) return;
        var released = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM image_finalization_events
            WHERE ManifestId=? AND Kind='StageReleased' AND AuditSequence<?;", deadline,
            obligation.Owner.OwnerId.ToString("D"), RetentionNumber(before));
        EvidenceRetentionCodec.Require(released == 1, "ImageWorkUnfinished");
        var lastCycleKind = AuditChainDatabase.Scalar(database, @"SELECT COALESCE((SELECT Kind
            FROM production_inspection_events WHERE InspectionId=? AND AuditSequence<?
            ORDER BY AuditSequence DESC LIMIT 1),0);", deadline,
            obligation.InspectionId!.Value.ToString("D"), RetentionNumber(before));
        EvidenceRetentionCodec.Require(lastCycleKind is (long)ProductionInspectionEventKind.AcknowledgementReset or
            (long)ProductionInspectionEventKind.RecoveryCompleted, "InspectionLifecycleUnfinished");
        // Inspect original Required obligations individually. A successful corrective item
        // never substitutes for a Failed/Pending original; delivery bytes remain immutable.
        if (AuditChainDatabase.TableExists(database, "production_outbox_deliveries", deadline))
        {
            var pending = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM production_outbox_deliveries d
                WHERE d.InspectionId=? AND d.Criticality=1 AND d.CreatedAuditSequence<? AND NOT EXISTS(
                    SELECT 1 FROM production_outbox_events s WHERE s.DeliveryId=d.DeliveryId
                    AND s.Kind='Succeeded' AND s.AuditSequence<?);", deadline,
                obligation.InspectionId!.Value.ToString("D"), RetentionNumber(before), RetentionNumber(before));
            EvidenceRetentionCodec.Require(pending == 0, "RequiredDeliveryUnfinished");
        }
    }
}
