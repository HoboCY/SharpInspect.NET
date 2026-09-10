using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V137_R20_FirstRecoveryJournalFailureRetiresExistingResourcesWithoutNewLease()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            blockStimulus: true, allowDisposeFailure: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "journal failure start");
        await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "journal failure session did not become ready");
        var opens = harness.Facility.OpenCount;
        Assert.True(harness.Factory.Created > 0);
        Assert.Contains(harness.CameraProvider.GetDiagnostics().Devices, value => value.IsOpen);

        // Dispose only the durable writer after preparation, then revoke the
        // facility. The first recovery-attempt append is now deterministically
        // unavailable, while all old provider callbacks can still retire.
        await harness.Fixture.Store.DisposeAsync();
        harness.Facility.SignalLost();
        await harness.Factory.AlgorithmDisposeCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (harness.Facility.DisposeCount < opens && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(opens, harness.Facility.DisposeCount);
        Assert.Equal(opens, harness.Facility.OpenCount);
        Assert.Equal(0, harness.Facility.ReleaseIsolationCount);
        Assert.All(harness.CameraProvider.GetDiagnostics().Devices, value =>
        {
            Assert.False(value.IsOpen);
            Assert.Equal(0, value.OutstandingLeases);
        });
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
        Assert.Contains("StationQualificationRecoveryRequired", station.AdmissionBlockers);
    }
}
