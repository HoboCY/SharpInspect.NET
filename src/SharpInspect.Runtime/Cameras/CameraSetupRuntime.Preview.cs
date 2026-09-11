using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Preview;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime.Cameras;

// A stage result never releases the camera lease. After any failed configuration
// stage the coordinator must Stop Preview and Restore the same lease. A cancelled
// frame read may instead be joined before another explicitly authorized stage.
internal sealed record PreviewDeviceCallResult<T>(bool Completed, string ReasonCode, T? Result,
    bool DeviceTransferred = false) where T : class;

internal sealed partial class CameraSetupRuntime
{
    // A Preview operation uses the same physical budget and late-device retirement
    // registry as activation. A returned timeout never abandons the device owner.
    internal async ValueTask<PreviewDeviceCallResult<T>> InvokePreviewDeviceAsync<T>(ICameraDevice device,
        Func<ICameraPreviewDevice, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken,
        Func<RecipeActivationPhysicalPhaseClaim>? phaseFactory = null) where T : class
    {
        if (device is not ICameraPreviewDevice preview)
            return new(false, "PreviewCapabilityUnavailable", null);
        if (cancellationToken.IsCancellationRequested)
            return new(false, "PreviewOperationCancelled", null);
        if (!TryReservePhysical()) return new(false, "CameraPhysicalCapacityExceeded", null);

        var budget = new CancellationTokenSource(PositiveTimeout(_options.OperationTimeout));
        var started = Stopwatch.GetTimestamp();
        Task<T>? providerTask = null;
        var transferred = false;
        using var cancellation = cancellationToken.Register(() => CancelPreviewBudget(budget));
        try
        {
            // Adapter calls may block before returning their ValueTask. They never
            // run on the Runtime command gate or the caller's cancellation thread.
            providerTask = Task.Run(async () =>
            {
                budget.Token.ThrowIfCancellationRequested();
                using var phase = phaseFactory?.Invoke();
                if (phase is { Available: false })
                    throw new PreviewPhaseRejectedException(phase.Failure ?? "PreviewSessionStopping");
                return await operation(preview, budget.Token).ConfigureAwait(false);
            });
            var result = await providerTask.WaitAsync(PositiveTimeout(_options.OperationTimeout),
                cancellationToken).ConfigureAwait(false);
            return result is null ? new(false, "PreviewProviderResultMissing", null) :
                new(true, "PreviewProviderOperationCompleted", result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CancelPreviewBudget(budget);
            if (exception is OperationCanceledException && providerTask is { IsCompleted: false })
            {
                // Ordinary cancellation gets the remaining original operation
                // budget to retire. A promptly cancelled frame read does not
                // transfer a healthy device to the late-operation quarantine.
                var elapsed = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - started) / Stopwatch.Frequency);
                var remaining = _options.OperationTimeout - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    try { await providerTask.WaitAsync(remaining).ConfigureAwait(false); }
                    catch (Exception retirement) when (retirement is not OutOfMemoryException) { }
                }
            }
            if (providerTask is { IsCompletedSuccessfully: true })
            {
                // A cancellation race cannot erase an actual completed hardware
                // operation. The coordinator still observes its synchronous exit
                // marker and restores before completing a cancelled command.
                var actual = providerTask.Result;
                return actual is null ? new(false, "PreviewProviderResultMissing", null) :
                    new(true, "PreviewProviderOperationCompleted", actual);
            }
            if (providerTask is { IsCompleted: false })
            {
                transferred = true;
                TrackPhysical(Task.Run(() => RetireLatePreviewDeviceAsync(providerTask, device, budget)));
            }
            var reason = exception is PreviewPhaseRejectedException rejected ? rejected.ReasonCode :
                exception is TimeoutException ? "PreviewOperationTimeout" :
                exception is OperationCanceledException ? "PreviewOperationCancelled" : "PreviewProviderOperationFailed";
            return new(false, reason, null, transferred);
        }
        finally
        {
            if (!transferred)
            {
                budget.Dispose();
                ReleasePhysical();
            }
        }
    }

    private sealed class PreviewPhaseRejectedException : Exception
    {
        internal PreviewPhaseRejectedException(string reasonCode) { ReasonCode = reasonCode; }
        internal string ReasonCode { get; }
    }

    private static void CancelPreviewBudget(CancellationTokenSource budget) =>
        _ = Task.Run(() =>
        {
            try { budget.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        });

    private async Task RetireLatePreviewDeviceAsync(Task providerTask, ICameraDevice device,
        CancellationTokenSource budget)
    {
        try
        {
            try { await providerTask.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            if (device is ICameraPreviewDevice preview)
            {
                try { await preview.StopPreviewAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
            try { await device.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            await DisposeDeviceUnboundedAsync(device).ConfigureAwait(false);
        }
        finally { budget.Dispose(); }
    }
}

internal sealed partial class RecipeActivationCameraLease
{
    // Once claimed by Preview this candidate can only be restored or safe-closed;
    // it can never be committed as a production activation.
    private bool _previewOwned;
    private PreviewTuningConfiguration? _latestPreviewConfiguration;

    internal ValueTask<PreviewDeviceCallResult<CameraPreviewConfigurationResult>> StartPreviewAsync(
        Guid sessionId, PreviewTuningConfiguration configuration, CancellationToken cancellationToken,
        Func<RecipeActivationPhysicalPhaseClaim>? phaseFactory = null) =>
        InvokePreviewAsync((device, token) => ConfigurePreviewDeviceAsync(device,
                () => device.StartPreviewAsync(sessionId, configuration, token), configuration, false),
            cancellationToken, starting: true, phaseFactory: phaseFactory);

    internal ValueTask<PreviewDeviceCallResult<CameraPreviewConfigurationResult>> TunePreviewAsync(
        PreviewTuningConfiguration configuration, CancellationToken cancellationToken,
        Func<RecipeActivationPhysicalPhaseClaim>? phaseFactory = null) =>
        InvokePreviewAsync((device, token) => ConfigurePreviewDeviceAsync(device,
                () => device.ApplyPreviewTuningAsync(configuration, token), configuration, false), cancellationToken,
            phaseFactory: phaseFactory);

    internal ValueTask<PreviewDeviceCallResult<CameraPreviewConfigurationResult>> FreezePreviewAsync(
        CancellationToken cancellationToken, Func<RecipeActivationPhysicalPhaseClaim>? phaseFactory = null) =>
        InvokePreviewAsync((device, token) => _latestPreviewConfiguration is { } current ?
            ConfigurePreviewDeviceAsync(device, () => device.FreezeFixedPreviewConfigurationAsync(token), current, true) :
            ValueTask.FromResult(CameraPreviewConfigurationResult.Failure("PreviewConfigurationUnavailable")), cancellationToken,
            phaseFactory: phaseFactory);

    internal ValueTask<PreviewDeviceCallResult<CameraPreviewFrameResult>> ReadPreviewFrameAsync(
        long afterSequence, CancellationToken cancellationToken,
        Func<RecipeActivationPhysicalPhaseClaim>? phaseFactory = null) =>
        InvokePreviewAsync((device, token) => device.ReadLatestPreviewFrameAsync(afterSequence, token), cancellationToken,
            phaseFactory: phaseFactory);

    internal ValueTask<PreviewDeviceCallResult<CameraOperationResult>> StopPreviewAsync(
        CancellationToken cancellationToken) =>
        InvokePreviewAsync(async (device, token) =>
        {
            var stopped = await device.StopPreviewAsync(token).ConfigureAwait(false);
            if (stopped.Succeeded && ((ICameraDevice)device).GetHealthSnapshot().Acquisition != CameraAcquisitionState.Stopped)
                return CameraOperationResult.Failure("PreviewStopReadBackMismatch");
            return stopped;
        }, cancellationToken);

    private async ValueTask<CameraPreviewConfigurationResult> ConfigurePreviewDeviceAsync(
        ICameraPreviewDevice preview, Func<ValueTask<CameraPreviewConfigurationResult>> operation,
        PreviewTuningConfiguration requested, bool requireFixed)
    {
        var capabilities = preview.PreviewCapabilities;
        if (!capabilities.Supported) return CameraPreviewConfigurationResult.Failure("PreviewCapabilityUnavailable");
        if (!requireFixed &&
            (requested.ExposureMode != PreviewAutomaticControlMode.Off && !capabilities.AutomaticExposure ||
             requested.GainMode != PreviewAutomaticControlMode.Off && !capabilities.AutomaticGain ||
             requested.WhiteBalanceMode != PreviewAutomaticControlMode.Off && !capabilities.AutomaticWhiteBalance))
            return CameraPreviewConfigurationResult.Failure("PreviewAutomaticControlUnsupported");
        var original = _candidateSnapshot!.Requested!;
        var common = ((ICameraDevice)preview).Capabilities;
        if (!common.ValidateConfiguration(PreviewDraftSettings.Merge(original, requested.ProcessSettings)).Succeeded)
            return CameraPreviewConfigurationResult.Failure("PreviewRequestedConfigurationUnsupported");
        var result = await operation().ConfigureAwait(false);
        if (result is not { Succeeded: true, Configuration: { } actual })
            return result ?? CameraPreviewConfigurationResult.Failure("PreviewProviderResultMissing");
        var error = PreviewDraftSettings.ValidateReadBack(common, original, requested, actual, requireFixed);
        if (error is not null) return CameraPreviewConfigurationResult.Failure(error);
        var health = ((ICameraDevice)preview).GetHealthSnapshot();
        if (health.ProviderAvailability != CameraProviderAvailability.Available ||
            health.Connection != CameraConnectionState.Open || health.Configuration != CameraConfigurationState.Applied ||
            health.Acquisition != CameraAcquisitionState.Previewing)
            return CameraPreviewConfigurationResult.Failure("PreviewAcquisitionReadBackMismatch");
        _latestPreviewConfiguration = actual;
        return result;
    }

    private async ValueTask<PreviewDeviceCallResult<T>> InvokePreviewAsync<T>(
        Func<ICameraPreviewDevice, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken, bool starting = false,
        Func<RecipeActivationPhysicalPhaseClaim>? phaseFactory = null) where T : class
    {
        try
        {
            if (!await _leaseGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return new(false, "PreviewDeviceOperationInProgress", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "PreviewOperationCancelled", null); }
        try
        {
            ICameraDevice? device;
            lock (this)
            {
                if (_disposed || _committed || _qualificationOwned || _productionOwned || _safeClosed || _restoreSucceeded || !_candidatePrepared ||
                    _candidateDevice is null || _candidateSnapshot is null)
                    return new(false, "PreviewDeviceOwnerUnavailable", null);
                if (starting ? _previewOwned : !_previewOwned)
                    return new(false, "PreviewDeviceSessionMismatch", null);
                if (starting) _previewOwned = true;
                device = _candidateDevice;
            }
            var result = await _owner.InvokePreviewDeviceAsync(device, operation, cancellationToken, phaseFactory)
                .ConfigureAwait(false);
            if (result.DeviceTransferred)
            {
                lock (this)
                {
                    // The physical registry now owns the actual unfinished call
                    // and its final disposal. Restore must not race that owner.
                    _candidateDevice = null;
                    _candidatePrepared = false;
                    ReasonCode = result.ReasonCode;
                }
                _owner.MarkActivationRestorationBlocked(_logicalRole);
            }
            return result;
        }
        finally { _leaseGate.Release(); }
    }
}
