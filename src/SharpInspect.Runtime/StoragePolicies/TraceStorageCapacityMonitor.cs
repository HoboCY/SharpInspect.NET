using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.StoragePolicies;

/// <summary>One observer owns its physical turn until it really exits, including after a logical timeout.</summary>
internal sealed class TraceStorageCapacityMonitor : ITraceStorageCapacityQuery
{
    private readonly ProductionStoreOptions _options;
    private readonly Guid _epoch;
    private readonly Action _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<ProductionStoreOptions, TraceStoragePolicyDefinition, StoreDeadline, CancellationToken,
        TraceStoragePhysicalObservation> _inventory;
    private readonly Task _loop;
    private sealed record Observation(TraceStorageCapacitySnapshot Snapshot, long Timestamp);
    private Observation _observation;
    private readonly Func<TraceCheckpointObservation>? _checkpoint;
    private long _revision;

    internal TraceStorageCapacityMonitor(ProductionStoreOptions options, Guid epoch, Task initialization,
        Action changed, Func<ProductionStoreOptions, TraceStoragePolicyDefinition, StoreDeadline, CancellationToken,
            TraceStoragePhysicalObservation>? inventory = null, Func<TraceCheckpointObservation>? checkpoint = null)
    {
        _options = options;
        _epoch = epoch;
        _changed = changed;
        _inventory = inventory ?? TraceStorageCapacityInventory.Observe;
        _checkpoint = checkpoint;
        _observation = new(TraceStorageCapacityEvaluator.Unavailable(epoch, 0, DateTimeOffset.UtcNow, "TraceStorageObservationPending"), 0);
        _loop = RunAsync(initialization);
    }

    internal Task PhysicalCompletion => _loop;

    public ValueTask<TraceStorageCapacitySnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Current());
    }

    internal TraceStorageCapacitySnapshot Current()
    {
        var observation = Volatile.Read(ref _observation);
        var result = observation.Snapshot;
        var timestamp = observation.Timestamp;
        var age = timestamp == 0 ? TimeSpan.MaxValue :
            TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency);
        // The observation cadence and physical timeout are immutable deployment inputs.
        // A hung observer never keeps a formerly healthy observation authoritative.
        var maximumAge = _options.StorageRetention!.ObservationInterval + _options.StorageRetention.FileTimeout;
        if (result.Available && (age > maximumAge || DateTimeOffset.UtcNow < result.ObservedAtUtc))
            return result with { Available = false, Health = TraceStorageHealth.Unavailable,
                ReasonCode = "TraceStorageObservationStale", AdmissionBlockers = new[] { "TraceStorageObservationStale" } };
        return result;
    }

    internal async Task<bool> StopAsync()
    {
        _stop.Cancel();
        try { await _loop.WaitAsync(_options.StorageRetention!.FileTimeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task RunAsync(Task initialization)
    {
        try
        {
            await initialization.WaitAsync(_stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                var turn = Task.Run(ObserveAsync, CancellationToken.None);
                try
                {
                    Publish(await turn.WaitAsync(_options.StorageRetention!.FileTimeout, _stop.Token).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LastFailure = exception;
                    Publish(TraceStorageCapacityEvaluator.Unavailable(_epoch, Interlocked.Increment(ref _revision),
                        DateTimeOffset.UtcNow, exception is TimeoutException ? "TraceStorageObservationDeadlineExceeded" :
                            _stop.IsCancellationRequested ? "TraceStorageObservationStopped" : "TraceStorageObservationUnavailable"));
                    // Keep the connection/native inventory and worker slot until the actual turn exits.
                    // Its late snapshot is discarded; it cannot restore admission after its deadline.
                    try { await turn.ConfigureAwait(false); } catch (Exception late) when (late is not OutOfMemoryException) { }
                }
                await Task.Delay(_options.StorageRetention!.ObservationInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Publish(TraceStorageCapacityEvaluator.Unavailable(_epoch, Interlocked.Increment(ref _revision),
                DateTimeOffset.UtcNow, "TraceStorageObservationStartupUnavailable"));
        }
    }

    internal Exception? LastFailure { get; private set; }

    private async Task<TraceStorageCapacitySnapshot> ObserveAsync()
    {
        var deadline = new StoreDeadline(_options.StorageRetention!.FileTimeout);
        var policy = await new SqliteTraceStoragePolicyQuery(_options).ReadAsync(null, _stop.Token).ConfigureAwait(false);
        if (!policy.Available || policy.Publication is null)
            return TraceStorageCapacityEvaluator.Unavailable(_epoch, Interlocked.Increment(ref _revision),
                DateTimeOffset.UtcNow, policy.ReasonCode);
        var physical = _inventory(_options, policy.Publication.Policy, deadline, _stop.Token);
        var images = _options.ImageFinalization is null ? null :
            await new SqliteProductionImageEvidenceQuery(_options).ReadBacklogAsync(_stop.Token).ConfigureAwait(false);
        var outbox = _options.Outbox is null ? null : Outbox.ProductionOutboxBinding.CompleteBacklog(_options.Outbox,
            await new SqliteProductionOutboxQuery(_options).ReadBacklogAsync(_stop.Token).ConfigureAwait(false));
        SqliteNative.EnsureDeadline(deadline, _stop.Token);
        return TraceStorageCapacityEvaluator.Evaluate(_epoch, Interlocked.Increment(ref _revision), DateTimeOffset.UtcNow,
            policy, _options.TraceStoragePolicies!.DeploymentScope, physical, images, outbox,
            _checkpoint?.Invoke() ?? Volatile.Read(ref _observation).Snapshot.Checkpoint);
    }

    private void Publish(TraceStorageCapacitySnapshot snapshot)
    {
        if (_checkpoint is not null) snapshot = snapshot with { Checkpoint = _checkpoint() };
        Volatile.Write(ref _observation, new(snapshot, Stopwatch.GetTimestamp()));
        _changed();
    }
}
