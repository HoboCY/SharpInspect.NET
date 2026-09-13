using System.Windows.Input;

namespace SharpInspect.Wpf;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public event EventHandler<Exception>? ExecutionFailed;

    // _isExecuting 只防止同一按钮重复发起操作，不承担 Runtime 的权限或互锁校验。
    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            _isExecuting = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await _execute();
        }
        catch (Exception exception)
        {
            // Async ICommand 按设计返回 void。Runtime 传输失败不能从输入事件逸出并击穿
            // WPF Dispatcher；宿主会将展示标记为不可用，同时保留物理停止入口。
            ExecutionFailed?.Invoke(this, exception);
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
