using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

internal sealed record CameraDeviceRetirement(bool SafeToReplace, string ReasonCode);

public sealed partial class CameraAcquisitionService
{
    private static readonly TimeSpan LeaseReturnObservationInterval =
        TimeSpan.FromMilliseconds(10);

    private readonly HashSet<TrackedLease> _outstandingLeases = new();
    private TaskCompletionSource<bool>? _leaseDrainCompletion;
    private Task<CameraHealthSnapshot?>? _healthReadTask;
    private Task<CameraDeviceRetirement>? _retirementTask;
    private bool _leaseReleaseFaulted;

    internal CameraDeviceDescriptor Descriptor => _descriptor;
    internal CameraCapabilities Capabilities => _capabilities;
    internal CameraAcquisitionOptions Options => _options;
    internal IFrameAcquisitionClock Clock => _clock;

    /// <summary>
    /// Closes admission and waits for actual acquisition, lease, stop and device disposal completion.
    /// Unlike bounded DisposeAsync, this task remains pending while a physical owner remains active.
    /// A false result prohibits replacement and must not be treated as successful cleanup.
    /// </summary>
    public async Task<CameraRetirementObservation> RetireAsync()
    {
        var result = await BeginRetirement().ConfigureAwait(false);
        return new CameraRetirementObservation(result.SafeToReplace, result.ReasonCode);
    }

    /// <summary>
    /// Closes admission immediately and owns one physical stop/dispose chain. The
    /// returned task is deliberately unbounded: a caller-facing DisposeAsync may
    /// wait for a configured bound, while the service retains the physical owner.
    /// </summary>
    internal Task<CameraDeviceRetirement> BeginRetirement()
    {
        Attempt? attempt;
        Task<CameraDeviceRetirement> retirement;
        lock (_sync)
        {
            if (_retirementTask is not null)
                return _retirementTask;

            _disposed = true;
            attempt = _attempt;
            retirement = Task.Run(() => RetireCoreAsync(attempt));
            _retirementTask = retirement;
        }

        // RequestCancellation takes the service gate itself, so it must be called
        // after the latch is published and the lock is released. Acquire/health
        // admission observes _disposed immediately in the meantime.
        attempt?.RequestCancellation();
        return retirement;
    }

    /// <summary>
    /// Performs one real, off-gate health call. A timeout or invalid provider result
    /// returns null while the physical task remains the sole retained probe until it
    /// actually completes; a late completion is never pushed into service state.
    /// </summary>
    internal async Task<CameraHealthSnapshot?> ReadHealthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<CameraHealthSnapshot?> healthTask;
        TaskCompletionSource<bool>? startGate = null;
        lock (_sync)
        {
            // A caller-facing timeout never adopts the late result as the next
            // probe's fact. The physical task was retained until completion; once
            // complete, retire that task before admitting a fresh probe.
            if (_healthReadTask is { IsCompleted: true })
                _healthReadTask = null;

            if (_disposed || _attempt is not null || _admissionInProgress ||
                _protocolRefreshQueued || HasPendingProtocolReadLocked())
                return null;

            if (_healthReadTask is null)
            {
                startGate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _healthReadTask = StartHealthReadLocked(startGate);
            }

            healthTask = _healthReadTask!;
        }

        // The physical getter is released only after the admission gate is open,
        // so a very fast ThreadPool worker cannot enter the SDK while _sync is held.
        startGate?.TrySetResult(true);

