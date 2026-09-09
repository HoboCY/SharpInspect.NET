using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Virtual;

/// <summary>
/// Explicit development-only preview input for one virtual camera scenario.
/// Preview images and automatic read-backs are immutable simulator facts; they
/// do not describe a physical camera or a production configuration.
/// </summary>
public sealed class VirtualCameraPreviewScenario
{
    public const int MaximumImages = 64;
    public const long MaximumImageBytes = 16L * 1024 * 1024;
    public const long MaximumTotalImageBytes = VirtualCameraScenario.MaximumImageBytes;
    public const int MaximumFrameRateHz = 10;
    public static readonly TimeSpan DefaultFrameInterval =
        TimeSpan.FromMilliseconds(100);

    public VirtualCameraPreviewScenario(IEnumerable<VirtualCameraImage> images,
        PreviewCameraProcessSettings? automaticOnceReadback = null,
        PreviewCameraProcessSettings? automaticContinuousReadback = null,
        TimeSpan? frameInterval = null,
        CameraPreviewCapabilities? capabilities = null)
    {
        Images = VirtualCameraContract.Copy(images, MaximumImages, nameof(images));
        if (Images.Count == 0)
            throw new ArgumentException("VirtualCameraPreviewImagesRequired", nameof(images));

        var first = Images[0];
        long imageBytes = 0;
        long maximumFrameBytes = 0;
        foreach (var image in Images)
        {
            if (image.StrideBytes != first.StrideBytes || image.Width != first.Width ||
                image.Height != first.Height || image.PixelFormat != first.PixelFormat ||
                image.ValidBits != first.ValidBits)
                throw new ArgumentException("VirtualCameraPreviewImageLayoutMismatch", nameof(images));
            var layoutBytes = checked((long)image.StrideBytes * image.Height);
            if (layoutBytes > MaximumImageBytes)
                throw new ArgumentException("VirtualCameraPreviewImageCapacityExceeded", nameof(images));
            imageBytes = checked(imageBytes + layoutBytes);
            if (imageBytes > MaximumTotalImageBytes)
                throw new ArgumentException("VirtualCameraPreviewImageCapacityExceeded", nameof(images));
            maximumFrameBytes = Math.Max(maximumFrameBytes, layoutBytes);
        }
        ImageBytes = imageBytes;
        MaximumFrameBytes = checked((int)maximumFrameBytes);

        AutomaticOnceReadback = ValidateReadback(automaticOnceReadback, first,
            nameof(automaticOnceReadback));
        AutomaticContinuousReadback = ValidateReadback(automaticContinuousReadback, first,
            nameof(automaticContinuousReadback));
        Capabilities = capabilities ?? new CameraPreviewCapabilities(
            supported: true,
            automaticExposure: AutomaticOnceReadback is not null ||
                AutomaticContinuousReadback is not null,
            automaticGain: AutomaticOnceReadback is not null ||
                AutomaticContinuousReadback is not null,
            automaticWhiteBalance: first.PixelFormat == VisionPixelFormat.Bgr24 &&
                (AutomaticOnceReadback?.WhiteBalanceRgb is not null ||
                 AutomaticContinuousReadback?.WhiteBalanceRgb is not null));
        if (!Capabilities.Supported &&
            (AutomaticOnceReadback is not null || AutomaticContinuousReadback is not null))
            throw new ArgumentException("VirtualCameraPreviewCapabilityReadbackMismatch",
                nameof(capabilities));
        if (Capabilities.AutomaticWhiteBalance &&
            (first.PixelFormat != VisionPixelFormat.Bgr24 ||
             (AutomaticOnceReadback is not null && AutomaticOnceReadback.WhiteBalanceRgb is null) ||
             (AutomaticContinuousReadback is not null && AutomaticContinuousReadback.WhiteBalanceRgb is null)))
            throw new ArgumentException("VirtualCameraPreviewAutomaticWhiteBalanceInvalid",
                nameof(capabilities));

        FrameInterval = frameInterval ?? DefaultFrameInterval;
        if (FrameInterval < DefaultFrameInterval || FrameInterval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(frameInterval),
                "VirtualCameraPreviewFrameRateExceeded");
        ContentHash = ComputeContentHash();
    }

