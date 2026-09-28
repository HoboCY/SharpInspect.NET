using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The schema-41 immutable station-maintenance ledger: one configuration row, one immutable
/// fact row per phase and one signed central-audit metadata entry per fact. The stored bytes
/// carry the complete typed fact, never an opaque hash alone.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string StationMaintenanceSchemaSql = @"
        CREATE TABLE station_maintenance_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE station_maintenance_facts(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            Phase INTEGER NOT NULL CHECK(Phase BETWEEN 1 AND 3),
            Payload TEXT NOT NULL CHECK(length(CAST(Payload AS BLOB)) BETWEEN 2 AND 65536),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>1),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            UNIQUE(OperationId,Phase));
        CREATE INDEX ix_station_maintenance_phase ON station_maintenance_facts(Phase,Position);
        CREATE TRIGGER station_maintenance_config_no_update BEFORE UPDATE ON station_maintenance_config
            BEGIN SELECT RAISE(ABORT,'ImmutableStationMaintenanceConfig'); END;
        CREATE TRIGGER station_maintenance_config_no_delete BEFORE DELETE ON station_maintenance_config
            BEGIN SELECT RAISE(ABORT,'ImmutableStationMaintenanceConfig'); END;
        CREATE TRIGGER station_maintenance_fact_no_update BEFORE UPDATE ON station_maintenance_facts
            BEGIN SELECT RAISE(ABORT,'ImmutableStationMaintenanceFact'); END;
        CREATE TRIGGER station_maintenance_fact_no_delete BEFORE DELETE ON station_maintenance_facts
            BEGIN SELECT RAISE(ABORT,'ImmutableStationMaintenanceFact'); END;
        CREATE TRIGGER station_maintenance_fact_contiguous BEFORE INSERT ON station_maintenance_facts
            BEGIN SELECT CASE WHEN NEW.Position<>COALESCE((SELECT MAX(Position) FROM station_maintenance_facts),0)+1
                THEN RAISE(ABORT,'StationMaintenancePositionGap') END; END;";

    internal const string StationMaintenanceAdmittedReason = "StationMaintenanceAdmitted";
    internal const string StationMaintenancePreparedReason = "StationMaintenancePrepared";

    internal static StationMaintenanceConfiguration StationMaintenanceConfigurationFor(ProductionStoreOptions store)
    {
        var option = store.StationMaintenance ?? throw new InvalidOperationException(
            "StationMaintenanceConfigurationRequired");
        option.ValidateProfile(store);
        var policy = option.Policy;
        return new(option.BindingHash, policy.ContentHash, option.InstallationBindingHash,
            option.RuntimeApplicationHash, store.AuditIntegrityPolicy!.ContentHash,
            DiagnosticSupportConfigurationFor(store).BindingHash, DiagnosticSupportStoreOptions.SchemaVersion,
            policy.RetirementTimeout.Ticks, policy.HandoffLifetime.Ticks, policy.HostExitTimeout.Ticks,
            option.MaximumOperations, option.MaximumTotalBytes);
    }

    internal static void InitializeStationMaintenanceTables(sqlite3 database, ProductionStoreOptions store,
        StoreDeadline deadline)
    {
        var configuration = StationMaintenanceConfigurationFor(store);
        var bytes = StationMaintenanceStorageCodec.EncodeConfiguration(configuration);
        SqliteNative.Execute(database, StationMaintenanceSchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO station_maintenance_config(Id,FormatVersion,Payload,BindingHash) VALUES(1,1,?,?);",
            deadline, Encoding.UTF8.GetString(bytes), configuration.BindingHash);
    }

    internal static StationMaintenanceConfiguration ReadStationMaintenanceConfiguration(sqlite3 database,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database,
            "SELECT FormatVersion,Payload,BindingHash FROM station_maintenance_config LIMIT 2;", deadline, statement =>
            {
                StationMaintenanceStorageCodec.Require(SqliteNative.ColumnInt64(statement, 0) == 1,
                    "ConfigurationVersionInvalid");
                var bytes = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 1) ?? string.Empty);
                var value = StationMaintenanceStorageCodec.DecodeConfiguration(bytes);
                StationMaintenanceStorageCodec.Require(value.BindingHash == SqliteNative.ColumnText(statement, 2),
                    "ConfigurationHashMismatch");
                return value;
            });
        StationMaintenanceStorageCodec.Require(rows.Count == 1, "ConfigurationMissing");
        return rows[0];
    }

    internal static void RequireConfiguredStationMaintenance(sqlite3 database, ProductionStoreOptions store,
        StoreDeadline deadline)
    {
        var version = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        if (store.StationMaintenance is null)
        {
            StationMaintenanceStorageCodec.Require(version != StationMaintenanceStoreOptions.SchemaVersion,
                "ConfigurationRequired");
            return;
        }
        StationMaintenanceStorageCodec.Require(version == StationMaintenanceStoreOptions.SchemaVersion,
            "GovernedMigrationRequired");
        StationMaintenanceStorageCodec.Require(
            ReadStationMaintenanceConfiguration(database, deadline).BindingHash ==
            StationMaintenanceConfigurationFor(store).BindingHash, "ConfigurationMismatch");
    }

    internal static IReadOnlyList<StationMaintenanceStoredRow> ReadStationMaintenanceRows(sqlite3 database,
        StationMaintenanceConfiguration configuration, StoreDeadline deadline)
    {
        var count = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM station_maintenance_facts;", deadline);
        var bytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM station_maintenance_facts;", deadline);
        StationMaintenanceStorageCodec.Require(count <= configuration.MaximumOperations, "OperationCapacityExceeded");
        StationMaintenanceStorageCodec.Require(bytes <= configuration.MaximumTotalBytes, "TotalCapacityExceeded");
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,EventId,OperationId,Phase,Payload,
            ContentHash,AuditSequence,AuditHash FROM station_maintenance_facts ORDER BY Position;",
            deadline, statement =>
            {
                var position = SqliteNative.ColumnInt64(statement, 0);
                var payloadBytes = Encoding.UTF8.GetBytes(SqliteNative.ColumnText(statement, 4) ?? string.Empty);
                var payload = StationMaintenanceStorageCodec.Decode(payloadBytes, configuration);
                var hash = SqliteNative.ColumnText(statement, 5) ?? string.Empty;
                StationMaintenanceStorageCodec.Require(payload.EventId.ToString("D") ==
                        SqliteNative.ColumnText(statement, 1) &&
                    payload.OperationId.ToString("D") == SqliteNative.ColumnText(statement, 2) &&
                    (long)payload.Phase == SqliteNative.ColumnInt64(statement, 3) &&
                    hash == StationMaintenanceStorageCodec.ContentHash(position, configuration.BindingHash,
                        payloadBytes), "RowBindingInvalid");
                return new StationMaintenanceStoredRow(position, payload, payloadBytes, hash,
                    SqliteNative.ColumnInt64(statement, 6), SqliteNative.ColumnText(statement, 7) ?? string.Empty);
            });
        ValidateStationMaintenanceHistory(configuration, rows);
        return rows;
    }

    /// <summary>Replays every stored operation with its one exact legal transition sequence.</summary>
    internal static void ValidateStationMaintenanceHistory(StationMaintenanceConfiguration configuration,
        IReadOnlyList<StationMaintenanceStoredRow> rows)
    {
        foreach (var group in rows.GroupBy(row => row.Payload.OperationId))
        {
            var history = group.OrderBy(row => row.Payload.AggregateSequence).ToArray();
            var first = history[0].Payload;
            StationMaintenanceStorageCodec.Require(first.Phase == StationMaintenanceFactPhase.Admitted &&
                first.AggregateSequence == 1 && first.ReasonCode == StationMaintenanceAdmittedReason &&
                history.Length <= StationMaintenanceStoreOptions.MaximumOperationFactsHardLimit - 1,
                "OperationStartInvalid");
            for (var index = 1; index < history.Length; index++)
            {
                var previous = history[index - 1].Payload;
                var current = history[index].Payload;
                StationMaintenanceStorageCodec.Require(previous.Phase == StationMaintenanceFactPhase.Admitted &&
                    current.AggregateSequence == previous.AggregateSequence + 1 &&
                    current.Phase is StationMaintenanceFactPhase.Prepared or StationMaintenanceFactPhase.Failed &&
                    current.Previous is not null &&
                    current.Previous.EventId == previous.EventId &&
                    current.Previous.Sequence == history[index - 1].AuditSequence &&
                    current.Previous.Hash == history[index - 1].AuditHash &&
                    current.PreviousContentHash == history[index - 1].ContentHash,
                    "OperationTransitionInvalid");
                RequireSameOperationBinding(previous, current);
            }
        }
    }

    private static void RequireSameOperationBinding(StationMaintenancePayload previous,
        StationMaintenancePayload current)
    {
        StationMaintenanceStorageCodec.Require(current.EventId != previous.EventId &&
            current.OperationId == previous.OperationId &&
            current.CommandCorrelationId == previous.CommandCorrelationId &&
            current.RuntimeEpoch == previous.RuntimeEpoch && current.Reason == previous.Reason &&
            current.ActorPrincipalId == previous.ActorPrincipalId && current.SessionId == previous.SessionId &&
            current.StepUpGrantId == previous.StepUpGrantId &&
            current.AuthorizationRevision == previous.AuthorizationRevision &&
            current.AuthorizationTarget == previous.AuthorizationTarget &&
            current.AuthorizationPolicyId == previous.AuthorizationPolicyId &&
            current.AuthorizationPolicyVersion == previous.AuthorizationPolicyVersion &&
            current.AuthorizationPolicyHash == previous.AuthorizationPolicyHash &&
            current.PolicyHash == previous.PolicyHash && current.ConfigurationHash == previous.ConfigurationHash &&
            current.InstallationBindingHash == previous.InstallationBindingHash &&
            current.RuntimeApplicationHash == previous.RuntimeApplicationHash &&
            current.ProcessId == previous.ProcessId &&
            current.ProcessStartedUtc == previous.ProcessStartedUtc &&
            current.ExpiresAtUtc == previous.ExpiresAtUtc &&
            current.AdmittedDeadlineUtc == previous.AdmittedDeadlineUtc &&
            current.Command.EventId == previous.Command.EventId &&
            current.Authorization.EventId == previous.Authorization.EventId,
            "OperationBindingChanged");
    }

    /// <summary>
    /// The remaining terminal-fact reserve of every open operation: an Admitted operation still
    /// owes exactly one future Prepared or Failed fact, every terminal operation none.
    /// </summary>
    internal static long ReadStationMaintenanceAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(CASE latest.Phase WHEN 1 THEN 1 ELSE 0 END),0) FROM (
                SELECT f.Phase FROM station_maintenance_facts f
                WHERE f.AggregateSequence=(SELECT MAX(g.AggregateSequence)
                    FROM station_maintenance_facts g WHERE g.OperationId=f.OperationId)) AS latest;", deadline);

    /// <summary>
    /// Re-proves one admitted fact against its accepted command fact and its
    /// StationMaintenanceAuthorized identity event. This is the exact command/identity feature
    /// gate of the ledger: the operation, the required permission and the closed identity event
    /// kind all have to agree.
    /// </summary>
    internal static void RequireStationMaintenanceAuthority(sqlite3 database,
        StationMaintenanceStoredRow row, StoreDeadline deadline)
    {
        var payload = row.Payload;
        if (payload.Phase != StationMaintenanceFactPhase.Admitted) return;
        VerifyStationMaintenanceOperationAuthority(database, payload, deadline);
    }

    private static void VerifyStationMaintenanceOperationAuthority(sqlite3 database,
        StationMaintenancePayload payload, StoreDeadline deadline)
    {
        var station = AuditChainDatabase.Text(database,
            "SELECT StationId FROM identity_policy_binding WHERE Id=1;", deadline)!;
        var commandRows = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Sequence=? AND Kind='CommandFact' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            Number(payload.Command.Sequence));
        StationMaintenanceStorageCodec.Require(commandRows.Count == 1 &&
            commandRows[0].Hash == payload.Command.Hash, "CommandAuditMissing");
        var exactCommand = ReadCommandAuditReference(database, payload.Command.EventId, deadline);
        StationMaintenanceStorageCodec.Require(exactCommand.Sequence == payload.Command.Sequence &&
            exactCommand.Hash == payload.Command.Hash, "CommandReferenceMismatch");
        var facts = AuditChainDatabase.Read(database, @"SELECT CorrelationId,CommandKind,
            AuthenticatedHumanPrincipalId,Phase,Disposition,ReasonCode,ClaimedSessionId,ClaimedStepUpGrantId
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline,
            statement => (CorrelationId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Kind: checked((int)SqliteNative.ColumnInt64(statement, 1)),
                Actor: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Phase: SqliteNative.ColumnInt64(statement, 3),
                Disposition: SqliteNative.ColumnInt64(statement, 4),
                Reason: SqliteNative.ColumnText(statement, 5) ?? string.Empty,
                Session: SqliteNative.ColumnText(statement, 6) ?? string.Empty,
                Grant: SqliteNative.ColumnText(statement, 7) ?? string.Empty),
            payload.Command.EventId.ToString("D"));
        StationMaintenanceStorageCodec.Require(facts.Count == 1 &&
            facts[0].Kind == (int)AuditedCommandKind.PrepareStationMaintenance &&
            facts[0].Actor == payload.ActorPrincipalId.ToString("D") &&
            facts[0].Phase == (int)CommandAuditPhase.Outcome &&
            facts[0].Disposition == (int)CommandDisposition.Accepted &&
            facts[0].Reason == payload.ReasonCode &&
            facts[0].Session == payload.SessionId.ToString("D") &&
            facts[0].Grant == payload.StepUpGrantId.ToString("D") &&
            Guid.TryParseExact(facts[0].Grant, "D", out var actualGrant) && actualGrant != Guid.Empty &&
            facts[0].CorrelationId == payload.CommandCorrelationId.ToString("D"),
            "CommandBindingMismatch");
        var commandCorrelation = facts[0].CorrelationId;
        var authorizationRows = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty),
            Number(payload.Authorization.Sequence));
        StationMaintenanceStorageCodec.Require(authorizationRows.Count == 1 &&
            authorizationRows[0].Sequence == payload.Authorization.Sequence &&
            authorizationRows[0].Hash == payload.Authorization.Hash && authorizationRows[0].Ordinal > 0,
            "AuthorizationAuditMissing");
        byte[] authorizationBytes;
        try { authorizationBytes = Convert.FromBase64String(authorizationRows[0].Payload); }
        catch (FormatException) { throw new InvalidOperationException("StationMaintenanceAuthorizationAuditInvalid"); }
        string?[] fields;
        try { fields = DecodeContractIdentityFields(authorizationBytes); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or EndOfStreamException
            or DecoderFallbackException)
        { throw new InvalidOperationException("StationMaintenanceAuthorizationAuditInvalid"); }
        IdentityAuditEvent.VerifyPayload(authorizationBytes, authorizationRows[0].Ordinal, station,
            StationMaintenanceStoreOptions.SchemaVersion);
        StationMaintenanceStorageCodec.Require(fields.Length == 49 &&
            fields[1] == payload.Authorization.EventId.ToString("D") &&
            fields[2] == nameof(IdentityEventKind.StationMaintenanceAuthorized) &&
            fields[4] == station && fields[5] == payload.ActorPrincipalId.ToString("D") &&
            fields[9] == payload.ReasonCode && fields[25] == payload.SessionId.ToString("D") &&
            fields[27] == payload.AuthorizationPolicyId && fields[28] == payload.AuthorizationPolicyVersion &&
            fields[29] == payload.AuthorizationPolicyHash &&
            fields[30] == payload.ActorPrincipalId.ToString("D") &&
            fields[31] == commandCorrelation &&
            fields[32] == facts[0].Grant && fields[33] == Permission.PrepareStationMaintenance.ToString() &&
            fields[37] == payload.AuthorizationTarget && fields[38] == commandCorrelation &&
            fields[39] == AuditedCommandKind.PrepareStationMaintenance.ToString() &&
            fields[42] == payload.OperationId.ToString("D"),
            "AuthorityBindingMismatch");
    }

    /// <summary>
    /// One-to-one coverage of the accepted maintenance command facts and the admitted rows.
    /// No accepted command may exist without exactly one durable admitted fact.
    /// </summary>
    internal static void RequireStationMaintenanceAuthorityCoverage(sqlite3 database,
        IReadOnlyList<StationMaintenanceStoredRow> rows, StoreDeadline deadline)
    {
        var accepted = AuditChainDatabase.Read(database, @"SELECT EventId,CommandKind FROM command_facts
            WHERE Phase=? AND Disposition=? AND CommandKind=? ORDER BY Position;", deadline,
            statement => (EventId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Kind: checked((int)SqliteNative.ColumnInt64(statement, 1))),
            Number((long)CommandAuditPhase.Outcome), Number((long)CommandDisposition.Accepted),
            Number((long)AuditedCommandKind.PrepareStationMaintenance));
        var admitted = rows.Where(row => row.Payload.Phase == StationMaintenanceFactPhase.Admitted)
            .Select(row => row.Payload.Command.EventId.ToString("D")).ToArray();
        StationMaintenanceStorageCodec.Require(
            admitted.Distinct(StringComparer.Ordinal).Count() == admitted.Length &&
            admitted.Length == accepted.Count(value => value.Kind == (int)AuditedCommandKind.PrepareStationMaintenance) &&
            accepted.All(value => admitted.Contains(value.EventId, StringComparer.Ordinal)),
            "CoverageMismatch");
    }

    internal static string StationMaintenanceNumber(long value) => value.ToString(CultureInfo.InvariantCulture);
}
