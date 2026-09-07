using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string FrameBufferAlarmCode = "FrameBufferExhausted";
    private const string FrameBufferAlarmSource = "Runtime.FrameBufferPool";
    private const AlarmResetPrerequisites FrameBufferResetPrerequisites =
        AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution;

    private bool IsFrameBufferAlarmMappingValid(AlarmPolicy? policy)
    {
        if (_frameBufferPool is null) return true;
        return policy is not null && policy.TryGetRule(FrameBufferAlarmCode, out var rule) &&
            rule is not null && rule.Source == FrameBufferAlarmSource && rule.IsLatched &&
            rule.ProductionImpact == ProductionImpact.BlockNewTriggers &&
            (rule.ResetPrerequisites & FrameBufferResetPrerequisites) == FrameBufferResetPrerequisites;
    }

    private bool HasFrameBufferFaultLocked() =>
        _frameBufferPool?.ProductionFaultLatched == true;

    private void ReconcileFrameBufferPoolLocked()
    {
        if (!HasFrameBufferFaultLocked()) return;
        if (_snapshot.Camera.Buffers == HealthState.Faulted &&
            _snapshot.AdmissionBlockers.Contains(FrameBufferAlarmCode) &&
            !_snapshot.Ready && _snapshot.ArmState == ProductionArmState.Disarmed)
            return;
        PublishLocked(_snapshot);
    }

    private StationStateSnapshot ApplyFrameBufferPoolStateLocked(StationStateSnapshot next)
    {
        if (!HasFrameBufferFaultLocked()) return next;
        var blockers = next.AdmissionBlockers.Where(item => item != FrameBufferAlarmCode).ToList();
        blockers.Add(FrameBufferAlarmCode);
        return next with
        {
            Ready = false,
            ArmState = ProductionArmState.Disarmed,
            Camera = next.Camera with { Buffers = HealthState.Faulted },
            AdmissionBlockers = new AdmissionBlockers(blockers)
        };
    }

    internal FrameBufferPoolSnapshot? GetFrameBufferPoolSnapshot() =>
        _frameBufferPool?.GetSnapshot();
}
