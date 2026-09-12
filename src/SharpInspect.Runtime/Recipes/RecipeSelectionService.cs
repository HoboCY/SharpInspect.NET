using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Startup verification of the durable selection projection; a missing revision is the valid default LocalOperatorOnly deployment.</summary>
internal sealed record RecipeSelectionStartupResult(bool Available, string ReasonCode,
    RecipeSelectionRevision? Current = null);

/// <summary>
/// The only local Recipe selection deployment authority. Preparation derives every
/// affected target from one verified snapshot of the fixed concrete query and the
/// identity writer rechecks the release high-water mark, current map, version and
/// Step-Up grant inside its own transaction. Selection performs no I/O, prepares no
/// algorithm, opens no device and never disarms or arms production.
/// </summary>
internal sealed class RecipeSelectionService : IRecipeSelectionService
{
    private readonly RecipeDraftService _drafts;
    private readonly IRecipeSelectionQuery _history;
    private readonly SqliteRecipeSelectionQuery _authority;
    private readonly LocalAuthorizationService _authorization;
    private readonly ProductionStoreOptions _options;
    private readonly Func<CancellationToken, ValueTask<RecipeSelectionRuntimeLease>> _reserveRuntime;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private readonly PlcResultContractBinder _binder = new();
    private int _active;

