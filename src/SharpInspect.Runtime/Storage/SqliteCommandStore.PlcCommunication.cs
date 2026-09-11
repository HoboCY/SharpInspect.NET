using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Input supplied by the single PLC communication owner.</summary>
internal sealed record PlcCommunicationWriteRequest(
    Guid RuntimeEpoch,
    string EndpointBindingHash,
    string ProfileHash,
    string PolicyHash,
    long Generation,
    int Attempt,
    PlcCommunicationEventKind Kind,
    string ReasonCode,
    uint? ControllerEpoch,
    DateTimeOffset ObservedAtUtc,
    long MonotonicTimestamp,
    Guid? RecoveryCycleId = null,
    Guid? QualificationSessionId = null,
    Guid? RunId = null,
    uint? CycleSequence = null);

internal sealed record PlcCommunicationStoredRow(
    long Position,
    string? PreviousHash,
    PlcCommunicationEvent Event,
    byte[] Payload,
    string PayloadHash);

/// <summary>
/// Schema-27 append-only PLC communication ledger. The writer creates the
/// event identity and central-audit binding; callers cannot supply a position,
/// event id, or audit reference.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string PlcCommunicationSchemaSql = @"
        CREATE TABLE plc_communication_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE plc_communication_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            EndpointBindingHash TEXT NOT NULL CHECK(length(EndpointBindingHash)=64),
            ProfileHash TEXT NOT NULL CHECK(length(ProfileHash)=64),
            PolicyHash TEXT NOT NULL CHECK(length(PolicyHash)=64),
            Generation INTEGER NOT NULL CHECK(Generation>=0),
            Attempt INTEGER NOT NULL CHECK(Attempt>=0),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 12),
            ReasonCode TEXT NOT NULL,
            ControllerEpoch INTEGER NULL CHECK(ControllerEpoch IS NULL OR ControllerEpoch>=0),
            ObservedAtUtc TEXT NOT NULL,
            MonotonicTimestamp INTEGER NOT NULL CHECK(MonotonicTimestamp>0),
            RecoveryCycleId TEXT NULL CHECK(RecoveryCycleId IS NULL OR length(RecoveryCycleId)=36),
            QualificationSessionId TEXT NULL CHECK(QualificationSessionId IS NULL OR length(QualificationSessionId)=36),
            RunId TEXT NULL CHECK(RunId IS NULL OR length(RunId)=36),
            CycleSequence INTEGER NULL CHECK(CycleSequence IS NULL OR CycleSequence>=0),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE INDEX ix_plc_communication_generation
            ON plc_communication_events(RuntimeEpoch,Generation,Attempt,Position);
        CREATE INDEX ix_plc_communication_recovery
            ON plc_communication_events(RecoveryCycleId,Position);
        CREATE INDEX ix_plc_communication_run
            ON plc_communication_events(QualificationSessionId,RunId,Position);
        CREATE TRIGGER plc_communication_config_immutable_update
            BEFORE UPDATE ON plc_communication_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcCommunicationConfiguration'); END;
        CREATE TRIGGER plc_communication_config_immutable_delete
            BEFORE DELETE ON plc_communication_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcCommunicationConfiguration'); END;
        CREATE TRIGGER plc_communication_event_immutable_update
            BEFORE UPDATE ON plc_communication_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcCommunicationEvent'); END;
        CREATE TRIGGER plc_communication_event_immutable_delete
            BEFORE DELETE ON plc_communication_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcCommunicationEvent'); END;";

    internal static void InitializePlcCommunicationSchema(sqlite3 database,
        PlcCommunicationStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        SqliteNative.Execute(database, PlcCommunicationSchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO plc_communication_store_config VALUES(1,1,?,?,?,?);", deadline,
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendPlcCommunicationStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredPlcCommunication(sqlite3 database,
        PlcCommunicationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var row = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM plc_communication_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (Format: SqliteNative.ColumnInt64(statement, 0),
                Entries: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnInt64(statement, 2),
                Total: SqliteNative.ColumnInt64(statement, 3),
                Binding: SqliteNative.ColumnText(statement, 4))).SingleOrDefault();
        AuditChainDatabase.Require(row.Format == PlcCommunicationStoreOptions.FormatVersion &&
            row.Entries == options.MaximumEntries && row.Payload == options.MaximumPayloadBytes &&
            row.Total == options.MaximumTotalBytes && row.Binding == options.BindingHash,
            "PlcCommunicationConfigurationMismatch");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='PlcCommunicationStoreActivated';",
            deadline) == 1, "PlcCommunicationActivationMissing");
    }

    internal static IReadOnlyList<PlcCommunicationStoredRow> ReadPlcCommunicationRows(
        sqlite3 database, PlcCommunicationStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredPlcCommunication(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,EventId,RuntimeEpoch,EndpointBindingHash,ProfileHash,
                PolicyHash,Generation,Attempt,Kind,ReasonCode,ControllerEpoch,ObservedAtUtc,
                MonotonicTimestamp,RecoveryCycleId,QualificationSessionId,RunId,CycleSequence,
                ContentHash,PayloadHash,Payload,AuditSequence,AuditHash
            FROM plc_communication_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadPlcCommunicationRow(statement, options),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "PlcCommunicationEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "PlcCommunicationTotalCapacityExceeded");
        return rows;
    }

    internal static long ReadPlcCommunicationAuditReserve(sqlite3 database,
        StoreDeadline deadline)
    {
        // Communication events are already represented by committed audit
        // rows. They do not reserve a future terminal row, so returning the
        // current count here would subtract already-consumed capacity twice.
        return 0;
    }

    internal ValueTask<StoreWriteResult> AppendPlcCommunicationEventAsync(
        PlcCommunicationWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (!PlcCommunicationEnabled)
            return ValueTask.FromException<StoreWriteResult>(
                new InvalidOperationException("PlcCommunicationConfigurationRequired"));
        ArgumentNullException.ThrowIfNull(request);
        return AppendPlcCommunicationEventCoreAsync(request, deadline, cancellationToken);
    }

    private async ValueTask<StoreWriteResult> AppendPlcCommunicationEventCoreAsync(
        PlcCommunicationWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || _queue is null || _queueSlots is null || _worker.IsCompleted)
            return new(false, "PlcCommunicationUnavailable");
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero)
            return new(false, "PlcCommunicationCommitDeadlineExceeded");
        if (!await _queueSlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "PlcCommunicationCommitDeadlineExceeded");
        var work = new PlcCommunicationWork(request);
        var queued = new WriteRequest(null, deadline, PlcCommunication: work);
        if (!_queue.Writer.TryWrite(queued))
        {
            _queueSlots.Release();
            return new(false, Volatile.Read(ref _disposed) != 0
                ? "TraceStoreDisposed" : "PlcCommunicationUnavailable");
        }
        return await queued.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendPlcCommunicationCore(sqlite3 database,
        PlcCommunicationWork work, StoreDeadline deadline)
    {
        var options = _options.PlcCommunication ??
            throw new InvalidOperationException("PlcCommunicationConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        ValidateWriteRequest(work.Request);
        if (Integrity?.State == AuditIntegrityState.Faulted)
            return new(false, Integrity.ReasonCode);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");

        var started = false;
        var committed = false;
        try
        {
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            var verification = VerifyForProtocolLedger(database, deadline);
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline, options);

            var rows = ReadPlcCommunicationRows(database, options, deadline);
            ValidateWriteRequest(work.Request, rows);
            AuditChainDatabase.Require(rows.Count < options.MaximumEntries,
                "PlcCommunicationEntryCapacityExceeded");
            var position = checked(rows.Count + 1L);
            var previousHash = rows.Count == 0 ? null : rows[^1].Event.ContentHash;
            var input = work.Request;
            var provisional = new PlcCommunicationEvent(position, previousHash, Guid.NewGuid(),
                input.RuntimeEpoch, input.EndpointBindingHash, input.ProfileHash, input.PolicyHash,
                input.Generation, input.Attempt, input.Kind, input.ReasonCode, input.ControllerEpoch,
                input.ObservedAtUtc.ToUniversalTime(), input.MonotonicTimestamp,
                input.RecoveryCycleId, input.QualificationSessionId, input.RunId, input.CycleSequence);
            var auditPayload = PlcCommunicationStorageCodec.EncodeAuditPayload(provisional);
            var audit = AuditChainDatabase.AppendPlcCommunicationLedgerEntry(database, policy, signingKey,
                position, auditPayload, options, deadline);
            var persisted = new PlcCommunicationEvent(position, previousHash, provisional.EventId,
                provisional.RuntimeEpoch, provisional.EndpointBindingHash, provisional.ProfileHash,
                provisional.PolicyHash, provisional.Generation, provisional.Attempt, provisional.Kind,
                provisional.ReasonCode, provisional.ControllerEpoch, provisional.ObservedAtUtc,
                provisional.MonotonicTimestamp, provisional.RecoveryCycleId,
                provisional.QualificationSessionId, provisional.RunId, provisional.CycleSequence,
                audit.Sequence, audit.Hash);
            var payload = PlcCommunicationStorageCodec.Encode(persisted);
            AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
                "PlcCommunicationPayloadCapacityExceeded");
            var used = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
            AuditChainDatabase.Require(checked(used + payload.Length) <= options.MaximumTotalBytes,
                "PlcCommunicationTotalCapacityExceeded");
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
            AuditChainDatabase.Execute(database, @"
                INSERT INTO plc_communication_events(
                    Position,PreviousHash,EventId,RuntimeEpoch,EndpointBindingHash,ProfileHash,
                    PolicyHash,Generation,Attempt,Kind,ReasonCode,ControllerEpoch,ObservedAtUtc,
                    MonotonicTimestamp,RecoveryCycleId,QualificationSessionId,RunId,CycleSequence,
                    ContentHash,PayloadHash,Payload,AuditSequence,AuditHash)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                position.ToString(CultureInfo.InvariantCulture), previousHash,
                persisted.EventId.ToString("D"), persisted.RuntimeEpoch.ToString("D"),
                persisted.EndpointBindingHash, persisted.ProfileHash, persisted.PolicyHash,
                persisted.Generation.ToString(CultureInfo.InvariantCulture),
                persisted.Attempt.ToString(CultureInfo.InvariantCulture),
                ((int)persisted.Kind).ToString(CultureInfo.InvariantCulture), persisted.ReasonCode,
                persisted.ControllerEpoch?.ToString(CultureInfo.InvariantCulture),
                persisted.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                persisted.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
                persisted.RecoveryCycleId?.ToString("D"), persisted.QualificationSessionId?.ToString("D"),
                persisted.RunId?.ToString("D"), persisted.CycleSequence?.ToString(CultureInfo.InvariantCulture),
                persisted.ContentHash, payloadHash, Convert.ToBase64String(payload),
                persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash!);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.EventCompletion.TrySetResult(persisted);
            Interlocked.Exchange(ref _lastCommittedAuditSequence, audit.Sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(policy, AuditIntegrityState.Verifying,
                policy.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, "PlcCommunicationPersisted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return new(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(ex, "PlcCommunicationCommitFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private static void ValidateWriteRequest(PlcCommunicationWriteRequest request)
    {
        if (request.RuntimeEpoch == Guid.Empty || request.Generation < 0 || request.Attempt < 0 ||
            request.MonotonicTimestamp <= 0 || request.ObservedAtUtc == default ||
            request.ObservedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("PlcCommunicationEventIdentityInvalid");
        if (!Enum.IsDefined(typeof(PlcCommunicationEventKind), request.Kind))
            throw new InvalidOperationException("PlcCommunicationEventKindInvalid");
        _ = QualificationContractValidation.Hash(request.EndpointBindingHash, nameof(request.EndpointBindingHash));
        _ = QualificationContractValidation.Hash(request.ProfileHash, nameof(request.ProfileHash));
        _ = QualificationContractValidation.Hash(request.PolicyHash, nameof(request.PolicyHash));
        _ = QualificationContractValidation.Reason(request.ReasonCode, nameof(request.ReasonCode));
        if (request.RecoveryCycleId == Guid.Empty || request.QualificationSessionId == Guid.Empty ||
            request.RunId == Guid.Empty)
            throw new InvalidOperationException("PlcCommunicationEventIdentityInvalid");
    }

    private static void ValidateWriteRequest(PlcCommunicationWriteRequest request,
        IReadOnlyList<PlcCommunicationStoredRow> priorRows)
    {
        ValidateWriteRequest(request);
        if (priorRows.Count == 0)
            return;

        var previous = priorRows[^1].Event;
        // Generation and attempt are scoped to one runtime epoch. A cold
        // restart gets a new epoch and may restart both counters, while events
        // within an epoch must never move backwards.
        if (previous.RuntimeEpoch != request.RuntimeEpoch)
            return;
        if (request.Generation < previous.Generation)
            throw new InvalidOperationException("PlcCommunicationGenerationRegression");
        if (request.Generation == previous.Generation && request.Attempt < previous.Attempt)
            throw new InvalidOperationException("PlcCommunicationAttemptRegression");
        if (request.MonotonicTimestamp <= previous.MonotonicTimestamp)
            throw new InvalidOperationException("PlcCommunicationMonotonicOrderInvalid");
    }

    private AuditIntegrityReport VerifyForProtocolLedger(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options;
        var request = new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries);
        return AuditChainDatabase.Verify(database, _policy, _signingKey!.KeyId,
            _signingKey.PublicKeyBase64, request, startup: true, deadline,
            validateAnchorReceipt: false, archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts, cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery, cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup, calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance, releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts, activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions, importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections, productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications, recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies, qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
                productionInspectionOptions: options.ProductionInspections);
    }

    internal static void VerifyPlcCommunicationActivationPayload(sqlite3 database,
        byte[] payload, PlcCommunicationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        options.Validate();
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "PlcCommunicationActivationPayloadMismatch");
    }

    internal static void VerifyPlcCommunicationAuditPayload(sqlite3 database, long position,
        byte[] payload, PlcCommunicationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        options.Validate();
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= options.MaximumPayloadBytes, "PlcCommunicationAuditPayloadInvalid");
        var row = ReadPlcCommunicationRows(database, options, deadline).SingleOrDefault(value =>
            value.Position == position) ?? throw new InvalidOperationException("PlcCommunicationEventMissing");
        var expected = PlcCommunicationStorageCodec.EncodeAuditPayload(row.Event);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(expected),
            "PlcCommunicationPayloadBindingMismatch");
        var central = AuditChainDatabase.Read(database, @"
            SELECT Kind,PlcCommunicationPosition,Payload,Hash,Sequence
            FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2),
                Hash: SqliteNative.ColumnText(statement, 3),
                Sequence: SqliteNative.ColumnInt64(statement, 4)),
            row.Event.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(central.Kind == "PlcCommunicationEvent" &&
            central.Position == position && central.Sequence == row.Event.AuditSequence &&
            central.Hash == row.Event.AuditHash && central.Payload == Convert.ToBase64String(payload),
            "PlcCommunicationCentralBindingMismatch");
    }

    internal static void ValidatePlcCommunicationHistory(sqlite3 database,
        PlcCommunicationStoreOptions options, StoreDeadline deadline)
    {
        var rows = ReadPlcCommunicationRows(database, options, deadline);
        string? previousContentHash = null;
        PlcCommunicationEvent? previous = null;
        foreach (var row in rows)
        {
            var value = row.Event;
            AuditChainDatabase.Require(value.Position == row.Position &&
                value.PreviousHash == previousContentHash,
                "PlcCommunicationLedgerLinkMismatch");
            if (previous is not null && previous.RuntimeEpoch == value.RuntimeEpoch)
            {
                AuditChainDatabase.Require(value.Generation >= previous.Generation,
                    "PlcCommunicationGenerationRegression");
                AuditChainDatabase.Require(value.Generation != previous.Generation ||
                    value.Attempt >= previous.Attempt, "PlcCommunicationAttemptRegression");
                AuditChainDatabase.Require(value.MonotonicTimestamp > previous.MonotonicTimestamp,
                    "PlcCommunicationMonotonicOrderInvalid");
            }
            var auditPayload = PlcCommunicationStorageCodec.EncodeAuditPayload(value);
            var central = AuditChainDatabase.Read(database, @"
                SELECT Kind,PlcCommunicationPosition,Hash,Payload
                FROM audit_entries WHERE Sequence=? LIMIT 1;", deadline,
                statement => (Kind: SqliteNative.ColumnText(statement, 0),
                    Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                    Hash: SqliteNative.ColumnText(statement, 2),
                    Payload: SqliteNative.ColumnText(statement, 3)),
                value.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(central.Kind == "PlcCommunicationEvent" &&
                central.Position == value.Position && central.Hash == value.AuditHash &&
                central.Payload == Convert.ToBase64String(auditPayload),
                "PlcCommunicationCentralBindingMismatch");
            previousContentHash = value.ContentHash;
            previous = value;
        }
    }

    private static PlcCommunicationStoredRow ReadPlcCommunicationRow(sqlite3_stmt statement,
        PlcCommunicationStoreOptions options)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var previous = SqliteNative.ColumnText(statement, 1);
        var eventId = ParsePlcGuid(SqliteNative.ColumnText(statement, 2));
        var runtime = ParsePlcGuid(SqliteNative.ColumnText(statement, 3));
        var endpoint = SqliteNative.ColumnText(statement, 4)!;
        var profile = SqliteNative.ColumnText(statement, 5)!;
        var policy = SqliteNative.ColumnText(statement, 6)!;
        var generation = SqliteNative.ColumnInt64(statement, 7);
        var attempt = checked((int)SqliteNative.ColumnInt64(statement, 8));
        var kind = (PlcCommunicationEventKind)checked((byte)SqliteNative.ColumnInt64(statement, 9));
        var reason = SqliteNative.ColumnText(statement, 10)!;
        var epoch = SqliteNative.ColumnInt64Nullable(statement, 11);
        var observed = ParsePlcTime(SqliteNative.ColumnText(statement, 12));
        var monotonic = SqliteNative.ColumnInt64(statement, 13);
        var recovery = ParseNullablePlcGuid(SqliteNative.ColumnText(statement, 14));
        var session = ParseNullablePlcGuid(SqliteNative.ColumnText(statement, 15));
        var run = ParseNullablePlcGuid(SqliteNative.ColumnText(statement, 16));
        var cycle = SqliteNative.ColumnInt64Nullable(statement, 17);
        var content = SqliteNative.ColumnText(statement, 18)!;
        var payloadHash = SqliteNative.ColumnText(statement, 19)!;
        var encoded = SqliteNative.ColumnText(statement, 20)!;
        var auditSequence = SqliteNative.ColumnInt64(statement, 21);
        var auditHash = SqliteNative.ColumnText(statement, 22)!;
        AuditChainDatabase.Require(encoded.Length <= options.MaximumPayloadBytes * 2 &&
            AuditCanonical.IsHash(content) && AuditCanonical.IsHash(payloadHash) &&
            AuditCanonical.IsHash(auditHash), "PlcCommunicationEventInvalid");
        var payload = Convert.FromBase64String(encoded);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes &&
            Convert.ToHexString(SHA256.HashData(payload)) == payloadHash,
            "PlcCommunicationPayloadInvalid");
        var value = new PlcCommunicationEvent(position, previous, eventId, runtime, endpoint, profile,
            policy, generation, attempt, kind, reason,
            epoch is long epochValueForEvent ? checked((uint)epochValueForEvent) : null,
            observed, monotonic, recovery, session, run,
            cycle is long cycleValueForEvent ? checked((uint)cycleValueForEvent) : null,
            auditSequence, auditHash, content);
        AuditChainDatabase.Require(value.Position == position && value.PreviousHash == previous &&
            value.SystemPrincipalId == SystemPrincipalId.PlcAdapter &&
            value.EventId == eventId && value.RuntimeEpoch == runtime && value.EndpointBindingHash == endpoint &&
            value.ProfileHash == profile && value.PolicyHash == policy && value.Generation == generation &&
            value.Attempt == attempt && value.Kind == kind && value.ReasonCode == reason &&
            (value.ControllerEpoch is null ? epoch is null : epoch is long epochValue &&
                value.ControllerEpoch == checked((uint)epochValue)) && value.ObservedAtUtc == observed &&
            value.MonotonicTimestamp == monotonic && value.RecoveryCycleId == recovery &&
            value.QualificationSessionId == session && value.RunId == run &&
            (value.CycleSequence is null ? cycle is null : cycle is long cycleValue &&
                value.CycleSequence == checked((uint)cycleValue)) && value.ContentHash == content &&
            value.AuditSequence == auditSequence && value.AuditHash == auditHash,
            "PlcCommunicationEventBindingMismatch");
        AuditChainDatabase.Require(PlcCommunicationStorageCodec.Encode(value).AsSpan().SequenceEqual(payload),
            "PlcCommunicationPayloadBindingMismatch");
        return new(position, previous, value, payload, payloadHash);
    }

    private static Guid ParsePlcGuid(string? value) => Guid.TryParse(value, out var parsed) &&
        parsed != Guid.Empty ? parsed : throw new InvalidOperationException("PlcCommunicationEventInvalid");

    private static Guid? ParseNullablePlcGuid(string? value) => string.IsNullOrEmpty(value) ? null : ParsePlcGuid(value);

    private static DateTimeOffset ParsePlcTime(string? value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        ? parsed : throw new InvalidOperationException("PlcCommunicationEventInvalid");
}