    public IReadOnlyList<VirtualCameraImage> Images { get; }
    public CameraPreviewCapabilities Capabilities { get; }
    public PreviewCameraProcessSettings? AutomaticOnceReadback { get; }
    public PreviewCameraProcessSettings? AutomaticContinuousReadback { get; }
    public TimeSpan FrameInterval { get; }
    public string ContentHash { get; }
    public long ImageBytes { get; }
    public int MaximumFrameBytes { get; }

    internal VirtualCameraImage ImageForSequence(long sequence) =>
        Images[checked((int)((sequence - 1) % Images.Count))];

    internal bool TryResolve(PreviewTuningConfiguration requested,
        out PreviewTuningConfiguration effective, out string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!MatchesLayout(requested.ProcessSettings))
        {
            effective = requested;
            reasonCode = "VirtualCameraPreviewConfigurationLayoutMismatch";
            return false;
        }

        var process = requested.ProcessSettings;
        var exposure = process.ExposureTimeUs;
        var gain = process.GainDb;
        var whiteBalance = process.WhiteBalanceRgb;

        if (!TryResolveAutomatic(requested.ExposureMode, Capabilities.AutomaticExposure,
                out var exposureSettings))
        {
            effective = requested;
            reasonCode = "VirtualCameraPreviewAutomaticExposureUnsupported";
            return false;
        }
        if (!TryResolveAutomatic(requested.GainMode, Capabilities.AutomaticGain,
                out var gainSettings))
        {
            effective = requested;
            reasonCode = "VirtualCameraPreviewAutomaticGainUnsupported";
            return false;
        }
        if (!TryResolveAutomatic(requested.WhiteBalanceMode,
                Capabilities.AutomaticWhiteBalance, out var whiteBalanceSettings))
        {
            effective = requested;
            reasonCode = "VirtualCameraPreviewAutomaticWhiteBalanceUnsupported";
            return false;
        }

        if (exposureSettings is not null) exposure = exposureSettings.ExposureTimeUs;
        if (gainSettings is not null) gain = gainSettings.GainDb;
        if (whiteBalanceSettings is not null)
            whiteBalance = whiteBalanceSettings.WhiteBalanceRgb;

        var resolvedProcess = exposure == process.ExposureTimeUs && gain == process.GainDb &&
            Equals(whiteBalance, process.WhiteBalanceRgb)
            ? process
            : new PreviewCameraProcessSettings(exposure, gain, process.RegionOfInterest,
                process.PixelFormat, process.ValidBits, whiteBalance);
        effective = new PreviewTuningConfiguration(resolvedProcess,
            requested.ExposureMode, requested.GainMode, requested.WhiteBalanceMode);
        reasonCode = "VirtualCameraPreviewConfigurationApplied";
        return true;

        bool TryResolveAutomatic(PreviewAutomaticControlMode mode, bool supported,
            out PreviewCameraProcessSettings? settings)
        {
            settings = null;
            if (mode == PreviewAutomaticControlMode.Off)
                return true;
            if (!supported)
                return false;
            settings = mode == PreviewAutomaticControlMode.Once
                ? AutomaticOnceReadback : AutomaticContinuousReadback;
            return settings is not null;
        }
    }

    internal bool MatchesLayout(PreviewCameraProcessSettings settings)
    {
        var image = Images[0];
        return settings.PixelFormat == image.PixelFormat &&
            settings.ValidBits == image.ValidBits &&
            settings.RegionOfInterest.Width == image.Width &&
            settings.RegionOfInterest.Height == image.Height;
    }

    private static PreviewCameraProcessSettings? ValidateReadback(
        PreviewCameraProcessSettings? settings, VirtualCameraImage image,
        string parameterName)
    {
        if (settings is null) return null;
        if (settings.PixelFormat != image.PixelFormat || settings.ValidBits != image.ValidBits ||
            settings.RegionOfInterest.Width != image.Width ||
            settings.RegionOfInterest.Height != image.Height)
            throw new ArgumentException("VirtualCameraPreviewReadbackLayoutMismatch", parameterName);
        return settings;
    }

    private string ComputeContentHash()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write("SharpInspect.VirtualCameraPreview.v1");
        writer.Write(Images.Count);
        foreach (var image in Images)
        {
            writer.Write(image.Id);
            writer.Write(image.ContentHash);
        }
        writer.Write(Capabilities.Supported);
        writer.Write(Capabilities.AutomaticExposure);
        writer.Write(Capabilities.AutomaticGain);
        writer.Write(Capabilities.AutomaticWhiteBalance);
        writer.Write(AutomaticOnceReadback?.ContentHash ?? string.Empty);
        writer.Write(AutomaticContinuousReadback?.ContentHash ?? string.Empty);
        writer.Write(FrameInterval.Ticks);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

