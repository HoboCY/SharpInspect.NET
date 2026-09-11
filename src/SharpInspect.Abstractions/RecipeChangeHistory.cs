using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Observed PLC request identity and the frozen selection revision used for its decision.</summary>
public sealed class RecipeChangeRequestEvidence
{
    internal RecipeChangeRequestEvidence(Guid runtimeEpoch, string endpointContentHash,
        RecipeContractReference protocolProfile, uint controllerEpoch, uint requestSequence, uint selectionCode,
        RecipeSelectionReference? selectionRevision, RecipeContractReference selectionPolicy,
        RecipeContractReference? selectionMap, RecipeSelectionMapEntry? target, DateTimeOffset observedAtUtc)
    {
        RuntimeEpoch = RecipeActivationValidation.RequiredGuid(runtimeEpoch, nameof(runtimeEpoch));
        EndpointContentHash = RecipeActivationValidation.Hash(endpointContentHash, nameof(endpointContentHash));
        ProtocolProfile = protocolProfile ?? throw new ArgumentNullException(nameof(protocolProfile));
        ControllerEpoch = controllerEpoch; RequestSequence = requestSequence; SelectionCode = selectionCode;
        SelectionRevision = selectionRevision;
        SelectionPolicy = selectionPolicy ?? throw new ArgumentNullException(nameof(selectionPolicy));
        SelectionMap = selectionMap;
        Target = target;
        if (selectionRevision is null && (selectionPolicy != RecipeSelectionPolicy.Default.Reference || selectionMap is not null) ||
            target is not null && (target.SelectionCode != selectionCode || selectionMap is null))
            throw new ArgumentException("RecipeChangeSelectionEvidenceInvalid");
        ObservedAtUtc = RecipeActivationValidation.Utc(observedAtUtc, nameof(observedAtUtc));
        RequestIdentityHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-recipe-activation-request-identity-v1", EndpointContentHash,
            ControllerEpoch.ToString(CultureInfo.InvariantCulture), RequestSequence.ToString(CultureInfo.InvariantCulture)
        });
        OperationId = new Guid(Convert.FromHexString(AlgorithmContractValidation.HashParts(new[]
            { "sharpinspect-recipe-change-operation-v1", RequestIdentityHash })).AsSpan(0, 16));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-change-request-evidence-v1", RuntimeEpoch.ToString("D"), EndpointContentHash,
            ProtocolProfile.Id, ProtocolProfile.Version, ProtocolProfile.ContentHash,
            ControllerEpoch.ToString(CultureInfo.InvariantCulture), RequestSequence.ToString(CultureInfo.InvariantCulture),
            SelectionCode.ToString(CultureInfo.InvariantCulture), SelectionRevision?.Position.ToString(CultureInfo.InvariantCulture),
            SelectionRevision?.RevisionId.ToString("D"), SelectionRevision?.ContentHash, SelectionPolicy.Id,
            SelectionPolicy.Version, SelectionPolicy.ContentHash, SelectionMap?.Id, SelectionMap?.Version,
            SelectionMap?.ContentHash, Target?.ContentHash, SystemPrincipalId,
            ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture), RequestIdentityHash, OperationId.ToString("D")
        });
    }
    public Guid RuntimeEpoch { get; }
    public string EndpointContentHash { get; }
    public RecipeContractReference ProtocolProfile { get; }
    public uint ControllerEpoch { get; }
    public uint RequestSequence { get; }
    public uint SelectionCode { get; }
    public RecipeSelectionReference? SelectionRevision { get; }
    public RecipeContractReference SelectionPolicy { get; }
    public RecipeContractReference? SelectionMap { get; }
    public RecipeSelectionMapEntry? Target { get; }
    public string SystemPrincipalId => global::SharpInspect.Abstractions.SystemPrincipalId.PlcAdapter;
    public DateTimeOffset ObservedAtUtc { get; }
    public string RequestIdentityHash { get; }
    public Guid OperationId { get; }
    public string ContentHash { get; }

    internal PlcRecipeActivationRequestContext ActivationContext() => Target is null || SelectionMap is null
        ? throw new InvalidOperationException("RecipeChangeMappedTargetRequired")
        : new(RuntimeEpoch, EndpointContentHash, ProtocolProfile, ControllerEpoch, RequestSequence, SelectionCode,
            SelectionPolicy, SelectionMap, Target.Recipe, Target.ReleaseId, Target.ReleaseRecordContentHash);
}

/// <summary>An immutable handshake fact. It never supplies activation or confirmation authority.</summary>
public sealed class RecipeChangeHistoryEvent
{
    internal RecipeChangeHistoryEvent(long position, Guid eventId, RecipeChangeRequestEvidence request,
        RecipeChangeEventKind kind, RecipeChangeOutcome? outcome, RecipeChangeReason? reason, string reasonCode,
        RecipeActivationReference? activation, DateTimeOffset recordedAtUtc)
    {
        if (position < 1) throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
        EventId = RecipeActivationValidation.RequiredGuid(eventId, nameof(eventId));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Kind = AlgorithmConfigurationValidation.Enum(kind, nameof(kind));
        if (outcome is { } o) AlgorithmConfigurationValidation.Enum(o, nameof(outcome));
        if (reason is { } r) AlgorithmConfigurationValidation.Enum(r, nameof(reason));
        if (kind == RecipeChangeEventKind.RequestObserved &&
                (outcome is not null || reason is not null || activation is not null || request.RequestSequence == 0 || request.SelectionCode == 0) ||
            kind != RecipeChangeEventKind.RequestObserved && (outcome is null || reason is null) ||
            outcome == RecipeChangeOutcome.Succeeded && (reason != RecipeChangeReason.None || activation is null) ||
            outcome is not null and not RecipeChangeOutcome.Succeeded && reason == RecipeChangeReason.None)
            throw new ArgumentException("RecipeChangeOutcomeEvidenceInvalid");
        Outcome = outcome; Reason = reason;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        Activation = RecipeActivationValidation.Reference(activation);
        RecordedAtUtc = RecipeActivationValidation.Utc(recordedAtUtc, nameof(recordedAtUtc));
        if (RecordedAtUtc < request.ObservedAtUtc) throw new ArgumentException("RecipeChangeAuditTimeInvalid");
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-recipe-change-history-event-v1", Position.ToString(CultureInfo.InvariantCulture),
            EventId.ToString("D"), request.ContentHash, Kind.ToString(), Outcome?.ToString(), Reason?.ToString(), ReasonCode,
            Activation?.Position.ToString(CultureInfo.InvariantCulture), Activation?.ActivationId.ToString("D"),
            Activation?.ContentHash, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture)
        });
    }
    public long Position { get; }
    public Guid EventId { get; }
    public RecipeChangeRequestEvidence Request { get; }
    public RecipeChangeEventKind Kind { get; }
    public RecipeChangeOutcome? Outcome { get; }
    public RecipeChangeReason? Reason { get; }
    public string ReasonCode { get; }
    public RecipeActivationReference? Activation { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
}

public sealed record RecipeChangeHistoryFilter(string? RequestIdentityHash = null, long AfterPosition = 0,
    long? ThroughPosition = null, int PageSize = 20);
public sealed record RecipeChangeHistoryPage(bool Available, string ReasonCode,
    IReadOnlyList<RecipeChangeHistoryEvent> Events, long ThroughPosition, long? NextAfterPosition);
public interface IRecipeChangeHistoryQuery
{
    ValueTask<RecipeChangeHistoryPage> QueryAsync(RecipeChangeHistoryFilter filter, CancellationToken cancellationToken = default);
}
