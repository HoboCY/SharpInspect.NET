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

    internal bool HasOutstandingLeases
    {
        get { lock (_sync) return _outstandingLeases.Count != 0; }
    }

    internal bool HasPendingActualWork
    {
        get
        {
            lock (_sync)
                return _attempt is not null || _admissionInProgress ||
                    _protocolReadTask is not null || _healthReadTask is not null ||
                    _protocolRefreshQueued || _retirementTask is { IsCompleted: false };
        }
    }

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

        // RequestCancellation 本身会取得服务锁，所以必须在发布锁存并释放锁后调用；期间采集/健康检查准入会立即看到 _disposed。
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
            // 面向调用者的超时不能把迟到结果当作下一次探针事实；物理任务完成前持续保留，完成后才回收，再准入新探针。
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

        // 只有准入锁打开后才放行物理 getter，避免快速 ThreadPool worker 在持有 _sync 时进入 SDK。
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

        // 准入、协议读取和健康探针都是物理调用；即使调用者已超时，也必须在 Stop 前静止。
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
        // 这里不能清除已完成读取：Refresh 调用者可能仍在等待合并事实；由 Refresh/退役所有者在观察完成后清除。
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

            // 通过底层 Dispose 保持同步 lease 契约，同时把“已归还”观测作为独立的服务屏障。
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
