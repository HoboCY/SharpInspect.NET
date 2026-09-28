using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Deployment;

/// <summary>
/// Explicit, bounded provisioning and verification of one station installation. Provision creates
/// the protected installation root and exactly one CreateNew marker
/// (<c>sharpinspect.installation.json</c>) directly under the root, outside any slot directory and
/// outside the database; it never repairs, never overwrites and never invents state. RequireBinding
/// re-reads the actual root handle, the actual protected ACLs, the marker and the root's physical
/// directory identity (volume and file index), so a replaced, re-ACLed or aliased root is refused
/// instead of trusted. Nothing is provisioned implicitly: hosts and tests must provision their own
/// isolated fixtures explicitly.
/// </summary>
public static class StationInstallationRegistry
{
    internal const string MarkerFileName = "sharpinspect.installation.json";
    internal const string MarkerSchema = "sharpinspect.station-installation/1";
    private const int MaximumMarkerBytes = 16 * 1024;
    private const int MaximumMarkerFieldLength = 1024;
    private const int MaximumAncestorSteps = 256;
    private const int MaximumPathParts = 256;

    private const uint FileReadData = 0x00000001;
    private const uint Synchronize = 0x00100000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private static readonly string[] MarkerFields =
    {
        "schema", "stationId", "installationId", "identityHash", "bindingHash",
        "rootPath", "rootVolume", "rootIndexHigh", "rootIndexLow"
    };

    /// <summary>Creates the protected installation root (or accepts an already protected, empty,
    /// non-reparse one) and writes exactly one bounded CreateNew installation marker. The marker is
    /// the only entry written; a missing, corrupt, conflicting or permissive root is refused and
    /// never auto-healed.</summary>
    public static string Provision(StationInstallationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireWindows("StationInstallationProvisioningWindowsRequired");

        var currentIdentity = WindowsIdentity.GetCurrent();
        var current = currentIdentity.User
                      ?? throw new InvalidOperationException("StationInstallationIdentityUnavailable");
        var isAdministrator = new WindowsPrincipal(currentIdentity)
            .IsInRole(StationDeploymentOwnership.Administrators);
        if (!StationInstallationOwnership.IsProvisioningIdentity(current, options.CoordinatorIdentity,
                isAdministrator))
            throw new InvalidOperationException("StationInstallationProvisionerUnauthorized");

        var identities = StationDeploymentOwnership.DeploymentIdentities(options);
        var root = options.InstallationRoot;
        RequireLocalExistingAncestor(root, "StationInstallationRootRejected");

        if (Directory.Exists(root))
        {
            RequireEmptyRoot(root);
            if (!StationInstallationOwnership.ValidateRestrictedAcl(root, isDirectory: true, identities,
                    out var aclReason))
                throw new InvalidOperationException("StationInstallationRootAclInvalid:" + aclReason);
        }
        else
        {
            var parent = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new InvalidOperationException("StationInstallationRootParentMissing");
            try
            {
                FileSystemAclExtensions.Create(new DirectoryInfo(root),
                    StationInstallationOwnership.CreateRestrictedDirectorySecurity(identities));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                        or Win32Exception or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException("StationInstallationRootCreationFailed", ex);
            }
        }

        var identity = ReadRootIdentity(root);
        WriteMarkerCreateNew(Path.Combine(root, MarkerFileName), EncodeMarker(options, identity), identities);
        // Re-read the installation exactly as RequireBinding does, so a partially written marker or
        // a race never reports success.
        return RequireBinding(options);
    }

