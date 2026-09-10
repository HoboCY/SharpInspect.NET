using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V134_S01_IndependentReadersVerifySchema20WithCompleteConfiguration()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true);
        await harness.WaitForVerifiedAsync();
        var audit = await new SqliteAuditIntegrityQuery(harness.Options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verified, audit.State);
        var activations = await new SqliteRecipeActivationQuery(harness.Options).QueryAsync(new(PageSize: 20));
        Assert.True(activations.Available, activations.ReasonCode);
        var previews = await new SqlitePreviewSessionQuery(harness.Options).QueryAsync(new(PageSize: 20));
        Assert.True(previews.Available, previews.ReasonCode);
        var trace = await new SqliteCommandTraceQuery(harness.Options).QueryAsync(new(PageSize: 20));
        Assert.NotEmpty(trace.Records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V134_S02_ImportConfigurationCannotBeOmittedOrReboundByIndependentReader(bool changedRoot)
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true);
        await harness.WaitForVerifiedAsync();
        var original = harness.Options;
        var changed = new ProductionStoreOptions(original.DatabasePath)
        {
            AuditIntegrityPolicy = original.AuditIntegrityPolicy,
            AlarmPolicy = original.AlarmPolicy,
            LocalIdentity = original.LocalIdentity,
            RecipeDrafts = original.RecipeDrafts,
            CameraSetup = original.CameraSetup,
            CameraRecovery = original.CameraRecovery,
            CameraNetwork = original.CameraNetwork,
            ImagingSetup = original.ImagingSetup,
            CalibrationSessions = original.CalibrationSessions,
            CalibrationGovernance = original.CalibrationGovernance,
            RecipeReleases = original.RecipeReleases,
            PlcResultContracts = original.PlcResultContracts,
            RecipeActivations = original.RecipeActivations,
            PreviewSessions = original.PreviewSessions,
            CalibrationImports = changedRoot ? new CalibrationImportStoreOptions
            {
                Artifacts = new CalibrationTransferArtifactOptions
                {
                    ArtifactRoot = Path.Combine(original.CalibrationImports!.Artifacts.ArtifactRoot, "different-root")
                }
            } : null
        };
        var audit = await new SqliteAuditIntegrityQuery(changed).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        var activations = await new SqliteRecipeActivationQuery(changed).QueryAsync(new(PageSize: 20));
        Assert.False(activations.Available);
        var previews = await new SqlitePreviewSessionQuery(changed).QueryAsync(new(PageSize: 20));
        Assert.False(previews.Available);
        var traceFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(changed).QueryAsync(new(PageSize: 20)).AsTask());
        Assert.Equal(changedRoot ? "CalibrationImportConfigurationMismatch" :
            "CalibrationImportConfigurationRequired", traceFailure.Message);
        // Failed read-only attempts do not invalidate the correctly configured store.
        var retained = await new SqliteAuditIntegrityQuery(original).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verified, retained.State);
        Assert.NotEmpty((await new SqliteCommandTraceQuery(original).QueryAsync(new(PageSize: 20))).Records);
    }
}
