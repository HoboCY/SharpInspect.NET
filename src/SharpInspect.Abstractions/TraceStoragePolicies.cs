using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace SharpInspect.Abstractions;

/// <summary>Evidence classes governed by the approved Trace Storage Policy.</summary>
public enum TraceRetentionClass : byte
{
    AuthoritativeImage = 1,
    Thumbnail = 2,
    RenderedEvidencePreview = 3,
    OperationalLog = 4,
    ProtectedDiagnosticRecord = 5,
    SupportBundle = 6,
    CompletedExport = 7,
    RecipeImportStaging = 8,
    RejectedRecipePackage = 9,
    QuarantineEvidence = 10,
    OrphanImageStage = 11
}

public enum RetentionStartEvent : byte
{
    ArtifactCreated = 1,
    Finalized = 2,
    ExportCompleted = 3,
    Quarantined = 4
}

public sealed class TraceRetentionRule
{
    public TraceRetentionRule(TraceRetentionClass evidenceClass, RetentionStartEvent startsAt,
        TimeSpan minimumRetention)
    {
        if (!Enum.IsDefined(evidenceClass))
            throw new ArgumentOutOfRangeException(nameof(evidenceClass), "TraceStorageRetentionClassInvalid");
        if (!Enum.IsDefined(startsAt))
            throw new ArgumentOutOfRangeException(nameof(startsAt), "TraceStorageRetentionStartInvalid");
        TraceStoragePolicyValidation.Retention(minimumRetention, nameof(minimumRetention));

        EvidenceClass = evidenceClass;
        StartsAt = startsAt;
        MinimumRetention = minimumRetention;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-retention-rule-v1", EvidenceClass.ToString(),
            StartsAt.ToString(), TraceStoragePolicyValidation.Duration(MinimumRetention)
        });
    }

    public TraceRetentionClass EvidenceClass { get; }
    public RetentionStartEvent StartsAt { get; }
    public TimeSpan MinimumRetention { get; }
    public string ContentHash { get; }
}

public sealed class TraceBacklogLimits
{
    public TraceBacklogLimits(long maximumItems, long maximumBytes, TimeSpan maximumOldestAge)
    {
        if (maximumItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumItems), "TraceStorageBacklogItemsInvalid");
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "TraceStorageBacklogBytesInvalid");
        TraceStoragePolicyValidation.Retention(maximumOldestAge, nameof(maximumOldestAge));

        MaximumItems = maximumItems;
        MaximumBytes = maximumBytes;
        MaximumOldestAge = maximumOldestAge;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-backlog-limits-v1",
            maximumItems.ToString(CultureInfo.InvariantCulture),
            maximumBytes.ToString(CultureInfo.InvariantCulture),
            TraceStoragePolicyValidation.Duration(maximumOldestAge)
        });
    }

    public long MaximumItems { get; }
    public long MaximumBytes { get; }
    public TimeSpan MaximumOldestAge { get; }
    public string ContentHash { get; }
}

public sealed class TraceStorageRouteLimit
{
    public TraceStorageRouteLimit(string routeId, string contractVersion, string contractHash,
        TraceBacklogLimits limits)
    {
        RouteId = TraceStoragePolicyValidation.Identifier(routeId, nameof(routeId));
        ContractVersion = TraceStoragePolicyValidation.Identifier(contractVersion, nameof(contractVersion));
        ContractHash = TraceStoragePolicyValidation.Hash(contractHash, nameof(contractHash));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-storage-route-limit-v1", RouteId, ContractVersion,
            ContractHash, Limits.ContentHash
        });
    }

    public string RouteId { get; }
    public string ContractVersion { get; }
    public string ContractHash { get; }
    public TraceBacklogLimits Limits { get; }
    public string ContentHash { get; }
}

public sealed class TraceStorageMaintenanceBudget
{
    public TraceStorageMaintenanceBudget(TimeSpan interval, TimeSpan maximumRunTime,
        long maximumBytes, int maximumItems)
    {
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(interval), "TraceStorageMaintenanceIntervalInvalid");
        if (maximumRunTime <= TimeSpan.Zero || maximumRunTime > interval)
            throw new ArgumentOutOfRangeException(nameof(maximumRunTime),
                "TraceStorageMaintenanceRunTimeInvalid");
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes),
                "TraceStorageMaintenanceBytesInvalid");
        if (maximumItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumItems),
                "TraceStorageMaintenanceItemsInvalid");

        Interval = interval;
        MaximumRunTime = maximumRunTime;
        MaximumBytes = maximumBytes;
        MaximumItems = maximumItems;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-storage-maintenance-budget-v1",
            TraceStoragePolicyValidation.Duration(interval),
            TraceStoragePolicyValidation.Duration(maximumRunTime),
            maximumBytes.ToString(CultureInfo.InvariantCulture),
            maximumItems.ToString(CultureInfo.InvariantCulture)
        });
    }

    public TimeSpan Interval { get; }
    public TimeSpan MaximumRunTime { get; }
    public long MaximumBytes { get; }
    public int MaximumItems { get; }
    public string ContentHash { get; }
}

