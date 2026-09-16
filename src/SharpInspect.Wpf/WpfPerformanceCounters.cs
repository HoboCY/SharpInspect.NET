using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Passive scalar counters. Reads neither dispatch nor render. Counts and complete copied bytes
/// describe successful operations; elapsed ticks include failed attempts, whose counts are separate.
/// Apply time starts inside the dispatcher action and excludes queue wait. Fields are individually
/// atomic, not a transactional cut; received minus applied/coalesced is not an exact pending count.
/// No pixels or payloads are retained. Zero is observed only when this collector is registered.
/// </summary>
public sealed class WpfPerformanceCounters : IPresentationPerformanceQuery
{
    public static WpfPerformanceCounters Shared { get; } = new();
    private long _received, _applied, _coalesced, _applyTicks, _copies, _copyBytes, _copyTicks, _renders, _renderTicks;
    private long _applyFailures, _copyFailures, _renderFailures;
    internal WpfPerformanceCounters() { }

    public PresentationPerformanceSnapshot ReadPerformance() => new(
        Interlocked.Read(ref _received), Interlocked.Read(ref _applied), Interlocked.Read(ref _coalesced),
        Interlocked.Read(ref _applyTicks), Interlocked.Read(ref _copies), Interlocked.Read(ref _copyBytes),
        Interlocked.Read(ref _copyTicks), Interlocked.Read(ref _renders), Interlocked.Read(ref _renderTicks), Stopwatch.Frequency)
    {
        SnapshotApplyFailures = Interlocked.Read(ref _applyFailures),
        ImageCopyFailures = Interlocked.Read(ref _copyFailures), RenderFailures = Interlocked.Read(ref _renderFailures)
    };

    internal void RecordSnapshotReceived() => Interlocked.Increment(ref _received);
    internal void RecordSnapshotCoalesced() => Interlocked.Increment(ref _coalesced);
    internal void RecordSnapshotApplied(long ticks) { Interlocked.Increment(ref _applied); Interlocked.Add(ref _applyTicks, ticks); }
    internal void RecordSnapshotApplyFailed(long ticks) { Interlocked.Increment(ref _applyFailures); Interlocked.Add(ref _applyTicks, ticks); }
    internal void RecordSnapshotGuardElapsed(long ticks) => Interlocked.Add(ref _applyTicks, ticks);
    internal void RecordImageCopied(long bytes, long ticks)
    { Interlocked.Increment(ref _copies); Interlocked.Add(ref _copyBytes, bytes); Interlocked.Add(ref _copyTicks, ticks); }
    internal void RecordImageCopyFailed(long ticks) { Interlocked.Increment(ref _copyFailures); Interlocked.Add(ref _copyTicks, ticks); }
    internal void RecordRendered(long ticks) { Interlocked.Increment(ref _renders); Interlocked.Add(ref _renderTicks, ticks); }
    internal void RecordRenderFailed(long ticks) { Interlocked.Increment(ref _renderFailures); Interlocked.Add(ref _renderTicks, ticks); }
}
