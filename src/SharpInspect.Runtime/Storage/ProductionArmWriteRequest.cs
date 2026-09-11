using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The exact evidence one append of the schema-32 production-arm ledger carries.
/// Attempted binds the immutable attempt context, policies and source; it never
/// carries authorization. The human fields exist only for the manual maintenance
/// cause, and a caller cannot inject a fabricated principal or session for a
/// Runtime-caused attempt.
/// </summary>
internal sealed record ProductionArmWriteRequest(Guid AttemptId, Guid RuntimeEpoch, ProductionArmCause Cause,
    ProductionArmEventKind Kind, string StationId, RecipeContractReference StartupPolicy,
    RecipeContractReference PostActivationPolicy, string DeploymentHash, RecipeChangeRequestEvidence? PlcRequest,
    RecipeActivationReference? Activation, string? MaintenanceHeadHash, long AdmissionGeneration,
    ProductionAdmissionReport? Report, IReadOnlyDictionary<string, string> ExpectedDurableHeads,
    IReadOnlyDictionary<string, string> CurrentDurableHeads, ProductionArmReason Reason, string ReasonCode,
    Guid? HumanCommandId = null, Guid? HumanPrincipalId = null, Guid? HumanSessionId = null,
    ProductionArmReadyReceipt? ReadyReceipt = null, ProductionArmInputStabilityEvidence? InputStability = null);

internal sealed record ProductionArmWriteResult(bool Committed, string ReasonCode,
    ProductionArmHistoryEvent? Event = null);

/// <summary>One queued append. The writer owns the definitive result and never
/// converts a committed transaction into a caller-visible failure.</summary>
internal sealed class ProductionArmWork
{
    internal ProductionArmWork(ProductionArmWriteRequest request) => Request = request;

    internal ProductionArmWriteRequest Request { get; }
    internal TaskCompletionSource<ProductionArmWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
