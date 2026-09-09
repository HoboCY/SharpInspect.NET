using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
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

/// <summary>
/// External-consumer demonstration for the immutable Recipe Release boundary.
/// The sample uses only public Abstractions, Runtime, and WPF contracts; the
/// release ledger is enabled solely by the explicit deployment policy supplied
/// in ProductionStoreOptions.
/// </summary>
internal static class RecipeReleaseDemo
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static int Run(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal, string? scenario)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null") ??
                throw new InvalidOperationException("RecipeReleaseConsumerPasswordRequired");
            var selected = NormalizeScenario(scenario, options.RecipeReleases?.Policy.Mode);
            Pump(() => RunCore(options, Path.GetFullPath(directory), userName!, expectedPrincipal!,
                password, selected));
            Console.WriteLine(OutputLine(selected));
            return 0;
        }
        catch (RecipeReleaseDemoCheckException exception)
        {
            Console.Error.WriteLine($"{CaseId(NormalizeScenario(scenario, options.RecipeReleases?.Policy.Mode))} recipe-release FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{CaseId(NormalizeScenario(scenario, options.RecipeReleases?.Policy.Mode))} recipe-release FAIL reason=RecipeReleaseConsumerCheckFailed");
            WriteFailureLocation(exception);
            return 1;
        }
    }

    internal static int Query(ProductionStoreOptions options, string directory, string? scenario)
    {
        try
        {
            var selected = NormalizeScenario(scenario, options.RecipeReleases?.Policy.Mode);
            QueryCore(options, Path.GetFullPath(directory), selected).GetAwaiter().GetResult();
            Console.WriteLine($"{CaseId(selected)}-Q recipe-release-query PASS readOnly=true databaseUnchanged=true");
            return 0;
        }
        catch (RecipeReleaseDemoCheckException exception)
        {
            Console.Error.WriteLine($"{CaseId(NormalizeScenario(scenario, options.RecipeReleases?.Policy.Mode))}-Q recipe-release-query FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{CaseId(NormalizeScenario(scenario, options.RecipeReleases?.Policy.Mode))}-Q recipe-release-query FAIL reason=RecipeReleaseQueryCheckFailed");
            WriteFailureLocation(exception);
            return 1;
        }
    }

    private static async Task RunCore(ProductionStoreOptions options, string directory,
        string userName, string expectedPrincipal, string password, string scenario)
    {
        Require(options.RecipeDrafts is not null && options.RecipeReleases is not null,
            "RecipeReleaseStoreNotConfigured");
        Require(File.Exists(options.DatabasePath), "RecipeReleaseConsumerRequiresPreparedIdentityStore");
        var draftOptions = options.RecipeDrafts!;
        var releaseOptions = options.RecipeReleases!;
        Directory.CreateDirectory(directory);

        var factory = RecipeDraftDemo.CreateFactory();
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        await Verified(runtime).ConfigureAwait(true);
        var before = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new(userName, password)).ConfigureAwait(true);
        Require(login.Succeeded && login.Identity?.PrincipalId.ToString("D") == expectedPrincipal,
            "RecipeReleaseConsumerAuthenticationFailed");
        var authorSession = sessions.Current;
        Require(authorSession.State == InteractiveSessionState.Authenticated &&
            authorSession.SessionId is not null &&
            string.Equals(authorSession.PrincipalId, expectedPrincipal, StringComparison.OrdinalIgnoreCase),
            "RecipeReleaseAuthorSessionUnavailable");
        await Verified(runtime).ConfigureAwait(true);

        var editor = provider.GetRequiredService<IRecipeDraftEditor>();
        var realReleaseService = provider.GetRequiredService<IRecipeReleaseService>();
        IRecipeReleaseService targetReleaseService = scenario == "concurrent"
            ? new ConcurrentDraftMutationReleaseService(realReleaseService, editor)
            : realReleaseService;
        var releaseService = new ObservedReleaseService(targetReleaseService);
        await using var viewModel = new RecipeDraftEditorViewModel(editor, sessions,
            new DispatcherUiDispatcher(Dispatcher.CurrentDispatcher), draftOptions.ExecutionPolicy,
            provider.GetRequiredService<IStepUpAuthentication>(), null, releaseService);
        var panel = new RecipeDraftEditorPanel(viewModel);
        var window = new Window
        {
            Content = panel,
            Width = 1280,
            Height = 1000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };

        var databaseBefore = HashFile(options.DatabasePath);
        Guid? releaseSessionId = authorSession.SessionId;
        try
        {
            window.Show();
            await Flush().ConfigureAwait(true);
            viewModel.SelectedAlgorithm = editor.Algorithms.Single();
            await viewModel.RefreshAsync().ConfigureAwait(true);
            ConfigureDraft(viewModel, releaseOptions.Policy, scenario);
            Require(viewModel.IsValid && viewModel.CanSave, "RecipeReleaseDraftUiUnavailable");
            var saved = await viewModel.SaveAsync().ConfigureAwait(true);
            Require(saved?.Saved == true && saved.Revision is not null,
                "RecipeReleaseDraftSaveFailed_" + (saved?.ReasonCode ?? viewModel.ErrorCode ?? "NoReason"));
            var source = viewModel.CurrentRevision ??
                throw new RecipeReleaseDemoCheckException("RecipeReleaseSourceRevisionMissing");
            await Verified(runtime).ConfigureAwait(true);

            if (scenario == "maker-checker")
            {
                var loggedOut = await sessions.LogoutAsync(authorSession.SessionId).ConfigureAwait(true);
                Require(loggedOut.Succeeded, "RecipeReleaseAuthorLogoutFailed");
                await Flush().ConfigureAwait(true);
                var approverLogin = await sessions.SignInAsync(new(userName, password)).ConfigureAwait(true);
                Require(approverLogin.Succeeded &&
                    approverLogin.Identity?.PrincipalId.ToString("D") == expectedPrincipal &&
                    approverLogin.Session.SessionId is { } approverSessionId &&
                    approverSessionId != authorSession.SessionId,
                    "RecipeReleaseApproverSessionNotIndependent");
                releaseSessionId = approverLogin.Session.SessionId;
                await Flush().ConfigureAwait(true);
                await viewModel.RefreshAsync().ConfigureAwait(true);
                var reopened = viewModel.History.SingleOrDefault(item =>
                    item.Revision.DraftId == source.DraftId &&
                    item.Revision.Revision == source.Revision &&
                    item.Revision.RevisionContentHash == source.RevisionContentHash);
                Require(reopened is not null, "RecipeReleaseSourceRevisionNotRetainedAfterSessionChange");
                viewModel.SelectedHistory = reopened;
                await viewModel.OpenSelectedAsync().ConfigureAwait(true);
                Require(viewModel.CurrentRevision is { } restored &&
                    restored.DraftId == source.DraftId && restored.Revision == source.Revision &&
                    restored.RevisionContentHash == source.RevisionContentHash,
                    "RecipeReleaseSourceRevisionReopenFailed");
            }

            var releasePasswordBox = panel.FindName("ReleaseStepUpPasswordBox") as PasswordBox;
            var releaseButton = panel.FindName("ReleaseStepUpButton") as Button;
            Require(releasePasswordBox is not null && releaseButton is not null,
                "RecipeReleaseStepUpPanelMissing");
            releasePasswordBox!.Password = password;
            Require(viewModel.CanReleaseWithStepUp && releaseButton!.IsEnabled,
                "RecipeReleaseStepUpUnavailable");
            InvokeButton(releaseButton!);
            await Until(() => !viewModel.IsBusy &&
                (viewModel.ReleasedRecipe is not null || viewModel.ErrorCode is not null)).ConfigureAwait(true);
            Require(string.Equals(releasePasswordBox!.Password, string.Empty, StringComparison.Ordinal),
                "RecipeReleaseStepUpPasswordNotCleared");

            var releases = await releaseService.QueryAsync(new(PageSize: 20)).ConfigureAwait(true);
            var after = await runtime.GetSnapshotAsync().ConfigureAwait(true);
            var expectedRejection = scenario switch
            {
                "maker-checker" => "RecipeReleaseMakerCheckerConflict",
                "concurrent" => "RecipeReleaseDraftRevisionConflict",
                "dependency" => "RecipeReleaseAssetAuthorityUnavailable",
                _ => null
            };
            if (expectedRejection is null)
            {
                var released = viewModel.ReleasedRecipe;
                Require(released is { Available: true } && viewModel.ErrorCode is null,
                    "RecipeReleaseNotAvailable_" + (viewModel.ErrorCode ?? "NoReason"));
                Require(releases.Available && releases.Recipes.Count == 1,
                    "RecipeReleaseHistoryMissing");
                var stored = releases.Recipes.Single();
                Require(stored.Record.ContentHash == released!.Record.ContentHash,
                    "RecipeReleaseHistoryHashMismatch");
                Require(before.ActiveRecipe == after.ActiveRecipe &&
                    before.ArmState == ProductionArmState.Disarmed &&
                    after.ArmState == ProductionArmState.Disarmed && !after.Ready,
                    "RecipeReleaseChangedProductionAuthority");
            }
            else
            {
                Require(viewModel.ReleasedRecipe is null &&
                    string.Equals(viewModel.ErrorCode, expectedRejection, StringComparison.Ordinal),
                    "RecipeReleaseExpectedRejectionMissing_" + (viewModel.ErrorCode ?? "NoReason"));
                Require(releases.Available && releases.Recipes.Count == 0,
                    "RecipeReleaseRejectedHistoryNotEmpty");
                Require(before.ActiveRecipe == after.ActiveRecipe &&
                    before.ArmState == ProductionArmState.Disarmed &&
                    after.ArmState == ProductionArmState.Disarmed && !after.Ready,
                    "RecipeReleaseRejectedChangedProductionAuthority");
            }

            Require(releaseService.ReleaseCalls == 1, "RecipeReleaseUnexpectedExecutionCount");
            releaseButton!.BringIntoView();
            await Flush().ConfigureAwait(true);
            var screenshotName = "release-editor.png";
            SaveWindow(window, Path.Combine(directory, screenshotName));
            var evidence = BuildEvidence(options, scenario, source, viewModel,
                releases, before, after, databaseBefore, HashFile(options.DatabasePath), screenshotName,
                expectedRejection, releaseSessionId, releaseService.ReleaseCalls);
            await File.WriteAllTextAsync(Path.Combine(directory, "release-evidence.json"),
                JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(true);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ConfigureDraft(RecipeDraftEditorViewModel viewModel,
        RecipeGovernancePolicy policy, string scenario)
    {
        viewModel.RecipeKey = "RecipeReleaseSample";
        viewModel.DisplayName = "外部 NuGet 发布治理草稿";
        viewModel.CameraRole = "Primary";
        viewModel.AddPolicyRequirement();
        var governance = viewModel.PolicyRequirements[^1];
        governance.Kind = RecipePolicyKind.RecipeGovernance;
        governance.ContractId = policy.Id;
        governance.ContractVersion = policy.Version;
        governance.ContractHash = policy.ContentHash;
        if (scenario == "dependency")
        {
            viewModel.AddAssetRequirement();
            var missing = viewModel.AssetRequirements[^1];
            missing.Kind = RecipeAssetKind.AlgorithmModel;
            missing.Role = "Missing.Release.Model";
            missing.ContractId = "Sample.Missing.Release.Model";
            missing.ContractVersion = "1";
            missing.ContractHash = new string('A', 64);
        }
    }

    private static ReleaseEvidence BuildEvidence(ProductionStoreOptions options, string scenario,
        RecipeDraftRevision source, RecipeDraftEditorViewModel viewModel,
        ReleasedRecipePage releases, StationStateSnapshot before, StationStateSnapshot after,
        string databaseBefore, string databaseAfter, string screenshotName, string? expectedRejection,
        Guid? releaseSessionId, int executionCount)
    {
        var released = viewModel.ReleasedRecipe;
        var record = released?.Record;
        var releaseOptions = options.RecipeReleases!;
        return new ReleaseEvidence(
            "Pass", CaseId(scenario), scenario, releaseOptions.Policy.Id,
            releaseOptions.Policy.Version, releaseOptions.Policy.Mode.ToString(),
            releaseOptions.Policy.ContentHash, released is not null,
            released?.Available == true, released?.Reference.Id, released?.Reference.Version,
            released?.Reference.ContentHash, record?.ContentHash, source.DraftId, source.Revision,
            source.RevisionContentHash, source.AuthorPrincipalId, source.AuthorSessionId,
            record?.ApproverPrincipalId, record?.ApproverSessionId, releaseSessionId,
            record?.AuthorizationTarget,
            viewModel.ErrorCode, expectedRejection, releases.Recipes.Count, executionCount,
            before.ActiveRecipe == after.ActiveRecipe, before.ArmState == after.ArmState,
            after.ArmState == ProductionArmState.Armed,
            before.Ready, after.Ready, released is not null, after.ActiveRecipe is not null, after.Ready, screenshotName,
            databaseBefore, databaseAfter, "InteractiveSessionLogin",
            releaseSessionId is { } actual && actual != source.AuthorSessionId);
    }

    private static async Task QueryCore(ProductionStoreOptions options, string directory,
        string scenario)
    {
        Require(options.RecipeReleases is not null && options.RecipeDrafts is not null,
            "RecipeReleaseQueryStoreNotConfigured");
        var evidencePath = Path.Combine(directory, "release-evidence.json");
        Require(File.Exists(evidencePath), "RecipeReleaseEvidenceMissing");
        var evidence = JsonSerializer.Deserialize<ReleaseEvidence>(
            await File.ReadAllTextAsync(evidencePath).ConfigureAwait(true), JsonOptions)
            ?? throw new RecipeReleaseDemoCheckException("RecipeReleaseEvidenceInvalid");
        Require(evidence.Result == "Pass" && evidence.Scenario == scenario,
            "RecipeReleaseEvidenceScenarioMismatch");
        var before = HashFile(options.DatabasePath);
        var query = new SqliteReleasedRecipeQuery(options);
        var page = await query.QueryAsync(new(PageSize: 20)).ConfigureAwait(true);
        Require(page.Available, "RecipeReleaseColdQueryUnavailable_" + page.ReasonCode);
        if (evidence.Released)
        {
            Require(evidence.RecipeId is not null && evidence.RecipeVersion is not null &&
                evidence.RecipeContentHash is not null && page.Recipes.Count == 1,
                "RecipeReleaseColdQueryReleaseMissing");
            var reference = new RecipeReference(evidence.RecipeId!, evidence.RecipeVersion!,
                evidence.RecipeContentHash!);
            var read = await query.ReadAsync(reference).ConfigureAwait(true);
            var readRecipe = read.Recipe;
            Require(read.Available && readRecipe is not null &&
                readRecipe.Record.ContentHash == evidence.RecordContentHash &&
                readRecipe.Record.Source.RevisionContentHash == evidence.RevisionContentHash,
                "RecipeReleaseColdQueryHashMismatch");
            Require(page.Recipes.Single().Record.ContentHash == evidence.RecordContentHash,
                "RecipeReleaseColdQueryPageHashMismatch");
        }
        else
        {
            Require(page.Recipes.Count == 0 && evidence.ExpectedRejection is not null,
                "RecipeReleaseColdQueryUnexpectedRelease");
        }
        var after = HashFile(options.DatabasePath);
        Require(before == after, "RecipeReleaseColdQueryChangedDatabase");
        await File.WriteAllTextAsync(Path.Combine(directory, "release-restart.json"),
            JsonSerializer.Serialize(new
            {
                result = "Pass",
                caseId = CaseId(scenario) + "-Q",
                scenario,
                readOnlyQuery = true,
                databaseUnchanged = true,
                releaseCount = page.Recipes.Count,
                releaseRecordContentHash = evidence.RecordContentHash,
                recipeContentHash = evidence.RecipeContentHash,
                revisionContentHash = evidence.RevisionContentHash,
                expectedRejection = evidence.ExpectedRejection
            }, JsonOptions)).ConfigureAwait(true);
    }

    private static string NormalizeScenario(string? scenario, RecipeGovernanceMode? mode)
    {
        if (string.IsNullOrWhiteSpace(scenario))
            return mode == RecipeGovernanceMode.MakerCheckerRelease ? "maker-checker" : "single";
        return scenario.Trim().ToLowerInvariant() switch
        {
            "single" or "single-approver" => "single",
            "maker" or "maker-checker" or "makerchecker" => "maker-checker",
            "concurrent" or "concurrent-edit" => "concurrent",
            "dependency" or "bad-dependency" => "dependency",
            _ => throw new ArgumentException("RecipeReleaseScenarioInvalid", nameof(scenario))
        };
    }

    private static string CaseId(string scenario) => scenario switch
    {
        "single" => "V130-C01",
        "maker-checker" => "V130-C02",
        "concurrent" => "V130-C03",
        "dependency" => "V130-C04",
        _ => "V130-C00"
    };

    private static string OutputLine(string scenario) => scenario switch
    {
        "single" => "V130-C01 recipe-release PASS released=true available=true activeUnchanged=true ready=false executionCount=1",
        "maker-checker" => "V130-C02 recipe-release-maker PASS released=false reason=RecipeReleaseMakerCheckerConflict activeUnchanged=true ready=false executionCount=1",
        "concurrent" => "V130-C03 recipe-release-concurrent PASS released=false reason=RecipeReleaseDraftRevisionConflict activeUnchanged=true ready=false executionCount=1",
        "dependency" => "V130-C04 recipe-release-dependency PASS released=false reason=RecipeReleaseAssetAuthorityUnavailable activeUnchanged=true ready=false executionCount=1",
        _ => "V130-C00 recipe-release FAIL reason=RecipeReleaseScenarioInvalid"
    };

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static async Task Verified(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = (await runtime.GetSnapshotAsync().ConfigureAwait(true)).AuditIntegrity?.State;
            if (state == AuditIntegrityState.Verified) return;
            Require(state != AuditIntegrityState.Faulted && !timeout.IsCancellationRequested,
                "RecipeReleaseAuditVerificationUnavailable");
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task Until(Func<bool> complete)
    {
        var started = Stopwatch.StartNew();
        do
        {
            await Task.Delay(20).ConfigureAwait(true);
            if (started.Elapsed > TimeSpan.FromSeconds(30))
                throw new RecipeReleaseDemoCheckException("RecipeReleaseOperationTimeout");
        }
        while (!complete());
        await Flush().ConfigureAwait(true);
    }

    private static void WriteFailureLocation(Exception exception)
    {
        // Development evidence contains code locations only, never exception messages,
        // command arguments, credentials or serialized identity/configuration payloads.
        var failure = exception.GetBaseException();
        Console.Error.WriteLine("exceptionType=" + failure.GetType().FullName);
        foreach (var frame in (new StackTrace(failure, true).GetFrames() ?? Array.Empty<StackFrame>()).Take(12))
        {
            var method = frame.GetMethod();
            Console.Error.WriteLine($"location={method?.DeclaringType?.FullName}.{method?.Name}:{frame.GetFileLineNumber()}");
        }
    }

    private static async Task Flush() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static void SaveWindow(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void InvokeButton(Button button)
    {
        Require(button.IsEnabled, "RecipeReleaseButtonUnavailable");
        ((IInvokeProvider)new ButtonAutomationPeer(button)
            .GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void Pump(Func<Task> operation)
    {
        Exception? failure = null;
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
        {
            try { await operation().ConfigureAwait(true); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new RecipeReleaseDemoCheckException(reason);
    }

    private sealed class ObservedReleaseService : IRecipeReleaseService
    {
        private readonly IRecipeReleaseService _inner;
        private int _releaseCalls;
        internal ObservedReleaseService(IRecipeReleaseService inner) => _inner = inner;
        internal int ReleaseCalls => Volatile.Read(ref _releaseCalls);
        public ValueTask<RecipeReleaseAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => _inner.GetAccessAsync(invocation, cancellationToken);
        public ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(reference, cancellationToken);
        public ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter,
            CancellationToken cancellationToken = default) => _inner.QueryAsync(filter, cancellationToken);
        public ValueTask<RecipeReleaseResult> ReleaseAsync(ReleaseRecipeCommand command,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _releaseCalls);
            return _inner.ReleaseAsync(command, cancellationToken);
        }
    }

    private sealed class ConcurrentDraftMutationReleaseService : IRecipeReleaseService
    {
        private readonly IRecipeReleaseService _inner;
        private readonly IRecipeDraftEditor _editor;
        private int _mutated;

        internal ConcurrentDraftMutationReleaseService(IRecipeReleaseService inner, IRecipeDraftEditor editor)
        { _inner = inner; _editor = editor; }

        public ValueTask<RecipeReleaseAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => _inner.GetAccessAsync(invocation, cancellationToken);

        public ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference,
            CancellationToken cancellationToken = default) => _inner.ReadAsync(reference, cancellationToken);

        public ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter,
            CancellationToken cancellationToken = default) => _inner.QueryAsync(filter, cancellationToken);

        public async ValueTask<RecipeReleaseResult> ReleaseAsync(ReleaseRecipeCommand command,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _mutated, 1) == 0)
            {
                var read = await _editor.ReadAsync(command.DraftId, command.ExpectedRevision,
                    cancellationToken).ConfigureAwait(true);
                Require(read.Available && read.Revision is not null,
                    "RecipeReleaseConcurrentSourceUnavailable");
                var source = read.Revision!;
                var changed = new RecipeDraftContent(source.Content.RecipeKey,
                    source.Content.DisplayName + " concurrent edit", source.Content.Algorithm,
                    source.Content.Configuration, source.Content.CameraRole, source.Content.Camera,
                    source.Content.AlgorithmExecutionTimeout, source.Content.AssetRequirements,
                    source.Content.PolicyRequirements, source.Content.ValueOrigins,
                    source.Content.CameraProviderExtension, source.Content.CalibrationRequirements,
                    source.Content.PartIdentityRequirement);
                var save = await _editor.SaveAsync(new RecipeDraftSaveRequest(Guid.NewGuid(),
                    source.DraftId, source.Revision, source.RevisionContentHash, changed,
                    "并发编辑验证", command.Invocation), cancellationToken).ConfigureAwait(true);
                Require(save.Saved, "RecipeReleaseConcurrentDraftSaveFailed_" + save.ReasonCode);
            }
            return await _inner.ReleaseAsync(command, cancellationToken).ConfigureAwait(true);
        }
    }

    private sealed record ReleaseEvidence(
        string Result,
        string CaseId,
        string Scenario,
        string PolicyId,
        string PolicyVersion,
        string PolicyMode,
        string PolicyContentHash,
        bool Released,
        bool Available,
        string? RecipeId,
        string? RecipeVersion,
        string? RecipeContentHash,
        string? RecordContentHash,
        Guid DraftId,
        long Revision,
        string RevisionContentHash,
        Guid AuthorPrincipalId,
        Guid AuthorSessionId,
        Guid? ApproverPrincipalId,
        Guid? ApproverSessionId,
        Guid? ReleaseSessionId,
        string? AuthorizationTarget,
        string? ErrorCode,
        string? ExpectedRejection,
        int ReleaseCount,
        int ExecutionCount,
        bool ActiveRecipeUnchanged,
        bool ArmStateUnchanged,
        bool Armed,
        bool ReadyBefore,
        bool ReadyAfter,
        bool Published,
        bool Active,
        bool ProductionReady,
        string Screenshot,
        string DatabaseHashBefore,
        string DatabaseHashAfter,
        string PhysicalBootstrapAuthority,
        bool IndependentSession);

    private sealed class RecipeReleaseDemoCheckException : Exception
    {
        internal RecipeReleaseDemoCheckException(string reasonCode) { ReasonCode = reasonCode; }
        internal string ReasonCode { get; }
    }
}
