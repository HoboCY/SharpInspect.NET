using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Cameras;

internal sealed partial class CameraSetupRuntime
{
    // Called only with a pending header and exact dependencies from the verified ledger.
    // Reuse the same cold close/reopen/readback machinery as recipe activation recovery.
    internal async ValueTask<RecipeActivationCameraRestoreResult> RecoverPreviewAfterRestartAsync(
        PreviewSessionHeader header, RecipeDraftRevision draft, RecipeActivationRecord? baseline,
        CancellationToken cancellationToken)
    {
        var previous = baseline?.SuccessfulSnapshot;
        if (PreviewDraftReference.FromRevision(draft) != header.Draft ||
            draft.Content.CameraRole != header.LogicalCameraRole ||
            header.ActiveActivation != baseline?.Reference ||
            header.ActiveSnapshotContentHash != previous?.ContentHash ||
            header.ActiveCameraContentHash != (previous is null ? null :
                RecipeActivationValidation.CameraHash(previous.CameraSetup)))
            return new(false, "PreviewRecoveryEvidenceMismatch", null, false);
        TaskCompletionSource<bool>? drain = null;
        var entered = false;
        try
        {
            drain = RegisterInFlight();
            entered = await WaitForOperationGateAsync(cancellationToken).ConfigureAwait(false);
            if (!entered) return new(false, "PreviewRecoveryCameraBusy", null, false);
            using var operation = new CancellationTokenSource(PositiveTimeout(_options.OperationTimeout));
            if (await CheckNetworkBarrierAsync(operation.Token).ConfigureAwait(false) is { } barrier)
                return new(false, barrier, null, false);
            var persisted = await LoadPersistedAsync(header.LogicalCameraRole, operation.Token).ConfigureAwait(false);
            if (!persisted.Succeeded || persisted.Pending)
                return new(false, persisted.Pending ? "CameraSetupOperationPending" : persisted.ReasonCode, null, false);
            var current = GetSlot(header.LogicalCameraRole)?.Snapshot;
            if (current?.Binding != header.CurrentBinding)
                return new(false, "PreviewRecoveryBindingConflict", null, false);
            return previous is null
                ? await RecoverWithoutPreviousBaselineAsync(current, draft.Content, operation.Token).ConfigureAwait(false)
                : await RecoverPreviousBaselineAsync(current, previous.CameraSetup, draft.Content, operation.Token)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkActivationRestorationBlocked(header.LogicalCameraRole);
            return new(false, "PreviewRecoveryUnavailable", null, true);
        }
        finally
        {
            if (entered) _operationGate.Release();
            if (drain is not null) CompleteInFlight(drain);
        }
    }
}
