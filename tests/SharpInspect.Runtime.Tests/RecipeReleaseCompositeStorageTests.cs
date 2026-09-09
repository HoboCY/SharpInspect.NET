#pragma warning disable CA1416

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// One real schema-16 writer covering the release, development result archive and alarm
/// projections together.  Each enabled store must remain queryable after the other stores
/// have committed their signed records.
/// </summary>
public sealed class RecipeReleaseCompositeStorageTests
{
    [Fact]
    public Task V130_S04_Schema16ReleaseArchiveAndAlarmWritesRemainQueryable() => RunAsync(false);

    [Fact]
    public Task V131_I01_Schema17ContractReleaseArchiveAndAlarmWritesRemainQueryable() => RunAsync(true);

    private static async Task RunAsync(bool enablePlc)
    {
        RequireWindows();
        var releasePolicy = new RecipeGovernancePolicy("V130.S04.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var alarmPolicy = new AlarmPolicy("V130.S04.Alarm", "1", new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null),
            new AlarmPolicyRule("V130_S04_TEST_ALARM", "V130_S04", AlarmSeverity.Warning,
                ProductionImpact.None, true, AlarmNotification.UntilCleared, null)
        }, TimeSpan.FromSeconds(30));

        await using var storage = await RecipeDraftStorageTests.Fixture.CreateAsync(
            enableArchive: true,
            recipeReleases: new RecipeReleaseStoreOptions(releasePolicy),
            alarmPolicy: alarmPolicy,
            plcResultContracts: enablePlc ? new PlcResultContractStoreOptions() : null);
        Assert.Equal(enablePlc ? 17L : RecipeReleaseStoreOptions.SchemaVersion,
            await storage.ScalarAsync("PRAGMA user_version;"));
        Assert.NotNull(storage.Options.RecipeReleases);
        Assert.NotNull(storage.Options.AlarmPolicy);
        Assert.NotNull(storage.Options.AlgorithmResultArchive);
        var saved = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            storage.Document("V130 S04 composite release"), "V130 S04 composite release");
        Assert.True(saved.Saved, saved.ReasonCode);
        var source = saved.Revision!;
        await storage.WaitForVerifiedAsync();

        var factory = new Factory(source.Content);
        await using var drafts = new RecipeDraftService(new[] { factory },
            storage.Options, storage.Authorization, new SqliteRecipeDraftQuery(storage.Options));
        await using var runtime = new StationRuntime(storage.Store, TimeSpan.FromMilliseconds(500),
            storage.Sessions, storage.Authorization);
        var releaseQuery = new SqliteReleasedRecipeQuery(storage.Options);
        var releases = new RecipeReleaseService(drafts, storage.Authorization, releaseQuery,
            storage.Options, () => runtime.GetSnapshotAsync());
        runtime.ConfigureRecipeReleaseService(releases);

        var command = new ReleaseRecipeCommand(Guid.NewGuid(), storage.Invocation(), source.DraftId,
            source.Revision, source.RevisionContentHash, releasePolicy.Reference,
            "V130 S04 composite release");
        var grant = await storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(),
            storage.Invocation(), new(Permission.ReleaseRecipe, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe), storage.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        await storage.WaitForVerifiedAsync();
        var released = await releases.ReleaseAsync(command with
        {
            Invocation = storage.Invocation(grant.GrantId)
        });
        Assert.Equal(CommandDisposition.Accepted, released.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, released.Outcome.Audit);
        Assert.NotNull(released.Recipe);
        await storage.WaitForVerifiedAsync();

        if (enablePlc)
        {
            var revision = await PlcResultContractTestSupport.CommitAsync(storage.Options, drafts,
                storage.Authorization, runtime, storage.Invocation(), storage.Password, factory.Descriptor.ResultSchema);
            Assert.Equal(released.Recipe!.Record.ContentHash, revision.Bindings.Single().ReleaseRecordContentHash);
        }

        var archiveDocument = CreateArchiveDocument();
        var archived = await storage.Store.AppendAlgorithmResultAsync(archiveDocument,
            new StoreDeadline(storage.Options.CommitTimeout));
        Assert.True(archived.Committed, archived.ReasonCode);
        await storage.WaitForVerifiedAsync();

        var runtimeEpoch = (await runtime.GetSnapshotAsync()).RuntimeEpoch;
        var observationTime = DateTimeOffset.UtcNow;
        var instanceId = Guid.NewGuid();
        var alarmInstance = new AlarmInstanceSnapshot(instanceId, "V130_S04_TEST_ALARM",
            "V130_S04", alarmPolicy.Id, alarmPolicy.Version, alarmPolicy.ContentHash,
            AlarmSeverity.Warning, ProductionImpact.None, true, AlarmNotification.UntilCleared,
            null, 0, AlarmResetPrerequisites.None, observationTime, observationTime, false,
            runtimeEpoch, 1, AlarmLifecycle.Active, false, null, null, 0);
        var observed = await storage.Store.UpdateAlarmObservationAsync(runtimeEpoch, _ =>
            new AlarmObservationUpdate(new object(), new[]
            {
                new AlarmHistoryRecord(0, Guid.NewGuid(), instanceId, alarmInstance.Code,
                    alarmInstance.Source, AlarmTransitionKind.Raised, observationTime,
                    observationTime, null, null, null, "AlarmRaised", alarmInstance, null)
            }), CancellationToken.None, new StoreDeadline(storage.Options.CommitTimeout));
        Assert.True(observed.Committed, observed.ReasonCode);
        await storage.WaitForVerifiedAsync();

        var archivePage = await new SqliteAlgorithmResultQuery(storage.Options).QueryAsync(
            new AlgorithmResultFilter(PageSize: 10));
        Assert.True(archivePage.Available, archivePage.ReasonCode);
        var archiveRecord = Assert.Single(archivePage.Records);
        Assert.Equal(archiveDocument.RecordId, archiveRecord.RecordId);
        Assert.Equal(archiveDocument.PayloadHash, archiveRecord.ContentHash);

        var alarmPage = await new SqliteAlarmHistoryQuery(storage.Options).QueryAsync(
            new AlarmHistoryFilter(code: alarmInstance.Code, pageSize: 10));
        Assert.True(alarmPage.Available, alarmPage.ReasonCode);
        var alarmRecord = Assert.Single(alarmPage.Records);
        Assert.Equal(AlarmTransitionKind.Raised, alarmRecord.Transition);
        Assert.Equal(alarmInstance.InstanceId, alarmRecord.InstanceId);

        var releasePage = await releaseQuery.QueryAsync(new ReleasedRecipeFilter(
            RecipeKey: source.Content.RecipeKey, PageSize: 10));
        Assert.True(releasePage.Available, releasePage.ReasonCode);
        var releaseRecord = Assert.Single(releasePage.Recipes);
        Assert.Equal(released.Recipe!.Reference, releaseRecord.Reference);

        var integrity = await new SqliteAuditIntegrityQuery(storage.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);

        var trace = await new SqliteCommandTraceQuery(storage.Options).QueryAsync(
            new CommandTraceFilter(CorrelationId: command.CorrelationId));
        Assert.Contains(trace.Records, record => record.Disposition == CommandDisposition.Accepted);
        var omittedArchive = new ProductionStoreOptions(storage.Options.DatabasePath)
        {
            AuditIntegrityPolicy = storage.Options.AuditIntegrityPolicy,
            LocalIdentity = storage.Options.LocalIdentity,
            RecipeDrafts = storage.Options.RecipeDrafts,
            RecipeReleases = storage.Options.RecipeReleases,
            PlcResultContracts = storage.Options.PlcResultContracts,
            AlarmPolicy = storage.Options.AlarmPolicy
        };
        var incompleteQuery = await new SqliteReleasedRecipeQuery(omittedArchive).QueryAsync(
            new ReleasedRecipeFilter());
        Assert.False(incompleteQuery.Available);
        Assert.Equal("AlgorithmResultArchiveConfigurationRequired", incompleteQuery.ReasonCode);
        var traceFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(omittedArchive).QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal("AlgorithmResultArchiveConfigurationRequired", traceFailure.Message);

        var archiveOptions = storage.Options.AlgorithmResultArchive!;
        var mismatchedArchive = new ProductionStoreOptions(storage.Options.DatabasePath)
        {
            AuditIntegrityPolicy = storage.Options.AuditIntegrityPolicy,
            LocalIdentity = storage.Options.LocalIdentity,
            RecipeDrafts = storage.Options.RecipeDrafts,
            RecipeReleases = storage.Options.RecipeReleases,
            PlcResultContracts = storage.Options.PlcResultContracts,
            AlarmPolicy = storage.Options.AlarmPolicy,
            AlgorithmResultArchive = new AlgorithmResultArchiveOptions
            {
                MaximumRecordBytes = archiveOptions.MaximumRecordBytes,
                MaximumTotalBytes = archiveOptions.MaximumTotalBytes,
                MaximumPageBytes = archiveOptions.MaximumPageBytes,
                MaximumRecords = archiveOptions.MaximumRecords - 1
            }
        };
        var mismatchedTrace = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(mismatchedArchive).QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal("AlgorithmResultArchiveConfigurationMismatch", mismatchedTrace.Message);
    }

    private static AlgorithmResultArchiveDocument CreateArchiveDocument()
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var configurationSchema = new AlgorithmConfigurationSchema("V130.S04.Archive.Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var overlay = new OverlayContract("V130.S04.Archive.Overlay", "1");
        var resultSchema = new AlgorithmResultSchema("V130.S04.Archive.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
        var descriptor = new AlgorithmDescriptor(
            new AlgorithmIdentity("V130.S04.Archive.Algorithm", "1"), configurationSchema,
            resultSchema);
        var prepared = new PreparedAlgorithm(descriptor, configuration, new NoopAlgorithm(),
            (_, _) => Task.CompletedTask);
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 2, 1), VisionPixelFormat.Mono8,
            null, 1000, 0, null);
        var frame = new FrameMetadata(correlation, "TopCamera", 2, 1, 2,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            Array.Empty<AlgorithmMeasurement>(), new OutputOverlaySet(overlay));
        var timing = new AlgorithmExecutionTimingSnapshot(
            new RecipeReference("V130.S04.Archive.Recipe", "1", new string('A', 64)),
            "V130.S04.Archive.Execution", "1", new string('B', 64),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var outcome = new AlgorithmExecutionOutcome(prepared, frame, ExecutionStatus.Success,
            null, result, timing, 1);
        Assert.True(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), outcome,
            out var document, out var reason), reason);
        return document!;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema 16 composite storage requires Windows machine protection.");
    }

    private sealed class NoopAlgorithm : IVisionAlgorithm
    {
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Factory : IVisionAlgorithmFactory
    {
        internal Factory(RecipeDraftContent content) => Descriptor = new(content.Algorithm.Algorithm,
            content.Algorithm.ConfigurationSchema, new("V115.Draft.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" },
                new("V115.Draft.Overlay", "1", 0, 64, 16)));

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("RecipeReleaseCompositeStorageTestMustNotCreateAlgorithm");
    }
}

#pragma warning restore CA1416
