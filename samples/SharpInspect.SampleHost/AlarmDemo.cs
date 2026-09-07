using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

internal static class AlarmDemo
{
    internal static AlarmPolicy ReadPolicy(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidOperationException("AlarmPolicyFileTooLarge");
        var document = JsonSerializer.Deserialize<PolicyDocument>(File.ReadAllText(path)) ??
            throw new InvalidOperationException("AlarmPolicyFileInvalid");
        return new(document.Id, document.Version, document.Rules, document.SourceObservationFreshness,
            document.MaximumActiveInstances, document.MaximumPlcEntries);
    }

    internal static int Run(ProductionStoreOptions options)
    {
        try { VerifyAsync(options).GetAwaiter().GetResult(); return 0; }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is AlarmAssertionException assertion
                ? "V109 CONSUMER FAIL " + assertion.Message : "V109 CONSUMER FAIL " + exception.GetType().Name);
            return 1;
        }
    }

    private static async Task VerifyAsync(ProductionStoreOptions options)
    {
        var services = new ServiceCollection().AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var query = provider.GetRequiredService<IAlarmHistoryQuery>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        StationStateSnapshot snapshot;
        do
        {
            snapshot = await runtime.GetSnapshotAsync(timeout.Token);
            if (snapshot.AlarmState is { Available: true } alarmState &&
                alarmState.Instances.Any(instance => instance.Code == "StartupRecoveryRequired") &&
                snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified) break;
            await Task.Delay(20, timeout.Token);
        } while (true);
        var initial = snapshot.AlarmState!;
        var startup = initial.Instances.Single(instance => instance.Code == "StartupRecoveryRequired");
        Require(!snapshot.Ready && snapshot.Alarms.BlocksProduction && startup.IsLatched &&
            startup.Severity == AlarmSeverity.Warning && startup.ProductionImpact == ProductionImpact.BlockNewTriggers,
            "startup fact must keep its explicit severity, impact and latch");
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole);
        var acknowledgement = await runtime.SubmitAsync(new AcknowledgeAlarmCommand(Guid.NewGuid(), invocation, startup.InstanceId));
        var reset = await runtime.SubmitAsync(new ResetAlarmCommand(Guid.NewGuid(), invocation, startup.InstanceId));
        Require(acknowledgement.Disposition == CommandDisposition.Rejected && reset.Disposition == CommandDisposition.Rejected &&
            acknowledgement.Audit == AuditPersistence.Persisted && reset.Audit == AuditPersistence.Persisted,
            "anonymous acknowledgement and reset must be durably rejected");
        var after = await runtime.GetSnapshotAsync(timeout.Token);
        var unchanged = after.AlarmState!.Instances.Single(instance => instance.InstanceId == startup.InstanceId);
        Require(!unchanged.Acknowledged && unchanged.Lifecycle != AlarmLifecycle.Cleared && !after.Ready,
            "rejected commands must not acknowledge, clear or arm");
        var first = await query.QueryAsync(new AlarmHistoryFilter(instanceId: startup.InstanceId, pageSize: 1), timeout.Token);
        Require(first.Available && first.Records.Count == 1 && first.Records[0].InstanceId == startup.InstanceId,
            "bounded history must name the same durable instance");
        if (first.NextAfterPosition is { } cursor)
        {
            var next = await query.QueryAsync(new AlarmHistoryFilter(instanceId: startup.InstanceId,
                afterPosition: cursor, throughPosition: first.ThroughPosition, pageSize: 1), timeout.Token);
            Require(next.Available && next.ThroughPosition == first.ThroughPosition &&
                next.Records.All(record => record.Position > cursor && record.InstanceId == startup.InstanceId),
                "history pagination must retain its frozen upper bound");
        }
        Require(initial.Plc.Entries.Any(entry => entry.InstanceId == startup.InstanceId) &&
            initial.Plc.TotalUncleared == initial.Instances.Count, "PLC summary must refer to the same complete instance set");
        Console.WriteLine($"V109-P01 alarm-consumer PASS instance={startup.InstanceId:D} ready=false acknowledged=false " +
            $"historyThrough={first.ThroughPosition} plcTotal={initial.Plc.TotalUncleared} physicalDevices=NotRun");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new AlarmAssertionException(reason);
    }

    private sealed class AlarmAssertionException : Exception
    {
        internal AlarmAssertionException(string reason) : base(reason) { }
    }

    private sealed record PolicyDocument(string Id, string Version, AlarmPolicyRule[] Rules,
        TimeSpan SourceObservationFreshness, int MaximumActiveInstances, int MaximumPlcEntries);
}
