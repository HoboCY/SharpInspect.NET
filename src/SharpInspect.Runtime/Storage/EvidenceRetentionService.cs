using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;

namespace SharpInspect.Runtime.Storage;

internal sealed class EvidenceRetentionService : IEvidenceRetentionService
{
    private readonly ProductionStoreOptions _options;
    private readonly LocalAuthorizationService _authorization;
    private readonly IEvidenceRetentionQuery _history;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    internal EvidenceRetentionService(ProductionStoreOptions options, LocalAuthorizationService authorization,
        IEvidenceRetentionQuery history, Func<ValueTask<StationStateSnapshot>> readStation)
    { _options = options; _authorization = authorization; _history = history; _readStation = readStation; }

    public ValueTask<EvidenceRetentionSnapshot> ReadAsync(EvidenceRetentionFilter filter,
        CancellationToken cancellationToken = default) => _history.ReadAsync(filter, cancellationToken);

    public ValueTask<EvidenceRetentionAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) => _authorization.GetRetentionAccessAsync(invocation, cancellationToken);

    public async ValueTask<EvidenceRetentionResult> ChangeAsync(ChangeEvidenceRetentionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var station = await _readStation().ConfigureAwait(false);
            return await _authorization.AuthorizeRetentionAsync(command, station.RuntimeEpoch,
                new StoreDeadline(_options.CommitTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "RetentionRuntimeUnavailable",
                AuditPersistence.Unavailable, Guid.NewGuid()), null);
        }
    }
}
