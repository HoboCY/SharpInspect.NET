using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed record RecipeActivationAdmissionDecision(RuntimeCommandOutcome Outcome,
    RecipeActivationRecord? Record = null, RecipeActivationRecord? PreviousActive = null);
internal sealed record RecipeActivationCommitAttempt(bool Committed, string ReasonCode,
    RecipeActivationResult? Result = null);

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<RecipeActivationCommitAttempt> TryCommitRecipeActivationAsync(
        ActivateRecipeCommand command, Guid epoch, RecipeActivationRecord admitted, RecipeActivationSnapshot snapshot,
        IReadOnlyList<RecipeActivationCheck> checks, Func<string?> claimRuntimeCommit,
        StoreDeadline deadline, CancellationToken callerCancellation)
    {
        try
        {
            var write = await _store.UpdateRecipeActivationCommandAsync(command, (identity, state, _) =>
            {
                IdentityUpdate Refuse(string reason) => new(new RecipeActivationCommitAttempt(false, reason),
                    Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                SessionAuthorizationLease? lease = null;
                StepUpGrant? grant = null;
                var transferred = false;
                try
                {
                    var existing = state.Records.LastOrDefault(value => value.AttemptId == admitted.AttemptId && value.IsTerminal);
                    if (existing is not null)
                    {
                        var replay = new RecipeActivationResult(new(command.CorrelationId,
                            existing.Outcome.Succeeded ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                            existing.Outcome.ReasonCode, AuditPersistence.Persisted, existing.AttemptId), existing);
                        return new(new RecipeActivationCommitAttempt(existing.Outcome.Succeeded,
                            existing.Outcome.ReasonCode, replay), Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                    }
                    if (!state.Enabled || state.PendingAdmission?.Reference != admitted.Reference ||
                        !state.Records.Any(value => value.Reference == admitted.Reference) ||
                        admitted.Admission is null || admitted.OperationId != command.OperationId ||
                        admitted.AuthorizationTarget != command.AuthorizationTarget)
                        return Refuse("RecipeActivationAdmissionConflict");
                    var previous = ActivationCurrent(state.Records, admitted.EvidenceKind);
                    if (previous?.Reference != admitted.PreviousActivation || previous?.SuccessfulSnapshot?.ContentHash !=
                        admitted.PreviousSnapshotContentHash) return Refuse("RecipeActivationCurrentConflict");
                    if (!Equals(admitted.HistoricalSelection, command.HistoricalSelection))
                        return Refuse("RecipeActivationHistoricalSelectionChanged");
                    if (ValidateHistoricalSelection(command, previous) is { } selectionFailure)
                        return Refuse(selectionFailure);
                    if (callerCancellation.IsCancellationRequested) return Refuse("RecipeActivationCancelled");
                    if (!TryLease(command.Invocation, out lease, out var reason)) return Refuse(reason);
                    var actor = Find(identity, lease!.Identity.PrincipalId);
                    if (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.ActivateRecipe))
                        return Refuse("PermissionDenied");
                    if (command.HistoricalSelection is not null &&
                        !actor.Permissions.Contains(Permission.SelectHistoricalCalibration))
                        return Refuse("PermissionDenied");
                    if (actor.PrincipalId != admitted.ActorPrincipalId || lease.SessionId != admitted.ActorSessionId ||
                        actor.AuthorizationRevision != admitted.ActorAuthorizationRevision ||
                        ActivationAuthorizationPolicy != admitted.AuthorizationPolicy)
                        return Refuse("RecipeActivationAuthorizationChanged");
                    reason = CheckGrant(command, actor, lease.SessionId, true, out grant);
                    if (reason != "Authorized") return Refuse(reason);
                    // Read the decision clock after authorization work. Dependency heads
                    // are the writer's transaction snapshot, including calibration policy
                    // and physical verification; strict expiry applies at this decision.
                    var time = ActivationTime(identity, state.Records);
                    if (CheckActivationSnapshotHeads(state, admitted, snapshot, time) is { } headFailure)
                        return Refuse(headFailure);
                    // This is the final bounded Runtime decision inside the SQLite transaction.
                    // Local Stop and this claim have a defined order; no provider I/O follows.
                    if (claimRuntimeCommit() is { } runtimeFailure) return Refuse(runtimeFailure);
                    var record = CreateActivationRecord(state, command, admitted.AttemptId, admitted.EvidenceKind,
                        previous, admitted, RecipeActivationOutcomeState.Succeeded, "RecipeActivated", checks,
                        new(RecipeActivationRestorationState.NotRequired, "RecipeActivationCommitted"), snapshot,
                        actor.PrincipalId, lease.SessionId, actor.AuthorizationRevision, ActivationAuthorizationPolicy, time);
                    var result = new RecipeActivationResult(new(command.CorrelationId, CommandDisposition.Accepted,
                        record.Outcome.ReasonCode, AuditPersistence.Persisted, record.AttemptId), record);
                    var guard = new AuthorizationCommitGuard(lease, () =>
                    { lock (_grantSync) if (grant is not null) grant.State = GrantState.Consumed; }, () =>
                    { lock (_grantSync) if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Active; });
                    transferred = true;
                    return ActivationUpdate(identity, command, epoch, record,
                        new RecipeActivationCommitAttempt(true, record.Outcome.ReasonCode, result), guard,
                        state.AdmissionCommands?.GetValueOrDefault(admitted.AttemptId));
                }
                finally
                {
                    if (!transferred)
                    {
                        lock (_grantSync) if (grant is { State: GrantState.Reserved }) grant.State = GrantState.Active;
                        lease?.Dispose();
                    }
                }
            }, CancellationToken.None, deadline).ConfigureAwait(false);
            // EnqueueVerifiedWorkAsync waits for the definitive writer completion after queue
            // admission; a false result here cannot conceal a still-running transaction.
            return write.Committed && write.Result is RecipeActivationCommitAttempt result ? result :
                new(false, write.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeActivationCommitUnavailable"); }
    }

    internal async ValueTask<RecipeActivationResult> FinalizeRecipeActivationFailureAsync(
        ActivateRecipeCommand command, Guid epoch, RecipeActivationRecord admitted,
        IReadOnlyList<RecipeActivationCheck> checks, RecipeActivationRestoration restoration,
        RecipeActivationOutcomeState outcome, string reason, StoreDeadline deadline)
    {
        if (outcome is not (RecipeActivationOutcomeState.Failed or RecipeActivationOutcomeState.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        try
        {
            var write = await _store.UpdateRecipeActivationCommandAsync(command, (identity, state, _) =>
            {
                var existing = state.Records.LastOrDefault(value => value.AttemptId == admitted.AttemptId && value.IsTerminal);
                if (existing is not null)
                    return new(new RecipeActivationResult(new(command.CorrelationId,
                        existing.Outcome.Succeeded ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                        existing.Outcome.ReasonCode, AuditPersistence.Persisted, existing.AttemptId), existing),
                        Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                if (state.PendingAdmission?.Reference != admitted.Reference || admitted.Admission is null ||
                    admitted.AuthorizationTarget != command.AuthorizationTarget || admitted.OperationId != command.OperationId)
                    return new(new RecipeActivationResult(new(command.CorrelationId, CommandDisposition.Rejected,
                        "RecipeActivationAdmissionConflict", AuditPersistence.NotAttempted, admitted.AttemptId)),
                        Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                // A terminal failure describes the admitted actor's operation. It must survive
                // logout/revocation, and grants neither a new permission nor an Active selection.
                var record = CreateActivationRecord(state, command, admitted.AttemptId, admitted.EvidenceKind,
                    null, admitted, outcome, reason, checks, restoration, null,
                    admitted.ActorPrincipalId, admitted.ActorSessionId, admitted.ActorAuthorizationRevision,
                    admitted.AuthorizationPolicy, ActivationTime(identity, state.Records));
                var result = new RecipeActivationResult(new(command.CorrelationId, CommandDisposition.Rejected,
                    reason, AuditPersistence.Persisted, admitted.AttemptId), record);
                return ActivationUpdate(identity, command, epoch, record, result,
                    admissionCommand: state.AdmissionCommands?.GetValueOrDefault(admitted.AttemptId));
            }, CancellationToken.None, deadline).ConfigureAwait(false);
            return write.Committed && write.Result is RecipeActivationResult result ? result :
                new(new(command.CorrelationId, CommandDisposition.Rejected, write.ReasonCode,
                    AuditPersistence.Unavailable, admitted.AttemptId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeActivationTerminalUnavailable",
            AuditPersistence.Unavailable, admitted.AttemptId)); }
    }

    internal async ValueTask<RecipeActivationAdmissionDecision> AdmitRecipeActivationAsync(
        ActivateRecipeCommand command, Guid epoch, Guid attemptId, RecipeActivationEvidenceKind evidenceKind,
        IReadOnlyList<RecipeActivationCheck> checks, string? preflightFailure, Func<string?> runtimeBlocker,
        StoreDeadline deadline, CancellationToken callerCancellation)
    {
        try
        {
            var write = await _store.UpdateRecipeActivationCommandAsync(command, (identity, state, duplicate) =>
            {
                SessionAuthorizationLease? lease = null;
                LocalAdministratorState? actor = null;
                var transferred = false;
                try
                {
                    var reason = state.Enabled ? "Authorized" : "RecipeActivationConfigurationRequired";
                    if (reason == "Authorized" && command.Invocation.Source != CommandSource.PhysicalConsole) reason = "LocalConsoleRequired";
                    if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason)) reason = leaseReason;
                    if (lease is not null) actor = Find(identity, lease.Identity.PrincipalId);
                    if (reason == "Authorized" && (actor is not { Enabled: true } ||
                        !actor.Permissions.Contains(Permission.ActivateRecipe))) reason = "PermissionDenied";
                    if (reason == "Authorized" && command.HistoricalSelection is not null &&
                        !actor!.Permissions.Contains(Permission.SelectHistoricalCalibration)) reason = "PermissionDenied";
                    if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, false, out _);
                    var authorized = reason == "Authorized";
                    var observed = ReplaceActivationCheck(checks, 1, authorized, authorized ? "RecipeActivationAuthorized" : reason);
                    var previous = ActivationCurrent(state.Records, evidenceKind);
                    if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
                    if (reason == "Authorized" && callerCancellation.IsCancellationRequested) reason = "RecipeActivationCancelled";
                    if (reason == "Authorized" && epoch == Guid.Empty) reason = "RecipeActivationRuntimeUnavailable";
                    if (reason == "Authorized" && state.PendingAdmission is not null) reason = "RecipeActivationRecoveryRequired";
                    if (reason == "Authorized" && command.ExpectedActive != previous?.Reference) reason = "RecipeActivationCurrentConflict";
                    if (reason == "Authorized")
                        reason = ValidateHistoricalSelection(command, previous) ?? "Authorized";
                    if (reason == "Authorized" && !state.Releases.Any(value => value.Recipe == command.Candidate &&
                        value.ReleaseId == command.ReleaseId && value.ContentHash == command.ReleaseRecordContentHash))
                        reason = "RecipeActivationExactReleaseMissing";
                    if (reason == "Authorized" && preflightFailure is not null)
                    {
                        if (RecipeActivationRuntimeLease.IsRuntimeBusy(preflightFailure))
                        {
                            // Runtime lock contention is not an activation outcome. Roll back
                            // this identity transaction so the caller can retry outside it.
                            var retry = new RecipeActivationAdmissionDecision(
                                new(command.CorrelationId, CommandDisposition.Rejected,
                                    preflightFailure, AuditPersistence.NotAttempted, attemptId),
                                null, previous);
                            return new(retry, Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                        }
                        reason = preflightFailure;
                    }
                    if (reason == "Authorized")
                    {
                        var blocker = runtimeBlocker();
                        if (RecipeActivationRuntimeLease.IsRuntimeBusy(blocker))
                        {
                            // ReadBlocker is deliberately non-blocking while the writer holds
                            // a session lease. Never turn that transient observation into a
                            // durable failed admission.
                            var retry = new RecipeActivationAdmissionDecision(
                                new(command.CorrelationId, CommandDisposition.Rejected,
                                    blocker!, AuditPersistence.NotAttempted, attemptId),
                                null, previous);
                            return new(retry, Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                        }
                        reason = blocker ?? "Authorized";
                    }
                    var admitted = reason == "Authorized";
                    var phase = admitted ? RecipeActivationOutcomeState.Admitted :
                        reason == "RecipeActivationCancelled" ? RecipeActivationOutcomeState.Cancelled : RecipeActivationOutcomeState.Failed;
                    if (admitted) reason = "RecipeActivationAdmitted";
                    var time = ActivationTime(identity, state.Records);
                    // A rejected request never acquired a restoration baseline.
                    // The durable successful head remains selected independently.
                    var record = CreateActivationRecord(state, command, attemptId, evidenceKind, admitted ? previous : null, null,
                        phase, reason, observed, new(RecipeActivationRestorationState.NotRequired,
                            "RecipeActivationHardwareUntouched"), null,
                        actor is not null && lease is not null ? actor.PrincipalId : null,
                        actor is not null && lease is not null ? lease.SessionId : null,
                        actor is not null && lease is not null ? actor.AuthorizationRevision : null,
                        actor is not null && lease is not null ? ActivationAuthorizationPolicy : null, time);
                    var outcome = new RuntimeCommandOutcome(command.CorrelationId, admitted ? CommandDisposition.Accepted :
                        CommandDisposition.Rejected, reason, AuditPersistence.Persisted, attemptId);
                    var result = new RecipeActivationAdmissionDecision(outcome, record, previous);
                    IIdentityTransactionGuard? guard = null;
                    if (admitted)
                    {
                        // A fresh Step-Up, if required by deployment, is checked again and consumed
                        // only at final success. Its exact command binding prevents reuse elsewhere.
                        guard = new AuthorizationCommitGuard(lease!, () => { }, () => { });
                        transferred = true;
                    }
                    return ActivationUpdate(identity, command, epoch, record, result, guard);
                }
                finally { if (!transferred) lease?.Dispose(); }
            }, CancellationToken.None, deadline).ConfigureAwait(false);
            return write.Committed && write.Result is RecipeActivationAdmissionDecision result ? result :
                new(new(command.CorrelationId, CommandDisposition.Rejected, write.ReasonCode,
                    AuditPersistence.Unavailable, attemptId));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeActivationAuditUnavailable",
                AuditPersistence.Unavailable, attemptId));
        }
    }

    internal ValueTask<RecipeActivationAccess> GetRecipeActivationAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken) => GetRecipeActivationAccessAsync(invocation, false, cancellationToken);

    internal ValueTask<RecipeActivationAccess> GetRecipeActivationAccessAsync(CommandInvocation invocation,
        bool historicalSelection, CancellationToken cancellationToken) => QueryAsync(async token =>
        {
            if (_store.RecipeActivationOptions is null)
                return new RecipeActivationAccess(false, "RecipeActivationConfigurationRequired");
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                var allowed = actor is { Enabled: true } && actor.Permissions.Contains(Permission.ActivateRecipe) &&
                    (!historicalSelection || actor.Permissions.Contains(Permission.SelectHistoricalCalibration));
                var requiredPermission = historicalSelection ? Permission.SelectHistoricalCalibration :
                    Permission.ActivateRecipe;
                return new(allowed, allowed ? "RecipeActivationAccessAvailable" : "PermissionDenied",
                    new(_options.AuthorizationPolicy.Id, _options.AuthorizationPolicy.Version,
                        _options.AuthorizationPolicy.ContentHash),
                    _options.AuthorizationPolicy.RequiresStepUp(requiredPermission));
            }
        }, reason => new(false, reason), cancellationToken);

    private RecipeContractReference ActivationAuthorizationPolicy => new(_options.AuthorizationPolicy.Id,
        _options.AuthorizationPolicy.Version, _options.AuthorizationPolicy.ContentHash);

    private string? CheckActivationSnapshotHeads(RecipeActivationCommandState state, RecipeActivationRecord admitted,
        RecipeActivationSnapshot snapshot, DateTimeOffset atUtc)
    {
        var release = state.Releases.SingleOrDefault(value => value.Recipe == admitted.Candidate &&
            value.ReleaseId == admitted.ReleaseId && value.ContentHash == admitted.ReleaseRecordContentHash);
        if (release is null || snapshot.Release.ContentHash != release.ContentHash ||
            snapshot.EvidenceKind != admitted.EvidenceKind) return "RecipeActivationReleaseChanged";
        var content = release.Source.Content;
        if (content.PartIdentityRequirement?.Mode != PartIdentityRequirementMode.None ||
            content.AssetRequirements.Count != 0 || content.CameraProviderExtension is not null)
            return "RecipeActivationDependenciesUnavailable";
        if (snapshot.AlgorithmExecutionPolicy.ContentHash != _store.RecipeDraftOptions!.ExecutionPolicy.ContentHash)
            return "RecipeActivationExecutionPolicyChanged";
        var currentContract = state.PlcResultContracts.LastOrDefault();
        if (currentContract is null || currentContract.Contract.ContentHash != snapshot.PlcResultContract.Contract.ContentHash ||
            snapshot.PlcResultContract.Recipe != admitted.Candidate || snapshot.PlcResultContract.Algorithm != content.Algorithm.Algorithm)
            return "RecipeActivationPlcContractChanged";
        if (state.Cameras is null || !state.Cameras.TryGetValue(content.CameraRole, out var camera) || camera.HasPending ||
            camera.Binding != snapshot.CameraSetup.Binding || snapshot.CameraSetup.Requested != content.Camera ||
            !Cameras.CameraSetupRuntime.IsActivationConfiguredHealth(snapshot.CameraSetup.Health))
            return "RecipeActivationCameraBindingChanged";
        var imaging = state.ImagingSetups?.GetValueOrDefault(content.CameraRole);
        var calibration = RecipeActivationCalibrationEvaluator.EvaluateRecords(content,
            admitted.Admission!.CalibrationSelections, snapshot.CameraSetup, imaging?.Current,
            state.CalibrationGovernanceRecords ?? Array.Empty<object>(), atUtc, state.CalibrationGovernancePosition,
            state.CalibrationGovernanceContentHash);
        if (!calibration.Allowed) return calibration.ReasonCode;
        if (!calibration.Bindings.Select(value => value.ContentHash).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(snapshot.CalibrationBindings.Select(value => value.ContentHash).OrderBy(value => value, StringComparer.Ordinal)))
            return "RecipeActivationCalibrationChanged";
        return null;
    }

    private DateTimeOffset ActivationTime(IdentityAuthorityState state, IReadOnlyList<RecipeActivationRecord> records)
    {
        var time = _utcNow().ToUniversalTime();
        if (time < state.LastObservedUtc) time = state.LastObservedUtc;
        if (records.LastOrDefault() is { } last && time < last.RecordedAtUtc) time = last.RecordedAtUtc;
        return time;
    }

    private static RecipeActivationRecord? ActivationCurrent(IReadOnlyList<RecipeActivationRecord> records,
        RecipeActivationEvidenceKind kind) => records.LastOrDefault(value => value.Outcome.Succeeded && value.EvidenceKind == kind);

    private static string? ValidateHistoricalSelection(ActivateRecipeCommand command,
        RecipeActivationRecord? previous)
        => ValidateHistoricalCalibrationBindings(command, previous?.SuccessfulSnapshot?.CalibrationBindings);

    internal static string? ValidateHistoricalCalibrationBindings(ActivateRecipeCommand command,
        IReadOnlyList<CalibrationRunProfileBinding>? previousBindings)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.HistoricalSelection is null)
        {
            // Once an active recipe has a calibration binding, changing that
            // selection must use the separately audited historical-selection route.
            if (previousBindings is not null &&
                !SelectionsMatchBindings(command.CalibrationSelections, previousBindings))
                return "HistoricalCalibrationSelectionRequired";
            return null;
        }

        if (previousBindings is null)
            return "HistoricalCalibrationPreviousProfileRequired";
        var previousExactProfile = command.HistoricalSelection.PreviousExactProfile;
        if (previousExactProfile is null)
            return "HistoricalCalibrationPreviousProfileRequired";
        var previousProfiles = previousBindings;
        if (previousProfiles.Count == 0 || !previousProfiles.Any(binding =>
                ProfileEquals(binding.Profile, previousExactProfile)))
            return "HistoricalCalibrationPreviousProfileConflict";
        if (command.CalibrationSelections.Count == 0)
            return "HistoricalCalibrationSelectionRequired";
        // This intent carries one prior profile. Preserve all other requirement
        // bindings so it cannot describe a different or additional replacement.
        var changed = command.CalibrationSelections.Where(selection =>
            !previousProfiles.Any(binding => binding.RequirementContentHash == selection.RequirementContentHash &&
                ProfileEquals(binding.Profile, selection.Profile))).ToArray();
        if (changed.Length != 1 || command.CalibrationSelections.Count != previousProfiles.Count ||
            previousProfiles.Any(binding => !command.CalibrationSelections.Any(selection =>
                selection.RequirementContentHash == binding.RequirementContentHash)))
            return "HistoricalCalibrationSingleReplacementRequired";
        var replaced = previousProfiles.Single(binding =>
            binding.RequirementContentHash == changed[0].RequirementContentHash);
        if (!ProfileEquals(replaced.Profile, previousExactProfile))
            return "HistoricalCalibrationPreviousProfileConflict";
        return null;
    }

    private static bool SelectionsMatchBindings(
        IReadOnlyList<CalibrationProfileSelection> selections,
        IReadOnlyList<CalibrationRunProfileBinding> bindings)
    {
        if (selections.Count != bindings.Count) return false;
        var selected = selections.Select(value => (value.RequirementContentHash, value.Profile))
            .OrderBy(value => value.RequirementContentHash, StringComparer.Ordinal).ToArray();
        var current = bindings.Select(value => (value.RequirementContentHash, value.Profile))
            .OrderBy(value => value.RequirementContentHash, StringComparer.Ordinal).ToArray();
        return selected.Zip(current).All(pair => pair.First.RequirementContentHash == pair.Second.RequirementContentHash &&
            ProfileEquals(pair.First.Profile, pair.Second.Profile));
    }

    private static bool ProfileEquals(CalibrationProfileReference left, CalibrationProfileReference right) =>
        left.ProfileId == right.ProfileId && left.Version == right.Version &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static IReadOnlyList<RecipeActivationCheck> ReplaceActivationCheck(IReadOnlyList<RecipeActivationCheck> checks,
        int number, bool passed, string reason)
    {
        var id = "V132.A" + number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        var subject = checks.FirstOrDefault(value => value.CheckId == id)?.Subject ?? (number == 1 ? "Authorization" : "Quiescence");
        return checks.Where(value => value.CheckId != id).Append(new RecipeActivationCheck(id, subject,
            passed ? RecipeActivationCheckStatus.Passed : RecipeActivationCheckStatus.Failed, reason)).ToArray();
    }

    private static RecipeActivationRecord CreateActivationRecord(RecipeActivationCommandState state,
        ActivateRecipeCommand command, Guid attemptId, RecipeActivationEvidenceKind evidenceKind,
        RecipeActivationRecord? previous, RecipeActivationRecord? admission, RecipeActivationOutcomeState outcome,
        string reason, IReadOnlyList<RecipeActivationCheck> checks, RecipeActivationRestoration restoration,
        RecipeActivationSnapshot? snapshot, Guid? actor, Guid? session, long? authorizationRevision,
        RecipeContractReference? policy, DateTimeOffset time)
    {
        var position = state.Records.Count + 1L;
        var id = Guid.NewGuid();
        var previousReference = admission?.PreviousActivation ?? previous?.Reference;
        var previousRecipe = admission?.PreviousRecipe ?? previous?.SuccessfulSnapshot?.Recipe;
        var previousHash = admission?.PreviousSnapshotContentHash ?? previous?.SuccessfulSnapshot?.ContentHash;
        var intent = outcome == RecipeActivationOutcomeState.Admitted ? new RecipeActivationAdmission(position,
            id, attemptId, command.OperationId, command.Candidate, command.ReleaseId, command.ReleaseRecordContentHash,
            command.ExpectedActive, previousReference, previousRecipe, previousHash, command.CalibrationSelections,
            command.ChangeReason, actor!.Value, session!.Value, authorizationRevision!.Value, policy!,
            command.AuthorizationTarget, evidenceKind, time, command.HistoricalSelection) : null;
        return new(position, id, attemptId, command.OperationId, admission?.Reference, previousReference,
            previousRecipe, previousHash, command.Candidate, command.ReleaseId, command.ReleaseRecordContentHash,
            snapshot?.Recipe, new(outcome, reason), checks, restoration, snapshot, evidenceKind, actor, session,
            authorizationRevision, policy, command.ChangeReason, command.AuthorizationTarget, time, intent,
            command.HistoricalSelection);
    }

    private IdentityUpdate ActivationUpdate(IdentityAuthorityState identity, ActivateRecipeCommand command,
        Guid epoch, RecipeActivationRecord record, object result, IIdentityTransactionGuard? guard = null,
        CommandAuditFact? admissionCommand = null)
    {
        var phase = record.Outcome.State == RecipeActivationOutcomeState.Admitted || record.AdmissionReference is null ?
            CommandAuditPhase.Outcome : record.Outcome.Succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed;
        var commandKind = command.HistoricalSelection is null ? AuditedCommandKind.ActivateRecipe :
            AuditedCommandKind.SelectHistoricalCalibration;
        var fact = new CommandAuditFact(Guid.NewGuid(), record.AttemptId, command.CorrelationId, epoch, record.RecordedAtUtc,
            commandKind, Enum.IsDefined(typeof(CommandSource), command.Invocation.Source) ? command.Invocation.Source : null,
            command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
            command.Invocation.SessionId, command.Invocation.StepUpGrantId, phase,
            phase == CommandAuditPhase.Outcome ? record.Outcome.State == RecipeActivationOutcomeState.Admitted ?
                CommandDisposition.Accepted : CommandDisposition.Rejected : null,
            record.Outcome.ReasonCode, record.ActorPrincipalId?.ToString("D"));
        if (phase != CommandAuditPhase.Outcome)
        {
            if (admissionCommand is null || admissionCommand.AttemptId != record.AttemptId ||
                admissionCommand.CorrelationId != command.CorrelationId ||
                admissionCommand.CommandKind != commandKind ||
                admissionCommand.Phase != CommandAuditPhase.Outcome ||
                admissionCommand.Disposition != CommandDisposition.Accepted ||
                admissionCommand.AuthenticatedHumanPrincipalId != record.ActorPrincipalId?.ToString("D"))
                throw new InvalidOperationException("RecipeActivationAdmissionCommandMissing");
            // A restart completes the original admitted attempt, including its
            // epoch and claimed invocation. It does not invent a new command or grant.
            fact = admissionCommand with { EventId = fact.EventId, OccurredAtUtc = record.RecordedAtUtc,
                Phase = phase, Disposition = null, ReasonCode = record.Outcome.ReasonCode };
        }
        var kind = record.Outcome.State switch
        {
            RecipeActivationOutcomeState.Admitted => IdentityEventKind.RecipeActivationAdmitted,
            RecipeActivationOutcomeState.Succeeded => IdentityEventKind.RecipeActivationCompleted,
            RecipeActivationOutcomeState.Cancelled => IdentityEventKind.RecipeActivationCancelled,
            _ => IdentityEventKind.RecipeActivationFailed
        };
        var audit = AuthorizationEvent(identity, kind, record.Outcome.ReasonCode, Binding(command),
            record.ActorPrincipalId, record.ActorSessionId, fact.ClaimedStepUpGrantId, command.CorrelationId,
            record.ActorAuthorizationRevision ?? 0, targetPrincipalId: record.ActorPrincipalId,
            capturedTime: record.RecordedAtUtc) with { OperationId = command.OperationId };
        return new(result, new[] { audit }, new[] { fact }, guard, RecipeActivation: new(record));
    }
}
