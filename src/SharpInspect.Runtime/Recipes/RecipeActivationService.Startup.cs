using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

internal sealed record RecipeActivationStartupResult(bool Available, string ReasonCode,
    RecipeActivationRecord? Current = null);

internal sealed partial class RecipeActivationService
{
    // Only the owning StationRuntime calls this after base store initialization while
    // its startup configuration fence is held. Ordinary history queries never do I/O
    // against a camera, and there is deliberately no public recovery bypass.
    internal async ValueTask<RecipeActivationStartupResult> RecoverAfterRestartAsync(Guid epoch,
        CameraSetupRuntime camera, CancellationToken cancellationToken)
    {
        RecipeActivationRecord? current = null;
        try
        {
            var page = await _startupHistory.QueryAsync(new(PageSize: 1), cancellationToken).ConfigureAwait(false);
            if (!page.Available) return new(false, page.ReasonCode);
            var selected = await _startupHistory.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (!selected.Available) return new(false, selected.ReasonCode);
            current = selected.Record;
            var pending = page.PendingAdmissions ?? Array.Empty<RecipeActivationRecord>();
            if (pending.Count == 0)
                return selected.RecoveryRequired ? new(false, "RecipeActivationStartupHistoryChanged", current) :
                    new(true, "RecipeActivationStartupHistoryVerified", current);
            if (pending.Count != 1 || selected.PendingAdmission?.Reference != pending[0].Reference)
                return new(false, "RecipeActivationStartupAdmissionConflict", current);
            var admitted = pending[0];
            var expectedKind = _fixture is null ? RecipeActivationEvidenceKind.LocalAuthority :
                RecipeActivationEvidenceKind.InternalContractFixture;
            if (admitted.EvidenceKind != expectedKind || admitted.Admission is null)
                return new(false, "RecipeActivationStartupEvidenceAuthorityMismatch", current);

            var previous = current;
            if (_fixture is not null)
            {
                // Sealed contract tests may recover their own baseline, but it is never
                // returned as the public current production record.
                previous = null;
                if (admitted.PreviousActivation is { } fixtureReference)
                {
                    var exact = await _startupHistory.ReadAsync(fixtureReference, cancellationToken).ConfigureAwait(false);
                    if (!exact.Available || exact.Record is not { Outcome.Succeeded: true } fixturePrevious ||
                        fixturePrevious.EvidenceKind != expectedKind)
                        return new(false, "RecipeActivationStartupPreviousUnavailable", current);
                    previous = fixturePrevious;
                }
            }
            if (previous?.Reference != admitted.PreviousActivation ||
                previous?.SuccessfulSnapshot?.Recipe != admitted.PreviousRecipe ||
                previous?.SuccessfulSnapshot?.ContentHash != admitted.PreviousSnapshotContentHash)
                return new(false, "RecipeActivationStartupPreviousChanged", current);
            var released = await _startupReleases.ReadAsync(admitted.Candidate, cancellationToken).ConfigureAwait(false);
            if (!released.Available || released.Recipe is null ||
                released.Recipe.Record.ReleaseId != admitted.ReleaseId ||
                released.Recipe.Record.ContentHash != admitted.ReleaseRecordContentHash)
                return new(false, "RecipeActivationStartupReleaseUnavailable", current);

            cancellationToken.ThrowIfCancellationRequested();
            var restored = await camera.RecoverRecipeActivationAfterRestartAsync(admitted,
                released.Recipe.Record, previous?.SuccessfulSnapshot, cancellationToken).ConfigureAwait(false);
            // A failed physical recovery leaves the durable admission unfinished. A new
            // process must see the same recovery requirement even if this process dies.
            if (!restored.Succeeded) return new(false, restored.ReasonCode, current);
            var baseline = previous?.SuccessfulSnapshot;
            var restoration = new RecipeActivationRestoration(baseline is null ?
                    RecipeActivationRestorationState.NoPreviousBaselineClosed : RecipeActivationRestorationState.Restored,
                restored.ReasonCode, baseline is null ? null : RecipeActivationValidation.CameraHash(baseline.CameraSetup),
                restored.Snapshot is null ? null : RecipeActivationValidation.CameraHash(restored.Snapshot), restored.Snapshot);
            var intent = admitted.Admission;
            var command = new ActivateRecipeCommand(admitted.OperationId,
                new(CommandSource.PhysicalConsole, intent.ActorPrincipalId.ToString("D"), intent.ActorSessionId),
                intent.Candidate, intent.ReleaseId, intent.ReleaseRecordContentHash, intent.ExpectedActive,
                intent.CalibrationSelections, intent.ChangeReason);
            var terminal = await _authorization.FinalizeRecipeActivationFailureAsync(command, epoch, admitted,
                admitted.Checks, restoration, RecipeActivationOutcomeState.Failed,
                "RecipeActivationInterruptedByRestart", new StoreDeadline(_options.CommitTimeout)).ConfigureAwait(false);
            if (terminal.Outcome.Audit != AuditPersistence.Persisted ||
                terminal.Record is not { Outcome.State: RecipeActivationOutcomeState.Failed } ||
                terminal.Record.AdmissionReference != admitted.Reference)
                return new(false, terminal.Outcome.Audit != AuditPersistence.Persisted ?
                    terminal.Outcome.ReasonCode : "RecipeActivationStartupTerminalUnavailable", current);

            // Cleanup has started, so this verification also outlives caller cancellation.
            var verified = await _startupHistory.ReadCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            var finalPage = await _startupHistory.QueryAsync(new(PageSize: 1), CancellationToken.None).ConfigureAwait(false);
            if (!verified.Available || !finalPage.Available || verified.RecoveryRequired ||
                finalPage.PendingAdmissions is { Count: > 0 } || verified.Record?.Reference != current?.Reference)
                return new(false, "RecipeActivationStartupTerminalNotVerified", current);
            return new(true, "RecipeActivationStartupRecovered", current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, "RecipeActivationStartupCancelled", current); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeActivationStartupUnavailable", current); }
    }
}
