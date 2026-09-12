using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.StoragePolicies;

internal sealed class TraceStoragePolicyService : ITraceStoragePolicyService
{
    private readonly ProductionStoreOptions _options;
    private readonly LocalAuthorizationService _authorization;
    private readonly SqliteCommandStore _store;
    private readonly ITraceStoragePolicyHistoryQuery _history;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly Guid _epoch = Guid.NewGuid();

    internal TraceStoragePolicyService(ProductionStoreOptions options, LocalAuthorizationService authorization,
        SqliteCommandStore store, ITraceStoragePolicyHistoryQuery history)
    { _options = options; _authorization = authorization; _store = store; _history = history; }

    public ValueTask<TraceStoragePolicyAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) => _authorization.GetTraceStoragePolicyAccessAsync(invocation, cancellationToken);

    public ValueTask<TraceStoragePolicyReadResult> ReadAsync(long? version = null, CancellationToken cancellationToken = default) =>
        _history.ReadAsync(version, cancellationToken);

    public ValueTask<TraceStoragePolicyHistoryPage> QueryAsync(TraceStoragePolicyFilter filter,
        CancellationToken cancellationToken = default) => _history.QueryAsync(filter, cancellationToken);

    public async ValueTask<TraceStoragePolicyResult> PublishAsync(PublishTraceStoragePolicyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_slots.Wait(0)) return Failure(command, "TraceStoragePolicyRequestCapacityExceeded");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_options.CommitTimeout);
            var deadline = new StoreDeadline(_options.CommitTimeout);
            var access = await GetAccessAsync(command.Invocation, budget.Token).ConfigureAwait(false);
            string? rejection = access.Allowed ? null : access.ReasonCode;
            if (rejection is null)
            {
                var observation = TraceStoragePreflightEvaluator.Observe(_options);
                rejection = TraceStoragePolicyValidator.Validate(command.Policy,
                    _options.TraceStoragePolicies?.DeploymentScope, observation.TotalBytes).FirstOrDefault();
            }
            budget.Token.ThrowIfCancellationRequested();
            return await _authorization.ExecuteTraceStoragePolicyAsync(command, rejection, _epoch, deadline, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(command, "TraceStoragePolicyDeadlineExceeded"); }
        finally { _slots.Release(); }
    }

    public async ValueTask<TraceStoragePreflightReport> GetPreflightAsync(CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var volume = TraceStoragePreflightEvaluator.Observe(_options);
        var outboxBacklog = _options.Outbox is { } outbox ? Outbox.ProductionOutboxBinding.CompleteBacklog(outbox,
            await new SqliteProductionOutboxQuery(_options).ReadBacklogAsync(cancellationToken).ConfigureAwait(false)) : null;
        return TraceStoragePreflightEvaluator.Evaluate(current, _options.TraceStoragePolicies?.DeploymentScope,
            volume, _store.VerifiedProfile, _authorization.TraceStoragePolicyUtcNow, outboxBacklog);
    }

    private static TraceStoragePolicyResult Failure(PublishTraceStoragePolicyCommand command, string reason) =>
        new(new(command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, Guid.NewGuid()));
}
