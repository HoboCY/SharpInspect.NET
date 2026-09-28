using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Deployment;

/// <summary>Which deployment role holds one station deployment lock. The owner is recorded for
/// diagnostics and audit only; it grants no authority beyond the protected object DACL.</summary>
public enum StationDeploymentOwner
{
    Runtime = 1,
    Coordinator = 2
}

/// <summary>
/// Kernel-object ownership for the machine-wide station deployment mutex. The object is created
/// with one protected DACL that grants full control only to the configured runtime identity, the
/// configured coordinator identity, LocalSystem and BuiltinAdministrators. Whether the object was
/// just created or already existed, its stored security descriptor is re-read and validated: a
/// permissive, partial or substituted DACL and an unconfigured owner are refused.
/// </summary>
internal static class StationDeploymentOwnership
{
    // MUTEX_ALL_ACCESS: STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | MUTEX_MODIFY_STATE.
    internal const uint MutexAllAccess = 0x001F0001;
    internal const string MutexNamePrefix = "Global\\SharpInspect.StationDeployment.";
    private const uint OwnerAndDacl = 0x00000005; // OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION
    private const int SeKernelObject = 6;

    internal static SecurityIdentifier LocalSystem { get; } = new(WellKnownSidType.LocalSystemSid, null);

    internal static SecurityIdentifier Administrators { get; } =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Machine-wide mutex name for one station identity. The name never contains a path,
    /// so moving the installation root does not silently switch the exclusion domain.</summary>
    internal static string MutexName(string identityHash)
    {
        if (string.IsNullOrWhiteSpace(identityHash))
            throw new ArgumentException("StationDeploymentIdentityHashRequired", nameof(identityHash));
        return MutexNamePrefix + identityHash;
    }

    /// <summary>The exact identities allowed by the protected deployment DACL: the configured
    /// runtime identity, the configured coordinator identity, LocalSystem and
    /// BuiltinAdministrators, each at most once.</summary>
    internal static IReadOnlyList<SecurityIdentifier> DeploymentIdentities(StationInstallationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var identities = new List<SecurityIdentifier>(4);
        Add(options.RuntimeIdentity);
        Add(options.CoordinatorIdentity);
        Add(LocalSystem);
        Add(Administrators);
        return identities;

        void Add(SecurityIdentifier identity)
        {
            if (!identities.Contains(identity)) identities.Add(identity);
        }
    }

    /// <summary>Creates or opens the named mutex with the exact protected deployment DACL and then
    /// validates the actual stored descriptor. An existing object that is not exactly protected
    /// for the configured identities is refused, never repaired.</summary>
    internal static SafeWaitHandle CreateOrOpenProtectedMutex(string name,
        IReadOnlyList<SecurityIdentifier> identities)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("StationDeploymentLockNameRequired", nameof(name));
        ArgumentNullException.ThrowIfNull(identities);

        var current = WindowsIdentity.GetCurrent().User
                      ?? throw new InvalidOperationException("StationDeploymentLockIdentityUnavailable");
        var descriptor = BuildMutexDescriptor(current, identities);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var attributes = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            Marshal.StructureToPtr(new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = pinned.AddrOfPinnedObject(),
                Inherit = 0
            }, attributes, false);

            var handle = CreateMutexEx(attributes, name, 0, MutexAllAccess);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new InvalidOperationException(
                    error == 5 ? "StationDeploymentLockAccessDenied" : "StationDeploymentLockUnavailable",
                    new Win32Exception(error));
            }

            if (!ValidateMutexSecurity(handle, identities, out var reason))
            {
                handle.Dispose();
                throw new InvalidOperationException(reason);
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(attributes);
            pinned.Free();
        }
    }

    /// <summary>Re-reads the stored security descriptor of one mutex handle. The owner must be one
    /// of the configured identities or the current process identity, and the DACL must be present,
    /// protected and exactly one full-control allow rule per configured identity.</summary>
    internal static bool ValidateMutexSecurity(SafeWaitHandle handle, IReadOnlyList<SecurityIdentifier> identities,
        out string reason)
    {
        reason = string.Empty;
        var result = GetSecurityInfo(handle, SeKernelObject, OwnerAndDacl, out _, IntPtr.Zero, out _,
            IntPtr.Zero, out var descriptor);
        if (result != 0 || descriptor == IntPtr.Zero)
        {
            reason = "StationDeploymentLockSecurityUnavailable";
            return false;
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, OwnerAndDacl,
                    out var text, out _))
            {
                reason = "StationDeploymentLockSecurityUnavailable";
                return false;
            }

            try
            {
                var raw = new RawSecurityDescriptor(Marshal.PtrToStringUni(text) ?? string.Empty);
                if ((raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
                    raw.DiscretionaryAcl is null)
                {
                    reason = "StationDeploymentLockAclMissing";
                    return false;
                }

                if ((raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
                {
                    reason = "StationDeploymentLockAclNotProtected";
                    return false;
                }

                var current = WindowsIdentity.GetCurrent().User;
                if (raw.Owner is not { } objectOwner ||
                    (!identities.Contains(objectOwner) && (current is null || !objectOwner.Equals(current))))
                {
                    reason = "StationDeploymentLockOwnerInvalid";
                    return false;
                }

                var expected = new HashSet<SecurityIdentifier>(identities);
                var seen = new HashSet<SecurityIdentifier>();
                var count = 0;
                foreach (GenericAce ace in raw.DiscretionaryAcl)
                {
                    count++;
                    if (ace is not CommonAce common || common.AceQualifier != AceQualifier.AccessAllowed ||
                        common.AceFlags != AceFlags.None || (uint)common.AccessMask != MutexAllAccess ||
                        !expected.Contains(common.SecurityIdentifier))
                    {
                        reason = "StationDeploymentLockAclSubstituted";
                        return false;
                    }

                    seen.Add(common.SecurityIdentifier);
                }

                if (count != expected.Count || seen.Count != expected.Count)
                {
                    reason = "StationDeploymentLockAclIncomplete";
                    return false;
                }

                return true;
            }
            finally
            {
                LocalFree(text);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static RawSecurityDescriptor BuildMutexDescriptor(SecurityIdentifier owner,
        IReadOnlyList<SecurityIdentifier> identities)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, 0);
        foreach (var identity in identities)
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed,
                (int)MutexAllAccess, identity, false, null));
        return new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected, owner, null, null, acl);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(IntPtr attributes, string name, uint flags,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeWaitHandle handle, int objectType, uint securityInformation,
        out IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor, uint revision,
        uint securityInformation, out IntPtr stringSecurityDescriptor, out uint stringLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
