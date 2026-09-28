using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SharpInspect.Runtime.Deployment;

#pragma warning disable CA1416

/// <summary>Fixed protected installation paths. Neither Runtime nor the coordinator replaces an existing operation.</summary>
internal static class StationMaintenanceHandoffStore
{
    internal const string HandoffName = "station-maintenance-handoff.json";
    internal const string ConsumedName = "station-maintenance-consumed.json";
    internal const string FailedName = "station-maintenance-failed.json";
    internal const int MaximumHandoffBytes = 64 * 1024;

    internal static void RequireRuntimeEntry(StationDeploymentLock ownership)
    {
        RequireOwnership(ownership, StationDeploymentOwner.Runtime);
        foreach (var name in new[] { HandoffName, ConsumedName, FailedName })
            if (ExistsStrict(Path.Combine(ownership.Options.InstallationRoot, name)))
                throw new InvalidOperationException("StationMaintenanceRecoveryRequired");
    }

    internal static void Publish(StationDeploymentLock ownership, Guid operation, byte[] artifact)
    {
        RequireOwnership(ownership, StationDeploymentOwner.Runtime);
        if (operation == Guid.Empty || artifact.Length is < 1 or > MaximumHandoffBytes)
            throw new InvalidOperationException("StationMaintenanceHandoffBoundsInvalid");
        if (ExistsStrict(Path.Combine(ownership.Options.InstallationRoot, ConsumedName)))
            throw new InvalidOperationException("StationMaintenanceHandoffAlreadyConsumed");
        CreateImmutable(ownership.Options, HandoffName, artifact);
    }

    internal static byte[] Read(StationDeploymentLock ownership)
    {
        RequireOwnership(ownership, StationDeploymentOwner.Coordinator);
        if (ExistsStrict(Path.Combine(ownership.Options.InstallationRoot, FailedName)))
            throw new InvalidOperationException("StationMaintenanceHandoffFailed");
        if (ExistsStrict(Path.Combine(ownership.Options.InstallationRoot, ConsumedName)))
            throw new InvalidOperationException("StationMaintenanceHandoffAlreadyConsumed");
        var path = Path.Combine(ownership.Options.InstallationRoot, HandoffName);
        RequireFileSecurity(ownership.Options, path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        RequireHandle(stream.SafeFileHandle, path);
        if (stream.Length is < 1 or > MaximumHandoffBytes) throw new InvalidOperationException("StationMaintenanceHandoffBoundsInvalid");
        var bytes = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidOperationException("StationMaintenanceHandoffTruncated");
            offset += count;
        }
        RequireFileSecurity(ownership.Options, path);
        RequireHandle(stream.SafeFileHandle, path);
        return bytes;
    }

    internal static void Consume(StationDeploymentLock ownership, Guid operation, byte[] artifact)
    {
        RequireOwnership(ownership, StationDeploymentOwner.Coordinator);
        if (operation == Guid.Empty) throw new InvalidOperationException("StationMaintenanceOperationMissing");
        // This is an external append-only one-operation journal for T59. Later upgrade/recovery owns continuation.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "SharpInspect.StationMaintenanceConsumed.v1", operationId = operation,
            installationBindingHash = ownership.Options.BindingHash,
            handoffSha256 = Convert.ToHexString(SHA256.HashData(artifact)),
            coordinatorOwnershipId = ownership.OwnershipId, observedUtc = DateTimeOffset.UtcNow
        });
        CreateImmutable(ownership.Options, ConsumedName, bytes);
    }

    internal static void RecordFailure(StationDeploymentLock ownership, Guid operation, string reason)
    {
        RequireOwnership(ownership, StationDeploymentOwner.Runtime);
        CreateImmutable(ownership.Options, FailedName, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "SharpInspect.StationMaintenanceFailed.v1", operationId = operation,
            installationBindingHash = ownership.Options.BindingHash, reasonCode = reason, observedUtc = DateTimeOffset.UtcNow
        }));
    }

    private static void CreateImmutable(StationInstallationOptions options, string name, byte[] bytes)
    {
        StationInstallationRegistry.RequireBinding(options);
        var path = Path.Combine(options.InstallationRoot, name);
        using (var stream = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew,
                   FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough,
                   StationInstallationOwnership.CreateRestrictedFileSecurity(StationDeploymentOwnership.DeploymentIdentities(options))))
        {
            RequireHandle(stream.SafeFileHandle, path);
            stream.Write(bytes); stream.Flush(flushToDisk: true);
            RequireHandle(stream.SafeFileHandle, path);
        }
        RequireFileSecurity(options, path);
        StationInstallationRegistry.RequireBinding(options);
    }

    private static void RequireOwnership(StationDeploymentLock ownership, StationDeploymentOwner role)
    {
        if (!ownership.IsHeld || ownership.Owner != role) throw new InvalidOperationException("StationDeploymentOwnershipRequired");
        StationInstallationRegistry.RequireBinding(ownership.Options);
    }

    private static bool ExistsStrict(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        // Directory missing, inaccessible entries and unknown state are deliberately not absence.
    }

    private static void RequireFileSecurity(StationInstallationOptions options, string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            !StationInstallationOwnership.ValidateRestrictedAcl(path, false,
                StationDeploymentOwnership.DeploymentIdentities(options), out _))
            throw new InvalidOperationException("StationMaintenanceHandoffFileRejected");
    }

    private static void RequireHandle(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1 ||
            !StationInstallationRegistry.HandlePathMatches(handle, path))
            throw new InvalidOperationException("StationMaintenanceHandoffFileIdentityRejected");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

#pragma warning restore CA1416
