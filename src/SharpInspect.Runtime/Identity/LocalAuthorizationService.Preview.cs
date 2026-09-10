using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// The Preview authority keeps the admitted actor and exact dependency heads in
/// the durable session event. Hardware code receives only the returned command
/// fact and header; it cannot manufacture either one.
/// </summary>
internal sealed partial class LocalAuthorizationService
{
    internal ValueTask<PreviewSessionAccess> GetPreviewAccessAsync(
        CommandInvocation invocation, CancellationToken cancellationToken = default) => QueryAsync(async token =>
        {
            var options = _store.PreviewSessionOptions;
            if (options is null)
                return new PreviewSessionAccess(false, "PreviewSessionConfigurationRequired", false);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason))
                return new PreviewSessionAccess(false, reason,
                    _options.AuthorizationPolicy.RequiresStepUp(Permission.RunPreview));
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunPreview);
                return new PreviewSessionAccess(allowed,
                    allowed ? "PreviewSessionAccessAvailable" : "PermissionDenied",
                    _options.AuthorizationPolicy.RequiresStepUp(Permission.RunPreview));
            }
        }, reason => new PreviewSessionAccess(false, reason,
            _options.AuthorizationPolicy.RequiresStepUp(Permission.RunPreview)), cancellationToken);

    /// <summary>
    /// Rechecks ownership for a background reader. The durable header is the
    /// identity boundary; a still-enabled RunPreview grant with another
    /// authorization revision cannot continue reading this session.
    /// </summary>
    internal ValueTask<PreviewSessionAccess> GetPreviewOwnerAccessAsync(
        PreviewSessionHeader header, CancellationToken cancellationToken = default) => QueryAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(header);
            if (_store.PreviewSessionOptions is null)
                return new PreviewSessionAccess(false, "PreviewSessionConfigurationRequired", false);
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var ownerReason = "AuthenticationRequired";
            if (_sessions is null || !_sessions.TryAcquireAuthorizationLease(header.ActorPrincipalId,
                    header.ActorSessionId, out var lease, out ownerReason))
                return new PreviewSessionAccess(false, ownerReason,
                    _options.AuthorizationPolicy.RequiresStepUp(Permission.RunPreview));
            using (lease)
            {
                var actor = Find(state, header.ActorPrincipalId);
                var policy = new RecipeContractReference(_options.AuthorizationPolicy.Id,
                    _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.RunPreview) &&
                    actor.AuthorizationRevision == header.ActorAuthorizationRevision &&
                    header.AuthorizationPolicy == policy;
                return new PreviewSessionAccess(allowed,
                    allowed ? "PreviewSessionOwnerAvailable" : "PreviewSessionAuthorityChanged",
                    _options.AuthorizationPolicy.RequiresStepUp(Permission.RunPreview));
            }
        }, reason => new PreviewSessionAccess(false, reason,
            _options.AuthorizationPolicy.RequiresStepUp(Permission.RunPreview)), cancellationToken);

    internal async ValueTask<PreviewSessionCommandResult> HandlePreviewCommandAsync(
        PreviewSessionCommand command, Guid runtimeEpoch, Guid attemptId,
        PreviewSessionAdmissionInput? input, PreviewSessionHeader? existing,
        string? forcedRejection, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(deadline);
        PreviewSessionCommandResult Unavailable(string reason) => new(
            new RuntimeCommandOutcome(command.CorrelationId, CommandDisposition.Rejected, reason,
                AuditPersistence.Unavailable, attemptId));
        if (command.CorrelationId == Guid.Empty || runtimeEpoch == Guid.Empty || attemptId == Guid.Empty)
            return Unavailable("InvalidCommandContext");
        try
        {
            // Cancellation is an input to the writer decision. Once the request
            // has entered the queue, a durable rejection/acceptance is returned.
            var write = await _store.UpdatePreviewSessionCommandAsync(command,
                (identity, state, actualInput) => AuthorizePreview(identity, state, command,
                    runtimeEpoch, attemptId, input, actualInput, existing, forcedRejection,
                    cancellationToken), CancellationToken.None, deadline).ConfigureAwait(false);
            return write.Committed && write.Result is PreviewSessionCommandResult result
                ? result : Unavailable(write.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("PreviewSessionAuditUnavailable"); }
    }

    internal ValueTask<PreviewSessionCommandResult> CompletePreviewCommandAsync(
        PreviewSessionHeader header, CommandAuditFact originalCommandFact,
        PreviewSessionCompletion completion, StoreDeadline deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(originalCommandFact);
        ArgumentNullException.ThrowIfNull(completion);
        var command = new PreviewSessionContinuationCommand(originalCommandFact.CorrelationId,
            new CommandInvocation(originalCommandFact.Source ?? CommandSource.PhysicalConsole,
                originalCommandFact.ClaimedPrincipalId, originalCommandFact.ClaimedSessionId,
                originalCommandFact.ClaimedStepUpGrantId), header.SessionId,
            originalCommandFact.CommandKind, header.AuthorizationTarget, header.ChangeReason);
        return CompletePreviewCommandAsync(command, header, originalCommandFact, completion,
            deadline, cancellationToken);
    }

    /// <summary>Completes a command with its original target and invocation context.</summary>
    internal async ValueTask<PreviewSessionCommandResult> CompletePreviewCommandAsync(
        PreviewSessionCommand command, PreviewSessionHeader header,
        CommandAuditFact originalCommandFact, PreviewSessionCompletion completion,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(originalCommandFact);
        ArgumentNullException.ThrowIfNull(completion);
        PreviewSessionCommandResult Unavailable(string reason) => new(
            new RuntimeCommandOutcome(originalCommandFact.CorrelationId,
                CommandDisposition.Rejected, reason, AuditPersistence.Unavailable,
                originalCommandFact.AttemptId));
        if (originalCommandFact.Phase != CommandAuditPhase.Outcome ||
            originalCommandFact.Disposition != CommandDisposition.Accepted ||
            originalCommandFact.AttemptId == Guid.Empty || originalCommandFact.CorrelationId == Guid.Empty ||
            originalCommandFact.RuntimeEpoch == Guid.Empty ||
            originalCommandFact.CommandKind is not (AuditedCommandKind.StartPreview or
                AuditedCommandKind.Tune or AuditedCommandKind.Freeze or
                AuditedCommandKind.SaveRecipeDraft or AuditedCommandKind.Exit) ||
            command.PreviewSessionId != header.SessionId ||
            CommandKind(command) != originalCommandFact.CommandKind ||
            command.CorrelationId != originalCommandFact.CorrelationId)
            return Unavailable("PreviewSessionCompletionInputInvalid");
        try
        {
            var write = await _store.UpdatePreviewSessionCommandAsync(command,
                (identity, state, _) => CompletePreview(identity, state, command, header,
                    originalCommandFact, completion), CancellationToken.None, deadline)
                .ConfigureAwait(false);
            return write.Committed && write.Result is PreviewSessionCommandResult result
                ? result : Unavailable(write.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("PreviewSessionTerminalUnavailable"); }
    }

    /// <summary>
    /// Records the already committed Draft-14 command as the Preview save event.
    /// The writer verifies the exact Draft revision and references its existing
    /// command/authentication evidence; no draft write is performed here.
    /// </summary>
    internal ValueTask<PreviewSessionCommandResult> RecordPreviewDraftSavedAsync(
        PreviewSessionHeader header, CommandAuditFact startCommandFact,
        SavePreviewToDraftCommand originalCommand, RecipeDraftRevision committedDraft,
        StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(startCommandFact);
        ArgumentNullException.ThrowIfNull(originalCommand);
        ArgumentNullException.ThrowIfNull(committedDraft);
        if (startCommandFact.CommandKind != AuditedCommandKind.StartPreview ||
            startCommandFact.Phase != CommandAuditPhase.Outcome ||
            startCommandFact.Disposition != CommandDisposition.Accepted ||
            originalCommand.PreviewSessionId != header.SessionId ||
             originalCommand.CorrelationId != committedDraft.OperationId ||
             originalCommand.ExpectedDraft.DraftId != committedDraft.DraftId ||
             originalCommand.ExpectedDraft.Revision == long.MaxValue ||
             committedDraft.Revision != originalCommand.ExpectedDraft.Revision + 1 ||
            committedDraft.PreviousRevisionContentHash != originalCommand.ExpectedDraft.RevisionContentHash ||
            header.FrozenSettingsContentHash is null ||
            originalCommand.FrozenSettingsContentHash != header.FrozenSettingsContentHash)
            return ValueTask.FromResult(new PreviewSessionCommandResult(new RuntimeCommandOutcome(
                originalCommand.CorrelationId, CommandDisposition.Rejected,
                "PreviewDraftCommandEvidenceInvalid", AuditPersistence.NotAttempted,
                startCommandFact.AttemptId)));
        return RecordPreviewDraftSavedCoreAsync(originalCommand, header, startCommandFact,
            committedDraft, deadline, cancellationToken);
    }

    private async ValueTask<PreviewSessionCommandResult> RecordPreviewDraftSavedCoreAsync(
        SavePreviewToDraftCommand command, PreviewSessionHeader header,
        CommandAuditFact startCommandFact, RecipeDraftRevision committedDraft,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        PreviewSessionCommandResult Unavailable(string reason) => new(
            new RuntimeCommandOutcome(command.CorrelationId,
                CommandDisposition.Rejected, reason, AuditPersistence.Unavailable,
                startCommandFact.AttemptId));
        try
        {
            // The save command is already durable. The dedicated writer path binds
            // its existing Draft-14 command and authorization records into the
            // immutable Preview event.
            var write = await _store.RecordPreviewDraftSavedAsync(header, startCommandFact,
                command, committedDraft, deadline, CancellationToken.None).ConfigureAwait(false);
            return write.Committed && write.Result is PreviewSessionCommandResult result
                ? result : Unavailable(write.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("PreviewDraftAuditUnavailable"); }
    }

    private IdentityUpdate AuthorizePreview(IdentityAuthorityState identity,
        PreviewSessionCommandState state, PreviewSessionCommand command, Guid runtimeEpoch,
        Guid attemptId, PreviewSessionAdmissionInput? callerInput,
        PreviewSessionAdmissionInput? actualInput, PreviewSessionHeader? callerHeader,
        string? forcedRejection, CancellationToken cancellationToken)
    {
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        LocalAdministratorState? actor = null;
        var transferred = false;
        try
        {
            var start = command is StartPreviewSessionCommand;
            var invocation = command.Invocation;
            var reason = state.Enabled ? "Authorized" : "PreviewSessionConfigurationRequired";
            if (reason == "Authorized" && invocation is null)
                reason = "PreviewCommandInvalid";
            if (reason == "Authorized" && invocation!.Source != CommandSource.PhysicalConsole)
                reason = "LocalConsoleRequired";
            if (reason == "Authorized" && !Enum.IsDefined(invocation!.Source))
                reason = "PreviewCommandInvalid";
            var leaseReason = "PreviewLeaseUnavailable";
            if (reason == "Authorized" && !TryLease(invocation!, out lease, out leaseReason))
                reason = leaseReason;
            if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
            var requiredPermission = RequiredPermission(command);
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                !actor.Permissions.Contains(requiredPermission))) reason = "PermissionDenied";
            if (reason == "Authorized" && runtimeEpoch == Guid.Empty) reason = "PreviewRuntimeUnavailable";
            // A rejected Runtime reservation does not read physical dependencies.
            // Preserve that authenticated barrier instead of treating its absent
            // admission input as a dependency change.
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;

            var current = state.Header;
            if (reason == "Authorized" && callerHeader is not null &&
                (current is null || callerHeader.ContentHash != current.ContentHash))
                reason = "PreviewSessionStateChanged";
            if (reason == "Authorized" && start)
            {
                if (state.RecoveryRequired) reason = "PreviewRecoveryRequired";
                else if (state.PendingHeader is not null) reason = "PreviewSessionInProgress";
                else if (state.Events.Count != 0) reason = "PreviewSessionAlreadyUsed";
                else if (actualInput is null || callerInput is null ||
                    !SameAdmission(callerInput, actualInput)) reason = "PreviewAdmissionChanged";
                else if (actualInput.Draft.DraftId != ((StartPreviewSessionCommand)command).Draft.DraftId ||
                    actualInput.Draft.Revision != ((StartPreviewSessionCommand)command).Draft.Revision ||
                    actualInput.Draft.RevisionContentHash != ((StartPreviewSessionCommand)command).Draft.RevisionContentHash)
                    reason = "PreviewDraftChanged";
                else if (((StartPreviewSessionCommand)command).ExpectedActive !=
                    actualInput.ActiveBaseline?.Reference) reason = "PreviewActiveChanged";
            }
            else if (reason == "Authorized")
            {
                if (current is null) reason = "PreviewSessionNotFound";
                else if (!current.IsActive) reason = current.RecoveryRequired ||
                    current.Restoration == PreviewRestorationState.RecoveryBlocked
                    ? "PreviewRecoveryRequired" : "PreviewSessionClosed";
                else if (state.RecoveryRequired) reason = "PreviewRecoveryRequired";
                else if (command.PreviewSessionId != current.SessionId) reason = "PreviewSessionNotFound";
                else if (actor is null || lease is null || actor.PrincipalId != current.ActorPrincipalId ||
                    lease.SessionId != current.ActorSessionId ||
                    actor.AuthorizationRevision != current.ActorAuthorizationRevision)
                    reason = "PreviewSessionAuthorityChanged";
                else if (actualInput is null || actualInput.CurrentBinding.Binding is null ||
                    actualInput.Draft.DraftId != current.Draft.DraftId ||
                    actualInput.Draft.Revision != current.Draft.Revision ||
                    actualInput.Draft.RevisionContentHash != current.Draft.RevisionContentHash)
                    reason = "PreviewSessionDependencyChanged";
                else reason = ValidatePreviewAction(command, current) ?? "Authorized";
            }
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested)
                reason = "PreviewCommandCancelled";
            if (reason == "Authorized")
                reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);

            var accepted = reason == "Authorized";
            var now = PreviewTime(identity, state);
            PreviewSessionHeader? header = current;
            var phase = PreviewPhase(command, accepted);
            if (accepted && start)
                header = CreatePreviewHeader((StartPreviewSessionCommand)command, runtimeEpoch,
                    attemptId, actualInput!, actor!, lease!.SessionId, now);
            else if (accepted && header is not null)
                header = TransitionPreviewHeader(header, phase, PreviewRestorationState.Pending,
                    "PreviewSessionActionAuthorized", recoveryRequired: false,
                    header.FrozenSettingsContentHash, header.LastSavedDraft);

            var finalReason = (accepted ? start ? "PreviewSessionStartAuthorized" :
                "PreviewSessionActionAuthorized" : reason) ?? "PreviewCommandInvalid";
            var fact = new CommandAuditFact(Guid.NewGuid(), attemptId, command.CorrelationId,
                runtimeEpoch, now, CommandKind(command),
                invocation is not null && Enum.IsDefined(invocation.Source) ? invocation.Source : null,
                invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                invocation?.SessionId, invocation?.StepUpGrantId,
                CommandAuditPhase.Outcome, accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                finalReason, actor?.PrincipalId.ToString("D"));
            var identityEventKind = accepted
                ? start ? IdentityEventKind.PreviewSessionStartAuthorized : IdentityEventKind.PreviewSessionActionAuthorized
                : header is not null ? IdentityEventKind.PreviewSessionFailed : IdentityEventKind.ManagementRejected;
            var evidenceActor = actor?.PrincipalId ?? header?.ActorPrincipalId;
            var evidenceSession = lease?.SessionId ?? header?.ActorSessionId;
            var evidenceRevision = actor?.AuthorizationRevision ?? header?.ActorAuthorizationRevision ?? 0;
            var identityEvent = AuthorizationEvent(identity, identityEventKind, finalReason,
                ValidBinding(Binding(command)) ? Binding(command) : null, evidenceActor,
                evidenceSession, invocation?.StepUpGrantId, command.CorrelationId,
                evidenceRevision, targetPrincipalId: evidenceActor,
                capturedTime: now) with { OperationId = header?.SessionId };
            PreviewSessionEvent? eventValue = null;
            if (header is not null && state.Enabled &&
                (accepted || current?.IsActive == true))
            {
                var eventHeader = accepted ? header : current!;
                eventValue = new PreviewSessionEvent(Math.Max(1, state.Events.Count + 1L), eventHeader,
                    attemptId, command.CorrelationId, CommandKind(command),
                    accepted ? phase : eventHeader.Phase, accepted ? PreviewRestorationState.Pending :
                    eventHeader.Restoration, finalReason, terminal: !accepted,
                    evidenceActor ?? eventHeader.ActorPrincipalId,
                    evidenceSession ?? eventHeader.ActorSessionId,
                    evidenceRevision,
                    command.AuthorizationTarget, now, command is ApplyPreviewTuningCommand tune ?
                        tune.Configuration : null, null, eventHeader.FrozenSettingsContentHash);
            }
            var outcome = new RuntimeCommandOutcome(command.CorrelationId,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                finalReason, AuditPersistence.Persisted, attemptId);
            var result = new PreviewSessionCommandResult(outcome,
                accepted ? header : current, eventValue, accepted ? fact : null);
            if (!accepted || eventValue is null)
                return new IdentityUpdate(result, new[] { identityEvent }, new[] { fact });
            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            transferred = true;
            return new IdentityUpdate(result, new[] { identityEvent }, new[] { fact }, guard,
                PreviewSession: new PreviewSessionMutation(header!, eventValue));
        }
        finally
        {
            if (!transferred)
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                lease?.Dispose();
            }
        }
    }

    private IdentityUpdate CompletePreview(IdentityAuthorityState identity,
        PreviewSessionCommandState state, PreviewSessionCommand command,
        PreviewSessionHeader requestedHeader, CommandAuditFact originalFact,
        PreviewSessionCompletion completion)
    {
        var current = state.Header;
        if (!state.Enabled || current is null || current.SessionId != requestedHeader.SessionId ||
            current.ContentHash != requestedHeader.ContentHash || state.RecoveryRequired &&
            current.Phase == PreviewSessionPhase.RecoveryBlocked)
            return PreviewCompletionDecision(identity, command, originalFact, "PreviewSessionStateChanged");
        var start = originalFact.CommandKind == AuditedCommandKind.StartPreview;
        var completesCommand = completion.CompleteCommand;
        var completesStart = completion.CompleteOriginalStart;
        // A Start owns the session lifetime. It may publish finite progress events
        // for the same accepted fact, and is completed exactly once at close. An
        // action may either publish progress or complete its own accepted attempt.
        if (start && completesCommand != completesStart)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionCompletionInputInvalid");
        if (!start && completesStart && originalFact.CommandKind != AuditedCommandKind.Exit)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionCompletionInputInvalid");
        if (!completesCommand && completesStart)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionCompletionInputInvalid");
        if (completesStart && !start && state.StartCommandFact is null)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionStartAuditMissing");
        if (state.Events.Any(value => value.AttemptId == originalFact.AttemptId &&
            value.CommandCorrelationId == originalFact.CorrelationId && value.Terminal &&
            (start || value.CommandKind == originalFact.CommandKind)))
            return new IdentityUpdate(new PreviewSessionCommandResult(
                new RuntimeCommandOutcome(originalFact.CorrelationId,
                    completion.Succeeded ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                    completion.ReasonCode, AuditPersistence.Persisted, originalFact.AttemptId),
                current, state.Events.LastOrDefault(value => value.AttemptId == originalFact.AttemptId &&
                    value.Terminal), originalFact), Array.Empty<IdentityAuditEvent>(),
                NoMutation: true);
        if (!Enum.IsDefined(completion.Phase) || !Enum.IsDefined(completion.Restoration) ||
            completion.ReasonCode is null or { Length: 0 })
            return PreviewCompletionDecision(identity, command, originalFact, "PreviewSessionCompletionInputInvalid");
        var sessionTerminal = completion.Phase is PreviewSessionPhase.Closed or
            PreviewSessionPhase.RecoveryBlocked;
        if (completesStart != sessionTerminal)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionTerminalPhaseInvalid");
        if (!completesCommand && sessionTerminal)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionTerminalPhaseInvalid");
        if (completesCommand && !start && !sessionTerminal &&
            originalFact.CommandKind is not (AuditedCommandKind.Tune or AuditedCommandKind.Freeze or
                AuditedCommandKind.SaveRecipeDraft))
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionActionPhaseInvalid");
        if (!completesCommand && (completion.Phase is PreviewSessionPhase.Idle or
            PreviewSessionPhase.Closed or PreviewSessionPhase.RecoveryBlocked))
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionProgressPhaseInvalid");
        if (sessionTerminal && originalFact.CommandKind != AuditedCommandKind.Exit &&
            !start)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionTerminalCommandInvalid");
        if (sessionTerminal && completion.Restoration == PreviewRestorationState.RecoveryBlocked &&
            completion.Phase != PreviewSessionPhase.RecoveryBlocked)
            return PreviewCompletionDecision(identity, command, originalFact,
                "PreviewSessionRestorationInvalid");
        if (completesStart && completion.Phase is not (PreviewSessionPhase.Closed or
            PreviewSessionPhase.RecoveryBlocked))
            return PreviewCompletionDecision(identity, command, originalFact, "PreviewSessionTerminalPhaseInvalid");

        var now = PreviewTime(identity, state);
        var header = TransitionPreviewHeader(current, completion.Phase, completion.Restoration,
            completion.ReasonCode, sessionTerminal &&
            completion.Phase == PreviewSessionPhase.RecoveryBlocked,
            completion.FrozenSettingsContentHash ?? current.FrozenSettingsContentHash,
            completion.SavedDraft ?? current.LastSavedDraft);
        CommandAuditFact? terminalFact = completesCommand ? new CommandAuditFact(Guid.NewGuid(),
            originalFact.AttemptId, originalFact.CorrelationId, originalFact.RuntimeEpoch, now,
            originalFact.CommandKind, originalFact.Source, originalFact.ClaimedPrincipalId,
            originalFact.ClaimedSessionId, originalFact.ClaimedStepUpGrantId,
            completion.Succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
            null, completion.ReasonCode, originalFact.AuthenticatedHumanPrincipalId) : null;
        CommandAuditFact? startTerminalFact = null;
        if (completesStart && !start)
        {
            var acceptedStart = state.StartCommandFact!;
            startTerminalFact = new CommandAuditFact(Guid.NewGuid(), acceptedStart.AttemptId,
                acceptedStart.CorrelationId, acceptedStart.RuntimeEpoch, now,
                acceptedStart.CommandKind, acceptedStart.Source, acceptedStart.ClaimedPrincipalId,
                acceptedStart.ClaimedSessionId, acceptedStart.ClaimedStepUpGrantId,
                completion.Succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
                null, completion.ReasonCode, acceptedStart.AuthenticatedHumanPrincipalId);
        }
        var kind = completesCommand || completesStart
            ? completion.Succeeded ? IdentityEventKind.PreviewSessionCompleted :
                IdentityEventKind.PreviewSessionFailed
            : IdentityEventKind.PreviewSessionActionAuthorized;
        var authorization = AuthorizationEvent(identity, kind, completion.ReasonCode,
            ValidBinding(Binding(command)) ? Binding(command) : null, current.ActorPrincipalId,
            current.ActorSessionId, originalFact.ClaimedStepUpGrantId, originalFact.CorrelationId,
            current.ActorAuthorizationRevision, targetPrincipalId: current.ActorPrincipalId,
            capturedTime: now) with { OperationId = current.SessionId };
        var eventValue = new PreviewSessionEvent(Math.Max(1, state.Events.Count + 1L), header,
            originalFact.AttemptId, originalFact.CorrelationId, originalFact.CommandKind,
            completion.Phase, completion.Restoration, completion.ReasonCode,
            terminal: completesCommand || completesStart,
            current.ActorPrincipalId, current.ActorSessionId, current.ActorAuthorizationRevision,
            command.AuthorizationTarget, now, completion.EffectiveConfiguration ??
            state.Events.LastOrDefault()?.Configuration, completion.SavedDraft,
            completion.FrozenSettingsContentHash ?? current.FrozenSettingsContentHash);
        var outcome = new RuntimeCommandOutcome(originalFact.CorrelationId,
            completion.Succeeded ? CommandDisposition.Accepted : CommandDisposition.Rejected,
            completion.ReasonCode, AuditPersistence.Persisted, originalFact.AttemptId);
        var facts = terminalFact is null ? null : new[] { terminalFact };
        return new IdentityUpdate(new PreviewSessionCommandResult(outcome, header, eventValue,
            terminalFact ?? originalFact), new[] { authorization }, facts,
            PreviewSession: new PreviewSessionMutation(header, eventValue,
                startTerminalFact is null ? null : new[] { startTerminalFact }));
    }

    private IdentityUpdate PreviewCompletionDecision(IdentityAuthorityState identity,
        PreviewSessionCommand command, CommandAuditFact originalFact, string reason)
    {
        // A malformed Start completion must not retire the Start responsibility:
        // only a durable Closed/RecoveryBlocked Preview event may do that. An
        // action completion can still record its own failed terminal fact.
        var fact = originalFact.CommandKind == AuditedCommandKind.StartPreview ? null :
            new CommandAuditFact(Guid.NewGuid(), originalFact.AttemptId,
                originalFact.CorrelationId, originalFact.RuntimeEpoch, _utcNow().ToUniversalTime(),
                originalFact.CommandKind, originalFact.Source, originalFact.ClaimedPrincipalId,
                originalFact.ClaimedSessionId, originalFact.ClaimedStepUpGrantId,
                CommandAuditPhase.Failed, null, reason, originalFact.AuthenticatedHumanPrincipalId);
        var audit = AuthorizationEvent(identity, IdentityEventKind.PreviewSessionFailed, reason,
            ValidBinding(Binding(command)) ? Binding(command) : null, null,
            originalFact.ClaimedSessionId, originalFact.ClaimedStepUpGrantId,
            originalFact.CorrelationId, 0, capturedTime: fact?.OccurredAtUtc ?? _utcNow().ToUniversalTime());
        return new IdentityUpdate(new PreviewSessionCommandResult(new RuntimeCommandOutcome(
            originalFact.CorrelationId, CommandDisposition.Rejected, reason,
            AuditPersistence.Persisted, originalFact.AttemptId)), new[] { audit },
            fact is null ? null : new[] { fact });
    }

    private static bool SameAdmission(PreviewSessionAdmissionInput expected,
        PreviewSessionAdmissionInput actual) => expected.Draft.DraftId == actual.Draft.DraftId &&
        expected.Draft.Revision == actual.Draft.Revision &&
        expected.Draft.RevisionContentHash == actual.Draft.RevisionContentHash &&
        expected.CurrentBinding.LogicalRole == actual.CurrentBinding.LogicalRole &&
        expected.CurrentBinding.Binding == actual.CurrentBinding.Binding &&
        expected.ActiveBaseline?.Reference == actual.ActiveBaseline?.Reference;

    private static string? ValidatePreviewAction(PreviewSessionCommand command,
        PreviewSessionHeader header)
    {
        switch (command)
        {
            case ApplyPreviewTuningCommand tune:
                return header.Phase is PreviewSessionPhase.Streaming or PreviewSessionPhase.Tuning
                    ? null : "PreviewTuningPhaseInvalid";
            case FreezePreviewSettingsCommand:
                return header.Phase is PreviewSessionPhase.Streaming or PreviewSessionPhase.Tuning
                    ? null : "PreviewFreezePhaseInvalid";
            case SavePreviewToDraftCommand save:
                if (header.FrozenSettingsContentHash is null ||
                    header.FrozenSettingsContentHash != save.FrozenSettingsContentHash)
                    return "PreviewFrozenSettingsMissing";
                return save.ExpectedDraft == header.Draft ? null : "PreviewDraftChanged";
            case ExitPreviewSessionCommand:
                return header.Phase is not (PreviewSessionPhase.Restoring or PreviewSessionPhase.Closed)
                    ? null : "PreviewSessionClosed";
            case PreviewSessionContinuationCommand:
                return null;
            default:
                return "PreviewCommandInvalid";
        }
    }

    private static PreviewSessionPhase PreviewPhase(PreviewSessionCommand command, bool accepted) =>
        !accepted ? PreviewSessionPhase.Idle : command switch
        {
            StartPreviewSessionCommand => PreviewSessionPhase.Admitted,
            ApplyPreviewTuningCommand => PreviewSessionPhase.Tuning,
            FreezePreviewSettingsCommand => PreviewSessionPhase.Freezing,
            SavePreviewToDraftCommand => PreviewSessionPhase.SavingDraft,
            ExitPreviewSessionCommand => PreviewSessionPhase.Restoring,
            PreviewSessionContinuationCommand continuation => continuation.OriginalCommandKind ==
                AuditedCommandKind.StartPreview ? PreviewSessionPhase.Closed : PreviewSessionPhase.Streaming,
            _ => throw new InvalidOperationException("PreviewCommandInvalid")
        };

    private PreviewSessionHeader CreatePreviewHeader(StartPreviewSessionCommand command,
        Guid runtimeEpoch, Guid attemptId, PreviewSessionAdmissionInput input,
        LocalAdministratorState actor, Guid sessionId, DateTimeOffset now)
    {
        var binding = input.CurrentBinding.Binding ?? throw new InvalidOperationException("PreviewCameraBindingMissing");
        var active = input.ActiveBaseline;
        var activeSnapshot = active?.SuccessfulSnapshot?.ContentHash;
        var activeCamera = active?.SuccessfulSnapshot is { } snapshot
            ? RecipeActivationValidation.CameraHash(snapshot.CameraSetup) : null;
        return new PreviewSessionHeader(1, command.PreviewSessionId, runtimeEpoch,
            command.CorrelationId, attemptId, PreviewDraftReference.FromRevision(input.Draft),
            input.Draft.Content.CameraRole, binding, active?.Reference, activeSnapshot,
            activeCamera, actor.PrincipalId, sessionId, actor.AuthorizationRevision,
            new RecipeContractReference(_options.AuthorizationPolicy.Id,
                _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash),
            command.AuthorizationTarget, command.ChangeReason, now);
    }

    private static PreviewSessionHeader TransitionPreviewHeader(PreviewSessionHeader current,
        PreviewSessionPhase phase, PreviewRestorationState restoration, string reason,
        bool recoveryRequired, string? frozenSettingsContentHash,
        PreviewDraftReference? lastSavedDraft) => new(current.Position, current.SessionId,
        current.RuntimeEpoch, current.StartCorrelationId, current.AttemptId, current.Draft,
        current.LogicalCameraRole, current.CurrentBinding, current.ActiveActivation,
        current.ActiveSnapshotContentHash, current.ActiveCameraContentHash,
        current.ActorPrincipalId, current.ActorSessionId, current.ActorAuthorizationRevision,
        current.AuthorizationPolicy, current.AuthorizationTarget, current.ChangeReason,
        current.StartedAtUtc, phase, restoration, reason, recoveryRequired,
        frozenSettingsContentHash, lastSavedDraft);

    private DateTimeOffset PreviewTime(IdentityAuthorityState identity,
        PreviewSessionCommandState state)
    {
        var now = _utcNow().ToUniversalTime();
        if (now < identity.LastObservedUtc) now = identity.LastObservedUtc;
        var last = state.Events.LastOrDefault()?.RecordedAtUtc;
        return last is { } observed && now < observed ? observed : now;
    }
}

/// <summary>Internal continuation command used by cleanup after the original actor has logged out.</summary>
internal sealed record PreviewSessionContinuationCommand : PreviewSessionCommand
{
    internal PreviewSessionContinuationCommand(Guid correlationId, CommandInvocation invocation,
        Guid sessionId, AuditedCommandKind originalCommandKind, string authorizationTarget,
        string changeReason) : base(correlationId, invocation, sessionId, changeReason)
    {
        if (originalCommandKind is not (AuditedCommandKind.StartPreview or AuditedCommandKind.Tune or
            AuditedCommandKind.Freeze or AuditedCommandKind.SaveRecipeDraft or AuditedCommandKind.Exit))
            throw new ArgumentOutOfRangeException(nameof(originalCommandKind));
        OriginalCommandKind = originalCommandKind;
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget,
            nameof(authorizationTarget));
    }

    internal AuditedCommandKind OriginalCommandKind { get; }
    public override string AuthorizationTarget { get; }
}
