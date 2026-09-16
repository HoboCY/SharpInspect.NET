using System.Windows;
using System.Windows.Controls;

namespace SharpInspect.Wpf;

public partial class StorageRetentionPanel : UserControl
{
    private StorageRetentionViewModel? _observed;

    public StorageRetentionPanel()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is StorageRetentionViewModel previous)
            {
                Detach(previous);
                previous.Deactivate();
            }
            Attach(args.NewValue as StorageRetentionViewModel);
            ClearSensitiveInputs();
        };
    }

    /// <summary>Attaches the retention surface without adding another public constructor.</summary>
    public void SetViewModel(StorageRetentionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SetViewModel(viewModel));
            return;
        }
        if (ReferenceEquals(DataContext, viewModel)) Attach(viewModel);
        else DataContext = viewModel;
        ClearSensitiveInputs();
    }

    public void Deactivate()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(Deactivate);
            return;
        }
        (DataContext as StorageRetentionViewModel)?.Deactivate();
        ClearSensitiveInputs();
    }

    private void Attach(StorageRetentionViewModel? viewModel)
    {
        if (ReferenceEquals(_observed, viewModel)) return;
        Detach(_observed);
        _observed = viewModel;
        if (_observed is not null) _observed.SensitiveInputsInvalidated += SensitiveInputsInvalidated;
    }

    private void Detach(StorageRetentionViewModel? viewModel)
    {
        if (viewModel is not null) viewModel.SensitiveInputsInvalidated -= SensitiveInputsInvalidated;
        if (ReferenceEquals(_observed, viewModel)) _observed = null;
    }

    private void SensitiveInputsInvalidated(object? sender, EventArgs args) => ClearSensitiveInputs();
    private void ClearSensitiveInputs() => StepUpPasswordBox.Clear();

    private async void PlaceHoldClick(object sender, RoutedEventArgs args) => await SubmitAsync(true, false, false);
    private async void ReleaseHoldClick(object sender, RoutedEventArgs args) => await SubmitAsync(false, true, false);
    private async void ExtendClick(object sender, RoutedEventArgs args) => await SubmitAsync(false, false, true);

    private async Task SubmitAsync(bool placeHold, bool releaseHold, bool extend)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            if (DataContext is not StorageRetentionViewModel model) return;
            if (placeHold) await model.PlaceHoldWithStepUpAsync(password);
            else if (releaseHold) await model.ReleaseHoldWithStepUpAsync(password);
            else if (extend) await model.ExtendWithStepUpAsync(password);
        }
        finally
        {
            password = string.Empty;
            StepUpPasswordBox.Clear();
        }
    }
}
