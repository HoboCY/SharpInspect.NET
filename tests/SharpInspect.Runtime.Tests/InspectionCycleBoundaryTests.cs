using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class InspectionCycleBoundaryTests
{
    [Fact]
    public async Task V140_B01_InvalidAndBusyRequestsNeverBecomeDeferredAdmissions()
    {
        var current = new ModbusControllerSignals(false, false, 1, 1);
        var accepted = 0;
        var failures = new List<string>();
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            Array.Empty<PlcControllerCycle>(), () => Interlocked.Increment(ref accepted), failures.Add,
            CancellationToken.None);
        observer.Start();
        await ObserveAsync(current);
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);

        await ObserveAsync(new(true, false, 0, 0));
        Assert.False(observer.TryTakeAccepted(out _));
        Assert.Equal("QualificationStimulusControllerEpochInvalid", observer.TakeRejected()?.ReasonCode);
        await ObserveAsync(new(false, false, 1, 1));
        await ObserveAsync(new(true, false, 1, 1));
        Assert.True(observer.TryTakeAccepted(out var first));
        Assert.Equal((uint)1, first!.CycleSequence);
        Assert.Equal(1, accepted);

        // A second physical edge arrives while the first run owns the slot.
        await ObserveAsync(new(false, false, 1, 2));
        await ObserveAsync(new(true, false, 1, 2));
        Assert.False(observer.TryTakeAccepted(out _));
        Assert.Equal("QualificationTriggerRejectedWhileNotReady", observer.TakeRejected()?.ReasonCode);
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);
        await ObserveAsync(new(true, false, 1, 2));
        Assert.False(observer.TryTakeAccepted(out _));
        await ObserveAsync(new(false, false, 1, 2));
        await ObserveAsync(new(true, false, 1, 2));
        Assert.False(observer.TryTakeAccepted(out _));
        Assert.Equal("QualificationDuplicateCycleRejected", observer.TakeRejected()?.ReasonCode);

        await ObserveAsync(new(false, false, 1, 3));
        await ObserveAsync(new(true, false, 1, 3));
        Assert.True(observer.TryTakeAccepted(out var next));
        Assert.Equal((uint)3, next!.CycleSequence);
        Assert.Equal(2, accepted);
        Assert.Empty(failures);

        async Task ObserveAsync(ModbusControllerSignals value)
        {
            var before = observer.Latest.Sequence;
            Volatile.Write(ref current, value);
            await WaitAsync(() => observer.Latest is var observed &&
                observed.Sequence > before && observed.Signals == value);
        }
    }

    [Fact]
    public async Task V140_B02_DisposeOwnsBlockedReadyAdvertisementAndSuppressesLateAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(new ModbusControllerSignals(false, false, 1, 1)),
            TimeSpan.FromMilliseconds(1), Array.Empty<PlcControllerCycle>(),
            () => callbacks++, _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        var enabling = observer.EnableAcceptingAsync(async () =>
        { entered.SetResult(); await release.Task; }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var retirement = observer.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enabling);
        await retirement.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(observer.TryTakeAccepted(out _));
        Assert.Equal(0, callbacks);
        await observer.DisposeAsync();
    }

    [Fact]
    public async Task V140_B03_UncertainPublicationResponseSealsAllLaterWrites()
    {
        var writes = new List<bool>();
        var output = new InspectionCycleOutputLatch((ready, busy, valid, fault, violation, token) =>
        {
            writes.Add(valid);
            return valid ? Task.FromException(new IOException("response lost after physical write")) : Task.CompletedTask;
        });
        await output.ChangeAsync(CancellationToken.None, busy: true);
        await Assert.ThrowsAsync<IOException>(() => output.ChangeAsync(CancellationToken.None, busy: false, valid: true));
        Assert.Null(output.ConfirmedResultValid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.ChangeAsync(CancellationToken.None,
            ready: false, busy: false, fault: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.ChangeAsync(CancellationToken.None, violation: true));
        Assert.Equal(new[] { false, true }, writes);
    }

    [Fact]
    public async Task V140_B04_ConcurrentProtocolFactCannotOverwriteNewerPublicationBits()
    {
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(bool Busy, bool Valid, bool Violation)>();
        var output = new InspectionCycleOutputLatch(async (ready, busy, valid, fault, violation, token) =>
        {
            writes.Add((busy, valid, violation));
            if (valid && !violation)
            { publicationEntered.SetResult(); await publicationResponse.Task; }
        });
        await output.ChangeAsync(CancellationToken.None, busy: true);
        var publication = output.ChangeAsync(CancellationToken.None, busy: false, valid: true);
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var protocolFact = output.ChangeAsync(CancellationToken.None, violation: true);
        Assert.False(protocolFact.IsCompleted);
        publicationResponse.SetResult();
        await Task.WhenAll(publication, protocolFact).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal((false, true, true), writes[^1]);
        Assert.True(output.ConfirmedResultValid);
    }

    [Fact]
    public async Task V140_B05_PersistedControllerKeyIsRejectedAfterObserverRestart()
    {
        var current = new ModbusControllerSignals(false, false, 22, 13);
        var accepted = 0;
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            new[] { new PlcControllerCycle(22, 13) }, () => accepted++, _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);
        Volatile.Write(ref current, new(true, false, 22, 13));
        await WaitAsync(() => observer.Latest.Signals?.Trigger == true);
        Assert.False(observer.TryTakeAccepted(out _));
        Assert.Equal(0, accepted);
        Assert.Equal("QualificationDuplicateCycleRejected", observer.TakeRejected()?.ReasonCode);
    }

    [Fact]
    public async Task V140_B06_ControllerEpochChangeIsDetectedWithTriggerLow()
    {
        var current = new ModbusControllerSignals(false, false, 1, 1);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            Array.Empty<PlcControllerCycle>(), () => callbacks++, reason => failed.TrySetResult(reason), CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);
        Volatile.Write(ref current, new(true, false, 1, 1));
        await WaitAsync(() => observer.Latest.Signals?.Trigger == true);
        Assert.True(observer.TryTakeAccepted(out _));
        Volatile.Write(ref current, new(false, false, 2, 1));
        Assert.Equal("QualificationControllerEpochChanged", await failed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Throws<InvalidOperationException>(observer.RequireHealthy);
        Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task V140_B07_ExitRevokesBlockedReadyAdvertisementPermanently()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(new ModbusControllerSignals(false, false, 1, 1)),
            TimeSpan.FromMilliseconds(1), Array.Empty<PlcControllerCycle>(),
            () => throw new InvalidOperationException("Unexpected admission"), _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        var enabling = observer.EnableAcceptingAsync(async () =>
        { entered.SetResult(); await release.Task; }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        observer.RevokeAdmission();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enabling);
        Assert.False(observer.TryTakeAccepted(out _));
        var writes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.EnableAcceptingAsync(
            () => { writes++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task V140_B08_ExitTurnsUnclaimedCandidateIntoDurableRejectionInput()
    {
        var current = new ModbusControllerSignals(false, false, 1, 1);
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            Array.Empty<PlcControllerCycle>(), () => { }, _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);
        Volatile.Write(ref current, new(true, false, 1, 1));
        await WaitAsync(() => observer.Latest.Signals?.Trigger == true);
        observer.RevokeAdmission();
        Assert.False(observer.TryTakeAccepted(out _));
        var rejected = observer.TakeRejected();
        Assert.NotNull(rejected);
        Assert.Equal(new PlcControllerCycle(1, 1), rejected!.Key);
        Assert.Equal("QualificationRequestRevokedBeforeAdmission", rejected.ReasonCode);
        Assert.Null(observer.TakeRejected());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task V140_B09_AckDeadlineUsesFirstObservationDespiteSlowJournal(bool highPhase, bool timely)
    {
        var current = new ModbusControllerSignals(false, false, 1, 1);
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            Array.Empty<PlcControllerCycle>(), () => { }, _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        var writes = new List<bool>();
        var output = new InspectionCycleOutputLatch((ready, busy, valid, fault, violation, token) =>
        { writes.Add(valid); return Task.CompletedTask; });
        var coordinator = new InspectionCycleCoordinator<string>();
        coordinator.SetPhase(InspectionCyclePhase.WritingPayload);
        var facts = new List<InspectionCycleDeliveryFact>();
        var receipt = new InspectionCycleCommitReceipt<string>(new(ExecutionKind.Qualification, Guid.NewGuid()),
            new string('A', 64), "durable payload");
        var operation = coordinator.PublishAndAcknowledgeAsync(receipt, new(1, 1), observer, output,
            (_, _) => Task.CompletedTask, async fact =>
            {
                facts.Add(fact);
                var isHigh = fact == InspectionCycleDeliveryFact.ResultValidPublished;
                var isLow = fact == InspectionCycleDeliveryFact.ResultValidCleared;
                if (!isHigh && !isLow) return;
                var delayedPhase = highPhase == isHigh;
                if (delayedPhase && !timely) await Task.Delay(600);
                Volatile.Write(ref current, new(false, isHigh, 1, 1));
                await WaitAsync(() => observer.Latest.Signals?.ResultAck == isHigh);
                // Timely acknowledgement remains valid even when writing its
                // preceding immutable journal fact returns after the deadline.
                if (delayedPhase && timely) await Task.Delay(600);
            }, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        if (timely)
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { true, false }, writes);
            Assert.Contains(InspectionCycleDeliveryFact.AckReset, facts);
        }
        else
        {
            await Assert.ThrowsAsync<InspectionCycleAckTimeoutException>(() => operation);
            Assert.DoesNotContain(InspectionCycleDeliveryFact.AckReset, facts);
            Assert.Equal(highPhase ? new[] { true } : new[] { true, false }, writes);
        }
    }

    [Fact]
    public async Task V140_B10_InitialAckHighCannotAdvertiseReady()
    {
        var current = new ModbusControllerSignals(false, true, 1, 1);
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            Array.Empty<PlcControllerCycle>(), () => { }, _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        var writes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => observer.EnableAcceptingAsync(
            () => { writes++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, writes);
        Volatile.Write(ref current, new(false, false, 1, 1));
        await WaitAsync(() => observer.Latest.Signals?.ResultAck == false);
        await observer.EnableAcceptingAsync(() => { writes++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, writes);
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(1, timeout.Token);
    }
}
