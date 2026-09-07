using System.Globalization;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Identity;
using SQLitePCL;

namespace SharpInspect.Runtime.Integrity;

/// <summary>Typed chain operations called only inside the authoritative writer transaction.</summary>
internal static class AuditChainDatabase
{
    internal static string SchemaSql => @"
        CREATE TABLE audit_policy(Id INTEGER PRIMARY KEY CHECK(Id=1), StationId TEXT NOT NULL,
            Version TEXT NOT NULL, ContentHash TEXT NOT NULL, SigningKeyId TEXT NOT NULL, PublicKey TEXT NOT NULL);
        CREATE TABLE audit_entries(Sequence INTEGER PRIMARY KEY CHECK(Sequence>0), Kind TEXT NOT NULL,
            FactPosition INTEGER UNIQUE, Payload TEXT NOT NULL, PreviousHash TEXT NOT NULL, Hash TEXT NOT NULL);
        CREATE TABLE audit_checkpoints(Sequence INTEGER PRIMARY KEY, CheckpointId TEXT NOT NULL UNIQUE, Document TEXT NOT NULL);
        CREATE TABLE audit_anchor_receipts(CheckpointId TEXT PRIMARY KEY, Sequence INTEGER NOT NULL UNIQUE, Document TEXT NOT NULL);
        " + string.Join("\n", new[] { "audit_policy", "audit_entries", "audit_checkpoints", "audit_anchor_receipts" }
            .SelectMany(table => new[] { "UPDATE", "DELETE" }.Select(operation =>
                $"CREATE TRIGGER {table}_immutable_{operation.ToLowerInvariant()} BEFORE {operation} ON {table} BEGIN SELECT RAISE(ABORT, 'ImmutableAuditEvidence'); END;"))) +
        "PRAGMA user_version=2;";

    internal static string SchemaSqlFor(int version)
    {
        if (version == 2) return SchemaSql;
        if (version is 3 or 4 or 5 or 6)
            return SchemaSql
                .Replace("FactPosition INTEGER UNIQUE,", "FactPosition INTEGER UNIQUE, IdentityPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("Hash TEXT NOT NULL);", @"Hash TEXT NOT NULL,
            CHECK((Kind='SigningKeyCreated' AND Sequence=1 AND FactPosition IS NULL AND IdentityPosition IS NULL)
                OR (Kind='CommandFact' AND Sequence>1 AND FactPosition IS NOT NULL AND FactPosition>0 AND IdentityPosition IS NULL)
                OR (Kind='IdentityEvent' AND Sequence>1 AND IdentityPosition IS NOT NULL AND IdentityPosition>0 AND FactPosition IS NULL)));", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=2;", $@"
            CREATE INDEX ix_audit_command_sequence ON audit_entries(Sequence) WHERE FactPosition IS NOT NULL;
            CREATE INDEX ix_audit_identity_sequence ON audit_entries(Sequence) WHERE IdentityPosition IS NOT NULL;
            PRAGMA user_version={version};", StringComparison.Ordinal);
        if (version == 7)
            return SchemaSql
                .Replace("FactPosition INTEGER UNIQUE,", "FactPosition INTEGER UNIQUE, IdentityPosition INTEGER UNIQUE, AlarmPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("Hash TEXT NOT NULL);", @"Hash TEXT NOT NULL,
            CHECK((Kind='SigningKeyCreated' AND Sequence=1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL)
                OR (Kind='CommandFact' AND Sequence>1 AND FactPosition IS NOT NULL AND FactPosition>0 AND IdentityPosition IS NULL AND AlarmPosition IS NULL)
                OR (Kind='IdentityEvent' AND Sequence>1 AND IdentityPosition IS NOT NULL AND IdentityPosition>0 AND FactPosition IS NULL AND AlarmPosition IS NULL)
                OR (Kind='AlarmEvent' AND Sequence>1 AND AlarmPosition IS NOT NULL AND AlarmPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL)));", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=2;", @"
            CREATE INDEX ix_audit_command_sequence ON audit_entries(Sequence) WHERE FactPosition IS NOT NULL;
            CREATE INDEX ix_audit_identity_sequence ON audit_entries(Sequence) WHERE IdentityPosition IS NOT NULL;
            CREATE INDEX ix_audit_alarm_sequence ON audit_entries(Sequence) WHERE AlarmPosition IS NOT NULL;
            PRAGMA user_version=7;", StringComparison.Ordinal);
        throw new ArgumentOutOfRangeException(nameof(version));
    }

    internal static void CreateGenesis(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key, StoreDeadline deadline)
    {
        Execute(db, "INSERT INTO audit_policy VALUES(1,?,?,?,?,?);", deadline,
            policy.StationId, policy.Version, policy.ContentHash, key.KeyId, key.PublicKeyBase64);
        var payload = AuditCanonical.Encode("SigningKeyCreated", policy.StationId, policy.Version,
            policy.ContentHash, key.KeyId, key.PublicKeyBase64, "SharpInspect.Runtime", null,
            "InitialEmptyStoreProvisioning", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        AppendEntry(db, policy, "SigningKeyCreated", null, payload, deadline);
        CreateCheckpoint(db, policy, key, deadline);
    }

    internal static void AppendCommand(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        Guid eventId, StoreDeadline deadline)
    {
        var position = Scalar(db, "SELECT Position FROM command_facts WHERE EventId=?;", deadline, eventId.ToString("D"));
        var payload = CommandPayload(db, position, deadline);
        AppendEntry(db, policy, "CommandFact", position, payload, deadline);
        var tail = Tail(db, deadline);
        var lastCheckpoint = Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline);
        if (tail.Sequence - lastCheckpoint >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
    }

    private static void AppendEntry(sqlite3 db, AuditIntegrityPolicy policy, string kind, long? position,
        byte[] payload, StoreDeadline deadline, long? identityPosition = null, long? alarmPosition = null)
    {
        var previous = Tail(db, deadline);
        var sequence = checked(previous.Sequence + 1);
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        var hash = EntryHash(schemaVersion, policy.StationId, sequence, previous.Hash, kind,
            position is { } ordinal ? Number(ordinal) : null,
            identityPosition is { } identity ? Number(identity) : null,
            alarmPosition is { } alarm ? Number(alarm) : null, payload);
        if (schemaVersion >= 7)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p ? Number(p) : null,
                identityPosition is { } i ? Number(i) : null,
                alarmPosition is { } a ? Number(a) : null,
                Convert.ToBase64String(payload), previous.Hash, hash);
        else if (schemaVersion >= 3)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p ? Number(p) : null,
                identityPosition is { } i ? Number(i) : null,
                Convert.ToBase64String(payload), previous.Hash, hash);
        else
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p ? Number(p) : null,
                Convert.ToBase64String(payload), previous.Hash, hash);
    }

    internal static long AppendIdentity(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        IdentityAuditEvent fact, StoreDeadline deadline)
    {
        var schemaVersion = checked((int)Scalar(db, "PRAGMA user_version;", deadline));
        Require(schemaVersion is 3 or 4 or 5 or 6 or 7, "AuditSchemaInvalid");
        var previous = Tail(db, deadline);
        var sequence = checked(previous.Sequence + 1);
        var ordinal = checked(Scalar(db, "SELECT COALESCE(MAX(IdentityPosition),0) FROM audit_entries;", deadline) + 1);
        var payload = fact.Encode(ordinal, schemaVersion);
        IdentityAuditEvent.VerifyPayload(payload, ordinal, policy.StationId, schemaVersion);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(sequence), "IdentityEvent", Number(ordinal), Convert.ToBase64String(payload), previous.Hash,
            EntryHash(schemaVersion, policy.StationId, sequence, previous.Hash, "IdentityEvent", null, Number(ordinal), null, payload));
        if (sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendAlarm(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == 7, "AuditSchemaInvalid");
        var payloadText = Text(db, "SELECT Payload FROM alarm_events WHERE Position=?;", deadline, Number(position));
        Require(payloadText is { Length: > 0 and <= AlarmStorageCodec.MaximumEncodedPayloadChars }, "AlarmHistoryMissing");
        var payload = Convert.FromBase64String(payloadText!);
        AlarmStorageCodec.ValidatePayload(payload, position);
        var previous = Tail(db, deadline);
        var sequence = checked(previous.Sequence + 1);
        var hash = EntryHash(schemaVersion, policy.StationId, sequence, previous.Hash, "AlarmEvent", null,
            null, Number(position), payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,AlarmPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(sequence), "AlarmEvent", Number(position), Convert.ToBase64String(payload), previous.Hash, hash);
        if (sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    private static void CreateCheckpoint(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key, StoreDeadline deadline)
    {
        var tail = Tail(db, deadline);
        var checkpoint = AuditCheckpointCrypto.Create(policy, key, tail.Sequence, tail.Hash, DateTimeOffset.UtcNow);
        Execute(db, "INSERT INTO audit_checkpoints VALUES(?,?,?);", deadline,
            Number(checkpoint.Sequence), checkpoint.CheckpointId.ToString("D"), JsonSerializer.Serialize(checkpoint));
    }

    internal static void StoreReceipt(sqlite3 db, AuditIntegrityPolicy policy, AuditAnchorReceipt receipt, StoreDeadline deadline)
    {
        var checkpoint = ReadCheckpoint(db, "WHERE CheckpointId=?", deadline, receipt.CheckpointId.ToString("D"));
        Require(checkpoint is not null && ReceiptMatches(policy, checkpoint, receipt), "AuditAnchorReceiptMismatch");
        var existing = Text(db, "SELECT Document FROM audit_anchor_receipts WHERE CheckpointId=?;", deadline,
            receipt.CheckpointId.ToString("D"));
        if (existing is not null)
        {
            Require(ReadReceipt(existing) == receipt, "AuditAnchorReceiptConflict");
            return;
        }
        Execute(db, "INSERT INTO audit_anchor_receipts VALUES(?,?,?);", deadline,
            receipt.CheckpointId.ToString("D"), Number(receipt.Sequence), JsonSerializer.Serialize(receipt));
    }

    internal static bool ReceiptMatches(AuditIntegrityPolicy policy, AuditCheckpoint checkpoint, AuditAnchorReceipt receipt) =>
        receipt.CheckpointId == checkpoint.CheckpointId && receipt.StationId == policy.StationId &&
        receipt.Sequence == checkpoint.Sequence && receipt.HeadHash == checkpoint.HeadHash &&
        receipt.PolicyHash == checkpoint.PolicyHash && receipt.SigningKeyId == checkpoint.SigningKeyId &&
        receipt.CheckpointHash == CheckpointDigest(checkpoint) &&
        receipt.RouteId == policy.ExternalAnchorRouteId && !string.IsNullOrWhiteSpace(receipt.ReceiptId) &&
        receipt.ReceiptId.Length <= 512 && receipt.AcceptedAtUtc != default;

    internal static string CheckpointDigest(AuditCheckpoint checkpoint) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(AuditCanonical.Encode("ExternalAuditCheckpoint",
            Convert.ToBase64String(AuditCheckpointCrypto.EncodeCheckpoint(checkpoint)), checkpoint.SignatureBase64)));

    internal static AuditCheckpoint? LatestCheckpoint(sqlite3 db, StoreDeadline deadline) =>
        ReadCheckpoint(db, "ORDER BY Sequence DESC LIMIT 1", deadline);

    internal static AuditCheckpoint? NextCheckpointForDelivery(sqlite3 db, StoreDeadline deadline) =>
        ReadCheckpoint(db, "WHERE Sequence > (SELECT COALESCE(MAX(Sequence),0) FROM audit_anchor_receipts) ORDER BY Sequence LIMIT 1", deadline);

    private static AuditCheckpoint? ReadCheckpoint(sqlite3 db, string predicate, StoreDeadline deadline, params string?[] args)
    {
        // Predicates are code-owned constants. Values are always bound.
        return Read(db, "SELECT Sequence,CheckpointId,Document FROM audit_checkpoints " + predicate + ";", deadline, s =>
        {
            AuditCheckpoint cp;
            try { cp = JsonSerializer.Deserialize<AuditCheckpoint>(SqliteNative.ColumnText(s, 2)!)
                ?? throw new InvalidOperationException("AuditCheckpointInvalid"); }
            catch (JsonException) { throw new InvalidOperationException("AuditCheckpointInvalid"); }
            Require(cp.Sequence == SqliteNative.ColumnInt64(s, 0) && cp.CheckpointId.ToString("D") ==
                SqliteNative.ColumnText(s, 1), "AuditCheckpointMismatch");
            return cp;
        }, args).SingleOrDefault();
    }

    internal static AuditIntegrityReport Verify(sqlite3 db, AuditIntegrityPolicy policy, string trustedKeyId,
        string trustedPublicKey, AuditVerificationRequest request, bool startup, StoreDeadline deadline,
        bool validateAnchorReceipt = true)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is 2 or 3 or 4 or 5 or 6 or 7, "AuditSchemaInvalid");
        var hasIdentity = schemaVersion >= 3;
        var hasAlarm = schemaVersion >= 7;
        var tail = Tail(db, deadline);
        Require(tail.Sequence > 0, "AuditChainMissing");
        var genesis = Read(db, "SELECT Kind,Payload,PreviousHash,Hash,FactPosition," + (hasIdentity ? "IdentityPosition" : "NULL") + "," + (hasAlarm ? "AlarmPosition" : "NULL") + " FROM audit_entries WHERE Sequence=1;", deadline,
            s => Enumerable.Range(0, 7).Select(i => SqliteNative.ColumnText(s, i)).ToArray()).SingleOrDefault();
        Require(genesis is not null && genesis[0] == "SigningKeyCreated" && genesis[2] == AuditCanonical.GenesisHash &&
            genesis[4] is null && genesis[5] is null && genesis[6] is null,
            "AuditGenesisMissingOrInvalid");
        Require(genesis![1]?.Length <= 24000 && EntryHash(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
            genesis[0]!, genesis[4], genesis[5], genesis[6], Convert.FromBase64String(genesis[1]!)) == genesis[3], "AuditGenesisHashMismatch");
        var genesisCheckpoint = ReadCheckpoint(db, "WHERE Sequence=1", deadline);
        Require(genesisCheckpoint is not null && genesisCheckpoint.HeadHash == genesis[3], "AuditGenesisCheckpointMissing");
        VerifyCheckpoint(policy, genesisCheckpoint!, trustedKeyId, trustedPublicKey);
        var binding = Read(db, "SELECT StationId,Version,ContentHash,SigningKeyId,PublicKey FROM audit_policy WHERE Id=1;",
            deadline, s => Enumerable.Range(0, 5).Select(i => SqliteNative.ColumnText(s, i)).ToArray()).SingleOrDefault();
        Require(binding is not null && binding.SequenceEqual(new[] { policy.StationId, policy.Version,
            policy.ContentHash, trustedKeyId, trustedPublicKey }), "AuditPolicyOrKeyMismatch");
        var checkpoint = LatestCheckpoint(db, deadline);
        Require(checkpoint is not null, "AuditRequiredCheckpointMissing");
        VerifyCheckpoint(policy, checkpoint!, trustedKeyId, trustedPublicKey);
        Require(checkpoint!.Sequence <= tail.Sequence && tail.Sequence - checkpoint.Sequence < policy.CheckpointEveryEntries,
            "AuditCheckpointCadenceViolation");
        Require(Text(db, "SELECT Hash FROM audit_entries WHERE Sequence=?;", deadline, Number(checkpoint.Sequence)) ==
            checkpoint.HeadHash, "AuditCheckpointMismatch");
        // Indexed extrema detect unmatched tail appends. Retained source integrity is checked
        // for each requested segment; this is not a whole-history proof outside that segment.
        var maxFact = Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM command_facts;", deadline);
        var maxIdentity = hasIdentity ? Scalar(db, "SELECT COALESCE(MAX(IdentityPosition),0) FROM audit_entries;", deadline) : 0;
        var maxAlarm = hasAlarm ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM alarm_events;", deadline) : 0;
        Require(checked(maxFact + maxIdentity + maxAlarm) == tail.Sequence - 1 && maxFact == Scalar(db,
            "SELECT COALESCE(MAX(FactPosition),0) FROM audit_entries;", deadline) &&
            (!hasAlarm || maxAlarm == Scalar(db, "SELECT COALESCE(MAX(AlarmPosition),0) FROM audit_entries;", deadline)) &&
            Scalar(db, "SELECT COALESCE(MIN(Position),1) FROM command_facts;", deadline) == 1, "AuditUnchainedFact");
        long? anchored = null;
        if (policy.RequireExternalAnchor && validateAnchorReceipt)
        {
            VerifyStoredReceipt(db, policy, checkpoint, deadline, "AuditRequiredAnchorPending");
            anchored = checkpoint.Sequence;
        }

        // A caller-selected cursor never supplies a trust root. Recheck from a signed
        // checkpoint at/before it (or genesis) within the policy's hard verification budget.
        var prefixCheckpoint = startup ? checkpoint : ReadCheckpoint(db,
            "WHERE Sequence<=? ORDER BY Sequence DESC LIMIT 1", deadline, Number(request.AfterSequence));
        if (prefixCheckpoint is not null) VerifyCheckpoint(policy, prefixCheckpoint, trustedKeyId, trustedPublicKey);
        var after = prefixCheckpoint is null ? 0 : prefixCheckpoint.Sequence - 1;
        Require(after < tail.Sequence, "AuditVerificationCursorInvalid");
        Require(request.AfterSequence < tail.Sequence, "AuditVerificationCursorInvalid");
        var target = startup ? tail.Sequence : Math.Min(tail.Sequence, checked(request.AfterSequence + request.MaximumEntries));
        var count = checked(target - after);
        Require(count <= policy.MaximumVerificationEntries, "AuditVerificationBudgetExceeded");
        var previousHash = after == 0 ? AuditCanonical.GenesisHash : Text(db,
            "SELECT Hash FROM audit_entries WHERE Sequence=?;", deadline, Number(after));
        Require(previousHash is not null, "AuditChainGap");
        var commandOrdinal = hasIdentity ? Scalar(db, "SELECT FactPosition FROM audit_entries WHERE FactPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : after == 0 ? 0 : after - 1;
        var identityOrdinal = hasIdentity ? Scalar(db, "SELECT IdentityPosition FROM audit_entries WHERE IdentityPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var alarmOrdinal = hasAlarm ? Scalar(db, "SELECT AlarmPosition FROM audit_entries WHERE AlarmPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var rows = Read(db, "SELECT Sequence,Kind,FactPosition,Payload,PreviousHash,Hash," + (hasIdentity ? "IdentityPosition" : "NULL") + "," + (hasAlarm ? "AlarmPosition" : "NULL") + " FROM audit_entries WHERE Sequence>? ORDER BY Sequence LIMIT ?;",
            deadline, s => new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7)), Number(after), Number(count));
        var next = after + 1;
        foreach (var row in rows)
        {
            Require(row.Sequence == next++, "AuditChainGap");
            Require(row.PreviousHash == previousHash, "AuditChainLinkMismatch");
            Require(row.Payload.Length <= (hasAlarm ? AlarmStorageCodec.MaximumEncodedPayloadChars : 24000), "AuditPayloadOversize");
            var payload = Convert.FromBase64String(row.Payload);
            Require(payload.Length <= (hasAlarm ? AlarmStorageCodec.MaximumPayloadBytes : 16384), "AuditPayloadOversize");
            Require(EntryHash(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                row.FactPosition, row.IdentityPosition, row.AlarmPosition, payload) == row.Hash, "AuditPayloadHashMismatch");
            if (row.Kind == "CommandFact")
            {
                Require(long.TryParse(row.FactPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position), "AuditFactLinkMissing");
                Require(position == ++commandOrdinal && row.IdentityPosition is null, "AuditCommandPositionGap");
                Require(CommandPayload(db, position, deadline).SequenceEqual(payload), "AuditFactMismatch");
            }
            else if (hasIdentity && row.Kind == "IdentityEvent")
            {
                Require(row.FactPosition is null && long.TryParse(row.IdentityPosition, NumberStyles.None, CultureInfo.InvariantCulture,
                    out _), "AuditIdentityPositionGap");
                Require(row.IdentityPosition == Number(++identityOrdinal), "AuditIdentityPositionGap");
                IdentityAuditEvent.VerifyPayload(payload, identityOrdinal, policy.StationId, (int)schemaVersion);
            }
            else if (hasAlarm && row.Kind == "AlarmEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null,
                    "AuditAlarmPositionGap");
                if (!long.TryParse(row.AlarmPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("AuditAlarmPositionGap");
                Require(position == ++alarmOrdinal, "AuditAlarmPositionGap");
                // Validate every persisted alarm index column against the signed envelope,
                // even when a higher-level query filters that row out of its page.
                _ = AlarmStorageCodec.ReadEventAtPosition(db, position, deadline);
                Require(AlarmPayload(db, position, deadline).SequenceEqual(payload), "AuditAlarmMismatch");
                AlarmStorageCodec.ValidatePayload(payload, position);
            }
            else Require(row.Sequence == 1 && row.Kind == "SigningKeyCreated" && row.FactPosition is null && row.IdentityPosition is null,
                "AuditEntryKindUnsupported");
            var cp = ReadCheckpoint(db, "WHERE Sequence=?", deadline, Number(row.Sequence));
            if ((row.Sequence - 1) % policy.CheckpointEveryEntries == 0)
                Require(cp is not null, "AuditRequiredCheckpointMissing");
            if (cp is not null)
            {
                VerifyCheckpoint(policy, cp, trustedKeyId, trustedPublicKey);
                Require(cp.HeadHash == row.Hash, "AuditCheckpointMismatch");
                if (policy.RequireExternalAnchor && validateAnchorReceipt)
                    VerifyStoredReceipt(db, policy, cp, deadline, "AuditAnchorReceiptMissing");
            }
            previousHash = row.Hash;
        }
        var verifiedThrough = next - 1;
        if (startup) Require(verifiedThrough == tail.Sequence, "AuditVerificationBudgetExceeded");
        if (hasAlarm)
            _ = AlarmStorageCodec.ReadPersistedPolicy(db, deadline);
        return new AuditIntegrityReport(AuditIntegrityState.Verified,
            startup ? "AuditStartupTailVerified" : after == 0 && verifiedThrough == tail.Sequence ? "AuditRetainedChainVerified" : "AuditSegmentVerified",
            policy.StationId, policy.Version, tail.Sequence, after + 1, verifiedThrough,
            checkpoint.Sequence, anchored, DateTimeOffset.UtcNow);
    }

    internal static void RequireFullAlarmVerification(sqlite3 db, AuditIntegrityReport report,
        StoreDeadline deadline)
    {
        Require(report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "AlarmVerificationBudgetExceeded");
    }

    internal static void VerifyCheckpoint(AuditIntegrityPolicy policy, AuditCheckpoint cp, string keyId, string publicKey)
    {
        Require(cp.StationId == policy.StationId && cp.PolicyVersion == policy.Version && cp.PolicyHash == policy.ContentHash &&
            cp.SigningKeyId == keyId && cp.PublicKeyBase64 == publicKey && AuditCheckpointCrypto.Verify(cp), "AuditCheckpointSignatureInvalid");
    }

    private static AuditAnchorReceipt ReadReceipt(string document)
    {
        try
        {
            var receipt = JsonSerializer.Deserialize<AuditAnchorReceipt>(document);
            Require(receipt is not null && receipt.CheckpointId != Guid.Empty && receipt.StationId is not null &&
                receipt.HeadHash is not null && receipt.RouteId is not null && receipt.ReceiptId is not null &&
                receipt.PolicyHash is not null && receipt.SigningKeyId is not null && receipt.CheckpointHash is not null,
                "AuditAnchorReceiptInvalid");
            return receipt!;
        }
        catch (JsonException) { throw new InvalidOperationException("AuditAnchorReceiptInvalid"); }
    }

    private static void VerifyStoredReceipt(sqlite3 db, AuditIntegrityPolicy policy, AuditCheckpoint checkpoint,
        StoreDeadline deadline, string missingReason)
    {
        var stored = Read(db, "SELECT Sequence,Document FROM audit_anchor_receipts WHERE CheckpointId=?;", deadline,
            s => new { Sequence = SqliteNative.ColumnInt64(s, 0), Document = SqliteNative.ColumnText(s, 1)! },
            checkpoint.CheckpointId.ToString("D")).SingleOrDefault();
        Require(stored is not null, missingReason);
        var receipt = ReadReceipt(stored!.Document);
        Require(stored.Sequence == checkpoint.Sequence && ReceiptMatches(policy, checkpoint, receipt), "AuditAnchorReceiptMismatch");
    }

    private static byte[] CommandPayload(sqlite3 db, long position, StoreDeadline deadline)
    {
        var fields = Read(db, @"SELECT Position,EventId,AttemptId,CorrelationId,RuntimeEpoch,EventVersion,AggregateSequence,
            OccurredAtUtc,SystemPrincipalId,AuthenticatedHumanPrincipalId,CommandKind,Source,ClaimedPrincipalId,
            ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition,ReasonCode FROM command_facts WHERE Position=?;",
            deadline, s => Enumerable.Range(0, 18).Select(i => SqliteNative.ColumnText(s, i)).ToArray(), Number(position)).SingleOrDefault();
        Require(fields is not null, "AuditFactMissing");
        var attempt = Read(db, @"SELECT a.AttemptId,a.CorrelationId,a.RuntimeEpoch,a.CommandKind,a.Source,a.ClaimedPrincipalId,
            a.ClaimedSessionId,a.ClaimedStepUpGrantId,a.OutcomeDisposition,a.OutcomeReasonCode,a.OutcomeEventId,
            a.OutcomeOccurredAtUtc,a.SystemPrincipalId,a.AuthenticatedHumanPrincipalId FROM command_attempts a
            JOIN command_facts f ON f.AttemptId=a.AttemptId WHERE f.Position=?;", deadline,
            s => Enumerable.Range(0, 14).Select(i => SqliteNative.ColumnText(s, i)).ToArray(), Number(position)).SingleOrDefault();
        Require(attempt is not null, "AuditAttemptMissing");
        return AuditCanonical.Encode("CommandFact", fields!.Concat(attempt!).ToArray());
    }

    internal static (long Sequence, string Hash) Tail(sqlite3 db, StoreDeadline deadline) => Read(db,
        "SELECT Sequence,Hash FROM audit_entries ORDER BY Sequence DESC LIMIT 1;", deadline,
        s => (SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!)).DefaultIfEmpty((0, AuditCanonical.GenesisHash)).Single();

    internal static (long Sequence, string Hash)? LastIdentityEntry(sqlite3 db, StoreDeadline deadline) => Read(db,
        "SELECT Sequence,Hash FROM audit_entries WHERE IdentityPosition IS NOT NULL ORDER BY IdentityPosition DESC LIMIT 1;",
        deadline, s => (SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!)).SingleOrDefault();

    internal static List<T> Read<T>(sqlite3 db, string sql, StoreDeadline deadline, Func<sqlite3_stmt, T> convert, params string?[] args) =>
        SqliteNative.WithStatement(db, sql, deadline, s =>
        {
            Bind(db, s, args);
            var result = new List<T>();
            while (SqliteNative.Step(db, s, deadline) == raw.SQLITE_ROW) result.Add(convert(s));
            return result;
        });
    internal static void Execute(sqlite3 db, string sql, StoreDeadline deadline, params string?[] args) =>
        SqliteNative.WithStatement(db, sql, deadline, s => { Bind(db, s, args); SqliteNative.Step(db, s, deadline); return 0; });
    internal static long Scalar(sqlite3 db, string sql, StoreDeadline deadline, params string?[] args) =>
        Read(db, sql, deadline, s => SqliteNative.ColumnInt64(s, 0), args).SingleOrDefault();
    internal static string? Text(sqlite3 db, string sql, StoreDeadline deadline, params string?[] args) =>
        Read(db, sql, deadline, s => SqliteNative.ColumnText(s, 0), args).SingleOrDefault();
    private static void Bind(sqlite3 db, sqlite3_stmt s, string?[] args)
    {
        for (var i = 0; i < args.Length; i++) SqliteNative.BindText(db, s, i + 1, args[i]);
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash, schemaVersion >= 7
            ? AuditCanonical.Encode("AuditEntryEnvelopeV4", kind, factPosition, identityPosition, alarmPosition, Convert.ToBase64String(payload))
            : schemaVersion >= 3
                ? AuditCanonical.Encode("AuditEntryEnvelopeV3", kind, factPosition, identityPosition, Convert.ToBase64String(payload))
                : payload);
    internal static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static byte[] AlarmPayload(sqlite3 db, long position, StoreDeadline deadline)
    {
        var encoded = Text(db, "SELECT Payload FROM alarm_events WHERE Position=?;", deadline, Number(position));
        Require(encoded is { Length: > 0 and <= AlarmStorageCodec.MaximumEncodedPayloadChars }, "AlarmHistoryMissing");
        return Convert.FromBase64String(encoded!);
    }

    private sealed record ChainRow(long Sequence, string Kind, string? FactPosition, string Payload, string PreviousHash,
        string Hash, string? IdentityPosition, string? AlarmPosition);
}
