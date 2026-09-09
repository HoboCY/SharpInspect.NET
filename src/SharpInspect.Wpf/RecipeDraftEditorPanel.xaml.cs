using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SharpInspect.Abstractions;

namespace SharpInspect.Wpf;

public partial class RecipeDraftEditorPanel : UserControl
{
    private readonly RecipeDraftEditorViewModel _fallbackViewModel;
    private readonly RecipeDraftCustomEditorHost _customEditorHost;
    private RecipeDraftEditorViewModel? _observedViewModel;
    private bool _applyingState;
    private bool _applyStateAgain;

    public RecipeDraftEditorPanel() : this(null, null) { }

    public RecipeDraftEditorPanel(RecipeDraftEditorViewModel? viewModel)
        : this(viewModel, null) { }

    public RecipeDraftEditorPanel(RecipeDraftEditorViewModel? viewModel,
        AlgorithmConfigurationEditorRegistry? registry)
    {
        InitializeComponent();
        AddHandler(DataObject.PastingEvent, new DataObjectPastingEventHandler(TextPasting), true);
        _fallbackViewModel = viewModel ?? new RecipeDraftEditorViewModel(
            null, null, new DispatcherUiDispatcher());
        DataContext = viewModel ?? _fallbackViewModel;
        _customEditorHost = new RecipeDraftCustomEditorHost(ViewModel, registry,
            CustomEditorContentControl, ApplyState);
        DataContextChanged += PanelDataContextChanged;
        IsVisibleChanged += PanelVisibilityChanged;
        Unloaded += PanelUnloaded;
        AttachViewModel(DataContext as RecipeDraftEditorViewModel);
        ApplyState();
    }

    public RecipeDraftEditorViewModel ViewModel =>
        DataContext as RecipeDraftEditorViewModel ?? _fallbackViewModel;

    public IReadOnlyList<RecipeAssetKind> AssetKinds => ViewModel.AssetKinds;
    public IReadOnlyList<RecipePolicyKind> PolicyKinds => ViewModel.PolicyKinds;

    public bool TryUseCustomEditor()
    {
        _customEditorHost.TryUseCustomEditor();
        ApplyState();
        return _customEditorHost.IsCustomActive;
    }

    public void UseGenericEditor()
    {
        _customEditorHost.UseGenericEditor();
        ApplyState();
    }

    /// <summary>Clears the transient editor projection when this page is hidden or locked.</summary>
    public void ClearSensitiveInputs()
    {
        StepUpPasswordBox.Clear();
        MigrationStepUpPasswordBox.Clear();
        ReleaseStepUpPasswordBox.Clear();
        _customEditorHost.UseGenericEditor();
        ViewModel.CancelPendingOperations();
        ViewModel.ClearTransientState();
        EditorScrollViewer.ScrollToHome();
        ApplyState();
    }

    internal void ScrollToEditorForSmoke()
    {
        EditorScrollViewer.ScrollToHome();
        UpdateLayout();
        DraftPanel.BringIntoView();
        EditorScrollViewer.UpdateLayout();
    }

