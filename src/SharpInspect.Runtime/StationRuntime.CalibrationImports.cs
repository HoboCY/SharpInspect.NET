using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private CalibrationTransferPackageStore? _calibrationTransfers;
    private CalibrationImportRevalidator? _calibrationImportRevalidator;
    private ImportedCalibrationPhysicalVerificationRegistry? _importPhysicalVerifiers;
    private int _calibrationImportCommands;
    private int _calibrationImportQueries;
    private int _calibrationTransferCodecWork;
    private ImportPhysicalReservation? _importPhysicalReservation;

    internal void ConfigureCalibrationImports(ProductionStoreOptions options,
        ImportedCalibrationPhysicalVerificationRegistry? verifiers)
    {
        if (options.CalibrationImports is not { } imports) return;
        lock (_sync)
        {
            if (_calibrationTransfers is not null) throw new InvalidOperationException("CalibrationImportsAlreadyConfigured");
            _calibrationTransfers = new CalibrationTransferPackageStore(imports.Artifacts);
            _calibrationImportRevalidator = _calibrationProcedures is null ? null : new(_calibrationProcedures);
            _importPhysicalVerifiers = verifiers;
        }
    }

    private async ValueTask<RuntimeCommandOutcome> SubmitCalibrationImportAsync(CalibrationImportCommand command,
        CancellationToken cancellationToken)
    {
        var attempt = Guid.NewGuid();
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId, CommandDisposition.Rejected,
            reason, AuditPersistence.NotAttempted, attempt);
        if (_authorization is null || _audit is not SqliteCommandStore store || _calibrationTransfers is null)
            return Unavailable("CalibrationImportUnavailable");
        if (Interlocked.Increment(ref _calibrationImportCommands) > 2)
        {
            Interlocked.Decrement(ref _calibrationImportCommands);
            return Unavailable("CalibrationImportCapacityExceeded");
        }
        var entered = false;
        var physicalStarted = false;
        RecipeActivationCameraLease? cameraLease = null;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        var token = bounded.Token;
        try
        {
            await _storeInitialization.WaitAsync(token).ConfigureAwait(false);
            string? rejection;
            lock (_sync) rejection = CalibrationImportBlockerLocked(command);
            var preparation = rejection is null
                ? await PrepareCalibrationImportAsync(command, store, token, () => physicalStarted = true).ConfigureAwait(false)
                : new CalibrationImportPreparation(Failure: rejection);

            // Reacquire the camera configuration for the short commit interval. Local image
            // recomputation needs no device lease; physical verification owns its actual lease
            // until the project verifier exits. Neither runs under the station command gate.
            if (preparation.Failure is null && command is not ImportCalibrationPackageCommand)
            {
                var role = preparation.Camera?.LogicalRole;
                if (role is null) preparation = preparation with { Failure = "CalibrationImportCameraUnavailable" };
                else
                {
                    cameraLease = await _cameraSetupRuntime.ReserveRecipeActivationAsync(role, token).ConfigureAwait(false);
                    preparation = preparation with { Camera = cameraLease.CurrentSnapshot,
                        Failure = cameraLease.Available ? null : cameraLease.ReasonCode };
                }
            }
            var deadline = new StoreDeadline(_audit.CommitTimeout);
            entered = await _commandGate.WaitAsync(PositiveRemaining(deadline), token).ConfigureAwait(false);
            if (!entered) return Unavailable("CalibrationImportCommitBusy");
            Guid epoch;
            lock (_sync)
            {
                rejection = CalibrationImportBlockerLocked(command, cameraLease?.Available == true);
                epoch = _snapshot.RuntimeEpoch;
            }
            var outcome = await _authorization.HandleCalibrationImportCommandAsync(command, epoch, attempt,
                rejection, preparation, deadline, token).ConfigureAwait(false);
            if (outcome.Audit == AuditPersistence.Unavailable) MarkAuditFault("CalibrationImportAuditUnavailable");
            if (outcome.Disposition == CommandDisposition.Accepted)
                lock (_sync) PublishLocked(_snapshot with
                {
                    LastCommand = new(command.CorrelationId, OperationState.Completed, outcome.ReasonCode)
                });
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (physicalStarted)
                await PersistCalibrationImportInterruptionAsync(command, attempt, "CalibrationImportCancelled", entered).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            return physicalStarted
                ? await PersistCalibrationImportInterruptionAsync(command, attempt, "CalibrationImportDeadlineExceeded", entered).ConfigureAwait(false)
                : Unavailable("CalibrationImportDeadlineExceeded");
        }
        catch (TimeoutException)
        {
            return physicalStarted
                ? await PersistCalibrationImportInterruptionAsync(command, attempt, "CalibrationImportDeadlineExceeded", entered).ConfigureAwait(false)
                : Unavailable("CalibrationImportDeadlineExceeded");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return Unavailable("CalibrationImportUnavailable"); }
        finally
        {
            if (entered) _commandGate.Release();
            if (cameraLease is not null) await cameraLease.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _calibrationImportCommands);
        }
    }

    private string? CalibrationImportBlockerLocked(CalibrationImportCommand command, bool cameraReserved = false)
    {
        if (StationQualificationConfigurationBlockedLocked) return "StationQualificationSessionInProgress";
        if (_disposed || _shutdownRequested) return "RuntimeStopped";
        if (!_storeReady || _auditFault) return "CalibrationImportAuditUnavailable";
        if (Volatile.Read(ref _pendingLocalStops) != 0) return "CalibrationImportLocalStopInProgress";
        if (command is ImportCalibrationPackageCommand) return null;
        ReconcileSessionLocked();
        if (_snapshot.Session is not { State: InteractiveSessionState.Authenticated } session ||
            session.SessionId != command.Invocation.SessionId || session.PrincipalId != command.Invocation.PrincipalId)
            return "CalibrationImportSessionChanged";
        if (_importPhysicalReservation is not null) return "CalibrationImportPhysicalVerificationInProgress";
        if (RecipeActivationConfigurationBlockedLocked) return "RecipeActivationInProgress";
        if (PreviewConfigurationBlockedLocked) return "PreviewSessionInProgress";
        if (ManualInspectionConfigurationBlockedLocked) return "ManualInspectionSessionInProgress";
        if (_snapshot.Ready || _snapshot.ArmState != ProductionArmState.Disarmed || _snapshot.Busy ||
            _snapshot.CurrentExecution is not null || _executionGuard.IsHung ||
            _snapshot.Evidence.PendingDeliveries != 0 || _snapshot.Evidence.PendingRequiredImages != 0 ||
            _snapshot.Mode != ExclusiveMode.None || _snapshot.Recovery == RecoveryState.InProgress ||
            _snapshot.Handshake is HandshakePhase.AwaitingResultAck or HandshakePhase.AwaitingAckReset ||
            _snapshot.LastCommand?.State == OperationState.Pending || _cameraNetworkMaintenanceActive ||
            _calibrationAdmissionInProgress || _calibrationWork is { IsCompleted: false } ||
            (!cameraReserved && _cameraSetupRuntime.ConfigurationMutationInProgress))
            return "CalibrationImportStationNotIdle";
        return null;
    }

    private async Task<CalibrationImportPreparation> PrepareCalibrationImportAsync(CalibrationImportCommand command,
        SqliteCommandStore store, CancellationToken token, Action physicalAdmission)
    {
        try
        {
            var access = await _authorization!.CheckCalibrationImportAuthorizationAsync(command, token).ConfigureAwait(false);
            if (access is not null) return new(Failure: access);
            if (command is ImportCalibrationPackageCommand import)
            {
                var package = await RunCalibrationTransferCodecAsync(() => CalibrationExportPackageCodec.Decode(import.Package), token)
                    .ConfigureAwait(false);
                var artifact = await _calibrationTransfers!.PreserveAsync(import.Package, token).ConfigureAwait(false);
                return new(Package: package, Artifact: artifact);
            }
            var query = await store.ReadCalibrationImportStateAsync(command, token).ConfigureAwait(false);
            if (!query.Available || query.State is not { } state) return new(Failure: query.ReasonCode);
            var candidateRef = CalibrationImportProjection.CommandCandidate(command);
            var candidate = state.Records.OfType<ImportedCalibrationCandidate>()
                .SingleOrDefault(value => value.Reference == candidateRef);
            if (candidate is null) return new(Failure: "CalibrationImportCandidateUnavailable");
            var bytes = await _calibrationTransfers!.ReadAsync(candidate.PackageHash, candidate.PackageLength, token).ConfigureAwait(false);
            var contents = await RunCalibrationTransferCodecAsync(() => CalibrationExportPackageCodec.Decode(bytes), token)
                .ConfigureAwait(false);
            if (state.Binding is null || state.Imaging is null) return new(Failure: "CalibrationImportLocalSetupUnavailable");
            CameraSetupSnapshot? camera;
            await using (var lease = await _cameraSetupRuntime.ReserveRecipeActivationAsync(state.Binding.LogicalRole, token)
                .ConfigureAwait(false))
            {
                if (!lease.Available) return new(Failure: lease.ReasonCode);
                camera = lease.CurrentSnapshot;
            }
            if (camera is null) return new(Failure: "CalibrationImportCameraUnavailable");
            var preparation = new CalibrationImportPreparation(Package: contents, VerifiedCandidate: candidate.Reference, Camera: camera);
            if (command is RevalidateImportedCalibrationCommand revalidate)
            {
                if (_calibrationImportRevalidator is null) return preparation with { Failure = "CalibrationImportProcedureUnavailable" };
                var policy = CalibrationImportProjection.CurrentPolicy(state.Governance, revalidate.Requirement.AcceptancePolicy,
                    _authorization.CalibrationGovernanceUtcNow);
                if (policy is null) return preparation with { Failure = "CalibrationImportLocalPolicyHeadChanged" };
                return preparation with { Recomputation = await _calibrationImportRevalidator.RecomputeAsync(contents, candidate,
                    revalidate.Requirement, policy, camera, revalidate.ImagingSetup, TimeSpan.FromSeconds(25), token).ConfigureAwait(false) };
            }
            if (command is VerifyImportedCalibrationCommand verify)
            {
                var evaluation = state.Records.OfType<ImportedCalibrationEvaluation>().LastOrDefault(value => value.Candidate == candidate.Reference);
                if (evaluation is null || evaluation.Reference != verify.Evaluation || !evaluation.Passed)
                    return preparation with { Failure = "CalibrationImportEvaluationUnavailable" };
                var policy = CalibrationImportProjection.CurrentPolicy(state.Governance,
                    evaluation.Content.Requirement.AcceptancePolicy, _authorization.CalibrationGovernanceUtcNow);
                if (policy is null) return preparation with { Failure = "CalibrationImportLocalPolicyHeadChanged" };
                if (policy.PhysicalVerification.Applicability != CalibrationPolicyApplicability.Required)
                    return preparation with { Failure = "CalibrationImportPhysicalVerificationNotApplicable" };
                var bindingFailure = CalibrationGovernanceProjection.PhysicalBindingFailures(
                    policy.PhysicalVerification, verify.Submission).FirstOrDefault();
                if (bindingFailure is not null) return preparation with { Failure = bindingFailure };
                if (_importPhysicalVerifiers is null) return preparation with { Failure = "CalibrationImportPhysicalProcedureUnavailable" };
                var physicalLease = await _cameraSetupRuntime.ReserveRecipeActivationAsync(camera.LogicalRole, token).ConfigureAwait(false);
                if (!physicalLease.Available || physicalLease.CurrentSnapshot is not { } physicalCamera)
                {
                    await physicalLease.DisposeAsync().ConfigureAwait(false);
                    return preparation with { Failure = physicalLease.ReasonCode };
                }
                ImportPhysicalReservation? reservation = null;
                string? blocked;
                Guid epoch;
                lock (_sync)
                {
                    epoch = _snapshot.RuntimeEpoch;
                    blocked = CalibrationImportBlockerLocked(command, cameraReserved: true);
                    if (blocked is null)
                    {
                        _importPhysicalReservation = reservation = new ImportPhysicalReservation(this, physicalLease, token);
                        physicalAdmission();
                    }
                }
                if (reservation is null)
                {
                    await physicalLease.DisposeAsync().ConfigureAwait(false);
                    return preparation with { Failure = blocked ?? "CalibrationImportPhysicalReservationUnavailable" };
                }
                // Registry owns the actual reservation from here, including cancellation,
                // timeout, callback failure and late retirement. The caller never releases it early.
                var physical = await _importPhysicalVerifiers.EvaluateAsync(
                    verify.CorrelationId, epoch, physicalCamera, ImagingSetupRevisionReference.FromRevision(state.Imaging),
                    reservation, () => _authorization.CalibrationGovernanceUtcNow, candidate, evaluation,
                    verify.Submission, TimeSpan.FromSeconds(25), reservation.Token).ConfigureAwait(false);
                return preparation with { PhysicalVerification = physical,
                    Failure = physical.Witness is null ? physical.Failure : null };
            }
            return preparation;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(Failure: "CalibrationImportEvidenceUnavailable"); }
    }

    private async Task<T> RunCalibrationTransferCodecAsync<T>(Func<T> action, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _calibrationTransferCodecWork, 1, 0) != 0)
            throw new InvalidOperationException("CalibrationTransferCodecBusy");
        var actual = Task.Run(() =>
        {
            try { token.ThrowIfCancellationRequested(); return action(); }
            finally { Interlocked.Exchange(ref _calibrationTransferCodecWork, 0); }
        }, CancellationToken.None);
        _ = actual.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await actual.WaitAsync(token).ConfigureAwait(false);
    }

    public ValueTask<CalibrationImportResult> ImportAsync(ImportCalibrationPackageCommand command,
        CancellationToken cancellationToken = default) => SubmitImportResultAsync(command, cancellationToken);
    public ValueTask<CalibrationImportResult> RevalidateImportAsync(RevalidateImportedCalibrationCommand command,
        CancellationToken cancellationToken = default) => SubmitImportResultAsync(command, cancellationToken);
    public ValueTask<CalibrationImportResult> VerifyImportAsync(VerifyImportedCalibrationCommand command,
        CancellationToken cancellationToken = default) => SubmitImportResultAsync(command, cancellationToken);
    public ValueTask<CalibrationImportResult> PublishImportAsync(PublishImportedCalibrationCommand command,
        CancellationToken cancellationToken = default) => SubmitImportResultAsync(command, cancellationToken);

    private async ValueTask<CalibrationImportResult> SubmitImportResultAsync(CalibrationImportCommand command,
        CancellationToken cancellationToken)
    {
        var outcome = await SubmitAsync(command, cancellationToken).ConfigureAwait(false);
        if (outcome.Disposition != CommandDisposition.Accepted) return new(outcome);
        var query = await ReadImportOperationAsync(command.CorrelationId, command.Invocation, cancellationToken).ConfigureAwait(false);
        return new(outcome, query.Value);
    }

    private async Task<RuntimeCommandOutcome> PersistCalibrationImportInterruptionAsync(
        CalibrationImportCommand command, Guid attempt, string reason, bool alreadyHoldingGate)
    {
        var entered = false;
        try
        {
            var deadline = new StoreDeadline(_audit!.CommitTimeout);
            if (!alreadyHoldingGate)
            {
                entered = await _commandGate.WaitAsync(PositiveRemaining(deadline)).ConfigureAwait(false);
                if (!entered) return new(command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt);
            }
            Guid epoch;
            lock (_sync) epoch = _snapshot.RuntimeEpoch;
            return await _authorization!.HandleCalibrationImportCommandAsync(command, epoch, attempt, reason,
                new CalibrationImportPreparation(Failure: reason), deadline, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(command.CorrelationId, CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attempt); }
        finally { if (entered) _commandGate.Release(); }
    }

    private sealed class ImportPhysicalReservation : IAsyncDisposable
    {
        private readonly StationRuntime _owner;
        private readonly RecipeActivationCameraLease _camera;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource<bool> _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        internal ImportPhysicalReservation(StationRuntime owner, RecipeActivationCameraLease camera, CancellationToken caller)
        {
            _owner = owner;
            _camera = camera;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(caller, owner._lifetime.Token);
        }
        internal CancellationToken Token => _cancellation.Token;
        internal Task Retirement => _retired.Task;
        internal void Cancel()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await _camera.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                lock (_owner._sync)
                    if (ReferenceEquals(_owner._importPhysicalReservation, this)) _owner._importPhysicalReservation = null;
                _cancellation.Dispose();
                _retired.TrySetResult(true);
            }
        }
    }
}
