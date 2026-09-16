using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class EvidenceRetentionStorageTests
{
    private static readonly DateTimeOffset Time = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('A', 64);
    internal static EvidenceRetentionPayload Establish(EvidenceRetentionObligation obligation,
        string policyHash, DateTimeOffset at) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EvidenceRetentionEventKind.ObligationEstablished,
            obligation.Owner, 1, null, obligation.ContentHash, at, "RetentionObligationEstablished",
            null, SystemPrincipalId.RetentionCleanup, null, null, null, null, null,
            null, null, null, obligation, policyHash);

    private static EvidenceRetentionObligation Obligation()
    {
        var value = new EvidenceRetentionObligation(new(EvidenceRetentionOwnerKind.QuarantinedFile, Guid.NewGuid()),
            TraceRetentionClass.QuarantineEvidence, null, Hash, 1, Hash, Hash, Hash, Hash,
            RetentionStartEvent.Quarantined, Time, Time.AddHours(1), Hash, "orphan.quarantine", 12, string.Empty);
        return value with { ContentHash = EvidenceRetentionCodec.ObligationHash(value) };
    }
    private static EvidenceRetentionStoredRow Append(EvidenceRetentionReplay replay, EvidenceRetentionPayload value)
    {
        var p = replay.LastPosition + 1;
        var row = new EvidenceRetentionStoredRow(p, value, p.ToString("X64"), p + 10, (p + 100).ToString("X64"));
        replay.Apply(row);
        return row;
    }
    private static EvidenceRetentionPayload Next(EvidenceRetentionStoredRow previous,
        EvidenceRetentionEventKind kind, DateTimeOffset? at = null)
    {
        var governance = kind is EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased or
            EvidenceRetentionEventKind.Extended;
        return previous.Payload with
        {
            EventId = Guid.NewGuid(), OperationId = Guid.NewGuid(), Kind = kind,
            AggregateSequence = previous.Payload.AggregateSequence + 1, PreviousContentHash = previous.ContentHash,
            RecordedAtUtc = at ?? Time.AddHours(2), ReasonCode = "RetentionFixtureEvent",
            EstablishedObligation = null, File = kind is EvidenceRetentionEventKind.DeleteIntent
                ? new(Hash, "orphan.quarantine", Hash, 12, Hash) : null,
            Reason = governance ? "Controlled governance test" : null,
            Authority = governance ? new(Guid.NewGuid(), 2, Hash, Guid.NewGuid(), 3, Hash, "fixture", "1", Hash) : null,
            HumanPrincipalId = governance ? Guid.NewGuid() : null, SessionId = governance ? Guid.NewGuid() : null,
            StepUpGrantId = governance ? Guid.NewGuid() : null,
            AuthorizationTarget = governance ? Hash : null, AuthorizationRevision = governance ? 1L : null,
            HoldId = kind is EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased
                ? Guid.NewGuid() : null, ExtendedUntilUtc = null
        };
    }

    [Fact, Trait("VerificationId", "V155_L01")]
    public void V155_L01_HoldAndDeleteIntentHaveOneExplicitOrder()
    {
        var replay = new EvidenceRetentionReplay();
        var established = Append(replay, Establish(Obligation(), Hash, Time));
        var held = Append(replay, Next(established, EvidenceRetentionEventKind.HoldPlaced));
        Assert.Equal(EvidenceRetentionDisposition.Held, replay.Subjects[established.Payload.Owner].Disposition);
        Assert.Throws<InvalidOperationException>(() => Append(replay, Next(held, EvidenceRetentionEventKind.DeleteIntent)));
        var released = Append(replay, Next(held, EvidenceRetentionEventKind.HoldReleased) with { HoldId = held.Payload.HoldId });
        var intent = Append(replay, Next(released, EvidenceRetentionEventKind.DeleteIntent));
        Assert.Equal(2, replay.FutureReserve);
        Assert.Throws<InvalidOperationException>(() => Append(replay, Next(intent, EvidenceRetentionEventKind.HoldPlaced)));
    }

    [Fact, Trait("VerificationId", "V155_L02")]
    public void V155_L02_UnknownDeletionStaysPendingAndOnlyExactCompletionConsumesReserve()
    {
        var replay = new EvidenceRetentionReplay();
        var established = Append(replay, Establish(Obligation(), Hash, Time));
        var intent = Append(replay, Next(established, EvidenceRetentionEventKind.DeleteIntent));
        var unknown = Append(replay, Next(intent, EvidenceRetentionEventKind.DeleteOutcomeUnknown) with
            { OperationId = intent.Payload.OperationId, File = intent.Payload.File });
        Assert.Equal(1, replay.FutureReserve);
        Assert.Equal(EvidenceRetentionDisposition.DeleteUnknown, replay.Subjects[intent.Payload.Owner].Disposition);
        Assert.Null(replay.Subjects[intent.Payload.Owner].Tombstone);
        Assert.Throws<InvalidOperationException>(() => Append(replay, Next(unknown, EvidenceRetentionEventKind.Tombstone) with
            { OperationId = intent.Payload.OperationId, File = intent.Payload.File! with { RawContentHash = new('B', 64) } }));
        var done = Append(replay, Next(unknown, EvidenceRetentionEventKind.Tombstone, Time.AddMinutes(10)) with
            { OperationId = intent.Payload.OperationId, File = intent.Payload.File });
        Assert.Equal(0, replay.FutureReserve);
        Assert.Equal(done.ContentHash, replay.Subjects[intent.Payload.Owner].Tombstone!.ContentHash);
    }

    [Fact, Trait("VerificationId", "V155_L03")]
    public void V155_L03_GlobalClockRollbackBlocksNewDeletionButAllowsOwedOutcome()
    {
        var replay = new EvidenceRetentionReplay();
        var old = Append(replay, Establish(Obligation(), Hash, Time));
        Append(replay, Establish(Obligation(), Hash, Time.AddHours(3)));
        Assert.Throws<InvalidOperationException>(() => Append(replay,
            Next(old, EvidenceRetentionEventKind.DeleteIntent, Time.AddHours(2))));
        var intent = Append(replay, Next(old, EvidenceRetentionEventKind.DeleteIntent, Time.AddHours(3)));
        var failed = Append(replay, Next(intent, EvidenceRetentionEventKind.DeleteFailed, Time.AddHours(1)) with
            { OperationId = intent.Payload.OperationId, File = intent.Payload.File });
        Assert.Equal(Time.AddHours(3), replay.LastRecordedAtUtc);
        Assert.Null(replay.Subjects[failed.Payload.Owner].DeleteIntent);
        var hold = Append(replay, Next(failed, EvidenceRetentionEventKind.HoldPlaced, Time.AddHours(1)));
        Assert.Contains(hold.Payload.HoldId!.Value, replay.Subjects[hold.Payload.Owner].ActiveHolds.Keys);
    }

    [Fact, Trait("VerificationId", "V155_L04")]
    public void V155_L04_ExpiryAndStrictExtensionNeverShortenFrozenObligation()
    {
        var replay = new EvidenceRetentionReplay();
        var established = Append(replay, Establish(Obligation(), Hash, Time));
        Assert.Throws<InvalidOperationException>(() => Append(replay,
            Next(established, EvidenceRetentionEventKind.DeleteIntent, Time.AddMinutes(59))));
        Assert.Throws<InvalidOperationException>(() => Append(replay,
            Next(established, EvidenceRetentionEventKind.Extended) with { ExtendedUntilUtc = Time.AddMinutes(59) }));
        var extended = Append(replay, Next(established, EvidenceRetentionEventKind.Extended) with
            { ExtendedUntilUtc = Time.AddDays(1) });
        Assert.Equal(Time.AddDays(1), replay.Subjects[extended.Payload.Owner].EffectiveUntilUtc);
        Assert.Equal(Time.AddHours(1), replay.Subjects[extended.Payload.Owner].Obligation.RetainUntilUtc);
        Assert.Throws<InvalidOperationException>(() => Append(replay, Next(extended, EvidenceRetentionEventKind.DeleteIntent)));
    }

    [Fact, Trait("VerificationId", "V155_S03")]
    public async Task V155_S03_ConcurrentSignedAuditPreservesReadSnapshotAndInvalidatesWriterProof()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture);
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var query = new SqliteEvidenceRetentionQuery(options);
        var written = 0;
        query.AfterSnapshotRead = () =>
        {
            var start = new EvidenceReconciliationPayload(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                EvidenceReconciliationPhase.Startup, EvidenceReconciliationEventKind.RunStarted,
                null, Time, "RunStarted", null, null, 0, 0, 0, 0, 0, 0);
            var result = store.AppendEvidenceReconciliationAsync(new[] { start }, null, Deadline()).AsTask().GetAwaiter().GetResult();
            Assert.True(result.Committed, result.ReasonCode);
            written++;
        };
        var snapshot = await query.ReadAsync(new());
        Assert.True(snapshot.Available, snapshot.ReasonCode);
        Assert.Empty(snapshot.Records);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => query.AcquireWriteProofAsync(default).AsTask());
        Assert.Equal("RetentionVerifiedSnapshotChanged", failure.Message);
        Assert.Equal(2, written);
    }

    [Fact, Trait("VerificationId", "V155_S04")]
    public async Task V155_S04_FabricatedOwnerAndSystemGovernanceCannotAcquireRetentionAuthority()
    {
        using var fixture = new ProductionOutboxStorageTests.OutboxStoreFixture();
        var options = Options(fixture);
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var fact = Establish(Obligation(), options.StorageRetention!.ExecutionPolicy.ContentHash, Time);
        var rejected = await store.AppendRetentionAsync(fact, null, Deadline());
        Assert.False(rejected.Committed);
        Assert.Equal("RetentionSourceObligationMismatch", rejected.ReasonCode);
        var fakeRow = new EvidenceRetentionStoredRow(1, fact, Hash, 11, Hash);
        var governance = await store.AppendRetentionAsync(Next(fakeRow, EvidenceRetentionEventKind.HoldPlaced), null, Deadline());
        Assert.False(governance.Committed);
        Assert.Equal("RetentionGovernanceAuthorityRequired", governance.ReasonCode);
        var snapshot = await new SqliteEvidenceRetentionQuery(options).ReadAsync(new());
        Assert.True(snapshot.Available, snapshot.ReasonCode);
        Assert.Empty(snapshot.Records);
    }

    [Fact, Trait("VerificationId", "V155_L05")]
    public void V155_L05_NonHoldFactCannotCarryAnEmptyHoldIdOrAnUnrecognizedField()
    {
        var fact = Establish(Obligation(), Hash, Time);
        Assert.Throws<InvalidOperationException>(() => EvidenceRetentionCodec.Encode(fact with { HoldId = Guid.Empty }, 16384));
        var encoded = EvidenceRetentionCodec.Encode(fact, 16384);
        var json = Encoding.UTF8.GetString(encoded).Insert(1, "\"UnrecognizedAuthority\":true,");
        Assert.Throws<InvalidOperationException>(() => EvidenceRetentionCodec.Decode(Encoding.UTF8.GetBytes(json), 16384));
    }
}
