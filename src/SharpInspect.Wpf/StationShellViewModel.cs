using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows.Input;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Bounded MVVM adapter over the Runtime application boundary.
/// It never mutates Runtime state directly and treats a command outcome as admission only.
/// </summary>
public sealed class StationShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly record struct FeedItem(StationStateSnapshot? Snapshot, bool Disconnected, long? ArrivalTimestamp)
    {
        public static FeedItem FromSnapshot(StationStateSnapshot snapshot, long arrivalTimestamp) =>
            new(snapshot, false, arrivalTimestamp);
        public static FeedItem DisconnectedItem => new(null, true, null);
    }

    private readonly IStationRuntime _runtime;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMonotonicClock _clock;
    private readonly SnapshotFreshnessPolicy _freshnessPolicy;
    private readonly Func<CommandInvocation> _invocationFactory;
    private readonly Channel<FeedItem> _feed;
    private readonly object _sync = new();
    private readonly HashSet<Guid> _seenEpochs = new();
    private readonly Queue<Guid> _seenEpochOrder = new();
    private readonly StationStateViewModel _state = new();
    private CancellationTokenSource? _lifetime;
    private Task? _pumpTask;
    private Task? _renderTask;
    private Task? _freshnessTask;
    private SnapshotFreshness _freshness = SnapshotFreshness.Unavailable;
    private RuntimeCommandOutcome? _lastCommandOutcome;
    private string? _commandFailureCode;
    private string _selectedSection = "Production";
    private Guid? _currentEpoch;
    private Guid? _feedEpochHint;
    private long _lastRevision;
    private long _arrivalTimestamp;
    private long _generation;
    private bool _hasArrival;
    private bool _awaitingFullSnapshot;
    private bool _discontinuityPending;
    private bool _started;
    private bool _disposed;

    public StationShellViewModel(
        IStationRuntime runtime,
        IUiDispatcher? dispatcher = null,
        IMonotonicClock? clock = null,
        SnapshotFreshnessPolicy? freshnessPolicy = null,
        int feedCapacity = 32,
        Func<CommandInvocation>? invocationFactory = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher();
        _clock = clock ?? new StopwatchMonotonicClock();
        _freshnessPolicy = freshnessPolicy ?? SnapshotFreshnessPolicy.Default;
        if (feedCapacity < 1 || feedCapacity > 256)
            throw new ArgumentOutOfRangeException(nameof(feedCapacity), "The presentation feed capacity must be between 1 and 256.");
        _invocationFactory = invocationFactory ?? (() => new CommandInvocation(CommandSource.PhysicalConsole));
        _feed = Channel.CreateBounded<FeedItem>(new BoundedChannelOptions(feedCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        ArmCommand = new AsyncRelayCommand(() => ArmProductionAsync().AsTask(), () => CanArmProduction);
        StopCommand = new AsyncRelayCommand(() => GracefulStopAsync().AsTask(), () => CanStopProduction);
        ArmCommand.ExecutionFailed += Command_ExecutionFailed;
        StopCommand.ExecutionFailed += Command_ExecutionFailed;
        NavigateCommand = new RelayCommand(parameter => NavigateTo(parameter as string ?? "Production"));
    }

    public StationStateViewModel State => _state;
    public StationStateSnapshot? CurrentSnapshot => _state.RawSnapshot;
    public SnapshotFreshness Freshness
    {
        get { lock (_sync) return _freshness; }
        private set
        {
            bool changed;
            lock (_sync)
            {
                changed = _freshness != value;
                _freshness = value;
            }
            if (changed)
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUnknownOrStale));
                OnPropertyChanged(nameof(CanArmProduction));
                ArmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsUnknownOrStale => Freshness != SnapshotFreshness.Fresh;
    public bool CanArmProduction => Freshness == SnapshotFreshness.Fresh && !State.Busy &&
        State.ArmState != ProductionArmState.Armed && CurrentSnapshot?.Session.State == InteractiveSessionState.Authenticated;
    public bool CanStopProduction => true;
    public string AuthenticationStatus => CurrentSnapshot?.Session.State switch
    { InteractiveSessionState.Authenticated => "已登录；操作权限仍由 Runtime 逐次检查。",
        InteractiveSessionState.Locked => "会话已锁定，请重新登录。", _ => "尚未登录。" };
    public RuntimeCommandOutcome? LastCommandOutcome
    {
        get => _lastCommandOutcome;
        private set => SetProperty(ref _lastCommandOutcome, value);
    }
    public string? CommandFailureCode
    {
        get => _commandFailureCode;
        private set => SetProperty(ref _commandFailureCode, value);
    }
    public CommandProgress? LastCommandProgress => State.LastCommand;
    public string SelectedSection
    {
        get => _selectedSection;
        private set => SetProperty(ref _selectedSection, value);
    }

    public AsyncRelayCommand ArmCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public ICommand NavigateCommand { get; }

    /// <summary>Starts the initial full query and the bounded watch/freshness loops.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StationShellViewModel));
            if (_started) return;
            _started = true;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            token = _lifetime.Token;
        }

        try
        {
            var refreshed = await RefreshFullSnapshotAsync(expectedEpoch: null, token).ConfigureAwait(false);
            if (!refreshed) await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            bool disposed;
            lock (_sync) disposed = _disposed;
            if (!disposed) await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
            lock (_sync)
            {
                _lifetime?.Dispose();
                _lifetime = null;
                _started = false;
            }
            throw;
        }
        catch
        {
            await MarkUnavailableAsync().ConfigureAwait(false);
        }
        _pumpTask = PumpFeedAsync(token);
        _renderTask = RenderFeedAsync(token);
        _freshnessTask = MonitorFreshnessAsync(token);
    }

    /// <summary>
    /// Applies one complete snapshot received by a controlled consumer source.
    /// Same-epoch duplicate/older revisions are ignored without resetting arrival age.
    /// </summary>
    internal Task ApplySnapshotAsync(StationStateSnapshot snapshot, CancellationToken cancellationToken = default) =>
        ObserveFeedSnapshotAsync(snapshot, cancellationToken);

    /// <summary>Feeds one watch item through the same epoch and revision guards used by the live loop.</summary>
    internal async Task ObserveFeedSnapshotAsync(StationStateSnapshot snapshot,
        CancellationToken cancellationToken = default, long? arrivalTimestampOverride = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        Guid? currentEpoch;
        bool awaitingFull;
        long currentRevision;
        bool wasPreviouslyApplied;
        long arrivalTimestamp;
        bool started;
        lock (_sync)
        {
            currentEpoch = _currentEpoch;
            awaitingFull = _awaitingFullSnapshot;
            currentRevision = _lastRevision;
            wasPreviouslyApplied = _seenEpochs.Contains(snapshot.RuntimeEpoch);
            UpdateFeedHintLocked(snapshot.RuntimeEpoch);
            arrivalTimestamp = arrivalTimestampOverride ?? _clock.GetTimestamp();
            started = _started;
        }

        if (currentEpoch is null)
        {
            if (started)
            {
                try
                {
                    var refreshed = await RefreshFullSnapshotAsync(snapshot.RuntimeEpoch, cancellationToken)
                        .ConfigureAwait(false);
                    if (!refreshed) await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
                }
                return;
            }
            await ApplyOnDispatcherAsync(snapshot, fresh: true, forceFull: true, generation: CurrentGeneration(),
                arrivalTimestamp,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (currentEpoch.Value == snapshot.RuntimeEpoch)
        {
            if (awaitingFull)
            {
                await MarkDiscontinuousAndRefreshAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (snapshot.Revision <= currentRevision) return;
            await ApplyOnDispatcherAsync(snapshot, fresh: true, forceFull: false, generation: CurrentGeneration(),
                arrivalTimestamp,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (_sync)
        {
            // Runtime Epoch is process-generation identity. A previously observed epoch
            // arriving after a newer epoch is delayed old feed data, never a new current state.
            if (wasPreviouslyApplied) return;
        }

        try
        {
            await MarkDiscontinuousAsync(cancellationToken).ConfigureAwait(false);
            var refreshed = await RefreshFullSnapshotAsync(snapshot.RuntimeEpoch, cancellationToken).ConfigureAwait(false);
            if (!refreshed) await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
        }
    }

    internal void RefreshFreshness()
    {
        _ = EvaluateFreshnessAsync(CancellationToken.None);
    }

    public async ValueTask<RuntimeCommandOutcome> ArmProductionAsync(CancellationToken cancellationToken = default)
    {
        if (Freshness != SnapshotFreshness.Fresh)
        {
            await RecordCommandFailureAsync(PresentationStateUnavailableException.SafeReasonCode)
                .ConfigureAwait(false);
            throw new PresentationStateUnavailableException();
        }
        var command = new ArmProductionCommand(Guid.NewGuid(), _invocationFactory());
        return await SubmitCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stop remains callable from stale, unavailable, and privacy-locked presentation states.</summary>
    public async ValueTask<RuntimeCommandOutcome> GracefulStopAsync(CancellationToken cancellationToken = default)
    {
        var command = new GracefulProductionStopCommand(Guid.NewGuid(), _invocationFactory());
        return await SubmitCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public void NavigateTo(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return;
        SelectedSection = section;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Cancel();
            _feed.Writer.TryComplete();
            tasks = new[] { _pumpTask, _renderTask, _freshnessTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _lifetime?.Dispose();
        }
    }

    private async Task<RuntimeCommandOutcome> SubmitCommandAsync(RuntimeCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeCommandOutcome outcome;
        try
        {
            outcome = await _runtime.SubmitAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await RecordCommandFailureAsync("CommandOutcomeUnknown").ConfigureAwait(false);
            throw;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            LastCommandOutcome = outcome;
            CommandFailureCode = null;
            OnPropertyChanged(nameof(LastCommandProgress));
        }).ConfigureAwait(false);
        return outcome;
    }

    private async Task PumpFeedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var snapshot in _runtime.WatchSnapshotsAsync(cancellationToken)
                    .ConfigureAwait(false))
                {
                    var arrivalTimestamp = _clock.GetTimestamp();
                    lock (_sync)
                    {
                        if (_currentEpoch.HasValue && snapshot.RuntimeEpoch != _currentEpoch &&
                            !_seenEpochs.Contains(snapshot.RuntimeEpoch) && _feedEpochHint != snapshot.RuntimeEpoch)
                        {
                            // A generation change is control state, not a coalescible notification.
                            _generation++;
                            _discontinuityPending = true;
                            _freshness = SnapshotFreshness.Discontinuous;
                        }
                        UpdateFeedHintLocked(snapshot.RuntimeEpoch);
                    }
                    _feed.Writer.TryWrite(FeedItem.FromSnapshot(snapshot, arrivalTimestamp));
                }
                if (!cancellationToken.IsCancellationRequested)
                    QueueDiscontinuity();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                QueueDiscontinuity();
            }

            try
            {
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RenderFeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _feed.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (ConsumeDiscontinuity() || item.Disconnected)
                {
                    await MarkDiscontinuousAndRefreshAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (item.Snapshot is not null)
                {
                    try
                    {
                        await ObserveFeedSnapshotAsync(item.Snapshot, cancellationToken, item.ArrivalTimestamp)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false); }
    }

    private async Task MonitorFreshnessAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_freshnessPolicy.CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await EvaluateFreshnessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task EvaluateFreshnessAsync(CancellationToken cancellationToken)
    {
        bool shouldMarkStale;
        lock (_sync)
        {
            shouldMarkStale = _freshness == SnapshotFreshness.Fresh && _hasArrival &&
                _clock.ElapsedSince(_arrivalTimestamp) >= _freshnessPolicy.MaximumAge;
        }

        if (!shouldMarkStale) return;
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                // Freshness and the sanitized StateViewModel change in one dispatcher turn.
                // A newer snapshot may have arrived while this check was queued.
                if (_freshness != SnapshotFreshness.Fresh || !_hasArrival ||
                    _clock.ElapsedSince(_arrivalTimestamp) < _freshnessPolicy.MaximumAge)
                    return;
                _freshness = SnapshotFreshness.Stale;
            }
            _state.SetUnknown();
            OnPropertyChanged(nameof(Freshness));
            OnPropertyChanged(nameof(IsUnknownOrStale));
            OnPropertyChanged(nameof(CanArmProduction));
            ArmCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
    }

    private async Task MarkDiscontinuousAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _generation++;
            _awaitingFullSnapshot = true;
            _freshness = SnapshotFreshness.Discontinuous;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            _state.SetUnknown();
            OnPropertyChanged(nameof(Freshness));
            OnPropertyChanged(nameof(IsUnknownOrStale));
            OnPropertyChanged(nameof(CanArmProduction));
            ArmCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task MarkDiscontinuousAndRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await MarkDiscontinuousAsync(cancellationToken).ConfigureAwait(false);
            var refreshed = await RefreshFullSnapshotAsync(CurrentFeedEpochHint(), cancellationToken)
                .ConfigureAwait(false);
            if (!refreshed) await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await MarkUnavailableAsync(requireFullSnapshot: true).ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshFullSnapshotAsync(Guid? expectedEpoch, CancellationToken cancellationToken)
    {
        long generation;
        lock (_sync)
        {
            generation = ++_generation;
            _awaitingFullSnapshot = true;
        }

        for (var attempt = 0; attempt < 8 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            var snapshot = await _runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var arrivalTimestamp = _clock.GetTimestamp();
            bool accepted;
            lock (_sync)
            {
                var hint = _feedEpochHint;
                accepted = generation == _generation &&
                    (!expectedEpoch.HasValue || snapshot.RuntimeEpoch == expectedEpoch.Value) &&
                    (!hint.HasValue || snapshot.RuntimeEpoch == hint.Value);
                // The epoch is marked applied only by the dispatcher action below. A newer
                // epoch observed while this query is pending must remain a valid hint.
            }

            if (accepted)
            {
                var applied = await ApplyOnDispatcherAsync(snapshot, fresh: true, forceFull: true, generation,
                    arrivalTimestamp, cancellationToken).ConfigureAwait(false);
                if (applied) return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (_feedEpochHint.HasValue) expectedEpoch = _feedEpochHint.Value;
            }
        }
        return false;
    }

    private async Task<bool> ApplyOnDispatcherAsync(StationStateSnapshot snapshot, bool fresh, bool forceFull,
        long generation, long? arrivalTimestamp, CancellationToken cancellationToken)
    {
        var applied = false;
        await _dispatcher.InvokeAsync(() =>
        {
            lock (_sync)
            {
                if (generation != _generation) return;
                if (forceFull && _feedEpochHint.HasValue && _feedEpochHint.Value != snapshot.RuntimeEpoch) return;
                if (_currentEpoch.HasValue && _currentEpoch.Value == snapshot.RuntimeEpoch &&
                    snapshot.Revision < _lastRevision) return;
                if (_currentEpoch.HasValue && _currentEpoch.Value == snapshot.RuntimeEpoch &&
                    snapshot.Revision == _lastRevision && !forceFull) return;
                var sameRevision = _currentEpoch.HasValue && _currentEpoch.Value == snapshot.RuntimeEpoch &&
                    snapshot.Revision == _lastRevision;
                var effectiveArrival = sameRevision && _hasArrival
                    ? _arrivalTimestamp : arrivalTimestamp ?? _clock.GetTimestamp();
                var visibleFresh = fresh && _clock.ElapsedSince(effectiveArrival) < _freshnessPolicy.MaximumAge;
                _currentEpoch = snapshot.RuntimeEpoch;
                _lastRevision = snapshot.Revision;
                _awaitingFullSnapshot = false;
                _freshness = visibleFresh ? SnapshotFreshness.Fresh : SnapshotFreshness.Stale;
                _arrivalTimestamp = effectiveArrival;
                _hasArrival = true;
                if (_seenEpochs.Add(snapshot.RuntimeEpoch))
                {
                    _seenEpochOrder.Enqueue(snapshot.RuntimeEpoch);
                    while (_seenEpochOrder.Count > 128)
                    {
                        var expired = _seenEpochOrder.Dequeue();
                        _seenEpochs.Remove(expired);
                    }
                }
                _feedEpochHint = snapshot.RuntimeEpoch;
                applied = true;
            }
            var presentationFresh = Freshness == SnapshotFreshness.Fresh;
            _state.SetSnapshot(snapshot, presentationFresh);
            OnPropertyChanged(nameof(CurrentSnapshot));
            OnPropertyChanged(nameof(Freshness));
            OnPropertyChanged(nameof(IsUnknownOrStale));
            OnPropertyChanged(nameof(CanArmProduction));
            OnPropertyChanged(nameof(LastCommandProgress));
            ArmCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return applied;
    }

    private long CurrentGeneration()
    {
        lock (_sync) return _generation;
    }

    private Guid? CurrentFeedEpochHint()
    {
        lock (_sync) return _feedEpochHint;
    }

    private void UpdateFeedHintLocked(Guid epoch)
    {
        if (!_seenEpochs.Contains(epoch)) _feedEpochHint = epoch;
        else if (!_feedEpochHint.HasValue) _feedEpochHint = epoch;
    }

    private void QueueDiscontinuity()
    {
        lock (_sync)
        {
            _generation++;
            _discontinuityPending = true;
            _freshness = SnapshotFreshness.Discontinuous;
        }
        _feed.Writer.TryWrite(FeedItem.DisconnectedItem);
    }

    private bool ConsumeDiscontinuity()
    {
        lock (_sync)
        {
            if (!_discontinuityPending) return false;
            _discontinuityPending = false;
            return true;
        }
    }

    private async Task MarkUnavailableAsync(bool requireFullSnapshot = false)
    {
        lock (_sync)
        {
            _generation++;
            _awaitingFullSnapshot = requireFullSnapshot;
            _freshness = SnapshotFreshness.Unavailable;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            _state.SetUnknown();
            OnPropertyChanged(nameof(Freshness));
            OnPropertyChanged(nameof(IsUnknownOrStale));
            OnPropertyChanged(nameof(CanArmProduction));
            ArmCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(false);
    }

    private async Task RecordCommandFailureAsync(string safeReasonCode)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            LastCommandOutcome = null;
            CommandFailureCode = safeReasonCode;
            OnPropertyChanged(nameof(LastCommandProgress));
        }).ConfigureAwait(false);
        await MarkUnavailableAsync().ConfigureAwait(false);
    }

    private void Command_ExecutionFailed(object? sender, Exception exception)
    {
        _ = MarkUnavailableAsync();
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
