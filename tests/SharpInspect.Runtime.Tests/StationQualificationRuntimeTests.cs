using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Real SQLite/runtime coverage for the non-production station-qualification
/// boundary.  The first provider creates the real draft/release/PLC/activation
/// baseline; the second provider is a cold composition over the same database
/// with the schema-23 qualification facility explicitly registered.
/// </summary>
public sealed partial class ManualInspectionRuntimeTests
{
    private const string QualificationFaultAbortCode = "V137.Qualification.FaultAbort";
    private const string QualificationFaultAbortSource = "Runtime.V137.Qualification";

    [Fact]
    public async Task V137_R01_StartNeedsFreshStepUpAndDoesNotOpenFacility()
    {
        await using var harness = await QualificationHarness.CreateAsync();

        var command = harness.StartCommand();
        var rejected = await harness.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Contains(rejected.ReasonCode, new[] { "StepUpRequired", "StationQualificationStepUpRequired" });
        Assert.Equal(0, harness.Facility.OpenCount);
        Assert.Equal(0, harness.Facility.StimulusCount);
    }

    [Fact]
    public async Task V137_R02_MissingPermissionIsPersistedWithoutFacilityIo()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            allowQualification: false);

        var rejected = await harness.Runtime.SubmitAsync(harness.StartCommand());

        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Equal("PermissionDenied", rejected.ReasonCode);
        Assert.Equal(0, harness.Facility.OpenCount);
        Assert.Equal(0, harness.Facility.WaitCount);
        Assert.Equal(0, harness.Facility.StimulusCount);
    }

    [Fact]
    public async Task V137_R03_IncompleteIsolationObservationFailsBeforeStimulus()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            incompleteIsolationObservation: true);

        var accepted = await harness.StartWithFreshStepUpAsync();
        AssertAccepted(accepted, "station qualification start");

        var terminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "incomplete station qualification did not terminate");

        Assert.Equal(StationQualificationSessionPhase.Closed, terminal.Phase);
        Assert.False(terminal.RecoveryRequired);
        Assert.Equal(0, harness.Facility.StimulusCount);
        Assert.Equal(0, harness.Facility.WriteCount);
        Assert.Contains("Destination", terminal.ReasonCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V137_R04_RealRunWritesTypedPayloadAndRestoresWithoutProductionState()
    {
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1);

        var accepted = await harness.StartWithFreshStepUpAsync();
        AssertAccepted(accepted, "station qualification start");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "station qualification run did not restore");

        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        Assert.False(closed.Ready);
        Assert.False(closed.ProductionAuthority);
        Assert.False(closed.CanIssueQualification);
        Assert.True(harness.Facility.OpenCount >= 1);
        Assert.True(harness.Facility.IsolationCount >= 1);
        Assert.True(harness.Facility.RestoreCount >= 1);
        Assert.True(harness.Facility.ReleaseIsolationCount >= 1);
        Assert.True(harness.Facility.DisposeCount >= 1);
        Assert.Equal(1, harness.Facility.WriteCount);

        var page = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: closed.SessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var run = Assert.Single(page.Runs, value => value.SessionId == closed.SessionId);
        Assert.True(run.Terminal);
        Assert.True(run.ExecutionStatus == ExecutionStatus.Success, closed.ReasonCode + ":" + run.ReasonCode);
        Assert.NotNull(run.FrameMetadata);
        Assert.NotNull(run.FrameProvenance);
        Assert.NotNull(run.QualificationPayload);
        Assert.Equal(run.SessionId, run.QualificationPayload!.SessionId);
        Assert.Equal(run.RunId.Value, run.QualificationPayload.RunId.Value);
        Assert.Equal(harness.Plan.QualificationContextHash, run.QualificationPayload.ContextHash);

        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Equal(harness.ActiveRecipe, station.ActiveRecipe);
        Assert.Equal(ExclusiveMode.None, station.Mode);
    }

    [Fact]
    public async Task V137_R05_AbortDuringStimulusWaitRestoresController()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);

        var accepted = await harness.StartWithFreshStepUpAsync();
        AssertAccepted(accepted, "station qualification start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not reach stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        var exit = await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, abort: true));
        AssertAccepted(exit, "station qualification abort");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "station qualification abort did not restore");

        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        Assert.False(closed.RecoveryRequired);
        Assert.Equal(0, harness.Facility.WriteCount);
        Assert.True(harness.Facility.RestoreCount >= 1);
        Assert.True(harness.Facility.ReleaseIsolationCount >= 1);
    }

    [Fact]
    public async Task V137_R06_RestoreFailureRetainsRecoveryFence()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            blockStimulus: true, restoreFailure: true, allowDisposeFailure: true);

        var accepted = await harness.StartWithFreshStepUpAsync();
        AssertAccepted(accepted, "station qualification start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not reach stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        AssertAccepted(await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, false)),
            "station qualification exit");

        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification restore failure did not retain fence");
        Assert.Equal(StationQualificationRestorationState.RecoveryBlocked, blocked.Restoration);
        Assert.True(blocked.RecoveryRequired);
        Assert.False(blocked.Ready);

        var opens = harness.Facility.OpenCount;
        var retry = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, retry.Disposition);
        Assert.Contains(retry.ReasonCode, new[] {
            "StationQualificationRecoveryRequired", "StationQualificationSessionInProgress"
        });
        Assert.Equal(opens, harness.Facility.OpenCount);
    }

    [Fact]
    public async Task V137_R07_SecondQualificationCannotOverlapAcceptedSession()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "station qualification start");
        var active = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not become active");
        var before = harness.Facility.OpenCount;

        var duplicate = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, duplicate.Disposition);
        Assert.Contains(duplicate.ReasonCode, new[] {
            "StationQualificationSessionInProgress", "StationQualificationOwnerConflict"
        });
        Assert.Equal(before, harness.Facility.OpenCount);

        AssertAccepted(await harness.Runtime.SubmitAsync(
            harness.ExitCommand(Assert.IsType<Guid>(active.SessionId), abort: true)),
            "station qualification cleanup abort");
        await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification cleanup did not finish");
    }

    [Fact]
    public async Task V137_R08_FacilityLossRequestsExitAndRestoresTarget()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification facility-loss start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not reach facility-loss stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        harness.Facility.SignalLost();
        var terminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification facility loss did not terminate");

        Assert.Contains("FacilityLost", terminal.ReasonCode, StringComparison.OrdinalIgnoreCase);
        Assert.True(harness.Facility.RestoreCount >= 1);
        Assert.True(harness.Facility.ReleaseIsolationCount >= 1);
        if (terminal.Phase == StationQualificationSessionPhase.Closed)
        {
            Assert.Equal(StationQualificationRestorationState.Restored, terminal.Restoration);
            Assert.False(terminal.RecoveryRequired);
        }
    }

    [Fact]
    public async Task V137_R09_LocalStopCancelsPendingStimulusAndRestoresBeforeDisarm()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification local-stop start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not reach local-stop stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(
            Guid.NewGuid(), harness.Invocation()));
        AssertAccepted(stop, "station qualification local stop");

        var terminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification local stop did not restore");
        Assert.True(harness.Facility.RestoreCount >= 1);
        Assert.Equal(0, harness.Facility.WriteCount);
        Assert.Equal(0, harness.Facility.StimulusCount);
        if (terminal.Phase == StationQualificationSessionPhase.Closed)
            Assert.Equal(StationQualificationRestorationState.Restored, terminal.Restoration);
    }

    [Fact]
    public async Task V137_R10_AbortAfterCoreCommitPreservesCommittedPayload()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification terminal-cancel start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Running &&
            snapshot.CurrentRunId is not null,
            "station qualification did not admit a run before terminal cancel");
        var sessionId = Assert.IsType<Guid>(running.SessionId);

        AssertAccepted(await harness.Runtime.SubmitAsync(
            harness.ExitCommand(sessionId, abort: true)),
            "station qualification terminal-cancel abort");
        await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId && snapshot.ExitRequested,
            "station qualification abort was not projected before physical publication release");
        harness.Facility.ReleaseWrite();

        var terminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification terminal cancel did not restore");
        Assert.Equal(StationQualificationSessionPhase.Closed, terminal.Phase);

        var page = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var run = Assert.Single(page.Runs, value => value.SessionId == sessionId);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, run.Decision);
        Assert.NotNull(run.ResultPayloadJson);
        Assert.NotNull(run.ResultPayloadHash);
        Assert.NotNull(run.QualificationPayload);
    }

    [Fact]
    public async Task V137_R11_ColdRuntimeRecoversPendingSessionAndSealsHistory()
    {
        // Take an online SQLite snapshot while the first runtime is waiting.
        // A new store, authority, runtime and provider recover that committed
        // image. This is a cold-composition test, not an OS process-kill test.
        await using var harness = await QualificationHarness.CreateAsync(
            blockStimulus: true, allowDisposeFailure: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification cold-restart start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not persist a pending session");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        await using var cold = await harness.OpenColdScopeAsync();
        var recovered = await cold.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "cold runtime did not finish pending-session recovery");
        Assert.Equal(StationQualificationSessionPhase.Closed, recovered.Phase);
        Assert.Equal(StationQualificationRestorationState.Restored, recovered.Restoration);
        Assert.False(recovered.RecoveryRequired);
        Assert.True(cold.Facility.OpenCount >= 1);
        Assert.True(cold.Facility.RestoreCount >= 1);
        Assert.True(cold.Facility.ReleaseIsolationCount >= 1);

        var history = await cold.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        var events = history.Events.Where(value => value.SessionId == sessionId)
            .OrderBy(value => value.Position).ToArray();
        Assert.NotEmpty(events);
        Assert.Contains(events, value => value.Phase == StationQualificationSessionPhase.Restoring);
        Assert.Equal(StationQualificationSessionPhase.Closed, events[^1].Phase);
        Assert.True(events[^1].Terminal);

        // The original composition remains on its own database and retires
        // separately; it cannot mutate the recovered snapshot's history.
        harness.Facility.SignalLost();
        var oldTerminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "old runtime did not retire after cold recovery");
        Assert.Contains("FacilityLost", oldTerminal.ReasonCode,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V137_R12_GracefulExitAfterRunAdmissionPreservesSuccess()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification graceful-terminal start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Running &&
            snapshot.CurrentRunId is not null,
            "station qualification did not admit a run before graceful exit");
        var sessionId = Assert.IsType<Guid>(running.SessionId);

        AssertAccepted(await harness.Runtime.SubmitAsync(
            harness.ExitCommand(sessionId, abort: false)),
            "station qualification graceful exit");
        await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId && snapshot.ExitRequested,
            "station qualification graceful exit was not projected before physical publication release");
        harness.Facility.ReleaseWrite();

        var terminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "station qualification graceful exit did not restore");
        Assert.Equal(StationQualificationRestorationState.Restored, terminal.Restoration);

        var page = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var run = Assert.Single(page.Runs, value => value.SessionId == sessionId);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, run.Decision);
        Assert.NotNull(run.ResultPayloadJson);
        Assert.NotNull(run.QualificationPayload);
    }

    [Fact]
    public async Task V137_R13_RecoveryBlockedColdRuntimeRetriesWithNewLeaseWithoutChangingFailure()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            blockStimulus: true, restoreFailure: true, allowDisposeFailure: true);

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification blocked-recovery start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification did not reach blocked-recovery stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        AssertAccepted(await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, abort: false)),
            "station qualification blocked-recovery exit");

        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification did not persist RecoveryBlocked");
        Assert.True(blocked.RecoveryRequired);
        Assert.Equal(StationQualificationRestorationState.RecoveryBlocked, blocked.Restoration);

        var before = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(before.Available, before.ReasonCode);
        var oldFailure = before.Events.Last(value =>
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked);
        Assert.False(oldFailure.Terminal);
        var oldStimulusCount = harness.Facility.StimulusCount;

        await using var cold = await harness.OpenColdScopeAsync();
        var recovered = await cold.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "cold runtime did not recover a previously blocked qualification");
        Assert.Equal(StationQualificationRestorationState.Restored, recovered.Restoration);
        Assert.False(recovered.RecoveryRequired);
        Assert.Equal(0, cold.Facility.StimulusCount);
        Assert.True(cold.Facility.OpenCount >= 1);
        Assert.True(cold.Facility.RestoreCount >= 1);

        var after = await cold.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(after.Available, after.ReasonCode);
        var retainedFailure = Assert.Single(after.Events,
            value => value.Position == oldFailure.Position);
        Assert.Equal(oldFailure.ContentHash, retainedFailure.ContentHash);
        Assert.Equal(oldFailure.ReasonCode, retainedFailure.ReasonCode);
        Assert.Contains(after.Events, value => value.Phase == StationQualificationSessionPhase.Restoring &&
            value.RecoveryAttempt is not null &&
            value.RecoveryAttempt.ContentHash != oldFailure.RecoveryAttempt?.ContentHash);
        Assert.Equal(StationQualificationSessionPhase.Closed, after.Events[^1].Phase);
        Assert.True(after.Events[^1].Terminal);
        Assert.Equal(oldStimulusCount, harness.Facility.StimulusCount);
    }

    [Fact]
    public async Task V137_R14_LocalStopAfterCoreCommitPreservesCommittedPayload()
    {
        // The physical publication barrier is after SQLite Core COMMIT.
        // Stop retires the facility but cannot rewrite that durable result.
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification local-stop terminal start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Running &&
            snapshot.CurrentRunId is not null,
            "station qualification did not admit the local-stop terminal run");
        var sessionId = Assert.IsType<Guid>(running.SessionId);

        AssertAccepted(await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(
            Guid.NewGuid(), harness.Invocation())), "station qualification local stop during write");
        harness.Facility.ReleaseWrite();

        var page = await harness.WaitForHistoryAsync(sessionId,
            value => value.Events.LastOrDefault() is
                { Phase: StationQualificationSessionPhase.Closed, Terminal: true } &&
                value.Runs.Any(run => run.SessionId == sessionId && run.Terminal),
            "local stop did not finalize the blocked qualification write");
        var run = Assert.Single(page.Runs, value => value.SessionId == sessionId);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, run.Decision);
        Assert.NotNull(run.ResultPayloadJson);
        Assert.NotNull(run.ResultPayloadHash);
        Assert.NotNull(run.QualificationPayload);
    }

    [Fact]
    public async Task V137_R15_LogoutAfterCoreCommitPreservesCommittedPayload()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification logout terminal start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Running &&
            snapshot.CurrentRunId is not null,
            "station qualification did not admit the logout terminal run");
        var sessionId = Assert.IsType<Guid>(running.SessionId);
        var committedPage = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(committedPage.Available, committedPage.ReasonCode);
        var committedHash = Assert.Single(committedPage.Runs).ContentHash;

        var logout = await harness.LogoutAsync();
        Assert.True(logout.Succeeded, logout.ReasonCode);
        Assert.Equal("SessionLoggedOut", logout.ReasonCode);
        // Logout revokes authority synchronously even if its separate audit
        // attempt is unavailable; the session audit tests cover that contract.
        Assert.Equal(InteractiveSessionState.Unauthenticated, harness.Fixture.Sessions.Current.State);
        Assert.Null(harness.Fixture.Sessions.Current.SessionId);
        harness.Facility.ReleaseWrite();

        var page = await harness.WaitForHistoryAsync(sessionId,
            value => value.Events.LastOrDefault() is
                { Phase: StationQualificationSessionPhase.Closed, Terminal: true } &&
                value.Runs.Any(run => run.SessionId == sessionId && run.Terminal),
            "logout did not finalize the blocked qualification write");
        var run = Assert.Single(page.Runs, value => value.SessionId == sessionId);
        Assert.Equal(committedHash, run.ContentHash);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, run.Decision);
        Assert.NotNull(run.ResultPayloadJson);
        Assert.NotNull(run.QualificationPayload);
    }

    [Fact]
    public async Task V137_R16_FaultAbortAfterCoreCommitPreservesCommittedPayload()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification fault-abort terminal start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Running &&
            snapshot.CurrentRunId is not null,
            "station qualification did not admit the fault-abort terminal run");
        var sessionId = Assert.IsType<Guid>(running.SessionId);

        var alarm = await harness.RaiseFaultAbortAsync();
        Assert.True(alarm.Accepted, alarm.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, alarm.Audit);
        harness.Facility.ReleaseWrite();

        var page = await harness.WaitForHistoryAsync(sessionId,
            value => value.Events.LastOrDefault() is
                { Phase: StationQualificationSessionPhase.Closed, Terminal: true } &&
                value.Runs.Any(run => run.SessionId == sessionId && run.Terminal),
            "fault abort did not finalize the blocked qualification write");
        var run = Assert.Single(page.Runs, value => value.SessionId == sessionId);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, run.Decision);
        Assert.NotNull(run.ResultPayloadJson);
        Assert.NotNull(run.QualificationPayload);
    }

    private static AlarmPolicy CreateQualificationAlarmPolicy()
    {
        return new AlarmPolicy("V137.Qualification.Alarm", "1",
            new[]
            {
                new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                    AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoPendingDelivery),
                new AlarmPolicyRule("PreviewRecoveryRequired", "Runtime.Preview",
                    AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution),
                new AlarmPolicyRule("AlgorithmHung", "Runtime.AlgorithmExecution",
                    AlarmSeverity.Critical, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 110),
                new AlarmPolicyRule("FrameBufferExhausted", "Runtime.FrameBufferPool",
                    AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null, 200,
                    AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution),
                new AlarmPolicyRule("ManualInspectionRecoveryRequired",
                    "Runtime.ManualInspection", AlarmSeverity.Warning,
                    ProductionImpact.BlockNewTriggers, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution),
                new AlarmPolicyRule(QualificationFaultAbortCode, QualificationFaultAbortSource,
                    AlarmSeverity.Critical, ProductionImpact.FaultAbort, true,
                    AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution),
                new AlarmPolicyRule(QualificationCycleAlarmCodes.TracePersistenceFailed,
                    QualificationCycleAlarmCodes.Source, AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution |
                        AlarmResetPrerequisites.NoPendingDelivery),
                new AlarmPolicyRule(QualificationCycleAlarmCodes.ResultAckTimeout,
                    QualificationCycleAlarmCodes.Source, AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution |
                        AlarmResetPrerequisites.NoPendingDelivery),
                new AlarmPolicyRule(QualificationCycleAlarmCodes.TriggerRejected,
                    QualificationCycleAlarmCodes.Source, AlarmSeverity.Warning,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution |
                        AlarmResetPrerequisites.NoPendingDelivery),
                new AlarmPolicyRule(QualificationCycleAlarmCodes.Interrupted,
                    QualificationCycleAlarmCodes.Source, AlarmSeverity.Error,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null,
                    ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                        AlarmResetPrerequisites.NoActiveExecution |
                        AlarmResetPrerequisites.NoPendingDelivery)
            }, TimeSpan.FromMinutes(1));
    }

    private static RecipeDraftContent CreateQualificationContent(
        ProductionStoreOptions options, bool activationReadyDraft)
    {
        var schema = new AlgorithmConfigurationSchema("V135.Manual.Config", "1",
            Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(schema,
            Array.Empty<AlgorithmConfigurationEntry>());
        var result = new AlgorithmResultSchema("V135.Manual.Result", "1",
            new[]
            {
                new AlgorithmFieldDefinition("Score", AlgorithmScalarType.Float64,
                    "ratio", true, new AlgorithmScalarConstraints(minFloat64: 0,
                        maxFloat64: 100))
            },
            new[] { "ManualSyntheticUnknown" },
            new OverlayContract("V135.Manual.Overlay", "1", 1, 4, 4, 0));
        var descriptor = new AlgorithmDescriptor(
            new AlgorithmIdentity("V135.Manual.Algorithm", "1"), schema, result);
        var binding = RecipeAlgorithmBinding.FromDescriptor(descriptor);
        var camera = new RequestedCameraConfiguration(
            ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
            new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null,
            250, 0, null);
        var execution = options.RecipeDrafts!.ExecutionPolicy;
        return new RecipeDraftContent(RecipeKey, "V135 Manual Runtime Draft", binding,
            configuration, LogicalRole, camera, TimeSpan.FromSeconds(2),
            Array.Empty<RecipeAssetRequirement>(), new[]
            {
                new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                    new RecipeContractReference(execution.Id, execution.Version,
                        execution.ContentHash))
            }, partIdentityRequirement: activationReadyDraft ? PartIdentityRequirement.None : null);
    }

    private static VirtualCameraProvider CreateQualificationProvider(
        VirtualCameraClock clock, bool timeout)
    {
        var capabilities = new CameraCapabilities(
            new[] { ProductionAcquisitionMode.SoftwareTrigger },
            new[] { VisionPixelFormat.Mono8 }, Array.Empty<int>(),
            new(10, 10_000, 1, CameraQuantizationMode.Exact),
            new(0, 24, 1, CameraQuantizationMode.Exact),
            new(0, 10_000, 1, CameraQuantizationMode.Exact),
            new(128, 96, new(0, 127, 1), new(0, 95, 1),
                new(1, 128, 1), new(1, 96, 1)));
        var image = VirtualCameraImage.CreateSynthetic("manual-frame", 16, 12,
            VisionPixelFormat.Mono8, null, Seed);
        var acquisitions = Enumerable.Range(0, 3).Select(_ =>
            new VirtualCameraAcquisitionPlan(timeout
                ? Array.Empty<VirtualCameraSignal>()
                : new[] { new VirtualCameraSignal(TimeSpan.FromMilliseconds(100),
                    VirtualCameraSignalKind.Frame, image.Id) })).ToArray();
        var configurations = Enumerable.Repeat(
            new VirtualCameraConfigurationPlan(VirtualCameraConfigurationOutcome.Success,
                TimeSpan.Zero), 32).ToArray();
        var scenario = new VirtualCameraScenario("V135.Manual", "1", Seed,
            StableDeviceIdentity, capabilities, new[] { image }, acquisitions,
            configurations);
        return new VirtualCameraProvider(new[] { scenario }, clock, poolCapacity: 2);
    }

    private sealed class QualificationHarness : IAsyncDisposable
    {
        private readonly QualificationFixture _fixture;
        private readonly ServiceProvider _services;
        private readonly ClockPump _clockPump;
        private readonly bool _allowDisposeFailure;
        private bool _disposed;

        private QualificationHarness(QualificationFixture fixture, ServiceProvider services,
            ClockPump clockPump, ControlledQualificationFacility facility,
            VirtualCameraProvider cameraProvider,
            StationQualificationPlan plan, RecipeDraftRevision draft,
            RecipeReference? activeRecipe,
            bool allowDisposeFailure)
        {
            _fixture = fixture;
            _services = services;
            _clockPump = clockPump;
            _allowDisposeFailure = allowDisposeFailure;
            Facility = facility;
            CameraProvider = cameraProvider;
            Plan = plan;
            Draft = draft;
            ActiveRecipe = activeRecipe;
            Runtime = services.GetRequiredService<IStationRuntime>();
            Qualification = services.GetRequiredService<IStationQualificationSessionService>();
            History = services.GetRequiredService<IStationQualificationHistoryQuery>();
        }

        internal ControlledQualificationFacility Facility { get; }
        internal VirtualCameraProvider CameraProvider { get; }
        internal ManualFactory Factory => (ManualFactory)_services.GetRequiredService<IVisionAlgorithmFactory>();
        internal StationQualificationPlan Plan { get; }
        internal ModbusQualificationProfile? ModbusProfile => _fixture.ModbusProfile;
        internal RecipeDraftRevision Draft { get; }
        internal RecipeReference? ActiveRecipe { get; }
        internal IStationRuntime Runtime { get; }
        internal IStationQualificationSessionService Qualification { get; }
        internal IStationQualificationHistoryQuery History { get; }
        internal QualificationFixture Fixture => _fixture;

        internal CommandInvocation Invocation(Guid? grant = null) => new(
            CommandSource.PhysicalConsole, _fixture.Sessions.Current.PrincipalId,
            _fixture.Sessions.Current.SessionId, grant);

        internal StartStationQualificationSessionCommand StartCommand() =>
            new(Guid.NewGuid(), Invocation(), Plan, "V137 station qualification start");

        internal ExitStationQualificationSessionCommand ExitCommand(Guid sessionId, bool abort) =>
            new(Guid.NewGuid(), Invocation(), sessionId, abort,
                abort ? "V137 station qualification abort" : "V137 station qualification exit");

        internal async Task<RuntimeCommandOutcome> StartWithFreshStepUpAsync()
        {
            var command = StartCommand();
            var grant = await GrantAsync(_fixture.Authorization, _fixture.Password,
                Invocation(), Permission.RunStationQualification, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.StartStationQualificationSession);
            return await Runtime.SubmitAsync(command with
            {
                Invocation = Invocation(grant.GrantId)
            });
        }

        internal async Task<RuntimeCommandOutcome> StartCommandWithFreshStepUpAsync()
        {
            return await StartWithFreshStepUpAsync();
        }

        internal async Task<SessionActionResult> LogoutAsync() =>
            await _fixture.Sessions.LogoutAsync(_fixture.Sessions.Current.SessionId);

        internal async Task<RecipeActivationResult> ActivateTargetAgainAsync()
        {
            var target = await new SqliteRecipeActivationQuery(_fixture.Options).ReadAsync(Plan.TargetActivation);
            Assert.True(target.Available, target.ReasonCode);
            return await ActivateAsync(_fixture, _services,
                new ReleasedRecipe(Assert.IsType<RecipeActivationRecord>(target.Record).SuccessfulSnapshot!.Release),
                Plan.TargetActivation);
        }

        internal async Task<AlarmObservationOutcome> RaiseFaultAbortAsync()
        {
            if (Runtime is not StationRuntime station)
                throw new XunitException("V137 fault-abort test did not use StationRuntime");
            station.RegisterAlarmSource(QualificationFaultAbortSource);
            var snapshot = await Runtime.GetSnapshotAsync();
            var observation = new AlarmObservation(snapshot.RuntimeEpoch,
                station.NextAlarmObservationSequence(QualificationFaultAbortCode),
                QualificationFaultAbortCode, QualificationFaultAbortSource, SourceHealthy: false,
                DateTimeOffset.UtcNow);
            return await station.ObserveAlarmAsync(observation);
        }

        internal async Task<StationQualificationSessionSnapshot> WaitForSnapshotAsync(
            Func<StationQualificationSessionSnapshot, bool> predicate, string timeoutReason)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            StationQualificationSessionSnapshot? last = null;
            string? lastUnavailable = null;
            while (DateTime.UtcNow < deadline)
            {
                var read = await Qualification.GetSnapshotAsync(Invocation());
                if (!read.Available && read.ReasonCode == "AuthorizationLeaseBusy")
                {
                    lastUnavailable = read.ReasonCode;
                    await Task.Delay(25);
                    continue;
                }
                if (!read.Available)
                {
                    var integrity = await new SqliteAuditIntegrityQuery(_fixture.Options)
                        .VerifyAsync(new AuditVerificationRequest(0, 200));
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                        { DataSource = _fixture.Options.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Position,Payload FROM station_qualification_events ORDER BY Position";
                    using var rows = command.ExecuteReader();
                    while (rows.Read())
                    {
                        try { StationQualificationStorageCodec.Decode(Convert.FromBase64String(rows.GetString(1))); }
                        catch (Exception exception) { throw new XunitException($"Qualification row {rows.GetInt64(0)}: {exception}"); }
                    }
                    throw new XunitException(read.ReasonCode + ":" + integrity.ReasonCode);
                }
                lastUnavailable = null;
                last = Assert.IsType<StationQualificationSessionSnapshot>(read.Snapshot);
                if (predicate(last)) return last;
                await Task.Delay(25);
            }
            throw new XunitException(timeoutReason + ":" + last?.Phase + "/" + (lastUnavailable ?? last?.ReasonCode));
        }

        internal async Task<StationQualificationHistoryPage> WaitForHistoryAsync(Guid sessionId,
            Func<StationQualificationHistoryPage, bool> predicate, string timeoutReason)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            StationQualificationHistoryPage? last = null;
            while (DateTime.UtcNow < deadline)
            {
                last = await History.QueryAsync(new StationQualificationHistoryFilter(
                    SessionId: sessionId, PageSize: 128));
                Assert.True(last.Available, last.ReasonCode);
                if (predicate(last)) return last;
                await Task.Delay(25);
            }
            throw new XunitException(timeoutReason + ":" + last?.ReasonCode);
        }

        internal async Task<ColdQualificationScope> OpenColdScopeAsync()
        {
            var options = _fixture.CreateColdSnapshotOptions();
            var store = new SqliteCommandStore(options);
            ServiceProvider? services = null;
            ClockPump? pump = null;
            try
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);

                var identity = new LocalIdentityService(store, options.LocalIdentity!,
                    new RecipeDraftStorageTests.Fixture.FixtureConsole());
                var sessions = new InteractiveSessionService(identity,
                    options.LocalIdentity!.AuthenticationPolicy, identity.PersistSessionEventAsync);
                var login = await sessions.SignInAsync(new PasswordSignInRequest(
                    QualificationFixture.UserName, QualificationFixture.PasswordText));
                Assert.True(login.Succeeded, login.ReasonCode);
                var authorization = new LocalAuthorizationService(store, options.LocalIdentity!,
                    identity, sessions);
                var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1,
                    0, 0, 0, TimeSpan.Zero));
                var provider = CreateQualificationProvider(clock, timeout: false);
                var factory = new ManualFactory();
                var facility = new ControlledQualificationFacility(Plan,
                    incompleteIsolationObservation: false, blockStimulus: false,
                    restoreFailure: false, blockWrite: false);
                services = BuildServices(options, store, identity, sessions, authorization,
                    factory, provider, clock, Plan, facility, maximumRuns: 1,
                    modbusProfile: _fixture.ModbusProfile);
                var runtime = services.GetRequiredService<IStationRuntime>();
                if (runtime is not StationRuntime station)
                    throw new XunitException("V137 cold composition did not use StationRuntime");
                await station.WaitForRecipeActivationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                await station.WaitForStationQualificationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                pump = ClockPump.Start(clock);
                return new ColdQualificationScope(services, pump, store, authorization, facility,
                    services.GetRequiredService<IStationQualificationSessionService>(),
                    services.GetRequiredService<IStationQualificationHistoryQuery>(),
                    sessions);
            }
            catch
            {
                if (pump is not null) await pump.DisposeAsync();
                if (services is not null) await services.DisposeAsync();
                else await store.DisposeAsync();
                throw;
            }
        }

        internal static async Task<QualificationHarness> CreateAsync(
            bool allowQualification = true, bool incompleteIsolationObservation = false,
            bool blockStimulus = false, bool restoreFailure = false, bool blockWrite = false,
            int maximumRuns = 1,
            bool allowDisposeFailure = false, StationQualificationStoreOptions? ledgerOptions = null,
            bool developmentFacility = true, RecipeTransferStoreOptions? recipeTransfers = null,
            int? maximumAuditEntries = null,
            Func<TraceStoragePolicySnapshot, ModbusQualificationProfile>? profileFactory = null,
            bool enableQualificationCycles = false,
            ModbusAlgorithmOutcomeMode? modbusAlgorithmOutcome = null,
            QualificationCycleStoreOptions? qualificationCycleOptions = null,
            IVisionAlgorithmFactory? qualificationExecutionFactory = null)
        {
            enableQualificationCycles |= profileFactory is not null ||
                qualificationCycleOptions is not null;
            var fixture = await QualificationFixture.CreateAsync(allowQualification, ledgerOptions,
                recipeTransfers, maximumAuditEntries, profileFactory, enableQualificationCycles,
                qualificationCycleOptions);
            ServiceProvider? initialServices = null;
            ClockPump? initialPump = null;
            ServiceProvider? services = null;
            ClockPump? pump = null;
            try
            {
                var initialClock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1,
                    0, 0, 0, TimeSpan.Zero));
                var initialProvider = CreateQualificationProvider(initialClock, timeout: false);
                var initialFactory = new ManualFactory();
                initialServices = BuildServices(fixture.BaseOptions, fixture.Store,
                    fixture.Identity, fixture.Sessions, fixture.Authorization, initialFactory,
                    initialProvider, initialClock, plan: null, facility: null,
                    heartbeatInterval: TimeSpan.FromSeconds(30));
                initialPump = ClockPump.Start(initialClock);
                var initialRuntime = initialServices.GetRequiredService<IStationRuntime>();
                if (initialRuntime is not StationRuntime initialStation)
                    throw new XunitException("V137 initial composition did not use StationRuntime");
                await initialStation.WaitForRecipeActivationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                await initialStation.WaitForStationQualificationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));

                var draftService = initialServices.GetRequiredService<RecipeDraftService>();
                var draftId = Guid.NewGuid();
                var content = CreateQualificationContent(fixture.BaseOptions, activationReadyDraft: true);
                var saved = await draftService.SaveAsync(new RecipeDraftSaveRequest(
                    Guid.NewGuid(), draftId, 0, null, content,
                    "V137 create station qualification draft", fixture.Invocation()));
                Assert.True(saved.Saved, saved.ReasonCode);
                var draft = Assert.IsType<RecipeDraftRevision>(saved.Revision);

                await ConfigureQualificationCameraAsync(fixture, initialRuntime,
                    initialProvider.Identity);
                var released = await ReleaseDraftAsync(fixture, initialServices, draft);
                await BindContractAsync(fixture, initialRuntime, initialFactory);
                // All preparation prerequisites are established. Stop the
                // fixture's sample producer before the activation writer takes
                // its non-blocking station lock, avoiding synthetic contention.
                await initialPump.DisposeAsync();
                initialPump = null;
                var activation = await ActivateAsync(fixture, initialServices, released);
                AssertAccepted(activation.Outcome, "activate qualification target; " +
                    $"state={activation.Record?.Outcome.State}; " +
                    $"admission={activation.Record?.AdmissionReference?.Position}; " +
                    $"restoration={activation.Record?.Restoration.State}/" +
                    $"{activation.Record?.Restoration.ReasonCode}");
                var activationQuery = new SqliteRecipeActivationQuery(fixture.BaseOptions);
                var baseline = Assert.IsType<RecipeActivationRecord>(activation.Record);
                var exact = await activationQuery.ReadAsync(baseline.Reference);
                Assert.True(exact.Available, exact.ReasonCode);
                Assert.Equal(baseline.ContentHash, exact.Record?.ContentHash);
                Assert.Equal(RecipeActivationOutcomeState.Succeeded, baseline.Outcome.State);
                Assert.NotNull(baseline.SuccessfulSnapshot);

                await initialServices.DisposeAsync();
                initialServices = null;

                await fixture.ReopenWithStationQualificationAsync();
                var clock = new VirtualCameraClock(new DateTimeOffset(2026, 1, 1,
                    0, 0, 0, TimeSpan.Zero));
                var provider = CreateQualificationProvider(clock, timeout: false);
                var factory = new ManualFactory();
                // Freeze one profile reference for both plan construction and
                // registration. The Modbus adapter binds the exact profile hash
                // and endpoint hash; recreating either value independently can
                // admit a plan for a different TCP endpoint.
                var modbusProfile = fixture.ModbusProfile;
                var plan = CreatePlan(baseline, developmentFacility, modbusProfile);
                if (modbusProfile is not null)
                {
                    Assert.Equal(modbusProfile.ContentHash, plan.ProfileHash);
                    Assert.Equal(modbusProfile.EndpointBindingHash,
                        plan.TransientControllerConfiguration.EndpointBindingHash);
                    Assert.Contains(modbusProfile.ScenarioId, plan.ScenarioIds);
                }
                var facility = new ControlledQualificationFacility(plan,
                    incompleteIsolationObservation, blockStimulus, restoreFailure, blockWrite);
                IVisionAlgorithmFactory? executionFactory = modbusAlgorithmOutcome is { } outcome
                    ? new ModbusOutcomeFactory(factory.Descriptor, outcome)
                    : qualificationExecutionFactory;
                services = BuildServices(fixture.Options, fixture.Store, fixture.Identity,
                    fixture.Sessions, fixture.Authorization, factory, provider, clock, plan, facility,
                    maximumRuns, modbusProfile: modbusProfile,
                    executionFactory: executionFactory);
                var runtime = services.GetRequiredService<IStationRuntime>();
                if (runtime is not StationRuntime station)
                    throw new XunitException("V137 qualification composition did not use StationRuntime");
                await station.WaitForRecipeActivationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                await station.WaitForStationQualificationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                pump = ClockPump.Start(clock);
                var beforeQualification = await runtime.GetSnapshotAsync();
                return new QualificationHarness(fixture, services, pump, facility, provider, plan, draft,
                    beforeQualification.ActiveRecipe,
                    allowDisposeFailure);
            }
            catch
            {
                if (pump is not null) await pump.DisposeAsync();
                if (services is not null) await DisposeProviderAsync(services, allowDisposeFailure);
                if (initialPump is not null) await initialPump.DisposeAsync();
                if (initialServices is not null) await DisposeProviderAsync(initialServices, allowDisposeFailure);
                await fixture.DisposeAsync();
                throw;
            }
        }

        private static async Task DisposeProviderAsync(ServiceProvider services, bool allowFailure)
        {
            try { await services.DisposeAsync(); }
            catch (InvalidOperationException exception) when (allowFailure &&
                exception.Message is "StationQualificationShutdownIncomplete" or
                "StationQualificationShutdownAdmissionIncomplete")
            {
                // R06 intentionally leaves the physical facility owner fenced;
                // only the bounded qualification shutdown outcomes are expected.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _clockPump.DisposeAsync();
            await DisposeProviderAsync(_services, _allowDisposeFailure);
            await _fixture.DisposeAsync();
        }

        internal sealed class ColdQualificationScope : IAsyncDisposable
        {
            private readonly ServiceProvider _services;
            private readonly ClockPump _clockPump;
            private readonly InteractiveSessionService _sessions;
            private readonly SqliteCommandStore _store;
            private readonly LocalAuthorizationService _authorization;
            private bool _disposed;

            internal ColdQualificationScope(ServiceProvider services, ClockPump clockPump,
                SqliteCommandStore store, LocalAuthorizationService authorization,
                ControlledQualificationFacility facility,
                IStationQualificationSessionService qualification,
                IStationQualificationHistoryQuery history,
                InteractiveSessionService sessions)
            {
                _services = services;
                _clockPump = clockPump;
                _sessions = sessions;
                _store = store;
                _authorization = authorization;
                Facility = facility;
                Qualification = qualification;
                History = history;
            }

            internal ControlledQualificationFacility Facility { get; }
            internal IStationQualificationSessionService Qualification { get; }
            internal IStationQualificationHistoryQuery History { get; }

            internal CommandInvocation Invocation() => new(CommandSource.PhysicalConsole,
                _sessions.Current.PrincipalId, _sessions.Current.SessionId);

            internal async Task<StationQualificationSessionSnapshot> WaitForSnapshotAsync(
                Func<StationQualificationSessionSnapshot, bool> predicate, string timeoutReason)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                StationQualificationSessionSnapshot? last = null;
                while (DateTime.UtcNow < deadline)
                {
                    var result = await Qualification.GetSnapshotAsync(Invocation());
                    Assert.True(result.Available, result.ReasonCode);
                    last = Assert.IsType<StationQualificationSessionSnapshot>(result.Snapshot);
                    if (predicate(last)) return last;
                    await Task.Delay(25);
                }
                throw new XunitException(timeoutReason + ":" + last?.Phase + "/" + last?.ReasonCode);
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                await _clockPump.DisposeAsync();
                await _services.DisposeAsync();
                _authorization.Dispose();
                await _sessions.DisposeAsync();
                await _store.DisposeAsync();
            }
        }

        private static ServiceProvider BuildServices(ProductionStoreOptions options,
            SqliteCommandStore store, LocalIdentityService identity,
            InteractiveSessionService sessions, LocalAuthorizationService authorization,
            ManualFactory factory, VirtualCameraProvider provider,
            VirtualCameraClock clock,
            StationQualificationPlan? plan, ControlledQualificationFacility? facility,
            int maximumRuns = 1, TimeSpan? heartbeatInterval = null,
            ModbusQualificationProfile? modbusProfile = null,
            IVisionAlgorithmFactory? executionFactory = null)
        {
            var registrations = new ServiceCollection();
            registrations.AddSingleton(options);
            registrations.AddSingleton(store);
            registrations.AddSingleton(identity);
            registrations.AddSingleton<IIdentityProvider>(identity);
            registrations.AddSingleton<ILocalAdministratorBootstrap>(identity);
            registrations.AddSingleton(sessions);
            registrations.AddSingleton<IInteractiveSessionService>(sessions);
            registrations.AddSingleton(authorization);
            registrations.AddSingleton<IStepUpAuthentication>(authorization);
            registrations.AddSingleton<IIdentityAdministrationQuery>(authorization);
            registrations.AddSingleton<IVisionAlgorithmFactory>(executionFactory ?? factory);
            registrations.AddSingleton(clock);
            registrations.AddSingleton<IFrameAcquisitionClock>(clock);
            registrations.AddSharpInspectAlgorithmPreparation(
                new AlgorithmPreparationOptions(TimeSpan.FromSeconds(5), 1, 2));
            registrations.AddSharpInspectAlgorithmExecution(new AlgorithmExecutionOptions(
                options.RecipeDrafts!.ExecutionPolicy, TimeSpan.FromSeconds(5)));
            registrations.AddSharpInspectFrameBufferPool(new FrameBufferPoolOptions(
                2, 4096, TimeSpan.FromSeconds(1)));
            registrations.AddSharpInspectCameraProvider(provider);
            registrations.AddSharpInspectCameraSetup(new CameraSetupOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(2),
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            });
            // Baseline preparation has no running qualification workload. Its
            // slow heartbeat avoids synthetic polling contention with activation;
            // the actual qualification runtime retains the 20 ms heartbeat.
            registrations.AddSharpInspectSqliteRuntime(options,
                heartbeatInterval ?? TimeSpan.FromMilliseconds(20));
            registrations.AddSingleton<RecipeActivationService>(p => new RecipeActivationService(
                p.GetRequiredService<RecipeDraftService>(), p.GetRequiredService<IReleasedRecipeQuery>(),
                p.GetRequiredService<IPlcResultContractQuery>(), p.GetRequiredService<IRecipeActivationQuery>(),
                authorization, store, options, p.GetService<AlgorithmPreparationService>(),
                p.GetService<AlgorithmPreparationOptions>(), p.GetService<FrameBufferPool>(),
                (correlation, token) => p.GetRequiredService<IStationRuntime>() is StationRuntime station
                    ? station.ReserveRecipeActivationAsync(correlation, token)
                    : ValueTask.FromResult(new RecipeActivationRuntimeLease(Guid.Empty,
                        "RecipeActivationRuntimeUnavailable", token)),
                () => p.GetRequiredService<IStationRuntime>().GetSnapshotAsync(),
                RecipeActivationInternalFixture.CreateForContractTests()));
            registrations.AddSingleton<IRecipeActivationService>(p =>
                p.GetRequiredService<RecipeActivationService>());
            if (plan is not null && facility is not null)
            {
                var qualificationOptions = new StationQualificationSessionOptions
                {
                    OperationTimeout = TimeSpan.FromSeconds(5),
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    ObservationFreshness = TimeSpan.FromSeconds(2),
                    MaximumRuns = maximumRuns
                };
                if (modbusProfile is null)
                    registrations.AddSharpInspectStationQualificationSessions(plan, facility,
                        qualificationOptions);
                else
                    registrations.AddSharpInspectModbusStationQualificationSessions(plan, facility,
                        modbusProfile, qualificationOptions);
            }
            return registrations.BuildServiceProvider();
        }

        private static StationQualificationPlan CreatePlan(RecipeActivationRecord baseline,
            bool developmentFacility = true, ModbusQualificationProfile? modbusProfile = null)
        {
            var identity = new QualificationHarnessIdentity("V137.Qualification.Facility", "1",
                Hash('A'), Hash('B'), developmentOnly: developmentFacility);
            var target = new QualificationControllerConfiguration(Hash('C'), Hash('D'),
                new byte[] { 1, 2, 3 });
            var transient = new QualificationControllerConfiguration(
                modbusProfile?.EndpointBindingHash ?? Hash('E'), Hash('F'),
                new byte[] { 4, 5, 6 });
            var snapshot = Assert.IsType<RecipeActivationSnapshot>(baseline.SuccessfulSnapshot);
            return new StationQualificationPlan(baseline.Reference,
                Hash('1'), snapshot.Release.ContentHash,
                modbusProfile?.ContentHash ?? baseline.ResultingRecipe!.ContentHash,
                Hash('2'), identity, target, transient, new[]
                {
                    new QualificationDestinationBinding(QualificationDestinationKind.ProductionPlcOutput,
                        "plc", Hash('3')),
                    new QualificationDestinationBinding(QualificationDestinationKind.OrdinaryOutbox,
                        "outbox", Hash('4')),
                    new QualificationDestinationBinding(QualificationDestinationKind.Mes,
                        "mes", Hash('5')),
                    new QualificationDestinationBinding(QualificationDestinationKind.Spc,
                        "spc", Hash('6')),
                    new QualificationDestinationBinding(QualificationDestinationKind.Yield,
                        "yield", Hash('7'))
                }, new[] { modbusProfile?.ScenarioId ?? "V137.Scenario.A" });
        }

        private sealed class ModbusOutcomeFactory : IVisionAlgorithmFactory
        {
            private readonly ModbusAlgorithmOutcomeMode _mode;

            internal ModbusOutcomeFactory(AlgorithmDescriptor descriptor,
                ModbusAlgorithmOutcomeMode mode)
            {
                Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
                _mode = mode;
            }

            public AlgorithmDescriptor Descriptor { get; }

            public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
                AlgorithmConfigurationSnapshot configuration,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                    configuration.Validate(Descriptor.ConfigurationSchema));
            }

            public ValueTask<IVisionAlgorithm> CreateAsync(
                AlgorithmConfigurationSnapshot configuration,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<IVisionAlgorithm>(
                    new ModbusOutcomeAlgorithm(_mode));
            }
        }

        private sealed class ModbusOutcomeAlgorithm : IVisionAlgorithm
        {
            private readonly ModbusAlgorithmOutcomeMode _mode;

            internal ModbusOutcomeAlgorithm(ModbusAlgorithmOutcomeMode mode)
            {
                _mode = mode;
            }

            public ValueTask WarmUpAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }

            public async ValueTask<AlgorithmResult> ExecuteAsync(
                AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                switch (_mode)
                {
                    case ModbusAlgorithmOutcomeMode.Error:
                        throw new InvalidOperationException("V140AlgorithmError");
                    case ModbusAlgorithmOutcomeMode.Timeout:
                        // A cooperative deadline expiry produces Timeout. A
                        // callback that ignores cancellation past the grace
                        // interval is AlgorithmHung, a separate process-level
                        // fence already exercised by the hang-probe tests.
                        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan,
                            cancellationToken).ConfigureAwait(false);
                        throw new InvalidOperationException("V140AlgorithmTimeoutUnexpectedReturn");
                    case ModbusAlgorithmOutcomeMode.SelfCancelled:
                        throw new OperationCanceledException("V140AlgorithmSelfCancelled");
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static async Task<ReleasedRecipe> ReleaseDraftAsync(
            QualificationFixture fixture, ServiceProvider services, RecipeDraftRevision draft)
        {
            var command = new ReleaseRecipeCommand(Guid.NewGuid(), fixture.Invocation(), draft.DraftId,
                draft.Revision, draft.RevisionContentHash, fixture.Options.RecipeReleases!.Policy.Reference,
                "V137 release qualification target");
            var grant = await GrantAsync(fixture.Authorization, fixture.Password, command.Invocation,
                Permission.ReleaseRecipe, command.CorrelationId, command.AuthorizationTarget,
                AuditedCommandKind.ReleaseRecipe);
            var result = await services.GetRequiredService<IRecipeReleaseService>().ReleaseAsync(
                command with { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
            AssertAccepted(result.Outcome, "release qualification target");
            return Assert.IsType<ReleasedRecipe>(result.Recipe);
        }

        private static async Task BindContractAsync(QualificationFixture fixture,
            IStationRuntime runtime, ManualFactory factory)
        {
            var command = new ChangePlcResultContractCommand(Guid.NewGuid(), fixture.Invocation(),
                PlcResultContractTestSupport.Contract(factory.Descriptor.ResultSchema), null,
                "V137 bind qualification PLC contract");
            var grant = await GrantAsync(fixture.Authorization, fixture.Password, command.Invocation,
                Permission.ManagePlcResultContract, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract);
            var result = await runtime.SubmitAsync(command with
            {
                Invocation = command.Invocation with { StepUpGrantId = grant.GrantId }
            });
            AssertAccepted(result, "bind qualification PLC contract");
        }

        private static async Task<RecipeActivationResult> ActivateAsync(
            QualificationFixture fixture, ServiceProvider services, ReleasedRecipe released,
            RecipeActivationReference? expectedActive = null)
        {
            for (var attempt = 0; ; attempt++)
            {
                var command = new ActivateRecipeCommand(Guid.NewGuid(), fixture.Invocation(),
                    released.Reference, released.Record.ReleaseId, released.Record.ContentHash,
                    expectedActive, null, "V137 activate qualification target");
                var grant = await GrantAsync(fixture.Authorization, fixture.Password, command.Invocation,
                    Permission.ActivateRecipe, command.CorrelationId, command.AuthorizationTarget,
                    AuditedCommandKind.ActivateRecipe);
                var result = await services.GetRequiredService<IRecipeActivationService>().ActivateAsync(command with
                {
                    Invocation = command.Invocation with { StepUpGrantId = grant.GrantId }
                });
                // The Runtime heartbeat can legitimately win its non-blocking
                // station lock while this fixture prepares a baseline. Retry
                // only a persisted refusal before physical admission, using a
                // new command and grant; any physical or business failure stays
                // visible to the caller's assertion.
                if (attempt >= 2 || result.Outcome is not
                    { Disposition: CommandDisposition.Rejected, Audit: AuditPersistence.Persisted,
                      ReasonCode: "RecipeActivationRuntimeBusy" } || result.Record is not
                    { AdmissionReference: null, Admission: null, SuccessfulSnapshot: null,
                      Outcome.State: RecipeActivationOutcomeState.Failed,
                      Restoration.State: RecipeActivationRestorationState.NotRequired,
                      Restoration.ReasonCode: "RecipeActivationHardwareUntouched" })
                    return result;
                await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
            }
        }

        private static async Task ConfigureQualificationCameraAsync(
            QualificationFixture fixture, IStationRuntime runtime, CameraProviderIdentity provider)
        {
            var camera = Assert.IsAssignableFrom<ICameraSetupRuntime>(runtime);
            var current = await camera.GetSetupAsync(LogicalRole, fixture.Invocation());
            Assert.True(current.Available, current.ReasonCode);
            var target = new CameraBindingTarget(provider, StableDeviceIdentity);
            var binding = current.Snapshot?.Binding;
            if (binding is not null && binding.Target == target) return;
            var operation = Guid.NewGuid();
            var grant = await GrantAsync(fixture.Authorization, fixture.Password,
                fixture.Invocation(), Permission.ManageCameraBindings, operation, LogicalRole,
                AuditedCommandKind.RebindCamera);
            var rebound = await camera.RebindAsync(new CameraRebindRequest(operation,
                fixture.Invocation(grant.GrantId), LogicalRole, binding?.Revision ?? 0,
                binding?.RevisionHash, target, "V137 bind qualification camera"));
            Assert.True(rebound.Succeeded, rebound.ReasonCode);
        }

        private static async Task<StepUpResult> GrantAsync(LocalAuthorizationService authorization,
            string password, CommandInvocation invocation, Permission permission, Guid operation,
            string target, AuditedCommandKind kind)
        {
            var result = await authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                invocation, new StepUpBinding(permission, operation, target, kind), password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.GrantId);
            return result;
        }

    private static string Hash(char value) => new(value, 64);

    }

    private sealed class ControlledQualificationFacility : IStationQualificationFacility
    {
        private readonly StationQualificationPlan _plan;
        private readonly bool _incompleteIsolationObservation;
        private readonly bool _blockStimulus;
        private readonly bool _restoreFailure;
        private readonly bool _blockWrite;
        private readonly CancellationTokenSource _lost = new();
        private readonly TaskCompletionSource<bool> _writeEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _writeReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _stimulusReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _openCount;
        private int _waitCount;
        private int _stimulusCount;
        private int _isolationCount;
        private int _isolated;
        private int _restoreCount;
        private int _releaseIsolationCount;
        private int _writeCount;
        private int _disposeCount;
        private int _controllerRestored;
        private long _sequence;
        private long _stimulusSequence;

        internal ControlledQualificationFacility(StationQualificationPlan plan,
            bool incompleteIsolationObservation, bool blockStimulus, bool restoreFailure,
            bool blockWrite)
        {
            _plan = plan;
            _incompleteIsolationObservation = incompleteIsolationObservation;
            _blockStimulus = blockStimulus;
            _restoreFailure = restoreFailure;
            _blockWrite = blockWrite;
        }

        public QualificationHarnessIdentity Identity => _plan.QualificationHarnessIdentity;
        internal int OpenCount => Volatile.Read(ref _openCount);
        internal int WaitCount => Volatile.Read(ref _waitCount);
        internal void ReleaseStimulus() => _stimulusReleased.TrySetResult(true);
        internal int StimulusCount => Volatile.Read(ref _stimulusCount);
        internal int IsolationCount => Volatile.Read(ref _isolationCount);
        internal int RestoreCount => Volatile.Read(ref _restoreCount);
        internal int ReleaseIsolationCount => Volatile.Read(ref _releaseIsolationCount);
        internal int WriteCount => Volatile.Read(ref _writeCount);
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        internal Task WriteEntered => _writeEntered.Task;

        public ValueTask<IStationQualificationFacilityLease> OpenAsync(
            QualificationFacilityRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Plan.ContentHash != _plan.ContentHash)
                throw new InvalidOperationException("V137FacilityPlanMismatch");
            Interlocked.Increment(ref _openCount);
            return ValueTask.FromResult<IStationQualificationFacilityLease>(
                new Lease(this, request));
        }

        internal void SignalLost() => _lost.Cancel();

        internal void ReleaseWrite() => _writeReleased.TrySetResult(true);

        private QualificationFacilityObservation Observe(QualificationFacilityRequest request,
            bool isolated, bool targetController)
        {
            var destinations = _plan.DestinationBindings
                .Where((_, index) => !_incompleteIsolationObservation || !isolated ||
                    Volatile.Read(ref _openCount) > 1 || index < 4)
                .Select(binding => new QualificationDestinationObservation(binding.Kind,
                    binding.DestinationId, binding.TargetBindingHash,
                    isolated ? QualificationDestinationRoute.IsolatedTest :
                        QualificationDestinationRoute.Production, false))
                .ToArray();
            var sequence = Interlocked.Increment(ref _sequence);
            return new QualificationFacilityObservation(Identity, request.SessionId,
                request.LeaseNonce, request.RuntimeEpoch, sequence, DateTimeOffset.UtcNow,
                connected: true, lineStopped: true,
                targetController ? _plan.TargetControllerConfiguration.ContentHash :
                    _plan.TransientControllerConfiguration.ContentHash, destinations);
        }

        private sealed class Lease : IStationQualificationFacilityLease
        {
            private readonly ControlledQualificationFacility _owner;
            private readonly QualificationFacilityRequest _request;
            private int _disposed;

            internal Lease(ControlledQualificationFacility owner,
                QualificationFacilityRequest request)
            { _owner = owner; _request = request; }

            public CancellationToken FacilityLost => _owner._lost.Token;

            public ValueTask<QualificationFacilityObservation> ObserveAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotDisposed();
                return ValueTask.FromResult(_owner.Observe(_request,
                    isolated: Volatile.Read(ref _owner._isolated) != 0,
                    targetController: Volatile.Read(ref _owner._isolated) == 0 ||
                        Volatile.Read(ref _owner._controllerRestored) != 0));
            }

            public ValueTask<QualificationFacilityOperationResult> ApplyIsolationAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotDisposed();
                Interlocked.Increment(ref _owner._isolationCount);
                Interlocked.Exchange(ref _owner._isolated, 1);
                Interlocked.Exchange(ref _owner._controllerRestored, 0);
                return ValueTask.FromResult(QualificationFacilityOperationResult.Success(
                    "V137IsolationApplied"));
            }

            public async ValueTask<QualificationFacilityStimulus> WaitForStimulusAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotDisposed();
                Interlocked.Increment(ref _owner._waitCount);
                if (_owner._blockStimulus)
                    await _owner._stimulusReleased.Task.WaitAsync(cancellationToken);
                var sequence = Interlocked.Increment(ref _owner._stimulusSequence);
                Interlocked.Increment(ref _owner._stimulusCount);
                return new QualificationFacilityStimulus(_request.SessionId,
                    _request.LeaseNonce, sequence, _owner._plan.ScenarioIds[0],
                    _owner._plan.QualificationContextHash, 1, checked((uint)sequence));
            }

            public async ValueTask<QualificationFacilityOperationResult> WriteQualificationResultAsync(
                StationQualificationPayload payload, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotDisposed();
                if (payload.SessionId != _request.SessionId ||
                    payload.ContextHash != _owner._plan.QualificationContextHash)
                    return QualificationFacilityOperationResult.Failure(
                        "V137PayloadBindingMismatch");
                Interlocked.Increment(ref _owner._writeCount);
                if (_owner._blockWrite)
                {
                    _owner._writeEntered.TrySetResult(true);
                    var completed = await Task.WhenAny(_owner._writeReleased.Task,
                        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).ConfigureAwait(false);
                    if (completed != _owner._writeReleased.Task)
                        throw new OperationCanceledException(cancellationToken);
                }
                return QualificationFacilityOperationResult.Success(
                    "V137QualificationPayloadWritten");
            }

            public ValueTask<QualificationFacilityOperationResult> RestoreControllerAsync(
                QualificationControllerConfiguration targetConfiguration,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotDisposed();
                Interlocked.Increment(ref _owner._restoreCount);
                if (_owner._restoreFailure)
                    return ValueTask.FromResult(QualificationFacilityOperationResult.Failure(
                        "V137RestoreFailed"));
                Interlocked.Exchange(ref _owner._controllerRestored, 1);
                return ValueTask.FromResult(QualificationFacilityOperationResult.Success(
                    "V137ControllerRestored"));
            }

            public ValueTask<QualificationFacilityOperationResult> ReleaseIsolationAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNotDisposed();
                Interlocked.Increment(ref _owner._releaseIsolationCount);
                Interlocked.Exchange(ref _owner._isolated, 0);
                return ValueTask.FromResult(QualificationFacilityOperationResult.Success(
                    "V137IsolationReleased"));
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Increment(ref _owner._disposeCount);
                return ValueTask.CompletedTask;
            }

            private void EnsureNotDisposed()
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(Lease));
            }
        }
    }

    private sealed class QualificationFixture : IAsyncDisposable
    {
        private const string Station = "V137QualificationStation";
        internal const string UserName = "qualification.runtime.admin";
        internal const string PasswordText = "V137 qualification password 2026!";
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _audit;
        private bool _disposed;

        private QualificationFixture(string directory, AuditIntegrityPolicy audit,
            ProductionStoreOptions baseOptions, SqliteCommandStore store,
            LocalIdentityService identity, InteractiveSessionService sessions,
            LocalAuthorizationService authorization,
            ModbusQualificationProfile? modbusProfile)
        {
            _directory = directory;
            _audit = audit;
            BaseOptions = baseOptions;
            Options = baseOptions;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            ModbusProfile = modbusProfile;
        }

        internal ProductionStoreOptions BaseOptions { get; }
        internal ProductionStoreOptions Options { get; private set; }
        internal SqliteCommandStore Store { get; private set; }
        internal LocalIdentityService Identity { get; private set; }
        internal InteractiveSessionService Sessions { get; private set; }
        internal LocalAuthorizationService Authorization { get; private set; }
        internal ModbusQualificationProfile? ModbusProfile { get; }
        internal string Password => PasswordText;

        internal CommandInvocation Invocation(Guid? grant = null) => new(
            CommandSource.PhysicalConsole, Sessions.Current.PrincipalId,
            Sessions.Current.SessionId, grant);

        internal static async Task<QualificationFixture> CreateAsync(bool allowQualification,
            StationQualificationStoreOptions? ledgerOptions = null,
            RecipeTransferStoreOptions? recipeTransfers = null,
            int? maximumAuditEntries = null,
            Func<TraceStoragePolicySnapshot, ModbusQualificationProfile>? profileFactory = null,
            bool enableQualificationCycles = false,
            QualificationCycleStoreOptions? qualificationCycleOptions = null)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Qualification SQLite fixture requires Windows machine key protection.");
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V137-Qualification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var audit = new AuditIntegrityPolicy(Station, "v1",
                "SharpInspect.Test.V137.Qualification." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                MaximumVerificationEntries = maximumAuditEntries ?? 10_000,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var traceEnabled = enableQualificationCycles || profileFactory is not null ||
                qualificationCycleOptions is not null;
            var policy = CreateQualificationPolicy(allowQualification, traceEnabled);
            var identityOptions = new LocalIdentityOptions(Station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v137-qualification-blocklist", "1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, policy);
            var execution = new AlgorithmExecutionPolicy("V137.Qualification.Execution", "1",
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
            var releasePolicy = new RecipeGovernancePolicy("V137.Qualification.Release", "1",
                RecipeGovernanceMode.SingleApproverRelease);
            var options = new ProductionStoreOptions(Path.Combine(directory, "qualification.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                AlarmPolicy = CreateQualificationAlarmPolicy(),
                RecipeDrafts = new RecipeDraftStoreOptions(execution),
                RecipeReleases = new RecipeReleaseStoreOptions(releasePolicy),
                PlcResultContracts = new PlcResultContractStoreOptions(),
                CameraSetup = new CameraSetupStoreOptions(),
                RecipeActivations = new RecipeActivationStoreOptions(),
                RecipeTransfers = recipeTransfers,
                TraceStoragePolicies = traceEnabled ? new TraceStoragePolicyStoreOptions
                {
                    DeploymentScope = new TraceStorageDeploymentScope(
                        "V140.Qualification.Deployment", "1", Array.Empty<TraceStorageRouteIdentity>())
                } : null,
                // Create schema-23 from the first provider.  The baseline
                // provider intentionally has no registered facility, while
                // the cold provider below adds the frozen plan and facility;
                // reopening an older schema would be a governed migration.
                StationQualifications = ledgerOptions ?? new StationQualificationStoreOptions(),
                QualificationCycles = traceEnabled ? qualificationCycleOptions ??
                    new QualificationCycleStoreOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(4),
                QueryTimeout = TimeSpan.FromSeconds(4),
                QueueCapacity = 8
            };
            SqliteCommandStore? store = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
                var identity = new LocalIdentityService(store, identityOptions,
                    new RecipeDraftStorageTests.Fixture.FixtureConsole());
                var bootstrap = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(bootstrap.Succeeded, bootstrap.ReasonCode);
                var token = bootstrap.Token!.TakeForDisplay();
                bootstrap.Token.Dispose();
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(Station, token, UserName,
                        "Qualification Runtime Administrator", PasswordText));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                var sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                var login = await sessions.SignInAsync(new PasswordSignInRequest(UserName, PasswordText));
                Assert.True(login.Succeeded, login.ReasonCode);
                var authorization = new LocalAuthorizationService(store, identityOptions,
                    identity, sessions);
                await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
                ModbusQualificationProfile? modbusProfile = null;
                if (traceEnabled)
                {
                    var policyService = new TraceStoragePolicyService(options, authorization, store,
                        new SqliteTraceStoragePolicyQuery(options));
                    var operation = Guid.NewGuid();
                    var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                        sessions.Current.PrincipalId, sessions.Current.SessionId);
                    var definition = TraceStoragePolicyRuntimeTests.Policy();
                    var command = new PublishTraceStoragePolicyCommand(operation, invocation, 0,
                        definition, "V140 qualification cycle storage policy");
                    var grant = await authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                        invocation, new StepUpBinding(Permission.ManageProductionPolicy, operation,
                            command.AuthorizationTarget, AuditedCommandKind.PublishTraceStoragePolicy), PasswordText));
                    Assert.True(grant.Succeeded, grant.ReasonCode);
                    var published = await policyService.PublishAsync(command with
                    {
                        Invocation = invocation with { StepUpGrantId = grant.GrantId }
                    });
                    Assert.True(published.Succeeded, published.Outcome.ReasonCode);
                    Assert.NotNull(published.Snapshot);
                    modbusProfile = profileFactory?.Invoke(published.Snapshot!);
                }
                return new QualificationFixture(directory, audit, options, store,
                    identity, sessions, authorization, modbusProfile);
            }
            catch
            {
                if (store is not null) await store.DisposeAsync();
                RecipeDraftStorageTests.Fixture.DeleteDirectoryForTest(directory, audit);
                throw;
            }
        }

        internal async Task ReopenWithStationQualificationAsync()
        {
            Authorization.Dispose();
            await Sessions.DisposeAsync();
            await Store.DisposeAsync();
            Options = CloneWithStationQualification(BaseOptions);
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(Store);
            Identity = new LocalIdentityService(Store, Options.LocalIdentity!,
                new RecipeDraftStorageTests.Fixture.FixtureConsole());
            Sessions = new InteractiveSessionService(Identity,
                Options.LocalIdentity!.AuthenticationPolicy, Identity.PersistSessionEventAsync);
            var login = await Sessions.SignInAsync(new PasswordSignInRequest(UserName, PasswordText));
            Assert.True(login.Succeeded, login.ReasonCode);
            Authorization = new LocalAuthorizationService(Store, Options.LocalIdentity!,
                Identity, Sessions);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(Store);
        }

        internal ProductionStoreOptions CreateColdSnapshotOptions()
        {
            var path = Path.Combine(_directory, "cold-" + Guid.NewGuid().ToString("N") + ".sqlite");
            using var source = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = Options.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
            using var target = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            source.Open();
            target.Open();
            source.BackupDatabase(target);
            return CloneWithStationQualification(Options, path);
        }

        private static ProductionStoreOptions CloneWithStationQualification(
            ProductionStoreOptions source, string? databasePath = null) => new(databasePath ?? source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = source.AlarmPolicy,
            ExternalAuditAnchor = source.ExternalAuditAnchor,
            AlgorithmResultArchive = source.AlgorithmResultArchive,
            RecipeDrafts = source.RecipeDrafts,
            CameraSetup = source.CameraSetup,
            CameraRecovery = source.CameraRecovery,
            CameraNetwork = source.CameraNetwork,
            ImagingSetup = source.ImagingSetup,
            CalibrationSessions = source.CalibrationSessions,
            CalibrationGovernance = source.CalibrationGovernance,
            RecipeReleases = source.RecipeReleases,
            PlcResultContracts = source.PlcResultContracts,
            RecipeActivations = source.RecipeActivations,
            RecipeTransfers = source.RecipeTransfers,
            TraceStoragePolicies = source.TraceStoragePolicies is { } trace
                ? new TraceStoragePolicyStoreOptions
                {
                    MaximumEntries = trace.MaximumEntries,
                    MaximumPayloadBytes = trace.MaximumPayloadBytes,
                    MaximumTotalBytes = trace.MaximumTotalBytes,
                    DeploymentScope = trace.DeploymentScope
                }
                : null,
            QualificationCycles = source.QualificationCycles is { } cycles
                ? new QualificationCycleStoreOptions
                {
                    MaximumEntries = cycles.MaximumEntries,
                    MaximumPayloadBytes = cycles.MaximumPayloadBytes,
                    MaximumTotalBytes = cycles.MaximumTotalBytes
                }
                : null,
            PreviewSessions = source.PreviewSessions,
            CalibrationImports = source.CalibrationImports,
            ManualInspections = source.ManualInspections,
            ProductionAdmission = source.ProductionAdmission,
            StationQualifications = source.StationQualifications is { } station
                ? new StationQualificationStoreOptions
                {
                    MaximumEntries = station.MaximumEntries,
                    MaximumPayloadBytes = station.MaximumPayloadBytes,
                    MaximumTotalBytes = station.MaximumTotalBytes
                }
                : new StationQualificationStoreOptions(),
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity
        };

        private static AuthorizationPolicy CreateQualificationPolicy(bool allowQualification,
            bool allowTraceStoragePolicy = false)
        {
            var roles = AuthorizationPolicy.Development.RoleBundles.ToDictionary(
                pair => pair.Key,
                pair => pair.Key == HumanRoleBundle.Administrator
                    ? pair.Value.Where(permission => allowQualification || permission != Permission.RunStationQualification)
                        .Concat(new[] { Permission.EditRecipeDraft })
                        .Concat(allowQualification ? new[] { Permission.RunStationQualification } :
                            Array.Empty<Permission>())
                        .Concat(allowTraceStoragePolicy ? new[] { Permission.ManageProductionPolicy } :
                            Array.Empty<Permission>()).Distinct()
                    : pair.Value.Where(permission => allowQualification || permission != Permission.RunStationQualification));
            return new AuthorizationPolicy("V137.Qualification.Authorization", "1", roles,
                AuthorizationPolicy.Development.StepUpPermissions
                    .Where(permission => allowQualification || permission != Permission.RunStationQualification).Concat(
                    allowQualification ? new[] { Permission.RunStationQualification } :
                        Array.Empty<Permission>())
                    .Concat(allowTraceStoragePolicy ? new[] { Permission.ManageProductionPolicy } :
                        Array.Empty<Permission>()).Distinct());
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            Authorization.Dispose();
            await Sessions.DisposeAsync();
            await Store.DisposeAsync();
            RecipeDraftStorageTests.Fixture.DeleteDirectoryForTest(_directory, _audit);
        }
    }
}
