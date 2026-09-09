using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T32 service-level integration.  The fixture uses the real SQLite writer,
/// local identity/session authority, recipe/release/PLC histories, algorithm
/// preparation and virtual camera provider.  The only substituted qualification
/// witness is the closed internal contract fixture used by the development case.
/// </summary>
public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V132_G01_PublicCompositionAuditsQualificationRejectionBeforeCameraIo()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;

        var result = await harness.Runtime.SubmitAsync(await harness.AuthorizedActivationCommand());

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal("FrameworkQualificationAuthorityUnavailable", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.NotNull(result.AttemptId);
        await harness.WaitForVerifiedAsync();

        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        var record = Assert.Single(page.Records, value => value.IsTerminal);
        Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
        Assert.Equal("FrameworkQualificationAuthorityUnavailable", record.Outcome.ReasonCode);
        Assert.Null(record.SuccessfulSnapshot);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A12" &&
            check.Status == RecipeActivationCheckStatus.Failed &&
            check.ReasonCode == "FrameworkQualificationAuthorityUnavailable");
        Assert.Equal(beforeOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(beforeApply, harness.CameraProvider.ApplyCount);

        var exact = await harness.ActivationHistory.ReadAsync(record.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(record.ContentHash, exact.Record!.ContentHash);
        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
    }

    [Fact]
    public async Task V132_G02_InternalFixtureRunsRealPreparationAndStaysOutOfProductionCurrent()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;

        var result = await harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand());

        Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted,
            result.Outcome.ReasonCode);
        Assert.Equal("RecipeActivated", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Succeeded, record.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, record.EvidenceKind);
        Assert.True(record.DevelopmentOnly);
        Assert.False(record.ProductionAuthority);
        Assert.False(record.CanBeActive);
        Assert.NotNull(record.SuccessfulSnapshot);
        Assert.True(record.SuccessfulSnapshot!.DevelopmentOnly);
        Assert.False(record.SuccessfulSnapshot.ProductionAuthority);
        Assert.Equal(record.ResultingRecipe, record.SuccessfulSnapshot.Recipe);
        Assert.All(Enumerable.Range(1, 18), number =>
            Assert.Contains(record.Checks, check =>
                check.CheckId == $"V132.A{number:D2}" && check.Satisfied));
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A12" &&
            check.ReasonCode == "InternalContractFixtureAssumption");
        Assert.True(harness.CameraProvider.OpenCount > beforeOpen);
        Assert.True(harness.CameraProvider.ApplyCount > beforeApply);
        Assert.True(harness.Factory.CreateCalls > 0);
        Assert.True(harness.Factory.WarmUpCalls > 0);

        await harness.WaitForVerifiedAsync();
        var exact = await harness.ActivationHistory.ReadAsync(record.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(record.ContentHash, exact.Record!.ContentHash);
        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
    }

    [Fact]
    public async Task V132_G03_InternalFixtureWarmUpFailurePersistsTerminalBeforeCameraIo()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;
        harness.Factory.FailWarmUp = true;

        var result = await harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand());

        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("AlgorithmWarmUpFailed", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, record.EvidenceKind);
        Assert.True(record.DevelopmentOnly);
        Assert.Null(record.SuccessfulSnapshot);
        Assert.Equal(RecipeActivationRestorationState.NotRequired, record.Restoration.State);
        Assert.Equal("RecipeActivationHardwareUntouched", record.Restoration.ReasonCode);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A06" &&
            check.Status == RecipeActivationCheckStatus.Failed &&
            check.ReasonCode == "AlgorithmWarmUpFailed");
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A08" &&
            check.Status == RecipeActivationCheckStatus.NotRun);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A09" &&
            check.Status == RecipeActivationCheckStatus.NotRun);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A11" &&
            check.Status == RecipeActivationCheckStatus.NotRun);
        Assert.Equal(beforeOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(beforeApply, harness.CameraProvider.ApplyCount);
        Assert.True(harness.Factory.CreateCalls > 0);
        Assert.True(harness.Factory.WarmUpCalls > 0);

        var admissionReference = record.AdmissionReference;
        Assert.NotNull(admissionReference);
        await harness.WaitForVerifiedAsync();
        var admission = await harness.ActivationHistory.ReadAsync(admissionReference!);
        Assert.True(admission.Available, admission.ReasonCode);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, admission.Record!.Outcome.State);
        var exact = await harness.ActivationHistory.ReadAsync(record.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(record.ContentHash, exact.Record!.ContentHash);
        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
    }

    [Fact]
    public async Task V132_G04_InternalFixtureCancellationAfterCandidateTouchSafeClosesWithoutPrevious()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;
        using var cancellation = new CancellationTokenSource();
        harness.CameraProvider.CancelActivationApply(cancellation.Cancel);

        var activation = harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand(), cancellation.Token).AsTask();
        await harness.CameraProvider.ActivationApplyStarted.WaitAsync(TimeSpan.FromSeconds(15));
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("RecipeActivationCancelled", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Cancelled, record.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, record.EvidenceKind);
        Assert.Null(record.PreviousActivation);
        Assert.Null(record.PreviousSnapshotContentHash);
        Assert.Null(record.SuccessfulSnapshot);
        Assert.Equal(RecipeActivationRestorationState.NoPreviousBaselineClosed,
            record.Restoration.State);
        Assert.Equal("NoPreviousBaselineClosed", record.Restoration.ReasonCode);
        var safeClosed = Assert.IsType<CameraSetupSnapshot>(record.Restoration.ActualCamera);
        Assert.Equal(CameraConnectionState.Closed, safeClosed.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unconfigured, safeClosed.Health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, safeClosed.Health.Acquisition);
        Assert.Null(safeClosed.Requested);
        Assert.Null(safeClosed.Effective);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A09" &&
            check.Status == RecipeActivationCheckStatus.Failed &&
            check.ReasonCode == "CameraOperationCancelled");
        Assert.True(harness.CameraProvider.OpenCount > beforeOpen);
        Assert.True(harness.CameraProvider.ApplyCount > beforeApply);
        Assert.True(harness.CameraProvider.ActivationStopCalls > 0);
        Assert.True(harness.CameraProvider.ActivationDisposeCalls > 0);
        Assert.True(harness.CameraProvider.ActivationDeviceDisposed);

        var admissionReference = record.AdmissionReference;
        Assert.NotNull(admissionReference);
        await harness.WaitForVerifiedAsync();
        var admission = await harness.ActivationHistory.ReadAsync(admissionReference!);
        Assert.True(admission.Available, admission.ReasonCode);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, admission.Record!.Outcome.State);
        var exact = await harness.ActivationHistory.ReadAsync(record.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(record.ContentHash, exact.Record!.ContentHash);

        var setup = await harness.Camera.GetSetupAsync("TopCamera", harness.Invocation());
        Assert.True(setup.Available, setup.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", setup.ReasonCode);
        var currentSetup = Assert.IsType<CameraSetupSnapshot>(setup.Snapshot);
        Assert.Equal(CameraConnectionState.Closed, currentSetup.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unconfigured, currentSetup.Health.Configuration);
        Assert.Null(currentSetup.Requested);
        Assert.Null(currentSetup.Effective);

        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
    }

    [Fact]
    public async Task V132_G05_InternalFixtureCandidateFailureRestoresPreviousSnapshotExactly()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var fixtureService = harness.CreateFixtureService();

        var baselineResult = await fixtureService.ActivateAsync(
            await harness.AuthorizedActivationCommand());
        Assert.True(baselineResult.Outcome.Disposition == CommandDisposition.Accepted,
            baselineResult.Outcome.ReasonCode);
        var baseline = Assert.IsType<RecipeActivationRecord>(baselineResult.Record);
        Assert.Equal(RecipeActivationOutcomeState.Succeeded, baseline.Outcome.State);
        var baselineSnapshot = Assert.IsType<RecipeActivationSnapshot>(baseline.SuccessfulSnapshot);
        var baselineCamera = baselineSnapshot.CameraSetup;
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;

        harness.CameraProvider.FailReplacementApply("T32ReplacementApplyFailure");
        var result = await fixtureService.ActivateAsync(
            await harness.AuthorizedActivationCommand(baseline.Reference));

        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("T32ReplacementApplyFailure", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, record.EvidenceKind);
        Assert.Equal(baseline.Reference, record.PreviousActivation);
        Assert.Equal(baselineSnapshot.ContentHash, record.PreviousSnapshotContentHash);
        Assert.Null(record.SuccessfulSnapshot);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A09" &&
            check.Status == RecipeActivationCheckStatus.Failed &&
            check.ReasonCode == "T32ReplacementApplyFailure");
        Assert.Equal(RecipeActivationRestorationState.Restored, record.Restoration.State);
        Assert.Equal("CameraActivationRestored", record.Restoration.ReasonCode);
        var restoredCamera = Assert.IsType<CameraSetupSnapshot>(record.Restoration.ActualCamera);
        Assert.Equal(baselineCamera.LogicalRole, restoredCamera.LogicalRole);
        Assert.Equal(baselineCamera.Binding, restoredCamera.Binding);
        Assert.Equal(baselineCamera.Differences, restoredCamera.Differences);
        Assert.Equal(baselineCamera.Extension, restoredCamera.Extension);
        Assert.Equal(baselineCamera.Capabilities?.ContentHash, restoredCamera.Capabilities?.ContentHash);
        Assert.Equal(baselineCamera.Requested, restoredCamera.Requested);
        Assert.Equal(baselineCamera.Effective, restoredCamera.Effective);
        Assert.Equal(CameraProviderAvailability.Available, restoredCamera.Health.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Open, restoredCamera.Health.Connection);
        Assert.Equal(CameraConfigurationState.Applied, restoredCamera.Health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, restoredCamera.Health.Acquisition);
        Assert.Equal(baselineCamera.Health.LastFault, restoredCamera.Health.LastFault);
        Assert.Equal(RecipeActivationValidation.CameraHash(baselineCamera),
            record.Restoration.RequestedEvidenceHash);
        Assert.Equal(RecipeActivationValidation.CameraHash(restoredCamera),
            record.Restoration.EffectiveEvidenceHash);
        Assert.True(harness.CameraProvider.OpenCount >= beforeOpen + 2);
        Assert.True(harness.CameraProvider.ApplyCount >= beforeApply + 2);
        Assert.True(harness.CameraProvider.ReplacementApplyCalls > 0);
        Assert.True(harness.CameraProvider.ReplacementDeviceDisposed);
        Assert.True(harness.CameraProvider.RestoreApplyCalls > 0);
        Assert.False(harness.CameraProvider.RestoreDeviceDisposed);

        await harness.WaitForVerifiedAsync();
        var exact = await harness.ActivationHistory.ReadAsync(record.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(record.ContentHash, exact.Record!.ContentHash);
        var baselineExact = await harness.ActivationHistory.ReadAsync(baseline.Reference);
        Assert.True(baselineExact.Available, baselineExact.ReasonCode);
        Assert.Equal(baseline.ContentHash, baselineExact.Record!.ContentHash);
        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        var latestSuccess = Assert.Single(page.Records, value =>
            value.Outcome.Succeeded && value.EvidenceKind == RecipeActivationEvidenceKind.InternalContractFixture);
        Assert.Equal(baseline.Reference, latestSuccess.Reference);
        Assert.Equal(baseline.ContentHash, latestSuccess.ContentHash);

        var setup = await harness.Camera.GetSetupAsync("TopCamera", harness.Invocation());
        Assert.True(setup.Available, setup.ReasonCode);
        Assert.Equal("CameraActivationRestored", setup.ReasonCode);
        var currentSetup = Assert.IsType<CameraSetupSnapshot>(setup.Snapshot);
        Assert.Equal(baselineCamera.LogicalRole, currentSetup.LogicalRole);
        Assert.Equal(baselineCamera.Binding, currentSetup.Binding);
        Assert.Equal(baselineCamera.Differences, currentSetup.Differences);
        Assert.Equal(baselineCamera.Extension, currentSetup.Extension);
        Assert.Equal(baselineCamera.Capabilities?.ContentHash, currentSetup.Capabilities?.ContentHash);
        Assert.Equal(baselineCamera.Requested, currentSetup.Requested);
        Assert.Equal(baselineCamera.Effective, currentSetup.Effective);
        Assert.Equal(CameraConnectionState.Open, currentSetup.Health.Connection);
        Assert.Equal(CameraConfigurationState.Applied, currentSetup.Health.Configuration);

        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
    }

    [Fact]
    public async Task V132_G06_InternalFixtureSessionRevocationAfterCandidateAppliedPersistsActorFailure()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;
        var actorSessionId = harness.Sessions.Current.SessionId!.Value;
        var actorPrincipalId = Guid.Parse(harness.Sessions.Current.PrincipalId!);
        var command = await harness.AuthorizedActivationCommand();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.CameraProvider.HoldActivationApply(() => release.Task);
        var activation = harness.CreateFixtureService().ActivateAsync(command).AsTask();

        await harness.CameraProvider.ActivationApplyConfigured.WaitAsync(TimeSpan.FromSeconds(15));
        var logout = await harness.Sessions.LogoutAsync(actorSessionId);
        try
        {
            Assert.True(logout.Succeeded, logout.ReasonCode);
            Assert.True(logout.AuditPersisted);
        }
        finally
        {
            release.TrySetResult(true);
        }
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("SessionMismatch", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        var record = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, record.EvidenceKind);
        Assert.Equal(actorPrincipalId, record.ActorPrincipalId);
        Assert.Equal(actorSessionId, record.ActorSessionId);
        Assert.NotNull(record.ActorAuthorizationRevision);
        Assert.NotNull(record.AuthorizationPolicy);
        Assert.Null(record.PreviousActivation);
        Assert.Null(record.SuccessfulSnapshot);
        Assert.Equal(RecipeActivationRestorationState.NoPreviousBaselineClosed,
            record.Restoration.State);
        Assert.Equal("NoPreviousBaselineClosed", record.Restoration.ReasonCode);
        var safeClosed = Assert.IsType<CameraSetupSnapshot>(record.Restoration.ActualCamera);
        Assert.Equal(CameraConnectionState.Closed, safeClosed.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unconfigured, safeClosed.Health.Configuration);
        Assert.Null(safeClosed.Requested);
        Assert.Null(safeClosed.Effective);
        Assert.Contains(record.Checks, check => check.CheckId == "V132.A09" &&
            check.Status == RecipeActivationCheckStatus.Passed &&
            check.ReasonCode == "CameraActivationPrepared");
        Assert.True(harness.CameraProvider.OpenCount > beforeOpen);
        Assert.True(harness.CameraProvider.ApplyCount > beforeApply);
        Assert.True(harness.CameraProvider.ActivationStopCalls > 0);
        Assert.True(harness.CameraProvider.ActivationDisposeCalls > 0);
        Assert.True(harness.CameraProvider.ActivationDeviceDisposed);

        var admissionReference = record.AdmissionReference;
        Assert.NotNull(admissionReference);
        await harness.WaitForVerifiedAsync();
        var admission = await harness.ActivationHistory.ReadAsync(admissionReference!);
        Assert.True(admission.Available, admission.ReasonCode);
        Assert.Equal(RecipeActivationOutcomeState.Admitted, admission.Record!.Outcome.State);
        var exact = await harness.ActivationHistory.ReadAsync(record.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal(record.ContentHash, exact.Record!.ContentHash);
        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Null(station.ActiveRecipe);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.False(station.Ready);
    }

    private sealed class ActivationHarness : IAsyncDisposable
    {
        private const string TestPassword = "V132 activation integration password 2026!";
        private readonly ServiceProvider _provider;
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _audit;
        private bool _disposed;

        private ActivationHarness(ServiceProvider provider, string directory,
            AuditIntegrityPolicy audit, ProductionStoreOptions options,
            SqliteCommandStore store, LocalIdentityService identity,
            IInteractiveSessionService sessions, LocalAuthorizationService authorization,
            StationRuntime runtime, ICameraSetupRuntime camera,
            RecipeDraftService drafts, IRecipeReleaseService releases,
            IReleasedRecipeQuery releaseHistory, IPlcResultContractService contracts,
            IPlcResultContractQuery contractHistory, IRecipeActivationService activations,
            IRecipeActivationQuery activationHistory, AlgorithmPreparationService preparation,
            AlgorithmPreparationOptions preparationOptions, FrameBufferPool framePool,
            ActivationFactory factory, VirtualCameraProvider cameraProvider,
            RecipeDraftRevision source, ReleasedRecipe released)
        {
            _provider = provider;
            _directory = directory;
            _audit = audit;
            Options = options;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            Runtime = runtime;
            Camera = camera;
            Drafts = drafts;
            Releases = releases;
            ReleaseHistory = releaseHistory;
            Contracts = contracts;
            ContractHistory = contractHistory;
            Activations = activations;
            ActivationHistory = activationHistory;
            Preparation = preparation;
            PreparationOptions = preparationOptions;
            FramePool = framePool;
            Factory = factory;
            CameraProvider = cameraProvider;
            Source = source;
            Released = released;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; }
        internal LocalIdentityService Identity { get; }
        internal IInteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal StationRuntime Runtime { get; }
        internal ICameraSetupRuntime Camera { get; }
        internal RecipeDraftService Drafts { get; }
        internal IRecipeReleaseService Releases { get; }
        internal IReleasedRecipeQuery ReleaseHistory { get; }
        internal IPlcResultContractService Contracts { get; }
        internal IPlcResultContractQuery ContractHistory { get; }
        internal IRecipeActivationService Activations { get; }
        internal IRecipeActivationQuery ActivationHistory { get; }
        internal AlgorithmPreparationService Preparation { get; }
        internal AlgorithmPreparationOptions PreparationOptions { get; }
        internal FrameBufferPool FramePool { get; }
        internal ActivationFactory Factory { get; }
        internal VirtualCameraProvider CameraProvider { get; }
        internal RecipeDraftRevision Source { get; private set; } = null!;
        internal ReleasedRecipe Released { get; private set; } = null!;

        internal CommandInvocation Invocation() => new(CommandSource.Integration,
            Sessions.Current.PrincipalId, Sessions.Current.SessionId);

        internal CommandInvocation ActivationInvocation() => new(CommandSource.PhysicalConsole,
            Sessions.Current.PrincipalId, Sessions.Current.SessionId);

        internal ActivateRecipeCommand ActivationCommand() => new(Guid.NewGuid(),
            ActivationInvocation(), Released.Reference, Released.Record.ReleaseId,
            Released.Record.ContentHash, null, null, "V132 service activation integration");

        internal async Task<ActivateRecipeCommand> AuthorizedActivationCommand(
            RecipeActivationReference? expectedActive = null)
        {
            var baseCommand = ActivationCommand();
            var command = new ActivateRecipeCommand(baseCommand.CorrelationId,
                baseCommand.Invocation, baseCommand.Candidate, baseCommand.ReleaseId,
                baseCommand.ReleaseRecordContentHash, expectedActive,
                baseCommand.CalibrationSelections, baseCommand.ChangeReason,
                baseCommand.OperationId);
            var grant = await GrantAsync(Permission.ActivateRecipe, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ActivateRecipe,
                command.Invocation, TestPassword);
            return command with { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } };
        }

        internal RecipeActivationService CreateFixtureService() => new(Drafts,
            ReleaseHistory, ContractHistory, ActivationHistory, Authorization, Store, Options,
            Preparation, PreparationOptions, FramePool,
            (correlation, token) => Runtime.ReserveRecipeActivationAsync(correlation, token),
            () => Runtime.GetSnapshotAsync(), RecipeActivationInternalFixture.CreateForContractTests());

        internal async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(Store);

        internal static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Audit integrity did not become Verified: " +
                store.Integrity?.ReasonCode);
        }

        internal static async Task<ActivationHarness> CreateAsync(
            AuthorizationPolicy? authorizationPolicy = null, bool enablePreview = false)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Recipe activation integration requires Windows machine-key protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V132-RecipeActivation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V132ActivationStation";
            var audit = new AuditIntegrityPolicy(station, "development-v1",
                "SharpInspect.T32.Activation." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var passwordPolicy = new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v132-activation-blocklist", "1",
                    new[] { "known-compromised-value" })
            };
            var identityOptions = new LocalIdentityOptions(station, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                authorizationPolicy ?? (enablePreview
                    ? CreatePreviewAuthorizationPolicy() : RecipeDraftTestPolicies.Authoring));
            var executionPolicy = new AlgorithmExecutionPolicy("V132.Activation.Execution", "1",
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
            var governance = new RecipeGovernancePolicy("V132.Activation.Release", "1",
                RecipeGovernanceMode.SingleApproverRelease);
            var preparationOptions = new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5));
            var factory = ActivationContract.CreateFactory();
            var cameraProvider = VirtualCameraProvider.Create();
            var options = new ProductionStoreOptions(Path.Combine(directory, "activation.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                AlarmPolicy = enablePreview ? CreatePreviewAlarmPolicy() : null,
                LocalIdentity = identityOptions,
                RecipeDrafts = new RecipeDraftStoreOptions(executionPolicy),
                CameraSetup = new CameraSetupStoreOptions(),
                RecipeReleases = new RecipeReleaseStoreOptions(governance),
                PlcResultContracts = new PlcResultContractStoreOptions(),
                RecipeActivations = new RecipeActivationStoreOptions(),
                PreviewSessions = enablePreview ? new PreviewSessionStoreOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(8),
                QueryTimeout = TimeSpan.FromSeconds(8),
                QueueCapacity = 32
            };

            var services = new ServiceCollection();
            services.AddSingleton<IVisionAlgorithmFactory>(factory);
            services.AddSharpInspectCameraProvider(cameraProvider);
            services.AddSharpInspectCameraSetup(new CameraSetupOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(2),
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            });
            services.AddSharpInspectAlgorithmPreparation(preparationOptions);
            services.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(2, 640 * 480,
                TimeSpan.FromMilliseconds(100)));
            services.AddSingleton<LocalIdentityService>(provider =>
                new LocalIdentityService(provider.GetRequiredService<SqliteCommandStore>(),
                    identityOptions, new FixtureConsole()));
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(50));
            var provider = services.BuildServiceProvider();

            try
            {
                var store = provider.GetRequiredService<SqliteCommandStore>();
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                var identity = provider.GetRequiredService<LocalIdentityService>();
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                var bootstrap = token.Token!.TakeForDisplay();
                token.Token.Dispose();
                await WaitForVerifiedAsync(store);
                const string userName = "v132.activation.admin";
                const string password = TestPassword;
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(station, bootstrap, userName,
                        "T32 Activation Administrator", password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await WaitForVerifiedAsync(store);
                var sessions = provider.GetRequiredService<IInteractiveSessionService>();
                var login = await sessions.SignInAsync(new PasswordSignInRequest(userName, password));
                Assert.True(login.Succeeded, login.ReasonCode);
                await WaitForVerifiedAsync(store);

                var runtime = Assert.IsType<StationRuntime>(provider.GetRequiredService<IStationRuntime>());
                await runtime.WaitForRecipeActivationStartupAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var camera = provider.GetRequiredService<ICameraSetupRuntime>();
                var authorization = provider.GetRequiredService<LocalAuthorizationService>();
                var drafts = Assert.IsType<RecipeDraftService>(
                    provider.GetRequiredService<IRecipeDraftEditor>());
                var releases = provider.GetRequiredService<IRecipeReleaseService>();
                var releaseHistory = provider.GetRequiredService<IReleasedRecipeQuery>();
                var contracts = provider.GetRequiredService<IPlcResultContractService>();
                var contractHistory = provider.GetRequiredService<IPlcResultContractQuery>();
                var activations = provider.GetRequiredService<IRecipeActivationService>();
                var activationHistory = provider.GetRequiredService<IRecipeActivationQuery>();
                var framePool = provider.GetRequiredService<FrameBufferPool>();
                var preparation = provider.GetRequiredService<AlgorithmPreparationService>();
                var harness = new ActivationHarness(provider, directory, audit, options, store,
                    identity, sessions, authorization, runtime, camera, drafts, releases,
                    releaseHistory, contracts, contractHistory, activations, activationHistory,
                    preparation, preparationOptions, framePool, factory, cameraProvider,
                    null!, null!);
                await harness.ConfigureCameraAsync(password);
                var source = await harness.CreateAndReleaseRecipeAsync(password);
                harness.Source = source.Source;
                harness.Released = source.Released;
                return harness;
            }
            catch
            {
                await provider.DisposeAsync();
                TryDelete(directory, audit);
                throw;
            }
        }

        private async Task ConfigureCameraAsync(string password)
        {
            var binding = new CameraBindingTarget(CameraProvider.Identity, "Camera:One");
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                Sessions.Current.PrincipalId, Sessions.Current.SessionId);
            var rebind = new CameraRebindRequest(Guid.NewGuid(), invocation, "TopCamera", 0,
                null, binding, "V132 bind activation camera");
            var rebindGrant = await GrantAsync(Permission.ManageCameraBindings, rebind.OperationId,
                "TopCamera", AuditedCommandKind.RebindCamera, invocation, password);
            var rebound = await Camera.RebindAsync(rebind with
            { Invocation = invocation with { StepUpGrantId = rebindGrant.GrantId } });
            Assert.True(rebound.Succeeded, rebound.ReasonCode);
            var revision = rebound.Snapshot!.Binding!;
            var apply = new CameraDebugConfigurationRequest(Guid.NewGuid(), invocation,
                "TopCamera", revision.Revision, revision.RevisionHash, ActivationContract.Camera,
                "V132 apply activation camera");
            var applyGrant = await GrantAsync(Permission.ManageCameraBindings, apply.OperationId,
                "TopCamera", AuditedCommandKind.ApplyCameraDebugConfiguration, invocation, password);
            var applied = await Camera.ApplyDebugConfigurationAsync(apply with
            { Invocation = invocation with { StepUpGrantId = applyGrant.GrantId } });
            Assert.True(applied.Succeeded, applied.ReasonCode);
        }

        private async Task<(RecipeDraftRevision Source, ReleasedRecipe Released)> CreateAndReleaseRecipeAsync(
            string password)
        {
            var content = ActivationContract.CreateContent(Options);
            var draftOperationId = Guid.NewGuid();
            var draftId = Guid.NewGuid();
            var draftInvocation = Invocation();
            var draftAccess = await Drafts.GetAccessAsync(draftInvocation);
            Assert.True(draftAccess.CanSave, draftAccess.ReasonCode);
            Guid? draftGrantId = null;
            if (draftAccess.RequiresStepUp)
            {
                var draftGrant = await GrantAsync(Permission.EditRecipeDraft, draftOperationId,
                    draftId.ToString("D"), AuditedCommandKind.SaveRecipeDraft,
                    draftInvocation, password);
                draftGrantId = draftGrant.GrantId;
                draftInvocation = draftInvocation with { StepUpGrantId = draftGrantId };
            }
            var saved = await Drafts.SaveAsync(new RecipeDraftSaveRequest(draftOperationId, draftId,
                0, null, content, "V132 create activation candidate", draftInvocation,
                draftGrantId));
            Assert.True(saved.Saved, saved.ReasonCode);
            var source = Assert.IsType<RecipeDraftRevision>(saved.Revision);
            await WaitForVerifiedAsync();

            var command = new ReleaseRecipeCommand(Guid.NewGuid(), Invocation(), source.DraftId,
                source.Revision, source.RevisionContentHash,
                Options.RecipeReleases!.Policy.Reference, "V132 release activation candidate");
            var grant = await GrantAsync(Permission.ReleaseRecipe, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe, Invocation(), password);
            var released = await Runtime.SubmitAsync(command with
            { Invocation = Invocation() with { StepUpGrantId = grant.GrantId } });
            Assert.Equal(CommandDisposition.Accepted, released.Disposition);
            Assert.Equal(AuditPersistence.Persisted, released.Audit);
            await WaitForVerifiedAsync();
            var read = await Releases.QueryAsync(new(PageSize: 20));
            Assert.True(read.Available, read.ReasonCode);
            var recipe = Assert.Single(read.Recipes);

            var contract = PlcResultContractTestSupport.Contract(ActivationContract.ResultSchema);
            var change = new ChangePlcResultContractCommand(Guid.NewGuid(), Invocation(), contract,
                null, "V132 bind activation PLC contract");
            var contractGrant = await GrantAsync(Permission.ManagePlcResultContract,
                change.CorrelationId, change.AuthorizationTarget,
                AuditedCommandKind.ChangePlcResultContract, Invocation(), password);
            var changed = await Runtime.SubmitAsync(change with
            { Invocation = Invocation() with { StepUpGrantId = contractGrant.GrantId } });
            Assert.Equal(CommandDisposition.Accepted, changed.Disposition);
            Assert.Equal(AuditPersistence.Persisted, changed.Audit);
            await WaitForVerifiedAsync();
            var currentContract = await ContractHistory.ReadCurrentAsync();
            Assert.True(currentContract.Available, currentContract.ReasonCode);
            Assert.NotNull(currentContract.Revision);
            return (source, recipe);
        }

        private async Task<StepUpResult> GrantAsync(Permission permission, Guid operationId,
            string target, AuditedCommandKind commandKind, CommandInvocation invocation, string password)
        {
            var grant = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                invocation, new StepUpBinding(permission, operationId, target, commandKind), password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            await WaitForVerifiedAsync();
            return grant;
        }

        private static void TryDelete(string directory, AuditIntegrityPolicy audit)
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(audit);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _provider.DisposeAsync();
            TryDelete(_directory, _audit);
        }

    }

    private static class ActivationContract
    {
        internal static readonly AlgorithmConfigurationSchema ConfigurationSchema =
            new("V132.Activation.Config", "1", new[]
            {
                new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64, "items", true,
                    new(minInt64: 0, maxInt64: 100), AlgorithmScalarValue.FromInt64(5))
            });
        internal static readonly AlgorithmConfigurationSnapshot Configuration =
            AlgorithmConfigurationSnapshot.Create(ConfigurationSchema, new[]
            {
                new AlgorithmConfigurationEntry("Threshold", "items", AlgorithmScalarValue.FromInt64(5))
            });
        internal static readonly OverlayContract Overlay = new("V132.Activation.Overlay", "1");
        internal static readonly AlgorithmResultSchema ResultSchema = new("V132.Activation.Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" }, Overlay);
        internal static readonly AlgorithmIdentity Identity = new("V132.Activation.Algorithm", "1");
        internal static readonly RequestedCameraConfiguration Camera = new(
            ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
            new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 1000, 0, null);

        internal static ActivationFactory CreateFactory() => new(new(Identity, ConfigurationSchema, ResultSchema));

        internal static RecipeDraftContent CreateContent(ProductionStoreOptions options)
        {
            var algorithm = new RecipeAlgorithmBinding(Identity, ConfigurationSchema,
                new RecipeContractReference(ResultSchema.Id, ResultSchema.Version, ResultSchema.ContentHash),
                new RecipeContractReference(Overlay.Id, Overlay.Version, Overlay.ContentHash));
            var execution = options.RecipeDrafts!.ExecutionPolicy;
            var requirements = new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(execution.Id, execution.Version, execution.ContentHash)),
                new RecipePolicyRequirement(RecipePolicyKind.RecipeGovernance,
                    options.RecipeReleases!.Policy.Reference)
            };
            return new RecipeDraftContent("V132.Activation.Recipe", "T32 activation candidate",
                algorithm, Configuration, "TopCamera", Camera, TimeSpan.FromMilliseconds(100),
                null, requirements, partIdentityRequirement: PartIdentityRequirement.None);
        }
    }

    private sealed class ActivationFactory : IVisionAlgorithmFactory
    {
        internal ActivationFactory(AlgorithmDescriptor descriptor) => Descriptor = descriptor;
        private int _createCalls;
        private int _warmUpCalls;
        internal bool FailWarmUp { get; set; }
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal int WarmUpCalls => Volatile.Read(ref _warmUpCalls);
        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _createCalls);
            return ValueTask.FromResult<IVisionAlgorithm>(new ActivationAlgorithm(this));
        }

        private sealed class ActivationAlgorithm : IVisionAlgorithm
        {
            private readonly ActivationFactory _owner;
            internal ActivationAlgorithm(ActivationFactory owner) => _owner = owner;
            public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _owner._warmUpCalls);
                if (_owner.FailWarmUp)
                    throw new InvalidOperationException("T32 activation warm-up failure.");
                return ValueTask.CompletedTask;
            }
            public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("T32 activation fixture does not execute production cycles.");
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed partial class VirtualCameraProvider : ICameraProvider
    {
        private readonly ConcurrentQueue<ICameraDevice> _devices;
        private readonly IReadOnlyList<VirtualCameraDevice> _all;
        private readonly VirtualCameraDevice _activationDevice;
        private readonly VirtualCameraDevice _replacementDevice;
        private readonly VirtualCameraDevice _restoreDevice;
        private int _openCount;

        private VirtualCameraProvider(CameraProviderIdentity identity,
            IReadOnlyList<VirtualCameraDevice> devices)
        {
            Identity = identity;
            _all = devices;
            _activationDevice = devices[2];
            _replacementDevice = devices[3];
            _restoreDevice = devices[4];
            _devices = new ConcurrentQueue<ICameraDevice>(devices);
        }

        internal static VirtualCameraProvider Create()
        {
            var identity = new CameraProviderIdentity("T32.Activation.Camera.Provider", "1",
                "T32.Activation.Camera.Adapter", "1");
            var devices = new[]
            {
                new VirtualCameraDevice(new CameraDeviceDescriptor(identity, "Camera:One", "T32 binding")),
                new VirtualCameraDevice(new CameraDeviceDescriptor(identity, "Camera:One", "T32 setup")),
                new VirtualCameraDevice(new CameraDeviceDescriptor(identity, "Camera:One", "T32 activation")),
                new VirtualCameraDevice(new CameraDeviceDescriptor(identity, "Camera:One", "T32 replacement")),
                new VirtualCameraDevice(new CameraDeviceDescriptor(identity, "Camera:One", "T32 restore"))
            };
            return new(identity, devices);
        }

        public CameraProviderIdentity Identity { get; }
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int ApplyCount => _all.Sum(device => device.ApplyCount);
        internal Task ActivationApplyStarted => _activationDevice.ApplyStarted;
        internal int ActivationStopCalls => _activationDevice.StopCalls;
        internal int ActivationDisposeCalls => _activationDevice.DisposeCalls;
        internal bool ActivationDeviceDisposed => _activationDevice.IsDisposed;
        internal Task ActivationApplyConfigured => _activationDevice.ApplyConfigured;
        internal int ReplacementApplyCalls => _replacementDevice.ApplyCount;
        internal bool ReplacementDeviceDisposed => _replacementDevice.IsDisposed;
        internal int RestoreApplyCalls => _restoreDevice.ApplyCount;
        internal bool RestoreDeviceDisposed => _restoreDevice.IsDisposed;

        internal void CancelActivationApply(Action cancellation) =>
            _activationDevice.SetApplyCallback(cancellation);
        internal void HoldActivationApply(Func<Task> continuation) =>
            _activationDevice.SetApplyContinuation(continuation);
        internal void FailReplacementApply(string reason) =>
            _replacementDevice.SetApplyFailure(reason);

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(_all.Select(device => device.Descriptor)));

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCount);
            return _devices.TryDequeue(out var device)
                ? ValueTask.FromResult(CameraOpenResult.Success(device))
                : ValueTask.FromResult(CameraOpenResult.Failure("T32VirtualCameraExhausted"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed partial class VirtualCameraDevice : ICameraDevice, ICameraPreviewDevice
    {
        private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
        private readonly TaskCompletionSource<bool> _applyStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _applyConfigured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _applyCount;
        private int _stopCalls;
        private int _disposeCalls;
        private Action? _applyCallback;
        private Func<Task>? _applyContinuation;
        private string? _applyFailureReason;
        private bool _disposed;

        internal VirtualCameraDevice(CameraDeviceDescriptor descriptor) => Descriptor = descriptor;
        internal CameraDeviceDescriptor Descriptor { get; }
        internal int ApplyCount => Volatile.Read(ref _applyCount);
        internal Task ApplyStarted => _applyStarted.Task;
        internal Task ApplyConfigured => _applyConfigured.Task;
        internal int StopCalls => Volatile.Read(ref _stopCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal bool IsDisposed => _disposed;
        internal void SetApplyCallback(Action callback) => _applyCallback = callback;
        internal void SetApplyContinuation(Func<Task> continuation) => _applyContinuation = continuation;
        internal void SetApplyFailure(string reason) => _applyFailureReason = reason;
        CameraDeviceDescriptor ICameraDevice.Descriptor => Descriptor;
        CameraCapabilities ICameraDevice.Capabilities => Capabilities;

        private static CameraCapabilities Capabilities { get; } = new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(640, 480,
                new CameraIntCapability(0, 636, 4), new CameraIntCapability(0, 476, 4),
                new CameraIntCapability(4, 640, 4), new CameraIntCapability(4, 480, 4)));

        public CameraHealthSnapshot GetHealthSnapshot() => new(
            CameraProviderAvailability.Available, _disposed ? CameraConnectionState.Closed :
                CameraConnectionState.Open, _disposed ? CameraConfigurationState.Unconfigured :
                _configuration, GetPreviewAcquisitionState(),
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())));

        public async ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _applyCount);
            _applyStarted.TrySetResult(true);
            var callback = Interlocked.Exchange(ref _applyCallback, null);
            if (callback is not null)
            {
                callback();
                return CameraConfigurationResult.Failure("CameraOperationCancelled");
            }
            if (_disposed) return CameraConfigurationResult.Failure("T32VirtualCameraClosed");
            var failure = Interlocked.Exchange(ref _applyFailureReason, null);
            if (failure is not null)
                return CameraConfigurationResult.Failure(failure);
            var result = Capabilities.ValidateConfiguration(requested);
            if (!result.Succeeded) return result;
            _configuration = CameraConfigurationState.Applied;
            _applyConfigured.TrySetResult(true);
            var continuation = Interlocked.Exchange(ref _applyContinuation, null);
            if (continuation is not null)
                await continuation().ConfigureAwait(false);
            return result;
        }

        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Failure("T32ActivationStartNotUsed"));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted,
                    "T32ActivationAcquireNotUsed")));

        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            if (FailDispose)
                return ValueTask.FromResult(CameraOperationResult.Failure("T33VirtualCameraStopFailure"));
            StopPreviewState();
            _configuration = CameraConfigurationState.Unconfigured;
            return ValueTask.FromResult(CameraOperationResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (FailDispose)
                throw new InvalidOperationException("T33VirtualCameraDisposeFailure");
            DisposePreviewState();
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-T32-Activation");
    }
}
