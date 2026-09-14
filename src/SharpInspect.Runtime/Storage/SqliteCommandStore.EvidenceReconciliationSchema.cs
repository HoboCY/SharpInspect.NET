using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceReconciliationConfiguration(string OptionsHash, string? ImageEvidenceHash,
    string? FinalizationHash, string? OutboxHash, string? OutboxRecoveryHash,
    string? StageRootHash, string? FinalRootHash, string? StageQuarantineHash, string? FinalQuarantineHash,
    int MaximumEvents, int MaximumPayloadBytes, long MaximumTotalBytes)
{
    internal byte[] Encode() => AuditCanonical.Encode("EvidenceReconciliationConfigurationV1",
        EvidenceReconciliationStoreOptions.RulesVersion, OptionsHash, ImageEvidenceHash, FinalizationHash,
        OutboxHash, OutboxRecoveryHash, StageRootHash, FinalRootHash, StageQuarantineHash, FinalQuarantineHash,
        MaximumEvents.ToString(CultureInfo.InvariantCulture), MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
        MaximumTotalBytes.ToString(CultureInfo.InvariantCulture));
    internal string BindingHash => Convert.ToHexString(SHA256.HashData(Encode()));
}

internal sealed partial class SqliteCommandStore
{
    internal const string EvidenceReconciliationActivationKind = "EvidenceReconciliationActivated";
    internal const string EvidenceReconciliationEventAuditKind = "EvidenceReconciliationEvent";
    internal const string EvidenceReconciliationSchemaSql = @"
        CREATE TABLE evidence_reconciliation_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            OptionsHash TEXT NOT NULL CHECK(length(OptionsHash)=64),
            ImageEvidenceHash TEXT CHECK(ImageEvidenceHash IS NULL OR length(ImageEvidenceHash)=64),
            FinalizationHash TEXT CHECK(FinalizationHash IS NULL OR length(FinalizationHash)=64),
            OutboxHash TEXT CHECK(OutboxHash IS NULL OR length(OutboxHash)=64),
            OutboxRecoveryHash TEXT CHECK(OutboxRecoveryHash IS NULL OR length(OutboxRecoveryHash)=64),
            StageRootHash TEXT CHECK(StageRootHash IS NULL OR length(StageRootHash)=64),
            FinalRootHash TEXT CHECK(FinalRootHash IS NULL OR length(FinalRootHash)=64),
            StageQuarantineHash TEXT CHECK(StageQuarantineHash IS NULL OR length(StageQuarantineHash)=64),
            FinalQuarantineHash TEXT CHECK(FinalQuarantineHash IS NULL OR length(FinalQuarantineHash)=64),
            MaximumEvents INTEGER NOT NULL CHECK(MaximumEvents BETWEEN 16 AND 100000),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes BETWEEN 4096 AND 65536),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes BETWEEN MaximumPayloadBytes AND 536870912),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE evidence_reconciliation_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            RunId TEXT NOT NULL CHECK(length(RunId)=36),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 10),
            OrphanId TEXT CHECK(OrphanId IS NULL OR length(OrphanId)=36),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>1),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE INDEX ix_reconciliation_run ON evidence_reconciliation_events(RunId,Position);
        CREATE INDEX ix_reconciliation_orphan ON evidence_reconciliation_events(OrphanId,Kind);
        CREATE TRIGGER evidence_reconciliation_config_no_update BEFORE UPDATE ON evidence_reconciliation_config
            BEGIN SELECT RAISE(ABORT,'ImmutableEvidenceReconciliationConfiguration'); END;
        CREATE TRIGGER evidence_reconciliation_config_no_delete BEFORE DELETE ON evidence_reconciliation_config
            BEGIN SELECT RAISE(ABORT,'ImmutableEvidenceReconciliationConfiguration'); END;
        CREATE TRIGGER evidence_reconciliation_event_no_update BEFORE UPDATE ON evidence_reconciliation_events
            BEGIN SELECT RAISE(ABORT,'ImmutableEvidenceReconciliationEvent'); END;
        CREATE TRIGGER evidence_reconciliation_event_no_delete BEFORE DELETE ON evidence_reconciliation_events
            BEGIN SELECT RAISE(ABORT,'ImmutableEvidenceReconciliationEvent'); END;
        CREATE TRIGGER evidence_reconciliation_event_contiguous BEFORE INSERT ON evidence_reconciliation_events
            BEGIN SELECT CASE WHEN NEW.Position<>COALESCE((SELECT MAX(Position)
                FROM evidence_reconciliation_events),0)+1
                THEN RAISE(ABORT,'EvidenceReconciliationPositionGap') END; END;";

    internal static EvidenceReconciliationConfiguration ReconciliationConfiguration(ProductionStoreOptions store)
    {
        var option = store.EvidenceReconciliation ??
            throw new InvalidOperationException("EvidenceReconciliationConfigurationRequired");
        option.Validate();
        return new(option.BindingHash, store.ImageEvidence?.BindingHash, store.ImageFinalization?.BindingHash,
            store.Outbox?.BindingHash, store.Outbox?.ManualRecovery?.BindingHash,
            store.ImageEvidence?.StageRootBindingHash, store.ImageFinalization?.FinalRootBindingHash,
            option.StageQuarantine?.BindingHash, option.FinalQuarantine?.BindingHash,
            option.MaximumEvents, option.MaximumPayloadBytes, option.MaximumTotalBytes);
    }

    internal static void InitializeEvidenceReconciliationTables(sqlite3 database, ProductionStoreOptions store,
        StoreDeadline deadline)
    {
        var configuration = ReconciliationConfiguration(store);
        SqliteNative.Execute(database, EvidenceReconciliationSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO evidence_reconciliation_config
            (Id,FormatVersion,OptionsHash,ImageEvidenceHash,FinalizationHash,OutboxHash,OutboxRecoveryHash,
             StageRootHash,FinalRootHash,StageQuarantineHash,FinalQuarantineHash,MaximumEvents,
             MaximumPayloadBytes,MaximumTotalBytes,BindingHash) VALUES(1,1,?,?,?,?,?,?,?,?,?,?,?,?,?);",
            deadline, configuration.OptionsHash, configuration.ImageEvidenceHash, configuration.FinalizationHash,
            configuration.OutboxHash, configuration.OutboxRecoveryHash, configuration.StageRootHash,
            configuration.FinalRootHash, configuration.StageQuarantineHash, configuration.FinalQuarantineHash,
            ReconciliationNumber(configuration.MaximumEvents), ReconciliationNumber(configuration.MaximumPayloadBytes),
            ReconciliationNumber(configuration.MaximumTotalBytes), configuration.BindingHash);
    }

    internal static EvidenceReconciliationConfiguration ReadEvidenceReconciliationConfiguration(sqlite3 database,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,OptionsHash,ImageEvidenceHash,
            FinalizationHash,OutboxHash,OutboxRecoveryHash,StageRootHash,FinalRootHash,StageQuarantineHash,
            FinalQuarantineHash,MaximumEvents,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM evidence_reconciliation_config LIMIT 2;", deadline, statement =>
        {
            AuditChainDatabase.Require(SqliteNative.ColumnInt64(statement, 0) == 1,
                "EvidenceReconciliationConfigurationVersionInvalid");
            var value = new EvidenceReconciliationConfiguration(SqliteNative.ColumnText(statement, 1)!,
                SqliteNative.ColumnText(statement, 2), SqliteNative.ColumnText(statement, 3),
                SqliteNative.ColumnText(statement, 4), SqliteNative.ColumnText(statement, 5),
                SqliteNative.ColumnText(statement, 6), SqliteNative.ColumnText(statement, 7),
                SqliteNative.ColumnText(statement, 8), SqliteNative.ColumnText(statement, 9),
                checked((int)SqliteNative.ColumnInt64(statement, 10)),
                checked((int)SqliteNative.ColumnInt64(statement, 11)), SqliteNative.ColumnInt64(statement, 12));
            RequireValidReconciliationConfiguration(value);
            AuditChainDatabase.Require(value.BindingHash == SqliteNative.ColumnText(statement, 13),
                "EvidenceReconciliationConfigurationHashMismatch");
            return value;
        });
        AuditChainDatabase.Require(rows.Count == 1, "EvidenceReconciliationConfigurationMissing");
        return rows[0];
    }

    private static void RequireValidReconciliationConfiguration(EvidenceReconciliationConfiguration value)
    {
        AuditChainDatabase.Require(EvidenceReconciliationStorageCodec.IsHash(value.OptionsHash),
            "EvidenceReconciliationConfigurationHashInvalid");
        foreach (var hash in new[] { value.ImageEvidenceHash, value.FinalizationHash, value.OutboxHash,
                     value.OutboxRecoveryHash, value.StageRootHash, value.FinalRootHash,
                     value.StageQuarantineHash, value.FinalQuarantineHash })
            AuditChainDatabase.Require(hash is null || EvidenceReconciliationStorageCodec.IsHash(hash),
                "EvidenceReconciliationConfigurationHashInvalid");
        var images = value.FinalizationHash is not null;
        AuditChainDatabase.Require((images || value.OutboxHash is not null) &&
            (value.OutboxRecoveryHash is null || value.OutboxHash is not null) &&
            images == (value.ImageEvidenceHash is not null) && images == (value.StageRootHash is not null) &&
            images == (value.FinalRootHash is not null) && images == (value.StageQuarantineHash is not null) &&
            images == (value.FinalQuarantineHash is not null), "EvidenceReconciliationFeatureProfileInvalid");
        AuditChainDatabase.Require(value.MaximumEvents is >= 16 and <= EvidenceReconciliationStoreOptions.MaximumEventsHardLimit &&
            value.MaximumPayloadBytes is >= 4096 and <= EvidenceReconciliationStoreOptions.MaximumPayloadBytesHardLimit &&
            value.MaximumTotalBytes >= value.MaximumPayloadBytes &&
            value.MaximumTotalBytes <= EvidenceReconciliationStoreOptions.MaximumTotalBytesHardLimit,
            "EvidenceReconciliationConfigurationBoundsInvalid");
    }

    internal static void RequireConfiguredEvidenceReconciliation(sqlite3 database, ProductionStoreOptions store,
        StoreDeadline deadline)
    {
        var version = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        if (store.EvidenceReconciliation is null)
        {
            AuditChainDatabase.Require(version != EvidenceReconciliationStoreOptions.SchemaVersion,
                "EvidenceReconciliationConfigurationRequired");
            return;
        }
        AuditChainDatabase.Require(version == EvidenceReconciliationStoreOptions.SchemaVersion,
            "EvidenceReconciliationGovernedMigrationRequired");
        AuditChainDatabase.Require(ReadEvidenceReconciliationConfiguration(database, deadline) ==
            ReconciliationConfiguration(store), "EvidenceReconciliationConfigurationMismatch");
    }

    internal static IReadOnlyList<EvidenceReconciliationStoredRow> ReadEvidenceReconciliationRows(sqlite3 database,
        EvidenceReconciliationConfiguration configuration, StoreDeadline deadline)
    {
        var count = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM evidence_reconciliation_events;", deadline);
        var bytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM evidence_reconciliation_events;", deadline);
        AuditChainDatabase.Require(count <= configuration.MaximumEvents && bytes <= configuration.MaximumTotalBytes,
            "EvidenceReconciliationLedgerCapacityExceeded");
        var replay = new EvidenceReconciliationReplay();
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,EventId,RunId,Kind,OrphanId,Payload,
            ContentHash,AuditSequence,AuditHash FROM evidence_reconciliation_events ORDER BY Position;", deadline,
            statement =>
            {
                var position = SqliteNative.ColumnInt64(statement, 0);
                var payload = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 5) ?? string.Empty);
                var fact = EvidenceReconciliationStorageCodec.Decode(payload, configuration.MaximumPayloadBytes);
                var hash = SqliteNative.ColumnText(statement, 6)!;
                AuditChainDatabase.Require(fact.EventId.ToString("D") == SqliteNative.ColumnText(statement, 1) &&
                    fact.RunId.ToString("D") == SqliteNative.ColumnText(statement, 2) &&
                    (long)fact.Kind == SqliteNative.ColumnInt64(statement, 3) &&
                    fact.Subject?.OrphanId?.ToString("D") == SqliteNative.ColumnText(statement, 4) &&
                    hash == EvidenceReconciliationStorageCodec.ContentHash(position, configuration.BindingHash, payload),
                    "EvidenceReconciliationRowBindingInvalid");
                var row = new EvidenceReconciliationStoredRow(position, fact, hash,
                    SqliteNative.ColumnInt64(statement, 7), SqliteNative.ColumnText(statement, 8)!);
                RequireEvidenceReconciliationSource(database, configuration, fact.Subject, deadline, row.AuditSequence);
                replay.Apply(row);
                return row;
            });
        RequireReconciliationCoverage(database, configuration, rows, deadline);
        return rows;
    }

    internal static long ReadEvidenceReconciliationAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database, @"SELECT COALESCE(SUM(CASE WHEN EXISTS(
                SELECT 1 FROM evidence_reconciliation_events f WHERE f.Kind=7 AND f.OrphanId=i.OrphanId)
                THEN 1 ELSE 2 END),0) FROM evidence_reconciliation_events i
            WHERE i.Kind=5 AND NOT EXISTS(SELECT 1 FROM evidence_reconciliation_events c
                WHERE c.Kind=6 AND c.OrphanId=i.OrphanId);", deadline);

    private static string ReconciliationNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
