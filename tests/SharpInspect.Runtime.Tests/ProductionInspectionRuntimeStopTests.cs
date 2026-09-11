using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_R05_LocalStopBarrierWinsOverTriggerWaitingForCommandGate()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();

        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready,
            "Production did not become ready before the local-stop barrier");
        await peer.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        var commandGate = GetCommandGate(runtime);
        Assert.True(await commandGate.WaitAsync(TimeSpan.FromSeconds(5)),
            "Could not establish the command-gate barrier");

        Task<RuntimeCommandOutcome>? stopTask = null;
        try
        {
            stopTask = runtime.SubmitAsync(new GracefulProductionStopCommand(
                Guid.NewGuid(), harness.Invocation())).AsTask();
            await WaitConditionAsync(() => ReadPendingLocalStops(runtime) == 1,
                "Local stop did not become pending behind the command gate");

            // This is a real controller edge while the local stop reservation is
            // pending. The peer sample proves the edge reached the observer; the
            // durable admission/final guard must still prevent physical work.
            var triggerSample = peer.WaitForControllerSampleAsync(61, 1);
            peer.RaiseTrigger(61, 1);
            await triggerSample.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            commandGate.Release();
        }

        var stop = await stopTask!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
        Assert.Equal(AuditPersistence.Persisted, stop.Audit);

        await WaitProductionAsync(harness, state => state.LastCommand is
            { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" } &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready &&
            state.Recovery == RecoveryState.None,
            "Local stop did not complete with a clean recovery state");
        peer.SetTrigger(false);

        var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        Assert.DoesNotContain(history.Events, value =>
            value.Kind is ProductionInspectionEventKind.Admitted or
                ProductionInspectionEventKind.CoreCommitted);
        Assert.Equal(0, peer.ResultValidHighCount);
        Assert.Equal(0, CountProductionPayloadWrites(peer));
        Assert.Equal(0, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
    }

    private static SemaphoreSlim GetCommandGate(StationRuntime runtime)
    {
        var field = typeof(StationRuntime).GetField("_commandGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<SemaphoreSlim>(field!.GetValue(runtime));
    }

    private static int ReadPendingLocalStops(StationRuntime runtime)
    {
        var field = typeof(StationRuntime).GetField("_pendingLocalStops",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<int>(field!.GetValue(runtime));
    }

    private static int CountProductionPayloadWrites(ModbusQualificationTestServer peer) =>
        peer.Writes.Count(value => value.Function == 0x10 && value.StartAddress < 100);
}
