using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.StoragePolicies;

internal sealed record TraceStoragePhysicalObservation(long DatabaseBytes, long WalBytes,
    IReadOnlyList<TraceStorageAreaObservation> Areas);

internal static class TraceStorageCapacityEvaluator
{
    internal static TraceStorageCapacitySnapshot Evaluate(Guid epoch, long revision, DateTimeOffset now,
        TraceStoragePolicyReadResult current, TraceStorageDeploymentScope? scope,
        TraceStoragePhysicalObservation physical, ImageBacklogSnapshot? images, OutboxBacklogSnapshot? outbox,
        TraceCheckpointObservation checkpoint)
    {
        if (!current.Available || current.Publication is null || current.Snapshot is null)
            return Unavailable(epoch, revision, now, current.ReasonCode, checkpoint);
        var policy = current.Publication.Policy;
        var failures = new List<string>();
        if (physical.DatabaseBytes < 0 || physical.WalBytes < 0 || physical.Areas.Count == 0)
            return Unavailable(epoch, revision, now, "TraceStoragePhysicalObservationInvalid", checkpoint);
        foreach (var area in physical.Areas)
        {
            if (area.TotalVolumeBytes <= 0 || area.AvailableVolumeBytes < 0 ||
                area.AvailableVolumeBytes > area.TotalVolumeBytes || area.OwnedFiles < 0 || area.OwnedFileBytes < 0)
                return Unavailable(epoch, revision, now, "TraceStoragePhysicalObservationInvalid", checkpoint);
            failures.AddRange(TraceStoragePolicyValidator.Validate(policy, scope, area.TotalVolumeBytes));
            if (area.RequiredReserveBytes != TraceStoragePolicyValidator.RequiredReserve(policy, area.TotalVolumeBytes))
                return Unavailable(epoch, revision, now, "TraceStorageReserveObservationMismatch", checkpoint);
            if (area.AvailableVolumeBytes < area.RequiredReserveBytes)
                failures.Add("TraceStorageReserveViolated." + area.Area);
            if (area.MaximumOwnedBytes is { } maxBytes && area.OwnedFileBytes >= maxBytes)
                failures.Add("TraceStorageOwnedBytesLimitReached." + area.Area);
            if (area.MaximumOwnedFiles is { } maxFiles && area.OwnedFiles >= maxFiles)
                failures.Add("TraceStorageOwnedFilesLimitReached." + area.Area);
            if (!area.InventoryComplete) failures.Add("TraceStorageInventoryBoundReached." + area.Area);
        }
        if (physical.WalBytes >= policy.MaximumWalBytes) failures.Add("TraceStorageWalPolicyLimitReached");
        if (images is { } backlog)
        {
            if (!ImageBacklogValid(backlog))
                return Unavailable(epoch, revision, now, "TraceStorageImageBacklogInvalid", checkpoint);
            failures.AddRange(ImageBacklogFailures(policy, backlog, now));
        }
        if (outbox is not null && Outbox.ProductionOutboxBinding.RequiredBacklogFailure(current.Snapshot, outbox, now) is { } failure)
            failures.Add(failure);
        if (checkpoint.Status is TraceCheckpointStatus.Busy or TraceCheckpointStatus.BudgetExceeded or
            TraceCheckpointStatus.Failed or TraceCheckpointStatus.Unknown)
            failures.Add(checkpoint.ReasonCode);
        var blockers = failures.Distinct(StringComparer.Ordinal).ToArray();
        return new(true, blockers.FirstOrDefault() ?? "TraceStorageWithinPolicy", epoch, revision, now,
            current.Publication.Version, current.Snapshot.ContentHash,
            blockers.Length == 0 ? TraceStorageHealth.Healthy : TraceStorageHealth.CapacityBlocked,
            physical.Areas, physical.DatabaseBytes, physical.WalBytes, policy.MaximumWalBytes,
            checkpoint, images, outbox, Array.AsReadOnly(blockers));
    }

    internal static bool ImageBacklogValid(ImageBacklogSnapshot backlog) => backlog.ThroughAuditSequence >= 0 &&
        backlog.Count >= 0 && backlog.Bytes >= 0 && (backlog.Count == 0) == (backlog.OldestCreatedAtUtc is null);

    internal static IEnumerable<string> ImageBacklogFailures(TraceStoragePolicyDefinition policy,
        ImageBacklogSnapshot backlog, DateTimeOffset now)
    {
        if (backlog.Count >= policy.ImageBacklog.MaximumItems) yield return "TraceStorageImageCountLimitReached";
        if (backlog.Bytes >= policy.ImageBacklog.MaximumBytes) yield return "TraceStorageImageBytesLimitReached";
        if (backlog.OldestCreatedAtUtc is { } oldest)
        {
            if (oldest > now) yield return "TraceStorageImageClockRollback";
            else if (now - oldest >= policy.ImageBacklog.MaximumOldestAge) yield return "TraceStorageImageAgeLimitReached";
        }
    }

    internal static TraceStorageCapacitySnapshot Unavailable(Guid epoch, long revision, DateTimeOffset now,
        string reason, TraceCheckpointObservation? checkpoint = null) =>
        new(false, reason, epoch, revision, now, null, null, TraceStorageHealth.Unavailable,
            Array.Empty<TraceStorageAreaObservation>(), 0, 0, null, checkpoint ??
                new(TraceCheckpointStatus.Awaiting, "TraceStorageStoppedCheckpointRequired", null, null, null, null, null, null),
            null, null, Array.AsReadOnly(new[] { reason }));
}
