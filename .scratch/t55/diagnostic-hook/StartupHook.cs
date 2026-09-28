using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class StartupHook
{
    private static readonly object Sync = new();
    private static readonly Queue<object> Records = new();
    private static long Dropped;
    [ThreadStatic] private static bool Inside;

    public static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("T55_DIAGNOSTIC_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                object[] records;
                lock (Sync) records = Records.ToArray();
                if (records.Length == 0) return;
                var runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SharpInspect.Runtime");
                var runtimePath = runtime?.Location;
                string? runtimeHash = null;
                if (!string.IsNullOrEmpty(runtimePath))
                {
                    using var input = File.OpenRead(runtimePath);
                    using var hash = SHA256.Create();
                    runtimeHash = Convert.ToHexString(hash.ComputeHash(input));
                }
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, $"first-chance-{Environment.ProcessId}.json"),
                    JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, CapturedAtUtc = DateTimeOffset.UtcNow,
                        Runtime = runtimePath, RuntimeSha256 = runtimeHash, Dropped, Records = records },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* Diagnostics never change the test's disposition. */ }
        };
    }

    private static void Observe(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (Inside) return;
        Inside = true;
        try
        {
            var frames = new StackTrace(args.Exception, false).GetFrames() ?? Array.Empty<StackFrame>();
            var methods = frames.Select(frame => frame.GetMethod())
                .Where(method => method?.DeclaringType?.FullName?.StartsWith("SharpInspect.Runtime", StringComparison.Ordinal) == true)
                .Take(12).Select(method => method!.DeclaringType!.FullName + "." + method.Name).ToArray();
            if (methods.Length == 0) return;
            // Record closed reason codes only, never arbitrary exception payloads,
            // credentials, SQL text, method arguments or account data.
            var message = args.Exception.Message;
            var code = Regex.IsMatch(message, "^[A-Za-z][A-Za-z0-9_.]{0,200}$") ? message : "NonCodeMessageOmitted";
            lock (Sync)
            {
                if (Records.Count == 256) { Records.Dequeue(); Dropped++; }
                Records.Enqueue(new { AtUtc = DateTimeOffset.UtcNow, Type = args.Exception.GetType().FullName,
                    Code = code, Methods = methods });
            }
        }
        catch { }
        finally { Inside = false; }
    }
}
