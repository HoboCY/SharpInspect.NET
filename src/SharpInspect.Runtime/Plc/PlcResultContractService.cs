using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Plc;

internal sealed record PlcResultContractPreparation(long? ReleaseHighWatermark,
    IReadOnlyList<PlcResultSchemaValidation> SchemaValidations,
    IReadOnlyList<PlcReleasedRecipeBinding> Bindings, string? Failure);

internal sealed class PlcResultContractRuntimeLease : IDisposable
{
    private Action? _release;
    private readonly Func<string?> _blocker;
    internal PlcResultContractRuntimeLease(Guid epoch, Func<string?> blocker, Action? release = null)
    { RuntimeEpoch = epoch; _blocker = blocker; _release = release; }
    internal Guid RuntimeEpoch { get; }
    internal string? GetBlocker() => _blocker();
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>The only deployment mutation authority; preparation never opens a device or changes a Recipe.</summary>
internal sealed class PlcResultContractService : IPlcResultContractService
{
    private readonly RecipeDraftService _drafts;
    private readonly IReleasedRecipeQuery _releases;
    private readonly IPlcResultContractQuery _history;
    private readonly LocalAuthorizationService _authorization;
    private readonly ProductionStoreOptions _options;
    private readonly Func<CancellationToken, ValueTask<PlcResultContractRuntimeLease>> _enterRuntime;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private readonly PlcResultContractBinder _binder = new();
    private int _active;

    internal PlcResultContractService(RecipeDraftService drafts, IReleasedRecipeQuery releases,
        IPlcResultContractQuery history, LocalAuthorizationService authorization, ProductionStoreOptions options,
        Func<CancellationToken, ValueTask<PlcResultContractRuntimeLease>> enterRuntime,
        Func<ValueTask<StationStateSnapshot>> readStation)
    {
        _drafts = drafts; _releases = releases; _history = history; _authorization = authorization;
        _options = options; _enterRuntime = enterRuntime; _readStation = readStation;
    }

    public ValueTask<PlcResultContractAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) => _authorization.GetPlcResultContractAccessAsync(invocation, cancellationToken);
    public ValueTask<PlcResultContractReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        _history.ReadCurrentAsync(cancellationToken);
    public ValueTask<PlcResultContractReadResult> ReadAsync(RecipeContractReference reference,
        CancellationToken cancellationToken = default) => _history.ReadAsync(reference, cancellationToken);
    public ValueTask<PlcResultContractPage> QueryAsync(PlcResultContractFilter filter,
        CancellationToken cancellationToken = default) => _history.QueryAsync(filter, cancellationToken);

