using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;
using Harness = SharpInspect.Runtime.Tests.RecipeActivationServiceTests.ActivationHarness;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeLifecycleAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V148_I01_LifecyclePrecheckRequiresFreshExactStepUpWithoutConsumingIt(bool abandon)
    {
        var baseline = RecipeDraftTestPolicies.Authoring;
        var policy = new AuthorizationPolicy("v148-lifecycle-precheck", "1",
            baseline.RoleBundles.ToDictionary(pair => pair.Key, pair => pair.Key == HumanRoleBundle.Administrator
                ? pair.Value.Append(Permission.AbandonRecipeDraft) : pair.Value.AsEnumerable()),
            baseline.StepUpPermissions.Where(permission => permission is not (Permission.RetireRecipe or Permission.AbandonRecipeDraft)));
        await using var harness = await Harness.CreateAsync(policy);
        var invocation = harness.ActivationInvocation();
        var source = harness.Source;
        var release = harness.Released.Record;
        var correlation = Guid.NewGuid();
        RuntimeCommand Command(string reason, CommandInvocation actor) => abandon
            ? new AbandonRecipeDraftCommand(correlation, actor, source.DraftId, source.Revision,
                source.RevisionContentHash, reason)
            : new RetireReleasedRecipeCommand(correlation, actor, release.Recipe, release.ReleaseId,
                release.ContentHash, null, reason);
        var permission = abandon ? Permission.AbandonRecipeDraft : Permission.RetireRecipe;
        Assert.True(policy.RequiresStepUp(permission));
        var missing = Command("V148 exact lifecycle operation", invocation);
        Assert.Equal("StepUpRequired", await harness.Authorization.PrecheckRecipeLifecycleAsync(missing, CancellationToken.None));
        var fabricated = Command("V148 exact lifecycle operation", invocation with { StepUpGrantId = Guid.NewGuid() });
        Assert.Equal("StepUpInvalid", await harness.Authorization.PrecheckRecipeLifecycleAsync(fabricated, CancellationToken.None));
        var target = abandon ? ((AbandonRecipeDraftCommand)missing).AuthorizationTarget :
            ((RetireReleasedRecipeCommand)missing).AuthorizationTarget;
        var grant = await harness.IssueGrantAsync(permission, correlation, target,
            abandon ? AuditedCommandKind.AbandonRecipeDraft : AuditedCommandKind.RetireReleasedRecipe);
        Assert.True(grant.Succeeded, grant.ReasonCode);
        await harness.WaitForVerifiedAsync();
        var authorized = Command("V148 exact lifecycle operation", invocation with { StepUpGrantId = grant.GrantId });
        Assert.Null(await harness.Authorization.PrecheckRecipeLifecycleAsync(authorized, CancellationToken.None));
        // Preflight grants no lifecycle transition and does not consume its grant.
        Assert.Null(await harness.Authorization.PrecheckRecipeLifecycleAsync(authorized, CancellationToken.None));
        var changed = Command("A different explicit reason", invocation with { StepUpGrantId = grant.GrantId });
        Assert.Equal("StepUpInvalid", await harness.Authorization.PrecheckRecipeLifecycleAsync(changed, CancellationToken.None));
        Assert.Null((await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
        Assert.True((await harness.ReleaseHistory.ReadAsync(release.Recipe)).Available);
    }
}
