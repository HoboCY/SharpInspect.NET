namespace SharpInspect.Runtime.Qualification;

/// <summary>Explicit alarm-policy keys for the isolated cycle entry.</summary>
public static class QualificationCycleAlarmCodes
{
    public const string Source = "Runtime.QualificationCycle";
    public const string TracePersistenceFailed = "QualificationCycleTracePersistenceFailed";
    public const string ResultAckTimeout = "QualificationResultAckTimeout";
    public const string TriggerRejected = "QualificationTriggerRejectedWhileBusy";
    public const string Interrupted = "QualificationCycleInterrupted";
}
