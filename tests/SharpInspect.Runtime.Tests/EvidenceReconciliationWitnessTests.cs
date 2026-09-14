using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class EvidenceReconciliationStorageTests
{
    [Fact, Trait("VerificationId", "V154_S05")]
    public async Task V154_S05_SameTailTamperingBetweenReadProofAndWriterIsRejected()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture);
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var start = Start();
        var first = await store.AppendEvidenceReconciliationAsync(new[] { start }, null, Deadline());
        Assert.True(first.Committed, first.ReasonCode);
        using var proof = await new SqliteEvidenceReconciliationQuery(options).AcquireWriteProofAsync(default);
        (long Sequence, string Hash) before;
        using (var connection = SqliteNative.Open(options.DatabasePath, readOnly: true))
            before = AuditChainDatabase.Tail(connection.Handle!, Deadline());
        store.EvidenceReconciliationAfterReadProof = () =>
        {
            store.EvidenceReconciliationAfterReadProof = null;
            using var connection = SqliteNative.Open(options.DatabasePath, readOnly: false);
            var database = connection.Handle!;
            var triggers = AuditChainDatabase.Read(database,
                "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='evidence_reconciliation_events' AND sql LIKE '%BEFORE UPDATE%';",
                Deadline(), row => (Name: SqliteNative.ColumnText(row, 0)!, Sql: SqliteNative.ColumnText(row, 1)!));
            var trigger = Assert.Single(triggers);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", Deadline());
            SqliteNative.Execute(database, "DROP TRIGGER \"" + trigger.Name + "\";", Deadline());
            SqliteNative.Execute(database, "UPDATE evidence_reconciliation_events SET Payload=replace(Payload,'RunStarted','RunTainted') WHERE Position=1;", Deadline());
            SqliteNative.Execute(database, trigger.Sql, Deadline());
            SqliteNative.Execute(database, "COMMIT;", Deadline());
            Assert.Equal(before, AuditChainDatabase.Tail(database, Deadline()));
        };
        var completed = start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.RunCompleted,
            PreviousCheckpointHash = first.Records![0].ContentHash };
        var result = await store.AppendEvidenceReconciliationAsync(new[] { completed }, null, Deadline());
        Assert.False(result.Committed);
        var witness = Assert.Throws<InvalidOperationException>(() => proof.RequireUnchanged(Deadline()));
        Assert.Equal("EvidenceReconciliationVerifiedSnapshotChanged", witness.Message);
        using (var connection = SqliteNative.Open(options.DatabasePath, readOnly: true))
        {
            Assert.Equal(before, AuditChainDatabase.Tail(connection.Handle!, Deadline()));
            Assert.Equal(1, AuditChainDatabase.Scalar(connection.Handle!, "SELECT COUNT(*) FROM evidence_reconciliation_events;", Deadline()));
        }
    }

    [Fact, Trait("VerificationId", "V154_S06")]
    public async Task V154_S06_ReadWitnessRetainsItsBoundedConnectionSlotUntilDisposed()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture);
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var query = new SqliteEvidenceReconciliationQuery(options);
        using var first = await query.AcquireWriteProofAsync(default);
        using var second = await query.AcquireWriteProofAsync(default);
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.ReadAsync(new(), cancelled.Token).AsTask());
        first.Dispose();
        var result = await query.ReadAsync(new());
        Assert.True(result.Available, result.ReasonCode);
        second.RequireUnchanged(Deadline());
    }

    [Fact, Trait("VerificationId", "V154_L05")]
    public void V154_L05_QuarantineReservesFirstFailureWithoutPretendingCompletion()
    {
        var replay = new EvidenceReconciliationReplay();
        var start = Start(); replay.Apply(Row(start, 1));
        var orphan = new EvidenceReconciliationSubject(EvidenceReconciliationSubjectKind.Orphan,
            null, null, null, null, null, Guid.NewGuid(), null, null, EvidenceReconciliationFileArea.Stage,
            "orphan.stage", Hash, "target.quarantine", Hash, Hash, 12, Hash);
        var intent = start with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.QuarantineIntent, Subject = orphan };
        Assert.Equal(2, replay.QuarantineReserveAfter(intent));
        replay.Apply(Row(intent, 2));
        var failed = intent with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.IntegrityFault };
        Assert.Equal(1, replay.QuarantineReserveAfter(failed));
        replay.Apply(Row(failed, 3));
        Assert.Single(replay.PendingQuarantines);
        Assert.True(replay.IntegrityFault);
        var completion = intent with { EventId = Guid.NewGuid(), Kind = EvidenceReconciliationEventKind.Quarantined };
        Assert.Equal(0, replay.QuarantineReserveAfter(completion));
        replay.Apply(Row(completion, 4));
        Assert.Empty(replay.PendingQuarantines);
        Assert.True(replay.IntegrityFault);
    }
}
