using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>
/// Installation and runtime verification of one named local diagnostic directory. Install may
/// create only a named absent or empty local fixed NTFS directory whose ancestors are neither
/// cloud-synchronized nor reparse points. Verify always re-reads the actual directory, its file
/// identity and its restricted ACL; it never trusts a stored boolean. The approved identities are
/// the current process user and BuiltinAdministrators, and no protection from a local
/// administrator or an offline disk reader is claimed.
/// </summary>
public static class DiagnosticDirectoryInstallation
{
    private const uint DaclSecurityInformation = 0x00000005; // OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION
    private const int FileObjectType = 1;                     // SE_FILE_OBJECT

    /// <summary>Validates or creates only the named directory and returns the actual installation
    /// binding. An existing directory must be empty; its ACL is restricted to the approved
    /// identities before the binding is computed.</summary>
    public static string Install(DiagnosticLocalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = options.Directory;
        RequireLocalExistingAncestor(directory);
        if (Directory.Exists(directory))
        {
            using (OpenDirectory(directory)) { }
            RequireEmpty(directory);
            var owner = new DirectorySecurity(directory, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (!Equals(owner, WindowsIdentity.GetCurrent().User) &&
                !Equals(owner, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)))
                throw new InvalidOperationException("DiagnosticDirectoryOwnerInvalid");
            ApplyRestrictedDirectoryAcl(directory);
        }
        else
        {
            // Only the named directory may be created; ancestors are never invented and must
            // already be present and validated.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                throw new InvalidOperationException("DiagnosticDirectoryParentMissing");
            try { FileSystemAclExtensions.Create(new DirectoryInfo(directory), CreateRestrictedDirectorySecurity()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                        or Win32Exception or ArgumentException or NotSupportedException)
            { throw new InvalidOperationException("DiagnosticDirectoryCreationFailed", ex); }
        }

        var binding = Describe(options).Binding;
        // The root can never be reused as a container for foreign content.
        RequireEmpty(directory);
        return binding;
    }

    /// <summary>Re-reads the actual directory identity and ACL and compares them with the binding
    /// returned by Install. A missing, replaced, reparse-point or re-ACLed root verifies false.</summary>
    public static bool Verify(DiagnosticLocalStoreOptions options, string bindingHash)
    {
        if (options is null || string.IsNullOrWhiteSpace(bindingHash) || bindingHash.Length != 64)
            return false;
        try { return string.Equals(Describe(options).Binding, bindingHash, StringComparison.Ordinal); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                    or Win32Exception or InvalidOperationException or ArgumentException
                                    or NotSupportedException or PlatformNotSupportedException)
        { return false; }
    }

    internal sealed record RootIdentity(string Binding, uint Volume, uint IndexHigh, uint IndexLow);

    /// <summary>Describes the actual root object and throws a fixed reason code when it is missing,
    /// reparse-pointed, outside a local fixed NTFS volume or ACL-restricted by other trustees.</summary>
    internal static RootIdentity Describe(DiagnosticLocalStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = options.Directory;
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(directory, ".probe")), out _, out var reason))
            throw new InvalidOperationException("DiagnosticDirectoryRejected:" + reason);
        using var handle = OpenDirectory(directory);
        var information = Information(handle, "DiagnosticDirectoryUnavailable");
        if ((information.Attributes & 0x10) == 0) throw new InvalidOperationException("DiagnosticDirectoryKindInvalid");
        if ((information.Attributes & 0x400) != 0) throw new InvalidOperationException("DiagnosticDirectoryReparsePoint");
        RequireHandlePath(handle, directory, "DiagnosticDirectoryHandlePathMismatch");
        if (!ValidateRestrictedAcl(directory, isDirectory: true))
            throw new InvalidOperationException("DiagnosticDirectoryAclInvalid");
        var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("DiagnosticDirectoryAclInvalid");
        var dacl = ReadDaclSddl(handle);
        // SetSecurityInfo may add SE_DACL_AUTO_INHERITED to an unchanged protected DACL.
        // It does not change any ACE or enable inheritance. Bind the exact access rules and
        // protection flag without treating that Windows bookkeeping bit as a policy change.
        var descriptor = new RawSecurityDescriptor(dacl);
        descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        dacl = descriptor.GetSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        var binding = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("DiagnosticDirectoryInstallationV1",
            options.BindingHash, information.Volume.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
            information.IndexHigh.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
            information.IndexLow.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
            current.Value, dacl)));
        return new(binding, information.Volume, information.IndexHigh, information.IndexLow);
    }

    /// <summary>An exact restricted DACL: protected, no inherited rules, and only the current
    /// process user and BuiltinAdministrators with full control. Deny rules are rejected.</summary>
    internal static bool ValidateRestrictedAcl(string path, bool isDirectory)
    {
        var current = WindowsIdentity.GetCurrent().User;
        if (current is null) return false;
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var allowed = new HashSet<SecurityIdentifier> { current, administrators };
        var seen = new HashSet<SecurityIdentifier>();
        try
        {
            FileSystemSecurity security = isDirectory
                ? new DirectorySecurity(path, AccessControlSections.Access | AccessControlSections.Owner)
                : new FileSecurity(path, AccessControlSections.Access | AccessControlSections.Owner);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !allowed.Contains(owner)) return false;
            if (!security.AreAccessRulesProtected) return false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true,
                         includeInherited: true, targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited) return false;
                if (rule.IdentityReference is not SecurityIdentifier sid || !allowed.Contains(sid)) return false;
                if ((rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl) return false;
                seen.Add(sid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        { return false; }
        return allowed.All(seen.Contains);
    }

    internal static FileSecurity CreateRestrictedFileSecurity()
    {
        var security = new FileSecurity();
        Restrict(security);
        return security;
    }

    private static DirectorySecurity CreateRestrictedDirectorySecurity()
    {
        var security = new DirectorySecurity();
        Restrict(security);
        return security;
    }

    private static void Restrict(FileSystemSecurity security)
    {
        var (current, administrators) = ApprovedIdentities();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
    }

    private static (SecurityIdentifier Current, SecurityIdentifier Administrators) ApprovedIdentities()
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("DiagnosticDirectoryAclInvalid");
        return (current, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    }

    private static void ApplyRestrictedDirectoryAcl(string directory)
    {
        try { FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(directory), CreateRestrictedDirectorySecurity()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                    or Win32Exception or ArgumentException)
        { throw new InvalidOperationException("DiagnosticDirectoryAclWriteFailed", ex); }
    }

    private static void RequireLocalExistingAncestor(string directory)
    {
        var existing = directory;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DiagnosticDirectoryParentMissing");
            existing = parent;
        }
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(existing, ".probe")), out _, out var reason))
            throw new InvalidOperationException("DiagnosticDirectoryRejected:" + reason);
    }

    private static void RequireEmpty(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            if (entries.MoveNext()) throw new InvalidOperationException("DiagnosticDirectoryNotEmpty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("DiagnosticDirectoryUnavailable", ex); }
    }

    private static SafeFileHandle OpenDirectory(string directory)
    {
        var handle = CreateFile(directory, 0x00120080 /* READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE */,
            3, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("DiagnosticDirectoryUnavailable",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return handle;
    }

    private static string ReadDaclSddl(SafeFileHandle handle)
    {
        var result = GetSecurityInfo(handle, FileObjectType, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero,
            out _, IntPtr.Zero, out var descriptor);
        if (result != 0 || descriptor == IntPtr.Zero)
            throw new InvalidOperationException("DiagnosticDirectoryAclUnavailable", new Win32Exception((int)result));
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, DaclSecurityInformation,
                    out var text, out _))
                throw new InvalidOperationException("DiagnosticDirectoryAclUnavailable",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            try { return Marshal.PtrToStringUni(text) ?? string.Empty; }
            finally { LocalFree(text); }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    private static FileInformation Information(SafeFileHandle handle, string reason)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new InvalidOperationException(reason, new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
    }

    internal static void RequireHandlePath(SafeFileHandle handle, string path, string reason)
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

    private static SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template) => CreateFileNative(
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + Path.GetFullPath(path),
            access, share, security, creation, flags, template);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInformation,
        IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor, uint revision,
        uint securityInformation, out IntPtr stringSecurityDescriptor, out uint stringLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
