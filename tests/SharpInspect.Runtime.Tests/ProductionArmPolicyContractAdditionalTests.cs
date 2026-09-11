using SharpInspect.Abstractions;
using SharpInspect.Runtime.Production;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Additional T47 declaration and input-stability checks. These are in-process contract
/// checks: they select no deployment, arm no Runtime, assert no Ready and produce no
/// production, maintenance or audit evidence.
/// </summary>
public sealed class ProductionArmPolicyContractAdditionalTests
{
    private static ProductionPolicyDocument Document(string id) => new(id, "1", "V147 " + id + " policy.");

    private static ProductionDeploymentManifest LegacyManifest() => new("V147.Legacy.Deployment", "1",
        Document("Logging"), Document("Diagnostics"), Document("Backup"), Document("Startup"),
        Document("Performance"), Document("Conformance"), Document("UiWorkload"), Array.Empty<string>());

    private static ProductionDeploymentManifest ArmingManifest(StartupProductionPolicy? startup,
        PostActivationArmPolicy? post) => new("V147.Legacy.Deployment", "1",
        Document("Logging"), Document("Diagnostics"), Document("Backup"), Document("Startup"),
        Document("Performance"), Document("Conformance"), Document("UiWorkload"), Array.Empty<string>(),
        startup ?? StartupProductionPolicy.Default, post ?? PostActivationArmPolicy.Default);

    private static StartupProductionPolicy AutomaticStartup(string id = "V147.StartupProduction") =>
        new(id, "1", StartupProductionMode.AutomaticArm);

    private static PostActivationArmPolicy AutomaticRearm(string id = "V147.PostActivationArm") =>
        new(id, "1", PostActivationArmMode.AutomaticRearmAfterPlcActivation);

    [Fact]
    public void V147_A06_EachArmingChoiceChangesTheDeploymentFingerprintIndependently()
    {
        var legacy = LegacyManifest();
        Assert.False(legacy.HasExplicitArmingPolicies);
        Assert.Equal(StartupProductionPolicy.Default.Reference, legacy.StartupProduction.Reference);
        Assert.Equal(PostActivationArmPolicy.Default.Reference, legacy.PostActivationArm.Reference);
        Assert.Equal(StartupProductionMode.ManualArm, legacy.StartupProduction.Mode);
        Assert.Equal(PostActivationArmMode.ManualRearm, legacy.PostActivationArm.Mode);

        var explicitDefaults = ArmingManifest(null, null);
        Assert.True(explicitDefaults.HasExplicitArmingPolicies);
        Assert.Equal(StartupProductionPolicy.Default.Reference, explicitDefaults.StartupProduction.Reference);
        Assert.Equal(PostActivationArmPolicy.Default.Reference, explicitDefaults.PostActivationArm.Reference);
        // Declaring the framework defaults explicitly is already a different deployment fingerprint.
        Assert.NotEqual(legacy.ContentHash, explicitDefaults.ContentHash);

        var startupOnly = ArmingManifest(AutomaticStartup(), null);
        var postOnly = ArmingManifest(null, AutomaticRearm());
        var both = ArmingManifest(AutomaticStartup(), AutomaticRearm());
        Assert.NotEqual(explicitDefaults.ContentHash, startupOnly.ContentHash);
        Assert.NotEqual(explicitDefaults.ContentHash, postOnly.ContentHash);
        // The two choices are separate: neither change implies the other.
        Assert.NotEqual(startupOnly.ContentHash, postOnly.ContentHash);
        Assert.NotEqual(startupOnly.ContentHash, both.ContentHash);
        Assert.NotEqual(postOnly.ContentHash, both.ContentHash);
        // The same declarations always produce the same fingerprint.
        Assert.Equal(both.ContentHash, ArmingManifest(AutomaticStartup(), AutomaticRearm()).ContentHash);
        Assert.Matches("^[0-9A-F]{64}$", both.ContentHash);
        Assert.Equal(StartupProductionMode.AutomaticArm, both.StartupProduction.Mode);
        Assert.Equal(PostActivationArmMode.AutomaticRearmAfterPlcActivation, both.PostActivationArm.Mode);
        // Frozen fingerprints: the legacy ten-argument construction keeps the historic v1
        // value, and each arming declaration moves the v2 fingerprint on its own.
        Assert.Equal("AC519047CB9AA5543BAA0AC21CC64B114B8A95A890FB89769E3985532829F266",
            legacy.ContentHash);
        Assert.Equal("94B0623E859085DF97A9560CB4AE2141EAC7D1A194170C3203C9FCF21EA882D0",
            explicitDefaults.ContentHash);
        Assert.Equal("A8E00E2A7E20F129BCF32C95C0384ECBDC966A8F8E0F6C7B3159783F3B0599F0",
            startupOnly.ContentHash);
        Assert.Equal("4F27F7D9FAAF356A121557FA86FF58105580491408B4295D6F058DB441FB7C14",
            postOnly.ContentHash);
        Assert.Equal("DF8E33111F28D484F037FE1F081FC04D3727B1C176E18638C09327D0E4394845",
            both.ContentHash);
    }
}

