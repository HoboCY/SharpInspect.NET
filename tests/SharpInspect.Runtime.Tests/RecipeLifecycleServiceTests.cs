using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Harness = SharpInspect.Runtime.Tests.RecipeActivationServiceTests.ActivationHarness;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeLifecycleServiceTests
{
    [Fact]
    public async Task V148_B01_NonactiveRetirementPersistsRejectsActivationAndDerivesNewDraft()
    {
        await using var harness = await Harness.CreateAsync(LifecyclePolicy(), enableRecipeLifecycle: true);
        var release = harness.Released.Record;
        var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            release.Recipe, release.ReleaseId, release.ContentHash, null, "V148 retire preserved nonactive version");
        var grant = await harness.IssueGrantAsync(Permission.RetireRecipe, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.RetireReleasedRecipe);
        var result = await harness.RecipeLifecycle.RetireAsync(command with
            { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var retired = Assert.IsType<RecipeLifecycleRecord>(result.Record);
        Assert.Null(retired.ClearedActive);
        Assert.False(result.RuntimeRecoveryRequired);
        await harness.WaitForVerifiedAsync();
        var cold = new SqliteRecipeLifecycleQuery(harness.Options);
        var read = await cold.ReadReleaseAsync(release.Recipe, release.ReleaseId, release.ContentHash);
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(ReleasedRecipeLifecycleState.Retired, read.State);
        Assert.Equal(retired.Reference, read.Transition?.Reference);
        Assert.Equal(release.ContentHash, (await harness.ReleaseHistory.ReadAsync(release.Recipe)).Recipe?.Record.ContentHash);
        var activate = await harness.Activations.ActivateAsync(await harness.AuthorizedActivationCommand());
        Assert.Equal(CommandDisposition.Rejected, activate.Outcome.Disposition);
        Assert.Contains("RecipeRetired", activate.Outcome.ReasonCode, StringComparison.Ordinal);
        await harness.WaitForVerifiedAsync();
        Assert.Null((await harness.ActivationHistory.ReadCurrentAsync()).Record);

        var derived = await DeriveAsync(harness, retired);
        Assert.NotEqual(release.Source.DraftId, derived.DraftId);
        Assert.Equal(retired.Reference, derived.Content.LifecycleLineage?.Transition);
        Assert.Equal(release.Source.Content.Configuration.ContentHash, derived.Content.Configuration.ContentHash);
        Assert.Equal(ReleasedRecipeLifecycleState.Retired,
            (await cold.ReadReleaseAsync(release.Recipe, release.ReleaseId, release.ContentHash)).State);
        Assert.Single((await cold.QueryAsync(new())).Records);
    }

    [Fact]
    public async Task V148_B02_AbandonedDraftCannotBeEditedOrReleasedAndDerivationPreservesSource()
    {
        await using var harness = await Harness.CreateAsync(LifecyclePolicy(), enableRecipeLifecycle: true);
        var source = await SaveNewAsync(harness, harness.Source.Content);
        var command = new AbandonRecipeDraftCommand(Guid.NewGuid(), harness.ActivationInvocation(), source.DraftId,
            source.Revision, source.RevisionContentHash, "V148 abandon unused draft");
        var grant = await harness.IssueGrantAsync(Permission.AbandonRecipeDraft, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.AbandonRecipeDraft);
        var result = await harness.RecipeLifecycle.AbandonAsync(command with
            { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
        var transition = Assert.IsType<RecipeLifecycleRecord>(result.Record);
        await harness.WaitForVerifiedAsync();
        var read = await new SqliteRecipeLifecycleQuery(harness.Options).ReadDraftAsync(source.DraftId);
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(RecipeDraftLifecycleState.Abandoned, read.State);
        var edit = await harness.Drafts.SaveAsync(new(Guid.NewGuid(), source.DraftId, source.Revision,
            source.RevisionContentHash, source.Content, "V148 prohibited edit", harness.Invocation()));
        Assert.False(edit.Saved);
        Assert.Equal("RecipeDraftAbandoned", edit.ReasonCode);
        var release = new ReleaseRecipeCommand(Guid.NewGuid(), harness.Invocation(), source.DraftId, source.Revision,
            source.RevisionContentHash, harness.Options.RecipeReleases!.Policy.Reference, "V148 prohibited release");
        var releaseGrant = await harness.IssueGrantAsync(Permission.ReleaseRecipe, release.CorrelationId,
            release.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var rejected = await harness.Releases.ReleaseAsync(release with
            { Invocation = release.Invocation with { StepUpGrantId = releaseGrant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RecipeDraftAbandoned", rejected.Outcome.ReasonCode);
        await harness.WaitForVerifiedAsync();
        var derived = await DeriveAsync(harness, transition);
        Assert.Equal(source.RevisionContentHash, derived.Content.LifecycleLineage?.SourceDraft.RevisionContentHash);
        Assert.Equal(source.Content.ContentHash, (await harness.Drafts.ReadAsync(source.DraftId, source.Revision)).Revision?.Content.ContentHash);
        Assert.Equal(RecipeDraftLifecycleState.Open, (await harness.RecipeLifecycle.ReadDraftAsync(derived.DraftId)).State);
    }

    [Fact]
    public async Task V148_B03_MissingStepUpAndStaleReleaseLeaveLifecycleAndActiveUnchanged()
    {
        await using var harness = await Harness.CreateAsync(LifecyclePolicy(), enableRecipeLifecycle: true);
        var release = harness.Released.Record;
        var command = new RetireReleasedRecipeCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            release.Recipe, release.ReleaseId, release.ContentHash, null, "V148 missing exact authorization");
        var missing = await harness.RecipeLifecycle.RetireAsync(command);
        Assert.Equal(CommandDisposition.Rejected, missing.Outcome.Disposition);
        Assert.Equal("StepUpRequired", missing.Outcome.ReasonCode);
        Assert.Null(missing.Record);
        await harness.WaitForVerifiedAsync();
        Assert.Empty((await harness.RecipeLifecycle.QueryAsync(new())).Records);
        Assert.Equal(ReleasedRecipeLifecycleState.Available,
            (await harness.RecipeLifecycle.ReadReleaseAsync(release.Recipe, release.ReleaseId, release.ContentHash)).State);
        var stale = new RetireReleasedRecipeCommand(Guid.NewGuid(), command.Invocation, release.Recipe,
            Guid.NewGuid(), release.ContentHash, null, "V148 stale release identity");
        var grant = await harness.IssueGrantAsync(Permission.RetireRecipe, stale.CorrelationId,
            stale.AuthorizationTarget, AuditedCommandKind.RetireReleasedRecipe);
        var refused = await harness.RecipeLifecycle.RetireAsync(stale with
            { Invocation = stale.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, refused.Outcome.Disposition);
        Assert.Null(refused.Record);
        await harness.WaitForVerifiedAsync();
        Assert.Empty((await harness.RecipeLifecycle.QueryAsync(new())).Records);
        Assert.Null((await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
    }

    private static AuthorizationPolicy LifecyclePolicy()
    {
        var baseline = RecipeDraftTestPolicies.Authoring;
        return new("v148-lifecycle", "1", baseline.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key == HumanRoleBundle.Administrator
                ? pair.Value.Append(Permission.AbandonRecipeDraft).Distinct() : pair.Value.AsEnumerable()), baseline.StepUpPermissions);
    }

    private static async Task<RecipeDraftRevision> SaveNewAsync(Harness harness, RecipeDraftContent content)
    {
        var id = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var grant = await harness.IssueGrantAsync(Permission.EditRecipeDraft, operation,
            id.ToString("D"), AuditedCommandKind.SaveRecipeDraft);
        var saved = await harness.Drafts.SaveAsync(new(operation, id, 0, null, content, "V148 new unused draft",
            harness.Invocation() with { StepUpGrantId = grant.GrantId }, grant.GrantId));
        Assert.True(saved.Saved, saved.ReasonCode);
        await harness.WaitForVerifiedAsync();
        return Assert.IsType<RecipeDraftRevision>(saved.Revision);
    }

    private static async Task<RecipeDraftRevision> DeriveAsync(Harness harness, RecipeLifecycleRecord transition)
    {
        var id = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var grant = await harness.IssueGrantAsync(Permission.EditRecipeDraft, operation,
            id.ToString("D"), AuditedCommandKind.SaveRecipeDraft);
        var result = await harness.DraftDerivation.DeriveAsync(new(operation, id, transition.Reference,
            "V148 new identity from preserved origin", harness.Invocation() with { StepUpGrantId = grant.GrantId }));
        Assert.True(result.Saved, result.ReasonCode);
        await harness.WaitForVerifiedAsync();
        return Assert.IsType<RecipeDraftRevision>(result.Revision);
    }
}