    internal RecipeSelectionService(RecipeDraftService drafts, IRecipeSelectionQuery history,
        SqliteRecipeSelectionQuery authority, LocalAuthorizationService authorization,
        ProductionStoreOptions options,
        Func<CancellationToken, ValueTask<RecipeSelectionRuntimeLease>> reserveRuntime,
        Func<ValueTask<StationStateSnapshot>> readStation)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RecipeDrafts is null || options.RecipeReleases is null)
            throw new ArgumentException("RecipeSelectionDependenciesUnavailable", nameof(options));
        _drafts = drafts;
        // Ordinary history is a presentation capability only. Authority decisions below
        // always use the concrete query's single same-snapshot authority read.
        _history = history;
        _authority = authority;
        _authorization = authorization;
        _options = options;
        _reserveRuntime = reserveRuntime ?? throw new ArgumentNullException(nameof(reserveRuntime));
        _readStation = readStation ?? throw new ArgumentNullException(nameof(readStation));
    }

    private RecipeContractReference ExecutionPolicy => new(_options.RecipeDrafts!.ExecutionPolicy.Id,
        _options.RecipeDrafts.ExecutionPolicy.Version, _options.RecipeDrafts.ExecutionPolicy.ContentHash);
    private RecipeContractReference GovernancePolicy => new(_options.RecipeReleases!.Policy.Id,
        _options.RecipeReleases.Policy.Version, _options.RecipeReleases.Policy.ContentHash);

    public ValueTask<RecipeSelectionAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) =>
        _authorization.GetRecipeSelectionAccessAsync(invocation, cancellationToken);

    public ValueTask<RecipeSelectionReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        _history.ReadCurrentAsync(cancellationToken);
    public ValueTask<RecipeSelectionReadResult> ReadAsync(RecipeSelectionReference reference,
        CancellationToken cancellationToken = default) => _history.ReadAsync(reference, cancellationToken);
    public ValueTask<RecipeSelectionPage> QueryAsync(RecipeSelectionFilter filter,
        CancellationToken cancellationToken = default) => _history.QueryAsync(filter, cancellationToken);

    /// <summary>
    /// Loads the startup projection from the concrete query after store initialization.
    /// Only this read attests that release, selection and PLC-contract authority were
    /// verified on one database snapshot.
    /// </summary>
    internal async ValueTask<RecipeSelectionStartupResult> ReadStartupAsync(CancellationToken cancellationToken)
    {
        if (_options.RecipeSelections is null)
            return new(true, "RecipeSelectionDefaultLocalOperatorOnly");
        try
        {
            var snapshot = await _authority.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
            return new(true, snapshot.Current is null
                ? "RecipeSelectionDefaultLocalOperatorOnly" : "RecipeSelectionAvailable", snapshot.Current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(exception, "RecipeSelectionStartupUnavailable")); }
    }

    public async ValueTask<RecipeSelectionChangeResult> ChangeAsync(ChangeRecipeSelectionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.CorrelationId == Guid.Empty || command.Invocation is null)
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "InvalidCommandContext",
                AuditPersistence.NotAttempted, Guid.NewGuid()));
        var entered = Interlocked.Increment(ref _active) <= 2;
        if (!entered) Interlocked.Decrement(ref _active);
        RecipeSelectionRuntimeLease? lease = null;
        try
        {
            RecipeSelectionPreparation preparation;
            try
            {
                preparation = RecipeSelectionPreparation.Rejected("RecipeSelectionCapacityExceeded");
                if (entered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // PhysicalConsole, ManageRecipeSelectionMap and the Step-Up grant are
                    // rechecked by the writer; this admission never replaces them.
                    var access = await GetAccessAsync(command.Invocation, cancellationToken).ConfigureAwait(false);
                    if (!access.CanChange) preparation = RecipeSelectionPreparation.Rejected(access.ReasonCode);
                    else
                    {
                        // The reserved configuration lease excludes activation, production
                        // triggers and every other workflow and holds Ready=false/Disarmed.
                        lease = await _reserveRuntime(cancellationToken).ConfigureAwait(false);
                        preparation = lease.GetBlocker() is { } blocker
                            ? RecipeSelectionPreparation.Rejected(blocker)
                            : await PrepareAsync(command, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { preparation = RecipeSelectionPreparation.Rejected("RecipeSelectionChangeCancelled"); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { preparation = RecipeSelectionPreparation.Rejected("RecipeSelectionValidationUnavailable"); }

            // Reading an immutable station snapshot changes nothing; a prepared or
            // rejected decision still audits against the real Runtime epoch.
            var epoch = lease?.RuntimeEpoch ?? (await _readStation().ConfigureAwait(false)).RuntimeEpoch;
            var result = await _authorization.ChangeRecipeSelectionAsync(command, epoch, preparation,
                () => lease is null ? "RecipeSelectionRuntimeLeaseRequired" : lease.GetBlocker(),
                new StoreDeadline(_options.CommitTimeout), cancellationToken).ConfigureAwait(false);
            if (result.Outcome.Disposition != CommandDisposition.Accepted) return result;
            // The durable revision is the only source for the cached projection. The
            // install is fenced by the still-held reservation and must match the exact
            // cached predecessor; a failure keeps the original cache and blocks the
            // station until it is reloaded from the committed ledger.
            if (result.Revision is not { } revision || lease is null || !lease.Publish(revision))
                return new(new(command.CorrelationId, CommandDisposition.Accepted,
                    "RecipeSelectionCacheReloadRequired", AuditPersistence.Persisted, result.Outcome.AttemptId));
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeSelectionChangeUnavailable",
                AuditPersistence.Unavailable, Guid.NewGuid()));
        }
        finally
        {
            // Release retires the reservation and its Ready=false hold; it never re-arms.
            lease?.Dispose();
            if (entered) Interlocked.Decrement(ref _active);
        }
    }

    private async ValueTask<RecipeSelectionPreparation> PrepareAsync(ChangeRecipeSelectionCommand command,
        CancellationToken cancellationToken)
    {
        // One verified same-database snapshot grants all release authority: current
        // revision, release history, release high-water mark and PLC contract revision.
        var snapshot = await _authority.ReadAuthorityAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.PlcContract is not { } plcContract)
            return RecipeSelectionPreparation.Rejected("RecipeSelectionPlcContractMissing");
        if (snapshot.Current?.Reference != command.ExpectedCurrent)
            return RecipeSelectionPreparation.Rejected("RecipeSelectionCurrentConflict");
        var highWatermark = snapshot.Releases.Select(value => value.Position).DefaultIfEmpty(0).Max();
        if (snapshot.ReleaseHighWatermark != highWatermark)
            return RecipeSelectionPreparation.Rejected("RecipeSelectionReleaseSnapshotChanged");
        var releases = new Dictionary<Guid, RecipeReleaseRecord>();
        foreach (var release in snapshot.Releases)
        {
            if (release.Position > highWatermark) continue;
            if (!releases.TryAdd(release.ReleaseId, release))
                return RecipeSelectionPreparation.Rejected("RecipeSelectionReleaseHistoryInvalid");
        }

        // The affected set is the full union of the previous and proposed maps, so a
        // removed entry is validated exactly like an added one and no target survives
        // unproved. Targets resolve by exact release identity only; a name, position or
        // latest alias never resolves.
        var affected = AffectedEntries(snapshot.Current?.Map, command.Map);
        var targets = new List<RecipeReleaseRecord>();
        var seen = new HashSet<Guid>();
        foreach (var entry in affected)
        {
            var release = ResolveAffectedRelease(releases, entry);
            if (release is null)
                return RecipeSelectionPreparation.Rejected(releases.ContainsKey(entry.ReleaseId)
                    ? "RecipeSelectionAffectedReleaseMismatch" : "RecipeSelectionAffectedReleaseMissing");
            if (seen.Add(release.ReleaseId)) targets.Add(release);
        }

        var validations = new List<RecipeSelectionValidatedRelease>();
        foreach (var release in targets.OrderBy(value => value.ReleaseId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = release.Source.Content;
            var validation = await _drafts.ValidateAsync(content, cancellationToken).ConfigureAwait(false);
            if (!validation.Valid) return RecipeSelectionPreparation.Rejected(validation.ReasonCode);
            var algorithm = _drafts.Algorithms.SingleOrDefault(value =>
                value.Identity == content.Algorithm.Algorithm &&
                value.ConfigurationSchema.Id == content.Algorithm.ConfigurationSchema.Id &&
                value.ConfigurationSchema.Version == content.Algorithm.ConfigurationSchema.Version &&
                value.ConfigurationSchema.ContentHash == content.Algorithm.ConfigurationSchema.ContentHash &&
                new RecipeContractReference(value.ResultSchema.Id, value.ResultSchema.Version, value.ResultSchema.ContentHash) ==
                    content.Algorithm.ResultSchema &&
                new RecipeContractReference(value.ResultSchema.OverlayContract.Id, value.ResultSchema.OverlayContract.Version,
                    value.ResultSchema.OverlayContract.ContentHash) == content.Algorithm.OverlayContract);
            if (algorithm is null)
                return RecipeSelectionPreparation.Rejected("RecipeSelectionReleasedAlgorithmUnavailable");
            if (!CurrentPolicyRequirementsSatisfied(content, ExecutionPolicy, GovernancePolicy,
                    _options.RecipeDrafts!.ExecutionPolicy.MaximumExecutionTimeout,
                    _options.RecipeReleases!.EvidenceCapturePolicies))
                return RecipeSelectionPreparation.Rejected("RecipeSelectionCurrentPolicyMismatch");
            // Calibration dependencies stay activatable-deferred here: no historical or
            // latest profile is ever chosen, and the real activation checks remain
            // authoritative. The writer independently rechecks lifecycle availability
            // for every proposed target before committing the map.
            var bound = _binder.Bind(release.Recipe, algorithm.Identity, algorithm.ResultSchema, plcContract.Contract);
            if (!bound.Bound || bound.Binding is null)
                return RecipeSelectionPreparation.Rejected(bound.ReasonCode);
            validations.Add(new RecipeSelectionValidatedRelease(release.Recipe, release.ReleaseId, release.ContentHash,
                ValidationProofHash(content, algorithm, ExecutionPolicy, GovernancePolicy, plcContract.Reference,
                    bound.Binding)));
        }
        return new RecipeSelectionPreparation(highWatermark, plcContract.Reference, validations.AsReadOnly(), null);
    }

    /// <summary>The complete union of previous and proposed map targets, including removed entries.</summary>
    internal static IReadOnlyList<RecipeSelectionMapEntry> AffectedEntries(RecipeSelectionMap? previous,
        RecipeSelectionMap? proposed)
    {
        var affected = new List<RecipeSelectionMapEntry>();
        if (previous is not null) affected.AddRange(previous.Entries);
        if (proposed is not null) affected.AddRange(proposed.Entries);
        return affected;
    }

    /// <summary>Exact immutable release identity only: id, Recipe reference and record content hash.</summary>
    internal static RecipeReleaseRecord? ResolveAffectedRelease(
        IReadOnlyDictionary<Guid, RecipeReleaseRecord> releases, RecipeSelectionMapEntry entry)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(entry);
        return releases.TryGetValue(entry.ReleaseId, out var release) && release.Recipe == entry.Recipe &&
            release.ContentHash == entry.ReleaseRecordContentHash ? release : null;
    }

    /// <summary>Exact current execution and governance policy requirements, including the bound timeout.</summary>
    internal static bool CurrentPolicyRequirementsSatisfied(RecipeDraftContent content,
        RecipeContractReference executionPolicy, RecipeContractReference governancePolicy,
        TimeSpan maximumExecutionTimeout) =>
        CurrentPolicyRequirementsSatisfied(content, executionPolicy, governancePolicy,
            maximumExecutionTimeout, null);

    /// <summary>
    /// Exact current policy requirements including the deployment Evidence Capture catalog.
    /// A declared Evidence Capture requirement resolves by exact identity only; a recipe that
    /// declares no capture policy keeps the same outcome without a catalog.
    /// </summary>
    internal static bool CurrentPolicyRequirementsSatisfied(RecipeDraftContent content,
        RecipeContractReference executionPolicy, RecipeContractReference governancePolicy,
        TimeSpan maximumExecutionTimeout, EvidenceCapturePolicyCatalog? evidenceCapturePolicies)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(executionPolicy);
        ArgumentNullException.ThrowIfNull(governancePolicy);
        if (content.PolicyRequirements.Count(value => value.Kind == RecipePolicyKind.AlgorithmExecution) != 1 ||
            !content.PolicyRequirements.All(value => value.Kind switch
            {
                RecipePolicyKind.AlgorithmExecution => value.Contract == executionPolicy,
                RecipePolicyKind.RecipeGovernance => value.Contract == governancePolicy,
                RecipePolicyKind.EvidenceCapture => evidenceCapturePolicies is not null &&
                    evidenceCapturePolicies.Resolve(value.Contract) is not null,
                _ => false
            }))
            return false;
        return content.AlgorithmExecutionTimeout <= maximumExecutionTimeout;
    }

    /// <summary>
    /// Binds the portable validation of one affected release to the exact algorithm,
    /// configuration, result and overlay references, the current execution and
    /// governance policies, the current PLC contract and its binding proof.
    /// </summary>
    internal static string ValidationProofHash(RecipeDraftContent content, AlgorithmDescriptor algorithm,
        RecipeContractReference executionPolicy, RecipeContractReference governancePolicy,
        RecipeContractReference plcContract, PlcResultContractBinding binding)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(executionPolicy);
        ArgumentNullException.ThrowIfNull(governancePolicy);
        ArgumentNullException.ThrowIfNull(plcContract);
        ArgumentNullException.ThrowIfNull(binding);
        // A declared Evidence Capture reference is already bound through the Recipe content
        // hash, so no deployment catalog identity or additional authority enters this proof.
        return AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-selection-portable-validation-v1", content.ContentHash,
            algorithm.Identity.Id, algorithm.Identity.Version,
            algorithm.ConfigurationSchema.Id, algorithm.ConfigurationSchema.Version,
            algorithm.ConfigurationSchema.ContentHash,
            content.Algorithm.ResultSchema.Id, content.Algorithm.ResultSchema.Version,
            content.Algorithm.ResultSchema.ContentHash,
            content.Algorithm.OverlayContract.Id, content.Algorithm.OverlayContract.Version,
            content.Algorithm.OverlayContract.ContentHash,
            executionPolicy.Id, executionPolicy.Version, executionPolicy.ContentHash,
            governancePolicy.Id, governancePolicy.Version, governancePolicy.ContentHash,
            plcContract.Id, plcContract.Version, plcContract.ContentHash,
            binding.ContentHash
        });
    }
}
