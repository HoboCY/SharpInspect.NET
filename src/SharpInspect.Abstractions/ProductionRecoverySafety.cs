using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The only observations which a recovery safety provider may publish.  A provider
/// never publishes Runtime Ready/Busy state as a substitute for physical stop evidence.
/// </summary>
public enum ProductionRecoverySafetyObservationStatus : byte
{
    SafeLineStopped = 1,
    LineNotStopped = 2,
    Unavailable = 3,
    Stale = 4,
    Invalid = 5
}

/// <summary>Current identity and availability of the external safety source.</summary>
public sealed class ProductionRecoverySafetySourceState
{
    public ProductionRecoverySafetySourceState(Guid sourceEpoch, long sourceGeneration, bool available)
    {
        if (sourceEpoch == Guid.Empty)
            throw new ArgumentException("ProductionRecoverySafetySourceEpochRequired", nameof(sourceEpoch));
        if (sourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceGeneration));

        SourceEpoch = sourceEpoch;
        SourceGeneration = sourceGeneration;
        Available = available;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-safety-source-state-v1",
            sourceEpoch.ToString("D"),
            sourceGeneration.ToString(CultureInfo.InvariantCulture),
            available ? "1" : "0"
        });
    }

    public Guid SourceEpoch { get; }
    public long SourceGeneration { get; }
    public bool Available { get; }
    public string ContentHash { get; }

    public bool Matches(ProductionRecoverySafetySourceState other) => other is not null &&
        SourceEpoch == other.SourceEpoch && SourceGeneration == other.SourceGeneration &&
        Available == other.Available;
}

/// <summary>Source invalidation notification; it is not a safe-stop decision.</summary>
public sealed class ProductionRecoverySafetySourceChangedEventArgs : EventArgs
{
    public ProductionRecoverySafetySourceChangedEventArgs(
        ProductionRecoverySafetySourceState source, string reasonCode)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
    }

    public ProductionRecoverySafetySourceState Source { get; }
    public string ReasonCode { get; }
}

