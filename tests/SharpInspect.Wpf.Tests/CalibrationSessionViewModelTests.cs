using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows.Media;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class CalibrationSessionViewModelTests
{
    [Fact]
    public async Task V124_U09_CollectingWaitsForRuntimeCommandAuditBeforeEnablingNextMutation()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        var collecting = fixture.ActiveSnapshot(CalibrationSessionPhase.Collecting);
        fixture.Runtime.Snapshot = collecting with
        { CalibrationSession = collecting.CalibrationSession! with { OperationInProgress = true } };
        fixture.Query.Result = new CalibrationSessionQueryResult(true, "Available", fixture.Evidence());
        await viewModel.RefreshAsync();
        viewModel.SelectedFrame = viewModel.Frames[0];
        viewModel.ExcludeReason = "整帧被遮挡";
        viewModel.ExitReason = "终止当前采集";

        Assert.False(viewModel.CanCapture);
        Assert.False(viewModel.CanCompute);
        Assert.False(viewModel.CanExclude);
        Assert.True(viewModel.CanExit);
        Assert.Null(await viewModel.CaptureCalibrationFrameAsync());
        Assert.Empty(fixture.Runtime.Commands);

        fixture.Runtime.Snapshot = collecting;
        await viewModel.RefreshAsync();
        Assert.True(viewModel.CanCapture);
        Assert.True(viewModel.CanCompute);
    }

    [Fact]
    public async Task V124_U08_UnknownSafetyStateIsDecidedByRuntimeWithoutInventingReadiness()
    {
        var fixture = Fixture.Create();
        fixture.Runtime.Snapshot = fixture.Runtime.Snapshot with
        { Handshake = HandshakePhase.Unknown, Recovery = RecoveryState.Required };
        fixture.Runtime.SubmitHandler = command => new RuntimeCommandOutcome(command.CorrelationId,
            CommandDisposition.Rejected, "SafetyStopUnverified", AuditPersistence.Persisted);
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        await viewModel.RefreshAsync();
        viewModel.StartReason = "申请由 Runtime 检查标定准入";

        var outcome = await viewModel.StartCalibrationSessionAsync("current-password");

        Assert.Single(fixture.Runtime.Commands);
        Assert.Equal(CommandDisposition.Rejected, outcome!.Disposition);
        Assert.Equal("SafetyStopUnverified", outcome.ReasonCode);
        Assert.Null(viewModel.CurrentSessionId);
        Assert.False(viewModel.Ready);
        Assert.Equal(HandshakePhase.Unknown, fixture.Runtime.Snapshot.Handshake);
        Assert.Equal(RecoveryState.Required, fixture.Runtime.Snapshot.Recovery);
    }

    [Fact]
    public async Task V124_U01_NavigationDoesNotSubmitOrEnterRuntimeSession()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);

        viewModel.Deactivate();

        Assert.Empty(fixture.Runtime.Commands);
        Assert.Null(viewModel.CurrentSessionId);
        Assert.Null(viewModel.Evidence);
    }

    [Fact]
    public async Task V124_U02_StartUsesExactActualReferencesAndAcceptedIsNotCompleted()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        await viewModel.RefreshAsync();
        viewModel.StartReason = "采集本次标定证据";

        var outcome = await viewModel.StartCalibrationSessionAsync("current-password");

        Assert.NotNull(outcome);
        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        Assert.NotNull(fixture.StepUp.LastRequest);
        Assert.NotNull(fixture.Runtime.Commands.Single() as StartCalibrationSessionCommand);
        var stepUp = fixture.StepUp.LastRequest!;
        var command = (StartCalibrationSessionCommand)fixture.Runtime.Commands.Single();
        Assert.Equal(Permission.RunCalibration, stepUp.Binding.Permission);
        Assert.Equal(AuditedCommandKind.StartCalibrationSession, stepUp.Binding.CommandKind);
        Assert.Equal(command.CorrelationId, stepUp.Binding.CommandCorrelationId);
        Assert.Equal(command.AuthorizationTarget, stepUp.Binding.TargetId);
        Assert.Equal(fixture.Binding.Revision, command.ExpectedBindingRevision);
        Assert.Equal(fixture.Binding.RevisionHash, command.ExpectedBindingRevisionHash);
        Assert.Equal(fixture.ImagingRevision.Revision, command.ExpectedImagingSetup.Revision);
        Assert.Equal(fixture.ImagingRevision.RevisionHash, command.ExpectedImagingSetup.RevisionHash);
        Assert.Equal(fixture.Plan.ContentHash, command.Plan.ContentHash);
        Assert.Equal("采集本次标定证据", command.Reason);
        Assert.Null(viewModel.CurrentSessionId); // Runtime has not published a session ID yet.
        Assert.Null(viewModel.CalibrationSession);
        Assert.NotEqual(CalibrationSessionOutcome.Completed, viewModel.CalibrationSession?.Outcome);
    }

    [Fact]
    public async Task V124_U03_CancelledOrStaleStartCannotSubmitFrozenIntent()
    {
        var fixture = Fixture.Create();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StepUp.Handler = (request, _) =>
        {
            entered.TrySetResult(true);
            return release.Task;
        };
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        await viewModel.RefreshAsync();
        viewModel.StartReason = "冻结的开始意图";

        var pending = viewModel.StartCalibrationSessionAsync("current-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.StartReason = "后来编辑的理由";
        viewModel.CancelPendingOperations();
        release.SetResult(new StepUpResult(true, "StepUpAccepted", Fixture.GrantId));

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(fixture.Runtime.Commands);
        Assert.Null(viewModel.CurrentSessionId);

        var frozenFixture = Fixture.Create();
        var frozenEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frozenRelease = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        frozenFixture.StepUp.Handler = (_, _) =>
        {
            frozenEntered.TrySetResult(true);
            return frozenRelease.Task;
        };
        await using var frozenViewModel = frozenFixture.CreateViewModel();
        frozenViewModel.Configure(frozenFixture.Plan);
        await frozenViewModel.RefreshAsync();
        frozenViewModel.StartReason = "原始冻结理由";
        var frozenPending = frozenViewModel.StartCalibrationSessionAsync("current-password");
        await frozenEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        frozenViewModel.StartReason = "Step-Up 等待期间的后来编辑";
        frozenRelease.SetResult(new StepUpResult(true, "StepUpAccepted", Fixture.GrantId));

        var frozenOutcome = await frozenPending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandDisposition.Accepted, frozenOutcome!.Disposition);
        Assert.Equal("原始冻结理由", Assert.IsType<StartCalibrationSessionCommand>(
            frozenFixture.Runtime.Commands.Single()).Reason);
    }

    [Fact]
    public async Task V124_U04_WholeFrameExclusionNeedsReasonAndCandidatePhaseBlocksFurtherMutation()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        fixture.Runtime.Snapshot = fixture.ActiveSnapshot(CalibrationSessionPhase.Collecting);
        fixture.Query.Result = new CalibrationSessionQueryResult(true, "Available", fixture.Evidence());
        await viewModel.RefreshAsync();
        viewModel.SelectedFrame = viewModel.Frames[0];

        var missingReason = await viewModel.ExcludeCalibrationFrameAsync(
            viewModel.SelectedFrame!.FrameId, string.Empty);

        Assert.Null(missingReason);
        Assert.Equal("CalibrationExclusionReasonRequired", viewModel.ErrorCode);
        Assert.Empty(fixture.Runtime.Commands);

        var excluded = await viewModel.ExcludeCalibrationFrameAsync(
            viewModel.SelectedFrame.FrameId, "整帧被遮挡");
        Assert.NotNull(excluded);
        Assert.IsType<ExcludeCalibrationFrameCommand>(fixture.Runtime.Commands[0]);

        fixture.Runtime.SubmitHandler = command =>
        {
            fixture.Runtime.Snapshot = fixture.ActiveSnapshot(CalibrationSessionPhase.CandidateRetained);
            return new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted,
                "CandidateRetained", AuditPersistence.Persisted);
        };
        var compute = await viewModel.ComputeCalibrationCandidateAsync();
        Assert.NotNull(compute);
        Assert.False(viewModel.CanCapture);
        Assert.False(viewModel.CanCompute);
        Assert.False(viewModel.CanExclude);

        var commandCount = fixture.Runtime.Commands.Count;
        var afterCandidate = await viewModel.ExcludeCalibrationFrameAsync(
            viewModel.SelectedFrame.FrameId, "不得在候选后排除");
        Assert.Null(afterCandidate);
        Assert.Equal(commandCount, fixture.Runtime.Commands.Count);
    }

    [Fact]
    public async Task V124_U05_ObservationsAndSourceImageAreReadOnlyAndUnmodified()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        fixture.Runtime.Snapshot = fixture.ActiveSnapshot(CalibrationSessionPhase.Collecting);
        fixture.Query.Result = new CalibrationSessionQueryResult(true, "Available", fixture.Evidence());
        fixture.Query.FrameResult = new CalibrationFrameQueryResult(true, "Available",
            fixture.Image());
        await viewModel.RefreshAsync();

        viewModel.SelectedFrame = viewModel.Frames[0];
        var imageResult = await viewModel.ReadSelectedFrameAsync();

        Assert.True(imageResult!.Available);
        Assert.NotNull(viewModel.SelectedFrameImage);
        Assert.Equal(4, viewModel.SelectedFrameImage!.PixelWidth);
        Assert.Equal(4, viewModel.SelectedFrameImage.PixelHeight);
        Assert.Equal(PixelFormats.Gray8, viewModel.SelectedFrameImage.Format);
        Assert.Single(viewModel.ObservationRows);
        Assert.Contains("(1,2)", viewModel.ObservationRows[0].FeatureSummary);
        Assert.Contains(fixture.Frames[0].SourceHash, viewModel.ObservationRows[0].SourceHash);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CalibrationFrameEvidence>)viewModel.Frames).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CalibrationObservationEvidence>)viewModel.Observations).Clear());
    }

    [Fact]
    public async Task V124_U06_SessionChangeClearsSensitiveReadbackAndRejectsLateStepUp()
    {
        var fixture = Fixture.Create();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StepUp.Handler = (_, _) =>
        {
            entered.TrySetResult(true);
            return release.Task;
        };
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        fixture.Runtime.Snapshot = fixture.ActiveSnapshot(CalibrationSessionPhase.Collecting);
        fixture.Query.Result = new CalibrationSessionQueryResult(true, "Available", fixture.Evidence());
        await viewModel.RefreshAsync();
        Assert.NotEmpty(viewModel.Frames);
        fixture.Sessions.Publish(new InteractiveSession(InteractiveSessionState.Locked, null, null));
        Assert.Empty(viewModel.Frames);
        Assert.Null(viewModel.CurrentBinding);

        fixture.Sessions.Publish(new InteractiveSession(InteractiveSessionState.Authenticated,
            Fixture.PrincipalId.ToString("D"), Fixture.SessionId));
        fixture.Runtime.Snapshot = fixture.ReadySnapshot();
        await viewModel.RefreshAsync();
        viewModel.StartReason = "会话变化前的意图";

        var pending = viewModel.StartCalibrationSessionAsync("current-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Sessions.Publish(new InteractiveSession(InteractiveSessionState.Locked, null, null));
        release.SetResult(new StepUpResult(true, "StepUpAccepted", Fixture.GrantId));

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(fixture.Runtime.Commands);
        Assert.Empty(viewModel.Frames);
        Assert.Empty(viewModel.Observations);
        Assert.Null(viewModel.CurrentBinding);
        Assert.Equal("CalibrationSessionChanged", viewModel.ErrorCode);
    }

    [Fact]
    public async Task V124_U07_UnsafeStationSnapshotCannotBeBypassedByPage()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.Configure(fixture.Plan);
        fixture.Runtime.Snapshot = fixture.UnsafeSnapshot();
        await viewModel.RefreshAsync();
        viewModel.StartReason = "尝试在未验证安全停线时开始";

        var outcome = await viewModel.StartCalibrationSessionAsync("current-password");

        Assert.Null(outcome);
        Assert.Equal("CalibrationStationNotIdle", viewModel.ErrorCode);
        Assert.Empty(fixture.Runtime.Commands);
        Assert.Equal(0, fixture.StepUp.CallCount);
    }

    private sealed class Fixture
    {
        private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string PixelHash = "374708FFF7719DD5979EC875D56CD2286F6D3CF7EC317A3B25632AAB28EC37BB";
        public static readonly Guid PrincipalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid GrantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private static readonly Guid ImagingRevisionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private static readonly Guid FrameId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        private Fixture(CalibrationSessionPlan plan, CameraBindingRevision binding,
            ImagingSetupRevision imagingRevision, FakeRuntime runtime, FakeQuery query,
            FakeCameraRuntime camera, FakeImagingRuntime imaging, FakeSessions sessions,
            FakeStepUp stepUp)
        {
            Plan = plan;
            Binding = binding;
            ImagingRevision = imagingRevision;
            Runtime = runtime;
            Query = query;
            Camera = camera;
            Imaging = imaging;
            Sessions = sessions;
            StepUp = stepUp;
        }

        public CalibrationSessionPlan Plan { get; }
        public CameraBindingRevision Binding { get; }
        public ImagingSetupRevision ImagingRevision { get; }
        public FakeRuntime Runtime { get; }
        public FakeQuery Query { get; }
        public FakeCameraRuntime Camera { get; }
        public FakeImagingRuntime Imaging { get; }
        public FakeSessions Sessions { get; }
        public FakeStepUp StepUp { get; }
        public IReadOnlyList<CalibrationFrameEvidence> Frames => Evidence().Frames;

        public static Fixture Create()
        {
            var provider = new CameraProviderIdentity("Fixture.Provider", "1", "Fixture.Package", "1");
            var target = new CameraBindingTarget(provider, "fixture-camera-01");
            var binding = new CameraBindingRevision(7, "Primary", 7,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), null, Hash, target,
                PrincipalId, SessionId, 5, "fixture binding", DateTimeOffset.UnixEpoch);
            var definition = new ImagingSetupDefinition("Lens-A", "Focus-Locked", "Mount-A", 250, "Sensor-Up");
            var imagingRevision = new ImagingSetupRevision(12, "Primary", 3, ImagingRevisionId,
                Hash, binding, definition, ImagingSetupChangeOrigin.OperatorDeclared,
                PrincipalId, SessionId, 5, "fixture imaging", DateTimeOffset.UnixEpoch);
            var inputContract = new RecipeContractReference("fixture-input", "1", Hash);
            var plan = new CalibrationSessionPlan(
                new CalibrationRequirement("Primary", CalibrationKind.Intrinsic, "geometry",
                    new RecipeContractReference("fixture-coefficients", "1", Hash),
                    new RecipeContractReference("fixture-acceptance", "1", Hash)),
                new CalibrationProcedureDescriptor(new RecipeContractReference("fixture-procedure", "1", Hash),
                    inputContract, CalibrationKind.Intrinsic),
                new CalibrationProcedureInputPayload(inputContract, new byte[] { 1, 2, 3 }),
                new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                    new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null),
                new CalibrationEvidenceSelectionPolicy("fixture-selection", "1", 1, 1, 0.1));

            var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Open, CameraConfigurationState.Applied,
                CameraAcquisitionState.Stopped,
                new FrameTimePoint(DateTimeOffset.UnixEpoch, 1));
            var camera = new FakeCameraRuntime(new CameraSetupQueryResult(true, "Available",
                new CameraSetupSnapshot("Primary", binding, health)));
            var imaging = new FakeImagingRuntime(new ImagingSetupQueryResult(true, "Available", imagingRevision));
            var sessions = new FakeSessions(AuthenticatedSession);
            var stepUp = new FakeStepUp();
            var runtime = new FakeRuntime(InitialSnapshot());
            var query = new FakeQuery();
            return new Fixture(plan, binding, imagingRevision, runtime, query, camera, imaging, sessions, stepUp);
        }

        public CalibrationSessionViewModel CreateViewModel() =>
            new(Runtime, Query, Camera, Imaging, Sessions, StepUp, new InlineDispatcher());

        public CalibrationSessionEvidence Evidence()
        {
            var command = StartCommand();
            var header = new CalibrationSessionHeader(SessionId, Guid.NewGuid(), Guid.NewGuid(),
                PrincipalId, SessionId, 5, DateTimeOffset.UnixEpoch, command, Binding,
                Plan.TemporaryConfiguration, EffectiveConfiguration(), Hash);
            var frame = FrameEvidence();
            var observation = new CalibrationObservationEvidence(Guid.Parse("66666666-6666-6666-6666-666666666666"),
                frame, Plan.Procedure, Plan.Input.ContentHash,
                new CalibrationExtractionResult(new[] { new CalibrationImageFeature("corner-0", 1, 2) }));
            return new CalibrationSessionEvidence(header,
                new CalibrationSessionState(SessionId, CalibrationSessionPhase.Collecting,
                    CalibrationSessionOutcome.Pending, 1, 1, 0, null, "Collecting", false),
                new[] { frame }, new[] { observation }, Array.Empty<CalibrationEvidenceExclusion>(), null,
                new CalibrationSelectionEvaluation(false, "CalibrationInsufficientFrames", 1, 1, 0.1, Hash));
        }

        public CalibrationFrameImage Image() => new(FrameEvidence(), new byte[16]);

        public StationStateSnapshot ActiveSnapshot(CalibrationSessionPhase phase) =>
            InitialSnapshot() with
            {
                Mode = ExclusiveMode.Calibration,
                CalibrationSession = new CalibrationSessionState(SessionId, phase,
                    CalibrationSessionOutcome.Pending, 1, 1, 0, null, phase.ToString(), false)
            };

        public StationStateSnapshot UnsafeSnapshot() => InitialSnapshot() with { Ready = true };

        public StationStateSnapshot ReadySnapshot() => InitialSnapshot();

        private StartCalibrationSessionCommand StartCommand() => new(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            new CommandInvocation(CommandSource.PhysicalConsole, PrincipalId.ToString("D"), SessionId, GrantId),
            Plan, Binding.Revision, Binding.RevisionHash,
            ImagingSetupRevisionReference.FromRevision(ImagingRevision), "fixture start");

        private CalibrationFrameEvidence FrameEvidence()
        {
            var correlation = new ExecutionCorrelationId(ExecutionKind.Calibration, FrameId);
            var metadata = new FrameMetadata(correlation,
                "Primary", 4, 4, 4, VisionPixelFormat.Mono8, null, DateTimeOffset.UnixEpoch,
                EffectiveConfiguration());
            var provenance = new FrameProvenance(correlation, "fixture-provider", "1",
                "fixture-package", "1", "fixture-sdk", "1", null, "fixture-camera-01", null, null,
                "Mono8", "CanonicalRows", false, false, null, null,
                new FrameAcquisitionMilestones(1, null, null, null, null));
            return new CalibrationFrameEvidence(SessionId, FrameId, metadata, provenance,
                PixelHash, 16, PixelHash + ".bin");
        }

        private EffectiveCameraConfiguration EffectiveConfiguration() => new(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0, new RegionOfInterest(0, 0, 4, 4),
            VisionPixelFormat.Mono8, null, 100, 0, null);

        private static StationStateSnapshot InitialSnapshot() => new(
            Guid.Parse("88888888-8888-8888-8888-888888888888"), 1, DateTimeOffset.UnixEpoch,
            RuntimeLifecycle.Running, ExclusiveMode.None, ProductionArmState.Disarmed, false, false,
            HandshakePhase.Idle, RecoveryState.None, null, null,
            new CameraHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new PlcHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
            new SubsystemHealth(HealthState.Healthy, "Healthy"),
            new EvidenceHealth(HealthState.Healthy, 0, 0),
            new QualificationState(QualificationMatch.Matches, QualificationMatch.Matches,
                QualificationMatch.Matches, QualificationMatch.Matches),
            new PerformanceHealth(HealthState.Healthy, false), new AlarmSummary(0, 0, false),
            AuthenticatedSession, null, new AdmissionBlockers(Array.Empty<string>()));

        private static InteractiveSession AuthenticatedSession =>
            new(InteractiveSessionState.Authenticated, PrincipalId.ToString("D"), SessionId);
    }

    private sealed class FakeRuntime : IStationRuntime
    {
        public FakeRuntime(StationStateSnapshot snapshot) => Snapshot = snapshot;
        public StationStateSnapshot Snapshot { get; set; }
        public List<RuntimeCommand> Commands { get; } = new();
        public Func<RuntimeCommand, RuntimeCommandOutcome>? SubmitHandler { get; set; }

        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Snapshot);
        }

        public async IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return ValueTask.FromResult(SubmitHandler?.Invoke(command) ??
                new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Accepted,
                    "Accepted", AuditPersistence.Persisted));
        }
    }

    private sealed class FakeQuery : ICalibrationSessionQuery
    {
        public CalibrationSessionQueryResult Result { get; set; } =
            new(false, "NotConfigured");
        public CalibrationFrameQueryResult FrameResult { get; set; } =
            new(false, "NotConfigured");

        public ValueTask<CalibrationSessionQueryResult> QueryCalibrationSessionAsync(Guid sessionId,
            CommandInvocation invocation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result);
        }

        public ValueTask<CalibrationFrameQueryResult> ReadCalibrationFrameAsync(Guid sessionId, Guid frameId,
            string expectedSourceHash, CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(FrameResult);
        }
    }

    private sealed class FakeCameraRuntime : ICameraSetupRuntime
    {
        public FakeCameraRuntime(CameraSetupQueryResult result) => Result = result;
        public CameraSetupQueryResult Result { get; }
        public IReadOnlyList<CameraProviderIdentity> Providers => Array.Empty<CameraProviderIdentity>();
        public ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole,
            CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result);
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CameraProviderIdentity provider,
            CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<CameraSetupOperationResult> RebindAsync(CameraRebindRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(
            CameraDebugConfigurationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeImagingRuntime : IImagingSetupRuntime
    {
        private readonly ImagingSetupQueryResult _result;
        public FakeImagingRuntime(ImagingSetupQueryResult result) => _result = result;
        public ValueTask<ImagingSetupQueryResult> GetImagingSetupAsync(string logicalCameraRole,
            CommandInvocation invocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_result);
        public ValueTask<ImagingSetupChangeResult> DeclareImagingSetupAsync(ImagingSetupChangeRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ImagingSetupHistoryResult> QueryImagingSetupHistoryAsync(string logicalCameraRole,
            CommandInvocation invocation, long afterPosition = 0, long? throughPosition = null,
            int pageSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        public int CallCount { get; private set; }
        public StepUpRequest? LastRequest { get; private set; }
        public Func<StepUpRequest, CancellationToken, Task<StepUpResult>>? Handler { get; set; }
        public ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Handler is null
                ? ValueTask.FromResult(new StepUpResult(true, "Accepted", Fixture.GrantId))
                : new ValueTask<StepUpResult>(Handler(request, cancellationToken));
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;
        public FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; private set; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(InteractiveSession session)
        {
            Current = session;
            _changed?.Invoke(this, new InteractiveSessionChangedEventArgs(session));
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
