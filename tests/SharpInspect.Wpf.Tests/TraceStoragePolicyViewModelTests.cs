using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class TraceStoragePolicyViewModelTests
{
    [Fact]
    public void V139_W01_BlankEditorHasNoImplicitNumericDefaultsAndReportsAllFields()
    {
        var editor = new TraceStoragePolicyEditor();

        Assert.False(editor.TryBuild(out var policy, out var errors));
        Assert.Null(policy);
        Assert.True(errors.Count >= 10);
        Assert.All(editor.RetentionRules, rule => Assert.Equal(string.Empty, rule.MinimumRetention));
        Assert.Equal(string.Empty, editor.MinimumReserveBytes);
        Assert.Equal(string.Empty, editor.MaximumWalBytes);
    }

    [Fact]
    public void V139_W02_LoadAndBuildPreservesEveryPolicyFieldAndHash()
    {
        var source = Policy("1", "full policy roundtrip");
        var editor = new TraceStoragePolicyEditor();

        editor.Load(source);

        Assert.True(editor.TryBuild(out var rebuilt, out var errors), string.Join("; ", errors));
        Assert.NotNull(rebuilt);
        Assert.Equal(source.ContentHash, rebuilt!.ContentHash);
        Assert.Equal(source.RetentionRules.Count, editor.RetentionRules.Count);
        Assert.Equal(source.RequiredRoutes.Count, editor.RequiredRoutes.Count);
    }

    [Fact]
    public async Task V139_W03_RefreshAllowsFirstExplicitPublishWithoutImplicitPolicy()
    {
        var fixture = new Fixture { RequiresStepUp = true };
        await using var model = fixture.CreateViewModel();

        await model.RefreshAsync();
        Assert.Null(model.Current);
        Assert.False(model.Editor.TryBuild(out _, out var blankErrors));
        Assert.NotEmpty(blankErrors);

        model.Editor.Load(Policy("1", "first approved policy"));
        Assert.True(model.CanPublish);
        var result = await model.PublishAsync("首次明确发布", "temporary-password");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        var publication = result.Publication!;
        Assert.Equal(1, publication.Version);
        Assert.Equal(publication.ContentHash, model.Current!.ContentHash);
        Assert.Equal(publication.Policy.ContentHash, fixture.LastCommand!.Policy.ContentHash);
        Assert.Equal(Permission.ManageProductionPolicy, fixture.LastRequest!.Binding.Permission);
        Assert.Equal(AuditedCommandKind.PublishTraceStoragePolicy,
            fixture.LastRequest.Binding.CommandKind);
        Assert.Equal(fixture.LastCommand.AuthorizationTarget,
            fixture.LastRequest.Binding.TargetId);
        Assert.Equal("temporary-password", fixture.LastPassword);
    }

    [Fact]
    public async Task V139_W04_RefreshAndPublishKeepPreflightHistoryAndSnapshotSeparate()
    {
        var fixture = new Fixture { RequiresStepUp = false };
        await using var model = fixture.CreateViewModel();
        model.Editor.Load(Policy("1", "first policy"));

        await model.RefreshAsync();
        var first = await model.PublishAsync("first", string.Empty);
        Assert.True(first!.Succeeded);
        var firstPublication = first.Publication!;
        var firstSnapshot = first.Snapshot!;
        await model.RefreshAsync();

        Assert.NotNull(model.Preflight);
        Assert.NotNull(model.History);
        Assert.Single(model.History!.Records);
        Assert.Equal(firstSnapshot.ContentHash, model.Snapshot!.ContentHash);
        Assert.Equal(firstPublication.ContentHash, model.Current!.ContentHash);
    }

    [Fact]
    public async Task V139_W05_SessionChangeDropsLateStepUpAndDoesNotSubmitOldPolicy()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture
        {
            RequiresStepUp = true,
            StepUpHandler = request =>
            {
                entered.TrySetResult(true);
                return new ValueTask<StepUpResult>(release.Task);
            }
        };
        await using var model = fixture.CreateViewModel();
        await model.RefreshAsync();
        model.Editor.Load(Policy("1", "stale policy"));

        var publish = model.PublishAsync("stale", "temporary-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Publish(new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null));
        release.TrySetResult(new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()));

        Assert.Null(await publish.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, fixture.PublishCount);
        Assert.Null(model.Current);
        Assert.False(model.CanPublish);
    }

    [Fact]
    public async Task V139_W06_PanelUsesStableNamesAndClearsPasswordBeforePublish()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Fixture? fixture = null;
            Window? window = null;
            try
            {
                fixture = new Fixture { RequiresStepUp = false };
                var model = fixture.CreateViewModel();
                model.RefreshAsync().GetAwaiter().GetResult();
                model.Editor.Load(Policy("1", "panel policy"));
                var panel = new TraceStoragePolicyPanel(model);
                window = new Window { Content = panel, Width = 1000, Height = 800,
                    ShowInTaskbar = false, ShowActivated = false, Left = -32000, Top = -32000 };
                window.Show();
                window.UpdateLayout();
                var reason = Assert.IsType<TextBox>(panel.FindName("ReasonTextBox"));
                var password = Assert.IsType<PasswordBox>(panel.FindName("StepUpPasswordBox"));
                var publish = Assert.IsType<Button>(panel.FindName("PublishButton"));
                Assert.NotNull(panel.FindName("RefreshButton"));
                reason.Text = "panel publish";
                password.Password = "temporary-password";
                publish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(string.Empty, password.Password);
                fixture.PublishCompleted.Task.GetAwaiter().GetResult();
                Assert.Equal(1, fixture.PublishCount);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V139_W07_SuccessInvalidatesOldPreflightAndHistoryUntilRefresh()
    {
        var fixture = new Fixture { RequiresStepUp = false };
        await using var model = fixture.CreateViewModel();
        model.Editor.Load(Policy("1", "versioned policy"));

        await model.RefreshAsync();
        var result = await model.PublishAsync("publish versioned policy", string.Empty);

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, model.Current!.Version);
        Assert.Equal(result.Publication!.ContentHash, model.Snapshot!.Publication.ContentHash);
        Assert.Null(model.Preflight);
        Assert.Null(model.History);
        Assert.False(model.CanPublish);
        Assert.Contains("刷新", model.StatusMessage, StringComparison.Ordinal);

        await model.RefreshAsync();
        Assert.NotNull(model.Preflight);
        Assert.NotNull(model.History);
        Assert.Equal(1, model.History!.ThroughVersion);
    }

    [Fact]
    public async Task V139_W08_AsyncRefreshPublishesNotificationsOnTheRealDispatcher()
    {
        var completed = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var fixture = new Fixture { RequiresStepUp = false };
                var service = new ThreadHoppingPolicyService(fixture);
                var model = new TraceStoragePolicyViewModel(service, fixture, fixture,
                    new DispatcherUiDispatcher(dispatcher));
                _ = new TraceStoragePolicyPanel(model);
                var notificationCount = 0;
                model.PropertyChanged += (_, _) =>
                {
                    if (!dispatcher.CheckAccess())
                        throw new InvalidOperationException("PropertyChanged escaped the UI dispatcher.");
                    Interlocked.Increment(ref notificationCount);
                };

                dispatcher.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        await model.RefreshAsync();
                        if (notificationCount == 0)
                            throw new InvalidOperationException("Refresh produced no UI notifications.");
                        completed.TrySetResult(null);
                    }
                    catch (Exception exception)
                    {
                        completed.TrySetResult(exception);
                    }
                    finally
                    {
                        try { await model.DisposeAsync(); }
                        catch (Exception exception) { completed.TrySetResult(exception); }
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                }), DispatcherPriority.Background);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                completed.TrySetResult(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var failure = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(failure);
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "The UI dispatcher did not shut down.");
    }

    [Fact]
    public async Task V139_W09_DelayedOldSessionNotificationCannotClearNewProjection()
    {
        var dispatcher = new DeferredDispatcher();
        var fixture = new Fixture { RequiresStepUp = false };
        await using var model = new TraceStoragePolicyViewModel(fixture, fixture, fixture, dispatcher);

        model.Editor.Load(Policy("1", "initial policy"));
        await model.RefreshAsync();
        var first = await model.PublishAsync("initial", string.Empty);
        Assert.NotNull(first);
        Assert.True(first!.Succeeded);

        var switched = new InteractiveSession(InteractiveSessionState.Authenticated,
            "new-principal", Guid.NewGuid());
        dispatcher.Hold = true;
        await Task.Run(() => fixture.Publish(switched));
        await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.Hold = false;

        // The session-change notification is still queued on the old UI turn.
        // A new refresh and publish must nevertheless complete on the UI thread.
        await model.RefreshAsync();
        model.Editor.Load(Policy("2", "new policy"));
        var second = await model.PublishAsync("new", string.Empty);
        Assert.NotNull(second);
        Assert.True(second!.Succeeded);
        await model.RefreshAsync();

        var current = model.Current;
        var lastResult = model.LastResult;
        var history = model.History;
        Assert.NotNull(current);
        Assert.NotNull(lastResult);
        Assert.NotNull(history);
        var currentHash = current!.ContentHash;
        var resultHash = lastResult!.Publication!.ContentHash;
        var historyThrough = history!.ThroughVersion;

        dispatcher.ReleaseAll();

        Assert.Equal(currentHash, model.Current!.ContentHash);
        Assert.Equal(resultHash, model.LastResult!.Publication!.ContentHash);
        Assert.Equal(historyThrough, model.History!.ThroughVersion);
        Assert.Contains(history!.Records,
            record => record.ContentHash == currentHash);
    }

    private static TraceStoragePolicyDefinition Policy(string version, string rationale) =>
        new("ui.trace.storage", version, "ADR-0078", "1", rationale,
            Enum.GetValues<TraceRetentionClass>().Select(value => new TraceRetentionRule(value,
                value == TraceRetentionClass.CompletedExport ? RetentionStartEvent.ExportCompleted :
                RetentionStartEvent.ArtifactCreated, TimeSpan.FromDays(30))),
            1_000_000, 5m,
            new[] { new TraceStorageRouteLimit("outbox", "1", new string('A', 64),
                new TraceBacklogLimits(100, 1_000_000, TimeSpan.FromDays(7))) },
            new TraceBacklogLimits(100, 1_000_000, TimeSpan.FromDays(7)),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            new TraceStorageMaintenanceBudget(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), 1_000_000, 100),
            new TraceStorageMaintenanceBudget(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), 1_000_000, 100),
            10_000_000);

    private sealed class Fixture : ITraceStoragePolicyService, IInteractiveSessionService, IStepUpAuthentication
    {
        private static readonly Guid PrincipalId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private readonly List<TraceStoragePolicyPublication> _publications = new();
        internal bool RequiresStepUp { get; set; }
        internal Func<StepUpRequest, ValueTask<StepUpResult>>? StepUpHandler { get; set; }
        internal StepUpRequest? LastRequest { get; private set; }
        internal string? LastPassword => LastRequest?.Password;
        internal PublishTraceStoragePolicyCommand? LastCommand { get; private set; }
        internal int PublishCount { get; private set; }
        internal TaskCompletionSource<bool> PublishCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InteractiveSession Current { get; private set; } =
            new(InteractiveSessionState.Authenticated, PrincipalId.ToString("D"), SessionId);
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;

        internal TraceStoragePolicyViewModel CreateViewModel() =>
            new(this, this, this, new InlineDispatcher());

        internal void Publish(InteractiveSession session)
        {
            Current = session;
            Changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }

        public ValueTask<TraceStoragePolicyAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TraceStoragePolicyAccess(true, "TraceStoragePolicyAccessAvailable", RequiresStepUp));

        public ValueTask<TraceStoragePolicyReadResult> ReadAsync(long? version = null,
            CancellationToken cancellationToken = default)
        {
            if (version is { } requested)
            {
                var exact = _publications.SingleOrDefault(value => value.Version == requested);
                return ValueTask.FromResult(new TraceStoragePolicyReadResult(true,
                    exact is null ? "TraceStoragePolicyVersionNotFound" : "TraceStoragePolicyAvailable",
                    exact, exact is null ? null : new TraceStoragePolicySnapshot(exact)));
            }
            var current = _publications.LastOrDefault();
            return ValueTask.FromResult(new TraceStoragePolicyReadResult(true,
                current is null ? "TraceStoragePolicyMissing" : "TraceStoragePolicyAvailable",
                current, current is null ? null : new TraceStoragePolicySnapshot(current)));
        }

        public ValueTask<TraceStoragePolicyHistoryPage> QueryAsync(TraceStoragePolicyFilter filter,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TraceStoragePolicyHistoryPage(true,
                "TraceStoragePolicyHistoryAvailable", _publications.ToArray(),
                _publications.LastOrDefault()?.Version ?? 0, null));

        public ValueTask<TraceStoragePreflightReport> GetPreflightAsync(
            CancellationToken cancellationToken = default)
        {
            var publication = _publications.LastOrDefault();
            var snapshot = publication is null ? null : new TraceStoragePolicySnapshot(publication);
            var rows = Enum.GetValues<TraceStoragePreflightGate>().Select(gate =>
            {
                var status = gate == TraceStoragePreflightGate.Policy
                    ? publication is null ? TraceStoragePreflightStatus.Missing : TraceStoragePreflightStatus.Passed
                    : gate == TraceStoragePreflightGate.ProductionCycle
                        ? TraceStoragePreflightStatus.NotImplemented
                        : TraceStoragePreflightStatus.Passed;
                var reason = status == TraceStoragePreflightStatus.Passed
                    ? "TraceStoragePreflightPassed"
                    : status == TraceStoragePreflightStatus.Missing
                        ? "TraceStoragePolicyMissing"
                        : "TraceStorageProductionCycleNotImplemented";
                return new TraceStoragePreflightRow(gate, status, reason);
            });
            return ValueTask.FromResult(new TraceStoragePreflightReport(DateTimeOffset.UtcNow,
                publication?.Version, snapshot?.ContentHash, rows));
        }

        public ValueTask<TraceStoragePolicyResult> PublishAsync(PublishTraceStoragePolicyCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            PublishCount++;
            var previous = _publications.LastOrDefault();
            var publication = new TraceStoragePolicyPublication((previous?.Version ?? 0) + 1,
                command.Policy, Guid.NewGuid(), PrincipalId, SessionId, 1,
                command.Invocation.StepUpGrantId ?? Guid.NewGuid(), DateTimeOffset.UtcNow,
                previous?.ContentHash);
            _publications.Add(publication);
            PublishCompleted.TrySetResult(true);
            return ValueTask.FromResult(new TraceStoragePolicyResult(
                new(command.CorrelationId, CommandDisposition.Accepted,
                    "TraceStoragePolicyAuthorized", AuditPersistence.Persisted), publication,
                new TraceStoragePolicySnapshot(publication)));
        }

        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (StepUpHandler is not null) return StepUpHandler(request);
            return ValueTask.FromResult(new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()));
        }

        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action) { action(); return ValueTask.CompletedTask; }
    }

    private sealed class DeferredDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource<bool> Completion)> _queued = new();
        private volatile bool _hold;

        internal TaskCompletionSource<bool> Queued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Hold
        {
            get => _hold;
            set => _hold = value;
        }

        public bool CheckAccess => !Hold;

        public ValueTask InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (CheckAccess)
            {
                action();
                return ValueTask.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queued.Enqueue((action, completion));
            Queued.TrySetResult(true);
            return new ValueTask(completion.Task);
        }

        internal void ReleaseAll()
        {
            while (_queued.TryDequeue(out var item))
            {
                try
                {
                    item.Action();
                    item.Completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                }
            }
        }
    }

    private sealed class ThreadHoppingPolicyService : ITraceStoragePolicyService
    {
        private readonly Fixture _fixture;

        internal ThreadHoppingPolicyService(Fixture fixture) => _fixture = fixture;

        public ValueTask<TraceStoragePolicyAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Hop(_fixture.GetAccessAsync(invocation, cancellationToken), cancellationToken);

        public ValueTask<TraceStoragePolicyReadResult> ReadAsync(long? version = null,
            CancellationToken cancellationToken = default) =>
            Hop(_fixture.ReadAsync(version, cancellationToken), cancellationToken);

        public ValueTask<TraceStoragePolicyHistoryPage> QueryAsync(TraceStoragePolicyFilter filter,
            CancellationToken cancellationToken = default) =>
            Hop(_fixture.QueryAsync(filter, cancellationToken), cancellationToken);

        public ValueTask<TraceStoragePreflightReport> GetPreflightAsync(
            CancellationToken cancellationToken = default) =>
            Hop(_fixture.GetPreflightAsync(cancellationToken), cancellationToken);

        public ValueTask<TraceStoragePolicyResult> PublishAsync(
            PublishTraceStoragePolicyCommand command,
            CancellationToken cancellationToken = default) =>
            Hop(_fixture.PublishAsync(command, cancellationToken), cancellationToken);

        private static async ValueTask<T> Hop<T>(ValueTask<T> operation,
            CancellationToken cancellationToken)
        {
            var result = await operation.ConfigureAwait(false);
            await Task.Factory.StartNew(static () => { }, cancellationToken,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
            return result;
        }
    }
}
