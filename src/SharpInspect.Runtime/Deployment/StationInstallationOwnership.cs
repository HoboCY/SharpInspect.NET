using System.ComponentModel;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Deployment;

/// <summary>
/// File-system ownership for one station installation. The protected root and the installation
/// marker carry exactly one protected DACL with full control for the configured runtime identity,
/// the configured coordinator identity, LocalSystem and BuiltinAdministrators. Validity always
/// re-reads the stored descriptor; inherited rules, deny rules, unknown trustees, partial rights,
/// duplicates and missing identities are all refused. Provision may only run as the configured
/// coordinator identity, LocalSystem or an administrator.
/// </summary>
internal static class StationInstallationOwnership
{
    internal static bool IsProvisioningIdentity(SecurityIdentifier current, SecurityIdentifier coordinator,
        bool isAdministrator) =>
        current.Equals(coordinator) || current.Equals(StationDeploymentOwnership.LocalSystem) || isAdministrator;

    internal static DirectorySecurity CreateRestrictedDirectorySecurity(
        IReadOnlyList<SecurityIdentifier> identities)
    {
        var security = new DirectorySecurity();
        Restrict(security, identities);
        return security;
    }

    internal static FileSecurity CreateRestrictedFileSecurity(IReadOnlyList<SecurityIdentifier> identities)
    {
        var security = new FileSecurity();
        Restrict(security, identities);
        return security;
    }

    /// <summary>An exact protected DACL: no inherited rules, no deny rules, one full-control allow
    /// rule for every configured identity and nothing else. The owner is not part of this check:
    /// provisioning may run as the coordinator, LocalSystem or an administrator user, while
    /// verification later runs as a different configured identity.</summary>
    internal static bool ValidateRestrictedAcl(string path, bool isDirectory,
        IReadOnlyList<SecurityIdentifier> identities, out string reason)
    {
        reason = string.Empty;
        var expected = new HashSet<SecurityIdentifier>(identities);
        var seen = new HashSet<SecurityIdentifier>();
        var rules = 0;
        try
        {
            FileSystemSecurity security = isDirectory
                ? new DirectorySecurity(path, AccessControlSections.Access)
                : new FileSecurity(path, AccessControlSections.Access);
            if (!security.AreAccessRulesProtected)
            {
                reason = "AclNotProtected";
                return false;
            }

            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true,
                         includeInherited: true, targetType: typeof(SecurityIdentifier)))
            {
                rules++;
                if (rule.AccessControlType != AccessControlType.Allow || rule.IsInherited)
                {
                    reason = "AclRuleInvalid";
                    return false;
                }

                if (rule.IdentityReference is not SecurityIdentifier sid || !expected.Contains(sid))
                {
                    reason = "AclIdentityInvalid";
                    return false;
                }

                if (rule.InheritanceFlags != InheritanceFlags.None)
                {
                    reason = "AclInheritanceInvalid";
                    return false;
                }

                if ((rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
                {
                    reason = "AclRightsInvalid";
                    return false;
                }

                seen.Add(sid);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                    or SecurityException or Win32Exception)
        {
            reason = "AclUnavailable";
            return false;
        }

        if (rules != expected.Count || seen.Count != expected.Count)
        {
            reason = "AclIdentityMissing";
            return false;
        }

        return true;
    }

    private static void Restrict(FileSystemSecurity security, IReadOnlyList<SecurityIdentifier> identities)
    {
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in identities)
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                AccessControlType.Allow));
    }
}
