using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

/// <summary>
/// Owns one explicitly selected custom editor session for the current draft.
/// The host keeps the extension surface limited to the draft edit context and
/// never discovers or resolves an editor on its own.
/// </summary>
internal sealed class RecipeDraftCustomEditorHost : IDisposable
{
    private readonly AlgorithmConfigurationEditorRegistry? _registry;
    private readonly ContentControl _contentHost;
    private readonly Action _stateChanged;

    private RecipeDraftEditorViewModel _viewModel;
    private RecipeDraftEditorViewModel.AlgorithmConfigurationDraftEditContext? _context;
    private Func<bool>? _contextIsCurrent;
    private Action? _contextActivate;
    private Action? _contextRevoke;
    private IAlgorithmConfigurationEditorSession? _session;
    private bool _disposed;
    private bool _deactivating;
    private bool _initializing;
    private bool _initializationFailed;
    private bool _refreshing;
    private bool _refreshPending;
    private bool _failing;
    private string _statusCode = "CustomEditorGeneric";
    private string _statusText = "通用编辑器已启用。";

    internal RecipeDraftCustomEditorHost(RecipeDraftEditorViewModel viewModel,
        AlgorithmConfigurationEditorRegistry? registry, ContentControl contentHost,
        Action stateChanged)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _registry = registry;
        _contentHost = contentHost ?? throw new ArgumentNullException(nameof(contentHost));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        // Keep the extension view outside the panel's ViewModel inheritance
        // chain. The extension receives only the restricted edit context.
        _contentHost.DataContext = null;
    }

    internal bool IsCustomActive => !_disposed && !_deactivating && _session is not null &&
        _context is not null && ContextIsCurrent();

    internal bool CanUseCustomEditor => !_disposed && !_deactivating && !_initializing &&
        !_refreshing && !IsCustomActive && _registry is not null &&
        _viewModel.IsConfigured && _viewModel.HasDraft && !_viewModel.IsBusy &&
        _viewModel.IsAuthenticated && _viewModel.CustomEditorAlgorithm is not null &&
        _viewModel.CustomEditorSchema is not null;

    internal string StatusCode => _statusCode;
    internal string StatusText => _statusText;

    internal void AttachViewModel(RecipeDraftEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (ReferenceEquals(_viewModel, viewModel)) return;

        DeactivateCore();
        _viewModel = viewModel;
        SetStatus("CustomEditorGeneric");
        NotifyStateChanged();
    }

    internal bool TryUseCustomEditor()
    {
        if (_disposed || _deactivating || _initializing || _refreshing) return false;
        if (IsCustomActive) return true;

        // A context can have become stale between the last PropertyChanged and
        // this explicit request. Revoke the old capability before resolving a
        // new one so a stale editor can never remain mounted.
        if (_session is not null || _context is not null)
            DeactivateCore();
        if (!TrySetContent(null))
        {
            SetStatus("CustomEditorViewInvalid");
            NotifyStateChanged();
            return false;
        }

        var algorithm = _viewModel.CustomEditorAlgorithm;
        var schema = _viewModel.CustomEditorSchema;
        if (_registry is null || algorithm is null || schema is null ||
            !_viewModel.IsConfigured || !_viewModel.HasDraft || _viewModel.IsBusy ||
            !_viewModel.IsAuthenticated)
        {
            SetStatus("CustomEditorUnavailable");
            NotifyStateChanged();
            return false;
        }

        if (!_registry.TryResolve(algorithm, schema, out var factory, out var resolveReason) ||
            factory is null)
        {
            SetStatus(resolveReason);
            NotifyStateChanged();
            return false;
        }

        _initializing = true;
        _initializationFailed = false;
        try
        {
            RecipeDraftEditorViewModel.AlgorithmConfigurationDraftEditContext? context;
            try
            {
                context = _viewModel.CreateCustomEditorContext(ReportFailure);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorInitializationFailed");
            }

            if (context is null)
                return FailInitialization("CustomEditorContextUnavailable");

            _context = context;
            _contextIsCurrent = () => context.IsCurrent;
            _contextActivate = context.Activate;
            _contextRevoke = context.Revoke;
            if (!context.IsCurrent)
                return FailInitialization("CustomEditorContextUnavailable");

            IAlgorithmConfigurationEditorSession? session;
            try
            {
                session = factory.Create(context);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return FailInitialization("CustomEditorInitializationFailed");
            }

            if (session is null)
                return FailInitialization("CustomEditorInitializationFailed");
            // Retain the returned session before checking a context which may
            // have been revoked during Create. Failure cleanup must still
            // dispose an object whose factory already constructed it.
            _session = session;
            if (_initializationFailed || !context.IsCurrent)
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorContextUnavailable");

            FrameworkElement? view;
            try
            {
                view = session.View;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorViewInvalid");
            }

            if (view is null)
                return FailInitialization("CustomEditorViewInvalid");
            if (_initializationFailed || !context.IsCurrent)
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorContextUnavailable");

            AlgorithmConfigurationEditorSnapshot? snapshot;
            try
            {
                snapshot = context.Read();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorInitializationFailed");
            }

            if (snapshot is null)
                return FailInitialization("CustomEditorContextUnavailable");
            try
            {
                using (context.EnterHostCallback())
                    session.Refresh(snapshot);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorRefreshFailed");
            }

            // Refresh is allowed to report a failure synchronously. Do not
            // mount or activate a context which has already been revoked.
            if (_initializationFailed || !context.IsCurrent)
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorContextUnavailable");

            if (!TrySetContent(view))
                return FailInitialization("CustomEditorViewInvalid");
            if (_initializationFailed || !context.IsCurrent)
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorContextUnavailable");

            _contextActivate?.Invoke();
            if (_initializationFailed || !context.IsCurrent)
                return FailInitialization(_initializationFailed
                    ? "CustomEditorReportedFailure" : "CustomEditorContextUnavailable");

            SetStatus("CustomEditorResolved");
            NotifyStateChanged();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FailInitialization("CustomEditorInitializationFailed");
        }
        finally
        {
            _initializing = false;
            if (_refreshPending && IsCustomActive && !_disposed)
                Refresh();
            else
                _refreshPending = false;
        }
    }

    internal void UseGenericEditor()
    {
        if (_disposed) return;
        DeactivateCore();
        SetStatus("CustomEditorGeneric");
        NotifyStateChanged();
    }

    internal void Refresh()
    {
        if (_disposed || _deactivating || _session is null || _context is null) return;
        if (_initializing || _refreshing)
        {
            _refreshPending = true;
            return;
        }

        var pass = 0;
        do
        {
            _refreshPending = false;
            _refreshing = true;
            try
            {
                if (!RefreshOnce()) return;
            }
            finally
            {
                _refreshing = false;
            }
            pass++;
        }
        while (_refreshPending && pass < 4 && IsCustomActive && !_disposed);

        // A faulty editor which changes the buffer on every Refresh must not
        // monopolize the UI thread. The next VM notification can retry safely.
        _refreshPending = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DeactivateCore();
    }

    private bool RefreshOnce()
    {
        var context = _context;
        var session = _session;
        if (context is null || session is null || !ContextIsCurrent())
            return FailActive("CustomEditorContextUnavailable", reportContext: false);

        AlgorithmConfigurationEditorSnapshot? snapshot;
        try
        {
            snapshot = context.Read();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return FailActive("CustomEditorRefreshFailed", reportContext: false);
        }

        if (snapshot is null)
            return FailActive("CustomEditorContextUnavailable", reportContext: false);
        try
        {
            using (context.EnterHostCallback())
                session.Refresh(snapshot);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!IsCustomActive) return false;
            return FailActive("CustomEditorRefreshFailed", reportContext: false);
        }

        if (!ContextIsCurrent())
        {
            if (!IsCustomActive) return false;
            return FailActive("CustomEditorContextUnavailable", reportContext: false);
        }
        return true;
    }

    private bool ContextIsCurrent()
    {
        try { return _contextIsCurrent?.Invoke() == true; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private void ReportFailure()
    {
        if (_disposed) return;
        if (_initializing)
        {
            _initializationFailed = true;
            return;
        }
        if (_failing || _deactivating) return;
        FailActive("CustomEditorReportedFailure", reportContext: false);
    }

    private bool FailInitialization(string reasonCode)
    {
        _initializationFailed = true;
        DeactivateCore();
        SetStatus(reasonCode);
        NotifyStateChanged();
        return false;
    }

    private bool FailActive(string reasonCode, bool reportContext)
    {
        if (!IsCustomActive && _context is null && _session is null) return false;
        if (_failing) return false;
        _failing = true;
        try
        {
            if (reportContext)
            {
                try { _context?.ReportFailure(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
            DeactivateCore();
            SetStatus(reasonCode);
            NotifyStateChanged();
            return false;
        }
        finally
        {
            _failing = false;
        }
    }

    private void DeactivateCore()
    {
        if (_deactivating) return;
        _deactivating = true;
        try
        {
            try { _contextRevoke?.Invoke(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            _context = null;
            _contextIsCurrent = null;
            _contextActivate = null;
            _contextRevoke = null;

            _ = TrySetContent(null);

            var session = _session;
            _session = null;
            try { session?.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        finally
        {
            _deactivating = false;
        }
    }

    private bool TrySetContent(FrameworkElement? view)
    {
        try
        {
            if (view is not null && (!view.Dispatcher.CheckAccess() ||
                LogicalTreeHelper.GetParent(view) is not null ||
                System.Windows.Media.VisualTreeHelper.GetParent(view) is not null)) return false;
            _contentHost.Content = view;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void SetStatus(string code)
    {
        _statusCode = code;
        _statusText = code switch
        {
            "CustomEditorResolved" => "自定义编辑器已启用。",
            "CustomEditorAmbiguous" => "自定义编辑器注册不唯一，已使用通用编辑器。",
            "CustomEditorDescriptorInvalid" => "自定义编辑器注册已失效，已使用通用编辑器。",
            "CustomEditorInitializationFailed" => "自定义编辑器初始化失败，已使用通用编辑器。",
            "CustomEditorViewInvalid" => "自定义编辑器视图不可用，已使用通用编辑器。",
            "CustomEditorRefreshFailed" => "自定义编辑器刷新失败，已使用通用编辑器。",
            "CustomEditorReportedFailure" => "自定义编辑器主动报告失败，已使用通用编辑器。",
            "CustomEditorContextUnavailable" => "自定义编辑器上下文已失效，已使用通用编辑器。",
            _ => "未找到与当前算法和 Schema 完全匹配的自定义编辑器，已使用通用编辑器。"
        };
        if (code == "CustomEditorGeneric") _statusText = "通用编辑器已启用。";
    }

    private void NotifyStateChanged()
    {
        try { _stateChanged(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}
