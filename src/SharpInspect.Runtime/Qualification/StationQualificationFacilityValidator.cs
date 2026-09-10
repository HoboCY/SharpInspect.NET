using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Qualification;

/// <summary>
/// Validates raw facts returned by a registered qualification facility before the
/// Runtime records or acts on them.  The facility is not trusted to report an
/// aggregate qualification result; this validator checks the exact request,
/// frozen plan and safety state instead.
/// </summary>
internal static class StationQualificationFacilityValidator
{
    private static readonly QualificationDestinationKind[] RequiredDestinationKinds =
    {
        QualificationDestinationKind.ProductionPlcOutput,
        QualificationDestinationKind.OrdinaryOutbox,
        QualificationDestinationKind.Mes,
        QualificationDestinationKind.Spc,
        QualificationDestinationKind.Yield
    };

    private static readonly TimeSpan MaximumFreshness = TimeSpan.FromMinutes(5);

    internal static string? ValidateObservation(QualificationFacilityRequest request,
        QualificationFacilityObservation observation, long previousSequence,
        DateTimeOffset nowUtc, TimeSpan freshness, bool isolated, bool targetController)
    {
        if (request is null)
            return "QualificationObservationRequestMissing";
        if (observation is null)
            return "QualificationObservationMissing";
        if (request.Plan is null)
            return "QualificationObservationPlanMissing";
        if (request.SessionId == Guid.Empty || request.LeaseNonce == Guid.Empty ||
            request.RuntimeEpoch == Guid.Empty)
            return "QualificationObservationRequestIdentityInvalid";
        if (previousSequence < 0)
            return "QualificationObservationPreviousSequenceInvalid";
        if (freshness <= TimeSpan.Zero || freshness > MaximumFreshness)
            return "QualificationObservationFreshnessInvalid";
        if (nowUtc == default || nowUtc.Offset != TimeSpan.Zero)
            return "QualificationObservationNowUtcInvalid";
        if (observation.ObservedAtUtc == default || observation.ObservedAtUtc.Offset != TimeSpan.Zero)
            return "QualificationObservationTimestampInvalid";
        if (observation.ObservedAtUtc > nowUtc)
            return "QualificationObservationFuture";
        if (nowUtc - observation.ObservedAtUtc > freshness)
            return "QualificationObservationStale";

        if (!SameHarness(request.Plan.QualificationHarnessIdentity, observation.Identity))
            return "QualificationObservationHarnessIdentityMismatch";
        if (observation.SessionId != request.SessionId)
            return "QualificationObservationSessionMismatch";
        if (observation.LeaseNonce != request.LeaseNonce)
            return "QualificationObservationLeaseMismatch";
        if (observation.RuntimeEpoch != request.RuntimeEpoch)
            return "QualificationObservationRuntimeEpochMismatch";
        if (observation.Sequence < 1 || observation.Sequence <= previousSequence)
            return "QualificationObservationSequenceNotIncreasing";
        if (!observation.Connected)
            return "QualificationObservationNotConnected";
        if (!observation.LineStopped)
            return "QualificationObservationLineNotStopped";

        var expectedController = targetController
            ? request.Plan.TargetControllerConfiguration.ContentHash
            : request.Plan.TransientControllerConfiguration.ContentHash;
        if (observation.EffectiveControllerConfigurationHash is null ||
            !string.Equals(observation.EffectiveControllerConfigurationHash, expectedController,
                StringComparison.Ordinal))
            return "QualificationObservationControllerMismatch";

        var destinationFailure = ValidateDestinations(request.Plan, observation.Destinations, isolated);
        if (destinationFailure is not null)
            return destinationFailure;

        return null;
    }

