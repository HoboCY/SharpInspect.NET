using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Explicit, bounded migration orchestration. Only the complete validated output reaches the draft writer.</summary>
internal sealed class AlgorithmConfigurationMigrationService : IAlgorithmConfigurationMigrationService, IAsyncDisposable
{
    private readonly RecipeDraftService _drafts;
    private readonly LocalAuthorizationService _authorization;
    private readonly AlgorithmConfigurationMigrationRegistry _registry;
    private readonly TimeSpan _timeout;
    private readonly IRecipeLifecycleHistoryQuery? _lifecycle;
    private readonly bool _lifecycleRequired;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private readonly SemaphoreSlim _calls = new(16, 16);
    private readonly object _reservationSync = new();
    private readonly Dictionary<Guid, MigrationReservation> _reservations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    internal AlgorithmConfigurationMigrationService(RecipeDraftService drafts,
        LocalAuthorizationService authorization, AlgorithmConfigurationMigrationRegistry registry,
        ProductionStoreOptions options, IRecipeLifecycleHistoryQuery? lifecycle = null)
    {
        _drafts = drafts; _authorization = authorization; _registry = registry;
        _lifecycle = lifecycle; _lifecycleRequired = options.RecipeLifecycle is not null;
        _timeout = options.CommitTimeout >= TimeSpan.FromMilliseconds(1) && options.CommitTimeout <= TimeSpan.FromMinutes(5)
            ? options.CommitTimeout : TimeSpan.FromSeconds(2);
    }

