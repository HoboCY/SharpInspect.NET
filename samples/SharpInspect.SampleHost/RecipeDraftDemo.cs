using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static partial class RecipeDraftDemo
{
    internal static int Run(ProductionStoreOptions options, string directory, string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
                throw new InvalidOperationException("DraftConsumerPasswordRequired");
            Pump(() => RunCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, password));
            Console.WriteLine("V115-N01 draft-editor PASS revisions=2 exactSchema=true invalidRejected=true unauthorizedRejected=true activeUnchanged=true ready=false");
            return 0;
        }
        catch (DraftDemoCheckException exception)
        {
            Console.Error.WriteLine("V115-N01 draft-editor FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("V115-N01 draft-editor FAIL reason=RecipeDraftConsumerCheckFailed");
            return 1;
        }
    }

    internal static int Query(ProductionStoreOptions options, string directory)
    {
        try
        {
            var services = new ServiceCollection();
            services.AddSharpInspectSqliteRuntime(options);
            using var provider = services.BuildServiceProvider();
            var query = provider.GetRequiredService<IRecipeDraftHistoryQuery>();
            var page = query.QueryAsync(new(PageSize: 20)).AsTask().GetAwaiter().GetResult();
            var evidence = JsonSerializer.Deserialize<DraftEvidence>(File.ReadAllText(Path.Combine(directory, "draft-evidence.json")))!;
            Require(page.Available && page.Revisions.Count == 2 && page.NextAfterPosition is null, "DraftRestartCountInvalid");
            var last = page.Revisions.Single(row => row.Revision == 2);
            Require(last.DraftId == evidence.DraftId && last.RevisionContentHash == evidence.RevisionHash &&
                last.Content.Configuration.ContentHash == evidence.ConfigurationHash &&
                last.Content.Algorithm.ConfigurationSchema.ContentHash == evidence.SchemaHash &&
                last.AuthorPrincipalId == evidence.AuthorPrincipalId && !last.CanRelease && !last.Active &&
                last.DependencyValidation == "NotRun", "DraftRestartBindingInvalid");
            File.WriteAllText(Path.Combine(directory, "draft-restart.json"), JsonSerializer.Serialize(new
            { Revisions = 2, OriginalSchema = true, AlgorithmFactoryRegistered = false, CanRelease = false,
                evidence.DraftId, evidence.RevisionHash, evidence.ConfigurationHash, evidence.SchemaHash }));
            Console.WriteLine("V115-N02 draft-restart PASS revisions=2 originalSchema=true factoryRegistered=false canRelease=false");
            return 0;
        }
        catch (DraftDemoCheckException exception)
        {
            Console.Error.WriteLine("V115-N02 draft-restart FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("V115-N02 draft-restart FAIL reason=RecipeDraftRestartCheckFailed");
            return 1;
        }
    }

    private static async Task RunCore(ProductionStoreOptions options, string directory, string userName,
        string expectedPrincipal, string password)
    {
        Require(File.Exists(options.DatabasePath), "DraftConsumerRequiresPreparedIdentityStore");
        var services = new ServiceCollection();
        services.AddSingleton(CreateFactory());
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        await Verified(runtime);
        var before = await runtime.GetSnapshotAsync();
        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new(userName, password));
        Require(login.Succeeded && login.Identity?.PrincipalId.ToString("D") == expectedPrincipal, "DraftConsumerAuthenticationFailed");
        await Verified(runtime);
        var editor = new ObservedDraftEditor(provider.GetRequiredService<IRecipeDraftEditor>());
        var query = provider.GetRequiredService<IRecipeDraftHistoryQuery>();
        await using var vm = new RecipeDraftEditorViewModel(editor, sessions,
            new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher), ExecutionPolicy);
        var panel = new RecipeDraftEditorPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 1280, Height = 960, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        try
        {
            window.Show();
            await Flush();
            var algorithms = Descendants<ComboBox>(panel).First(control => control.Items.Count != 0 && control.Items[0] is AlgorithmDescriptor);
            algorithms.SelectedItem = editor.Algorithms.Single();
            Invoke(panel, vm.NewDraftCommand);
            await Until(() => vm.HasDraft && vm.Fields.Count == 7);
            await vm.RefreshAsync();
            panel.UpdateLayout();
            SetInput(panel, vm, "RecipeKey", "SampleRecipe");
            SetInput(panel, vm, "DisplayName", "通用编辑器验证草稿");
            SetInput(panel, vm, "RoiWidthText", "64");
            SetInput(panel, vm, "RoiHeightText", "48");
            SetInput(panel, vm, "AlgorithmExecutionTimeoutText", "500");
            var count = vm.Fields.Single(field => field.Key == "count");
            SetInput(panel, count, "InputText", "not-an-integer");
            Require(!vm.IsValid && !vm.SaveCommand.CanExecute(null), "DraftUiInvalidValueAccepted");
            var initial = await query.QueryAsync(new());
            Require(initial.Available && initial.Revisions.Count == 0, "DraftInvalidUiWroteHistory");

            SetInput(panel, count, "InputText", "40");
            SetInput(panel, vm.Fields.Single(field => field.Key == "threshold"), "InputText", "12.5");
            SetInput(panel, vm.Fields.Single(field => field.Key == "tag"), "InputText", "manual-tag");
            var optionalFlag = vm.Fields.Single(field => field.Key == "optionalFlag");
            var flagControl = Descendants<CheckBox>(panel).Single(control => ReferenceEquals(control.DataContext, optionalFlag));
            flagControl.IsChecked = false;
            flagControl.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateSource();
            var mode = vm.Fields.Single(field => field.Key == "mode");
            Descendants<ComboBox>(panel).Single(control => ReferenceEquals(control.DataContext, mode)).SelectedItem = "accurate";
            InvokeButton(Descendants<Button>(panel).Single(button => Equals(button.Content, "添加资产")));
            await Until(() => vm.AssetRequirements.Count == 1);
            var asset = vm.AssetRequirements.Single();
            SetInput(panel, asset, "Role", "MainCalibration");
            SetInput(panel, asset, "ContractId", "Sample.RequiredCalibration");
            SetInput(panel, asset, "ContractVersion", "1");
            SetInput(panel, asset, "ContractHash", new string('A', 64));
            InvokeButton(Descendants<Button>(panel).Single(button => Equals(button.Content, "添加政策")));
            await Until(() => vm.PolicyRequirements.Count == 2);
            var imagePolicy = vm.PolicyRequirements.Last();
            Descendants<ComboBox>(panel).Single(control => ReferenceEquals(control.DataContext, imagePolicy)).SelectedItem = RecipePolicyKind.ImageAcquisition;
            SetInput(panel, imagePolicy, "ContractId", "Sample.RequiredImagePolicy");
            SetInput(panel, imagePolicy, "ContractVersion", "1");
            SetInput(panel, imagePolicy, "ContractHash", new string('B', 64));
            SetInput(panel, vm.Fields.Single(field => field.Key == "threshold"), "InputText", "99");
            Invoke(panel, vm.ValidateCommand);
            await Until(() => !vm.IsBusy && vm.ErrorCode is not null);
            Require(!vm.IsValid && !vm.SaveCommand.CanExecute(null), "DraftSemanticInvalidValueAccepted");
            var afterSemanticRejection = await query.QueryAsync(new());
            Require(afterSemanticRejection.Available && afterSemanticRejection.Revisions.Count == 0,
                "DraftSemanticRejectionWroteHistory");
            SetInput(panel, vm.Fields.Single(field => field.Key == "threshold"), "InputText", "12.5");
            await vm.RefreshAsync();
            Invoke(panel, vm.SaveCommand);
            await Until(() => vm.CurrentRevision?.Revision == 1 || (!vm.IsBusy && vm.ErrorCode is not null));
            Require(vm.CurrentRevision?.Revision == 1, "DraftFirstSaveFailed_" +
                (editor.LastSaveReason ?? vm.ErrorCode ?? "NoReason"));
            await Verified(runtime);
            var first = vm.CurrentRevision!;
            Require(first.Content.Configuration.Values.Single(value => value.Key == "tag").Value.Type == AlgorithmScalarType.Enum &&
                !first.Content.Configuration.Values.Single(value => value.Key == "optionalFlag").Value.AsBoolean(), "DraftScalarTypesLost");
            Require(first.Content.AssetRequirements.Count == 1 && first.Content.PolicyRequirements.Count == 2 &&
                first.DependencyValidation == "NotRun", "DraftMissingDependenciesWereAssumedSatisfied");

            var invocation = new CommandInvocation(CommandSource.PhysicalConsole, sessions.Current.PrincipalId, sessions.Current.SessionId);
            var corrupted = new AlgorithmConfigurationSnapshot(first.Content.Configuration.SchemaId,
                first.Content.Configuration.SchemaVersion, first.Content.Configuration.SchemaContentHash,
                first.Content.Configuration.CanonicalizationVersion, first.Content.Configuration.ContentHash,
                first.Content.Configuration.Values.Concat(new[] {
                    new AlgorithmConfigurationEntry("undeclared", "none", AlgorithmScalarValue.FromInt64(1)) }));
            var invalidContent = CopyContent(first.Content, corrupted);
            var invalid = await editor.SaveAsync(new(Guid.NewGuid(), first.DraftId, 1, first.RevisionContentHash,
                invalidContent, "验证未知字段拒绝", invocation));
            Require(!invalid.Saved, "DraftApiUnknownFieldAccepted");

            await vm.RefreshAsync();
            vm.SelectedHistory = vm.History.Single(row => row.Revision.Revision == 1);
            Invoke(panel, vm.OpenSelectedCommand);
            await Until(() => !vm.IsBusy && vm.CurrentRevision?.Revision == 1);
            Require(vm.CurrentRevision!.Content.Configuration.ContentHash == first.Content.Configuration.ContentHash,
                "DraftReopenHashChanged");
            panel.UpdateLayout();
            SetInput(panel, vm.Fields.Single(field => field.Key == "label"), "InputText", "重新打开后编辑");
            SetInput(panel, vm, "ChangeReason", "验证第二次追加草稿");
            await vm.RefreshAsync();
            Invoke(panel, vm.SaveCommand);
            await Until(() => vm.CurrentRevision?.Revision == 2 || (!vm.IsBusy && vm.ErrorCode is not null));
            Require(vm.CurrentRevision?.Revision == 2, "DraftSecondSaveFailed_" +
                (editor.LastSaveReason ?? vm.ErrorCode ?? "NoReason"));
            await Verified(runtime);
            var last = vm.CurrentRevision!;
            Require(last.AuthorPrincipalId.ToString("D") == expectedPrincipal &&
                last.PreviousRevisionContentHash == first.RevisionContentHash && !last.CanRelease && !last.Active,
                "DraftProvenanceInvalid");
            Invoke(panel, vm.RefreshCommand);
            await Until(() => !vm.IsBusy && vm.History.Count == 2);
            Descendants<ListBox>(panel).Single().SelectedItem = vm.History.Single(row => row.Revision.Revision == 2);
            await Flush();
            SaveWindow(window, Path.Combine(directory, "draft-editor.png"));
            Descendants<Button>(panel).Single(button => ReferenceEquals(button.Command, vm.SaveCommand)).BringIntoView();
            await Flush();
            SaveWindow(window, Path.Combine(directory, "draft-editor-dependencies.png"));
            await sessions.LockAsync(sessions.Current.SessionId, SessionLockReason.UserRequested);
            await Verified(runtime);
            var denied = await editor.SaveAsync(new(Guid.NewGuid(), last.DraftId, 2, last.RevisionContentHash,
                last.Content, "已锁定会话不得保存", invocation));
            Require(!denied.Saved, "DraftLockedSessionAccepted");
            var page = await query.QueryAsync(new());
            var after = await runtime.GetSnapshotAsync();
            Require(page.Available && page.Revisions.Count == 2 && before.ActiveRecipe == after.ActiveRecipe &&
                !before.Ready && !after.Ready, "DraftChangedProductionAuthority");
            File.WriteAllText(Path.Combine(directory, "draft-evidence.json"), JsonSerializer.Serialize(new DraftEvidence(
                last.DraftId, last.RevisionContentHash, last.Content.Configuration.ContentHash,
                last.Content.Algorithm.ConfigurationSchema.ContentHash, last.AuthorPrincipalId)));
        }
        finally { window.Close(); }
    }

    private static RecipeDraftContent CopyContent(RecipeDraftContent content, AlgorithmConfigurationSnapshot configuration) =>
        new(content.RecipeKey, content.DisplayName, content.Algorithm, configuration, content.CameraRole,
            content.Camera, content.AlgorithmExecutionTimeout, content.AssetRequirements, content.PolicyRequirements);

    private static void SetInput(DependencyObject root, object context, string property, string value)
    {
        var control = Descendants<TextBox>(root).Single(box => ReferenceEquals(box.DataContext, context) &&
            box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == property);
        control.Text = value;
        control.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
    }

    private static void Invoke(DependencyObject root, System.Windows.Input.ICommand command)
    {
        var button = Descendants<Button>(root).Single(item => ReferenceEquals(item.Command, command));
        InvokeButton(button);
    }

    private static void InvokeButton(Button button)
    {
        Require(button.IsEnabled, "DraftConsumerButtonUnavailable");
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task Until(Func<bool> complete)
    {
        var started = Stopwatch.StartNew();
        do { await Task.Delay(20); if (started.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(); }
        while (!complete());
        await Flush();
    }

    private static async Task Verified(IStationRuntime runtime)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            var state = (await runtime.GetSnapshotAsync()).AuditIntegrity?.State;
            if (state == AuditIntegrityState.Verified) return;
            Require(state != AuditIntegrityState.Faulted && started.Elapsed < TimeSpan.FromSeconds(20), "DraftAuditUnavailable");
            await Task.Delay(20);
        }
    }

    private static async Task Flush() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Pump(Func<Task> operation)
    {
        Exception? failure = null;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try { await operation(); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new DraftDemoCheckException(reason); }
    private sealed class DraftDemoCheckException : Exception
    {
        internal DraftDemoCheckException(string reasonCode) { ReasonCode = reasonCode; }
        internal string ReasonCode { get; }
    }
    // Observe only the Runtime's stable result code; never capture inputs,
    // passwords, factory issue text, exceptions or configuration values.
    private sealed class ObservedDraftEditor : IRecipeDraftEditor
    {
        private readonly IRecipeDraftEditor _inner;
        internal ObservedDraftEditor(IRecipeDraftEditor inner) { _inner = inner; }
        internal string? LastSaveReason { get; private set; }
        public IReadOnlyList<AlgorithmDescriptor> Algorithms => _inner.Algorithms;
        public IReadOnlyList<AlgorithmConfigurationEntry> GetAuthoringDefaults(AlgorithmIdentity algorithm) =>
            _inner.GetAuthoringDefaults(algorithm);
        public ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            _inner.GetAccessAsync(invocation, cancellationToken);
        public ValueTask<RecipeDraftValidationResult> ValidateAsync(RecipeDraftContent content, CancellationToken cancellationToken = default) =>
            _inner.ValidateAsync(content, cancellationToken);
        public async ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.SaveAsync(request, cancellationToken);
            LastSaveReason = result.ReasonCode;
            return result;
        }
        public ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(draftId, revision, cancellationToken);
        public ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter, CancellationToken cancellationToken = default) =>
            _inner.QueryAsync(filter, cancellationToken);
    }
    private sealed record DraftEvidence(Guid DraftId, string RevisionHash, string ConfigurationHash,
        string SchemaHash, Guid AuthorPrincipalId);
}
