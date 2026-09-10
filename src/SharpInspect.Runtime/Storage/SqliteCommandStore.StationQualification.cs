using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Persistence for the schema-23 station qualification evidence ledger.  The
/// ledger is deliberately independent from Manual Inspection and production
/// facts; its payload is the authority and the scalar columns are checked
/// indexes only.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    // Internal guard composition keeps the actual identity authorization and
    // writer-thread lease. It cannot replace the request or mint authority.
    internal Func<IIdentityTransactionGuard, IIdentityTransactionGuard>?
        QualificationCoreCommitGuardDecorator { get; set; }

    internal const string StationQualificationStoreActivatedKind = "StationQualificationStoreActivated";
    internal const string StationQualificationEventKind = "StationQualificationEvent";

    internal const string StationQualificationSchemaSql = @"
        CREATE TABLE station_qualification_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE station_qualification_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            StartCorrelationId TEXT NOT NULL CHECK(length(StartCorrelationId)=36),
            StartAttemptId TEXT NOT NULL CHECK(length(StartAttemptId)=36),
            CommandCorrelationId TEXT NOT NULL CHECK(length(CommandCorrelationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            CommandKind INTEGER NOT NULL CHECK(CommandKind IN (45,46)),
            Phase INTEGER NOT NULL CHECK(Phase IN (1,2,3,4,5,6,7,8)),
            Restoration INTEGER NOT NULL CHECK(Restoration IN (1,2,3,4)),
            Terminal INTEGER NOT NULL CHECK(Terminal IN (0,1)),
            RunId TEXT NULL CHECK(RunId IS NULL OR length(RunId)=36),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            ActorAuthorizationRevision INTEGER NOT NULL CHECK(ActorAuthorizationRevision>=0),
            AuthorizationTarget TEXT NOT NULL CHECK(length(AuthorizationTarget)=64),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0),
            RecordedAtUtc TEXT NOT NULL,
            HeaderContentHash TEXT NOT NULL CHECK(length(HeaderContentHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandAuditSequence INTEGER NULL CHECK(CommandAuditSequence IS NULL OR CommandAuditSequence>0),
            CommandAuditHash TEXT NULL CHECK(CommandAuditHash IS NULL OR length(CommandAuditHash)=64),
            AuthorizationAuditSequence INTEGER NULL CHECK(AuthorizationAuditSequence IS NULL OR AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NULL CHECK(AuthorizationAuditHash IS NULL OR length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64),
            CommandAuthorizationTarget TEXT NOT NULL CHECK(length(CommandAuthorizationTarget)=64),
            UNIQUE(SessionId,Position));
        CREATE INDEX ix_station_qualification_events_session ON station_qualification_events(SessionId,Position);
        CREATE INDEX ix_station_qualification_events_run ON station_qualification_events(RunId,Position);
        CREATE INDEX ix_station_qualification_events_audit ON station_qualification_events(AuditSequence);
        CREATE TRIGGER station_qualification_config_immutable_update BEFORE UPDATE
            ON station_qualification_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableStationQualificationConfiguration');
        END;
        CREATE TRIGGER station_qualification_config_immutable_delete BEFORE DELETE
            ON station_qualification_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableStationQualificationConfiguration');
        END;
        CREATE TRIGGER station_qualification_event_immutable_update BEFORE UPDATE
            ON station_qualification_events BEGIN
            SELECT RAISE(ABORT,'ImmutableStationQualificationEvent');
        END;
        CREATE TRIGGER station_qualification_event_immutable_delete BEFORE DELETE
            ON station_qualification_events BEGIN
            SELECT RAISE(ABORT,'ImmutableStationQualificationEvent');
        END;";

    internal static void InitializeStationQualificationSchema(sqlite3 database,
        StationQualificationStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, StationQualificationSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO station_qualification_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            StationQualificationStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendStationQualificationStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredStationQualifications(sqlite3 database,
        StationQualificationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM station_qualification_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4) ?? string.Empty)).ToArray();
        AuditChainDatabase.Require(configured.Length == 1 &&
            configured[0].FormatVersion == StationQualificationStoreOptions.FormatVersion &&
            configured[0].MaximumEntries == options.MaximumEntries &&
            configured[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configured[0].BindingHash == options.BindingHash,
            "StationQualificationConfigurationMismatch");
    }

    internal static void VerifyStationQualificationActivationPayload(sqlite3 database,
        byte[] payload, StationQualificationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "StationQualificationActivationBindingMismatch");
        RequireConfiguredStationQualifications(database, options, deadline);
    }

    internal sealed record StationQualificationStoredRow(long Position, string? PreviousHash,
        byte[] Payload, StationQualificationSessionEvent Event);

    internal static List<StationQualificationStoredRow> ReadStationQualificationRows(
        sqlite3 database, StationQualificationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredStationQualifications(database, options, deadline);
        long decodedTotal = 0;
        var profileResolver = ReadStationQualificationProfileResolver(database, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,SessionId,RuntimeEpoch,StartCorrelationId,StartAttemptId,
                CommandCorrelationId,AttemptId,CommandKind,Phase,Restoration,Terminal,RunId,
                ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,AuthorizationTarget,
                ReasonCode,RecordedAtUtc,HeaderContentHash,ContentHash,PayloadHash,Payload,
                CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash,AuditSequence,AuditHash,CommandAuthorizationTarget
            FROM station_qualification_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var previous = SqliteNative.ColumnText(statement, 1);
            var encoded = SqliteNative.ColumnText(statement, 22);
            AuditChainDatabase.Require(encoded is { Length: > 0 } &&
                encoded.Length <= checked(options.MaximumPayloadBytes * 2),
                "StationQualificationPayloadCapacityExceeded");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded!);
                AuditChainDatabase.Require(Convert.ToBase64String(payload) == encoded,
                    "StationQualificationPayloadCanonicalMismatch");
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("StationQualificationPayloadInvalid", exception); }
            AuditChainDatabase.Require(payload.Length is > 0 && payload.Length <= options.MaximumPayloadBytes,
                "StationQualificationPayloadCapacityExceeded");
            decodedTotal = checked(decodedTotal + payload.Length);
            AuditChainDatabase.Require(decodedTotal <= options.MaximumTotalBytes,
                "StationQualificationTotalCapacityExceeded");
            var value = StationQualificationStorageCodec.Decode(payload, profileResolver);
            AuditChainDatabase.Require(value.Position == position &&
                value.PreviousHash == previous &&
                value.SessionId == ParseStationGuid(SqliteNative.ColumnText(statement, 2)) &&
                value.RuntimeEpoch == ParseStationGuid(SqliteNative.ColumnText(statement, 3)) &&
                value.Header.StartCorrelationId == ParseStationGuid(SqliteNative.ColumnText(statement, 4)) &&
                value.Header.StartAttemptId == ParseStationGuid(SqliteNative.ColumnText(statement, 5)) &&
                value.CommandCorrelationId == ParseStationGuid(SqliteNative.ColumnText(statement, 6)) &&
                value.AttemptId == ParseStationGuid(SqliteNative.ColumnText(statement, 7)) &&
                (int)value.CommandKind == SqliteNative.ColumnInt64(statement, 8) &&
                (int)value.Phase == SqliteNative.ColumnInt64(statement, 9) &&
                (int)value.Restoration == SqliteNative.ColumnInt64(statement, 10) &&
                (value.Terminal ? 1 : 0) == SqliteNative.ColumnInt64(statement, 11) &&
                (value.Run?.RunId.Value.ToString("D") == SqliteNative.ColumnText(statement, 12)) &&
                value.Header.ActorPrincipalId == ParseStationGuid(SqliteNative.ColumnText(statement, 13)) &&
                value.Header.ActorSessionId == ParseStationGuid(SqliteNative.ColumnText(statement, 14)) &&
                value.Header.ActorAuthorizationRevision == SqliteNative.ColumnInt64(statement, 15) &&
                value.Header.AuthorizationTarget == SqliteNative.ColumnText(statement, 16) &&
                value.ReasonCode == SqliteNative.ColumnText(statement, 17) &&
                value.RecordedAtUtc == ParseStationTime(SqliteNative.ColumnText(statement, 18)) &&
                value.Header.ContentHash == SqliteNative.ColumnText(statement, 19) &&
                value.ContentHash == SqliteNative.ColumnText(statement, 20) &&
                StationQualificationStorageCodec.PayloadHash(EncodeStationQualificationAuditPayload(value)) ==
                    SqliteNative.ColumnText(statement, 21) &&
                value.CommandAuditSequence == SqliteNative.ColumnInt64Nullable(statement, 23) &&
                value.CommandAuditHash == SqliteNative.ColumnText(statement, 24) &&
                value.AuthorizationAuditSequence == SqliteNative.ColumnInt64Nullable(statement, 25) &&
                value.AuthorizationAuditHash == SqliteNative.ColumnText(statement, 26) &&
                value.AuditSequence == SqliteNative.ColumnInt64(statement, 27) &&
                value.AuditHash == SqliteNative.ColumnText(statement, 28) &&
                value.CommandAuthorizationTarget == SqliteNative.ColumnText(statement, 29),
                "StationQualificationEventColumnBindingMismatch");
            return new StationQualificationStoredRow(position, previous, payload, value);
        }).ToList();
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "StationQualificationEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "StationQualificationTotalCapacityExceeded");
        string? previousHash = null;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.PreviousHash == previousHash,
                "StationQualificationPreviousHashMismatch");
            // PreviousHash is the qualification ledger's own immutable event
            // chain.  AuditHash belongs to the central chain and is bound by
            // the event reference columns separately.
            previousHash = row.Event.ContentHash;
        }
        return rows;
    }

    internal static void VerifyStationQualificationAuditPayload(sqlite3 database, long position,
        byte[] auditPayload, StationQualificationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditPayload);
        var row = ReadStationQualificationRows(database, options, deadline)
            .SingleOrDefault(value => value.Position == position);
        AuditChainDatabase.Require(row is not null, "StationQualificationAuditBindingMissing");
        var expected = EncodeStationQualificationAuditPayload(row!.Event);
        AuditChainDatabase.Require(expected.SequenceEqual(auditPayload) &&
            StationQualificationStorageCodec.PayloadHash(auditPayload) ==
            StationQualificationStorageCodec.PayloadHash(expected),
            "StationQualificationAuditBindingMismatch");
    }

    internal static long ReadStationQualificationAuditReserve(sqlite3 database,
        StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "station_qualification_store_config", deadline) ||
            !AuditChainDatabase.TableExists(database, "station_qualification_events", deadline))
            return 0;
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM station_qualification_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => new StationQualificationStoreOptions
            {
                MaximumEntries = checked((int)SqliteNative.ColumnInt64(statement, 1)),
                MaximumPayloadBytes = checked((int)SqliteNative.ColumnInt64(statement, 2)),
                MaximumTotalBytes = SqliteNative.ColumnInt64(statement, 3)
            }).SingleOrDefault();
        if (configured is null) return 0;
        var rows = ReadStationQualificationRows(database, configured, deadline);
        return ReadStationQualificationAuditReserveAfter(rows, null);
    }

    internal static long ReadStationQualificationAuditReserveAfter(
        IReadOnlyList<StationQualificationStoredRow> rows,
        StationQualificationSessionEvent? candidate)
    {
        return checked(ReadStationQualificationFutureRowsAfter(rows, candidate) *
            StationQualificationStoreOptions.AuditEntriesPerEvent);
    }

    internal static long ReadStationQualificationFutureRowsAfter(
        IReadOnlyList<StationQualificationStoredRow> rows,
        StationQualificationSessionEvent? candidate)
    {
        var events = rows.Select(value => value.Event).ToList();
        if (candidate is not null)
            events.Add(candidate);
        var latest = events
            .GroupBy(value => value.SessionId)
            .Select(group =>
            {
                var ordered = group.OrderBy(value => value.Position).ToArray();
                return (Last: ordered[^1], PendingRun: ordered.Where(value => value.Run is not null)
                    .GroupBy(value => value.Run!.RunId.Value)
                    .Any(run => !run.Last().Run!.Completed),
                    ExitAccepted: ordered.Any(value => value.CommandKind ==
                        AuditedCommandKind.ExitStationQualificationSession));
            })
            .ToArray();
        // The first accepted Exit has its own identity, command and ledger
        // footprint before restoration can spend the physical recovery tail.
        // Reserve that row until it is accepted, including during a pending
        // run or blocked recovery; unrelated writes cannot consume it.
        return checked(latest.Where(value => !value.Last.Terminal)
            .Sum(value => checked(StationQualificationFutureRows(value.Last, value.PendingRun) +
                (value.ExitAccepted ? 0 : 1))));
    }

    /// <summary>
    /// Rows that must remain available after an accepted event.  Qualification
    /// can enter a blocked recovery path after the physical facility has been
    /// opened, so the reservation includes a run retirement (when applicable),
    /// a recovery-block/restoring transition, and the final Closed row.  It is
    /// intentionally conservative; a bounded store must reject admission
    /// before writing when this tail cannot fit.
    /// </summary>
    internal static long StationQualificationFutureRows(StationQualificationSessionEvent value) =>
        StationQualificationFutureRows(value, pendingRun: value.Run is { Completed: false });

    private static long StationQualificationFutureRows(StationQualificationSessionEvent value,
        bool pendingRun)
    {
        if (value.Terminal) return 0;
        // Admission must reserve the complete physical retirement tail before
        // opening the facility.  A later Exit event often has no embedded Run,
        // so the reserve calculation above carries the pending-run state from
        // all rows in this session rather than trusting only the candidate row.
        if (value.Phase == StationQualificationSessionPhase.RecoveryBlocked)
            return 5; // new attempt, optional run cancellation, two readbacks, Closed
        if (value.Phase == StationQualificationSessionPhase.Restoring)
        {
            var remaining = value.ReasonCode switch
            {
                "StationQualificationTargetRestored" => 2,
                "StationQualificationTargetReadbackVerified" => 1,
                _ => 3 // recovery-attempt row, or an equivalent early Restoring row
            };
            return checked(remaining + (pendingRun ? 1 : 0));
        }
        return checked(4 + (pendingRun ? 1 : 0));
    }

    private sealed class StationQualificationWriteContext
    {
        private long _reserve;
        internal StationQualificationWriteContext(long reserve) => _reserve = reserve;
        internal long TakeReserve()
        {
            AuditChainDatabase.Require(_reserve >= 0, "StationQualificationAuditReservationInvalid");
            return _reserve--;
        }
    }

    private StationQualificationWriteContext? PrepareStationQualificationWrite(
        sqlite3 database, IdentityUpdate update, StoreDeadline deadline)
    {
        if (update.Result is not StationQualificationTransactionResult result ||
            !result.Accepted || result.Event is null)
            return null;
        var writes = checked(update.Events.Count + (update.CommandFacts?.Count ?? 0) + 1);
        var reserve = AuditChainDatabase.EnsureStationQualificationTransactionCapacity(database,
            _policy!, writes, result.Event, deadline);
        return new StationQualificationWriteContext(reserve);
    }

    /// <summary>
    /// Reads the sealed qualification recovery boundary from the same verified
    /// SQLite snapshot used by the startup coordinator.  The result is derived
    /// from the last event of each session; callers cannot select an arbitrary
    /// header or replace a pending session id.
    /// </summary>
    internal async ValueTask<StationQualificationRecoveryState> ReadStationQualificationRecoveryStateAsync(
        CancellationToken cancellationToken = default)
    {
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        var options = _options.StationQualifications;
        if (!initialized.Committed || options is null || _policy is null || _signingKey is null)
            return new(false, initialized.ReasonCode is { Length: > 0 } reason ? reason :
                "StationQualificationStoreUnavailable");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(false, pathReason);
        try
        {
            return await Task.Run(() => ReadStationQualificationRecoveryStateCore(path!, options,
                cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, SqliteAuditIntegrityQuery.FaultReason(exception,
                "StationQualificationRecoveryUnavailable"));
        }
    }

    private StationQualificationRecoveryState ReadStationQualificationRecoveryStateCore(
        string path, StationQualificationStoreOptions options, CancellationToken cancellationToken)
    {
        using var connection = SqliteNative.Open(path, readOnly: true);
        var database = connection.Handle!;
        SqliteNative.ConfigureSqliteLimit(database, _options);
        StationQualificationStoreOptions.ConfigureSqliteLimit(database);
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        var committed = false;
        try
        {
            var schema = checked((int)AuditChainDatabase.Scalar(database,
                "PRAGMA user_version;", deadline));
            AuditChainDatabase.Require(schema is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion,
                schema < PlcCommunicationStoreOptions.SchemaVersion
                    ? "StationQualificationGovernedMigrationRequired" : "StoreSchemaTooNew");
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries),
                startup: true, deadline, validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup, calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance, releaseOptions: _options.RecipeReleases,
                contractOptions: _options.PlcResultContracts, activationOptions: _options.RecipeActivations,
                previewOptions: _options.PreviewSessions, importOptions: _options.CalibrationImports,
                manualOptions: _options.ManualInspections,
                productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: options,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles,
                plcCommunicationOptions: _options.PlcCommunication);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
        if (_options.PlcCommunication is not null)
            AuditChainDatabase.RequireFullPlcCommunicationVerification(database, verification, deadline,
                _options.PlcCommunication);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
            if (_options.AlarmPolicy is not null)
                AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null)
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (_options.RecipeDrafts is not null)
                AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                    _options.RecipeDrafts);
            if (_options.CameraSetup is not null)
                AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                    _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                    _options.CameraRecovery);
            if (_options.CameraNetwork is not null)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                    _options.CameraNetwork);
            if (_options.ImagingSetup is not null)
                AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                    _options.ImagingSetup);
            if (_options.CalibrationGovernance is not null)
                AuditChainDatabase.RequireFullCalibrationGovernanceVerification(database, verification, deadline,
                    _options.CalibrationGovernance);
            if (_options.RecipeReleases is not null)
                AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                    _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (_options.PlcResultContracts is not null)
                AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                    _options.PlcResultContracts);
            if (_options.RecipeActivations is not null)
                AuditChainDatabase.RequireFullRecipeActivationVerification(database, verification, deadline,
                    _options.RecipeActivations, _options.RecipeReleases, _options.PlcResultContracts,
                    _options.CalibrationGovernance);
            if (_options.PreviewSessions is not null)
                AuditChainDatabase.RequireFullPreviewSessionVerification(database, verification, deadline,
                    _options.PreviewSessions);
            if (_options.CalibrationImports is not null)
                AuditChainDatabase.RequireFullCalibrationImportVerification(database, verification, deadline,
                    _options.CalibrationImports);
            if (_options.ManualInspections is not null)
                AuditChainDatabase.RequireFullManualInspectionVerification(database, verification, deadline,
                    _options.ManualInspections);
            if (_options.ProductionAdmission is not null)
                AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                    _options.ProductionAdmission);
            AuditChainDatabase.RequireFullStationQualificationVerification(database, verification, deadline,
                options);

            var rows = ReadStationQualificationRows(database, options, deadline);
            var latest = rows.Select(value => value.Event).GroupBy(value => value.SessionId)
                .Select(group => group.OrderBy(value => value.Position).Last())
                .Where(value => !value.Terminal ||
                    value.Phase == StationQualificationSessionPhase.RecoveryBlocked ||
                    value.Restoration == StationQualificationRestorationState.RecoveryBlocked)
                .OrderBy(value => value.Position).LastOrDefault();
            if (latest is null)
            {
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                committed = true;
                return new(true, "StationQualificationIdle");
            }

            var sessionEvents = rows.Where(value => value.Event.SessionId == latest.SessionId)
                .Select(value => value.Event).OrderBy(value => value.Position).ToArray();
            var start = sessionEvents.FirstOrDefault(value =>
                value.CommandKind == AuditedCommandKind.StartStationQualificationSession);
            AuditChainDatabase.Require(start is not null && start.CommandAuditSequence is > 0,
                "StationQualificationStartAuditMissing");
            var startFact = ReadFact(database, start!.AttemptId, 1, deadline);
            AuditChainDatabase.Require(startFact is not null &&
                startFact.Disposition == CommandDisposition.Accepted &&
                startFact.CommandKind == AuditedCommandKind.StartStationQualificationSession &&
                startFact.CorrelationId == start.CommandCorrelationId &&
                startFact.AttemptId == start.AttemptId,
                "StationQualificationStartCommandMissing");
            var pendingFacts = ReadStationQualificationPendingCommandFacts(database,
                sessionEvents, deadline);
            var recoveryRequired = latest.Phase == StationQualificationSessionPhase.RecoveryBlocked ||
                latest.Restoration == StationQualificationRestorationState.RecoveryBlocked;
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            committed = true;
            return new(true, recoveryRequired ? "StationQualificationRecoveryPending" :
                "StationQualificationPending", latest, latest.Header, startFact,
                latest.Header.TargetBaseline, sessionEvents,
                sessionEvents.Where(value => value.Run is not null).Select(value => value.Run!).ToArray(),
                pendingFacts, RecoveryRequired: recoveryRequired, RecoverablePending: true);
        }
        finally
        {
            if (!committed)
                try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
        }
    }

    private static IReadOnlyList<CommandAuditFact> ReadStationQualificationPendingCommandFacts(
        sqlite3 database, IReadOnlyList<StationQualificationSessionEvent> events,
        StoreDeadline deadline)
    {
        var facts = new Dictionary<Guid, CommandAuditFact>();
        foreach (var value in events)
        {
            if (value.CommandAuditSequence is not > 0) continue;
            var fact = ReadFact(database, value.AttemptId, 1, deadline);
            if (fact is { Disposition: CommandDisposition.Accepted } &&
                fact.CommandKind == value.CommandKind &&
                fact.CorrelationId == value.CommandCorrelationId &&
                ReadFact(database, fact.AttemptId, 2, deadline) is null)
                facts[fact.EventId] = fact;
        }
        return facts.Values.OrderBy(value => value.OccurredAtUtc).ToArray();
    }

    /// <summary>Removes the central chain reference before calculating the row's
    /// central-audit payload.  This avoids a hash fixed point.</summary>
    internal static byte[] EncodeStationQualificationAuditPayload(
        StationQualificationSessionEvent value)
    {
        var withoutAudit = new StationQualificationSessionEvent(value.Position, value.PreviousHash,
            value.Header, value.CommandCorrelationId, value.AttemptId, value.CommandKind,
            value.Phase, value.Restoration, value.ReasonCode, value.Terminal, value.RecordedAtUtc,
            value.Observation, value.Run, value.CommandAuditSequence, value.CommandAuditHash,
            value.AuthorizationAuditSequence, value.AuthorizationAuditHash, 0, null,
            value.RecoveryAttempt, value.CommandAuthorizationTarget);
        return StationQualificationStorageCodec.Encode(withoutAudit);
    }

    internal ValueTask<StationQualificationTransactionResult> AppendStationQualificationProgressAsync(
        StationQualificationProgressRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!StationQualificationEnabled)
            return ValueTask.FromResult(StationQualificationUnavailable(request,
                "StationQualificationConfigurationRequired"));
        if (request.AuthorizeProgress is null && !IsStationQualificationCleanup(request))
            return ValueTask.FromResult(StationQualificationUnavailable(request,
                "StationQualificationProgressAuthorizationRequired"));
        var work = new StationQualificationProgressWork(request);
        var queued = EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                StationQualificationProgress: work), deadline, cancellationToken,
            "StationQualificationUnavailable", "StationQualificationCommitDeadlineExceeded");
        return AwaitStationQualificationProgressAsync(queued, work, request);
    }

    private static async ValueTask<StationQualificationTransactionResult> AwaitStationQualificationProgressAsync(
        ValueTask<StoreWriteResult> queued, StationQualificationProgressWork work,
        StationQualificationProgressRequest request)
    {
        try
        {
            var result = await queued.ConfigureAwait(false);
            return work.Result ?? StationQualificationUnavailable(request, result.ReasonCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return StationQualificationUnavailable(request, "StationQualificationUnavailable"); }
    }

    private static StationQualificationTransactionResult StationQualificationUnavailable(
        StationQualificationProgressRequest request, string reason) =>
        new(new(request.CommandCorrelationId, CommandDisposition.Rejected, reason,
            AuditPersistence.Unavailable, request.AttemptId), request.Header, null, false,
            request.ExpectedLastPosition, request.ExpectedLastHash, request.CommandFact);

    /// <summary>
    /// A qualification ledger row may retire the original Start responsibility
    /// and, for an explicit Exit, the Exit responsibility in the same SQLite
    /// transaction. Progress deliberately reuses the accepted command fact;
    /// only a terminal Closed row consumes aggregate-sequence-2 facts.
    /// </summary>
    private void AppendStationQualificationTerminalFacts(sqlite3 database,
        StationQualificationProgressRequest request, StationQualificationSessionEvent persisted,
        StationQualificationWriteContext capacity, StoreDeadline deadline)
    {
        if (!persisted.Terminal) return;
        AuditChainDatabase.Require(persisted.Terminal &&
            persisted.Phase == StationQualificationSessionPhase.Closed &&
            persisted.Restoration == StationQualificationRestorationState.Restored,
            "StationQualificationTerminalFactPhaseInvalid");

        var accepted = new List<CommandAuditFact>(2);
        // Terminal obligations are derived from the sealed session, never
        // optional completion flags supplied by a progress caller.
        {
            var start = ReadFact(database, request.Header.StartAttemptId, 1, deadline);
            AuditChainDatabase.Require(start is { Phase: CommandAuditPhase.Outcome,
                Disposition: CommandDisposition.Accepted } &&
                start.CommandKind == AuditedCommandKind.StartStationQualificationSession &&
                start.CorrelationId == request.Header.StartCorrelationId &&
                start.AttemptId == request.Header.StartAttemptId,
                "StationQualificationStartCommandMissing");
            accepted.Add(start!);
        }
        if (persisted.CommandKind == AuditedCommandKind.ExitStationQualificationSession)
        {
            var current = ReadFact(database, request.AttemptId, 1, deadline);
            AuditChainDatabase.Require(current is { Phase: CommandAuditPhase.Outcome,
                Disposition: CommandDisposition.Accepted } &&
                current.CommandKind == request.CommandKind &&
                current.CorrelationId == request.CommandCorrelationId &&
                current.AttemptId == request.AttemptId,
                "StationQualificationCommandMissing");
            accepted.Add(current!);
        }

        foreach (var fact in accepted.GroupBy(value => value.AttemptId).Select(group => group.First()))
        {
            var existing = ReadFact(database, fact.AttemptId, 2, deadline);
            if (existing is not null)
            {
                AuditChainDatabase.Require(existing.Phase == CommandAuditPhase.Completed &&
                    existing.Disposition is null && existing.ReasonCode == persisted.ReasonCode &&
                    SameStationQualificationFactContext(fact, existing),
                    "StationQualificationCommandTerminalConflict");
                continue;
            }
            var terminal = fact with
            {
                EventId = Guid.NewGuid(),
                OccurredAtUtc = persisted.RecordedAtUtc,
                Phase = CommandAuditPhase.Completed,
                Disposition = null,
                ReasonCode = persisted.ReasonCode
            };
            ValidateFact(terminal);
            InsertFact(database, terminal, aggregateSequence: 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, terminal.EventId,
                deadline, stationQualificationReserveOverride: capacity.TakeReserve());
        }
    }

    private static int StationQualificationTerminalFactCount(
        StationQualificationProgressRequest request) =>
        !request.Terminal ? 0 : request.CommandKind ==
            AuditedCommandKind.ExitStationQualificationSession ? 2 : 1;

    private static bool SameStationQualificationFactContext(CommandAuditFact accepted,
        CommandAuditFact terminal) => terminal.AttemptId == accepted.AttemptId &&
        terminal.CorrelationId == accepted.CorrelationId && terminal.RuntimeEpoch == accepted.RuntimeEpoch &&
        terminal.CommandKind == accepted.CommandKind && terminal.Source == accepted.Source &&
        terminal.ClaimedPrincipalId == accepted.ClaimedPrincipalId &&
        terminal.ClaimedSessionId == accepted.ClaimedSessionId &&
        terminal.ClaimedStepUpGrantId == accepted.ClaimedStepUpGrantId &&
        terminal.AuthenticatedHumanPrincipalId == accepted.AuthenticatedHumanPrincipalId;

    private StoreWriteResult AppendStationQualificationProgressCore(sqlite3 database,
        StationQualificationProgressWork work, StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified)
            return new(false, Integrity?.ReasonCode ?? "StationQualificationAuditUnavailable",
                RetryAfterIntegrityRecheck: Integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");
        var options = _options.StationQualifications ??
            throw new InvalidOperationException("StationQualificationConfigurationRequired");
        options.Validate();
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        var committed = false;
        IIdentityTransactionGuard? progressGuard = null;
        try
        {
            VerifyStationQualificationWriterAuthority(database, deadline);
            if (work.Request.AuthorizeProgress is { } authorizeProgress)
            {
                var originalRequest = work.Request;
                var authorization = authorizeProgress(ReadIdentityState(database, deadline), originalRequest);
                progressGuard = authorization?.Guard;
                if (originalRequest.Cycle?.Kind == QualificationCycleEventKind.CoreCommitted &&
                    progressGuard is not null && QualificationCoreCommitGuardDecorator is { } decorate)
                    progressGuard = decorate(progressGuard) ??
                        throw new InvalidOperationException("QualificationCycleCommitGuardRequired");
                if (authorization?.Request is null)
                    throw new InvalidOperationException("StationQualificationProgressAuthorizationUnavailable");
                RequireStationQualificationAuthorizationSubstitution(originalRequest, authorization.Request);
                work.Request = authorization.Request;
            }
            var rows = ReadStationQualificationRows(database, options, deadline);
            var last = rows.Count == 0 ? null : rows[^1].Event;
            AuditChainDatabase.Require(requestHeaderMatches(work.Request.Header, last, work.Request),
                "StationQualificationHeaderMismatch");
            AuditChainDatabase.Require(work.Request.ExpectedLastPosition == (last?.Position ?? 0) &&
                work.Request.ExpectedLastHash == (last?.ContentHash),
                "StationQualificationCursorConflict");
            EnsureStationQualificationTransition(rows, work.Request);

            var commandReference = work.Request.CommandFact is { } fact
                ? ReadCommandAuditReferenceByEventId(database, fact.EventId, deadline)
                : ReadStationQualificationCommandReference(database, work.Request, deadline);
            var authorizationReference = ReadStationQualificationAuthorizationReference(database,
                work.Request.CommandCorrelationId, deadline);
            AuditChainDatabase.Require(commandReference is not null,
                "StationQualificationCommandAuditMissing");
            AuditChainDatabase.Require(authorizationReference is not null,
                "StationQualificationAuthorizationAuditMissing");
            var commandAudit = commandReference ??
                throw new InvalidOperationException("StationQualificationCommandAuditMissing");
            var authorizationAudit = authorizationReference ??
                throw new InvalidOperationException("StationQualificationAuthorizationAuditMissing");
            var commandTarget = ReadStationQualificationAuthorizationTarget(authorizationAudit.Payload);
            AuditChainDatabase.Require(commandTarget is { Length: 64 },
                "StationQualificationAuthorizationTargetMissing");
            var eventRequest = work.Request with { CommandAuthorizationTarget = commandTarget };
            var dependencyFailure = ValidateStationQualificationProgressAuthority(database, eventRequest,
                deadline, acceptedCleanup: IsStationQualificationCleanup(eventRequest));
            if (dependencyFailure is not null) throw new InvalidOperationException(dependencyFailure);
            var eventValue = BuildStationQualificationEvent(eventRequest,
                checked((last?.Position ?? 0) + 1), last?.ContentHash, last?.RecordedAtUtc,
                commandAudit, authorizationAudit);
            var terminalFactCount = StationQualificationTerminalFactCount(work.Request);
            // Both ledgers commit atomically. Spend this cycle transition's
            // reserved tail now, while reserving its still-unwritten audit row.
            // Using the previous cycle reserve here prevents Ack from closing
            // a run once unrelated writes have filled their allowed budget.
            long? cycleReserve = work.Request.Cycle is { } pendingCycle
                ? checked(ReadQualificationCycleProgressAuditReserve(database,
                    pendingCycle, eventValue, deadline) + 1)
                : null;
            var capacityReserve = AuditChainDatabase.EnsureStationQualificationTransactionCapacity(
                database, _policy!, checked(1 + terminalFactCount), eventValue, deadline,
                cycleReserve);
            var capacity = new StationQualificationWriteContext(capacityReserve);
            var persisted = AppendStationQualificationEventRecord(database, eventValue,
                commandAudit, authorizationAudit, options, deadline, capacity.TakeReserve(),
                cycleReserve);
            QualificationCycleEvent? cycleEvent = null;
            if (work.Request.Cycle is { } cycleRequest)
            {
                var cycleOptions = _options.QualificationCycles ??
                    throw new InvalidOperationException("QualificationCycleConfigurationRequired");
                cycleEvent = AppendQualificationCycleEventRecord(database, cycleRequest,
                    persisted, cycleOptions, deadline);
            }
            AppendStationQualificationTerminalFacts(database, work.Request, persisted, capacity, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            try { progressGuard?.Commit(); } catch (Exception) { }
            work.Result = new(new(persisted.CommandCorrelationId,
                CommandDisposition.Accepted, persisted.ReasonCode, AuditPersistence.Persisted,
                persisted.AttemptId), persisted.Header, persisted, true,
                eventValue.Position - 1, eventValue.PreviousHash, work.Request.CommandFact, cycleEvent);
            Interlocked.Exchange(ref _lastCommittedAuditSequence, persisted.AuditSequence);
            return new(true, "StationQualificationEventPersisted", work.Request.CommandFact);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, exception is InvalidOperationException ? exception.Message :
                SqliteAuditIntegrityQuery.FaultReason(exception, "StationQualificationCommitFailed"));
        }
        finally
        {
            if (!committed) Rollback(database);
            try { progressGuard?.Dispose(); } catch (Exception) { }
        }

        static bool requestHeaderMatches(StationQualificationSessionHeader header,
            StationQualificationSessionEvent? last, StationQualificationProgressRequest request) =>
            last is null ||
            last.Header.ContentHash == header.ContentHash && last.SessionId == header.SessionId ||
            last.Terminal && last.Phase == StationQualificationSessionPhase.Closed &&
            request.CommandKind == AuditedCommandKind.StartStationQualificationSession &&
            request.Phase == StationQualificationSessionPhase.Admitted && !request.Terminal &&
            request.Header.SessionId != last.SessionId;
    }

    private static bool IsStationQualificationCleanup(StationQualificationProgressRequest request) =>
        request.Terminal || request.Phase is StationQualificationSessionPhase.Restoring or
            StationQualificationSessionPhase.RecoveryBlocked ||
        request.Run is { Terminal: true, ExecutionStatus: not ExecutionStatus.Success };

    private static void RequireStationQualificationAuthorizationSubstitution(
        StationQualificationProgressRequest original, StationQualificationProgressRequest authorized)
    {
        AuditChainDatabase.Require(authorized.Header.ContentHash == original.Header.ContentHash &&
            authorized.Header.SessionId == original.Header.SessionId &&
            authorized.ExpectedLastPosition == original.ExpectedLastPosition &&
            authorized.ExpectedLastHash == original.ExpectedLastHash &&
            authorized.CommandCorrelationId == original.CommandCorrelationId &&
            authorized.AttemptId == original.AttemptId && authorized.CommandKind == original.CommandKind &&
            authorized.Terminal == original.Terminal &&
            authorized.RecoveryAttempt?.ContentHash == original.RecoveryAttempt?.ContentHash &&
            authorized.CommandAuthorizationTarget == original.CommandAuthorizationTarget &&
            SameQualificationCycleRequest(authorized.Cycle, original.Cycle),
            "StationQualificationProgressAuthorizationContextMismatch");
    }

    private static bool SameQualificationCycleRequest(QualificationCycleWriteRequest? left,
        QualificationCycleWriteRequest? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Kind == right.Kind && left.RunId?.Value == right.RunId?.Value &&
            left.ProfileHash == right.ProfileHash && left.Policy?.ContentHash == right.Policy?.ContentHash &&
            left.RejectedControllerEpoch == right.RejectedControllerEpoch &&
            left.RejectedCycleSequence == right.RejectedCycleSequence &&
            left.EndpointBindingHash == right.EndpointBindingHash && left.ReasonCode == right.ReasonCode &&
            left.ControllerEpoch == right.ControllerEpoch && left.CycleSequence == right.CycleSequence;
    }

    private void AppendStationQualificationIdentityMutation(sqlite3 database,
        IdentityUpdate update, IdentityWork work, StationQualificationWriteContext? capacity,
        StoreDeadline deadline)
    {
        if (update.Result is not StationQualificationTransactionResult result ||
            !result.Accepted || result.Event is null)
            return;
        var options = _options.StationQualifications ??
            throw new InvalidOperationException("StationQualificationConfigurationRequired");
        var commandFact = update.CommandFacts?.FirstOrDefault(value =>
            value.Phase == CommandAuditPhase.Outcome) ?? update.CommandFacts?.FirstOrDefault();
        AuditChainDatabase.Require(commandFact is not null,
            "StationQualificationCommandAuditMissing");
        var commandReference = ReadCommandAuditReferenceByEventId(database,
            commandFact!.EventId, deadline);
        var authorizationReference = ReadIdentityAuditReferenceByEventId(database,
            update.Events.Single().EventId, deadline);
        AuditChainDatabase.Require(authorizationReference is not null,
            "StationQualificationAuthorizationAuditMissing");
        var authorizationAudit = authorizationReference ??
            throw new InvalidOperationException("StationQualificationAuthorizationAuditMissing");
        var commandTarget = ReadStationQualificationAuthorizationTarget(authorizationAudit.Payload);
        AuditChainDatabase.Require(commandTarget is { Length: 64 },
            "StationQualificationAuthorizationTargetMissing");
        var eventValue = RebindStationQualificationCommandTarget(result.Event, commandTarget!);
        var persisted = AppendStationQualificationEventRecord(database, eventValue,
            commandReference, authorizationAudit, options, deadline,
            capacity?.TakeReserve());
        work.Result = result with { Header = persisted.Header, Event = persisted,
            CommandFact = commandFact };
    }

    private static void EnsureStationQualificationTransition(
        IReadOnlyList<StationQualificationStoredRow> rows, StationQualificationProgressRequest request)
    {
        var sameSession = rows.Where(row => row.Event.SessionId == request.Header.SessionId)
            .Select(row => row.Event).OrderBy(value => value.Position).ToArray();
        var current = sameSession.LastOrDefault();
        if (current is null)
        {
            var globalLast = rows.Count == 0 ? null : rows[^1].Event;
            AuditChainDatabase.Require(request.CommandKind ==
                AuditedCommandKind.StartStationQualificationSession &&
                request.Phase == StationQualificationSessionPhase.Admitted && !request.Terminal &&
                (globalLast is null || (globalLast.Terminal &&
                    globalLast.Phase == StationQualificationSessionPhase.Closed)),
                "StationQualificationSessionAlreadyActive");
        }
        AuditChainDatabase.Require(current is null || !current.Terminal,
            "StationQualificationSessionAlreadyTerminal");
        if (request.Terminal)
        {
            var exit = sameSession.LastOrDefault(value =>
                value.CommandKind == AuditedCommandKind.ExitStationQualificationSession);
            AuditChainDatabase.Require(exit is null ||
                (request.CommandKind == exit.CommandKind && request.AttemptId == exit.AttemptId),
                "StationQualificationTerminalCommandMismatch");
        }
        RequireStationQualificationRecoveryContinuation(current, request.Header, request.Phase,
            request.RecoveryAttempt, request.Observation, request.Run);
        if (request.Run is { } run)
        {
            AuditChainDatabase.Require(run.SessionId == request.Header.SessionId &&
                run.ContextHash == request.Header.Plan.QualificationContextHash,
                "StationQualificationRunBindingMismatch");
            var previousRun = sameSession.Where(value => value.Run?.RunId.Value == run.RunId.Value)
                .Select(value => value.Run).LastOrDefault();
            if (previousRun is not null)
                AuditChainDatabase.Require(previousRun.Completed
                    ? run.ContentHash == previousRun.ContentHash
                    : run.ContentHash == previousRun.ContentHash || run.Completed,
                    "StationQualificationRunConflict");
            if (current?.Phase is StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked)
                AuditChainDatabase.Require(previousRun is not null && !previousRun.Completed && run.Completed &&
                    run.ExecutionStatus == ExecutionStatus.Cancelled && run.QualificationPayload is null &&
                    run.ResultPayloadJson is null && run.ResultPayloadHash is null,
                    "StationQualificationRecoveryRunAdmissionForbidden");
        }
        if (request.Terminal)
            AuditChainDatabase.Require(request.Phase is StationQualificationSessionPhase.Closed &&
                request.Restoration is StationQualificationRestorationState.Restored,
                "StationQualificationTerminalPhaseInvalid");
    }

    private static void RequireStationQualificationRecoveryContinuation(StationQualificationSessionEvent? current,
        StationQualificationSessionHeader header, StationQualificationSessionPhase phase,
        StationQualificationRecoveryAttempt? attempt, QualificationFacilityObservation? observation,
        StationQualificationRunRecord? run)
    {
        if (current?.RecoveryAttempt is { } previous)
        {
            AuditChainDatabase.Require(phase is StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked or StationQualificationSessionPhase.Closed,
                "StationQualificationRecoveryCannotResumeTesting");
            AuditChainDatabase.Require(attempt is not null, "StationQualificationRecoveryAttemptMissing");
            if (previous.ContentHash != attempt!.ContentHash)
                AuditChainDatabase.Require(phase == StationQualificationSessionPhase.Restoring &&
                    attempt.RuntimeEpoch != previous.RuntimeEpoch && attempt.LeaseNonce != previous.LeaseNonce &&
                    observation is null && run is null, "StationQualificationRecoveryAttemptMismatch");
        }
        else if (current is { Phase: StationQualificationSessionPhase.Restoring or
            StationQualificationSessionPhase.RecoveryBlocked })
        {
            // The first restoring/blocked event may still belong to the
            // original lease. The restoration attempt is journaled before
            // retiring that lease. Normal exit retains the Runtime epoch;
            // both normal exit and restart must introduce a fresh nonce.
            if (attempt is not null)
                AuditChainDatabase.Require(phase == StationQualificationSessionPhase.Restoring &&
                    attempt.LeaseNonce != header.LeaseNonce &&
                    observation is null && run is null,
                    "StationQualificationRecoveryAttemptAdmissionInvalid");
            else
                AuditChainDatabase.Require(phase is StationQualificationSessionPhase.Restoring or
                    StationQualificationSessionPhase.RecoveryBlocked or StationQualificationSessionPhase.Closed,
                    "StationQualificationRecoveryTransitionInvalid");
        }
        else if (attempt is not null)
            AuditChainDatabase.Require(phase == StationQualificationSessionPhase.Restoring &&
                attempt.LeaseNonce != header.LeaseNonce &&
                observation is null && run is null,
                "StationQualificationRecoveryAttemptAdmissionInvalid");
    }

    private static StationQualificationSessionEvent BuildStationQualificationEvent(
        StationQualificationProgressRequest request, long position, string? previousHash,
        DateTimeOffset? previousRecordedAtUtc, AuditCommandReference command,
        AuditIdentityReference authorization)
    {
        var run = request.Run;
        var eventReason = request.ReasonCode;
        if (run is not null && request.FinalizeRun is not null && run.Completed)
        {
            var finalized = request.FinalizeRun(run) ??
                throw new InvalidOperationException("StationQualificationRunFinalizationInvalid");
            AuditChainDatabase.Require(finalized.RunId.Value == run.RunId.Value &&
                finalized.SessionId == run.SessionId && finalized.Position == run.Position &&
                finalized.StimulusSequence == run.StimulusSequence && finalized.ContextHash == run.ContextHash &&
                finalized.ScenarioId == run.ScenarioId && finalized.ControllerEpoch == run.ControllerEpoch &&
                finalized.CycleSequence == run.CycleSequence && finalized.AdmittedAtUtc == run.AdmittedAtUtc,
                "StationQualificationRunFinalizationBindingMismatch");
            run = finalized;
            // A terminal evaluator is allowed to retire a race-lost run. Its
            // event reason must describe the same immutable final fact as the
            // embedded run, otherwise hot projections and cold replay disagree.
            eventReason = finalized.ReasonCode;
        }
        var recordedAtUtc = DateTimeOffset.UtcNow;
        if (recordedAtUtc < request.Header.StartedAtUtc)
            recordedAtUtc = request.Header.StartedAtUtc;
        if (previousRecordedAtUtc is { } previous && recordedAtUtc < previous)
            recordedAtUtc = previous;
        return new(position, previousHash, request.Header, request.CommandCorrelationId,
            request.AttemptId, request.CommandKind, request.Phase, request.Restoration,
            eventReason, request.Terminal, recordedAtUtc,
            request.Observation, run, command.Sequence, command.Hash,
            authorization.Sequence, authorization.Hash, 0, null, request.RecoveryAttempt,
            request.CommandAuthorizationTarget);
    }

    private static StationQualificationSessionEvent RebindStationQualificationCommandTarget(
        StationQualificationSessionEvent value, string target) => new(value.Position,
        value.PreviousHash, value.Header, value.CommandCorrelationId, value.AttemptId,
        value.CommandKind, value.Phase, value.Restoration, value.ReasonCode, value.Terminal,
        value.RecordedAtUtc, value.Observation, value.Run, value.CommandAuditSequence,
        value.CommandAuditHash, value.AuthorizationAuditSequence, value.AuthorizationAuditHash,
        value.AuditSequence, value.AuditHash, value.RecoveryAttempt, target);

    private StationQualificationSessionEvent AppendStationQualificationEventRecord(
        sqlite3 database, StationQualificationSessionEvent value, AuditCommandReference command,
        AuditIdentityReference authorization, StationQualificationStoreOptions options,
        StoreDeadline deadline, long? stationQualificationReserveOverride = null,
        long? qualificationCycleReserveOverride = null)
    {
        var rows = ReadStationQualificationRows(database, options, deadline);
        AuditChainDatabase.Require(rows.Count < options.MaximumEntries,
            "StationQualificationEntryCapacityExceeded");
        var semanticFailure = ValidateStationQualificationSemantics(rows.Select(row => row.Event).ToArray(), value);
        if (semanticFailure is not null) throw new InvalidOperationException(semanticFailure);
        var previousSession = rows.LastOrDefault(row => row.Event.SessionId == value.SessionId)?.Event;
        if (previousSession is not null)
            RequireStationQualificationPhaseTransition(previousSession, value);
        var position = value.Position;
        AuditChainDatabase.Require(position == rows.Count + 1L,
            "StationQualificationPositionGap");
        var previousHash = rows.Count == 0 ? null : rows[^1].Event.ContentHash;
        var withReferences = new StationQualificationSessionEvent(position, previousHash,
            value.Header, value.CommandCorrelationId, value.AttemptId, value.CommandKind,
            value.Phase, value.Restoration, value.ReasonCode, value.Terminal, value.RecordedAtUtc,
            value.Observation, value.Run, command.Sequence, command.Hash,
            authorization.Sequence, authorization.Hash, 0, null, value.RecoveryAttempt,
            value.CommandAuthorizationTarget);
        var auditPayload = EncodeStationQualificationAuditPayload(withReferences);
        var futureAuditReserve = stationQualificationReserveOverride ??
            ReadStationQualificationAuditReserveAfter(rows, withReferences);
        var auditSequence = AuditChainDatabase.AppendStationQualificationLedgerEntry(database,
            _policy!, _signingKey!, position, auditPayload, options, deadline,
            futureAuditReserve, qualificationCycleReserveOverride);
        var auditHash = AuditChainDatabase.Read(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND StationQualificationPosition=? LIMIT 2;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            auditSequence.ToString(CultureInfo.InvariantCulture), StationQualificationEventKind,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(auditHash is { Length: 64 },
            "StationQualificationAuditEntryMissing");
        var persisted = new StationQualificationSessionEvent(position, previousHash,
            value.Header, value.CommandCorrelationId, value.AttemptId, value.CommandKind,
            value.Phase, value.Restoration, value.ReasonCode, value.Terminal, value.RecordedAtUtc,
            value.Observation, value.Run, command.Sequence, command.Hash,
            authorization.Sequence, authorization.Hash, auditSequence, auditHash,
            value.RecoveryAttempt, value.CommandAuthorizationTarget);
        var payload = StationQualificationStorageCodec.Encode(persisted);
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        var futureRows = ReadStationQualificationFutureRowsAfter(rows,
            persisted);
        AuditChainDatabase.Require(checked((long)rows.Count + 1 + futureRows) <=
            options.MaximumEntries, "StationQualificationEntryCapacityExceeded");
        AuditChainDatabase.Require(checked(total + payload.Length +
            futureRows * (long)options.MaximumPayloadBytes) <= options.MaximumTotalBytes,
            "StationQualificationTotalCapacityExceeded");
        InsertStationQualificationEvent(database, persisted, payload, auditPayload, options, deadline);
        return persisted;
    }

    private static void InsertStationQualificationEvent(sqlite3 database,
        StationQualificationSessionEvent value, byte[] payload, byte[] auditPayload,
        StationQualificationStoreOptions options, StoreDeadline deadline)
    {
        var payloadHash = StationQualificationStorageCodec.PayloadHash(auditPayload);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO station_qualification_events(
                Position,PreviousHash,SessionId,RuntimeEpoch,StartCorrelationId,StartAttemptId,
                CommandCorrelationId,AttemptId,CommandKind,Phase,Restoration,Terminal,RunId,
                ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,AuthorizationTarget,
                ReasonCode,RecordedAtUtc,HeaderContentHash,ContentHash,PayloadHash,Payload,
                CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,AuthorizationAuditHash,
                AuditSequence,AuditHash,CommandAuthorizationTarget)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            value.Position.ToString(CultureInfo.InvariantCulture), value.PreviousHash,
            value.SessionId.ToString("D"), value.RuntimeEpoch.ToString("D"),
            value.Header.StartCorrelationId.ToString("D"), value.Header.StartAttemptId.ToString("D"),
            value.CommandCorrelationId.ToString("D"), value.AttemptId.ToString("D"),
            ((int)value.CommandKind).ToString(CultureInfo.InvariantCulture),
            ((int)value.Phase).ToString(CultureInfo.InvariantCulture),
            ((int)value.Restoration).ToString(CultureInfo.InvariantCulture), value.Terminal ? "1" : "0",
            value.Run?.RunId.Value.ToString("D"), value.Header.ActorPrincipalId.ToString("D"),
            value.Header.ActorSessionId.ToString("D"), value.Header.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            value.Header.AuthorizationTarget, value.ReasonCode,
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), value.Header.ContentHash,
            value.ContentHash, payloadHash, Convert.ToBase64String(payload),
            value.CommandAuditSequence!.Value.ToString(CultureInfo.InvariantCulture), value.CommandAuditHash!,
            value.AuthorizationAuditSequence!.Value.ToString(CultureInfo.InvariantCulture), value.AuthorizationAuditHash!,
            value.AuditSequence.ToString(CultureInfo.InvariantCulture), value.AuditHash!,
            value.CommandAuthorizationTarget);
    }

    private static AuditCommandReference? ReadStationQualificationCommandReference(
        sqlite3 database, StationQualificationProgressRequest request, StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT e.Sequence,e.Hash,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,f.CommandKind
            FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
            WHERE e.Kind='CommandFact' AND f.AttemptId=? AND f.CorrelationId=? AND f.CommandKind=?
            ORDER BY f.AggregateSequence DESC LIMIT 2;", deadline,
            statement => new AuditCommandReference(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParseStationGuid(SqliteNative.ColumnText(statement, 2)),
                ParseStationGuid(SqliteNative.ColumnText(statement, 3)),
                ParseStationGuid(SqliteNative.ColumnText(statement, 4)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 5)),
            request.AttemptId.ToString("D"), request.CommandCorrelationId.ToString("D"),
            ((int)request.CommandKind).ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        return row is { Sequence: > 0, Hash.Length: 64 } ? row : null;
    }

    private static AuditIdentityReference? ReadStationQualificationAuthorizationReference(
        sqlite3 database, Guid correlationId, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database,
            "SELECT Sequence,Hash,Payload FROM audit_entries WHERE Kind='IdentityEvent' ORDER BY Sequence DESC;",
            deadline, statement => new AuditIdentityReference(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                SqliteNative.ColumnText(statement, 2) ?? string.Empty));
        foreach (var row in rows)
        {
            try
            {
                var payload = Convert.FromBase64String(row.Payload);
                if (IdentityAuditEvent.TryReadEventKind(payload, out var kind) &&
                    kind == IdentityEventKind.StationQualificationAuthorized &&
                    IdentityAuditEvent.TryReadCommandCorrelation(payload, out var bound) &&
                    bound == correlationId)
                    return row;
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>
    /// Reads the target from the already verified identity payload.  The
    /// station-qualification identity event stores this at the canonical
    /// action-target field; using the payload here preserves a distinct Exit
    /// target instead of silently replacing it with the Start header target.
    /// </summary>
    private static string? ReadStationQualificationAuthorizationTarget(string encodedPayload)
    {
        try
        {
            var payload = Convert.FromBase64String(encodedPayload);
            var fields = DecodeStationQualificationIdentityFields(payload);
            var target = fields.Length == 49 ? fields[37] : null;
            return target is { Length: 64 } && target.All(Uri.IsHexDigit) ? target : null;
        }
        catch (Exception exception) when (exception is FormatException or EndOfStreamException or
            DecoderFallbackException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private static string?[] DecodeStationQualificationIdentityFields(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var version = reader.ReadBytes(4);
        if (version.Length != 4 || BinaryPrimitives.ReadInt32BigEndian(version) !=
            AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("StationQualificationIdentityPayloadInvalid");
        string? ReadValue()
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            if (marker != 1) throw new InvalidOperationException("StationQualificationIdentityPayloadInvalid");
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length != 4) throw new EndOfStreamException();
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > 4096) throw new InvalidOperationException("StationQualificationIdentityPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        if (ReadValue() != "IdentityEvent")
            throw new InvalidOperationException("StationQualificationIdentityPayloadInvalid");
        var countBytes = reader.ReadBytes(4);
        if (countBytes.Length != 4) throw new EndOfStreamException();
        var count = BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count != 49) throw new InvalidOperationException("StationQualificationIdentityPayloadInvalid");
        var fields = new string?[count];
        for (var index = 0; index < count; index++) fields[index] = ReadValue();
        if (stream.Position != stream.Length)
            throw new InvalidOperationException("StationQualificationIdentityPayloadTrailingBytes");
        return fields;
    }

    internal static void ValidateStationQualificationHistory(sqlite3 database,
        StationQualificationStoreOptions options, StoreDeadline deadline)
        => VerifyAllStationQualifications(database, options, deadline);

    private sealed class StationQualificationProgressWork
    {
        internal StationQualificationProgressWork(StationQualificationProgressRequest request) => Request = request;
        internal StationQualificationProgressRequest Request { get; set; }
        internal StationQualificationTransactionResult? Result { get; set; }
    }

    private static Guid ParseStationGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty ? parsed :
        throw new InvalidOperationException("StationQualificationGuidInvalid");

    private static DateTimeOffset ParseStationTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed :
        throw new InvalidOperationException("StationQualificationTimestampInvalid");
}
