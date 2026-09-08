namespace SharpInspect.Abstractions;

/// <summary>
/// Exact project procedure for decoding independent verification evidence and recomputing its measurements.
/// Registering a procedure establishes an implementation, not production qualification or metrological traceability.
/// </summary>
public interface IPhysicalCalibrationVerificationProcedure
{
    RecipeContractReference ProcedureContract { get; }
    RecipeContractReference EvidenceContract { get; }
    IReadOnlyList<CalibrationQualityMetric> Evaluate(PhysicalCalibrationVerificationContext context,
        CancellationToken cancellationToken);
}

/// <summary>Immutable source data. A verifier must derive measurements from Evidence and the exact Profile.</summary>
public sealed class PhysicalCalibrationVerificationContext
{
    internal PhysicalCalibrationVerificationContext(PublishedCalibrationProfileVersion profile,
        PhysicalCalibrationVerificationSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(submission);
        Profile = profile;
        IndependentReference = submission.IndependentReference;
        PerformedAtUtc = submission.PerformedAtUtc;
        Evidence = submission.Evidence;
    }

    public PublishedCalibrationProfileVersion Profile { get; }
    public RecipeContractReference IndependentReference { get; }
    public DateTimeOffset PerformedAtUtc { get; }
    public PhysicalCalibrationVerificationEvidencePayload Evidence { get; }
}
