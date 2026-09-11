using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionInspectionObserverTests
{
    [Fact]
    public async Task V142_R06_AdmissionRevokedDuringReadyPublicationCanBeEnabledAgain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(new ModbusControllerSignals(false, false, 61, 1)),
            TimeSpan.FromMilliseconds(1), Array.Empty<PlcControllerCycle>(),
            () => throw new InvalidOperationException("Unexpected admission"), _ => { }, CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);

        var enabling = observer.EnableAcceptingAsync(async () =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        observer.RejectPendingAdmission("ProductionTriggerPermitRevoked");
        Assert.False(observer.IsAccepting);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enabling);
        Assert.False(observer.IsAccepting);

        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);
        Assert.True(observer.IsAccepting);
    }

    [Fact]
    public async Task V142_R07_RevokedPendingKeyRemainsRejectedAfterFreshEnable()
    {
        var current = new ModbusControllerSignals(false, false, 61, 1);
        var accepted = 0;
        await using var observer = new InspectionCycleRequestObserver(
            _ => Task.FromResult(Volatile.Read(ref current)), TimeSpan.FromMilliseconds(1),
            Array.Empty<PlcControllerCycle>(), () => Interlocked.Increment(ref accepted), _ => { },
            CancellationToken.None);
        observer.Start();
        await WaitAsync(() => observer.Latest.Sequence > 0);
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);

        await ObserveAsync(new(false, false, 61, 1));
        await ObserveAsync(new(true, false, 61, 1));
        Assert.False(observer.IsAccepting);
        observer.RejectPendingAdmission("ProductionTriggerPermitRevoked");
        Assert.False(observer.IsAccepting);
        Assert.False(observer.TryTakeAccepted(out _));
        var revoked = observer.TakeRejected();
        Assert.NotNull(revoked);
        Assert.Equal(new PlcControllerCycle(61, 1), revoked!.Key);
        Assert.Equal("ProductionTriggerPermitRevoked", revoked.ReasonCode);
        Assert.Equal(1, accepted);

        await ObserveAsync(new(false, false, 61, 1));
        await observer.EnableAcceptingAsync(() => Task.CompletedTask, CancellationToken.None);
        Assert.True(observer.IsAccepting);
        await ObserveAsync(new(true, false, 61, 1));
        Assert.False(observer.TryTakeAccepted(out _));
        var duplicate = observer.TakeRejected();
        Assert.NotNull(duplicate);
        Assert.Equal(new PlcControllerCycle(61, 1), duplicate!.Key);
        Assert.Equal("QualificationDuplicateCycleRejected", duplicate.ReasonCode);
        Assert.Equal(1, accepted);

        await ObserveAsync(new(false, false, 61, 2));
        await ObserveAsync(new(true, false, 61, 2));
        Assert.True(observer.TryTakeAccepted(out var fresh));
        Assert.Equal((uint)2, fresh!.CycleSequence);
        Assert.Equal(2, accepted);

        async Task ObserveAsync(ModbusControllerSignals value)
        {
            var before = observer.Latest.Sequence;
            Volatile.Write(ref current, value);
            await WaitAsync(() => observer.Latest is var observed &&
                observed.Sequence > before && observed.Signals == value);
        }
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(1, timeout.Token).ConfigureAwait(false);
    }
}
