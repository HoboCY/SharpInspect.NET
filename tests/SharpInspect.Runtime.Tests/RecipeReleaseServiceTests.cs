using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeReleaseServiceTests
{
    [Fact]
    public async Task V130_R01_SingleApproverReleasesOwnDraftWithImmutableContentAndNoActivation()
    {
        await using var h = await Harness.CreateAsync();
        var before = await h.Runtime.GetSnapshotAsync();
        var command = await h.AuthorizeAsync(h.Command());
        var result = await h.Releases.ReleaseAsync(command);
        Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var recipe = Assert.IsType<ReleasedRecipe>(result.Recipe);
        Assert.True(recipe.Available);
        Assert.Equal(h.Source.Content.ContentHash, recipe.Reference.ContentHash);
        Assert.NotEqual(recipe.Reference.ContentHash, recipe.Record.ContentHash);
        Assert.Equal(h.Source.AuthorPrincipalId, recipe.Record.ApproverPrincipalId);
        Assert.Contains(h.Source.AuthorPrincipalId, recipe.Record.ContributingAuthors);
        Assert.Equal(command.CorrelationId, recipe.Record.OperationId);
        Assert.Equal(command.Invocation.StepUpGrantId, recipe.Record.StepUpGrantId);
        Assert.Contains(recipe.Record.Checks, value => value.GateId == "AlgorithmSemantic" && value.Passed);
        Assert.Equal(0, h.Factory.CreateCalls);
        var after = await h.Runtime.GetSnapshotAsync();
        Assert.Equal(before.ActiveRecipe, after.ActiveRecipe);
        Assert.Equal(before.ArmState, after.ArmState);
        Assert.False(after.Ready);
        await h.Storage.WaitForVerifiedAsync();
        var read = await new SqliteReleasedRecipeQuery(h.Storage.Options).ReadAsync(recipe.Reference);
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(recipe.Record.ContentHash, read.Recipe!.Record.ContentHash);
        var duplicate = await h.Releases.ReleaseAsync(command);
        Assert.Equal(CommandDisposition.Rejected, duplicate.Outcome.Disposition);
        Assert.Null(duplicate.Recipe);
        Assert.Single((await h.Releases.QueryAsync(new())).Recipes);
    }

    [Fact]
    public async Task V130_R02_ReleaseRequiresFreshGrantBoundToTheExactCorrelation()
    {
        await using var h = await Harness.CreateAsync();
        var noGrant = await h.Releases.ReleaseAsync(h.Command());
        Assert.Equal("StepUpRequired", noGrant.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, noGrant.Outcome.Audit);
        var authorized = await h.AuthorizeAsync(h.Command());
        var differentCorrelation = h.Command() with { Invocation = authorized.Invocation };
        Assert.Equal(authorized.AuthorizationTarget, differentCorrelation.AuthorizationTarget);
        var mismatch = await h.Releases.ReleaseAsync(differentCorrelation);
        Assert.Equal("StepUpInvalid", mismatch.Outcome.ReasonCode);
        Assert.Null(mismatch.Recipe);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
    }

    [Fact]
    public async Task V130_R03_MakerCheckerRejectsSamePersonAfterANewSession()
    {
        await using var h = await Harness.CreateAsync(RecipeGovernanceMode.MakerCheckerRelease);
        var previousSession = h.Storage.Sessions.Current.SessionId;
        await h.Storage.Sessions.LogoutAsync(previousSession);
        var signIn = await h.Storage.Sessions.SignInAsync(new(h.Storage.UserName, h.Password));
        Assert.True(signIn.Succeeded, signIn.ReasonCode);
        Assert.NotEqual(previousSession, signIn.Session.SessionId);
        Assert.Equal(h.Source.AuthorPrincipalId.ToString("D"), signIn.Session.PrincipalId);
        await h.Storage.WaitForVerifiedAsync();
        var result = await h.Releases.ReleaseAsync(await h.AuthorizeAsync(h.Command()));
        Assert.Equal("RecipeReleaseMakerCheckerConflict", result.Outcome.ReasonCode);
        Assert.Null(result.Recipe);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
    }

    [Fact]
    public async Task V130_R04_DifferentHumanCheckerCanReleaseThroughPublicRuntimeCommand()
    {
        await using var h = await Harness.CreateAsync(RecipeGovernanceMode.MakerCheckerRelease);
        var checker = await h.CreateAndSignInCheckerAsync();
        var command = await h.AuthorizeAsync(h.Command());
        var outcome = await h.Runtime.SubmitAsync(command);
        Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
        var page = await h.Releases.QueryAsync(new());
        Assert.True(page.Available, page.ReasonCode);
        var released = Assert.Single(page.Recipes);
        Assert.Equal(checker, released.Record.ApproverPrincipalId);
        Assert.DoesNotContain(checker, released.Record.ContributingAuthors);
        Assert.Contains(h.Source.AuthorPrincipalId, released.Record.ContributingAuthors);
        Assert.False((await h.Runtime.GetSnapshotAsync()).Ready);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V130_R05_EditDuringSemanticReviewInvalidatesTheExactApproval(bool noOp)
    {
        await using var h = await Harness.CreateAsync();
        var command = await h.AuthorizeAsync(h.Command());
        h.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var pending = h.Releases.ReleaseAsync(command).AsTask();
            await h.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var document = h.Storage.Document(noOp ? h.Source.Content.DisplayName : "Changed during review");
            var next = await h.Storage.SaveAsync(Guid.NewGuid(), h.Source.DraftId, h.Source.Revision,
                h.Source.RevisionContentHash, document, "new revision invalidates approval");
            Assert.True(next.Saved, next.ReasonCode);
            h.Factory.Hold.TrySetResult(true);
            var result = await pending;
            Assert.Equal("RecipeReleaseDraftRevisionConflict", result.Outcome.ReasonCode);
            Assert.Null(result.Recipe);
            Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
        }
        finally { h.Factory.Hold.TrySetResult(true); }
    }

    [Fact]
    public async Task V130_R06_SemanticAndMissingDependencyFailuresAreAuditedWithoutRelease()
    {
        await using var h = await Harness.CreateAsync();
        h.Factory.SemanticInvalid = true;
        var semantic = await h.Releases.ReleaseAsync(await h.AuthorizeAsync(h.Command()));
        Assert.Equal("RecipeDraftAlgorithmSemanticInvalid", semantic.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, semantic.Outcome.Audit);
        h.Factory.SemanticInvalid = false;
        var source = h.Source.Content;
        var withMissing = new RecipeDraftContent(source.RecipeKey, source.DisplayName, source.Algorithm,
            source.Configuration, source.CameraRole, source.Camera, source.AlgorithmExecutionTimeout,
            new[] { new RecipeAssetRequirement(RecipeAssetKind.AlgorithmModel, "Model", new("Absent.Model", "1", new string('A', 64))) },
            source.PolicyRequirements, source.ValueOrigins);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(withMissing, out var document, out var reason), reason);
        var saved = await h.Storage.SaveAsync(Guid.NewGuid(), h.Source.DraftId, h.Source.Revision,
            h.Source.RevisionContentHash, document!, "requires model");
        Assert.True(saved.Saved, saved.ReasonCode);
        h.Source = saved.Revision!;
        var dependency = await h.Releases.ReleaseAsync(await h.AuthorizeAsync(h.Command()));
        Assert.Equal("RecipeReleaseAssetAuthorityUnavailable", dependency.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, dependency.Outcome.Audit);
        Assert.Null(dependency.Recipe);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
    }

    [Fact]
    public async Task V130_R07_CancelledRequestIsAnAuditedRejection()
    {
        await using var h = await Harness.CreateAsync();
        var command = await h.AuthorizeAsync(h.Command());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await h.Releases.ReleaseAsync(command, cancellation.Token);
        Assert.Equal("RecipeReleaseCancelled", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        Assert.Null(result.Recipe);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
        await h.Storage.WaitForVerifiedAsync();
        var retry = await h.Releases.ReleaseAsync(command);
        Assert.Equal(CommandDisposition.Accepted, retry.Outcome.Disposition);
        Assert.Equal(command.Invocation.StepUpGrantId, retry.Recipe!.Record.StepUpGrantId);
    }

    [Fact]
    public async Task V130_R08_OperatorCannotReleaseOrObtainAReleaseGrant()
    {
        await using var h = await Harness.CreateAsync();
        await h.CreateAndSignInCheckerAsync(HumanRoleBundle.Operator);
        var access = await h.Releases.GetAccessAsync(h.Storage.Invocation());
        Assert.False(access.CanRelease);
        var command = h.Command();
        var stepUp = await h.Storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(), h.Storage.Invocation(),
            new(Permission.ReleaseRecipe, command.CorrelationId, command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe), h.Password));
        Assert.False(stepUp.Succeeded);
        var rejected = await h.Runtime.SubmitAsync(command);
        Assert.Equal("PermissionDenied", rejected.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
        Assert.Equal(0, h.Factory.ValidationCalls);
    }

    [Fact]
    public async Task V130_R09_TamperedAuditedDraftCannotProduceAReleasedRecipe()
    {
        await using var h = await Harness.CreateAsync();
        var command = await h.AuthorizeAsync(h.Command());
        await RecipeDraftStorageTests.TamperFirstDraftPayloadAndBindingAsync(h.Storage.Options.DatabasePath,
            h.Storage.Document("tampered after approval"));
        var rejected = await h.Releases.ReleaseAsync(command);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, rejected.Outcome.Audit);
        Assert.Null(rejected.Recipe);
        Assert.Equal(0, await h.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_release_events;"));
        Assert.False((await h.Releases.QueryAsync(new())).Available);
        Assert.False((await h.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V130_R10_NewReleasePreservesOldVersionAndColdQueryDoesNotResolveFactories()
    {
        await using var h = await Harness.CreateAsync();
        var first = await h.Releases.ReleaseAsync(await h.AuthorizeAsync(h.Command()));
        Assert.Equal(CommandDisposition.Accepted, first.Outcome.Disposition);
        var old = first.Recipe!;
        var next = await h.Storage.SaveAsync(Guid.NewGuid(), h.Source.DraftId, h.Source.Revision,
            h.Source.RevisionContentHash, h.Storage.Document("Second release"), "revision for new version");
        Assert.True(next.Saved, next.ReasonCode);
        h.Source = next.Revision!;
        var second = await h.Releases.ReleaseAsync(await h.AuthorizeAsync(h.Command()));
        Assert.Equal(CommandDisposition.Accepted, second.Outcome.Disposition);
        Assert.Equal(2, second.Recipe!.Record.RecipeVersion);
        Assert.NotEqual(old.Reference.ContentHash, second.Recipe.Reference.ContentHash);
        var services = new ServiceCollection();
        services.AddSingleton<IVisionAlgorithmFactory>(_ => throw new InvalidOperationException("HistoryMustNotResolveFactory"));
        services.AddSingleton<SqliteCommandStore>(_ => throw new InvalidOperationException("HistoryMustNotCreateWriter"));
        services.AddSingleton<IStationRuntime>(_ => throw new InvalidOperationException("HistoryMustNotStartRuntime"));
        services.AddSharpInspectSqliteRuntime(h.Storage.Options);
        await using var provider = services.BuildServiceProvider();
        var query = provider.GetRequiredService<IReleasedRecipeQuery>();
        var read = await query.ReadAsync(old.Reference);
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(old.Record.ContentHash, read.Recipe!.Record.ContentHash);
        Assert.Equal("1", read.Recipe.Reference.Version);
        Assert.Equal(2, (await query.QueryAsync(new())).Recipes.Count);
        Assert.Equal(0, h.Factory.CreateCalls);
    }

    [Fact]
    public async Task V130_R11_CommandCannotSelectAWeakerGovernancePolicy()
    {
        await using var h = await Harness.CreateAsync(RecipeGovernanceMode.MakerCheckerRelease);
        var selected = h.Storage.Options.RecipeReleases!.Policy;
        var weaker = new RecipeGovernancePolicy(selected.Id, selected.Version, RecipeGovernanceMode.SingleApproverRelease);
        var command = new ReleaseRecipeCommand(Guid.NewGuid(), h.Storage.Invocation(), h.Source.DraftId,
            h.Source.Revision, h.Source.RevisionContentHash, weaker.Reference, "cannot override deployment policy");
        var rejected = await h.Releases.ReleaseAsync(await h.AuthorizeAsync(command));
        Assert.Equal("RecipeReleaseGovernancePolicyMismatch", rejected.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Outcome.Audit);
        Assert.Null(rejected.Recipe);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
    }

    [Fact]
    public async Task V130_R12_DirectReleaseServiceAuditsRejectionAfterRuntimeStopped()
    {
        await using var h = await Harness.CreateAsync();
        await h.Runtime.DisposeAsync();
        Assert.Equal(RuntimeLifecycle.Stopped, (await h.Runtime.GetSnapshotAsync()).Lifecycle);
        var command = await h.AuthorizeAsync(h.Command());
        var rejected = await h.Releases.ReleaseAsync(command);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("RuntimeStopped", rejected.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Outcome.Audit);
        Assert.Null(rejected.Recipe);
        Assert.Empty((await h.Releases.QueryAsync(new())).Recipes);
    }

    [Fact]
    public async Task V130_R13_LegacyDraftDeploymentHasNoImplicitReleasePolicyOrGrant()
    {
        await using var storage = await RecipeDraftStorageTests.Fixture.CreateAsync();
        var access = await storage.Authorization.GetRecipeReleaseAccessAsync(storage.Invocation(), CancellationToken.None);
        Assert.False(access.CanRelease);
        Assert.Null(access.Policy);
        var operation = Guid.NewGuid();
        var stepUp = await storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(), storage.Invocation(),
            new(Permission.ReleaseRecipe, operation, new string('A', 64), AuditedCommandKind.ReleaseRecipe), storage.Password));
        Assert.False(stepUp.Succeeded);
        Assert.Equal("RecipeReleaseConfigurationRequired", stepUp.ReasonCode);
        await storage.WaitForVerifiedAsync();
        Assert.Equal(9, await storage.ScalarAsync("PRAGMA user_version;"));
        var saved = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            storage.Document("still a draft"), "old authoring deployment remains usable");
        Assert.True(saved.Saved, saved.ReasonCode);
        Assert.False((await new SqliteReleasedRecipeQuery(storage.Options).QueryAsync(new())).Available);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(RecipeDraftStorageTests.Fixture storage, RecipeDraftRevision source)
        {
            Storage = storage; Source = source; Password = storage.Password;
            Factory = new(source.Content);
            Drafts = new(new[] { Factory }, storage.Options, storage.Authorization, new SqliteRecipeDraftQuery(storage.Options));
            Runtime = new(storage.Store, TimeSpan.FromMilliseconds(500), storage.Sessions, storage.Authorization);
            Releases = new(Drafts, storage.Authorization, new SqliteReleasedRecipeQuery(storage.Options), storage.Options,
                () => Runtime.GetSnapshotAsync());
            Runtime.ConfigureRecipeReleaseService(Releases);
        }
        internal RecipeDraftStorageTests.Fixture Storage { get; }
        internal RecipeDraftRevision Source { get; set; }
        internal Factory Factory { get; }
        internal RecipeDraftService Drafts { get; }
        internal StationRuntime Runtime { get; }
        internal RecipeReleaseService Releases { get; }
        internal string Password { get; private set; }
        internal static async Task<Harness> CreateAsync(RecipeGovernanceMode mode = RecipeGovernanceMode.SingleApproverRelease)
        {
            var storage = await RecipeDraftStorageTests.Fixture.CreateAsync(recipeReleases:
                new RecipeReleaseStoreOptions(new("V130.Release.Policy", "1", mode)));
            var saved = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                storage.Document("Release candidate"), "new draft");
            Assert.True(saved.Saved, saved.ReasonCode);
            await storage.WaitForVerifiedAsync();
            return new(storage, saved.Revision!);
        }
        internal ReleaseRecipeCommand Command() => new(Guid.NewGuid(), Storage.Invocation(), Source.DraftId,
            Source.Revision, Source.RevisionContentHash, Storage.Options.RecipeReleases!.Policy.Reference, "reviewed complete recipe");
        internal async Task<ReleaseRecipeCommand> AuthorizeAsync(ReleaseRecipeCommand command)
        {
            var grant = await Storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(), Storage.Invocation(),
                new(Permission.ReleaseRecipe, command.CorrelationId, command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe), Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            await Storage.WaitForVerifiedAsync();
            return command with { Invocation = Storage.Invocation(grant.GrantId) };
        }
        internal async Task<Guid> CreateAndSignInCheckerAsync(HumanRoleBundle role = HumanRoleBundle.Technician)
        {
            var id = Guid.NewGuid(); var correlation = Guid.NewGuid();
            var grant = await Storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(), Storage.Invocation(),
                new(Permission.ManageAccounts, correlation, id.ToString("D"), AuditedCommandKind.CreateHumanAccount), Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            await Storage.WaitForVerifiedAsync();
            const string checkerPassword = "V130 different checker password 2026!";
            var created = await Runtime.SubmitAsync(new CreateHumanAccountCommand(correlation, Storage.Invocation(grant.GrantId),
                id, "release.checker", "Release Checker", checkerPassword, role));
            Assert.Equal(CommandDisposition.Accepted, created.Disposition);
            await Storage.WaitForVerifiedAsync();
            await Storage.Sessions.LogoutAsync(Storage.Sessions.Current.SessionId);
            var login = await Storage.Sessions.SignInAsync(new("release.checker", checkerPassword));
            Assert.True(login.Succeeded, login.ReasonCode);
            Password = checkerPassword;
            await Storage.WaitForVerifiedAsync();
            return id;
        }
        public async ValueTask DisposeAsync()
        { Factory.Hold?.TrySetResult(true); await Runtime.DisposeAsync(); await Drafts.DisposeAsync(); await Storage.DisposeAsync(); }
    }

    private sealed class Factory : IVisionAlgorithmFactory
    {
        internal Factory(RecipeDraftContent content) => Descriptor = new(content.Algorithm.Algorithm,
            content.Algorithm.ConfigurationSchema, new("V115.Draft.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
                new[] { "NoDefect" }, new("V115.Draft.Overlay", "1", 0, 64, 16)));
        public AlgorithmDescriptor Descriptor { get; }
        internal bool SemanticInvalid { get; set; }
        internal TaskCompletionSource<bool>? Hold { get; set; }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CreateCalls { get; private set; }
        internal int ValidationCalls { get; private set; }
        public async ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            ValidationCalls++;
            Entered.TrySetResult(true);
            if (Hold is { } hold) await hold.Task;
            return SemanticInvalid ? new[] { new AlgorithmValidationIssue("CrossFieldRejected", "Threshold") } : Array.Empty<AlgorithmValidationIssue>();
        }
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        { CreateCalls++; throw new InvalidOperationException("ReleaseMustNotCreateAlgorithm"); }
    }
}
