using System.Collections.ObjectModel;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// Bounded authoring boundary for Recipe Drafts.  It validates an immutable
/// candidate against the currently registered descriptor and its semantic
/// validator, but never creates, warms, publishes, activates, or executes an
/// algorithm instance.
/// </summary>
internal sealed class RecipeDraftService : IRecipeDraftEditor, IAsyncDisposable
{
    private const int MaximumFactories = 64;
    private const int MaximumOutstandingValidations = 16;
    private const int MaximumConcurrentSemanticValidations = 2;
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private readonly Dictionary<(string Id, string Version), Registration> _registrations = new();
    private readonly ProductionStoreOptions _options;
    private readonly RecipeDraftStoreOptions _draftOptions;
    private readonly LocalAuthorizationService _authorization;
    private readonly IRecipeDraftHistoryQuery _history;
    private readonly SemaphoreSlim _semanticSlots =
        new(MaximumConcurrentSemanticValidations, MaximumConcurrentSemanticValidations);
    private readonly SemaphoreSlim _saveSlots = new(4, 4);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _running = new();
    private readonly ReadOnlyCollection<AlgorithmDescriptor> _algorithms;
    private TaskCompletionSource<bool> _drained = CreateCompletedDrain();
    private Task? _lifetimeCancellation;
    private int _outstanding;
    private int _activeSaves;
    private bool _disposed;
    private bool _resourcesDisposed;

    internal RecipeDraftService(IEnumerable<IVisionAlgorithmFactory> factories,
        ProductionStoreOptions options, LocalAuthorizationService authorization,
        IRecipeDraftHistoryQuery history)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(history);
        if (options.RecipeDrafts is null)
            throw new ArgumentException("RecipeDraftConfigurationRequired", nameof(options));

        _options = options;
        _draftOptions = options.RecipeDrafts;
        _authorization = authorization;
        _history = history;

        var descriptors = new List<AlgorithmDescriptor>();
        foreach (var factory in factories)
        {
            if (factory is null || descriptors.Count == MaximumFactories)
                throw new ArgumentException("RecipeDraftAlgorithmRegistrationInvalid", nameof(factories));
            var descriptor = factory.Descriptor ??
                throw new ArgumentException("RecipeDraftAlgorithmDescriptorRequired", nameof(factories));
            var identity = descriptor.Identity ??
                throw new ArgumentException("RecipeDraftAlgorithmDescriptorInvalid", nameof(factories));
            if (!_registrations.TryAdd((identity.Id, identity.Version), new Registration(factory, descriptor)))
                throw new ArgumentException("RecipeDraftAlgorithmIdentityAlreadyRegistered", nameof(factories));
            descriptors.Add(descriptor);
        }

