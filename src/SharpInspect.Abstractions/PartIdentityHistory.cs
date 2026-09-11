using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Independent append-only events for identity failures and corrections.</summary>
public enum PartIdentityHistoryEventKind : byte
{
    RejectedTrigger = 1,
    Correction = 2
}

/// <summary>
/// Immutable evidence retained when a trigger is rejected before an InspectionId exists.
/// A provider observation is optional because a missing provider/capability is itself a
/// rejection; the requirement and cycle-level source state remain durable.
/// </summary>
public sealed class PartIdentityRejectionContext
{
    public PartIdentityRejectionContext(PartIdentityRequirement? requirement,
        PartIdentityProviderBinding? binding = null,
        PartIdentityProviderObservation? observation = null,
        long connectionGeneration = 0,
        IEnumerable<byte>? rawProofBytes = null,
        string? rawProofHash = null,
        string? readEvidenceHash = null)
    {
        if (connectionGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(connectionGeneration));
        if (binding is not null && requirement is { Mode: PartIdentityRequirementMode.None })
            throw new ArgumentException("PartIdentityRejectedNoneCannotBindProvider", nameof(binding));

        var copied = rawProofBytes?.ToArray() ?? Array.Empty<byte>();
        if (copied.Length > 4096)
            throw new ArgumentException("PartIdentityRejectedProofTooLarge", nameof(rawProofBytes));
        var computedHash = copied.Length == 0 ? null : AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-part-identity-rejection-proof-v1", Convert.ToHexString(copied)
        });
        if (rawProofHash is not null)
        {
            rawProofHash = RecipeActivationValidation.Hash(rawProofHash, nameof(rawProofHash));
            if (computedHash is not null && !string.Equals(rawProofHash, computedHash,
                    StringComparison.Ordinal))
                throw new ArgumentException("PartIdentityRejectedProofHashMismatch", nameof(rawProofHash));
        }
        if (readEvidenceHash is not null)
            readEvidenceHash = RecipeActivationValidation.Hash(readEvidenceHash, nameof(readEvidenceHash));

        Binding = binding;
        Observation = observation;
        Requirement = requirement;
        ConnectionGeneration = connectionGeneration;
        RawProofBytes = new ReadOnlyCollection<byte>(copied);
        RawProofHash = rawProofHash ?? computedHash;
        ReadEvidenceHash = readEvidenceHash;
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-part-identity-rejection-context-v2", requirement?.ContentHash,
            binding?.ContentHash, observation?.ContentHash,
            connectionGeneration.ToString(CultureInfo.InvariantCulture), RawProofHash,
            Convert.ToHexString(copied), ReadEvidenceHash
        });
    }

    public PartIdentityRequirement? Requirement { get; }
    public PartIdentityProviderBinding? Binding { get; }
    public PartIdentityProviderObservation? Observation { get; }
    public long ConnectionGeneration { get; }
    public ReadOnlyCollection<byte> RawProofBytes { get; }
    public string? RawProofHash { get; }
    /// <summary>Hash of the complete provider/PLC read protocol evidence, if available.</summary>
    public string? ReadEvidenceHash { get; }
    public string ContentHash { get; }
}

