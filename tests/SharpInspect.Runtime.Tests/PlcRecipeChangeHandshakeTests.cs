using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PlcRecipeChangeHandshakeTests
{
    private static readonly ModbusRecipeChangeControllerSignals Zero = new(false, false, 0, 0);
    private static readonly ModbusRecipeChangeRuntimeSignals Clear = new(false, 0, 0, 0, 0);
    private static readonly ModbusRecipeChangeControllerSignals Request = new(true, false, 7, 12);

    [Fact]
    public async Task V146_H01_RepeatedRequestHasOneActivationAndAnAcknowledgedReset()
    {
        var completion = new TaskCompletionSource<RecipeChangeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(_ => completion.Task);
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request);
        for (var i = 0; i < 8; i++) await fixture.Observe(Request);
        Assert.Equal(1, fixture.Begun);
        Assert.Empty(fixture.Writes);
        completion.SetResult(Success());
        await fixture.Observe(Request);
        await fixture.Observe(Request);
        Assert.Single(fixture.Writes);
        Assert.Equal(new ModbusRecipeChangeRuntimeSignals(true, 1, 0, 7, 12), fixture.Writes[0]);
        await fixture.Observe(Request with { Acknowledgement = true });
        Assert.Equal(Clear, fixture.Writes[1]);
        await fixture.Observe(Request with { Acknowledgement = true });
        await fixture.Observe(Zero);
        Assert.False(fixture.Handshake.Active);
        Assert.Equal(new[] { RecipeChangeEventKind.ResponsePublished, RecipeChangeEventKind.AcknowledgementObserved,
            RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved }, fixture.Events.Select(value => value.Kind));
        Assert.Equal(1, fixture.Begun);
    }

    [Fact]
    public async Task V146_H02_BusyDecisionIsFixedAtObservationEvenWhenTheStationBecomesIdle()
    {
        var busy = true;
        using var fixture = new Fixture(_ => Task.FromResult(busy
            ? new RecipeChangeDecision(RecipeChangeOutcome.RejectedBusy, RecipeChangeReason.RuntimeBusy, "RecipeActivationRuntimeBusy")
            : Success()));
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request);
        busy = false;
        await fixture.Observe(Request);
        Assert.Equal((ushort)RecipeChangeOutcome.RejectedBusy, Assert.Single(fixture.Writes).Outcome);
        Assert.Equal(1, fixture.Begun);
    }

    [Theory]
    [InlineData(7u, 13u, false)]
    [InlineData(8u, 12u, false)]
    [InlineData(7u, 12u, true)]
    public async Task V146_H03_ChangedIdentityOrPrematureAcknowledgementRevokesPendingActivation(
        uint sequence, uint code, bool acknowledgement)
    {
        var completion = new TaskCompletionSource<RecipeChangeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(_ => completion.Task);
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(new(true, acknowledgement, sequence, code)));
        Assert.Equal(1, fixture.Cancelled);
        Assert.Equal(RecipeChangeEventKind.ProtocolFault, Assert.Single(fixture.Events).Kind);
        Assert.Empty(fixture.Writes);
        completion.SetResult(new(RecipeChangeOutcome.FailedActivation, RecipeChangeReason.Cancelled, "RecipeActivationCancelled"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(Request));
        Assert.Equal(1, fixture.Begun);
    }

    [Fact]
    public async Task V146_H04_CompletedIdentityCannotStartASecondActivation()
    {
        using var fixture = new Fixture(_ => Task.FromResult(Success()));
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request); await fixture.Observe(Request);
        await fixture.Observe(Request with { Acknowledgement = true }); await fixture.Observe(Zero);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(Request));
        Assert.Equal("RecipeChangeRequestIdentityReused", error.Message);
        Assert.Equal(1, fixture.Begun);
        Assert.Equal(2, fixture.Writes.Count);
    }

    [Fact]
    public async Task V146_H05_DirtyStartupDoesNotClearOrReplayUnfinishedFields()
    {
        using var fixture = new Fixture(_ => Task.FromResult(Success()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Handshake.InitializeAsync(Request, Clear));
        Assert.Equal(0, fixture.Begun); Assert.Empty(fixture.Writes);
        Assert.Equal("RecipeChangeInitialStateInvalid", Assert.Single(fixture.Events).ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V146_H11_RetainedRuntimeResponseNeverClearsOrReplaysAtStartup(bool responseValid)
    {
        using var fixture = new Fixture(_ => Task.FromResult(Success()));
        var retained = new ModbusRecipeChangeRuntimeSignals(responseValid, 1, 0, 7, 12);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Handshake.InitializeAsync(Zero, retained));
        Assert.Equal("RecipeChangeInitialStateInvalid", error.Message);
        Assert.Equal(0, fixture.Begun);
        Assert.Empty(fixture.Writes);
        Assert.Equal(RecipeChangeEventKind.ProtocolFault, Assert.Single(fixture.Events).Kind);
    }

    [Fact]
    public async Task V146_H06_MissingAcknowledgementTimesOutWithoutForgingReset()
    {
        long time = Stopwatch.Frequency;
        using var fixture = new Fixture(_ => Task.FromResult(Success()), () => time);
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request); await fixture.Observe(Request);
        time += Stopwatch.Frequency * 3;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(Request));
        Assert.Equal("RecipeChangeHandshakeDeadlineExceeded", error.Message);
        Assert.Single(fixture.Writes);
        Assert.DoesNotContain(fixture.Events, value => value.Kind is RecipeChangeEventKind.AcknowledgementObserved or RecipeChangeEventKind.ResetObserved);
    }

    [Fact]
    public async Task V146_H07_FailedDurableDecisionNeverProducesAPhysicalResponse()
    {
        using var fixture = new Fixture(_ => Task.FromException<RecipeChangeDecision>(new IOException("AuditCommitFailed")));
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(Request));
        Assert.Equal("RecipeChangeDecisionUnavailable", error.Message);
        Assert.Empty(fixture.Writes);
        Assert.Equal(1, fixture.Cancelled);
    }

    [Theory]
    [InlineData(RecipeChangeEventKind.ResponsePublished, 1)]
    [InlineData(RecipeChangeEventKind.AcknowledgementObserved, 1)]
    [InlineData(RecipeChangeEventKind.ResponseCleared, 2)]
    [InlineData(RecipeChangeEventKind.ResetObserved, 2)]
    public async Task V146_H08_AuditFailureAfterDurableDecisionPermanentlyClosesTheHandshake(
        RecipeChangeEventKind failure, int expectedWrites)
    {
        using var fixture = new Fixture(_ => Task.FromResult(Success()), recordFailure: failure);
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await fixture.Observe(Request);
            await fixture.Observe(Request with { Acknowledgement = true });
            await fixture.Observe(Zero);
        });
        Assert.Equal("RecipeChangeAuditUnavailable", error.Message);
        Assert.Equal(expectedWrites, fixture.Writes.Count);
        Assert.Equal(1, fixture.Cancelled);
        Assert.DoesNotContain(fixture.Events, value => value.Kind == failure);
        Assert.Equal(RecipeChangeEventKind.ProtocolFault, fixture.Events.Last().Kind);
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Observe(Request with { Acknowledgement = true }));
        Assert.Equal("RecipeChangeHandshakeUnavailable", retry.Message);
        Assert.Equal(expectedWrites, fixture.Writes.Count);
    }

    [Fact]
    public async Task V146_H09_PersistedReplayFaultCannotPublishAnOrdinaryResponse()
    {
        using var fixture = new Fixture(_ => Task.FromResult(new RecipeChangeDecision(
            RecipeChangeOutcome.ProtocolFault, RecipeChangeReason.DuplicateRequest, "RecipeChangeRequestIdentityReused")));
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(Request));
        Assert.Equal("RecipeChangeRequestIdentityReused", error.Message);
        Assert.Empty(fixture.Writes);
        Assert.Equal(1, fixture.Cancelled);
    }

    [Fact]
    public async Task V146_H10_PartialControllerResetCannotCompleteTheHandshake()
    {
        using var fixture = new Fixture(_ => Task.FromResult(Success()));
        await fixture.Handshake.InitializeAsync(Zero, Clear);
        await fixture.Observe(Request); await fixture.Observe(Request);
        await fixture.Observe(Request with { Acknowledgement = true });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Observe(Request));
        Assert.Equal("RecipeChangePartialResetInvalid", error.Message);
        Assert.DoesNotContain(fixture.Events, value => value.Kind == RecipeChangeEventKind.ResetObserved);
    }

    private static RecipeChangeDecision Success() => new(RecipeChangeOutcome.Succeeded, RecipeChangeReason.None,
        "RecipeActivationSucceeded", new(2, Guid.NewGuid(), new string('A', 64)));

    private sealed class Fixture : IDisposable
    {
        internal int Begun;
        internal int Cancelled;
        internal List<RecipeChangeTransition> Events { get; } = new();
        internal List<ModbusRecipeChangeRuntimeSignals> Writes { get; } = new();
        internal PlcRecipeChangeHandshake Handshake { get; }
        internal Fixture(Func<CancellationToken, Task<RecipeChangeDecision>> execute, Func<long>? clock = null,
            RecipeChangeEventKind? recordFailure = null)
        {
            Handshake = new(TimeSpan.FromSeconds(2), _ =>
            {
                Begun++;
                return new(execute, () => Cancelled++);
            }, value =>
            {
                if (value.Kind == recordFailure) return Task.FromException(new IOException("AuditCommitFailed"));
                Events.Add(value); return Task.CompletedTask;
            },
                (valid, outcome, reason, sequence, code, _) =>
                { Writes.Add(new(valid, outcome, reason, sequence, code)); return Task.CompletedTask; }, clock);
        }
        internal Task Observe(ModbusRecipeChangeControllerSignals value) => Handshake.ObserveAsync(value, CancellationToken.None);
        public void Dispose() => Handshake.Dispose();
    }
}
