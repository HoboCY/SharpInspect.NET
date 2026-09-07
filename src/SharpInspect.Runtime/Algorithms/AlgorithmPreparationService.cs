using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Algorithms;

/// <summary>Resource bounds for preparation; these are separate from a Recipe's execution deadline.</summary>
public sealed class AlgorithmPreparationOptions
{
    public AlgorithmPreparationOptions(TimeSpan maximumPreparationTimeout, int maximumConcurrentPreparations = 4,
        int maximumOwnedInstances = 16, TimeSpan? shutdownWaitTimeout = null)
    {
        if (maximumPreparationTimeout <= TimeSpan.Zero || maximumPreparationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumPreparationTimeout));
        if (maximumConcurrentPreparations is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentPreparations));
        if (maximumOwnedInstances < maximumConcurrentPreparations || maximumOwnedInstances > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumOwnedInstances));
        var shutdown = shutdownWaitTimeout ?? TimeSpan.FromSeconds(2);
        if (shutdown <= TimeSpan.Zero || shutdown > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(shutdownWaitTimeout));
        MaximumPreparationTimeout = maximumPreparationTimeout;
        MaximumConcurrentPreparations = maximumConcurrentPreparations;
        MaximumOwnedInstances = maximumOwnedInstances;
        ShutdownWaitTimeout = shutdown;
    }
    public TimeSpan MaximumPreparationTimeout { get; }
    public int MaximumConcurrentPreparations { get; }
    public int MaximumOwnedInstances { get; }
    public TimeSpan ShutdownWaitTimeout { get; }
}

public sealed record AlgorithmPreparationRequest(AlgorithmIdentity Algorithm,
    AlgorithmConfigurationSnapshot Configuration, string ResultSchemaId, string ResultSchemaVersion,
    string ResultSchemaContentHash, string OverlayContractId, string OverlayContractVersion,
    string OverlayContractContentHash, TimeSpan PreparationTimeout);

public sealed record AlgorithmPreparationResult(bool Succeeded, string ReasonCode,
    PreparedAlgorithm? Prepared, IReadOnlyList<AlgorithmValidationIssue> Issues);

/// <summary>A fully prepared, owned instance. Raw algorithms and station execution capabilities are not public.</summary>
public sealed class PreparedAlgorithm : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IVisionAlgorithm _algorithm;
    private readonly Func<IVisionAlgorithm, PreparedAlgorithm, Task> _retire;
    private Task? _disposal;
    internal PreparedAlgorithm(AlgorithmDescriptor descriptor, AlgorithmConfigurationSnapshot configuration,
        IVisionAlgorithm algorithm, Func<IVisionAlgorithm, PreparedAlgorithm, Task> retire)
    {
        InstanceId = Guid.NewGuid(); Descriptor = descriptor; Configuration = configuration;
        _algorithm = algorithm; _retire = retire;
    }
    public Guid InstanceId { get; }
    public AlgorithmDescriptor Descriptor { get; }
    public AlgorithmConfigurationSnapshot Configuration { get; }
    public bool IsRetired { get { lock (_sync) return _disposal is not null; } }
    internal IVisionAlgorithm Algorithm
    {
        get { lock (_sync) return _disposal is null ? _algorithm : throw new InvalidOperationException("AlgorithmInstanceRetired"); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new ValueTask(_disposal ??= Task.Run(() => _retire(_algorithm, this)));
    }
}