/// <summary>
/// Focused T47 fresh-window checks for one arm attempt. Historical or cached observations and
/// interrupted sample streams can never complete the window; only a fresh, uninterrupted,
/// strictly newer sequence of clear samples can.
/// </summary>
public sealed class ProductionArmInputStabilityContractTests
{
    private const long Frequency = TimeSpan.TicksPerSecond;

    private static PlcCommunicationPolicy Policy() => new("V147.Stability", "1",
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(1));

    private static long WindowTicks(PlcCommunicationPolicy policy) =>
        (long)Math.Ceiling(policy.SynchronizationStabilityWindow.TotalSeconds * Frequency);

    [Fact]
    public void V147_B01_RepeatedReadsOfOneCachedSampleCannotCompleteTheWindow()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var stability = new ProductionArmInputStability(policy, 0, 1, Frequency);
        stability.Observe(2, window / 2, true);
        Assert.False(stability.IsStable(window * 4));
        // Re-reading the same sample (same sequence, same time) changes nothing.
        stability.Observe(2, window * 5, true);
        Assert.False(stability.IsStable(window * 6));
        // Two strictly newer clear samples do complete it.
        stability.Observe(3, window * 6, true);
        stability.Observe(4, window * 7, true);
        Assert.True(stability.IsStable(window * 7));
    }

    [Fact]
    public void V147_B02_SamplesFromBeforeTheAttemptStartCannotOpenTheWindow()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var stability = new ProductionArmInputStability(policy, window * 2, 5, Frequency);
        stability.Observe(6, 0, true);
        stability.Observe(7, window * 2, true);
        Assert.False(stability.IsStable(window * 3));
        // The first genuinely fresh clear sample starts the window, not the old history.
        stability.Observe(8, window * 3, true);
        stability.Observe(9, window * 4, true);
        Assert.True(stability.IsStable(window * 4));
    }

    [Fact]
    public void V147_B03_OneHighPulseRestartsTheWholeWindow()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var stability = new ProductionArmInputStability(policy, 0, 1, Frequency);
        stability.Observe(2, window, true);
        stability.Observe(3, window * 2, true);
        Assert.True(stability.IsStable(window * 2));
        stability.Observe(4, window * 2 + window / 2, false);
        Assert.False(stability.IsStable(window * 2 + window / 2));
        // Clear again, but the accumulated window is gone and must be rebuilt.
        stability.Observe(5, window * 3, true);
        Assert.False(stability.IsStable(window * 3));
        stability.Observe(6, window * 4, true);
        Assert.True(stability.IsStable(window * 4));
    }

    [Fact]
    public void V147_B04_AGapBeyondTheMaximumSampleGapRestartsTheWholeWindow()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var gap = (long)Math.Ceiling((policy.OperationTimeout + policy.PollInterval).TotalSeconds * Frequency);
        var stability = new ProductionArmInputStability(policy, 0, 1, Frequency);
        stability.Observe(2, window, true);
        var resumed = window + gap + 1;
        stability.Observe(3, resumed, true);
        Assert.False(stability.IsStable(resumed));
        // Half a window after the stale gap is still not a fresh window.
        Assert.False(stability.IsStable(resumed + window / 2));
        stability.Observe(4, resumed + window, true);
        Assert.True(stability.IsStable(resumed + window));
    }
}
