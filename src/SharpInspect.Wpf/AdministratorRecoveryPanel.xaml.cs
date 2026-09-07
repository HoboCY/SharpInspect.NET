using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public partial class AdministratorRecoveryPanel : UserControl
{
    private readonly AdministratorRecoveryViewModel _fallbackViewModel;
    private string? _displayedKit;
    private string? _displayedKitPrincipalId;
    private Guid? _displayedKitSessionId;
    private bool _kitCopied;

    public AdministratorRecoveryPanel() : this(null) { }

    public AdministratorRecoveryPanel(AdministratorRecoveryViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new AdministratorRecoveryViewModel(
            null, null, new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += RecoveryDataContextChanged;
        AttachViewModel(DataContext as AdministratorRecoveryViewModel);
        ApplyState();
    }

    public AdministratorRecoveryViewModel ViewModel =>
        DataContext as AdministratorRecoveryViewModel ?? _fallbackViewModel;

    /// <summary>
    /// Clears every PasswordBox and the local one-time kit presentation. It also
    /// invalidates a pending UI operation without changing Runtime state.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        RecoveryCodeBox.Clear();
        RecoveryUserNameBox.Clear();
        RecoveryDisplayNameBox.Clear();
        RecoveryPasswordBox.Clear();
        RotatePasswordBox.Clear();
        CustodyConfirmationBox.Clear();
        ClearDisplayedKitPresentation("尚未显示新恢复码。");
        _kitCopied = false;
        KitCopyStatusLabel.Text = string.Empty;
        ViewModel.CancelPendingOperations();
        ViewModel.ClearSensitiveState();
        ApplyState();
    }

    private void RecoveryDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AdministratorRecoveryViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(e.NewValue as AdministratorRecoveryViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(AdministratorRecoveryViewModel? viewModel)
    {
        if (viewModel is not null) viewModel.PropertyChanged += ViewModelChanged;
    }

    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyState();

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        ApplyState();
    }

    private async void RecoverClick(object sender, RoutedEventArgs e)
    {
        var recoveryCode = RecoveryCodeBox.Password;
        var userName = RecoveryUserNameBox.Text;
        var displayName = RecoveryDisplayNameBox.Text;
        var newPassword = RecoveryPasswordBox.Password;
        try
        {
            ClearRecoveryInputs();
            await ViewModel.RecoverAdministratorAsync(recoveryCode, userName, displayName, newPassword);
        }
        finally
        {
            // The clear is deliberately repeated in finally for exceptional and
            // cancellation paths as well as ordinary Runtime results.
            ClearRecoveryInputs();
            ApplyState();
        }
    }

    private async void RotateClick(object sender, RoutedEventArgs e)
    {
        var password = RotatePasswordBox.Password;
        try
        {
            RotatePasswordBox.Clear();
            await ViewModel.RotateRecoveryKitAsync(password);
        }
        finally
        {
            RotatePasswordBox.Clear();
            ApplyState();
        }
    }

    private async void ConfirmCustodyClick(object sender, RoutedEventArgs e)
    {
        var confirmationCode = CustodyConfirmationBox.Password;
        try
        {
            CustodyConfirmationBox.Clear();
            await ViewModel.ConfirmRecoveryKitCustodyAsync(confirmationCode);
        }
        finally
        {
            CustodyConfirmationBox.Clear();
            ApplyState();
        }
    }

    private void RevealKitClick(object sender, RoutedEventArgs e)
    {
        if (_displayedKit is not null) return;
        if (!ViewModel.TryRevealRecoveryKit(out var kit, out var principalId, out var sessionId) ||
            kit is null)
        {
            KitCopyStatusLabel.Text = "新恢复码已不可显示，请重新轮换。";
            ApplyState();
            return;
        }

        // Check the captured issuing owner after the atomic consume and before
        // writing the secret into a visual control. Never rebind it to a newer
        // Current session.
        if (!ViewModel.IsRecoveryKitSessionCurrent(principalId, sessionId))
        {
            KitDisplayLabel.Text = "恢复码显示已清除。";
            KitCopyStatusLabel.Text = "当前会话已变化，请重新认证后轮换。";
            ApplyState();
            return;
        }

        _displayedKit = kit;
        _displayedKitPrincipalId = principalId;
        _displayedKitSessionId = sessionId;
        KitDisplayLabel.Text = kit;
        KitCopyStatusLabel.Text = "恢复码已在本页显示一次，请确认受控保管。";
        ApplyState();
    }

    private void CopyKitClick(object sender, RoutedEventArgs e)
    {
        if (_kitCopied) return;
        if (_displayedKit is null)
        {
            RevealKitClick(sender, e);
            if (_displayedKit is null) return;
        }

        if (!ViewModel.IsRecoveryKitSessionCurrent(_displayedKitPrincipalId, _displayedKitSessionId))
        {
            ClearDisplayedKitPresentation("恢复码显示已清除：当前会话已变化。请重新认证后轮换。");
            ApplyState();
            return;
        }

        try
        {
            Clipboard.SetText(_displayedKit);
            _kitCopied = true;
            KitCopyStatusLabel.Text = "恢复码已复制一次；请在确认托管后清除本页。";
        }
        catch
        {
            KitCopyStatusLabel.Text = "复制不可用，请手工输入页面中的恢复码。";
        }
        ApplyState();
    }

    private void ClearKitClick(object sender, RoutedEventArgs e)
    {
        ClearDisplayedKitPresentation("恢复码显示已清除。");
        _kitCopied = false;
        KitCopyStatusLabel.Text = string.Empty;
        ViewModel.ClearSensitiveState();
        ApplyState();
    }

    private void ClearRecoveryInputs()
    {
        RecoveryCodeBox.Clear();
        RecoveryUserNameBox.Clear();
        RecoveryDisplayNameBox.Clear();
        RecoveryPasswordBox.Clear();
    }

    private void ApplyState()
    {
        var viewModel = ViewModel;
        if (_displayedKit is not null &&
            !viewModel.IsRecoveryKitSessionCurrent(_displayedKitPrincipalId, _displayedKitSessionId))
        {
            ClearDisplayedKitPresentation("恢复码显示已清除：当前会话已变化。");
        }

        UnavailablePanel.Visibility = viewModel.IsConfigured && !viewModel.IsUnavailable
            ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = viewModel.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = viewModel.StatusLabel;
        ReasonLabel.Text = viewModel.StatusReasonCode is { Length: > 0 } reason
            ? $"原因：{reason} · KitState={viewModel.KitState}"
            : $"KitState={viewModel.KitState}";
        StatusMessageLabel.Text = viewModel.StatusMessage;
        ErrorLabel.Text = viewModel.ErrorCode ?? string.Empty;
        RecoverButton.IsEnabled = viewModel.CanRecover;
        RotateButton.IsEnabled = viewModel.CanRotateRecoveryKit;
        ConfirmCustodyButton.IsEnabled = viewModel.CanConfirmRecoveryKitCustody;
        RevealKitButton.IsEnabled = viewModel.CanRevealRecoveryKit && _displayedKit is null;
        CopyKitButton.IsEnabled = !_kitCopied && (_displayedKit is not null || viewModel.CanRevealRecoveryKit);
        ClearKitButton.IsEnabled = _displayedKit is not null || viewModel.HasRecoveryKit;
    }

    private void ClearDisplayedKitPresentation(string message)
    {
        _displayedKit = null;
        _displayedKitPrincipalId = null;
        _displayedKitSessionId = null;
        _kitCopied = false;
        KitDisplayLabel.Text = message;
        KitCopyStatusLabel.Text = string.Empty;
    }
}
