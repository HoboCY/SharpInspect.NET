using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit schema-39 capacity observation and governed retention. Existing stores
/// require the bundled 38-to-39 startup migration. No deletion class is enabled implicitly.
/// </summary>
public sealed class TraceStorageRetentionOptions
{
    internal const int SchemaVersion = 39;
    internal const string ExecutionProfile = "SharpInspect.StorageRetention.StartupMaintenance.v1";
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 64 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;

    public TraceStorageRetentionOptions(TraceRetentionExecutionPolicy executionPolicy,
        TimeSpan observationInterval, TimeSpan fileTimeout, TimeSpan startupTimeout, TraceStorageRecoveryBudget recoveryBudget)
    {
        ExecutionPolicy = executionPolicy ?? throw new ArgumentNullException(nameof(executionPolicy));
        RecoveryBudget = recoveryBudget ?? throw new ArgumentNullException(nameof(recoveryBudget));
        ObservationInterval = observationInterval;
        FileTimeout = fileTimeout;
        StartupTimeout = startupTimeout;
        Validate();
    }

    public TraceRetentionExecutionPolicy ExecutionPolicy { get; }
    public TraceStorageRecoveryBudget RecoveryBudget { get; }
    public TimeSpan ObservationInterval { get; }
    public TimeSpan FileTimeout { get; }
    public TimeSpan StartupTimeout { get; }
    public int MaximumPageSize { get; init; } = 128;
    public int MaximumEvents { get; init; } = 10000;
    public int MaximumPayloadBytes { get; init; } = 16 * 1024;
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;
    public string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (ObservationInterval < TimeSpan.FromMilliseconds(10) || ObservationInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(ObservationInterval));
        if (FileTimeout <= TimeSpan.Zero || FileTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(FileTimeout));
        if (StartupTimeout < FileTimeout || StartupTimeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        if (MaximumPageSize is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(MaximumPageSize));
        if (MaximumEvents is < 16 or > MaximumEventsHardLimit) throw new ArgumentOutOfRangeException(nameof(MaximumEvents));
        if (MaximumPayloadBytes is < 4096 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("TraceStorageRetentionOptionsV1", ExecutionPolicy.ContentHash,
            N(ObservationInterval.Ticks), N(FileTimeout.Ticks), N(StartupTimeout.Ticks),
            N(MaximumPageSize), N(MaximumEvents), N(MaximumPayloadBytes), N(MaximumTotalBytes), RecoveryBudget.ContentHash);
    }

    internal void ValidateProfile(ProductionStoreOptions store)
    {
        Validate();
        if (store.EvidenceReconciliation is null || store.TraceStoragePolicies is null)
            throw new ArgumentException("RetentionRequiresReconciliationAndTracePolicy");
        store.EvidenceReconciliation.ValidateProfile(store);
        if (store.ImageFinalization is null && ExecutionPolicy.DeletableClasses.Count != 0)
            throw new ArgumentException("RetentionImageDeletionWithoutImageProfile");
        if (store.ImageFinalization is { } images &&
            ExecutionPolicy.DeletableClasses.Contains(TraceRetentionClass.AuthoritativeImage) &&
            ExecutionPolicy.Cleanup.MaximumBytes < images.FinalRoot.MaximumFinalFileBytes)
            throw new ArgumentException("RetentionBudgetCannotProcessLargestImage");
        if (ExecutionPolicy.DeletableClasses.Any(value => value is
                TraceRetentionClass.QuarantineEvidence or TraceRetentionClass.OrphanImageStage) &&
            ExecutionPolicy.Cleanup.MaximumBytes < Math.Max(
                store.EvidenceReconciliation.StageQuarantine?.MaximumFileBytes ?? 0,
                store.EvidenceReconciliation.FinalQuarantine?.MaximumFileBytes ?? 0))
            throw new ArgumentException("RetentionBudgetCannotProcessLargestQuarantine");
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
