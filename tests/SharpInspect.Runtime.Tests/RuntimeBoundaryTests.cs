using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RuntimeBoundaryTests
{
    private static StationRuntime CreateRuntime(TimeSpan? interval = null) => new(new ProbeAuditWriter(), interval);

    private static readonly CommandInvocation Console = new(CommandSource.PhysicalConsole);

    [Fact]
    public async Task V101_R01_ExplicitRegistrationSharesOneHeadlessAuthority()
    {
        var services = new ServiceCollection();
        services.AddSharpInspectRuntime();
        services.AddSharpInspectRuntime();
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        Assert.Same(runtime, provider.GetRequiredService<IStationRuntime>());
        foreach (var assembly in new[] { typeof(IStationRuntime).Assembly, typeof(StationRuntime).Assembly })
        {
            Assert.StartsWith("SharpInspect.", assembly.GetName().Name);
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a =>
                a.Name!.Contains("Wpf") || a.Name.Contains("Presentation") || a.Name.Contains("OpenCv") || a.Name.Contains("Hik"));
        }
    }

    [Fact]
    public async Task V101_R02_UnconfiguredStartupAndClaimedAdminCannotArm()
    {
        await using var runtime = CreateRuntime();
        var before = await runtime.GetSnapshotAsync();
        Assert.False(before.Ready);
        Assert.Equal(ProductionArmState.Disarmed, before.ArmState);
        Assert.Equal(HealthState.Unconfigured, before.Camera.Connection);
        Assert.Equal(QualificationMatch.Missing, before.Qualification.StationAcceptance);
        Assert.Contains("DeploymentPoliciesMissing", before.AdmissionBlockers);
        Assert.Contains("StartupRecoveryNotVerified", before.AdmissionBlockers);
        var id = Guid.NewGuid();
        var outcome = await runtime.SubmitAsync(new ArmProductionCommand(id,
            new CommandInvocation(CommandSource.PhysicalConsole, "Administrator", Guid.NewGuid(), Guid.NewGuid())));
        Assert.Equal(id, outcome.CorrelationId);
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("DeploymentPoliciesMissing", outcome.ReasonCode);
        Assert.Same(before, await runtime.GetSnapshotAsync());
    }

    [Fact]
    public async Task V101_R03_StopAcceptanceIsSeparateFromCorrelatedCompletion()
    {
        await using var runtime = CreateRuntime(TimeSpan.FromMilliseconds(100));
        var id = Guid.NewGuid();
        var outcome = await runtime.SubmitAsync(new GracefulProductionStopCommand(id, Console));
        Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
        Assert.Equal("StopAdmitted", outcome.ReasonCode);
        var admitted = await runtime.GetSnapshotAsync();
        Assert.Equal(id, admitted.LastCommand!.CorrelationId);
        // A fast scheduler may already have completed it: acceptance itself still makes no completion claim.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var snapshot in runtime.WatchSnapshotsAsync(deadline.Token))
        {
            if (snapshot.LastCommand?.State != OperationState.Completed) continue;
            Assert.Equal(id, snapshot.LastCommand.CorrelationId);
            Assert.False(snapshot.Ready);
            Assert.Equal(RecoveryState.Required, snapshot.Recovery);
            Assert.Equal(HandshakePhase.Unknown, snapshot.Handshake);
            break;
        }
        Assert.Equal(CommandDisposition.Rejected, (await runtime.SubmitAsync(
            new GracefulProductionStopCommand(id, Console))).Disposition);
    }

    [Fact]
    public async Task V101_R04_RevisionAdvancesAndSlowReadersReceiveCompleteLatestState()
    {
        await using var runtime = CreateRuntime(TimeSpan.FromMilliseconds(20));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = runtime.WatchSnapshotsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await feed.MoveNextAsync());
        var first = feed.Current;
        var firstRevision = first.Revision;
        await Task.Delay(130, deadline.Token);
        var full = await runtime.GetSnapshotAsync();
        Assert.True(await feed.MoveNextAsync());
        Assert.True(feed.Current.Revision >= full.Revision);
        Assert.True(feed.Current.Revision > first.Revision);
        Assert.Equal(first.RuntimeEpoch, feed.Current.RuntimeEpoch);
        Assert.NotEmpty(feed.Current.AdmissionBlockers);
        Assert.False(first.Ready);
        Assert.Equal(firstRevision, first.Revision);
    }

    [Fact]
    public async Task V101_R05_EpochChangesAndViewUnsubscriptionDoesNotStopRuntime()
    {
        await using var runtime = CreateRuntime(TimeSpan.FromMilliseconds(20));
        await using var replacement = CreateRuntime();
        var before = await runtime.GetSnapshotAsync();
        Assert.NotEqual(before.RuntimeEpoch, (await replacement.GetSnapshotAsync()).RuntimeEpoch);
        await using (var view = runtime.WatchSnapshotsAsync().GetAsyncEnumerator())
            Assert.True(await view.MoveNextAsync());
        await Task.Delay(60);
        var after = await runtime.GetSnapshotAsync();
        Assert.Equal(RuntimeLifecycle.Running, after.Lifecycle);
        Assert.True(after.Revision > before.Revision);
        Assert.Equal(before.ArmState, after.ArmState);
    }

    [Fact]
    public void V101_R06_SnapshotListsDoNotBorrowMutableCallerStorage()
    {
        var source = new[] { "Missing" };
        var blockers = new AdmissionBlockers(source);
        source[0] = "Passed";
        Assert.Equal("Missing", blockers[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)blockers)[0] = "Passed");
    }

    [Fact]
    public async Task V101_R07_InvalidRemoteCancelledAndStoppedCommandsDoNotTransition()
    {
        await using var runtime = CreateRuntime();
        Assert.Equal("InvalidCommandContext", (await runtime.SubmitAsync(new ArmProductionCommand(Guid.Empty, Console))).ReasonCode);
        Assert.Equal("LocalConsoleRequired", (await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.Integration)))).ReasonCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runtime.SubmitAsync(
            new GracefulProductionStopCommand(Guid.NewGuid(), Console), cancelled.Token));
        await runtime.DisposeAsync();
        Assert.Equal("RuntimeStopped", (await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), Console))).ReasonCode);
        Assert.Equal(RuntimeLifecycle.Stopped, (await runtime.GetSnapshotAsync()).Lifecycle);
    }

    [Fact]
    public async Task V101_R08_ShutdownResolvesAnAcceptedPendingStop()
    {
        await using var runtime = CreateRuntime(TimeSpan.FromSeconds(30));
        var id = Guid.NewGuid();
        Assert.Equal(CommandDisposition.Accepted,
            (await runtime.SubmitAsync(new GracefulProductionStopCommand(id, Console))).Disposition);
        Assert.Equal(OperationState.Pending, (await runtime.GetSnapshotAsync()).LastCommand!.State);
        await runtime.DisposeAsync();
        var final = await runtime.GetSnapshotAsync();
        Assert.Equal(id, final.LastCommand!.CorrelationId);
        Assert.Equal(OperationState.Failed, final.LastCommand.State);
        Assert.Equal("RuntimeStopped", final.LastCommand.ReasonCode);
    }

    [Fact]
    public async Task V101_R09_RejectedRequestsCannotDisableLocalStopOrReexecuteIt()
    {
        await using var runtime = CreateRuntime(TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < 4096; i++)
            await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), Console));
        Assert.Equal("DeploymentPoliciesMissing",
            (await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), Console))).ReasonCode);
        var id = Guid.NewGuid();
        Assert.Equal(CommandDisposition.Accepted,
            (await runtime.SubmitAsync(new GracefulProductionStopCommand(id, Console))).Disposition);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var snapshot in runtime.WatchSnapshotsAsync(deadline.Token))
            if (snapshot.LastCommand?.State == OperationState.Completed) break;
        Assert.Equal("DuplicateCorrelationId",
            (await runtime.SubmitAsync(new GracefulProductionStopCommand(id, Console))).ReasonCode);
        Assert.Equal("AlreadyLocallyDisarmed",
            (await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console))).ReasonCode);
    }
}
