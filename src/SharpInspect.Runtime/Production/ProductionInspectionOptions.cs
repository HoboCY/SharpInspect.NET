using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Plc;

namespace SharpInspect.Runtime.Production;

/// <summary>Production entry bindings and bounded resource operations. Qualification remains a separate prerequisite.</summary>
public sealed class ProductionInspectionOptions
{
    public ProductionInspectionOptions(string stationId, ProductionEvidenceRequirement evidenceRequirement,
        ModbusProductionProfile profile,
        long tracePolicyVersion, string tracePolicySnapshotHash, TimeSpan operationTimeout,
        TimeSpan retirementTimeout, ProductionDeploymentManifest? deployment = null)
    {
        StationId = AlgorithmContractValidation.Identifier(stationId, nameof(stationId));
        if (!Enum.IsDefined(evidenceRequirement)) throw new ArgumentOutOfRangeException(nameof(evidenceRequirement));
        EvidenceRequirement = evidenceRequirement;
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (tracePolicyVersion < 1) throw new ArgumentOutOfRangeException(nameof(tracePolicyVersion));
        TracePolicyVersion = tracePolicyVersion;
        TracePolicySnapshotHash = AlgorithmConfigurationValidation.Hash(tracePolicySnapshotHash,
            nameof(tracePolicySnapshotHash)).ToUpperInvariant();
        OperationTimeout = Duration(operationTimeout, nameof(operationTimeout));
        RetirementTimeout = Duration(retirementTimeout, nameof(retirementTimeout));
        Deployment = deployment;
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-production-inspection-options-v1", StationId, EvidenceRequirement.ToString(), profile.ContentHash,
            tracePolicyVersion.ToString(CultureInfo.InvariantCulture), TracePolicySnapshotHash,
            OperationTimeout.ToString("c", CultureInfo.InvariantCulture),
            RetirementTimeout.ToString("c", CultureInfo.InvariantCulture), deployment?.ContentHash
        });
    }

    public string StationId { get; }
    public ProductionEvidenceRequirement EvidenceRequirement { get; }
    public ModbusProductionProfile Profile { get; }
    public long TracePolicyVersion { get; }
    public string TracePolicySnapshotHash { get; }
    public TimeSpan OperationTimeout { get; }
    public TimeSpan RetirementTimeout { get; }
    public string ContentHash { get; }
    public ProductionDeploymentManifest? Deployment { get; }

    private static TimeSpan Duration(TimeSpan value, string name) =>
        value >= TimeSpan.FromMilliseconds(1) && value <= TimeSpan.FromMinutes(5)
            ? value : throw new ArgumentOutOfRangeException(name);
}
