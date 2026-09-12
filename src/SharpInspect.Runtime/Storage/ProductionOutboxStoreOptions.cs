using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit, bounded, optional storage for the schema-36 production outbox. The declared
/// routes, every capacity budget and the presence of the optional legacy image/lifecycle
/// features are hashed into the immutable configuration row, so a store can never be opened
/// against a different deployment routing or different bounds. Routes carry no network address
/// and no credential: the transport is registered separately and this type never sends.
/// </summary>
public sealed class ProductionOutboxStoreOptions
{
    internal const int SchemaVersion = 36;
    internal const int FormatVersion = 1;
    internal const int MaximumRoutesHardLimit = 64;
    internal const int MaximumAttemptsHardLimit = 32;
    internal const int MaximumEventsHardLimit = 100_000;
    internal const int MaximumPayloadBytesHardLimit = 8 * 1024 * 1024;
    internal const long MaximumTotalBytesHardLimit = 512L * 1024 * 1024;
    internal const int MaximumPageSizeHardLimit = 512;
    internal const int MaximumAttemptTimeoutMillisecondsHardLimit = 60_000;
    internal const int MaximumRetryDelayMillisecondsHardLimit = 3_600_000;
    internal const int MaximumReceiptBytes = 16 * 1024;
    internal const int MaximumStoredReceiptBytes = 4 * ((MaximumReceiptBytes + 2) / 3);
    internal const int MaximumAuditPayloadBytes = MaximumReceiptBytes + 4 * 1024;
    internal const int ControlVerificationReserve = 64;
    internal const int SqliteValueLimitBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The complete future fact reserve of one persisted attempt: its AttemptStarted fact and
    /// exactly one terminal outcome. A consumed budget keeps its reserve until the attempt is
    /// durably resolved, so an admitted obligation can always record every outcome it owes.
    /// </summary>
    internal const int ReserveEventsPerAttempt = 2;

    public ProductionOutboxStoreOptions(IEnumerable<OutboxRouteDefinition> routes)
        : this(routes, null, null, null)
    {
    }

