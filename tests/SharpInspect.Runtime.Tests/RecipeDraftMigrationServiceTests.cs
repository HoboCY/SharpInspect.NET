using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeDraftMigrationServiceTests
{
    [Fact]
    public async Task V148_M01_AbandonedHistoricalSourceMigratesIntoNewDraftWithBothLineages()
    {
        await using var fixture = await Harness.CreateAsync(enableRecipeLifecycle: true);
        var storage = fixture.Storage;
        var source = fixture.Source;
        var abandon = new AbandonRecipeDraftCommand(Guid.NewGuid(), storage.Invocation(), source.DraftId,
            source.Revision, source.RevisionContentHash, "Preserve old schema as abandoned history");
        var grant = await storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(), storage.Invocation(),
            new(Permission.AbandonRecipeDraft, abandon.CorrelationId, abandon.AuthorizationTarget,
                AuditedCommandKind.AbandonRecipeDraft), storage.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        var abandoned = await storage.Authorization.ApplyRecipeLifecycleAsync(abandon with
            { Invocation = storage.Invocation(grant.GrantId) }, Guid.NewGuid(), null, null, null,
            new StoreDeadline(storage.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, abandoned.Outcome.Disposition);
        var migrated = await fixture.Service.MigrateAsync(fixture.Request());
        Assert.True(migrated.Created, migrated.ReasonCode);
        var target = Assert.IsType<RecipeDraftRevision>(migrated.Revision);
        Assert.NotEqual(source.DraftId, target.DraftId);
        Assert.Equal(abandoned.Record!.Reference, target.Content.LifecycleLineage?.Transition);
        Assert.Equal(source.RevisionContentHash, target.Content.MigrationLineage?.Plan.Source.RevisionContentHash);
        Assert.Equal(fixture.Factory.Descriptor.Identity, target.Content.Algorithm.Algorithm);
        Assert.Equal(0, fixture.Factory.CreateCalls);
        await storage.WaitForVerifiedAsync();
        var cold = await new SqliteRecipeDraftQuery(storage.Options).ReadAsync(target.DraftId, 1);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Equal(target.RevisionContentHash, cold.Revision?.RevisionContentHash);
        Assert.Equal(RecipeDraftLifecycleState.Abandoned,
            (await new SqliteRecipeLifecycleQuery(storage.Options).ReadDraftAsync(source.DraftId)).State);
    }

    [Fact]
    public async Task V128_R01_ExplicitHistoricalSourceCreatesCompleteDraftAndExactReplayDoesNotTransformAgain()
    {
        await using var fixture = await Harness.CreateAsync();
        var request = fixture.Request();
        var source = fixture.Source;
        var result = await fixture.Service.MigrateAsync(request);
        Assert.True(result.Created, result.ReasonCode);
        var target = Assert.IsType<RecipeDraftRevision>(result.Revision);
        Assert.Equal(request.Plan.TargetDraftId, target.DraftId);
        Assert.Equal(1, target.Revision);
        Assert.Null(target.PreviousRevisionContentHash);
        Assert.Equal(source.Content.RecipeKey, target.Content.RecipeKey);
        Assert.Equal(source.Content.DisplayName, target.Content.DisplayName);
        Assert.Equal(source.Content.Camera.ExposureTimeUs, target.Content.Camera.ExposureTimeUs);
        Assert.Equal(source.Content.CameraRole, target.Content.CameraRole);
        Assert.Equal(source.Content.AlgorithmExecutionTimeout, target.Content.AlgorithmExecutionTimeout);
        Assert.Equal(source.Content.PolicyRequirements, target.Content.PolicyRequirements);
        Assert.All(target.Content.ValueOrigins, item => Assert.Equal(RecipeDraftValueOrigin.Explicit, item.Origin));
        var lineage = Assert.IsType<RecipeDraftMigrationLineage>(target.Content.MigrationLineage);
        Assert.Equal(source.RevisionContentHash, lineage.Plan.Source.RevisionContentHash);
        Assert.Equal(source.Content.Configuration.ContentHash, lineage.InputConfigurationContentHash);
        Assert.Equal(target.Content.Configuration.ContentHash, lineage.InitialOutputConfigurationContentHash);
        Assert.Equal(source.AuthorPrincipalId, target.AuthorPrincipalId);
        Assert.False(lineage.ApprovalInherited);
        Assert.False(target.Active); Assert.False(target.Published); Assert.False(target.CanRelease);
        Assert.Equal("NotRun", target.DependencyValidation);
        Assert.Equal(0, fixture.Factory.CreateCalls);
        Assert.Equal(1, fixture.Migrator.Calls);
        await fixture.Storage.WaitForVerifiedAsync();

        var replay = await fixture.Service.MigrateAsync(request);
        Assert.True(replay.Created, replay.ReasonCode);
        Assert.Equal(target.RevisionContentHash, replay.Revision!.RevisionContentHash);
        Assert.Equal(1, fixture.Migrator.Calls);
        Assert.Equal(2, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        var old = await fixture.Query.ReadAsync(source.DraftId, source.Revision);
        Assert.True(old.Available, old.ReasonCode);
        Assert.Equal(source.RevisionContentHash, old.Revision!.RevisionContentHash);
        Assert.Equal(source.Content.ContentHash, old.Revision.Content.ContentHash);
        Assert.Equal(source.AuthorPrincipalId, old.Revision.AuthorPrincipalId);
        await fixture.Storage.RestartStoreAsync();
        var cold = await new SqliteRecipeDraftQuery(fixture.Storage.Options).ReadAsync(target.DraftId, 1);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Equal(lineage.ContentHash, cold.Revision!.Content.MigrationLineage!.ContentHash);
        Assert.Equal(target.RevisionContentHash, cold.Revision.RevisionContentHash);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("migrator")]
    [InlineData("target-schema")]
    [InlineData("missing")]
    [InlineData("unit")]
    [InlineData("range")]
    [InlineData("type")]
    [InlineData("semantic")]
    public async Task V128_R02_InvalidSelectionOrOutputLeavesOnlyOriginalRevision(string scenario)
    {
        await using var fixture = await Harness.CreateAsync();
        var request = fixture.Request();
        var plan = request.Plan;
        if (scenario == "source") plan = new(plan.OperationId,
            new(plan.Source.DraftId, plan.Source.Revision, new string('A', 64)), plan.TargetDraftId,
            plan.TargetAlgorithm, plan.TargetSchema, plan.Migrator, plan.ChangeReason);
        if (scenario == "migrator") plan = new(plan.OperationId, plan.Source, plan.TargetDraftId,
            plan.TargetAlgorithm, plan.TargetSchema, new("Missing.Migrator", "1", new string('B', 64)), plan.ChangeReason);
        if (scenario == "target-schema") plan = new(plan.OperationId, plan.Source, plan.TargetDraftId,
            plan.TargetAlgorithm, new(plan.TargetSchema.Id, "wrong", plan.TargetSchema.ContentHash), plan.Migrator, plan.ChangeReason);
        if (scenario == "missing") fixture.Migrator.Output = Array.Empty<AlgorithmConfigurationEntry>();
        if (scenario == "unit") fixture.Migrator.Output = new[] { new AlgorithmConfigurationEntry("Limit", "mm", AlgorithmScalarValue.FromInt64(5)) };
        if (scenario == "range") fixture.Migrator.Output = new[] { new AlgorithmConfigurationEntry("Limit", "items", AlgorithmScalarValue.FromInt64(101)) };
        if (scenario == "type") fixture.Migrator.Output = new[] { new AlgorithmConfigurationEntry("Limit", "items", AlgorithmScalarValue.FromString("5")) };
        if (scenario == "semantic") fixture.Factory.SemanticInvalid = true;
        var result = await fixture.Service.MigrateAsync(new(plan, request.Invocation));
        Assert.False(result.Created);
        Assert.Null(result.Revision);
        Assert.Equal(1, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(0, fixture.Factory.CreateCalls);
        var old = await fixture.Query.ReadAsync(fixture.Source.DraftId, fixture.Source.Revision);
        Assert.True(old.Available, old.ReasonCode);
        Assert.Equal(fixture.Source.RevisionContentHash, old.Revision!.RevisionContentHash);
    }

    [Fact]
    public async Task V128_R03_CancellationDuringTransformCannotPublishLateOutput()
    {
        await using var fixture = await Harness.CreateAsync();
        fixture.Migrator.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Service.MigrateAsync(fixture.Request(), cancellation.Token).AsTask();
        await fixture.Migrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Created);
        fixture.Migrator.Hold.TrySetResult(true);
        await fixture.Migrator.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        var old = await fixture.Query.ReadAsync(fixture.Source.DraftId, 1);
        Assert.Equal(fixture.Source.RevisionContentHash, old.Revision!.RevisionContentHash);
    }

    [Fact]
    public async Task V128_R04_StepUpBindsCompleteMigrationIntentAndCannotBeRetargeted()
    {
        await using var fixture = await Harness.CreateAsync(requireStepUp: true);
        var request = fixture.Request();
        var plan = request.Plan;
        var issued = await fixture.Storage.Authorization.ReauthenticateAsync(new(plan.OperationId,
            request.Invocation, new(Permission.EditRecipeDraft, plan.OperationId, plan.ContentHash,
                AuditedCommandKind.MigrateAlgorithmConfiguration), fixture.Storage.Password));
        Assert.True(issued.Succeeded, issued.ReasonCode);
        await fixture.Storage.WaitForVerifiedAsync();
        var changed = new RecipeDraftMigrationPlan(plan.OperationId, plan.Source, plan.TargetDraftId,
            plan.TargetAlgorithm, plan.TargetSchema, plan.Migrator, "different reason after grant");
        var rejected = await fixture.Service.MigrateAsync(new(changed, fixture.Storage.Invocation(issued.GrantId), issued.GrantId));
        Assert.False(rejected.Created);
        Assert.Equal("StepUpInvalid", rejected.ReasonCode);
        await fixture.Storage.WaitForVerifiedAsync();
        var accepted = await fixture.Service.MigrateAsync(new(plan, fixture.Storage.Invocation(issued.GrantId), issued.GrantId));
        Assert.True(accepted.Created, accepted.ReasonCode);
        Assert.Equal(2, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        await fixture.Storage.WaitForVerifiedAsync();
        var calls = fixture.Migrator.Calls;
        var replay = await fixture.Service.MigrateAsync(new(plan,
            fixture.Storage.Invocation(issued.GrantId), issued.GrantId));
        Assert.True(replay.Created, replay.ReasonCode);
        Assert.Equal(accepted.Revision!.RevisionContentHash, replay.Revision!.RevisionContentHash);
        Assert.Equal(calls, fixture.Migrator.Calls);
        Assert.Equal(2, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V128_R05_OrdinaryEditingRetainsLineageAndCannotRemoveOrCopyItToAnotherDraft()
    {
        await using var fixture = await Harness.CreateAsync();
        var migrated = await fixture.Service.MigrateAsync(fixture.Request());
        Assert.True(migrated.Created, migrated.ReasonCode);
        await fixture.Storage.WaitForVerifiedAsync();
        var first = migrated.Revision!;
        var content = first.Content;
        var stripped = new RecipeDraftContent(content.RecipeKey, "Edited", content.Algorithm, content.Configuration,
            content.CameraRole, content.Camera, content.AlgorithmExecutionTimeout, content.AssetRequirements,
            content.PolicyRequirements, content.ValueOrigins, content.CameraProviderExtension, content.CalibrationRequirements);
        var reject = await fixture.Drafts.SaveAsync(new(Guid.NewGuid(), first.DraftId, 1,
            first.RevisionContentHash, stripped, "remove lineage", fixture.Storage.Invocation()));
        Assert.False(reject.Saved);
        var clone = await fixture.Drafts.SaveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            content, "copy lineage", fixture.Storage.Invocation()));
        Assert.False(clone.Saved);
        var edited = new RecipeDraftContent(content.MigrationLineage, content.RecipeKey, "Edited", content.Algorithm,
            content.Configuration, content.CameraRole, content.Camera, content.AlgorithmExecutionTimeout,
            content.AssetRequirements, content.PolicyRequirements, content.ValueOrigins,
            content.CameraProviderExtension, content.CalibrationRequirements);
        var saved = await fixture.Drafts.SaveAsync(new(Guid.NewGuid(), first.DraftId, 1,
            first.RevisionContentHash, edited, "ordinary revision preserves origin", fixture.Storage.Invocation()));
        Assert.True(saved.Saved, saved.ReasonCode);
        await fixture.Storage.WaitForVerifiedAsync();
        Assert.Equal(content.MigrationLineage!.ContentHash, saved.Revision!.Content.MigrationLineage!.ContentHash);
        Assert.Equal(3, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.False(saved.Revision.CanRelease);
    }

    [Fact]
    public async Task V128_R06_DamagedPersistedSourceFailsBeforeTransformerAndCreatesNoTarget()
    {
        await using var fixture = await Harness.CreateAsync();
        await RecipeDraftStorageTests.TamperFirstDraftPayloadAndBindingAsync(fixture.Storage.Options.DatabasePath,
            fixture.Storage.Document("Tampered source payload with recomputed payload hash"));
        var read = await fixture.Query.ReadAsync(fixture.Source.DraftId, 1);
        Assert.False(read.Available);
        var result = await fixture.Service.MigrateAsync(fixture.Request());
        Assert.False(result.Created);
        Assert.Null(result.Revision);
        Assert.Equal(0, fixture.Migrator.Calls);
        Assert.Equal(1, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V128_R07_SelectedHistoricalRevisionIsNotSilentlyReplacedByTheLatestDraft()
    {
        await using var fixture = await Harness.CreateAsync();
        var latest = await fixture.Storage.SaveAsync(Guid.NewGuid(), fixture.Source.DraftId, 1,
            fixture.Source.RevisionContentHash, fixture.Storage.Document("Later source revision"), "later authoring");
        Assert.True(latest.Saved, latest.ReasonCode);
        await fixture.Storage.WaitForVerifiedAsync();
        var migrated = await fixture.Service.MigrateAsync(fixture.Request());
        Assert.True(migrated.Created, migrated.ReasonCode);
        Assert.Equal(fixture.Source.Content.DisplayName, migrated.Revision!.Content.DisplayName);
        Assert.NotEqual(latest.Revision!.Content.DisplayName, migrated.Revision.Content.DisplayName);
        Assert.Equal(1, migrated.Revision.Content.MigrationLineage!.Plan.Source.Revision);
        Assert.Equal(fixture.Source.RevisionContentHash,
            migrated.Revision.Content.MigrationLineage.Plan.Source.RevisionContentHash);
        Assert.Equal(3, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V128_R08_ConcurrentExactPlanWaitsForCommitWithoutRunningTransformerAgain()
    {
        await using var fixture = await Harness.CreateAsync();
        fixture.Factory.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = fixture.Request();
        var first = fixture.Service.MigrateAsync(request).AsTask();
        await fixture.Factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = fixture.Service.MigrateAsync(request).AsTask();
        try
        {
            var changed = new RecipeDraftMigrationPlan(request.Plan.OperationId, request.Plan.Source,
                request.Plan.TargetDraftId, request.Plan.TargetAlgorithm, request.Plan.TargetSchema,
                request.Plan.Migrator, "conflicting concurrent intent");
            var conflict = await fixture.Service.MigrateAsync(new(changed, request.Invocation)).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(conflict.Created);
            Assert.Equal("RecipeDraftMigrationOperationConflict", conflict.ReasonCode);
            var observation = await Task.WhenAny(fixture.Migrator.SecondEntered.Task, Task.Delay(150));
            Assert.NotSame(fixture.Migrator.SecondEntered.Task, observation);
            Assert.False(duplicate.IsCompleted);
        }
        finally { fixture.Factory.Hold.TrySetResult(true); }
        var results = await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.True(result.Created, result.ReasonCode));
        Assert.Equal(results[0].Revision!.RevisionContentHash, results[1].Revision!.RevisionContentHash);
        Assert.Equal(1, fixture.Migrator.Calls);
        Assert.Equal(2, await fixture.Storage.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(RecipeDraftStorageTests.Fixture storage, RecipeDraftRevision source)
        {
            Storage = storage; Source = source;
            Query = new(storage.Options);
            Factory = new();
            Migrator = new(source, Factory.Descriptor);
            // The source factory is deliberately absent. Its immutable embedded schema is sufficient to migrate.
            Drafts = new(new[] { Factory }, storage.Options, storage.Authorization, Query);
            Registry = new(new[] { Migrator }, TimeSpan.FromSeconds(8));
            Service = new(Drafts, storage.Authorization, Registry, storage.Options,
                storage.Options.RecipeLifecycle is null ? null : new SqliteRecipeLifecycleQuery(storage.Options));
        }
        internal RecipeDraftStorageTests.Fixture Storage { get; }
        internal RecipeDraftRevision Source { get; }
        internal SqliteRecipeDraftQuery Query { get; }
        internal TargetFactory Factory { get; }
        internal Migrator Migrator { get; }
        internal RecipeDraftService Drafts { get; }
        internal AlgorithmConfigurationMigrationRegistry Registry { get; }
        internal AlgorithmConfigurationMigrationService Service { get; }
        internal RecipeDraftMigrationRequest Request() => new(new(Guid.NewGuid(),
            new(Source.DraftId, Source.Revision, Source.RevisionContentHash), Guid.NewGuid(),
            Factory.Descriptor.Identity, Migrator.Descriptor.TargetSchema, Migrator.Descriptor.Migrator, "explicit config migration"),
            Storage.Invocation());
        internal static async Task<Harness> CreateAsync(bool requireStepUp = false, bool enableRecipeLifecycle = false)
        {
            var baseline = RecipeDraftTestPolicies.Authoring;
            var lifecyclePolicy = enableRecipeLifecycle ? new AuthorizationPolicy("V148.Migration", "1",
                baseline.RoleBundles.ToDictionary(pair => pair.Key, pair => pair.Key == HumanRoleBundle.Administrator
                    ? pair.Value.Append(Permission.AbandonRecipeDraft).Distinct() : pair.Value.AsEnumerable()),
                baseline.StepUpPermissions) : null;
            var storage = await RecipeDraftStorageTests.Fixture.CreateAsync(requireStepUp,
                authorizationPolicy: lifecyclePolicy, recipeLifecycle: enableRecipeLifecycle ? new RecipeLifecycleStoreOptions() : null);
            var operation = Guid.NewGuid(); var id = Guid.NewGuid();
            Guid? grant = null;
            if (requireStepUp)
            {
                var issued = await storage.Authorization.ReauthenticateAsync(new(operation, storage.Invocation(),
                    new(Permission.EditRecipeDraft, operation, id.ToString("D"), AuditedCommandKind.SaveRecipeDraft), storage.Password));
                Assert.True(issued.Succeeded, issued.ReasonCode); grant = issued.GrantId;
                await storage.WaitForVerifiedAsync();
            }
            var result = await storage.SaveAsync(operation, id, 0, null, storage.Document("Historical source"), "source", grant);
            Assert.True(result.Saved, result.ReasonCode);
            await storage.WaitForVerifiedAsync();
            return new(storage, result.Revision!);
        }
        public async ValueTask DisposeAsync()
        { await Service.DisposeAsync(); await Registry.DisposeAsync(); await Drafts.DisposeAsync(); await Storage.DisposeAsync(); }
    }

    private sealed class TargetFactory : IVisionAlgorithmFactory
    {
        internal TargetFactory()
        {
            var schema = new AlgorithmConfigurationSchema("V128.Target.Config", "2", new[] {
                new AlgorithmFieldDefinition("Limit", AlgorithmScalarType.Int64, "items", true,
                    new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100)) });
            Descriptor = new(new("V128.Target.Algorithm", "2"), schema,
                new("V128.Result", "1", Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" },
                    new("V128.Overlay", "1", 0, 64, 16)));
        }
        public AlgorithmDescriptor Descriptor { get; }
        internal bool SemanticInvalid { get; set; }
        internal TaskCompletionSource<bool>? Hold { get; set; }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CreateCalls { get; private set; }
        public async ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            if (Hold is { } hold) await hold.Task;
            return SemanticInvalid ? new[] { new AlgorithmValidationIssue("TargetSemanticRejected", "Limit") } :
                Array.Empty<AlgorithmValidationIssue>();
        }
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        { CreateCalls++; throw new InvalidOperationException("MigrationCannotCreateAlgorithm"); }
    }

    private sealed class Migrator : IAlgorithmConfigurationMigrator
    {
        internal Migrator(RecipeDraftRevision source, AlgorithmDescriptor target)
        {
            var schema = source.Content.Algorithm.ConfigurationSchema;
            Descriptor = new(new("V128.Test.Migrator", "1", new string('C', 64)), source.Content.Algorithm.Algorithm,
                new(schema.Id, schema.Version, schema.ContentHash), target.Identity,
                new(target.ConfigurationSchema.Id, target.ConfigurationSchema.Version, target.ConfigurationSchema.ContentHash));
        }
        public AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
        internal int Calls { get; private set; }
        internal IReadOnlyList<AlgorithmConfigurationEntry> Output { get; set; } = new[] {
            new AlgorithmConfigurationEntry("Limit", "items", AlgorithmScalarValue.FromInt64(5)) };
        internal TaskCompletionSource<bool>? Hold { get; set; }
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AlgorithmConfigurationMigrationTransformResult> MigrateAsync(
            AlgorithmConfigurationMigrationContext context, CancellationToken cancellationToken = default)
        {
            Calls++; Entered.TrySetResult(true);
            if (Calls > 1) SecondEntered.TrySetResult(true);
            try
            {
                if (Hold is { } hold) await hold.Task;
                return new(true, "Converted", Output, new[] { new AlgorithmValidationIssue("LimitRenamed", "Limit") });
            }
            finally { Finished.TrySetResult(true); }
        }
    }
}
