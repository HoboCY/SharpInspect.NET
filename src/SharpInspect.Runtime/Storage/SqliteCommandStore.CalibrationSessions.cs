using System.Globalization;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Schema 14 calibration-session store shape and activation binding.</summary>
internal sealed partial class SqliteCommandStore
{
    internal const string CalibrationSessionSchemaSql = @"
        CREATE TABLE calibration_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumSessions INTEGER NOT NULL CHECK(MaximumSessions>0),
            MaximumEvents INTEGER NOT NULL CHECK(MaximumEvents>0),
            MaximumEventPayloadBytes INTEGER NOT NULL CHECK(MaximumEventPayloadBytes>0),
            MaximumFramesPerSession INTEGER NOT NULL CHECK(MaximumFramesPerSession>0),
            MaximumFrameBytes INTEGER NOT NULL CHECK(MaximumFrameBytes>0),
            MaximumTotalFrameBytes INTEGER NOT NULL CHECK(MaximumTotalFrameBytes>0),
            EvidenceRoot TEXT NOT NULL CHECK(length(EvidenceRoot)>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE calibration_sessions(
            SessionId TEXT NOT NULL PRIMARY KEY CHECK(length(SessionId)=36),
            Position INTEGER NOT NULL UNIQUE CHECK(Position>0),
            AdmissionCorrelationId TEXT NOT NULL UNIQUE CHECK(length(AdmissionCorrelationId)=36),
            AdmissionAttemptId TEXT NOT NULL UNIQUE CHECK(length(AdmissionAttemptId)=36),
            RuntimeEpoch TEXT NOT NULL CHECK(length(RuntimeEpoch)=36),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            InteractiveSessionId TEXT NOT NULL CHECK(length(InteractiveSessionId)=36),
            AuthorizationRevision INTEGER NOT NULL CHECK(AuthorizationRevision>=0),
            StepUpGrantId TEXT NOT NULL CHECK(length(StepUpGrantId)=36),
            LogicalRole TEXT NOT NULL CHECK(length(LogicalRole)>0 AND length(LogicalRole)<=64),
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            AdmissionAuditSequence INTEGER NOT NULL CHECK(AdmissionAuditSequence>0),
            AdmissionAuditHash TEXT NOT NULL CHECK(length(AdmissionAuditHash)=64),
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE INDEX ix_calibration_sessions_correlation ON calibration_sessions(AdmissionCorrelationId);
        CREATE TABLE calibration_session_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            SessionId TEXT NOT NULL REFERENCES calibration_sessions(SessionId),
            Sequence INTEGER NOT NULL CHECK(Sequence>0),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            Kind TEXT NOT NULL CHECK(length(Kind)>0 AND length(Kind)<=128),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64),
            UNIQUE(SessionId,Sequence));
        CREATE INDEX ix_calibration_session_events_session ON calibration_session_events(SessionId,Sequence);
        CREATE INDEX ix_calibration_session_events_operation ON calibration_session_events(OperationId,Position);
        CREATE TABLE calibration_frame_manifests(
            FrameId TEXT NOT NULL PRIMARY KEY CHECK(length(FrameId)=36),
            SessionId TEXT NOT NULL REFERENCES calibration_sessions(SessionId),
            Position INTEGER NOT NULL UNIQUE CHECK(Position>0),
            SourceHash TEXT NOT NULL CHECK(length(SourceHash)=64),
            RelativePath TEXT NOT NULL CHECK(length(RelativePath)>0),
            ByteLength INTEGER NOT NULL CHECK(ByteLength>0),
            PixelHash TEXT NOT NULL CHECK(length(PixelHash)=64),
            Payload TEXT NOT NULL,
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL CHECK(length(AuditHash)=64));
        CREATE INDEX ix_calibration_frame_manifests_session ON calibration_frame_manifests(SessionId,Position);
        CREATE TRIGGER calibration_store_config_immutable_update BEFORE UPDATE ON calibration_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationStoreConfiguration');
        END;
        CREATE TRIGGER calibration_store_config_immutable_delete BEFORE DELETE ON calibration_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationStoreConfiguration');
        END;
        CREATE TRIGGER calibration_session_immutable_update BEFORE UPDATE ON calibration_sessions BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationSession');
        END;
        CREATE TRIGGER calibration_session_immutable_delete BEFORE DELETE ON calibration_sessions BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationSession');
        END;
        CREATE TRIGGER calibration_session_event_immutable_update BEFORE UPDATE ON calibration_session_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationSessionEvent');
        END;
        CREATE TRIGGER calibration_session_event_immutable_delete BEFORE DELETE ON calibration_session_events BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationSessionEvent');
        END;
        CREATE TRIGGER calibration_frame_manifest_immutable_update BEFORE UPDATE ON calibration_frame_manifests BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationFrameManifest');
        END;
        CREATE TRIGGER calibration_frame_manifest_immutable_delete BEFORE DELETE ON calibration_frame_manifests BEGIN
            SELECT RAISE(ABORT,'ImmutableCalibrationFrameManifest');
        END;";

