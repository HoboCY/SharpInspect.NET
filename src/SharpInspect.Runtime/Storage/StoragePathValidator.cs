using Microsoft.Win32;
using System.Security;
using System.Runtime.Versioning;

namespace SharpInspect.Runtime.Storage;

internal static class StoragePathValidator
{
    private const int MaximumPathParts = 256;
    private static readonly string[] CloudPathNames =
    {
        "onedrive", "dropbox", "google drive", "googledrive", "icloud drive", "iclouddrive"
    };

    public static bool TryValidate(ProductionStoreOptions options, out string databasePath, out string reason)
    {
        databasePath = string.Empty;
        reason = string.Empty;

        if (options is null)
        {
            reason = "StoreOptionsRequired";
            return false;
        }

        if (options.CommitTimeout < TimeSpan.FromMilliseconds(1) ||
            options.CommitTimeout > TimeSpan.FromMinutes(5))
        {
            reason = "StoreCommitTimeoutOutOfRange";
            return false;
        }

        if (options.QueryTimeout < TimeSpan.FromMilliseconds(1) ||
            options.QueryTimeout > TimeSpan.FromMinutes(5))
        {
            reason = "StoreQueryTimeoutOutOfRange";
            return false;
        }

        if (options.QueueCapacity is < 1 or > 256)
        {
            reason = "StoreQueueCapacityOutOfRange";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            reason = "LocalNtfsRequired";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            reason = "StorePathRequired";
            return false;
        }

        if (!Path.IsPathFullyQualified(options.DatabasePath) || options.DatabasePath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            options.DatabasePath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            options.DatabasePath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            reason = "StorePathMustBeExplicitLocalPath";
            return false;
        }

        try
        {
            databasePath = Path.GetFullPath(options.DatabasePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "StorePathInvalid";
            return false;
        }

        if (Path.IsPathRooted(databasePath) == false || IsUncPath(databasePath))
        {
            reason = "StorePathNetworkShare";
            return false;
        }

        if (HasInvalidWindowsPathPart(databasePath))
        {
            reason = "StorePathInvalidName";
            return false;
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            reason = "StorePathDirectoryMissing";
            return false;
        }

        string root;
        try
        {
            root = Path.GetPathRoot(databasePath) ?? string.Empty;
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                reason = "StorePathRemovableOrNetwork";
                return false;
            }

            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = "StorePathNtfsRequired";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = "StorePathVolumeUnavailable";
            return false;
        }

        if (HasReparsePoint(databasePath) || HasReparsePoint(databasePath + "-wal") ||
            HasReparsePoint(databasePath + "-shm") || HasReparsePoint(databasePath + ".lock"))
        {
            reason = "StorePathReparsePoint";
            return false;
        }

        if (IsCloudSynchronized(databasePath))
        {
            reason = "StorePathCloudSynchronized";
            return false;
        }

        return true;
    }

    private static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static bool HasReparsePoint(string path)
    {
        var current = path;
        var parts = 0;
        while (!string.IsNullOrEmpty(current) && parts++ < MaximumPathParts)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }

        return parts >= MaximumPathParts;
    }

    private static bool IsCloudSynchronized(string path)
    {
        foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer", "Dropbox", "GoogleDriveFS", "iCloudDrive" })
        {
            // Windows environment variable names are case-insensitive, including ONEDRIVE.
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (IsUnderPath(path, value)) return true;
        }

        if (OperatingSystem.IsWindows())
        {
            if (!TryReadSyncRootRegistryPaths(out var roots)) return true;
            foreach (var root in roots)
                if (IsUnderPath(path, root)) return true;
        }

        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (CloudPathNames.Contains(part, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadSyncRootRegistryPaths(out IReadOnlyList<string> roots)
    {
        var values = new List<string>();
        var locations = new[]
        {
            (RegistryHive.CurrentUser, @"Software\Microsoft\OneDrive\Accounts"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"),
            (RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager"),
            (RegistryHive.CurrentUser, @"Software\Dropbox"),
            (RegistryHive.CurrentUser, @"Software\Google\DriveFS")
        };

        try
        {
            foreach (var location in locations)
            {
                using var baseKey = RegistryKey.OpenBaseKey(location.Item1, RegistryView.Default);
                using var key = baseKey.OpenSubKey(location.Item2, writable: false);
                if (key is null) continue;
                values.AddRange(ReadRegistryStrings(key, 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            roots = Array.Empty<string>();
            return false;
        }

        roots = values;
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadRegistryStrings(RegistryKey key, int depth)
    {
        var values = new List<string>();
        if (depth > 3) return values;
        var pathNames = new HashSet<string>(new[] { "UserFolder", "UserSyncRoot", "LocalPath", "MountPoint", "Path", "Root" },
            StringComparer.OrdinalIgnoreCase);
        foreach (var valueName in key.GetValueNames())
        {
            // SyncRootManager stores UserSyncRoots values under the user's SID, not "Path".
            if (!pathNames.Contains(valueName) && !key.Name.EndsWith(@"\UserSyncRoots", StringComparison.OrdinalIgnoreCase)) continue;
            var value = key.GetValue(valueName);
            if (value is string text && IsPathLike(text)) values.Add(text);
        }

        foreach (var childName in key.GetSubKeyNames())
        {
            var child = key.OpenSubKey(childName, writable: false);
            if (child is null) continue;
            using (child)
                values.AddRange(ReadRegistryStrings(child, depth + 1));
        }

        return values;
    }

    private static bool HasInvalidWindowsPathPart(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var remainder = path[root.Length..];
        foreach (var part in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith(' ') || part.EndsWith('.')) return true;
            if (part.Contains(':', StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsPathLike(string value) =>
        value.Length > 2 && (Path.IsPathRooted(value) || value.StartsWith(@"\\", StringComparison.Ordinal));

    private static bool IsUnderPath(string candidate, string root)
    {
        string normalized;
        try { normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var candidateNormalized = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidateNormalized, normalized, StringComparison.OrdinalIgnoreCase) ||
            candidateNormalized.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
