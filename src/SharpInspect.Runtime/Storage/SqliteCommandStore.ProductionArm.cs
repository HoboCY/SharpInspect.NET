using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema-32 production-arm ledger storage: one bounded append-only table of
/// immutable arm events, one immutable configuration row, and metadata entries
/// in the existing central audit chain (the schema-31 envelope is reused, so no
/// earlier audit position, hash, or envelope is rewritten).
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ProductionArmActivatedKind = "ProductionArmStoreActivated";
    internal const string ProductionArmEventAuditKind = "ProductionArmEvent";

    internal const string ProductionArmSchemaSql = @"
        CREATE TABLE production_arm_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaxEvents INTEGER NOT NULL CHECK(MaxEvents>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaxTotalBytes INTEGER NOT NULL CHECK(MaxTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE production_arm_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            Cause INTEGER NOT NULL CHECK(Cause BETWEEN 1 AND 3),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 7),
            RequestIdentityHash TEXT NULL CHECK(RequestIdentityHash IS NULL OR length(RequestIdentityHash)=64),
            HumanCommandId TEXT NULL CHECK(HumanCommandId IS NULL OR length(HumanCommandId)=36),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64), Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64));
        CREATE UNIQUE INDEX ux_production_arm_attempted ON production_arm_events(AttemptId) WHERE Kind=1;
        CREATE UNIQUE INDEX ux_production_arm_startup_epoch ON production_arm_events(RuntimeEpoch)
            WHERE Cause=1 AND Kind=1;
        CREATE UNIQUE INDEX ux_production_arm_plc_request ON production_arm_events(RequestIdentityHash)
            WHERE Cause=2 AND Kind=1;
        CREATE UNIQUE INDEX ux_production_arm_manual_command ON production_arm_events(HumanCommandId)
            WHERE Cause=3 AND Kind=1;
        CREATE INDEX ix_production_arm_attempt ON production_arm_events(AttemptId, Position);
        CREATE TRIGGER production_arm_config_immutable_update BEFORE UPDATE ON production_arm_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionArmConfiguration'); END;
        CREATE TRIGGER production_arm_config_immutable_delete BEFORE DELETE ON production_arm_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionArmConfiguration'); END;
        CREATE TRIGGER production_arm_event_immutable_update BEFORE UPDATE ON production_arm_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionArmEvent'); END;
        CREATE TRIGGER production_arm_event_immutable_delete BEFORE DELETE ON production_arm_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionArmEvent'); END;";

    internal static void InitializeProductionArmSchema(sqlite3 database, ProductionArmStoreOptions options,
        StoreDeadline deadline, AuditIntegrityPolicy policy, IAuditSigningKey key)
    {
        options.Validate();
        SqliteNative.Execute(database, ProductionArmSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO production_arm_store_config
            (Id,FormatVersion,MaxEvents,MaximumPayloadBytes,MaxTotalBytes,BindingHash)
            VALUES(1,1,?,?,?,?);", deadline, N(options.MaxEvents), N(options.MaximumPayloadBytes),
            N(options.MaxTotalBytes), options.BindingHash);
        AuditChainDatabase.AppendProductionArmMetadata(database, policy, key, ProductionArmActivatedKind,
            options.EncodeActivationPayload(), options, deadline, 0);
    }

    internal static void RequireConfiguredProductionArm(sqlite3 database, ProductionArmStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaxEvents,MaximumPayloadBytes,
            MaxTotalBytes,BindingHash FROM production_arm_store_config WHERE Id=1 LIMIT 2;", deadline,
            value => new[]
            {
                SqliteNative.ColumnText(value, 0), SqliteNative.ColumnText(value, 1), SqliteNative.ColumnText(value, 2),
                SqliteNative.ColumnText(value, 3), SqliteNative.ColumnText(value, 4)
            });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
        {
            "1", N(options.MaxEvents), N(options.MaximumPayloadBytes), N(options.MaxTotalBytes), options.BindingHash
        }), "ProductionArmConfigurationMismatch");
    }

    internal static List<ProductionArmStoredRow> ReadProductionArmRows(sqlite3 database,
        ProductionArmStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        if (AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM production_arm_events;", deadline) >
            options.MaxEvents)
            throw new InvalidOperationException("ProductionArmEntryCapacityExceeded");
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,PreviousHash,EventId,AttemptId,RuntimeEpoch,
            Cause,Kind,RequestIdentityHash,HumanCommandId,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash
            FROM production_arm_events ORDER BY Position;", deadline, value =>
        {
            string Text(int column) => SqliteNative.ColumnText(value, column) ??
                throw new InvalidOperationException("ProductionArmColumnMissing");
            var payload = DecodeProductionArmPayload(Text(11));
            var arm = ProductionArmStorageCodec.Decode(payload);
            if (arm.Position != SqliteNative.ColumnInt64(value, 0) || arm.EventId.ToString("D") != Text(2) ||
                arm.AttemptId.ToString("D") != Text(3) || arm.RuntimeEpoch.ToString("D") != Text(4) ||
                (long)arm.Cause != SqliteNative.ColumnInt64(value, 5) ||
                (long)arm.Kind != SqliteNative.ColumnInt64(value, 6) ||
                !string.Equals(arm.PlcRequest?.RequestIdentityHash, SqliteNative.ColumnText(value, 7), StringComparison.Ordinal) ||
                !string.Equals(arm.HumanCommandId?.ToString("D"), SqliteNative.ColumnText(value, 8), StringComparison.Ordinal) ||
                arm.ContentHash != Text(9) || Convert.ToHexString(SHA256.HashData(payload)) != Text(10))
                throw new InvalidOperationException("ProductionArmIndexedPayloadMismatch");
            return new ProductionArmStoredRow(arm, SqliteNative.ColumnText(value, 1), Text(10), payload,
                SqliteNative.ColumnInt64(value, 12), Text(13));
        });
        if (rows.Sum(value => (long)value.Payload.Length) > options.MaxTotalBytes)
            throw new InvalidOperationException("ProductionArmTotalCapacityExceeded");
        return rows;
    }

    internal static byte[] DecodeProductionArmPayload(string text)
    {
        if (text.Length > ((ProductionArmStoreOptions.MaximumPayloadBytesHardLimit + 2) / 3) * 4)
            throw new InvalidOperationException("ProductionArmPayloadCapacityExceeded");
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length is < 1 or > ProductionArmStoreOptions.MaximumPayloadBytesHardLimit ||
            Convert.ToBase64String(bytes) != text)
            throw new InvalidOperationException("ProductionArmPayloadEncodingInvalid");
        return bytes;
    }

    /// <summary>
    /// The number of central audit metadata entries one attempt may still need.
    /// The value covers a possible Authorized, ReadyConfirmed and one optional
    /// status-delivery observation, so no shared writer can consume the budget
    /// a pending attempt already reserved.
    /// </summary>
    internal static long ProductionArmRemaining(IReadOnlyList<ProductionArmHistoryEvent> history)
    {
        if (history.Count == 0) return 0;
        if (history.Any(value => value.StatusDelivery)) return 0;
        if (history.Any(value => value.Terminal)) return ProductionArmStoreOptions.TerminalAttemptReserveEntries;
        if (history.Any(value => value.Kind == ProductionArmEventKind.Authorized)) return 2;
        return ProductionArmStoreOptions.OpenAttemptReserveEntries;
    }

    internal static long ReadProductionArmAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database, @"SELECT COALESCE(SUM(CASE
            WHEN StatusSeen=1 THEN 0 WHEN TerminalSeen=1 THEN 1 WHEN AuthorizedSeen=1 THEN 2 ELSE 3 END),0) FROM (
            SELECT MAX(Kind IN (3,4,5)) AS TerminalSeen, MAX(Kind IN (6,7)) AS StatusSeen, MAX(Kind=2) AS AuthorizedSeen
            FROM production_arm_events GROUP BY AttemptId);", deadline);

    internal sealed record ProductionArmStoredRow(ProductionArmHistoryEvent Event, string? PreviousHash,
        string PayloadHash, byte[] Payload, long AuditSequence, string AuditHash);
}
