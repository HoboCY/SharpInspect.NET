using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ManualCameraAcquisitionTests
{
    [Fact]
    public async Task V135_R01_PublicManualSelectionCannotBypassRuntimeCorrelation()
    {
        var clock = new FixtureClock();
        var device = new FixtureControlledDevice(clock);
        await using var service = CreateService(device, clock);

        var result = await service.AcquireAsync(ExecutionKind.Manual, "Primary");

        Assert.False(result.Accepted);
        Assert.Null(result.Correlation);
        Assert.Equal("ProductionAcquisitionUnavailable", result.ReasonCode);
        Assert.Equal(0, device.AcquireCalls);
    }

    [Fact]
    public async Task V135_R02_ManualCorrelationUsesControlledAcquireCoreAndRetires()
    {
        var clock = new FixtureClock();
        var device = new FixtureControlledDevice(clock);
        await using var service = CreateService(device, clock);
        service.EnableManualAcquisition();
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());

        var result = await service.AcquireManualAsync(correlation, "Primary");

        Assert.True(result.Accepted);
        Assert.Equal(correlation, result.Correlation);
        Assert.Equal(correlation, result.Outcome!.Correlation);
        Assert.Equal("ManualFixtureFailure", result.ReasonCode);
        Assert.Equal(ExecutionStatus.Error, result.Outcome.ExecutionStatus);
        Assert.Equal(1, device.AcquireCalls);

        var retirement = await service.RetireAsync();
        Assert.True(retirement.SafeToReplace);
        Assert.Equal(1, device.StopCalls);
        Assert.Equal(1, device.DisposeCalls);
    }

    [Fact]
    public async Task V135_R04_PhysicalPhaseClaimsWrapStartAndControlledAcquire()
    {
        var clock = new FixtureClock();
        var device = new FixtureControlledDevice(clock);
        var phases = 0;
        var released = 0;
        var borrow = new ManualControlledCameraBorrow(device, () =>
        {
            var id = Interlocked.Increment(ref phases);
            return RecipeActivationPhysicalPhaseClaim.Granted(id,
                () => Interlocked.Increment(ref released));
        });
        await using var service = new CameraAcquisitionService(borrow,
            new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0, null),
            clock, new CameraAcquisitionOptions(TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1)));
        service.EnableManualAcquisition();

        var result = await service.AcquireManualAsync(
            new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid()), "Primary");

        Assert.True(result.Accepted);
        Assert.Equal("ManualFixtureFailure", result.ReasonCode);
        Assert.Equal(1, device.StartCalls);
        Assert.Equal(1, device.AcquireCalls);
        Assert.Equal(2, phases);
        Assert.Equal(2, released);
        var retirement = await service.RetireAsync();
        Assert.True(retirement.SafeToReplace);
    }

    [Fact]
    public void V135_R03_ManualFrameBridgeReturnsConcreteBoundedPoolLease()
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
            new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 100, 0, null);
        var metadata = new FrameMetadata(correlation, "Primary", 2, 2, 2,
            VisionPixelFormat.Mono8, null, DateTimeOffset.UtcNow, configuration);
        var point = new FrameTimePoint(DateTimeOffset.UtcNow, 1);
        var milestones = new FrameAcquisitionMilestones(1_000, point, point, point, point);
        var provenance = new FrameProvenance(correlation, "FixtureProvider", "1",
            "FixtureAdapter", "1", "FixtureSdk", "1", null, "FixtureDevice", null,
            null, "Mono8", "passthrough-v1", false, false, null, null, milestones);
        using var source = new FixtureLease(new FixtureFrame(metadata,
            new[] { new byte[] { 1, 2 }, new byte[] { 3, 4 } }), provenance);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 64,
            TimeSpan.FromSeconds(1)));

        var copied = ManualFrameBufferBridge.TryCopyToPool(source, pool);

        Assert.True(copied.Succeeded);
        var lease = Assert.IsType<FrameBufferLease>(copied.Lease);
        using (lease)
        {
            Assert.Equal(new byte[] { 1, 2 }, lease.Frame.GetRowSpan(0).ToArray());
            Assert.Equal(new byte[] { 3, 4 }, lease.Frame.GetRowSpan(1).ToArray());
            Assert.Equal(correlation, lease.Provenance.Correlation);
            Assert.NotNull(lease.Provenance.PoolCopyEvidence);
        }
    }

    private static CameraAcquisitionService CreateService(FixtureControlledDevice device,
        FixtureClock clock) => new(device, new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
            new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 100, 0, null),
        clock, new CameraAcquisitionOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

    private sealed class FixtureControlledDevice : IControlledCameraDevice
    {
        private readonly FixtureClock _clock;

        internal FixtureControlledDevice(FixtureClock clock) => _clock = clock;

        internal int AcquireCalls { get; private set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }
        internal int DisposeCalls { get; private set; }

        public CameraDeviceDescriptor Descriptor { get; } = new(
            new CameraProviderIdentity("FixtureProvider", "1", "FixtureAdapter", "1"),
            "FixtureDevice", "Manual acquisition fixture");

        public CameraCapabilities Capabilities { get; } = new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(1, 1, new(0, 0, 1), new(0, 0, 1),
                new(1, 1, 1), new(1, 1, 1)));

        public CameraHealthSnapshot GetHealthSnapshot() => new(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Applied, CameraAcquisitionState.Armed,
            _clock.GetTimePoint());

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraConfigurationResult.Failure("ManualFixtureNotUsed"));

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            StartAndReturnSuccess();

        private ValueTask<CameraOperationResult> StartAndReturnSuccess()
        {
            StartCalls++;
            return ValueTask.FromResult(CameraOperationResult.Success("ManualFixtureStarted"));
        }

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.DeviceFault,
                    "ManualFixtureUncontrolledAcquire")));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control, CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            return new ValueTask<FrameAcquisitionResult>(CompleteAsync(request, control,
                cancellationToken));
        }

        private static async Task<FrameAcquisitionResult> CompleteAsync(
            FrameAcquisitionRequest request, FrameAcquisitionControl control,
            CancellationToken cancellationToken)
        {
            Assert.True(control.AcknowledgePending(request));
            await control.WaitForBusyAsync(cancellationToken);
            return FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.DeviceFault,
                    "ManualFixtureFailure"));
        }

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return ValueTask.FromResult(CameraOperationResult.Success("ManualFixtureStopped"));
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) => new(Guid.Parse("3bcbcf36-40e0-4d9e-8d5b-2fc9f9156d8f"),
            1, 0, false, Array.Empty<CameraProtocolObservation>());

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureClock : IFrameAcquisitionClock
    {
        private static readonly DateTimeOffset Epoch =
            new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public long Frequency => 1_000;

        public FrameTimePoint GetTimePoint() => new(Epoch, 1);

        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            internal static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FixtureLease : IFrameBufferLease
    {
        private int _returned;

        internal FixtureLease(VisionFrame frame, FrameProvenance provenance)
        {
            Frame = frame;
            Provenance = provenance;
            LeaseId = Guid.NewGuid();
        }

        public Guid LeaseId { get; }
        public VisionFrame Frame { get; }
        public FrameProvenance Provenance { get; }
        public bool IsReturned => Volatile.Read(ref _returned) != 0;
        public void Dispose() => Interlocked.Exchange(ref _returned, 1);
    }

    private sealed class FixtureFrame : VisionFrame
    {
        private readonly IReadOnlyList<byte[]> _rows;

        internal FixtureFrame(FrameMetadata metadata, IReadOnlyList<byte[]> rows) : base(metadata) =>
            _rows = rows;

        public override bool IsLoanActive => true;
        public override ReadOnlySpan<byte> GetRowSpan(int row) => _rows[row];
    }
}
