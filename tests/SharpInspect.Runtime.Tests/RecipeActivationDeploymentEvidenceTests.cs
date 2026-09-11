using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused contract checks for the handoff between physical activation staging and
/// production deployment evidence. The integration harness exercises the full
/// callback path; these tests pin the pre-stage and development-fixture states.
/// </summary>
public sealed class RecipeActivationDeploymentEvidenceTests
{
    [Fact]
    public void LocalAuthorityChecksKeepDeploymentGatesUnexecutedBeforeEvidenceCapture()
    {
        var checks = new RecipeActivationChecks();

        Assert.All(checks.Snapshot().Where(value =>
                value.CheckId is "V132.A12" or "V132.A13" or "V132.A14" or "V132.A15" or
                "V132.A16" or "V132.A17" or "V132.A18"), value =>
            Assert.Equal(RecipeActivationCheckStatus.NotRun, value.Status));
    }

    [Fact]
    public void DevelopmentFixtureKeepsItsExplicitHistoricalAssumption()
    {
        var checks = new RecipeActivationChecks();
        checks.VerifyInstalledAuthorities(RecipeActivationInternalFixture.CreateForContractTests());

        Assert.All(checks.Snapshot().Where(value =>
                value.CheckId is "V132.A12" or "V132.A13" or "V132.A14" or "V132.A15" or
                "V132.A16" or "V132.A17" or "V132.A18"), value =>
        {
            Assert.Equal(RecipeActivationCheckStatus.Passed, value.Status);
            Assert.Equal("InternalContractFixtureAssumption", value.ReasonCode);
        });
    }
}

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V132_G12_LocalAuthorityDefersDeploymentFailureUntilStagingSnapshot()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;
        var service = new RecipeActivationService(harness.Drafts, harness.ReleaseHistory,
            harness.ContractHistory, harness.ActivationHistory, harness.Authorization, harness.Store,
            harness.Options, harness.Preparation, harness.PreparationOptions, harness.FramePool,
            (correlation, token) => harness.Runtime.ReserveRecipeActivationAsync(correlation, token),
            () => harness.Runtime.GetSnapshotAsync(), fixture: null,
            deploymentEvidence: (_, _) =>
                ValueTask.FromResult<RecipeActivationDeploymentEvidence?>(null));

        var result = await service.ActivateAsync(await harness.AuthorizedActivationCommand());

        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("ProductionDeploymentEvidenceUnavailable", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationEvidenceKind.LocalAuthority, record.EvidenceKind);
        Assert.True(harness.CameraProvider.OpenCount > beforeOpen);
        Assert.True(harness.CameraProvider.ApplyCount > beforeApply);
        Assert.All(Enumerable.Range(12, 7), number => Assert.Contains(record.Checks, check =>
            check.CheckId == $"V132.A{number:D2}" &&
            check.Status == RecipeActivationCheckStatus.Failed &&
            check.ReasonCode == "ProductionDeploymentEvidenceUnavailable"));
    }
}
