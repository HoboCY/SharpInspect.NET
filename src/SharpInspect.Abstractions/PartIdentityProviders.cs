using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace SharpInspect.Abstractions;

/// <summary>The only provider classes which may be bound to a production identity requirement.</summary>
public enum PartIdentityProviderSourceKind : byte
{
    StablePlc = 1,
    Staged = 2
}

/// <summary>A raw provider observation.  The runtime, rather than the provider, decides acceptance.</summary>
public enum PartIdentityObservationStatus : byte
{
    Present = 1,
    Missing = 2,
    Ambiguous = 3,
    Invalid = 4,
    Stale = 5,
    Error = 6
}

/// <summary>
/// A bounded, versioned identity value format.  Values are validated exactly as supplied;
/// this contract never trims, changes case, or otherwise normalizes a part value.
/// </summary>
public sealed class PartIdentityFormat
{
    public PartIdentityFormat(string id, string version, int minimumLength, int maximumLength,
        string allowedCharacters, string? requiredPrefix = null, string? requiredSuffix = null)
    {
        Id = AlgorithmContractValidation.Identifier(id, nameof(id));
        Version = AlgorithmContractValidation.Identifier(version, nameof(version));
        if (minimumLength < 1 || minimumLength > maximumLength || maximumLength > 256)
            throw new ArgumentOutOfRangeException(nameof(minimumLength), "PartIdentityFormatLengthInvalid");

        AllowedCharacters = AlgorithmContractValidation.BoundedText(allowedCharacters,
            nameof(allowedCharacters), 128);
        if (AllowedCharacters.Distinct().Count() != AllowedCharacters.Length)
            throw new ArgumentException("PartIdentityFormatAllowedCharactersDuplicate", nameof(allowedCharacters));
        if (AllowedCharacters.Any(char.IsControl))
            throw new ArgumentException("PartIdentityFormatAllowedCharactersInvalid", nameof(allowedCharacters));

        RequiredPrefix = ValidateAffix(requiredPrefix, nameof(requiredPrefix), maximumLength);
        RequiredSuffix = ValidateAffix(requiredSuffix, nameof(requiredSuffix), maximumLength);
        if (RequiredPrefix is not null && RequiredPrefix.Any(character => AllowedCharacters.IndexOf(character) < 0))
            throw new ArgumentException("PartIdentityFormatAffixCharacterInvalid", nameof(requiredPrefix));
        if (RequiredSuffix is not null && RequiredSuffix.Any(character => AllowedCharacters.IndexOf(character) < 0))
            throw new ArgumentException("PartIdentityFormatAffixCharacterInvalid", nameof(requiredSuffix));
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-format-v1", Id, Version,
            minimumLength.ToString(CultureInfo.InvariantCulture),
            maximumLength.ToString(CultureInfo.InvariantCulture), AllowedCharacters,
            RequiredPrefix, RequiredSuffix
        });
    }

    public string Id { get; }
    public string Version { get; }
    public int MinimumLength { get; }
    public int MaximumLength { get; }
    public string AllowedCharacters { get; }
    public string? RequiredPrefix { get; }
    public string? RequiredSuffix { get; }
    public string ContentHash { get; }

    public RecipeContractReference ToContractReference() =>
        new(Id, Version, ContentHash);

    public bool TryValidate(string? value, out string reasonCode)
    {
        if (value is null)
        {
            reasonCode = "PartIdentityValueMissing";
            return false;
        }
        if (value.Length < MinimumLength || value.Length > MaximumLength)
        {
            reasonCode = "PartIdentityValueLengthInvalid";
            return false;
        }
        if (RequiredPrefix is not null && !value.StartsWith(RequiredPrefix, StringComparison.Ordinal))
        {
            reasonCode = "PartIdentityValuePrefixInvalid";
            return false;
        }
        if (RequiredSuffix is not null && !value.EndsWith(RequiredSuffix, StringComparison.Ordinal))
        {
            reasonCode = "PartIdentityValueSuffixInvalid";
            return false;
        }
        foreach (var character in value)
        {
            if (AllowedCharacters.IndexOf(character) < 0)
            {
                reasonCode = "PartIdentityValueCharacterInvalid";
                return false;
            }
        }

        reasonCode = "PartIdentityValueValid";
        return true;
    }

    private static string? ValidateAffix(string? value, string parameterName, int maximumLength)
    {
        if (value is null) return null;
        var bounded = AlgorithmContractValidation.BoundedText(value, parameterName, maximumLength);
        if (bounded.Length > maximumLength)
            throw new ArgumentOutOfRangeException(parameterName, "PartIdentityFormatAffixInvalid");
        return bounded;
    }
}

