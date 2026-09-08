using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Internal, Engine-produced archive input. It is never a public mutation DTO.</summary>
internal sealed record AlgorithmResultArchiveDocument(
    Guid RecordId,
    DateTimeOffset RecordedAtUtc,
    Guid PreparedInstanceId,
    AlgorithmIdentity Algorithm,
    ExecutionCorrelationId Correlation,
    string ConfigurationContentHash,
    string ConfigurationSchemaId,
    string ConfigurationSchemaVersion,
    string ConfigurationSchemaContentHash,
    FrameMetadata FrameMetadata,
    AlgorithmResultSchema ResultSchema,
    AlgorithmResult Result,
    AlgorithmExecutionTimingSnapshot Timing,
    long AdmittedMonotonicTimestamp,
    long MonotonicFrequency,
    string PayloadJson,
    string PayloadHash);

internal sealed partial class SqliteCommandStore
{
    private const string AlgorithmResultSchemaSql = @"
        CREATE TABLE algorithm_result_archive_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumRecordBytes INTEGER NOT NULL,
            MaximumTotalBytes INTEGER NOT NULL,
            MaximumRecords INTEGER NOT NULL,
            MaximumPageBytes INTEGER NOT NULL,
            ContentHash TEXT NOT NULL CHECK(length(ContentHash)=64));
        CREATE TABLE development_algorithm_results(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            RecordId TEXT NOT NULL UNIQUE CHECK(length(RecordId)=36),
            RecordedAtUtc TEXT NOT NULL,
            PreparedInstanceId TEXT NOT NULL CHECK(length(PreparedInstanceId)=36),
            CorrelationKind INTEGER NOT NULL CHECK(CorrelationKind IN (1,2)),
            CorrelationId TEXT NOT NULL CHECK(length(CorrelationId)=36),
            AlgorithmId TEXT NOT NULL,
            AlgorithmVersion TEXT NOT NULL,
            ConfigurationContentHash TEXT NOT NULL CHECK(length(ConfigurationContentHash)=64),
            ConfigurationSchemaId TEXT NOT NULL,
            ConfigurationSchemaVersion TEXT NOT NULL,
            ConfigurationSchemaContentHash TEXT NOT NULL CHECK(length(ConfigurationSchemaContentHash)=64),
            ResultSchemaId TEXT NOT NULL,
            ResultSchemaVersion TEXT NOT NULL,
            ResultSchemaContentHash TEXT NOT NULL CHECK(length(ResultSchemaContentHash)=64),
            OverlayContractId TEXT NOT NULL,
            OverlayContractVersion TEXT NOT NULL,
            OverlayContractContentHash TEXT NOT NULL CHECK(length(OverlayContractContentHash)=64),
            ExecutionStatus INTEGER NOT NULL CHECK(ExecutionStatus=0),
            Decision INTEGER NOT NULL CHECK(Decision IN (0,1,2)),
            ReasonCode TEXT NULL,
            FrameWidth INTEGER NOT NULL,
            FrameHeight INTEGER NOT NULL,
            FrameStrideBytes INTEGER NOT NULL,
            FrameValidBits INTEGER NULL,
            FramePixelFormat INTEGER NOT NULL,
            DevelopmentOnly INTEGER NOT NULL CHECK(DevelopmentOnly=1),
            RecordKind TEXT NOT NULL CHECK(RecordKind='DevelopmentComputation'),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            PayloadJson TEXT NOT NULL,
            UNIQUE(CorrelationKind,CorrelationId));
        CREATE INDEX ix_algorithm_result_correlation ON development_algorithm_results(CorrelationKind,CorrelationId,Position);
        CREATE INDEX ix_algorithm_result_recorded ON development_algorithm_results(RecordedAtUtc,Position);
        CREATE TRIGGER algorithm_result_archive_config_immutable_update BEFORE UPDATE ON algorithm_result_archive_config BEGIN
            SELECT RAISE(ABORT, 'ImmutableAlgorithmResultArchiveConfig');
        END;
        CREATE TRIGGER algorithm_result_archive_config_immutable_delete BEFORE DELETE ON algorithm_result_archive_config BEGIN
            SELECT RAISE(ABORT, 'ImmutableAlgorithmResultArchiveConfig');
        END;
        CREATE TRIGGER development_algorithm_results_immutable_update BEFORE UPDATE ON development_algorithm_results BEGIN
            SELECT RAISE(ABORT, 'ImmutableAlgorithmResult');
        END;
        CREATE TRIGGER development_algorithm_results_immutable_delete BEFORE DELETE ON development_algorithm_results BEGIN
            SELECT RAISE(ABORT, 'ImmutableAlgorithmResult');
        END;";

    internal ValueTask<StoreWriteResult> AppendAlgorithmResultAsync(AlgorithmResultArchiveDocument document,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(deadline);
        return EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline, AlgorithmResult: document),
            deadline, cancellationToken, "AlgorithmResultArchiveUnavailable", "AlgorithmResultCommitDeadlineExceeded");
    }

    private void InitializeAlgorithmResultSchema(SQLitePCL.sqlite3 database,
        AlgorithmResultArchiveOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var existing = ReadArchiveConfig(database, deadline);
        if (existing is not null)
        {
            RequireArchiveConfig(existing, options);
            return;
        }

        AuditChainDatabase.Execute(database, @"INSERT INTO algorithm_result_archive_config
            (Id,FormatVersion,MaximumRecordBytes,MaximumTotalBytes,MaximumRecords,MaximumPageBytes,ContentHash)
            VALUES(1,1,?,?,?,?,?);", deadline,
            options.MaximumRecordBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumRecords.ToString(CultureInfo.InvariantCulture),
            options.MaximumPageBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendAlgorithmArchiveActivation(database, _policy!, _signingKey!, options, deadline);
    }

    internal static byte[] ReadAuditBindingPayload(SQLitePCL.sqlite3 database, long position, StoreDeadline deadline)
    {
        var row = ReadArchiveRow(database, position, deadline);
        AuditChainDatabase.Require(row is not null, "AlgorithmResultMissing");
        return EncodeBinding(row!);
    }

    internal static byte[] ReadAndValidate(SQLitePCL.sqlite3 database, long position, byte[] signedPayload,
        StoreDeadline deadline, AlgorithmResultArchiveOptions? archiveOptions = null)
    {
        var row = ReadArchiveRow(database, position, deadline);
        AuditChainDatabase.Require(row is not null, "AlgorithmResultMissing");
        var binding = EncodeBinding(row!);
        AuditChainDatabase.Require(binding.SequenceEqual(signedPayload), "AlgorithmResultBindingMismatch");
        AuditChainDatabase.Require(row!.PayloadJson is { Length: > 0 }, "AlgorithmResultPayloadMissing");
        var payloadBytes = Encoding.UTF8.GetBytes(row.PayloadJson);
        var maximumRecordBytes = archiveOptions?.MaximumRecordBytes ??
            AlgorithmResultArchiveOptions.DefaultMaximumRecordBytes;
        AuditChainDatabase.Require(payloadBytes.Length <= maximumRecordBytes,
            "AlgorithmResultPayloadOversized");
        var hash = Convert.ToHexString(SHA256.HashData(payloadBytes));
        AuditChainDatabase.Require(string.Equals(hash, row.PayloadHash, StringComparison.Ordinal),
            "AlgorithmResultPayloadHashMismatch");
        var decoded = AlgorithmResultStorageCodec.Decode(position, ParseTime(row.RecordedAtUtc),
            row.PayloadJson, row.PayloadHash);
        ValidateDecodedRow(row!, decoded);
        return binding;
    }

    private static void ValidateDecodedRow(ArchiveRow row, AlgorithmResultRecord decoded)
    {
        AuditChainDatabase.Require(decoded.Position == row.Position,
            "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(Guid.TryParseExact(row.RecordId, "D", out var recordId) &&
            recordId == decoded.RecordId, "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(Guid.TryParseExact(row.PreparedInstanceId, "D", out var preparedInstanceId) &&
            preparedInstanceId == decoded.PreparedInstanceId, "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(Guid.TryParseExact(row.CorrelationId, "D", out var correlationId) &&
            correlationId == decoded.Correlation.Value &&
            row.CorrelationKind == (long)decoded.Correlation.Kind, "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(ParseTime(row.RecordedAtUtc) == decoded.RecordedAtUtc,
            "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(row.AlgorithmId == decoded.Algorithm.Id &&
            row.AlgorithmVersion == decoded.Algorithm.Version &&
            row.ConfigurationContentHash == decoded.ConfigurationContentHash &&
            row.ConfigurationSchemaId == decoded.ConfigurationSchemaId &&
            row.ConfigurationSchemaVersion == decoded.ConfigurationSchemaVersion &&
            row.ConfigurationSchemaContentHash == decoded.ConfigurationSchemaContentHash,
            "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(row.ResultSchemaId == decoded.ResultSchema.Id &&
            row.ResultSchemaVersion == decoded.ResultSchema.Version &&
            row.ResultSchemaContentHash == decoded.ResultSchema.ContentHash &&
            row.OverlayContractId == decoded.ResultSchema.OverlayContract.Id &&
            row.OverlayContractVersion == decoded.ResultSchema.OverlayContract.Version &&
            row.OverlayContractContentHash == decoded.ResultSchema.OverlayContract.ContentHash &&
            decoded.Result.OverlaySet.ContractId == row.OverlayContractId &&
            decoded.Result.OverlaySet.ContractVersion == row.OverlayContractVersion,
            "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(row.ExecutionStatus == (long)ExecutionStatus.Success &&
            decoded.ExecutionStatus == ExecutionStatus.Success &&
            row.Decision == (long)decoded.Decision &&
            row.ReasonCode == decoded.ReasonCode &&
            row.DevelopmentOnly == 1 && decoded.DevelopmentOnly &&
            row.RecordKind == "DevelopmentComputation" &&
            row.PayloadHash == decoded.ContentHash,
            "AlgorithmResultIndexPayloadMismatch");
        AuditChainDatabase.Require(row.FrameWidth == decoded.FrameMetadata.Width &&
            row.FrameHeight == decoded.FrameMetadata.Height &&
            row.FrameStrideBytes == decoded.FrameMetadata.StrideBytes &&
            row.FramePixelFormat == (int)decoded.FrameMetadata.PixelFormat &&
            TryParseValidBits(row.FrameValidBits, out var rowValidBits) &&
            rowValidBits == decoded.FrameMetadata.ValidBits,
            "AlgorithmResultIndexPayloadMismatch");
    }

    private static bool TryParseValidBits(string? value, out int? validBits)
    {
        if (value is null)
        {
            validBits = null;
            return true;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            validBits = parsed;
            return true;
        }

        validBits = null;
        return false;
    }

    internal static void VerifyActivationPayload(SQLitePCL.sqlite3 database, byte[] payload,
        AlgorithmResultArchiveOptions options, StoreDeadline deadline)
    {
        options.Validate();
        AuditChainDatabase.Require(payload.SequenceEqual(options.EncodeActivationPayload()),
            "AlgorithmResultArchiveActivationMismatch");
        var config = ReadArchiveConfig(database, deadline);
        AuditChainDatabase.Require(config is not null, "AlgorithmResultArchiveConfigurationMissing");
        RequireArchiveConfig(config!, options);
    }

    internal static void RequireConfiguredArchive(SQLitePCL.sqlite3 database,
        AlgorithmResultArchiveOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var config = ReadArchiveConfig(database, deadline);
        AuditChainDatabase.Require(config is not null, "AlgorithmResultArchiveConfigurationMissing");
        RequireArchiveConfig(config!, options);
    }

    private static void RequireArchiveConfig(ArchiveConfig config, AlgorithmResultArchiveOptions options)
    {
        AuditChainDatabase.Require(config.FormatVersion == 1 &&
            config.MaximumRecordBytes == options.MaximumRecordBytes &&
            config.MaximumTotalBytes == options.MaximumTotalBytes &&
            config.MaximumRecords == options.MaximumRecords &&
            config.MaximumPageBytes == options.MaximumPageBytes &&
            string.Equals(config.ContentHash, options.BindingHash, StringComparison.Ordinal),
            "AlgorithmResultArchiveConfigurationMismatch");
    }

    private static ArchiveConfig? ReadArchiveConfig(SQLitePCL.sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumRecordBytes,MaximumTotalBytes,
            MaximumRecords,MaximumPageBytes,ContentHash FROM algorithm_result_archive_config LIMIT 2;", deadline,
            statement => new ArchiveConfig(
                checked((int)SqliteNative.ColumnInt64(statement, 0)),
                checked((int)SqliteNative.ColumnInt64(statement, 1)),
                SqliteNative.ColumnInt64(statement, 2),
                checked((int)SqliteNative.ColumnInt64(statement, 3)),
                checked((int)SqliteNative.ColumnInt64(statement, 4)),
                SqliteNative.ColumnText(statement, 5)!)).SingleOrDefault();

    private StoreWriteResult AppendAlgorithmResultCore(SQLitePCL.sqlite3 database,
        AlgorithmResultArchiveDocument document, StoreDeadline deadline)
    {
        // The codec timestamp is only a temporary transport value.  The writer owns
        // the durable observation time and the payload deliberately contains no clock
        // value, so idempotent retries still use the same payload hash.
        document = document with { RecordedAtUtc = DateTimeOffset.UtcNow };
        try { ValidateDocument(document, _options.AlgorithmResultArchive!); }
        catch (InvalidOperationException ex) { return new StoreWriteResult(false, ex.Message); }
        if (_policy is not null)
        {
            var integrity = Integrity;
            if (integrity?.State == AuditIntegrityState.Faulted)
                return new StoreWriteResult(false, integrity.ReasonCode);
            if (integrity?.State != AuditIntegrityState.Verified)
                return new StoreWriteResult(false, integrity?.ReasonCode ?? "AuditIntegrityUnavailable",
                    RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        }
        if (_walLimitExceeded || GetWalLength() > 64L * 1024 * 1024)
            return new StoreWriteResult(false, "TraceStoreWalLimit");

        var started = false;
        var committed = false;
        try
        {
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            var alarmStore = _options.AlarmPolicy is not null;
            var draftStore = _options.RecipeDrafts is not null;
            AuditIntegrityReport verification;
            try
            {
                verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                    _signingKey.PublicKeyBase64,
                    new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries),
                    false, deadline, validateAnchorReceipt: false, archiveOptions: _options.AlgorithmResultArchive,
                    recipeDraftOptions: _options.RecipeDrafts, cameraSetupOptions: _options.CameraSetup,
                    cameraRecoveryOptions: _options.CameraRecovery, cameraNetworkOptions: _options.CameraNetwork,
                    imagingSetupOptions: _options.ImagingSetup,
                    calibrationSessionOptions: _options.CalibrationSessions,
                governanceOptions: _options.CalibrationGovernance);
                if (alarmStore) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
                AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
                if (draftStore) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                    _options.RecipeDrafts);
                if (_options.CameraSetup is not null)
                    AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                        _options.CameraSetup);
                if (_options.CameraRecovery is not null)
                    AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                        _options.CameraRecovery);
                if (_options.CameraNetwork is not null)
                    AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                        _options.CameraNetwork);
                if (_options.ImagingSetup is not null)
                    AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                        _options.ImagingSetup);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var reason = SqliteAuditIntegrityQuery.FaultReason(ex, "AlgorithmResultHistoryUnavailable");
                if (!AuditChainDatabase.IsCapacityReason(reason))
                    SetIntegrityFault(reason, IsStructuralFault(reason));
                return new StoreWriteResult(false, reason);
            }

            var existing = ReadArchiveRowByRecordId(database, document.RecordId, deadline);
            if (existing is not null)
            {
                if (string.Equals(existing.PayloadHash, document.PayloadHash, StringComparison.Ordinal))
                    return new StoreWriteResult(true, "AlgorithmResultAlreadyPersisted");
                return new StoreWriteResult(false, "AlgorithmResultRecordIdConflict");
            }
            if (Exists(database, "SELECT 1 FROM development_algorithm_results WHERE CorrelationKind=? AND CorrelationId=? LIMIT 1;",
                    ((int)document.Correlation.Kind).ToString(CultureInfo.InvariantCulture), document.Correlation.Value.ToString("D"), deadline))
                return new StoreWriteResult(false, "AlgorithmResultCorrelationConflict");

            AuditChainDatabase.EnsureNextSequenceAvailable(database, _policy!, deadline, archiveData: true);
            var count = AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM development_algorithm_results;", deadline);
            var totalBytes = AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(SUM(length(CAST(PayloadJson AS BLOB))),0) FROM development_algorithm_results;", deadline);
            var archiveOptions = _options.AlgorithmResultArchive!;
            if (count >= archiveOptions.MaximumRecords || totalBytes > archiveOptions.MaximumTotalBytes -
                Encoding.UTF8.GetByteCount(document.PayloadJson))
                return new StoreWriteResult(false, "AlgorithmResultArchiveCapacityExceeded");

            var position = checked(AuditChainDatabase.Scalar(database,
                "SELECT COALESCE(MAX(Position),0) FROM development_algorithm_results;", deadline) + 1);
            InsertArchiveRow(database, position, document, deadline);
            AuditChainDatabase.AppendAlgorithmComputation(database, _policy!, _signingKey!, position, deadline);
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying,
                _policy!.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, "AlgorithmResultPersisted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        {
            return new StoreWriteResult(false, ex.Message);
        }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private static void ValidateDocument(AlgorithmResultArchiveDocument document,
        AlgorithmResultArchiveOptions options)
    {
        if (document.RecordId == Guid.Empty || document.PreparedInstanceId == Guid.Empty)
            throw new InvalidOperationException("AlgorithmResultIdentityInvalid");
        if (document.Correlation is null || document.Correlation.Value == Guid.Empty ||
            document.Correlation.Kind == ExecutionKind.Production ||
            !Enum.IsDefined(typeof(ExecutionKind), document.Correlation.Kind))
            throw new InvalidOperationException("AlgorithmResultProductionOrCorrelationInvalid");
        if (document.FrameMetadata is null || document.FrameMetadata.Correlation != document.Correlation)
            throw new InvalidOperationException("AlgorithmResultFrameBindingMismatch");
        if (document.Result is null || document.ResultSchema is null || document.Algorithm is null ||
            document.Timing is null || document.PayloadJson is null || document.PayloadHash is null)
            throw new InvalidOperationException("AlgorithmResultDocumentInvalid");
        var bytes = Encoding.UTF8.GetBytes(document.PayloadJson);
        if (bytes.Length is < 1 || bytes.Length > options.MaximumRecordBytes)
            throw new InvalidOperationException("AlgorithmResultPayloadOversized");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), document.PayloadHash,
                StringComparison.Ordinal))
            throw new InvalidOperationException("AlgorithmResultPayloadHashMismatch");
        if (document.RecordedAtUtc == default || document.AdmittedMonotonicTimestamp < 0 ||
            document.MonotonicFrequency < 1)
            throw new InvalidOperationException("AlgorithmResultTimingInvalid");
        var decoded = AlgorithmResultStorageCodec.Decode(0, document.RecordedAtUtc,
            document.PayloadJson, document.PayloadHash);
        ValidateDecodedRow(ToArchiveRow(0, document), decoded);
    }

    private static ArchiveRow ToArchiveRow(long position, AlgorithmResultArchiveDocument document) => new(
        position, document.RecordId.ToString("D"), FormatTime(document.RecordedAtUtc),
        document.PreparedInstanceId.ToString("D"), (long)document.Correlation.Kind,
        document.Correlation.Value.ToString("D"), document.Algorithm.Id, document.Algorithm.Version,
        document.ConfigurationContentHash, document.ConfigurationSchemaId, document.ConfigurationSchemaVersion,
        document.ConfigurationSchemaContentHash, document.ResultSchema.Id, document.ResultSchema.Version,
        document.ResultSchema.ContentHash, document.ResultSchema.OverlayContract.Id,
        document.ResultSchema.OverlayContract.Version, document.ResultSchema.OverlayContract.ContentHash,
        (long)ExecutionStatus.Success, (long)document.Result.Decision, document.Result.ReasonCode,
        document.FrameMetadata.Width, document.FrameMetadata.Height, document.FrameMetadata.StrideBytes,
        document.FrameMetadata.ValidBits?.ToString(CultureInfo.InvariantCulture),
        (int)document.FrameMetadata.PixelFormat, 1, "DevelopmentComputation", document.PayloadHash,
        document.PayloadJson);

    private static void InsertArchiveRow(SQLitePCL.sqlite3 database, long position,
        AlgorithmResultArchiveDocument document, StoreDeadline deadline)
    {
        SqliteNative.WithStatement(database, @"INSERT INTO development_algorithm_results
            (Position,RecordId,RecordedAtUtc,PreparedInstanceId,CorrelationKind,CorrelationId,
             AlgorithmId,AlgorithmVersion,ConfigurationContentHash,ConfigurationSchemaId,ConfigurationSchemaVersion,
             ConfigurationSchemaContentHash,ResultSchemaId,ResultSchemaVersion,ResultSchemaContentHash,
             OverlayContractId,OverlayContractVersion,OverlayContractContentHash,ExecutionStatus,Decision,ReasonCode,
             FrameWidth,FrameHeight,FrameStrideBytes,FrameValidBits,FramePixelFormat,DevelopmentOnly,RecordKind,PayloadHash,PayloadJson)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline, statement =>
        {
            var i = 1;
            SqliteNative.BindInt64(database, statement, i++, position);
            SqliteNative.BindGuid(database, statement, i++, document.RecordId);
            SqliteNative.BindText(database, statement, i++, FormatTime(document.RecordedAtUtc));
            SqliteNative.BindGuid(database, statement, i++, document.PreparedInstanceId);
            SqliteNative.BindInt(database, statement, i++, (int)document.Correlation.Kind);
            SqliteNative.BindGuid(database, statement, i++, document.Correlation.Value);
            SqliteNative.BindText(database, statement, i++, document.Algorithm.Id);
            SqliteNative.BindText(database, statement, i++, document.Algorithm.Version);
            SqliteNative.BindText(database, statement, i++, document.ConfigurationContentHash);
            SqliteNative.BindText(database, statement, i++, document.ConfigurationSchemaId);
            SqliteNative.BindText(database, statement, i++, document.ConfigurationSchemaVersion);
            SqliteNative.BindText(database, statement, i++, document.ConfigurationSchemaContentHash);
            SqliteNative.BindText(database, statement, i++, document.ResultSchema.Id);
            SqliteNative.BindText(database, statement, i++, document.ResultSchema.Version);
            SqliteNative.BindText(database, statement, i++, document.ResultSchema.ContentHash);
            SqliteNative.BindText(database, statement, i++, document.ResultSchema.OverlayContract.Id);
            SqliteNative.BindText(database, statement, i++, document.ResultSchema.OverlayContract.Version);
            SqliteNative.BindText(database, statement, i++, document.ResultSchema.OverlayContract.ContentHash);
            SqliteNative.BindInt(database, statement, i++, 0);
            SqliteNative.BindInt(database, statement, i++, (int)document.Result.Decision);
            SqliteNative.BindText(database, statement, i++, document.Result.ReasonCode);
            SqliteNative.BindInt(database, statement, i++, document.FrameMetadata.Width);
            SqliteNative.BindInt(database, statement, i++, document.FrameMetadata.Height);
            SqliteNative.BindInt(database, statement, i++, document.FrameMetadata.StrideBytes);
            if (document.FrameMetadata.ValidBits is { } bits) SqliteNative.BindInt(database, statement, i++, bits);
            else raw.sqlite3_bind_null(statement, i++);
            SqliteNative.BindInt(database, statement, i++, (int)document.FrameMetadata.PixelFormat);
            SqliteNative.BindInt(database, statement, i++, 1);
            SqliteNative.BindText(database, statement, i++, "DevelopmentComputation");
            SqliteNative.BindText(database, statement, i++, document.PayloadHash);
            SqliteNative.BindText(database, statement, i, document.PayloadJson);
            SqliteNative.Step(database, statement, deadline);
            return 0;
        });
    }

    private static ArchiveRow? ReadArchiveRow(SQLitePCL.sqlite3 database, long position, StoreDeadline deadline) =>
        AuditChainDatabase.Read(database, ArchiveSelect + " WHERE Position=?;", deadline,
            ReadArchiveRow, position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();

    private static ArchiveRow? ReadArchiveRowByRecordId(SQLitePCL.sqlite3 database, Guid recordId, StoreDeadline deadline) =>
        AuditChainDatabase.Read(database, ArchiveSelect + " WHERE RecordId=?;", deadline,
            ReadArchiveRow, recordId.ToString("D")).SingleOrDefault();

    private static ArchiveRow ReadArchiveRow(sqlite3_stmt statement) => new(
        SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!,
        SqliteNative.ColumnText(statement, 2)!, SqliteNative.ColumnText(statement, 3)!,
        SqliteNative.ColumnInt64(statement, 4), SqliteNative.ColumnText(statement, 5)!,
        SqliteNative.ColumnText(statement, 6)!, SqliteNative.ColumnText(statement, 7)!,
        SqliteNative.ColumnText(statement, 8)!, SqliteNative.ColumnText(statement, 9)!,
        SqliteNative.ColumnText(statement, 10)!, SqliteNative.ColumnText(statement, 11)!,
        SqliteNative.ColumnText(statement, 12)!, SqliteNative.ColumnText(statement, 13)!,
        SqliteNative.ColumnText(statement, 14)!, SqliteNative.ColumnText(statement, 15)!,
        SqliteNative.ColumnText(statement, 16)!, SqliteNative.ColumnText(statement, 17)!,
        SqliteNative.ColumnInt64(statement, 18), SqliteNative.ColumnInt64(statement, 19),
        SqliteNative.ColumnText(statement, 20), checked((int)SqliteNative.ColumnInt64(statement, 21)),
        checked((int)SqliteNative.ColumnInt64(statement, 22)), checked((int)SqliteNative.ColumnInt64(statement, 23)),
        SqliteNative.ColumnText(statement, 24), checked((int)SqliteNative.ColumnInt64(statement, 25)),
        SqliteNative.ColumnInt64(statement, 26), SqliteNative.ColumnText(statement, 27)!,
        SqliteNative.ColumnText(statement, 28)!, SqliteNative.ColumnText(statement, 29)!);

    private const string ArchiveSelect = @"SELECT Position,RecordId,RecordedAtUtc,PreparedInstanceId,
        CorrelationKind,CorrelationId,AlgorithmId,AlgorithmVersion,ConfigurationContentHash,
        ConfigurationSchemaId,ConfigurationSchemaVersion,ConfigurationSchemaContentHash,ResultSchemaId,
        ResultSchemaVersion,ResultSchemaContentHash,OverlayContractId,OverlayContractVersion,
        OverlayContractContentHash,ExecutionStatus,Decision,ReasonCode,FrameWidth,FrameHeight,FrameStrideBytes,
        FrameValidBits,FramePixelFormat,DevelopmentOnly,RecordKind,PayloadHash,PayloadJson
        FROM development_algorithm_results";

    private static byte[] EncodeBinding(ArchiveRow row) => AuditCanonical.Encode("AlgorithmComputationBinding",
        row.Position.ToString(CultureInfo.InvariantCulture), row.RecordId, row.RecordedAtUtc, row.PreparedInstanceId,
        row.CorrelationKind.ToString(CultureInfo.InvariantCulture), row.CorrelationId, row.AlgorithmId, row.AlgorithmVersion,
        row.ConfigurationContentHash, row.ConfigurationSchemaId, row.ConfigurationSchemaVersion, row.ConfigurationSchemaContentHash,
        row.ResultSchemaId, row.ResultSchemaVersion, row.ResultSchemaContentHash, row.OverlayContractId,
        row.OverlayContractVersion, row.OverlayContractContentHash, row.ExecutionStatus.ToString(CultureInfo.InvariantCulture),
        row.Decision.ToString(CultureInfo.InvariantCulture), row.ReasonCode, row.FrameWidth.ToString(CultureInfo.InvariantCulture),
        row.FrameHeight.ToString(CultureInfo.InvariantCulture), row.FrameStrideBytes.ToString(CultureInfo.InvariantCulture),
        row.FrameValidBits, row.FramePixelFormat.ToString(CultureInfo.InvariantCulture), row.DevelopmentOnly.ToString(CultureInfo.InvariantCulture),
        row.RecordKind, row.PayloadHash);

    private sealed record ArchiveConfig(int FormatVersion, int MaximumRecordBytes, long MaximumTotalBytes,
        int MaximumRecords, int MaximumPageBytes, string ContentHash);

    private sealed record ArchiveRow(long Position, string RecordId, string RecordedAtUtc, string PreparedInstanceId,
        long CorrelationKind, string CorrelationId, string AlgorithmId, string AlgorithmVersion,
        string ConfigurationContentHash, string ConfigurationSchemaId, string ConfigurationSchemaVersion,
        string ConfigurationSchemaContentHash, string ResultSchemaId, string ResultSchemaVersion,
        string ResultSchemaContentHash, string OverlayContractId, string OverlayContractVersion,
        string OverlayContractContentHash, long ExecutionStatus, long Decision, string? ReasonCode,
        int FrameWidth, int FrameHeight, int FrameStrideBytes, string? FrameValidBits, int FramePixelFormat,
        long DevelopmentOnly, string RecordKind, string PayloadHash, string PayloadJson);

    private static bool Exists(SQLitePCL.sqlite3 database, string sql, string first, string second,
        StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            SqliteNative.BindText(database, statement, 1, first);
            SqliteNative.BindText(database, statement, 2, second);
            return SqliteNative.Step(database, statement, deadline) == raw.SQLITE_ROW;
        });
}
