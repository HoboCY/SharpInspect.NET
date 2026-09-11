using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// Identifies which requester produced one activation request. The value is
/// evidence authority vocabulary only; it never widens who may request an
/// activation.
/// </summary>
public enum RecipeActivationActorKind : byte
{
    /// <summary>An authenticated interactive Human Principal.</summary>
    Human = 1,
    /// <summary>The fixed PLC Adapter System Principal acting for the Line Controller.</summary>
    PlcAdapter = 2
}

/// <summary>
/// The immutable requester of one activation request. A human actor carries the
/// exact interactive session and authorization revision; the PLC adapter actor
/// carries its fixed system identity and the complete PLC request context. There
/// is deliberately no public constructor and no way to add a human session to a
/// PLC actor or a fabricated human identity to a PLC request.
/// </summary>
public sealed class RecipeActivationActor
{
    private const string PlcAdapterSystemPrincipalId = SharpInspect.Abstractions.SystemPrincipalId.PlcAdapter;

    private RecipeActivationActor(RecipeActivationActorKind kind, Guid? humanPrincipalId, Guid? humanSessionId,
        long? humanAuthorizationRevision, string? systemPrincipalId,
        PlcRecipeActivationRequestContext? plcRequestContext)
    {
        Kind = kind;
        HumanPrincipalId = humanPrincipalId;
        HumanSessionId = humanSessionId;
        HumanAuthorizationRevision = humanAuthorizationRevision;
        SystemPrincipalId = systemPrincipalId;
        PlcRequestContext = plcRequestContext;
        ContentHash = kind == RecipeActivationActorKind.Human
            ? AlgorithmContractValidation.HashParts(new[]
            {
                "sharpinspect-recipe-activation-actor-human-v1", humanPrincipalId!.Value.ToString("D"),
                humanSessionId!.Value.ToString("D"),
                humanAuthorizationRevision!.Value.ToString(CultureInfo.InvariantCulture)
            })
            : AlgorithmContractValidation.HashParts(new[]
            {
                "sharpinspect-recipe-activation-actor-plc-adapter-v1", PlcAdapterSystemPrincipalId,
                plcRequestContext!.ContentHash
            });
    }

    internal static RecipeActivationActor Human(Guid principalId, Guid sessionId, long authorizationRevision)
    {
        if (principalId == Guid.Empty)
            throw new ArgumentException("RecipeActivationActorInvalid", nameof(principalId));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("RecipeActivationSessionInvalid", nameof(sessionId));
        if (authorizationRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(authorizationRevision));
        return new(RecipeActivationActorKind.Human, principalId, sessionId, authorizationRevision, null, null);
    }

    internal static RecipeActivationActor PlcAdapter(PlcRecipeActivationRequestContext requestContext) =>
        new(RecipeActivationActorKind.PlcAdapter, null, null, null, PlcAdapterSystemPrincipalId,
            requestContext ?? throw new ArgumentNullException(nameof(requestContext)));

    public RecipeActivationActorKind Kind { get; }
    public Guid? HumanPrincipalId { get; }
    public Guid? HumanSessionId { get; }
    public long? HumanAuthorizationRevision { get; }
    public string? SystemPrincipalId { get; }
    public PlcRecipeActivationRequestContext? PlcRequestContext { get; }
    public string ContentHash { get; }
    public bool IsHuman => Kind == RecipeActivationActorKind.Human;
    public bool IsPlcAdapter => Kind == RecipeActivationActorKind.PlcAdapter;

    /// <summary>Exact identity comparison; two actors are interchangeable only for identical evidence.</summary>
    internal bool Matches(RecipeActivationActor? other) => other is not null &&
        Kind == other.Kind && HumanPrincipalId == other.HumanPrincipalId &&
        HumanSessionId == other.HumanSessionId &&
        HumanAuthorizationRevision == other.HumanAuthorizationRevision &&
        string.Equals(SystemPrincipalId, other.SystemPrincipalId, StringComparison.Ordinal) &&
        (PlcRequestContext is null ? other.PlcRequestContext is null : PlcRequestContext.Matches(other.PlcRequestContext));
}

