namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Explicit opt-in for audited manual production recovery in schema 30.
/// Requires production inspections, local identity and central audit integrity.
/// Existing databases require a separately governed migration; startup never upgrades them.
/// </summary>
public sealed class ProductionRecoveryStoreOptions
{
    internal const int SchemaVersion = 30;
}
