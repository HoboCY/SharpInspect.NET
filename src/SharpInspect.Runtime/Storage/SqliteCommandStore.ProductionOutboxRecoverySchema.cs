using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal const string ProductionOutboxRecoveryConfigTable = "production_outbox_recovery_config";
    internal const string ProductionOutboxRecoveryActivationKind = "ProductionOutboxRecoveryActivated";

    internal static byte[] ProductionOutboxRecoveryActivationPayload(ProductionOutboxStoreOptions outbox) =>
        AuditCanonical.Encode("ProductionOutboxRecoveryActivationV1",
            ProductionOutboxRecoveryConfigBindingHash(outbox, outbox.ManualRecovery!));

    /// <summary>
    /// Schema-37 tables. Existing schema-36 tables, rows and hashes stay byte-for-byte
    /// untouched; the extension only adds immutable recovery/correction facts and their
    /// bounded configuration row. Every trigger mirrors the schema-36 immutability and
    /// contiguous-position rules so no row can be edited, deleted or reordered.
    /// </summary>
    internal const string ProductionOutboxRecoverySchemaSql = @"
        CREATE TABLE production_outbox_recovery_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            OutboxBindingHash TEXT NOT NULL CHECK(length(OutboxBindingHash)=64),
            RecoveryBindingHash TEXT NOT NULL CHECK(length(RecoveryBindingHash)=64),
            MaximumGrantedAttempts INTEGER NOT NULL CHECK(MaximumGrantedAttempts BETWEEN 1 AND 16),
            MaximumCumulativeGrantedAttempts INTEGER NOT NULL CHECK(MaximumCumulativeGrantedAttempts BETWEEN 1 AND 32),
            MaximumRecoveryOperations INTEGER NOT NULL CHECK(MaximumRecoveryOperations BETWEEN 1 AND 256),
            MaximumCorrections INTEGER NOT NULL CHECK(MaximumCorrections BETWEEN 1 AND 256),
            MaximumCorrectionPayloadBytes INTEGER NOT NULL CHECK(MaximumCorrectionPayloadBytes BETWEEN 1 AND 8388608),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE production_outbox_recovery(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            RecoveryId TEXT NOT NULL UNIQUE CHECK(length(RecoveryId)=36),
            DeliveryId TEXT NOT NULL CHECK(length(DeliveryId)=36)
                REFERENCES production_outbox_deliveries(DeliveryId),
            GrantedAttempts INTEGER NOT NULL CHECK(GrantedAttempts BETWEEN 1 AND 16),
            Reason TEXT NOT NULL CHECK(length(Reason) BETWEEN 1 AND 256),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            StepUpGrantId TEXT NOT NULL CHECK(length(StepUpGrantId)=36),
            AuthorizationPolicyId TEXT NOT NULL CHECK(length(AuthorizationPolicyId) BETWEEN 1 AND 64),
            AuthorizationPolicyVersion TEXT NOT NULL CHECK(length(AuthorizationPolicyVersion) BETWEEN 1 AND 64),
            AuthorizationPolicyHash TEXT NOT NULL CHECK(length(AuthorizationPolicyHash)=64),
            CommandCorrelationId TEXT NOT NULL CHECK(length(CommandCorrelationId)=36),
            CommandEventId TEXT NOT NULL CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            RecordedAtUtc TEXT NOT NULL CHECK(length(RecordedAtUtc)=33),
            TargetBinding TEXT NOT NULL CHECK(length(TargetBinding) BETWEEN 1 AND 128),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64));
        CREATE TABLE production_outbox_corrections(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            DeliveryId TEXT NOT NULL UNIQUE CHECK(length(DeliveryId)=36)
                REFERENCES production_outbox_deliveries(DeliveryId),
            SourceDeliveryId TEXT NOT NULL CHECK(length(SourceDeliveryId)=36)
                REFERENCES production_outbox_deliveries(DeliveryId),
            Reason TEXT NOT NULL CHECK(length(Reason) BETWEEN 1 AND 256),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            StepUpGrantId TEXT NOT NULL CHECK(length(StepUpGrantId)=36),
            AuthorizationPolicyId TEXT NOT NULL CHECK(length(AuthorizationPolicyId) BETWEEN 1 AND 64),
            AuthorizationPolicyVersion TEXT NOT NULL CHECK(length(AuthorizationPolicyVersion) BETWEEN 1 AND 64),
            AuthorizationPolicyHash TEXT NOT NULL CHECK(length(AuthorizationPolicyHash)=64),
            CommandCorrelationId TEXT NOT NULL CHECK(length(CommandCorrelationId)=36),
            CommandEventId TEXT NOT NULL CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            RecordedAtUtc TEXT NOT NULL CHECK(length(RecordedAtUtc)=33),
            TargetBinding TEXT NOT NULL CHECK(length(TargetBinding) BETWEEN 1 AND 128),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            CHECK(DeliveryId<>SourceDeliveryId));
        CREATE TRIGGER production_outbox_recovery_config_immutable_update BEFORE UPDATE
            ON production_outbox_recovery_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxRecoveryConfiguration');
        END;
        CREATE TRIGGER production_outbox_recovery_config_immutable_delete BEFORE DELETE
            ON production_outbox_recovery_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxRecoveryConfiguration');
        END;
        CREATE TRIGGER production_outbox_recovery_immutable_update BEFORE UPDATE
            ON production_outbox_recovery BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxRecovery');
        END;
        CREATE TRIGGER production_outbox_recovery_immutable_delete BEFORE DELETE
            ON production_outbox_recovery BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxRecovery');
        END;
        CREATE TRIGGER production_outbox_recovery_insert BEFORE INSERT
            ON production_outbox_recovery BEGIN
            SELECT CASE WHEN NEW.Position <>
                COALESCE((SELECT MAX(Position) FROM production_outbox_recovery),0)+1
                THEN RAISE(ABORT,'ProductionOutboxRecoveryPositionGap') END;
        END;
        CREATE TRIGGER production_outbox_correction_immutable_update BEFORE UPDATE
            ON production_outbox_corrections BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxCorrection');
        END;
        CREATE TRIGGER production_outbox_correction_immutable_delete BEFORE DELETE
            ON production_outbox_corrections BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxCorrection');
        END;
        CREATE TRIGGER production_outbox_correction_insert BEFORE INSERT
            ON production_outbox_corrections BEGIN
            SELECT CASE WHEN NEW.Position <>
                COALESCE((SELECT MAX(Position) FROM production_outbox_corrections),0)+1
                THEN RAISE(ABORT,'ProductionOutboxCorrectionPositionGap') END;
        END;";

    internal static void InitializeProductionOutboxRecoveryTables(sqlite3 database,
        ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(recovery);
        outbox.Validate();
        recovery.Validate();
        SqliteNative.Execute(database, ProductionOutboxRecoverySchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO production_outbox_recovery_config
            (Id,FormatVersion,OutboxBindingHash,RecoveryBindingHash,MaximumGrantedAttempts,
             MaximumCumulativeGrantedAttempts,MaximumRecoveryOperations,MaximumCorrections,
             MaximumCorrectionPayloadBytes,BindingHash)
            VALUES(1,?,?,?,?,?,?,?,?,?);", deadline,
            ProductionOutboxNumber(ProductionOutboxRecoveryOptions.FormatVersion), outbox.BindingHash,
            recovery.BindingHash, ProductionOutboxNumber(recovery.MaximumGrantedAttempts),
            ProductionOutboxNumber(recovery.MaximumCumulativeGrantedAttempts),
            ProductionOutboxNumber(recovery.MaximumRecoveryOperations),
            ProductionOutboxNumber(recovery.MaximumCorrections),
            ProductionOutboxNumber(recovery.MaximumCorrectionPayloadBytes),
            ProductionOutboxRecoveryConfigBindingHash(outbox, recovery));
    }

    /// <summary>
    /// Adds the schema-37 extension inside the caller's migration or fresh-store transaction.
    /// The outbox configuration must already be present and exact: the migration never invents
    /// an outbox row, it only extends an existing schema-36 store.
    /// </summary>
    internal static void InitializeProductionOutboxRecoverySchema(sqlite3 database,
        ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey key)
    {
        // The caller has already created or migrated the exact schema-36 outbox configuration;
        // this only adds the extension tables and their immutable configuration row.
        InitializeProductionOutboxRecoveryTables(database, outbox, recovery, deadline);
        AuditChainDatabase.AppendProductionOutboxRecoveryActivation(database, policy, key, outbox, deadline);
    }

    internal static string ProductionOutboxRecoveryConfigBindingHash(ProductionOutboxStoreOptions outbox,
        ProductionOutboxRecoveryOptions recovery)
    {
        recovery.Validate();
        return Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("ProductionOutboxRecoveryConfigV1",
            ProductionOutboxNumber(ProductionOutboxRecoveryOptions.FormatVersion), outbox.BindingHash,
            recovery.BindingHash, ProductionOutboxNumber(recovery.MaximumGrantedAttempts),
            ProductionOutboxNumber(recovery.MaximumCumulativeGrantedAttempts),
            ProductionOutboxNumber(recovery.MaximumRecoveryOperations),
            ProductionOutboxNumber(recovery.MaximumCorrections),
            ProductionOutboxNumber(recovery.MaximumCorrectionPayloadBytes))));
    }

    /// <summary>
    /// Requires the single immutable schema-37 configuration row to equal the caller's declared
    /// outbox binding and exact recovery budget. A missing, foreign or edited row fails closed.
    /// </summary>
    internal static void RequireConfiguredProductionOutboxRecovery(sqlite3 database,
        ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(recovery);
        outbox.Validate();
        recovery.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,OutboxBindingHash,RecoveryBindingHash,
            MaximumGrantedAttempts,MaximumCumulativeGrantedAttempts,MaximumRecoveryOperations,MaximumCorrections,
            MaximumCorrectionPayloadBytes,BindingHash
            FROM production_outbox_recovery_config WHERE Id=1 LIMIT 2;", deadline, statement => new[]
        {
            SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            SqliteNative.ColumnText(statement, 1) ?? string.Empty,
            SqliteNative.ColumnText(statement, 2) ?? string.Empty,
            SqliteNative.ColumnText(statement, 3) ?? string.Empty,
            SqliteNative.ColumnText(statement, 4) ?? string.Empty,
            SqliteNative.ColumnText(statement, 5) ?? string.Empty,
            SqliteNative.ColumnText(statement, 6) ?? string.Empty,
            SqliteNative.ColumnText(statement, 7) ?? string.Empty,
            SqliteNative.ColumnText(statement, 8) ?? string.Empty
        });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
        {
            ProductionOutboxNumber(ProductionOutboxRecoveryOptions.FormatVersion), outbox.BindingHash,
            recovery.BindingHash, ProductionOutboxNumber(recovery.MaximumGrantedAttempts),
            ProductionOutboxNumber(recovery.MaximumCumulativeGrantedAttempts),
            ProductionOutboxNumber(recovery.MaximumRecoveryOperations),
            ProductionOutboxNumber(recovery.MaximumCorrections),
            ProductionOutboxNumber(recovery.MaximumCorrectionPayloadBytes),
            ProductionOutboxRecoveryConfigBindingHash(outbox, recovery)
        }, StringComparer.Ordinal), "ProductionOutboxRecoveryConfigurationMismatch");
    }

    internal sealed record ProductionOutboxRecoveryStoredRow(long Position, Guid RecoveryId, Guid DeliveryId,
        int GrantedAttempts, string Reason, Guid ActorPrincipalId, Guid ActorSessionId, Guid StepUpGrantId,
        string AuthorizationPolicyId, string AuthorizationPolicyVersion, string AuthorizationPolicyHash,
        Guid CommandCorrelationId, Guid CommandEventId, long CommandAuditSequence, string CommandAuditHash,
        Guid AuthorizationEventId, long AuthorizationAuditSequence, string AuthorizationAuditHash,
        DateTimeOffset RecordedAtUtc, string TargetBinding, string ContentHash)
    {
        internal ProductionOutboxRecoveryRecord Project() => new(RecoveryId, DeliveryId, GrantedAttempts, Reason,
            ActorPrincipalId, RecordedAtUtc, AuthorizationAuditSequence, ContentHash);
    }

    internal sealed record ProductionOutboxCorrectionStoredRow(long Position, Guid DeliveryId,
        Guid SourceDeliveryId, string Reason, string PayloadHash, Guid ActorPrincipalId, Guid ActorSessionId,
        Guid StepUpGrantId, string AuthorizationPolicyId, string AuthorizationPolicyVersion,
        string AuthorizationPolicyHash, Guid CommandCorrelationId, Guid CommandEventId,
        long CommandAuditSequence, string CommandAuditHash, Guid AuthorizationEventId,
        long AuthorizationAuditSequence, string AuthorizationAuditHash, DateTimeOffset RecordedAtUtc,
        string TargetBinding, string ContentHash)
    {
        internal ProductionOutboxCorrectionRecord Project() => new(DeliveryId, SourceDeliveryId, Reason,
            PayloadHash, ActorPrincipalId, RecordedAtUtc, AuthorizationAuditSequence, ContentHash);
    }

    internal sealed record ProductionOutboxRecoveryGrant(Guid DeliveryId, Guid RecoveryId,
        long AuthorizationAuditSequence, int GrantedAttempts);

    internal static string ProductionOutboxRecoveryContentHash(Guid recoveryId, Guid deliveryId, int grantedAttempts,
        string reason, Guid actorPrincipalId, Guid actorSessionId, Guid stepUpGrantId,
        string authorizationPolicyId, string authorizationPolicyVersion, string authorizationPolicyHash,
        Guid commandCorrelationId, Guid commandEventId, long commandAuditSequence, string commandAuditHash,
        Guid authorizationEventId, long authorizationAuditSequence, string authorizationAuditHash,
        DateTimeOffset recordedAtUtc, string targetBinding) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("ProductionOutboxRecoveryV1",
            ProductionOutboxNumber(grantedAttempts), recoveryId.ToString("D"), deliveryId.ToString("D"), reason,
            actorPrincipalId.ToString("D"), actorSessionId.ToString("D"), stepUpGrantId.ToString("D"),
            authorizationPolicyId, authorizationPolicyVersion, authorizationPolicyHash,
            commandCorrelationId.ToString("D"), commandEventId.ToString("D"),
            ProductionOutboxNumber(commandAuditSequence), commandAuditHash, authorizationEventId.ToString("D"),
            ProductionOutboxNumber(authorizationAuditSequence), authorizationAuditHash,
            recordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), targetBinding)));

    internal static string ProductionOutboxCorrectionContentHash(Guid deliveryId, Guid sourceDeliveryId,
        string reason, string payloadHash, Guid actorPrincipalId, Guid actorSessionId, Guid stepUpGrantId,
        string authorizationPolicyId, string authorizationPolicyVersion, string authorizationPolicyHash,
        Guid commandCorrelationId, Guid commandEventId, long commandAuditSequence, string commandAuditHash,
        Guid authorizationEventId, long authorizationAuditSequence, string authorizationAuditHash,
        DateTimeOffset recordedAtUtc, string targetBinding) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("ProductionOutboxCorrectionV1",
            deliveryId.ToString("D"), sourceDeliveryId.ToString("D"), reason, payloadHash,
            actorPrincipalId.ToString("D"), actorSessionId.ToString("D"), stepUpGrantId.ToString("D"),
            authorizationPolicyId, authorizationPolicyVersion, authorizationPolicyHash,
            commandCorrelationId.ToString("D"), commandEventId.ToString("D"),
            ProductionOutboxNumber(commandAuditSequence), commandAuditHash, authorizationEventId.ToString("D"),
            ProductionOutboxNumber(authorizationAuditSequence), authorizationAuditHash,
            recordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), targetBinding)));

    /// <summary>Reads and re-proves every immutable schema-37 recovery row.</summary>
    internal static IReadOnlyList<ProductionOutboxRecoveryStoredRow> ReadProductionOutboxRecoveryRows(
        sqlite3 database, ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery,
        StoreDeadline deadline)
    {
        RequireConfiguredProductionOutboxRecovery(database, outbox, recovery, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,RecoveryId,DeliveryId,GrantedAttempts,Reason,ActorPrincipalId,ActorSessionId,
                StepUpGrantId,AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,
                CommandCorrelationId,CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                AuthorizationAuditSequence,AuthorizationAuditHash,RecordedAtUtc,TargetBinding,ContentHash
            FROM production_outbox_recovery ORDER BY Position LIMIT ?;", deadline,
            statement => ReadProductionOutboxRecoveryRow(statement),
            checked(recovery.MaximumRecoveryOperations + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= recovery.MaximumRecoveryOperations,
            "ProductionOutboxRecoveryEntryCapacityExceeded");
        return rows;
    }

    internal static IReadOnlyList<ProductionOutboxCorrectionStoredRow> ReadProductionOutboxCorrectionRows(
        sqlite3 database, ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery,
        StoreDeadline deadline)
    {
        RequireConfiguredProductionOutboxRecovery(database, outbox, recovery, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,DeliveryId,SourceDeliveryId,Reason,PayloadHash,ActorPrincipalId,ActorSessionId,
                StepUpGrantId,AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,
                CommandCorrelationId,CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                AuthorizationAuditSequence,AuthorizationAuditHash,RecordedAtUtc,TargetBinding,ContentHash
            FROM production_outbox_corrections ORDER BY Position LIMIT ?;", deadline,
            statement => ReadProductionOutboxCorrectionRow(statement),
            checked(recovery.MaximumCorrections + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= recovery.MaximumCorrections,
            "ProductionOutboxCorrectionEntryCapacityExceeded");
        return rows;
    }

    internal static IReadOnlyList<ProductionOutboxRecoveryGrant> ReadProductionOutboxRecoveryGrants(
        sqlite3 database, ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery,
        StoreDeadline deadline) =>
        ReadProductionOutboxRecoveryRows(database, outbox, recovery, deadline)
            .Select(row => new ProductionOutboxRecoveryGrant(row.DeliveryId, row.RecoveryId,
                row.AuthorizationAuditSequence, row.GrantedAttempts)).ToArray();

    private static ProductionOutboxRecoveryStoredRow ReadProductionOutboxRecoveryRow(sqlite3_stmt statement)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var recoveryId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 1));
        var deliveryId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 2));
        var granted = checked((int)SqliteNative.ColumnInt64(statement, 3));
        var reason = SqliteNative.ColumnText(statement, 4) ?? string.Empty;
        var actor = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 5));
        var session = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 6));
        var grant = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 7));
        var policyId = SqliteNative.ColumnText(statement, 8) ?? string.Empty;
        var policyVersion = SqliteNative.ColumnText(statement, 9) ?? string.Empty;
        var policyHash = SqliteNative.ColumnText(statement, 10) ?? string.Empty;
        var correlation = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 11));
        var commandEvent = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 12));
        var commandSequence = SqliteNative.ColumnInt64(statement, 13);
        var commandHash = SqliteNative.ColumnText(statement, 14) ?? string.Empty;
        var authorizationEvent = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 15));
        var authorizationSequence = SqliteNative.ColumnInt64(statement, 16);
        var authorizationHash = SqliteNative.ColumnText(statement, 17) ?? string.Empty;
        var recordedAt = ParseProductionOutboxTime(SqliteNative.ColumnText(statement, 18));
        var target = SqliteNative.ColumnText(statement, 19) ?? string.Empty;
        var contentHash = SqliteNative.ColumnText(statement, 20) ?? string.Empty;
        AuditChainDatabase.Require(position > 0 && granted is >= 1 and <= 16 && commandSequence > 0 &&
            authorizationSequence > 0 && AuditCanonical.IsHash(policyHash) && AuditCanonical.IsHash(commandHash) &&
            AuditCanonical.IsHash(authorizationHash) && AuditCanonical.IsHash(contentHash),
            "ProductionOutboxRecoveryRowInvalid");
        return new(position, recoveryId, deliveryId, granted, reason, actor, session, grant, policyId,
            policyVersion, policyHash, correlation, commandEvent, commandSequence, commandHash,
            authorizationEvent, authorizationSequence, authorizationHash, recordedAt, target, contentHash);
    }

    private static ProductionOutboxCorrectionStoredRow ReadProductionOutboxCorrectionRow(sqlite3_stmt statement)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var deliveryId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 1));
        var sourceId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 2));
        var reason = SqliteNative.ColumnText(statement, 3) ?? string.Empty;
        var payloadHash = SqliteNative.ColumnText(statement, 4) ?? string.Empty;
        var actor = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 5));
        var session = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 6));
        var grant = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 7));
        var policyId = SqliteNative.ColumnText(statement, 8) ?? string.Empty;
        var policyVersion = SqliteNative.ColumnText(statement, 9) ?? string.Empty;
        var policyHash = SqliteNative.ColumnText(statement, 10) ?? string.Empty;
        var correlation = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 11));
        var commandEvent = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 12));
        var commandSequence = SqliteNative.ColumnInt64(statement, 13);
        var commandHash = SqliteNative.ColumnText(statement, 14) ?? string.Empty;
        var authorizationEvent = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 15));
        var authorizationSequence = SqliteNative.ColumnInt64(statement, 16);
        var authorizationHash = SqliteNative.ColumnText(statement, 17) ?? string.Empty;
        var recordedAt = ParseProductionOutboxTime(SqliteNative.ColumnText(statement, 18));
        var target = SqliteNative.ColumnText(statement, 19) ?? string.Empty;
        var contentHash = SqliteNative.ColumnText(statement, 20) ?? string.Empty;
        AuditChainDatabase.Require(position > 0 && deliveryId != sourceId && commandSequence > 0 &&
            authorizationSequence > 0 && AuditCanonical.IsHash(payloadHash) && AuditCanonical.IsHash(policyHash) &&
            AuditCanonical.IsHash(commandHash) && AuditCanonical.IsHash(authorizationHash) &&
            AuditCanonical.IsHash(contentHash), "ProductionOutboxCorrectionRowInvalid");
        return new(position, deliveryId, sourceId, reason, payloadHash, actor, session, grant, policyId,
            policyVersion, policyHash, correlation, commandEvent, commandSequence, commandHash,
            authorizationEvent, authorizationSequence, authorizationHash, recordedAt, target, contentHash);
    }

    /// <summary>
    /// Re-proves the complete schema-37 extension before it is projected: the exact immutable
    /// configuration row, the count and cumulative-grant bounds, the correction chain bound and
    /// the bidirectional binding of every operation row to its signed identity event and command
    /// fact. Existing schema-36 rows are untouched and are re-proved by the schema-36 guard.
    /// </summary>
    internal static void ValidateProductionOutboxRecoveryHistory(sqlite3 database,
        ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery,
        ProductionInspectionStoreOptions productionOptions, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(productionOptions);
        RequireConfiguredProductionOutboxRecovery(database, outbox, recovery, deadline);
        var deliveries = ReadProductionOutboxDeliveries(database, outbox, deadline);
        var recoveryRows = ReadProductionOutboxRecoveryRows(database, outbox, recovery, deadline);
        var correctionRows = ReadProductionOutboxCorrectionRows(database, outbox, recovery, deadline);
        VerifyProductionOutboxGovernanceTargets(database, deliveries,
            ReadProductionOutboxRows(database, outbox, deadline), recoveryRows, correctionRows, deadline);
        var station = ReadContractStationId(database, deadline);
        var grants = new Dictionary<Guid, int>();
        foreach (var row in recoveryRows)
        {
            var delivery = deliveries.SingleOrDefault(value => value.Delivery.DeliveryId == row.DeliveryId) ??
                throw new InvalidOperationException("ProductionOutboxRecoveryDeliveryMissing");
            grants.TryGetValue(row.DeliveryId, out var granted);
            granted = checked(granted + row.GrantedAttempts);
            AuditChainDatabase.Require(granted <= recovery.MaximumCumulativeGrantedAttempts &&
                delivery.Delivery.MaximumAttempts + granted <= ProductionOutboxStoreOptions.MaximumAttemptsHardLimit,
                "ProductionOutboxRecoveryGrantCapacityExceeded");
            grants[row.DeliveryId] = granted;
            if (ProductionOutboxRecoveryContentHash(row.RecoveryId, row.DeliveryId, row.GrantedAttempts,
                    row.Reason, row.ActorPrincipalId, row.ActorSessionId, row.StepUpGrantId,
                    row.AuthorizationPolicyId, row.AuthorizationPolicyVersion, row.AuthorizationPolicyHash,
                    row.CommandCorrelationId, row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash,
                    row.AuthorizationEventId, row.AuthorizationAuditSequence, row.AuthorizationAuditHash,
                    row.RecordedAtUtc, row.TargetBinding) != row.ContentHash)
                throw new InvalidOperationException("ProductionOutboxRecoveryContentHashMismatch");
            VerifyProductionOutboxOperationAuthority(database, station, row.AuthorizationEventId,
                row.AuthorizationAuditSequence, row.AuthorizationAuditHash, IdentityEventKind.OutboxDeliveryRecovered,
                "OutboxRecoveryAuthorized", Permission.RecoverOutboxDelivery,
                AuditedCommandKind.RecoverOutboxDelivery, row.ActorPrincipalId, row.ActorSessionId,
                row.StepUpGrantId, row.AuthorizationPolicyId, row.AuthorizationPolicyVersion,
                row.AuthorizationPolicyHash, row.CommandCorrelationId, row.CommandEventId,
                row.CommandAuditSequence, row.CommandAuditHash, row.TargetBinding,
                ProductionOutboxRecoveryOperationBinding(row), deadline);
        }
        var byDelivery = correctionRows.ToDictionary(value => value.DeliveryId);
        foreach (var row in correctionRows)
        {
            var delivery = deliveries.SingleOrDefault(value => value.Delivery.DeliveryId == row.DeliveryId) ??
                throw new InvalidOperationException("ProductionOutboxCorrectionDeliveryMissing");
            AuditChainDatabase.Require(deliveries.Any(value => value.Delivery.DeliveryId == row.SourceDeliveryId),
                "ProductionOutboxCorrectionSourceMissing");
            AuditChainDatabase.Require(delivery.Delivery.Payload?.ContentHash == row.PayloadHash,
                "ProductionOutboxCorrectionPayloadMismatch");
            // The chain is finite and acyclic with a hard depth bound. A link can only reference
            // a row that already existed, so following sources strictly decreases position.
            var depth = 0;
            var cursor = row.SourceDeliveryId;
            while (byDelivery.TryGetValue(cursor, out var sourceLink))
            {
                if (++depth > 8) throw new InvalidOperationException("ProductionOutboxCorrectionChainExceeded");
                cursor = sourceLink.SourceDeliveryId;
            }
            if (ProductionOutboxCorrectionContentHash(row.DeliveryId, row.SourceDeliveryId, row.Reason,
                    row.PayloadHash, row.ActorPrincipalId, row.ActorSessionId, row.StepUpGrantId,
                    row.AuthorizationPolicyId, row.AuthorizationPolicyVersion, row.AuthorizationPolicyHash,
                    row.CommandCorrelationId, row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash,
                    row.AuthorizationEventId, row.AuthorizationAuditSequence, row.AuthorizationAuditHash,
                    row.RecordedAtUtc, row.TargetBinding) != row.ContentHash)
                throw new InvalidOperationException("ProductionOutboxCorrectionContentHashMismatch");
            VerifyProductionOutboxOperationAuthority(database, station, row.AuthorizationEventId,
                row.AuthorizationAuditSequence, row.AuthorizationAuditHash,
                IdentityEventKind.OutboxCorrectiveDeliveryCreated, "OutboxCorrectionCreated",
                Permission.CreateCorrectiveOutboxDelivery, AuditedCommandKind.CreateCorrectiveOutboxDelivery,
                row.ActorPrincipalId, row.ActorSessionId, row.StepUpGrantId, row.AuthorizationPolicyId,
                row.AuthorizationPolicyVersion, row.AuthorizationPolicyHash, row.CommandCorrelationId,
                row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash, row.TargetBinding,
                ProductionOutboxCorrectionOperationBinding(row), deadline);
        }
    }

    private static void VerifyProductionOutboxOperationAuthority(sqlite3 database, string station,
        Guid authorizationEventId, long authorizationSequence, string authorizationHash, IdentityEventKind kind,
        string reasonCode, Permission permission, AuditedCommandKind commandKind, Guid actorPrincipalId,
        Guid actorSessionId, Guid stepUpGrantId, string policyId, string policyVersion, string policyHash,
        Guid commandCorrelationId, Guid commandEventId, long commandAuditSequence, string commandAuditHash,
        string targetBinding, string operationBinding, StoreDeadline deadline,
        int authoritySchemaVersion = ProductionOutboxRecoveryOptions.SchemaVersion)
    {
        var authorization = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty),
            authorizationSequence.ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(authorization.Count == 1 && authorization[0].Hash == authorizationHash &&
            authorization[0].Ordinal > 0, "ProductionOutboxRecoveryAuthorizationAuditMissing");
        byte[] payload;
        try { payload = Convert.FromBase64String(authorization[0].Payload); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ProductionOutboxRecoveryAuthorizationAuditInvalid", exception); }
        string?[] fields;
        try { fields = DecodeContractIdentityFields(payload); }
        catch (InvalidOperationException exception)
        { throw new InvalidOperationException("ProductionOutboxRecoveryAuthorizationAuditInvalid", exception); }
        IdentityAuditEvent.VerifyPayload(payload, authorization[0].Ordinal, station,
            authoritySchemaVersion);
        AuditChainDatabase.Require(fields.Length == 49 && fields[1] == authorizationEventId.ToString("D") &&
            fields[2] == kind.ToString() && fields[4] == station && fields[5] == actorPrincipalId.ToString("D") &&
            fields[9] == reasonCode && fields[25] == actorSessionId.ToString("D") &&
            fields[27] == policyId && fields[28] == policyVersion && fields[29] == policyHash &&
            fields[30] == actorPrincipalId.ToString("D") && fields[31] == commandCorrelationId.ToString("D") &&
            fields[32] == stepUpGrantId.ToString("D") && fields[33] == permission.ToString() &&
            fields[37] == targetBinding && fields[38] == commandCorrelationId.ToString("D") &&
            fields[39] == commandKind.ToString() && fields[42] == commandCorrelationId.ToString("D") &&
            fields[45] == operationBinding, "ProductionOutboxRecoveryAuthorizationBindingMismatch");
        var command = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Sequence=? AND Kind='CommandFact' LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            commandAuditSequence.ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(command.Count == 1 && command[0].Hash == commandAuditHash,
            "ProductionOutboxRecoveryCommandAuditMissing");
        var exactCommand = ReadCommandAuditReference(database, commandEventId, deadline);
        AuditChainDatabase.Require(exactCommand.Sequence == commandAuditSequence && exactCommand.Hash == commandAuditHash,
            "ProductionOutboxRecoveryCommandReferenceMismatch");
        var facts = AuditChainDatabase.Read(database, @"SELECT CorrelationId,CommandKind,
            AuthenticatedHumanPrincipalId,Phase,Disposition,ReasonCode,ClaimedSessionId,ClaimedStepUpGrantId
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline,
            statement => (CorrelationId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Kind: checked((int)SqliteNative.ColumnInt64(statement, 1)),
                Actor: SqliteNative.ColumnText(statement, 2), Phase: SqliteNative.ColumnInt64(statement, 3),
                Disposition: SqliteNative.ColumnInt64(statement, 4), Reason: SqliteNative.ColumnText(statement, 5),
                Session: SqliteNative.ColumnText(statement, 6), Grant: SqliteNative.ColumnText(statement, 7)),
            commandEventId.ToString("D"));
        AuditChainDatabase.Require(facts.Count == 1 &&
            facts[0].CorrelationId == commandCorrelationId.ToString("D") &&
            facts[0].Kind == (int)commandKind && facts[0].Actor == actorPrincipalId.ToString("D") &&
            facts[0].Phase == (int)CommandAuditPhase.Outcome && facts[0].Disposition == (int)CommandDisposition.Accepted &&
            facts[0].Reason == reasonCode && facts[0].Session == actorSessionId.ToString("D") &&
            facts[0].Grant == stepUpGrantId.ToString("D"),
            "ProductionOutboxRecoveryCommandBindingMismatch");
    }
}
