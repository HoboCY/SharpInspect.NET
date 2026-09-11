using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V142_R11_ColdRestartBlocksAcceptedCoreWithoutReplayingPhysicalWork(bool appendFault)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();

        await PrepareProductionAsync(harness, issuer);
        var oldState = await harness.Runtime.GetSnapshotAsync();
        var productionOptions = harness.Service<ProductionInspectionOptions>();
        var profileHash = productionOptions.Profile.ContentHash;
        var policyHash = productionOptions.TracePolicySnapshotHash;
        var oldConnectionCount = peer.ConnectionCount;

        // Stop the live coordinator before writing the synthetic accepted cycle.
        // The cycle is still written through the real production store writer, so
        // the restart test observes the same durable boundary as a late process
        // exit after Core (and, optionally, after its fault fact).
        await harness.StopRuntimePreservingFixtureAsync();
        var admission = await BuildAdmissionAsync(harness);
        var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.True(admitted.Committed, admitted.ReasonCode);
        var durableAdmission = Assert.IsType<ProductionInspectionAdmission>(admitted.Admission);

        var committed = await harness.Fixture.Store.CommitProductionInspectionCoreAsync(
            new ProductionInspectionCoreWriteRequest(TimeoutCore(durableAdmission)),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.True(committed.Committed, committed.ReasonCode);
        var durableCore = Assert.IsType<ProductionInspectionCore>(committed.Core);
        Assert.Equal(durableAdmission.InspectionId, durableCore.Admission.InspectionId);

        if (appendFault)
        {
            var fault = await harness.Fixture.Store.AppendProductionInspectionEventAsync(
                new ProductionInspectionEventWriteRequest(durableAdmission.InspectionId,
                    ProductionInspectionEventKind.FaultTerminated, "V142ColdRestartFault",
                    DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
                new StoreDeadline(TimeSpan.FromSeconds(4)));
            Assert.True(fault.Committed, fault.ReasonCode);
        }

        var oldHistory = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(PageSize: 128));
        Assert.True(oldHistory.Available, oldHistory.ReasonCode);
        Assert.Contains(oldHistory.Events, value => value.InspectionId == durableAdmission.InspectionId &&
            value.Kind == ProductionInspectionEventKind.CoreCommitted &&
            value.Core!.ContentHash == durableCore.ContentHash);

        // Fixture.RestartStoreAsync closes the old writer and constructs a fresh
        // SqliteCommandStore with the exact same options/database. Keep the old
        // service composition alive until the cold runtime has been created; its
        // read-only activation/query collaborators are immutable and the new
        // runtime owns the reopened store.
        await harness.Fixture.RestartStoreAsync();

        var clock = new VirtualCameraClock(DateTimeOffset.UtcNow);
        await using var clockPump = ClockPump.Start(clock);
        var provider = ManualHarness.CreateProvider(clock, timeout: false);
        var restarted = CreateRestartedManualRuntime(harness, new[] { provider }, clock);
        restarted.ConfigureProductionInspections(productionOptions, harness.Fixture.Options,
            harness.Service<AlgorithmExecutionOptions>(), clock);
        try
        {
            await restarted.WaitForRecipeActivationStartupAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            await restarted.WaitForManualInspectionStartupAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));

            var coldState = await WaitForProductionRecoveryBlockerAsync(restarted);
            Assert.NotEqual(oldState.RuntimeEpoch, coldState.RuntimeEpoch);
            Assert.Equal(RecoveryState.Required, coldState.Recovery);
            Assert.Equal(ProductionArmState.Disarmed, coldState.ArmState);
            Assert.False(coldState.Ready);
            Assert.Contains("ProductionInspectionStartupRecoveryRequired",
                coldState.AdmissionBlockers);
            Assert.DoesNotContain("ProductionInspectionStartupDependencyUnavailable",
                coldState.AdmissionBlockers);
            Assert.DoesNotContain("ProductionInspectionHistoryUnavailable",
                coldState.AdmissionBlockers);

            var coldQuery = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
            var coldRead = await coldQuery.ReadAsync(durableAdmission.InspectionId);
            Assert.True(coldRead.Available, coldRead.ReasonCode);
            Assert.True(coldRead.RecoveryRequired);
            Assert.NotNull(coldRead.Latest);

            var coldPage = await coldQuery.QueryAsync(new(PageSize: 128));
            Assert.True(coldPage.Available, coldPage.ReasonCode);
            var events = coldPage.Events.Where(value =>
                value.InspectionId == durableAdmission.InspectionId).ToArray();
            Assert.Contains(events, value => value.Kind == ProductionInspectionEventKind.Admitted);
            var coldCore = Assert.Single(events, value =>
                value.Kind == ProductionInspectionEventKind.CoreCommitted);
            Assert.Equal(durableAdmission.InspectionId, coldCore.InspectionId);
            Assert.Equal(durableCore.ContentHash, coldCore.Core!.ContentHash);
            Assert.Equal(productionOptions.Profile.EndpointBindingHash,
                coldCore.Admission.EndpointBindingHash);
            Assert.Equal(profileHash, coldCore.Admission.PlcProfileHash);
            Assert.Equal(policyHash, coldCore.Admission.TracePolicySnapshot.ContentHash);
            if (appendFault)
                Assert.Contains(events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
            else
                Assert.DoesNotContain(events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);

            Assert.DoesNotContain(events, value => value.Kind is
                ProductionInspectionEventKind.PublicationPrepared or
                ProductionInspectionEventKind.ResultValidRaised or
                ProductionInspectionEventKind.ResultAcknowledged or
                ProductionInspectionEventKind.ResultValidCleared or
                ProductionInspectionEventKind.AcknowledgementReset or
                ProductionInspectionEventKind.RecoveryCompleted);

            var providerBeforeDispose = provider.GetDiagnostics().Devices.Single();
            Assert.Equal(0, providerBeforeDispose.OpenCount);
            Assert.Equal(0, providerBeforeDispose.FramesProduced);
            Assert.Equal(oldConnectionCount, peer.ConnectionCount);
        }
        finally
        {
            await restarted.DisposeAsync();
        }

        var providerAfterDispose = provider.GetDiagnostics().Devices.Single();
        Assert.Equal(0, providerAfterDispose.OpenCount);
        Assert.Equal(0, providerAfterDispose.FramesProduced);
        Assert.Equal(oldConnectionCount, peer.ConnectionCount);
    }

    private static async Task<StationStateSnapshot> WaitForProductionRecoveryBlockerAsync(
        StationRuntime runtime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        StationStateSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await runtime.GetSnapshotAsync();
            if (last.AdmissionBlockers.Contains("ProductionInspectionStartupRecoveryRequired"))
                return last;
            await Task.Delay(25);
        }

        throw new XunitException("Production cold restart did not reach the exact recovery blocker: " +
            last?.Recovery + "/" + last?.ArmState + "/" +
            string.Join(",", last?.AdmissionBlockers.AsEnumerable() ?? Array.Empty<string>()));
    }
}
