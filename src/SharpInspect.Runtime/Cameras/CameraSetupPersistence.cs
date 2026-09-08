using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Cameras;

/// <summary>
/// Explicit internal bridge for durable camera setup evidence.  The SQLite store
/// implements this boundary; the coordinator never writes camera tables itself.
/// Each append must atomically persist the supplied command fact and camera event
/// in the store's signed transaction.
/// </summary>
internal interface ICameraSetupPersistence
{
    ValueTask<CameraSetupPersistentState> ReadCameraSetupAsync(string logicalRole,
        CancellationToken cancellationToken = default);

    ValueTask<StoreWriteResult> AppendCameraSetupAsync(CameraSetupPersistenceRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default);
}

internal sealed class CameraSetupPersistentState
{
    internal CameraSetupPersistentState(CameraSetupSnapshot? snapshot, bool pending,
        string reasonCode = "CameraSetupUnconfigured")
    {
        Snapshot = snapshot;
        Pending = pending;
        ReasonCode = reasonCode;
    }

    internal CameraSetupSnapshot? Snapshot { get; }
    internal bool Pending { get; }
    internal string ReasonCode { get; }
}

internal enum CameraSetupPersistencePhase
{
    Admission,
    Terminal
}

/// <summary>One bounded, fully typed camera event supplied to the durable writer.</summary>
internal sealed class CameraSetupPersistenceEvent
{
    internal CameraSetupPersistenceEvent(CameraSetupPersistencePhase phase, Guid operationId, string logicalRole,
        AuditedCommandKind commandKind, CameraBindingRevision? previousBinding,
        CameraBindingRevision? binding, CameraBindingTarget? target,
        RequestedCameraConfiguration? requested, EffectiveCameraConfiguration? effective,
        IEnumerable<CameraConfigurationDifference>? differences,
        CameraProviderExtensionRequirement? extension, CameraHealthSnapshot? health,
        bool succeeded, string reasonCode, string changeReason,
        Guid actorPrincipalId, Guid sessionId, long authorizationRevision,
        CameraSetupAuthorizationReservation? reservation = null)
    {
        Phase = phase;
        OperationId = operationId;
        LogicalRole = logicalRole ?? throw new ArgumentNullException(nameof(logicalRole));
        CommandKind = commandKind;
        PreviousBinding = previousBinding;
        Binding = binding;
        Target = target;
        Requested = requested;
        Effective = effective;
        Differences = new ReadOnlyCollection<CameraConfigurationDifference>(
            (differences ?? Array.Empty<CameraConfigurationDifference>()).ToArray());
        Extension = extension;
        Health = health;
        Succeeded = succeeded;
        ReasonCode = reasonCode ?? throw new ArgumentNullException(nameof(reasonCode));
        ChangeReason = changeReason ?? throw new ArgumentNullException(nameof(changeReason));
        ActorPrincipalId = actorPrincipalId;
        SessionId = sessionId;
        AuthorizationRevision = authorizationRevision;
        Reservation = reservation;
    }

    internal CameraSetupPersistencePhase Phase { get; }
    internal Guid OperationId { get; }
    internal string LogicalRole { get; }
    internal AuditedCommandKind CommandKind { get; }
    internal CameraBindingRevision? PreviousBinding { get; }
    internal CameraBindingRevision? Binding { get; }
    internal CameraBindingTarget? Target { get; }
    internal RequestedCameraConfiguration? Requested { get; }
    internal EffectiveCameraConfiguration? Effective { get; }
    internal IReadOnlyList<CameraConfigurationDifference> Differences { get; }
    internal CameraProviderExtensionRequirement? Extension { get; }
    internal CameraHealthSnapshot? Health { get; }
    internal bool Succeeded { get; }
    internal string ReasonCode { get; }
    internal string ChangeReason { get; }
    internal Guid ActorPrincipalId { get; }
    internal Guid SessionId { get; }
    internal long AuthorizationRevision { get; }
    internal CameraSetupAuthorizationReservation? Reservation { get; }
}

internal sealed class CameraSetupPersistenceRequest
{
    internal CameraSetupPersistenceRequest(CommandAuditFact commandFact, CameraSetupPersistenceEvent cameraEvent)
    {
        CommandFact = commandFact ?? throw new ArgumentNullException(nameof(commandFact));
        CameraEvent = cameraEvent ?? throw new ArgumentNullException(nameof(cameraEvent));
    }

    internal CommandAuditFact CommandFact { get; }
    internal CameraSetupPersistenceEvent CameraEvent { get; }

