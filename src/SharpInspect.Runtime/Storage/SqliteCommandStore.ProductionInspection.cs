using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Outbox;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// A production inspection write is admitted by Runtime, then completed by
/// this single writer.  FinalGuard is deliberately a synchronous callback:
/// it is invoked after all rows and the central audit candidate are prepared,
/// immediately before COMMIT, while the writer still owns the transaction.
/// A null result permits COMMIT; any non-empty result rolls the transaction
/// back and is returned as the rejection reason.
/// </summary>
internal sealed record ProductionInspectionAdmissionWriteRequest(
    ProductionInspectionAdmission Admission,
    Func<string?>? FinalGuard = null);

internal sealed record ProductionInspectionCoreWriteRequest(
    ProductionInspectionCore Core,
    Func<string?>? FinalGuard = null,
    ProductionImageStager.StageCommitClaim? ImageClaim = null,
    FrozenOutboxBatch? OutboxBatch = null);

internal sealed record ProductionInspectionEventWriteRequest(
    Guid InspectionId,
    ProductionInspectionEventKind Kind,
    string ReasonCode,
    DateTimeOffset RecordedAtUtc,
    long MonotonicTimestamp,
    Func<string?>? FinalGuard = null);

internal sealed class ProductionInspectionWork
{
    internal ProductionInspectionWork(ProductionInspectionAdmissionWriteRequest request)
    {
        Admission = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal ProductionInspectionWork(ProductionInspectionCoreWriteRequest request)
    {
        Core = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal ProductionInspectionWork(ProductionInspectionEventWriteRequest request)
    {
        Event = request ?? throw new ArgumentNullException(nameof(request));
    }

    internal ProductionInspectionAdmissionWriteRequest? Admission { get; }
    internal ProductionInspectionCoreWriteRequest? Core { get; }
    internal ProductionInspectionEventWriteRequest? Event { get; }
    internal TaskCompletionSource<ProductionInspectionWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record ProductionInspectionWriteResult(
    bool Committed,
    string ReasonCode,
    ProductionInspectionAdmission? Admission = null,
    ProductionInspectionCore? Core = null,
    ProductionInspectionHistoryEvent? Event = null);

internal sealed record ProductionInspectionStoredRow(
    long Position, ProductionInspectionHistoryEvent Event, byte[] Payload, string PayloadHash);

internal sealed partial class SqliteCommandStore
{
    // The writer owns all mutations. The projection is only an in-process
    // fast path for the follow-up lifecycle event; cold readers still verify
    // the immutable rows through SqliteProductionInspectionHistoryQuery.
    private readonly object _productionInspectionProjectionSync = new();
    private readonly Dictionary<Guid, (ProductionInspectionAdmission Admission, long Position)> _productionInspectionAdmissions = new();
    private readonly Dictionary<Guid, ProductionInspectionCore> _productionInspectionCores = new();

    internal const string ProductionInspectionStoreActivatedKind =
        "ProductionInspectionStoreActivated";
    internal const string ProductionInspectionEventAuditKind =
        "ProductionInspectionEvent";

    internal const string ProductionInspectionSchemaSql = @"
        CREATE TABLE production_inspection_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE production_inspection_admissions(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            InspectionId TEXT NOT NULL UNIQUE CHECK(length(InspectionId)=36),
            CorrelationId TEXT NOT NULL CHECK(length(CorrelationId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            StationId TEXT NOT NULL,
            AdmissionGeneration INTEGER NOT NULL CHECK(AdmissionGeneration>=0),
            ControllerEpoch INTEGER NOT NULL CHECK(ControllerEpoch>=0),
            CycleSequence INTEGER NOT NULL CHECK(CycleSequence>=0),
            EvidenceRequirement INTEGER NOT NULL CHECK(EvidenceRequirement IN (1,2,3)),
            ActivationPosition INTEGER NOT NULL CHECK(ActivationPosition>0),
            ActivationId TEXT NOT NULL CHECK(length(ActivationId)=36),
            ActivationHash TEXT NOT NULL CHECK(length(ActivationHash)=64),
            ActivationSnapshotHash TEXT NOT NULL CHECK(length(ActivationSnapshotHash)=64),
            EndpointBindingHash TEXT NOT NULL CHECK(length(EndpointBindingHash)=64),
            PlcProfileHash TEXT NOT NULL CHECK(length(PlcProfileHash)=64),
            PlcPolicyHash TEXT NOT NULL CHECK(length(PlcPolicyHash)=64),
            ConnectionGeneration INTEGER NOT NULL CHECK(ConnectionGeneration>=0),
            ConnectionAttempt INTEGER NOT NULL CHECK(ConnectionAttempt>=0),
            AcceptedAtUtc TEXT NOT NULL,
            AcceptedMonotonicTimestamp INTEGER NOT NULL CHECK(AcceptedMonotonicTimestamp>0),
            TracePolicyVersion INTEGER NOT NULL CHECK(TracePolicyVersion>0),
            TracePolicyHash TEXT NOT NULL CHECK(length(TracePolicyHash)=64),
            RetentionObligationsHash TEXT NOT NULL CHECK(length(RetentionObligationsHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE TABLE production_inspection_cores(
            InspectionId TEXT NOT NULL PRIMARY KEY CHECK(length(InspectionId)=36),
            AdmissionPosition INTEGER NOT NULL CHECK(AdmissionPosition>0),
            AdmissionContentHash TEXT NOT NULL CHECK(length(AdmissionContentHash)=64),
            State INTEGER NOT NULL CHECK(State IN (2,3,4)),
            ExecutionStatus INTEGER NOT NULL,
            Decision INTEGER NOT NULL,
            ReasonCode TEXT NOT NULL,
            AcquisitionFailureKind INTEGER NULL,
            AcquisitionFailureReasonCode TEXT NULL,
            PreparedAlgorithmInstanceId TEXT NOT NULL CHECK(length(PreparedAlgorithmInstanceId)=36),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL,
            CommittedAtUtc TEXT NOT NULL,
            CommittedMonotonicTimestamp INTEGER NOT NULL CHECK(CommittedMonotonicTimestamp>0),
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE TABLE production_inspection_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            InspectionId TEXT NOT NULL CHECK(length(InspectionId)=36),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 10),
            ReasonCode TEXT NOT NULL,
            RecordedAtUtc TEXT NOT NULL,
            MonotonicTimestamp INTEGER NOT NULL CHECK(MonotonicTimestamp>0),
            AdmissionContentHash TEXT NOT NULL CHECK(length(AdmissionContentHash)=64),
            CoreContentHash TEXT NULL CHECK(CoreContentHash IS NULL OR length(CoreContentHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            UNIQUE(InspectionId,Kind));
        CREATE INDEX ix_production_inspection_admission_correlation
            ON production_inspection_admissions(CorrelationId,Position);
        CREATE INDEX ix_production_inspection_events_inspection
            ON production_inspection_events(InspectionId,Position);
        CREATE TRIGGER production_inspection_config_immutable_update
            BEFORE UPDATE ON production_inspection_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionConfiguration'); END;
        CREATE TRIGGER production_inspection_config_immutable_delete
            BEFORE DELETE ON production_inspection_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionConfiguration'); END;
        CREATE TRIGGER production_inspection_admission_immutable_update
            BEFORE UPDATE ON production_inspection_admissions BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionAdmission'); END;
        CREATE TRIGGER production_inspection_admission_immutable_delete
            BEFORE DELETE ON production_inspection_admissions BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionAdmission'); END;
        CREATE TRIGGER production_inspection_core_immutable_update
            BEFORE UPDATE ON production_inspection_cores BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionCore'); END;
        CREATE TRIGGER production_inspection_core_immutable_delete
            BEFORE DELETE ON production_inspection_cores BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionCore'); END;
        CREATE TRIGGER production_inspection_event_immutable_update
            BEFORE UPDATE ON production_inspection_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionEvent'); END;
        CREATE TRIGGER production_inspection_event_immutable_delete
            BEFORE DELETE ON production_inspection_events BEGIN
            SELECT RAISE(ABORT,'ImmutableProductionInspectionEvent'); END;";

    internal static IReadOnlyList<ProductionInspectionStoredRow> ReadProductionInspectionRows(
        sqlite3 database, ProductionInspectionStoreOptions options, StoreDeadline deadline,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? calibrationResolver = null)
    {
        RequireConfiguredProductionInspection(database, options, deadline);
        calibrationResolver ??= ReadStationQualificationProfileResolver(database, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,InspectionId,Kind,ReasonCode,RecordedAtUtc,MonotonicTimestamp,
                AdmissionContentHash,CoreContentHash,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash
            FROM production_inspection_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadProductionInspectionRow(statement, options, calibrationResolver),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "ProductionInspectionEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + ProductionInspectionStoredPayloadBytes(row.Payload.Length)));
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "ProductionInspectionTotalCapacityExceeded");
        return rows;
    }

    internal static void ValidateProductionInspectionHistory(
        sqlite3 database, ProductionInspectionStoreOptions options, StoreDeadline deadline)
    {
        var rows = ReadProductionInspectionRows(database, options, deadline);
        long previous = 0;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == previous + 1,
                "ProductionInspectionPositionGap");
            previous = row.Position;
        }
        ValidateProductionInspectionProjections(database, rows, deadline);
        ValidateProductionRecoveryHistory(database, rows, options, deadline);
        var reserved = ReadProductionInspectionReservation(database, deadline);
        AuditChainDatabase.Require(rows.Count + reserved.Rows <= options.MaximumEntries,
            "ProductionInspectionEntryCapacityExceeded");
        var usedBytes = rows.Sum(value => ProductionInspectionStoredPayloadBytes(value.Payload.Length));
        AuditChainDatabase.Require(checked(usedBytes + reserved.Rows *
            ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <= options.MaximumTotalBytes,
            "ProductionInspectionTotalCapacityExceeded");
    }

    internal static void VerifyProductionInspectionActivationPayload(
        sqlite3 database, byte[] payload, ProductionInspectionStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "ProductionInspectionActivationPayloadMismatch");
        RequireConfiguredProductionInspection(database, options, deadline);
    }

    internal static void VerifyProductionInspectionAuditPayload(
        sqlite3 database, long position, byte[] payload, ProductionInspectionStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= options.MaximumPayloadBytes,
            "ProductionInspectionAuditPayloadInvalid");
        var binding = AuditChainDatabase.Read(database, @"
            SELECT Kind,Payload FROM audit_entries
            WHERE ProductionInspectionPosition=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Payload: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(binding.Kind == ProductionInspectionEventAuditKind,
            "ProductionInspectionAuditBindingMismatch");
        byte[] centralPayload;
        try { centralPayload = Convert.FromBase64String(binding.Payload); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ProductionInspectionAuditPayloadInvalid", exception); }
        AuditChainDatabase.Require(centralPayload.SequenceEqual(payload),
            "ProductionInspectionAuditPayloadMismatch");
        var row = AuditChainDatabase.Read(database, @"
            SELECT AuditSequence,AuditHash,PayloadHash
            FROM production_inspection_events WHERE Position=? LIMIT 2;", deadline,
            statement => (AuditSequence: SqliteNative.ColumnInt64(statement, 0),
                AuditHash: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                PayloadHash: SqliteNative.ColumnText(statement, 2) ?? string.Empty),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row.AuditSequence > 0 && AuditCanonical.IsHash(row.AuditHash) &&
            AuditCanonical.IsHash(row.PayloadHash), "ProductionInspectionAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Hash FROM audit_entries
            WHERE ProductionInspectionPosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(audit.Sequence == row.AuditSequence && audit.Hash == row.AuditHash,
            "ProductionInspectionAuditBindingMismatch");
        var eventPayloadText = AuditChainDatabase.Text(database,
            "SELECT Payload FROM production_inspection_events WHERE Position=?;", deadline,
            position.ToString(CultureInfo.InvariantCulture));
        byte[] eventPayload;
        try { eventPayload = Convert.FromBase64String(eventPayloadText ?? string.Empty); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ProductionInspectionPayloadInvalid", exception); }
        AuditChainDatabase.Require(ProductionInspectionStorageCodec.PayloadHash(eventPayload) == row.PayloadHash,
            "ProductionInspectionPayloadHashMismatch");
        var actual = ProductionInspectionStorageCodec.DecodeEventEnvelope(eventPayload,
            ReadStationQualificationProfileResolver(database, deadline));
        AuditChainDatabase.Require(actual.Position == position &&
            ProductionInspectionStorageCodec.EncodeAuditBinding(actual).AsSpan().SequenceEqual(payload),
            "ProductionInspectionCentralPayloadBindingMismatch");
    }

    private static ProductionInspectionStoredRow ReadProductionInspectionRow(
        sqlite3_stmt statement, ProductionInspectionStoreOptions options,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? calibrationResolver)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var inspectionId = ParseProductionInspectionGuid(SqliteNative.ColumnText(statement, 1));
        var kind = (ProductionInspectionEventKind)checked((byte)SqliteNative.ColumnInt64(statement, 2));
        var reason = SqliteNative.ColumnText(statement, 3) ?? string.Empty;
        var recordedAt = ParseProductionInspectionTime(SqliteNative.ColumnText(statement, 4));
        var monotonic = SqliteNative.ColumnInt64(statement, 5);
        var admissionHash = SqliteNative.ColumnText(statement, 6);
        var coreHash = SqliteNative.ColumnText(statement, 7);
        var contentHash = SqliteNative.ColumnText(statement, 8);
        var payloadHash = SqliteNative.ColumnText(statement, 9);
        var encoded = SqliteNative.ColumnText(statement, 10) ?? string.Empty;
        var auditSequence = SqliteNative.ColumnInt64(statement, 11);
        var auditHash = SqliteNative.ColumnText(statement, 12);
        AuditChainDatabase.Require(AuditCanonical.IsHash(contentHash) &&
            AuditCanonical.IsHash(payloadHash) && AuditCanonical.IsHash(auditHash) &&
            encoded.Length <= checked(options.MaximumPayloadBytes * 2),
            "ProductionInspectionEventInvalid");
        byte[] payload;
        try { payload = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new InvalidOperationException("ProductionInspectionPayloadInvalid", exception); }
        AuditChainDatabase.Require(payload.Length is > 0 and <= ProductionInspectionStoreOptions.MaximumPayloadBytesHardLimit &&
            payload.Length <= options.MaximumPayloadBytes &&
            Convert.ToHexString(SHA256.HashData(payload)) == payloadHash,
            "ProductionInspectionPayloadInvalid");
        var value = ProductionInspectionStorageCodec.DecodeEventEnvelope(payload, calibrationResolver);
        AuditChainDatabase.Require(value.Position == position && value.InspectionId == inspectionId &&
            value.Kind == kind && value.ReasonCode == reason && value.RecordedAtUtc == recordedAt &&
            value.MonotonicTimestamp == monotonic && value.Admission.ContentHash == admissionHash &&
            value.Core?.ContentHash == coreHash && value.ContentHash == contentHash &&
            value.AuditSequence == auditSequence && value.AuditHash == auditHash,
            "ProductionInspectionEventBindingMismatch");
        return new ProductionInspectionStoredRow(position, value, payload, payloadHash!);
    }

    private static Guid ParseProductionInspectionGuid(string? value) => Guid.TryParse(value, out var parsed) &&
        parsed != Guid.Empty ? parsed : throw new InvalidOperationException("ProductionInspectionEventInvalid");

    private static DateTimeOffset ParseProductionInspectionTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed :
        throw new InvalidOperationException("ProductionInspectionEventInvalid");

    internal static void InitializeProductionInspectionSchema(sqlite3 database,
        ProductionInspectionStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, ProductionInspectionSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_inspection_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            ProductionInspectionStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendProductionInspectionStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredProductionInspection(sqlite3 database,
        ProductionInspectionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM production_inspection_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4) ?? string.Empty)).SingleOrDefault();
        AuditChainDatabase.Require(configured.FormatVersion == ProductionInspectionStoreOptions.FormatVersion &&
            configured.MaximumEntries == options.MaximumEntries &&
            configured.MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured.MaximumTotalBytes == options.MaximumTotalBytes &&
            configured.BindingHash == options.BindingHash,
            "ProductionInspectionConfigurationMismatch");
    }

    internal ValueTask<ProductionInspectionWriteResult> AdmitProductionInspectionAsync(
        ProductionInspectionAdmissionWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) => EnqueueProductionInspectionAsync(
            new ProductionInspectionWork(request), deadline, cancellationToken);

    internal ValueTask<ProductionInspectionWriteResult> CommitProductionInspectionCoreAsync(
        ProductionInspectionCoreWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) => EnqueueProductionInspectionAsync(
            new ProductionInspectionWork(request), deadline, cancellationToken);

    internal ValueTask<ProductionInspectionWriteResult> AppendProductionInspectionEventAsync(
        ProductionInspectionEventWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) => EnqueueProductionInspectionAsync(
            new ProductionInspectionWork(request), deadline, cancellationToken);

    private async ValueTask<ProductionInspectionWriteResult> EnqueueProductionInspectionAsync(
        ProductionInspectionWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProductionInspectionEnabled || Volatile.Read(ref _disposed) != 0 ||
            _queue is null || _queueSlots is null || _worker.IsCompleted)
            return new(false, ProductionInspectionEnabled ? "ProductionInspectionUnavailable" :
                "ProductionInspectionConfigurationRequired");
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "ProductionInspectionCommitDeadlineExceeded");
        var queued = new WriteRequest(null, deadline, ProductionInspection: work);
        if (!_queue.Writer.TryWrite(queued))
        {
            _queueSlots.Release();
            return new(false, Volatile.Read(ref _disposed) != 0 ? "TraceStoreDisposed" :
                "ProductionInspectionUnavailable");
        }
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendProductionInspectionCore(sqlite3 database,
        ProductionInspectionWork work, StoreDeadline deadline)
    {
        if (Integrity?.State == AuditIntegrityState.Faulted)
            return ProductionInspectionRejected(work, Integrity.ReasonCode);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return ProductionInspectionRejected(work, "TraceStoreWalLimit");
        if (work.Admission is not null)
            return AppendProductionInspectionAdmissionCore(database, work, deadline);
        if (work.Core is not null)
            return AppendProductionInspectionCoreCommit(database, work, deadline);
        if (work.Event is not null)
            return AppendProductionInspectionEventCore(database, work, deadline);
        throw new InvalidOperationException("ProductionInspectionWriteInvalid");
    }

    private StoreWriteResult AppendProductionInspectionAdmissionCore(sqlite3 database,
        ProductionInspectionWork work, StoreDeadline deadline)
    {
        var request = work.Admission ?? throw new InvalidOperationException("ProductionInspectionWriteInvalid");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredProductionInspection(database, options, deadline);
            AuditChainDatabase.RequireFullProductionInspectionVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            EnsureImageEvidenceAdmissionCapacity(database, request.Admission, deadline);
            ValidateProductionInspectionAdmissionUniqueness(database, options,
                request.Admission, deadline);
            var position = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0)+1 FROM production_inspection_events;", deadline));
            var provisional = new ProductionInspectionHistoryEvent(position,
                ProductionInspectionEventKind.Admitted, request.Admission, null, "ProductionInspectionAdmitted",
                request.Admission.AcceptedAtUtc, request.Admission.AcceptedMonotonicTimestamp);
            var reservation = ReadProductionInspectionReservation(database, deadline, provisional.InspectionId, provisional.Kind);
            var audit = AuditChainDatabase.AppendProductionInspectionLedgerEntry(database, policy, signingKey,
                position, ProductionInspectionStorageCodec.EncodeAuditBinding(provisional), options, deadline,
                productionInspectionReserveOverride: reservation.AuditEntries);
            var persisted = new ProductionInspectionHistoryEvent(position,
                ProductionInspectionEventKind.Admitted, request.Admission, null,
                provisional.ReasonCode, provisional.RecordedAtUtc, provisional.MonotonicTimestamp,
                audit.Sequence, audit.Hash);
            var payload = ProductionInspectionStorageCodec.Encode(persisted);
            EnsureProductionPayloadCapacity(database, options, payload, reservation, deadline);
            AuditChainDatabase.Execute(database, @"
                INSERT INTO production_inspection_admissions(
                    Position,InspectionId,CorrelationId,RuntimeEpoch,StationId,AdmissionGeneration,
                    ControllerEpoch,CycleSequence,EvidenceRequirement,ActivationPosition,ActivationId,
                    ActivationHash,ActivationSnapshotHash,EndpointBindingHash,PlcProfileHash,PlcPolicyHash,
                    ConnectionGeneration,ConnectionAttempt,AcceptedAtUtc,AcceptedMonotonicTimestamp,
                    TracePolicyVersion,TracePolicyHash,RetentionObligationsHash,ContentHash,PayloadHash,
                    Payload,AuditSequence,AuditHash)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                position.ToString(CultureInfo.InvariantCulture), request.Admission.InspectionId.ToString("D"),
                request.Admission.CorrelationId.ToString("D"), request.Admission.RuntimeEpoch.ToString("D"),
                request.Admission.StationId,
                request.Admission.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
                request.Admission.ControllerCycle.ControllerEpoch.ToString(CultureInfo.InvariantCulture),
                request.Admission.ControllerCycle.CycleSequence.ToString(CultureInfo.InvariantCulture),
                ((int)request.Admission.EvidenceRequirement).ToString(CultureInfo.InvariantCulture),
                request.Admission.ActivationReference.Position.ToString(CultureInfo.InvariantCulture),
                request.Admission.ActivationReference.ActivationId.ToString("D"),
                request.Admission.ActivationReference.ContentHash,
                request.Admission.ActivationSnapshot.ContentHash,
                request.Admission.EndpointBindingHash, request.Admission.PlcProfileHash,
                request.Admission.PlcPolicyHash,
                request.Admission.ConnectionGeneration.ToString(CultureInfo.InvariantCulture),
                request.Admission.ConnectionAttempt.ToString(CultureInfo.InvariantCulture),
                request.Admission.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                request.Admission.AcceptedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
                request.Admission.TracePolicySnapshot.Version.ToString(CultureInfo.InvariantCulture),
                request.Admission.TracePolicySnapshot.ContentHash,
                RetentionHash(request.Admission.RetentionObligations), request.Admission.ContentHash,
                ProductionInspectionStorageCodec.PayloadHash(payload), Convert.ToBase64String(payload),
                persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash!);
            InsertProductionInspectionEvent(database, persisted, payload, deadline);
            var durable = ReadPersistedProductionInspectionEvent(database, options, position, deadline).Event;
            var outboxReason = ProductionOutboxAdmissionFailure(database, request.Admission, deadline);
            if (outboxReason is not null)
                return ProductionInspectionRejected(work, outboxReason);
            // T52: the candidate is durable in this transaction but not yet accepted, so its
            // complete uncreated outbox liability must fit before the final fence may commit.
            var outboxCapacityReason = ProductionOutboxAdmissionCapacityFailure(database,
                request.Admission, deadline);
            if (outboxCapacityReason is not null)
                return ProductionInspectionRejected(work, outboxCapacityReason);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ProductionInspectionRejected(work, guardReason);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            lock (_productionInspectionProjectionSync)
                _productionInspectionAdmissions[durable.Admission.InspectionId] =
                    (durable.Admission, durable.Position);
            work.Completion.TrySetResult(new(true, "ProductionInspectionAdmitted",
                Admission: durable.Admission, Event: durable));
            PublishProductionInspectionIntegrity(policy, audit.Sequence);
            return new(true, "ProductionInspectionAdmitted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ProductionInspectionRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ProductionInspection", StringComparison.Ordinal))
        { return ProductionInspectionRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ProductionInspectionRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex, "ProductionInspectionAdmissionFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendProductionInspectionCoreCommit(sqlite3 database,
        ProductionInspectionWork work, StoreDeadline deadline)
    {
        var request = work.Core ?? throw new InvalidOperationException("ProductionInspectionWriteInvalid");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredProductionInspection(database, options, deadline);
            AuditChainDatabase.RequireFullProductionInspectionVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var admission = ReadProductionInspectionAdmissionIdentity(database, options,
                request.Core.Admission.InspectionId, deadline);
            AuditChainDatabase.Require(admission is not null &&
                admission.Value.Admission.ContentHash == request.Core.Admission.ContentHash,
                "ProductionInspectionAdmissionMissing");
            AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
                "SELECT COUNT(*) FROM production_inspection_cores WHERE InspectionId=?;", deadline,
                request.Core.Admission.InspectionId.ToString("D")) == 0, "ProductionInspectionCoreDuplicate");
            var position = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0)+1 FROM production_inspection_events;", deadline));
            var provisional = new ProductionInspectionHistoryEvent(position,
                ProductionInspectionEventKind.CoreCommitted, request.Core.Admission, request.Core,
                request.Core.ReasonCode, request.Core.CommittedAtUtc, request.Core.CommittedMonotonicTimestamp);
            var reservation = ReadProductionInspectionReservation(database, deadline, provisional.InspectionId, provisional.Kind);
            var audit = AuditChainDatabase.AppendProductionInspectionLedgerEntry(database, policy, signingKey,
                position, ProductionInspectionStorageCodec.EncodeAuditBinding(provisional), options, deadline,
                productionInspectionReserveOverride: reservation.AuditEntries);
            var persisted = new ProductionInspectionHistoryEvent(position,
                ProductionInspectionEventKind.CoreCommitted, request.Core.Admission, request.Core,
                provisional.ReasonCode, provisional.RecordedAtUtc, provisional.MonotonicTimestamp,
                audit.Sequence, audit.Hash);
            var payload = ProductionInspectionStorageCodec.Encode(persisted);
            EnsureProductionPayloadCapacity(database, options, payload, reservation, deadline);
            AuditChainDatabase.Execute(database, @"
                INSERT INTO production_inspection_cores(
                    InspectionId,AdmissionPosition,AdmissionContentHash,State,ExecutionStatus,Decision,
                    ReasonCode,AcquisitionFailureKind,AcquisitionFailureReasonCode,PreparedAlgorithmInstanceId,
                    ContentHash,PayloadHash,Payload,CommittedAtUtc,CommittedMonotonicTimestamp,AuditSequence,AuditHash)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                request.Core.Admission.InspectionId.ToString("D"),
                admission!.Value.Position.ToString(CultureInfo.InvariantCulture), request.Core.Admission.ContentHash,
                ((int)request.Core.State).ToString(CultureInfo.InvariantCulture),
                ((int)request.Core.ExecutionStatus).ToString(CultureInfo.InvariantCulture),
                ((int)request.Core.Decision).ToString(CultureInfo.InvariantCulture), request.Core.ReasonCode,
                request.Core.AcquisitionFailureKind is { } kind ? ((int)kind).ToString(CultureInfo.InvariantCulture) : null,
                request.Core.AcquisitionFailureReasonCode,
                request.Core.PreparedAlgorithmInstanceId.ToString("D"), request.Core.ContentHash,
                ProductionInspectionStorageCodec.PayloadHash(payload), Convert.ToBase64String(payload),
                request.Core.CommittedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                request.Core.CommittedMonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
                persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash!);
            InsertProductionImageEvidence(database, request, deadline);
            InsertProductionOutboxBatch(database, request, deadline);
            InsertProductionInspectionEvent(database, persisted, payload, deadline);
            var durable = ReadPersistedProductionInspectionEvent(database, options, position, deadline).Event;
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ProductionInspectionRejected(work, guardReason);
            ConsumeProductionImageClaim(request);
            var committedAuditSequence = _options.Outbox is null ? audit.Sequence :
                AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            lock (_productionInspectionProjectionSync)
            {
                _productionInspectionAdmissions[durable.Admission.InspectionId] =
                    (durable.Admission, admission!.Value.Position);
                _productionInspectionCores[durable.Admission.InspectionId] =
                    durable.Core ?? throw new InvalidOperationException("ProductionInspectionCoreMissing");
            }
            work.Completion.TrySetResult(new(true, "ProductionInspectionCoreCommitted",
                Core: durable.Core, Event: durable));
            PublishProductionInspectionIntegrity(policy, committedAuditSequence);
            return new(true, "ProductionInspectionCoreCommitted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ProductionInspectionRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ProductionInspection", StringComparison.Ordinal))
        { return ProductionInspectionRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ProductionInspectionRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex, "ProductionInspectionCoreFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendProductionInspectionEventCore(sqlite3 database,
        ProductionInspectionWork work, StoreDeadline deadline)
    {
        var request = work.Event ?? throw new InvalidOperationException("ProductionInspectionWriteInvalid");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            if (request.InspectionId == Guid.Empty || !Enum.IsDefined(request.Kind) ||
                request.RecordedAtUtc == default || request.RecordedAtUtc.Offset != TimeSpan.Zero ||
                request.MonotonicTimestamp <= 0)
                throw new InvalidOperationException("ProductionInspectionEventIdentityInvalid");
            if (ProductionRecoveryEnabled && request.Kind is
                (ProductionInspectionEventKind.RecoveryRequired or ProductionInspectionEventKind.RecoveryCompleted))
                throw new InvalidOperationException("ProductionRecoveryDedicatedWriterRequired");
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            AuditChainDatabase.RequireFullProductionInspectionVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var admission = ReadProductionInspectionAdmissionIdentity(database, options,
                request.InspectionId, deadline) ??
                throw new InvalidOperationException("ProductionInspectionAdmissionMissing");
            var history = ReadProductionInspectionRows(database, options, deadline);
            AuditChainDatabase.Require(!history.Any(value =>
                value.Event.InspectionId == request.InspectionId && value.Event.Kind == request.Kind),
                "ProductionInspectionEventDuplicate");
            ValidateProductionInspectionEventTransition(history, request.InspectionId, request.Kind);
            var core = ReadProductionInspectionCoreIdentity(database, options, request.InspectionId, deadline);
            if (request.Kind is ProductionInspectionEventKind.PublicationPrepared or
                ProductionInspectionEventKind.ResultValidRaised or
                ProductionInspectionEventKind.ResultAcknowledged or
                ProductionInspectionEventKind.ResultValidCleared or
                ProductionInspectionEventKind.AcknowledgementReset)
                AuditChainDatabase.Require(core is not null, "ProductionInspectionCoreMissing");
            var position = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0)+1 FROM production_inspection_events;", deadline));
            var provisional = new ProductionInspectionHistoryEvent(position, request.Kind, admission.Admission,
                core, request.ReasonCode, request.RecordedAtUtc.ToUniversalTime(), request.MonotonicTimestamp);
            var reservation = ReadProductionInspectionReservation(database, deadline, provisional.InspectionId, provisional.Kind);
            var audit = AuditChainDatabase.AppendProductionInspectionLedgerEntry(database, policy, signingKey,
                position, ProductionInspectionStorageCodec.EncodeAuditBinding(provisional), options, deadline,
                productionInspectionReserveOverride: reservation.AuditEntries);
            var persisted = new ProductionInspectionHistoryEvent(position, request.Kind, admission.Admission,
                core, provisional.ReasonCode, provisional.RecordedAtUtc, provisional.MonotonicTimestamp,
                audit.Sequence, audit.Hash);
            var payload = ProductionInspectionStorageCodec.Encode(persisted);
            EnsureProductionPayloadCapacity(database, options, payload, reservation, deadline);
            InsertProductionInspectionEvent(database, persisted, payload, deadline);
            var durable = ReadPersistedProductionInspectionEvent(database, options, position, deadline).Event;
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ProductionInspectionRejected(work, guardReason);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "ProductionInspectionEventAppended", Event: durable));
            PublishProductionInspectionIntegrity(policy, audit.Sequence);
            return new(true, "ProductionInspectionEventAppended");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ProductionInspectionRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ProductionInspection", StringComparison.Ordinal))
        { return ProductionInspectionRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ProductionInspectionRejected(work, ex is InvalidOperationException &&
            ex.Message.StartsWith("ProductionRecovery", StringComparison.Ordinal) ? ex.Message :
            SqliteAuditIntegrityQuery.FaultReason(ex, "ProductionInspectionEventFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private static StoreWriteResult ProductionInspectionRejected(
        ProductionInspectionWork work, string reason)
    {
        work.Completion.TrySetResult(new(false, reason));
        return new(false, reason);
    }

    private static string? EvaluateProductionFinalGuard(Func<string?>? finalGuard)
    {
        if (finalGuard is null) return null;
        var reason = finalGuard();
        return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private static void EnsureProductionPayloadCapacity(sqlite3 database,
        ProductionInspectionStoreOptions options, byte[] payload, ProductionInspectionReservation reserved,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(payload.Length > 0 && payload.Length <= options.MaximumPayloadBytes,
            "ProductionInspectionPayloadCapacityExceeded");
        var used = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM production_inspection_events;", deadline);
        var usedRows = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_inspection_events;", deadline);
        AuditChainDatabase.Require(checked(usedRows + 1 + reserved.Rows) <= options.MaximumEntries,
            "ProductionInspectionEntryCapacityExceeded");
        AuditChainDatabase.Require(checked(used + ProductionInspectionStoredPayloadBytes(payload.Length) +
            reserved.Rows * ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <= options.MaximumTotalBytes,
            "ProductionInspectionTotalCapacityExceeded");
    }

    private static void InsertProductionInspectionEvent(sqlite3 database,
        ProductionInspectionHistoryEvent value, byte[] payload, StoreDeadline deadline) =>
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_inspection_events(
                Position,InspectionId,Kind,ReasonCode,RecordedAtUtc,MonotonicTimestamp,
                AdmissionContentHash,CoreContentHash,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            value.Position.ToString(CultureInfo.InvariantCulture), value.InspectionId.ToString("D"),
            ((int)value.Kind).ToString(CultureInfo.InvariantCulture), value.ReasonCode,
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            value.Admission.ContentHash, value.Core?.ContentHash, value.ContentHash,
            ProductionInspectionStorageCodec.PayloadHash(payload), Convert.ToBase64String(payload),
            value.AuditSequence.ToString(CultureInfo.InvariantCulture), value.AuditHash!);

    private void PublishProductionInspectionIntegrity(AuditIntegrityPolicy policy, long sequence)
    {
        Interlocked.Exchange(ref _lastCommittedAuditSequence, sequence);
        PublishIntegrity(SqliteAuditIntegrityQuery.Report(policy, AuditIntegrityState.Verifying,
            policy.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
        WakeIntegrityMonitor();
    }

    private static ProductionInspectionStoredRow ReadPersistedProductionInspectionEvent(
        sqlite3 database, ProductionInspectionStoreOptions options, long position,
        StoreDeadline deadline)
    {
        var row = ReadProductionInspectionRows(database, options, deadline)
            .SingleOrDefault(value => value.Position == position);
        AuditChainDatabase.Require(row is not null, "ProductionInspectionPersistedEventMissing");
        return row!;
    }

    private static void ValidateProductionInspectionAdmissionUniqueness(
        sqlite3 database, ProductionInspectionStoreOptions options,
        ProductionInspectionAdmission candidate, StoreDeadline deadline)
    {
        var history = ReadProductionInspectionRows(database, options, deadline)
            .Where(value => value.Event.Kind == ProductionInspectionEventKind.Admitted)
            .ToArray();
        foreach (var row in history)
        {
            var admission = row.Event.Admission;
            AuditChainDatabase.Require(admission.InspectionId != candidate.InspectionId &&
                admission.CorrelationId != candidate.CorrelationId,
                "ProductionInspectionAdmissionDuplicate");
            ValidateProductionInspectionPartIdentityUniqueness(admission, candidate);
            AuditChainDatabase.Require(!(admission.EndpointBindingHash == candidate.EndpointBindingHash &&
                admission.ControllerCycle == candidate.ControllerCycle),
                "ProductionInspectionControllerCycleDuplicate");
        }
    }

    private static void ValidateProductionInspectionPartIdentityUniqueness(
        ProductionInspectionAdmission existing, ProductionInspectionAdmission candidate)
    {
        if (existing.PartIdentityEvidence is not { } prior ||
            candidate.PartIdentityEvidence is not { } current)
            return;

        if (prior.SourceKind != current.SourceKind)
            return;

        if (current.SourceKind == PartIdentityProviderSourceKind.StablePlc)
        {
            if (prior.StablePlcSnapshot is { } priorPlc &&
                current.StablePlcSnapshot is { } currentPlc &&
                string.Equals(prior.SourceContractHash, current.SourceContractHash,
                    StringComparison.Ordinal) &&
                string.Equals(prior.Cycle.EndpointBindingHash,
                    current.Cycle.EndpointBindingHash, StringComparison.Ordinal) &&
                prior.Cycle.ControllerEpoch == current.Cycle.ControllerEpoch &&
                priorPlc.Revision == currentPlc.Revision)
                throw new InvalidOperationException("ProductionInspectionPartIdentitySourceTokenDuplicate");
        }
        else if (current.SourceKind == PartIdentityProviderSourceKind.Staged &&
            prior.SourceEpoch == current.SourceEpoch &&
            prior.StageToken is { } priorToken && current.StageToken is { } currentToken &&
            priorToken == currentToken)
        {
            throw new InvalidOperationException("ProductionInspectionPartIdentitySourceTokenDuplicate");
        }
    }

    private static (ProductionInspectionAdmission Admission, long Position)?
        ReadProductionInspectionAdmissionIdentity(sqlite3 database,
            ProductionInspectionStoreOptions options, Guid inspectionId, StoreDeadline deadline)
    {
        var matches = ReadProductionInspectionRows(database, options, deadline)
            .Where(value => value.Event.Kind == ProductionInspectionEventKind.Admitted &&
                value.Event.InspectionId == inspectionId).ToArray();
        AuditChainDatabase.Require(matches.Length <= 1, "ProductionInspectionAdmissionDuplicate");
        return matches.Length == 0 ? null :
            (matches[0].Event.Admission, matches[0].Position);
    }

    private static ProductionInspectionCore? ReadProductionInspectionCoreIdentity(
        sqlite3 database, ProductionInspectionStoreOptions options, Guid inspectionId,
        StoreDeadline deadline)
    {
        var matches = ReadProductionInspectionRows(database, options, deadline)
            .Where(value => value.Event.Kind == ProductionInspectionEventKind.CoreCommitted &&
                value.Event.InspectionId == inspectionId).ToArray();
        AuditChainDatabase.Require(matches.Length <= 1, "ProductionInspectionCoreDuplicate");
        return matches.Length == 0 ? null : matches[0].Event.Core;
    }

    private static void ValidateProductionInspectionEventTransition(
        IReadOnlyList<ProductionInspectionStoredRow> history, Guid inspectionId,
        ProductionInspectionEventKind requestedKind)
    {
        var latest = history.Where(value => value.Event.InspectionId == inspectionId)
            .OrderBy(value => value.Position).LastOrDefault()?.Event.Kind;
        ProductionInspectionEventKind? required = requestedKind switch
        {
            ProductionInspectionEventKind.PublicationPrepared => ProductionInspectionEventKind.CoreCommitted,
            ProductionInspectionEventKind.ResultValidRaised => ProductionInspectionEventKind.PublicationPrepared,
            ProductionInspectionEventKind.ResultAcknowledged => ProductionInspectionEventKind.ResultValidRaised,
            ProductionInspectionEventKind.ResultValidCleared => ProductionInspectionEventKind.ResultAcknowledged,
            ProductionInspectionEventKind.AcknowledgementReset => ProductionInspectionEventKind.ResultValidCleared,
            _ => null
        };
        if (required.HasValue)
            AuditChainDatabase.Require(latest == required.Value,
                "ProductionInspectionEventTransitionInvalid");
    }

    private static string RetentionHash(IReadOnlyCollection<TraceRetentionObligation> obligations) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("ProductionInspectionRetentionV1",
            obligations.OrderBy(value => value.ContentHash, StringComparer.Ordinal)
                .Select(value => value.ContentHash).ToArray())));
}
