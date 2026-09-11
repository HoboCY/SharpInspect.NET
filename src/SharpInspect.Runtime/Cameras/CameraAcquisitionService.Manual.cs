using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cameras;

public sealed partial class CameraAcquisitionService
{
    /// <summary>
    /// Enables the Runtime-owned manual correlation path.  The service is still a
    /// one-attempt owner and this flag never makes the public production seam
    /// available.
    /// </summary>
    internal void EnableManualAcquisition()
    {
        lock (_sync)
        {
            if (_disposed || _attempt is not null || _admissionInProgress ||
                _calibrationEnabled || _manualEnabled || _qualificationSessionEnabled || _productionOwnedEnabled)
                throw new InvalidOperationException("CameraManualAcquisitionUnavailable");
            _manualEnabled = true;
        }
    }

    /// <summary>
    /// Internal manual acquisition.  The Runtime supplies the correlation so a
    /// caller cannot manufacture a manual run through the public acquisition API.
    /// </summary>
    internal ValueTask<CameraAcquisitionAttempt> AcquireManualAsync(
        ExecutionCorrelationId correlation, string logicalCameraRole,
        CancellationToken cancellationToken = default)
    {
        if (correlation is null || correlation.Kind != ExecutionKind.Manual ||
            correlation.Value == Guid.Empty)
            return new(AcquireRejected("CameraManualCorrelationInvalid"));

        return new(AcquireCoreAsync(ExecutionKind.Manual, logicalCameraRole,
            cancellationToken, correlation));
    }

    private static CameraAcquisitionAttempt AcquireRejected(string reasonCode) =>
        new(false, reasonCode, null, null);
}

/// <summary>
/// A non-owning controlled-device view used by the manual acquisition service.
/// The activation lease remains the sole owner of the raw provider device.  The
/// view starts the already configured candidate before forwarding the controlled
/// acquisition, while its DisposeAsync deliberately does not dispose that device.
/// </summary>
internal sealed class ManualControlledCameraBorrow : IControlledCameraDevice
{
    private readonly IControlledCameraDevice _inner;

    private readonly Func<RecipeActivationPhysicalPhaseClaim>? _physicalPhaseFactory;

    internal ManualControlledCameraBorrow(IControlledCameraDevice inner,
        Func<RecipeActivationPhysicalPhaseClaim>? physicalPhaseFactory = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _physicalPhaseFactory = physicalPhaseFactory;
    }

    public CameraDeviceDescriptor Descriptor => _inner.Descriptor;
    public CameraCapabilities Capabilities => _inner.Capabilities;
    public CameraHealthSnapshot GetHealthSnapshot() => _inner.GetHealthSnapshot();

    public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
        RequestedCameraConfiguration requested, CancellationToken cancellationToken = default) =>
        _inner.ApplyConfigurationAsync(requested, cancellationToken);

    public ValueTask<CameraOperationResult> StartAsync(
        CancellationToken cancellationToken = default) => _inner.StartAsync(cancellationToken);

    public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.AcquireAsync(request, cancellationToken);

    public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
        FrameAcquisitionControl control, CancellationToken cancellationToken = default) =>
        StartAndAcquireAsync(request, control, cancellationToken);

    public ValueTask<CameraOperationResult> StopAsync(
        CancellationToken cancellationToken = default) => _inner.StopAsync(cancellationToken);

    public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
        int maximumCount = 64) => _inner.ReadProtocolObservations(afterSequence, maximumCount);

    /// <summary>Only the activation lease may dispose the raw camera.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask<FrameAcquisitionResult> StartAndAcquireAsync(
        FrameAcquisitionRequest request, FrameAcquisitionControl control,
        CancellationToken cancellationToken)
    {
        CameraOperationResult? started;
        RecipeActivationPhysicalPhaseClaim? startPhase = null;
        try
        {
            if (!TryBeginPhysicalPhase(out startPhase, out var phaseFailure))
                return Failure(CameraAcquisitionFailureKind.DeviceFault, phaseFailure);
            started = await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(CameraAcquisitionFailureKind.Cancelled,
                "CameraManualAcquisitionCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(CameraAcquisitionFailureKind.DeviceFault,
                "CameraManualDeviceStartFailed");
        }
        finally
        {
            startPhase?.Dispose();
        }

        if (started is null || !started.Succeeded)
            return Failure(CameraAcquisitionFailureKind.DeviceFault,
                started?.ReasonCode ?? "CameraManualDeviceStartFailed");

        RecipeActivationPhysicalPhaseClaim? acquirePhase = null;
        try
        {
            if (!TryBeginPhysicalPhase(out acquirePhase, out var phaseFailure))
                return Failure(CameraAcquisitionFailureKind.DeviceFault, phaseFailure);
            var result = await _inner.AcquireAsync(request, control, cancellationToken)
                .ConfigureAwait(false);
            return result ?? Failure(CameraAcquisitionFailureKind.DeviceFault,
                "CameraManualAcquisitionResultMissing");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(CameraAcquisitionFailureKind.Cancelled,
                "CameraManualAcquisitionCancelled");
        }
        catch (OperationCanceledException)
        {
            return Failure(CameraAcquisitionFailureKind.DeviceFault,
                "CameraManualAcquisitionCancelledByDevice");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(CameraAcquisitionFailureKind.DeviceFault,
                "CameraManualAcquisitionFailed");
        }
        finally
        {
            acquirePhase?.Dispose();
        }
    }

    private bool TryBeginPhysicalPhase(
        out RecipeActivationPhysicalPhaseClaim? claim, out string reasonCode)
    {
        claim = null;
        reasonCode = "CameraManualPhysicalPhaseUnavailable";
        if (_physicalPhaseFactory is null) return true;

        try
        {
            claim = _physicalPhaseFactory();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }

        if (claim is { Available: true }) return true;
        reasonCode = claim?.Failure ?? reasonCode;
        claim?.Dispose();
        claim = null;
        return false;
    }

    private static FrameAcquisitionResult Failure(CameraAcquisitionFailureKind kind,
        string reasonCode) => FrameAcquisitionResult.FailureResult(
            new CameraAcquisitionFailure(kind, reasonCode));
}
