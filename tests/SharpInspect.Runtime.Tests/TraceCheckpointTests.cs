using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceCheckpointTests
{
    [Fact]
    [Trait("VerificationId", "V155_K01")]
    public async Task V155_K01_ApprovedStartupCheckpointIsAuditedAndRestartCannotResetInterval()
    {
        await using var fixture = await CreateFixtureAsync();
        Assert.Empty(Read(fixture.Options));
        var publication = await PublishAsync(fixture, Policy());
        var commandCount = await fixture.ScalarAsync("SELECT COUNT(*) FROM command_facts;");
        var before = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        await fixture.Store.DisposeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var store = new SqliteCommandStore(fixture.Options, Length, new(() => now)))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
            Assert.Equal(TraceCheckpointStatus.Completed, store.Checkpoint.Status);
            Assert.Equal(0, store.Checkpoint.WalBytes);
            var history = Read(fixture.Options);
            Assert.Equal(2, history.Count);
            Assert.Equal(TraceCheckpointStatus.Awaiting, history[0].Record.Status);
            Assert.Equal(TraceCheckpointStatus.Completed, history[1].Record.Status);
            Assert.Equal(publication.Snapshot!.ContentHash, history[1].Record.PolicySnapshotHash);
            Assert.Equal(before + 2, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
            Assert.Equal(commandCount, await fixture.ScalarAsync("SELECT COUNT(*) FROM command_facts;"));
            Assert.True(Length(fixture.Options.DatabasePath + "-wal") > 0); // Durable outcome itself regrows WAL.
            var cold = await new SqliteTraceStoragePolicyQuery(fixture.Options).ReadAsync(null);
            Assert.True(cold.Available, cold.ReasonCode);
            var preflightService = new StoragePolicies.TraceStoragePolicyService(fixture.Options, fixture.Authorization,
                store, new SqliteTraceStoragePolicyQuery(fixture.Options));
            var preflight = await preflightService.GetPreflightAsync();
            Assert.Equal(TraceStoragePreflightStatus.Passed,
                Assert.Single(preflight.Rows, row => row.Gate == TraceStoragePreflightGate.Checkpoint).Status);
        }
        foreach (var clock in new[] { now + TimeSpan.FromSeconds(1), now - TimeSpan.FromHours(1) })
        {
            await using var store = new SqliteCommandStore(fixture.Options, Length, new(() => clock));
            Assert.True((await store.Initialization).Committed);
            Assert.Equal(2, Read(fixture.Options).Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("VerificationId", "V155_K02")]
    public async Task V155_K02_ByteAndFrameBudgetsRefuseNativeWorkAndRemainVisible(bool bytes)
    {
        await using var fixture = await CreateFixtureAsync();
        await PublishAsync(fixture, Policy(bytes ? 64L << 20 : 512L << 20));
        var pageSize = await fixture.ScalarAsync("PRAGMA page_size;");
        var observedBytes = bytes ? 65L << 20 : checked(100000 * (pageSize + 24) + 33);
        await fixture.Store.DisposeAsync();
        var enteredNative = false;
        await using var store = new SqliteCommandStore(fixture.Options, _ => observedBytes,
            new(Phase: phase => enteredNative |= phase == "BeforeNative"));
        Assert.True((await store.Initialization).Committed);
        Assert.False(enteredNative);
        Assert.Equal(TraceCheckpointStatus.BudgetExceeded, store.Checkpoint.Status);
        var rows = Read(fixture.Options);
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[1].Record.CheckpointedFrames);
        var cold = await new SqliteEvidenceRetentionQuery(fixture.Options).ReadAsync(new());
        Assert.True(cold.Available, cold.ReasonCode);
    }

    [Theory]
    [InlineData("Started")]
    [InlineData("BeforeNative")]
    [InlineData("AfterNative")]
    [Trait("VerificationId", "V155_K03")]
    public async Task V155_K03_InterruptedObservationIsUnknownEvenWhenWalWasActuallyTruncated(string phase)
    {
        await using var fixture = await CreateFixtureAsync();
        await PublishAsync(fixture, Policy());
        await fixture.Store.DisposeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var interrupted = new SqliteCommandStore(fixture.Options, Length,
            new(() => now, reached => { if (reached == phase) throw new IOException("ControlledCheckpointInterruption"); })))
        {
            var result = await interrupted.Initialization;
            Assert.Equal(phase != "Started", result.Committed);
        }
        await using var recovered = new SqliteCommandStore(fixture.Options, Length, new(() => now));
        var initialized = await recovered.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(recovered);
        var rows = Read(fixture.Options);
        Assert.Equal(2, rows.Count);
        Assert.Equal(TraceCheckpointStatus.Unknown, rows[1].Record.Status);
        Assert.Equal(TraceCheckpointStatus.Unknown, recovered.Checkpoint.Status);
        Assert.DoesNotContain(rows, row => row.Record.Status == TraceCheckpointStatus.Completed);
    }

    [Fact]
    [Trait("VerificationId", "V155_K04")]
    public async Task V155_K04_PhysicalOwnerRetainsWriterLeaseUntilActualCompletion()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = EvidenceRetentionStorageTests.Options(fixture);
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RegisterStorageRuntimeOwner(async () => { entered.SetResult(true); await released.Task; });
        var stopping = store.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            await using var competing = new SqliteCommandStore(options);
            Assert.False((await competing.Initialization).Committed);
        }
        finally { released.TrySetResult(true); await stopping; }
        await using var replacement = new SqliteCommandStore(options);
        Assert.True((await replacement.Initialization).Committed);
    }

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    [Fact, Trait("VerificationId", "V155_K05")]
    public async Task V155_K05_PinnedReaderProducesBoundedVisibleFailureThenLaterMaintenanceCanRecover()
    {
        await using var fixture = await CreateFixtureAsync();
        await PublishAsync(fixture, Policy());
        var reader = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true);
        try
        {
            SqliteNative.Execute(reader.Handle!, "BEGIN;", new StoreDeadline(TimeSpan.FromSeconds(5)));
            _ = AuditChainDatabase.Scalar(reader.Handle!, "SELECT COUNT(*) FROM audit_entries;", new StoreDeadline(TimeSpan.FromSeconds(5)));
            await fixture.Store.DisposeAsync();
            await using var blocked = new SqliteCommandStore(fixture.Options, Length);
            var opened = await blocked.Initialization.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(opened.Committed, opened.ReasonCode);
            Assert.Contains(blocked.Checkpoint.Status, new[] { TraceCheckpointStatus.Busy, TraceCheckpointStatus.Unknown });
            Assert.Equal(2, Read(fixture.Options).Count);
        }
        finally { reader.Dispose(); }
        await using var recovered = new SqliteCommandStore(fixture.Options, Length,
            new(() => DateTimeOffset.UtcNow.AddMinutes(2)));
        Assert.True((await recovered.Initialization).Committed);
        Assert.Equal(TraceCheckpointStatus.Completed, recovered.Checkpoint.Status);
        Assert.Equal(4, Read(fixture.Options).Count);
    }

    [Fact, Trait("VerificationId", "V155_K06")]
    public async Task V155_K06_LogicallyTimedOutStartupKeepsItsNativeOwnerAndStoreLock()
    {
        await using var fixture = await CreateFixtureAsync();
        await PublishAsync(fixture, Policy());
        await fixture.Store.DisposeAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var blocked = new SqliteCommandStore(fixture.Options, Length, new(Phase: phase =>
        {
            if (phase == "BeforeNative") { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<TimeoutException>(() => blocked.Initialization.WaitAsync(TimeSpan.FromMilliseconds(30)));
            await using var competing = new SqliteCommandStore(fixture.Options);
            Assert.False((await competing.Initialization).Committed);
            Assert.False(blocked.Initialization.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await blocked.Initialization).Committed);
        Assert.Equal(TraceCheckpointStatus.Completed, blocked.Checkpoint.Status);
    }

    private static IReadOnlyList<TraceCheckpointRow> Read(ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(10));
        return AuditChainDatabase.ReadTraceCheckpoints(connection.Handle!,
            SqliteCommandStore.ReadRetentionConfiguration(connection.Handle!, deadline), deadline);
    }

    private static async Task<TraceStoragePolicyResult> PublishAsync(RecipeDraftStorageTests.Fixture fixture,
        TraceStoragePolicyDefinition policy)
    {
        var result = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
            await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 0, policy));
        Assert.True(result.Succeeded, result.Outcome.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        return result;
    }

    internal static TraceStoragePolicyDefinition Policy(long bytes = 64 * 1024 * 1024, int frames = 100000)
    {
        var p = TraceStoragePolicyRuntimeTests.Policy();
        return new(p.PolicyId, p.Version, p.ApprovalReference, p.ApprovalVersion, p.Rationale, p.RetentionRules,
            p.MinimumReserveBytes, p.MinimumReservePercent, p.RequiredRoutes, p.ImageBacklog, p.EvidenceStageTimeout,
            p.TraceCommitTimeout, p.Scrubber, new(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2), bytes, frames), p.MaximumWalBytes);
    }

    internal static Task<RecipeDraftStorageTests.Fixture> CreateFixtureAsync(TimeSpan? observerTimeout = null,
        Func<string, long>? readWalLength = null, TraceStorageRecoveryBudget? recoveryBudget = null)
    {
        using var template = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = EvidenceRetentionStorageTests.Options(template);
        var retention = new TraceStorageRetentionOptions(options.StorageRetention!.ExecutionPolicy,
            TimeSpan.FromMilliseconds(50), observerTimeout ?? options.StorageRetention.FileTimeout,
            TimeSpan.FromSeconds(10), recoveryBudget ?? options.StorageRetention.RecoveryBudget);
        var roles = RecipeDraftTestPolicies.Authoring.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key == HumanRoleBundle.Administrator ? pair.Value.Append(Permission.ManageProductionPolicy).Distinct() : pair.Value);
        return RecipeDraftStorageTests.Fixture.CreateAsync(authorizationPolicy:
            new AuthorizationPolicy("checkpoint-tests", "1", roles, RecipeDraftTestPolicies.Authoring.StepUpPermissions),
            traceStoragePolicies: options.TraceStoragePolicies, productionInspections: options.ProductionInspections,
            outbox: options.Outbox, evidenceReconciliation: options.EvidenceReconciliation, storageRetention: retention,
            readWalLength: readWalLength);
    }
}
