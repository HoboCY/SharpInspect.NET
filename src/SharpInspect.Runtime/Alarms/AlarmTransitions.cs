using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Alarms;

/// <summary>One trusted, sequenced source observation. This type is internal by design.</summary>
internal sealed record AlarmObservation(
    Guid RuntimeEpoch,
    long Sequence,
    string Code,
    string Source,
    bool SourceHealthy,
    DateTimeOffset ObservedAtUtc);

internal sealed record AlarmTransitionDecision(
    bool Succeeded,
    string ReasonCode,
    IReadOnlyList<AlarmHistoryRecord> Events);

/// <summary>
/// Pure alarm lifecycle decisions. Durable positions and actor attribution are assigned by
/// the Runtime/store boundary after these decisions have been accepted.
/// </summary>
internal static class AlarmTransitions
{
    private const string StateUnavailable = "AlarmStateUnavailable";
    private const string RuntimeEpochMismatch = "AlarmRuntimeEpochMismatch";
    private const string ObservationOutOfOrder = "AlarmObservationOutOfOrder";
    private const string CodeUnmapped = "AlarmCodeUnmapped";
    private const string SourceMismatch = "AlarmSourceMismatch";
    private const string CodeInvalid = "AlarmCodeInvalid";
    private const string SourceInvalid = "AlarmSourceInvalid";
    private const string ObservationInvalid = "AlarmObservationInvalid";
    private const string CapacityExceeded = "AlarmActiveInstanceCapacityExceeded";
    private const string AcknowledgeReason = "AlarmAcknowledged";
    private const string ResetReason = "AlarmResetCompleted";

