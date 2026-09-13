using System.Diagnostics;
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
    internal string? ProductionPreparedBinaryHash(Guid instanceId) => _algorithms?.ProductionPreparedBinaryHash(instanceId);
    private readonly TimeSpan _preparationTimeout;
    private readonly FrameBufferPool? _frames;
    private readonly Func<Guid, CancellationToken, ValueTask<RecipeActivationRuntimeLease>> _reserveRuntime;
    private readonly Func<ValueTask<StationStateSnapshot>> _readStation;
    private readonly RecipeActivationInternalFixture? _fixture;
    private readonly Func<RecipeActivationSnapshot, CancellationToken,
        ValueTask<RecipeActivationDeploymentEvidence?>>? _deploymentEvidence;
    private readonly PartIdentityBindingRegistry? _partIdentities;
    private int _active;

    internal RecipeActivationService(RecipeDraftService drafts, IReleasedRecipeQuery releases,
        IPlcResultContractQuery contracts, IRecipeActivationQuery history, LocalAuthorizationService authorization,
        SqliteCommandStore store, ProductionStoreOptions options, AlgorithmPreparationService? algorithms,
        AlgorithmPreparationOptions? preparationOptions, FrameBufferPool? frames,
        Func<Guid, CancellationToken, ValueTask<RecipeActivationRuntimeLease>> reserveRuntime,
        Func<ValueTask<StationStateSnapshot>> readStation, RecipeActivationInternalFixture? fixture = null,
        Func<RecipeActivationSnapshot, CancellationToken,
            ValueTask<RecipeActivationDeploymentEvidence?>>? deploymentEvidence = null,
        PartIdentityBindingRegistry? partIdentities = null)
    {
        _preparation = new(drafts, releases, contracts, options, partIdentities); _history = history;
        // Startup recovery is an authority decision. Public query registrations
        // are capabilities for consumers and cannot attest that recovery is done.
        _startupHistory = new SqliteRecipeActivationQuery(options);
        _startupReleases = new SqliteReleasedRecipeQuery(options);
        _authorization = authorization; _store = store; _options = options; _algorithms = algorithms;
        _preparationTimeout = preparationOptions?.MaximumPreparationTimeout ?? TimeSpan.FromSeconds(5);
        _frames = frames; _reserveRuntime = reserveRuntime; _readStation = readStation; _fixture = fixture;
        _deploymentEvidence = deploymentEvidence;
        _partIdentities = partIdentities;
    }

    public ValueTask<RecipeActivationAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) => _authorization.GetRecipeActivationAccessAsync(invocation, cancellationToken);
    public ValueTask<RecipeActivationReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
        _history.ReadCurrentAsync(cancellationToken);
    public ValueTask<RecipeActivationReadResult> ReadAsync(RecipeActivationReference reference,
        CancellationToken cancellationToken = default) => _history.ReadAsync(reference, cancellationToken);
    public ValueTask<RecipeActivationPage> QueryAsync(RecipeActivationFilter filter,
        CancellationToken cancellationToken = default) => _history.QueryAsync(filter, cancellationToken);

    public ValueTask<RecipeActivationResult> ActivateAsync(ActivateRecipeCommand command,
        CancellationToken cancellationToken = default) => ActivateCoreAsync(command, null, cancellationToken);

    internal ValueTask<RecipeActivationResult> ActivatePlcAsync(ActivateRecipeCommand command,
        PlcRecipeActivationCapability capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(capability);
        if (_fixture is not null || !capability.TryConsume(command))
            return ValueTask.FromResult(new RecipeActivationResult(new(command.CorrelationId, CommandDisposition.Rejected,
                "RecipeChangeCapabilityInvalid", AuditPersistence.NotAttempted, Guid.NewGuid())));
        return ActivateCoreAsync(command, capability, cancellationToken);
    }

    private async ValueTask<RecipeActivationResult> ActivateCoreAsync(ActivateRecipeCommand command,
        PlcRecipeActivationCapability? plc, CancellationToken cancellationToken)
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
        RecipeActivationRuntimeLease? runtime = plc?.Runtime;
        RecipeActivationRecord? admitted = null;
        RecipeActivationRecord? previous = null;
        var execution = new RecipeActivationExecution();
        var durableSuccess = false;
        RecipeActivationDeploymentEvidence? stagedEvidence = null;
        PartIdentityBindingObservation? commitPartIdentity = null;
        Guid epoch = Guid.Empty;
        string? failure = entered ? null : "RecipeActivationCapacityExceeded";
        try
        {
            try
            {
                if (failure is null && plc is not null)
                {
                    failure = plc.RevocationFailure ?? runtime!.GetBlocker();
                    checks.Observe(1, failure is null, failure ?? "MappedPlcRecipeActivationCapability");
                    checks.Observe(2, failure is null, failure ?? "RecipeActivationQuiescenceReserved");
                }
                if (failure is null && plc is null)
                {
                    var access = await _authorization.GetRecipeActivationAccessAsync(command.Invocation,
                        command.HistoricalSelection is not null, cancellationToken).ConfigureAwait(false);
                    checks.Observe(1, access.CanActivate, access.ReasonCode);
                    if (!access.CanActivate) failure = access.ReasonCode;
                    else
                    {
                        var reserveStarted = Stopwatch.GetTimestamp();
                        while (true)
                        {
                            var remaining = Remaining(_options.CommitTimeout, reserveStarted);
                            if (remaining <= TimeSpan.Zero)
                            {
                                failure = "RecipeActivationRuntimeBusy";
                                break;
                            }
                            runtime = await _reserveRuntime(command.CorrelationId,
                                cancellationToken).ConfigureAwait(false);
                            checks.Observe(2, runtime.Available,
                                runtime.Failure ?? "RecipeActivationQuiescenceReserved");
                            failure = runtime.Failure;
                            if (!RecipeActivationRuntimeLease.IsRuntimeBusy(failure)) break;
                            runtime.Dispose();
                            runtime = null;
                            if (!await DelayRuntimeBusyAsync(reserveStarted, _options.CommitTimeout,
                                    cancellationToken).ConfigureAwait(false))
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { failure = "RecipeActivationCancelled"; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { failure = "RecipeActivationAdmissionUnavailable"; }
            epoch = runtime?.RuntimeEpoch ?? (await _readStation().ConfigureAwait(false)).RuntimeEpoch;
            var admissionStarted = Stopwatch.GetTimestamp();
            RecipeActivationAdmissionDecision admission;
            while (true)
            {
                var remaining = Remaining(_options.CommitTimeout, admissionStarted);
                if (remaining <= TimeSpan.Zero)
                {
                    admission = RuntimeBusyAdmission(command, attempt);
                    break;
                }
                admission = await _authorization.AdmitRecipeActivationAsync(command, epoch, attempt, kind,
                    checks.Snapshot(), failure,
                    () => runtime is null ? "RecipeActivationRuntimeUnavailable" : runtime.GetBlocker(),
                    new StoreDeadline(remaining), runtime?.Token ?? cancellationToken, plc).ConfigureAwait(false);
                if (plc is not null || !IsRetryableRuntimeBusy(admission) || runtime is null) break;
                // Identity writer 已回滚瞬时竞争；在事务外等待，并复用同一 command/attempt。
                try
                {
                    var blocker = await runtime.WaitForBlockerAsync(
                        Remaining(_options.CommitTimeout, admissionStarted), runtime.Token)
                        .ConfigureAwait(false);
                    if (blocker is null) continue;
                    admission = NonMutatingAdmission(command, attempt, blocker);
                }
                catch (OperationCanceledException) when (
                    runtime!.Token.IsCancellationRequested || cancellationToken.IsCancellationRequested)
                {
                    admission = new(new(command.CorrelationId, CommandDisposition.Rejected,
                        "RecipeActivationCancelled", AuditPersistence.NotAttempted, attempt));
                }
                break;
            }
            if (admission.Record?.Outcome.State != RecipeActivationOutcomeState.Admitted)
            {
                runtime?.PublishTerminal(admission.Outcome.ReasonCode, admission.Outcome.Audit == AuditPersistence.Unavailable);
                return new(admission.Outcome, admission.Record);
            }
            admitted = admission.Record;
            previous = admission.PreviousActive;
            var token = runtime!.Token;
            var inputs = await _preparation.PrepareAsync(command, checks, token).ConfigureAwait(false);
            // The closed development fixture keeps its historical behavior. A
            // local-authority activation with a deployment observer defers A12-A18
            // until physical staging has produced the immutable snapshot. Without
            // an observer the old fail-closed rejection is retained before I/O.
            if (_fixture is not null || _deploymentEvidence is null)
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
                if (_fixture is null && _deploymentEvidence is not null)
                {
                    stagedEvidence = await _deploymentEvidence(execution.Snapshot, token).ConfigureAwait(false);
                    checks.VerifyDeploymentEvidence(execution.Snapshot, stagedEvidence);
                    failure = checks.Failure;
                }
            }
            if (failure is null && execution.Snapshot is not null)
            {
                // 进入提交栅栏前立即重新采集；回调只做有界观察，不在提交回调中执行 I/O。
                // 观察发生变化时闭合失败，避免封存过期的部署证据。
                if (_fixture is null && _deploymentEvidence is not null)
                {
                    var finalEvidence = await _deploymentEvidence(execution.Snapshot, token).ConfigureAwait(false);
                    if (stagedEvidence is null || finalEvidence is null ||
                        stagedEvidence.ContentHash != finalEvidence.ContentHash)
                    {
                        checks.Set(12, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        checks.Set(13, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        checks.Set(14, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        checks.Set(15, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        checks.Set(16, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        checks.Set(17, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        checks.Set(18, RecipeActivationCheckStatus.Failed,
                            "ProductionDeploymentEvidenceChangedBeforeCommit");
                        failure = checks.Failure;
                    }
                    else
                    {
                        checks.VerifyDeploymentEvidence(execution.Snapshot, finalEvidence);
                        failure = checks.Failure;
                        commitPartIdentity = finalEvidence.PartIdentity;
                    }
                }
            }
            if (failure is null && execution.Snapshot is not null)
            {
                var commitStarted = Stopwatch.GetTimestamp();
                while (true)
                {
                    var remaining = Remaining(_options.CommitTimeout, commitStarted);
                    if (remaining <= TimeSpan.Zero)
                    {
                        failure = "RecipeActivationRuntimeBusy";
                        break;
                    }
                    RecipeActivationCommitLease? acquiredCommit = null;
                    using (var commitBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        commitBudget.CancelAfter(remaining);
                        try
                        {
                            acquiredCommit = await runtime.EnterCommitAsync(commitBudget.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (
                            !token.IsCancellationRequested && commitBudget.IsCancellationRequested)
                        {
                            failure = "RecipeActivationRuntimeBusy";
                        }
                    }
                    if (acquiredCommit is { } commit)
                    {
                        using (commit)
                        {
                            var transactionRemaining = Remaining(_options.CommitTimeout, commitStarted);
                            failure = transactionRemaining <= TimeSpan.Zero
                                ? "RecipeActivationRuntimeBusy" : commit.GetBlocker();
                            if (failure is null)
                            {
                                var committed = await _authorization.TryCommitRecipeActivationAsync(command, epoch,
                                    admitted, execution.Snapshot, checks.Snapshot(), () =>
                                    {
                                        // 最终 writer 决策只观察 registry 自有状态；此处绝不能运行 Provider 回调。
                                        if (_fixture is null && execution.Snapshot.Release.Source.Content
                                                .PartIdentityRequirement?.Mode != PartIdentityRequirementMode.None &&
                                            (commitPartIdentity is null || _partIdentities is null ||
                                             _partIdentities.RevisionFor(commitPartIdentity.Provider) !=
                                             commitPartIdentity.RegistryRevision))
                                            return "PartIdentitySourceChangedBeforeCommit";
                                        var pool = _frames?.GetSnapshot();
                                        if (pool is not { IsDisposed: false, ProductionFaultLatched: false,
                                            OutstandingLeases: 0, ActiveReaders: 0 })
                                            return "RecipeActivationFramePoolChanged";
                                        return commit.TryBeginCommit();
                                    }, new StoreDeadline(transactionRemaining), token, plc).ConfigureAwait(false);
                                if (committed.Committed && committed.Result is { } result)
                                {
                                    durableSuccess = true;
                                    if (result.Record is { } committedRecord) runtime.RecordCommitted(committedRecord);
                                    var installed = execution.InstallCommitted(runtime);
                                    // Do not hold the Runtime command gate while an old
                                    // prepared algorithm retires outside the transaction.
                                    commit.Dispose();
                                    var cleanup = installed.Previous is null ? null :
                                        await RecipeActivationExecution.RetireBoundedAsync(installed.Previous)
                                            .ConfigureAwait(false);
                                    runtime.PublishTerminal(!installed.Installed ?
                                        "RecipeActivationResourceInstallFailed" :
                                        cleanup ?? result.Outcome.ReasonCode,
                                        !installed.Installed || cleanup is not null);
                                    return result;
                                }
                                failure = committed.ReasonCode;
                            }
                        }
                    }
                    if (plc is not null || !RecipeActivationRuntimeLease.IsRuntimeBusy(failure)) break;
                    // candidate 已准备好；最终 claim 忙碌意味着事务已回滚，因此只重试持久化提交。
                    try
                    {
                        var blocker = await runtime.WaitForBlockerAsync(
                            Remaining(_options.CommitTimeout, commitStarted), token)
                            .ConfigureAwait(false);
                        if (blocker is null) continue;
                        failure = blocker;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        failure = "RecipeActivationCancelled";
                    }
                    break;
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
            // Camera operation ownership 必须先结束，才能释放 station reservation。
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

    private static bool IsRetryableRuntimeBusy(RecipeActivationAdmissionDecision admission) =>
        admission.Record is null && admission.Outcome.Audit == AuditPersistence.NotAttempted &&
        RecipeActivationRuntimeLease.IsRuntimeBusy(admission.Outcome.ReasonCode);

    private static RecipeActivationAdmissionDecision RuntimeBusyAdmission(
        ActivateRecipeCommand command, Guid attemptId) =>
        NonMutatingAdmission(command, attemptId, "RecipeActivationRuntimeBusy");

    private static RecipeActivationAdmissionDecision NonMutatingAdmission(
        ActivateRecipeCommand command, Guid attemptId, string reason) =>
        new(new(command.CorrelationId, CommandDisposition.Rejected,
            reason, AuditPersistence.NotAttempted, attemptId));

    private static TimeSpan Remaining(TimeSpan budget, long started)
    {
        var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) /
            (double)Stopwatch.Frequency);
        var remaining = budget - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async ValueTask<bool> DelayRuntimeBusyAsync(long started,
        TimeSpan budget, CancellationToken cancellationToken)
    {
        var remaining = Remaining(budget, started);
        if (remaining <= TimeSpan.Zero) return false;
        var delay = remaining > TimeSpan.FromMilliseconds(10)
            ? TimeSpan.FromMilliseconds(10) : remaining;
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
