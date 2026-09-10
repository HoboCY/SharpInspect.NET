using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task ExecuteModbusQualificationCyclesAsync(StationQualificationOwner owner)
    {
        var profile = _qualificationModbusProfile!;
        var coordinator = owner.CycleCoordinator = new InspectionCycleCoordinator<StationQualificationPayload>();
        await using var channel = new ModbusQualificationChannel(profile);
        var output = new InspectionCycleOutputLatch(channel.WriteStateAsync);
        Task? execution = null;
        var stateInitialized = false;
        ModbusControllerSignals? pendingAdmission = null;
        InspectionCycleRequestObserver? observer = null;
        try
        {
            channel.ValidatePayloadBinding(owner.Header.TargetBaseline.SuccessfulSnapshot!.PlcResultContract);
            owner.CycleStoragePolicy = await ReadQualificationCyclePolicyAsync(owner).ConfigureAwait(false);
            var knownKeys = await ReadQualificationCycleKeysAsync(profile.EndpointBindingHash,
                owner.Cancellation.Token).ConfigureAwait(false);
            using (var claim = ClaimStationQualificationPhysicalPhase(owner))
            {
                if (!claim.Available) throw new OperationCanceledException("QualificationModbusConnectRevoked");
                await channel.ConnectAsync(owner.Cancellation.Token).ConfigureAwait(false);
            }
            ModbusRuntimeSignals initial;
            using (var claim = ClaimStationQualificationPhysicalPhase(owner))
            {
                if (!claim.Available) throw new OperationCanceledException("QualificationModbusInitialReadRevoked");
                initial = await channel.ReadRuntimeStateAsync(owner.Cancellation.Token).ConfigureAwait(false);
            }
            if (initial.QualificationReady || initial.Busy || initial.ResultValid || initial.CycleFault ||
                initial.ProtocolViolation || initial.ProductionReady)
            {
                owner.ModbusRecoveryRequired = true;
                throw new InvalidOperationException("QualificationModbusInitialStateNotClear");
            }
            using (var claim = ClaimStationQualificationPhysicalPhase(owner))
            {
                if (!claim.Available) throw new OperationCanceledException("QualificationModbusInitialWriteRevoked");
                await output.ChangeAsync(owner.Cancellation.Token).ConfigureAwait(false);
                stateInitialized = true;
            }
            observer = new(channel.ReadAsync, profile.PollInterval, knownKeys,
                () => coordinator.SetPhase(InspectionCyclePhase.Accepted),
                reason =>
                {
                    lock (_sync) RequestStationQualificationExitLocked(owner, reason, abort: true);
                }, owner.Cancellation.Token);
            lock (_sync)
            {
                owner.CycleObserver = observer;
                if (owner.ExitRequested) observer.RevokeAdmission();
            }
            observer.Start();
            while (observer.Latest.Sequence == 0)
            {
                observer.RequireHealthy();
                await Task.Delay(profile.PollInterval, owner.StimulusCancellation.Token).ConfigureAwait(false);
            }

            while (true)
            {
                lock (_sync) if (owner.ExitRequested) break;
                await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
                owner.CycleStoragePolicy = await ReadQualificationCyclePolicyAsync(owner).ConfigureAwait(false);
                var isolated = await ObserveStationQualificationFacilityAsync(owner, true, false,
                    owner.Cancellation.Token).ConfigureAwait(false);
                await FlushRejectionsAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    if (HasActiveQualificationCycleAlarmLocked())
                    {
                        RequestStationQualificationExitLocked(owner, "QualificationProtocolAlarmBlocksNextCycle", abort: false);
                        break;
                    }
                }
                coordinator.SetPhase(InspectionCyclePhase.AwaitRequest);
                await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.ReadyForStimulus,
                    "StationQualificationReadyForStimulus", isolated).ConfigureAwait(false);
                await observer.EnableAcceptingAsync(() => output.ChangeAsync(owner.Cancellation.Token,
                    ready: true, busy: false, valid: false), owner.Cancellation.Token).ConfigureAwait(false);
                ModbusControllerSignals? accepted = null;
                while (!observer.TryTakeAccepted(out accepted))
                {
                    lock (_sync) if (owner.ExitRequested) break;
                    await FlushRejectionsAsync().ConfigureAwait(false);
                    await Task.Delay(profile.PollInterval, owner.StimulusCancellation.Token).ConfigureAwait(false);
                }
                observer.StopAccepting();
                pendingAdmission = accepted;
                bool ended;
                lock (_sync) ended = owner.ExitRequested;
                if (ended)
                {
                    if (accepted is not null)
                        await RecordRejectionAsync(new(new(accepted.ControllerEpoch, accepted.CycleSequence),
                            "QualificationRequestRevokedBeforeAdmission")).ConfigureAwait(false);
                    pendingAdmission = null;
                    break;
                }
                var signals = accepted ?? throw new InvalidOperationException("QualificationModbusRequestMissing");
                using (var claim = ClaimStationQualificationPhysicalPhase(owner))
                {
                    if (!claim.Available) throw new OperationCanceledException("QualificationModbusBusyWriteRevoked");
                    await output.ChangeAsync(owner.Cancellation.Token, ready: false, busy: true).ConfigureAwait(false);
                }
                var stimulus = new QualificationFacilityStimulus(owner.Header.SessionId, owner.Header.LeaseNonce,
                    checked(owner.StimulusSequence + 1), profile.ScenarioId, owner.Header.Plan.QualificationContextHash,
                    signals.ControllerEpoch, signals.CycleSequence);
                var rejection = StationQualificationFacilityValidator.ValidateStimulus(owner.Request, stimulus, owner.StimulusSequence);
                if (rejection is not null) throw new InvalidOperationException(rejection);
                await RequireStationQualificationAuthorityAsync(owner).ConfigureAwait(false);
                var admissionObservation = await ObserveStationQualificationFacilityAsync(owner, true, false,
                    owner.Cancellation.Token).ConfigureAwait(false);
                await AdmitStationQualificationRunAsync(owner, stimulus, admissionObservation).ConfigureAwait(false);
                pendingAdmission = null;

                execution = ExecuteStationQualificationCycleAsync(owner, stimulus, coordinator, (receipt, token) =>
                    coordinator.PublishAndAcknowledgeAsync(receipt,
                        new(signals.ControllerEpoch, signals.CycleSequence), observer, output, channel.WritePayloadAsync,
                        fact => RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.Running,
                            "QualificationCycle" + fact, cycle: new(ToQualificationCycleKind(fact),
                                receipt.Payload.RunId, profile.ContentHash)),
                        profile.AcknowledgementTimeout, profile.PollInterval, token));
                // The observer continues sampling independently while these
                // serialized journal writes wait behind the core transaction.
                while (!execution.IsCompleted)
                {
                    owner.Cancellation.Token.ThrowIfCancellationRequested();
                    await FlushRejectionsAsync().ConfigureAwait(false);
                    await Task.WhenAny(execution, Task.Delay(profile.PollInterval, owner.Cancellation.Token)).ConfigureAwait(false);
                }
                await execution.ConfigureAwait(false);
                execution = null;
                if (coordinator.Phase == InspectionCyclePhase.FaultTerminated)
                    throw new InvalidOperationException("QualificationCycleExecutionFaultTerminated");
                observer.CompleteCycle();
                await FlushRejectionsAsync().ConfigureAwait(false);
                if (++owner.RunCount >= _stationQualificationOptions!.MaximumRuns)
                {
                    lock (_sync) RequestStationQualificationExitLocked(owner, "StationQualificationRunLimitReached", abort: false);
                    break;
                }
            }
            observer.StopAccepting();
            await FlushRejectionsAsync().ConfigureAwait(false);
            await output.ChangeAsync(CancellationToken.None, ready: false, busy: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!owner.ModbusRecoveryRequired && owner.ExitRequested && !owner.Aborted)
        {
            observer?.StopAccepting();
            // No accepted cycle owns a result. This is an ordinary controlled
            // exit from the request wait; still revoke the advertised permit.
            try
            {
                if (stateInitialized)
                    await output.ChangeAsync(CancellationToken.None, ready: false, busy: false).ConfigureAwait(false);
                if (pendingAdmission is not null)
                    await RecordRejectionAsync(new(new(pendingAdmission.ControllerEpoch, pendingAdmission.CycleSequence),
                        "QualificationRequestRevokedBeforeAdmission")).ConfigureAwait(false);
                await FlushRejectionsAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { await HandleFaultAsync(exception).ConfigureAwait(false); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { await HandleFaultAsync(exception).ConfigureAwait(false); }
        finally
        {
            if (observer is not null) await observer.DisposeAsync().ConfigureAwait(false);
            lock (_sync) owner.CycleObserver = null;
        }

        async Task HandleFaultAsync(Exception exception)
        {
            observer?.StopAccepting();
            var hadUnresolvedCycle = owner.ModbusRecoveryRequired;
            var timedOut = exception is InspectionCycleAckTimeoutException;
            var traceFailure = coordinator.Phase == InspectionCyclePhase.Committing ||
                exception is InspectionCyclePersistenceException;
            var controllerChanged = exception.Message == "QualificationControllerEpochChanged" ||
                owner.ExitReason == "QualificationControllerEpochChanged";
            lock (_sync)
            {
                owner.ModbusRecoveryRequired = true;
                owner.CycleFaultTerminated = true;
                _stationQualificationRecoveryBlocked = true;
                owner.Restoration = StationQualificationRestorationState.RecoveryBlocked;
                RequestStationQualificationExitLocked(owner, controllerChanged ? "QualificationControllerEpochChanged" :
                    timedOut ? "QualificationResultAckTimeout" :
                    traceFailure ? "QualificationCycleTracePersistenceFailed" : "QualificationCycleInterrupted", abort: true);
            }
            // Preserve ResultValid and the payload. If the channel is uncertain
            // it is already sealed and this update performs no replay.
            try { await output.ChangeAsync(CancellationToken.None, ready: false, busy: false, fault: true).ConfigureAwait(false); }
            catch (Exception fault) when (fault is not OutOfMemoryException) { }
            if (execution is not null)
            {
                try { await execution.ConfigureAwait(false); }
                catch (Exception pending) when (pending is not OutOfMemoryException) { }
                execution = null;
            }
            try
            {
                await RecordStationQualificationProgressAsync(owner, StationQualificationSessionPhase.RecoveryBlocked,
                    owner.ExitReason, cycle: hadUnresolvedCycle && owner.CurrentRunId is { } runId ?
                        new(timedOut ? QualificationCycleEventKind.AckTimeout : QualificationCycleEventKind.CycleFaultTerminated,
                            runId, profile.ContentHash, ReasonCode: owner.ExitReason) : null).ConfigureAwait(false);
            }
            catch (Exception journal) when (journal is not OutOfMemoryException)
            { MarkAuditFault("QualificationCycleFaultJournalUnavailable", alarmAuthorityUnavailable: true); }
            await LatchQualificationCycleAlarmAsync(timedOut ? QualificationCycleAlarmCodes.ResultAckTimeout :
                traceFailure ? QualificationCycleAlarmCodes.TracePersistenceFailed : QualificationCycleAlarmCodes.Interrupted).ConfigureAwait(false);
        }
        async Task FlushRejectionsAsync()
        {
            if (observer is null) return;
            while (observer.TakeRejected() is { } rejected)
                await RecordRejectionAsync(rejected).ConfigureAwait(false);
        }

        async Task RecordRejectionAsync(RejectedCycleRequest rejected)
        {
            observer?.StopAccepting();
            await output.ChangeAsync(owner.Cancellation.Token, ready: false, violation: true).ConfigureAwait(false);
            StationQualificationSessionPhase phase;
            lock (_sync)
            {
                if (owner.CurrentRunId is null)
                {
                    if (!owner.ExitRequested)
                        RequestStationQualificationExitLocked(owner, "QualificationProtocolAlarmBlocksNextCycle", abort: false);
                    // No accepted run remains to drain. Enter the existing
                    // restoration phase without inventing a Running identity
                    // or moving backwards from Ready into isolation.
                    phase = StationQualificationSessionPhase.Restoring;
                }
                else phase = owner.ExitRequested ? owner.LastEvent.Phase : StationQualificationSessionPhase.Running;
            }
            await RecordStationQualificationProgressAsync(owner, phase,
                rejected.ReasonCode, cycle: new(QualificationCycleEventKind.ProtocolRequestRejected,
                    owner.CurrentRunId, profile.ContentHash,
                    RejectedControllerEpoch: rejected.Key.ControllerEpoch,
                    RejectedCycleSequence: rejected.Key.CycleSequence, ReasonCode: rejected.ReasonCode)).ConfigureAwait(false);
            await LatchQualificationCycleAlarmAsync(QualificationCycleAlarmCodes.TriggerRejected).ConfigureAwait(false);
        }
    }

    private bool HasActiveQualificationCycleAlarmLocked() => _snapshot.AlarmState?.Instances.Any(value =>
        QualificationCycleCodes.Contains(value.Code, StringComparer.Ordinal) && value.Lifecycle != AlarmLifecycle.Cleared) == true;

    private Task<TraceStoragePolicySnapshot> ReadQualificationCyclePolicyAsync(StationQualificationOwner owner) =>
        ReadQualificationCyclePolicyAsync(owner.Cancellation.Token);

    private async Task<TraceStoragePolicySnapshot> ReadQualificationCyclePolicyAsync(CancellationToken token)
    {
        var profile = _qualificationModbusProfile!;
        var options = _stationQualificationStoreOptions!;
        var read = await new SqliteTraceStoragePolicyQuery(options).ReadAsync(null, token).ConfigureAwait(false);
        if (!read.Available || read.Snapshot is not { } snapshot || snapshot.Version != profile.TracePolicyVersion ||
            snapshot.ContentHash != profile.TracePolicySnapshotHash || snapshot.Policy.RequiredRoutes.Count != 0 ||
            options.TraceStoragePolicies?.DeploymentScope?.RequiredRoutes.Count != 0)
            throw new InvalidOperationException("QualificationCycleStoragePolicyUnavailable");
        return snapshot;
    }

    private async Task<IReadOnlyCollection<PlcControllerCycle>> ReadQualificationCycleKeysAsync(string endpoint,
        CancellationToken token)
    {
        var query = new SqliteQualificationCycleHistoryQuery(_stationQualificationStoreOptions!);
        var keys = new HashSet<PlcControllerCycle>();
        long after = 0;
        long? through = null;
        do
        {
            var page = await query.QueryAsync(new(AfterPosition: after, ThroughPosition: through, PageSize: 128), token).ConfigureAwait(false);
            if (!page.Available) throw new InvalidOperationException("QualificationCycleHistoryUnavailable");
            through ??= page.ThroughPosition;
            foreach (var fact in page.Events)
                if (fact.EndpointBindingHash == endpoint && fact.ControllerEpoch is { } epoch && fact.CycleSequence is { } sequence)
                    keys.Add(new(epoch, sequence));
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after) throw new InvalidOperationException("QualificationCycleHistoryCursorInvalid");
            after = next;
        } while (true);
        return keys;
    }

    private static QualificationCycleEventKind ToQualificationCycleKind(InspectionCycleDeliveryFact fact) => fact switch
    {
        InspectionCycleDeliveryFact.PublicationPrepared => QualificationCycleEventKind.PublicationPrepared,
        InspectionCycleDeliveryFact.ResultValidPublished => QualificationCycleEventKind.ResultValidPublished,
        InspectionCycleDeliveryFact.ResultAckObserved => QualificationCycleEventKind.ResultAckObserved,
        InspectionCycleDeliveryFact.ResultValidCleared => QualificationCycleEventKind.ResultValidCleared,
        InspectionCycleDeliveryFact.AckReset => QualificationCycleEventKind.AckReset,
        _ => throw new ArgumentOutOfRangeException(nameof(fact))
    };
}
