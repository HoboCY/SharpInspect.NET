using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V134_G09_LegacyStoreRequiresExplicitHistoricalSelectionConfiguration()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var command = CreateHistoricalCommand(harness);
        var result = await harness.Runtime.SubmitAsync(command);
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal("HistoricalCalibrationConfigurationRequired", result.ReasonCode);
        Assert.Equal(AuditPersistence.NotAttempted, result.Audit);
        var stepUp = await harness.Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
            command.Invocation, new StepUpBinding(Permission.SelectHistoricalCalibration, command.OperationId,
                command.AuthorizationTarget, AuditedCommandKind.SelectHistoricalCalibration),
            "V132 activation integration password 2026!"));
        Assert.False(stepUp.Succeeded);
        Assert.Equal("HistoricalCalibrationConfigurationRequired", stepUp.ReasonCode);
        await harness.WaitForVerifiedAsync();
    }

    [Fact]
    public async Task V134_G01_HistoricalSelectionRequiresSeparatePermissionAndLeavesCameraUntouched()
    {
        await using var harness = await ActivationHarness.CreateAsync(
            CreateHistoricalSelectionPolicy(includeSelectionPermission: false), enableImports: true);
        var command = CreateHistoricalCommand(harness);
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;

        var result = await harness.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal("PermissionDenied", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        await harness.WaitForVerifiedAsync();
        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 50));
        Assert.True(page.Available, page.ReasonCode);
        var record = Assert.Single(page.Records, value => value.OperationId == command.OperationId);
        var trace = await new SqliteCommandTraceQuery(harness.Options).QueryAsync(
            new(CorrelationId: command.CorrelationId, PageSize: 20));
        Assert.Contains(trace.Records, value => value.CommandKind == AuditedCommandKind.SelectHistoricalCalibration);
        Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
        Assert.NotNull(record.HistoricalSelection);
        Assert.Equal(command.HistoricalSelection, record.HistoricalSelection);
        Assert.Equal(beforeOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(beforeApply, harness.CameraProvider.ApplyCount);
    }

    [Fact]
    public async Task V134_G02_HistoricalSelectionUsesFreshStepUpAndPersistsV2Intent()
    {
        await using var harness = await ActivationHarness.CreateAsync(
            CreateHistoricalSelectionPolicy(includeSelectionPermission: true), enableImports: true);
        var command = CreateHistoricalCommand(harness);
        var grant = await IssueGrantAsync(harness, command,
            Permission.SelectHistoricalCalibration, AuditedCommandKind.SelectHistoricalCalibration);
        command = command with
        {
            Invocation = command.Invocation with { StepUpGrantId = grant.GrantId }
        };
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;

        var result = await harness.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal("HistoricalCalibrationPreviousProfileRequired", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        await harness.WaitForVerifiedAsync();
        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 50));
        Assert.True(page.Available, page.ReasonCode);
        var record = Assert.Single(page.Records, value => value.OperationId == command.OperationId);
        Assert.Equal(RecipeActivationOutcomeState.Failed, record.Outcome.State);
        Assert.Equal(command.HistoricalSelection, record.HistoricalSelection);
        Assert.Equal(command.AuthorizationTarget, record.AuthorizationTarget);
        Assert.Equal(beforeOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(beforeApply, harness.CameraProvider.ApplyCount);
    }

    [Fact]
    public async Task V134_G03_OrdinaryActivationCannotChangeAnActiveCalibrationSelection()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var first = await harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand());
        var active = Assert.IsType<RecipeActivationRecord>(first.Record);
        Assert.Equal(RecipeActivationOutcomeState.Succeeded, active.Outcome.State);
        Assert.NotNull(active.SuccessfulSnapshot);
        Assert.Empty(active.SuccessfulSnapshot!.CalibrationBindings);

        var seed = harness.ActivationCommand();
        var selection = new CalibrationProfileSelection(new string('A', 64),
            new CalibrationProfileReference(Guid.NewGuid(), 1, new string('B', 64)));
        var changed = new ActivateRecipeCommand(seed.CorrelationId, seed.Invocation, seed.Candidate,
            seed.ReleaseId, seed.ReleaseRecordContentHash, active.Reference, new[] { selection },
            seed.ChangeReason, seed.OperationId);
        var grant = await IssueGrantAsync(harness, changed,
            Permission.ActivateRecipe, AuditedCommandKind.ActivateRecipe);
        changed = changed with
        {
            Invocation = changed.Invocation with { StepUpGrantId = grant.GrantId }
        };
        var beforeOpen = harness.CameraProvider.OpenCount;
        var beforeApply = harness.CameraProvider.ApplyCount;

        var result = await harness.CreateFixtureService().ActivateAsync(changed);

        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("HistoricalCalibrationSelectionRequired", result.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        Assert.NotNull(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Failed, result.Record!.Outcome.State);
        Assert.Null(result.Record.SuccessfulSnapshot);
        var retained = await harness.ActivationHistory.ReadAsync(active.Reference);
        Assert.True(retained.Available, retained.ReasonCode);
        Assert.Equal(active.ContentHash, retained.Record!.ContentHash);
        Assert.Equal(beforeOpen, harness.CameraProvider.OpenCount);
        Assert.Equal(beforeApply, harness.CameraProvider.ApplyCount);
    }

    [Fact]
    public void V134_G04_CrossRecipeOrdinaryCalibrationChangeRequiresHistoricalRoute()
    {
        var profiles = CreateHistoricalProfiles();
        var previous = new[] { Binding(RequirementA, profiles.Previous) };
        var command = CreateOrdinarySelectionCommand(
            new[] { Selection(RequirementA, profiles.Replacement.Reference) },
            "recipe-two");

        var failure = LocalAuthorizationService.ValidateHistoricalCalibrationBindings(
            command, previous);

        Assert.Equal("HistoricalCalibrationSelectionRequired", failure);
        Assert.Null(command.HistoricalSelection);
        Assert.Equal("recipe-two", command.Candidate.Id);
    }

    [Fact]
    public void V134_G05_HistoricalRouteRejectsMultipleCalibrationReplacements()
    {
        var profiles = CreateHistoricalProfiles();
        var previous = new[]
        {
            Binding(RequirementA, profiles.Previous),
            Binding(RequirementB, profiles.Replacement)
        };
        var command = CreateHistoricalSelectionCommand(
            new[]
            {
                Selection(RequirementA, profiles.Replacement.Reference),
                Selection(RequirementB, profiles.Previous.Reference)
            }, profiles.Previous.Reference);

        var failure = LocalAuthorizationService.ValidateHistoricalCalibrationBindings(
            command, previous);

        Assert.Equal("HistoricalCalibrationSingleReplacementRequired", failure);
    }

    [Fact]
    public void V134_G06_HistoricalRouteRejectsPriorProfileForAnotherRequirement()
    {
        var profiles = CreateHistoricalProfiles();
        var previous = new[]
        {
            Binding(RequirementA, profiles.Previous),
            Binding(RequirementB, profiles.Replacement)
        };
        var command = CreateHistoricalSelectionCommand(
            new[]
            {
                Selection(RequirementA, profiles.Replacement.Reference),
                Selection(RequirementB, profiles.Replacement.Reference)
            }, profiles.Replacement.Reference);

        var failure = LocalAuthorizationService.ValidateHistoricalCalibrationBindings(
            command, previous);

        Assert.Equal("HistoricalCalibrationPreviousProfileConflict", failure);
    }

    [Fact]
    public void V134_G07_HistoricalRouteAllowsOneReplacementAndRetainsUnchangedBinding()
    {
        var profiles = CreateHistoricalProfiles();
        var previous = new[]
        {
            Binding(RequirementA, profiles.Previous),
            Binding(RequirementB, profiles.Replacement)
        };
        var command = CreateHistoricalSelectionCommand(
            new[]
            {
                Selection(RequirementA, profiles.Replacement.Reference),
                Selection(RequirementB, profiles.Replacement.Reference)
            }, profiles.Previous.Reference);

        var failure = LocalAuthorizationService.ValidateHistoricalCalibrationBindings(
            command, previous);

        Assert.Null(failure);
        Assert.Equal(2, command.CalibrationSelections.Count);
        var retained = Assert.Single(command.CalibrationSelections,
            value => value.RequirementContentHash == RequirementB);
        Assert.Equal(profiles.Replacement.Reference, retained.Profile);
    }

    [Fact]
    public void V134_G08_HistoricalRouteRejectsDroppingAnUnchangedBinding()
    {
        var profiles = CreateHistoricalProfiles();
        var previous = new[]
        {
            Binding(RequirementA, profiles.Previous),
            Binding(RequirementB, profiles.Replacement)
        };
        var command = CreateHistoricalSelectionCommand(
            new[] { Selection(RequirementA, profiles.Replacement.Reference) },
            profiles.Previous.Reference);

        var failure = LocalAuthorizationService.ValidateHistoricalCalibrationBindings(
            command, previous);

        Assert.Equal("HistoricalCalibrationSingleReplacementRequired", failure);
    }

    private const string RequirementA =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string RequirementB =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static (PublishedCalibrationProfileVersion Previous,
        PublishedCalibrationProfileVersion Replacement) CreateHistoricalProfiles()
    {
        return (CalibrationExportPackageTests.ExportFixture.Create().Profile,
            CalibrationValidityTests.Fixture.Create().Profile);
    }

    private static CalibrationRunProfileBinding Binding(string requirement,
        PublishedCalibrationProfileVersion profile) =>
        new(requirement, profile, null, null);

    private static CalibrationProfileSelection Selection(string requirement,
        CalibrationProfileReference profile) => new(requirement, profile);

    private static ActivateRecipeCommand CreateOrdinarySelectionCommand(
        IReadOnlyList<CalibrationProfileSelection> selections, string recipeId)
    {
        var correlationId = Guid.NewGuid();
        return new ActivateRecipeCommand(correlationId,
            new CommandInvocation(CommandSource.Integration, "historical-selection-test",
                Guid.NewGuid()),
            new RecipeReference(recipeId, "1", HashFor(recipeId)), Guid.NewGuid(),
            HashFor("release"), null, selections, "V134 historical selection boundary",
            correlationId);
    }

    private static SelectHistoricalCalibrationCommand CreateHistoricalSelectionCommand(
        IReadOnlyList<CalibrationProfileSelection> selections,
        CalibrationProfileReference previousExactProfile)
    {
        var correlationId = Guid.NewGuid();
        var reason = "V134 historical selection boundary";
        var intent = new HistoricalCalibrationSelectionIntent(
            HistoricalCalibrationSelectionSources.LocalProfileHistory,
            previousExactProfile, reason);
        return new SelectHistoricalCalibrationCommand(correlationId,
            new CommandInvocation(CommandSource.Integration, "historical-selection-test",
                Guid.NewGuid()),
            new RecipeReference("historical-recipe", "1", HashFor("historical-recipe")),
            Guid.NewGuid(), HashFor("release"), null, selections, intent, correlationId);
    }

    private static string HashFor(string value) => value switch
    {
        "recipe-two" =>
            "2222222222222222222222222222222222222222222222222222222222222222",
        "historical-recipe" =>
            "3333333333333333333333333333333333333333333333333333333333333333",
        _ => "4444444444444444444444444444444444444444444444444444444444444444"
    };

    private static SelectHistoricalCalibrationCommand CreateHistoricalCommand(ActivationHarness harness)
    {
        var seed = harness.ActivationCommand();
        var selection = new CalibrationProfileSelection(new string('A', 64),
            new CalibrationProfileReference(Guid.NewGuid(), 1, new string('B', 64)));
        var historical = new HistoricalCalibrationSelectionIntent(
            HistoricalCalibrationSelectionSources.LocalProfileHistory, null, seed.ChangeReason);
        return new SelectHistoricalCalibrationCommand(seed.CorrelationId, seed.Invocation, seed.Candidate,
            seed.ReleaseId, seed.ReleaseRecordContentHash, seed.ExpectedActive, new[] { selection },
            historical, seed.OperationId);
    }

    private static AuthorizationPolicy CreateHistoricalSelectionPolicy(bool includeSelectionPermission)
    {
        var source = RecipeDraftTestPolicies.Authoring;
        var roles = source.RoleBundles.ToDictionary(pair => pair.Key,
            pair => (IEnumerable<Permission>)(includeSelectionPermission
                ? pair.Value.Append(Permission.SelectHistoricalCalibration).Distinct()
                : pair.Value.Where(permission => permission != Permission.SelectHistoricalCalibration)));
        return new AuthorizationPolicy("t34-historical-selection", "t34-historical-selection-v1",
            roles, source.StepUpPermissions);
    }

    private static async Task<StepUpResult> IssueGrantAsync(ActivationHarness harness,
        ActivateRecipeCommand command, Permission permission, AuditedCommandKind commandKind)
    {
        var grant = await harness.Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
            command.Invocation, new StepUpBinding(permission, command.OperationId,
                command.AuthorizationTarget, commandKind),
            "V132 activation integration password 2026!"));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        await harness.WaitForVerifiedAsync();
        return grant;
    }

}
