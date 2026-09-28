from pathlib import Path
root = Path('src/SharpInspect.Wpf')
for name, kind in [('FramePreviewImage.cs', 'VisionFrame'), ('PreviewDisplayImage.cs', 'CameraPreviewFrame')]:
    p=root/name
    s=p.read_text(encoding='utf-8-sig')
    marker='        ArgumentNullException.ThrowIfNull(frame);\n        var copyStartTicks = Stopwatch.GetTimestamp();'
    assert s.count(marker)==1
    typ=p.stem
    wrapper='''        ArgumentNullException.ThrowIfNull(frame);
        var started = Stopwatch.GetTimestamp();
        TYP? result = null;
        try { return result = CopyCore(frame); }
        finally
        {
            var ticks = Stopwatch.GetTimestamp() - started;
            if (result is null) counters.RecordImageCopyFailed(ticks);
            else counters.RecordImageCopied(checked((long)result.BitmapSource.PixelWidth * result.BitmapSource.PixelHeight *
                ((result.BitmapSource.Format.BitsPerPixel + 7) / 8)), ticks);
        }
    }

    private static TYP CopyCore(KIND frame)
    {'''.replace('TYP',typ).replace('KIND',kind)
    s=s.replace(marker,wrapper)
    s='\n'.join(l for l in s.split('\n') if '只有完整复制成功才计入次数、显示字节与耗时' not in l and 'counters.RecordImageCopied(displayBytes,' not in l)
    p.write_text(s,encoding='utf-8')
p=root/'StationShellViewModel.cs'
s=p.read_text(encoding='utf-8-sig')
s=s.replace('    private readonly int _feedCapacity;\n','').replace('        _feedCapacity = feedCapacity;\n','')
a=s.index('    // 镜像有界通道'); b=s.index('    private readonly HashSet<Guid>',a)
s=s[:a]+s[b:]
s=s.replace('            AllowSynchronousContinuations = false\n        });','            AllowSynchronousContinuations = false\n        }, item => { if (item.Snapshot is not null) _counters.RecordSnapshotCoalesced(); });')
s=s.replace('后者的丢弃由镜像计数精确记入 coalesced。','原生 DropOldest 回调只把被淘汰的快照计入 coalesced。')
a=s.index('    /// <summary>\n    /// 写入一个有界 feed 条目'); b=s.index('    private async Task MarkUnavailableAsync',a)
s=s[:a]+'    private void TryWriteFeedItem(FeedItem item) => _feed.Writer.TryWrite(item);\n\n'+s[b:]
a=s.index('            // 与 ReadAllAsync 等价'); b=s.index('                    if (ConsumeDiscontinuity()',a)
s=s[:a]+'            await foreach (var item in _feed.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))\n            {\n'+s[b:]
a=s.index('    private async Task RenderFeedAsync'); b=s.index('    private ',a+15)
part=s[a:b]
tail='                }\n            }\n        }\n        catch'
assert tail in part
part=part.replace(tail,'            }\n        }\n        catch',1)
s=s[:a]+part+s[b:]
s=s.replace('            var applyStartTicks = Stopwatch.GetTimestamp();\n','            var applyStartTicks = Stopwatch.GetTimestamp();\n            var failed = false;\n            try\n            {\n',1)
old='''            // 只有真正完成应用才累计次数与耗时；调度器排队等待不计入，应用路径异常也不计入。
            if (applied)
                _counters.RecordSnapshotApplied(Stopwatch.GetTimestamp() - applyStartTicks);'''
new='''            }
            catch { failed = true; throw; }
            finally
            {
                var ticks = Stopwatch.GetTimestamp() - applyStartTicks;
                if (failed) _counters.RecordSnapshotApplyFailed(ticks);
                else if (applied) _counters.RecordSnapshotApplied(ticks);
                else _counters.RecordSnapshotGuardElapsed(ticks);
            }'''
assert old in s
s=s.replace(old,new)
p.write_text(s,encoding='utf-8')
p=root/'FrameOverlayPresenter.cs'
s=p.read_text(encoding='utf-8-sig')
old='''    public RenderedOverlayPreview RenderPreview()
    {
        var renderStartTicks = Stopwatch.GetTimestamp();'''
new='''    public RenderedOverlayPreview RenderPreview()
    {
        var started = Stopwatch.GetTimestamp();
        var succeeded = false;
        try { var result = RenderPreviewCore(); succeeded = true; return result; }
        finally
        {
            var ticks = Stopwatch.GetTimestamp() - started;
            if (succeeded) PerformanceCounters.RecordRendered(ticks);
            else PerformanceCounters.RecordRenderFailed(ticks);
        }
    }

    private RenderedOverlayPreview RenderPreviewCore()
    {'''
assert old in s
s=s.replace(old,new)
s=s.replace('            // 只有完整绘制成功才计数；绘制异常路径不计次数与耗时。\n            PerformanceCounters.RecordRendered(Stopwatch.GetTimestamp() - renderStartTicks);\n','')
old='''    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);'''
new='''    protected override void OnRender(DrawingContext drawingContext)
    {
        var started = Stopwatch.GetTimestamp();
        var succeeded = false;
        try { succeeded = RenderCore(drawingContext); }
        finally
        {
            var ticks = Stopwatch.GetTimestamp() - started;
            if (succeeded) PerformanceCounters.RecordRendered(ticks);
            else PerformanceCounters.RecordRenderFailed(ticks);
        }
    }

    private bool RenderCore(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);'''
assert old in s
s=s.replace(old,new)
s=s.replace('        if (!projection.Available) return;\n        var renderStartTicks = Stopwatch.GetTimestamp();','        if (!projection.Available) return false;')
s=s.replace('''        else
            // 实际绘制完成才计数；绘制失败路径不计。
            PerformanceCounters.RecordRendered(Stopwatch.GetTimestamp() - renderStartTicks);''','''        return !drawingFailed;''')
p.write_text(s,encoding='utf-8')
p=root/'WpfPerformanceCounters.cs'
p.write_text('''using System.Diagnostics;
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
''',encoding='utf-8')
