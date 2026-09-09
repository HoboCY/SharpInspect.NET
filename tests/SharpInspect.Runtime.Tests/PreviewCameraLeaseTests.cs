using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationCameraTests
{
    [Fact]
    public async Task V133_R01_PreviewCandidateCannotCommitAndSafeClosesWithoutBaseline()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var candidate = new PreviewFakeDevice(candidateInner);
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        await BindAsync(harness, identity, "preview-r01-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);

        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);

        var sessionId = Guid.NewGuid();
        var started = await lease.StartPreviewAsync(sessionId, PreviewConfiguration(20),
            CancellationToken.None);
        Assert.True(started.Completed, started.ReasonCode);
        Assert.True(started.Result!.Succeeded, started.Result.ReasonCode);
        Assert.False(lease.Commit());
        Assert.False(candidateInner.Disposed);

        var stopped = await lease.StopPreviewAsync(CancellationToken.None);
        Assert.True(stopped.Completed, stopped.ReasonCode);
        Assert.True(stopped.Result!.Succeeded, stopped.Result.ReasonCode);

        var closed = await lease.RestoreAsync(null);
        Assert.True(closed.Succeeded, closed.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", closed.ReasonCode);
        Assert.True(candidateInner.Disposed);
        Assert.Equal(1, candidate.StartPreviewCalls);
        Assert.Equal(1, candidate.StopPreviewCalls);
    }

    [Fact]
    public async Task V133_R02_PreviewTuneFreezeAndRestorePreserveDurableConfiguration()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var baselineInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "baseline"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var restoredInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "restored"), Capabilities());
        var baselineDevice = new PreviewFakeDevice(baselineInner, supported: false);
        var candidate = new PreviewFakeDevice(candidateInner);
        var restoredDevice = new PreviewFakeDevice(restoredInner, supported: false);
        var provider = new FakeProvider(identity, binding, baselineDevice, candidate, restoredDevice);
        await using var harness = Create(provider);

        var baseline = await PrepareBaselineAsync(harness, identity, "preview-r02");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);

        var sessionId = Guid.NewGuid();
        var started = await lease.StartPreviewAsync(sessionId,
            PreviewConfiguration(20), CancellationToken.None);
        Assert.True(started.Completed, started.ReasonCode);
        Assert.True(started.Result!.Succeeded, started.Result.ReasonCode);

        var tuned = await lease.TunePreviewAsync(PreviewConfiguration(30, 1),
            CancellationToken.None);
        Assert.True(tuned.Completed, tuned.ReasonCode);
        Assert.True(tuned.Result!.Succeeded, tuned.Result.ReasonCode);
        Assert.Equal(30, tuned.Result.Configuration!.ProcessSettings.ExposureTimeUs);
        Assert.Equal(1, tuned.Result.Configuration.ProcessSettings.GainDb);

        var frozen = await lease.FreezePreviewAsync(CancellationToken.None);
        Assert.True(frozen.Completed, frozen.ReasonCode);
        Assert.True(frozen.Result!.Succeeded, frozen.Result.ReasonCode);
        Assert.Equal(PreviewAutomaticControlMode.Off, frozen.Result.Configuration!.ExposureMode);
        Assert.Equal(PreviewAutomaticControlMode.Off, frozen.Result.Configuration.GainMode);
        Assert.Equal(PreviewAutomaticControlMode.Off, frozen.Result.Configuration.WhiteBalanceMode);
        Assert.Equal(30, frozen.Result.Configuration.ProcessSettings.ExposureTimeUs);
        Assert.Equal(1, frozen.Result.Configuration.ProcessSettings.GainDb);
        Assert.Equal(1, candidate.StartPreviewCalls);
        Assert.Equal(1, candidate.TunePreviewCalls);
        Assert.Equal(1, candidate.FreezePreviewCalls);

        var stopped = await lease.StopPreviewAsync(CancellationToken.None);
        Assert.True(stopped.Completed, stopped.ReasonCode);
        Assert.True(stopped.Result!.Succeeded, stopped.Result.ReasonCode);

        var restored = await lease.RestoreAsync(baseline);
        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.Equal("CameraActivationRestored", restored.ReasonCode);
        Assert.Equal(baseline.Requested, restoredDevice.LastAppliedRequest);
        Assert.Equal(baseline.Effective, restored.Snapshot!.Effective);
        Assert.Equal(baseline.Health.Configuration, restored.Snapshot.Health.Configuration);
        Assert.Equal(baseline.Health.Acquisition, restored.Snapshot.Health.Acquisition);
        Assert.True(candidateInner.Disposed);
        Assert.False(restoredInner.Disposed);
    }

    [Fact]
    public async Task V133_R03_CancelledPreviewReadRetiresPromptlyAndRestoresHealthyCandidate()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var baselineInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "baseline"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var restoredInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "restored"), Capabilities());
        var baselineDevice = new PreviewFakeDevice(baselineInner, supported: false);
        var candidate = new PreviewFakeDevice(candidateInner);
        var restoredDevice = new PreviewFakeDevice(restoredInner, supported: false);
        var provider = new FakeProvider(identity, binding, baselineDevice, candidate, restoredDevice);
        await using var harness = Create(provider);

        var baseline = await PrepareBaselineAsync(harness, identity, "preview-r03");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);
        var sessionId = Guid.NewGuid();
        var started = await lease.StartPreviewAsync(sessionId,
            PreviewConfiguration(20), CancellationToken.None);
        Assert.True(started.Result!.Succeeded, started.Result.ReasonCode);

        candidate.ReadTaskHandler = (_, token) => WaitForBudgetCancellationAsync(
            candidate, token);
        using var callerCancellation = new CancellationTokenSource();
        var readTask = lease.ReadPreviewFrameAsync(0, callerCancellation.Token).AsTask();
        await candidate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        callerCancellation.Cancel();
        await candidate.BudgetCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(read.Completed);
        Assert.Equal("PreviewOperationCancelled", read.ReasonCode);
        Assert.False(read.DeviceTransferred);
        Assert.False(candidateInner.Disposed);

        var stopped = await lease.StopPreviewAsync(CancellationToken.None);
        Assert.True(stopped.Completed, stopped.ReasonCode);
        Assert.True(stopped.Result!.Succeeded, stopped.Result.ReasonCode);
        var restored = await lease.RestoreAsync(baseline);
        Assert.True(restored.Succeeded, restored.ReasonCode);
        Assert.True(candidateInner.Disposed);
        Assert.False(restoredInner.Disposed);
    }

    [Fact]
    public async Task V133_R04_IgnoredPreviewReadTimeoutTransfersDeviceAndBlocksReopen()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var baselineInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "baseline"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var restoredInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "restored"), Capabilities());
        var baselineDevice = new PreviewFakeDevice(baselineInner, supported: false);
        var candidate = new PreviewFakeDevice(candidateInner);
        var restoredDevice = new PreviewFakeDevice(restoredInner, supported: false);
        var provider = new FakeProvider(identity, binding, baselineDevice, candidate, restoredDevice);
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(500));

        var baseline = await PrepareBaselineAsync(harness, identity, "preview-r04");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);
        var sessionId = Guid.NewGuid();
        var started = await lease.StartPreviewAsync(sessionId,
            PreviewConfiguration(20), CancellationToken.None);
        Assert.True(started.Result!.Succeeded, started.Result.ReasonCode);

        candidate.ReadTaskHandler = (_, _) => candidate.ReadRelease.Task;
        var readTask = lease.ReadPreviewFrameAsync(0, CancellationToken.None).AsTask();
        await candidate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(read.Completed);
        Assert.Equal("PreviewOperationTimeout", read.ReasonCode);
        Assert.True(read.DeviceTransferred);
        Assert.False(candidateInner.Disposed);
        var openCountBeforeRestore = provider.OpenCount;

        var restore = await lease.RestoreAsync(baseline);
        Assert.False(restore.Succeeded);
        Assert.Equal("CameraDeviceCleanupRequired", restore.ReasonCode);
        Assert.Equal(openCountBeforeRestore, provider.OpenCount);
        Assert.False(restoredInner.Disposed);

        candidate.ReadRelease.TrySetResult(CameraPreviewFrameResult.Success(
            PreviewFrame(sessionId)));
        await candidateInner.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, candidate.StopPreviewCalls);
        Assert.True(candidate.StopCalls >= 1);
        Assert.True(candidateInner.Disposed);
    }

    [Fact]
    public async Task V133_R05_FirstPreviewPhysicalClaimRejectsWithoutStartAndRestoresOwner()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var candidate = new PreviewFakeDevice(candidateInner);
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        await BindAsync(harness, identity, "preview-r05-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);
        Assert.False(candidateInner.Disposed);

        var started = await lease.StartPreviewAsync(Guid.NewGuid(), PreviewConfiguration(20),
            CancellationToken.None, () =>
                RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationCancelled"));
        Assert.False(started.Completed);
        Assert.Equal("RecipeActivationCancelled", started.ReasonCode);
        Assert.Null(started.Result);
        Assert.Equal(0, candidate.StartPreviewCalls);
        Assert.False(lease.Commit());
        Assert.False(candidateInner.Disposed);

        var closed = await lease.RestoreAsync(null);
        Assert.True(closed.Succeeded, closed.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", closed.ReasonCode);
        Assert.True(candidateInner.Disposed);
    }

    [Fact]
    public async Task V133_R06_ManualPreviewReadBackLieIsRejectedAndCandidateIsClosed()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var candidate = new PreviewFakeDevice(candidateInner)
        {
            LieAboutConfigurationReadBack = true
        };
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        await BindAsync(harness, identity, "preview-r06-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);

        var started = await lease.StartPreviewAsync(Guid.NewGuid(), PreviewConfiguration(20),
            CancellationToken.None);
        Assert.True(started.Completed, started.ReasonCode);
        Assert.False(started.Result!.Succeeded);
        Assert.Equal("PreviewConfigurationReadBackMismatch", started.Result.ReasonCode);
        Assert.Equal(1, candidate.StartPreviewCalls);

        var closed = await lease.RestoreAsync(null);
        Assert.True(closed.Succeeded, closed.ReasonCode);
        Assert.True(candidateInner.Disposed);
    }

    [Fact]
    public async Task V133_R07_SuccessfulStartWithStoppedHealthIsRejected()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var candidate = new PreviewFakeDevice(candidateInner)
        {
            LieAboutPreviewHealth = true
        };
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        await BindAsync(harness, identity, "preview-r07-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);

        var started = await lease.StartPreviewAsync(Guid.NewGuid(), PreviewConfiguration(20),
            CancellationToken.None);
        Assert.True(started.Completed, started.ReasonCode);
        Assert.False(started.Result!.Succeeded);
        Assert.Equal("PreviewAcquisitionReadBackMismatch", started.Result.ReasonCode);
        Assert.Equal(1, candidate.StartPreviewCalls);

        var closed = await lease.RestoreAsync(null);
        Assert.True(closed.Succeeded, closed.ReasonCode);
        Assert.True(candidateInner.Disposed);
    }

    [Fact]
    public async Task V133_R08_CancellationRaceKeepsCompletedProviderResultAndAllowsRestore()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var candidateInner = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var candidate = new PreviewFakeDevice(candidateInner);
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        await BindAsync(harness, identity, "preview-r08-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var applied = await lease.ApplyAsync(Request(20));
        Assert.True(applied.Succeeded, applied.ReasonCode);
        var sessionId = Guid.NewGuid();
        var started = await lease.StartPreviewAsync(sessionId,
            PreviewConfiguration(20), CancellationToken.None);
        Assert.True(started.Result!.Succeeded, started.Result.ReasonCode);

        candidate.ReadTaskHandler = (_, token) =>
        {
            token.Register(() => candidate.BudgetCancellationObserved.TrySetResult(true));
            return candidate.ReadRelease.Task;
        };
        using var callerCancellation = new CancellationTokenSource();
        var readTask = lease.ReadPreviewFrameAsync(0, callerCancellation.Token).AsTask();
        await candidate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        await candidate.BudgetCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        candidate.ReadRelease.TrySetResult(CameraPreviewFrameResult.Success(
            PreviewFrame(sessionId)));

        var read = await readTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(read.Completed, read.ReasonCode);
        Assert.True(read.Result!.Succeeded, read.Result.ReasonCode);
        Assert.False(read.DeviceTransferred);
        Assert.False(candidateInner.Disposed);

        var stopped = await lease.StopPreviewAsync(CancellationToken.None);
        Assert.True(stopped.Completed, stopped.ReasonCode);
        Assert.True(stopped.Result!.Succeeded, stopped.Result.ReasonCode);
        var closed = await lease.RestoreAsync(null);
        Assert.True(closed.Succeeded, closed.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", closed.ReasonCode);
        Assert.True(candidateInner.Disposed);
    }

    [Theory]
    [InlineData("PreviewRuntimeBusy")]
    [InlineData("PreviewOperationSuperseded")]
    [InlineData("PreviewPhysicalOperationInProgress")]
    public async Task V133_R09_RefusedReadClaimDoesNotTouchOrRetireTheCamera(string reason)
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var inner = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "preview-candidate"), Capabilities());
        var candidate = new PreviewFakeDevice(inner);
        await using var harness = Create(new FakeProvider(identity, binding, candidate));
        await BindAsync(harness, identity, "preview-r09-bind");
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True((await lease.ApplyAsync(Request(20))).Succeeded);
        Assert.True((await lease.StartPreviewAsync(Guid.NewGuid(), PreviewConfiguration(20),
            CancellationToken.None)).Result!.Succeeded);

        var refused = await lease.ReadPreviewFrameAsync(0, CancellationToken.None,
            () => RecipeActivationPhysicalPhaseClaim.Unavailable(reason));
        Assert.False(refused.Completed);
        Assert.Equal(reason, refused.ReasonCode);
        Assert.False(refused.DeviceTransferred);
        Assert.Equal(0, candidate.ReadPreviewCalls);
        Assert.False(inner.Disposed);

        var next = await lease.ReadPreviewFrameAsync(0, CancellationToken.None);
        Assert.True(next.Completed, next.ReasonCode);
        Assert.True(next.Result!.Succeeded, next.Result.ReasonCode);
        Assert.Equal(1, candidate.ReadPreviewCalls);
        Assert.True((await lease.StopPreviewAsync(CancellationToken.None)).Result!.Succeeded);
        Assert.True((await lease.RestoreAsync(null)).Succeeded);
        Assert.True(inner.Disposed);
    }

    private static async Task BindAsync(RuntimeHarness harness,
        CameraProviderIdentity identity, string changeReason)
    {
        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), changeReason));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
    }

    private static async Task<CameraSetupSnapshot> PrepareBaselineAsync(
        RuntimeHarness harness, CameraProviderIdentity identity, string prefix)
    {
        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), prefix + "-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baseline = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(10), prefix + "-baseline"));
        Assert.True(baseline.Succeeded, baseline.ReasonCode);
        return baseline.Snapshot!;
    }

    private static PreviewTuningConfiguration PreviewConfiguration(double exposure,
        double gain = 0, PreviewAutomaticControlMode exposureMode = PreviewAutomaticControlMode.Off,
        PreviewAutomaticControlMode gainMode = PreviewAutomaticControlMode.Off,
        PreviewAutomaticControlMode whiteBalanceMode = PreviewAutomaticControlMode.Off) =>
        new(new PreviewCameraProcessSettings(exposure, gain,
                new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null),
            exposureMode, gainMode, whiteBalanceMode);

    private static CameraPreviewFrame PreviewFrame(Guid sessionId, long sequence = 1) =>
        new(sessionId, sequence,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())),
            640, 480, 640, VisionPixelFormat.Mono8, null, new byte[640 * 480]);

    private static async Task<CameraPreviewFrameResult> WaitForBudgetCancellationAsync(
        PreviewFakeDevice candidate, CancellationToken token)
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() =>
        {
            candidate.BudgetCancellationObserved.TrySetResult(true);
            release.TrySetResult(true);
        });
        await release.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return CameraPreviewFrameResult.Failure("PreviewReadUnexpectedlyCompleted");
    }

    private sealed class PreviewFakeDevice : ICameraDevice, ICameraPreviewDevice
    {
        private readonly FakeDevice _inner;
        private readonly CameraPreviewCapabilities _previewCapabilities;
        private readonly object _gate = new();
        private bool _previewing;
        private Guid _sessionId;
        private PreviewTuningConfiguration? _configuration;
        private int _startPreviewCalls;
        private int _tunePreviewCalls;
        private int _freezePreviewCalls;
        private int _readPreviewCalls;
        private int _stopPreviewCalls;
        private int _stopCalls;

        internal PreviewFakeDevice(FakeDevice inner, bool supported = true,
            bool automaticExposure = false, bool automaticGain = false,
            bool automaticWhiteBalance = false)
        {
            _inner = inner;
            _previewCapabilities = new CameraPreviewCapabilities(supported, automaticExposure,
                automaticGain, automaticWhiteBalance);
        }

        internal bool LieAboutConfigurationReadBack { get; set; }
        internal bool LieAboutPreviewHealth { get; set; }
        internal Func<long, CancellationToken, Task<CameraPreviewFrameResult>>? ReadTaskHandler { get; set; }
        internal TaskCompletionSource<bool> ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> BudgetCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CameraPreviewFrameResult> ReadRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal RequestedCameraConfiguration? LastAppliedRequest { get; private set; }
        internal int StartPreviewCalls => Volatile.Read(ref _startPreviewCalls);
        internal int TunePreviewCalls => Volatile.Read(ref _tunePreviewCalls);
        internal int FreezePreviewCalls => Volatile.Read(ref _freezePreviewCalls);
        internal int ReadPreviewCalls => Volatile.Read(ref _readPreviewCalls);
        internal int StopPreviewCalls => Volatile.Read(ref _stopPreviewCalls);
        internal int StopCalls => Volatile.Read(ref _stopCalls);

        public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
        public CameraCapabilities Capabilities => _inner.Capabilities;
        public CameraPreviewCapabilities PreviewCapabilities => _previewCapabilities;

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            var health = _inner.GetHealthSnapshot();
            lock (_gate)
            {
                if (!_previewing || LieAboutPreviewHealth ||
                    health.Configuration != CameraConfigurationState.Applied)
                    return health;
                return new CameraHealthSnapshot(health.ProviderAvailability, health.Connection,
                    CameraConfigurationState.Applied, CameraAcquisitionState.Previewing,
                    health.ObservedAt, health.LastFault);
            }
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default)
        {
            LastAppliedRequest = requested;
            return _inner.ApplyConfigurationAsync(requested, cancellationToken);
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) => _inner.AcquireAsync(request, cancellationToken);

        public async ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            lock (_gate) _previewing = false;
            return await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate) _previewing = false;
            return _inner.DisposeAsync();
        }

        public ValueTask<CameraPreviewConfigurationResult> StartPreviewAsync(Guid sessionId,
            PreviewTuningConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startPreviewCalls);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_previewCapabilities.Supported)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "PreviewCapabilityUnavailable"));
            lock (_gate)
            {
                if (_previewing)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "PreviewAlreadyStarted"));
                _previewing = true;
                _sessionId = sessionId;
                var actual = ReadBack(configuration);
                _configuration = actual;
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(actual));
            }
        }

        public ValueTask<CameraPreviewConfigurationResult> ApplyPreviewTuningAsync(
            PreviewTuningConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _tunePreviewCalls);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_previewing)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "PreviewNotStarted"));
                var actual = ReadBack(configuration);
                _configuration = actual;
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(actual));
            }
        }

        public ValueTask<CameraPreviewConfigurationResult> FreezeFixedPreviewConfigurationAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _freezePreviewCalls);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_previewing || _configuration is null)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "PreviewNotStarted"));
                var actual = new PreviewTuningConfiguration(_configuration.ProcessSettings);
                _configuration = actual;
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(actual));
            }
        }

        public ValueTask<CameraPreviewFrameResult> ReadLatestPreviewFrameAsync(long afterSequence,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readPreviewCalls);
            ReadStarted.TrySetResult(true);
            cancellationToken.ThrowIfCancellationRequested();
            Guid sessionId;
            lock (_gate)
            {
                if (!_previewing)
                    return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                        "PreviewNotStarted"));
                sessionId = _sessionId;
            }
            if (ReadTaskHandler is { } handler)
                return new ValueTask<CameraPreviewFrameResult>(handler(afterSequence, cancellationToken));
            if (afterSequence == long.MaxValue)
                return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                    "PreviewSequenceExhausted"));
            return ValueTask.FromResult(CameraPreviewFrameResult.Success(
                PreviewFrame(sessionId, checked(afterSequence + 1))));
        }

        public ValueTask<CameraOperationResult> StopPreviewAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopPreviewCalls);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_previewing)
                    return ValueTask.FromResult(CameraOperationResult.Failure("PreviewNotStarted"));
                _previewing = false;
            }
            return ValueTask.FromResult(CameraOperationResult.Success("PreviewStopped"));
        }

        private PreviewTuningConfiguration ReadBack(PreviewTuningConfiguration requested)
        {
            if (!LieAboutConfigurationReadBack)
                return requested;
            var process = requested.ProcessSettings;
            return new PreviewTuningConfiguration(new PreviewCameraProcessSettings(
                process.ExposureTimeUs + 10, process.GainDb, process.RegionOfInterest,
                process.PixelFormat, process.ValidBits, process.WhiteBalanceRgb),
                requested.ExposureMode, requested.GainMode, requested.WhiteBalanceMode);
        }
    }
}