/// <summary>
/// Immutable deployment binding.  It identifies exactly one source/provider and its
/// value format; it is not a capability or a successful observation.
/// </summary>
public sealed class PartIdentityProviderBinding
{
    public PartIdentityProviderBinding(string bindingId, string bindingVersion, string logicalRole,
        PartIdentityProviderSourceKind sourceKind, string providerId, string providerVersion,
        string sourceContractHash, PartIdentityFormat format, TimeSpan freshnessLimit,
        TimeSpan latchTimeout, int maximumCallsPerCycle)
    {
        BindingId = AlgorithmContractValidation.Identifier(bindingId, nameof(bindingId));
        BindingVersion = AlgorithmContractValidation.Identifier(bindingVersion, nameof(bindingVersion));
        LogicalRole = AlgorithmContractValidation.Identifier(logicalRole, nameof(logicalRole));
        SourceKind = ValidateEnum(sourceKind, nameof(sourceKind));
        ProviderId = AlgorithmContractValidation.Identifier(providerId, nameof(providerId));
        ProviderVersion = AlgorithmContractValidation.Identifier(providerVersion, nameof(providerVersion));
        SourceContractHash = RecipeActivationValidation.Hash(sourceContractHash, nameof(sourceContractHash));
        Format = format ?? throw new ArgumentNullException(nameof(format));
        if (freshnessLimit <= TimeSpan.Zero || freshnessLimit > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(freshnessLimit), "PartIdentityFreshnessInvalid");
        if (latchTimeout <= TimeSpan.Zero || latchTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(latchTimeout), "PartIdentityLatchTimeoutInvalid");
        if (maximumCallsPerCycle < 1 || maximumCallsPerCycle > 8)
            throw new ArgumentOutOfRangeException(nameof(maximumCallsPerCycle), "PartIdentityCallLimitInvalid");
        FreshnessLimit = freshnessLimit;
        LatchTimeout = latchTimeout;
        MaximumCallsPerCycle = maximumCallsPerCycle;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-provider-binding-v1", BindingId, BindingVersion,
            LogicalRole, SourceKind.ToString(), ProviderId, ProviderVersion, SourceContractHash,
            Format.ContentHash, freshnessLimit.Ticks.ToString(CultureInfo.InvariantCulture),
            latchTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            maximumCallsPerCycle.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string BindingId { get; }
    public string BindingVersion { get; }
    public string LogicalRole { get; }
    public PartIdentityProviderSourceKind SourceKind { get; }
    public string ProviderId { get; }
    public string ProviderVersion { get; }
    public string SourceContractHash { get; }
    public PartIdentityFormat Format { get; }
    public TimeSpan FreshnessLimit { get; }
    public TimeSpan LatchTimeout { get; }
    public int MaximumCallsPerCycle { get; }
    public string ContentHash { get; }

    private static PartIdentityProviderSourceKind ValidateEnum(
        PartIdentityProviderSourceKind value, string parameterName)
    {
        if (!Enum.IsDefined(typeof(PartIdentityProviderSourceKind), value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}

/// <summary>Provider readiness metadata; it contains no acceptance decision.</summary>
public sealed class PartIdentityProviderCapabilities
{
    public PartIdentityProviderCapabilities(bool ready, bool canLatch,
        PartIdentityProviderSourceKind sourceKind, int maximumCallsPerCycle, string bindingHash,
        Guid sourceEpoch, long sourceGeneration)
    {
        if (!Enum.IsDefined(typeof(PartIdentityProviderSourceKind), sourceKind))
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        if (maximumCallsPerCycle < 1 || maximumCallsPerCycle > 8)
            throw new ArgumentOutOfRangeException(nameof(maximumCallsPerCycle));
        if (sourceEpoch == Guid.Empty) throw new ArgumentException("PartIdentitySourceEpochRequired", nameof(sourceEpoch));
        if (sourceGeneration < 1) throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        BindingHash = RecipeActivationValidation.Hash(bindingHash, nameof(bindingHash));
        Ready = ready;
        CanLatch = canLatch;
        SourceKind = sourceKind;
        MaximumCallsPerCycle = maximumCallsPerCycle;
        SourceEpoch = sourceEpoch;
        SourceGeneration = sourceGeneration;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-provider-capabilities-v1", ready ? "1" : "0",
            canLatch ? "1" : "0", sourceKind.ToString(),
            maximumCallsPerCycle.ToString(CultureInfo.InvariantCulture), BindingHash,
            sourceEpoch.ToString("D"), sourceGeneration.ToString(CultureInfo.InvariantCulture)
        });
    }

    public bool Ready { get; }
    public bool CanLatch { get; }
    public PartIdentityProviderSourceKind SourceKind { get; }
    public int MaximumCallsPerCycle { get; }
    public string BindingHash { get; }
    public Guid SourceEpoch { get; }
    public long SourceGeneration { get; }
    public string ContentHash { get; }
}

/// <summary>Notification that a mutable provider source epoch or generation changed.</summary>
public sealed class PartIdentitySourceChangedEventArgs : EventArgs
{
    public PartIdentitySourceChangedEventArgs(PartIdentityProviderCapabilities capabilities)
    {
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public PartIdentityProviderCapabilities Capabilities { get; }
}

/// <summary>The runtime-owned identity of the physical cycle being observed.</summary>
public sealed class PartIdentityCycleBinding
{
    public PartIdentityCycleBinding(Guid runtimeEpoch, string endpointBindingHash,
        long connectionGeneration, uint controllerEpoch, uint cycleSequence)
    {
        if (runtimeEpoch == Guid.Empty) throw new ArgumentException("PartIdentityRuntimeEpochRequired", nameof(runtimeEpoch));
        RuntimeEpoch = runtimeEpoch;
        EndpointBindingHash = RecipeActivationValidation.Hash(endpointBindingHash, nameof(endpointBindingHash));
        if (connectionGeneration < 0) throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        if (controllerEpoch == 0) throw new ArgumentOutOfRangeException(nameof(controllerEpoch));
        if (cycleSequence == 0) throw new ArgumentOutOfRangeException(nameof(cycleSequence));
        ConnectionGeneration = connectionGeneration;
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-cycle-binding-v1", runtimeEpoch.ToString("D"),
            EndpointBindingHash, connectionGeneration.ToString(CultureInfo.InvariantCulture),
            controllerEpoch.ToString(CultureInfo.InvariantCulture),
            cycleSequence.ToString(CultureInfo.InvariantCulture)
        });
    }

    public Guid RuntimeEpoch { get; }
    public string EndpointBindingHash { get; }
    public long ConnectionGeneration { get; }
    public uint ControllerEpoch { get; }
    public uint CycleSequence { get; }
    public string ContentHash { get; }

    public bool Matches(PartIdentityCycleBinding other) => other is not null &&
        RuntimeEpoch == other.RuntimeEpoch &&
        string.Equals(EndpointBindingHash, other.EndpointBindingHash, StringComparison.Ordinal) &&
        ConnectionGeneration == other.ConnectionGeneration &&
        ControllerEpoch == other.ControllerEpoch && CycleSequence == other.CycleSequence;
}

/// <summary>
/// A stable PLC read proof.  The raw bytes are retained so a validator can reject malformed
/// UTF-8 instead of trusting a decoded string supplied by a caller.
/// </summary>
public sealed class PartIdentityStablePlcSnapshot
{
    public PartIdentityStablePlcSnapshot(PartIdentityCycleBinding cycle, string sourceContractHash,
        uint revision, ushort state, PartIdentityObservationStatus status,
        IEnumerable<byte>? rawUtf8Bytes, long sourceSequence, DateTimeOffset observedAtUtc,
        long monotonicTimestamp, long monotonicFrequency, string readEvidenceHash)
    {
        Cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        SourceContractHash = RecipeActivationValidation.Hash(sourceContractHash, nameof(sourceContractHash));
        if (revision == 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(typeof(PartIdentityObservationStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (sourceSequence <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSequence));
        if (observedAtUtc == default) throw new ArgumentException("PartIdentityTimestampRequired", nameof(observedAtUtc));
        if (monotonicTimestamp <= 0) throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
        if (monotonicFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));
        ReadEvidenceHash = RecipeActivationValidation.Hash(readEvidenceHash, nameof(readEvidenceHash));
        var copied = CopyBytes(rawUtf8Bytes);
        Revision = revision;
        State = state;
        Status = status;
        RawUtf8Bytes = copied;
        SourceSequence = sourceSequence;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        MonotonicTimestamp = monotonicTimestamp;
        MonotonicFrequency = monotonicFrequency;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-stable-plc-snapshot-v1", Cycle.ContentHash,
            SourceContractHash, revision.ToString(CultureInfo.InvariantCulture),
            state.ToString(CultureInfo.InvariantCulture), status.ToString(),
            Convert.ToHexString(copied.ToArray()), sourceSequence.ToString(CultureInfo.InvariantCulture),
            ObservedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            monotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            monotonicFrequency.ToString(CultureInfo.InvariantCulture), ReadEvidenceHash
        });
    }