    // Keep the writer adapter's admission envelope explicit.  These projections
    // deliberately come from the typed camera event rather than being caller
    // supplied a second time, so the command fact, camera event, and identity
    // authorization context cannot drift inside one SQLite transaction.
    internal Guid OperationId => CameraEvent.OperationId;
    internal string LogicalRole => CameraEvent.LogicalRole;
    internal AuditedCommandKind CommandKind => CameraEvent.CommandKind;
    internal Guid ActorPrincipalId => CameraEvent.ActorPrincipalId;
    internal Guid SessionId => CameraEvent.SessionId;
    internal long AuthorizationRevision => CameraEvent.AuthorizationRevision;
    internal string ReasonCode => CameraEvent.ReasonCode;
    internal string ChangeReason => CameraEvent.ChangeReason;
}

/// <summary>
/// Adapter for the single SQLite writer.  It deliberately uses the store's
/// identity transaction callback rather than issuing a command append followed
/// by a separate camera append.  The latter could leave a command fact without
/// its signed camera projection after a process interruption.
/// </summary>
internal sealed class SqliteCameraSetupPersistence : ICameraSetupPersistence
{
    private readonly SqliteCommandStore _store;

    internal SqliteCameraSetupPersistence(SqliteCommandStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<CameraSetupPersistentState> ReadCameraSetupAsync(string logicalRole,
        CancellationToken cancellationToken = default)
    {
        var result = await _store.ReadCameraSetupAsync(logicalRole, cancellationToken).ConfigureAwait(false);
        var state = result.State;
        return new CameraSetupPersistentState(state.ToPublicResult().Snapshot,
            state.HasPending, !state.HasPending ? result.Result.ReasonCode :
                "CameraSetupOperationPending");
    }

    public async ValueTask<StoreWriteResult> AppendCameraSetupAsync(CameraSetupPersistenceRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deadline);

        IdentityWriteResult result;
        try
        {
            result = await _store.UpdateCameraSetupAsync(request.OperationId, request.LogicalRole,
                (state, cameraState, duplicate) => CreateUpdate(request, state, cameraState, duplicate), cancellationToken, deadline)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new StoreWriteResult(false, "TraceAuditUnavailable"); }

        return result.Result is CameraSetupPersistenceCommit commit
            ? commit.Result
            : new StoreWriteResult(false, result.ReasonCode);
    }

    private static IdentityUpdate CreateUpdate(CameraSetupPersistenceRequest request,
        IdentityAuthorityState state, CameraSetupStoreSnapshot cameraState, bool storeReportsDuplicate)
    {
        var history = cameraState.OperationEvents
            .Where(item => item.OperationId == request.OperationId)
            .ToArray();
        var hasAdmission = history.Any(item => item.Phase == CameraSetupEventPhase.Admission);
        var hasTerminal = history.Any(item => item.Phase is not CameraSetupEventPhase.Admission);
        // The storage callback reports any existing operation row.  A terminal
        // for an existing Apply admission is the one legal continuation; every
        // other repeat is a conflict. Rebind has no durable admission row and is
        // therefore a duplicate only after its completed binding row exists.
        var duplicate = request.CameraEvent.Phase == CameraSetupPersistencePhase.Admission
            ? storeReportsDuplicate || history.Length != 0
            : request.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration
                ? !hasAdmission || hasTerminal
                : history.Length != 0;

        var authorizationFailure = ValidateWriterAuthorization(request, state, cameraState);
        var casFailure = authorizationFailure is null
            ? ValidateBindingCas(request.CameraEvent, cameraState)
            : authorizationFailure;
        if (casFailure is not null)
            return RejectedUpdate(request, state, casFailure);

        // Hold the real session monitor only on the durable writer thread.  The
        // coordinator reservation intentionally carries no monitor-bound lease;
        // the identity service reacquires one here and SqliteCommandStore owns
        // its commit/dispose lifecycle on this same thread.
        IIdentityTransactionGuard? commitGuard = null;
        if (!duplicate)
        {
            if (request.CameraEvent.Reservation is not { } reservation)
                return RejectedUpdate(request, state, "AuthorizationReservationUnavailable");

            commitGuard = LocalAuthorizationService.AcquireCameraSetupCommitGuard(
                reservation, state,
                requireActiveGrant: request.CameraEvent.Phase == CameraSetupPersistencePhase.Admission,
                out var guardReason);
            if (commitGuard is null)
                return RejectedUpdate(request, state, guardReason);
        }

        var handedOff = false;
        try
        {
            var fact = request.CommandFact;
            var eventKind = request.CameraEvent.Phase == CameraSetupPersistencePhase.Admission
                ? IdentityEventKind.CameraSetupActionAuthorized
                : IdentityEventKind.CameraSetupOperationCompleted;
            if (duplicate)
            {
                fact = fact with { EventId = Guid.NewGuid(), Disposition = CommandDisposition.Rejected,
                    ReasonCode = "CameraSetupOperationConflict" };
            }

            var identity = new IdentityAuditEvent(Guid.NewGuid(), eventKind,
                fact.OccurredAtUtc, state.StationId, request.ActorPrincipalId, null, null, null,
                duplicate ? "CameraSetupOperationConflict" : request.ReasonCode,
                ActorPrincipalId: request.ActorPrincipalId, CommandCorrelationId: fact.CorrelationId,
                StepUpGrantId: fact.ClaimedStepUpGrantId,
                RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: request.AuthorizationRevision,
                // Camera change reasons are free-form evidence owned by the typed
                // camera event. IdentityAuditEvent.ManagementReason is a closed
                // IdentityManagementReason enum and must not receive this text.
                ManagementReason: null,
                ActionTargetId: request.LogicalRole, BoundCommandCorrelationId: fact.CorrelationId,
                ActionCommandKind: request.CommandKind.ToString(), OperationId: request.OperationId,
                SessionId: request.SessionId);

            var cameraEvents = duplicate ? Array.Empty<CameraSetupEvent>() :
                BuildCameraEvents(request);
            var persistedFact = fact with { ReasonCode = SafeReason(fact.ReasonCode) };
            var disposition = duplicate
                ? new StoreWriteResult(false, "CameraSetupOperationConflict", persistedFact)
                : new StoreWriteResult(true, "CameraSetupPersisted", persistedFact);
            var result = new IdentityUpdate(new CameraSetupPersistenceCommit(disposition),
                new[] { identity }, new[] { persistedFact }, CommitGuard: commitGuard,
                CameraEvents: cameraEvents);
            handedOff = true;
            return result;
        }
        finally
        {
            if (!handedOff) commitGuard?.Dispose();
        }
    }

