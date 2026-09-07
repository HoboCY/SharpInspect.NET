using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class CommandTraceViewModelTests
{
    [Fact]
    public async Task V102_U01_RefreshFreezesSnapshotUpperBoundAndNextPageKeepsFilters()
    {
        var query = new RecordingTraceQuery
        {
            Responses = new Queue<CommandTracePage>(new[]
            {
                Page(through: 12, nextAfter: 5, recordPosition: 5),
                Page(through: 12, nextAfter: null, recordPosition: 8)
            })
        };
        var correlationId = Guid.NewGuid();
        await using var viewModel = new CommandTraceViewModel(query);
        viewModel.CorrelationIdText = correlationId.ToString();
        viewModel.ClaimedPrincipalIdText = "claimed/operator";

        await viewModel.RefreshAsync();
        await viewModel.NextPageAsync();

        Assert.Collection(query.Filters,
            first =>
            {
                Assert.Equal(correlationId, first.CorrelationId);
                Assert.Equal("claimed/operator", first.ClaimedPrincipalId);
                Assert.Equal(0, first.AfterPosition);
                Assert.Null(first.ThroughPosition);
                Assert.Equal(50, first.PageSize);
            },
            second =>
            {
                Assert.Equal(correlationId, second.CorrelationId);
                Assert.Equal("claimed/operator", second.ClaimedPrincipalId);
                Assert.Equal(5, second.AfterPosition);
                Assert.Equal(12, second.ThroughPosition);
                Assert.Equal(50, second.PageSize);
            });
        Assert.Equal(12, viewModel.ThroughPosition);
        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Single(viewModel.Rows);
        Assert.Equal(8, viewModel.Rows[0].Position);
    }

    [Fact]
    public async Task V102_U02_InvalidCorrelationGuidIsRecoverableWithoutQuerying()
    {
        var query = new RecordingTraceQuery();
        await using var viewModel = new CommandTraceViewModel(query);

        viewModel.CorrelationIdText = "not-a-guid";
        await viewModel.RefreshAsync();

        Assert.Empty(query.Filters);
        Assert.False(viewModel.HasCurrentPage);
        Assert.Empty(viewModel.Rows);
        Assert.Contains("Guid", viewModel.ErrorMessage);
        Assert.Contains("修正", viewModel.StatusMessage);

        var valid = Guid.NewGuid();
        viewModel.CorrelationIdText = valid.ToString();
        query.Responses.Enqueue(Page(through: 4, nextAfter: null, recordPosition: 4));
        await viewModel.RefreshAsync();

        Assert.Null(viewModel.ErrorMessage);
        Assert.True(viewModel.HasCurrentPage);
        Assert.Equal(valid, query.Filters.Single().CorrelationId);
    }

    [Fact]
    public async Task V102_U03_QueryFailureClearsRowsAndDoesNotRepresentStalePageAsCurrent()
    {
        var query = new RecordingTraceQuery
        {
            Responses = new Queue<CommandTracePage>(new[]
            {
                Page(through: 4, nextAfter: null, recordPosition: 4)
            }),
        };
        await using var viewModel = new CommandTraceViewModel(query);

        await viewModel.RefreshAsync();
        Assert.True(viewModel.HasCurrentPage);
        Assert.Single(viewModel.Rows);

        query.Exception = new InvalidOperationException("store unavailable");
        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasCurrentPage);
        Assert.Empty(viewModel.Rows);
        Assert.Null(viewModel.ThroughPosition);
        Assert.Contains("TraceQueryFailed", viewModel.ErrorMessage);
        Assert.DoesNotContain("store unavailable", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task V102_U04_ConcurrentOlderResponseCannotOverrideNewerFilter()
    {
        var query = new BlockingTraceQuery();
        await using var viewModel = new CommandTraceViewModel(query);
        var firstCorrelation = Guid.NewGuid();
        var secondCorrelation = Guid.NewGuid();

        viewModel.CorrelationIdText = firstCorrelation.ToString();
        var firstRefresh = viewModel.RefreshAsync();
        await query.WaitForCallAsync(1);

        viewModel.CorrelationIdText = secondCorrelation.ToString();
        var secondRefresh = viewModel.RefreshAsync();
        await query.WaitForCallAsync(2);

        query.Complete(1, Page(through: 20, nextAfter: null, recordPosition: 2));
        query.Complete(0, Page(through: 20, nextAfter: null, recordPosition: 1));
        await secondRefresh;
        await firstRefresh;

        Assert.Equal(secondCorrelation, query.Filters[1].CorrelationId);
        Assert.True(viewModel.HasCurrentPage);
        Assert.Single(viewModel.Rows);
        Assert.Equal(2, viewModel.Rows[0].Position);
    }

    [Fact]
    public async Task V102_U05_PreviousPageRequeriesWithinTheSameSnapshotUpperBound()
    {
        var query = new RecordingTraceQuery
        {
            Responses = new Queue<CommandTracePage>(new[]
            {
                Page(through: 10, nextAfter: 5, recordPosition: 5),
                Page(through: 10, nextAfter: null, recordPosition: 10),
                Page(through: 10, nextAfter: 5, recordPosition: 5)
            })
        };
        await using var viewModel = new CommandTraceViewModel(query);

        await viewModel.RefreshAsync();
        await viewModel.NextPageAsync();
        await viewModel.PreviousPageAsync();

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(10, viewModel.ThroughPosition);
        Assert.Equal(5, viewModel.Rows.Single().Position);
        Assert.Equal(new long[] { 0, 5, 0 }, query.Filters.Select(filter => filter.AfterPosition));
        Assert.All(query.Filters.Skip(1), filter => Assert.Equal(10, filter.ThroughPosition));
    }

    [Fact]
    public async Task V102_U06_PageNavigationCannotStartWhileAnotherQueryIsBusy()
    {
        var query = new BlockingTraceQuery();
        await using var viewModel = new CommandTraceViewModel(query);
        var refresh = viewModel.RefreshAsync();
        await query.WaitForCallAsync(1);

        await viewModel.NextPageAsync();

        Assert.Single(query.Filters);
        Assert.False(viewModel.CanNextPage);
        query.Complete(0, Page(through: 1, nextAfter: null, recordPosition: 1));
        await refresh;
    }

    [Fact]
    public async Task V102_U07_ExpiredStableUpperBoundClearsPageAndAsksForRefresh()
    {
        var query = new RecordingTraceQuery
        {
            Responses = new Queue<CommandTracePage>(new[]
            {
                Page(through: 10, nextAfter: 5, recordPosition: 5),
                Page(through: 11, nextAfter: null, recordPosition: 10)
            })
        };
        await using var viewModel = new CommandTraceViewModel(query);

        await viewModel.RefreshAsync();
        await viewModel.NextPageAsync();

        Assert.False(viewModel.HasCurrentPage);
        Assert.Empty(viewModel.Rows);
        Assert.Contains("过期", viewModel.StatusMessage);
        Assert.Contains("TraceQuerySnapshotExpired", viewModel.ErrorMessage);
    }

    private static CommandTracePage Page(long through, long? nextAfter, long recordPosition) =>
        new(new ReadOnlyCollection<CommandTraceRecord>(new[] { Record(recordPosition) }), through, nextAfter);

    private static CommandTraceRecord Record(long position) =>
        new(position, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 1,
            DateTimeOffset.UtcNow, "system", null, AuditedCommandKind.ArmProduction,
            CommandSource.PhysicalConsole, "claimed", null, null, CommandAuditPhase.Outcome,
            CommandDisposition.Accepted, "Admitted");

    private sealed class RecordingTraceQuery : ICommandTraceQuery
    {
        public Queue<CommandTracePage> Responses { get; set; } = new();
        public List<CommandTraceFilter> Filters { get; } = new();
        public Exception? Exception { get; set; }

        public ValueTask<CommandTracePage> QueryAsync(CommandTraceFilter filter,
            CancellationToken cancellationToken = default)
        {
            Filters.Add(filter);
            if (Exception is not null) throw Exception;
            return ValueTask.FromResult(Responses.Dequeue());
        }
    }

    private sealed class BlockingTraceQuery : ICommandTraceQuery
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<CommandTracePage>> _pending = new();
        private readonly TaskCompletionSource<bool> _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<CommandTraceFilter> Filters { get; } = new();

        public ValueTask<CommandTracePage> QueryAsync(CommandTraceFilter filter,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<CommandTracePage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                Filters.Add(filter);
                _pending.Add(completion);
                if (_pending.Count >= 1) _firstCall.TrySetResult(true);
            }
            return new ValueTask<CommandTracePage>(completion.Task);
        }

        public async Task WaitForCallAsync(int count)
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_pending.Count >= count) return;
                }

                await Task.Delay(1);
            }
        }

        public void Complete(int index, CommandTracePage page)
        {
            lock (_sync) _pending[index].TrySetResult(page);
        }
    }
}