/// <summary>
/// One schema-29 history row.  Rejected triggers deliberately have no
/// InspectionId.  Corrections only point at an existing immutable production
/// admission and carry the old/new values; the original Core is never updated.
/// </summary>
public sealed record PartIdentityHistoryEvent
{
    internal PartIdentityHistoryEvent(long position, string? previousHash, Guid eventId,
        PartIdentityHistoryEventKind kind, Guid correlationId, Guid attemptId,
        Guid runtimeEpoch, string stationId, uint controllerEpoch, uint cycleSequence,
        string endpointBindingHash, PartIdentityEvidence? evidence, string reasonCode,
        Guid? inspectionId = null, string? admissionContentHash = null,
        string? expectedPreviousCorrectionHash = null, string? oldValue = null,
        string? newValue = null, Guid? actorPrincipalId = null, Guid? actorSessionId = null,
        long authorizationRevision = 0, Guid? stepUpGrantId = null,
        string? authorizationTarget = null, DateTimeOffset? recordedAtUtc = null,
        long auditSequence = 0, string? auditHash = null, string? contentHash = null,
        long commandAuditSequence = 0, string? commandAuditHash = null,
        long authorizationAuditSequence = 0, string? authorizationAuditHash = null,
        PartIdentityRejectionContext? rejectionContext = null)
    {
        if (position < 1 || eventId == Guid.Empty || correlationId == Guid.Empty ||
            attemptId == Guid.Empty || runtimeEpoch == Guid.Empty)
            throw new ArgumentException("PartIdentityHistoryIdentityInvalid");
        if (!Enum.IsDefined(typeof(PartIdentityHistoryEventKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (auditSequence < 0 || (auditSequence == 0) != (auditHash is null))
            throw new ArgumentException("PartIdentityHistoryAuditReferenceInvalid");
        if (commandAuditSequence < 0 || (commandAuditSequence == 0) != (commandAuditHash is null) ||
            authorizationAuditSequence < 0 ||
            (authorizationAuditSequence == 0) != (authorizationAuditHash is null))
            throw new ArgumentException("PartIdentityHistoryAuditReferenceInvalid");
        StationId = AlgorithmContractValidation.Identifier(stationId, nameof(stationId));
        EndpointBindingHash = RecipeActivationValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode));
        RecordedAtUtc = Utc(recordedAtUtc ?? DateTimeOffset.UtcNow, nameof(recordedAtUtc));
        PreviousHash = OptionalHash(previousHash, nameof(previousHash));
        AuditHash = OptionalHash(auditHash, nameof(auditHash));
        Evidence = evidence;
        RejectionContext = rejectionContext;
        if (kind == PartIdentityHistoryEventKind.RejectedTrigger)
        {
            if (rejectionContext is null)
                throw new ArgumentException("PartIdentityRejectedContextRequired", nameof(rejectionContext));
            if (evidence is not null)
                throw new ArgumentException("PartIdentityRejectedAcceptedEvidenceForbidden",
                    nameof(evidence));
            if (inspectionId is not null || admissionContentHash is not null || actorPrincipalId is not null ||
                actorSessionId is not null || newValue is not null || oldValue is not null ||
                expectedPreviousCorrectionHash is not null)
                throw new ArgumentException("PartIdentityRejectedTriggerMustNotReferenceInspection");
            // A rejected read may be Missing/Stale/Ambiguous/Invalid and therefore
            // has no accepted PartIdentityEvidence object.  The immutable cycle
            // identity below plus ReasonCode is the rejection evidence; accepted
            // evidence belongs to the production admission/Core ledger.
        }
        else
        {
            if (rejectionContext is not null)
                throw new ArgumentException("PartIdentityCorrectionCannotCarryRejectionContext",
                    nameof(rejectionContext));
            if (controllerEpoch == 0 || cycleSequence == 0 ||
                inspectionId is null || inspectionId == Guid.Empty ||
                admissionContentHash is null || newValue is null ||
                actorPrincipalId is null || actorPrincipalId == Guid.Empty ||
                actorSessionId is null || actorSessionId == Guid.Empty || authorizationRevision < 0 ||
                authorizationTarget is null || stepUpGrantId is null || stepUpGrantId == Guid.Empty)
                throw new ArgumentException("PartIdentityCorrectionBindingInvalid");
            _ = RecipeActivationValidation.Hash(admissionContentHash, nameof(admissionContentHash));
            if (oldValue is not null)
                _ = AlgorithmContractValidation.BoundedText(oldValue, nameof(oldValue), 256);
            _ = AlgorithmContractValidation.BoundedText(newValue, nameof(newValue), 256);
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                throw new ArgumentException("PartIdentityCorrectionNoChange");
            authorizationTarget = AlgorithmContractValidation.BoundedText(authorizationTarget,
                nameof(authorizationTarget), 256);
            if (evidence is null ||
                (expectedPreviousCorrectionHash is null &&
                 !string.Equals(evidence.Value, oldValue, StringComparison.Ordinal)))
                throw new ArgumentException("PartIdentityCorrectionEvidenceMismatch");
            expectedPreviousCorrectionHash = OptionalHash(expectedPreviousCorrectionHash,
                nameof(expectedPreviousCorrectionHash));
        }

        Position = position;
        EventId = eventId;
        Kind = kind;
        CorrelationId = correlationId;
        AttemptId = attemptId;
        RuntimeEpoch = runtimeEpoch;
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        InspectionId = inspectionId;
        AdmissionContentHash = admissionContentHash is null ? null :
            RecipeActivationValidation.Hash(admissionContentHash, nameof(admissionContentHash));
        ExpectedPreviousCorrectionHash = expectedPreviousCorrectionHash;
        OldValue = oldValue;
        NewValue = newValue;
        ActorPrincipalId = actorPrincipalId;
        ActorSessionId = actorSessionId;
        AuthorizationRevision = authorizationRevision;
        StepUpGrantId = stepUpGrantId;
        AuthorizationTarget = authorizationTarget;
        AuditSequence = auditSequence;
        CommandAuditSequence = commandAuditSequence;
        CommandAuditHash = OptionalHash(commandAuditHash, nameof(commandAuditHash));
        AuthorizationAuditSequence = authorizationAuditSequence;
        AuthorizationAuditHash = OptionalHash(authorizationAuditHash, nameof(authorizationAuditHash));
        ContentHash = contentHash is null ? ComputeContentHash() :
            RecipeActivationValidation.Hash(contentHash, nameof(contentHash));
        if (contentHash is not null && !string.Equals(ContentHash, ComputeContentHash(),
                StringComparison.Ordinal))
            throw new ArgumentException("PartIdentityHistoryContentHashMismatch", nameof(contentHash));
    }

    public long Position { get; }
    public string? PreviousHash { get; }
    public Guid EventId { get; }
    public PartIdentityHistoryEventKind Kind { get; }
    public Guid CorrelationId { get; }
    public Guid AttemptId { get; }
    public Guid RuntimeEpoch { get; }
    public string StationId { get; }
    public uint ControllerEpoch { get; }
    public uint CycleSequence { get; }
    public string EndpointBindingHash { get; }
    public PartIdentityEvidence? Evidence { get; }
    public PartIdentityRejectionContext? RejectionContext { get; }
    public string ReasonCode { get; }
    public Guid? InspectionId { get; }
    public string? AdmissionContentHash { get; }
    public string? ExpectedPreviousCorrectionHash { get; }
    public string? OldValue { get; }
    public string? NewValue { get; }
    public Guid? ActorPrincipalId { get; }
    public Guid? ActorSessionId { get; }
    public long AuthorizationRevision { get; }
    public Guid? StepUpGrantId { get; }
    public string? AuthorizationTarget { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public long AuditSequence { get; }
    public string? AuditHash { get; }
    public long CommandAuditSequence { get; }
    public string? CommandAuditHash { get; }
    public long AuthorizationAuditSequence { get; }
    public string? AuthorizationAuditHash { get; }
    public string ContentHash { get; }

    private string ComputeContentHash() => AlgorithmContractValidation.HashParts(new string?[]
    {
        "sharpinspect-part-identity-history-v1", Position.ToString(CultureInfo.InvariantCulture),
        PreviousHash, EventId.ToString("D"), Kind.ToString(), CorrelationId.ToString("D"),
        AttemptId.ToString("D"), RuntimeEpoch.ToString("D"), StationId,
        ControllerEpoch.ToString(CultureInfo.InvariantCulture), CycleSequence.ToString(CultureInfo.InvariantCulture),
        EndpointBindingHash, Evidence?.ContentHash, ReasonCode, InspectionId?.ToString("D"),
        AdmissionContentHash, ExpectedPreviousCorrectionHash, OldValue, NewValue,
        ActorPrincipalId?.ToString("D"), ActorSessionId?.ToString("D"),
        AuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId?.ToString("D"),
        AuthorizationTarget, RejectionContext?.ContentHash,
        RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        CommandAuditSequence.ToString(CultureInfo.InvariantCulture), CommandAuditHash,
        AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture), AuthorizationAuditHash
    });

    private static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("PartIdentityTimestampInvalid", parameterName);
        return value;
    }