    private static IdentityUpdate RejectedUpdate(CameraSetupPersistenceRequest request,
        IdentityAuthorityState state, string reason)
    {
        var safe = SafeReason(reason);
        var fact = request.CommandFact with
        {
            EventId = Guid.NewGuid(),
            Disposition = CommandDisposition.Rejected,
            ReasonCode = safe
        };
        var identity = new IdentityAuditEvent(Guid.NewGuid(),
            request.CameraEvent.Phase == CameraSetupPersistencePhase.Admission
                ? IdentityEventKind.CameraSetupActionAuthorized
                : IdentityEventKind.CameraSetupOperationCompleted,
            fact.OccurredAtUtc, state.StationId, request.ActorPrincipalId, null, null, null, safe,
            ActorPrincipalId: request.ActorPrincipalId, CommandCorrelationId: fact.CorrelationId,
            StepUpGrantId: fact.ClaimedStepUpGrantId,
            RequiredPermission: Permission.ManageCameraBindings.ToString(),
            AuthorizationRevision: request.AuthorizationRevision,
            // Keep the free-form camera reason in CameraEvent.ChangeReason; the
            // identity audit field is reserved for its closed enum vocabulary.
            ManagementReason: null,
            ActionTargetId: request.LogicalRole, BoundCommandCorrelationId: fact.CorrelationId,
            ActionCommandKind: request.CommandKind.ToString(), OperationId: request.OperationId,
            SessionId: request.SessionId);
        return new IdentityUpdate(new CameraSetupPersistenceCommit(
                new StoreWriteResult(false, safe, fact)),
            new[] { identity }, new[] { fact });
    }

    private static string? ValidateWriterAuthorization(CameraSetupPersistenceRequest request,
        IdentityAuthorityState state, CameraSetupStoreSnapshot cameraState)
    {
        var actor = state.EnumerateAccounts().SingleOrDefault(account =>
            account.PrincipalId == request.ActorPrincipalId);
        if (actor is not { Enabled: true }) return "AuthorizationStale";
        if (!actor.Permissions.Contains(Permission.ManageCameraBindings)) return "PermissionDenied";
        if (actor.AuthorizationRevision != request.AuthorizationRevision) return "AuthorizationStale";
        if (request.SessionId == Guid.Empty) return "SessionInvalid";
        return null;
    }

