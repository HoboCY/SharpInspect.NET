using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IRecipeLifecycleService? _recipeLifecycleService;

    internal void ConfigureRecipeLifecycleService(IRecipeLifecycleService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_recipeLifecycleService is not null) throw new InvalidOperationException("RecipeLifecycleAlreadyConfigured");
            _recipeLifecycleService = service;
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitRecipeLifecycleAsync(RuntimeCommand command,
        CancellationToken token)
    {
        IRecipeLifecycleService? service;
        lock (_sync)
        {
            if (_shutdownRequested || _disposed)
                return new(command.CorrelationId, CommandDisposition.Rejected, "RuntimeStopped",
                    AuditPersistence.Unavailable, Guid.NewGuid());
            service = _recipeLifecycleService;
        }
        if (service is null) return new(command.CorrelationId, CommandDisposition.Rejected,
            "RecipeLifecycleConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid());
        return (command is AbandonRecipeDraftCommand abandon
            ? await service.AbandonAsync(abandon, token).ConfigureAwait(false)
            : await service.RetireAsync((RetireReleasedRecipeCommand)command, token).ConfigureAwait(false)).Outcome;
    }
}
