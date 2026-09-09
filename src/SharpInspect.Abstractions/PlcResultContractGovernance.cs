using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Changes the deployment result contract as an immutable revision under fresh Step-Up.</summary>
public sealed record ChangePlcResultContractCommand : RuntimeCommand
{
    public ChangePlcResultContractCommand(Guid correlationId, CommandInvocation invocation,
        PlcResultContract proposal, RecipeContractReference? expectedCurrent, string changeReason)
        : base(correlationId, invocation)
    {
        if (correlationId == Guid.Empty) throw new ArgumentException("PlcResultContractCorrelationRequired");
        ArgumentNullException.ThrowIfNull(invocation);
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
        ExpectedCurrent = expectedCurrent;
        ChangeReason = AlgorithmContractValidation.BoundedText(changeReason, nameof(changeReason), 256);
        if (string.IsNullOrWhiteSpace(ChangeReason)) throw new ArgumentException("PlcResultContractChangeReasonRequired");
        AuthorizationTarget = ComputeAuthorizationTarget(proposal, expectedCurrent, ChangeReason);
    }
    internal static string ComputeAuthorizationTarget(PlcResultContract proposal,
        RecipeContractReference? expectedCurrent, string changeReason) => AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-change-plc-result-contract-v1", proposal.Id, proposal.Version, proposal.ContentHash,
            expectedCurrent?.Id, expectedCurrent?.Version, expectedCurrent?.ContentHash, changeReason });
    public PlcResultContract Proposal { get; }
    public RecipeContractReference? ExpectedCurrent { get; }
    public string ChangeReason { get; }
    public string AuthorizationTarget { get; }
}

/// <summary>Exact immutable released-recipe evidence validated against a deployment contract revision.</summary>
public sealed class PlcReleasedRecipeBinding
{
    internal PlcReleasedRecipeBinding(Guid releaseId, string releaseRecordContentHash, PlcResultContractBinding binding)
    {
        if (releaseId == Guid.Empty) throw new ArgumentException("PlcReleasedRecipeIdRequired");
        ReleaseId = releaseId;
        ReleaseRecordContentHash = AlgorithmConfigurationValidation.Hash(releaseRecordContentHash,
            nameof(releaseRecordContentHash)).ToUpperInvariant();
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-released-recipe-binding-v1", ReleaseId.ToString("D"), ReleaseRecordContentHash, binding.ContentHash });
    }
    public Guid ReleaseId { get; }
    public string ReleaseRecordContentHash { get; }
    public PlcResultContractBinding Binding { get; }
    public string ContentHash { get; }
}

