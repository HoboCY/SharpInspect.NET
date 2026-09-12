using SharpInspect.Abstractions;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("Start")]
    [InlineData("PermanentFailure")]
    [InlineData("Success")]
    [Trait("VerificationId", "V152_T11")]
    public async Task V152_T11_OutboxCommitPublishesAuditRecheckBeforeReturningSuccess(string transition)
    {
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        var attempt = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        Task<OutboxWriteResult> StartAsync() => fixture.Store.BeginOutboxAttemptAsync(new(delivery.DeliveryId,
            attempt, 1, epoch, DateTimeOffset.UtcNow, OutboxReceiverFixture.Hash("audit-publication")),
            fixture.Deadline()).AsTask();
        if (transition != "Start")
        {
            var started = await StartAsync();
            Assert.True(started.Committed, started.ReasonCode);
        }
        using var authority = new OutboxAttemptAuthority(TimeSpan.FromMinutes(1), CancellationToken.None);
        var claim = transition == "Success" ? OutboxAcceptanceVerifier.CreateClaim(delivery, attempt, epoch,
            fixture.Receiver.Accept(delivery), authority) : null;
        Func<Task<OutboxWriteResult>> write = transition switch
        {
            "Start" => StartAsync,
            "PermanentFailure" => () => fixture.Store.AppendOutboxOutcomeAsync(new OutboxFailureRequest(
                delivery.DeliveryId, attempt, epoch, DateTimeOffset.UtcNow, "V152PermanentFailure",
                OutboxFailureCategory.Permanent, null), fixture.Deadline()).AsTask(),
            _ => () => fixture.Store.AppendOutboxOutcomeAsync(new OutboxSuccessRequest(delivery.DeliveryId,
                epoch, DateTimeOffset.UtcNow, claim!), fixture.Deadline()).AsTask()
        };
        var result = await StoreAuditPublicationBarrier.AssertPublishedBeforeCompletionAsync(fixture.Store, write,
            () => fixture.Scalar("SELECT COUNT(*) FROM production_outbox_events;"), transition == "Start" ? 2 : 3);
        Assert.True(result.Committed, result.ReasonCode);
        var history = await fixture.Query.ReadHistoryAsync();
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(transition == "Start" ? OutboxEventKind.AttemptStarted : transition == "PermanentFailure"
            ? OutboxEventKind.AttemptFailed : OutboxEventKind.Succeeded, history.Events.Last().Kind);
        var pending = await fixture.Query.ReadPendingAsync();
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.Equal(transition == "Success" ? 0 : 1, pending.Items.Count);
    }
}
