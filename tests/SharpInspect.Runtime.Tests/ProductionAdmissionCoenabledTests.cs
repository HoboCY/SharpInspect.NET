using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V136_M01_Schema22PreservesManualExecutionAndIndependentHistoryReaders(bool minimal)
    {
        await using var harness = await ManualHarness.CreateAsync(minimalStore: minimal, productionAdmission: true);
        Assert.Equal(22, await harness.Fixture.ScalarAsync("PRAGMA user_version;"));
        var start = await harness.StartAsync();
        AssertAccepted(start, "schema22 Manual start");
        var ready = await harness.WaitForSnapshotAsync(snapshot => snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "schema22 Manual did not prepare");
        var session = ready.SessionId!.Value;
        AssertAccepted(await harness.RunAsync(session), "schema22 Manual run");
        await harness.WaitForSnapshotAsync(snapshot => snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "schema22 Manual run did not complete");
        AssertAccepted(await harness.ExitAsync(session, ManualInspectionExitMode.Graceful), "schema22 Manual exit");
        await harness.WaitForSnapshotAsync(snapshot => snapshot.Phase == ManualInspectionSessionPhase.Closed,
            "schema22 Manual did not close");
        var history = await new SqliteManualInspectionQuery(harness.Fixture.Options)
            .QueryAsync(new ManualInspectionHistoryFilter(SessionId: session));
        Assert.True(history.Available, history.ReasonCode);
        var run = Assert.Single(history.Runs);
        Assert.True(run.Terminal);
        Assert.Equal(ExecutionStatus.Success, run.ExecutionStatus);
        Assert.False(history.RecoveryRequired);
        var admission = await new SqliteProductionAdmissionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(admission.Available, admission.ReasonCode);
        Assert.Null(admission.Latest);
        var trace = await new SqliteCommandTraceQuery(harness.Fixture.Options)
            .QueryAsync(new CommandTraceFilter(CorrelationId: start.CorrelationId));
        Assert.NotEmpty(trace.Records);
        var integrity = await new SqliteAuditIntegrityQuery(harness.Fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 500));
        Assert.True(integrity.State == AuditIntegrityState.Verified, integrity.ReasonCode);
        if (!minimal)
        {
            var active = await harness.Activations.ReadCurrentAsync();
            Assert.True(active.Available, active.ReasonCode);
            Assert.Null(active.Record);
        }
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V136_M02_Schema22KeepsManualTerminalReserveWhenGenericAuditCapacityIsExhausted()
    {
        await using var harness = await ManualHarness.CreateAsync(maximumAuditEntries: 512, productionAdmission: true);
        AssertAccepted(await harness.StartAsync(), "schema22 reserve start");
        var ready = await harness.WaitForSnapshotAsync(snapshot => snapshot.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "schema22 reserve did not prepare");
        var exhausted = await ExhaustGenericAuditCapacityAsync(harness);
        Assert.Contains("CapacityExceeded", exhausted.ReasonCode, StringComparison.Ordinal);
        AssertAccepted(await harness.ExitAsync(ready.SessionId!.Value, ManualInspectionExitMode.Graceful),
            "schema22 reserved exit");
        var closed = await harness.WaitForSnapshotAsync(snapshot => snapshot.Phase == ManualInspectionSessionPhase.Closed,
            "schema22 reserved exit did not close");
        Assert.False(closed.RecoveryRequired);
    }
}
