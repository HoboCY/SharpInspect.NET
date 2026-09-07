using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlgorithmExecutionPolicyTests
{
    [Fact]
    public void V113_P01_ContentHashIsStableAndCoversIdentityAndAllDurations()
    {
        var baseline = Policy();
        var same = Policy();
        Assert.Equal(baseline.ContentHash, same.ContentHash);
        Assert.Equal(64, baseline.ContentHash.Length);
        Assert.Matches("^[0-9A-F]{64}$", baseline.ContentHash);
        Assert.NotEqual(baseline.ContentHash,
            new AlgorithmExecutionPolicy("other", baseline.Version, baseline.MinimumExecutionTimeout,
                baseline.MaximumExecutionTimeout, baseline.CancellationGracePeriod).ContentHash);
        Assert.NotEqual(baseline.ContentHash,
            new AlgorithmExecutionPolicy(baseline.Id, baseline.Version, TimeSpan.FromMilliseconds(101),
                baseline.MaximumExecutionTimeout, baseline.CancellationGracePeriod).ContentHash);
        Assert.NotEqual(baseline.ContentHash,
            new AlgorithmExecutionPolicy(baseline.Id, baseline.Version, baseline.MinimumExecutionTimeout,
                TimeSpan.FromMilliseconds(501), baseline.CancellationGracePeriod).ContentHash);
        Assert.NotEqual(baseline.ContentHash,
            new AlgorithmExecutionPolicy(baseline.Id, baseline.Version, baseline.MinimumExecutionTimeout,
                baseline.MaximumExecutionTimeout, TimeSpan.FromMilliseconds(251)).ContentHash);
    }

    [Fact]
    public void V113_P02_ExactPolicyBoundariesBindAndSnapshotIsDefensive()
    {
        var policy = Policy();
        var recipe = Recipe();
        var minimum = new AlgorithmExecutionRequest(recipe, policy.MinimumExecutionTimeout);
        var maximum = new AlgorithmExecutionRequest(recipe, policy.MaximumExecutionTimeout);

        Assert.True(policy.TryBind(minimum, out var lower, out var lowerReason), lowerReason);
        Assert.True(policy.TryBind(maximum, out var upper, out var upperReason), upperReason);
        Assert.Equal("AlgorithmExecutionTimingBound", lowerReason);
        Assert.Equal("AlgorithmExecutionTimingBound", upperReason);
        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.Equal(policy.ContentHash, lower!.PolicyContentHash);
        Assert.Equal(policy.CancellationGracePeriod, lower.CancellationGracePeriod);
        Assert.Equal(policy.MinimumExecutionTimeout, lower.AlgorithmExecutionTimeout);
        Assert.Equal(policy.MaximumExecutionTimeout, upper!.AlgorithmExecutionTimeout);

        var changedRecipe = recipe with { Id = "Recipe.changed" };
        var changedRequest = minimum with { Recipe = changedRecipe };
        Assert.Equal("Recipe.original", lower.Recipe.Id);
        Assert.Equal("Recipe.changed", changedRequest.Recipe.Id);
        Assert.Equal("Recipe.original", lower.Recipe.Id);
    }

    [Fact]
    public void V113_P03_MissingAndMalformedRecipeIdentityAreRejectedWithoutSnapshot()
    {
        var policy = Policy();
        var cases = new[]
        {
            (new AlgorithmExecutionRequest(null!, TimeSpan.FromMilliseconds(100)), "RecipeRequired"),
            (new AlgorithmExecutionRequest(new RecipeReference("bad/id", "1", Hash()),
                TimeSpan.FromMilliseconds(100)), "RecipeIdentityInvalid"),
            (new AlgorithmExecutionRequest(new RecipeReference("Recipe.original", "", Hash()),
                TimeSpan.FromMilliseconds(100)), "RecipeIdentityInvalid"),
            (new AlgorithmExecutionRequest(new RecipeReference("Recipe.original", "1", "not-a-hash"),
                TimeSpan.FromMilliseconds(100)), "RecipeContentHashInvalid")
        };

        foreach (var (request, expectedReason) in cases)
        {
            Assert.False(policy.TryBind(request, out var snapshot, out var reason));
            Assert.Null(snapshot);
            Assert.Equal(expectedReason, reason);
        }

        Assert.False(policy.TryBind(null, out var missing, out var missingReason));
        Assert.Null(missing);
        Assert.Equal("AlgorithmExecutionRequestRequired", missingReason);
    }

    [Fact]
    public void V113_P04_NonPositiveFractionalAndUnrepresentableDurationsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AlgorithmExecutionPolicy("policy", "1", TimeSpan.Zero,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AlgorithmExecutionPolicy("policy", "1", TimeSpan.FromTicks(1),
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AlgorithmExecutionPolicy("policy", "1", TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100), TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AlgorithmExecutionPolicy("policy", "1", TimeSpan.FromTicks(
                (long)int.MaxValue * TimeSpan.TicksPerMillisecond + 1),
                TimeSpan.FromMilliseconds(int.MaxValue), TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentException>(() =>
            new AlgorithmExecutionPolicy("policy", "1", TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void V113_P05_RequestTimeoutMustBeWholeMillisecondAndWithinPolicyRange()
    {
        var policy = Policy();
        var cases = new[]
        {
            (TimeSpan.Zero, "AlgorithmExecutionTimeoutInvalid"),
            (TimeSpan.FromTicks(1), "AlgorithmExecutionTimeoutInvalid"),
            (TimeSpan.FromMilliseconds(99), "AlgorithmExecutionTimeoutBelowMinimum"),
            (TimeSpan.FromMilliseconds(501), "AlgorithmExecutionTimeoutAboveMaximum"),
            (TimeSpan.FromMilliseconds(100.5), "AlgorithmExecutionTimeoutInvalid")
        };

        foreach (var (timeout, expectedReason) in cases)
        {
            Assert.False(policy.TryBind(new AlgorithmExecutionRequest(Recipe(), timeout),
                out var snapshot, out var reason));
            Assert.Null(snapshot);
            Assert.Equal(expectedReason, reason);
        }
    }

    [Fact]
    public void V113_P06_PolicyIdentityIsBoundedAndNoImplicitDefaultsExist()
    {
        Assert.Throws<ArgumentException>(() => new AlgorithmExecutionPolicy("policy/name", "1",
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentException>(() => new AlgorithmExecutionPolicy("policy", "v 1",
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentException>(() => new AlgorithmExecutionPolicy("", "1",
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlgorithmExecutionPolicy("policy", "1",
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(0)));
    }

    private static AlgorithmExecutionPolicy Policy() => new("Policy.demo", "v1",
        TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250));

    private static RecipeReference Recipe() => new("Recipe.original", "v1", Hash());

    private static string Hash() => new('A', 64);
}
