using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal const string CalibrationImportEventKind = "CalibrationImportEvent";
    private const string CalibrationImportSchemaSql = @"
        CREATE TABLE calibration_import_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL, MaximumPayloadBytes INTEGER NOT NULL,
            MaximumTotalBytes INTEGER NOT NULL, BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE calibration_import_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            Kind TEXT NOT NULL CHECK(Kind IN ('ImportedCandidate','LocalRevalidation','LocalPhysicalVerification','LocalProfilePublication')),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            RecordContentHash TEXT NOT NULL CHECK(length(RecordContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64), Payload TEXT NOT NULL,
            CommandEventId TEXT NOT NULL CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64));
        CREATE TRIGGER calibration_import_config_no_update BEFORE UPDATE ON calibration_import_store_config
            BEGIN SELECT RAISE(ABORT,'ImmutableCalibrationImportConfiguration'); END;
        CREATE TRIGGER calibration_import_config_no_delete BEFORE DELETE ON calibration_import_store_config
            BEGIN SELECT RAISE(ABORT,'ImmutableCalibrationImportConfiguration'); END;
        CREATE TRIGGER calibration_import_no_update BEFORE UPDATE ON calibration_import_events
            BEGIN SELECT RAISE(ABORT,'ImmutableCalibrationImportEvent'); END;
        CREATE TRIGGER calibration_import_no_delete BEFORE DELETE ON calibration_import_events
            BEGIN SELECT RAISE(ABORT,'ImmutableCalibrationImportEvent'); END;";

    internal static void InitializeCalibrationImportSchema(sqlite3 database, CalibrationImportStoreOptions options,
        StoreDeadline deadline, AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        options.Validate();
        SqliteNative.Execute(database, CalibrationImportSchemaSql, deadline);
        AuditChainDatabase.Execute(database, "INSERT INTO calibration_import_store_config VALUES(1,1,?,?,?,?);", deadline,
            Number(options.MaximumEntries), Number(options.MaximumPayloadBytes), Number(options.MaximumTotalBytes), options.BindingHash);
        AuditChainDatabase.AppendCalibrationImportStoreActivation(database, policy, signingKey, options, deadline);
    }

    internal static void RequireConfiguredCalibrationImports(sqlite3 database, CalibrationImportStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        var value = AuditChainDatabase.Read(database,
            "SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash FROM calibration_import_store_config WHERE Id=1;",
            deadline, s => (Format: SqliteNative.ColumnInt64(s, 0), Entries: SqliteNative.ColumnInt64(s, 1),
                Payload: SqliteNative.ColumnInt64(s, 2), Total: SqliteNative.ColumnInt64(s, 3), Hash: SqliteNative.ColumnText(s, 4)))
            .SingleOrDefault();
        AuditChainDatabase.Require(value.Format == 1 && value.Entries == options.MaximumEntries &&
            value.Payload == options.MaximumPayloadBytes && value.Total == options.MaximumTotalBytes && value.Hash == options.BindingHash,
            "CalibrationImportConfigurationMismatch");
    }

    internal static void VerifyCalibrationImportActivationPayload(sqlite3 database, byte[] payload,
        CalibrationImportStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredCalibrationImports(database, options, deadline);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "CalibrationImportActivationBindingMismatch");
    }

    internal static void VerifyCalibrationImportAuditPayload(sqlite3 database, long position, byte[] payload,
        CalibrationImportStoreOptions options, StoreDeadline deadline)
    {
        // The complete ledger pass validates order, record bytes and foreign audit references.
        // Each central audit entry resolves only its own row, keeping that pass linear.
        RequireConfiguredCalibrationImports(database, options, deadline);
        var row = AuditChainDatabase.Read(database, @"SELECT Position,Kind,OperationId,PreviousHash,RecordContentHash,
            PayloadHash,Payload,CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
            AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash FROM calibration_import_events
            WHERE Position=?;", deadline, ReadImportStoredEvent, Number(position)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null && payload.AsSpan().SequenceEqual(EncodeImportAuditBinding(row)),
            "CalibrationImportAuditPayloadMismatch");
    }

    private static IReadOnlyList<CalibrationImportStoredEvent> ReadCalibrationImportRows(sqlite3 database,
        CalibrationImportStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredCalibrationImports(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,Kind,OperationId,PreviousHash,RecordContentHash,
            PayloadHash,Payload,CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
            AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash FROM calibration_import_events
            ORDER BY Position LIMIT ?;", deadline, ReadImportStoredEvent, Number(options.MaximumEntries + 1L));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries, "CalibrationImportEntryCapacityExceeded");
        string? previous = null;
        long total = 0;
        var operations = new HashSet<Guid>();
        long previousAuditSequence = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            AuditChainDatabase.Require(row.Position == index + 1L && row.PreviousHash == previous && operations.Add(row.OperationId) &&
                row.AuditSequence > previousAuditSequence && row.AuthorizationAuditSequence < row.CommandAuditSequence &&
                row.CommandAuditSequence < row.AuditSequence,
                "CalibrationImportLedgerOrderInvalid");
            var bytes = DecodeImportRowPayload(row, options);
            total = checked(total + bytes.Length);
            AuditChainDatabase.Require(total <= options.MaximumTotalBytes, "CalibrationImportTotalCapacityExceeded");
            var record = CalibrationGovernanceCodec.DecodeImport(bytes);
            AuditChainDatabase.Require(record.Position == row.Position && record.OperationId == row.OperationId &&
                record.ContentHash == row.RecordContentHash && CalibrationGovernanceCodec.ImportRecordKind(record) == row.Kind,
                "CalibrationImportRecordBindingMismatch");
            ValidateImportAuditReferences(database, row, record, deadline);
            previous = row.AuditHash;
            previousAuditSequence = row.AuditSequence;
        }
        return rows;
    }

    private static CalibrationImportStoredEvent ReadImportStoredEvent(sqlite3_stmt s) =>
        new(SqliteNative.ColumnInt64(s, 0),
                SqliteNative.ColumnText(s, 1)!, ParseGuid(SqliteNative.ColumnText(s, 2), "CalibrationImportOperationInvalid"),
                SqliteNative.ColumnText(s, 3), SqliteNative.ColumnText(s, 4)!, SqliteNative.ColumnText(s, 5)!,
                SqliteNative.ColumnText(s, 6)!, ParseGuid(SqliteNative.ColumnText(s, 7), "CalibrationImportCommandInvalid"),
                SqliteNative.ColumnInt64(s, 8), SqliteNative.ColumnText(s, 9)!,
                ParseGuid(SqliteNative.ColumnText(s, 10), "CalibrationImportAuthorizationInvalid"),
                SqliteNative.ColumnInt64(s, 11), SqliteNative.ColumnText(s, 12)!,
                SqliteNative.ColumnInt64(s, 13), SqliteNative.ColumnText(s, 14)!);

    private static byte[] DecodeImportRowPayload(CalibrationImportStoredEvent row, CalibrationImportStoreOptions options)
    {
        AuditChainDatabase.Require(row.Payload.Length > 0 && row.Payload.Length <= ((options.MaximumPayloadBytes + 2L) / 3) * 4,
            "CalibrationImportPayloadCapacityExceeded");
        var bytes = Convert.FromBase64String(row.Payload);
        AuditChainDatabase.Require(bytes.Length <= options.MaximumPayloadBytes && Convert.ToBase64String(bytes) == row.Payload &&
            Convert.ToHexString(SHA256.HashData(bytes)) == row.PayloadHash, "CalibrationImportPayloadHashMismatch");
        return bytes;
    }

    private static void ValidateImportAuditReferences(sqlite3 database, CalibrationImportStoredEvent row,
        CalibrationImportRecord record, StoreDeadline deadline)
    {
        var command = ReadCommandAuditReference(database, row.CommandEventId, row.OperationId, row.Kind, deadline);
        var authorization = ReadAuthorizationAuditReference(database, row.AuthorizationEventId, row.OperationId,
            command.CommandKind, ReadGovernanceStationId(database, deadline), deadline);
        AuditChainDatabase.Require(command.Sequence == row.CommandAuditSequence && command.Hash == row.CommandAuditHash &&
            authorization.Sequence == row.AuthorizationAuditSequence && authorization.Hash == row.AuthorizationAuditHash &&
            authorization.RequiredPermission == ExpectedGovernancePermission(command.CommandKind).ToString() &&
            command.AuthenticatedHumanPrincipalId == record.Actor.PrincipalId &&
            command.ClaimedSessionId == record.Actor.SessionId && command.ClaimedStepUpGrantId == authorization.StepUpGrantId &&
            authorization.PrincipalId == record.Actor.PrincipalId && authorization.ActorPrincipalId == record.Actor.PrincipalId &&
            authorization.SessionId == record.Actor.SessionId && authorization.AuthorizationRevision == record.Actor.AuthorizationRevision &&
            authorization.OccurredAtUtc == record.RecordedAtUtc && command.OccurredAtUtc == record.RecordedAtUtc &&
            authorization.ActionTarget == record.AuthorizationTarget,
            "CalibrationImportAuthorizationBindingMismatch");
        if (record is ImportedCalibrationPhysicalVerification physical)
        {
            var epoch = AuditChainDatabase.Text(database, "SELECT RuntimeEpoch FROM command_facts WHERE EventId=?;",
                deadline, row.CommandEventId.ToString("D"));
            AuditChainDatabase.Require(physical.Witness is not null && epoch == physical.Witness.RuntimeEpoch.ToString("D"),
                "CalibrationImportPhysicalWitnessEpochMismatch");
        }
        var audit = AuditChainDatabase.Read(database,
            "SELECT Payload,Hash FROM audit_entries WHERE Sequence=? AND Kind='CalibrationImportEvent' AND CalibrationImportPosition=?;",
            deadline, s => (Payload: SqliteNative.ColumnText(s, 0), Hash: SqliteNative.ColumnText(s, 1)),
            Number(row.AuditSequence), Number(row.Position)).SingleOrDefault();
        AuditChainDatabase.Require(audit.Hash == row.AuditHash && audit.Payload is not null &&
            Convert.FromBase64String(audit.Payload).AsSpan().SequenceEqual(EncodeImportAuditBinding(row)),
            "CalibrationImportAuditBindingMismatch");
    }

    private static byte[] EncodeImportAuditBinding(CalibrationImportStoredEvent row) => AuditCanonical.Encode(
        "CalibrationImportLedgerEntryV1", Number(row.Position), row.Kind, row.OperationId.ToString("D"), row.PreviousHash,
        row.RecordContentHash, row.PayloadHash, row.CommandEventId.ToString("D"), Number(row.CommandAuditSequence),
        row.CommandAuditHash, row.AuthorizationEventId.ToString("D"), Number(row.AuthorizationAuditSequence),
        row.AuthorizationAuditHash, row.Payload);

    private void AppendCalibrationImport(sqlite3 database, CalibrationImportRecord record,
        CommandAuditFact fact, IdentityAuditEvent identity, StoreDeadline deadline)
    {
        var options = _options.CalibrationImports ?? throw new InvalidOperationException("CalibrationImportConfigurationRequired");
        var rows = ReadCalibrationImportRows(database, options, deadline);
        var bytes = CalibrationGovernanceCodec.EncodeImport(record);
        AuditChainDatabase.Require(record.Position == rows.Count + 1L && record.Position <= options.MaximumEntries &&
            bytes.Length <= options.MaximumPayloadBytes &&
            rows.Sum(value => (long)DecodeImportRowPayload(value, options).Length) + bytes.Length <= options.MaximumTotalBytes,
            "CalibrationImportCapacityExceeded");
        var kind = CalibrationGovernanceCodec.ImportRecordKind(record);
        var command = ReadCommandAuditReference(database, fact.EventId, record.OperationId, kind, deadline);
        var authorization = ReadAuthorizationAuditReference(database, identity.EventId, record.OperationId,
            command.CommandKind, _policy!.StationId, deadline);
        var row = new CalibrationImportStoredEvent(record.Position, kind, record.OperationId, rows.LastOrDefault()?.AuditHash,
            record.ContentHash, Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToBase64String(bytes), fact.EventId,
            command.Sequence, command.Hash, identity.EventId, authorization.Sequence, authorization.Hash, 0, string.Empty);
        var sequence = AuditChainDatabase.AppendCalibrationImportLedgerEntry(database, _policy, _signingKey!, record.Position,
            EncodeImportAuditBinding(row), deadline);
        var hash = AuditChainDatabase.Text(database, "SELECT Hash FROM audit_entries WHERE Sequence=?;", deadline, Number(sequence));
        row = row with { AuditSequence = sequence, AuditHash = hash! };
        AuditChainDatabase.Execute(database, @"INSERT INTO calibration_import_events VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            Number(row.Position), row.Kind, row.OperationId.ToString("D"), row.PreviousHash, row.RecordContentHash, row.PayloadHash,
            row.Payload, row.CommandEventId.ToString("D"), Number(row.CommandAuditSequence), row.CommandAuditHash,
            row.AuthorizationEventId.ToString("D"), Number(row.AuthorizationAuditSequence), row.AuthorizationAuditHash,
            Number(row.AuditSequence), row.AuditHash);
        ValidateImportAuditReferences(database, row, record, deadline);
        ValidateCalibrationImportHistory(database, options, deadline);
    }

    internal static void ValidateCalibrationImportHistory(sqlite3 database, CalibrationImportStoreOptions options,
        StoreDeadline deadline)
    {
        var rows = ReadCalibrationImportRows(database, options, deadline);
        var records = rows
            .Select(value => CalibrationGovernanceCodec.DecodeImport(DecodeImportRowPayload(value, options))).ToArray();
        var governance = AuditChainDatabase.Read(database,
            "SELECT AuditSequence,Kind,Payload FROM calibration_governance_events ORDER BY AuditSequence;", deadline,
            s => (Sequence: SqliteNative.ColumnInt64(s, 0), Value: CalibrationGovernanceCodec.Decode(
                SqliteNative.ColumnText(s, 1)!, Convert.FromBase64String(SqliteNative.ColumnText(s, 2)!))));
        var prefix = new List<object>();
        var governanceIndex = 0;
        CalibrationImportHistoryValidator.Validate(records, record =>
        {
            var before = rows[checked((int)record.Position - 1)].AuditSequence;
            while (governanceIndex < governance.Count && governance[governanceIndex].Sequence < before)
                prefix.Add(governance[governanceIndex++].Value);
            return prefix;
        });
        using var blobs = new CalibrationTransferPackageStore(options.Artifacts);
        foreach (var candidate in records.OfType<ImportedCalibrationCandidate>())
        {
            if (deadline.Expired) throw new TimeoutException("CalibrationImportVerificationDeadlineExceeded");
            using var bounded = new CancellationTokenSource(deadline.Remaining);
            var package = blobs.ReadAsync(candidate.PackageHash, candidate.PackageLength, bounded.Token).GetAwaiter().GetResult();
            var decoded = CalibrationExportPackageCodec.Decode(package);
            AuditChainDatabase.Require(decoded.Manifest.PackageId == candidate.SourcePackageId &&
                decoded.Manifest.SourceStationId == candidate.SourceStationId && decoded.Manifest.ContentHash == candidate.SourceManifestHash,
                "CalibrationImportSourceBindingMismatch");
        }
    }

    private static IReadOnlyList<object> ReadCalibrationGovernanceRowsForImport(sqlite3 database, StoreDeadline deadline)
    {
        // The complete audit verifier independently checks every legacy governance row.
        // Reading both sets here uses the caller's single SQLite transaction snapshot.
        return AuditChainDatabase.Read(database, "SELECT Kind,Payload FROM calibration_governance_events ORDER BY Position;", deadline,
            s => CalibrationGovernanceCodec.Decode(SqliteNative.ColumnText(s, 0)!,
                Convert.FromBase64String(SqliteNative.ColumnText(s, 1)!)));
    }
}

internal sealed record CalibrationImportStoredEvent(long Position, string Kind, Guid OperationId, string? PreviousHash,
    string RecordContentHash, string PayloadHash, string Payload, Guid CommandEventId, long CommandAuditSequence,
    string CommandAuditHash, Guid AuthorizationEventId, long AuthorizationAuditSequence, string AuthorizationAuditHash,
    long AuditSequence, string AuditHash);
