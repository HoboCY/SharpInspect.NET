using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class ImagingSetupViewModelTests
{
    [Fact]
    public async Task V123_U01_AuthorizedRefreshBindsActualCameraAndImagingRevision()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.True(viewModel.HasCurrentBinding);
        Assert.True(viewModel.HasCurrentRevision);
        Assert.Equal(fixture.Binding.Revision, viewModel.ExpectedBindingRevision);
        Assert.Equal(fixture.Binding.RevisionHash, viewModel.ExpectedBindingRevisionHash);
        Assert.Equal(fixture.Revision.Revision, viewModel.ExpectedImagingRevision);
        Assert.Equal(fixture.Revision.RevisionHash, viewModel.ExpectedImagingRevisionHash);
        Assert.Equal(1, fixture.Camera.GetSetupCount);
        Assert.Equal(1, fixture.Imaging.GetCurrentCount);
        Assert.NotNull(fixture.Camera.LastInvocation);
        Assert.Equal(SessionId, fixture.Camera.LastInvocation!.SessionId);
        Assert.Equal(PrincipalId.ToString("D"), fixture.Camera.LastInvocation.PrincipalId);
        Assert.Equal(1, fixture.Imaging.GetHistoryCount);
    }

    [Fact]
    public async Task V123_U02_DeclarationBindsActualBindingRevisionAndFullIntentToGrant()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.LensIdentity = "Lens-B";
        viewModel.FocusOrFocalLengthState = "Focus-Adjusted";
        viewModel.MountingPose = "Mount-Changed";
        viewModel.WorkingDistanceMmText = "251.5";
        viewModel.SensorOrientation = "Sensor-Right";
        viewModel.ChangeReason = "更换镜头并重新调焦";

        var result = await viewModel.DeclareAsync("current-password");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, fixture.StepUp.CallCount);
        Assert.Equal(1, fixture.Imaging.DeclareCount);
        Assert.NotNull(fixture.StepUp.LastRequest);
        Assert.NotNull(fixture.Imaging.LastRequest);

        var stepUp = fixture.StepUp.LastRequest!;
        var request = fixture.Imaging.LastRequest!;
        Assert.Equal("current-password", stepUp.Password);
        Assert.Equal(Permission.ManageCameraBindings, stepUp.Binding.Permission);
        Assert.Equal((AuditedCommandKind)19, stepUp.Binding.CommandKind);
        Assert.Equal(request.OperationId, stepUp.Binding.CommandCorrelationId);
        Assert.Equal(request.AuthorizationTarget, stepUp.Binding.TargetId);
        Assert.Equal(fixture.Binding.Revision, request.ExpectedBindingRevision);
        Assert.Equal(fixture.Binding.RevisionHash, request.ExpectedBindingRevisionHash);
        Assert.Equal(fixture.Revision.Revision, request.ExpectedRevision);
        Assert.Equal(fixture.Revision.RevisionHash, request.ExpectedRevisionHash);
        Assert.Equal(GrantId, request.Invocation.StepUpGrantId);
        Assert.Equal(viewModel.LogicalCameraRole, request.LogicalCameraRole);
        Assert.Equal("Lens-B", request.Definition.LensIdentity);
        Assert.Equal(251.5, request.Definition.WorkingDistanceMm);
        Assert.Equal("更换镜头并重新调焦", request.ChangeReason);
        Assert.Equal(AuditPersistence.Persisted, viewModel.LastChangeResult!.AuditPersistence);
        Assert.False(viewModel.ProductionReady);
        Assert.False(viewModel.CanPublishCalibrationProfile);
    }

    [Fact]
    public async Task V123_U03_InvalidDistanceDoesNotCallStepUpOrRuntime()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();

        viewModel.WorkingDistanceMmText = "NaN";
        var result = await viewModel.DeclareAsync("current-password");

        Assert.Null(result);
        Assert.Equal("ImagingSetupDistanceInvalid", viewModel.ErrorCode);
        Assert.Equal(0, fixture.StepUp.CallCount);
        Assert.Equal(0, fixture.Imaging.DeclareCount);

        viewModel.WorkingDistanceMmText = "250";
        Assert.Null(viewModel.ErrorCode);
        Assert.True(viewModel.CanDeclare);
    }

    [Fact]
    public async Task V123_U04_MissingBindingCannotDeclare()
    {
        var fixture = Fixture.Create();
        fixture.Camera.QueryResult = new CameraSetupQueryResult(true,
            "CameraSetupAvailable",
            new CameraSetupSnapshot("Primary", null, fixture.Health));
        await using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();
        var result = await viewModel.DeclareAsync("current-password");

        Assert.Null(result);
        Assert.Equal("ImagingSetupBindingRequired", viewModel.ErrorCode);
        Assert.Equal(0, fixture.Imaging.GetCurrentCount);
        Assert.Equal(0, fixture.StepUp.CallCount);
        Assert.False(viewModel.CanDeclare);
    }

    [Fact]
    public async Task V123_U05_CancelledStepUpCannotSubmitLateResult()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StepUp.Handler = (_, _) =>
        {
            entered.TrySetResult(true);
            return release.Task;
        };

        var pending = viewModel.DeclareAsync("current-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.CancelPendingOperations();
        release.SetResult(new StepUpResult(true, "StepUpAccepted", GrantId));

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, fixture.Imaging.DeclareCount);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("成像设置请求已取消，可重新操作。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V123_U06_EditingWhileStepUpPendingSubmitsOnlyFrozenIntent()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StepUp.Handler = (_, _) =>
        {
            entered.TrySetResult(true);
            return release.Task;
        };

        var pending = viewModel.DeclareAsync("current-password");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.WorkingDistanceMmText = "999";
        release.SetResult(new StepUpResult(true, "StepUpAccepted", GrantId));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, fixture.Imaging.DeclareCount);
        Assert.Equal(250, fixture.Imaging.LastRequest!.Definition.WorkingDistanceMm);
        Assert.Equal(AuditPersistence.Persisted, fixture.Imaging.LastRequest is not null
            ? result.AuditPersistence : AuditPersistence.Unavailable);
    }

    [Fact]
    public async Task V123_U07_HistoryIsDisplayedButNeverGrantsProfileOrReadyAuthority()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.True(viewModel.HistoryAvailable);
        Assert.Single(viewModel.History);
        Assert.Equal(fixture.Revision.RevisionHash, viewModel.History[0].RevisionHash);
        Assert.Contains("更换镜头", viewModel.History[0].ChangeReason);
        Assert.Contains("标定需求", viewModel.CalibrationRequirementStatus);
        Assert.Contains("不会声称自动检测", viewModel.NoAutomaticPhysicalDetectionNotice);
        Assert.False(viewModel.ProductionReady);
        Assert.False(viewModel.Ready);
        Assert.False(viewModel.CanPublishProfile);
    }

    private sealed class Fixture
    {
        private Fixture(CameraBindingRevision binding, ImagingSetupRevision revision,
            CameraHealthSnapshot health, FakeCameraRuntime camera,
            FakeImagingRuntime imaging, FakeSessions sessions, FakeStepUp stepUp)
        {
            Binding = binding;
            Revision = revision;
            Health = health;
            Camera = camera;
            Imaging = imaging;
            Sessions = sessions;
            StepUp = stepUp;
        }

        public CameraBindingRevision Binding { get; }
        public ImagingSetupRevision Revision { get; }
        public CameraHealthSnapshot Health { get; }
        public FakeCameraRuntime Camera { get; }
        public FakeImagingRuntime Imaging { get; }
        public FakeSessions Sessions { get; }
        public FakeStepUp StepUp { get; }

        public static Fixture Create()
        {
            var provider = new CameraProviderIdentity(
                "Fixture.Provider", "1", "Fixture.Package", "1");
            var target = new CameraBindingTarget(provider, "fixture-camera-01");
            var binding = new CameraBindingRevision(
                7, "Primary", 7,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                null, new string('A', 64), target,
                PrincipalId, SessionId, 5,
                "fixture binding", DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
            var definition = new ImagingSetupDefinition(
                "Lens-A", "Focus-Locked", "Mount-A", 250, "Sensor-Up");
            var revision = new ImagingSetupRevision(
                12, "Primary", 3,
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                new string('B', 64), binding, definition,
                ImagingSetupChangeOrigin.OperatorDeclared,
                PrincipalId, SessionId, 5,
                "更换镜头", DateTimeOffset.Parse("2025-01-02T00:00:00Z"));
            var health = new CameraHealthSnapshot(
                CameraProviderAvailability.Available,
                CameraConnectionState.Open,
                CameraConfigurationState.Applied,
                CameraAcquisitionState.Stopped,
                new FrameTimePoint(DateTimeOffset.Parse("2025-01-02T00:00:00Z"), 1));
            var camera = new FakeCameraRuntime(provider,
                new CameraSetupQueryResult(true, "CameraSetupAvailable",
                    new CameraSetupSnapshot("Primary", binding, health)));
            var imaging = new FakeImagingRuntime(revision);
            var sessions = new FakeSessions(AuthenticatedSession);
            var stepUp = new FakeStepUp();
            return new Fixture(binding, revision, health, camera, imaging, sessions, stepUp);
        }

        public ImagingSetupViewModel CreateViewModel() =>
            new(Imaging, Camera, Sessions, StepUp, new InlineDispatcher());
    }

    private sealed class FakeCameraRuntime : ICameraSetupRuntime
    {
        public FakeCameraRuntime(CameraProviderIdentity provider,
            CameraSetupQueryResult queryResult)
        {
            Providers = new[] { provider };
            QueryResult = queryResult;
        }

        public IReadOnlyList<CameraProviderIdentity> Providers { get; }
        public CameraSetupQueryResult QueryResult { get; set; }
        public int GetSetupCount { get; private set; }
        public CommandInvocation? LastInvocation { get; private set; }

        public ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole,
            CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            GetSetupCount++;
            LastInvocation = invocation;
            return ValueTask.FromResult(QueryResult);
        }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CameraProviderIdentity provider, CommandInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CameraSetupOperationResult> RebindAsync(
            CameraRebindRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(
            CameraDebugConfigurationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeImagingRuntime : IImagingSetupRuntime
    {
        private readonly ImagingSetupRevision _revision;

        public FakeImagingRuntime(ImagingSetupRevision revision) => _revision = revision;

        public int GetCurrentCount { get; private set; }
        public int GetHistoryCount { get; private set; }
        public int DeclareCount { get; private set; }
        public ImagingSetupChangeRequest? LastRequest { get; private set; }

        public ValueTask<ImagingSetupQueryResult> GetImagingSetupAsync(
            string logicalCameraRole, CommandInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            GetCurrentCount++;
            return ValueTask.FromResult(new ImagingSetupQueryResult(
                true, "ImagingSetupAvailable", _revision));
        }

        public ValueTask<ImagingSetupChangeResult> DeclareImagingSetupAsync(
            ImagingSetupChangeRequest request,
            CancellationToken cancellationToken = default)
        {
            DeclareCount++;
            LastRequest = request;
            return ValueTask.FromResult(new ImagingSetupChangeResult(
                true, "ImagingSetupRevisionCreated", AuditPersistence.Persisted, _revision));
        }

        public ValueTask<ImagingSetupHistoryResult> QueryImagingSetupHistoryAsync(
            string logicalCameraRole, CommandInvocation invocation,
            long afterPosition = 0, long? throughPosition = null, int pageSize = 50,
            CancellationToken cancellationToken = default)
        {
            GetHistoryCount++;
            return ValueTask.FromResult(new ImagingSetupHistoryResult(
                true, "ImagingSetupHistoryAvailable", new[] { _revision },
                12, null));
        }
    }

    private sealed class FakeStepUp : IStepUpAuthentication
    {
        public int CallCount { get; private set; }
        public StepUpRequest? LastRequest { get; private set; }
        public Func<StepUpRequest, CancellationToken, Task<StepUpResult>>? Handler { get; set; }

        public ValueTask<StepUpResult> ReauthenticateAsync(
            StepUpRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            if (Handler is not null)
                return new ValueTask<StepUpResult>(Handler(request, cancellationToken));
            return ValueTask.FromResult(new StepUpResult(
                true, "StepUpAccepted", GrantId));
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

        public ValueTask<SessionSignInResult> SignInAsync(
            PasswordSignInRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<InteractiveSession> GetSessionAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId,
            SessionLockReason reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

    private static readonly Guid PrincipalId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GrantId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static InteractiveSession AuthenticatedSession =>
        new(InteractiveSessionState.Authenticated,
            PrincipalId.ToString("D"), SessionId);
}