    public PartIdentityCycleBinding Cycle { get; }
    public string SourceContractHash { get; }
    public uint Revision { get; }
    public ushort State { get; }
    public PartIdentityObservationStatus Status { get; }
    public ReadOnlyCollection<byte> RawUtf8Bytes { get; }
    public long SourceSequence { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public long MonotonicFrequency { get; }
    public string ReadEvidenceHash { get; }
    public string ContentHash { get; }

    public byte[] GetRawUtf8Bytes() => RawUtf8Bytes.ToArray();

    public bool TryGetUtf8Value(out string? value)
    {
        try
        {
            value = new UTF8Encoding(false, true).GetString(RawUtf8Bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = null;
            return false;
        }
    }

    private static ReadOnlyCollection<byte> CopyBytes(IEnumerable<byte>? values)
    {
        if (values is null) return new ReadOnlyCollection<byte>(Array.Empty<byte>());
        var result = values.ToArray();
        if (result.Length > 4096)
            throw new ArgumentException("PartIdentityRawEvidenceTooLarge", nameof(values));
        return new ReadOnlyCollection<byte>(result);
    }
}

/// <summary>A runtime-owned call to one already bound provider.  It has no InspectionId.</summary>
public sealed class PartIdentityLatchRequest
{
    public PartIdentityLatchRequest(PartIdentityProviderBinding binding, PartIdentityCycleBinding cycle,
        int providerCallOrdinal, Guid sourceEpoch, long sourceGeneration, Guid? stageToken = null,
        PartIdentityStablePlcSnapshot? stablePlcSnapshot = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        if (providerCallOrdinal < 1 || providerCallOrdinal > binding.MaximumCallsPerCycle)
            throw new ArgumentOutOfRangeException(nameof(providerCallOrdinal), "PartIdentityCallOrdinalInvalid");
        if (sourceEpoch == Guid.Empty)
            throw new ArgumentException("PartIdentitySourceEpochRequired", nameof(sourceEpoch));
        if (sourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        if (binding.SourceKind == PartIdentityProviderSourceKind.Staged)
        {
            if (stageToken == Guid.Empty)
                throw new ArgumentException("PartIdentityStageTokenInvalid", nameof(stageToken));
            if (stablePlcSnapshot is not null)
                throw new ArgumentException("PartIdentityStagedCannotCarryPlcProof", nameof(stablePlcSnapshot));
        }
        else
        {
            if (stageToken is not null)
                throw new ArgumentException("PartIdentityPlcCannotCarryStageToken", nameof(stageToken));
            if (stablePlcSnapshot is null)
                throw new ArgumentException("PartIdentityPlcProofRequired", nameof(stablePlcSnapshot));
        }
        if (stablePlcSnapshot is not null && !stablePlcSnapshot.Cycle.Matches(cycle))
            throw new ArgumentException("PartIdentityPlcCycleMismatch", nameof(stablePlcSnapshot));
        ProviderCallOrdinal = providerCallOrdinal;
        SourceEpoch = sourceEpoch;
        SourceGeneration = sourceGeneration;
        StageToken = stageToken;
        StablePlcSnapshot = stablePlcSnapshot;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-latch-request-v1", binding.ContentHash,
            cycle.ContentHash, providerCallOrdinal.ToString(CultureInfo.InvariantCulture),
            sourceEpoch.ToString("D"), sourceGeneration.ToString(CultureInfo.InvariantCulture),
            stageToken?.ToString("D"), stablePlcSnapshot?.ContentHash
        });
    }

    public PartIdentityProviderBinding Binding { get; }
    public PartIdentityCycleBinding Cycle { get; }
    public int ProviderCallOrdinal { get; }
    public Guid SourceEpoch { get; }
    public long SourceGeneration { get; }
    public Guid? StageToken { get; }
    public PartIdentityStablePlcSnapshot? StablePlcSnapshot { get; }
    public string ContentHash { get; }
}

/// <summary>Immutable raw observation returned by a provider before runtime validation.</summary>
public sealed class PartIdentityProviderObservation
{
    public PartIdentityProviderObservation(PartIdentityProviderBinding binding,
        PartIdentityCycleBinding cycle, PartIdentityObservationStatus status, string? value,
        string reasonCode, long sourceSequence, DateTimeOffset observedAtUtc,
        long monotonicTimestamp, long monotonicFrequency, Guid sourceEpoch, long sourceGeneration,
        Guid? stageToken = null,
        PartIdentityStablePlcSnapshot? stablePlcSnapshot = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        if (!Enum.IsDefined(typeof(PartIdentityObservationStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        if (value is not null)
            Value = AlgorithmContractValidation.BoundedText(value, nameof(value), 512);
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        if (sourceSequence <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSequence));
        if (observedAtUtc == default) throw new ArgumentException("PartIdentityTimestampRequired", nameof(observedAtUtc));
        if (monotonicTimestamp <= 0) throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
        if (monotonicFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));
        if (sourceEpoch == Guid.Empty)
            throw new ArgumentException("PartIdentitySourceEpochRequired", nameof(sourceEpoch));
        if (sourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        if (binding.SourceKind == PartIdentityProviderSourceKind.Staged && stablePlcSnapshot is not null)
            throw new ArgumentException("PartIdentityStagedCannotCarryPlcProof", nameof(stablePlcSnapshot));
        if (binding.SourceKind == PartIdentityProviderSourceKind.StablePlc && stageToken is not null)
            throw new ArgumentException("PartIdentityPlcCannotCarryStageToken", nameof(stageToken));
        if (stablePlcSnapshot is not null && !stablePlcSnapshot.Cycle.Matches(cycle))
            throw new ArgumentException("PartIdentityPlcCycleMismatch", nameof(stablePlcSnapshot));
        if (stablePlcSnapshot is not null && !string.Equals(
                stablePlcSnapshot.SourceContractHash, binding.SourceContractHash, StringComparison.Ordinal))
            throw new ArgumentException("PartIdentityPlcSourceContractMismatch", nameof(stablePlcSnapshot));
        if (status == PartIdentityObservationStatus.Present &&
            binding.SourceKind == PartIdentityProviderSourceKind.Staged && stageToken is null)
            throw new ArgumentException("PartIdentityStageTokenRequired", nameof(stageToken));

        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        SourceSequence = sourceSequence;
        SourceEpoch = sourceEpoch;
        SourceGeneration = sourceGeneration;
        MonotonicTimestamp = monotonicTimestamp;
        MonotonicFrequency = monotonicFrequency;
        StageToken = stageToken;
        StablePlcSnapshot = stablePlcSnapshot;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-provider-observation-v1", binding.ContentHash,
            cycle.ContentHash, status.ToString(), Value, ReasonCode,
            sourceSequence.ToString(CultureInfo.InvariantCulture),
            sourceEpoch.ToString("D"), sourceGeneration.ToString(CultureInfo.InvariantCulture),
            ObservedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            monotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            monotonicFrequency.ToString(CultureInfo.InvariantCulture), stageToken?.ToString("D"),
            stablePlcSnapshot?.ContentHash
        });
    }

