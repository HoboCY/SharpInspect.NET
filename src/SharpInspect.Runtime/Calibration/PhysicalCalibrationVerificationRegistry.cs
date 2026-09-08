using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>A bounded registry of explicitly selected project implementations; no verifier is installed implicitly.</summary>
public sealed class PhysicalCalibrationVerificationRegistry
{
    private readonly IReadOnlyList<Registration> _procedures;
    private int _running;
    public PhysicalCalibrationVerificationRegistry(IEnumerable<IPhysicalCalibrationVerificationProcedure> procedures)
    {
        var copied = AlgorithmContractValidation.Copy(procedures, nameof(procedures), 16);
        _procedures = copied.Select(value => new Registration(value, value.ProcedureContract, value.EvidenceContract)).ToArray();
        if (_procedures.Any(value => value.ProcedureContract is null || value.EvidenceContract is null) ||
            _procedures.Select(value => value.ProcedureContract).Distinct().Count() != _procedures.Count)
            throw new ArgumentException("PhysicalVerificationProcedureRegistrationInvalid");
    }

    internal async Task<PhysicalVerificationComputation> EvaluateAsync(PublishedCalibrationProfileVersion profile,
        PhysicalCalibrationVerificationSubmission submission, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var procedure = _procedures.SingleOrDefault(value => value.ProcedureContract == submission.ProcedureContract &&
            value.EvidenceContract == submission.Evidence.Format);
        if (procedure is null) return new(profile.ContentHash, submission.ContentHash, Array.Empty<CalibrationQualityMetric>(),
            "PhysicalVerificationProcedureUnavailable");
        if (procedure.Procedure.ProcedureContract != procedure.ProcedureContract ||
            procedure.Procedure.EvidenceContract != procedure.EvidenceContract)
            return new(profile.ContentHash, submission.ContentHash, Array.Empty<CalibrationQualityMetric>(),
                "PhysicalVerificationProcedureContractChanged");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new(profile.ContentHash, submission.ContentHash, Array.Empty<CalibrationQualityMetric>(),
                "PhysicalVerificationProcedureBusy");
        var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        var operation = Task.Run(() =>
        {
            try
            {
                var metrics = AlgorithmContractValidation.Copy(procedure.Procedure.Evaluate(
                    new PhysicalCalibrationVerificationContext(profile, submission), bounded.Token), "metrics", 32);
                if (metrics.Count == 0 || metrics.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() != metrics.Count)
                    return new PhysicalVerificationComputation(profile.ContentHash, submission.ContentHash,
                        Array.Empty<CalibrationQualityMetric>(), "PhysicalVerificationMetricsInvalid");
                if (!SameMetrics(metrics, submission.Metrics))
                    return new PhysicalVerificationComputation(profile.ContentHash, submission.ContentHash,
                        metrics, "PhysicalVerificationSubmittedMetricsMismatch");
                return new PhysicalVerificationComputation(profile.ContentHash, submission.ContentHash, metrics, null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return new PhysicalVerificationComputation(profile.ContentHash, submission.ContentHash,
                    Array.Empty<CalibrationQualityMetric>(), "PhysicalVerificationProcedureFailed");
            }
            finally
            {
                bounded.Dispose();
                Interlocked.Exchange(ref _running, 0);
            }
        }, CancellationToken.None);
        // A non-cooperative verifier keeps its actual slot until it exits. Timeout never admits a second invocation.
        try { return await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new(profile.ContentHash, submission.ContentHash, Array.Empty<CalibrationQualityMetric>(),
                "PhysicalVerificationProcedureDeadlineExceeded");
        }
    }

    private static bool SameMetrics(IEnumerable<CalibrationQualityMetric> first,
        IEnumerable<CalibrationQualityMetric> second) =>
        first.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => (value.Key, value.Unit, value.Value))
            .SequenceEqual(second.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => (value.Key, value.Unit, value.Value)));

    private sealed record Registration(IPhysicalCalibrationVerificationProcedure Procedure,
        RecipeContractReference ProcedureContract, RecipeContractReference EvidenceContract);
}

internal sealed record PhysicalVerificationComputation(string ProfileContentHash, string SubmissionContentHash,
    IReadOnlyList<CalibrationQualityMetric> Metrics, string? Failure);