    private void InitializeCalibrationSessionSchema(sqlite3 database,
        CalibrationSessionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Execute(database, @"INSERT INTO calibration_store_config
            (Id,FormatVersion,MaximumSessions,MaximumEvents,MaximumEventPayloadBytes,
             MaximumFramesPerSession,MaximumFrameBytes,MaximumTotalFrameBytes,EvidenceRoot,BindingHash)
            VALUES(1,?,?,?,?,?,?,?,?,?);", deadline,
            CalibrationSessionStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumSessions.ToString(CultureInfo.InvariantCulture),
            options.MaximumEvents.ToString(CultureInfo.InvariantCulture),
            options.MaximumEventPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumFramesPerSession.ToString(CultureInfo.InvariantCulture),
            options.MaximumFrameBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalFrameBytes.ToString(CultureInfo.InvariantCulture),
            options.EvidenceRoot, options.BindingHash);
        _ = AuditChainDatabase.AppendCalibrationStoreActivation(database, _policy!, _signingKey!,
            options, deadline);
    }

    internal static void RequireConfiguredCalibrationSessions(sqlite3 database,
        CalibrationSessionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var configs = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumSessions,
            MaximumEvents,MaximumEventPayloadBytes,MaximumFramesPerSession,MaximumFrameBytes,
            MaximumTotalFrameBytes,EvidenceRoot,BindingHash
            FROM calibration_store_config WHERE Id=1;", deadline, statement =>
            (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumSessions: SqliteNative.ColumnInt64(statement, 1),
                MaximumEvents: SqliteNative.ColumnInt64(statement, 2),
                MaximumEventPayloadBytes: SqliteNative.ColumnInt64(statement, 3),
                MaximumFramesPerSession: SqliteNative.ColumnInt64(statement, 4),
                MaximumFrameBytes: SqliteNative.ColumnInt64(statement, 5),
                MaximumTotalFrameBytes: SqliteNative.ColumnInt64(statement, 6),
                EvidenceRoot: SqliteNative.ColumnText(statement, 7),
                BindingHash: SqliteNative.ColumnText(statement, 8)) ).ToArray();
        AuditChainDatabase.Require(configs.Length == 1 &&
            configs[0].FormatVersion == CalibrationSessionStoreOptions.FormatVersion &&
            configs[0].MaximumSessions == options.MaximumSessions &&
            configs[0].MaximumEvents == options.MaximumEvents &&
            configs[0].MaximumEventPayloadBytes == options.MaximumEventPayloadBytes &&
            configs[0].MaximumFramesPerSession == options.MaximumFramesPerSession &&
            configs[0].MaximumFrameBytes == options.MaximumFrameBytes &&
            configs[0].MaximumTotalFrameBytes == options.MaximumTotalFrameBytes &&
            configs[0].EvidenceRoot == options.EvidenceRoot &&
            configs[0].BindingHash == options.BindingHash, "CalibrationConfigurationMismatch");
    }

    internal static void VerifyCalibrationStoreActivationPayload(sqlite3 database, byte[] payload,
        CalibrationSessionStoreOptions options, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(payload.SequenceEqual(options.EncodeActivationPayload()),
            "CalibrationActivationMismatch");
        RequireConfiguredCalibrationSessions(database, options, deadline);
    }
}