/// <summary>
/// An approved, versioned deployment policy. It describes rules only; it does
/// not represent a production run, inspection, artifact, or retention obligation.
/// </summary>
public sealed class TraceStoragePolicyDefinition
{
    public TraceStoragePolicyDefinition(string policyId, string version, string approvalReference,
        string approvalVersion, string rationale, IEnumerable<TraceRetentionRule> retentionRules,
        long minimumReserveBytes, decimal minimumReservePercent,
        IEnumerable<TraceStorageRouteLimit> requiredRoutes, TraceBacklogLimits imageBacklog,
        TimeSpan evidenceStageTimeout, TimeSpan traceCommitTimeout,
        TraceStorageMaintenanceBudget scrubber, TraceStorageMaintenanceBudget checkpoint,
        long maximumWalBytes)
    {
        PolicyId = TraceStoragePolicyValidation.Identifier(policyId, nameof(policyId));
        Version = TraceStoragePolicyValidation.Identifier(version, nameof(version));
        ApprovalReference = TraceStoragePolicyValidation.Text(approvalReference,
            nameof(approvalReference), 256);
        ApprovalVersion = TraceStoragePolicyValidation.Identifier(approvalVersion,
            nameof(approvalVersion));
        Rationale = TraceStoragePolicyValidation.Text(rationale, nameof(rationale), 4096);
        RetentionRules = ValidateRetentionRules(retentionRules);
        if (minimumReserveBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumReserveBytes),
                "TraceStorageReserveBytesInvalid");
        if (minimumReservePercent <= 0m || minimumReservePercent >= 100m)
            throw new ArgumentOutOfRangeException(nameof(minimumReservePercent),
                "TraceStorageReservePercentInvalid");
        MinimumReserveBytes = minimumReserveBytes;
        MinimumReservePercent = minimumReservePercent;
        RequiredRoutes = ValidateRoutes(requiredRoutes);
        ImageBacklog = imageBacklog ?? throw new ArgumentNullException(nameof(imageBacklog));
        EvidenceStageTimeout = TraceStoragePolicyValidation.CommitTimeout(evidenceStageTimeout,
            nameof(evidenceStageTimeout));
        TraceCommitTimeout = TraceStoragePolicyValidation.CommitTimeout(traceCommitTimeout,
            nameof(traceCommitTimeout));
        Scrubber = scrubber ?? throw new ArgumentNullException(nameof(scrubber));
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        if (maximumWalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumWalBytes),
                "TraceStorageWalBytesInvalid");
        MaximumWalBytes = maximumWalBytes;

        var parts = new List<string?>
        {
            "sharpinspect-trace-storage-policy-definition-v1", PolicyId, Version,
            ApprovalReference, ApprovalVersion, Rationale,
            MinimumReserveBytes.ToString(CultureInfo.InvariantCulture),
            MinimumReservePercent.ToString("G29", CultureInfo.InvariantCulture),
            ImageBacklog.ContentHash, TraceStoragePolicyValidation.Duration(EvidenceStageTimeout),
            TraceStoragePolicyValidation.Duration(TraceCommitTimeout), Scrubber.ContentHash,
            Checkpoint.ContentHash, MaximumWalBytes.ToString(CultureInfo.InvariantCulture),
            RetentionRules.Count.ToString(CultureInfo.InvariantCulture)
        };
        parts.AddRange(RetentionRules.Select(value => value.ContentHash));
        parts.Add(RequiredRoutes.Count.ToString(CultureInfo.InvariantCulture));
        parts.AddRange(RequiredRoutes.Select(value => value.ContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(parts);
    }

    public string PolicyId { get; }
    public string Version { get; }
    public string ApprovalReference { get; }
    public string ApprovalVersion { get; }
    public string Rationale { get; }
    public ReadOnlyCollection<TraceRetentionRule> RetentionRules { get; }
    public long MinimumReserveBytes { get; }
    public decimal MinimumReservePercent { get; }
    public ReadOnlyCollection<TraceStorageRouteLimit> RequiredRoutes { get; }
    public TraceBacklogLimits ImageBacklog { get; }
    public TimeSpan EvidenceStageTimeout { get; }
    public TimeSpan TraceCommitTimeout { get; }
    public TraceStorageMaintenanceBudget Scrubber { get; }
    public TraceStorageMaintenanceBudget Checkpoint { get; }
    public long MaximumWalBytes { get; }
    public string ContentHash { get; }

    private static ReadOnlyCollection<TraceRetentionRule> ValidateRetentionRules(
        IEnumerable<TraceRetentionRule> values)
    {
        var copied = TraceStoragePolicyValidation.Copy(values, nameof(values), 32);
        if (copied.Count != Enum.GetValues<TraceRetentionClass>().Length)
            throw new ArgumentException("TraceStorageRetentionClassesIncomplete", nameof(values));
        if (copied.Select(value => value.EvidenceClass).Distinct().Count() != copied.Count)
            throw new ArgumentException("TraceStorageRetentionClassDuplicate", nameof(values));
        if (copied.Select(value => (int)value.EvidenceClass).OrderBy(value => value).SequenceEqual(
                Enum.GetValues<TraceRetentionClass>().Select(value => (int)value).OrderBy(value => value)) is false)
            throw new ArgumentException("TraceStorageRetentionClassUnknown", nameof(values));
        return new ReadOnlyCollection<TraceRetentionRule>(copied
            .OrderBy(value => value.EvidenceClass).ToArray());
    }

    private static ReadOnlyCollection<TraceStorageRouteLimit> ValidateRoutes(
        IEnumerable<TraceStorageRouteLimit> values)
    {
        var copied = TraceStoragePolicyValidation.Copy(values, nameof(values), 64);
        if (copied.Select(value => value.RouteId).Distinct(StringComparer.Ordinal).Count() != copied.Count)
            throw new ArgumentException("TraceStorageRouteDuplicate", nameof(values));
        return new ReadOnlyCollection<TraceStorageRouteLimit>(copied
            .OrderBy(value => value.RouteId, StringComparer.Ordinal).ToArray());
    }
}

