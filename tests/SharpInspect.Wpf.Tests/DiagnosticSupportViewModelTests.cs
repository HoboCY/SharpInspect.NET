using System.Globalization;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Wpf.Tests;

public sealed class DiagnosticSupportViewModelTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Epoch = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Password = "ephemeral-fixture-password";

    [Fact]
    [Trait("VerificationId", "V158_W01")]
    public async Task V158_W01_StartCaptureFreezesExactProfileAndBindsFreshStepUpGrant()
    {
        var runtime = new Runtime();
        var query = new Query();
        var sessions = new Sessions();
        var stepUp = new StepUp();
        await using var model = Model(runtime, query, sessions, stepUp);
        await model.RefreshAsync();
        Assert.Equal("基线（未提升）", model.CapturePhaseText);
        Assert.True(model.CanStartCapture);
        Assert.True(model.CanRefresh);

        model.SelectedComponent = "Algorithm";
        model.SelectedCaptureLevel = "Debug";
        model.DurationSecondsText = "120";
        model.MaximumEventsText = "250";
        model.SelectedReason = model.ReasonOptions.Single(option => option.Value == DiagnosticSupportReason.FaultInvestigation);

        var outcome = await model.StartCaptureWithStepUpAsync(Password);

        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        var command = Assert.IsType<StartDiagnosticCaptureCommand>(runtime.Last);
        Assert.Equal(DiagnosticSupportReason.FaultInvestigation, command.Reason);
        Assert.Equal("Algorithm", Assert.Single(command.Profile.Components));
        Assert.Equal(DiagnosticLevel.Debug, command.Profile.MinimumLevel);
        Assert.Equal(TimeSpan.FromSeconds(120), command.Profile.Duration);
        Assert.Equal(250, command.Profile.MaximumEvents);
        var expectedProfile = new DiagnosticCaptureProfile(LoggingPolicy().ContentHash, new[] { "Algorithm" },
            DiagnosticLevel.Debug, TimeSpan.FromSeconds(120), 250);
        Assert.Equal(expectedProfile.ContentHash, command.Profile.ContentHash);
        Assert.Equal(new StepUpBinding(Permission.StartDiagnosticCapture, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.StartDiagnosticCapture), stepUp.Binding);
        Assert.Equal(stepUp.Grant, command.Invocation.StepUpGrantId);
        Assert.Equal(sessions.Current.SessionId, command.Invocation.SessionId);
        Assert.Equal(1, stepUp.CallCount);
        Assert.DoesNotContain(Password, model.StatusMessage);
        foreach (var property in model.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length != 0) continue;
            Assert.DoesNotContain(Password, property.GetValue(model)?.ToString() ?? string.Empty);
        }
    }

    [Fact]
    [Trait("VerificationId", "V158_W02")]
    public async Task V158_W02_RejectedStepUpNeverReachesTheRuntime()
    {
        var runtime = new Runtime();
        var stepUp = new StepUp(_ => Task.FromResult(new StepUpResult(false, "Denied")));
        await using var model = Model(runtime, new Query(), new Sessions(), stepUp);
        await model.RefreshAsync();

        Assert.Null(await model.StartCaptureWithStepUpAsync(Password));

        Assert.Equal(1, stepUp.CallCount);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Null(runtime.Last);
        Assert.Contains("身份验证未通过", model.StatusMessage);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData("component")]
    [InlineData("level")]
    [InlineData("duration")]
    [InlineData("events")]
    [InlineData("reason")]
    [InlineData("window")]
    [InlineData("lock")]
    [InlineData("deactivate")]
    [Trait("VerificationId", "V158_W03")]
    public async Task V158_W03_ChangedInputSessionOrNavigationDuringStepUpNeverSubmits(string change)
    {
        var runtime = new Runtime();
        var sessions = new Sessions();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepUp = new StepUp(_ => { entered.TrySetResult(true); return release.Task; });
        await using var model = Model(runtime, new Query(), sessions, stepUp);
        await model.RefreshAsync();
        model.FromUtcText = "2026-09-16T00:00:00Z";
        model.ThroughUtcText = "2026-09-16T00:30:00Z";

        var bundle = change == "window";
        var submit = bundle
            ? model.CreateBundleWithStepUpAsync(Password)
            : model.StartCaptureWithStepUpAsync(Password);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(model.IsBusy);
        switch (change)
        {
            case "component": model.SelectedComponent = "Runtime"; break;
            case "level": model.SelectedCaptureLevel = "Trace"; break;
            case "duration": model.DurationSecondsText = "5"; break;
            case "events": model.MaximumEventsText = "7"; break;
            case "reason": model.SelectedReason = model.ReasonOptions[1]; break;
            case "window": model.ThroughUtcText = "2026-09-16T00:45:00Z"; break;
            case "lock": sessions.Lock(); break;
            default: model.Deactivate(); break;
        }
        release.TrySetResult(new StepUpResult(true, "Authenticated", stepUp.Grant));

        Assert.Null(await submit.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, stepUp.CallCount);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Null(runtime.Last);
        Assert.False(model.IsBusy);
    }

    [Fact]
    [Trait("VerificationId", "V158_W04")]
    public async Task V158_W04_StopCaptureUsesAuthoritativeSessionAndOwnBinding()
    {
        var runtime = new Runtime();
        var captureSessionId = Guid.NewGuid();
        var query = new Query
        {
            Capture = CaptureSnapshot(DiagnosticCapturePhase.Active, captureSessionId, observed: 12, maximum: 500,
                started: Utc(9, 0), expires: Utc(9, 5))
        };
        var sessions = new Sessions();
        var stepUp = new StepUp();
        await using var model = Model(runtime, query, sessions, stepUp);
        await model.RefreshAsync();
        Assert.True(model.CanStopCapture);
        Assert.False(model.CanStartCapture);

        var outcome = await model.StopCaptureWithStepUpAsync(Password);

        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        var command = Assert.IsType<StopDiagnosticCaptureCommand>(runtime.Last);
        Assert.Equal(captureSessionId, command.OperationId);
        Assert.Equal(new StepUpBinding(Permission.StartDiagnosticCapture, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.StopDiagnosticCapture), stepUp.Binding);
        Assert.Equal(stepUp.Grant, command.Invocation.StepUpGrantId);
        Assert.Equal("未读取", model.CapturePhaseText);
        Assert.False(model.CanStopCapture);
        Assert.Contains("已接纳", model.StatusMessage);
        Assert.Contains("刷新", model.StatusMessage);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("events")]
    [InlineData("component")]
    [InlineData("level")]
    [Trait("VerificationId", "V158_W05")]
    public async Task V158_W05_ProfileLimitsUseDeployedPolicy(string invalid)
    {
        var runtime = new Runtime();
        var stepUp = new StepUp();
        await using var model = Model(runtime, new Query(), new Sessions(), stepUp,
            LoggingPolicy(TimeSpan.FromMinutes(10), 900));
        await model.RefreshAsync();
        switch (invalid)
        {
            case "duration": model.DurationSecondsText = "601"; break;
            case "events": model.MaximumEventsText = "901"; break;
            case "component": model.SelectedComponent = "NotApproved"; break;
            default: model.SelectedCaptureLevel = "Information"; break;
        }

        Assert.False(model.CanStartCapture);
        Assert.Null(await model.StartCaptureWithStepUpAsync(Password));

        Assert.Equal(0, stepUp.CallCount);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.False(model.IsBusy);
    }

    [Fact]
    [Trait("VerificationId", "V158_W06")]
    public async Task V158_W06_DefaultBundleFreezesExplicitUtcWindowAndCurrentEpoch()
    {
        var runtime = new Runtime();
        var sessions = new Sessions();
        var stepUp = new StepUp();
        var supportPolicy = SupportPolicy(TimeSpan.FromHours(6));
        await using var model = Model(runtime, new Query(), sessions, stepUp, supportPolicy: supportPolicy);
        await model.RefreshAsync();
        Assert.Equal(Epoch.ToString("D"), model.RuntimeEpochText);
        model.FromUtcText = "2026-09-16T00:00:00Z";
        model.ThroughUtcText = "2026-09-16T01:30:00Z";
        Assert.True(model.CanCreateBundle);

        var outcome = await model.CreateBundleWithStepUpAsync(Password);

        Assert.Equal(CommandDisposition.Accepted, outcome!.Disposition);
        var command = Assert.IsType<CreateSupportBundleCommand>(runtime.Last);
        Assert.Equal(Epoch, command.Scope.RuntimeEpoch);
        Assert.Equal(TimeSpan.Zero, command.Scope.FromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, command.Scope.ThroughUtc.Offset);
        Assert.Equal(Utc(0, 0), command.Scope.FromUtc);
        Assert.Equal(Utc(1, 30), command.Scope.ThroughUtc);
        Assert.Empty(command.Scope.Executions);
        Assert.Empty(command.Scope.CommandCorrelations);
        Assert.Empty(command.Scope.CaptureSessions);
        Assert.Equal(supportPolicy.ContentHash, command.SupportPolicyHash);
        var expectedScope = new SupportBundleScope(Epoch, Utc(0, 0), Utc(1, 30),
            Array.Empty<ExecutionCorrelationId>(), Array.Empty<Guid>(), Array.Empty<Guid>());
        Assert.Equal(expectedScope.ContentHash, command.Scope.ContentHash);
        Assert.Equal(new StepUpBinding(Permission.ExportSupportBundle, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.CreateSupportBundle), stepUp.Binding);
        Assert.Equal(stepUp.Grant, command.Invocation.StepUpGrantId);
        Assert.Empty(model.BundleIdText);
        Assert.Empty(model.BundleHashText);
        Assert.Empty(model.BundleBytesText);
        Assert.Contains("已接纳", model.StatusMessage);
        Assert.Contains("完成后刷新", model.StatusMessage);
    }

    [Theory]
    [InlineData("reversed")]
    [InlineData("beyond")]
    [InlineData("missing")]
    [Trait("VerificationId", "V158_W07")]
    public async Task V158_W07_InvalidOrOversizedUtcWindowNeverAuthenticates(string scenario)
    {
        var runtime = new Runtime();
        var stepUp = new StepUp();
        await using var model = Model(runtime, new Query(), new Sessions(), stepUp,
            supportPolicy: SupportPolicy(TimeSpan.FromHours(1)));
        await model.RefreshAsync();
        switch (scenario)
        {
            case "reversed":
                model.FromUtcText = "2026-09-16T02:00:00Z";
                model.ThroughUtcText = "2026-09-16T01:00:00Z";
                break;
            case "beyond":
                model.FromUtcText = "2026-09-16T00:00:00Z";
                model.ThroughUtcText = "2026-09-16T03:00:00Z";
                break;
            default:
                model.FromUtcText = string.Empty;
                model.ThroughUtcText = string.Empty;
                break;
        }

        Assert.False(model.CanCreateBundle);
        Assert.Null(await model.CreateBundleWithStepUpAsync(Password));

        Assert.Equal(0, stepUp.CallCount);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.False(model.IsBusy);
    }

    [Fact]
    [Trait("VerificationId", "V158_W08")]
    public async Task V158_W08_RuntimeEpochChangeDuringStepUpPreventsBundleSubmission()
    {
        var runtime = new Runtime();
        var sessions = new Sessions();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepUpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepUp = new StepUp(_ => { entered.TrySetResult(true); return release.Task; });
        await using var model = Model(runtime, new Query(), sessions, stepUp);
        await model.RefreshAsync();
        model.FromUtcText = "2026-09-16T00:00:00Z";
        model.ThroughUtcText = "2026-09-16T00:30:00Z";

        var submit = model.CreateBundleWithStepUpAsync(Password);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        runtime.Snapshot = StationSnapshot(Guid.NewGuid());
        release.TrySetResult(new StepUpResult(true, "Authenticated", stepUp.Grant));

        Assert.Null(await submit.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, stepUp.CallCount);
        Assert.Equal(0, runtime.SubmitCount);
        Assert.Contains("Runtime 标识已变化", model.StatusMessage);
        Assert.False(model.IsBusy);
    }

    [Fact]
    [Trait("VerificationId", "V158_W09")]
    public async Task V158_W09_AcceptedCaptureAndBundleNeverDisplayCompletion()
    {
        var runtime = new Runtime();
        var query = new Query();
        var sessions = new Sessions();
        var stepUp = new StepUp();
        await using var model = Model(runtime, query, sessions, stepUp);
        await model.RefreshAsync();

        Assert.Equal(CommandDisposition.Accepted,
            (await model.StartCaptureWithStepUpAsync(Password))!.Disposition);
        Assert.Equal("未读取", model.CapturePhaseText);
        Assert.Contains("尚未激活", model.StatusMessage);
        Assert.False(model.CanStopCapture);

        var captureSessionId = Guid.NewGuid();
        query.Capture = CaptureSnapshot(DiagnosticCapturePhase.Active, captureSessionId, observed: 4, maximum: 800);
        await model.RefreshAsync();
        Assert.Equal("进行中", model.CapturePhaseText);
        Assert.True(model.CanStopCapture);

        model.FromUtcText = "2026-09-16T00:00:00Z";
        model.ThroughUtcText = "2026-09-16T00:30:00Z";
        Assert.Equal(CommandDisposition.Accepted,
            (await model.CreateBundleWithStepUpAsync(Password))!.Disposition);
        Assert.Empty(model.BundleIdText);
        Assert.Empty(model.BundleHashText);
        Assert.Empty(model.BundleBytesText);
        Assert.Contains("已接纳", model.StatusMessage);
        Assert.Contains("完成后刷新", model.StatusMessage);

        var bundleId = Guid.NewGuid();
        query.Bundle = BundleSnapshot(SupportBundlePhase.Completed, bundleId, Hash, 123456, Utc(10, 0));
        await model.RefreshAsync();
        Assert.Equal("已完成", model.BundlePhaseText);
        Assert.Equal(bundleId.ToString("D"), model.BundleIdText);
        Assert.Equal(Hash, model.BundleHashText);
        Assert.Contains("123456", model.BundleBytesText);
        Assert.Contains("到期", model.BundleExpiryText);
    }

    [Fact]
    [Trait("VerificationId", "V158_W10")]
    public async Task V158_W10_RefreshShowsAuthoritativeCountsAndRejectsInconsistentState()
    {
        var runtime = new Runtime();
        var captureSessionId = Guid.NewGuid();
        var query = new Query
        {
            Capture = CaptureSnapshot(DiagnosticCapturePhase.Active, captureSessionId, observed: 7, maximum: 500,
                started: Utc(9, 0), expires: Utc(9, 10)),
            Bundle = BundleSnapshot(SupportBundlePhase.Collecting)
        };
        var stepUp = new StepUp();
        await using var model = Model(runtime, query, new Sessions(), stepUp);
        await model.RefreshAsync();

        Assert.Equal(Epoch.ToString("D"), model.RuntimeEpochText);
        Assert.Equal("进行中", model.CapturePhaseText);
        Assert.Contains("7", model.CaptureEventsText);
        Assert.Contains("500", model.CaptureEventsText);
        Assert.Contains("2026-09-16 09:00:00", model.CaptureWindowText);
        Assert.Contains("2026-09-16 09:10:00", model.CaptureWindowText);
        Assert.Equal("正在收集", model.BundlePhaseText);
        Assert.Empty(model.BundleIdText);

        query.Capture = CaptureSnapshot(DiagnosticCapturePhase.Active, captureSessionId, epoch: Guid.NewGuid());
        await model.RefreshAsync();
        Assert.Equal("未读取", model.CapturePhaseText);
        Assert.Contains("不可用", model.StatusMessage);
        Assert.False(model.CanStartCapture);
        Assert.False(model.CanStopCapture);

        query.Capture = CaptureSnapshot(DiagnosticCapturePhase.Active, captureSessionId, observed: 7, maximum: 500);
        query.Bundle = BundleSnapshot(SupportBundlePhase.Completed, Guid.NewGuid(), contentHash: null, bytes: 128);
        await model.RefreshAsync();
        Assert.Equal("未读取", model.BundlePhaseText);
        Assert.Empty(model.BundleIdText);
        Assert.Empty(model.BundleHashText);
        Assert.Contains("不可用", model.StatusMessage);
    }

    private static DiagnosticSupportViewModel Model(Runtime runtime, Query query, Sessions sessions,
        StepUp stepUp, LoggingDiagnosticsPolicy? loggingPolicy = null,
        DiagnosticSupportPolicy? supportPolicy = null) =>
        new(runtime, query, stepUp, sessions, loggingPolicy ?? LoggingPolicy(),
            supportPolicy ?? SupportPolicy(TimeSpan.FromHours(6)), new InlineUiDispatcher());

    private static LoggingDiagnosticsPolicy LoggingPolicy(TimeSpan? maximumCaptureDuration = null,
        int maximumCaptureEvents = 1000)
    {
        var safeFiles = new DiagnosticFileBudget(1024, 8192, 4, 32768,
            TimeSpan.FromMinutes(5), TimeSpan.FromDays(7));
        var protectedFiles = new DiagnosticFileBudget(1024, 8192, 4, 32768,
            TimeSpan.FromMinutes(5), TimeSpan.FromDays(3));
        var safeQueue = new DiagnosticQueueBudget(64, 65536, 16, 16384,
            DiagnosticLevel.Warning, TimeSpan.FromSeconds(5));
        var protectedQueue = new DiagnosticQueueBudget(64, 65536, 16, 16384,
            DiagnosticLevel.Warning, TimeSpan.FromSeconds(5));
        var forwardedQueue = new DiagnosticQueueBudget(64, 65536, 16, 16384,
            DiagnosticLevel.Warning, TimeSpan.FromSeconds(5));
        var producers = new DiagnosticProducerBudget(1000, 1048576, 16, 128, 100,
            TimeSpan.FromMinutes(1), 8);
        var contracts = new[]
        {
            new DiagnosticEventContract("DIAG-ALGO-EVENT", 1, "Algorithm", DiagnosticLevel.Debug, true,
                Array.Empty<DiagnosticFieldContract>()),
            new DiagnosticEventContract("DIAG-RUNTIME-EVENT", 1, "Runtime", DiagnosticLevel.Information, false,
                Array.Empty<DiagnosticFieldContract>())
        };
        return new LoggingDiagnosticsPolicy("logging-policy", "1", "approval-1", "trace-v1", Hash,
            DiagnosticLevel.Information, contracts, producers, safeQueue, protectedQueue, forwardedQueue,
            safeFiles, protectedFiles, 10, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5),
            100, 4096, maximumCaptureDuration ?? TimeSpan.FromMinutes(10), maximumCaptureEvents);
    }

    private static DiagnosticSupportPolicy SupportPolicy(TimeSpan maximumScope) =>
        new("support-policy", "1", "approval-1", Hash, Hash, maximumScope, 100, 4096, 65536,
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(100), TimeSpan.FromDays(1),
            32, 8192, 65536);

    private static StationStateSnapshot StationSnapshot(Guid epoch) => new(
        epoch, 1, DateTimeOffset.UtcNow, RuntimeLifecycle.Running, ExclusiveMode.None,
        ProductionArmState.Disarmed, false, false, HandshakePhase.Idle, RecoveryState.None,
        null, null,
        new CameraHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
        new PlcHealth(HealthState.Healthy, HealthState.Healthy, HealthState.Healthy),
        new SubsystemHealth(HealthState.Healthy, "Ready"),
        new EvidenceHealth(HealthState.Healthy, 0, 0),
        new QualificationState(QualificationMatch.Matches, QualificationMatch.Matches,
            QualificationMatch.Matches, QualificationMatch.Matches),
        new PerformanceHealth(HealthState.Healthy, false),
        new AlarmSummary(0, 0, false),
        new InteractiveSession(InteractiveSessionState.Unauthenticated, null, null),
        null, new AdmissionBlockers(Array.Empty<string>()));

    private static DiagnosticCaptureSnapshot CaptureSnapshot(
        DiagnosticCapturePhase phase = DiagnosticCapturePhase.Baseline, Guid? sessionId = null,
        long observed = 0, int? maximum = null, DateTimeOffset? started = null,
        DateTimeOffset? expires = null, Guid? epoch = null) => new(true, epoch ?? Epoch, sessionId, phase,
        phase is DiagnosticCapturePhase.Admitted or DiagnosticCapturePhase.Active or DiagnosticCapturePhase.Draining,
        "Baseline", null, observed, maximum, started, expires);

    private static SupportBundleSnapshot BundleSnapshot(SupportBundlePhase phase = SupportBundlePhase.Idle,
        Guid? bundleId = null, string? contentHash = null, long? bytes = null,
        DateTimeOffset? expires = null, Guid? epoch = null) =>
        new(true, epoch ?? Epoch, bundleId, phase, "Idle", contentHash, bytes, expires);

    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 9, 16, hour, minute, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("lock")]
    [InlineData("deactivate")]
    [Trait("VerificationId", "V158_W11")]
    public async Task V158_W11_AuthorityOrNavigationLossDuringFinalEpochReadNeverSubmits(string change)
    {
        var runtime = new Runtime();
        var sessions = new Sessions();
        await using var model = Model(runtime, new Query(), sessions, new StepUp());
        await model.RefreshAsync();
        model.FromUtcText = "2026-09-16T00:00:00Z";
        model.ThroughUtcText = "2026-09-16T00:30:00Z";
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StationStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SnapshotRead = () => { entered.TrySetResult(true); return release.Task; };
        var submit = model.CreateBundleWithStepUpAsync(Password);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (change == "lock") sessions.Lock(); else model.Deactivate();
        }
        finally { release.TrySetResult(runtime.Snapshot); }
        Assert.Null(await submit.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, runtime.SubmitCount);
    }

    private sealed class Runtime : IStationRuntime
    {
        internal Runtime(Guid? epoch = null) => Snapshot = StationSnapshot(epoch ?? Epoch);
        internal StationStateSnapshot Snapshot { get; set; }
        internal RuntimeCommand? Last { get; private set; }
        internal int SubmitCount { get; private set; }
        internal Func<Task<StationStateSnapshot>>? SnapshotRead { get; set; }
        public ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            SnapshotRead is { } read ? new ValueTask<StationStateSnapshot>(read()) : ValueTask.FromResult(Snapshot);
        public IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            Last = command;
            SubmitCount++;
            return ValueTask.FromResult(new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Accepted, "Persisted", AuditPersistence.Persisted));
        }
    }

    private sealed class Query : IDiagnosticSupportQuery
    {
        internal DiagnosticCaptureSnapshot Capture { get; set; } = CaptureSnapshot();
        internal SupportBundleSnapshot Bundle { get; set; } = BundleSnapshot();
        public DiagnosticCaptureSnapshot ReadCapture() => Capture;
        public SupportBundleSnapshot ReadBundle() => Bundle;
    }

    private sealed class StepUp : IStepUpAuthentication
    {
        private readonly Func<StepUpRequest, Task<StepUpResult>>? _response;
        internal StepUp(Func<StepUpRequest, Task<StepUpResult>>? response = null) => _response = response;
        internal Guid Grant { get; } = Guid.NewGuid();
        internal StepUpBinding? Binding { get; private set; }
        internal int CallCount { get; private set; }
        public async ValueTask<StepUpResult> ReauthenticateAsync(StepUpRequest request,
            CancellationToken cancellationToken = default)
        {
            Binding = request.Binding;
            CallCount++;
            return _response is null
                ? new StepUpResult(true, "Authenticated", Grant)
                : await _response(request);
        }
    }

    private sealed class Sessions : IInteractiveSessionService
    {
        public InteractiveSession Current { get; private set; } = new(
            InteractiveSessionState.Authenticated, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            Guid.NewGuid());
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;
        internal void Lock()
        {
            Current = new InteractiveSession(InteractiveSessionState.Locked, null, null);
            Changed?.Invoke(this, new InteractiveSessionChangedEventArgs(Current));
        }
        public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
        public ValueTask<SessionSignInResult> SignInAsync(PasswordSignInRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LockAsync(Guid? expectedSessionId, SessionLockReason reason,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> LogoutAsync(Guid? expectedSessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SessionActionResult> ReportActivityAsync(Guid sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
