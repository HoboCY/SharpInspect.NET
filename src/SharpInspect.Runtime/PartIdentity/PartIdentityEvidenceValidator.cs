using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.PartIdentity;

/// <summary>
/// Validates provider output at the runtime boundary.  A provider can supply observations,
/// but it cannot make them accepted evidence.
/// </summary>
internal static class PartIdentityEvidenceValidator
{
    internal static PartIdentityValidationResult Validate(
        PartIdentityRequirement requirement,
        PartIdentityProviderBinding? binding,
        PartIdentityProviderCapabilities? capabilities,
        PartIdentityLatchRequest? request,
        PartIdentityProviderObservation? observation,
        DateTimeOffset nowUtc,
        long nowMonotonicTimestamp,
        long monotonicFrequency,
        long previousSourceSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        _ = nowUtc; // Freshness is monotonic; wall-clock rollback must not change acceptance.

        if (requirement.Mode == PartIdentityRequirementMode.None)
        {
            if (binding is not null || capabilities is not null || request is not null || observation is not null)
                return Reject("PartIdentityProviderForbidden");
            return Accept("PartIdentityNotRequired", null);
        }

        if (binding is null) return Reject("PartIdentityProviderBindingMissing");
        if (capabilities is null) return Reject("PartIdentityProviderCapabilitiesMissing");
        if (request is null) return Reject("PartIdentityLatchRequestMissing");
        if (observation is null) return Reject("PartIdentityObservationMissing");
        if (requirement.LogicalRole is null || requirement.Format is null)
            return Reject("PartIdentityRequirementSourceMissing");
        if (!string.Equals(binding.LogicalRole, requirement.LogicalRole, StringComparison.Ordinal))
            return Reject("PartIdentityLogicalRoleMismatch");
        if (!string.Equals(binding.Format.ContentHash, requirement.Format.ContentHash,
                StringComparison.Ordinal))
            return Reject("PartIdentityFormatMismatch");
        if (!string.Equals(capabilities.BindingHash, binding.ContentHash, StringComparison.Ordinal) ||
            capabilities.SourceKind != binding.SourceKind ||
            capabilities.MaximumCallsPerCycle != binding.MaximumCallsPerCycle)
            return Reject("PartIdentityProviderCapabilitiesMismatch");
        if (!capabilities.Ready || !capabilities.CanLatch)
            return Reject("PartIdentityProviderUnavailable");
        if (!string.Equals(request.Binding.ContentHash, binding.ContentHash, StringComparison.Ordinal))
            return Reject("PartIdentityRequestBindingMismatch");
        if (!string.Equals(observation.Binding.ContentHash, binding.ContentHash, StringComparison.Ordinal))
            return Reject("PartIdentityObservationBindingMismatch");
        if (!request.Cycle.Matches(observation.Cycle))
            return Reject("PartIdentityCycleMismatch");
        if (request.SourceEpoch != capabilities.SourceEpoch ||
            request.SourceGeneration != capabilities.SourceGeneration)
            return Reject("PartIdentityRequestSourceGenerationMismatch");
        if (observation.SourceEpoch != capabilities.SourceEpoch ||
            observation.SourceGeneration != capabilities.SourceGeneration)
            return Reject("PartIdentityObservationSourceGenerationMismatch");

        if (request.ProviderCallOrdinal < 1 || request.ProviderCallOrdinal > binding.MaximumCallsPerCycle)
            return Reject("PartIdentityCallOrdinalInvalid");
        if (observation.SourceSequence <= 0 ||
            (previousSourceSequence > 0 && observation.SourceSequence <= previousSourceSequence))
            return Reject("PartIdentitySourceSequenceNotIncreasing");
        if (nowMonotonicTimestamp <= 0 || monotonicFrequency <= 0)
            return Reject("PartIdentityClockInvalid");
        if (observation.MonotonicFrequency != monotonicFrequency)
            return Reject("PartIdentityMonotonicFrequencyMismatch");
        if (observation.MonotonicTimestamp > nowMonotonicTimestamp)
            return Reject("PartIdentityObservationFromFuture");
        var ageSeconds = (nowMonotonicTimestamp - observation.MonotonicTimestamp) /
            (double)monotonicFrequency;
        if (ageSeconds > binding.FreshnessLimit.TotalSeconds)
            return Reject("PartIdentityObservationStale");

        var sourceResult = ValidateSourceProof(binding, request, observation);
        if (sourceResult is not null) return Reject(sourceResult);

        switch (observation.Status)
        {
            case PartIdentityObservationStatus.Missing:
                if (observation.Value is not null)
                    return Reject("PartIdentityMissingValuePresent");
                if (requirement.Mode == PartIdentityRequirementMode.Required)
                    return Reject("PartIdentityRequiredMissing");
                return Accept("PartIdentityNotProvided", new PartIdentityEvidence(
                    PartIdentityEvidenceState.NotProvided, null, observation));
            case PartIdentityObservationStatus.Present:
                if (observation.Value is null)
                    return Reject("PartIdentityValueMissing");
                if (!binding.Format.TryValidate(observation.Value, out var formatReason))
                    return Reject(formatReason);
                return Accept("PartIdentityProvided", new PartIdentityEvidence(
                    PartIdentityEvidenceState.Provided, observation.Value, observation));
            case PartIdentityObservationStatus.Ambiguous:
                return Reject("PartIdentityObservationAmbiguous");
            case PartIdentityObservationStatus.Invalid:
                return Reject("PartIdentityObservationInvalid");
            case PartIdentityObservationStatus.Stale:
                return Reject("PartIdentityObservationStale");
            case PartIdentityObservationStatus.Error:
                return Reject("PartIdentityObservationError");
            default:
                return Reject("PartIdentityObservationStatusInvalid");
        }
    }

