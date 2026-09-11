using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable evidence that one production-arm attempt observed a fresh, uninterrupted and strictly
/// advancing clear-input window under one exact PLC communication policy. Every duration is a
/// monotonic elapsed value captured from the runtime timestamp source that enforced the window: the
/// UTC wall clock is never an authority here, and a repeated read of one cached sample contributes
/// nothing. The instance is self-contained so it can be embedded in the schema-32 production-arm
/// Authorized and Ready records; it proves only the window the runtime observed.
/// </summary>
public sealed class ProductionArmInputStabilityEvidence
{
    /// <summary>Captured by the runtime tracker under the exact policy object of the attempt.</summary>
    internal ProductionArmInputStabilityEvidence(PlcCommunicationPolicy communicationPolicy, long timestampFrequency,
        long attemptInitialSequence, long firstSequence, long lastSequence, int sampleCount,
        long stableDurationTicks, long maximumObservedGapTicks, long freshnessAgeTicks,
        int highInputResetCount, int sampleGapResetCount)
        : this(Reference(communicationPolicy),
            communicationPolicy.SynchronizationStabilityWindow.Ticks,
            (communicationPolicy.OperationTimeout + communicationPolicy.PollInterval).Ticks,
            attemptInitialSequence, firstSequence, lastSequence, sampleCount,
            DurationTicks(stableDurationTicks, timestampFrequency, roundUp: false),
            DurationTicks(maximumObservedGapTicks, timestampFrequency, roundUp: true),
            DurationTicks(freshnessAgeTicks, timestampFrequency, roundUp: true), highInputResetCount, sampleGapResetCount)
    {
    }

