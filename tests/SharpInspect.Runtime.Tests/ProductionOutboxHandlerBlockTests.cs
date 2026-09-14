using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(OutboxRouteCriticality.Required, "pending")]
    [InlineData(OutboxRouteCriticality.Required, "active")]
    [InlineData(OutboxRouteCriticality.Required, "exhausted")]
    [InlineData(OutboxRouteCriticality.Required, "permanent")]
    [InlineData(OutboxRouteCriticality.BestEffort, "pending")]
    [Trait("VerificationId", "V153_T20")]
    public async Task V153_T20_MissingHistoricalHandlerPersistsWithoutFabricatingAnAttempt(
        OutboxRouteCriticality criticality, string initial)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync(route => new(new[] { route })
            { ManualRecovery = new(), MaximumAttempts = 1 }, criticality: criticality);
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        if (initial != "pending")
        {
            var attempt = Guid.NewGuid(); var epoch = Guid.NewGuid();
            var start = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, attempt, 1, epoch,
                DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("historical-handler")), fixture.Deadline());
            Assert.True(start.Committed, start.ReasonCode);
            if (initial != "active")
            {
                var now = DateTimeOffset.UtcNow;
                var result = await fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(delivery.DeliveryId,
                    attempt, epoch, now, "V153FixtureFailure", initial == "permanent" ?
                        OutboxFailureCategory.Permanent : OutboxFailureCategory.Transient,
                    initial == "permanent" ? null : now.AddSeconds(1)), fixture.Deadline());
                Assert.True(result.Committed, result.ReasonCode);
            }
        }
        var missing = new ProductionOutboxOptions(Array.Empty<OutboxTransportBinding>());
        await RunHandlerStartupAsync(fixture, missing);
        var page = await fixture.Query.ReadPendingAsync();
        Assert.True(page.Available, page.ReasonCode);
        var blocked = Assert.Single(page.Items);
        Assert.True(blocked.PermanentBlock);
        Assert.False(blocked.RetryEligible);
        Assert.Null(blocked.ActiveAttemptId);
        Assert.Equal(initial == "pending" ? 0 : 1, blocked.AttemptCount);
        Assert.Equal(delivery.Payload!.CopyBytes(), blocked.Delivery.Payload!.CopyBytes());
        Assert.Equal(delivery.DeliveryId, blocked.Delivery.DeliveryId);
        Assert.Equal(fixture.Core.ContentHash, blocked.Delivery.CoreHash);
        Assert.Equal(criticality == OutboxRouteCriticality.Required,
            ProductionOutboxBinding.RequiredBacklogFailure(fixture.Core.Admission.TracePolicySnapshot,
                page.Backlog!, DateTimeOffset.UtcNow) is not null);
        var history = await fixture.Query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.True(history.Available, history.ReasonCode);
        var fact = Assert.Single(history.Events, x => x.Kind == OutboxEventKind.HandlerBlocked);
        Assert.Null(fact.AttemptId); Assert.Null(fact.AttemptNumber); Assert.Null(fact.RuntimeEpoch);
        Assert.Equal(OutboxFailureCategory.Permanent, fact.FailureCategory);
        Assert.Equal(delivery.Payload.ContentHash, fact.PayloadHash);
        if (initial == "active")
            Assert.Equal(OutboxFailureCategory.UnknownOutcome,
                Assert.Single(history.Events, x => x.Kind == OutboxEventKind.AttemptFailed).FailureCategory);

        // Repeated startup adds no duplicate system facts; restoring the transport alone
        // never grants retry authority or reclassifies the old block as a success.
        await RunHandlerStartupAsync(fixture, missing);
        var restored = new ProductionOutboxOptions(new[]
            { fixture.Receiver.Binding(new ProductionOutboxProtocolTests.ReceiverTransport(fixture.Receiver)) });
        await RunHandlerStartupAsync(fixture, restored);
        var again = await fixture.Query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.Equal(history.Events.Select(x => x.ContentHash), again.Events.Select(x => x.ContentHash));
        Assert.Equal(0, fixture.Receiver.EffectCount());
        var unauthorizedAttempt = await fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId, Guid.NewGuid(),
            blocked.AttemptCount + 1, Guid.NewGuid(), DateTimeOffset.UtcNow,
            OutboxReceiverFixture.Hash("restored-handler")), fixture.Deadline());
        Assert.False(unauthorizedAttempt.Committed);
        Assert.Equal("ProductionOutboxPermanentBlockRecorded", unauthorizedAttempt.ReasonCode);

        await fixture.Store.DisposeAsync();
        await using var reopened = new SqliteCommandStore(fixture.Options);
        var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        var cold = await new SqliteProductionOutboxQuery(fixture.Options).ReadPendingAsync();
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Equal(blocked.StateRevisionHash, Assert.Single(cold.Items).StateRevisionHash);
    }

    private static async Task RunHandlerStartupAsync(OutboxCoreFixture fixture, ProductionOutboxOptions transports)
    {
        string? fault = null;
        var worker = new ProductionOutboxWorker(fixture.Store, fixture.Options, transports, Guid.NewGuid(),
            Task.CompletedTask, _ => { }, reason => { fault = reason; return Task.CompletedTask; });
        try { await worker.Startup.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { Assert.True(await worker.StopAsync()); }
        Assert.Null(fault);
    }
}
