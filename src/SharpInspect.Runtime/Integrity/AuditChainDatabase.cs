using System.Globalization;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
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
        if (version == CalibrationSessionStoreOptions.SchemaVersion)
        {
            // Schema 14 extends the audit envelope with three independent,
            // immutable positions.  Each session header, event and frame
            // manifest therefore has one signed chain entry of its own.
            var sql = SchemaSqlFor(ImagingSetupStoreOptions.SchemaVersion)
                .Replace("ImagingPosition INTEGER UNIQUE,", "ImagingPosition INTEGER UNIQUE, CalibrationSessionPosition INTEGER UNIQUE, CalibrationEventPosition INTEGER UNIQUE, CalibrationManifestPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("ImagingPosition IS NULL)", "ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL)", StringComparison.Ordinal)
                .Replace("ImagingPosition IS NOT NULL AND ImagingPosition>0 AND FactPosition", "ImagingPosition IS NOT NULL AND ImagingPosition>0 AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND FactPosition", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=13;", "PRAGMA user_version=14;", StringComparison.Ordinal);
            const string imagingRevision = " OR (Kind='ImagingSetupRevision'";
            var revisionStart = sql.LastIndexOf(imagingRevision, StringComparison.Ordinal);
            var checkClose = revisionStart < 0 ? -1 : sql.IndexOf(")));", revisionStart,
                StringComparison.Ordinal);
            if (revisionStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='CalibrationStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL)
                 OR (Kind='CalibrationSessionHeader' AND Sequence>1 AND CalibrationSessionPosition IS NOT NULL AND CalibrationSessionPosition>0 AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL)
                 OR (Kind='CalibrationSessionEvent' AND Sequence>1 AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NOT NULL AND CalibrationEventPosition>0 AND CalibrationManifestPosition IS NULL AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL)
                 OR (Kind='CalibrationFrameManifest' AND Sequence>1 AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NOT NULL AND CalibrationManifestPosition>0 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL)");
            sql = sql.Replace("PRAGMA user_version=14;", @"
             CREATE INDEX ix_audit_calibration_session_sequence ON audit_entries(Sequence) WHERE CalibrationSessionPosition IS NOT NULL;
             CREATE INDEX ix_audit_calibration_event_sequence ON audit_entries(Sequence) WHERE CalibrationEventPosition IS NOT NULL;
             CREATE INDEX ix_audit_calibration_manifest_sequence ON audit_entries(Sequence) WHERE CalibrationManifestPosition IS NOT NULL;
             PRAGMA user_version=14;", StringComparison.Ordinal);
            return sql;
        }
        if (version == CalibrationGovernanceStoreOptions.SchemaVersion)
        {
            // Schema 15 preserves every schema-14 calibration column and
            // payload envelope, then adds one independent governance position
            // binding.  The schema-14 SQL branch above remains untouched so
            // its canonical audit bytes and checks cannot drift.
            var sql = SchemaSqlFor(CalibrationSessionStoreOptions.SchemaVersion)
                .Replace("CalibrationManifestPosition INTEGER UNIQUE,", "CalibrationManifestPosition INTEGER UNIQUE, GovernancePosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("ImagingPosition IS NULL)", "ImagingPosition IS NULL AND GovernancePosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=14;", @"
             CREATE INDEX ix_audit_governance_sequence ON audit_entries(Sequence) WHERE GovernancePosition IS NOT NULL;
             PRAGMA user_version=15;", StringComparison.Ordinal);
            const string calibrationManifest = " OR (Kind='CalibrationFrameManifest'";
            var manifestStart = sql.LastIndexOf(calibrationManifest, StringComparison.Ordinal);
            var checkClose = manifestStart < 0 ? -1 : sql.IndexOf(")));", manifestStart,
                StringComparison.Ordinal);
            if (manifestStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='CalibrationGovernanceStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL)
                 OR (Kind='CalibrationGovernanceEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NOT NULL AND GovernancePosition>0)");
            return sql;
        }
        if (version == RecipeReleaseStoreOptions.SchemaVersion)
        {
            // Schema 16 preserves the schema-15 envelope and every existing
            // typed position.  Released recipes receive an independent signed
            // position so the older hashes and ledgers remain byte stable.
            var sql = SchemaSqlFor(CalibrationGovernanceStoreOptions.SchemaVersion)
                .Replace("GovernancePosition INTEGER UNIQUE,", "GovernancePosition INTEGER UNIQUE, ReleasePosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("GovernancePosition IS NULL)", "GovernancePosition IS NULL AND ReleasePosition IS NULL)", StringComparison.Ordinal)
                .Replace("GovernancePosition IS NOT NULL AND GovernancePosition>0)", "GovernancePosition IS NOT NULL AND GovernancePosition>0 AND ReleasePosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=15;", @"
             CREATE INDEX ix_audit_release_sequence ON audit_entries(Sequence) WHERE ReleasePosition IS NOT NULL;
             PRAGMA user_version=16;", StringComparison.Ordinal);
            const string governanceEvent = " OR (Kind='CalibrationGovernanceEvent'";
            var governanceStart = sql.LastIndexOf(governanceEvent, StringComparison.Ordinal);
            var checkClose = governanceStart < 0 ? -1 : sql.IndexOf(")));", governanceStart,
                StringComparison.Ordinal);
            if (governanceStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='RecipeReleaseStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL)
                 OR (Kind='RecipeReleaseEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NOT NULL AND ReleasePosition>0)");
            return sql;
        }
        if (version == PlcResultContractStoreOptions.SchemaVersion)
        {
            // Schema 17 preserves the schema-16 envelope and every existing
            // position column.  PLC result contracts have an independent
            // append-only position so old Recipe Release hashes remain byte
            // stable while the new ledger is included in the central chain.
            var sql = SchemaSqlFor(RecipeReleaseStoreOptions.SchemaVersion)
                .Replace("ReleasePosition INTEGER UNIQUE,", "ReleasePosition INTEGER UNIQUE, PlcResultContractPosition INTEGER UNIQUE,",
                    StringComparison.Ordinal)
                .Replace("ReleasePosition IS NULL)", "ReleasePosition IS NULL AND PlcResultContractPosition IS NULL)",
                    StringComparison.Ordinal)
                .Replace("ReleasePosition IS NOT NULL AND ReleasePosition>0)",
                    "ReleasePosition IS NOT NULL AND ReleasePosition>0 AND PlcResultContractPosition IS NULL)",
                    StringComparison.Ordinal)
                .Replace("PRAGMA user_version=16;", @"
             CREATE INDEX ix_audit_plc_result_contract_sequence ON audit_entries(Sequence) WHERE PlcResultContractPosition IS NOT NULL;
             PRAGMA user_version=17;", StringComparison.Ordinal);
            const string releaseEvent = " OR (Kind='RecipeReleaseEvent'";
            var releaseStart = sql.LastIndexOf(releaseEvent, StringComparison.Ordinal);
            var checkClose = releaseStart < 0 ? -1 : sql.IndexOf(")));", releaseStart,
                StringComparison.Ordinal);
            if (releaseStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='PlcResultContractStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL)
                 OR (Kind='PlcResultContractEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NOT NULL AND PlcResultContractPosition>0)");
            return sql;
        }
        if (version == RecipeActivationStoreOptions.SchemaVersion)
        {
            // Schema 18 preserves the schema-17 envelope and every previous
            // position column.  Activations get one independent position and a
            // new hash envelope; the older schema branches are left untouched.
            var sql = SchemaSqlFor(PlcResultContractStoreOptions.SchemaVersion)
                .Replace("PlcResultContractPosition INTEGER UNIQUE,", "PlcResultContractPosition INTEGER UNIQUE, ActivationPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("PlcResultContractPosition IS NULL)", "PlcResultContractPosition IS NULL AND ActivationPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PlcResultContractPosition IS NOT NULL AND PlcResultContractPosition>0)",
                    "PlcResultContractPosition IS NOT NULL AND PlcResultContractPosition>0 AND ActivationPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=17;", @"
             CREATE INDEX ix_audit_activation_sequence ON audit_entries(Sequence) WHERE ActivationPosition IS NOT NULL;
             PRAGMA user_version=18;", StringComparison.Ordinal);
            const string contractEvent = " OR (Kind='PlcResultContractEvent'";
            var contractStart = sql.LastIndexOf(contractEvent, StringComparison.Ordinal);
            var checkClose = contractStart < 0 ? -1 : sql.IndexOf(")));", contractStart, StringComparison.Ordinal);
            if (contractStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='RecipeActivationStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL)
                 OR (Kind='RecipeActivationEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NOT NULL AND ActivationPosition>0)");
            return sql;
        }
        if (version == PreviewSessionStoreOptions.SchemaVersion)
        {
            // Schema 19 preserves the schema-18 envelope and every existing
            // position/hash contract. Preview receives one independent signed
            // position; its event payload is the only new chain member.
            var sql = SchemaSqlFor(RecipeActivationStoreOptions.SchemaVersion)
                .Replace("ActivationPosition INTEGER UNIQUE,", "ActivationPosition INTEGER UNIQUE, PreviewPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("ActivationPosition IS NULL)", "ActivationPosition IS NULL AND PreviewPosition IS NULL)", StringComparison.Ordinal)
                .Replace("ActivationPosition IS NOT NULL AND ActivationPosition>0)",
                    "ActivationPosition IS NOT NULL AND ActivationPosition>0 AND PreviewPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=18;", @"
             CREATE INDEX ix_audit_preview_sequence ON audit_entries(Sequence) WHERE PreviewPosition IS NOT NULL;
             PRAGMA user_version=19;", StringComparison.Ordinal);
            const string activationEvent = " OR (Kind='RecipeActivationEvent'";
            var activationStart = sql.LastIndexOf(activationEvent, StringComparison.Ordinal);
            var checkClose = activationStart < 0 ? -1 : sql.IndexOf(")));", activationStart,
                StringComparison.Ordinal);
            if (activationStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='PreviewSessionStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL)
                 OR (Kind='PreviewSessionEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NOT NULL AND PreviewPosition>0)");
            return sql;
        }
        if (version == CalibrationImportStoreOptions.SchemaVersion)
        {
            // Schema 20 preserves the complete schema-19 envelope and adds an
            // independent central-audit position for the import provenance
            // ledger.  No schema-19 SQL or hash envelope is changed.
            var sql = SchemaSqlFor(PreviewSessionStoreOptions.SchemaVersion)
                .Replace("PreviewPosition INTEGER UNIQUE,", "PreviewPosition INTEGER UNIQUE, CalibrationImportPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("PreviewPosition IS NULL)", "PreviewPosition IS NULL AND CalibrationImportPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PreviewPosition IS NOT NULL AND PreviewPosition>0)",
                    "PreviewPosition IS NOT NULL AND PreviewPosition>0 AND CalibrationImportPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=19;", @"
             CREATE INDEX ix_audit_calibration_import_sequence ON audit_entries(Sequence) WHERE CalibrationImportPosition IS NOT NULL;
             PRAGMA user_version=20;", StringComparison.Ordinal);
            const string previewEvent = " OR (Kind='PreviewSessionEvent'";
            var previewStart = sql.LastIndexOf(previewEvent, StringComparison.Ordinal);
            // The schema-19 CHECK ends in ")));"; keep this lookup explicit so
            // a formatting-only change cannot silently omit schema-20 kinds.
            var checkClose = previewStart < 0 ? -1 : sql.IndexOf(")));", previewStart, StringComparison.Ordinal);
            if (previewStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='CalibrationImportStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL)
                 OR (Kind='CalibrationImportEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NOT NULL AND CalibrationImportPosition>0)");
            return sql;
        }
        if (version == ManualInspectionStoreOptions.SchemaVersion)
        {
            // Schema 21 keeps every schema-20 byte and adds one independent
            // position for the non-production Manual Inspection ledger.
            var sql = SchemaSqlFor(CalibrationImportStoreOptions.SchemaVersion)
                .Replace("CalibrationImportPosition INTEGER UNIQUE,", "CalibrationImportPosition INTEGER UNIQUE, ManualInspectionPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("CalibrationImportPosition IS NULL)", "CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL)", StringComparison.Ordinal)
                .Replace("CalibrationImportPosition IS NOT NULL AND CalibrationImportPosition>0)",
                    "CalibrationImportPosition IS NOT NULL AND CalibrationImportPosition>0 AND ManualInspectionPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=20;", @"
             CREATE INDEX ix_audit_manual_inspection_sequence ON audit_entries(Sequence) WHERE ManualInspectionPosition IS NOT NULL;
             PRAGMA user_version=21;", StringComparison.Ordinal);
            const string importEvent = " OR (Kind='CalibrationImportEvent'";
            var importStart = sql.LastIndexOf(importEvent, StringComparison.Ordinal);
            var checkClose = importStart < 0 ? -1 : sql.IndexOf(")));", importStart, StringComparison.Ordinal);
            if (importStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='ManualInspectionStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL)
                 OR (Kind='ManualInspectionEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NOT NULL AND ManualInspectionPosition>0)");
            return sql;
        }
        if (version == ProductionAdmissionStoreOptions.SchemaVersion)
        {
            // Schema 22 preserves the complete schema-21 envelope and adds one
            // independent central-audit position for production admission
            // reports.  The report ledger remains opt-in; this branch only
            // describes the immutable central shape when it is enabled.
            var sql = SchemaSqlFor(ManualInspectionStoreOptions.SchemaVersion)
                .Replace("ManualInspectionPosition INTEGER UNIQUE,", "ManualInspectionPosition INTEGER UNIQUE, ProductionAdmissionPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("ManualInspectionPosition IS NULL)", "ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL)", StringComparison.Ordinal)
                .Replace("ManualInspectionPosition IS NOT NULL AND ManualInspectionPosition>0)",
                    "ManualInspectionPosition IS NOT NULL AND ManualInspectionPosition>0 AND ProductionAdmissionPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=21;", @"
             CREATE INDEX ix_audit_production_admission_sequence ON audit_entries(Sequence) WHERE ProductionAdmissionPosition IS NOT NULL;
             PRAGMA user_version=22;", StringComparison.Ordinal);
            const string manualEvent = " OR (Kind='ManualInspectionEvent'";
            var manualStart = sql.LastIndexOf(manualEvent, StringComparison.Ordinal);
            var checkClose = manualStart < 0 ? -1 : sql.IndexOf(")));", manualStart, StringComparison.Ordinal);
            if (manualStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='ProductionAdmissionStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL)
                 OR (Kind='ProductionAdmissionEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NOT NULL AND ProductionAdmissionPosition>0)");
            return sql;
        }
        if (version == StationQualificationStoreOptions.SchemaVersion)
        {
            // Schema 23 preserves the complete schema-22 envelope and adds one
            // independent position for the non-production Station Qualification
            // ledger.  Earlier envelopes remain byte-for-byte unchanged.
            var sql = SchemaSqlFor(ProductionAdmissionStoreOptions.SchemaVersion)
                .Replace("ProductionAdmissionPosition INTEGER UNIQUE,", "ProductionAdmissionPosition INTEGER UNIQUE, StationQualificationPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("ProductionAdmissionPosition IS NULL)", "ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL)", StringComparison.Ordinal)
                .Replace("ProductionAdmissionPosition IS NOT NULL AND ProductionAdmissionPosition>0)",
                    "ProductionAdmissionPosition IS NOT NULL AND ProductionAdmissionPosition>0 AND StationQualificationPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=22;", @"
             CREATE INDEX ix_audit_station_qualification_sequence ON audit_entries(Sequence) WHERE StationQualificationPosition IS NOT NULL;
             PRAGMA user_version=23;", StringComparison.Ordinal);
            const string productionEvent = " OR (Kind='ProductionAdmissionEvent'";
            var productionStart = sql.LastIndexOf(productionEvent, StringComparison.Ordinal);
            var checkClose = productionStart < 0 ? -1 : sql.IndexOf(")));", productionStart, StringComparison.Ordinal);
            if (productionStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='StationQualificationStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL)
                 OR (Kind='StationQualificationEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NOT NULL AND StationQualificationPosition>0)");
            return sql;
        }
        if (version == RecipeTransferStoreOptions.SchemaVersion)
        {
            // Schema 24 preserves the complete schema-23 envelope and adds one
            // independent position for governed recipe-transfer history.  The
            // V20 hash envelope is selected only for schema 24 rows; all older
            // schema branches remain byte-for-byte unchanged.
            var sql = SchemaSqlFor(StationQualificationStoreOptions.SchemaVersion)
                .Replace("StationQualificationPosition INTEGER UNIQUE,", "StationQualificationPosition INTEGER UNIQUE, RecipeTransferPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("StationQualificationPosition IS NULL)", "StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL)", StringComparison.Ordinal)
                .Replace("StationQualificationPosition IS NOT NULL AND StationQualificationPosition>0)",
                    "StationQualificationPosition IS NOT NULL AND StationQualificationPosition>0 AND RecipeTransferPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=23;", @"
             CREATE INDEX ix_audit_recipe_transfer_sequence ON audit_entries(Sequence) WHERE RecipeTransferPosition IS NOT NULL;
             PRAGMA user_version=24;", StringComparison.Ordinal);
            const string qualificationEvent = " OR (Kind='StationQualificationEvent'";
            var qualificationStart = sql.LastIndexOf(qualificationEvent, StringComparison.Ordinal);
            var checkClose = qualificationStart < 0 ? -1 : sql.IndexOf(")));", qualificationStart,
                StringComparison.Ordinal);
            if (qualificationStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='RecipeTransferStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL)
                 OR (Kind='RecipeTransferEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NOT NULL AND RecipeTransferPosition>0)");
            return sql;
        }
        if (version == TraceStoragePolicyStoreOptions.SchemaVersion)
        {
            // Schema 25 preserves the complete schema-24 envelope and adds one
            // independent position for immutable trace-storage policy
            // publications.  V21 is selected only by schema 25; all prior
            // envelopes and hashes remain unchanged.
            var sql = SchemaSqlFor(RecipeTransferStoreOptions.SchemaVersion)
                .Replace("RecipeTransferPosition INTEGER UNIQUE,", "RecipeTransferPosition INTEGER UNIQUE, TraceStoragePolicyPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("RecipeTransferPosition IS NULL)", "RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL)", StringComparison.Ordinal)
                .Replace("RecipeTransferPosition IS NOT NULL AND RecipeTransferPosition>0)",
                    "RecipeTransferPosition IS NOT NULL AND RecipeTransferPosition>0 AND TraceStoragePolicyPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=24;", @"
             CREATE INDEX ix_audit_trace_storage_policy_sequence ON audit_entries(Sequence) WHERE TraceStoragePolicyPosition IS NOT NULL;
             PRAGMA user_version=25;", StringComparison.Ordinal);
            const string transferEvent = " OR (Kind='RecipeTransferEvent'";
            var transferStart = sql.LastIndexOf(transferEvent, StringComparison.Ordinal);
            var checkClose = transferStart < 0 ? -1 : sql.IndexOf(")));", transferStart,
                StringComparison.Ordinal);
            if (transferStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='TraceStoragePolicyStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL)
                 OR (Kind='TraceStoragePolicyEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NOT NULL AND TraceStoragePolicyPosition>0)");
            return sql;
        }
        if (version == QualificationCycleStoreOptions.SchemaVersion)
        {
            // Schema 26 keeps every schema-25 envelope byte-for-byte and adds
            // one independent cursor for the shared qualification-cycle
            // lifecycle ledger.  V22 is selected only for schema-26 rows.
            var sql = SchemaSqlFor(TraceStoragePolicyStoreOptions.SchemaVersion)
                .Replace("TraceStoragePolicyPosition INTEGER UNIQUE,", "TraceStoragePolicyPosition INTEGER UNIQUE, QualificationCyclePosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("TraceStoragePolicyPosition IS NULL)", "TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL)", StringComparison.Ordinal)
                .Replace("TraceStoragePolicyPosition IS NOT NULL AND TraceStoragePolicyPosition>0)",
                    "TraceStoragePolicyPosition IS NOT NULL AND TraceStoragePolicyPosition>0 AND QualificationCyclePosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=25;", @"
             CREATE INDEX ix_audit_qualification_cycle_sequence ON audit_entries(Sequence) WHERE QualificationCyclePosition IS NOT NULL;
             PRAGMA user_version=26;", StringComparison.Ordinal);
            const string policyEvent = " OR (Kind='TraceStoragePolicyEvent'";
            var policyStart = sql.LastIndexOf(policyEvent, StringComparison.Ordinal);
            var checkClose = policyStart < 0 ? -1 : sql.IndexOf(")));", policyStart,
                StringComparison.Ordinal);
            if (policyStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='QualificationCycleStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL)
                 OR (Kind='QualificationCycleEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NOT NULL AND QualificationCyclePosition>0)");
            return sql;
        }
        if (version == PlcCommunicationStoreOptions.SchemaVersion)
        {
            // Schema 27 preserves the schema-26 envelope and adds one cursor
            // for the append-only PLC communication facts ledger. Older
            // schemas and hashes remain byte-for-byte unchanged.
            var sql = SchemaSqlFor(QualificationCycleStoreOptions.SchemaVersion)
                .Replace("QualificationCyclePosition INTEGER UNIQUE,", "QualificationCyclePosition INTEGER UNIQUE, PlcCommunicationPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("QualificationCyclePosition IS NULL)", "QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL)", StringComparison.Ordinal)
                .Replace("QualificationCyclePosition IS NOT NULL AND QualificationCyclePosition>0)",
                    "QualificationCyclePosition IS NOT NULL AND QualificationCyclePosition>0 AND PlcCommunicationPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=26;", @"
             CREATE INDEX ix_audit_plc_communication_sequence ON audit_entries(Sequence) WHERE PlcCommunicationPosition IS NOT NULL;
             PRAGMA user_version=27;", StringComparison.Ordinal);
            const string cycleEvent = " OR (Kind='QualificationCycleEvent'";
            var cycleStart = sql.LastIndexOf(cycleEvent, StringComparison.Ordinal);
            var checkClose = cycleStart < 0 ? -1 : sql.IndexOf(")));", cycleStart, StringComparison.Ordinal);
            if (cycleStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='PlcCommunicationStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL)
                 OR (Kind='PlcCommunicationEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NOT NULL AND PlcCommunicationPosition>0)");
            return sql;
        }
        if (version == ProductionInspectionStoreOptions.SchemaVersion)
        {
            // Schema 28 preserves the schema-27 audit envelope and adds one
            // independent cursor for the production inspection Core ledger.
            // V24 is selected only for schema-28 rows; all older envelopes
            // remain byte-for-byte unchanged.
            var sql = SchemaSqlFor(PlcCommunicationStoreOptions.SchemaVersion)
                .Replace("PlcCommunicationPosition INTEGER UNIQUE,", "PlcCommunicationPosition INTEGER UNIQUE, ProductionInspectionPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("PlcCommunicationPosition IS NULL)", "PlcCommunicationPosition IS NULL AND ProductionInspectionPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PlcCommunicationPosition IS NOT NULL AND PlcCommunicationPosition>0)",
                    "PlcCommunicationPosition IS NOT NULL AND PlcCommunicationPosition>0 AND ProductionInspectionPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=27;", @"
             CREATE INDEX ix_audit_production_inspection_sequence ON audit_entries(Sequence) WHERE ProductionInspectionPosition IS NOT NULL;
             PRAGMA user_version=28;", StringComparison.Ordinal);
            const string communicationEvent = " OR (Kind='PlcCommunicationEvent'";
            var communicationStart = sql.LastIndexOf(communicationEvent, StringComparison.Ordinal);
            var checkClose = communicationStart < 0 ? -1 : sql.IndexOf(")));", communicationStart,
                StringComparison.Ordinal);
            if (communicationStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            sql = sql.Insert(checkClose + 1, @" OR (Kind='ProductionInspectionStoreActivated' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL AND ProductionInspectionPosition IS NULL)
                 OR (Kind='ProductionInspectionEvent' AND Sequence>1 AND FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL AND ProductionInspectionPosition IS NOT NULL AND ProductionInspectionPosition>0)");
            return sql;
        }
        if (version == PartIdentityStoreOptions.SchemaVersion)
        {
            // Schema 29 preserves every schema-28 hash/envelope field and adds
            // one independent cursor for rejected-trigger/correction facts.
            var sql = SchemaSqlFor(ProductionInspectionStoreOptions.SchemaVersion)
                .Replace("ProductionInspectionPosition INTEGER UNIQUE,", "ProductionInspectionPosition INTEGER UNIQUE, PartIdentityPosition INTEGER UNIQUE,", StringComparison.Ordinal)
                .Replace("ProductionInspectionPosition IS NULL)", "ProductionInspectionPosition IS NULL AND PartIdentityPosition IS NULL)", StringComparison.Ordinal)
                .Replace("ProductionInspectionPosition IS NOT NULL AND ProductionInspectionPosition>0)",
                    "ProductionInspectionPosition IS NOT NULL AND ProductionInspectionPosition>0 AND PartIdentityPosition IS NULL)", StringComparison.Ordinal)
                .Replace("PRAGMA user_version=28;", @"
             CREATE INDEX ix_audit_part_identity_sequence ON audit_entries(Sequence) WHERE PartIdentityPosition IS NOT NULL;
             PRAGMA user_version=29;", StringComparison.Ordinal);
            const string inspectionEvent = " OR (Kind='ProductionInspectionEvent'";
            var inspectionStart = sql.LastIndexOf(inspectionEvent, StringComparison.Ordinal);
            var checkClose = inspectionStart < 0 ? -1 : sql.IndexOf(")));", inspectionStart,
                StringComparison.Ordinal);
            if (inspectionStart < 0 || checkClose < 0)
                throw new InvalidOperationException("AuditSchemaDefinitionInvalid");
            const string nullPositions = "FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL AND ProductionInspectionPosition IS NULL";
            sql = sql.Insert(checkClose + 1, $" OR (Kind='PartIdentityStoreActivated' AND Sequence>1 AND {nullPositions} AND PartIdentityPosition IS NULL)\n                 OR (Kind='PartIdentityEvent' AND Sequence>1 AND {nullPositions} AND PartIdentityPosition IS NOT NULL AND PartIdentityPosition>0)");
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
        "ImagingSetupPayloadCapacityExceeded" or "CalibrationSessionCapacityExceeded" or
        "CalibrationEventCapacityExceeded" or "CalibrationEventPayloadCapacityExceeded" or
        "CalibrationFrameCapacityExceeded" or "CalibrationFrameTotalCapacityExceeded" or
        "CalibrationGovernanceEntryCapacityExceeded" or "CalibrationGovernancePayloadCapacityExceeded" or
        "CalibrationGovernanceTotalCapacityExceeded" or "CalibrationGovernanceAuditCapacityExceeded" or
        "RecipeReleaseEntryCapacityExceeded" or "RecipeReleasePayloadCapacityExceeded" or
        "RecipeReleaseTotalCapacityExceeded" or "RecipeReleaseAuditCapacityExceeded" or
        "PlcResultContractRevisionCapacityExceeded" or "PlcResultContractPayloadCapacityExceeded" or
        "PlcResultContractTotalCapacityExceeded" or "PlcResultContractAuditCapacityExceeded" or
        "RecipeActivationEntryCapacityExceeded" or "RecipeActivationPayloadCapacityExceeded" or
        "RecipeActivationTotalCapacityExceeded" or "RecipeActivationAuditCapacityExceeded" or
        "PreviewSessionEntryCapacityExceeded" or "PreviewSessionPayloadCapacityExceeded" or
        "PreviewSessionTotalCapacityExceeded" or "PreviewSessionAuditCapacityExceeded" or
        "CalibrationImportEntryCapacityExceeded" or "CalibrationImportPayloadCapacityExceeded" or
        "CalibrationImportTotalCapacityExceeded" or "CalibrationImportAuditCapacityExceeded" or
        "CalibrationImportCapacityExceeded" or "ManualInspectionAuditCapacityExceeded" or
        "ManualInspectionEntryCapacityExceeded" or "ManualInspectionPayloadCapacityExceeded" or
        "ManualInspectionTotalCapacityExceeded" or "ProductionAdmissionAuditCapacityExceeded" or
        "ProductionAdmissionEntryCapacityExceeded" or "ProductionAdmissionPayloadCapacityExceeded" or
        "ProductionAdmissionTotalCapacityExceeded" or "ProductionAdmissionCapacityExceeded" or
        "TraceStoragePolicyAuditCapacityExceeded" or "TraceStoragePolicyEntryCapacityExceeded" or
        "QualificationCycleAuditCapacityExceeded" or "QualificationCycleEntryCapacityExceeded" or
        "QualificationCyclePayloadCapacityExceeded" or "QualificationCycleTotalCapacityExceeded" or
        "TraceStoragePolicyPayloadCapacityExceeded" or "TraceStoragePolicyTotalCapacityExceeded" or
        "TraceStoragePolicyCapacityExceeded" or "PlcCommunicationAuditCapacityExceeded" or
        "PlcCommunicationEntryCapacityExceeded" or "PlcCommunicationPayloadCapacityExceeded" or
        "PlcCommunicationTotalCapacityExceeded" or "PlcCommunicationCapacityExceeded" or
        "ProductionInspectionAuditCapacityExceeded" or "ProductionInspectionEntryCapacityExceeded" or
        "ProductionInspectionPayloadCapacityExceeded" or "ProductionInspectionTotalCapacityExceeded" or
        "ProductionInspectionCapacityExceeded" or "PartIdentityAuditCapacityExceeded" or
        "PartIdentityEntryCapacityExceeded" or "PartIdentityPayloadCapacityExceeded" or
        "PartIdentityTotalCapacityExceeded" or "PartIdentityCapacityExceeded";

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
        if (schemaVersion is not (CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or
            RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) ||
            !TableExists(db, "camera_network_events", deadline)) return 0;
        var pending = PendingCameraNetworkOperations(db, deadline);
        return mode switch
        {
            CameraNetworkAuditWriteMode.Admission => checked(pending + 1),
            CameraNetworkAuditWriteMode.Terminal => Math.Max(0, pending - 1),
            _ => pending
        };
    }

    internal static bool CanReserveProductionInspectionAudit(sqlite3 database, AuditIntegrityPolicy policy,
        StoreDeadline deadline, long reserveAfterNextEntry)
    {
        try
        {
            _ = NextSequence(database, policy, deadline, archiveData: false,
                productionInspectionData: true, productionInspectionReserveOverride: reserveAfterNextEntry);
            return true;
        }
        catch (InvalidOperationException exception) when (IsCapacityReason(exception.Message)) { return false; }
    }

    private static (long Sequence, string PreviousHash, int SchemaVersion) NextSequence(
        sqlite3 db, AuditIntegrityPolicy policy, StoreDeadline deadline, bool archiveData,
        bool recipeDraftData = false, bool cameraSetupData = false, bool cameraNetworkData = false,
        bool imagingData = false,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic,
        bool governanceData = false, bool releaseData = false, bool contractData = false,
        bool activationData = false, bool previewData = false, bool calibrationImportData = false,
        bool manualInspectionData = false, long? manualInspectionReserveOverride = null,
        bool productionAdmissionData = false, long? productionAdmissionReserveOverride = null,
        bool stationQualificationData = false, long? stationQualificationReserveOverride = null,
        bool recipeTransferData = false, bool traceStoragePolicyData = false,
        long? traceStoragePolicyReserveOverride = null,
        bool qualificationCycleData = false, long? qualificationCycleReserveOverride = null,
        bool plcCommunicationData = false, long? plcCommunicationReserveOverride = null,
        bool productionInspectionData = false, long? productionInspectionReserveOverride = null,
        bool partIdentityData = false, long? partIdentityReserveOverride = null)
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
            if (schemaVersion is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
                CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or
                PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, ImagingSetupStoreOptions.ControlVerificationReserve);
            if (schemaVersion == CalibrationSessionStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, CalibrationSessionStoreOptions.ControlVerificationReserve);
            if (schemaVersion is CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or
                PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, CalibrationGovernanceStoreOptions.ControlVerificationReserve);
            if (schemaVersion is RecipeReleaseStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, RecipeReleaseStoreOptions.ControlVerificationReserve);
            if (schemaVersion is PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or
                PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, PlcResultContractStoreOptions.ControlVerificationReserve);
            if (schemaVersion is RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or
                CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, RecipeActivationStoreOptions.ControlVerificationReserve);
            if (schemaVersion == PreviewSessionStoreOptions.SchemaVersion ||
                schemaVersion == CalibrationImportStoreOptions.SchemaVersion ||
                schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
                (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion))
                controlReserve = Math.Max(controlReserve, PreviewSessionStoreOptions.ControlVerificationReserve);
            if (schemaVersion == CalibrationImportStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, CalibrationImportStoreOptions.ControlVerificationReserve);
            if (schemaVersion == ManualInspectionStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, ManualInspectionStoreOptions.ControlVerificationReserve);
            if (schemaVersion is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, StationQualificationStoreOptions.ControlVerificationReserve);
            if (schemaVersion == RecipeTransferStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, RecipeTransferStoreOptions.ControlVerificationReserve);
            if (schemaVersion is TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, TraceStoragePolicyStoreOptions.ControlVerificationReserve);
            if (schemaVersion is QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, QualificationCycleStoreOptions.ControlVerificationReserve);
            if (schemaVersion is PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, PlcCommunicationStoreOptions.ControlVerificationReserve);
            if (schemaVersion == ProductionInspectionStoreOptions.SchemaVersion || schemaVersion == PartIdentityStoreOptions.SchemaVersion)
                controlReserve = Math.Max(controlReserve, ProductionInspectionStoreOptions.ControlVerificationReserve);
            var reservedTerminalOperations = CameraNetworkReservedTerminalOperations(db,
                schemaVersion, cameraNetworkMode, deadline);
            // Schema 22 can carry the already-published schema-21 Manual
            // ledger.  Its pending sessions still reserve their terminal
            // audit budget; only a schema-22 store without those tables may
            // omit that reserve.
            var manualConfigured = TableExists(db, "manual_inspection_store_config", deadline) &&
                TableExists(db, "manual_inspection_events", deadline);
            var manualReserve = (schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
                ((schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) && manualConfigured))
                ? manualInspectionReserveOverride ?? SqliteCommandStore.ReadManualInspectionAuditReserve(db, deadline) : 0;
            var productionConfigured = TableExists(db, "production_admission_store_config", deadline) &&
                TableExists(db, "production_admission_events", deadline);
            var productionReserve = (schemaVersion == ProductionAdmissionStoreOptions.SchemaVersion ||
                ((schemaVersion is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) && productionConfigured))
                ? productionAdmissionReserveOverride ?? SqliteCommandStore.ReadProductionAdmissionAuditReserve(db, deadline) : 0;
            var qualificationConfigured = TableExists(db, "station_qualification_store_config", deadline) &&
                TableExists(db, "station_qualification_events", deadline);
            var stationReserve = schemaVersion == StationQualificationStoreOptions.SchemaVersion ||
                ((schemaVersion is RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) && qualificationConfigured)
                ? stationQualificationReserveOverride ?? SqliteCommandStore.ReadStationQualificationAuditReserve(db, deadline) : 0;
            var cycleConfigured = TableExists(db, "qualification_cycle_store_config", deadline) &&
                TableExists(db, "qualification_cycle_events", deadline);
            var cycleReserve = schemaVersion == QualificationCycleStoreOptions.SchemaVersion ||
                ((schemaVersion is PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) && cycleConfigured)
                ? qualificationCycleReserveOverride ?? SqliteCommandStore.ReadQualificationCycleAuditReserve(db, deadline) : 0;
            var plcConfigured = TableExists(db, "plc_communication_store_config", deadline) &&
                TableExists(db, "plc_communication_events", deadline);
            // Communication rows do not reserve a future terminal entry.  The
            // already committed communication audit rows are represented by the
            // current tail and must not be subtracted a second time here.
            var plcReserve = (schemaVersion is PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion) || plcConfigured
                ? plcCommunicationReserveOverride ?? 0 : 0;
            var productionInspectionConfigured = TableExists(db, "production_inspection_store_config", deadline) &&
                TableExists(db, "production_inspection_events", deadline);
            var productionInspectionReserve = productionInspectionConfigured
                ? productionInspectionReserveOverride ?? SqliteCommandStore.ReadProductionInspectionAuditReserve(db, deadline) : 0;
            var partIdentityConfigured = TableExists(db, "part_identity_store_config", deadline) &&
                TableExists(db, "part_identity_events", deadline);
            var partIdentityReserve = partIdentityConfigured
                ? partIdentityReserveOverride ?? SqliteCommandStore.ReadPartIdentityAuditReserve(db, deadline) : 0;
            var limit = checked(policy.MaximumVerificationEntries - controlReserve -
                reservedTerminalOperations * CameraNetworkStoreOptions.AuditEntriesPerTerminal - manualReserve - productionReserve - stationReserve -
                (traceStoragePolicyReserveOverride ?? 0) - cycleReserve - plcReserve - productionInspectionReserve - partIdentityReserve);
            Require(sequence <= limit, archiveData
                ? recipeDraftData ? "RecipeDraftArchiveCapacityExceeded" : "AlgorithmResultArchiveCapacityExceeded"
                : cameraNetworkData || schemaVersion == CameraNetworkStoreOptions.SchemaVersion
                    ? "CameraNetworkCapacityExceeded"
                : imagingData ? "ImagingSetupCapacityExceeded"
                : releaseData ? "RecipeReleaseAuditCapacityExceeded"
                : governanceData ? "CalibrationGovernanceAuditCapacityExceeded"
                : contractData ? "PlcResultContractAuditCapacityExceeded"
                : activationData ? "RecipeActivationAuditCapacityExceeded"
                : calibrationImportData ? "CalibrationImportAuditCapacityExceeded"
                : manualInspectionData ? "ManualInspectionAuditCapacityExceeded"
                : productionAdmissionData ? "ProductionAdmissionAuditCapacityExceeded"
                 : stationQualificationData ? "StationQualificationAuditCapacityExceeded"
                 : recipeTransferData ? "RecipeTransferAuditCapacityExceeded"
                 : traceStoragePolicyData ? "TraceStoragePolicyAuditCapacityExceeded"
                 : qualificationCycleData ? "QualificationCycleAuditCapacityExceeded"
                 : plcCommunicationData ? "PlcCommunicationAuditCapacityExceeded"
                 : productionInspectionData ? "ProductionInspectionAuditCapacityExceeded"
                 : partIdentityData ? "PartIdentityAuditCapacityExceeded"
                 : previewData ? "PreviewSessionAuditCapacityExceeded"
                : cameraSetupData ? "CameraSetupCapacityExceeded" : "AuditVerificationCapacityExceeded");
        }
        return (sequence, previous.Hash, schemaVersion);
    }

    internal static void EnsureManualInspectionTransactionCapacity(sqlite3 db, AuditIntegrityPolicy policy,
        int writes, long futureAudit, StoreDeadline deadline)
    {
        Require(writes > 0 && futureAudit >= 0, "ManualInspectionAuditReservationInvalid");
        _ = NextSequence(db, policy, deadline, archiveData: false, manualInspectionData: true,
            manualInspectionReserveOverride: checked(futureAudit + writes - 1));
    }

    /// <summary>
    /// Reserves the complete central-audit footprint of one production
    /// admission transaction while the writer still holds its transaction
    /// lock.  An accepted Arm keeps enough budget for its later terminal
    /// responsibility; a rejected Arm has no future responsibility.
    /// </summary>
    internal static long EnsureProductionAdmissionTransactionCapacity(sqlite3 db,
        AuditIntegrityPolicy policy, int writes, bool admitted, StoreDeadline deadline,
        bool completingAdmitted = false)
    {
        Require(writes > 0, "ProductionAdmissionAuditReservationInvalid");
        var configured = AuditChainDatabase.Read(db, @"
            SELECT MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes
            FROM production_admission_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => new ProductionAdmissionStoreOptions
            {
                MaximumEntries = checked((int)SqliteNative.ColumnInt64(statement, 0)),
                MaximumPayloadBytes = checked((int)SqliteNative.ColumnInt64(statement, 1)),
                MaximumTotalBytes = SqliteNative.ColumnInt64(statement, 2)
            }).SingleOrDefault() ?? throw new InvalidOperationException(
                "ProductionAdmissionConfigurationRequired");
        configured.Validate();
        var rows = SqliteCommandStore.ReadProductionAdmissionRows(db, configured, deadline);
        var pending = rows.GroupBy(row => row.Event.AttemptId)
            .Count(group => group.Last().Event.Kind == ProductionAdmissionEventKind.Admitted);
        Require(!completingAdmitted || pending > 0, "ProductionAdmissionAdmissionMissing");
        var remainingPending = checked(pending - (completingAdmitted ? 1 : 0));
        var rowsToReserve = checked(1 + remainingPending + (admitted ? 1 : 0));
        Require(checked(rows.Count + rowsToReserve) <= configured.MaximumEntries,
            "ProductionAdmissionEntryCapacityExceeded");
        var payloadSlots = checked(1 + remainingPending + (admitted ? 1 : 0));
        var usedPayloadBytes = rows.Aggregate(0L,
            (sum, row) => checked(sum + row.Payload.Length));
        Require(checked(usedPayloadBytes + checked((long)payloadSlots * configured.MaximumPayloadBytes) <=
            configured.MaximumTotalBytes), "ProductionAdmissionTotalCapacityExceeded");
        var existingReserve = SqliteCommandStore.ReadProductionAdmissionAuditReserve(db, deadline);
        var futureAudit = admitted ? ProductionAdmissionStoreOptions.AuditEntriesPerEvent : 0;
        var releasedAudit = completingAdmitted ? ProductionAdmissionStoreOptions.AuditEntriesPerEvent : 0;
        var reserve = checked(existingReserve - releasedAudit + futureAudit + writes - 1);
        Require(reserve >= 0, "ProductionAdmissionAuditReservationInvalid");
        _ = NextSequence(db, policy, deadline, archiveData: false,
            productionAdmissionData: true,
            productionAdmissionReserveOverride: reserve);
        return reserve;
    }

    /// <summary>
    /// Reserves the complete central-audit and local-ledger footprint of one
    /// accepted Station Qualification identity transaction.  The candidate is
    /// included in the post-state calculation, so a newly admitted session
    /// cannot consume the last central or local row needed by its recovery
    /// terminal event.
    /// </summary>
    internal static long EnsureStationQualificationTransactionCapacity(sqlite3 db,
        AuditIntegrityPolicy policy, int writes, StationQualificationSessionEvent candidate,
        StoreDeadline deadline, long? qualificationCycleReserveOverride = null)
    {
        Require(writes > 0, "StationQualificationAuditReservationInvalid");
        var configured = Read(db, @"
            SELECT MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes
            FROM station_qualification_store_config WHERE Id=1 LIMIT 2;", deadline,
            statement => new StationQualificationStoreOptions
            {
                MaximumEntries = checked((int)SqliteNative.ColumnInt64(statement, 0)),
                MaximumPayloadBytes = checked((int)SqliteNative.ColumnInt64(statement, 1)),
                MaximumTotalBytes = SqliteNative.ColumnInt64(statement, 2)
            }).SingleOrDefault() ?? throw new InvalidOperationException(
                "StationQualificationConfigurationRequired");
        configured.Validate();
        var rows = SqliteCommandStore.ReadStationQualificationRows(db, configured, deadline);
        var current = rows.Select(value => value.Event)
            .GroupBy(value => value.SessionId)
            .Select(group => group.OrderBy(value => value.Position).Last())
            .Where(value => !value.Terminal)
            .ToArray();
        if (candidate.CommandKind == AuditedCommandKind.StartStationQualificationSession &&
            candidate.Phase == StationQualificationSessionPhase.Admitted)
            Require(current.Length == 0, "StationQualificationSessionAlreadyPending");
        else
            Require(current.Any(value => value.SessionId == candidate.SessionId),
                "StationQualificationSessionMissing");

        var currentPayload = StationQualificationStorageCodec.Encode(candidate).Length;
        var futureRows = SqliteCommandStore.ReadStationQualificationFutureRowsAfter(rows, candidate);
        Require(checked((long)rows.Count + 1 + futureRows) <= configured.MaximumEntries,
            "StationQualificationEntryCapacityExceeded");
        var usedBytes = rows.Aggregate(0L,
            (sum, row) => checked(sum + row.Payload.Length));
        Require(checked(usedBytes + currentPayload +
            futureRows * (long)configured.MaximumPayloadBytes) <= configured.MaximumTotalBytes,
            "StationQualificationTotalCapacityExceeded");

        var postAuditReserve = SqliteCommandStore.ReadStationQualificationAuditReserveAfter(rows, candidate);
        var reserve = checked(postAuditReserve + writes - 1);
        _ = NextSequence(db, policy, deadline, archiveData: false,
            stationQualificationData: true,
            stationQualificationReserveOverride: reserve,
            qualificationCycleReserveOverride: qualificationCycleReserveOverride);
        return reserve;
    }

    /// <summary>
    /// Reserves the immutable trace-policy publication row and its central
    /// audit payload before identity/command facts are appended.  The ledger
    /// has no terminal workflow, so one publication is the complete future
    /// obligation; the central control reserve still protects verification.
    /// </summary>
    internal static long EnsureTraceStoragePolicyTransactionCapacity(sqlite3 db,
        AuditIntegrityPolicy policy, TraceStoragePolicyStoreOptions options,
        int candidatePayloadBytes, StoreDeadline deadline)
    {
        options.Validate();
        Require(candidatePayloadBytes > 0 && candidatePayloadBytes <= options.MaximumPayloadBytes,
            "TraceStoragePolicyPayloadCapacityExceeded");
        var rows = Scalar(db, "SELECT COUNT(*) FROM trace_storage_policy_events;", deadline);
        Require(rows < options.MaximumEntries, "TraceStoragePolicyEntryCapacityExceeded");
        var usedBytes = Scalar(db, @"
            SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
            FROM audit_entries WHERE TraceStoragePolicyPosition IS NOT NULL;", deadline);
        Require(checked(usedBytes + candidatePayloadBytes) <= options.MaximumTotalBytes,
            "TraceStoragePolicyTotalCapacityExceeded");
        var reserve = checked(TraceStoragePolicyStoreOptions.AuditEntriesPerEvent - 1);
        _ = NextSequence(db, policy, deadline, archiveData: false,
            traceStoragePolicyData: true, traceStoragePolicyReserveOverride: reserve);
        return reserve;
    }

    internal static void EnsureNextSequenceAvailable(sqlite3 db, AuditIntegrityPolicy policy,
        StoreDeadline deadline, bool archiveData, bool recipeDraftData = false, bool cameraSetupData = false,
        bool cameraNetworkData = false, bool imagingData = false, bool releaseData = false,
        bool contractData = false, bool activationData = false, bool previewData = false,
        bool calibrationImportData = false, bool manualInspectionData = false,
        bool productionAdmissionData = false) =>
        _ = NextSequence(db, policy, deadline, archiveData, recipeDraftData, cameraSetupData, cameraNetworkData,
            imagingData, releaseData: releaseData, contractData: contractData, activationData: activationData,
            previewData: previewData, calibrationImportData: calibrationImportData,
            manualInspectionData: manualInspectionData, productionAdmissionData: productionAdmissionData);

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
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic,
        long? manualInspectionReserveOverride = null,
        long? productionAdmissionReserveOverride = null,
        long? stationQualificationReserveOverride = null,
        long? traceStoragePolicyReserveOverride = null,
        long? qualificationCycleReserveOverride = null,
        long? partIdentityReserveOverride = null)
    {
        var position = Scalar(db, "SELECT Position FROM command_facts WHERE EventId=?;", deadline, eventId.ToString("D"));
        var payload = CommandPayload(db, position, deadline);
        AppendEntry(db, policy, "CommandFact", position, payload, deadline,
            cameraNetworkMode: cameraNetworkMode, manualInspectionReserveOverride: manualInspectionReserveOverride,
            productionAdmissionReserveOverride: productionAdmissionReserveOverride,
            stationQualificationReserveOverride: stationQualificationReserveOverride,
            traceStoragePolicyReserveOverride: traceStoragePolicyReserveOverride,
            qualificationCycleReserveOverride: qualificationCycleReserveOverride,
            partIdentityReserveOverride: partIdentityReserveOverride);
        var tail = Tail(db, deadline);
        var lastCheckpoint = Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline);
        if (tail.Sequence - lastCheckpoint >= policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
    }

    private static long AppendEntry(sqlite3 db, AuditIntegrityPolicy policy, string kind, long? position,
        byte[] payload, StoreDeadline deadline, long? identityPosition = null, long? alarmPosition = null,
        long? resultPosition = null, long? draftPosition = null, long? networkPosition = null,
        long? imagingPosition = null,
        long? calibrationSessionPosition = null, long? calibrationEventPosition = null,
        long? calibrationManifestPosition = null,
        long? governancePosition = null,
        long? releasePosition = null,
        long? plcResultContractPosition = null,
        long? activationPosition = null,
        long? previewPosition = null,
        long? calibrationImportPosition = null,
        long? manualInspectionPosition = null,
        long? productionAdmissionPosition = null,
        long? stationQualificationPosition = null,
        long? recipeTransferPosition = null,
        long? traceStoragePolicyPosition = null,
        long? qualificationCyclePosition = null,
        long? plcCommunicationPosition = null,
        long? productionInspectionPosition = null,
        long? partIdentityPosition = null,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic,
        long? manualInspectionReserveOverride = null,
        long? productionAdmissionReserveOverride = null,
        long? stationQualificationReserveOverride = null,
        long? traceStoragePolicyReserveOverride = null,
        long? qualificationCycleReserveOverride = null,
        long? plcCommunicationReserveOverride = null,
        long? productionInspectionReserveOverride = null,
        long? partIdentityReserveOverride = null)
    {
        var next = NextSequence(db, policy, deadline, archiveData: false,
            cameraNetworkData: cameraNetworkMode != CameraNetworkAuditWriteMode.Generic,
            imagingData: imagingPosition is not null || kind is "ImagingSetupRevision" or "ImagingSetupStoreActivated",
            cameraNetworkMode: cameraNetworkMode, governanceData: governancePosition is not null ||
                kind is "CalibrationGovernanceStoreActivated" or "CalibrationGovernanceEvent",
            releaseData: releasePosition is not null || kind is "RecipeReleaseStoreActivated" or "RecipeReleaseEvent",
            contractData: plcResultContractPosition is not null || kind is "PlcResultContractStoreActivated" or "PlcResultContractEvent",
            activationData: activationPosition is not null || kind is "RecipeActivationStoreActivated" or "RecipeActivationEvent",
            previewData: previewPosition is not null || kind is "PreviewSessionStoreActivated" or "PreviewSessionEvent",
            calibrationImportData: calibrationImportPosition is not null ||
                kind is "CalibrationImportStoreActivated" or "CalibrationImportEvent",
            manualInspectionData: manualInspectionPosition is not null ||
                kind is "ManualInspectionStoreActivated" or "ManualInspectionEvent",
             productionAdmissionData: productionAdmissionPosition is not null ||
                 kind is "ProductionAdmissionStoreActivated" or "ProductionAdmissionEvent",
             stationQualificationData: stationQualificationPosition is not null ||
                 kind is "StationQualificationStoreActivated" or "StationQualificationEvent",
             recipeTransferData: recipeTransferPosition is not null ||
                 kind is "RecipeTransferStoreActivated" or "RecipeTransferEvent",
            traceStoragePolicyData: traceStoragePolicyPosition is not null ||
                kind is "TraceStoragePolicyStoreActivated" or "TraceStoragePolicyEvent",
            qualificationCycleData: qualificationCyclePosition is not null ||
                kind is "QualificationCycleStoreActivated" or "QualificationCycleEvent",
            plcCommunicationData: plcCommunicationPosition is not null ||
                 kind is "PlcCommunicationStoreActivated" or "PlcCommunicationEvent",
             productionInspectionData: productionInspectionPosition is not null ||
                 kind is "ProductionInspectionStoreActivated" or "ProductionInspectionEvent",
             partIdentityData: partIdentityPosition is not null ||
                 kind is "PartIdentityStoreActivated" or "PartIdentityEvent",
             manualInspectionReserveOverride: manualInspectionReserveOverride,
             productionAdmissionReserveOverride: productionAdmissionReserveOverride,
             stationQualificationReserveOverride: stationQualificationReserveOverride,
             traceStoragePolicyReserveOverride: traceStoragePolicyReserveOverride,
             qualificationCycleReserveOverride: qualificationCycleReserveOverride,
             plcCommunicationReserveOverride: plcCommunicationReserveOverride,
             productionInspectionReserveOverride: productionInspectionReserveOverride,
             partIdentityReserveOverride: partIdentityReserveOverride);
        var sequence = next.Sequence;
        var previousHash = next.PreviousHash;
        var schemaVersion = next.SchemaVersion;
         var hash = schemaVersion >= PartIdentityStoreOptions.SchemaVersion
             ? EntryHashV25(schemaVersion, policy.StationId, sequence, previousHash, kind,
                 position is { } ordinal29 ? Number(ordinal29) : null,
                 identityPosition is { } identity29 ? Number(identity29) : null,
                 alarmPosition is { } alarm29 ? Number(alarm29) : null,
                 resultPosition is { } result29 ? Number(result29) : null,
                 draftPosition is { } draft29 ? Number(draft29) : null,
                 null,
                 networkPosition is { } network29 ? Number(network29) : null,
                 imagingPosition is { } imaging29 ? Number(imaging29) : null,
                 calibrationSessionPosition is { } session29 ? Number(session29) : null,
                 calibrationEventPosition is { } event29 ? Number(event29) : null,
                 calibrationManifestPosition is { } manifest29 ? Number(manifest29) : null,
                 governancePosition is { } governance29 ? Number(governance29) : null,
                 releasePosition is { } release29 ? Number(release29) : null,
                 plcResultContractPosition is { } contract29 ? Number(contract29) : null,
                 activationPosition is { } activation29 ? Number(activation29) : null,
                 previewPosition is { } preview29 ? Number(preview29) : null,
                 calibrationImportPosition is { } import29 ? Number(import29) : null,
                 manualInspectionPosition is { } manual29 ? Number(manual29) : null,
                 productionAdmissionPosition is { } production29 ? Number(production29) : null,
                 stationQualificationPosition is { } station29 ? Number(station29) : null,
                 recipeTransferPosition is { } transfer29 ? Number(transfer29) : null,
                 traceStoragePolicyPosition is { } trace29 ? Number(trace29) : null,
                 qualificationCyclePosition is { } cycle29 ? Number(cycle29) : null,
                 plcCommunicationPosition is { } plc29 ? Number(plc29) : null,
                 productionInspectionPosition is { } inspection29 ? Number(inspection29) : null,
                 partIdentityPosition is { } part29 ? Number(part29) : null, payload)
             : schemaVersion >= ProductionInspectionStoreOptions.SchemaVersion
             ? EntryHashV24(schemaVersion, policy.StationId, sequence, previousHash, kind,
                 position is { } ordinal28 ? Number(ordinal28) : null,
                 identityPosition is { } identity28 ? Number(identity28) : null,
                 alarmPosition is { } alarm28 ? Number(alarm28) : null,
                 resultPosition is { } result28 ? Number(result28) : null,
                 draftPosition is { } draft28 ? Number(draft28) : null,
                 null,
                 networkPosition is { } network28 ? Number(network28) : null,
                 imagingPosition is { } imaging28 ? Number(imaging28) : null,
                 calibrationSessionPosition is { } session28 ? Number(session28) : null,
                 calibrationEventPosition is { } event28 ? Number(event28) : null,
                 calibrationManifestPosition is { } manifest28 ? Number(manifest28) : null,
                 governancePosition is { } governance28 ? Number(governance28) : null,
                 releasePosition is { } release28 ? Number(release28) : null,
                 plcResultContractPosition is { } contract28 ? Number(contract28) : null,
                 activationPosition is { } activation28 ? Number(activation28) : null,
                 previewPosition is { } preview28 ? Number(preview28) : null,
                 calibrationImportPosition is { } import28 ? Number(import28) : null,
                 manualInspectionPosition is { } manual28 ? Number(manual28) : null,
                 productionAdmissionPosition is { } production28 ? Number(production28) : null,
                 stationQualificationPosition is { } station28 ? Number(station28) : null,
                 recipeTransferPosition is { } transfer28 ? Number(transfer28) : null,
                 traceStoragePolicyPosition is { } trace28 ? Number(trace28) : null,
                 qualificationCyclePosition is { } cycle28 ? Number(cycle28) : null,
                 plcCommunicationPosition is { } plc28 ? Number(plc28) : null,
                 productionInspectionPosition is { } inspection28 ? Number(inspection28) : null, payload)
             : schemaVersion >= PlcCommunicationStoreOptions.SchemaVersion
             ? EntryHashV23(schemaVersion, policy.StationId, sequence, previousHash, kind,
                 position is { } ordinal27 ? Number(ordinal27) : null,
                 identityPosition is { } identity27 ? Number(identity27) : null,
                 alarmPosition is { } alarm27 ? Number(alarm27) : null,
                 resultPosition is { } result27 ? Number(result27) : null,
                 draftPosition is { } draft27 ? Number(draft27) : null,
                 null,
                 networkPosition is { } network27 ? Number(network27) : null,
                 imagingPosition is { } imaging27 ? Number(imaging27) : null,
                 calibrationSessionPosition is { } session27 ? Number(session27) : null,
                 calibrationEventPosition is { } event27 ? Number(event27) : null,
                 calibrationManifestPosition is { } manifest27 ? Number(manifest27) : null,
                 governancePosition is { } governance27 ? Number(governance27) : null,
                 releasePosition is { } release27 ? Number(release27) : null,
                 plcResultContractPosition is { } contract27 ? Number(contract27) : null,
                 activationPosition is { } activation27 ? Number(activation27) : null,
                 previewPosition is { } preview27 ? Number(preview27) : null,
                 calibrationImportPosition is { } import27 ? Number(import27) : null,
                 manualInspectionPosition is { } manual27 ? Number(manual27) : null,
                 productionAdmissionPosition is { } production27 ? Number(production27) : null,
                 stationQualificationPosition is { } station27 ? Number(station27) : null,
                 recipeTransferPosition is { } transfer27 ? Number(transfer27) : null,
                 traceStoragePolicyPosition is { } trace27 ? Number(trace27) : null,
                 qualificationCyclePosition is { } cycle27 ? Number(cycle27) : null,
                 plcCommunicationPosition is { } plc27 ? Number(plc27) : null, payload)
             : schemaVersion >= QualificationCycleStoreOptions.SchemaVersion
             ? EntryHashV22(schemaVersion, policy.StationId, sequence, previousHash, kind,
                 position is { } ordinal26 ? Number(ordinal26) : null,
                 identityPosition is { } identity26 ? Number(identity26) : null,
                 alarmPosition is { } alarm26 ? Number(alarm26) : null,
                 resultPosition is { } result26 ? Number(result26) : null,
                 draftPosition is { } draft26 ? Number(draft26) : null,
                 null,
                 networkPosition is { } network26 ? Number(network26) : null,
                 imagingPosition is { } imaging26 ? Number(imaging26) : null,
                 calibrationSessionPosition is { } session26 ? Number(session26) : null,
                 calibrationEventPosition is { } event26 ? Number(event26) : null,
                 calibrationManifestPosition is { } manifest26 ? Number(manifest26) : null,
                 governancePosition is { } governance26 ? Number(governance26) : null,
                 releasePosition is { } release26 ? Number(release26) : null,
                 plcResultContractPosition is { } contract26 ? Number(contract26) : null,
                 activationPosition is { } activation26 ? Number(activation26) : null,
                 previewPosition is { } preview26 ? Number(preview26) : null,
                 calibrationImportPosition is { } import26 ? Number(import26) : null,
                 manualInspectionPosition is { } manual26 ? Number(manual26) : null,
                 productionAdmissionPosition is { } production26 ? Number(production26) : null,
                 stationQualificationPosition is { } station26 ? Number(station26) : null,
                 recipeTransferPosition is { } transfer26 ? Number(transfer26) : null,
                 traceStoragePolicyPosition is { } trace26 ? Number(trace26) : null,
                 qualificationCyclePosition is { } cycle26 ? Number(cycle26) : null, payload)
             : schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion
             ? EntryHashV21(schemaVersion, policy.StationId, sequence, previousHash, kind,
                 position is { } ordinal25 ? Number(ordinal25) : null,
                 identityPosition is { } identity25 ? Number(identity25) : null,
                 alarmPosition is { } alarm25 ? Number(alarm25) : null,
                 resultPosition is { } result25 ? Number(result25) : null,
                 draftPosition is { } draft25 ? Number(draft25) : null,
                 null,
                 networkPosition is { } network25 ? Number(network25) : null,
                 imagingPosition is { } imaging25 ? Number(imaging25) : null,
                 calibrationSessionPosition is { } session25 ? Number(session25) : null,
                 calibrationEventPosition is { } event25 ? Number(event25) : null,
                 calibrationManifestPosition is { } manifest25 ? Number(manifest25) : null,
                 governancePosition is { } governance25 ? Number(governance25) : null,
                 releasePosition is { } release25 ? Number(release25) : null,
                 plcResultContractPosition is { } contract25 ? Number(contract25) : null,
                 activationPosition is { } activation25 ? Number(activation25) : null,
                 previewPosition is { } preview25 ? Number(preview25) : null,
                 calibrationImportPosition is { } import25 ? Number(import25) : null,
                 manualInspectionPosition is { } manual25 ? Number(manual25) : null,
                 productionAdmissionPosition is { } production25 ? Number(production25) : null,
                 stationQualificationPosition is { } station25 ? Number(station25) : null,
                 recipeTransferPosition is { } transfer25 ? Number(transfer25) : null,
                 traceStoragePolicyPosition is { } trace25 ? Number(trace25) : null, payload)
             : schemaVersion >= RecipeTransferStoreOptions.SchemaVersion
             ? EntryHashV20(schemaVersion, policy.StationId, sequence, previousHash, kind,
                 position is { } ordinal24 ? Number(ordinal24) : null,
                 identityPosition is { } identity24 ? Number(identity24) : null,
                 alarmPosition is { } alarm24 ? Number(alarm24) : null,
                 resultPosition is { } result24 ? Number(result24) : null,
                 draftPosition is { } draft24 ? Number(draft24) : null,
                 null,
                 networkPosition is { } network24 ? Number(network24) : null,
                 imagingPosition is { } imaging24 ? Number(imaging24) : null,
                 calibrationSessionPosition is { } session24 ? Number(session24) : null,
                 calibrationEventPosition is { } event24 ? Number(event24) : null,
                 calibrationManifestPosition is { } manifest24 ? Number(manifest24) : null,
                 governancePosition is { } governance24 ? Number(governance24) : null,
                 releasePosition is { } release24 ? Number(release24) : null,
                 plcResultContractPosition is { } contract24 ? Number(contract24) : null,
                 activationPosition is { } activation24 ? Number(activation24) : null,
                 previewPosition is { } preview24 ? Number(preview24) : null,
                 calibrationImportPosition is { } import24 ? Number(import24) : null,
                 manualInspectionPosition is { } manual24 ? Number(manual24) : null,
                 productionAdmissionPosition is { } production24 ? Number(production24) : null,
                 stationQualificationPosition is { } station24 ? Number(station24) : null,
                 recipeTransferPosition is { } transfer24 ? Number(transfer24) : null, payload)
             : schemaVersion >= StationQualificationStoreOptions.SchemaVersion
            ? EntryHashV19(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal23 ? Number(ordinal23) : null,
                identityPosition is { } identity23 ? Number(identity23) : null,
                alarmPosition is { } alarm23 ? Number(alarm23) : null,
                resultPosition is { } result23 ? Number(result23) : null,
                draftPosition is { } draft23 ? Number(draft23) : null,
                null,
                networkPosition is { } network23 ? Number(network23) : null,
                imagingPosition is { } imaging23 ? Number(imaging23) : null,
                calibrationSessionPosition is { } session23 ? Number(session23) : null,
                calibrationEventPosition is { } event23 ? Number(event23) : null,
                calibrationManifestPosition is { } manifest23 ? Number(manifest23) : null,
                governancePosition is { } governance23 ? Number(governance23) : null,
                releasePosition is { } release23 ? Number(release23) : null,
                plcResultContractPosition is { } contract23 ? Number(contract23) : null,
                activationPosition is { } activation23 ? Number(activation23) : null,
                previewPosition is { } preview23 ? Number(preview23) : null,
                calibrationImportPosition is { } import23 ? Number(import23) : null,
                manualInspectionPosition is { } manual23 ? Number(manual23) : null,
                productionAdmissionPosition is { } production23 ? Number(production23) : null,
                stationQualificationPosition is { } station23 ? Number(station23) : null, payload)
            : schemaVersion >= ProductionAdmissionStoreOptions.SchemaVersion
            ? EntryHashV18(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal22 ? Number(ordinal22) : null,
                identityPosition is { } identity22 ? Number(identity22) : null,
                alarmPosition is { } alarm22 ? Number(alarm22) : null,
                resultPosition is { } result22 ? Number(result22) : null,
                draftPosition is { } draft22 ? Number(draft22) : null,
                null,
                networkPosition is { } network22 ? Number(network22) : null,
                imagingPosition is { } imaging22 ? Number(imaging22) : null,
                calibrationSessionPosition is { } calibrationSession22 ? Number(calibrationSession22) : null,
                calibrationEventPosition is { } calibrationEvent22 ? Number(calibrationEvent22) : null,
                calibrationManifestPosition is { } calibrationManifest22 ? Number(calibrationManifest22) : null,
                governancePosition is { } governance22 ? Number(governance22) : null,
                releasePosition is { } release22 ? Number(release22) : null,
                plcResultContractPosition is { } contract22 ? Number(contract22) : null,
                activationPosition is { } activation22 ? Number(activation22) : null,
                previewPosition is { } preview22 ? Number(preview22) : null,
                calibrationImportPosition is { } import22 ? Number(import22) : null,
                manualInspectionPosition is { } manual22 ? Number(manual22) : null,
                productionAdmissionPosition is { } admission22 ? Number(admission22) : null, payload)
            : schemaVersion >= ManualInspectionStoreOptions.SchemaVersion
            ? EntryHashV17(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal21 ? Number(ordinal21) : null,
                identityPosition is { } identity21 ? Number(identity21) : null,
                alarmPosition is { } alarm21 ? Number(alarm21) : null,
                resultPosition is { } result21 ? Number(result21) : null,
                draftPosition is { } draft21 ? Number(draft21) : null,
                null,
                networkPosition is { } network21 ? Number(network21) : null,
                imagingPosition is { } imaging21 ? Number(imaging21) : null,
                calibrationSessionPosition is { } calibrationSession21 ? Number(calibrationSession21) : null,
                calibrationEventPosition is { } calibrationEvent21 ? Number(calibrationEvent21) : null,
                calibrationManifestPosition is { } calibrationManifest21 ? Number(calibrationManifest21) : null,
                governancePosition is { } governance21 ? Number(governance21) : null,
                releasePosition is { } release21 ? Number(release21) : null,
                plcResultContractPosition is { } contract21 ? Number(contract21) : null,
                activationPosition is { } activation21 ? Number(activation21) : null,
                previewPosition is { } preview21 ? Number(preview21) : null,
                calibrationImportPosition is { } import21 ? Number(import21) : null,
                manualInspectionPosition is { } manual21 ? Number(manual21) : null, payload)
            : schemaVersion >= CalibrationImportStoreOptions.SchemaVersion
            ? EntryHashV16(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal20 ? Number(ordinal20) : null,
                identityPosition is { } identity20 ? Number(identity20) : null,
                alarmPosition is { } alarm20 ? Number(alarm20) : null,
                resultPosition is { } result20 ? Number(result20) : null,
                draftPosition is { } draft20 ? Number(draft20) : null,
                null,
                networkPosition is { } network20 ? Number(network20) : null,
                imagingPosition is { } imaging20 ? Number(imaging20) : null,
                calibrationSessionPosition is { } calibrationSession20 ? Number(calibrationSession20) : null,
                calibrationEventPosition is { } calibrationEvent20 ? Number(calibrationEvent20) : null,
                calibrationManifestPosition is { } calibrationManifest20 ? Number(calibrationManifest20) : null,
                governancePosition is { } governance20 ? Number(governance20) : null,
                releasePosition is { } release20 ? Number(release20) : null,
                plcResultContractPosition is { } contract20 ? Number(contract20) : null,
                activationPosition is { } activation20 ? Number(activation20) : null,
                previewPosition is { } preview20 ? Number(preview20) : null,
                calibrationImportPosition is { } import20 ? Number(import20) : null, payload)
            : schemaVersion >= PreviewSessionStoreOptions.SchemaVersion
            ? EntryHashV15(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal19 ? Number(ordinal19) : null,
                identityPosition is { } identity19 ? Number(identity19) : null,
                alarmPosition is { } alarm19 ? Number(alarm19) : null,
                resultPosition is { } result19 ? Number(result19) : null,
                draftPosition is { } draft19 ? Number(draft19) : null,
                null,
                networkPosition is { } network19 ? Number(network19) : null,
                imagingPosition is { } imaging19 ? Number(imaging19) : null,
                calibrationSessionPosition is { } calibrationSession19 ? Number(calibrationSession19) : null,
                calibrationEventPosition is { } calibrationEvent19 ? Number(calibrationEvent19) : null,
                calibrationManifestPosition is { } calibrationManifest19 ? Number(calibrationManifest19) : null,
                governancePosition is { } governance19 ? Number(governance19) : null,
                releasePosition is { } release19 ? Number(release19) : null,
                plcResultContractPosition is { } contract19 ? Number(contract19) : null,
                activationPosition is { } activation19 ? Number(activation19) : null,
                previewPosition is { } preview19 ? Number(preview19) : null, payload)
            : schemaVersion >= RecipeActivationStoreOptions.SchemaVersion
            ? EntryHashV14(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal18 ? Number(ordinal18) : null,
                identityPosition is { } identity18 ? Number(identity18) : null,
                alarmPosition is { } alarm18 ? Number(alarm18) : null,
                resultPosition is { } result18 ? Number(result18) : null,
                draftPosition is { } draft18 ? Number(draft18) : null,
                null,
                networkPosition is { } network18 ? Number(network18) : null,
                imagingPosition is { } imaging18 ? Number(imaging18) : null,
                calibrationSessionPosition is { } calibrationSession18 ? Number(calibrationSession18) : null,
                calibrationEventPosition is { } calibrationEvent18 ? Number(calibrationEvent18) : null,
                calibrationManifestPosition is { } calibrationManifest18 ? Number(calibrationManifest18) : null,
                governancePosition is { } governance18 ? Number(governance18) : null,
                releasePosition is { } release18 ? Number(release18) : null,
                plcResultContractPosition is { } contract18 ? Number(contract18) : null,
                activationPosition is { } activation18 ? Number(activation18) : null, payload)
            : schemaVersion >= PlcResultContractStoreOptions.SchemaVersion
            ? EntryHashV13(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal17 ? Number(ordinal17) : null,
                identityPosition is { } identity17 ? Number(identity17) : null,
                alarmPosition is { } alarm17 ? Number(alarm17) : null,
                resultPosition is { } result17 ? Number(result17) : null,
                draftPosition is { } draft17 ? Number(draft17) : null,
                null,
                networkPosition is { } network17 ? Number(network17) : null,
                imagingPosition is { } imaging17 ? Number(imaging17) : null,
                calibrationSessionPosition is { } calibrationSession17 ? Number(calibrationSession17) : null,
                calibrationEventPosition is { } calibrationEvent17 ? Number(calibrationEvent17) : null,
                calibrationManifestPosition is { } calibrationManifest17 ? Number(calibrationManifest17) : null,
                governancePosition is { } governance17 ? Number(governance17) : null,
                releasePosition is { } release17 ? Number(release17) : null,
                plcResultContractPosition is { } contract17 ? Number(contract17) : null, payload)
            : schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion
            ? EntryHashV12(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal16 ? Number(ordinal16) : null,
                identityPosition is { } identity16 ? Number(identity16) : null,
                alarmPosition is { } alarm16 ? Number(alarm16) : null,
                resultPosition is { } result16 ? Number(result16) : null,
                draftPosition is { } draft16 ? Number(draft16) : null,
                null,
                networkPosition is { } network16 ? Number(network16) : null,
                imagingPosition is { } imaging16 ? Number(imaging16) : null,
                calibrationSessionPosition is { } calibrationSession16 ? Number(calibrationSession16) : null,
                calibrationEventPosition is { } calibrationEvent16 ? Number(calibrationEvent16) : null,
                calibrationManifestPosition is { } calibrationManifest16 ? Number(calibrationManifest16) : null,
                governancePosition is { } governance16 ? Number(governance16) : null,
                releasePosition is { } release16 ? Number(release16) : null, payload)
            : schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion
            ? EntryHashV11(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal15 ? Number(ordinal15) : null,
                identityPosition is { } identity15 ? Number(identity15) : null,
                alarmPosition is { } alarm15 ? Number(alarm15) : null,
                resultPosition is { } result15 ? Number(result15) : null,
                draftPosition is { } draft15 ? Number(draft15) : null,
                null,
                networkPosition is { } network15 ? Number(network15) : null,
                imagingPosition is { } imaging15 ? Number(imaging15) : null,
                calibrationSessionPosition is { } calibrationSession15 ? Number(calibrationSession15) : null,
                calibrationEventPosition is { } calibrationEvent15 ? Number(calibrationEvent15) : null,
                calibrationManifestPosition is { } calibrationManifest15 ? Number(calibrationManifest15) : null,
                governancePosition is { } governance15 ? Number(governance15) : null, payload)
            : EntryHash(schemaVersion, policy.StationId, sequence, previousHash, kind,
                position is { } ordinal ? Number(ordinal) : null,
                identityPosition is { } identity ? Number(identity) : null,
                alarmPosition is { } alarm ? Number(alarm) : null,
                resultPosition is { } result ? Number(result) : null,
                draftPosition is { } draft ? Number(draft) : null,
                null,
                networkPosition is { } network ? Number(network) : null,
                imagingPosition is { } imaging ? Number(imaging) : null,
                calibrationSessionPosition is { } calibrationSession ? Number(calibrationSession) : null,
                calibrationEventPosition is { } calibrationEvent ? Number(calibrationEvent) : null,
                calibrationManifestPosition is { } calibrationManifest ? Number(calibrationManifest) : null,
                payload);
        if (schemaVersion >= PartIdentityStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,ProductionInspectionPosition,PartIdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p29 ? Number(p29) : null,
                identityPosition is { } i29 ? Number(i29) : null,
                alarmPosition is { } a29 ? Number(a29) : null,
                resultPosition is { } r29 ? Number(r29) : null,
                draftPosition is { } d29 ? Number(d29) : null,
                null, networkPosition is { } n29 ? Number(n29) : null,
                imagingPosition is { } m29 ? Number(m29) : null,
                calibrationSessionPosition is { } s29 ? Number(s29) : null,
                calibrationEventPosition is { } e29 ? Number(e29) : null,
                calibrationManifestPosition is { } f29 ? Number(f29) : null,
                governancePosition is { } g29 ? Number(g29) : null,
                releasePosition is { } q29 ? Number(q29) : null,
                plcResultContractPosition is { } c29 ? Number(c29) : null,
                activationPosition is { } x29 ? Number(x29) : null,
                previewPosition is { } y29 ? Number(y29) : null,
                calibrationImportPosition is { } z29 ? Number(z29) : null,
                manualInspectionPosition is { } u29 ? Number(u29) : null,
                productionAdmissionPosition is { } v29 ? Number(v29) : null,
                stationQualificationPosition is { } w29 ? Number(w29) : null,
                recipeTransferPosition is { } t29 ? Number(t29) : null,
                traceStoragePolicyPosition is { } l29 ? Number(l29) : null,
                qualificationCyclePosition is { } k29 ? Number(k29) : null,
                plcCommunicationPosition is { } h29 ? Number(h29) : null,
                productionInspectionPosition is { } j29 ? Number(j29) : null,
                partIdentityPosition is { } b29 ? Number(b29) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= ProductionInspectionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,ProductionInspectionPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p28 ? Number(p28) : null,
                identityPosition is { } i28 ? Number(i28) : null,
                alarmPosition is { } a28 ? Number(a28) : null,
                resultPosition is { } r28 ? Number(r28) : null,
                draftPosition is { } d28 ? Number(d28) : null,
                null, networkPosition is { } n28 ? Number(n28) : null,
                imagingPosition is { } m28 ? Number(m28) : null,
                calibrationSessionPosition is { } s28 ? Number(s28) : null,
                calibrationEventPosition is { } e28 ? Number(e28) : null,
                calibrationManifestPosition is { } f28 ? Number(f28) : null,
                governancePosition is { } g28 ? Number(g28) : null,
                releasePosition is { } q28 ? Number(q28) : null,
                plcResultContractPosition is { } c28 ? Number(c28) : null,
                activationPosition is { } x28 ? Number(x28) : null,
                previewPosition is { } y28 ? Number(y28) : null,
                calibrationImportPosition is { } z28 ? Number(z28) : null,
                manualInspectionPosition is { } u28 ? Number(u28) : null,
                productionAdmissionPosition is { } v28 ? Number(v28) : null,
                stationQualificationPosition is { } w28 ? Number(w28) : null,
                recipeTransferPosition is { } t28 ? Number(t28) : null,
                traceStoragePolicyPosition is { } l28 ? Number(l28) : null,
                qualificationCyclePosition is { } k28 ? Number(k28) : null,
                plcCommunicationPosition is { } h28 ? Number(h28) : null,
                productionInspectionPosition is { } j28 ? Number(j28) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= PlcCommunicationStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p27 ? Number(p27) : null,
                identityPosition is { } i27 ? Number(i27) : null,
                alarmPosition is { } a27 ? Number(a27) : null,
                resultPosition is { } r27 ? Number(r27) : null,
                draftPosition is { } d27 ? Number(d27) : null,
                null, networkPosition is { } n27 ? Number(n27) : null,
                imagingPosition is { } m27 ? Number(m27) : null,
                calibrationSessionPosition is { } s27 ? Number(s27) : null,
                calibrationEventPosition is { } e27 ? Number(e27) : null,
                calibrationManifestPosition is { } f27 ? Number(f27) : null,
                governancePosition is { } g27 ? Number(g27) : null,
                releasePosition is { } q27 ? Number(q27) : null,
                plcResultContractPosition is { } c27 ? Number(c27) : null,
                activationPosition is { } x27 ? Number(x27) : null,
                previewPosition is { } y27 ? Number(y27) : null,
                calibrationImportPosition is { } z27 ? Number(z27) : null,
                manualInspectionPosition is { } u27 ? Number(u27) : null,
                productionAdmissionPosition is { } v27 ? Number(v27) : null,
                stationQualificationPosition is { } w27 ? Number(w27) : null,
                recipeTransferPosition is { } t27 ? Number(t27) : null,
                traceStoragePolicyPosition is { } l27 ? Number(l27) : null,
                qualificationCyclePosition is { } k27 ? Number(k27) : null,
                plcCommunicationPosition is { } h27 ? Number(h27) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= QualificationCycleStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p26 ? Number(p26) : null,
                identityPosition is { } i26 ? Number(i26) : null,
                alarmPosition is { } a26 ? Number(a26) : null,
                resultPosition is { } r26 ? Number(r26) : null,
                draftPosition is { } d26 ? Number(d26) : null,
                null, networkPosition is { } n26 ? Number(n26) : null,
                imagingPosition is { } m26 ? Number(m26) : null,
                calibrationSessionPosition is { } s26 ? Number(s26) : null,
                calibrationEventPosition is { } e26 ? Number(e26) : null,
                calibrationManifestPosition is { } f26 ? Number(f26) : null,
                governancePosition is { } g26 ? Number(g26) : null,
                releasePosition is { } q26 ? Number(q26) : null,
                plcResultContractPosition is { } c26 ? Number(c26) : null,
                activationPosition is { } x26 ? Number(x26) : null,
                previewPosition is { } y26 ? Number(y26) : null,
                calibrationImportPosition is { } z26 ? Number(z26) : null,
                manualInspectionPosition is { } u26 ? Number(u26) : null,
                productionAdmissionPosition is { } v26 ? Number(v26) : null,
                stationQualificationPosition is { } w26 ? Number(w26) : null,
                recipeTransferPosition is { } t26 ? Number(t26) : null,
                traceStoragePolicyPosition is { } l26 ? Number(l26) : null,
                qualificationCyclePosition is { } k26 ? Number(k26) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p25 ? Number(p25) : null,
                identityPosition is { } i25 ? Number(i25) : null,
                alarmPosition is { } a25 ? Number(a25) : null,
                resultPosition is { } r25 ? Number(r25) : null,
                draftPosition is { } d25 ? Number(d25) : null,
                null, networkPosition is { } n25 ? Number(n25) : null,
                imagingPosition is { } m25 ? Number(m25) : null,
                calibrationSessionPosition is { } s25 ? Number(s25) : null,
                calibrationEventPosition is { } e25 ? Number(e25) : null,
                calibrationManifestPosition is { } f25 ? Number(f25) : null,
                governancePosition is { } g25 ? Number(g25) : null,
                releasePosition is { } q25 ? Number(q25) : null,
                plcResultContractPosition is { } c25 ? Number(c25) : null,
                activationPosition is { } x25 ? Number(x25) : null,
                previewPosition is { } y25 ? Number(y25) : null,
                calibrationImportPosition is { } z25 ? Number(z25) : null,
                manualInspectionPosition is { } u25 ? Number(u25) : null,
                productionAdmissionPosition is { } v25 ? Number(v25) : null,
                stationQualificationPosition is { } w25 ? Number(w25) : null,
                recipeTransferPosition is { } t25 ? Number(t25) : null,
                traceStoragePolicyPosition is { } l25 ? Number(l25) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= RecipeTransferStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p24 ? Number(p24) : null,
                identityPosition is { } i24 ? Number(i24) : null,
                alarmPosition is { } a24 ? Number(a24) : null,
                resultPosition is { } r24 ? Number(r24) : null,
                draftPosition is { } d24 ? Number(d24) : null,
                null, networkPosition is { } n24 ? Number(n24) : null,
                imagingPosition is { } m24 ? Number(m24) : null,
                calibrationSessionPosition is { } s24 ? Number(s24) : null,
                calibrationEventPosition is { } e24 ? Number(e24) : null,
                calibrationManifestPosition is { } f24 ? Number(f24) : null,
                governancePosition is { } g24 ? Number(g24) : null,
                releasePosition is { } q24 ? Number(q24) : null,
                plcResultContractPosition is { } c24 ? Number(c24) : null,
                activationPosition is { } x24 ? Number(x24) : null,
                previewPosition is { } y24 ? Number(y24) : null,
                calibrationImportPosition is { } z24 ? Number(z24) : null,
                manualInspectionPosition is { } u24 ? Number(u24) : null,
                productionAdmissionPosition is { } v24 ? Number(v24) : null,
                stationQualificationPosition is { } w24 ? Number(w24) : null,
                recipeTransferPosition is { } t24 ? Number(t24) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= StationQualificationStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p23 ? Number(p23) : null,
                identityPosition is { } i23 ? Number(i23) : null,
                alarmPosition is { } a23 ? Number(a23) : null,
                resultPosition is { } r23 ? Number(r23) : null,
                draftPosition is { } d23 ? Number(d23) : null,
                null, networkPosition is { } n23 ? Number(n23) : null,
                imagingPosition is { } m23 ? Number(m23) : null,
                calibrationSessionPosition is { } s23 ? Number(s23) : null,
                calibrationEventPosition is { } e23 ? Number(e23) : null,
                calibrationManifestPosition is { } f23 ? Number(f23) : null,
                governancePosition is { } g23 ? Number(g23) : null,
                releasePosition is { } q23 ? Number(q23) : null,
                plcResultContractPosition is { } c23 ? Number(c23) : null,
                activationPosition is { } x23 ? Number(x23) : null,
                previewPosition is { } y23 ? Number(y23) : null,
                calibrationImportPosition is { } z23 ? Number(z23) : null,
                manualInspectionPosition is { } u23 ? Number(u23) : null,
                productionAdmissionPosition is { } v23 ? Number(v23) : null,
                stationQualificationPosition is { } w23 ? Number(w23) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= ProductionAdmissionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p22 ? Number(p22) : null,
                identityPosition is { } i22 ? Number(i22) : null,
                alarmPosition is { } a22 ? Number(a22) : null,
                resultPosition is { } r22 ? Number(r22) : null,
                draftPosition is { } d22 ? Number(d22) : null,
                null, networkPosition is { } n22 ? Number(n22) : null,
                imagingPosition is { } m22 ? Number(m22) : null,
                calibrationSessionPosition is { } s22 ? Number(s22) : null,
                calibrationEventPosition is { } e22 ? Number(e22) : null,
                calibrationManifestPosition is { } f22 ? Number(f22) : null,
                governancePosition is { } g22 ? Number(g22) : null,
                releasePosition is { } q22 ? Number(q22) : null,
                plcResultContractPosition is { } c22 ? Number(c22) : null,
                activationPosition is { } x22 ? Number(x22) : null,
                previewPosition is { } y22 ? Number(y22) : null,
                calibrationImportPosition is { } z22 ? Number(z22) : null,
                manualInspectionPosition is { } u22 ? Number(u22) : null,
                productionAdmissionPosition is { } v22 ? Number(v22) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= ManualInspectionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p21 ? Number(p21) : null,
                identityPosition is { } i21 ? Number(i21) : null,
                alarmPosition is { } a21 ? Number(a21) : null,
                resultPosition is { } r21 ? Number(r21) : null,
                draftPosition is { } d21 ? Number(d21) : null,
                null, networkPosition is { } n21 ? Number(n21) : null,
                imagingPosition is { } m21 ? Number(m21) : null,
                calibrationSessionPosition is { } s21 ? Number(s21) : null,
                calibrationEventPosition is { } e21 ? Number(e21) : null,
                calibrationManifestPosition is { } f21 ? Number(f21) : null,
                governancePosition is { } g21 ? Number(g21) : null,
                releasePosition is { } q21 ? Number(q21) : null,
                plcResultContractPosition is { } c21 ? Number(c21) : null,
                activationPosition is { } x21 ? Number(x21) : null,
                previewPosition is { } y21 ? Number(y21) : null,
                calibrationImportPosition is { } z21 ? Number(z21) : null,
                manualInspectionPosition is { } u21 ? Number(u21) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= CalibrationImportStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p20 ? Number(p20) : null,
                identityPosition is { } i20 ? Number(i20) : null,
                alarmPosition is { } a20 ? Number(a20) : null,
                resultPosition is { } r20 ? Number(r20) : null,
                draftPosition is { } d20 ? Number(d20) : null,
                null, networkPosition is { } n20 ? Number(n20) : null,
                imagingPosition is { } m20 ? Number(m20) : null,
                calibrationSessionPosition is { } s20 ? Number(s20) : null,
                calibrationEventPosition is { } e20 ? Number(e20) : null,
                calibrationManifestPosition is { } f20 ? Number(f20) : null,
                governancePosition is { } g20 ? Number(g20) : null,
                releasePosition is { } q20 ? Number(q20) : null,
                plcResultContractPosition is { } c20 ? Number(c20) : null,
                activationPosition is { } x20 ? Number(x20) : null,
                previewPosition is { } y20 ? Number(y20) : null,
                calibrationImportPosition is { } z20 ? Number(z20) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= PreviewSessionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p19 ? Number(p19) : null,
                identityPosition is { } i19 ? Number(i19) : null,
                alarmPosition is { } a19 ? Number(a19) : null,
                resultPosition is { } r19 ? Number(r19) : null,
                draftPosition is { } d19 ? Number(d19) : null,
                null, networkPosition is { } n19 ? Number(n19) : null,
                imagingPosition is { } m19 ? Number(m19) : null,
                calibrationSessionPosition is { } s19 ? Number(s19) : null,
                calibrationEventPosition is { } e19 ? Number(e19) : null,
                calibrationManifestPosition is { } f19 ? Number(f19) : null,
                governancePosition is { } g19 ? Number(g19) : null,
                releasePosition is { } q19 ? Number(q19) : null,
                plcResultContractPosition is { } c19 ? Number(c19) : null,
                activationPosition is { } x19 ? Number(x19) : null,
                previewPosition is { } y19 ? Number(y19) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= RecipeActivationStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p18 ? Number(p18) : null,
                identityPosition is { } i18 ? Number(i18) : null,
                alarmPosition is { } a18 ? Number(a18) : null,
                resultPosition is { } r18 ? Number(r18) : null,
                draftPosition is { } d18 ? Number(d18) : null,
                null, networkPosition is { } n18 ? Number(n18) : null,
                imagingPosition is { } m18 ? Number(m18) : null,
                calibrationSessionPosition is { } s18 ? Number(s18) : null,
                calibrationEventPosition is { } e18 ? Number(e18) : null,
                calibrationManifestPosition is { } f18 ? Number(f18) : null,
                governancePosition is { } g18 ? Number(g18) : null,
                releasePosition is { } q18 ? Number(q18) : null,
                plcResultContractPosition is { } c18 ? Number(c18) : null,
                activationPosition is { } x18 ? Number(x18) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= PlcResultContractStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p17 ? Number(p17) : null,
                identityPosition is { } i17 ? Number(i17) : null,
                alarmPosition is { } a17 ? Number(a17) : null,
                resultPosition is { } r17 ? Number(r17) : null,
                draftPosition is { } d17 ? Number(d17) : null,
                null, networkPosition is { } n17 ? Number(n17) : null,
                imagingPosition is { } m17 ? Number(m17) : null,
                calibrationSessionPosition is { } s17 ? Number(s17) : null,
                calibrationEventPosition is { } e17 ? Number(e17) : null,
                calibrationManifestPosition is { } f17 ? Number(f17) : null,
                governancePosition is { } g17 ? Number(g17) : null,
                releasePosition is { } q17 ? Number(q17) : null,
                plcResultContractPosition is { } c17 ? Number(c17) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p16 ? Number(p16) : null,
                identityPosition is { } i16 ? Number(i16) : null,
                alarmPosition is { } a16 ? Number(a16) : null,
                resultPosition is { } r16 ? Number(r16) : null,
                draftPosition is { } d16 ? Number(d16) : null,
                null, networkPosition is { } n16 ? Number(n16) : null,
                imagingPosition is { } m16 ? Number(m16) : null,
                calibrationSessionPosition is { } s16 ? Number(s16) : null,
                calibrationEventPosition is { } e16 ? Number(e16) : null,
                calibrationManifestPosition is { } f16 ? Number(f16) : null,
                governancePosition is { } g16 ? Number(g16) : null,
                releasePosition is { } q16 ? Number(q16) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p15 ? Number(p15) : null,
                identityPosition is { } i15 ? Number(i15) : null,
                alarmPosition is { } a15 ? Number(a15) : null,
                resultPosition is { } r15 ? Number(r15) : null,
                draftPosition is { } d15 ? Number(d15) : null,
                null, networkPosition is { } n15 ? Number(n15) : null,
                imagingPosition is { } m15 ? Number(m15) : null,
                calibrationSessionPosition is { } s15 ? Number(s15) : null,
                calibrationEventPosition is { } e15 ? Number(e15) : null,
                calibrationManifestPosition is { } f15 ? Number(f15) : null,
                governancePosition is { } g15 ? Number(g15) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= CalibrationSessionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,FactPosition,IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                Number(sequence), kind, position is { } p14 ? Number(p14) : null,
                identityPosition is { } i14 ? Number(i14) : null,
                alarmPosition is { } a14 ? Number(a14) : null,
                resultPosition is { } r14 ? Number(r14) : null,
                draftPosition is { } d14 ? Number(d14) : null,
                null, networkPosition is { } n14 ? Number(n14) : null,
                imagingPosition is { } m14 ? Number(m14) : null,
                calibrationSessionPosition is { } s14 ? Number(s14) : null,
                calibrationEventPosition is { } e14 ? Number(e14) : null,
                calibrationManifestPosition is { } f14 ? Number(f14) : null,
                Convert.ToBase64String(payload), previousHash, hash);
        else if (schemaVersion >= ImagingSetupStoreOptions.SchemaVersion)
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
        return sequence;
    }

    internal static long AppendIdentity(sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key,
        IdentityAuditEvent fact, StoreDeadline deadline,
        CameraNetworkAuditWriteMode cameraNetworkMode = CameraNetworkAuditWriteMode.Generic,
        long? manualInspectionReserveOverride = null,
        long? productionAdmissionReserveOverride = null,
        long? stationQualificationReserveOverride = null,
        long? partIdentityReserveOverride = null)
    {
        var schemaVersion = checked((int)Scalar(db, "PRAGMA user_version;", deadline));
        Require(schemaVersion is 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29, "AuditSchemaInvalid");
        var next = NextSequence(db, policy, deadline, archiveData: false,
            cameraNetworkMode: cameraNetworkMode, manualInspectionReserveOverride: manualInspectionReserveOverride,
            productionAdmissionReserveOverride: productionAdmissionReserveOverride,
            stationQualificationReserveOverride: stationQualificationReserveOverride,
            partIdentityReserveOverride: partIdentityReserveOverride);
        var sequence = next.Sequence;
        var ordinal = checked(Scalar(db, "SELECT COALESCE(MAX(IdentityPosition),0) FROM audit_entries;", deadline) + 1);
        var payload = fact.Encode(ordinal, schemaVersion);
        IdentityAuditEvent.VerifyPayload(payload, ordinal, policy.StationId, schemaVersion);
        var hash = schemaVersion >= PartIdentityStoreOptions.SchemaVersion
            ? EntryHashV25(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, payload)
            : schemaVersion >= QualificationCycleStoreOptions.SchemaVersion
            ? EntryHashV22(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, payload)
            : schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion
            ? EntryHashV21(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null, null, null, payload)
            : schemaVersion >= StationQualificationStoreOptions.SchemaVersion
            ? EntryHashV19(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, null, null, payload)
            : schemaVersion >= ProductionAdmissionStoreOptions.SchemaVersion
            ? EntryHashV18(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, payload)
            : schemaVersion >= ManualInspectionStoreOptions.SchemaVersion
            ? EntryHashV17(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, null, payload)
            : schemaVersion >= CalibrationImportStoreOptions.SchemaVersion
            ? EntryHashV16(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, payload)
            : schemaVersion >= PreviewSessionStoreOptions.SchemaVersion
            ? EntryHashV15(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, payload)
            : schemaVersion >= RecipeActivationStoreOptions.SchemaVersion
            ? EntryHashV14(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null, null, null,
                payload)
            : schemaVersion >= PlcResultContractStoreOptions.SchemaVersion
            ? EntryHashV13(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null, null, payload)
            : schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion
            ? EntryHashV12(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, null, payload)
            : schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion
            ? EntryHashV11(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent",
                null, Number(ordinal), null, null, null, null, null, null, null, null, null, null, payload)
            : EntryHash(schemaVersion, policy.StationId, sequence, next.PreviousHash, "IdentityEvent", null,
                Number(ordinal), null, null, payload);
        if (schemaVersion >= PartIdentityStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), Convert.ToBase64String(payload),
                next.PreviousHash, hash);
        else if (schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null, null, null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= RecipeTransferStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null, null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= StationQualificationStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= ProductionAdmissionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= ManualInspectionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= CalibrationImportStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= PreviewSessionStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= RecipeActivationStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= PlcResultContractStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,ReleasePosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null, null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else if (schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion)
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,GovernancePosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), null,
                Convert.ToBase64String(payload), next.PreviousHash, hash);
        else
            Execute(db, "INSERT INTO audit_entries(Sequence,Kind,IdentityPosition,Payload,PreviousHash,Hash) VALUES(?,?,?,?,?,?);",
                deadline, Number(sequence), "IdentityEvent", Number(ordinal), Convert.ToBase64String(payload),
                next.PreviousHash, hash);
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
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
            ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
        Require(schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
        Require(schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
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
        Require(schemaVersion is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
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
        Require(schemaVersion is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
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

    internal static long AppendCalibrationStoreActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, CalibrationSessionStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationStoreActivated';",
            deadline) == 0, "CalibrationActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "CalibrationStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    /// <summary>
    /// Appends the signed envelope for one schema-14 calibration ledger row.  The
    /// row is written by the caller in the same SQLite transaction; the position
    /// column makes the relationship one-to-one and lets full verification reject
    /// a missing, duplicated or reordered ledger record.
    /// </summary>
    internal static long AppendCalibrationLedgerEntry(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, string kind, long position, byte[] payload, StoreDeadline deadline)
    {
        Require(Scalar(db, "PRAGMA user_version;", deadline) is CalibrationSessionStoreOptions.SchemaVersion or
            CalibrationGovernanceStoreOptions.SchemaVersion or RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        Require(kind is "CalibrationSessionHeader" or "CalibrationSessionEvent" or "CalibrationFrameManifest" &&
            position > 0 && payload.Length is > 0 and <= CalibrationSessionStoreOptions.SqliteValueLimitBytes &&
            Convert.ToBase64String(payload).Length <= CalibrationSessionStorageCodec.MaximumEncodedChars,
            "CalibrationAuditPayloadInvalid");
        var sequence = AppendEntry(db, policy, kind, null, payload, deadline,
            calibrationSessionPosition: kind == "CalibrationSessionHeader" ? position : null,
            calibrationEventPosition: kind == "CalibrationSessionEvent" ? position : null,
            calibrationManifestPosition: kind == "CalibrationFrameManifest" ? position : null);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendCalibrationGovernanceStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, CalibrationGovernanceStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationGovernanceStoreActivated';",
            deadline) == 0, "CalibrationGovernanceActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "CalibrationGovernanceStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendCalibrationGovernanceLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is CalibrationGovernanceStoreOptions.SchemaVersion or
            RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(position > 0 && payload.Length is > 0 and <= CalibrationGovernanceStoreOptions.SqliteValueLimitBytes &&
            Convert.ToBase64String(payload).Length <= CalibrationGovernanceStoreOptions.SqliteValueLimitBytes * 2,
            "CalibrationGovernanceAuditPayloadInvalid");
        var sequence = AppendEntry(db, policy, SqliteCommandStore.CalibrationGovernanceEventKind, null, payload, deadline,
            governancePosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendRecipeReleaseStoreActivation(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, RecipeReleaseStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeReleaseStoreActivated';",
            deadline) == 0, "RecipeReleaseActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "RecipeReleaseStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendRecipeReleaseLedgerEntry(sqlite3 db, AuditIntegrityPolicy policy,
        IAuditSigningKey key, long position, byte[] payload, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(position > 0 && payload.Length is > 0 and <= RecipeReleaseStoreOptions.SqliteValueLimitBytes &&
            Convert.ToBase64String(payload).Length <= RecipeReleaseStoreOptions.SqliteValueLimitBytes * 2,
            "RecipeReleaseAuditPayloadInvalid");
        var sequence = AppendEntry(db, policy, SqliteCommandStore.RecipeReleaseEventKind, null, payload, deadline,
            releasePosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendPlcResultContractStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, PlcResultContractStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='PlcResultContractStoreActivated';",
            deadline) == 0, "PlcResultContractActivationConflict");
        options.Validate();
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "PlcResultContractStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendPlcResultContractLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        PlcResultContractStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= PlcResultContractStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= PlcResultContractStoreOptions.SqliteValueLimitBytes * 2,
            "PlcResultContractAuditPayloadInvalid");
        var sequence = AppendEntry(db, policy, "PlcResultContractEvent", null, payload, deadline,
            plcResultContractPosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendRecipeActivationStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, RecipeActivationStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeActivationStoreActivated';",
            deadline) == 0, "RecipeActivationActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, SqliteCommandStore.RecipeActivationStoreActivatedKind, null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendRecipeActivationLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        RecipeActivationStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= RecipeActivationStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= RecipeActivationStoreOptions.SqliteValueLimitBytes * 2,
            "RecipeActivationAuditPayloadInvalid");
        var sequence = AppendEntry(db, policy, SqliteCommandStore.RecipeActivationEventKind, null, payload, deadline,
            activationPosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendPreviewSessionStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, PreviewSessionStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == PreviewSessionStoreOptions.SchemaVersion ||
            schemaVersion == CalibrationImportStoreOptions.SchemaVersion || schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
            (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion), "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='PreviewSessionStoreActivated';",
            deadline) == 0, "PreviewSessionActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, SqliteCommandStore.PreviewSessionStoreActivatedKind, null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendPreviewSessionLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        PreviewSessionStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == PreviewSessionStoreOptions.SchemaVersion ||
            schemaVersion == CalibrationImportStoreOptions.SchemaVersion || schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
            (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion), "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= PreviewSessionStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= PreviewSessionStoreOptions.SqliteValueLimitBytes * 2,
            "PreviewSessionAuditPayloadInvalid");
        var sequence = AppendEntry(db, policy, SqliteCommandStore.PreviewSessionEventKind, null, payload, deadline,
            previewPosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendCalibrationImportStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, CalibrationImportStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == CalibrationImportStoreOptions.SchemaVersion || schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
            (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion), "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationImportStoreActivated';",
            deadline) == 0, "CalibrationImportActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "CalibrationImportStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendCalibrationImportLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == CalibrationImportStoreOptions.SchemaVersion || schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
            (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion), "AuditSchemaInvalid");
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= CalibrationImportStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= CalibrationImportStoreOptions.SqliteValueLimitBytes * 2,
            "CalibrationImportAuditPayloadInvalid");
        if (TableExists(db, "calibration_import_events", deadline))
            Require(position == Scalar(db, "SELECT COALESCE(MAX(Position),0)+1 FROM calibration_import_events;", deadline),
                "CalibrationImportPositionGap");
        var sequence = AppendEntry(db, policy, "CalibrationImportEvent", null, payload, deadline,
            calibrationImportPosition: position);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendManualInspectionStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, ManualInspectionStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
            (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion), "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ManualInspectionStoreActivated';",
            deadline) == 0, "ManualInspectionActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "ManualInspectionStoreActivated", null, payload, deadline,
            manualInspectionPosition: null);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendManualInspectionLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == ManualInspectionStoreOptions.SchemaVersion ||
            (schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion), "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= ManualInspectionStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= ManualInspectionStoreOptions.SqliteValueLimitBytes * 2,
            "ManualInspectionAuditPayloadInvalid");
        if (TableExists(db, "manual_inspection_events", deadline))
            Require(position == Scalar(db, "SELECT COALESCE(MAX(Position),0)+1 FROM manual_inspection_events;", deadline),
                "ManualInspectionPositionGap");
        var sequence = AppendEntry(db, policy, "ManualInspectionEvent", null, payload, deadline,
            manualInspectionPosition: position, manualInspectionReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendProductionAdmissionStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, ProductionAdmissionStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require((schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion),
            "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionAdmissionStoreActivated';",
            deadline) == 0, "ProductionAdmissionActivationConflict");
        Require(Scalar(db, "SELECT COUNT(*) FROM production_admission_events;", deadline) == 0,
            "ProductionAdmissionActivationConflict");
        var payload = options.EncodeActivationPayload();
        AppendEntry(db, policy, "ProductionAdmissionStoreActivated", null, payload, deadline);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendProductionAdmissionLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        ProductionAdmissionStoreOptions options, StoreDeadline deadline,
        long? productionAdmissionReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require((schemaVersion is ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion),
            "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= ProductionAdmissionStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= ProductionAdmissionStoreOptions.SqliteValueLimitBytes * 2,
            "ProductionAdmissionAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(Position),0)+1 FROM production_admission_events;", deadline),
            "ProductionAdmissionPositionGap");
        var sequence = AppendEntry(db, policy, "ProductionAdmissionEvent", null, payload, deadline,
            productionAdmissionPosition: position,
            productionAdmissionReserveOverride: productionAdmissionReserveOverride);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendStationQualificationStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, StationQualificationStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='StationQualificationStoreActivated';",
            deadline) == 0, "StationQualificationActivationConflict");
        Require(Scalar(db, "SELECT COUNT(*) FROM station_qualification_events;", deadline) == 0,
            "StationQualificationActivationConflict");
        AppendEntry(db, policy, "StationQualificationStoreActivated", null,
            options.EncodeActivationPayload(), deadline,
            stationQualificationReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static long AppendStationQualificationLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        StationQualificationStoreOptions options, StoreDeadline deadline,
        long? stationQualificationReserveOverride = null,
        long? qualificationCycleReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion,
            "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= StationQualificationStoreOptions.MaximumPayloadBytesHardLimit &&
            Convert.ToBase64String(payload).Length <= StationQualificationStoreOptions.SqliteValueLimitBytes * 2,
            "StationQualificationAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(Position),0)+1 FROM station_qualification_events;", deadline),
            "StationQualificationPositionGap");
        var sequence = AppendEntry(db, policy, SqliteCommandStore.StationQualificationEventKind, null, payload, deadline,
            stationQualificationPosition: position,
            stationQualificationReserveOverride: stationQualificationReserveOverride,
            qualificationCycleReserveOverride: qualificationCycleReserveOverride);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return sequence;
    }

    internal static long AppendRecipeTransferStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, RecipeTransferStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeTransferStoreActivated';",
            deadline) == 0, "RecipeTransferActivationConflict");
        AppendEntry(db, policy, "RecipeTransferStoreActivated", null,
            options.EncodeActivationPayload(), deadline, recipeTransferPosition: null,
            stationQualificationReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static (long Sequence, string Hash) AppendRecipeTransferLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        RecipeTransferStoreOptions options, StoreDeadline deadline,
        long? recipeTransferReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } && payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= RecipeTransferStoreOptions.MaximumPayloadBytesHardLimit,
            "RecipeTransferAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(Position),0)+1 FROM recipe_transfer_events;", deadline),
            "RecipeTransferPositionGap");
        var sequence = AppendEntry(db, policy, "RecipeTransferEvent", null, payload, deadline,
            recipeTransferPosition: position,
            stationQualificationReserveOverride: null);
        var tail = Tail(db, deadline);
        Require(tail.Sequence == sequence, "RecipeTransferAuditMismatch");
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    internal static long AppendTraceStoragePolicyStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, TraceStoragePolicyStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='TraceStoragePolicyStoreActivated';",
            deadline) == 0, "TraceStoragePolicyActivationConflict");
        AppendEntry(db, policy, "TraceStoragePolicyStoreActivated", null,
            options.EncodeActivationPayload(), deadline, traceStoragePolicyReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static (long Sequence, string Hash) AppendTraceStoragePolicyLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        TraceStoragePolicyStoreOptions options, StoreDeadline deadline,
        long? traceStoragePolicyReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= TraceStoragePolicyStoreOptions.MaximumPayloadBytesHardLimit,
            "TraceStoragePolicyAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(Position),0)+1 FROM trace_storage_policy_events;", deadline),
            "TraceStoragePolicyPositionGap");
        var sequence = AppendEntry(db, policy, "TraceStoragePolicyEvent", null, payload, deadline,
            traceStoragePolicyPosition: position,
            traceStoragePolicyReserveOverride: traceStoragePolicyReserveOverride);
        var tail = Tail(db, deadline);
        Require(tail.Sequence == sequence, "TraceStoragePolicyAuditMismatch");
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    internal static long AppendQualificationCycleStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, QualificationCycleStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='QualificationCycleStoreActivated';",
            deadline) == 0, "QualificationCycleActivationConflict");
        AppendEntry(db, policy, "QualificationCycleStoreActivated", null,
            options.EncodeActivationPayload(), deadline, qualificationCycleReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static (long Sequence, string Hash) AppendQualificationCycleLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        QualificationCycleStoreOptions options, StoreDeadline deadline,
        long? qualificationCycleReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= QualificationCycleStoreOptions.MaximumPayloadBytesHardLimit,
            "QualificationCycleAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(Position),0)+1 FROM qualification_cycle_events;", deadline),
            "QualificationCyclePositionGap");
        var sequence = AppendEntry(db, policy, "QualificationCycleEvent", null, payload, deadline,
            qualificationCyclePosition: position,
            qualificationCycleReserveOverride: qualificationCycleReserveOverride);
        var tail = Tail(db, deadline);
        Require(tail.Sequence == sequence, "QualificationCycleAuditMismatch");
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    internal static long AppendPlcCommunicationStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key,
        PlcCommunicationStoreOptions options, StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='PlcCommunicationStoreActivated';",
            deadline) == 0, "PlcCommunicationActivationConflict");
        AppendEntry(db, policy, "PlcCommunicationStoreActivated", null,
            options.EncodeActivationPayload(), deadline, plcCommunicationReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static (long Sequence, string Hash) AppendPlcCommunicationLedgerEntry(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, long position, byte[] payload,
        PlcCommunicationStoreOptions options, StoreDeadline deadline,
        long? plcCommunicationReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= PlcCommunicationStoreOptions.MaximumPayloadBytesHardLimit,
            "PlcCommunicationAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(PlcCommunicationPosition),0)+1 FROM audit_entries;", deadline),
            "PlcCommunicationPositionGap");
        var sequence = AppendEntry(db, policy, "PlcCommunicationEvent", null, payload, deadline,
            plcCommunicationPosition: position,
            plcCommunicationReserveOverride: plcCommunicationReserveOverride);
        var tail = Tail(db, deadline);
        Require(tail.Sequence == sequence, "PlcCommunicationAuditMismatch");
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    internal static long AppendProductionInspectionStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, ProductionInspectionStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionInspectionStoreActivated';",
            deadline) == 0, "ProductionInspectionActivationConflict");
        AppendEntry(db, policy, "ProductionInspectionStoreActivated", null,
            options.EncodeActivationPayload(), deadline, productionInspectionReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static (long Sequence, string Hash) AppendProductionInspectionLedgerEntry(
        sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key, long position,
        byte[] payload, ProductionInspectionStoreOptions options, StoreDeadline deadline,
        long? productionInspectionReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is ProductionInspectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= ProductionInspectionStoreOptions.MaximumPayloadBytesHardLimit,
            "ProductionInspectionAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(ProductionInspectionPosition),0)+1 FROM audit_entries;", deadline),
            "ProductionInspectionPositionGap");
        var sequence = AppendEntry(db, policy, "ProductionInspectionEvent", null, payload, deadline,
            productionInspectionPosition: position,
            productionInspectionReserveOverride: productionInspectionReserveOverride);
        var tail = Tail(db, deadline);
        Require(tail.Sequence == sequence, "ProductionInspectionAuditMismatch");
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return (sequence, tail.Hash);
    }

    internal static long AppendPartIdentityStoreActivation(sqlite3 db,
        AuditIntegrityPolicy policy, IAuditSigningKey key, PartIdentityStoreOptions options,
        StoreDeadline deadline)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='PartIdentityStoreActivated';",
            deadline) == 0, "PartIdentityActivationConflict");
        AppendEntry(db, policy, "PartIdentityStoreActivated", null,
            options.EncodeActivationPayload(), deadline, partIdentityReserveOverride: 0);
        var tail = Tail(db, deadline);
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return tail.Sequence;
    }

    internal static (long Sequence, string Hash) AppendPartIdentityLedgerEntry(
        sqlite3 db, AuditIntegrityPolicy policy, IAuditSigningKey key, long position,
        byte[] payload, PartIdentityStoreOptions options, StoreDeadline deadline,
        long? partIdentityReserveOverride = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion == PartIdentityStoreOptions.SchemaVersion, "AuditSchemaInvalid");
        options.Validate();
        Require(position > 0 && payload is { Length: > 0 } &&
            payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= PartIdentityStoreOptions.MaximumPayloadBytesHardLimit,
            "PartIdentityAuditPayloadInvalid");
        Require(position == Scalar(db,
            "SELECT COALESCE(MAX(PartIdentityPosition),0)+1 FROM audit_entries;", deadline),
            "PartIdentityPositionGap");
        var sequence = AppendEntry(db, policy, "PartIdentityEvent", null, payload, deadline,
            partIdentityPosition: position, partIdentityReserveOverride: partIdentityReserveOverride);
        var tail = Tail(db, deadline);
        Require(tail.Sequence == sequence, "PartIdentityAuditMismatch");
        if (tail.Sequence - Scalar(db, "SELECT COALESCE(MAX(Sequence),0) FROM audit_checkpoints;", deadline) >=
            policy.CheckpointEveryEntries)
            CreateCheckpoint(db, policy, key, deadline);
        return (sequence, tail.Hash);
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
        ImagingSetupStoreOptions? imagingSetupOptions = null,
         CalibrationSessionStoreOptions? calibrationSessionOptions = null,
         CalibrationGovernanceStoreOptions? governanceOptions = null,
        RecipeReleaseStoreOptions? releaseOptions = null,
        PlcResultContractStoreOptions? contractOptions = null,
        RecipeActivationStoreOptions? activationOptions = null,
        PreviewSessionStoreOptions? previewOptions = null,
         CalibrationImportStoreOptions? importOptions = null,
        ManualInspectionStoreOptions? manualOptions = null,
        ProductionAdmissionStoreOptions? productionAdmissionOptions = null,
        StationQualificationStoreOptions? stationQualificationOptions = null,
         RecipeTransferStoreOptions? recipeTransferOptions = null,
         TraceStoragePolicyStoreOptions? traceStoragePolicyOptions = null,
         QualificationCycleStoreOptions? qualificationCycleOptions = null,
         PlcCommunicationStoreOptions? plcCommunicationOptions = null,
         ProductionInspectionStoreOptions? productionInspectionOptions = null,
         PartIdentityStoreOptions? partIdentityOptions = null)
    {
        var schemaVersion = Scalar(db, "PRAGMA user_version;", deadline);
        Require(schemaVersion is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29, "AuditSchemaInvalid");
        var schema16 = schemaVersion == RecipeReleaseStoreOptions.SchemaVersion;
        var schema17 = schemaVersion == PlcResultContractStoreOptions.SchemaVersion;
        var schema18 = schemaVersion == RecipeActivationStoreOptions.SchemaVersion;
        var schema19 = schemaVersion == PreviewSessionStoreOptions.SchemaVersion;
        var schema20 = schemaVersion == CalibrationImportStoreOptions.SchemaVersion;
        var schema21 = schemaVersion == ManualInspectionStoreOptions.SchemaVersion;
        var schema22 = schemaVersion == ProductionAdmissionStoreOptions.SchemaVersion;
        var schema23 = schemaVersion == StationQualificationStoreOptions.SchemaVersion;
        var schema24 = schemaVersion == RecipeTransferStoreOptions.SchemaVersion;
        var schema25 = schemaVersion == TraceStoragePolicyStoreOptions.SchemaVersion;
        var schema26 = schemaVersion == QualificationCycleStoreOptions.SchemaVersion;
        var schema27 = schemaVersion == PlcCommunicationStoreOptions.SchemaVersion;
        var schema28 = schemaVersion == ProductionInspectionStoreOptions.SchemaVersion;
        var schema29 = schemaVersion == PartIdentityStoreOptions.SchemaVersion;
        if (schema22)
            Require(productionAdmissionOptions is not null, "ProductionAdmissionConfigurationRequired");
        if (schema23)
            Require(stationQualificationOptions is not null, "StationQualificationConfigurationRequired");
        if (schema24)
        {
            Require(recipeTransferOptions is not null, "RecipeTransferConfigurationRequired");
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
        }
        if (schema25)
            Require(traceStoragePolicyOptions is not null, "TraceStoragePolicyConfigurationRequired");
        if (schema26)
        {
            Require(qualificationCycleOptions is not null, "QualificationCycleConfigurationRequired");
            Require(stationQualificationOptions is not null, "StationQualificationConfigurationRequired");
            Require(traceStoragePolicyOptions is not null, "TraceStoragePolicyConfigurationRequired");
        }
        if (schema27)
        {
            Require(plcCommunicationOptions is not null, "PlcCommunicationConfigurationRequired");
        }
        if (schema28)
            Require(productionInspectionOptions is not null, "ProductionInspectionConfigurationRequired");
        if (schema29)
            Require(partIdentityOptions is not null, "PartIdentityConfigurationRequired");
        if (schema17)
        {
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
            Require(releaseOptions is not null, "RecipeReleaseConfigurationRequired");
        }
        if (schema18)
        {
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
            Require(releaseOptions is not null, "RecipeReleaseConfigurationRequired");
            Require(contractOptions is not null, "PlcResultContractConfigurationRequired");
            Require(cameraSetupOptions is not null, "CameraSetupConfigurationRequired");
            Require(activationOptions is not null, "RecipeActivationConfigurationRequired");
        }
        if (schema19)
        {
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
            Require(releaseOptions is not null, "RecipeReleaseConfigurationRequired");
            Require(contractOptions is not null, "PlcResultContractConfigurationRequired");
            Require(cameraSetupOptions is not null, "CameraSetupConfigurationRequired");
            Require(activationOptions is not null, "RecipeActivationConfigurationRequired");
            Require(previewOptions is not null, "PreviewSessionConfigurationRequired");
        }
        if (schema20)
        {
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
            Require(releaseOptions is not null, "RecipeReleaseConfigurationRequired");
            Require(contractOptions is not null, "PlcResultContractConfigurationRequired");
            Require(cameraSetupOptions is not null, "CameraSetupConfigurationRequired");
            Require(activationOptions is not null, "RecipeActivationConfigurationRequired");
            Require(previewOptions is not null, "PreviewSessionConfigurationRequired");
            Require(importOptions is not null, "CalibrationImportConfigurationRequired");
        }
        if (schema21)
        {
            Require(manualOptions is not null, "ManualInspectionConfigurationRequired");
            Require(recipeDraftOptions is not null, "RecipeDraftConfigurationRequired");
            Require(cameraSetupOptions is not null, "CameraSetupConfigurationRequired");
        }
        if (schema16 || schema17 || schema18 || schema19 || schema20 || schema21 || schema22 || schema23 || schema24 || schema25 || schema26 || schema27 || schema28 || schema29)
            RequireReleaseLedgerPresence(db, deadline, archiveOptions is not null,
                cameraSetupOptions is not null, cameraRecoveryOptions is not null,
                cameraNetworkOptions is not null, imagingSetupOptions is not null,
                calibrationSessionOptions is not null, governanceOptions is not null,
                release: (schema25 || schema26 || schema27 || schema28 || schema29) ? releaseOptions is not null : schema24 ? releaseOptions is not null :
                     schema21 || schema22 || schema23 ? releaseOptions is not null : true,
                plcResultContract: (schema25 || schema26 || schema27 || schema28 || schema29) ? contractOptions is not null : schema21 || schema22 || schema23 || schema24 ? contractOptions is not null : schema17 || schema18 || schema19 || schema20,
                activation: (schema25 || schema26 || schema27 || schema28 || schema29) ? activationOptions is not null : schema21 || schema22 || schema23 || schema24 ? activationOptions is not null : schema18 || schema19 || schema20,
                preview: (schema25 || schema26 || schema27 || schema28 || schema29) ? previewOptions is not null : schema21 || schema22 || schema23 || schema24 ? previewOptions is not null : schema19 || schema20,
                calibrationImport: (schema25 || schema26 || schema27 || schema28 || schema29) ? importOptions is not null : schema21 || schema22 || schema23 || schema24 ? importOptions is not null : schema20,
                manualInspection: (schema25 || schema26 || schema27 || schema28 || schema29) ? manualOptions is not null : schema22 || schema23 || schema24 ? manualOptions is not null : schema21,
                productionAdmission: (schema25 || schema26 || schema27 || schema28 || schema29) ? productionAdmissionOptions is not null : schema22 || ((schema23 || schema24) && productionAdmissionOptions is not null),
                 stationQualification: (schema25 || schema26 || schema27 || schema28 || schema29) ? stationQualificationOptions is not null : schema23 || (schema24 && stationQualificationOptions is not null),
                 recipeTransfer: (schema25 || schema26 || schema27 || schema28 || schema29) ? recipeTransferOptions is not null : schema24,
                traceStoragePolicy: schema25 || schema26 || (schema27 && traceStoragePolicyOptions is not null) || (schema28 && traceStoragePolicyOptions is not null) || (schema29 && traceStoragePolicyOptions is not null),
                qualificationCycle: schema26 || (schema27 && qualificationCycleOptions is not null) || (schema28 && qualificationCycleOptions is not null) || (schema29 && qualificationCycleOptions is not null),
                plcCommunication: schema27 || (schema28 && plcCommunicationOptions is not null) || (schema29 && plcCommunicationOptions is not null));
        var hasIdentity = schemaVersion >= 3;
        var hasAlarm = schemaVersion >= 7;
        var modernOptional = schema16 || schema17 || schema18 || schema19 || schema20 || schema21 || schema22 || schema23 || schema24 || schema25 || schema26 || schema27 || schema28 || schema29;
        var hasCamera = modernOptional ? cameraSetupOptions is not null : schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion;
        var hasRecovery = modernOptional ? cameraRecoveryOptions is not null : schemaVersion == CameraRecoveryStoreOptions.SchemaVersion ||
            schemaVersion is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion ||
            (schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) && cameraRecoveryOptions is not null;
        var hasNetwork = modernOptional ? cameraNetworkOptions is not null : schemaVersion == CameraNetworkStoreOptions.SchemaVersion ||
            (schemaVersion is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion) &&
            cameraNetworkOptions is not null;
        var hasImaging = modernOptional ? imagingSetupOptions is not null : schemaVersion is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion;
        var hasCalibration = modernOptional ? calibrationSessionOptions is not null : schemaVersion is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion;
        var hasGovernance = modernOptional ? governanceOptions is not null : schemaVersion == CalibrationGovernanceStoreOptions.SchemaVersion;
        var hasRelease = modernOptional ? releaseOptions is not null : schema16 || schema17 || schema18 || schema19 || schema20;
        var hasContract = modernOptional ? contractOptions is not null : schema17 || schema18 || schema19 || schema20;
        var hasActivation = modernOptional ? activationOptions is not null : schema18 || schema19 || schema20;
        var hasPreview = modernOptional ? previewOptions is not null : schema19 || schema20;
        var hasImport = modernOptional ? importOptions is not null : schema20;
        var hasManual = modernOptional ? manualOptions is not null : schema21;
        var hasProductionAdmission = modernOptional ? productionAdmissionOptions is not null : schema22;
        var hasStationQualification = modernOptional ? stationQualificationOptions is not null : schema23;
        var hasRecipeTransfer = schema24 || ((schema25 || schema26 || schema27 || schema28 || schema29) && recipeTransferOptions is not null);
        var hasTraceStoragePolicy = schema25 || schema26 || ((schema27 || schema28 || schema29) && traceStoragePolicyOptions is not null &&
            TableExists(db, "trace_storage_policy_events", deadline));
        var hasQualificationCycle = schema26 || ((schema27 || schema28 || schema29) && qualificationCycleOptions is not null &&
            TableExists(db, "qualification_cycle_events", deadline));
        var hasPlcCommunication = schema27 || ((schema28 || schema29) && plcCommunicationOptions is not null &&
            TableExists(db, "plc_communication_events", deadline));
        var hasProductionInspection = modernOptional ? productionInspectionOptions is not null : schema28;
        var hasPartIdentity = schema29 && partIdentityOptions is not null &&
            TableExists(db, "part_identity_events", deadline);
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
        if (hasCalibration != (calibrationSessionOptions is not null))
            throw new InvalidOperationException(hasCalibration
                ? "CalibrationConfigurationRequired" : "CalibrationGovernedMigrationRequired");
        if (hasGovernance != (governanceOptions is not null))
            throw new InvalidOperationException(hasGovernance
                ? "CalibrationGovernanceConfigurationRequired" : "CalibrationGovernanceMigrationRequired");
        if (hasRelease != (releaseOptions is not null))
            throw new InvalidOperationException(hasRelease
                ? "RecipeReleaseConfigurationRequired" : "RecipeReleaseMigrationRequired");
        if (hasContract != (contractOptions is not null))
            throw new InvalidOperationException(hasContract
                ? "PlcResultContractConfigurationRequired" : "PlcResultContractMigrationRequired");
        if (hasActivation != (activationOptions is not null))
            throw new InvalidOperationException(hasActivation
                ? "RecipeActivationConfigurationRequired" : "RecipeActivationMigrationRequired");
        if (hasPreview != (previewOptions is not null))
            throw new InvalidOperationException(hasPreview
                ? "PreviewSessionConfigurationRequired" : "PreviewSessionMigrationRequired");
        if (hasImport != (importOptions is not null))
            throw new InvalidOperationException(hasImport
                ? "CalibrationImportConfigurationRequired" : "CalibrationImportMigrationRequired");
        if (hasManual != (manualOptions is not null))
            throw new InvalidOperationException(hasManual
                ? "ManualInspectionConfigurationRequired" : "ManualInspectionMigrationRequired");
        if (hasProductionAdmission != (productionAdmissionOptions is not null))
            throw new InvalidOperationException(hasProductionAdmission
                ? "ProductionAdmissionConfigurationRequired" : "ProductionAdmissionMigrationRequired");
        if (hasStationQualification != (stationQualificationOptions is not null))
            throw new InvalidOperationException(hasStationQualification
                ? "StationQualificationConfigurationRequired" : "StationQualificationMigrationRequired");
        if (hasRecipeTransfer != (recipeTransferOptions is not null))
            throw new InvalidOperationException(hasRecipeTransfer
                ? "RecipeTransferConfigurationRequired" : "RecipeTransferGovernedMigrationRequired");
        if (hasTraceStoragePolicy != (traceStoragePolicyOptions is not null))
            throw new InvalidOperationException(hasTraceStoragePolicy
                ? "TraceStoragePolicyConfigurationRequired" : "TraceStoragePolicyGovernedMigrationRequired");
        if (hasQualificationCycle != (qualificationCycleOptions is not null))
            throw new InvalidOperationException(hasQualificationCycle
                ? "QualificationCycleConfigurationRequired" : "QualificationCycleGovernedMigrationRequired");
        if (hasPlcCommunication != (plcCommunicationOptions is not null))
            throw new InvalidOperationException(hasPlcCommunication
                ? "PlcCommunicationConfigurationRequired" : "PlcCommunicationGovernedMigrationRequired");
        if (hasProductionInspection != (productionInspectionOptions is not null))
            throw new InvalidOperationException(hasProductionInspection
                ? "ProductionInspectionConfigurationRequired" : "ProductionInspectionGovernedMigrationRequired");
        if (hasPartIdentity != (partIdentityOptions is not null))
            throw new InvalidOperationException(hasPartIdentity
                ? "PartIdentityConfigurationRequired" : "PartIdentityGovernedMigrationRequired");
        if (hasGovernance)
        {
            governanceOptions!.Validate();
            SqliteCommandStore.RequireConfiguredCalibrationGovernance(db, governanceOptions, deadline);
        }
        if (hasRelease)
        {
            releaseOptions!.Validate();
            SqliteCommandStore.RequireConfiguredRecipeReleases(db, releaseOptions, deadline);
        }
        if (hasContract)
        {
            contractOptions!.Validate();
            SqliteCommandStore.RequireConfiguredPlcResultContracts(db, contractOptions, deadline);
        }
        if (hasActivation)
        {
            activationOptions!.Validate();
            SqliteCommandStore.RequireConfiguredRecipeActivations(db, activationOptions, deadline);
        }
        if (hasPreview)
        {
            previewOptions!.Validate();
            SqliteCommandStore.RequireConfiguredPreviewSessions(db, previewOptions, deadline);
        }
        if (hasImport)
        {
            importOptions!.Validate();
            SqliteCommandStore.RequireConfiguredCalibrationImports(db, importOptions, deadline);
        }
        if (hasManual)
        {
            manualOptions!.Validate();
            SqliteCommandStore.RequireConfiguredManualInspections(db, manualOptions, deadline);
        }
        if (hasProductionAdmission)
        {
            productionAdmissionOptions!.Validate();
            SqliteCommandStore.RequireConfiguredProductionAdmission(db, productionAdmissionOptions, deadline);
        }
        if (hasStationQualification)
        {
            stationQualificationOptions!.Validate();
            SqliteCommandStore.RequireConfiguredStationQualifications(db, stationQualificationOptions, deadline);
        }
        if (hasRecipeTransfer)
        {
            recipeTransferOptions!.Validate();
            SqliteCommandStore.RequireConfiguredRecipeTransfers(db, recipeTransferOptions, deadline);
        }
        if (hasTraceStoragePolicy)
        {
            traceStoragePolicyOptions!.Validate();
            SqliteCommandStore.RequireConfiguredTraceStoragePolicies(db, traceStoragePolicyOptions, deadline);
        }
        if (hasQualificationCycle)
        {
            qualificationCycleOptions!.Validate();
            SqliteCommandStore.RequireConfiguredQualificationCycles(db, qualificationCycleOptions, deadline);
        }
        if (hasPlcCommunication)
        {
            plcCommunicationOptions!.Validate();
            SqliteCommandStore.RequireConfiguredPlcCommunication(db, plcCommunicationOptions, deadline);
        }
        if (hasProductionInspection)
        {
            productionInspectionOptions!.Validate();
            SqliteCommandStore.RequireConfiguredProductionInspection(db, productionInspectionOptions, deadline);
        }
        if (hasPartIdentity)
        {
            partIdentityOptions!.Validate();
            SqliteCommandStore.RequireConfiguredPartIdentity(db, partIdentityOptions, deadline);
        }
        var hasArchive = modernOptional ? archiveOptions is not null : schemaVersion == AlgorithmResultArchiveOptions.SchemaVersion ||
            ((schemaVersion is RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
                CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
                CalibrationGovernanceStoreOptions.SchemaVersion) &&
                archiveOptions is not null);
        var hasDraft = modernOptional ? recipeDraftOptions is not null : schemaVersion == RecipeDraftStoreOptions.SchemaVersion ||
            ((schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion) &&
                recipeDraftOptions is not null);
        if (schemaVersion == RecipeDraftStoreOptions.SchemaVersion && recipeDraftOptions is null)
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (schema16 && recipeDraftOptions is null)
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if ((schema17 || schema18) && recipeDraftOptions is null && TableExists(db, "recipe_draft_revisions", deadline))
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (archiveOptions is not null && !hasArchive)
            throw new InvalidOperationException("AlgorithmResultArchiveGovernedMigrationRequired");
        if (recipeDraftOptions is null &&
            (schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                  CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                 PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion) &&
            TableExists(db, "recipe_draft_revisions", deadline))
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (archiveOptions is null &&
            (schemaVersion is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                  CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                 PlcResultContractStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion) &&
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
        if (hasCalibration)
        {
            Require(calibrationSessionOptions is not null, "CalibrationConfigurationRequired");
            calibrationSessionOptions!.Validate();
            SqliteCommandStore.RequireConfiguredCalibrationSessions(db, calibrationSessionOptions, deadline);
        }
        if ((schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                 ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
                 CalibrationGovernanceStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion) &&
            cameraRecoveryOptions is null && TableExists(db, "camera_recovery_terminal_events", deadline))
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        if ((schemaVersion is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                 ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or
                 CalibrationGovernanceStoreOptions.SchemaVersion or RecipeActivationStoreOptions.SchemaVersion) &&
            cameraRecoveryOptions is not null && !TableExists(db, "camera_recovery_terminal_events", deadline))
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        if ((schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                 CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                 RecipeActivationStoreOptions.SchemaVersion) &&
            cameraNetworkOptions is null && TableExists(db, "camera_network_events", deadline))
            throw new InvalidOperationException("CameraNetworkConfigurationRequired");
        if ((schemaVersion is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                 CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion or
                 RecipeActivationStoreOptions.SchemaVersion) &&
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
         var genesisPositionColumns = schema29
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,ProductionInspectionPosition,PartIdentityPosition"
             : schema28
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,ProductionInspectionPosition"
             : schema27
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition"
             : schema26
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition"
             : schema25
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition"
             : schema24
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition"
             : schema23
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition"
            : schema22
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition"
            : schema21
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition"
            :
            (hasIdentity ? "IdentityPosition" : "NULL") + "," +
            (hasAlarm ? "AlarmPosition" : "NULL") + "," +
            (hasArchive ? "ResultPosition" : "NULL") + "," +
            (hasDraft ? "DraftPosition" : "NULL") + "," +
            (hasCamera ? "CameraPosition" : "NULL") + "," +
            (hasNetwork ? "NetworkPosition" : "NULL") + "," +
            (hasImaging ? "ImagingPosition" : "NULL") + "," +
            (hasCalibration ? "CalibrationSessionPosition" : "NULL") + "," +
              (hasCalibration ? "CalibrationEventPosition" : "NULL") + "," +
              (hasCalibration ? "CalibrationManifestPosition" : "NULL") + "," +
            (hasGovernance ? "GovernancePosition" : "NULL") + "," +
            (hasRelease ? "ReleasePosition" : "NULL") + "," +
            (hasContract ? "PlcResultContractPosition" : "NULL") + "," +
            (hasActivation ? "ActivationPosition" : "NULL") +
            (hasPreview ? ",PreviewPosition" : string.Empty) +
            (hasImport ? ",CalibrationImportPosition" : string.Empty) +
            (hasManual ? ",ManualInspectionPosition" : string.Empty);
        var genesis = Read(db, "SELECT Kind,Payload,PreviousHash,Hash,FactPosition," +
            genesisPositionColumns + " FROM audit_entries WHERE Sequence=1;", deadline,
                 s => Enumerable.Range(0, schema29 ? 30 : schema28 ? 29 : schema27 ? 28 : schema26 ? 27 : schema25 ? 26 : schema24 ? 25 : schema23 ? 24 : schema22 ? 23 : hasManual ? 22 : hasImport ? 21 : hasPreview ? 20 : hasActivation ? 19 : hasContract ? 18 : hasRelease ? 17 : hasGovernance ? 16 : hasCalibration ? 15 : hasImaging ? 12 : 11)
                  .Select(i => SqliteNative.ColumnText(s, i)).ToArray()).SingleOrDefault();
        Require(genesis is not null && genesis[0] == "SigningKeyCreated" && genesis[2] == AuditCanonical.GenesisHash &&
             ((!schema21 && !schema22 && !schema23 && !schema24 && !schema25 && !schema26 && !schema27) || genesis.Skip(4).All(value => value is null)) &&
            genesis[4] is null && genesis[5] is null && genesis[6] is null && genesis[7] is null && genesis[8] is null &&
            genesis[9] is null && genesis[10] is null && (!hasImaging || genesis[11] is null) &&
              (!hasCalibration || (genesis[12] is null && genesis[13] is null && genesis[14] is null)) &&
              (!hasGovernance || genesis[15] is null) &&
               (!hasRelease || genesis[16] is null) &&
               (!hasContract || genesis[17] is null) &&
                 (!hasActivation || genesis[18] is null) && (!hasPreview || genesis[19] is null) &&
                  (!hasImport || genesis[20] is null) && (!hasManual || genesis[21] is null) &&
                  (!hasProductionAdmission || genesis[22] is null) && (!hasStationQualification || genesis[23] is null) &&
                  (!hasRecipeTransfer || genesis[24] is null) &&
                  (!hasTraceStoragePolicy || genesis[25] is null) && (!hasQualificationCycle || genesis[26] is null) &&
                  (!hasPlcCommunication || genesis[27] is null) &&
                  (!schema28 || genesis[28] is null) && (!schema29 || genesis[29] is null),
                 "AuditGenesisMissingOrInvalid");
           var genesisHash = schema29
               ? EntryHashV25(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                   genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                   genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23], genesis[24], genesis[25], genesis[26], genesis[27], genesis[28], genesis[29],
                   Convert.FromBase64String(genesis[1]!))
               : schema28
               ? EntryHashV24(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                   genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                   genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23], genesis[24], genesis[25], genesis[26], genesis[27], genesis[28],
                   Convert.FromBase64String(genesis[1]!))
               : schema27
               ? EntryHashV23(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                   genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                   genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23], genesis[24], genesis[25], genesis[26], genesis[27],
                   Convert.FromBase64String(genesis[1]!))
               : schema26
               ? EntryHashV22(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                   genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                   genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23], genesis[24], genesis[25], genesis[26],
                   Convert.FromBase64String(genesis[1]!))
               : schema25
               ? EntryHashV21(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                   genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                   genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23], genesis[24], genesis[25],
                   Convert.FromBase64String(genesis[1]!))
               : schema24
               ? EntryHashV20(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                   genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                   genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23], genesis[24],
                   Convert.FromBase64String(genesis[1]!))
               : schema23
               ? EntryHashV19(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                  genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                  genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22], genesis[23],
                  Convert.FromBase64String(genesis[1]!))
              : schema22
              ? EntryHashV18(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                  genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                  genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21], genesis[22],
                  Convert.FromBase64String(genesis[1]!))
              : hasManual
              ? EntryHashV17(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                  genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                  genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20], genesis[21],
                  Convert.FromBase64String(genesis[1]!))
              : hasImport
             ? EntryHashV16(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                 genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                 genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19], genesis[20],
                 Convert.FromBase64String(genesis[1]!))
             : hasPreview
             ? EntryHashV15(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                 genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                 genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18], genesis[19],
                 Convert.FromBase64String(genesis[1]!))
             : hasActivation
             ? EntryHashV14(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                 genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                 genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17], genesis[18],
                 Convert.FromBase64String(genesis[1]!))
             : hasContract
             ? EntryHashV13(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                 genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                 genesis[12], genesis[13], genesis[14], genesis[15], genesis[16], genesis[17],
                 Convert.FromBase64String(genesis[1]!))
             : hasRelease
             ? EntryHashV12(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                 genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                 genesis[12], genesis[13], genesis[14], genesis[15], genesis[16],
                 Convert.FromBase64String(genesis[1]!))
            : hasGovernance
            ? EntryHashV11(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                genesis[12], genesis[13], genesis[14], genesis[15], Convert.FromBase64String(genesis[1]!))
            : hasCalibration
            ? EntryHash(schemaVersion, policy.StationId, 1, AuditCanonical.GenesisHash,
                genesis![0]!, genesis[4], genesis[5], genesis[6], genesis[7], genesis[8], genesis[9], genesis[10], genesis[11],
                genesis[12], genesis[13], genesis[14], Convert.FromBase64String(genesis[1]!))
            : hasImaging
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
        var maxCalibrationSession = hasCalibration ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM calibration_sessions;", deadline) : 0;
        var maxCalibrationEvent = hasCalibration ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM calibration_session_events;", deadline) : 0;
        var maxCalibrationManifest = hasCalibration ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM calibration_frame_manifests;", deadline) : 0;
        var maxGovernance = hasGovernance ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM calibration_governance_events;", deadline) : 0;
        var maxRelease = hasRelease ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM recipe_release_events;", deadline) : 0;
        var maxContract = hasContract ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM plc_result_contract_events;", deadline) : 0;
        var maxActivation = hasActivation ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM recipe_activation_events;", deadline) : 0;
        var maxPreview = hasPreview ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM preview_session_events;", deadline) : 0;
        var maxImport = hasImport ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM calibration_import_events;", deadline) : 0;
        var maxManual = hasManual ? Scalar(db, "SELECT COALESCE(MAX(Position),0) FROM manual_inspection_events;", deadline) : 0;
        var maxProductionAdmission = hasProductionAdmission ? Scalar(db,
            "SELECT COALESCE(MAX(Position),0) FROM production_admission_events;", deadline) : 0;
         var maxStationQualification = hasStationQualification ? Scalar(db,
             "SELECT COALESCE(MAX(Position),0) FROM station_qualification_events;", deadline) : 0;
         var maxRecipeTransfer = hasRecipeTransfer ? Scalar(db,
             "SELECT COALESCE(MAX(Position),0) FROM recipe_transfer_events;", deadline) : 0;
        var maxTraceStoragePolicy = hasTraceStoragePolicy ? Scalar(db,
            "SELECT COALESCE(MAX(Position),0) FROM trace_storage_policy_events;", deadline) : 0;
        var maxPlcCommunication = hasPlcCommunication ? Scalar(db,
            "SELECT COALESCE(MAX(Position),0) FROM plc_communication_events;", deadline) : 0;
        var maxProductionInspection = hasProductionInspection ? Scalar(db,
            "SELECT COALESCE(MAX(Position),0) FROM production_inspection_events;", deadline) : 0;
        var maxPartIdentity = hasPartIdentity ? Scalar(db,
            "SELECT COALESCE(MAX(Position),0) FROM part_identity_events;", deadline) : 0;
        var archiveActivations = hasArchive ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='AlgorithmArchiveActivated';", deadline) : 0;
        var draftActivations = hasDraft ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeDraftStoreActivated';", deadline) : 0;
        var cameraActivations = hasCamera ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraSetupStoreActivated';", deadline) : 0;
        var recoveryActivations = hasRecovery ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraRecoveryStoreActivated';", deadline) : 0;
        var networkActivations = hasNetwork ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraNetworkStoreActivated';", deadline) : 0;
        var imagingActivations = hasImaging ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImagingSetupStoreActivated';", deadline) : 0;
        var calibrationActivations = hasCalibration ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationStoreActivated';", deadline) : 0;
        var governanceActivations = hasGovernance ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationGovernanceStoreActivated';", deadline) : 0;
        var releaseActivations = hasRelease ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeReleaseStoreActivated';", deadline) : 0;
        var contractActivations = hasContract ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='PlcResultContractStoreActivated';", deadline) : 0;
        var activationActivations = hasActivation ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeActivationStoreActivated';", deadline) : 0;
        var previewActivations = hasPreview ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='PreviewSessionStoreActivated';", deadline) : 0;
        var importActivations = hasImport ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationImportStoreActivated';", deadline) : 0;
        var manualActivations = hasManual ? Scalar(db, "SELECT COUNT(*) FROM audit_entries WHERE Kind='ManualInspectionStoreActivated';", deadline) : 0;
        var productionAdmissionActivations = hasProductionAdmission ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionAdmissionStoreActivated';", deadline) : 0;
         var stationQualificationActivations = hasStationQualification ? Scalar(db,
             "SELECT COUNT(*) FROM audit_entries WHERE Kind='StationQualificationStoreActivated';", deadline) : 0;
         var recipeTransferActivations = hasRecipeTransfer ? Scalar(db,
             "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeTransferStoreActivated';", deadline) : 0;
        var traceStoragePolicyActivations = hasTraceStoragePolicy ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='TraceStoragePolicyStoreActivated';", deadline) : 0;
        var qualificationCycleActivations = hasQualificationCycle ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='QualificationCycleStoreActivated';", deadline) : 0;
        var plcCommunicationActivations = hasPlcCommunication ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='PlcCommunicationStoreActivated';", deadline) : 0;
        var productionInspectionActivations = hasProductionInspection ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionInspectionStoreActivated';", deadline) : 0;
        var partIdentityActivations = hasPartIdentity ? Scalar(db,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='PartIdentityStoreActivated';", deadline) : 0;
        var maxQualificationCycle = hasQualificationCycle ? Scalar(db,
            "SELECT COUNT(*) FROM qualification_cycle_events;", deadline) : 0;
        Require(!hasArchive || archiveActivations == 1, "AlgorithmResultArchiveActivationMissing");
        Require(!hasDraft || draftActivations == 1, "RecipeDraftActivationMissing");
        Require(!hasCamera || cameraActivations == 1, "CameraSetupActivationMissing");
        Require(!hasRecovery || recoveryActivations == 1, "CameraRecoveryActivationMissing");
        Require(!hasNetwork || networkActivations == 1, "CameraNetworkActivationMissing");
        Require(!hasImaging || imagingActivations == 1, "ImagingSetupActivationMissing");
        Require(!hasCalibration || calibrationActivations == 1, "CalibrationActivationMissing");
        Require(!hasGovernance || governanceActivations == 1, "CalibrationGovernanceActivationMissing");
        Require(!hasRelease || releaseActivations == 1, "RecipeReleaseActivationMissing");
        Require(!hasContract || contractActivations == 1, "PlcResultContractActivationMissing");
        Require(!hasActivation || activationActivations == 1, "RecipeActivationActivationMissing");
        Require(!hasPreview || previewActivations == 1, "PreviewSessionActivationMissing");
        Require(!hasImport || importActivations == 1, "CalibrationImportActivationMissing");
        Require(!hasManual || manualActivations == 1, "ManualInspectionActivationMissing");
        Require(!hasProductionAdmission || productionAdmissionActivations == 1,
            "ProductionAdmissionActivationMissing");
         Require(!hasStationQualification || stationQualificationActivations == 1,
             "StationQualificationActivationMissing");
         Require(!hasRecipeTransfer || recipeTransferActivations == 1,
             "RecipeTransferActivationMissing");
        Require(!hasTraceStoragePolicy || traceStoragePolicyActivations == 1,
            "TraceStoragePolicyActivationMissing");
        Require(!hasQualificationCycle || qualificationCycleActivations == 1, "QualificationCycleActivationMissing");
        Require(!hasPlcCommunication || plcCommunicationActivations == 1, "PlcCommunicationActivationMissing");
        Require(!hasProductionInspection || productionInspectionActivations == 1,
            "ProductionInspectionActivationMissing");
        Require(!hasPartIdentity || partIdentityActivations == 1,
            "PartIdentityActivationMissing");
        Require(checked(maxFact + maxIdentity + maxAlarm + maxResult + maxDraft + maxCamera +
            maxRecovery + maxNetwork + maxImaging + maxCalibrationSession + maxCalibrationEvent + maxCalibrationManifest +
              maxGovernance + maxRelease + maxContract + maxActivation + maxPreview + maxImport + maxManual +
               maxProductionAdmission + maxStationQualification + maxRecipeTransfer + archiveActivations + draftActivations + cameraActivations + recoveryActivations +
              networkActivations + imagingActivations + calibrationActivations + governanceActivations + releaseActivations +
              contractActivations + activationActivations + previewActivations + importActivations + manualActivations +
               productionAdmissionActivations + stationQualificationActivations + recipeTransferActivations +
               traceStoragePolicyActivations + maxTraceStoragePolicy + qualificationCycleActivations + maxQualificationCycle +
               plcCommunicationActivations + maxPlcCommunication + productionInspectionActivations + maxProductionInspection + partIdentityActivations + maxPartIdentity) == tail.Sequence - 1 && maxFact == Scalar(db,
            "SELECT COALESCE(MAX(FactPosition),0) FROM audit_entries;", deadline) &&
            (!hasAlarm || maxAlarm == Scalar(db, "SELECT COALESCE(MAX(AlarmPosition),0) FROM audit_entries;", deadline)) &&
            (!hasArchive || maxResult == Scalar(db, "SELECT COALESCE(MAX(ResultPosition),0) FROM audit_entries;", deadline)) &&
            (!hasDraft || maxDraft == Scalar(db, "SELECT COALESCE(MAX(DraftPosition),0) FROM audit_entries;", deadline)) &&
            (!hasCamera || maxCamera == Scalar(db, "SELECT COALESCE(MAX(CameraPosition),0) FROM audit_entries;", deadline)) &&
            (!hasNetwork || maxNetwork == Scalar(db, "SELECT COALESCE(MAX(NetworkPosition),0) FROM audit_entries;", deadline)) &&
            (!hasImaging || maxImaging == Scalar(db, "SELECT COALESCE(MAX(ImagingPosition),0) FROM audit_entries;", deadline)) &&
            (!hasCalibration || maxCalibrationSession == Scalar(db, "SELECT COALESCE(MAX(CalibrationSessionPosition),0) FROM audit_entries;", deadline)) &&
            (!hasCalibration || maxCalibrationEvent == Scalar(db, "SELECT COALESCE(MAX(CalibrationEventPosition),0) FROM audit_entries;", deadline)) &&
            (!hasCalibration || maxCalibrationManifest == Scalar(db, "SELECT COALESCE(MAX(CalibrationManifestPosition),0) FROM audit_entries;", deadline)) &&
            (!hasGovernance || maxGovernance == Scalar(db, "SELECT COALESCE(MAX(GovernancePosition),0) FROM audit_entries;", deadline)) &&
            (!hasRelease || maxRelease == Scalar(db, "SELECT COALESCE(MAX(ReleasePosition),0) FROM audit_entries;", deadline)) &&
             (!hasContract || maxContract == Scalar(db, "SELECT COALESCE(MAX(PlcResultContractPosition),0) FROM audit_entries;", deadline)) &&
             (!hasActivation || maxActivation == Scalar(db, "SELECT COALESCE(MAX(ActivationPosition),0) FROM audit_entries;", deadline)) &&
              (!hasPreview || maxPreview == Scalar(db, "SELECT COALESCE(MAX(PreviewPosition),0) FROM audit_entries;", deadline)) &&
              (!hasImport || maxImport == Scalar(db, "SELECT COALESCE(MAX(CalibrationImportPosition),0) FROM audit_entries;", deadline)) &&
              (!hasManual || maxManual == Scalar(db, "SELECT COALESCE(MAX(ManualInspectionPosition),0) FROM audit_entries;", deadline)) &&
            (!hasProductionAdmission || maxProductionAdmission == Scalar(db,
                "SELECT COALESCE(MAX(ProductionAdmissionPosition),0) FROM audit_entries;", deadline)) &&
             (!hasStationQualification || maxStationQualification == Scalar(db,
                 "SELECT COALESCE(MAX(StationQualificationPosition),0) FROM audit_entries;", deadline)) &&
             (!hasRecipeTransfer || maxRecipeTransfer == Scalar(db,
                 "SELECT COALESCE(MAX(RecipeTransferPosition),0) FROM audit_entries;", deadline)) &&
             (!hasTraceStoragePolicy || maxTraceStoragePolicy == Scalar(db,
                 "SELECT COALESCE(MAX(TraceStoragePolicyPosition),0) FROM audit_entries;", deadline)) &&
             (!hasQualificationCycle || maxQualificationCycle == Scalar(db,
                 "SELECT COALESCE(MAX(QualificationCyclePosition),0) FROM audit_entries;", deadline)) &&
             (!hasPlcCommunication || maxPlcCommunication == Scalar(db,
                 "SELECT COALESCE(MAX(PlcCommunicationPosition),0) FROM audit_entries;", deadline)) &&
             (!hasProductionInspection || maxProductionInspection == Scalar(db,
                 "SELECT COALESCE(MAX(ProductionInspectionPosition),0) FROM audit_entries;", deadline)) &&
             (!hasPartIdentity || maxPartIdentity == Scalar(db,
                 "SELECT COALESCE(MAX(PartIdentityPosition),0) FROM audit_entries;", deadline)) &&
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
        var fullCalibrationVerification = hasCalibration && calibrationSessionOptions is not null;
        var fullGovernanceVerification = hasGovernance && governanceOptions is not null;
        var fullReleaseVerification = hasRelease && releaseOptions is not null;
        var fullContractVerification = hasContract && contractOptions is not null;
        var fullActivationVerification = hasActivation && activationOptions is not null;
        var fullPreviewVerification = hasPreview && previewOptions is not null;
         var fullImportVerification = hasImport && importOptions is not null;
         var fullProductionAdmissionVerification = hasProductionAdmission && productionAdmissionOptions is not null;
         var fullRecipeTransferVerification = hasRecipeTransfer && recipeTransferOptions is not null;
        var fullTraceStoragePolicyVerification = hasTraceStoragePolicy && traceStoragePolicyOptions is not null;
        var fullQualificationCycleVerification = hasQualificationCycle && qualificationCycleOptions is not null;
        var fullPlcCommunicationVerification = hasPlcCommunication && plcCommunicationOptions is not null;
        var fullProductionInspectionVerification = hasProductionInspection && productionInspectionOptions is not null;
        var fullPartIdentityVerification = hasPartIdentity && partIdentityOptions is not null;
        var prefixCheckpoint = startup && !fullArchiveVerification && !fullDraftVerification && !fullCameraVerification &&
            !fullRecoveryVerification && !fullNetworkVerification && !fullImagingVerification &&
            !fullCalibrationVerification && !fullGovernanceVerification && !fullReleaseVerification && !fullContractVerification && !fullActivationVerification && !fullPreviewVerification && !fullImportVerification &&
            !fullProductionAdmissionVerification && !fullRecipeTransferVerification && !fullTraceStoragePolicyVerification &&
            !fullQualificationCycleVerification && !fullPlcCommunicationVerification &&
            !fullProductionInspectionVerification && !fullPartIdentityVerification ? checkpoint : ReadCheckpoint(db,
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
        var calibrationSessionOrdinal = hasCalibration ? Scalar(db, "SELECT CalibrationSessionPosition FROM audit_entries WHERE CalibrationSessionPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var calibrationEventOrdinal = hasCalibration ? Scalar(db, "SELECT CalibrationEventPosition FROM audit_entries WHERE CalibrationEventPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var calibrationManifestOrdinal = hasCalibration ? Scalar(db, "SELECT CalibrationManifestPosition FROM audit_entries WHERE CalibrationManifestPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var governanceOrdinal = hasGovernance ? Scalar(db, "SELECT GovernancePosition FROM audit_entries WHERE GovernancePosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var releaseOrdinal = hasRelease ? Scalar(db, "SELECT ReleasePosition FROM audit_entries WHERE ReleasePosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var contractOrdinal = hasContract ? Scalar(db, "SELECT PlcResultContractPosition FROM audit_entries WHERE PlcResultContractPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var activationOrdinal = hasActivation ? Scalar(db, "SELECT ActivationPosition FROM audit_entries WHERE ActivationPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var previewOrdinal = hasPreview ? Scalar(db, "SELECT PreviewPosition FROM audit_entries WHERE PreviewPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var importOrdinal = hasImport ? Scalar(db, "SELECT CalibrationImportPosition FROM audit_entries WHERE CalibrationImportPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var manualOrdinal = hasManual ? Scalar(db, "SELECT ManualInspectionPosition FROM audit_entries WHERE ManualInspectionPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;", deadline, Number(after)) : 0;
        var productionAdmissionOrdinal = hasProductionAdmission ? Scalar(db,
            "SELECT ProductionAdmissionPosition FROM audit_entries WHERE ProductionAdmissionPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
            deadline, Number(after)) : 0;
        var stationQualificationOrdinal = hasStationQualification ? Scalar(db,
            "SELECT StationQualificationPosition FROM audit_entries WHERE StationQualificationPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
            deadline, Number(after)) : 0;
        Dictionary<long, SqliteCommandStore.StationQualificationStoredRow>? stationQualificationRows = null;
         var recipeTransferOrdinal = hasRecipeTransfer ? Scalar(db,
             "SELECT RecipeTransferPosition FROM audit_entries WHERE RecipeTransferPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
             deadline, Number(after)) : 0;
         var traceStoragePolicyOrdinal = hasTraceStoragePolicy ? Scalar(db,
             "SELECT TraceStoragePolicyPosition FROM audit_entries WHERE TraceStoragePolicyPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
             deadline, Number(after)) : 0;
        var qualificationCycleOrdinal = hasQualificationCycle ? Scalar(db,
            "SELECT QualificationCyclePosition FROM audit_entries WHERE QualificationCyclePosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
            deadline, Number(after)) : 0;
        var plcCommunicationOrdinal = hasPlcCommunication ? Scalar(db,
            "SELECT PlcCommunicationPosition FROM audit_entries WHERE PlcCommunicationPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
            deadline, Number(after)) : 0;
        var productionInspectionOrdinal = hasProductionInspection ? Scalar(db,
            "SELECT ProductionInspectionPosition FROM audit_entries WHERE ProductionInspectionPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
            deadline, Number(after)) : 0;
        var partIdentityOrdinal = hasPartIdentity ? Scalar(db,
            "SELECT PartIdentityPosition FROM audit_entries WHERE PartIdentityPosition IS NOT NULL AND Sequence<=? ORDER BY Sequence DESC LIMIT 1;",
            deadline, Number(after)) : 0;
        var positionColumns = schema29
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,ProductionInspectionPosition,PartIdentityPosition"
            : schema28
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition,ProductionInspectionPosition"
            : schema27
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition,PlcCommunicationPosition"
            : schema26
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition,QualificationCyclePosition"
            : schema25
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition,TraceStoragePolicyPosition"
            : schema24
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition,RecipeTransferPosition"
            : schema23
             ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition,StationQualificationPosition"
            : schema22
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition,ProductionAdmissionPosition"
            : schema21
            ? "IdentityPosition,AlarmPosition,ResultPosition,DraftPosition,CameraPosition,NetworkPosition,ImagingPosition,CalibrationSessionPosition,CalibrationEventPosition,CalibrationManifestPosition,GovernancePosition,ReleasePosition,PlcResultContractPosition,ActivationPosition,PreviewPosition,CalibrationImportPosition,ManualInspectionPosition"
            : (hasIdentity ? "IdentityPosition" : "NULL") + "," + (hasAlarm ? "AlarmPosition" : "NULL") + "," +
              (hasArchive ? "ResultPosition" : "NULL") + "," + (hasDraft ? "DraftPosition" : "NULL") + "," +
              (hasCamera ? "CameraPosition" : "NULL") + "," + (hasNetwork ? "NetworkPosition" : "NULL") + "," +
              (hasImaging ? "ImagingPosition" : "NULL") + "," + (hasCalibration ? "CalibrationSessionPosition" : "NULL") + "," +
              (hasCalibration ? "CalibrationEventPosition" : "NULL") + "," + (hasCalibration ? "CalibrationManifestPosition" : "NULL") + "," +
              (hasGovernance ? "GovernancePosition" : "NULL") + "," + (hasRelease ? "ReleasePosition" : "NULL") + "," +
              (hasContract ? "PlcResultContractPosition" : "NULL") + (hasActivation ? ",ActivationPosition" : string.Empty) +
              (hasPreview ? ",PreviewPosition" : string.Empty) + (hasImport ? ",CalibrationImportPosition" : string.Empty);
        var rows = Read(db, "SELECT Sequence,Kind,FactPosition,Payload,PreviousHash,Hash," + positionColumns +
            " FROM audit_entries WHERE Sequence>? ORDER BY Sequence LIMIT ?;", deadline, s =>
        {
            if (schema29)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    SqliteNative.ColumnText(s, 23), SqliteNative.ColumnText(s, 24), SqliteNative.ColumnText(s, 25),
                    SqliteNative.ColumnText(s, 26), SqliteNative.ColumnText(s, 27), SqliteNative.ColumnText(s, 28),
                    SqliteNative.ColumnText(s, 29), SqliteNative.ColumnText(s, 30));
            if (schema28)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    SqliteNative.ColumnText(s, 23), SqliteNative.ColumnText(s, 24), SqliteNative.ColumnText(s, 25),
                    SqliteNative.ColumnText(s, 26), SqliteNative.ColumnText(s, 27), SqliteNative.ColumnText(s, 28),
                    SqliteNative.ColumnText(s, 29));
            if (schema27)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    SqliteNative.ColumnText(s, 23), SqliteNative.ColumnText(s, 24), SqliteNative.ColumnText(s, 25),
                    SqliteNative.ColumnText(s, 26), SqliteNative.ColumnText(s, 27), SqliteNative.ColumnText(s, 28));
            if (schema26)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    SqliteNative.ColumnText(s, 23), SqliteNative.ColumnText(s, 24), SqliteNative.ColumnText(s, 25),
                    SqliteNative.ColumnText(s, 26), SqliteNative.ColumnText(s, 27));
            if (schema25)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    SqliteNative.ColumnText(s, 23), SqliteNative.ColumnText(s, 24), SqliteNative.ColumnText(s, 25),
                    SqliteNative.ColumnText(s, 26));
            if (schema24)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    SqliteNative.ColumnText(s, 23), SqliteNative.ColumnText(s, 24), SqliteNative.ColumnText(s, 25));
            if (schema21 || schema22 || schema23)
                return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                    SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                    SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                    SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                    SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12), SqliteNative.ColumnText(s, 13),
                    SqliteNative.ColumnText(s, 14), SqliteNative.ColumnText(s, 15), SqliteNative.ColumnText(s, 16),
                    SqliteNative.ColumnText(s, 17), SqliteNative.ColumnText(s, 18), SqliteNative.ColumnText(s, 19),
                    SqliteNative.ColumnText(s, 20), SqliteNative.ColumnText(s, 21), SqliteNative.ColumnText(s, 22),
                    schema22 || schema23 ? SqliteNative.ColumnText(s, 23) : null,
                    schema23 ? SqliteNative.ColumnText(s, 24) : null,
                    null);
            var legacyActivation = 19;
            var legacyPreview = legacyActivation + (hasActivation ? 1 : 0);
            var legacyImport = legacyPreview + (hasPreview ? 1 : 0);
            return new ChainRow(SqliteNative.ColumnInt64(s, 0), SqliteNative.ColumnText(s, 1)!,
                SqliteNative.ColumnText(s, 2), SqliteNative.ColumnText(s, 3)!, SqliteNative.ColumnText(s, 4)!,
                SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6), SqliteNative.ColumnText(s, 7),
                SqliteNative.ColumnText(s, 8), SqliteNative.ColumnText(s, 9), SqliteNative.ColumnText(s, 10),
                SqliteNative.ColumnText(s, 11), SqliteNative.ColumnText(s, 12),
                hasCalibration ? SqliteNative.ColumnText(s, 13) : null,
                hasCalibration ? SqliteNative.ColumnText(s, 14) : null,
                hasCalibration ? SqliteNative.ColumnText(s, 15) : null,
                hasGovernance ? SqliteNative.ColumnText(s, 16) : null,
                hasRelease ? SqliteNative.ColumnText(s, 17) : null,
                hasContract ? SqliteNative.ColumnText(s, 18) : null,
                hasActivation ? SqliteNative.ColumnText(s, legacyActivation) : null,
                hasPreview ? SqliteNative.ColumnText(s, legacyPreview) : null,
                hasImport ? SqliteNative.ColumnText(s, legacyImport) : null,
                null, null, null, null);
        }, Number(after), Number(count));
        var next = after + 1;
        foreach (var row in rows)
        {
            Require(row.Sequence == next++, "AuditChainGap");
            Require(row.PreviousHash == previousHash, "AuditChainLinkMismatch");
            Require(row.Kind == "CameraNetworkEvent" || row.NetworkPosition is null,
                "AuditNetworkPositionGap");
            Require(row.Kind == "ImagingSetupRevision" || row.ImagingPosition is null,
                "AuditImagingPositionGap");
            var calibrationKind = row.Kind is "CalibrationSessionHeader" or "CalibrationSessionEvent" or "CalibrationFrameManifest";
            Require(calibrationKind || (row.CalibrationSessionPosition is null && row.CalibrationEventPosition is null &&
                row.CalibrationManifestPosition is null), "AuditCalibrationPositionGap");
            var governanceKind = row.Kind is "CalibrationGovernanceStoreActivated" or "CalibrationGovernanceEvent";
            Require(governanceKind || row.GovernancePosition is null, "AuditGovernancePositionGap");
            var releaseKind = row.Kind is "RecipeReleaseStoreActivated" or "RecipeReleaseEvent";
            Require(releaseKind || row.ReleasePosition is null, "AuditReleasePositionGap");
            var contractKind = row.Kind is "PlcResultContractStoreActivated" or "PlcResultContractEvent";
            Require(contractKind || row.PlcResultContractPosition is null, "AuditPlcResultContractPositionGap");
            var activationKind = row.Kind is "RecipeActivationStoreActivated" or "RecipeActivationEvent";
            Require(activationKind || row.ActivationPosition is null, "AuditRecipeActivationPositionGap");
            var previewKind = row.Kind is "PreviewSessionStoreActivated" or "PreviewSessionEvent";
            Require(previewKind || row.PreviewPosition is null, "AuditPreviewSessionPositionGap");
             var importKind = row.Kind is "CalibrationImportStoreActivated" or "CalibrationImportEvent";
             Require(importKind || row.CalibrationImportPosition is null, "AuditCalibrationImportPositionGap");
             var manualKind = row.Kind is "ManualInspectionStoreActivated" or "ManualInspectionEvent";
             Require(manualKind || row.ManualInspectionPosition is null, "AuditManualInspectionPositionGap");
             var productionAdmissionKind = row.Kind is "ProductionAdmissionStoreActivated" or "ProductionAdmissionEvent";
             Require(productionAdmissionKind || row.ProductionAdmissionPosition is null,
                 "AuditProductionAdmissionPositionGap");
              var stationKind = row.Kind is "StationQualificationStoreActivated" or "StationQualificationEvent";
              var recipeTransferKind = row.Kind is "RecipeTransferStoreActivated" or "RecipeTransferEvent";
              var traceStoragePolicyKind = row.Kind is "TraceStoragePolicyStoreActivated" or "TraceStoragePolicyEvent";
              Require(recipeTransferKind || row.RecipeTransferPosition is null,
                  "AuditRecipeTransferPositionGap");
               Require(traceStoragePolicyKind || row.TraceStoragePolicyPosition is null,
                   "AuditTraceStoragePolicyPositionGap");
               var qualificationCycleKind = row.Kind is "QualificationCycleStoreActivated" or "QualificationCycleEvent";
               Require(qualificationCycleKind || row.QualificationCyclePosition is null,
                   "AuditQualificationCyclePositionGap");
               var plcCommunicationKind = row.Kind is "PlcCommunicationStoreActivated" or "PlcCommunicationEvent";
               Require(plcCommunicationKind || row.PlcCommunicationPosition is null,
                   "AuditPlcCommunicationPositionGap");
               var productionInspectionKind = row.Kind is "ProductionInspectionStoreActivated" or "ProductionInspectionEvent";
               Require(productionInspectionKind || row.ProductionInspectionPosition is null,
                   "AuditProductionInspectionPositionGap");
               var partIdentityKind = row.Kind is "PartIdentityStoreActivated" or "PartIdentityEvent";
               Require(partIdentityKind || row.PartIdentityPosition is null,
                   "AuditPartIdentityPositionGap");
               var encodedPayloadLimit = partIdentityKind ? partIdentityOptions!.MaximumPayloadBytes * 2 :
                   productionInspectionKind ? productionInspectionOptions!.MaximumPayloadBytes * 2 :
                   plcCommunicationKind ? plcCommunicationOptions!.MaximumPayloadBytes * 2 :
                   qualificationCycleKind ? qualificationCycleOptions!.MaximumPayloadBytes * 2 :
                   traceStoragePolicyKind ? traceStoragePolicyOptions!.MaximumPayloadBytes * 2 :
                  stationKind ? stationQualificationOptions!.MaximumPayloadBytes * 2 :
                  row.Kind is "RecipeTransferStoreActivated" or "RecipeTransferEvent" ? recipeTransferOptions!.MaximumPayloadBytes * 2 :
                 manualKind ? manualOptions!.MaximumPayloadBytes * 2 :
                  productionAdmissionKind ? productionAdmissionOptions!.MaximumPayloadBytes * 2 :
                 importKind ? importOptions!.MaximumPayloadBytes * 2 :
                contractKind ? PlcResultContractStoreOptions.SqliteValueLimitBytes * 2 :
                activationKind ? activationOptions!.MaximumPayloadBytes * 2 :
                previewKind ? previewOptions!.MaximumPayloadBytes * 2 :
                releaseKind ? RecipeReleaseStoreOptions.SqliteValueLimitBytes * 2 :
                governanceKind ? CalibrationGovernanceStoreOptions.SqliteValueLimitBytes * 2 :
                hasCalibration ? CalibrationSessionStorageCodec.MaximumEncodedChars :
                hasImaging ? ImagingSetupRevisionStorageCodec.MaximumEncodedPayloadChars :
                hasNetwork ? CameraNetworkStorageCodec.MaximumEncodedPayloadChars :
                hasCamera ? CameraSetupStorageCodec.MaximumEncodedPayloadChars :
                hasArchive ? AlgorithmResultArchiveOptions.MaximumBindingPayloadBytes * 2 :
                hasAlarm ? AlarmStorageCodec.MaximumEncodedPayloadChars : 24000;
            Require(row.Payload.Length <= encodedPayloadLimit, "AuditPayloadOversize");
            var payload = Convert.FromBase64String(row.Payload);
              var payloadLimit = partIdentityKind ? partIdentityOptions!.MaximumPayloadBytes :
                  productionInspectionKind ? productionInspectionOptions!.MaximumPayloadBytes :
                  plcCommunicationKind ? plcCommunicationOptions!.MaximumPayloadBytes :
                  qualificationCycleKind ? qualificationCycleOptions!.MaximumPayloadBytes :
                   traceStoragePolicyKind ? traceStoragePolicyOptions!.MaximumPayloadBytes :
                  stationKind ? stationQualificationOptions!.MaximumPayloadBytes :
                  row.Kind is "RecipeTransferStoreActivated" or "RecipeTransferEvent" ? recipeTransferOptions!.MaximumPayloadBytes :
                 manualKind ? manualOptions!.MaximumPayloadBytes :
                  productionAdmissionKind ? productionAdmissionOptions!.MaximumPayloadBytes :
                 importKind ? importOptions!.MaximumPayloadBytes :
                contractKind ? PlcResultContractStoreOptions.SqliteValueLimitBytes :
                activationKind ? activationOptions!.MaximumPayloadBytes :
                previewKind ? previewOptions!.MaximumPayloadBytes :
                releaseKind ? RecipeReleaseStoreOptions.SqliteValueLimitBytes :
                governanceKind ? CalibrationGovernanceStoreOptions.SqliteValueLimitBytes :
                hasCalibration ? CalibrationSessionStoreOptions.SqliteValueLimitBytes :
                hasImaging ? ImagingSetupRevisionStorageCodec.MaximumPayloadBytes :
                hasNetwork ? CameraNetworkStorageCodec.MaximumPayloadBytes :
                hasCamera ? CameraSetupStorageCodec.MaximumPayloadBytes :
                hasArchive ? AlgorithmResultArchiveOptions.MaximumBindingPayloadBytes :
                hasAlarm ? AlarmStorageCodec.MaximumPayloadBytes : 16384;
            Require(payload.Length <= payloadLimit, "AuditPayloadOversize");
              var rowHash = schema29
                  ? EntryHashV25(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                      row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                      row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                      row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                      row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                      row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                      row.ProductionAdmissionPosition, row.StationQualificationPosition, row.RecipeTransferPosition,
                      row.TraceStoragePolicyPosition, row.QualificationCyclePosition, row.PlcCommunicationPosition,
                      row.ProductionInspectionPosition, row.PartIdentityPosition, payload)
                  : schema28
                  ? EntryHashV24(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                      row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                      row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                      row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                      row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                      row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                      row.ProductionAdmissionPosition, row.StationQualificationPosition, row.RecipeTransferPosition,
                      row.TraceStoragePolicyPosition, row.QualificationCyclePosition, row.PlcCommunicationPosition,
                      row.ProductionInspectionPosition, payload)
                  : schema27
                  ? EntryHashV23(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                      row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                      row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                      row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                      row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                      row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                      row.ProductionAdmissionPosition, row.StationQualificationPosition, row.RecipeTransferPosition,
                      row.TraceStoragePolicyPosition, row.QualificationCyclePosition, row.PlcCommunicationPosition, payload)
                  : schema26
                  ? EntryHashV22(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                      row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                      row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                      row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                      row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                      row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                      row.ProductionAdmissionPosition, row.StationQualificationPosition, row.RecipeTransferPosition,
                      row.TraceStoragePolicyPosition, row.QualificationCyclePosition, payload)
                  : schema25
                  ? EntryHashV21(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                      row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                      row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                      row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                      row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                      row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                      row.ProductionAdmissionPosition, row.StationQualificationPosition, row.RecipeTransferPosition,
                      row.TraceStoragePolicyPosition, payload)
                  : schema24
                  ? EntryHashV20(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                      row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                      row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                      row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                      row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                      row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                      row.ProductionAdmissionPosition, row.StationQualificationPosition, row.RecipeTransferPosition, payload)
                  : schema23
                 ? EntryHashV19(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                     row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                     row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                     row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                     row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                     row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                     row.ProductionAdmissionPosition, row.StationQualificationPosition, payload)
                 : schema22
                 ? EntryHashV18(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                     row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                     row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                     row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                     row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                     row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition,
                     row.ProductionAdmissionPosition, payload)
                 : schema21
                 ? EntryHashV17(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                     row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                     row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                     row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                     row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                     row.PreviewPosition, row.CalibrationImportPosition, row.ManualInspectionPosition, payload)
                 : hasImport
                ? EntryHashV16(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                    row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                    row.PreviewPosition, row.CalibrationImportPosition, payload)
                : hasPreview
                ? EntryHashV15(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                    row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition,
                    row.PreviewPosition, payload)
                : hasActivation
                ? EntryHashV14(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                    row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, row.ActivationPosition, payload)
                : hasContract
                ? EntryHashV13(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                    row.GovernancePosition, row.ReleasePosition, row.PlcResultContractPosition, payload)
                : hasRelease
                ? EntryHashV12(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                    row.GovernancePosition, row.ReleasePosition, payload)
                : hasGovernance
                ? EntryHashV11(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition,
                    row.GovernancePosition, payload)
                : hasCalibration
                ? EntryHash(schemaVersion, policy.StationId, row.Sequence, previousHash!, row.Kind,
                    row.FactPosition, row.IdentityPosition, row.AlarmPosition, row.ResultPosition,
                    row.DraftPosition, row.CameraPosition, row.NetworkPosition, row.ImagingPosition,
                    row.CalibrationSessionPosition, row.CalibrationEventPosition, row.CalibrationManifestPosition, payload)
                : hasImaging
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
            else if (hasCalibration && row.Kind == "CalibrationStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null,
                    "CalibrationActivationBindingMismatch");
                SqliteCommandStore.VerifyCalibrationStoreActivationPayload(db, payload,
                    calibrationSessionOptions!, deadline);
            }
            else if (hasCalibration && row.Kind == "CalibrationSessionHeader")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationEventPosition is null &&
                    row.CalibrationManifestPosition is null, "CalibrationSessionAuditPositionGap");
                if (!long.TryParse(row.CalibrationSessionPosition, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("CalibrationSessionAuditPositionGap");
                Require(position == ++calibrationSessionOrdinal, "CalibrationSessionAuditPositionGap");
                var stored = Text(db, "SELECT Payload FROM calibration_sessions WHERE Position=?;", deadline,
                    Number(position));
                Require(stored is not null && stored == row.Payload, "CalibrationSessionAuditBindingMismatch");
            }
            else if (hasCalibration && row.Kind == "CalibrationSessionEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationManifestPosition is null, "CalibrationEventAuditPositionGap");
                if (!long.TryParse(row.CalibrationEventPosition, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("CalibrationEventAuditPositionGap");
                Require(position == ++calibrationEventOrdinal, "CalibrationEventAuditPositionGap");
                var stored = Text(db, "SELECT Payload FROM calibration_session_events WHERE Position=?;", deadline,
                    Number(position));
                Require(stored is not null && stored == row.Payload, "CalibrationEventAuditBindingMismatch");
            }
            else if (hasCalibration && row.Kind == "CalibrationFrameManifest")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null, "CalibrationManifestAuditPositionGap");
                if (!long.TryParse(row.CalibrationManifestPosition, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("CalibrationManifestAuditPositionGap");
                Require(position == ++calibrationManifestOrdinal, "CalibrationManifestAuditPositionGap");
                var stored = Text(db, "SELECT Payload FROM calibration_frame_manifests WHERE Position=?;", deadline,
                    Number(position));
                Require(stored is not null && stored == row.Payload, "CalibrationManifestAuditBindingMismatch");
            }
            else if (hasGovernance && row.Kind == "CalibrationGovernanceStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null, "CalibrationGovernanceActivationBindingMismatch");
                SqliteCommandStore.VerifyCalibrationGovernanceActivationPayload(db, payload,
                    governanceOptions!, deadline);
            }
            else if (hasGovernance && row.Kind == "CalibrationGovernanceEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null,
                    "CalibrationGovernancePositionGap");
                if (!long.TryParse(row.GovernancePosition, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var position))
                    throw new InvalidOperationException("CalibrationGovernancePositionGap");
                Require(position == ++governanceOrdinal, "CalibrationGovernancePositionGap");
                SqliteCommandStore.VerifyCalibrationGovernanceAuditPayload(db, position, payload,
                    governanceOptions!, deadline);
            }
            else if (hasRelease && row.Kind == "RecipeReleaseStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null,
                    "RecipeReleaseActivationBindingMismatch");
                SqliteCommandStore.VerifyRecipeReleaseActivationPayload(db, payload, releaseOptions!, deadline);
            }
            else if (hasRelease && row.Kind == "RecipeReleaseEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null, "RecipeReleasePositionGap");
                if (!long.TryParse(row.ReleasePosition, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var position))
                    throw new InvalidOperationException("RecipeReleasePositionGap");
                Require(position == ++releaseOrdinal, "RecipeReleasePositionGap");
                SqliteCommandStore.VerifyRecipeReleaseAuditPayload(db, position, payload,
                    releaseOptions!, deadline);
            }
            else if (hasContract && row.Kind == "PlcResultContractStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null && row.PlcResultContractPosition is null,
                    "PlcResultContractActivationBindingMismatch");
                SqliteCommandStore.VerifyPlcResultContractActivationPayload(db, payload,
                    contractOptions!, deadline);
            }
            else if (hasContract && row.Kind == "PlcResultContractEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null,
                    "PlcResultContractPositionGap");
                if (!long.TryParse(row.PlcResultContractPosition, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("PlcResultContractPositionGap");
                Require(position == ++contractOrdinal, "PlcResultContractPositionGap");
                SqliteCommandStore.VerifyPlcResultContractAuditPayload(db, position, payload,
                    contractOptions!, deadline);
            }
            else if (hasActivation && row.Kind == "RecipeActivationStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null && row.PlcResultContractPosition is null &&
                    row.ActivationPosition is null, "RecipeActivationActivationBindingMismatch");
                SqliteCommandStore.VerifyRecipeActivationActivationPayload(db, payload,
                    activationOptions!, deadline);
            }
            else if (hasActivation && row.Kind == "RecipeActivationEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null && row.PlcResultContractPosition is null,
                    "RecipeActivationPositionGap");
                if (!long.TryParse(row.ActivationPosition, NumberStyles.None, CultureInfo.InvariantCulture,
                        out var position))
                    throw new InvalidOperationException("RecipeActivationPositionGap");
                Require(position == ++activationOrdinal, "RecipeActivationPositionGap");
                SqliteCommandStore.VerifyRecipeActivationAuditPayload(db, position, payload,
                    activationOptions!, deadline, governanceOptions);
            }
            else if (hasImport && row.Kind == "CalibrationImportStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null &&
                    row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                    row.PreviewPosition is null && row.CalibrationImportPosition is null,
                    "CalibrationImportActivationBindingMismatch");
                SqliteCommandStore.VerifyCalibrationImportActivationPayload(db, payload,
                    importOptions!, deadline);
            }
            else if (hasImport && row.Kind == "CalibrationImportEvent")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null &&
                    row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                    row.PreviewPosition is null, "CalibrationImportPositionGap");
                if (!long.TryParse(row.CalibrationImportPosition, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("CalibrationImportPositionGap");
                Require(position == ++importOrdinal, "CalibrationImportPositionGap");
                SqliteCommandStore.VerifyCalibrationImportAuditPayload(db, position, payload,
                    importOptions!, deadline);
            }
            else if (hasPreview && row.Kind == "PreviewSessionStoreActivated")
            {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null &&
                    row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                    row.PreviewPosition is null, "PreviewSessionActivationBindingMismatch");
                SqliteCommandStore.VerifyPreviewSessionActivationPayload(db, payload,
                    previewOptions!, deadline);
            }
             else if (hasPreview && row.Kind == "PreviewSessionEvent")
             {
                Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                    row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                    row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                    row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                    row.GovernancePosition is null && row.ReleasePosition is null &&
                    row.PlcResultContractPosition is null && row.ActivationPosition is null,
                    "PreviewSessionPositionGap");
                if (!long.TryParse(row.PreviewPosition, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position))
                    throw new InvalidOperationException("PreviewSessionPositionGap");
                Require(position == ++previewOrdinal, "PreviewSessionPositionGap");
                 SqliteCommandStore.VerifyPreviewSessionAuditPayload(db, position, payload,
                     previewOptions!, deadline);
             }
             else if (hasManual && row.Kind == "ManualInspectionStoreActivated")
             {
                 Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                     row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                     row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                     row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                     row.GovernancePosition is null && row.ReleasePosition is null &&
                     row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                     row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                     row.ManualInspectionPosition is null, "ManualInspectionActivationBindingMismatch");
                 SqliteCommandStore.VerifyManualInspectionActivationPayload(db, payload,
                     manualOptions!, deadline);
             }
             else if (hasManual && row.Kind == "ManualInspectionEvent")
             {
                 Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                     row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                     row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                     row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                     row.GovernancePosition is null && row.ReleasePosition is null &&
                     row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                     row.PreviewPosition is null && row.CalibrationImportPosition is null,
                     "ManualInspectionPositionGap");
                 if (!long.TryParse(row.ManualInspectionPosition, NumberStyles.None,
                         CultureInfo.InvariantCulture, out var position))
                     throw new InvalidOperationException("ManualInspectionPositionGap");
                 Require(position == ++manualOrdinal, "ManualInspectionPositionGap");
                 SqliteCommandStore.VerifyManualInspectionAuditPayload(db, position, payload,
                     manualOptions!, deadline);
             }
             else if (hasProductionAdmission && row.Kind == "ProductionAdmissionStoreActivated")
             {
                 Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                     row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                     row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                     row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                     row.GovernancePosition is null && row.ReleasePosition is null &&
                     row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                     row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                     row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null,
                     "ProductionAdmissionActivationBindingMismatch");
                 SqliteCommandStore.VerifyProductionAdmissionActivationPayload(db, payload,
                     productionAdmissionOptions!, deadline);
             }
             else if (hasProductionAdmission && row.Kind == "ProductionAdmissionEvent")
             {
                 Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                     row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                     row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                     row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                     row.GovernancePosition is null && row.ReleasePosition is null &&
                     row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                     row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                     row.ManualInspectionPosition is null,
                     "ProductionAdmissionPositionGap");
                 if (!long.TryParse(row.ProductionAdmissionPosition, NumberStyles.None,
                         CultureInfo.InvariantCulture, out var position))
                     throw new InvalidOperationException("ProductionAdmissionPositionGap");
                 Require(position == ++productionAdmissionOrdinal, "ProductionAdmissionPositionGap");
                 SqliteCommandStore.VerifyProductionAdmissionAuditPayload(db, position, payload,
                     productionAdmissionOptions!, deadline);
             }
             else if (hasStationQualification && row.Kind == "StationQualificationStoreActivated")
             {
                 Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                     row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                     row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                     row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                     row.GovernancePosition is null && row.ReleasePosition is null &&
                     row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                     row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                     row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                     row.StationQualificationPosition is null,
                     "StationQualificationActivationBindingMismatch");
                 SqliteCommandStore.VerifyStationQualificationActivationPayload(db, payload,
                     stationQualificationOptions!, deadline);
             }
              else if (hasStationQualification && row.Kind == "StationQualificationEvent")
              {
                 Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                     row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                     row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                     row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                     row.GovernancePosition is null && row.ReleasePosition is null &&
                     row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                     row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                     row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null,
                     "StationQualificationPositionGap");
                 if (!long.TryParse(row.StationQualificationPosition, NumberStyles.None,
                         CultureInfo.InvariantCulture, out var position))
                     throw new InvalidOperationException("StationQualificationPositionGap");
                 Require(position == ++stationQualificationOrdinal, "StationQualificationPositionGap");
                  // Reuse decoded rows only within this verification snapshot.
                  // Reading the whole ledger for every central entry is quadratic.
                  stationQualificationRows ??= SqliteCommandStore.ReadStationQualificationRows(db,
                      stationQualificationOptions!, deadline).ToDictionary(value => value.Position);
                  stationQualificationRows.TryGetValue(position, out var qualificationRow);
                  SqliteCommandStore.VerifyStationQualificationAuditPayload(qualificationRow, payload);
              }
              else if (hasRecipeTransfer && row.Kind == "RecipeTransferStoreActivated")
              {
                  Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                      row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                      row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                      row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                      row.GovernancePosition is null && row.ReleasePosition is null &&
                      row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                      row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                      row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                      row.StationQualificationPosition is null && row.RecipeTransferPosition is null,
                      "RecipeTransferActivationBindingMismatch");
                  SqliteCommandStore.VerifyRecipeTransferActivationPayload(db, payload,
                      recipeTransferOptions!, deadline);
              }
              else if (hasRecipeTransfer && row.Kind == "RecipeTransferEvent")
              {
                  Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                      row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                      row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                      row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                      row.GovernancePosition is null && row.ReleasePosition is null &&
                      row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                      row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                      row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                      row.StationQualificationPosition is null,
                      "RecipeTransferPositionGap");
                  if (!long.TryParse(row.RecipeTransferPosition, NumberStyles.None,
                          CultureInfo.InvariantCulture, out var position))
                      throw new InvalidOperationException("RecipeTransferPositionGap");
                  Require(position == ++recipeTransferOrdinal, "RecipeTransferPositionGap");
                  SqliteCommandStore.VerifyRecipeTransferAuditPayload(db, position, payload,
                      recipeTransferOptions!, deadline);
              }
              else if (hasTraceStoragePolicy && row.Kind == "TraceStoragePolicyStoreActivated")
              {
                  Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                      row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                      row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                      row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                      row.GovernancePosition is null && row.ReleasePosition is null &&
                      row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                      row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                      row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                      row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                      row.TraceStoragePolicyPosition is null,
                      "TraceStoragePolicyActivationBindingMismatch");
                  SqliteCommandStore.VerifyTraceStoragePolicyActivationPayload(db, payload,
                      traceStoragePolicyOptions!, deadline);
              }
               else if (hasTraceStoragePolicy && row.Kind == "TraceStoragePolicyEvent")
               {
                  Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                      row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                      row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                      row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                      row.GovernancePosition is null && row.ReleasePosition is null &&
                      row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                      row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                      row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                      row.StationQualificationPosition is null && row.RecipeTransferPosition is null,
                      "TraceStoragePolicyPositionGap");
                  if (!long.TryParse(row.TraceStoragePolicyPosition, NumberStyles.None,
                          CultureInfo.InvariantCulture, out var position))
                      throw new InvalidOperationException("TraceStoragePolicyPositionGap");
                  Require(position == ++traceStoragePolicyOrdinal, "TraceStoragePolicyPositionGap");
                   SqliteCommandStore.VerifyTraceStoragePolicyAuditPayload(db, position, payload,
                       traceStoragePolicyOptions!, deadline);
               }
               else if (hasQualificationCycle && row.Kind == "QualificationCycleStoreActivated")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null,
                       "QualificationCycleActivationBindingMismatch");
                   SqliteCommandStore.VerifyQualificationCycleActivationPayload(db, payload,
                       qualificationCycleOptions!, deadline);
               }
               else if (hasQualificationCycle && row.Kind == "QualificationCycleEvent")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null,
                       "QualificationCyclePositionGap");
                   if (!long.TryParse(row.QualificationCyclePosition, NumberStyles.None,
                           CultureInfo.InvariantCulture, out var position))
                       throw new InvalidOperationException("QualificationCyclePositionGap");
                   Require(position == ++qualificationCycleOrdinal, "QualificationCyclePositionGap");
                   SqliteCommandStore.VerifyQualificationCycleAuditPayload(db, position, payload,
                       qualificationCycleOptions!, deadline);
               }
               else if (hasPlcCommunication && row.Kind == "PlcCommunicationStoreActivated")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null &&
                       row.PlcCommunicationPosition is null,
                       "PlcCommunicationActivationBindingMismatch");
                   SqliteCommandStore.VerifyPlcCommunicationActivationPayload(db, payload,
                       plcCommunicationOptions!, deadline);
               }
               else if (hasPlcCommunication && row.Kind == "PlcCommunicationEvent")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null,
                       "PlcCommunicationPositionGap");
                   if (!long.TryParse(row.PlcCommunicationPosition, NumberStyles.None,
                           CultureInfo.InvariantCulture, out var position))
                       throw new InvalidOperationException("PlcCommunicationPositionGap");
                   Require(position == ++plcCommunicationOrdinal, "PlcCommunicationPositionGap");
                   SqliteCommandStore.VerifyPlcCommunicationAuditPayload(db, position, payload,
                       plcCommunicationOptions!, deadline);
               }
               else if (hasProductionInspection && row.Kind == "ProductionInspectionStoreActivated")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null &&
                       row.PlcCommunicationPosition is null && row.ProductionInspectionPosition is null,
                       "ProductionInspectionActivationBindingMismatch");
                   SqliteCommandStore.VerifyProductionInspectionActivationPayload(db, payload,
                       productionInspectionOptions!, deadline);
               }
               else if (hasProductionInspection && row.Kind == "ProductionInspectionEvent")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null &&
                       row.PlcCommunicationPosition is null,
                       "ProductionInspectionPositionGap");
                   if (!long.TryParse(row.ProductionInspectionPosition, NumberStyles.None,
                           CultureInfo.InvariantCulture, out var position))
                       throw new InvalidOperationException("ProductionInspectionPositionGap");
                   Require(position == ++productionInspectionOrdinal, "ProductionInspectionPositionGap");
                   SqliteCommandStore.VerifyProductionInspectionAuditPayload(db, position, payload,
                       productionInspectionOptions!, deadline);
               }
               else if (hasPartIdentity && row.Kind == "PartIdentityStoreActivated")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null &&
                       row.PlcCommunicationPosition is null && row.ProductionInspectionPosition is null &&
                       row.PartIdentityPosition is null,
                       "PartIdentityActivationBindingMismatch");
                   SqliteCommandStore.VerifyPartIdentityActivationPayload(db, payload,
                       partIdentityOptions!, deadline);
               }
               else if (hasPartIdentity && row.Kind == "PartIdentityEvent")
               {
                   Require(row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null &&
                       row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null &&
                       row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null &&
                       row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null &&
                       row.GovernancePosition is null && row.ReleasePosition is null &&
                       row.PlcResultContractPosition is null && row.ActivationPosition is null &&
                       row.PreviewPosition is null && row.CalibrationImportPosition is null &&
                       row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null &&
                       row.StationQualificationPosition is null && row.RecipeTransferPosition is null &&
                       row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null &&
                       row.PlcCommunicationPosition is null && row.ProductionInspectionPosition is null,
                       "PartIdentityPositionGap");
                   if (!long.TryParse(row.PartIdentityPosition, NumberStyles.None,
                           CultureInfo.InvariantCulture, out var position))
                       throw new InvalidOperationException("PartIdentityPositionGap");
                   Require(position == ++partIdentityOrdinal, "PartIdentityPositionGap");
                   SqliteCommandStore.VerifyPartIdentityAuditPayload(db, position, payload,
                       partIdentityOptions!, deadline);
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
             else Require(row.Sequence == 1 && row.Kind == "SigningKeyCreated" && row.FactPosition is null && row.IdentityPosition is null && row.AlarmPosition is null && row.ResultPosition is null && row.DraftPosition is null && row.CameraPosition is null && row.NetworkPosition is null && row.ImagingPosition is null && row.CalibrationSessionPosition is null && row.CalibrationEventPosition is null && row.CalibrationManifestPosition is null && row.GovernancePosition is null && row.ReleasePosition is null && row.PlcResultContractPosition is null && row.ActivationPosition is null && row.PreviewPosition is null && row.CalibrationImportPosition is null && row.ManualInspectionPosition is null && row.ProductionAdmissionPosition is null && row.StationQualificationPosition is null && row.RecipeTransferPosition is null && row.TraceStoragePolicyPosition is null && row.QualificationCyclePosition is null && row.PlcCommunicationPosition is null && row.ProductionInspectionPosition is null && row.PartIdentityPosition is null,
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
        // Every schema-15 audit observation checks cross-record policy, candidate,
        // profile and verification semantics as well as signed row hashes.
        if (hasGovernance)
            SqliteCommandStore.ValidateCalibrationGovernanceHistory(db, governanceOptions!, deadline);
        if (hasRelease)
            SqliteCommandStore.ValidateRecipeReleaseHistory(db, releaseOptions!, recipeDraftOptions!,
                SqliteCommandStore.ReadCalibrationAcceptancePolicies(db, governanceOptions, deadline), null, deadline,
                governanceOptions);
        if (hasContract)
            SqliteCommandStore.ValidatePlcResultContractHistory(db, contractOptions!, deadline);
        if (hasActivation)
            SqliteCommandStore.VerifyRecipeActivationHistory(db, activationOptions!, releaseOptions!,
                contractOptions!, deadline, governanceOptions);
        if (hasImport)
            SqliteCommandStore.ValidateCalibrationImportHistory(db, importOptions!, deadline);
        if (hasRecipeTransfer)
            SqliteCommandStore.ValidateRecipeTransferHistory(db, recipeTransferOptions!, deadline);
        if (hasTraceStoragePolicy)
            SqliteCommandStore.ValidateTraceStoragePolicyHistory(db, traceStoragePolicyOptions!, deadline);
        if (hasQualificationCycle)
            SqliteCommandStore.ValidateQualificationCycleHistory(db, qualificationCycleOptions!, deadline);
        if (hasPlcCommunication)
            SqliteCommandStore.ValidatePlcCommunicationHistory(db, plcCommunicationOptions!, deadline);
        if (hasPartIdentity)
            SqliteCommandStore.ValidatePartIdentityHistory(db, partIdentityOptions!, deadline);
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

    internal static void RequireFullCalibrationGovernanceVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        CalibrationGovernanceStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "CalibrationGovernanceVerificationBudgetExceeded");
        if (options is not null)
            SqliteCommandStore.ValidateCalibrationGovernanceHistory(db, options, deadline);
    }

    internal static void RequireFullRecipeReleaseVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        RecipeReleaseStoreOptions? options = null,
        RecipeDraftStoreOptions? draftOptions = null,
        CalibrationGovernanceStoreOptions? governanceOptions = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "RecipeReleaseVerificationBudgetExceeded");
        if (options is not null)
        {
            Require(draftOptions is not null, "RecipeDraftConfigurationRequired");
            var policies = SqliteCommandStore.ReadCalibrationAcceptancePolicies(db, governanceOptions, deadline);
            SqliteCommandStore.ValidateRecipeReleaseHistory(db, options, draftOptions!, policies, null, deadline,
                governanceOptions);
        }
    }

    internal static void RequireFullPlcResultContractVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        PlcResultContractStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "PlcResultContractVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidatePlcResultContractHistory(db, options, deadline);
        }
    }

    internal static void RequireFullRecipeActivationVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        RecipeActivationStoreOptions? options = null,
        RecipeReleaseStoreOptions? releaseOptions = null,
        PlcResultContractStoreOptions? contractOptions = null,
        CalibrationGovernanceStoreOptions? governanceOptions = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "RecipeActivationVerificationBudgetExceeded");
        if (options is not null)
        {
            Require(releaseOptions is not null, "RecipeReleaseConfigurationRequired");
            Require(contractOptions is not null, "PlcResultContractConfigurationRequired");
            SqliteCommandStore.VerifyRecipeActivationHistory(db, options, releaseOptions!,
                contractOptions!, deadline, governanceOptions);
        }
    }

    internal static void RequireFullPreviewSessionVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        PreviewSessionStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "PreviewSessionVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidatePreviewSessionHistory(db, options, deadline);
        }
    }

    internal static void RequireFullCalibrationImportVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        CalibrationImportStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "CalibrationImportVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidateCalibrationImportHistory(db, options, deadline);
        }
    }

    internal static void RequireFullManualInspectionVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        ManualInspectionStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "ManualInspectionVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.VerifyAllManualInspections(db, options, deadline);
        }
    }

    internal static void RequireFullProductionAdmissionVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        ProductionAdmissionStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "ProductionAdmissionVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.VerifyAllProductionAdmissions(db, options, deadline);
        }
    }

    internal static void RequireFullStationQualificationVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        StationQualificationStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "StationQualificationVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidateStationQualificationHistory(db, options, deadline);
        }
    }

    internal static void RequireFullRecipeTransferVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        RecipeTransferStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "RecipeTransferVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidateRecipeTransferHistory(db, options, deadline);
        }
    }

    internal static void RequireFullTraceStoragePolicyVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        TraceStoragePolicyStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "TraceStoragePolicyVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidateTraceStoragePolicyHistory(db, options, deadline);
        }
    }

    internal static void RequireFullQualificationCycleVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        QualificationCycleStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "QualificationCycleVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidateQualificationCycleHistory(db, options, deadline);
        }
    }

    internal static void RequireFullPlcCommunicationVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        PlcCommunicationStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "PlcCommunicationVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidatePlcCommunicationHistory(db, options, deadline);
        }
    }

    internal static void RequireFullProductionInspectionVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        ProductionInspectionStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "ProductionInspectionVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidateProductionInspectionHistory(db, options, deadline);
        }
    }

    internal static void RequireFullPartIdentityVerification(sqlite3 db,
        AuditIntegrityReport report, StoreDeadline deadline,
        PartIdentityStoreOptions? options = null)
    {
        Require(report.VerifiedFromSequence == 1 &&
            report.VerifiedThroughSequence == Tail(db, deadline).Sequence,
            "PartIdentityVerificationBudgetExceeded");
        if (options is not null)
        {
            options.Validate();
            SqliteCommandStore.ValidatePartIdentityHistory(db, options, deadline);
        }
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
    internal static void RequireReleaseLedgerPresence(sqlite3 db, StoreDeadline deadline,
        bool archive, bool camera, bool recovery, bool network, bool imaging,
        bool calibration, bool governance, bool release, bool plcResultContract = false,
        bool activation = false, bool preview = false, bool calibrationImport = false,
        bool manualInspection = false, bool productionAdmission = false,
        bool stationQualification = false, bool recipeTransfer = false,
        bool traceStoragePolicy = false, bool qualificationCycle = false,
        bool plcCommunication = false)
    {
        void RequireLedger(bool configured, string reason, params string[] tables)
        {
            foreach (var table in tables)
                Require(TableExists(db, table, deadline) == configured, reason);
        }
        RequireLedger(archive, "AlgorithmResultArchiveConfigurationRequired",
            "algorithm_result_archive_config", "development_algorithm_results");
        RequireLedger(camera, "CameraSetupConfigurationRequired",
            "camera_setup_store_config", "camera_setup_events");
        RequireLedger(recovery, "CameraRecoveryConfigurationRequired",
            "camera_recovery_store_config", "camera_recovery_terminal_events");
        RequireLedger(network, "CameraNetworkConfigurationRequired",
            "camera_network_store_config", "camera_network_events");
        RequireLedger(imaging, "ImagingSetupConfigurationRequired",
            "imaging_setup_store_config", "imaging_setup_revisions");
        RequireLedger(calibration, "CalibrationConfigurationRequired",
            "calibration_store_config", "calibration_sessions", "calibration_session_events",
            "calibration_frame_manifests");
        RequireLedger(governance, "CalibrationGovernanceConfigurationRequired",
            "calibration_governance_store_config", "calibration_governance_events");
        RequireLedger(release, "RecipeReleaseConfigurationRequired",
            "recipe_release_store_config", "recipe_release_events");
        RequireLedger(plcResultContract, "PlcResultContractConfigurationRequired",
            "plc_result_contract_store_config", "plc_result_contract_events");
        RequireLedger(activation, "RecipeActivationConfigurationRequired",
            "recipe_activation_store_config", "recipe_activation_events");
        RequireLedger(preview, "PreviewSessionConfigurationRequired",
            "preview_session_store_config", "preview_session_events");
        RequireLedger(calibrationImport, "CalibrationImportConfigurationRequired",
            "calibration_import_store_config", "calibration_import_events");
        RequireLedger(manualInspection, "ManualInspectionConfigurationRequired",
            "manual_inspection_store_config", "manual_inspection_events");
        RequireLedger(productionAdmission, "ProductionAdmissionConfigurationRequired",
            "production_admission_store_config", "production_admission_events");
        RequireLedger(stationQualification, "StationQualificationConfigurationRequired",
            "station_qualification_store_config", "station_qualification_events");
        RequireLedger(recipeTransfer, "RecipeTransferConfigurationRequired",
            "recipe_transfer_store_config", "recipe_transfer_events", "recipe_transfer_import_provenance");
        RequireLedger(traceStoragePolicy, "TraceStoragePolicyConfigurationRequired",
            "trace_storage_policy_store_config", "trace_storage_policy_events");
        RequireLedger(qualificationCycle, "QualificationCycleConfigurationRequired",
            "qualification_cycle_store_config", "qualification_cycle_events");
        RequireLedger(plcCommunication, "PlcCommunicationConfigurationRequired",
            "plc_communication_store_config", "plc_communication_events");
    }

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
        byte[] payload) => EntryHash(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
            identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
            imagingPosition, null, null, null, payload);

    private static string EntryHash(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition, string? resultPosition,
        string? draftPosition, string? cameraPosition, string? networkPosition, string? imagingPosition,
        string? calibrationSessionPosition, string? calibrationEventPosition, string? calibrationManifestPosition,
        byte[] payload) =>
        schemaVersion >= StationQualificationStoreOptions.SchemaVersion
            ? EntryHashV19(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, null, null, null, null, null, null,
                payload)
        : schemaVersion >= ProductionAdmissionStoreOptions.SchemaVersion
            ? EntryHashV18(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, null, null, null, null, null, payload)
        : schemaVersion >= ManualInspectionStoreOptions.SchemaVersion
            ? EntryHashV17(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, null, null, null, null, payload)
        : schemaVersion >= CalibrationImportStoreOptions.SchemaVersion
            ? EntryHashV16(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, null, null, null, payload)
        : schemaVersion >= PreviewSessionStoreOptions.SchemaVersion
            ? EntryHashV15(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, null, null, payload)
        : schemaVersion >= RecipeActivationStoreOptions.SchemaVersion
            ? EntryHashV14(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, null, payload)
        : schemaVersion >= PlcResultContractStoreOptions.SchemaVersion
            ? EntryHashV13(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, null, payload)
        : schemaVersion >= RecipeReleaseStoreOptions.SchemaVersion
            ? EntryHashV12(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, null, payload)
        : schemaVersion >= CalibrationGovernanceStoreOptions.SchemaVersion
            ? EntryHashV11(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, null, payload)
        : AuditCanonical.Hash(stationId, sequence, previousHash, schemaVersion >= CameraSetupStoreOptions.SchemaVersion
            ? schemaVersion >= CalibrationSessionStoreOptions.SchemaVersion
                ? AuditCanonical.Encode("AuditEntryEnvelopeV10", kind, factPosition, identityPosition, alarmPosition,
                    resultPosition, draftPosition, cameraPosition, networkPosition, imagingPosition,
                    calibrationSessionPosition, calibrationEventPosition, calibrationManifestPosition,
                    Convert.ToBase64String(payload))
            : schemaVersion >= ImagingSetupStoreOptions.SchemaVersion
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

    private static string EntryHashV11(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV11", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
            imagingPosition, calibrationSessionPosition, calibrationEventPosition,
            calibrationManifestPosition, governancePosition, Convert.ToBase64String(payload)));

    private static string EntryHashV12(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV12", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                 calibrationManifestPosition, governancePosition, releasePosition,
                 Convert.ToBase64String(payload)));

    private static string EntryHashV13(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV13", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                 plcResultContractPosition, Convert.ToBase64String(payload)));

    private static string EntryHashV14(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV14", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                 plcResultContractPosition, activationPosition, Convert.ToBase64String(payload)));

    private static string EntryHashV15(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV15", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                Convert.ToBase64String(payload)));

    private static string EntryHashV16(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV16", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                 calibrationImportPosition, Convert.ToBase64String(payload)));

    private static string EntryHashV17(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV17", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, Convert.ToBase64String(payload)));

    private static string EntryHashV18(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV18", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                Convert.ToBase64String(payload)));

    private static string EntryHashV19(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition, byte[] payload)
    {
        if (schemaVersion >= RecipeTransferStoreOptions.SchemaVersion)
            return EntryHashV20(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition, plcResultContractPosition,
                activationPosition, previewPosition, calibrationImportPosition, manualInspectionPosition,
                productionAdmissionPosition, stationQualificationPosition, null, payload);
        return AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV19", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, Convert.ToBase64String(payload)));
    }

    private static string EntryHashV20(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition,
        string? recipeTransferPosition, byte[] payload)
    {
        if (schemaVersion >= TraceStoragePolicyStoreOptions.SchemaVersion)
            return EntryHashV21(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition, plcResultContractPosition,
                activationPosition, previewPosition, calibrationImportPosition, manualInspectionPosition,
                productionAdmissionPosition, stationQualificationPosition, recipeTransferPosition, null, payload);
        return AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV20", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, Convert.ToBase64String(payload)));
    }

    private static string EntryHashV21(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition,
        string? recipeTransferPosition, string? traceStoragePolicyPosition, byte[] payload)
    {
        if (schemaVersion >= QualificationCycleStoreOptions.SchemaVersion)
            return EntryHashV22(schemaVersion, stationId, sequence, previousHash, kind, factPosition,
                identityPosition, alarmPosition, resultPosition, draftPosition, cameraPosition,
                networkPosition, imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition, plcResultContractPosition,
                activationPosition, previewPosition, calibrationImportPosition, manualInspectionPosition,
                productionAdmissionPosition, stationQualificationPosition, recipeTransferPosition,
                traceStoragePolicyPosition, null, payload);
        return AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV21", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                Convert.ToBase64String(payload)));
    }

    private static string EntryHashV22(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition,
        string? recipeTransferPosition, string? traceStoragePolicyPosition,
        string? qualificationCyclePosition, byte[] payload)
    {
        if (schemaVersion >= PlcCommunicationStoreOptions.SchemaVersion)
            return EntryHashV23(schemaVersion, stationId, sequence, previousHash, kind,
                factPosition, identityPosition, alarmPosition, resultPosition, draftPosition,
                cameraPosition, networkPosition, imagingPosition, calibrationSessionPosition,
                calibrationEventPosition, calibrationManifestPosition, governancePosition,
                releasePosition, plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                qualificationCyclePosition, null, payload);
        return AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV22", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                qualificationCyclePosition, Convert.ToBase64String(payload)));
    }

    private static string EntryHashV23(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition,
        string? recipeTransferPosition, string? traceStoragePolicyPosition,
        string? qualificationCyclePosition, string? plcCommunicationPosition, byte[] payload)
    {
        if (schemaVersion >= ProductionInspectionStoreOptions.SchemaVersion)
            return EntryHashV24(schemaVersion, stationId, sequence, previousHash, kind,
                factPosition, identityPosition, alarmPosition, resultPosition, draftPosition,
                cameraPosition, networkPosition, imagingPosition, calibrationSessionPosition,
                calibrationEventPosition, calibrationManifestPosition, governancePosition,
                releasePosition, plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                qualificationCyclePosition, plcCommunicationPosition, null, payload);
        return AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV23", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
            qualificationCyclePosition, plcCommunicationPosition, Convert.ToBase64String(payload)));
    }

    private static string EntryHashV24(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition,
        string? recipeTransferPosition, string? traceStoragePolicyPosition,
        string? qualificationCyclePosition, string? plcCommunicationPosition,
        string? productionInspectionPosition, byte[] payload)
    {
        if (schemaVersion >= PartIdentityStoreOptions.SchemaVersion)
            return EntryHashV25(schemaVersion, stationId, sequence, previousHash, kind,
                factPosition, identityPosition, alarmPosition, resultPosition, draftPosition,
                cameraPosition, networkPosition, imagingPosition, calibrationSessionPosition,
                calibrationEventPosition, calibrationManifestPosition, governancePosition,
                releasePosition, plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                qualificationCyclePosition, plcCommunicationPosition, productionInspectionPosition,
                null, payload);
        return AuditCanonical.Hash(stationId, sequence, previousHash,
        AuditCanonical.Encode("AuditEntryEnvelopeV24", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                qualificationCyclePosition, plcCommunicationPosition, productionInspectionPosition,
                Convert.ToBase64String(payload)));
    }

    private static string EntryHashV25(long schemaVersion, string stationId, long sequence, string previousHash,
        string kind, string? factPosition, string? identityPosition, string? alarmPosition,
        string? resultPosition, string? draftPosition, string? cameraPosition, string? networkPosition,
        string? imagingPosition, string? calibrationSessionPosition, string? calibrationEventPosition,
        string? calibrationManifestPosition, string? governancePosition, string? releasePosition,
        string? plcResultContractPosition, string? activationPosition, string? previewPosition,
        string? calibrationImportPosition, string? manualInspectionPosition,
        string? productionAdmissionPosition, string? stationQualificationPosition,
        string? recipeTransferPosition, string? traceStoragePolicyPosition,
        string? qualificationCyclePosition, string? plcCommunicationPosition,
        string? productionInspectionPosition, string? partIdentityPosition, byte[] payload) =>
        AuditCanonical.Hash(stationId, sequence, previousHash,
            AuditCanonical.Encode("AuditEntryEnvelopeV25", kind, factPosition, identityPosition,
                alarmPosition, resultPosition, draftPosition, cameraPosition, networkPosition,
                imagingPosition, calibrationSessionPosition, calibrationEventPosition,
                calibrationManifestPosition, governancePosition, releasePosition,
                plcResultContractPosition, activationPosition, previewPosition,
                calibrationImportPosition, manualInspectionPosition, productionAdmissionPosition,
                stationQualificationPosition, recipeTransferPosition, traceStoragePolicyPosition,
                qualificationCyclePosition, plcCommunicationPosition, productionInspectionPosition,
                partIdentityPosition, Convert.ToBase64String(payload)));
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
         string? CameraPosition, string? NetworkPosition, string? ImagingPosition,
          string? CalibrationSessionPosition, string? CalibrationEventPosition, string? CalibrationManifestPosition,
         string? GovernancePosition, string? ReleasePosition, string? PlcResultContractPosition,
          string? ActivationPosition, string? PreviewPosition, string? CalibrationImportPosition,
            string? ManualInspectionPosition, string? ProductionAdmissionPosition,
            string? StationQualificationPosition, string? RecipeTransferPosition,
            string? TraceStoragePolicyPosition = null, string? QualificationCyclePosition = null,
            string? PlcCommunicationPosition = null, string? ProductionInspectionPosition = null,
            string? PartIdentityPosition = null);
}
