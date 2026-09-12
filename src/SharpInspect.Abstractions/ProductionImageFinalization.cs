using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The closed set of immutable finalization lifecycle events.</summary>
public enum ProductionImageFinalizationKind : byte
{
    AttemptStarted = 1,
    AttemptFailed = 2,
    Succeeded = 3,
    StageReleased = 4
}

/// <summary>The current persistent finalization state of one obligated image.</summary>
public enum ProductionImageFinalizationState : byte { Pending = 1, Failed = 2, Succeeded = 3 }

/// <summary>Whether the canonical stage of a succeeded obligation was already released.</summary>
public enum ProductionImageCleanupState : byte { Pending = 1, Released = 2 }

/// <summary>
/// Stable failure classification: a Temporary failure may be retried automatically after
/// its retry instant, an Integrity failure is a recorded integrity conflict that blocks
/// automatic normal retry acceptance.
/// </summary>
public enum ProductionImageFailureCategory : byte { Temporary = 1, Integrity = 2 }

/// <summary>
/// The frozen retry budget facts of one started attempt. The store derives both file names
/// from the frozen manifest and the attempt identity; no caller supplies a path.
/// </summary>
public sealed record ProductionImageAttemptDescriptor
{
    internal ProductionImageAttemptDescriptor(Guid attemptId, int attemptNumber, int attemptLimit,
        int remainingAttempts, string finalRootBindingHash, string finalFileName,
        string temporaryFileName)
    {
        if (attemptId == Guid.Empty || attemptNumber < 1 || attemptLimit < 1 ||
            remainingAttempts < 0 || remainingAttempts >= attemptLimit)
            throw new ArgumentException("ProductionImageAttemptIdentityInvalid");
        AttemptId = attemptId;
        AttemptNumber = attemptNumber;
        AttemptLimit = attemptLimit;
        RemainingAttempts = remainingAttempts;
        FinalRootBindingHash = Hash(finalRootBindingHash);
        FinalFileName = FileName(finalFileName, ".png", "ProductionImageFinalFileNameInvalid");
        TemporaryFileName = FileName(temporaryFileName, ".tmp", "ProductionImageTemporaryFileNameInvalid");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-production-image-attempt-v1", AttemptId.ToString("D"),
            Number(AttemptNumber), Number(AttemptLimit), Number(RemainingAttempts),
            FinalRootBindingHash, FinalFileName, TemporaryFileName
        });
    }

    public Guid AttemptId { get; }
    public int AttemptNumber { get; }
    /// <summary>The persisted attempt budget of this work, fixed when the obligation was admitted.</summary>
    public int AttemptLimit { get; }
    /// <summary>Attempts that remain after this attempt, counted inside the persisted limit.</summary>
    public int RemainingAttempts { get; }
    public string FinalRootBindingHash { get; }
    public string FinalFileName { get; }
    public string TemporaryFileName { get; }
    public string ContentHash { get; }

    private static string Hash(string value) => AlgorithmConfigurationValidation.Hash(value,
        nameof(value)).ToUpperInvariant();

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FileName(string value, string suffix, string reason) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.EndsWith(suffix, StringComparison.Ordinal) &&
        value.IndexOfAny(new[] { '\\', '/', ':', '*' }) < 0 &&
        !value.Contains("..", StringComparison.Ordinal)
            ? value
            : throw new ArgumentException(reason);
}

/// <summary>The stable failure facts of one failed attempt.</summary>
public sealed record ProductionImageFailureDescriptor
{
    internal ProductionImageFailureDescriptor(ProductionImageFailureCategory category,
        DateTimeOffset? retryAfterUtc)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (category == ProductionImageFailureCategory.Integrity && retryAfterUtc is not null)
            throw new ArgumentException("ProductionImageIntegrityFailureRetryUnsupported",
                nameof(retryAfterUtc));
        Category = category;
        RetryAfterUtc = retryAfterUtc is { } value
            ? TraceRetentionObligation.ValidateUtc(value, nameof(retryAfterUtc))
            : null;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-production-image-failure-v1", Category.ToString(),
            RetryAfterUtc?.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public ProductionImageFailureCategory Category { get; }
    /// <summary>The earliest automatically retryable instant; absent for integrity conflicts.</summary>
    public DateTimeOffset? RetryAfterUtc { get; }
    public string ContentHash { get; }
}

