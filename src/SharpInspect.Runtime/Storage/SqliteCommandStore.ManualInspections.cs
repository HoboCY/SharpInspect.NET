using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The schema-21 Manual Inspection ledger.  It is deliberately a separate
/// append-only projection: Manual work can be queried and recovered without
/// becoming a production result, activation, or algorithm archive record.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ManualInspectionStoreActivatedKind = "ManualInspectionStoreActivated";
    internal const string ManualInspectionEventKind = "ManualInspectionEvent";

    internal const string ManualInspectionSchemaSql = @"
        CREATE TABLE manual_inspection_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE manual_inspection_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            StartCorrelationId TEXT NOT NULL CHECK(length(StartCorrelationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            CommandCorrelationId TEXT NOT NULL CHECK(length(CommandCorrelationId)=36),
            CommandKind INTEGER NOT NULL CHECK(CommandKind IN (42,43,44)),
            Phase INTEGER NOT NULL CHECK(Phase IN (1,2,3,4,5,6,7,8,9)),
            Restoration INTEGER NOT NULL CHECK(Restoration IN (1,2,3,4,5)),
            Terminal INTEGER NOT NULL CHECK(Terminal IN (0,1)),
            ManualRunId TEXT NULL CHECK(ManualRunId IS NULL OR length(ManualRunId)=36),
            OutcomeContentHash TEXT NULL CHECK(OutcomeContentHash IS NULL OR length(OutcomeContentHash)=64),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            ActorAuthorizationRevision INTEGER NOT NULL CHECK(ActorAuthorizationRevision>=0),
            AuthorizationTarget TEXT NOT NULL CHECK(length(AuthorizationTarget)=64),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0),
            RecordedAtUtc TEXT NOT NULL,
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64),
            UNIQUE(SessionId,Position));
        CREATE INDEX ix_manual_inspection_events_session ON manual_inspection_events(SessionId,Position);
        CREATE INDEX ix_manual_inspection_events_run ON manual_inspection_events(ManualRunId,Position);
        CREATE INDEX ix_manual_inspection_events_audit ON manual_inspection_events(AuditSequence);
        CREATE TRIGGER manual_inspection_config_immutable_update BEFORE UPDATE
            ON manual_inspection_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableManualInspectionConfiguration');
        END;
        CREATE TRIGGER manual_inspection_config_immutable_delete BEFORE DELETE
            ON manual_inspection_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableManualInspectionConfiguration');
        END;
        CREATE TRIGGER manual_inspection_event_immutable_update BEFORE UPDATE
            ON manual_inspection_events BEGIN
            SELECT RAISE(ABORT,'ImmutableManualInspectionEvent');
        END;
        CREATE TRIGGER manual_inspection_event_immutable_delete BEFORE DELETE
            ON manual_inspection_events BEGIN
            SELECT RAISE(ABORT,'ImmutableManualInspectionEvent');
        END;";

    internal static void InitializeManualInspectionSchema(sqlite3 database,
        ManualInspectionStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, ManualInspectionSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO manual_inspection_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            ManualInspectionStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendManualInspectionStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredManualInspections(sqlite3 database,
        ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM manual_inspection_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4) ?? string.Empty)).ToArray();
        AuditChainDatabase.Require(configured.Length == 1 &&
            configured[0].FormatVersion == ManualInspectionStoreOptions.FormatVersion &&
            configured[0].MaximumEntries == options.MaximumEntries &&
            configured[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configured[0].BindingHash == options.BindingHash,
            "ManualInspectionConfigurationMismatch");
    }

    internal static void VerifyManualInspectionActivationPayload(sqlite3 database,
        byte[] payload, ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "ManualInspectionActivationBindingMismatch");
        RequireConfiguredManualInspections(database, options, deadline);
    }

    /// <summary>Canonical row projection shared by the command writer and read-only query.</summary>
    internal sealed record ManualInspectionStoredRow(long Position, string? PreviousHash,
        byte[] Payload, ManualInspectionSessionEvent Event, ManualInspectionRunRecord? Run);

    /// <summary>
    /// Reads only the canonical payload column.  Scalar columns are checked
    /// against the decoded value so a tampered projection cannot be silently
    /// accepted by a cold query.
    /// </summary>
    internal static List<ManualInspectionStoredRow> ReadManualInspectionRows(sqlite3 database,
        ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredManualInspections(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,SessionId,RuntimeEpoch,StartCorrelationId,AttemptId,
                CommandCorrelationId,CommandKind,Phase,Restoration,Terminal,ManualRunId,
                OutcomeContentHash,ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,
                AuthorizationTarget,ReasonCode,RecordedAtUtc,ContentHash,PayloadHash,Payload,
                CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash,AuditSequence,AuditHash
            FROM manual_inspection_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var previous = SqliteNative.ColumnText(statement, 1);
            var encoded = SqliteNative.ColumnText(statement, 21);
            AuditChainDatabase.Require(encoded is { Length: > 0 } &&
                encoded.Length <= checked(options.MaximumPayloadBytes * 2),
                "ManualInspectionPayloadCapacityExceeded");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded!);
                AuditChainDatabase.Require(Convert.ToBase64String(payload) == encoded,
                    "ManualInspectionPayloadCanonicalMismatch");
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("ManualInspectionPayloadInvalid", exception); }
            AuditChainDatabase.Require(payload.Length is > 0 && payload.Length <= options.MaximumPayloadBytes,
                "ManualInspectionPayloadCapacityExceeded");
            var decoded = ManualInspectionStorageCodec.Decode(payload);
            var value = decoded.Event;
            AuditChainDatabase.Require(value.Position == position &&
                value.SessionId == ParseManualGuid(SqliteNative.ColumnText(statement, 2)) &&
                value.RuntimeEpoch == ParseManualGuid(SqliteNative.ColumnText(statement, 3)) &&
                value.Header.StartCorrelationId == ParseManualGuid(SqliteNative.ColumnText(statement, 4)) &&
                value.AttemptId == ParseManualGuid(SqliteNative.ColumnText(statement, 5)) &&
                value.CommandCorrelationId == ParseManualGuid(SqliteNative.ColumnText(statement, 6)) &&
                (int)value.CommandKind == SqliteNative.ColumnInt64(statement, 7) &&
                (int)value.Phase == SqliteNative.ColumnInt64(statement, 8) &&
                (int)value.Restoration == SqliteNative.ColumnInt64(statement, 9) &&
                (value.Terminal ? 1 : 0) == SqliteNative.ColumnInt64(statement, 10) &&
                value.ManualRunId == ParseNullableManualGuid(SqliteNative.ColumnText(statement, 11)) &&
                value.OutcomeContentHash == SqliteNative.ColumnText(statement, 12) &&
                value.ActorPrincipalId == ParseManualGuid(SqliteNative.ColumnText(statement, 13)) &&
                value.ActorSessionId == ParseManualGuid(SqliteNative.ColumnText(statement, 14)) &&
                value.ActorAuthorizationRevision == SqliteNative.ColumnInt64(statement, 15) &&
                value.AuthorizationTarget == SqliteNative.ColumnText(statement, 16) &&
                value.ReasonCode == SqliteNative.ColumnText(statement, 17) &&
                value.RecordedAtUtc == ParseManualTime(SqliteNative.ColumnText(statement, 18)) &&
                value.ContentHash == SqliteNative.ColumnText(statement, 19) &&
                value.PayloadHash == SqliteNative.ColumnText(statement, 20) &&
                value.CommandAuditSequence == SqliteNative.ColumnInt64Nullable(statement, 22) &&
                value.CommandAuditHash == SqliteNative.ColumnText(statement, 23) &&
                value.AuthorizationAuditSequence == SqliteNative.ColumnInt64Nullable(statement, 24) &&
                value.AuthorizationAuditHash == SqliteNative.ColumnText(statement, 25) &&
                value.AuditSequence == SqliteNative.ColumnInt64(statement, 26) &&
                value.AuditHash == SqliteNative.ColumnText(statement, 27),
                "ManualInspectionEventColumnBindingMismatch");
            AuditChainDatabase.Require(value.PayloadHash == ManualInspectionStorageCodec.PayloadHash(
                EncodeManualAuditPayload(value, decoded.Run)), "ManualInspectionPayloadHashMismatch");
            return new ManualInspectionStoredRow(position, previous, payload, value, decoded.Run);
        }).ToList();
        string? previousHash = null;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.PreviousHash == previousHash,
                "ManualInspectionPreviousHashMismatch");
            previousHash = row.Event.AuditHash;
        }
        return rows;
    }

    internal static void VerifyManualInspectionAuditPayload(sqlite3 database, long position,
        byte[] auditPayload, ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditPayload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var row = ReadManualInspectionRows(database, options, deadline)
            .SingleOrDefault(value => value.Position == position);
        AuditChainDatabase.Require(row is not null, "ManualInspectionAuditBindingMissing");
        var expected = EncodeManualAuditPayload(row!.Event, row.Run);
        AuditChainDatabase.Require(expected.SequenceEqual(auditPayload) &&
            ManualInspectionStorageCodec.PayloadHash(auditPayload) == row.Event.PayloadHash,
            "ManualInspectionAuditBindingMismatch");
    }

    internal ValueTask<IdentityWriteResult> UpdateManualInspectionCommandAsync(
        ManualInspectionCommand command,
        Func<IdentityAuthorityState, ManualInspectionCommandState,
            ManualInspectionAdmissionInput?, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline? deadline = null,
        Func<IdentityAuthorityState, ManualInspectionCommandState, ManualInspectionCommand,
            ManualInspectionSessionHeader, string?>? replayAuthorization = null,
        bool allowReplay = true)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(update);
        if (command.CorrelationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "CorrelationIdRequired"));
        if (_options.ManualInspections is null)
            return ValueTask.FromResult(new IdentityWriteResult(false,
                "ManualInspectionConfigurationRequired"));
        return EnqueueIdentityAsync(new IdentityWork(command, update, replayAuthorization, allowReplay),
            cancellationToken, deadline);
    }

    internal ManualInspectionCommandState ReadManualInspectionCommandState(
        sqlite3 database, ManualInspectionCommand command, StoreDeadline deadline)
    {
        var options = _options.ManualInspections;
        if (options is null)
            return new(false, null, Array.Empty<ManualInspectionSessionEvent>(),
                Array.Empty<ManualInspectionRunRecord>(), null, null, null, null, false);
        options.Validate();
        RequireConfiguredManualInspections(database, options, deadline);
        var rows = ReadManualInspectionRows(database, options, deadline);
        var sessionId = ManualCommandSessionId(command);
        var events = sessionId is Guid requestedSession
            ? rows.Where(value => value.Event.SessionId == requestedSession)
                .Select(value => value.Event).ToArray()
            : command is StartManualInspectionSessionCommand startCommand
                ? rows.Where(value => value.Event.Header.StartCorrelationId == startCommand.CorrelationId)
                    .Select(value => value.Event).ToArray()
                : Array.Empty<ManualInspectionSessionEvent>();
        var current = events.LastOrDefault();
        var pending = rows.Select(value => value.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => value.Header.IsActive || value.Header.RecoveryRequired ||
                value.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
                value.Restoration == ManualInspectionRestorationState.RecoveryBlocked)
            .OrderBy(value => value.Position).LastOrDefault()?.Header;

        var selection = current?.Header.Selection ?? command switch
        {
            StartManualInspectionSessionCommand selectionCommand => selectionCommand.Selection,
            _ => null
        };
        (RecipeDraftRevision? Draft, RecipeReleaseRecord? Release) source = selection is null
            ? (null, null)
            : ReadManualSource(database, selection, deadline);
        var draft = source.Draft;
        var release = source.Release;
        var role = current?.Header.CurrentBinding.LogicalRole ?? draft?.Content.CameraRole;
        var camera = role is null ? null : ReadCameraSetupState(database, role, deadline);
        var expectedActive = command switch
        {
            StartManualInspectionSessionCommand expectedActiveCommand => expectedActiveCommand.ExpectedActive,
            _ => current?.Header.ActiveActivation
        };
        var active = ReadManualActiveBaseline(database, expectedActive, deadline);
        var recoveryRequired = rows.Any(value => value.Event.Header.RecoveryRequired ||
            value.Event.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
            value.Event.Restoration == ManualInspectionRestorationState.RecoveryBlocked);

        CommandAuditFact? startFact = null;
        var startEvent = events.FirstOrDefault(value =>
            value.CommandKind == AuditedCommandKind.StartManualInspectionSession);
        if (startEvent is not null)
            startFact = ReadFact(database, startEvent.AttemptId, 1, deadline);

        var pendingSessionId = current?.SessionId ?? sessionId;
        var pendingFacts = pendingSessionId is Guid currentSession
            ? ReadManualPendingCommandFacts(database,
                rows.Where(value => value.Event.SessionId == currentSession), deadline)
            : Array.Empty<CommandAuditFact>();
        return new ManualInspectionCommandState(true, current?.Header, events,
            rows.Where(value => sessionId is Guid id && value.Event.SessionId == id && value.Run is not null)
                .Select(value => value.Run!).ToArray(), draft, release, camera, active,
            recoveryRequired, pending, startFact, pendingFacts);
    }

    /// <summary>
    /// A retry can carry a fresh caller attempt while reusing a correlation
    /// that already admitted a Manual command.  Once that command has a
    /// terminal ledger event, return the durable result and do not invoke the
    /// authority callback again.  An in-flight admission is deliberately left
    /// to the completion callback: the runtime uses the same correlation for
    /// its bounded progress/terminal continuations.
    /// </summary>
    private IdentityUpdate? TryReadManualInspectionReplay(sqlite3 database,
        IdentityAuthorityState identity, ManualInspectionCommandState state,
        ManualInspectionCommand command, StoreDeadline deadline,
        Func<IdentityAuthorityState, ManualInspectionCommandState, ManualInspectionCommand,
            ManualInspectionSessionHeader, string?>? replayAuthorization)
    {
        var options = _options.ManualInspections;
        if (options is null) return null;
        var attempts = AuditChainDatabase.Read(database, @"
            SELECT AttemptId FROM command_attempts
            WHERE CorrelationId=? AND OutcomeDisposition=0
            ORDER BY AttemptId LIMIT 2;", deadline,
            statement => ParseManualGuid(SqliteNative.ColumnText(statement, 0)),
            command.CorrelationId.ToString("D"));
        var facts = attempts.Select(attempt => ReadFact(database, attempt, 1, deadline))
            .Where(value => value is not null && value.Phase == CommandAuditPhase.Outcome &&
                value.Disposition == CommandDisposition.Accepted)
            .Select(value => value!).ToArray();
        if (facts.Length == 0) return null;

        var rows = ReadManualInspectionRows(database, options, deadline);
        var commandKind = ManualCommandKind(command);
        var exactFacts = facts.Where(value => value.CommandKind == commandKind).ToArray();
        var commandRows = rows.Where(value => value.Event.CommandCorrelationId == command.CorrelationId)
            .OrderBy(value => value.Position).ToArray();
        if (facts.Length != 1 || exactFacts.Length != 1 || commandRows.Length == 0)
            return ManualInspectionReplayRejected(command, "ManualInspectionDuplicateCorrelationMismatch",
                exactFacts.FirstOrDefault()?.AttemptId);

        var fact = exactFacts[0];
        var exactRows = commandRows.Where(value => value.Event.AttemptId == fact.AttemptId &&
            value.Event.CommandKind == commandKind).ToArray();
        var latest = exactRows.LastOrDefault();
        if (latest is null || latest.Event.CommandCorrelationId != fact.CorrelationId ||
            latest.Event.RuntimeEpoch != fact.RuntimeEpoch ||
            latest.Event.AuthorizationTarget != command.AuthorizationTarget ||
            latest.Event.ActorPrincipalId.ToString("D") != fact.AuthenticatedHumanPrincipalId ||
            latest.Event.ActorSessionId != command.Invocation.SessionId ||
            fact.Source != command.Invocation.Source ||
            fact.ClaimedPrincipalId != command.Invocation.PrincipalId ||
            fact.ClaimedSessionId != command.Invocation.SessionId ||
            fact.ClaimedStepUpGrantId != command.Invocation.StepUpGrantId)
            return ManualInspectionReplayRejected(command, "ManualInspectionDuplicateCorrelationMismatch",
                fact.AttemptId);

        var terminal = ReadFact(database, fact.AttemptId, 2, deadline);
        if (terminal is not null)
        {
            AuditChainDatabase.Require(terminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
                terminal.Disposition is null && SameCommandContext(fact, terminal),
                "ManualInspectionReplayTerminalContextMismatch");
            if (latest.Event.Terminal)
                AuditChainDatabase.Require(terminal.ReasonCode == latest.Event.ReasonCode,
                    "ManualInspectionReplayTerminalReasonMismatch");
        }
        if (latest.Event.Terminal && terminal is null)
            return ManualInspectionReplayRejected(command, "ManualInspectionCommandTerminalMissing", fact.AttemptId);
        if (!latest.Event.Terminal && terminal is null) return null;
        if (commandKind == AuditedCommandKind.RunManualInspection && latest.Run is null)
            return ManualInspectionReplayRejected(command, "ManualInspectionDuplicateCorrelationMismatch", fact.AttemptId);

        if (replayAuthorization is not null)
        {
            var rejection = replayAuthorization(identity, state, command, latest.Event.Header);
            if (rejection is { Length: > 0 } && rejection != "Authorized")
                return ManualInspectionReplayRejected(command, rejection, fact.AttemptId);
        }

        var succeeded = terminal?.Phase == CommandAuditPhase.Failed
            ? false : !IsManualInspectionTerminalFailure(latest.Event, latest.Run);
        var outcome = new RuntimeCommandOutcome(command.CorrelationId,
            succeeded ? CommandDisposition.Accepted : CommandDisposition.Rejected,
            terminal?.ReasonCode ?? latest.Event.ReasonCode, AuditPersistence.Persisted, fact.AttemptId);
        return new IdentityUpdate(new ManualInspectionCommandResult(outcome,
            latest.Event.Header, latest.Event, latest.Run, fact),
            Array.Empty<IdentityAuditEvent>(), NoMutation: true);
    }

    private static bool IsManualInspectionTerminalFailure(ManualInspectionSessionEvent value,
        ManualInspectionRunRecord? run) => value.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
        value.Restoration == ManualInspectionRestorationState.RecoveryBlocked ||
        value.CommandKind == AuditedCommandKind.RunManualInspection &&
        run?.Status is not ManualInspectionRunStatus.Completed;

    private static IdentityUpdate ManualInspectionReplayRejected(ManualInspectionCommand command,
        string reason, Guid? attemptId) => new(new ManualInspectionCommandResult(
            new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected,
                reason, AuditPersistence.NotAttempted, attemptId)),
        Array.Empty<IdentityAuditEvent>(), NoMutation: true);

    internal async ValueTask<ManualInspectionRecoveryState> ReadManualInspectionRecoveryStateAsync(
        CancellationToken cancellationToken)
    {
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        var options = _options.ManualInspections;
        if (!initialized.Committed || options is null || _policy is null || _signingKey is null)
            return new(false, initialized.ReasonCode is { Length: > 0 } reason ? reason :
                "ManualInspectionStoreUnavailable");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var pathReason))
            return new(false, pathReason);
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(path, readOnly: true);
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            ManualInspectionStoreOptions.ConfigureSqliteLimit(database);
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline,
                cancellationToken);
            var committed = false;
            try
            {
                var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
                AuditChainDatabase.Require(schema is ManualInspectionStoreOptions.SchemaVersion or
                    ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion,
                    schema < ManualInspectionStoreOptions.SchemaVersion
                        ? "ManualInspectionGovernedMigrationRequired" : "StoreSchemaTooNew");
                var verification = AuditChainDatabase.Verify(database, _policy,
                    _signingKey.KeyId, _signingKey.PublicKeyBase64,
                    new AuditVerificationRequest(0, _policy.MaximumVerificationEntries),
                    startup: false, deadline, validateAnchorReceipt: false,
                    archiveOptions: _options.AlgorithmResultArchive,
                    recipeDraftOptions: _options.RecipeDrafts,
                    cameraSetupOptions: _options.CameraSetup,
                    cameraRecoveryOptions: _options.CameraRecovery,
                    cameraNetworkOptions: _options.CameraNetwork,
                    imagingSetupOptions: _options.ImagingSetup,
                    calibrationSessionOptions: _options.CalibrationSessions,
                    governanceOptions: _options.CalibrationGovernance,
                    releaseOptions: _options.RecipeReleases,
                    contractOptions: _options.PlcResultContracts,
                     activationOptions: _options.RecipeActivations,
                     previewOptions: _options.PreviewSessions,
                     importOptions: _options.CalibrationImports,
                     manualOptions: options,
                     productionAdmissionOptions: _options.ProductionAdmission,
                stationQualificationOptions: _options.StationQualifications,
                recipeTransferOptions: _options.RecipeTransfers,
                traceStoragePolicyOptions: _options.TraceStoragePolicies,
                qualificationCycleOptions: _options.QualificationCycles);
            RecipeTransferReadGuard.RequireVerified(database, verification, deadline, _options);
            TraceStoragePolicyReadGuard.RequireVerified(database, verification, deadline, _options);
                RequireStationQualificationWriteSnapshot(database, verification, deadline);
                AuditChainDatabase.RequireFullManualInspectionVerification(database,
                    verification, deadline, options);
                if (_options.ProductionAdmission is not null)
                    AuditChainDatabase.RequireFullProductionAdmissionVerification(database, verification, deadline,
                        _options.ProductionAdmission);
                var rows = ReadManualInspectionRows(database, options, deadline);
                var projection = BuildManualRecoveryProjection(database, rows, deadline);
                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                committed = true;
                return projection;
            }
            finally
            {
                if (!committed)
                    try { raw.sqlite3_exec(database, "ROLLBACK;"); } catch { }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private ManualInspectionRecoveryState BuildManualRecoveryProjection(sqlite3 database,
        IReadOnlyList<ManualInspectionStoredRow> rows, StoreDeadline deadline)
    {
        var latest = rows.Select(value => value.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => value.Header.IsActive || value.Header.RecoveryRequired ||
                value.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
                value.Restoration == ManualInspectionRestorationState.RecoveryBlocked)
            .OrderBy(value => value.Position).LastOrDefault();
        if (latest is null)
            return new(true, "ManualInspectionIdle", Events: rows.Select(value => value.Event).ToArray(),
                Runs: rows.Where(value => value.Run is not null).Select(value => value.Run!).ToArray(),
                PendingCommandFacts: Array.Empty<CommandAuditFact>(), RecoveryRequired: false);
        var sessionEvents = rows.Where(value => value.Event.SessionId == latest.SessionId)
            .Select(value => value.Event).ToArray();
        var start = sessionEvents.FirstOrDefault(value =>
            value.CommandKind == AuditedCommandKind.StartManualInspectionSession);
        var startFact = start is null ? null : ReadFact(database, start.AttemptId, 1, deadline);
        var pendingFacts = ReadManualPendingCommandFacts(database,
            rows.Where(value => value.Event.SessionId == latest.SessionId), deadline);
        var (draft, release) = ReadManualSource(database, latest.Header.Selection, deadline);
        var active = ReadManualActiveBaseline(database, latest.Header.ActiveActivation, deadline);
        return new(true, latest.Header.RecoveryRequired || latest.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
            latest.Restoration == ManualInspectionRestorationState.RecoveryBlocked
                ? "ManualInspectionRecoveryRequired" : "ManualInspectionPending", latest.Header, startFact,
            draft, release, active, sessionEvents,
            rows.Where(value => value.Event.SessionId == latest.SessionId && value.Run is not null)
                .Select(value => value.Run!).ToArray(), pendingFacts,
            latest.Header.RecoveryRequired || latest.Phase == ManualInspectionSessionPhase.RecoveryBlocked ||
            latest.Restoration == ManualInspectionRestorationState.RecoveryBlocked);
    }

    private (RecipeDraftRevision? Draft, RecipeReleaseRecord? Release) ReadManualSource(
        sqlite3 database, ManualRecipeSelection selection, StoreDeadline deadline)
    {
        var draftOptions = _options.RecipeDrafts ??
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (selection.Kind == ManualRecipeSourceKind.Draft)
        {
            var reference = new PreviewDraftReference(selection.DraftId!.Value,
                selection.DraftRevision!.Value, selection.DraftRevisionContentHash!);
            return (ReadRecipeDraftRevisionByReference(database, reference, deadline, draftOptions), null);
        }
        var releaseOptions = _options.RecipeReleases ??
            throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
        var release = ReadRecipeReleaseRows(database, releaseOptions, deadline)
            .Where(value => value.Record.ReleaseId == selection.ReleaseId &&
                value.Record.ContentHash == selection.ReleaseRecordContentHash &&
                value.Record.Recipe == selection.Recipe)
            .Select(value => value.Record).SingleOrDefault();
        AuditChainDatabase.Require(release is not null, "ManualInspectionReleaseMissing");
        return (release!.Source, release);
    }

    private static IReadOnlyList<CommandAuditFact> ReadManualPendingCommandFacts(
        sqlite3 database, IEnumerable<ManualInspectionStoredRow> rows, StoreDeadline deadline)
    {
        var pending = new Dictionary<Guid, CommandAuditFact>();
        foreach (var row in rows)
        {
            if (row.Event.Terminal) continue;
            var accepted = ReadFact(database, row.Event.AttemptId, 1, deadline);
            if (accepted is null || accepted.Phase != CommandAuditPhase.Outcome ||
                accepted.Disposition != CommandDisposition.Accepted ||
                ReadFact(database, accepted.AttemptId, 2, deadline) is not null)
                continue;
            pending[accepted.EventId] = accepted;
        }
        return pending.Values.ToArray();
    }

    private RecipeActivationRecord? ReadManualActiveBaseline(sqlite3 database,
        RecipeActivationReference? expected, StoreDeadline deadline)
    {
        if (expected is null) return null;
        var activation = ReadRecipeActivationCommandState(database, deadline);
        var record = activation.Records.SingleOrDefault(value => value.Reference == expected &&
            value.Outcome.Succeeded && value.SuccessfulSnapshot is not null);
        AuditChainDatabase.Require(record is not null, "ManualInspectionActiveBaselineMissing");
        return record;
    }

    private static Guid? ManualCommandSessionId(ManualInspectionCommand command) => command switch
    {
        RunManualInspectionCommand run => run.SessionId,
        ExitManualInspectionSessionCommand exit => exit.SessionId,
        ManualInspectionContinuationCommand continuation => continuation.SessionId,
        _ => null
    };

    private void AppendManualInspectionIdentityMutation(sqlite3 database,
        IdentityUpdate update, ManualInspectionCommandState state,
        ManualInspectionCommand command, IdentityWork work, StoreDeadline deadline)
    {
        var mutation = update.ManualInspection ??
            throw new InvalidOperationException("ManualInspectionMutationMissing");
        var options = _options.ManualInspections ??
            throw new InvalidOperationException("ManualInspectionConfigurationRequired");
        options.Validate();
        AuditChainDatabase.Require(state.Enabled && update.Events.Count == 1,
            "ManualInspectionMutationMismatch");
        var rows = ReadManualInspectionRows(database, options, deadline);
        var futureRows = ManualInspectionFutureReserve(state, mutation).Rows;
        AuditChainDatabase.Require(checked((long)rows.Count + 1 + futureRows) <= options.MaximumEntries,
            "ManualInspectionEntryCapacityExceeded");

        var value = mutation.Event;
        var expectedKind = ManualCommandKind(command);
        AuditChainDatabase.Require(value.CommandKind == expectedKind &&
            value.CommandCorrelationId == command.CorrelationId &&
            value.Header.SessionId == value.SessionId &&
            value.Header.RuntimeEpoch == value.RuntimeEpoch &&
            value.AuthorizationTarget == command.AuthorizationTarget,
            "ManualInspectionMutationMismatch");

        // Manual ledger rows always bind to the accepted outcome fact. A
        // completion fact may be the current IdentityUpdate fact, so resolve
        // aggregate sequence 1 from the exact command attempt instead of
        // accidentally binding a terminal continuation.
        var commandReference = ReadManualCommandAuditReference(database, value, deadline);
        var authorizationReference = ReadIdentityAuditReferenceByEventId(database,
            update.Events[0].EventId, deadline) ??
            throw new InvalidOperationException("ManualInspectionAuthorizationAuditMissing");
        var position = checked(rows.Count + 1L);
        var previousHash = rows.Count == 0 ? null : rows[^1].Event.AuditHash;
        var noAuditRun = mutation.Run is null ? null : CloneManualRun(mutation.Run,
            commandReference.Sequence, commandReference.Hash, 0, null);
        var referenced = NewManualEvent(value, position, commandReference.Sequence,
            commandReference.Hash, authorizationReference.Sequence,
            authorizationReference.Hash, payloadHash: null, auditSequence: 0,
            auditHash: null, outcomeContentHash: noAuditRun?.ContentHash ?? value.OutcomeContentHash);
        var payloadHash = ManualInspectionStorageCodec.PayloadHash(
            EncodeManualAuditPayload(referenced, noAuditRun));
        var beforeAudit = NewManualEvent(referenced, position, commandReference.Sequence,
            commandReference.Hash, authorizationReference.Sequence,
            authorizationReference.Hash, payloadHash, 0, null,
            outcomeContentHash: referenced.OutcomeContentHash);
        var auditPayload = EncodeManualAuditPayload(beforeAudit, noAuditRun);
        var auditSequence = AuditChainDatabase.AppendManualInspectionLedgerEntry(database,
            _policy!, _signingKey!, position, auditPayload, options, deadline);
        var auditHash = AuditChainDatabase.Read(database,
            "SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND ManualInspectionPosition=? LIMIT 2;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            auditSequence.ToString(CultureInfo.InvariantCulture), ManualInspectionEventKind,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(auditHash is { Length: 64 },
            "ManualInspectionAuditEntryMissing");

        var persistedRun = mutation.Run is null ? null : CloneManualRun(mutation.Run,
            commandReference.Sequence, commandReference.Hash, auditSequence, auditHash);
        var persisted = NewManualEvent(beforeAudit, position,
            commandReference.Sequence, commandReference.Hash,
            authorizationReference.Sequence, authorizationReference.Hash,
            payloadHash, auditSequence, auditHash,
            outcomeContentHash: beforeAudit.OutcomeContentHash);
        var payload = ManualInspectionStorageCodec.Encode(persisted, persistedRun);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "ManualInspectionPayloadCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        var reservedBytes = checked((long)futureRows * options.MaximumPayloadBytes);
        AuditChainDatabase.Require(checked(total + payload.Length + reservedBytes) <= options.MaximumTotalBytes,
            "ManualInspectionTotalCapacityExceeded");
        AuditChainDatabase.Execute(database, @"
            INSERT INTO manual_inspection_events(
                Position,PreviousHash,SessionId,RuntimeEpoch,StartCorrelationId,AttemptId,
                CommandCorrelationId,CommandKind,Phase,Restoration,Terminal,ManualRunId,
                OutcomeContentHash,ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,
                AuthorizationTarget,ReasonCode,RecordedAtUtc,ContentHash,PayloadHash,Payload,
                CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), previousHash,
            persisted.SessionId.ToString("D"), persisted.RuntimeEpoch.ToString("D"),
            persisted.Header.StartCorrelationId.ToString("D"), persisted.AttemptId.ToString("D"),
            persisted.CommandCorrelationId.ToString("D"), ((int)persisted.CommandKind).ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Phase).ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Restoration).ToString(CultureInfo.InvariantCulture),
            persisted.Terminal ? "1" : "0", persisted.ManualRunId?.ToString("D"),
            persisted.OutcomeContentHash, persisted.ActorPrincipalId.ToString("D"),
            persisted.ActorSessionId.ToString("D"),
            persisted.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            persisted.AuthorizationTarget, persisted.ReasonCode,
            persisted.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            persisted.ContentHash, persisted.PayloadHash!, Convert.ToBase64String(payload),
            persisted.CommandAuditSequence!.Value.ToString(CultureInfo.InvariantCulture),
            persisted.CommandAuditHash!, persisted.AuthorizationAuditSequence!.Value.ToString(CultureInfo.InvariantCulture),
            persisted.AuthorizationAuditHash!, persisted.AuditSequence.ToString(CultureInfo.InvariantCulture),
            persisted.AuditHash!);

        AppendAdditionalManualTerminalFacts(database, mutation.AdditionalTerminalFacts,
            state, value, deadline);
        if (update.Result is ManualInspectionCommandResult result)
            work.Result = result with { Header = persisted.Header, Event = persisted,
                Run = persistedRun, CommandFact = update.CommandFacts?.FirstOrDefault() };
    }

    private static AuditedCommandKind ManualCommandKind(ManualInspectionCommand command) => command switch
    {
        StartManualInspectionSessionCommand => AuditedCommandKind.StartManualInspectionSession,
        RunManualInspectionCommand => AuditedCommandKind.RunManualInspection,
        ExitManualInspectionSessionCommand => AuditedCommandKind.ExitManualInspectionSession,
        ManualInspectionContinuationCommand continuation => continuation.OriginalCommandKind,
        _ => throw new InvalidOperationException("ManualInspectionCommandKindInvalid")
    };

    private static ManualInspectionSessionEvent NewManualEvent(
        ManualInspectionSessionEvent value, long position, long commandSequence,
        string commandHash, long authorizationSequence, string authorizationHash,
        string? payloadHash, long auditSequence, string? auditHash,
        string? outcomeContentHash) => new(position, value.Header, value.AttemptId,
            value.CommandCorrelationId, value.CommandKind, value.Phase, value.Restoration,
            value.ReasonCode, value.Terminal, value.ActorPrincipalId,
            value.ActorSessionId, value.ActorAuthorizationRevision,
            value.AuthorizationTarget, value.RecordedAtUtc, value.ManualRunId,
            outcomeContentHash, commandSequence, commandHash, authorizationSequence,
            authorizationHash, payloadHash, auditSequence, auditHash);

    private static byte[] EncodeManualAuditPayload(ManualInspectionSessionEvent value,
        ManualInspectionRunRecord? run)
    {
        var commandSequence = value.CommandAuditSequence ??
            throw new InvalidOperationException("ManualInspectionCommandAuditMissing");
        var commandHash = value.CommandAuditHash ??
            throw new InvalidOperationException("ManualInspectionCommandAuditMissing");
        var authorizationSequence = value.AuthorizationAuditSequence ??
            throw new InvalidOperationException("ManualInspectionAuthorizationAuditMissing");
        var authorizationHash = value.AuthorizationAuditHash ??
            throw new InvalidOperationException("ManualInspectionAuthorizationAuditMissing");
        // PayloadHash is the digest of this audit-free representation. Keep
        // it out of the representation itself so verification has no
        // self-referential hash cycle.
        var strippedEvent = NewManualEvent(value, value.Position, commandSequence,
            commandHash, authorizationSequence, authorizationHash, payloadHash: null,
            0, null, value.OutcomeContentHash);
        var strippedRun = run is null ? null : CloneManualRun(run,
            run.CommandAuditSequence, run.CommandAuditHash, 0, null);
        return ManualInspectionStorageCodec.Encode(strippedEvent, strippedRun);
    }

    private static ManualInspectionRunRecord CloneManualRun(ManualInspectionRunRecord value,
        long? commandSequence, string? commandHash, long auditSequence, string? auditHash) =>
        new(value.Position, value.RunId, value.SessionId, value.RuntimeEpoch,
            value.CommandCorrelationId, value.AttemptId, value.PartIdentity, value.Status,
            value.Decision, value.ExecutionStatus, value.ReasonCode, value.AdmittedAtUtc,
            value.StartedAtUtc, value.CompletedAtUtc, value.FrameMetadata,
            value.FrameProvenance, value.AlgorithmResultPayload,
            value.AlgorithmResultContentHash, value.Evidence,
            commandAuditSequence: commandSequence, commandAuditHash: commandHash,
            auditSequence: auditSequence, auditHash: auditHash,
            partIdentitySource: value.PartIdentitySource,
            partIdentityActorPrincipalId: value.PartIdentityActorPrincipalId,
            partIdentityActorSessionId: value.PartIdentityActorSessionId,
            preparedInstanceId: value.PreparedInstanceId, algorithm: value.Algorithm,
            configuration: value.Configuration,
            configurationContentHash: value.ConfigurationContentHash,
            configurationSchemaId: value.ConfigurationSchemaId,
            configurationSchemaVersion: value.ConfigurationSchemaVersion,
            configurationSchemaContentHash: value.ConfigurationSchemaContentHash,
            resultSchema: value.ResultSchema, result: value.Result,
            frameOverlay: value.FrameOverlay, timing: value.Timing,
            admittedMonotonicTimestamp: value.AdmittedMonotonicTimestamp,
            monotonicFrequency: value.MonotonicFrequency,
            droppedDiagnosticCount: value.DroppedDiagnosticCount,
            resultProjectionContentHash: value.ResultProjectionContentHash,
            frameOverlayContentHash: value.FrameOverlayContentHash,
            resultSchemaContentHash: value.ResultSchemaContentHash);

    private static (long Rows, long Audit) ManualInspectionFutureReserve(ManualInspectionCommandState state,
        ManualInspectionMutation mutation)
    {
        if (!mutation.Header.IsActive) return (0, 0);
        var actions = state.Events.Append(mutation.Event).GroupBy(value => value.AttemptId)
            .Select(group => group.Last()).Where(value => !value.Terminal).ToArray();
        var runs = actions.LongCount(value => value.CommandKind == AuditedCommandKind.RunManualInspection);
        var exits = actions.LongCount(value => value.CommandKind == AuditedCommandKind.ExitManualInspectionSession);
        // Cold recovery may close each pending Exit and then the original Start.
        // Keep that capacity in addition to any unfinished Run. Before an Exit
        // exists, also reserve its admission and terminal lifecycle.
        var sessionRows = exits > 0 ? exits + 1 :
            mutation.Header.Phase == ManualInspectionSessionPhase.Restoring ? 1 : 3;
        return (checked(runs + sessionRows), checked((runs + sessionRows) * 3));
    }

    private void EnsureManualInspectionTransactionCapacity(sqlite3 database, IdentityUpdate update,
        ManualInspectionCommandState state, StoreDeadline deadline)
    {
        var mutation = update.ManualInspection!;
        var future = ManualInspectionFutureReserve(state, mutation);
        var writes = checked(update.Events.Count + (update.CommandFacts?.Count ?? 0) + 1 +
            (mutation.AdditionalTerminalFacts?.Count ?? 0));
        AuditChainDatabase.EnsureManualInspectionTransactionCapacity(database, _policy!,
            writes, future.Audit, deadline);
    }

    internal static long ReadManualInspectionAuditReserve(sqlite3 database, StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "manual_inspection_events", deadline)) return 0;
        var current = AuditChainDatabase.Read(database, @"
            SELECT e.SessionId,e.Phase FROM manual_inspection_events e
            WHERE e.Position=(SELECT MAX(s.Position) FROM manual_inspection_events s WHERE s.SessionId=e.SessionId)
              AND e.Phase NOT IN (8,9);", deadline,
            statement => (Session: SqliteNative.ColumnText(statement, 0), Phase: SqliteNative.ColumnInt64(statement, 1)));
        long reserve = 0;
        foreach (var session in current)
        {
            var pending = AuditChainDatabase.Read(database, @"
                SELECT e.CommandKind FROM manual_inspection_events e WHERE e.SessionId=? AND e.CommandKind IN (43,44)
                  AND e.Position=(SELECT MAX(a.Position) FROM manual_inspection_events a WHERE a.AttemptId=e.AttemptId)
                  AND e.Terminal=0;", deadline, statement => SqliteNative.ColumnInt64(statement, 0), session.Session);
            var runs = pending.LongCount(kind => kind == (long)AuditedCommandKind.RunManualInspection);
            var exits = pending.LongCount(kind => kind == (long)AuditedCommandKind.ExitManualInspectionSession);
            var sessionRows = exits > 0 ? exits + 1 :
                session.Phase == (long)ManualInspectionSessionPhase.Restoring ? 1 : 3;
            reserve = checked(reserve + (runs + sessionRows) * 3);
        }
        return reserve;
    }

    private static ManualAuditReference ReadManualCommandAuditReference(sqlite3 database,
        ManualInspectionSessionEvent value, StoreDeadline deadline)
    {
        var reference = AuditChainDatabase.Read(database, @"
            SELECT e.Sequence,e.Hash,f.AttemptId,f.CorrelationId,f.RuntimeEpoch,f.CommandKind
            FROM audit_entries e JOIN command_facts f ON f.Position=e.FactPosition
            WHERE e.Kind='CommandFact' AND f.AttemptId=? AND f.AggregateSequence=1
              AND f.CorrelationId=? AND f.CommandKind=? LIMIT 2;", deadline,
            statement => new ManualAuditReference(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParseManualGuid(SqliteNative.ColumnText(statement, 2)),
                ParseManualGuid(SqliteNative.ColumnText(statement, 3)),
                ParseManualGuid(SqliteNative.ColumnText(statement, 4)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 5)),
            value.AttemptId.ToString("D"), value.CommandCorrelationId.ToString("D"),
            ((int)value.CommandKind).ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(reference.Sequence > 0 && reference.Hash.Length == 64 &&
            reference.AttemptId == value.AttemptId && reference.CorrelationId == value.CommandCorrelationId &&
            reference.RuntimeEpoch == value.RuntimeEpoch && reference.CommandKind == value.CommandKind,
            "ManualInspectionCommandAuditMissing");
        return reference;
    }

    private void AppendAdditionalManualTerminalFacts(sqlite3 database,
        IReadOnlyList<CommandAuditFact>? facts, ManualInspectionCommandState state,
        ManualInspectionSessionEvent eventValue, StoreDeadline deadline)
    {
        if (facts is null || facts.Count == 0) return;
        AuditChainDatabase.Require(facts.Count == 1 && state.StartCommandFact is { } start &&
            eventValue.Terminal && facts[0].AttemptId == start.AttemptId &&
            facts[0].CorrelationId == start.CorrelationId &&
            facts[0].CommandKind == AuditedCommandKind.StartManualInspectionSession,
            "ManualInspectionAdditionalTerminalLimit");
        foreach (var fact in facts)
        {
            ValidateFact(fact);
            AuditChainDatabase.Require(fact.Phase is CommandAuditPhase.Completed or
                CommandAuditPhase.Failed && fact.Disposition is null,
                "ManualInspectionAdditionalTerminalInvalid");
            var attempt = ReadAttempt(database, fact.AttemptId, deadline);
            AuditChainDatabase.Require(attempt is not null &&
                attempt.Value.OutcomeDisposition == CommandDisposition.Accepted &&
                attempt.Value.Matches(fact), "ManualInspectionAdditionalTerminalContextMismatch");
            var existing = ReadFact(database, fact.AttemptId, 2, deadline);
            if (existing is not null)
            {
                AuditChainDatabase.Require(existing.EventId == fact.EventId &&
                    existing.Phase == fact.Phase && existing.ReasonCode == fact.ReasonCode &&
                    existing.OccurredAtUtc == fact.OccurredAtUtc &&
                    existing.CommandKind == fact.CommandKind,
                    "ManualInspectionAdditionalTerminalConflict");
                continue;
            }
            AuditChainDatabase.Require(!Exists(database,
                "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", fact.EventId, deadline),
                "DuplicateEventId");
            InsertFact(database, fact, 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, fact.EventId,
                deadline, manualInspectionReserveOverride: 0);
        }
    }

    private readonly record struct ManualAuditReference(long Sequence, string Hash,
        Guid AttemptId, Guid CorrelationId, Guid RuntimeEpoch, AuditedCommandKind CommandKind);

    private static Guid ParseManualGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty ? parsed :
        throw new InvalidOperationException("ManualInspectionGuidInvalid");

    private static Guid? ParseNullableManualGuid(string? value) => value is null ? null : ParseManualGuid(value);

    private static DateTimeOffset ParseManualTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
            out var parsed) && parsed.Offset == TimeSpan.Zero ? parsed :
        throw new InvalidOperationException("ManualInspectionTimestampInvalid");
}
