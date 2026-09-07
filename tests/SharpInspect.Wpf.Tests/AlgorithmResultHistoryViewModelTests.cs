using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class AlgorithmResultHistoryViewModelTests
{
    [Fact]
    public async Task V114_U01_KeysetPagesKeepWatermarkAndSelectionIsReadOnly()
    {
        var first = Record(1); var second = Record(2); var third = Record(3);
        var query = new Query(filter => Task.FromResult(filter.AfterPosition == 0
            ? Page(new[] { first, second }, 3, 2) : Page(new[] { third }, 3, null)));
        await using var model = new AlgorithmResultHistoryViewModel(query, new InlineUiDispatcher(), 2);
        await model.RefreshAsync();
        Assert.Equal(2, model.Rows.Count);
        Assert.Same(first.Overlay, model.SelectedOverlay);
        model.SelectedRecord = second;
        Assert.Same(second.Overlay, model.SelectedOverlay);
        await model.NextPageAsync();
        Assert.Single(model.Rows);
        Assert.Equal(3L, query.LastFilter!.ThroughPosition);
        Assert.Equal(2, query.LastFilter.AfterPosition);
        Assert.Same(third, model.SelectedRecord);
        Assert.False(model.HasNextPage);
    }

    [Fact]
    public async Task V114_U02_FilterChangeDiscardsUncancelledLatePageAndClearsGeometry()
    {
        var response = new TaskCompletionSource<AlgorithmResultPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Signal();
        var query = new Query(_ => { entered.TrySetResult(true); return response.Task; });
        await using var model = new AlgorithmResultHistoryViewModel(query, new InlineUiDispatcher());
        var load = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            model.CorrelationText = Guid.NewGuid().ToString("D");
            response.TrySetResult(Page(new[] { Record(1) }, 1, null));
            await load.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(model.Rows); Assert.Null(model.SelectedOverlay);
            Assert.False(model.IsBusy);
            Assert.Contains("筛选已变化", model.StatusMessage);
        }
        finally { response.TrySetResult(Page(Array.Empty<AlgorithmResultRecord>(), 0, null)); }
    }

    [Fact]
    public async Task V114_U03_InvalidOrUnavailablePagesNeverExposeSubsetOrRawReason()
    {
        var record = Record(1);
        var pages = new[]
        {
            new AlgorithmResultPage(false, "private-secret-detail", Array.AsReadOnly(new[] { record }), 1, null),
            Page(new[] { record, record }, 2, null),
            Page(new[] { record }, 0, null),
            Page(new[] { record }, 2, 2)
        };
        foreach (var page in pages)
        {
            await using var model = new AlgorithmResultHistoryViewModel(new Query(_ => Task.FromResult(page)),
                new InlineUiDispatcher());
            await model.RefreshAsync();
            Assert.Empty(model.Rows); Assert.Null(model.SelectedOverlay);
            Assert.Contains("不可用", model.StatusMessage);
            Assert.DoesNotContain("private", model.StatusMessage);
        }
        await using var failed = new AlgorithmResultHistoryViewModel(new Query(_ =>
            throw new InvalidOperationException("private-secret-detail")), new InlineUiDispatcher());
        await failed.RefreshAsync();
        Assert.Empty(failed.Rows); Assert.DoesNotContain("private", failed.StatusMessage);
    }

    [Fact]
    public async Task V114_U04_DisposeAndRepeatedRefreshDoNotAdmitLateDataOrParallelQuery()
    {
        var response = new TaskCompletionSource<AlgorithmResultPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Signal();
        var query = new Query(_ => { entered.TrySetResult(true); return response.Task; });
        var model = new AlgorithmResultHistoryViewModel(query, new InlineUiDispatcher());
        var load = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await model.RefreshAsync();
            Assert.Equal(1, query.Count);
            await model.DisposeAsync();
            response.TrySetResult(Page(new[] { Record(1) }, 1, null));
            await load.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(model.Rows); Assert.Null(model.SelectedOverlay);
            Assert.False(model.RefreshCommand.CanExecute(null));
            Assert.Contains("已关闭", model.StatusMessage);
        }
        finally { response.TrySetResult(Page(Array.Empty<AlgorithmResultRecord>(), 0, null)); await model.DisposeAsync(); }
    }

    [Fact]
    public async Task V114_U05_InvalidFilterDoesNotQueryAndMissingCapabilityIsExplicit()
    {
        var query = new Query(_ => Task.FromResult(Page(Array.Empty<AlgorithmResultRecord>(), 0, null)));
        await using var model = new AlgorithmResultHistoryViewModel(query, new InlineUiDispatcher());
        model.CorrelationText = "not-an-id";
        await model.RefreshAsync();
        model.CorrelationText = Guid.NewGuid().ToString("D");
        model.CorrelationKind = ExecutionKind.Production;
        await model.RefreshAsync();
        Assert.Equal(0, query.Count);
        Assert.Null(model.SelectedOverlay);
        await using var missing = new AlgorithmResultHistoryViewModel(null, new InlineUiDispatcher());
        await missing.RefreshAsync();
        Assert.Contains("未配置", missing.StatusMessage);
        Assert.False(missing.RefreshCommand.CanExecute(null));
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task V114_U06_ActualPanelBindsSelectionAndClearsRenderedSnapshot()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            System.Windows.Window? window = null;
            AlgorithmResultHistoryViewModel? model = null;
            try
            {
                var record = Record(1);
                model = new AlgorithmResultHistoryViewModel(new Query(_ => Task.FromResult(Page(new[] { record }, 1, null))),
                    new InlineUiDispatcher());
                model.RefreshAsync().GetAwaiter().GetResult();
                var panel = new AlgorithmResultPanel { DataContext = model };
                window = new System.Windows.Window { Content = panel, Width = 900, Height = 750,
                    ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual };
                window.Show(); window.UpdateLayout();
                Assert.Same(record.Overlay, panel.Presenter.Snapshot);
                Assert.True(panel.Presenter.IsEmpty);
                model.SelectedRecord = null;
                window.UpdateLayout();
                Assert.Null(panel.Presenter.Snapshot);
                Assert.False(panel.Presenter.IsAvailable);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally
            {
                window?.Close(); model?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completion.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static AlgorithmResultPage Page(AlgorithmResultRecord[] records, long through, long? next) =>
        new(true, "AlgorithmResultHistoryAvailable", Array.AsReadOnly(records), through, next);

    private static AlgorithmResultRecord Record(long position)
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
            new RegionOfInterest(0, 0, 4, 3), VisionPixelFormat.Mono8, null, 1000, 0, null);
        var frame = new FrameMetadata(correlation, "Primary", 4, 3, 4, VisionPixelFormat.Mono8, null,
            DateTimeOffset.UtcNow, camera);
        var schema = new AlgorithmResultSchema("Ui.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            Array.Empty<string>(), new OverlayContract("Ui.Overlay", "1"));
        var policy = new AlgorithmExecutionPolicy("Ui.Policy", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        Assert.True(policy.TryBind(new(new RecipeReference("Ui.Recipe", "1", new string('A', 64)),
            TimeSpan.FromSeconds(1)), out var timing, out _));
        return new(position, Guid.NewGuid(), DateTimeOffset.UtcNow, new string('B', 64), Guid.NewGuid(),
            new AlgorithmIdentity("Ui.Algorithm", "1"), new string('C', 64), "Ui.Config", "1", new string('D', 64),
            frame, schema, new(InspectionDecision.Pass, null, Array.Empty<AlgorithmMeasurement>(),
                new OutputOverlaySet(schema.OverlayContract)), timing!, 1, 10_000_000);
    }

    private sealed class Query : IAlgorithmResultQuery
    {
        private readonly Func<AlgorithmResultFilter, Task<AlgorithmResultPage>> _read;
        public Query(Func<AlgorithmResultFilter, Task<AlgorithmResultPage>> read) => _read = read;
        public int Count { get; private set; }
        public AlgorithmResultFilter? LastFilter { get; private set; }
        public ValueTask<AlgorithmResultPage> QueryAsync(AlgorithmResultFilter filter, CancellationToken cancellationToken = default)
        { Count++; LastFilter = filter; return new(_read(filter)); }
    }
}
