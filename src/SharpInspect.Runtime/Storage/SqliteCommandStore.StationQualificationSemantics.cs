using SharpInspect.Abstractions;
using SharpInspect.Runtime.Qualification;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private static readonly TimeSpan StationQualificationMaximumObservationFreshness =
        TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validates the semantic part of a qualification event before it is appended
    /// and while the immutable ledger is replayed.  Hash-chain, audit-reference,
    /// and SQL capacity checks remain in their respective storage verifiers; this
    /// method only evaluates the facility/run/restore facts carried by the event.
    /// </summary>
    internal static string? ValidateStationQualificationSemantics(
        IReadOnlyList<StationQualificationSessionEvent> priorEvents,
        StationQualificationSessionEvent candidate)
    {
        if (priorEvents is null)
            return "StationQualificationPriorEventsMissing";
        if (candidate is null)
            return "StationQualificationCandidateMissing";

        var sessionEvents = new List<StationQualificationSessionEvent>();
        foreach (var prior in priorEvents)
        {
            if (prior is null)
                return "StationQualificationPriorEventMissing";
            if (prior.SessionId != candidate.SessionId)
                continue;
            if (prior.Position >= candidate.Position)
                return "StationQualificationEventOrderInvalid";
            sessionEvents.Add(prior);
        }

        sessionEvents.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        for (var index = 1; index < sessionEvents.Count; index++)
        {
            if (sessionEvents[index - 1].Position == sessionEvents[index].Position)
                return "StationQualificationEventPositionDuplicate";
            if (sessionEvents[index - 1].Header.ContentHash != sessionEvents[index].Header.ContentHash)
                return "StationQualificationHeaderChanged";
        }

        var previous = sessionEvents.Count == 0 ? null : sessionEvents[^1];
        if (previous is not null && previous.Header.ContentHash != candidate.Header.ContentHash)
            return "StationQualificationHeaderChanged";
        if (previous is not null && candidate.RecordedAtUtc < previous.RecordedAtUtc)
            return "StationQualificationEventTimestampOrderInvalid";
        if (previous?.Phase == StationQualificationSessionPhase.Closed)
            return "StationQualificationClosedAppend";

        if (sessionEvents.Count == 0)
        {
            var startFailure = ValidateStartEvent(candidate);
            if (startFailure is not null)
                return startFailure;
        }

        if (candidate.Phase == StationQualificationSessionPhase.ReadyForStimulus &&
            candidate.CommandKind == AuditedCommandKind.StartStationQualificationSession &&
            (candidate.Observation is null || candidate.ReasonCode != "StationQualificationReadyForStimulus" ||
             !sessionEvents.Any(value => value.ReasonCode == "StationQualificationIsolationVerified" &&
                 value.Observation is not null && value.RecoveryAttempt is null)))
            return "StationQualificationReadyIsolationEvidenceMissing";

        var observationFailure = ValidateObservation(candidate, sessionEvents);
        if (observationFailure is not null)
            return observationFailure;

        var runFailure = ValidateRun(candidate, sessionEvents);
        if (runFailure is not null)
            return runFailure;

        if (candidate.Phase == StationQualificationSessionPhase.Closed)
            return ValidateClosedEvent(candidate, sessionEvents);

        if (candidate.Phase == StationQualificationSessionPhase.RecoveryBlocked &&
            candidate.Terminal)
            return "StationQualificationRecoveryBlockedTerminal";

        return null;
    }

    private static string? ValidateStartEvent(StationQualificationSessionEvent candidate)
    {
        if (candidate.CommandKind != AuditedCommandKind.StartStationQualificationSession ||
            candidate.CommandCorrelationId != candidate.Header.StartCorrelationId ||
            candidate.AttemptId != candidate.Header.StartAttemptId ||
            candidate.CommandAuthorizationTarget != candidate.Header.AuthorizationTarget)
            return "StationQualificationStartAuthorizationMismatch";
        if (candidate.Phase != StationQualificationSessionPhase.Admitted ||
            candidate.Restoration != StationQualificationRestorationState.Pending ||
            candidate.Terminal || candidate.Observation is not null || candidate.Run is not null ||
            candidate.RecoveryAttempt is not null)
            return "StationQualificationStartEventInvalid";
        return null;
    }

    private static string? ValidateObservation(
        StationQualificationSessionEvent candidate,
        IReadOnlyList<StationQualificationSessionEvent> priorEvents)
    {
        var observation = candidate.Observation;
        if (observation is null)
            return null;

        if (!TryGetObservationMode(candidate.ReasonCode, out var isolated, out var targetController))
            return "StationQualificationObservationReasonInvalid";

        var leaseNonce = candidate.RecoveryAttempt?.LeaseNonce ?? candidate.Header.LeaseNonce;
        var runtimeEpoch = candidate.RecoveryAttempt?.RuntimeEpoch ?? candidate.Header.RuntimeEpoch;
        var previousSequence = 0L;
        foreach (var prior in priorEvents)
        {
            var priorObservation = prior.Observation;
            if (priorObservation is null ||
                priorObservation.LeaseNonce != leaseNonce ||
                priorObservation.RuntimeEpoch != runtimeEpoch)
                continue;
            previousSequence = Math.Max(previousSequence, priorObservation.Sequence);
        }

        QualificationFacilityRequest request;
        try
        {
            request = new(candidate.SessionId, leaseNonce, runtimeEpoch, candidate.Header.Plan);
        }
        catch (ArgumentException)
        {
            return "StationQualificationObservationRequestInvalid";
        }

        return StationQualificationFacilityValidator.ValidateObservation(request, observation,
            previousSequence, candidate.RecordedAtUtc,
            StationQualificationMaximumObservationFreshness, isolated, targetController);
    }

    private static bool TryGetObservationMode(string reason, out bool isolated, out bool targetController)
    {
        switch (reason)
        {
            case "StationQualificationTargetObserved":
            case "StationQualificationTargetReadbackVerified":
                isolated = false;
                targetController = true;
                return true;

            case "StationQualificationTargetRestored":
                isolated = true;
                targetController = true;
                return true;

            case "StationQualificationIsolationVerified":
            case "StationQualificationReadyForStimulus":
            case "StationQualificationRunAdmitted":
                isolated = true;
                targetController = false;
                return true;

            default:
                isolated = false;
                targetController = false;
                return false;
        }
    }

    private static string? ValidateRun(
        StationQualificationSessionEvent candidate,
        IReadOnlyList<StationQualificationSessionEvent> priorEvents)
    {
        var run = candidate.Run;
        if (run is null)
            return null;
        if (run.SessionId != candidate.SessionId || run.ContextHash != candidate.Header.Plan.QualificationContextHash)
            return "StationQualificationRunBindingMismatch";
        if (run.ControllerEpoch == 0)
            return "StationQualificationRunControllerEpochInvalid";
        if (run.CycleSequence == 0)
            return "StationQualificationRunCycleSequenceInvalid";
        if (!candidate.Header.Plan.ScenarioIds.Contains(run.ScenarioId, StringComparer.Ordinal))
            return "StationQualificationRunScenarioMismatch";
        if (run.AdmittedAtUtc > candidate.RecordedAtUtc)
            return "StationQualificationRunAdmissionInFuture";
        if (run.CompletedAtUtc is { } completed && completed > candidate.RecordedAtUtc)
            return "StationQualificationRunCompletionInFuture";

        var priorRuns = priorEvents
            .Where(value => value.Run is not null)
            .Select(value => value.Run!)
            .ToArray();
        var sameRun = priorRuns.LastOrDefault(value => value.RunId.Value == run.RunId.Value);
        if (sameRun is null)
        {
            if (candidate.Observation is null || candidate.RecoveryAttempt is not null ||
                priorEvents.LastOrDefault()?.Phase != StationQualificationSessionPhase.ReadyForStimulus)
                return "StationQualificationRunIsolationEvidenceMissing";
            if (run.Position != candidate.Position)
                return "StationQualificationRunAdmissionPositionMismatch";
            if (candidate.Phase != StationQualificationSessionPhase.Running || run.Completed)
                return "StationQualificationRunAdmissionPhaseInvalid";

            var previousStimulus = priorRuns.Length == 0 ? 0 : priorRuns.Max(value => value.StimulusSequence);
            QualificationFacilityStimulus stimulus;
            try
            {
                stimulus = new(candidate.Header.SessionId, candidate.Header.LeaseNonce,
                    run.StimulusSequence, run.ScenarioId, run.ContextHash,
                    run.ControllerEpoch, run.CycleSequence);
            }
            catch (ArgumentException)
            {
                return "StationQualificationStimulusInvalid";
            }

            var stimulusFailure = StationQualificationFacilityValidator.ValidateStimulus(
                new QualificationFacilityRequest(candidate.Header.SessionId, candidate.Header.LeaseNonce,
                    candidate.Header.RuntimeEpoch, candidate.Header.Plan), stimulus, previousStimulus);
            return stimulusFailure;
        }

        if (sameRun.Completed)
            return "StationQualificationRunAlreadyCompleted";
        if (run.Position != sameRun.Position || run.SessionId != sameRun.SessionId ||
            run.StimulusSequence != sameRun.StimulusSequence ||
            run.ScenarioId != sameRun.ScenarioId || run.ContextHash != sameRun.ContextHash ||
            run.ControllerEpoch != sameRun.ControllerEpoch || run.CycleSequence != sameRun.CycleSequence ||
            run.AdmittedAtUtc != sameRun.AdmittedAtUtc)
            return "StationQualificationRunIdentityChanged";
        if (!run.Completed)
            return "StationQualificationRunDuplicateAdmission";
        if (candidate.Phase is not (StationQualificationSessionPhase.Running or
            StationQualificationSessionPhase.Restoring))
            return "StationQualificationRunCompletionPhaseInvalid";
        if (candidate.Phase == StationQualificationSessionPhase.Restoring &&
            run.ExecutionStatus != ExecutionStatus.Cancelled)
            return "StationQualificationRecoveryRunMustCancel";

        return ValidateCompletedRun(candidate.Header, run);
    }

    private static string? ValidateCompletedRun(
        StationQualificationSessionHeader header, StationQualificationRunRecord run)
    {
        if (run.ExecutionStatus is null || run.CompletedAtUtc is null)
            return "StationQualificationRunCompletionMissing";

        if (run.ExecutionStatus != ExecutionStatus.Success)
        {
            if (run.Decision != InspectionDecision.Unknown || run.ResultPayloadJson is not null ||
                run.ResultPayloadHash is not null)
                return "StationQualificationNonSuccessEvidenceUnexpected";
            // Legacy cancelled/no-frame facts remain readable without a wire
            // result. A real failed algorithm may carry a validated Unknown.
            if (run.QualificationPayload is null) return null;
        }

        var resultJson = run.ResultPayloadJson;
        var resultHash = run.ResultPayloadHash;
        var frameMetadata = run.FrameMetadata;
        var frameProvenance = run.FrameProvenance;
        var payload = run.QualificationPayload;
        if ((run.ExecutionStatus == ExecutionStatus.Success &&
            (string.IsNullOrWhiteSpace(resultJson) || string.IsNullOrWhiteSpace(resultHash))) ||
            frameMetadata is null || frameProvenance is null || payload is null)
            return "StationQualificationSuccessEvidenceIncomplete";
        if (frameMetadata.Correlation.Kind != ExecutionKind.Qualification ||
            frameMetadata.Correlation.Value != run.RunId.Value ||
            frameProvenance.Correlation.Kind != ExecutionKind.Qualification ||
            frameProvenance.Correlation.Value != run.RunId.Value)
            return "StationQualificationSuccessEvidenceCorrelationMismatch";

        var snapshot = header.TargetBaseline.SuccessfulSnapshot;
        if (snapshot is null || payload.Binding.ContentHash != snapshot.PlcResultContract.ContentHash)
            return "StationQualificationSuccessContractMismatch";
        if (payload.SessionId != run.SessionId || payload.RunId.Value != run.RunId.Value ||
            payload.ContextHash != run.ContextHash)
            return "StationQualificationSuccessPayloadIdentityMismatch";
        if (payload.ControllerEpoch != run.ControllerEpoch || payload.CycleSequence != run.CycleSequence)
            return "StationQualificationSuccessPayloadCycleMismatch";
        if (payload.ExecutionStatus != run.ExecutionStatus || payload.Decision != run.Decision)
            return "StationQualificationSuccessPayloadOutcomeMismatch";
        if (payload.ReasonCode is not null && !string.Equals(payload.ReasonCode, run.ReasonCode, StringComparison.Ordinal))
            return "StationQualificationSuccessPayloadReasonMismatch";
        if (run.ExecutionStatus != ExecutionStatus.Success)
        {
            if (run.Timing is null || run.Timing.Recipe != snapshot.Recipe ||
                string.IsNullOrWhiteSpace(payload.ReasonCode))
                return "StationQualificationUnknownEvidenceIncomplete";
            return null;
        }

        try
        {
            var decoded = AlgorithmResultStorageCodec.Decode(run.Position, run.CompletedAtUtc.Value,
                resultJson!, resultHash!);
            if (decoded.RecordId != run.RunId.Value ||
                decoded.Timing.Recipe != snapshot.Recipe ||
                decoded.Algorithm != snapshot.PlcResultContract.Algorithm ||
                decoded.ResultSchema.ContentHash != snapshot.PlcResultContract.ResultSchema.ContentHash ||
                decoded.Correlation.Kind != ExecutionKind.Qualification ||
                decoded.Correlation.Value != run.RunId.Value ||
                decoded.ExecutionStatus != run.ExecutionStatus ||
                decoded.Decision != run.Decision ||
                !string.Equals(decoded.ReasonCode, payload.ReasonCode, StringComparison.Ordinal))
                return "StationQualificationSuccessResultBindingMismatch";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "StationQualificationSuccessResultInvalid";
        }

        return null;
    }

    private static string? ValidateClosedEvent(
        StationQualificationSessionEvent candidate,
        IReadOnlyList<StationQualificationSessionEvent> priorEvents)
    {
        if (!candidate.Terminal || candidate.Restoration != StationQualificationRestorationState.Restored)
            return "StationQualificationClosedEventInvalid";

        var readback = priorEvents.LastOrDefault(value =>
            value.Phase == StationQualificationSessionPhase.Restoring &&
            value.ReasonCode == "StationQualificationTargetReadbackVerified" &&
            value.Observation is not null &&
            value.RecoveryAttempt?.ContentHash == candidate.RecoveryAttempt?.ContentHash);
        var restored = priorEvents.LastOrDefault(value =>
            value.Phase == StationQualificationSessionPhase.Restoring &&
            value.ReasonCode == "StationQualificationTargetRestored" && value.Observation is not null &&
            value.RecoveryAttempt?.ContentHash == candidate.RecoveryAttempt?.ContentHash);
        return readback is null || restored is null || restored.Position >= readback.Position ||
            priorEvents.LastOrDefault()?.Position != readback.Position || candidate.RecoveryAttempt is null
            ? "StationQualificationTargetReadbackEvidenceMissing"
            : null;
    }
}
