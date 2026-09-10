using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.StoragePolicies;

internal sealed record TraceStorageVolumeObservation(bool Available, string ReasonCode,
    string? DatabasePath = null, long? TotalBytes = null, long? FreeBytes = null, long? WalBytes = null);

internal static class TraceStoragePreflightEvaluator
{
    internal static TraceStorageVolumeObservation Observe(ProductionStoreOptions options)
    {
        if (!StoragePathValidator.TryValidate(options, out var path, out var reason)) return new(false, reason);
        try
        {
            var volume = new DriveInfo(Path.GetPathRoot(path)!);
            var total = volume.TotalSize;
            var free = volume.AvailableFreeSpace;
            if (total <= 0 || free < 0 || free > total) return new(false, "TraceStorageVolumeObservationInvalid");
            return new(true, "TraceStorageVolumeObserved", path, total, free, ReadWalLength(path + "-wal"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "TraceStorageVolumeObservationUnavailable"); }
    }

    internal static long? ReadWalLength(string path, Func<string, long>? readLength = null)
    {
        try
        {
            var bytes = (readLength ?? (value => new FileInfo(value).Length))(path);
            return bytes >= 0 ? bytes : null;
        }
        catch (FileNotFoundException) { return 0; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    internal static TraceStoragePreflightReport Evaluate(TraceStoragePolicyReadResult current,
        TraceStorageDeploymentScope? scope, TraceStorageVolumeObservation volume,
        SqliteCommandStore.VerifiedSqliteProfile? profile, DateTimeOffset now)
    {
        var policy = current.Available ? current.Publication?.Policy : null;
        var rows = new List<TraceStoragePreflightRow>();
        void Row(TraceStoragePreflightGate gate, TraceStoragePreflightStatus status, string reason,
            string? expected = null, string? observed = null) => rows.Add(new(gate, status, reason, expected, observed));
        var validation = policy is null ? Array.Empty<string>() : TraceStoragePolicyValidator.Validate(policy, scope, volume.TotalBytes);
        Row(TraceStoragePreflightGate.Policy, policy is null ? TraceStoragePreflightStatus.Missing :
            validation.Count == 0 ? TraceStoragePreflightStatus.Passed : TraceStoragePreflightStatus.Failed,
            policy is null ? current.ReasonCode : validation.FirstOrDefault() ?? "TraceStoragePolicyValid");
        Row(TraceStoragePreflightGate.RouteInventory,
            policy is null || scope is null ? TraceStoragePreflightStatus.NotConfigured :
            TraceStoragePolicyValidator.RoutesMatch(policy, scope) ? TraceStoragePreflightStatus.Passed : TraceStoragePreflightStatus.Mismatch,
            policy is null || scope is null ? "TraceStorageRouteInventoryUnavailable" :
            TraceStoragePolicyValidator.RoutesMatch(policy, scope) ? "TraceStorageRouteInventoryMatched" : "TraceStorageRequiredRouteSetMismatch",
            scope?.ContentHash);
        Row(TraceStoragePreflightGate.StoragePath, volume.Available ? TraceStoragePreflightStatus.Passed : TraceStoragePreflightStatus.Failed,
            volume.ReasonCode);
        var profileValid = profile is { JournalMode: "wal", Synchronous: 2, ForeignKeys: 1, WalAutoCheckpoint: 0 };
        Row(TraceStoragePreflightGate.SqliteProfile, profile is null ? TraceStoragePreflightStatus.Missing :
            profileValid ? TraceStoragePreflightStatus.Passed : TraceStoragePreflightStatus.Mismatch,
            profile is null ? "TraceStorageSqliteProfileUnavailable" : profileValid ?
            "TraceStorageSqliteProfileVerified" : "TraceStorageSqliteProfileMismatch");
        long? reserve = policy is not null && volume.TotalBytes is > 0 ?
            TraceStoragePolicyValidator.RequiredReserve(policy, volume.TotalBytes.Value) : null;
        Row(TraceStoragePreflightGate.StorageReserve, reserve is null || volume.FreeBytes is null ? TraceStoragePreflightStatus.Missing :
            volume.FreeBytes >= reserve ? TraceStoragePreflightStatus.Passed : TraceStoragePreflightStatus.Failed,
            reserve is null || volume.FreeBytes is null ? "TraceStorageReserveUnobserved" :
            volume.FreeBytes >= reserve ? "TraceStorageReserveSatisfied" : "TraceStorageReserveViolated",
            Number(reserve), Number(volume.FreeBytes));
        Row(TraceStoragePreflightGate.WalCapacity, policy is null || volume.WalBytes is null ? TraceStoragePreflightStatus.Missing :
            volume.WalBytes < policy.MaximumWalBytes ? TraceStoragePreflightStatus.Passed : TraceStoragePreflightStatus.Failed,
            policy is null || volume.WalBytes is null ? "TraceStorageWalUnobserved" :
            volume.WalBytes < policy.MaximumWalBytes ? "TraceStorageWalWithinPolicy" : "TraceStorageWalPolicyLimitReached",
            Number(policy?.MaximumWalBytes), Number(volume.WalBytes));
        Row(TraceStoragePreflightGate.Checkpoint, TraceStoragePreflightStatus.NotImplemented, "TraceStoragePolicyCheckpointEnforcementUnavailable");
        Row(TraceStoragePreflightGate.ImageBacklog, TraceStoragePreflightStatus.NotImplemented, "TraceStorageImageBacklogUnobserved");
        Row(TraceStoragePreflightGate.RequiredRouteBacklog, TraceStoragePreflightStatus.NotImplemented, "TraceStorageRouteBacklogUnobserved");
        Row(TraceStoragePreflightGate.EvidenceReconciliation, TraceStoragePreflightStatus.NotImplemented, "TraceStorageEvidenceReconciliationUnavailable");
        Row(TraceStoragePreflightGate.Scrubber, TraceStoragePreflightStatus.NotImplemented, "TraceStorageScrubberEnforcementUnavailable");
        Row(TraceStoragePreflightGate.OtherDeploymentPolicies, TraceStoragePreflightStatus.NotImplemented, "TraceStorageOtherDeploymentPoliciesUnavailable");
        Row(TraceStoragePreflightGate.ProductionCycle, TraceStoragePreflightStatus.NotImplemented, "TraceStorageProductionCycleUnavailable");
        return new(now, current.Available ? current.Publication?.Version : null,
            current.Available ? current.Snapshot?.ContentHash : null, rows);
    }

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
}
