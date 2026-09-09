using System.Collections.Concurrent;
using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T32 camera activation lease tests.  They exercise the physical camera
/// transaction directly while leaving authorization and the durable Active
/// commit to the owning StationRuntime command.
/// </summary>
public sealed partial class RecipeActivationCameraTests
{
    [Fact]
    public async Task V132_R12_StopBeforeFirstPhysicalClaimPreservesBaselineAndItsDeviceOwner()
    {
        var identity = ProviderIdentity();
        var bound = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var baselineDevice = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "baseline"), Capabilities());
        var provider = new FakeProvider(identity, bound, baselineDevice);
        await using var harness = Create(provider);
        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(), harness.Invocation(),
            "Primary", 0, null, new CameraBindingTarget(identity, "Camera:One"), "bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baseline = await harness.Runtime.ApplyDebugConfigurationAsync(new CameraDebugConfigurationRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", rebound.Snapshot!.Binding!.Revision,
            rebound.Snapshot.Binding.RevisionHash, Request(10), "baseline"));
        Assert.True(baseline.Succeeded, baseline.ReasonCode);
        await using (var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary"))
        {
            var rejected = await lease.ApplyAsync(Request(20), physicalPhaseFactory: () =>
                RecipeActivationPhysicalPhaseClaim.Unavailable("RecipeActivationCancelled"));
            Assert.False(rejected.Succeeded);
            Assert.False(rejected.HardwareTouched);
            Assert.Equal("RecipeActivationCancelled", rejected.ReasonCode);
            var unchanged = await harness.Runtime.GetSetupAsync("Primary", harness.Invocation());
            Assert.Equal(baseline.Snapshot, unchanged.Snapshot);
            Assert.False(baselineDevice.Disposed);
            Assert.Equal(2, provider.OpenCount);
        }
        await harness.DisposeAsync();
        Assert.True(baselineDevice.Disposed);
    }

    [Fact]
    public async Task V132_R01_LeaseStagesCandidateAndInstallsItOnlyAfterCommit()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);
        var staged = await lease.ApplyAsync(Request(20));

        Assert.True(staged.Succeeded, staged.ReasonCode);
        Assert.True(staged.HardwareTouched);
        Assert.False(lease.Committed);
        Assert.False(candidate.Disposed);
        Assert.Equal(20, staged.Snapshot!.Effective!.ExposureTimeUs);

        Assert.True(lease.Commit());
        Assert.True(lease.Committed);
        await lease.DisposeAsync();

        var current = await harness.Runtime.GetSetupAsync("Primary", harness.Invocation());
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(20, current.Snapshot!.Effective!.ExposureTimeUs);
        Assert.False(candidate.Disposed);
    }

    [Fact]
    public async Task V132_R02_ReadBackFailureCanRestoreTheDurableBaseline()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var baselineDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "baseline"), Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var restored = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "restored"),
            Capabilities());
        candidate.ApplyHandler = requested =>
        {
            var canonical = candidate.Capabilities.ValidateConfiguration(requested).Effective!;
            var forged = new EffectiveCameraConfiguration(canonical.ProductionAcquisitionMode,
                canonical.ExposureTimeUs + 10, canonical.GainDb, canonical.RegionOfInterest,
                canonical.PixelFormat, canonical.ValidBits, canonical.AcquisitionTimeoutMs,
                canonical.TriggerDelayUs, canonical.WhiteBalanceRgb);
            candidate.MarkApplied();
            return CameraConfigurationResult.Success(forged);
        };
        var provider = new FakeProvider(identity, binding, baselineDevice, candidate, restored);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baselineResult = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(10), "baseline-config"));
        Assert.True(baselineResult.Succeeded, baselineResult.ReasonCode);
        var durableBaseline = baselineResult.Snapshot!;

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);
        var staged = await lease.ApplyAsync(Request(20));
        Assert.False(staged.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", staged.ReasonCode);
        Assert.True(candidate.Disposed);
        Assert.True(lease.HardwareTouched);

        var restore = await lease.RestoreAsync(durableBaseline);
        Assert.True(restore.Succeeded, restore.ReasonCode);
        Assert.Equal("CameraActivationRestored", restore.ReasonCode);
        Assert.Equal(10, restore.Snapshot!.Effective!.ExposureTimeUs);

        await lease.DisposeAsync();
        var current = await harness.Runtime.GetSetupAsync("Primary", harness.Invocation());
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(10, current.Snapshot!.Effective!.ExposureTimeUs);
        Assert.False(restored.Disposed);
    }

    [Fact]
    public async Task V132_R03_MissingBaselineWithCandidateReturnsSafeClose()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var staged = await lease.ApplyAsync(Request(20));
        Assert.True(staged.Succeeded, staged.ReasonCode);

        var restore = await lease.RestoreAsync(null);
        Assert.True(restore.Succeeded, restore.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", restore.ReasonCode);
        Assert.True(candidate.Disposed);

        var current = await harness.Runtime.GetSetupAsync("Primary", harness.Invocation());
        Assert.Equal(CameraProviderAvailability.Available,
            current.Snapshot!.Health.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Closed, current.Snapshot.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unconfigured, current.Snapshot.Health.Configuration);

        await lease.DisposeAsync();
        var retry = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(retry.Available, retry.ReasonCode);
        await retry.DisposeAsync();
    }

    [Fact]
    public async Task V132_R04_NoPreviousBaselineClosesEveryHandleAndReturnsSafeClose()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");

        var closed = await lease.RestoreAsync(null);

        Assert.True(closed.Succeeded, closed.ReasonCode);
        Assert.Equal("NoPreviousBaselineClosed", closed.ReasonCode);
        Assert.NotNull(closed.Snapshot);
        Assert.Equal(CameraProviderAvailability.Available,
            closed.Snapshot!.Health.ProviderAvailability);
        Assert.Equal(CameraConnectionState.Closed, closed.Snapshot.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unconfigured, closed.Snapshot.Health.Configuration);
        Assert.Null(closed.Snapshot.Health.LastFault);
        Assert.False(lease.Committed);
        Assert.True(binding.Disposed);
        Assert.False(candidate.Disposed);

        await lease.DisposeAsync();
        var retry = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(retry.Available, retry.ReasonCode);
        await retry.DisposeAsync();
    }

    [Fact]
    public async Task V132_R05_NoPreviousBaselineWithLateStopFailsUnknownAndDoesNotReopen()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var stop = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.StopHandler = _ => stop.Task;
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var staged = await lease.ApplyAsync(Request(20));
        Assert.True(staged.Succeeded, staged.ReasonCode);

        var closed = await lease.RestoreAsync(null);

        Assert.False(closed.Succeeded);
        Assert.Equal("CameraDeviceCleanupRequired", closed.ReasonCode);
        Assert.Equal(CameraConfigurationState.Unknown,
            (await harness.Runtime.GetSetupAsync("Primary", harness.Invocation())).Snapshot!
                .Health.Configuration);
        Assert.Equal(2, provider.OpenCount);
        Assert.False(candidate.Disposed);

        stop.TrySetResult(CameraOperationResult.Success());
        await candidate.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task V132_R06_CancelledLateApplyBlocksRestoreUntilPhysicalReturn()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var baseline = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "baseline"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var restored = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "restored"),
            Capabilities());
        var lateApply = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.ApplyTaskHandler = (_, _) =>
        {
            candidate.ApplyStarted.TrySetResult(true);
            return lateApply.Task;
        };
        var provider = new FakeProvider(identity, binding, baseline, candidate, restored);
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromSeconds(2));

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baselineResult = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(10), "baseline-config"));
        Assert.True(baselineResult.Succeeded, baselineResult.ReasonCode);

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        using var cancellation = new CancellationTokenSource();
        var applyTask = lease.ApplyAsync(Request(20), cancellationToken: cancellation.Token).AsTask();
        await candidate.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var staged = await applyTask;
        Assert.False(staged.Succeeded);
        Assert.Equal("CameraOperationCancelled", staged.ReasonCode);
        Assert.False(candidate.Disposed);

        var restore = await lease.RestoreAsync(baselineResult.Snapshot);

        Assert.False(restore.Succeeded);
        Assert.Equal("CameraDeviceCleanupRequired", restore.ReasonCode);
        Assert.Equal(3, provider.OpenCount);
        Assert.False(restored.Disposed);

        var expected = candidate.Capabilities.ValidateConfiguration(Request(20)).Effective!;
        lateApply.TrySetResult(CameraConfigurationResult.Success(expected));
        await candidate.DisposedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task V132_R07_PartialApplyFailureClosesCandidateAndRestoresBaseline()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var baseline = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "baseline"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var restored = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "restored"),
            Capabilities());
        candidate.ApplyHandler = _ =>
        {
            candidate.MarkApplied();
            return CameraConfigurationResult.Failure("CameraConfigurationPartialWrite");
        };
        var provider = new FakeProvider(identity, binding, baseline, candidate, restored);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baselineResult = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(10), "baseline-config"));
        Assert.True(baselineResult.Succeeded, baselineResult.ReasonCode);

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var staged = await lease.ApplyAsync(Request(20));

        Assert.False(staged.Succeeded);
        Assert.Equal("CameraConfigurationPartialWrite", staged.ReasonCode);
        Assert.True(candidate.Disposed);

        var restore = await lease.RestoreAsync(baselineResult.Snapshot);
        Assert.True(restore.Succeeded, restore.ReasonCode);
        Assert.Equal("CameraActivationRestored", restore.ReasonCode);
        Assert.Equal(10, restore.Snapshot!.Effective!.ExposureTimeUs);
        Assert.False(restored.Disposed);
    }

    [Fact]
    public async Task V132_R08_RestoreReadBackFailureClosesReplacementAndPublishesUnknown()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var baseline = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "baseline"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var restored = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "restored"),
            Capabilities());
        restored.ApplyHandler = requested =>
        {
            var canonical = restored.Capabilities.ValidateConfiguration(requested).Effective!;
            restored.MarkApplied();
            var forged = new EffectiveCameraConfiguration(canonical.ProductionAcquisitionMode,
                canonical.ExposureTimeUs + 10, canonical.GainDb, canonical.RegionOfInterest,
                canonical.PixelFormat, canonical.ValidBits, canonical.AcquisitionTimeoutMs,
                canonical.TriggerDelayUs, canonical.WhiteBalanceRgb);
            return CameraConfigurationResult.Success(forged);
        };
        var provider = new FakeProvider(identity, binding, baseline, candidate, restored);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "activation-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baselineResult = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(10), "baseline-config"));
        Assert.True(baselineResult.Succeeded, baselineResult.ReasonCode);

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        var staged = await lease.ApplyAsync(Request(20));
        Assert.True(staged.Succeeded, staged.ReasonCode);

        var restore = await lease.RestoreAsync(baselineResult.Snapshot);

        Assert.False(restore.Succeeded);
        Assert.Equal("CameraActivationBaselineReadBackMismatch", restore.ReasonCode);
        Assert.True(restored.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown,
            (await harness.Runtime.GetSetupAsync("Primary", harness.Invocation())).Snapshot!
                .Health.Configuration);
    }

    [Fact]
    public async Task V132_R09_ConfigurationMutationMarkerStaysSetUntilProviderCompletes()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var release = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.ApplyTaskHandler = (_, _) =>
        {
            candidate.ApplyStarted.TrySetResult(true);
            return release.Task;
        };
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "marker-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        Assert.False(harness.Runtime.ConfigurationMutationInProgress);

        var applyTask = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(20), "marker-apply")).AsTask();
        await candidate.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(harness.Runtime.ConfigurationMutationInProgress);

        candidate.MarkApplied();
        release.TrySetResult(CameraConfigurationResult.Success(
            candidate.Capabilities.ValidateConfiguration(Request(20)).Effective!));
        var applied = await applyTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(applied.Succeeded, applied.ReasonCode);
        Assert.False(harness.Runtime.ConfigurationMutationInProgress);
    }

    [Fact]
    public async Task V132_R10_ConfigurationMutationMarkerClearsAfterProviderRejection()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var release = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        candidate.ApplyTaskHandler = (_, _) =>
        {
            candidate.ApplyStarted.TrySetResult(true);
            return release.Task;
        };
        var provider = new FakeProvider(identity, binding, candidate);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "marker-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);

        var applyTask = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(20), "marker-reject")).AsTask();
        await candidate.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(harness.Runtime.ConfigurationMutationInProgress);

        release.TrySetResult(CameraConfigurationResult.Failure("CameraConfigurationRejected"));
        var rejected = await applyTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(rejected.Succeeded);
        Assert.Equal("CameraConfigurationRejected", rejected.ReasonCode);
        Assert.False(harness.Runtime.ConfigurationMutationInProgress);
    }

    [Fact]
    public async Task V132_R11_RestoreContinuesAfterCallerAndRuntimeCancellation()
    {
        var identity = ProviderIdentity();
        var binding = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "binding"),
            Capabilities());
        var baseline = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "baseline"),
            Capabilities());
        var candidate = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "candidate"),
            Capabilities());
        var restored = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "restored"),
            Capabilities());
        var restoreApply = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerToken = default(CancellationToken);
        restored.ApplyTaskHandler = (requested, token) =>
        {
            providerToken = token;
            restored.MarkApplied();
            restored.ApplyStarted.TrySetResult(true);
            return restoreApply.Task;
        };
        var provider = new FakeProvider(identity, binding, baseline, candidate, restored);
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(new CameraRebindRequest(
            Guid.NewGuid(), harness.Invocation(), "Primary", 0, null,
            new CameraBindingTarget(identity, "Camera:One"), "restore-cancel-bind"));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var baselineResult = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.Invocation(), "Primary",
                rebound.Snapshot!.Binding!.Revision, rebound.Snapshot.Binding.RevisionHash,
                Request(10), "restore-cancel-baseline"));
        Assert.True(baselineResult.Succeeded, baselineResult.ReasonCode);

        await using var lease = await harness.Runtime.ReserveRecipeActivationAsync("Primary");
        Assert.True(lease.Available, lease.ReasonCode);
        var staged = await lease.ApplyAsync(Request(20));
        Assert.True(staged.Succeeded, staged.ReasonCode);

        using var callerCancellation = new CancellationTokenSource();
        var restoreTask = lease.RestoreAsync(baselineResult.Snapshot!, callerCancellation.Token).AsTask();
        await restored.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(providerToken.IsCancellationRequested);

        callerCancellation.Cancel();
        harness.Runtime.CancelOperations();
        Assert.False(providerToken.IsCancellationRequested);
        restoreApply.TrySetResult(restored.Capabilities.ValidateConfiguration(Request(10)).Effective is { } effective
            ? CameraConfigurationResult.Success(effective)
            : CameraConfigurationResult.Failure("RestoreConfigurationInvalid"));

        var restoredResult = await restoreTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(restoredResult.Succeeded, restoredResult.ReasonCode);
        Assert.Equal("CameraActivationRestored", restoredResult.ReasonCode);
        Assert.Equal(10, restoredResult.Snapshot!.Effective!.ExposureTimeUs);
        Assert.Equal(CameraConfigurationState.Applied, restoredResult.Snapshot.Health.Configuration);
        Assert.False(providerToken.IsCancellationRequested);
    }

    private static RuntimeHarness Create(FakeProvider provider,
        TimeSpan? operationTimeout = null, TimeSpan? shutdownTimeout = null)
    {
        var principal = Guid.NewGuid();
        var session = Guid.NewGuid();
        var authorizer = new FakeAuthorizer(principal, session);
        var sessions = new FakeSessions(new InteractiveSession(
            InteractiveSessionState.Authenticated, principal.ToString("D"), session));
        var station = new CameraSetupRuntime.CameraStationContext(Guid.NewGuid(), false,
            ProductionArmState.Disarmed, false, null, 0, HandshakePhase.Idle,
            ExclusiveMode.None, RecoveryState.None, false, null, false, false);
        var audit = new FakeAuditWriter();
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        var runtime = new CameraSetupRuntime(new[] { provider }, new CameraSetupOptions
        {
            OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2),
            ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2)
        }, audit, sessions, new FakeIdentityQuery(), () => station,
            (_, snapshot) => published.Enqueue(snapshot), authorizer);
        return new RuntimeHarness(runtime, principal, session);
    }

    private static RequestedCameraConfiguration Request(double exposure) => new(
        ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
        new RegionOfInterest(0, 0, 640, 480), VisionPixelFormat.Mono8, null, 100, 0, null);

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
        new("T32.Test.Camera.Provider", "1", "T32.Test.Camera.Package", "1");

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        internal RuntimeHarness(CameraSetupRuntime runtime, Guid principalId, Guid sessionId)
        { Runtime = runtime; PrincipalId = principalId; SessionId = sessionId; }
        internal CameraSetupRuntime Runtime { get; }
        private Guid PrincipalId { get; }
        private Guid SessionId { get; }
        internal CommandInvocation Invocation() =>
            new(CommandSource.PhysicalConsole, PrincipalId.ToString("D"), SessionId);
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class FakeProvider : ICameraProvider
    {
        private readonly Queue<ICameraDevice> _devices;
        private int _openCount;
        internal FakeProvider(CameraProviderIdentity identity, params ICameraDevice[] devices)
        { Identity = identity; _devices = new Queue<ICameraDevice>(devices); }
        public CameraProviderIdentity Identity { get; }
        internal int OpenCount => Volatile.Read(ref _openCount);
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));
        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCount);
            if (_devices.Count == 0)
                return ValueTask.FromResult(CameraOpenResult.Failure("NoTestDevice"));
            return ValueTask.FromResult(CameraOpenResult.Success(_devices.Dequeue()));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDevice : ICameraDevice
    {
        private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
        internal FakeDevice(CameraDeviceDescriptor descriptor, CameraCapabilities capabilities)
        { Descriptor = descriptor; Capabilities = capabilities; }
        internal Func<RequestedCameraConfiguration, CameraConfigurationResult>? ApplyHandler { get; set; }
        internal Func<RequestedCameraConfiguration, CancellationToken, Task<CameraConfigurationResult>>?
            ApplyTaskHandler { get; set; }
        internal Func<CancellationToken, Task<CameraOperationResult>>? StopHandler { get; set; }
        internal TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DisposedCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        internal CameraDeviceDescriptor Descriptor { get; }
        internal CameraCapabilities Capabilities { get; }
        public CameraDeviceDescriptor DescriptorPublic => Descriptor;
        CameraDeviceDescriptor ICameraDevice.Descriptor => Descriptor;
        CameraCapabilities ICameraDevice.Capabilities => Capabilities;

        internal void MarkApplied() => _configuration = CameraConfigurationState.Applied;

        public CameraHealthSnapshot GetHealthSnapshot() => new(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            _configuration, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())));

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            if (ApplyTaskHandler is { } taskHandler)
                return new ValueTask<CameraConfigurationResult>(
                    taskHandler(requested, cancellationToken));
            if (ApplyHandler is { } handler)
                return ValueTask.FromResult(handler(requested));
            var result = Capabilities.ValidateConfiguration(requested);
            if (result.Succeeded) MarkApplied();
            return ValueTask.FromResult(result);
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Failure("NotUsed"));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted, "NotUsed")));

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default) => StopHandler is { } handler
                ? new ValueTask<CameraOperationResult>(handler(cancellationToken))
                : ValueTask.FromResult(CameraOperationResult.Success());

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposedCompletion.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAuditWriter : ICommandAuditWriter
    {
        public AuditIntegrityReport? Integrity => null;
        public Task<StoreWriteResult> Initialization =>
            Task.FromResult(new StoreWriteResult(true, "Ready"));
        public TimeSpan CommitTimeout => TimeSpan.FromSeconds(2);
        public ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact,
            StoreDeadline deadline, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StoreWriteResult(true, "Persisted", fact));
    }

    private sealed class FakeAuthorizer : ICameraSetupAuthorizer
    {
        internal FakeAuthorizer(Guid principalId, Guid sessionId)
        { PrincipalId = principalId; SessionId = sessionId; }
        private Guid PrincipalId { get; }
        private Guid SessionId { get; }
        public ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
            CommandInvocation invocation, bool readOnly, Guid operationId, string targetId,
            AuditedCommandKind commandKind, CancellationToken cancellationToken = default)
        {
            CameraSetupAuthorizationReservation? reservation = null;
            if (!readOnly)
                reservation = CameraSetupAuthorizationReservation.Create(static () => { }, static () => { });
            return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                PrincipalId, SessionId, 1, reservation));
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        internal FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; }
        public event EventHandler<InteractiveSessionChangedEventArgs>? Changed
        {
            add { }
            remove { }
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

    private sealed class FakeIdentityQuery : IIdentityAdministrationQuery
    {
        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
