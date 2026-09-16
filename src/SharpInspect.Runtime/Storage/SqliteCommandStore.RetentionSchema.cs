using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record RetentionConfiguration(string OptionsHash, string ReconciliationHash,
    string TracePolicyStoreHash, string DeploymentScopeHash, string ExecutionPolicyHash,
    string PolicyId, string PolicyVersion, string ApprovalReference, string Rationale,
    TraceRetentionClass[] DeletableClasses, long CleanupIntervalTicks, long CleanupRuntimeTicks,
    long CleanupBytes, int CleanupItems, int MaximumDeletionAttempts, int MaximumEvents, int MaximumPayloadBytes, long MaximumTotalBytes,
    long ControlWalAdmissionBytes, int MaximumControlFacts, long CheckpointPlanningReserveBytes)
{
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);
    internal string BindingHash => Convert.ToHexString(SHA256.HashData(Encode()));
    internal TraceStorageRecoveryBudget RecoveryBudget() => new(ControlWalAdmissionBytes, MaximumControlFacts, CheckpointPlanningReserveBytes);
    internal TraceRetentionExecutionPolicy ExecutionPolicy() => new(PolicyId, PolicyVersion, ApprovalReference,
        Rationale, DeletableClasses, new(TimeSpan.FromTicks(CleanupIntervalTicks),
            TimeSpan.FromTicks(CleanupRuntimeTicks), CleanupBytes, CleanupItems), MaximumDeletionAttempts);
}