internal sealed class PlcCommunicationWork
{
    internal PlcCommunicationWork(PlcCommunicationWriteRequest request) => Request = request;
    internal PlcCommunicationWriteRequest Request { get; }
    internal TaskCompletionSource<PlcCommunicationEvent> EventCompletion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class PlcCommunicationStorageCodec
{
    internal static byte[] EncodeAuditPayload(PlcCommunicationEvent value) => AuditCanonical.Encode(
        "PlcCommunicationEventAuditV1", value.Position.ToString(CultureInfo.InvariantCulture),
        value.SystemPrincipalId, value.PreviousHash, value.EventId.ToString("D"), value.RuntimeEpoch.ToString("D"),
        value.EndpointBindingHash, value.ProfileHash, value.PolicyHash,
        value.Generation.ToString(CultureInfo.InvariantCulture), value.Attempt.ToString(CultureInfo.InvariantCulture),
        ((int)value.Kind).ToString(CultureInfo.InvariantCulture), value.ReasonCode,
        value.ControllerEpoch?.ToString(CultureInfo.InvariantCulture),
        value.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        value.MonotonicTimestamp.ToString(CultureInfo.InvariantCulture), value.RecoveryCycleId?.ToString("D"),
        value.QualificationSessionId?.ToString("D"), value.RunId?.ToString("D"),
        value.CycleSequence?.ToString(CultureInfo.InvariantCulture));

    internal static byte[] Encode(PlcCommunicationEvent value) => AuditCanonical.Encode(
        "PlcCommunicationEventStorageV1", Convert.ToBase64String(EncodeAuditPayload(value)),
        value.ContentHash, value.AuditSequence.ToString(CultureInfo.InvariantCulture), value.AuditHash);

}
