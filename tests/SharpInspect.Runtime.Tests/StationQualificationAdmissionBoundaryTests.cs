using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V137_S04_InsufficientRestorationRowsRejectsStartBeforeFacilityOpen()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            ledgerOptions: new StationQualificationStoreOptions { MaximumEntries = 2 });
        var result = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Contains("Capacity", result.ReasonCode, StringComparison.Ordinal);
        Assert.Equal(0, harness.Facility.OpenCount);
        var page = await harness.History.QueryAsync(new());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task V137_S05_InsufficientRestorationBytesRejectsStartBeforeFacilityOpen()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            ledgerOptions: new StationQualificationStoreOptions
            {
                MaximumPayloadBytes = 1024, MaximumTotalBytes = 1024
            });
        var result = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(0, harness.Facility.OpenCount);
        var page = await harness.History.QueryAsync(new());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task V137_S06_CapacityReachedDuringPreparationStillHasDurableRestorationTail()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            ledgerOptions: new StationQualificationStoreOptions { MaximumEntries = 6 });
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "bounded qualification admission");
        var terminal = await harness.WaitForSnapshotAsync(value =>
            value.Phase is StationQualificationSessionPhase.Closed or StationQualificationSessionPhase.RecoveryBlocked,
            "bounded qualification did not retire");
        Assert.Equal(StationQualificationSessionPhase.Closed, terminal.Phase);
        Assert.Equal(StationQualificationRestorationState.Restored, terminal.Restoration);
        Assert.Equal(0, harness.Facility.StimulusCount);
        var page = await harness.History.QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.InRange(page.Events.Count, 1, 6);
        Assert.True(page.Events[^1].Terminal);
    }

    [Fact]
    public async Task V137_R18_ProductionFacilityCannotConsumeDevelopmentActivation()
    {
        await using var harness = await QualificationHarness.CreateAsync(developmentFacility: false);
        var result = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(0, harness.Facility.OpenCount);
        var current = await new SqliteRecipeActivationQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
    }

    [Fact]
    public async Task V137_R19_SupersededTargetRejectsStartBeforeFacilityIo()
    {
        await using var harness = await QualificationHarness.CreateAsync();
        var replacement = await harness.ActivateTargetAgainAsync();
        AssertAccepted(replacement.Outcome, "replace qualification target activation");
        Assert.NotEqual(harness.Plan.TargetActivation, replacement.Record?.Reference);
        var rejected = await harness.StartWithFreshStepUpAsync();
        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Equal(0, harness.Facility.OpenCount);
    }

    [Fact]
    public async Task V137_R21_ClosedSessionAllowsNewSessionWithSeparateRunIdentity()
    {
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "first qualification session");
        var first = await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.Closed, "first session did not close");
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "second qualification session");
        var second = await harness.WaitForSnapshotAsync(value =>
            value.SessionId != first.SessionId && value.Phase == StationQualificationSessionPhase.Closed,
            "second session did not close");
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.LastRunId, second.LastRunId);
        var page = await harness.History.QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(2, page.Runs.Count);
        Assert.Equal(2, page.Events.Count(value => value.Terminal));
        Assert.All(page.Runs, value => Assert.Equal(ExecutionStatus.Success, value.ExecutionStatus));
    }
}
