using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

internal static partial class RecipeDraftDemo
{
    internal static int RunMigration(ProductionStoreOptions options, string directory, string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
                throw new InvalidOperationException("DraftConsumerPasswordRequired");
            Pump(() => RunMigrationCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!, password));
            Console.WriteLine("V128-N01 draft-migration PASS revisions=3 sourceUnchanged=true lineageRetained=true activeUnchanged=true ready=false");
            return 0;
        }
        catch (DraftDemoCheckException exception)
        { Console.Error.WriteLine("V128-N01 draft-migration FAIL reason=" + exception.ReasonCode); return 1; }
        catch (Exception exception)
        { Console.Error.WriteLine("V128-N01 draft-migration FAIL exception=" + exception.GetType().Name); return 1; }
    }

    private static async Task RunMigrationCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password)
    {
        Require(File.Exists(options.DatabasePath), "DraftConsumerRequiresPreparedIdentityStore");
        var sourceFactory = CreateFactory();
        var targetFactory = new MigrationTargetFactory(sourceFactory.Descriptor);
        var transformer = new SampleConfigurationMigrator(sourceFactory.Descriptor, targetFactory.Descriptor);
        var services = new ServiceCollection();
        services.AddSingleton(sourceFactory);
        services.AddSingleton<IVisionAlgorithmFactory>(targetFactory);
        services.AddSingleton<IAlgorithmConfigurationMigrator>(transformer);
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
        var editor = provider.GetRequiredService<IRecipeDraftEditor>();
        var query = provider.GetRequiredService<IRecipeDraftHistoryQuery>();
        await using var vm = new RecipeDraftEditorViewModel(editor, sessions,
            new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher), ExecutionPolicy, null,
            provider.GetRequiredService<IAlgorithmConfigurationMigrationService>());
        var panel = new RecipeDraftEditorPanel { DataContext = vm };
        var window = new Window { Content = panel, Width = 1280, Height = 1000, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        try
        {
            window.Show();
            vm.SelectedAlgorithm = editor.Algorithms.Single(item => item.Identity.Version == "1");
            Invoke(panel, vm.NewDraftCommand);
            await Until(() => vm.HasDraft && vm.Fields.Count == 7);
            await vm.RefreshAsync();
            SetInput(panel, vm, "RecipeKey", "MigrationSample");
            SetInput(panel, vm, "DisplayName", "显式迁移历史草稿");
            SetInput(panel, vm, "RoiWidthText", "64");
            SetInput(panel, vm, "RoiHeightText", "48");
            SetInput(panel, vm, "AlgorithmExecutionTimeoutText", "500");
            Invoke(panel, vm.SaveCommand);
            await Until(() => !vm.IsBusy && (vm.CurrentRevision is not null || vm.ErrorCode is not null));
            var source = vm.CurrentRevision;
            Require(source is { Revision: 1 }, "MigrationSourceSaveFailed_" + vm.ErrorCode);
            await Verified(runtime);
            await vm.RefreshAsync();
            vm.SelectedHistory = vm.History.Single();
            vm.SelectedMigrationTargetAlgorithm = editor.Algorithms.Single(item => item.Identity.Version == "2");
            vm.SelectedMigrationMigrator = vm.MigrationMigrators.Single();
            Require(vm.MigrateCommand.CanExecute(null), "MigrationUiCommandUnavailable");
            Invoke(panel, vm.MigrateCommand);
            await Until(() => !vm.IsBusy);
            var target = vm.CurrentRevision;
            Require(target is { Revision: 1 } && target.DraftId != source!.DraftId &&
                target.Content.MigrationLineage is not null, "MigrationUiFailed_" + vm.ErrorCode);
            Require(target!.Content.Configuration.Values.Any(value => value.Key == "quantity") &&
                target.Content.Configuration.Values.All(value => value.Key != "count"), "MigrationOutputInvalid");
            Require(vm.MigrationWarnings.Count == 1, "MigrationWarningMissing");
            await Verified(runtime);
            var unchanged = await query.ReadAsync(source!.DraftId, source.Revision);
            Require(unchanged.Available && unchanged.Revision?.RevisionContentHash == source.RevisionContentHash,
                "MigrationSourceChanged");
            await Flush();
            SaveWindow(window, Path.Combine(directory, "draft-migration-editor.png"));

            SetInput(panel, vm, "DisplayName", "迁移后正常修订，保留来源");
            SetInput(panel, vm, "ChangeReason", "本地编辑迁移草稿");
            await vm.RefreshAsync();
            Invoke(panel, vm.SaveCommand);
            await Until(() => !vm.IsBusy);
            var edited = vm.CurrentRevision;
            Require(edited is { Revision: 2 } && edited.DraftId == target.DraftId &&
                edited.Content.MigrationLineage?.ContentHash == target.Content.MigrationLineage!.ContentHash,
                "MigrationOrdinaryEditLostLineage_" + vm.ErrorCode);
            await Verified(runtime);
            var after = await runtime.GetSnapshotAsync();
            Require(before.ActiveRecipe == after.ActiveRecipe && !edited!.Active && !edited.CanRelease &&
                !edited.Published && transformer.Calls == 1, "MigrationProductionAuthorityChanged");
            File.WriteAllText(Path.Combine(directory, "draft-migration-evidence.json"), JsonSerializer.Serialize(new MigrationEvidence(
                source.DraftId, source.RevisionContentHash, source.Content.ContentHash, edited!.DraftId,
                target.RevisionContentHash, edited.RevisionContentHash, target.Content.MigrationLineage!.ContentHash,
                source.Content.Configuration.ContentHash, target.Content.Configuration.ContentHash,
                target.Content.Algorithm.ConfigurationSchema.ContentHash, edited.AuthorPrincipalId)));
        }
        finally { window.Close(); }
    }

    internal static int QueryMigration(ProductionStoreOptions options, string directory)
    {
        try
        {
            var query = new SqliteRecipeDraftQuery(options);
            var page = query.QueryAsync(new(PageSize: 20)).AsTask().GetAwaiter().GetResult();
            var evidence = JsonSerializer.Deserialize<MigrationEvidence>(File.ReadAllText(Path.Combine(directory, "draft-migration-evidence.json")))!;
            Require(page.Available && page.Revisions.Count == 3, "MigrationRestartCountInvalid");
            var source = page.Revisions.Single(item => item.DraftId == evidence.SourceDraftId);
            var targets = page.Revisions.Where(item => item.DraftId == evidence.TargetDraftId).OrderBy(item => item.Revision).ToArray();
            Require(source.RevisionContentHash == evidence.SourceRevisionHash && source.Content.ContentHash == evidence.SourceContentHash &&
                targets.Length == 2 && targets[0].RevisionContentHash == evidence.InitialTargetRevisionHash &&
                targets[1].RevisionContentHash == evidence.EditedTargetRevisionHash &&
                targets.All(item => item.Content.MigrationLineage?.ContentHash == evidence.LineageHash &&
                    item.AuthorPrincipalId == evidence.Actor && !item.Active && !item.Published && !item.CanRelease &&
                    !item.Content.MigrationLineage.ApprovalInherited), "MigrationRestartBindingInvalid");
            File.WriteAllText(Path.Combine(directory, "draft-migration-restart.json"), JsonSerializer.Serialize(new {
                Result = "Pass", Revisions = 3, SourceUnchanged = true, LineageRetained = true,
                AlgorithmFactoryRegistered = false, MigratorRegistered = false, ApprovalInherited = false,
                CanRelease = false, evidence.SourceRevisionHash, evidence.EditedTargetRevisionHash, evidence.LineageHash }));
            Console.WriteLine("V128-N02 draft-migration-restart PASS revisions=3 sourceUnchanged=true lineageRetained=true canRelease=false");
            return 0;
        }
        catch (DraftDemoCheckException exception)
        { Console.Error.WriteLine("V128-N02 draft-migration-restart FAIL reason=" + exception.ReasonCode); return 1; }
        catch (Exception exception)
        { Console.Error.WriteLine("V128-N02 draft-migration-restart FAIL exception=" + exception.GetType().Name); return 1; }
    }

    private sealed record MigrationEvidence(Guid SourceDraftId, string SourceRevisionHash, string SourceContentHash,
        Guid TargetDraftId, string InitialTargetRevisionHash, string EditedTargetRevisionHash, string LineageHash,
        string InputConfigurationHash, string OutputConfigurationHash, string TargetSchemaHash, Guid Actor);

    private sealed class MigrationTargetFactory : IVisionAlgorithmFactory
    {
        internal MigrationTargetFactory(AlgorithmDescriptor source)
        {
            Descriptor = new(new(source.Identity.Id, "2"), new(source.ConfigurationSchema.Id, "2",
                source.ConfigurationSchema.Fields.Select(field => field.Key == "count" ?
                    new AlgorithmFieldDefinition("quantity", field.Type, field.Unit, field.Required, field.Constraints,
                        field.AuthoringDefault, field.HelpText) : field)), source.ResultSchema);
        }
        public AlgorithmDescriptor Descriptor { get; }
        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default)
        {
            var structural = configuration.Validate(Descriptor.ConfigurationSchema);
            if (structural.Count != 0) return ValueTask.FromResult(structural);
            return ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                configuration.Values.Single(item => item.Key == "threshold").Value.AsFloat64() <
                configuration.Values.Single(item => item.Key == "quantity").Value.AsInt64() ? Array.Empty<AlgorithmValidationIssue>() :
                    new[] { new AlgorithmValidationIssue("SampleThresholdMustBeBelowQuantity", "threshold") });
        }
        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("MigrationCannotExecuteAlgorithm");
    }

    private sealed class SampleConfigurationMigrator : IAlgorithmConfigurationMigrator
    {
        internal SampleConfigurationMigrator(AlgorithmDescriptor source, AlgorithmDescriptor target)
        {
            Descriptor = new(new("Sample.CountToQuantity", "1", new string('C', 64)), source.Identity,
                new(source.ConfigurationSchema.Id, source.ConfigurationSchema.Version, source.ConfigurationSchema.ContentHash),
                target.Identity, new(target.ConfigurationSchema.Id, target.ConfigurationSchema.Version, target.ConfigurationSchema.ContentHash));
        }
        public AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
        internal int Calls { get; private set; }
        public ValueTask<AlgorithmConfigurationMigrationTransformResult> MigrateAsync(AlgorithmConfigurationMigrationContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AlgorithmConfigurationMigrationTransformResult(true, "SampleCountRenamed",
                context.SourceConfiguration.Values.Select(item => item.Key == "count" ?
                    new AlgorithmConfigurationEntry("quantity", item.Unit, item.Value) : item),
                new[] { new AlgorithmValidationIssue("SampleReviewQuantityBeforeRelease", "quantity") }));
        }
    }
}