public sealed partial class VirtualCameraScenario
{
    /// <summary>Explicit preview input; null preserves the legacy scenario contract.</summary>
    public VirtualCameraPreviewScenario? Preview { get; private set; }
}

internal sealed partial class VirtualCameraDevice
{
    private static readonly CameraPreviewCapabilities UnsupportedPreviewCapabilities =
        new(false, false, false, false);
    private readonly object _previewGate = new();
    private PreviewState? _previewState;
    private int _previewActive;

    public CameraPreviewCapabilities PreviewCapabilities =>
        _scenario.Preview?.Capabilities ?? UnsupportedPreviewCapabilities;

    internal bool IsPreviewActive() => Volatile.Read(ref _previewActive) != 0;

    public ValueTask<CameraPreviewConfigurationResult> StartPreviewAsync(Guid sessionId,
        PreviewTuningConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (sessionId == Guid.Empty)
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                "VirtualCameraPreviewSessionInvalid"));
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                "VirtualCameraPreviewCancelled"));
        var scenario = _scenario.Preview;
        if (scenario is null || !scenario.Capabilities.Supported)
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                "VirtualCameraPreviewUnsupported"));

        lock (_previewGate)
        {
            if (_previewState is not null || IsPreviewActive())
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "VirtualCameraPreviewAlreadyStarted"));
            lock (_gate)
            {
                if (_disposed)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "VirtualCameraDisposed"));
                if (_connection != CameraConnectionState.Open)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "VirtualCameraNotOpen"));
                if (_configuration != CameraConfigurationState.Applied)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "VirtualCameraPreviewConfigurationRequired"));
                if (_pendingConfiguration is not null || _pendingAcquisition is not null ||
                    _acquisition != CameraAcquisitionState.Stopped)
                    return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                        "VirtualCameraPreviewProductionBusy"));
                Volatile.Write(ref _previewActive, 1);
                _acquisition = CameraAcquisitionState.Previewing;
            }

            if (!scenario.TryResolve(configuration, out var effective, out var reasonCode))
            {
                ResetPreviewAcquisition();
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(reasonCode));
            }

            var state = new PreviewState(sessionId, configuration, effective);
            try
            {
                _previewState = state;
                GeneratePreviewFrameLocked(state, scenario);
                ScheduleNextPreviewFrameLocked(state, scenario);
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(effective));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _previewState = null;
                ResetPreviewAcquisition();
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "VirtualCameraPreviewStartFailed"));
            }
        }
    }

    public ValueTask<CameraPreviewConfigurationResult> ApplyPreviewTuningAsync(
        PreviewTuningConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                "VirtualCameraPreviewCancelled"));
        var scenario = _scenario.Preview;
        if (scenario is null || !scenario.Capabilities.Supported)
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                "VirtualCameraPreviewUnsupported"));

        lock (_previewGate)
        {
            if (_previewState is not { } state)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "VirtualCameraPreviewNotStarted"));
            if (state.FailureReason is not null)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    state.FailureReason));
            if (!scenario.TryResolve(configuration, out var effective, out var reasonCode))
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(reasonCode));
            state.RequestedConfiguration = configuration;
            state.EffectiveConfiguration = effective;
            try
            {
                GeneratePreviewFrameLocked(state, scenario);
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(effective));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                state.FailureReason = "VirtualCameraPreviewTuningFailed";
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    state.FailureReason));
            }
        }
    }

    public ValueTask<CameraPreviewConfigurationResult> FreezeFixedPreviewConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                "VirtualCameraPreviewCancelled"));
        lock (_previewGate)
        {
            if (_previewState is not { } state)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    "VirtualCameraPreviewNotStarted"));
            if (state.FailureReason is not null)
                return ValueTask.FromResult(CameraPreviewConfigurationResult.Failure(
                    state.FailureReason));
            var fixedConfiguration = new PreviewTuningConfiguration(
                state.EffectiveConfiguration.ProcessSettings);
            state.RequestedConfiguration = fixedConfiguration;
            state.EffectiveConfiguration = fixedConfiguration;
            return ValueTask.FromResult(CameraPreviewConfigurationResult.Success(fixedConfiguration));
        }
    }

    public ValueTask<CameraPreviewFrameResult> ReadLatestPreviewFrameAsync(long afterSequence,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < -1)
            return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                "VirtualCameraPreviewSequenceInvalid"));
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                "VirtualCameraPreviewCancelled"));
        lock (_previewGate)
        {
            if (_previewState is not { } state)
                return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                    "VirtualCameraPreviewNotStarted"));
            if (state.FailureReason is not null)
                return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                    state.FailureReason));
            if (state.ReadInProgress)
                return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                    "VirtualCameraPreviewReadBusy"));
            state.ReadInProgress = true;
            try
            {
                if (state.Latest is null)
                    return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                        "VirtualCameraPreviewFrameNotReady"));
                if (state.Latest.Sequence <= afterSequence)
                    return ValueTask.FromResult(CameraPreviewFrameResult.Failure(
                        "VirtualCameraPreviewNoNewFrame"));
                return ValueTask.FromResult(CameraPreviewFrameResult.Success(state.Latest));
            }
            finally { state.ReadInProgress = false; }
        }
    }

    public ValueTask<CameraOperationResult> StopPreviewAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(CameraOperationResult.Failure(
                "VirtualCameraPreviewCancelled"));
        if (_scenario.Preview is null || !_scenario.Preview.Capabilities.Supported)
            return ValueTask.FromResult(CameraOperationResult.Failure(
                "VirtualCameraPreviewUnsupported"));
        StopPreviewForLifecycle();
        return ValueTask.FromResult(CameraOperationResult.Success("VirtualCameraPreviewStopped"));
    }

    internal void StopPreviewForLifecycle()
    {
        IDisposable? schedule;
        lock (_previewGate)
        {
            var state = _previewState;
            _previewState = null;
            schedule = state?.Schedule;
            if (state is not null)
            {
                state.Stopped = true;
                state.Schedule = null;
            }
        }
        schedule?.Dispose();
        lock (_gate)
        {
            if (!_disposed && _acquisition == CameraAcquisitionState.Previewing)
                _acquisition = CameraAcquisitionState.Stopped;
        }
        Volatile.Write(ref _previewActive, 0);
    }

    private void ResetPreviewAcquisition()
    {
        lock (_gate)
        {
            if (!_disposed && _acquisition == CameraAcquisitionState.Previewing)
                _acquisition = CameraAcquisitionState.Stopped;
        }
        Volatile.Write(ref _previewActive, 0);
    }

    private void GeneratePreviewFrameLocked(PreviewState state,
        VirtualCameraPreviewScenario scenario)
    {
        var sequence = checked(state.Sequence + 1);
        var image = scenario.ImageForSequence(sequence);
        state.Latest = new CameraPreviewFrame(state.SessionId, sequence,
            _clock.GetTimePoint(), image.Width, image.Height, image.StrideBytes,
            image.PixelFormat, image.ValidBits, image.PixelBytes);
        state.Sequence = sequence;
    }

    private void ScheduleNextPreviewFrameLocked(PreviewState state,
        VirtualCameraPreviewScenario scenario)
    {
        var due = checked(_clock.Timestamp + scenario.FrameInterval.Ticks);
        state.Schedule = _clock.Schedule(due, FrameAcquisitionClockPhase.FrameObservation,
            () => OnPreviewClock(state));
    }

    private void OnPreviewClock(PreviewState state)
    {
        lock (_previewGate)
        {
            if (!ReferenceEquals(_previewState, state) || state.Stopped)
                return;
            state.Schedule = null;
            var scenario = _scenario.Preview;
            if (scenario is null || state.FailureReason is not null)
                return;
            try
            {
                GeneratePreviewFrameLocked(state, scenario);
                ScheduleNextPreviewFrameLocked(state, scenario);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                state.FailureReason = "VirtualCameraPreviewFrameGenerationFailed";
            }
        }
    }

    private sealed class PreviewState
    {
        internal PreviewState(Guid sessionId, PreviewTuningConfiguration requested,
            PreviewTuningConfiguration effective)
        {
            SessionId = sessionId;
            RequestedConfiguration = requested;
            EffectiveConfiguration = effective;
        }

        internal readonly Guid SessionId;
        internal long Sequence;
        internal PreviewTuningConfiguration RequestedConfiguration;
        internal PreviewTuningConfiguration EffectiveConfiguration;
        internal CameraPreviewFrame? Latest;
        internal IDisposable? Schedule;
        internal string? FailureReason;
        internal bool ReadInProgress;
        internal bool Stopped;
    }
}