    private static string? OptionalHash(string? value, string parameterName) => value is null ? null :
        RecipeActivationValidation.Hash(value, parameterName);
}

public sealed record PartIdentityHistoryFilter(Guid? InspectionId = null,
    Guid? CorrelationId = null, Guid? RuntimeEpoch = null, long AfterPosition = 0,
    long? ThroughPosition = null, int PageSize = 20);

public sealed record PartIdentityHistoryReadResult(bool Available, string ReasonCode,
    PartIdentityHistoryEvent? Latest = null, bool RecoveryRequired = false);

public sealed record PartIdentityHistoryPage
{
    public PartIdentityHistoryPage(bool available, string reasonCode,
        IEnumerable<PartIdentityHistoryEvent>? events, long throughPosition,
        long? nextAfterPosition, bool recoveryRequired = false)
    {
        Available = available;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        Events = new ReadOnlyCollection<PartIdentityHistoryEvent>(
            (events ?? Array.Empty<PartIdentityHistoryEvent>()).ToArray());
        if (throughPosition < 0 || nextAfterPosition is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughPosition));
        ThroughPosition = throughPosition;
        NextAfterPosition = nextAfterPosition;
        RecoveryRequired = recoveryRequired;
    }

    public bool Available { get; }
    public string ReasonCode { get; }
    public ReadOnlyCollection<PartIdentityHistoryEvent> Events { get; }
    public long ThroughPosition { get; }
    public long? NextAfterPosition { get; }
    public bool RecoveryRequired { get; }
}

