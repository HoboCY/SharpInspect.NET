using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.OutboxProbe;
using SharpInspect.Runtime.Outbox;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V152_X01 and V152_X02: real receiver-process evidence for the isolated receiver only. The
/// parent spawns an actual probe process, waits for the flushed durability boundary, terminates
/// only that child process tree, reopens the receiver SQLite file to prove which business effects
/// survived, and then replays the same delivery identity and exact bytes from a new child process.
/// This is not a production MES, receiver, hardware or qualification claim, and it does not claim
/// anything about an interrupted sender or Core transaction.
/// </summary>
public sealed class ProductionOutboxReceiverProcessTests
{
    private static readonly TimeSpan BoundaryDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TerminationDeadline = TimeSpan.FromSeconds(10);

    [Theory]
    [Trait("VerificationId", "V152_X01")]
    [InlineData(OutboxReceiverProbeContract.BeforeSend, 0L)]
    [InlineData(OutboxReceiverProbeContract.BeforeCommit, 0L)]
    [InlineData(OutboxReceiverProbeContract.AfterCommit, 1L)]
    public async Task V152_X01_KilledReceiverProcessKeepsOnlyCommittedEffectAndReplayAcceptsOnce(
        string phase, long effectsAfterKill)
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(Payload("V152-X01-" + phase));
        var export = fixture.ExportProbeInput(delivery);

        // before-send: the child is killed before it invokes receiver acceptance at all.
        // before-commit: the child is killed after the receipt and business rows were inserted
        // inside the SQL transaction but before COMMIT, so recovery must discard both.
        // after-commit: the child is killed after COMMIT and before it returns the acceptance.
        var killed = await RunChildAsync(export, phase);
        AssertBoundaryEvidence(killed, phase);
        AssertEffectCounts(export, delivery, effectsAfterKill, killed);
        var storedBeforeReplay = IsolatedOutboxReceiver.ReadStoredReceipt(export.DatabasePath, delivery.DeliveryId);
        Assert.True((storedBeforeReplay is not null) == (effectsAfterKill == 1),
            Failure($"The reopened receiver stored a receipt={storedBeforeReplay is not null} for " +
                $"{effectsAfterKill} committed effect(s).", killed));
        if (storedBeforeReplay is not null)
            Assert.Equal(delivery.Payload!.ContentHash, storedBeforeReplay.PayloadHash);

