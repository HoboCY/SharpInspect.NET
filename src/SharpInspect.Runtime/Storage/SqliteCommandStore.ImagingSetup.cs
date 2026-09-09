using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Schema 13 append-only imaging setup declaration ledger.</summary>
internal sealed partial class SqliteCommandStore
{
    internal const string ImagingSetupSchemaSql = @"
        CREATE TABLE imaging_setup_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumRevisionCount INTEGER NOT NULL CHECK(MaximumRevisionCount>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE imaging_setup_revisions(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            LogicalRole TEXT NOT NULL CHECK(length(LogicalRole)>0 AND length(LogicalRole)<=64),
            Revision INTEGER NOT NULL CHECK(Revision>0),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PreviousRevisionHash TEXT NULL CHECK(PreviousRevisionHash IS NULL OR length(PreviousRevisionHash)=64),
            RevisionHash TEXT NOT NULL CHECK(length(RevisionHash)=64),
            BindingRevision INTEGER NOT NULL CHECK(BindingRevision>0),
            BindingRevisionHash TEXT NOT NULL CHECK(length(BindingRevisionHash)=64),
            BindingTargetHash TEXT NOT NULL CHECK(length(BindingTargetHash)=64),
            Origin INTEGER NOT NULL CHECK(Origin IN (0,1)),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0 AND length(ChangeReason)<=512),
            RecordedAtUtc TEXT NOT NULL,
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            UNIQUE(LogicalRole,Revision));
        CREATE INDEX ix_imaging_setup_role_position ON imaging_setup_revisions(LogicalRole,Position);
        CREATE INDEX ix_imaging_setup_operation ON imaging_setup_revisions(OperationId,Position);
        CREATE TRIGGER imaging_setup_config_immutable_update BEFORE UPDATE ON imaging_setup_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableImagingSetupConfiguration');
        END;
        CREATE TRIGGER imaging_setup_config_immutable_delete BEFORE DELETE ON imaging_setup_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableImagingSetupConfiguration');
        END;
        CREATE TRIGGER imaging_setup_revision_immutable_update BEFORE UPDATE ON imaging_setup_revisions BEGIN
            SELECT RAISE(ABORT,'ImmutableImagingSetupRevision');
        END;
        CREATE TRIGGER imaging_setup_revision_immutable_delete BEFORE DELETE ON imaging_setup_revisions BEGIN
            SELECT RAISE(ABORT,'ImmutableImagingSetupRevision');
        END;";

