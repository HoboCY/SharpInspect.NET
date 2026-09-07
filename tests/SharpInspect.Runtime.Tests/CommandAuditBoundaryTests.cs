using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>Isolated authority tests. This in-memory writer is not a storage qualification fixture.</summary>
internal sealed class ProbeAuditWriter : ICommandAuditWriter
{
    public Task<StoreWriteResult> Initialization { get; set; } = Task.FromResult(new StoreWriteResult(true, "SqliteProfileVerified"));
    public TimeSpan CommitTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public List<CommandAuditFact> Facts { get; } = new();
    public Func<CommandAuditFact, StoreDeadline, CancellationToken, ValueTask<StoreWriteResult>>? Write { get; set; }

    public ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact, StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Write is not null) return Write(fact, deadline, cancellationToken);
        Facts.Add(fact);
        return ValueTask.FromResult(new StoreWriteResult(true, "Committed", fact));
    }
}

public sealed class CommandAuditBoundaryTests
{
    private static readonly CommandInvocation Console = new(CommandSource.PhysicalConsole);

    [Fact]
    public async Task V102_R01_MissingStoreCannotAdmitStopOrInventAnAuditRecord()
    {
        await using var runtime = new StationRuntime();
        var outcome = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console));
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, outcome.Audit);
        Assert.Equal("TraceAuditUnavailable", outcome.ReasonCode);
        var state = await runtime.GetSnapshotAsync();
        Assert.Null(state.LastCommand);
        Assert.False(state.Ready);
        Assert.Equal(HealthState.Faulted, state.Store.State);
    }

    [Fact]
    public async Task V102_R02_OutcomeMustCommitBeforeAcceptanceAndTerminalBeforeCompletion()
    {
        var first = new TaskCompletionSource<StoreWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<StoreWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CommandAuditFact>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalEntered = new TaskCompletionSource<CommandAuditFact>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ProbeAuditWriter { Write = (fact, _, _) =>
        {
            if (fact.Phase == CommandAuditPhase.Outcome) { entered.SetResult(fact); return new(first.Task); }
            terminalEntered.SetResult(fact); return new(second.Task);
        } };
        await using var runtime = new StationRuntime(writer, TimeSpan.FromMilliseconds(20));
        var submit = runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console)).AsTask();
        var fact = await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(submit.IsCompleted);
        Assert.Null((await runtime.GetSnapshotAsync()).LastCommand);
        first.SetResult(new StoreWriteResult(true, "Committed", fact));
        var outcome = await submit;
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        Assert.Equal(fact.AttemptId, outcome.AttemptId);
        var terminal = await terminalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(OperationState.Pending, (await runtime.GetSnapshotAsync()).LastCommand!.State);
        second.SetResult(new StoreWriteResult(true, "Committed", terminal));
        await WaitForAsync(runtime, OperationState.Completed);
        Assert.Equal(fact.AttemptId, terminal.AttemptId);
        Assert.NotEqual(fact.EventId, terminal.EventId);
    }

    [Fact]
    public async Task V102_R03_CommitFailureDoesNotAdmitAndIsVisibleInHealth()
    {
        var writer = new ProbeAuditWriter { Write = (_, _, _) => ValueTask.FromResult(new StoreWriteResult(false, "DiskFull")) };
        await using var runtime = new StationRuntime(writer);
        var result = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console));
        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, result.Audit);
        var state = await runtime.GetSnapshotAsync();
        Assert.Null(state.LastCommand);
        Assert.Equal("DiskFull", state.Store.ReasonCode);
        Assert.Contains("TraceAuditUnavailable", state.AdmissionBlockers);
        Assert.False(state.Ready);
    }

    [Fact]
    public async Task V102_R04_TerminalAuditFailureCannotPublishCompleted()
    {
        var writer = new ProbeAuditWriter { Write = (fact, _, _) => ValueTask.FromResult(fact.Phase == CommandAuditPhase.Outcome
            ? new StoreWriteResult(true, "Committed", fact) : new StoreWriteResult(false, "CommitFailure")) };
        await using var runtime = new StationRuntime(writer, TimeSpan.FromMilliseconds(20));
        var result = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console));
        Assert.Equal(CommandDisposition.Accepted, result.Disposition);
        await WaitForAsync(runtime, OperationState.Failed);
        var state = await runtime.GetSnapshotAsync();
        Assert.Equal("TraceAuditUnavailable", state.LastCommand!.ReasonCode);
        Assert.False(state.Ready);
        Assert.Equal(HealthState.Faulted, state.Store.State);
    }

    [Fact]
    public async Task V102_R05_ClaimedIdentityIsRetainedAsInputAndRejectedCommandsAreFacts()
    {
        var writer = new ProbeAuditWriter();
        await using var runtime = new StationRuntime(writer);
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole, "Administrator", Guid.NewGuid(), Guid.NewGuid());
        var outcome = await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), invocation));
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        var fact = Assert.Single(writer.Facts);
        Assert.Equal("Administrator", fact.ClaimedPrincipalId);
        Assert.Equal(invocation.SessionId, fact.ClaimedSessionId);
        Assert.Equal(invocation.StepUpGrantId, fact.ClaimedStepUpGrantId);
        Assert.Equal(CommandDisposition.Rejected, fact.Disposition);
        Assert.Equal(InteractiveSessionState.Unauthenticated, (await runtime.GetSnapshotAsync()).Session.State);
    }

    [Fact]
    public async Task V102_R06_PersistedDuplicateOutcomeCannotBeExecutedAgain()
    {
        var writer = new ProbeAuditWriter { Write = (fact, _, _) => ValueTask.FromResult(new StoreWriteResult(true, "Committed",
            fact with { Disposition = CommandDisposition.Rejected, ReasonCode = "DuplicateCorrelationId" })) };
        await using var runtime = new StationRuntime(writer);
        var outcome = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console));
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("DuplicateCorrelationId", outcome.ReasonCode);
        Assert.Null((await runtime.GetSnapshotAsync()).LastCommand);
    }

    private static async Task WaitForAsync(IStationRuntime runtime, OperationState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var snapshot in runtime.WatchSnapshotsAsync(timeout.Token))
            if (snapshot.LastCommand?.State == state) return;
        Assert.Fail("Runtime stopped before expected state");
    }

    [Fact]
    public async Task V102_R07_LateStoreInitializationCannotEraseAnUnrecordedCommandFault()
    {
        var initialization = new TaskCompletionSource<StoreWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ProbeAuditWriter { Initialization = initialization.Task, CommitTimeout = TimeSpan.FromMilliseconds(30) };
        await using var runtime = new StationRuntime(writer, TimeSpan.FromMilliseconds(20));
        var outcome = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console));
        Assert.Equal(AuditPersistence.Unavailable, outcome.Audit);
        initialization.SetResult(new StoreWriteResult(true, "SqliteProfileVerified"));
        // The second command awaits the same initialization, establishing that its publication completed.
        await runtime.SubmitAsync(new ArmProductionCommand(Guid.NewGuid(), Console));
        var state = await runtime.GetSnapshotAsync();
        Assert.Equal(HealthState.Faulted, state.Store.State);
        Assert.Contains("TraceAuditUnavailable", state.AdmissionBlockers);
        Assert.Null(state.LastCommand);
    }

    [Fact]
    public async Task V102_R08_ShutdownSettlesAnAlreadyStartedTerminalTransactionBeforeStopping()
    {
        var terminalEntered = new TaskCompletionSource<CommandAuditFact>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitTerminal = new TaskCompletionSource<StoreWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new ProbeAuditWriter { Write = (fact, _, _) =>
        {
            if (fact.Phase == CommandAuditPhase.Outcome) return ValueTask.FromResult(new StoreWriteResult(true, "Committed", fact));
            terminalEntered.SetResult(fact);
            return new(commitTerminal.Task);
        } };
        await using var runtime = new StationRuntime(writer, TimeSpan.FromMilliseconds(20));
        await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), Console));
        var terminal = await terminalEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var shutdown = runtime.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(OperationState.Pending, (await runtime.GetSnapshotAsync()).LastCommand!.State);
        commitTerminal.SetResult(new StoreWriteResult(true, "Committed", terminal));
        await shutdown;
        var stopped = await runtime.GetSnapshotAsync();
        Assert.Equal(RuntimeLifecycle.Stopped, stopped.Lifecycle);
        Assert.Equal(OperationState.Completed, stopped.LastCommand!.State);
        Assert.False(stopped.Ready);
        Assert.Equal(CommandAuditPhase.Completed, terminal.Phase);
    }
}