        _algorithms = new ReadOnlyCollection<AlgorithmDescriptor>(descriptors
            .OrderBy(item => item.Identity.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Version, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<AlgorithmDescriptor> Algorithms => _algorithms;

    public IReadOnlyList<AlgorithmConfigurationEntry> GetAuthoringDefaults(AlgorithmIdentity algorithm)
    {
        if (algorithm is null || !_registrations.TryGetValue((algorithm.Id, algorithm.Version), out var registration))
            return Array.Empty<AlgorithmConfigurationEntry>();

        return new ReadOnlyCollection<AlgorithmConfigurationEntry>(registration.Descriptor.ConfigurationSchema.Fields
            .Where(field => field.AuthoringDefault is not null)
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => new AlgorithmConfigurationEntry(field.Key, field.Unit, field.AuthoringDefault!))
            .ToArray());
    }

    public async ValueTask<RecipeDraftAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _authorization.GetRecipeDraftAccessAsync(invocation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeDraftAuthorizationUnavailable", _draftOptions.RequireStepUp); }
    }

    public async ValueTask<RecipeDraftValidationResult> ValidateAsync(RecipeDraftContent content,
        CancellationToken cancellationToken = default)
    {
        if (content is null)
            return Failure("RecipeDraftContentRequired");
        if (cancellationToken.IsCancellationRequested)
            return Failure("RecipeDraftValidationCancelled");

        CancellationBudget? operation = null;
        lock (_sync)
        {
            if (_disposed) return Failure("RecipeDraftServiceDisposed");
            if (_outstanding >= MaximumOutstandingValidations)
                return Failure("RecipeDraftValidationCapacityExceeded");
            BeginActivityLocked();
            _outstanding++;
            try
            {
                // Read the lifetime token while the admission gate is held.  Dispose
                // cannot release the lifetime CTS until this reservation is drained.
                operation = new CancellationBudget(cancellationToken, _lifetime.Token, ValidationTimeout);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _outstanding--;
                SignalDrainedLocked();
                return Failure("RecipeDraftValidationUnavailable");
            }
            catch
            {
                _outstanding--;
                SignalDrainedLocked();
                throw;
            }
        }
        var handedToPhysicalValidation = false;
        try
        {
            var basic = ValidateStructure(content, out var registration);
            if (basic is not null) return basic;
            if (cancellationToken.IsCancellationRequested)
                return Failure("RecipeDraftValidationCancelled");

            var semanticOperation = operation!;
            var validationTimeout = ValidationTimeout;
            Task<RecipeDraftValidationResult> work;
            lock (_sync)
            {
                if (_disposed)
                    return Failure("RecipeDraftServiceDisposed");
                work = Task.Run(() => ValidateSemanticAsync(registration!, content, semanticOperation), CancellationToken.None);
                _running.Add(work);
                handedToPhysicalValidation = true;
            }
            _ = work.ContinueWith(static (task, state) => ((RecipeDraftService)state!).OnCompleted(task),
                this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                return await work.WaitAsync(validationTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                operation.RequestCancellation();
                Observe(work);
                return Failure("RecipeDraftSemanticValidationTimedOut");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                operation.RequestCancellation();
                Observe(work);
                return Failure("RecipeDraftValidationCancelled");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                operation.RequestCancellation();
                Observe(work);
                return Failure("RecipeDraftSemanticValidationFailed");
            }
        }
        finally
        {
            if (!handedToPhysicalValidation)
            {
                operation?.Dispose();
                CompleteValidationReservation();
            }
        }
    }

    public async ValueTask<RecipeDraftSaveResult> SaveAsync(RecipeDraftSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            return SaveFailure("RecipeDraftSaveRequestRequired");
        if (cancellationToken.IsCancellationRequested)
            return SaveFailure("RecipeDraftSaveCancelled");

        CancellationBudget budget;
        lock (_sync)
        {
            if (_disposed) return SaveFailure("RecipeDraftServiceDisposed");
            if (!_saveSlots.Wait(0)) return SaveFailure("RecipeDraftSaveCapacityExceeded");
            BeginActivityLocked();
            _activeSaves++;
            try
            {
                // Budget construction is part of the same admission transaction as
                // the save slot.  Dispose cannot tear down _lifetime in this window.
                budget = new CancellationBudget(cancellationToken, _lifetime.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _saveSlots.Release();
                _activeSaves--;
                SignalDrainedLocked();
                return SaveFailure("RecipeDraftSaveUnavailable");
            }
            catch
            {
                _saveSlots.Release();
                _activeSaves--;
                SignalDrainedLocked();
                throw;
            }
        }
        Task? physicalOperation = null;
        try
        {
            var deadline = new StoreDeadline(CommitTimeout);
            var accessTask = GetAccessAsync(request.Invocation, budget.Token).AsTask();
            physicalOperation = accessTask;
            var access = await accessTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!access.CanSave) return SaveFailure(access.ReasonCode);
            if (access.RequiresStepUp &&
                (request.StepUpGrantId is not { } grant || request.Invocation?.StepUpGrantId != grant))
                return SaveFailure("StepUpRequired");

            var validationTask = ValidateAsync(request.Content, budget.Token).AsTask();
            physicalOperation = validationTask;
            var validation = await validationTask.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false);
            if (!validation.Valid)
                return new(false, validation.ReasonCode, null, validation.Issues);
            budget.Token.ThrowIfCancellationRequested();
            if (deadline.Expired) return SaveFailure("RecipeDraftSaveDeadlineExceeded");

            if (!RecipeDraftStorageCodec.TryEncodeContent(request.Content, out var document, out var encodeReason) ||
                document is null)
                return SaveFailure(encodeReason.Length == 0 ? "RecipeDraftContentInvalid" : encodeReason);
            budget.Token.ThrowIfCancellationRequested();
            if (deadline.Expired) return SaveFailure("RecipeDraftSaveDeadlineExceeded");

            var saveTask = _authorization.SaveRecipeDraftAsync(request, document, deadline, budget.Token).AsTask();
            physicalOperation = saveTask;
            // The store has already admitted this request to its writer queue. From
            // that point on its completion is the commit truth: a caller cancel must
            // not turn a committed revision into a visible SaveCancelled result.
            return await saveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return SaveFailure("RecipeDraftSaveCancelled"); }
        catch (TimeoutException)
        {
            budget.RequestCancellation();
            return SaveFailure("RecipeDraftSaveDeadlineExceeded");
        }
        catch (OperationCanceledException)
        {
            budget.RequestCancellation();
            return SaveFailure("RecipeDraftSaveDeadlineExceeded");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return SaveFailure("RecipeDraftSaveUnavailable"); }
        finally
        {
            if (physicalOperation is { IsCompleted: false } pending)
                _ = pending.ContinueWith(task =>
                {
                    _ = task.Exception;
                    FinishSave(budget);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else FinishSave(budget);
        }
    }

    public async ValueTask<RecipeDraftReadResult> ReadAsync(Guid draftId, long? revision = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _history.ReadAsync(draftId, revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeDraftQueryUnavailable", null); }
    }

    public async ValueTask<RecipeDraftPage> QueryAsync(RecipeDraftFilter filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _history.QueryAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, "RecipeDraftQueryUnavailable", Array.Empty<RecipeDraftRevision>(), 0, null); }
    }

    public async ValueTask DisposeAsync()
    {
        Task lifecycle;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCancellation = Task.Run(() => TryCancel(_lifetime));
            lifecycle = Task.WhenAll(_lifetimeCancellation, _drained.Task);
            _ = lifecycle.ContinueWith(static (_, state) =>
                ((RecipeDraftService)state!).FinalizeResources(), this, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        try { await lifecycle.WaitAsync(ShutdownWait).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { }
        FinalizeResources();
    }

    private RecipeDraftValidationResult? ValidateStructure(RecipeDraftContent content,
        out Registration? registration)
    {
        registration = null;
        try
        {
            if (content.Algorithm is null || content.Algorithm.Algorithm is null)
                return Failure("RecipeDraftAlgorithmBindingInvalid");
            if (!_registrations.TryGetValue((content.Algorithm.Algorithm.Id, content.Algorithm.Algorithm.Version),
                    out registration))
                return Failure("RecipeDraftAlgorithmNotRegistered");

            var descriptor = registration.Descriptor;
            if (!SameIdentity(content.Algorithm.Algorithm, descriptor.Identity))
                return Failure("RecipeDraftAlgorithmBindingMismatch");
            if (!SameSchema(content.Algorithm.ConfigurationSchema, descriptor.ConfigurationSchema))
                return Failure("RecipeDraftConfigurationSchemaMismatch");
            if (!SameContract(content.Algorithm.ResultSchema, descriptor.ResultSchema.Id,
                    descriptor.ResultSchema.Version, descriptor.ResultSchema.ContentHash))
                return Failure("RecipeDraftResultSchemaBindingMismatch");
            if (!SameContract(content.Algorithm.OverlayContract, descriptor.ResultSchema.OverlayContract.Id,
                    descriptor.ResultSchema.OverlayContract.Version, descriptor.ResultSchema.OverlayContract.ContentHash))
                return Failure("RecipeDraftOverlayContractBindingMismatch");

            var configurationIssues = content.Configuration.Validate(descriptor.ConfigurationSchema);
            if (configurationIssues.Count != 0)
                return new(false, "RecipeDraftConfigurationInvalid", configurationIssues);

            var policyMatches = content.PolicyRequirements
                .Where(item => item.Kind == RecipePolicyKind.AlgorithmExecution).ToArray();
            if (policyMatches.Length != 1)
                return Failure("RecipeDraftExecutionPolicyRequired");
            var policy = _draftOptions.ExecutionPolicy;
            var policyReference = policyMatches[0].Contract;
            if (!SameContract(policyReference, policy.Id, policy.Version, policy.ContentHash))
                return Failure("RecipeDraftExecutionPolicyMismatch");
            var recipe = new RecipeReference(content.RecipeKey, "draft", content.ContentHash);
            if (!policy.TryBind(new AlgorithmExecutionRequest(recipe, content.AlgorithmExecutionTimeout),
                    out _, out _))
                return Failure("RecipeDraftExecutionTimeoutInvalid");

            if (!RecipeDraftStorageCodec.TryEncodeContent(content, out _, out var codecReason))
                return Failure(codecReason.Length == 0 ? "RecipeDraftContentInvalid" : codecReason);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Failure("RecipeDraftContentInvalid"); }
    }

    private async Task<RecipeDraftValidationResult> ValidateSemanticAsync(Registration registration,
        RecipeDraftContent content, CancellationBudget operation)
    {
        var enteredFactory = false;
        var enteredSlot = false;
        try
        {
            if (operation.ConsumerCancellationRequested)
                return Failure("RecipeDraftValidationCancelled");
            if (!DescriptorMatchesFactory(registration))
                return Failure("RecipeDraftAlgorithmDescriptorChanged");
            await registration.Gate.WaitAsync(operation.Token).ConfigureAwait(false);
            enteredFactory = true;
            await _semanticSlots.WaitAsync(operation.Token).ConfigureAwait(false);
            enteredSlot = true;
            if (operation.ConsumerCancellationRequested)
                return Failure("RecipeDraftValidationCancelled");
            var issues = await registration.Factory.ValidateConfigurationAsync(content.Configuration, operation.Token)
                .ConfigureAwait(false);
            if (issues is null)
                return Failure("RecipeDraftSemanticValidationFailed");
            if (issues.Count != 0)
                return new(false, "RecipeDraftAlgorithmSemanticInvalid", CopyIssues(issues));
            if (!DescriptorMatchesFactory(registration))
                return Failure("RecipeDraftAlgorithmDescriptorChanged");
            return new(true, "RecipeDraftValid", Array.Empty<AlgorithmValidationIssue>());
        }
        catch (OperationCanceledException)
        {
            return Failure(_lifetime.IsCancellationRequested ? "RecipeDraftServiceDisposed" :
                "RecipeDraftSemanticValidationTimedOut");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Failure("RecipeDraftSemanticValidationFailed"); }
        finally
        {
            if (enteredSlot) _semanticSlots.Release();
            if (enteredFactory) registration.Gate.Release();
            operation.Dispose();
        }
    }

    private void OnCompleted(Task task)
    {
        lock (_sync)
        {
            _running.Remove(task);
            _outstanding--;
            SignalDrainedLocked();
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(static completed =>
    {
        _ = completed.Exception;
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static void TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private void FinishSave(CancellationBudget budget)
    {
        try { budget.Dispose(); }
        finally
        {
            lock (_sync)
            {
                try { _saveSlots.Release(); }
                finally
                {
                    _activeSaves--;
                    SignalDrainedLocked();
                }
            }
        }
    }

    private void CompleteValidationReservation()
    {
        lock (_sync)
        {
            _outstanding--;
            SignalDrainedLocked();
        }
    }

    private void BeginActivityLocked()
    {
        if (_outstanding == 0 && _activeSaves == 0 && _drained.Task.IsCompleted)
            _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void SignalDrainedLocked()
    {
        if (_outstanding == 0 && _activeSaves == 0)
            _drained.TrySetResult(true);
    }

    private void FinalizeResources()
    {
        lock (_sync)
        {
            if (_resourcesDisposed || !_disposed || !_drained.Task.IsCompleted ||
                _lifetimeCancellation is null ||
                !_lifetimeCancellation.IsCompletedSuccessfully)
                return;
            _resourcesDisposed = true;
        }

        // No admission can happen after _disposed, and the drain proves that all
        // continuations which release these resources have actually finished.
        _semanticSlots.Dispose();
        _saveSlots.Dispose();
        _lifetime.Dispose();
    }

    private static TaskCompletionSource<bool> CreateCompletedDrain()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }

    /// <summary>
    /// Separates a consumer cancellation callback from the token given to a
    /// factory or storage operation. A hostile callback therefore cannot run
    /// synchronously on the UI/caller thread; the physical operation still
    /// owns its gate until it observes cancellation and actually completes.
    /// </summary>
    private sealed class CancellationBudget : IDisposable
    {
        private readonly CancellationTokenSource _work;
        private readonly CancellationToken _consumer;
        private readonly CancellationTokenRegistration _callerRegistration;
        private int _cancelRequested;

        internal CancellationBudget(CancellationToken caller, CancellationToken lifetime,
            TimeSpan? timeout = null)
        {
            _consumer = caller;
            _work = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            if (timeout is { } duration) _work.CancelAfter(duration);
            _callerRegistration = caller.Register(static state =>
                ((CancellationBudget)state!).RequestCancellation(), this);
            if (caller.IsCancellationRequested) TryCancel(_work);
        }

        internal CancellationToken Token => _work.Token;
        internal bool ConsumerCancellationRequested => _consumer.IsCancellationRequested;

        internal void RequestCancellation()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) != 0) return;
            _ = Task.Run(() => TryCancel(_work));
        }

        public void Dispose()
        {
            _callerRegistration.Dispose();
            _work.Dispose();
        }
    }

    private TimeSpan CommitTimeout => _options.CommitTimeout >= TimeSpan.FromMilliseconds(1) &&
        _options.CommitTimeout <= TimeSpan.FromMinutes(5)
        ? _options.CommitTimeout : TimeSpan.FromSeconds(2);

    private TimeSpan ValidationTimeout => CommitTimeout;

    private static RecipeDraftValidationResult Failure(string reason, IReadOnlyList<AlgorithmValidationIssue>? issues = null) =>
        new(false, reason, new ReadOnlyCollection<AlgorithmValidationIssue>((issues ??
            Array.Empty<AlgorithmValidationIssue>()).Take(32).ToArray()));

    private static RecipeDraftSaveResult SaveFailure(string reason, IReadOnlyList<AlgorithmValidationIssue>? issues = null) =>
        new(false, reason, null, new ReadOnlyCollection<AlgorithmValidationIssue>((issues ??
            Array.Empty<AlgorithmValidationIssue>()).Take(32).ToArray()));

    private static IReadOnlyList<AlgorithmValidationIssue> CopyIssues(IReadOnlyList<AlgorithmValidationIssue> issues) =>
        new ReadOnlyCollection<AlgorithmValidationIssue>(issues.Take(32).ToArray());

    private static bool SameIdentity(AlgorithmIdentity left, AlgorithmIdentity right) =>
        left.Id == right.Id && left.Version == right.Version;

    private static bool SameSchema(AlgorithmConfigurationSchema left, AlgorithmConfigurationSchema right) =>
        left.Id == right.Id && left.Version == right.Version && left.ContentHash == right.ContentHash;

    private static bool SameContract(RecipeContractReference left, string id, string version, string hash) =>
        left.Id == id && left.Version == version && left.ContentHash == hash;

    private static bool DescriptorMatchesFactory(Registration registration)
    {
        var current = registration.Factory.Descriptor;
        return SameIdentity(current.Identity, registration.Descriptor.Identity) &&
            SameSchema(current.ConfigurationSchema, registration.Descriptor.ConfigurationSchema) &&
            SameContract(new RecipeContractReference(current.ResultSchema.Id, current.ResultSchema.Version,
                current.ResultSchema.ContentHash), registration.Descriptor.ResultSchema.Id,
                registration.Descriptor.ResultSchema.Version, registration.Descriptor.ResultSchema.ContentHash) &&
            current.ResultSchema.OverlayContract.Id == registration.Descriptor.ResultSchema.OverlayContract.Id &&
            current.ResultSchema.OverlayContract.Version == registration.Descriptor.ResultSchema.OverlayContract.Version &&
            current.ResultSchema.OverlayContract.ContentHash == registration.Descriptor.ResultSchema.OverlayContract.ContentHash;
    }

    private sealed record Registration(IVisionAlgorithmFactory Factory, AlgorithmDescriptor Descriptor)
    {
        public SemaphoreSlim Gate { get; } = AlgorithmFactorySynchronization.For(Factory);
    }
}
