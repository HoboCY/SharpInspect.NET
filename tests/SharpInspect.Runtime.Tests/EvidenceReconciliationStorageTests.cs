using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class EvidenceReconciliationStorageTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('A', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V154_S01")]
    public async Task V154_S01_ColdSchema38PreservesOptionalRecoveryAndSignedStartupFacts(bool recovery)
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture, recovery);
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            var started = Start();
            var first = await store.AppendEvidenceReconciliationAsync(new[] { started }, null, Deadline());
            Assert.True(first.Committed, first.ReasonCode);
            var finish = started with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.RunCompleted,
                ReasonCode = "StartupVerified", PreviousCheckpointHash = first.Records![0].ContentHash };
            var completed = await store.AppendEvidenceReconciliationAsync(new[] { finish }, null, Deadline());
            Assert.True(completed.Committed, completed.ReasonCode);
        }
        await using (var reopened = new SqliteCommandStore(options))
        {
            var initialized = await reopened.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            var snapshot = await new SqliteEvidenceReconciliationQuery(options).ReadAsync(new(PageSize: 1));
            Assert.True(snapshot.Available, snapshot.ReasonCode);
            Assert.Single(snapshot.Records);
            Assert.True(snapshot.HasMore);
            Assert.True(snapshot.LatestStartup!.Completed);
            Assert.False(snapshot.IntegrityFaultRecorded);
            var next = await new SqliteEvidenceReconciliationQuery(options).ReadAsync(new(snapshot.NextPosition, 1));
            Assert.True(next.Available, next.ReasonCode);
            Assert.False(next.HasMore);
            Assert.Equal(EvidenceReconciliationEventKind.RunCompleted, Assert.Single(next.Records).Kind);
            using var connection = SqliteNative.Open(fixture.DatabasePath, readOnly: true);
            Assert.Equal(38, AuditChainDatabase.Scalar(connection.Handle!, "PRAGMA user_version;", Deadline()));
            Assert.Equal(recovery ? 1 : 0, AuditChainDatabase.Scalar(connection.Handle!,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='production_outbox_recovery_config';", Deadline()));
            Assert.Equal(0, AuditChainDatabase.Scalar(connection.Handle!,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='image_evidence_store_config';", Deadline()));
        }
        await using var oldWriter = new SqliteCommandStore(fixture.Options());
        Assert.False((await oldWriter.Initialization).Committed);
    }

    [Fact]
    [Trait("VerificationId", "V154_S02")]
    public async Task V154_S02_InvalidSecondFactRollsBackWholeBatchAndAuditTail()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture);
        await using var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        var query = new SqliteEvidenceReconciliationQuery(options);
        var before = await query.ReadAsync(new());
        Assert.True(before.Available, before.ReasonCode);
        var started = Start();
        var impossible = started with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.RunCompleted,
            PreviousCheckpointHash = Hash };
        var rejected = await store.AppendEvidenceReconciliationAsync(new[] { started, impossible }, null, Deadline());
        Assert.False(rejected.Committed);
        Assert.Equal("EvidenceReconciliationCheckpointMismatch", rejected.ReasonCode);
        var after = await query.ReadAsync(new());
        Assert.True(after.Available, after.ReasonCode);
        Assert.Empty(after.Records);
        Assert.Equal(before.ThroughAuditSequence, after.ThroughAuditSequence);
    }

    [Fact]
    [Trait("VerificationId", "V154_S03")]
    public async Task V154_S03_ChangedBudgetCannotReadOrOpenAnExistingSchema38Store()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        await using (var store = new SqliteCommandStore(Options(fixture)))
            Assert.True((await store.Initialization).Committed);
        var changed = Options(fixture, maximumEvents: 99);
        var snapshot = await new SqliteEvidenceReconciliationQuery(changed).ReadAsync(new());
        Assert.False(snapshot.Available);
        Assert.Equal("EvidenceReconciliationConfigurationMismatch", snapshot.ReasonCode);
        await using var rejected = new SqliteCommandStore(changed);
        Assert.False((await rejected.Initialization).Committed);
    }

    [Fact]
    [Trait("VerificationId", "V154_L01")]
    public void V154_L01_DeferredItemsRemainSeparateAndOnlyCheckpointAdvancesCursor()
    {
        var start = Start(EvidenceReconciliationPhase.HistoricalScrub) with { ThroughSourcePosition = 10 };
        var replay = new EvidenceReconciliationReplay();
        var first = Row(start, 1); replay.Apply(first);
        var deferred = start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.WorkDeferred,
            Subject = OutboxSubject(4), ReasonCode = "ProductionBusy" };
        replay.Apply(Row(deferred, 2));
        var run = Assert.Single(replay.Runs);
        Assert.Equal(0, run.AfterSourcePosition);
        Assert.Equal(0, run.ScannedItems);
        var checkpoint = start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.PageCompleted,
            PreviousCheckpointHash = first.ContentHash, AfterSourcePosition = 4, ScannedItems = 1, DeferredItems = 1 };
        replay.Apply(Row(checkpoint, 3));
        Assert.Equal(4, run.AfterSourcePosition);
        Assert.Equal(1, run.ScannedItems);
        Assert.Equal(0, run.VerifiedItems);
        Assert.Equal(1, run.DeferredItems);
        var stale = checkpoint with { EventId = Guid.NewGuid(), AfterSourcePosition = 6 };
        Assert.Throws<InvalidOperationException>(() => replay.Apply(Row(stale, 4)));
    }

    [Fact]
    [Trait("VerificationId", "V154_L02")]
    public void V154_L02_StartupEpochCannotBorrowPreviousStartupCompletion()
    {
        var replay = new EvidenceReconciliationReplay();
        var start = Start(); replay.Apply(Row(start, 1));
        var wrongEpoch = start with { EventId = Guid.NewGuid(), RuntimeEpoch = Guid.NewGuid(),
            Kind = EvidenceReconciliationEventKind.RunCompleted, PreviousCheckpointHash = Row(start, 1).ContentHash };
        Assert.Throws<InvalidOperationException>(() => replay.Apply(Row(wrongEpoch, 2)));
    }

    [Fact]
    [Trait("VerificationId", "V154_L03")]
    public void V154_L03_OrphanCannotAcquireProductionIdentityAndUnknownJsonIsRejected()
    {
        var orphan = new EvidenceReconciliationSubject(EvidenceReconciliationSubjectKind.Orphan, null,
            Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null,
            EvidenceReconciliationFileArea.Stage, "lost.stage", Hash, null, null, null, null, null);
        var value = Start() with { Kind = EvidenceReconciliationEventKind.IntegrityFault, Subject = orphan };
        Assert.Throws<InvalidOperationException>(() => EvidenceReconciliationStorageCodec.Encode(value, 4096));
        var valid = EvidenceReconciliationStorageCodec.Encode(Start(), 4096);
        var injected = Encoding.UTF8.GetString(valid).Insert(1, "\"InventedReference\":1,");
        Assert.Throws<InvalidOperationException>(() => EvidenceReconciliationStorageCodec.Decode(Encoding.UTF8.GetBytes(injected), 4096));
    }

    [Fact]
    [Trait("VerificationId", "V154_L04")]
    public void V154_L04_HealthyLaterObservationDoesNotEraseRecordedIntegrityFault()
    {
        var replay = new EvidenceReconciliationReplay();
        var start = Start(); var first = Row(start, 1); replay.Apply(first);
        var fault = start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.IntegrityFault,
            Subject = OutboxSubject(1), ReasonCode = "HistoricalEvidenceMissing" };
        replay.Apply(Row(fault, 2));
        replay.Apply(Row(start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.PageCompleted,
            PreviousCheckpointHash = first.ContentHash, ScannedItems = 1 }, 3));
        replay.Apply(Row(start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.OutboxVerified,
            Subject = OutboxSubject(1), ReasonCode = "LocalEvidenceVerified" }, 4));
        Assert.True(replay.IntegrityFault);
        Assert.True(Assert.Single(replay.Runs).IntegrityFault);
    }

    internal static ProductionStoreOptions Options(ProductionOutboxStorageTests.OutboxStoreFixture fixture,
        bool recovery = false, int maximumEvents = 100) => new(fixture.DatabasePath)
    {
        AuditIntegrityPolicy = fixture.Policy, LocalIdentity = fixture.Identity,
        ProductionInspections = fixture.Inspections, TraceStoragePolicies = fixture.TracePolicies,
        Outbox = new ProductionOutboxStoreOptions(fixture.Outbox().Routes)
            { ManualRecovery = recovery ? new ProductionOutboxRecoveryOptions() : null },
        EvidenceReconciliation = new(new TraceStorageMaintenanceBudget(TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5), 1024 * 1024, 16)) { MaximumEvents = maximumEvents },
        CommitTimeout = TimeSpan.FromSeconds(10), QueryTimeout = TimeSpan.FromSeconds(10)
    };

    private static EvidenceReconciliationPayload Start(EvidenceReconciliationPhase phase = EvidenceReconciliationPhase.Startup) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), phase, EvidenceReconciliationEventKind.RunStarted,
            phase == EvidenceReconciliationPhase.Startup ? null : EvidenceReconciliationStream.Outbox,
            Time, "RunStarted", null, null, 0, 0, 0, 0, 0, 0);
    private static EvidenceReconciliationStoredRow Row(EvidenceReconciliationPayload fact, int position) =>
        new(position, fact, position.ToString("X64"), position + 10, (position + 100).ToString("X64"));
    private static EvidenceReconciliationSubject OutboxSubject(long position) => new(EvidenceReconciliationSubjectKind.Outbox,
        position, Guid.NewGuid(), null, null, Guid.NewGuid(), null, Hash, Hash, null, null, null, null, null, null, null, null);
    private static StoreDeadline Deadline() => new(TimeSpan.FromSeconds(10));
}
