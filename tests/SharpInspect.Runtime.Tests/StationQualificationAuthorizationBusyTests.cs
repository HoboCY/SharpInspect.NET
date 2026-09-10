using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("release")]
    [InlineData("timeout")]
    [InlineData("abort")]
    public async Task V137_R22_AuthorityGateWaitsBoundedlyForActualSessionCommitLease(string ending)
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true, maximumRuns: 1);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "authorization busy start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.ReadyForStimulus, "qualification was not ready");
        var invocation = harness.Invocation();
        var station = Assert.IsType<StationRuntime>(harness.Runtime);
        station.RegisterAlarmSource(QualificationFaultAbortSource);
        var fault = new AlarmObservation(ready.RuntimeEpoch,
            station.NextAlarmObservationSequence(QualificationFaultAbortCode),
            QualificationFaultAbortCode, QualificationFaultAbortSource, false, DateTimeOffset.UtcNow);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var owner = typeof(StationRuntime).GetField("_stationQualificationOwner", instance)!.GetValue(harness.Runtime)!;
        var check = typeof(StationRuntime).GetMethod("RequireStationQualificationAuthorityAsync", instance)!;
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // The real lease uses Monitor: its acquisition and disposal must remain
        // on this synchronous thread, just as they do in the identity writer.
        var holder = Task.Run(() =>
        {
            Assert.True(harness.Fixture.Sessions.TryAcquireAuthorizationLease(
                Guid.Parse(invocation.PrincipalId!), invocation.SessionId!.Value,
                out var lease, out var reason), reason);
            using (lease)
            {
                acquired.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)), "test session lease was not released");
            }
        });
        Task? abort = null;
        try
        {
            Assert.True(await Task.Run(() => acquired.Wait(TimeSpan.FromSeconds(3))));
            // Invoke the same gate used before the next physical phase while
            // the real operation worker remains at the controlled stimulus.
            var authority = Assert.IsAssignableFrom<Task>(check.Invoke(harness.Runtime, new[] { owner }));
            var pause = Task.Delay(250);
            Assert.Same(pause, await Task.WhenAny(authority, pause));
            Assert.Equal(0, harness.Facility.StimulusCount);
            Assert.Equal(0, harness.Facility.WriteCount);
            if (ending == "release")
            {
                release.Set();
                await holder;
                await authority.WaitAsync(TimeSpan.FromSeconds(5));
                harness.Facility.ReleaseStimulus();
            }
            else
            {
                if (ending == "abort") abort = station.ObserveAlarmAsync(fault).AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    authority.WaitAsync(TimeSpan.FromSeconds(ending == "abort" ? 3 : 8)));
                Assert.Equal(ending == "timeout" ? "StationQualificationAuthorizationWaitTimeout" :
                    "StationQualificationFaultAbort", owner.GetType().GetProperty("ExitReason", instance)!.GetValue(owner));
            }
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            if (abort is not null) await abort.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var page = await harness.WaitForHistoryAsync(ready.SessionId!.Value,
            value => value.Events.LastOrDefault() is { Terminal: true }, "authorization wait did not restore");
        if (ending == "release")
            Assert.Equal(ExecutionStatus.Success, Assert.Single(page.Runs).ExecutionStatus);
        else
            Assert.Empty(page.Runs);
        Assert.Equal(StationQualificationRestorationState.Restored, page.Events[^1].Restoration);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
    }
}
