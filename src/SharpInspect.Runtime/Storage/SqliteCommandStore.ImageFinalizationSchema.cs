using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>One decoded immutable finalization fact row.</summary>
internal sealed record ImageFinalizationStoredRow(long Position,
    ProductionImageFinalizationEvent Event, byte[] Payload, string PayloadHash);

/// <summary>
/// Schema-35 production image finalization storage, independent from every earlier ledger and
/// bound to the qualified local final root. The ledger keeps the immutable finalization facts,
/// the current per-work projection and one bounded immutable configuration row; the image bytes
/// stay in the qualified final root and this store never opens a file.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ImageFinalizationActivationKind = "ImageFinalizationStoreActivated";

    internal static string ImageFinalizationAuditKind(ProductionImageFinalizationKind kind) => kind switch
    {
        ProductionImageFinalizationKind.AttemptStarted => "ImageFinalizationAttemptStarted",
        ProductionImageFinalizationKind.AttemptFailed => "ImageFinalizationAttemptFailed",
        ProductionImageFinalizationKind.Succeeded => "ImageFinalizationSucceeded",
        ProductionImageFinalizationKind.StageReleased => "ImageFinalizationStageReleased",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static bool IsImageFinalizationAuditKind(string kind) => kind is
        "ImageFinalizationAttemptStarted" or "ImageFinalizationAttemptFailed" or
        "ImageFinalizationSucceeded" or "ImageFinalizationStageReleased";

    internal const string ImageFinalizationSchemaSql = @"
        CREATE TABLE image_finalization_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumAttempts INTEGER NOT NULL CHECK(MaximumAttempts>0),
            MaximumRetryDelayMilliseconds INTEGER NOT NULL CHECK(MaximumRetryDelayMilliseconds>0),
            MaximumEvents INTEGER NOT NULL CHECK(MaximumEvents>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            MaximumPageSize INTEGER NOT NULL CHECK(MaximumPageSize>0),
            WorkerCount INTEGER NOT NULL CHECK(WorkerCount=1),
            FinalRootBindingHash TEXT NOT NULL CHECK(length(FinalRootBindingHash)=64),
            ImageEvidenceBindingHash TEXT NOT NULL CHECK(length(ImageEvidenceBindingHash)=64),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE image_finalization_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            WorkId TEXT NOT NULL CHECK(length(WorkId)=36)
                REFERENCES pending_image_work(WorkId),
            ManifestId TEXT NOT NULL CHECK(length(ManifestId)=36),
            InspectionId TEXT NOT NULL CHECK(length(InspectionId)=36),
            WorkContentHash TEXT NOT NULL CHECK(length(WorkContentHash)=64),
            ManifestContentHash TEXT NOT NULL CHECK(length(ManifestContentHash)=64),
            AggregateSequence INTEGER NOT NULL CHECK(AggregateSequence>0),
            AttemptId TEXT CHECK(AttemptId IS NULL OR length(AttemptId)=36),
            AttemptNumber INTEGER CHECK(AttemptNumber IS NULL OR AttemptNumber>0),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            RecordedAtUtc TEXT NOT NULL CHECK(length(RecordedAtUtc)=33),
            PrincipalId TEXT NOT NULL CHECK(PrincipalId='SharpInspect.Runtime'),
            Kind TEXT NOT NULL CHECK(Kind IN
                ('AttemptStarted','AttemptFailed','Succeeded','StageReleased')),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode) BETWEEN 1 AND 256),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            CHECK((Kind='StageReleased')=(AttemptId IS NULL)),
            CHECK((Kind='StageReleased')=(AttemptNumber IS NULL)),
            UNIQUE(WorkId,AggregateSequence),
            UNIQUE(WorkId,AttemptId,Kind));
        CREATE UNIQUE INDEX ix_image_finalization_one_completion
            ON image_finalization_events(WorkId,Kind) WHERE Kind IN ('Succeeded','StageReleased');
        CREATE TABLE image_finalization_work(
            WorkId TEXT NOT NULL PRIMARY KEY CHECK(length(WorkId)=36)
                REFERENCES pending_image_work(WorkId),
            ManifestId TEXT NOT NULL CHECK(length(ManifestId)=36),
            InspectionId TEXT NOT NULL CHECK(length(InspectionId)=36),
            WorkContentHash TEXT NOT NULL CHECK(length(WorkContentHash)=64),
            ManifestContentHash TEXT NOT NULL CHECK(length(ManifestContentHash)=64),
            State TEXT NOT NULL CHECK(State IN ('Pending','Failed','Succeeded')),
            CleanupState TEXT NOT NULL CHECK(CleanupState IN ('Pending','Released')),
            AttemptCount INTEGER NOT NULL CHECK(AttemptCount>=0),
            NextAttemptNumber INTEGER NOT NULL CHECK(NextAttemptNumber>0),
            RetryEligible INTEGER NOT NULL CHECK(RetryEligible IN (0,1)),
            IntegrityConflict INTEGER NOT NULL CHECK(IntegrityConflict IN (0,1)),
            ActiveAttemptId TEXT CHECK(ActiveAttemptId IS NULL OR length(ActiveAttemptId)=36),
            LastFailureReasonCode TEXT CHECK(LastFailureReasonCode IS NULL OR
                (length(LastFailureReasonCode) BETWEEN 1 AND 256)),
            LastFailureCategory TEXT CHECK(LastFailureCategory IS NULL OR
                LastFailureCategory IN ('Temporary','Integrity')),
            RetryAfterUtc TEXT,
            SuccessFinalRootBindingHash TEXT CHECK(SuccessFinalRootBindingHash IS NULL OR
                length(SuccessFinalRootBindingHash)=64),
            SuccessFinalFileName TEXT,
            SuccessEncodedByteLength INTEGER CHECK(SuccessEncodedByteLength IS NULL OR SuccessEncodedByteLength>0),
            SuccessWidth INTEGER, SuccessHeight INTEGER, SuccessPixelFormat INTEGER, SuccessValidBits INTEGER,
            SuccessCanonicalHashScheme TEXT, SuccessCanonicalHashSchemeVersion INTEGER,
            SuccessCanonicalPixelHash TEXT, SuccessContentHash TEXT,
            LastEventPosition INTEGER NOT NULL CHECK(LastEventPosition>0),
            LastEventContentHash TEXT NOT NULL CHECK(length(LastEventContentHash)=64),
            CHECK(CleanupState='Pending' OR State='Succeeded'),
            CHECK(State<>'Succeeded' OR (SuccessFinalFileName IS NOT NULL AND
                SuccessEncodedByteLength IS NOT NULL AND SuccessContentHash IS NOT NULL)),
            CHECK(AttemptCount<NextAttemptNumber),
            CHECK(IntegrityConflict=0 OR RetryEligible=0),
            CHECK((LastFailureReasonCode IS NULL)=(LastFailureCategory IS NULL)),
            CHECK((LastFailureCategory='Temporary')=(RetryAfterUtc IS NOT NULL)));
        CREATE TRIGGER image_finalization_config_immutable_update BEFORE UPDATE
            ON image_finalization_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableImageFinalizationConfiguration');
        END;
        CREATE TRIGGER image_finalization_config_immutable_delete BEFORE DELETE
            ON image_finalization_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableImageFinalizationConfiguration');
        END;
        CREATE TRIGGER image_finalization_event_immutable_update BEFORE UPDATE
            ON image_finalization_events BEGIN
            SELECT RAISE(ABORT,'ImmutableImageFinalizationEvent');
        END;
        CREATE TRIGGER image_finalization_event_immutable_delete BEFORE DELETE
            ON image_finalization_events BEGIN
            SELECT RAISE(ABORT,'ImmutableImageFinalizationEvent');
        END;
        CREATE TRIGGER image_finalization_event_insert BEFORE INSERT
            ON image_finalization_events BEGIN
            SELECT CASE WHEN NEW.Position <>
                COALESCE((SELECT MAX(Position) FROM image_finalization_events),0)+1
                THEN RAISE(ABORT,'ImageFinalizationPositionGap') END;
            SELECT CASE WHEN NEW.AggregateSequence <>
                COALESCE((SELECT MAX(AggregateSequence) FROM image_finalization_events
                    WHERE WorkId=NEW.WorkId),0)+1
                THEN RAISE(ABORT,'ImageFinalizationAggregateGap') END;
            SELECT CASE WHEN NEW.Kind='AttemptStarted' AND NEW.AttemptNumber <>
                COALESCE((SELECT MAX(AttemptNumber) FROM image_finalization_events
                    WHERE WorkId=NEW.WorkId),0)+1
                THEN RAISE(ABORT,'ImageFinalizationAttemptSequence') END;
        END;";

    /// <summary>
    /// Creates the declared schema-35 tables and inserts the one immutable configuration row.
    /// </summary>
    internal static void InitializeImageFinalizationTables(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        SqliteNative.Execute(database, ImageFinalizationSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO image_finalization_store_config
            (Id,FormatVersion,MaximumAttempts,MaximumRetryDelayMilliseconds,MaximumEvents,
             MaximumPayloadBytes,MaximumTotalBytes,MaximumPageSize,WorkerCount,
             FinalRootBindingHash,ImageEvidenceBindingHash,BindingHash)
            VALUES(1,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            ImageFinalizationNumber(ProductionImageFinalizationStoreOptions.FormatVersion),
            ImageFinalizationNumber(options.MaximumAttempts),
            ImageFinalizationNumber((long)options.MaximumRetryDelay.TotalMilliseconds),
            ImageFinalizationNumber(options.MaximumEvents),
            ImageFinalizationNumber(options.MaximumPayloadBytes),
            ImageFinalizationNumber(options.MaximumTotalBytes),
            ImageFinalizationNumber(options.MaximumPageSize),
            ImageFinalizationNumber(ProductionImageFinalizationStoreOptions.FixedWorkerCount),
            options.FinalRootBindingHash, options.ImageEvidenceBindingHash, options.BindingHash);
    }

    /// <summary>
    /// Creates the declared schema-35 tables, the immutable configuration row and the signed
    /// store-activation entry inside the caller's writer or migration transaction.
    /// </summary>
    internal static void InitializeImageFinalizationSchema(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline, AuditIntegrityPolicy policy,
        IAuditSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(key);
        InitializeImageFinalizationTables(database, options, deadline);
        AuditChainDatabase.AppendImageFinalizationActivation(database, policy, key, options, deadline);
    }

    /// <summary>
    /// Requires the single immutable configuration row to equal the caller's explicitly bounded
    /// options, including the qualified final root binding. A missing, foreign or edited row
    /// fails closed.
    /// </summary>
    internal static void RequireConfiguredImageFinalization(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumAttempts,
            MaximumRetryDelayMilliseconds,MaximumEvents,MaximumPayloadBytes,MaximumTotalBytes,
            MaximumPageSize,WorkerCount,FinalRootBindingHash,ImageEvidenceBindingHash,BindingHash
            FROM image_finalization_store_config WHERE Id=1 LIMIT 2;", deadline, value => new[]
        {
            SqliteNative.ColumnText(value, 0) ?? string.Empty,
            SqliteNative.ColumnText(value, 1) ?? string.Empty,
            SqliteNative.ColumnText(value, 2) ?? string.Empty,
            SqliteNative.ColumnText(value, 3) ?? string.Empty,
            SqliteNative.ColumnText(value, 4) ?? string.Empty,
            SqliteNative.ColumnText(value, 5) ?? string.Empty,
            SqliteNative.ColumnText(value, 6) ?? string.Empty,
            SqliteNative.ColumnText(value, 7) ?? string.Empty,
            SqliteNative.ColumnText(value, 8) ?? string.Empty,
            SqliteNative.ColumnText(value, 9) ?? string.Empty,
            SqliteNative.ColumnText(value, 10) ?? string.Empty
        });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
        {
            ImageFinalizationNumber(ProductionImageFinalizationStoreOptions.FormatVersion),
            ImageFinalizationNumber(options.MaximumAttempts),
            ImageFinalizationNumber((long)options.MaximumRetryDelay.TotalMilliseconds),
            ImageFinalizationNumber(options.MaximumEvents),
            ImageFinalizationNumber(options.MaximumPayloadBytes),
            ImageFinalizationNumber(options.MaximumTotalBytes),
            ImageFinalizationNumber(options.MaximumPageSize),
            ImageFinalizationNumber(ProductionImageFinalizationStoreOptions.FixedWorkerCount),
            options.FinalRootBindingHash, options.ImageEvidenceBindingHash, options.BindingHash
        }), "ImageFinalizationConfigurationMismatch");
    }

    /// <summary>
    /// Full central-audit proof for the schema-35 finalization store before any fact is
    /// projected: the signed chain is verified from genesis through the tail with the option
    /// supplied, so the reverse metadata membership and the exact activation binding, the
    /// immutable configuration row and the table shape are all re-proved.
    /// </summary>
    internal static void VerifyImageFinalizationReadGuard(sqlite3 database, ProductionStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        var finalization = options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        finalization.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is
            ProductionImageFinalizationStoreOptions.SchemaVersion or ProductionOutboxStoreOptions.SchemaVersion,
            "ImageFinalizationGovernedMigrationRequired");
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
            stationQualificationOptions: options.StationQualifications, recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies,
            qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
            productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            recipeSelectionOptions: options.RecipeSelections, productionArmOptions: options.ProductionArming,
            recipeLifecycleOptions: options.RecipeLifecycle, imageEvidenceOptions: options.ImageEvidence,
            imageFinalizationOptions: finalization, productionOutboxOptions: options.Outbox);
        AuditChainDatabase.RequireFullImageFinalizationVerification(database, report, deadline, finalization);
    }

    /// <summary>Reads and re-proves every immutable finalization event row.</summary>
    internal static IReadOnlyList<ImageFinalizationStoredRow> ReadImageFinalizationRows(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredImageFinalization(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,EventId,WorkId,ManifestId,InspectionId,WorkContentHash,ManifestContentHash,
                AggregateSequence,AttemptId,AttemptNumber,RuntimeEpoch,RecordedAtUtc,PrincipalId,Kind,
                ReasonCode,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash
            FROM image_finalization_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadImageFinalizationRow(statement, options),
            checked(options.MaximumEvents + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEvents,
            "ImageFinalizationEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) =>
            checked(sum + ProductionInspectionStoredPayloadBytes(row.Payload.Length)));
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "ImageFinalizationTotalCapacityExceeded");
        return rows;
    }

    private static ImageFinalizationStoredRow ReadImageFinalizationRow(sqlite3_stmt statement,
        ProductionImageFinalizationStoreOptions options)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var eventId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 1));
        var workId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 2));
        var manifestId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 3));
        var inspectionId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 4));
        var workHash = SqliteNative.ColumnText(statement, 5);
        var manifestHash = SqliteNative.ColumnText(statement, 6);
        var aggregate = SqliteNative.ColumnInt64(statement, 7);
        var attemptText = SqliteNative.ColumnText(statement, 8);
        var attemptNumber = SqliteNative.ColumnInt64Nullable(statement, 9);
        var runtimeEpoch = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 10));
        var recordedAt = ParseImageFinalizationTime(SqliteNative.ColumnText(statement, 11));
        var principal = SqliteNative.ColumnText(statement, 12);
        var kind = ParseImageFinalizationKind(SqliteNative.ColumnText(statement, 13));
        var reason = SqliteNative.ColumnText(statement, 14) ?? string.Empty;
        var contentHash = SqliteNative.ColumnText(statement, 15);
        var payloadHash = SqliteNative.ColumnText(statement, 16);
        var encoded = SqliteNative.ColumnText(statement, 17) ?? string.Empty;
        var auditSequence = SqliteNative.ColumnInt64(statement, 18);
        var auditHash = SqliteNative.ColumnText(statement, 19);
        AuditChainDatabase.Require(principal == SystemPrincipalId.Runtime &&
            (attemptNumber is null or > 0) && AuditCanonical.IsHash(contentHash) &&
            AuditCanonical.IsHash(payloadHash) && AuditCanonical.IsHash(auditHash) &&
            encoded.Length <= checked(options.MaximumPayloadBytes * 2),
            "ImageFinalizationEventInvalid");
        byte[] payload;
        try { payload = Convert.FromBase64String(encoded); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ImageFinalizationPayloadInvalid", exception); }
        AuditChainDatabase.Require(payload.Length is > 0 &&
            payload.Length <= ProductionImageFinalizationStoreOptions.MaximumPayloadBytesHardLimit &&
            payload.Length <= options.MaximumPayloadBytes &&
            ProductionImageFinalizationStorageCodec.PayloadHash(payload) == payloadHash,
            "ImageFinalizationPayloadInvalid");
        var value = ProductionImageFinalizationStorageCodec.DecodeEvent(payload);
        Guid? attemptId = attemptText is null ? null : ParseImageFinalizationGuid(attemptText);
        AuditChainDatabase.Require(value.Position == position && value.EventId == eventId &&
            value.WorkId == workId && value.ManifestId == manifestId &&
            value.InspectionId == inspectionId && value.WorkContentHash == workHash &&
            value.ManifestContentHash == manifestHash && value.AggregateSequence == aggregate &&
            value.AttemptId == attemptId && value.AttemptNumber == (int?)attemptNumber &&
            value.RuntimeEpoch == runtimeEpoch && value.RecordedAtUtc == recordedAt &&
            value.Kind == kind && value.ReasonCode == reason && value.ContentHash == contentHash &&
            value.AuditSequence == auditSequence && value.AuditHash == auditHash,
            "ImageFinalizationEventBindingMismatch");
        return new ImageFinalizationStoredRow(position, value, payload, payloadHash!);
    }

    /// <summary>
    /// Re-proves the complete schema-35 local ledger before it is projected: the immutable
    /// shape and capacity, every relationship to the pending manifest and pending work facts
    /// of schema 34, the contiguous Work sequences, the attempt and completion rules, and the
    /// exact replay of the current per-work projection.
    /// </summary>
    internal static void ValidateImageFinalizationHistory(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, ProductionImageEvidenceStoreOptions evidenceOptions,
        ProductionInspectionStoreOptions productionOptions, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(evidenceOptions);
        ArgumentNullException.ThrowIfNull(productionOptions);
        RequireConfiguredProductionInspection(database, productionOptions, deadline);
        RequireConfiguredImageFinalization(database, options, deadline);
        var rows = ReadImageFinalizationRows(database, options, deadline);
        var released = new HashSet<Guid>();
        long previousPosition = 0;
        var aggregates = new Dictionary<Guid, long>();
        var attempts = new Dictionary<Guid, int>();
        var activeAttempts = new Dictionary<Guid, Guid>();
        var completed = new Dictionary<Guid, ProductionImageFinalizationKind>();
        var lastAttempt = new Dictionary<Guid, (Guid AttemptId, bool Failed)>();
        var integrityConflicts = new HashSet<Guid>();
        foreach (var row in rows)
        {
            var value = row.Event;
            AuditChainDatabase.Require(value.Position == previousPosition + 1,
                "ImageFinalizationPositionGap");
            previousPosition = value.Position;
            aggregates.TryGetValue(value.WorkId, out var aggregate);
            AuditChainDatabase.Require(value.AggregateSequence == aggregate + 1,
                "ImageFinalizationAggregateGap");
            aggregates[value.WorkId] = value.AggregateSequence;
            switch (value.Kind)
            {
                case ProductionImageFinalizationKind.AttemptStarted:
                    AuditChainDatabase.Require(!integrityConflicts.Contains(value.WorkId),
                        "ImageFinalizationIntegrityConflictRecorded");
                    attempts.TryGetValue(value.WorkId, out var previousAttempt);
                    AuditChainDatabase.Require(value.AttemptNumber == previousAttempt + 1,
                        "ImageFinalizationAttemptSequence");
                    AuditChainDatabase.Require(!activeAttempts.ContainsKey(value.WorkId) &&
                        !completed.ContainsKey(value.WorkId), "ImageFinalizationAttemptConflict");
                    attempts[value.WorkId] = value.AttemptNumber!.Value;
                    activeAttempts[value.WorkId] = value.AttemptId!.Value;
                    lastAttempt[value.WorkId] = (value.AttemptId!.Value, false);
                    break;
                case ProductionImageFinalizationKind.AttemptFailed:
                    AuditChainDatabase.Require(activeAttempts.TryGetValue(value.WorkId, out var active) &&
                        active == value.AttemptId, "ImageFinalizationAttemptBindingMismatch");
                    activeAttempts.Remove(value.WorkId);
                    lastAttempt[value.WorkId] = (value.AttemptId!.Value, true);
                    if (value.Failure!.Category == ProductionImageFailureCategory.Integrity)
                        integrityConflicts.Add(value.WorkId);
                    break;
                case ProductionImageFinalizationKind.Succeeded:
                    AuditChainDatabase.Require(!integrityConflicts.Contains(value.WorkId),
                        "ImageFinalizationIntegrityConflictRecorded");
                    // A success either finishes the active attempt or recovers a failed attempt
                    // that was the last one started for this work; the verified claim binds the
                    // exact attempt and the bound final file, never an arbitrary file.
                    var activeSuccess = activeAttempts.TryGetValue(value.WorkId, out var activeNow) &&
                        activeNow == value.AttemptId;
                    var recoveredSuccess = !activeSuccess &&
                        !activeAttempts.ContainsKey(value.WorkId) &&
                        lastAttempt.TryGetValue(value.WorkId, out var previousFailed) &&
                        previousFailed.AttemptId == value.AttemptId && previousFailed.Failed;
                    AuditChainDatabase.Require(!completed.ContainsKey(value.WorkId) &&
                        (activeSuccess || recoveredSuccess) &&
                        attempts.TryGetValue(value.WorkId, out var lastNumber) &&
                        lastNumber == value.AttemptNumber,
                        "ImageFinalizationAttemptBindingMismatch");
                    activeAttempts.Remove(value.WorkId);
                    completed[value.WorkId] = value.Kind;
                    break;
                case ProductionImageFinalizationKind.StageReleased:
                    AuditChainDatabase.Require(completed.TryGetValue(value.WorkId, out var completion) &&
                        completion == ProductionImageFinalizationKind.Succeeded &&
                        !released.Contains(value.WorkId), "ImageFinalizationReleaseSequence");
                    released.Add(value.WorkId);
                    break;
            }
            ValidateImageFinalizationSourceBinding(database, value, deadline);
        }
        ValidateImageFinalizationWorkProjection(database, rows, deadline);
        var reserved = ReadImageFinalizationReserveRows(database, deadline);
        AuditChainDatabase.Require(rows.Count + reserved.Events <= options.MaximumEvents,
            "ImageFinalizationEntryCapacityExceeded");
        var usedBytes = rows.Aggregate(0L, (sum, row) =>
            checked(sum + ProductionInspectionStoredPayloadBytes(row.Payload.Length)));
        AuditChainDatabase.Require(checked(usedBytes + reserved.Events *
            ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <= options.MaximumTotalBytes,
            "ImageFinalizationTotalCapacityExceeded");
        var largest = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(length(Payload)),0) FROM image_finalization_events;", deadline);
        AuditChainDatabase.Require(largest <= ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes),
            "ImageFinalizationPayloadCapacityExceeded");
    }

    /// <summary>
    /// Re-proves that the finalization facts describe exactly the frozen pending manifest and
    /// pending work of one Core obligation.
    /// </summary>
    private static void ValidateImageFinalizationSourceBinding(sqlite3 database,
        ProductionImageFinalizationEvent value, StoreDeadline deadline)
    {
        var matches = AuditChainDatabase.Read(database, @"
            SELECT w.ManifestId,w.InspectionId,w.ContentHash,m.ContentHash,m.InspectionId
            FROM pending_image_work w JOIN pending_image_manifests m ON m.ManifestId=w.ManifestId
            WHERE w.WorkId=? LIMIT 2;", deadline, statement => new[]
        {
            SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            SqliteNative.ColumnText(statement, 1) ?? string.Empty,
            SqliteNative.ColumnText(statement, 2) ?? string.Empty,
            SqliteNative.ColumnText(statement, 3) ?? string.Empty,
            SqliteNative.ColumnText(statement, 4) ?? string.Empty
        }, value.WorkId.ToString("D"));
        AuditChainDatabase.Require(matches.Count == 1 &&
            matches[0][0] == value.ManifestId.ToString("D") &&
            matches[0][1] == value.InspectionId.ToString("D") &&
            matches[0][2] == value.WorkContentHash &&
            matches[0][3] == value.ManifestContentHash &&
            matches[0][4] == value.InspectionId.ToString("D"),
            "ImageFinalizationSourceBindingMismatch");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_inspection_cores WHERE InspectionId=?;", deadline,
            value.InspectionId.ToString("D")) == 1,
            "ImageFinalizationSourceCoreMissing");
    }

    /// <summary>
    /// Replays every fact of one work and requires the mutable projection to be the exact
    /// derived result, so an edited projection row can never masquerade as persisted state.
    /// </summary>
    private static void ValidateImageFinalizationWorkProjection(sqlite3 database,
        IReadOnlyList<ImageFinalizationStoredRow> rows, StoreDeadline deadline)
    {
        var groups = rows.GroupBy(row => row.Event.WorkId).ToDictionary(group => group.Key,
            group => group.OrderBy(row => row.Position).ToArray());
        var stored = AuditChainDatabase.Read(database, @"
            SELECT WorkId,State,CleanupState,AttemptCount,NextAttemptNumber,RetryEligible,
                IntegrityConflict,ActiveAttemptId,LastFailureReasonCode,LastFailureCategory,RetryAfterUtc,
                SuccessContentHash,LastEventPosition,LastEventContentHash
            FROM image_finalization_work ORDER BY WorkId LIMIT ?;", deadline,
            statement => (WorkId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                State: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                Cleanup: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                AttemptCount: SqliteNative.ColumnInt64(statement, 3),
                NextAttempt: SqliteNative.ColumnInt64(statement, 4),
                RetryEligible: SqliteNative.ColumnInt64(statement, 5),
                Conflict: SqliteNative.ColumnInt64(statement, 6),
                Active: SqliteNative.ColumnText(statement, 7),
                Reason: SqliteNative.ColumnText(statement, 8),
                Category: SqliteNative.ColumnText(statement, 9),
                RetryAfter: SqliteNative.ColumnText(statement, 10),
                SuccessHash: SqliteNative.ColumnText(statement, 11),
                LastPosition: SqliteNative.ColumnInt64(statement, 12),
                LastHash: SqliteNative.ColumnText(statement, 13) ?? string.Empty),
            checked(rows.Count + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(stored.Count == groups.Count,
            "ImageFinalizationWorkProjectionMismatch");
        foreach (var row in stored)
        {
            if (!Guid.TryParse(row.WorkId, out var workId) || !groups.TryGetValue(workId, out var events))
                throw new InvalidOperationException("ImageFinalizationWorkProjectionMismatch");
            var state = DeriveImageFinalizationWorkState(
                events!.Select(row => row.Event).ToArray());
            string? expectedRetryAfter = null;
            if (state.LastFailure is { } failure &&
                failure.Category == ProductionImageFailureCategory.Temporary)
                expectedRetryAfter = failure.RetryAfterUtc!.Value.ToString("O",
                    CultureInfo.InvariantCulture);
            AuditChainDatabase.Require(row.State == state.State.ToString() &&
                row.Cleanup == state.CleanupState.ToString() &&
                row.AttemptCount == state.AttemptCount &&
                row.NextAttempt == state.NextAttemptNumber &&
                row.RetryEligible == (state.RetryEligible ? 1 : 0) &&
                row.Conflict == (state.IntegrityConflict ? 1 : 0) &&
                row.Active == state.ActiveAttemptId?.ToString("D") &&
                row.Reason == state.LastFailure?.ReasonCode &&
                row.Category == state.LastFailure?.Category.ToString() &&
                row.RetryAfter == expectedRetryAfter &&
                row.SuccessHash == state.Success?.ContentHash &&
                row.LastPosition == events![^1].Position &&
                row.LastHash == events[^1].Event.ContentHash,
                "ImageFinalizationWorkProjectionMismatch");
        }
    }

    private sealed record ImageFinalizationDerivedState
    {
        internal ProductionImageFinalizationState State { get; set; } = ProductionImageFinalizationState.Pending;
        internal ProductionImageCleanupState CleanupState { get; set; } = ProductionImageCleanupState.Pending;
        internal int AttemptCount { get; set; }
        internal int NextAttemptNumber { get; set; } = 1;
        internal bool RetryEligible { get; set; } = true;
        internal bool IntegrityConflict { get; set; }
        internal Guid? ActiveAttemptId { get; set; }
        internal (string ReasonCode, ProductionImageFailureCategory Category,
            DateTimeOffset? RetryAfterUtc)? LastFailure { get; set; }
        internal ProductionImageSuccessDescriptor? Success { get; set; }
    }

    private static ImageFinalizationDerivedState DeriveImageFinalizationWorkState(
        IReadOnlyList<ProductionImageFinalizationEvent> events)
    {
        var state = new ImageFinalizationDerivedState();
        var attemptLimit = int.MaxValue;
        foreach (var value in events)
        {
            switch (value.Kind)
            {
                case ProductionImageFinalizationKind.AttemptStarted:
                    state.AttemptCount = value.AttemptNumber!.Value;
                    state.NextAttemptNumber = checked(value.AttemptNumber.Value + 1);
                    state.ActiveAttemptId = value.AttemptId;
                    attemptLimit = value.Attempt!.AttemptLimit;
                    break;
                case ProductionImageFinalizationKind.AttemptFailed:
                    state.ActiveAttemptId = null;
                    state.State = ProductionImageFinalizationState.Failed;
                    state.LastFailure = (value.ReasonCode, value.Failure!.Category,
                        value.Failure.RetryAfterUtc);
                    state.RetryEligible = !state.IntegrityConflict && state.AttemptCount < attemptLimit;
                    if (value.Failure.Category == ProductionImageFailureCategory.Integrity)
                    {
                        state.IntegrityConflict = true;
                        state.RetryEligible = false;
                    }
                    break;
                case ProductionImageFinalizationKind.Succeeded:
                    state.ActiveAttemptId = null;
                    state.State = ProductionImageFinalizationState.Succeeded;
                    state.RetryEligible = false;
                    state.Success = value.Success;
                    break;
                case ProductionImageFinalizationKind.StageReleased:
                    state.CleanupState = ProductionImageCleanupState.Released;
                    break;
            }
        }
        return state;
    }

    /// <summary>Outstanding finalization events reserved for already-persisted work.</summary>
    private static (long Events, long OpenObligations, long UnreleasedSuccesses, long ActiveAttempts)
        ReadImageFinalizationReserveRows(sqlite3 database, StoreDeadline deadline)
    {
        var open = AuditChainDatabase.Scalar(database, @"
            SELECT COUNT(*) FROM pending_image_manifests m JOIN pending_image_work w
                ON w.ManifestId=m.ManifestId
            WHERE NOT EXISTS(SELECT 1 FROM image_finalization_events e
                WHERE e.WorkId=w.WorkId AND e.Kind='Succeeded');", deadline);
        var unreleased = AuditChainDatabase.Scalar(database, @"
            SELECT COUNT(*) FROM image_finalization_work
            WHERE State='Succeeded' AND CleanupState='Pending';", deadline);
        var active = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM image_finalization_work WHERE ActiveAttemptId IS NOT NULL;", deadline);
        var events = checked(
            ProductionImageFinalizationStoreOptions.ReserveEventsPerOpenObligation * open +
            ProductionImageFinalizationStoreOptions.ReserveEventsPerUnreleasedSuccess * unreleased +
            ProductionImageFinalizationStoreOptions.ReserveEventsPerActiveAttempt * active);
        return (events, open, unreleased, active);
    }

    internal static void VerifyImageFinalizationActivationPayload(sqlite3 database, byte[] payload,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "ImageFinalizationActivationPayloadMismatch");
        RequireConfiguredImageFinalization(database, options, deadline);
    }

    /// <summary>
    /// Proves one metadata entry against its stored immutable fact in both directions: the
    /// audit kind must match the fact kind, the referenced sequence and hash must be the exact
    /// row reference and the signed payload must equal the canonical binding of that row.
    /// </summary>
    internal static void VerifyImageFinalizationAuditPayload(sqlite3 database, long position,
        string kind, long sequence, string hash, byte[] payload,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= ProductionImageFinalizationStoreOptions.MaximumAuditPayloadBytes &&
            IsImageFinalizationAuditKind(kind), "ImageFinalizationAuditPayloadInvalid");
        var row = AuditChainDatabase.Read(database, @"
            SELECT Kind,AuditSequence,AuditHash,PayloadHash,Payload FROM image_finalization_events
            WHERE Position=? LIMIT 2;", deadline, statement =>
                (Kind: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                    AuditSequence: SqliteNative.ColumnInt64(statement, 1),
                    AuditHash: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                    PayloadHash: SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                    Payload: SqliteNative.ColumnText(statement, 4) ?? string.Empty),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row.Kind.Length > 0 && row.AuditSequence == sequence &&
            row.AuditHash == hash && row.Kind == kind,
            "ImageFinalizationAuditBindingMismatch");
        byte[] stored;
        try { stored = Convert.FromBase64String(row.Payload); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ImageFinalizationPayloadInvalid", exception); }
        AuditChainDatabase.Require(ProductionImageFinalizationStorageCodec.PayloadHash(stored) ==
            row.PayloadHash, "ImageFinalizationPayloadHashMismatch");
        var value = ProductionImageFinalizationStorageCodec.DecodeEvent(stored);
        AuditChainDatabase.Require(value.Position == position &&
            ImageFinalizationAuditKind(value.Kind) == kind &&
            ProductionImageFinalizationStorageCodec.EncodeAuditPayload(value).AsSpan().SequenceEqual(payload),
            "ImageFinalizationCentralPayloadBindingMismatch");
    }

    internal static void ValidateImageFinalizationAllocation(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredImageFinalization(database, options, deadline);
    }

    private static Guid ParseImageFinalizationGuid(string? value) => Guid.TryParse(value, out var parsed) &&
        parsed != Guid.Empty ? parsed : throw new InvalidOperationException("ImageFinalizationEventInvalid");

    private static DateTimeOffset ParseImageFinalizationTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed :
        throw new InvalidOperationException("ImageFinalizationEventInvalid");

    private static ProductionImageFinalizationKind ParseImageFinalizationKind(string? value) =>
        value switch
        {
            "AttemptStarted" => ProductionImageFinalizationKind.AttemptStarted,
            "AttemptFailed" => ProductionImageFinalizationKind.AttemptFailed,
            "Succeeded" => ProductionImageFinalizationKind.Succeeded,
            "StageReleased" => ProductionImageFinalizationKind.StageReleased,
            _ => throw new InvalidOperationException("ImageFinalizationEventInvalid")
        };

    private static string ImageFinalizationNumber(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