    /// <summary>
    /// Rebuilds the evidence from its canonical primitives: the bound policy identity, the effective
    /// monotonic thresholds and the observed window facts. A supplied <paramref name="contentHash"/>
    /// must reproduce the canonical hash, so a persisted record that was altered fails closed.
    /// </summary>
    internal ProductionArmInputStabilityEvidence(RecipeContractReference communicationPolicy,
        long requiredStabilityWindowTicks, long maximumSampleGapTicks, long attemptInitialSequence,
        long firstSequence, long lastSequence, int sampleCount, long stableDurationTicks,
        long maximumObservedGapTicks, long freshnessAgeTicks, int highInputResetCount, int sampleGapResetCount,
        string? contentHash = null)
    {
        CommunicationPolicy = communicationPolicy ?? throw new ArgumentNullException(nameof(communicationPolicy));
        if (requiredStabilityWindowTicks < 1 || maximumSampleGapTicks < 1)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceThresholdInvalid",
                nameof(requiredStabilityWindowTicks));
        if (attemptInitialSequence < 0 || firstSequence <= attemptInitialSequence ||
            lastSequence < firstSequence)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceSequenceInvalid", nameof(firstSequence));
        if (sampleCount < 2 || lastSequence - firstSequence < sampleCount - 1L)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceSequenceNotMonotonic",
                nameof(sampleCount));
        if (stableDurationTicks < requiredStabilityWindowTicks)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceDurationInvalid",
                nameof(stableDurationTicks));
        // Duration is a lower bound (floor), while the largest gap is an upper
        // bound (ceiling). The same fractional timestamp interval can therefore
        // differ by one 100 ns tick. Policy thresholds remain exact and strict.
        if (maximumObservedGapTicks <= 0 || maximumObservedGapTicks > maximumSampleGapTicks ||
            maximumObservedGapTicks - stableDurationTicks > 1)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceObservedGapInvalid",
                nameof(maximumObservedGapTicks));
        if ((decimal)stableDurationTicks > (decimal)maximumObservedGapTicks * (sampleCount - 1))
            throw new ArgumentException("ProductionArmInputStabilityEvidenceSampleSpanInvalid", nameof(stableDurationTicks));
        if (freshnessAgeTicks < 0 || freshnessAgeTicks > maximumSampleGapTicks)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceFreshnessInvalid",
                nameof(freshnessAgeTicks));
        if (highInputResetCount < 0 || sampleGapResetCount < 0)
            throw new ArgumentException("ProductionArmInputStabilityEvidenceResetCountInvalid",
                nameof(highInputResetCount));
        RequiredStabilityWindow = TimeSpan.FromTicks(requiredStabilityWindowTicks);
        MaximumSampleGap = TimeSpan.FromTicks(maximumSampleGapTicks);
        AttemptInitialSequence = attemptInitialSequence;
        FirstSequence = firstSequence;
        LastSequence = lastSequence;
        SampleCount = sampleCount;
        StableDuration = TimeSpan.FromTicks(stableDurationTicks);
        MaximumObservedGap = TimeSpan.FromTicks(maximumObservedGapTicks);
        FreshnessAge = TimeSpan.FromTicks(freshnessAgeTicks);
        HighInputResetCount = highInputResetCount;
        SampleGapResetCount = sampleGapResetCount;
        ContentHash = contentHash is null
            ? ComputeContentHash()
            : RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(), StringComparison.Ordinal))
            throw new ArgumentException("ProductionArmInputStabilityEvidenceContentHashMismatch",
                nameof(contentHash));
    }

    /// <summary>The exact PLC communication policy identity that governed the attempt.</summary>
    public RecipeContractReference CommunicationPolicy { get; }
    /// <summary>The effective stability window the attempt had to complete, in monotonic ticks.</summary>
    public TimeSpan RequiredStabilityWindow { get; }
    /// <summary>The effective maximum sample gap the attempt tolerated, in monotonic ticks.</summary>
    public TimeSpan MaximumSampleGap { get; }
    /// <summary>The attempt's input sequence when it started; every captured sample is strictly newer.</summary>
    public long AttemptInitialSequence { get; }
    /// <summary>The first clear sample that opened the captured window.</summary>
    public long FirstSequence { get; }
    /// <summary>The last clear sample of the captured window.</summary>
    public long LastSequence { get; }
    /// <summary>The number of accepted clear samples of the captured window.</summary>
    public int SampleCount { get; }
    /// <summary>First-to-last clear sample span in monotonic ticks; at least <see cref="RequiredStabilityWindow"/>.</summary>
    public TimeSpan StableDuration { get; }
    /// <summary>The largest clear-sample gap inside the captured window, in monotonic ticks.</summary>
    public TimeSpan MaximumObservedGap { get; }
    /// <summary>Capture-instant age of the last clear sample, in monotonic ticks.</summary>
    public TimeSpan FreshnessAge { get; }
    /// <summary>Clear windows this attempt ended with a high input before the captured window.</summary>
    public int HighInputResetCount { get; }
    /// <summary>Clear windows this attempt ended with an over-gap interruption before the captured window.</summary>
    public int SampleGapResetCount { get; }
    /// <summary>Canonical identity of every published field.</summary>
    public string ContentHash { get; }

    private string ComputeContentHash() => AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-production-arm-input-stability-evidence-v1",
        CommunicationPolicy.Id, CommunicationPolicy.Version, CommunicationPolicy.ContentHash,
        AttemptInitialSequence.ToString(CultureInfo.InvariantCulture),
        RequiredStabilityWindow.Ticks.ToString(CultureInfo.InvariantCulture),
        MaximumSampleGap.Ticks.ToString(CultureInfo.InvariantCulture),
        FirstSequence.ToString(CultureInfo.InvariantCulture),
        LastSequence.ToString(CultureInfo.InvariantCulture),
        SampleCount.ToString(CultureInfo.InvariantCulture),
        StableDuration.Ticks.ToString(CultureInfo.InvariantCulture),
        MaximumObservedGap.Ticks.ToString(CultureInfo.InvariantCulture),
        FreshnessAge.Ticks.ToString(CultureInfo.InvariantCulture),
        HighInputResetCount.ToString(CultureInfo.InvariantCulture),
        SampleGapResetCount.ToString(CultureInfo.InvariantCulture)
    });

    private static RecipeContractReference Reference(PlcCommunicationPolicy communicationPolicy)
    {
        ArgumentNullException.ThrowIfNull(communicationPolicy);
        return new RecipeContractReference(communicationPolicy.Id, communicationPolicy.Version,
            communicationPolicy.ContentHash);
    }

    // Persist elapsed durations in TimeSpan's 100ns units. Stopwatch ticks have
    // a platform frequency and must never be placed directly into a TimeSpan.
    // Round conservatively: do not overstate stability or understate gaps/age.
    private static long DurationTicks(long ticks, long timestampFrequency, bool roundUp)
    {
        if (timestampFrequency < 1)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency),
                "ProductionArmInputStabilityEvidenceTimestampFrequencyInvalid");
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        var scaled = (decimal)ticks * TimeSpan.TicksPerSecond / timestampFrequency;
        return checked((long)(roundUp ? Math.Ceiling(scaled) : Math.Floor(scaled)));
    }
}
