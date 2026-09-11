using System.Reflection;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T46 regression for the busy-rejected PLC recipe change request. The observer latches
/// <c>StationRuntime._recipeChangeInProgress</c> after its atomic admission attempt and holds it
/// for the whole response/ACK/reset handshake. That latch may only refuse new configuration
/// admissions: an activation or map-selection reservation which already owned its hold when the
/// request was observed must keep its commit path, must never be revoked by the foreign
/// handshake, and must not be replaced by a second activation or a restoration.
///
/// The fixture writes the same private field the observer sets, so the assertion targets the
/// real blocker decision of the real station; the real Modbus wire observation (register
/// sequence, durable duplicate rejection) stays with the loopback transport tests. No audit
/// evidence, PLC register state or rejection decision is fabricated here.
/// </summary>
public sealed class RecipeChangeReservationRegressionTests
{
    [Fact]
    public async Task V146_B01_RejectedRecipeChangeHandshakeKeepsAdmittedActivationReservationAndRefusesNewEntrants()
    {
        // The local-authority activation takes exactly this reservation before it touches any
        // provider. The probe writer keeps the station boundary in scope without claiming
        // storage qualification.
        await using var runtime = new StationRuntime(new ProbeAuditWriter(), TimeSpan.FromMilliseconds(20));
        using var reservation = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);
        Assert.Null(reservation.GetBlocker());
        Assert.Contains("RecipeActivationInProgress", (await runtime.GetSnapshotAsync()).AdmissionBlockers);

        try
        {
            SetRecipeChangeLatch(runtime, active: true);

            // The admitted activation keeps its own blocker decision and its durable commit
            // fence; the rejected request may not steal either of them.
            Assert.Null(reservation.GetBlocker());
            string? claim;
            using (var commit = await reservation.EnterCommitAsync(CancellationToken.None))
            {
                Assert.Null(commit.GetBlocker());
                claim = commit.TryBeginCommit();
            }
            Assert.Null(claim);

            // No second activation may take the station while the admitted one owns the hold.
            using (var second = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None))
            {
                Assert.False(second.Available);
                Assert.Equal("RecipeActivationInProgress", second.Failure);
            }

            reservation.PublishTerminal("V146ActivationCommittedFixture", false);
            reservation.Dispose();

