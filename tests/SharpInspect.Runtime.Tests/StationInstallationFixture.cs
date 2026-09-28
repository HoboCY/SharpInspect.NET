using System.Security.AccessControl;
using System.Security.Principal;
using SharpInspect.Runtime.Deployment;

namespace SharpInspect.Runtime.Tests;

/// <summary>Explicit isolated installation with the real OS exclusion protocol; never station qualification.</summary>
internal static class StationInstallationFixture
{
    internal static StationInstallationOptions Provision(string station, string database)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var root = Path.GetDirectoryName(database)!;
        Directory.CreateDirectory(root);
        var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("FixtureSidUnavailable");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { current, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
        var options = new StationInstallationOptions(station, Guid.NewGuid(), root, database, current.Value, current.Value);
        StationInstallationRegistry.Provision(options);
        return options;
    }
}
