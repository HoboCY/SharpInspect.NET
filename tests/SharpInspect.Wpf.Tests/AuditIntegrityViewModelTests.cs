using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class AuditIntegrityViewModelTests
{
    [Fact]
    public async Task V103_U07_FaultOnNextSegmentKeepsIntegrityReasonAndClearsPriorSuccess()
    {
        var query = new RecordingIntegrityQuery();
        query.Responses.Enqueue(Report(AuditIntegrityState.Verified, through: 500, verifiedFrom: 1, verifiedThrough: 200));
        query.Responses.Enqueue(Report(AuditIntegrityState.Faulted, through: 0, verifiedFrom: 0, verifiedThrough: 0)
            with { ReasonCode = "AuditPayloadHashMismatch" });
        await using var viewModel = new AuditIntegrityViewModel(query);
        await viewModel.RefreshAsync();
        await viewModel.NextSegmentAsync();
        Assert.False(viewModel.IsVerified);
        Assert.Equal("AuditPayloadHashMismatch", viewModel.ReasonCode);
        Assert.Equal(0, viewModel.VerifiedThroughSequence);
        Assert.False(viewModel.CanNextSegment);
    }

    [Fact]
    public async Task V103_U01_RefreshUsesFixedBoundAndShowsBoundedVerifiedRange()
    {
        var query = new RecordingIntegrityQuery();
        query.Responses.Enqueue(Report(AuditIntegrityState.Verified, through: 500,
            verifiedFrom: 1, verifiedThrough: 200, checkpoint: 200, anchor: 180));
        await using var viewModel = new AuditIntegrityViewModel(query);

        await viewModel.RefreshAsync();

        var request = Assert.Single(query.Requests);
        Assert.Equal(0, request.AfterSequence);
        Assert.Equal(AuditIntegrityViewModel.MaximumEntries, request.MaximumEntries);
        Assert.Equal(AuditIntegrityState.Verified, viewModel.State);
        Assert.True(viewModel.IsVerified);
        Assert.True(viewModel.HasReport);
        Assert.Equal(500, viewModel.ThroughSequence);
        Assert.Equal(1, viewModel.VerifiedFromSequence);
        Assert.Equal(200, viewModel.VerifiedThroughSequence);
        Assert.Equal(200, viewModel.CheckpointSequence);
        Assert.Equal(180, viewModel.AnchoredSequence);
        Assert.True(viewModel.CanNextSegment);
        Assert.Contains("有界", viewModel.StatusMessage);
        Assert.Contains("1–200 / 总上界 500", viewModel.SummaryText);
        Assert.Contains("部分验证不授予 Ready", viewModel.LimitationNotice);
    }

    [Fact]
    public async Task V103_U02_NextSegmentKeepsCapturedUpperBoundAndAdvancesVerifiedCursor()
    {
        var query = new RecordingIntegrityQuery();
        query.Responses.Enqueue(Report(AuditIntegrityState.Verified, through: 500,
            verifiedFrom: 1, verifiedThrough: 200));
        query.Responses.Enqueue(Report(AuditIntegrityState.Verified, through: 500,
            verifiedFrom: 201, verifiedThrough: 400, checkpoint: 400, anchor: 399));
        await using var viewModel = new AuditIntegrityViewModel(query);

        await viewModel.RefreshAsync();
        await viewModel.NextSegmentAsync();

        Assert.Collection(query.Requests,
            first =>
            {
                Assert.Equal(0, first.AfterSequence);
                Assert.Equal(200, first.MaximumEntries);
            },
            second =>
            {
                Assert.Equal(200, second.AfterSequence);
                Assert.Equal(200, second.MaximumEntries);
            });
        Assert.Equal(500, viewModel.ThroughSequence);
        Assert.Equal(1, viewModel.VerifiedFromSequence);
        Assert.Equal(400, viewModel.VerifiedThroughSequence);
        Assert.Equal(2, viewModel.SegmentCount);
        Assert.True(viewModel.CanNextSegment);
        Assert.Contains("1–400 / 总上界 500", viewModel.SummaryText);
    }

    [Fact]
    public async Task V103_U03_QueryFailureClearsPreviousVerifiedReport()
    {
        var query = new RecordingIntegrityQuery();
        query.Responses.Enqueue(Report(AuditIntegrityState.Verified, through: 12,
            verifiedFrom: 1, verifiedThrough: 12));
        await using var viewModel = new AuditIntegrityViewModel(query);

        await viewModel.RefreshAsync();
        query.Exception = new InvalidOperationException("store details must not escape");
        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasReport);
        Assert.False(viewModel.IsVerified);
        Assert.Equal(AuditIntegrityState.Faulted, viewModel.State);
        Assert.Null(viewModel.ThroughSequence);
        Assert.Null(viewModel.VerifiedThroughSequence);
        Assert.False(viewModel.CanNextSegment);
        Assert.Contains("AuditIntegrityQueryFailed", viewModel.ErrorMessage);
        Assert.DoesNotContain("store details must not escape", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task V103_U04_LateOlderResponseCannotOverrideNewerRefresh()
    {
        var query = new BlockingIntegrityQuery();
        await using var viewModel = new AuditIntegrityViewModel(query);

        var firstRefresh = viewModel.RefreshAsync();
        await query.WaitForCallAsync(1);
        var secondRefresh = viewModel.RefreshAsync();
        await query.WaitForCallAsync(2);

        query.Complete(1, Report(AuditIntegrityState.Verified, through: 20,
            verifiedFrom: 11, verifiedThrough: 20));
        query.Complete(0, Report(AuditIntegrityState.Faulted, through: 10,
            verifiedFrom: 1, verifiedThrough: 2, reasonCode: "OlderFault"));
        await secondRefresh;
        await firstRefresh;

        Assert.Equal(AuditIntegrityState.Verified, viewModel.State);
        Assert.True(viewModel.HasReport);
        Assert.Equal(20, viewModel.ThroughSequence);
        Assert.Equal(20, viewModel.VerifiedThroughSequence);
        Assert.DoesNotContain("OlderFault", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V103_U05_CancelledVerificationClearsCurrentStateAndCanRetry()
    {
        var query = new BlockingIntegrityQuery();
        await using var viewModel = new AuditIntegrityViewModel(query);
        using var cancellation = new CancellationTokenSource();

        var refresh = viewModel.RefreshAsync(cancellation.Token);
        await query.WaitForCallAsync(1);
        cancellation.Cancel();
        query.Complete(0, Report(AuditIntegrityState.Verified, through: 9,
            verifiedFrom: 1, verifiedThrough: 9));
        await refresh;

        Assert.False(viewModel.HasReport);
        Assert.Equal(AuditIntegrityState.NotConfigured, viewModel.State);
        Assert.Contains("取消", viewModel.StatusMessage);

        query.ImmediateResponses.Enqueue(Report(AuditIntegrityState.Verified, through: 9,
            verifiedFrom: 1, verifiedThrough: 9));
        await viewModel.RefreshAsync();
        Assert.True(viewModel.IsVerified);
    }

    [Fact]
    public async Task V103_U06_MissingQueryIsExplicitlyNotConfigured()
    {
        await using var viewModel = new AuditIntegrityViewModel(null);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsConfigured);
        Assert.Equal(AuditIntegrityState.NotConfigured, viewModel.State);
        Assert.False(viewModel.CanRefresh);
        Assert.False(viewModel.CanNextSegment);
        Assert.Contains("未配置", viewModel.StatusMessage);
        Assert.Contains("未配置", viewModel.StateLabel);
    }

    private static AuditIntegrityReport Report(AuditIntegrityState state, long through,
        long verifiedFrom, long verifiedThrough, long? checkpoint = null, long? anchor = null,
        string reasonCode = "AuditIntegrityVerified") =>
        new(state, reasonCode, "station-01", "policy-v1", through, verifiedFrom,
            verifiedThrough, checkpoint, anchor, DateTimeOffset.UtcNow);

    private sealed class RecordingIntegrityQuery : IAuditIntegrityQuery
    {
        public Queue<AuditIntegrityReport> Responses { get; } = new();
        public Queue<AuditIntegrityReport> ImmediateResponses { get; } = new();
        public List<AuditVerificationRequest> Requests { get; } = new();
        public Exception? Exception { get; set; }

        public ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Exception is not null) throw Exception;
            if (ImmediateResponses.Count != 0) return ValueTask.FromResult(ImmediateResponses.Dequeue());
            return ValueTask.FromResult(Responses.Dequeue());
        }
    }

    private sealed class BlockingIntegrityQuery : IAuditIntegrityQuery
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource<AuditIntegrityReport>> _pending = new();
        public List<AuditVerificationRequest> Requests { get; } = new();
        public Queue<AuditIntegrityReport> ImmediateResponses { get; } = new();

        public ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<AuditIntegrityReport>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                Requests.Add(request);
                if (ImmediateResponses.Count != 0)
                    return ValueTask.FromResult(ImmediateResponses.Dequeue());
                _pending.Add(completion);
            }
            return new ValueTask<AuditIntegrityReport>(completion.Task);
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

        public void Complete(int index, AuditIntegrityReport report)
        {
            lock (_sync) _pending[index].TrySetResult(report);
        }
    }
}
