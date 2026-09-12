using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private bool _productionAdmissionEnabled;
    private IProductionAdmissionFactsSource? _productionAdmissionFactsSource;
    // Qualification evidence is an internal Runtime-owned seam.  It can replace
    // only the signed qualification inputs; the built-in runtime gates below are
    // always captured from the live StationStateSnapshot.
    private IProductionInspectionQualificationEvidenceProvider?
        _productionInspectionQualificationEvidenceProvider;
    private long _admissionGeneration;
    private string _admissionStateHash = string.Empty;
    // Keep the immutable facts so the fixed engine can re-evaluate time-dependent
    // qualification validity as snapshots advance.
    private ProductionAdmissionFacts? _lastAdmissionFacts;
    private string? _productionAdmissionFactsFailure;
    private ProductionAdmissionGateResult? _lastVerifiedStoreIntegrityGate;

    private ProductionAdmissionGateResult? MaterialStoreIntegrityGate =>
        _audit?.Integrity?.State is AuditIntegrityState.Verifying or AuditIntegrityState.Verified
            ? _lastVerifiedStoreIntegrityGate : null;

    private string MaterialAdmissionEvidenceHash(ProductionAdmissionReport report) =>
        ProductionAdmissionEngine.MaterialEvidenceHash(report, MaterialStoreIntegrityGate);

    internal bool ProductionAdmissionEnabled => _productionAdmissionEnabled;
    internal long AdmissionGeneration
    {
        get { lock (_sync) return _admissionGeneration; }
    }

    /// <summary>
    /// Installs the internal source for qualification evidence used by the
    /// production admission capture.  This is deliberately separate from the
    /// public admission facts source: a provider cannot supply, override, or
    /// mark any Runtime gate as passed.
    /// </summary>
    internal void ConfigureProductionInspectionQualificationEvidenceProvider(
        IProductionInspectionQualificationEvidenceProvider? provider)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_productionInspectionQualificationEvidenceProvider, provider)) return;
            _productionInspectionQualificationEvidenceProvider = provider;
            if (!_productionAdmissionEnabled || _disposed || _shutdownRequested) return;

            _admissionGeneration = checked(_admissionGeneration + 1);
            PublishUnavailableProductionAdmissionLocked(
                provider is null ? "ProductionQualificationEvidenceUnavailable" :
                    "ProductionQualificationEvidenceProviderChanged");
        }
    }

    private void ConfigureProductionAdmission(ProductionStoreOptions? options,
        IProductionAdmissionFactsSource? factsSource)
    {
        _productionAdmissionEnabled = options?.ProductionAdmission is not null;
        _productionAdmissionFactsSource = factsSource ?? new CurrentStationFactsSource(this);
        _admissionGeneration = 1;
        if (_productionAdmissionEnabled)
        {
            // Publish a conservative initial report synchronously.  It is composed solely
            // from the built-in unconfigured source; a caller supplied source is evaluated
            // only through the bounded async refresh/Arm path below.
            var facts = CaptureDefaultFactsLocked();
            _lastAdmissionFacts = facts;
            _snapshot = _snapshot with
            {
                ProductionAdmission = ProductionAdmissionEngine.Evaluate(_snapshot.RuntimeEpoch,
                    _snapshot.Revision, _admissionGeneration, DateTimeOffset.UtcNow, facts)
            };
        }
        _admissionStateHash = ComputeAdmissionStateHash(_snapshot);
        if (_productionAdmissionEnabled && _audit is SqliteCommandStore store)
            store.ProductionAdmissionMaterialChanging += OnProductionAdmissionMaterialChanging;
    }

    private void OnProductionAdmissionMaterialChanging()
    {
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return;
            // This is the same lock as the final Ready fence. The writer invokes
            // it before commit; a change after that fence first disarms production.
            _admissionGeneration = checked(_admissionGeneration + 1);
            PublishUnavailableProductionAdmissionLocked("ProductionAdmissionDurableHeadsChanged",
                writerInvalidation: true);
        }
    }

    internal async ValueTask<bool> VerifyProductionAdmissionHeadsAsync(
        IReadOnlyDictionary<string, string> expectedHeads, StoreDeadline deadline)
    {
        try
        {
            if (_audit is not SqliteCommandStore { ProductionAdmissionEnabled: true } store) return false;
            using var cancellation = new CancellationTokenSource(PositiveRemaining(deadline));
            var current = await store.ReadProductionAdmissionDurableHeadsAsync(cancellation.Token)
                .AsTask().WaitAsync(PositiveRemaining(deadline)).ConfigureAwait(false);
            if (SqliteCommandStore.ProductionAdmissionHeadsEqual(expectedHeads, current)) return true;
            lock (_sync) PublishUnavailableProductionAdmissionLocked("ProductionAdmissionDurableHeadsChanged");
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync) PublishUnavailableProductionAdmissionLocked("ProductionAdmissionFactsUnavailable");
            return false;
        }
    }

    internal async ValueTask<ProductionAdmissionReport?> RefreshProductionAdmissionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_productionAdmissionEnabled) return null;
        AdmissionCapture capture;
        lock (_sync)
        {
            if (_disposed || _shutdownRequested) return _snapshot.ProductionAdmission;
            capture = new AdmissionCapture(_snapshot.RuntimeEpoch, _snapshot.Revision,
                _admissionGeneration, _admissionStateHash);
        }

        ProductionAdmissionFacts facts;
        try
        {
            facts = await _productionAdmissionFactsSource!.CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_sync)
                return PublishUnavailableProductionAdmissionLocked("ProductionAdmissionFactsUnavailable");
        }

        var report = ProductionAdmissionEngine.Evaluate(capture.RuntimeEpoch,
            checked(capture.Revision + 1), capture.Generation, DateTimeOffset.UtcNow, facts);
        lock (_sync)
        {
            if (_disposed || _shutdownRequested || _snapshot.RuntimeEpoch != capture.RuntimeEpoch ||
                _admissionGeneration != capture.Generation ||
                !string.Equals(_admissionStateHash, capture.StateHash, StringComparison.Ordinal))
                return _snapshot.ProductionAdmission;
            _productionAdmissionFactsFailure = null;
            _lastAdmissionFacts = facts;
            PublishLocked(_snapshot with { ProductionAdmission = report });
            return _snapshot.ProductionAdmission;
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitProductionArmAsync(
        ArmProductionCommand command, CancellationToken callerCancellation)
    {
        var attemptId = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason, ProductionAdmissionReport? report = null) =>
            new(command.CorrelationId, CommandDisposition.Rejected, reason,
                AuditPersistence.Unavailable, attemptId) { ProductionAdmission = report };

        if (command.CorrelationId == Guid.Empty || command.Invocation is null ||
            !Enum.IsDefined(command.Invocation.Source) || command.Invocation.PrincipalId?.Length > 256)
            return Unavailable("InvalidCommandContext", _snapshot.ProductionAdmission);

        var deadline = new StoreDeadline(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            lock (_sync)
            {
                if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped", _snapshot.ProductionAdmission);
                if (LocalStopPendingLocked)
                    return Unavailable("LocalStopPending", _snapshot.ProductionAdmission);
                if (_automaticProductionArm is { Terminal: false } || _manualMaintenanceArm is { Terminal: false })
                    return Unavailable("ProductionArmAttemptInProgress", _snapshot.ProductionAdmission);
                if (_snapshot.LastCommand?.CorrelationId == command.CorrelationId)
                    return Unavailable("DuplicateCorrelationId", _snapshot.ProductionAdmission);
            }

            await _storeInitialization.WaitAsync(PositiveRemaining(deadline), callerCancellation)
                .ConfigureAwait(false);
            if (_authorization is null || _audit is not SqliteCommandStore { ProductionAdmissionEnabled: true })
                return Unavailable("ProductionAdmissionConfigurationRequired", _snapshot.ProductionAdmission);

            // Keep the same bounded preparation and audit-verification admission used by
            // other governed commands.  Arm does not derive credentials, but preparation
            // still provides a uniform deadline/cancellation boundary.
            var preparationTask = _authorization.PrepareCommandAsync(command, callerCancellation);
            _ = preparationTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var preparation = await preparationTask.WaitAsync(PositiveRemaining(deadline), callerCancellation)
                .ConfigureAwait(false);
            if (preparation.Reason is not null)
                return Unavailable(preparation.Reason, _snapshot.ProductionAdmission);

            entered = await _commandGate.WaitAsync(PositiveRemaining(deadline), callerCancellation)
                .ConfigureAwait(false);
            if (!entered) return Unavailable("CommandDeadlineExceeded", _snapshot.ProductionAdmission);

            string? networkBarrier;
            AdmissionCapture capture;
            lock (_sync)
            {
                if (_shutdownRequested || _disposed) return Unavailable("RuntimeStopped", _snapshot.ProductionAdmission);
                if (LocalStopPendingLocked)
                    return Unavailable("LocalStopPending", _snapshot.ProductionAdmission);
                capture = new AdmissionCapture(_snapshot.RuntimeEpoch, _snapshot.Revision,
                    _admissionGeneration, _admissionStateHash);
            }
            networkBarrier = await _cameraSetupRuntime.CheckNetworkBarrierAsync(callerCancellation)
                .AsTask().WaitAsync(PositiveRemaining(deadline), callerCancellation).ConfigureAwait(false);
            string? forced;
            lock (_sync)
            {
                forced = _shutdownRequested || _disposed ? "RuntimeStopped" : networkBarrier;
                if (forced is null && _activationReservation is { } recipeReservation)
                    forced = recipeReservation.Purpose == ActivationReservationPurpose.Retirement
                        ? "RecipeRetirementInProgress" : "RecipeActivationInProgress";
                if (forced is null && StationQualificationConfigurationBlockedLocked) forced = "StationQualificationSessionInProgress";
                if (forced is null && LocalStopPendingLocked) forced = "LocalStopPending";
                if (forced is null && _snapshot.Mode != ExclusiveMode.None) forced = "ExclusiveWorkInProgress";
                if (forced is null && _snapshot.Recovery != RecoveryState.None) forced = "StartupRecoveryNotVerified";
            }

            ProductionAdmissionFacts facts;
            try
            {
                var factsTask = _productionAdmissionFactsSource!.CaptureAsync(callerCancellation).AsTask();
                _ = factsTask.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                facts = await factsTask.WaitAsync(PositiveRemaining(deadline), callerCancellation)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var factsReason = exception is TimeoutException ? "ProductionAdmissionFactsDeadlineExceeded" :
                    "ProductionAdmissionFactsUnavailable";
                lock (_sync) return Unavailable(factsReason, PublishUnavailableProductionAdmissionLocked(factsReason));
            }

            ProductionAdmissionReport report;
            lock (_sync)
            {
                // Do not let a heartbeat or another admission-affecting projection
                // replace the source facts while this captured report is stale.
                var changed = _snapshot.RuntimeEpoch != capture.RuntimeEpoch ||
                    _admissionGeneration != capture.Generation ||
                    !string.Equals(_admissionStateHash, capture.StateHash, StringComparison.Ordinal);
                if (changed)
                {
                    // A valid human attempt still gets a durable rejection. Do
                    // not replace current facts with this obsolete observation.
                    forced = "ProductionAdmissionChanged";
                    PublishLocked(_snapshot);
                }
                else
                {
                    _productionAdmissionFactsFailure = null;
                    _lastAdmissionFacts = facts;
                    // Linearize newly captured material facts before handing the
                    // attempt to the writer. Later fences compare this generation.
                    var observedReport = ProductionAdmissionEngine.Evaluate(capture.RuntimeEpoch,
                        checked(_snapshot.Revision + 1), capture.Generation, DateTimeOffset.UtcNow, facts);
                    PublishLocked(_snapshot with { ProductionAdmission = observedReport });
                }
                capture = new AdmissionCapture(_snapshot.RuntimeEpoch, _snapshot.Revision,
                    _admissionGeneration, _admissionStateHash);
                report = _snapshot.ProductionAdmission!;
                facts = _lastAdmissionFacts!;
            }
            var outcome = await _authorization.HandleProductionArmAsync(command, capture.RuntimeEpoch,
                attemptId, capture.Generation, facts, report, forced, deadline, callerCancellation)
                .ConfigureAwait(false);
            if (outcome.Audit == AuditPersistence.Unavailable)
            {
                MarkAuditFault(outcome.ReasonCode);
                lock (_sync) PublishLocked(_snapshot with { ProductionAdmission = report });
                return outcome;
            }

            if (outcome.Disposition != CommandDisposition.Accepted)
            {
                lock (_sync)
                {
                    if (!_disposed) PublishLocked(_snapshot with { ProductionAdmission = report,
                        LastCommand = new CommandProgress(command.CorrelationId,
                            OperationState.Completed, outcome.ReasonCode) });
                }
                return outcome;
            }

            // A committed identity/report transaction can briefly make the audit monitor
            // Verifying.  Do not publish Ready until that exact commit is observable as
            // Verified; a caller cancellation after admission cannot erase the commit.
            var auditVerified = await WaitForProductionAuditVerifiedAsync(deadline).ConfigureAwait(false);
            var durableHeadsVerified = auditVerified &&
                await VerifyProductionAdmissionHeadsAsync(facts.DurableHeads, deadline).ConfigureAwait(false);
            bool stable;
            lock (_sync)
            {
                if (!_disposed) PublishLocked(_snapshot);
                stable = auditVerified && durableHeadsVerified && ProductionArmLiveFenceLocked(capture);
            }

            if (stable)
            {
                // Finalize the authorized report before considering Ready. This
                // ledger event proves durable authorization, never that the station
                // was Armed. Only the subsequent live-state fence can publish Armed.
                var completionCommitted = await CompleteProductionAdmissionTerminalAsync(
                    command, outcome, capture, "ProductionAdmissionFinalized", deadline).ConfigureAwait(false);
                var completionVerified = completionCommitted &&
                    await WaitForProductionAuditVerifiedAsync(deadline).ConfigureAwait(false);
                var completionHeadsVerified = completionVerified &&
                    await VerifyProductionAdmissionHeadsAsync(facts.DurableHeads, deadline).ConfigureAwait(false);
                var maintenanceAuthorized = completionHeadsVerified &&
                    await PrepareManualMaintenanceArmAsync(command, capture, facts.DurableHeads, deadline).ConfigureAwait(false);
                lock (_sync)
                {
                    if (!_disposed) PublishLocked(_snapshot);
                    var stillStable = completionCommitted && completionVerified && completionHeadsVerified && maintenanceAuthorized &&
                        ProductionArmLiveFenceLocked(capture);
                    if (stillStable)
                        PublishLocked(_snapshot with { Ready = _productionInspectionOptions is null, ArmState = ProductionArmState.Armed,
                            ProductionAdmission = report,
                            LastCommand = new CommandProgress(command.CorrelationId,
                                OperationState.Completed, "ProductionArmed") }, completingProductionArm: true);
                    else if (!_disposed)
                        PublishLocked(_snapshot with { Ready = false,
                            ArmState = ProductionArmState.Disarmed, ProductionAdmission = report,
                            LastCommand = new CommandProgress(command.CorrelationId,
                                OperationState.Failed,
                                completionCommitted && completionVerified
                                    ? "ProductionAdmissionChanged" : "ProductionAdmissionAuditUnavailable") });
                    if (stillStable) return outcome;
                }

                return outcome with { ProductionAdmission = report };
            }

            var reason = auditVerified ? "ProductionAdmissionChanged" : "ProductionAdmissionAuditUnavailable";
            _ = await CompleteProductionAdmissionTerminalAsync(command, outcome, capture, reason, deadline)
                .ConfigureAwait(false);
            lock (_sync)
            {
                if (!_disposed) PublishLocked(_snapshot with { Ready = false,
                    ArmState = ProductionArmState.Disarmed, ProductionAdmission = report,
                    LastCommand = new CommandProgress(command.CorrelationId,
                        OperationState.Failed, reason) });
            }
            return outcome with { ProductionAdmission = report };
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            return Unavailable("ProductionAdmissionCancelled", _snapshot.ProductionAdmission);
        }
        catch (TimeoutException)
        {
            return Unavailable("CommandDeadlineExceeded", _snapshot.ProductionAdmission);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable(exception.Message.StartsWith("ProductionAdmission", StringComparison.Ordinal)
                ? exception.Message : "ProductionAdmissionUnavailable", _snapshot.ProductionAdmission);
        }
        finally
        {
            ManualMaintenanceArmCapability? unarmed;
            lock (_sync) unarmed = _manualMaintenanceArm is { Terminal: false } pending &&
                pending.Admission.CorrelationId == command.CorrelationId && _snapshot.ArmState != ProductionArmState.Armed
                    ? pending : null;
            if (unarmed is not null)
                await FailManualMaintenanceArmAsync(unarmed, null, "ProductionArmManualMaintenanceAdmissionFailed")
                    .ConfigureAwait(false);
            if (entered) _commandGate.Release();
        }
    }

    private async ValueTask<bool> WaitForProductionAuditVerifiedAsync(StoreDeadline deadline)
    {
        if (_audit is null) return false;
        try
        {
            if (_authorization is not null)
                await _authorization.WaitForAuditAsync(CancellationToken.None)
                    .WaitAsync(PositiveRemaining(deadline), CancellationToken.None).ConfigureAwait(false);
            return _audit.Integrity?.State == AuditIntegrityState.Verified;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    private async ValueTask<bool> CompleteProductionAdmissionTerminalAsync(ArmProductionCommand command,
        RuntimeCommandOutcome outcome, AdmissionCapture capture, string reason, StoreDeadline deadline)
    {
        if (_audit is IProductionAdmissionTerminalWriter terminalWriter)
        {
            try
            {
                var result = await terminalWriter.CompleteProductionAdmissionAsync(command.CorrelationId,
                    outcome.AttemptId ?? Guid.Empty, capture.RuntimeEpoch, capture.Generation,
                    reason, deadline).ConfigureAwait(false);
                return result.Committed;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
        }
        return false;
    }

    private void ObserveProductionAdmissionStateLocked(StationStateSnapshot next)
    {
        if (!_productionAdmissionEnabled) return;
        var hash = ComputeAdmissionStateHash(next);
        if (!string.Equals(hash, _admissionStateHash, StringComparison.Ordinal))
        {
            _admissionStateHash = hash;
            _admissionGeneration = checked(_admissionGeneration + 1);
        }
    }

    private ProductionAdmissionReport? PublishUnavailableProductionAdmissionLocked(string reason,
        bool writerInvalidation = false)
    {
        if (_disposed || _shutdownRequested) return _snapshot.ProductionAdmission;
        _productionAdmissionFactsFailure = reason;
        var previous = _lastAdmissionFacts ?? CaptureDefaultFactsLocked();
        var gates = ProductionAdmissionReport.RequiredGates
            .Where(gate => !ProductionAdmissionEngine.IsQualificationGate(gate))
            .Select(gate => new ProductionAdmissionGateResult(gate,
                ProductionAdmissionGateStatus.Blocked, reason)).ToArray();
        _lastAdmissionFacts = new ProductionAdmissionFacts(previous.Configuration,
            previous.Qualifications, gates, previous.DurableHeads);
        var report = ProductionAdmissionEngine.Evaluate(_snapshot.RuntimeEpoch,
            checked(_snapshot.Revision + 1), _admissionGeneration, DateTimeOffset.UtcNow, _lastAdmissionFacts);
        if (writerInvalidation)
        {
            // The pre-commit observer performs only immutable projection and bounded
            // channel writes. Do not enter runtime/device/session projections here.
            var revision = checked(_snapshot.Revision + 1);
            var alarms = _snapshot.AlarmState is { } current
                ? new AlarmStateSnapshot(current.Available, current.ReasonCode, _snapshot.RuntimeEpoch,
                    revision, current.Policy, current.Instances, current.Plc) : null;
            _snapshot = _snapshot with { Revision = revision, ObservedAtUtc = DateTimeOffset.UtcNow,
                Ready = false, ArmState = ProductionArmState.Disarmed, ProductionAdmission = report,
                AlarmState = alarms };
            _admissionStateHash = ComputeAdmissionStateHash(_snapshot);
            if (_productionInspectionOwner is { Current: null, Observer: { } observer })
                observer.RejectPendingAdmission("ProductionTriggerPermitRevoked");
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(_snapshot);
            return report;
        }
        PublishLocked(_snapshot with { Ready = false, ArmState = ProductionArmState.Disarmed,
            ProductionAdmission = report });
        return _snapshot.ProductionAdmission;
    }

    private ProductionAdmissionReport RebindProductionAdmissionReport(
        ProductionAdmissionReport report, Guid runtimeEpoch, long revision, long generation)
    {
        // Re-evaluate qualification expiry while preserving the captured evidence
        // when only the snapshot revision advances.
        if (_lastAdmissionFacts is { } facts)
            return ProductionAdmissionEngine.Evaluate(runtimeEpoch, revision, generation,
                DateTimeOffset.UtcNow, facts);
        return report;
    }

    private ProductionAdmissionFacts CaptureDefaultFactsLocked(StationStateSnapshot? observedState = null,
        IReadOnlyDictionary<string, string>? durableHeads = null, bool captureSucceeded = false,
        ProductionQualificationInputs? qualifications = null)
    {
        // A production cycle deliberately projects Busy/CurrentExecution while it
        // owns the physical resources.  Those fields describe the live operation,
        // not a new admission blocker.  Normalize only this observation copy; the
        // published snapshot remains the authoritative Busy/Ack projection.
        var state = NormalizeProductionAdmissionObservationLocked(observedState ?? _snapshot);
        var ownedProductionProgress = IsOwnedProductionProgressLocked(observedState ?? _snapshot);
        var gates = ProductionAdmissionReport.RequiredGates
            .Where(gate => !ProductionAdmissionEngine.IsQualificationGate(gate))
            .Select(gate => gate switch
            {
                ProductionAdmissionGate.ActiveRecipe => state.ActiveRecipe is { } recipe
                    ? new ProductionAdmissionGateResult(gate, ProductionAdmissionGateStatus.Passed,
                        "ActiveRecipeObserved", observedFingerprint: recipe.ContentHash)
                    : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                        "ActiveRecipeMissing"),
                ProductionAdmissionGate.PreparedAlgorithm => PreparedAlgorithmGate(gate, state),
                ProductionAdmissionGate.RecipeAssets => RecipeAssetsGate(gate, state),
                ProductionAdmissionGate.CameraBinding => state.CameraSetup is { BindingRevision: > 0 } camera
                    ? new ProductionAdmissionGateResult(gate, ProductionAdmissionGateStatus.Passed,
                        "CameraBindingObserved", observedFingerprint: ProductionAdmissionCanonical.Hash(
                            "camera-binding-revision-v1", camera.BindingRevision.ToString(CultureInfo.InvariantCulture)))
                    : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                        "CameraBindingMissing"),
                ProductionAdmissionGate.CameraConfiguration => state.CameraSetup?.Configuration switch
                {
                    CameraConfigurationState.Applied => RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                        "CameraConfigurationApplied"),
                    CameraConfigurationState.Applying => RuntimeGate(gate, ProductionAdmissionGateStatus.Blocked,
                        "CameraConfigurationApplying"),
                    _ => RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                        "CameraConfigurationNotVerified")
                },
                ProductionAdmissionGate.CameraHealth => state.CameraSetup is { } setup &&
                    setup.ProviderAvailability == CameraProviderAvailability.Available &&
                    setup.Connection == CameraConnectionState.Open &&
                    setup.Configuration == CameraConfigurationState.Applied &&
                    state.Camera.Connection == HealthState.Healthy &&
                    state.Camera.Configuration == HealthState.Healthy
                    ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed, "CameraHealthAvailable")
                    : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                        "CameraHealthUnavailable"),
                ProductionAdmissionGate.PlcCommunication => state.PlcCommunication is { Healthy: true }
                    ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed, "PlcCommunicationHealthy")
                    : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                        state.PlcCommunication?.ReasonCode ?? "PlcCommunicationHealthUnavailable"),
                ProductionAdmissionGate.ControllerSynchronization => HealthGate(gate, state.Plc.Synchronization,
                    "ControllerSynchronizationHealthy", "ControllerSynchronizationUnavailable"),
                ProductionAdmissionGate.Recovery => state.Recovery switch
                {
                    RecoveryState.None => RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                        "StartupRecoveryVerified"),
                    RecoveryState.InProgress => RuntimeGate(gate, ProductionAdmissionGateStatus.Blocked,
                        "StartupRecoveryInProgress"),
                    _ => RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                        "StartupRecoveryNotVerified")
                },
                ProductionAdmissionGate.ExclusiveWork => state.Mode == ExclusiveMode.None && !state.Busy
                    ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed, "ExclusiveWorkClear")
                    : RuntimeGate(gate, ProductionAdmissionGateStatus.Blocked,
                        "ExclusiveWorkInProgress"),
                ProductionAdmissionGate.Alarms => state.AlarmState is { Available: true } &&
                    !state.Alarms.BlocksProduction
                    ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                        "ProductionAlarmsClear")
                    : state.Alarms.BlocksProduction
                        ? RuntimeGate(gate, ProductionAdmissionGateStatus.Blocked,
                            "ProductionAlarmBlocking")
                        : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                            "AlarmAuthorityUnavailable"),
                ProductionAdmissionGate.StoreIntegrity => StoreIntegrityGate(gate),
                ProductionAdmissionGate.EvidenceReconciliation => EvidenceGate(gate, state),
                ProductionAdmissionGate.Backlog => BacklogGate(gate, ownedProductionProgress),
                ProductionAdmissionGate.ProductionCycle => ProductionCycleGate(gate, state),
                ProductionAdmissionGate.DeploymentPolicies or ProductionAdmissionGate.VersionPolicy or
                    ProductionAdmissionGate.StoreCapacity or ProductionAdmissionGate.IdentityRecovery =>
                    ProductionDeploymentGate(gate),
                _ => RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                    ProductionAdmissionEngine.MissingReason(gate))
            }).ToArray();
        if (!captureSucceeded && _productionAdmissionFactsFailure is { } failure)
            gates = gates.Select(gate => new ProductionAdmissionGateResult(gate.Gate,
                ProductionAdmissionGateStatus.Blocked, failure)).ToArray();
        // PublishLocked re-captures the built-in gates on every snapshot. Preserve
        // the last verified qualification target while the internal provider is
        // installed; otherwise a heartbeat would silently replace signed evidence
        // with the conservative empty target and disarm an otherwise stable arm.
        var effectiveConfiguration = _productionInspectionQualificationEvidenceProvider is not null
            ? _lastAdmissionFacts?.Configuration
            : null;
        var effectiveQualifications = qualifications ??
            (_productionInspectionQualificationEvidenceProvider is not null
                ? _lastAdmissionFacts?.Qualifications : null) ?? ProductionQualificationInputs.Unconfigured;
        return new ProductionAdmissionFacts(effectiveConfiguration ??
            new(new Dictionary<ProductionConfigurationBinding, string>()),
            effectiveQualifications, gates,
            durableHeads ?? _lastAdmissionFacts?.DurableHeads ?? new Dictionary<string, string>());

        ProductionAdmissionGateResult StoreIntegrityGate(ProductionAdmissionGate gate)
        {
            return _audit?.Integrity?.State switch
            {
                AuditIntegrityState.Verified => RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                    "TraceAuditVerified"),
                AuditIntegrityState.Verifying => RuntimeGate(gate, ProductionAdmissionGateStatus.Blocked,
                    "TraceAuditVerificationPending"),
                AuditIntegrityState.Faulted => RuntimeGate(gate, ProductionAdmissionGateStatus.Failed,
                    "TraceAuditUnavailable"),
                _ => RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                    "TraceStoreUnavailable")
            };
        }

        static ProductionAdmissionGateResult HealthGate(ProductionAdmissionGate gate,
            HealthState health, string healthyReason, string unavailableReason) => health switch
            {
                HealthState.Healthy => RuntimeGate(gate, ProductionAdmissionGateStatus.Passed, healthyReason),
                HealthState.Faulted => RuntimeGate(gate, ProductionAdmissionGateStatus.Failed, unavailableReason),
                _ => RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured, unavailableReason)
            };

        static ProductionAdmissionGateResult RuntimeGate(ProductionAdmissionGate gate,
            ProductionAdmissionGateStatus status, string reason, string? observedFingerprint = null) =>
            new(gate, status, reason, observedFingerprint: observedFingerprint);

        ProductionAdmissionGateResult PreparedAlgorithmGate(ProductionAdmissionGate gate,
            StationStateSnapshot current)
        {
            var activation = _activeActivation;
            var content = activation?.Snapshot.Release.Source.Content;
            var prepared = activation?.Algorithm;
            var exact = activation is { Snapshot.ProductionAuthority: true,
                    Snapshot: { } snapshot } &&
                current.ActiveRecipe is { } activeRecipe &&
                activeRecipe == snapshot.Recipe &&
                prepared is { IsRetired: false } algorithm &&
                algorithm.InstanceId == snapshot.PreparedAlgorithmInstanceId &&
                algorithm.Descriptor.Identity == content?.Algorithm.Algorithm &&
                algorithm.Descriptor.ConfigurationSchema.Id == content?.Algorithm.ConfigurationSchema.Id &&
                algorithm.Descriptor.ConfigurationSchema.Version == content?.Algorithm.ConfigurationSchema.Version &&
                algorithm.Descriptor.ConfigurationSchema.ContentHash == content?.Algorithm.ConfigurationSchema.ContentHash &&
                algorithm.Descriptor.ResultSchema.Id == content?.Algorithm.ResultSchema.Id &&
                algorithm.Descriptor.ResultSchema.Version == content?.Algorithm.ResultSchema.Version &&
                algorithm.Descriptor.ResultSchema.ContentHash == content?.Algorithm.ResultSchema.ContentHash &&
                algorithm.Configuration.ContentHash == content?.Configuration.ContentHash;
            return exact
                ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                    "PreparedAlgorithmExact", ProductionAdmissionCanonical.Hash(
                        "production-prepared-algorithm-v1", activation!.Snapshot.ContentHash,
                        prepared!.InstanceId.ToString("D"), prepared.Descriptor.Identity.Id,
                        prepared.Descriptor.Identity.Version, prepared.Configuration.ContentHash))
                : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                    "PreparedAlgorithmExactUnavailable");
        }

        ProductionAdmissionGateResult RecipeAssetsGate(ProductionAdmissionGate gate,
            StationStateSnapshot current)
        {
            var activation = _activeActivation;
            var content = activation?.Snapshot.Release.Source.Content;
            var exact = activation is { Snapshot.ProductionAuthority: true,
                    Snapshot: { } snapshot } && current.ActiveRecipe == snapshot.Recipe &&
                content is not null &&
                content.AssetRequirements.Count == 0 && content.CameraProviderExtension is null &&
                content.CalibrationRequirements.Count == 0;
            return exact
                ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                    "RecipeAssetsExplicitlyEmpty", ProductionAdmissionCanonical.Hash(
                        "production-recipe-assets-v1", activation!.Snapshot.Release.ContentHash,
                        content!.ContentHash))
                : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                    "RecipeAssetsVerificationUnavailable");
        }

        ProductionAdmissionGateResult ProductionCycleGate(ProductionAdmissionGate gate,
            StationStateSnapshot current)
        {
            var options = _productionInspectionOptions;
            var storeOptions = _productionInspectionStoreOptions;
            var activation = _activeActivation;
            var content = activation?.Snapshot.Release.Source.Content;
            var policy = _productionInspectionPolicy;
            var owner = _productionInspectionOwner;
            var ownerReady = owner is { Aborted: false } &&
                !owner.Cancellation.IsCancellationRequested;
            var exact = options is { EvidenceRequirement: ProductionEvidenceRequirement.None } &&
                storeOptions is { ProductionInspections: not null } &&
                _audit is SqliteCommandStore { ProductionInspectionEnabled: true } &&
                _productionInspectionClock is not null && _frameBufferPool is not null &&
                ownerReady && _productionInspectionStartupVerified &&
                !_productionInspectionRecoveryBlocked && activation is { Snapshot.ProductionAuthority: true,
                    Snapshot: { } snapshot, Algorithm.IsRetired: false } && current.ActiveRecipe == snapshot.Recipe &&
                ProductionPartIdentityReadyLocked(content?.PartIdentityRequirement) &&
                Images.ProductionImageEvidenceBinding.IsAvailable(options, storeOptions, content) &&
                Outbox.ProductionOutboxBinding.RoutesMatch(storeOptions, policy) && ProductionOutboxConfiguredLocked();
            return exact
                ? RuntimeGate(gate, ProductionAdmissionGateStatus.Passed, "ProductionCycleConfigured",
                    content!.PartIdentityRequirement!.Mode == PartIdentityRequirementMode.None ?
                    ProductionAdmissionCanonical.Hash("production-cycle-v1", options!.ContentHash,
                        storeOptions!.ProductionInspections!.BindingHash, activation!.Snapshot.ContentHash,
                        policy!.ContentHash, owner!.RuntimeEpoch.ToString("D")) :
                    ProductionAdmissionCanonical.Hash("production-cycle-v2", options!.ContentHash,
                        storeOptions!.ProductionInspections!.BindingHash, storeOptions.PartIdentities!.BindingHash,
                        activation!.Snapshot.ContentHash, policy!.ContentHash, owner!.RuntimeEpoch.ToString("D"),
                        _productionDeploymentObservation!.PartIdentity!.MaterialHash))
                : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                    "ProductionCycleUnavailable");
        }

        ProductionAdmissionGateResult EvidenceGate(ProductionAdmissionGate gate,
            StationStateSnapshot current)
        {
            if (_productionInspectionOptions?.ImageStage is not null && ProductionImageBacklogExceededLocked())
                return RuntimeGate(gate, ProductionAdmissionGateStatus.Failed,
                    "ProductionImageBacklogLimitExceeded");
            var noPending = ProductionImageBacklogReadyLocked(current) &&
                current.Evidence.PendingDeliveries == 0;
            var routesReady = _productionInspectionOptions?.EvidenceRequirement ==
                    ProductionEvidenceRequirement.None &&
                Outbox.ProductionOutboxBinding.RoutesMatch(_productionInspectionStoreOptions, _productionInspectionPolicy) &&
                ProductionOutboxConfiguredLocked();
            if (_productionInspectionStartupVerified && !_productionInspectionRecoveryBlocked && noPending &&
                routesReady && current.Evidence.State != HealthState.Faulted)
                return RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                    "ProductionEvidenceLedgerVerified");
            return current.Evidence.State == HealthState.Faulted
                ? RuntimeGate(gate, ProductionAdmissionGateStatus.Failed,
                    "EvidenceReconciliationFailed")
                : RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                    "EvidenceReconciliationUnavailable");
        }

        ProductionAdmissionGateResult BacklogGate(ProductionAdmissionGate gate,
            bool ownedProgress)
        {
            if (ProductionOutboxBacklogFailureLocked() is { } outboxFailure)
                return RuntimeGate(gate, ProductionAdmissionGateStatus.Failed, outboxFailure);
            if (_productionInspectionStartupVerified && !_productionInspectionRecoveryBlocked &&
                (_productionInspectionOwner?.Current is null || ownedProgress))
                return RuntimeGate(gate, ProductionAdmissionGateStatus.Passed,
                    "ProductionBacklogLedgerVerified");
            return RuntimeGate(gate, ProductionAdmissionGateStatus.NotConfigured,
                "ProductionBacklogStateUnavailable");
        }
    }

    private string ComputeAdmissionStateHash(StationStateSnapshot state)
    {
        state = NormalizeProductionAdmissionObservationLocked(state);
        // Acquisition Unknown/Healthy describes the current frame attempt, not
        // the deployed camera configuration. A fault remains material. This also
        // keeps completion of a normal frame from revoking the next cycle's Arm.
        var cameraMaterial = _productionInspectionOptions is null ? state.Camera.ToString() :
            string.Join("|", state.Camera.Connection, state.Camera.Configuration, state.Camera.Buffers,
                state.Camera.Acquisition == HealthState.Faulted ? "AcquisitionFaulted" : "AcquisitionProgress");
        var cameraSetupMaterial = _productionInspectionOptions is not null && state.CameraSetup is { } setup
            ? (setup with { Acquisition = CameraAcquisitionState.Stopped }).ToString()
            : state.CameraSetup?.ToString();
        var evidenceMaterial = _productionInspectionOptions?.ImageStage is null ? state.Evidence.ToString() :
            string.Join("|", state.Evidence.State, state.Evidence.PendingDeliveries,
                ProductionImageBacklogReadyLocked(state) ? "ImageBacklogWithinLimit" : "ImageBacklogBlocked");
        var fields = new List<string?>
        {
            "production-admission-runtime-state-v1", state.RuntimeEpoch.ToString("D"),
            state.Lifecycle.ToString(), state.Mode.ToString(), state.Busy.ToString(),
            state.Handshake.ToString(), state.Recovery.ToString(), state.CurrentExecution?.ToString(),
            state.ActiveRecipe?.ToString(), cameraMaterial, state.Plc.ToString(),
            state.Store.ToString(), evidenceMaterial, state.Qualification.ToString(),
            state.Performance.ToString(), OutboxAdmissionAlarmSummaryLocked(state).ToString(), cameraSetupMaterial,
            state.CameraRecovery is { } recovery
                ? string.Join("|", recovery.State, recovery.AttemptCount, recovery.MaximumAttempts,
                    recovery.SourceHealthy, recovery.ReasonCode, recovery.Health?.ToString()) : null,
            state.CalibrationSession is { } calibration
                ? string.Join("|", calibration.Phase, calibration.Outcome, calibration.FrameCount,
                    calibration.ObservationCount, calibration.ExcludedFrameCount, calibration.CandidateId,
                    calibration.ReasonCode, calibration.RestorationVerified, calibration.OperationInProgress) : null
        };
        // This blocker is a short-lived projection of the audit monitor's
        // Verifying watermark, so it must not count as a material admission
        // change while the just-committed Arm transaction is being checked.
        fields.AddRange(state.AdmissionBlockers
            .Where(value => !string.Equals(value, "AuditIntegrityUnavailable", StringComparison.Ordinal))
            .OrderBy(value => value, StringComparer.Ordinal));
        if (_lastAdmissionFacts is { } facts)
            fields.Add(facts.MaterialObservationHash(MaterialStoreIntegrityGate));
        if (state.ProductionAdmission is { } admission)
            fields.Add(MaterialAdmissionEvidenceHash(admission));
        // Audit monitor state is an asynchronous projection of the same durable
        // commit.  Including its transient Verifying/Verified watermark here would
        // invalidate Arm's own post-commit generation fence.  The Arm path waits
        // for that watermark separately and the report carries the store gate.
        return ProductionAdmissionCanonical.Hash("production-admission-runtime-state-v1", fields.ToArray());
    }

    /// <summary>
    /// Returns the state used for admission-material observation while an already
    /// accepted production cycle owns the physical resources.  This deliberately
    /// does not mutate or publish the station snapshot: Busy/Ack remains visible
    /// to Runtime consumers, but it cannot invalidate the same cycle's admission.
    /// </summary>
    internal StationStateSnapshot NormalizeProductionAdmissionObservationLocked(
        StationStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsOwnedProductionProgressLocked(state)) return state;
        return state with { Busy = false, CurrentExecution = null, Handshake = HandshakePhase.Idle };
    }

    private readonly record struct AdmissionCapture(Guid RuntimeEpoch, long Revision,
        long Generation, string StateHash);

    private sealed class CurrentStationFactsSource : IProductionAdmissionFactsSource
    {
        private readonly StationRuntime _owner;
        internal CurrentStationFactsSource(StationRuntime owner) => _owner = owner;
        public async ValueTask<ProductionAdmissionFacts> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Capture verified semantic heads on a separate read transaction. No
            // SQLite or signature work is performed while holding the runtime lock.
            var heads = _owner._audit is SqliteCommandStore { ProductionAdmissionEnabled: true } store
                ? await store.ReadProductionAdmissionDurableHeadsAsync(cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, string>();
            ProductionAdmissionFacts runtimeFacts;
            StationStateSnapshot state;
            RecipeActivationSnapshot? activation;
            IProductionInspectionQualificationEvidenceProvider? provider;
            lock (_owner._sync)
            {
                if (_owner._disposed || _owner._shutdownRequested)
                    throw new InvalidOperationException("ProductionAdmissionRuntimeStopped");
                state = _owner._snapshot;
                activation = _owner._activeActivation?.Snapshot;
                provider = _owner._productionInspectionQualificationEvidenceProvider;
                runtimeFacts = _owner.CaptureDefaultFactsLocked(state, heads, captureSucceeded: true);
            }

            var deployment = await _owner.CaptureProductionDeploymentAsync(activation, cancellationToken).ConfigureAwait(false);
            var configuration = deployment?.Configuration ?? runtimeFacts.Configuration;
            var evidence = provider is null ? ProductionQualificationInputs.Unconfigured :
                await provider.CaptureAsync(configuration, state, activation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (evidence is null)
                throw new InvalidOperationException("ProductionQualificationEvidenceUnavailable");
            lock (_owner._sync)
            {
                _owner._productionDeploymentObservation = deployment;
                runtimeFacts = _owner.CaptureDefaultFactsLocked(state, heads, captureSucceeded: true);
            }
            return new ProductionAdmissionFacts(configuration, evidence,
                runtimeFacts.RuntimeGates.Values.ToArray(), runtimeFacts.DurableHeads);
        }
    }
}

/// <summary>
/// Internal qualification-only evidence seam for the production admission path.
/// Implementations may provide signed qualification inputs, but they cannot
/// provide any of the Runtime gates or durable-head observations.
/// </summary>
internal interface IProductionInspectionQualificationEvidenceProvider
{
    ValueTask<ProductionQualificationInputs> CaptureAsync(ProductionConfiguration observedConfiguration, StationStateSnapshot state,
        RecipeActivationSnapshot? activation, CancellationToken cancellationToken);
}
