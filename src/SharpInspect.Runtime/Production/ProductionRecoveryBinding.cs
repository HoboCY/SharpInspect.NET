using System.Globalization;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Production;

/// <summary>
/// Explicit deployment binding for the separately governed production recovery
/// safety source.  No timeout or provider is supplied by this type implicitly.
/// </summary>
public sealed class ProductionRecoveryBinding
{
    public ProductionRecoveryBinding(
        string stationId,
        string endpointBindingHash,
        ProductionRecoverySafetyProviderBinding safetySource,
        TimeSpan operationTimeout,
        TimeSpan retirementTimeout)
    {
        StationId = AlgorithmContractValidation.Identifier(stationId, nameof(stationId));
        EndpointBindingHash = RecipeActivationValidation.Hash(endpointBindingHash,
            nameof(endpointBindingHash));
        SafetySource = safetySource ?? throw new ArgumentNullException(nameof(safetySource));
        if (!string.Equals(SafetySource.StationId, StationId, StringComparison.Ordinal))
            throw new ArgumentException("ProductionRecoverySafetyStationMismatch", nameof(safetySource));
        if (!string.Equals(SafetySource.EndpointBindingHash, EndpointBindingHash,
                StringComparison.Ordinal))
            throw new ArgumentException("ProductionRecoverySafetyEndpointBindingMismatch",
                nameof(safetySource));

        OperationTimeout = ValidateBudget(operationTimeout, nameof(operationTimeout));
        RetirementTimeout = ValidateBudget(retirementTimeout, nameof(retirementTimeout));
        ContentHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-recovery-binding-v1", StationId,
            EndpointBindingHash, SafetySource.ContentHash,
            OperationTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
            RetirementTimeout.Ticks.ToString(CultureInfo.InvariantCulture)
        });
    }

    public string StationId { get; }
    public string EndpointBindingHash { get; }
    public ProductionRecoverySafetyProviderBinding SafetySource { get; }
    public TimeSpan OperationTimeout { get; }
    public TimeSpan RetirementTimeout { get; }
    public string ContentHash { get; }

    internal static TimeSpan ValidateBudget(TimeSpan value, string parameterName)
    {
        // The upper bound is only the CancellationTokenSource representable range;
        // deployments must still provide both values explicitly.
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(parameterName,
                "ProductionRecoverySafetyBudgetInvalid");
        return value;
    }
}
