using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Production;

internal enum ProductionArmMaintenanceState
{
    Unavailable = 1,
    InProgress = 2,
    ManualArmRequired = 3,
    ManualArmConfirmed = 4
}

/// <summary>
/// Observation from the governed maintenance journal reader, not host configuration.
/// No production reader exists yet; absence always means Unavailable. An internal
/// development fixture can exercise the consumer but cannot qualify a deployment.
/// </summary>
internal sealed class ProductionArmMaintenanceEvidence
{
    internal ProductionArmMaintenanceEvidence(string stationId, string deploymentHash,
        string journalHeadHash, ProductionArmMaintenanceState state)
    {
        StationId = AlgorithmConfigurationValidation.Identifier(stationId, nameof(stationId));
        DeploymentHash = RecipeActivationValidation.Hash(deploymentHash, nameof(deploymentHash));
        JournalHeadHash = RecipeActivationValidation.Hash(journalHeadHash, nameof(journalHeadHash));
        State = AlgorithmConfigurationValidation.Enum(state, nameof(state));
    }
    internal string StationId { get; }
    internal string DeploymentHash { get; }
    internal string JournalHeadHash { get; }
    internal ProductionArmMaintenanceState State { get; }
}

/// <summary>
/// Runtime-owned cached evidence. Current must return immediately without I/O.
/// Replacements synchronously raise Changed after releasing the provider lock;
/// Runtime reads Current while holding its own state lock.
/// </summary>
internal interface IProductionArmMaintenanceEvidenceProvider
{
    ProductionArmMaintenanceEvidence? Current { get; }
    event Action? Changed;
}
