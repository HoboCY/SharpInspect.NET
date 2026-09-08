using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused coordinator tests.  The provider and device doubles below expose the
/// physical call boundaries so a test can release an ignored-cancellation call
/// after the Runtime operation has already returned.
/// </summary>
public sealed class CameraSetupRuntimeTests
{
    [Fact]
    public async Task V117_R01_WrongDeviceIdentityIsRejectedAndTheOpenedDeviceIsDisposed()
    {
        var identity = ProviderIdentity();
        var wrong = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:Other", "wrong"), Capabilities());
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(wrong)));
        await using var harness = Create(provider);

        var result = await harness.Runtime.RebindAsync(BindRequest(harness, identity));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraDeviceOpenFailed", result.ReasonCode);
        Assert.True(wrong.Disposed);
        Assert.Equal(2, harness.Audit.Facts.Count);
        Assert.Equal(CommandAuditPhase.Outcome, harness.Audit.Facts[0].Phase);
        Assert.Equal(CommandAuditPhase.Failed, harness.Audit.Facts[1].Phase);
        Assert.Contains(harness.Published, item => item.Health.Configuration ==
            CameraConfigurationState.Unknown);
    }

    [Fact]
    public async Task V117_R02_ReadBackMismatchClosesDeviceAndPublishesNoEffectiveConfiguration()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var configuredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured"), Capabilities());
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? bindingDevice : configuredDevice)));
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;
        var requested = Request();
        var expected = harness.NewInvocation();
        configuredDevice.ApplyHandler = (value, _) =>
        {
            configuredDevice.MarkApplied();
            var canonical = configuredDevice.Capabilities.ValidateConfiguration(value).Effective!;
            var forged = new EffectiveCameraConfiguration(canonical.ProductionAcquisitionMode,
                canonical.ExposureTimeUs + 10, canonical.GainDb, canonical.RegionOfInterest,
                canonical.PixelFormat, canonical.ValidBits, canonical.AcquisitionTimeoutMs,
                canonical.TriggerDelayUs, canonical.WhiteBalanceRgb);
            return Task.FromResult(CameraConfigurationResult.Success(forged));
        };

        var result = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), expected, "Primary",
                binding.Revision, binding.RevisionHash, requested, "readback-test"));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", result.ReasonCode);
        Assert.Equal(requested, result.Snapshot!.Requested);
        Assert.Null(result.Snapshot!.Effective);
        Assert.Empty(result.Snapshot.Differences);
        Assert.True(configuredDevice.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot.Health.Configuration);
    }

    [Fact]
    public async Task V117_R12_FailedApplyPublishesChangedAdmissionRequestAndClearsEffectiveEvidence()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var firstConfiguredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured-first"), Capabilities());
        var failedConfiguredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured-failed"), Capabilities());
        var devices = new[] { bindingDevice, firstConfiguredDevice, failedConfiguredDevice };
        var opens = 0;
        var provider = new FakeProvider(identity, (_, _) =>
        {
            var index = Math.Min(Interlocked.Increment(ref opens), devices.Length) - 1;
            return Task.FromResult(CameraOpenResult.Success(devices[index]));
        });
        await using var harness = Create(provider);

        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;
        var firstRequest = Request();
        var firstApply = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, firstRequest, "first-apply"));
        Assert.True(firstApply.Succeeded, firstApply.ReasonCode);

        var changedRequest = new RequestedCameraConfiguration(
            firstRequest.ProductionAcquisitionMode, firstRequest.ExposureTimeUs + 10,
            firstRequest.GainDb, firstRequest.RegionOfInterest, firstRequest.PixelFormat,
            firstRequest.ValidBits, firstRequest.AcquisitionTimeoutMs,
            firstRequest.TriggerDelayUs, firstRequest.WhiteBalanceRgb);
        failedConfiguredDevice.ApplyHandler = (value, _) =>
        {
            failedConfiguredDevice.MarkApplied();
            var canonical = failedConfiguredDevice.Capabilities.ValidateConfiguration(value).Effective!;
            var forged = new EffectiveCameraConfiguration(canonical.ProductionAcquisitionMode,
                canonical.ExposureTimeUs + 10, canonical.GainDb, canonical.RegionOfInterest,
                canonical.PixelFormat, canonical.ValidBits, canonical.AcquisitionTimeoutMs,
                canonical.TriggerDelayUs, canonical.WhiteBalanceRgb);
            return Task.FromResult(CameraConfigurationResult.Success(forged));
        };

        var failed = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, changedRequest, "changed-failed-apply"));

        Assert.False(failed.Succeeded);
        Assert.Equal("CameraReadBackEffectiveMismatch", failed.ReasonCode);
        Assert.Equal(changedRequest, failed.Snapshot!.Requested);
        Assert.Null(failed.Snapshot.Effective);
        Assert.Empty(failed.Snapshot.Differences);
        Assert.Equal(CameraConfigurationState.Unknown, failed.Snapshot.Health.Configuration);
        Assert.Equal(CameraConnectionState.Closed, failed.Snapshot.Health.Connection);
    }

    [Fact]
    public async Task V117_R03_IgnoredOpenCancellationReturnsWithinBoundAndLateDeviceIsDisposed()
    {
        var identity = ProviderIdentity();
        var open = new TaskCompletionSource<CameraOpenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(identity, (_, _) => open.Task);
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();

        var result = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"operation took {stopwatch.Elapsed}");

        var late = new FakeDevice(new CameraDeviceDescriptor(identity, "Camera:One", "late"),
            Capabilities());
        open.TrySetResult(CameraOpenResult.Success(late));
        await Eventually(() => late.Disposed);
        Assert.Equal(2, harness.Audit.Facts.Count);
    }

    [Fact]
    public async Task V117_R04_IgnoredApplyCancellationReturnsWithinBoundAndRetainsPhysicalCleanup()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var configuredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured"), Capabilities());
        var apply = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++opens == 1 ? bindingDevice : configuredDevice)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(250));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;
        configuredDevice.ApplyHandler = (_, _) => apply.Task;
        var stopwatch = Stopwatch.StartNew();

        var operation = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "ignored-cancel-test"));
        stopwatch.Stop();

        Assert.False(operation.Succeeded);
        Assert.Equal("CameraOperationTimeout", operation.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"operation took {stopwatch.Elapsed}");
        Assert.False(configuredDevice.Disposed);

        apply.TrySetResult(CameraConfigurationResult.Failure("CameraConfigurationApplyFailed"));
        await Eventually(() => configuredDevice.Disposed);
        Assert.True(configuredDevice.StopCalled);
    }

    [Fact]
    public async Task V117_R05_CallerCancellationTransfersLateDeviceOwnershipUntilPhysicalReturn()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var configuredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured"), Capabilities());
        var apply = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++opens == 1 ? bindingDevice : configuredDevice)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromSeconds(2));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;
        configuredDevice.ApplyHandler = (_, _) => apply.Task;
        using var cancellation = new CancellationTokenSource();
        var task = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "caller-cancel-test"),
            cancellation.Token).AsTask();
        await Eventually(() => configuredDevice.ApplyStarted);
        cancellation.Cancel();

        var result = await task;

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationCancelled", result.ReasonCode);
        Assert.False(configuredDevice.Disposed);
        apply.TrySetResult(CameraConfigurationResult.Failure("CameraConfigurationApplyFailed"));
        await Eventually(() => configuredDevice.Disposed);
    }

    [Fact]
    public async Task V117_R06_DisposeWaitsForTrackedPhysicalCleanupWithoutReleasingNewState()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var configuredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured"), Capabilities());
        var apply = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++opens == 1 ? bindingDevice : configuredDevice)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(150));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;
        configuredDevice.ApplyHandler = (_, _) => apply.Task;
        var result = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "dispose-test"));
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.False(configuredDevice.Disposed);

        var dispose = harness.Runtime.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(configuredDevice.Disposed);

        apply.TrySetResult(CameraConfigurationResult.Failure("CameraConfigurationApplyFailed"));
        await Eventually(() => configuredDevice.Disposed);
    }

    [Fact]
    public async Task V117_R07_MutationWithoutGovernedAuthorizerFailsBeforeProviderOrAudit()
    {
        var identity = ProviderIdentity();
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("ShouldNotOpen")));
        await using var harness = Create(provider, authorizer: null, useAuthorizer: false);

        var result = await harness.Runtime.RebindAsync(BindRequest(harness, identity));

        Assert.False(result.Succeeded);
        Assert.Equal("AuthorizationUnavailable", result.ReasonCode);
        Assert.Equal(0, provider.OpenCount);
        Assert.Empty(harness.Audit.Facts);
    }

    [Fact]
    public async Task V117_R08_GovernedSessionMismatchReleasesReservationAndDoesNotOpenDevice()
    {
        var identity = ProviderIdentity();
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("ShouldNotOpen")));
        var mismatch = new FakeAuthorizer(Guid.NewGuid(), Guid.NewGuid(), revision: 4);
        await using var harness = Create(provider, mismatch);

        var result = await harness.Runtime.RebindAsync(BindRequest(harness, identity));

        Assert.False(result.Succeeded);
        Assert.Equal("SessionChanged", result.ReasonCode);
        Assert.True(mismatch.ReservationRolledBack);
        Assert.Equal(0, provider.OpenCount);
        Assert.Empty(harness.Audit.Facts);
    }

    [Fact]
    public async Task V117_R09_IgnoredStopIsBoundedAndOldPhysicalDeviceRemainsOwnedUntilRelease()
    {
        var identity = ProviderIdentity();
        var first = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "first"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement"), Capabilities());
        var stop = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.StopHandler = _ => stop.Task;
        var opens = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++opens == 1 ? first : replacement)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var binding = initial.Snapshot!.Binding!;
        var stopwatch = Stopwatch.StartNew();

        var result = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(),
            harness.NewInvocation(), "Primary", binding.Revision, binding.RevisionHash,
            new CameraBindingTarget(identity, "Camera:One"), "stop-bound-test"));
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"operation took {stopwatch.Elapsed}");
        Assert.True(first.StopCalled);
        Assert.False(first.Disposed);
        Assert.False(replacement.Disposed);
        Assert.Equal(1, provider.OpenCount);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot!.Health.Configuration);

        stop.TrySetResult(CameraOperationResult.Success());
        await Eventually(() => first.Disposed);
    }

    [Fact]
    public async Task V117_R10_PersistedPendingOperationBlocksRebindAndApplyBeforeProviderUse()
    {
        var identity = ProviderIdentity();
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("ShouldNotOpen")));
        var target = new CameraBindingTarget(identity, "Camera:One");
        var binding = new CameraBindingRevision(11, "Primary", 1, Guid.NewGuid(), null,
            new string('A', 64), target, Guid.NewGuid(), Guid.NewGuid(), 4,
            "persisted-pending", DateTimeOffset.UtcNow);
        var pending = new CameraSetupSnapshot("Primary", binding,
            new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                CameraAcquisitionState.Stopped,
                new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
        var persistence = new FakePendingPersistence(
            new CameraSetupPersistentState(pending, true, "CameraSetupOperationPending"));
        await using var harness = Create(provider, persistence: persistence);

        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        var applied = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "pending-apply"));

        Assert.False(rebound.Succeeded);
        Assert.Equal("CameraSetupOperationPending", rebound.ReasonCode);
        Assert.False(applied.Succeeded);
        Assert.Equal("CameraSetupOperationPending", applied.ReasonCode);
        Assert.Equal(0, provider.OpenCount);
        Assert.Equal(0, persistence.AppendCalls);
        var projection = Assert.Single(harness.Published);
        Assert.Equal(binding.RevisionHash, projection.Binding!.RevisionHash);
        Assert.Equal(CameraConnectionState.Closed, projection.Health.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, projection.Health.Configuration);
    }

    [Fact]
    public void V117_R11_FailedApplyTerminalCasUsesPriorBindingWithoutWeakeningAdmission()
    {
        var provider = ProviderIdentity();
        var target = new CameraBindingTarget(provider, "Camera:One");
        var binding = new CameraBindingRevision(11, "Primary", 1, Guid.NewGuid(), null,
            new string('A', 64), target, Guid.NewGuid(), Guid.NewGuid(), 4,
            "persisted-binding", DateTimeOffset.UtcNow);
        var state = new CameraSetupStoreSnapshot("Primary", binding, null, null, null,
            Array.Empty<CameraConfigurationDifference>(), null, null,
            Array.Empty<CameraSetupEvent>());

        var failedTerminal = ApplyPersistenceEvent(CameraSetupPersistencePhase.Terminal,
            succeeded: false, previousBinding: binding, binding: null, target);
        Assert.Null(InvokeValidateBindingCas(failedTerminal, state));

        var differentTarget = new CameraBindingTarget(provider, "Camera:Other");
        var mismatchedTarget = ApplyPersistenceEvent(CameraSetupPersistencePhase.Terminal,
            succeeded: false, previousBinding: binding, binding: null, differentTarget);
        Assert.Equal("CameraBindingRevisionConflict", InvokeValidateBindingCas(mismatchedTarget, state));

        var successfulWithoutBinding = ApplyPersistenceEvent(CameraSetupPersistencePhase.Terminal,
            succeeded: true, previousBinding: binding, binding: null, target);
        Assert.Equal("CameraBindingRevisionConflict",
            InvokeValidateBindingCas(successfulWithoutBinding, state));

        var unboundState = new CameraSetupStoreSnapshot("Primary", null, null, null, null,
            Array.Empty<CameraConfigurationDifference>(), null, null,
            Array.Empty<CameraSetupEvent>());
        var unboundAdmission = ApplyPersistenceEvent(CameraSetupPersistencePhase.Admission,
            succeeded: true, previousBinding: null, binding: null, target);
        Assert.Equal("CameraBindingRevisionConflict",
            InvokeValidateBindingCas(unboundAdmission, unboundState));
    }

    [Fact]
    public async Task V117_R13_GateTimeoutAndCancellationReleaseReservationsBeforeProviderUse()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var configuredDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "configured"), Capabilities());
        var apply = new TaskCompletionSource<CameraConfigurationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        configuredDevice.ApplyHandler = (_, _) => apply.Task;
        var opens = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++opens == 1 ? bindingDevice : configuredDevice)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;

        var terminal = new TaskCompletionSource<StoreWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Audit.FailedAppendStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Audit.AppendHandler = fact => fact.Phase == CommandAuditPhase.Failed
            ? terminal.Task
            : Task.FromResult(new StoreWriteResult(true, "Persisted", fact));

        var first = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "gate-holder")).AsTask();
        await harness.Audit.FailedAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var cancelled = harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "gate-cancel"),
            cancellation.Token).AsTask();
        await Eventually(() => harness.Authorizer.ReservationCount >= 2);
        cancellation.Cancel();
        var cancelledResult = await cancelled;
        Assert.False(cancelledResult.Succeeded);
        Assert.Equal("CameraOperationCancelled", cancelledResult.ReasonCode);

        var timedOut = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "gate-timeout"));
        Assert.False(timedOut.Succeeded);
        Assert.Equal("CameraSetupBusy", timedOut.ReasonCode);
        Assert.Equal(2, opens);
        // The initial rebind also reserves a grant; the two waiting commands
        // account for the remaining admissions.  Both waiting reservations
        // must be rolled back when cancellation/timeout wins the gate.
        Assert.Equal(4, harness.Authorizer.ReservationCount);
        Assert.Equal(2, harness.Authorizer.ReservationRollbackCount);

        terminal.TrySetResult(new StoreWriteResult(true, "Persisted"));
        apply.TrySetResult(CameraConfigurationResult.Failure("CameraConfigurationApplyFailed"));
        var firstResult = await first;
        Assert.False(firstResult.Succeeded);
        Assert.Equal("CameraOperationTimeout", firstResult.ReasonCode);
        await Eventually(() => configuredDevice.Disposed);
    }

    [Fact]
    public async Task V117_R14_DiscoveryLateCompletionDoesNotReleaseDisposedQueryGate()
    {
        var identity = ProviderIdentity();
        var discovery = new TaskCompletionSource<CameraDiscoveryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("NotUsed")))
        {
            DiscoverHandler = _ => discovery.Task,
            DiscoverStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var query = harness.Runtime.DiscoverAsync(identity, harness.NewInvocation()).AsTask();
        await provider.DiscoverStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var dispose = harness.Runtime.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"dispose took {stopwatch.Elapsed}");

        discovery.TrySetResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));
        var result = await query.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(result.Succeeded);
        Assert.Equal("CameraDiscoveryCancelled", result.ReasonCode);
    }

    [Fact]
    public async Task V117_R15_BlockedHealthProbeIsSingleAndCannotBlockShutdown()
    {
        var identity = ProviderIdentity();
        var device = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "health"), Capabilities());
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(device)));
        await using var harness = Create(provider, shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.HealthHandler = () =>
        {
            entered.TrySetResult(true);
            return release.Task.GetAwaiter().GetResult();
        };
        harness.Runtime.Heartbeat();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Runtime.Heartbeat();
        Assert.Equal(2, device.HealthCallCount); // one initial rebind read plus one probe

        var stopwatch = Stopwatch.StartNew();
        var dispose = harness.Runtime.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"dispose took {stopwatch.Elapsed}");

        release.TrySetResult(new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
    }

    [Fact]
    public async Task V117_R16_DiscoveryPhysicalCapacityRejectsNewProviderCalls()
    {
        var identity = ProviderIdentity();
        var pending = new List<TaskCompletionSource<CameraDiscoveryResult>>();
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Failure("NotUsed")))
        {
            DiscoverHandler = _ =>
            {
                var next = new TaskCompletionSource<CameraDiscoveryResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(next);
                return next.Task;
            }
        };
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        for (var index = 0; index < 16; index++)
        {
            var result = await harness.Runtime.DiscoverAsync(identity, harness.NewInvocation());
            Assert.Equal("CameraDiscoveryTimeout", result.ReasonCode);
        }

        var rejected = await harness.Runtime.DiscoverAsync(identity, harness.NewInvocation());
        Assert.False(rejected.Succeeded);
        Assert.Equal("CameraPhysicalCapacityExceeded", rejected.ReasonCode);
        Assert.Equal(16, provider.DiscoverCount);

        foreach (var call in pending)
            call.TrySetResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));
    }

    [Fact]
    public async Task V117_R17_OpenPhysicalCapacityRejectsNewProviderCalls()
    {
        var identity = ProviderIdentity();
        var pending = new List<TaskCompletionSource<CameraOpenResult>>();
        var provider = new FakeProvider(identity, (_, _) =>
        {
            var next = new TaskCompletionSource<CameraOpenResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(next);
            return next.Task;
        });
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        for (var index = 0; index < 16; index++)
        {
            var result = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
            Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        }

        var rejected = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.False(rejected.Succeeded);
        Assert.Equal("CameraPhysicalCapacityExceeded", rejected.ReasonCode);
        Assert.Equal(16, provider.OpenCount);

        foreach (var call in pending)
        {
            var device = new FakeDevice(
                new CameraDeviceDescriptor(identity, "Camera:One", "late"), Capabilities());
            call.TrySetResult(CameraOpenResult.Success(device));
        }
    }

    [Fact]
    public async Task V117_R18_ApplyPhysicalCapacityRejectsConfigurationProviderCall()
    {
        var identity = ProviderIdentity();
        var bindingDevice = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "binding"), Capabilities());
        var pending = new List<TaskCompletionSource<CameraConfigurationResult>>();
        var opened = new List<FakeDevice>();
        var provider = new FakeProvider(identity, (_, _) =>
        {
            if (opened.Count == 0)
            {
                opened.Add(bindingDevice);
                return Task.FromResult(CameraOpenResult.Success(bindingDevice));
            }

            var device = new FakeDevice(
                new CameraDeviceDescriptor(identity, "Camera:One", "configured"), Capabilities());
            var next = new TaskCompletionSource<CameraConfigurationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            device.ApplyHandler = (_, _) =>
            {
                pending.Add(next);
                return next.Task;
            };
            opened.Add(device);
            return Task.FromResult(CameraOpenResult.Success(device));
        });
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;

        for (var index = 0; index < 16; index++)
        {
            var result = await harness.Runtime.ApplyDebugConfigurationAsync(
                new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                    binding.Revision, binding.RevisionHash, Request(), $"capacity-{index}"));
            Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        }

        var rejected = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "capacity-rejected"));
        Assert.False(rejected.Succeeded);
        Assert.Equal("CameraPhysicalCapacityExceeded", rejected.ReasonCode);
        Assert.Equal(16, pending.Count);
        // Initial bind plus one newly opened device for each of the sixteen
        // admitted, still-running configuration calls.  The seventeenth
        // configuration request is rejected before opening a device.
        Assert.Equal(17, provider.OpenCount);
        Assert.Equal(16, opened.Skip(1).Sum(device => device.ApplyCallCount));

        foreach (var call in pending)
            call.TrySetResult(CameraConfigurationResult.Failure("CameraConfigurationApplyFailed"));
        await Eventually(() => opened.Skip(1).All(device => device.Disposed));
    }

    [Fact]
    public async Task V117_R19_CommittedRebindReadbackFailureIsUnavailableUntilRefresh()
    {
        var identity = ProviderIdentity();
        var device = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "readback"), Capabilities());
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(device)));
        var persistence = new FakeReadbackPersistence();
        await using var harness = Create(provider, persistence: persistence);

        var result = await harness.Runtime.RebindAsync(BindRequest(harness, identity));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraSetupReadbackUnavailable", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Null(result.Snapshot!.Binding);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot.Health.Configuration);
        Assert.True(device.Disposed);
        Assert.Equal(0, result.Snapshot.Binding?.Position ?? 0);

        persistence.FailReadback = false;
        var refreshed = await harness.Runtime.GetSetupAsync("Primary", harness.NewInvocation());
        Assert.True(refreshed.Available, refreshed.ReasonCode);
        Assert.Equal(42, refreshed.Snapshot!.Binding!.Position);
        Assert.NotEqual(refreshed.Snapshot.Binding.Revision, refreshed.Snapshot.Binding.Position);
    }

    [Fact]
    public async Task V117_R20_FullPhysicalCapacityTransfersExistingDeviceCloseOwnership()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "existing"), Capabilities());
        var pending = new ConcurrentQueue<TaskCompletionSource<CameraDiscoveryResult>>();
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(existing)))
        {
            DiscoverHandler = _ =>
            {
                var call = new TaskCompletionSource<CameraDiscoveryResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Enqueue(call);
                return call.Task;
            }
        };
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        var rebound = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(rebound.Succeeded, rebound.ReasonCode);
        var binding = rebound.Snapshot!.Binding!;

        var discoveryResults = new List<CameraDiscoveryResult>();
        for (var index = 0; index < 16; index++)
            discoveryResults.Add(await harness.Runtime.DiscoverAsync(identity, harness.NewInvocation()));
        Assert.Equal(16, pending.Count);
        Assert.All(discoveryResults, result => Assert.Equal("CameraDiscoveryTimeout", result.ReasonCode));

        var result = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "capacity-existing-device"));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(existing.StopCalled);
        Assert.False(existing.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot!.Health.Configuration);

        foreach (var call in pending)
            call.TrySetResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));
        await Eventually(() => existing.StopCalled && existing.Disposed);
    }

    [Fact]
    public async Task V117_R21_CancelledOpenDisposesCompletedProviderTask()
    {
        var identity = ProviderIdentity();
        var device = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "cancel-race"), Capabilities());
        var open = new TaskCompletionSource<CameraOpenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(identity, (_, cancellationToken) =>
        {
            // This registration runs after the WaitAsync cancellation
            // registration and completes the provider task before the runtime
            // observes the cancellation catch path.
            cancellationToken.Register(() =>
                open.TrySetResult(CameraOpenResult.Success(device)));
            return open.Task;
        })
        {
            OpenStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var operation = harness.Runtime.RebindAsync(BindRequest(harness, identity), cancellation.Token).AsTask();
        await provider.OpenStarted!.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationCancelled", result.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        await Eventually(() => device.Disposed);
    }

    [Fact]
    public async Task V117_R22_BlockedRebindHealthIsBoundedAndLateDeviceIsDisposed()
    {
        var identity = ProviderIdentity();
        var initial = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "health-initial"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "health-replacement"), Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        replacement.HealthHandler = () => health.Task.GetAwaiter().GetResult();
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? initial : replacement)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);
        var binding = first.Snapshot!.Binding!;

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(),
            harness.NewInvocation(), "Primary", binding.Revision, binding.RevisionHash,
            new CameraBindingTarget(identity, "Camera:One"), "blocked-health"));
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"operation took {stopwatch.Elapsed}");
        Assert.Equal(2, provider.OpenCount);
        Assert.False(replacement.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot!.Health.Configuration);

        health.TrySetResult(new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
        await Eventually(() => replacement.Disposed);
    }

    [Fact]
    public async Task V117_R23_BlockedApplyHealthIsBoundedAndLateDeviceIsDisposed()
    {
        var identity = ProviderIdentity();
        var initial = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "apply-health-initial"), Capabilities());
        var configured = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "apply-health-configured"), Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        configured.HealthHandler = () => health.Task.GetAwaiter().GetResult();
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? initial : configured)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));
        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);
        var binding = first.Snapshot!.Binding!;

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "blocked-apply-health"));
        stopwatch.Stop();

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"operation took {stopwatch.Elapsed}");
        Assert.Equal(2, provider.OpenCount);
        Assert.False(configured.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot!.Health.Configuration);

        health.TrySetResult(new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Applied, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
        await Eventually(() => configured.Disposed);
    }

    [Fact]
    public async Task V117_R24_ShutdownBoundsBlockedDeviceDisposeAndRetainsCleanupOwnership()
    {
        var identity = ProviderIdentity();
        var device = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "shutdown-dispose"), Capabilities());
        var dispose = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        device.DisposeHandler = () => dispose.Task;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(device)));
        await using var harness = Create(provider,
            operationTimeout: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);

        var stopwatch = Stopwatch.StartNew();
        await harness.Runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"shutdown took {stopwatch.Elapsed}");
        Assert.False(device.Disposed);
        dispose.TrySetResult(true);
        await Eventually(() => device.Disposed);
    }

    [Fact]
    public async Task V117_R25_RebindDisposeFaultKeepsOldDeviceAndDoesNotOpenReplacement()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "dispose-fault-rebind"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement-must-not-open"), Capabilities());
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? existing : replacement)));
        await using var harness = Create(provider);

        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);
        var binding = first.Snapshot!.Binding!;
        existing.SynchronousDisposeException = new TimeoutException("device dispose failed");

        var result = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(),
            harness.NewInvocation(), "Primary", binding.Revision, binding.RevisionHash,
            new CameraBindingTarget(identity, "Camera:One"), "dispose-fault-rebind"));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraDeviceDisposeFailed", result.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.True(existing.StopCalled);
        Assert.False(existing.Disposed);
        Assert.False(replacement.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot!.Health.Configuration);
        var healthCalls = existing.HealthCallCount;
        harness.Runtime.Heartbeat();
        await Task.Delay(350);
        Assert.Equal(healthCalls, existing.HealthCallCount);
    }

    [Fact]
    public async Task V117_R26_ApplyDisposeFaultKeepsOldDeviceAndDoesNotOpenReplacement()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "dispose-fault-apply"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement-must-not-open"), Capabilities());
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? existing : replacement)));
        await using var harness = Create(provider);

        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);
        var binding = first.Snapshot!.Binding!;
        existing.DisposeHandler = () => Task.FromException(
            new InvalidOperationException("device dispose failed"));

        var result = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "dispose-fault-apply"));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraDeviceDisposeFailed", result.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.True(existing.StopCalled);
        Assert.False(existing.Disposed);
        Assert.False(replacement.Disposed);
        Assert.Equal(CameraConfigurationState.Unknown, result.Snapshot!.Health.Configuration);
        var healthCalls = existing.HealthCallCount;
        harness.Runtime.Heartbeat();
        await Task.Delay(350);
        Assert.Equal(healthCalls, existing.HealthCallCount);
    }

    [Fact]
    public async Task V117_R27_HeartbeatProbeDefersRebindCloseWithoutConcurrentProviderCalls()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "heartbeat-rebind"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement-must-not-open"), Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        existing.HealthStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? existing : replacement)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));

        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);
        var binding = first.Snapshot!.Binding!;
        existing.HealthHandler = () => health.Task.GetAwaiter().GetResult();
        harness.Runtime.Heartbeat();
        await existing.HealthStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(),
            harness.NewInvocation(), "Primary", binding.Revision, binding.RevisionHash,
            new CameraBindingTarget(identity, "Camera:One"), "heartbeat-rebind"));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(existing.StopCalled);
        Assert.False(existing.Disposed);
        Assert.Equal(0, existing.ProviderOverlapCount);

        health.TrySetResult(new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
        await Eventually(() => existing.Disposed);
        Assert.Equal(0, existing.ProviderOverlapCount);
    }

    [Fact]
    public async Task V117_R28_HeartbeatProbeDefersApplyCloseWithoutConcurrentProviderCalls()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "heartbeat-apply"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement-must-not-open"), Capabilities());
        var health = new TaskCompletionSource<CameraHealthSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        existing.HealthStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? existing : replacement)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));

        var first = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(first.Succeeded, first.ReasonCode);
        var binding = first.Snapshot!.Binding!;
        existing.HealthHandler = () => health.Task.GetAwaiter().GetResult();
        harness.Runtime.Heartbeat();
        await existing.HealthStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "heartbeat-apply"));

        Assert.False(result.Succeeded);
        Assert.Equal("CameraOperationTimeout", result.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(existing.StopCalled);
        Assert.False(existing.Disposed);
        Assert.Equal(0, existing.ProviderOverlapCount);

        health.TrySetResult(new CameraHealthSnapshot(
            CameraProviderAvailability.Available, CameraConnectionState.Open,
            CameraConfigurationState.Unconfigured, CameraAcquisitionState.Stopped,
            new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
        await Eventually(() => existing.Disposed);
        Assert.Equal(0, existing.ProviderOverlapCount);
    }

    [Fact]
    public async Task V117_R29_LateRebindDisposeFaultRetainsRoleAndBlocksRetry()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "late-rebind-dispose-fault"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement-must-not-open"), Capabilities());
        var stop = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispose = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? existing : replacement)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));

        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var binding = initial.Snapshot!.Binding!;
        existing.StopHandler = _ => stop.Task;
        existing.DisposeHandler = () => dispose.Task;

        var failed = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(),
            harness.NewInvocation(), "Primary", binding.Revision, binding.RevisionHash,
            new CameraBindingTarget(identity, "Camera:One"), "late-rebind-dispose-fault"));
        Assert.False(failed.Succeeded);
        Assert.Equal("CameraOperationTimeout", failed.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(existing.Disposed);

        stop.TrySetResult(CameraOperationResult.Success());
        await Eventually(() => existing.DisposeStarted);
        dispose.TrySetException(new InvalidOperationException("late dispose failed"));
        await Eventually(() => existing.DisposeStarted && !existing.Disposed);

        var retry = await harness.Runtime.RebindAsync(new CameraRebindRequest(Guid.NewGuid(),
            harness.NewInvocation(), "Primary", binding.Revision, binding.RevisionHash,
            new CameraBindingTarget(identity, "Camera:One"), "late-rebind-retry"));
        Assert.False(retry.Succeeded);
        Assert.Equal("CameraDeviceCleanupRequired", retry.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        var current = await harness.Runtime.GetSetupAsync("Primary", harness.NewInvocation());
        Assert.True(current.Available);
        Assert.Equal(CameraConfigurationState.Unknown, current.Snapshot!.Health.Configuration);
        Assert.False(existing.Disposed);
        Assert.False(replacement.Disposed);
        await Task.Delay(350); // Allow the faulted cleanup continuation to retire its registration.
        var healthCalls = existing.HealthCallCount;
        harness.Runtime.Heartbeat();
        await Task.Delay(350);
        Assert.Equal(healthCalls, existing.HealthCallCount);
    }

    [Fact]
    public async Task V117_R30_LateApplyDisposeFaultRetainsRoleAndBlocksRetry()
    {
        var identity = ProviderIdentity();
        var existing = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "late-apply-dispose-fault"), Capabilities());
        var replacement = new FakeDevice(
            new CameraDeviceDescriptor(identity, "Camera:One", "replacement-must-not-open"), Capabilities());
        var stop = new TaskCompletionSource<CameraOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispose = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        var provider = new FakeProvider(identity, (_, _) =>
            Task.FromResult(CameraOpenResult.Success(++openCount == 1 ? existing : replacement)));
        await using var harness = Create(provider, operationTimeout: TimeSpan.FromMilliseconds(100));

        var initial = await harness.Runtime.RebindAsync(BindRequest(harness, identity));
        Assert.True(initial.Succeeded, initial.ReasonCode);
        var binding = initial.Snapshot!.Binding!;
        existing.StopHandler = _ => stop.Task;
        existing.DisposeHandler = () => dispose.Task;

        var failed = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "late-apply-dispose-fault"));
        Assert.False(failed.Succeeded);
        Assert.Equal("CameraOperationTimeout", failed.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        Assert.False(existing.Disposed);

        stop.TrySetResult(CameraOperationResult.Success());
        await Eventually(() => existing.DisposeStarted);
        dispose.TrySetException(new InvalidOperationException("late dispose failed"));
        await Eventually(() => existing.DisposeStarted && !existing.Disposed);

        var retry = await harness.Runtime.ApplyDebugConfigurationAsync(
            new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.NewInvocation(), "Primary",
                binding.Revision, binding.RevisionHash, Request(), "late-apply-retry"));
        Assert.False(retry.Succeeded);
        Assert.Equal("CameraDeviceCleanupRequired", retry.ReasonCode);
        Assert.Equal(1, provider.OpenCount);
        var current = await harness.Runtime.GetSetupAsync("Primary", harness.NewInvocation());
        Assert.True(current.Available);
        Assert.Equal(CameraConfigurationState.Unknown, current.Snapshot!.Health.Configuration);
        Assert.False(existing.Disposed);
        Assert.False(replacement.Disposed);
        await Task.Delay(350); // Allow the faulted cleanup continuation to retire its registration.
        var healthCalls = existing.HealthCallCount;
        harness.Runtime.Heartbeat();
        await Task.Delay(350);
        Assert.Equal(healthCalls, existing.HealthCallCount);
    }

    private static RuntimeHarness Create(FakeProvider provider,
        FakeAuthorizer? authorizer = null, TimeSpan? operationTimeout = null,
        TimeSpan? shutdownTimeout = null, bool useAuthorizer = true,
        ICameraSetupPersistence? persistence = null)
    {
        var principal = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var effectiveAuthorizer = authorizer ?? new FakeAuthorizer(principal, sessionId, revision: 4);
        var sessions = new FakeSessions(new InteractiveSession(
            InteractiveSessionState.Authenticated, principal.ToString("D"), sessionId));
        var identity = new FakeIdentityQuery();
        var audit = new FakeAuditWriter();
        var station = new CameraSetupRuntime.CameraStationContext(Guid.NewGuid(), false,
            ProductionArmState.Disarmed, false, null, 0, HandshakePhase.Idle,
            ExclusiveMode.None, RecoveryState.None, false, null, false, false);
        var published = new ConcurrentQueue<CameraSetupSnapshot>();
        var options = new CameraSetupOptions
        {
            OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2),
            ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(2)
        };
        var runtime = new CameraSetupRuntime(new[] { provider }, options, audit, sessions,
            identity, () => station, (_, snapshot) => published.Enqueue(snapshot),
            useAuthorizer ? effectiveAuthorizer : null, persistence: persistence);
        return new RuntimeHarness(runtime, audit, sessions, effectiveAuthorizer,
            principal, sessionId, published);
    }

    private static CameraRebindRequest BindRequest(RuntimeHarness harness,
        CameraProviderIdentity provider, string stableIdentity = "Camera:One") =>
        new(Guid.NewGuid(), harness.NewInvocation(), "Primary", 0, null,
            new CameraBindingTarget(provider, stableIdentity), "bind-test");

    private static RequestedCameraConfiguration Request() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
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
        new("Test.Camera.Provider", "1", "Test.Camera.Package", "1");

    private static CameraSetupPersistenceEvent ApplyPersistenceEvent(
        CameraSetupPersistencePhase phase, bool succeeded,
        CameraBindingRevision? previousBinding, CameraBindingRevision? binding,
        CameraBindingTarget target) => new(phase, Guid.NewGuid(), "Primary",
        AuditedCommandKind.ApplyCameraDebugConfiguration, previousBinding, binding, target,
        Request(), effective: null, differences: null, extension: null, health: null,
        succeeded, succeeded ? "CameraConfigurationApplied" : "CameraConfigurationApplyFailed",
        "camera-cas-test", Guid.NewGuid(), Guid.NewGuid(), 4);

    private static string? InvokeValidateBindingCas(CameraSetupPersistenceEvent input,
        CameraSetupStoreSnapshot state)
    {
        var method = typeof(SqliteCameraSetupPersistence).GetMethod("ValidateBindingCas",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, new object[] { input, state });
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(2))
                throw new TimeoutException("test physical cleanup did not complete");
            await Task.Delay(10);
        }
    }

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        internal RuntimeHarness(CameraSetupRuntime runtime, FakeAuditWriter audit,
            FakeSessions sessions, FakeAuthorizer authorizer, Guid principalId, Guid sessionId,
            ConcurrentQueue<CameraSetupSnapshot> published)
        {
            Runtime = runtime; Audit = audit; Sessions = sessions; PrincipalId = principalId;
            SessionId = sessionId; Authorizer = authorizer; Published = published;
        }

        internal CameraSetupRuntime Runtime { get; }
        internal FakeAuditWriter Audit { get; }
        internal FakeSessions Sessions { get; }
        internal FakeAuthorizer Authorizer { get; }
        internal Guid PrincipalId { get; }
        internal Guid SessionId { get; }
        internal ConcurrentQueue<CameraSetupSnapshot> Published { get; }

        internal CommandInvocation NewInvocation() =>
            new(CommandSource.PhysicalConsole, PrincipalId.ToString("D"), SessionId);

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class FakeProvider : ICameraProvider
    {
        private readonly Func<string, CancellationToken, Task<CameraOpenResult>> _open;
        internal FakeProvider(CameraProviderIdentity identity,
            Func<string, CancellationToken, Task<CameraOpenResult>> open)
        { Identity = identity; _open = open; }
        public CameraProviderIdentity Identity { get; }
        internal int OpenCount { get; private set; }
        internal TaskCompletionSource<bool>? OpenStarted { get; set; }
        internal int DiscoverCount { get; private set; }
        internal Func<CancellationToken, Task<CameraDiscoveryResult>>? DiscoverHandler { get; set; }
        internal TaskCompletionSource<bool>? DiscoverStarted { get; set; }
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            DiscoverStarted?.TrySetResult(true);
            var handler = DiscoverHandler;
            return handler is null
                ? ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()))
                : new ValueTask<CameraDiscoveryResult>(handler(cancellationToken));
        }
        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            OpenStarted?.TrySetResult(true);
            return new ValueTask<CameraOpenResult>(_open(stableDeviceIdentity, cancellationToken));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDevice : ICameraDevice
    {
        private readonly object _sync = new();
        private CameraConfigurationState _configuration = CameraConfigurationState.Unconfigured;
        internal FakeDevice(CameraDeviceDescriptor descriptor, CameraCapabilities capabilities)
        { Descriptor = descriptor; Capabilities = capabilities; }
        internal Func<RequestedCameraConfiguration, CancellationToken, Task<CameraConfigurationResult>>?
            ApplyHandler { private get; set; }
        internal Func<CancellationToken, Task<CameraOperationResult>>? StopHandler { private get; set; }
        internal Func<CameraHealthSnapshot>? HealthHandler { private get; set; }
        internal Func<Task>? DisposeHandler { private get; set; }
        internal Exception? SynchronousDisposeException { private get; set; }
        internal bool Disposed { get; private set; }
        internal bool DisposeStarted => Volatile.Read(ref _disposeStarted) != 0;
        internal bool StopCalled { get; private set; }
        internal bool ApplyStarted { get; private set; }
        internal TaskCompletionSource<bool>? HealthStarted { get; set; }
        internal int ProviderOverlapCount => Volatile.Read(ref _providerOverlapCount);
        internal int ApplyCallCount => Volatile.Read(ref _applyCallCount);
        internal int HealthCallCount => Volatile.Read(ref _healthCallCount);
        private int _applyCallCount;
        private int _healthCallCount;
        private int _activeProviderCalls;
        private int _providerOverlapCount;
        private int _disposeStarted;
        public CameraDeviceDescriptor Descriptor { get; }
        public CameraCapabilities Capabilities { get; }

        internal void MarkApplied() { lock (_sync) _configuration = CameraConfigurationState.Applied; }

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            EnterProviderCall();
            Interlocked.Increment(ref _healthCallCount);
            try
            {
                HealthStarted?.TrySetResult(true);
                var handler = HealthHandler;
                return handler is not null ? handler() : new CameraHealthSnapshot(
                    CameraProviderAvailability.Available, CameraConnectionState.Open,
                    _configuration, CameraAcquisitionState.Stopped,
                    new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp())));
            }
            finally { ExitProviderCall(); }
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested, CancellationToken cancellationToken = default)
        {
            ApplyStarted = true;
            Interlocked.Increment(ref _applyCallCount);
            var handler = ApplyHandler;
            if (handler is not null)
                return new ValueTask<CameraConfigurationResult>(handler(requested, cancellationToken));
            MarkApplied();
            return ValueTask.FromResult(Capabilities.ValidateConfiguration(requested));
        }

        public ValueTask<CameraOperationResult> StartAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraOperationResult.Failure("NotUsed"));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.NotStarted, "NotUsed")));

        public async ValueTask<CameraOperationResult> StopAsync(CancellationToken cancellationToken = default)
        {
            EnterProviderCall();
            StopCalled = true;
            try
            {
                var handler = StopHandler;
                return handler is null
                    ? CameraOperationResult.Success()
                    : await handler(cancellationToken).ConfigureAwait(false);
            }
            finally { ExitProviderCall(); }
        }

        public ValueTask DisposeAsync()
        {
            if (SynchronousDisposeException is { } exception)
                throw exception;
            return DisposeCoreAsync();
        }

        private async ValueTask DisposeCoreAsync()
        {
            EnterProviderCall();
            try
            {
                Interlocked.Exchange(ref _disposeStarted, 1);
                var handler = DisposeHandler;
                if (handler is not null) await handler().ConfigureAwait(false);
                Disposed = true;
            }
            finally { ExitProviderCall(); }
        }

        private void EnterProviderCall()
        {
            if (Interlocked.Increment(ref _activeProviderCalls) > 1)
                Interlocked.Increment(ref _providerOverlapCount);
        }

        private void ExitProviderCall() => Interlocked.Decrement(ref _activeProviderCalls);
    }

    private sealed class FakeAuditWriter : ICommandAuditWriter
    {
        internal List<CommandAuditFact> Facts { get; } = new();
        internal Func<CommandAuditFact, Task<StoreWriteResult>>? AppendHandler { get; set; }
        internal TaskCompletionSource<bool>? FailedAppendStarted { get; set; }
        public AuditIntegrityReport? Integrity => null;
        public Task<StoreWriteResult> Initialization =>
            Task.FromResult(new StoreWriteResult(true, "Ready"));
        public TimeSpan CommitTimeout => TimeSpan.FromSeconds(2);
        public ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact, StoreDeadline deadline,
            CancellationToken cancellationToken = default)
        {
            Facts.Add(fact);
            if (fact.Phase == CommandAuditPhase.Failed) FailedAppendStarted?.TrySetResult(true);
            var handler = AppendHandler;
            return handler is null
                ? ValueTask.FromResult(new StoreWriteResult(true, "Persisted", fact))
                : new ValueTask<StoreWriteResult>(handler(fact));
        }
    }

    private sealed class FakeAuthorizer : ICameraSetupAuthorizer
    {
        internal FakeAuthorizer(Guid principalId, Guid sessionId, long revision)
        { PrincipalId = principalId; SessionId = sessionId; Revision = revision; }
        internal Guid PrincipalId { get; }
        internal Guid SessionId { get; }
        private long Revision { get; }
        private int _reservationCount;
        private int _reservationRollbackCount;
        private int _reservationCommitCount;
        internal bool ReservationRolledBack => ReservationRollbackCount != 0;
        internal int ReservationCount => Volatile.Read(ref _reservationCount);
        internal int ReservationRollbackCount => Volatile.Read(ref _reservationRollbackCount);
        internal int ReservationCommitCount => Volatile.Read(ref _reservationCommitCount);

        public ValueTask<CameraSetupAuthorization> AuthorizeCameraSetupAsync(
            CommandInvocation invocation, bool readOnly, Guid operationId, string targetId,
            AuditedCommandKind commandKind, CancellationToken cancellationToken = default)
        {
            if (readOnly)
                return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                    PrincipalId, SessionId, Revision, null));
            Interlocked.Increment(ref _reservationCount);
            var reservation = CameraSetupAuthorizationReservation.Create(
                () => Interlocked.Increment(ref _reservationCommitCount),
                () => Interlocked.Increment(ref _reservationRollbackCount));
            return ValueTask.FromResult(new CameraSetupAuthorization(true, "Authorized",
                PrincipalId, SessionId, Revision, reservation));
        }
    }

    private sealed class FakeSessions : IInteractiveSessionService
    {
        internal FakeSessions(InteractiveSession current) => Current = current;
        public InteractiveSession Current { get; }
        private EventHandler<InteractiveSessionChangedEventArgs>? _changed;
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
    }

    private sealed class FakeIdentityQuery : IIdentityAdministrationQuery
    {
        public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeReadbackPersistence : ICameraSetupPersistence
    {
        private int _readCalls;
        private CameraBindingRevision? _binding;
        internal bool FailReadback { get; set; } = true;

        public ValueTask<CameraSetupPersistentState> ReadCameraSetupAsync(string logicalRole,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _readCalls);
            if (FailReadback && call > 1)
                throw new InvalidOperationException("controlled-readback-failure");

            var binding = _binding;
            var snapshot = binding is null ? null : new CameraSetupSnapshot(logicalRole, binding,
                new CameraHealthSnapshot(CameraProviderAvailability.Available,
                    CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                    CameraAcquisitionState.Stopped,
                    new FrameTimePoint(DateTimeOffset.UtcNow, Math.Max(0, Stopwatch.GetTimestamp()))));
            return ValueTask.FromResult(new CameraSetupPersistentState(snapshot, false,
                "CameraSetupRestarted"));
        }

        public ValueTask<StoreWriteResult> AppendCameraSetupAsync(CameraSetupPersistenceRequest request,
            StoreDeadline deadline, CancellationToken cancellationToken = default)
        {
            if (request.CameraEvent.Phase == CameraSetupPersistencePhase.Terminal &&
                request.CameraEvent.Binding is { } binding)
                _binding = binding with { Position = 42 };
            return ValueTask.FromResult(new StoreWriteResult(true, "Persisted", request.CommandFact));
        }
    }

    private sealed class FakePendingPersistence : ICameraSetupPersistence
    {
        private readonly CameraSetupPersistentState _state;
        internal FakePendingPersistence(CameraSetupPersistentState state) => _state = state;
        internal int AppendCalls { get; private set; }

        public ValueTask<CameraSetupPersistentState> ReadCameraSetupAsync(string logicalRole,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(_state);

        public ValueTask<StoreWriteResult> AppendCameraSetupAsync(CameraSetupPersistenceRequest request,
            StoreDeadline deadline, CancellationToken cancellationToken = default)
        {
            AppendCalls++;
            return ValueTask.FromResult(new StoreWriteResult(false, "UnexpectedPendingWrite"));
        }
    }
}
