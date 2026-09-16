using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceRetentionSource(long AuditSequence, EvidenceRetentionObligation? Obligation,
    PendingImageManifest? Manifest);

public sealed partial class SqliteEvidenceRetentionQuery
{
    /// <summary>Scheduling prefilter only; the single writer rechecks every obligation before DeleteIntent.</summary>
    internal async Task<IReadOnlySet<Guid>> ReadCompletedImagesAsync(CancellationToken token)
    {
        if (_options.ImageFinalization is null) return new HashSet<Guid>();
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (!await Slots.WaitAsync(deadline.Remaining, token).ConfigureAwait(false))
            throw new TimeoutException("RetentionQueryDeadlineExceeded");
        try
        {
            return await Task.Run<IReadOnlySet<Guid>>(() =>
            {
                if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
                using var connection = SqliteNative.Open(path, true);
                var database = connection.Handle!;
                SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, token);
                SqliteCommandStore.RequireConfiguredRetention(database, _options, deadline);
                SqliteCommandStore.VerifyEvidenceReconciliationReadGuard(database, _options, deadline);
                var outbox = _options.Outbox is null ? "" : @" AND NOT EXISTS (
                    SELECT 1 FROM production_outbox_deliveries d WHERE d.InspectionId=m.InspectionId AND d.Criticality=1
                    AND NOT EXISTS (SELECT 1 FROM production_outbox_events delivered
                        WHERE delivered.DeliveryId=d.DeliveryId AND delivered.Kind='Succeeded'))";
                var ids = AuditChainDatabase.Read(database, @"SELECT m.ManifestId FROM pending_image_manifests m
                    WHERE EXISTS(SELECT 1 FROM image_finalization_events s WHERE s.ManifestId=m.ManifestId AND s.Kind='StageReleased')
                    AND (SELECT e.Kind FROM production_inspection_events e WHERE e.InspectionId=m.InspectionId
                        ORDER BY e.AuditSequence DESC LIMIT 1) IN (6,8)" + outbox + " LIMIT ?;", deadline,
                    statement => Guid.Parse(SqliteNative.ColumnText(statement, 0)!),
                    (_options.ImageEvidence!.MaxImages + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture));
                EvidenceRetentionCodec.Require(ids.Count <= _options.ImageEvidence.MaxImages, "ImageSourceCapacityExceeded");
                SqliteNative.Execute(database, "COMMIT;", deadline, token);
                return ids.ToHashSet();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { Slots.Release(); }
    }

    internal async ValueTask<EvidenceRetentionReadState> ReadStateAsync(CancellationToken token)
    {
        using var snapshot = await AcquireSnapshotAsync(false, token).ConfigureAwait(false);
        return snapshot.State;
    }

    internal async Task<IReadOnlyList<EvidenceRetentionSource>> ReadSourcesAsync(long afterAuditSequence,
        int pageSize, CancellationToken token)
    {
        if (_options.StorageRetention is null || pageSize < 1 || pageSize > _options.StorageRetention.MaximumPageSize || afterAuditSequence < 0)
            throw new ArgumentException("RetentionSourcePageInvalid");
        var deadline = new StoreDeadline(_options.QueryTimeout);
        if (!await Slots.WaitAsync(deadline.Remaining, token).ConfigureAwait(false))
            throw new TimeoutException("RetentionQueryDeadlineExceeded");
        try
        {
            return await Task.Run<IReadOnlyList<EvidenceRetentionSource>>(() =>
            {
                if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
                using var connection = SqliteNative.Open(path, readOnly: true);
                var database = connection.Handle!;
                SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, token);
                SqliteCommandStore.RequireConfiguredRetention(database, _options, deadline);
                SqliteCommandStore.VerifyEvidenceReconciliationReadGuard(database, _options, deadline);
                var imageSql = _options.ImageFinalization is null ? "" : @"
                    SELECT 1 AS OwnerKind,ManifestId AS OwnerId,AuditSequence FROM image_finalization_events WHERE Kind='Succeeded'
                    UNION ALL ";
                var rows = AuditChainDatabase.Read(database, "SELECT OwnerKind,OwnerId,AuditSequence FROM (" + imageSql + @"
                    SELECT 2 AS OwnerKind,OrphanId AS OwnerId,AuditSequence FROM evidence_reconciliation_events WHERE Kind=6)
                    WHERE AuditSequence>? ORDER BY AuditSequence LIMIT ?;", deadline,
                    statement => ((EvidenceRetentionOwnerKind)SqliteNative.ColumnInt64(statement, 0),
                        Guid.Parse(SqliteNative.ColumnText(statement, 1)!), SqliteNative.ColumnInt64(statement, 2)),
                    afterAuditSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var result = new List<EvidenceRetentionSource>();
                foreach (var (kind, id, audit) in rows)
                {
                    SqliteNative.EnsureDeadline(deadline, token);
                    var obligation = kind == EvidenceRetentionOwnerKind.ImageManifest
                        ? SqliteCommandStore.ReadImageRetentionObligation(database, id, deadline)
                        : SqliteCommandStore.ReadQuarantineRetentionObligation(database, id, deadline);
                    PendingImageManifest? manifest = null;
                    if (kind == EvidenceRetentionOwnerKind.ImageManifest)
                    {
                        var payload = AuditChainDatabase.Text(database,
                            "SELECT Payload FROM pending_image_manifests WHERE ManifestId=?;", deadline, id.ToString("D"));
                        manifest = ProductionInspectionStorageCodec.DecodeImageManifest(Convert.FromBase64String(payload!));
                    }
                    result.Add(new(audit, obligation, manifest));
                }
                SqliteNative.Execute(database, "COMMIT;", deadline, token);
                return result.AsReadOnly();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { Slots.Release(); }
    }

    internal async Task<PendingImageManifest?> ReadManifestAsync(EvidenceRetentionObligation obligation, CancellationToken token)
    {
        if (obligation.Owner.Kind != EvidenceRetentionOwnerKind.ImageManifest) return null;
        // The source audit is the unique finalized-image event, so a one-row source page
        // resolves its frozen manifest without accepting a caller-provided locator.
        var source = (await ReadSourcesAsync(obligation.SourceAuditSequence - 1, 1, token).ConfigureAwait(false)).Single();
        EvidenceRetentionCodec.Require(source.Obligation == obligation && source.Manifest is not null, "SourceChanged");
        return source.Manifest;
    }
}
