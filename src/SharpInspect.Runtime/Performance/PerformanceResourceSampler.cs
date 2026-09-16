using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Runtime.Performance;

/// <summary>Called only by the monitor's single retained physical worker, never by a production callback.</summary>
internal sealed class PerformanceResourceSampler
{
    private readonly string? _databasePath;
    private readonly FrameBufferPool? _pool;
    private readonly IPresentationPerformanceQuery? _presentation;
    private readonly Func<IReadOnlyList<PerformanceResourceValue>> _runtime;
    private long _lastAt, _lastCpuTicks, _lastGcIndex;
    private double _observedGcPauseMilliseconds;
    private bool _gcPauseCoverageLost;

    internal PerformanceResourceSampler(string? databasePath, FrameBufferPool? pool,
        IPresentationPerformanceQuery? presentation, Func<IReadOnlyList<PerformanceResourceValue>> runtime)
    { _databasePath = databasePath; _pool = pool; _presentation = presentation; _runtime = runtime; }

    internal PerformanceResourceSample Read(long sequence)
    {
        var at = Stopwatch.GetTimestamp();
        var values = Enum.GetValues<PerformanceResource>().ToDictionary(resource => resource,
            resource => Unknown(resource, "PerformanceCollectorUnavailable"));
        void Value(PerformanceResource resource, double value) => values[resource] = double.IsFinite(value) && value >= 0
            ? new(resource, value, PerformanceObservationOutcome.Observed, "PerformanceResourceObserved")
            : Unknown(resource, "PerformanceResourceValueInvalid");
        void Observe(PerformanceResource resource, Func<double> read)
        {
            try { Value(resource, read()); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { values[resource] = Unknown(resource, "PerformanceResourceReadFailed"); }
        }

        using var process = Process.GetCurrentProcess();
        try { process.Refresh(); } catch (Exception exception) when (exception is not OutOfMemoryException) { }
        Observe(PerformanceResource.WorkingSetBytes, () => process.WorkingSet64);
        Observe(PerformanceResource.PrivateBytes, () => process.PrivateMemorySize64);
        Observe(PerformanceResource.ThreadCount, () => process.Threads.Count);
        Observe(PerformanceResource.HandleCount, () => process.HandleCount);
        try
        {
            var cpu = process.TotalProcessorTime.Ticks;
            if (_lastAt != 0 && at > _lastAt && cpu >= _lastCpuTicks)
                Value(PerformanceResource.ProcessCpuPercent, (cpu - _lastCpuTicks) / (double)TimeSpan.TicksPerSecond /
                    ((at - _lastAt) / (double)Stopwatch.Frequency) / Environment.ProcessorCount * 100);
            else values[PerformanceResource.ProcessCpuPercent] = Unknown(PerformanceResource.ProcessCpuPercent, "PerformanceCpuWarmupSample");
            _lastCpuTicks = cpu; _lastAt = at;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { values[PerformanceResource.ProcessCpuPercent] = Unknown(PerformanceResource.ProcessCpuPercent, "PerformanceCpuReadFailed"); }

        var gc = GC.GetGCMemoryInfo();
        if (gc.Index != 0)
        {
            Value(PerformanceResource.ManagedHeapBytes, gc.HeapSizeBytes);
            Value(PerformanceResource.ManagedCommittedBytes, gc.TotalCommittedBytes);
            if (values[PerformanceResource.PrivateBytes].Value is { } privateBytes)
                Value(PerformanceResource.NonGcPrivateBytesEstimate, Math.Max(0, privateBytes - gc.TotalCommittedBytes));
        }
        values[PerformanceResource.NativeMemoryBytes] = Unknown(PerformanceResource.NativeMemoryBytes, "ExactNativeAllocatorCollectorUnavailable");
        Value(PerformanceResource.TotalAllocatedBytes, GC.GetTotalAllocatedBytes(precise: false));
        Value(PerformanceResource.Gen0Collections, GC.CollectionCount(0));
        Value(PerformanceResource.Gen1Collections, GC.CollectionCount(1));
        Value(PerformanceResource.Gen2Collections, GC.CollectionCount(2));
        // Polling GCMemoryInfo cannot reconstruct collections skipped between samples. Never invent their pause time.
        if (_lastGcIndex > 0 && gc.Index > _lastGcIndex + 1) _gcPauseCoverageLost = true;
        if (!_gcPauseCoverageLost && gc.Index > 0 && _lastGcIndex > 0 && gc.Index <= _lastGcIndex + 1)
        {
            if (gc.Index != _lastGcIndex)
                foreach (var pause in gc.PauseDurations) _observedGcPauseMilliseconds += pause.TotalMilliseconds;
            Value(PerformanceResource.GcPauseMilliseconds, _observedGcPauseMilliseconds);
        }
        else values[PerformanceResource.GcPauseMilliseconds] = Unknown(PerformanceResource.GcPauseMilliseconds, "PerformanceGcPauseCoverageUnknown");
        _lastGcIndex = gc.Index;

        if (_pool is not null)
        {
            var pool = _pool.GetSnapshot();
            Value(PerformanceResource.FramePoolCapacity, pool.Capacity);
            Value(PerformanceResource.OutstandingFrameLeases, pool.OutstandingLeases);
            Value(PerformanceResource.PeakFrameLeases, pool.PeakLeases);
            Value(PerformanceResource.FramePoolExhaustions, pool.ExhaustionCount);
        }
        if (!string.IsNullOrEmpty(_databasePath))
        {
            Observe(PerformanceResource.SqliteDatabaseBytes, () => new FileInfo(_databasePath).Length);
            Observe(PerformanceResource.SqliteWalBytes, () =>
            {
                _ = new FileInfo(_databasePath).Length;
                try { return new FileInfo(_databasePath + "-wal").Length; }
                catch (FileNotFoundException) { return 0; } // Observed absent WAL of an existing database.
            });
        }
        foreach (var value in _runtime()) values[value.Resource] = value;
        if (_presentation is not null)
        {
            try
            {
                var presentation = _presentation.ReadPerformance();
                if (presentation.MonotonicFrequency <= 0) throw new InvalidOperationException("PerformancePresentationClockInvalid");
                Value(PerformanceResource.WpfSnapshotsReceived, presentation.SnapshotsReceived);
                Value(PerformanceResource.WpfSnapshotsApplied, presentation.SnapshotsApplied);
                Value(PerformanceResource.WpfSnapshotsCoalesced, presentation.SnapshotsCoalesced);
                Value(PerformanceResource.WpfSnapshotApplyMilliseconds, presentation.SnapshotApplyTicks * 1000d / presentation.MonotonicFrequency);
                Value(PerformanceResource.WpfImageCopies, presentation.ImageCopies);
                Value(PerformanceResource.WpfImageCopyBytes, presentation.ImageCopyBytes);
                Value(PerformanceResource.WpfImageCopyMilliseconds, presentation.ImageCopyTicks * 1000d / presentation.MonotonicFrequency);
                Value(PerformanceResource.WpfRenderCount, presentation.RenderCount);
                Value(PerformanceResource.WpfRenderMilliseconds, presentation.RenderTicks * 1000d / presentation.MonotonicFrequency);
                Value(PerformanceResource.WpfSnapshotApplyFailures, presentation.SnapshotApplyFailures);
                Value(PerformanceResource.WpfImageCopyFailures, presentation.ImageCopyFailures);
                Value(PerformanceResource.WpfRenderFailures, presentation.RenderFailures);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                foreach (var resource in Enum.GetValues<PerformanceResource>().Where(value => value >= PerformanceResource.WpfSnapshotsReceived))
                    values[resource] = Unknown(resource, "PerformancePresentationReadFailed");
            }
        }
        return new(sequence, at, values.Values.OrderBy(value => value.Resource));
    }

    internal static PerformanceResourceValue Unknown(PerformanceResource resource, string reason) =>
        new(resource, null, PerformanceObservationOutcome.Unknown, reason);
    internal static PerformanceResourceValue Measured(PerformanceResource resource, double value) =>
        new(resource, value, PerformanceObservationOutcome.Observed, "PerformanceResourceObserved");
}
