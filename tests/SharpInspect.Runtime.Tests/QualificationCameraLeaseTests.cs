using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationCameraTests
{
    [Fact]
    public async Task V137_A03_OrdinaryPreparedLeaseCannotAcquireQualificationOrClaimItsOwnership()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new(identity, "Camera:One", "binding"), Capabilities());
        var candidate = new QualificationCountingDevice(
            new FakeDevice(new(identity, "Camera:One", "candidate"), Capabilities()));
        await using var harness = Create(new FakeProvider(identity, binding, candidate));
        await BindAsync(harness, identity, "qualification-a03-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);
        Assert.True((await lease.ApplyAsync(Request(20))).Succeeded);
        using var pool = new FrameBufferPool(new(1, 1024, TimeSpan.FromSeconds(1)));
        using var clock = new SystemFrameAcquisitionClock();

        var rejected = await lease.AcquireQualificationFrameAsync(
            new(ExecutionKind.Qualification, Guid.NewGuid()), pool, clock);

        Assert.False(rejected.Succeeded);
        Assert.Equal("CameraQualificationLeaseRequired", rejected.ReasonCode);
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.AcquireCalls);
        // A rejected qualification call must not convert the ordinary activation lease.
        Assert.True(lease.Commit());
    }

    [Fact]
    public async Task V137_A04_QualificationReservationRejectsCommitPreviewAndManualAcquisition()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new(identity, "Camera:One", "binding"), Capabilities());
        var candidate = new QualificationCountingDevice(
            new FakeDevice(new(identity, "Camera:One", "candidate"), Capabilities()));
        await using var harness = Create(new FakeProvider(identity, binding, candidate));
        await BindAsync(harness, identity, "qualification-a04-bind");
        await using var lease = await harness.Runtime.ReserveQualificationAcquisitionAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);
        Assert.True((await lease.ApplyAsync(Request(20))).Succeeded);
        Assert.False(lease.Commit());
        var preview = await lease.StartPreviewAsync(Guid.NewGuid(), PreviewConfiguration(20), CancellationToken.None);
        Assert.False(preview.Completed);
        using var pool = new FrameBufferPool(new(1, 1024, TimeSpan.FromSeconds(1)));
        using var clock = new SystemFrameAcquisitionClock();
        var manual = await lease.AcquireManualFrameAsync(new(ExecutionKind.Manual, Guid.NewGuid()), pool, clock);
        Assert.False(manual.Succeeded);
        Assert.Equal("CameraNonProductionOwnerConflict", manual.ReasonCode);
        Assert.Equal(0, candidate.StartCalls);
        Assert.Equal(0, candidate.AcquireCalls);
        var closed = await lease.RestoreAsync(null);
        Assert.True(closed.Succeeded, closed.ReasonCode);
    }

    private sealed class QualificationCountingDevice : ICameraDevice
    {
        private readonly ICameraDevice _inner;
        internal QualificationCountingDevice(ICameraDevice inner) => _inner = inner;
        internal int StartCalls { get; private set; }
        internal int AcquireCalls { get; private set; }
        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;
        public CameraHealthSnapshot GetHealthSnapshot() => _inner.GetHealthSnapshot();
        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default) => _inner.ApplyConfigurationAsync(requested, cancellationToken);
        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default)
        { StartCalls++; return _inner.StartAsync(cancellationToken); }
        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default)
        { AcquireCalls++; return _inner.AcquireAsync(request, cancellationToken); }
        public ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
