using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.PartIdentity;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(PartIdentityRequirementMode.Required)]
    [InlineData(PartIdentityRequirementMode.Optional)]
    public async Task V143_R12_SourceRestartAfterFinalDeploymentCaptureFailsActivationBeforeWriterClaim(
        PartIdentityRequirementMode requirementMode)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged,
            new string('A', 64));
        var provider = new StagedPartIdentityProvider(binding);
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true,
            productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(requirementMode, binding),
            partIdentityStore: new PartIdentityStoreOptions(),
            configureAdditionalServices: services =>
                services.AddSingleton<IPartIdentityProvider>(provider));

        using var issuer = new ProductionTestIssuer();
        var station = Assert.IsType<StationRuntime>(harness.Runtime);
        station.ConfigureProductionInspectionQualificationEvidenceProvider(issuer);
        var registry = harness.Service<PartIdentityBindingRegistry>();
        var finalCaptureEntered = new TaskCompletionSource<RecipeActivationDeploymentEvidence?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalCapture = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var evidenceCalls = 0;

        var activationService = new RecipeActivationService(
            harness.Service<RecipeDraftService>(),
            harness.Service<IReleasedRecipeQuery>(),
            harness.Service<IPlcResultContractQuery>(),
            harness.Activations,
            harness.Fixture.Authorization,
            harness.Fixture.Store,
            harness.Fixture.Options,
            harness.Service<AlgorithmPreparationService>(),
            harness.Service<AlgorithmPreparationOptions>(),
            harness.Service<FrameBufferPool>(),
            (correlation, token) => station.ReserveRecipeActivationAsync(correlation, token),
            () => station.GetSnapshotAsync(),
            deploymentEvidence: async (candidate, token) =>
            {
                var evidence = await station.CaptureRecipeActivationDeploymentEvidenceAsync(
                    candidate, token).ConfigureAwait(false);
                if (evidence is not null && Interlocked.Increment(ref evidenceCalls) == 2)
                {
                    // The second capture is the final deployment observation. Holding
                    // after it returns gives the test an exact window before the
                    // writer's synchronous Runtime/registry commit fence.
                    finalCaptureEntered.TrySetResult(evidence);
                    await releaseFinalCapture.Task.WaitAsync(TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);
                }
                return evidence;
            },
            partIdentities: registry);

        var command = await PrepareActivationFenceCommandAsync(harness);
        var activation = activationService.ActivateAsync(command).AsTask();
        try
        {
            var finalEvidence = await finalCaptureEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(finalEvidence);
            Assert.NotNull(finalEvidence!.PartIdentity);

            var beforeRestart = registry.RevisionFor(provider);
            provider.RestartSource();
            Assert.True(registry.RevisionFor(provider) > beforeRestart);
            releaseFinalCapture.TrySetResult(true);

            var result = await activation.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
            Assert.Equal("PartIdentitySourceChangedBeforeCommit", result.Outcome.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);

            var record = Assert.IsType<RecipeActivationRecord>(result.Record);
            Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
            Assert.Equal("PartIdentitySourceChangedBeforeCommit", record.Outcome.ReasonCode);
            Assert.Null(record.SuccessfulSnapshot);

            await harness.Fixture.WaitForVerifiedAsync();
            var page = await harness.Activations.QueryAsync(new(PageSize: 128));
            Assert.True(page.Available, page.ReasonCode);
            Assert.Contains(page.Records, value => value.Reference == record.Reference &&
                value.Outcome.State == RecipeActivationOutcomeState.Failed &&
                value.Outcome.ReasonCode == "PartIdentitySourceChangedBeforeCommit");
            Assert.DoesNotContain(page.Records, value => value.Reference == record.Reference &&
                value.Outcome.State == RecipeActivationOutcomeState.Succeeded);

            var current = await harness.Activations.ReadCurrentAsync();
            Assert.True(current.Available, current.ReasonCode);
            Assert.Null(current.Record);
            var stationState = await harness.Runtime.GetSnapshotAsync();
            Assert.Null(stationState.ActiveRecipe);
        }
        finally
        {
            releaseFinalCapture.TrySetResult(true);
            if (!activation.IsCompleted)
            {
                try { await activation.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception) { }
            }
        }
    }

    private static async Task<ActivateRecipeCommand> PrepareActivationFenceCommandAsync(
        ManualHarness harness)
    {
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        var fixture = harness.Fixture;
        var invocation = harness.Invocation();
        var release = new ReleaseRecipeCommand(Guid.NewGuid(), invocation, harness.Draft.DraftId,
            harness.Draft.Revision, harness.Draft.RevisionContentHash,
            fixture.Options.RecipeReleases!.Policy.Reference,
            "V143 release part identity activation fence candidate");
        var releaseGrant = await ProductionGrantAsync(harness, Permission.ReleaseRecipe,
            release.CorrelationId, release.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var released = await harness.Service<IRecipeReleaseService>().ReleaseAsync(release with
        { Invocation = invocation with { StepUpGrantId = releaseGrant } });
        AssertAccepted(released.Outcome, "Release part identity activation fence candidate");
        var recipe = Assert.IsType<ReleasedRecipe>(released.Recipe);

        var contract = PlcResultContractTestSupport.Contract(harness.Factory.Descriptor.ResultSchema,
            frameworkReasons: PlcResultContract.ProductionFailureReasonCatalogV2);
        var change = new ChangePlcResultContractCommand(Guid.NewGuid(), invocation, contract, null,
            "V143 bind part identity activation fence PLC contract");
        var contractGrant = await ProductionGrantAsync(harness, Permission.ManagePlcResultContract,
            change.CorrelationId, change.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract);
        AssertAccepted(await runtime.SubmitAsync(change with
        { Invocation = invocation with { StepUpGrantId = contractGrant } }),
            "Bind part identity activation fence PLC contract");
        await WaitProductionAsync(harness, state => state.Recovery == RecoveryState.None &&
            state.PlcCommunication is { Healthy: true }, "Activation fence production startup");

        var activate = new ActivateRecipeCommand(Guid.NewGuid(), invocation, recipe.Reference,
            recipe.Record.ReleaseId, recipe.Record.ContentHash, null, null,
            "V143 activate part identity fence candidate");
        var activationGrant = await ProductionGrantAsync(harness, Permission.ActivateRecipe,
            activate.CorrelationId, activate.AuthorizationTarget, AuditedCommandKind.ActivateRecipe);
        return activate with { Invocation = invocation with { StepUpGrantId = activationGrant } };
    }
}
