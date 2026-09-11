using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AuthorizationPolicyTests
{
    [Fact]
    public void V106_C01_PermissionsAndSystemPrincipalsHaveStableBoundaries()
    {
        Assert.Equal(0, (int)Permission.None);
        Assert.Equal(1, (int)Permission.ManageAccounts);
        Assert.Equal(28, (int)Permission.DeleteEvidence);
        Assert.Equal(1, (int)SystemPermission.RecordCommand);
        Assert.Equal(5, (int)SystemPermission.CleanupRetention);
        Assert.Equal(7, (int)SystemPermission.RecordProductionInspection);
        Assert.Equal(8, (int)SystemPermission.RequestMappedRecipeActivation);
        Assert.Equal(9, (int)SystemPermission.AttemptPolicyControlledProductionArm);

        Assert.Equal("SharpInspect.Runtime", SystemPrincipalId.Runtime);
        Assert.Equal("SharpInspect.Outbox", SystemPrincipalId.Outbox);
        Assert.Equal("SharpInspect.EvidenceFinalizer", SystemPrincipalId.EvidenceFinalizer);
        Assert.Equal("SharpInspect.EvidenceScrubber", SystemPrincipalId.EvidenceScrubber);
        Assert.Equal("SharpInspect.RetentionCleanup", SystemPrincipalId.RetentionCleanup);

        Assert.Equal(6, SystemPrincipalCatalog.All.Count);
        Assert.Equal(SystemPrincipalId.Runtime, SystemPrincipalCatalog.Runtime.Id);
        Assert.Equal(new[] { SystemPermission.RecordCommand, SystemPermission.RecordProductionInspection,
            SystemPermission.AttemptPolicyControlledProductionArm },
            SystemPrincipalCatalog.Runtime.Permissions);
        Assert.Equal(new[] { SystemPermission.DeliverOutbox },
            SystemPrincipalCatalog.Outbox.Permissions);
        Assert.Equal(new[] { SystemPermission.FinalizeEvidence },
            SystemPrincipalCatalog.EvidenceFinalizer.Permissions);
        Assert.Equal(new[] { SystemPermission.ScrubEvidence },
            SystemPrincipalCatalog.EvidenceScrubber.Permissions);
        Assert.Equal(new[] { SystemPermission.CleanupRetention },
            SystemPrincipalCatalog.RetentionCleanup.Permissions);
        Assert.Equal("SharpInspect.PlcAdapter", SystemPrincipalCatalog.PlcAdapter.Id);
        Assert.Equal(new[] { SystemPermission.RecordPlcCommunication, SystemPermission.RequestMappedRecipeActivation },
            SystemPrincipalCatalog.PlcAdapter.Permissions);
        Assert.All(SystemPrincipalCatalog.All.Where(principal => principal.Id != SystemPrincipalId.PlcAdapter),
            principal => Assert.DoesNotContain(SystemPermission.RequestMappedRecipeActivation, principal.Permissions));
        Assert.DoesNotContain(SystemPrincipalCatalog.All,
            principal => principal.Permissions.Contains(SystemPermission.None));
        Assert.All(SystemPrincipalCatalog.All.Where(principal => principal.Id != SystemPrincipalId.Runtime),
            principal => Assert.DoesNotContain(SystemPermission.RecordProductionInspection, principal.Permissions));
        Assert.All(SystemPrincipalCatalog.All.Where(principal => principal.Id != SystemPrincipalId.Runtime),
            principal => Assert.DoesNotContain(SystemPermission.AttemptPolicyControlledProductionArm, principal.Permissions));
    }

    [Fact]
    public void V106_C02_DevelopmentRolesAreExplicitAndStepUpIsFineGrained()
    {
        var policy = AuthorizationPolicy.Development;

        Assert.Equal("development", policy.Id);
        Assert.Equal("development-2026-09", policy.Version);
        Assert.Equal(16, AuthorizationPolicy.MaxHumanAccounts);
        Assert.Equal(3, policy.RoleBundles.Count);
        Assert.Contains(Permission.ArmProduction,
            policy.GetPermissions(HumanRoleBundle.Operator));
        Assert.Contains(Permission.ActivateRecipe,
            policy.GetPermissions(HumanRoleBundle.Operator));
        Assert.Contains(Permission.ManagePermissions,
            policy.GetPermissions(HumanRoleBundle.Administrator));
        Assert.True(policy.RequiresStepUp(Permission.ManageAccounts));
        Assert.True(policy.RequiresStepUp(Permission.DeleteEvidence));
        Assert.False(policy.RequiresStepUp(Permission.ArmProduction));
        Assert.False(policy.RequiresStepUp(Permission.ActivateRecipe));
        policy.Validate();
    }

    [Fact]
    public void V106_C03_MandatoryStepUpCannotBeDisabledAndOptionalActionsCanBeTightened()
    {
        var policy = CreatePolicy(
            new[] { Permission.ArmProduction, Permission.ActivateRecipe },
            new[] { Permission.ArmProduction, Permission.ActivateRecipe });

        Assert.True(policy.RequiresStepUp(Permission.ManagePermissions));
        Assert.True(policy.RequiresStepUp(Permission.ArmProduction));
        Assert.True(policy.RequiresStepUp(Permission.ActivateRecipe));
        Assert.Contains(Permission.ManagePermissions, policy.StepUpPermissions);
        Assert.Contains(Permission.ArmProduction, policy.StepUpPermissions);
        Assert.Contains(Permission.ActivateRecipe, policy.StepUpPermissions);

        Assert.Throws<ArgumentException>(() => CreatePolicy(
            new[] { Permission.None }, Array.Empty<Permission>()));
        Assert.Throws<ArgumentException>(() => CreatePolicy(
            new[] { (Permission)999 }, Array.Empty<Permission>()));
    }

    [Fact]
    public void V106_C04_PolicyCollectionsAreDefensiveAndReadOnly()
    {
        var operatorPermissions = new List<Permission> { Permission.ArmProduction };
        var roleBundles = NewRoleBundles(operatorPermissions);
        var stepUp = new List<Permission> { Permission.ArmProduction };
        var policy = new AuthorizationPolicy("custom", "v1", roleBundles, stepUp);

        operatorPermissions.Add(Permission.DeleteEvidence);
        roleBundles[HumanRoleBundle.Operator] = new[] { Permission.DeleteEvidence };
        stepUp.Clear();

        Assert.DoesNotContain(Permission.DeleteEvidence,
            policy.GetPermissions(HumanRoleBundle.Operator));
        Assert.Contains(Permission.ArmProduction, policy.StepUpPermissions);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<Permission>)policy.GetPermissions(HumanRoleBundle.Operator))[0] = Permission.DeleteEvidence);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<HumanRoleBundle, IReadOnlyList<Permission>>)policy.RoleBundles)
                .Add(HumanRoleBundle.Operator, Array.Empty<Permission>()));
    }

    [Fact]
    public void V106_C05_ContentHashIsStableAndCoversAllPolicyBehavior()
    {
        var policy = AuthorizationPolicy.Development;
        var expected = policy.ContentHash;
        Assert.Equal(64, expected.Length);
        Assert.Equal(expected, policy.ContentHash);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, policy.ContentHash);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        var reorderedRoleBundles = new Dictionary<HumanRoleBundle, IEnumerable<Permission>>
        {
            [HumanRoleBundle.Administrator] = policy.GetPermissions(HumanRoleBundle.Administrator).ToArray(),
            [HumanRoleBundle.Operator] = policy.GetPermissions(HumanRoleBundle.Operator).ToArray(),
            [HumanRoleBundle.Technician] = policy.GetPermissions(HumanRoleBundle.Technician).ToArray()
        };
        Assert.Equal(expected, new AuthorizationPolicy(
            policy.Id, policy.Version, reorderedRoleBundles, policy.StepUpPermissions).ContentHash);
        Assert.NotEqual(expected, CreatePolicy(
            policy.GetPermissions(HumanRoleBundle.Operator),
            policy.StepUpPermissions,
            id: "other").ContentHash);
        Assert.NotEqual(expected, CreatePolicy(
            policy.GetPermissions(HumanRoleBundle.Operator),
            policy.StepUpPermissions,
            version: "other").ContentHash);
        Assert.NotEqual(expected, CreatePolicy(
            policy.GetPermissions(HumanRoleBundle.Operator),
            new[] { Permission.ArmProduction }).ContentHash);
        Assert.NotEqual(expected, CreatePolicy(
            new[] { Permission.DeleteEvidence },
            policy.StepUpPermissions).ContentHash);
    }

    [Fact]
    public void V106_C06_RoleShapeAndIdentifiersAreValidated()
    {
        var complete = NewRoleBundles(new[] { Permission.ArmProduction });
        Assert.Throws<ArgumentException>(() =>
            new AuthorizationPolicy("", "v1", complete));
        Assert.Throws<ArgumentException>(() =>
            new AuthorizationPolicy("id", "", complete));

        var missingRole = new Dictionary<HumanRoleBundle, IEnumerable<Permission>>
        {
            [HumanRoleBundle.Operator] = new[] { Permission.ArmProduction },
            [HumanRoleBundle.Technician] = new[] { Permission.ArmProduction }
        };
        Assert.Throws<ArgumentException>(() =>
            new AuthorizationPolicy("id", "v1", missingRole));

        var extraRole = NewRoleBundles(new[] { Permission.ArmProduction });
        extraRole[(HumanRoleBundle)99] = Array.Empty<Permission>();
        Assert.Throws<ArgumentException>(() =>
            new AuthorizationPolicy("id", "v1", extraRole));
    }

    private static AuthorizationPolicy CreatePolicy(
        IEnumerable<Permission> operatorPermissions,
        IEnumerable<Permission> stepUpPermissions,
        string id = "custom",
        string version = "v1")
    {
        var permissions = NewRoleBundles(operatorPermissions);
        return new AuthorizationPolicy(id, version, permissions, stepUpPermissions);
    }

    private static Dictionary<HumanRoleBundle, IEnumerable<Permission>> NewRoleBundles(
        IEnumerable<Permission> operatorPermissions)
        => new()
        {
            [HumanRoleBundle.Operator] = operatorPermissions.ToArray(),
            [HumanRoleBundle.Technician] = new[] { Permission.ReleaseRecipe },
            [HumanRoleBundle.Administrator] = new[] { Permission.ManagePermissions }
        };
}
