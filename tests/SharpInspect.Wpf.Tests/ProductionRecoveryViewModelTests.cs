using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class ProductionRecoveryViewModelTests
{
    [Fact]
    public async Task V144_W01_RecoveryBindsExactPendingEventAndFreshStepUpTarget()
    {
        var session = AuthenticatedSession();
        var pending = PendingItem();
        var query = new FakeHistory(pending);
        var runtime = new FakeRuntime();
        var stepUp = new FakeStepUp();
        var sessions = new FakeSessions(session);
        await using var model = new ProductionRecoveryViewModel(query, runtime, sessions,
            stepUp, new InlineDispatcher());

        await model.RefreshAsync();
        model.SelectedItem = pending;
        model.SelectedDisposition = PartDisposition.Isolated;

        var outcome = await model.RecoverAsync("controller acknowledged late", "hold for review",
            PartDisposition.Isolated, "one-time-password");

        Assert.Equal(CommandDisposition.Accepted, outcome?.Disposition);
        var command = Assert.IsType<ManualProductionRecoveryCommand>(runtime.LastCommand);
        Assert.Equal(pending.InspectionId, command.InspectionId);
        Assert.Equal(pending.EventHash, command.ExpectedEventHash);
        Assert.Equal("controller acknowledged late", command.ReasonCode);
        Assert.Equal("hold for review", command.DispositionNote);
        Assert.Equal(PartDisposition.Isolated, command.Disposition);
        Assert.Equal(command.AuthorizationTarget, stepUp.LastRequest?.Binding.TargetId);
        Assert.Equal("one-time-password", stepUp.LastRequest?.Password);
        Assert.Equal(command.AuthorizationTarget,
            ManualProductionRecoveryCommand.ComputeAuthorizationTarget(command.CorrelationId,
                command.InspectionId, command.ExpectedEventHash, command.ReasonCode,
                command.Disposition, command.DispositionNote));
        Assert.True(model.IsStale);
        Assert.False(model.CanRecover);
    }

    [Fact]
    public async Task V144_W02_StaleRefreshCannotReplaceSelectionAfterSessionChange()
    {
        var session = AuthenticatedSession();
        var pending = PendingItem();
        var query = new BlockingHistory(pending);
        var sessions = new FakeSessions(session);
        await using var model = new ProductionRecoveryViewModel(query, new FakeRuntime(), sessions,
            new FakeStepUp(), new InlineDispatcher());

        await model.RefreshAsync();
        model.SelectedItem = pending;
        query.BlockNext();
        var refresh = model.RefreshAsync();
        await query.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        sessions.Publish(new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null));
        query.Release();
        await refresh;

        Assert.Empty(model.Pending);
        Assert.Null(model.SelectedItem);
        Assert.True(model.IsStale);
        Assert.False(model.CanRecover);
    }

    [Fact]
    public async Task V144_W03_LogoutClearsPendingSelectionAndDisablesCommandSurface()
    {
        var session = AuthenticatedSession();
        var pending = PendingItem();
        var sessions = new FakeSessions(session);
        await using var model = new ProductionRecoveryViewModel(new FakeHistory(pending),
            new FakeRuntime(), sessions, new FakeStepUp(), new InlineDispatcher());

        await model.RefreshAsync();
        model.SelectedItem = pending;
        model.SelectedDisposition = PartDisposition.Scrapped;
        Assert.True(model.Pending.Count == 1);
        sessions.Publish(new InteractiveSession(InteractiveSessionState.Locked, null, null));

        Assert.Empty(model.Pending);
        Assert.Null(model.SelectedItem);
        Assert.Null(model.SelectedDisposition);
        Assert.False(model.CanRefresh);
        Assert.False(model.CanRecover);
        Assert.Contains("会话", model.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V144_W04_CancelledRefreshCannotPublishFreshStateAfterDispatchWasQueued()
    {
        var pending = PendingItem();
        var dispatcher = new PausedDispatcher();
        await using var model = new ProductionRecoveryViewModel(new FakeHistory(pending),
            new FakeRuntime(), new FakeSessions(AuthenticatedSession()), new FakeStepUp(), dispatcher);

        var refresh = model.RefreshAsync();
        await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        model.CancelPendingOperations();
        dispatcher.Release.TrySetResult(true);
        await refresh;

        Assert.True(model.IsStale);
        Assert.Empty(model.Pending);
        Assert.False(model.CanRecover);
    }

    [Fact]
    public async Task V144_W05_UnavailableRefreshPreservesVisibleHistoryButDisablesRecovery()
    {
        var pending = PendingItem();
        var query = new FakeHistory(pending);
        await using var model = new ProductionRecoveryViewModel(query, new FakeRuntime(),
            new FakeSessions(AuthenticatedSession()), new FakeStepUp(), new InlineDispatcher());
        await model.RefreshAsync();
        model.SelectedItem = pending;
        model.SelectedDisposition = PartDisposition.Isolated;
        model.ReasonText = "physical disposition verified";
        Assert.True(model.CanRecover);
        query.Page = new(false, "ProductionRecoveryHistoryUnavailable", null, 0, null);

        await model.RefreshAsync();

        Assert.Same(pending, Assert.Single(model.Pending));
        Assert.Same(pending, model.SelectedItem);
        Assert.True(model.IsStale);
        Assert.False(model.CanRecover);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V144_W06_RecoveryPanelRendersWithAndWithoutConfiguration(bool configured)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            ProductionRecoveryViewModel? model = null;
            try
            {
                var item = PendingItem();
                model = configured ? new ProductionRecoveryViewModel(new FakeHistory(item),
                    new FakeRuntime(), new FakeSessions(AuthenticatedSession()), new FakeStepUp(),
                    new InlineDispatcher()) : new ProductionRecoveryViewModel();
                if (configured)
                {
                    model.RefreshAsync().GetAwaiter().GetResult();
                    model.SelectedItem = item;
                }
                var panel = new ProductionRecoveryPanel(model);
                window = new Window { Content = panel, Width = 1000, Height = 900,
                    ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000 };
                window.Show();
                window.UpdateLayout();
                var grid = Assert.IsType<DataGrid>(panel.FindName("PendingGrid"));
                Assert.Equal(configured ? 1 : 0, grid.Items.Count);
                if (configured) Assert.Same(item, grid.SelectedItem);
                else Assert.False(Assert.IsType<Button>(panel.FindName("RecoverButton")).IsEnabled);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                model?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static InteractiveSession AuthenticatedSession() =>
        new(InteractiveSessionState.Authenticated, "recovery-operator", Guid.NewGuid());

    private static ProductionRecoveryPendingItem PendingItem()
    {
        var inspectionId = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var admission = Uninitialized<ProductionInspectionAdmission>();
        Set(admission, nameof(ProductionInspectionAdmission.InspectionId), inspectionId);
        Set(admission, nameof(ProductionInspectionAdmission.RuntimeEpoch), runtimeEpoch);
        Set(admission, nameof(ProductionInspectionAdmission.ControllerCycle),
            new PlcControllerCycle(17, 42));

        var history = Uninitialized<ProductionInspectionHistoryEvent>();
        Set(history, nameof(ProductionInspectionHistoryEvent.Position), 8L);
        Set(history, nameof(ProductionInspectionHistoryEvent.Admission), admission);
        Set(history, nameof(ProductionInspectionHistoryEvent.RecordedAtUtc), DateTimeOffset.UtcNow);
        Set(history, nameof(ProductionInspectionHistoryEvent.MonotonicTimestamp), 1L);
        Set(history, nameof(ProductionInspectionHistoryEvent.ReasonCode), "RecoveryRequired");

        var eventHash = new string('A', 64);
        Set(history, nameof(ProductionInspectionHistoryEvent.ContentHash), eventHash);
        return new ProductionRecoveryPendingItem(inspectionId, 8, eventHash,
            ProductionRecoveryDeliveryPhase.RecoveryRequired,
            ProductionRecoveryUncertaintyKind.ProcessRestart, history);
    }

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void Set<T>(T target, string propertyName, object? value) where T : class
    {
        var field = typeof(T).GetField("<" + propertyName + ">k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is null) throw new MissingFieldException(typeof(T).FullName, propertyName);
        field.SetValue(target, value);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; }
    }

    private sealed class PausedDispatcher : IUiDispatcher
    {
        internal TaskCompletionSource<bool> Queued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CheckAccess => true;
        public async ValueTask InvokeAsync(Action action)
        {
            Queued.TrySetResult(true);
            await Release.Task;
            action();
        }
    }

    private sealed class FakeHistory : IProductionRecoveryHistoryQuery
    {
        internal ProductionRecoveryPendingPage Page { get; set; }
        internal FakeHistory(ProductionRecoveryPendingItem item) => Page =
            new(true, "ProductionRecoveryPendingAvailable", new[] { item }, 1, null);
        public ValueTask<ProductionRecoveryPendingPage> QueryPendingAsync(int pageSize = 128,
            long afterPosition = 0, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Page);
        public ValueTask<ProductionRecoveryHistoryReadResult> ReadCurrentAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProductionRecoveryHistoryReadResult> ReadAsync(Guid inspectionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProductionRecoveryHistoryPage> QueryAsync(ProductionInspectionHistoryFilter filter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingHistory : IProductionRecoveryHistoryQuery
    {
        private readonly ProductionRecoveryPendingPage _page;
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _blockNext;
        internal TaskCompletionSource<bool> Started { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BlockingHistory(ProductionRecoveryPendingItem item) => _page =
            new(true, "ProductionRecoveryPendingAvailable", new[] { item }, 1, null);
        internal void BlockNext()
        {
            Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockNext = true;
        }
        internal void Release() => _release.TrySetResult(true);
        public async ValueTask<ProductionRecoveryPendingPage> QueryPendingAsync(int pageSize = 128,
            long afterPosition = 0, CancellationToken cancellationToken = default)
        {
            if (!_blockNext) return _page;
            _blockNext = false;
            Started.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            return _page;
        }
        public ValueTask<ProductionRecoveryHistoryReadResult> ReadCurrentAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProductionRecoveryHistoryReadResult> ReadAsync(Guid inspectionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ProductionRecoveryHistoryPage> QueryAsync(ProductionInspectionHistoryFilter filter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRuntime : IStationRuntime
    {
        internal RuntimeCommand? LastCommand { get; private set; }
        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return ValueTask.FromResult(new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Accepted, "ProductionRecoveryAccepted", AuditPersistence.Persisted));
        }
        public ValueTask<StationStateSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        internal StepUpRequest? LastRequest { get; private set; }
        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()));
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        internal FakeSessions(InteractiveSession current) => Current = current;
        internal void Publish(InteractiveSession session)
        {
            Current = session;
            Changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