public sealed record PublishTraceStoragePolicyCommand : RuntimeCommand
{
    public PublishTraceStoragePolicyCommand(Guid correlationId, CommandInvocation invocation,
        long expectedVersion, TraceStoragePolicyDefinition policy, string reason)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty)
            throw new ArgumentException("TraceStoragePolicyCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        if (expectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion),
                "TraceStoragePolicyExpectedVersionInvalid");
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        Reason = TraceStoragePolicyValidation.Text(reason, nameof(reason), 512);
        ExpectedVersion = expectedVersion;
        AuthorizationTarget = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-storage-policy-publish-command-v1",
            ExpectedVersion.ToString(CultureInfo.InvariantCulture), Policy.ContentHash, Reason
        });
    }

    public long ExpectedVersion { get; }
    public TraceStoragePolicyDefinition Policy { get; }
    public string Reason { get; }
    public string AuthorizationTarget { get; }
}

/// <summary>Writer-produced immutable publication metadata.</summary>
public sealed class TraceStoragePolicyPublication
{
    public TraceStoragePolicyPublication(long version, TraceStoragePolicyDefinition policy,
        Guid operationId, Guid principalId, Guid sessionId, long authorizationRevision,
        Guid stepUpGrantId, DateTimeOffset publishedAtUtc, string? previousContentHash)
    {
        if (version < 1 || operationId == Guid.Empty || principalId == Guid.Empty ||
            sessionId == Guid.Empty || authorizationRevision < 0 || stepUpGrantId == Guid.Empty)
            throw new ArgumentException("TraceStoragePolicyPublicationIdentityInvalid");
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        PreviousContentHash = previousContentHash is null ? null :
            TraceStoragePolicyValidation.Hash(previousContentHash, nameof(previousContentHash));
        if (publishedAtUtc == default || publishedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("TraceStoragePolicyPublicationTimestampInvalid",
                nameof(publishedAtUtc));

        Version = version;
        OperationId = operationId;
        PrincipalId = principalId;
        SessionId = sessionId;
        AuthorizationRevision = authorizationRevision;
        StepUpGrantId = stepUpGrantId;
        PublishedAtUtc = publishedAtUtc;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-trace-storage-policy-publication-v1",
            Version.ToString(CultureInfo.InvariantCulture), Policy.ContentHash,
            OperationId.ToString("D"), PrincipalId.ToString("D"), SessionId.ToString("D"),
            AuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId.ToString("D"),
            PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture), PreviousContentHash
        });
    }

    public long Version { get; }
    public TraceStoragePolicyDefinition Policy { get; }
    public Guid OperationId { get; }
    public Guid PrincipalId { get; }
    public Guid SessionId { get; }
    public long AuthorizationRevision { get; }
    public Guid StepUpGrantId { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public string? PreviousContentHash { get; }
    public string ContentHash { get; }
}

