using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema-26 qualification-cycle ledger.  The ledger is deliberately a
/// projection of the StationQualification writer event: it never creates a
/// run, changes a session phase, or grants production authority by itself.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string QualificationCycleSchemaSql = @"
        CREATE TABLE qualification_cycle_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE qualification_cycle_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            RunId TEXT NULL CHECK(RunId IS NULL OR length(RunId)=36),
            ControllerEpoch INTEGER NULL CHECK(ControllerEpoch IS NULL OR ControllerEpoch>=0),
            CycleSequence INTEGER NULL CHECK(CycleSequence IS NULL OR CycleSequence>=0),
            EndpointBindingHash TEXT NOT NULL CHECK(length(EndpointBindingHash)=64),
            ProfileHash TEXT NOT NULL CHECK(length(ProfileHash)=64),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 10),
            PolicyPublicationPayload TEXT NULL,
            PolicySnapshotHash TEXT NULL CHECK(PolicySnapshotHash IS NULL OR length(PolicySnapshotHash)=64),
            QualificationPosition INTEGER NOT NULL CHECK(QualificationPosition>0),
            QualificationHash TEXT NOT NULL CHECK(length(QualificationHash)=64),
            RunContentHash TEXT NULL CHECK(RunContentHash IS NULL OR length(RunContentHash)=64),
            ReasonCode TEXT NOT NULL,
            RecordedAtUtc TEXT NOT NULL,
            Terminal INTEGER NOT NULL CHECK(Terminal IN (0,1)),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE UNIQUE INDEX ux_qualification_cycle_session_position
            ON qualification_cycle_events(SessionId,Position);
        CREATE INDEX ix_qualification_cycle_run_position
            ON qualification_cycle_events(SessionId,RunId,Position);
        CREATE TRIGGER qualification_cycle_config_immutable_update
            BEFORE UPDATE ON qualification_cycle_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableQualificationCycleConfiguration'); END;
        CREATE TRIGGER qualification_cycle_config_immutable_delete
            BEFORE DELETE ON qualification_cycle_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableQualificationCycleConfiguration'); END;
        CREATE TRIGGER qualification_cycle_event_immutable_update
            BEFORE UPDATE ON qualification_cycle_events BEGIN
            SELECT RAISE(ABORT,'ImmutableQualificationCycleEvent'); END;
        CREATE TRIGGER qualification_cycle_event_immutable_delete
            BEFORE DELETE ON qualification_cycle_events BEGIN
            SELECT RAISE(ABORT,'ImmutableQualificationCycleEvent'); END;";

    internal static void InitializeQualificationCycleSchema(sqlite3 database,
        QualificationCycleStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        SqliteNative.Execute(database, QualificationCycleSchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO qualification_cycle_store_config VALUES(1,1,?,?,?,?);", deadline,
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendQualificationCycleStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredQualificationCycles(sqlite3 database,
        QualificationCycleStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var row = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM qualification_cycle_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (Format: SqliteNative.ColumnInt64(statement, 0),
                Entries: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnInt64(statement, 2),
                Total: SqliteNative.ColumnInt64(statement, 3),
                Binding: SqliteNative.ColumnText(statement, 4))).SingleOrDefault();
        AuditChainDatabase.Require(row.Format == QualificationCycleStoreOptions.FormatVersion &&
            row.Entries == options.MaximumEntries && row.Payload == options.MaximumPayloadBytes &&
            row.Total == options.MaximumTotalBytes && row.Binding == options.BindingHash,
            "QualificationCycleConfigurationMismatch");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='QualificationCycleStoreActivated';",
            deadline) == 1, "QualificationCycleActivationMissing");
    }

    internal static IReadOnlyList<QualificationCycleStoredRow> ReadQualificationCycleRows(
        sqlite3 database, QualificationCycleStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredQualificationCycles(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,SessionId,RunId,ControllerEpoch,CycleSequence,
                EndpointBindingHash,ProfileHash,Kind,PolicyPublicationPayload,PolicySnapshotHash,
                QualificationPosition,QualificationHash,RunContentHash,ReasonCode,RecordedAtUtc,
                Terminal,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash
            FROM qualification_cycle_events ORDER BY Position LIMIT ?;", deadline,
            statement => ReadQualificationCycleRow(statement, options),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "QualificationCycleEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes,
            "QualificationCycleTotalCapacityExceeded");
        return rows;
    }

    internal static long ReadQualificationCycleAuditReserve(sqlite3 database,
        StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "qualification_cycle_events", deadline))
            return 0;
        var rows = AuditChainDatabase.Read(database,
            "SELECT Position,SessionId,Kind,RunId FROM qualification_cycle_events ORDER BY Position;",
            deadline, statement => (Position: SqliteNative.ColumnInt64(statement, 0),
                SessionId: SqliteNative.ColumnText(statement, 1)!,
                Kind: (QualificationCycleEventKind)SqliteNative.ColumnInt64(statement, 2),
                RunId: SqliteNative.ColumnText(statement, 3)));
        // A protocol rejection has no lifecycle authority.  Reserve per run,
        // rather than per session, so a late rejection or an AckReset for a
        // different run cannot release the recovery tail of an older run.
        var futureRows = rows.Where(value => value.RunId is not null &&
                value.Kind != QualificationCycleEventKind.ProtocolRequestRejected)
            .GroupBy(value => (value.SessionId, RunId: value.RunId!))
            .Sum(group => (long)QualificationCycleRemainingRows(group.OrderBy(value => value.Position).Last().Kind));
        return checked(futureRows *
            QualificationCycleStoreOptions.AuditEntriesPerEvent);
    }

    private long ReadQualificationCycleProgressAuditReserve(sqlite3 database,
        QualificationCycleWriteRequest request, StationQualificationSessionEvent qualification,
        StoreDeadline deadline)
    {
        var options = _options.QualificationCycles ??
            throw new InvalidOperationException("QualificationCycleConfigurationRequired");
        var events = ReadQualificationCycleRows(database, options, deadline)
            .Select(value => value.Event).ToArray();
        var existing = QualificationCycleFutureRows(events);
        if (request.IsProtocolRejection)
            return checked(existing * QualificationCycleStoreOptions.AuditEntriesPerEvent);
        var runId = request.RunId ?? qualification.Run?.RunId ??
            throw new InvalidOperationException("QualificationCycleRunRequired");
        var previous = events.LastOrDefault(value => value.SessionId == qualification.SessionId &&
            value.RunId?.Value == runId.Value &&
            value.Kind != QualificationCycleEventKind.ProtocolRequestRejected);
        var kind = request.Kind == QualificationCycleEventKind.CoreCommitted &&
            qualification.Run?.QualificationPayload is null
                ? QualificationCycleEventKind.CycleFaultTerminated : request.Kind;
        var future = checked(existing - (previous is null ? 0 :
            QualificationCycleRemainingRows(previous.Kind)) + QualificationCycleRemainingRows(kind));
        // The append below still validates the complete transition before COMMIT;
        // this projection only accounts for its atomic transaction footprint.
        return checked(future * QualificationCycleStoreOptions.AuditEntriesPerEvent);
    }

    internal QualificationCycleEvent AppendQualificationCycleEventRecord(
        sqlite3 database, QualificationCycleWriteRequest request,
        StationQualificationSessionEvent qualificationEvent,
        QualificationCycleStoreOptions options, StoreDeadline deadline,
        long? cycleReserveOverride = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(qualificationEvent);
        options.Validate();
        if (request.Kind == QualificationCycleEventKind.CoreCommitted && qualificationEvent.Run?.QualificationPayload is null)
            request = request with { Kind = QualificationCycleEventKind.CycleFaultTerminated };
        var rows = ReadQualificationCycleRows(database, options, deadline);
        AuditChainDatabase.Require(rows.Count < options.MaximumEntries,
            "QualificationCycleEntryCapacityExceeded");
        var endpoint = request.EndpointBindingHash ??
            qualificationEvent.Header.Plan.TransientControllerConfiguration.EndpointBindingHash;
        var profile = QualificationContractValidation.Hash(request.ProfileHash, nameof(request.ProfileHash));
        AuditChainDatabase.Require(profile == qualificationEvent.Header.Plan.ProfileHash,
            "QualificationCycleProfileMismatch");
        var runId = request.RunId ?? qualificationEvent.Run?.RunId;
        if (!request.IsProtocolRejection)
            AuditChainDatabase.Require(runId is not null, "QualificationCycleRunRequired");
        if (request.Kind == QualificationCycleEventKind.Admitted)
        {
            AuditChainDatabase.Require(runId is not null && qualificationEvent.Run is not null &&
                !qualificationEvent.Run.Terminal && request.Policy is not null &&
                qualificationEvent.Run.ControllerEpoch > 0 && qualificationEvent.Run.CycleSequence > 0,
                "QualificationCycleAdmissionInvalid");
            AuditChainDatabase.Require(rows.All(value => value.Event.RunId?.Value != runId?.Value ||
                value.Event.Kind != QualificationCycleEventKind.Admitted),
                "QualificationCycleDuplicateAdmission");
        }
        if (request.IsProtocolRejection)
        {
            AuditChainDatabase.Require(request.RejectedControllerEpoch.HasValue &&
                request.RejectedCycleSequence.HasValue, "QualificationCycleRejectedSequenceMissing");
            runId = request.RunId;
        }
        else
        {
            AuditChainDatabase.Require(qualificationEvent.Run is null ||
                qualificationEvent.Run.RunId.Value == runId!.Value,
                "QualificationCycleRunBindingMismatch");
        }

        var sameRun = rows.Where(value => value.Event.SessionId == qualificationEvent.SessionId &&
            value.Event.RunId?.Value == runId?.Value &&
            value.Event.Kind != QualificationCycleEventKind.ProtocolRequestRejected)
            .Select(value => value.Event).ToArray();
        // A protocol rejection is an observation of a refused request.  It
        // must never become the predecessor for the accepted lifecycle: its
        // rejected controller key may be absent or may refer to a different
        // run.  Continue the run from its last non-rejected lifecycle event.
        var previous = sameRun.LastOrDefault();
        if (request.Kind != QualificationCycleEventKind.Admitted && !request.IsProtocolRejection)
            AuditChainDatabase.Require(sameRun.Any(), "QualificationCycleAdmissionMissing");
        if (request.Kind == QualificationCycleEventKind.CoreCommitted)
            AuditChainDatabase.Require(qualificationEvent.Run is { Completed: true } &&
                qualificationEvent.Run.RunId.Value == runId!.Value,
                "QualificationCycleCoreBindingMismatch");
        if (request.Kind != QualificationCycleEventKind.Admitted && request.Policy is not null && previous?.PolicySnapshot is not null)
            AuditChainDatabase.Require(request.Policy.ContentHash == previous.PolicySnapshot.ContentHash,
                "QualificationCyclePolicyChanged");

        var policySnapshot = request.Kind == QualificationCycleEventKind.Admitted
            ? request.Policy
            : request.Policy ?? previous?.PolicySnapshot;
        var controllerEpoch = request.IsProtocolRejection ? request.RejectedControllerEpoch :
            request.ControllerEpoch ?? qualificationEvent.Run?.ControllerEpoch ?? previous?.ControllerEpoch;
        var cycleSequence = request.IsProtocolRejection ? request.RejectedCycleSequence :
            request.CycleSequence ?? qualificationEvent.Run?.CycleSequence ?? previous?.CycleSequence;
        var position = checked(rows.Count + 1L);
        var previousHash = rows.Count == 0 ? null : rows[^1].Event.ContentHash;
        var runContentHash = qualificationEvent.Run?.ContentHash ?? previous?.RunContentHash;
        var terminal = request.Kind is QualificationCycleEventKind.AckReset or
            QualificationCycleEventKind.AckTimeout or QualificationCycleEventKind.CycleFaultTerminated;
        var reason = request.ReasonCode ?? qualificationEvent.ReasonCode;
        var projected = new QualificationCycleEvent(position, previousHash,
            qualificationEvent.SessionId, runId, endpoint, profile, request.Kind, policySnapshot,
            qualificationEvent.Position, qualificationEvent.ContentHash, runContentHash, reason,
            qualificationEvent.RecordedAtUtc, terminal, 0, null, controllerEpoch, cycleSequence);
        ValidateQualificationCycleTransition(projected, rows.Select(value => value.Event).ToArray(),
            qualificationEvent, request.Kind == QualificationCycleEventKind.Admitted ?
                ReadQualificationCyclePolicyAt(database, qualificationEvent.AuditSequence, deadline) : null);
        var auditPayload = QualificationCycleStorageCodec.EncodeAuditPayload(projected);
        var payload = QualificationCycleStorageCodec.Encode(projected);
        var total = rows.Aggregate(0L, (sum, value) => checked(sum + value.Payload.Length));
        var futureRows = QualificationCycleFutureRows(rows.Select(value => value.Event).Append(projected));
        var persistedLength = checked(payload.Length + 68); // final audit hash length prefix and 64 ASCII bytes
        AuditChainDatabase.Require(persistedLength <= options.MaximumPayloadBytes,
            "QualificationCyclePayloadCapacityExceeded");
        AuditChainDatabase.Require(checked(rows.Count + 1L + futureRows) <= options.MaximumEntries,
            "QualificationCycleEntryCapacityExceeded");
        AuditChainDatabase.Require(checked(total + persistedLength + futureRows * options.MaximumPayloadBytes) <= options.MaximumTotalBytes,
            "QualificationCycleTotalCapacityExceeded");
        var futureReserve = cycleReserveOverride ??
            ReadQualificationCycleFutureReserve(rows, projected);
        var central = AuditChainDatabase.AppendQualificationCycleLedgerEntry(database,
            _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured"),
            _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable"),
            position, auditPayload, options, deadline, futureReserve);
        var persisted = new QualificationCycleEvent(position, previousHash,
            qualificationEvent.SessionId, runId, endpoint, profile, request.Kind, policySnapshot,
            qualificationEvent.Position, qualificationEvent.ContentHash, runContentHash, reason,
            qualificationEvent.RecordedAtUtc, terminal, central.Sequence, central.Hash,
            controllerEpoch, cycleSequence);
        payload = QualificationCycleStorageCodec.Encode(persisted);
        AuditChainDatabase.Require(payload.Length == persistedLength, "QualificationCyclePayloadSizeMismatch");
        AuditChainDatabase.Execute(database, @"
            INSERT INTO qualification_cycle_events(
                Position,PreviousHash,SessionId,RunId,ControllerEpoch,CycleSequence,
                EndpointBindingHash,ProfileHash,Kind,PolicyPublicationPayload,PolicySnapshotHash,
                QualificationPosition,QualificationHash,RunContentHash,ReasonCode,RecordedAtUtc,
                Terminal,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), previousHash,
            persisted.SessionId.ToString("D"), persisted.RunId?.Value.ToString("D"),
            persisted.ControllerEpoch?.ToString(CultureInfo.InvariantCulture),
            persisted.CycleSequence?.ToString(CultureInfo.InvariantCulture), persisted.EndpointBindingHash,
            persisted.ProfileHash, ((int)persisted.Kind).ToString(CultureInfo.InvariantCulture),
            persisted.PolicySnapshot is null ? null : Convert.ToBase64String(
                TraceStoragePolicyStorageCodec.EncodePublication(persisted.PolicySnapshot.Publication)),
            persisted.PolicySnapshot?.ContentHash,
            persisted.QualificationPosition.ToString(CultureInfo.InvariantCulture), persisted.QualificationHash,
            persisted.RunContentHash, persisted.ReasonCode,
            persisted.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            persisted.Terminal ? "1" : "0", persisted.ContentHash,
            Convert.ToHexString(SHA256.HashData(payload)), Convert.ToBase64String(payload),
            persisted.AuditSequence.ToString(CultureInfo.InvariantCulture), persisted.AuditHash!);
        return persisted;
    }

    internal static void ValidateQualificationCycleHistory(sqlite3 database,
        QualificationCycleStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        RequireConfiguredQualificationCycles(database, options, deadline);
        var rows = ReadQualificationCycleRows(database, options, deadline);
        var qualifications = ReadQualificationCycleDependencyRows(database, deadline).ToDictionary(value => value.Position);
        var verified = new List<QualificationCycleEvent>();
        var previousHash = (string?)null;
        var position = 1L;
        var admissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            AuditChainDatabase.Require(row.Position == position++ && row.PreviousHash == previousHash,
                "QualificationCyclePositionGap");
            AuditChainDatabase.Require(row.PayloadHash.Length == 64,
                "QualificationCyclePayloadBindingMismatch");
            var central = AuditChainDatabase.Read(database, @"
                SELECT Kind,QualificationCyclePosition,Payload,Hash
                FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
                statement => (Kind: SqliteNative.ColumnText(statement, 0),
                    Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                    Payload: SqliteNative.ColumnText(statement, 2),
                    Hash: SqliteNative.ColumnText(statement, 3)),
                row.Event.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(central.Kind == "QualificationCycleEvent" &&
                central.Position == row.Position && central.Hash == row.AuditHash &&
                central.Payload == Convert.ToBase64String(QualificationCycleStorageCodec.EncodeAuditPayload(row.Event)),
                "QualificationCycleCentralBindingMismatch");
            AuditChainDatabase.Require(qualifications.TryGetValue(row.Event.QualificationPosition, out var qualification),
                "QualificationCycleQualificationReferenceMissing");
            ValidateQualificationCycleTransition(row.Event, verified, qualification!.Event,
                row.Event.Kind == QualificationCycleEventKind.Admitted ?
                    ReadQualificationCyclePolicyAt(database, qualification.Event.AuditSequence, deadline) : null);
            AuditChainDatabase.Require(qualification.Event.AuditSequence < row.AuditSequence,
                "QualificationCycleQualificationAuditOrderInvalid");
            verified.Add(row.Event);
            if (row.Event.Kind == QualificationCycleEventKind.Admitted)
                AuditChainDatabase.Require(admissions.Add(row.Event.SessionId.ToString("D") + "\u001f" +
                    row.Event.RunId?.Value.ToString("D")), "QualificationCycleDuplicateAdmission");
            previousHash = row.Event.ContentHash;
        }
    }

    internal static void VerifyQualificationCycleActivationPayload(sqlite3 database,
        byte[] payload, QualificationCycleStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        options.Validate();
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()),
            "QualificationCycleActivationPayloadMismatch");
    }

    internal static void VerifyQualificationCycleAuditPayload(sqlite3 database, long position,
        byte[] payload, QualificationCycleStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(payload);
        options.Validate();
        AuditChainDatabase.Require(position > 0 && payload.Length > 0 &&
            payload.Length <= options.MaximumPayloadBytes,
            "QualificationCycleAuditPayloadInvalid");
        var row = ReadQualificationCycleRows(database, options, deadline).SingleOrDefault(value =>
            value.Position == position) ?? throw new InvalidOperationException("QualificationCycleEventMissing");
        var expected = QualificationCycleStorageCodec.EncodeAuditPayload(row.Event);
        AuditChainDatabase.Require(payload.AsSpan().SequenceEqual(expected),
            "QualificationCyclePayloadBindingMismatch");
        var central = AuditChainDatabase.Read(database, @"
            SELECT Kind,QualificationCyclePosition,Payload,Hash,Sequence
            FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2),
                Hash: SqliteNative.ColumnText(statement, 3),
                Sequence: SqliteNative.ColumnInt64(statement, 4)),
            row.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(central.Kind == "QualificationCycleEvent" &&
            central.Position == position && central.Sequence == row.AuditSequence &&
            central.Hash == row.AuditHash && central.Payload == Convert.ToBase64String(payload),
            "QualificationCycleCentralBindingMismatch");
    }

    internal static QualificationCycleEvent ReadQualificationCycleEvent(sqlite3 database,
        long position, QualificationCycleStoreOptions options, StoreDeadline deadline)
    {
        var row = ReadQualificationCycleRows(database, options, deadline).SingleOrDefault(value =>
            value.Position == position);
        return row?.Event ?? throw new InvalidOperationException("QualificationCycleEventMissing");
    }

    private static QualificationCycleStoredRow ReadQualificationCycleRow(sqlite3_stmt statement,
        QualificationCycleStoreOptions options)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var previous = SqliteNative.ColumnText(statement, 1);
        var session = ParseGuid(SqliteNative.ColumnText(statement, 2));
        var runText = SqliteNative.ColumnText(statement, 3);
        var run = string.IsNullOrWhiteSpace(runText) ? (QualificationRunId?)null :
            new QualificationRunId(ParseGuid(runText));
        var epoch = SqliteNative.ColumnInt64Nullable(statement, 4);
        var sequence = SqliteNative.ColumnInt64Nullable(statement, 5);
        var endpoint = SqliteNative.ColumnText(statement, 6)!;
        var profile = SqliteNative.ColumnText(statement, 7)!;
        var kind = (QualificationCycleEventKind)SqliteNative.ColumnInt64(statement, 8);
        var policyBytes = SqliteNative.ColumnText(statement, 9);
        var policyHash = SqliteNative.ColumnText(statement, 10);
        TraceStoragePolicySnapshot? policy = null;
        if (policyBytes is not null)
        {
            policy = new TraceStoragePolicySnapshot(TraceStoragePolicyStorageCodec.DecodePublication(
                Convert.FromBase64String(policyBytes)));
            AuditChainDatabase.Require(policyHash == policy.ContentHash,
                "QualificationCyclePolicyBindingMismatch");
        }
        else AuditChainDatabase.Require(policyHash is null, "QualificationCyclePolicyBindingMismatch");
        var qualificationPosition = SqliteNative.ColumnInt64(statement, 11);
        var qualificationHash = SqliteNative.ColumnText(statement, 12)!;
        var runContent = SqliteNative.ColumnText(statement, 13);
        var reason = SqliteNative.ColumnText(statement, 14)!;
        var recorded = ParseTime(SqliteNative.ColumnText(statement, 15));
        var terminal = SqliteNative.ColumnInt64(statement, 16) != 0;
        var contentHash = SqliteNative.ColumnText(statement, 17)!;
        var payloadHash = SqliteNative.ColumnText(statement, 18)!;
        var encoded = SqliteNative.ColumnText(statement, 19)!;
        var auditSequence = SqliteNative.ColumnInt64(statement, 20);
        var auditHash = SqliteNative.ColumnText(statement, 21)!;
        AuditChainDatabase.Require(encoded.Length <= options.MaximumPayloadBytes * 2 &&
            AuditCanonical.IsHash(contentHash) && AuditCanonical.IsHash(payloadHash) &&
            AuditCanonical.IsHash(auditHash), "QualificationCycleEventInvalid");
        var payload = Convert.FromBase64String(encoded);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes &&
            Convert.ToHexString(SHA256.HashData(payload)) == payloadHash, "QualificationCyclePayloadInvalid");
        var value = QualificationCycleStorageCodec.Decode(payload);
        AuditChainDatabase.Require(value.Position == position && value.PreviousHash == previous &&
            value.SessionId == session && value.RunId?.Value == run?.Value &&
            value.EndpointBindingHash == endpoint && value.ProfileHash == profile && value.Kind == kind &&
            value.PolicySnapshot?.ContentHash == policy?.ContentHash && value.QualificationPosition == qualificationPosition &&
            value.QualificationHash == qualificationHash && value.RunContentHash == runContent &&
            value.ReasonCode == reason && value.RecordedAtUtc == recorded && value.Terminal == terminal &&
            value.ContentHash == contentHash && value.AuditSequence == auditSequence && value.AuditHash == auditHash &&
            (value.ControllerEpoch is null ? epoch is null :
                epoch is long epochValue && value.ControllerEpoch == checked((uint)epochValue)) &&
            (value.CycleSequence is null ? sequence is null :
                sequence is long sequenceValue && value.CycleSequence == checked((uint)sequenceValue)),
            "QualificationCycleEventBindingMismatch");
        return new(position, previous, value, payload, payloadHash, auditSequence, auditHash);
    }

    private static long ReadQualificationCycleFutureReserve(
        IReadOnlyList<QualificationCycleStoredRow> rows, QualificationCycleEvent candidate)
        => checked(QualificationCycleFutureRows(rows.Select(value => value.Event).Append(candidate)) *
            QualificationCycleStoreOptions.AuditEntriesPerEvent);
}
