using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Calibration;

/// <summary>
/// Explicit project implementations for physical verification before an imported Profile exists.
/// The caller transfers the device reservation to this registry for the lifetime of the actual task.
/// </summary>
public sealed class ImportedCalibrationPhysicalVerificationRegistry
{
    private readonly IReadOnlyList<Registration> _registrations;
    private int _running;

    public ImportedCalibrationPhysicalVerificationRegistry(
        IEnumerable<IImportedCalibrationPhysicalVerificationProcedure> procedures)
    {
        _registrations = AlgorithmContractValidation.Copy(procedures, nameof(procedures), 16)
            .Select(value => new Registration(value, value.ProcedureContract, value.EvidenceContract)).ToArray();
        if (_registrations.Any(value => value.ProcedureContract is null || value.EvidenceContract is null) ||
            _registrations.Select(value => value.ProcedureContract).Distinct().Count() != _registrations.Count)
            throw new ArgumentException("CalibrationImportPhysicalVerifierRegistrationInvalid");
    }

    /// <summary>
    /// Runs an imported physical verifier against a lease-confirmed, exact camera snapshot.
    /// Caller cancellation only retires the caller's wait. The worker retains the slot and
    /// reservation until the verifier exits and its finally block completes.
    /// </summary>
    internal async Task<ImportedPhysicalVerificationComputation> EvaluateAsync(
        Guid commandCorrelationId, Guid runtimeEpoch, CameraSetupSnapshot cameraSnapshot,
        ImagingSetupRevisionReference imagingSetup, IAsyncDisposable deviceReservation,
        Func<DateTimeOffset> clock, ImportedCalibrationCandidate candidate,
        ImportedCalibrationEvaluation evaluation, PhysicalCalibrationVerificationSubmission submission,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cameraSnapshot);
        ArgumentNullException.ThrowIfNull(imagingSetup);
        ArgumentNullException.ThrowIfNull(deviceReservation);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(submission);

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ImportedPhysicalVerificationComputation Failure(string reason,
            IReadOnlyList<CalibrationQualityMetric>? metrics = null,
            CalibrationImportPhysicalWitness? witness = null) =>
            new(candidate.ContentHash, evaluation.ContentHash, submission.ContentHash,
                metrics ?? Array.Empty<CalibrationQualityMetric>(), reason, witness);

        var preflightFailure = ValidatePreflight(commandCorrelationId, runtimeEpoch, cameraSnapshot,
            imagingSetup, candidate, evaluation, submission);
        if (preflightFailure is not null)
        {
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure(preflightFailure);
        }