    internal static AlarmPlcProjection Project(AlarmPolicy policy,
        IEnumerable<AlarmInstanceSnapshot> instances)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(instances);
        var all = instances.ToArray();
        var uncleared = all.Where(instance => instance.Lifecycle != AlarmLifecycle.Cleared).ToArray();
        var entries = uncleared
            .Where(instance => instance.PlcCode.HasValue)
            .OrderByDescending(instance => instance.PlcPriority)
            .ThenBy(instance => instance.FirstObservedAtUtc)
            .ThenBy(instance => instance.InstanceId)
            .Take(policy.MaximumPlcEntries)
            .Select(instance => new AlarmPlcEntry(instance.InstanceId, instance.Code,
                instance.PlcCode!.Value, instance.Severity, instance.ProductionImpact))
            .ToArray();
        return new AlarmPlcProjection(entries, uncleared.Length,
            uncleared.Count(instance => instance.ProductionImpact != ProductionImpact.None),
            uncleared.Any(instance => instance.ProductionImpact == ProductionImpact.FaultAbort));
    }

    internal static AlarmTransitionDecision Observe(AlarmStateSnapshot current,
        AlarmObservation observation, Guid runtimeEpoch, DateTimeOffset auditAtUtc)
    {
        if (current is null || !current.Available || current.Policy is null)
            return RejectWithoutEvent(StateUnavailable);
        if (runtimeEpoch != current.RuntimeEpoch || observation is null ||
            observation.RuntimeEpoch != runtimeEpoch)
            return RejectWithoutEvent(RuntimeEpochMismatch);
        if (observation.Sequence <= 0 || observation.ObservedAtUtc == default || auditAtUtc == default)
            return RejectWithoutEvent(ObservationInvalid);
        if (!IsSafeIdentifier(observation.Code))
            return RejectWithoutEvent(CodeInvalid);
        if (!IsSafeIdentifier(observation.Source))
            return RejectWithoutEvent(SourceInvalid);
        if (!current.Policy.TryGetRule(observation.Code, out var rule) || rule is null)
            return Reject(CodeUnmapped, auditAtUtc, observation.Code, observation.Source);
        if (!string.Equals(rule.Source, observation.Source, StringComparison.Ordinal))
            return Reject(SourceMismatch, auditAtUtc, observation.Code, observation.Source);

        var instances = current.Instances.ToList();
        var sameCodeSource = instances.Where(instance =>
                string.Equals(instance.Code, observation.Code, StringComparison.Ordinal) &&
                string.Equals(instance.Source, observation.Source, StringComparison.Ordinal))
            .OrderByDescending(instance => instance.SourceObservationSequence)
            .FirstOrDefault();
        if (sameCodeSource is not null && observation.Sequence <= sameCodeSource.SourceObservationSequence)
            return Reject(ObservationOutOfOrder, auditAtUtc, observation.Code, observation.Source, includeEvent: false);

        var active = sameCodeSource is not null && sameCodeSource.Lifecycle != AlarmLifecycle.Cleared;
        var events = new List<AlarmHistoryRecord>();
        string outcome;
        if (!observation.SourceHealthy)
        {
            AlarmInstanceSnapshot updated;
            if (active)
            {
                updated = Snapshot(current.Policy, rule, sameCodeSource!, observation,
                    AlarmLifecycle.Active, sameCodeSource!.Acknowledged);
                Replace(instances, updated);
                events.Add(Event(updated, AlarmTransitionKind.Observed, observation.ObservedAtUtc,
                    auditAtUtc, "AlarmObserved"));
                outcome = "AlarmObserved";
            }
            else
            {
                if (instances.Count(instance => instance.Lifecycle != AlarmLifecycle.Cleared) >=
                    current.Policy.MaximumActiveInstances)
                    return Reject(CapacityExceeded, auditAtUtc, observation.Code, observation.Source);
                updated = Snapshot(current.Policy, rule, null, observation,
                    AlarmLifecycle.Active, acknowledged: false);
                instances.Add(updated);
                events.Add(Event(updated, AlarmTransitionKind.Raised, observation.ObservedAtUtc,
                    auditAtUtc, "AlarmRaised"));
                outcome = "AlarmRaised";
            }
        }
        else if (active)
        {
            var updated = Snapshot(current.Policy, rule, sameCodeSource!, observation,
                rule.IsLatched ? AlarmLifecycle.RecoveredLatched : AlarmLifecycle.Cleared,
                sameCodeSource!.Acknowledged);
            Replace(instances, updated);
            events.Add(Event(updated, AlarmTransitionKind.SourceRecovered, observation.ObservedAtUtc,
                auditAtUtc, "AlarmSourceRecovered"));
            if (!rule.IsLatched)
                events.Add(Event(updated, AlarmTransitionKind.Cleared, observation.ObservedAtUtc,
                    auditAtUtc, "AlarmCleared"));
            outcome = rule.IsLatched ? "AlarmSourceRecovered" : "AlarmCleared";
        }
        else
        {
            events.Add(Event(null, AlarmTransitionKind.Observed, observation.ObservedAtUtc,
                auditAtUtc, "AlarmObserved", observation.Code, observation.Source));
            outcome = "AlarmObserved";
        }

        var beforeProjection = Project(current.Policy, current.Instances);
        var afterProjection = Project(current.Policy, instances);
        if (!ProjectionEqual(beforeProjection, afterProjection))
            events.Add(ProjectionEvent(afterProjection, observation.ObservedAtUtc, auditAtUtc));
        return Success(outcome, events);
    }

    internal static AlarmTransitionDecision DecideCommand(AlarmStateSnapshot current,
        RuntimeCommand command, Guid runtimeEpoch, bool sourceFresh,
        AlarmResetPrerequisites unmetPrerequisites, DateTimeOffset auditAtUtc)
    {
        if (current is null || !current.Available || current.Policy is null)
            return RejectWithoutEvent(StateUnavailable);
        if (runtimeEpoch != current.RuntimeEpoch)
            return RejectWithoutEvent(RuntimeEpochMismatch);
        if (auditAtUtc == default)
            return RejectWithoutEvent(ObservationInvalid);
        if (!HasKnownPrerequisites(unmetPrerequisites))
            return RejectWithoutEvent("AlarmResetPrerequisitesInvalid");

        Guid instanceId;
        if (command is AcknowledgeAlarmCommand acknowledge)
            instanceId = acknowledge.AlarmInstanceId;
        else if (command is ResetAlarmCommand resetCommand)
            instanceId = resetCommand.AlarmInstanceId;
        else
            return RejectWithoutEvent("AlarmCommandUnsupported");

        if (instanceId == Guid.Empty)
            return RejectWithoutEvent("AlarmInstanceNotFound");
        var instance = current.Instances.SingleOrDefault(candidate => candidate.InstanceId == instanceId &&
            candidate.Lifecycle != AlarmLifecycle.Cleared);
        if (instance is null)
            return RejectWithoutEvent("AlarmInstanceNotFound");

        if (command is AcknowledgeAlarmCommand)
        {
            if (instance.Acknowledged)
                return RejectWithoutEvent("AlarmAlreadyAcknowledged");
            var updated = instance with
            {
                Acknowledged = true,
                AcknowledgedBy = null,
                AcknowledgedAtUtc = auditAtUtc,
                LastTransitionPosition = 0
            };
            return Success(AcknowledgeReason, new[]
            {
                Event(updated, AlarmTransitionKind.Acknowledged, auditAtUtc, auditAtUtc,
                    AcknowledgeReason)
            });
        }

        if (!instance.IsLatched)
            return RejectWithoutEvent("AlarmResetNotLatched");
        if (!instance.SourceHealthy)
            return RejectWithoutEvent("AlarmSourceNotHealthy");
        if (instance.SourceRuntimeEpoch != runtimeEpoch)
            return RejectWithoutEvent("AlarmSourceRuntimeEpochMismatch");
        if (!sourceFresh)
            return RejectWithoutEvent("AlarmSourceObservationStale");
        if (unmetPrerequisites != AlarmResetPrerequisites.None)
            return RejectWithoutEvent("AlarmResetPrerequisitesUnmet");

        var reset = instance with { LastTransitionPosition = 0 };
        var cleared = instance with { Lifecycle = AlarmLifecycle.Cleared, LastTransitionPosition = 0 };
        var events = new List<AlarmHistoryRecord>
        {
            Event(reset, AlarmTransitionKind.Reset, auditAtUtc, auditAtUtc, ResetReason),
            Event(cleared, AlarmTransitionKind.Cleared, auditAtUtc, auditAtUtc, ResetReason)
        };
        var beforeProjection = Project(current.Policy, current.Instances);
        var afterInstances = current.Instances.Select(candidate =>
            candidate.InstanceId == instance.InstanceId ? cleared : candidate).ToArray();
        var afterProjection = Project(current.Policy, afterInstances);
        if (!ProjectionEqual(beforeProjection, afterProjection))
            events.Add(ProjectionEvent(afterProjection, auditAtUtc, auditAtUtc));
        return Success(ResetReason, events);
    }

    private static AlarmTransitionDecision Success(string reason, IEnumerable<AlarmHistoryRecord> events) =>
        new(true, reason, new ReadOnlyCollection<AlarmHistoryRecord>(events.ToArray()));

    private static AlarmTransitionDecision Reject(string reason, DateTimeOffset auditAtUtc,
        string? code = null, string? source = null, Guid? instanceId = null,
        AlarmInstanceSnapshot? instance = null, bool includeEvent = true)
    {
        if (!includeEvent)
            return new(false, reason, Array.Empty<AlarmHistoryRecord>());
        var observed = auditAtUtc == default ? DateTimeOffset.UnixEpoch : auditAtUtc;
        return new(false, reason, new ReadOnlyCollection<AlarmHistoryRecord>(new[]
        {
            new AlarmHistoryRecord(0, Guid.NewGuid(), instanceId ?? instance?.InstanceId,
                code ?? instance?.Code, source ?? instance?.Source, AlarmTransitionKind.BoundaryRejected,
                observed, observed, null, null, null, reason, null, null)
        }));
    }

    private static AlarmTransitionDecision RejectWithoutEvent(string reason) =>
        new(false, reason, Array.Empty<AlarmHistoryRecord>());

    private static AlarmHistoryRecord Event(AlarmInstanceSnapshot? instance,
        AlarmTransitionKind transition, DateTimeOffset observedAtUtc, DateTimeOffset auditAtUtc,
        string reason, string? code = null, string? source = null) =>
        new(0, Guid.NewGuid(), instance?.InstanceId, code ?? instance?.Code, source ?? instance?.Source,
            transition, observedAtUtc, auditAtUtc, null, null, null, reason, instance, null);

    private static AlarmHistoryRecord ProjectionEvent(AlarmPlcProjection projection,
        DateTimeOffset observedAtUtc, DateTimeOffset auditAtUtc) =>
        new(0, Guid.NewGuid(), null, null, null, AlarmTransitionKind.ProjectionChanged,
            observedAtUtc, auditAtUtc, null, null, null, "AlarmProjectionChanged", null, projection);

    private static AlarmInstanceSnapshot Snapshot(AlarmPolicy policy, AlarmPolicyRule rule,
        AlarmInstanceSnapshot? previous, AlarmObservation observation, AlarmLifecycle lifecycle,
        bool acknowledged)
    {
        var first = previous?.FirstObservedAtUtc ?? observation.ObservedAtUtc;
        return new(previous?.InstanceId ?? Guid.NewGuid(), rule.Code, rule.Source, policy.Id, policy.Version,
            policy.ContentHash, rule.Severity, rule.ProductionImpact, rule.IsLatched, rule.Notification,
            rule.PlcCode, rule.PlcPriority, rule.ResetPrerequisites, first, observation.ObservedAtUtc,
            observation.SourceHealthy, observation.RuntimeEpoch, observation.Sequence, lifecycle,
            acknowledged, previous?.AcknowledgedBy, previous?.AcknowledgedAtUtc, 0);
    }

    private static void Replace(List<AlarmInstanceSnapshot> instances, AlarmInstanceSnapshot replacement)
    {
        var index = instances.FindIndex(instance => instance.InstanceId == replacement.InstanceId);
        if (index < 0) instances.Add(replacement);
        else instances[index] = replacement;
    }

    private static bool ProjectionEqual(AlarmPlcProjection first, AlarmPlcProjection second) =>
        first.TotalUncleared == second.TotalUncleared && first.BlockingCount == second.BlockingCount &&
        first.FaultAbortPresent == second.FaultAbortPresent && first.Entries.SequenceEqual(second.Entries);

    private static bool HasKnownPrerequisites(AlarmResetPrerequisites value) =>
        (value & ~(AlarmResetPrerequisites.NoActiveExecution | AlarmResetPrerequisites.NoPendingDelivery |
            AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoExclusiveMode)) == 0;

    private static bool IsSafeIdentifier(string value) =>
        value is { Length: >= 1 and <= 64 } && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
}
