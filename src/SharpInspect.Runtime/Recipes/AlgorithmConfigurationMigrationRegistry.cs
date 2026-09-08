using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// Bounded host registry for explicit configuration migrations.  A migration is
/// admitted only when its complete plan/context tuple matches the frozen descriptor.
/// </summary>
internal sealed class AlgorithmConfigurationMigrationRegistry : IAsyncDisposable
{
    private const int MaximumDescriptors = 64;
    private const int MaximumAdmitted = 16;
    private const int MaximumConcurrentMigrations = 2;
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    private readonly object _lifecycleSync = new();
    private readonly IReadOnlyList<Registration> _registrations;
    private readonly ReadOnlyCollection<AlgorithmConfigurationMigrationDescriptor> _migrations;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _globalSlot =
        new(MaximumConcurrentMigrations, MaximumConcurrentMigrations);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool> _drained = CompletedDrain();
    private int _admitted;
    private bool _disposed;
    private bool _resourcesDisposed;

    internal AlgorithmConfigurationMigrationRegistry(
        IEnumerable<IAlgorithmConfigurationMigrator> migrators, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(migrators);
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _timeout = timeout;
        var registrations = new List<Registration>();
        var descriptors = new List<AlgorithmConfigurationMigrationDescriptor>();
        var descriptorHashes = new HashSet<string>(StringComparer.Ordinal);
        var migratorIdentities = new Dictionary<(string Id, string Version), string>();
        foreach (var migrator in migrators)
        {
            if (registrations.Count >= MaximumDescriptors)
                throw new ArgumentException("RecipeDraftMigrationDescriptorCapacityExceeded", nameof(migrators));
            if (migrator is null)
                throw new ArgumentException("RecipeDraftMigrationMigratorRegistrationInvalid", nameof(migrators));

            AlgorithmConfigurationMigrationDescriptor? descriptor;
            try { descriptor = migrator.Descriptor; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                throw new ArgumentException("RecipeDraftMigrationDescriptorInvalid", nameof(migrators), exception);
            }
            if (descriptor is null)
                throw new ArgumentException("RecipeDraftMigrationDescriptorInvalid", nameof(migrators));

            var identity = (descriptor.Migrator.Id, descriptor.Migrator.Version);
            if (!descriptorHashes.Add(descriptor.ContentHash))
                throw new ArgumentException("RecipeDraftMigrationDescriptorDuplicate", nameof(migrators));
            if (migratorIdentities.TryGetValue(identity, out var existingHash))
                throw new ArgumentException(
                    existingHash == descriptor.ContentHash
                        ? "RecipeDraftMigrationDescriptorDuplicate"
                        : "RecipeDraftMigrationMigratorIdentityConflict",
                    nameof(migrators));

            migratorIdentities.Add(identity, descriptor.ContentHash);
            registrations.Add(new Registration(migrator, descriptor));
            descriptors.Add(descriptor);
        }

        _registrations = new ReadOnlyCollection<Registration>(registrations.ToArray());
        _migrations = new ReadOnlyCollection<AlgorithmConfigurationMigrationDescriptor>(descriptors.ToArray());
    }

    internal IReadOnlyList<AlgorithmConfigurationMigrationDescriptor> Migrations => _migrations;

