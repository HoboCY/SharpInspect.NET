using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlarmContractTests
{
    [Fact]
    public void V109_C01_PolicyCopiesRulesAndCanonicalHashIgnoresInputOrder()
    {
        var rules = new List<AlarmPolicyRule>
        {
            Rule("TEMP-HIGH", "oven", AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, plcCode: 7),
            Rule("DOOR-OPEN", "guard", AlarmSeverity.Critical, ProductionImpact.FaultAbort)
        };
        var policy = new AlarmPolicy("station-policy", "v1", rules,
            TimeSpan.FromSeconds(30), maximumActiveInstances: 8, maximumPlcEntries: 2);
        var hash = policy.ContentHash;

        rules.Clear();
        Assert.Equal(2, policy.Rules.Count);
        Assert.Equal("oven", policy.Rules[0].Source);
        Assert.True(policy.TryGetRule("TEMP-HIGH", out var found));
        Assert.Equal(AlarmSeverity.Warning, found!.Severity);
        Assert.False(policy.TryGetRule("UNKNOWN", out var missing));
        Assert.Null(missing);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToUpperInvariant());
        Assert.Throws<NotSupportedException>(() => ((IList<AlarmPolicyRule>)policy.Rules)[0] = Rule("MUTATE", "x"));

        var reordered = new AlarmPolicy("station-policy", "v1", new[]
        {
            policy.Rules[1], policy.Rules[0]
        }, TimeSpan.FromSeconds(30), maximumActiveInstances: 8, maximumPlcEntries: 2);
        Assert.Equal(hash, reordered.ContentHash);
    }

    [Fact]
    public void V109_C02_SeverityImpactAndFreshnessAreIndependentHashedPolicyFields()
    {
        var baselineRule = Rule("CAMERA-DEGRADED", "camera-1",
            AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers);
        var baseline = Policy(baselineRule);
        var critical = Policy(baselineRule with { Severity = AlarmSeverity.Critical });
        var abort = Policy(baselineRule with { ProductionImpact = ProductionImpact.FaultAbort });
        var fresh = new AlarmPolicy("policy", "v1", new[] { baselineRule },
            TimeSpan.FromSeconds(31));

        Assert.Equal(AlarmSeverity.Warning, baseline.Rules[0].Severity);
        Assert.Equal(ProductionImpact.BlockNewTriggers, baseline.Rules[0].ProductionImpact);
        Assert.Equal(AlarmSeverity.Critical, critical.Rules[0].Severity);
        Assert.Equal(ProductionImpact.BlockNewTriggers, critical.Rules[0].ProductionImpact);
        Assert.Equal(AlarmSeverity.Warning, abort.Rules[0].Severity);
        Assert.Equal(ProductionImpact.FaultAbort, abort.Rules[0].ProductionImpact);
        Assert.NotEqual(baseline.ContentHash, critical.ContentHash);
        Assert.NotEqual(baseline.ContentHash, abort.ContentHash);
        Assert.NotEqual(baseline.ContentHash, fresh.ContentHash);
    }

    [Fact]
    public void V109_C03_PolicyRejectsUnstableCodesEnumsFlagsAndCapacityOverflow()
    {
        Assert.Throws<ArgumentException>(() => Policy(Rule("bad code", "source")));
        Assert.Throws<ArgumentException>(() => Policy(Rule("CODE", "source with space")));
        Assert.Throws<ArgumentException>(() => new AlarmPolicy("bad id", "v1",
            new[] { Rule("CODE", "source") }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => new AlarmPolicy("policy", "v1", new[]
        {
            Rule("DUP", "source"), Rule("DUP", "other-source")
        }, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentException>(() => Policy(Rule("ENUM", "source",
            (AlarmSeverity)99, ProductionImpact.None)));
        Assert.Throws<ArgumentException>(() => Policy(Rule("FLAGS", "source",
            AlarmSeverity.Info, ProductionImpact.None, reset: (AlarmResetPrerequisites)16)));
        Assert.Throws<ArgumentException>(() => Policy(Rule("PLCZERO", "source",
            AlarmSeverity.Info, ProductionImpact.None, plcCode: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlarmPolicy("policy", "v1",
            new[] { Rule("CODE", "source") }, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlarmPolicy("policy", "v1",
            new[] { Rule("CODE", "source") }, TimeSpan.FromMinutes(5).Add(TimeSpan.FromTicks(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlarmPolicy("policy", "v1",
            new[] { Rule("CODE", "source") }, TimeSpan.FromSeconds(1), maximumActiveInstances: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlarmPolicy("policy", "v1",
            new[] { Rule("CODE", "source") }, TimeSpan.FromSeconds(1), maximumPlcEntries: 17));
    }

    [Fact]
    public void V109_C04_StateAndPlcProjectionDefensivelyCopyAllInstances()
    {
        var policy = Policy(Rule("TEMP-HIGH", "oven", AlarmSeverity.Warning,
            ProductionImpact.BlockNewTriggers, plcCode: 7));
        var epoch = Guid.NewGuid();
        var active = Snapshot(policy, Guid.NewGuid(), AlarmLifecycle.Active, epoch);
        var cleared = Snapshot(policy, Guid.NewGuid(), AlarmLifecycle.Cleared, epoch);
        var instances = new List<AlarmInstanceSnapshot> { active, cleared };
        var plcEntries = new List<AlarmPlcEntry>
        {
            new(active.InstanceId, active.Code, 7, active.Severity, active.ProductionImpact)
        };
        var projection = new AlarmPlcProjection(plcEntries, totalUncleared: 2,
            blockingCount: 1, faultAbortPresent: false);
        var state = new AlarmStateSnapshot(true, "AlarmStateReady", epoch, 4,
            policy, instances, projection);

        instances.Clear();
        plcEntries.Clear();
        Assert.Equal(2, state.Instances.Count);
        Assert.Single(state.Plc.Entries);
        Assert.Equal(1, state.Plc.HiddenCount);
        Assert.Contains(state.Instances, item => item.Lifecycle == AlarmLifecycle.Cleared);
        Assert.Throws<NotSupportedException>(() => ((IList<AlarmInstanceSnapshot>)state.Instances)[0] = cleared);
        Assert.Throws<NotSupportedException>(() => ((IList<AlarmPlcEntry>)state.Plc.Entries)[0] =
            new(Guid.NewGuid(), "OTHER", 8, AlarmSeverity.Info, ProductionImpact.None));
    }

    [Fact]
    public void V109_C05_HistoryFilterAndPageStayBoundedAndImmutable()
    {
        var policy = Policy(Rule("ALARM", "source"));
        var instance = Snapshot(policy, Guid.NewGuid(), AlarmLifecycle.Active, Guid.NewGuid());
        var records = new List<AlarmHistoryRecord>
        {
            new(1, Guid.NewGuid(), instance.InstanceId, instance.Code, instance.Source,
                AlarmTransitionKind.Raised, instance.FirstObservedAtUtc, instance.LastObservedAtUtc,
                null, null, null, "Raised", instance, null)
        };
        var page = new AlarmHistoryPage(records, throughPosition: 1, nextAfterPosition: null,
            available: true, reasonCode: "HistoryAvailable");
        records.Clear();
        Assert.Single(page.Records);
        Assert.Throws<NotSupportedException>(() => ((IList<AlarmHistoryRecord>)page.Records)[0] =
            page.Records[0]);

        var filter = new AlarmHistoryFilter(instance.InstanceId, instance.Code,
            afterPosition: 0, throughPosition: 10, pageSize: 100);
        Assert.Equal(10, filter.ThroughPosition);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlarmHistoryFilter(pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlarmHistoryFilter(afterPosition: 5, throughPosition: 4));
        Assert.Throws<ArgumentException>(() => new AlarmHistoryFilter(code: "bad code"));
        Assert.Throws<ArgumentException>(() => new AlarmHistoryPage(records: new[] { page.Records[0] },
            throughPosition: 1, nextAfterPosition: null, available: true, reasonCode: ""));
    }

    private static AlarmPolicy Policy(AlarmPolicyRule rule) =>
        new("policy", "v1", new[] { rule }, TimeSpan.FromSeconds(30));

    private static AlarmPolicyRule Rule(string code, string source,
        AlarmSeverity severity = AlarmSeverity.Info,
        ProductionImpact impact = ProductionImpact.None,
        bool latched = false,
        AlarmNotification notification = AlarmNotification.None,
        ushort? plcCode = null,
        int priority = 0,
        AlarmResetPrerequisites reset = AlarmResetPrerequisites.None) =>
        new(code, source, severity, impact, latched, notification, plcCode, priority, reset);

    private static AlarmInstanceSnapshot Snapshot(AlarmPolicy policy, Guid instanceId,
        AlarmLifecycle lifecycle, Guid epoch)
    {
        var observed = DateTimeOffset.UtcNow;
        var rule = policy.Rules[0];
        return new(instanceId, rule.Code, rule.Source, policy.Id, policy.Version, policy.ContentHash,
            rule.Severity, rule.ProductionImpact, rule.IsLatched, rule.Notification, rule.PlcCode,
            rule.PlcPriority, rule.ResetPrerequisites, observed, observed, lifecycle == AlarmLifecycle.Cleared,
            epoch, 1, lifecycle, false, null, null, 1);
    }
}
