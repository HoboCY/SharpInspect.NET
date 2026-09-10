using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    private ManualInspectionSessionHeader CreateManualInspectionHeader(StartManualInspectionSessionCommand command,
        Guid runtimeEpoch, Guid attemptId, RecipeDraftRevision draft, CameraBindingRevision binding,
        RecipeActivationRecord? baseline, LocalAdministratorState actor, Guid actorSessionId, DateTimeOffset now)
    {
        return new(1, Guid.NewGuid(), runtimeEpoch, command.CorrelationId, attemptId, command.Selection,
            draft.Content.ContentHash, binding, baseline?.Reference, baseline?.SuccessfulSnapshot?.ContentHash,
            baseline?.SuccessfulSnapshot is { } snapshot ? RecipeActivationValidation.CameraHash(snapshot.CameraSetup) : null,
            actor.PrincipalId, actorSessionId, actor.AuthorizationRevision,
            new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash),
            command.AuthorizationTarget, command.Reason, now, ManualInspectionSessionPhase.Admitted,
            ManualInspectionRestorationState.Pending, "ManualInspectionSessionAdmitted");
    }

    private static ManualInspectionSessionHeader TransitionManualInspectionHeader(ManualInspectionSessionHeader current,
        ManualInspectionSessionPhase phase, ManualInspectionRestorationState restoration, string reason,
        bool recoveryRequired) => new(current.Position, current.SessionId, current.RuntimeEpoch,
            current.StartCorrelationId, current.AttemptId, current.Selection, current.SourceContentHash,
            current.CurrentBinding, current.ActiveActivation, current.ActiveSnapshotContentHash,
            current.ActiveCameraContentHash, current.ActorPrincipalId, current.ActorSessionId,
            current.ActorAuthorizationRevision, current.AuthorizationPolicy, current.AuthorizationTarget,
            current.ChangeReason, current.StartedAtUtc, phase, restoration, reason, recoveryRequired);

    private static Guid? ManualInspectionCommandSessionId(ManualInspectionCommand command) => command switch
    {
        RunManualInspectionCommand run => run.SessionId,
        ExitManualInspectionSessionCommand exit => exit.SessionId,
        ManualInspectionContinuationCommand continuation => continuation.SessionId,
        _ => null
    };
}

/// <summary>Private completion authority binds an existing accepted command, never new admission.</summary>
internal sealed record ManualInspectionContinuationCommand : ManualInspectionCommand
{
    internal ManualInspectionContinuationCommand(Guid correlationId, CommandInvocation invocation,
        Guid sessionId, AuditedCommandKind originalCommandKind, string authorizationTarget, string reason)
        : base(correlationId, invocation, reason)
    {
        SessionId = sessionId;
        OriginalCommandKind = originalCommandKind;
        AuthorizationTarget = authorizationTarget;
    }
    internal Guid SessionId { get; }
    internal AuditedCommandKind OriginalCommandKind { get; }
    public override string AuthorizationTarget { get; }
}
