using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class ProductionAdmissionViewModelTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    [Trait("VerificationId", "V136_U01")]
    public void FreshSnapshotExposesCompleteReadOnlyAdmissionReport()
    {
        var report = CreateReport();
        var viewModel = new StationStateViewModel();
        viewModel.SetSnapshot(CreateSnapshot(report.RuntimeEpoch) with { ProductionAdmission = report }, fresh: true);

        Assert.Same(report, viewModel.ProductionAdmission);
        Assert.Same(report, viewModel.DisplayedProductionAdmission);
        Assert.Equal(ProductionAdmissionReport.RequiredGates.Count,
            viewModel.DisplayedProductionAdmission!.Gates.Count);
        Assert.True(viewModel.DisplayedProductionAdmission.CanArm);
    }

    [Fact]
    [Trait("VerificationId", "V136_U02")]
    public void StaleSnapshotRetainsRawEvidenceButHidesCurrentAdmissionClaim()
    {
        var report = CreateReport();
        var viewModel = new StationStateViewModel();
        viewModel.SetSnapshot(CreateSnapshot(report.RuntimeEpoch) with { ProductionAdmission = report }, fresh: true);

        viewModel.SetUnknown();

        Assert.Same(report, viewModel.ProductionAdmission);
        Assert.Null(viewModel.DisplayedProductionAdmission);
        Assert.False(viewModel.Ready);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("VerificationId", "V136_U03")]
    public void FreshOuterSnapshotCannotPromoteReportFromDifferentEpochOrRevision(bool differentEpoch)
    {
        var report = CreateReport();
        var viewModel = new StationStateViewModel();
        var snapshot = CreateSnapshot(differentEpoch ? Guid.NewGuid() : report.RuntimeEpoch);
        if (!differentEpoch) snapshot = snapshot with { Revision = snapshot.Revision + 1 };
        viewModel.SetSnapshot(snapshot with { ProductionAdmission = report }, fresh: true);
        Assert.Same(report, viewModel.ProductionAdmission);
        Assert.Null(viewModel.DisplayedProductionAdmission);
    }

    private static ProductionAdmissionReport CreateReport()
    {
        var rows = ProductionAdmissionReport.RequiredGates.Select(gate =>
            new ProductionAdmissionGateResult(gate,
                gate == ProductionAdmissionGate.PowerLossQualification
                    ? ProductionAdmissionGateStatus.NotApplicable
                    : ProductionAdmissionGateStatus.Passed,
                gate == ProductionAdmissionGate.PowerLossQualification
                    ? "PowerLossQualificationNotApplicable" : "GatePassed",
                Hash, Hash, Hash)).ToArray();
        return new ProductionAdmissionReport(Guid.NewGuid(), 3, 2, DateTimeOffset.UtcNow,
            Hash, Hash, Hash, Hash, Hash, rows);
    }

    private static StationStateSnapshot CreateSnapshot(Guid epoch) =>
        new(epoch, 3, DateTimeOffset.UtcNow, RuntimeLifecycle.Running,
            ExclusiveMode.None, ProductionArmState.Disarmed, false, false, HandshakePhase.Idle,
            RecoveryState.None, null, null,
            new CameraHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new PlcHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new SubsystemHealth(HealthState.Healthy, "Ready"), new EvidenceHealth(HealthState.Healthy, 0, 0),
            new QualificationState(QualificationMatch.Matches, QualificationMatch.Matches,
                QualificationMatch.Matches, QualificationMatch.Matches),
            new PerformanceHealth(HealthState.Healthy, false), new AlarmSummary(0, 0, false),
            new InteractiveSession(InteractiveSessionState.Authenticated, "operator", Guid.NewGuid()),
            null, new AdmissionBlockers(Array.Empty<string>()));
}