    internal static string? ValidateStimulus(QualificationFacilityRequest request,
        QualificationFacilityStimulus stimulus, long previousStimulusSequence)
    {
        if (request is null)
            return "QualificationStimulusRequestMissing";
        if (stimulus is null)
            return "QualificationStimulusMissing";
        if (request.Plan is null)
            return "QualificationStimulusPlanMissing";
        if (request.SessionId == Guid.Empty || request.LeaseNonce == Guid.Empty ||
            request.RuntimeEpoch == Guid.Empty)
            return "QualificationStimulusRequestIdentityInvalid";
        if (previousStimulusSequence < 0)
            return "QualificationStimulusPreviousSequenceInvalid";
        if (stimulus.SessionId != request.SessionId)
            return "QualificationStimulusSessionMismatch";
        if (stimulus.LeaseNonce != request.LeaseNonce)
            return "QualificationStimulusLeaseMismatch";
        if (stimulus.PositiveSequence < 1 || stimulus.PositiveSequence <= previousStimulusSequence)
            return "QualificationStimulusSequenceNotIncreasing";
        if (!request.Plan.ScenarioIds.Contains(stimulus.ScenarioId, StringComparer.Ordinal))
            return "QualificationStimulusScenarioMismatch";
        if (!string.Equals(stimulus.QualificationContextHash, request.Plan.QualificationContextHash,
                StringComparison.Ordinal))
            return "QualificationStimulusContextMismatch";
        if (stimulus.ControllerEpoch == 0)
            return "QualificationStimulusControllerEpochInvalid";
        if (stimulus.CycleSequence == 0)
            return "QualificationStimulusCycleSequenceInvalid";
        return null;
    }

    private static string? ValidateDestinations(StationQualificationPlan plan,
        IReadOnlyList<QualificationDestinationObservation> destinations, bool isolated)
    {
        if (destinations is null || destinations.Count != RequiredDestinationKinds.Length)
            return "QualificationObservationDestinationSetIncomplete";
        if (plan.DestinationBindings is null ||
            plan.DestinationBindings.Count != RequiredDestinationKinds.Length)
            return "QualificationObservationDestinationPlanInvalid";

        var seenKinds = new HashSet<QualificationDestinationKind>();
        foreach (var destination in destinations)
        {
            if (destination is null)
                return "QualificationObservationDestinationMissing";
            if (!Enum.IsDefined(typeof(QualificationDestinationKind), destination.Kind))
                return "QualificationObservationDestinationKindInvalid";
            if (!seenKinds.Add(destination.Kind))
                return "QualificationObservationDestinationDuplicate";

            var expected = plan.DestinationBindings.FirstOrDefault(value => value.Kind == destination.Kind);
            if (expected is null)
                return "QualificationObservationDestinationMissing";
            if (!string.Equals(destination.DestinationId, expected.DestinationId,
                    StringComparison.Ordinal) ||
                !string.Equals(destination.TargetBindingHash, expected.TargetBindingHash,
                    StringComparison.Ordinal))
                return "QualificationObservationDestinationBindingMismatch";

            if (isolated)
            {
                if (destination.Route is not (QualificationDestinationRoute.IsolatedTest or
                    QualificationDestinationRoute.Disconnected))
                    return "QualificationObservationDestinationNotIsolated";
                if (destination.Kind != QualificationDestinationKind.ProductionPlcOutput &&
                    destination.OutputEnabled)
                    return "QualificationObservationDestinationOutputEnabled";
                if (destination.Kind == QualificationDestinationKind.ProductionPlcOutput &&
                    destination.Route == QualificationDestinationRoute.Disconnected &&
                    destination.OutputEnabled)
                    return "QualificationObservationDestinationOutputEnabled";
            }
            else
            {
                if (destination.Route != QualificationDestinationRoute.Production)
                    return "QualificationObservationDestinationNotRestored";
                if (destination.OutputEnabled)
                    return "QualificationObservationDestinationOutputEnabled";
            }
        }

        if (RequiredDestinationKinds.Any(kind => !seenKinds.Contains(kind)))
            return "QualificationObservationDestinationSetIncomplete";
        return null;
    }

    private static bool SameHarness(QualificationHarnessIdentity expected,
        QualificationHarnessIdentity actual)
    {
        return expected is not null && actual is not null &&
            string.Equals(expected.Id, actual.Id, StringComparison.Ordinal) &&
            string.Equals(expected.Version, actual.Version, StringComparison.Ordinal) &&
            string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal) &&
            string.Equals(expected.CoveredPathHash, actual.CoveredPathHash, StringComparison.Ordinal) &&
            expected.DevelopmentOnly == actual.DevelopmentOnly;
    }
}