/// <summary>
/// Explicit managed Factory preparation. This service neither activates a Recipe nor grants production Ready.
/// Ignored cancellation retains the physical preparation slot until the actual stage and safe retirement finish.
/// </summary>
public sealed class AlgorithmPreparationService : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Id, string Version), Registration> _registrations = new();
    private readonly HashSet<IVisionAlgorithm> _owned = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<IVisionAlgorithm, object> _seenInstances = new();
    private readonly HashSet<PreparedAlgorithm> _published = new();
    private readonly Dictionary<Attempt, Task> _running = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _capacity;
    private readonly AlgorithmPreparationOptions _options;
    private int _reservedCreations;
    private bool _disposed;

    public AlgorithmPreparationService(IEnumerable<IVisionAlgorithmFactory> factories, AlgorithmPreparationOptions options)
    {
        ArgumentNullException.ThrowIfNull(factories); ArgumentNullException.ThrowIfNull(options);
        _options = options; _capacity = new(options.MaximumConcurrentPreparations, options.MaximumConcurrentPreparations);
        var references = new HashSet<IVisionAlgorithmFactory>(ReferenceEqualityComparer.Instance);
        foreach (var factory in factories)
        {
            if (_registrations.Count == 64) throw new ArgumentException("AlgorithmRegistrationCapacityExceeded", nameof(factories));
            if (factory is null || !references.Add(factory)) throw new ArgumentException("AlgorithmFactoryRegistrationInvalid", nameof(factories));
            // The descriptor and all nested contracts are immutable defensive copies supplied at registration.
            var descriptor = factory.Descriptor ?? throw new ArgumentException("AlgorithmDescriptorRequired", nameof(factories));
            if (!_registrations.TryAdd((descriptor.Identity.Id, descriptor.Identity.Version), new(factory, descriptor)))
                throw new ArgumentException("AlgorithmIdentityAlreadyRegistered", nameof(factories));
        }
        Descriptors = Array.AsReadOnly(_registrations.Values.Select(item => item.Descriptor)
            .OrderBy(item => item.Identity.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Version, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<AlgorithmDescriptor> Descriptors { get; }
    public int OwnedInstanceCount { get { lock (_sync) return _owned.Count; } }
    public int PendingPreparationCount { get { lock (_sync) return _running.Count; } }

    public async ValueTask<AlgorithmPreparationResult> PrepareAsync(AlgorithmPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = Stopwatch.GetTimestamp();
        lock (_sync) if (_disposed) return Failure("AlgorithmPreparationServiceDisposed");
        if (cancellationToken.IsCancellationRequested) return Failure("AlgorithmPreparationCancelled");
        if (request.PreparationTimeout <= TimeSpan.Zero || request.PreparationTimeout > _options.MaximumPreparationTimeout)
            return Failure("AlgorithmPreparationTimeoutInvalid");
        if (request.Algorithm is null || !_registrations.TryGetValue((request.Algorithm.Id, request.Algorithm.Version), out var registration))
            return Failure("AlgorithmNotRegistered");
        var descriptor = registration.Descriptor;
        if (descriptor.ResultSchema.Id != request.ResultSchemaId || descriptor.ResultSchema.Version != request.ResultSchemaVersion ||
            descriptor.ResultSchema.ContentHash != request.ResultSchemaContentHash ||
            descriptor.ResultSchema.OverlayContract.Id != request.OverlayContractId ||
            descriptor.ResultSchema.OverlayContract.Version != request.OverlayContractVersion ||
            descriptor.ResultSchema.OverlayContract.ContentHash != request.OverlayContractContentHash)
            return Failure("AlgorithmResultBindingMismatch");
        if (request.Configuration is null) return Failure("AlgorithmConfigurationRequired");
        AlgorithmConfigurationSnapshot configuration;
        IReadOnlyList<AlgorithmValidationIssue> issues;
        try
        {
            var source = request.Configuration;
            configuration = new(source.SchemaId, source.SchemaVersion, source.SchemaContentHash,
                source.CanonicalizationVersion, source.ContentHash, source.Values);
            issues = configuration.Validate(descriptor.ConfigurationSchema);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Failure("AlgorithmConfigurationInvalid"); }
        if (issues.Count != 0) return Failure("AlgorithmConfigurationInvalid", issues);

        var preparationRemaining = request.PreparationTimeout - TimeSpan.FromSeconds(
            (Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency);
        if (preparationRemaining <= TimeSpan.Zero) return Failure("AlgorithmPreparationTimedOut");
        var attempt = new Attempt(preparationRemaining, cancellationToken, _lifetime.Token);
        var factoryAcquired = false;
        var capacityAcquired = false;
        var started = false;
        try
        {
            // One Factory is never called concurrently, even when it returns the same instance.
            var initialRemaining = attempt.Remaining;
            if (initialRemaining <= TimeSpan.Zero) return Failure("AlgorithmPreparationTimedOut");
            factoryAcquired = await registration.Gate.WaitAsync(initialRemaining, attempt.WaitToken).ConfigureAwait(false);
            if (!factoryAcquired) return Failure("AlgorithmPreparationTimedOut");
            var remaining = attempt.Remaining;
            capacityAcquired = remaining > TimeSpan.Zero &&
                await _capacity.WaitAsync(remaining, attempt.WaitToken).ConfigureAwait(false);
            if (!capacityAcquired) return Failure("AlgorithmPreparationTimedOut");
            lock (_sync)
            {
                if (_disposed) return Failure("AlgorithmPreparationServiceDisposed");
                var work = Task.Run(() => PrepareCoreAsync(registration, configuration, attempt));
                _running.Add(attempt, work);
                started = true;
                _ = ObserveCompletionAsync(attempt, work);
            }
            remaining = attempt.Remaining;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            var result = await attempt.Completion.Task.WaitAsync(remaining, attempt.WaitToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (!result.Succeeded) return result;
                if (_disposed || cancellationToken.IsCancellationRequested || attempt.Remaining <= TimeSpan.Zero || !attempt.TryDeliver())
                    return Failure(_disposed ? "AlgorithmPreparationServiceDisposed" :
                        cancellationToken.IsCancellationRequested ? "AlgorithmPreparationCancelled" : "AlgorithmPreparationTimedOut");
                _published.Add(result.Prepared!);
                return result;
            }
        }
        catch (TimeoutException) { return Failure("AlgorithmPreparationTimedOut"); }
        catch (OperationCanceledException)
        { return Failure(cancellationToken.IsCancellationRequested ? "AlgorithmPreparationCancelled" : "AlgorithmPreparationServiceDisposed"); }
        finally
        {
            attempt.AbandonUnlessDelivered();
            if (!started)
            {
                if (capacityAcquired) _capacity.Release();
                if (factoryAcquired) registration.Gate.Release();
                attempt.Dispose();
            }
        }
    }

    private async Task PrepareCoreAsync(Registration registration, AlgorithmConfigurationSnapshot configuration, Attempt attempt)
    {
        IVisionAlgorithm? algorithm = null;
        var owned = false;
        var creationReserved = false;
        var delivered = false;
        var stage = "AlgorithmSemanticValidationFailed";
        try
        {
            attempt.Token.ThrowIfCancellationRequested();
            var issues = await registration.Factory.ValidateConfigurationAsync(configuration, attempt.Token).ConfigureAwait(false);
            if (issues is null || issues.Count != 0)
            {
                attempt.Completion.TrySetResult(Failure(stage));
                return;
            }
            attempt.Token.ThrowIfCancellationRequested();
            stage = "AlgorithmCreationFailed";
            lock (_sync)
            {
                if (_owned.Count + _reservedCreations >= _options.MaximumOwnedInstances)
                { attempt.Completion.TrySetResult(Failure("AlgorithmInstanceCapacityExceeded")); return; }
                _reservedCreations++;
                creationReserved = true;
            }
            algorithm = await registration.Factory.CreateAsync(configuration, attempt.Token).ConfigureAwait(false);
            if (algorithm is null) throw new InvalidOperationException();
            lock (_sync)
            {
                _reservedCreations--;
                creationReserved = false;
                if (_owned.Contains(algorithm))
                {
                    // Another attempt owns this object; disposing it would destroy that owner's instance.
                    attempt.Completion.TrySetResult(Failure("AlgorithmInstanceAlreadyOwned"));
                    return;
                }
                if (_seenInstances.TryGetValue(algorithm, out _))
                {
                    attempt.Completion.TrySetResult(Failure("AlgorithmInstanceRetired"));
                    return;
                }
                _seenInstances.Add(algorithm, new object());
                _owned.Add(algorithm); owned = true;
            }
            if (attempt.Completion.Task.IsCompleted) return;
            attempt.Token.ThrowIfCancellationRequested();
            stage = "AlgorithmWarmUpFailed";
            await algorithm.WarmUpAsync(attempt.Token).ConfigureAwait(false);
            attempt.Token.ThrowIfCancellationRequested();
            var prepared = new PreparedAlgorithm(registration.Descriptor, configuration, algorithm, RetirePublishedAsync);
            attempt.Completion.TrySetResult(new(true, "AlgorithmPrepared", prepared, Array.Empty<AlgorithmValidationIssue>()));
            delivered = await attempt.Delivery.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested)
        { attempt.Completion.TrySetResult(Failure("AlgorithmPreparationCancelled")); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { attempt.Completion.TrySetResult(Failure(stage)); }
        finally
        {
            if (creationReserved) lock (_sync) _reservedCreations--;
            await attempt.FinishStageAsync().ConfigureAwait(false);
            if (owned && !delivered && algorithm is not null) await RetireUnpublishedAsync(algorithm).ConfigureAwait(false);
            _capacity.Release();
            registration.Gate.Release();
        }
    }

    private async Task RetireUnpublishedAsync(IVisionAlgorithm algorithm)
    {
        try
        {
            await algorithm.DisposeAsync().ConfigureAwait(false);
            lock (_sync) _owned.Remove(algorithm);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { /* A failed retirement cannot prove that the object is safe to reuse. Keep its reservation. */ }
    }

    private async Task RetirePublishedAsync(IVisionAlgorithm algorithm, PreparedAlgorithm prepared)
    {
        try { await algorithm.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new InvalidOperationException("AlgorithmRetirementFailed"); }
        lock (_sync) { _owned.Remove(algorithm); _published.Remove(prepared); }
    }

    private async Task ObserveCompletionAsync(Attempt attempt, Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally { lock (_sync) _running.Remove(attempt); attempt.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var attempt in _running.Keys) attempt.AbandonUnlessDelivered();
            // Consumer cancellation callbacks run on separate bounded attempt work, never on this shutdown caller.
            _lifetime.Cancel();
            pending = _running.Values.Concat(_published.Select(item => item.DisposeAsync().AsTask())).ToArray();
        }
        try { await Task.WhenAll(pending).WaitAsync(_options.ShutdownWaitTimeout).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { /* Uncooperative stages keep their objects and slots; a bounded shutdown does not claim retirement. */ }
    }

    private static AlgorithmPreparationResult Failure(string code, IReadOnlyList<AlgorithmValidationIssue>? issues = null) =>
        new(false, code, null, Array.AsReadOnly((issues ?? Array.Empty<AlgorithmValidationIssue>()).Take(32).ToArray()));

    private sealed record Registration(IVisionAlgorithmFactory Factory, AlgorithmDescriptor Descriptor)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class Attempt : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TimeSpan _timeout;
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationTokenSource _waitCancellation;
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _lifetimeRegistration;
        private readonly object _cancellationGate = new();
        private Task? _cancellationTask;
        private bool _cancellationClosed;
        private int _deliveryState;
        public Attempt(TimeSpan timeout, CancellationToken caller, CancellationToken lifetime)
        {
            _timeout = timeout; _cancellation = new CancellationTokenSource();
            Token = _cancellation.Token;
            // This token is private to Runtime waits, so consumer callbacks cannot delay the caller.
            _waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
            WaitToken = _waitCancellation.Token;
            _callerRegistration = caller.Register(static state => ((Attempt)state!).RequestCancellation(), this);
            _lifetimeRegistration = lifetime.Register(static state => ((Attempt)state!).RequestCancellation(), this);
        }
        public CancellationToken Token { get; }
        public CancellationToken WaitToken { get; }
        public TimeSpan Remaining => _timeout - TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _started) / (double)Stopwatch.Frequency);
        public TaskCompletionSource<AlgorithmPreparationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Delivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryDeliver()
        {
            if (Interlocked.CompareExchange(ref _deliveryState, 1, 0) != 0) return false;
            Delivery.TrySetResult(true); return true;
        }
        public void AbandonUnlessDelivered()
        {
            if (Interlocked.CompareExchange(ref _deliveryState, 2, 0) != 0) return;
            RequestCancellation();
            Delivery.TrySetResult(false);
        }
        private void RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cancellationClosed || _cancellationTask is not null) return;
                _cancellationTask = Task.Run(() =>
                {
                    try { _cancellation.Cancel(); }
                    catch (AggregateException) { }
                    catch (ObjectDisposedException) { }
                });
            }
        }
        public Task FinishStageAsync()
        {
            lock (_cancellationGate)
            {
                _cancellationClosed = true;
                _callerRegistration.Unregister();
                _lifetimeRegistration.Unregister();
                return _cancellationTask ?? Task.CompletedTask;
            }
        }
        public void Dispose()
        {
            lock (_cancellationGate) _cancellationClosed = true;
            _callerRegistration.Unregister(); _lifetimeRegistration.Unregister(); _cancellation.Dispose();
            _waitCancellation.Dispose();
        }
    }
}
