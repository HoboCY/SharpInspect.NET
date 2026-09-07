using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpInspect.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmHangProcessTests
{
    private readonly ITestOutputHelper _output;

    public AlgorithmHangProcessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task V113_H01_UnreturnedAlgorithmFixesTimeoutKeepsLoanAndLatchesProcessHung()
    {
        var run = await RunProbeAsync("hang", TimeSpan.FromSeconds(8));
        Assert.True(run.Killed, "The deliberately unreturned child must be terminated by the test harness.");
        AssertSafeProbeOutput(run);

        var terminal = Event(run, "execute-terminal");
        Assert.Equal("Timeout", String(terminal, "status"));
        Assert.Equal("Unknown", String(terminal, "decision"));
        Assert.True(Long(terminal, "admittedMonotonicTimestamp") > 0);
        Assert.True(Long(terminal, "admittedMonotonicFrequency") > 0);
        AssertTiming(terminal, timeoutMilliseconds: 250, graceMilliseconds: 150);

        var hung = Event(run, "hung-observed");
        Assert.True(Bool(hung, "hung"));
        Assert.True(Bool(hung, "guardHung"));
        Assert.Equal("AlgorithmHung", String(hung, "blockedReason"));
        Assert.Equal(1, Int(hung, "outstandingLeases"));
        Assert.True(Bool(hung, "frameLoanActive"));
        Assert.Equal("Rejected", String(Event(run, "prepare-after-hung"), "status"));
        Assert.Equal("AlgorithmHung", String(Event(run, "prepare-after-hung"), "reason"));
        Assert.Equal("Rejected", String(Event(run, "new-engine"), "status"));
        Assert.Equal("AlgorithmHung", String(Event(run, "new-engine"), "reason"));
        var inFlight = Event(run, "prepare-inflight-after-hung");
        Assert.Equal("Rejected", String(inFlight, "status"));
        Assert.False(Bool(inFlight, "published"));
        Assert.Equal("AlgorithmHung", String(inFlight, "reason"));
        Assert.Equal(1, Int(inFlight, "disposeCount"));
    }

    [Fact]
    public async Task V113_H02_LatePassCannotReplaceFixedTimeoutAndEventuallyReleasesFrame()
    {
        var run = await RunProbeAsync("late-pass", TimeSpan.FromSeconds(8));
        Assert.False(run.Killed);
        Assert.Equal(0, run.ExitCode);
        AssertSafeProbeOutput(run);

        var terminal = Event(run, "execute-terminal");
        Assert.Equal("Timeout", String(terminal, "status"));
        Assert.Equal("Unknown", String(terminal, "decision"));
        var late = Event(run, "late-observed");
        Assert.True(Bool(late, "algorithmReturned"));
        Assert.Equal("Timeout", String(late, "status"));
        Assert.Equal("Unknown", String(late, "decision"));
        Assert.Equal("AlgorithmExecutionTimeout", String(late, "reason"));
        Assert.True(Bool(late, "guardHung"));
        Assert.Equal(0, Int(late, "outstandingLeases"));
        Assert.False(Bool(late, "frameLoanActive"));
    }

    [Fact]
    public async Task V113_H03_LateExceptionCannotReplaceFixedTimeoutOrExposeExceptionDetail()
    {
        var run = await RunProbeAsync("late-exception", TimeSpan.FromSeconds(8));
        Assert.False(run.Killed);
        Assert.Equal(0, run.ExitCode);
        AssertSafeProbeOutput(run);

        var terminal = Event(run, "execute-terminal");
        var late = Event(run, "late-observed");
        Assert.Equal("Timeout", String(terminal, "status"));
        Assert.Equal("Unknown", String(terminal, "decision"));
        Assert.True(Bool(late, "algorithmReturned"));
        Assert.Equal("Timeout", String(late, "status"));
        Assert.Equal("Unknown", String(late, "decision"));
        Assert.Equal("AlgorithmExecutionTimeout", String(late, "reason"));
        Assert.DoesNotContain("LateFailure", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, Int(late, "outstandingLeases"));
        Assert.False(Bool(late, "frameLoanActive"));
    }

    [Fact]
    public async Task V113_H04_BlockingCancellationCallbackKeepsLoanAfterGraceUntilChildIsKilled()
    {
        var run = await RunProbeAsync("cancel-callback", TimeSpan.FromSeconds(8));
        Assert.True(run.Killed, "The callback intentionally blocks beyond the grace period.");
        AssertSafeProbeOutput(run);

        var terminal = Event(run, "execute-terminal");
        Assert.Equal("Cancelled", String(terminal, "status"));
        Assert.Equal("Unknown", String(terminal, "decision"));
        var hung = Event(run, "hung-observed");
        Assert.True(Bool(hung, "hung"));
        Assert.True(Bool(hung, "callbackEntered"));
        Assert.Equal(1, Int(hung, "outstandingLeases"));
        Assert.True(Bool(hung, "frameLoanActive"));
    }

    [Fact]
    public async Task V113_H05_NewProcessStartsWithClearGuardButStationRuntimeStillRequiresStartupRecovery()
    {
        var killed = await RunProbeAsync("hang", TimeSpan.FromSeconds(8));
        Assert.True(killed.Killed);
        AssertSafeProbeOutput(killed);

        var fresh = await RunProbeAsync("fresh", TimeSpan.FromSeconds(5));
        Assert.False(fresh.Killed);
        Assert.Equal(0, fresh.ExitCode);
        AssertSafeProbeOutput(fresh);
        var freshEvent = Event(fresh, "fresh-process");
        Assert.False(Bool(freshEvent, "hung"));
        Assert.Equal("None", String(freshEvent, "blockedReason"));
        Assert.False(Bool(freshEvent, "ready"));
        Assert.True(Bool(freshEvent, "startupRecoveryBlocked"));
    }

    [Fact]
    public async Task V113_H06_LazyGraceExpiryBlocksPreparedReplacementAtNextAdmission()
    {
        var run = await RunProbeAsync("lazy", TimeSpan.FromSeconds(8));
        Assert.False(run.Killed);
        Assert.Equal(0, run.ExitCode);
        AssertSafeProbeOutput(run);

        var terminal = Event(run, "lazy-terminal");
        Assert.Equal("Timeout", String(terminal, "status"));
        Assert.Equal("Unknown", String(terminal, "decision"));
        var admission = Event(run, "lazy-admission");
        Assert.Equal("Rejected", String(admission, "status"));
        Assert.False(Bool(admission, "executed"));
        Assert.Equal("AlgorithmHung", String(admission, "reason"));
        Assert.Equal(0, Int(admission, "secondExecuteCalls"));
        Assert.False(Bool(admission, "prepareAfterHung"));
        Assert.Equal("AlgorithmHung", String(admission, "prepareReason"));
        Assert.True(Bool(admission, "guardHung"));
    }

    [Fact]
    public async Task V113_H07_AlarmGovernanceRunsOnlyInsideFreshChildProcess()
    {
        var run = await RunProbeAsync("alarm", TimeSpan.FromSeconds(20));
        Assert.False(run.Killed);
        Assert.Equal(0, run.ExitCode);
        AssertSafeProbeOutput(run);

        var matches = run.Events.Where(item =>
            item.TryGetProperty("eventName", out var eventName) &&
            eventName.GetString() == "alarm-governance").ToArray();
        Assert.Single(matches);
        Assert.True(matches[0].GetProperty("passed").GetBoolean());
    }

    private async Task<ProbeRun> RunProbeAsync(string mode, TimeSpan hardDeadline)
    {
        Assert.True(Environment.Is64BitProcess, "The T13 probe must run in an x64 test process.");
        var probe = LocateProbe();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(probe)!
        };
        startInfo.ArgumentList.Add(probe);
        startInfo.ArgumentList.Add(mode);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var started = false;
        var evidenceWritten = false;
        var processId = 0;
        try
        {
            started = process.Start();
            Assert.True(started);
            processId = process.Id;
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, 64 * 1024);
            var stderrTask = ReadBoundedAsync(process.StandardError, 16 * 1024);
            var waitTask = process.WaitForExitAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(hardDeadline));
            var killed = false;
            if (completed != waitTask)
            {
                killed = true;
                TryKill(process);
                await waitTask.WaitAsync(TimeSpan.FromSeconds(3));
            }

            var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(3));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(3));
            var exitCode = process.ExitCode;
            WriteProbeEvidence(mode, processId, exitCode, killed, stdout, stderr);
            evidenceWritten = true;
            return new ProbeRun(processId, exitCode, killed, stdout, stderr, ParseEvents(stdout));
        }
        finally
        {
            if (!evidenceWritten)
                _output.WriteLine("probe mode={0} pid={1} evidence=unavailable", mode,
                    processId == 0 ? "unavailable" : processId.ToString());
            if (started)
            {
                TryKill(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    private void WriteProbeEvidence(string mode, int processId, int exitCode, bool killed,
        string stdout, string stderr)
    {
        _output.WriteLine("probe mode={0} pid={1} exitCode={2} killed={3}",
            mode, processId, exitCode, killed);
        _output.WriteLine("probe stdout={0}", BoundEvidence(stdout, 8 * 1024));
        if (stderr.Length != 0)
            _output.WriteLine("probe stderr={0}", BoundEvidence(stderr, 4 * 1024));
    }

    private static string BoundEvidence(string value, int maximumCharacters)
    {
        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "\\n{output-truncated}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[2048];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            if (builder.Length < maximumCharacters)
            {
                var accepted = Math.Min(count, maximumCharacters - builder.Length);
                builder.Append(buffer, 0, accepted);
                truncated |= accepted != count;
            }
            else
            {
                truncated = true;
            }
        }

        return truncated ? builder.Append("\n{\"event\":\"output-truncated\"}").ToString() : builder.ToString();
    }

    private static string LocateProbe()
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "AlgorithmHangProbe",
            "SharpInspect.AlgorithmHangProbe.dll");
        if (!File.Exists(probe)) throw new Xunit.Sdk.XunitException("AlgorithmHangProbeNotBuilt");
        return probe;
    }

    private static IReadOnlyList<JsonElement> ParseEvents(string stdout)
    {
        var events = new List<JsonElement>();
        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            events.Add(document.RootElement.Clone());
        }

        return events;
    }

    private static JsonElement Event(ProbeRun run, string eventName)
    {
        var matches = run.Events.Where(item => String(item, "event") == eventName).ToArray();
        Assert.Single(matches);
        return matches[0];
    }

    private static void AssertTiming(JsonElement terminal, long timeoutMilliseconds, long graceMilliseconds)
    {
        var timing = terminal.GetProperty("timing");
        Assert.True(timing.GetProperty("present").GetBoolean());
        Assert.Equal(timeoutMilliseconds, timing.GetProperty("timeoutMilliseconds").GetInt64());
        Assert.Equal(graceMilliseconds, timing.GetProperty("graceMilliseconds").GetInt64());
        Assert.Equal("Probe.ExecutionPolicy", timing.GetProperty("policyId").GetString());
        Assert.Equal("v1", timing.GetProperty("policyVersion").GetString());
        Assert.Equal(64, timing.GetProperty("policyContentHash").GetString()!.Length);
    }

    private static void AssertSafeProbeOutput(ProbeRun run)
    {
        Assert.DoesNotContain("message", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Exception", run.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", run.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static string String(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? string.Empty;
    private static bool Bool(JsonElement element, string property) => element.GetProperty(property).GetBoolean();
    private static int Int(JsonElement element, string property) => element.GetProperty(property).GetInt32();
    private static long Long(JsonElement element, string property) => element.GetProperty(property).GetInt64();

    private sealed record ProbeRun(int ProcessId, int ExitCode, bool Killed, string Stdout, string Stderr,
        IReadOnlyList<JsonElement> Events);
}
