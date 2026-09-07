using System.Globalization;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AuthenticationPolicyTests
{
    [Fact]
    public void V105_C01_DevelopmentPolicyAndSafetyCeilingsAreExplicit()
    {
        var policy = AuthenticationPolicy.Development;

        Assert.Equal("development", policy.Id);
        Assert.Equal("development-2026-09", policy.Version);
        Assert.Equal(10, policy.AccountFailureLimit);
        Assert.Equal(50, policy.StationFailureLimit);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.InitialDelay);
        Assert.Equal(TimeSpan.FromMinutes(15), policy.MaximumDelay);
        Assert.Equal(TimeSpan.FromMinutes(15), policy.SessionIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.StepUpFreshness);
        policy.Validate();

        Assert.Throws<ArgumentOutOfRangeException>(() => (policy with { AccountFailureLimit = 101 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (policy with { StationFailureLimit = 101 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { InitialDelay = TimeSpan.FromMilliseconds(999) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { InitialDelay = TimeSpan.FromMinutes(6) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { MaximumDelay = TimeSpan.FromMilliseconds(999) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { MaximumDelay = TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { SessionIdleTimeout = TimeSpan.FromSeconds(59) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { SessionIdleTimeout = TimeSpan.FromHours(1).Add(TimeSpan.FromTicks(1)) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { StepUpFreshness = TimeSpan.FromSeconds(16 * 60) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (policy with { StepUpFreshness = TimeSpan.FromMinutes(6), SessionIdleTimeout = TimeSpan.FromMinutes(5) }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { Id = new string('i', 129) }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { Version = " " }).Validate());
    }

    [Fact]
    public void V105_C02_DelayGrowsExponentiallyAndSaturatesWithoutOverflow()
    {
        var policy = AuthenticationPolicy.Development with { MaximumDelay = TimeSpan.FromSeconds(10) };

        Assert.Equal(TimeSpan.Zero, policy.DelayForFailures(0));
        Assert.Equal(TimeSpan.FromSeconds(1), policy.DelayForFailures(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayForFailures(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayForFailures(3));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.DelayForFailures(4));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayForFailures(5));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayForFailures(6));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayForFailures(int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.DelayForFailures(-1));
    }

    [Fact]
    public void V105_C03_ContentHashIsStableCultureIndependentAndCoversEveryField()
    {
        var policy = AuthenticationPolicy.Development;
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

        var variants = new[]
        {
            policy with { Id = "other" },
            policy with { Version = "other" },
            policy with { AccountFailureLimit = 9 },
            policy with { StationFailureLimit = 49 },
            policy with { InitialDelay = TimeSpan.FromSeconds(2) },
            policy with { MaximumDelay = TimeSpan.FromMinutes(14) },
            policy with { SessionIdleTimeout = TimeSpan.FromMinutes(14) },
            policy with { StepUpFreshness = TimeSpan.FromMinutes(4) }
        };

        Assert.All(variants, variant => Assert.NotEqual(expected, variant.ContentHash));
    }
}
