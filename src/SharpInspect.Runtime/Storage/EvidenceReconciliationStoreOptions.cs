using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// A controlled quarantine for one evidence root. Quarantine preserves orphan bytes for
/// investigation; it cannot satisfy an image manifest or create an inspection reference.
/// </summary>
public sealed class EvidenceQuarantineRootOptions
{
    public EvidenceQuarantineRootOptions(string root, long maximumFileBytes,
        long maximumTotalBytes, int maximumFiles)
    {
        // Apply the same fixed/local/NTFS/no-cloud/no-reparse boundary as final evidence.
        var validated = new ProductionImageFinalizationRootOptions(root, maximumFileBytes,
            maximumTotalBytes, maximumFiles);
        Root = validated.FinalRoot;
        MaximumFileBytes = maximumFileBytes;
        MaximumTotalBytes = maximumTotalBytes;
        MaximumFiles = maximumFiles;
        BindingHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "EvidenceQuarantineRootV1", validated.ContentHash)));
    }

    public string Root { get; }
    public long MaximumFileBytes { get; }
    public long MaximumTotalBytes { get; }
    public int MaximumFiles { get; }
    public string BindingHash { get; }

    internal void ValidateAgainst(string sourceRoot)
    {
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(Root, ".probe")),
                out _, out var reason))
            throw new InvalidOperationException("EvidenceQuarantineRootRejected:" + reason);
        var source = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var target = Root.TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EvidenceQuarantineMustShareSourceVolume");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EvidenceQuarantineRootsOverlap");
    }
}

/// <summary>
/// Explicit schema-38 reconciliation capability, independent of Outbox. Supplying these
/// options requires an explicit startup-maintenance migration of an existing 35/36/37
/// store. Omission retains the original storage and worker profile.
/// </summary>
public sealed class EvidenceReconciliationStoreOptions
{
    internal const int SchemaVersion = 38;
    internal const int FormatVersion = 1;
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 64 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const string RulesVersion = "evidence-reconciliation-v1";

    public EvidenceReconciliationStoreOptions(TraceStorageMaintenanceBudget scrubber)
    {
        Scrubber = scrubber ?? throw new ArgumentNullException(nameof(scrubber));
    }

    public TraceStorageMaintenanceBudget Scrubber { get; }
    public EvidenceQuarantineRootOptions? StageQuarantine { get; init; }
    public EvidenceQuarantineRootOptions? FinalQuarantine { get; init; }
    public int MaximumPageSize { get; init; } = 128;
    public int MaximumEvents { get; init; } = 10_000;
    public int MaximumPayloadBytes { get; init; } = 16 * 1024;
    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FileTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumPageSize is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(MaximumPageSize));
        if (MaximumEvents is < 16 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents));
        if (MaximumPayloadBytes is < 4096 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        if (StartupTimeout <= TimeSpan.Zero || StartupTimeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout));
        if (FileTimeout <= TimeSpan.Zero || FileTimeout > TimeSpan.FromMinutes(5) || FileTimeout > StartupTimeout)
            throw new ArgumentOutOfRangeException(nameof(FileTimeout));
        if (Scrubber.MaximumItems > 512 || Scrubber.MaximumBytes > 4L * 1024 * 1024 * 1024)
            throw new ArgumentException("EvidenceScrubberBudgetExceedsHardLimit");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("EvidenceReconciliationOptionsV1", RulesVersion,
            Scrubber.ContentHash, StageQuarantine?.BindingHash ?? "absent",
            FinalQuarantine?.BindingHash ?? "absent", Number(MaximumPageSize), Number(MaximumEvents),
            Number(MaximumPayloadBytes), Number(MaximumTotalBytes), Number(StartupTimeout.Ticks),
            Number(FileTimeout.Ticks));
    }

    internal void ValidateProfile(ProductionStoreOptions store)
    {
        Validate();
        if (store.ImageFinalization is null && store.Outbox is null)
            throw new ArgumentException("EvidenceReconciliationSourceProfileRequired");
        if (store.ImageEvidence is not null && store.ImageFinalization is null)
            throw new ArgumentException("EvidenceReconciliationRequiresCompleteImageFinalization");
        if (store.ImageFinalization is not { } images)
        {
            if (StageQuarantine is not null || FinalQuarantine is not null)
                throw new ArgumentException("EvidenceQuarantineWithoutImages");
            return;
        }
        if (StageQuarantine is null || FinalQuarantine is null)
            throw new ArgumentException("EvidenceQuarantineConfigurationRequired");
        StageQuarantine.ValidateAgainst(images.ImageEvidence.Stage.StageRoot);
        FinalQuarantine.ValidateAgainst(images.FinalRoot.FinalRoot);
        var roots = new[] { images.ImageEvidence.Stage.StageRoot, images.FinalRoot.FinalRoot,
            StageQuarantine.Root, FinalQuarantine.Root };
        for (var left = 0; left < roots.Length; left++)
        for (var right = left + 1; right < roots.Length; right++)
        {
            var first = roots[left].TrimEnd(Path.DirectorySeparatorChar);
            var second = roots[right].TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
                first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("EvidenceReconciliationRootsOverlap");
        }
        // One completed PNG must fit an entire scrub turn; otherwise a finite budget
        // could leave that retained file permanently unexamined without progress.
        if (Scrubber.MaximumBytes < checked(images.FinalRoot.MaximumFinalFileBytes + images.ImageEvidence.Stage.MaximumStageBytes))
            throw new ArgumentException("EvidenceScrubberCannotVerifyLargestPermittedImage");
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