    internal ValueTask<ImagingSetupReadResult> ReadImagingSetupAsync(string logicalCameraRole,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(logicalCameraRole))
            throw new ArgumentException("ImagingSetupLogicalRoleInvalid", nameof(logicalCameraRole));
        return new ValueTask<ImagingSetupReadResult>(ReadImagingSetupCoreAsync(logicalCameraRole,
            cancellationToken));
    }

    internal ValueTask<IdentityWriteResult> UpdateImagingSetupAsync(Guid operationId,
        string logicalCameraRole,
        Func<IdentityAuthorityState, CameraSetupStoreSnapshot, ImagingSetupStoreSnapshot, bool,
            IdentityUpdate> update, CancellationToken cancellationToken, StoreDeadline? deadline = null)
    {
        if (operationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "OperationIdRequired"));
        if (string.IsNullOrEmpty(logicalCameraRole))
            return ValueTask.FromResult(new IdentityWriteResult(false, "ImagingSetupLogicalRoleInvalid"));
        ArgumentNullException.ThrowIfNull(update);
        if (!ImagingSetupEnabled || !CameraSetupEnabled)
            return ValueTask.FromResult(new IdentityWriteResult(false, "ImagingSetupUnavailable"));
        return EnqueueIdentityAsync(new IdentityWork(operationId, logicalCameraRole, update),
            cancellationToken, deadline);
    }

    private async Task<ImagingSetupReadResult> ReadImagingSetupCoreAsync(string logicalCameraRole,
        CancellationToken cancellationToken)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || !ImagingSetupEnabled || _databasePath is null ||
            _policy is null || _signingKey is null)
            throw new InvalidOperationException(initialized.ReasonCode);
        using var connection = SqliteNative.Open(_databasePath, true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
        var database = connection.Handle!;
        var deadline = new StoreDeadline(_options.QueryTimeout);
        SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
        try
        {
            var verification = AuditChainDatabase.Verify(database, _policy, _signingKey.KeyId,
                _signingKey.PublicKeyBase64, new AuditVerificationRequest(0,
                    _policy.MaximumVerificationEntries), startup: true, deadline,
                validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance,
                 releaseOptions: _options.RecipeReleases,
                 contractOptions: _options.PlcResultContracts);
            AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                    _options.CameraRecovery);
            if (_options.CameraNetwork is not null)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                    _options.CameraNetwork);
            AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                _options.ImagingSetup);
            if (_options.RecipeReleases is not null)
                AuditChainDatabase.RequireFullRecipeReleaseVerification(database, verification, deadline,
                    _options.RecipeReleases, _options.RecipeDrafts, _options.CalibrationGovernance);
            if (_options.PlcResultContracts is not null)
                AuditChainDatabase.RequireFullPlcResultContractVerification(database, verification, deadline,
                    _options.PlcResultContracts);
            var state = ReadImagingSetupState(database, logicalCameraRole, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return new ImagingSetupReadResult(state);
        }
        catch
        {
            try { AuditChainDatabase.Execute(database, "ROLLBACK;", deadline); } catch { }
            throw;
        }
    }

    private void InitializeImagingSetupSchema(sqlite3 database, ImagingSetupStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Execute(database, @"INSERT INTO imaging_setup_store_config
            (Id,FormatVersion,MaximumRevisionCount,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline, "1",
            options.MaximumRevisionCount.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        _ = AuditChainDatabase.AppendImagingSetupActivation(database, _policy!, _signingKey!, options,
            deadline);
    }

    internal static void RequireConfiguredImagingSetup(sqlite3 database,
        ImagingSetupStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var configs = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumRevisionCount,
            MaximumPayloadBytes,MaximumTotalBytes,BindingHash FROM imaging_setup_store_config WHERE Id=1;",
            deadline, statement => (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumRevisionCount: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4)!)).ToArray();
        AuditChainDatabase.Require(configs.Length == 1 &&
            configs[0].FormatVersion == ImagingSetupStoreOptions.FormatVersion &&
            configs[0].MaximumRevisionCount == options.MaximumRevisionCount &&
            configs[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configs[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configs[0].BindingHash == options.BindingHash, "ImagingSetupConfigurationMismatch");
    }

    internal static void VerifyImagingSetupActivationPayload(sqlite3 database, byte[] payload,
        ImagingSetupStoreOptions options, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(payload.SequenceEqual(options.EncodeActivationPayload()),
            "ImagingSetupActivationMismatch");
        RequireConfiguredImagingSetup(database, options, deadline);
    }

    internal static byte[] ReadAndValidateImagingSetup(sqlite3 database, long position,
        byte[] auditPayload, StoreDeadline deadline, ImagingSetupStoreOptions options)
    {
        var row = ReadImagingSetupRows(database, "WHERE Position=? LIMIT 1", deadline,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "ImagingSetupRevisionMissing");
        var payload = DecodeImagingPayload(row!.Payload);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "ImagingSetupPayloadCapacityExceeded");
        AuditChainDatabase.Require(payload.SequenceEqual(auditPayload), "ImagingSetupBindingMismatch");
        var value = ImagingSetupRevisionStorageCodec.Decode(payload, position);
        ValidateImagingSetupRow(row, value);
        return payload;
    }

    internal static void ValidateImagingSetupHistory(sqlite3 database,
        ImagingSetupStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredImagingSetup(database, options, deadline);
        var rows = ReadImagingSetupRows(database, "ORDER BY Position", deadline);
        AuditChainDatabase.Require(rows.Count <= options.MaximumRevisionCount,
            "ImagingSetupRevisionCapacityExceeded");
        var expectedPosition = 1L;
        var totalBytes = 0L;
        var byRole = new Dictionary<string, ImagingSetupRevision?>(StringComparer.Ordinal);
        var seenOperations = new HashSet<Guid>();
        var values = new List<ImagingSetupRevision>(rows.Count);
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == expectedPosition++, "ImagingSetupPositionGap");
            var payload = DecodeImagingPayload(row.Payload);
            AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
                "ImagingSetupPayloadCapacityExceeded");
            totalBytes = checked(totalBytes + payload.Length);
            AuditChainDatabase.Require(totalBytes <= options.MaximumTotalBytes,
                "ImagingSetupTotalCapacityExceeded");
            var value = ImagingSetupRevisionStorageCodec.Decode(payload, row.Position);
            ValidateImagingSetupRow(row, value);
            AuditChainDatabase.Require(seenOperations.Add(value.OperationId),
                "ImagingSetupOperationConflict");
            byRole.TryGetValue(value.LogicalCameraRole, out var previous);
            AuditChainDatabase.Require(value.Revision == (previous?.Revision ?? 0) + 1 &&
                value.PreviousRevisionHash == previous?.RevisionHash,
                "ImagingSetupRevisionConflict");
            byRole[value.LogicalCameraRole] = value;
            values.Add(value);
        }
        ValidateImagingAuthorizationHistory(database, values, deadline);
    }

    private static void ValidateImagingAuthorizationHistory(sqlite3 database,
        IReadOnlyList<ImagingSetupRevision> revisions, StoreDeadline deadline)
    {
        var stationId = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1;", deadline);
        AuditChainDatabase.Require(stationId is { Length: > 0 },
            "ImagingSetupAuthorizationBindingMismatch");
        var identities = ReadImagingIdentityFacts(database, stationId!, deadline);
        var commands = AuditChainDatabase.Read(database, @"
            SELECT AttemptId,CorrelationId,RuntimeEpoch,CommandKind,ClaimedPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId,AuthenticatedHumanPrincipalId,
                Phase,Disposition,ReasonCode FROM command_facts
            WHERE CommandKind=? ORDER BY Position;", deadline, statement =>
            new ImagingCommandFact(ImagingParseGuid(SqliteNative.ColumnText(statement, 0)),
                ImagingParseGuid(SqliteNative.ColumnText(statement, 1)),
                ImagingParseGuid(SqliteNative.ColumnText(statement, 2)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 3),
                SqliteNative.ColumnText(statement, 4),
                ImagingParseNullableGuid(SqliteNative.ColumnText(statement, 5)),
                ImagingParseNullableGuid(SqliteNative.ColumnText(statement, 6)),
                ImagingParseNullableGuid(SqliteNative.ColumnText(statement, 7)),
                (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 8),
                ParseNullableDisposition(SqliteNative.ColumnText(statement, 9)),
                SqliteNative.ColumnText(statement, 10)!), ((int)AuditedCommandKind.DeclareImagingSetup).ToString(CultureInfo.InvariantCulture));
        foreach (var revision in revisions)
        {
            var action = identities.SingleOrDefault(item => item.Kind == IdentityEventKind.CameraSetupActionAuthorized &&
                item.OperationId == revision.OperationId && item.ReasonCode == "ImagingSetupRevisionPersisted");
            var completed = identities.SingleOrDefault(item => item.Kind == IdentityEventKind.CameraSetupOperationCompleted &&
                item.OperationId == revision.OperationId && item.ReasonCode == "ImagingSetupRevisionPersisted");
            var outcome = commands.SingleOrDefault(item => item.CorrelationId == revision.OperationId &&
                item.Phase == CommandAuditPhase.Outcome && item.Disposition == CommandDisposition.Accepted);
            var terminal = commands.SingleOrDefault(item => item.CorrelationId == revision.OperationId &&
                item.Phase == CommandAuditPhase.Completed && item.Disposition is null);
            AuditChainDatabase.Require(action is not null && completed is not null && outcome is not null &&
                terminal is not null && action.PrincipalId == revision.ActorPrincipalId &&
                action.CommandCorrelationId == revision.OperationId &&
                action.BoundCommandCorrelationId == revision.OperationId &&
                completed.CommandCorrelationId == revision.OperationId &&
                completed.BoundCommandCorrelationId == revision.OperationId &&
                action.StepUpGrantId is { } grantId && grantId != Guid.Empty &&
                completed.StepUpGrantId == grantId &&
                completed.ActionTargetId == action.ActionTargetId &&
                action.ActorPrincipalId == revision.ActorPrincipalId && completed.PrincipalId == revision.ActorPrincipalId &&
                completed.ActorPrincipalId == revision.ActorPrincipalId && action.SessionId == revision.SessionId &&
                completed.SessionId == revision.SessionId && action.AuthorizationRevision == revision.AuthorizationRevision &&
                completed.AuthorizationRevision == revision.AuthorizationRevision &&
                action.RequiredPermission == Permission.ManageCameraBindings.ToString() &&
                completed.RequiredPermission == Permission.ManageCameraBindings.ToString() &&
                action.CommandKind == AuditedCommandKind.DeclareImagingSetup &&
                completed.CommandKind == AuditedCommandKind.DeclareImagingSetup &&
                action.ActionTargetId == ImagingSetupRevisionStorageCodec.ComputeAuthorizationTarget(revision,
                    revision.Binding.Revision, revision.Binding.RevisionHash, revision.Revision - 1,
                    revision.PreviousRevisionHash) &&
                outcome.AuthenticatedHumanPrincipalId == revision.ActorPrincipalId &&
                terminal.AuthenticatedHumanPrincipalId == revision.ActorPrincipalId &&
                outcome.ClaimedSessionId == revision.SessionId && terminal.ClaimedSessionId == revision.SessionId &&
                outcome.ClaimedStepUpGrantId == action.StepUpGrantId && terminal.ClaimedStepUpGrantId == action.StepUpGrantId &&
                outcome.ReasonCode == "ImagingSetupRevisionPersisted" && terminal.ReasonCode == outcome.ReasonCode,
                "ImagingSetupAuthorizationBindingMismatch");
        }
    }

    private static List<CameraAuthorizationAudit> ReadImagingIdentityFacts(sqlite3 database,
        string stationId, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL
            ORDER BY IdentityPosition;", deadline, statement =>
        {
            var ordinal = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 1);
            AuditChainDatabase.Require(encoded is { Length: > 0 },
                "ImagingSetupAuthorizationBindingMismatch");
            try { return (ordinal, Payload: Convert.FromBase64String(encoded!)); }
            catch (FormatException ex)
            { throw new InvalidOperationException("ImagingSetupAuthorizationBindingMismatch", ex); }
        });
        var result = new List<CameraAuthorizationAudit>();
        foreach (var row in rows)
        {
            if (IdentityAuditEvent.TryReadImagingSetupAuthorization(row.Payload, row.ordinal,
                    stationId, out var binding))
                result.Add(binding);
        }
        return result;
    }

    private static List<ImagingSetupRow> ReadImagingSetupRows(sqlite3 database, string predicate,
        StoreDeadline deadline, params string?[] args) => AuditChainDatabase.Read(database, @"SELECT Position,
            LogicalRole,Revision,OperationId,PreviousRevisionHash,RevisionHash,BindingRevision,
            BindingRevisionHash,BindingTargetHash,Origin,ActorPrincipalId,SessionId,AuthorizationRevision,
            ChangeReason,RecordedAtUtc,Payload,PayloadHash FROM imaging_setup_revisions " + predicate + ";",
        deadline, ReadImagingSetupRow, args);

    private static ImagingSetupRow ReadImagingSetupRow(sqlite3_stmt statement) => new(
        SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!,
        SqliteNative.ColumnInt64(statement, 2), SqliteNative.ColumnText(statement, 3)!,
        SqliteNative.ColumnText(statement, 4), SqliteNative.ColumnText(statement, 5)!,
        SqliteNative.ColumnInt64(statement, 6), SqliteNative.ColumnText(statement, 7)!,
        SqliteNative.ColumnText(statement, 8)!, SqliteNative.ColumnInt64(statement, 9),
        SqliteNative.ColumnText(statement, 10)!, SqliteNative.ColumnText(statement, 11)!,
        SqliteNative.ColumnInt64(statement, 12), SqliteNative.ColumnText(statement, 13)!,
        SqliteNative.ColumnText(statement, 14)!, SqliteNative.ColumnText(statement, 15)!,
        SqliteNative.ColumnText(statement, 16)!);

    private static ImagingSetupStoreSnapshot ReadImagingSetupState(sqlite3 database,
        string logicalCameraRole, StoreDeadline deadline)
    {
        var rows = ReadImagingSetupRows(database, "ORDER BY Position", deadline);
        var values = new List<ImagingSetupRevision>();
        var byRole = new Dictionary<string, ImagingSetupRevision?>(StringComparer.Ordinal);
        var expectedPosition = 1L;
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == expectedPosition++, "ImagingSetupPositionGap");
            var value = ImagingSetupRevisionStorageCodec.Decode(DecodeImagingPayload(row.Payload), row.Position);
            ValidateImagingSetupRow(row, value);
            byRole.TryGetValue(value.LogicalCameraRole, out var previous);
            AuditChainDatabase.Require(value.Revision == (previous?.Revision ?? 0) + 1 &&
                value.PreviousRevisionHash == previous?.RevisionHash, "ImagingSetupRevisionConflict");
            byRole[value.LogicalCameraRole] = value;
            if (value.LogicalCameraRole == logicalCameraRole) values.Add(value);
        }
        return new ImagingSetupStoreSnapshot(logicalCameraRole, values.AsReadOnly());
    }

    private static bool ReadImagingSetupOperation(sqlite3 database, Guid operationId,
        StoreDeadline deadline)
    {
        var rows = ReadImagingSetupRows(database, "WHERE OperationId=? ORDER BY Position", deadline,
            operationId.ToString("D"));
        foreach (var row in rows)
        {
            var value = ImagingSetupRevisionStorageCodec.Decode(DecodeImagingPayload(row.Payload), row.Position);
            ValidateImagingSetupRow(row, value);
        }
        return rows.Count != 0;
    }

    private ImagingSetupRevision InsertImagingSetupRevision(sqlite3 database,
        ImagingSetupRevisionMutation mutation, ImagingSetupStoreSnapshot state, StoreDeadline deadline)
    {
        var options = _options.ImagingSetup ?? throw new InvalidOperationException("ImagingSetupUnavailable");
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM imaging_setup_revisions;", deadline));
        var previous = state.Current;
        var value = new ImagingSetupRevision(position, mutation.Change.LogicalCameraRole,
            checked((previous?.Revision ?? 0) + 1), mutation.Change.OperationId,
            previous?.RevisionHash, mutation.Binding, mutation.Change.Definition, mutation.Origin,
            mutation.ActorPrincipalId, mutation.SessionId, mutation.AuthorizationRevision,
            mutation.Change.ChangeReason, DateTimeOffset.UtcNow);
        var payload = ImagingSetupRevisionStorageCodec.Encode(value);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "ImagingSetupPayloadCapacityExceeded");
        // Match the canonical byte count used by history verification; SQLite stores base64 text.
        var total = AuditChainDatabase.Scalar(database, @"SELECT COALESCE(SUM(
            (length(Payload) / 4) * 3 -
            (length(Payload) - length(rtrim(Payload, '=')))), 0)
            FROM imaging_setup_revisions;", deadline);
        AuditChainDatabase.Require(checked(total + payload.Length) <= options.MaximumTotalBytes,
            "ImagingSetupTotalCapacityExceeded");
        AuditChainDatabase.Require(position <= options.MaximumRevisionCount,
            "ImagingSetupRevisionCapacityExceeded");
        AuditChainDatabase.Execute(database, @"INSERT INTO imaging_setup_revisions(
            Position,LogicalRole,Revision,OperationId,PreviousRevisionHash,RevisionHash,
            BindingRevision,BindingRevisionHash,BindingTargetHash,Origin,ActorPrincipalId,SessionId,
            AuthorizationRevision,ChangeReason,RecordedAtUtc,Payload,PayloadHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), value.LogicalCameraRole,
            value.Revision.ToString(CultureInfo.InvariantCulture), value.OperationId.ToString("D"),
            value.PreviousRevisionHash, value.RevisionHash,
            value.Binding.Revision.ToString(CultureInfo.InvariantCulture), value.Binding.RevisionHash,
            value.Binding.Target.ContentHash, ((int)value.Origin).ToString(CultureInfo.InvariantCulture),
            value.ActorPrincipalId.ToString("D"), value.SessionId.ToString("D"),
            value.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), value.ChangeReason,
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), Convert.ToBase64String(payload),
            ImagingSetupRevisionStorageCodec.PayloadHash(payload));
        return value;
    }

    private static byte[] DecodeImagingPayload(string encoded)
    {
        if (encoded.Length is < 1 or > ImagingSetupRevisionStorageCodec.MaximumEncodedPayloadChars)
            throw new InvalidOperationException("ImagingSetupPayloadOversized");
        try
        {
            var payload = Convert.FromBase64String(encoded);
            AuditChainDatabase.Require(payload.Length <= ImagingSetupRevisionStorageCodec.MaximumPayloadBytes,
                "ImagingSetupPayloadOversized");
            return payload;
        }
        catch (FormatException ex) { throw new InvalidOperationException("ImagingSetupPayloadInvalid", ex); }
    }

    private static void ValidateImagingSetupRow(ImagingSetupRow row, ImagingSetupRevision value)
    {
        AuditChainDatabase.Require(row.Position == value.Position && row.LogicalRole == value.LogicalCameraRole &&
            row.Revision == value.Revision && Guid.TryParseExact(row.OperationId, "D", out var operationId) &&
            operationId == value.OperationId && row.PreviousRevisionHash == value.PreviousRevisionHash &&
            row.RevisionHash == value.RevisionHash && row.BindingRevision == value.Binding.Revision &&
            row.BindingRevisionHash == value.Binding.RevisionHash && row.BindingTargetHash == value.Binding.Target.ContentHash &&
            row.Origin == (long)value.Origin && row.ActorPrincipalId == value.ActorPrincipalId.ToString("D") &&
            row.SessionId == value.SessionId.ToString("D") && row.AuthorizationRevision == value.AuthorizationRevision &&
            row.ChangeReason == value.ChangeReason && ImagingParseTime(row.RecordedAtUtc) == value.RecordedAtUtc &&
            row.Payload == Convert.ToBase64String(ImagingSetupRevisionStorageCodec.Encode(value)) &&
            row.PayloadHash == ImagingSetupRevisionStorageCodec.PayloadHash(ImagingSetupRevisionStorageCodec.Encode(value)),
            "ImagingSetupRowBindingMismatch");
    }

    private static DateTimeOffset ImagingParseTime(string value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed :
        throw new InvalidOperationException("ImagingSetupRowBindingMismatch");
    private static Guid ImagingParseGuid(string? value) => Guid.TryParse(value, out var result) ? result :
        throw new InvalidOperationException("ImagingSetupAuthorizationBindingMismatch");
    private static Guid? ImagingParseNullableGuid(string? value) => string.IsNullOrEmpty(value) ? null : ImagingParseGuid(value);
    private static CommandDisposition? ParseNullableDisposition(string? value) => string.IsNullOrEmpty(value) ? null :
        Enum.TryParse<CommandDisposition>(value, out var result) ? result :
        throw new InvalidOperationException("ImagingSetupAuthorizationBindingMismatch");

    private sealed record ImagingSetupRow(long Position, string LogicalRole, long Revision, string OperationId,
        string? PreviousRevisionHash, string RevisionHash, long BindingRevision, string BindingRevisionHash,
        string BindingTargetHash, long Origin, string ActorPrincipalId, string SessionId,
        long AuthorizationRevision, string ChangeReason, string RecordedAtUtc, string Payload, string PayloadHash);
    private sealed record ImagingCommandFact(Guid AttemptId, Guid CorrelationId, Guid RuntimeEpoch,
        AuditedCommandKind CommandKind, string? ClaimedPrincipalId, Guid? ClaimedSessionId,
        Guid? ClaimedStepUpGrantId, Guid? AuthenticatedHumanPrincipalId, CommandAuditPhase Phase,
        CommandDisposition? Disposition, string ReasonCode);
}