    internal async ValueTask<AlgorithmConfigurationMigrationTransformResult> TransformAsync(
        RecipeDraftMigrationPlan plan, AlgorithmConfigurationMigrationContext context,
        CancellationToken cancellationToken)
    {
        if (plan is null || context is null)
            return Failure("RecipeDraftMigrationInputInvalid");

        Registration? registration;
        try { registration = Resolve(plan, context); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Failure("RecipeDraftMigrationInputInvalid"); }

        if (registration is null)
            return Failure("RecipeDraftMigrationMigratorUnavailable");
        if (IsTargetUnchanged(context))
            return Failure("RecipeDraftMigrationTargetUnchanged");
        if (!registration.IsCurrent())
            return Failure("RecipeDraftMigrationDescriptorChanged");
        if (cancellationToken.IsCancellationRequested)
            return Failure("RecipeDraftMigrationCancelled");

        var admitted = false;
        var globalSlot = false;
        var migratorSlot = false;
        var handedOff = false;
        CancellationBudget? workCancellation = null;
        try
        {
            if (!TryAdmit(out var admissionFailure))
                return Failure(admissionFailure);
            admitted = true;

            if (!_globalSlot.Wait(0))
                return Failure("RecipeDraftMigrationBusy");
            globalSlot = true;
            if (!registration.Slot.Wait(0))
                return Failure("RecipeDraftMigrationBusy");
            migratorSlot = true;

            lock (_lifecycleSync)
            {
                if (_disposed)
                    return Failure("RecipeDraftMigrationRegistryDisposed");
            }

            if (!registration.IsCurrent())
                return Failure("RecipeDraftMigrationDescriptorChanged");

            var work = new CancellationBudget(cancellationToken, _lifetime.Token, _timeout);
            workCancellation = work;
            var operation = Task.Run(() => ExecuteAsync(registration, plan, context,
                work.Token), CancellationToken.None);
            var state = new RunningOperation(this, registration, work,
                globalSlot, migratorSlot);
            _ = operation.ContinueWith(static (completed, state) =>
                ((RunningOperation)state!).Complete(completed), state,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            handedOff = true;

            try
            {
                return await operation.WaitAsync(_timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                work.RequestCancellation();
                return Failure("RecipeDraftMigrationTimedOut");
            }
            catch (OperationCanceledException)
            {
                work.RequestCancellation();
                return Failure("RecipeDraftMigrationCancelled");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return Failure("RecipeDraftMigrationFailed");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure("RecipeDraftMigrationFailed");
        }
        finally
        {
            if (!handedOff)
            {
                workCancellation?.Dispose();
                Release(registration, globalSlot, migratorSlot, admitted);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drained;
        var requestCancellation = false;
        lock (_lifecycleSync)
        {
            if (!_disposed)
            {
                _disposed = true;
                requestCancellation = true;
            }
            drained = _drained.Task;
        }

        if (requestCancellation)
            RequestCancellation(_lifetime);

        try { await drained.WaitAsync(ShutdownWait).ConfigureAwait(false); }
        catch (TimeoutException) { }
        TryDisposeResources();
    }

    private async Task<AlgorithmConfigurationMigrationTransformResult> ExecuteAsync(
        Registration registration, RecipeDraftMigrationPlan plan,
        AlgorithmConfigurationMigrationContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!registration.IsCurrent())
                return Failure("RecipeDraftMigrationDescriptorChanged");
            cancellationToken.ThrowIfCancellationRequested();
            var result = await registration.Migrator.MigrateAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (!registration.IsCurrent())
                return Failure("RecipeDraftMigrationDescriptorChanged");
            return result ?? Failure("RecipeDraftMigrationResultMissing");
        }
        catch (OperationCanceledException)
        {
            return !registration.IsCurrent()
                ? Failure("RecipeDraftMigrationDescriptorChanged")
                : Failure("RecipeDraftMigrationCancelled");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return !registration.IsCurrent()
                ? Failure("RecipeDraftMigrationDescriptorChanged")
                : Failure("RecipeDraftMigrationFailed");
        }
    }

    private Registration? Resolve(RecipeDraftMigrationPlan plan,
        AlgorithmConfigurationMigrationContext context)
    {
        if (plan.Migrator is null || plan.Source is null || plan.TargetAlgorithm is null ||
            plan.TargetSchema is null || context.SourceRevision is null ||
            context.SourceBinding is null || context.SourceConfiguration is null ||
            context.TargetDescriptor is null)
            return null;

        var registration = _registrations.SingleOrDefault(value =>
            SameContract(value.Descriptor.Migrator, plan.Migrator));
        if (registration is null || !Matches(registration.Descriptor, plan, context))
            return null;
        return registration;
    }

    private static bool Matches(AlgorithmConfigurationMigrationDescriptor descriptor,
        RecipeDraftMigrationPlan plan, AlgorithmConfigurationMigrationContext context)
    {
        var sourceBinding = context.SourceBinding;
        var sourceSchema = sourceBinding.ConfigurationSchema;
        var sourceConfiguration = context.SourceConfiguration;
        var target = context.TargetDescriptor;
        return context.SourceRevision == plan.Source &&
            SameContract(descriptor.Migrator, plan.Migrator) &&
            SameIdentity(sourceBinding.Algorithm, descriptor.SourceAlgorithm) &&
            SameSchema(sourceSchema, descriptor.SourceSchema) &&
            SameIdentity(plan.TargetAlgorithm, descriptor.TargetAlgorithm) &&
            SameContract(plan.TargetSchema, descriptor.TargetSchema) &&
            SameIdentity(target.Identity, descriptor.TargetAlgorithm) &&
            SameSchema(target.ConfigurationSchema, descriptor.TargetSchema) &&
            SameIdentity(plan.TargetAlgorithm, target.Identity) &&
            sourceConfiguration.SchemaId == sourceSchema.Id &&
            sourceConfiguration.SchemaVersion == sourceSchema.Version &&
            sourceConfiguration.SchemaContentHash == sourceSchema.ContentHash;
    }

    private static bool IsTargetUnchanged(AlgorithmConfigurationMigrationContext context) =>
        context.SourceBinding.ConfigurationSchema.Id == context.TargetDescriptor.ConfigurationSchema.Id &&
        context.SourceBinding.ConfigurationSchema.Version == context.TargetDescriptor.ConfigurationSchema.Version &&
        context.SourceBinding.ConfigurationSchema.ContentHash == context.TargetDescriptor.ConfigurationSchema.ContentHash;

    private static bool SameIdentity(AlgorithmIdentity first, AlgorithmIdentity second) =>
        first is not null && second is not null && first.Id == second.Id && first.Version == second.Version;

    private static bool SameSchema(AlgorithmConfigurationSchema schema,
        RecipeContractReference contract) =>
        schema is not null && contract is not null && schema.Id == contract.Id &&
        schema.Version == contract.Version && schema.ContentHash == contract.ContentHash;

    private static bool SameContract(RecipeContractReference first,
        RecipeContractReference second) =>
        first is not null && second is not null && first.Id == second.Id &&
        first.Version == second.Version && first.ContentHash == second.ContentHash;

    private static bool SameDescriptor(AlgorithmConfigurationMigrationDescriptor first,
        AlgorithmConfigurationMigrationDescriptor second) =>
        first.ContentHash == second.ContentHash &&
        SameContract(first.Migrator, second.Migrator) &&
        SameIdentity(first.SourceAlgorithm, second.SourceAlgorithm) &&
        SameContract(first.SourceSchema, second.SourceSchema) &&
        SameIdentity(first.TargetAlgorithm, second.TargetAlgorithm) &&
        SameContract(first.TargetSchema, second.TargetSchema);

    private bool TryAdmit(out string failure)
    {
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                failure = "RecipeDraftMigrationRegistryDisposed";
                return false;
            }
            if (_admitted >= MaximumAdmitted)
            {
                failure = "RecipeDraftMigrationCapacityExceeded";
                return false;
            }

            if (_admitted == 0)
                _drained = PendingDrain();
            _admitted++;
            failure = string.Empty;
            return true;
        }
    }

    private void Release(Registration registration, bool globalSlot,
        bool migratorSlot, bool admitted)
    {
        if (migratorSlot) registration.Slot.Release();
        if (globalSlot) _globalSlot.Release();

        var dispose = false;
        lock (_lifecycleSync)
        {
            if (admitted)
            {
                _admitted--;
                if (_admitted == 0)
                {
                    _drained.TrySetResult(true);
                    if (_disposed && !_resourcesDisposed)
                    {
                        _resourcesDisposed = true;
                        dispose = true;
                    }
                }
            }
        }
        if (dispose) DisposeResources();
    }

    private void TryDisposeResources()
    {
        var dispose = false;
        lock (_lifecycleSync)
        {
            if (_disposed && _admitted == 0 && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                dispose = true;
            }
        }
        if (dispose) DisposeResources();
    }

    private void DisposeResources()
    {
        _lifetime.Dispose();
        _globalSlot.Dispose();
        foreach (var registration in _registrations)
            registration.Slot.Dispose();
    }

    private static void RequestCancellation(CancellationTokenSource source)
    {
        _ = Task.Run(() =>
        {
            try { source.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        });
    }

    private static AlgorithmConfigurationMigrationTransformResult Failure(string reason) =>
        new(false, reason);

    private static TaskCompletionSource<bool> CompletedDrain()
    {
        var source = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }

    private static TaskCompletionSource<bool> PendingDrain() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class RunningOperation
    {
        private readonly AlgorithmConfigurationMigrationRegistry _owner;
        private readonly Registration _registration;
        private readonly CancellationBudget _workCancellation;
        private readonly bool _globalSlot;
        private readonly bool _migratorSlot;

        internal RunningOperation(AlgorithmConfigurationMigrationRegistry owner,
            Registration registration, CancellationBudget workCancellation,
            bool globalSlot, bool migratorSlot)
        {
            _owner = owner;
            _registration = registration;
            _workCancellation = workCancellation;
            _globalSlot = globalSlot;
            _migratorSlot = migratorSlot;
        }

        internal void Complete(Task<AlgorithmConfigurationMigrationTransformResult> task)
        {
            _ = task.Exception;
            var cancellation = _workCancellation.Finish();
            _ = cancellation.ContinueWith(static (completed, state) =>
            {
                var operation = (RunningOperation)state!;
                _ = completed.Exception;
                operation._workCancellation.DisposeWork();
                operation._owner.Release(operation._registration,
                    operation._globalSlot, operation._migratorSlot, admitted: true);
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Keeps consumer and lifetime cancellation callbacks off the caller and shutdown
    /// threads.  The cancellation work is observed before the migration reservation is
    /// released, so a late callback cannot race disposal or a subsequent migration.
    /// </summary>
    private sealed class CancellationBudget : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _work = new();
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _lifetimeRegistration;
        private readonly Timer _timeoutTimer;
        private Task? _cancellationTask;
        private bool _closed;
        private int _workDisposed;

        internal CancellationBudget(CancellationToken caller, CancellationToken lifetime,
            TimeSpan timeout)
        {
            CancellationTokenRegistration callerRegistration = default;
            CancellationTokenRegistration lifetimeRegistration = default;
            Timer? timeoutTimer = null;
            try
            {
                callerRegistration = caller.Register(static state =>
                    ((CancellationBudget)state!).RequestCancellation(), this);
                lifetimeRegistration = lifetime.Register(static state =>
                    ((CancellationBudget)state!).RequestCancellation(), this);
                timeoutTimer = new Timer(static state =>
                    ((CancellationBudget)state!).RequestCancellation(), this,
                    timeout, Timeout.InfiniteTimeSpan);
            }
            catch
            {
                callerRegistration.Unregister();
                lifetimeRegistration.Unregister();
                timeoutTimer?.Dispose();
                _work.Dispose();
                throw;
            }

            _callerRegistration = callerRegistration;
            _lifetimeRegistration = lifetimeRegistration;
            _timeoutTimer = timeoutTimer!;
            if (caller.IsCancellationRequested || lifetime.IsCancellationRequested)
                RequestCancellation();
        }

        internal CancellationToken Token => _work.Token;

        internal void RequestCancellation()
        {
            lock (_sync)
            {
                if (_closed || _cancellationTask is not null)
                    return;
                _cancellationTask = Task.Run(() =>
                {
                    try { _work.Cancel(); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { }
                });
            }
        }

        internal Task Finish()
        {
            lock (_sync)
            {
                if (!_closed)
                {
                    _closed = true;
                    _callerRegistration.Unregister();
                    _lifetimeRegistration.Unregister();
                    _timeoutTimer.Dispose();
                }
                return _cancellationTask ?? Task.CompletedTask;
            }
        }

        internal void DisposeWork()
        {
            if (Interlocked.Exchange(ref _workDisposed, 1) == 0)
                _work.Dispose();
        }

        public void Dispose()
        {
            var cancellation = Finish();
            if (cancellation.IsCompleted)
            {
                DisposeWork();
                return;
            }

            _ = cancellation.ContinueWith(static (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationBudget)state!).DisposeWork();
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class Registration
    {
        internal Registration(IAlgorithmConfigurationMigrator migrator,
            AlgorithmConfigurationMigrationDescriptor descriptor)
        {
            Migrator = migrator;
            Descriptor = descriptor;
        }

        internal IAlgorithmConfigurationMigrator Migrator { get; }
        internal AlgorithmConfigurationMigrationDescriptor Descriptor { get; }
        internal SemaphoreSlim Slot { get; } = new(1, 1);

        internal bool IsCurrent()
        {
            try
            {
                var current = Migrator.Descriptor;
                return current is not null && SameDescriptor(current, Descriptor);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return false; }
        }
    }
}
