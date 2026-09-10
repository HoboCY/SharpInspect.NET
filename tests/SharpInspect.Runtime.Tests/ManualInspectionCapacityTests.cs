using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V135_Q08_EntryBudgetRejectsStartBeforeOpeningCameraOrWritingManualEvent()
    {
        await using var harness = await ManualHarness.CreateAsync(
            manualStoreOptions: new ManualInspectionStoreOptions { MaximumEntries = 2 });
        var before = harness.CameraProvider.GetDiagnostics().Devices.Single();

        var start = await harness.StartAsync();

        Assert.Equal(CommandDisposition.Rejected, start.Disposition);
        Assert.Equal("ManualInspectionEntryCapacityExceeded", start.ReasonCode);
        Assert.Equal(0L, await harness.Fixture.ScalarAsync(
            "SELECT COUNT(*) FROM manual_inspection_events;"));
        var after = harness.CameraProvider.GetDiagnostics().Devices.Single();
        Assert.Equal(before.OpenCount, after.OpenCount);
        Assert.Equal(before.IsOpen, after.IsOpen);
    }

    [Fact]
    public async Task V135_Q09_TotalByteBudgetIncludesStartAndColdRecoveryReserve()
    {
        const int maximumPayloadBytes = 64 * 1024;
        // Start reserves three future session rows for cold recovery. Keeping
        // the total budget at one payload must therefore reject admission
        // before the first Manual ledger row is written.
        await using var harness = await ManualHarness.CreateAsync(
            manualStoreOptions: new ManualInspectionStoreOptions
            {
                MaximumPayloadBytes = maximumPayloadBytes,
                MaximumTotalBytes = maximumPayloadBytes
            });
        var before = harness.CameraProvider.GetDiagnostics().Devices.Single();

        var start = await harness.StartAsync();

        Assert.Equal(CommandDisposition.Rejected, start.Disposition);
        Assert.Equal("ManualInspectionTotalCapacityExceeded", start.ReasonCode);
        Assert.Equal(0L, await harness.Fixture.ScalarAsync(
            "SELECT COUNT(*) FROM manual_inspection_events;"));
        var after = harness.CameraProvider.GetDiagnostics().Devices.Single();
        Assert.Equal(before.OpenCount, after.OpenCount);
        Assert.Equal(before.IsOpen, after.IsOpen);
    }

    [Fact]
    public async Task V135_Q10_AuditBudgetRejectsStartWhenOnlyControlReserveRemains()
    {
        await using var harness = await ManualHarness.CreateAsync(maximumAuditEntries: 256);
        await ExhaustGenericAuditCapacityAsync(harness);
        var before = harness.CameraProvider.GetDiagnostics().Devices.Single();

        var start = await harness.StartAsync();

        Assert.Equal(CommandDisposition.Rejected, start.Disposition);
        Assert.Equal("ManualInspectionAuditCapacityExceeded", start.ReasonCode);
        Assert.Equal(0L, await harness.Fixture.ScalarAsync(
            "SELECT COUNT(*) FROM manual_inspection_events;"));
        var after = harness.CameraProvider.GetDiagnostics().Devices.Single();
        Assert.Equal(before.OpenCount, after.OpenCount);
        Assert.Equal(before.IsOpen, after.IsOpen);
    }

    [Fact]
    public async Task V135_Q11_AuditReserveKeepsGracefulExitAdmissibleAfterGenericBudgetIsExhausted()
    {
        await using var harness = await ManualHarness.CreateAsync(maximumAuditEntries: 256);
        AssertAccepted(await harness.StartAsync(), "Capacity reserve start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Capacity reserve session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);

        var exhausted = await ExhaustGenericAuditCapacityAsync(harness);
        Assert.Contains("CapacityExceeded", exhausted.ReasonCode, StringComparison.Ordinal);

        var exit = await harness.ExitAsync(sessionId, ManualInspectionExitMode.Graceful);
        AssertAccepted(exit, "Capacity reserve graceful exit");
        var closed = await harness.WaitForSnapshotAsync(value =>
            value.SessionId == sessionId && value.Phase == ManualInspectionSessionPhase.Closed,
            "Capacity reserve graceful exit did not close");
        Assert.Equal(ManualInspectionRestorationState.NoActiveBaselineClosed, closed.Restoration);
        Assert.False(closed.RecoveryRequired);
    }

    private static async Task<StoreWriteResult> ExhaustGenericAuditCapacityAsync(ManualHarness harness)
    {
        StoreWriteResult? exhausted = null;
        for (var index = 0; index < 512; index++)
        {
            await harness.Fixture.WaitForVerifiedAsync();
            var result = await harness.Fixture.Store.AppendAsync(PaddingFact(),
                new StoreDeadline(harness.Fixture.Options.CommitTimeout));
            if (!result.Committed)
            {
                exhausted = result;
                break;
            }
        }

        Assert.NotNull(exhausted);
        Assert.Contains("CapacityExceeded", exhausted!.ReasonCode, StringComparison.Ordinal);
        return exhausted!;
    }

    private static CommandAuditFact PaddingFact() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        AuditedCommandKind.ArmProduction, CommandSource.PhysicalConsole,
        "V135-manual-budget-padding", null, null, CommandAuditPhase.Outcome,
        CommandDisposition.Rejected, "V135ManualBudgetPadding");
}