        var registration = _registrations.SingleOrDefault(value =>
            value.ProcedureContract == submission.ProcedureContract &&
            value.EvidenceContract == submission.Evidence.Format);
        if (registration is null)
        {
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalVerifierUnavailable");
        }
        if (!Matches(registration))
        {
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalVerifierChanged");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalVerificationCancelled");
        }
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalVerifierBusy");
        }

        DateTimeOffset startedAtUtc;
        try
        {
            startedAtUtc = ReadClock(clock);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Exchange(ref _running, 0);
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalWitnessTimeInvalid");
        }

        if (startedAtUtc < evaluation.RecordedAtUtc)
        {
            Interlocked.Exchange(ref _running, 0);
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalWitnessTimeInvalid");
        }

        var bounded = new CancellationTokenSource();
        var callerRegistration = cancellationToken.Register(static state =>
            QueueCancellation((CancellationTokenSource)state!), bounded);
        Timer timeoutTimer;
        try
        {
            timeoutTimer = new Timer(static state =>
                QueueCancellation((CancellationTokenSource)state!), bounded,
                timeout, Timeout.InfiniteTimeSpan);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            callerRegistration.Dispose();
            bounded.Dispose();
            Interlocked.Exchange(ref _running, 0);
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalVerificationFailed");
        }

        Task<ImportedPhysicalVerificationComputation> actual;
        try
        {
            actual = Task.Run(() => RunActualAsync(registration, commandCorrelationId, runtimeEpoch,
                cameraSnapshot, imagingSetup, deviceReservation, clock, candidate, evaluation, submission,
                startedAtUtc, bounded, callerRegistration, timeoutTimer), CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            callerRegistration.Dispose();
            timeoutTimer.Dispose();
            bounded.Dispose();
            Interlocked.Exchange(ref _running, 0);
            await DisposeReservationAsync(deviceReservation).ConfigureAwait(false);
            return Failure("CalibrationImportPhysicalVerificationFailed");
        }

        // Cancellation retires only this await. The actual task owns bounded, timer,
        // registration, the physical slot, and the transferred device reservation.
        try
        {
            return await actual.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            QueueCancellation(bounded);
            return Failure("CalibrationImportPhysicalVerifierDeadlineExceeded");
        }
    }

    private async Task<ImportedPhysicalVerificationComputation> RunActualAsync(
        Registration registration, Guid commandCorrelationId, Guid runtimeEpoch,
        CameraSetupSnapshot cameraSnapshot, ImagingSetupRevisionReference imagingSetup,
        IAsyncDisposable deviceReservation, Func<DateTimeOffset> clock,
        ImportedCalibrationCandidate candidate, ImportedCalibrationEvaluation evaluation,
        PhysicalCalibrationVerificationSubmission submission, DateTimeOffset startedAtUtc,
        CancellationTokenSource bounded, CancellationTokenRegistration callerRegistration,
        Timer timeoutTimer)
    {
        var metrics = (IReadOnlyList<CalibrationQualityMetric>)Array.Empty<CalibrationQualityMetric>();
        string? failure = null;
        CalibrationImportPhysicalWitness? witness = null;
        try
        {
            try
            {
                metrics = AlgorithmContractValidation.Copy(registration.Procedure.Evaluate(
                    new ImportedCalibrationPhysicalVerificationContext(candidate, evaluation, submission),
                    bounded.Token), "metrics", 32);
                if (metrics.Count == 0 || metrics.Select(value => value.Key)
                        .Distinct(StringComparer.Ordinal).Count() != metrics.Count)
                    failure = "CalibrationImportPhysicalMetricsInvalid";
                else if (bounded.IsCancellationRequested)
                    failure = "CalibrationImportPhysicalVerificationCancelled";
                else if (!Matches(registration))
                    failure = "CalibrationImportPhysicalVerifierChanged";
                else if (!SameMetrics(metrics, submission.Metrics))
                    failure = "CalibrationImportPhysicalSubmittedMetricsMismatch";
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure = bounded.IsCancellationRequested
                    ? "CalibrationImportPhysicalVerificationCancelled"
                    : "CalibrationImportPhysicalVerificationFailed";
            }
        }
        finally
        {
            try
            {
                var completedAtUtc = ReadClock(clock);
                witness = new CalibrationImportPhysicalWitness(commandCorrelationId, runtimeEpoch,
                    cameraSnapshot.Binding!, imagingSetup,
                    CalibrationFrameGeometry.FromRequested(cameraSnapshot.Requested!),
                    CalibrationFrameGeometry.FromEffective(cameraSnapshot.Effective!),
                    startedAtUtc, completedAtUtc);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure ??= "CalibrationImportPhysicalWitnessTimeInvalid";
            }

            try
            {
                await deviceReservation.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure ??= "CalibrationImportPhysicalReservationReleaseFailed";
            }

            timeoutTimer.Dispose();
            callerRegistration.Dispose();
            bounded.Dispose();
            Interlocked.Exchange(ref _running, 0);
        }

        return new ImportedPhysicalVerificationComputation(candidate.ContentHash, evaluation.ContentHash,
            submission.ContentHash, metrics, failure, witness);
    }

    private static string? ValidatePreflight(Guid commandCorrelationId, Guid runtimeEpoch,
        CameraSetupSnapshot cameraSnapshot, ImagingSetupRevisionReference imagingSetup,
        ImportedCalibrationCandidate candidate, ImportedCalibrationEvaluation evaluation,
        PhysicalCalibrationVerificationSubmission submission)
    {
        if (commandCorrelationId == Guid.Empty || runtimeEpoch == Guid.Empty)
            return "CalibrationImportPhysicalWitnessIdentityInvalid";
        if (cameraSnapshot.Binding is null || cameraSnapshot.Requested is null || cameraSnapshot.Effective is null)
            return "CalibrationImportPhysicalCameraSnapshotUnavailable";
        if (cameraSnapshot.Health.ProviderAvailability != CameraProviderAvailability.Available ||
            cameraSnapshot.Health.Connection != CameraConnectionState.Open ||
            cameraSnapshot.Health.Configuration != CameraConfigurationState.Applied ||
            cameraSnapshot.Health.Acquisition != CameraAcquisitionState.Stopped)
            return "CalibrationImportPhysicalCameraSnapshotNotReady";
        if (cameraSnapshot.LogicalRole != evaluation.Content.Requirement.LogicalCameraRole ||
            cameraSnapshot.Binding.RevisionHash != evaluation.Binding.RevisionHash ||
            cameraSnapshot.Binding.Target != evaluation.Binding.Target)
            return "CalibrationImportPhysicalCameraBindingMismatch";
        if (imagingSetup != evaluation.Content.ImagingSetup ||
            imagingSetup.LogicalCameraRole != cameraSnapshot.LogicalRole)
            return "CalibrationImportPhysicalImagingSetupMismatch";

        try
        {
            if (CalibrationFrameGeometry.FromRequested(cameraSnapshot.Requested).ContentHash !=
                    evaluation.Content.RequestedGeometry.ContentHash ||
                CalibrationFrameGeometry.FromEffective(cameraSnapshot.Effective).ContentHash !=
                    evaluation.Content.EffectiveGeometry.ContentHash)
                return "CalibrationImportPhysicalGeometryMismatch";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "CalibrationImportPhysicalGeometryMismatch";
        }

        _ = candidate;
        return null;
    }

    private static DateTimeOffset ReadClock(Func<DateTimeOffset> clock)
    {
        var value = clock().ToUniversalTime();
        if (value == default)
            throw new ArgumentException("CalibrationImportPhysicalWitnessTimeRequired");
        return value;
    }

    private static bool SameMetrics(IEnumerable<CalibrationQualityMetric> first,
        IEnumerable<CalibrationQualityMetric> second) =>
        first.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => (value.Key, value.Unit, value.Value))
            .SequenceEqual(second.OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => (value.Key, value.Unit, value.Value)));

    private static bool Matches(Registration value) =>
        value.Procedure.ProcedureContract == value.ProcedureContract &&
        value.Procedure.EvidenceContract == value.EvidenceContract;

    private static void QueueCancellation(CancellationTokenSource bounded) =>
        _ = Task.Run(() =>
        {
            try { bounded.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        });

    private static async Task DisposeReservationAsync(IAsyncDisposable reservation)
    {
        try { await reservation.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private sealed record Registration(IImportedCalibrationPhysicalVerificationProcedure Procedure,
        RecipeContractReference ProcedureContract, RecipeContractReference EvidenceContract);
}

internal sealed record ImportedPhysicalVerificationComputation(string CandidateHash, string EvaluationHash,
    string SubmissionHash, IReadOnlyList<CalibrationQualityMetric> Metrics, string? Failure,
    CalibrationImportPhysicalWitness? Witness = null);