/// <summary>
/// The complete immutable context of one PLC-requested activation: the dedicated
/// Recipe Change Handshake request identity, the exact deployment policy and map
/// versions that resolved the request, and the exact Released Recipe the map
/// selected. It never carries calibration choices, a historical selection, or a
/// human identity, and it cannot be re-pointed at another candidate.
/// </summary>
public sealed class PlcRecipeActivationRequestContext
{
    internal PlcRecipeActivationRequestContext(Guid runtimeEpoch, string endpointContentHash,
        RecipeContractReference protocolProfile, uint controllerEpoch, uint requestSequence, uint selectionCode,
        RecipeContractReference selectionPolicy, RecipeContractReference selectionMap, RecipeReference candidate,
        Guid releaseId, string releaseRecordContentHash)
    {
        RuntimeEpoch = RecipeActivationValidation.RequiredGuid(runtimeEpoch, nameof(runtimeEpoch));
        EndpointContentHash = RecipeActivationValidation.Hash(endpointContentHash, nameof(endpointContentHash));
        ProtocolProfile = protocolProfile ?? throw new ArgumentNullException(nameof(protocolProfile));
        // A controller epoch of zero is a real observed value; a protocol policy that
        // forbids it rejects the request separately and never rewrites it here.
        ControllerEpoch = controllerEpoch;
        if (requestSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(requestSequence));
        RequestSequence = requestSequence;
        if (selectionCode == 0)
            throw new ArgumentOutOfRangeException(nameof(selectionCode));
        SelectionCode = selectionCode;
        SelectionPolicy = selectionPolicy ?? throw new ArgumentNullException(nameof(selectionPolicy));
        SelectionMap = selectionMap ?? throw new ArgumentNullException(nameof(selectionMap));
        Candidate = RecipeActivationValidation.Recipe(candidate, nameof(candidate));
        ReleaseId = RecipeActivationValidation.RequiredGuid(releaseId, nameof(releaseId));
        ReleaseRecordContentHash = RecipeActivationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-recipe-activation-request-context-v1", RuntimeEpoch.ToString("D"),
            EndpointContentHash, ProtocolProfile.Id, ProtocolProfile.Version, ProtocolProfile.ContentHash,
            ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            RequestSequence.ToString(CultureInfo.InvariantCulture),
            SelectionCode.ToString(CultureInfo.InvariantCulture),
            SelectionPolicy.Id, SelectionPolicy.Version, SelectionPolicy.ContentHash,
            SelectionMap.Id, SelectionMap.Version, SelectionMap.ContentHash,
            Candidate.Id, Candidate.Version, Candidate.ContentHash, ReleaseId.ToString("D"),
            ReleaseRecordContentHash
        });
        // Request identity deliberately excludes the runtime epoch, the selection code
        // and the selection policy/map versions so a restart or a later policy revision
        // cannot erase the identity of an already observed PLC request.
        RequestIdentityHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-plc-recipe-activation-request-identity-v1", EndpointContentHash,
            ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            RequestSequence.ToString(CultureInfo.InvariantCulture)
        });
    }

    public Guid RuntimeEpoch { get; }
    public string EndpointContentHash { get; }
    public RecipeContractReference ProtocolProfile { get; }
    public uint ControllerEpoch { get; }
    public uint RequestSequence { get; }
    public uint SelectionCode { get; }
    public RecipeContractReference SelectionPolicy { get; }
    public RecipeContractReference SelectionMap { get; }
    public RecipeReference Candidate { get; }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public string ContentHash { get; }
    public string RequestIdentityHash { get; }

    /// <summary>Exact value comparison; the content hash alone is already domain separated.</summary>
    internal bool Matches(PlcRecipeActivationRequestContext? other) => other is not null &&
        RuntimeEpoch == other.RuntimeEpoch &&
        string.Equals(EndpointContentHash, other.EndpointContentHash, StringComparison.Ordinal) &&
        ProtocolProfile == other.ProtocolProfile && ControllerEpoch == other.ControllerEpoch &&
        RequestSequence == other.RequestSequence && SelectionCode == other.SelectionCode &&
        SelectionPolicy == other.SelectionPolicy && SelectionMap == other.SelectionMap &&
        Candidate == other.Candidate && ReleaseId == other.ReleaseId &&
        string.Equals(ReleaseRecordContentHash, other.ReleaseRecordContentHash, StringComparison.Ordinal);
}
