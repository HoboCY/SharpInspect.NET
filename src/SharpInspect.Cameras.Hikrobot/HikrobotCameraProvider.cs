using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>
/// Managed Hikrobot provider boundary.  The public construction path is
/// deliberately dependency-only: V1 has an empty production MVS compatibility
/// catalog, so a local installation remains diagnostic evidence and cannot make
/// this provider production-compatible.
/// </summary>
public sealed class HikrobotCameraProvider : ICameraProvider
{
    public const string ProviderId = "SharpInspect.Hikrobot";
    public const string ProviderVersion = "1";
    public const string AdapterPackageId = "SharpInspect.NET.Cameras.Hikrobot";
    public const string AdapterPackageVersion = "0.1.0-dev.1";

    private const string ProductionCatalogEmptyReason =
        "HikrobotCompatibilityCatalogEmpty";
    private const string ProviderDisposedReason = "HikrobotProviderDisposed";
    private const string DeviceMissingReason = "HikrobotDeviceMissing";
    private const string DeviceAlreadyOpenReason = "HikrobotDeviceAlreadyOpen";
    private const string OpenInProgressReason = "HikrobotOpenInProgress";
    private const string DiscoveryCapacityReason = "HikrobotDiscoveryCapacityExceeded";
    private const string DiscoveryDuplicateReason = "HikrobotDiscoveryDuplicateIdentity";
    private const string DiscoveryDescriptorReason = "HikrobotDiscoveryDescriptorInvalid";
    private const string DiscoveryFailedReason = "HikrobotDiscoveryFailed";
    private const string OpenFailedReason = "HikrobotOpenFailed";
    private const string OpenIdentityMismatchReason = "HikrobotOpenedIdentityMismatch";
    private const string OpenAbandonedReason = "HikrobotOpenAbandoned";

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _sdkGate = new(1, 1);
    private readonly HashSet<Task> _sdkOperations = new();
    private readonly IFrameAcquisitionClock _clock;
    private readonly FrameBufferPoolOptions _poolOptions;
    private readonly IHikrobotSdkRuntime? _sdk;
    private readonly CameraProviderIdentity _identity = new(
        ProviderId, ProviderVersion, AdapterPackageId, AdapterPackageVersion);
    private readonly HikrobotDependencyReport _dependencyReport;

    private ICameraDevice? _openedDevice;
    // A raw SDK handle can exist before the managed device wrapper is
    // constructed (for example when descriptor validation fails). Keep that
    // exact handle as the provider owner until Dispose actually returns so a
    // failed cleanup cannot release the one-device slot.
    private IHikrobotSdkDevice? _rawCleanupOwner;
    private Task<CameraOpenResult>? _openOperation;
    private OpenAttempt? _openAttempt;
    private Task<CameraDiscoveryResult>? _discoveryOperation;
    private Task? _disposeOperation;
    private bool _opening;
    private bool _disposed;
    private bool _disposeCompleted;
    private bool _runtimeDisposed;

