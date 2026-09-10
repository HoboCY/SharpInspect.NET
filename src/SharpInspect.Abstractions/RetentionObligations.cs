using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// A pure value calculation for one artifact hash and one frozen policy rule.
/// It is not a production run, inspection record, or persistence command.
/// </summary>
public sealed class TraceRetentionObligation
{
    public TraceRetentionObligation(string artifactContentHash, TraceStoragePolicySnapshot snapshot,
        TraceRetentionRule rule, DateTimeOffset startedAtUtc)
    {
        ArtifactContentHash = TraceStoragePolicyValidation.Hash(artifactContentHash,
            nameof(artifactContentHash));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentNullException.ThrowIfNull(rule);
        Rule = snapshot.RetentionRules.FirstOrDefault(candidate =>
            candidate.ContentHash == rule.ContentHash &&
            candidate.EvidenceClass == rule.EvidenceClass &&
            candidate.StartsAt == rule.StartsAt &&
            candidate.MinimumRetention == rule.MinimumRetention)
            ?? throw new ArgumentException("TraceStorageRetentionRuleNotInSnapshot", nameof(rule));
        StartedAtUtc = ValidateUtc(startedAtUtc, nameof(startedAtUtc));
        RetainUntilUtc = TraceStoragePolicyValidation.RetentionExpiry(StartedAtUtc,
            Rule.MinimumRetention);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-retention-obligation-v1", ArtifactContentHash,
            Snapshot.ContentHash, Snapshot.Publication.ContentHash,
            Rule.ContentHash, StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            RetainUntilUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public string ArtifactContentHash { get; }
    public TraceStoragePolicySnapshot Snapshot { get; }
    public string SnapshotHash => Snapshot.ContentHash;
    public string PublicationHash => Snapshot.Publication.ContentHash;
    public TraceRetentionRule Rule { get; }
    public TraceRetentionClass EvidenceClass => Rule.EvidenceClass;
    public RetentionStartEvent StartsAt => Rule.StartsAt;
    public DateTimeOffset StartedAtUtc { get; }
    public TimeSpan MinimumRetention => Rule.MinimumRetention;
    public DateTimeOffset RetainUntilUtc { get; }
    public string ContentHash { get; }

    internal static DateTimeOffset ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("TraceStorageObligationTimestampInvalid", parameterName);
        return value;
    }
}

/// <summary>An append-only extension. Its deadline must strictly increase.</summary>
public sealed class TraceRetentionExtension
{
    public TraceRetentionExtension(TraceRetentionObligation obligation,
        DateTimeOffset extendedUntilUtc, string reason, DateTimeOffset extendedAtUtc)
        : this(obligation, null, extendedUntilUtc, reason, extendedAtUtc)
    {
    }

    public TraceRetentionExtension(TraceRetentionExtension predecessor,
        DateTimeOffset extendedUntilUtc, string reason, DateTimeOffset extendedAtUtc)
        : this(predecessor?.Obligation ?? throw new ArgumentNullException(nameof(predecessor)),
            predecessor, extendedUntilUtc, reason, extendedAtUtc)
    {
    }

