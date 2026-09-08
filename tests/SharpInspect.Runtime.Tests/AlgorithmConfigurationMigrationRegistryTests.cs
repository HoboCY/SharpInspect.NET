using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmConfigurationMigrationRegistryTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task V128_M01_RegistryFreezesDescriptorsAndRejectsDuplicateRegistrations()
    {
        var fixture = MigrationFixture.Create();
        using var migrator = new TestMigrator(fixture.Descriptor);
        using var duplicate = new TestMigrator(fixture.Descriptor);

        await using var registry = new AlgorithmConfigurationMigrationRegistry(
            new[] { migrator }, TimeSpan.FromSeconds(1));

        Assert.Single(registry.Migrations);
        Assert.Same(fixture.Descriptor, registry.Migrations[0]);
        var duplicateException = Assert.Throws<ArgumentException>(() =>
            new AlgorithmConfigurationMigrationRegistry(new[] { migrator, duplicate },
                TimeSpan.FromSeconds(1)));
        Assert.StartsWith("RecipeDraftMigrationDescriptorDuplicate", duplicateException.Message);
        Assert.Equal("migrators", duplicateException.ParamName);

        var conflictingMigrator = new TestMigrator(new AlgorithmConfigurationMigrationDescriptor(
            new RecipeContractReference(fixture.Descriptor.Migrator.Id,
                fixture.Descriptor.Migrator.Version, HashB),
            fixture.Descriptor.SourceAlgorithm, fixture.Descriptor.SourceSchema,
            fixture.Descriptor.TargetAlgorithm, fixture.Descriptor.TargetSchema));
        try
        {
            var conflictException = Assert.Throws<ArgumentException>(() =>
                new AlgorithmConfigurationMigrationRegistry(new[] { migrator, conflictingMigrator },
                    TimeSpan.FromSeconds(1)));
            Assert.StartsWith("RecipeDraftMigrationMigratorIdentityConflict", conflictException.Message);
            Assert.Equal("migrators", conflictException.ParamName);
        }
        finally
        {
            conflictingMigrator.Dispose();
        }

        var tooMany = Enumerable.Range(0, 65)
            .Select(index => new TestMigrator(new AlgorithmConfigurationMigrationDescriptor(
                new RecipeContractReference("Migration." + index, "1", HashA),
                fixture.Descriptor.SourceAlgorithm, fixture.Descriptor.SourceSchema,
                fixture.Descriptor.TargetAlgorithm, fixture.Descriptor.TargetSchema)))
            .ToArray();
        try
        {
            var capacityException = Assert.Throws<ArgumentException>(() =>
                new AlgorithmConfigurationMigrationRegistry(tooMany, TimeSpan.FromSeconds(1)));
            Assert.StartsWith("RecipeDraftMigrationDescriptorCapacityExceeded", capacityException.Message);
            Assert.Equal("migrators", capacityException.ParamName);
        }
        finally
        {
            foreach (var item in tooMany) item.Dispose();
        }
    }

    [Fact]
    public async Task V128_M02_TransformRequiresTheExactTupleAndRejectsAnUnchangedTarget()
    {
        var fixture = MigrationFixture.Create();
        var migrator = new TestMigrator(fixture.Descriptor);
        await using var registry = new AlgorithmConfigurationMigrationRegistry(
            new[] { migrator }, TimeSpan.FromSeconds(1));

        var success = await registry.TransformAsync(fixture.Plan, fixture.Context,
            CancellationToken.None);
        Assert.True(success.Succeeded, success.ReasonCode);
        Assert.Single(success.Values);

        var unknownPlan = MigrationFixture.CreatePlan(fixture, new RecipeContractReference(
            "Migration.Unknown", "1", HashA));
        var unavailable = await registry.TransformAsync(unknownPlan, fixture.Context,
            CancellationToken.None);
        Assert.False(unavailable.Succeeded);
        Assert.Equal("RecipeDraftMigrationMigratorUnavailable", unavailable.ReasonCode);
        Assert.Empty(unavailable.Values);

        var unchanged = MigrationFixture.Create(unchangedTarget: true);
        var unchangedMigrator = new TestMigrator(unchanged.Descriptor);
        await using var unchangedRegistry = new AlgorithmConfigurationMigrationRegistry(
            new[] { unchangedMigrator }, TimeSpan.FromSeconds(1));
        var unchangedResult = await unchangedRegistry.TransformAsync(unchanged.Plan,
            unchanged.Context, CancellationToken.None);
        Assert.False(unchangedResult.Succeeded);
        Assert.Equal("RecipeDraftMigrationTargetUnchanged", unchangedResult.ReasonCode);
        Assert.Empty(unchangedResult.Values);
        Assert.Equal(0, unchangedMigrator.CallCount);
    }

    [Fact]
    public async Task V128_M03_DescriptorContentHashIsCheckedBeforeAndAfterMigration()
    {
        var fixture = MigrationFixture.Create();
        var changed = fixture.WithChangedDescriptor();
        var migrator = new TestMigrator(fixture.Descriptor);
        await using var registry = new AlgorithmConfigurationMigrationRegistry(
            new[] { migrator }, TimeSpan.FromSeconds(1));

        migrator.CurrentDescriptor = changed;
        var before = await registry.TransformAsync(fixture.Plan, fixture.Context,
            CancellationToken.None);
        Assert.False(before.Succeeded);
        Assert.Equal("RecipeDraftMigrationDescriptorChanged", before.ReasonCode);
        Assert.Empty(before.Values);
        Assert.Equal(0, migrator.CallCount);

        migrator.CurrentDescriptor = fixture.Descriptor;
        migrator.Handler = (_, _) =>
        {
            migrator.CurrentDescriptor = changed;
            return ValueTask.FromResult(Success());
        };
        var after = await registry.TransformAsync(fixture.Plan, fixture.Context,
            CancellationToken.None);
        Assert.False(after.Succeeded);
        Assert.Equal("RecipeDraftMigrationDescriptorChanged", after.ReasonCode);
        Assert.Empty(after.Values);
        Assert.Equal(1, migrator.CallCount);
    }

    [Fact]
    public async Task V128_M04_TimeoutRetainsTheActualMigratorSlotUntilLateWorkExits()
    {
        var fixture = MigrationFixture.Create();
        var migrator = new BlockingMigrator(fixture.Descriptor);
        await using var registry = new AlgorithmConfigurationMigrationRegistry(
            new[] { migrator }, TimeSpan.FromMilliseconds(40));

        var first = registry.TransformAsync(fixture.Plan, fixture.Context,
            CancellationToken.None).AsTask();
        await migrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var timedOut = await first;
        Assert.False(timedOut.Succeeded);
        Assert.Equal("RecipeDraftMigrationTimedOut", timedOut.ReasonCode);
        Assert.Empty(timedOut.Values);

        var busy = await registry.TransformAsync(fixture.Plan, fixture.Context,
            CancellationToken.None);
        Assert.False(busy.Succeeded);
        Assert.Equal("RecipeDraftMigrationBusy", busy.ReasonCode);
        Assert.Equal(1, migrator.CallCount);

        migrator.Release.TrySetResult(true);
        await migrator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var recovered = await EventuallyTransformAsync(registry, fixture);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        Assert.Equal(2, migrator.CallCount);

        Assert.False(timedOut.Succeeded);
        Assert.Empty(timedOut.Values);
    }

    [Fact]
    public async Task V128_M05_CancellationRetainsTheActualSlotAndDoesNotReuseLateOutput()
    {
        var fixture = MigrationFixture.Create();
        var migrator = new BlockingMigrator(fixture.Descriptor);
        await using var registry = new AlgorithmConfigurationMigrationRegistry(
            new[] { migrator }, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();

        var first = registry.TransformAsync(fixture.Plan, fixture.Context,
            cancellation.Token).AsTask();
        await migrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        var cancelled = await first;
        Assert.False(cancelled.Succeeded);
        Assert.Equal("RecipeDraftMigrationCancelled", cancelled.ReasonCode);
        Assert.Empty(cancelled.Values);

        var busy = await registry.TransformAsync(fixture.Plan, fixture.Context,
            CancellationToken.None);
        Assert.False(busy.Succeeded);
        Assert.Equal("RecipeDraftMigrationBusy", busy.ReasonCode);
        Assert.Equal(1, migrator.CallCount);

        migrator.Release.TrySetResult(true);
        await migrator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var recovered = await EventuallyTransformAsync(registry, fixture);
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        Assert.Equal(2, migrator.CallCount);
        Assert.False(cancelled.Succeeded);
        Assert.Empty(cancelled.Values);
    }

    [Fact]
    public async Task V128_M06_GlobalActualSlotsAreBoundedAcrossMigrators()
    {
        var firstFixture = MigrationFixture.Create(migratorId: "Migration.First");
        var secondFixture = MigrationFixture.Create(migratorId: "Migration.Second");
        var thirdFixture = MigrationFixture.Create(migratorId: "Migration.Third");
        var first = new BlockingMigrator(firstFixture.Descriptor);
        var second = new BlockingMigrator(secondFixture.Descriptor);
        var third = new TestMigrator(thirdFixture.Descriptor);
        await using var registry = new AlgorithmConfigurationMigrationRegistry(
            new IAlgorithmConfigurationMigrator[] { first, second, third },
            TimeSpan.FromSeconds(2));

        var firstTask = registry.TransformAsync(firstFixture.Plan, firstFixture.Context,
            CancellationToken.None).AsTask();
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondTask = registry.TransformAsync(secondFixture.Plan, secondFixture.Context,
            CancellationToken.None).AsTask();
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var busy = await registry.TransformAsync(thirdFixture.Plan, thirdFixture.Context,
            CancellationToken.None);
        Assert.False(busy.Succeeded);
        Assert.Equal("RecipeDraftMigrationBusy", busy.ReasonCode);
        Assert.Equal(0, third.CallCount);

        first.Release.TrySetResult(true);
        second.Release.TrySetResult(true);
        Assert.True((await firstTask).Succeeded);
        Assert.True((await secondTask).Succeeded);
        var thirdResult = await EventuallyTransformAsync(registry, thirdFixture);
        Assert.True(thirdResult.Succeeded, thirdResult.ReasonCode);
        Assert.Equal(1, third.CallCount);
    }

    [Fact]
    public async Task V128_M07_DisposeReturnsWithinBoundWhenMigratorIgnoresCancellation()
    {
        var fixture = MigrationFixture.Create();
        var migrator = new BlockingMigrator(fixture.Descriptor);
        var registry = new AlgorithmConfigurationMigrationRegistry(
            new[] { migrator }, TimeSpan.FromSeconds(5));
        try
        {
            _ = registry.TransformAsync(fixture.Plan, fixture.Context,
                CancellationToken.None).AsTask();
            await migrator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var dispose = registry.DisposeAsync().AsTask();
            await dispose.WaitAsync(TimeSpan.FromSeconds(3));
            var rejected = await registry.TransformAsync(fixture.Plan, fixture.Context,
                CancellationToken.None);
            Assert.False(rejected.Succeeded);
            Assert.Equal("RecipeDraftMigrationRegistryDisposed", rejected.ReasonCode);
        }
        finally
        {
            migrator.Release.TrySetResult(true);
            await migrator.Completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await registry.DisposeAsync();
        }
    }

    private static async Task<AlgorithmConfigurationMigrationTransformResult>
        EventuallyTransformAsync(AlgorithmConfigurationMigrationRegistry registry,
            MigrationFixture fixture)
    {
        AlgorithmConfigurationMigrationTransformResult result =
            new(false, "RecipeDraftMigrationBusy");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            result = await registry.TransformAsync(fixture.Plan, fixture.Context,
                CancellationToken.None);
            if (result.Succeeded || result.ReasonCode != "RecipeDraftMigrationBusy")
                break;
            await Task.Delay(5);
        }
        return result;
    }

    private static AlgorithmConfigurationMigrationTransformResult Success() =>
        new(true, "RecipeDraftMigrationSucceeded", new[]
        {
            new AlgorithmConfigurationEntry("Migrated", "value",
                AlgorithmScalarValue.FromInt64(1))
        });

    private sealed class MigrationFixture
    {
        private MigrationFixture(AlgorithmIdentity sourceAlgorithm,
            AlgorithmConfigurationSchema sourceSchema, AlgorithmIdentity targetAlgorithm,
            AlgorithmConfigurationSchema targetSchema,
            RecipeContractReference migrator, AlgorithmConfigurationMigrationDescriptor descriptor,
            RecipeAlgorithmBinding sourceBinding, AlgorithmConfigurationSnapshot sourceConfiguration,
            AlgorithmDescriptor targetDescriptor, RecipeDraftRevisionReference sourceRevision,
            RecipeDraftMigrationPlan plan, AlgorithmConfigurationMigrationContext context)
        {
            SourceAlgorithm = sourceAlgorithm;
            SourceSchema = sourceSchema;
            TargetAlgorithm = targetAlgorithm;
            TargetSchema = targetSchema;
            Migrator = migrator;
            Descriptor = descriptor;
            SourceBinding = sourceBinding;
            SourceConfiguration = sourceConfiguration;
            TargetDescriptor = targetDescriptor;
            SourceRevision = sourceRevision;
            Plan = plan;
            Context = context;
        }

        internal AlgorithmIdentity SourceAlgorithm { get; }
        internal AlgorithmConfigurationSchema SourceSchema { get; }
        internal AlgorithmIdentity TargetAlgorithm { get; }
        internal AlgorithmConfigurationSchema TargetSchema { get; }
        internal RecipeContractReference Migrator { get; }
        internal AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
        internal RecipeAlgorithmBinding SourceBinding { get; }
        internal AlgorithmConfigurationSnapshot SourceConfiguration { get; }
        internal AlgorithmDescriptor TargetDescriptor { get; }
        internal RecipeDraftRevisionReference SourceRevision { get; }
        internal RecipeDraftMigrationPlan Plan { get; }
        internal AlgorithmConfigurationMigrationContext Context { get; }

        internal static MigrationFixture Create(bool unchangedTarget = false,
            string migratorId = "Migration.Default")
        {
            var sourceAlgorithm = new AlgorithmIdentity("Source.Algorithm", "1");
            var sourceSchema = new AlgorithmConfigurationSchema("Source.Schema", "1",
                Array.Empty<AlgorithmFieldDefinition>());
            var targetAlgorithm = unchangedTarget
                ? sourceAlgorithm
                : new AlgorithmIdentity("Target.Algorithm", "2");
            var targetSchema = unchangedTarget
                ? sourceSchema
                : new AlgorithmConfigurationSchema("Target.Schema", "2",
                    Array.Empty<AlgorithmFieldDefinition>());
            var sourceBinding = Binding(sourceAlgorithm, sourceSchema, "Source");
            var sourceConfiguration = AlgorithmConfigurationSnapshot.Create(sourceSchema,
                Array.Empty<AlgorithmConfigurationEntry>());
            var targetDescriptor = CreateAlgorithmDescriptor(targetAlgorithm, targetSchema, "Target");
            var sourceRevision = new RecipeDraftRevisionReference(
                Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, HashA);
            var migrator = new RecipeContractReference(migratorId, "1", HashA);
            var descriptor = new AlgorithmConfigurationMigrationDescriptor(migrator,
                sourceAlgorithm, Contract(sourceSchema), targetAlgorithm, Contract(targetSchema));
            var plan = new RecipeDraftMigrationPlan(
                Guid.Parse("22222222-2222-2222-2222-222222222222"), sourceRevision,
                Guid.Parse("33333333-3333-3333-3333-333333333333"), targetAlgorithm,
                Contract(targetSchema), migrator, "migrate fixture");
            var context = new AlgorithmConfigurationMigrationContext(sourceRevision,
                sourceBinding, sourceConfiguration, targetDescriptor);
            return new(sourceAlgorithm, sourceSchema, targetAlgorithm, targetSchema,
                migrator, descriptor, sourceBinding, sourceConfiguration, targetDescriptor,
                sourceRevision, plan, context);
        }

        internal static RecipeDraftMigrationPlan CreatePlan(MigrationFixture fixture,
            RecipeContractReference migrator) => new(
                fixture.Plan.OperationId, fixture.SourceRevision,
                fixture.Plan.TargetDraftId, fixture.TargetAlgorithm,
                Contract(fixture.TargetSchema), migrator, fixture.Plan.ChangeReason);

        internal AlgorithmConfigurationMigrationDescriptor WithChangedDescriptor() =>
            new(Migrator, SourceAlgorithm, Contract(SourceSchema), TargetAlgorithm,
                Contract(new AlgorithmConfigurationSchema("Changed.Target.Schema", "3",
                    Array.Empty<AlgorithmFieldDefinition>())));

        private static RecipeAlgorithmBinding Binding(AlgorithmIdentity identity,
            AlgorithmConfigurationSchema schema, string suffix)
        {
            var overlay = new OverlayContract("Migration." + suffix + ".Overlay", "1", 0, 0, 0);
            var result = new AlgorithmResultSchema("Migration." + suffix + ".Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
            return new RecipeAlgorithmBinding(identity, schema,
                new RecipeContractReference(result.Id, result.Version, result.ContentHash),
                new RecipeContractReference(overlay.Id, overlay.Version, overlay.ContentHash));
        }

        private static AlgorithmDescriptor CreateAlgorithmDescriptor(AlgorithmIdentity identity,
            AlgorithmConfigurationSchema schema, string suffix)
        {
            var overlay = new OverlayContract("Migration." + suffix + ".DescriptorOverlay", "1", 0, 0, 0);
            var result = new AlgorithmResultSchema("Migration." + suffix + ".DescriptorResult", "1",
                Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
            return new AlgorithmDescriptor(identity, schema, result);
        }

        private static RecipeContractReference Contract(AlgorithmConfigurationSchema schema) =>
            new(schema.Id, schema.Version, schema.ContentHash);
    }

    private class TestMigrator : IAlgorithmConfigurationMigrator, IDisposable
    {
        private AlgorithmConfigurationMigrationDescriptor _descriptor;

        internal TestMigrator(AlgorithmConfigurationMigrationDescriptor descriptor)
        { _descriptor = descriptor; }

        public AlgorithmConfigurationMigrationDescriptor Descriptor => _descriptor;
        internal AlgorithmConfigurationMigrationDescriptor CurrentDescriptor
        {
            get => _descriptor;
            set => _descriptor = value;
        }
        internal Func<AlgorithmConfigurationMigrationContext, CancellationToken,
            ValueTask<AlgorithmConfigurationMigrationTransformResult>> Handler { get; set; } =
            (_, _) => ValueTask.FromResult(Success());
        internal int CallCount { get; private set; }

        public ValueTask<AlgorithmConfigurationMigrationTransformResult> MigrateAsync(
            AlgorithmConfigurationMigrationContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Handler(context, cancellationToken);
        }

        public void Dispose() { }
    }

    private sealed class BlockingMigrator : TestMigrator
    {
        internal readonly TaskCompletionSource<bool> Entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BlockingMigrator(AlgorithmConfigurationMigrationDescriptor descriptor)
            : base(descriptor)
        {
            Handler = RunAsync;
        }

        private async ValueTask<AlgorithmConfigurationMigrationTransformResult> RunAsync(
            AlgorithmConfigurationMigrationContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            try
            {
                await Release.Task.ConfigureAwait(false);
                return Success();
            }
            finally
            {
                Completed.TrySetResult(true);
            }
        }
    }
}