    /// <summary>Read-only verification of one installation: the root must exist as a named local
    /// fixed-NTFS directory without reparse aliases, its protected ACL must be exactly the
    /// configured identities, the marker must be present, bounded, well-formed and declared for
    /// these exact options, and the root's physical directory identity must match the identity
    /// recorded at provisioning. Returns the stable installation binding.</summary>
    public static string RequireBinding(StationInstallationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireWindows("StationInstallationVerificationWindowsRequired");

        var identities = StationDeploymentOwnership.DeploymentIdentities(options);
        var root = options.InstallationRoot;
        if (!Directory.Exists(root))
            throw new InvalidOperationException("StationInstallationRootMissing");
        RequireLocalExistingAncestor(root, "StationInstallationRootRejected");

        var identity = ReadRootIdentity(root);
        if (!StationInstallationOwnership.ValidateRestrictedAcl(root, isDirectory: true, identities,
                out var rootAclReason))
            throw new InvalidOperationException("StationInstallationRootAclInvalid:" + rootAclReason);

        var marker = ReadMarker(root, identities);
        if (!string.Equals(marker.Schema, MarkerSchema, StringComparison.Ordinal) ||
            !string.Equals(marker.StationId, options.StationId, StringComparison.Ordinal) ||
            !string.Equals(marker.InstallationId, options.InstallationId.ToString("D", CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marker.IdentityHash, options.IdentityHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marker.BindingHash, options.BindingHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(marker.RootPath, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("StationInstallationMarkerConflict");

        if (marker.Volume != identity.Volume || marker.IndexHigh != identity.IndexHigh ||
            marker.IndexLow != identity.IndexLow)
            throw new InvalidOperationException("StationInstallationRootReplaced");

        RequireDatabaseAnchor(options);
        return InstallationBinding(options, identity);
    }

    /// <summary>Refuses a database path that would be written below any registered installation.
    /// Anchors are scanned from the database directory up to the volume root in bounded steps; a
    /// valid registration, a corrupt marker and an unreadable marker all deny - unknown state is
    /// never interpreted as absent. An existing multi-hard-link database file is refused as an
    /// alias of content that lives outside the frozen binding.</summary>
    internal static void RequireUnregisteredDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("StationDatabasePathRequired", nameof(databasePath));
        if (!Path.IsPathFullyQualified(databasePath) || HasDevicePrefix(databasePath))
            throw new ArgumentException("StationDatabasePathMustBeExplicitLocalPath", nameof(databasePath));

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("StationDatabasePathInvalid", nameof(databasePath), ex);
        }

        if (canonical.StartsWith(@"\\", StringComparison.Ordinal) ||
            canonical.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("StationDatabasePathMustBeExplicitLocalPath", nameof(databasePath));

        var directory = Path.GetDirectoryName(canonical);
        var steps = 0;
        while (!string.IsNullOrEmpty(directory) && steps++ < MaximumAncestorSteps)
        {
            var marker = Path.Combine(directory, MarkerFileName);
            if (File.Exists(marker) || Directory.Exists(marker))
                throw new InvalidOperationException(ReadsAsInstallationMarker(marker)
                    ? "StationDatabaseRegistered"
                    : "StationDatabaseRegistrationUnknown");
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                break;
            directory = parent;
        }

        if (steps >= MaximumAncestorSteps)
            throw new InvalidOperationException("StationDatabaseRegistrationUnknown");

        if (File.Exists(canonical))
        {
            using var handle = OpenFile(canonical, FileReadAttributes | ReadControl | Synchronize,
                ShareAll, "StationDatabaseUnavailable");
            var information = Information(handle, "StationDatabaseUnavailable");
            if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                !HandlePathMatches(handle, canonical))
                throw new InvalidOperationException("StationDatabaseAliasRejected");
            if (information.Links != 1)
                throw new InvalidOperationException("StationDatabaseHardLinkAlias");
        }
        else if (Directory.Exists(canonical))
        {
            throw new InvalidOperationException("StationDatabaseAliasRejected");
        }
    }

    /// <summary>Physical identity of one existing root directory as the operating system reports
    /// it: the volume serial number and the file index. Re-read for every operation.</summary>
    internal static (uint Volume, uint IndexHigh, uint IndexLow) ReadRootIdentity(string root)
    {
        using var handle = OpenDirectory(root, "StationInstallationRootUnavailable");
        var information = Information(handle, "StationInstallationRootUnavailable");
        if ((information.Attributes & FileAttributeDirectory) == 0)
            throw new InvalidOperationException("StationInstallationRootKindInvalid");
        if ((information.Attributes & FileAttributeReparsePoint) != 0)
            throw new InvalidOperationException("StationInstallationRootReparsePoint");
        RequireHandlePath(handle, root, "StationInstallationRootHandleMismatch");
        return (information.Volume, information.IndexHigh, information.IndexLow);
    }

    private static string InstallationBinding(StationInstallationOptions options,
        (uint Volume, uint IndexHigh, uint IndexLow) identity) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("StationInstallationBindingV1",
            options.BindingHash, options.InstallationRoot.ToUpperInvariant(),
            identity.Volume.ToString("X8", CultureInfo.InvariantCulture),
            identity.IndexHigh.ToString("X8", CultureInfo.InvariantCulture),
            identity.IndexLow.ToString("X8", CultureInfo.InvariantCulture))));

