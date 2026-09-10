using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>The immutable event kinds in the schema-22 admission authorization ledger.
/// Completed means the authorization and report were finalized, not that the station reached Armed.</summary>
public enum ProductionAdmissionEventKind : byte
{
    Admitted = 1,
    Rejected = 2,
    Completed = 3,
    Failed = 4
}

/// <summary>
/// A durable, read-only admission report event.  The report is an observation
/// of the fixed gates; the surrounding fields bind it to the command, actor,
/// authorization decision, durable heads, and central audit chain.
/// </summary>
public sealed record ProductionAdmissionHistoryEvent
{
    internal ProductionAdmissionHistoryEvent(long position, ProductionAdmissionEventKind kind,
        ProductionAdmissionReport report, Guid correlationId, Guid attemptId, Guid runtimeEpoch,
        long admissionGeneration, Guid actorPrincipalId, Guid actorSessionId,
        long actorAuthorizationRevision, RecipeContractReference authorizationPolicy,
        Guid? stepUpGrantId, IReadOnlyDictionary<string, string> expectedDurableHeads,
        IReadOnlyDictionary<string, string> currentDurableHeads, string authorizationTarget,
        string reasonCode, long? commandAuditSequence, string? commandAuditHash,
        long? authorizationAuditSequence, string? authorizationAuditHash, long auditSequence,
        string? auditHash, string? payloadHash, string? contentHash = null)
    {
        if (position < 1 || !Enum.IsDefined(kind) || report is null || correlationId == Guid.Empty ||
            attemptId == Guid.Empty || runtimeEpoch == Guid.Empty || admissionGeneration < 0 ||
            actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty ||
            actorAuthorizationRevision < 0)
            throw new ArgumentException("ProductionAdmissionHistoryIdentityInvalid");
        if (commandAuditSequence is < 1 || authorizationAuditSequence is < 1 || auditSequence < 0)
            throw new ArgumentException("ProductionAdmissionHistoryAuditReferenceInvalid");
        if (commandAuditSequence.HasValue != (commandAuditHash is not null) ||
            authorizationAuditSequence.HasValue != (authorizationAuditHash is not null) ||
            (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("ProductionAdmissionHistoryAuditReferenceInvalid");
        Report = report;
        Position = position;
        Kind = kind;
        CorrelationId = correlationId;
        AttemptId = attemptId;
        RuntimeEpoch = runtimeEpoch;
        AdmissionGeneration = admissionGeneration;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision;
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        StepUpGrantId = stepUpGrantId;
        ExpectedDurableHeads = CopyHeads(expectedDurableHeads, nameof(expectedDurableHeads));
        CurrentDurableHeads = CopyHeads(currentDurableHeads, nameof(currentDurableHeads));
        AuthorizationTarget = RecipeActivationValidation.Hash(authorizationTarget, nameof(authorizationTarget));
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = Hash(commandAuditHash, nameof(commandAuditHash));
        AuthorizationAuditSequence = authorizationAuditSequence;
        AuthorizationAuditHash = Hash(authorizationAuditHash, nameof(authorizationAuditHash));
        AuditSequence = auditSequence;
        AuditHash = Hash(auditHash, nameof(auditHash));
        PayloadHash = Hash(payloadHash, nameof(payloadHash));
        ContentHash = contentHash is null ? ComputeContentHash() :
            RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(), StringComparison.Ordinal))
            throw new ArgumentException("ProductionAdmissionHistoryContentHashMismatch", nameof(contentHash));
    }

