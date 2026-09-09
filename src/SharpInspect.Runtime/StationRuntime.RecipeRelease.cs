using SharpInspect.Abstractions;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private IRecipeReleaseService? _recipeReleaseService;

    internal void ConfigureRecipeReleaseService(IRecipeReleaseService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        lock (_sync)
        {
            if (_recipeReleaseService is not null) throw new InvalidOperationException("RecipeReleaseAlreadyConfigured");
            _recipeReleaseService = service;
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitRecipeReleaseAsync(ReleaseRecipeCommand command,
        CancellationToken cancellationToken)
    {
        IRecipeReleaseService? service;
        lock (_sync)
        {
            if (_shutdownRequested || _disposed)
                return new(command.CorrelationId, CommandDisposition.Rejected, "RuntimeStopped", AuditPersistence.Unavailable, Guid.NewGuid());
            service = _recipeReleaseService;
        }
        return service is null ? new(command.CorrelationId, CommandDisposition.Rejected,
            "RecipeReleaseConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid()) :
            (await service.ReleaseAsync(command, cancellationToken).ConfigureAwait(false)).Outcome;
    }
}