    private static byte[] EncodeMarker(StationInstallationOptions options,
        (uint Volume, uint IndexHigh, uint IndexLow) identity)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", MarkerSchema);
            writer.WriteString("stationId", options.StationId);
            writer.WriteString("installationId", options.InstallationId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("identityHash", options.IdentityHash);
            writer.WriteString("bindingHash", options.BindingHash);
            writer.WriteString("rootPath", options.InstallationRoot);
            writer.WriteString("rootVolume", identity.Volume.ToString("X8", CultureInfo.InvariantCulture));
            writer.WriteString("rootIndexHigh", identity.IndexHigh.ToString("X8", CultureInfo.InvariantCulture));
            writer.WriteString("rootIndexLow", identity.IndexLow.ToString("X8", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteMarkerCreateNew(string markerPath, byte[] bytes,
        IReadOnlyList<SecurityIdentifier> identities)
    {
        try
        {
            using var stream = FileSystemAclExtensions.Create(new FileInfo(markerPath), FileMode.CreateNew,
                FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough,
                StationInstallationOwnership.CreateRestrictedFileSecurity(identities));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                    or Win32Exception or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("StationInstallationMarkerCreationFailed", ex);
        }
    }

    private static MarkerRecord ReadMarker(string root, IReadOnlyList<SecurityIdentifier> identities)
    {
        var markerPath = Path.Combine(root, MarkerFileName);
        using var handle = OpenMarker(markerPath);
        var information = Information(handle, "StationInstallationMarkerUnavailable");
        if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            information.Links != 1 || !HandlePathMatches(handle, markerPath))
            throw new InvalidOperationException("StationInstallationMarkerAlias");
        var length = Length(information);
        if (length < 1 || length > MaximumMarkerBytes)
            throw new InvalidOperationException("StationInstallationMarkerSizeInvalid");
        if (!StationInstallationOwnership.ValidateRestrictedAcl(markerPath, isDirectory: false, identities,
                out var aclReason))
            throw new InvalidOperationException("StationInstallationMarkerAclInvalid:" + aclReason);
        return ParseMarker(ReadBounded(handle, (int)length));
    }

    private static MarkerRecord ParseMarker(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("StationInstallationMarkerCorrupt");
        }

        using (document)
        {
            var element = document.RootElement;
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("StationInstallationMarkerCorrupt");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (Array.IndexOf(MarkerFields, property.Name) < 0 || values.ContainsKey(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("StationInstallationMarkerCorrupt");
                var text = property.Value.GetString();
                if (text is null || text.Length > MaximumMarkerFieldLength)
                    throw new InvalidOperationException("StationInstallationMarkerCorrupt");
                values[property.Name] = text;
            }

            if (values.Count != MarkerFields.Length ||
                !TryReadIdentity(values, "rootVolume", out var volume) ||
                !TryReadIdentity(values, "rootIndexHigh", out var indexHigh) ||
                !TryReadIdentity(values, "rootIndexLow", out var indexLow))
                throw new InvalidOperationException("StationInstallationMarkerCorrupt");

            return new MarkerRecord(values["schema"], values["stationId"], values["installationId"],
                values["identityHash"], values["bindingHash"], values["rootPath"], volume, indexHigh, indexLow);
        }
    }

    private static bool TryReadIdentity(IReadOnlyDictionary<string, string> values, string field, out uint value) =>
        uint.TryParse(values[field], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    private static bool ReadsAsInstallationMarker(string markerPath)
    {
        try
        {
            using var handle = OpenFile(markerPath, FileReadData | FileReadAttributes | ReadControl | Synchronize,
                ShareAll, "StationInstallationMarkerUnavailable");
            var information = Information(handle, "StationInstallationMarkerUnavailable");
            if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                information.Links != 1)
                return false;
            var length = Length(information);
            if (length < 1 || length > MaximumMarkerBytes) return false;
            using var document = JsonDocument.Parse(ReadBounded(handle, (int)length),
                new JsonDocumentOptions { MaxDepth = 4 });
            var element = document.RootElement;
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty("schema", out var schema) &&
                   schema.ValueKind == JsonValueKind.String &&
                   string.Equals(schema.GetString(), MarkerSchema, StringComparison.Ordinal) &&
                   element.TryGetProperty("stationId", out _) && element.TryGetProperty("installationId", out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                    or Win32Exception or InvalidOperationException or JsonException
                                    or ArgumentException)
        {
            return false;
        }
    }

    private static void RequireDatabaseAnchor(StationInstallationOptions options)
    {
        var databasePath = options.DatabasePath;
        var root = options.InstallationRoot;
        if (!IsStrictlyBelow(databasePath, root))
            throw new InvalidOperationException("StationInstallationDatabaseOutsideRoot");

        var directory = Path.GetDirectoryName(databasePath);
        var steps = 0;
        while (!string.IsNullOrEmpty(directory) && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            if (++steps > MaximumPathParts)
                throw new InvalidOperationException("StationInstallationDatabaseAnchorInvalid");
            if (File.Exists(directory))
                throw new InvalidOperationException("StationInstallationDatabaseAlias");
            if (Directory.Exists(directory))
            {
                using var handle = OpenDirectory(directory, "StationInstallationDatabaseAnchorUnavailable");
                var information = Information(handle, "StationInstallationDatabaseAnchorUnavailable");
                if ((information.Attributes & FileAttributeReparsePoint) != 0 ||
                    !HandlePathMatches(handle, directory))
                    throw new InvalidOperationException("StationInstallationDatabaseAlias");
            }

            directory = Path.GetDirectoryName(directory);
        }

        if (!string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("StationInstallationDatabaseOutsideRoot");

        if (Directory.Exists(databasePath))
            throw new InvalidOperationException("StationInstallationDatabaseAlias");
        if (!File.Exists(databasePath)) return;
        using var database = OpenFile(databasePath, FileReadAttributes | ReadControl | Synchronize, ShareAll,
            "StationInstallationDatabaseUnavailable");
        var databaseInformation = Information(database, "StationInstallationDatabaseUnavailable");
        if ((databaseInformation.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
            databaseInformation.Links != 1 || !HandlePathMatches(database, databasePath))
            throw new InvalidOperationException("StationInstallationDatabaseAlias");
    }

    private static void RequireEmptyRoot(string root)
    {
        var marker = Path.Combine(root, MarkerFileName);
        if (File.Exists(marker) || Directory.Exists(marker))
            throw new InvalidOperationException("StationInstallationAlreadyProvisioned");
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            if (entries.MoveNext()) throw new InvalidOperationException("StationInstallationRootNotEmpty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("StationInstallationRootUnavailable", ex);
        }
    }

    private static void RequireLocalExistingAncestor(string path, string reason)
    {
        var existing = path;
        while (true)
        {
            if (Directory.Exists(existing)) break;
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(reason);
            existing = parent;
        }

        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(existing, ".probe")),
                out _, out var pathReason))
            throw new InvalidOperationException(reason + ":" + pathReason);
    }

    private static void RequireWindows(string reason)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(reason);
    }

    private static SafeFileHandle OpenDirectory(string path, string reason) =>
        CreateFile(path, ReadControl | FileReadAttributes | Synchronize, ShareAll, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero, reason);

    private static SafeFileHandle OpenMarker(string path) =>
        CreateFile(path, FileReadData | FileReadAttributes | ReadControl | Synchronize, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero, "StationInstallationMarkerMissing");

    private static SafeFileHandle OpenFile(string path, uint access, uint share, string reason) =>
        CreateFile(path, access, share, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero, reason);

    private static SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template, string reason)
    {
        var handle = CreateFileNative(
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + Path.GetFullPath(path),
            access, share, security, creation, flags, template);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is 2 or 3) throw new InvalidOperationException(reason);
        throw new InvalidOperationException(reason, new Win32Exception(error));
    }

    private static byte[] ReadBounded(SafeFileHandle handle, int length)
    {
        var result = new byte[length];
        var chunk = new byte[Math.Min(4096, length)];
        var offset = 0;
        while (offset < length)
        {
            var request = Math.Min(chunk.Length, length - offset);
            if (!ReadFile(handle, chunk, (uint)request, out var read, IntPtr.Zero))
                throw new InvalidOperationException("StationInstallationMarkerUnavailable",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            if (read == 0) break;
            Buffer.BlockCopy(chunk, 0, result, offset, (int)read);
            offset += (int)read;
        }

        if (offset != length) throw new InvalidOperationException("StationInstallationMarkerCorrupt");
        return result;
    }

    private static void RequireHandlePath(SafeFileHandle handle, string path, string reason)
    {
        if (!HandlePathMatches(handle, path)) throw new InvalidOperationException(reason);
    }

    internal static bool HandlePathMatches(SafeFileHandle handle, string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        return string.Equals(Path.TrimEndingDirectorySeparator(actual),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase);
    }

    private static long Length(in FileInformation information) =>
        ((long)information.SizeHigh << 32) | information.SizeLow;

    private static FileInformation Information(SafeFileHandle handle, string reason) =>
        GetFileInformationByHandle(handle, out var information)
            ? information
            : throw new InvalidOperationException(reason, new Win32Exception(Marshal.GetLastWin32Error()));

    private static bool IsStrictlyBelow(string path, string root) =>
        path.Length > root.Length && path.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasDevicePrefix(string value) =>
        value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
        value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
        value.StartsWith(@"\??\", StringComparison.Ordinal);

    private static uint ShareAll => (uint)(FileShare.Read | FileShare.Write | FileShare.Delete);

    private sealed record MarkerRecord(string Schema, string StationId, string InstallationId, string IdentityHash,
        string BindingHash, string RootPath, uint Volume, uint IndexHigh, uint IndexLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint count, out int read,
        IntPtr overlapped);
}