    public long Position { get; }
    public ProductionAdmissionEventKind Kind { get; }
    public ProductionAdmissionReport Report { get; }
    public Guid CorrelationId { get; }
    public Guid AttemptId { get; }
    public Guid RuntimeEpoch { get; }
    public long AdmissionGeneration { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public Guid? StepUpGrantId { get; }
    public ReadOnlyDictionary<string, string> ExpectedDurableHeads { get; }
    public ReadOnlyDictionary<string, string> CurrentDurableHeads { get; }
    public string AuthorizationTarget { get; }
    public string ReasonCode { get; }
    public long? CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long? AuthorizationAuditSequence { get; }
    public string? AuthorizationAuditHash { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public string? PayloadHash { get; }
    public string ContentHash { get; }

    private string ComputeContentHash()
    {
        var fields = new List<string?>(32 +
            (ExpectedDurableHeads.Count + CurrentDurableHeads.Count) * 2 +
            Report.Gates.Count * 8)
        {
            "sharpinspect-production-admission-history-v1",
            Position.ToString(CultureInfo.InvariantCulture), Kind.ToString(), Report.ContentHash,
            CorrelationId.ToString("D"), AttemptId.ToString("D"), RuntimeEpoch.ToString("D"),
            AdmissionGeneration.ToString(CultureInfo.InvariantCulture), ActorPrincipalId.ToString("D"),
            ActorSessionId.ToString("D"), ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            AuthorizationPolicy.Id, AuthorizationPolicy.Version, AuthorizationPolicy.ContentHash,
            StepUpGrantId?.ToString("D"), AuthorizationTarget, ReasonCode,
            CommandAuditSequence?.ToString(CultureInfo.InvariantCulture), CommandAuditHash,
            AuthorizationAuditSequence?.ToString(CultureInfo.InvariantCulture), AuthorizationAuditHash,
            AuditSequence.ToString(CultureInfo.InvariantCulture), AuditHash, PayloadHash
        };
        fields.Add("expected-heads");
        foreach (var pair in ExpectedDurableHeads) fields.AddRange(new[] { pair.Key, pair.Value });
        fields.Add("current-heads");
        foreach (var pair in CurrentDurableHeads) fields.AddRange(new[] { pair.Key, pair.Value });
        return AlgorithmContractValidation.HashParts(fields);
    }

    private static ReadOnlyDictionary<string, string> CopyHeads(
        IReadOnlyDictionary<string, string> heads, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(heads, parameterName);
        if (heads.Count > 64) throw new ArgumentException("ProductionAdmissionDurableHeadLimitExceeded", parameterName);
        var copied = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in heads)
            copied.Add(ProductionAdmissionIdentifier(pair.Key, parameterName),
                RecipeActivationValidation.Hash(pair.Value, parameterName));
        return new ReadOnlyDictionary<string, string>(copied);
    }

    private static string ProductionAdmissionIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("ProductionAdmissionIdentifierInvalid", parameterName);
        return value;
    }

    private static string? Hash(string? value, string parameterName) => value is null
        ? null : RecipeActivationValidation.Hash(value, parameterName);
}

public sealed record ProductionAdmissionHistoryFilter(Guid? CorrelationId = null,
    Guid? AttemptId = null, long AfterPosition = 0, long? ThroughPosition = null,
    int PageSize = 20);

public sealed record ProductionAdmissionHistoryReadResult(bool Available, string ReasonCode,
    ProductionAdmissionHistoryEvent? Latest = null, bool RecoveryRequired = false);

public sealed record ProductionAdmissionHistoryPage(bool Available, string ReasonCode,
    ReadOnlyCollection<ProductionAdmissionHistoryEvent> Events, long ThroughPosition,
    long? NextAfterPosition, ProductionAdmissionHistoryEvent? Pending = null,
    bool RecoveryRequired = false);

/// <summary>Bounded read-only access to the schema-22 admission report ledger.</summary>
public interface IProductionAdmissionHistoryQuery
{
    ValueTask<ProductionAdmissionHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<ProductionAdmissionHistoryReadResult> ReadAsync(Guid correlationId,
        CancellationToken cancellationToken = default);
    ValueTask<ProductionAdmissionHistoryPage> QueryAsync(ProductionAdmissionHistoryFilter filter,
        CancellationToken cancellationToken = default);
}
