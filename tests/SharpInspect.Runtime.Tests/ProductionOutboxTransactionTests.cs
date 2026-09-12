using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V152_T01")]
    public async Task V152_T01_CoreFinalFenceRollsBackAllOutboxRowsAndRetriesTheSameFrozenBatch()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        var before = fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;");
        var refused = await fixture.Store.CommitProductionInspectionCoreAsync(
            new(fixture.Core, () => "ProductionInspectionCommitFenceBusy", null, fixture.Batch), fixture.Deadline());
        Assert.False(refused.Committed);
        Assert.Equal("ProductionInspectionCommitFenceBusy", refused.ReasonCode);
        Assert.Equal(before, fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM production_inspection_cores;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_deliveries;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_events;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_work;"));
        await fixture.CommitAsync();
        var delivery = Assert.Single((await fixture.Query.ReadPendingAsync()).Items).Delivery;
        Assert.Equal(fixture.Batch.Deliveries[0].DeliveryId, delivery.DeliveryId);
        Assert.Equal(fixture.Batch.Deliveries[0].Payload!.CopyBytes(), delivery.Payload!.CopyBytes());
        Assert.Equal(fixture.Core.ContentHash, delivery.CoreHash);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [Trait("VerificationId", "V152_T02")]
    public async Task V152_T02_AdmissionReservesDeliveryCreatedAndBothAttemptFacts(int limit, bool fits)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { MaximumAttempts = 1, MaximumEvents = limit }, admit: false);
        var capacity = await fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(capacity.Available, capacity.ReasonCode);
        Assert.Equal(fits, capacity.CanAdmit);
        var before = fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;");
        var result = await fixture.Store.AdmitProductionInspectionAsync(new(fixture.Core.Admission), fixture.Deadline());
        Assert.Equal(fits, result.Committed);
        if (!fits)
        {
            Assert.Equal("ProductionOutboxReserveCapacityExceeded", result.ReasonCode);
            Assert.Equal(before, fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;"));
        }
        else await fixture.CommitAsync();
        Assert.Equal(fits ? 1 : 0, fixture.Scalar("SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.Equal(fits ? 1 : 0, fixture.Scalar("SELECT COUNT(*) FROM production_inspection_cores;"));
        Assert.Equal(fits ? 1 : 0, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_deliveries;"));
        var page = await fixture.Query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode); // Capacity rejection must not poison a readable store.
        Assert.Equal(fits ? 1 : 0, page.Items.Count);
    }

    [Fact]
    [Trait("VerificationId", "V152_T03")]
    public async Task V152_T03_ReceiptFenceDoesNotConsumeClaimAndRetiredClaimCannotCommitSuccess()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        var attempt = Guid.NewGuid(); var epoch = Guid.NewGuid();
        var started = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1,
            epoch, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("transaction-test")), fixture.Deadline());
        Assert.True(started.Committed, started.ReasonCode);
        using var authority = new OutboxAttemptAuthority(TimeSpan.FromMinutes(1), CancellationToken.None);
        var receipt = fixture.Receiver.Accept(delivery);
        var claim = OutboxAcceptanceVerifier.CreateClaim(delivery, attempt, epoch, receipt, authority);
        var busy = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxSuccessRequest(delivery.DeliveryId,
            epoch, DateTimeOffset.UtcNow, claim, () => "ProductionOutboxCommitFenceBusy"), fixture.Deadline());
        Assert.False(busy.Committed);
        Assert.Equal("ProductionOutboxCommitFenceBusy", busy.ReasonCode);
        Assert.True(claim.IsCurrent);
        Assert.DoesNotContain((await fixture.Query.ReadHistoryAsync()).Events, entry => entry.Kind == OutboxEventKind.Succeeded);
        authority.Retire();
        var retired = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxSuccessRequest(delivery.DeliveryId,
            epoch, DateTimeOffset.UtcNow, claim), fixture.Deadline());
        Assert.False(retired.Committed);
        Assert.Equal("ProductionOutboxAcceptanceClaimRetired", retired.ReasonCode);
        Assert.False(claim.TryConsume());
        Assert.Single((await fixture.Query.ReadPendingAsync()).Items);
        Assert.Equal(1L, fixture.Receiver.EffectCount());
    }

    [Fact]
    [Trait("VerificationId", "V152_T04")]
    public async Task V152_T04_AuthenticatedSuccessIsIdempotentAndLeavesThePendingPage()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        var attempt = Guid.NewGuid(); var epoch = Guid.NewGuid();
        var started = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1,
            epoch, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("transaction-test")), fixture.Deadline());
        Assert.True(started.Committed, started.ReasonCode);
        using var authority = new OutboxAttemptAuthority(TimeSpan.FromMinutes(1), CancellationToken.None);
        var claim = OutboxAcceptanceVerifier.CreateClaim(delivery, attempt, epoch, fixture.Receiver.Accept(delivery), authority);
        var request = new OutboxSuccessRequest(delivery.DeliveryId, epoch, DateTimeOffset.UtcNow, claim);
        var succeeded = await fixture.Store.AppendOutboxOutcomeAsync(request, fixture.Deadline());
        Assert.True(succeeded.Committed, succeeded.ReasonCode);
        var repeated = await fixture.Store.AppendOutboxOutcomeAsync(request, fixture.Deadline());
        Assert.True(repeated.Committed, repeated.ReasonCode);
        Assert.Equal(succeeded.Event!.ContentHash, repeated.Event!.ContentHash);
        Assert.Empty((await fixture.Query.ReadPendingAsync()).Items);
        Assert.Single((await fixture.Query.ReadHistoryAsync()).Events, entry => entry.Kind == OutboxEventKind.Succeeded);
        Assert.False(claim.TryConsume());
        Assert.Equal(1L, fixture.Receiver.EffectCount());
    }

    [Theory]
    [InlineData("count", false)]
    [InlineData("bytes", false)]
    [InlineData("age", false)]
    [InlineData("permanent", false)]
    [InlineData("transient", true)]
    [Trait("VerificationId", "V152_T05")]
    public async Task V152_T05_NewAdmissionReadsDurableRequiredBacklogWithoutRuntimeProjection(string condition, bool allowed)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(maxItems: condition == "count" ? 1 : 100,
            maxBytes: condition == "bytes" ? 1 : 64 * 1024 * 1024,
            maxAge: condition == "age" ? TimeSpan.FromMilliseconds(1) : null);
        await fixture.CommitAsync();
        if (condition is "permanent" or "transient")
        {
            var delivery = fixture.Batch.Deliveries[0];
            var attempt = Guid.NewGuid(); var epoch = Guid.NewGuid();
            var started = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1,
                epoch, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("admission-test")), fixture.Deadline());
            Assert.True(started.Committed, started.ReasonCode);
            var permanent = condition == "permanent";
            var recorded = DateTimeOffset.UtcNow;
            var failed = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(delivery.DeliveryId,
                attempt, epoch, recorded, "V152AdmissionFailure", permanent ? OutboxFailureCategory.Permanent :
                OutboxFailureCategory.Transient, permanent ? null : recorded.AddSeconds(1)), fixture.Deadline());
            Assert.True(failed.Committed, failed.ReasonCode);
        }
        var original = fixture.Core.Admission;
        var candidate = new ProductionInspectionAdmission(Guid.NewGuid(), Guid.NewGuid(), original.RuntimeEpoch,
            original.StationId, original.AdmissionGeneration, new PlcControllerCycle(61, 2), original.EvidenceRequirement,
            original.ActivationReference, original.ActivationSnapshot, original.EndpointBindingHash, original.PlcProfileHash,
            original.PlcPolicyHash, original.ConnectionGeneration, original.ConnectionAttempt, DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), original.TracePolicySnapshot);
        var before = fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;");
        var result = await fixture.Store.AdmitProductionInspectionAsync(new(candidate), fixture.Deadline());
        Assert.Equal(allowed, result.Committed);
        if (!allowed)
        {
            Assert.Equal(condition == "permanent" ? "OutboxRequiredPermanentBlock" : "OutboxRequiredBacklogLimitExceeded",
                result.ReasonCode);
            Assert.Equal(before, fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;"));
        }
        Assert.Equal(allowed ? 2 : 1, fixture.Scalar("SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.True((await fixture.Query.ReadPendingAsync()).Available);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("core")]
    [InlineData("audit")]
    [InlineData("projection")]
    [Trait("VerificationId", "V152_T06")]
    public async Task V152_T06_TamperedEvidenceOrProjectionBlocksColdReadAndNewAttempt(string target)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            var triggerName = target == "projection" ? null : "production_outbox_delivery_immutable_update";
            string? triggerSql = null;
            if (triggerName is not null)
            {
                await using var lookup = connection.CreateCommand();
                lookup.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name;";
                lookup.Parameters.AddWithValue("$name", triggerName);
                triggerSql = Assert.IsType<string>(await lookup.ExecuteScalarAsync());
            }
            await using var command = connection.CreateCommand();
            var tamper = target switch
            {
                "payload" => "UPDATE production_outbox_deliveries SET PayloadBase64=$value;",
                "core" => "UPDATE production_outbox_deliveries SET CoreHash=$value;",
                "audit" => "UPDATE production_outbox_deliveries SET CreatedAuditHash=$value;",
                _ => "UPDATE production_outbox_work SET State='Succeeded';"
            };
            command.CommandText = "BEGIN IMMEDIATE; " + (triggerName is null ? "" : $"DROP TRIGGER {triggerName}; ") +
                tamper + (triggerSql is null ? "" : triggerSql + "; ") + " COMMIT;";
            if (target != "projection")
                command.Parameters.AddWithValue("$value", target == "payload" ?
                    Convert.ToBase64String(new byte[fixture.Batch.Deliveries[0].Payload!.ByteLength]) :
                    OutboxReceiverFixture.Hash("tampered"));
            await command.ExecuteNonQueryAsync();
        }
        var before = fixture.Scalar("SELECT COUNT(*) FROM production_outbox_events;");
        var cold = await fixture.Query.ReadPendingAsync();
        Assert.False(cold.Available);
        Assert.Empty(cold.Items);
        var started = await fixture.Store.BeginOutboxAttemptAsync(new(fixture.Batch.Deliveries[0].DeliveryId,
            Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("tamper-test")), fixture.Deadline());
        Assert.False(started.Committed);
        Assert.Equal(before, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_events;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V152_T07")]
    public async Task V152_T07_ColdSenderReopensInterruptedAttemptAndReusesItsPersistedDelivery(bool receiverAlreadyCommitted)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { MaximumRetryDelay = TimeSpan.FromMilliseconds(10) });
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        var oldAttempt = Guid.NewGuid(); var oldEpoch = Guid.NewGuid();
        var started = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, oldAttempt, 1,
            oldEpoch, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("old-sender")), fixture.Deadline());
        Assert.True(started.Committed, started.ReasonCode);
        var receipt = receiverAlreadyCommitted ? fixture.Receiver.Accept(delivery) : null;
        // Close every old store handle while leaving the attempt unresolved. The receiver
        // process-kill tests separately prove its physical transaction boundaries.
        await fixture.Store.DisposeAsync();
        await using var reopened = new SqliteCommandStore(fixture.Options);
        var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        var replacement = new RuntimeHeldOutboxTransport(fixture.Receiver);
        replacement.Release.TrySetResult(true);
        var newEpoch = Guid.NewGuid();
        var faults = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var worker = new ProductionOutboxWorker(reopened, fixture.Options,
            new(new[] { fixture.Receiver.Binding(replacement) }), newEpoch, reopened.Initialization,
            _ => { }, reason => { faults.Enqueue(reason); return Task.CompletedTask; });
        try
        {
            await worker.Startup.WaitAsync(TimeSpan.FromSeconds(15));
            var query = new SqliteProductionOutboxQuery(fixture.Options);
            var success = await WaitOutboxEventAsync(query, delivery.DeliveryId, OutboxEventKind.Succeeded,
                () => worker.FailureReason);
            Assert.Equal(newEpoch, success.RuntimeEpoch);
            Assert.Equal(2, success.AttemptNumber);
            Assert.NotNull(replacement.Delivery);
            Assert.NotSame(delivery, replacement.Delivery);
            Assert.Equal(delivery.DeliveryId, replacement.Delivery!.DeliveryId);
            Assert.Equal(delivery.CoreHash, replacement.Delivery.CoreHash);
            Assert.Equal(delivery.Payload!.CopyBytes(), replacement.Delivery.Payload!.CopyBytes());
            if (receipt is not null) Assert.Equal(receipt, success.CopyReceipt());
            var history = await query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
            var interrupted = Assert.Single(history.Events, entry => entry.Kind == OutboxEventKind.AttemptFailed);
            Assert.Equal(oldAttempt, interrupted.AttemptId);
            Assert.Equal(oldEpoch, interrupted.RuntimeEpoch);
            Assert.Equal(OutboxFailureCategory.UnknownOutcome, interrupted.FailureCategory);
            Assert.Equal("OutboxProcessRestartOutcomeUnknown", interrupted.ReasonCode);
            Assert.Equal(1L, fixture.Receiver.EffectCount());
            Assert.Empty((await query.ReadPendingAsync()).Items);
            Assert.Empty(faults);
        }
        finally { Assert.True(await worker.StopAsync()); }
    }

    [Fact]
    [Trait("VerificationId", "V152_T08")]
    public async Task V152_T08_PreCoreReservationBlocksAnotherAdmissionAndFaultReleasesIt()
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { MaximumAttempts = 1, MaximumEvents = 4 });
        var capacity = await fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(capacity.Available, capacity.ReasonCode);
        Assert.False(capacity.CanAdmit);
        Assert.Equal("ProductionOutboxReserveCapacityExceeded", capacity.ReasonCode);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_deliveries;"));
        var original = fixture.Core.Admission;
        var next = new ProductionInspectionAdmission(Guid.NewGuid(), Guid.NewGuid(), original.RuntimeEpoch,
            original.StationId, original.AdmissionGeneration, new PlcControllerCycle(61, 2), original.EvidenceRequirement,
            original.ActivationReference, original.ActivationSnapshot, original.EndpointBindingHash, original.PlcProfileHash,
            original.PlcPolicyHash, original.ConnectionGeneration, original.ConnectionAttempt, DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), original.TracePolicySnapshot);
        var refused = await fixture.Store.AdmitProductionInspectionAsync(new(next), fixture.Deadline());
        Assert.False(refused.Committed);
        Assert.Equal("ProductionOutboxReserveCapacityExceeded", refused.ReasonCode);
        var fault = await fixture.Store.AppendProductionInspectionEventAsync(new(original.InspectionId,
            ProductionInspectionEventKind.FaultTerminated, "V152BeforeCoreFault", DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp()), fixture.Deadline());
        Assert.True(fault.Committed, fault.ReasonCode);
        var released = await fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(released.Available, released.ReasonCode);
        Assert.True(released.CanAdmit, released.ReasonCode);
        var admitted = await fixture.Store.AdmitProductionInspectionAsync(new(next), fixture.Deadline());
        Assert.True(admitted.Committed, admitted.ReasonCode);
        var core = TimeoutCore(admitted.Admission!);
        var batch = FrozenOutboxBatch.Prepare(core, fixture.Options.Outbox!, default, fixture.Deadline());
        var committed = await fixture.Store.CommitProductionInspectionCoreAsync(new(core, null, null, batch), fixture.Deadline());
        Assert.True(committed.Committed, committed.ReasonCode);
        var attempt = await fixture.Store.BeginOutboxAttemptAsync(new(batch.Deliveries[0].DeliveryId, Guid.NewGuid(), 1,
            Guid.NewGuid(), DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("reservation-transfer")), fixture.Deadline());
        Assert.True(attempt.Committed, attempt.ReasonCode);
        Assert.True((await fixture.Query.ReadPendingAsync()).Available);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM production_inspection_cores;"));
    }

    [Theory]
    [InlineData(false, 1024 * 1024)]
    [InlineData(true, 1024 * 1024)]
    [InlineData(false, 4096)]
    [InlineData(true, 4096)]
    [Trait("VerificationId", "V152_T09")]
    public async Task V152_T09_AdmissionByteReserveUsesPayloadAndIndependentReceiptBounds(bool fits, int maximumPayload)
    {
        var storedMaximum = Convert.ToBase64String(new byte[maximumPayload]).Length;
        var storedReceipt = Convert.ToBase64String(new byte[16 * 1024]).Length;
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { MaximumAttempts = 1, MaximumPayloadBytes = maximumPayload,
                MaximumTotalBytes = storedMaximum + 2L * storedReceipt - (fits ? 0 : 1) },
            admit: false, routeMaximumPayload: maximumPayload);
        var before = fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;");
        var admitted = await fixture.Store.AdmitProductionInspectionAsync(new(fixture.Core.Admission), fixture.Deadline());
        Assert.Equal(fits, admitted.Committed);
        if (fits) await fixture.CommitAsync();
        else
        {
            Assert.Equal("ProductionOutboxReserveTotalCapacityExceeded", admitted.ReasonCode);
            Assert.Equal(before, fixture.Scalar("SELECT MAX(Sequence) FROM audit_entries;"));
        }
        Assert.Equal(fits ? 1 : 0, fixture.Scalar("SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.True((await fixture.Query.ReadPendingAsync()).Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V152_T10")]
    public async Task V152_T10_TerminalBestEffortFailureReleasesUnusedAttemptReserve(bool preparationFailure)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { MaximumAttempts = 5, MaximumEvents = 16 }, criticality: OutboxRouteCriticality.BestEffort,
            routeMaximumPayload: preparationFailure ? 16 : 1024 * 1024);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        if (preparationFailure) Assert.Null(delivery.Payload);
        else
        {
            var attempt = Guid.NewGuid(); var epoch = Guid.NewGuid();
            var started = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1, epoch,
                DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("terminal-reserve")), fixture.Deadline());
            Assert.True(started.Committed, started.ReasonCode);
            var failed = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(delivery.DeliveryId,
                attempt, epoch, DateTimeOffset.UtcNow, "V152PermanentReceiverRejection", OutboxFailureCategory.Permanent,
                null), fixture.Deadline());
            Assert.True(failed.Committed, failed.ReasonCode);
        }
        var capacity = await fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(capacity.Available, capacity.ReasonCode);
        Assert.True(capacity.CanAdmit, capacity.ReasonCode);
        var old = fixture.Core.Admission;
        var candidate = new ProductionInspectionAdmission(Guid.NewGuid(), Guid.NewGuid(), old.RuntimeEpoch,
            old.StationId, old.AdmissionGeneration, new PlcControllerCycle(61, 2), old.EvidenceRequirement,
            old.ActivationReference, old.ActivationSnapshot, old.EndpointBindingHash, old.PlcProfileHash, old.PlcPolicyHash,
            old.ConnectionGeneration, old.ConnectionAttempt, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), old.TracePolicySnapshot);
        var admitted = await fixture.Store.AdmitProductionInspectionAsync(new(candidate), fixture.Deadline());
        Assert.True(admitted.Committed, admitted.ReasonCode);
        Assert.Single((await fixture.Query.ReadPendingAsync()).Items); // The failed immutable obligation remains visible.
        Assert.Equal(preparationFailure ? 1 : 3, fixture.Scalar("SELECT COUNT(*) FROM production_outbox_events;"));
    }

    private sealed class OutboxCoreFixture : IAsyncDisposable
    {
        private readonly ManualHarness _harness;
        private readonly ModbusQualificationTestServer _peer;
        private OutboxCoreFixture(ManualHarness harness, ModbusQualificationTestServer peer,
            OutboxReceiverFixture receiver, ProductionInspectionCore core, FrozenOutboxBatch batch)
        { _harness = harness; _peer = peer; Receiver = receiver; Core = core; Batch = batch; }
        internal OutboxReceiverFixture Receiver { get; }
        internal ProductionInspectionCore Core { get; }
        internal FrozenOutboxBatch Batch { get; }
        internal SqliteCommandStore Store => _harness.Fixture.Store;
        internal ProductionStoreOptions Options => _harness.Fixture.Options;
        internal SqliteProductionOutboxQuery Query => new(Options);
        internal StoreDeadline Deadline() => new(TimeSpan.FromSeconds(4));
        internal long Scalar(string sql) => _harness.Fixture.Scalar(sql);
        internal async Task CommitAsync()
        {
            var committed = await Store.CommitProductionInspectionCoreAsync(new(Core, null, null, Batch), Deadline());
            Assert.True(committed.Committed, committed.ReasonCode);
        }
        internal static async Task<OutboxCoreFixture> CreateAsync(
            Func<OutboxRouteDefinition, ProductionOutboxStoreOptions>? configure = null, long maxItems = 100, bool admit = true,
            long maxBytes = 64 * 1024 * 1024, TimeSpan? maxAge = null,
            OutboxRouteCriticality criticality = OutboxRouteCriticality.Required, int routeMaximumPayload = 1024 * 1024)
        {
            var receiver = new OutboxReceiverFixture(criticality, maximumPayloadBytes: routeMaximumPayload);
            var peer = ModbusQualificationTestServer.Start();
            ManualHarness? harness = null;
            try
            {
                var options = configure?.Invoke(receiver.Route) ?? new(new[] { receiver.Route });
                harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
                    outbox: options, outboxTransports: new(new[] { receiver.Binding(new RuntimeRejectedOutboxTransport()) }),
                    tracePolicy: OutboxPolicy(options, maxItems, maxBytes, maxAge));
                using var issuer = new ProductionTestIssuer();
                await PrepareProductionAsync(harness, issuer);
                var admission = await BuildAdmissionAsync(harness);
                await harness.StopRuntimePreservingFixtureAsync(); // One writer remains; no background sender competes.
                if (admit)
                {
                    var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(new(admission),
                        new StoreDeadline(TimeSpan.FromSeconds(4)));
                    Assert.True(admitted.Committed, admitted.ReasonCode);
                    admission = Assert.IsType<ProductionInspectionAdmission>(admitted.Admission);
                }
                var core = TimeoutCore(admission);
                var batch = FrozenOutboxBatch.Prepare(core, options, CancellationToken.None,
                    new StoreDeadline(TimeSpan.FromSeconds(4)));
                return new(harness, peer, receiver, core, batch);
            }
            catch
            {
                if (harness is not null) await harness.DisposeAsync();
                await peer.DisposeAsync(); receiver.Dispose(); throw;
            }
        }
        public async ValueTask DisposeAsync()
        { await _harness.DisposeAsync(); await _peer.DisposeAsync(); Receiver.Dispose(); }
    }
}
