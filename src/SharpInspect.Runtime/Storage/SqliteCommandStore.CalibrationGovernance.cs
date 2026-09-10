using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal bool CalibrationGovernanceEnabled => _options.CalibrationGovernance is not null;
    internal CalibrationGovernanceStoreOptions? CalibrationGovernanceOptions => _options.CalibrationGovernance;

    internal const string CalibrationGovernanceEventKind = "CalibrationGovernanceEvent";

    internal const string CalibrationGovernanceSchemaSql = @"
        CREATE TABLE calibration_governance_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE calibration_governance_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            Kind TEXT NOT NULL CHECK(Kind IN ('AcceptancePolicyPublished','CandidatePolicyEvaluated',
                'CalibrationProfilePublished','PhysicalVerificationRecorded')),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            RecordContentHash TEXT NOT NULL CHECK(length(RecordContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandEventId TEXT NOT NULL CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64));
        CREATE INDEX ix_calibration_governance_operation ON calibration_governance_events(OperationId);
        CREATE INDEX ix_calibration_governance_kind_position ON calibration_governance_events(Kind,Position);
        CREATE INDEX ix_calibration_governance_audit ON calibration_governance_events(AuditSequence);
        CREATE TRIGGER calibration_governance_config_immutable_update BEFORE UPDATE
            ON calibration_governance_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationGovernanceConfiguration');
        END;
        CREATE TRIGGER calibration_governance_config_immutable_delete BEFORE DELETE
            ON calibration_governance_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationGovernanceConfiguration');
        END;
        CREATE TRIGGER calibration_governance_event_immutable_update BEFORE UPDATE
            ON calibration_governance_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationGovernanceEvent');
        END;
        CREATE TRIGGER calibration_governance_event_immutable_delete BEFORE DELETE
            ON calibration_governance_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationGovernanceEvent');
        END;";

    internal static void InitializeCalibrationGovernanceSchema(sqlite3 database,
        CalibrationGovernanceStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, CalibrationGovernanceSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO calibration_governance_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            CalibrationGovernanceStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendCalibrationGovernanceStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredCalibrationGovernance(sqlite3 database,
        CalibrationGovernanceStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM calibration_governance_store_config WHERE Id=1;", deadline,
            statement => (
                FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4))).SingleOrDefault();
        AuditChainDatabase.Require(configured != default &&
            configured.FormatVersion == CalibrationGovernanceStoreOptions.FormatVersion &&
            configured.MaximumEntries == options.MaximumEntries &&
            configured.MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured.MaximumTotalBytes == options.MaximumTotalBytes &&
            configured.BindingHash == options.BindingHash,
            "CalibrationGovernanceConfigurationMismatch");
    }

    internal static void VerifyCalibrationGovernanceActivationPayload(sqlite3 database,
        byte[] payload, CalibrationGovernanceStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()))
            throw new InvalidOperationException("CalibrationGovernanceActivationBindingMismatch");
        RequireConfiguredCalibrationGovernance(database, options, deadline);
    }

    internal static void VerifyCalibrationGovernanceAuditPayload(sqlite3 database, long position,
        byte[] auditPayload, CalibrationGovernanceStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditPayload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var stored = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,OperationId,PreviousHash,RecordContentHash,PayloadHash,Payload,
                CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash
            FROM calibration_governance_events WHERE Position=?;", deadline, statement =>
        {
            var encoded = SqliteNative.ColumnText(statement, 6);
            var recordPayload = DecodeCanonicalPayload(encoded,
                "CalibrationGovernancePayloadInvalid", "CalibrationGovernancePayloadCanonicalMismatch");
            return new CalibrationGovernanceStoredEvent(
                SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParseGuid(SqliteNative.ColumnText(statement, 2), "CalibrationGovernanceOperationInvalid"),
                SqliteNative.ColumnText(statement, 3), SqliteNative.ColumnText(statement, 4) ?? string.Empty,
                SqliteNative.ColumnText(statement, 5) ?? string.Empty, recordPayload,
                ParseGuid(SqliteNative.ColumnText(statement, 7), "CalibrationGovernanceCommandReferenceInvalid"),
                SqliteNative.ColumnInt64(statement, 8), SqliteNative.ColumnText(statement, 9) ?? string.Empty,
                ParseGuid(SqliteNative.ColumnText(statement, 10), "CalibrationGovernanceAuthorizationReferenceInvalid"),
                SqliteNative.ColumnInt64(statement, 11), SqliteNative.ColumnText(statement, 12) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 13), SqliteNative.ColumnText(statement, 14) ?? string.Empty);
        }, Number(position)).SingleOrDefault();
        if (stored is null || stored.Position != position || !IsGovernanceKind(stored.Kind))
            throw new InvalidOperationException("CalibrationGovernanceAuditBindingMismatch");
        if (stored.Payload.Length < 1 || stored.Payload.Length > options.MaximumPayloadBytes ||
            stored.Payload.Length > CalibrationGovernanceCodec.MaximumPayloadBytes)
            throw new InvalidOperationException("CalibrationGovernancePayloadCapacityExceeded");
        RequireHash(stored.RecordContentHash, "CalibrationGovernanceRecordContentHashInvalid");
        RequireHash(stored.PayloadHash, "CalibrationGovernancePayloadHashInvalid");
        RequireHash(stored.CommandAuditHash, "CalibrationGovernanceCommandAuditHashInvalid");
        RequireHash(stored.AuthorizationAuditHash, "CalibrationGovernanceAuthorizationAuditHashInvalid");
        RequireHash(stored.AuditHash, "CalibrationGovernanceAuditHashInvalid");
        if (stored.PreviousHash is not null)
            RequireHash(stored.PreviousHash, "CalibrationGovernancePreviousHashInvalid");
        var expectedPrevious = position == 1 ? null : AuditChainDatabase.Text(database,
            "SELECT AuditHash FROM calibration_governance_events WHERE Position=?;", deadline,
            Number(position - 1));
        if (!string.Equals(stored.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            throw new InvalidOperationException("CalibrationGovernancePreviousHashMismatch");
        if (stored.PayloadHash != Convert.ToHexString(SHA256.HashData(stored.Payload)))
            throw new InvalidOperationException("CalibrationGovernancePayloadHashMismatch");
        var decoded = CalibrationGovernanceCodec.Decode(stored.Kind, stored.Payload);
        var metadata = CalibrationGovernanceProjection.Metadata(decoded);
        if (metadata.Position != stored.Position || metadata.OperationId != stored.OperationId ||
            CalibrationGovernanceCodec.Kind(decoded) != stored.Kind ||
            CalibrationGovernanceCodec.ContentHash(decoded) != stored.RecordContentHash)
            throw new InvalidOperationException("CalibrationGovernanceRecordBindingMismatch");
        var centralHash = AuditChainDatabase.Text(database, @"
            SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND GovernancePosition=?;",
            deadline, Number(stored.AuditSequence), CalibrationGovernanceEventKind, Number(position));
        if (centralHash != stored.AuditHash)
            throw new InvalidOperationException("CalibrationGovernanceAuditBindingMismatch");
        var expected = EncodeGovernanceAuditBinding(stored.Position, stored.Kind, stored.OperationId,
            stored.PreviousHash, stored.RecordContentHash, stored.PayloadHash, stored.CommandEventId,
            stored.CommandAuditSequence, stored.CommandAuditHash, stored.AuthorizationEventId,
            stored.AuthorizationAuditSequence, stored.AuthorizationAuditHash, stored.Payload);
        if (!auditPayload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("CalibrationGovernanceAuditPayloadMismatch");
    }

    internal static IReadOnlyList<CalibrationGovernanceStoredEvent> ReadCalibrationGovernanceLedger(
        sqlite3 database, CalibrationGovernanceStoreOptions options, StoreDeadline deadline,
        CalibrationGovernanceReplayValidator? validator = null,
        Func<Guid, CalibrationSessionEvidence?>? sessionLoader = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (validator is null || sessionLoader is null)
            throw new InvalidOperationException("CalibrationGovernanceValidatorRequired");

        RequireConfiguredCalibrationGovernance(database, options, deadline);
        var entries = ReadCalibrationGovernanceRows(database, options, deadline);
        var decoded = entries.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload))
            .ToArray();
        var reason = validator(entries, sessionLoader);
        if (reason is not null)
            throw new InvalidOperationException(reason);
        _ = decoded;
        return entries;
    }

    internal static IReadOnlyList<CalibrationGovernanceStoredEvent> ReadCalibrationGovernanceLedgerUnchecked(
        sqlite3 database, CalibrationGovernanceStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredCalibrationGovernance(database, options, deadline);
        return ReadCalibrationGovernanceRows(database, options, deadline);
    }

    internal CalibrationGovernanceLedgerQueryResult ReadCalibrationGovernanceState(
        sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.CalibrationGovernance;
        if (options is null)
            return new(false, "CalibrationGovernanceConfigurationRequired", Array.Empty<CalibrationGovernanceStoredEvent>());
        if (_policy is null || _signingKey is null)
            return new(false, "CalibrationGovernanceAuditUnavailable", Array.Empty<CalibrationGovernanceStoredEvent>());

        CalibrationSessionEvidence? Load(Guid id)
        {
            var row = ReadCalibrationSessionRows(database, id, deadline).SingleOrDefault();
            return row is null ? null : BuildEvidence(database, row, deadline);
        }

        try
        {
            VerifyCalibrationReadSnapshot(database, deadline);
            var entries = ReadCalibrationGovernanceLedger(database, options, deadline,
                (stored, loader) =>
                {
                    var values = stored.Select(value => CalibrationGovernanceCodec.Decode(value.Kind,
                        value.Payload)).ToArray();
                    CalibrationGovernanceProjection.ValidateRecords(values, loader);
                    return null;
                }, Load);
            return new(true, "CalibrationGovernanceLedgerAvailable", entries);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is InvalidOperationException invalid &&
                !string.IsNullOrWhiteSpace(invalid.Message)
                ? invalid.Message : "CalibrationGovernanceLedgerUnavailable";
            return new(false, reason, Array.Empty<CalibrationGovernanceStoredEvent>());
        }
    }

    internal async ValueTask<CalibrationGovernanceLedgerQueryResult> ReadCalibrationGovernanceAsync(
        CancellationToken cancellationToken = default)
    {
        var initialized = await Initialization.ConfigureAwait(false);
        if (!initialized.Committed || _databasePath is null)
            return new(false, initialized.ReasonCode, Array.Empty<CalibrationGovernanceStoredEvent>());
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.CalibrationGovernance is null)
            return new(false, "CalibrationGovernanceConfigurationRequired", Array.Empty<CalibrationGovernanceStoredEvent>());
        if (_policy is null || _signingKey is null)
            return new(false, "CalibrationGovernanceAuditUnavailable", Array.Empty<CalibrationGovernanceStoredEvent>());

        try
        {
            return await Task.Run(() =>
            {
                using var connection = SqliteNative.Open(_databasePath, readOnly: true);
                var database = connection.Handle!;
                SqliteNative.ConfigureSqliteLimit(database, _options);
                var deadline = new StoreDeadline(_options.QueryTimeout);
                SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
                try
                {
                    var result = ReadCalibrationGovernanceState(database, deadline);
                    SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                    return result;
                }
                catch
                {
                    try { SqliteNative.Execute(database, "ROLLBACK;", deadline); } catch { }
                    throw;
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is InvalidOperationException invalid &&
                !string.IsNullOrWhiteSpace(invalid.Message)
                ? invalid.Message : "CalibrationGovernanceLedgerUnavailable";
            return new(false, reason, Array.Empty<CalibrationGovernanceStoredEvent>());
        }
    }

    internal CalibrationGovernanceStoredEvent AppendCalibrationGovernance(sqlite3 database,
        CalibrationGovernanceAppendRequest request, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.CalibrationGovernance ??
            throw new InvalidOperationException("CalibrationGovernanceConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("CalibrationGovernanceAuditUnavailable");
        var signingKey = _signingKey ?? throw new InvalidOperationException("CalibrationGovernanceAuditUnavailable");
        options.Validate();
        var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        AuditChainDatabase.Require(schema is CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
            RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
            CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or
            ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion,
            "CalibrationGovernanceSchemaInvalid");
        RequireConfiguredCalibrationGovernance(database, options, deadline);

        var payload = request.Payload.ToArray();
        if (payload.Length < 1 || payload.Length > options.MaximumPayloadBytes ||
            payload.Length > CalibrationGovernanceCodec.MaximumPayloadBytes)
            throw new InvalidOperationException("CalibrationGovernancePayloadCapacityExceeded");
        if (request.OperationId == Guid.Empty || request.CommandEventId == Guid.Empty ||
            request.AuthorizationEventId == Guid.Empty)
            throw new InvalidOperationException("CalibrationGovernanceIdentityInvalid");
        RequireHash(request.RecordContentHash, "CalibrationGovernanceRecordContentHashInvalid");
        var kind = request.Kind;
        if (!IsGovernanceKind(kind)) throw new InvalidOperationException("CalibrationGovernanceKindUnknown");
        var decoded = CalibrationGovernanceCodec.Decode(kind, payload);
        var canonical = CalibrationGovernanceCodec.Encode(decoded);
        if (!payload.AsSpan().SequenceEqual(canonical))
            throw new InvalidOperationException("CalibrationGovernancePayloadCanonicalMismatch");
        if (CalibrationGovernanceCodec.Kind(decoded) != kind ||
            CalibrationGovernanceCodec.ContentHash(decoded) != request.RecordContentHash)
            throw new InvalidOperationException("CalibrationGovernanceContentHashMismatch");

        var metadata = CalibrationGovernanceProjection.Metadata(decoded);
        if (metadata.OperationId != request.OperationId)
            throw new InvalidOperationException("CalibrationGovernanceOperationBindingMismatch");
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        var entries = ReadCalibrationGovernanceRows(database, options, deadline);
        if (entries.Count >= options.MaximumEntries)
            throw new InvalidOperationException("CalibrationGovernanceEntryCapacityExceeded");
        var total = entries.Aggregate(0L, (sum, entry) => checked(sum + entry.Payload.Length));
        if (checked(total + payload.Length) > options.MaximumTotalBytes)
            throw new InvalidOperationException("CalibrationGovernanceTotalCapacityExceeded");
        if (entries.Any(entry => entry.OperationId == request.OperationId))
            throw new InvalidOperationException("CalibrationGovernanceOperationConflict");

        var position = checked(entries.Count + 1L);
        var previousHash = entries.Count == 0 ? null : entries[^1].AuditHash;
        if (!string.Equals(previousHash, request.ExpectedPreviousHash, StringComparison.Ordinal))
            throw new InvalidOperationException("CalibrationGovernancePreviousHashMismatch");

        var command = ReadCommandAuditReference(database, request.CommandEventId, request.OperationId,
            kind, deadline);
        var authorization = ReadAuthorizationAuditReference(database, request.AuthorizationEventId,
            request.OperationId, command.CommandKind, policy.StationId, deadline);
        var bindingPayload = EncodeGovernanceAuditBinding(position, kind, request.OperationId,
            previousHash, request.RecordContentHash, payloadHash, request.CommandEventId,
            command.Sequence, command.Hash, request.AuthorizationEventId, authorization.Sequence,
            authorization.Hash, payload);
        var auditSequence = AuditChainDatabase.AppendCalibrationGovernanceLedgerEntry(database, policy,
            signingKey, position, bindingPayload, deadline);
        var auditHash = ReadGovernanceAuditHash(database, auditSequence, position, deadline);
        var stored = new CalibrationGovernanceStoredEvent(position, kind, request.OperationId,
            previousHash, request.RecordContentHash, payloadHash, payload,
            request.CommandEventId, command.Sequence, command.Hash,
            request.AuthorizationEventId, authorization.Sequence, authorization.Hash,
            auditSequence, auditHash);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO calibration_governance_events
                (Position,Kind,OperationId,PreviousHash,RecordContentHash,PayloadHash,Payload,
                 CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                 AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            Number(stored.Position), stored.Kind, stored.OperationId.ToString("D"), stored.PreviousHash,
            stored.RecordContentHash, stored.PayloadHash, Convert.ToBase64String(stored.Payload),
            stored.CommandEventId.ToString("D"), Number(stored.CommandAuditSequence), stored.CommandAuditHash,
            stored.AuthorizationEventId.ToString("D"), Number(stored.AuthorizationAuditSequence),
            stored.AuthorizationAuditHash, Number(stored.AuditSequence), stored.AuditHash);
        ValidateGovernanceAuditReferences(database, stored, decoded, deadline);
        return stored;
    }

    internal static void ValidateCalibrationGovernanceHistory(sqlite3 database,
        CalibrationGovernanceStoreOptions options, StoreDeadline deadline)
    {
        var stored = ReadCalibrationGovernanceLedgerUnchecked(database, options, deadline);
        var records = stored.Select(value => CalibrationGovernanceCodec.Decode(value.Kind,
            value.Payload)).ToArray();
        CalibrationGovernanceProjection.ValidateRecords(records, id =>
        {
            var row = ReadCalibrationSessionRows(database, id, deadline).SingleOrDefault();
            return row is null ? null : BuildEvidence(database, row, deadline);
        });
    }

    private static List<CalibrationGovernanceStoredEvent> ReadCalibrationGovernanceRows(sqlite3 database,
        CalibrationGovernanceStoreOptions options, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,OperationId,PreviousHash,RecordContentHash,PayloadHash,Payload,
                CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash
            FROM calibration_governance_events ORDER BY Position;", deadline, statement =>
        {
            var encoded = SqliteNative.ColumnText(statement, 6);
            if (encoded is null || encoded.Length == 0)
                throw new InvalidOperationException("CalibrationGovernancePayloadInvalid");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded);
                if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                    throw new InvalidOperationException("CalibrationGovernancePayloadCanonicalMismatch");
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("CalibrationGovernancePayloadInvalid", exception);
            }
            return new CalibrationGovernanceStoredEvent(
                SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                ParseGuid(SqliteNative.ColumnText(statement, 2), "CalibrationGovernanceOperationInvalid"),
                SqliteNative.ColumnText(statement, 3), SqliteNative.ColumnText(statement, 4) ?? string.Empty,
                SqliteNative.ColumnText(statement, 5) ?? string.Empty, payload,
                ParseGuid(SqliteNative.ColumnText(statement, 7), "CalibrationGovernanceCommandReferenceInvalid"),
                SqliteNative.ColumnInt64(statement, 8), SqliteNative.ColumnText(statement, 9) ?? string.Empty,
                ParseGuid(SqliteNative.ColumnText(statement, 10), "CalibrationGovernanceAuthorizationReferenceInvalid"),
                SqliteNative.ColumnInt64(statement, 11), SqliteNative.ColumnText(statement, 12) ?? string.Empty,
                SqliteNative.ColumnInt64(statement, 13), SqliteNative.ColumnText(statement, 14) ?? string.Empty);
        });

        if (rows.Count > options.MaximumEntries)
            throw new InvalidOperationException("CalibrationGovernanceEntryCapacityExceeded");
        var total = 0L;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Position != index + 1L)
                throw new InvalidOperationException("CalibrationGovernancePositionGap");
            if (row.Payload.Length < 1 || row.Payload.Length > options.MaximumPayloadBytes ||
                row.Payload.Length > CalibrationGovernanceCodec.MaximumPayloadBytes)
                throw new InvalidOperationException("CalibrationGovernancePayloadCapacityExceeded");
            total = checked(total + row.Payload.Length);
            if (total > options.MaximumTotalBytes)
                throw new InvalidOperationException("CalibrationGovernanceTotalCapacityExceeded");
            if (!IsGovernanceKind(row.Kind))
                throw new InvalidOperationException("CalibrationGovernanceKindUnknown");
            RequireHash(row.RecordContentHash, "CalibrationGovernanceRecordContentHashInvalid");
            RequireHash(row.PayloadHash, "CalibrationGovernancePayloadHashInvalid");
            RequireHash(row.CommandAuditHash, "CalibrationGovernanceCommandAuditHashInvalid");
            RequireHash(row.AuthorizationAuditHash, "CalibrationGovernanceAuthorizationAuditHashInvalid");
            RequireHash(row.AuditHash, "CalibrationGovernanceAuditHashInvalid");
            if (row.PreviousHash is not null) RequireHash(row.PreviousHash,
                "CalibrationGovernancePreviousHashInvalid");
            var expectedPrevious = index == 0 ? null : rows[index - 1].AuditHash;
            if (!string.Equals(row.PreviousHash, expectedPrevious, StringComparison.Ordinal))
                throw new InvalidOperationException("CalibrationGovernancePreviousHashMismatch");
            var expectedPayloadHash = Convert.ToHexString(SHA256.HashData(row.Payload));
            if (row.PayloadHash != expectedPayloadHash)
                throw new InvalidOperationException("CalibrationGovernancePayloadHashMismatch");
            var decoded = CalibrationGovernanceCodec.Decode(row.Kind, row.Payload);
            var metadata = CalibrationGovernanceProjection.Metadata(decoded);
            if (metadata.Position != row.Position || metadata.OperationId != row.OperationId ||
                CalibrationGovernanceCodec.Kind(decoded) != row.Kind ||
                CalibrationGovernanceCodec.ContentHash(decoded) != row.RecordContentHash)
                throw new InvalidOperationException("CalibrationGovernanceRecordBindingMismatch");
            ValidateGovernanceAuditReferences(database, row, decoded, deadline);
        }
        return rows;
    }

    private static void ValidateGovernanceAuditReferences(sqlite3 database,
        CalibrationGovernanceStoredEvent row, object decoded, StoreDeadline deadline)
    {
        var metadata = CalibrationGovernanceProjection.Metadata(decoded);
        var commandKind = ExpectedCommandKind(row.Kind);
        var command = ReadCommandAuditReference(database, row.CommandEventId, row.OperationId,
            row.Kind, deadline);
        if (command.Sequence != row.CommandAuditSequence || command.Hash != row.CommandAuditHash ||
            command.CorrelationId != row.OperationId || command.CommandKind != commandKind ||
            command.Phase != CommandAuditPhase.Outcome || command.Disposition != CommandDisposition.Accepted)
            throw new InvalidOperationException("CalibrationGovernanceCommandAuditBindingMismatch");
        var authorization = ReadAuthorizationAuditReference(database, row.AuthorizationEventId,
            row.OperationId, commandKind, ReadGovernanceStationId(database, deadline), deadline);
        if (authorization.Sequence != row.AuthorizationAuditSequence || authorization.Hash != row.AuthorizationAuditHash ||
            authorization.RequiredPermission != ExpectedGovernancePermission(commandKind).ToString() ||
            !IsGovernanceActionTarget(authorization.ActionTarget) ||
            command.AuthenticatedHumanPrincipalId != authorization.PrincipalId ||
            command.ClaimedSessionId != authorization.SessionId ||
            command.ClaimedStepUpGrantId != authorization.StepUpGrantId ||
            metadata.Actor.PrincipalId != authorization.ActorPrincipalId ||
            metadata.Actor.SessionId != authorization.SessionId ||
            metadata.Actor.AuthorizationRevision != authorization.AuthorizationRevision ||
            metadata.RecordedAtUtc != authorization.OccurredAtUtc)
            throw new InvalidOperationException("CalibrationGovernanceAuthorizationAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Kind,GovernancePosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind=? AND GovernancePosition=?;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Kind: SqliteNative.ColumnText(statement, 1),
                Position: SqliteNative.ColumnInt64(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3),
                Hash: SqliteNative.ColumnText(statement, 4)),
            Number(row.AuditSequence), CalibrationGovernanceEventKind, Number(row.Position)).SingleOrDefault();
        if (audit == default || audit.Sequence != row.AuditSequence || audit.Position != row.Position ||
            audit.Hash != row.AuditHash)
            throw new InvalidOperationException("CalibrationGovernanceAuditBindingMismatch");
        var payload = Convert.FromBase64String(audit.Payload ?? string.Empty);
        var expectedBinding = EncodeGovernanceAuditBinding(row.Position, row.Kind, row.OperationId,
            row.PreviousHash, row.RecordContentHash, row.PayloadHash, row.CommandEventId,
            row.CommandAuditSequence, row.CommandAuditHash, row.AuthorizationEventId,
            row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.Payload);
        if (!payload.AsSpan().SequenceEqual(expectedBinding))
            throw new InvalidOperationException("CalibrationGovernanceAuditPayloadMismatch");
    }

    private static GovernanceCommandAuditReference ReadCommandAuditReference(
        sqlite3 database, Guid eventId, Guid operationId, string kind, StoreDeadline deadline)
    {
        var command = AuditChainDatabase.Read(database, @"
            SELECT Position,CorrelationId,CommandKind,OccurredAtUtc,AuthenticatedHumanPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition
            FROM command_facts WHERE EventId=?;", deadline,
            statement =>
            {
                var commandKind = ParseGovernanceEnum<AuditedCommandKind>(
                    SqliteNative.ColumnInt64(statement, 2), "CalibrationGovernanceCommandKindInvalid");
                var phase = ParseGovernanceEnum<CommandAuditPhase>(
                    SqliteNative.ColumnInt64(statement, 7), "CalibrationGovernanceCommandPhaseInvalid");
                var rawDisposition = SqliteNative.ColumnInt64Nullable(statement, 8);
                if (rawDisposition is not { } dispositionValue)
                    throw new InvalidOperationException("CalibrationGovernanceCommandDispositionInvalid");
                var disposition = ParseGovernanceEnum<CommandDisposition>(dispositionValue,
                    "CalibrationGovernanceCommandDispositionInvalid");
                if (phase != CommandAuditPhase.Outcome || disposition != CommandDisposition.Accepted)
                    throw new InvalidOperationException("CalibrationGovernanceCommandOutcomeInvalid");
                return new GovernanceCommandAuditReference(
                    SqliteNative.ColumnInt64(statement, 0),
                    0,
                    string.Empty,
                    ParseGuid(SqliteNative.ColumnText(statement, 1), "CalibrationGovernanceCommandInvalid"),
                    commandKind,
                    ParseGovernanceTime(SqliteNative.ColumnText(statement, 3),
                        "CalibrationGovernanceCommandTimeInvalid"),
                    ParseGovernanceGuid(SqliteNative.ColumnText(statement, 4),
                        "CalibrationGovernanceAuthenticatedPrincipalInvalid"),
                    ParseGovernanceGuid(SqliteNative.ColumnText(statement, 5),
                        "CalibrationGovernanceClaimedSessionInvalid"),
                    ParseGovernanceGuid(SqliteNative.ColumnText(statement, 6),
                        "CalibrationGovernanceClaimedGrantInvalid"),
                    phase, disposition);
            },
            eventId.ToString("D")).SingleOrDefault();
        if (command == default || command.CorrelationId != operationId ||
            command.CommandKind != ExpectedCommandKind(kind))
            throw new InvalidOperationException("CalibrationGovernanceCommandBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Hash FROM audit_entries
            WHERE Kind='CommandFact' AND FactPosition=?;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty), Number(command.Position)).SingleOrDefault();
        if (audit == default || !IsHash(audit.Hash))
            throw new InvalidOperationException("CalibrationGovernanceCommandAuditMissing");
        return command with { Sequence = audit.Sequence, Hash = audit.Hash };
    }

    private static GovernanceAuthorizationAuditReference ReadAuthorizationAuditReference(sqlite3 database,
        Guid eventId, Guid operationId, AuditedCommandKind commandKind, string stationId,
        StoreDeadline deadline)
    {
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty));
        var matches = new List<GovernanceAuthorizationAuditReference>();
        foreach (var row in rows)
        {
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(row.Payload);
                if (!string.Equals(Convert.ToBase64String(payload), row.Payload, StringComparison.Ordinal))
                    continue;
                IdentityAuditEvent.VerifyPayload(payload, row.Ordinal, stationId, schemaVersion);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or
                                               InvalidOperationException or EndOfStreamException)
            { continue; }
            if (!IsHash(row.Hash) || !TryReadIdentityAuthorization(payload, eventId, operationId,
                    commandKind, out var fields)) continue;
            matches.Add(new GovernanceAuthorizationAuditReference(row.Sequence, row.Hash,
                fields.PrincipalId, fields.ActorPrincipalId, fields.SessionId, fields.StepUpGrantId,
                fields.AuthorizationRevision, fields.RequiredPermission, fields.ActionTarget,
                fields.OccurredAtUtc));
        }
        if (matches.Count != 1) throw new InvalidOperationException("CalibrationGovernanceAuthorizationAuditMissing");
        return matches[0];
    }

    private static bool TryReadIdentityAuthorization(byte[] payload, Guid eventId, Guid operationId,
        AuditedCommandKind commandKind, out GovernanceIdentityAuthorizationFields fields)
    {
        fields = default;
        try
        {
            var values = DecodeGovernanceIdentityFields(payload);
            if (values.Length != 49 || !Guid.TryParseExact(values[1], "D", out var parsedEvent) ||
                parsedEvent != eventId || values[2] != IdentityEventKind.CalibrationGovernanceActionAuthorized.ToString() ||
                !Guid.TryParseExact(values[31], "D", out var correlation) ||
                !Guid.TryParseExact(values[38], "D", out var boundCorrelation) ||
                correlation != operationId || boundCorrelation != operationId ||
                values[39] != commandKind.ToString() || !Guid.TryParseExact(values[42], "D", out var operation) ||
                operation != operationId || !TryParseGovernanceGuid(values[5], out var principalId) ||
                !TryParseGovernanceGuid(values[30], out var actorPrincipalId) || principalId != actorPrincipalId ||
                !TryParseGovernanceGuid(values[25], out var sessionId) ||
                !TryParseGovernanceGuid(values[32], out var stepUpGrantId) ||
                !TryParseGovernanceRevision(values[35], out var authorizationRevision) ||
                !TryParseGovernanceTime(values[3], out var occurredAtUtc) ||
                values[34] is not null || values[33] != ExpectedGovernancePermission(commandKind).ToString() ||
                !IsGovernanceActionTarget(values[37]))
                return false;
            fields = new GovernanceIdentityAuthorizationFields(principalId, actorPrincipalId, sessionId,
                stepUpGrantId, authorizationRevision, values[33]!, values[37]!, occurredAtUtc);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
                                           InvalidOperationException or EndOfStreamException or
                                           DecoderFallbackException)
        { return false; }
    }

    private static string?[] DecodeGovernanceIdentityFields(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var version = reader.ReadBytes(4);
        if (version.Length != 4 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(version) !=
            AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("CalibrationGovernanceAuthorizationPayloadInvalid");
        string? Value()
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            if (marker != 1) throw new InvalidOperationException("CalibrationGovernanceAuthorizationPayloadInvalid");
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length != 4) throw new EndOfStreamException();
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > 1024) throw new InvalidOperationException("CalibrationGovernanceAuthorizationPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        if (Value() != "IdentityEvent") throw new InvalidOperationException("CalibrationGovernanceAuthorizationPayloadInvalid");
        var countBytes = reader.ReadBytes(4);
        if (countBytes.Length != 4) throw new EndOfStreamException();
        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count != 49) throw new InvalidOperationException("CalibrationGovernanceAuthorizationPayloadInvalid");
        var result = new string?[count];
        for (var i = 0; i < count; i++) result[i] = Value();
        if (stream.Position != stream.Length) throw new InvalidOperationException("CalibrationGovernanceAuthorizationPayloadTrailingBytes");
        return result;
    }

    private static byte[] EncodeGovernanceAuditBinding(long position, string kind, Guid operationId,
        string? previousHash, string recordContentHash, string payloadHash, Guid commandEventId,
        long commandAuditSequence, string commandAuditHash, Guid authorizationEventId,
        long authorizationAuditSequence, string authorizationAuditHash, byte[] payload) =>
        AuditCanonical.Encode("CalibrationGovernanceLedgerEntryV1",
            Number(position), kind, operationId.ToString("D"), previousHash, recordContentHash,
            payloadHash, commandEventId.ToString("D"), Number(commandAuditSequence), commandAuditHash,
            authorizationEventId.ToString("D"), Number(authorizationAuditSequence), authorizationAuditHash,
            Convert.ToBase64String(payload));

    private static string ReadGovernanceAuditHash(sqlite3 database, long sequence, long position,
        StoreDeadline deadline) => AuditChainDatabase.Read(database, @"
        SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND GovernancePosition=?;", deadline,
        statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
        Number(sequence), CalibrationGovernanceEventKind, Number(position)).SingleOrDefault()
        ?? throw new InvalidOperationException("CalibrationGovernanceAuditEntryMissing");

    private static string ReadGovernanceStationId(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1;", deadline)
        ?? throw new InvalidOperationException("CalibrationGovernanceStationIdentityMissing");

    private static AuditedCommandKind ExpectedCommandKind(string kind) => kind switch
    {
        CalibrationGovernanceCodec.AcceptancePolicyPublished => AuditedCommandKind.PublishCalibrationAcceptancePolicy,
        CalibrationGovernanceCodec.CandidatePolicyEvaluated => AuditedCommandKind.EvaluateCalibrationCandidate,
        CalibrationGovernanceCodec.CalibrationProfilePublished => AuditedCommandKind.PublishCalibrationProfile,
        CalibrationGovernanceCodec.PhysicalVerificationRecorded => AuditedCommandKind.RecordPhysicalCalibrationVerification,
        "ImportedCandidate" => AuditedCommandKind.ImportCalibrationPackage,
        "LocalRevalidation" => AuditedCommandKind.RevalidateImportedCalibration,
        "LocalPhysicalVerification" => AuditedCommandKind.VerifyImportedCalibration,
        "LocalProfilePublication" => AuditedCommandKind.PublishImportedCalibration,
        _ => throw new InvalidOperationException("CalibrationGovernanceKindUnknown")
    };

    private static Permission ExpectedGovernancePermission(AuditedCommandKind kind) => kind switch
    {
        AuditedCommandKind.PublishCalibrationAcceptancePolicy => Permission.ManageCalibrationAcceptancePolicy,
        AuditedCommandKind.EvaluateCalibrationCandidate or AuditedCommandKind.PublishCalibrationProfile =>
            Permission.PublishCalibration,
        AuditedCommandKind.RecordPhysicalCalibrationVerification =>
            Permission.RecordPhysicalCalibrationVerification,
        AuditedCommandKind.ImportCalibrationPackage or AuditedCommandKind.RevalidateImportedCalibration or
            AuditedCommandKind.PublishImportedCalibration => Permission.PublishCalibration,
        AuditedCommandKind.VerifyImportedCalibration => Permission.RecordPhysicalCalibrationVerification,
        _ => throw new InvalidOperationException("CalibrationGovernanceCommandKindInvalid")
    };

    private static bool IsGovernanceActionTarget(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');

    private static T ParseGovernanceEnum<T>(long value, string reason) where T : struct, Enum
    {
        if (value < int.MinValue || value > int.MaxValue ||
            !Enum.IsDefined(typeof(T), (int)value))
            throw new InvalidOperationException(reason);
        return (T)Enum.ToObject(typeof(T), (int)value);
    }

    private static Guid ParseGovernanceGuid(string? value, string reason) =>
        TryParseGovernanceGuid(value, out var result) ? result : throw new InvalidOperationException(reason);

    private static bool TryParseGovernanceGuid(string? value, out Guid result) =>
        Guid.TryParseExact(value, "D", out result) && result != Guid.Empty;

    private static DateTimeOffset ParseGovernanceTime(string? value, string reason) =>
        TryParseGovernanceTime(value, out var result) ? result : throw new InvalidOperationException(reason);

    private static bool TryParseGovernanceTime(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out result) && result != default;

    private static bool TryParseGovernanceRevision(string? value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) &&
        result >= 0 && value == result.ToString(CultureInfo.InvariantCulture);

    private static bool IsGovernanceKind(string? kind) => kind is
        CalibrationGovernanceCodec.AcceptancePolicyPublished or
        CalibrationGovernanceCodec.CandidatePolicyEvaluated or
        CalibrationGovernanceCodec.CalibrationProfilePublished or
        CalibrationGovernanceCodec.PhysicalVerificationRecorded;

    private static Guid ParseGuid(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty
            ? result : throw new InvalidOperationException(reason);

    private static void RequireHash(string? value, string reason)
    {
        if (!IsHash(value)) throw new InvalidOperationException(reason);
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static byte[] DecodeCanonicalPayload(string? encoded, string invalidReason,
        string mismatchReason)
    {
        if (encoded is null or { Length: 0 })
            throw new InvalidOperationException(invalidReason);
        try
        {
            var payload = Convert.FromBase64String(encoded);
            if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                throw new InvalidOperationException(mismatchReason);
            return payload;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(invalidReason, exception);
        }
    }


    private readonly record struct GovernanceCommandAuditReference(
        long Position, long Sequence, string Hash, Guid CorrelationId, AuditedCommandKind CommandKind,
        DateTimeOffset OccurredAtUtc, Guid AuthenticatedHumanPrincipalId,
        Guid ClaimedSessionId, Guid ClaimedStepUpGrantId, CommandAuditPhase Phase,
        CommandDisposition Disposition);

    private readonly record struct GovernanceAuthorizationAuditReference(
        long Sequence, string Hash, Guid PrincipalId, Guid ActorPrincipalId,
        Guid SessionId, Guid StepUpGrantId, long AuthorizationRevision,
        string RequiredPermission, string ActionTarget, DateTimeOffset OccurredAtUtc);

    private readonly record struct GovernanceIdentityAuthorizationFields(
        Guid PrincipalId, Guid ActorPrincipalId, Guid SessionId, Guid StepUpGrantId,
        long AuthorizationRevision, string RequiredPermission, string ActionTarget,
        DateTimeOffset OccurredAtUtc);
}

internal sealed class CalibrationGovernanceStoredEvent
{
    internal CalibrationGovernanceStoredEvent(long position, string kind, Guid operationId,
        string? previousHash, string recordContentHash, string payloadHash, byte[] payload,
        Guid commandEventId, long commandAuditSequence, string commandAuditHash,
        Guid authorizationEventId, long authorizationAuditSequence, string authorizationAuditHash,
        long auditSequence, string auditHash)
    {
        Position = position;
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        OperationId = operationId;
        PreviousHash = previousHash;
        RecordContentHash = recordContentHash ?? throw new ArgumentNullException(nameof(recordContentHash));
        PayloadHash = payloadHash ?? throw new ArgumentNullException(nameof(payloadHash));
        Payload = payload?.ToArray() ?? throw new ArgumentNullException(nameof(payload));
        CommandEventId = commandEventId;
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = commandAuditHash ?? throw new ArgumentNullException(nameof(commandAuditHash));
        AuthorizationEventId = authorizationEventId;
        AuthorizationAuditSequence = authorizationAuditSequence;
        AuthorizationAuditHash = authorizationAuditHash ?? throw new ArgumentNullException(nameof(authorizationAuditHash));
        AuditSequence = auditSequence;
        AuditHash = auditHash ?? throw new ArgumentNullException(nameof(auditHash));
    }

    internal long Position { get; }
    internal string Kind { get; }
    internal Guid OperationId { get; }
    internal string? PreviousHash { get; }
    internal string RecordContentHash { get; }
    internal string PayloadHash { get; }
    internal byte[] Payload { get; }
    internal Guid CommandEventId { get; }
    internal long CommandAuditSequence { get; }
    internal string CommandAuditHash { get; }
    internal Guid AuthorizationEventId { get; }
    internal long AuthorizationAuditSequence { get; }
    internal string AuthorizationAuditHash { get; }
    internal long AuditSequence { get; }
    internal string AuditHash { get; }
}

internal sealed record CalibrationGovernanceAppendRequest(
    string Kind,
    Guid OperationId,
    string RecordContentHash,
    ReadOnlyMemory<byte> Payload,
    Guid CommandEventId,
    Guid AuthorizationEventId,
    string? ExpectedPreviousHash);

internal delegate string? CalibrationGovernanceReplayValidator(
    IReadOnlyList<CalibrationGovernanceStoredEvent> records,
    Func<Guid, CalibrationSessionEvidence?> sessionLoader);

internal sealed record CalibrationGovernanceLedgerQueryResult(bool Available, string ReasonCode,
    IReadOnlyList<CalibrationGovernanceStoredEvent> Records);
