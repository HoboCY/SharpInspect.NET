using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Cameras;

/// <summary>Internal durable boundary for the immutable imaging declaration ledger.</summary>
internal interface IImagingSetupRevisionPersistence
{
    ValueTask<ImagingSetupStoreSnapshot> ReadAsync(string logicalCameraRole,
        CancellationToken cancellationToken = default);
    ValueTask<ImagingSetupHistoryResult> QueryHistoryAsync(string logicalCameraRole,
        long afterPosition, long? throughPosition, int pageSize,
        CancellationToken cancellationToken = default);
    ValueTask<StoreWriteResult> AppendAsync(ImagingSetupPersistenceRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default);
}

internal sealed record ImagingSetupStoreSnapshot(string LogicalCameraRole,
    IReadOnlyList<ImagingSetupRevision> Revisions)
{
    internal ImagingSetupRevision? Current => Revisions.Count == 0 ? null : Revisions[^1];
    internal long ThroughPosition => Revisions.Count == 0 ? 0 : Revisions.Max(item => item.Position);
}

internal sealed record ImagingSetupReadResult(ImagingSetupStoreSnapshot State);

internal sealed record ImagingSetupPersistenceRequest(CommandAuditFact CommandFact,
    ImagingSetupChangeRequest Change, CameraSetupAuthorization Authorization)
{
    internal Guid OperationId => Change.OperationId;
    internal string LogicalCameraRole => Change.LogicalCameraRole;
}

internal sealed record ImagingSetupRevisionMutation(ImagingSetupChangeRequest Change,
    CameraBindingRevision Binding, Guid ActorPrincipalId, Guid SessionId,
    long AuthorizationRevision, ImagingSetupChangeOrigin Origin = ImagingSetupChangeOrigin.OperatorDeclared);

internal sealed record ImagingSetupPersistenceCommit(StoreWriteResult Result,
    ImagingSetupRevision? Revision);

internal sealed class SqliteImagingSetupRevisionPersistence : IImagingSetupRevisionPersistence
{
    private readonly SqliteCommandStore _store;

