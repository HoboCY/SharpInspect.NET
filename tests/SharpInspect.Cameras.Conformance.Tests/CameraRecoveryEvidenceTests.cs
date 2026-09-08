using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Cameras.Conformance.Tests;

public sealed class CameraRecoveryEvidenceTests
{
    [Theory]
    [InlineData("Complete", true)]
    [InlineData("RetryThenComplete", true)]
    [InlineData("MissingCompletion", false)]
    [InlineData("Overflow", false)]
    [InlineData("WrongEpoch", false)]
    [InlineData("WrongCycle", false)]
    [InlineData("SequenceGap", false)]
    [InlineData("TimeReversed", false)]
    [InlineData("WrongAttempt", false)]
    public void V122_A11_RecoveryRequiresCompleteContinuousPublicEventHistory(
        string mutation, bool expected)
    {
        var epoch = Guid.NewGuid();
        var cycle = Guid.NewGuid();
        var pageEpoch = mutation == "WrongEpoch" ? Guid.NewGuid() : epoch;
        var kinds = new List<CameraRecoveryEventKind>
        {
            CameraRecoveryEventKind.SourceDisconnected,
            CameraRecoveryEventKind.CycleStarted,
            CameraRecoveryEventKind.AttemptStarted
        };
        if (mutation == "RetryThenComplete")
        {
            kinds.Add(CameraRecoveryEventKind.AttemptFailed);
            kinds.Add(CameraRecoveryEventKind.AttemptStarted);
        }
        if (mutation != "MissingCompletion") kinds.Add(CameraRecoveryEventKind.CycleCompleted);
        var events = kinds.Select((kind, index) => new CameraRecoveryEvent(
            pageEpoch, index + 1 + (mutation == "SequenceGap" && index >= 2 ? 1 : 0),
            mutation == "WrongCycle" && index == 3 ? Guid.NewGuid() : cycle,
            index < 2 ? 0 : mutation == "WrongAttempt" ? 2 : index >= 4 ? 2 : 1,
            kind, new FrameTimePoint(DateTimeOffset.UnixEpoch,
                mutation == "TimeReversed" && index == 3 ? 0 : index), "RecoveryTestEvent"))
            .ToArray();
        var page = new CameraRecoveryEventPage(pageEpoch, 1, events[^1].Sequence,
            mutation == "Overflow", events);

        Assert.Equal(expected, CameraConformancePublicObserver.HasCompleteRecoveryEvents(
            epoch, 0, cycle, page));
    }
}
