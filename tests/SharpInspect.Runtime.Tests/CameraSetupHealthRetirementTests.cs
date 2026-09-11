using System.Collections.Concurrent;
using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V143 regression coverage for a camera setup operation which meets a live
/// heartbeat probe while retiring the current device.  The provider call is
/// deliberately non-cooperative so the test observes the real ownership handoff.
/// </summary>
public sealed class CameraSetupHealthRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V143_L01_ApplyWaitsForHealthRetirementOrRetainsOwnerOnDeadline(
        bool deadlineWins)
    {
        var identity = ProviderIdentity();
        var existing = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "health-retirement-existing"),
            Capabilities());
        var replacement = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "health-retirement-replacement"),
            Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new RetirementProvider(identity, (_, _) =>
        {
            var opened = Interlocked.Increment(ref openCount) == 1 ? existing : replacement;
            return Task.FromResult(CameraOpenResult.Success(opened));
        });
        await using var harness = Create(provider,
            deadlineWins ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(2));

        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var binding = initial.Snapshot!.Binding!;

        existing.HealthStarted = entered;
        existing.HealthHandler = () => health.Task.GetAwaiter().GetResult();
        harness.Runtime.Heartbeat();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var operation = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "health-retirement"));
        var operationTask = operation.AsTask();
        await harness.Audit.AdmissionWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));

        if (deadlineWins)
        {
            var failed = await operationTask;
            Assert.False(failed.Succeeded);
            Assert.Equal("CameraOperationTimeout", failed.ReasonCode);
            Assert.Equal(1, provider.OpenCount);
            Assert.False(existing.StopCalled);
            Assert.False(existing.Disposed);

            health.TrySetResult(UnconfiguredHealth());
            await Eventually(() => existing.Disposed);
            Assert.True(existing.StopCalled);
            Assert.Equal(1, provider.OpenCount);
        }
        else
        {
            // The operation must still own the old device while the provider
            // health call is blocked.  The previous implementation returned
            // Transferred here and completed a false timeout instead.
            Assert.False(operationTask.IsCompleted);
            health.TrySetResult(UnconfiguredHealth());

            var applied = await operationTask;
            Assert.True(applied.Succeeded, applied.ReasonCode);
            Assert.Equal("CameraConfigurationApplied", applied.ReasonCode);
            Assert.Equal(2, provider.OpenCount);
            Assert.True(existing.StopCalled);
            Assert.True(existing.Disposed);
            Assert.True(replacement.Applied);
            Assert.False(replacement.Disposed);
            Assert.Equal(0, existing.ProviderOverlapCount);
        }
    }

    [Fact]
    public async Task V143_L02_ApplyCancellationDoesNotGrantFreshStopBudget()
    {
        var identity = ProviderIdentity();
        var existing = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "stop-budget-existing"),
            Capabilities());
        var replacement = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "stop-budget-replacement"),
            Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRelease = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new RetirementProvider(identity, (_, _) =>
        {
            var opened = Interlocked.Increment(ref openCount) == 1 ? existing : replacement;
            return Task.FromResult(CameraOpenResult.Success(opened));
        });
        await using var harness = Create(provider, TimeSpan.FromSeconds(2));

        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var binding = initial.Snapshot!.Binding!;
        existing.HealthStarted = entered;
        existing.HealthHandler = () => health.Task.GetAwaiter().GetResult();
        existing.StopHandler = async cancellationToken =>
        {
            stopStarted.TrySetResult(true);
            return await stopRelease.Task.WaitAsync(cancellationToken);
        };
        harness.Runtime.Heartbeat();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var callerCancellation = new CancellationTokenSource();
        var operationTask = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "stop-budget"),
            callerCancellation.Token).AsTask();
        await harness.Audit.AdmissionWritten.Task.WaitAsync(TimeSpan.FromSeconds(2));
        health.TrySetResult(UnconfiguredHealth());
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        callerCancellation.Cancel();
        var cancelled = await operationTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(cancelled.Succeeded);
        Assert.Equal("CameraOperationCancelled", cancelled.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(existing.Disposed);

        stopRelease.TrySetResult(CameraOperationResult.Success());
        await Eventually(() => existing.Disposed);
        Assert.True(existing.StopCalled);
        Assert.Equal(1, provider.OpenCount);
    }

    [Fact]
    public async Task V143_L03_ActivationApplyPropagatesCallerCancellationThroughProbeRetirement()
    {
        var identity = ProviderIdentity();
        var existing = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "activation-health-existing"),
            Capabilities());
        var replacement = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "activation-health-replacement"),
            Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new RetirementProvider(identity, (_, _) =>
        {
            var opened = Interlocked.Increment(ref openCount) == 1 ? existing : replacement;
            return Task.FromResult(CameraOpenResult.Success(opened));
        });
        await using var harness = Create(provider, TimeSpan.FromSeconds(2));

        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);

        await using (lease)
        {
            existing.HealthStarted = entered;
            existing.HealthHandler = () => health.Task.GetAwaiter().GetResult();
            harness.Runtime.Heartbeat();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            using var callerCancellation = new CancellationTokenSource();
            var apply = lease.ApplyAsync(Request(), cancellationToken: callerCancellation.Token).AsTask();
            Assert.False(apply.IsCompleted);

            callerCancellation.Cancel();
            var cancelled = await apply.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(cancelled.Succeeded);
            Assert.Equal("CameraOperationCancelled", cancelled.ReasonCode);
        }

        health.TrySetResult(UnconfiguredHealth());
        await Eventually(() => existing.Disposed);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(replacement.Disposed);
    }

    [Fact]
    public async Task V143_L04_ApplyCancellationAfterDisposeDoesNotOpenReplacement()
    {
        var identity = ProviderIdentity();
        var existing = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "dispose-cancel-existing"),
            Capabilities());
        var replacement = new RetirementDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "dispose-cancel-replacement"),
            Capabilities());
        var openCount = 0;
        var provider = new RetirementProvider(identity, (_, _) =>
        {
            var opened = Interlocked.Increment(ref openCount) == 1 ? existing : replacement;
            return Task.FromResult(CameraOpenResult.Success(opened));
        });
        await using var harness = Create(provider, TimeSpan.FromSeconds(2));

        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var binding = initial.Snapshot!.Binding!;
        using var callerCancellation = new CancellationTokenSource();
        existing.DisposeHandler = callerCancellation.Cancel;

        var cancelled = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "dispose-cancel"),
            callerCancellation.Token);

        Assert.False(cancelled.Succeeded);
        Assert.Equal("CameraOperationCancelled", cancelled.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.True(existing.Disposed);
        Assert.False(replacement.Disposed);
    }

    private static RuntimeHarness Create(RetirementProvider provider, TimeSpan operationTimeout)
    {
        var principal = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var authorizer = new RetirementAuthorizer(principal, sessionId);
        var sessions = new RetirementSessions(new InteractiveSession(
            InteractiveSessionState.Authenticated, principal.ToString("D"), sessionId));
        var identity = new RetirementIdentityQuery();
        var audit = new RetirementAuditWriter();
        var station = new CameraSetupRuntime.CameraStationContext(Guid.NewGuid(), false,
            ProductionArmState.Disarmed, false, null, 0, HandshakePhase.Idle,
            ExclusiveMode.None, RecoveryState.None, false, null, false, false);
        var options = new CameraSetupOptions
        {
            OperationTimeout = operationTimeout,
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        };
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        var runtime = new CameraSetupRuntime(new[] { provider }, options, audit, sessions,
            identity, () => station, (_, snapshot) => published.Enqueue(snapshot), authorizer);
        return new RuntimeHarness(runtime, audit, principal, sessionId);
    }

    private static CameraRebindRequest BindRequest(RuntimeHarness harness,
        CameraProviderIdentity identity) =>
        new(Guid.NewGuid(), harness.NewInvocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "V143-health-bind");

    private static RequestedCameraConfiguration Request() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
        new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 100, 0, null);

    private static CameraHealthSnapshot UnconfiguredHealth() => new(
        CameraProviderAvailability.Available, CameraConnectionState.Open,
        CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
        new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())));

    private static CameraCapabilities Capabilities() => new(
        new[] { ProductionAcquisitionMode.SoftwareTrigger },
        new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
        new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
        new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
        new CameraRoiCapabilities(640, 480,
            new CameraIntCapability(0, 636, 4), new CameraIntCapability(0, 476, 4),
            new CameraIntCapability(4, 640, 4), new CameraIntCapability(4, 480, 4)));

    private static CameraProviderIdentity ProviderIdentity() =>
        new("Test.Camera.Provider", "1", "Test.Camera.Package", "1");

    private static async Task Eventually(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(2))
                throw new TimeoutException("camera retirement did not complete");
            await Task.Delay(10);
        }
    }

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        internal RuntimeHarness(CameraSetupRuntime runtime, RetirementAuditWriter audit,
            Guid principalId, Guid sessionId)
        {
            Runtime = runtime;
            Audit = audit;
            PrincipalId = principalId;
            SessionId = sessionId;
        }

        internal CameraSetupRuntime Runtime { get; }
        internal RetirementAuditWriter Audit { get; }
        internal Guid PrincipalId { get; }
        internal Guid SessionId { get; }

        internal CommandInvocation NewInvocation() =>
            new(CommandSource.PhysicalConsole, PrincipalId.ToString("D"), SessionId);

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class RetirementProvider : ICameraProvider
    {
        private readonly Func<string, CancellationToken, Task<CameraOpenResult>> _open;
        private int _openCount;

        internal RetirementProvider(CameraProviderIdentity identity,
            Func<string, CancellationToken, Task<CameraOpenResult>> open)
        {
            Identity = identity;
            _open = open;
        }

        public CameraProviderIdentity Identity { get; }
        internal int OpenCount => Volatile.Read(ref _openCount);

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCount);
            return new ValueTask<CameraOpenResult>(_open(stableDeviceIdentity, cancellationToken));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RetirementDevice : ICameraDevice
    {
        private readonly object _sync = new();
        private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
        private int _activeProviderCalls;
        private int _providerOverlapCount;

        internal RetirementDevice(CameraDeviceDescriptor descriptor, CameraCapabilities capabilities)
        {
            Descriptor = descriptor;
            Capabilities = capabilities;
        }

        internal Func<CameraHealthSnapshot>? HealthHandler { private get; set; }
        internal Func<CancellationToken, Task<CameraOperationResult>>? StopHandler { private get; set; }
        internal Action? DisposeHandler { private get; set; }
        internal TaskCompletionSource<bool>? HealthStarted { private get; set; }
        internal bool StopCalled { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Applied
        {
            get { lock (_sync) return _configuration == CameraConfigurationState.Applied; }
        }
        internal int ProviderOverlapCount => Volatile.Read(ref _providerOverlapCount);

        public CameraDeviceDescriptor Descriptor { get; }
        public CameraCapabilities Capabilities { get; }

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            EnterProviderCall();
            try
            {
                HealthStarted?.TrySetResult(true);
                var handler = HealthHandler;
                if (handler is not null) return handler();
                lock (_sync)
                {
                    return new CameraHealthSnapshot(CameraProviderAvailability.Available,
                        CameraConnectionState.Open, _configuration,
                        CameraAcquisitionState.Stopped,
                        new FrameTimePoint(DateTimeOffset.UtcNow,
                            Math.Max(0, Stopwatch.GetTimestamp())));
                }
            }
            finally
            {
                ExitProviderCall();
            }
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default)
        {
            var result = Capabilities.ValidateConfiguration(requested);
            if (result.Succeeded)
            {
                lock (_sync) _configuration = CameraConfigurationState.Applied;
            }
            return ValueTask.FromResult(result);
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Failure("V143NotUsed"));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted,
                    "V143NotUsed")));

        public async ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            EnterProviderCall();
            try
            {
                StopCalled = true;
                var handler = StopHandler;
                return handler is null
                    ? CameraOperationResult.Success()
                    : await handler(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ExitProviderCall();
            }
        }

        public ValueTask DisposeAsync()
        {
            EnterProviderCall();
            try
            {
                DisposeHandler?.Invoke();
                Disposed = true;
                return ValueTask.CompletedTask;
            }
            finally
            {
                ExitProviderCall();
            }
        }

        private void EnterProviderCall()
        {
            if (Interlocked.Increment(ref _activeProviderCalls) > 1)
                Interlocked.Increment(ref _providerOverlapCount);
        }

        private void ExitProviderCall() => Interlocked.Decrement(ref _activeProviderCalls);
    }

    private sealed class RetirementAuditWriter : ICommandAuditWriter
    {
        internal TaskCompletionSource<bool> AdmissionWritten { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AuditIntegrityReport? Integrity => null;
        public Task<StoreWriteResult> Initialization =>
            Task.FromResult(new StoreWriteResult(true, "Ready"));
        public TimeSpan CommitTimeout => TimeSpan.FromSeconds(2);

        public ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact,
            StoreDeadline deadline, CancellationToken cancellationToken = default)
        {
            if (fact.ReasonCode == "CameraDebugConfigurationAdmitted")
                AdmissionWritten.TrySetResult(true);
            return ValueTask.FromResult(new StoreWriteResult(true, "Persisted", fact));
        }
    }

    private sealed class RetirementAuthorizer : ICameraSetupAuthorizer
    {
        internal RetirementAuthorizer(Guid principalId, Guid sessionId)
        {
            PrincipalId = principalId;
            SessionId = sessionId;
        }

        private Guid PrincipalId { get; }
        private Guid SessionId { get; }

        public ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
            CommandInvocation invocation, bool readOnly, Guid operationId, string targetId,
            AuditedCommandKind commandKind, CancellationToken cancellationToken = default)
        {
            var reservation = readOnly ? null : CameraSetupAuthorizationReservation.Create(
                static () => { }, static () => { });
            return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                PrincipalId, SessionId, 1, reservation));
        }
    }

    private sealed class RetirementSessions : IInteractiveSessionService
    {
        internal RetirementSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; }
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

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
    }

    private sealed class RetirementIdentityQuery : IIdentityAdministrationQuery
    {
        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
