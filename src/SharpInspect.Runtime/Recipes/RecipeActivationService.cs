using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Recipes;

/// <summary>The governed activation coordinator. Success never arms production.</summary>
internal sealed partial class RecipeActivationService : IRecipeActivationService
{
    private readonly RecipeActivationPreparation _preparation;
    private readonly IRecipeActivationQuery _history;
    private readonly SqliteRecipeActivationQuery _startupHistory;
    private readonly SqliteReleasedRecipeQuery _startupReleases;
    private readonly LocalAuthorizationService _authorization;
    private readonly SqliteCommandStore _store;
    private readonly ProductionStoreOptions _options;
    private readonly AlgorithmPreparationService? _algorithms;
    private readonly TimeSpan _preparationTimeout;
    private readonly FrameBufferPool? _frames;
    private readonly Func<Guid, CancellationToken, ValueTask<RecipeActivationRuntimeLease>> _reserveRuntime;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private readonly RecipeActivationInternalFixture? _fixture;
    private int _active;

    internal RecipeActivationService(RecipeDraftService drafts, IReleasedRecipeQuery releases,
        IPlcResultContractQuery contracts, IRecipeActivationQuery history, LocalAuthorizationService authorization,
        SqliteCommandStore store, ProductionStoreOptions options, AlgorithmPreparationService? algorithms,
        AlgorithmPreparationOptions? preparationOptions, FrameBufferPool? frames,
        Func<Guid, CancellationToken, ValueTask<RecipeActivationRuntimeLease>> reserveRuntime,
        Func<ValueTask<StationStateSnapshot>> readStation, RecipeActivationInternalFixture? fixture = null)
    {
        _preparation = new(drafts, releases, contracts, options); _history = history;
        // Startup recovery is an authority decision. Public query registrations
        // are capabilities for consumers and cannot attest that recovery is done.
        _startupHistory = new SqliteRecipeActivationQuery(options);
        _startupReleases = new SqliteReleasedRecipeQuery(options);
        _authorization = authorization; _store = store; _options = options; _algorithms = algorithms;
        _preparationTimeout = preparationOptions?.MaximumPreparationTimeout ?? TimeSpan.FromSeconds(5);
        _frames = frames; _reserveRuntime = reserveRuntime; _readStation = readStation; _fixture = fixture;
    }

