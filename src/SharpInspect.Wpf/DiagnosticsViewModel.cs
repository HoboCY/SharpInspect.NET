using System.Collections.ObjectModel;
using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public sealed record DiagnosticDisplayRow(string Time, string Code, string Level, string Execution, string Properties);

/// <summary>One bounded page. Session/channel/navigation changes invalidate in-flight projection.</summary>
public sealed class DiagnosticsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDiagnosticPipelineHealthQuery _health;
    private readonly IDiagnosticHistoryQuery _history;
    private readonly IInteractiveSessionService? _sessions;
    private readonly IUiDispatcher _dispatcher;
    private readonly int _maximumRecords, _maximumBytes;
    private readonly ObservableCollection<DiagnosticDisplayRow> _rows = new();
    private CancellationTokenSource? _cancellation;
    private long _generation;
    private bool _protected, _busy, _disposed;
    private string _status = "尚未查询日志。", _healthText = "诊断健康未知。";

    public DiagnosticsViewModel(IDiagnosticPipelineHealthQuery health, IDiagnosticHistoryQuery history,
        IInteractiveSessionService? sessions, int maximumRecords, int maximumBytes, IUiDispatcher? dispatcher = null)
    {
        if (maximumRecords is < 1 or > 1000 || maximumBytes is < 256 or > 4194304)
            throw new ArgumentException("DiagnosticQueryBoundsInvalid");
        _health = health; _history = history; _sessions = sessions; _maximumRecords = maximumRecords; _maximumBytes = maximumBytes;
        _dispatcher = dispatcher ?? new DispatcherUiDispatcher(); Rows = new(_rows);
        RefreshCommand = new(RefreshAsync, () => !_disposed && !_busy);
        if (sessions is not null) sessions.Changed += SessionChanged;
    }
    public ReadOnlyObservableCollection<DiagnosticDisplayRow> Rows { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public string StatusText => _status;
    public string HealthText => _healthText;
    public bool IsLoading => _busy;
    public bool ProtectedChannel
    {
        get => _protected;
        set { EnsureUi(); if (_protected == value) return; _protected = value; Deactivate(); OnPropertyChanged(); }
    }

    public async Task RefreshAsync()
    {
        EnsureUi(); if (_disposed || _busy) return;
        Deactivate();
        var session = _sessions?.Current;
        if (session is not { State: InteractiveSessionState.Authenticated, PrincipalId: not null, SessionId: not null })
        { _status = "请先登录。"; Notify(); return; }
        var generation = Interlocked.Read(ref _generation); var protectedChannel = _protected;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation; _busy = true;
        _status = "正在查询。"; Notify();
        try
        {
            var health = _health.ReadHealth();
            var page = await _history.ReadAsync(new(protectedChannel, _maximumRecords, _maximumBytes,
                new(CommandSource.PhysicalConsole, session.PrincipalId, session.SessionId)), cancellation.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != Interlocked.Read(ref _generation) || _sessions?.Current.SessionId != session.SessionId ||
                    _sessions.Current.State != InteractiveSessionState.Authenticated) return;
                _healthText = health.Configured
                    ? $"{(health.Unhealthy ? "诊断降级" : "诊断可用")}；接受 {health.Accepted}，拒绝 {health.Rejected}，额度丢弃 {health.QuotaDropped}，排队丢弃 {health.SaturationDropped}，迟到丢弃 {health.LateDropped}。"
                    : "诊断策略未激活；输出健康未知。";
                if (!page.Available) { _status = "日志查询不可用：" + Bounded(page.ReasonCode); return; }
                if (page.Records.Count > _maximumRecords) { _status = "日志查询超出限制。"; return; }
                foreach (var record in page.Records)
                {
                    if (record.Properties.Count > 32) { _rows.Clear(); _status = "日志记录无效。"; return; }
                    _rows.Add(new(record.ObservedAtUtc.ToString("u", CultureInfo.InvariantCulture), Bounded(record.Code),
                        record.Level.ToString(), record.Execution?.Value.ToString("D") ?? "—",
                        string.Join("; ", record.Properties.Select(property => Bounded(property.Name) + "=" + Scalar(property.Value)))));
                }
                _status = $"已读取 {_rows.Count} 条{(protectedChannel ? "受保护诊断" : "安全日志")}；省略 {page.OmittedRecords} 条无效记录。";
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { await _dispatcher.InvokeAsync(() => { if (generation == Interlocked.Read(ref _generation)) _status = "日志查询不可用。"; }); }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            { if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null; _busy = false; Notify(); });
        }
    }

    private static string Bounded(string? value) => value is null ? "—" : value.Length <= 128 ? value : "Invalid";
    private static string Scalar(DiagnosticScalar scalar) => scalar.Kind switch
    {
        DiagnosticScalarKind.Boolean => scalar.Boolean ? "true" : "false",
        DiagnosticScalarKind.Int64 => scalar.Integer.ToString(CultureInfo.InvariantCulture),
        DiagnosticScalarKind.Number => scalar.Number.ToString("R", CultureInfo.InvariantCulture),
        DiagnosticScalarKind.Symbol => Bounded(scalar.Symbol), _ => "Invalid"
    };
    public void Deactivate()
    {
        EnsureUi(); Interlocked.Increment(ref _generation); _cancellation?.Cancel(); _cancellation = null;
        _rows.Clear(); _status = "日志内容已清除。"; _healthText = "诊断健康未知。"; Notify();
    }
    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        Interlocked.Increment(ref _generation);
        _ = _dispatcher.InvokeAsync(Deactivate);
    }
    private void EnsureUi() { if (!_dispatcher.CheckAccess) throw new PresentationStateUnavailableException(); }
    private void Notify()
    { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(HealthText)); OnPropertyChanged(nameof(IsLoading)); RefreshCommand.RaiseCanExecuteChanged(); }
    public async ValueTask DisposeAsync() => await _dispatcher.InvokeAsync(() =>
    { if (_disposed) return; _disposed = true; if (_sessions is not null) _sessions.Changed -= SessionChanged; Deactivate(); });
}
