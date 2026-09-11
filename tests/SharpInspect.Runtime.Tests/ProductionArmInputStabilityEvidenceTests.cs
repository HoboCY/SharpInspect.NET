using SharpInspect.Abstractions;
using SharpInspect.Runtime.Production;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T47 immutable input-stability evidence checks. Every case drives the real monotonic tracker with
/// explicit tick sequences and reads the evidence contract it produces: no wall clock, PLC, store,
/// audit or Runtime deployment is involved, no case arms or qualifies anything, and nothing here is
/// production acceptance evidence.
/// </summary>
public sealed class ProductionArmInputStabilityEvidenceContractTests
{
    private const long Frequency = TimeSpan.TicksPerSecond;

    private static PlcCommunicationPolicy Policy() => new("V147.StabilityEvidence", "1",
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(1));

    private static long WindowTicks(PlcCommunicationPolicy policy) =>
        (long)Math.Ceiling(policy.SynchronizationStabilityWindow.TotalSeconds * Frequency);

    [Theory]
    [InlineData(1000L)]
    [InlineData(1000000000L)]
    public void V147_B11_ElapsedEvidenceUsesTimeSpanUnitsAcrossTimestampFrequencies(long frequency)
    {
        var stability = new ProductionArmInputStability(Policy(), 0, 0, frequency);
        stability.Observe(1, frequency / 100, true);
        stability.Observe(2, frequency / 20, true);
        var evidence = Assert.IsType<ProductionArmInputStabilityEvidence>(stability.TryCapture(frequency / 20));
        Assert.Equal(TimeSpan.FromMilliseconds(40), evidence.StableDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(40), evidence.RequiredStabilityWindow);
        Assert.Equal(TimeSpan.FromMilliseconds(40), evidence.MaximumObservedGap);
    }

    private static long SampleGapTicks(PlcCommunicationPolicy policy) =>
        (long)Math.Ceiling((policy.OperationTimeout + policy.PollInterval).TotalSeconds * Frequency);

    [Theory]
    [InlineData(1000000000L, 40000001L, 400000L, 400001L)]
    [InlineData(3000000L, 120001L, 400003L, 400004L)]
    public void V147_B12_FractionalTimestampTicksPreserveAValidTwoSampleWindow(
        long frequency, long elapsed, long expectedDuration, long expectedGap)
    {
        var stability = new ProductionArmInputStability(Policy(), 0, 10, frequency);
        stability.Observe(11, 1, true);
        stability.Observe(12, 1 + elapsed, true);
        var evidence = Assert.IsType<ProductionArmInputStabilityEvidence>(stability.TryCapture(1 + elapsed));
        Assert.Equal(TimeSpan.FromTicks(expectedDuration), evidence.StableDuration);
        Assert.Equal(TimeSpan.FromTicks(expectedGap), evidence.MaximumObservedGap);
        Assert.True(evidence.StableDuration >= evidence.RequiredStabilityWindow);
        Assert.True(evidence.MaximumObservedGap <= evidence.MaximumSampleGap);
        Assert.Equal(2, evidence.SampleCount);
        // The canonical persisted values must survive reconstruction despite the
        // one-100ns-tick gap between the conservative lower and upper bounds.
        var restored = new ProductionArmInputStabilityEvidence(evidence.CommunicationPolicy,
            evidence.RequiredStabilityWindow.Ticks, evidence.MaximumSampleGap.Ticks,
            evidence.AttemptInitialSequence, evidence.FirstSequence, evidence.LastSequence,
            evidence.SampleCount, evidence.StableDuration.Ticks, evidence.MaximumObservedGap.Ticks,
            evidence.FreshnessAge.Ticks, evidence.HighInputResetCount, evidence.SampleGapResetCount,
            evidence.ContentHash);
        Assert.Equal(evidence.ContentHash, restored.ContentHash);
    }

