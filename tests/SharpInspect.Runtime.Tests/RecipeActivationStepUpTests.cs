using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V132_G08_TightenedPolicyRequiresExactStepUpAndConsumesItAtSuccess()
    {
        var baseline = RecipeDraftTestPolicies.Authoring;
        var policy = new AuthorizationPolicy("v132-activation-stepup", "1",
            baseline.RoleBundles.ToDictionary(pair => pair.Key, pair => pair.Value.AsEnumerable()),
            baseline.StepUpPermissions.Append(Permission.ActivateRecipe));
        await using var harness = await ActivationHarness.CreateAsync(policy);
        var service = harness.CreateFixtureService();
        var access = await service.GetAccessAsync(harness.ActivationInvocation());
        Assert.True(access.CanActivate, access.ReasonCode);
        Assert.True(access.RequiresStepUp);
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;
        var missing = await service.ActivateAsync(harness.ActivationCommand());
        Assert.Equal("StepUpRequired", missing.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, missing.Outcome.Audit);
        Assert.Null(missing.Record!.AdmissionReference);
        Assert.Equal(beforeOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(beforeApply, harness.CameraProvider.ApplyCount);

        // The grant is bound to this exact activation target, not merely the station.
        var command = await harness.AuthorizedActivationCommand();
        var success = await service.ActivateAsync(command);
        Assert.True(success.Outcome.Disposition == CommandDisposition.Accepted, success.Outcome.ReasonCode);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, success.Record!.EvidenceKind);
        var afterOpen = harness.CameraProvider.OpenCount;
        var afterApply = harness.CameraProvider.ApplyCount;
        var reused = await service.ActivateAsync(command);
        Assert.Equal("StepUpInvalid", reused.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, reused.Outcome.Audit);
        Assert.Equal(afterOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(afterApply, harness.CameraProvider.ApplyCount);
        await harness.WaitForVerifiedAsync();
        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(success.Record.Reference, Assert.Single(page.Records, value => value.Outcome.Succeeded).Reference);
        Assert.Empty(page.PendingAdmissions!);
        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
    }
}
