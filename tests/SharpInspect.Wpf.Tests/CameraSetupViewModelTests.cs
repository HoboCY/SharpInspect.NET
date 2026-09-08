using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SharpInspect.Abstractions;
using SharpInspect.Wpf;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class CameraSetupViewModelTests
{
    [Fact]
    public async Task V117_W01_UnconfiguredPageIsExplicitlyUnavailableAndDoesNotSelectAnything()
    {
        await using var viewModel = new CameraSetupViewModel(null, null, null, new InlineDispatcher());

        Assert.False(viewModel.IsConfigured);
        Assert.False(viewModel.CanRefresh);
        Assert.Empty(viewModel.Providers);
        Assert.Empty(viewModel.Devices);
        Assert.Null(viewModel.SelectedProvider);
        Assert.Null(viewModel.SelectedDevice);
        Assert.Equal("CameraSetupRuntimeUnavailable", viewModel.ErrorCode);
        Assert.Contains("不可用", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V117_W02_DiscoveryRequiresExplicitProviderAndNeverSelectsFirstDevice()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();

        await viewModel.DiscoverAsync();
        Assert.Equal("CameraSetupProviderSelectionRequired", viewModel.ErrorCode);
        Assert.Equal(0, fixture.Runtime.DiscoverCount);

        viewModel.SelectedProvider = fixture.Provider;
        await viewModel.DiscoverAsync();

        Assert.Equal(1, fixture.Runtime.DiscoverCount);
        Assert.Equal(2, viewModel.CandidateCount);
        Assert.Null(viewModel.SelectedDevice);
        Assert.False(viewModel.CanRebind);

        viewModel.SelectedDevice = viewModel.Devices[1];
        Assert.Same(viewModel.Devices[1], viewModel.SelectedDevice);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.HasSetup);
        Assert.Equal(CameraProviderAvailability.Available, viewModel.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Open, viewModel.Connection);
        Assert.Equal(CameraConfigurationState.Applied, viewModel.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, viewModel.Acquisition);
        Assert.Contains("配方激活", viewModel.StatusMessage);
        Assert.False(viewModel.ProductionReady);
        Assert.True(viewModel.RequiresRecipeActivation);
    }

    [Fact]
    public async Task V117_W03_InvalidConfigurationIsRecoverableAndDoesNotCallStepUpOrRuntime()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();

        viewModel.ExposureTimeUsText = "not-a-number";
        var outcome = await viewModel.ApplyAsync("secret");

        Assert.Null(outcome);
        Assert.Equal("CameraSetupNumericInputInvalid", viewModel.ErrorCode);
        Assert.Equal(0, fixture.StepUp.CallCount);
        Assert.Equal(0, fixture.Runtime.ApplyCount);

        viewModel.ExposureTimeUsText = "100";
        Assert.Null(viewModel.ErrorCode);
        Assert.True(viewModel.CanApply);
    }

    [Fact]
    public async Task V117_W04_RebindUsesExactSelectedTargetAndStepUpBinding()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.SelectedProvider = fixture.Provider;
        await viewModel.DiscoverAsync();
        viewModel.SelectedDevice = viewModel.Devices[1];
        await viewModel.RefreshAsync();

        var result = await viewModel.RebindAsync("step-up secret");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, fixture.StepUp.CallCount);
        Assert.Equal(1, fixture.Runtime.RebindCount);
        Assert.NotNull(fixture.StepUp.LastRequest);
        Assert.NotNull(fixture.Runtime.LastRebind);

        var stepUp = fixture.StepUp.LastRequest!;
        var request = fixture.Runtime.LastRebind!;
        Assert.Equal("step-up secret", stepUp.Password);
        Assert.Equal(Permission.ManageCameraBindings, stepUp.Binding.Permission);
        Assert.Equal(AuditedCommandKind.RebindCamera, stepUp.Binding.CommandKind);
        Assert.Equal(viewModel.LogicalRole, stepUp.Binding.TargetId);
        Assert.Equal(stepUp.CorrelationId, stepUp.Binding.CommandCorrelationId);
        Assert.Equal(stepUp.CorrelationId, request.OperationId);
        Assert.Equal(GrantId, request.Invocation.StepUpGrantId);
        Assert.Equal(viewModel.LogicalRole, request.LogicalRole);
        Assert.Equal(viewModel.ExpectedBindingRevision, request.ExpectedBindingRevision);
        Assert.Same(viewModel.SelectedDevice!.Provider, request.Target.Provider);
        Assert.Equal(viewModel.SelectedDevice.StableDeviceIdentity, request.Target.StableDeviceIdentity);
        Assert.Equal(AuditPersistence.Persisted, viewModel.LastOperationResult!.Audit);
        Assert.Contains("记录", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V117_W05_SessionChangeCancelsStepUpAndCannotSubmitLateResult()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        viewModel.SelectedProvider = fixture.Provider;
        await viewModel.DiscoverAsync();
        viewModel.SelectedDevice = viewModel.Devices[0];
        await viewModel.RefreshAsync();

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StepUp.Handler = async (_, _) =>
        {
            entered.SetResult(true);
            return await release.Task.ConfigureAwait(false);
        };

        var pending = viewModel.RebindAsync("secret");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Sessions.Publish(new InteractiveSession(InteractiveSessionState.Locked, null, null));
        release.SetResult(new StepUpResult(true, "StepUpAccepted", Guid.NewGuid()));

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, fixture.Runtime.RebindCount);
        Assert.False(viewModel.HasSetup);
        Assert.Null(viewModel.SelectedProvider);
        Assert.Null(viewModel.SelectedDevice);
        Assert.Equal("CameraSetupSessionChanged", viewModel.ErrorCode);
    }

    [Fact]
    public async Task V117_W06_LateQueryForOldRoleCannotPopulateCurrentPage()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CameraSetupQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.QueryHandler = (_, _, _) =>
        {
            entered.SetResult(true);
            return release.Task;
        };

        var pending = viewModel.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.LogicalRole = "Secondary";
        release.SetResult(fixture.QueryResult);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.HasSetup);
        Assert.Equal("Secondary", viewModel.LogicalRole);
        Assert.NotEqual("Primary", viewModel.CurrentSetup?.LogicalRole);
    }

    [Fact]
    public async Task V117_W07_ApplyParsesCompleteCommonConfigurationAndKeepsExtensionUnsupported()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        viewModel.ExposureTimeUsText = "125.5";
        viewModel.GainDbText = "2.25";
        viewModel.RoiWidthText = "64";
        viewModel.RoiHeightText = "48";

        var result = await viewModel.ApplyAsync("step-up secret");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.Equal(1, fixture.Runtime.ApplyCount);
        Assert.NotNull(fixture.Runtime.LastApply);
        Assert.Equal(125.5, fixture.Runtime.LastApply!.Requested.ExposureTimeUs);
        Assert.Equal(2.25, fixture.Runtime.LastApply.Requested.GainDb);
        Assert.True(viewModel.ExtensionUnsupported);
        Assert.Null(fixture.Runtime.LastApply.Extension);
        Assert.False(viewModel.ProductionReady);
    }

    [Fact]
    public async Task V117_W08_CancellationCannotApplyLateReadback()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CameraSetupQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.QueryHandler = (_, _, _) =>
        {
            entered.SetResult(true);
            return release.Task;
        };

        var pending = viewModel.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.CancelPendingOperations();
        release.SetResult(fixture.QueryResult);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(viewModel.HasSetup);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("相机设置请求已取消，可重新操作。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V117_W09_SuccessWithoutReadbackClearsOldSetupAndFailsClosed()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        Assert.True(viewModel.HasSetup);

        fixture.Runtime.ApplyHandler = (_, _) => Task.FromResult(
            new CameraSetupOperationResult(true, "CameraConfigurationApplied",
                AuditPersistence.Persisted));

        var result = await viewModel.ApplyAsync("step-up secret");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.False(viewModel.HasSetup);
        Assert.Equal(0, viewModel.ExpectedBindingRevision);
        Assert.Equal("CameraSetupOperationResultInvalid", viewModel.ErrorCode);
        Assert.Contains("读回无效", viewModel.StatusMessage);
        Assert.DoesNotContain("已提交", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V117_W10_SuccessForAnotherLogicalRoleClearsOldSetupAndFailsClosed()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var current = fixture.QueryResult.Snapshot!;
        var mismatched = new CameraSetupSnapshot("Secondary", null, current.Health,
            current.Requested, current.Effective, current.Differences,
            current.Extension, "CameraSetupRead", current.Capabilities);
        fixture.Runtime.ApplyHandler = (_, _) => Task.FromResult(
            new CameraSetupOperationResult(true, "CameraConfigurationApplied",
                AuditPersistence.Persisted, mismatched));

        var result = await viewModel.ApplyAsync("step-up secret");

        Assert.NotNull(result);
        Assert.True(result!.Succeeded);
        Assert.False(viewModel.HasSetup);
        Assert.Null(viewModel.CurrentBinding);
        Assert.Equal("CameraSetupOperationResultInvalid", viewModel.ErrorCode);
        Assert.DoesNotContain("已提交", viewModel.StatusMessage);
    }

    [Fact]
    public async Task V117_W11_LogicalRoleUsesPublicSixtyFourCharacterBoundary()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();

        viewModel.LogicalRole = new string('R', 64);
        Assert.Equal(new string('R', 64), viewModel.LogicalRole);
        Assert.Null(viewModel.ErrorCode);

        viewModel.LogicalRole = new string('R', 65);
        Assert.Equal(new string('R', 65), viewModel.LogicalRole);
        Assert.Equal("CameraSetupLogicalRoleInvalid", viewModel.ErrorCode);

        viewModel.LogicalRole = "Primary";
        Assert.Equal("Primary", viewModel.LogicalRole);
        Assert.Null(viewModel.ErrorCode);
    }

    [Fact]
    public async Task V117_W12_PanelClearsPasswordOnSessionAndDataContextChanges()
    {
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            CameraSetupViewModel? first = null;
            CameraSetupViewModel? second = null;
            try
            {
                var firstFixture = Fixture.Create();
                var secondFixture = Fixture.Create();
                first = firstFixture.CreateViewModel();
                second = secondFixture.CreateViewModel();
                var panel = new CameraSetupPanel(first);
                window = new Window
                {
                    Content = panel,
                    Width = 960,
                    Height = 720,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Left = -32000,
                    Top = -32000,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                window.UpdateLayout();

                var passwordBox = Assert.IsType<PasswordBox>(
                    panel.FindName("StepUpPasswordBox"));
                passwordBox.Password = "session-secret";
                firstFixture.Sessions.Publish(new InteractiveSession(
                    InteractiveSessionState.Locked, null, null));
                Assert.Equal(string.Empty, passwordBox.Password);

                passwordBox.Password = "context-secret";
                panel.DataContext = second;
                Assert.Equal(string.Empty, passwordBox.Password);
            }
            catch (Exception exception)
            {
                completed.TrySetException(exception);
            }
            finally
            {
                window?.Close();
                first?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                second?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                completed.TrySetResult(true);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task V117_W13_MatchingFailureSnapshotRemainsVisibleAsExplicitFailureState()
    {
        var fixture = Fixture.Create();
        await using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var current = fixture.QueryResult.Snapshot!;
        var failureHealth = new CameraHealthSnapshot(
            CameraProviderAvailability.DependencyMissing,
            CameraConnectionState.Closed,
            CameraConfigurationState.Unknown,
            CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.Parse("2025-01-01T00:00:01+00:00"), 11),
            new CameraFault(CameraFaultClassification.DependencyUnavailable,
                "CameraProviderUnavailable"));
        var failureSnapshot = new CameraSetupSnapshot("Primary", current.Binding, failureHealth,
            current.Requested, effective: null, differences: Array.Empty<CameraConfigurationDifference>(),
            reasonCode: "CameraSetupUnavailable");
        fixture.Runtime.ApplyHandler = (_, _) => Task.FromResult(
            new CameraSetupOperationResult(false, "CameraProviderUnavailable",
                AuditPersistence.Persisted, failureSnapshot));

        var result = await viewModel.ApplyAsync("step-up secret");

        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
        Assert.NotNull(viewModel.CurrentSetup);
        Assert.Equal(CameraProviderAvailability.DependencyMissing, viewModel.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Closed, viewModel.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, viewModel.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, viewModel.Acquisition);
        Assert.Null(viewModel.EffectiveConfiguration);
        Assert.Equal("CameraProviderUnavailable", viewModel.ErrorCode);
        Assert.Contains("未完成", viewModel.StatusMessage);
    }

    private sealed class Fixture
    {
        public readonly CameraProviderIdentity Provider;
        public readonly CameraDeviceDescriptor[] Devices;
        public readonly FakeRuntime Runtime;
        public readonly FakeSessions Sessions;
        public readonly FakeStepUp StepUp;
        public readonly CameraSetupQueryResult QueryResult;

        private Fixture(CameraProviderIdentity provider, CameraDeviceDescriptor[] devices,
            FakeRuntime runtime, FakeSessions sessions, FakeStepUp stepUp,
            CameraSetupQueryResult queryResult)
        {
            Provider = provider;
            Devices = devices;
            Runtime = runtime;
            Sessions = sessions;
            StepUp = stepUp;
            QueryResult = queryResult;
        }

        public static Fixture Create()
        {
            var provider = new CameraProviderIdentity("Provider.One", "1", "Adapter.Package", "1");
            var devices = new[]
            {
                new CameraDeviceDescriptor(provider, "camera-01", "前置相机"),
                new CameraDeviceDescriptor(provider, "camera-02", "后置相机")
            };
            var target = new CameraBindingTarget(provider, devices[0].StableDeviceIdentity);
            var binding = new CameraBindingRevision(7, "Primary", 7, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                null, new string('A', 64), target, PrincipalId, SessionId, 4,
                "fixture-reason", DateTimeOffset.Parse("2025-01-01T00:00:00+00:00"));
            var requested = new RequestedCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 1000, 0, null);
            var effective = new EffectiveCameraConfiguration(
                requested.ProductionAcquisitionMode, requested.ExposureTimeUs, requested.GainDb,
                requested.RegionOfInterest, requested.PixelFormat, requested.ValidBits,
                requested.AcquisitionTimeoutMs, requested.TriggerDelayUs, requested.WhiteBalanceRgb);
            var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Open, CameraConfigurationState.Applied,
                CameraAcquisitionState.Stopped,
                new FrameTimePoint(DateTimeOffset.Parse("2025-01-01T00:00:00+00:00"), 10));
            var snapshot = new CameraSetupSnapshot("Primary", binding, health, requested, effective,
                Array.Empty<CameraConfigurationDifference>(), reasonCode: "CameraSetupRead");
            var queryResult = new CameraSetupQueryResult(true, "CameraSetupAvailable", snapshot);
            var sessions = new FakeSessions(AuthenticatedSession);
            var runtime = new FakeRuntime(provider, devices, queryResult);
            var stepUp = new FakeStepUp();
            return new Fixture(provider, devices, runtime, sessions, stepUp, queryResult);
        }

        public CameraSetupViewModel CreateViewModel() =>
            new(Runtime, Sessions, StepUp, new InlineDispatcher());
    }

    private sealed class FakeRuntime : ICameraSetupRuntime
    {
        private readonly CameraDiscoveryResult _discovery;
        private CameraSetupQueryResult _queryResult;

        public FakeRuntime(CameraProviderIdentity provider, IReadOnlyList<CameraDeviceDescriptor> devices,
            CameraSetupQueryResult queryResult)
        {
            Providers = new[] { provider };
            _discovery = CameraDiscoveryResult.Success(devices);
            _queryResult = queryResult;
        }

        public IReadOnlyList<CameraProviderIdentity> Providers { get; }
        public int DiscoverCount { get; private set; }
        public int GetSetupCount { get; private set; }
        public int RebindCount { get; private set; }
        public int ApplyCount { get; private set; }
        public CameraRebindRequest? LastRebind { get; private set; }
        public CameraDebugConfigurationRequest? LastApply { get; private set; }
        public Func<CameraProviderIdentity, CommandInvocation, CancellationToken, Task<CameraDiscoveryResult>>?
            DiscoveryHandler { get; set; }
        public Func<string, CommandInvocation, CancellationToken, Task<CameraSetupQueryResult>>?
            QueryHandler { get; set; }
        public Func<CameraRebindRequest, CancellationToken, Task<CameraSetupOperationResult>>?
            RebindHandler { get; set; }
        public Func<CameraDebugConfigurationRequest, CancellationToken, Task<CameraSetupOperationResult>>?
            ApplyHandler { get; set; }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CameraProviderIdentity provider,
            CommandInvocation invocation, CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            if (DiscoveryHandler is not null)
                return new ValueTask<CameraDiscoveryResult>(DiscoveryHandler(provider, invocation, cancellationToken));
            return ValueTask.FromResult(_discovery);
        }

        public ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole,
            CommandInvocation invocation, CancellationToken cancellationToken = default)
        {
            GetSetupCount++;
            if (QueryHandler is not null)
                return new ValueTask<CameraSetupQueryResult>(QueryHandler(logicalRole, invocation, cancellationToken));
            return ValueTask.FromResult(_queryResult);
        }

        public ValueTask<CameraSetupOperationResult> RebindAsync(CameraRebindRequest request,
            CancellationToken cancellationToken = default)
        {
            RebindCount++;
            LastRebind = request;
            if (RebindHandler is not null)
                return new ValueTask<CameraSetupOperationResult>(RebindHandler(request, cancellationToken));
            return ValueTask.FromResult(new CameraSetupOperationResult(true,
                "CameraRebindCompleted", AuditPersistence.Persisted, _queryResult.Snapshot));
        }

        public ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(
            CameraDebugConfigurationRequest request, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            LastApply = request;
            if (ApplyHandler is not null)
                return new ValueTask<CameraSetupOperationResult>(ApplyHandler(request, cancellationToken));
            return ValueTask.FromResult(new CameraSetupOperationResult(true,
                "CameraConfigurationApplied", AuditPersistence.Persisted, _queryResult.Snapshot));
        }
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
            if (Handler is not null)
                return new ValueTask<StepUpResult>(Handler(request, cancellationToken));
            return ValueTask.FromResult(new StepUpResult(true, "StepUpAccepted", GrantId));
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

    private static readonly Guid PrincipalId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid GrantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static InteractiveSession AuthenticatedSession =>
        new(InteractiveSessionState.Authenticated, PrincipalId.ToString("D"), SessionId);
}