    private static string? ValidateBindingCas(CameraSetupPersistenceEvent input,
        CameraSetupStoreSnapshot state)
    {
        var current = state.Binding;
        // A Rebind terminal publishes the new binding in the same transaction,
        // so its CAS must compare the durable binding with the prior binding.
        // For an initial rebind both are null; comparing against input.Binding
        // would reject that valid transition before the completed row exists.
        var expected = input.Phase == CameraSetupPersistencePhase.Terminal &&
            input.CommandKind == AuditedCommandKind.RebindCamera
            ? input.PreviousBinding
            : input.PreviousBinding ?? input.Binding;
        if (!SameBinding(current, expected)) return "CameraBindingRevisionConflict";
        // A failed Apply terminal deliberately has no Binding or RevisionHash:
        // the admission already owns the current binding and the failed
        // operation must record that it left it unchanged.  Keep this narrow;
        // admissions and successful terminals still require their binding.
        var failedApplyTerminal = input.Phase == CameraSetupPersistencePhase.Terminal &&
            input.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration &&
            !input.Succeeded && input.Binding is null;
        if (failedApplyTerminal)
        {
            if (input.PreviousBinding is not { } previous || input.Target is not { } target ||
                !SameTarget(previous.Target, target))
                return "CameraBindingRevisionConflict";
        }
        else if (input.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration &&
            input.Binding is null)
        {
            return "CameraBindingRevisionConflict";
        }
        if (input.Phase == CameraSetupPersistencePhase.Terminal &&
            input.CommandKind == AuditedCommandKind.RebindCamera && input.Succeeded &&
            input.Binding is null) return "CameraBindingRevisionConflict";
        return null;
    }

    private static bool SameBinding(CameraBindingRevision? left, CameraBindingRevision? right) =>
        left is null && right is null || left is not null && right is not null &&
        left.Revision == right.Revision &&
        string.Equals(left.RevisionHash, right.RevisionHash, StringComparison.Ordinal) &&
        left.Target.ContentHash == right.Target.ContentHash &&
        left.LogicalRole == right.LogicalRole;

    private static bool SameTarget(CameraBindingTarget left, CameraBindingTarget right) =>
        left.ContentHash == right.ContentHash && left.Provider == right.Provider &&
        left.StableDeviceIdentity == right.StableDeviceIdentity;

    private static IReadOnlyList<CameraSetupEvent> BuildCameraEvents(CameraSetupPersistenceRequest request)
    {
        var input = request.CameraEvent;
        // A rebind is represented by its completed binding row.  Apply uses an
        // admission/terminal pair so a crash between the two remains visible as
        // a pending operation on the next verified read.
        if (input.Phase == CameraSetupPersistencePhase.Admission &&
            input.CommandKind == AuditedCommandKind.RebindCamera)
            return Array.Empty<CameraSetupEvent>();

        var phase = input.Phase == CameraSetupPersistencePhase.Admission
            ? CameraSetupEventPhase.Admission
            : input.CommandKind == AuditedCommandKind.RebindCamera && input.Succeeded
                ? CameraSetupEventPhase.Completed
                : input.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration
                    ? CameraSetupEventPhase.Terminal : CameraSetupEventPhase.Rejected;
        var recordedAt = input.CommandKind == AuditedCommandKind.RebindCamera && input.Succeeded &&
            input.Binding is not null
            ? input.Binding.RecordedAtUtc
            : request.CommandFact.OccurredAtUtc;
        var eventValue = new CameraSetupEvent(0, Guid.NewGuid(), input.OperationId, input.LogicalRole,
            input.CommandKind, phase, input.Binding?.Revision ?? input.PreviousBinding?.Revision ?? 0,
            input.PreviousBinding?.RevisionHash, input.Binding?.RevisionHash,
            input.PreviousBinding?.Target, input.Target, input.Requested, input.Effective,
            input.Differences, input.Extension, input.Health, input.Succeeded,
            SafeReason(input.ReasonCode), input.ChangeReason, input.ActorPrincipalId, input.SessionId,
            input.AuthorizationRevision, recordedAt);
        return new[] { eventValue };
    }

    private static string SafeReason(string reason) => reason is { Length: > 0 and <= 128 } &&
        reason.All(static character => character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z' || character is >= '0' and <= '9' ||
            character is '_' or '-') ? reason : "CameraSetupUnavailable";

    private sealed record CameraSetupPersistenceCommit(StoreWriteResult Result);
}

internal static class CameraSetupPersistenceFactory
{
    internal static ICameraSetupPersistence? Create(ICommandAuditWriter? audit) =>
        audit is SqliteCommandStore store ? new SqliteCameraSetupPersistence(store) : null;
}