    /// <summary>One complete two-sample clear window captured at the instant of its last sample.</summary>
    private static ProductionArmInputStabilityEvidence Capture(long startedAt, long initialSequence,
        long firstSequence, long firstObservedAt, long secondSequence, long secondObservedAt)
    {
        var stability = new ProductionArmInputStability(Policy(), startedAt, initialSequence, Frequency);
        stability.Observe(firstSequence, firstObservedAt, true);
        stability.Observe(secondSequence, secondObservedAt, true);
        return stability.TryCapture(secondObservedAt) ??
            throw new InvalidOperationException("The fixture window must be complete.");
    }

    [Fact]
    public void V147_B05_EvidenceRequiresAFreshPostAttemptWindowAndAMonotonicFreshnessAge()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var gap = SampleGapTicks(policy);
        var stability = new ProductionArmInputStability(policy, window * 2, 10, Frequency);

        // Samples at or before the attempt start can neither open the window nor be captured.
        stability.Observe(11, window, true);
        stability.Observe(12, window * 2, true);
        Assert.Null(stability.TryCapture(window * 2));
        Assert.Null(stability.TryCapture(window * 3));

        // Two strictly fresh clear samples complete the window, and the capture instant is an input.
        stability.Observe(13, window * 3, true);
        stability.Observe(14, window * 4, true);
        var evidence = stability.TryCapture(window * 4 + 25);
        Assert.NotNull(evidence);
        Assert.Equal(new RecipeContractReference(policy.Id, policy.Version, policy.ContentHash),
            evidence.CommunicationPolicy);
        Assert.Equal(policy.SynchronizationStabilityWindow, evidence.RequiredStabilityWindow);
        Assert.Equal(policy.OperationTimeout + policy.PollInterval, evidence.MaximumSampleGap);
        Assert.Equal(10, evidence.AttemptInitialSequence);
        Assert.Equal(13, evidence.FirstSequence);
        Assert.Equal(14, evidence.LastSequence);
        Assert.Equal(2, evidence.SampleCount);
        Assert.Equal(TimeSpan.FromTicks(window), evidence.StableDuration);
        Assert.Equal(TimeSpan.FromTicks(window), evidence.MaximumObservedGap);
        Assert.Equal(TimeSpan.FromTicks(25), evidence.FreshnessAge);
        Assert.Equal(0, evidence.HighInputResetCount);
        Assert.Equal(0, evidence.SampleGapResetCount);

