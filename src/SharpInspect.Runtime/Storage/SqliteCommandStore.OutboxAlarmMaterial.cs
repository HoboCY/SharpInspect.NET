using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Outbox;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    // Every head reader and commit fence uses this same narrow classification. The signed
    // alarm ledger remains complete; only the production permit's material head is selected.
    private string? ReadOutboxAlarmMaterialHead(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.Outbox is not { } outbox || _options.AlarmPolicy is null ||
            !outbox.Routes.Any(route => route.Criticality == OutboxRouteCriticality.BestEffort)) return null;
        if (ReadUserVersion(database, deadline) != ProductionOutboxStoreOptions.SchemaVersion) return null;
        RequireConfiguredProductionOutbox(database, outbox, deadline);
        var policy = AlarmStorageCodec.ReadPersistedPolicy(database, deadline);
        AlarmStorageCodec.RequireConfiguredPolicy(policy, _options.AlarmPolicy);
        if (policy is null) return null;
        var through = AuditChainDatabase.Scalar(database, "SELECT COALESCE(MAX(Position),0) FROM alarm_events;", deadline);
        if (through > _policy!.MaximumVerificationEntries) return null; // No unbounded exemption scan.
        var instances = new Dictionary<Guid, AlarmInstanceSnapshot>();
        StoredAlarmEvent? previous = null;
        var previousExempt = false;
        string? material = null;
        long after = 0;
        while (after < through)
        {
            var page = AlarmStorageCodec.ReadEventPage(database, new(afterPosition: after, pageSize: 100), through, deadline)
                .Take(100).ToArray();
            if (page.Length == 0) throw new InvalidOperationException("AlarmHistoryShapeInvalid");
            var auditRows = AuditChainDatabase.Read(database, @"
                SELECT AlarmPosition,Payload,Hash FROM audit_entries WHERE Kind='AlarmEvent'
                    AND AlarmPosition>? AND AlarmPosition<=? ORDER BY AlarmPosition LIMIT 101;", deadline,
                row => (Position: SqliteNative.ColumnInt64(row, 0), Payload: SqliteNative.ColumnText(row, 1),
                    Hash: SqliteNative.ColumnText(row, 2)), after.ToString(CultureInfo.InvariantCulture),
                page[^1].Position.ToString(CultureInfo.InvariantCulture));
            if (auditRows.Count != page.Length) throw new InvalidOperationException("AuditAlarmBindingMismatch");
            for (var index = 0; index < page.Length; index++)
            {
                var item = page[index];
                var record = item.Record;
                var audit = auditRows[index];
                if (item.Position != after + 1 || audit.Position != item.Position ||
                    audit.Payload != Convert.ToBase64String(AlarmStorageCodec.Encode(record, item.Policy,
                        item.RuntimeEpoch, item.SystemPrincipalId)))
                    throw new InvalidOperationException("AuditAlarmBindingMismatch");
                var exempt = record.Transition is not (AlarmTransitionKind.PolicyActivated or
                    AlarmTransitionKind.BoundaryRejected or AlarmTransitionKind.ProjectionChanged) &&
                    record.Instance is { } candidate && record.Code == candidate.Code && record.Source == candidate.Source &&
                    record.PlcProjection is null && record.InstanceId == candidate.InstanceId &&
                    OutboxAlarmMaterial.IsNonBlockingInstance(policy, candidate) &&
                    (!instances.TryGetValue(candidate.InstanceId, out var previousInstance) ||
                        OutboxAlarmMaterial.IsNonBlockingInstance(policy, previousInstance));
                if (record.Instance is { } instance)
                {
                    if (record.Transition == AlarmTransitionKind.Cleared) instances.Remove(instance.InstanceId);
                    else instances[instance.InstanceId] = instance;
                }
                if (record.Transition == AlarmTransitionKind.ProjectionChanged && previousExempt && previous is { } prior &&
                    record.Instance is null && record.InstanceId is null && record.Code is null && record.Source is null &&
                    item.RuntimeEpoch == prior.RuntimeEpoch && record.ObservedAtUtc == prior.Record.ObservedAtUtc &&
                    record.AuditAtUtc == prior.Record.AuditAtUtc && record.ActorPrincipalId == prior.Record.ActorPrincipalId &&
                    record.SessionId == prior.Record.SessionId && record.CommandCorrelationId == prior.Record.CommandCorrelationId &&
                    record.PlcProjection is { } projection &&
                    OutboxAlarmMaterial.ProjectionEqual(projection, AlarmTransitions.Project(policy, instances.Values)))
                    exempt = true;
                if (!exempt) material = audit.Hash;
                previous = item;
                previousExempt = exempt && record.Transition != AlarmTransitionKind.ProjectionChanged;
                after = item.Position;
            }
        }
        return material;
    }
}