    internal SqliteImagingSetupRevisionPersistence(SqliteCommandStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<ImagingSetupStoreSnapshot> ReadAsync(string logicalCameraRole,
        CancellationToken cancellationToken = default)
    {
        var result = await _store.ReadImagingSetupAsync(logicalCameraRole, cancellationToken)
            .ConfigureAwait(false);
        return result.State;
    }

    public async ValueTask<ImagingSetupHistoryResult> QueryHistoryAsync(string logicalCameraRole,
        long afterPosition, long? throughPosition, int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (afterPosition < 0 || throughPosition is < 0 || pageSize is < 1 or > 256)
            return new ImagingSetupHistoryResult(false, "ImagingSetupHistoryCursorInvalid",
                Array.Empty<ImagingSetupRevision>(), 0, null);
        var state = await ReadAsync(logicalCameraRole, cancellationToken).ConfigureAwait(false);
        var through = throughPosition ?? state.ThroughPosition;
        if (through < afterPosition || through > state.ThroughPosition)
            return new ImagingSetupHistoryResult(false, "ImagingSetupHistoryCursorInvalid",
                Array.Empty<ImagingSetupRevision>(), state.ThroughPosition, null);
        var page = state.Revisions.Where(item => item.Position > afterPosition && item.Position <= through)
            .Take(pageSize).ToArray();
        var next = page.Length == pageSize && page.Length > 0 &&
            state.Revisions.Any(item => item.Position > page[^1].Position && item.Position <= through)
            ? (long?)page[^1].Position : null;
        return new ImagingSetupHistoryResult(true, "ImagingSetupHistoryAvailable", page, through, next);
    }

    public async ValueTask<StoreWriteResult> AppendAsync(ImagingSetupPersistenceRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deadline);
        try
        {
            var result = await _store.UpdateImagingSetupAsync(request.OperationId,
                request.LogicalCameraRole,
                (state, camera, imaging, duplicate) => CreateUpdate(request, state, camera, imaging, duplicate),
                cancellationToken, deadline).ConfigureAwait(false);
            return result.Result is ImagingSetupPersistenceCommit commit
                ? commit.Result : new StoreWriteResult(false, result.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new StoreWriteResult(false, SafeReason(ex is InvalidOperationException ? ex.Message : null)); }
    }

    private static IdentityUpdate CreateUpdate(ImagingSetupPersistenceRequest request,
        IdentityAuthorityState identity, CameraSetupStoreSnapshot camera,
        ImagingSetupStoreSnapshot imaging, bool storeReportsDuplicate)
    {
        var existing = imaging.Revisions.SingleOrDefault(item => item.OperationId == request.OperationId);
        if (storeReportsDuplicate || existing is not null)
        {
            if (existing is not null && MatchesExisting(request, existing))
                return new IdentityUpdate(new ImagingSetupPersistenceCommit(
                    new StoreWriteResult(true, "ImagingSetupAlreadyPersisted"), existing),
                    Array.Empty<IdentityAuditEvent>(), NoMutation: true);
            return Rejected(request, identity, "ImagingSetupOperationConflict");
        }

        var binding = camera.Binding;
        if (binding is null || binding.Revision != request.Change.ExpectedBindingRevision ||
            binding.RevisionHash != request.Change.ExpectedBindingRevisionHash)
            return Rejected(request, identity, "CameraBindingRevisionConflict");
        var current = imaging.Current;
        if ((current?.Revision ?? 0) != request.Change.ExpectedRevision ||
            current?.RevisionHash != request.Change.ExpectedRevisionHash)
            return Rejected(request, identity, "ImagingSetupRevisionConflict");

        var actor = identity.EnumerateAccounts().SingleOrDefault(item =>
            item.PrincipalId == request.Authorization.PrincipalId);
        if (actor is not { Enabled: true }) return Rejected(request, identity, "AuthorizationStale");
        if (!actor.Permissions.Contains(Permission.ManageCameraBindings))
            return Rejected(request, identity, "PermissionDenied");
        if (actor.AuthorizationRevision != request.Authorization.AuthorizationRevision)
            return Rejected(request, identity, "AuthorizationStale");
        if (request.Authorization.Reservation is not { } reservation)
            return Rejected(request, identity, "AuthorizationReservationUnavailable");
        if (!LocalAuthorizationService.MatchesCameraSetupAuthorization(reservation,
                request.OperationId, request.Change.AuthorizationTarget,
                AuditedCommandKind.DeclareImagingSetup))
            return Rejected(request, identity, "StepUpInvalid");
        var guard = LocalAuthorizationService.AcquireCameraSetupCommitGuard(reservation,
            identity, requireActiveGrant: true, out var guardReason);
        if (guard is null) return Rejected(request, identity, guardReason);

        var fact = request.CommandFact with
        {
            AuthenticatedHumanPrincipalId = request.Authorization.PrincipalId.ToString("D"),
            ClaimedPrincipalId = request.Authorization.PrincipalId.ToString("D"),
            ClaimedSessionId = request.Authorization.SessionId,
            CommandKind = AuditedCommandKind.DeclareImagingSetup,
            ReasonCode = "ImagingSetupRevisionPersisted",
            Disposition = CommandDisposition.Accepted,
            Phase = CommandAuditPhase.Outcome
        };
        var terminal = fact with
        {
            EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed, Disposition = null
        };
        var authorization = new IdentityAuditEvent(Guid.NewGuid(),
            IdentityEventKind.CameraSetupActionAuthorized, fact.OccurredAtUtc, identity.StationId,
            request.Authorization.PrincipalId, null, null, null, fact.ReasonCode,
            ActorPrincipalId: request.Authorization.PrincipalId,
            CommandCorrelationId: fact.CorrelationId, StepUpGrantId: fact.ClaimedStepUpGrantId,
            RequiredPermission: Permission.ManageCameraBindings.ToString(),
            AuthorizationRevision: request.Authorization.AuthorizationRevision,
            ActionTargetId: request.Change.AuthorizationTarget,
            BoundCommandCorrelationId: fact.CorrelationId,
            ActionCommandKind: AuditedCommandKind.DeclareImagingSetup.ToString(),
            OperationId: request.OperationId, SessionId: request.Authorization.SessionId);
        var completed = authorization with
        {
            EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
            OccurredAtUtc = terminal.OccurredAtUtc
        };
        var mutation = new ImagingSetupRevisionMutation(request.Change, binding,
            request.Authorization.PrincipalId, request.Authorization.SessionId,
            request.Authorization.AuthorizationRevision);
        var result = new IdentityUpdate(new ImagingSetupPersistenceCommit(
                new StoreWriteResult(true, "ImagingSetupPersisted", terminal), null),
            new[] { authorization, completed }, new[] { fact, terminal }, CommitGuard: guard,
            ImagingRevision: mutation);
        return result;
    }

    private static IdentityUpdate Rejected(ImagingSetupPersistenceRequest request,
        IdentityAuthorityState identity, string reason)
    {
        var safe = SafeReason(reason);
        var fact = request.CommandFact with
        {
            EventId = Guid.NewGuid(), CommandKind = AuditedCommandKind.DeclareImagingSetup,
            Phase = CommandAuditPhase.Outcome, Disposition = CommandDisposition.Rejected,
            ReasonCode = safe, AuthenticatedHumanPrincipalId = request.Authorization.PrincipalId == Guid.Empty
                ? null : request.Authorization.PrincipalId.ToString("D")
        };
        var audit = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.CameraSetupActionAuthorized,
            fact.OccurredAtUtc, identity.StationId,
            request.Authorization.PrincipalId == Guid.Empty ? null : request.Authorization.PrincipalId,
            null, null, null, safe,
            ActorPrincipalId: request.Authorization.PrincipalId == Guid.Empty ? null : request.Authorization.PrincipalId,
            CommandCorrelationId: fact.CorrelationId, StepUpGrantId: fact.ClaimedStepUpGrantId,
            RequiredPermission: Permission.ManageCameraBindings.ToString(),
            AuthorizationRevision: request.Authorization.AuthorizationRevision,
            ActionTargetId: request.Change.AuthorizationTarget,
            BoundCommandCorrelationId: fact.CorrelationId,
            ActionCommandKind: AuditedCommandKind.DeclareImagingSetup.ToString(),
            OperationId: request.OperationId, SessionId: request.Authorization.SessionId);
        return new IdentityUpdate(new ImagingSetupPersistenceCommit(
                new StoreWriteResult(false, safe, fact), null), new[] { audit }, new[] { fact });
    }

    private static bool MatchesExisting(ImagingSetupPersistenceRequest request, ImagingSetupRevision existing)
    {
        var change = request.Change;
        return existing.LogicalCameraRole == change.LogicalCameraRole &&
            existing.Binding.Revision == change.ExpectedBindingRevision &&
            existing.Binding.RevisionHash == change.ExpectedBindingRevisionHash &&
            existing.Revision == change.ExpectedRevision + 1 &&
            existing.PreviousRevisionHash == change.ExpectedRevisionHash &&
            existing.Definition.ContentHash == change.Definition.ContentHash &&
            existing.ChangeReason == change.ChangeReason &&
            existing.ActorPrincipalId == request.Authorization.PrincipalId &&
            existing.SessionId == request.Authorization.SessionId;
    }

    private static string SafeReason(string? reason) => reason is { Length: > 0 and <= 128 } &&
        reason.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? reason : "ImagingSetupUnavailable";
}

internal static class ImagingSetupRevisionPersistenceFactory
{
    internal static IImagingSetupRevisionPersistence? Create(ICommandAuditWriter? audit) =>
        audit is SqliteCommandStore { ImagingSetupEnabled: true } store
            ? new SqliteImagingSetupRevisionPersistence(store) : null;
}
