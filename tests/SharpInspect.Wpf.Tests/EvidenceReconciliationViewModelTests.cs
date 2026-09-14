using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class EvidenceReconciliationViewModelTests
{
    private static readonly string Hash = new('A', 64);
    private static readonly DateTimeOffset Time = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact, Trait("VerificationId", "V154_W01")]
    public async Task V154_W01_BoundedPagesReplaceRowsAndKeepDeferredWorkSeparate()
    {
        var first = Record(1); var second = Record(2);
        var query = new Query(filter => Task.FromResult(Page(filter.AfterPosition == 0 ? first : second,
            more: filter.AfterPosition == 0)));
        await using var model = new EvidenceReconciliationViewModel(query, new InlineUiDispatcher(), 1);
        await model.RefreshAsync();
        Assert.Single(model.Rows);
        Assert.True(model.HasNextPage);
        Assert.Contains("已核验 0", model.ScrubberSummary);
        Assert.Contains("延期 1", model.ScrubberSummary);
        await model.NextPageAsync();
        Assert.Single(model.Rows);
        Assert.Same(second, model.SelectedRecord);
        Assert.Equal(1, query.Last!.AfterPosition);
        Assert.Equal(1, query.Last.PageSize);
        Assert.False(model.HasNextPage);
        Assert.Contains(second.ContentHash, model.SelectionSummary);
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V154_W02")]
    public async Task V154_W02_DeactivationOrDisposalDiscardsUncancellableLateEvidence(bool dispose)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<EvidenceReconciliationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(_ => { entered.SetResult(true); return response.Task; });
        await using var model = new EvidenceReconciliationViewModel(query, new InlineUiDispatcher());
        var loading = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (dispose) await model.DisposeAsync(); else model.Deactivate();
            response.SetResult(Page(Record(1)));
            await loading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(model.Rows);
            Assert.Null(model.SelectedRecord);
            Assert.False(model.HasNextPage);
            Assert.DoesNotContain(Hash, model.SelectionSummary);
            Assert.DoesNotContain("已完成", model.StartupSummary);
            Assert.False(model.IsBusy);
        }
        finally { response.TrySetResult(new(false, "private-error", 0, null, 0, false, null, null, 0, false)); }
    }

    [Theory]
    [InlineData("unavailable"), InlineData("duplicate"), InlineData("enum"), InlineData("cursor"),
     InlineData("orphan-reference"), InlineData("counter-overflow"), InlineData("missing-subject")]
    [Trait("VerificationId", "V154_W03")]
    public async Task V154_W03_ContradictoryPagesCannotBecomeDisplayedEvidence(string invalid)
    {
        var record = Record(1);
        var progress = Progress();
        IReadOnlyList<EvidenceReconciliationRecord> records = new[] { record };
        if (invalid == "duplicate") records = new[] { record, record };
        if (invalid == "enum") records = new[] { record with { Kind = (EvidenceReconciliationEventKind)255 } };
        if (invalid == "missing-subject") records = new[] { record with { Subject = null } };
        if (invalid == "orphan-reference")
            records = new[] { record with { Kind = EvidenceReconciliationEventKind.IntegrityFault,
                Subject = record.Subject! with { Kind = EvidenceReconciliationSubjectKind.Orphan, OrphanId = Guid.NewGuid() } } };
        if (invalid == "counter-overflow")
            progress = progress with { ScannedItems = 1, VerifiedItems = long.MaxValue, DeferredItems = long.MaxValue };
        var snapshot = new EvidenceReconciliationSnapshot(invalid != "unavailable", "private-error", 20, records,
            invalid == "cursor" ? 100 : 1, false, null, progress, 0, false);
        await using var model = new EvidenceReconciliationViewModel(new Query(_ => Task.FromResult(snapshot)), new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Empty(model.Rows);
        Assert.Null(model.SelectedRecord);
        Assert.Contains("不可用", model.StatusMessage);
        Assert.DoesNotContain("private", model.StatusMessage);
        Assert.DoesNotContain(Hash, model.SelectionSummary);
    }

    [Fact, Trait("VerificationId", "V154_W04")]
    public async Task V154_W04_LatchedFaultAndPendingQuarantineAreReadOnlyAndClearedOnLeave()
    {
        var row = Record(1) with { Kind = EvidenceReconciliationEventKind.IntegrityFault };
        var snapshot = new EvidenceReconciliationSnapshot(true, "Available", 20, new[] { row }, 1, false,
            null, Progress() with { IntegrityFault = true }, 1, true);
        await using var model = new EvidenceReconciliationViewModel(new Query(_ => Task.FromResult(snapshot)), new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Contains("完整性故障", model.IntegritySummary);
        Assert.Contains("故障", model.ScrubberSummary);
        model.Deactivate();
        Assert.Empty(model.Rows);
        Assert.DoesNotContain("已记录完整性故障", model.IntegritySummary);
        await using var missing = new EvidenceReconciliationViewModel(null, new InlineUiDispatcher());
        await missing.RefreshAsync();
        Assert.False(missing.RefreshCommand.CanExecute(null));
        Assert.Contains("未配置", missing.StatusMessage);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceReconciliationViewModel(null, new InlineUiDispatcher(), 101));
    }

    private static EvidenceReconciliationProgress Progress() => new(Guid.NewGuid(), EvidenceReconciliationPhase.HistoricalScrub,
        2, 1, 1, 0, 1, 0, false, false, 1, Hash, Time);
    private static EvidenceReconciliationRecord Record(long position) => new(position, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), EvidenceReconciliationPhase.HistoricalScrub, EvidenceReconciliationEventKind.WorkDeferred,
        new(EvidenceReconciliationSubjectKind.Outbox, position, Guid.NewGuid(), null, null, Guid.NewGuid(),
            null, Hash, Hash, null, null, null, null, null, null, null, null), Time, "ActiveWorkDeferred", position + 10, Hash);
    private static EvidenceReconciliationSnapshot Page(EvidenceReconciliationRecord row, bool more = false) =>
        new(true, "Available", 20, new[] { row }, row.Position, more, null, Progress(), 0, false);
    private sealed class Query : IEvidenceReconciliationQuery
    {
        private readonly Func<EvidenceReconciliationFilter, Task<EvidenceReconciliationSnapshot>> _read;
        internal Query(Func<EvidenceReconciliationFilter, Task<EvidenceReconciliationSnapshot>> read) => _read = read;
        internal EvidenceReconciliationFilter? Last { get; private set; }
        public async ValueTask<EvidenceReconciliationSnapshot> ReadAsync(EvidenceReconciliationFilter filter,
            CancellationToken cancellationToken = default) { Last = filter; return await _read(filter); }
    }
}
