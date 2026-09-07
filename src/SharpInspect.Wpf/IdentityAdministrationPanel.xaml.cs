using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public partial class IdentityAdministrationPanel : UserControl
{
    private readonly IdentityAdministrationViewModel _fallbackViewModel;
    private TaskCompletionSource<RuntimeCommandOutcome?>? _smokeCreateCompletion;

    public IdentityAdministrationPanel() : this(null) { }

    public IdentityAdministrationPanel(IdentityAdministrationViewModel? viewModel)
    {
        InitializeComponent();
        _fallbackViewModel = viewModel ?? new IdentityAdministrationViewModel(
            null, null, null, null, new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        DataContextChanged += AdministrationDataContextChanged;
        AttachViewModel(DataContext as IdentityAdministrationViewModel);
        ApplyState();
    }

    /// <summary>
    /// Clears PasswordBox/TextBox values, permission selection, authority rows,
    /// and any management request that has not reached Runtime submission.
    /// </summary>
    public void ClearSensitiveInputs()
    {
        CreateUserNameBox.Clear();
        CreateDisplayNameBox.Clear();
        CreatePasswordBox.Clear();
        CreateStepUpPasswordBox.Clear();
        ActionStepUpPasswordBox.Clear();
        RebindPasswordBox.Clear();
        PermissionListBox.UnselectAll();
        ViewModel.CancelPendingOperations();
        ViewModel.ClearSensitiveState();
        ApplyState();
    }

    public IdentityAdministrationViewModel ViewModel =>
        DataContext as IdentityAdministrationViewModel ?? _fallbackViewModel;

    internal Task RefreshSmokeAsync() => ViewModel.RefreshAsync();

    internal void ScrollToBottomForSmoke()
    {
        AdministrationScrollViewer.ScrollToEnd();
        UpdateLayout();
        var top = SetPermissionsButton.TransformToAncestor(AdministrationScrollViewer)
            .Transform(new Point(0, 0)).Y;
        if (top < -1 || top + SetPermissionsButton.ActualHeight >
            AdministrationScrollViewer.ViewportHeight + 1)
            throw new InvalidOperationException("IdentityAdministrationPermissionsNotReachable");
    }

    /// <summary>
    /// Drives the same button handler used by the visible create-account form.
    /// The isolated consumer smoke fills the real TextBox/PasswordBox controls,
    /// raises the routed Click event, and observes the Runtime outcome.
    /// </summary>
    internal async Task<RuntimeCommandOutcome?> SubmitCreateSmokeAsync(
        string userName, string displayName, string password, string stepUpPassword)
    {
        if (Visibility != Visibility.Visible) throw new InvalidOperationException("IdentityAdministrationPanelHidden");
        CreateUserNameBox.Text = userName;
        CreateDisplayNameBox.Text = displayName;
        CreatePasswordBox.Password = password;
        CreateStepUpPasswordBox.Password = stepUpPassword;
        RoleComboBox.SelectedItem = HumanRoleBundle.Operator;

        var completion = new TaskCompletionSource<RuntimeCommandOutcome?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _smokeCreateCompletion = completion;
        try
        {
            CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var outcome = await completion.Task.ConfigureAwait(true);
            if (CreateUserNameBox.Text.Length != 0 || CreateDisplayNameBox.Text.Length != 0 ||
                CreatePasswordBox.Password.Length != 0 || CreateStepUpPasswordBox.Password.Length != 0)
                throw new InvalidOperationException("IdentityAdministrationCreateInputsNotCleared");
            return outcome;
        }
        finally
        {
            _smokeCreateCompletion = null;
        }
    }

    private void AdministrationDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IdentityAdministrationViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(e.NewValue as IdentityAdministrationViewModel);
        ClearSensitiveInputs();
        ApplyState();
    }

    private void AttachViewModel(IdentityAdministrationViewModel? viewModel)
    {
        if (viewModel is not null) viewModel.PropertyChanged += ViewModelChanged;
    }

    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyState();

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
        ApplyState();
    }

    private async void CreateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var userName = CreateUserNameBox.Text;
            var displayName = CreateDisplayNameBox.Text;
            var password = CreatePasswordBox.Password;
            var stepUpPassword = CreateStepUpPasswordBox.Password;
            var role = ViewModel.SelectedRoleBundle;
            ClearCreateInputs();
            var outcome = await ViewModel.CreateAccountAsync(userName, displayName, password, role, stepUpPassword);
            ApplyState();
            _smokeCreateCompletion?.TrySetResult(outcome);
        }
        catch (Exception exception)
        {
            if (_smokeCreateCompletion is not null) _smokeCreateCompletion.TrySetException(exception);
            else throw;
        }
    }

    private async void DisableClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } account) return;
        var stepUpPassword = ActionStepUpPasswordBox.Password;
        ActionStepUpPasswordBox.Clear();
        await ViewModel.DisableAccountAsync(account.PrincipalId, stepUpPassword);
        ApplyState();
    }

    private async void UnlockClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } account) return;
        var stepUpPassword = ActionStepUpPasswordBox.Password;
        ActionStepUpPasswordBox.Clear();
        await ViewModel.UnlockAccountAsync(account.PrincipalId, stepUpPassword);
        ApplyState();
    }

    private async void RebindClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } account) return;
        var newPassword = RebindPasswordBox.Password;
        var stepUpPassword = ActionStepUpPasswordBox.Password;
        RebindPasswordBox.Clear();
        ActionStepUpPasswordBox.Clear();
        await ViewModel.RebindAccountAsync(account.PrincipalId, newPassword, stepUpPassword);
        ApplyState();
    }

    private async void SetPermissionsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } account) return;
        var permissions = PermissionListBox.SelectedItems.Cast<Permission>().ToArray();
        var stepUpPassword = ActionStepUpPasswordBox.Password;
        ActionStepUpPasswordBox.Clear();
        PermissionListBox.UnselectAll();
        await ViewModel.SetPermissionsAsync(account.PrincipalId, permissions, stepUpPassword);
        ApplyState();
    }

    private void ClearCreateInputs()
    {
        CreateUserNameBox.Clear();
        CreateDisplayNameBox.Clear();
        CreatePasswordBox.Clear();
        CreateStepUpPasswordBox.Clear();
    }

    private void ApplyState()
    {
        var viewModel = ViewModel;
        var configured = viewModel.IsConfigured;
        UnavailablePanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        ConfiguredPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = viewModel.IsUnavailable ? "不可用" : "已读取";
        StatusMessage.Text = viewModel.StatusMessage;
        ErrorLabel.Text = viewModel.ErrorCode ?? string.Empty;
        CreateButton.IsEnabled = viewModel.CanCreateAccount;
        DisableButton.IsEnabled = viewModel.CanDisableAccount;
        UnlockButton.IsEnabled = viewModel.CanUnlockAccount;
        RebindButton.IsEnabled = viewModel.CanRebindAccount;
        SetPermissionsButton.IsEnabled = viewModel.CanSetPermissions;
    }
}
