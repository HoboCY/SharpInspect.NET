using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Explicit recovery planning inputs. The WAL value admits a bounded control transaction;
/// it is not a physical ceiling and does not predict SQLite page amplification. The fact
/// quota counts all IdentityEvent and TraceStoragePolicyEvent rows since retention activation,
/// including ordinary authentication, and is never replenished by restart or publication.
/// </summary>
public sealed class TraceStorageRecoveryBudget
{
    public TraceStorageRecoveryBudget(long controlWalAdmissionBytes, int maximumControlFacts,
        long checkpointPlanningReserveBytes)
    {
        if (controlWalAdmissionBytes is <= 0 or > 4L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(controlWalAdmissionBytes));
        if (maximumControlFacts is < 8 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumControlFacts));
        if (checkpointPlanningReserveBytes <= 0 || checkpointPlanningReserveBytes >= controlWalAdmissionBytes)
            throw new ArgumentOutOfRangeException(nameof(checkpointPlanningReserveBytes));
        ControlWalAdmissionBytes = controlWalAdmissionBytes;
        MaximumControlFacts = maximumControlFacts;
        CheckpointPlanningReserveBytes = checkpointPlanningReserveBytes;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "trace-storage-recovery-budget-v1",
            controlWalAdmissionBytes.ToString(CultureInfo.InvariantCulture), maximumControlFacts.ToString(CultureInfo.InvariantCulture),
            checkpointPlanningReserveBytes.ToString(CultureInfo.InvariantCulture), "all-control-facts-since-retention-activation" });
    }
    public long ControlWalAdmissionBytes { get; }
    public int MaximumControlFacts { get; }
    public long CheckpointPlanningReserveBytes { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Explicit approved permission to execute retention rules. It never shortens an
/// existing obligation, authorizes deletion of a Core, or grants a human permission.
/// </summary>
public sealed class TraceRetentionExecutionPolicy
{
    public TraceRetentionExecutionPolicy(string policyId, string version, string approvalReference,
        string rationale, IEnumerable<TraceRetentionClass> deletableClasses,
        TraceStorageMaintenanceBudget cleanup, int maximumDeletionAttempts)
    {
        PolicyId = TraceStoragePolicyValidation.Identifier(policyId, nameof(policyId));
        Version = TraceStoragePolicyValidation.Identifier(version, nameof(version));
        ApprovalReference = TraceStoragePolicyValidation.Text(approvalReference, nameof(approvalReference), 256);
        Rationale = TraceStoragePolicyValidation.Text(rationale, nameof(rationale), 4096);
        var classes = AlgorithmContractValidation.Copy(deletableClasses, nameof(deletableClasses), 11)
            .OrderBy(value => value).ToArray();
        if (classes.Any(value => value is not (TraceRetentionClass.AuthoritativeImage or
                TraceRetentionClass.QuarantineEvidence or TraceRetentionClass.OrphanImageStage)) ||
            classes.Distinct().Count() != classes.Length)
            throw new ArgumentException("RetentionDeletionClassUnsupportedOrDuplicate", nameof(deletableClasses));
        DeletableClasses = Array.AsReadOnly(classes);
        Cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        if (maximumDeletionAttempts is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maximumDeletionAttempts));
        MaximumDeletionAttempts = maximumDeletionAttempts;
        if (cleanup.MaximumItems > 64 || cleanup.MaximumBytes > 4L * 1024 * 1024 * 1024)
            throw new ArgumentException("RetentionCleanupBudgetExceedsHardLimit", nameof(cleanup));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-retention-execution-policy-v1", PolicyId, Version, ApprovalReference, Rationale,
            Cleanup.ContentHash, MaximumDeletionAttempts.ToString(CultureInfo.InvariantCulture), classes.Length.ToString(CultureInfo.InvariantCulture)
        }.Concat(classes.Select(value => value.ToString())));
    }

    public string PolicyId { get; }
    public string Version { get; }
    public string ApprovalReference { get; }
    public string Rationale { get; }
    public ReadOnlyCollection<TraceRetentionClass> DeletableClasses { get; }
    public TraceStorageMaintenanceBudget Cleanup { get; }
    public int MaximumDeletionAttempts { get; }
    public string ContentHash { get; }
}

public enum TraceStorageArea : byte { Database = 1, ImageStage = 2, FinalImages = 3, StageQuarantine = 4, FinalQuarantine = 5 }
public enum TraceStorageHealth : byte { Unavailable = 1, Healthy = 2, CapacityBlocked = 3, IntegrityBlocked = 4 }
public enum TraceCheckpointStatus : byte { NotConfigured = 1, Awaiting = 2, Completed = 3, Busy = 4, BudgetExceeded = 5, Failed = 6, Unknown = 7 }

/// <summary>One actual volume and owned artifact inventory; physical free bytes are not inferred from deletion totals.</summary>
public sealed record TraceStorageAreaObservation(TraceStorageArea Area, string RootBindingHash,
    long TotalVolumeBytes, long AvailableVolumeBytes, long RequiredReserveBytes,
    long OwnedFileBytes, long OwnedFiles, long? MaximumOwnedBytes, long? MaximumOwnedFiles,
    bool InventoryComplete = true);

/// <summary>Recorded result of bounded checkpoint work performed during stopped maintenance.</summary>
public sealed record TraceCheckpointObservation(TraceCheckpointStatus Status, string ReasonCode,
    DateTimeOffset? ObservedAtUtc, string? PolicySnapshotHash, long? WalBytes,
    long? LogFrames, long? CheckpointedFrames, TimeSpan? Elapsed);

/// <summary>Read-only capacity evidence; it never authorizes production or a file operation.</summary>
public sealed record TraceStorageCapacitySnapshot(bool Available, string ReasonCode, Guid RuntimeEpoch,
    long Revision, DateTimeOffset ObservedAtUtc, long? PolicyVersion, string? PolicySnapshotHash,
    TraceStorageHealth Health, IReadOnlyList<TraceStorageAreaObservation> Areas,
    long DatabaseBytes, long WalBytes, long? MaximumWalBytes, TraceCheckpointObservation Checkpoint,
    ImageBacklogSnapshot? ImageBacklog, OutboxBacklogSnapshot? OutboxBacklog,
    IReadOnlyList<string> AdmissionBlockers);

public interface ITraceStorageCapacityQuery
{
    ValueTask<TraceStorageCapacitySnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
