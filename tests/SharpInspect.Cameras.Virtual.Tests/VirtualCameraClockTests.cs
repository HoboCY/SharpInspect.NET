using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualCameraClockTests
{
    [Fact]
    public void V116_T01_InitialAndAdvanceProduceUtcAndMonotonicFrameTime()
    {
        var initial = new DateTimeOffset(2026, 9, 8, 8, 0, 0, TimeSpan.FromHours(8));
        using var clock = new VirtualCameraClock(initial);

        Assert.Equal(0, clock.Timestamp);
        Assert.Equal(initial.ToUniversalTime(), clock.UtcNow);

        clock.AdvanceBy(TimeSpan.FromMilliseconds(250));

        Assert.Equal(TimeSpan.FromMilliseconds(250).Ticks, clock.Timestamp);
        Assert.Equal(initial.ToUniversalTime().AddMilliseconds(250), clock.UtcNow);
        var point = clock.GetTimePoint();
        Assert.Equal(clock.UtcNow, point.HostObservedAtUtc);
        Assert.Equal(clock.Timestamp, point.MonotonicTimestamp);
        Assert.Equal(TimeSpan.TicksPerSecond, VirtualCameraClock.Frequency);
    }

    [Fact]
    public void V116_T02_EventsUseDueTimeAndStableRegistrationOrderAcrossLargeJump()
    {
        using var clock = new VirtualCameraClock(Utc());
        var observed = new List<(string Name, long Timestamp)>();
        var five = 5 * TimeSpan.TicksPerMillisecond;
        var ten = 10 * TimeSpan.TicksPerMillisecond;

        clock.Schedule(five, () => observed.Add(("first", clock.Timestamp)));
        clock.Schedule(five, () => observed.Add(("second", clock.Timestamp)));
        clock.Schedule(ten, () => observed.Add(("third", clock.Timestamp)));

        clock.AdvanceTo(ten);

        Assert.Equal(new[] { "first", "second", "third" }, observed.Select(item => item.Name));
        Assert.Equal(new[] { five, five, ten }, observed.Select(item => item.Timestamp));
        Assert.Equal(ten, clock.Timestamp);

        clock.Schedule(clock.Timestamp, () => observed.Add(("zero", clock.Timestamp)));
        clock.AdvanceBy(TimeSpan.Zero);
        Assert.Equal("zero", observed[^1].Name);
        Assert.Equal(0, clock.PendingEventCount);
    }

    [Fact]
    public void V116_T03_UtcJumpDoesNotMoveMonotonicSchedule()
    {
        using var clock = new VirtualCameraClock(Utc());
        clock.AdvanceBy(TimeSpan.FromSeconds(1));
        var due = clock.Timestamp + TimeSpan.FromSeconds(2).Ticks;
        var fired = false;
        clock.Schedule(due, () => fired = true);

        clock.SetUtc(new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, clock.Timestamp);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 4, 0, 0, TimeSpan.Zero), clock.UtcNow);

        clock.AdvanceTo(due);

        Assert.True(fired);
        Assert.Equal(due, clock.Timestamp);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 4, 0, 2, TimeSpan.Zero), clock.UtcNow);
    }

    [Fact]
    public void V116_T04_CancellationPhysicallyRemovesEventsAndCapacityIsBounded()
    {
        using var clock = new VirtualCameraClock(Utc());
        var handles = Enumerable.Range(0, 512)
            .Select(index => clock.Schedule(1_000_000 + index, static () => { }))
            .ToArray();

        Assert.Equal(512, clock.PendingEventCount);
        var full = Assert.Throws<InvalidOperationException>(() =>
            clock.Schedule(2_000_000, static () => { }));
        Assert.Equal("VirtualCameraClockScheduleCapacityExceeded", full.Message);

        for (var index = 0; index < handles.Length; index += 2)
        {
            handles[index].Dispose();
            handles[index].Dispose();
        }

        Assert.Equal(256, clock.PendingEventCount);
        var replacements = Enumerable.Range(0, 256)
            .Select(index => clock.Schedule(2_000_000 + index, static () => { }))
            .ToArray();
        Assert.Equal(512, clock.PendingEventCount);

        foreach (var handle in handles) handle.Dispose();
        foreach (var handle in replacements) handle.Dispose();
        Assert.Equal(0, clock.PendingEventCount);
    }

    [Fact]
    public void V116_T05_InvalidOrOverflowingMutationsDoNotChangeState()
    {
        using var clock = new VirtualCameraClock(Utc());

        var negative = Assert.Throws<ArgumentOutOfRangeException>(() =>
            clock.AdvanceBy(TimeSpan.FromTicks(-1)));
        Assert.StartsWith("VirtualCameraClockDeltaNegative", negative.Message, StringComparison.Ordinal);
        Assert.Equal("delta", negative.ParamName);
        Assert.Equal(0, clock.Timestamp);

        var invalid = Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(-1));
        Assert.StartsWith("VirtualCameraClockTimestampInvalid", invalid.Message, StringComparison.Ordinal);
        Assert.Equal("timestamp", invalid.ParamName);
        Assert.Equal(0, clock.Timestamp);

        clock.AdvanceBy(TimeSpan.FromTicks(1));
        var past = Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(0));
        Assert.StartsWith("VirtualCameraClockTimestampCannotGoBackwards", past.Message, StringComparison.Ordinal);
        Assert.Equal("timestamp", past.ParamName);

        var schedulePast = Assert.Throws<ArgumentOutOfRangeException>(() =>
            clock.Schedule(-1, static () => { }));
        Assert.StartsWith("VirtualCameraClockScheduleInPast", schedulePast.Message, StringComparison.Ordinal);
        Assert.Equal("dueTimestamp", schedulePast.ParamName);

        using var nearUtcMaximum = new VirtualCameraClock(DateTimeOffset.MaxValue);
        var overflow = Assert.Throws<ArgumentOutOfRangeException>(() =>
            nearUtcMaximum.AdvanceBy(TimeSpan.FromTicks(1)));
        Assert.StartsWith("VirtualCameraClockUtcOverflow", overflow.Message, StringComparison.Ordinal);
        Assert.Equal("delta", overflow.ParamName);
        Assert.Equal(0, nearUtcMaximum.Timestamp);
    }

    [Fact]
    public void V116_T06_ReentryAndCallbackFailuresAreStableAndDoNotLeakDetails()
    {
        using var reentry = new VirtualCameraClock(Utc());
        string? reentryReason = null;
        reentry.Schedule(0, () =>
        {
            var exception = Assert.Throws<InvalidOperationException>(() => reentry.AdvanceBy(TimeSpan.Zero));
            reentryReason = exception.Message;
        });
        reentry.AdvanceBy(TimeSpan.Zero);
        Assert.Equal("VirtualCameraClockReentrantOperation", reentryReason);

        using var failure = new VirtualCameraClock(Utc());
        failure.Schedule(0, () => throw new InvalidOperationException("private callback detail"));
        var callbackFailure = Assert.Throws<InvalidOperationException>(() =>
            failure.AdvanceBy(TimeSpan.Zero));
        Assert.Equal("VirtualCameraClockCallbackFailed", callbackFailure.Message);
        Assert.DoesNotContain("private callback detail", callbackFailure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void V116_T07_CallbackMayScheduleNowAndSelfLoopIsBounded()
    {
        using var clock = new VirtualCameraClock(Utc());
        var order = new List<string>();
        clock.Schedule(0, () =>
        {
            order.Add("outer");
            clock.Schedule(clock.Timestamp, () => order.Add("inner"));
        });
        clock.AdvanceBy(TimeSpan.Zero);
        Assert.Equal(new[] { "outer", "inner" }, order);

        using var loop = new VirtualCameraClock(Utc());
        var count = 0;
        Action? callback = null;
        callback = () =>
        {
            count++;
            loop.Schedule(loop.Timestamp, callback!);
        };
        loop.Schedule(0, callback);

        var limit = Assert.Throws<InvalidOperationException>(() => loop.AdvanceBy(TimeSpan.Zero));
        Assert.Equal("VirtualCameraClockCallbackLimitExceeded", limit.Message);
        Assert.Equal(4096, count);
        Assert.Equal(0, loop.PendingEventCount);
    }

    [Fact]
    public async Task V116_T08_ConcurrentAdvanceAndUtcMutationAreRejectedWithoutGuessingOrder()
    {
        using var clock = new VirtualCameraClock(Utc());
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.Schedule(0, () =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        });

        var advancing = Task.Run(() => clock.AdvanceBy(TimeSpan.Zero));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var advanceBusy = Assert.Throws<InvalidOperationException>(() => clock.AdvanceBy(TimeSpan.Zero));
            Assert.Equal("VirtualCameraClockBusy", advanceBusy.Message);
            var utcBusy = Assert.Throws<InvalidOperationException>(() => clock.SetUtc(Utc().AddDays(1)));
            Assert.Equal("VirtualCameraClockBusy", utcBusy.Message);
        }
        finally
        {
            release.TrySetResult(true);
            await advancing.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void V116_T09_DisposeIsIdempotentAndStopsPendingCallbacks()
    {
        using var clock = new VirtualCameraClock(Utc());
        var called = false;
        clock.Schedule(10, () => called = true);

        clock.Dispose();
        clock.Dispose();

        Assert.Equal(0, clock.PendingEventCount);
        Assert.False(called);
        var disposed = Assert.Throws<InvalidOperationException>(() => clock.AdvanceBy(TimeSpan.Zero));
        Assert.Equal("VirtualCameraClockDisposed", disposed.Message);
        var scheduleDisposed = Assert.Throws<InvalidOperationException>(() =>
            clock.Schedule(10, static () => { }));
        Assert.Equal("VirtualCameraClockDisposed", scheduleDisposed.Message);
    }

    [Fact]
    public void V116_T10_ConcurrentScheduleCancellationReturnsCapacityExactlyOnce()
    {
        using var clock = new VirtualCameraClock(Utc());
        var called = 0;
        var handles = Enumerable.Range(0, 512).Select(_ =>
            clock.Schedule(1, () => Interlocked.Increment(ref called))).ToArray();
        Parallel.For(0, 4096, index => handles[index % handles.Length].Dispose());
        Assert.Equal(0, clock.PendingEventCount);
        clock.AdvanceTo(1);
        Assert.Equal(0, called);
        // The same bounded queue can be filled again after actual cancellation.
        foreach (var unused in handles) clock.Schedule(2, () => called++);
        clock.AdvanceTo(2);
        Assert.Equal(512, called);
    }

    [Fact]
    public void V116_T11_EveryAcceptedUtcCanBePublishedAsSharedFrameTimePoint()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualCameraClock(default));
        using var clock = new VirtualCameraClock(Utc().ToOffset(TimeSpan.FromHours(8)));
        Assert.Equal(Utc(), clock.GetTimePoint().HostObservedAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetUtc(default));
        Assert.Equal(Utc(), clock.GetTimePoint().HostObservedAtUtc);
        clock.SetUtc(DateTimeOffset.MinValue.AddTicks(1));
        Assert.Equal(DateTimeOffset.MinValue.AddTicks(1), clock.GetTimePoint().HostObservedAtUtc);
    }

    private static DateTimeOffset Utc() =>
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
}
