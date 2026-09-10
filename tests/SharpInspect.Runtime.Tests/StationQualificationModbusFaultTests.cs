using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V140_R13_LateCommittedCoreCannotReopenPublication()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Fixture.Store.QualificationCoreCommitGuardDecorator = guard =>
            new DelayedQualificationCommitGuard(guard, entered, release);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "late core start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.ReadyForStimulus, "late core readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(53, 1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // This independent read must see the real committed Core while the
            // owning writer is held before returning its receipt to Runtime.
            var committed = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
                .QueryAsync(new(SessionId: sessionId, PageSize: 128));
            Assert.True(committed.Available, committed.ReasonCode);
            var core = Assert.Single(committed.Events,
                value => value.Kind == QualificationCycleEventKind.CoreCommitted);
            await Task.Delay(core.PolicySnapshot!.Policy.TraceCommitTimeout + TimeSpan.FromMilliseconds(100));
            Assert.DoesNotContain(controller.Writes, value => value.Function == 0x10 &&
                value.StartAddress != harness.ModbusProfile!.RuntimeStartAddress);
            Assert.Equal(0, controller.ResultValidHighCount);
        }
        finally { release.Set(); }
        await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked, "late core recovery barrier");
        await harness.WaitForHistoryAsync(sessionId, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "late core retirement journal");
        var cycles = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(cycles.Available, cycles.ReasonCode);
        Assert.Single(cycles.Events, value => value.Kind == QualificationCycleEventKind.CoreCommitted);
        Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.CycleFaultTerminated);
        Assert.DoesNotContain(cycles.Events, value => value.Kind == QualificationCycleEventKind.PublicationPrepared);
        Assert.DoesNotContain(controller.Writes, value => value.Function == 0x10 &&
            value.StartAddress != harness.ModbusProfile!.RuntimeStartAddress);
        Assert.Equal(0, controller.ResultValidHighCount);
        var connectionCount = controller.ConnectionCount;
        var writeCount = controller.Writes.Count;
        await using var cold = await harness.OpenColdScopeAsync();
        await cold.WaitForSnapshotAsync(value => value.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "cold late core recovery barrier");
        Assert.Equal(connectionCount, controller.ConnectionCount);
        Assert.Equal(writeCount, controller.Writes.Count);
        Assert.Equal(0, cold.Facility.OpenCount);
    }

    [Fact]
    public async Task V140_R12_CoreTransactionFailureNeverPublishesPayloadOrValid()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot));
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "core failure start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.ReadyForStimulus, "core failure readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Fail the actual SQLite Core insertion while permitting admission and
        // the independent fault tail. This is a transaction failure, not a
        // fabricated algorithm Unknown or a mocked persistence receipt.
        await using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = harness.Fixture.Options.DatabasePath, Pooling = false }.ToString()))
        {
            await database.OpenAsync();
            using var command = database.CreateCommand();
            command.CommandText = "CREATE TRIGGER v140_fail_core BEFORE INSERT ON qualification_cycle_events " +
                "WHEN NEW.Kind=" + (int)QualificationCycleEventKind.CoreCommitted +
                " BEGIN SELECT RAISE(ABORT,'V140InjectedCoreFailure'); END;";
            await command.ExecuteNonQueryAsync();
        }
        controller.RaiseTrigger(52, 1);
        await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked, "core failure recovery barrier");
        Assert.DoesNotContain(controller.Writes, value => value.Function == 0x10 &&
            value.StartAddress != harness.ModbusProfile!.RuntimeStartAddress);
        Assert.Equal(0, controller.ResultValidHighCount);
        var cycles = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options)
            .QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(cycles.Available, cycles.ReasonCode);
        Assert.Contains(cycles.Events, value => value.Kind == QualificationCycleEventKind.Admitted);
        Assert.DoesNotContain(cycles.Events, value => value.Kind is QualificationCycleEventKind.CoreCommitted or
            QualificationCycleEventKind.PublicationPrepared or QualificationCycleEventKind.ResultValidPublished);
        Assert.True(cycles.RecoveryRequired);
    }

    [Fact]
    public async Task V140_R10_ColdPendingCycleNeverReconnectsOrRestoresIsolation()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.AutoAcknowledge = false;
        controller.HoldFirstPayloadWrite = false;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            allowDisposeFailure: true,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                acknowledgementTimeout: TimeSpan.FromMilliseconds(250)));
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "cold pending start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.ReadyForStimulus, "cold pending readiness");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        await controller.WaitForReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        controller.RaiseTrigger(51, 1);
        await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked, "original acknowledgement timeout");
        await harness.WaitForHistoryAsync(sessionId, page => page.Events.Any(value =>
            value.ReasonCode == "QualificationModbusRecoveryRequired"), "original owner retirement journal");
        var before = await new SqliteQualificationCycleHistoryQuery(harness.Fixture.Options).ReadAsync(sessionId);
        Assert.True(before.Available, before.ReasonCode);
        Assert.True(before.RecoveryRequired);
        var connections = controller.ConnectionCount;
        var writes = controller.Writes.Count;

        await using var cold = await harness.OpenColdScopeAsync();
        var blocked = await cold.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.RecoveryBlocked, "cold pending recovery barrier");
        Assert.True(blocked.RecoveryRequired);
        Assert.Equal(0, cold.Facility.OpenCount);
        Assert.Equal(0, cold.Facility.RestoreCount);
        Assert.Equal(0, cold.Facility.ReleaseIsolationCount);
        Assert.Equal(connections, controller.ConnectionCount);
        Assert.Equal(writes, controller.Writes.Count);
        Assert.True(controller.RuntimeResultValid);
        var retained = await cold.History.QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(retained.Available, retained.ReasonCode);
        Assert.Single(retained.Runs);
        Assert.NotNull(retained.Runs.Single().QualificationPayload);
    }

    [Fact]
    public async Task V140_R11_ExitWhileInitialReadIsInFlightPreventsEveryWrite()
    {
        await using var controller = ModbusQualificationTestServer.Start();
        controller.HoldInitialRuntimeRead = true;
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1,
            profileFactory: snapshot => controller.CreateProfile(snapshot,
                transportTimeout: TimeSpan.FromSeconds(10)));
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "exit initial read start");
        await controller.WaitForInitialRuntimeReadAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var running = await harness.WaitForSnapshotAsync(value => value.SessionId is not null,
            "initial read session identity");
        var sessionId = Assert.IsType<Guid>(running.SessionId);
        try
        {
            var exit = await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, abort: false));
            AssertAccepted(exit, "exit during initial read");
            Assert.Empty(controller.Writes);
        }
        finally { controller.ReleaseInitialRuntimeRead(); }
        await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.Closed, "initial read exit did not retire");
        Assert.Empty(controller.Writes);
        Assert.Equal(0, controller.ReadyWriteCount);
        var history = await harness.History.QueryAsync(new(SessionId: sessionId, PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Runs);
    }
    private sealed class DelayedQualificationCommitGuard : IIdentityTransactionGuard
    {
        private readonly IIdentityTransactionGuard _inner;
        private readonly TaskCompletionSource<bool> _entered;
        private readonly ManualResetEventSlim _release;
        internal DelayedQualificationCommitGuard(IIdentityTransactionGuard inner,
            TaskCompletionSource<bool> entered, ManualResetEventSlim release)
        { _inner = inner; _entered = entered; _release = release; }
        public void Commit()
        {
            _inner.Commit();
            _entered.TrySetResult(true);
            _release.Wait(TimeSpan.FromSeconds(20));
        }
        public void Dispose() => _inner.Dispose();
    }
}