        try
        {
            return await healthTask.WaitAsync(_options.ProtocolReadTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_healthReadTask, healthTask) && healthTask.IsCompleted)
                    _healthReadTask = null;
            }
        }
    }

    private async Task<CameraDeviceRetirement> RetireCoreAsync(Attempt? attempt)
    {
        if (attempt is not null)
        {
            attempt.RequestCancellation();
            try { await attempt.PhysicalCompletion.Task.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }

        // Admission, protocol reads and health probes are all physical calls. They
        // must quiesce before Stop, even when their caller already timed out.
        await WaitForAdmissionAndProtocolReadAsync().ConfigureAwait(false);

        var leasesReleased = await WaitForOutstandingLeasesAsync().ConfigureAwait(false);
        if (!leasesReleased)
            return new CameraDeviceRetirement(false, "CameraLeaseReleaseFailed");

        CameraOperationResult? stopResult = null;
        Exception? stopFailure = null;
        try
        {
            stopResult = await InvokeOperationAsync(() => _device.StopAsync())
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stopFailure = exception;
        }

        Exception? disposeFailure = null;
        try
        {
            await InvokeDisposeAsync(_device).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            disposeFailure = exception;
        }

        if (stopFailure is not null)
            return new CameraDeviceRetirement(false, "CameraStopFailed");
        if (stopResult is null || !stopResult.Succeeded)
            return new CameraDeviceRetirement(false,
                stopResult?.ReasonCode ?? "CameraStopFailed");
        if (disposeFailure is not null)
            return new CameraDeviceRetirement(false, "CameraDeviceDisposeFailed");

        return new CameraDeviceRetirement(true, "CameraDeviceRetired");
    }

    private Task<CameraHealthSnapshot?> StartHealthReadLocked(
        TaskCompletionSource<bool> startGate)
    {
        return Task.Factory.StartNew<CameraHealthSnapshot?>(static state =>
        {
            var context = (HealthReadContext)state!;
            context.StartGate.Task.GetAwaiter().GetResult();
            var service = context.Service;
            try { return service._device.GetHealthSnapshot(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return null; }
        }, new HealthReadContext(this, startGate), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private bool HasPendingHealthReadLocked()
    {
        if (_healthReadTask is { IsCompleted: true })
            _healthReadTask = null;
        return _healthReadTask is not null;
    }

    private bool HasPendingProtocolReadLocked()
        // Do not clear a completed read here: its Refresh caller may still be
        // waiting to merge the returned facts. The Refresh/retirement owner is
        // responsible for clearing it after observing completion.
        => _protocolReadTask is not null;

    private async Task<bool> WaitForOutstandingLeasesAsync()
    {
        while (true)
        {
            Task<bool> drain;
            lock (_sync)
            {
                if (_leaseReleaseFaulted)
                    return false;
                if (_outstandingLeases.Count == 0)
                    return true;
                drain = (_leaseDrainCompletion ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            try
            {
                if (!await drain.ConfigureAwait(false))
                    return false;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return false;
            }
        }
    }

    private void CompleteLeaseRelease(TrackedLease lease, bool returned)
    {
        lock (_sync)
        {
            if (returned)
                _outstandingLeases.Remove(lease);
            else
                _leaseReleaseFaulted = true;

            if (_leaseReleaseFaulted)
                _leaseDrainCompletion?.TrySetResult(false);
            else if (_outstandingLeases.Count == 0)
                _leaseDrainCompletion?.TrySetResult(true);
            lease.CompleteReturn(returned);
        }
    }

    private void TrackLeaseLocked(TrackedLease lease)
    {
        if (!_outstandingLeases.Add(lease))
            return;

        if (_leaseDrainCompletion is null || _leaseDrainCompletion.Task.IsCompleted)
            _leaseDrainCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HealthReadContext
    {
        internal HealthReadContext(CameraAcquisitionService service,
            TaskCompletionSource<bool> startGate)
        {
            Service = service;
            StartGate = startGate;
        }

        internal CameraAcquisitionService Service { get; }
        internal TaskCompletionSource<bool> StartGate { get; }
    }

    private sealed class TrackedLease : IFrameBufferLease
    {
        private readonly CameraAcquisitionService _service;
        private readonly IFrameBufferLease _inner;
        private readonly TaskCompletionSource<bool> _returnCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeStarted;

        internal TrackedLease(CameraAcquisitionService service, IFrameBufferLease inner)
        {
            _service = service;
            _inner = inner;
        }

        public Guid LeaseId => _inner.LeaseId;
        public VisionFrame Frame => _inner.Frame;
        public FrameProvenance Provenance => _inner.Provenance;
        public bool IsReturned => _inner.IsReturned;
        internal Task<bool> ReturnCompletion => _returnCompletion.Task;
        internal void CompleteReturn(bool returned) => _returnCompletion.TrySetResult(returned);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            Task disposal;
            try
            {
                disposal = Task.Factory.StartNew(static state =>
                {
                    var lease = (TrackedLease)state!;
                    lease.DisposeInner();
                }, this, CancellationToken.None, TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _service.CompleteLeaseRelease(this, returned: false);
                throw;
            }

            // Preserve the synchronous lease contract through the underlying Dispose,
            // while the returned-state observation remains a separate service barrier.
            disposal.GetAwaiter().GetResult();
        }

        private void DisposeInner()
        {
            try
            {
                _inner.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _service.CompleteLeaseRelease(this, returned: false);
                throw;
            }

            Task observer;
            try
            {
                observer = Task.Factory.StartNew(static state =>
                    ((TrackedLease)state!).ObserveReturnedAsync(), this,
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _service.CompleteLeaseRelease(this, returned: false);
                throw;
            }

            _ = observer;
        }

        private async Task ObserveReturnedAsync()
        {
            while (true)
            {
                bool returned;
                try { returned = _inner.IsReturned; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _service.CompleteLeaseRelease(this, returned: false);
                    return;
                }

                if (returned)
                {
                    _service.CompleteLeaseRelease(this, returned: true);
                    return;
                }

                await Task.Delay(LeaseReturnObservationInterval).ConfigureAwait(false);
            }
        }
    }
}
