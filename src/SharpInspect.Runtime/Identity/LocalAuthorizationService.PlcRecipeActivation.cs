using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    private async ValueTask<RecipeActivationAdmissionDecision> AdmitPlcRecipeActivationAsync(
        ActivateRecipeCommand command, Guid epoch, Guid attempt, RecipeActivationEvidenceKind kind,
        IReadOnlyList<RecipeActivationCheck> checks, string? preflightFailure, StoreDeadline deadline,
        CancellationToken cancellation, PlcRecipeActivationCapability capability)
    {
        try
        {
            var write = await _store.UpdateRecipeActivationCommandAsync(command, (identity, state, duplicate) =>
            {
                var previous = ActivationCurrent(state.Records, kind);
                if (capability.Check(command, epoch, state) is { } capabilityFailure)
                    return new(new RecipeActivationAdmissionDecision(new(command.CorrelationId, CommandDisposition.Rejected,
                        capabilityFailure, AuditPersistence.NotAttempted, attempt)), Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                var reason = !state.Enabled ? "RecipeActivationConfigurationRequired" :
                    kind != RecipeActivationEvidenceKind.LocalAuthority ? "RecipeChangeLocalAuthorityRequired" :
                    "Authorized";
                var authorized = reason == "Authorized";
                if (authorized && duplicate) reason = "DuplicateCorrelationId";
                if (reason == "Authorized" && cancellation.IsCancellationRequested) reason = "RecipeActivationCancelled";
                if (reason == "Authorized" && state.PendingAdmission is not null) reason = "RecipeActivationRecoveryRequired";
                if (reason == "Authorized" && command.ExpectedActive != previous?.Reference) reason = "RecipeActivationCurrentConflict";
                if (reason == "Authorized") reason = ValidateHistoricalSelection(command, previous) ?? "Authorized";
                if (reason == "Authorized" && !state.Releases.Any(value => value.Recipe == command.Candidate &&
                        value.ReleaseId == command.ReleaseId && value.ContentHash == command.ReleaseRecordContentHash))
                    reason = "RecipeActivationExactReleaseMissing";
                if (reason == "Authorized" && preflightFailure is not null) reason = preflightFailure;
                var accepted = reason == "Authorized";
                var observed = ReplaceActivationCheck(checks, 1, authorized,
                    authorized ? "MappedPlcRecipeActivationAuthorized" : reason);
                var phase = accepted ? RecipeActivationOutcomeState.Admitted :
                    reason == "RecipeActivationCancelled" ? RecipeActivationOutcomeState.Cancelled : RecipeActivationOutcomeState.Failed;
                if (accepted) reason = "RecipeActivationAdmitted";
                var record = CreateActivationRecord(state, command, attempt, kind, accepted ? previous : null, null,
                    phase, reason, observed, new(RecipeActivationRestorationState.NotRequired, "RecipeActivationHardwareUntouched"),
                    null, null, null, null, ActivationAuthorizationPolicy, ActivationTime(identity, state.Records));
                var result = new RecipeActivationAdmissionDecision(new(command.CorrelationId,
                    accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected, reason, AuditPersistence.Persisted, attempt),
                    record, previous);
                return ActivationUpdate(identity, command, epoch, record, result);
            }, CancellationToken.None, deadline).ConfigureAwait(false);
            return write.Committed && write.Result is RecipeActivationAdmissionDecision result ? result :
                new(new(command.CorrelationId, CommandDisposition.Rejected, write.ReasonCode, AuditPersistence.Unavailable, attempt));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeActivationAuditUnavailable", AuditPersistence.Unavailable, attempt)); }
    }

    private async ValueTask<RecipeActivationCommitAttempt> TryCommitPlcRecipeActivationAsync(
        ActivateRecipeCommand command, Guid epoch, RecipeActivationRecord admitted, RecipeActivationSnapshot snapshot,
        IReadOnlyList<RecipeActivationCheck> checks, Func<string?> claimRuntime, StoreDeadline deadline,
        CancellationToken cancellation, PlcRecipeActivationCapability capability)
    {
        try
        {
            var write = await _store.UpdateRecipeActivationCommandAsync(command, (identity, state, _) =>
            {
                IdentityUpdate Refuse(string reason) => new(new RecipeActivationCommitAttempt(false, reason),
                    Array.Empty<IdentityAuditEvent>(), NoMutation: true);
                if (!state.Enabled || state.PendingAdmission?.Reference != admitted.Reference ||
                    !state.Records.Any(value => value.Reference == admitted.Reference) ||
                    admitted.Admission is null || admitted.OperationId != command.OperationId ||
                    admitted.AuthorizationTarget != command.AuthorizationTarget ||
                    admitted.Actor is not { IsPlcAdapter: true, PlcRequestContext: { } context } ||
                    !context.Matches(capability.Context) || admitted.EvidenceKind != RecipeActivationEvidenceKind.LocalAuthority)
                    return Refuse("RecipeActivationAdmissionConflict");
                if (capability.Check(command, epoch, state) is { } capabilityFailure) return Refuse(capabilityFailure);
                if (cancellation.IsCancellationRequested) return Refuse("RecipeActivationCancelled");
                if (ActivationAuthorizationPolicy != admitted.AuthorizationPolicy)
                    return Refuse("RecipeActivationAuthorizationChanged");
                var previous = ActivationCurrent(state.Records, admitted.EvidenceKind);
                if (previous?.Reference != admitted.PreviousActivation || previous?.SuccessfulSnapshot?.ContentHash !=
                    admitted.PreviousSnapshotContentHash) return Refuse("RecipeActivationCurrentConflict");
                if (ValidateHistoricalSelection(command, previous) is { } historicalFailure) return Refuse(historicalFailure);
                var time = ActivationTime(identity, state.Records);
                if (CheckActivationSnapshotHeads(state, admitted, snapshot, time) is { } headFailure) return Refuse(headFailure);
                if (capability.TryClaimCommit(claimRuntime) is { } runtimeFailure) return Refuse(runtimeFailure);
                var record = CreateActivationRecord(state, command, admitted.AttemptId, admitted.EvidenceKind,
                    previous, admitted, RecipeActivationOutcomeState.Succeeded, "RecipeActivated", checks,
                    new(RecipeActivationRestorationState.NotRequired, "RecipeActivationCommitted"), snapshot,
                    null, null, null, ActivationAuthorizationPolicy, time);
                var result = new RecipeActivationResult(new(command.CorrelationId, CommandDisposition.Accepted,
                    record.Outcome.ReasonCode, AuditPersistence.Persisted, record.AttemptId), record);
                return ActivationUpdate(identity, command, epoch, record,
                    new RecipeActivationCommitAttempt(true, record.Outcome.ReasonCode, result),
                    admissionCommand: state.AdmissionCommands?.GetValueOrDefault(admitted.AttemptId));
            }, CancellationToken.None, deadline).ConfigureAwait(false);
            return write.Committed && write.Result is RecipeActivationCommitAttempt result ? result : new(false, write.ReasonCode);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeActivationCommitUnavailable"); }
    }
}
