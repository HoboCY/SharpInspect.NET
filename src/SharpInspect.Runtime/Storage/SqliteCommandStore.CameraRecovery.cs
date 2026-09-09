using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema 11 camera recovery evidence. The terminal ledger is deliberately
/// separate from identity_authority: a crash leaves the signed admission
/// pending and never causes a restart replay or an assumed hardware outcome.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string CameraRecoverySchemaSql = @"
        CREATE TABLE camera_recovery_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE camera_recovery_terminal_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            AttemptId TEXT NOT NULL UNIQUE CHECK(length(AttemptId)=36),
            CorrelationId TEXT NOT NULL CHECK(length(CorrelationId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            ExpectedCycleId TEXT NOT NULL CHECK(length(ExpectedCycleId)=36),
            NewCycleId TEXT NULL CHECK(NewCycleId IS NULL OR length(NewCycleId)=36),
            LogicalRole TEXT NOT NULL CHECK(length(LogicalRole)>0 AND length(LogicalRole)<=64),
            Started INTEGER NOT NULL CHECK(Started IN (0,1)),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0 AND length(ReasonCode)<=128),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            RecordedAtUtc TEXT NOT NULL,
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            CHECK((Started=1 AND NewCycleId IS NOT NULL) OR (Started=0 AND NewCycleId IS NULL)));
        CREATE INDEX ix_camera_recovery_terminal_correlation ON camera_recovery_terminal_events(CorrelationId);
        CREATE INDEX ix_camera_recovery_terminal_cycle ON camera_recovery_terminal_events(ExpectedCycleId);
        CREATE TRIGGER camera_recovery_config_immutable_update BEFORE UPDATE ON camera_recovery_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraRecoveryConfiguration');
        END;
        CREATE TRIGGER camera_recovery_config_immutable_delete BEFORE DELETE ON camera_recovery_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraRecoveryConfiguration');
        END;
        CREATE TRIGGER camera_recovery_terminal_immutable_update BEFORE UPDATE ON camera_recovery_terminal_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraRecoveryTerminal');
        END;
        CREATE TRIGGER camera_recovery_terminal_immutable_delete BEFORE DELETE ON camera_recovery_terminal_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraRecoveryTerminal');
        END;";

    internal ValueTask<StoreWriteResult> AppendCameraRecoveryTerminalAsync(
        CommandAuditFact admission, Guid? newCycleId, bool started, string reasonCode,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!CameraRecoveryEnabled) return ValueTask.FromResult(new StoreWriteResult(false, "CameraRecoveryUnavailable"));
        if (admission.Phase != CommandAuditPhase.Outcome ||
            admission.Disposition != CommandDisposition.Accepted ||
            admission.CommandKind != AuditedCommandKind.StartCameraRecoveryCycle)
            return ValueTask.FromResult(new StoreWriteResult(false, "CameraRecoveryTerminalInputInvalid"));
        return EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                RecoveryTerminal: new CameraRecoveryTerminalWork(admission, newCycleId, started, reasonCode)),
            deadline, cancellationToken, "CameraRecoveryUnavailable", "CameraRecoveryCommitDeadlineExceeded");
    }

    private void InitializeCameraRecoverySchema(sqlite3 database,
        CameraRecoveryStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Execute(database, @"INSERT INTO camera_recovery_store_config
            (Id,FormatVersion,BindingHash) VALUES(1,?,?);", deadline,
            CameraRecoveryStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        _ = AuditChainDatabase.AppendCameraRecoveryActivation(database, _policy!, _signingKey!, options, deadline);
    }

    internal static void RequireConfiguredCameraRecovery(sqlite3 database,
        CameraRecoveryStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var configs = AuditChainDatabase.Read(database, @"SELECT FormatVersion,BindingHash
            FROM camera_recovery_store_config WHERE Id=1;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                BindingHash: SqliteNative.ColumnText(statement, 1)!)).ToArray();
        AuditChainDatabase.Require(configs.Length == 1 &&
            configs[0].FormatVersion == CameraRecoveryStoreOptions.FormatVersion &&
            configs[0].BindingHash == options.BindingHash,
            "CameraRecoveryConfigurationMismatch");
    }

    internal static void VerifyCameraRecoveryActivationPayload(sqlite3 database,
        byte[] payload, CameraRecoveryStoreOptions options, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(payload.SequenceEqual(options.EncodeActivationPayload()),
            "CameraRecoveryActivationMismatch");
        RequireConfiguredCameraRecovery(database, options, deadline);
    }

    internal static byte[] ReadAndValidateCameraRecovery(sqlite3 database, long position,
        byte[] auditPayload, StoreDeadline deadline, CameraRecoveryStoreOptions? options = null)
    {
        var row = ReadCameraRecoveryRows(database, "WHERE Position=? LIMIT 1", deadline,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "CameraRecoveryTerminalMissing");
        var rowValue = row!;
        var record = DecodeRow(rowValue);
        var payload = DecodePayload(rowValue.Payload);
        AuditChainDatabase.Require(payload.SequenceEqual(auditPayload), "CameraRecoveryTerminalBindingMismatch");
        AuditChainDatabase.Require(CameraRecoveryStorageCodec.ReadPosition(payload) == position,
            "CameraRecoveryTerminalPositionMismatch");
        CameraRecoveryStorageCodec.Validate(record);
        options?.Validate();
        AuditChainDatabase.Require(CameraRecoveryStorageCodec.Encode(record).SequenceEqual(payload) &&
            CameraRecoveryStorageCodec.PayloadHash(payload) == rowValue.PayloadHash,
            "CameraRecoveryTerminalBindingMismatch");
        return payload;
    }

    internal static void ValidateCameraRecoveryHistory(sqlite3 database,
        CameraRecoveryStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredCameraRecovery(database, options, deadline);
        var rows = ReadCameraRecoveryRows(database, "ORDER BY Position", deadline);
        AuditChainDatabase.Require(rows.Count <= CameraRecoveryStoreOptions.MaximumEventsHardLimit,
            "CameraRecoveryEventCapacityExceeded");
        var expectedPosition = 1L;
        var totalBytes = 0L;
        var eventIds = new HashSet<Guid>();
        var attempts = new HashSet<Guid>();
        var records = new List<CameraRecoveryTerminalRecord>(rows.Count);
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == expectedPosition++, "CameraRecoveryPositionGap");
            var record = DecodeRow(row);
            AuditChainDatabase.Require(eventIds.Add(record.EventId) && attempts.Add(record.AttemptId),
                "CameraRecoveryTerminalConflict");
            var payload = DecodePayload(row.Payload);
            totalBytes = checked(totalBytes + payload.Length);
            AuditChainDatabase.Require(totalBytes <= CameraRecoveryStoreOptions.MaximumTotalBytesHardLimit,
                "CameraRecoveryTotalCapacityExceeded");
            AuditChainDatabase.Require(CameraRecoveryStorageCodec.Encode(record).SequenceEqual(payload) &&
                CameraRecoveryStorageCodec.PayloadHash(payload) == row.PayloadHash,
                "CameraRecoveryTerminalBindingMismatch");
            records.Add(record);
        }
        ValidateCameraRecoveryCrossChain(database, records, deadline);
    }

    private static void ValidateCameraRecoveryCrossChain(sqlite3 database,
        IReadOnlyList<CameraRecoveryTerminalRecord> terminals, StoreDeadline deadline)
    {
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline);
        AuditChainDatabase.Require(stationId is { Length: > 0 },
            "CameraRecoveryAuthorizationBindingMismatch");
        var identities = AuditChainDatabase.Read(database, @"
            SELECT IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY IdentityPosition;",
            deadline, statement =>
        {
            var ordinal = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 1);
            AuditChainDatabase.Require(encoded is { Length: > 0 }, "CameraRecoveryAuthorizationBindingMismatch");
            return (Ordinal: ordinal, Payload: Convert.FromBase64String(encoded!));
        }).Where(item => IdentityAuditEvent.TryReadCameraRecoveryAuthorization(item.Payload,
            item.Ordinal, stationId!, out var ignored)).ToArray();
        var authorizations = identities.Select(item =>
        {
            IdentityAuditEvent.TryReadCameraRecoveryAuthorization(item.Payload, item.Ordinal,
                stationId!, out var binding);
            return binding;
        }).ToArray();
        var commands = AuditChainDatabase.Read(database, @"
            SELECT Position,EventId,AttemptId,CorrelationId,RuntimeEpoch,CommandKind,Source,
                ClaimedPrincipalId,ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition,
                ReasonCode,AuthenticatedHumanPrincipalId FROM command_facts
            WHERE CommandKind=17 ORDER BY Position;", deadline, ReadRecoveryCommandAudit);

        foreach (var terminal in terminals)
        {
            var admission = commands.SingleOrDefault(item => item.AttemptId == terminal.AttemptId &&
                item.Phase == CommandAuditPhase.Outcome && item.Disposition == CommandDisposition.Accepted);
            AuditChainDatabase.Require(admission is not null &&
                admission.CorrelationId == terminal.CorrelationId &&
                admission.RuntimeEpoch == terminal.RuntimeEpoch &&
                admission.ClaimedSessionId == terminal.SessionId &&
                admission.AuthenticatedHumanPrincipalId == terminal.ActorPrincipalId.ToString("D"),
                "CameraRecoveryCommandBindingMismatch");
            var identity = authorizations.SingleOrDefault(item =>
                item.Kind == IdentityEventKind.CameraRecoveryCycleStartAuthorized &&
                item.CommandCorrelationId == terminal.CorrelationId &&
                item.ActorPrincipalId == terminal.ActorPrincipalId &&
                item.PrincipalId == terminal.ActorPrincipalId &&
                item.SessionId == terminal.SessionId &&
                item.AuthorizationRevision == terminal.AuthorizationRevision &&
                item.ExpectedCycleId == terminal.ExpectedCycleId &&
                item.ActionTargetId == terminal.LogicalRole);
            AuditChainDatabase.Require(identity is not null &&
                identity.RecoveryReasonCode is { Length: > 0 } &&
                identity.StepUpGrantId == admission!.ClaimedStepUpGrantId &&
                identity.CommandCorrelationId == admission.CorrelationId &&
                admission.ClaimedPrincipalId == terminal.ActorPrincipalId.ToString("D"),
                "CameraRecoveryAuthorizationBindingMismatch");
            var terminalCommand = commands.SingleOrDefault(item => item.AttemptId == terminal.AttemptId &&
                item.Phase == (terminal.Started ? CommandAuditPhase.Completed : CommandAuditPhase.Failed));
            AuditChainDatabase.Require(terminalCommand is not null && terminalCommand.Disposition is null &&
                terminalCommand.CorrelationId == admission!.CorrelationId &&
                terminalCommand.RuntimeEpoch == admission.RuntimeEpoch &&
                terminalCommand.Source == admission.Source &&
                terminalCommand.ClaimedPrincipalId == admission.ClaimedPrincipalId &&
                terminalCommand.ClaimedSessionId == admission.ClaimedSessionId &&
                terminalCommand.ClaimedStepUpGrantId == admission.ClaimedStepUpGrantId &&
                terminalCommand.AuthenticatedHumanPrincipalId == admission.AuthenticatedHumanPrincipalId &&
                terminalCommand.ReasonCode == terminal.ReasonCode,
                "CameraRecoveryTerminalCommandBindingMismatch");
        }
    }

    private StoreWriteResult AppendCameraRecoveryTerminalCore(sqlite3 database,
        CameraRecoveryTerminalWork work, StoreDeadline deadline)
    {
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new(false, integrity?.ReasonCode ?? "CameraRecoveryAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");
        var transactionStarted = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            var archiveStore = _options.AlgorithmResultArchive is not null;
            var draftStore = _options.RecipeDrafts is not null;
            var cameraStore = _options.CameraSetup is not null;
            var networkStore = _options.CameraNetwork is not null;
            var alarmStore = _options.AlarmPolicy is not null;
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries), startup: true,
                deadline, validateAnchorReceipt: false,
                archiveOptions: archiveStore ? _options.AlgorithmResultArchive : null,
                recipeDraftOptions: draftStore ? _options.RecipeDrafts : null,
                cameraSetupOptions: cameraStore ? _options.CameraSetup : null,
                cameraRecoveryOptions: _options.CameraRecovery,
                 cameraNetworkOptions: networkStore ? _options.CameraNetwork : null,
                 imagingSetupOptions: _options.ImagingSetup,
                 calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                releaseOptions: _options.RecipeReleases);
            if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (archiveStore) AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            if (draftStore) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
            if (cameraStore) AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
            AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                _options.CameraRecovery);
            if (networkStore)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                    _options.CameraNetwork);
            if (_options.ImagingSetup is not null)
                AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                    _options.ImagingSetup);
            if (_options.RecipeReleases is not null)
                AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                    _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);

            ValidateFact(work.Admission);
            var attempt = ReadAttempt(database, work.Admission.AttemptId, deadline);
            AuditChainDatabase.Require(attempt is not null &&
                attempt.Value.OutcomeDisposition == CommandDisposition.Accepted &&
                attempt.Value.Matches(work.Admission), "CameraRecoveryAdmissionContextMismatch");
            var persistedAdmission = ReadFact(database, work.Admission.AttemptId, aggregateSequence: 1, deadline);
            AuditChainDatabase.Require(persistedAdmission is not null &&
                persistedAdmission.EventId == work.Admission.EventId &&
                persistedAdmission.ReasonCode == work.Admission.ReasonCode &&
                persistedAdmission.Phase == CommandAuditPhase.Outcome &&
                persistedAdmission.Disposition == CommandDisposition.Accepted,
                "CameraRecoveryAdmissionMissing");
            AuditChainDatabase.Require(!Exists(database,
                "SELECT 1 FROM camera_recovery_terminal_events WHERE AttemptId=? LIMIT 1;",
                work.Admission.AttemptId, deadline), "CameraRecoveryTerminalDuplicate");
            var authorization = ReadCameraRecoveryAuthorization(database, work.Admission, deadline);
            AuditChainDatabase.Require(authorization is not null, "CameraRecoveryAuthorizationBindingMismatch");
            AuditChainDatabase.Require(CameraRecoveryStorageCodec.StableIdentifier(work.ReasonCode),
                "CameraRecoveryTerminalReasonInvalid");
            AuditChainDatabase.Require(work.Started
                ? work.NewCycleId is { } newCycle && newCycle != Guid.Empty
                : work.NewCycleId is null, "CameraRecoveryTerminalStateInvalid");

            var position = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0) FROM camera_recovery_terminal_events;", deadline) + 1);
            AuditChainDatabase.Require(position <= CameraRecoveryStoreOptions.MaximumEventsHardLimit,
                "CameraRecoveryEventCapacityExceeded");
            var record = CameraRecoveryTerminalRecord.Create(position, Guid.NewGuid(), work.Admission,
                work.NewCycleId, work.Started, work.ReasonCode, authorization!, DateTimeOffset.UtcNow);
            var payload = CameraRecoveryStorageCodec.Encode(record);
            var previousBytes = AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(SUM(length(Payload)),0) FROM camera_recovery_terminal_events;", deadline);
            AuditChainDatabase.Require(checked(previousBytes + payload.Length) <=
                CameraRecoveryStoreOptions.MaximumTotalBytesHardLimit,
                "CameraRecoveryTotalCapacityExceeded");
            InsertCameraRecoveryTerminal(database, record, payload, deadline);
            _ = AuditChainDatabase.AppendCameraRecoveryEvent(database, _policy!, _signingKey!,
                position, deadline);
            var terminalFact = new CommandAuditFact(Guid.NewGuid(), work.Admission.AttemptId,
                work.Admission.CorrelationId, work.Admission.RuntimeEpoch, DateTimeOffset.UtcNow,
                AuditedCommandKind.StartCameraRecoveryCycle, work.Admission.Source,
                work.Admission.ClaimedPrincipalId, work.Admission.ClaimedSessionId,
                work.Admission.ClaimedStepUpGrantId,
                work.Started ? CommandAuditPhase.Completed : CommandAuditPhase.Failed, null,
                work.ReasonCode, work.Admission.AuthenticatedHumanPrincipalId);
            InsertFact(database, terminalFact, aggregateSequence: 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, terminalFact.EventId, deadline);
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, "CameraRecoveryTerminalPersisted", terminalFact);
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return new StoreWriteResult(false, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("CameraRecovery", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Audit", StringComparison.Ordinal) ||
            ex.Message.StartsWith("Identity", StringComparison.Ordinal))
        { return new StoreWriteResult(false, ex.Message); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private static void InsertCameraRecoveryTerminal(sqlite3 database,
        CameraRecoveryTerminalRecord record, byte[] payload, StoreDeadline deadline)
    {
        const string sql = @"INSERT INTO camera_recovery_terminal_events
            (Position,EventId,AttemptId,CorrelationId,RuntimeEpoch,ExpectedCycleId,NewCycleId,
             LogicalRole,Started,ReasonCode,ActorPrincipalId,SessionId,AuthorizationRevision,
             RecordedAtUtc,Payload,PayloadHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);";
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            SqliteNative.BindInt64(database, statement, 1, record.Position);
            SqliteNative.BindGuid(database, statement, 2, record.EventId);
            SqliteNative.BindGuid(database, statement, 3, record.AttemptId);
            SqliteNative.BindGuid(database, statement, 4, record.CorrelationId);
            SqliteNative.BindGuid(database, statement, 5, record.RuntimeEpoch);
            SqliteNative.BindGuid(database, statement, 6, record.ExpectedCycleId);
            BindGuid(database, statement, 7, record.NewCycleId);
            SqliteNative.BindText(database, statement, 8, record.LogicalRole);
            SqliteNative.BindInt(database, statement, 9, record.Started ? 1 : 0);
            SqliteNative.BindText(database, statement, 10, record.ReasonCode);
            SqliteNative.BindGuid(database, statement, 11, record.ActorPrincipalId);
            SqliteNative.BindGuid(database, statement, 12, record.SessionId);
            SqliteNative.BindInt64(database, statement, 13, record.AuthorizationRevision);
            SqliteNative.BindText(database, statement, 14, record.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            SqliteNative.BindText(database, statement, 15, Convert.ToBase64String(payload));
            SqliteNative.BindText(database, statement, 16, CameraRecoveryStorageCodec.PayloadHash(payload));
            SqliteNative.Step(database, statement, deadline);
            return 0;
        });
    }

    private static List<CameraRecoveryRow> ReadCameraRecoveryRows(sqlite3 database,
        string suffix, StoreDeadline deadline, params string?[] args) =>
        AuditChainDatabase.Read(database, @"SELECT Position,EventId,AttemptId,CorrelationId,RuntimeEpoch,
            ExpectedCycleId,NewCycleId,LogicalRole,Started,ReasonCode,ActorPrincipalId,SessionId,
            AuthorizationRevision,RecordedAtUtc,Payload,PayloadHash
            FROM camera_recovery_terminal_events " + suffix + ";", deadline,
            statement => new CameraRecoveryRow(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1)!, SqliteNative.ColumnText(statement, 2)!,
                SqliteNative.ColumnText(statement, 3)!, SqliteNative.ColumnText(statement, 4)!,
                SqliteNative.ColumnText(statement, 5)!, SqliteNative.ColumnText(statement, 6),
                SqliteNative.ColumnText(statement, 7)!, SqliteNative.ColumnInt64(statement, 8),
                SqliteNative.ColumnText(statement, 9)!, SqliteNative.ColumnText(statement, 10)!,
                SqliteNative.ColumnText(statement, 11)!, SqliteNative.ColumnInt64(statement, 12),
                SqliteNative.ColumnText(statement, 13)!, SqliteNative.ColumnText(statement, 14)!,
                SqliteNative.ColumnText(statement, 15)!), args);

    private static CameraRecoveryTerminalRecord DecodeRow(CameraRecoveryRow row)
    {
        var eventId = Guid.Empty;
        var attemptId = Guid.Empty;
        var correlationId = Guid.Empty;
        var runtimeEpoch = Guid.Empty;
        var expectedCycle = Guid.Empty;
        var actor = Guid.Empty;
        var session = Guid.Empty;
        var recorded = default(DateTimeOffset);
        var valid = Guid.TryParseExact(row.EventId, "D", out eventId) && eventId != Guid.Empty &&
            Guid.TryParseExact(row.AttemptId, "D", out attemptId) && attemptId != Guid.Empty &&
            Guid.TryParseExact(row.CorrelationId, "D", out correlationId) && correlationId != Guid.Empty &&
            Guid.TryParseExact(row.RuntimeEpoch, "D", out runtimeEpoch) && runtimeEpoch != Guid.Empty &&
            Guid.TryParseExact(row.ExpectedCycleId, "D", out expectedCycle) && expectedCycle != Guid.Empty &&
            (row.NewCycleId is null || Guid.TryParseExact(row.NewCycleId, "D", out _)) &&
            Guid.TryParseExact(row.ActorPrincipalId, "D", out actor) && actor != Guid.Empty &&
            Guid.TryParseExact(row.SessionId, "D", out session) && session != Guid.Empty &&
            row.Started is 0 or 1 && row.AuthorizationRevision >= 0 &&
            DateTimeOffset.TryParseExact(row.RecordedAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out recorded);
        AuditChainDatabase.Require(valid, "CameraRecoveryTerminalInvalid");
        var value = new CameraRecoveryTerminalRecord(row.Position, eventId, attemptId, correlationId,
            runtimeEpoch, expectedCycle,
            row.NewCycleId is null ? null : Guid.ParseExact(row.NewCycleId, "D"), row.LogicalRole,
            row.Started == 1, row.ReasonCode, actor, session, row.AuthorizationRevision, recorded);
        CameraRecoveryStorageCodec.Validate(value);
        return value;
    }

    private static byte[] DecodePayload(string encoded)
    {
        AuditChainDatabase.Require(encoded.Length > 0 && encoded.Length <=
            CameraRecoveryStorageCodec.MaximumEncodedPayloadChars, "CameraRecoveryPayloadInvalid");
        try { return Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidOperationException("CameraRecoveryPayloadInvalid", ex); }
    }

    private static CameraRecoveryAuthorizationAudit? ReadCameraRecoveryAuthorization(
        sqlite3 database, CommandAuditFact admission, StoreDeadline deadline)
    {
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline);
        var rows = AuditChainDatabase.Read(database, @"SELECT IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY IdentityPosition;", deadline,
            statement => (Ordinal: SqliteNative.ColumnInt64(statement, 0),
                Payload: Convert.FromBase64String(SqliteNative.ColumnText(statement, 1)!)));
        Guid.TryParseExact(admission.AuthenticatedHumanPrincipalId, "D", out var actor);
        Guid.TryParseExact(admission.ClaimedSessionId?.ToString(), "D", out var session);
        return rows.Select(item => IdentityAuditEvent.TryReadCameraRecoveryAuthorization(item.Payload,
                    item.Ordinal, stationId!, out var binding) ? binding : null)
            .OfType<CameraRecoveryAuthorizationAudit>()
            .SingleOrDefault(item => item.Kind == IdentityEventKind.CameraRecoveryCycleStartAuthorized &&
                item.ActorPrincipalId == actor && item.PrincipalId == actor &&
                item.CommandCorrelationId == admission.CorrelationId && item.SessionId == session &&
                item.CommandKind == AuditedCommandKind.StartCameraRecoveryCycle);
    }

    private sealed record CameraRecoveryRow(long Position, string EventId, string AttemptId,
        string CorrelationId, string RuntimeEpoch, string ExpectedCycleId, string? NewCycleId,
        string LogicalRole, long Started, string ReasonCode, string ActorPrincipalId,
        string SessionId, long AuthorizationRevision, string RecordedAtUtc, string Payload,
        string PayloadHash);

    private sealed record CameraRecoveryTerminalWork(CommandAuditFact Admission, Guid? NewCycleId,
        bool Started, string ReasonCode);

    private sealed record CameraRecoveryCommandAudit(long Position, Guid EventId, Guid AttemptId,
        Guid CorrelationId, Guid RuntimeEpoch, AuditedCommandKind CommandKind, CommandSource? Source,
        string? ClaimedPrincipalId, Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId,
        CommandAuditPhase Phase, CommandDisposition? Disposition, string ReasonCode,
        string? AuthenticatedHumanPrincipalId);

    private static CameraRecoveryCommandAudit ReadRecoveryCommandAudit(sqlite3_stmt statement) =>
        new(SqliteNative.ColumnInt64(statement, 0), ParseGuid(SqliteNative.ColumnText(statement, 1)),
            ParseGuid(SqliteNative.ColumnText(statement, 2)), ParseGuid(SqliteNative.ColumnText(statement, 3)),
            ParseGuid(SqliteNative.ColumnText(statement, 4)),
            (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 5),
            ParseNullableEnum<CommandSource>(SqliteNative.ColumnText(statement, 6)),
            SqliteNative.ColumnText(statement, 7), ParseNullableGuid(SqliteNative.ColumnText(statement, 8)),
            ParseNullableGuid(SqliteNative.ColumnText(statement, 9)),
            (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 10),
            ParseNullableEnum<CommandDisposition>(SqliteNative.ColumnText(statement, 11)),
            SqliteNative.ColumnText(statement, 12)!, SqliteNative.ColumnText(statement, 13));
}