        // A stale capture instant, or one before the last sample, produces no evidence at all.
        Assert.Null(stability.TryCapture(window * 4 + gap + 1));
        Assert.Null(stability.TryCapture(window * 4 - 1));
    }

    [Fact]
    public void V147_B06_AHighInputPulseResetsTheWindowAndIsCounted()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var stability = new ProductionArmInputStability(policy, 0, 100, Frequency);
        stability.Observe(101, window, true);
        stability.Observe(102, window * 2, true);
        Assert.NotNull(stability.TryCapture(window * 2));

        // One high sample ends the accumulated window; nothing can be captured afterwards.
        stability.Observe(103, window * 2 + window / 2, false);
        Assert.Null(stability.TryCapture(window * 2 + window / 2));
        Assert.Null(stability.TryCapture(window * 3));

        // The window must be rebuilt from scratch, and the discarded window stays visible.
        stability.Observe(104, window * 4, true);
        Assert.Null(stability.TryCapture(window * 4));
        stability.Observe(105, window * 5, true);
        var evidence = stability.TryCapture(window * 5);
        Assert.NotNull(evidence);
        Assert.Equal(policy.ContentHash, evidence.CommunicationPolicy.ContentHash);
        Assert.Equal(1, evidence.HighInputResetCount);
        Assert.Equal(0, evidence.SampleGapResetCount);
        Assert.Equal(104, evidence.FirstSequence);
        Assert.Equal(105, evidence.LastSequence);
        Assert.Equal(2, evidence.SampleCount);
        Assert.Equal(TimeSpan.FromTicks(window), evidence.StableDuration);
        Assert.Equal(TimeSpan.Zero, evidence.FreshnessAge);
    }

    [Fact]
    public void V147_B07_AnOverGapInterruptionResetsTheWindowAndIsCounted()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var gap = SampleGapTicks(policy);
        var stability = new ProductionArmInputStability(policy, 0, 200, Frequency);
        stability.Observe(201, window, true);
        stability.Observe(202, window * 2, true);
        Assert.NotNull(stability.TryCapture(window * 2));

        // A gap beyond the allowed sample gap ends the window even though inputs stayed clear.
        var resumed = window * 2 + gap + 1;
        stability.Observe(203, resumed, true);
        Assert.Null(stability.TryCapture(resumed));

        // The interruption gap stays outside the rebuilt window's own observed gap.
        stability.Observe(204, resumed + window, true);
        var evidence = stability.TryCapture(resumed + window);
        Assert.NotNull(evidence);
        Assert.Equal(1, evidence.SampleGapResetCount);
        Assert.Equal(0, evidence.HighInputResetCount);
        Assert.Equal(203, evidence.FirstSequence);
        Assert.Equal(204, evidence.LastSequence);
        Assert.Equal(2, evidence.SampleCount);
        Assert.Equal(TimeSpan.FromTicks(window), evidence.StableDuration);
        Assert.Equal(TimeSpan.FromTicks(window), evidence.MaximumObservedGap);
    }

    [Fact]
    public void V147_B08_RepeatedReadsOfOneCachedSampleCannotCompleteOrRefreshTheWindow()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var gap = SampleGapTicks(policy);
        var stability = new ProductionArmInputStability(policy, 0, 300, Frequency);
        stability.Observe(301, window, true);

        // Re-reading the same cached sample later is not a new sample: it neither advances the
        // window nor refreshes its monotonic freshness.
        stability.Observe(301, window * 3, true);
        Assert.Null(stability.TryCapture(window * 3));
        Assert.Null(stability.TryCapture(window + gap + 1));

        // Only strictly newer samples contribute, and the cached reads are not counted.
        stability.Observe(302, window * 4, true);
        var evidence = stability.TryCapture(window * 4);
        Assert.NotNull(evidence);
        Assert.Equal(301, evidence.FirstSequence);
        Assert.Equal(302, evidence.LastSequence);
        Assert.Equal(2, evidence.SampleCount);
        Assert.Equal(TimeSpan.FromTicks(window * 3), evidence.StableDuration);
        Assert.Equal(TimeSpan.FromTicks(window * 3), evidence.MaximumObservedGap);
    }

    [Fact]
    public void V147_B09_EvidenceRejectsImpossibleWindowFactsAndRewardsNothingForOptimism()
    {
        var policy = Policy();
        var window = WindowTicks(policy);
        var gap = SampleGapTicks(policy);
        var reference = new RecipeContractReference(policy.Id, policy.Version, policy.ContentHash);

        // Baseline: one coherent two-sample window that the contract accepts unchanged.
        var coherent = new ProductionArmInputStabilityEvidence(reference, window, gap, 10, 11, 20, 2,
            window, window, 0, 0, 0);
        Assert.Equal(window, coherent.RequiredStabilityWindow.Ticks);
        Assert.Equal(gap, coherent.MaximumSampleGap.Ticks);
        Assert.Equal(10, coherent.AttemptInitialSequence);
        Assert.Equal(20, coherent.LastSequence);

        // The bound policy reference is required.
        Assert.Throws<ArgumentNullException>(() => new ProductionArmInputStabilityEvidence(null!,
            window, gap, 10, 11, 20, 2, window, window, 0, 0, 0));
        // The effective thresholds must be positive monotonic durations.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            0, gap, 10, 11, 20, 2, window, window, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, 0, 10, 11, 20, 2, window, window, 0, 0, 0));
        // The attempt's own sequence cannot be negative, and the first sample must be strictly newer.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, -1, 11, 20, 2, window, window, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 10, 20, 2, window, window, 0, 0, 0));
        // The last sample can never precede the first one.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 21, 20, 2, window, window, 0, 0, 0));
        // Fewer than two samples, or a count that cannot be strictly advancing and unique.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 1, window, window, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 11, 2, window, window, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 12, 3, window, window, 0, 0, 0));
        // A span shorter than the required window never completed a window.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window - 1, window - 1, 0, 0, 0));
        // An observed gap beyond the allowed maximum, or longer than the window itself.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window, gap + 1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window, window + 2, 0, 0, 0));
        // A freshness age beyond the allowed sample gap, or one before the last sample.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window, window, gap + 1, 0, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window, window, -1, 0, 0));
        // Reset counters are counts, never negative.
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window, window, 0, -1, 0));
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(reference,
            window, gap, 10, 11, 20, 2, window, window, 0, 0, -1));
    }

    [Fact]
    public void V147_B10_EvidenceIdentityIsCanonicalImmutableAndRebuildableFromItsPrimitives()
    {
        var window = WindowTicks(Policy());

        // The same monotonic telemetry always produces the same canonical identity.
        var first = Capture(0, 10, 11, window, 12, window * 2);
        var second = Capture(0, 10, 11, window, 12, window * 2);
        Assert.NotSame(first, second);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Matches("^[0-9A-F]{64}$", first.ContentHash);
        // Frozen v1 identity of this fixture window; it covers the bound policy and every fact.
        Assert.Equal("31CB0A908A07E93070AFE85D6AE04020C5E35A7542F1A9FA011023B2C1FAB8E6",
            first.ContentHash);

        // Different observed telemetry cannot reuse the same identity.
        var otherAttempt = Capture(0, 9, 11, window, 12, window * 2);
        var otherWindow = Capture(0, 10, 11, window, 12, window * 3);
        Assert.NotEqual(first.ContentHash, otherAttempt.ContentHash);
        Assert.NotEqual(first.ContentHash, otherWindow.ContentHash);

        // Rebuilding from the published canonical primitives reproduces the exact identity, which is
        // what a persisted schema-32 record needs, and a tampered persisted hash fails closed.
        var rebuilt = new ProductionArmInputStabilityEvidence(first.CommunicationPolicy,
            first.RequiredStabilityWindow.Ticks, first.MaximumSampleGap.Ticks, first.AttemptInitialSequence,
            first.FirstSequence, first.LastSequence, first.SampleCount, first.StableDuration.Ticks,
            first.MaximumObservedGap.Ticks, first.FreshnessAge.Ticks, first.HighInputResetCount,
            first.SampleGapResetCount, first.ContentHash);
        Assert.Equal(first.ContentHash, rebuilt.ContentHash);
        Assert.Equal(first.FirstSequence, rebuilt.FirstSequence);
        Assert.Equal(first.LastSequence, rebuilt.LastSequence);
        Assert.Equal(first.SampleCount, rebuilt.SampleCount);
        Assert.Equal(first.StableDuration, rebuilt.StableDuration);
        Assert.Equal(first.MaximumObservedGap, rebuilt.MaximumObservedGap);
        Assert.Equal(first.FreshnessAge, rebuilt.FreshnessAge);
        var altered = (first.ContentHash[0] == 'A' ? "B" : "A") + first.ContentHash.Substring(1);
        Assert.Throws<ArgumentException>(() => new ProductionArmInputStabilityEvidence(
            first.CommunicationPolicy, first.RequiredStabilityWindow.Ticks, first.MaximumSampleGap.Ticks,
            first.AttemptInitialSequence, first.FirstSequence, first.LastSequence, first.SampleCount,
            first.StableDuration.Ticks, first.MaximumObservedGap.Ticks, first.FreshnessAge.Ticks,
            first.HighInputResetCount, first.SampleGapResetCount, altered));
    }
}