    /// <summary>
    /// Creates the production-facing provider.  No SDK object, mode, runtime
    /// path, qualified-version list, or override can be supplied here.
    /// </summary>
    public HikrobotCameraProvider(IFrameAcquisitionClock clock,
        FrameBufferPoolOptions poolOptions)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _poolOptions = poolOptions ?? throw new ArgumentNullException(nameof(poolOptions));
        _dependencyReport = HikrobotDependencies.Detect();
    }

    /// <summary>
    /// Test and unpackaged-device-probe seam.  It never changes the production
    /// compatibility catalog used by the public constructor.
    /// </summary>
    internal HikrobotCameraProvider(IHikrobotSdkRuntime sdk,
        IFrameAcquisitionClock clock, FrameBufferPoolOptions poolOptions)
        : this(clock, poolOptions)
    {
        _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
    }

    public CameraProviderIdentity Identity => _identity;
    public HikrobotDependencyReport DependencyReport => _dependencyReport;

    public ValueTask<CameraDiscoveryResult> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraDiscoveryResult>(cancellationToken);

        if (_sdk is null)
            return ValueTask.FromResult(CameraDiscoveryResult.Failure(
                IsDisposed ? ProviderDisposedReason : ProductionCatalogEmptyReason));

        Task<CameraDiscoveryResult> operation;
        lock (_stateGate)
        {
            if (_disposed)
                return ValueTask.FromResult(CameraDiscoveryResult.Failure(ProviderDisposedReason));

            // The actual SDK operation is deliberately uncancellable and shared
            // by every caller while it is in flight. Each caller only cancels its
            // own wait, so repeated probes cannot queue unbounded SDK calls.
            var current = _discoveryOperation;
            if (current is null || current.IsCompleted)
            {
                current = DiscoverCoreAsync();
                _discoveryOperation = current;
            }
            operation = current;
        }

        return new ValueTask<CameraDiscoveryResult>(WaitWithCancellationAsync(
            operation, cancellationToken));
    }

    public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<CameraOpenResult>(cancellationToken);
        ArgumentNullException.ThrowIfNull(stableDeviceIdentity);

        if (_sdk is null)
            return ValueTask.FromResult(CameraOpenResult.Failure(
                IsDisposed ? ProviderDisposedReason : ProductionCatalogEmptyReason));

        Task<CameraOpenResult> operation;
        OpenAttempt attempt;
        lock (_stateGate)
        {
            if (_disposed)
                return ValueTask.FromResult(CameraOpenResult.Failure(ProviderDisposedReason));

            if (!IsOpenSlotAvailableLocked())
                return ValueTask.FromResult(CameraOpenResult.Failure(DeviceAlreadyOpenReason));
            if (_opening)
                return ValueTask.FromResult(CameraOpenResult.Failure(OpenInProgressReason));

            _opening = true;
            attempt = new OpenAttempt(stableDeviceIdentity);
            _openAttempt = attempt;
            operation = OpenCoreAsync(attempt);
            _openOperation = operation;
        }

        return new ValueTask<CameraOpenResult>(WaitOpenWithCancellationAsync(
            attempt, operation, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        Task dispose;
        lock (_stateGate)
        {
            if (_disposeCompleted)
                return ValueTask.CompletedTask;
            if (_disposeOperation is not null)
                return new ValueTask(_disposeOperation);

            _disposed = true;

            var completion = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeOperation = completion.Task;
            dispose = completion.Task;
            _ = DisposeCoreAsync(completion);
        }

        return new ValueTask(dispose);
    }

    private bool IsDisposed
    {
        get { lock (_stateGate) return _disposed; }
    }

    private async Task<CameraDiscoveryResult> DiscoverCoreAsync()
    {
        IReadOnlyList<HikrobotSdkDescriptor>? discovered;
        try
        {
            discovered = await AwaitSdkAsync(() => _sdk!.Discover(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAdapterException(exception))
        {
            return CameraDiscoveryResult.Failure(SdkReason(exception, DiscoveryFailedReason));
        }

        if (discovered is null)
            return CameraDiscoveryResult.Failure(DiscoveryFailedReason);
        if (discovered.Count > 64)
            return CameraDiscoveryResult.Failure(DiscoveryCapacityReason);

        var descriptors = new List<CameraDeviceDescriptor>(discovered.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in discovered)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.StableIdentity) ||
                !identities.Add(item.StableIdentity))
                return CameraDiscoveryResult.Failure(item is null ||
                    string.IsNullOrWhiteSpace(item.StableIdentity)
                    ? DiscoveryDescriptorReason : DiscoveryDuplicateReason);

            try
            {
                var model = string.IsNullOrWhiteSpace(item.Model) ? null : item.Model;
                var displayName = string.IsNullOrWhiteSpace(model)
                    ? "Hikrobot Camera " + item.StableIdentity
                    : model!;
                descriptors.Add(new CameraDeviceDescriptor(Identity,
                    item.StableIdentity, displayName, model));
            }
            catch (ArgumentException)
            {
                return CameraDiscoveryResult.Failure(DiscoveryDescriptorReason);
            }
        }

        lock (_stateGate)
        {
            if (_disposed) return CameraDiscoveryResult.Failure(ProviderDisposedReason);
        }
        return CameraDiscoveryResult.Success(descriptors);
    }

    private async Task<CameraOpenResult> OpenCoreAsync(OpenAttempt attempt)
    {
        IHikrobotSdkDevice? rawDevice = null;
        try
        {
            var opened = await AwaitSdkAsync(() => OpenSdkDevice(attempt.StableDeviceIdentity),
                CancellationToken.None).ConfigureAwait(false);
            rawDevice = opened.Device;

            if (!string.Equals(opened.Descriptor.StableIdentity,
                    attempt.StableDeviceIdentity,
                    StringComparison.Ordinal))
            {
                DisposeRawDevice(rawDevice);
                rawDevice = null;
                return CameraOpenResult.Failure(OpenIdentityMismatchReason);
            }

            var serialized = new SerializedHikrobotSdkDevice(this, rawDevice,
                opened.Descriptor, opened.Capabilities);
            HikrobotCameraDevice device;
            try
            {
                device = new HikrobotCameraDevice(serialized, Identity,
                    opened.RuntimeVersion, _clock, _poolOptions);
            }
            catch (Exception exception)
            {
                try
                {
                    serialized.Dispose();
                    rawDevice = null;
                }
                catch
                {
                    // The managed wrapper could not be constructed and its
                    // serialized cleanup also failed. Keep the raw handle for
                    // provider-level retry rather than losing it on an
                    // exceptional constructor path.
                    RememberRawCleanupOwner(rawDevice!);
                    throw;
                }
                if (!IsAdapterException(exception)) throw;
                return CameraOpenResult.Failure(SdkReason(exception, OpenFailedReason));
            }

            lock (_stateGate)
            {
                if (_disposed || attempt.Abandoned ||
                    !ReferenceEquals(_openAttempt, attempt) || _openedDevice is not null)
                {
                    // Retire an abandoned or stale generation outside the state
                    // lock. Its device is never published as another generation's
                    // owner.
                }
                else
                {
                    _openedDevice = device;
                    attempt.Published = true;
                    attempt.Device = device;
                    rawDevice = null;
                    return CameraOpenResult.Success(device);
                }
            }

            // Keep the late device as the provider's exact owner while its
            // retirement is in progress. This lets a concurrent provider
            // disposal take the same owner and retry a failed native Dispose;
            // a later generation can only start after RetirementCompleted.
            lock (_stateGate)
            {
                if (_openedDevice is null && ReferenceEquals(_openAttempt, attempt))
                    _openedDevice = device;
            }
            rawDevice = null;
            await device.DisposeAsync().ConfigureAwait(false);
            lock (_stateGate)
            {
                if (ReferenceEquals(_openedDevice, device) && device.RetirementCompleted)
                    _openedDevice = null;
            }
            var reason = _disposed
                ? ProviderDisposedReason
                : attempt.Abandoned ? OpenAbandonedReason : DeviceAlreadyOpenReason;
            return CameraOpenResult.Failure(reason);
        }
        catch (Exception exception) when (IsAdapterException(exception))
        {
            if (rawDevice is null)
            {
                // OpenSdkDevice may have observed a raw handle but failed while
                // reading its descriptor/capabilities before returning it.
                // Recover that exact owner and make one serialized cleanup
                // attempt; a failed attempt remains provider-owned for retry.
                lock (_stateGate) rawDevice = _rawCleanupOwner;
            }
            if (rawDevice is not null) DisposeRawDevice(rawDevice);
            return CameraOpenResult.Failure(SdkReason(exception, OpenFailedReason));
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_openAttempt, attempt))
                {
                    // A failed late cleanup remains an in-flight generation
                    // until its exact owner retires. A published device has
                    // already completed Open, so it does not keep this flag.
                    _opening = !attempt.Published &&
                        ((_openedDevice is HikrobotCameraDevice device &&
                            !device.RetirementCompleted) ||
                         _rawCleanupOwner is not null);
                }
                // Keep the completed task while DisposeAsync may be taking its
                // snapshot; a later open replaces it only after this operation.
            }
        }
    }

    private OpenedSdkDevice OpenSdkDevice(string stableDeviceIdentity)
    {
        var device = _sdk!.Open(stableDeviceIdentity);
        if (device is null) throw new HikrobotSdkException(OpenFailedReason);
        try
        {
            return new OpenedSdkDevice(device, device.Descriptor, device.Capabilities,
                _sdk.RuntimeVersion);
        }
        catch
        {
            // The caller's rawDevice local is not assigned until this method
            // returns. Preserve the handle here so metadata failures cannot
            // lose ownership before OpenCore gets a chance to report them.
            RememberRawCleanupOwner(device);
            throw;
        }
    }

    private bool IsOpenSlotAvailableLocked()
    {
        // A failed raw-handle cleanup has no managed device health snapshot,
        // but it still owns the physical slot. Only a confirmed Dispose can
        // clear this owner.
        if (_rawCleanupOwner is not null) return false;
        if (_openedDevice is null) return true;

        // Disconnected health during retirement does not prove that native
        // Stop/Dispose has returned. Keep the one-device slot reserved until
        // the concrete owner publishes physical retirement completion.
        if (_openedDevice is HikrobotCameraDevice device && device.RetirementCompleted)
        {
            _openedDevice = null;
            return true;
        }
        return false;
    }

    private async Task<CameraOpenResult> WaitOpenWithCancellationAsync(
        OpenAttempt attempt, Task<CameraOpenResult> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // WaitAsync cancellation means this caller did not receive the
            // ownership token. The operation itself remains uncancellable, so
            // mark this exact generation and let OpenCore retire any late device.
            MarkOpenAbandoned(attempt);
            throw;
        }
    }

    private void MarkOpenAbandoned(OpenAttempt attempt)
    {
        HikrobotCameraDevice? device = null;
        lock (_stateGate)
        {
            if (attempt.Abandoned) return;
            attempt.Abandoned = true;
            if (ReferenceEquals(_openAttempt, attempt) && attempt.Published &&
                attempt.Device is not null &&
                ReferenceEquals(_openedDevice, attempt.Device))
                device = attempt.Device;
        }

        if (device is not null)
            _ = RetireAbandonedDeviceAsync(device);
    }

    private static async Task RetireAbandonedDeviceAsync(HikrobotCameraDevice device)
    {
        try { await device.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (IsAdapterException(exception)) { }
    }

    private Task<T> StartSdkOperation<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_stateGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HikrobotCameraProvider));
            var task = Task.Run(() => InvokeSdk(operation));
            _sdkOperations.Add(task);
            _ = task.ContinueWith(completed =>
            {
                _ = completed.Exception;
                lock (_stateGate) _sdkOperations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task<T> AwaitSdkAsync<T>(Func<T> operation,
        CancellationToken cancellationToken)
    {
        var task = StartSdkOperation(operation);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private T InvokeSdk<T>(Func<T> operation)
    {
        _sdkGate.Wait();
        try { return operation(); }
        finally { _sdkGate.Release(); }
    }

    private void InvokeSdk(Action operation)
    {
        _sdkGate.Wait();
        try { operation(); }
        finally { _sdkGate.Release(); }
    }

    private bool DisposeRawDevice(IHikrobotSdkDevice device)
    {
        try
        {
            InvokeSdk(device.Dispose);
            ForgetRawCleanupOwner(device);
            return true;
        }
        catch (Exception exception)
        {
            RememberRawCleanupOwner(device);
            if (!IsAdapterException(exception)) throw;
            return false;
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<object?> completion)
    {
        var completed = false;
        Exception? failure = null;
        try
        {
            Task<CameraOpenResult>? openOperation;
            lock (_stateGate) openOperation = _openOperation;
            if (openOperation is not null)
                await ObserveAsync(openOperation).ConfigureAwait(false);

            ICameraDevice? openedDevice;
            IHikrobotSdkDevice? rawOwner;
            lock (_stateGate)
            {
                // Do not clear either owner before physical retirement. A
                // failed Stop/Dispose must remain retryable by a later
                // DisposeAsync call on this same provider.
                openedDevice = _openedDevice;
                rawOwner = _rawCleanupOwner;
            }
            if (openedDevice is not null)
            {
                try { await openedDevice.DisposeAsync().ConfigureAwait(false); }
                catch (Exception)
                {
                    throw new HikrobotSdkException(
                        "HikrobotDeviceRetirementFailed");
                }

                if (openedDevice is not HikrobotCameraDevice device ||
                    !device.RetirementCompleted)
                    throw new HikrobotSdkException(
                        "HikrobotDeviceRetirementIncomplete");

                lock (_stateGate)
                {
                    if (ReferenceEquals(_openedDevice, openedDevice))
                        _openedDevice = null;
                }
            }

            if (rawOwner is not null && !DisposeRawDevice(rawOwner))
                throw new HikrobotSdkException("HikrobotRawDeviceRetirementIncomplete");

            await DrainSdkOperationsAsync().ConfigureAwait(false);
            if (_sdk is not null)
            {
                // All tracked operations and the opened device have retired.
                // The final runtime Dispose remains in the same serialized SDK
                // domain, even though the provider is already marked disposed.
                var shouldDisposeRuntime = false;
                lock (_stateGate) shouldDisposeRuntime = !_runtimeDisposed;
                if (shouldDisposeRuntime)
                {
                    await Task.Run(() => InvokeSdk(_sdk.Dispose)).ConfigureAwait(false);
                    lock (_stateGate) _runtimeDisposed = true;
                }
            }

            lock (_stateGate)
            {
                if (_openedDevice is not null || _rawCleanupOwner is not null)
                    throw new HikrobotSdkException(
                        "HikrobotDeviceRetirementIncomplete");
                _opening = false;
                _disposeCompleted = true;
            }
            completed = true;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // A successful provider dispose is published only after its SDK
            // semaphore has really been sealed. For a failed attempt, clear
            // the in-flight marker before publishing the fault so an awaiter
            // can immediately request the next physical cleanup retry.
            if (completed) _sdkGate.Dispose();
            lock (_stateGate)
            {
                if (!completed && ReferenceEquals(_disposeOperation, completion.Task))
                    _disposeOperation = null;
            }
            if (completed)
                completion.TrySetResult(null);
            else
                completion.TrySetException(failure ?? new HikrobotSdkException(
                    "HikrobotProviderDisposeFailed"));
        }
    }

    private void RememberRawCleanupOwner(IHikrobotSdkDevice device)
    {
        lock (_stateGate)
        {
            if (_rawCleanupOwner is null)
                _rawCleanupOwner = device;
        }
    }

    private void ForgetRawCleanupOwner(IHikrobotSdkDevice device)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_rawCleanupOwner, device))
                _rawCleanupOwner = null;
        }
    }

    private async Task DrainSdkOperationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_stateGate) pending = _sdkOperations.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception exception) when (IsAdapterException(exception))
            {
                foreach (var task in pending) _ = task.Exception;
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (IsAdapterException(exception)) { }
    }

    private static async Task<T> WaitWithCancellationAsync<T>(Task<T> operation,
        CancellationToken cancellationToken) =>
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);

    private static string SdkReason(Exception exception, string fallback)
    {
        if (exception is HikrobotSdkException sdk && IsStableReason(sdk.ReasonCode))
            return sdk.ReasonCode;
        return fallback;
    }

    private static bool IsStableReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            >= '0' and <= '9' or '.' or '_' or '-' or ':');

    private static bool IsAdapterException(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException;

    private sealed class OpenAttempt
    {
        internal OpenAttempt(string stableDeviceIdentity) =>
            StableDeviceIdentity = stableDeviceIdentity;

        internal string StableDeviceIdentity { get; }
        internal bool Abandoned { get; set; }
        internal bool Published { get; set; }
        internal HikrobotCameraDevice? Device { get; set; }
    }

    private readonly record struct OpenedSdkDevice(IHikrobotSdkDevice Device,
        HikrobotSdkDescriptor Descriptor, CameraCapabilities Capabilities,
        string RuntimeVersion);

    /// <summary>
    /// Serializes every actual SDK device call through the provider's one SDK
    /// lifecycle gate while retaining only immutable descriptor/capability data
    /// outside the SDK call.
    /// </summary>
    private sealed class SerializedHikrobotSdkDevice : IHikrobotSdkDevice
    {
        private readonly HikrobotCameraProvider _owner;
        private readonly IHikrobotSdkDevice _inner;
        private readonly HikrobotSdkDescriptor _descriptor;
        private readonly CameraCapabilities _capabilities;
        private readonly object _disposeGate = new();
        private bool _disposed;

        internal SerializedHikrobotSdkDevice(HikrobotCameraProvider owner,
            IHikrobotSdkDevice inner, HikrobotSdkDescriptor descriptor,
            CameraCapabilities capabilities)
        {
            _owner = owner;
            _inner = inner;
            _descriptor = descriptor;
            _capabilities = capabilities;
        }

        public HikrobotSdkDescriptor Descriptor => _descriptor;
        public CameraCapabilities Capabilities => _capabilities;

        public EffectiveCameraConfiguration Apply(RequestedCameraConfiguration requested) =>
            _owner.InvokeSdk(() => _inner.Apply(requested));

        public void Start(Action<HikrobotNativeFrame> callback) =>
            _owner.InvokeSdk(() => _inner.Start(callback));

        public void TriggerSoftware() => _owner.InvokeSdk(_inner.TriggerSoftware);

        public void Stop() => _owner.InvokeSdk(_inner.Stop);

        public void Dispose()
        {
            lock (_disposeGate)
            {
                if (_disposed) return;

                // Do not seal the wrapper until the real SDK Dispose returns.
                // A failed native Dispose leaves the owner retryable, while
                // this gate serializes competing cleanup callers.
                _owner.InvokeSdk(_inner.Dispose);
                _disposed = true;
            }
        }
    }
}
