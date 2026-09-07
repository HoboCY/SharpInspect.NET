namespace SharpInspect.Abstractions;

public enum CommandSource { PhysicalConsole, Integration }
public enum CommandDisposition { Accepted, Rejected }

/// <summary>Attribution only. Supplied identities and grants never prove authorization.</summary>
public sealed record CommandInvocation(CommandSource Source, string? PrincipalId = null,
    Guid? SessionId = null, Guid? StepUpGrantId = null);

public abstract record RuntimeCommand(Guid CorrelationId, CommandInvocation Invocation);
public sealed record ArmProductionCommand(Guid CorrelationId, CommandInvocation Invocation)
    : RuntimeCommand(CorrelationId, Invocation);
public sealed record GracefulProductionStopCommand(Guid CorrelationId, CommandInvocation Invocation)
    : RuntimeCommand(CorrelationId, Invocation);

public enum GovernedAuditChangeKind { RotateSigningKey, RetireSigningKey, CorrectHistoricalFact, DeleteEvidence }
/// <summary>The authorization boundary remains closed until governed identity and maintenance are available.</summary>
public sealed record GovernedAuditChangeCommand(Guid CorrelationId, CommandInvocation Invocation,
    GovernedAuditChangeKind Change) : RuntimeCommand(CorrelationId, Invocation);

/// <summary>Acceptance is admission of responsibility, not completion; query subsequent snapshots.</summary>
public sealed record RuntimeCommandOutcome(Guid CorrelationId, CommandDisposition Disposition, string ReasonCode,
    AuditPersistence Audit = AuditPersistence.NotAttempted, Guid? AttemptId = null);

public interface IStationRuntime
{
    ValueTask<StationStateSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<StationStateSnapshot> WatchSnapshotsAsync(CancellationToken cancellationToken = default);
    ValueTask<RuntimeCommandOutcome> SubmitAsync(RuntimeCommand command, CancellationToken cancellationToken = default);
}
