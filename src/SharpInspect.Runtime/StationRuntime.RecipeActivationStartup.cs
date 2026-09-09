using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private bool _activationStartupPending;
    private bool _activationStartupBlocked;
    private Task? _recipeActivationStartupTask;

    private void ConfigureRecipeActivationStartup(bool configured)
    {
        if (!configured) return;
        _activationStartupPending = true;
        _snapshot = _snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
            AdmissionBlockers = new AdmissionBlockers(_snapshot.AdmissionBlockers
                .Append("RecipeActivationStartupRecoveryPending")) };
    }

    internal Task WaitForRecipeActivationStartupAsync() =>
        _recipeActivationStartupTask ?? Task.CompletedTask;

    private async Task InitializeRecipeActivationAsync(IRecipeActivationService service)
    {
        RecipeActivationStartupResult result;
        try
        {
            // This task is not part of _storeInitialization and never reserves the live
            // activation gate. Both choices prevent a startup task waiting on itself.
            await _storeInitialization.ConfigureAwait(false);
            Guid epoch;
            lock (_sync)
            {
                epoch = _snapshot.RuntimeEpoch;
                if (!_baseStoreReady || _auditFault || _shutdownRequested || _disposed)
                    throw new InvalidOperationException("RecipeActivationStartupStoreUnavailable");
                PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
                    Recovery = RecoveryState.InProgress });
            }
            result = service is RecipeActivationService authority ?
                await authority.RecoverAfterRestartAsync(epoch, _cameraSetupRuntime, _lifetime.Token).ConfigureAwait(false) :
                new(false, "RecipeActivationStartupAuthorityUnavailable");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { result = new(false, "RecipeActivationStartupUnavailable"); }

        // Provider calls have finished before taking the command gate. Stop retains its
        // independent lane throughout recovery and no startup outcome can arm production.
        if (!await _commandGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
        {
            lock (_sync) CompleteRecipeActivationStartupLocked(new(false, "RecipeActivationStartupRuntimeBusy", result.Current));
            return;
        }
        try { lock (_sync) CompleteRecipeActivationStartupLocked(result); }
        finally { _commandGate.Release(); }
    }

    private void CompleteRecipeActivationStartupLocked(RecipeActivationStartupResult result)
    {
        _activationStartupPending = false;
        _activationStartupBlocked = !result.Available;
        _storeReady = _baseStoreReady && !_activationStartupBlocked;
        if (_disposed) return;
        var current = result.Current is { CanBeActive: true } ? result.Current : null;
        var blockers = _snapshot.AdmissionBlockers.Where(code =>
            code is not "RecipeActivationStartupRecoveryPending" and not "RecipeActivationStartupRecoveryRequired").ToList();
        if (!result.Available)
        {
            blockers.Add("RecipeActivationStartupRecoveryRequired");
            blockers.Add(result.ReasonCode);
        }
        if (current is not null)
        {
            blockers.Remove("ActiveRecipeMissing");
            // A persisted instance ID is historical evidence, never a live prepared object.
            blockers.Add("ActiveRecipePreparationRequired");
        }
        PublishLocked(_snapshot with { ActiveRecipe = current?.ResultingRecipe,
            Ready = false, ArmState = ProductionArmState.Disarmed, Recovery = RecoveryState.Required,
            AdmissionBlockers = new AdmissionBlockers(blockers.Distinct(StringComparer.Ordinal)) });
    }
}
