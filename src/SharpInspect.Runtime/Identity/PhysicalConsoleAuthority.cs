using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SharpInspect.Runtime.Identity;

internal sealed record ConsoleAuthority(bool PhysicalConsole, bool WindowsAdministrator, string? WindowsSid);

internal interface IPhysicalConsoleAuthority
{
    ConsoleAuthority Observe();
}

internal sealed class PhysicalConsoleAuthority : IPhysicalConsoleAuthority
{
    public ConsoleAuthority Observe()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive) return new(false, false, null);
        try
        {
            using var process = Process.GetCurrentProcess();
            var active = WTSGetActiveConsoleSessionId();
            if (active == uint.MaxValue || process.SessionId != active || GetSystemMetrics(0x1000) != 0)
                return new(false, false, null);
            using var identity = WindowsIdentity.GetCurrent();
            return new(true, new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), identity.User?.Value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new(false, false, null); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);
}
