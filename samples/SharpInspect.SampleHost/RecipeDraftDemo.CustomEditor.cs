using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static partial class RecipeDraftDemo
{
    private const string CustomEditorOutputRun =
        "V129-N01 custom-editor PASS revisions=3 exactSchema=true invalidRejected=true semanticRejected=true fallback=true contextRevoked=true activeUnchanged=true ready=false";
    private const string CustomEditorOutputRestart =
        "V129-N02 custom-editor-restart PASS revisions=3 originalSchema=true factoryRegistered=false customEditorRegistered=false canRelease=false";

    internal static int RunCustomEditor(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
                throw new InvalidOperationException("DraftConsumerPasswordRequired");
            Pump(() => RunCustomEditorCore(options, Path.GetFullPath(directory), userName!,
                expectedPrincipal!, password));
            Console.WriteLine(CustomEditorOutputRun);
            return 0;
        }
        catch (DraftDemoCheckException exception)
        {
            Console.Error.WriteLine("V129-N01 custom-editor FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("V129-N01 custom-editor FAIL reason=RecipeDraftCustomEditorCheckFailed");
            return 1;
        }
    }

    internal static int QueryCustomEditor(ProductionStoreOptions options, string directory)
    {
        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            var evidencePath = Path.Combine(fullDirectory, "custom-editor-evidence.json");
            var evidence = ReadCustomEditorEvidence(evidencePath);
            var query = new SqliteRecipeDraftQuery(options);
            var page = query.QueryAsync(new(PageSize: 20)).AsTask().GetAwaiter().GetResult();
            Require(page.Available && page.Revisions.Count == 3 && page.NextAfterPosition is null,
                "CustomEditorRestartCountInvalid");

            var revisions = page.Revisions.OrderBy(item => item.Revision).ToArray();
            Require(revisions.All(item => item.DraftId == evidence.DraftId),
                "CustomEditorRestartDraftMismatch");
            Require(revisions.Select(item => item.RevisionContentHash).SequenceEqual(evidence.RevisionHashes),
                "CustomEditorRestartRevisionHashMismatch");
            Require(revisions.Select(item => item.Content.Configuration.ContentHash)
                    .SequenceEqual(evidence.ConfigurationHashes),
                "CustomEditorRestartConfigurationHashMismatch");
            Require(revisions.All(item => item.AuthorPrincipalId == evidence.AuthorPrincipalId &&
                !item.Published && !item.Active && !item.CanRelease &&
                item.DependencyValidation == "NotRun"), "CustomEditorRestartAuthorityChanged");
            Require(revisions.All(item => item.Content.Algorithm.ConfigurationSchema.ContentHash == evidence.SchemaHash),
                "CustomEditorRestartSchemaChanged");
            Require(revisions.All(item => item.Content.Configuration.Validate(
                item.Content.Algorithm.ConfigurationSchema).Count == 0),
                "CustomEditorRestartTypedConfigurationInvalid");

            var first = revisions[0];
            var custom = revisions[1];
            var fallback = revisions[2];
            Require(Count(first) == 20 && Count(custom) == 30 && Count(fallback) == 30,
                "CustomEditorRestartCountValueInvalid");
            Require(Label(fallback) == "通用回退保存", "CustomEditorRestartFallbackValueInvalid");
            Require(SameExcept(first, custom, "count") && SameExcept(custom, fallback, "label"),
                "CustomEditorRestartFieldsChanged");

            WriteJson(Path.Combine(fullDirectory, "custom-editor-restart.json"), new
            {
                result = "Pass",
                schema = 9,
                revisions = 3,
                draftId = evidence.DraftId,
                authorPrincipalId = evidence.AuthorPrincipalId,
                originalSchema = true,
                factoryRegistered = false,
                customEditorRegistered = false,
                migratorRegistered = false,
                canRelease = false,
                published = false,
                active = false,
                dependencyValidation = "NotRun",
                productionReady = false,
                readOnlyQuery = true,
                revisionHashes = evidence.RevisionHashes,
                configurationHashes = evidence.ConfigurationHashes,
                typedConfiguration = true,
                authorsVerified = true
            });
            Console.WriteLine(CustomEditorOutputRestart);
            return 0;
        }
        catch (DraftDemoCheckException exception)
        {
            Console.Error.WriteLine("V129-N02 custom-editor-restart FAIL reason=" + exception.ReasonCode);
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("V129-N02 custom-editor-restart FAIL reason=RecipeDraftCustomEditorRestartCheckFailed");
            return 1;
        }
    }

    private static async Task RunCustomEditorCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password)
    {
        Require(File.Exists(options.DatabasePath), "DraftConsumerRequiresPreparedIdentityStore");
        Directory.CreateDirectory(directory);
        var algorithmFactory = CreateFactory();
        var customFactory = new SampleCustomEditorFactory(algorithmFactory.Descriptor);
        var registry = new AlgorithmConfigurationEditorRegistry(new IAlgorithmConfigurationEditorFactory[]
        {
            customFactory
        });
        var services = new ServiceCollection();
        services.AddSingleton(algorithmFactory);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        await Verified(runtime);
        var before = await runtime.GetSnapshotAsync();
        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new(userName, password));
        Require(login.Succeeded && login.Identity?.PrincipalId.ToString("D") == expectedPrincipal,
            "DraftConsumerAuthenticationFailed");
        await Verified(runtime);

        var editor = new ObservedDraftEditor(provider.GetRequiredService<IRecipeDraftEditor>());
        var query = provider.GetRequiredService<IRecipeDraftHistoryQuery>();
        await using var viewModel = new RecipeDraftEditorViewModel(editor, sessions,
            new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher), ExecutionPolicy);
        var panel = new RecipeDraftEditorPanel(viewModel, registry);
        var window = new Window
        {
            Content = panel,
            Width = 1280,
            Height = 960,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };
        try
        {
            window.Show();
            await Flush();
            var algorithms = Descendants<ComboBox>(panel).First(control =>
                control.Items.Count != 0 && control.Items[0] is AlgorithmDescriptor);
            algorithms.SelectedItem = editor.Algorithms.Single();
            Invoke(panel, viewModel.NewDraftCommand);
            await Until(() => viewModel.HasDraft && viewModel.Fields.Count == 7);
            await viewModel.RefreshAsync();
            panel.UpdateLayout();
            SetInput(panel, viewModel, "RecipeKey", "CustomEditorRecipe");
            SetInput(panel, viewModel, "DisplayName", "受限自定义编辑器验证草稿");
            SetInput(panel, viewModel, "RoiWidthText", "64");
            SetInput(panel, viewModel, "RoiHeightText", "48");
            SetInput(panel, viewModel, "AlgorithmExecutionTimeoutText", "500");
            SetInput(panel, viewModel, "ChangeReason", "通用编辑器初始保存");
            Require(viewModel.IsValid && viewModel.SaveCommand.CanExecute(null),
                "CustomEditorGenericInitialStateInvalid");

            Invoke(panel, viewModel.SaveCommand);
            await Until(() => viewModel.CurrentRevision?.Revision == 1 ||
                (!viewModel.IsBusy && viewModel.ErrorCode is not null));
            Require(viewModel.CurrentRevision?.Revision == 1,
                "CustomEditorGenericInitialSaveFailed_" + (editor.LastSaveReason ?? viewModel.ErrorCode ?? "NoReason"));
            await Verified(runtime);
            var first = viewModel.CurrentRevision!;
            Require(Count(first) == 20, "CustomEditorGenericInitialCountInvalid");

            await viewModel.RefreshAsync();
            Require(panel.TryUseCustomEditor(), "CustomEditorRegistrationNotResolved");
            var customSession = customFactory.LastSession;
            var customContext = customFactory.LastContext;
            Require(customSession is not null && customContext is not null,
                "CustomEditorSessionNotCreated");
            var customInitial = customContext!.Read();
            Require(customInitial is not null && customInitial.Configuration is not null,
                "CustomEditorInitialSnapshotUnavailable");

            var applied = customSession!.ReplaceCountText("30");
            Require(applied.Applied && applied.ReasonCode == "CustomEditorConfigurationApplied",
                "CustomEditorValidReplacementRejected");
            SetInput(panel, viewModel, "ChangeReason", "自定义编辑器保存");
            Invoke(panel, viewModel.SaveCommand);
            await Until(() => viewModel.CurrentRevision?.Revision == 2 ||
                (!viewModel.IsBusy && viewModel.ErrorCode is not null));
            Require(viewModel.CurrentRevision?.Revision == 2,
                "CustomEditorSaveFailed_" + (editor.LastSaveReason ?? viewModel.ErrorCode ?? "NoReason"));
            await Verified(runtime);
            var customRevision = viewModel.CurrentRevision!;
            Require(Count(customRevision) == 30 && SameExcept(first, customRevision, "count"),
                "CustomEditorChangedUnrelatedFields");

            // Save/reload revokes the prior editor capability. Explicitly select it again
            // before testing the two rejection paths, so a factory is never run implicitly.
            await viewModel.RefreshAsync();
            var invalidContext = customFactory.LastContext;
            Require(panel.TryUseCustomEditor(), "CustomEditorReselectFailed");
            var invalidSession = customFactory.LastSession!;
            invalidContext = customFactory.LastContext!;
            var hashBeforeInvalid = viewModel.DraftContentHash;
            var invalid = invalidSession.ReplaceCountText("not-an-integer");
            Require(!invalid.Applied && invalid.ReasonCode == "CustomEditorConfigurationInvalid",
                "CustomEditorInvalidSchemaAccepted");
            Require(viewModel.DraftContentHash == hashBeforeInvalid && !viewModel.CanSave,
                "CustomEditorInvalidSchemaChangedDraft");
            var afterInvalid = await query.QueryAsync(new(PageSize: 20));
            Require(afterInvalid.Available && afterInvalid.Revisions.Count == 2,
                "CustomEditorInvalidSchemaWroteRevision");

            var semantic = invalidSession.ReplaceCountText("5");
            Require(semantic.Applied, "CustomEditorSemanticCandidateRejectedTooEarly");
            var semanticResult = await invalidSession.ValidateCurrentAsync();
            Require(!semanticResult.Valid && semanticResult.Issues.Any(issue =>
                    issue.Code == "SampleThresholdMustBeBelowCount"),
                "CustomEditorSemanticInvalidAccepted");
            var afterSemantic = await query.QueryAsync(new(PageSize: 20));
            Require(afterSemantic.Available && afterSemantic.Revisions.Count == 2 && !viewModel.CanSave,
                "CustomEditorSemanticRejectionWroteRevision");
            Require(invalidSession.ReplaceCountText("30").Applied,
                "CustomEditorSemanticRecoveryRejected");
            Require((await invalidSession.ValidateCurrentAsync()).Valid,
                "CustomEditorSemanticRecoveryInvalid");

            var failureSnapshot = invalidContext.Read();
            var hashBeforeFailure = viewModel.DraftContentHash;
            Require(failureSnapshot is not null, "CustomEditorFailureSnapshotUnavailable");
            invalidSession.ReportFailure();
            Require(invalidContext.Read() is null, "CustomEditorFailureDidNotRevokeContext");
            var lateWrite = invalidContext.TryReplace(failureSnapshot!.EditRevision, failureSnapshot.Values);
            Require(!lateWrite.Applied && lateWrite.ReasonCode == "CustomEditorContextUnavailable" &&
                viewModel.DraftContentHash == hashBeforeFailure,
                "CustomEditorRevokedContextWroteDraft");
            panel.UseGenericEditor();

            customFactory.ThrowOnCreate = true;
            Require(!panel.TryUseCustomEditor(), "CustomEditorInitializationFailureAccepted");
            customFactory.ThrowOnCreate = false;
            SetInput(panel, viewModel, "ChangeReason", "通用回退保存");
            SetInput(panel, viewModel.Fields.Single(field => field.Key == "label"), "InputText", "通用回退保存");
            await viewModel.RefreshAsync();
            Require(viewModel.IsValid && viewModel.SaveCommand.CanExecute(null),
                "CustomEditorFallbackGenericUnavailable");
            Invoke(panel, viewModel.SaveCommand);
            await Until(() => viewModel.CurrentRevision?.Revision == 3 ||
                (!viewModel.IsBusy && viewModel.ErrorCode is not null));
            Require(viewModel.CurrentRevision?.Revision == 3,
                "CustomEditorFallbackSaveFailed_" + (editor.LastSaveReason ?? viewModel.ErrorCode ?? "NoReason"));
            await Verified(runtime);
            var fallbackRevision = viewModel.CurrentRevision!;
            Require(Count(fallbackRevision) == 30 && Label(fallbackRevision) == "通用回退保存" &&
                SameExcept(customRevision, fallbackRevision, "label"),
                "CustomEditorFallbackChangedUnrelatedFields");

            // Refreshing the host must revoke the editor without constructing a new one.
            var factoryCallsBeforeRefresh = customFactory.CreateCalls;
            await viewModel.RefreshAsync();
            Require(customFactory.CreateCalls == factoryCallsBeforeRefresh,
                "CustomEditorWasImplicitlyRecreatedAfterRefresh");
            Require(panel.TryUseCustomEditor(), "CustomEditorExplicitReselectAfterRefreshFailed");
            Require(customFactory.CreateCalls == factoryCallsBeforeRefresh + 1,
                "CustomEditorExplicitReselectNotObserved");
            await Flush();
            SaveWindow(window, Path.Combine(directory, "custom-editor.png"));
            panel.UseGenericEditor();

            var after = await runtime.GetSnapshotAsync();
            Require(before.ActiveRecipe == after.ActiveRecipe && before.Ready == after.Ready &&
                !before.Ready && !after.Ready, "CustomEditorChangedProductionAuthority");
            var history = await query.QueryAsync(new(PageSize: 20));
            Require(history.Available && history.Revisions.Count == 3,
                "CustomEditorFinalHistoryInvalid");
            var revisions = history.Revisions.OrderBy(item => item.Revision).ToArray();
            WriteJson(Path.Combine(directory, "custom-editor-evidence.json"), new
            {
                result = "Pass",
                schema = 9,
                revisions = 3,
                draftId = fallbackRevision.DraftId,
                authorPrincipalId = fallbackRevision.AuthorPrincipalId,
                authors = revisions.Select(item => item.AuthorPrincipalId).ToArray(),
                algorithmId = fallbackRevision.Content.Algorithm.Algorithm.Id,
                algorithmVersion = fallbackRevision.Content.Algorithm.Algorithm.Version,
                schemaId = fallbackRevision.Content.Algorithm.ConfigurationSchema.Id,
                schemaVersion = fallbackRevision.Content.Algorithm.ConfigurationSchema.Version,
                schemaHash = fallbackRevision.Content.Algorithm.ConfigurationSchema.ContentHash,
                editorContract = new
                {
                    id = customFactory.Descriptor.Editor.Id,
                    version = customFactory.Descriptor.Editor.Version,
                    contentHash = customFactory.Descriptor.Editor.ContentHash
                },
                revisionHashes = revisions.Select(item => item.RevisionContentHash).ToArray(),
                configurationHashes = revisions.Select(item => item.Content.Configuration.ContentHash).ToArray(),
                invalidSchemaRejected = true,
                semanticRejected = true,
                contextRevoked = true,
                factoryInitializationFallback = true,
                explicitReselectAfterRefresh = true,
                unrelatedFieldsPreserved = true,
                activeUnchanged = before.ActiveRecipe == after.ActiveRecipe,
                readyUnchanged = before.Ready == after.Ready,
                published = revisions.Any(item => item.Published),
                active = revisions.Any(item => item.Active),
                canRelease = revisions.Any(item => item.CanRelease),
                dependencyValidation = "NotRun",
                productionReady = false,
                factoryRegistered = true,
                customEditorRegistered = true,
                migratorRegistered = false
            });
        }
        finally
        {
            window.Close();
        }
    }

    private static CustomEditorEvidence ReadCustomEditorEvidence(string path)
    {
        Require(File.Exists(path), "CustomEditorEvidenceMissing");
        var value = JsonSerializer.Deserialize<CustomEditorEvidence>(File.ReadAllText(path), EvidenceJsonOptions);
        Require(value is not null && value.Result == "Pass" && value.Revisions == 3,
            "CustomEditorEvidenceInvalid");
        return value!;
    }

    private static long Count(RecipeDraftRevision revision) => revision.Content.Configuration.Values
        .Single(value => value.Key == "count").Value.AsInt64();

    private static string Label(RecipeDraftRevision revision) => revision.Content.Configuration.Values
        .Single(value => value.Key == "label").Value.AsString();

    private static bool SameExcept(RecipeDraftRevision left, RecipeDraftRevision right, string changedKey)
    {
        if (left.DraftId != right.DraftId ||
            left.Content.RecipeKey != right.Content.RecipeKey ||
            left.Content.CameraRole != right.Content.CameraRole ||
            left.Content.Camera != right.Content.Camera ||
            left.Content.AlgorithmExecutionTimeout != right.Content.AlgorithmExecutionTimeout ||
            left.Content.Algorithm.Algorithm != right.Content.Algorithm.Algorithm ||
            left.Content.Algorithm.ConfigurationSchema.ContentHash !=
                right.Content.Algorithm.ConfigurationSchema.ContentHash ||
            left.Content.AssetRequirements.Count != right.Content.AssetRequirements.Count ||
            left.Content.PolicyRequirements.Count != right.Content.PolicyRequirements.Count ||
            left.Content.CalibrationRequirements.Count != right.Content.CalibrationRequirements.Count)
            return false;
        var leftValues = left.Content.Configuration.Values.ToDictionary(value => value.Key,
            StringComparer.Ordinal);
        var rightValues = right.Content.Configuration.Values.ToDictionary(value => value.Key,
            StringComparer.Ordinal);
        return leftValues.Count == rightValues.Count && leftValues.Keys.All(key =>
            key == changedKey || (rightValues.TryGetValue(key, out var value) &&
                leftValues[key].Unit == value.Unit && leftValues[key].Value.Equals(value.Value)));
    }

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, EvidenceJsonOptions));

    private sealed record CustomEditorEvidence(string Result, int Schema, int Revisions, Guid DraftId,
        Guid AuthorPrincipalId, string SchemaHash, string[] RevisionHashes,
        string[] ConfigurationHashes);

    private sealed class SampleCustomEditorFactory : IAlgorithmConfigurationEditorFactory
    {
        internal SampleCustomEditorFactory(AlgorithmDescriptor descriptor)
        {
            Descriptor = new AlgorithmConfigurationEditorDescriptor(
                new RecipeContractReference("Sample.DraftCustomEditor", "1", new string('C', 64)),
                descriptor.Identity,
                new RecipeContractReference(descriptor.ConfigurationSchema.Id,
                    descriptor.ConfigurationSchema.Version, descriptor.ConfigurationSchema.ContentHash));
        }

        public AlgorithmConfigurationEditorDescriptor Descriptor { get; }
        internal bool ThrowOnCreate { get; set; }
        internal int CreateCalls { get; private set; }
        internal IAlgorithmConfigurationDraftEditContext? LastContext { get; private set; }
        internal SampleCustomEditorSession? LastSession { get; private set; }

        public IAlgorithmConfigurationEditorSession Create(IAlgorithmConfigurationDraftEditContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            CreateCalls++;
            if (ThrowOnCreate) throw new InvalidOperationException("SampleCustomEditorInitializationFailed");
            LastContext = context;
            LastSession = new SampleCustomEditorSession(context);
            return LastSession;
        }
    }

    /// <summary>
    /// The sample extension stores only the constrained Draft capability and a WPF view. It has
    /// no Runtime, algorithm factory, store, device, PLC or service-provider reference.
    /// </summary>
    private sealed class SampleCustomEditorSession : IAlgorithmConfigurationEditorSession
    {
        private readonly IAlgorithmConfigurationDraftEditContext _context;
        private readonly TextBlock _status;

        internal SampleCustomEditorSession(IAlgorithmConfigurationDraftEditContext context)
        {
            _context = context;
            var panel = new StackPanel
            {
                Margin = new Thickness(12),
                Background = Brushes.White,
                Orientation = Orientation.Vertical
            };
            panel.Children.Add(new TextBlock
            {
                Text = "受限自定义配置编辑器 · 完整 Schema 替换",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "此视图只能通过宿主 Draft 编辑能力读、替换、验证；保存仍由宿主命令完成。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            panel.Children.Add(new TextBlock { Text = "count (items)" });
            CountBox = new TextBox { MinWidth = 180, Margin = new Thickness(0, 2, 0, 8) };
            panel.Children.Add(CountBox);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var apply = new Button { Content = "应用 count", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            apply.Click += (_, _) => ReplaceCountText(CountBox.Text);
            buttons.Children.Add(apply);
            var validate = new Button { Content = "验证完整配置", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            validate.Click += async (_, _) => await ValidateCurrentAsync();
            buttons.Children.Add(validate);
            var failure = new Button { Content = "报告编辑器故障", Padding = new Thickness(10, 4, 10, 4) };
            failure.Click += (_, _) => ReportFailure();
            buttons.Children.Add(failure);
            panel.Children.Add(buttons);
            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_status);
            View = panel;
        }

        internal TextBox CountBox { get; }
        public FrameworkElement View { get; }

        public void Refresh(AlgorithmConfigurationEditorSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var count = snapshot.Values.SingleOrDefault(value => value.Key == "count");
            CountBox.Text = count is null ? string.Empty : ScalarText(count);
            _status.Text = snapshot.PendingInvalid
                ? "配置未应用：Schema 或语义校验失败。"
                : $"编辑版本 {snapshot.EditRevision} · {snapshot.Values.Count} 个完整字段 · 等待宿主保存";
        }

        internal AlgorithmConfigurationEditorEditResult ReplaceCountText(string text)
        {
            var snapshot = _context.Read();
            if (snapshot is null)
                return new(false, "CustomEditorContextUnavailable");
            var value = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? AlgorithmScalarValue.FromInt64(count)
                : AlgorithmScalarValue.FromString(text);
            var values = snapshot.Values.Select(entry => entry.Key == "count"
                ? new AlgorithmConfigurationEntry(entry.Key, entry.Unit, value) : entry).ToArray();
            var result = _context.TryReplace(snapshot.EditRevision, values);
            _status.Text = result.ReasonCode;
            if (result.Applied && _context.Read() is { } updated) Refresh(updated);
            return result;
        }

        internal async Task<RecipeDraftValidationResult> ValidateCurrentAsync()
        {
            var result = await _context.ValidateAsync();
            _status.Text = result.Valid ? "完整配置验证通过。" : result.ReasonCode;
            return result;
        }

        internal void ReportFailure() => _context.ReportFailure();

        public void Dispose()
        {
            // The host revokes the context when the session is removed. The sample view owns no
            // unmanaged resource and has no authority to revoke or persist the Draft itself.
        }

        private static string ScalarText(AlgorithmConfigurationEntry entry) => entry.Value.Type switch
        {
            AlgorithmScalarType.Int64 => entry.Value.AsInt64().ToString(CultureInfo.InvariantCulture),
            AlgorithmScalarType.Float64 => entry.Value.AsFloat64().ToString("R", CultureInfo.InvariantCulture),
            AlgorithmScalarType.Boolean => entry.Value.AsBoolean().ToString(),
            AlgorithmScalarType.String => entry.Value.AsString(),
            AlgorithmScalarType.Enum => entry.Value.AsEnum(),
            _ => string.Empty
        };
    }
}
