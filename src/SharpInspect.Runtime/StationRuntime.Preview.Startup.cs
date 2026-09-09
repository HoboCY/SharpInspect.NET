using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    internal Task WaitForPreviewStartupAsync() => _previewStartupTask ?? Task.CompletedTask;

    private async Task InitializePreviewAsync()
    {
        PreviewSessionOwner? owner = null;
        try
        {
            await _storeInitialization.ConfigureAwait(false);
            await WaitForRecipeActivationStartupAsync().ConfigureAwait(false);
            if (_audit is not SqliteCommandStore store) throw new InvalidOperationException("PreviewStoreUnavailable");
            var recovered = await store.ReadPreviewRecoveryStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (!recovered.Available) throw new InvalidOperationException(recovered.ReasonCode);
            if (recovered.Header is null)
            {
                lock (_sync)
                {
                    _previewStartupPending = false;
                    _previewSnapshot = new(_snapshot.RuntimeEpoch, checked(++_previewRevision), null,
                        PreviewSessionPhase.Idle, "PreviewIdle", null, null, null, null,
                        PreviewRestorationState.NotRequired, false, null, null);
                    PublishLocked(_snapshot with { Mode = ExclusiveMode.None, Ready = false,
                        ArmState = ProductionArmState.Disarmed,
                        AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(value =>
                            value != "PreviewStartupRecoveryPending")) });
                }
                return;
            }
            if (recovered.Header.RecoveryRequired || recovered.Header.Phase == PreviewSessionPhase.RecoveryBlocked)
                throw new InvalidOperationException("PreviewRecoveryRequired");
            if (recovered.StartFact is null || recovered.Draft is null)
                throw new InvalidOperationException("PreviewRecoveryEvidenceMissing");
            owner = new(recovered.Header, recovered.StartFact, recovered.Draft, recovered.ActiveBaseline)
            {
                ExitRequested = true, ExitReason = "PreviewInterruptedByRestart",
                LastSavedDraft = recovered.Header.LastSavedDraft
            };
            lock (_sync)
            {
                _previewOwner = owner;
                PublishPreviewLocked(owner, PreviewSessionPhase.Restoring, "PreviewStartupRestoring");
            }
            var restored = await _cameraSetupRuntime.RecoverPreviewAfterRestartAsync(recovered.Header,
                recovered.Draft, recovered.ActiveBaseline, CancellationToken.None).ConfigureAwait(false);
            var restoration = !restored.Succeeded ? PreviewRestorationState.RecoveryBlocked :
                recovered.ActiveBaseline is null ? PreviewRestorationState.NoActiveBaselineClosed :
                    PreviewRestorationState.Restored;
            await FinishPreviewSessionAsync(owner, false, restored.Succeeded
                ? "PreviewInterruptedSessionRestored" : restored.ReasonCode, restoration).ConfigureAwait(false);
            lock (_sync)
            {
                _previewStartupPending = false;
                PublishLocked(_snapshot with { Mode = _previewRecoveryBlocked ? ExclusiveMode.Preview : ExclusiveMode.None,
                    Ready = false, ArmState = ProductionArmState.Disarmed,
                    AdmissionBlockers = new(_snapshot.AdmissionBlockers.Where(value =>
                        value != "PreviewStartupRecoveryPending")) });
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
            {
                _previewStartupPending = false;
                _previewRecoveryBlocked = true;
                _previewSnapshot = new(_snapshot.RuntimeEpoch, checked(++_previewRevision), owner?.Header.SessionId,
                    PreviewSessionPhase.RecoveryBlocked, "PreviewStartupRecoveryRequired",
                    owner is null ? null : PreviewDraftReference.FromRevision(owner.Draft), null, null, null,
                    PreviewRestorationState.RecoveryBlocked, true, owner?.StartFact.CorrelationId,
                    owner?.LastSavedDraft);
                PublishLocked(_snapshot);
            }
            try { await LatchPreviewRecoveryAlarmAsync().ConfigureAwait(false); }
            finally { owner?.Retired.TrySetResult(true); }
        }
    }
}