    private static string? ValidateSourceProof(PartIdentityProviderBinding binding,
        PartIdentityLatchRequest request, PartIdentityProviderObservation observation)
    {
        if (binding.SourceKind == PartIdentityProviderSourceKind.Staged)
        {
            if (request.StablePlcSnapshot is not null || observation.StablePlcSnapshot is not null)
                return "PartIdentityStagedCannotCarryPlcProof";
            if (request.StageToken.HasValue && request.StageToken.Value == Guid.Empty)
                return "PartIdentityStageTokenInvalid";
            if (request.StageToken.HasValue && observation.StageToken != request.StageToken)
                return "PartIdentityStageTokenMismatch";
            if (!request.StageToken.HasValue && observation.Status == PartIdentityObservationStatus.Present &&
                !observation.StageToken.HasValue)
                return "PartIdentityStageTokenRequired";
            return null;
        }

        if (request.StageToken is not null || observation.StageToken is not null)
            return "PartIdentityPlcCannotCarryStageToken";
        var requested = request.StablePlcSnapshot;
        var observed = observation.StablePlcSnapshot;
        if (requested is null || observed is null)
            return "PartIdentityPlcProofMissing";
        if (!string.Equals(requested.ContentHash, observed.ContentHash, StringComparison.Ordinal))
            return "PartIdentityPlcProofMismatch";
        if (!requested.Cycle.Matches(request.Cycle) || !observed.Cycle.Matches(request.Cycle))
            return "PartIdentityPlcCycleMismatch";
        if (!string.Equals(requested.SourceContractHash, binding.SourceContractHash,
                StringComparison.Ordinal) || !string.Equals(observed.SourceContractHash,
                binding.SourceContractHash, StringComparison.Ordinal))
            return "PartIdentityPlcSourceContractMismatch";
        if ((requested.Revision & 1) != 0 || (observed.Revision & 1) != 0)
            return "PartIdentityPlcRevisionUnstable";
        if (requested.Status != observation.Status || observed.Status != observation.Status)
            return "PartIdentityPlcStatusMismatch";
        if (requested.SourceSequence != observation.SourceSequence ||
            observed.SourceSequence != observation.SourceSequence ||
            requested.MonotonicTimestamp != observation.MonotonicTimestamp ||
            observed.MonotonicTimestamp != observation.MonotonicTimestamp ||
            requested.MonotonicFrequency != observation.MonotonicFrequency ||
            observed.MonotonicFrequency != observation.MonotonicFrequency ||
            requested.ObservedAtUtc != observation.ObservedAtUtc ||
            observed.ObservedAtUtc != observation.ObservedAtUtc)
            return "PartIdentityPlcTimingMismatch";
        if (!observed.TryGetUtf8Value(out var decoded))
            return "PartIdentityStablePlcEncodingInvalid";
        if (observation.Status == PartIdentityObservationStatus.Missing)
        {
            if (observed.RawUtf8Bytes.Count != 0 || decoded is not "")
                return "PartIdentityMissingRawValuePresent";
        }
        else if (!string.Equals(decoded, observation.Value, StringComparison.Ordinal))
        {
            return "PartIdentityStablePlcValueMismatch";
        }
        return null;
    }

    private static PartIdentityValidationResult Reject(string reasonCode) =>
        new(false, reasonCode, null);

    private static PartIdentityValidationResult Accept(string reasonCode,
        PartIdentityEvidence? evidence) => new(true, reasonCode, evidence);
}
