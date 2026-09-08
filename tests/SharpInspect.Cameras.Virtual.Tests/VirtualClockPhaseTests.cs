using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualClockPhaseTests
{
    [Fact]
    public void V118_V01_EqualTimestampUsesFrameObservationBeforeDeadlineRegardlessOfRegistrationOrder()
    {
        using var clock = new VirtualCameraClock(Utc());
        var ordered = new List<string>();
        var shared = (IFrameAcquisitionClock)clock;

        shared.Schedule(5, FrameAcquisitionClockPhase.Deadline,
            () => ordered.Add("deadline"));
        shared.Schedule(5, FrameAcquisitionClockPhase.FrameObservation,
            () => ordered.Add("frame"));

        clock.AdvanceTo(5);

        Assert.Equal(new[] { "frame", "deadline" }, ordered);
        Assert.Equal(5, clock.Timestamp);
    }

    [Fact]
    public void V118_V02_FrameObservationCannotBeRegisteredAfterDeadlinePhaseAtSameTimestamp()
    {
        using var clock = new VirtualCameraClock(Utc());
        var shared = (IFrameAcquisitionClock)clock;
        string? reason = null;
        shared.Schedule(5, FrameAcquisitionClockPhase.Deadline, () =>
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                shared.Schedule(clock.Timestamp, FrameAcquisitionClockPhase.FrameObservation,
                    static () => { }));
            reason = exception.Message;
        });

        clock.AdvanceTo(5);

        Assert.Equal("VirtualCameraClockPhaseClosed", reason);
        Assert.Equal(0, clock.PendingEventCount);
    }

    [Fact]
    public void V118_V03_ExplicitInterfaceExposesTheExistingMonotonicFrequency()
    {
        using var clock = new VirtualCameraClock(Utc());
        Assert.Equal(VirtualCameraClock.Frequency,
            ((IFrameAcquisitionClock)clock).Frequency);
    }

    [Fact]
    public void V118_V04_SamePhaseReentrantRegistrationRetainsStableSequenceOrder()
    {
        using var clock = new VirtualCameraClock(Utc());
        var shared = (IFrameAcquisitionClock)clock;
        var order = new List<string>();
        shared.Schedule(0, FrameAcquisitionClockPhase.FrameObservation, () =>
        {
            order.Add("first");
            shared.Schedule(clock.Timestamp, FrameAcquisitionClockPhase.FrameObservation,
                () => order.Add("nested"));
        });
        shared.Schedule(0, FrameAcquisitionClockPhase.FrameObservation,
            () => order.Add("second"));

        clock.AdvanceBy(TimeSpan.Zero);

        Assert.Equal(new[] { "first", "second", "nested" }, order);
    }

    [Fact]
    public void V118_V14_DeadlineWatermarkRejectsSameTimestampObservationAfterAdvance()
    {
        using var clock = new VirtualCameraClock(Utc());
        var shared = (IFrameAcquisitionClock)clock;
        var deadlineCalls = 0;
        var observationCalls = 0;
        shared.Schedule(5, FrameAcquisitionClockPhase.Deadline,
            () => deadlineCalls++);

        clock.AdvanceTo(5);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            shared.Schedule(5, FrameAcquisitionClockPhase.FrameObservation,
                () => observationCalls++));
        Assert.Equal("VirtualCameraClockPhaseClosed", exception.Message);
        Assert.Equal(1, deadlineCalls);
        Assert.Equal(0, observationCalls);
        Assert.Equal(0, clock.PendingEventCount);

        clock.AdvanceBy(TimeSpan.Zero);
        Assert.Equal(0, observationCalls);
    }

    private static DateTimeOffset Utc() =>
        new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
}
