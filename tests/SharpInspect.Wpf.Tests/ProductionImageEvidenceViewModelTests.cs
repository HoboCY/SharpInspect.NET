using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class ProductionImageEvidenceViewModelTests
{
    [Theory]
    [Trait("VerificationId", "V151_W01")]
    [InlineData(ProductionImageFinalizationState.Pending, "待生成")]
    [InlineData(ProductionImageFinalizationState.Failed, "生成失败")]
    [InlineData(ProductionImageFinalizationState.Succeeded, "已持久完成")]
    public async Task V151_W01_PersistentStatesAndCanonicalIdentityRemainDistinct(ProductionImageFinalizationState state, string label)
    {
        var record = Record(1, state);
        await using var model = new ProductionImageEvidenceViewModel(new Query(_ => Task.FromResult(Page(record))),
            new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Single(model.Rows);
        Assert.Same(record, model.SelectedRecord);
        Assert.Contains(label, model.SelectionSummary);
        Assert.Contains("有效位 12", model.SelectionSummary);
        Assert.Equal(record.Identity.CanonicalPixelHash, model.ContentHash);
        if (state == ProductionImageFinalizationState.Succeeded) Assert.Contains(".png", model.FinalFileSummary);
        if (state == ProductionImageFinalizationState.Failed) Assert.Contains("Fixture.EncodingFailed", model.FinalFileSummary);
        Assert.DoesNotContain("Fixture.EncodingFailed", model.SelectionSummary);
    }

    [Fact]
    [Trait("VerificationId", "V151_W02")]
    public async Task V151_W02_PageNavigationKeepsCoreWatermarkAndReplacesRows()
    {
        var first = Record(1);
        var second = Record(2);
        var query = new Query(filter => Task.FromResult(filter.AfterPosition == 0
            ? new ProductionImageEvidencePage(true, "Available", new[] { first }, 2, 1, 20)
            : new ProductionImageEvidencePage(true, "Available", new[] { second }, 2, null, 22)));
        await using var model = new ProductionImageEvidenceViewModel(query, new InlineUiDispatcher(), 1);
        await model.RefreshAsync();
        Assert.True(model.HasNextPage);
        await model.NextPageAsync();
        Assert.Single(model.Rows);
        Assert.Same(second, model.SelectedRecord);
        Assert.Equal(2, query.LastFilter!.ThroughPosition);
        Assert.Equal(1, query.LastFilter.AfterPosition);
        Assert.False(model.HasNextPage);
        Assert.Contains("22", model.StatusMessage);
    }

    [Theory]
    [Trait("VerificationId", "V151_W03")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V151_W03_FilterOrPrivacyDeactivationDiscardsLateResponse(bool deactivate)
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ProductionImageEvidencePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(_ => { entered.TrySetResult(null); return response.Task; });
        await using var model = new ProductionImageEvidenceViewModel(query, new InlineUiDispatcher());
        var loading = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (deactivate) model.Deactivate();
            else model.InspectionText = Guid.NewGuid().ToString("D");
            response.TrySetResult(Page(Record(1, ProductionImageFinalizationState.Succeeded)));
            await loading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(model.Rows);
            Assert.Null(model.SelectedRecord);
            Assert.Empty(model.ContentHash);
            Assert.False(model.HasNextPage);
        }
        finally { response.TrySetResult(new(false, "Unavailable", Array.Empty<ProductionImageEvidenceRecord>(), 0, null, 0)); }
    }

    [Fact]
    [Trait("VerificationId", "V151_W04")]
    public async Task V151_W04_UnavailableOrContradictoryPageDoesNotLeakRowsOrRawReason()
    {
        var record = Record(1);
        var pages = new[]
        {
            new ProductionImageEvidencePage(false, "private-detail", new[] { record }, 1, null, 20),
            new ProductionImageEvidencePage(true, "Available", new[] { record, record }, 1, null, 20),
            new ProductionImageEvidencePage(true, "Available", new[] { record }, 0, null, 20),
            new ProductionImageEvidencePage(true, "Available", new[] { record }, 1, 0, 20)
        };
        foreach (var page in pages)
        {
            await using var model = new ProductionImageEvidenceViewModel(new Query(_ => Task.FromResult(page)),
                new InlineUiDispatcher());
            await model.RefreshAsync();
            Assert.Empty(model.Rows);
            Assert.Null(model.SelectedRecord);
            Assert.Contains("不可用", model.StatusMessage);
            Assert.DoesNotContain("private", model.StatusMessage);
        }
    }

    [Fact]
    [Trait("VerificationId", "V151_W05")]
    public async Task V151_W05_MissingQueryAndInvalidFilterDoNotInventEvidence()
    {
        await using var missing = new ProductionImageEvidenceViewModel(null, new InlineUiDispatcher());
        await missing.RefreshAsync();
        Assert.Contains("未配置", missing.StatusMessage);
        Assert.False(missing.RefreshCommand.CanExecute(null));
        var query = new Query(_ => Task.FromResult(Page(Record(1))));
        await using var model = new ProductionImageEvidenceViewModel(query, new InlineUiDispatcher());
        model.InspectionText = "invalid-inspection";
        await model.RefreshAsync();
        Assert.Equal(0, query.Count);
        Assert.Empty(model.Rows);
    }

    [Fact]
    [Trait("VerificationId", "V151_W06")]
    public async Task V151_W06_RefreshAndDisposeDoNotCreateParallelQueriesOrLateSelection()
    {
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ProductionImageEvidencePage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(_ => { entered.TrySetResult(null); return response.Task; });
        await using var model = new ProductionImageEvidenceViewModel(query, new InlineUiDispatcher());
        var loading = model.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await model.RefreshAsync();
            Assert.Equal(1, query.Count);
            await model.DisposeAsync();
            response.TrySetResult(Page(Record(1)));
            await loading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(model.Rows);
            Assert.False(model.RefreshCommand.CanExecute(null));
            Assert.Contains("已关闭", model.StatusMessage);
        }
        finally { response.TrySetResult(new(false, "Unavailable", Array.Empty<ProductionImageEvidenceRecord>(), 0, null, 0)); }
    }

    private static ProductionImageEvidenceRecord Record(long position,
        ProductionImageFinalizationState state = ProductionImageFinalizationState.Pending)
    {
        var inspection = Guid.NewGuid();
        var manifest = Guid.NewGuid();
        var work = Guid.NewGuid();
        var hash = new string('A', 64);
        var identity = new ProductionImageCoreImageIdentity(inspection, manifest, work, Guid.NewGuid(), hash,
            3, 2, VisionPixelFormat.Mono16, 12, 44, hash, DateTimeOffset.UtcNow, hash, hash);
        var success = state == ProductionImageFinalizationState.Succeeded
            ? new ProductionImageSuccessDescriptor(hash, manifest.ToString("N") + ".png", 88, 3, 2,
                VisionPixelFormat.Mono16, 12, CanonicalImagePixelContent.HashScheme,
                CanonicalImagePixelContent.HashSchemeVersion, hash) : null;
        var failed = state == ProductionImageFinalizationState.Failed;
        var projection = new ProductionImageFinalizationWorkState(work, manifest, inspection, hash, hash, state,
            ProductionImageCleanupState.Pending, 1, 2, true, failed ? "Fixture.EncodingFailed" : null,
            failed ? ProductionImageFailureCategory.Temporary : null, failed ? DateTimeOffset.UtcNow : null,
            success, 1, hash);
        return new(identity, projection, Array.Empty<ProductionImageFinalizationEvent>(), position);
    }
    private static ProductionImageEvidencePage Page(ProductionImageEvidenceRecord record) =>
        new(true, "Available", new[] { record }, record.Position, null, 20);
    private sealed class Query : IProductionImageEvidenceQuery
    {
        private readonly Func<ProductionImageEvidenceFilter, Task<ProductionImageEvidencePage>> _handler;
        internal Query(Func<ProductionImageEvidenceFilter, Task<ProductionImageEvidencePage>> handler) => _handler = handler;
        internal int Count { get; private set; }
        internal ProductionImageEvidenceFilter? LastFilter { get; private set; }
        public async ValueTask<ProductionImageEvidencePage> QueryAsync(ProductionImageEvidenceFilter filter,
            CancellationToken cancellationToken = default)
        {
            Count++;
            LastFilter = filter;
            return await _handler(filter);
        }
        public ValueTask<ProductionImageWorkQueuePage> ReadWorkQueueAsync(int pageSize = 128,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ImageBacklogSnapshot> ReadBacklogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
