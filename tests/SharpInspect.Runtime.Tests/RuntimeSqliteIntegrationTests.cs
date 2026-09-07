using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RuntimeSqliteIntegrationTests
{
    [Fact]
    public async Task V102_I01_RuntimeAndIndependentReaderRebuildFactsAcrossRestartAndRejectDuplicate()
    {
        var options = CreateOptions();
        var armId = Guid.NewGuid();
        var stopId = Guid.NewGuid();
        Guid firstEpoch;
        Guid[] originalEvents;
        var services = new ServiceCollection();
        // Registration order cannot silently strand Runtime on the missing-store scaffold.
        services.AddSharpInspectRuntime(TimeSpan.FromMilliseconds(20));
        services.AddSharpInspectSqliteRuntime(options);
        await using (var provider = services.BuildServiceProvider())
        {
            var runtime = provider.GetRequiredService<IStationRuntime>();
            var query = provider.GetRequiredService<ICommandTraceQuery>();
            Assert.False(query is ICommandAuditWriter);
            firstEpoch = (await runtime.GetSnapshotAsync()).RuntimeEpoch;
            var arm = await runtime.SubmitAsync(new ArmProductionCommand(armId,
                new CommandInvocation(CommandSource.PhysicalConsole, "Administrator", Guid.NewGuid(), Guid.NewGuid())));
            Assert.Equal(CommandDisposition.Rejected, arm.Disposition);
            Assert.Equal(AuditPersistence.Persisted, arm.Audit);
            var stop = await runtime.SubmitAsync(new GracefulProductionStopCommand(stopId, new(CommandSource.PhysicalConsole)));
            Assert.Equal(CommandDisposition.Accepted, stop.Disposition);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var state in runtime.WatchSnapshotsAsync(timeout.Token))
                if (state.LastCommand?.State == OperationState.Completed) break;
            var records = (await query.QueryAsync(new CommandTraceFilter())).Records;
            Assert.Equal(3, records.Count);
            Assert.All(records, r => { Assert.Equal("SharpInspect.Runtime", r.SystemPrincipalId); Assert.Null(r.AuthenticatedHumanPrincipalId); });
            Assert.Equal("Administrator", records[0].ClaimedPrincipalId);
            Assert.Equal(new[] { CommandAuditPhase.Outcome, CommandAuditPhase.Completed }, records.Where(r => r.CorrelationId == stopId).Select(r => r.Phase));
            originalEvents = records.Select(r => r.EventId).ToArray();
        }

        var independentReader = new SqliteCommandTraceQuery(options);
        Assert.Equal(originalEvents, (await independentReader.QueryAsync(new CommandTraceFilter())).Records.Select(x => x.EventId));
        var replacementServices = new ServiceCollection();
        replacementServices.AddSharpInspectSqliteRuntime(options);
        await using var replacement = replacementServices.BuildServiceProvider();
        var restarted = replacement.GetRequiredService<IStationRuntime>();
        var duplicate = await restarted.SubmitAsync(new GracefulProductionStopCommand(stopId, new(CommandSource.PhysicalConsole)));
        Assert.Equal(CommandDisposition.Rejected, duplicate.Disposition);
        Assert.Equal(AuditPersistence.Persisted, duplicate.Audit);
        Assert.Equal("DuplicateCorrelationId", duplicate.ReasonCode);
        var snapshot = await restarted.GetSnapshotAsync();
        Assert.NotEqual(firstEpoch, snapshot.RuntimeEpoch);
        Assert.Null(snapshot.LastCommand);
        Assert.False(snapshot.Ready);
        var after = await independentReader.QueryAsync(new CommandTraceFilter(CorrelationId: stopId));
        Assert.Equal(3, after.Records.Count);
        Assert.Equal(CommandDisposition.Rejected, after.Records[2].Disposition);
    }

    [Fact]
    public async Task V102_I02_InvalidStorePathStaysVisibleAndCannotAdmitCommand()
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(new ProductionStoreOptions(@"\\unavailable-server\share\trace.sqlite"));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var outcome = await runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), new(CommandSource.PhysicalConsole)));
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, outcome.Audit);
        var state = await runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.Null(state.LastCommand);
        Assert.Equal(HealthState.Faulted, state.Store.State);
        Assert.Contains(state.AdmissionBlockers, x => x.Contains("StorePath", StringComparison.Ordinal));
    }

    private static ProductionStoreOptions CreateOptions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new ProductionStoreOptions(Path.Combine(directory, "trace.sqlite"));
    }
}
