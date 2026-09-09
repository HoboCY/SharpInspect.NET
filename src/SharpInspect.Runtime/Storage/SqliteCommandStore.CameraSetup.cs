using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema 10 camera setup storage.  Camera facts are kept in their own immutable
/// stream, while the main audit chain carries the signed CameraPosition binding for
/// every row.  The only mutation path is the existing identity writer transaction.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string CameraSetupSchemaSql = @"
        CREATE TABLE camera_setup_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEvents INTEGER NOT NULL CHECK(MaximumEvents>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            MaximumPendingOperations INTEGER NOT NULL CHECK(MaximumPendingOperations>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE camera_setup_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            LogicalRole TEXT NOT NULL CHECK(length(LogicalRole)>0 AND length(LogicalRole)<=64),
            CommandKind INTEGER NOT NULL CHECK(CommandKind IN (15,16)),
            Phase INTEGER NOT NULL CHECK(Phase IN (0,1,2,3)),
            BindingRevision INTEGER NOT NULL CHECK(BindingRevision>=0),
            PreviousRevisionHash TEXT NULL CHECK(PreviousRevisionHash IS NULL OR length(PreviousRevisionHash)=64),
            RevisionHash TEXT NULL CHECK(RevisionHash IS NULL OR length(RevisionHash)=64),
            PreviousTargetHash TEXT NULL CHECK(PreviousTargetHash IS NULL OR length(PreviousTargetHash)=64),
            TargetHash TEXT NULL CHECK(TargetHash IS NULL OR length(TargetHash)=64),
            Succeeded INTEGER NOT NULL CHECK(Succeeded IN (0,1)),
            ReasonCode TEXT NOT NULL CHECK(length(ReasonCode)>0 AND length(ReasonCode)<=128),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0 AND length(ChangeReason)<=256),
            ActorPrincipalId TEXT NULL CHECK(ActorPrincipalId IS NULL OR length(ActorPrincipalId)=36),
            SessionId TEXT NULL CHECK(SessionId IS NULL OR length(SessionId)=36),
            AuthorAuthorizationRevision INTEGER NOT NULL CHECK(AuthorAuthorizationRevision>=0),
            RecordedAtUtc TEXT NOT NULL,
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            UNIQUE(OperationId,Phase));
        CREATE INDEX ix_camera_setup_role_position ON camera_setup_events(LogicalRole,Position);
        CREATE INDEX ix_camera_setup_operation ON camera_setup_events(OperationId,Position);
        CREATE TRIGGER camera_setup_config_immutable_update BEFORE UPDATE ON camera_setup_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraSetupConfiguration');
        END;
        CREATE TRIGGER camera_setup_config_immutable_delete BEFORE DELETE ON camera_setup_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraSetupConfiguration');
        END;
        CREATE TRIGGER camera_setup_event_immutable_update BEFORE UPDATE ON camera_setup_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraSetupEvent');
        END;
        CREATE TRIGGER camera_setup_event_immutable_delete BEFORE DELETE ON camera_setup_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCameraSetupEvent');
        END;";

    internal ValueTask<CameraSetupReadResult> ReadCameraSetupAsync(string logicalRole,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(logicalRole)) throw new ArgumentException("CameraSetupLogicalRoleInvalid", nameof(logicalRole));
        return new ValueTask<CameraSetupReadResult>(ReadCameraSetupCoreAsync(logicalRole, cancellationToken));
    }

    internal ValueTask<IdentityWriteResult> UpdateCameraSetupAsync(Guid operationId, string logicalRole,
        Func<IdentityAuthorityState, CameraSetupStoreSnapshot, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline? deadline = null)
    {
        if (operationId == Guid.Empty) return ValueTask.FromResult(new IdentityWriteResult(false, "OperationIdRequired"));
        if (string.IsNullOrEmpty(logicalRole)) return ValueTask.FromResult(new IdentityWriteResult(false, "CameraSetupLogicalRoleInvalid"));
        ArgumentNullException.ThrowIfNull(update);
        if (!CameraSetupEnabled) return ValueTask.FromResult(new IdentityWriteResult(false, "CameraSetupUnavailable"));
        return EnqueueIdentityAsync(new IdentityWork(operationId, logicalRole, update), cancellationToken, deadline);
    }

    /// <summary>Compatibility overload for callers that derive the role inside their callback.</summary>
    internal ValueTask<IdentityWriteResult> UpdateCameraSetupAsync(Guid operationId,
        Func<IdentityAuthorityState, CameraSetupStoreSnapshot, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline? deadline = null) =>
        UpdateCameraSetupAsync(operationId, string.Empty, update, cancellationToken, deadline);

    private async Task<CameraSetupReadResult> ReadCameraSetupCoreAsync(string logicalRole,
        CancellationToken cancellationToken)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || !CameraSetupEnabled || _databasePath is null || _policy is null || _signingKey is null)
            throw new InvalidOperationException(initialized.ReasonCode);
        using var connection = SqliteNative.Open(_databasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
        var database = connection.Handle!;
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        try
        {
            var verification = AuditChainDatabase.Verify(database, _policy, _signingKey.KeyId,
                _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(0, _policy.MaximumVerificationEntries), startup: true,
                deadline, validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery,
                cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                releaseOptions: _options.RecipeReleases,
                contractOptions: _options.PlcResultContracts,
                activationOptions: _options.RecipeActivations);
            AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline, _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                    _options.CameraRecovery);
            if (_options.CameraNetwork is not null)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                    _options.CameraNetwork);
            if (_options.ImagingSetup is not null)
                AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                    _options.ImagingSetup);
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
            var state = ReadCameraSetupState(database, logicalRole, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return new CameraSetupReadResult(state.ToPublicResult(), state);
        }
        catch
        {
            try { AuditChainDatabase.Execute(database, "ROLLBACK;", deadline); } catch { }
            throw;
        }
    }

    private void InitializeCameraSetupSchema(sqlite3 database, CameraSetupStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Execute(database, @"INSERT INTO camera_setup_store_config
            (Id,FormatVersion,MaximumEvents,MaximumPayloadBytes,MaximumTotalBytes,MaximumPendingOperations,BindingHash)
            VALUES(1,?,?,?,?,?,?);", deadline, "1",
            options.MaximumEvents.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumPendingOperations.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        _ = AuditChainDatabase.AppendCameraSetupActivation(database, _policy!, _signingKey!, options, deadline);
    }

    internal static void RequireConfiguredCameraSetup(sqlite3 database, CameraSetupStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        var configs = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumEvents,
            MaximumPayloadBytes,MaximumTotalBytes,MaximumPendingOperations,BindingHash
            FROM camera_setup_store_config WHERE Id=1;", deadline,
            statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEvents: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                MaximumPendingOperations: SqliteNative.ColumnInt64(statement, 4),
                BindingHash: SqliteNative.ColumnText(statement, 5)!)).ToArray();
        AuditChainDatabase.Require(configs.Length == 1 && configs[0].FormatVersion == CameraSetupStoreOptions.FormatVersion &&
            configs[0].MaximumEvents == options.MaximumEvents && configs[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configs[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configs[0].MaximumPendingOperations == options.MaximumPendingOperations &&
            configs[0].BindingHash == options.BindingHash, "CameraSetupConfigurationMismatch");
    }

    internal static void VerifyCameraSetupActivationPayload(sqlite3 database, byte[] payload,
        CameraSetupStoreOptions options, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(payload.SequenceEqual(options.EncodeActivationPayload()),
            "CameraSetupActivationMismatch");
        RequireConfiguredCameraSetup(database, options, deadline);
    }

    internal static byte[] ReadAndValidateCameraSetup(sqlite3 database, long position, byte[] auditPayload,
        StoreDeadline deadline, CameraSetupStoreOptions options)
    {
        var row = ReadCameraSetupRows(database, "WHERE Position=? LIMIT 1", deadline,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "CameraSetupEventMissing");
        var payload = DecodePayload(row!.Payload, position);
        AuditChainDatabase.Require(payload.SequenceEqual(auditPayload), "CameraSetupBindingMismatch");
        var value = CameraSetupStorageCodec.Decode(payload, position);
        ValidateCameraSetupRow(row, value);
        return payload;
    }

    internal static void ValidateCameraSetupHistory(sqlite3 database, CameraSetupStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredCameraSetup(database, options, deadline);
        var rows = ReadCameraSetupRows(database, "ORDER BY Position", deadline);
        var expectedPosition = 1L;
        var byOperation = new Dictionary<Guid, CameraSetupEvent>();
        var byRole = new Dictionary<string, CameraSetupEvent?>(StringComparer.Ordinal);
        var seenOperations = new HashSet<Guid>();
        var seenEvents = new HashSet<Guid>();
        var values = new List<CameraSetupEvent>(rows.Count);
        var totalBytes = 0L;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == expectedPosition++, "CameraSetupPositionGap");
            AuditChainDatabase.Require(Guid.TryParseExact(row.EventId, "D", out var eventId) &&
                eventId != Guid.Empty && seenEvents.Add(eventId), "CameraSetupEventConflict");
            var payload = DecodePayload(row.Payload, row.Position);
            totalBytes = checked(totalBytes + payload.Length);
            AuditChainDatabase.Require(totalBytes <= options.MaximumTotalBytes, "CameraSetupTotalCapacityExceeded");
            var value = CameraSetupStorageCodec.Decode(payload, row.Position);
            ValidateCameraSetupRow(row, value);
            ValidateCameraSetupTransition(value, byOperation, byRole, seenOperations);
            values.Add(value);
        }
        AuditChainDatabase.Require(rows.Count <= options.MaximumEvents, "CameraSetupEventCapacityExceeded");
        ValidateCameraAuthorizationHistory(database, values, options, byOperation.Count,
            byOperation.Values.Select(item => item.LogicalRole).ToHashSet(StringComparer.Ordinal), deadline,
            checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline)));
    }

    private static void ValidateCameraAuthorizationHistory(sqlite3 database,
        IReadOnlyList<CameraSetupEvent> cameraEvents, CameraSetupStoreOptions options,
        int cameraPendingCount, IReadOnlySet<string> cameraPendingRoles, StoreDeadline deadline,
        int schemaVersion)
    {
        var facts = ReadCameraAuthorizationFacts(database, deadline, schemaVersion);
        var stationId = facts.StationId;
        var authorizations = facts.Authorizations;
        var commandRows = facts.Commands;

        var cameraOperations = cameraEvents.Select(item => item.OperationId).ToHashSet();
        foreach (var cameraEvent in cameraEvents)
        {
            var expectedIdentityKind = cameraEvent.Phase == CameraSetupEventPhase.Admission
                ? IdentityEventKind.CameraSetupActionAuthorized
                : IdentityEventKind.CameraSetupOperationCompleted;
            var identity = authorizations.SingleOrDefault(item =>
                item.Kind == expectedIdentityKind && item.OperationId == cameraEvent.OperationId &&
                item.ReasonCode == cameraEvent.ReasonCode);
            AuditChainDatabase.Require(identity is not null &&
                identity.PrincipalId == cameraEvent.ActorPrincipalId &&
                identity.ActorPrincipalId == cameraEvent.ActorPrincipalId &&
                identity.CommandCorrelationId == cameraEvent.OperationId &&
                identity.BoundCommandCorrelationId == cameraEvent.OperationId &&
                identity.StepUpGrantId is not null &&
                identity.RequiredPermission == Permission.ManageCameraBindings.ToString() &&
                identity.ActionTargetId == cameraEvent.LogicalRole &&
                identity.CommandKind == cameraEvent.CommandKind &&
                identity.SessionId == cameraEvent.SessionId &&
                identity.AuthorizationRevision == cameraEvent.AuthorAuthorizationRevision &&
                identity.ReasonCode == cameraEvent.ReasonCode,
                "CameraSetupAuthorizationBindingMismatch");

            var expectedPhase = cameraEvent.Phase == CameraSetupEventPhase.Admission
                ? CommandAuditPhase.Outcome
                : cameraEvent.Succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed;
            var command = commandRows.SingleOrDefault(item => item.CorrelationId == cameraEvent.OperationId &&
                item.CommandKind == cameraEvent.CommandKind && item.Phase == expectedPhase &&
                item.ReasonCode == cameraEvent.ReasonCode &&
                (cameraEvent.Phase != CameraSetupEventPhase.Admission ||
                    item.Disposition == CommandDisposition.Accepted));
            AuditChainDatabase.Require(command is not null &&
                command.ClaimedPrincipalId == cameraEvent.ActorPrincipalId &&
                command.ClaimedSessionId == cameraEvent.SessionId &&
                command.AuthenticatedHumanPrincipalId == cameraEvent.ActorPrincipalId &&
                command.ReasonCode == cameraEvent.ReasonCode &&
                (cameraEvent.Phase != CameraSetupEventPhase.Admission ||
                    command.Disposition == CommandDisposition.Accepted) &&
                (cameraEvent.Phase == CameraSetupEventPhase.Admission || command.Disposition is null) &&
                (identity!.StepUpGrantId == command.ClaimedStepUpGrantId),
                "CameraSetupCommandBindingMismatch");

            if (cameraEvent.Phase != CameraSetupEventPhase.Admission)
            {
                // A terminal/rebind completion inherits the original admission's
                // exact actor, session, credential authorization revision, grant,
                // command kind and operation. Rebind has no camera admission row,
                // so this identity/command pair is its durable admission record.
                var admissionIdentity = authorizations.SingleOrDefault(item =>
                    item.Kind == IdentityEventKind.CameraSetupActionAuthorized &&
                    item.OperationId == cameraEvent.OperationId);
                var admissionCommand = commandRows.SingleOrDefault(item =>
                    item.CorrelationId == cameraEvent.OperationId &&
                    item.CommandKind == cameraEvent.CommandKind &&
                    item.Phase == CommandAuditPhase.Outcome &&
                    item.Disposition == CommandDisposition.Accepted);
                AuditChainDatabase.Require(admissionIdentity is not null && admissionCommand is not null &&
                    admissionCommand.Disposition == CommandDisposition.Accepted &&
                    SameCameraAuthorization(admissionIdentity, identity!) &&
                    SameCameraCommand(admissionCommand, command!),
                    "CameraSetupAdmissionBindingMismatch");
            }
        }

        var pendingAdmissions = FindPendingRebindAdmissions(authorizations, commandRows,
            cameraOperations);
        AuditChainDatabase.Require(checked(cameraPendingCount + pendingAdmissions.Count) <=
            options.MaximumPendingOperations,
            "CameraSetupPendingCapacityExceeded");
        AuditChainDatabase.Require(pendingAdmissions.GroupBy(item => item.LogicalRole)
            .All(group => group.Count() == 1), "CameraSetupPendingConflict");
        AuditChainDatabase.Require(pendingAdmissions.All(item =>
            !cameraPendingRoles.Contains(item.LogicalRole)), "CameraSetupPendingConflict");

        // A rebind admission is represented by its signed identity/command
        // admission pair; the camera stream gets a row only when the hardware
        // operation completes.  A pre-audit rejection is also a legitimate
        // identity attempt, but it must carry a fixed rejection reason and must
        // never look like an accepted authorization.
        foreach (var identity in authorizations)
        {
            var hasCameraEvent = cameraEvents.Any(item => item.OperationId == identity.OperationId &&
                (identity.Kind == (item.Phase == CameraSetupEventPhase.Admission
                    ? IdentityEventKind.CameraSetupActionAuthorized
                    : IdentityEventKind.CameraSetupOperationCompleted)));
            if (hasCameraEvent) continue;

            if (IsCameraRejectedAttempt(identity, commandRows)) continue;

            var rebindTerminal = identity.Kind == IdentityEventKind.CameraSetupActionAuthorized
                ? cameraEvents.SingleOrDefault(item => item.OperationId == identity.OperationId &&
                    item.CommandKind == AuditedCommandKind.RebindCamera &&
                    item.Phase is CameraSetupEventPhase.Completed or CameraSetupEventPhase.Rejected)
                : null;
            if (rebindTerminal is not null)
            {
                var terminalIdentity = authorizations.SingleOrDefault(item =>
                    item.Kind == IdentityEventKind.CameraSetupOperationCompleted &&
                    item.OperationId == identity.OperationId &&
                    item.ReasonCode == rebindTerminal.ReasonCode);
                var admissionCommand = commandRows.SingleOrDefault(item =>
                    item.CorrelationId == identity.OperationId &&
                    item.CommandKind == AuditedCommandKind.RebindCamera &&
                    item.Phase == CommandAuditPhase.Outcome &&
                    item.Disposition == CommandDisposition.Accepted);
                var terminalCommand = commandRows.SingleOrDefault(item =>
                    item.CorrelationId == identity.OperationId &&
                    item.CommandKind == AuditedCommandKind.RebindCamera &&
                    item.Phase == (rebindTerminal.Succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed));
                AuditChainDatabase.Require(terminalIdentity is not null && admissionCommand is not null &&
                    terminalCommand is not null && admissionCommand.Disposition == CommandDisposition.Accepted &&
                    SameCameraAuthorization(identity, terminalIdentity) &&
                    SameCameraCommand(admissionCommand, terminalCommand) &&
                    terminalCommand.Disposition is null,
                    "CameraSetupAdmissionBindingMismatch");
                continue;
            }

            var relatedCommands = commandRows.Where(item => item.CorrelationId == identity.OperationId).ToArray();
            if (IsCameraPreAuditRejection(identity, relatedCommands)) continue;

            AuditChainDatabase.Require(identity.Kind == IdentityEventKind.CameraSetupActionAuthorized &&
                !cameraOperations.Contains(identity.OperationId) &&
                identity.CommandKind == AuditedCommandKind.RebindCamera &&
                relatedCommands.Any(item => item.CommandKind == AuditedCommandKind.RebindCamera &&
                    item.Phase == CommandAuditPhase.Outcome &&
                    item.Disposition == CommandDisposition.Accepted),
                "CameraSetupAuthorizationBindingMismatch");
        }
    }

    private static (string StationId, List<CameraAuthorizationAudit> Authorizations,
        List<CameraCommandAudit> Commands) ReadCameraAuthorizationFacts(sqlite3 database,
        StoreDeadline deadline, int schemaVersion)
    {
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline);
        AuditChainDatabase.Require(stationId is { Length: > 0 },
            "CameraSetupAuthorizationBindingMismatch");

        var identityRows = AuditChainDatabase.Read(database, @"
            SELECT IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL
            ORDER BY IdentityPosition;", deadline, statement =>
        {
            var ordinal = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 1);
            AuditChainDatabase.Require(encoded is { Length: > 0 },
                "CameraSetupAuthorizationBindingMismatch");
            byte[] payload;
            try { payload = Convert.FromBase64String(encoded!); }
            catch (FormatException ex)
            { throw new InvalidOperationException("CameraSetupAuthorizationBindingMismatch", ex); }
            return (Ordinal: ordinal, Payload: payload);
        });
        var authorizations = new List<CameraAuthorizationAudit>();
        foreach (var row in identityRows)
        {
            if (IdentityAuditEvent.TryReadCameraAuthorization(row.Payload, row.Ordinal,
                    stationId!, schemaVersion, out var binding))
            {
                authorizations.Add(binding);
            }
            else if (schemaVersion >= CameraNetworkStoreOptions.SchemaVersion &&
                IdentityAuditEvent.TryReadCameraNetworkAuthorization(row.Payload, row.Ordinal,
                    stationId!, out _))
            {
                // Schema 12 reuses the camera setup identity event kinds for the
                // independent network ledger. Its command kind is 18, so it is
                // deliberately excluded from this schema-10 setup projection.
            }
            else if (schemaVersion >= ImagingSetupStoreOptions.SchemaVersion &&
                IdentityAuditEvent.TryReadImagingSetupAuthorization(row.Payload, row.Ordinal,
                    stationId!, out _))
            {
                // Schema 13 reuses the camera setup identity event envelope for
                // imaging declarations. Its command kind is 19 and belongs to
                // the independent imaging ledger.
            }
            else if (IdentityAuditEvent.TryReadEventKind(row.Payload, out var kind) &&
                kind is IdentityEventKind.CameraSetupActionAuthorized or
                    IdentityEventKind.CameraSetupOperationCompleted)
            {
                AuditChainDatabase.Require(false, "CameraSetupAuthorizationBindingMismatch");
            }
        }

        // Ordinary command facts may carry a free-form claimed-principal value.
        // Only camera command kinds have the strict GUID envelope understood by
        // this verifier.
        var commands = AuditChainDatabase.Read(database, @"
            SELECT AttemptId,CorrelationId,RuntimeEpoch,CommandKind,ClaimedPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId,AuthenticatedHumanPrincipalId,
                Phase,Disposition,ReasonCode FROM command_facts
            WHERE CommandKind IN (15,16)
            ORDER BY Position;", deadline, ReadCameraCommandAudit);
        return (stationId!, authorizations, commands);
    }

    private static List<CameraSetupPendingAdmission> FindPendingRebindAdmissions(
        IReadOnlyList<CameraAuthorizationAudit> authorizations,
        IReadOnlyList<CameraCommandAudit> commands,
        IReadOnlySet<Guid> cameraOperations)
    {
        var pending = new List<CameraSetupPendingAdmission>();
        foreach (var identity in authorizations.Where(item =>
                     item.Kind == IdentityEventKind.CameraSetupActionAuthorized &&
                     item.CommandKind == AuditedCommandKind.RebindCamera &&
                     item.StepUpGrantId is not null &&
                     !cameraOperations.Contains(item.OperationId)))
        {
            // A signed accepted Outcome is the durable admission.  A rejected
            // retry may share the operation id, so it is deliberately ignored
            // here after the accepted row has been matched exactly.
            var accepted = commands.Where(item =>
                item.CorrelationId == identity.OperationId &&
                item.CommandKind == AuditedCommandKind.RebindCamera &&
                item.Phase == CommandAuditPhase.Outcome &&
                item.Disposition == CommandDisposition.Accepted).ToArray();
            if (accepted.Length == 0) continue;
            AuditChainDatabase.Require(accepted.Length == 1,
                "CameraSetupAuthorizationBindingMismatch");
            var command = accepted[0];
            AuditChainDatabase.Require(identity.PrincipalId == identity.ActorPrincipalId &&
                identity.CommandCorrelationId == identity.OperationId &&
                identity.BoundCommandCorrelationId == identity.OperationId &&
                identity.RequiredPermission == Permission.ManageCameraBindings.ToString() &&
                CameraSetupStorageCodec.IsLogicalRole(identity.ActionTargetId) &&
                command.ClaimedPrincipalId == identity.PrincipalId &&
                command.ClaimedSessionId == identity.SessionId &&
                command.ClaimedStepUpGrantId == identity.StepUpGrantId &&
                command.AuthenticatedHumanPrincipalId == identity.ActorPrincipalId &&
                command.ReasonCode == identity.ReasonCode,
                "CameraSetupAuthorizationBindingMismatch");

            // A terminal identity/command without its camera row is not a
            // pending operation.  It is a broken cross-stream commit and must
            // fail closed instead of being projected as recoverable work.
            AuditChainDatabase.Require(!authorizations.Any(item =>
                    item.Kind == IdentityEventKind.CameraSetupOperationCompleted &&
                    item.OperationId == identity.OperationId &&
                    !IsCameraRejectedAttempt(item, commands)) &&
                !commands.Any(item => item.CorrelationId == identity.OperationId &&
                    item.CommandKind == AuditedCommandKind.RebindCamera &&
                    item.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed),
                "CameraSetupAuthorizationBindingMismatch");

            pending.Add(new CameraSetupPendingAdmission(identity.OperationId,
                identity.ActionTargetId, identity.CommandKind, identity.ActorPrincipalId,
                identity.SessionId, identity.StepUpGrantId!.Value,
                identity.AuthorizationRevision, identity.ReasonCode));
        }
        return pending;
    }

    private static bool IsCameraPreAuditRejection(CameraAuthorizationAudit identity,
        IReadOnlyList<CameraCommandAudit> relatedCommands)
    {
        if (identity.ReasonCode is not (
            "AuthorizationUnavailable" or "AuthorizationStale" or "AuthorizationLeaseUnavailable" or
            "AuthorizationReservationUnavailable" or "PermissionDenied" or "SessionInvalid" or
            "SessionMismatch" or "StepUpInvalid" or "CameraSetupPendingConflict" or
            "CameraBindingRevisionConflict" or "CameraSetupOperationPending"))
            return false;
        if (relatedCommands.Count == 0) return true;
        return relatedCommands.All(item => item.CommandKind == identity.CommandKind &&
            item.Phase == CommandAuditPhase.Outcome &&
            item.Disposition == CommandDisposition.Rejected &&
            item.ReasonCode == identity.ReasonCode &&
            item.ClaimedPrincipalId == identity.PrincipalId &&
            item.ClaimedSessionId == identity.SessionId &&
            item.AuthenticatedHumanPrincipalId == identity.ActorPrincipalId);
    }

    private static bool IsCameraRejectedAttempt(CameraAuthorizationAudit identity,
        IReadOnlyList<CameraCommandAudit> allCommands)
    {
        if (identity.ReasonCode is not ("CameraSetupOperationConflict" or
            "AuthorizationUnavailable" or "AuthorizationStale" or
            "AuthorizationLeaseUnavailable" or "AuthorizationReservationUnavailable" or
            "PermissionDenied" or "SessionInvalid" or "SessionMismatch" or
            "StepUpInvalid" or "CameraSetupPendingConflict" or
            "CameraBindingRevisionConflict" or "CameraSetupOperationPending"))
            return false;

        // A repeated operation can legitimately share the correlation with the
        // original accepted attempt.  Validate the rejected attempt itself, but
        // do not require every historical command for the correlation to be
        // rejected; the original accepted outcome remains the durable admission.
        return allCommands.Where(item => item.CorrelationId == identity.OperationId)
            .Any(item => item.CommandKind == identity.CommandKind &&
                item.Phase == CommandAuditPhase.Outcome &&
                item.Disposition == CommandDisposition.Rejected &&
                item.ReasonCode == identity.ReasonCode &&
                item.ClaimedPrincipalId == identity.PrincipalId &&
                item.ClaimedSessionId == identity.SessionId &&
                item.AuthenticatedHumanPrincipalId == identity.ActorPrincipalId);
    }

    private static CameraCommandAudit ReadCameraCommandAudit(sqlite3_stmt statement)
    {
        var attemptId = ParseCameraGuid(SqliteNative.ColumnText(statement, 0));
        var correlationId = ParseCameraGuid(SqliteNative.ColumnText(statement, 1));
        var runtimeEpoch = ParseCameraGuid(SqliteNative.ColumnText(statement, 2));
        var commandKindValue = SqliteNative.ColumnInt64(statement, 3);
        AuditChainDatabase.Require(Enum.IsDefined(typeof(AuditedCommandKind), (int)commandKindValue),
            "CameraSetupCommandBindingMismatch");
        var commandKind = (AuditedCommandKind)commandKindValue;
        var claimedPrincipal = ParseCameraNullableGuid(SqliteNative.ColumnText(statement, 4));
        var claimedSession = ParseCameraNullableGuid(SqliteNative.ColumnText(statement, 5));
        var grant = ParseCameraNullableGuid(SqliteNative.ColumnText(statement, 6));
        var authenticated = ParseCameraNullableGuid(SqliteNative.ColumnText(statement, 7));
        var phaseValue = SqliteNative.ColumnInt64(statement, 8);
        AuditChainDatabase.Require(Enum.IsDefined(typeof(CommandAuditPhase), (int)phaseValue),
            "CameraSetupCommandBindingMismatch");
        var phase = (CommandAuditPhase)phaseValue;
        var dispositionText = SqliteNative.ColumnText(statement, 9);
        CommandDisposition? disposition = null;
        if (dispositionText is not null)
        {
            AuditChainDatabase.Require(long.TryParse(dispositionText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var dispositionValue) &&
                Enum.IsDefined(typeof(CommandDisposition), (int)dispositionValue),
                "CameraSetupCommandBindingMismatch");
            disposition = (CommandDisposition)dispositionValue;
        }
        var reason = SqliteNative.ColumnText(statement, 10);
        AuditChainDatabase.Require(reason is { Length: > 0 }, "CameraSetupCommandBindingMismatch");
        return new CameraCommandAudit(attemptId, correlationId, runtimeEpoch, commandKind,
            claimedPrincipal, claimedSession, grant, authenticated, phase, disposition, reason!);
    }

    private static Guid ParseCameraGuid(string? value)
    {
        AuditChainDatabase.Require(Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty,
            "CameraSetupCommandBindingMismatch");
        return result;
    }

    private static Guid? ParseCameraNullableGuid(string? value)
    {
        if (value is null) return null;
        return ParseCameraGuid(value);
    }

    private static bool SameCameraAuthorization(CameraAuthorizationAudit first,
        CameraAuthorizationAudit second) =>
        first.PrincipalId == second.PrincipalId && first.ActorPrincipalId == second.ActorPrincipalId &&
        first.CommandCorrelationId == second.CommandCorrelationId && first.StepUpGrantId == second.StepUpGrantId &&
        first.RequiredPermission == second.RequiredPermission && first.ActionTargetId == second.ActionTargetId &&
        first.BoundCommandCorrelationId == second.BoundCommandCorrelationId &&
        first.CommandKind == second.CommandKind && first.OperationId == second.OperationId &&
        first.SessionId == second.SessionId && first.AuthorizationRevision == second.AuthorizationRevision;

    private static bool SameCameraCommand(CameraCommandAudit first, CameraCommandAudit second) =>
        first.AttemptId == second.AttemptId && first.CorrelationId == second.CorrelationId &&
        first.RuntimeEpoch == second.RuntimeEpoch && first.CommandKind == second.CommandKind &&
        first.ClaimedPrincipalId == second.ClaimedPrincipalId &&
        first.ClaimedSessionId == second.ClaimedSessionId &&
        first.ClaimedStepUpGrantId == second.ClaimedStepUpGrantId &&
        first.AuthenticatedHumanPrincipalId == second.AuthenticatedHumanPrincipalId;

    private sealed record CameraCommandAudit(
        Guid AttemptId, Guid CorrelationId, Guid RuntimeEpoch, AuditedCommandKind CommandKind,
        Guid? ClaimedPrincipalId, Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId,
        Guid? AuthenticatedHumanPrincipalId, CommandAuditPhase Phase,
        CommandDisposition? Disposition, string ReasonCode);

    private void AppendCameraSetupEvents(sqlite3 database,
        IReadOnlyList<CameraSetupEvent> events, StoreDeadline deadline)
    {
        if (events.Count == 0) return;
        var options = _options.CameraSetup ?? throw new InvalidOperationException("CameraSetupUnavailable");
        options.Validate();
        var count = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM camera_setup_events;", deadline);
        var historyRows = ReadCameraSetupRows(database, "ORDER BY Position", deadline);
        var byOperation = new Dictionary<Guid, CameraSetupEvent>();
        var byRole = new Dictionary<string, CameraSetupEvent?>(StringComparer.Ordinal);
        var seenOperations = new HashSet<Guid>();
        var totalPayloadBytes = 0L;
        foreach (var row in historyRows)
        {
            var rawPayload = DecodePayload(row.Payload, row.Position);
            totalPayloadBytes = checked(totalPayloadBytes + rawPayload.Length);
            AuditChainDatabase.Require(totalPayloadBytes <= options.MaximumTotalBytes,
                "CameraSetupTotalCapacityExceeded");
            var existing = CameraSetupStorageCodec.Decode(rawPayload, row.Position);
            ValidateCameraSetupRow(row, existing);
            ValidateCameraSetupTransition(existing, byOperation, byRole, seenOperations);
        }
        var pending = byOperation.Count;
        foreach (var input in events)
        {
            AuditChainDatabase.Require(input.Position == 0, "CameraSetupPositionMustBeWriterAssigned");
            if (input.Phase == CameraSetupEventPhase.Admission)
                AuditChainDatabase.Require(pending < options.MaximumPendingOperations,
                    "CameraSetupPendingCapacityExceeded");
            AuditChainDatabase.Require(++count <= options.MaximumEvents, "CameraSetupEventCapacityExceeded");
            var position = count;
            var value = input with
            {
                Position = position,
                RecordedAtUtc = input.RecordedAtUtc == default
                    ? DateTimeOffset.UtcNow : input.RecordedAtUtc.ToUniversalTime()
            };
            CameraSetupStorageCodec.ValidateEvent(value, position);
            ValidateCameraSetupTransition(value, byOperation, byRole, seenOperations);
            pending = byOperation.Count;
            AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
                "SELECT COUNT(*) FROM camera_setup_events WHERE OperationId=? AND Phase=?;",
                deadline, value.OperationId.ToString("D"), ((int)value.Phase).ToString(CultureInfo.InvariantCulture)) == 0,
                "CameraSetupOperationConflict");
            var payload = CameraSetupStorageCodec.Encode(value);
            AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
                "CameraSetupPayloadCapacityExceeded");
            totalPayloadBytes = checked(totalPayloadBytes + payload.Length);
            AuditChainDatabase.Require(totalPayloadBytes <= options.MaximumTotalBytes,
                "CameraSetupTotalCapacityExceeded");
            InsertCameraSetupRow(database, value, payload, deadline);
            _ = AuditChainDatabase.AppendCameraSetupEvent(database, _policy!, _signingKey!,
                position, deadline);
        }
    }

    private static void InsertCameraSetupRow(sqlite3 database, CameraSetupEvent value,
        byte[] payload, StoreDeadline deadline)
    {
        const string sql = @"INSERT INTO camera_setup_events(
            Position,EventId,OperationId,LogicalRole,CommandKind,Phase,BindingRevision,
            PreviousRevisionHash,RevisionHash,PreviousTargetHash,TargetHash,Succeeded,
            ReasonCode,ChangeReason,ActorPrincipalId,SessionId,AuthorAuthorizationRevision,
            RecordedAtUtc,Payload,PayloadHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);";
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            var index = 1;
            SqliteNative.BindInt64(database, statement, index++, value.Position);
            SqliteNative.BindGuid(database, statement, index++, value.EventId);
            SqliteNative.BindGuid(database, statement, index++, value.OperationId);
            SqliteNative.BindText(database, statement, index++, value.LogicalRole);
            SqliteNative.BindInt(database, statement, index++, (int)value.CommandKind);
            SqliteNative.BindInt(database, statement, index++, (int)value.Phase);
            SqliteNative.BindInt64(database, statement, index++, value.BindingRevision);
            SqliteNative.BindText(database, statement, index++, value.PreviousRevisionHash);
            SqliteNative.BindText(database, statement, index++, value.RevisionHash);
            SqliteNative.BindText(database, statement, index++, value.PreviousTarget?.ContentHash);
            SqliteNative.BindText(database, statement, index++, value.Target?.ContentHash);
            SqliteNative.BindInt(database, statement, index++, value.Succeeded ? 1 : 0);
            SqliteNative.BindText(database, statement, index++, value.ReasonCode);
            SqliteNative.BindText(database, statement, index++, value.ChangeReason);
            if (value.ActorPrincipalId is { } actor) SqliteNative.BindGuid(database, statement, index++, actor);
            else SqliteNative.BindText(database, statement, index++, null);
            if (value.SessionId is { } session) SqliteNative.BindGuid(database, statement, index++, session);
            else SqliteNative.BindText(database, statement, index++, null);
            SqliteNative.BindInt64(database, statement, index++, value.AuthorAuthorizationRevision);
            SqliteNative.BindText(database, statement, index++, FormatTime(value.RecordedAtUtc));
            SqliteNative.BindText(database, statement, index++, Convert.ToBase64String(payload));
            SqliteNative.BindText(database, statement, index, CameraSetupStorageCodec.PayloadHash(payload));
            SqliteNative.Step(database, statement, deadline);
            return 0;
        });
    }

    internal static bool ReadCameraSetupOperation(sqlite3 database, Guid operationId,
        StoreDeadline deadline)
    {
        var rows = ReadCameraSetupRows(database, "WHERE OperationId=? ORDER BY Position", deadline,
            operationId.ToString("D"));
        foreach (var row in rows)
        {
            var value = CameraSetupStorageCodec.Decode(DecodePayload(row.Payload, row.Position), row.Position);
            ValidateCameraSetupRow(row, value);
        }
        return rows.Count != 0;
    }

    private static void ValidateCameraSetupTransition(CameraSetupEvent value,
        Dictionary<Guid, CameraSetupEvent> byOperation,
        Dictionary<string, CameraSetupEvent?> byRole,
        HashSet<Guid> seenOperations)
    {
        if (value.Phase == CameraSetupEventPhase.Admission)
        {
            AuditChainDatabase.Require(seenOperations.Add(value.OperationId) &&
                !byOperation.ContainsKey(value.OperationId), "CameraSetupOperationConflict");
            AuditChainDatabase.Require(!HasPendingRole(value.LogicalRole, byOperation),
                "CameraSetupPendingConflict");
            AuditChainDatabase.Require(value.Target is not null && value.BindingRevision >= 0,
                "CameraSetupAdmissionInvalid");
            if (value.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration)
                RequireCurrentApplyBinding(value, byRole);
            else
                RequireCurrentBinding(value, byRole, requireTarget: true);
            byOperation[value.OperationId] = value;
            return;
        }

        if (value.Phase == CameraSetupEventPhase.Terminal)
        {
            AuditChainDatabase.Require(byOperation.TryGetValue(value.OperationId, out var admission) &&
                admission is not null && admission.CommandKind == value.CommandKind &&
                admission.LogicalRole == value.LogicalRole && admission.BindingRevision == value.BindingRevision &&
                SameTarget(admission.Target, value.Target) &&
                admission.PreviousRevisionHash == value.PreviousRevisionHash &&
                SameTarget(admission.PreviousTarget, value.PreviousTarget) &&
                SameConfiguration(admission.Requested, value.Requested) &&
                SameExtension(admission.Extension, value.Extension) &&
                admission.ActorPrincipalId == value.ActorPrincipalId && admission.SessionId == value.SessionId &&
                admission.AuthorAuthorizationRevision == value.AuthorAuthorizationRevision &&
                admission.ChangeReason == value.ChangeReason &&
                (!value.Succeeded || admission.RevisionHash == value.RevisionHash) &&
                (value.Succeeded || value.RevisionHash is null), "CameraSetupTerminalWithoutAdmission");
            if (value.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration)
                RequireCurrentApplyBinding(value, byRole);
            byOperation.Remove(value.OperationId);
            return;
        }

        if (value.Phase == CameraSetupEventPhase.Completed && value.CommandKind == AuditedCommandKind.RebindCamera)
        {
            AuditChainDatabase.Require(!HasPendingRole(value.LogicalRole, byOperation),
                "CameraSetupPendingConflict");
            AuditChainDatabase.Require(value.Target is not null && value.RevisionHash is not null,
                "CameraSetupBindingInvalid");
            byRole.TryGetValue(value.LogicalRole, out var previous);
            var expectedRevision = (previous?.BindingRevision ?? 0) + 1;
            AuditChainDatabase.Require(value.BindingRevision == expectedRevision &&
                value.PreviousRevisionHash == previous?.RevisionHash &&
                SameTarget(value.PreviousTarget, previous?.Target), "CameraSetupRevisionConflict");
            var expectedHash = CameraSetupStorageCodec.ComputeRevisionHash(value, expectedRevision,
                previous?.RevisionHash, value.Target!);
            AuditChainDatabase.Require(string.Equals(value.RevisionHash, expectedHash, StringComparison.Ordinal),
                "CameraSetupRevisionHashMismatch");
            AuditChainDatabase.Require(seenOperations.Add(value.OperationId), "CameraSetupOperationConflict");
            byRole[value.LogicalRole] = value;
            return;
        }

        if (value.Phase == CameraSetupEventPhase.Rejected)
        {
            AuditChainDatabase.Require(!HasPendingRole(value.LogicalRole, byOperation),
                "CameraSetupPendingConflict");
            AuditChainDatabase.Require(seenOperations.Add(value.OperationId), "CameraSetupOperationConflict");
            RequireCurrentBinding(value, byRole, requireTarget: false);
            return;
        }

        AuditChainDatabase.Require(false, "CameraSetupEventPhaseInvalid");
    }

    private static void RequireCurrentBinding(CameraSetupEvent value,
        Dictionary<string, CameraSetupEvent?> byRole, bool requireTarget)
    {
        byRole.TryGetValue(value.LogicalRole, out var current);
        AuditChainDatabase.Require(value.BindingRevision == (current?.BindingRevision ?? 0) &&
            value.PreviousRevisionHash == current?.RevisionHash &&
            SameTarget(value.PreviousTarget, current?.Target), "CameraSetupRevisionConflict");
        if (requireTarget)
            AuditChainDatabase.Require(value.Target is not null, "CameraSetupTargetRequired");
    }

    private static void RequireCurrentApplyBinding(CameraSetupEvent value,
        Dictionary<string, CameraSetupEvent?> byRole)
    {
        byRole.TryGetValue(value.LogicalRole, out var current);
        AuditChainDatabase.Require(current is not null && current.Target is not null &&
            value.Target is not null && SameTarget(value.Target, current.Target) &&
            value.PreviousTarget is not null && SameTarget(value.PreviousTarget, current.Target) &&
            value.BindingRevision == current.BindingRevision &&
            value.PreviousRevisionHash == current.RevisionHash,
            "CameraSetupRevisionConflict");
    }

    private static bool HasPendingRole(string logicalRole,
        Dictionary<Guid, CameraSetupEvent> byOperation) =>
        byOperation.Values.Any(item => item.LogicalRole == logicalRole);

    private static bool SameConfiguration(RequestedCameraConfiguration? left,
        RequestedCameraConfiguration? right) => left is null && right is null ||
        left is not null && right is not null &&
        left.ProductionAcquisitionMode == right.ProductionAcquisitionMode &&
        left.ExposureTimeUs == right.ExposureTimeUs && left.GainDb == right.GainDb &&
        left.RegionOfInterest == right.RegionOfInterest && left.PixelFormat == right.PixelFormat &&
        left.ValidBits == right.ValidBits && left.AcquisitionTimeoutMs == right.AcquisitionTimeoutMs &&
        left.TriggerDelayUs == right.TriggerDelayUs && left.WhiteBalanceRgb == right.WhiteBalanceRgb;

    private static bool SameExtension(CameraProviderExtensionRequirement? left,
        CameraProviderExtensionRequirement? right) => left is null && right is null ||
        left is not null && right is not null && left.Provider == right.Provider &&
        left.ContractId == right.ContractId && left.ContractVersion == right.ContractVersion &&
        left.ConfigurationContentHash == right.ConfigurationContentHash;

    private static List<CameraSetupRow> ReadCameraSetupRows(sqlite3 database, string predicate,
        StoreDeadline deadline, params string?[] args) => AuditChainDatabase.Read(database, @"SELECT Position,EventId,
            OperationId,LogicalRole,CommandKind,Phase,BindingRevision,PreviousRevisionHash,RevisionHash,
            PreviousTargetHash,TargetHash,Succeeded,ReasonCode,ChangeReason,ActorPrincipalId,SessionId,
            AuthorAuthorizationRevision,RecordedAtUtc,Payload,PayloadHash FROM camera_setup_events " + predicate + ";",
        deadline, ReadCameraSetupRow, args);

    private static CameraSetupRow ReadCameraSetupRow(sqlite3_stmt statement) => new(
        SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!,
        SqliteNative.ColumnText(statement, 2)!, SqliteNative.ColumnText(statement, 3)!,
        SqliteNative.ColumnInt64(statement, 4), SqliteNative.ColumnInt64(statement, 5),
        SqliteNative.ColumnInt64(statement, 6), SqliteNative.ColumnText(statement, 7),
        SqliteNative.ColumnText(statement, 8), SqliteNative.ColumnText(statement, 9),
        SqliteNative.ColumnText(statement, 10), SqliteNative.ColumnInt64(statement, 11) != 0,
        SqliteNative.ColumnText(statement, 12)!, SqliteNative.ColumnText(statement, 13)!,
        SqliteNative.ColumnText(statement, 14), SqliteNative.ColumnText(statement, 15),
        SqliteNative.ColumnInt64(statement, 16), SqliteNative.ColumnText(statement, 17)!,
        SqliteNative.ColumnText(statement, 18)!, SqliteNative.ColumnText(statement, 19)!);

    private static byte[] DecodePayload(string encoded, long position)
    {
        if (encoded.Length is < 1 or > CameraSetupStorageCodec.MaximumEncodedPayloadChars)
            throw new InvalidOperationException("CameraSetupPayloadOversized");
        byte[] payload;
        try { payload = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new InvalidOperationException("CameraSetupPayloadInvalid", ex); }
        AuditChainDatabase.Require(payload.Length <= CameraSetupStorageCodec.MaximumPayloadBytes,
            "CameraSetupPayloadOversized");
        return payload;
    }

    private static void ValidateCameraSetupRow(CameraSetupRow row, CameraSetupEvent value)
    {
        AuditChainDatabase.Require(Guid.TryParseExact(row.EventId, "D", out var eventId) && eventId == value.EventId &&
            Guid.TryParseExact(row.OperationId, "D", out var operationId) && operationId == value.OperationId &&
            row.LogicalRole == value.LogicalRole && row.CommandKind == (long)value.CommandKind &&
            row.Phase == (long)value.Phase && row.BindingRevision == value.BindingRevision &&
            row.PreviousRevisionHash == value.PreviousRevisionHash && row.RevisionHash == value.RevisionHash &&
            row.PreviousTargetHash == (value.PreviousTarget?.ContentHash) && row.TargetHash == value.Target?.ContentHash &&
            row.Succeeded == value.Succeeded && row.ReasonCode == value.ReasonCode &&
            row.ChangeReason == value.ChangeReason && row.ActorPrincipalId == value.ActorPrincipalId?.ToString("D") &&
            row.SessionId == value.SessionId?.ToString("D") &&
            row.AuthorAuthorizationRevision == value.AuthorAuthorizationRevision &&
            ParseTime(row.RecordedAtUtc) == value.RecordedAtUtc &&
            row.Payload == Convert.ToBase64String(CameraSetupStorageCodec.Encode(value)) &&
            row.PayloadHash == CameraSetupStorageCodec.PayloadHash(CameraSetupStorageCodec.Encode(value)),
            "CameraSetupRowBindingMismatch");
    }

    private static CameraSetupStoreSnapshot ReadCameraSetupState(sqlite3 database, string logicalRole,
        StoreDeadline deadline)
    {
        var rows = ReadCameraSetupRows(database, "ORDER BY Position", deadline);
        var builders = new Dictionary<string, CameraSetupStateBuilder>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var value = CameraSetupStorageCodec.Decode(DecodePayload(row.Payload, row.Position), row.Position);
            ValidateCameraSetupRow(row, value);
            if (!builders.TryGetValue(value.LogicalRole, out var builder))
             {
                 builder = new CameraSetupStateBuilder(value.LogicalRole);
                 builders.Add(value.LogicalRole, builder);
             }
             builder.Apply(value);
        }
        var cameraOperations = rows.Select(row => ParseCameraGuid(row.OperationId)).ToHashSet();
        var authorizationFacts = ReadCameraAuthorizationFacts(database, deadline,
            checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline)));
        var pendingAdmissions = FindPendingRebindAdmissions(authorizationFacts.Authorizations,
            authorizationFacts.Commands, cameraOperations);
        var pendingAdmission = pendingAdmissions.SingleOrDefault(item =>
            item.LogicalRole == logicalRole);
        var pendingOperationCount = builders.Values.Count(static builder => builder.HasPending) +
            pendingAdmissions.Count;
        return builders.TryGetValue(logicalRole, out var found)
            ? found.ToSnapshot(pendingAdmission, pendingOperationCount)
            : new CameraSetupStoreSnapshot(logicalRole, null, null, null, null,
                Array.Empty<CameraConfigurationDifference>(), null, null, Array.Empty<CameraSetupEvent>(),
                pendingAdmission, pendingOperationCount);
    }

    private static bool SameTarget(CameraBindingTarget? left, CameraBindingTarget? right) =>
        left is null && right is null || left is not null && right is not null &&
        left.ContentHash == right.ContentHash && left.Provider == right.Provider &&
        left.StableDeviceIdentity == right.StableDeviceIdentity;

    private sealed record CameraSetupRow(long Position, string EventId, string OperationId, string LogicalRole,
        long CommandKind, long Phase, long BindingRevision, string? PreviousRevisionHash,
        string? RevisionHash, string? PreviousTargetHash, string? TargetHash, bool Succeeded,
        string ReasonCode, string ChangeReason, string? ActorPrincipalId, string? SessionId,
        long AuthorAuthorizationRevision, string RecordedAtUtc, string Payload, string PayloadHash);

    private sealed class CameraSetupStateBuilder
    {
        private readonly List<CameraSetupEvent> _events = new();
        private CameraSetupEvent? _pending;
        private CameraSetupEvent? _bindingEvent;
        private CameraHealthSnapshot? _health;
        private RequestedCameraConfiguration? _requested;
        private EffectiveCameraConfiguration? _effective;
        private IReadOnlyList<CameraConfigurationDifference> _differences = Array.Empty<CameraConfigurationDifference>();
        private CameraProviderExtensionRequirement? _extension;

        internal CameraSetupStateBuilder(string logicalRole) => LogicalRole = logicalRole;
        internal string LogicalRole { get; }
        internal bool HasPending => _pending is not null;

        internal void Apply(CameraSetupEvent value)
        {
            _events.Add(value);
            if (value.Phase == CameraSetupEventPhase.Admission)
            {
                AuditChainDatabase.Require(_pending is null, "CameraSetupPendingConflict");
                _pending = value;
                return;
            }
            if (value.Phase == CameraSetupEventPhase.Terminal)
            {
                AuditChainDatabase.Require(_pending is { OperationId: var id } && id == value.OperationId,
                    "CameraSetupTerminalWithoutAdmission");
                _pending = null;
                if (value.Succeeded)
                {
                    _requested = value.Requested;
                    _effective = value.Effective;
                    _differences = value.Differences;
                    _extension = value.Extension;
                }
                else
                {
                    // A failed Apply is a durable terminal outcome.  Preserve
                    // the request for diagnostics, but never project the prior
                    // successful effective configuration through the failure.
                    // The codec constrains this terminal health to
                    // Closed/Unknown/Stopped before it reaches the builder.
                    _requested = value.Requested;
                    _effective = null;
                    _differences = Array.Empty<CameraConfigurationDifference>();
                    _extension = null;
                }
                if (value.Health is not null) _health = value.Health;
                return;
            }
            if (value.Phase == CameraSetupEventPhase.Completed &&
                value.CommandKind == AuditedCommandKind.RebindCamera && value.Succeeded)
            {
                _bindingEvent = value;
                // Rebinding changes the camera identity. Configuration evidence
                // belongs to the previous binding and must not follow the new
                // target through a restart or a read-back projection.
                _requested = null;
                _effective = null;
                _differences = Array.Empty<CameraConfigurationDifference>();
                _extension = null;
                if (value.Health is not null) _health = value.Health;
            }
            else if (value.Health is not null) _health = value.Health;
        }

        internal CameraSetupStoreSnapshot ToSnapshot(CameraSetupPendingAdmission? pendingAdmission = null,
            int pendingOperationCount = 0)
        {
            CameraBindingRevision? binding = null;
            if (_bindingEvent is { Target: not null, RevisionHash: not null,
                ActorPrincipalId: not null, SessionId: not null } item)
            {
                binding = new CameraBindingRevision(item.Position, item.LogicalRole, item.BindingRevision,
                    item.OperationId, item.PreviousRevisionHash, item.RevisionHash, item.Target,
                    item.ActorPrincipalId.Value, item.SessionId.Value, item.AuthorAuthorizationRevision,
                    item.ChangeReason, item.RecordedAtUtc);
            }
            return new CameraSetupStoreSnapshot(LogicalRole, binding, _health, _requested, _effective,
                new ReadOnlyCollection<CameraConfigurationDifference>(_differences.ToArray()), _extension,
                _pending, new ReadOnlyCollection<CameraSetupEvent>(_events.ToArray()), pendingAdmission,
                pendingOperationCount);
        }
    }
}