    public async ValueTask<PlcResultContractChangeResult> ChangeAsync(ChangePlcResultContractCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null)
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "InvalidCommandContext",
                AuditPersistence.NotAttempted, Guid.NewGuid()));
        var entered = Interlocked.Increment(ref _active) <= 2;
        if (!entered) Interlocked.Decrement(ref _active);
        PlcResultContractRuntimeLease? lease = null;
        try
        {
            PlcResultContractPreparation preparation;
            try
            {
                preparation = Failure("PlcResultContractCapacityExceeded");
                if (entered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var access = await GetAccessAsync(command.Invocation, cancellationToken).ConfigureAwait(false);
                    if (!access.CanChange) preparation = Failure(access.ReasonCode);
                    else
                    {
                        lease = await _enterRuntime(cancellationToken).ConfigureAwait(false);
                        preparation = lease.GetBlocker() is { } blocker ? Failure(blocker) :
                            await PrepareAsync(command, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { preparation = Failure("PlcResultContractChangeCancelled"); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { preparation = Failure("PlcResultContractValidationUnavailable"); }

            var epoch = lease?.RuntimeEpoch ?? (await _readStation().ConfigureAwait(false)).RuntimeEpoch;
            // The writer audits all decisions and rechecks identity, Step-Up, current revision,
            // the complete release high-water mark and Runtime state within the decision transaction.
            return await _authorization.ChangePlcResultContractAsync(command, epoch, preparation,
                () => lease is null ? "PlcResultContractRuntimeLeaseRequired" : lease.GetBlocker(),
                new StoreDeadline(_options.CommitTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "PlcResultContractChangeUnavailable",
                AuditPersistence.Unavailable, Guid.NewGuid()));
        }
        finally { lease?.Dispose(); if (entered) Interlocked.Decrement(ref _active); }
    }

    private async ValueTask<PlcResultContractPreparation> PrepareAsync(ChangePlcResultContractCommand command,
        CancellationToken cancellationToken)
    {
        var current = await _history.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Available) return Failure(current.ReasonCode);
        if (current.Revision?.Reference != command.ExpectedCurrent) return Failure("PlcResultContractCurrentConflict");
        var schemas = new List<PlcResultSchemaValidation>();
        foreach (var map in command.Proposal.SchemaMaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = _drafts.Algorithms.Select(value => value.ResultSchema)
                .Where(value => Reference(value) == map.ResultSchema).ToArray();
            if (matches.Length == 0) return Failure("PlcResultContractSchemaUnavailable");
            var validation = _binder.ValidateSchema(command.Proposal, matches[0]);
            if (!validation.Valid || validation.Validation is null) return Failure(validation.ReasonCode);
            schemas.Add(validation.Validation);
        }
        if (schemas.Count == 0) return Failure("PlcResultContractSchemaMapRequired");
        var expectedReasons = schemas.SelectMany(value => value.Schema.ReasonCodes)
            .Concat(PlcResultContract.FrameworkReasonCodes).Append(null).ToHashSet(StringComparer.Ordinal);
        var reasonField = command.Proposal.FrameworkFields.Single(value => value.Field == PlcFrameworkResultField.ResultReasonCode);
        if (!expectedReasons.SetEquals(reasonField.ReasonCodes.Select(value => value.ReasonCode)))
            return Failure("PlcResultContractGlobalReasonCatalogMismatch");

        var affected = command.Proposal.SchemaMaps.Select(value => value.ResultSchema)
            .Concat(current.Revision?.Contract.SchemaMaps.Select(value => value.ResultSchema) ??
                Array.Empty<RecipeContractReference>()).ToHashSet();
        var bindings = new List<PlcReleasedRecipeBinding>();
        long? through = null;
        long after = 0;
        var seen = new HashSet<Guid>();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _releases.QueryAsync(new(null, after, through, 200), cancellationToken).ConfigureAwait(false);
            if (!page.Available) return Failure(page.ReasonCode);
            if (through is not null && page.ThroughPosition != through.Value)
                return Failure("PlcResultContractReleaseSnapshotChanged");
            through ??= page.ThroughPosition;
            foreach (var release in page.Recipes)
            {
                if (!seen.Add(release.Record.ReleaseId) || seen.Count > 10000)
                    return Failure("PlcResultContractReleaseHistoryInvalid");
                if (!release.Available || !affected.Contains(release.Content.Algorithm.ResultSchema)) continue;
                var descriptor = _drafts.Algorithms.SingleOrDefault(value =>
                    value.Identity == release.Content.Algorithm.Algorithm &&
                    Reference(value.ResultSchema) == release.Content.Algorithm.ResultSchema);
                if (descriptor is null) return Failure("PlcResultContractReleasedAlgorithmUnavailable");
                var binding = _binder.Bind(release.Reference, descriptor.Identity, descriptor.ResultSchema, command.Proposal);
                if (!binding.Bound || binding.Binding is null) return Failure(binding.ReasonCode);
                bindings.Add(new(release.Record.ReleaseId, release.Record.ContentHash, binding.Binding));
            }
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after || next > through.Value) return Failure("PlcResultContractReleasePaginationInvalid");
            after = next;
        } while (true);
        return new(through, schemas.AsReadOnly(), bindings.AsReadOnly(), null);
    }

    private static RecipeContractReference Reference(AlgorithmResultSchema schema) => new(schema.Id, schema.Version, schema.ContentHash);
    private static PlcResultContractPreparation Failure(string reason) => new(null,
        Array.Empty<PlcResultSchemaValidation>(), Array.Empty<PlcReleasedRecipeBinding>(), reason);
}