        var replay = await RunChildAsync(export, OutboxReceiverProbeContract.NoBoundary);
        Assert.NotEqual(killed.ProcessId, replay.ProcessId);
        var accepted = AssertAccepted(replay, delivery);
        var verified = OutboxAcceptanceVerifier.VerifyReceipt(delivery, accepted.Receipt);
        Assert.Equal(accepted.ReceiptHash, verified.ReceiptHash);
        Assert.NotEqual(Guid.Empty, Guid.Parse(verified.ReceiptId));
        Assert.Equal(TimeSpan.Zero, verified.AcceptedAtUtc.Offset);
        AssertEffectCounts(export, delivery, 1L, replay);
        var storedAfterReplay = IsolatedOutboxReceiver.ReadStoredReceipt(export.DatabasePath, delivery.DeliveryId);
        Assert.NotNull(storedAfterReplay);
        Assert.Equal(accepted.Receipt, storedAfterReplay!.Receipt);
        if (storedBeforeReplay is not null)
            Assert.Equal(storedBeforeReplay.Receipt, accepted.Receipt);
    }

    [Fact]
    [Trait("VerificationId", "V152_X02")]
    public async Task V152_X02_CommittedReceiverReplayReturnsOriginalReceiptAndDifferentBytesStayConflict()
    {
        using var fixture = new OutboxReceiverFixture();
        var delivery = fixture.Delivery(Payload("V152-X02"));
        var payload = delivery.Payload!;
        var export = fixture.ExportProbeInput(delivery);

        var killed = await RunChildAsync(export, OutboxReceiverProbeContract.AfterCommit);
        AssertBoundaryEvidence(killed, OutboxReceiverProbeContract.AfterCommit);
        var original = IsolatedOutboxReceiver.ReadStoredReceipt(export.DatabasePath, delivery.DeliveryId);
        Assert.NotNull(original);
        Assert.Equal(payload.ContentHash, original!.PayloadHash);
        AssertEffectCounts(export, delivery, 1L, killed);

        // The new child replays the same delivery identity and the exact same payload bytes.
        var duplicate = await RunChildAsync(export, OutboxReceiverProbeContract.NoBoundary);
        Assert.NotEqual(killed.ProcessId, duplicate.ProcessId);
        var accepted = AssertAccepted(duplicate, delivery);
        Assert.Equal(original.Receipt, accepted.Receipt);
        var verified = OutboxAcceptanceVerifier.VerifyReceipt(delivery, accepted.Receipt);
        Assert.Equal(original.ReceiptId, verified.ReceiptId);
        Assert.Equal(original.AcceptedAtUtc, verified.AcceptedAtUtc);
        Assert.Equal(accepted.ReceiptHash, verified.ReceiptHash);
        AssertEffectCounts(export, delivery, 1L, duplicate);

        // Same delivery identity with different bytes is a permanent conflict, not a second effect.
        var conflicting = fixture.Delivery(Payload("V152-X02-conflict"), delivery.DeliveryId,
            delivery.InspectionId);
        var conflictExport = fixture.ExportProbeInput(conflicting);
        var conflict = await RunChildAsync(conflictExport, OutboxReceiverProbeContract.NoBoundary);
        AssertConflict(conflict, conflicting);
        AssertEffectCounts(export, delivery, 1L, conflict);
        var unchanged = IsolatedOutboxReceiver.ReadStoredReceipt(export.DatabasePath, delivery.DeliveryId);
        Assert.NotNull(unchanged);
        Assert.Equal(original.Receipt, unchanged!.Receipt);
        Assert.Equal(payload.ContentHash, unchanged.PayloadHash);
        Assert.NotEqual(conflicting.Payload!.ContentHash, unchanged.PayloadHash);
    }

    private static byte[] Payload(string label) =>
        Encoding.UTF8.GetBytes("{\"schema\":\"outbox-probe\",\"label\":\"" + label + "\",\"decision\":\"Pass\"}");

    private static void AssertEffectCounts(OutboxReceiverProbeExport export, OutboxDelivery delivery,
        long expected, ChildRun run)
    {
        var total = IsolatedOutboxReceiver.EffectCount(export.DatabasePath);
        var deliveryEffects = IsolatedOutboxReceiver.DeliveryEffectCount(export.DatabasePath, delivery.DeliveryId);
        Assert.True(total == expected && deliveryEffects == expected,
            Failure($"The reopened receiver must expose exactly {expected} committed effect(s) for the " +
                $"delivery but exposed total={total} delivery={deliveryEffects}.", run));
    }

    private static void AssertBoundaryEvidence(ChildRun run, string phase)
    {
        Assert.True(run.ExitCode != 0,
            Failure($"The killed child must exit with a nonzero code but exited with {run.ExitCode}.", run));
        Assert.True(run.KilledAtBoundary,
            Failure("The parent must terminate the child at the requested durability boundary.", run));
        Assert.Equal(phase, run.BoundaryPhase);
        Assert.DoesNotContain(run.Events, item => EventName(item) == OutboxReceiverProbeContract.AcceptEvent);
        Assert.DoesNotContain(run.Events, item => EventName(item) == OutboxReceiverProbeContract.ErrorEvent);
        Assert.DoesNotContain("receiptBase64", run.Stdout, StringComparison.Ordinal);
        var boundary = SingleEvent(run, OutboxReceiverProbeContract.BoundaryEvent);
        Assert.Equal(phase, boundary.GetProperty("phase").GetString());
        Assert.Equal((long)run.ProcessId, boundary.GetProperty("executedPid").GetInt64());
        Assert.Equal(phase is OutboxReceiverProbeContract.AfterCommit ? 1L : 0L,
            IsolatedOutboxReceiver.EffectCount(run.DatabasePath));
    }

    private static AcceptedEvidence AssertAccepted(ChildRun run, OutboxDelivery delivery)
    {
        Assert.True(run.ExitCode == 0,
            Failure($"The replay child must accept the delivery and exit with code 0 but exited with {run.ExitCode}.", run));
        Assert.DoesNotContain(run.Events, item => EventName(item) == OutboxReceiverProbeContract.ConflictEvent);
        Assert.DoesNotContain(run.Events, item => EventName(item) == OutboxReceiverProbeContract.ErrorEvent);
        var accepted = SingleEvent(run, OutboxReceiverProbeContract.AcceptEvent);
        var payload = delivery.Payload!;
        Assert.Equal((long)run.ProcessId, accepted.GetProperty("executedPid").GetInt64());
        Assert.Equal(delivery.DeliveryId.ToString("D"), accepted.GetProperty("deliveryId").GetString());
        Assert.Equal(delivery.ContentHash, accepted.GetProperty("deliveryContentHash").GetString());
        Assert.Equal(payload.ContentHash, accepted.GetProperty("payloadHash").GetString());
        Assert.Equal((long)payload.ByteLength, accepted.GetProperty("payloadBytes").GetInt64());
        Assert.Equal(1L, accepted.GetProperty("effectCount").GetInt64());
        Assert.Equal(1L, accepted.GetProperty("deliveryEffectCount").GetInt64());
        var receipt = Convert.FromBase64String(accepted.GetProperty("receiptBase64").GetString()!);
        Assert.InRange(receipt.Length, 1, OutboxReceiverProbeContract.MaximumReceiptBytes);
        var receiptHash = accepted.GetProperty("receiptHash").GetString()!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(receipt)), receiptHash);
        return new(receipt, receiptHash);
    }

    private static void AssertConflict(ChildRun run, OutboxDelivery conflicting)
    {
        Assert.True(run.ExitCode == 0,
            Failure($"The conflicting child must report the conflict and exit with code 0 but exited with {run.ExitCode}.", run));
        Assert.DoesNotContain(run.Events, item => EventName(item) == OutboxReceiverProbeContract.AcceptEvent);
        Assert.DoesNotContain(run.Events, item => EventName(item) == OutboxReceiverProbeContract.ErrorEvent);
        var conflict = SingleEvent(run, OutboxReceiverProbeContract.ConflictEvent);
        Assert.Equal(OutboxReceiverProbeContract.ConflictReason, conflict.GetProperty("conflict").GetString());
        Assert.Equal((long)run.ProcessId, conflict.GetProperty("executedPid").GetInt64());
        Assert.Equal(conflicting.DeliveryId.ToString("D"), conflict.GetProperty("deliveryId").GetString());
        Assert.Equal(conflicting.Payload!.ContentHash, conflict.GetProperty("payloadHash").GetString());
        Assert.Equal(1L, conflict.GetProperty("effectCount").GetInt64());
        Assert.Equal(1L, conflict.GetProperty("deliveryEffectCount").GetInt64());
    }

    /// <summary>
    /// Runs the probe child to completion, or to the reported durability boundary and then kills
    /// only that child process tree. The killed path never disposes or rolls back the receiver.
    /// </summary>
    private static async Task<ChildRun> RunChildAsync(OutboxReceiverProbeExport export, string phase)
    {
        var probe = ProbePath();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(probe)!
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add("outbox-receiver");
        start.ArgumentList.Add(export.DatabasePath);
        start.ArgumentList.Add(export.InputPath);
        start.ArgumentList.Add(export.OwnerKeyPath);
        start.ArgumentList.Add(phase);

        using var process = new Process { StartInfo = start };
        var stdout = new BoundedText(64 * 1024);
        var stderr = new BoundedText(16 * 1024);
        var boundary = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        Task? stdoutPump = null;
        Task? stderrPump = null;
        try
        {
            started = process.Start();
            Assert.True(started, "The isolated receiver probe process could not be started.");
            stdoutPump = PumpLinesAsync(process.StandardOutput, stdout, boundary);
            stderrPump = PumpTextAsync(process.StandardError, stderr);
            var waitForExit = process.WaitForExitAsync();
            var killedAtBoundary = phase != OutboxReceiverProbeContract.NoBoundary;
            string? boundaryPhase = null;
            if (killedAtBoundary)
            {
                var reported = await Task.WhenAny(boundary.Task, waitForExit,
                    Task.Delay(BoundaryDeadline));
                Assert.True(ReferenceEquals(reported, boundary.Task),
                    Failure($"The child did not report the '{phase}' durability boundary within " +
                        $"{BoundaryDeadline.TotalSeconds:0} seconds.", export.DatabasePath, process.Id, stderr.Text,
                        stdout.Text));
                Assert.False(process.HasExited,
                    Failure("The child exited instead of blocking at the reported durability boundary.",
                        export.DatabasePath, process.Id, stderr.Text, stdout.Text));
                boundaryPhase = await boundary.Task;
                Assert.Equal(phase, boundaryPhase);
                process.Kill(entireProcessTree: true);
                await waitForExit.WaitAsync(TerminationDeadline);
                Assert.NotEqual(0, process.ExitCode);
            }
            else
            {
                var exited = await Task.WhenAny(waitForExit, Task.Delay(ExitDeadline));
                Assert.True(ReferenceEquals(exited, waitForExit),
                    Failure($"The child did not finish within {ExitDeadline.TotalSeconds:0} seconds.",
                        export.DatabasePath, process.Id, stderr.Text, stdout.Text));
                await waitForExit;
                Assert.True(process.ExitCode == 0,
                    Failure($"The child exited with code {process.ExitCode}.", export.DatabasePath, process.Id,
                        stderr.Text, stdout.Text));
            }

            await Task.WhenAll(stdoutPump, stderrPump).WaitAsync(TimeSpan.FromSeconds(5));
            var run = new ChildRun(process.Id, process.ExitCode, killedAtBoundary, boundaryPhase,
                export.DatabasePath, stdout.Text, stderr.Text, ParseEvents(stdout.Text));
            PersistChildEvidence(phase, run);
            return run;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TerminationDeadline);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                }
            }
            if (stdoutPump is not null) await ObserveAsync(stdoutPump);
            if (stderrPump is not null) await ObserveAsync(stderrPump);
        }
    }

    private static async Task PumpLinesAsync(StreamReader reader, BoundedText output,
        TaskCompletionSource<string> boundary)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;
            output.Append(line);
            output.Append("\n");
            if (line.StartsWith(OutboxReceiverProbeContract.BoundaryPrefix, StringComparison.Ordinal))
                boundary.TrySetResult(line[OutboxReceiverProbeContract.BoundaryPrefix.Length..]);
        }
    }

    private static async Task PumpTextAsync(StreamReader reader, BoundedText output)
    {
        var buffer = new char[2048];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            output.Append(new string(buffer, 0, count));
        }
    }

    private static async Task ObserveAsync(Task pump)
    {
        try { await pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
    }

    private static IReadOnlyList<JsonElement> ParseEvents(string stdout)
    {
        var events = new List<JsonElement>();
        foreach (var rawLine in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('{')) continue; // The flushed boundary marker line is not JSON.
            try
            {
                using var document = JsonDocument.Parse(line);
                events.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Truncated or interleaved evidence is reported through the missing structured event.
            }
        }
        return events;
    }

    private static JsonElement SingleEvent(ChildRun run, string eventName)
    {
        var matches = run.Events.Where(item => EventName(item) == eventName).ToArray();
        Assert.True(matches.Length == 1,
            Failure($"Exactly one '{eventName}' event was expected but {matches.Length} were reported.", run));
        return matches[0];
    }

    private static string EventName(JsonElement item) =>
        item.TryGetProperty("event", out var name) ? name.GetString() ?? string.Empty : string.Empty;

    private static string Failure(string message, ChildRun run) =>
        Failure(message, run.DatabasePath, run.ProcessId, run.Stderr, run.Stdout);

    private static string Failure(string message, string databasePath, int processId, string stderr, string stdout) =>
        $"{message} receiverDatabase={databasePath} processId={processId} " +
        $"stderr={Bounded(stderr, 2048)} stdout={Bounded(stdout, 4096)}";

    private static string Bounded(string value, int maximumCharacters)
    {
        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "\\n{output-truncated}";
    }

    /// <summary>
    /// Keeps the bounded child evidence next to the receiver database so a failing run can still be
    /// inspected after the test process exits.
    /// </summary>
    private static void PersistChildEvidence(string phase, ChildRun run)
    {
        try
        {
            var directory = Path.GetDirectoryName(run.DatabasePath)!;
            var path = Path.Combine(directory, "receiver-probe-" + phase + "-child-" +
                run.ProcessId.ToString(CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, string.Join("\r\n", new[]
            {
                "phase=" + phase,
                "processId=" + run.ProcessId.ToString(CultureInfo.InvariantCulture),
                "exitCode=" + run.ExitCode.ToString(CultureInfo.InvariantCulture),
                "killedAtBoundary=" + run.KilledAtBoundary.ToString(CultureInfo.InvariantCulture),
                "boundaryPhase=" + (run.BoundaryPhase ?? "none"),
                "stdout=" + Bounded(run.Stdout, 8192),
                "stderr=" + Bounded(run.Stderr, 4096)
            }));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Evidence persistence never replaces the receiver evidence the test asserts on.
        }
    }

    private static string ProbePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AlgorithmHangProbe",
            "SharpInspect.AlgorithmHangProbe.dll");
        Assert.True(File.Exists(path), path);
        return path;
    }

    private sealed record AcceptedEvidence(byte[] Receipt, string ReceiptHash);

    private sealed record ChildRun(int ProcessId, int ExitCode, bool KilledAtBoundary, string? BoundaryPhase,
        string DatabasePath, string Stdout, string Stderr, IReadOnlyList<JsonElement> Events);

    private sealed class BoundedText
    {
        private readonly int _maximumCharacters;
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        internal BoundedText(int maximumCharacters) => _maximumCharacters = maximumCharacters;
        internal string Text
        {
            get { lock (_gate) return _builder.ToString(); }
        }
        internal void Append(string value)
        {
            lock (_gate)
            {
                if (_builder.Length >= _maximumCharacters) return;
                var accepted = Math.Min(value.Length, _maximumCharacters - _builder.Length);
                _builder.Append(value, 0, accepted);
                if (accepted != value.Length) _builder.Append("{output-truncated}");
            }
        }
    }
}
