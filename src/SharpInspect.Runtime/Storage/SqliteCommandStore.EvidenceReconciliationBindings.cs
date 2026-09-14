using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static void RequireEvidenceReconciliationSource(sqlite3 database,
        EvidenceReconciliationConfiguration configuration, EvidenceReconciliationSubject? subject,
        StoreDeadline deadline, long? beforeAuditSequence)
    {
        if (subject is null) return;
        if (subject.Kind == EvidenceReconciliationSubjectKind.Orphan)
        {
            var stage = subject.FileArea == EvidenceReconciliationFileArea.Stage;
            AuditChainDatabase.Require(configuration.FinalizationHash is not null &&
                subject.SourceRootBindingHash == (stage ? configuration.StageRootHash : configuration.FinalRootHash) &&
                (subject.QuarantineRootBindingHash is null || subject.QuarantineRootBindingHash ==
                    (stage ? configuration.StageQuarantineHash : configuration.FinalQuarantineHash)),
                "EvidenceReconciliationOrphanRootBindingInvalid");
            RequireReconciliationOrphanUnreferenced(database, subject, beforeAuditSequence, deadline);
            return;
        }
        var upper = ReconciliationNumber(beforeAuditSequence ?? long.MaxValue);
        if (subject.Kind == EvidenceReconciliationSubjectKind.Image)
        {
            AuditChainDatabase.Require(configuration.FinalizationHash is not null,
                "EvidenceReconciliationImageFeatureMissing");
            var count = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM pending_image_manifests m
                JOIN pending_image_work w ON w.ManifestId=m.ManifestId
                JOIN production_inspection_events c ON c.InspectionId=m.InspectionId
                WHERE m.ManifestId=? AND m.InspectionId=? AND w.WorkId=? AND m.ContentHash=?
                AND c.Position=? AND c.Kind=? AND c.AuditSequence<?;", deadline,
                subject.ManifestId!.Value.ToString("D"), subject.InspectionId!.Value.ToString("D"),
                subject.WorkId!.Value.ToString("D"), subject.ReferenceContentHash,
                ReconciliationNumber(subject.SourcePosition!.Value),
                ReconciliationNumber((long)ProductionInspectionEventKind.CoreCommitted), upper);
            AuditChainDatabase.Require(count == 1, "EvidenceReconciliationImageReferenceInvalid");
        }
        else
        {
            AuditChainDatabase.Require(configuration.OutboxHash is not null,
                "EvidenceReconciliationOutboxFeatureMissing");
            var count = AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM production_outbox_deliveries d
                JOIN production_outbox_events e ON e.DeliveryId=d.DeliveryId
                WHERE d.DeliveryId=? AND d.InspectionId=? AND d.Position=? AND d.ContentHash=?
                AND e.Kind='Created' AND e.AuditSequence<?;", deadline, subject.DeliveryId!.Value.ToString("D"),
                subject.InspectionId!.Value.ToString("D"), ReconciliationNumber(subject.SourcePosition!.Value),
                subject.ReferenceContentHash, upper);
            AuditChainDatabase.Require(count == 1, "EvidenceReconciliationOutboxReferenceInvalid");
        }
        var revision = ReadEvidenceReconciliationSourceRevision(database, configuration, subject.Kind,
            subject.WorkId ?? subject.DeliveryId!.Value, deadline, beforeAuditSequence);
        AuditChainDatabase.Require(revision == subject.ObservedLifecycleHash,
            "EvidenceReconciliationSourceChanged");
    }

    internal static string ReadEvidenceReconciliationSourceRevision(sqlite3 database,
        EvidenceReconciliationConfiguration configuration, EvidenceReconciliationSubjectKind kind,
        Guid identity, StoreDeadline deadline, long? beforeAuditSequence = null)
    {
        var upper = ReconciliationNumber(beforeAuditSequence ?? long.MaxValue);
        var id = identity.ToString("D");
        if (kind == EvidenceReconciliationSubjectKind.Image)
        {
            var rows = AuditChainDatabase.Read(database, @"SELECT ContentHash FROM image_finalization_events
                WHERE WorkId=? AND AuditSequence<? ORDER BY AuditSequence DESC LIMIT 1;", deadline,
                statement => SqliteNative.ColumnText(statement, 0)!, id, upper);
            if (rows.Count != 0) return rows[0];
            var initial = AuditChainDatabase.Read(database,
                "SELECT ContentHash FROM pending_image_work WHERE WorkId=?;", deadline,
                statement => SqliteNative.ColumnText(statement, 0)!, id);
            AuditChainDatabase.Require(initial.Count == 1, "EvidenceReconciliationSourceMissing");
            return initial[0];
        }
        AuditChainDatabase.Require(kind == EvidenceReconciliationSubjectKind.Outbox,
            "EvidenceReconciliationSourceKindInvalid");
        var sql = @"SELECT ContentHash FROM (SELECT ContentHash,AuditSequence FROM production_outbox_events
            WHERE DeliveryId=? AND AuditSequence<?";
        var bindings = new List<string?> { id, upper };
        if (configuration.OutboxRecoveryHash is not null)
        {
            sql += @" UNION ALL SELECT ContentHash,AuthorizationAuditSequence AS AuditSequence
                FROM production_outbox_recovery WHERE DeliveryId=? AND AuthorizationAuditSequence<?
                UNION ALL SELECT ContentHash,AuthorizationAuditSequence AS AuditSequence
                FROM production_outbox_corrections WHERE (DeliveryId=? OR SourceDeliveryId=?)
                    AND AuthorizationAuditSequence<?";
            bindings.AddRange(new[] { id, upper, id, id, upper });
        }
        sql += ") ORDER BY AuditSequence DESC LIMIT 1;";
        var heads = AuditChainDatabase.Read(database, sql, deadline,
            statement => SqliteNative.ColumnText(statement, 0)!, bindings.ToArray());
        AuditChainDatabase.Require(heads.Count == 1, "EvidenceReconciliationSourceMissing");
        return heads[0];
    }
}
