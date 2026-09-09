#pragma warning disable CA1416

using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Verifies the schema-16 release dependency against the real published calibration
/// policy ledger.  A policy publication is sufficient for the release dependency;
/// it does not manufacture a calibration candidate, Profile, physical verification,
/// or a production activation capability.
/// </summary>
public sealed class RecipeReleaseCalibrationDependencyTests
{
    [Fact]
    public async Task V130_R14_PublishedCalibrationPolicySatisfiesRecipeReleaseWithoutStationQualification()
    {
        var releasePolicy = new RecipeGovernancePolicy("V130.Calibration.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, withCalibrationGovernance: true,
            recipeReleases: new RecipeReleaseStoreOptions(releasePolicy));
        await fixture.WaitForHealthySourceAsync();

        var policy = fixture.GovernancePolicy!;
        var publishCommand = new PublishCalibrationAcceptancePolicyCommand(
            Guid.NewGuid(), fixture.User.Invocation, policy, null,
            "V130 publish calibration policy for recipe dependency");
        var publishInvocation = await fixture.GrantAsync(
            Permission.ManageCalibrationAcceptancePolicy, publishCommand.CorrelationId,
            publishCommand.AuthorizationTarget,
            AuditedCommandKind.PublishCalibrationAcceptancePolicy);
        var published = await fixture.Runtime.PublishPolicyAsync(
            publishCommand with { Invocation = publishInvocation });
        Assert.Equal(CommandDisposition.Accepted, published.Outcome.Disposition);
        Assert.Equal("CalibrationAcceptancePolicyPublished", published.Outcome.ReasonCode);
        Assert.NotNull(published.Revision);
        var publishedRevision = published.Revision!;

        var exactPolicy = await fixture.Runtime.ReadPolicyAsync(
            publishedRevision.Policy.Reference, fixture.User.Invocation);
        Assert.True(exactPolicy.Available, exactPolicy.ReasonCode);
        Assert.Equal(publishedRevision.ContentHash, exactPolicy.Value!.ContentHash);
        Assert.Equal(CalibrationPolicyApplicability.Required,
            exactPolicy.Value.Policy.PhysicalVerification.Applicability);

        var governance = await fixture.Store.ReadCalibrationGovernanceAsync();
        Assert.True(governance.Available, governance.ReasonCode);
        Assert.Single(governance.Records);
        Assert.Equal(CalibrationGovernanceCodec.AcceptancePolicyPublished,
            governance.Records[0].Kind);

        var factory = new ReleaseAlgorithmFactory();
        var content = CreateRecipeContent(fixture, factory.Descriptor, publishedRevision.Policy);
        await using var drafts = new RecipeDraftService(new[] { factory }, fixture.Options,
            fixture.Authorization, new SqliteRecipeDraftQuery(fixture.Options));
        var access = await drafts.GetAccessAsync(fixture.User.Invocation);
        Assert.True(access.CanSave, access.ReasonCode);

        var draftId = Guid.NewGuid();
        var saved = await drafts.SaveAsync(new RecipeDraftSaveRequest(
            Guid.NewGuid(), draftId, 0, null, content,
            "V130 recipe binds the published calibration acceptance policy",
            fixture.User.Invocation));
        Assert.True(saved.Saved, saved.ReasonCode);
        Assert.NotNull(saved.Revision);
        var source = saved.Revision!;
        Assert.Equal(publishedRevision.Policy.Reference,
            source.Content.CalibrationRequirements.Single().AcceptancePolicy);
        Assert.Equal(publishedRevision.Policy.CoefficientContract,
            source.Content.CalibrationRequirements.Single().CoefficientContract);

        var releases = new RecipeReleaseService(drafts, fixture.Authorization,
            new SqliteReleasedRecipeQuery(fixture.Options), fixture.Options,
            () => fixture.Runtime.GetSnapshotAsync());
        fixture.Runtime.ConfigureRecipeReleaseService(releases);
        var releaseCommand = new ReleaseRecipeCommand(
            Guid.NewGuid(), fixture.User.Invocation, source.DraftId, source.Revision,
            source.RevisionContentHash, releasePolicy.Reference,
            "V130 release recipe with published calibration dependency");
        var releaseInvocation = await fixture.GrantAsync(
            Permission.ReleaseRecipe, releaseCommand.CorrelationId,
            releaseCommand.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var releaseOutcome = await fixture.Runtime.SubmitAsync(
            releaseCommand with { Invocation = releaseInvocation });
        Assert.Equal(CommandDisposition.Accepted, releaseOutcome.Disposition);
        Assert.Equal("RecipeReleased", releaseOutcome.ReasonCode);

        await CalibrationSessionRuntimeTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var history = await releases.QueryAsync(new ReleasedRecipeFilter(content.RecipeKey));
        Assert.True(history.Available, history.ReasonCode);
        var released = Assert.Single(history.Recipes);
        var calibrationGate = Assert.Single(released.Record.Checks,
            check => check.GateId == "CalibrationDependency");
        Assert.True(calibrationGate.Passed, calibrationGate.ReasonCode);
        Assert.Equal(publishedRevision.Policy.Reference, calibrationGate.Contract);

        var coldRead = await new SqliteReleasedRecipeQuery(fixture.Options)
            .ReadAsync(released.Reference);
        Assert.True(coldRead.Available, coldRead.ReasonCode);
        Assert.Equal(released.Record.ContentHash, coldRead.Recipe!.Record.ContentHash);

        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.Equal(ExclusiveMode.None, snapshot.Mode);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
        Assert.False(snapshot.Ready);
        Assert.Null(snapshot.ActiveRecipe);

        var finalGovernance = await fixture.Store.ReadCalibrationGovernanceAsync();
        Assert.True(finalGovernance.Available, finalGovernance.ReasonCode);
        Assert.Single(finalGovernance.Records);
        Assert.Equal(CalibrationGovernanceCodec.AcceptancePolicyPublished,
            finalGovernance.Records[0].Kind);
    }

    private static RecipeDraftContent CreateRecipeContent(
        CalibrationSessionRuntimeTests.Fixture fixture, AlgorithmDescriptor descriptor,
        CalibrationAcceptancePolicy policy)
    {
        var execution = fixture.Options.RecipeDrafts!.ExecutionPolicy;
        var configuration = AlgorithmConfigurationSnapshot.Create(
            descriptor.ConfigurationSchema,
            new[]
            {
                new AlgorithmConfigurationEntry("Threshold", "items",
                    AlgorithmScalarValue.FromInt64(5))
            });
        var executionReference = new RecipeContractReference(
            execution.Id, execution.Version, execution.ContentHash);
        var requirements = new[]
        {
            new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                executionReference),
            new RecipePolicyRequirement(RecipePolicyKind.RecipeGovernance,
                fixture.Options.RecipeReleases!.Policy.Reference)
        };
        var calibration = new CalibrationRequirement("TopCamera", policy.Kind,
            policy.LogicalPurpose, policy.CoefficientContract, policy.Reference);
        return new RecipeDraftContent("V130.Calibration.Recipe",
            "V130 calibration dependent recipe", RecipeAlgorithmBinding.FromDescriptor(descriptor),
            configuration, "TopCamera",
            new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                100, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8,
                null, 1000, 0, null),
            TimeSpan.FromMilliseconds(100), null, requirements,
            calibrationRequirements: new[] { calibration });
    }

    private sealed class ReleaseAlgorithmFactory : IVisionAlgorithmFactory
    {
        internal ReleaseAlgorithmFactory()
        {
            var schema = new AlgorithmConfigurationSchema("V130.Calibration.Recipe.Config", "1",
                new[]
                {
                    new AlgorithmFieldDefinition("Threshold", AlgorithmScalarType.Int64,
                        "items", true,
                        new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                        AlgorithmScalarValue.FromInt64(5))
                });
            var overlay = new OverlayContract("V130.Calibration.Recipe.Overlay", "1",
                0, 64, 16);
            var result = new AlgorithmResultSchema("V130.Calibration.Recipe.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" }, overlay);
            Descriptor = new AlgorithmDescriptor(
                new AlgorithmIdentity("V130.Calibration.Recipe.Algorithm", "1"),
                schema, result);
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
            throw new InvalidOperationException("V130ReleaseMustNotCreateAlgorithm");
    }
}

#pragma warning restore CA1416
