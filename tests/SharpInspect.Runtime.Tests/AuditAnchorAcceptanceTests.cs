using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

#pragma warning disable CA1416
public sealed class AuditAnchorAcceptanceTests
{
    [Fact]
    public async Task V103_A10_IndependentAnchorClockSkewDoesNotInvalidateBoundCheckpoint()
    {
        using var fixture = new Fixture();
        fixture.Anchor.ClockOffset = TimeSpan.FromHours(-2);
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        var receipt = Assert.Single(fixture.Anchor.Receipts.Values);
        Assert.True(receipt.AcceptedAtUtc < DateTimeOffset.UtcNow.AddHours(-1));
        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verified, report.State);
    }

    [Theory]
    [InlineData("audit_anchor_receipts", "null", "AuditAnchorReceiptInvalid")]
    [InlineData("audit_anchor_receipts", "invalid-json", "AuditAnchorReceiptInvalid")]
    [InlineData("audit_checkpoints", "invalid-json", "AuditCheckpointInvalid")]
    public async Task V103_A09_MalformedPersistedDocumentsLatchAndNeverRepairThemselves(string table, string malformed, string reason)
    {
        using var fixture = new Fixture();
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        string original;
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, true))
        {
            using var command = read.CreateCommand(); command.CommandText = "SELECT Document FROM " + table + " LIMIT 1;";
            original = (string)command.ExecuteScalar()!;
        }
        fixture.Execute("DROP TRIGGER " + table + "_immutable_update;");
        void SetDocument(string value)
        {
            using var write = SqliteNative.Open(fixture.Options.DatabasePath, false);
            using var command = write.CreateCommand();
            command.CommandText = "UPDATE " + table + " SET Document=$value;";
            command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
        }
        SetDocument(malformed);
        await Wait(() => store.Integrity?.ReasonCode == reason);
        SetDocument(original);
        await Task.Delay(1200);
        Assert.Equal(AuditIntegrityState.Faulted, store.Integrity?.State);
        Assert.Equal(reason, store.Integrity?.ReasonCode);
    }

    [Fact]
    public async Task V103_A07_MismatchedDeliveryReceiptLatchesIntegrityFault()
    {
        using var fixture = new Fixture(checkpointEvery: 1);
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        fixture.Anchor.BadDeliveryReceipt = true;
        Assert.True((await store.AppendAsync(Fact(), new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
        await Wait(() => store.Integrity?.ReasonCode == "AuditAnchorReceiptMismatch");
        fixture.Anchor.BadDeliveryReceipt = false;
        await Task.Delay(1200);
        Assert.Equal(AuditIntegrityState.Faulted, store.Integrity?.State);
        Assert.False((await store.AppendAsync(Fact(), new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
    }

    [Fact]
    public async Task V103_A08_PublicQueryAndMonitorTreatSharedAdapterBusyAsRetryable()
    {
        using var fixture = new Fixture();
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = AuditAnchorClient.InvokeAsync(fixture.Anchor, _ => { entered.SetResult(); return new ValueTask<int>(release.Task); },
            TimeSpan.FromSeconds(5), default);
        await entered.Task;
        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verifying, report.State);
        Assert.Equal("AuditAnchorBusy", report.ReasonCode);
        await Task.Delay(1200);
        release.SetResult(1);
        await holder;
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
    }

    [Fact]
    public async Task V103_A06_PersistedCheckpointIsRecheckedWithoutRedelivery()
    {
        using var fixture = new Fixture();
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        fixture.Anchor.Fail = true; // A second delivery would now fail; read verification remains available.
        var reads = fixture.Anchor.Reads;
        await Wait(() => fixture.Anchor.Reads >= reads + 2);
        Assert.Single(fixture.Anchor.Deliveries);
        Assert.Equal(AuditIntegrityState.Verified, store.Integrity?.State);
    }

    [Fact]
    public async Task V103_A05_CancellationIgnoringAdapterRemainsOneBoundedInFlightCall()
    {
        var anchor = new Anchor();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ValueTask<int> NeverUntilReleased(CancellationToken _) { Interlocked.Increment(ref calls); return new(release.Task); }
        await Assert.ThrowsAsync<TimeoutException>(() => AuditAnchorClient.InvokeAsync(anchor, NeverUntilReleased,
            TimeSpan.FromMilliseconds(50), default));
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => AuditAnchorClient.InvokeAsync(anchor,
            NeverUntilReleased, TimeSpan.FromMilliseconds(50), default));
        Assert.Equal("AuditAnchorBusy", busy.Message);
        Assert.Equal(1, calls);
        release.SetResult(42);
        await Task.Delay(30);
        Assert.Equal(42, await AuditAnchorClient.InvokeAsync(anchor, NeverUntilReleased, TimeSpan.FromSeconds(1), default));
    }

    [Fact]
    public async Task V103_A01_RequiredAnchorHasDurableIdempotentReceiptAndRejectsRemoteMismatch()
    {
        using var fixture = new Fixture();
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        Assert.Single(fixture.Anchor.Receipts);
        Assert.Equal(1, fixture.Count("audit_anchor_receipts"));
        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(new());
        Assert.Equal(1, report.AnchoredSequence);
        fixture.Anchor.Mismatch = true;
        var mismatch = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Faulted, mismatch.State);
        Assert.Equal("AuditExternalAnchorMismatch", mismatch.ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Faulted);
        var rejected = await store.AppendAsync(Fact(), new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.False(rejected.Committed);
        Assert.Equal(0, fixture.Count("command_facts"));
    }

    [Fact]
    public async Task V103_A02_RemoteSuccessLocalReceiptFailureRetriesSameCheckpointAcrossRestart()
    {
        using var fixture = new Fixture();
        fixture.Anchor.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid checkpoint;
        await using (var store = new SqliteCommandStore(fixture.Options))
        {
            Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
            await Wait(() => fixture.Anchor.Deliveries.Count > 0);
            checkpoint = fixture.Anchor.Deliveries.First();
            fixture.Execute("CREATE TRIGGER fail_receipt BEFORE INSERT ON audit_anchor_receipts BEGIN SELECT RAISE(ABORT,'injected receipt failure'); END;");
            fixture.Anchor.Pause.SetResult();
            await Wait(() => store.Integrity?.ReasonCode == "AuditReceiptCommitFailed");
            Assert.Single(fixture.Anchor.Receipts);
            Assert.Equal(0, fixture.Count("audit_anchor_receipts"));
        }
        fixture.Execute("DROP TRIGGER fail_receipt;");
        await using (var reopened = new SqliteCommandStore(fixture.Options))
        {
            Assert.True((await reopened.Initialization).Committed, (await reopened.Initialization).ReasonCode);
            await Wait(() => reopened.Integrity?.State == AuditIntegrityState.Verified);
            Assert.Equal(1, fixture.Count("audit_anchor_receipts"));
            Assert.All(fixture.Anchor.Deliveries, id => Assert.Equal(checkpoint, id));
        }
    }

    [Fact]
    public async Task V103_A03_AnchorFailureAfterCommitCannotRewriteAcceptedResult()
    {
        using var fixture = new Fixture(checkpointEvery: 1);
        await using var store = new SqliteCommandStore(fixture.Options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Verified);
        fixture.Anchor.Fail = true;
        var fact = Fact();
        var accepted = await store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.True(accepted.Committed);
        Assert.Equal(CommandDisposition.Accepted, accepted.Fact!.Disposition);
        await Wait(() => store.Integrity?.State == AuditIntegrityState.Faulted);
        var rows = (await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(new())).Records;
        Assert.Equal(CommandDisposition.Accepted, Assert.Single(rows).Disposition);
        Assert.False((await store.AppendAsync(Fact(), new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
    }

    [Fact]
    public async Task V103_A04_RequiredUnconfiguredRouteNeverVerifiesOrAdmits()
    {
        using var fixture = new Fixture();
        var options = new ProductionStoreOptions(fixture.Options.DatabasePath) { AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy };
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed, (await store.Initialization).ReasonCode);
        await Wait(() => store.Integrity?.ReasonCode == "AuditRequiredAnchorUnavailable");
        Assert.False((await store.AppendAsync(Fact(), new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);
        Assert.Equal(0, fixture.Count("command_facts"));
    }

    private static CommandAuditFact Fact() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        DateTimeOffset.UtcNow, AuditedCommandKind.GracefulProductionStop, CommandSource.PhysicalConsole,
        null, null, null, CommandAuditPhase.Outcome, CommandDisposition.Accepted, "StopAdmitted");

    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _keyName = "SharpInspect.V103.AnchorTests." + Guid.NewGuid().ToString("N");
        internal Anchor Anchor { get; } = new();
        internal ProductionStoreOptions Options { get; }
        internal Fixture(int checkpointEvery = 2)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SharpInspect.Tests", "V103-anchor", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            Options = new ProductionStoreOptions(Path.Combine(folder, "station.sqlite"))
            {
                AuditIntegrityPolicy = new AuditIntegrityPolicy("AnchorTestStation", "v1", _keyName)
                {
                    AllowInitialKeyCreation = true, CheckpointEveryEntries = checkpointEvery,
                    RequireExternalAnchor = true, ExternalAnchorRouteId = "isolated-test-route",
                    VerificationInterval = TimeSpan.FromSeconds(1), AnchorTimeout = TimeSpan.FromSeconds(2)
                }, ExternalAuditAnchor = Anchor
            };
        }
        internal long Count(string table)
        {
            using var connection = SqliteNative.Open(Options.DatabasePath, true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + table;
            return (long)command.ExecuteScalar()!;
        }
        internal void Execute(string sql)
        {
            using var connection = SqliteNative.Open(Options.DatabasePath, false);
            using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            var path = WindowsMachineAuditKey.GetKeyPath(Options.AuditIntegrityPolicy!);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class Anchor : IExternalAuditAnchor
    {
        internal readonly ConcurrentDictionary<Guid, AuditAnchorReceipt> Receipts = new();
        internal readonly ConcurrentQueue<Guid> Deliveries = new();
        internal volatile bool Mismatch;
        internal volatile bool Fail;
        internal volatile bool BadDeliveryReceipt;
        internal TimeSpan ClockOffset;
        internal int Reads;
        internal TaskCompletionSource? Pause;
        public async ValueTask<AuditAnchorReceipt> DeliverAsync(AuditCheckpoint cp, string idempotencyKey, CancellationToken token)
        {
            Assert.Equal(cp.StationId + "/isolated-test-route/" + cp.CheckpointId.ToString("D"), idempotencyKey);
            Deliveries.Enqueue(cp.CheckpointId);
            var receipt = Receipts.GetOrAdd(cp.CheckpointId, _ => new(cp.CheckpointId, cp.StationId, cp.Sequence, cp.HeadHash,
                "isolated-test-route", "receipt-" + cp.CheckpointId, cp.PolicyHash, cp.SigningKeyId,
                AuditChainDatabase.CheckpointDigest(cp), DateTimeOffset.UtcNow + ClockOffset));
            if (Pause is not null) await Pause.Task.WaitAsync(token);
            if (Fail) throw new IOException("isolated route unavailable");
            return BadDeliveryReceipt ? receipt with { HeadHash = new string('0', 64) } : receipt;
        }
        public ValueTask<AuditAnchorReceipt?> ReadLatestAsync(string stationId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            var receipt = Receipts.Values.OrderByDescending(r => r.Sequence).FirstOrDefault();
            return ValueTask.FromResult(receipt is not null && Mismatch ? receipt with { HeadHash = new string('0', 64) } : receipt);
        }
    }
}
#pragma warning restore CA1416
