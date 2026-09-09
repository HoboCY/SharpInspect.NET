using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeActivationPhysicalClaimTests
{
    [Fact]
    public async Task V132_P01_ClaimSurvivesStopButNextPhysicalPhaseIsCancelled()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(),
            TimeSpan.FromMilliseconds(20));
        using var reservation = await runtime.ReserveRecipeActivationAsync(
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(reservation.Available, reservation.Failure);

        using var current = reservation.TryBeginPhysicalPhase();
        Assert.True(current.Available, current.Failure);
        Assert.NotEqual(0, current.PhaseId);

        var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(
            Guid.NewGuid(), new CommandInvocation(CommandSource.PhysicalConsole)))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CommandDisposition.Accepted, stop.Disposition);

        // Stop establishes the synchronous blocker, while the already admitted
        // provider phase is allowed to finish and release its matching claim.
        Assert.True(current.Available);
        using var next = reservation.TryBeginPhysicalPhase();
        Assert.False(next.Available);
        Assert.Equal("RecipeActivationCancelled", next.Failure);
    }

    [Fact]
    public async Task V132_P02_StaleClaimCannotClearSuccessorPhysicalPhase()
    {
        await using var runtime = new StationRuntime(new ProbeAuditWriter(),
            TimeSpan.FromMilliseconds(20));
        var firstReservation = await runtime.ReserveRecipeActivationAsync(
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(firstReservation.Available, firstReservation.Failure);
        var stale = firstReservation.TryBeginPhysicalPhase();
        Assert.True(stale.Available, stale.Failure);

        // Retire the reservation while its old phase object is still alive. A
        // successor may then be admitted, so its phase id must be independent.
        firstReservation.PublishTerminal("RecipeActivationPhysicalClaimFixtureFinished", false);
        firstReservation.Dispose();
        using var successor = await runtime.ReserveRecipeActivationAsync(
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(successor.Available, successor.Failure);
        using var current = successor.TryBeginPhysicalPhase();
        Assert.True(current.Available, current.Failure);

        stale.Dispose();
        using var blocked = successor.TryBeginPhysicalPhase();
        Assert.False(blocked.Available);
        Assert.Equal("RecipeActivationPhysicalPhaseInProgress", blocked.Failure);
    }
}
