using System.Security.Cryptography;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed partial class ProductionOutboxViewModelTests
{
    [Fact]
    [Trait("VerificationId", "V153_W01")]
    public async Task V153_W01_PermanentRequiredItemKeepsIdentityAndAttemptsVisible()
    {
        var row = Row(1, permanent: true);
        await using var model = new ProductionOutboxViewModel(new Query(_ => Task.FromResult(Page(row))),
            new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Same(row, Assert.Single(model.Rows));
        Assert.Same(row, model.SelectedItem);
        Assert.Contains("永久阻塞", model.SelectionSummary);
        Assert.Contains("已尝试 2 次", model.SelectionSummary);
        Assert.Contains("必需路由", model.SelectionSummary);
        Assert.Equal(row.Delivery.Payload!.ContentHash, model.PayloadHash);
        Assert.DoesNotContain("已确认送达", model.SelectionSummary);
    }

    [Fact]
    [Trait("VerificationId", "V153_W02")]
    public async Task V153_W02_PagingReplacesRowsAndUsesPersistedCursor()
    {
        var first = Row(1);
        var second = Row(2);
        var query = new Query(after => Task.FromResult(after == 0
            ? new OutboxPendingPage(true, "Available", new[] { first }, true, 1, new(20, Array.Empty<OutboxRouteBacklog>()))
            : Page(second)));
        await using var model = new ProductionOutboxViewModel(query, new InlineUiDispatcher(), 1);
        await model.RefreshAsync();
        Assert.True(model.NextPageCommand.CanExecute(null));
        await model.NextPageAsync();
        Assert.Equal(1, query.LastAfter);
        Assert.Same(second, Assert.Single(model.Rows));
        Assert.False(model.NextPageCommand.CanExecute(null));
    }

    [Fact]
    [Trait("VerificationId", "V153_W03")]
    public async Task V153_W03_LateQueryCannotRestorePrivateRowsAfterDeactivation()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<OutboxPendingPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(_ => { entered.TrySetResult(true); return response.Task; });
        await using var model = new ProductionOutboxViewModel(query, new InlineUiDispatcher());
        var loading = model.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        model.Deactivate();
        response.TrySetResult(Page(Row(1)));
        await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(model.Rows);
        Assert.Empty(model.Events);
        Assert.Null(model.SelectedItem);
        Assert.Empty(model.PayloadHash);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [Trait("VerificationId", "V153_W04")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task V153_W04_InvalidOrUnavailablePageNeverDisplaysRowsOrRawError(int scenario)
    {
        var row = Row(1);
        var page = scenario switch
        {
            0 => new OutboxPendingPage(false, "private-service-detail", new[] { row }, false, 1, new(20, Array.Empty<OutboxRouteBacklog>())),
            1 => new OutboxPendingPage(true, "Available", new[] { row, row }, false, 1, new(20, Array.Empty<OutboxRouteBacklog>())),
            2 => new OutboxPendingPage(true, "Available", new[] { row }, true, 0, new(20, Array.Empty<OutboxRouteBacklog>())),
            _ => new OutboxPendingPage(true, "Available", new[] { row }, false, 1, null)
        };
        await using var model = new ProductionOutboxViewModel(new Query(_ => Task.FromResult(page)),
            new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Empty(model.Rows);
        Assert.Null(model.SelectedItem);
        Assert.Contains("不可用", model.StatusMessage);
        Assert.DoesNotContain("private", model.StatusMessage);
    }

    [Theory]
    [Trait("VerificationId", "V153_W05")]
    [InlineData(OutboxFailureCategory.Transient)]
    [InlineData(OutboxFailureCategory.UnknownOutcome)]
    [InlineData(OutboxFailureCategory.Permanent)]
    public async Task V153_W05_HistoryPreservesDistinctFailureCategory(OutboxFailureCategory category)
    {
        var row = Row(1);
        var fact = Event(row, category);
        var query = new Query(_ => Task.FromResult(Page(row)), (_, _) => Task.FromResult(
            new OutboxHistoryPage(true, "Available", new[] { fact }, false, 1)));
        await using var model = new ProductionOutboxViewModel(query, new InlineUiDispatcher());
        await model.RefreshAsync();
        await model.ReadHistoryAsync();
        Assert.Equal(category, Assert.Single(model.Events).FailureCategory);
        Assert.Equal(row.Delivery.DeliveryId, query.LastDelivery);
    }

    [Fact]
    [Trait("VerificationId", "V153_W06")]
    public async Task V153_W06_HistoryForAnotherDeliveryIsRejected()
    {
        var row = Row(1);
        var query = new Query(_ => Task.FromResult(Page(row)), (_, _) => Task.FromResult(
            new OutboxHistoryPage(true, "Available", new[] { Event(Row(2), OutboxFailureCategory.Permanent) }, false, 1)));
        await using var model = new ProductionOutboxViewModel(query, new InlineUiDispatcher());
        await model.RefreshAsync();
        await model.ReadHistoryAsync();
        Assert.Empty(model.Events);
        Assert.Contains("不可用", model.StatusMessage);
        Assert.Same(row, model.SelectedItem);
    }

    [Fact]
    [Trait("VerificationId", "V153_W07")]
    public async Task V153_W07_MissingConfigurationKeepsCommandsUnavailable()
    {
        await using var model = new ProductionOutboxViewModel(null, new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Empty(model.Rows);
        Assert.Contains("未配置", model.StatusMessage);
        Assert.False(model.RefreshCommand.CanExecute(null));
        Assert.False(model.ReadHistoryCommand.CanExecute(null));
    }

    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    [Trait("VerificationId", "V153_W08")]
    public async Task V153_W08_DeliveryHistoryRemainsQueryableWithoutPendingRow()
    {
        var retired = Row(1);
        var query = new Query(_ => Task.FromResult(new OutboxPendingPage(true, "Available",
            Array.Empty<OutboxPendingItem>(), false, 0, new(20, Array.Empty<OutboxRouteBacklog>()))),
            (_, _) => Task.FromResult(new OutboxHistoryPage(true, "Available",
                new[] { Event(retired, OutboxFailureCategory.UnknownOutcome) }, false, 1)));
        await using var model = new ProductionOutboxViewModel(query, new InlineUiDispatcher());
        await model.RefreshAsync();
        Assert.Empty(model.Rows);
        model.HistoryDeliveryText = "invalid-id";
        await model.ReadHistoryAsync();
        Assert.Null(query.LastDelivery);
        model.HistoryDeliveryText = retired.Delivery.DeliveryId.ToString("D");
        Assert.True(model.ReadHistoryCommand.CanExecute(null));
        await model.ReadHistoryAsync();
        Assert.Equal(retired.Delivery.DeliveryId, Assert.Single(model.Events).DeliveryId);
    }

    [Fact]
    [Trait("VerificationId", "V153_W09")]
    public async Task V153_W09_ChangingHistoryIdentityDiscardsLateResponse()
    {
        var first = Row(1);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<OutboxHistoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new Query(_ => Task.FromResult(Page(first)), (_, _) =>
        { entered.TrySetResult(true); return response.Task; });
        await using var model = new ProductionOutboxViewModel(query, new InlineUiDispatcher());
        await model.RefreshAsync();
        var loading = model.ReadHistoryAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        model.HistoryDeliveryText = Guid.NewGuid().ToString("D");
        response.TrySetResult(new(true, "Available", new[] { Event(first, OutboxFailureCategory.Permanent) }, false, 1));
        await loading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(model.Events);
        Assert.Null(model.SelectedItem);
        Assert.Empty(model.PayloadHash);
        Assert.False(model.NextHistoryPageCommand.CanExecute(null));
    }

    private static OutboxPendingItem Row(long position, bool permanent = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var contract = new OutboxContractReference("fixture", "1", Hash);
        var route = new OutboxRouteDefinition("fixture-route", "1", OutboxRouteCriticality.Required,
            "fixture-destination", contract, "application/json", contract,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), contract, 1024);
        var delivery = new OutboxDelivery(Guid.NewGuid(), Guid.NewGuid(), Hash, route,
            new OutboxPayloadSnapshot(contract, "application/json", new byte[] { 123, 125 }), null,
            DateTimeOffset.UtcNow, 5);
        return new(delivery, OutboxDeliveryState.Failed, 2, null, null, !permanent, null,
            permanent, "FixtureFailure", position, Hash);
    }
    private static OutboxPendingPage Page(OutboxPendingItem row) => new(true, "Available", new[] { row },
        false, row.Position, new(20, Array.Empty<OutboxRouteBacklog>()));
    private static ProductionOutboxEvent Event(OutboxPendingItem item, OutboxFailureCategory category) =>
        new(position: 1, eventId: Guid.NewGuid(), deliveryId: item.Delivery.DeliveryId,
            inspectionId: item.Delivery.InspectionId, coreHash: Hash, routeSetHash: Hash,
            routeId: item.Delivery.Route.RouteId, routeVersion: "1", routeContentHash: item.Delivery.Route.ContentHash,
            aggregateSequence: 2, kind: OutboxEventKind.AttemptFailed, attemptId: Guid.NewGuid(), attemptNumber: 2,
            runtimeEpoch: Guid.NewGuid(), recordedAtUtc: DateTimeOffset.UtcNow, reasonCode: "FixtureFailure",
            failureCategory: category, retryAfterUtc: null, connectionBindingHash: Hash, attemptBudget: 5,
            receiptId: null, receiptHash: null, acceptedAtUtc: null, receipt: Array.Empty<byte>(),
            payloadHash: item.Delivery.Payload!.ContentHash, contentHash: Hash, auditSequence: 20, auditHash: Hash);

    private sealed class Query : IProductionOutboxQuery
    {
        private readonly Func<long, Task<OutboxPendingPage>> _pending;
        private readonly Func<Guid?, long, Task<OutboxHistoryPage>> _history;
        internal Query(Func<long, Task<OutboxPendingPage>> pending,
            Func<Guid?, long, Task<OutboxHistoryPage>>? history = null)
        {
            _pending = pending;
            _history = history ?? ((_, _) => Task.FromResult(new OutboxHistoryPage(true, "Available",
                Array.Empty<ProductionOutboxEvent>(), false, 0)));
        }
        internal long LastAfter { get; private set; }
        internal Guid? LastDelivery { get; private set; }
        public async ValueTask<OutboxPendingPage> ReadPendingAsync(long afterPosition = 0, int? pageSize = null,
            CancellationToken cancellationToken = default)
        { LastAfter = afterPosition; return await _pending(afterPosition); }
        public ValueTask<OutboxBacklogSnapshot> ReadBacklogAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OutboxBacklogSnapshot(20, Array.Empty<OutboxRouteBacklog>()));
        public async ValueTask<OutboxHistoryPage> ReadHistoryAsync(Guid? inspectionId = null, Guid? deliveryId = null,
            long afterPosition = 0, int? pageSize = null, CancellationToken cancellationToken = default)
        { LastDelivery = deliveryId; return await _history(deliveryId, afterPosition); }
    }
}
