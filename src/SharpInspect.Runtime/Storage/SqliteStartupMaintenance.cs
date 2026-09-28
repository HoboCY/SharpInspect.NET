using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The bundled, exclusive SQLite 32-to-33 startup migration. All existing
/// configuration remains pinned; only RecipeLifecycle is added. Required remote
/// audit anchoring is not supported by this local migration plan. Hosts must
/// enter maintenance before opening this service and perform normal startup
/// reconciliation afterwards. This service creates no Runtime or production arm.
/// </summary>
public static class SqliteStartupMaintenance
{
    public static ValueTask<StoreStartupMaintenanceOpenResult> OpenAsync(
        ProductionStoreOptions target, StoreStartupMaintenanceOptions maintenance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(maintenance);
        maintenance.Validate();
        var session = new Session(target, maintenance);
        try
        {
            session.Open(cancellationToken);
            return ValueTask.FromResult(new StoreStartupMaintenanceOpenResult(session, session.Status));
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception))
        {
            session.Fail(exception);
            var status = session.Status;
            session.DisposeAsync().GetAwaiter().GetResult();
            return ValueTask.FromResult(new StoreStartupMaintenanceOpenResult(null, status));
        }
        catch
        {
            session.DisposeAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    private static bool IsMaintenanceFailure(Exception exception) => exception is
        InvalidOperationException or IOException or UnauthorizedAccessException or
        SqliteException or SqliteNativeException or OperationCanceledException or
        TimeoutException or CryptographicException or ArgumentException or NotSupportedException or
        StoreMigrationFingerprint.Failure or OverflowException or BadImageFormatException;

    private sealed class Session : IStoreStartupMaintenanceSession
    {
        private readonly object _gate = new();
        private readonly ProductionStoreOptions _targetOptions;
        private readonly StoreStartupMaintenanceOptions _options;
        private string _path = string.Empty;
        private FileStream? _lease;
        private SqliteConnection? _connection;
        private SqliteCommandStore.StartupMaintenanceSchema? _source;
        private SqliteCommandStore.StartupMaintenanceSchema? _target;
        private StoreMigrationJournal? _journal;
        private StoreMigrationJournalData? _data;
        private bool _transaction;
        private bool _contextBound;
        private bool _disposed;
        private StoreMigrationStatus _status = new(Guid.Empty, StoreMigrationPhase.MaintenanceRequired,
            "StoreMigrationNotOpened");

        internal Session(ProductionStoreOptions target, StoreStartupMaintenanceOptions options)
        { _targetOptions = target; _options = options; }

        public StoreMigrationStatus Status { get { lock (_gate) return _status; } }
        private sqlite3 Database => _connection!.Handle!;
        private StoreDeadline Deadline() => new(_options.OperationTimeout);

        internal void Open(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!StoragePathValidator.TryValidate(_targetOptions, out _path, out var reason))
                throw new InvalidOperationException(reason);
            if (_targetOptions.RecipeLifecycle is null)
                throw new InvalidOperationException("StoreMigrationTargetLifecycleRequired");
            if (_targetOptions.AuditIntegrityPolicy is { RequireExternalAnchor: true })
                throw new InvalidOperationException("StoreMigrationExternalAnchorPlanUnsupported");
            _target = new SqliteCommandStore.StartupMaintenanceSchema(_targetOptions);
            _source = new SqliteCommandStore.StartupMaintenanceSchema(SqliteCommandStore.MigrationSourceOptions(_targetOptions));
            if (_source.Version != 32 || _target.Version != 33)
                throw new InvalidOperationException("StoreMigrationPathUnsupported");
            var deadline = Deadline();
            StoreMigrationJournalGuard.ValidatePath(_options.SourceRuntimeAssemblyPath);
            var sourceApplication = ApplicationIdentity(_options.SourceRuntimeAssemblyPath, deadline, token);
            var targetApplication = ApplicationIdentity(typeof(SqliteStartupMaintenance).Assembly.Location, deadline, token);
            _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (!StoreMigrationJournalGuard.Exists(_path))
                throw new InvalidOperationException("StoreMigrationSourceDatabaseMissing");
            RequireFileBudget(_path);
            var journalPath = StoreMigrationJournalGuard.JournalPath(_path);
            var markerPath = StoreMigrationJournalGuard.MarkerPath(_path);
            StoreMigrationJournalGuard.ValidatePath(journalPath);
            StoreMigrationJournalGuard.ValidatePath(markerPath);
            var hasJournal = StoreMigrationJournalGuard.Exists(journalPath);
            var hasMarker = StoreMigrationJournalGuard.Exists(markerPath);
            if (hasMarker != hasJournal)
                throw new InvalidOperationException("StoreMigrationJournalOrMarkerMissing");
            // Unknown versions are observed through a read-only preflight.
            // ReadWrite (never Create) is opened only for a supported generation.
            long version;
            using (var preflight = OpenExisting(_path, readOnly: true))
                version = AuditChainDatabase.Scalar(preflight.Handle!, "PRAGMA user_version;", deadline);
            if (version is not (32 or 33) || !hasJournal && version != 32)
                throw new InvalidOperationException(version > 33
                    ? "StoreMigrationNewerSchemaUnsupported" : "StoreMigrationPathUnsupported");
            _connection = OpenExisting(_path, readOnly: false);
            if (!hasJournal)
            {
                var operation = Guid.NewGuid();
                // The permanent marker is flushed first. A crash before a
                // complete journal is explicit maintenance, never clean startup.
                StoreMigrationJournalGuard.CreateMarker(_path, operation);
                _journal = new StoreMigrationJournal(journalPath, _options.MaximumJournalBytes, create: true);
                _contextBound = true;
                Record(new StoreMigrationJournalData
                {
                    OperationId = operation, Phase = StoreMigrationPhase.Opened, DatabasePath = _path,
                    SourceApplicationPath = _options.SourceRuntimeAssemblyPath,
                    SourceApplicationVersion = sourceApplication.Version, SourceApplicationSha256 = sourceApplication.Hash,
                    TargetApplicationVersion = targetApplication.Version, TargetApplicationSha256 = targetApplication.Hash,
                    LifecycleConfigurationHash = StoreMigrationJournalGuard.LifecycleHash(_targetOptions.RecipeLifecycle),
                    ReasonCode = "StoreMigrationOpened"
                });
                _source.ConfigureConnection(Database, deadline);
                return;
            }
            _journal = new StoreMigrationJournal(journalPath, _options.MaximumJournalBytes, create: false);
            _data = _journal.Last?.Data ?? throw new InvalidOperationException("StoreMigrationJournalEmpty");
            if (_data.OperationId != StoreMigrationJournalGuard.ReadMarker(_path) ||
                !string.Equals(_data.DatabasePath, _path, StringComparison.OrdinalIgnoreCase) ||
                _data.SourceApplicationPath != _options.SourceRuntimeAssemblyPath ||
                _data.SourceApplicationVersion != sourceApplication.Version || _data.SourceApplicationSha256 != sourceApplication.Hash ||
                _data.TargetApplicationVersion != targetApplication.Version || _data.TargetApplicationSha256 != targetApplication.Hash ||
                _data.LifecycleConfigurationHash != StoreMigrationJournalGuard.LifecycleHash(_targetOptions.RecipeLifecycle))
                throw new InvalidOperationException("StoreMigrationResumeContextMismatch");
            _contextBound = true;
            _status = StatusFor(_journal.Last!);
            using var interrupt = token.Register(() => raw.sqlite3_interrupt(Database));
            if (_data.Phase == StoreMigrationPhase.Completed)
            {
                StoreMigrationJournalGuard.RequireCompletedLineage(_data, _data.OperationId, _targetOptions, _path);
                _target.ConfigureConnection(Database, deadline);
                Verify(_target, _connection, deadline, token);
                return;
            }
            if (_data.BackupVerified) VerifyBackup(_data, deadline, token);
            if (version == 32)
            {
                if (_data.DatabaseCommitObserved)
                    throw new InvalidOperationException("StoreMigrationCommittedGenerationMissing");
                _source.ConfigureConnection(Database, deadline);
                var proof = VerifySource(deadline, token);
                if (_data.SourceFingerprint is not null && _data.SourceFingerprint != proof.Fingerprint.ContentHash)
                    throw new InvalidOperationException("StoreMigrationSourceChangedDuringInterruption");
                Record(SourceProof(_data with
                {
                    Attempt = _data.Phase == StoreMigrationPhase.Opened ? _data.Attempt : checked(_data.Attempt + 1),
                    Phase = StoreMigrationPhase.SourceVerified,
                    BackupPath = null, BackupCreatedAtUtc = null, BackupByteLength = null,
                    BackupSha256 = null, BackupVerified = false,
                    TargetFingerprint = null, TargetAuditSequence = null, TargetAuditHash = null,
                    CommitIntentDurable = false, DatabaseCommitObserved = false,
                    ReasonCode = "StoreMigrationSourceReverifiedAfterInterruption"
                }, proof));
                return;
            }
            if (!_data.CommitIntentDurable || _data.TargetFingerprint is null)
                throw new InvalidOperationException("StoreMigrationCommitOutcomeAmbiguous");
            _target.ConfigureConnection(Database, deadline);
            VerifyTarget(deadline, token, requireRecordedFingerprint: true);
            if (_data.Phase != StoreMigrationPhase.DatabaseCommitted)
                Record(_data with { Phase = StoreMigrationPhase.DatabaseCommitted, DatabaseCommitObserved = true,
                    ReasonCode = "StoreMigrationCommittedGenerationRecovered" });
        }

        public ValueTask<StoreMigrationStatus> AdvanceAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Session));
                if (_status.Phase is StoreMigrationPhase.Completed or StoreMigrationPhase.MaintenanceRequired)
                    return ValueTask.FromResult(_status);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var deadline = Deadline();
                    // The cold reopen phase replaces the connection. Its native
                    // helpers and fingerprint reader use their bounded deadlines;
                    // never interrupt a handle concurrently with closing it.
                    using var interrupt = _data!.Phase == StoreMigrationPhase.DatabaseCommitted
                        ? default(CancellationTokenRegistration)
                        : cancellationToken.Register(() => raw.sqlite3_interrupt(Database));
                    Advance(deadline, cancellationToken);
                }
                catch (Exception exception) when (IsMaintenanceFailure(exception)) { Fail(exception); }
                return ValueTask.FromResult(_status);
            }
        }

        private void Advance(StoreDeadline deadline, CancellationToken token)
        {
            var data = _data!;
            var next = data with { Phase = data.Phase + 1, ReasonCode = "StoreMigration" + (data.Phase + 1) };
            switch (data.Phase)
            {
                case StoreMigrationPhase.Opened:
                    next = SourceProof(next, VerifySource(deadline, token));
                    break;
                case StoreMigrationPhase.SourceVerified:
                    Checkpoint(deadline, token);
                    break;
                case StoreMigrationPhase.CheckpointVerified:
                    next = next with { BackupPath = StoreMigrationJournalGuard.BackupPath(_path, data.OperationId, data.Attempt) };
                    StoreMigrationJournalGuard.ValidatePath(next.BackupPath!);
                    if (StoreMigrationJournalGuard.Exists(next.BackupPath!))
                        throw new InvalidOperationException("StoreMigrationBackupAlreadyExists");
                    break;
                case StoreMigrationPhase.BackupStarted:
                    CreateBackup(data.BackupPath!, deadline, token);
                    next = next with { BackupCreatedAtUtc = DateTimeOffset.UtcNow,
                        BackupByteLength = new FileInfo(data.BackupPath!).Length,
                        BackupSha256 = FileHash(data.BackupPath!, _options.MaximumDatabaseBytes, deadline, token) };
                    break;
                case StoreMigrationPhase.BackupCreated:
                    VerifyBackup(data, deadline, token);
                    next = next with { BackupVerified = true };
                    break;
                case StoreMigrationPhase.BackupVerified:
                    // Recheck immediately before opening the only mutation transaction.
                    if (VerifySource(deadline, token).Fingerprint.ContentHash != data.SourceFingerprint)
                        throw new InvalidOperationException("StoreMigrationSourceChangedBeforeTransaction");
                    SqliteNative.Execute(Database, "BEGIN IMMEDIATE;", deadline, token);
                    _transaction = true;
                    break;
                case StoreMigrationPhase.TransactionStarted:
                    _source!.CaptureConstraintTables(Database, deadline);
                    break;
                case StoreMigrationPhase.TablesCaptured:
                    _target!.RebuildConstraintTables(Database, deadline);
                    break;
                case StoreMigrationPhase.TablesRebuilt:
                    _target!.AddLifecycle(Database, deadline);
                    break;
                case StoreMigrationPhase.FeatureInitialized:
                    var proof = VerifyTarget(deadline, token, requireRecordedFingerprint: false);
                    next = next with { TargetFingerprint = proof.Fingerprint.ContentHash,
                        TargetAuditSequence = proof.Sequence, TargetAuditHash = proof.Hash };
                    break;
                case StoreMigrationPhase.TargetVerified:
                    // This frame is flushed before COMMIT can be attempted.
                    VerifyBackup(data, deadline, token);
                    next = next with { CommitIntentDurable = true };
                    break;
                case StoreMigrationPhase.CommitIntent:
                    SqliteNative.Execute(Database, "COMMIT;", deadline, token);
                    _transaction = false;
                    next = next with { DatabaseCommitObserved = true };
                    break;
                case StoreMigrationPhase.DatabaseCommitted:
                    // Cold reopen, actual checkpoint and full target proof before
                    // recording completion; never infer success from COMMIT alone.
                    _connection!.Dispose();
                    _connection = OpenExisting(_path, readOnly: false);
                    _target!.ConfigureConnection(Database, deadline);
                    VerifyTarget(deadline, token, requireRecordedFingerprint: true);
                    Checkpoint(deadline, token);
                    next = next with { ReasonCode = "SchemaMigratedStartupReconciliationRequired" };
                    break;
                default: throw new InvalidOperationException("StoreMigrationPhaseUnsupported");
            }
            token.ThrowIfCancellationRequested();
            if (deadline.Expired) throw new TimeoutException("StoreMigrationDeadlineExceeded");
            Record(next);
        }

        private (MigrationDatabaseFingerprint Fingerprint, long Sequence, string Hash) VerifySource(
            StoreDeadline deadline, CancellationToken token)
        {
            var proof = Verify(_source!, _connection!, deadline, token);
            _source!.RequireQuiescent(Database, deadline);
            return proof;
        }

        private (MigrationDatabaseFingerprint Fingerprint, long Sequence, string Hash) VerifyTarget(
            StoreDeadline deadline, CancellationToken token, bool requireRecordedFingerprint)
        {
            var proof = Verify(_target!, _connection!, deadline, token);
            if (!StoreMigrationFingerprint.ExistingRowsUnchanged(Database, _data!.SourceTables!,
                    _options.MaximumDatabaseBytes, deadline, token))
                throw new InvalidOperationException("StoreMigrationImmutableRowsChanged");
            var sourceAudit = AuditChainDatabase.Read(Database,
                "SELECT Hash FROM audit_entries WHERE Sequence=?;", deadline,
                statement => SqliteNative.ColumnText(statement, 0),
                _data.SourceAuditSequence!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (sourceAudit.Count != 1 || sourceAudit[0] != _data.SourceAuditHash || proof.Sequence <= _data.SourceAuditSequence)
                throw new InvalidOperationException("StoreMigrationAuditContinuityLost");
            if (requireRecordedFingerprint && (proof.Fingerprint.ContentHash != _data.TargetFingerprint ||
                proof.Sequence != _data.TargetAuditSequence || proof.Hash != _data.TargetAuditHash))
                throw new InvalidOperationException("StoreMigrationTargetChangedDuringInterruption");
            return proof;
        }

        private (MigrationDatabaseFingerprint Fingerprint, long Sequence, string Hash) Verify(
            SqliteCommandStore.StartupMaintenanceSchema schema, SqliteConnection connection,
            StoreDeadline deadline, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var database = connection.Handle!;
            var pages = AuditChainDatabase.Scalar(database, "PRAGMA page_count;", deadline);
            var pageSize = AuditChainDatabase.Scalar(database, "PRAGMA page_size;", deadline);
            if (pages < 1 || pageSize < 512 || checked(pages * pageSize) > _options.MaximumDatabaseBytes)
                throw new InvalidOperationException("StoreMigrationDatabaseCapacityExceeded");
            var check = _options.IntegrityCheck == StoreMigrationIntegrityCheck.Full ? "integrity_check" : "quick_check";
            var result = AuditChainDatabase.Read(database, "PRAGMA " + check + "(1);", deadline,
                statement => SqliteNative.ColumnText(statement, 0));
            if (result.Count != 1 || result[0] != "ok")
                throw new InvalidOperationException("StoreMigrationDatabaseIntegrityFailed");
            var tail = schema.VerifyExisting(connection, deadline);
            return (StoreMigrationFingerprint.Read(database, _options.MaximumDatabaseBytes, deadline, token), tail.Sequence, tail.Hash);
        }

        private void CreateBackup(string path, StoreDeadline deadline, CancellationToken token)
        {
            StoreMigrationJournalGuard.ValidatePath(path);
            // Reserve an empty new destination without replacing evidence from a
            // previous interrupted copy. SQLite, not filesystem DB/WAL copying,
            // produces the consistent snapshot.
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Flush(true);
            using var destination = OpenExisting(path, readOnly: false);
            using var backup = raw.sqlite3_backup_init(destination.Handle!, "main", Database, "main");
            if (backup is null || backup.IsInvalid)
                throw new InvalidOperationException("StoreMigrationBackupOpenFailed");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (deadline.Expired) throw new TimeoutException("StoreMigrationBackupDeadlineExceeded");
                var code = raw.sqlite3_backup_step(backup, 256);
                if (code == raw.SQLITE_DONE) break;
                if (code is not (raw.SQLITE_OK or raw.SQLITE_BUSY or raw.SQLITE_LOCKED))
                    throw new InvalidOperationException("StoreMigrationBackupFailed");
                if (code is raw.SQLITE_BUSY or raw.SQLITE_LOCKED && token.WaitHandle.WaitOne(10))
                    token.ThrowIfCancellationRequested();
                RequireFileBudget(path);
            }
            var finished = raw.sqlite3_backup_finish(backup);
            if (finished != raw.SQLITE_OK) throw new InvalidOperationException("StoreMigrationBackupFinishFailed");
        }

        private void VerifyBackup(StoreMigrationJournalData data, StoreDeadline deadline, CancellationToken token)
        {
            var expectedPath = StoreMigrationJournalGuard.BackupPath(_path, data.OperationId, data.Attempt);
            if (data.BackupPath != expectedPath || data.BackupByteLength is null or < 1 ||
                data.BackupCreatedAtUtc is null || data.BackupSha256 is null)
                throw new InvalidOperationException("StoreMigrationBackupProofMissing");
            StoreMigrationJournalGuard.ValidatePath(expectedPath);
            RequireFileBudget(expectedPath);
            if (new FileInfo(expectedPath).Length != data.BackupByteLength ||
                FileHash(expectedPath, _options.MaximumDatabaseBytes, deadline, token) != data.BackupSha256)
                throw new InvalidOperationException("StoreMigrationBackupHashMismatch");
            using var connection = OpenExisting(expectedPath, readOnly: true);
            // Connection-local policy readback cannot mutate the backup.
            SqliteNative.ConfigureSqliteLimit(connection.Handle!, _source!.Options);
            SqliteNative.Execute(connection.Handle!, "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA wal_autocheckpoint=0;", deadline, token);
            var proof = Verify(_source, connection, deadline, token);
            if (proof.Fingerprint.ContentHash != data.SourceFingerprint ||
                proof.Sequence != data.SourceAuditSequence || proof.Hash != data.SourceAuditHash)
                throw new InvalidOperationException("StoreMigrationBackupLogicalMismatch");
            if (FileHash(expectedPath, _options.MaximumDatabaseBytes, deadline, token) != data.BackupSha256)
                throw new InvalidOperationException("StoreMigrationBackupChangedDuringVerification");
        }

        private void Checkpoint(StoreDeadline deadline, CancellationToken token)
        {
            var result = SqliteNative.WithStatement(Database, "PRAGMA wal_checkpoint(TRUNCATE);", deadline, statement =>
            {
                if (SqliteNative.Step(Database, statement, deadline, token) != raw.SQLITE_ROW)
                    throw new InvalidOperationException("StoreMigrationCheckpointUnobserved");
                var busy = SqliteNative.ColumnInt64(statement, 0);
                var pages = SqliteNative.ColumnInt64(statement, 1);
                var written = SqliteNative.ColumnInt64(statement, 2);
                if (SqliteNative.Step(Database, statement, deadline, token) != raw.SQLITE_DONE)
                    throw new InvalidOperationException("StoreMigrationCheckpointUnobserved");
                return busy == 0 && pages == 0 && written == 0;
            }, token);
            if (!result) throw new InvalidOperationException("StoreMigrationCheckpointBusy");
        }

        private static StoreMigrationJournalData SourceProof(StoreMigrationJournalData data,
            (MigrationDatabaseFingerprint Fingerprint, long Sequence, string Hash) proof) => data with
        {
            SourceFingerprint = proof.Fingerprint.ContentHash, SourceTables = proof.Fingerprint.Tables.ToArray(),
            SourceAuditSequence = proof.Sequence, SourceAuditHash = proof.Hash
        };

        private void Record(StoreMigrationJournalData data)
        {
            var entry = _journal!.Append(data);
            _data = entry.Data;
            _status = StatusFor(entry);
        }

        private static StoreMigrationStatus StatusFor(StoreMigrationJournalEntry entry)
        {
            var data = entry.Data;
            VerifiedStoreMigrationBackup? backup = data.BackupVerified ? new(data.BackupPath!, data.SourceSchemaVersion,
                data.SourceApplicationVersion, data.SourceApplicationSha256, data.BackupCreatedAtUtc!.Value,
                data.BackupByteLength!.Value, data.BackupSha256!, data.SourceFingerprint!) : null;
            return new(data.OperationId, data.Phase, data.ReasonCode, entry.ContentHash, backup);
        }

        internal void Fail(Exception exception)
        {
            var reason = exception switch
            {
                OperationCanceledException => "StoreMigrationCancelled",
                TimeoutException => "StoreMigrationDeadlineExceeded",
                SqliteNativeException native => native.ReasonCode,
                StoreMigrationFingerprint.Failure failure => failure.ReasonCode,
                IOException => "StoreMigrationIoUnavailable",
                UnauthorizedAccessException => "StoreMigrationAccessDenied",
                SqliteException => "StoreMigrationSqliteFailure",
                _ => exception.Message.Length is > 0 and <= 200 && exception.Message.All(char.IsLetterOrDigit)
                    ? exception.Message : "StoreMigrationValidationFailed"
            };
            Rollback();
            if (_contextBound && _journal is not null && _data is not null && _data.Phase != StoreMigrationPhase.Completed)
            {
                try { Record(_data with { Phase = StoreMigrationPhase.MaintenanceRequired, ReasonCode = reason }); }
                catch (Exception journalFailure) when (IsMaintenanceFailure(journalFailure))
                { reason = "StoreMigrationFailureJournalUnavailable"; }
            }
            _status = new StoreMigrationStatus(_data?.OperationId ?? Guid.Empty,
                StoreMigrationPhase.MaintenanceRequired, reason, _journal?.Last?.ContentHash, _status.Backup);
        }

        private void Rollback()
        {
            if (!_transaction || _connection is null) return;
            try { SqliteNative.Execute(Database, "ROLLBACK;", new StoreDeadline(TimeSpan.FromSeconds(10))); }
            catch (Exception exception) when (IsMaintenanceFailure(exception)) { }
            // A failed/uncertain COMMIT is classified from durable state on the
            // next open. This flag is not claimed as proof of a disk rollback.
            _transaction = false;
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                Rollback();
                _connection?.Dispose();
                _source?.Dispose();
                _target?.Dispose();
                _journal?.Dispose();
                _lease?.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private void RequireFileBudget(string path)
        {
            var length = new FileInfo(path).Length;
            if (length > _options.MaximumDatabaseBytes)
                throw new InvalidOperationException("StoreMigrationDatabaseCapacityExceeded");
        }

        private static SqliteConnection OpenExisting(string path, bool readOnly)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private, Pooling = false
            }.ToString());
            try { connection.Open(); return connection; }
            catch { connection.Dispose(); throw; }
        }

        private static (string Version, string Hash) ApplicationIdentity(string path, StoreDeadline deadline, CancellationToken token)
        {
            var metadata = AssemblyName.GetAssemblyName(path);
            if (metadata.Name != "SharpInspect.Runtime" || metadata.Version is null)
                throw new InvalidOperationException("StoreMigrationApplicationIdentityInvalid");
            return (metadata.Version.ToString(), FileHash(path, 128L * 1024 * 1024, deadline, token));
        }

        private static string FileHash(string path, long maximum, StoreDeadline deadline, CancellationToken token)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            if (file.Length is < 1 || file.Length > maximum)
                throw new InvalidOperationException("StoreMigrationFileCapacityExceeded");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (deadline.Expired) throw new TimeoutException("StoreMigrationHashDeadlineExceeded");
                var count = file.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                total = checked(total + count);
                if (total > maximum) throw new InvalidOperationException("StoreMigrationFileCapacityExceeded");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
    }
}
