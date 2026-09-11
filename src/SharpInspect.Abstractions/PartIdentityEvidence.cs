using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The only two states a validated identity may expose to the production pipeline.</summary>
public enum PartIdentityEvidenceState : byte
{
    Provided = 1,
    NotProvided = 2
}

/// <summary>
/// Immutable runtime-created evidence.  It is deliberately independent of InspectionId;
/// the inspection identity is allocated only after this evidence has been accepted.
/// </summary>
public sealed class PartIdentityEvidence
{
    internal PartIdentityEvidence(PartIdentityEvidenceState state, string? value,
        PartIdentityProviderObservation observation)
    {
        if (!Enum.IsDefined(typeof(PartIdentityEvidenceState), state))
            throw new ArgumentOutOfRangeException(nameof(state));
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        if (state == PartIdentityEvidenceState.Provided)
        {
            if (observation.Status != PartIdentityObservationStatus.Present || value is null)
                throw new ArgumentException("PartIdentityProvidedEvidenceInvalid", nameof(value));
            Value = AlgorithmContractValidation.BoundedText(value, nameof(value), 512);
        }
        else
        {
            if (observation.Status != PartIdentityObservationStatus.Missing || value is not null)
                throw new ArgumentException("PartIdentityNotProvidedEvidenceInvalid", nameof(value));
        }

        State = state;
        BindingId = observation.Binding.BindingId;
        BindingHash = observation.Binding.ContentHash;
        LogicalRole = observation.Binding.LogicalRole;
        FormatHash = observation.Binding.Format.ContentHash;
        SourceKind = observation.Binding.SourceKind;
        ProviderId = observation.Binding.ProviderId;
        ProviderVersion = observation.Binding.ProviderVersion;
        SourceContractHash = observation.Binding.SourceContractHash;
        Cycle = observation.Cycle;
        SourceSequence = observation.SourceSequence;
        SourceEpoch = observation.SourceEpoch;
        SourceGeneration = observation.SourceGeneration;
        ObservedAtUtc = observation.ObservedAtUtc;
        MonotonicTimestamp = observation.MonotonicTimestamp;
        MonotonicFrequency = observation.MonotonicFrequency;
        StageToken = observation.StageToken;
        StablePlcSnapshot = observation.StablePlcSnapshot;
        ObservationHash = observation.ContentHash;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-evidence-v1", state.ToString(), Value,
            BindingId, BindingHash, LogicalRole, FormatHash, SourceKind.ToString(),
            ProviderId, ProviderVersion, SourceContractHash, Cycle.ContentHash,
            SourceSequence.ToString(CultureInfo.InvariantCulture),
            SourceEpoch.ToString("D"), SourceGeneration.ToString(CultureInfo.InvariantCulture),
            ObservedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            MonotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            MonotonicFrequency.ToString(CultureInfo.InvariantCulture),
            StageToken?.ToString("D"),
            StablePlcSnapshot?.ContentHash, ObservationHash
        });
    }

    public PartIdentityEvidenceState State { get; }
    public string? Value { get; }
    public string BindingId { get; }
    public string BindingHash { get; }
    public string LogicalRole { get; }
    public string FormatHash { get; }
    public PartIdentityProviderSourceKind SourceKind { get; }
    public string ProviderId { get; }
    public string ProviderVersion { get; }
    public string SourceContractHash { get; }
    public PartIdentityCycleBinding Cycle { get; }
    public long SourceSequence { get; }
    public Guid SourceEpoch { get; }
    public long SourceGeneration { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public long MonotonicFrequency { get; }
    public Guid? StageToken { get; }
    public PartIdentityStablePlcSnapshot? StablePlcSnapshot { get; }
    public string? ReadEvidenceHash => StablePlcSnapshot?.ReadEvidenceHash;
    public string ObservationHash { get; }
    public PartIdentityProviderObservation Observation { get; }
    public string ContentHash { get; }
}

/// <summary>Outcome of the runtime's independent provider/evidence validation.</summary>
public sealed class PartIdentityValidationResult
{
    internal PartIdentityValidationResult(bool accepted, string reasonCode,
        PartIdentityEvidence? evidence)
    {
        Accepted = accepted;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        Evidence = evidence;
    }

    public bool Accepted { get; }
    public bool Succeeded => Accepted;
    public string ReasonCode { get; }
    public PartIdentityEvidence? Evidence { get; }
}
