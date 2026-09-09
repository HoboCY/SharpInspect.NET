using System.Collections.Concurrent;
using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeActivationRuntimeReservationTests
{
    [Fact]
    public async Task V132_C04_CommitClaimWinsBeforeStopAndReleasedLeasesCannotBeReused()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        using var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        var commit = await reservation.EnterCommitAsync(CancellationToken.None);
        Assert.Null(commit.TryBeginCommit());
        var stop = runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole))).AsTask();
        Assert.False(stop.IsCompleted);
        Assert.False(reservation.Token.IsCancellationRequested);
        Assert.Null(commit.GetBlocker());
        commit.Dispose();
        Assert.Equal("RecipeActivationCommitLeaseReleased", commit.TryBeginCommit());
        Assert.Equal(CommandDisposition.Accepted, (await stop.WaitAsync(TimeSpan.FromSeconds(2))).Disposition);
        reservation.PublishTerminal("RecipeActivationCommitFixtureFinished", false);
    }

    [Fact]
    public async Task V132_C05_ReleasedReservationCannotInstallOverItsSuccessor()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter());
        var first = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        first.PublishTerminal("RecipeActivationNotAdmitted", false);
        first.Dispose();
        using var second = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(second.Available, second.Failure);
        using var staleCommit = await first.EnterCommitAsync(CancellationToken.None);
        Assert.Equal("RecipeActivationReservationUnavailable", staleCommit.TryBeginCommit());
        // Rejection occurs before arguments or retained install delegates can be consumed.
        Assert.False(first.Install(null!, null!).Installed);
        Assert.Null((await runtime.GetSnapshotAsync()).ActiveRecipe);
        second.PublishTerminal("RecipeActivationNotAdmitted", false);
    }

    [Fact]
    public async Task V132_C06_MissingTerminalRetainsRecoveryFenceEvenAfterStopCompletes()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await runtime.GetSnapshotAsync(timeout.Token)).LastCommand?.State != OperationState.Completed)
            await Task.Delay(10, timeout.Token);
        reservation.Dispose();
        using var second = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(second.Available);
        Assert.Equal("RecipeActivationRecoveryRequired", second.Failure);
        using var plc = await runtime.EnterPlcResultContractChangeAsync(CancellationToken.None);
        Assert.Equal("RecipeActivationInProgress", plc.GetBlocker());
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.Contains("RecipeActivationRecoveryRequired", snapshot.AdmissionBlockers);
        Assert.DoesNotContain("RecipeActivationInProgress", snapshot.AdmissionBlockers);
        Assert.False(snapshot.Ready);
    }

    [Fact]
    public async Task V132_C07_ShutdownWithBlockedCancellationFailsWithinItsBudgetAndRetainsOwnership()
    {
        var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = reservation.Token.Register(() => { callbackEntered.TrySetResult(true); releaseCallback.Wait(); });
        try
        {
            var shutdown = runtime.DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal("RecipeActivationShutdownIncomplete", failure.Message);
            Assert.NotEqual(RuntimeLifecycle.Stopped, (await runtime.GetSnapshotAsync()).Lifecycle);
            Assert.True(reservation.Available);
        }
        finally
        {
            releaseCallback.Set();
            callback.Dispose();
            reservation.Dispose();
        }
    }

    [Fact]
    public async Task V132_C08_ShutdownBoundsBlockedCameraCancellationWithoutSynchronousCameraCancel()
    {
        await using var fixture = await CameraShutdownFixture.CreateAsync();
        var runtime = fixture.Runtime;
        var camera = (ICameraSetupRuntime)runtime;
        var invocation = fixture.Invocation();
        var binding = new CameraBindingTarget(fixture.Provider.Identity, "Camera:One");
        var rebind = new CameraRebindRequest(Guid.NewGuid(), invocation, "Primary", 0, null,
            binding, "v132-camera-shutdown-bind");
        var grant = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.ManageCameraBindings, rebind.OperationId,
                "Primary", AuditedCommandKind.RebindCamera), fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);

        var rebound = await camera.RebindAsync(rebind with
        { Invocation = invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        using var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(),
            CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);
        var activationCamera = Assert.IsType<CameraSetupRuntime>(reservation.Camera);
        await using var cameraLease = await activationCamera.ReserveRecipeActivationAsync(
            "Primary", reservation.Token);
        Assert.True(cameraLease.Available, cameraLease.ReasonCode);

        var apply = cameraLease.ApplyAsync(fixture.Request, cancellationToken: reservation.Token).AsTask();
        await fixture.Device.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Device.ApplyToken.CanBeCanceled);

        // DisposeAsync must first bound the activation reservation.  Its cancellation
        // callback is deliberately blocked, so the camera lifetime is still untouched
        // while the five-second activation shutdown budget expires.
        var shutdown = runtime.DisposeAsync().AsTask();
        await fixture.Device.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            shutdown.WaitAsync(TimeSpan.FromSeconds(8)));

        Assert.Equal("RecipeActivationShutdownIncomplete", failure.Message);
        Assert.True(shutdown.IsCompleted);
        Assert.True(fixture.Device.ApplyToken.IsCancellationRequested);
        Assert.False(fixture.Device.CancellationReleased.Task.IsCompleted);

        // Release the provider callback only after the bounded station shutdown
        // outcome has been observed; this proves cleanup does not depend on an
        // unbounded synchronous CancelCameraSetupOperations call.
        fixture.Device.ReleaseCancellation.Set();
        fixture.Device.ApplyCompletion.TrySetResult(
            CameraConfigurationResult.Failure("V132CameraShutdownApplyReleased"));
        await apply.WaitAsync(TimeSpan.FromSeconds(3));
        await cameraLease.DisposeAsync();
        await activationCamera.DisposeAsync();
        reservation.Dispose();
    }

    [Fact]
    public async Task V132_C01_PhysicalStopRemainsResponsiveWhileActivationCancellationCallbackIsBlocked()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        using var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = reservation.Token.Register(() =>
        {
            callbackEntered.TrySetResult(true);
            releaseCallback.Wait();
        });
        try
        {
            var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
                new CommandInvocation(CommandSource.PhysicalConsole))).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            Assert.Equal(AuditPersistence.Persisted, stop.Audit);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var commit = await reservation.EnterCommitAsync(CancellationToken.None);
            Assert.Equal("RecipeActivationCancelled", commit.GetBlocker());
            Assert.Equal("RecipeActivationCancelled", commit.TryBeginCommit());
            var state = await runtime.GetSnapshotAsync();
            Assert.False(state.Ready);
            Assert.Equal(ProductionArmState.Disarmed, state.ArmState);
            Assert.Null(state.ActiveRecipe);
        }
        finally
        {
            releaseCallback.Set();
            callback.Dispose();
            reservation.PublishTerminal("RecipeActivationCancelled", false);
        }
    }

    [Fact]
    public async Task V132_C02_ReservationStillBlocksConfigurationAfterStopReplacesLastCommand()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        using var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);
        var correlation = Guid.NewGuid();
        Assert.Equal(CommandDisposition.Accepted, (await runtime.SubmitAsync(new GracefulProductionStopCommand(
            correlation, new CommandInvocation(CommandSource.PhysicalConsole)))).Disposition);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while ((await runtime.GetSnapshotAsync(timeout.Token)).LastCommand?.State != OperationState.Completed)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(correlation, (await runtime.GetSnapshotAsync()).LastCommand!.CorrelationId);
        using var second = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(second.Available);
        Assert.Equal("RecipeActivationInProgress", second.Failure);
        using var plcChange = await runtime.EnterPlcResultContractChangeAsync(CancellationToken.None);
        Assert.Equal("RecipeActivationInProgress", plcChange.GetBlocker());
        reservation.PublishTerminal("RecipeActivationCancelled", false);
    }

    [Fact]
    public async Task V132_C03_ReleaseAllowsNextReservationAndNeverAutomaticallyArms()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        using (var first = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None))
        {
            Assert.True(first.Available, first.Failure);
            first.PublishTerminal("RecipeActivationPrerequisitesUnavailable", false);
        }
        using var second = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(second.Available, second.Failure);
        var snapshot = await runtime.GetSnapshotAsync();
        Assert.False(snapshot.Ready);
        Assert.Equal(ProductionArmState.Disarmed, snapshot.ArmState);
        Assert.Null(snapshot.ActiveRecipe);
        second.PublishTerminal("RecipeActivationPrerequisitesUnavailable", false);
    }

    private sealed class CameraShutdownFixture : IAsyncDisposable
    {
        private const string UserName = "v132.camera.shutdown.admin";
        private const string TestPassword = "V132 camera shutdown integration password 2026!";
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _auditPolicy;
        private bool _disposed;

        private CameraShutdownFixture(string directory, AuditIntegrityPolicy auditPolicy,
            ProductionStoreOptions options, SqliteCommandStore store, LocalIdentityService identity,
            InteractiveSessionService sessions, LocalAuthorizationService authorization,
            StationRuntime runtime, BlockingCameraProvider provider)
        {
            _directory = directory;
            _auditPolicy = auditPolicy;
            Options = options;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            Runtime = runtime;
            Provider = provider;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal StationRuntime Runtime { get; }
        internal BlockingCameraProvider Provider { get; }
        internal BlockingCameraDevice Device => Provider.ActivationDevice;
        internal string Password => TestPassword;
        internal RequestedCameraConfiguration Request => new(
            ProductionAcquisitionMode.SoftwareTrigger, 20, 0,
            new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null,
            100, 0, null);

        internal CommandInvocation Invocation() => new(CommandSource.PhysicalConsole,
            Sessions.Current.PrincipalId, Sessions.Current.SessionId);

        internal Task WaitForVerifiedAsync() => WaitForVerifiedAsync(Store);

        internal static async Task<CameraShutdownFixture> CreateAsync()
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Camera shutdown integration requires Windows machine-key protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V132-CameraShutdown-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V132CameraShutdownStation";
            var audit = new AuditIntegrityPolicy(station, "v132-camera-shutdown-v1",
                "SharpInspect.T32.CameraShutdown." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identityOptions = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v132-camera-shutdown-blocklist", "1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var provider = BlockingCameraProvider.Create();
            var options = new ProductionStoreOptions(Path.Combine(directory, "camera-shutdown.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                CameraSetup = new CameraSetupStoreOptions(),
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 32
            };

            SqliteCommandStore? store = null;
            LocalIdentityService? identity = null;
            InteractiveSessionService? sessions = null;
            LocalAuthorizationService? authorization = null;
            StationRuntime? runtime = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);

                identity = new LocalIdentityService(store, identityOptions, new FixtureConsole());
                var issued = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(issued.Succeeded, issued.ReasonCode);
                var bootstrap = issued.Token!.TakeForDisplay();
                issued.Token.Dispose();
                await WaitForVerifiedAsync(store);
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(station, bootstrap, UserName,
                        "T32 Camera Shutdown Administrator", TestPassword));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await WaitForVerifiedAsync(store);

                sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                var login = await sessions.SignInAsync(new PasswordSignInRequest(UserName, TestPassword));
                Assert.True(login.Succeeded, login.ReasonCode);
                authorization = new LocalAuthorizationService(store, identityOptions, identity, sessions);
                await WaitForVerifiedAsync(store);

                runtime = new StationRuntime(store, TimeSpan.FromMilliseconds(20), sessions,
                    authorization, cameraProviders: new[] { provider },
                    cameraSetupOptions: new CameraSetupOptions
                    {
                        OperationTimeout = TimeSpan.FromSeconds(2),
                        ShutdownTimeout = TimeSpan.FromSeconds(2)
                    }, productionStoreOptions: options);
                return new CameraShutdownFixture(directory, audit, options, store, identity,
                    sessions, authorization, runtime, provider);
            }
            catch
            {
                if (runtime is not null)
                {
                    try { await runtime.DisposeAsync(); }
                    catch (InvalidOperationException) { }
                }
                authorization?.Dispose();
                if (sessions is not null) await sessions.DisposeAsync();
                if (store is not null) await store.DisposeAsync();
                Cleanup(directory, audit);
                throw;
            }
        }

        private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Camera shutdown audit did not become Verified: " +
                store.Integrity?.ReasonCode);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                try { await Runtime.DisposeAsync(); }
                catch (InvalidOperationException exception) when (
                    exception.Message == "RecipeActivationShutdownIncomplete") { }
            }
            finally
            {
                Authorization.Dispose();
                await Sessions.DisposeAsync();
                await Store.DisposeAsync();
                Cleanup(_directory, _auditPolicy);
            }
        }

        private static void Cleanup(string directory, AuditIntegrityPolicy policy)
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                var fullDirectory = Path.GetFullPath(directory);
                var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                    "SharpInspect.Runtime.Tests"));
                if (fullDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class FixtureConsole : IPhysicalConsoleAuthority
        {
            public ConsoleAuthority Observe() => new(true, true,
                "S-1-5-21-T32-CameraShutdown");
        }
    }

    private sealed class BlockingCameraProvider : ICameraProvider
    {
        private readonly ConcurrentQueue<ICameraDevice> _devices;

        private BlockingCameraProvider(CameraProviderIdentity identity,
            BlockingCameraDevice binding, BlockingCameraDevice activation)
        {
            Identity = identity;
            BindingDevice = binding;
            ActivationDevice = activation;
            _devices = new ConcurrentQueue<ICameraDevice>(new ICameraDevice[]
                { binding, activation });
        }

        internal static BlockingCameraProvider Create()
        {
            var identity = new CameraProviderIdentity("T32.CameraShutdown.Provider", "1",
                "T32.CameraShutdown.Adapter", "1");
            var binding = new BlockingCameraDevice(
                new CameraDeviceDescriptor(identity, "Camera:One", "T32 shutdown binding"), false);
            var activation = new BlockingCameraDevice(
                new CameraDeviceDescriptor(identity, "Camera:One", "T32 shutdown activation"), true);
            return new BlockingCameraProvider(identity, binding, activation);
        }

        public CameraProviderIdentity Identity { get; }
        internal BlockingCameraDevice BindingDevice { get; }
        internal BlockingCameraDevice ActivationDevice { get; }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(new[]
            {
                BindingDevice.Descriptor, ActivationDevice.Descriptor
            }));

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default) =>
            _devices.TryDequeue(out var device)
                ? ValueTask.FromResult(CameraOpenResult.Success(device))
                : ValueTask.FromResult(CameraOpenResult.Failure("V132CameraShutdownDeviceExhausted"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingCameraDevice : ICameraDevice
    {
        private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
        private readonly bool _blockApply;
        private bool _disposed;

        internal BlockingCameraDevice(CameraDeviceDescriptor descriptor, bool blockApply)
        { Descriptor = descriptor; _blockApply = blockApply; }

        internal TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CancellationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CancellationReleased { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CameraConfigurationResult> ApplyCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim ReleaseCancellation { get; } = new();
        internal CancellationToken ApplyToken { get; private set; }
        internal CameraDeviceDescriptor Descriptor { get; }
        internal CameraCapabilities Capabilities { get; } = new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(10, 100, 10, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 100, 5, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(640, 480,
                new CameraIntCapability(0, 636, 4), new CameraIntCapability(0, 476, 4),
                new CameraIntCapability(4, 640, 4), new CameraIntCapability(4, 480, 4)));
        CameraDeviceDescriptor ICameraDevice.Descriptor => Descriptor;
        CameraCapabilities ICameraDevice.Capabilities => Capabilities;

        public CameraHealthSnapshot GetHealthSnapshot() => new(
            CameraProviderAvailability.Available,
            _disposed ? CameraConnectionState.Closed : CameraConnectionState.Open,
            _disposed ? CameraConfigurationState.Unconfigured : _configuration,
            CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())));

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            if (_blockApply)
            {
                ApplyToken = cancellationToken;
                ApplyStarted.TrySetResult(true);
                return new ValueTask<CameraConfigurationResult>(WaitForApplyAsync(cancellationToken));
            }

            var result = Capabilities.ValidateConfiguration(requested);
            if (result.Succeeded) _configuration = CameraConfigurationState.Applied;
            return ValueTask.FromResult(result);
        }

        private async Task<CameraConfigurationResult> WaitForApplyAsync(CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                CancellationEntered.TrySetResult(true);
                try { ReleaseCancellation.Wait(); }
                finally { CancellationReleased.TrySetResult(true); }
            });
            return await ApplyCompletion.Task.ConfigureAwait(false);
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Failure("V132CameraShutdownStartNotUsed"));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted,
                    "V132CameraShutdownAcquireNotUsed")));

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            _configuration = CameraConfigurationState.Unconfigured;
            return ValueTask.FromResult(CameraOperationResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
