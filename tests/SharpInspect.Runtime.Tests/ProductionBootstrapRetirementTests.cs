using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    [Trait("VerificationId", "V152_R05")]
    public async Task V152_R05_GracefulBootstrapExitDrainsIssuedHeartbeatBeforeClosing()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.FreezeControllerHeartbeat = true;
        await using var harness = await QualificationHarness.CreateAsync(allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateCommunicationProfile(snapshot), enablePlcCommunication: true);
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var observedRequests = 0;
        var cancellationObserved = false;
        controller.BeforeHeartbeatResponse = async () =>
        {
            Assert.Equal(1, Interlocked.Increment(ref observedRequests));
            var owner = typeof(StationRuntime).GetField("_stationQualificationOwner", hidden)!.GetValue(runtime)!;
            var stimulus = (CancellationTokenSource)owner.GetType().GetProperty("StimulusCancellation", hidden)!.GetValue(owner)!;
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = stimulus.Token.Register(() => cancelled.TrySetResult());
            // Enter the real synchronous Runtime exit boundary while the first
            // heartbeat response is still withheld. R06 separately exercises
            // the public audited Exit command; this case fixes the IO interleave.
            typeof(StationRuntime).GetMethod("RequestStationQualificationStop", hidden)!.Invoke(runtime,
                new object[] { "V152ControlledBootstrapExit", false });
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            cancellationObserved = true;
        };
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "controlled bootstrap start");
        var terminal = await harness.WaitForSnapshotAsync(value => value.Phase is
            StationQualificationSessionPhase.Closed or StationQualificationSessionPhase.RecoveryBlocked,
            "controlled bootstrap terminal");
        Assert.True(cancellationObserved);
        Assert.Equal(1, observedRequests);
        Assert.True(terminal.Phase == StationQualificationSessionPhase.Closed,
            $"Normal bootstrap exit became {terminal.Phase}/{terminal.ReasonCode}");
        Assert.Equal(0, controller.ReadyWriteCount);
        Assert.Equal(0, controller.ResultValidHighCount);
        var history = await new SqlitePlcCommunicationHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        Assert.DoesNotContain(history.Events, value => value.Kind is PlcCommunicationEventKind.SynchronizationWindowObserved
            or PlcCommunicationEventKind.RecoveryCycleStarted);
        var requests = controller.RequestCount;
        controller.FreezeControllerHeartbeat = false;
        await Task.Delay(150);
        Assert.Equal(requests, controller.RequestCount);
    }
}

public sealed class PlcBootstrapRevocationTests
{
    [Fact]
    [Trait("VerificationId", "V152_P10")]
    public async Task V152_P10_RevokedBootstrapRequestIsNotReportedAsSynchronizationTimeout()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        var profile = controller.CreateCommunicationProfile();
        await using var channel = new ModbusQualificationChannel(profile,
            startOwnedRequest: _ => throw new PlcRequestRevokedException());
        await using var owner = new PlcCommunicationOwner(profile, Guid.NewGuid(), _ => { }, _ => { },
            _ => Task.CompletedTask);
        await channel.ConnectAsync();
        await Assert.ThrowsAsync<PlcRequestRevokedException>(() => owner.SynchronizeAsync(channel, CancellationToken.None));
        Assert.Null(owner.Failure);
        Assert.Equal(0, controller.RequestCount);
        // No bytes were sent, so the connection remains usable for an explicit
        // retirement-only clear; revocation must not masquerade as transport loss.
        await channel.WriteStateAsync(false, false, false, false, false);
        Assert.Single(controller.StateWrites);
        Assert.Equal(0, controller.ReadyWriteCount);
    }
}