    public PartIdentityProviderBinding Binding { get; }
    public PartIdentityCycleBinding Cycle { get; }
    public PartIdentityObservationStatus Status { get; }
    public string? Value { get; }
    public string ReasonCode { get; }
    public long SourceSequence { get; }
    public Guid SourceEpoch { get; }
    public long SourceGeneration { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public long MonotonicFrequency { get; }
    public Guid? StageToken { get; }
    public PartIdentityStablePlcSnapshot? StablePlcSnapshot { get; }
    public string ContentHash { get; }
}

public interface IPartIdentityProvider
{
    PartIdentityProviderBinding Binding { get; }
    PartIdentityProviderCapabilities Capabilities { get; }
    event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged;
    ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default);
    ValueTask<PartIdentityProviderObservation> TryLatchAsync(
        PartIdentityLatchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>An explicit stage operation; it is an input to a single-use staged provider.</summary>
public sealed class PartIdentityStageRequest
{
    public PartIdentityStageRequest(Guid stageToken, PartIdentityCycleBinding cycle, string value)
    {
        if (stageToken == Guid.Empty) throw new ArgumentException("PartIdentityStageTokenRequired", nameof(stageToken));
        StageToken = stageToken;
        Cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        Value = AlgorithmContractValidation.BoundedText(value, nameof(value), 512);
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-stage-request-v1", stageToken.ToString("D"),
            cycle.ContentHash, Value
        });
    }

    public Guid StageToken { get; }
    public PartIdentityCycleBinding Cycle { get; }
    public string Value { get; }
    public string ContentHash { get; }
}

public sealed class PartIdentityStageResult
{
    internal PartIdentityStageResult(bool accepted, string reasonCode, Guid stageToken)
    {
        Accepted = accepted;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        StageToken = stageToken;
    }

    public bool Accepted { get; }
    public string ReasonCode { get; }
    public Guid StageToken { get; }
}

/// <summary>
/// Development-controlled in-memory staged provider.  Stage tokens are atomically
/// compare-and-consumed; no value is replayed after a successful latch.
/// </summary>
