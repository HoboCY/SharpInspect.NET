using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ControlledAcquisitionContractTests
{
    [Fact]
    public async Task V118_C01_PendingAndBusyAreBoundToOneExactRequestAndCannotReopen()
    {
        var request = Request();
        var clock = new Clock();
        var control = new FrameAcquisitionControl(request, clock);
        Assert.False(control.AcknowledgePending(Request()));
        Assert.False(control.TryOpen(new(clock.GetTimePoint(), 100, clock.Frequency)));
        Assert.True(control.AcknowledgePending(request));
        Assert.False(control.AcknowledgePending(request));
        Assert.False(control.TryOpen(new(clock.GetTimePoint(), 100, clock.Frequency + 1)));
        var waiting = control.WaitForBusyAsync();
        Assert.False(waiting.IsCompleted);
        var start = new FrameAcquisitionStart(clock.GetTimePoint(), 100, clock.Frequency);
        Assert.True(control.TryOpen(start));
        Assert.Same(start, await waiting);
        Assert.True(control.IsBusy);
        control.Close();
        control.Close();
        Assert.True(control.IsClosed);
        Assert.False(control.IsBusy);
        Assert.False(control.TryOpen(start));
        Assert.Same(start, control.Start);
    }

    [Fact]
    public async Task V118_C02_ClosedUnopenedGateCancelsWaitWithoutGrantingARequest()
    {
        var request = Request();
        var control = new FrameAcquisitionControl(request, new Clock());
        var waiting = control.WaitForBusyAsync();
        control.Close();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.PendingInstalled);
        Assert.False(control.AcknowledgePending(request));
        Assert.Null(control.Start);
    }

    [Fact]
    public void V118_C03_ProtocolPagesAreImmutableOrderedAndBounded()
    {
        var first = Observation(2);
        var values = new[] { first, Observation(3) };
        var snapshot = new CameraProtocolSnapshot(Guid.NewGuid(), 2, 10, true, values);
        values[0] = Observation(8);
        Assert.Same(first, snapshot.Observations[0]);
        Assert.Equal(10, snapshot.ThroughSequence);
        Assert.Equal(3, snapshot.Observations[^1].Sequence);
        Assert.True(snapshot.Overflowed);
        Assert.Throws<ArgumentException>(() => new CameraProtocolSnapshot(Guid.NewGuid(), 2, 10, false,
            new[] { Observation(3), Observation(2) }));
        Assert.Throws<ArgumentException>(() => new CameraProtocolSnapshot(Guid.NewGuid(), 2, 10, false,
            new[] { Observation(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CameraProtocolSnapshot(Guid.NewGuid(), 1,
            long.MaxValue, false, Array.Empty<CameraProtocolObservation>()));
        Assert.Throws<ArgumentNullException>(() => new CameraProtocolSnapshot(Guid.NewGuid(), 1, 0, false, null!));
        Assert.Throws<ArgumentException>(() => new CameraProtocolSnapshot(Guid.NewGuid(), 1, 65, false,
            Enumerable.Range(1, 65).Select(i => Observation(i))));
    }

    private static FrameAcquisitionRequest Request() => new(
        new ExecutionCorrelationId(ExecutionKind.Qualification, Guid.NewGuid()), "Primary");
    private static CameraProtocolObservation Observation(long sequence) => new(sequence,
        CameraProtocolViolationKind.EarlyFrame, "CameraEarlyFrame", new(DateTimeOffset.UtcNow, sequence));
    private sealed class Clock : IFrameAcquisitionClock
    {
        public long Frequency => 1000;
        public FrameTimePoint GetTimePoint() => new(DateTimeOffset.UtcNow, 1);
        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase, Action callback) =>
            throw new NotSupportedException();
    }
}
