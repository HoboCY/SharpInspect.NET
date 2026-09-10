using System.Runtime.CompilerServices;
using System.Windows;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class ManualInspectionSessionViewModelTests
{
    private static readonly Guid PrincipalId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InteractiveSessionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ManualSessionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ManualRunId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string Hash =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    [Trait("VerificationId", "V135_W01")]
    public async Task RefreshReadsBoundedPublicStateWithoutStartingAndSelectionKeepsExactDraft()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        var selection = new ManualRecipeSelection(
            Guid.Parse("55555555-5555-5555-5555-555555555555"), 7, Hash);
        viewModel.Selection = selection;
        viewModel.StartReason = "人工检测前置确认";

        Assert.Empty(fixture.Runtime.Commands);
        await viewModel.RefreshAsync();

        Assert.Empty(fixture.Runtime.Commands);
        Assert.True(viewModel.IsSnapshotFresh);
        Assert.Equal(selection, viewModel.Selection);
        Assert.Equal("ManualInspectionSnapshot", fixture.Manual.LastSnapshotReason);
        Assert.Equal(1, fixture.Manual.AccessReads);
        Assert.Equal(1, fixture.Manual.SnapshotReads);
        Assert.True(viewModel.CanStart);
    }

    [Fact]
    [Trait("VerificationId", "V135_W02")]
    public async Task StartAndRunSubmitTypedCommandsWithRuntimeOwnedSessionAndRunIdentity()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        var selection = new ManualRecipeSelection(
            Guid.Parse("66666666-6666-6666-6666-666666666666"), 3, Hash);
        viewModel.Selection = selection;
        viewModel.StartReason = "开始人工检测";
        viewModel.RunReason = "执行样件检测";
        viewModel.PartIdentityText = "PART-2026-001";

        var started = await viewModel.StartManualInspectionSessionAsync();

        var start = Assert.IsType<StartManualInspectionSessionCommand>(
            Assert.Single(fixture.Runtime.Commands));
        Assert.Equal(CommandDisposition.Accepted, started!.Disposition);
        Assert.Equal(selection, start.Selection);
        Assert.Null(start.ExpectedActive);
        Assert.Equal(CommandSource.PhysicalConsole, start.Invocation.Source);
        Assert.Equal(PrincipalId.ToString("D"), start.Invocation.PrincipalId);
        Assert.Equal(InteractiveSessionId, start.Invocation.SessionId);
        Assert.Equal(ManualSessionId, viewModel.CurrentManualSessionId);
        Assert.True(viewModel.CanRunOne);

        var run = await viewModel.RunOneAsync();
        var runCommand = Assert.IsType<RunManualInspectionCommand>(
            fixture.Runtime.Commands.Single(command => command is RunManualInspectionCommand));
        Assert.Equal(CommandDisposition.Accepted, run!.Disposition);
        Assert.Equal(ManualSessionId, runCommand.SessionId);
        Assert.Equal("PART-2026-001", runCommand.PartIdentity!.Value);
        Assert.Equal("执行样件检测", runCommand.Reason);
        Assert.Equal(ManualRunId, viewModel.LastManualRunId);
    }

    [Fact]
    [Trait("VerificationId", "V135_W03")]
    public async Task PageCancellationDoesNotCancelSubmittedRunAndExitModeIsTyped()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Selection = new ManualRecipeSelection(
            Guid.Parse("77777777-7777-7777-7777-777777777777"), 2, Hash);
        viewModel.StartReason = "开始人工检测";
        viewModel.RunReason = "等待检测完成";
        viewModel.ExitReason = "测试完成后退出";
        Assert.Equal(CommandDisposition.Accepted,
            (await viewModel.StartManualInspectionSessionAsync())!.Disposition);

        fixture.Runtime.SubmitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.ReleaseSubmit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var runTask = viewModel.RunOneAsync();
        await fixture.Runtime.SubmitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.CancelPendingOperations();

        Assert.False(fixture.Runtime.SubmittedCancellation.IsCancellationRequested);
        fixture.Runtime.ReleaseSubmit.TrySetResult(true);
        var run = await runTask;
        Assert.Equal(CommandDisposition.Accepted, run!.Disposition);
        Assert.DoesNotContain(fixture.Runtime.Commands,
            command => command is ExitManualInspectionSessionCommand);

        var exit = await viewModel.AbortAsync();
        var exitCommand = Assert.IsType<ExitManualInspectionSessionCommand>(
            fixture.Runtime.Commands.Last());
        Assert.Equal(CommandDisposition.Accepted, exit!.Disposition);
        Assert.Equal(ManualInspectionExitMode.Abort, exitCommand.Mode);
        Assert.Equal(ManualSessionId, exitCommand.SessionId);
    }

    [Fact]
    [Trait("VerificationId", "V135_W04")]
    public async Task MissingSelectionOrReasonNeverSubmitsRuntimeCommand()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();

        Assert.Null(await viewModel.StartManualInspectionSessionAsync());
        Assert.Equal("ManualRecipeSelectionRequired", viewModel.ErrorCode);
        Assert.Empty(fixture.Runtime.Commands);

        viewModel.Selection = new ManualRecipeSelection(
            Guid.Parse("88888888-8888-8888-8888-888888888888"), 1, Hash);
        Assert.Null(await viewModel.StartManualInspectionSessionAsync());
        Assert.Equal("ManualStartReasonRequired", viewModel.ErrorCode);
        Assert.Empty(fixture.Runtime.Commands);
    }

    [Fact]
    [Trait("VerificationId", "V135_W05")]
    public async Task TightenedPolicyUsesExactStepUpBindingAndGrantBearingInvocation()
    {
        var fixture = Fixture.Create();
        fixture.Manual.Access = new(true, "ManualInspectionAccessAvailable", true);
        var stepUp = new FakeStepUp();
        await using var viewModel = fixture.CreateViewModel(stepUp);
        viewModel.Selection = new ManualRecipeSelection(
            Guid.Parse("99999999-9999-9999-9999-999999999999"), 4, Hash);
        viewModel.StartReason = "需要二次确认的人工检测";

        var outcome = await viewModel.StartManualInspectionSessionAsync("current-password");

        var command = Assert.IsType<StartManualInspectionSessionCommand>(
            Assert.Single(fixture.Runtime.Commands));
        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        Assert.NotNull(stepUp.Request);
        Assert.Equal(Permission.RunManualInspection, stepUp.Request!.Binding.Permission);
        Assert.Equal(AuditedCommandKind.StartManualInspectionSession,
            stepUp.Request.Binding.CommandKind);
        Assert.Equal(command.CorrelationId, stepUp.Request.CorrelationId);
        Assert.Equal(command.AuthorizationTarget, stepUp.Request.Binding.TargetId);
        Assert.Equal(command.CorrelationId, stepUp.Request.Binding.CommandCorrelationId);
        Assert.Equal("current-password", stepUp.Request.Password);
        Assert.Equal(stepUp.GrantId, command.Invocation.StepUpGrantId!.Value);
        Assert.Equal(InteractiveSessionId, command.Invocation.SessionId);
    }

    [Fact]
    [Trait("VerificationId", "V135_W06")]
    public async Task HistoryRefreshDisplaysReadOnlyStatusActorAndExactSourceWithNoRuntimeCommand()
    {
        var fixture = Fixture.Create();
        var history = new FakeHistoryQuery(CreateHistoryPage());
        await using var viewModel = fixture.CreateViewModel(history: history);

        await viewModel.RefreshHistoryAsync();

        var row = Assert.Single(viewModel.HistoryRows);
        Assert.Equal(ManualInspectionSessionPhase.Admitted, row.Phase!.Value);
        Assert.Null(row.Status);
        Assert.Equal(PrincipalId, row.ActorPrincipalId!.Value);
        Assert.Equal(ManualRecipeSourceKind.Draft, row.SourceKind!.Value);
        Assert.Equal(Guid.Parse("ABABABAB-ABAB-ABAB-ABAB-ABABABABABAB"),
            row.Selection!.DraftId!.Value);
        Assert.Same(row, viewModel.SelectedHistoryRow = row);
        Assert.Equal(0L, history.Filters.Single().AfterPosition);
        Assert.Equal(ManualInspectionSessionViewModel.HistoryPageSize,
            history.Filters.Single().PageSize);
        Assert.Empty(fixture.Runtime.Commands);
    }

    [Fact]
    [Trait("VerificationId", "V135_W07")]
    public async Task TypedHistoryResultAndOverlayAreDisplayedWithoutRuntimeCommand()
    {
        var fixture = Fixture.Create();
        var history = new FakeHistoryQuery(CreateTypedHistoryPage());
        await using var viewModel = fixture.CreateViewModel(history: history);

        await viewModel.RefreshHistoryAsync();

        var row = Assert.Single(viewModel.HistoryRows, item => item.Run is not null);
        Assert.True(row.HasTypedResult);
        Assert.Same(row.Run!.Result, row.Result);
        Assert.Same(row.Run.ResultSchema, row.ResultSchema);
        Assert.Same(row.Run.FrameOverlay, row.FrameOverlay);
        Assert.Equal("Fixture.Manual.Result", row.ResultSchema!.Id);
        Assert.Contains("Typed Result=Fixture.Manual.Result v1", row.ResultSummary,
            StringComparison.Ordinal);
        Assert.Contains("Measurements=1", row.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("Frame Overlay=Fixture.Manual.Overlay v1", row.OverlaySummary,
            StringComparison.Ordinal);
        Assert.Contains("Elements=1", row.OverlaySummary, StringComparison.Ordinal);
        Assert.Same(row, viewModel.SelectedHistoryRow = row);
        Assert.Empty(fixture.Runtime.Commands);
    }

    [Fact]
    [Trait("VerificationId", "V135_W08")]
    public async Task PrivacyInputClearDoesNotSubmitAnImplicitExit()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.PartIdentityText = "PART-PRIVATE";
        viewModel.StartReason = "private start reason";
        viewModel.RunReason = "private run reason";
        viewModel.ExitReason = "private exit reason";

        viewModel.ClearSensitiveInputs();

        Assert.Equal(string.Empty, viewModel.PartIdentityText);
        Assert.Equal(string.Empty, viewModel.StartReason);
        Assert.Equal(string.Empty, viewModel.RunReason);
        Assert.Equal(string.Empty, viewModel.ExitReason);
        Assert.Empty(fixture.Runtime.Commands);
    }

    [Fact]
    [Trait("VerificationId", "V135_W09")]
    public async Task PanelLoadAndPrivacyClearDoNotStartOrExitManualInspection()
    {
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            ManualInspectionSessionViewModel? viewModel = null;
            try
            {
                var fixture = Fixture.Create();
                viewModel = fixture.CreateViewModel();
                viewModel.PartIdentityText = "PART-UI-PRIVATE";
                viewModel.StartReason = "private start reason";
                viewModel.RunReason = "private run reason";
                viewModel.ExitReason = "private exit reason";
                var panel = new ManualInspectionSessionPanel(viewModel);
                window = new Window
                {
                    Content = panel, Width = 1000, Height = 900,
                    ShowInTaskbar = false, ShowActivated = false,
                    Left = -32000, Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();
                Assert.True(panel.ActualHeight > 0);

                Assert.Empty(fixture.Runtime.Commands);
                panel.ClearSensitiveInputs();

                Assert.Equal(string.Empty, viewModel.PartIdentityText);
                Assert.Equal(string.Empty, viewModel.StartReason);
                Assert.Equal(string.Empty, viewModel.RunReason);
                Assert.Equal(string.Empty, viewModel.ExitReason);
                Assert.Empty(fixture.Runtime.Commands);
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
            finally
            {
                window?.Close();
                if (viewModel is not null)
                    viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ManualInspectionHistoryPage CreateHistoryPage()
    {
        var provider = new CameraProviderIdentity("Fixture.Provider", "1",
            "Fixture.Adapter", "1");
        var target = new CameraBindingTarget(provider, "camera-01");
        var binding = new CameraBindingRevision(1, "Primary", 1,
            Guid.Parse("12121212-1212-1212-1212-121212121212"), null, Hash,
            target, PrincipalId, InteractiveSessionId, 1, "history fixture",
            DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"));
        var selection = new ManualRecipeSelection(
            Guid.Parse("ABABABAB-ABAB-ABAB-ABAB-ABABABABABAB"), 2, Hash);
        var header = new ManualInspectionSessionHeader(1, ManualSessionId,
            Guid.Parse("13131313-1313-1313-1313-131313131313"),
            Guid.Parse("14141414-1414-1414-1414-141414141414"),
            Guid.Parse("15151515-1515-1515-1515-151515151515"), selection, Hash,
            binding, null, null, null, PrincipalId, InteractiveSessionId, 1,
            new RecipeContractReference("Fixture.ManualPolicy", "1", Hash), Hash,
            "history fixture", DateTimeOffset.Parse("2026-01-01T00:00:01+00:00"));
        var @event = new ManualInspectionSessionEvent(1, header, header.AttemptId,
            header.StartCorrelationId, AuditedCommandKind.StartManualInspectionSession,
            ManualInspectionSessionPhase.Admitted, ManualInspectionRestorationState.NotRequired,
            "ManualInspectionSessionAdmitted", false, PrincipalId, InteractiveSessionId, 1,
            Hash, DateTimeOffset.Parse("2026-01-01T00:00:02+00:00"));
        return new ManualInspectionHistoryPage(true, "ManualInspectionHistoryAvailable",
            new[] { @event }, Array.Empty<ManualInspectionRunRecord>(), 1, null);
    }

    private static ManualInspectionHistoryPage CreateTypedHistoryPage()
    {
        var basePage = CreateHistoryPage();
        var header = basePage.Events[0].Header;
        var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, ManualRunId);
        var camera = new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
            1_000, 0, new RegionOfInterest(0, 0, 4, 3), VisionPixelFormat.Mono8,
            null, 1_000, 0, null);
        var frame = new FrameMetadata(correlation, "Primary", 4, 3, 4,
            VisionPixelFormat.Mono8, null,
            DateTimeOffset.Parse("2026-01-01T00:00:03+00:00"), camera);
        var schema = new AlgorithmResultSchema("Fixture.Manual.Result", "1",
            new[]
            {
                new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64,
                    "ratio", required: true,
                    constraints: new AlgorithmScalarConstraints(minFloat64: 0, maxFloat64: 100))
            },
            new[] { "FixtureManualUnknown" },
            new OverlayContract("Fixture.Manual.Overlay", "1",
                maximumElements: 1, maximumTotalPoints: 4,
                maximumPointsPerElement: 4, maximumTextLength: 0));
        var result = new AlgorithmResult(InspectionDecision.Pass, null,
            new[]
            {
                new AlgorithmMeasurement("Score", "ratio",
                    AlgorithmScalarValue.FromFloat64(12.5))
            },
            new OutputOverlaySet(schema.OverlayContract,
                new OverlayPrimitive[]
                {
                    new OverlayAxisAlignedRectangle(new OverlayPoint(1, 1), 1, 1)
                }));
        var overlay = new FrameOverlaySnapshot(Guid.Parse("56565656-5656-5656-5656-565656565656"),
            frame, schema, result.OverlaySet, Hash);
        var run = new ManualInspectionRunRecord(
            position: 2, runId: ManualRunId, sessionId: ManualSessionId,
            runtimeEpoch: header.RuntimeEpoch,
            commandCorrelationId: Guid.Parse("57575757-5757-5757-5757-575757575757"),
            attemptId: header.AttemptId, partIdentity: null,
            status: ManualInspectionRunStatus.Completed, decision: InspectionDecision.Pass,
            executionStatus: ExecutionStatus.Success,
            reasonCode: "ManualInspectionCompleted",
            admittedAtUtc: DateTimeOffset.Parse("2026-01-01T00:00:03+00:00"),
            startedAtUtc: DateTimeOffset.Parse("2026-01-01T00:00:03.001+00:00"),
            completedAtUtc: DateTimeOffset.Parse("2026-01-01T00:00:03.002+00:00"),
            frameMetadata: frame, frameProvenance: null,
            algorithmResultPayload: null, algorithmResultContentHash: null,
            preparedInstanceId: Guid.Parse("58585858-5858-5858-5858-585858585858"),
            algorithm: new AlgorithmIdentity("Fixture.Manual", "1"),
            resultSchema: schema, result: result, frameOverlay: overlay);
        return new ManualInspectionHistoryPage(true, "ManualInspectionHistoryAvailable",
            basePage.Events, new[] { run }, 2, null);
    }

    private sealed class Fixture
    {
        private Fixture()
        {
            Sessions = new FakeSessions(new InteractiveSession(
                InteractiveSessionState.Authenticated, PrincipalId.ToString("D"),
                InteractiveSessionId));
            Manual = new FakeManualService();
            Runtime = new FakeRuntime(Manual);
            Drafts = new EmptyDraftQuery();
            Released = new EmptyReleasedQuery();
            Activations = new EmptyActivationQuery();
        }

        public FakeRuntime Runtime { get; }
        public FakeManualService Manual { get; }
        public FakeSessions Sessions { get; }
        public EmptyDraftQuery Drafts { get; }
        public EmptyReleasedQuery Released { get; }
        public EmptyActivationQuery Activations { get; }

        public static Fixture Create() => new();

        public ManualInspectionSessionViewModel CreateViewModel(IStepUpAuthentication? stepUp = null,
            IManualInspectionHistoryQuery? history = null) =>
            new(Runtime, Manual, Sessions, Drafts, Released, Activations,
                new InlineDispatcher(), stepUp, history);
    }

    private sealed class FakeRuntime : IStationRuntime
    {
        private readonly FakeManualService _manual;

        public FakeRuntime(FakeManualService manual) => _manual = manual;
        public List<RuntimeCommand> Commands { get; } = new();
        public TaskCompletionSource<bool>? SubmitStarted { get; set; }
        public TaskCompletionSource<bool>? ReleaseSubmit { get; set; }
        public CancellationToken SubmittedCancellation { get; private set; }

        public ValueTask<StationStateSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            SubmittedCancellation = cancellationToken;
            SubmitStarted?.TrySetResult(true);
            if (ReleaseSubmit is not null)
                await ReleaseSubmit.Task.ConfigureAwait(false);

            switch (command)
            {
                case StartManualInspectionSessionCommand start:
                    _manual.Snapshot = _manual.CreateSnapshot(
                        ManualInspectionSessionPhase.ReadyForRun, ManualSessionId,
                        null, null, start.CorrelationId);
                    break;
                case RunManualInspectionCommand run:
                    _manual.Snapshot = _manual.CreateSnapshot(
                        ManualInspectionSessionPhase.ReadyForRun, ManualSessionId,
                        ManualRunId, ManualRunId, run.CorrelationId);
                    break;
                case ExitManualInspectionSessionCommand exit:
                    _manual.Snapshot = _manual.CreateSnapshot(
                        ManualInspectionSessionPhase.Closed, ManualSessionId,
                        null, ManualRunId, exit.CorrelationId);
                    break;
            }

            return new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Accepted, "ManualCommandAccepted", AuditPersistence.Persisted);
        }
    }

    private sealed class FakeManualService : IManualInspectionSessionService
    {
        public FakeManualService()
        {
            Snapshot = CreateSnapshot(ManualInspectionSessionPhase.Idle, null, null, null, null);
        }

        public int AccessReads { get; private set; }
        public int SnapshotReads { get; private set; }
        public string LastSnapshotReason { get; private set; } = string.Empty;
        public ManualInspectionAccess Access { get; set; } =
            new(true, "ManualInspectionAccessAvailable", false);
        public ManualInspectionSessionSnapshot Snapshot { get; set; }

        public ValueTask<ManualInspectionAccess> GetAccessAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccessReads++;
            return ValueTask.FromResult(Access);
        }

        public ValueTask<ManualInspectionSessionReadResult> GetSnapshotAsync(
            CommandInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SnapshotReads++;
            LastSnapshotReason = "ManualInspectionSnapshot";
            return ValueTask.FromResult(new ManualInspectionSessionReadResult(
                true, LastSnapshotReason, Snapshot));
        }

        public ManualInspectionSessionSnapshot CreateSnapshot(ManualInspectionSessionPhase phase,
            Guid? sessionId, Guid? currentRunId, Guid? lastRunId, Guid? correlationId) =>
            new(Guid.Parse("99999999-9999-9999-9999-999999999999"), (Snapshot?.Revision ?? 0) + 1,
                sessionId, phase, phase.ToString(), DateTimeOffset.UtcNow,
                sessionId is null ? null : PrincipalId,
                sessionId is null ? null : InteractiveSessionId,
                null, currentRunId, lastRunId,
                phase == ManualInspectionSessionPhase.Closed
                    ? ManualInspectionRestorationState.NoActiveBaselineClosed
                    : ManualInspectionRestorationState.NotRequired,
                false, false, correlationId);
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        public readonly Guid GrantId = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
        public StepUpRequest? Request { get; private set; }

        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return ValueTask.FromResult(new StepUpResult(true, "StepUpAccepted", GrantId));
        }
    }

    private sealed class FakeHistoryQuery : IManualInspectionHistoryQuery
    {
        private readonly ManualInspectionHistoryPage _page;

        public FakeHistoryQuery(ManualInspectionHistoryPage page) => _page = page;
        public List<ManualInspectionHistoryFilter> Filters { get; } = new();

        public ValueTask<ManualInspectionHistoryReadResult> ReadCurrentAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ManualInspectionHistoryReadResult(
                true, "ManualInspectionHistoryAvailable", _page.PendingHeader));

        public ValueTask<ManualInspectionHistoryReadResult> ReadAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => ReadCurrentAsync(cancellationToken);

        public ValueTask<ManualInspectionHistoryPage> QueryAsync(ManualInspectionHistoryFilter filter,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Filters.Add(filter);
            return ValueTask.FromResult(_page);
        }
    }

    private sealed class EmptyDraftQuery : IRecipeDraftHistoryQuery
    {
        public ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeDraftReadResult(false, "RecipeDraftNotFound", null));

        public ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeDraftPage(true, "RecipeDraftHistoryAvailable",
                Array.Empty<RecipeDraftRevision>(), 0, null));
    }

    private sealed class EmptyReleasedQuery : IReleasedRecipeQuery
    {
        public ValueTask<ReleasedRecipeReadResult> ReadAsync(RecipeReference reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ReleasedRecipeReadResult(false, "ReleasedRecipeNotFound"));

        public ValueTask<ReleasedRecipePage> QueryAsync(ReleasedRecipeFilter filter,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ReleasedRecipePage(true, "ReleasedRecipeHistoryAvailable",
                Array.Empty<ReleasedRecipe>(), 0, null));
    }

    private sealed class EmptyActivationQuery : IRecipeActivationQuery
    {
        public ValueTask<RecipeActivationReadResult> ReadCurrentAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeActivationReadResult(true, "NoActiveRecipe", null));

        public ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeActivationReadResult(false, "ActivationNotFound"));

        public ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RecipeActivationPage(true, "ActivationHistoryAvailable",
                Array.Empty<RecipeActivationRecord>(), 0, null));
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

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess => true;
        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
