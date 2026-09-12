using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema-36 production outbox storage. The ledger stores the frozen at-most-one obligation
/// per configured route of every production Core, the immutable attempt/outcome facts of the
/// single delivery lifecycle and the current per-delivery projection. It never opens a file,
/// a socket or a formatter and it never sends: the Runtime worker owns the transport.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ProductionOutboxActivationKind = "ProductionOutboxStoreActivated";

    internal static string ProductionOutboxAuditKind(OutboxEventKind kind) => kind switch
    {
        OutboxEventKind.Created => "ProductionOutboxCreated",
        OutboxEventKind.AttemptStarted => "ProductionOutboxAttemptStarted",
        OutboxEventKind.AttemptFailed => "ProductionOutboxAttemptFailed",
        OutboxEventKind.Succeeded => "ProductionOutboxSucceeded",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static bool IsProductionOutboxAuditKind(string kind) => kind is
        "ProductionOutboxCreated" or "ProductionOutboxAttemptStarted" or
        "ProductionOutboxAttemptFailed" or "ProductionOutboxSucceeded";

    internal const string ProductionOutboxSchemaSql = @"
        CREATE TABLE production_outbox_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            RouteSetHash TEXT NOT NULL CHECK(length(RouteSetHash)=64),
            RouteCount INTEGER NOT NULL CHECK(RouteCount BETWEEN 1 AND 64),
            MaximumAttempts INTEGER NOT NULL CHECK(MaximumAttempts>0),
            MaximumRetryDelayMilliseconds INTEGER NOT NULL CHECK(MaximumRetryDelayMilliseconds>0),
            AttemptTimeoutMilliseconds INTEGER NOT NULL CHECK(AttemptTimeoutMilliseconds>0),
            MaximumEvents INTEGER NOT NULL CHECK(MaximumEvents>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            MaximumPageSize INTEGER NOT NULL CHECK(MaximumPageSize>0),
            RecipeLifecycleBindingHash TEXT CHECK(RecipeLifecycleBindingHash IS NULL OR
                length(RecipeLifecycleBindingHash)=64),
            ImageEvidenceBindingHash TEXT CHECK(ImageEvidenceBindingHash IS NULL OR
                length(ImageEvidenceBindingHash)=64),
            ImageFinalizationBindingHash TEXT CHECK(ImageFinalizationBindingHash IS NULL OR
                length(ImageFinalizationBindingHash)=64),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64),
            CHECK(ImageEvidenceBindingHash IS NULL OR RecipeLifecycleBindingHash IS NOT NULL),
            CHECK(ImageFinalizationBindingHash IS NULL OR ImageEvidenceBindingHash IS NOT NULL));
        CREATE TABLE production_outbox_deliveries(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            DeliveryId TEXT NOT NULL UNIQUE CHECK(length(DeliveryId)=36),
            InspectionId TEXT NOT NULL CHECK(length(InspectionId)=36),
            CoreHash TEXT NOT NULL CHECK(length(CoreHash)=64),
            RouteSetHash TEXT NOT NULL CHECK(length(RouteSetHash)=64),
            RouteId TEXT NOT NULL CHECK(length(RouteId) BETWEEN 1 AND 64),
            RouteVersion TEXT NOT NULL CHECK(length(RouteVersion) BETWEEN 1 AND 64),
            RouteContentHash TEXT NOT NULL CHECK(length(RouteContentHash)=64),
            Criticality INTEGER NOT NULL CHECK(Criticality IN (1,2)),
            DestinationIdentity TEXT NOT NULL CHECK(length(DestinationIdentity) BETWEEN 1 AND 64),
            PayloadContractId TEXT NOT NULL CHECK(length(PayloadContractId) BETWEEN 1 AND 64),
            PayloadContractVersion TEXT NOT NULL CHECK(length(PayloadContractVersion) BETWEEN 1 AND 64),
            PayloadContractContentHash TEXT NOT NULL CHECK(length(PayloadContractContentHash)=64),
            ContentType TEXT NOT NULL CHECK(length(ContentType) BETWEEN 1 AND 128),
            ReceiverContractId TEXT NOT NULL CHECK(length(ReceiverContractId) BETWEEN 1 AND 64),
            ReceiverContractVersion TEXT NOT NULL CHECK(length(ReceiverContractVersion) BETWEEN 1 AND 64),
            ReceiverContractContentHash TEXT NOT NULL CHECK(length(ReceiverContractContentHash)=64),
            ReceiverPublicKeyBase64 TEXT NOT NULL CHECK(length(ReceiverPublicKeyBase64) BETWEEN 32 AND 2048),
            AdapterContractId TEXT NOT NULL CHECK(length(AdapterContractId) BETWEEN 1 AND 64),
            AdapterContractVersion TEXT NOT NULL CHECK(length(AdapterContractVersion) BETWEEN 1 AND 64),
            AdapterContractContentHash TEXT NOT NULL CHECK(length(AdapterContractContentHash)=64),
            RouteMaximumPayloadBytes INTEGER NOT NULL CHECK(RouteMaximumPayloadBytes BETWEEN 1 AND 8388608),
            PayloadBase64 TEXT,
            PayloadContentHash TEXT CHECK(PayloadContentHash IS NULL OR length(PayloadContentHash)=64),
            PayloadByteLength INTEGER CHECK(PayloadByteLength IS NULL OR PayloadByteLength>0),
            PreparationFailure TEXT CHECK(PreparationFailure IS NULL OR
                length(PreparationFailure) BETWEEN 1 AND 64),
            CreatedAtUtc TEXT NOT NULL CHECK(length(CreatedAtUtc)=33),
            MaximumAttempts INTEGER NOT NULL CHECK(MaximumAttempts BETWEEN 1 AND 32),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            CreatedEventId TEXT NOT NULL CHECK(length(CreatedEventId)=36),
            CreatedAuditSequence INTEGER NOT NULL CHECK(CreatedAuditSequence>0),
            CreatedAuditHash TEXT NOT NULL CHECK(length(CreatedAuditHash)=64),
            CHECK((PayloadBase64 IS NULL)<>(PreparationFailure IS NULL)),
            CHECK(PayloadBase64 IS NULL OR (PayloadContentHash IS NOT NULL AND PayloadByteLength IS NOT NULL)),
            CHECK(PayloadBase64 IS NOT NULL OR Criticality=2));
        CREATE TABLE production_outbox_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            DeliveryId TEXT NOT NULL REFERENCES production_outbox_deliveries(DeliveryId),
            InspectionId TEXT NOT NULL CHECK(length(InspectionId)=36),
            CoreHash TEXT NOT NULL CHECK(length(CoreHash)=64),
            RouteSetHash TEXT NOT NULL CHECK(length(RouteSetHash)=64),
            RouteId TEXT NOT NULL CHECK(length(RouteId) BETWEEN 1 AND 64),
            RouteVersion TEXT NOT NULL CHECK(length(RouteVersion) BETWEEN 1 AND 64),
            RouteContentHash TEXT NOT NULL CHECK(length(RouteContentHash)=64),
            AggregateSequence INTEGER NOT NULL CHECK(AggregateSequence>0),
            Kind TEXT NOT NULL CHECK(Kind IN ('Created','AttemptStarted','AttemptFailed','Succeeded')),
            AttemptId TEXT CHECK(AttemptId IS NULL OR length(AttemptId)=36),
            AttemptNumber INTEGER CHECK(AttemptNumber IS NULL OR AttemptNumber>0),
            RuntimeEpoch TEXT CHECK(RuntimeEpoch IS NULL OR length(RuntimeEpoch)=36),
            RecordedAtUtc TEXT NOT NULL CHECK(length(RecordedAtUtc)=33),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode) BETWEEN 1 AND 256),
            FailureCategory TEXT CHECK(FailureCategory IS NULL OR
                FailureCategory IN ('Transient','UnknownOutcome','Permanent')),
            RetryAfterUtc TEXT,
            ConnectionBindingHash TEXT CHECK(ConnectionBindingHash IS NULL OR
                length(ConnectionBindingHash)=64),
            AttemptBudget INTEGER CHECK(AttemptBudget IS NULL OR AttemptBudget>0),
            PayloadHash TEXT CHECK(PayloadHash IS NULL OR length(PayloadHash)=64),
            ReceiptId TEXT CHECK(ReceiptId IS NULL OR length(ReceiptId) BETWEEN 1 AND 256),
            ReceiptHash TEXT CHECK(ReceiptHash IS NULL OR length(ReceiptHash)=64),
            AcceptedAtUtc TEXT,
            ReceiptBase64 TEXT,
            ReceiptLength INTEGER CHECK(ReceiptLength IS NULL OR
                (ReceiptLength>0 AND ReceiptLength<=16384)),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            CHECK((Kind='Created')=(AttemptId IS NULL)),
            CHECK((Kind='Created')=(AttemptNumber IS NULL)),
            CHECK((Kind='Created')=(RuntimeEpoch IS NULL)),
            CHECK((Kind='Succeeded')=(ReceiptId IS NOT NULL)),
            CHECK((Kind='AttemptFailed')=(FailureCategory IS NOT NULL)),
            CHECK((Kind<>'AttemptFailed') OR RetryAfterUtc IS NOT NULL OR
                FailureCategory IN ('UnknownOutcome','Permanent')),
            CHECK(RetryAfterUtc IS NULL OR FailureCategory IN ('Transient','UnknownOutcome')),
            CHECK(FailureCategory<>'Transient' OR RetryAfterUtc IS NOT NULL),
            CHECK(FailureCategory<>'Permanent' OR RetryAfterUtc IS NULL),
            CHECK((ReceiptId IS NULL)=(ReceiptHash IS NULL)),
            CHECK((ReceiptId IS NULL)=(AcceptedAtUtc IS NULL)),
            CHECK((ReceiptId IS NULL)=(ReceiptBase64 IS NULL)),
            CHECK((ReceiptId IS NULL)=(ReceiptLength IS NULL)),
            UNIQUE(DeliveryId,AggregateSequence));
        CREATE UNIQUE INDEX ix_production_outbox_one_success
            ON production_outbox_events(DeliveryId) WHERE Kind='Succeeded';
        CREATE UNIQUE INDEX ix_production_outbox_one_created
            ON production_outbox_events(DeliveryId) WHERE Kind='Created';
        CREATE TABLE production_outbox_work(
            DeliveryId TEXT NOT NULL PRIMARY KEY CHECK(length(DeliveryId)=36)
                REFERENCES production_outbox_deliveries(DeliveryId),
            State TEXT NOT NULL CHECK(State IN ('Pending','Failed','Succeeded')),
            AttemptCount INTEGER NOT NULL CHECK(AttemptCount>=0),
            NextAttemptNumber INTEGER NOT NULL CHECK(NextAttemptNumber>0),
            RetryEligible INTEGER NOT NULL CHECK(RetryEligible IN (0,1)),
            PermanentBlock INTEGER NOT NULL CHECK(PermanentBlock IN (0,1)),
            ActiveAttemptId TEXT CHECK(ActiveAttemptId IS NULL OR length(ActiveAttemptId)=36),
            ActiveRuntimeEpoch TEXT CHECK(ActiveRuntimeEpoch IS NULL OR length(ActiveRuntimeEpoch)=36),
            LastFailureReasonCode TEXT CHECK(LastFailureReasonCode IS NULL OR
                length(LastFailureReasonCode) BETWEEN 1 AND 256),
            LastFailureCategory TEXT CHECK(LastFailureCategory IS NULL OR
                LastFailureCategory IN ('Transient','UnknownOutcome','Permanent')),
            RetryAfterUtc TEXT,
            LastEventPosition INTEGER NOT NULL CHECK(LastEventPosition>0),
            LastEventContentHash TEXT NOT NULL CHECK(length(LastEventContentHash)=64),
            CHECK(AttemptCount<NextAttemptNumber),
            CHECK(PermanentBlock=0 OR RetryEligible=0),
            CHECK(State='Pending' OR ActiveAttemptId IS NULL),
            CHECK((ActiveAttemptId IS NULL)=(ActiveRuntimeEpoch IS NULL)),
            CHECK((LastFailureReasonCode IS NULL)=(LastFailureCategory IS NULL)),
            CHECK(RetryAfterUtc IS NULL OR LastFailureCategory IN ('Transient','UnknownOutcome')),
            CHECK(LastFailureCategory<>'Transient' OR RetryAfterUtc IS NOT NULL),
            CHECK(LastFailureCategory<>'Permanent' OR RetryAfterUtc IS NULL));
        CREATE TRIGGER production_outbox_config_immutable_update BEFORE UPDATE
            ON production_outbox_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxConfiguration');
        END;
        CREATE TRIGGER production_outbox_config_immutable_delete BEFORE DELETE
            ON production_outbox_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxConfiguration');
        END;
        CREATE TRIGGER production_outbox_delivery_immutable_update BEFORE UPDATE
            ON production_outbox_deliveries BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxDelivery');
        END;
        CREATE TRIGGER production_outbox_delivery_immutable_delete BEFORE DELETE
            ON production_outbox_deliveries BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxDelivery');
        END;
        CREATE TRIGGER production_outbox_delivery_insert BEFORE INSERT
            ON production_outbox_deliveries BEGIN
            SELECT CASE WHEN NEW.Position <>
                COALESCE((SELECT MAX(Position) FROM production_outbox_deliveries),0)+1
                THEN RAISE(ABORT,'ProductionOutboxDeliveryPositionGap') END;
        END;
        CREATE TRIGGER production_outbox_event_immutable_update BEFORE UPDATE
            ON production_outbox_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxEvent');
        END;
        CREATE TRIGGER production_outbox_event_immutable_delete BEFORE DELETE
            ON production_outbox_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionOutboxEvent');
        END;
        CREATE TRIGGER production_outbox_event_insert BEFORE INSERT
            ON production_outbox_events BEGIN
            SELECT CASE WHEN NEW.Position <>
                COALESCE((SELECT MAX(Position) FROM production_outbox_events),0)+1
                THEN RAISE(ABORT,'ProductionOutboxPositionGap') END;
            SELECT CASE WHEN NEW.AggregateSequence <>
                COALESCE((SELECT MAX(AggregateSequence) FROM production_outbox_events
                    WHERE DeliveryId=NEW.DeliveryId),0)+1
                THEN RAISE(ABORT,'ProductionOutboxAggregateGap') END;
            SELECT CASE WHEN NEW.Kind='AttemptStarted' AND NEW.AttemptNumber <>
                COALESCE((SELECT MAX(AttemptNumber) FROM production_outbox_events
                    WHERE DeliveryId=NEW.DeliveryId),0)+1
                THEN RAISE(ABORT,'ProductionOutboxAttemptSequence') END;
        END;";

    internal static void InitializeProductionOutboxTables(sqlite3 database,
        ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        SqliteNative.Execute(database, ProductionOutboxSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO production_outbox_store_config
            (Id,FormatVersion,RouteSetHash,RouteCount,MaximumAttempts,MaximumRetryDelayMilliseconds,
             AttemptTimeoutMilliseconds,MaximumEvents,MaximumPayloadBytes,MaximumTotalBytes,MaximumPageSize,
             RecipeLifecycleBindingHash,ImageEvidenceBindingHash,ImageFinalizationBindingHash,BindingHash)
            VALUES(1,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            ProductionOutboxNumber(ProductionOutboxStoreOptions.FormatVersion), options.RouteSetHash,
            ProductionOutboxNumber(options.RouteCount),
            ProductionOutboxNumber(options.MaximumAttempts),
            ProductionOutboxNumber((long)options.MaximumRetryDelay.TotalMilliseconds),
            ProductionOutboxNumber((long)options.AttemptTimeout.TotalMilliseconds),
            ProductionOutboxNumber(options.MaximumEvents),
            ProductionOutboxNumber(options.MaximumPayloadBytes),
            ProductionOutboxNumber(options.MaximumTotalBytes),
            ProductionOutboxNumber(options.MaximumPageSize),
            options.RecipeLifecyclePresenceHash, options.ImageEvidencePresenceHash,
            options.ImageFinalizationPresenceHash, options.BindingHash);
    }

    /// <summary>
    /// Creates the declared schema-36 tables, the immutable configuration row and the signed
    /// store-activation entry inside the caller's writer or migration transaction.
    /// </summary>
    internal static void InitializeProductionOutboxSchema(sqlite3 database,
        ProductionOutboxStoreOptions options, StoreDeadline deadline, AuditIntegrityPolicy policy,
        IAuditSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(key);
        InitializeProductionOutboxTables(database, options, deadline);
        AuditChainDatabase.AppendProductionOutboxActivation(database, policy, key, options, deadline);
    }

    /// <summary>
    /// Requires the single immutable configuration row to equal the caller's explicitly bounded
    /// routes, budgets and optional legacy-feature presence. A missing, foreign or edited row
    /// fails closed.
    /// </summary>
    internal static void RequireConfiguredProductionOutbox(sqlite3 database,
        ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,RouteSetHash,RouteCount,MaximumAttempts,
            MaximumRetryDelayMilliseconds,AttemptTimeoutMilliseconds,MaximumEvents,MaximumPayloadBytes,
            MaximumTotalBytes,MaximumPageSize,RecipeLifecycleBindingHash,ImageEvidenceBindingHash,
            ImageFinalizationBindingHash,BindingHash
            FROM production_outbox_store_config WHERE Id=1 LIMIT 2;", deadline, statement => new[]
        {
            SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            SqliteNative.ColumnText(statement, 1) ?? string.Empty,
            SqliteNative.ColumnText(statement, 2) ?? string.Empty,
            SqliteNative.ColumnText(statement, 3) ?? string.Empty,
            SqliteNative.ColumnText(statement, 4) ?? string.Empty,
            SqliteNative.ColumnText(statement, 5) ?? string.Empty,
            SqliteNative.ColumnText(statement, 6) ?? string.Empty,
            SqliteNative.ColumnText(statement, 7) ?? string.Empty,
            SqliteNative.ColumnText(statement, 8) ?? string.Empty,
            SqliteNative.ColumnText(statement, 9) ?? string.Empty,
            SqliteNative.ColumnText(statement, 10),
            SqliteNative.ColumnText(statement, 11),
            SqliteNative.ColumnText(statement, 12),
            SqliteNative.ColumnText(statement, 13) ?? string.Empty
        });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
        {
            ProductionOutboxNumber(ProductionOutboxStoreOptions.FormatVersion), options.RouteSetHash,
            ProductionOutboxNumber(options.RouteCount),
            ProductionOutboxNumber(options.MaximumAttempts),
            ProductionOutboxNumber((long)options.MaximumRetryDelay.TotalMilliseconds),
            ProductionOutboxNumber((long)options.AttemptTimeout.TotalMilliseconds),
            ProductionOutboxNumber(options.MaximumEvents),
            ProductionOutboxNumber(options.MaximumPayloadBytes),
            ProductionOutboxNumber(options.MaximumTotalBytes),
            ProductionOutboxNumber(options.MaximumPageSize),
            options.RecipeLifecyclePresenceHash, options.ImageEvidencePresenceHash,
            options.ImageFinalizationPresenceHash, options.BindingHash
        }, StringComparer.Ordinal), "ProductionOutboxConfigurationMismatch");
    }

    /// <summary>
    /// Full central-audit proof for the schema-36 outbox before any fact is projected: the
    /// signed chain is verified from genesis through the tail with every configured ledger
    /// supplied, so the outbox membership, the exact activation binding and the immutable
    /// configuration row are all re-proved.
    /// </summary>
    internal static void VerifyProductionOutboxReadGuard(sqlite3 database, ProductionStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        var outbox = options.Outbox ?? throw new InvalidOperationException(
            "ProductionOutboxConfigurationRequired");
        outbox.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException(
            "AuditPolicyNotConfigured");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) ==
            ProductionOutboxStoreOptions.SchemaVersion, "ProductionOutboxGovernedMigrationRequired");
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
            validateAnchorReceipt: false, archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts, cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery, cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup, calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance, releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts, activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions, importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections, productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications,
            recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies,
            qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
            productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            recipeSelectionOptions: options.RecipeSelections, productionArmOptions: options.ProductionArming,
            recipeLifecycleOptions: outbox.RecipeLifecycle, imageEvidenceOptions: outbox.ImageEvidence,
            imageFinalizationOptions: outbox.ImageFinalization, productionOutboxOptions: outbox);
        AuditChainDatabase.RequireFullProductionOutboxVerification(database, report, deadline, outbox);
    }

    /// <summary>Reads and re-proves every immutable outbox event row.</summary>
    internal static IReadOnlyList<ProductionOutboxStoredEvent> ReadProductionOutboxRows(sqlite3 database,
        ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredProductionOutbox(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,EventId,DeliveryId,InspectionId,CoreHash,RouteSetHash,RouteId,RouteVersion,
                RouteContentHash,AggregateSequence,Kind,AttemptId,AttemptNumber,RuntimeEpoch,RecordedAtUtc,
                ReasonCode,FailureCategory,RetryAfterUtc,ConnectionBindingHash,AttemptBudget,PayloadHash,
                ReceiptId,ReceiptHash,AcceptedAtUtc,ReceiptBase64,ReceiptLength,ContentHash,AuditSequence,AuditHash
            FROM production_outbox_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadProductionOutboxEvent(statement),
            checked(options.MaximumEvents + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEvents,
            "ProductionOutboxEntryCapacityExceeded");
        return rows;
    }

    /// <summary>Reads and re-proves every immutable outbox delivery row.</summary>
    internal static IReadOnlyList<ProductionOutboxStoredDelivery> ReadProductionOutboxDeliveries(
        sqlite3 database, ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredProductionOutbox(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,DeliveryId,InspectionId,CoreHash,RouteSetHash,RouteId,RouteVersion,
                RouteContentHash,Criticality,DestinationIdentity,PayloadContractId,PayloadContractVersion,
                PayloadContractContentHash,ContentType,ReceiverContractId,ReceiverContractVersion,
                ReceiverContractContentHash,ReceiverPublicKeyBase64,AdapterContractId,AdapterContractVersion,
                AdapterContractContentHash,RouteMaximumPayloadBytes,PayloadBase64,PayloadContentHash,
                PayloadByteLength,PreparationFailure,CreatedAtUtc,MaximumAttempts,ContentHash,CreatedEventId,
                CreatedAuditSequence,CreatedAuditHash
            FROM production_outbox_deliveries ORDER BY Position LIMIT ?;", deadline,
            statement => ReadProductionOutboxDelivery(statement),
            checked(options.MaximumEvents + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEvents,
            "ProductionOutboxEntryCapacityExceeded");
        return rows;
    }

    /// <summary>
    /// The re-derived projection of every obligated delivery: the persisted work row is only
    /// used for its existence, because the state is always the exact replay of the immutable
    /// facts.
    /// </summary>
    internal static IReadOnlyList<ProductionOutboxWorkState> ReadProductionOutboxObligationStates(
        sqlite3 database, ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        var deliveries = ReadProductionOutboxDeliveries(database, options, deadline);
        var events = ReadProductionOutboxRows(database, options, deadline);
        var persisted = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_work;", deadline);
        AuditChainDatabase.Require(persisted == deliveries.Count,
            "ProductionOutboxWorkProjectionMismatch");
        var groups = events.GroupBy(row => row.Event.DeliveryId)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<ProductionOutboxEvent>)group.OrderBy(row => row.Position)
                    .Select(row => row.Event).ToArray());
        var states = new List<ProductionOutboxWorkState>(deliveries.Count);
        foreach (var delivery in deliveries)
        {
            if (!groups.TryGetValue(delivery.Delivery.DeliveryId, out var list) || list.Count == 0)
                throw new InvalidOperationException("ProductionOutboxCreatedEventMissing");
            states.Add(DeriveProductionOutboxState(delivery.Delivery, list));
        }
        return states;
    }

    private static ProductionOutboxStoredEvent ReadProductionOutboxEvent(sqlite3_stmt statement)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var eventId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 1));
        var deliveryId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 2));
        var inspectionId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 3));
        var coreHash = SqliteNative.ColumnText(statement, 4);
        var routeSetHash = SqliteNative.ColumnText(statement, 5);
        var routeId = SqliteNative.ColumnText(statement, 6);
        var routeVersion = SqliteNative.ColumnText(statement, 7);
        var routeContentHash = SqliteNative.ColumnText(statement, 8);
        var aggregate = SqliteNative.ColumnInt64(statement, 9);
        var kind = ParseProductionOutboxKind(SqliteNative.ColumnText(statement, 10));
        var attemptText = SqliteNative.ColumnText(statement, 11);
        var attemptNumberColumn = SqliteNative.ColumnInt64Nullable(statement, 12);
        var runtimeEpochText = SqliteNative.ColumnText(statement, 13);
        var recordedAt = ParseProductionOutboxTime(SqliteNative.ColumnText(statement, 14));
        var reason = SqliteNative.ColumnText(statement, 15) ?? string.Empty;
        var failureText = SqliteNative.ColumnText(statement, 16);
        var retryAfterText = SqliteNative.ColumnText(statement, 17);
        var connectionHash = SqliteNative.ColumnText(statement, 18);
        var attemptBudget = SqliteNative.ColumnInt64Nullable(statement, 19);
        var payloadHash = SqliteNative.ColumnText(statement, 20);
        var receiptId = SqliteNative.ColumnText(statement, 21);
        var receiptHash = SqliteNative.ColumnText(statement, 22);
        var acceptedText = SqliteNative.ColumnText(statement, 23);
        var receiptBase64 = SqliteNative.ColumnText(statement, 24);
        var receiptLength = SqliteNative.ColumnInt64Nullable(statement, 25);
        var contentHash = SqliteNative.ColumnText(statement, 26) ?? string.Empty;
        var auditSequence = SqliteNative.ColumnInt64(statement, 27);
        var auditHash = SqliteNative.ColumnText(statement, 28) ?? string.Empty;
        Guid? attemptId = attemptText is null ? null : ParseProductionOutboxGuid(attemptText);
        Guid? runtimeEpoch = runtimeEpochText is null ? null : ParseProductionOutboxGuid(runtimeEpochText);
        int? attemptNumber = attemptNumberColumn is null ? null : checked((int)attemptNumberColumn.Value);
        OutboxFailureCategory? failure = failureText is null ? null : ParseProductionOutboxCategory(failureText);
        DateTimeOffset? retryAfter = retryAfterText is null ? null :
            ParseProductionOutboxTime(retryAfterText);
        DateTimeOffset? acceptedAt = acceptedText is null ? null : ParseProductionOutboxTime(acceptedText);
        byte[]? receipt = null;
        if (receiptBase64 is not null)
        {
            try { receipt = Convert.FromBase64String(receiptBase64); }
            catch (FormatException exception)
            { throw new InvalidOperationException("ProductionOutboxPayloadInvalid", exception); }
        }
        AuditChainDatabase.Require(attemptBudget is null or (> 0 and <= 32) &&
            AuditCanonical.IsHash(coreHash) && AuditCanonical.IsHash(routeSetHash) &&
            AuditCanonical.IsHash(routeContentHash) && AuditCanonical.IsHash(contentHash) &&
            AuditCanonical.IsHash(auditHash), "ProductionOutboxEventInvalid");
        string? receiptContentHash = null;
        if (receipt is not null)
        {
            AuditChainDatabase.Require(receipt.Length is > 0 and <= ProductionOutboxStoreOptions.MaximumReceiptBytes &&
                receiptLength == receipt.LongLength, "ProductionOutboxReceiptInvalid");
            receiptContentHash = ProductionOutboxStorageCodec.Hash(receipt);
        }
        else
        {
            AuditChainDatabase.Require(receiptLength is null && receiptHash is null && receiptId is null &&
                acceptedAt is null, "ProductionOutboxReceiptInvalid");
        }
        var bindings = new ProductionOutboxEventBindings(position, eventId, deliveryId, inspectionId,
            coreHash!, routeSetHash!, routeId ?? string.Empty, routeVersion ?? string.Empty,
            routeContentHash!, aggregate, kind, attemptId, attemptNumber, runtimeEpoch, recordedAt, reason,
            failure, retryAfter, connectionHash, attemptBudget is null ? null : checked((int)attemptBudget.Value),
            payloadHash, receiptId, receiptHash, acceptedAt, receiptContentHash, contentHash);
        AuditChainDatabase.Require(ProductionOutboxStorageCodec.EventContentHash(bindings) == contentHash,
            "ProductionOutboxEventBindingMismatch");
        var receiptSpan = receipt is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(receipt);
        var value = new ProductionOutboxEvent(position, eventId, deliveryId, inspectionId, coreHash!,
            routeSetHash!, routeId ?? string.Empty, routeVersion ?? string.Empty, routeContentHash!, aggregate,
            kind, attemptId, attemptNumber, runtimeEpoch, recordedAt, reason, failure, retryAfter,
            connectionHash, attemptBudget is null ? null : checked((int)attemptBudget.Value), receiptId,
            receiptHash, acceptedAt, receiptSpan, payloadHash, contentHash,
            auditSequence, auditHash);
        return new ProductionOutboxStoredEvent(position, value, aggregate, kind, attemptId, attemptNumber);
    }

    private static ProductionOutboxStoredDelivery ReadProductionOutboxDelivery(sqlite3_stmt statement)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var deliveryId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 1));
        var inspectionId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 2));
        var coreHash = SqliteNative.ColumnText(statement, 3) ?? string.Empty;
        var routeSetHash = SqliteNative.ColumnText(statement, 4) ?? string.Empty;
        var routeId = SqliteNative.ColumnText(statement, 5) ?? string.Empty;
        var routeVersion = SqliteNative.ColumnText(statement, 6) ?? string.Empty;
        var routeContentHash = SqliteNative.ColumnText(statement, 7) ?? string.Empty;
        var criticality = checked((int)SqliteNative.ColumnInt64(statement, 8));
        var destinationIdentity = SqliteNative.ColumnText(statement, 9) ?? string.Empty;
        var payloadContractId = SqliteNative.ColumnText(statement, 10) ?? string.Empty;
        var payloadContractVersion = SqliteNative.ColumnText(statement, 11) ?? string.Empty;
        var payloadContractContentHash = SqliteNative.ColumnText(statement, 12) ?? string.Empty;
        var contentType = SqliteNative.ColumnText(statement, 13) ?? string.Empty;
        var receiverContractId = SqliteNative.ColumnText(statement, 14) ?? string.Empty;
        var receiverContractVersion = SqliteNative.ColumnText(statement, 15) ?? string.Empty;
        var receiverContractContentHash = SqliteNative.ColumnText(statement, 16) ?? string.Empty;
        var receiverPublicKeyBase64 = SqliteNative.ColumnText(statement, 17) ?? string.Empty;
        var adapterContractId = SqliteNative.ColumnText(statement, 18) ?? string.Empty;
        var adapterContractVersion = SqliteNative.ColumnText(statement, 19) ?? string.Empty;
        var adapterContractContentHash = SqliteNative.ColumnText(statement, 20) ?? string.Empty;
        var routeMaximumPayloadBytes = checked((int)SqliteNative.ColumnInt64(statement, 21));
        var payloadBase64 = SqliteNative.ColumnText(statement, 22);
        var payloadContentHash = SqliteNative.ColumnText(statement, 23);
        var payloadByteLength = SqliteNative.ColumnInt64Nullable(statement, 24);
        var preparationFailure = SqliteNative.ColumnText(statement, 25);
        var createdAt = ParseProductionOutboxTime(SqliteNative.ColumnText(statement, 26));
        var maximumAttempts = checked((int)SqliteNative.ColumnInt64(statement, 27));
        var contentHash = SqliteNative.ColumnText(statement, 28) ?? string.Empty;
        var createdEventId = ParseProductionOutboxGuid(SqliteNative.ColumnText(statement, 29));
        var createdAuditSequence = SqliteNative.ColumnInt64(statement, 30);
        var createdAuditHash = SqliteNative.ColumnText(statement, 31) ?? string.Empty;
        byte[]? payload = null;
        if (payloadBase64 is not null)
        {
            try { payload = Convert.FromBase64String(payloadBase64); }
            catch (FormatException exception)
            { throw new InvalidOperationException("ProductionOutboxPayloadInvalid", exception); }
        }
        AuditChainDatabase.Require(AuditCanonical.IsHash(coreHash) && AuditCanonical.IsHash(routeSetHash) &&
            AuditCanonical.IsHash(contentHash) && AuditCanonical.IsHash(createdAuditHash) &&
            createdAuditSequence > 0, "ProductionOutboxDeliveryRowInvalid");
        OutboxDelivery delivery;
        try
        {
            delivery = ProductionOutboxStorageCodec.ReconstructDelivery(deliveryId, inspectionId, coreHash,
                position, routeId, routeVersion, routeContentHash, criticality, destinationIdentity,
                payloadContractId, payloadContractVersion, payloadContractContentHash, contentType,
                receiverContractId, receiverContractVersion, receiverContractContentHash,
                receiverPublicKeyBase64, adapterContractId, adapterContractVersion,
                adapterContractContentHash, routeMaximumPayloadBytes, payload, payloadContentHash,
                payloadByteLength, preparationFailure, createdAt, maximumAttempts, contentHash);
        }
        catch (ArgumentException exception)
        { throw new InvalidOperationException("ProductionOutboxDeliveryRowInvalid", exception); }
        return new ProductionOutboxStoredDelivery(position, delivery, contentHash, createdEventId,
            createdAuditSequence, createdAuditHash);
    }

    /// <summary>
    /// Re-proves the complete schema-36 local ledger before it is projected: the immutable
    /// shape and capacity, every relationship to the frozen production Core, the contiguous
    /// per-delivery sequences, the attempt and one-success rules, and the exact replay of the
    /// current per-delivery projection.
    /// </summary>
    internal static void ValidateProductionOutboxHistory(sqlite3 database,
        ProductionOutboxStoreOptions options, ProductionInspectionStoreOptions productionOptions,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(productionOptions);
        RequireConfiguredProductionInspection(database, productionOptions, deadline);
        var deliveries = ReadProductionOutboxDeliveries(database, options, deadline);
        var rows = ReadProductionOutboxRows(database, options, deadline);
        var byId = new Dictionary<Guid, ProductionOutboxStoredDelivery>();
        foreach (var delivery in deliveries)
        {
            AuditChainDatabase.Require(byId.TryAdd(delivery.Delivery.DeliveryId, delivery),
                "ProductionOutboxDeliveryDuplicate");
            AuditChainDatabase.Require(delivery.Delivery.CoreHash.Length == 64,
                "ProductionOutboxDeliveryRowInvalid");
            var cores = AuditChainDatabase.Read(database,
                "SELECT COUNT(*) FROM production_inspection_cores WHERE InspectionId=? AND ContentHash=? LIMIT 2;",
                deadline, statement => SqliteNative.ColumnInt64(statement, 0),
                delivery.Delivery.InspectionId.ToString("D"), delivery.Delivery.CoreHash);
            AuditChainDatabase.Require(cores.Count == 1 && cores[0] == 1,
                "ProductionOutboxSourceCoreMissing");
            var routeSets = AuditChainDatabase.Read(database,
                "SELECT COUNT(*) FROM production_outbox_deliveries WHERE DeliveryId=? AND RouteSetHash=? LIMIT 2;",
                deadline, statement => SqliteNative.ColumnInt64(statement, 0),
                delivery.Delivery.DeliveryId.ToString("D"), options.RouteSetHash);
            AuditChainDatabase.Require(routeSets.Count == 1 && routeSets[0] == 1,
                "ProductionOutboxRouteSetBindingMismatch");
        }
        long previousPosition = 0;
        var aggregates = new Dictionary<Guid, long>();
        var events = new Dictionary<Guid, List<ProductionOutboxEvent>>();
        foreach (var row in rows)
        {
            var value = row.Event;
            AuditChainDatabase.Require(value.Position == previousPosition + 1,
                "ProductionOutboxPositionGap");
            previousPosition = value.Position;
            AuditChainDatabase.Require(byId.TryGetValue(value.DeliveryId, out var delivery) &&
                value.RouteSetHash == options.RouteSetHash &&
                delivery!.Delivery.InspectionId == value.InspectionId &&
                delivery.Delivery.CoreHash == value.CoreHash &&
                delivery.Delivery.Route.RouteId == value.RouteId &&
                delivery.Delivery.Route.Version == value.RouteVersion &&
                delivery.Delivery.Route.ContentHash == value.RouteContentHash,
                "ProductionOutboxDeliveryBindingMismatch");
            aggregates.TryGetValue(value.DeliveryId, out var aggregate);
            AuditChainDatabase.Require(value.AggregateSequence == aggregate + 1,
                "ProductionOutboxAggregateGap");
            aggregates[value.DeliveryId] = value.AggregateSequence;
            if (value.Kind == OutboxEventKind.Created)
            {
                AuditChainDatabase.Require(value.AggregateSequence == 1 &&
                    value.PayloadHash == delivery!.Delivery.Payload?.ContentHash &&
                    value.EventId == delivery.CreatedEventId &&
                    value.AuditSequence == delivery.CreatedAuditSequence &&
                    value.AuditHash == delivery.CreatedAuditHash,
                    "ProductionOutboxCreatedBindingMismatch");
            }
            else
            {
                AuditChainDatabase.Require(delivery!.Delivery.Payload is not null &&
                    value.PayloadHash == delivery.Delivery.Payload.ContentHash &&
                    value.AttemptBudget == delivery.Delivery.MaximumAttempts,
                    "ProductionOutboxDeliveryBindingMismatch");
            }
            if (!events.TryGetValue(value.DeliveryId, out var list))
            {
                list = new List<ProductionOutboxEvent>();
                events[value.DeliveryId] = list;
            }
            list.Add(value);
        }
        AuditChainDatabase.Require(events.Count == deliveries.Count, "ProductionOutboxCreatedEventMissing");
        var created = new HashSet<Guid>();
        var succeeded = new HashSet<Guid>();
        var activeAttempts = new Dictionary<Guid, Guid>();
        var attemptNumbers = new Dictionary<Guid, int>();
        foreach (var delivery in deliveries)
        {
            var deliveryId = delivery.Delivery.DeliveryId;
            if (!events.TryGetValue(deliveryId, out var list))
                throw new InvalidOperationException("ProductionOutboxCreatedEventMissing");
            foreach (var value in list!)
            {
                switch (value.Kind)
                {
                    case OutboxEventKind.Created:
                        AuditChainDatabase.Require(created.Add(deliveryId) && !succeeded.Contains(deliveryId),
                            "ProductionOutboxCreatedBindingMismatch");
                        break;
                    case OutboxEventKind.AttemptStarted:
                        attemptNumbers.TryGetValue(deliveryId, out var previousAttempt);
                        AuditChainDatabase.Require(created.Contains(deliveryId) &&
                            value.AttemptNumber == previousAttempt + 1 &&
                            !succeeded.Contains(deliveryId) && !activeAttempts.ContainsKey(deliveryId) &&
                            value.AttemptId is not null && value.RuntimeEpoch is not null &&
                            value.ConnectionBindingHash is not null &&
                            value.AttemptNumber <= delivery.Delivery.MaximumAttempts,
                            "ProductionOutboxAttemptBindingMismatch");
                        attemptNumbers[deliveryId] = value.AttemptNumber!.Value;
                        activeAttempts[deliveryId] = value.AttemptId!.Value;
                        break;
                    case OutboxEventKind.AttemptFailed:
                        var recovered = !activeAttempts.TryGetValue(deliveryId, out var active) ||
                            active != value.AttemptId;
                        AuditChainDatabase.Require(!recovered || previousFailureMatches(list!, value),
                            "ProductionOutboxAttemptBindingMismatch");
                        activeAttempts.Remove(deliveryId);
                        AuditChainDatabase.Require(created.Contains(deliveryId) && !succeeded.Contains(deliveryId),
                            "ProductionOutboxAttemptBindingMismatch");
                        break;
                    case OutboxEventKind.Succeeded:
                        var activeSuccess = activeAttempts.TryGetValue(deliveryId, out var currentActive) &&
                            currentActive == value.AttemptId;
                        var recoveredSuccess = !activeSuccess && !activeAttempts.ContainsKey(deliveryId) &&
                            previousFailureMatches(list!, value);
                        AuditChainDatabase.Require(created.Contains(deliveryId) && !succeeded.Contains(deliveryId) &&
                            (activeSuccess || recoveredSuccess) && value.AttemptId is not null &&
                            attemptNumbers.TryGetValue(deliveryId, out var lastNumber) &&
                            lastNumber == value.AttemptNumber && value.AttemptBudget == delivery.Delivery.MaximumAttempts,
                            "ProductionOutboxAttemptBindingMismatch");
                        activeAttempts.Remove(deliveryId);
                        succeeded.Add(deliveryId);
                        break;
                }
            }
        }
        ValidateProductionOutboxProjection(database, deliveries, events, deadline);
        var reserved = ReadProductionOutboxReserveRows(database, deadline);
        // A durable Admitted cycle without a Core has not created its batch yet, so its complete
        // future liability must already fit alongside every persisted obligation.
        var preCore = ReadProductionOutboxPreCoreReserve(database, options, deadline);
        AuditChainDatabase.Require(rows.Count + deliveries.Count + reserved.Events + preCore.Rows <=
            options.MaximumEvents, "ProductionOutboxEntryCapacityExceeded");
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        var storedPayloadBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(PayloadBase64)),0) FROM production_outbox_deliveries;", deadline);
        AuditChainDatabase.Require(checked(usedBytes + storedPayloadBytes + reserved.Events *
            ProductionOutboxStoreOptions.MaximumStoredReceiptBytes + preCore.PayloadBytes) <=
            options.MaximumTotalBytes, "ProductionOutboxTotalCapacityExceeded");
    }

    private static bool previousFailureMatches(IReadOnlyList<ProductionOutboxEvent> events,
        ProductionOutboxEvent success)
    {
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Kind == OutboxEventKind.AttemptStarted)
                return events[index].AttemptId == success.AttemptId &&
                    events[index].RuntimeEpoch == success.RuntimeEpoch;
            if (events[index].AttemptId == success.AttemptId &&
                events[index].Kind == OutboxEventKind.AttemptFailed)
                return true;
        }
        return false;
    }

    private sealed record ProductionOutboxDerivedState
    {
        internal OutboxDeliveryState State { get; set; } = OutboxDeliveryState.Pending;
        internal int AttemptCount { get; set; }
        internal int NextAttemptNumber { get; set; } = 1;
        internal bool RetryEligible { get; set; } = true;
        internal bool PermanentBlock { get; set; }
        internal Guid? ActiveAttemptId { get; set; }
        internal Guid? ActiveRuntimeEpoch { get; set; }
        internal (string ReasonCode, OutboxFailureCategory Category, DateTimeOffset? RetryAfterUtc)? LastFailure
        { get; set; }
    }

    /// <summary>
    /// The single derivation of the mutable projection from the immutable facts. Both the
    /// writer (before it upserts the row) and the full replay validation use it, so an edited
    /// projection row can never masquerade as persisted state.
    /// </summary>
    internal static ProductionOutboxWorkState DeriveProductionOutboxState(
        OutboxDelivery delivery, IReadOnlyList<ProductionOutboxEvent> events)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(events);
        var state = new ProductionOutboxDerivedState();
        // A preparation failure is an immutable unsendable obligation: it has no payload, can
        // never start an attempt and is permanently blocked from the moment it is created.
        if (delivery.Payload is null)
        {
            var created = events.Count == 0 ? null : events[0];
            state.State = OutboxDeliveryState.Failed;
            state.PermanentBlock = true;
            state.RetryEligible = false;
            state.LastFailure = (created?.ReasonCode ?? "ProductionOutboxPreparationFailed",
                OutboxFailureCategory.Permanent, null);
        }
        foreach (var value in events)
        {
            switch (value.Kind)
            {
                case OutboxEventKind.AttemptStarted:
                    state.State = OutboxDeliveryState.Pending;
                    state.AttemptCount = value.AttemptNumber!.Value;
                    state.NextAttemptNumber = checked(value.AttemptNumber.Value + 1);
                    state.ActiveAttemptId = value.AttemptId;
                    state.ActiveRuntimeEpoch = value.RuntimeEpoch;
                    break;
                case OutboxEventKind.AttemptFailed:
                    state.ActiveAttemptId = null;
                    state.ActiveRuntimeEpoch = null;
                    state.State = OutboxDeliveryState.Failed;
                    state.LastFailure = (value.ReasonCode, value.FailureCategory!.Value, value.RetryAfterUtc);
                    if (value.FailureCategory == OutboxFailureCategory.Permanent)
                    {
                        state.PermanentBlock = true;
                        state.RetryEligible = false;
                    }
                    else
                    {
                        state.RetryEligible = state.AttemptCount < delivery.MaximumAttempts;
                    }
                    break;
                case OutboxEventKind.Succeeded:
                    state.ActiveAttemptId = null;
                    state.ActiveRuntimeEpoch = null;
                    state.State = OutboxDeliveryState.Succeeded;
                    state.RetryEligible = false;
                    break;
            }
        }
        return new ProductionOutboxWorkState(delivery, state.State, state.AttemptCount,
            state.NextAttemptNumber, state.RetryEligible, state.PermanentBlock, state.ActiveAttemptId,
            state.ActiveRuntimeEpoch, state.LastFailure?.ReasonCode, state.LastFailure?.Category,
            state.LastFailure?.RetryAfterUtc, events.Count == 0 ? 0 : events[^1].Position,
            events.Count == 0 ? null : events[^1].ContentHash);
    }

    private static void ValidateProductionOutboxProjection(sqlite3 database,
        IReadOnlyList<ProductionOutboxStoredDelivery> deliveries,
        IReadOnlyDictionary<Guid, List<ProductionOutboxEvent>> events, StoreDeadline deadline)
    {
        var stored = AuditChainDatabase.Read(database, @"
            SELECT DeliveryId,State,AttemptCount,NextAttemptNumber,RetryEligible,PermanentBlock,
                ActiveAttemptId,ActiveRuntimeEpoch,LastFailureReasonCode,LastFailureCategory,RetryAfterUtc,
                LastEventPosition,LastEventContentHash
            FROM production_outbox_work ORDER BY DeliveryId LIMIT ?;", deadline,
            statement => (DeliveryId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                State: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                AttemptCount: SqliteNative.ColumnInt64(statement, 2),
                NextAttempt: SqliteNative.ColumnInt64(statement, 3),
                RetryEligible: SqliteNative.ColumnInt64(statement, 4),
                PermanentBlock: SqliteNative.ColumnInt64(statement, 5),
                Active: SqliteNative.ColumnText(statement, 6),
                ActiveEpoch: SqliteNative.ColumnText(statement, 7),
                Reason: SqliteNative.ColumnText(statement, 8),
                Category: SqliteNative.ColumnText(statement, 9),
                RetryAfter: SqliteNative.ColumnText(statement, 10),
                LastPosition: SqliteNative.ColumnInt64(statement, 11),
                LastHash: SqliteNative.ColumnText(statement, 12) ?? string.Empty),
            checked(deliveries.Count + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(stored.Count == deliveries.Count,
            "ProductionOutboxWorkProjectionMismatch");
        foreach (var row in stored)
        {
            if (!Guid.TryParse(row.DeliveryId, out var deliveryId))
                throw new InvalidOperationException("ProductionOutboxWorkProjectionMismatch");
            var delivery = deliveries.SingleOrDefault(value =>
                value.Delivery.DeliveryId == deliveryId) ??
                throw new InvalidOperationException("ProductionOutboxWorkProjectionMismatch");
            if (!events.TryGetValue(deliveryId, out var list) || list!.Count == 0)
                throw new InvalidOperationException("ProductionOutboxWorkProjectionMismatch");
            var state = DeriveProductionOutboxState(delivery.Delivery, list!);
            string? expectedRetryAfter = null;
            if ((state.LastFailureCategory is OutboxFailureCategory.Transient or
                    OutboxFailureCategory.UnknownOutcome) && state.RetryAfterUtc is not null)
                expectedRetryAfter = state.RetryAfterUtc!.Value.ToString("O", CultureInfo.InvariantCulture);
            AuditChainDatabase.Require(row.State == state.State.ToString() &&
                row.AttemptCount == state.AttemptCount &&
                row.NextAttempt == state.NextAttemptNumber &&
                row.RetryEligible == (state.RetryEligible ? 1 : 0) &&
                row.PermanentBlock == (state.PermanentBlock ? 1 : 0) &&
                row.Active == state.ActiveAttemptId?.ToString("D") &&
                row.ActiveEpoch == state.ActiveRuntimeEpoch?.ToString("D") &&
                row.Reason == state.LastFailureReasonCode &&
                row.Category == state.LastFailureCategory?.ToString() &&
                row.RetryAfter == expectedRetryAfter &&
                row.LastPosition == state.LastEventPosition &&
                row.LastHash == state.LastEventContentHash,
                "ProductionOutboxWorkProjectionMismatch");
        }
    }

    /// <summary>
    /// Outstanding outbox facts reserved for already-persisted obligations. Every sendable
    /// delivery without success or permanent failure keeps its remaining attempt budget.
    /// Terminal facts remain stored and charged, but release unused future attempts. This is only the
    /// persisted part: the uncreated liability of every durable Admitted cycle without a Core is
    /// derived separately from the same ledger.
    /// </summary>
    internal static (long Events, long OpenDeliveries) ReadProductionOutboxReserveRows(sqlite3 database,
        StoreDeadline deadline)
    {
        var events = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(MAX(0,2*MaximumAttempts-(SELECT COUNT(*) FROM production_outbox_events e
                WHERE e.DeliveryId=d.DeliveryId AND e.Kind<>'Created'))),0)
            FROM production_outbox_deliveries d
            WHERE d.PayloadBase64 IS NOT NULL AND NOT EXISTS(SELECT 1 FROM production_outbox_events s
                WHERE s.DeliveryId=d.DeliveryId AND (s.Kind='Succeeded' OR s.FailureCategory='Permanent'));", deadline);
        var open = AuditChainDatabase.Scalar(database, @"
            SELECT COUNT(*) FROM production_outbox_deliveries d
            WHERE NOT EXISTS(SELECT 1 FROM production_outbox_events s
                WHERE s.DeliveryId=d.DeliveryId AND s.Kind='Succeeded');", deadline);
        return (events, open);
    }

    /// <summary>
    /// The complete outbox fact reserve every shared writer must leave untouched: the persisted
    /// obligation reserve plus the uncreated-batch reserve of every durable Admitted cycle that
    /// owns no Core row yet.
    /// </summary>
    internal static long ReadProductionOutboxAuditReserve(sqlite3 database, StoreDeadline deadline) =>
        checked(ReadProductionOutboxReserveRows(database, deadline).Events +
            ReadProductionOutboxPreCoreAuditReserve(database, deadline));

    internal static void VerifyProductionOutboxActivationPayload(sqlite3 database, byte[] payload,
        ProductionOutboxStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "ProductionOutboxActivationPayloadMismatch");
        RequireConfiguredProductionOutbox(database, options, deadline);
    }

    /// <summary>
    /// Proves one metadata entry against its stored immutable fact in both directions: the
    /// audit kind must match the fact kind, the entry payload must equal the canonical binding
    /// of the referenced row and the row must reference the exact entry sequence and hash.
    /// </summary>
    internal static void VerifyProductionOutboxAuditPayload(sqlite3 database, long position,
        string kind, long sequence, string hash, byte[] payload, ProductionOutboxStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= ProductionOutboxStoreOptions.MaximumAuditPayloadBytes &&
            IsProductionOutboxAuditKind(kind), "ProductionOutboxAuditPayloadInvalid");
        var row = AuditChainDatabase.Read(database, @"
            SELECT Kind,AuditSequence,AuditHash,PayloadHash,ReceiptBase64 FROM production_outbox_events
            WHERE Position=? LIMIT 2;", deadline, statement =>
                (Kind: ParseProductionOutboxKind(SqliteNative.ColumnText(statement, 0)),
                    AuditSequence: SqliteNative.ColumnInt64(statement, 1),
                    AuditHash: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                    PayloadHash: SqliteNative.ColumnText(statement, 3),
                    ReceiptBase64: SqliteNative.ColumnText(statement, 4)),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row.AuditSequence == sequence && row.AuditHash == hash &&
            ProductionOutboxAuditKind(row.Kind) == kind, "ProductionOutboxAuditBindingMismatch");
        var rows = ReadProductionOutboxRows(database, options, deadline);
        var stored = rows.SingleOrDefault(value => value.Position == position) ??
            throw new InvalidOperationException("ProductionOutboxAuditRowMissing");
        AuditChainDatabase.Require(ProductionOutboxStorageCodec.EncodeAuditBinding(
                ProductionOutboxStorageCodec.Bindings(stored.Event))
            .AsSpan().SequenceEqual(payload), "ProductionOutboxCentralPayloadBindingMismatch");
    }

    /// <summary>
    /// Bounded event/byte capacity for the outbox itself while the writer still holds its
    /// transaction lock: every persisted row plus the complete remaining budget of every open
    /// obligation, plus the complete uncreated liability of every cycle that is still Admitted
    /// without a Core, plus the fact being admitted, must still fit.
    /// </summary>
    internal static void EnsureProductionOutboxEventCapacity(sqlite3 database,
        ProductionOutboxStoreOptions options, long futureReserve, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Require(futureReserve >= 0, "ProductionOutboxReserveCapacityExceeded");
        var preCore = ReadProductionOutboxPreCoreReserve(database, options, deadline);
        var events = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_events;", deadline);
        var deliveries = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_deliveries;", deadline);
        AuditChainDatabase.Require(checked(events + deliveries + 1 + futureReserve + preCore.Rows) <=
            options.MaximumEvents, "ProductionOutboxEntryCapacityExceeded");
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        var payloadBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(PayloadBase64)),0) FROM production_outbox_deliveries;", deadline);
        var maximumStored = ProductionOutboxStoreOptions.MaximumStoredReceiptBytes;
        AuditChainDatabase.Require(checked(usedBytes + payloadBytes + maximumStored +
            futureReserve * maximumStored + preCore.PayloadBytes) <= options.MaximumTotalBytes,
            "ProductionOutboxTotalCapacityExceeded");
        var largest = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        AuditChainDatabase.Require(largest <= ProductionInspectionStoredPayloadBytes(
            ProductionOutboxStoreOptions.MaximumReceiptBytes), "ProductionOutboxPayloadCapacityExceeded");
    }

    private static Guid ParseProductionOutboxGuid(string? value) => Guid.TryParse(value, out var parsed) &&
        parsed != Guid.Empty ? parsed : throw new InvalidOperationException("ProductionOutboxEventInvalid");

    private static DateTimeOffset ParseProductionOutboxTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed :
        throw new InvalidOperationException("ProductionOutboxEventInvalid");

    private static OutboxEventKind ParseProductionOutboxKind(string? value) => value switch
    {
        "Created" => OutboxEventKind.Created,
        "AttemptStarted" => OutboxEventKind.AttemptStarted,
        "AttemptFailed" => OutboxEventKind.AttemptFailed,
        "Succeeded" => OutboxEventKind.Succeeded,
        _ => throw new InvalidOperationException("ProductionOutboxEventInvalid")
    };

    private static OutboxFailureCategory ParseProductionOutboxCategory(string? value) => value switch
    {
        "Transient" => OutboxFailureCategory.Transient,
        "UnknownOutcome" => OutboxFailureCategory.UnknownOutcome,
        "Permanent" => OutboxFailureCategory.Permanent,
        _ => throw new InvalidOperationException("ProductionOutboxEventInvalid")
    };

    private static string ProductionOutboxNumber(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