/// <summary>Read-only schema-29 identity-failure/correction history.</summary>
public interface IPartIdentityHistoryQuery
{
    ValueTask<PartIdentityHistoryReadResult> ReadCurrentAsync(
        CancellationToken cancellationToken = default);
    ValueTask<PartIdentityHistoryReadResult> ReadAsync(Guid inspectionId,
        CancellationToken cancellationToken = default);
    ValueTask<PartIdentityHistoryPage> QueryAsync(PartIdentityHistoryFilter filter,
        CancellationToken cancellationToken = default);
}

/// <summary>Correction request; authorization is always re-evaluated by Runtime.</summary>
public sealed record CorrectProductionPartIdentityCommand : RuntimeCommand
{
    public CorrectProductionPartIdentityCommand(Guid correlationId, CommandInvocation invocation,
        Guid inspectionId, string admissionContentHash, string? expectedPreviousCorrectionHash,
        string? previousValue, string correctedValue, string reasonCode, string? authorizationTarget = null)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty || inspectionId == Guid.Empty)
            throw new ArgumentException("PartIdentityCorrectionIdentityInvalid");
        ArgumentNullException.ThrowIfNull(invocation);
        InspectionId = inspectionId;
        AdmissionContentHash = RecipeActivationValidation.Hash(admissionContentHash,
            nameof(AdmissionContentHash));
        ExpectedPreviousCorrectionHash = expectedPreviousCorrectionHash is null ? null :
            RecipeActivationValidation.Hash(expectedPreviousCorrectionHash,
                nameof(ExpectedPreviousCorrectionHash));
        PreviousValue = previousValue is null ? null :
            AlgorithmContractValidation.BoundedText(previousValue, nameof(PreviousValue), 256);
        CorrectedValue = AlgorithmContractValidation.BoundedText(correctedValue, nameof(CorrectedValue), 256);
        if (string.Equals(previousValue, correctedValue, StringComparison.Ordinal))
            throw new ArgumentException("PartIdentityCorrectionNoChange");
        ReasonCode = RecipeActivationValidation.Reason(reasonCode, nameof(ReasonCode));
        var expectedTarget = ComputeAuthorizationTarget(InspectionId, AdmissionContentHash,
            ExpectedPreviousCorrectionHash, PreviousValue, CorrectedValue, ReasonCode);
        if (authorizationTarget is not null && !string.Equals(
                AlgorithmContractValidation.BoundedText(authorizationTarget,
                    nameof(authorizationTarget), 256), expectedTarget, StringComparison.Ordinal))
            throw new ArgumentException("PartIdentityCorrectionAuthorizationTargetMismatch",
                nameof(authorizationTarget));
        AuthorizationTarget = expectedTarget;
    }

    public Guid InspectionId { get; }
    public string AdmissionContentHash { get; }
    public string? ExpectedPreviousCorrectionHash { get; }
    public string? PreviousValue { get; }
    public string CorrectedValue { get; }
    public string ReasonCode { get; }
    public string AuthorizationTarget { get; }

    public static string ComputeAuthorizationTarget(Guid inspectionId, string admissionContentHash,
        string? expectedPreviousCorrectionHash, string? previousValue, string correctedValue,
        string reasonCode) => AlgorithmContractValidation.HashParts(new string?[]
    {
        "sharpinspect-part-identity-correction-target-v1", inspectionId.ToString("D"),
        RecipeActivationValidation.Hash(admissionContentHash, nameof(admissionContentHash)),
        expectedPreviousCorrectionHash, previousValue, correctedValue,
        RecipeActivationValidation.Reason(reasonCode, nameof(reasonCode))
    });
}

public sealed record PartIdentityCorrectionResult
{
    public PartIdentityCorrectionResult(RuntimeCommandOutcome outcome,
        PartIdentityHistoryEvent? @event = null)
    {
        Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        Event = @event;
    }

    public RuntimeCommandOutcome Outcome { get; }
    public PartIdentityHistoryEvent? Event { get; }
}
