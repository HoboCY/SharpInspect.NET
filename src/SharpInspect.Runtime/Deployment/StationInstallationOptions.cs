using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using SharpInspect.Runtime.Integrity;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Deployment;

/// <summary>
/// Immutable bounded description of one station installation: the named local installation root
/// that owns the runtime slots and the database, the database file, and the two deployment
/// identities (runtime and coordinator) that may hold the machine-wide station deployment lock.
/// The constructor performs no IO and supplies no defaults; path shape, the database-below-root
/// invariant and both entities are validated syntactically, while the physical volume, reparse,
/// cloud and hard-link checks stay in the registry. IdentityHash covers the station and
/// installation identity only, so moving the installation or its database never changes the
/// machine-wide lock domain; BindingHash covers every declared field.
/// </summary>
public sealed class StationInstallationOptions
{
    internal const int MaximumStationIdLength = 200;
    private const int MaximumPathLength = 32767;
    private const int MaximumSidLength = 256;

    /// <summary>Creates one bounded installation description. All values are required; nothing is
    /// created, read or repaired here.</summary>
    public StationInstallationOptions(string stationId, Guid installationId, string installationRoot,
        string databasePath, string runtimeSid, string coordinatorSid)
    {
        StationId = RequireStationId(stationId);
        if (installationId == Guid.Empty)
            throw new ArgumentException("StationInstallationIdRequired", nameof(installationId));
        InstallationId = installationId;
        InstallationRoot = RequireInstallationRoot(installationRoot);
        DatabasePath = RequireDatabasePath(databasePath, InstallationRoot);
        RuntimeIdentity = RequireIdentity(runtimeSid, nameof(runtimeSid));
        CoordinatorIdentity = RequireIdentity(coordinatorSid, nameof(coordinatorSid));
        RuntimeSid = RuntimeIdentity.Value;
        CoordinatorSid = CoordinatorIdentity.Value;

        var identity = InstallationId.ToString("D", CultureInfo.InvariantCulture);
        IdentityHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "StationInstallationIdentityV1", StationId, identity)));
        BindingHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "StationInstallationOptionsV1", StationId, identity, InstallationRoot.ToUpperInvariant(),
            DatabasePath.ToUpperInvariant(), RuntimeSid, CoordinatorSid)));
    }

    /// <summary>Bounded station identifier; never empty and never containing control characters.</summary>
    public string StationId { get; }

    /// <summary>Non-empty installation identity. A new identity is a new installation even when the
    /// root path is reused.</summary>
    public Guid InstallationId { get; }

    /// <summary>Canonical absolute named local installation root.</summary>
    public string InstallationRoot { get; }

    /// <summary>Canonical database file path strictly below the installation root.</summary>
    public string DatabasePath { get; }

    /// <summary>Canonical string form of the configured runtime identity.</summary>
    public string RuntimeSid { get; }

    /// <summary>Canonical string form of the configured coordinator identity.</summary>
    public string CoordinatorSid { get; }

    /// <summary>Stable uppercase SHA-256 over the station and installation identity only. The
    /// machine-wide lock name derives from this hash alone.</summary>
    public string IdentityHash { get; }

    /// <summary>Stable uppercase SHA-256 over every declared field, including both paths and both
    /// identities.</summary>
    public string BindingHash { get; }

    internal SecurityIdentifier RuntimeIdentity { get; }

    internal SecurityIdentifier CoordinatorIdentity { get; }

    private static string RequireStationId(string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId))
            throw new ArgumentException("StationIdRequired", nameof(stationId));
        if (stationId.Length > MaximumStationIdLength)
            throw new ArgumentException("StationIdTooLong", nameof(stationId));
        foreach (var character in stationId)
            if (char.IsControl(character))
                throw new ArgumentException("StationIdInvalid", nameof(stationId));
        return stationId;
    }

    private static string RequireInstallationRoot(string installationRoot)
    {
        var canonical = CanonicalPath(installationRoot, "StationInstallationRoot", nameof(installationRoot));
        var leaf = Path.GetFileName(canonical);
        if (leaf.Length == 0 || string.Equals(canonical, Path.GetPathRoot(canonical), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("StationInstallationRootMustBeNamed", nameof(installationRoot));
        if (HasInvalidPathPart(canonical))
            throw new ArgumentException("StationInstallationRootInvalidName", nameof(installationRoot));
        return canonical;
    }

    private static string RequireDatabasePath(string databasePath, string installationRoot)
    {
        var canonical = CanonicalPath(databasePath, "StationDatabasePath", nameof(databasePath));
        if (Path.GetFileName(canonical).Length == 0)
            throw new ArgumentException("StationDatabasePathMustBeNamed", nameof(databasePath));
        if (HasInvalidPathPart(canonical))
            throw new ArgumentException("StationDatabasePathInvalidName", nameof(databasePath));
        if (!IsStrictlyBelow(canonical, installationRoot))
            throw new ArgumentException("StationDatabasePathOutsideRoot", nameof(databasePath));
        return canonical;
    }

    private static string CanonicalPath(string value, string reason, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(reason + "Required", parameter);
        if (value.Length > MaximumPathLength)
            throw new ArgumentException(reason + "TooLong", parameter);
        if (!Path.IsPathFullyQualified(value) || HasDevicePrefix(value))
            throw new ArgumentException(reason + "MustBeExplicitLocalPath", parameter);

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(reason + "Invalid", parameter, ex);
        }

        if (canonical.StartsWith(@"\\", StringComparison.Ordinal) ||
            canonical.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException(reason + "MustBeExplicitLocalPath", parameter);
        return canonical;
    }

    private static SecurityIdentifier RequireIdentity(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("StationIdentitySidRequired", parameter);
        if (value.Length > MaximumSidLength)
            throw new ArgumentException("StationIdentitySidTooLong", parameter);
        try
        {
            var identity = new SecurityIdentifier(value);
            if (string.IsNullOrEmpty(identity.Value))
                throw new ArgumentException("StationIdentitySidInvalid", parameter);
            return identity;
        }
        catch (Exception ex) when (ex is ArgumentException or SystemException)
        {
            throw new ArgumentException("StationIdentitySidInvalid", parameter, ex);
        }
    }

    private static bool HasDevicePrefix(string value) =>
        value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        value.StartsWith(@"\??\", StringComparison.Ordinal);

    private static bool IsStrictlyBelow(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasInvalidPathPart(string path)
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
}
