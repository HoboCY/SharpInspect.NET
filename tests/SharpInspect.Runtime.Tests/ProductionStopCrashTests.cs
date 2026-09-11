using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionStopCrashTests
{
    [Fact]
    public async Task V145_D01_KilledHostCannotTurnAnAcceptedStopIntoNormalCompletionOnRestart()
    {
        const string childRootVariable = "SHARPINSPECT_V145_CRASH_CHILD_ROOT";
        var childRoot = Environment.GetEnvironmentVariable(childRootVariable);
        if (childRoot is not null)
        {
            var childOptions = new ProductionStoreOptions(Path.Combine(childRoot, "trace.db"));
            await using var childStore = new SqliteCommandStore(childOptions);
            Assert.True((await childStore.Initialization).Committed);
            // Hold completion scheduling while retaining the real accepted command and
            // durable writer. The parent kills this host before Dispose can run.
            await using var runtime = new StationRuntime(childStore, TimeSpan.FromSeconds(30));
            var until = DateTime.UtcNow.AddSeconds(10);
            while ((await runtime.GetSnapshotAsync()).Store.State != HealthState.Healthy && DateTime.UtcNow < until)
                await Task.Delay(20);
            var correlation = Guid.NewGuid();
            var accepted = await runtime.SubmitAsync(new GracefulProductionStopCommand(correlation,
                new CommandInvocation(CommandSource.PhysicalConsole)));
            Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
            Assert.Equal(AuditPersistence.Persisted, accepted.Audit);
            var marker = Path.Combine(childRoot, "accepted.tmp");
            await File.WriteAllTextAsync(marker, correlation.ToString("D"));
            File.Move(marker, Path.Combine(childRoot, "accepted.txt"));
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "v145-stop-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment[childRootVariable] = root;
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ProductionStopCrashTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + typeof(ProductionStopCrashTests).FullName + "." +
            nameof(V145_D01_KilledHostCannotTurnAnAcceptedStopIntoNormalCompletionOnRestart));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var acceptedPath = Path.Combine(root, "accepted.txt");
        try
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(acceptedPath) && !process.HasExited && DateTime.UtcNow < until) await Task.Delay(20);
            Assert.True(File.Exists(acceptedPath), "Child did not durably accept Stop before its kill boundary.");
            // Only the explicitly created test process tree is terminated.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var correlation = Guid.Parse(await File.ReadAllTextAsync(acceptedPath));
            var options = new ProductionStoreOptions(Path.Combine(root, "trace.db"));
            await using var store = new SqliteCommandStore(options);
            Assert.True((await store.Initialization).Committed);
            await using (var restarted = new StationRuntime(store, TimeSpan.FromMilliseconds(20)))
            {
                await Task.Delay(100);
                Assert.False((await restarted.GetSnapshotAsync()).Ready);
            }
            var history = await new SqliteCommandTraceQuery(options).QueryAsync(new(CorrelationId: correlation));
            Assert.Equal(CommandDisposition.Accepted, Assert.Single(history.Records).Disposition);
            Assert.DoesNotContain(history.Records, value => value.Phase == CommandAuditPhase.Completed);
            Console.WriteLine("V145_D01 abrupt process evidence: " + root);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(root, "process.log"), await output + await error);
        }
    }
}
