using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The optional schema-37 production-outbox recovery extension. It bounds the governed manual
/// recovery allowance (fresh attempts per operation and cumulatively per delivery), the number of
/// immutable recovery operations and corrective deliveries one store may carry, and the explicit
/// corrective payload size. Its exact bounds are hashed into the immutable schema-37
/// configuration row, so a store never opens against a different recovery budget. Supplying this
/// value to <see cref="ProductionOutboxStoreOptions.ManualRecovery"/> is the only way to select
/// the extension; without it the store keeps the exact schema-36 behavior.
/// </summary>
public sealed class ProductionOutboxRecoveryOptions
{
    internal const int SchemaVersion = 37;
    internal const int FormatVersion = 1;
    internal const int MaximumGrantedAttemptsHardLimit = 16;
    internal const int MaximumCumulativeGrantedAttemptsHardLimit = 32;
    internal const int MaximumRecoveryOperationsHardLimit = 256;
    internal const int MaximumCorrectionsHardLimit = 256;
    internal const long MaximumCorrectionPayloadBytesHardLimit = 8L * 1024 * 1024;

    /// <summary>Fresh automatically-sendable attempts granted by one recovery operation.</summary>
    public int MaximumGrantedAttempts { get; init; } = 5;

    /// <summary>
    /// Cumulative bound of all recovery grants for one delivery. The delivery's original attempt
    /// budget plus every grant may never exceed the hard total bound; earlier facts stay intact.
    /// </summary>
    public int MaximumCumulativeGrantedAttempts { get; init; } = 16;

    /// <summary>Maximum immutable recovery operations the store may record.</summary>
    public int MaximumRecoveryOperations { get; init; } = 64;

    /// <summary>Maximum immutable corrective deliveries the store may record.</summary>
    public int MaximumCorrections { get; init; } = 64;

    /// <summary>Maximum explicit final bytes of one corrective delivery.</summary>
    public long MaximumCorrectionPayloadBytes { get; init; } = 1024 * 1024;

    /// <summary>The exact binding hash persisted in the schema-37 recovery configuration row.</summary>
    public string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (MaximumGrantedAttempts is < 1 or > MaximumGrantedAttemptsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumGrantedAttempts),
                "ProductionOutboxRecoveryGrantInvalid");
        if (MaximumCumulativeGrantedAttempts < MaximumGrantedAttempts ||
            MaximumCumulativeGrantedAttempts > MaximumCumulativeGrantedAttemptsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumCumulativeGrantedAttempts),
                "ProductionOutboxRecoveryCumulativeGrantInvalid");
        if (MaximumRecoveryOperations is < 1 or > MaximumRecoveryOperationsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumRecoveryOperations),
                "ProductionOutboxRecoveryOperationCapacityInvalid");
        if (MaximumCorrections is < 1 or > MaximumCorrectionsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumCorrections),
                "ProductionOutboxCorrectionCapacityInvalid");
        if (MaximumCorrectionPayloadBytes is < 1 or > MaximumCorrectionPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumCorrectionPayloadBytes),
                "ProductionOutboxCorrectionPayloadCapacityInvalid");
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionOutboxRecoveryOptions", Number(FormatVersion),
            Number(MaximumGrantedAttempts), Number(MaximumCumulativeGrantedAttempts),
            Number(MaximumRecoveryOperations), Number(MaximumCorrections),
            Number(MaximumCorrectionPayloadBytes));
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
