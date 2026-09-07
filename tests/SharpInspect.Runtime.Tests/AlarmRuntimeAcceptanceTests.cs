#pragma warning disable CA1416

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V109 acceptance coverage over the signed SQLite store, real identity/session authority and
/// StationRuntime alarm boundary.  The only source that this fixture treats as trusted without
/// registration is Runtime.StartupRecovery; device registrations still use the internal adapter
/// seam so these tests cannot become a public provider bypass.
/// </summary>
public sealed class AlarmRuntimeAcceptanceTests
{
    [Fact]
    public async Task V109_R01_StartupRecoveryAlarmIsLatchedAndKeepsRuntimeNotReady()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));

        var snapshot = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState?.Instances.Any(instance => instance.Code == "StartupRecoveryRequired" &&
                instance.Lifecycle == AlarmLifecycle.Active) == true);

        var alarm = Assert.Single(snapshot.AlarmState!.Instances,
            instance => instance.Code == "StartupRecoveryRequired");
        Assert.False(snapshot.Ready);
        Assert.Contains("AlarmProductionBlocked", snapshot.AdmissionBlockers);
        Assert.Equal("Runtime.StartupRecovery", alarm.Source);
        Assert.Equal(AlarmSeverity.Warning, alarm.Severity);
        Assert.Equal(ProductionImpact.BlockNewTriggers, alarm.ProductionImpact);
        Assert.True(alarm.IsLatched);
        Assert.True(alarm.ResetPrerequisites.HasFlag(AlarmResetPrerequisites.RecoveryComplete));
        Assert.True(alarm.ResetPrerequisites.HasFlag(AlarmResetPrerequisites.NoPendingDelivery));
        Assert.Equal(snapshot.RuntimeEpoch, alarm.SourceRuntimeEpoch);
    }

    [Fact]
    public async Task V109_R02_NoneImpactDoesNotEraseBlockingAlarmOrBoundedPlcProjection()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        runtime.RegisterAlarmSource("Device.Status");

        var deviceObservation = await ObserveAsync(fixture, "DeviceFault", "Camera.Primary", false);
        Assert.True(deviceObservation.Accepted, deviceObservation.ReasonCode);
        var noticeObservation = await ObserveAsync(fixture, "Notice", "Device.Status", false);
        Assert.True(noticeObservation.Accepted, noticeObservation.ReasonCode);

        var snapshot = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState?.Instances.Count(instance => instance.Lifecycle != AlarmLifecycle.Cleared) == 3);
        var state = snapshot.AlarmState!;
        var device = Assert.Single(state.Instances, instance => instance.Code == "DeviceFault");
        var notice = Assert.Single(state.Instances, instance => instance.Code == "Notice");

        Assert.Equal(3, state.Plc.TotalUncleared);
        Assert.Equal(2, state.Plc.BlockingCount);
        Assert.True(state.Plc.FaultAbortPresent);
        Assert.Single(state.Plc.Entries);
        Assert.Contains(state.Plc.Entries, entry => entry.InstanceId == device.InstanceId &&
            entry.ProductionImpact == ProductionImpact.FaultAbort);
        Assert.DoesNotContain(state.Plc.Entries, entry => entry.InstanceId == notice.InstanceId);
        Assert.Contains(state.Instances, instance => instance.Code == "StartupRecoveryRequired");
        Assert.False(snapshot.Ready);
    }

    [Fact]
    public async Task V109_R03_AdministratorAcknowledgesAndResetsOnlyTheExactHealthyInstance()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");

        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);
        var raised = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        var deviceId = raised.InstanceId;
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);

        var acknowledgeCorrelation = Guid.NewGuid();
        var acknowledge = await fixture.Runtime.SubmitAsync(new AcknowledgeAlarmCommand(
            acknowledgeCorrelation, Invocation(actor), deviceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Accepted, acknowledge.Disposition);
        Assert.Equal(AuditPersistence.Persisted, acknowledge.Audit);

        var acknowledged = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        Assert.Equal(deviceId, acknowledged.InstanceId);
        Assert.Equal(AlarmLifecycle.Active, acknowledged.Lifecycle);
        Assert.True(acknowledged.Acknowledged);
        Assert.True((await fixture.Runtime.GetSnapshotAsync()).AlarmState!.Plc.FaultAbortPresent);

        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", true);
        var recovered = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.InstanceId == deviceId && instance.Lifecycle == AlarmLifecycle.RecoveredLatched &&
                instance.SourceHealthy);
        Assert.Equal(deviceId, recovered.InstanceId);

        var (resetCorrelation, resetGrant) = await IssueResetGrantAsync(fixture, actor, deviceId);
        var reset = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(resetCorrelation,
            Invocation(actor, resetGrant), deviceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Accepted, reset.Disposition);
        Assert.Equal(AuditPersistence.Persisted, reset.Audit);

        var afterReset = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState is { } alarms && !alarms.Instances.Any(instance => instance.InstanceId == deviceId));
        Assert.Contains(afterReset.AlarmState!.Instances,
            instance => instance.Code == "StartupRecoveryRequired" && instance.Lifecycle == AlarmLifecycle.Active);
        Assert.Equal(1, afterReset.AlarmState.Plc.TotalUncleared);
        Assert.False(afterReset.Ready);

        var history = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
            instanceId: deviceId, pageSize: 100));
        Assert.Contains(history.Records, record => record.Transition == AlarmTransitionKind.Acknowledged &&
            record.ActorPrincipalId == actor.PrincipalId && record.SessionId == actor.SessionId);
        Assert.Contains(history.Records, record => record.Transition == AlarmTransitionKind.Reset &&
            record.ActorPrincipalId == actor.PrincipalId && record.SessionId == actor.SessionId &&
            record.CommandCorrelationId == resetCorrelation);
        Assert.Contains(history.Records, record => record.Transition == AlarmTransitionKind.Cleared &&
            record.CommandCorrelationId == resetCorrelation);

        var trace = await fixture.TraceQuery.QueryAsync(new CommandTraceFilter(
            CorrelationId: resetCorrelation, PageSize: 20));
        Assert.Equal(new[] { CommandAuditPhase.Outcome, CommandAuditPhase.Completed },
            trace.Records.Select(record => record.Phase));
        Assert.All(trace.Records, record =>
        {
            Assert.Equal(AuditedCommandKind.ResetAlarm, record.CommandKind);
            Assert.Equal(actor.PrincipalId.ToString("D"), record.AuthenticatedHumanPrincipalId);
            Assert.Equal(actor.SessionId, record.ClaimedSessionId);
        });
    }

    [Fact]
    public async Task V109_R04_OperatorWithoutResetPermissionCannotResetLatchedAlarm()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);
        var raised = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", true);
        await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.InstanceId == raised.InstanceId &&
                instance.Lifecycle == AlarmLifecycle.RecoveredLatched);

        var administrator = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var operatorActor = await CreateOperatorAsync(fixture, administrator);
        var authorization = await fixture.AuthorizationQuery.GetCurrentAuthorizationAsync(operatorActor.SessionId);
        Assert.True(authorization.Available, authorization.ReasonCode);
        Assert.DoesNotContain(Permission.ResetAlarm, authorization.Account!.Permissions);

        var rejected = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(Guid.NewGuid(),
            Invocation(operatorActor), raised.InstanceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("PermissionDenied", rejected.ReasonCode);
        Assert.Contains((await fixture.Runtime.GetSnapshotAsync()).AlarmState!.Instances,
            instance => instance.InstanceId == raised.InstanceId &&
                instance.Lifecycle == AlarmLifecycle.RecoveredLatched);
    }

    [Fact]
    public async Task V109_R05_WrongStepUpTargetAndSessionAreRejectedWithoutChangingAlarm()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);
        var raised = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", true);
        await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.InstanceId == raised.InstanceId &&
                instance.Lifecycle == AlarmLifecycle.RecoveredLatched);

        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var (correlation, grant) = await IssueResetGrantAsync(fixture, actor, raised.InstanceId);
        var wrongTarget = Guid.NewGuid();
        var targetRejected = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(correlation,
            Invocation(actor, grant), wrongTarget));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Rejected, targetRejected.Disposition);
        Assert.Equal("StepUpInvalid", targetRejected.ReasonCode);

        var logout = await fixture.Sessions.LogoutAsync(actor.SessionId);
        Assert.True(logout.Succeeded, logout.ReasonCode);
        await fixture.WaitVerifiedAsync();
        var replacement = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var sessionRejected = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(Guid.NewGuid(),
            Invocation(replacement, grant), raised.InstanceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Rejected, sessionRejected.Disposition);
        Assert.Equal("StepUpInvalid", sessionRejected.ReasonCode);

        var unchanged = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        Assert.Equal(raised.InstanceId, unchanged.InstanceId);
        Assert.Equal(AlarmLifecycle.RecoveredLatched, unchanged.Lifecycle);
    }

    [Fact]
    public async Task V109_R06_UnknownAlarmCodeFailsClosedWithoutCreatingAnInstance()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        Assert.Throws<ArgumentException>(() => runtime.RegisterAlarmSource("Unknown.Provider"));
        runtime.RegisterAlarmSource("Camera.Primary");

        var outcome = await ObserveAsync(fixture, "UnknownAlarm", "Camera.Primary", false);
        Assert.False(outcome.Accepted);
        Assert.Equal("AlarmCodeUnmapped", outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);

        var snapshot = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState is { Available: false, ReasonCode: "AlarmCodeUnmapped" });
        Assert.DoesNotContain(snapshot.AlarmState!.Instances, instance => instance.Code == "UnknownAlarm");
        Assert.Contains("AlarmAuthorityUnavailable", snapshot.AdmissionBlockers);

        var history = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(code: "UnknownAlarm", pageSize: 20));
        Assert.False(history.Available);
        Assert.Equal("AlarmCodeUnmapped", history.ReasonCode);
        Assert.Contains(history.Records, record => record.Transition == AlarmTransitionKind.BoundaryRejected &&
            record.Code == "UnknownAlarm" && record.Instance is null);
    }

    [Fact]
    public async Task V109_R07_RestartPreservesOldEpochButRequiresFreshObservationBeforeReset()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);
        var raised = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", true);
        await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.InstanceId == raised.InstanceId &&
                instance.Lifecycle == AlarmLifecycle.RecoveredLatched);
        var oldEpoch = (await fixture.Runtime.GetSnapshotAsync()).RuntimeEpoch;

        await fixture.RestartAsync();
        var restarted = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState?.Instances.Any(instance => instance.InstanceId == raised.InstanceId) == true);
        Assert.NotEqual(oldEpoch, restarted.RuntimeEpoch);
        var persisted = Assert.Single(restarted.AlarmState!.Instances,
            instance => instance.InstanceId == raised.InstanceId);
        Assert.Equal(oldEpoch, persisted.SourceRuntimeEpoch);
        Assert.True(persisted.SourceHealthy);
        Assert.Equal(AlarmLifecycle.RecoveredLatched, persisted.Lifecycle);

        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var (oldCorrelation, oldGrant) = await IssueResetGrantAsync(fixture, actor, raised.InstanceId);
        var oldEpochRejected = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(oldCorrelation,
            Invocation(actor, oldGrant), raised.InstanceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Rejected, oldEpochRejected.Disposition);
        Assert.Equal("AlarmSourceRuntimeEpochMismatch", oldEpochRejected.ReasonCode);

        TrustedRuntime(fixture).RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", true);
        await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.InstanceId == raised.InstanceId &&
                instance.SourceRuntimeEpoch == restarted.RuntimeEpoch && instance.SourceHealthy);

        var (resetCorrelation, resetGrant) = await IssueResetGrantAsync(fixture, actor, raised.InstanceId);
        var reset = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(resetCorrelation,
            Invocation(actor, resetGrant), raised.InstanceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Accepted, reset.Disposition);
        var afterReset = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState is { } alarms && !alarms.Instances.Any(instance => instance.InstanceId == raised.InstanceId));
        Assert.Contains(afterReset.AlarmState!.Instances,
            instance => instance.Code == "StartupRecoveryRequired" && instance.Lifecycle == AlarmLifecycle.Active);
        Assert.False(afterReset.Ready);
    }

    [Fact]
    public async Task V109_R08_WatchdogRejectsExpiredHealthyObservationEvenWithFutureProviderTime()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromMilliseconds(200));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);
        var raised = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault");
        var actor = await SignInAsync(fixture, fixture.BootstrapUserName, fixture.BootstrapPassword);
        var (correlation, grant) = await IssueResetGrantAsync(fixture, actor, raised.InstanceId);

        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", true,
            DateTimeOffset.UtcNow.AddYears(20));
        await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.InstanceId == raised.InstanceId &&
                instance.SourceObservationSequence > raised.SourceObservationSequence &&
                !instance.SourceHealthy,
            timeout: TimeSpan.FromSeconds(5));

        var rejected = await fixture.Runtime.SubmitAsync(new ResetAlarmCommand(correlation,
            Invocation(actor, grant), raised.InstanceId));
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Contains(rejected.ReasonCode, new[] { "AlarmSourceNotHealthy", "AlarmSourceObservationStale" });
        Assert.Contains((await fixture.Runtime.GetSnapshotAsync()).AlarmState!.Instances,
            instance => instance.InstanceId == raised.InstanceId && instance.Lifecycle == AlarmLifecycle.Active);
    }

    [Fact]
    public async Task V109_R09_OutOfOrderSequenceDoesNotPolluteStateAndNextObservationStillWorks()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);

        var before = await fixture.Runtime.GetSnapshotAsync();
        var historyBefore = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
            code: "DeviceFault", pageSize: 100));
        var next = runtime.NextAlarmObservationSequence("DeviceFault");
        var integrity = fixture.Store.Integrity;

        var maximumRejected = await ObserveWithSequenceAsync(fixture, "DeviceFault", "Camera.Primary",
            false, long.MaxValue);
        Assert.False(maximumRejected.Accepted);
        Assert.Equal("AlarmObservationOutOfOrder", maximumRejected.ReasonCode);
        Assert.Equal(AuditPersistence.NotAttempted, maximumRejected.Audit);

        var jumpedRejected = await ObserveWithSequenceAsync(fixture, "DeviceFault", "Camera.Primary",
            false, checked(next + 100));
        Assert.False(jumpedRejected.Accepted);
        Assert.Equal("AlarmObservationOutOfOrder", jumpedRejected.ReasonCode);
        Assert.Equal(AuditPersistence.NotAttempted, jumpedRejected.Audit);

        var afterRejected = await fixture.Runtime.GetSnapshotAsync();
        Assert.Equal(before.AlarmState!.Instances.ToArray(), afterRejected.AlarmState!.Instances.ToArray());
        Assert.Equal(before.AlarmState.Plc.Entries.ToArray(), afterRejected.AlarmState.Plc.Entries.ToArray());
        Assert.Equal(before.AlarmState.Plc.TotalUncleared, afterRejected.AlarmState.Plc.TotalUncleared);
        Assert.Equal(before.AlarmState.Plc.BlockingCount, afterRejected.AlarmState.Plc.BlockingCount);
        Assert.Equal(before.AlarmState.Plc.FaultAbortPresent, afterRejected.AlarmState.Plc.FaultAbortPresent);
        Assert.Equal(before.AlarmState.Plc.HiddenCount, afterRejected.AlarmState.Plc.HiddenCount);
        var historyAfter = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
            code: "DeviceFault", pageSize: 100));
        Assert.Equal(historyBefore.ThroughPosition, historyAfter.ThroughPosition);
        Assert.Equal(historyBefore.Records.ToArray(), historyAfter.Records.ToArray());
        Assert.Equal(next, runtime.NextAlarmObservationSequence("DeviceFault"));
        var integrityAfter = fixture.Store.Integrity;
        Assert.Equal(AuditIntegrityState.Verified, integrityAfter?.State);
        Assert.Equal(integrity?.State, integrityAfter?.State);
        Assert.Equal(integrity?.ThroughSequence, integrityAfter?.ThroughSequence);
        Assert.Equal(integrity?.VerifiedFromSequence, integrityAfter?.VerifiedFromSequence);
        Assert.Equal(integrity?.VerifiedThroughSequence, integrityAfter?.VerifiedThroughSequence);
        Assert.Equal(integrity?.CheckpointSequence, integrityAfter?.CheckpointSequence);
        Assert.Equal(integrity?.AnchoredSequence, integrityAfter?.AnchoredSequence);

        var nextFault = await ObserveWithSequenceAsync(fixture, "DeviceFault", "Camera.Primary",
            false, next);
        Assert.True(nextFault.Accepted, nextFault.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, nextFault.Audit);
        var nextHealthy = await ObserveWithSequenceAsync(fixture, "DeviceFault", "Camera.Primary",
            true, checked(next + 1));
        Assert.True(nextHealthy.Accepted, nextHealthy.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, nextHealthy.Audit);

        var final = await WaitForInstanceAsync(fixture.Runtime, "DeviceFault",
            instance => instance.SourceObservationSequence == next + 1 &&
                instance.Lifecycle == AlarmLifecycle.RecoveredLatched && instance.SourceHealthy);
        Assert.Equal(next + 1, final.SourceObservationSequence);
    }

    [Fact]
    public async Task V109_R10_PhysicalConsoleStopWinsOverQueuedAlarmObservationPressure()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        var snapshot = await WaitForSnapshotAsync(fixture.Runtime, state => state.AlarmState is not null);
        var sequence = runtime.NextAlarmObservationSequence("DeviceFault");
        var observation = new AlarmObservation(snapshot.RuntimeEpoch, sequence, "DeviceFault",
            "Camera.Primary", false, DateTimeOffset.UtcNow);

        var gate = CommandGate(runtime);
        await gate.WaitAsync();
        var gateHeld = true;
        try
        {
            var observations = Enumerable.Range(0, 16)
                .Select(_ => runtime.ObserveAlarmAsync(observation).AsTask()).ToArray();
            Assert.All(observations, task => Assert.False(task.IsCompleted));

            var stopCorrelation = Guid.NewGuid();
            var stopTask = fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(
                stopCorrelation, new CommandInvocation(CommandSource.PhysicalConsole))).AsTask();
            Assert.False(stopTask.IsCompleted);

            // Both queues are blocked behind the held production gate. Releasing it proves
            // the pending-local-stop check, rather than scheduler timing, chooses Stop.
            gate.Release();
            gateHeld = false;

            var stop = await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(stop.Disposition == CommandDisposition.Accepted,
                "Stop outcome: " + stop.ReasonCode);
            Assert.Equal("StopAdmitted", stop.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, stop.Audit);

            var disarmed = await WaitForSnapshotAsync(fixture.Runtime, state =>
                state.LastCommand is { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" });
            Assert.Equal(stopCorrelation, disarmed.LastCommand!.CorrelationId);

            var outcomes = await Task.WhenAll(observations).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.DoesNotContain(outcomes, outcome => outcome.ReasonCode == "AlarmQueueFull");
            Assert.Contains(outcomes, outcome => outcome.Accepted);
            Assert.All(outcomes, outcome => Assert.NotEqual(AuditPersistence.Unavailable, outcome.Audit));
            await fixture.WaitVerifiedAsync();

            var stopTrace = await fixture.TraceQuery.QueryAsync(new CommandTraceFilter(
                CorrelationId: stopCorrelation, PageSize: 20));
            var completedStop = Assert.Single(stopTrace.Records,
                record => record.Phase == CommandAuditPhase.Completed &&
                    record.ReasonCode == "LocallyDisarmed");
            var alarmHistory = await fixture.AlarmHistory.QueryAsync(new AlarmHistoryFilter(
                code: "DeviceFault", pageSize: 100));
            var raisedDeviceFault = Assert.Single(alarmHistory.Records,
                record => record.Code == "DeviceFault" &&
                    record.Transition == AlarmTransitionKind.Raised);
            var stopAuditSequence = ReadAuditSequence(fixture.DatabasePath, "FactPosition",
                completedStop.Position);
            var alarmAuditSequence = ReadAuditSequence(fixture.DatabasePath, "AlarmPosition",
                raisedDeviceFault.Position);
            Assert.True(stopAuditSequence < alarmAuditSequence,
                $"Stop.Completed audit sequence {stopAuditSequence} must precede " +
                $"DeviceFault.Raised audit sequence {alarmAuditSequence}.");
        }
        finally
        {
            if (gateHeld) gate.Release();
        }
    }

    [Fact]
    public async Task V109_R11_AlarmReadFailurePublishesUnavailableStateWithoutErasingEvidence()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        runtime.RegisterAlarmSource("Camera.Primary");
        await ObserveAcceptedAsync(fixture, "DeviceFault", "Camera.Primary", false);

        var before = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState?.Instances.Any(instance => instance.Code == "DeviceFault") == true);
        var beforeAlarm = before.AlarmState!;
        TamperPersistedAlarmPolicyHash(fixture.DatabasePath);

        var refresh = typeof(StationRuntime).GetMethod("RefreshAlarmsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refresh);
        var refreshTask = Assert.IsAssignableFrom<Task>(refresh!.Invoke(runtime,
            new object[] { CancellationToken.None }));
        await refreshTask;

        var unavailable = await WaitForSnapshotAsync(fixture.Runtime, state =>
            state.AlarmState is { Available: false, ReasonCode: "AlarmHistoryUnavailable" } &&
            state.AdmissionBlockers.Contains("AlarmAuthorityUnavailable"));
        Assert.True(unavailable.Revision > before.Revision);
        Assert.Equal(before.RuntimeEpoch, unavailable.RuntimeEpoch);
        Assert.Equal(unavailable.RuntimeEpoch, unavailable.AlarmState!.RuntimeEpoch);
        Assert.Equal(unavailable.Revision, unavailable.AlarmState.Revision);
        Assert.Equal(beforeAlarm.Instances.ToArray(), unavailable.AlarmState.Instances.ToArray());
        Assert.Equal(beforeAlarm.Plc.Entries.ToArray(), unavailable.AlarmState.Plc.Entries.ToArray());
        Assert.Equal(beforeAlarm.Plc.TotalUncleared, unavailable.AlarmState.Plc.TotalUncleared);
        Assert.Equal(beforeAlarm.Plc.BlockingCount, unavailable.AlarmState.Plc.BlockingCount);
        Assert.Equal(beforeAlarm.Plc.FaultAbortPresent, unavailable.AlarmState.Plc.FaultAbortPresent);
        Assert.Equal(beforeAlarm.Plc.HiddenCount, unavailable.AlarmState.Plc.HiddenCount);
        Assert.False(unavailable.Ready);
        Assert.Contains("TraceAuditUnavailable", unavailable.AdmissionBlockers);
        Assert.Contains("AlarmAuthorityUnavailable", unavailable.AdmissionBlockers);
    }

    [Fact]
    public async Task V109_R12_PhysicalConsoleStopHasDedicatedCapacityBeyondOrdinaryCommandLimit()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        var runtime = TrustedRuntime(fixture);
        var ordinaryCorrelations = Enumerable.Range(0, 64)
            .Select(_ => Guid.NewGuid()).ToArray();
        var ordinaryTasks = Array.Empty<Task<RuntimeCommandOutcome>>();
        var gate = CommandGate(runtime);
        await gate.WaitAsync();
        var gateHeld = true;
        try
        {
            ordinaryTasks = ordinaryCorrelations.Select(correlation =>
                fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(correlation,
                    new CommandInvocation(CommandSource.Integration))).AsTask()).ToArray();
            Assert.All(ordinaryTasks, task => Assert.False(task.IsCompleted));

            var stopCorrelation = Guid.NewGuid();
            var stopTask = fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(
                stopCorrelation, new CommandInvocation(CommandSource.PhysicalConsole))).AsTask();
            Assert.False(stopTask.IsCompleted);

            // Keep the actual gate held until all 64 ordinary slots and the dedicated local
            // slot are admitted. Releasing it makes the runtime's pending-local-stop yield
            // path, rather than scheduler timing, decide which request reaches the writer.
            gate.Release();
            gateHeld = false;

            var stop = await stopTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(stop.Disposition == CommandDisposition.Accepted, stop.ReasonCode);
            Assert.Equal("StopAdmitted", stop.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, stop.Audit);

            var disarmed = await WaitForSnapshotAsync(fixture.Runtime, state =>
                state.LastCommand is { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" });
            Assert.Equal(stopCorrelation, disarmed.LastCommand!.CorrelationId);

            var ordinaryOutcomes = await Task.WhenAll(ordinaryTasks)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(ordinaryOutcomes, outcome => outcome.ReasonCode == "CommandQueueFull");
            await fixture.WaitVerifiedAsync();

            // Once the verifier is settled, persist one ordinary rejection so the global
            // sequence comparison remains non-vacuous even when the saturated batch expires
            // before it can obtain a durable Outcome.
            var probeCorrelation = Guid.NewGuid();
            var probe = await fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(
                probeCorrelation, new CommandInvocation(CommandSource.Integration)));
            Assert.Equal(CommandDisposition.Rejected, probe.Disposition);
            Assert.Equal("LocalConsoleRequired", probe.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, probe.Audit);
            await fixture.WaitVerifiedAsync();

            var trace = await fixture.TraceQuery.QueryAsync(new CommandTraceFilter(PageSize: 200));
            var completedStop = Assert.Single(trace.Records,
                record => record.CorrelationId == stopCorrelation &&
                    record.Phase == CommandAuditPhase.Completed &&
                    record.ReasonCode == "LocallyDisarmed");
            var ordinaryCorrelationSet = ordinaryCorrelations.Append(probeCorrelation).ToHashSet();
            var ordinaryTraceOutcomes = trace.Records.Where(record =>
                ordinaryCorrelationSet.Contains(record.CorrelationId) &&
                record.Phase == CommandAuditPhase.Outcome).ToArray();
            // Ordinary requests may legitimately expire while waiting behind the bounded
            // gate or the store verifier. Compare every Outcome that was durably written;
            // a deadline rejection has no audit position to compare and must not fail Stop.
            var stopAuditSequence = ReadAuditSequence(fixture.DatabasePath, "FactPosition",
                completedStop.Position);
            foreach (var ordinaryOutcome in ordinaryTraceOutcomes)
            {
                var ordinaryAuditSequence = ReadAuditSequence(fixture.DatabasePath, "FactPosition",
                    ordinaryOutcome.Position);
                Assert.True(stopAuditSequence < ordinaryAuditSequence,
                    $"Stop.Completed audit sequence {stopAuditSequence} must precede " +
                    $"ordinary Outcome {ordinaryOutcome.CorrelationId} sequence {ordinaryAuditSequence}.");
            }
        }
        finally
        {
            if (gateHeld) gate.Release();
            try
            {
                await Task.WhenAll(ordinaryTasks).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
                // Preserve the primary assertion while allowing the fixture to dispose cleanly.
            }
        }
    }

    [Fact]
    public async Task V109_R13_LocalStopDuringAuditRecheckStillVerifiesTheActualSignedChain()
    {
        await using var fixture = await AlarmAcceptanceFixture.CreateAsync(TimeSpan.FromSeconds(5));
        await WaitForSnapshotAsync(fixture.Runtime, state => state.AlarmState?.Instances.Count > 0);
        await fixture.WaitVerifiedAsync();

        // Freeze only the advisory monitor, leaving the real writer and signed database live.
        // This deterministically represents a just-committed startup event awaiting its poll.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var lifetime = Assert.IsType<CancellationTokenSource>(typeof(SqliteCommandStore)
            .GetField("_integrityLifetime", flags)!.GetValue(fixture.Store));
        lifetime.Cancel();
        var monitor = Assert.IsAssignableFrom<Task>(typeof(SqliteCommandStore)
            .GetField("_integrityMonitor", flags)!.GetValue(fixture.Store));
        await monitor.WaitAsync(TimeSpan.FromSeconds(5));
        typeof(SqliteCommandStore).GetField("_integrity", flags)!.SetValue(fixture.Store,
            SqliteAuditIntegrityQuery.Report(fixture.AuditPolicy, AuditIntegrityState.Verifying, "AuditRecheckPending"));

        var ordinary = await fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.Integration)));
        Assert.Equal(AuditPersistence.Unavailable, ordinary.Audit);
        var correlation = Guid.NewGuid();
        var local = await fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(correlation,
            new CommandInvocation(CommandSource.PhysicalConsole)));
        Assert.True(local.Disposition == CommandDisposition.Accepted, local.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, local.Audit);
        await WaitForSnapshotAsync(fixture.Runtime, state => state.LastCommand is
            { State: OperationState.Completed, ReasonCode: "LocallyDisarmed" });
        var trace = await fixture.TraceQuery.QueryAsync(new CommandTraceFilter(CorrelationId: correlation));
        Assert.Equal(new[] { CommandAuditPhase.Outcome, CommandAuditPhase.Completed }, trace.Records.Select(item => item.Phase));

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var tamper = connection.CreateCommand();
            tamper.CommandText = "DROP TRIGGER alarm_events_immutable_update; " +
                "UPDATE alarm_events SET PayloadHash=printf('%064d',0) WHERE Position=1; " +
                "CREATE TRIGGER alarm_events_immutable_update BEFORE UPDATE ON alarm_events BEGIN SELECT RAISE(ABORT,'ImmutableAlarmEvent'); END;";
            tamper.ExecuteNonQuery();
        }
        var rejected = await fixture.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        Assert.Equal(AuditPersistence.Unavailable, rejected.Audit);
        Assert.Equal(AuditIntegrityState.Faulted, fixture.Store.Integrity!.State);
    }

    private static StationRuntime TrustedRuntime(AlarmAcceptanceFixture fixture) =>
        Assert.IsType<StationRuntime>(fixture.Runtime);

    private static SemaphoreSlim CommandGate(StationRuntime runtime)
    {
        var field = typeof(StationRuntime).GetField("_commandGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<SemaphoreSlim>(field!.GetValue(runtime));
    }

    private static long ReadAuditSequence(string databasePath, string positionColumn, long position)
    {
        Assert.Contains(positionColumn, new[] { "FactPosition", "AlarmPosition" });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Sequence FROM audit_entries WHERE {positionColumn} = $position;";
        command.Parameters.AddWithValue("$position", position);
        var value = command.ExecuteScalar();
        Assert.NotNull(value);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void TamperPersistedAlarmPolicyHash(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DROP TRIGGER alarm_policy_immutable_update;";
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE alarm_policy_versions SET ContentHash = $hash;";
        command.Parameters.AddWithValue("$hash", new string('0', 64));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static async Task<AlarmObservationOutcome> ObserveAsync(AlarmAcceptanceFixture fixture,
        string code, string source, bool healthy, DateTimeOffset? observedAtUtc = null)
    {
        var runtime = TrustedRuntime(fixture);
        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        var observation = new AlarmObservation(snapshot.RuntimeEpoch,
            runtime.NextAlarmObservationSequence(code), code, source, healthy,
            observedAtUtc ?? DateTimeOffset.UtcNow);
        var outcome = await runtime.ObserveAlarmAsync(observation);
        await fixture.WaitVerifiedAsync();
        return outcome;
    }

    private static async Task<AlarmObservationOutcome> ObserveWithSequenceAsync(
        AlarmAcceptanceFixture fixture, string code, string source, bool healthy, long sequence,
        DateTimeOffset? observedAtUtc = null)
    {
        var runtime = TrustedRuntime(fixture);
        var snapshot = await fixture.Runtime.GetSnapshotAsync();
        var observation = new AlarmObservation(snapshot.RuntimeEpoch, sequence, code, source, healthy,
            observedAtUtc ?? DateTimeOffset.UtcNow);
        var outcome = await runtime.ObserveAlarmAsync(observation);
        await fixture.WaitVerifiedAsync();
        return outcome;
    }

    private static async Task ObserveAcceptedAsync(AlarmAcceptanceFixture fixture,
        string code, string source, bool healthy, DateTimeOffset? observedAtUtc = null)
    {
        var outcome = await ObserveAsync(fixture, code, source, healthy, observedAtUtc);
        Assert.True(outcome.Accepted, outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
    }

    private static async Task<AlarmInstanceSnapshot> WaitForInstanceAsync(IStationRuntime runtime,
        string code, Func<AlarmInstanceSnapshot, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        var snapshot = await WaitForSnapshotAsync(runtime, state => state.AlarmState?.Instances.Any(instance =>
            instance.Code == code && (predicate is null || predicate(instance))) == true, timeout);
        return Assert.Single(snapshot.AlarmState!.Instances, instance =>
            instance.Code == code && (predicate is null || predicate(instance)));
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(IStationRuntime runtime,
        Func<StationStateSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        StationStateSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await runtime.GetSnapshotAsync();
            if (predicate(last)) return last;
            await Task.Delay(25);
        }

        throw new XunitException("Alarm snapshot condition timed out. Last reason: " +
            (last?.AlarmState?.ReasonCode ?? "AlarmStateMissing"));
    }

    private static async Task<AlarmActor> SignInAsync(AlarmAcceptanceFixture fixture,
        string userName, string password)
    {
        var result = await fixture.Sessions.SignInAsync(new PasswordSignInRequest(userName, password));
        using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!result.Succeeded && result.ReasonCode == "SessionRevocationPending")
        {
            await Task.Delay(25, retryTimeout.Token);
            result = await fixture.Sessions.SignInAsync(
                new PasswordSignInRequest(userName, password), retryTimeout.Token);
        }

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.NotNull(result.Identity);
        Assert.True(result.Session.SessionId.HasValue);
        await fixture.WaitVerifiedAsync();
        return new(result.Identity!.PrincipalId, result.Session.SessionId!.Value, userName, password);
    }

    private static CommandInvocation Invocation(AlarmActor actor, Guid? grant = null) =>
        new(CommandSource.PhysicalConsole, actor.PrincipalId.ToString("D"), actor.SessionId, grant);

    private static async Task<(Guid CorrelationId, Guid GrantId)> IssueResetGrantAsync(
        AlarmAcceptanceFixture fixture, AlarmActor actor, Guid instanceId)
    {
        var correlation = Guid.NewGuid();
        var request = new StepUpRequest(correlation, Invocation(actor),
            new StepUpBinding(Permission.ResetAlarm, correlation, instanceId.ToString("D"),
                AuditedCommandKind.ResetAlarm), actor.Password);
        var result = await fixture.StepUp.ReauthenticateAsync(request);
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(result.GrantId.HasValue);
        await fixture.WaitVerifiedAsync();
        return (correlation, result.GrantId!.Value);
    }

    private static async Task<AlarmActor> CreateOperatorAsync(AlarmAcceptanceFixture fixture,
        AlarmActor administrator)
    {
        var principalId = Guid.NewGuid();
        var userName = "alarm-operator-" + Guid.NewGuid().ToString("N")[..8];
        var password = "Alarm operator account secret 2026!";
        var correlation = Guid.NewGuid();
        var command = new CreateHumanAccountCommand(correlation, Invocation(administrator), principalId,
            userName, "Alarm Operator", password, HumanRoleBundle.Operator);
        var request = new StepUpRequest(correlation, Invocation(administrator),
            new StepUpBinding(Permission.ManageAccounts, correlation, principalId.ToString("D"),
                AuditedCommandKind.CreateHumanAccount), administrator.Password);
        var stepUp = await fixture.StepUp.ReauthenticateAsync(request);
        Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
        Assert.True(stepUp.GrantId.HasValue);
        var created = await fixture.Runtime.SubmitAsync(command with
        {
            Invocation = Invocation(administrator, stepUp.GrantId)
        });
        await fixture.WaitVerifiedAsync();
        Assert.Equal(CommandDisposition.Accepted, created.Disposition);
        return await SignInAsync(fixture, userName, password);
    }

    private sealed record AlarmActor(Guid PrincipalId, Guid SessionId, string UserName, string Password);
}

internal sealed class AlarmAcceptanceFixture : IAsyncDisposable
{
    private ServiceProvider? _provider;

    private AlarmAcceptanceFixture(string directoryPath, string databasePath, string stationId,
        string bootstrapUserName, string bootstrapPassword, AuditIntegrityPolicy auditPolicy,
        LocalIdentityOptions identityOptions, ProductionStoreOptions options, AlarmPolicy alarmPolicy)
    {
        DirectoryPath = directoryPath;
        DatabasePath = databasePath;
        StationId = stationId;
        BootstrapUserName = bootstrapUserName;
        BootstrapPassword = bootstrapPassword;
        AuditPolicy = auditPolicy;
        IdentityOptions = identityOptions;
        Options = options;
        AlarmPolicy = alarmPolicy;
    }

    public string DirectoryPath { get; }
    public string DatabasePath { get; }
    public string StationId { get; }
    public string BootstrapUserName { get; }
    public string BootstrapPassword { get; }
    public AuditIntegrityPolicy AuditPolicy { get; }
    public LocalIdentityOptions IdentityOptions { get; }
    public ProductionStoreOptions Options { get; }
    public AlarmPolicy AlarmPolicy { get; }
    public SqliteCommandStore Store { get; private set; } = null!;
    public IIdentityProvider Identity { get; private set; } = null!;
    public IInteractiveSessionService Sessions { get; private set; } = null!;
    public IStepUpAuthentication StepUp { get; private set; } = null!;
    public LocalAuthorizationService Authorization { get; private set; } = null!;
    public IIdentityAdministrationQuery AuthorizationQuery { get; private set; } = null!;
    public IStationRuntime Runtime { get; private set; } = null!;
    public IAlarmHistoryQuery AlarmHistory { get; private set; } = null!;
    public ICommandTraceQuery TraceQuery { get; private set; } = null!;

    public static async Task<AlarmAcceptanceFixture> CreateAsync(TimeSpan sourceFreshness)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Alarm Runtime acceptance requires Windows DPAPI machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V109-Alarms",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stationId = "V109AlarmStation";
        var alarmPolicy = CreateAlarmPolicy(sourceFreshness);
        var auditPolicy = new AuditIntegrityPolicy(stationId, "v1",
            "SharpInspect.Test.V109.Alarm." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "audit-keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identityOptions = new LocalIdentityOptions(stationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v109-alarm-blocklist", "v1",
                    new[] { "known-compromised-value" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(Path.Combine(directory, "alarms.sqlite"))
        {
            AuditIntegrityPolicy = auditPolicy,
            LocalIdentity = identityOptions,
            AlarmPolicy = alarmPolicy,
            CommitTimeout = TimeSpan.FromSeconds(2),
            QueryTimeout = TimeSpan.FromSeconds(2),
            QueueCapacity = 16
        };
        var fixture = new AlarmAcceptanceFixture(directory, options.DatabasePath, stationId,
            "alarm-bootstrap", "V109 bootstrap administrator secret 2026!", auditPolicy,
            identityOptions, options, alarmPolicy);
        await fixture.BuildProviderAsync(resolveRuntime: false);
        await fixture.BootstrapAsync();
        fixture.ResolveRuntimeServices();
        return fixture;
    }

    private static AlarmPolicy CreateAlarmPolicy(TimeSpan sourceFreshness) => new(
        "v109-alarm-policy", "development-v1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery", AlarmSeverity.Warning,
                ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null, 100,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("DeviceFault", "Camera.Primary", AlarmSeverity.Info,
                ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, 71, 200,
                AlarmResetPrerequisites.None),
            new AlarmPolicyRule("Notice", "Device.Status", AlarmSeverity.Critical,
                ProductionImpact.None, false, AlarmNotification.UntilAcknowledged, null)
        }, sourceFreshness, maximumActiveInstances: 16, maximumPlcEntries: 1);

    private async Task BuildProviderAsync(bool resolveRuntime = true)
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(Options, TimeSpan.FromMilliseconds(50));
        _provider = services.BuildServiceProvider();
        Store = _provider.GetRequiredService<SqliteCommandStore>();
        var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await WaitVerifiedAsync();

        Identity = _provider.GetRequiredService<IIdentityProvider>();
        Sessions = _provider.GetRequiredService<IInteractiveSessionService>();
        StepUp = _provider.GetRequiredService<IStepUpAuthentication>();
        Authorization = _provider.GetRequiredService<LocalAuthorizationService>();
        AuthorizationQuery = _provider.GetRequiredService<IIdentityAdministrationQuery>();
        if (resolveRuntime) ResolveRuntimeServices();
    }

    private void ResolveRuntimeServices()
    {
        if (_provider is null) throw new InvalidOperationException("AlarmProviderUnavailable");
        Runtime = _provider.GetRequiredService<IStationRuntime>();
        AlarmHistory = _provider.GetRequiredService<IAlarmHistoryQuery>();
        TraceQuery = _provider.GetRequiredService<ICommandTraceQuery>();
    }

    private async Task BootstrapAsync()
    {
        var bootstrap = new LocalIdentityService(Store, IdentityOptions, new ControlledConsoleAuthority());
        var tokenResult = await bootstrap.ProvisionBootstrapTokenAsync();
        Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
        var token = tokenResult.Token!.TakeForDisplay();
        await WaitVerifiedAsync();
        var created = await bootstrap.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
            StationId, token, BootstrapUserName, "V109 Alarm Administrator", BootstrapPassword));
        Assert.True(created.Succeeded, created.ReasonCode);
        created.RecoveryKit?.Dispose();
        await WaitVerifiedAsync();
    }

    public async Task RestartAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
            _provider = null;
        }

        await BuildProviderAsync();
    }

    public async Task<AuditIntegrityReport> WaitVerifiedAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var report = Store.Integrity;
            if (report is { State: AuditIntegrityState.Verified }) return report;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException("Audit integrity faulted: " + report.ReasonCode);
            await Task.Delay(25);
        }

        throw new XunitException("Audit integrity did not become Verified. Last state: " +
            Store.Integrity?.State + ", reason: " + Store.Integrity?.ReasonCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        try
        {
            var keyPath = WindowsMachineAuditKey.GetKeyPath(AuditPolicy);
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private sealed class ControlledConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V109-ALARM-TEST");
    }
}

#pragma warning restore CA1416