/// <summary>
/// The verified commit descriptor of one succeeded obligation: the exact final root and
/// file name, the encoded PNG byte length and the canonical pixel contract of the bound
/// pending manifest. It never replaces the manifest; it must equal it.
/// </summary>
public sealed record ProductionImageSuccessDescriptor
{
    internal ProductionImageSuccessDescriptor(string finalRootBindingHash, string finalFileName,
        long encodedByteLength, int width, int height, VisionPixelFormat pixelFormat,
        int? validBits, string canonicalHashScheme, int canonicalHashSchemeVersion,
        string canonicalPixelHash)
    {
        _ = CanonicalImagePixelContent.CreateEnvelope(width, height, pixelFormat, validBits);
        if (encodedByteLength is < 1 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(encodedByteLength));
        if (canonicalHashScheme != CanonicalImagePixelContent.HashScheme ||
            canonicalHashSchemeVersion != CanonicalImagePixelContent.HashSchemeVersion)
            throw new ArgumentException("ProductionImageCanonicalHashSchemeUnsupported",
                nameof(canonicalHashScheme));
        FinalRootBindingHash = AlgorithmConfigurationValidation.Hash(finalRootBindingHash,
            nameof(finalRootBindingHash)).ToUpperInvariant();
        FinalFileName = AlgorithmConfigurationValidation.Identifier(finalFileName,
            nameof(finalFileName));
        if (FinalFileName.Length > 128 || !FinalFileName.EndsWith(".png", StringComparison.Ordinal) ||
            FinalFileName.Contains("..", StringComparison.Ordinal) ||
            FinalFileName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("ProductionImageFinalFileNameInvalid", nameof(finalFileName));
        EncodedByteLength = encodedByteLength;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        CanonicalHashScheme = canonicalHashScheme;
        CanonicalHashSchemeVersion = canonicalHashSchemeVersion;
        CanonicalPixelHash = AlgorithmConfigurationValidation.Hash(canonicalPixelHash,
            nameof(canonicalPixelHash)).ToUpperInvariant();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-production-image-success-v1", FinalRootBindingHash, FinalFileName,
            Number(EncodedByteLength), Number(Width), Number(Height), PixelFormat.ToString(),
            ValidBits?.ToString(CultureInfo.InvariantCulture), CanonicalHashScheme,
            Number(CanonicalHashSchemeVersion), CanonicalPixelHash
        });
    }

    public string FinalRootBindingHash { get; }
    public string FinalFileName { get; }
    public long EncodedByteLength { get; }
    public int Width { get; }
    public int Height { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public string CanonicalHashScheme { get; }
    public int CanonicalHashSchemeVersion { get; }
    public string CanonicalPixelHash { get; }
    public string ContentHash { get; }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One immutable image finalization fact. Position is the contiguous global store position,
/// AggregateSequence is the contiguous position inside one WorkId, and AuditSequence/AuditHash
/// are the one central audit metadata entry that binds this exact row. The content hash covers
/// every substantive field and deliberately excludes the audit reference.
/// </summary>
public sealed class ProductionImageFinalizationEvent
{
    internal ProductionImageFinalizationEvent(long position, Guid eventId, Guid workId,
        Guid manifestId, Guid inspectionId, string workContentHash, string manifestContentHash,
        long aggregateSequence, Guid? attemptId, int? attemptNumber, Guid runtimeEpoch,
        DateTimeOffset recordedAtUtc, ProductionImageFinalizationKind kind, string reasonCode,
        ProductionImageAttemptDescriptor? attempt, ProductionImageFailureDescriptor? failure,
        ProductionImageSuccessDescriptor? success, long auditSequence = 0, string? auditHash = null,
        string? contentHash = null)
    {
        if (position < 1 || eventId == Guid.Empty || workId == Guid.Empty ||
            manifestId == Guid.Empty || inspectionId == Guid.Empty || aggregateSequence < 1 ||
            runtimeEpoch == Guid.Empty || !Enum.IsDefined(kind))
            throw new ArgumentException("ProductionImageFinalizationEventIdentityInvalid");
        var attemptBound = kind is ProductionImageFinalizationKind.AttemptStarted or
            ProductionImageFinalizationKind.AttemptFailed or ProductionImageFinalizationKind.Succeeded;
        if (attemptBound != (attemptId is not null) || attemptBound != (attemptNumber is not null) ||
            attemptBound != (attempt is not null))
            throw new ArgumentException("ProductionImageFinalizationAttemptBindingInvalid");
        if (attempt is not null && (attempt.AttemptId != attemptId || attempt.AttemptNumber != attemptNumber))
            throw new ArgumentException("ProductionImageFinalizationAttemptBindingInvalid");
        if ((kind == ProductionImageFinalizationKind.AttemptFailed) != (failure is not null) ||
            (kind == ProductionImageFinalizationKind.Succeeded) != (success is not null))
            throw new ArgumentException("ProductionImageFinalizationKindDetailMismatch");
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("ProductionImageFinalizationAuditReferenceInvalid");

        Position = position;
        EventId = eventId;
        WorkId = workId;
        ManifestId = manifestId;
        InspectionId = inspectionId;
        WorkContentHash = Hash(workContentHash);
        ManifestContentHash = Hash(manifestContentHash);
        AggregateSequence = aggregateSequence;
        AttemptId = attemptId;
        AttemptNumber = attemptNumber;
        RuntimeEpoch = runtimeEpoch;
        RecordedAtUtc = TraceRetentionObligation.ValidateUtc(recordedAtUtc, nameof(recordedAtUtc));
        Kind = kind;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        Attempt = attempt;
        Failure = failure;
        Success = success;
        AuditSequence = auditSequence;
        AuditHash = auditHash is null ? null :
            AlgorithmConfigurationValidation.Hash(auditHash, nameof(auditHash)).ToUpperInvariant();
        var computed = ComputeContentHash();
        if (contentHash is not null && !string.Equals(Hash(contentHash), computed, StringComparison.Ordinal))
            throw new ArgumentException("ProductionImageFinalizationContentHashMismatch", nameof(contentHash));
        ContentHash = computed;
    }

    public long Position { get; }
    public Guid EventId { get; }
    public Guid WorkId { get; }
    public Guid ManifestId { get; }
    public Guid InspectionId { get; }
    public string WorkContentHash { get; }
    public string ManifestContentHash { get; }
    public long AggregateSequence { get; }
    public Guid? AttemptId { get; }
    public int? AttemptNumber { get; }
    public Guid RuntimeEpoch { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    /// <summary>The fixed system principal; a finalization fact is never attributed to a human.</summary>
    public string SystemPrincipalId => SharpInspect.Abstractions.SystemPrincipalId.Runtime;
    public ProductionImageFinalizationKind Kind { get; }
    public string ReasonCode { get; }
    public ProductionImageAttemptDescriptor? Attempt { get; }
    public ProductionImageFailureDescriptor? Failure { get; }
    public ProductionImageSuccessDescriptor? Success { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string ContentHash { get; }

    private string ComputeContentHash() => AlgorithmContractValidation.HashParts(new[]
    {
        "sharpinspect-image-finalization-event-v1", Number(Position), EventId.ToString("D"),
        WorkId.ToString("D"), ManifestId.ToString("D"), InspectionId.ToString("D"),
        WorkContentHash, ManifestContentHash, Number(AggregateSequence),
        AttemptId?.ToString("D"), AttemptNumber?.ToString(CultureInfo.InvariantCulture),
        RuntimeEpoch.ToString("D"), RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        SystemPrincipalId, Kind.ToString(), ReasonCode, Attempt?.ContentHash,
        Failure?.ContentHash, Success?.ContentHash
    });

    private static string Hash(string value) => AlgorithmConfigurationValidation.Hash(value,
        nameof(value)).ToUpperInvariant();

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One bounded, verified backlog watermark. Count and Bytes cover every obligated image
/// without a Succeeded fact, so Runtime can never apply a stale in-memory projection.
/// </summary>
public sealed record ImageBacklogSnapshot(long ThroughAuditSequence, long Count, long Bytes,
    DateTimeOffset? OldestCreatedAtUtc)
{
    public static ImageBacklogSnapshot Empty(long throughAuditSequence) =>
        new(throughAuditSequence, 0, 0, null);
}

/// <summary>The current persistent projection of one obligated image.</summary>
public sealed class ProductionImageFinalizationWorkState
{
    internal ProductionImageFinalizationWorkState(Guid workId, Guid manifestId, Guid inspectionId,
        string workContentHash, string manifestContentHash, ProductionImageFinalizationState state,
        ProductionImageCleanupState cleanupState, int attemptCount, int nextAttemptNumber,
        bool retryEligible, string? lastFailureReasonCode,
        ProductionImageFailureCategory? lastFailureCategory, DateTimeOffset? retryAfterUtc,
        ProductionImageSuccessDescriptor? success, long lastEventPosition, string? lastEventContentHash,
        Guid? activeAttemptId = null, bool integrityConflict = false)
    {
        if (workId == Guid.Empty || manifestId == Guid.Empty || inspectionId == Guid.Empty ||
            !Enum.IsDefined(state) || !Enum.IsDefined(cleanupState) || attemptCount < 0 ||
            nextAttemptNumber < 1 || lastEventPosition < 0 ||
            (lastEventPosition == 0) != (lastEventContentHash is null))
            throw new ArgumentException("ProductionImageFinalizationWorkStateInvalid");
        if (state != ProductionImageFinalizationState.Succeeded && success is not null)
            throw new ArgumentException("ProductionImageFinalizationSuccessStateMismatch");
        if (cleanupState == ProductionImageCleanupState.Released &&
            state != ProductionImageFinalizationState.Succeeded)
            throw new ArgumentException("ProductionImageCleanupStateMismatch");
        if (activeAttemptId == Guid.Empty || activeAttemptId.HasValue &&
            (attemptCount == 0 || state == ProductionImageFinalizationState.Succeeded) ||
            integrityConflict && retryEligible)
            throw new ArgumentException("ProductionImageActiveAttemptStateInvalid");
        if ((lastFailureReasonCode is null) != (lastFailureCategory is null) ||
            (lastFailureCategory == ProductionImageFailureCategory.Temporary) != (retryAfterUtc is not null))
            throw new ArgumentException("ProductionImageLastFailureInvalid");
        WorkId = workId;
        ManifestId = manifestId;
        InspectionId = inspectionId;
        WorkContentHash = Hash(workContentHash);
        ManifestContentHash = Hash(manifestContentHash);
        State = state;
        CleanupState = cleanupState;
        AttemptCount = attemptCount;
        NextAttemptNumber = nextAttemptNumber;
        RetryEligible = retryEligible;
        LastFailureReasonCode = lastFailureReasonCode is null ? null :
            AlgorithmConfigurationValidation.Identifier(lastFailureReasonCode,
                nameof(lastFailureReasonCode));
        LastFailureCategory = lastFailureCategory;
        RetryAfterUtc = retryAfterUtc is { } value
            ? TraceRetentionObligation.ValidateUtc(value, nameof(retryAfterUtc))
            : null;
        Success = success;
        LastEventPosition = lastEventPosition;
        LastEventContentHash = lastEventContentHash is null ? null : Hash(lastEventContentHash);
        ActiveAttemptId = activeAttemptId;
        IntegrityConflict = integrityConflict;
    }

    public Guid WorkId { get; }
    public Guid ManifestId { get; }
    public Guid InspectionId { get; }
    public string WorkContentHash { get; }
    public string ManifestContentHash { get; }
    public ProductionImageFinalizationState State { get; }
    public ProductionImageCleanupState CleanupState { get; }
    public int AttemptCount { get; }
    /// <summary>The persisted next attempt number; the store never reuses one.</summary>
    public int NextAttemptNumber { get; }
    /// <summary>False while a recorded integrity conflict blocks automatic normal retry.</summary>
    public bool RetryEligible { get; }
    public string? LastFailureReasonCode { get; }
    public ProductionImageFailureCategory? LastFailureCategory { get; }
    public DateTimeOffset? RetryAfterUtc { get; }
    public ProductionImageSuccessDescriptor? Success { get; }
    public long LastEventPosition { get; }
    public string? LastEventContentHash { get; }
    public Guid? ActiveAttemptId { get; }
    public bool IntegrityConflict { get; }

    private static string Hash(string value) => AlgorithmConfigurationValidation.Hash(value,
        nameof(value)).ToUpperInvariant();
}

/// <summary>The original Core image identity of one obligated image.</summary>
public sealed record ProductionImageCoreImageIdentity
{
    internal ProductionImageCoreImageIdentity(Guid inspectionId, Guid manifestId, Guid workId,
        Guid stageId, string canonicalPixelHash, int width, int height, VisionPixelFormat pixelFormat,
        int? validBits, long canonicalByteLength, string evidencePolicyContentHash,
        DateTimeOffset createdAtUtc, string manifestContentHash, string workContentHash)
    {
        InspectionId = inspectionId;
        ManifestId = manifestId;
        WorkId = workId;
        StageId = stageId;
        CanonicalPixelHash = AlgorithmConfigurationValidation.Hash(canonicalPixelHash,
            nameof(canonicalPixelHash)).ToUpperInvariant();
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        CanonicalByteLength = canonicalByteLength;
        EvidencePolicyContentHash = AlgorithmConfigurationValidation.Hash(
            evidencePolicyContentHash, nameof(evidencePolicyContentHash)).ToUpperInvariant();
        CreatedAtUtc = TraceRetentionObligation.ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        ManifestContentHash = AlgorithmConfigurationValidation.Hash(manifestContentHash,
            nameof(manifestContentHash)).ToUpperInvariant();
        WorkContentHash = AlgorithmConfigurationValidation.Hash(workContentHash,
            nameof(workContentHash)).ToUpperInvariant();
    }

    public Guid InspectionId { get; }
    public Guid ManifestId { get; }
    public Guid WorkId { get; }
    public Guid StageId { get; }
    public string CanonicalPixelHash { get; }
    public int Width { get; }
    public int Height { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public long CanonicalByteLength { get; }
    public string EvidencePolicyContentHash { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string ManifestContentHash { get; }
    public string WorkContentHash { get; }
}

/// <summary>
/// One verified readonly projection: the original Core image identity, the current persistent
/// finalization state and the complete append-only history of that obligation.
/// </summary>
public sealed record ProductionImageEvidenceRecord
{
    internal ProductionImageEvidenceRecord(ProductionImageCoreImageIdentity identity,
        ProductionImageFinalizationWorkState state,
        IEnumerable<ProductionImageFinalizationEvent> events, long position)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Events = new ReadOnlyCollection<ProductionImageFinalizationEvent>(
            (events ?? Array.Empty<ProductionImageFinalizationEvent>()).ToArray());
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
    }

    public ProductionImageCoreImageIdentity Identity { get; }
    public ProductionImageFinalizationWorkState State { get; }
    public ReadOnlyCollection<ProductionImageFinalizationEvent> Events { get; }
    /// <summary>The Core commit position this obligation was projected at.</summary>
    public long Position { get; }
}

public sealed record ProductionImageEvidenceFilter(Guid? InspectionId = null,
    Guid? WorkId = null, long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 128);

public sealed record ProductionImageEvidencePage(bool Available, string ReasonCode,
    IReadOnlyList<ProductionImageEvidenceRecord> Items, long ThroughPosition,
    long? NextAfterPosition, long ThroughAuditSequence);

/// <summary>
/// The bounded worker queue at one fixed audit watermark: obligations that still have no
/// Succeeded fact and obligations that succeeded but whose canonical stage was not released yet.
/// </summary>
public sealed record ProductionImageWorkQueuePage(bool Available, string ReasonCode,
    IReadOnlyList<ProductionImageFinalizationWorkState> Pending,
    IReadOnlyList<ProductionImageFinalizationWorkState> SucceededNotReleased,
    bool PendingTruncated, bool SucceededNotReleasedTruncated, long ThroughAuditSequence);

/// <summary>
/// Read-only, bounded access to production image evidence finalization. It exposes no
/// mutation surface and never accepts an arbitrary filesystem path.
/// </summary>
public interface IProductionImageEvidenceQuery
{
    ValueTask<ProductionImageEvidencePage> QueryAsync(ProductionImageEvidenceFilter filter,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionImageWorkQueuePage> ReadWorkQueueAsync(int pageSize = 128,
        CancellationToken cancellationToken = default);
    ValueTask<ImageBacklogSnapshot> ReadBacklogAsync(CancellationToken cancellationToken = default);
}
