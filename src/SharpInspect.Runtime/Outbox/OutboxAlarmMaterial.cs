using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Outbox;

internal static class OutboxAlarmMaterial
{
    internal const string BestEffortCode = "OutboxBestEffortDeliveryFailed";
    internal const string Source = "Runtime.Outbox";

    internal static bool IsNonBlockingInstance(AlarmPolicy policy, AlarmInstanceSnapshot instance) =>
        instance.Code == BestEffortCode && instance.Source == Source &&
        instance.PolicyId == policy.Id && instance.PolicyVersion == policy.Version &&
        instance.PolicyContentHash == policy.ContentHash &&
        policy.TryGetRule(BestEffortCode, out var rule) && rule is not null && rule.Source == Source &&
        rule.ProductionImpact == ProductionImpact.None && instance.ProductionImpact == ProductionImpact.None &&
        instance.Severity == rule.Severity && instance.IsLatched == rule.IsLatched &&
        instance.Notification == rule.Notification && instance.PlcCode == rule.PlcCode &&
        instance.PlcPriority == rule.PlcPriority && instance.ResetPrerequisites == rule.ResetPrerequisites;

    internal static bool ProjectionEqual(AlarmPlcProjection left, AlarmPlcProjection right) =>
        left.TotalUncleared == right.TotalUncleared && left.BlockingCount == right.BlockingCount &&
        left.FaultAbortPresent == right.FaultAbortPresent && left.Entries.SequenceEqual(right.Entries);
}