/// <summary>
/// Immutable deployment binding for one physical recovery safety source.  The
/// assembly hash is the existing runtime binary hash contract; it is checked again
/// against the actual provider type when the registry is created.
/// </summary>
public sealed class ProductionRecoverySafetyProviderBinding
{
    public ProductionRecoverySafetyProviderBinding(
        string bindingId,
        string bindingVersion,
        string stationId,
        string endpointBindingHash,
        string sourceId,
        string sourceVersion,
        string sourceConfigurationHash,
        string providerTypeName,
        string providerAssemblyHash,
        TimeSpan freshnessLimit)
    {
        BindingId = AlgorithmContractValidation.Identifier(bindingId, nameof(bindingId));
        BindingVersion = AlgorithmContractValidation.Identifier(bindingVersion, nameof(bindingVersion));
        StationId = AlgorithmContractValidation.Identifier(stationId, nameof(stationId));
        EndpointBindingHash = RecipeActivationValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        SourceId = AlgorithmContractValidation.Identifier(sourceId, nameof(sourceId));
        SourceVersion = AlgorithmContractValidation.Identifier(sourceVersion, nameof(sourceVersion));
        SourceConfigurationHash = RecipeActivationValidation.Hash(sourceConfigurationHash,
            nameof(sourceConfigurationHash));
        ProviderTypeName = AlgorithmContractValidation.BoundedText(providerTypeName,
            nameof(providerTypeName), 1024);
        if (string.IsNullOrWhiteSpace(ProviderTypeName))
            throw new ArgumentException("ProductionRecoverySafetyProviderTypeRequired", nameof(providerTypeName));
        ProviderAssemblyHash = RecipeActivationValidation.Hash(providerAssemblyHash,
            nameof(providerAssemblyHash));
        if (freshnessLimit <= TimeSpan.Zero || freshnessLimit >= TimeSpan.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(freshnessLimit),
                "ProductionRecoverySafetyFreshnessInvalid");
        FreshnessLimit = freshnessLimit;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-safety-provider-binding-v1", BindingId,
            BindingVersion, StationId, EndpointBindingHash, SourceId, SourceVersion,
            SourceConfigurationHash, ProviderTypeName, ProviderAssemblyHash,
            freshnessLimit.Ticks.ToString(CultureInfo.InvariantCulture)
        });
    }

    public ProductionRecoverySafetyProviderBinding(
        string bindingId,
        string bindingVersion,
        string stationId,
        string endpointBindingHash,
        string sourceId,
        string sourceVersion,
        string sourceConfigurationHash,
        Type providerType,
        string providerAssemblyHash,
        TimeSpan freshnessLimit)
        : this(bindingId, bindingVersion, stationId, endpointBindingHash, sourceId, sourceVersion,
            sourceConfigurationHash,
            (providerType ?? throw new ArgumentNullException(nameof(providerType))).AssemblyQualifiedName
                ?? providerType.FullName ?? providerType.Name,
            providerAssemblyHash, freshnessLimit)
    { }

    public string BindingId { get; }
    public string BindingVersion { get; }
    public string StationId { get; }
    public string EndpointBindingHash { get; }
    public string SourceId { get; }
    public string SourceVersion { get; }
    public string SourceConfigurationHash { get; }
    public string ProviderTypeName { get; }
    public string ProviderAssemblyHash { get; }
    public TimeSpan FreshnessLimit { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Raw evidence from a registered safety observer.  Only the physical line state
/// is represented; software handshake flags cannot satisfy this contract.
/// </summary>
public sealed class ProductionRecoverySafetyObservation
{
    public ProductionRecoverySafetyObservation(
        ProductionRecoverySafetyProviderBinding binding,
        ProductionRecoverySafetyObservationStatus status,
        string reasonCode,
        Guid sourceEpoch,
        long sourceGeneration,
        DateTimeOffset observedAtUtc,
        long monotonicTimestamp,
        long monotonicFrequency)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (!Enum.IsDefined(typeof(ProductionRecoverySafetyObservationStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (sourceEpoch == Guid.Empty)
            throw new ArgumentException("ProductionRecoverySafetySourceEpochRequired", nameof(sourceEpoch));
        if (sourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        if (observedAtUtc == default)
            throw new ArgumentException("ProductionRecoverySafetyTimestampRequired", nameof(observedAtUtc));
        if (monotonicTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
        if (monotonicFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(monotonicFrequency));

        Status = status;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        SourceEpoch = sourceEpoch;
        SourceGeneration = sourceGeneration;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        MonotonicTimestamp = monotonicTimestamp;
        MonotonicFrequency = monotonicFrequency;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-safety-observation-v1", binding.ContentHash,
            status.ToString(), ReasonCode, sourceEpoch.ToString("D"),
            sourceGeneration.ToString(CultureInfo.InvariantCulture),
            ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            monotonicTimestamp.ToString(CultureInfo.InvariantCulture),
            monotonicFrequency.ToString(CultureInfo.InvariantCulture)
        });
    }

    public ProductionRecoverySafetyProviderBinding Binding { get; }
    public ProductionRecoverySafetyObservationStatus Status { get; }
    public string ReasonCode { get; }
    public Guid SourceEpoch { get; }
    public long SourceGeneration { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public long MonotonicTimestamp { get; }
    public long MonotonicFrequency { get; }
    public bool LineStopped => Status == ProductionRecoverySafetyObservationStatus.SafeLineStopped;
    // Existing ProductionRecoverySafetyEvidence consumes this established name;
    // keep it as a compatibility projection of the canonical LineStopped property.
    public bool IsSafeLineStopped => LineStopped;
    public string ContentHash { get; }
}

/// <summary>
/// A provider owns the source observation mechanics.  It cannot grant recovery by
/// itself: the Runtime registry validates the binding, freshness and current source.
/// </summary>
public interface IProductionRecoverySafetyProvider
{
    ProductionRecoverySafetyProviderBinding Binding { get; }

    /// <summary>
    /// The provider's current source epoch, generation, and availability.  A
    /// provider must linearize a source transition by synchronously raising
    /// <see cref="SourceChanged"/> before this property can expose the new
    /// state.  The registry uses that notification as the invalidation point;
    /// it never treats a late property read as a final physical-send fence.
    /// </summary>
    ProductionRecoverySafetySourceState Source { get; }

    /// <summary>
    /// Synchronous source invalidation notification.  The registry updates its
    /// cached source and revision before forwarding the notification to its
    /// consumers.  Implementations must complete all subscribed handlers before
    /// making the new source state observable through <see cref="Source"/>.
    /// </summary>
    event EventHandler<ProductionRecoverySafetySourceChangedEventArgs>? SourceChanged;
    ValueTask<ProductionRecoverySafetyObservation> ObserveAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Validated capture returned by the Runtime safety registry.</summary>
public sealed class ProductionRecoverySafetyCapture
{
    internal ProductionRecoverySafetyCapture(
        Guid runtimeEpoch,
        bool available,
        string reasonCode,
        long revision,
        ProductionRecoverySafetyObservation? observation,
        ProductionRecoverySafetySourceState source)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("ProductionRecoverySafetyRuntimeEpochRequired", nameof(runtimeEpoch));
        RuntimeEpoch = runtimeEpoch;
        Available = available;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        Revision = revision;
        Observation = observation;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-safety-capture-v1", runtimeEpoch.ToString("D"),
            available ? "1" : "0", ReasonCode, revision.ToString(CultureInfo.InvariantCulture),
            observation?.ContentHash, source.ContentHash
        });
    }

    public Guid RuntimeEpoch { get; }
    public bool Available { get; }
    public string ReasonCode { get; }
    public long Revision { get; }
    public ProductionRecoverySafetyObservation? Observation { get; }
    public ProductionRecoverySafetySourceState Source { get; }
    public string ContentHash { get; }
}
