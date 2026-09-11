using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T47 startup and post-activation arming policy declaration checks. These are in-process
/// contract checks only: they select no deployment, arm no Runtime, assert no Ready and produce no
/// production, maintenance or audit evidence.
/// </summary>
public sealed class ProductionArmingPolicyContractTests
{
    private const string StartupId = "Station.StartupProduction";
    private const string StartupVersion = "v1";
    private const string RearmId = "Station.PostActivationArm";
    private const string RearmVersion = "v1";

    [Fact]
    public void V147_A01_BothPoliciesDefaultToManualAndAreSelectedIndependently()
    {
        Assert.Equal(StartupProductionMode.ManualArm, StartupProductionPolicy.Default.Mode);
        Assert.Equal(PostActivationArmMode.ManualRearm, PostActivationArmPolicy.Default.Mode);

        var automaticStartup = new StartupProductionPolicy(StartupId, StartupVersion,
            StartupProductionMode.AutomaticArm);
        var automaticRearm = new PostActivationArmPolicy(RearmId, RearmVersion,
            PostActivationArmMode.AutomaticRearmAfterPlcActivation);

        Assert.Equal(StartupProductionMode.AutomaticArm, automaticStartup.Mode);
        Assert.Equal(PostActivationArmMode.AutomaticRearmAfterPlcActivation, automaticRearm.Mode);
        // Declaring automatic arming in one policy never moves either default away from manual.
        Assert.Equal(StartupProductionMode.ManualArm, StartupProductionPolicy.Default.Mode);
        Assert.Equal(PostActivationArmMode.ManualRearm, PostActivationArmPolicy.Default.Mode);
        // The two declarations are separate identities, not one shared policy.
        Assert.NotEqual(StartupProductionPolicy.Default.Id, PostActivationArmPolicy.Default.Id);
        Assert.NotEqual(StartupProductionPolicy.Default.ContentHash,
            PostActivationArmPolicy.Default.ContentHash);
    }

    [Fact]
    public void V147_A02_HashAndReferenceAreDeterministicAndExactlyBound()
    {
        var startup = new StartupProductionPolicy(StartupId, StartupVersion,
            StartupProductionMode.AutomaticArm);
        var sameStartup = new StartupProductionPolicy(StartupId, StartupVersion,
            StartupProductionMode.AutomaticArm);
        Assert.Equal(startup.ContentHash, sameStartup.ContentHash);
        Assert.Matches("^[0-9A-F]{64}$", startup.ContentHash);
        Assert.Equal(startup.Reference, sameStartup.Reference);
        Assert.Equal(new RecipeContractReference(StartupId, StartupVersion, startup.ContentHash),
            startup.Reference);

        var rearm = new PostActivationArmPolicy(RearmId, RearmVersion,
            PostActivationArmMode.AutomaticRearmAfterPlcActivation);
        var sameRearm = new PostActivationArmPolicy(RearmId, RearmVersion,
            PostActivationArmMode.AutomaticRearmAfterPlcActivation);
        Assert.Equal(rearm.ContentHash, sameRearm.ContentHash);
        Assert.Matches("^[0-9A-F]{64}$", rearm.ContentHash);
        Assert.Equal(rearm.Reference, sameRearm.Reference);
        Assert.Equal(new RecipeContractReference(RearmId, RearmVersion, rearm.ContentHash),
            rearm.Reference);

        // Two declarations, two independent content identities.
        Assert.NotEqual(startup.ContentHash, rearm.ContentHash);
    }

    [Fact]
    public void V147_A03_ChangedIdVersionOrModeChangesTheHashOfEachPolicy()
    {
        var startup = new StartupProductionPolicy(StartupId, StartupVersion,
            StartupProductionMode.ManualArm);
        Assert.NotEqual(startup.ContentHash, new StartupProductionPolicy("Station.StartupProduction.Other",
            StartupVersion, StartupProductionMode.ManualArm).ContentHash);
        Assert.NotEqual(startup.ContentHash, new StartupProductionPolicy(StartupId, "v2",
            StartupProductionMode.ManualArm).ContentHash);
        Assert.NotEqual(startup.ContentHash, new StartupProductionPolicy(StartupId, StartupVersion,
            StartupProductionMode.AutomaticArm).ContentHash);

        var rearm = new PostActivationArmPolicy(RearmId, RearmVersion, PostActivationArmMode.ManualRearm);
        Assert.NotEqual(rearm.ContentHash, new PostActivationArmPolicy("Station.PostActivationArm.Other",
            RearmVersion, PostActivationArmMode.ManualRearm).ContentHash);
        Assert.NotEqual(rearm.ContentHash, new PostActivationArmPolicy(RearmId, "v2",
            PostActivationArmMode.ManualRearm).ContentHash);
        Assert.NotEqual(rearm.ContentHash, new PostActivationArmPolicy(RearmId, RearmVersion,
            PostActivationArmMode.AutomaticRearmAfterPlcActivation).ContentHash);
    }

    [Fact]
    public void V147_A04_MalformedOrMissingIdsAndVersionsAreRejectedForBothPolicies()
    {
        var malformed = new[]
        {
            "", " ", "Station/Startup", "Station@Startup", "Station Startup", new string('a', 65)
        };
        foreach (var invalid in malformed)
        {
            Assert.Throws<ArgumentException>(() =>
                new StartupProductionPolicy(invalid, StartupVersion, StartupProductionMode.ManualArm));
            Assert.Throws<ArgumentException>(() =>
                new StartupProductionPolicy(StartupId, invalid, StartupProductionMode.ManualArm));
            Assert.Throws<ArgumentException>(() =>
                new PostActivationArmPolicy(invalid, RearmVersion, PostActivationArmMode.ManualRearm));
            Assert.Throws<ArgumentException>(() =>
                new PostActivationArmPolicy(RearmId, invalid, PostActivationArmMode.ManualRearm));
        }

        Assert.Throws<ArgumentNullException>(() =>
            new StartupProductionPolicy(null!, StartupVersion, StartupProductionMode.ManualArm));
        Assert.Throws<ArgumentNullException>(() =>
            new StartupProductionPolicy(StartupId, null!, StartupProductionMode.ManualArm));
        Assert.Throws<ArgumentNullException>(() =>
            new PostActivationArmPolicy(null!, RearmVersion, PostActivationArmMode.ManualRearm));
        Assert.Throws<ArgumentNullException>(() =>
            new PostActivationArmPolicy(RearmId, null!, PostActivationArmMode.ManualRearm));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void V147_A05_UndefinedModesAreRejectedForBothPolicies(int rawMode)
    {
        Assert.Throws<ArgumentException>(() => new StartupProductionPolicy(StartupId, StartupVersion,
            (StartupProductionMode)rawMode));
        Assert.Throws<ArgumentException>(() => new PostActivationArmPolicy(RearmId, RearmVersion,
            (PostActivationArmMode)rawMode));
    }
}