    public IReadOnlyList<AlgorithmConfigurationMigrationDescriptor> Migrations => _registry.Migrations;
    public ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default)
        => _drafts.GetAccessAsync(invocation, cancellationToken);

    public async ValueTask<RecipeDraftMigrationResult> MigrateAsync(RecipeDraftMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Plan is null || request.Invocation is null) return Failure("RecipeDraftMigrationRequestRequired");
        if (cancellationToken.IsCancellationRequested) return Failure("RecipeDraftMigrationCancelled");
        if (Volatile.Read(ref _disposed) != 0) return Failure("RecipeDraftMigrationServiceDisposed");
        if (!_calls.Wait(0)) return Failure("RecipeDraftMigrationCapacityExceeded");
        MigrationReservation? reservation = null;
        var ownsReservation = false;
        try
        {
            lock (_reservationSync)
            {
                if (_reservations.TryGetValue(request.Plan.OperationId, out reservation))
                {
                    if (reservation.PlanHash != request.Plan.ContentHash)
                        return Failure("RecipeDraftMigrationOperationConflict");
                }
                else
                {
                    reservation = new(request.Plan.ContentHash);
                    _reservations.Add(request.Plan.OperationId, reservation);
                    ownsReservation = true;
                }
            }
            if (!ownsReservation)
            {
                var first = await reservation!.Completion.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
                // Do not share another caller's authorization. A committed result is reread and
                // replay-authorized for this caller's current identity and original signed grant.
                if (first.Created) return await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                var access = await GetAccessAsync(request.Invocation, cancellationToken).AsTask()
                    .WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
                return access.CanSave ? Failure(first.ReasonCode) : Failure(access.ReasonCode);
            }
            var result = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            reservation!.Completion.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException) { return Failure("RecipeDraftMigrationCancelled"); }
        catch (TimeoutException) { return Failure("RecipeDraftMigrationDeadlineExceeded"); }
        finally
        {
            if (ownsReservation)
            {
                reservation!.Completion.TrySetResult(Failure("RecipeDraftMigrationUnavailable"));
                lock (_reservationSync) _reservations.Remove(request.Plan.OperationId);
            }
            _calls.Release();
        }
    }

    private async ValueTask<RecipeDraftMigrationResult> ExecuteAsync(RecipeDraftMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request?.Plan is null || request.Invocation is null) return Failure("RecipeDraftMigrationRequestRequired");
        if (cancellationToken.IsCancellationRequested) return Failure("RecipeDraftMigrationCancelled");
        if (Volatile.Read(ref _disposed) != 0) return Failure("RecipeDraftMigrationServiceDisposed");
        if (!_slots.Wait(0)) return Failure("RecipeDraftMigrationCapacityExceeded");
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        Task? physical = null;
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return Failure("RecipeDraftMigrationServiceDisposed");
            var deadline = new StoreDeadline(_timeout);
            var plan = request.Plan;
            var accessTask = GetAccessAsync(request.Invocation, budget.Token).AsTask();
            physical = accessTask;
            var access = await accessTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!access.CanSave) return Failure(access.ReasonCode);
            if (access.RequiresStepUp && (request.StepUpGrantId is not { } grant ||
                request.Invocation.StepUpGrantId != grant)) return Failure("StepUpRequired");

            // An exact retry returns the committed output. It must not rerun a transformer,
            // require the old plugin to remain installed, or replace a nondeterministic output.
            var replayTask = _drafts.ReadAsync(plan.TargetDraftId, 1, budget.Token).AsTask();
            physical = replayTask;
            var replay = await replayTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (replay.Available && replay.Revision is { } committed)
            {
                if (committed.Content.MigrationLineage?.Plan.ContentHash != plan.ContentHash ||
                    committed.OperationId != plan.OperationId)
                    return Failure("RecipeDraftMigrationTargetExists");
                return await CommitAsync(committed.Content).ConfigureAwait(false);
            }
            if (replay.ReasonCode != "RecipeDraftRevisionNotFound") return Failure(replay.ReasonCode);

            var sourceTask = _drafts.ReadAsync(plan.Source.DraftId, plan.Source.Revision, budget.Token).AsTask();
            physical = sourceTask;
            var read = await sourceTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!read.Available || read.Revision is not { } source) return Failure(read.ReasonCode);
            if (source.DraftId != plan.Source.DraftId || source.Revision != plan.Source.Revision ||
                source.RevisionContentHash != plan.Source.RevisionContentHash)
                return Failure("RecipeDraftMigrationSourceMismatch");
            var sourceIssues = source.Content.Configuration.Validate(source.Content.Algorithm.ConfigurationSchema);
            if (sourceIssues.Count != 0) return Failure("RecipeDraftMigrationSourceInvalid", sourceIssues);

            var target = _drafts.Algorithms.SingleOrDefault(item => item.Identity.Id == plan.TargetAlgorithm.Id &&
                item.Identity.Version == plan.TargetAlgorithm.Version);
            if (target is null) return Failure("RecipeDraftMigrationTargetNotRegistered");
            if (target.ConfigurationSchema.Id != plan.TargetSchema.Id ||
                target.ConfigurationSchema.Version != plan.TargetSchema.Version ||
                target.ConfigurationSchema.ContentHash != plan.TargetSchema.ContentHash)
                return Failure("RecipeDraftMigrationTargetSchemaMismatch");
            var descriptor = Migrations.SingleOrDefault(item => item.Migrator == plan.Migrator &&
                Same(item.SourceAlgorithm, source.Content.Algorithm.Algorithm) &&
                item.SourceSchema.Id == source.Content.Algorithm.ConfigurationSchema.Id &&
                item.SourceSchema.Version == source.Content.Algorithm.ConfigurationSchema.Version &&
                item.SourceSchema.ContentHash == source.Content.Algorithm.ConfigurationSchema.ContentHash &&
                Same(item.TargetAlgorithm, plan.TargetAlgorithm) && item.TargetSchema == plan.TargetSchema);
            if (descriptor is null) return Failure("RecipeDraftMigrationMigratorUnavailable");
            var context = new AlgorithmConfigurationMigrationContext(plan.Source, source.Content.Algorithm,
                source.Content.Configuration, target);
            var transformTask = _registry.TransformAsync(plan, context, budget.Token).AsTask();
            physical = transformTask;
            var transformed = await transformTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!transformed.Succeeded) return Failure(transformed.ReasonCode, transformed.Issues);
            budget.Token.ThrowIfCancellationRequested();

            AlgorithmConfigurationSnapshot configuration;
            try { configuration = AlgorithmConfigurationSnapshot.Create(target.ConfigurationSchema, transformed.Values); }
            catch (ArgumentException)
            { return Failure("RecipeDraftMigrationTargetInvalid", new[] { new AlgorithmValidationIssue("RecipeDraftMigrationDraftCorrectionRequired") }); }
            var lineage = new RecipeDraftMigrationLineage(plan, descriptor, source.Content.Configuration.ContentHash,
                configuration.ContentHash, transformed.Issues);
            var old = source.Content;
            var lifecycleLineage = await RecipeLifecycleSources.ResolveForMigrationAsync(_lifecycle, source,
                _lifecycleRequired, budget.Token).ConfigureAwait(false);
            var content = new RecipeDraftContent(lifecycleLineage, lineage, old.RecipeKey, old.DisplayName,
                RecipeAlgorithmBinding.FromDescriptor(target), configuration, old.CameraRole, old.Camera,
                old.AlgorithmExecutionTimeout, old.AssetRequirements, old.PolicyRequirements,
                configuration.Values.Select(value => new RecipeDraftFieldOrigin(value.Key, RecipeDraftValueOrigin.Explicit)),
                old.CameraProviderExtension, old.CalibrationRequirements, old.PartIdentityRequirement);
            var validationTask = _drafts.ValidateAsync(content, budget.Token).AsTask();
            physical = validationTask;
            var validation = await validationTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!validation.Valid) return Failure(validation.ReasonCode, validation.Issues);
            return await CommitAsync(content).ConfigureAwait(false);

            async ValueTask<RecipeDraftMigrationResult> CommitAsync(RecipeDraftContent candidate)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (deadline.Expired) return Failure("RecipeDraftMigrationDeadlineExceeded");
                if (!RecipeDraftStorageCodec.TryEncodeContent(candidate, out var document, out var reason) || document is null)
                    return Failure(reason);
                var save = new RecipeDraftSaveRequest(plan.OperationId, plan.TargetDraftId, 0, null, candidate,
                    plan.ChangeReason, request.Invocation, request.StepUpGrantId);
                var commit = _authorization.SaveRecipeDraftMigrationAsync(save, document, plan, deadline, budget.Token).AsTask();
                physical = commit;
                // Once admitted to the writer, its result is the commit truth even if the UI cancels.
                var result = await commit.ConfigureAwait(false);
                return new(result.Saved, result.Saved ? "RecipeDraftMigrationPersisted" : result.ReasonCode,
                    result.Revision, result.Saved ? candidate.MigrationLineage!.Warnings : result.Issues);
            }
        }
        catch (OperationCanceledException) { return Failure("RecipeDraftMigrationCancelled"); }
        catch (TimeoutException) { return Failure("RecipeDraftMigrationDeadlineExceeded"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Failure("RecipeDraftMigrationUnavailable"); }
        finally
        {
            if (physical is { IsCompleted: false } pending)
            {
                // A timed-out provider continues to own admission until it really exits.
                var cancellation = Task.Run(() => { try { budget.Cancel(); } catch (Exception) { } });
                _ = Task.WhenAll(pending, cancellation).ContinueWith(task =>
                { _ = task.Exception; budget.Dispose(); _slots.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else { budget.Dispose(); _slots.Release(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _ = Task.Run(() => { try { _lifetime.Cancel(); } catch (Exception) { } });
        return ValueTask.CompletedTask;
    }

    private static bool Same(AlgorithmIdentity left, AlgorithmIdentity right)
        => left.Id == right.Id && left.Version == right.Version;
    private static RecipeDraftMigrationResult Failure(string reason, IReadOnlyList<AlgorithmValidationIssue>? issues = null)
        => new(false, reason, null, issues ?? Array.Empty<AlgorithmValidationIssue>());

    private sealed class MigrationReservation
    {
        internal MigrationReservation(string planHash) { PlanHash = planHash; }
        internal string PlanHash { get; }
        internal TaskCompletionSource<RecipeDraftMigrationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
