using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal StationQualificationProgressAuthorization AuthorizeStationQualificationProgress(
        IdentityAuthorityState identity, StationQualificationProgressRequest request)
    {
        // Restoration and the failure/cancellation of an already admitted run
        // remain responsibilities after the operator's authority has ended.
        if (request.Terminal || request.Phase is StationQualificationSessionPhase.Restoring or
            StationQualificationSessionPhase.RecoveryBlocked ||
            request.Run is { Terminal: true, ExecutionStatus: not ExecutionStatus.Success })
            return new(request);

        var header = request.Header;
        var actor = Find(identity, header.ActorPrincipalId);
        SessionAuthorizationLease? lease = null;
        var authorized = actor is { Enabled: true } &&
            actor.Permissions.Contains(Permission.RunStationQualification) &&
            actor.AuthorizationRevision == header.ActorAuthorizationRevision &&
            _options.AuthorizationPolicy.Id == header.AuthorizationPolicy.Id &&
            _options.AuthorizationPolicy.Version == header.AuthorizationPolicy.Version &&
            _options.AuthorizationPolicy.ContentHash == header.AuthorizationPolicy.ContentHash &&
            _sessions is not null && _sessions.TryAcquireAuthorizationLease(
                header.ActorPrincipalId, header.ActorSessionId, out lease, out _);
        if (authorized)
            return new(request, new AuthorizationCommitGuard(lease!, () => { }, () => { }));

        lease?.Dispose();
        if (request.Run is not { Terminal: true, ExecutionStatus: ExecutionStatus.Success } success)
            throw new InvalidOperationException("StationQualificationProgressAuthorityEnded");
        const string reason = "StationQualificationAuthorityEndedBeforeTerminal";
        var cancelled = new StationQualificationRunRecord(success.Position, success.RunId,
            success.SessionId, success.StimulusSequence, success.ScenarioId, success.ContextHash,
            success.ControllerEpoch, success.CycleSequence, success.AdmittedAtUtc, success.CompletedAtUtc,
            ExecutionStatus.Cancelled, InspectionDecision.Unknown, reason, success.FrameMetadata,
            success.FrameProvenance, null, null, null, success.Timing);
        return new(request with { Run = cancelled, ReasonCode = reason });
    }
}
