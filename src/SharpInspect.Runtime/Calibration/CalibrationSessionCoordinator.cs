using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Calibration;

/// <summary>One admitted session. Station serializes commands; this owner retains every actual asynchronous call.</summary>
internal sealed class CalibrationSessionCoordinator
{
    private readonly SqliteCommandStore _store;
    private readonly CameraRecoveryService _camera;
    private readonly CameraCalibrationBaselineSnapshot _baseline;
    private readonly IRegisteredCalibrationProcedure? _procedure;
    private readonly CalibrationFrameEvidenceStore _images;
    private readonly CalibrationSessionOptions _options;
    private readonly Action<CalibrationSessionEvidence> _publish;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<bool> _cancellationRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CameraCalibrationRestoreResult> _physicalRestoration =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CameraCalibrationLease? _lease;
    private CalibrationSessionEvidence _evidence;
    private int _cancelRequested;
    private CalibrationSessionCommand? _authorizationCommand;

    internal CalibrationSessionCoordinator(CalibrationSessionEvidence initial, SqliteCommandStore store,
        CameraRecoveryService camera, CameraCalibrationBaselineSnapshot baseline,
        IRegisteredCalibrationProcedure? procedure, CalibrationFrameEvidenceStore images,
        CalibrationSessionOptions options, Action<CalibrationSessionEvidence> publish)
    {
        _evidence = initial; _store = store; _camera = camera; _baseline = baseline;
        _procedure = procedure; _images = images; _options = options; _publish = publish;
    }
    internal CalibrationSessionEvidence Evidence => Volatile.Read(ref _evidence);
    internal CalibrationSessionHeader Header => Evidence.Header;
    internal void SetAuthorizationCommand(RuntimeCommand command) =>
        _authorizationCommand = command as CalibrationSessionCommand;
    internal bool RestorationVerified => Evidence.State.RestorationVerified && !_camera.IsCalibrationActive;
    internal bool CancellationRequested => Volatile.Read(ref _cancelRequested) != 0;
    internal Task<CameraCalibrationRestoreResult> PhysicalRestoration => _physicalRestoration.Task;
    internal void Cancel()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        _cancellationRequested.TrySetResult(true);
        RequestCancellation(_cancellation);
    }

    private void ThrowIfCancellationRequested()
    {
        if (CancellationRequested) throw new OperationCanceledException("CalibrationSessionCancelled", _cancellation.Token);
    }

    private static void RequestCancellation(CancellationTokenSource source)
    {
        // Consumer cancellation callbacks must not run while Station owns its command or snapshot lock.
        _ = Task.Run(() =>
        {
            try { source.Cancel(); }
            catch (AggregateException) { }
            catch (ObjectDisposedException) { }
        });
    }

    internal async Task StartAsync()
    {
        if (!_camera.TryHoldCalibrationAdmission(Header.SessionId, out var holdReason))
            throw new InvalidOperationException(holdReason);
        await RecordAsync(Header.Command.CorrelationId, CalibrationSessionPhase.Configuring,
            "CalibrationTemporaryConfigurationRequested").ConfigureAwait(false);
        ThrowIfCancellationRequested();
        var result = await _camera.BeginCalibrationConfigurationAsync(Header.SessionId,
            Header.Command.Plan.TemporaryConfiguration, _cancellation.Token).ConfigureAwait(false);
        _lease = result.Lease;
        if (!result.Succeeded) throw new InvalidOperationException(result.ReasonCode);
        if (CalibrationSessionContractHash.Configuration(_lease!.BaselineRequested) != Header.BaselineRequestedHash ||
            CalibrationSessionContractHash.Configuration(_lease.BaselineEffective) != Header.BaselineEffectiveHash ||
            _lease.BaselineTarget.ContentHash != Header.Binding.Target.ContentHash)
            throw new InvalidOperationException("CalibrationBaselineChangedBeforeConfiguration");
        await RecordAsync(Header.Command.CorrelationId, CalibrationSessionPhase.Configuring,
            "CalibrationTemporaryConfigurationApplied", temporaryConfiguration:
                new CalibrationTemporaryConfigurationEvidence(_lease.TemporaryRequested,
                    _lease.TemporaryEffective)).ConfigureAwait(false);
        await RecordAsync(Header.Command.CorrelationId, CalibrationSessionPhase.Collecting,
            "CalibrationSessionCollecting").ConfigureAwait(false);
    }

    internal async Task CaptureAsync(CaptureCalibrationFrameCommand command)
    {
        await RecordAsync(command.CorrelationId, CalibrationSessionPhase.Capturing,
            "CalibrationCaptureStarted").ConfigureAwait(false);
        ThrowIfCancellationRequested();
        if (_lease is null) throw new InvalidOperationException("CalibrationCameraLeaseMissing");
        var frameId = Guid.NewGuid();
        CalibrationFrameEvidence frame;
        var capture = await _lease.CaptureAsync(frameId, _cancellation.Token).ConfigureAwait(false);
        using (var outcome = capture.Outcome)
        {
            if (!capture.Accepted || outcome is not { Succeeded: true, Lease: { } source })
                throw new InvalidOperationException(outcome?.ReasonCode ?? capture.ReasonCode);
            frame = await _images.PreserveAsync(Header, frameId, source, _cancellation.Token).ConfigureAwait(false);
            // 原始相机租约只有在不可变 manifest 事务提交后才归还。
            await RecordAsync(command.CorrelationId, CalibrationSessionPhase.Capturing,
                "CalibrationFrameManifestCommitted", frame: frame).ConfigureAwait(false);
        }
        var image = await _images.ReadAsync(frame, _cancellation.Token).ConfigureAwait(false);
        using var borrowed = new CalibrationBorrowedFrame(image);
        var extracted = await InvokeProcedureAsync(command.CorrelationId, token =>
            _procedure!.ExtractAsync(Header.Command.Plan.Input, borrowed, Header.SessionId,
                frameId, frame.SourceHash, token)).ConfigureAwait(false);
        var observation = new CalibrationObservationEvidence(Guid.NewGuid(), frame,
            Header.Command.Plan.Procedure, Header.Command.Plan.Input.ContentHash, extracted);
        await RecordAsync(command.CorrelationId, CalibrationSessionPhase.Collecting,
            "CalibrationObservationRetained", observation: observation).ConfigureAwait(false);
    }

    internal Task ExcludeAsync(ExcludeCalibrationFrameCommand command)
    {
        ThrowIfCancellationRequested();
        return RecordAsync(command.CorrelationId, CalibrationSessionPhase.Collecting, "CalibrationFrameExcluded",
            exclusion: new CalibrationEvidenceExclusion(command.FrameId, Header.ActorPrincipalId,
                Header.InteractiveSessionId, command.Reason, DateTimeOffset.UtcNow));
    }

    internal async Task ComputeAsync(ComputeCalibrationCandidateCommand command)
    {
        ThrowIfCancellationRequested();
        var selected = Evidence;
        if (!selected.Selection.Sufficient) throw new InvalidOperationException(selected.Selection.ReasonCode);
        await RecordAsync(command.CorrelationId, CalibrationSessionPhase.Computing,
            "CalibrationComputationStarted").ConfigureAwait(false);
        var excluded = selected.Exclusions.Select(item => item.FrameId).ToHashSet();
        var loans = new List<CalibrationBorrowedFrame>();
        try
        {
            var inputs = new List<CalibrationObservationInput>();
            foreach (var observation in selected.Observations.Where(item => !excluded.Contains(item.Frame.FrameId)))
            {
                var image = await _images.ReadAsync(observation.Frame, _cancellation.Token).ConfigureAwait(false);
                var loan = new CalibrationBorrowedFrame(image);
                loans.Add(loan);
                inputs.Add(new CalibrationObservationInput(loan, observation.Result.Features,
                    Header.SessionId, observation.Frame.FrameId, observation.Frame.SourceHash,
                    observation.Result.Receipt));
            }
            var result = await InvokeProcedureAsync(command.CorrelationId,
                token => _procedure!.ComputeAsync(Header.Command.Plan.Input, inputs, token)).ConfigureAwait(false);
            ThrowIfCancellationRequested();
            if (result.Coefficients.Format != Header.Command.Plan.Requirement.CoefficientContract)
                throw new CalibrationProcedureException("CalibrationCoefficientContractMismatch");
            var candidate = new CalibrationCandidateEvidence(Guid.NewGuid(), Header.SessionId,
                Header.ContentHash, selected.Selection.SelectionHash, result, DateTimeOffset.UtcNow);
            await RecordAsync(command.CorrelationId, CalibrationSessionPhase.CandidateRetained,
                "CalibrationCandidateRetained", candidate: candidate).ConfigureAwait(false);
        }
        finally { foreach (var loan in loans) loan.Dispose(); }
    }

    private async Task<T> InvokeProcedureAsync<T>(Guid operationId, Func<CancellationToken, ValueTask<T>> invoke)
    {
        ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        // A consumer can block before returning its ValueTask, so invoke it off the command/UI thread.
        var actual = Task.Run(async () =>
        {
            ThrowIfCancellationRequested();
            return await invoke(deadline.Token).ConfigureAwait(false);
        }, CancellationToken.None);
        try
        {
            // Internal cancellation must not depend on a consumer's synchronous token callbacks.
            var winner = await Task.WhenAny(actual, _cancellationRequested.Task)
                .WaitAsync(_options.OperationTimeout).ConfigureAwait(false);
            ThrowIfCancellationRequested();
            if (winner != actual) throw new OperationCanceledException("CalibrationSessionCancelled");
            return await actual.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            var cancelled = CancellationRequested;
            RequestCancellation(deadline);
            try
            {
                await RestoreWhileProcedurePendingAsync(operationId, cancelled ?
                    "CalibrationProcedureCancellationPending" : ex is TimeoutException ?
                        "CalibrationProcedureDeadlineExceeded" : "CalibrationProcedureUnexpectedCancellation").ConfigureAwait(false);
            }
            finally
            {
                // 物理相机恢复由独立 owner 负责；这些 managed 副本和 durable session fence 必须
                // 覆盖不合作的 consumer task。
                try { await actual.ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OutOfMemoryException) { }
            }
            if (cancelled) throw new OperationCanceledException("CalibrationSessionCancelled", ex, _cancellation.Token);
            if (ex is TimeoutException) throw new TimeoutException("CalibrationProcedureDeadlineExceeded", ex);
            throw new InvalidOperationException("CalibrationProcedureUnexpectedCancellation", ex);
        }
    }

    private async Task RestoreWhileProcedurePendingAsync(Guid operationId, string reason)
    {
        Exception? journalFailure = null;
        try
        {
            await RecordAsync(operationId, CalibrationSessionPhase.Restoring,
                reason).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException) { journalFailure = failure; }
        var restored = await RestorePhysicalBaselineAsync().ConfigureAwait(false);
        if (journalFailure is not null)
            throw new InvalidOperationException("CalibrationRestorationAuditUnavailable", journalFailure);
        await RecordAsync(operationId, CalibrationSessionPhase.RecoveryBlocked,
            restored.Succeeded ? reason : restored.ReasonCode).ConfigureAwait(false);
    }

    private async Task<CameraCalibrationRestoreResult> RestorePhysicalBaselineAsync(bool afterRestart = false)
    {
        CameraCalibrationRestoreResult result;
        try
        {
            result = !afterRestart && _lease is not null
                ? await _lease.RestoreAsync(CancellationToken.None).ConfigureAwait(false)
                : await _camera.RestorePersistedCalibrationBaselineAsync(Header.SessionId, _baseline,
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { result = new CameraCalibrationRestoreResult(false, "CalibrationBaselineRestoreUnavailable"); }
        _physicalRestoration.TrySetResult(result);
        return result;
    }

    internal async Task RestoreAsync(Guid operationId, CalibrationSessionOutcome outcome, string reason,
        bool afterRestart = false)
    {
        if (RestorationVerified) return;
        if (Evidence.State.RestorationVerified)
        {
            ConfirmDurableClose();
            return;
        }
        if (!_camera.TryHoldCalibrationAdmission(Header.SessionId, out var holdReason))
            throw new InvalidOperationException(holdReason);
        Exception? restorationAuditFailure = null;
        try
        {
            await RecordAsync(operationId, CalibrationSessionPhase.Restoring,
                afterRestart ? "CalibrationRestoringAfterRestart" : "CalibrationRestoringBaseline").ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        { restorationAuditFailure = failure; }
        var result = await RestorePhysicalBaselineAsync(afterRestart).ConfigureAwait(false);
        // journal 写入失败不能阻止物理安全动作；缺少持久证据时，即使相机已恢复，session 仍保持独占。
        if (restorationAuditFailure is not null)
            throw new InvalidOperationException("CalibrationRestorationAuditUnavailable", restorationAuditFailure);
        if (!result.Succeeded)
        {
            await RecordAsync(operationId, CalibrationSessionPhase.RecoveryBlocked,
                result.ReasonCode).ConfigureAwait(false);
            return;
        }
        var terminal = StartTerminal(outcome == CalibrationSessionOutcome.Completed, reason);
        await RecordAsync(operationId, CalibrationSessionPhase.Restored, reason, outcome: outcome,
            terminal: terminal).ConfigureAwait(false);
        ConfirmDurableClose();
    }

    private void ConfirmDurableClose()
    {
        if (!_camera.ConfirmCalibrationSessionClosed(Header.SessionId))
            throw new InvalidOperationException("CalibrationDurableCloseAcknowledgementUnavailable");
        _publish(Evidence);
    }

    private CommandAuditFact StartTerminal(bool completed, string reason) =>
        new(Guid.NewGuid(), Header.AdmissionAttemptId, Header.Command.CorrelationId, Header.RuntimeEpoch,
            DateTimeOffset.UtcNow, AuditedCommandKind.StartCalibrationSession, Header.Command.Invocation.Source,
            Header.Command.Invocation.PrincipalId, Header.InteractiveSessionId, Header.Command.Invocation.StepUpGrantId,
            completed ? CommandAuditPhase.Completed : CommandAuditPhase.Failed, null, reason,
            Header.ActorPrincipalId.ToString("D"));

    private async Task RecordAsync(Guid operationId, CalibrationSessionPhase phase, string reason,
        CalibrationFrameEvidence? frame = null, CalibrationObservationEvidence? observation = null,
        CalibrationEvidenceExclusion? exclusion = null, CalibrationCandidateEvidence? candidate = null,
        CalibrationSessionOutcome outcome = CalibrationSessionOutcome.Pending, CommandAuditFact? terminal = null,
        CalibrationTemporaryConfigurationEvidence? temporaryConfiguration = null)
    {
        var entry = new CalibrationSessionEvent(Guid.NewGuid(), Header.SessionId, operationId, phase,
            outcome, reason, DateTimeOffset.UtcNow, frame, observation, exclusion, candidate,
            _authorizationCommand?.CorrelationId == operationId ? _authorizationCommand : null,
            temporaryConfiguration);
        var result = await _store.AppendCalibrationEventAsync(entry, terminal, CancellationToken.None).ConfigureAwait(false);
        if (!result.Committed) throw new InvalidOperationException(result.ReasonCode);
        var query = await _store.ReadCalibrationSessionAsync(Header.SessionId, CancellationToken.None).ConfigureAwait(false);
        var evidence = query is { Available: true, Evidence: { } verified } ? verified :
            throw new InvalidOperationException(query.ReasonCode);
        Volatile.Write(ref _evidence, evidence);
        // 物理恢复及其签名 terminal 必须先完成，再释放设备 fence。
        if (phase != CalibrationSessionPhase.Restored) _publish(evidence);
    }
}