    public ProductionOutboxStoreOptions(IEnumerable<OutboxRouteDefinition> routes,
        RecipeLifecycleStoreOptions? recipeLifecycle, ProductionImageEvidenceStoreOptions? imageEvidence,
        ProductionImageFinalizationStoreOptions? imageFinalization)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var copied = routes.Take(MaximumRoutesHardLimit + 1).ToArray();
        if (copied.Length is < 1 or > MaximumRoutesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(routes), "ProductionOutboxRouteCapacityInvalid");
        if (copied.Any(route => route is null))
            throw new ArgumentException("ProductionOutboxRouteRequired", nameof(routes));
        Array.Sort(copied, (left, right) => string.CompareOrdinal(left.RouteId, right.RouteId));
        if (copied.Select(route => route.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copied.Length)
            throw new ArgumentException("ProductionOutboxRouteDuplicate", nameof(routes));
        Routes = new ReadOnlyCollection<OutboxRouteDefinition>(copied);
        RecipeLifecycle = recipeLifecycle;
        ImageEvidence = imageEvidence;
        ImageFinalization = imageFinalization;
        RouteSetHash = Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "ProductionOutboxRouteSetV1", copied.Select(route => route.ContentHash).ToArray())));
    }

    /// <summary>The frozen deployment routes, ordered by RouteId and unique by RouteId.</summary>
    public ReadOnlyCollection<OutboxRouteDefinition> Routes { get; }

    /// <summary>
    /// The already-present schema-33 lifecycle ledger this store must re-prove, or null when
    /// the store is a fresh schema-36 store without the legacy image/lifecycle features. Its
    /// presence and exact old activation hash are part of this store's immutable configuration.
    /// </summary>
    public RecipeLifecycleStoreOptions? RecipeLifecycle { get; }

    /// <summary>
    /// The already-present schema-34 image evidence store, or null. Present exactly when the
    /// source store carried it; the exact old binding hash is part of the configuration row.
    /// </summary>
    public ProductionImageEvidenceStoreOptions? ImageEvidence { get; }

    /// <summary>
    /// The already-present schema-35 image finalization ledger, or null. Present exactly when
    /// the source store carried it; the exact old binding hash is part of the configuration row.
    /// </summary>
    public ProductionImageFinalizationStoreOptions? ImageFinalization { get; }

    /// <summary>Persisted per-delivery attempt budget.</summary>
    public int MaximumAttempts { get; init; } = 5;

    /// <summary>Upper bound of one automatically retryable delay.</summary>
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of one transport attempt.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaximumEvents { get; init; } = 20_000;
    public int MaximumPayloadBytes { get; init; } = 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 128L * 1024 * 1024;
    public int MaximumPageSize { get; init; } = 128;

    /// <summary>The exact route-set hash persisted in the configuration row.</summary>
    public string RouteSetHash { get; }

    /// <summary>
    /// The exact bounded route count persisted in the configuration row. It is the only
    /// persisted bound a shared audit writer needs to derive the complete uncreated-batch
    /// reserve of a durable Admitted cycle that owns no Core row yet.
    /// </summary>
    internal int RouteCount => Routes.Count;

    /// <summary>The exact schema-33 lifecycle activation hash bound by this store, if present.</summary>
    internal string? RecipeLifecyclePresenceHash => RecipeLifecycle is null ? null :
        Convert.ToHexString(SHA256.HashData(RecipeLifecycle.EncodeActivationPayload()));

    internal string? ImageEvidencePresenceHash => ImageEvidence?.BindingHash;

    internal string? ImageFinalizationPresenceHash => ImageFinalization?.BindingHash;

    /// <summary>The complete persisted option hash: routes, budgets and optional presence.</summary>
    internal string BindingHash => Convert.ToHexString(SHA256.HashData(EncodeBinding()));

    internal void Validate()
    {
        if (Routes.Count is < 1 or > MaximumRoutesHardLimit ||
            Routes.Select(route => route.RouteId).Distinct(StringComparer.Ordinal).Count() != Routes.Count)
            throw new ArgumentException("ProductionOutboxRouteCapacityInvalid", nameof(Routes));
        if (ImageEvidence is not null && RecipeLifecycle is null)
            throw new ArgumentException("ProductionOutboxImageEvidenceRequiresLifecycle", nameof(ImageEvidence));
        if (ImageFinalization is not null && ImageEvidence is null)
            throw new ArgumentException("ProductionOutboxFinalizationRequiresImageEvidence",
                nameof(ImageFinalization));
        if (MaximumAttempts is < 1 or > MaximumAttemptsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts), "ProductionOutboxAttemptCapacityInvalid");
        if (MaximumRetryDelay < TimeSpan.FromMilliseconds(1) ||
            MaximumRetryDelay > TimeSpan.FromMinutes(60))
            throw new ArgumentOutOfRangeException(nameof(MaximumRetryDelay), "ProductionOutboxRetryDelayInvalid");
        if (AttemptTimeout < TimeSpan.FromMilliseconds(1) || AttemptTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(AttemptTimeout), "ProductionOutboxAttemptTimeoutInvalid");
        if (MaximumEvents is < 1 or > MaximumEventsHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents), "ProductionOutboxEntryCapacityInvalid");
        if (MaximumPayloadBytes is < 1 or > MaximumPayloadBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes),
                "ProductionOutboxPayloadCapacityInvalid");
        if (MaximumTotalBytes < MaximumPayloadBytes || MaximumTotalBytes > MaximumTotalBytesHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes),
                "ProductionOutboxTotalCapacityInvalid");
        if (MaximumPageSize is < 1 or > MaximumPageSizeHardLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumPageSize), "ProductionOutboxPageCapacityInvalid");
        if (Routes.Any(route => route.MaximumPayloadBytes > MaximumPayloadBytes))
            throw new ArgumentException("ProductionOutboxRoutePayloadCapacityInvalid", nameof(Routes));
        if (!IsHash(RouteSetHash))
            throw new ArgumentException("ProductionOutboxRouteSetHashInvalid", nameof(RouteSetHash));
        if (RecipeLifecyclePresenceHash is { } lifecycle && !IsHash(lifecycle))
            throw new ArgumentException("ProductionOutboxLifecycleBindingInvalid", nameof(RecipeLifecycle));
        if (ImageEvidencePresenceHash is { } evidence && !IsHash(evidence))
            throw new ArgumentException("ProductionOutboxImageEvidenceBindingInvalid", nameof(ImageEvidence));
        if (ImageFinalizationPresenceHash is { } finalization && !IsHash(finalization))
            throw new ArgumentException("ProductionOutboxImageFinalizationBindingInvalid",
                nameof(ImageFinalization));
    }

    internal byte[] EncodeBinding()
    {
        Validate();
        return AuditCanonical.Encode("ProductionOutboxStoreOptions", Number(FormatVersion), RouteSetHash,
            Number(RouteCount), Number(MaximumAttempts), Number((long)MaximumRetryDelay.TotalMilliseconds),
            Number((long)AttemptTimeout.TotalMilliseconds), Number(MaximumEvents), Number(MaximumPayloadBytes),
            Number(MaximumTotalBytes), Number(MaximumPageSize),
            RecipeLifecyclePresenceHash ?? "absent", ImageEvidencePresenceHash ?? "absent",
            ImageFinalizationPresenceHash ?? "absent", Number(ControlVerificationReserve), Number(MaximumReceiptBytes));
    }

    internal byte[] EncodeActivationPayload() => AuditCanonical.Encode("ProductionOutboxStoreActivated",
        Convert.ToBase64String(EncodeBinding()), BindingHash);

    internal static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
