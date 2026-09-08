using System.Globalization;
using SharpInspect.Abstractions;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, versioned admission limits for the local Recipe Draft ledger.
/// Supplying this option opts a store into schema 9; no existing schema is
/// migrated implicitly.
/// </summary>
public sealed class RecipeDraftStoreOptions
{
    internal const int SchemaVersion = 9;
    internal const int FormatVersion = 1;
    internal const int MaximumRecordBytesHardLimit = 2 * 1024 * 1024;
    internal const int MaximumPageBytesHardLimit = 4 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 256L * 1024 * 1024;
    internal const int MaximumRevisionCountHardLimit = 10_000;
    internal const int SharedAuditControlReserve = 64;
    // A full draft row carries a canonical document plus SQLite row metadata. Keep
    // the connection limit above the 2 MiB payload bound while remaining bounded.
    internal const int SqliteValueLimitBytes = 4 * 1024 * 1024;

    public RecipeDraftStoreOptions(AlgorithmExecutionPolicy executionPolicy)
    {
        ExecutionPolicy = executionPolicy ?? throw new ArgumentNullException(nameof(executionPolicy));
        Validate();
    }

    /// <summary>Policy used to validate the persisted AlgorithmExecution requirement.</summary>
    public AlgorithmExecutionPolicy ExecutionPolicy { get; }

    public int MaximumRecordBytes { get; init; } = 2 * 1024 * 1024;
    public int MaximumPageBytes { get; init; } = 4 * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumRevisionCount { get; init; } = 10_000;

    /// <summary>Deployment may require fresh Step-Up for every draft save.</summary>
    public bool RequireStepUp { get; init; }

    internal string BindingHash => AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-recipe-draft-store-v1",
        ExecutionPolicy.Id,
        ExecutionPolicy.Version,
        ExecutionPolicy.ContentHash,
        MaximumRecordBytes.ToString(CultureInfo.InvariantCulture),
        MaximumPageBytes.ToString(CultureInfo.InvariantCulture),
        MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
        MaximumRevisionCount.ToString(CultureInfo.InvariantCulture),
        RequireStepUp ? "1" : "0",
        SharedAuditControlReserve.ToString(CultureInfo.InvariantCulture)
    });

    internal void Validate()
    {
        ExecutionPolicy.GetType();
        if (MaximumRecordBytes is <= 0 or > MaximumRecordBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumRecordBytes), "RecipeDraftRecordCapacityInvalid");
        if (MaximumPageBytes is <= 0 or > MaximumPageBytesHardLimit || MaximumPageBytes < MaximumRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumPageBytes), "RecipeDraftPageCapacityInvalid");
        if (MaximumTotalBytes is <= 0 or > MaximumTotalBytesHardLimit || MaximumTotalBytes < MaximumRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes), "RecipeDraftTotalCapacityInvalid");
        if (MaximumRevisionCount is <= 0 or > MaximumRevisionCountHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumRevisionCount), "RecipeDraftRevisionCapacityInvalid");
    }

    internal static void ConfigureSqliteLimit(sqlite3 database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _ = raw.sqlite3_limit(database, raw.SQLITE_LIMIT_LENGTH, SqliteValueLimitBytes);
    }
}