    public ValueTask<RecipeActivationAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) => _authorization.GetRecipeActivationAccessAsync(invocation, cancellationToken);
    public ValueTask<RecipeActivationReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        _history.ReadCurrentAsync(cancellationToken);
    public ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
        CancellationToken cancellationToken = default) => _history.ReadAsync(reference, cancellationToken);
    public ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
        CancellationToken cancellationToken = default) => _history.QueryAsync(filter, cancellationToken);

    public async ValueTask<RecipeActivationResult> ActivateAsync(ActivateRecipeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.HistoricalSelection is not null && !_store.CalibrationImportEnabled)
            return new(new(command.CorrelationId, CommandDisposition.Rejected,
                "HistoricalCalibrationConfigurationRequired", AuditPersistence.NotAttempted, Guid.NewGuid()));
        var attempt = Guid.NewGuid();
        var kind = _fixture is null ? RecipeActivationEvidenceKind.LocalAuthority : RecipeActivationEvidenceKind.InternalContractFixture;
        var checks = new RecipeActivationChecks();
        var entered = Interlocked.Increment(ref _active) <= 2;
        if (!entered) Interlocked.Decrement(ref _active);
        RecipeActivationRuntimeLease? runtime = null;
        RecipeActivationRecord? admitted = null;
        RecipeActivationRecord? previous = null;
        var execution = new RecipeActivationExecution();
        var durableSuccess = false;
        Guid epoch = Guid.Empty;
        string? failure = entered ? null : "RecipeActivationCapacityExceeded";
        try
        {
            try
            {
                if (failure is null)
                {
                    var access = await _authorization.GetRecipeActivationAccessAsync(command.Invocation,
                        command.HistoricalSelection is not null, cancellationToken).ConfigureAwait(false);
                    checks.Observe(1, access.CanActivate, access.ReasonCode);
                    if (!access.CanActivate) failure = access.ReasonCode;
                    else
                    {
                        runtime = await _reserveRuntime(command.CorrelationId, cancellationToken).ConfigureAwait(false);
                        checks.Observe(2, runtime.Available, runtime.Failure ?? "RecipeActivationQuiescenceReserved");
                        failure = runtime.Failure;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { failure = "RecipeActivationCancelled"; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { failure = "RecipeActivationAdmissionUnavailable"; }
            epoch = runtime?.RuntimeEpoch ?? (await _readStation().ConfigureAwait(false)).RuntimeEpoch;
            var admission = await _authorization.AdmitRecipeActivationAsync(command, epoch, attempt, kind,
                checks.Snapshot(), failure, () => runtime is null ? "RecipeActivationRuntimeUnavailable" : runtime.GetBlocker(),
                new StoreDeadline(_options.CommitTimeout),
                runtime?.Token ?? cancellationToken).ConfigureAwait(false);
            if (admission.Record?.Outcome.State != RecipeActivationOutcomeState.Admitted)
            {
                runtime?.PublishTerminal(admission.Outcome.ReasonCode, admission.Outcome.Audit == AuditPersistence.Unavailable);
                return new(admission.Outcome, admission.Record);
            }
            admitted = admission.Record;
            previous = admission.PreviousActive;
            var token = runtime!.Token;
            var inputs = await _preparation.PrepareAsync(command, checks, token).ConfigureAwait(false);
            checks.VerifyInstalledAuthorities(_fixture);
            if (_algorithms is null) checks.Observe(6, false, "RecipeActivationAlgorithmPreparationUnavailable");
            if (_frames is null) checks.Observe(11, false, "RecipeActivationFramePoolUnavailable");
            failure = checks.Failure ?? (inputs is null ? "RecipeActivationDependenciesUnavailable" : null);
            if (failure is null)
            {
                await execution.StageAsync(admitted, inputs!, runtime, checks, _algorithms, _preparationTimeout,
                    _options.RecipeDrafts!.ExecutionPolicy, _frames, _store, previous?.SuccessfulSnapshot).ConfigureAwait(false);
                failure = execution.Failure ?? checks.Failure;
            }
            if (failure is null && execution.Snapshot is not null)
            {
                using var commit = await runtime.EnterCommitAsync(token).ConfigureAwait(false);
                failure = commit.GetBlocker();
                if (failure is null)
                {
                    var committed = await _authorization.TryCommitRecipeActivationAsync(command, epoch, admitted,
                        execution.Snapshot, checks.Snapshot(), () =>
                        {
                            var pool = _frames?.GetSnapshot();
                            if (pool is not { IsDisposed: false, ProductionFaultLatched: false, OutstandingLeases: 0, ActiveReaders: 0 })
                                return "RecipeActivationFramePoolChanged";
                            return commit.TryBeginCommit();
                        }, new StoreDeadline(_options.CommitTimeout), token).ConfigureAwait(false);
                    if (committed.Committed && committed.Result is { } result)
                    {
                        durableSuccess = true;
                        var installed = execution.InstallCommitted(runtime);
                        commit.Dispose();
                        var cleanup = installed.Previous is null ? null :
                            await RecipeActivationExecution.RetireBoundedAsync(installed.Previous).ConfigureAwait(false);
                        runtime.PublishTerminal(!installed.Installed ? "RecipeActivationResourceInstallFailed" :
                            cleanup ?? result.Outcome.ReasonCode, !installed.Installed || cleanup is not null);
                        return result;
                    }
                    failure = committed.ReasonCode;
                }
            }
            return await FailAsync(failure ?? "RecipeActivationPreparationFailed").ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (durableSuccess)
            {
                runtime?.PublishTerminal("RecipeActivationPostCommitFault", true);
                return new(new(command.CorrelationId, CommandDisposition.Accepted, "RecipeActivationPostCommitFault",
                    AuditPersistence.Persisted, attempt));
            }
            var reason = runtime?.Token.IsCancellationRequested == true || cancellationToken.IsCancellationRequested
                ? "RecipeActivationCancelled" : "RecipeActivationPreparationFailed";
            if (admitted is not null)
            {
                try { return await FailAsync(reason).ConfigureAwait(false); }
                catch (Exception cleanupException) when (cleanupException is not OutOfMemoryException)
                { runtime?.PublishTerminal("RecipeActivationRecoveryRequired", true); }
            }
            else runtime?.PublishTerminal("RecipeActivationAuditUnavailable", true);
            return new(new(command.CorrelationId, CommandDisposition.Rejected, "RecipeActivationTerminalUnavailable",
                AuditPersistence.Unavailable, attempt));
        }
        finally
        {
            // Camera operation ownership must retire before the station reservation is released.
            try { await execution.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { runtime?.PublishTerminal("RecipeActivationResourceCleanupFailed", true); }
            finally
            {
                runtime?.Dispose();
                if (entered) Interlocked.Decrement(ref _active);
            }
        }

        async ValueTask<RecipeActivationResult> FailAsync(string reason)
        {
            var restoration = await execution.RestoreAsync(previous?.SuccessfulSnapshot).ConfigureAwait(false);
            await execution.RetireCandidateAsync().ConfigureAwait(false);
            var cancelled = reason == "RecipeActivationCancelled" || runtime?.Token.IsCancellationRequested == true;
            var result = await _authorization.FinalizeRecipeActivationFailureAsync(command, epoch, admitted!, checks.Snapshot(),
                restoration, cancelled ? RecipeActivationOutcomeState.Cancelled : RecipeActivationOutcomeState.Failed,
                cancelled ? "RecipeActivationCancelled" : reason, new StoreDeadline(_options.CommitTimeout)).ConfigureAwait(false);
            runtime?.PublishTerminal(execution.CleanupFailure ?? result.Outcome.ReasonCode,
                restoration.State == RecipeActivationRestorationState.Failed || execution.CleanupFailure is not null ||
                result.Outcome.Audit != AuditPersistence.Persisted);
            return result;
        }
    }
}
