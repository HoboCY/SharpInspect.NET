using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class AlarmLifecycleTests
{
    [Fact]
    public void V109_L01_ProjectKeepsAllUnclearedAndBuildsDeterministicBoundedPlcProjection()
    {
        var policy = Policy(
            Rule("FAULT", "source-a", AlarmSeverity.Info, ProductionImpact.FaultAbort,
                plcCode: 3, priority: 10),
            Rule("BLOCK", "source-b", AlarmSeverity.Critical, ProductionImpact.BlockNewTriggers,
                plcCode: 7, priority: 10),
            Rule("NOTICE", "source-c", AlarmSeverity.Warning, ProductionImpact.None),
            maximumPlcEntries: 2);
        var epoch = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var block = Snapshot(policy, "BLOCK", Guid.NewGuid(), epoch, AlarmLifecycle.Active,
            sourceHealthy: false, sequence: 1, firstObserved: start);
        var fault = Snapshot(policy, "FAULT", Guid.NewGuid(), epoch, AlarmLifecycle.Active,
            sourceHealthy: false, sequence: 1, firstObserved: start.AddSeconds(1));
        var notice = Snapshot(policy, "NOTICE", Guid.NewGuid(), epoch, AlarmLifecycle.Active,
            sourceHealthy: false, sequence: 1, firstObserved: start.AddSeconds(2));
        var cleared = Snapshot(policy, "BLOCK", Guid.NewGuid(), epoch, AlarmLifecycle.Cleared,
            sourceHealthy: true, sequence: 2, firstObserved: start.AddSeconds(3));

        var projection = AlarmTransitions.Project(policy, new[] { block, fault, notice, cleared });

        Assert.Equal(3, projection.TotalUncleared);
        Assert.Equal(2, projection.BlockingCount);
        Assert.True(projection.FaultAbortPresent);
        Assert.Equal(1, projection.HiddenCount);
        Assert.Equal(new[] { "BLOCK", "FAULT" }, projection.Entries.Select(entry => entry.Code));
        Assert.Equal(AlarmSeverity.Critical, projection.Entries[0].Severity);
        Assert.Equal(ProductionImpact.BlockNewTriggers, projection.Entries[0].ProductionImpact);
        Assert.Equal(AlarmSeverity.Info, projection.Entries[1].Severity);
        Assert.Equal(ProductionImpact.FaultAbort, projection.Entries[1].ProductionImpact);

        // The register projection capacity limits visible entries, not the number of
        // mapped policy codes. A hidden FaultAbort still participates in admission.
        var singleRegisterPolicy = new AlarmPolicy(policy.Id, policy.Version, policy.Rules,
            policy.SourceObservationFreshness, policy.MaximumActiveInstances, maximumPlcEntries: 1);
        var single = AlarmTransitions.Project(singleRegisterPolicy, new[] { block, fault, notice });
        Assert.Equal("BLOCK", Assert.Single(single.Entries).Code);
        Assert.Equal(2, single.HiddenCount);
        Assert.Equal(2, single.BlockingCount);
        Assert.True(single.FaultAbortPresent);
    }

    [Fact]
    public void V109_L02_ObserveKeepsContinuousIdentityRejectsStaleAndCreatesNewRecurrence()
    {
        var policy = Policy(Rule("DOOR", "guard", AlarmSeverity.Error,
            ProductionImpact.BlockNewTriggers, plcCode: 9));
        var epoch = Guid.NewGuid();
        var current = EmptyState(policy, epoch);
        var observed = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        var raised = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "DOOR", "guard", sourceHealthy: false, observedAtUtc: observed),
            epoch, observed.AddMilliseconds(1));
        Assert.True(raised.Succeeded, raised.ReasonCode);
        Assert.Equal("AlarmRaised", raised.ReasonCode);
        Assert.Contains(AlarmTransitionKind.Raised, raised.Events.Select(item => item.Transition));
        Assert.Contains(AlarmTransitionKind.ProjectionChanged, raised.Events.Select(item => item.Transition));
        current = Apply(current, raised);
        var firstId = Assert.Single(current.Instances).InstanceId;

        var repeated = AlarmTransitions.Observe(current,
            Observation(epoch, 2, "DOOR", "guard", sourceHealthy: false,
                observedAtUtc: observed.AddSeconds(1)),
            epoch, observed.AddSeconds(1));
        Assert.True(repeated.Succeeded, repeated.ReasonCode);
        Assert.Equal("AlarmObserved", repeated.ReasonCode);
        Assert.Equal(firstId, Assert.Single(repeated.Events,
            item => item.Transition == AlarmTransitionKind.Observed).InstanceId);
        Assert.DoesNotContain(repeated.Events, item => item.Transition == AlarmTransitionKind.ProjectionChanged);
        current = Apply(current, repeated);

        var stale = AlarmTransitions.Observe(current,
            Observation(epoch, 2, "DOOR", "guard", sourceHealthy: false,
                observedAtUtc: observed.AddSeconds(2)),
            epoch, observed.AddSeconds(2));
        Assert.False(stale.Succeeded);
        Assert.Equal("AlarmObservationOutOfOrder", stale.ReasonCode);
        Assert.Empty(stale.Events);

        var recovered = AlarmTransitions.Observe(current,
            Observation(epoch, 3, "DOOR", "guard", sourceHealthy: true,
                observedAtUtc: observed.AddSeconds(3)),
            epoch, observed.AddSeconds(3));
        Assert.True(recovered.Succeeded, recovered.ReasonCode);
        Assert.Equal("AlarmCleared", recovered.ReasonCode);
        Assert.Equal(new[] { AlarmTransitionKind.SourceRecovered, AlarmTransitionKind.Cleared },
            recovered.Events.Where(item => item.Instance is not null).Select(item => item.Transition));
        current = Apply(current, recovered);
        Assert.Equal(AlarmLifecycle.Cleared, Assert.Single(current.Instances).Lifecycle);

        var raisedAgain = AlarmTransitions.Observe(current,
            Observation(epoch, 4, "DOOR", "guard", sourceHealthy: false,
                observedAtUtc: observed.AddSeconds(4)),
            epoch, observed.AddSeconds(4));
        Assert.True(raisedAgain.Succeeded, raisedAgain.ReasonCode);
        var secondId = raisedAgain.Events.Single(item => item.Transition == AlarmTransitionKind.Raised).InstanceId;
        Assert.NotEqual(firstId, secondId);

        var healthyBeforeFault = AlarmTransitions.Observe(EmptyState(policy, epoch),
            Observation(epoch, 1, "DOOR", "guard", sourceHealthy: true,
                observedAtUtc: observed.AddSeconds(5)),
            epoch, observed.AddSeconds(5));
        Assert.True(healthyBeforeFault.Succeeded, healthyBeforeFault.ReasonCode);
        Assert.Equal("AlarmObserved", healthyBeforeFault.ReasonCode);
        Assert.Contains(healthyBeforeFault.Events,
            item => item.Transition == AlarmTransitionKind.Observed && item.Instance is null);
    }

    [Fact]
    public void V109_L03_UnknownUnsafeOrWrongSourceFailsClosedAndEpochMismatchHasNoBoundaryEvent()
    {
        var policy = Policy(
            Rule("KNOWN", "sensor-1", AlarmSeverity.Warning, ProductionImpact.None),
            Rule("SECOND", "sensor-2", AlarmSeverity.Error, ProductionImpact.None));
        var epoch = Guid.NewGuid();
        var current = EmptyState(policy, epoch);
        var observed = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

        var unknown = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "UNKNOWN", "sensor-1", sourceHealthy: false,
                observedAtUtc: observed),
            epoch, observed);
        Assert.False(unknown.Succeeded);
        Assert.Equal("AlarmCodeUnmapped", unknown.ReasonCode);
        var unknownEvent = Assert.Single(unknown.Events);
        Assert.Equal(AlarmTransitionKind.BoundaryRejected, unknownEvent.Transition);
        Assert.Equal("UNKNOWN", unknownEvent.Code);
        Assert.Equal("sensor-1", unknownEvent.Source);
        Assert.Null(unknownEvent.Instance);

        var wrongSource = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "KNOWN", "sensor-2", sourceHealthy: false,
                observedAtUtc: observed),
            epoch, observed);
        Assert.False(wrongSource.Succeeded);
        Assert.Equal("AlarmSourceMismatch", wrongSource.ReasonCode);
        Assert.Equal(AlarmTransitionKind.BoundaryRejected, Assert.Single(wrongSource.Events).Transition);
        Assert.Null(wrongSource.Events[0].Instance);

        var unsafeCode = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "raw/message", "sensor-1", sourceHealthy: false,
                observedAtUtc: observed),
            epoch, observed);
        Assert.False(unsafeCode.Succeeded);
        Assert.Equal("AlarmCodeInvalid", unsafeCode.ReasonCode);
        Assert.Empty(unsafeCode.Events);

        var unsafeSource = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "KNOWN", "raw:message", sourceHealthy: false,
                observedAtUtc: observed),
            epoch, observed);
        Assert.False(unsafeSource.Succeeded);
        Assert.Equal("AlarmSourceInvalid", unsafeSource.ReasonCode);
        Assert.Empty(unsafeSource.Events);

        var mismatchedEpoch = AlarmTransitions.Observe(current,
            Observation(Guid.NewGuid(), 1, "KNOWN", "sensor-1", sourceHealthy: false,
                observedAtUtc: observed),
            epoch, observed);
        Assert.False(mismatchedEpoch.Succeeded);
        Assert.Equal("AlarmRuntimeEpochMismatch", mismatchedEpoch.ReasonCode);
        Assert.Empty(mismatchedEpoch.Events);
        Assert.Empty(current.Instances);
    }

    [Fact]
    public void V109_L04_AcknowledgementDoesNotClearOrHideFaultImpact()
    {
        var policy = Policy(Rule("FAULT", "sensor", AlarmSeverity.Info,
            ProductionImpact.FaultAbort, plcCode: 2));
        var epoch = Guid.NewGuid();
        var instance = Snapshot(policy, "FAULT", Guid.NewGuid(), epoch, AlarmLifecycle.Active,
            sourceHealthy: false, sequence: 1,
            firstObserved: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        var current = State(policy, epoch, instance);
        var command = new AcknowledgeAlarmCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.Integration, "forged", Guid.NewGuid(), Guid.NewGuid()),
            instance.InstanceId);

        var acknowledged = AlarmTransitions.DecideCommand(current, command, epoch,
            sourceFresh: false, unmetPrerequisites: AlarmResetPrerequisites.None,
            auditAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 1, TimeSpan.Zero));
        Assert.True(acknowledged.Succeeded, acknowledged.ReasonCode);
        Assert.Equal("AlarmAcknowledged", acknowledged.ReasonCode);
        var eventRecord = Assert.Single(acknowledged.Events);
        Assert.Equal(AlarmTransitionKind.Acknowledged, eventRecord.Transition);
        Assert.Null(eventRecord.ActorPrincipalId);
        Assert.True(eventRecord.Instance!.Acknowledged);
        Assert.Equal(AlarmLifecycle.Active, eventRecord.Instance.Lifecycle);
        Assert.False(eventRecord.Instance.SourceHealthy);
        Assert.Equal(ProductionImpact.FaultAbort, eventRecord.Instance.ProductionImpact);

        current = Apply(current, acknowledged);
        Assert.True(current.Plc.FaultAbortPresent);
        Assert.Equal(1, current.Plc.TotalUncleared);
        var duplicate = AlarmTransitions.DecideCommand(current, command, epoch,
            sourceFresh: false, unmetPrerequisites: AlarmResetPrerequisites.None,
            auditAtUtc: new DateTimeOffset(2026, 9, 8, 0, 0, 2, TimeSpan.Zero));
        Assert.False(duplicate.Succeeded);
        Assert.Equal("AlarmAlreadyAcknowledged", duplicate.ReasonCode);
        Assert.Empty(duplicate.Events);
    }

    [Fact]
    public void V109_L05_LatchedResetRequiresHealthyFreshEpochAndAllPrerequisites()
    {
        var policy = Policy(Rule("LATCHED", "camera", AlarmSeverity.Error,
            ProductionImpact.BlockNewTriggers, latched: true,
            notification: AlarmNotification.UntilCleared, plcCode: 4,
            reset: AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery));
        var epoch = Guid.NewGuid();
        var instance = Snapshot(policy, "LATCHED", Guid.NewGuid(), epoch,
            AlarmLifecycle.RecoveredLatched, sourceHealthy: true, sequence: 4,
            firstObserved: new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        var current = State(policy, epoch, instance);
        var command = new ResetAlarmCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole), instance.InstanceId);
        var auditAt = new DateTimeOffset(2026, 9, 8, 0, 0, 1, TimeSpan.Zero);

        var stale = AlarmTransitions.DecideCommand(current, command, epoch,
            sourceFresh: false, unmetPrerequisites: AlarmResetPrerequisites.None,
            auditAtUtc: auditAt);
        Assert.False(stale.Succeeded);
        Assert.Equal("AlarmSourceObservationStale", stale.ReasonCode);
        Assert.Empty(stale.Events);

        var wrongEpoch = AlarmTransitions.DecideCommand(current, command, Guid.NewGuid(),
            sourceFresh: true, unmetPrerequisites: AlarmResetPrerequisites.None,
            auditAtUtc: auditAt);
        Assert.False(wrongEpoch.Succeeded);
        Assert.Equal("AlarmRuntimeEpochMismatch", wrongEpoch.ReasonCode);
        Assert.Empty(wrongEpoch.Events);

        var blocked = AlarmTransitions.DecideCommand(current, command, epoch,
            sourceFresh: true, unmetPrerequisites: AlarmResetPrerequisites.NoActiveExecution,
            auditAtUtc: auditAt);
        Assert.False(blocked.Succeeded);
        Assert.Equal("AlarmResetPrerequisitesUnmet", blocked.ReasonCode);
        Assert.Empty(blocked.Events);

        var reset = AlarmTransitions.DecideCommand(current, command, epoch,
            sourceFresh: true, unmetPrerequisites: AlarmResetPrerequisites.None,
            auditAtUtc: auditAt);
        Assert.True(reset.Succeeded, reset.ReasonCode);
        Assert.Equal("AlarmResetCompleted", reset.ReasonCode);
        Assert.Equal(new[] { AlarmTransitionKind.Reset, AlarmTransitionKind.Cleared },
            reset.Events.Where(item => item.Instance is not null).Select(item => item.Transition));
        Assert.All(reset.Events, item => Assert.Null(item.ActorPrincipalId));

        current = Apply(current, reset);
        Assert.Equal(AlarmLifecycle.Cleared, Assert.Single(current.Instances).Lifecycle);
        Assert.Equal(0, current.Plc.TotalUncleared);
        Assert.False(current.Plc.FaultAbortPresent);
    }

    [Fact]
    public void V109_L06_ActiveCapacityIsARecoverablePolicyBoundary()
    {
        var policy = Policy(
            Rule("FIRST", "source-1", AlarmSeverity.Warning, ProductionImpact.None),
            Rule("SECOND", "source-2", AlarmSeverity.Warning, ProductionImpact.None),
            maximumActiveInstances: 1);
        var epoch = Guid.NewGuid();
        var observed = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var current = EmptyState(policy, epoch);
        var first = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "FIRST", "source-1", sourceHealthy: false,
                observedAtUtc: observed),
            epoch, observed);
        Assert.True(first.Succeeded, first.ReasonCode);
        current = Apply(current, first);

        var overCapacity = AlarmTransitions.Observe(current,
            Observation(epoch, 1, "SECOND", "source-2", sourceHealthy: false,
                observedAtUtc: observed.AddSeconds(1)),
            epoch, observed.AddSeconds(1));
        Assert.False(overCapacity.Succeeded);
        Assert.Equal("AlarmActiveInstanceCapacityExceeded", overCapacity.ReasonCode);
        var boundary = Assert.Single(overCapacity.Events);
        Assert.Equal(AlarmTransitionKind.BoundaryRejected, boundary.Transition);
        Assert.Null(boundary.Instance);
        Assert.Equal("SECOND", boundary.Code);
        Assert.Equal("source-2", boundary.Source);
        Assert.Single(current.Instances);
    }

    private static AlarmPolicy Policy(params AlarmPolicyRule[] rules) =>
        new("policy", "v1", rules, TimeSpan.FromSeconds(30),
            maximumActiveInstances: 8, maximumPlcEntries: 2);

    private static AlarmPolicy Policy(AlarmPolicyRule first, AlarmPolicyRule second,
        int maximumActiveInstances) =>
        new("policy", "v1", new[] { first, second }, TimeSpan.FromSeconds(30),
            maximumActiveInstances: maximumActiveInstances, maximumPlcEntries: 2);

    private static AlarmPolicy Policy(AlarmPolicyRule first, AlarmPolicyRule second,
        AlarmPolicyRule third, int maximumPlcEntries) =>
        new("policy", "v1", new[] { first, second, third }, TimeSpan.FromSeconds(30),
            maximumActiveInstances: 8, maximumPlcEntries: maximumPlcEntries);

    private static AlarmPolicy Policy(AlarmPolicyRule first, AlarmPolicyRule second,
        AlarmPolicyRule third, AlarmPolicyRule fourth, int maximumPlcEntries) =>
        new("policy", "v1", new[] { first, second, third, fourth }, TimeSpan.FromSeconds(30),
            maximumActiveInstances: 8, maximumPlcEntries: maximumPlcEntries);

    private static AlarmPolicyRule Rule(string code, string source,
        AlarmSeverity severity = AlarmSeverity.Info,
        ProductionImpact impact = ProductionImpact.None,
        bool latched = false,
        AlarmNotification notification = AlarmNotification.None,
        ushort? plcCode = null,
        int priority = 0,
        AlarmResetPrerequisites reset = AlarmResetPrerequisites.None) =>
        new(code, source, severity, impact, latched, notification, plcCode, priority, reset);

    private static AlarmObservation Observation(Guid epoch, long sequence, string code,
        string source, bool sourceHealthy, DateTimeOffset observedAtUtc) =>
        new(epoch, sequence, code, source, sourceHealthy, observedAtUtc);

    private static AlarmStateSnapshot EmptyState(AlarmPolicy policy, Guid epoch) =>
        State(policy, epoch);

    private static AlarmStateSnapshot State(AlarmPolicy policy, Guid epoch,
        params AlarmInstanceSnapshot[] instances) =>
        new(true, "AlarmStateReady", epoch, 0, policy, instances,
            AlarmTransitions.Project(policy, instances));

    private static AlarmInstanceSnapshot Snapshot(AlarmPolicy policy, string code, Guid instanceId,
        Guid epoch, AlarmLifecycle lifecycle, bool sourceHealthy, long sequence,
        DateTimeOffset firstObserved, bool acknowledged = false)
    {
        var rule = policy.Rules.Single(item => item.Code == code);
        return new(instanceId, rule.Code, rule.Source, policy.Id, policy.Version,
            policy.ContentHash, rule.Severity, rule.ProductionImpact, rule.IsLatched,
            rule.Notification, rule.PlcCode, rule.PlcPriority, rule.ResetPrerequisites,
            firstObserved, firstObserved.AddSeconds(sequence), sourceHealthy, epoch, sequence,
            lifecycle, acknowledged, null, acknowledged ? firstObserved : null, 0);
    }

    private static AlarmStateSnapshot Apply(AlarmStateSnapshot current,
        AlarmTransitionDecision decision)
    {
        var instances = current.Instances.ToList();
        foreach (var instance in decision.Events.Select(item => item.Instance).Where(item => item is not null))
        {
            var value = instance!;
            var index = instances.FindIndex(item => item.InstanceId == value.InstanceId);
            if (index < 0) instances.Add(value);
            else instances[index] = value;
        }

        return new(current.Available, current.ReasonCode, current.RuntimeEpoch,
            current.Revision + 1, current.Policy, instances,
            AlarmTransitions.Project(current.Policy!, instances));
    }
}