    private TraceRetentionExtension(TraceRetentionObligation obligation,
        TraceRetentionExtension? predecessor, DateTimeOffset extendedUntilUtc,
        string reason, DateTimeOffset extendedAtUtc)
    {
        Obligation = obligation ?? throw new ArgumentNullException(nameof(obligation));
        if (predecessor is not null && predecessor.Obligation.ContentHash != obligation.ContentHash)
            throw new ArgumentException("TraceStorageRetentionExtensionObligationMismatch",
                nameof(predecessor));
        Predecessor = predecessor;
        PreviousEffectiveUntilUtc = predecessor?.EffectiveUntilUtc ?? obligation.RetainUntilUtc;
        ExtendedUntilUtc = TraceRetentionObligation.ValidateUtc(extendedUntilUtc,
            nameof(extendedUntilUtc));
        if (ExtendedUntilUtc <= PreviousEffectiveUntilUtc)
            throw new ArgumentException("TraceStorageRetentionExtensionMustExtend",
                nameof(extendedUntilUtc));
        Reason = TraceStoragePolicyValidation.Text(reason, nameof(reason), 512);
        ExtendedAtUtc = TraceRetentionObligation.ValidateUtc(extendedAtUtc,
            nameof(extendedAtUtc));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-retention-extension-v1", obligation.ContentHash,
            predecessor?.ContentHash ?? obligation.ContentHash,
            PreviousEffectiveUntilUtc.ToString("O", CultureInfo.InvariantCulture),
            ExtendedUntilUtc.ToString("O", CultureInfo.InvariantCulture), Reason,
            ExtendedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public TraceRetentionObligation Obligation { get; }
    public TraceRetentionExtension? Predecessor { get; }
    public DateTimeOffset PreviousEffectiveUntilUtc { get; }
    public DateTimeOffset ExtendedUntilUtc { get; }
    public DateTimeOffset EffectiveUntilUtc => ExtendedUntilUtc;
    public string Reason { get; }
    public DateTimeOffset ExtendedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>A hold is an append-only protection over one exact obligation hash.</summary>
public sealed class TraceRetentionHold
{
    public TraceRetentionHold(string holdId, TraceRetentionObligation obligation,
        string reason, DateTimeOffset placedAtUtc)
    {
        HoldId = TraceStoragePolicyValidation.Identifier(holdId, nameof(holdId));
        Obligation = obligation ?? throw new ArgumentNullException(nameof(obligation));
        Reason = TraceStoragePolicyValidation.Text(reason, nameof(reason), 512);
        PlacedAtUtc = TraceRetentionObligation.ValidateUtc(placedAtUtc, nameof(placedAtUtc));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-retention-hold-v1", HoldId, Obligation.ContentHash,
            Reason, PlacedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public string HoldId { get; }
    public TraceRetentionObligation Obligation { get; }
    public string Reason { get; }
    public DateTimeOffset PlacedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>
/// Exact release evidence for one hold. There is intentionally no shortening or
/// deletion operation in this contract.
/// </summary>
public sealed class TraceRetentionHoldRelease
{
    public TraceRetentionHoldRelease(TraceRetentionHold hold, string holdId,
        string obligationContentHash, string reason, DateTimeOffset releasedAtUtc)
    {
        Hold = hold ?? throw new ArgumentNullException(nameof(hold));
        if (!string.Equals(hold.HoldId, holdId, StringComparison.Ordinal))
            throw new ArgumentException("TraceStorageRetentionHoldMismatch", nameof(holdId));
        var normalizedHash = TraceStoragePolicyValidation.Hash(obligationContentHash,
            nameof(obligationContentHash));
        if (!string.Equals(hold.Obligation.ContentHash, normalizedHash, StringComparison.Ordinal))
            throw new ArgumentException("TraceStorageRetentionHoldMismatch",
                nameof(obligationContentHash));
        HoldId = holdId;
        ObligationContentHash = normalizedHash;
        Reason = TraceStoragePolicyValidation.Text(reason, nameof(reason), 512);
        ReleasedAtUtc = TraceRetentionObligation.ValidateUtc(releasedAtUtc,
            nameof(releasedAtUtc));
        if (ReleasedAtUtc < Hold.PlacedAtUtc)
            throw new ArgumentException("TraceStorageRetentionHoldReleaseBeforePlacement",
                nameof(releasedAtUtc));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-trace-retention-hold-release-v1", Hold.ContentHash,
            HoldId, ObligationContentHash, Reason,
            ReleasedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public TraceRetentionHold Hold { get; }
    public string HoldId { get; }
    public string ObligationContentHash { get; }
    public string Reason { get; }
    public DateTimeOffset ReleasedAtUtc { get; }
    public string ContentHash { get; }
}