    private void PanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is RecipeDraftEditorViewModel oldViewModel)
            oldViewModel.PropertyChanged -= ViewModelChanged;
        AttachViewModel(args.NewValue as RecipeDraftEditorViewModel);
        ApplyState();
    }

    private void AttachViewModel(RecipeDraftEditorViewModel? viewModel)
    {
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged -= ViewModelChanged;
        _observedViewModel = viewModel ?? _fallbackViewModel;
        if (_observedViewModel is not null)
            _observedViewModel.PropertyChanged += ViewModelChanged;
        _customEditorHost.AttachViewModel(_observedViewModel ?? _fallbackViewModel);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args) => ApplyState();

    private void PanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is bool visible && !visible)
            InvalidateCustomEditorPresentation();
    }

    private void PanelUnloaded(object sender, RoutedEventArgs args) =>
        InvalidateCustomEditorPresentation();

    private void InvalidateCustomEditorPresentation()
    {
        MigrationStepUpPasswordBox.Clear();
        ReleaseStepUpPasswordBox.Clear();
        _customEditorHost.UseGenericEditor();
    }

    private void TextPasting(object sender, DataObjectPastingEventArgs args)
    {
        if (!args.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
        if (args.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
        var textBox = FindTextBox(args.OriginalSource as DependencyObject);
        if (textBox is null) return;
        var propertyName = BindingOperations.GetBinding(textBox, TextBox.TextProperty)?.Path?.Path;
        if (!RecipeDraftInputBounds.TryGetBounds(textBox.DataContext, propertyName,
                out var maximumCharacters, out var maximumBytes)) return;

        if (RecipeDraftInputBounds.TryComposeReplacement(textBox.Text,
                textBox.SelectionStart, textBox.SelectionLength, pasted,
                maximumCharacters, maximumBytes, out _)) return;

        args.CancelCommand();
        switch (textBox.DataContext)
        {
            case RecipeDraftEditorViewModel viewModel when propertyName is not null:
                viewModel.RejectPastedInput(propertyName);
                break;
            case RecipeDraftFieldViewModel field:
                field.RejectPastedInput();
                break;
            case RecipeDraftAssetRequirementViewModel asset when propertyName is not null:
                asset.RejectPastedInput(propertyName);
                break;
            case RecipeDraftPolicyRequirementViewModel policy when propertyName is not null:
                policy.RejectPastedInput(propertyName);
                break;
        }
    }

    private static TextBox? FindTextBox(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBox textBox) return textBox;
            try { source = VisualTreeHelper.GetParent(source); }
            catch (ArgumentException) { return null; }
        }
        return null;
    }

    private void ApplyState()
    {
        if (_applyingState)
        {
            _applyStateAgain = true;
            return;
        }

        _applyingState = true;
        try
        {
            var viewModel = ViewModel;
            var configured = viewModel.IsConfigured;
            var customActive = _customEditorHost.IsCustomActive;
            UnavailablePanel.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
            ConfiguredPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
            StateLabel.Text = viewModel.IsBusy ? "正在读取…" : viewModel.IsUnavailable ? "不可用 / 未授权" : "可编辑视图";
            StatusText.Text = viewModel.StatusMessage;
            ErrorText.Text = viewModel.ErrorCode ?? string.Empty;
            DependencyLabel.Text = $"依赖状态：{viewModel.DependenciesStatus} · 发布资格：{(viewModel.CanRelease ? "可用" : "不可用")}";
            StepUpLabel.Text = viewModel.RequiresStepUp
                ? viewModel.HasStepUpService
                    ? "保存需当前密码再次确认；确认后才会提交本次草稿操作。"
                    : "保存需再次确认，但当前未配置确认服务。"
                : string.Empty;
            ReleaseStepUpLabel.Text = viewModel.ReleaseRequiresStepUp
                ? viewModel.HasStepUpService
                    ? "发布需当前密码新鲜确认；治理策略和精确 Draft 修订会绑定到本次操作。"
                    : "发布需要再次确认，但当前未配置确认服务。"
                : string.Empty;
            var editable = configured && viewModel.HasDraft && !viewModel.IsBusy;
            // The algorithm selector must remain usable before a draft exists; the
            // schema field surface itself is disabled until New Draft/Open succeeds.
            DraftPanel.IsEnabled = configured && !viewModel.IsBusy;
            FieldsItemsControl.IsEnabled = editable && !customActive;
            GenericEditorContainer.Visibility = customActive ? Visibility.Collapsed : Visibility.Visible;
            CustomEditorContentControl.Visibility = customActive ? Visibility.Visible : Visibility.Collapsed;
            CustomEditorContentControl.IsEnabled = configured && !viewModel.IsBusy;
            CustomEditorButton.IsEnabled = configured && _customEditorHost.CanUseCustomEditor;
            GenericEditorButton.IsEnabled = configured && !viewModel.IsBusy;
            CustomEditorStatusText.Text = _customEditorHost.StatusText;
            RefreshButton.IsEnabled = configured && viewModel.CanRefresh;
            NewDraftButton.IsEnabled = configured && viewModel.CanCreateDraft;
            OpenButton.IsEnabled = configured && viewModel.CanOpenSelected;
            NextHistoryButton.IsEnabled = configured && viewModel.CanNextHistory;
            ValidateButton.IsEnabled = configured && viewModel.CanValidate;
            SaveButton.IsEnabled = configured && viewModel.CanSave;
            SaveWithStepUpButton.IsEnabled = configured && viewModel.CanSaveWithStepUp;
            ReleaseButton.IsEnabled = configured && viewModel.CanRelease &&
                !viewModel.ReleaseRequiresStepUp;
            ReleaseStepUpButton.IsEnabled = configured && viewModel.CanReleaseWithStepUp;
            ReleaseStepUpPasswordBox.IsEnabled = configured && !viewModel.IsBusy;
            MigrationStepUpPasswordBox.IsEnabled = configured && !viewModel.IsBusy;
            MigrationStepUpButton.IsEnabled = configured && viewModel.CanStepUpMigrate;
            if (!configured || !viewModel.IsAuthenticated)
            {
                MigrationStepUpPasswordBox.Clear();
                ReleaseStepUpPasswordBox.Clear();
            }
            UnavailableText.Text = configured
                ? "请选择算法并新建草稿，或打开已有草稿。"
                : "配方草稿编辑不可用：未配置受限编辑服务。";
            _customEditorHost.Refresh();
            CustomEditorStatusText.Text = _customEditorHost.StatusText;
        }
        finally
        {
            _applyingState = false;
            if (_applyStateAgain)
            {
                _applyStateAgain = false;
                ApplyState();
            }
        }
    }

    private void CustomEditorClick(object sender, RoutedEventArgs args) => TryUseCustomEditor();

    private void GenericEditorClick(object sender, RoutedEventArgs args) => UseGenericEditor();

    private void FieldActionClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || button.DataContext is not RecipeDraftFieldViewModel field)
            return;
        if (string.Equals(button.Tag as string, "Default", StringComparison.Ordinal))
            ViewModel.UseAuthoringDefault(field);
        else
            ViewModel.ClearField(field);
    }

    private void AddAssetClick(object sender, RoutedEventArgs args) => ViewModel.AddAssetRequirement();
    private void AddPolicyClick(object sender, RoutedEventArgs args) => ViewModel.AddPolicyRequirement();

    private async void SaveWithStepUpClick(object sender, RoutedEventArgs args)
    {
        var password = StepUpPasswordBox.Password;
        StepUpPasswordBox.Clear();
        try
        {
            await ViewModel.SaveWithStepUpAsync(password);
        }
        finally
        {
            StepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private async void ReleaseStepUpClick(object sender, RoutedEventArgs args)
    {
        var password = ReleaseStepUpPasswordBox.Password;
        ReleaseStepUpPasswordBox.Clear();
        try
        {
            await ViewModel.ReleaseWithStepUpAsync(password).ConfigureAwait(true);
        }
        finally
        {
            ReleaseStepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private async void MigrationStepUpClick(object sender, RoutedEventArgs args)
    {
        var password = MigrationStepUpPasswordBox.Password;
        MigrationStepUpPasswordBox.Clear();
        try
        {
            await ViewModel.MigrateWithStepUpAsync(password);
        }
        finally
        {
            MigrationStepUpPasswordBox.Clear();
            ApplyState();
        }
    }

    private void RemoveRequirementClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button) return;
        if (button.DataContext is RecipeDraftAssetRequirementViewModel asset)
            ViewModel.RemoveAssetRequirement(asset);
        else if (button.DataContext is RecipeDraftPolicyRequirementViewModel policy)
            ViewModel.RemovePolicyRequirement(policy);
    }
}

public sealed class RecipeDraftFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ChoiceTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not RecipeDraftFieldViewModel field) return TextTemplate;
        if (field.Type == AlgorithmScalarType.Boolean) return BooleanTemplate ?? TextTemplate;
        if (field.HasChoiceValues) return ChoiceTemplate ?? TextTemplate;
        return TextTemplate;
    }
}
