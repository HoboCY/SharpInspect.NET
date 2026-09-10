using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// The manual workflow and calibration workflow share one StationRuntime.  This test
/// proves the manual owner is a durable admission barrier: a calibration command with
/// an otherwise valid RunCalibration Step-Up is rejected before a calibration session
/// or a second camera owner can be created.
/// </summary>
public sealed class ManualInspectionCalibrationCompetitionTests
{
    [Fact]
    public async Task V135_M18_ManualOwnerRejectsAuthorizedCalibrationBeforeCameraMutation()
    {
        var factory = new CompetitionFactory();
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true,
            manualInspections: new ManualInspectionStoreOptions(),
            manualFactory: factory);

        var draft = await SaveManualDraftAsync(fixture, factory);
        var manual = Assert.IsAssignableFrom<IManualInspectionSessionService>(fixture.Runtime);
        var manualAccess = await manual.GetAccessAsync(fixture.User.Invocation);
        Assert.True(manualAccess.CanRun, manualAccess.ReasonCode);

        var manualStart = new StartManualInspectionSessionCommand(Guid.NewGuid(),
            fixture.User.Invocation, ManualRecipeSelection.FromDraft(draft), null,
            "V135 M18 occupy camera for calibration competition");
        var manualAdmission = await fixture.Runtime.SubmitAsync(manualStart);
        AssertAccepted(manualAdmission, "Manual admission");
        var ready = await WaitForManualSnapshotAsync(manual, fixture.User.Invocation,
            value => value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        var calibration = await fixture.CreateAuthorizedStartCommandAsync();
        var openCount = fixture.Provider.OpenCount;
        var deviceCount = fixture.Provider.Devices.Count;
        var calibrationResult = await fixture.Runtime.SubmitAsync(calibration);

        Assert.Equal(CommandDisposition.Rejected, calibrationResult.Disposition);
        Assert.Equal("ManualInspectionSessionInProgress", calibrationResult.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, calibrationResult.Audit);
        Assert.Empty(await fixture.Store.ReadOpenCalibrationSessionsAsync());
        Assert.Null((await fixture.Runtime.GetSnapshotAsync()).CalibrationSession);
        Assert.Equal(openCount, fixture.Provider.OpenCount);
        Assert.Equal(deviceCount, fixture.Provider.Devices.Count);

        var exit = await fixture.Runtime.SubmitAsync(new ExitManualInspectionSessionCommand(
            Guid.NewGuid(), fixture.User.Invocation, sessionId,
            ManualInspectionExitMode.Graceful, "V135 M18 release camera after competition"));
        AssertAccepted(exit, "Manual exit");
        var closed = await WaitForManualSnapshotAsync(manual, fixture.User.Invocation,
            value => value.SessionId == sessionId &&
                value.Phase == ManualInspectionSessionPhase.Closed,
            "Manual session did not close after competition");
        Assert.False(closed.RecoveryRequired);
        Assert.Equal(ManualInspectionRestorationState.NoActiveBaselineClosed,
            closed.Restoration);
    }

    private static async Task<RecipeDraftRevision> SaveManualDraftAsync(
        CalibrationSessionRuntimeTests.Fixture fixture, CompetitionFactory factory)
    {
        var execution = fixture.Options.RecipeDrafts!.ExecutionPolicy;
        var configuration = AlgorithmConfigurationSnapshot.Create(
            factory.Descriptor.ConfigurationSchema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var content = new RecipeDraftContent(
            "V135.M18.ManualCalibrationCompetition",
            "V135 M18 manual calibration competition draft",
            RecipeAlgorithmBinding.FromDescriptor(factory.Descriptor), configuration,
            "TopCamera", fixture.BaselineRequested, TimeSpan.FromSeconds(2),
            Array.Empty<RecipeAssetRequirement>(), new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(execution.Id, execution.Version,
                        execution.ContentHash))
            },
            partIdentityRequirement: PartIdentityRequirement.None);
        var request = new RecipeDraftSaveRequest(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            content, "V135 M18 save manual calibration competition draft",
            fixture.User.Invocation);
        var result = await fixture.ManualDrafts!.SaveAsync(request);
        Assert.True(result.Saved, result.ReasonCode);
        return Assert.IsType<RecipeDraftRevision>(result.Revision);
    }

    private static async Task<ManualInspectionSessionSnapshot> WaitForManualSnapshotAsync(
        IManualInspectionSessionService manual, CommandInvocation invocation,
        Func<ManualInspectionSessionSnapshot, bool> predicate, string timeoutReason)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        ManualInspectionSessionSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var result = await manual.GetSnapshotAsync(invocation);
            Assert.True(result.Available, result.ReasonCode);
            last = Assert.IsType<ManualInspectionSessionSnapshot>(result.Snapshot);
            if (predicate(last)) return last;
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(timeoutReason + ":" + last?.Phase + "/" +
            last?.ReasonCode);
    }

    private static void AssertAccepted(RuntimeCommandOutcome result, string operation)
    {
        Assert.Equal(CommandDisposition.Accepted, result.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.True(result.AttemptId is { } attempt && attempt != Guid.Empty,
            operation + " did not retain an audit attempt: " + result.ReasonCode);
    }

    private sealed class CompetitionFactory : IVisionAlgorithmFactory
    {
        public CompetitionFactory()
        {
            var configuration = new AlgorithmConfigurationSchema(
                "V135.M18.Manual.Config", "1", Array.Empty<AlgorithmFieldDefinition>());
            var overlay = new OverlayContract("V135.M18.Manual.Overlay", "1", 0, 4, 4);
            var result = new AlgorithmResultSchema("V135.M18.Manual.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "ManualSyntheticUnknown" },
                overlay);
            Descriptor = new AlgorithmDescriptor(
                new AlgorithmIdentity("V135.M18.Manual.Algorithm", "1"),
                configuration, result);
        }

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(
            AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new CompetitionAlgorithm());
    }

    private sealed class CompetitionAlgorithm : IVisionAlgorithm
    {
        public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("V135M18ExecutionNotRequired");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
