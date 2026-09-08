#pragma warning disable CA1416

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraNetworkCompositeStorageTests
{
    [Fact]
    public async Task V120_M01_NetworkQueryAndVerifyRemainUsableWithLargeArchivePayload()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            archiveOptions: new AlgorithmResultArchiveOptions());
        var document = LargeArchiveDocument();
        var written = await fixture.Store.AppendAlgorithmResultAsync(document,
            new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(written.Committed, written.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var payloadBytes = await fixture.ScalarAsync(
            "SELECT length(CAST(PayloadJson AS BLOB)) FROM development_algorithm_results WHERE Position=1;");
        Assert.InRange(payloadBytes, 512L * 1024 + 1, 1024L * 1024);

        var admitted = await fixture.AdmitAsync();
        var terminalSnapshot = SucceededSnapshot(admitted);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        Assert.Equal(terminalSnapshot,
            await fixture.Persistence.ReadLatestAsync(fixture.Target));
        var report = await new SqliteAuditIntegrityQuery(fixture.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, report.State);
    }

    [Fact]
    public async Task V120_M02_NetworkQueryAndVerifyRemainUsableWithLargeRecipePayload()
    {
        RequireWindows();
        var execution = new AlgorithmExecutionPolicy("V120.Composite.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var factory = LargeDraftFactory.Create(execution);
        var recipeOptions = new RecipeDraftStoreOptions(execution)
        {
            MaximumRecordBytes = 2 * 1024 * 1024,
            MaximumPageBytes = 4 * 1024 * 1024,
            MaximumTotalBytes = 4 * 1024 * 1024
        };
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            recipeOptions: recipeOptions, authorizationPolicy: RecipeDraftTestPolicies.Authoring);
        var signedIn = await fixture.SignInAsync();
        var query = new SqliteRecipeDraftQuery(fixture.Options);
        await using var drafts = new RecipeDraftService(new[] { factory }, fixture.Options,
            fixture.Authorization, query);
        var draftId = Guid.NewGuid();
        var saved = await drafts.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(), draftId, 0,
            null, factory.Content, "V120 large composite draft", signedIn.Invocation));
        Assert.True(saved.Saved, saved.ReasonCode);
        Assert.NotNull(saved.Revision);
        await fixture.WaitForVerifiedAsync();

        var payloadBytes = await fixture.ScalarAsync(
            "SELECT length(CAST(PayloadJson AS BLOB)) FROM recipe_draft_revisions WHERE Position=1;");
        Assert.InRange(payloadBytes, 512L * 1024 + 1, 2L * 1024 * 1024);
        var page = await query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 1));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(factory.Content.ContentHash, Assert.Single(page.Revisions).Content.ContentHash);

        var admitted = await fixture.AdmitAsync();
        var terminalSnapshot = SucceededSnapshot(admitted);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        Assert.Equal(terminalSnapshot,
            await fixture.Persistence.ReadLatestAsync(fixture.Target));
        var report = await new SqliteAuditIntegrityQuery(fixture.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, report.State);
    }

    private static CameraNetworkSnapshot SucceededSnapshot(CameraNetworkAdmissionHandle admitted) =>
        new(admitted.Request.OperationId, admitted.Request.Target,
            CameraNetworkMaintenanceState.Succeeded, admitted.Previous,
            admitted.Request.Requested, admitted.Request.Requested, true,
            "CameraNetworkChanged", DateTimeOffset.UtcNow);

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Camera network composite storage requires Windows machine protection.");
    }

    private static AlgorithmResultArchiveDocument LargeArchiveDocument()
    {
        var outcome = LargeOutcome();
        Assert.True(AlgorithmResultStorageCodec.TryEncode(Guid.NewGuid(), outcome,
            out var document, out var reason), reason);
        return document!;
    }

    private static AlgorithmExecutionOutcome LargeOutcome()
    {
        const int fieldCount = 160;
        const int textLength = 3500;
        var text = new string('x', textLength);
        var configurationSchema = new AlgorithmConfigurationSchema("V120.Composite.Archive.Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var resultFields = Enumerable.Range(0, fieldCount)
            .Select(index => new AlgorithmFieldDefinition("Result" + index.ToString("D3"),
                AlgorithmScalarType.String, "text", true)).ToArray();
        var overlay = new OverlayContract("V120.Composite.Archive.Overlay", "1", 0, 64, 16);
        var resultSchema = new AlgorithmResultSchema("V120.Composite.Archive.Result", "1",
            resultFields, Array.Empty<string>(), overlay);
        var descriptor = new AlgorithmDescriptor(
            new AlgorithmIdentity("V120.Composite.Archive.Algorithm", "1"),
            configurationSchema, resultSchema);
        var prepared = new PreparedAlgorithm(descriptor, configuration, new NoopAlgorithm(),
            (_, _) => Task.CompletedTask);
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1000, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8,
            null, 1000, 0, null);
        var frame = new FrameMetadata(correlation, "TopCamera", 16, 12, 16,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, camera);
        var measurements = resultFields.Select(field => new AlgorithmMeasurement(field.Key, field.Unit,
            AlgorithmScalarValue.FromString(text))).ToArray();
        var result = new AlgorithmResult(InspectionDecision.Pass, null, measurements,
            new OutputOverlaySet(overlay));
        var timing = new AlgorithmExecutionTimingSnapshot(
            new RecipeReference("V120.Composite.Archive.Recipe", "1", new string('A', 64)),
            "V120.Composite.Archive.Execution", "1", new string('B', 64),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        return new AlgorithmExecutionOutcome(prepared, frame, ExecutionStatus.Success, null,
            result, timing, 1);
    }

    private sealed class NoopAlgorithm : IVisionAlgorithm
    {
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LargeDraftFactory : IVisionAlgorithmFactory
    {
        private LargeDraftFactory(AlgorithmExecutionPolicy executionPolicy,
            AlgorithmDescriptor descriptor, RecipeDraftContent content)
        {
            ExecutionPolicy = executionPolicy;
            Descriptor = descriptor;
            Content = content;
        }

        internal AlgorithmExecutionPolicy ExecutionPolicy { get; }
        internal RecipeDraftContent Content { get; }
        public AlgorithmDescriptor Descriptor { get; }

        internal static LargeDraftFactory Create(AlgorithmExecutionPolicy executionPolicy)
        {
            const int fieldCount = 160;
            const int textLength = 3500;
            var text = new string('x', textLength);
            var fields = Enumerable.Range(0, fieldCount)
                .Select(index => new AlgorithmFieldDefinition("Field" + index.ToString("D3"),
                    AlgorithmScalarType.String, "text", true,
                    new AlgorithmScalarConstraints(maxLength: textLength), helpText: text))
                .ToArray();
            var configurationSchema = new AlgorithmConfigurationSchema(
                "V120.Composite.Recipe.Config", "1", fields);
            var values = fields.Select(field => new AlgorithmConfigurationEntry(field.Key,
                field.Unit, AlgorithmScalarValue.FromString(text))).ToArray();
            var configuration = AlgorithmConfigurationSnapshot.Create(configurationSchema, values);
            var overlay = new OverlayContract("V120.Composite.Recipe.Overlay", "1", 0, 64, 16);
            var resultSchema = new AlgorithmResultSchema("V120.Composite.Recipe.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" }, overlay);
            var descriptor = new AlgorithmDescriptor(
                new AlgorithmIdentity("V120.Composite.Recipe.Algorithm", "1"),
                configurationSchema, resultSchema);
            var binding = RecipeAlgorithmBinding.FromDescriptor(descriptor);
            var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                100, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8,
                null, 1000, 0, null);
            var requirement = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new RecipeContractReference(executionPolicy.Id, executionPolicy.Version,
                    executionPolicy.ContentHash));
            var content = new RecipeDraftContent("V120.Composite.Recipe", "V120 large recipe draft",
                binding, configuration, "TopCamera", camera, TimeSpan.FromMilliseconds(100), null,
                new[] { requirement });
            return new LargeDraftFactory(executionPolicy, descriptor, content);
        }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new DraftNoopAlgorithm());

        private sealed class DraftNoopAlgorithm : IVisionAlgorithm
        {
            public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

#pragma warning restore CA1416