/// <summary>Exact rules captured for future trace work; it has no run or inspection identity.</summary>
public sealed class TraceStoragePolicySnapshot
{
    public TraceStoragePolicySnapshot(TraceStoragePolicyPublication publication)
    {
        Publication = publication ?? throw new ArgumentNullException(nameof(publication));
        RetentionRules = new ReadOnlyCollection<TraceRetentionRule>(
            publication.Policy.RetentionRules.ToArray());
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-storage-policy-snapshot-v1", Publication.ContentHash,
            Publication.Policy.ContentHash,
            RetentionRules.Count.ToString(CultureInfo.InvariantCulture)
        }.Concat(RetentionRules.Select(value => value.ContentHash)));
    }

    public TraceStoragePolicyPublication Publication { get; }
    public long Version => Publication.Version;
    public TraceStoragePolicyDefinition Policy => Publication.Policy;
    public ReadOnlyCollection<TraceRetentionRule> RetentionRules { get; }
    public string ContentHash { get; }
}

public sealed record TraceStoragePolicyAccess(bool Allowed, string ReasonCode, bool RequiresStepUp);
public sealed record TraceStoragePolicyReadResult(bool Available, string ReasonCode,
    TraceStoragePolicyPublication? Publication = null, TraceStoragePolicySnapshot? Snapshot = null);
public sealed record TraceStoragePolicyResult(RuntimeCommandOutcome Outcome,
    TraceStoragePolicyPublication? Publication = null, TraceStoragePolicySnapshot? Snapshot = null)
{
    public bool Succeeded => Outcome.Disposition == CommandDisposition.Accepted &&
        Outcome.Audit == AuditPersistence.Persisted;
}

internal static class TraceStoragePolicyValidation
{
    internal const int MaximumTextLength = 4096;
    internal static readonly TimeSpan MaximumRetention = TimeSpan.FromDays(36500);

    internal static string Identifier(string value, string parameterName, int maximumLength = 128)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length < 1 || value.Length > maximumLength || value.Trim() != value ||
            value.Any(char.IsControl))
            throw new ArgumentException("TraceStorageIdentifierInvalid", parameterName);
        foreach (var character in value)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException("TraceStorageIdentifierInvalid", parameterName);
        }
        return value;
    }

    internal static string Text(string value, string parameterName, int maximumLength)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length < 1 || value.Length > maximumLength || value.Any(char.IsControl) ||
            value.Trim().Length == 0)
            throw new ArgumentException("TraceStorageTextInvalid", parameterName);
        try { _ = new UTF8Encoding(false, true).GetBytes(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("TraceStorageTextInvalid", parameterName, exception);
        }
        return value;
    }

    internal static string Hash(string value, string parameterName)
    {
        if (value is null) throw new ArgumentNullException(parameterName);
        if (value.Length != 64 || value.Any(character =>
                !((character is >= '0' and <= '9') || (character is >= 'A' and <= 'F') ||
                  (character is >= 'a' and <= 'f'))))
            throw new ArgumentException("TraceStorageHashInvalid", parameterName);
        return value.ToUpperInvariant();
    }

    internal static List<T> Copy<T>(IEnumerable<T> values, string parameterName, int maximumCount)
    {
        if (values is null) throw new ArgumentNullException(parameterName);
        var copied = new List<T>(Math.Min(maximumCount, 16));
        using var enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (copied.Count == maximumCount)
                throw new ArgumentException("TraceStorageCollectionCapacityExceeded", parameterName);
            if (enumerator.Current is null)
                throw new ArgumentException("TraceStorageCollectionContainsNull", parameterName);
            copied.Add(enumerator.Current);
        }
        return copied;
    }

    internal static void Retention(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > MaximumRetention)
            throw new ArgumentOutOfRangeException(parameterName, "TraceStorageRetentionDurationInvalid");
    }

    internal static DateTimeOffset RetentionExpiry(DateTimeOffset startedAtUtc, TimeSpan retention)
    {
        if (startedAtUtc == default || startedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("TraceStorageObligationTimestampInvalid", nameof(startedAtUtc));
        try { return checked(startedAtUtc + retention); }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("TraceStorageObligationExpiryOverflow", exception);
        }
    }

    internal static TimeSpan CommitTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.FromMilliseconds(1) || value > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(parameterName, "TraceStorageTimeoutInvalid");
        return value;
    }

    internal static string Duration(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);
}