internal sealed partial class SqliteCommandStore
{
    internal const string RetentionActivationKind = "EvidenceRetentionActivated";
    internal const string RetentionEventAuditKind = "EvidenceRetentionEvent";
    internal const string RetentionSchemaSql = @"
        CREATE TABLE evidence_retention_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE evidence_retention_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            OwnerKind INTEGER NOT NULL CHECK(OwnerKind BETWEEN 1 AND 2),
            OwnerId TEXT NOT NULL CHECK(length(OwnerId)=36),
            AggregateSequence INTEGER NOT NULL CHECK(AggregateSequence>0),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 8),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>1),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            UNIQUE(OwnerKind,OwnerId,AggregateSequence));
        CREATE INDEX ix_retention_operation ON evidence_retention_events(OperationId,Kind);
        CREATE INDEX ix_retention_owner ON evidence_retention_events(OwnerKind,OwnerId,Position);
        CREATE TRIGGER evidence_retention_config_no_update BEFORE UPDATE ON evidence_retention_config
            BEGIN SELECT RAISE(ABORT,'ImmutableRetentionConfiguration'); END;
        CREATE TRIGGER evidence_retention_config_no_delete BEFORE DELETE ON evidence_retention_config
            BEGIN SELECT RAISE(ABORT,'ImmutableRetentionConfiguration'); END;
        CREATE TRIGGER evidence_retention_event_no_update BEFORE UPDATE ON evidence_retention_events
            BEGIN SELECT RAISE(ABORT,'ImmutableRetentionEvent'); END;
        CREATE TRIGGER evidence_retention_event_no_delete BEFORE DELETE ON evidence_retention_events
            BEGIN SELECT RAISE(ABORT,'ImmutableRetentionEvent'); END;
        CREATE TRIGGER evidence_retention_event_contiguous BEFORE INSERT ON evidence_retention_events
            BEGIN SELECT CASE WHEN NEW.Position<>COALESCE((SELECT MAX(Position) FROM evidence_retention_events),0)+1
                THEN RAISE(ABORT,'RetentionPositionGap') END; END;";

    internal static RetentionConfiguration RetentionConfigurationFor(ProductionStoreOptions store)
    {
        var option = store.StorageRetention ?? throw new InvalidOperationException("RetentionConfigurationRequired");
        option.ValidateProfile(store);
        var policy = option.ExecutionPolicy;
        return new(option.BindingHash, store.EvidenceReconciliation!.BindingHash,
            store.TraceStoragePolicies!.BindingHash, store.TraceStoragePolicies.DeploymentScope.ContentHash,
            policy.ContentHash, policy.PolicyId, policy.Version, policy.ApprovalReference, policy.Rationale,
            policy.DeletableClasses.ToArray(), policy.Cleanup.Interval.Ticks, policy.Cleanup.MaximumRunTime.Ticks,
            policy.Cleanup.MaximumBytes, policy.Cleanup.MaximumItems, policy.MaximumDeletionAttempts,
            option.MaximumEvents, option.MaximumPayloadBytes, option.MaximumTotalBytes,
            option.RecoveryBudget.ControlWalAdmissionBytes, option.RecoveryBudget.MaximumControlFacts,
            option.RecoveryBudget.CheckpointPlanningReserveBytes);
    }

    internal static void InitializeRetentionTables(sqlite3 database, ProductionStoreOptions store, StoreDeadline deadline)
    {
        var configuration = RetentionConfigurationFor(store);
        var bytes = configuration.Encode();
        EvidenceRetentionCodec.Require(bytes.Length <= 65536, "ConfigurationCapacityExceeded");
        SqliteNative.Execute(database, RetentionSchemaSql, deadline);
        AuditChainDatabase.Execute(database, "INSERT INTO evidence_retention_config(Id,FormatVersion,Payload,BindingHash) VALUES(1,1,?,?);",
            deadline, Encoding.UTF8.GetString(bytes), configuration.BindingHash);
    }

    internal static RetentionConfiguration ReadRetentionConfiguration(sqlite3 database, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database,
            "SELECT FormatVersion,Payload,BindingHash FROM evidence_retention_config LIMIT 2;", deadline, statement =>
        {
            EvidenceRetentionCodec.Require(SqliteNative.ColumnInt64(statement, 0) == 1, "ConfigurationVersionInvalid");
            var bytes = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 1) ?? string.Empty);
            EvidenceRetentionCodec.Require(bytes.Length is >= 2 and <= 65536, "ConfigurationSizeInvalid");
            var value = JsonSerializer.Deserialize<RetentionConfiguration>(bytes) ??
                throw new InvalidOperationException("RetentionConfigurationMissing");
            EvidenceRetentionCodec.Require(value.DeletableClasses is not null &&
                bytes.AsSpan().SequenceEqual(value.Encode()) && value.BindingHash == SqliteNative.ColumnText(statement, 2),
                "ConfigurationHashMismatch");
            EvidenceRetentionCodec.Require(new[] { value.OptionsHash, value.ReconciliationHash, value.TracePolicyStoreHash,
                value.DeploymentScopeHash, value.ExecutionPolicyHash }.All(EvidenceRetentionCodec.Hash),
                "ConfigurationBindingInvalid");
            EvidenceRetentionCodec.Require(value.ExecutionPolicy().ContentHash == value.ExecutionPolicyHash &&
                value.MaximumEvents is >= 16 and <= TraceStorageRetentionOptions.MaximumEventsHardLimit &&
                value.MaximumPayloadBytes is >= 4096 and <= TraceStorageRetentionOptions.MaximumPayloadBytesHardLimit &&
                value.MaximumTotalBytes >= value.MaximumPayloadBytes &&
                value.MaximumTotalBytes <= TraceStorageRetentionOptions.MaximumTotalBytesHardLimit,
                "ConfigurationBoundsInvalid");
            _ = value.RecoveryBudget();
            return value;
        });
        EvidenceRetentionCodec.Require(rows.Count == 1, "ConfigurationMissing");
        return rows[0];
    }

    internal static void RequireConfiguredRetention(sqlite3 database, ProductionStoreOptions store, StoreDeadline deadline)
    {
        var version = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        if (store.StorageRetention is null)
        {
            EvidenceRetentionCodec.Require(version is not (TraceStorageRetentionOptions.SchemaVersion or
                DiagnosticSupportStoreOptions.SchemaVersion), "ConfigurationRequired");
            return;
        }
        EvidenceRetentionCodec.Require(version is TraceStorageRetentionOptions.SchemaVersion or
            DiagnosticSupportStoreOptions.SchemaVersion, "GovernedMigrationRequired");
        EvidenceRetentionCodec.Require(ReadRetentionConfiguration(database, deadline).BindingHash ==
            RetentionConfigurationFor(store).BindingHash, "ConfigurationMismatch");
    }

    internal static IReadOnlyList<EvidenceRetentionStoredRow> ReadRetentionRows(sqlite3 database,
        RetentionConfiguration configuration, StoreDeadline deadline)
    {
        var count = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM evidence_retention_events;", deadline);
        var bytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM evidence_retention_events;", deadline);
        EvidenceRetentionCodec.Require(count <= configuration.MaximumEvents && bytes <= configuration.MaximumTotalBytes,
            "LedgerCapacityExceeded");
        var replay = new EvidenceRetentionReplay();
        return AuditChainDatabase.Read(database, @"SELECT Position,EventId,OperationId,OwnerKind,OwnerId,
            AggregateSequence,Kind,Payload,ContentHash,AuditSequence,AuditHash FROM evidence_retention_events ORDER BY Position;",
            deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var payloadBytes = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 7) ?? string.Empty);
            var fact = EvidenceRetentionCodec.Decode(payloadBytes, configuration.MaximumPayloadBytes);
            var hash = SqliteNative.ColumnText(statement, 8)!;
            EvidenceRetentionCodec.Require(fact.EventId.ToString("D") == SqliteNative.ColumnText(statement, 1) &&
                fact.OperationId.ToString("D") == SqliteNative.ColumnText(statement, 2) &&
                (long)fact.Owner.Kind == SqliteNative.ColumnInt64(statement, 3) &&
                fact.Owner.OwnerId.ToString("D") == SqliteNative.ColumnText(statement, 4) &&
                fact.AggregateSequence == SqliteNative.ColumnInt64(statement, 5) &&
                (long)fact.Kind == SqliteNative.ColumnInt64(statement, 6) &&
                fact.ExecutionPolicyHash == configuration.ExecutionPolicyHash &&
                hash == EvidenceRetentionCodec.ContentHash(position, configuration.BindingHash, payloadBytes),
                "RowBindingInvalid");
            var row = new EvidenceRetentionStoredRow(position, fact, hash,
                SqliteNative.ColumnInt64(statement, 9), SqliteNative.ColumnText(statement, 10)!);
            RequireRetentionSource(database, configuration, row, deadline);
            replay.Apply(row);
            EvidenceRetentionCodec.Require(replay.Subjects[row.Payload.Owner].DeleteAttempts <= configuration.MaximumDeletionAttempts,
                "DeletionAttemptsExceeded");
            return row;
        });
    }

    internal static long ReadRetentionAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database, @"SELECT COALESCE(SUM(CASE WHEN EXISTS(
            SELECT 1 FROM evidence_retention_events u WHERE u.OperationId=i.OperationId AND u.Kind=7)
            THEN 1 ELSE 2 END),0) FROM evidence_retention_events i WHERE i.Kind=5 AND NOT EXISTS(
            SELECT 1 FROM evidence_retention_events c WHERE c.OperationId=i.OperationId AND c.Kind IN(6,8));", deadline);

    private static string RetentionNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
