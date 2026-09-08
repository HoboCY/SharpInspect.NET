using System.Globalization;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Cameras;
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
        if (version == 8)
            return SchemaSql
                .Replace("FactPosition INTEGER UNIQUE,", "FactPosition INTEGER UNIQUE, IdentityPosition INTEGER UNIQUE, AlarmPosition INTEGER UNIQUE, ResultPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("Hash TEXT NOT NULL);", @"Hash TEXT NOT NULL,
            CHECK((Kind='SigningKeyCreated' AND Sequence=1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL)
                OR (Kind='CommandFact' AND Sequence>1 AND FactPosition IS NOT NULL AND FactPosition>0 AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL)
                OR (Kind='IdentityEvent' AND Sequence>1 AND IdentityPosition IS NOT NULL AND IdentityPosition>0 AND FactPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL)
                OR (Kind='AlarmEvent' AND Sequence>1 AND AlarmPosition IS NOT NULL AND AlarmPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND ResultPosition IS NULL)
                OR (Kind='AlgorithmArchiveActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL)
                OR (Kind='AlgorithmComputation' AND Sequence>1 AND ResultPosition IS NOT NULL AND ResultPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL)));", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=2;", @"
            CREATE INDEX ix_audit_command_sequence ON audit_entries(Sequence) WHERE FactPosition IS NOT NULL;
            CREATE INDEX ix_audit_identity_sequence ON audit_entries(Sequence) WHERE IdentityPosition IS NOT NULL;
            CREATE INDEX ix_audit_alarm_sequence ON audit_entries(Sequence) WHERE AlarmPosition IS NOT NULL;
            CREATE INDEX ix_audit_result_sequence ON audit_entries(Sequence) WHERE ResultPosition IS NOT NULL;
            PRAGMA user_version=8;", StringComparison.Ordinal);
        if (version == RecipeDraftStoreOptions.SchemaVersion)
            return SchemaSql
                .Replace("FactPosition INTEGER UNIQUE,", "FactPosition INTEGER UNIQUE, IdentityPosition INTEGER UNIQUE, AlarmPosition INTEGER UNIQUE, ResultPosition INTEGER UNIQUE, DraftPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("Hash TEXT NOT NULL);", @"Hash TEXT NOT NULL,
            CHECK((Kind='SigningKeyCreated' AND Sequence=1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='CommandFact' AND Sequence>1 AND FactPosition IS NOT NULL AND FactPosition>0 AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='IdentityEvent' AND Sequence>1 AND IdentityPosition IS NOT NULL AND IdentityPosition>0 AND FactPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='AlarmEvent' AND Sequence>1 AND AlarmPosition IS NOT NULL AND AlarmPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='AlgorithmArchiveActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='AlgorithmComputation' AND Sequence>1 AND ResultPosition IS NOT NULL AND ResultPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='RecipeDraftStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)
                OR (Kind='RecipeDraftRevision' AND Sequence>1 AND DraftPosition IS NOT NULL AND DraftPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL)));", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=2;", @"
            CREATE INDEX ix_audit_command_sequence ON audit_entries(Sequence) WHERE FactPosition IS NOT NULL;
            CREATE INDEX ix_audit_identity_sequence ON audit_entries(Sequence) WHERE IdentityPosition IS NOT NULL;
            CREATE INDEX ix_audit_alarm_sequence ON audit_entries(Sequence) WHERE AlarmPosition IS NOT NULL;
            CREATE INDEX ix_audit_result_sequence ON audit_entries(Sequence) WHERE ResultPosition IS NOT NULL;
            CREATE INDEX ix_audit_draft_sequence ON audit_entries(Sequence) WHERE DraftPosition IS NOT NULL;
            PRAGMA user_version=9;", StringComparison.Ordinal);
        if (version == CameraSetupStoreOptions.SchemaVersion)
        {
            var sql = SchemaSqlFor(RecipeDraftStoreOptions.SchemaVersion)
                .Replace("DraftPosition INTEGER UNIQUE,", "DraftPosition INTEGER UNIQUE, CameraPosition INTEGER UNIQUE,",
                    StringComparison.Ordinal)
                .Replace("DraftPosition IS NULL)", "DraftPosition IS NULL AND CameraPosition IS NULL)",
                    StringComparison.Ordinal);
            // The schema 9 CHECK ends with the RecipeDraftRevision alternative.
            // Insert the two schema 10 alternatives immediately before the
            // CHECK's closing parentheses.  Replacing a shorter suffix here is
            // unsafe because every prior alternative also contains a nullable
            // ResultPosition/DraftPosition pair.
            const string recipeRevision = "             OR (Kind='RecipeDraftRevision'";
            var recipeStart = sql.LastIndexOf(recipeRevision, StringComparison.Ordinal);
            var checkClose = recipeStart < 0 ? -1 : sql.IndexOf(")));", recipeStart,
                StringComparison.Ordinal);
            if (recipeStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='CameraSetupStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL)
                 OR (Kind='CameraSetupEvent' AND Sequence>1 AND CameraPosition IS NOT NULL AND CameraPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL)");
            sql = sql.Replace("PRAGMA user_version=9;", @"
             CREATE INDEX ix_audit_camera_sequence ON audit_entries(Sequence) WHERE CameraPosition IS NOT NULL;
             PRAGMA user_version=10;", StringComparison.Ordinal);
            return sql;
        }
        if (version == CameraRecoveryStoreOptions.SchemaVersion)
        {
            // Schema 11 keeps the schema-10 audit envelope and camera
            // positions byte-for-byte. Recovery terminal events are typed in
            // their own bounded ledger and therefore remain metadata entries
            // with all position columns NULL.
            var sql = SchemaSqlFor(CameraSetupStoreOptions.SchemaVersion)
                .Replace("PRAGMA user_version=10;", "PRAGMA user_version=11;", StringComparison.Ordinal);
            const string cameraEvent = " OR (Kind='CameraSetupEvent'";
            var cameraStart = sql.LastIndexOf(cameraEvent, StringComparison.Ordinal);
            var checkClose = cameraStart < 0 ? -1 : sql.IndexOf(")));", cameraStart, StringComparison.Ordinal);
            if (cameraStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='CameraRecoveryStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL)
                 OR (Kind='CameraRecoveryEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL)");
            return sql;
        }
        if (version == CameraNetworkStoreOptions.SchemaVersion)
        {
            // Schema 12 preserves every schema-11 column and envelope byte. The
            // only audit-chain addition is an independent NetworkPosition
            // binding for the bounded network ledger.
            var sql = SchemaSqlFor(CameraRecoveryStoreOptions.SchemaVersion)
                .Replace("CameraPosition INTEGER UNIQUE,", "CameraPosition INTEGER UNIQUE, NetworkPosition INTEGER UNIQUE,",
                    StringComparison.Ordinal)
                .Replace("CameraPosition IS NULL)", "CameraPosition IS NULL AND NetworkPosition IS NULL)",
                    StringComparison.Ordinal)
                .Replace("PRAGMA user_version=11;", @"
             CREATE INDEX ix_audit_network_sequence ON audit_entries(Sequence) WHERE NetworkPosition IS NOT NULL;
             PRAGMA user_version=12;", StringComparison.Ordinal);
            const string recoveryEvent = " OR (Kind='CameraRecoveryEvent'";
            var recoveryStart = sql.LastIndexOf(recoveryEvent, StringComparison.Ordinal);
            var checkClose = recoveryStart < 0 ? -1 : sql.IndexOf(")));", recoveryStart,
                StringComparison.Ordinal);
            if (recoveryStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='CameraNetworkStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL)
                 OR (Kind='CameraNetworkEvent' AND Sequence>1 AND NetworkPosition IS NOT NULL AND NetworkPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL)");
            return sql;
        }
        if (version == ImagingSetupStoreOptions.SchemaVersion)
        {
            // Schema 13 preserves the schema-12 envelope and adds one independent
            // position column for the immutable imaging declaration ledger.
            var sql = SchemaSqlFor(CameraNetworkStoreOptions.SchemaVersion)
                .Replace("NetworkPosition INTEGER UNIQUE,", "NetworkPosition INTEGER UNIQUE, ImagingPosition INTEGER UNIQUE,",
                    StringComparison.Ordinal)
                .Replace("NetworkPosition IS NULL)", "NetworkPosition IS NULL AND ImagingPosition IS NULL)",
                    StringComparison.Ordinal)
                .Replace("PRAGMA user_version=12;", @"
             CREATE INDEX ix_audit_imaging_sequence ON audit_entries(Sequence) WHERE ImagingPosition IS NOT NULL;
             PRAGMA user_version=13;", StringComparison.Ordinal);
            const string networkEvent = " OR (Kind='CameraNetworkEvent'";
            var networkStart = sql.LastIndexOf(networkEvent, StringComparison.Ordinal);
            var checkClose = networkStart < 0 ? -1 : sql.IndexOf(")));", networkStart,
                StringComparison.Ordinal);
            if (networkStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='ImagingSetupStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL)
                 OR (Kind='ImagingSetupRevision' AND Sequence>1 AND ImagingPosition IS NOT NULL AND ImagingPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL)");
            return sql;
        }
        throw new ArgumentOutOfRangeException(nameof(version));
    }

    internal static bool IsCapacityReason(string reason) => reason is
        "AuditVerificationCapacityExceeded" or "AlgorithmResultArchiveCapacityExceeded" or
        "RecipeDraftCapacityExceeded" or "RecipeDraftArchiveCapacityExceeded" or
        "RecipeDraftRevisionCapacityExceeded" or "RecipeDraftTotalCapacityExceeded" or
        "CameraSetupCapacityExceeded" or "CameraSetupEventCapacityExceeded" or
        "CameraSetupTotalCapacityExceeded" or "CameraRecoveryEventCapacityExceeded" or
        "CameraRecoveryTotalCapacityExceeded" or "CameraNetworkCapacityExceeded" or
        "CameraNetworkEventCapacityExceeded" or "CameraNetworkTotalCapacityExceeded" or
        "CameraNetworkPendingCapacityExceeded" or "ImagingSetupCapacityExceeded" or
        "ImagingSetupRevisionCapacityExceeded" or "ImagingSetupTotalCapacityExceeded" or
        "ImagingSetupPayloadCapacityExceeded";

    internal enum CameraNetworkAuditWriteMode
    {
        Generic,
        Admission,
        Terminal
    }

    private static long PendingCameraNetworkOperations(sqlite3 db, StoreDeadline deadline)
    {
        // Empty-store genesis precedes optional ledger creation in the same
        // initialization transaction. Existing stores validate their full shape.
        if (Scalar(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_network_events';",
                deadline) == 0) return 0;
        return Scalar(db, @"
            SELECT COUNT(*) FROM camera_network_events admission
            WHERE admission.Phase=? AND NOT EXISTS(
                SELECT 1 FROM camera_network_events terminal
                WHERE terminal.OperationId=admission.OperationId AND terminal.Phase=?);",
            deadline, ((int)CameraNetworkEventPhase.Admission).ToString(CultureInfo.InvariantCulture),
            ((int)CameraNetworkEventPhase.Terminal).ToString(CultureInfo.InvariantCulture));
    }

    private static long CameraNetworkReservedTerminalOperations(sqlite3 db,
        int schemaVersion, CameraNetworkAuditWriteMode mode, StoreDeadline deadline)
    {
        if (schemaVersion is not (CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) ||
            !TableExists(db, "camera_network_events", deadline)) return 0;
        var pending = PendingCameraNetworkOperations(db, deadline);
        return mode switch
        {
            CameraNetworkAuditWriteMode.Admission => checked(pending + 1),
            CameraNetworkAuditWriteMode.Terminal => Math.Max(0, pending - 1),
            _ => pending
        };
    }

    private static (long Sequence, string PreviousHash, int SchemaVersion) NextSequence(
        sqlite3 db, AuditIntegrityPolicy policy, StoreDeadline deadline, bool archiveData,
        bool recipeDraftData = false, bool cameraSetupData = false, bool cameraNetworkData = false,
        bool imagingData = false,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic)
    {
        var schemaVersion = checked((int)Scalar(db, "PRAGMA user_version;", deadline));
        var previous = Tail(db, deadline);
        var sequence = checked(previous.Sequence + 1);
        if (schemaVersion >= AlgorithmResultArchiveOptions.SchemaVersion)
        {
            var controlReserve = archiveData
                ? recipeDraftData ? RecipeDraftStoreOptions.SharedAuditControlReserve
                    : AlgorithmResultArchiveOptions.ControlVerificationReserve
                : cameraSetupData ? CameraSetupStoreOptions.ControlVerificationReserve
                : imagingData ? ImagingSetupStoreOptions.ControlVerificationReserve : 0;
            if (schemaVersion == CameraNetworkStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, CameraNetworkStoreOptions.ControlVerificationReserve);
            if (schemaVersion == ImagingSetupStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, ImagingSetupStoreOptions.ControlVerificationReserve);
            var reservedTerminalOperations = CameraNetworkReservedTerminalOperations(db,
                schemaVersion, cameraNetworkMode, deadline);
            var limit = checked(policy.MaximumVerificationEntries - controlReserve -
                reservedTerminalOperations * CameraNetworkStoreOptions.AuditEntriesPerTerminal);
            Require(sequence <= limit, archiveData
                ? recipeDraftData ? "RecipeDraftArchiveCapacityExceeded" : "AlgorithmResultArchiveCapacityExceeded"
                : cameraNetworkData || schemaVersion == CameraNetworkStoreOptions.SchemaVersion
                    ? "CameraNetworkCapacityExceeded"
                : imagingData ? "ImagingSetupCapacityExceeded"
                : cameraSetupData ? "CameraSetupCapacityExceeded" : "AuditVerificationCapacityExceeded");
        }
        return (sequence, previous.Hash, schemaVersion);
    }

    internal static void EnsureNextSequenceAvailable(sqlite3 db, AuditIntegrityPolicy policy,
        StoreDeadline deadline, bool archiveData, bool recipeDraftData = false, bool cameraSetupData = false,
        bool cameraNetworkData = false, bool imagingData = false) =>
        _ = NextSequence(db, policy, deadline, archiveData, recipeDraftData, cameraSetupData, cameraNetworkData,
            imagingData);

    internal static void EnsureCameraNetworkTransactionCapacity(sqlite3 db, AuditIntegrityPolicy policy,
        CameraNetworkEventPhase phase, StoreDeadline deadline)
    {
        var pending = PendingCameraNetworkOperations(db, deadline);
        Require(phase != CameraNetworkEventPhase.Terminal || pending > 0, "CameraNetworkAdmissionMissing");
        var remaining = phase == CameraNetworkEventPhase.Admission ? pending + 1 :
            phase == CameraNetworkEventPhase.Terminal ? pending - 1 : pending;
        var entries = phase == CameraNetworkEventPhase.Rejected ? 2 : 3;
        Require(checked(Tail(db, deadline).Sequence + entries) <=
            policy.MaximumVerificationEntries - CameraNetworkStoreOptions.ControlVerificationReserve -
            remaining * CameraNetworkStoreOptions.AuditEntriesPerTerminal, "CameraNetworkCapacityExceeded");
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
        Guid eventId, StoreDeadline deadline,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic)
    {
        var position = Scalar(db, "SELECT Position FROM command_facts WHERE EventId=?;", deadline, eventId.ToString("D"));
        var payload = CommandPayload(db, position, deadline);
        AppendEntry(db, policy, "CommandFact", position, payload, deadline,
            cameraNetworkMode: cameraNetworkMode);
        var tail = Tail(db, deadline);
        var lastCheckpoint = Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline);
        if (tail.Sequence - lastCheckpoint >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
    }

    private static void AppendEntry(sqlite3 db, AuditIntegrityPolicy policy, string kind, long? position,
        byte[] payload, StoreDeadline deadline, long? identityPosition = null, long? alarmPosition = null,
        long? resultPosition = null, long? draftPosition = null, long? networkPosition = null,
        long? imagingPosition = null,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic)
    {
        var next = NextSequence(db, policy, deadline, archiveData: false,
            cameraNetworkData: cameraNetworkMode != CameraNetworkAuditWriteMode.Generic,
            imagingData: imagingPosition is not null || kind is "ImagingSetupRevision" or "ImagingSetupStoreActivated",
            cameraNetworkMode: cameraNetworkMode);
        var sequence = next.Sequence;
        var previousHash = next.PreviousHash;
        var schemaVersion = next.SchemaVersion;
        var hash = EntryHash(schemaVersion, policy.StationId, sequence, previousHash, kind,
            position is { } ordinal ? Number(ordinal) : null,
            identityPosition is { } identity ? Number(identity) : null,
            alarmPosition is { } alarm ? Number(alarm) : null,
            resultPosition is { } result ? Number(result) : null,
            draftPosition is { } draft ? Number(draft) : null,
            null,
            networkPosition is { } network ? Number(network) : null,
            imagingPosition is { } imaging ? Number(imaging) : null, payload);
        if (schemaVersion >= ImagingSetupStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p13 ? Number(p13) : null,
                identityPosition is { } i13 ? Number(i13) : null,
                alarmPosition is { } a13 ? Number(a13) : null,
                resultPosition is { } r13 ? Number(r13) : null,
                draftPosition is { } d13 ? Number(d13) : null,
                null, networkPosition is { } n13 ? Number(n13) : null,
                imagingPosition is { } m13 ? Number(m13) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= CameraNetworkStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p12 ? Number(p12) : null,
                identityPosition is { } i12 ? Number(i12) : null,
                alarmPosition is { } a12 ? Number(a12) : null,
                resultPosition is { } r12 ? Number(r12) : null,
                draftPosition is { } d12 ? Number(d12) : null,
                null, networkPosition is { } n12 ? Number(n12) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= RecipeDraftStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p9 ? Number(p9) : null,
                identityPosition is { } i9 ? Number(i9) : null,
                alarmPosition is { } a9 ? Number(a9) : null,
                resultPosition is { } r9 ? Number(r9) : null,
                draftPosition is { } d9 ? Number(d9) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= 8)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p8 ? Number(p8) : null,
                identityPosition is { } i8 ? Number(i8) : null,
                alarmPosition is { } a8 ? Number(a8) : null,
                resultPosition is { } r8 ? Number(r8) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= 7)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p ? Number(p) : null,
                identityPosition is { } i ? Number(i) : null,
                alarmPosition is { } a ? Number(a) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= 3)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p ? Number(p) : null,
                identityPosition is { } i ? Number(i) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p ? Number(p) : null,
                Convert.ToBase64String(payload), previousHash, hash);
    }

    internal static long AppendIdentity(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        IdentityAuditEvent fact, StoreDeadline deadline,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic)
    {
        var schemaVersion = checked((int)Scalar(db, "PRAGMA user_version;", deadline));
        Require(schemaVersion is 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13, "AuditSchemaInvalid");
        var next = NextSequence(db, policy, deadline, archiveData: false,
            cameraNetworkMode: cameraNetworkMode);
        var sequence = next.Sequence;
        var ordinal = checked(Scalar(db, "SELECT COALESCE(MAX(IdentityPosition),0) FROM audit_entries;", deadline) + 1);
        var payload = fact.Encode(ordinal, schemaVersion);
        IdentityAuditEvent.VerifyPayload(payload, ordinal, policy.StationId, schemaVersion);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(sequence), "IdentityEvent", Number(ordinal), Convert.ToBase64String(payload), next.PreviousHash,
            EntryHash(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent", null, Number(ordinal), null, null, payload));
        if (sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendAlarm(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion >= 7, "AuditSchemaInvalid");
        var payloadText = Text(db, "SELECT Payload FROM alarm_events WHERE Position=?;", deadline, Number(position));
        Require(payloadText is { Length: > 0 and <= AlarmStorageCodec.MaximumEncodedPayloadChars }, "AlarmHistoryMissing");
        var payload = Convert.FromBase64String(payloadText!);
        AlarmStorageCodec.ValidatePayload(payload, position);
        var next = NextSequence(db, policy, deadline, archiveData: false);
        var sequence = next.Sequence;
        var hash = EntryHash(schemaVersion, policy.StationId, sequence, next.PreviousHash, "AlarmEvent", null,
            null, Number(position), null, payload);
        if (schemaVersion >= 8)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,AlarmPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
                deadline, Number(sequence), "AlarmEvent", Number(position), Convert.ToBase64String(payload), next.PreviousHash, hash);
        else
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,AlarmPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
                deadline, Number(sequence), "AlarmEvent", Number(position), Convert.ToBase64String(payload), next.PreviousHash, hash);
        if (sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendAlgorithmArchiveActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, AlgorithmResultArchiveOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is AlgorithmResultArchiveOptions.SchemaVersion or RecipeDraftStoreOptions.SchemaVersion or
            CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='AlgorithmArchiveActivated';", deadline) == 0,
            "AlgorithmResultArchiveActivationConflict");
        var payload = options.EncodeActivationPayload();
        var next = NextSequence(db, policy, deadline, archiveData: false);
        var sequence = next.Sequence;
        var hash = EntryHash(schemaVersion, policy.StationId, sequence, next.PreviousHash,
            "AlgorithmArchiveActivated", null, null, null, null, payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?);",
            deadline, Number(sequence), "AlgorithmArchiveActivated", Convert.ToBase64String(payload), next.PreviousHash, hash);
        if (sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendAlgorithmComputation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is AlgorithmResultArchiveOptions.SchemaVersion or RecipeDraftStoreOptions.SchemaVersion or
            CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        var payload = SqliteCommandStore.ReadAuditBindingPayload(db, position, deadline);
        var next = NextSequence(db, policy, deadline, archiveData: true);
        var sequence = next.Sequence;
        var hash = EntryHash(schemaVersion, policy.StationId, sequence, next.PreviousHash,
            "AlgorithmComputation", null, null, null, Number(position), payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,ResultPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(sequence), "AlgorithmComputation", Number(position), Convert.ToBase64String(payload), next.PreviousHash, hash);
        if (sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendRecipeDraftStoreActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, RecipeDraftStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
            CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
            ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeDraftStoreActivated';", deadline) == 0,
            "RecipeDraftActivationConflict");
        var payload = AuditCanonical.Encode("RecipeDraftStoreActivated", options.BindingHash,
            options.ExecutionPolicy.Id, options.ExecutionPolicy.Version, options.ExecutionPolicy.ContentHash,
            options.MaximumRecordBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumPageBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumRevisionCount.ToString(CultureInfo.InvariantCulture), options.RequireStepUp ? "1" : "0");
        var next = NextSequence(db, policy, deadline, archiveData: false);
        var hash = EntryHash(schemaVersion, policy.StationId, next.Sequence, next.PreviousHash,
            "RecipeDraftStoreActivated", null, null, null, null, payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?);",
            deadline, Number(next.Sequence), "RecipeDraftStoreActivated", Convert.ToBase64String(payload),
            next.PreviousHash, hash);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendRecipeDraftRevision(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
            CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
            ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        var payload = SqliteCommandStore.ReadRecipeDraftBindingPayload(db, position, deadline);
        var next = NextSequence(db, policy, deadline, archiveData: true, recipeDraftData: true);
        var hash = EntryHash(schemaVersion, policy.StationId, next.Sequence, next.PreviousHash,
            "RecipeDraftRevision", null, null, null, null, Number(position), payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,DraftPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(next.Sequence), "RecipeDraftRevision", Number(position), Convert.ToBase64String(payload),
            next.PreviousHash, hash);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendCameraSetupActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, CameraSetupStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraSetupStoreActivated';", deadline) == 0,
            "CameraSetupActivationConflict");
        var payload = options.EncodeActivationPayload();
        var next = NextSequence(db, policy, deadline, archiveData: false);
        var hash = EntryHash(schemaVersion, policy.StationId, next.Sequence, next.PreviousHash,
            "CameraSetupStoreActivated", null, null, null, null, null, payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?);",
            deadline, Number(next.Sequence), "CameraSetupStoreActivated", Convert.ToBase64String(payload),
            next.PreviousHash, hash);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendCameraSetupEvent(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        var payloadText = Text(db, "SELECT Payload FROM camera_setup_events WHERE Position=?;", deadline, Number(position));
        Require(payloadText is { Length: > 0 and <= CameraSetupStorageCodec.MaximumEncodedPayloadChars },
            "CameraSetupEventMissing");
        var payload = Convert.FromBase64String(payloadText!);
        CameraSetupStorageCodec.ValidatePayload(payload, position);
        var next = NextSequence(db, policy, deadline, archiveData: false, cameraSetupData: true);
        var hash = EntryHash(schemaVersion, policy.StationId, next.Sequence, next.PreviousHash,
            "CameraSetupEvent", null, null, null, null, null, Number(position), payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,CameraPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(next.Sequence), "CameraSetupEvent", Number(position), Convert.ToBase64String(payload),
            next.PreviousHash, hash);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendCameraRecoveryActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, CameraRecoveryStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
            ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraRecoveryStoreActivated';", deadline) == 0,
            "CameraRecoveryActivationConflict");
        var payload = options.EncodeActivationPayload();
        var next = NextSequence(db, policy, deadline, archiveData: false, cameraSetupData: true);
        AppendEntry(db, policy, "CameraRecoveryStoreActivated", null, payload, deadline);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendCameraRecoveryEvent(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
            ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        var payloadText = Text(db, "SELECT Payload FROM camera_recovery_terminal_events WHERE Position=?;",
            deadline, Number(position));
        Require(payloadText is { Length: > 0 and <= CameraRecoveryStorageCodec.MaximumEncodedPayloadChars },
            "CameraRecoveryTerminalMissing");
        var payload = Convert.FromBase64String(payloadText!);
        _ = SqliteCommandStore.ReadAndValidateCameraRecovery(db, position, payload, deadline);
        var next = NextSequence(db, policy, deadline, archiveData: false, cameraSetupData: true);
        AppendEntry(db, policy, "CameraRecoveryEvent", null, payload, deadline);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendCameraNetworkActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, CameraNetworkStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraNetworkStoreActivated';", deadline) == 0,
            "CameraNetworkActivationConflict");
        var payload = options.EncodeActivationPayload();
        var next = NextSequence(db, policy, deadline, archiveData: false);
        AppendEntry(db, policy, "CameraNetworkStoreActivated", null, payload, deadline);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendCameraNetworkEvent(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        var payloadText = Text(db, "SELECT Payload FROM camera_network_events WHERE Position=?;",
            deadline, Number(position));
        Require(payloadText is { Length: > 0 and <= CameraNetworkStorageCodec.MaximumEncodedPayloadChars },
            "CameraNetworkEventMissing");
        var payload = Convert.FromBase64String(payloadText!);
        var value = CameraNetworkStorageCodec.Decode(payload, position);
        CameraNetworkStorageCodec.Validate(value);
        var next = NextSequence(db, policy, deadline, archiveData: false, cameraNetworkData: true);
        var hash = EntryHash(schemaVersion, policy.StationId, next.Sequence, next.PreviousHash,
            "CameraNetworkEvent", null, null, null, null, null, null, Number(position), payload);
        Execute(db, "INSERT INTO audit_entries(Sequence,Kind,NetworkPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
            deadline, Number(next.Sequence), "CameraNetworkEvent", Number(position), Convert.ToBase64String(payload),
            next.PreviousHash, hash);
        if (next.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return next.Sequence;
    }

    internal static long AppendImagingSetupActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, ImagingSetupStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == ImagingSetupStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImagingSetupStoreActivated';",
            deadline) == 0, "ImagingSetupActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "ImagingSetupStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendImagingSetupRevision(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, ImagingSetupStoreOptions options, long position, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == ImagingSetupStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        var payloadText = Text(db, "SELECT Payload FROM imaging_setup_revisions WHERE Position=?;",
            deadline, Number(position));
        Require(payloadText is { Length: > 0 and <= ImagingSetupRevisionStorageCodec.MaximumEncodedPayloadChars },
            "ImagingSetupRevisionMissing");
        var payload = Convert.FromBase64String(payloadText!);
        SqliteCommandStore.ReadAndValidateImagingSetup(db, position, payload, deadline, options);
        AppendEntry(db, policy, "ImagingSetupRevision", null, payload, deadline,
            imagingPosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
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
        bool validateAnchorReceipt = true, AlgorithmResultArchiveOptions? archiveOptions = null,
        RecipeDraftStoreOptions? recipeDraftOptions = null,
        CameraSetupStoreOptions? cameraSetupOptions = null,
        CameraRecoveryStoreOptions? cameraRecoveryOptions = null,
        CameraNetworkStoreOptions? cameraNetworkOptions = null,
        ImagingSetupStoreOptions? imagingSetupOptions = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13, "AuditSchemaInvalid");
        var hasIdentity = schemaVersion >= 3;
        var hasAlarm = schemaVersion >= 7;
        var hasCamera = schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion;
        var hasRecovery = schemaVersion == CameraRecoveryStoreOptions.SchemaVersion ||
            (schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) && cameraRecoveryOptions is not null;
        var hasNetwork = schemaVersion == CameraNetworkStoreOptions.SchemaVersion ||
            schemaVersion == ImagingSetupStoreOptions.SchemaVersion && cameraNetworkOptions is not null;
        var hasImaging = schemaVersion == ImagingSetupStoreOptions.SchemaVersion;
        if (hasCamera != (cameraSetupOptions is not null))
            throw new InvalidOperationException(hasCamera
                ? "CameraSetupConfigurationRequired" : "CameraSetupGovernedMigrationRequired");
        if (hasRecovery != (cameraRecoveryOptions is not null))
            throw new InvalidOperationException(hasRecovery
                ? "CameraRecoveryConfigurationRequired" : "CameraRecoveryGovernedMigrationRequired");
        if (hasNetwork != (cameraNetworkOptions is not null))
            throw new InvalidOperationException(hasNetwork
                ? "CameraNetworkConfigurationRequired" : "CameraNetworkGovernedMigrationRequired");
        if (hasImaging != (imagingSetupOptions is not null))
            throw new InvalidOperationException(hasImaging
                ? "ImagingSetupConfigurationRequired" : "ImagingSetupGovernedMigrationRequired");
        var hasArchive = schemaVersion == AlgorithmResultArchiveOptions.SchemaVersion ||
            ((schemaVersion is RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
                CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                ImagingSetupStoreOptions.SchemaVersion) && archiveOptions is not null);
        var hasDraft = schemaVersion == RecipeDraftStoreOptions.SchemaVersion ||
            ((schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
                recipeDraftOptions is not null);
        if (schemaVersion == RecipeDraftStoreOptions.SchemaVersion && recipeDraftOptions is null)
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (archiveOptions is not null && !hasArchive)
            throw new InvalidOperationException("AlgorithmResultArchiveGovernedMigrationRequired");
        if (recipeDraftOptions is null &&
            (schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
            TableExists(db, "recipe_draft_revisions", deadline))
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (archiveOptions is null &&
            (schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
            TableExists(db, "development_algorithm_results", deadline))
            throw new InvalidOperationException("AlgorithmResultArchiveConfigurationRequired");
        if (hasArchive)
        {
            Require(archiveOptions is not null, "AlgorithmResultArchiveConfigurationRequired");
            archiveOptions!.Validate();
            SqliteCommandStore.RequireConfiguredArchive(db, archiveOptions, deadline);
        }
        if (hasDraft)
        {
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
            recipeDraftOptions!.Validate();
            SqliteCommandStore.RequireConfiguredRecipeDrafts(db, recipeDraftOptions, deadline);
        }
        if (hasCamera)
        {
            Require(cameraSetupOptions is not null, "CameraSetupConfigurationRequired");
            cameraSetupOptions!.Validate();
            SqliteCommandStore.RequireConfiguredCameraSetup(db, cameraSetupOptions, deadline);
        }
        if (hasImaging)
        {
            Require(imagingSetupOptions is not null, "ImagingSetupConfigurationRequired");
            imagingSetupOptions!.Validate();
            SqliteCommandStore.RequireConfiguredImagingSetup(db, imagingSetupOptions, deadline);
        }
        if ((schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                ImagingSetupStoreOptions.SchemaVersion) &&
            cameraRecoveryOptions is null && TableExists(db, "camera_recovery_terminal_events", deadline))
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        if ((schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                ImagingSetupStoreOptions.SchemaVersion) &&
            cameraRecoveryOptions is not null && !TableExists(db, "camera_recovery_terminal_events", deadline))
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        if ((schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
            cameraNetworkOptions is null && TableExists(db, "camera_network_events", deadline))
            throw new InvalidOperationException("CameraNetworkConfigurationRequired");
        if ((schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
            cameraNetworkOptions is not null && !TableExists(db, "camera_network_events", deadline))
            throw new InvalidOperationException("CameraNetworkConfigurationRequired");
        if (hasRecovery)
        {
            Require(cameraRecoveryOptions is not null, "CameraRecoveryConfigurationRequired");
            cameraRecoveryOptions!.Validate();
            SqliteCommandStore.RequireConfiguredCameraRecovery(db, cameraRecoveryOptions, deadline);
        }
        if (hasNetwork)
        {
            cameraNetworkOptions!.Validate();
            SqliteCommandStore.RequireConfiguredCameraNetwork(db, cameraNetworkOptions, deadline);
        }
        var tail = Tail(db, deadline);
        Require(tail.Sequence > 0, "AuditChainMissing");
        var genesis = Read(db, "SELECT Kind,Payload,PreviousHash,Hash,FactPosition," +
            (hasIdentity ? "IdentityPosition" : "NULL") + "," +
            (hasAlarm ? "AlarmPosition" : "NULL") + "," +
            (hasArchive ? "ResultPosition" : "NULL") + "," +
            (hasDraft ? "DraftPosition" : "NULL") + "," +
            (hasCamera ? "CameraPosition" : "NULL") + "," +
            (hasNetwork ? "NetworkPosition" : "NULL") + "," +
            (hasImaging ? "ImagingPosition" : "NULL") +
            " FROM audit_entries WHERE Sequence=1;", deadline,
            s => Enumerable.Range(0, hasImaging ? 12 : 11).Select(i => SqliteNative.ColumnText(s, i)).ToArray()).SingleOrDefault();
        Require(genesis is not null && genesis[0] == "SigningKeyCreated" && genesis[2] == AuditCanonical.GenesisHash &&
            genesis[4] is null && genesis[5] is null && genesis[6] is null && genesis[7] is null && genesis[8] is null &&
            genesis[9] is null && genesis[10] is null && (!hasImaging || genesis[11] is null),
            "AuditGenesisMissingOrInvalid");
        var genesisHash = hasImaging
            ? EntryHash(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                Convert.FromBase64String(genesis[1]!))
            : hasNetwork
            ? EntryHash(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10],
                Convert.FromBase64String(genesis[1]!))
            : EntryHash(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9],
                Convert.FromBase64String(genesis[1]!));
        Require(genesis![1]?.Length <= 24000 && genesisHash == genesis[3], "AuditGenesisHashMismatch");
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
        var maxResult = hasArchive ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM development_algorithm_results;", deadline) : 0;
        var maxDraft = hasDraft ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM recipe_draft_revisions;", deadline) : 0;
        var maxCamera = hasCamera ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM camera_setup_events;", deadline) : 0;
        var maxRecovery = hasRecovery ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM camera_recovery_terminal_events;", deadline) : 0;
        var maxNetwork = hasNetwork ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM camera_network_events;", deadline) : 0;
        var maxImaging = hasImaging ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM imaging_setup_revisions;", deadline) : 0;
        var archiveActivations = hasArchive ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='AlgorithmArchiveActivated';", deadline) : 0;
        var draftActivations = hasDraft ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeDraftStoreActivated';", deadline) : 0;
        var cameraActivations = hasCamera ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraSetupStoreActivated';", deadline) : 0;
        var recoveryActivations = hasRecovery ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraRecoveryStoreActivated';", deadline) : 0;
        var networkActivations = hasNetwork ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraNetworkStoreActivated';", deadline) : 0;
        var imagingActivations = hasImaging ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImagingSetupStoreActivated';", deadline) : 0;
        Require(!hasArchive || archiveActivations == 1, "AlgorithmResultArchiveActivationMissing");
        Require(!hasDraft || draftActivations == 1, "RecipeDraftActivationMissing");
        Require(!hasCamera || cameraActivations == 1, "CameraSetupActivationMissing");
        Require(!hasRecovery || recoveryActivations == 1, "CameraRecoveryActivationMissing");
        Require(!hasNetwork || networkActivations == 1, "CameraNetworkActivationMissing");
        Require(!hasImaging || imagingActivations == 1, "ImagingSetupActivationMissing");
        Require(checked(maxFact + maxIdentity + maxAlarm + maxResult + maxDraft + maxCamera +
            maxRecovery + maxNetwork + maxImaging + archiveActivations + draftActivations + cameraActivations + recoveryActivations + networkActivations + imagingActivations) == tail.Sequence - 1 && maxFact == Scalar(db,
            "SELECT COALESCE(MAX(FactPosition),0) FROM audit_entries;", deadline) &&
            (!hasAlarm || maxAlarm == Scalar(db, "SELECT COALESCE(MAX(AlarmPosition),0) FROM audit_entries;", deadline)) &&
            (!hasArchive || maxResult == Scalar(db, "SELECT COALESCE(MAX(ResultPosition),0) FROM audit_entries;", deadline)) &&
            (!hasDraft || maxDraft == Scalar(db, "SELECT COALESCE(MAX(DraftPosition),0) FROM audit_entries;", deadline)) &&
            (!hasCamera || maxCamera == Scalar(db, "SELECT COALESCE(MAX(CameraPosition),0) FROM audit_entries;", deadline)) &&
            (!hasNetwork || maxNetwork == Scalar(db, "SELECT COALESCE(MAX(NetworkPosition),0) FROM audit_entries;", deadline)) &&
            (!hasImaging || maxImaging == Scalar(db, "SELECT COALESCE(MAX(ImagingPosition),0) FROM audit_entries;", deadline)) &&
            Scalar(db, "SELECT COALESCE(MIN(Position),1) FROM command_facts;", deadline) == 1, "AuditUnchainedFact");
        long? anchored = null;
        if (policy.RequireExternalAnchor && validateAnchorReceipt)
        {
            VerifyStoredReceipt(db, policy, checkpoint, deadline, "AuditRequiredAnchorPending");
            anchored = checkpoint.Sequence;
        }

        // A caller-selected cursor never supplies a trust root. Recheck from a signed
        // checkpoint at/before it (or genesis) within the policy's hard verification budget.
        var fullArchiveVerification = hasArchive && archiveOptions is not null;
        var fullDraftVerification = hasDraft && recipeDraftOptions is not null;
        var fullCameraVerification = hasCamera && cameraSetupOptions is not null;
        var fullRecoveryVerification = hasRecovery && cameraRecoveryOptions is not null;
        var fullNetworkVerification = hasNetwork && cameraNetworkOptions is not null;
        var fullImagingVerification = hasImaging && imagingSetupOptions is not null;
        var prefixCheckpoint = startup && !fullArchiveVerification && !fullDraftVerification && !fullCameraVerification &&
            !fullRecoveryVerification && !fullNetworkVerification && !fullImagingVerification ? checkpoint : ReadCheckpoint(db,
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
        var resultOrdinal = hasArchive ? Scalar(db, "SELECT ResultPosition FROM audit_entries WHERE ResultPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var draftOrdinal = hasDraft ? Scalar(db, "SELECT DraftPosition FROM audit_entries WHERE DraftPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var cameraOrdinal = hasCamera ? Scalar(db, "SELECT CameraPosition FROM audit_entries WHERE CameraPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var recoveryOrdinal = hasRecovery ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraRecoveryEvent' AND Sequence<=?;",
            deadline, Number(after)) : 0;
        var networkOrdinal = hasNetwork ? Scalar(db, "SELECT NetworkPosition FROM audit_entries WHERE NetworkPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var imagingOrdinal = hasImaging ? Scalar(db, "SELECT ImagingPosition FROM audit_entries WHERE ImagingPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var rows = Read(db, "SELECT Sequence,Kind,FactPosition,Payload,PreviousHash,Hash," + (hasIdentity ? "IdentityPosition" : "NULL") + "," + (hasAlarm ? "AlarmPosition" : "NULL") + "," + (hasArchive ? "ResultPosition" : "NULL") + "," + (hasDraft ? "DraftPosition" : "NULL") + "," + (hasCamera ? "CameraPosition" : "NULL") + "," + (hasNetwork ? "NetworkPosition" : "NULL") + "," + (hasImaging ? "ImagingPosition" : "NULL") + " FROM audit_entries WHERE Sequence>? ORDER BY Sequence LIMIT ?;",
            deadline, s => new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7), SqliteNative.ColumnText(s, 8),
                SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10), SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12)), Number(after), Number(count));
        var next = after + 1;
        foreach (var row in rows)
        {
            Require(row.Sequence == next++, "AuditChainGap");
            Require(row.PreviousHash == previousHash, "AuditChainLinkMismatch");
            Require(row.Kind == "CameraNetworkEvent" || row.NetworkPosition is null,
                "AuditNetworkPositionGap");
            Require(row.Kind == "ImagingSetupRevision" || row.ImagingPosition is null,
                "AuditImagingPositionGap");
            var encodedPayloadLimit = hasImaging ? ImagingSetupRevisionStorageCodec.MaximumEncodedPayloadChars :
                hasNetwork ? CameraNetworkStorageCodec.MaximumEncodedPayloadChars :
                hasCamera ? CameraSetupStorageCodec.MaximumEncodedPayloadChars :
                hasArchive ? AlgorithmResultArchiveOptions.MaximumBindingPayloadBytes * 2 :
                hasAlarm ? AlarmStorageCodec.MaximumEncodedPayloadChars : 24000;
            Require(row.Payload.Length <= encodedPayloadLimit, "AuditPayloadOversize");
            var payload = Convert.FromBase64String(row.Payload);
            var payloadLimit = hasImaging ? ImagingSetupRevisionStorageCodec.MaximumPayloadBytes :
                hasNetwork ? CameraNetworkStorageCodec.MaximumPayloadBytes :
                hasCamera ? CameraSetupStorageCodec.MaximumPayloadBytes :
                hasArchive ? AlgorithmResultArchiveOptions.MaximumBindingPayloadBytes :
                hasAlarm ? AlarmStorageCodec.MaximumPayloadBytes : 16384;
            Require(payload.Length <= payloadLimit, "AuditPayloadOversize");
            var rowHash = hasImaging
                ? EntryHash(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition, payload)
                : hasNetwork
                ? EntryHash(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, payload)
                : EntryHash(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, payload);
            Require(rowHash == row.Hash, "AuditPayloadHashMismatch");
            if (row.Kind == "CommandFact")
            {
                Require(long.TryParse(row.FactPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position), "AuditFactLinkMissing");
                Require(position == ++commandOrdinal && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "AuditCommandPositionGap");
                Require(CommandPayload(db, position, deadline).SequenceEqual(payload), "AuditFactMismatch");
            }
            else if (hasIdentity && row.Kind == "IdentityEvent")
            {
                Require(row.FactPosition is null && long.TryParse(row.IdentityPosition, NumberStyles.None, CultureInfo.InvariantCulture,
                    out _) && row.AlarmPosition is null && row.ResultPosition is null && row.DraftPosition is null &&
                    row.CameraPosition is null && row.NetworkPosition is null && row.ImagingPosition is null,
                    "AuditIdentityPositionGap");
                Require(row.IdentityPosition == Number(++identityOrdinal), "AuditIdentityPositionGap");
                IdentityAuditEvent.VerifyPayload(payload, identityOrdinal, policy.StationId, (int)schemaVersion);
            }
            else if (hasAlarm && row.Kind == "AlarmEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.ResultPosition is null &&
                    row.DraftPosition is null && row.CameraPosition is null && row.NetworkPosition is null &&
                    row.ImagingPosition is null,
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
            else if (hasArchive && row.Kind == "AlgorithmArchiveActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "AlgorithmResultArchiveActivationBindingMismatch");
                SqliteCommandStore.VerifyActivationPayload(db, payload, archiveOptions!, deadline);
            }
            else if (hasArchive && row.Kind == "AlgorithmComputation")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.DraftPosition is null && row.CameraPosition is null && row.NetworkPosition is null &&
                    row.ImagingPosition is null,
                    "AuditResultPositionGap");
                if (!long.TryParse(row.ResultPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("AuditResultPositionGap");
                Require(position == ++resultOrdinal, "AuditResultPositionGap");
                var resultBinding = SqliteCommandStore.ReadAndValidate(db, position, payload, deadline,
                    archiveOptions);
                Require(resultBinding.Length > 0, "AlgorithmResultBindingMismatch");
            }
            else if (hasDraft && row.Kind == "RecipeDraftStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "RecipeDraftActivationBindingMismatch");
                SqliteCommandStore.VerifyRecipeDraftActivationPayload(db, payload, recipeDraftOptions!, deadline);
            }
            else if (hasDraft && row.Kind == "RecipeDraftRevision")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.CameraPosition is null && row.NetworkPosition is null &&
                    row.ImagingPosition is null,
                    "RecipeDraftPositionGap");
                if (!long.TryParse(row.DraftPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("RecipeDraftPositionGap");
                Require(position == ++draftOrdinal, "RecipeDraftPositionGap");
                var draftBinding = SqliteCommandStore.ReadAndValidateRecipeDraft(db, position, payload, deadline,
                    recipeDraftOptions!);
                Require(draftBinding.Length > 0, "RecipeDraftBindingMismatch");
            }
            else if (hasCamera && row.Kind == "CameraSetupStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "CameraSetupActivationBindingMismatch");
                SqliteCommandStore.VerifyCameraSetupActivationPayload(db, payload, cameraSetupOptions!, deadline);
            }
            else if (hasCamera && row.Kind == "CameraSetupEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.NetworkPosition is null &&
                    row.ImagingPosition is null,
                    "CameraSetupPositionGap");
                if (!long.TryParse(row.CameraPosition, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("CameraSetupPositionGap");
                Require(position == ++cameraOrdinal, "CameraSetupPositionGap");
                var cameraBinding = SqliteCommandStore.ReadAndValidateCameraSetup(db, position, payload, deadline,
                    cameraSetupOptions!);
                Require(cameraBinding.Length > 0, "CameraSetupBindingMismatch");
            }
            else if (hasRecovery && row.Kind == "CameraRecoveryStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "CameraRecoveryActivationBindingMismatch");
                SqliteCommandStore.VerifyCameraRecoveryActivationPayload(db, payload,
                    cameraRecoveryOptions!, deadline);
            }
            else if (hasRecovery && row.Kind == "CameraRecoveryEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "CameraRecoveryPositionGap");
                var position = CameraRecoveryStorageCodec.ReadPosition(payload);
                Require(position == ++recoveryOrdinal, "CameraRecoveryPositionGap");
                var recoveryBinding = SqliteCommandStore.ReadAndValidateCameraRecovery(db, position,
                    payload, deadline, cameraRecoveryOptions!);
                Require(recoveryBinding.Length > 0, "CameraRecoveryBindingMismatch");
            }
            else if (hasNetwork && row.Kind == "CameraNetworkStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "CameraNetworkActivationBindingMismatch");
                SqliteCommandStore.VerifyCameraNetworkActivationPayload(db, payload,
                    cameraNetworkOptions!, deadline);
            }
            else if (hasNetwork && row.Kind == "CameraNetworkEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.ImagingPosition is null,
                    "CameraNetworkPositionGap");
                if (!long.TryParse(row.NetworkPosition, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var position))
                    throw new InvalidOperationException("CameraNetworkPositionGap");
                Require(position == ++networkOrdinal, "CameraNetworkPositionGap");
                var networkBinding = SqliteCommandStore.ReadAndValidateCameraNetwork(db, position,
                    payload, deadline, cameraNetworkOptions!);
                Require(networkBinding.Length > 0, "CameraNetworkBindingMismatch");
            }
            else if (hasImaging && row.Kind == "ImagingSetupStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "ImagingSetupActivationBindingMismatch");
                SqliteCommandStore.VerifyImagingSetupActivationPayload(db, payload,
                    imagingSetupOptions!, deadline);
            }
            else if (hasImaging && row.Kind == "ImagingSetupRevision")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null,
                    "AuditImagingPositionGap");
                if (!long.TryParse(row.ImagingPosition, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var position))
                    throw new InvalidOperationException("AuditImagingPositionGap");
                Require(position == ++imagingOrdinal, "AuditImagingPositionGap");
                var imagingBinding = SqliteCommandStore.ReadAndValidateImagingSetup(db, position,
                    payload, deadline, imagingSetupOptions!);
                Require(imagingBinding.Length > 0, "ImagingSetupBindingMismatch");
            }
            else Require(row.Sequence == 1 && row.Kind == "SigningKeyCreated" && row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null && row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null && row.NetworkPosition is null && row.ImagingPosition is null,
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

    internal static void RequireFullAlgorithmResultVerification(sqlite3 db, AuditIntegrityReport report,
        StoreDeadline deadline)
    {
        Require(report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "AlgorithmResultVerificationBudgetExceeded");
    }

    internal static void RequireFullRecipeDraftVerification(sqlite3 db, AuditIntegrityReport report,
        StoreDeadline deadline, RecipeDraftStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "RecipeDraftVerificationBudgetExceeded");
        if (options is not null)
            SqliteCommandStore.ValidateRecipeDraftHistory(db, options, deadline);
    }

    internal static void RequireFullCameraSetupVerification(sqlite3 db, AuditIntegrityReport report,
        StoreDeadline deadline, CameraSetupStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "CameraSetupVerificationBudgetExceeded");
        if (options is not null)
            SqliteCommandStore.ValidateCameraSetupHistory(db, options, deadline);
    }

    internal static void RequireFullCameraRecoveryVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        CameraRecoveryStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "CameraRecoveryVerificationBudgetExceeded");
        if (options is not null)
            SqliteCommandStore.ValidateCameraRecoveryHistory(db, options, deadline);
    }

    internal static void RequireFullCameraNetworkVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        CameraNetworkStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "CameraNetworkVerificationBudgetExceeded");
        if (options is not null)
            SqliteCommandStore.ValidateCameraNetworkHistory(db, options, deadline);
    }

    internal static void RequireFullImagingSetupVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        ImagingSetupStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "ImagingSetupVerificationBudgetExceeded");
        if (options is not null)
            SqliteCommandStore.ValidateImagingSetupHistory(db, options, deadline);
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
    internal static bool TableExists(sqlite3 db, string table, StoreDeadline deadline) =>
        Text(db, "SELECT name FROM sqlite_master WHERE type='table' AND name=? LIMIT 1;", deadline, table) is not null;
    private static void Bind(sqlite3 db, sqlite3_stmt s, string?[] args)
    {
        for (var i = 0; i < args.Length; i++) SqliteNative.BindText(db, s, i + 1, args[i]);
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, string? resultPosition, byte[] payload) =>
        EntryHash(schemaVersion, stationId, sequence, previousHash, kind, factPosition, identityPosition,
            alarmPosition, resultPosition, null, null, payload);

    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, string? resultPosition,
        string? draftPosition, byte[] payload) =>
        EntryHash(schemaVersion, stationId, sequence, previousHash, kind, factPosition, identityPosition,
            alarmPosition, resultPosition, draftPosition, null, payload);

    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, string? resultPosition,
        string? draftPosition, string? cameraPosition, byte[] payload) =>
        EntryHash(schemaVersion, stationId, sequence, previousHash, kind, factPosition, identityPosition,
            alarmPosition, resultPosition, draftPosition, cameraPosition, null, payload);

    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, string? resultPosition,
        string? draftPosition, string? cameraPosition, string? networkPosition, byte[] payload) =>
        EntryHash(schemaVersion, stationId, sequence, previousHash, kind, factPosition, identityPosition,
            alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition, null, payload);

    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, string? resultPosition,
        string? draftPosition, string? cameraPosition, string? networkPosition, string? imagingPosition,
        byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash, schemaVersion >= CameraSetupStoreOptions.SchemaVersion
            ? schemaVersion >= ImagingSetupStoreOptions.SchemaVersion
                ? AuditCanonical.Encode("AuditEntryEnvelopeV9", kind, factPosition, identityPosition, alarmPosition,
                    resultPosition, draftPosition, cameraPosition, networkPosition, imagingPosition,
                    Convert.ToBase64String(payload))
            : schemaVersion >= CameraNetworkStoreOptions.SchemaVersion
                ? AuditCanonical.Encode("AuditEntryEnvelopeV8", kind, factPosition, identityPosition, alarmPosition,
                    resultPosition, draftPosition, cameraPosition, networkPosition, Convert.ToBase64String(payload))
                : AuditCanonical.Encode("AuditEntryEnvelopeV7", kind, factPosition, identityPosition, alarmPosition,
                    resultPosition, draftPosition, cameraPosition, Convert.ToBase64String(payload))
            : schemaVersion >= RecipeDraftStoreOptions.SchemaVersion
                ? AuditCanonical.Encode("AuditEntryEnvelopeV6", kind, factPosition, identityPosition, alarmPosition,
                    resultPosition, draftPosition, Convert.ToBase64String(payload))
            : schemaVersion >= 7
                ? schemaVersion >= 8
                    ? AuditCanonical.Encode("AuditEntryEnvelopeV5", kind, factPosition, identityPosition, alarmPosition, resultPosition, Convert.ToBase64String(payload))
                    : AuditCanonical.Encode("AuditEntryEnvelopeV4", kind, factPosition, identityPosition, alarmPosition, Convert.ToBase64String(payload))
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
        string Hash, string? IdentityPosition, string? AlarmPosition, string? ResultPosition, string? DraftPosition,
        string? CameraPosition, string? NetworkPosition, string? ImagingPosition);
}
