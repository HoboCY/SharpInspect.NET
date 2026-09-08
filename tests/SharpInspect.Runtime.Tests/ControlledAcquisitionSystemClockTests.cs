using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ControlledAcquisitionSystemClockTests
{
    [Fact]
    public async Task V118_T01_RealSchedulerOrdersPhasesAndNeverRunsOnTheScheduleCaller()
    {
        using var clock = new SystemFrameAcquisitionClock();
        var observed = new ConcurrentQueue<string>();
        var threads = new ConcurrentQueue<int>();
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = Environment.CurrentManagedThreadId;
        var due = clock.GetTimePoint().MonotonicTimestamp + clock.Frequency;
        using var deadline = clock.Schedule(due, FrameAcquisitionClockPhase.Deadline, () =>
        {
            threads.Enqueue(Environment.CurrentManagedThreadId);
            observed.Enqueue("deadline");
            finished.TrySetResult(true);
        });
        using var frameA = clock.Schedule(due, FrameAcquisitionClockPhase.FrameObservation, () =>
        {
            threads.Enqueue(Environment.CurrentManagedThreadId);
            observed.Enqueue("frame-a");
        });
        using var frameB = clock.Schedule(due, FrameAcquisitionClockPhase.FrameObservation, () =>
        {
            threads.Enqueue(Environment.CurrentManagedThreadId);
            observed.Enqueue("frame-b");
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "frame-a", "frame-b", "deadline" }, observed);
        Assert.All(threads, thread => Assert.NotEqual(caller, thread));
        Assert.ThrowsAny<ArgumentException>(() => clock.Schedule(due,
            FrameAcquisitionClockPhase.FrameObservation, () => observed.Enqueue("too-late")));
        Assert.Equal(3, observed.Count);
    }

    [Fact]
    public void V118_T02_CancelledSchedulesReleaseCapacityAndDisposedClockRejectsRegistration()
    {
        using var clock = new SystemFrameAcquisitionClock();
        var due = clock.GetTimePoint().MonotonicTimestamp + 30 * clock.Frequency;
        var handles = new List<IDisposable>();
        try
        {
            for (var i = 0; i < 256; i++)
                handles.Add(clock.Schedule(due, FrameAcquisitionClockPhase.Deadline,
                    () => throw new InvalidOperationException("CancelledScheduleRan")));
            Assert.Throws<InvalidOperationException>(() => clock.Schedule(due,
                FrameAcquisitionClockPhase.Deadline, () => { }));
            handles[0].Dispose();
            using var replacement = clock.Schedule(due, FrameAcquisitionClockPhase.Deadline, () => { });
            clock.Dispose();
            Assert.Throws<InvalidOperationException>(() => clock.Schedule(due,
                FrameAcquisitionClockPhase.Deadline, () => { }));
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }
}