/// <summary>Append-only governance evidence. Changing this contract never edits a Released Recipe.</summary>
public sealed class PlcResultContractRevision
{
    internal PlcResultContractRevision(long position, Guid revisionId, Guid operationId, PlcResultContract contract,
        RecipeContractReference? previousContract, long releaseHighWatermark,
        IEnumerable<PlcResultSchemaValidation> schemaValidations, IEnumerable<PlcReleasedRecipeBinding> bindings,
        Guid actorPrincipalId, Guid actorSessionId, long actorAuthorizationRevision, Guid stepUpGrantId,
        RecipeContractReference authorizationPolicy, string changeReason, string authorizationTarget, DateTimeOffset recordedAtUtc)
    {
        if (position < 1 || revisionId == Guid.Empty || operationId == Guid.Empty || releaseHighWatermark < 0 ||
            actorPrincipalId == Guid.Empty || actorSessionId == Guid.Empty || actorAuthorizationRevision < 0 || stepUpGrantId == Guid.Empty)
            throw new ArgumentException("PlcResultContractRevisionIdentityInvalid");
        Position = position; RevisionId = revisionId; OperationId = operationId;
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        PreviousContract = previousContract; ReleaseHighWatermark = releaseHighWatermark;
        SchemaValidations = AlgorithmContractValidation.Copy(schemaValidations.OrderBy(value => value.Schema.ContentHash, StringComparer.Ordinal),
            nameof(schemaValidations), 64);
        Bindings = AlgorithmContractValidation.Copy(bindings.OrderBy(value => value.ReleaseId), nameof(bindings), 10000);
        if (SchemaValidations.Count == 0 || SchemaValidations.Any(value => value.Contract.ContentHash != contract.ContentHash) ||
            Bindings.Any(value => value.Binding.Contract.ContentHash != contract.ContentHash) ||
            SchemaValidations.Select(value => value.Schema.ContentHash).Distinct(StringComparer.Ordinal).Count() != SchemaValidations.Count ||
            Bindings.Select(value => value.ReleaseId).Distinct().Count() != Bindings.Count)
            throw new ArgumentException("PlcResultContractRevisionEvidenceInvalid");
        var schemas = SchemaValidations.Select(value => new RecipeContractReference(value.Schema.Id, value.Schema.Version, value.Schema.ContentHash)).ToHashSet();
        if (schemas.Count != contract.SchemaMaps.Count || !schemas.SetEquals(contract.SchemaMaps.Select(value => value.ResultSchema)) ||
            Bindings.Count > releaseHighWatermark ||
            Bindings.Any(value => !SchemaValidations.Any(proof => proof.ContentHash == value.Binding.Validation.ContentHash)))
            throw new ArgumentException("PlcResultContractRevisionCoverageInvalid");
        ActorPrincipalId = actorPrincipalId; ActorSessionId = actorSessionId;
        ActorAuthorizationRevision = actorAuthorizationRevision; StepUpGrantId = stepUpGrantId;
        AuthorizationPolicy = authorizationPolicy ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        ChangeReason = AlgorithmContractValidation.BoundedText(changeReason, nameof(changeReason), 256);
        if (string.IsNullOrWhiteSpace(ChangeReason)) throw new ArgumentException("PlcResultContractChangeReasonRequired");
        AuthorizationTarget = AlgorithmConfigurationValidation.Hash(authorizationTarget, nameof(authorizationTarget)).ToUpperInvariant();
        if (AuthorizationTarget != ChangePlcResultContractCommand.ComputeAuthorizationTarget(contract, previousContract, ChangeReason))
            throw new ArgumentException("PlcResultContractRevisionAuthorizationTargetMismatch");
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        { "sharpinspect-plc-result-contract-revision-v1", Position.ToString(CultureInfo.InvariantCulture),
            RevisionId.ToString("D"), OperationId.ToString("D"), contract.Id, contract.Version, contract.ContentHash,
            previousContract?.Id, previousContract?.Version, previousContract?.ContentHash,
            ReleaseHighWatermark.ToString(CultureInfo.InvariantCulture), ActorPrincipalId.ToString("D"), ActorSessionId.ToString("D"),
            ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), StepUpGrantId.ToString("D"),
            authorizationPolicy.Id, authorizationPolicy.Version, authorizationPolicy.ContentHash,
            ChangeReason, AuthorizationTarget, RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            SchemaValidations.Count.ToString(CultureInfo.InvariantCulture) }
            .Concat(SchemaValidations.Select(value => value.ContentHash))
            .Concat(new[] { Bindings.Count.ToString(CultureInfo.InvariantCulture) })
            .Concat(Bindings.Select(value => value.ContentHash)));
    }
    public long Position { get; }
    public Guid RevisionId { get; }
    public Guid OperationId { get; }
    public PlcResultContract Contract { get; }
    public RecipeContractReference Reference => new(Contract.Id, Contract.Version, Contract.ContentHash);
    public RecipeContractReference? PreviousContract { get; }
    public long ReleaseHighWatermark { get; }
    public ReadOnlyCollection<PlcResultSchemaValidation> SchemaValidations { get; }
    public ReadOnlyCollection<PlcReleasedRecipeBinding> Bindings { get; }
    public Guid ActorPrincipalId { get; }
    public Guid ActorSessionId { get; }
    public long ActorAuthorizationRevision { get; }
    public Guid StepUpGrantId { get; }
    public RecipeContractReference AuthorizationPolicy { get; }
    public string ChangeReason { get; }
    public string AuthorizationTarget { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
}

public sealed record PlcResultContractAccess(bool CanChange, string ReasonCode)
{
    public bool RequiresStepUp => true;
}
public sealed record PlcResultContractChangeResult(RuntimeCommandOutcome Outcome, PlcResultContractRevision? Revision = null);
/// <summary>Available=true, Revision=null is a valid empty history with no current deployment contract.</summary>
public sealed record PlcResultContractReadResult(bool Available, string ReasonCode, PlcResultContractRevision? Revision = null);
public sealed record PlcResultContractFilter(long AfterPosition = 0, long? ThroughPosition = null, int PageSize = 20);
public sealed record PlcResultContractPage(bool Available, string ReasonCode, IReadOnlyList<PlcResultContractRevision> Revisions,
    long ThroughPosition, long? NextAfterPosition);

public interface IPlcResultContractQuery
{
    ValueTask<PlcResultContractReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default);
    ValueTask<PlcResultContractReadResult> ReadAsync(RecipeContractReference reference, CancellationToken cancellationToken = default);
    ValueTask<PlcResultContractPage> QueryAsync(PlcResultContractFilter filter, CancellationToken cancellationToken = default);
}
public interface IPlcResultContractService : IPlcResultContractQuery
{
    ValueTask<PlcResultContractAccess> GetAccessAsync(CommandInvocation invocation, CancellationToken cancellationToken = default);
    ValueTask<PlcResultContractChangeResult> ChangeAsync(ChangePlcResultContractCommand command, CancellationToken cancellationToken = default);
}
