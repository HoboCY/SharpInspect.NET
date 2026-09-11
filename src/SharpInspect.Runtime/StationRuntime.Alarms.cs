using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private const string StartupAlarmCode = "StartupRecoveryRequired";
    private const string StartupAlarmSource = "Runtime.StartupRecovery";
    private readonly HashSet<string> _registeredAlarmSources = new(StringComparer.Ordinal) { StartupAlarmSource };
    private readonly Dictionary<string, ReceivedAlarmObservation> _alarmObservations = new(StringComparer.Ordinal);
    private Task? _alarmMaintenance;
    private int _queuedAlarmObservations;
    private SqliteCommandStore? AlarmStore => _audit as SqliteCommandStore;
    private AlarmPolicy? ConfiguredAlarmPolicy => AlarmStore?.ConfiguredAlarmPolicy;

    // Only Runtime and its future trusted adapters register observation sources. No public
    // host, UI or diagnostic capability can submit a healthy flag or register a source.
    internal void RegisterAlarmSource(string source)
    {
        if (ConfiguredAlarmPolicy is not { } policy || !policy.Rules.Any(rule => rule.Source == source))
            throw new ArgumentException("AlarmSourceUnmapped", nameof(source));
        lock (_sync) _registeredAlarmSources.Add(source);
    }

    private async Task InitializeAlarmsAsync()
    {
        if (ConfiguredAlarmPolicy is not { } policy)
        {
            if (_frameBufferPool is not null)
            {
                lock (_sync)
                {
                    if (_disposed) return;
                    var blockers = _snapshot.AdmissionBlockers
                        .Where(code => code != "AlarmAuthorityUnavailable")
                        .Append("AlarmAuthorityUnavailable");
                    PublishLocked(_snapshot with
                    {
                        Ready = false,
                        AlarmState = new AlarmStateSnapshot(false, "FrameBufferAlarmMappingUnavailable",
                            _snapshot.RuntimeEpoch, _snapshot.Revision, null,
                            Array.Empty<AlarmInstanceSnapshot>(),
                            new AlarmPlcProjection(Array.Empty<AlarmPlcEntry>(), 0, 0, false)),
                        AdmissionBlockers = new AdmissionBlockers(blockers)
                    });
                }
            }
            return;
        }
        if (!IsAlgorithmHungAlarmMappingValid(policy) || !IsCameraAcquisitionAlarmMappingValid(policy) ||
            !IsCameraRecoveryAlarmMappingValid(policy))
        {
            lock (_sync)
            {
                if (!_disposed) PublishLocked(_snapshot);
            }
            return;
        }
        if (policy.TryGetRule(AlgorithmHungAlarmCode, out var algorithmHungRule) &&
            algorithmHungRule is not null && algorithmHungRule.Source == AlgorithmHungAlarmSource)
        {
            lock (_sync) _registeredAlarmSources.Add(AlgorithmHungAlarmSource);
        }
        if (_frameBufferPool is not null && IsFrameBufferAlarmMappingValid(policy))
        {
            lock (_sync) _registeredAlarmSources.Add(FrameBufferAlarmSource);
        }
        if (CameraAcquisitionRegistered && IsCameraAcquisitionAlarmMappingValid(policy))
        {
            lock (_sync) _registeredAlarmSources.Add(CameraAcquisitionAlarmSource);
        }
        if (_cameraRecoveryService is not null && IsCameraRecoveryAlarmMappingValid(policy))
        {
            lock (_sync) _registeredAlarmSources.Add(CameraRecoveryAlarmSource);
        }
        await RefreshAlarmsAsync(CancellationToken.None).ConfigureAwait(false);
        // Commands await the complete initialization before handling. Acquiring their gate
        // here would deadlock a local Stop already waiting for initialization while holding it.
        await ObserveStartupAlarmAsync(_lifetime.Token).ConfigureAwait(false);
    }

    internal async ValueTask<AlarmObservationOutcome> ObserveAlarmAsync(AlarmObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _queuedAlarmObservations) > 16)
        {
            Interlocked.Decrement(ref _queuedAlarmObservations);
            MarkAuditFault("AlarmQueueFull", alarmAuthorityUnavailable: true);
            return new(false, "AlarmQueueFull", AuditPersistence.Unavailable);
        }
        var entered = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await _storeInitialization.WaitAsync(timeout.Token).ConfigureAwait(false);
            await WaitForAlarmCommandGateAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            return await ObserveAlarmCoreAsync(observation, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "AlarmObservationDeadlineExceeded", AuditPersistence.Unavailable); }
        finally
        {
            if (entered) _commandGate.Release();
            Interlocked.Decrement(ref _queuedAlarmObservations);
        }
    }

    private async Task WaitForAlarmCommandGateAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            bool stopPending;
            lock (_sync) stopPending = Volatile.Read(ref _pendingLocalStops) > 0 ||
                _snapshot.LastCommand is { State: OperationState.Pending, ReasonCode: "StopAdmitted" } &&
                _pendingProductionStopRetirement is null;
            if (!stopPending && _audit?.Integrity?.State != AuditIntegrityState.Verifying) return;
            // Idle Stop retains its bounded priority through completion. An accepted
            // production Stop must keep observing alarms while its cycle may need Abort.
            // Audit verification still completes without occupying the command gate.
            _commandGate.Release();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<AlarmObservationOutcome> ObserveAlarmCoreAsync(AlarmObservation observation,
        CancellationToken cancellationToken, bool previewRestorationDuringShutdown = false,
        bool manualRestorationDuringShutdown = false, bool qualificationCycleDuringShutdown = false)
    {
        if (AlarmStore is not { } store || ConfiguredAlarmPolicy is null)
            return new(false, "AlarmPolicyUnavailable", AuditPersistence.NotAttempted);
        if (string.IsNullOrWhiteSpace(observation.Code))
            return new(false, "AlarmObservationInvalid", AuditPersistence.NotAttempted);
        Guid epoch;
        var trustedPreviewRetirement = previewRestorationDuringShutdown &&
            observation.Code == PreviewAlarmCode && observation.Source == PreviewAlarmSource &&
            !observation.SourceHealthy || manualRestorationDuringShutdown &&
            observation.Code == ManualInspectionAlarmCode && observation.Source == ManualInspectionAlarmSource &&
            !observation.SourceHealthy || qualificationCycleDuringShutdown &&
            observation.Source == SharpInspect.Runtime.Qualification.QualificationCycleAlarmCodes.Source &&
            QualificationCycleCodes.Contains(observation.Code, StringComparer.Ordinal) && !observation.SourceHealthy;
        lock (_sync)
        {
            if ((_shutdownRequested && !trustedPreviewRetirement) || _disposed)
                return new(false, "RuntimeStopped", AuditPersistence.NotAttempted);
            epoch = _snapshot.RuntimeEpoch;
        }
        try
        {
            await WaitForAlarmAuditAsync(cancellationToken).ConfigureAwait(false);
            var write = await store.UpdateAlarmObservationAsync(epoch, current =>
            {
                AlarmTransitionDecision decision;
                lock (_sync)
                {
                    if ((_shutdownRequested && !trustedPreviewRetirement) || _disposed || cancellationToken.IsCancellationRequested)
                        decision = new(false, "AlarmObservationCancelled", Array.Empty<AlarmHistoryRecord>());
                    else if (observation.RuntimeEpoch == epoch &&
                        (observation.Sequence == long.MaxValue ||
                         observation.Sequence != NextAlarmObservationSequenceLocked(observation.Code)))
                        decision = new(false, "AlarmObservationOutOfOrder", Array.Empty<AlarmHistoryRecord>());
                    else if (!_registeredAlarmSources.Contains(observation.Source))
                        decision = new(false, "AlarmSourceMismatch", new[]
                        {
                            new AlarmHistoryRecord(0, Guid.NewGuid(), null, null, null,
                                AlarmTransitionKind.BoundaryRejected, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                                null, null, null, "AlarmSourceMismatch", null, null)
                        });
                    else
                    {
                        decision = AlarmTransitions.Observe(current, observation, epoch, DateTimeOffset.UtcNow);
                        if (decision.Succeeded && !observation.SourceHealthy &&
                            current.Policy!.TryGetRule(observation.Code, out var rule) &&
                            rule!.ProductionImpact != ProductionImpact.None)
                            PublishLocked(_snapshot with { Ready = false });
                    }
                }
                return new AlarmObservationUpdate(decision, decision.Events);
            }, cancellationToken).ConfigureAwait(false);
            if (!write.Committed || write.Result is not AlarmTransitionDecision applied)
            {
                MarkAuditFault("AlarmAuditUnavailable", alarmAuthorityUnavailable: true);
                return new(false, "AlarmAuditUnavailable", AuditPersistence.Unavailable);
            }
            if (applied.Succeeded)
            {
                lock (_sync) _alarmObservations[observation.Code] = new(observation.RuntimeEpoch,
                    observation.Sequence, Stopwatch.GetTimestamp(), observation.SourceHealthy);
            }
            await RefreshAlarmsAsync(CancellationToken.None).ConfigureAwait(false);
            return new(applied.Succeeded, applied.ReasonCode,
                applied.Events.Count == 0 ? AuditPersistence.NotAttempted : AuditPersistence.Persisted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MarkAuditFault("AlarmAuditUnavailable", alarmAuthorityUnavailable: true);
            return new(false, "AlarmAuditUnavailable", AuditPersistence.Unavailable);
        }
    }

    private async Task WaitForAlarmAuditAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_audit?.CommitTimeout ?? TimeSpan.FromSeconds(2));
        while (_audit?.Integrity?.State == AuditIntegrityState.Verifying)
            await Task.Delay(20, deadline.Token).ConfigureAwait(false);
    }

    private async Task RefreshAlarmsAsync(CancellationToken cancellationToken)
    {
        if (AlarmStore is not { } store || ConfiguredAlarmPolicy is null) return;
        if (!IsAlgorithmHungAlarmMappingValid(ConfiguredAlarmPolicy) ||
            !IsCameraAcquisitionAlarmMappingValid(ConfiguredAlarmPolicy) ||
            !IsCameraRecoveryAlarmMappingValid(ConfiguredAlarmPolicy))
        {
            lock (_sync)
            {
                if (!_disposed) PublishLocked(_snapshot);
            }
            return;
        }
        Guid epoch;
        lock (_sync) epoch = _snapshot.RuntimeEpoch;
        try
        {
            var current = await store.ReadAlarmStateAsync(epoch, cancellationToken).ConfigureAwait(false);
            if (_frameBufferPool is not null && !IsFrameBufferAlarmMappingValid(ConfiguredAlarmPolicy))
                current = new AlarmStateSnapshot(false, "FrameBufferAlarmMappingUnavailable", epoch, 0,
                    ConfiguredAlarmPolicy, current.Instances, current.Plc);
            lock (_sync)
            {
                if (_disposed) return;
                var blocking = current.Instances.Any(instance => instance.ProductionImpact != ProductionImpact.None);
                if (current.Instances.Any(instance => instance.ProductionImpact == ProductionImpact.FaultAbort))
                {
                    RequestManualInspectionStop("ManualInspectionFaultAbort", abort: true);
                    RequestStationQualificationStop("StationQualificationFaultAbort", abort: true);
                    RequestProductionInspectionAbort("ProductionInspectionFaultAbort");
                }
                var blockers = _snapshot.AdmissionBlockers.Where(code => code is not
                    ("AlarmProductionBlocked" or "AlarmAuthorityUnavailable")).ToList();
                if (blocking) blockers.Add("AlarmProductionBlocked");
                if (!current.Available) blockers.Add("AlarmAuthorityUnavailable");
                PublishLocked(_snapshot with
                {
                    Ready = _snapshot.Ready && !blocking && current.Available,
                    Alarms = new(current.Instances.Count(instance => instance.Lifecycle == AlarmLifecycle.Active),
                        current.Instances.Count(instance => instance.IsLatched), blocking),
                    AlarmState = current, AdmissionBlockers = new(blockers)
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("AlarmHistoryUnavailable", alarmAuthorityUnavailable: true); }
    }

    private AlarmTransitionDecision EvaluateAlarmCommand(AlarmStateSnapshot current, RuntimeCommand command)
    {
        lock (_sync)
        {
            var id = command switch
            {
                AcknowledgeAlarmCommand acknowledge => acknowledge.AlarmInstanceId,
                ResetAlarmCommand reset => reset.AlarmInstanceId,
                _ => Guid.Empty
            };
            var instance = current.Instances.SingleOrDefault(item => item.InstanceId == id);
            var fresh = instance is not null && current.Policy is { } policy &&
                _alarmObservations.TryGetValue(instance.Code, out var received) &&
                received.Epoch == _snapshot.RuntimeEpoch && received.Sequence == instance.SourceObservationSequence &&
                received.Healthy && IsFresh(received, policy);
            var unmet = AlarmResetPrerequisites.None;
            if (instance is not null)
            {
                var required = instance.ResetPrerequisites;
                if (_snapshot.Busy || _snapshot.CurrentExecution is not null)
                    unmet |= required & AlarmResetPrerequisites.NoActiveExecution;
                if (_snapshot.Evidence.PendingDeliveries > 0 || _snapshot.Handshake != HandshakePhase.Idle ||
                    _stationQualificationOwner?.ModbusRecoveryRequired == true)
                    unmet |= required & AlarmResetPrerequisites.NoPendingDelivery;
                if (_snapshot.Recovery != RecoveryState.None || _stationQualificationRecoveryBlocked ||
                    (_cameraRecoveryService is not null &&
                     _cameraRecoveryService.GetSnapshot().State != CameraRecoveryState.Healthy))
                    unmet |= required & AlarmResetPrerequisites.RecoveryComplete;
                if (_snapshot.Mode != ExclusiveMode.None)
                    unmet |= required & AlarmResetPrerequisites.NoExclusiveMode;
            }
            return AlarmTransitions.DecideCommand(current, command, _snapshot.RuntimeEpoch,
                fresh, unmet, DateTimeOffset.UtcNow);
        }
    }

    private static bool IsFresh(ReceivedAlarmObservation observation, AlarmPolicy policy)
    {
        var elapsed = (Stopwatch.GetTimestamp() - observation.ReceivedTimestamp) / (double)Stopwatch.Frequency;
        return elapsed >= 0 && elapsed < policy.SourceObservationFreshness.TotalSeconds;
    }

    private async Task ObserveStartupAlarmAsync(CancellationToken cancellationToken)
    {
        AlarmObservation observation;
        lock (_sync)
        {
            var sequence = NextAlarmObservationSequenceLocked(StartupAlarmCode);
            observation = new(_snapshot.RuntimeEpoch, sequence, StartupAlarmCode, StartupAlarmSource,
                _snapshot.Recovery == RecoveryState.None, DateTimeOffset.UtcNow);
        }
        await ObserveAlarmCoreAsync(observation, cancellationToken).ConfigureAwait(false);
    }

    // Source observation sequences are assigned by Runtime, independently of provider sequence
    // numbers. Future adapters must reject their own stale provider revisions before this boundary.
    internal long NextAlarmObservationSequence(string code)
    {
        lock (_sync) return NextAlarmObservationSequenceLocked(code);
    }

    private long NextAlarmObservationSequenceLocked(string code)
    {
        var previous = Math.Max(_alarmObservations.TryGetValue(code, out var received) ? received.Sequence : 0,
            _snapshot.AlarmState?.Instances.Where(instance => instance.Code == code)
                .Select(instance => instance.SourceObservationSequence).DefaultIfEmpty(0).Max() ?? 0);
        return previous >= long.MaxValue - 1 ? long.MaxValue : previous + 1;
    }

    private void ScheduleAlarmMaintenanceLocked()
    {
        if (ConfiguredAlarmPolicy is null || !_storeInitialization.IsCompletedSuccessfully ||
            _alarmMaintenance is { IsCompleted: false }) return;
        _alarmMaintenance = Task.Run(MaintainAlarmHealthAsync);
    }

    private async Task MaintainAlarmHealthAsync()
    {
        var entered = false;
        try
        {
            if (ConfiguredAlarmPolicy is not { } policy) return;
            lock (_sync) if (_snapshot.AlarmState is { Available: false }) return;
            await WaitForAlarmCommandGateAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            bool refreshStartup;
            lock (_sync) refreshStartup = !_alarmObservations.TryGetValue(StartupAlarmCode, out var startup) ||
                !IsFresh(startup, policy) || startup.Healthy != (_snapshot.Recovery == RecoveryState.None);
            bool refreshFrameBuffer;
            lock (_sync)
            {
                refreshFrameBuffer = _frameBufferPool is not null &&
                    IsFrameBufferAlarmMappingValid(policy) &&
                    (!_alarmObservations.TryGetValue(FrameBufferAlarmCode, out var frameBuffer) ||
                     frameBuffer.Healthy != !_frameBufferPool.ProductionFaultLatched ||
                     !IsFresh(frameBuffer, policy));
            }
            AlarmObservation[] expired;
            lock (_sync)
            {
                expired = _alarmObservations.Where(pair => pair.Key != StartupAlarmCode &&
                    !(pair.Key == FrameBufferAlarmCode && _frameBufferPool is not null &&
                      IsFrameBufferAlarmMappingValid(policy)) &&
                    !(_cameraRecoveryService is not null &&
                      (pair.Key is CameraDisconnectedAlarmCode or CameraRecoveryFailedAlarmCode) &&
                      IsCameraRecoveryAlarmMappingValid(policy)) && pair.Value.Healthy &&
                    !IsFresh(pair.Value, policy)).Select(pair => new AlarmObservation(_snapshot.RuntimeEpoch,
                        NextAlarmObservationSequenceLocked(pair.Key), pair.Key,
                        policy.Rules.Single(rule => rule.Code == pair.Key).Source, false, DateTimeOffset.UtcNow)).ToArray();
            }
            if (refreshStartup) await ObserveStartupAlarmAsync(_lifetime.Token).ConfigureAwait(false);
            if (refreshFrameBuffer)
            {
                AlarmObservation? frameObservation = null;
                lock (_sync)
                {
                    if (_frameBufferPool is not null && IsFrameBufferAlarmMappingValid(policy))
                    {
                        var sequence = NextAlarmObservationSequenceLocked(FrameBufferAlarmCode);
                        var healthy = !_frameBufferPool.ProductionFaultLatched;
                        var hasActiveInstance = _snapshot.AlarmState?.Instances.Any(instance =>
                            instance.Code == FrameBufferAlarmCode && instance.Lifecycle != AlarmLifecycle.Cleared) == true;
                        if (healthy && !hasActiveInstance)
                        {
                            // A healthy source without an alarm instance has no durable
                            // lifecycle event. Keep the trusted monotonic observation local
                            // so freshness is renewed without manufacturing an audit row that
                            // has no instance to bind to.
                            _alarmObservations[FrameBufferAlarmCode] = new(_snapshot.RuntimeEpoch,
                                sequence, Stopwatch.GetTimestamp(), true);
                        }
                        else
                        {
                            frameObservation = new AlarmObservation(_snapshot.RuntimeEpoch, sequence,
                                FrameBufferAlarmCode, FrameBufferAlarmSource, healthy, DateTimeOffset.UtcNow);
                        }
                    }
                }
                if (frameObservation is not null)
                    await ObserveAlarmCoreAsync(frameObservation, _lifetime.Token).ConfigureAwait(false);
            }
            foreach (var observation in expired)
                await ObserveAlarmCoreAsync(observation, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { MarkAuditFault("AlarmObservationUnavailable", alarmAuthorityUnavailable: true); }
        finally { if (entered) _commandGate.Release(); }
    }

    private sealed record ReceivedAlarmObservation(Guid Epoch, long Sequence, long ReceivedTimestamp, bool Healthy);
}

internal sealed record AlarmObservationOutcome(bool Accepted, string ReasonCode, AuditPersistence Audit);
