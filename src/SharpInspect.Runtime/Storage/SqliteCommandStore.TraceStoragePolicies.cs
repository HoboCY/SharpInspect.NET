using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal const string TraceStoragePolicySchemaSql = @"
        CREATE TABLE trace_storage_policy_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE trace_storage_policy_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            Kind INTEGER NOT NULL CHECK(Kind=1),
            Version INTEGER NOT NULL UNIQUE CHECK(Version>0),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PrincipalId TEXT NOT NULL CHECK(length(PrincipalId)=36),
            SessionId TEXT NOT NULL CHECK(length(SessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            StepUpGrantId TEXT NOT NULL CHECK(length(StepUpGrantId)=36),
            PolicyId TEXT NOT NULL, PolicyVersion TEXT NOT NULL,
            PolicyHash TEXT NOT NULL CHECK(length(PolicyHash)=64),
            PublicationHash TEXT NOT NULL CHECK(length(PublicationHash)=64),
            SnapshotHash TEXT NOT NULL CHECK(length(SnapshotHash)=64),
            PreviousContentHash TEXT NULL CHECK(PreviousContentHash IS NULL OR length(PreviousContentHash)=64),
            RecordedAtUtc TEXT NOT NULL, PublicationPayload TEXT NOT NULL,
            CommandSequence INTEGER NOT NULL CHECK(CommandSequence>0),
            CommandHash TEXT NOT NULL CHECK(length(CommandHash)=64),
            IdentitySequence INTEGER NOT NULL CHECK(IdentitySequence>0),
            IdentityHash TEXT NOT NULL CHECK(length(IdentityHash)=64),
            CentralSequence INTEGER NOT NULL CHECK(CentralSequence>0),
            CentralHash TEXT NOT NULL CHECK(length(CentralHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64));
        CREATE INDEX ix_trace_storage_policy_event_version
            ON trace_storage_policy_events(Version);
        CREATE TRIGGER trace_storage_policy_config_immutable_update
            BEFORE UPDATE ON trace_storage_policy_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableTraceStoragePolicyConfiguration'); END;
        CREATE TRIGGER trace_storage_policy_config_immutable_delete
            BEFORE DELETE ON trace_storage_policy_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableTraceStoragePolicyConfiguration'); END;
        CREATE TRIGGER trace_storage_policy_event_immutable_update
            BEFORE UPDATE ON trace_storage_policy_events BEGIN
            SELECT RAISE(ABORT,'ImmutableTraceStoragePolicyEvent'); END;
        CREATE TRIGGER trace_storage_policy_event_immutable_delete
            BEFORE DELETE ON trace_storage_policy_events BEGIN
            SELECT RAISE(ABORT,'ImmutableTraceStoragePolicyEvent'); END;";

    internal static void InitializeTraceStoragePolicySchema(sqlite3 database,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        SqliteNative.Execute(database, TraceStoragePolicySchemaSql, deadline);
        AuditChainDatabase.Execute(database,
            "INSERT INTO trace_storage_policy_store_config VALUES(1,1,?,?,?,?);", deadline,
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.BindingHash);
        AuditChainDatabase.AppendTraceStoragePolicyStoreActivation(database, policy, signingKey,
            options, deadline);
    }

    internal static void RequireConfiguredTraceStoragePolicies(sqlite3 database,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var row = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM trace_storage_policy_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => (Format: SqliteNative.ColumnInt64(statement, 0),
                Entries: SqliteNative.ColumnInt64(statement, 1), Payload: SqliteNative.ColumnInt64(statement, 2),
                Total: SqliteNative.ColumnInt64(statement, 3),
                Binding: SqliteNative.ColumnText(statement, 4))).SingleOrDefault();
        AuditChainDatabase.Require(row.Format == TraceStoragePolicyStoreOptions.FormatVersion &&
            row.Entries == options.MaximumEntries && row.Payload == options.MaximumPayloadBytes &&
            row.Total == options.MaximumTotalBytes &&
            row.Binding == options.BindingHash, "TraceStoragePolicyConfigurationMismatch");
        var activation = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='TraceStoragePolicyStoreActivated';", deadline);
        AuditChainDatabase.Require(activation == 1, "TraceStoragePolicyActivationMissing");
    }

    internal static TraceStoragePolicyReadResult ReadTraceStoragePolicyState(sqlite3 database,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline, long? version = null)
    {
        RequireConfiguredTraceStoragePolicies(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,Version,OperationId,PrincipalId,SessionId,AuthorizationRevision,
                StepUpGrantId,PolicyHash,PublicationHash,SnapshotHash,RecordedAtUtc,PublicationPayload,
                CommandSequence,CommandHash,IdentitySequence,IdentityHash,CentralSequence,CentralHash,PayloadHash,
                PolicyId,PolicyVersion,PreviousContentHash
            FROM trace_storage_policy_events
            WHERE (? IS NULL OR Version=?) ORDER BY Version LIMIT ?;", deadline,
            statement => ReadStoredRow(statement, options),
            version?.ToString(CultureInfo.InvariantCulture), version?.ToString(CultureInfo.InvariantCulture),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "TraceStoragePolicyEntryCapacityExceeded");
        if (version is not null && rows.Count == 0)
            return new(true, "TraceStoragePolicyVersionNotFound");
        if (rows.Count == 0)
            return new(true, "TraceStoragePolicyMissing");
        var row = version is null ? rows[^1] : rows[0];
        return new(true, "TraceStoragePolicyAvailable", row.Publication,
            new TraceStoragePolicySnapshot(row.Publication));
    }

    internal static IReadOnlyList<TraceStoragePolicyPublication> ReadTraceStoragePolicyPublications(
        sqlite3 database, TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredTraceStoragePolicies(database, options, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,Version,OperationId,PrincipalId,SessionId,AuthorizationRevision,
                StepUpGrantId,PolicyHash,PublicationHash,SnapshotHash,RecordedAtUtc,PublicationPayload,
                CommandSequence,CommandHash,IdentitySequence,IdentityHash,CentralSequence,CentralHash,PayloadHash,
                PolicyId,PolicyVersion,PreviousContentHash
            FROM trace_storage_policy_events ORDER BY Version LIMIT ?;", deadline,
            statement => ReadStoredRow(statement, options),
            checked(options.MaximumEntries + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "TraceStoragePolicyEntryCapacityExceeded");
        return rows.Select(row => row.Publication).ToArray();
    }

    internal static TraceStoragePolicyStoredRow ReadTraceStoragePolicyRow(sqlite3 database,
        long position, TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        RequireConfiguredTraceStoragePolicies(database, options, deadline);
        var row = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,Version,OperationId,PrincipalId,SessionId,AuthorizationRevision,
                StepUpGrantId,PolicyHash,PublicationHash,SnapshotHash,RecordedAtUtc,PublicationPayload,
                CommandSequence,CommandHash,IdentitySequence,IdentityHash,CentralSequence,CentralHash,PayloadHash,
                PolicyId,PolicyVersion,PreviousContentHash
            FROM trace_storage_policy_events WHERE Position=? LIMIT 2;", deadline,
            statement => ReadStoredRow(statement, options), position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        return row ?? throw new InvalidOperationException("TraceStoragePolicyEventMissing");
    }

    internal static long AppendTraceStoragePolicyEvent(sqlite3 database, AuditIntegrityPolicy policy,
        IAuditSigningKey signingKey, TraceStoragePolicyPublication publication, long position,
        long commandSequence, string commandHash, long identitySequence, string identityHash,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Require(position > 0 && commandSequence > 0 && identitySequence > 0 &&
            AuditCanonical.IsHash(commandHash) && AuditCanonical.IsHash(identityHash),
            "TraceStoragePolicyEventBindingInvalid");
        var policyBytes = TraceStoragePolicyStorageCodec.EncodePublication(publication);
        var snapshot = new TraceStoragePolicySnapshot(publication);
        var snapshotHash = snapshot.ContentHash;
        var payload = TraceStoragePolicyStorageCodec.EncodeEventPayload(position, publication, snapshotHash,
            commandSequence, commandHash, identitySequence, identityHash, publication.PublishedAtUtc, policyBytes);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= TraceStoragePolicyStoreOptions.MaximumPayloadBytesHardLimit,
            "TraceStoragePolicyPayloadCapacityExceeded");
        var payloadHash = TraceStoragePolicyStorageCodec.Hash(payload);
        // The central ledger stores the canonical event as base64 text.  Check
        // the exact persisted byte footprint before appending the central row;
        // the post-append check below is only a tamper/concurrency assertion.
        var persistedPayloadBytes = Convert.ToBase64String(payload).Length;
        var usedBytesBefore = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM audit_entries WHERE TraceStoragePolicyPosition IS NOT NULL;", deadline);
        AuditChainDatabase.Require(checked(usedBytesBefore + persistedPayloadBytes) <=
            options.MaximumTotalBytes, "TraceStoragePolicyTotalCapacityExceeded");
        var central = AuditChainDatabase.AppendTraceStoragePolicyLedgerEntry(database, policy, signingKey,
            position, payload, options, deadline);
        var usedEntries = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM trace_storage_policy_events;", deadline);
        var usedBytes = AuditChainDatabase.Scalar(database, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM audit_entries WHERE TraceStoragePolicyPosition IS NOT NULL;", deadline);
        AuditChainDatabase.Require(usedEntries < options.MaximumEntries &&
            usedBytes <= options.MaximumTotalBytes,
            "TraceStoragePolicyEntryCapacityExceeded");
        AuditChainDatabase.Execute(database, @"
            INSERT INTO trace_storage_policy_events(Position,Kind,Version,OperationId,PrincipalId,SessionId,
                AuthorizationRevision,StepUpGrantId,PolicyId,PolicyVersion,PolicyHash,PublicationHash,
                SnapshotHash,PreviousContentHash,RecordedAtUtc,PublicationPayload,CommandSequence,CommandHash,
                IdentitySequence,IdentityHash,CentralSequence,CentralHash,PayloadHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), ((int)TraceStoragePolicyEventKind.Published).ToString(CultureInfo.InvariantCulture),
            publication.Version.ToString(CultureInfo.InvariantCulture), publication.OperationId.ToString("D"),
            publication.PrincipalId.ToString("D"), publication.SessionId.ToString("D"),
            publication.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), publication.StepUpGrantId.ToString("D"),
            publication.Policy.PolicyId, publication.Policy.Version, publication.Policy.ContentHash,
            publication.ContentHash, snapshotHash, publication.PreviousContentHash,
            publication.PublishedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Convert.ToBase64String(policyBytes), commandSequence.ToString(CultureInfo.InvariantCulture), commandHash,
            identitySequence.ToString(CultureInfo.InvariantCulture), identityHash,
            central.Sequence.ToString(CultureInfo.InvariantCulture), central.Hash, payloadHash);
        return position;
    }

    private static TraceStoragePolicyStoredRow ReadStoredRow(sqlite3_stmt statement,
        TraceStoragePolicyStoreOptions options)
    {
        var position = SqliteNative.ColumnInt64(statement, 0);
        var kind = SqliteNative.ColumnInt64(statement, 1);
        var version = SqliteNative.ColumnInt64(statement, 2);
        var operationId = ParseGuid(SqliteNative.ColumnText(statement, 3));
        var principalId = ParseGuid(SqliteNative.ColumnText(statement, 4));
        var sessionId = ParseGuid(SqliteNative.ColumnText(statement, 5));
        var revision = SqliteNative.ColumnInt64(statement, 6);
        var grantId = ParseGuid(SqliteNative.ColumnText(statement, 7));
        var policyHash = SqliteNative.ColumnText(statement, 8)!;
        var publicationHash = SqliteNative.ColumnText(statement, 9)!;
        var snapshotHash = SqliteNative.ColumnText(statement, 10)!;
        var recordedAt = ParseTime(SqliteNative.ColumnText(statement, 11));
        var encoded = SqliteNative.ColumnText(statement, 12)!;
        var commandSequence = SqliteNative.ColumnInt64(statement, 13);
        var commandHash = SqliteNative.ColumnText(statement, 14)!;
        var identitySequence = SqliteNative.ColumnInt64(statement, 15);
        var identityHash = SqliteNative.ColumnText(statement, 16)!;
        var centralSequence = SqliteNative.ColumnInt64(statement, 17);
        var centralHash = SqliteNative.ColumnText(statement, 18)!;
        var payloadHash = SqliteNative.ColumnText(statement, 19)!;
        var policyId = SqliteNative.ColumnText(statement, 20);
        var policyVersion = SqliteNative.ColumnText(statement, 21);
        var previousContentHash = SqliteNative.ColumnText(statement, 22);
        if (kind != (long)TraceStoragePolicyEventKind.Published || version != position ||
            !AuditCanonical.IsHash(policyHash) || !AuditCanonical.IsHash(publicationHash) ||
            !AuditCanonical.IsHash(snapshotHash) || !AuditCanonical.IsHash(commandHash) ||
            !AuditCanonical.IsHash(identityHash) || !AuditCanonical.IsHash(centralHash) ||
            !AuditCanonical.IsHash(payloadHash) || commandSequence <= 0 || identitySequence <= 0 ||
            centralSequence <= 0 || encoded.Length > options.MaximumPayloadBytes * 2)
            throw new InvalidOperationException("TraceStoragePolicyEventInvalid");
        byte[] publicationBytes;
        try { publicationBytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new InvalidOperationException("TraceStoragePolicyPayloadInvalid", exception); }
        var publication = TraceStoragePolicyStorageCodec.DecodePublication(publicationBytes);
        if (publication.Version != version || publication.OperationId != operationId ||
            publication.PrincipalId != principalId || publication.SessionId != sessionId ||
            publication.AuthorizationRevision != revision || publication.StepUpGrantId != grantId ||
            publication.Policy.ContentHash != policyHash || publication.ContentHash != publicationHash ||
            publication.Policy.PolicyId != policyId || publication.Policy.Version != policyVersion ||
            publication.PreviousContentHash != previousContentHash)
            throw new InvalidOperationException("TraceStoragePolicyEventBindingMismatch");
        var expectedSnapshot = new TraceStoragePolicySnapshot(publication);
        if (expectedSnapshot.ContentHash != snapshotHash)
            throw new InvalidOperationException("TraceStoragePolicySnapshotHashMismatch");
        return new TraceStoragePolicyStoredRow(position, (TraceStoragePolicyEventKind)kind, publication,
            snapshotHash, recordedAt, commandSequence, commandHash, identitySequence, identityHash,
            centralSequence, centralHash, payloadHash, publicationBytes);
    }

    internal sealed record TraceStoragePolicyStoredRow(long Position, TraceStoragePolicyEventKind Kind,
        TraceStoragePolicyPublication Publication, string SnapshotHash, DateTimeOffset RecordedAtUtc,
        long CommandSequence, string CommandHash, long IdentitySequence, string IdentityHash,
        long CentralSequence, string CentralHash, string PayloadHash, byte[] PublicationBytes);
}
