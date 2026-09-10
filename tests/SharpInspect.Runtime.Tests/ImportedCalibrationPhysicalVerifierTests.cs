using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ImportedCalibrationPhysicalVerifierTests
{
    [Fact]
    public void V134_V01_ImportedVerifierContextExposesOnlyBoundInputs()
    {
        var properties = typeof(ImportedCalibrationPhysicalVerificationContext)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Candidate", "Evaluation", "Evidence", "IndependentReference", "PerformedAtUtc" },
            properties.Select(value => value.Name).ToArray());
        Assert.DoesNotContain("Submission", properties.Select(value => value.Name));
        Assert.DoesNotContain("Metrics", properties.Select(value => value.Name));
        Assert.DoesNotContain(typeof(PhysicalCalibrationVerificationSubmission),
            properties.Select(value => value.PropertyType));
    }

    [Fact]
    public async Task V134_V02_RegistryComparesIndependentMetricsAndReturnsLeaseWitness()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var procedure = new CapturingProcedure(fixture.Physical.Submission.ProcedureContract,
            fixture.Physical.Submission.Evidence.Format,
            new[] { new CalibrationQualityMetric("Scale", 0.70, "mm/pixel") });
        var registry = new ImportedCalibrationPhysicalVerificationRegistry(new[] { procedure });
        var reservation = new TestReservation();
        var correlation = Guid.Parse("B1B1B1B1-B1B1-B1B1-B1B1-B1B1B1B1B1B1");
        var epoch = Guid.Parse("C1C1C1C1-C1C1-C1C1-C1C1-C1C1C1C1C1C1");
        var started = fixture.Evaluation.RecordedAtUtc.AddMinutes(1);
        var completed = started.AddSeconds(1);
        var camera = CreateCameraSnapshot(fixture);
        var clockValues = new Queue<DateTimeOffset>(new[] { started, completed });

        var result = await registry.EvaluateAsync(correlation, epoch, camera,
            fixture.Evaluation.Content.ImagingSetup, reservation,
            () => clockValues.Count == 0 ? completed : clockValues.Dequeue(),
            fixture.Candidate, fixture.Evaluation, fixture.Physical.Submission,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("CalibrationImportPhysicalSubmittedMetricsMismatch", result.Failure);
        Assert.Single(result.Metrics);
        Assert.NotNull(result.Witness);
        Assert.Equal(correlation, result.Witness!.OperationId);
        Assert.Equal(epoch, result.Witness.RuntimeEpoch);
        Assert.Equal(started, result.Witness.StartedAtUtc);
        Assert.Equal(completed, result.Witness.CompletedAtUtc);
        Assert.Equal(1, reservation.DisposeCount);
        Assert.NotNull(procedure.Context);
        Assert.Equal(fixture.Physical.Submission.IndependentReference, procedure.Context!.IndependentReference);
        Assert.Equal(fixture.Physical.Submission.PerformedAtUtc, procedure.Context.PerformedAtUtc);
        Assert.Equal(fixture.Physical.Submission.Evidence.ContentHash, procedure.Context.Evidence.ContentHash);
    }

    [Fact]
    public async Task V134_V03_CallerCancellationDoesNotReleaseReservationUntilWorkerExits()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var procedure = new BlockingProcedure(fixture.Physical.Submission.ProcedureContract,
            fixture.Physical.Submission.Evidence.Format, fixture.Physical.Submission.Metrics);
        var registry = new ImportedCalibrationPhysicalVerificationRegistry(new[] { procedure });
        var reservation = new TestReservation();
        using var cancellation = new CancellationTokenSource();
        var camera = CreateCameraSnapshot(fixture);
        var evaluate = registry.EvaluateAsync(
            Guid.Parse("D1D1D1D1-D1D1-D1D1-D1D1-D1D1D1D1D1D1"),
            Guid.Parse("E1E1E1E1-E1E1-E1E1-E1E1-E1E1E1E1E1E1"), camera,
            fixture.Evaluation.Content.ImagingSetup, reservation,
            () => fixture.Evaluation.RecordedAtUtc.AddMinutes(1), fixture.Candidate,
            fixture.Evaluation, fixture.Physical.Submission, TimeSpan.FromSeconds(5), cancellation.Token);

        try
        {
            await procedure.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await evaluate);
            Assert.Equal(0, reservation.DisposeCount);
        }
        finally { procedure.Release(); }
        await WaitForAsync(() => reservation.DisposeCount == 1);
    }

    [Fact]
    public async Task V134_V05_PluginCancellationCallbackCannotBlockTheCancellingCaller()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var procedure = new BlockingProcedure(fixture.Physical.Submission.ProcedureContract,
            fixture.Physical.Submission.Evidence.Format, fixture.Physical.Submission.Metrics)
        { BlockCancellationCallback = true };
        var registry = new ImportedCalibrationPhysicalVerificationRegistry(new[] { procedure });
        var reservation = new TestReservation();
        using var cancellation = new CancellationTokenSource();
        var evaluate = registry.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid(), CreateCameraSnapshot(fixture),
            fixture.Evaluation.Content.ImagingSetup, reservation,
            () => fixture.Evaluation.RecordedAtUtc.AddMinutes(1), fixture.Candidate,
            fixture.Evaluation, fixture.Physical.Submission, TimeSpan.FromSeconds(5), cancellation.Token);
        try
        {
            await procedure.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Run(cancellation.Cancel).WaitAsync(TimeSpan.FromSeconds(3));
            await procedure.CancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await evaluate);
            Assert.Equal(0, reservation.DisposeCount);
        }
        finally { procedure.Release(); }
        await WaitForAsync(() => reservation.DisposeCount == 1);
    }

    [Fact]
    public async Task V134_V06_TimeoutDoesNotReleaseTheActualVerifierSlotOrDeviceLease()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var procedure = new BlockingProcedure(fixture.Physical.Submission.ProcedureContract,
            fixture.Physical.Submission.Evidence.Format, fixture.Physical.Submission.Metrics);
        var registry = new ImportedCalibrationPhysicalVerificationRegistry(new[] { procedure });
        var reservation = new TestReservation();
        try
        {
            var result = await registry.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid(), CreateCameraSnapshot(fixture),
                fixture.Evaluation.Content.ImagingSetup, reservation,
                () => fixture.Evaluation.RecordedAtUtc.AddMinutes(1), fixture.Candidate,
                fixture.Evaluation, fixture.Physical.Submission, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal("CalibrationImportPhysicalVerifierDeadlineExceeded", result.Failure);
            Assert.Equal(0, reservation.DisposeCount);
            var secondReservation = new TestReservation();
            var busy = await registry.EvaluateAsync(Guid.NewGuid(), Guid.NewGuid(), CreateCameraSnapshot(fixture),
                fixture.Evaluation.Content.ImagingSetup, secondReservation,
                () => fixture.Evaluation.RecordedAtUtc.AddMinutes(1), fixture.Candidate,
                fixture.Evaluation, fixture.Physical.Submission, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Equal("CalibrationImportPhysicalVerifierBusy", busy.Failure);
            Assert.Equal(1, secondReservation.DisposeCount);
        }
        finally { procedure.Release(); }
        await WaitForAsync(() => reservation.DisposeCount == 1);
    }

    [Fact]
    public async Task V134_V04_PreflightFailureReleasesTransferredReservation()
    {
        var fixture = CalibrationImportTestDataFactory.Create();
        var procedure = new CapturingProcedure(fixture.Physical.Submission.ProcedureContract,
            fixture.Physical.Submission.Evidence.Format, fixture.Physical.Submission.Metrics);
        var registry = new ImportedCalibrationPhysicalVerificationRegistry(new[] { procedure });
        var reservation = new TestReservation();
        var closed = new CameraSetupSnapshot(fixture.Evaluation.Content.Requirement.LogicalCameraRole,
            null, new CameraHealthSnapshot(CameraProviderAvailability.DependencyMissing,
                CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                CameraAcquisitionState.Stopped, new FrameTimePoint(
                    fixture.Evaluation.RecordedAtUtc, 1)));

        var result = await registry.EvaluateAsync(
            Guid.Parse("F1F1F1F1-F1F1-F1F1-F1F1-F1F1F1F1F1F1"),
            Guid.Parse("12121212-1212-1212-1212-121212121212"), closed,
            fixture.Evaluation.Content.ImagingSetup, reservation,
            () => fixture.Evaluation.RecordedAtUtc.AddMinutes(1), fixture.Candidate,
            fixture.Evaluation, fixture.Physical.Submission, TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("CalibrationImportPhysicalCameraSnapshotUnavailable", result.Failure);
        Assert.Equal(1, reservation.DisposeCount);
    }

    private static CameraSetupSnapshot CreateCameraSnapshot(CalibrationImportFixture fixture)
    {
        var geometry = fixture.Evaluation.Content.RequestedGeometry;
        var request = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            100, 0, geometry.RegionOfInterest, geometry.PixelFormat, geometry.ValidBits, 1000, 0, null);
        var effective = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            100, 0, geometry.RegionOfInterest, geometry.PixelFormat, geometry.ValidBits, 1000, 0, null);
        var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
            CameraConnectionState.Open, CameraConfigurationState.Applied, CameraAcquisitionState.Stopped,
            new FrameTimePoint(fixture.Evaluation.RecordedAtUtc, 1));
        return new CameraSetupSnapshot(geometry is not null
            ? fixture.Evaluation.Content.Requirement.LogicalCameraRole
            : throw new InvalidOperationException(), fixture.Evaluation.Binding, health, request, effective);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        Assert.True(condition());
    }

    private sealed class CapturingProcedure : IImportedCalibrationPhysicalVerificationProcedure
    {
        private readonly IReadOnlyList<CalibrationQualityMetric> _metrics;
        public CapturingProcedure(RecipeContractReference procedureContract,
            RecipeContractReference evidenceContract, IReadOnlyList<CalibrationQualityMetric> metrics)
        {
            ProcedureContract = procedureContract;
            EvidenceContract = evidenceContract;
            _metrics = metrics;
        }
        public RecipeContractReference ProcedureContract { get; }
        public RecipeContractReference EvidenceContract { get; }
        public ImportedCalibrationPhysicalVerificationContext? Context { get; private set; }
        public IReadOnlyList<CalibrationQualityMetric> Evaluate(
            ImportedCalibrationPhysicalVerificationContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return _metrics;
        }
    }

    private sealed class BlockingProcedure : IImportedCalibrationPhysicalVerificationProcedure
    {
        private readonly IReadOnlyList<CalibrationQualityMetric> _metrics;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseCancellation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BlockingProcedure(RecipeContractReference procedureContract,
            RecipeContractReference evidenceContract, IReadOnlyList<CalibrationQualityMetric> metrics)
        {
            ProcedureContract = procedureContract;
            EvidenceContract = evidenceContract;
            _metrics = metrics;
        }
        public RecipeContractReference ProcedureContract { get; }
        public RecipeContractReference EvidenceContract { get; }
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool BlockCancellationCallback { get; init; }
        internal TaskCompletionSource<bool> CancellationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CalibrationQualityMetric> Evaluate(
            ImportedCalibrationPhysicalVerificationContext context, CancellationToken cancellationToken)
        {
            using var registration = BlockCancellationCallback ? cancellationToken.Register(() =>
            {
                CancellationStarted.TrySetResult(true);
                _releaseCancellation.Task.GetAwaiter().GetResult();
            }) : default;
            Started.TrySetResult(true);
            _release.Task.GetAwaiter().GetResult();
            return _metrics;
        }
        internal void Release()
        {
            _releaseCancellation.TrySetResult(true);
            _release.TrySetResult(true);
        }
    }

    private sealed class TestReservation : IAsyncDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
