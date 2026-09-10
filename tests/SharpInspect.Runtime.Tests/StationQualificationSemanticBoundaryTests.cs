using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V137_S07_MissingQualificationDestinationIsRejectedWithoutCursorAdvance()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        var ready = await StartQualificationAtReadyAsync(harness);

        var before = await LastQualificationEventAsync(harness, ready.SessionId);
        var missingDestination = CreateQualificationObservation(ready,
            checked((ready.Observation?.Sequence ?? 0) + 1), destinationCount: 4);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.ReadyForStimulus, missingDestination);
        Assert.Equal("QualificationObservationDestinationSetIncomplete", rejected.Outcome.ReasonCode);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, ready.SessionId));
        await AbortQualificationAsync(harness, ready.SessionId);
    }

    [Fact]
    public async Task V137_S08_DuplicateQualificationObservationSequenceIsRejectedWithoutCursorAdvance()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        var ready = await StartQualificationAtReadyAsync(harness);

        var before = await LastQualificationEventAsync(harness, ready.SessionId);
        var duplicate = CreateQualificationObservation(ready,
            ready.Observation?.Sequence ?? 1, destinationCount: 5);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.ReadyForStimulus, duplicate);
        Assert.Equal("QualificationObservationSequenceNotIncreasing", rejected.Outcome.ReasonCode);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, ready.SessionId));
        await AbortQualificationAsync(harness, ready.SessionId);
    }

    [Fact]
    public async Task V137_S09_UnsafeQualificationObservationIsRejectedWithoutCursorAdvance()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        var ready = await StartQualificationAtReadyAsync(harness);

        var before = await LastQualificationEventAsync(harness, ready.SessionId);
        var unsafeObservation = CreateQualificationObservation(ready,
            checked((ready.Observation?.Sequence ?? 0) + 1), destinationCount: 5,
            lineStopped: false);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.ReadyForStimulus, unsafeObservation);
        Assert.Equal("QualificationObservationLineNotStopped", rejected.Outcome.ReasonCode);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, ready.SessionId));
        await AbortQualificationAsync(harness, ready.SessionId);
    }

    [Fact]
    public async Task V137_S10_UndeclaredQualificationScenarioCannotAdmitRun()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        var ready = await StartQualificationAtReadyAsync(harness);

        var before = await LastQualificationEventAsync(harness, ready.SessionId);
        var observation = CreateQualificationObservation(ready,
            checked((ready.Observation?.Sequence ?? 0) + 1), destinationCount: 5);
        var run = CreateAdmittedRun(ready, "V137.Scenario.NotDeclared", 1, 1);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.Running, observation, run);
        Assert.Equal("StationQualificationRunScenarioMismatch", rejected.Outcome.ReasonCode);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, ready.SessionId));
        await AbortQualificationAsync(harness, ready.SessionId);
    }

    [Fact]
    public async Task V137_S11_ZeroControllerOrCycleCannotAdmitRun()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        var ready = await StartQualificationAtReadyAsync(harness);

        var before = await LastQualificationEventAsync(harness, ready.SessionId);
        var observation = CreateQualificationObservation(ready,
            checked((ready.Observation?.Sequence ?? 0) + 1), destinationCount: 5);
        var run = CreateAdmittedRun(ready, ready.Header.Plan.ScenarioIds[0], 0, 0);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.Running, observation, run);
        Assert.Equal("StationQualificationRunControllerEpochInvalid", rejected.Outcome.ReasonCode);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, ready.SessionId));
        await AbortQualificationAsync(harness, ready.SessionId);
    }

    [Fact]
    public async Task V137_S12_SuccessWithoutResultFrameOrPayloadCannotFinalizeAdmittedRun()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockWrite: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification semantic success-boundary start");
        await harness.Facility.WriteEntered.WaitAsync(TimeSpan.FromSeconds(15));
        var running = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Running &&
            snapshot.CurrentRunId is not null,
            "qualification semantic run was not admitted");
        var sessionId = Assert.IsType<Guid>(running.SessionId);
        var before = await LastQualificationEventAsync(harness, sessionId);
        var admitted = Assert.IsType<StationQualificationRunRecord>(before.Run);
        var incompleteSuccess = new StationQualificationRunRecord(
            admitted.Position, admitted.RunId, admitted.SessionId, admitted.StimulusSequence,
            admitted.ScenarioId, admitted.ContextHash, admitted.ControllerEpoch,
            admitted.CycleSequence, admitted.AdmittedAtUtc, DateTimeOffset.UtcNow,
            ExecutionStatus.Success, InspectionDecision.Pass,
            "V137 success missing qualification evidence", null, null, null, null, null, null);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.Running, observation: null, run: incompleteSuccess);
        Assert.Equal("StationQualificationSuccessEvidenceIncomplete", rejected.Outcome.ReasonCode);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, sessionId));
        harness.Facility.ReleaseWrite();
        var terminal = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "qualification semantic success-boundary cleanup did not finish");
        Assert.Equal(StationQualificationSessionPhase.Closed, terminal.Phase);
    }

    [Fact]
    public async Task V137_S13_ClosedWithUnrestoredFlagsCannotCreateTerminalLedgerRow()
    {
        await using var harness = await QualificationHarness.CreateAsync(blockStimulus: true);
        var ready = await StartQualificationAtReadyAsync(harness);

        var before = await LastQualificationEventAsync(harness, ready.SessionId);
        var rejected = await AppendQualificationProgressAsync(harness, before,
            StationQualificationSessionPhase.Closed, observation: null, run: null,
            terminal: true, restoration: StationQualificationRestorationState.NotRequired,
            completeOriginalStart: true);

        AssertRejectedWithoutCursorAdvance(rejected, before,
            await LastQualificationEventAsync(harness, ready.SessionId));
        await AbortQualificationAsync(harness, ready.SessionId);
    }

    private static async Task<StationQualificationSessionEvent> StartQualificationAtReadyAsync(
        QualificationHarness harness)
    {
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "qualification semantic boundary start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "qualification semantic boundary session did not reach ReadyForStimulus");
        return await LastQualificationEventAsync(harness,
            Assert.IsType<Guid>(ready.SessionId));
    }

    private static async Task<StationQualificationSessionEvent> LastQualificationEventAsync(
        QualificationHarness harness, Guid sessionId)
    {
        var page = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        return Assert.IsType<StationQualificationSessionEvent>(
            page.Events.OrderBy(value => value.Position).LastOrDefault());
    }

    private static QualificationFacilityObservation CreateQualificationObservation(
        StationQualificationSessionEvent previous, long sequence, int destinationCount,
        bool lineStopped = true)
    {
        var plan = previous.Header.Plan;
        var destinations = plan.DestinationBindings.Take(destinationCount).Select(binding =>
            new QualificationDestinationObservation(binding.Kind, binding.DestinationId,
                binding.TargetBindingHash, QualificationDestinationRoute.IsolatedTest, false));
        return new QualificationFacilityObservation(plan.QualificationHarnessIdentity,
            previous.SessionId, previous.Header.LeaseNonce, previous.Header.RuntimeEpoch, sequence,
            DateTimeOffset.UtcNow, connected: true, lineStopped,
            plan.TransientControllerConfiguration.ContentHash, destinations);
    }

    private static StationQualificationRunRecord CreateAdmittedRun(
        StationQualificationSessionEvent previous, string scenarioId,
        uint controllerEpoch, uint cycleSequence) => new(
        previous.Position + 1, new QualificationRunId(Guid.NewGuid()), previous.SessionId,
        1, scenarioId, previous.Header.Plan.QualificationContextHash,
        controllerEpoch, cycleSequence, DateTimeOffset.UtcNow, null, null,
        InspectionDecision.Unknown, "StationQualificationRunAdmitted",
        null, null, null, null, null, null);

    private static async Task<StationQualificationTransactionResult> AppendQualificationProgressAsync(
        QualificationHarness harness, StationQualificationSessionEvent previous,
        StationQualificationSessionPhase phase, QualificationFacilityObservation? observation = null,
        StationQualificationRunRecord? run = null, bool terminal = false,
        StationQualificationRestorationState? restoration = null,
        bool completeOriginalStart = false)
    {
        var request = new StationQualificationProgressRequest(
            previous.Header, previous.Position, previous.ContentHash,
            previous.CommandCorrelationId, previous.AttemptId, previous.CommandKind,
            phase, restoration ?? previous.Restoration,
            phase == StationQualificationSessionPhase.ReadyForStimulus ? "StationQualificationReadyForStimulus" :
                run is { Completed: false } ? "StationQualificationRunAdmitted" :
                "V137 semantic boundary rejection", terminal, observation, run,
            CompleteOriginalStart: completeOriginalStart,
            CompleteCommand: false, RecoveryAttempt: previous.RecoveryAttempt,
            CommandAuthorizationTarget: previous.CommandAuthorizationTarget,
            AuthorizeProgress: harness.Fixture.Authorization.AuthorizeStationQualificationProgress);
        return await harness.Fixture.Store.AppendStationQualificationProgressAsync(request,
            new StoreDeadline(TimeSpan.FromSeconds(2)));
    }

    private static void AssertRejectedWithoutCursorAdvance(
        StationQualificationTransactionResult result,
        StationQualificationSessionEvent before,
        StationQualificationSessionEvent after)
    {
        Assert.False(result.Accepted, result.Outcome.ReasonCode);
        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.ContentHash, after.ContentHash);
    }

    private static async Task AbortQualificationAsync(QualificationHarness harness, Guid sessionId)
    {
        AssertAccepted(await harness.Runtime.SubmitAsync(
            harness.ExitCommand(sessionId, abort: true)),
            "qualification semantic boundary cleanup");
        await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase is StationQualificationSessionPhase.Closed or
                StationQualificationSessionPhase.RecoveryBlocked,
            "qualification semantic boundary cleanup did not finish");
    }
}