            // With the admitted reservation retired, the rejected request is the only
            // remaining blocker and still refuses every new entrant until its reset arrives.
            using var latched = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.False(latched.Available);
            Assert.Equal("RecipeChangeHandshakeInProgress", latched.Failure);
        }
        finally
        {
            SetRecipeChangeLatch(runtime, active: false);
        }

        // Releasing the reservation removed its published blocker, and the cleared latch
        // admits the next reservation without a recovery fence.
        Assert.DoesNotContain("RecipeActivationInProgress", (await runtime.GetSnapshotAsync()).AdmissionBlockers);
        using var released = await runtime.ReserveRecipeActivationAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.True(released.Available, released.Failure);
        released.PublishTerminal("V146ActivationFixtureReleased", false);
    }

    [Fact]
    public async Task V146_B02_RejectedRecipeChangeHandshakeDoesNotPreemptExistingMapSelectionReservation()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();

        // The real station reservation that a governed map change takes first, before the
        // identity writer rechecks the live blocker on the same reservation.
        using var reservation = await harness.Runtime.ReserveRecipeSelectionChangeAsync(CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);
        Assert.Null(reservation.GetBlocker());
        Assert.Contains("RecipeSelectionChangeInProgress",
            (await harness.Runtime.GetSnapshotAsync()).AdmissionBlockers);

        try
        {
            SetRecipeChangeLatch(harness.Runtime, active: true);

            // The in-flight map change keeps its own hold: a foreign handshake that owns no
            // reservation must not revoke a reservation taken before it was observed.
            Assert.Null(reservation.GetBlocker());

            using (var competingChange = await harness.Runtime.ReserveRecipeSelectionChangeAsync(
                CancellationToken.None))
            {
                Assert.False(competingChange.Available);
                Assert.Equal("RecipeSelectionChangeInProgress", competingChange.Failure);
            }
            using (var competingActivation = await harness.Runtime.ReserveRecipeActivationAsync(
                Guid.NewGuid(), CancellationToken.None))
            {
                Assert.False(competingActivation.Available);
                Assert.Equal("RecipeSelectionChangeInProgress", competingActivation.Failure);
            }

            reservation.Dispose();

            // The latch outlives the retired change and remains the only blocker, so it still
            // refuses new map changes and new activations exactly as designed.
            using (var latchedChange = await harness.Runtime.ReserveRecipeSelectionChangeAsync(
                CancellationToken.None))
            {
                Assert.False(latchedChange.Available);
                Assert.Equal("RecipeSelectionActivationConflict", latchedChange.Failure);
            }
            using (var latchedActivation = await harness.Runtime.ReserveRecipeActivationAsync(
                Guid.NewGuid(), CancellationToken.None))
            {
                Assert.False(latchedActivation.Available);
                Assert.Equal("RecipeChangeHandshakeInProgress", latchedActivation.Failure);
            }
        }
        finally
        {
            SetRecipeChangeLatch(harness.Runtime, active: false);
        }

        // The retired reservation published no Ready/hold residue and the cleared latch
        // admits the next map change.
        Assert.DoesNotContain("RecipeSelectionChangeInProgress",
            (await harness.Runtime.GetSnapshotAsync()).AdmissionBlockers);
        using var released = await harness.Runtime.ReserveRecipeSelectionChangeAsync(CancellationToken.None);
        Assert.True(released.Available, released.Failure);
        released.Dispose();
    }

    [Fact]
    public async Task V146_B03_InFlightHumanActivationCommitsUnderRejectedRecipeChangeHandshakeLatch()
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(
            enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();

        var applyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.CameraProvider.HoldActivationApply(() =>
        {
            applyEntered.TrySetResult(true);
            return applyRelease.Task;
        });

        // The local-authority service already owns the station reservation and durable
        // admission here; the hold is inside the candidate camera apply.
        var activation = harness.CreateFixtureService()
            .ActivateAsync(await harness.AuthorizedActivationCommand()).AsTask();
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        try
        {
            SetRecipeChangeLatch(harness.Runtime, active: true);

            using (var competingActivation = await harness.Runtime.ReserveRecipeActivationAsync(
                Guid.NewGuid(), CancellationToken.None))
            {
                Assert.False(competingActivation.Available);
                Assert.Equal("RecipeActivationInProgress", competingActivation.Failure);
            }
            using (var competingChange = await harness.Runtime.ReserveRecipeSelectionChangeAsync(
                CancellationToken.None))
            {
                Assert.False(competingChange.Available);
                Assert.Equal("RecipeSelectionActivationConflict", competingChange.Failure);
            }

            // Release the held candidate while the rejected request stays latched: the
            // activation that already owned the reservation must still commit.
            applyRelease.TrySetResult(true);
            var result = await activation.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
            Assert.Equal("RecipeActivated", result.Outcome.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
            var record = result.Record;
            Assert.NotNull(record);
            Assert.True(record!.Outcome.Succeeded);
            // No second activation attempt and no restoration reached the hardware.
            Assert.Equal(0, harness.CameraProvider.ReplacementApplyCalls);
            Assert.Equal(0, harness.CameraProvider.RestoreApplyCalls);
        }
        finally
        {
            applyRelease.TrySetResult(true);
            SetRecipeChangeLatch(harness.Runtime, active: false);
        }

        // Exactly one activation reached a terminal state, and neither the station nor the
        // activation history demanded a recovery.
        var page = await harness.ActivationHistory.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        var terminal = Assert.Single(page.Records, value => value.IsTerminal);
        Assert.True(terminal.Outcome.Succeeded);
        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.False(current.RecoveryRequired);
        Assert.Null(current.Record);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.False(station.Ready);
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.DoesNotContain("RecipeActivationRecoveryRequired", station.AdmissionBlockers);
        Assert.DoesNotContain("RecipeActivationInProgress", station.AdmissionBlockers);
    }

    /// <summary>
    /// Writes the observer latch that <c>BeginRecipeChange</c> sets after its atomic admission
    /// attempt, once a request was not granted a reservation (for example a busy rejection).
    /// This is the test fixture for that latch state; the real controller observation and the
    /// response/ACK/reset wire sequence are owned by the loopback transport tests.
    /// </summary>
    /// <remarks>
    /// Bounded follow-up cut-in for the adjacent "a stale request never installs a reservation"
    /// gate (not implemented here, because it needs the real controller sequence): drive a
    /// reused endpoint/epoch/request-sequence identity over the wire and assert that the
    /// duplicate decision is the durable <c>ProtocolFault</c>/<c>DuplicateRequest</c> record with
    /// no activation reference, that the reservation installed by the stale observation is
    /// retired by the duplicate observation, and that only the reset clears the latch so a new
    /// honest request is admitted again.
    /// </remarks>
    private static void SetRecipeChangeLatch(StationRuntime runtime, bool active)
    {
        var field = typeof(StationRuntime).GetField("_recipeChangeInProgress",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("StationRuntime._recipeChangeInProgress is unavailable.");
        field.SetValue(runtime, active);
        Assert.Equal(active, Assert.IsType<bool>(field.GetValue(runtime)));
    }
}
