using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionInspectionAdmissionTests
{
    [Fact]
    public async Task V142_A01_QualificationProviderCannotInventTargetOrRuntimeGates()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        await using var runtime = new StationRuntime(new ProbeAuditWriter(),
            TimeSpan.FromMilliseconds(20), productionStoreOptions: new ProductionStoreOptions
            {
                ProductionAdmission = new ProductionAdmissionStoreOptions()
            });

        runtime.ConfigureProductionInspectionQualificationEvidenceProvider(
            new FixedQualificationEvidenceProvider(fixture));
        var first = await runtime.RefreshProductionAdmissionAsync();

        Assert.NotNull(first);
        Assert.NotEqual(ProductionAdmissionGateStatus.Passed,
            Assert.Single(first!.Gates, gate => gate.Gate == ProductionAdmissionGate.FrameworkQualification).Status);
        Assert.NotEqual(ProductionAdmissionGateStatus.Passed,
            Assert.Single(first.Gates, gate => gate.Gate == ProductionAdmissionGate.StationAcceptance).Status);
        Assert.NotEqual(ProductionAdmissionGateStatus.Passed,
            Assert.Single(first.Gates, gate => gate.Gate == ProductionAdmissionGate.CameraHealth).Status);
        Assert.DoesNotContain(first.Gates, gate =>
            (gate.Gate is ProductionAdmissionGate.PreparedAlgorithm or
                ProductionAdmissionGate.RecipeAssets or ProductionAdmissionGate.ProductionCycle or
                ProductionAdmissionGate.Backlog or ProductionAdmissionGate.EvidenceReconciliation) &&
            gate.Status == ProductionAdmissionGateStatus.Passed);

        await Task.Delay(80);
        var rebound = (await runtime.GetSnapshotAsync()).ProductionAdmission;
        Assert.NotNull(rebound);
        Assert.NotEqual(ProductionAdmissionGateStatus.Passed,
            Assert.Single(rebound!.Gates, gate => gate.Gate == ProductionAdmissionGate.FrameworkQualification).Status);
        Assert.NotEqual(ProductionAdmissionGateStatus.Passed,
            Assert.Single(rebound.Gates, gate => gate.Gate == ProductionAdmissionGate.CameraHealth).Status);
    }

    private sealed class FixedQualificationEvidenceProvider :
        IProductionInspectionQualificationEvidenceProvider
    {
        private readonly ProductionAdmissionTestFixture _fixture;

        internal FixedQualificationEvidenceProvider(ProductionAdmissionTestFixture fixture) =>
            _fixture = fixture;

        public ValueTask<ProductionQualificationInputs> CaptureAsync(
            ProductionConfiguration observedConfiguration, StationStateSnapshot state, RecipeActivationSnapshot? activation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_fixture.Inputs());
        }
    }
}
