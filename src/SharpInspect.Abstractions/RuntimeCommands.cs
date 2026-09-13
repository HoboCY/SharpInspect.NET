namespace SharpInspect.Abstractions;

public enum CommandSource { PhysicalConsole, Integration }
public enum CommandDisposition { Accepted, Rejected }

/// <summary>Attribution only. Supplied identities and grants never prove authorization.</summary>
public sealed record CommandInvocation(CommandSource Source, string? PrincipalId = null,
    Guid? SessionId = null, Guid? StepUpGrantId = null);

// 调用方请求改变工位状态时，应通过强类型命令进入 Runtime；Invocation 仅提供归因上下文，不能代替授权校验。
public abstract record RuntimeCommand(Guid CorrelationId, CommandInvocation Invocation);
public sealed record ArmProductionCommand(Guid CorrelationId, CommandInvocation Invocation)
    : RuntimeCommand(CorrelationId, Invocation);
public sealed record GracefulProductionStopCommand(Guid CorrelationId, CommandInvocation Invocation)
    : RuntimeCommand(CorrelationId, Invocation);

public sealed record AcknowledgeAlarmCommand(Guid CorrelationId, CommandInvocation Invocation, Guid AlarmInstanceId)
    : RuntimeCommand(CorrelationId, Invocation);
public sealed record ResetAlarmCommand(Guid CorrelationId, CommandInvocation Invocation, Guid AlarmInstanceId)
    : RuntimeCommand(CorrelationId, Invocation);

public enum GovernedAuditChangeKind { RotateSigningKey, RetireSigningKey, CorrectHistoricalFact, DeleteEvidence }
/// <summary>The authorization boundary remains closed until governed identity and maintenance are available.</summary>
public sealed record GovernedAuditChangeCommand(Guid CorrelationId, CommandInvocation Invocation,
    GovernedAuditChangeKind Change) : RuntimeCommand(CorrelationId, Invocation);

/// <summary>Acceptance is admission of responsibility, not completion; query subsequent snapshots.</summary>
public sealed record RuntimeCommandOutcome(Guid CorrelationId, CommandDisposition Disposition, string ReasonCode,
    AuditPersistence Audit = AuditPersistence.NotAttempted, Guid? AttemptId = null)
{
    /// <summary>Exact attempted admission evidence; this projection cannot authorize a later command.</summary>
    public ProductionAdmissionReport? ProductionAdmission { get; init; }
}

public interface IStationRuntime
{
    ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(CancellationToken cancellationToken = default);
    // Accepted 表示命令在本次准入/执行边界被接受；涉及后台操作时，仍需查询后续快照/进度确定终态。
    ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command, CancellationToken cancellationToken = default);
}
