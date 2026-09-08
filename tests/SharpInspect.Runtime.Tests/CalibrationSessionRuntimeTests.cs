#pragma warning disable CA1416

using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Integration coverage for the governed calibration-session seam.  These tests use
/// a real schema-14 SQLite writer, the local identity/Step-Up services, and a small
/// deterministic controlled-camera double.  The double is deliberately test-only:
/// it exposes physical call counts and a held procedure task, while all session,
/// evidence, authorization, and ownership decisions remain in Runtime.
/// </summary>
public sealed class CalibrationSessionRuntimeTests
{
    [Fact]
    public async Task V125_R01_UnexpectedCoefficientContractFailsAndRestoresWithoutCandidate()
    {
        await using var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true);
        fixture.Procedure.CoefficientOverride = new RecipeContractReference("unexpected-coefficients", "1", new string('B', 64));
        await fixture.WaitForHealthySourceAsync();
        var start = await fixture.Runtime.SubmitAsync(await fixture.CreateAuthorizedStartCommandAsync());
        Assert.Equal(CommandDisposition.Accepted, start.Disposition);
        var sessionId = (await WaitForSnapshotAsync(fixture, state => state.CalibrationSession is
            { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false })).CalibrationSession!.SessionId;
        var capture = await fixture.Runtime.SubmitAsync(new CaptureCalibrationFrameCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.Equal(CommandDisposition.Accepted, capture.Disposition);
        await WaitForSnapshotAsync(fixture, state => state.CalibrationSession is
            { ObservationCount: 1, OperationInProgress: false });
        var compute = await fixture.Runtime.SubmitAsync(new ComputeCalibrationCandidateCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.Equal(CommandDisposition.Accepted, compute.Disposition);
        var settled = await WaitForSnapshotAsync(fixture, state => state.CalibrationSession is
            { OperationInProgress: false, Phase: CalibrationSessionPhase.CandidateRetained or CalibrationSessionPhase.Restored });
        var evidence = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(evidence.Available, evidence.ReasonCode);
        Assert.Null(evidence.Evidence!.Candidate);
        Assert.Equal(CalibrationSessionPhase.Restored, settled.CalibrationSession!.Phase);
        Assert.Equal("CalibrationCoefficientContractMismatch", settled.CalibrationSession.ReasonCode);
        Assert.True(settled.CalibrationSession.RestorationVerified);
        Assert.False(settled.Ready);
        Assert.Equal(ExclusiveMode.None, settled.Mode);
    }

    [Fact]
    public async Task V124_R01_NoDevelopmentFixtureStopsBeforeAnyPhysicalAdmission()
    {
        await using var fixture = await Fixture.CreateAsync(withDevelopmentFixture: false);
        await fixture.WaitForHealthySourceAsync();

        var start = fixture.CreateStartCommand();
        var result = await fixture.Runtime.SubmitAsync(start);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        var rejectedSnapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.True(result.ReasonCode == "SafetyStopUnverified", $"{result.ReasonCode}; {rejectedSnapshot.Store}; alarm={rejectedSnapshot.AlarmState?.ReasonCode}; integrity={rejectedSnapshot.AuditIntegrity?.ReasonCode}");

        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        Assert.Null(snapshot.CalibrationSession);
        Assert.Equal(0, fixture.Provider.OpenCount);
        Assert.Equal(0, fixture.Initial.ApplyCount);
        Assert.Equal(0, fixture.Initial.StartCount);
        Assert.Equal(0, fixture.Initial.AcquireCount);
    }

    [Fact]
    public async Task V124_R02_AcceptedHeaderConsumesOneStepUpAndAssignsDurableSession()
    {
        await using var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true);
        await fixture.WaitForHealthySourceAsync();

        var withoutStepUp = await fixture.Runtime.SubmitAsync(fixture.CreateStartCommand());
        Assert.Equal(CommandDisposition.Rejected, withoutStepUp.Disposition);
        Assert.Equal("StepUpRequired", withoutStepUp.ReasonCode);
        Assert.Equal(0, fixture.Provider.OpenCount);

        var start = await fixture.CreateAuthorizedStartCommandAsync();
        var accepted = await fixture.Runtime.SubmitAsync(start);
        Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
        Assert.Equal("CalibrationSessionStartAuthorized", accepted.ReasonCode);

        var snapshot = await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { } session && session.SessionId != Guid.Empty);
        var sessionId = snapshot.CalibrationSession!.SessionId;
        Assert.NotEqual(Guid.Empty, sessionId);
        Assert.Equal(ExclusiveMode.Calibration, snapshot.Mode);
        Assert.False(snapshot.Ready);

        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false });
        Assert.True(fixture.Provider.OpenCount >= 1);

        var evidence = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(evidence.Available, evidence.ReasonCode);
        Assert.Equal(sessionId, evidence.Evidence!.State.SessionId);
        Assert.Equal(CalibrationSessionOutcome.Pending, evidence.Evidence!.State.Outcome);
        Assert.NotNull(evidence.Evidence!.TemporaryConfiguration);
        Assert.Equal(fixture.Plan.TemporaryConfiguration,
            evidence.Evidence!.TemporaryConfiguration!.Requested);
        Assert.Equal(fixture.BaselineEffective,
            evidence.Evidence!.TemporaryConfiguration!.Effective);

        await fixture.ExitAsync(sessionId);
    }

    [Fact]
    public async Task V124_R03_CaptureRetainsObservationAndWholeFrameExclusionFreezesDuringCompute()
    {
        await using var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true,
            blockCompute: true);
        await fixture.WaitForHealthySourceAsync();
        var start = await fixture.CreateAuthorizedStartCommandAsync();
        var accepted = await fixture.Runtime.SubmitAsync(start);
        Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
        var sessionId = (await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false }))
            .CalibrationSession!.SessionId;

        var firstCapture = await fixture.Runtime.SubmitAsync(
            new CaptureCalibrationFrameCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.True(firstCapture.Disposition == CommandDisposition.Accepted, firstCapture.ReasonCode);
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { FrameCount: >= 1, ObservationCount: >= 1, OperationInProgress: false });

        var secondCapture = await fixture.Runtime.SubmitAsync(
            new CaptureCalibrationFrameCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.True(secondCapture.Disposition == CommandDisposition.Accepted, secondCapture.ReasonCode);
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { FrameCount: >= 2, ObservationCount: >= 2, OperationInProgress: false });

        var beforeExclusion = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(beforeExclusion.Available, beforeExclusion.ReasonCode);
        Assert.Equal(2, beforeExclusion.Evidence!.Frames.Count);
        Assert.Equal(2, beforeExclusion.Evidence!.Observations.Count);
        var firstFrame = beforeExclusion.Evidence!.Frames[0];
        var secondFrame = beforeExclusion.Evidence!.Frames[1];

        var excluded = await fixture.Runtime.SubmitAsync(new ExcludeCalibrationFrameCommand(
            Guid.NewGuid(), fixture.User.Invocation, sessionId, firstFrame.FrameId,
            "whole frame excluded by operator"));
        Assert.True(excluded.Disposition == CommandDisposition.Accepted, excluded.ReasonCode);
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { ExcludedFrameCount: 1, OperationInProgress: false });

        var compute = await fixture.Runtime.SubmitAsync(
            new ComputeCalibrationCandidateCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.True(compute.Disposition == CommandDisposition.Accepted, compute.ReasonCode);
        await fixture.Procedure.ComputeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Computing });

        var lateExclusion = await fixture.Runtime.SubmitAsync(new ExcludeCalibrationFrameCommand(
            Guid.NewGuid(), fixture.User.Invocation, sessionId, secondFrame.FrameId,
            "must be rejected after computation starts"));
        Assert.Equal(CommandDisposition.Rejected, lateExclusion.Disposition);
        Assert.Contains(lateExclusion.ReasonCode,
            new[] { "CalibrationOperationInProgress", "CalibrationEvidenceSelectionFrozen" });

        var stillFrozen = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(stillFrozen.Available, stillFrozen.ReasonCode);
        Assert.Single(stillFrozen.Evidence!.Exclusions);
        Assert.Equal(firstFrame.FrameId, stillFrozen.Evidence!.Exclusions[0].FrameId);
        Assert.Null(stillFrozen.Evidence!.Candidate);

        fixture.Procedure.ReleaseCompute();
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.CandidateRetained, OperationInProgress: false });
        var completedCompute = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(completedCompute.Available, completedCompute.ReasonCode);
        Assert.NotNull(completedCompute.Evidence!.Candidate);
        Assert.Equal(2, completedCompute.Evidence!.Observations.Count);
        Assert.Single(completedCompute.Evidence!.Exclusions);

        await fixture.ExitAsync(sessionId);
    }

    [Fact]
    public async Task V124_R04_ProcedureTimeoutRestoresPhysicalBaselineBeforeUncooperativeTaskReturns()
    {
        await using var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true,
            blockCompute: true, operationTimeout: TimeSpan.FromMilliseconds(100));
        await fixture.WaitForHealthySourceAsync();
        var start = await fixture.CreateAuthorizedStartCommandAsync();
        var accepted = await fixture.Runtime.SubmitAsync(start);
        Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
        var sessionId = (await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false }))
            .CalibrationSession!.SessionId;

        var capture = await fixture.Runtime.SubmitAsync(
            new CaptureCalibrationFrameCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.True(capture.Disposition == CommandDisposition.Accepted, capture.ReasonCode);
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { FrameCount: 1, ObservationCount: 1, OperationInProgress: false });

        var compute = await fixture.Runtime.SubmitAsync(
            new ComputeCalibrationCandidateCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.True(compute.Disposition == CommandDisposition.Accepted, compute.ReasonCode);
        await fixture.Procedure.ComputeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The procedure ignores the Runtime cancellation token. Runtime must still
        // perform the physical safety action and retain the session fence and loan.
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.RecoveryBlocked });
        await EventuallyAsync(() => fixture.Procedure.CancellationObserved);
        Assert.NotNull(fixture.Procedure.LastComputeFrame);
        Assert.True(fixture.Procedure.LastComputeFrame!.IsLoanActive);

        var blocked = await fixture.Runtime.GetSnapshotAsync();
        Assert.False(blocked.Ready);
        Assert.Equal(ExclusiveMode.Calibration, blocked.Mode);
        Assert.NotNull(blocked.CalibrationSession);
        Assert.False(blocked.CalibrationSession!.RestorationVerified);
        Assert.True(fixture.Recovery.IsCalibrationActive);

        // Open count: initial seed + temporary owner + the restored baseline owner.
        await EventuallyAsync(() => fixture.Provider.OpenCount >= 2);
        var restored = fixture.Provider.Devices.Last();
        Assert.Equal(fixture.BaselineEffective, restored.EffectiveConfiguration);
        Assert.True(restored.StartCount >= 1);

        fixture.Procedure.ReleaseCompute();
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Restored,
                Outcome: CalibrationSessionOutcome.Failed, RestorationVerified: true });

        var terminal = await fixture.QueryEvidenceAsync(sessionId);
        Assert.True(terminal.Available, terminal.ReasonCode);
        Assert.Equal(CalibrationSessionPhase.Restored, terminal.Evidence!.State.Phase);
        Assert.Equal(CalibrationSessionOutcome.Failed, terminal.Evidence!.State.Outcome);
        Assert.NotEqual(CalibrationSessionOutcome.Cancelled, terminal.Evidence!.State.Outcome);
        Assert.False(fixture.Procedure.LastComputeFrame!.IsLoanActive);
        Assert.False(fixture.Recovery.IsCalibrationActive);
    }

    [Fact]
    public async Task V124_R05_ShutdownTimesOutWithoutDroppingCameraFenceOrBorrowedFrame()
    {
        // This test intentionally owns the fixture manually: the first Runtime dispose is
        // expected to fail closed while the procedure still owns its borrowed frame.  Cleanup
        // is completed only after the actual consumer task is released.
        var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true,
            blockCompute: true, operationTimeout: TimeSpan.FromMilliseconds(100));
        try
        {
            await fixture.WaitForHealthySourceAsync();
            var start = await fixture.CreateAuthorizedStartCommandAsync();
            var accepted = await fixture.Runtime.SubmitAsync(start);
            Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
            var sessionId = (await WaitForSnapshotAsync(fixture,
                current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false }))
                .CalibrationSession!.SessionId;

            var capture = await fixture.Runtime.SubmitAsync(
                new CaptureCalibrationFrameCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
            Assert.True(capture.Disposition == CommandDisposition.Accepted, capture.ReasonCode);
            await WaitForSnapshotAsync(fixture,
                current => current.CalibrationSession is { FrameCount: 1, ObservationCount: 1, OperationInProgress: false });

            var computeCorrelation = Guid.NewGuid();
            var compute = await fixture.Runtime.SubmitAsync(
                new ComputeCalibrationCandidateCommand(computeCorrelation, fixture.User.Invocation, sessionId));
            Assert.True(compute.Disposition == CommandDisposition.Accepted, compute.ReasonCode);
            await fixture.Procedure.ComputeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var disposeFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await fixture.Runtime.DisposeAsync());
            Assert.Equal("CalibrationShutdownIncomplete", disposeFailure.Message);

            var blocked = await WaitForSnapshotAsync(fixture,
                current => current.CalibrationSession is { Phase: CalibrationSessionPhase.RecoveryBlocked });
            Assert.NotEqual(RuntimeLifecycle.Stopped, blocked.Lifecycle);
            Assert.Equal(ExclusiveMode.Calibration, blocked.Mode);
            Assert.False(blocked.Ready);
            Assert.NotNull(blocked.CalibrationSession);
            Assert.False(blocked.CalibrationSession!.RestorationVerified);
            Assert.True(fixture.Recovery.IsCalibrationActive);
            Assert.NotNull(fixture.Procedure.LastComputeFrame);
            Assert.True(fixture.Procedure.LastComputeFrame!.IsLoanActive);
            await EventuallyAsync(() => fixture.Procedure.CancellationObserved);

            // The physical owner is restored before shutdown reports the bounded failure,
            // while the uncooperative procedure and session fence remain retained.
            await EventuallyAsync(() => fixture.Provider.OpenCount >= 2);
            var restored = fixture.Provider.Devices.Last();
            Assert.Equal(fixture.BaselineEffective, restored.EffectiveConfiguration);
            Assert.True(restored.StartCount >= 1);

            fixture.Procedure.ReleaseCompute();
            await WaitForSnapshotAsync(fixture,
                current => current.CalibrationSession is { Phase: CalibrationSessionPhase.Restored,
                    Outcome: CalibrationSessionOutcome.Cancelled, RestorationVerified: true } &&
                    current.LastCommand is { CorrelationId: var correlation,
                        State: OperationState.Failed } && correlation == computeCorrelation);
            Assert.False(fixture.Procedure.LastComputeFrame!.IsLoanActive);
            Assert.False(fixture.Recovery.IsCalibrationActive);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task V124_R06_ShutdownRejectsAdmissionBlockedInTypedInputValidation()
    {
        var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true,
            blockInputValidation: true);
        try
        {
            await fixture.WaitForHealthySourceAsync();
            var start = await fixture.CreateAuthorizedStartCommandAsync();
            var startTask = fixture.Runtime.SubmitAsync(start).AsTask();
            await fixture.Procedure.InputValidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Submit holds the command gate while the typed input codec is validating.
            // Shutdown must publish its stopping fence before waiting for that gate, so
            // releasing the deterministic validation call cannot create an admitted header.
            var shutdownTask = fixture.Runtime.DisposeAsync().AsTask();
            fixture.Procedure.ReleaseInputValidation();

            var rejected = await startTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
            Assert.Equal("RuntimeStopping", rejected.ReasonCode);
            Assert.Null((await fixture.Runtime.GetSnapshotAsync()).CalibrationSession);
            Assert.Equal(0, fixture.Provider.OpenCount);
            Assert.Equal(0, fixture.Initial.ApplyCount);
            Assert.Equal(0, fixture.Initial.StartCount);
            Assert.Equal(0, fixture.Initial.AcquireCount);

            await shutdownTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task V124_R07_RestartRestoresPendingSessionBeforeStoreReady()
    {
        // This is an integration-level crash/restart simulation.  The first runtime has
        // already settled its admission work, then loses its recovery owner before its
        // shutdown attempt.  The second runtime must restore the exact durable baseline
        // before publishing a healthy store projection.
        var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true);
        SqliteCommandStore? reopenedStore = null;
        CameraRecoveryService? reopenedRecovery = null;
        StationRuntime? reopenedRuntime = null;
        TaskCompletionSource<bool>? healthEntered = null;
        TaskCompletionSource<bool>? releaseHealth = null;
        try
        {
            await fixture.WaitForHealthySourceAsync();
            var start = await fixture.CreateAuthorizedStartCommandAsync();
            var accepted = await fixture.Runtime.SubmitAsync(start);
            Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);

            // Do not simulate the restart while Start is still changing physical state.
            var collecting = await WaitForSnapshotAsync(fixture,
                current => current.CalibrationSession is
                { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false });
            var sessionId = collecting.CalibrationSession!.SessionId;
            var beforeRestart = await fixture.Store.ReadCalibrationSessionAsync(
                sessionId, CancellationToken.None);
            Assert.True(beforeRestart.Available, beforeRestart.ReasonCode);
            Assert.Equal(CalibrationSessionOutcome.Pending,
                beforeRestart.Evidence!.State.Outcome);
            Assert.Equal(CalibrationSessionPhase.Collecting,
                beforeRestart.Evidence.State.Phase);
            Assert.NotNull(beforeRestart.Evidence.TemporaryConfiguration);

            // The old owner is deliberately retired first.  Shutdown must then fail
            // closed, retain its one task, and finish its heartbeat before the store is
            // released for the next runtime.
            await fixture.Recovery.DisposeAsync();
            var oldShutdown = fixture.Runtime.DisposeAsync().AsTask();
            var shutdownFailure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await oldShutdown.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal("CalibrationShutdownIncomplete", shutdownFailure.Message);
            Assert.True(oldShutdown.IsCompleted);
            Assert.Same(oldShutdown, fixture.Runtime.DisposeAsync().AsTask());
            await fixture.Store.DisposeAsync();

            var provider = new TestProvider();
            var target = new CameraBindingTarget(provider.Identity, "Virtual:Camera.One");
            var clock = new TestClock();
            var initial = new TestDevice(provider.Identity, target.StableDeviceIdentity,
                clock, fixture.BaselineEffective, armed: true, frameSeed: 100);
            provider.SetInitial(initial);

            // Hold the newly opened candidate at its actual health call.  The runtime
            // must remain in restart restoration, with the store unavailable, until
            // that physical proof completes.
            healthEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releaseHealth = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            provider.BlockNextOpenedDeviceHealth(healthEntered, releaseHealth);

            var acquisition = new CameraAcquisitionService(initial,
                fixture.BaselineEffective, clock,
                new CameraAcquisitionOptions(TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(500), protocolReadTimeout: TimeSpan.FromSeconds(5)));
            reopenedRecovery = new CameraRecoveryService(provider, target, "TopCamera",
                fixture.BaselineRequested, acquisition, clock,
                new CameraRecoveryOptions(TimeSpan.FromMilliseconds(20), 3,
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));

            reopenedStore = new SqliteCommandStore(fixture.Options);
            var reopenedInitialization = await reopenedStore.Initialization.WaitAsync(
                TimeSpan.FromSeconds(15));
            Assert.True(reopenedInitialization.Committed, reopenedInitialization.ReasonCode);
            await Fixture.WaitForVerifiedAsync(reopenedStore);
            reopenedRuntime = new StationRuntime(reopenedStore,
                TimeSpan.FromMilliseconds(20), cameraRecoveryService: reopenedRecovery,
                calibrationSessionOptions: new CalibrationSessionOptions
                {
                    OperationTimeout = TimeSpan.FromSeconds(5)
                }, productionStoreOptions: fixture.Options);

            await healthEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var restoring = await WaitForSnapshotAsync(reopenedRuntime,
                current => current.CalibrationSession is
                { Phase: CalibrationSessionPhase.Restoring });
            Assert.Equal(RuntimeLifecycle.Running, restoring.Lifecycle);
            Assert.Equal(ExclusiveMode.Calibration, restoring.Mode);
            Assert.False(restoring.Ready);
            Assert.False(restoring.CalibrationSession!.RestorationVerified);
            Assert.NotEqual(HealthState.Healthy, restoring.Store.State);
            Assert.Equal(1, provider.OpenCount);
            var candidate = Assert.Single(provider.Devices);
            Assert.True(candidate.ApplyCount >= 1);
            Assert.True(candidate.StartCount >= 1);
            Assert.Equal(fixture.BaselineEffective, candidate.EffectiveConfiguration);
            Assert.Equal(CameraRecoveryState.RecoveryRequired, reopenedRecovery.GetSnapshot().State);

            releaseHealth.TrySetResult(true);

            var restored = await WaitForSnapshotAsync(reopenedRuntime,
                current => current.CalibrationSession is
                {
                    Phase: CalibrationSessionPhase.Restored,
                    Outcome: CalibrationSessionOutcome.RestartAborted,
                    RestorationVerified: true
                } && current.Store.State == HealthState.Healthy);
            Assert.Equal(ExclusiveMode.None, restored.Mode);
            Assert.False(restored.Ready);
            Assert.Equal(CameraRecoveryState.Healthy, reopenedRecovery.GetSnapshot().State);
            Assert.True(reopenedRecovery.GetSnapshot().SourceHealthy);
            Assert.Equal(fixture.BaselineEffective,
                Assert.Single(provider.Devices).EffectiveConfiguration);
            await Fixture.WaitForVerifiedAsync(reopenedStore);

            // Query the signed, complete evidence directly from the reopened store.  No
            // authorization service is supplied to the second runtime, so this also
            // proves restart recovery is independent of a new interactive session.
            var afterRestart = await reopenedStore.ReadCalibrationSessionAsync(
                sessionId, CancellationToken.None);
            Assert.True(afterRestart.Available, afterRestart.ReasonCode);
            Assert.Equal(sessionId, afterRestart.Evidence!.Header.SessionId);
            Assert.Equal(CalibrationSessionPhase.Restored,
                afterRestart.Evidence.State.Phase);
            Assert.Equal(CalibrationSessionOutcome.RestartAborted,
                afterRestart.Evidence.State.Outcome);
            Assert.True(afterRestart.Evidence.State.RestorationVerified);
            Assert.Empty(await reopenedStore.ReadOpenCalibrationSessionsAsync(
                CancellationToken.None));
            Assert.Empty(await reopenedStore.ReadPendingCalibrationActionsAsync(
                sessionId, CancellationToken.None));
        }
        finally
        {
            releaseHealth?.TrySetResult(true);
            if (reopenedRuntime is not null)
            {
                try { await reopenedRuntime.DisposeAsync(); }
                catch (InvalidOperationException failure) when
                    (failure.Message == "CalibrationShutdownIncomplete") { }
            }
            if (reopenedRecovery is not null)
                await reopenedRecovery.RetireAsync();
            if (reopenedStore is not null)
                await reopenedStore.DisposeAsync();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task V124_R08_LockedSessionSerializesPendingActionAndRestores()
    {
        await using var fixture = await Fixture.CreateAsync(withDevelopmentFixture: true,
            blockCompute: true);
        await fixture.WaitForHealthySourceAsync();

        var start = await fixture.CreateAuthorizedStartCommandAsync();
        var acceptedStart = await fixture.Runtime.SubmitAsync(start);
        Assert.True(acceptedStart.Disposition == CommandDisposition.Accepted,
            acceptedStart.ReasonCode);
        var sessionId = (await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is
            { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false }))
            .CalibrationSession!.SessionId;

        var capture = await fixture.Runtime.SubmitAsync(
            new CaptureCalibrationFrameCommand(Guid.NewGuid(), fixture.User.Invocation, sessionId));
        Assert.True(capture.Disposition == CommandDisposition.Accepted, capture.ReasonCode);
        await WaitForSnapshotAsync(fixture,
            current => current.CalibrationSession is
            { FrameCount: 1, ObservationCount: 1, OperationInProgress: false });

        var computeCorrelation = Guid.NewGuid();
        var compute = await fixture.Runtime.SubmitAsync(
            new ComputeCalibrationCandidateCommand(computeCorrelation,
                fixture.User.Invocation, sessionId));
        Assert.True(compute.Disposition == CommandDisposition.Accepted, compute.ReasonCode);
        await fixture.Procedure.ComputeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Locking publishes the authority revocation immediately.  The pending compute
        // remains owned by Runtime until its actual consumer task settles and the same
        // physical baseline restoration completes.
        var locked = await fixture.Sessions.LockAsync(fixture.User.SessionId,
            SessionLockReason.UserRequested);
        Assert.True(locked.Succeeded, locked.ReasonCode);
        var immediatelyLocked = await fixture.Runtime.GetSnapshotAsync();
        Assert.Equal(InteractiveSessionState.Locked, immediatelyLocked.Session.State);
        Assert.True(immediatelyLocked.CalibrationSession is { OperationInProgress: true } ||
            immediatelyLocked.CalibrationSession is
            { Phase: CalibrationSessionPhase.Restored, RestorationVerified: true });

        fixture.Procedure.ReleaseCompute();
        var restored = await WaitForSnapshotAsync(fixture,
            current => current.Session.State == InteractiveSessionState.Locked &&
                current.CalibrationSession is
                {
                    Phase: CalibrationSessionPhase.Restored,
                    Outcome: CalibrationSessionOutcome.Cancelled,
                    RestorationVerified: true,
                    OperationInProgress: false
                });
        Assert.Equal(ExclusiveMode.None, restored.Mode);
        Assert.False(restored.Ready);
        Assert.False(fixture.Recovery.IsCalibrationActive);

        await Fixture.WaitForVerifiedAsync(fixture.Store);
        var evidence = await fixture.Store.ReadCalibrationSessionAsync(
            sessionId, CancellationToken.None);
        Assert.True(evidence.Available, evidence.ReasonCode);
        Assert.Equal(CalibrationSessionOutcome.Cancelled, evidence.Evidence!.State.Outcome);
        Assert.Equal(CalibrationSessionPhase.Restored, evidence.Evidence.State.Phase);
        Assert.True(evidence.Evidence.State.RestorationVerified);

        var trace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(CorrelationId: computeCorrelation, PageSize: 20));
        Assert.Equal(new[] { CommandAuditPhase.Outcome, CommandAuditPhase.Failed },
            trace.Records.Select(record => record.Phase));
        Assert.All(trace.Records, record =>
        {
            Assert.Equal(AuditedCommandKind.ComputeCalibrationCandidate, record.CommandKind);
            Assert.Equal(computeCorrelation, record.CorrelationId);
        });

        var startTrace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(CorrelationId: start.CorrelationId, PageSize: 20));
        Assert.Contains(startTrace.Records, record =>
            record.CommandKind == AuditedCommandKind.StartCalibrationSession &&
            record.Phase == CommandAuditPhase.Failed);
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 15d);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new XunitException("Calibration runtime condition timed out.");
            await Task.Delay(10);
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 15d);
        while (!await condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new XunitException("Calibration runtime condition timed out.");
            await Task.Delay(10);
        }
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(Fixture fixture,
        Func<StationStateSnapshot, bool> condition)
    {
        StationStateSnapshot current = await fixture.Runtime.GetSnapshotAsync();
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 15d);
        while (!condition(current))
        {
            if (current.Store.State == HealthState.Faulted)
                throw new XunitException($"Calibration store fault: {current.Store.ReasonCode}; integrity={current.AuditIntegrity?.ReasonCode}; alarm={current.AlarmState?.ReasonCode}.");
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new XunitException($"Calibration snapshot condition timed out at {current.CalibrationSession?.Phase}/{current.CalibrationSession?.ReasonCode}; store={current.Store}; alarm={current.AlarmState?.ReasonCode}; last={current.LastCommand}.");
            await Task.Delay(10);
            current = await fixture.Runtime.GetSnapshotAsync();
        }
        return current;
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(
        StationRuntime runtime, Func<StationStateSnapshot, bool> condition)
    {
        StationStateSnapshot current = await runtime.GetSnapshotAsync();
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 15d);
        while (!condition(current))
        {
            if (current.Store.State == HealthState.Faulted)
                throw new XunitException($"Calibration restart store fault: {current.Store.ReasonCode}; integrity={current.AuditIntegrity?.ReasonCode}; alarm={current.AlarmState?.ReasonCode}.");
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new XunitException($"Calibration restart snapshot condition timed out at {current.CalibrationSession?.Phase}/{current.CalibrationSession?.ReasonCode}; store={current.Store}; alarm={current.AlarmState?.ReasonCode}; last={current.LastCommand}.");
            await Task.Delay(10);
            current = await runtime.GetSnapshotAsync();
        }
        return current;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string UserName = "calibration-session-admin";
        private const string Password = "V124 calibration session test secret 26!";
        private const string Role = "TopCamera";
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _audit;
        private bool _disposed;

        private Fixture(string directory, AuditIntegrityPolicy audit,
            ProductionStoreOptions options, SqliteCommandStore store,
            LocalIdentityService identity, InteractiveSessionService sessions,
            LocalAuthorizationService authorization, StationRuntime runtime,
            TestProvider provider, TestDevice initial, CameraRecoveryService recovery,
            TestProcedure procedure, SignedIn user, CameraBindingRevision binding,
            ImagingSetupRevision imaging, CalibrationSessionPlan plan,
            EffectiveCameraConfiguration baselineEffective)
        {
            _directory = directory;
            _audit = audit;
            Options = options;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            Runtime = runtime;
            Provider = provider;
            Initial = initial;
            Recovery = recovery;
            Procedure = procedure;
            User = user;
            Binding = binding;
            Imaging = imaging;
            Plan = plan;
            BaselineEffective = baselineEffective;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal StationRuntime Runtime { get; }
        internal TestProvider Provider { get; }
        internal TestDevice Initial { get; }
        internal CameraRecoveryService Recovery { get; }
        internal TestProcedure Procedure { get; }
        internal SignedIn User { get; }
        internal CameraBindingRevision Binding { get; }
        internal ImagingSetupRevision Imaging { get; }
        internal CalibrationSessionPlan Plan { get; }
        internal EffectiveCameraConfiguration BaselineEffective { get; }
        internal RequestedCameraConfiguration BaselineRequested { get; private init; } = null!;

        internal static async Task<Fixture> CreateAsync(bool withDevelopmentFixture,
            bool blockCompute = false, TimeSpan? operationTimeout = null,
            bool blockInputValidation = false)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Calibration session integration requires Windows machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V124-CalibrationSession-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var evidenceRoot = Path.Combine(directory, "evidence");
            Directory.CreateDirectory(evidenceRoot);
            var audit = new AuditIntegrityPolicy("V124CalibrationSession", "1",
                "SharpInspect.Test.V124.CalibrationSession." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1),
                MaximumVerificationEntries = 10_000
            };
            var authorizationPolicy = CreateAuthorizationPolicy();
            var identityOptions = new LocalIdentityOptions(audit.StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v124-calibration-blocklist", "1",
                        new[] { "known-compromised-calibration-password" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                authorizationPolicy);
            var options = new ProductionStoreOptions(Path.Combine(directory, "calibration.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                AlarmPolicy = CreateAlarmPolicy(),
                CameraSetup = new CameraSetupStoreOptions(),
                CameraRecovery = new CameraRecoveryStoreOptions(),
                ImagingSetup = new ImagingSetupStoreOptions(),
                CalibrationSessions = new CalibrationSessionStoreOptions
                {
                    EvidenceRoot = evidenceRoot,
                    MaximumSessions = 4,
                    MaximumEvents = 256,
                    MaximumFramesPerSession = 8,
                    MaximumFrameBytes = 1024 * 1024,
                    MaximumTotalFrameBytes = 8 * 1024 * 1024
                },
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 16
            };

            SqliteCommandStore? store = null;
            InteractiveSessionService? sessions = null;
            LocalAuthorizationService? authorization = null;
            CameraRecoveryService? recovery = null;
            StationRuntime? runtime = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialization = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialization.Committed, initialization.ReasonCode);
                await WaitForVerifiedAsync(store);

                var identity = new LocalIdentityService(store, identityOptions,
                    new FixtureConsoleAuthority());
                sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                authorization = new LocalAuthorizationService(store, identityOptions, identity, sessions);

                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(audit.StationId, token.Token!.TakeForDisplay(),
                        UserName, "V124 Calibration Administrator", Password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await WaitForVerifiedAsync(store);

                var signedInResult = await sessions.SignInAsync(
                    new PasswordSignInRequest(UserName, Password));
                Assert.True(signedInResult.Succeeded, signedInResult.ReasonCode);
                var user = new SignedIn(signedInResult.Identity!.PrincipalId,
                    signedInResult.Session.SessionId!.Value,
                    (await store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts().Single(item =>
                        item.PrincipalId == signedInResult.Identity.PrincipalId).AuthorizationRevision);
                await WaitForVerifiedAsync(store);

                var provider = new TestProvider();
                var target = new CameraBindingTarget(provider.Identity, "Virtual:Camera.One");
                var baselineRequested = Configuration(10);
                var capabilities = TestDevice.CreateCapabilities();
                var baselineEffective = capabilities.ValidateConfiguration(baselineRequested).Effective!;
                var initial = new TestDevice(provider.Identity, target.StableDeviceIdentity,
                    new TestClock(), baselineEffective, armed: true, frameSeed: 1);
                provider.SetInitial(initial);
                var binding = await AppendCompletedBindingAsync(store, audit, user, target);
                var imaging = await AppendImagingAsync(store, options, authorization, user, binding);

                var procedure = new TestProcedure(blockCompute, blockInputValidation);
                var plan = CreatePlan(procedure, baselineRequested);
                var imagingReference = ImagingSetupRevisionReference.FromRevision(imaging);
                var calibrationOptions = new CalibrationSessionOptions
                {
                    OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(2),
                    DevelopmentFixture = withDevelopmentFixture
                        ? new DevelopmentCalibrationFixture("V124-test-fixture", binding,
                            imagingReference, baselineRequested, baselineEffective, plan, options)
                        : null
                };
                var clock = initial.Clock;
                var acquisition = new CameraAcquisitionService(initial, baselineEffective, clock,
                    new CameraAcquisitionOptions(TimeSpan.FromMilliseconds(500),
                        TimeSpan.FromMilliseconds(500), protocolReadTimeout: TimeSpan.FromMilliseconds(100)));
                recovery = new CameraRecoveryService(provider, target, Role, baselineRequested,
                    acquisition, clock, new CameraRecoveryOptions(TimeSpan.FromMilliseconds(20), 3,
                        TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)));
                var registry = new CalibrationProcedureRegistry(new IRegisteredCalibrationProcedure[]
                {
                    new RegisteredCalibrationProcedure<byte>(procedure)
                });
                runtime = new StationRuntime(store, TimeSpan.FromMilliseconds(20), sessions,
                    authorization, cameraRecoveryService: recovery,
                    calibrationSessionOptions: calibrationOptions,
                    calibrationProcedures: registry, productionStoreOptions: options);

                var fixture = new Fixture(directory, audit, options, store, identity, sessions,
                    authorization, runtime, provider, initial, recovery, procedure, user, binding,
                    imaging, plan, baselineEffective)
                {
                    BaselineRequested = baselineRequested
                };
                return fixture;
            }
            catch
            {
                if (runtime is not null) await runtime.DisposeAsync();
                if (recovery is not null) await recovery.DisposeAsync();
                authorization?.Dispose();
                if (sessions is not null) await sessions.DisposeAsync();
                if (store is not null) await store.DisposeAsync();
                Cleanup(directory, audit);
                throw;
            }
        }

        internal StartCalibrationSessionCommand CreateStartCommand() => new(Guid.NewGuid(),
            User.Invocation, Plan, Binding.Revision, Binding.RevisionHash,
            ImagingSetupRevisionReference.FromRevision(Imaging), "V124 calibration session");

        internal async Task<StartCalibrationSessionCommand> CreateAuthorizedStartCommandAsync()
        {
            var command = CreateStartCommand();
            var result = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                User.Invocation, new StepUpBinding(Permission.RunCalibration,
                    command.CorrelationId, command.AuthorizationTarget,
                    AuditedCommandKind.StartCalibrationSession), Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.GrantId);
            await WaitForVerifiedAsync(Store);
            return command with { Invocation = User.Invocation with { StepUpGrantId = result.GrantId } };
        }

        internal async Task<CalibrationSessionQueryResult> QueryEvidenceAsync(Guid sessionId) =>
            await Runtime.QueryCalibrationSessionAsync(sessionId, User.Invocation);

        internal async Task WaitForHealthySourceAsync()
        {
            await EventuallyAsync(() =>
                Recovery.GetSnapshot() is { State: CameraRecoveryState.Healthy, SourceHealthy: true });
            await WaitForSnapshotAsync(this, snapshot => snapshot.Store.State == HealthState.Healthy &&
                snapshot.AlarmState is { Available: true });
        }

        internal async Task ExitAsync(Guid sessionId)
        {
            var accepted = await Runtime.SubmitAsync(new ExitCalibrationSessionCommand(Guid.NewGuid(),
                User.Invocation, sessionId, "V124 test exit", Cancel: true));
            Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
            await WaitForSnapshotAsync(this, current => current.CalibrationSession is
                { Phase: CalibrationSessionPhase.Restored, RestorationVerified: true });
        }

        private static async Task<CameraBindingRevision> AppendCompletedBindingAsync(
            SqliteCommandStore store, AuditIntegrityPolicy audit, SignedIn user,
            CameraBindingTarget target)
        {
            var operationId = Guid.NewGuid();
            var recordedAt = DateTimeOffset.UtcNow;
            var cameraEvent = new CameraSetupEvent(0, Guid.NewGuid(), operationId, Role,
                AuditedCommandKind.RebindCamera, CameraSetupEventPhase.Completed, 1,
                target: target, succeeded: true, reasonCode: "CameraRebindCompleted",
                changeReason: "V124 calibration binding", actorPrincipalId: user.PrincipalId,
                sessionId: user.SessionId, authorAuthorizationRevision: user.AuthorizationRevision,
                recordedAtUtc: recordedAt);
            cameraEvent = cameraEvent with
            {
                RevisionHash = CameraSetupStorageCodec.ComputeRevisionHash(
                    cameraEvent, 1, null, target)
            };
            var grant = Guid.NewGuid();
            var admission = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupActionAuthorized, recordedAt, audit.StationId,
                user.PrincipalId, null, null, null, "CameraRebindAdmitted",
                ActorPrincipalId: user.PrincipalId, CommandCorrelationId: operationId,
                StepUpGrantId: grant, RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: user.AuthorizationRevision,
                ManagementReason: IdentityManagementReason.AccessChange.ToString(),
                ActionTargetId: Role, BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(),
                OperationId: operationId, SessionId: user.SessionId);
            var terminalIdentity = admission with
            {
                EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
                ReasonCode = "CameraRebindCompleted"
            };
            var attempt = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            var outcome = new CommandAuditFact(Guid.NewGuid(), attempt, operationId, epoch,
                recordedAt, AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
                user.PrincipalId.ToString("D"), user.SessionId, grant, CommandAuditPhase.Outcome,
                CommandDisposition.Accepted, "CameraRebindAdmitted", user.PrincipalId.ToString("D"));
            var terminal = outcome with
            {
                EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed,
                Disposition = null, ReasonCode = "CameraRebindCompleted"
            };
            var write = await store.UpdateIdentityAsync(state => new IdentityUpdate(
                new object(), new[] { admission, terminalIdentity },
                new[] { outcome, terminal }, CameraEvents: new[] { cameraEvent }),
                CancellationToken.None);
            Assert.True(write.Committed, write.ReasonCode);
            await WaitForVerifiedAsync(store);
            return (await store.ReadCameraSetupAsync(Role)).State.Binding!;
        }

        private static async Task<ImagingSetupRevision> AppendImagingAsync(
            SqliteCommandStore store, ProductionStoreOptions options,
            LocalAuthorizationService authorization, SignedIn user,
            CameraBindingRevision binding)
        {
            var persistence = new SqliteImagingSetupRevisionPersistence(store);
            var operationId = Guid.NewGuid();
            var definition = new ImagingSetupDefinition("V124-Lens", "Focus-100",
                "Mount-Top", 250, "SensorUp");
            var provisional = new ImagingSetupChangeRequest(operationId, user.Invocation, Role,
                binding.Revision, binding.RevisionHash, 0, null, definition,
                "V124 imaging baseline declaration");
            var stepUp = await authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                user.Invocation, new StepUpBinding(Permission.ManageCameraBindings, operationId,
                    provisional.AuthorizationTarget, AuditedCommandKind.DeclareImagingSetup), Password));
            Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
            var grant = stepUp.GrantId!.Value;
            var changed = new ImagingSetupChangeRequest(operationId,
                user.Invocation with { StepUpGrantId = grant }, Role,
                binding.Revision, binding.RevisionHash, 0, null, definition,
                provisional.ChangeReason);
            var cameraAuthorization = await authorization.AuthorizeCameraSetupAsync(
                changed.Invocation, readOnly: false, operationId,
                changed.AuthorizationTarget, AuditedCommandKind.DeclareImagingSetup);
            Assert.True(cameraAuthorization.Authorized, cameraAuthorization.ReasonCode);
            try
            {
                var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId,
                    Guid.NewGuid(), DateTimeOffset.UtcNow,
                    AuditedCommandKind.DeclareImagingSetup, CommandSource.PhysicalConsole,
                    user.PrincipalId.ToString("D"), user.SessionId, grant,
                    CommandAuditPhase.Outcome, CommandDisposition.Accepted,
                    "ImagingSetupRevisionPersisted", user.PrincipalId.ToString("D"));
                var write = await persistence.AppendAsync(new ImagingSetupPersistenceRequest(
                    fact, changed, cameraAuthorization), new StoreDeadline(options.CommitTimeout));
                Assert.True(write.Committed, write.ReasonCode);
            }
            finally { cameraAuthorization.Reservation?.Dispose(); }
            await WaitForVerifiedAsync(store);
            return (await persistence.ReadAsync(Role)).Current!;
        }

        private static CalibrationSessionPlan CreatePlan(TestProcedure procedure,
            RequestedCameraConfiguration temporary)
        {
            var hash = new string('A', 64);
            var inputContract = new RecipeContractReference("v124-calibration-input", "1", hash);
            var descriptor = procedure.Descriptor;
            return new CalibrationSessionPlan(
                new CalibrationRequirement(Role, CalibrationKind.Intrinsic, "geometry",
                    new RecipeContractReference("v124-coefficients", "1", hash),
                    new RecipeContractReference("v124-acceptance", "1", hash)),
                descriptor, new CalibrationProcedureInputPayload(inputContract, new byte[] { 1 }),
                temporary, new CalibrationEvidenceSelectionPolicy("v124-selection", "1",
                    minimumFrames: 1, minimumFeaturesPerFrame: 2, minimumImageCoverage: 0));
        }

        private static RequestedCameraConfiguration Configuration(double exposure) => new(
            ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
            new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 100, 0, null);

        private static AuthorizationPolicy CreateAuthorizationPolicy()
        {
            var development = AuthorizationPolicy.Development;
            var roles = development.RoleBundles.ToDictionary(item => item.Key,
                item => item.Key == HumanRoleBundle.Administrator
                    ? item.Value.Append(Permission.RunCalibration)
                    : item.Value.AsEnumerable());
            return new AuthorizationPolicy("v124-calibration", "1", roles);
        }

        private static AlarmPolicy CreateAlarmPolicy()
        {
            const AlarmResetPrerequisites prerequisites = AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution | AlarmResetPrerequisites.NoPendingDelivery;
            var rules = new List<AlarmPolicyRule>
            {
                new("StartupRecoveryRequired", "Runtime.StartupRecovery", AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: prerequisites),
                new("CameraDisconnected", "Runtime.CameraRecovery", AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: prerequisites),
                new("CameraRecoveryFailed", "Runtime.CameraRecovery", AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: prerequisites)
            };
            foreach (var code in new[] { "CameraEarlyFrame", "CameraExtraFrame", "CameraLateFrame",
                "CameraCorrelationMismatch", "CameraEarlyHardwarePulse", "CameraDuplicateHardwarePulse",
                "CameraTriggerWhileBusy", "CameraInvalidFrame", "CameraObservationGap" })
                rules.Add(new(code, "Runtime.CameraAcquisition", AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: prerequisites));
            return new AlarmPolicy("v124-calibration", "1", rules, TimeSpan.FromSeconds(10));
        }

        internal static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 15d);
            while (store.Integrity?.State != AuditIntegrityState.Verified)
            {
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new XunitException("Calibration schema-14 audit did not become Verified.");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { await Runtime.DisposeAsync(); }
            catch (InvalidOperationException failure) when
                (failure.Message == "CalibrationShutdownIncomplete") { }
            finally
            {
                try { await Recovery.DisposeAsync(); }
                finally
                {
                    Authorization.Dispose();
                    await Sessions.DisposeAsync();
                    await Store.DisposeAsync();
                    Cleanup(_directory, _audit);
                }
            }
        }

        private static void Cleanup(string directory, AuditIntegrityPolicy audit)
        {
            try
            {
                var key = WindowsMachineAuditKey.GetKeyPath(audit);
                if (File.Exists(key)) File.Delete(key);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                var full = Path.GetFullPath(directory);
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                    "SharpInspect.Runtime.Tests"));
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record SignedIn(Guid PrincipalId, Guid SessionId,
        long AuthorizationRevision)
    {
        internal CommandInvocation Invocation => new(CommandSource.PhysicalConsole,
            PrincipalId.ToString("D"), SessionId);
    }

    private sealed class FixtureConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true,
            "S-1-5-21-V124-CalibrationSession");
    }

    private sealed class TestProcedure : ICalibrationProcedure<byte>
    {
        private readonly bool _blockCompute;
        private readonly bool _blockInputValidation;
        private readonly TaskCompletionSource<bool> _computeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseCompute =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _inputValidationStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseInputValidation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inputValidationBlocked;
        private readonly ICalibrationInputCodec<byte> _codec;

        internal TestProcedure(bool blockCompute, bool blockInputValidation = false)
        {
            _blockCompute = blockCompute;
            _blockInputValidation = blockInputValidation;
            var hash = new string('A', 64);
            var input = new RecipeContractReference("v124-calibration-input", "1", hash);
            _codec = new ByteCodec(this, input);
            Descriptor = new CalibrationProcedureDescriptor(
                new RecipeContractReference("v124-calibration-procedure", "1", hash),
                input, CalibrationKind.Intrinsic);
        }

        internal TaskCompletionSource<bool> ComputeStarted => _computeStarted;
        internal TaskCompletionSource<bool> InputValidationStarted => _inputValidationStarted;
        internal VisionFrame? LastComputeFrame { get; private set; }
        internal bool CancellationObserved { get; private set; }
        internal RecipeContractReference? CoefficientOverride { get; set; }
        public CalibrationProcedureDescriptor Descriptor { get; }
        public ICalibrationInputCodec<byte> InputCodec => _codec;

        public ValueTask<CalibrationExtractionResult> ExtractAsync(
            CalibrationExtractionContext<byte> context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CalibrationExtractionResult(new[]
            {
                new CalibrationImageFeature("top-left", 0, 0),
                new CalibrationImageFeature("bottom-right", 1, 1)
            }));

        public async ValueTask<CalibrationProcedureComputationResult> ComputeAsync(
            CalibrationComputationContext<byte> context,
            CancellationToken cancellationToken = default)
        {
            LastComputeFrame = context.Observations[0].Frame;
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TestProcedure)state!).CancellationObserved = true, this);
            CancellationObserved = cancellationToken.IsCancellationRequested;
            _computeStarted.TrySetResult(true);
            if (_blockCompute)
                await _releaseCompute.Task.ConfigureAwait(false);
            return Result();
        }

        internal void ReleaseCompute() => _releaseCompute.TrySetResult(true);
        internal void ReleaseInputValidation() => _releaseInputValidation.TrySetResult(true);

        private CalibrationProcedureComputationResult Result() =>
            new(new CalibrationCoefficientPayload(
                CoefficientOverride ?? new RecipeContractReference("v124-coefficients", "1", new string('A', 64)),
                new byte[] { 7, 4 }), new[] { new CalibrationQualityMetric("fit", 1, "unit") });

        private sealed class ByteCodec : ICalibrationInputCodec<byte>
        {
            private readonly TestProcedure _owner;

            internal ByteCodec(TestProcedure owner, RecipeContractReference inputContract)
            {
                _owner = owner;
                InputContract = inputContract;
            }
            public RecipeContractReference InputContract { get; }
            public byte Decode(ReadOnlyMemory<byte> canonicalBytes)
            {
                if (_owner._blockInputValidation &&
                    Interlocked.Exchange(ref _owner._inputValidationBlocked, 1) == 0)
                {
                    _owner._inputValidationStarted.TrySetResult(true);
                    _owner._releaseInputValidation.Task.GetAwaiter().GetResult();
                }

                return canonicalBytes.Length == 1 ? canonicalBytes.Span[0] :
                    throw new InvalidOperationException("V124CalibrationInputInvalid");
            }
            public ReadOnlyMemory<byte> Encode(byte input) => new[] { input };
        }
    }

    private sealed class TestProvider : ICameraProvider
    {
        private readonly object _sync = new();
        private TestDevice? _initial;
        private (TaskCompletionSource<bool> Entered, TaskCompletionSource<bool> Release)?
            _nextOpenedHealthBarrier;
        private int _openCount;

        public CameraProviderIdentity Identity { get; } = new("SharpInspect.Virtual", "1",
            "SharpInspect.NET.Cameras.Virtual", "0.1.0-dev.1");
        internal List<TestDevice> Devices { get; } = new();
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal void SetInitial(TestDevice initial) => _initial = initial;
        internal void BlockNextOpenedDeviceHealth(TaskCompletionSource<bool> entered,
            TaskCompletionSource<bool> release)
        {
            ArgumentNullException.ThrowIfNull(entered);
            ArgumentNullException.ThrowIfNull(release);
            lock (_sync) _nextOpenedHealthBarrier = (entered, release);
        }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CameraDiscoveryResult.Success(Array.Empty<CameraDeviceDescriptor>()));

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            if (stableDeviceIdentity != "Virtual:Camera.One")
                return ValueTask.FromResult(CameraOpenResult.Failure("V124UnexpectedStableIdentity"));
            (TaskCompletionSource<bool> Entered, TaskCompletionSource<bool> Release)? barrier;
            lock (_sync)
            {
                barrier = _nextOpenedHealthBarrier;
                _nextOpenedHealthBarrier = null;
            }
            var device = new TestDevice(Identity, stableDeviceIdentity,
                _initial!.Clock, null, armed: false, frameSeed: OpenCount + 2,
                healthEntered: barrier?.Entered, healthRelease: barrier?.Release);
            lock (_sync) Devices.Add(device);
            Interlocked.Increment(ref _openCount);
            return ValueTask.FromResult(CameraOpenResult.Success(device));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestDevice : IControlledCameraDevice
    {
        private readonly object _sync = new();
        private readonly TestClock _clock;
        private readonly Guid _protocolEpoch = Guid.NewGuid();
        private readonly int _frameSeed;
        private readonly TaskCompletionSource<bool>? _healthEntered;
        private readonly TaskCompletionSource<bool>? _healthRelease;
        private CameraHealthSnapshot _health;
        private EffectiveCameraConfiguration? _effectiveConfiguration;
        private int _frameCounter;
        private int _acquireCount;
        private int _healthReadCount;
        private int _healthBarrierUsed;

        internal TestDevice(CameraProviderIdentity provider, string stableIdentity,
            TestClock clock, EffectiveCameraConfiguration? effective,
            bool armed, int frameSeed,
            TaskCompletionSource<bool>? healthEntered = null,
            TaskCompletionSource<bool>? healthRelease = null)
        {
            _clock = clock;
            _frameSeed = frameSeed;
            _healthEntered = healthEntered;
            _healthRelease = healthRelease;
            if (_healthEntered is not null && _healthRelease is null)
                throw new ArgumentException("V124HealthBarrierReleaseRequired", nameof(healthRelease));
            _effectiveConfiguration = effective;
            Descriptor = new CameraDeviceDescriptor(provider, stableIdentity,
                "V124 deterministic camera", "V124-Test-Camera");
            _health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Open,
                armed ? CameraConfigurationState.Applied : CameraConfigurationState.Unconfigured,
                armed ? CameraAcquisitionState.Armed : CameraAcquisitionState.Stopped,
                clock.GetTimePoint());
        }

        internal TestClock Clock => _clock;
        internal int ApplyCount { get; private set; }
        internal int StartCount { get; private set; }
        internal int StopCount { get; private set; }
        internal int AcquireCount => Volatile.Read(ref _acquireCount);
        internal int HealthReadCount => Volatile.Read(ref _healthReadCount);
        internal EffectiveCameraConfiguration? EffectiveConfiguration
        {
            get { lock (_sync) return _effectiveConfiguration; }
        }

        public CameraDeviceDescriptor Descriptor { get; }
        public CameraCapabilities Capabilities { get; } = CreateCapabilities();

        internal static CameraCapabilities CreateCapabilities() => new(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new CameraDoubleCapability(1, 100, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(-10, 10, 1, CameraQuantizationMode.Exact),
            new CameraDoubleCapability(0, 10, 1, CameraQuantizationMode.Exact),
            new CameraRoiCapabilities(2, 2, new(0, 0, 1), new(0, 0, 1),
                new(2, 2, 1), new(2, 2, 1)));

        public CameraHealthSnapshot GetHealthSnapshot()
        {
            Interlocked.Increment(ref _healthReadCount);
            if (_healthEntered is not null &&
                Interlocked.Exchange(ref _healthBarrierUsed, 1) == 0)
            {
                _healthEntered.TrySetResult(true);
                _healthRelease!.Task.GetAwaiter().GetResult();
            }
            lock (_sync) return _health;
        }

        public ValueTask<CameraConfigurationResult> ApplyConfigurationAsync(
            RequestedCameraConfiguration requested,
            CancellationToken cancellationToken = default)
        {
            var result = Capabilities.ValidateConfiguration(requested);
            lock (_sync)
            {
                ApplyCount++;
                if (result.Succeeded)
                {
                    _effectiveConfiguration = result.Effective;
                    _health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                        CameraConnectionState.Open, CameraConfigurationState.Applied,
                        CameraAcquisitionState.Stopped, _clock.GetTimePoint());
                }
            }
            return ValueTask.FromResult(result);
        }

        public ValueTask<CameraOperationResult> StartAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                StartCount++;
                _health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                    CameraConnectionState.Open, CameraConfigurationState.Applied,
                    CameraAcquisitionState.Armed, _clock.GetTimePoint());
            }
            return ValueTask.FromResult(CameraOperationResult.Success("V124CameraStarted"));
        }

        public ValueTask<CameraOperationResult> StopAsync(
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                StopCount++;
                _health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                    CameraConnectionState.Open,
                    _effectiveConfiguration is null ? CameraConfigurationState.Unconfigured :
                        CameraConfigurationState.Applied,
                    CameraAcquisitionState.Stopped, _clock.GetTimePoint());
            }
            return ValueTask.FromResult(CameraOperationResult.Success("V124CameraStopped"));
        }

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(FrameAcquisitionResult.FailureResult(
                new CameraAcquisitionFailure(CameraAcquisitionFailureKind.ProtocolViolation,
                    "V124ControlledPathRequired")));

        public ValueTask<FrameAcquisitionResult> AcquireAsync(FrameAcquisitionRequest request,
            FrameAcquisitionControl control,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCount);
            return new ValueTask<FrameAcquisitionResult>(AcquireControlledAsync(request, control,
                cancellationToken));
        }

        private async Task<FrameAcquisitionResult> AcquireControlledAsync(
            FrameAcquisitionRequest request, FrameAcquisitionControl control,
            CancellationToken cancellationToken)
        {
            if (!control.AcknowledgePending(request))
                return FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                    CameraAcquisitionFailureKind.ProtocolViolation, "V124ControlRejected"));
            FrameAcquisitionStart start;
            try { start = await control.WaitForBusyAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                    CameraAcquisitionFailureKind.Cancelled, "V124CameraAcquireCancelled"));
            }
            if (!control.IsBusy)
                return FrameAcquisitionResult.FailureResult(new CameraAcquisitionFailure(
                    CameraAcquisitionFailureKind.ProtocolViolation, "V124BusyClosed"));

            var now = _clock.GetTimePoint();
            var milestones = new FrameAcquisitionMilestones(_clock.Frequency, start.BusyAt,
                start.BusyAt, now, now);
            var bytes = new byte[]
            {
                (byte)(_frameSeed + Interlocked.Increment(ref _frameCounter)),
                2, 3, 4
            };
            var effective = EffectiveConfiguration ??
                throw new InvalidOperationException("V124EffectiveConfigurationMissing");
            var metadata = new FrameMetadata(request.Correlation, request.LogicalCameraRole,
                2, 2, 2, VisionPixelFormat.Mono8, null, now.HostObservedAtUtc, effective);
            var provenance = new FrameProvenance(request.Correlation, Descriptor.Provider.Id,
                Descriptor.Provider.Version, Descriptor.Provider.AdapterPackageId,
                Descriptor.Provider.AdapterVersion, "V124.TestSdk", "1", null,
                Descriptor.StableDeviceIdentity, Descriptor.ReportedModel, null, "Mono8",
                "V124-test-passthrough", false, false, null,
                (ulong)_frameCounter, milestones);
            var lease = new TestLease(bytes, metadata, provenance);
            return FrameAcquisitionResult.Success(lease);
        }

        public CameraProtocolSnapshot ReadProtocolObservations(long afterSequence,
            int maximumCount = 64) => new(_protocolEpoch, 1, 0, false,
            Array.Empty<CameraProtocolObservation>());

        public ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                _health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                    CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
                    CameraAcquisitionState.Stopped, _clock.GetTimePoint());
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestLease : IFrameBufferLease
    {
        private readonly byte[] _bytes;
        private int _returned;

        internal TestLease(byte[] bytes, FrameMetadata metadata, FrameProvenance provenance)
        {
            _bytes = bytes;
            Frame = new TestFrame(metadata, _bytes, () => Volatile.Read(ref _returned) == 0);
            Provenance = provenance;
            LeaseId = Guid.NewGuid();
        }

        public Guid LeaseId { get; }
        public VisionFrame Frame { get; }
        public FrameProvenance Provenance { get; }
        public bool IsReturned => Volatile.Read(ref _returned) != 0;
        public void Dispose() => Interlocked.Exchange(ref _returned, 1);
    }

    private sealed class TestFrame : VisionFrame
    {
        private readonly byte[] _bytes;
        private readonly Func<bool> _active;

        internal TestFrame(FrameMetadata metadata, byte[] bytes, Func<bool> active)
            : base(metadata)
        {
            _bytes = bytes;
            _active = active;
        }

        public override bool IsLoanActive => _active();
        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            if (!IsLoanActive) throw new ObjectDisposedException(nameof(TestFrame));
            if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
            return _bytes.AsSpan(row * Metadata.ValidRowBytes, Metadata.ValidRowBytes);
        }
    }

    private sealed class TestClock : IFrameAcquisitionClock
    {
        private readonly object _sync = new();
        private readonly List<Scheduled> _scheduled = new();
        private long _now;
        private long _sequence;

        public long Frequency => 1_000;
        public FrameTimePoint GetTimePoint() => new(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(
                Volatile.Read(ref _now)), Volatile.Read(ref _now));

        public IDisposable Schedule(long dueTimestamp, FrameAcquisitionClockPhase phase,
            Action callback)
        {
            lock (_sync)
            {
                var scheduled = new Scheduled(dueTimestamp, phase, ++_sequence, callback);
                _scheduled.Add(scheduled);
                return scheduled;
            }
        }

        private sealed class Scheduled : IDisposable
        {
            internal Scheduled(long due, FrameAcquisitionClockPhase phase,
                long sequence, Action callback)
            { Due = due; Phase = phase; Sequence = sequence; Callback = callback; }
            internal long Due { get; }
            internal FrameAcquisitionClockPhase Phase { get; }
            internal long Sequence { get; }
            internal Action Callback { get; }
            public void Dispose() { }
        }
    }
}

#pragma warning restore CA1416
