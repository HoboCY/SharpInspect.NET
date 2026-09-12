namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Limits for the bundled startup migrations: schema 32 to schema 33 (Recipe
/// lifecycle) and schema 33 to schema 34 (production image evidence). The source
/// runtime assembly is inspected as provenance, never loaded or executed.
/// Deployment authorization and application-slot selection belong to the host's
/// governed upgrade workflow; these settings grant no production authority.
/// </summary>
public sealed class StoreStartupMaintenanceOptions
{
    public StoreStartupMaintenanceOptions(string sourceRuntimeAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(sourceRuntimeAssemblyPath) ||
            !Path.IsPathFullyQualified(sourceRuntimeAssemblyPath))
            throw new ArgumentException("StoreMigrationSourceApplicationPathRequired", nameof(sourceRuntimeAssemblyPath));
        SourceRuntimeAssemblyPath = Path.GetFullPath(sourceRuntimeAssemblyPath);
    }

    public string SourceRuntimeAssemblyPath { get; }
    public StoreMigrationIntegrityCheck IntegrityCheck { get; init; } = StoreMigrationIntegrityCheck.Full;
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public long MaximumDatabaseBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaximumJournalBytes { get; init; } = 4L * 1024 * 1024;

    internal void Validate()
    {
        if (!Enum.IsDefined(typeof(StoreMigrationIntegrityCheck), IntegrityCheck))
            throw new ArgumentOutOfRangeException(nameof(IntegrityCheck));
        if (OperationTimeout < TimeSpan.FromSeconds(1) || OperationTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout), "StoreMigrationTimeoutInvalid");
        if (MaximumDatabaseBytes < 65536 || MaximumDatabaseBytes > 16L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumDatabaseBytes), "StoreMigrationDatabaseLimitInvalid");
        if (MaximumJournalBytes < 128 * 1024 || MaximumJournalBytes > 16L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumJournalBytes), "StoreMigrationJournalLimitInvalid");
    }
}

public enum StoreMigrationIntegrityCheck { Full = 1, Quick = 2 }

public enum StoreMigrationPhase
{
    Opened = 1,
    SourceVerified = 2,
    CheckpointVerified = 3,
    BackupStarted = 4,
    BackupCreated = 5,
    BackupVerified = 6,
    TransactionStarted = 7,
    TablesCaptured = 8,
    TablesRebuilt = 9,
    FeatureInitialized = 10,
    TargetVerified = 11,
    CommitIntent = 12,
    DatabaseCommitted = 13,
    Completed = 14,
    MaintenanceRequired = 15
}

/// <summary>A verified database backup, not a complete station recovery set.</summary>
public sealed class VerifiedStoreMigrationBackup
{
    internal VerifiedStoreMigrationBackup(string path, int sourceSchemaVersion,
        string sourceApplicationVersion, string sourceApplicationSha256,
        DateTimeOffset createdAtUtc, long byteLength, string sha256, string sourceFingerprint)
    {
        Path = path;
        SourceSchemaVersion = sourceSchemaVersion;
        SourceApplicationVersion = sourceApplicationVersion;
        SourceApplicationSha256 = sourceApplicationSha256;
        CreatedAtUtc = createdAtUtc;
        ByteLength = byteLength;
        Sha256 = sha256;
        SourceFingerprint = sourceFingerprint;
    }

    public string Path { get; }
    public int SourceSchemaVersion { get; }
    public string SourceApplicationVersion { get; }
    public string SourceApplicationSha256 { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public long ByteLength { get; }
    public string Sha256 { get; }
    public string SourceFingerprint { get; }
}

public sealed class StoreMigrationStatus
{
    internal StoreMigrationStatus(Guid operationId, StoreMigrationPhase phase, string reasonCode,
        string? journalHeadHash = null, VerifiedStoreMigrationBackup? backup = null,
        int sourceSchemaVersion = 32, int targetSchemaVersion = RecipeLifecycleStoreOptions.SchemaVersion)
    {
        OperationId = operationId;
        Phase = phase;
        ReasonCode = reasonCode;
        JournalHeadHash = journalHeadHash;
        Backup = backup;
        SourceSchemaVersion = sourceSchemaVersion;
        TargetSchemaVersion = targetSchemaVersion;
    }

    public Guid OperationId { get; }
    public StoreMigrationPhase Phase { get; }
    public string ReasonCode { get; }
    public int SourceSchemaVersion { get; }
    public int TargetSchemaVersion { get; }
    public string? JournalHeadHash { get; }
    public VerifiedStoreMigrationBackup? Backup { get; }
    public bool Completed => Phase == StoreMigrationPhase.Completed;
    public bool Ready => false;
}

/// <summary>
/// An exclusive startup-maintenance owner. Advancing or disposing a session
/// never opens a production Runtime. Disposing an unfinished transaction rolls
/// it back while preserving its journal and backup for a subsequent inspection.
/// </summary>
public interface IStoreStartupMaintenanceSession : IAsyncDisposable
{
    StoreMigrationStatus Status { get; }
    ValueTask<StoreMigrationStatus> AdvanceAsync(CancellationToken cancellationToken = default);
}

public sealed class StoreStartupMaintenanceOpenResult
{
    internal StoreStartupMaintenanceOpenResult(IStoreStartupMaintenanceSession? session,
        StoreMigrationStatus status)
    {
        Session = session;
        Status = status;
    }

    public IStoreStartupMaintenanceSession? Session { get; }
    public StoreMigrationStatus Status { get; }
    public bool Available => Session is not null;
}
