using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationGovernancePermissionTests
{
    [Fact]
    public void V127_C01_NewPermissionsAreStableAndDoNotRewriteDevelopmentPolicy()
    {
        Assert.Equal(11, (int)Permission.PublishCalibration);
        Assert.Equal(33, (int)Permission.ManageCalibrationAcceptancePolicy);
        Assert.Equal(34, (int)Permission.RecordPhysicalCalibrationVerification);

        var policy = AuthorizationPolicy.Development;

        Assert.Equal("D7202FE18DE53788D5CC9A8E898F796E345CB5B2EBB265E5615C669282825C50",
            policy.ContentHash);
        Assert.All(policy.RoleBundles.Values, permissions =>
        {
            Assert.DoesNotContain(Permission.ManageCalibrationAcceptancePolicy, permissions);
            Assert.DoesNotContain(Permission.RecordPhysicalCalibrationVerification, permissions);
        });
        Assert.Equal(LegacyStepUpPermissions(), policy.StepUpPermissions);
        Assert.DoesNotContain(Permission.ManageCalibrationAcceptancePolicy, policy.StepUpPermissions);
        Assert.DoesNotContain(Permission.RecordPhysicalCalibrationVerification, policy.StepUpPermissions);
        Assert.True(policy.RequiresStepUp(Permission.ManageCalibrationAcceptancePolicy));
        Assert.True(policy.RequiresStepUp(Permission.RecordPhysicalCalibrationVerification));
    }

    [Theory]
    [InlineData(Permission.ManageCalibrationAcceptancePolicy)]
    [InlineData(Permission.RecordPhysicalCalibrationVerification)]
    public void V127_C02_ExplicitRoleAssignmentAddsMandatoryStepUp(Permission permission)
    {
        var roles = LegacyRoleBundles();
        roles[HumanRoleBundle.Technician] = roles[HumanRoleBundle.Technician].Append(permission);

        var policy = new AuthorizationPolicy("calibration-governance", "1", roles);

        Assert.Contains(permission, policy.GetPermissions(HumanRoleBundle.Technician));
        Assert.Equal(LegacyStepUpPermissions().Append(permission).OrderBy(value => value),
            policy.StepUpPermissions);
        Assert.True(policy.RequiresStepUp(permission));
    }

    [Fact]
    public void V127_C03_OldOverridesRemainStableAndExplicitNewOverridesAreRecorded()
    {
        var legacy = new AuthorizationPolicy("legacy", "1", LegacyRoleBundles());
        var withOldOverrides = new AuthorizationPolicy("legacy", "1", LegacyRoleBundles(),
            LegacyStepUpPermissions());
        var withExplicitNewPermissions = new AuthorizationPolicy("legacy", "1", LegacyRoleBundles(),
            new[]
            {
                Permission.ManageCalibrationAcceptancePolicy,
                Permission.RecordPhysicalCalibrationVerification
            });

        Assert.Equal(legacy.ContentHash, withOldOverrides.ContentHash);
        Assert.Equal(legacy.StepUpPermissions, withOldOverrides.StepUpPermissions);
        Assert.Equal(LegacyStepUpPermissions()
                .Concat(new[]
                {
                    Permission.ManageCalibrationAcceptancePolicy,
                    Permission.RecordPhysicalCalibrationVerification
                })
                .OrderBy(value => value),
            withExplicitNewPermissions.StepUpPermissions);
        Assert.NotEqual(legacy.ContentHash, withExplicitNewPermissions.ContentHash);
        Assert.True(withExplicitNewPermissions.RequiresStepUp(Permission.ManageCalibrationAcceptancePolicy));
        Assert.True(withExplicitNewPermissions.RequiresStepUp(Permission.RecordPhysicalCalibrationVerification));
    }

    private static Dictionary<HumanRoleBundle, IEnumerable<Permission>> LegacyRoleBundles()
        => AuthorizationPolicy.Development.RoleBundles.ToDictionary(
            pair => pair.Key,
            pair => (IEnumerable<Permission>)pair.Value.ToArray());

    private static Permission[] LegacyStepUpPermissions()
        => new[]
        {
            Permission.ManageAccounts,
            Permission.ManagePermissions,
            Permission.UnlockCredential,
            Permission.RebindCredential,
            Permission.ReleaseRecipe,
            Permission.RetireRecipe,
            Permission.ManageRecipeSelectionMap,
            Permission.ManagePlcResultContract,
            Permission.PublishCalibration,
            Permission.SelectHistoricalCalibration,
            Permission.ManageCameraBindings,
            Permission.ManageProductionPolicy,
            Permission.ManageRecipeTrustStore,
            Permission.ManageRecipeSigningKeys,
            Permission.ManageDeploymentTrustStore,
            Permission.ManageBackupPolicy,
            Permission.UpgradeDeployment,
            Permission.RestoreStation,
            Permission.RunStationQualification,
            Permission.ApproveStationProductionAcceptance,
            Permission.RunDiagnostics,
            Permission.ExportProtectedDiagnostics,
            Permission.ManualRecovery,
            Permission.ManageAuditSigningKeys,
            Permission.CorrectHistoricalFact,
            Permission.DeleteEvidence,
            Permission.ResetAlarm
        };
}
