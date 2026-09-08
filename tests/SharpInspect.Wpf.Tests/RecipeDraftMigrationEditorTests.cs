using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace SharpInspect.Wpf.Tests;

/// <summary>
/// Focused presentation tests for the explicit local Draft migration flow.
/// The fake migration service models the already constrained Runtime boundary;
/// it does not make migration decisions in the test itself.
/// </summary>
public sealed class RecipeDraftMigrationEditorTests
{
    [Fact]
    public async Task V128_W01_MigrationWithoutServiceLeavesSourceProjectionUntouched()
    {
        await using var fixture = await OpenFixtureAsync(withService: false);
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var result = await fixture.Model.MigrateAsync();

        Assert.Null(result);
        Assert.Equal("RecipeDraftMigrationUnavailable", fixture.Model.ErrorCode);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
        Assert.Null(fixture.Service);
    }

    [Fact]
    public async Task V128_W02_MissingMigratorDoesNotCallServiceOrReplaceSource()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var result = await fixture.Model.MigrateAsync();

        Assert.Null(result);
        Assert.Equal("RecipeDraftMigrationMigratorRequired", fixture.Model.ErrorCode);
        Assert.Equal(0, service.MigrateCount);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
    }

    [Fact]
    public async Task V128_W03_ChangingTargetAfterMigratorSelectionIsRejected()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        var wrongTarget = fixture.WrongTargetDescriptor;
        fixture.Model.SelectedMigrationTargetAlgorithm = wrongTarget;
        // Keep the exact registered migrator while selecting an unregistered
        // target.  The ViewModel must reject this pair before the service call.
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var result = await fixture.Model.MigrateAsync();

        Assert.Null(result);
        Assert.Equal("RecipeDraftMigrationTargetInvalid", fixture.Model.ErrorCode);
        Assert.Equal(0, service.MigrateCount);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
    }

    [Fact]
    public async Task V128_W04_InvalidSourceRevisionHashIsRejectedBeforeServiceCall()
    {
        await using var fixture = await OpenFixtureAsync(corruptSource: true);
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;

        var result = await fixture.Model.MigrateAsync();

        Assert.Null(result);
        Assert.Equal("RecipeDraftMigrationSourceInvalid", fixture.Model.ErrorCode);
        Assert.Equal(0, service.MigrateCount);
        Assert.Equal("not-a-sha256", fixture.Model.CurrentRevision!.RevisionContentHash);
    }

    [Fact]
    public async Task V128_W09_CancelKeepsAdmissionBusyUntilMigrationReturns()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;
        service.BlockMigrations = true;
        service.HoldCancellationCompletion = true;
        var sourceDraftId = fixture.Model.DraftId;
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var migration = fixture.Model.MigrateAsync();
        await service.MigrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Model.CanCancelMigration);
        fixture.Model.CancelMigration();
        await service.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(fixture.Model.IsBusy);
        Assert.False(fixture.Model.CanMigrate);
        Assert.False(fixture.Model.CanSave);
        Assert.Null(await fixture.Model.MigrateAsync());
        Assert.Null(await fixture.Model.SaveAsync());
        Assert.Equal(1, service.MigrateCount);
        service.AllowCancelledCompletion.TrySetResult(true);

        var result = await migration.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
        Assert.Equal(sourceDraftId, fixture.Model.DraftId);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
        Assert.Null(service.CreatedRevision);
    }

    [Fact]
    public async Task V128_W06_CreatedMigrationRereadsTargetAndLoadsLineage()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;
        var sourceDraftId = fixture.Model.DraftId;
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var result = await fixture.Model.MigrateAsync();

        Assert.NotNull(result);
        Assert.True(result!.Created);
        Assert.NotNull(result.Revision);
        Assert.NotEqual(sourceDraftId, fixture.Model.DraftId);
        Assert.Equal(fixture.TargetDescriptor.Identity,
            fixture.Model.CurrentRevision!.Content.Algorithm.Algorithm);
        Assert.Equal(sourceHash, fixture.Model.MigrationSourceRevisionHash);
        Assert.NotNull(fixture.Model.MigrationLineage);
        Assert.Equal(result.Revision!.Content.MigrationLineage!.ContentHash,
            fixture.Model.MigrationLineage!.ContentHash);
        Assert.Equal(1, fixture.Editor.TargetReadCount);
        var current = fixture.Model.CurrentRevision!;
        Assert.False(current.Published);
        Assert.False(current.Active);
        Assert.False(current.CanRelease);
    }

    [Fact]
    public async Task V128_W07_StepUpBindsPermissionOperationAndPlanContentHash()
    {
        await using var fixture = await OpenFixtureAsync(requiresStepUp: true);
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;

        var result = await fixture.Model.MigrateWithStepUpAsync("temporary-password");

        Assert.NotNull(result);
        Assert.True(result!.Created);
        var stepUp = fixture.StepUp!;
        Assert.NotNull(stepUp.LastRequest);
        var request = stepUp.LastRequest!;
        var serviceRequest = service.LastRequest!;
        Assert.Equal(Permission.EditRecipeDraft, request.Binding.Permission);
        Assert.Equal(AuditedCommandKind.MigrateAlgorithmConfiguration,
            request.Binding.CommandKind);
        Assert.Equal(serviceRequest.Plan.OperationId,
            request.Binding.CommandCorrelationId);
        Assert.Equal(serviceRequest.Plan.ContentHash,
            request.Binding.TargetId);
        Assert.Equal(stepUp.LastResult!.GrantId, serviceRequest.StepUpGrantId);
        Assert.Equal("temporary-password", stepUp.LastPassword);
    }

    [Fact]
    public async Task V128_W08_AlreadyCreatedMigrationRemainsCommittedWhenUiCancelRaces()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;
        service.BlockMigrations = true;
        service.ReturnCreatedAfterCancellation = true;
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var migration = fixture.Model.MigrateAsync();
        await service.MigrationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Model.CancelMigration();
        service.AllowCommitAfterCancellation.TrySetResult(true);

        var result = await migration.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.True(result!.Created);
        Assert.NotEqual("RecipeDraftMigrationCancelled", fixture.Model.ErrorCode);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
        Assert.NotNull(service.CreatedRevision);
    }

    [Fact]
    public async Task V128_W10_TargetValidationFailurePreservesSourceProjection()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;
        service.RejectTarget = true;
        var sourceDraftId = fixture.Model.DraftId;
        var sourceHash = fixture.Model.CurrentRevision!.RevisionContentHash;

        var result = await fixture.Model.MigrateAsync();

        Assert.NotNull(result);
        Assert.False(result!.Created);
        Assert.Null(result.Revision);
        Assert.Equal("RecipeDraftAlgorithmSemanticInvalid", fixture.Model.ErrorCode);
        Assert.Equal(sourceDraftId, fixture.Model.DraftId);
        Assert.Equal(sourceHash, fixture.Model.CurrentRevision!.RevisionContentHash);
        Assert.Null(service.CreatedRevision);
    }

    [Fact]
    public async Task V128_W11_ColdOpeningTargetSeparatesLineageSourceFromNextMigrationSource()
    {
        await using var fixture = await OpenFixtureAsync();
        var service = fixture.Service!;
        fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
        fixture.Model.SelectedMigrationMigrator = service.Descriptor;
        var sourceDraftId = fixture.Model.DraftId;
        var sourceRevision = fixture.Model.CurrentRevision!;

        var result = await fixture.Model.MigrateAsync();

        var targetRevision = Assert.IsType<RecipeDraftRevision>(result!.Revision);
        fixture.Editor.IncludeTargetInHistory = true;
        await using var cold = new RecipeDraftEditorViewModel(fixture.Editor, fixture.Sessions,
            new InlineUiDispatcher(), null, null, service);
        await cold.RefreshAsync();
        var targetHistory = Assert.Single(cold.History, item =>
            item.Revision.DraftId == targetRevision.DraftId);
        cold.SelectedHistory = targetHistory;
        await cold.OpenSelectedAsync();

        Assert.Equal(sourceDraftId, cold.MigrationLineageSourceDraftId);
        Assert.Equal(sourceRevision.Revision, cold.MigrationLineageSourceRevision);
        Assert.Equal(sourceRevision.RevisionContentHash,
            cold.MigrationLineageSourceRevisionHash);
        Assert.Equal(sourceRevision.Content.Configuration.ContentHash,
            cold.MigrationLineageSourceInputConfigurationHash);
        Assert.Equal(sourceRevision.RevisionContentHash, cold.MigrationSourceRevisionHash);
        Assert.Equal(targetRevision.DraftId, cold.MigrationSelectionSourceDraftId);
        Assert.Equal(targetRevision.Revision, cold.MigrationSelectionSourceRevision);
        Assert.Equal(targetRevision.RevisionContentHash,
            cold.MigrationSelectionSourceRevisionHash);
        Assert.Null(cold.SelectedMigrationTargetAlgorithm);
        Assert.Null(cold.SelectedMigrationMigrator);
        Assert.False(cold.CanMigrate);
        Assert.NotEmpty(cold.MigrationLineageSourceDescriptorText);
    }

    [Fact]
    public async Task V128_W12_PanelStepUpClickReadsAndClearsMigrationPassword()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MigrationFixture? fixture = null;
            Window? window = null;
            try
            {
                fixture = OpenFixtureAsync(requiresStepUp: true).GetAwaiter().GetResult();
                var service = fixture.Service!;
                fixture.Model.SelectedMigrationTargetAlgorithm = fixture.TargetDescriptor;
                fixture.Model.SelectedMigrationMigrator = service.Descriptor;
                var panel = new RecipeDraftEditorPanel(fixture.Model);
                window = new Window
                {
                    Content = panel, Width = 1000, Height = 900, ShowInTaskbar = false,
                    ShowActivated = false, Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();
                var passwordBox = Assert.IsType<PasswordBox>(
                    panel.FindName("MigrationStepUpPasswordBox"));
                var button = Assert.IsType<Button>(panel.FindName("MigrationStepUpButton"));
                Assert.False(fixture.Model.CanMigrate);
                Assert.False(fixture.Model.MigrateCommand.CanExecute(null));
                Assert.True(fixture.Model.CanStepUpMigrate);
                Assert.True(button.IsEnabled);
                passwordBox.Password = "temporary-password";

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(string.Empty, passwordBox.Password);
                Assert.Equal("temporary-password", fixture.StepUp!.LastPassword);
                Assert.NotNull(service.CreatedRevision);
            }
            catch (Exception exception) { completed.TrySetException(exception); }
            finally
            {
                window?.Close();
                if (fixture is not null)
                    fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<MigrationFixture> OpenFixtureAsync(bool withService = true,
        bool corruptSource = false, bool requiresStepUp = false)
    {
        var source = Descriptor("Recipe.Source", "Source.Config", 1);
        var target = Descriptor("Recipe.Target", "Target.Config", 2);
        var wrongTarget = Descriptor("Recipe.WrongTarget", "Wrong.Config", 3);
        var sourceContent = Content(source);
        var sourceRevision = new RecipeDraftRevision(1,
            Guid.Parse("00000000-0000-0000-0000-000000000101"), 1,
            Guid.Parse("00000000-0000-0000-0000-000000000102"), null,
            corruptSource ? "not-a-sha256" : new string('A', 64), sourceContent,
            Guid.Parse("00000000-0000-0000-0000-000000000103"),
            Guid.Parse("00000000-0000-0000-0000-000000000104"), 7,
            "source", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var editor = new FakeEditor(new[] { source, target, wrongTarget }, sourceRevision);
        var sessions = new FakeSessions(Authenticated());
        var service = withService
            ? new FakeMigrationService(editor, target, requiresStepUp)
            : null;
        var stepUp = requiresStepUp ? new FakeStepUp() : null;
        var model = new RecipeDraftEditorViewModel(editor, sessions,
            new InlineUiDispatcher(), null, stepUp, service);

        await model.RefreshAsync();
        model.SelectedHistory = Assert.Single(model.History);
        await model.OpenSelectedAsync();
        return new MigrationFixture(model, editor, sessions, source, target, wrongTarget, service, stepUp);
    }

    private static AlgorithmDescriptor Descriptor(string algorithmId, string schemaId, int version)
    {
        var fields = new[]
        {
            new AlgorithmFieldDefinition("Count", AlgorithmScalarType.Int64, "items", true,
                new AlgorithmScalarConstraints(minInt64: 0, maxInt64: 100),
                AlgorithmScalarValue.FromInt64(version), "migration test value")
        };
        var schema = new AlgorithmConfigurationSchema(schemaId, "1", fields);
        var overlay = new OverlayContract(algorithmId + ".Overlay", "1");
        var result = new AlgorithmResultSchema(algorithmId + ".Result", "1",
            Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
        return new AlgorithmDescriptor(new AlgorithmIdentity(algorithmId, "1"), schema, result);
    }

    private static RecipeDraftContent Content(AlgorithmDescriptor descriptor,
        RecipeDraftMigrationLineage? lineage = null)
    {
        var entries = descriptor.ConfigurationSchema.Fields
            .Where(field => field.AuthoringDefault is not null)
            .Select(field => new AlgorithmConfigurationEntry(field.Key, field.Unit,
                field.AuthoringDefault!)).ToArray();
        var configuration = AlgorithmConfigurationSnapshot.Create(
            descriptor.ConfigurationSchema, entries);
        var origins = entries.Select(entry => new RecipeDraftFieldOrigin(entry.Key,
            RecipeDraftValueOrigin.AuthoringDefault));
        return lineage is null
            ? new RecipeDraftContent("MigrationRecipe", "Migration Recipe",
                RecipeAlgorithmBinding.FromDescriptor(descriptor), configuration, "Primary",
                Camera(), TimeSpan.FromMilliseconds(250), Array.Empty<RecipeAssetRequirement>(),
                Array.Empty<RecipePolicyRequirement>(), origins)
            : new RecipeDraftContent(lineage, "MigratedRecipe", "Migrated Recipe",
                RecipeAlgorithmBinding.FromDescriptor(descriptor), configuration, "Primary",
                Camera(), TimeSpan.FromMilliseconds(250), Array.Empty<RecipeAssetRequirement>(),
                Array.Empty<RecipePolicyRequirement>(), origins);
    }

    private static RequestedCameraConfiguration Camera() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
        new RegionOfInterest(0, 0, 1, 1), VisionPixelFormat.Mono8, null, 1000, 0, null);

    private static InteractiveSession Authenticated() => new(
        InteractiveSessionState.Authenticated,
        "00000000-0000-0000-0000-000000000201",
        Guid.Parse("00000000-0000-0000-0000-000000000202"));

    private sealed class MigrationFixture : IAsyncDisposable
    {
        public MigrationFixture(RecipeDraftEditorViewModel model, FakeEditor editor,
            FakeSessions sessions, AlgorithmDescriptor sourceDescriptor, AlgorithmDescriptor targetDescriptor,
            AlgorithmDescriptor wrongTargetDescriptor, FakeMigrationService? service,
            FakeStepUp? stepUp)
        {
            Model = model;
            Editor = editor;
            Sessions = sessions;
            SourceDescriptor = sourceDescriptor;
            TargetDescriptor = targetDescriptor;
            WrongTargetDescriptor = wrongTargetDescriptor;
            Service = service;
            StepUp = stepUp;
        }

        public RecipeDraftEditorViewModel Model { get; }
        public FakeEditor Editor { get; }
        public FakeSessions Sessions { get; }
        public AlgorithmDescriptor SourceDescriptor { get; }
        public AlgorithmDescriptor TargetDescriptor { get; }
        public AlgorithmDescriptor WrongTargetDescriptor { get; }
        public FakeMigrationService? Service { get; }
        public FakeStepUp? StepUp { get; }

        public ValueTask DisposeAsync() => Model.DisposeAsync();
    }

    private sealed class FakeEditor : IRecipeDraftEditor
    {
        private readonly IReadOnlyList<AlgorithmDescriptor> _algorithms;
        private RecipeDraftRevision _source;
        private RecipeDraftRevision? _target;

        public FakeEditor(IReadOnlyList<AlgorithmDescriptor> algorithms,
            RecipeDraftRevision source)
        {
            _algorithms = algorithms;
            _source = source;
        }

        public IReadOnlyList<AlgorithmDescriptor> Algorithms => _algorithms;
        public RecipeDraftAccess Access { get; set; } = new(true,
            "RecipeDraftAccessGranted", false);
        public int TargetReadCount { get; private set; }
        public RecipeDraftSaveRequest? LastSaveRequest { get; private set; }
        public bool IncludeTargetInHistory { get; set; }

        public IReadOnlyList<AlgorithmConfigurationEntry> GetAuthoringDefaults(
            AlgorithmIdentity algorithm) => _algorithms
                .First(item => item.Identity.Id == algorithm.Id &&
                    item.Identity.Version == algorithm.Version).ConfigurationSchema.Fields
                .Where(field => field.AuthoringDefault is not null)
                .Select(field => new AlgorithmConfigurationEntry(field.Key, field.Unit,
                    field.AuthoringDefault!)).ToArray();

        public ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Access);

        public ValueTask<RecipeDraftValidationResult> ValidateAsync(RecipeDraftContent content,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new RecipeDraftValidationResult(true, "RecipeDraftValid",
                Array.Empty<AlgorithmValidationIssue>()));

        public ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSaveRequest = request;
            var revision = new RecipeDraftRevision(request.ExpectedRevision + 1,
                request.DraftId, request.ExpectedRevision + 1, request.OperationId,
                request.ExpectedRevisionContentHash, new string('C', 64), request.Content,
                Guid.Parse("00000000-0000-0000-0000-000000000203"),
                request.Invocation.SessionId!.Value, 7, request.ChangeReason,
                DateTimeOffset.UtcNow);
            if (request.DraftId == _source.DraftId) _source = revision;
            else _target = revision;
            return ValueTask.FromResult(new RecipeDraftSaveResult(true,
                "RecipeDraftSaved", revision, Array.Empty<AlgorithmValidationIssue>()));
        }

        public ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
            CancellationToken cancellationToken = default)
        {
            if (_target is { } target && target.DraftId == draftId &&
                (revision is null || target.Revision == revision.Value))
            {
                TargetReadCount++;
                return ValueTask.FromResult(new RecipeDraftReadResult(true,
                    "RecipeDraftRead", target));
            }
            if (_source.DraftId == draftId &&
                (revision is null || _source.Revision == revision.Value))
                return ValueTask.FromResult(new RecipeDraftReadResult(true,
                    "RecipeDraftRead", _source));
            return ValueTask.FromResult(new RecipeDraftReadResult(false,
                "RecipeDraftNotFound", null));
        }

        public ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter,
            CancellationToken cancellationToken = default)
        {
            var revisions = IncludeTargetInHistory && _target is { } target
                ? new[] { _source, target }
                : new[] { _source };
            return ValueTask.FromResult(new RecipeDraftPage(true,
                "RecipeDraftHistoryAvailable", revisions, revisions[^1].Position, null));
        }

        public void SetTarget(RecipeDraftRevision target) => _target = target;
    }

    private sealed class FakeMigrationService : IAlgorithmConfigurationMigrationService
    {
        private readonly FakeEditor _editor;
        private readonly AlgorithmDescriptor _target;

        public FakeMigrationService(FakeEditor editor, AlgorithmDescriptor target,
            bool requiresStepUp)
        {
            _editor = editor;
            _target = target;
            Access = new RecipeDraftAccess(true, "RecipeDraftAccessGranted", requiresStepUp);
            var source = editor.Algorithms[0];
            Descriptor = new AlgorithmConfigurationMigrationDescriptor(
                new RecipeContractReference("Migration.Migrator", "1", new string('D', 64)),
                source.Identity,
                new RecipeContractReference(source.ConfigurationSchema.Id,
                    source.ConfigurationSchema.Version, source.ConfigurationSchema.ContentHash),
                target.Identity,
                new RecipeContractReference(target.ConfigurationSchema.Id,
                    target.ConfigurationSchema.Version, target.ConfigurationSchema.ContentHash));
        }

        public AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
        public IReadOnlyList<AlgorithmConfigurationMigrationDescriptor> Migrations =>
            new[] { Descriptor };
        public RecipeDraftAccess Access { get; }
        public int MigrateCount { get; private set; }
        public RecipeDraftMigrationRequest? LastRequest { get; private set; }
        public RecipeDraftRevision? CreatedRevision { get; private set; }
        public bool BlockMigrations { get; set; }
        public bool ReturnCreatedAfterCancellation { get; set; }
        public bool HoldCancellationCompletion { get; set; }
        public bool RejectTarget { get; set; }
        public TaskCompletionSource<bool> MigrationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowCommitAfterCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowCancelledCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Access);

        public async ValueTask<RecipeDraftMigrationResult> MigrateAsync(
            RecipeDraftMigrationRequest request, CancellationToken cancellationToken = default)
        {
            MigrateCount++;
            LastRequest = request;
            if (BlockMigrations)
            {
                MigrationStarted.TrySetResult(true);
                if (ReturnCreatedAfterCancellation)
                    await AllowCommitAfterCancellation.Task.ConfigureAwait(false);
                else
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (HoldCancellationCompletion)
                    {
                        CancellationObserved.TrySetResult(true);
                        await AllowCancelledCompletion.Task.ConfigureAwait(false);
                        throw;
                    }
                }
            }

            if (RejectTarget)
                return new RecipeDraftMigrationResult(false,
                    "RecipeDraftAlgorithmSemanticInvalid", null,
                    new[] { new AlgorithmValidationIssue("TargetConfigurationInvalid", "Count") });

            var source = _editor.Algorithms[0];
            var sourceConfiguration = Configuration(source);
            var targetConfiguration = Configuration(_target);
            var lineage = new RecipeDraftMigrationLineage(request.Plan, Descriptor,
                sourceConfiguration.ContentHash, targetConfiguration.ContentHash,
                Array.Empty<AlgorithmValidationIssue>());
            var content = Content(_target, lineage);
            var revision = new RecipeDraftRevision(2, request.Plan.TargetDraftId, 1,
                request.Plan.OperationId, null, new string('B', 64), content,
                Guid.Parse("00000000-0000-0000-0000-000000000203"),
                request.Invocation.SessionId!.Value, 8, request.Plan.ChangeReason,
                DateTimeOffset.UtcNow);
            CreatedRevision = revision;
            _editor.SetTarget(revision);
            return new RecipeDraftMigrationResult(true, "RecipeDraftMigrationCreated",
                revision, Array.Empty<AlgorithmValidationIssue>());
        }

        private static AlgorithmConfigurationSnapshot Configuration(AlgorithmDescriptor descriptor)
        {
            var entries = descriptor.ConfigurationSchema.Fields
                .Where(field => field.AuthoringDefault is not null)
                .Select(field => new AlgorithmConfigurationEntry(field.Key, field.Unit,
                    field.AuthoringDefault!));
            return AlgorithmConfigurationSnapshot.Create(descriptor.ConfigurationSchema, entries);
        }
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        public StepUpRequest? LastRequest { get; private set; }
        public StepUpResult? LastResult { get; private set; }
        public string? LastPassword { get; private set; }

        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastPassword = request.Password;
            var result = new StepUpResult(true, "StepUpAccepted",
                Guid.Parse("00000000-0000-0000-0000-000000000204"));
            LastResult = result;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;

        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId,
            SessionLockReason reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Publish(InteractiveSession session)
        {
            Current = session;
            Changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
        }
    }
}
