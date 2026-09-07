using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public partial class IdentityPanel : UserControl
{
    private bool _recoveryKitDisplayed;

    public IdentityPanel()
    {
        InitializeComponent();
        DataContextChanged += IdentityDataContextChanged;
    }

    public void ClearSensitiveInputs()
    {
        BootstrapTokenBox.Clear();
        BootstrapPasswordBox.Clear();
        LoginPasswordBox.Clear();
        BootstrapUserNameBox.Clear();
        BootstrapDisplayNameBox.Clear();
        LoginUserNameBox.Clear();
        RecoveryKitDisplayBox.Clear();
        _recoveryKitDisplayed = false;
        if (DataContext is IdentityViewModel viewModel)
        {
            viewModel.CancelPendingOperation();
        }

        ApplyState();
    }

    private void IdentityDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is IdentityViewModel oldViewModel)
            oldViewModel.PropertyChanged -= IdentityChanged;
        if (e.NewValue is IdentityViewModel newViewModel)
            newViewModel.PropertyChanged += IdentityChanged;
        ClearSensitiveInputs();
        ApplyState();
    }

    private void IdentityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyState();

    private void ApplyState()
    {
        var viewModel = DataContext as IdentityViewModel;
        var available = viewModel is not null;
        UnavailablePanel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        AvailablePanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        BootstrapPanel.Visibility = viewModel?.BootstrapRequired == true
            ? Visibility.Visible : Visibility.Collapsed;
        BootstrapButton.IsEnabled = viewModel?.CanBootstrap == true;
        LoginButton.IsEnabled = viewModel?.CanAuthenticate == true;
        RevealRecoveryKitButton.IsEnabled = viewModel?.CanRevealRecoveryKit == true;
        RecoveryPanel.Visibility = viewModel?.HasRecoveryKit == true || _recoveryKitDisplayed
            ? Visibility.Visible : Visibility.Collapsed;
        CopyRecoveryKitButton.IsEnabled = _recoveryKitDisplayed && RecoveryKitDisplayBox.Text.Length != 0;
        ClearRecoveryKitButton.IsEnabled = _recoveryKitDisplayed || viewModel?.HasRecoveryKit == true;
    }

    private async void CreateAdministratorClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IdentityViewModel viewModel) return;
        var token = BootstrapTokenBox.Password;
        var userName = BootstrapUserNameBox.Text;
        var displayName = BootstrapDisplayNameBox.Text;
        var password = BootstrapPasswordBox.Password;
        BootstrapTokenBox.Clear();
        BootstrapPasswordBox.Clear();
        BootstrapUserNameBox.Clear();
        BootstrapDisplayNameBox.Clear();
        await viewModel.CreateFirstAdministratorAsync(token, userName, displayName, password);
        ApplyState();
    }

    private async void AuthenticateClick(object sender, RoutedEventArgs e)
        => await AuthenticateFromInputsAsync();

    private async Task AuthenticateFromInputsAsync()
    {
        if (DataContext is not IdentityViewModel viewModel) return;
        var userName = LoginUserNameBox.Text;
        var password = LoginPasswordBox.Password;
        LoginUserNameBox.Clear();
        LoginPasswordBox.Clear();
        await viewModel.AuthenticateAsync(userName, password);
        ApplyState();
    }

    internal async Task SubmitLoginSmokeAsync(string userName, string password)
    {
        LoginUserNameBox.Text = userName;
        LoginPasswordBox.Password = password;
        await AuthenticateFromInputsAsync();
        if (LoginPasswordBox.Password.Length != 0) throw new InvalidOperationException("IdentityPasswordInputNotCleared");
    }

    private void RevealRecoveryKitClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not IdentityViewModel viewModel) return;
        var secret = viewModel.RevealRecoveryKit();
        if (secret is null) return;
        RecoveryKitDisplayBox.Text = secret;
        _recoveryKitDisplayed = true;
        ApplyState();
    }

    private void CopyRecoveryKitClick(object sender, RoutedEventArgs e)
    {
        if (!_recoveryKitDisplayed || RecoveryKitDisplayBox.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(RecoveryKitDisplayBox.Text);
            ClearRecoveryKitDisplay();
        }
        catch (ExternalException)
        {
            // Keep the display available for a manual copy if the desktop clipboard is busy.
        }
    }

    private void ClearRecoveryKitClick(object sender, RoutedEventArgs e) => ClearRecoveryKitDisplay();

    private void ClearRecoveryKitDisplay()
    {
        RecoveryKitDisplayBox.Clear();
        _recoveryKitDisplayed = false;
        if (DataContext is IdentityViewModel viewModel) viewModel.ClearTransientSecrets();
        ApplyState();
    }
}
