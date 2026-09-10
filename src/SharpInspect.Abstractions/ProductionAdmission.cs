using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>
/// The fixed production admission gates.  The order is part of the public
/// projection and is also the order used by the canonical report hash.
/// </summary>
public enum ProductionAdmissionGate : byte
{
    DeploymentPolicies = 1,
    VersionPolicy = 2,
    ActiveRecipe = 3,
    PreparedAlgorithm = 4,
    RecipeAssets = 5,
    CameraBinding = 6,
    CameraConfiguration = 7,
    CameraHealth = 8,
    PlcCommunication = 9,
    ControllerSynchronization = 10,
    Recovery = 11,
    ExclusiveWork = 12,
    Alarms = 13,
    StoreIntegrity = 14,
    StoreCapacity = 15,
    EvidenceReconciliation = 16,
    Backlog = 17,
    IdentityRecovery = 18,
    FrameworkQualification = 19,
    ProviderQualification = 20,
    PerformanceQualification = 21,
    StationAcceptance = 22,
    PowerLossQualification = 23,
    ProductionCycle = 24
}

/// <summary>The closed status set for one production admission gate.</summary>
public enum ProductionAdmissionGateStatus : byte
{
    Passed = 1,
    Missing = 2,
    NotConfigured = 3,
    Mismatch = 4,
    Expired = 5,
    Failed = 6,
    Blocked = 7,
    NotApplicable = 8
}

/// <summary>
/// One immutable, attributable admission observation.  It is a report row;
/// it does not grant a permission or change Runtime state.
/// </summary>
public sealed class ProductionAdmissionGateResult
{
    internal ProductionAdmissionGateResult(ProductionAdmissionGate gate,
        ProductionAdmissionGateStatus status, string reasonCode,
        string? expectedFingerprint = null, string? observedFingerprint = null,
        string? evidenceRecordHash = null,
        IEnumerable<string>? additionalEvidenceRecordHashes = null)
    {
        if (!Enum.IsDefined(gate))
            throw new ArgumentOutOfRangeException(nameof(gate), "ProductionAdmissionGateInvalid");
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), "ProductionAdmissionGateStatusInvalid");

        Gate = gate;
        Status = status;
        ReasonCode = AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode));
        ExpectedFingerprint = Fingerprint(expectedFingerprint, nameof(expectedFingerprint));
        ObservedFingerprint = Fingerprint(observedFingerprint, nameof(observedFingerprint));
        EvidenceRecordHash = Fingerprint(evidenceRecordHash, nameof(evidenceRecordHash));
        var evidenceHashes = new List<string>(6);
        if (EvidenceRecordHash is not null) evidenceHashes.Add(EvidenceRecordHash);
        foreach (var additional in AlgorithmContractValidation.Copy(
                     additionalEvidenceRecordHashes, nameof(additionalEvidenceRecordHashes), 6))
        {
            var normalized = Fingerprint(additional, nameof(additionalEvidenceRecordHashes));
            if (normalized is null)
                throw new ArgumentException("ProductionAdmissionEvidenceRecordHashInvalid",
                    nameof(additionalEvidenceRecordHashes));
            if (!evidenceHashes.Contains(normalized, StringComparer.Ordinal))
                evidenceHashes.Add(normalized);
            if (evidenceHashes.Count > 6)
                throw new ArgumentException("ProductionAdmissionEvidenceRecordHashLimitExceeded",
                    nameof(additionalEvidenceRecordHashes));
        }
        EvidenceRecordHashes = new ReadOnlyCollection<string>(evidenceHashes.ToArray());
        // The convenience primary reference is a projection of the canonical list,
        // including rows whose only available evidence arrived as an additional item.
        EvidenceRecordHash = evidenceHashes.FirstOrDefault();
        if (status == ProductionAdmissionGateStatus.NotApplicable &&
            gate != ProductionAdmissionGate.PowerLossQualification)
            throw new ArgumentException("ProductionAdmissionNotApplicableGateInvalid", nameof(gate));
    }

    public ProductionAdmissionGate Gate { get; }
    public ProductionAdmissionGateStatus Status { get; }
    public string ReasonCode { get; }
    public string? ExpectedFingerprint { get; }
    public string? ObservedFingerprint { get; }
    public string? EvidenceRecordHash { get; }
    public ReadOnlyCollection<string> EvidenceRecordHashes { get; }

    private static string? Fingerprint(string? value, string parameterName) => value is null
        ? null
        : AlgorithmConfigurationValidation.Hash(value, parameterName).ToUpperInvariant();
}

/// <summary>
/// Complete read-only production admission projection.  Runtime constructs it
/// from its current authoritative facts; consumers cannot supply a CanArm flag.
/// </summary>
public sealed class ProductionAdmissionReport
{
    private static readonly ProductionAdmissionGate[] RequiredGateValues =
    {
        ProductionAdmissionGate.DeploymentPolicies,
        ProductionAdmissionGate.VersionPolicy,
        ProductionAdmissionGate.ActiveRecipe,
        ProductionAdmissionGate.PreparedAlgorithm,
        ProductionAdmissionGate.RecipeAssets,
        ProductionAdmissionGate.CameraBinding,
        ProductionAdmissionGate.CameraConfiguration,
        ProductionAdmissionGate.CameraHealth,
        ProductionAdmissionGate.PlcCommunication,
        ProductionAdmissionGate.ControllerSynchronization,
        ProductionAdmissionGate.Recovery,
        ProductionAdmissionGate.ExclusiveWork,
        ProductionAdmissionGate.Alarms,
        ProductionAdmissionGate.StoreIntegrity,
        ProductionAdmissionGate.StoreCapacity,
        ProductionAdmissionGate.EvidenceReconciliation,
        ProductionAdmissionGate.Backlog,
        ProductionAdmissionGate.IdentityRecovery,
        ProductionAdmissionGate.FrameworkQualification,
        ProductionAdmissionGate.ProviderQualification,
        ProductionAdmissionGate.PerformanceQualification,
        ProductionAdmissionGate.StationAcceptance,
        ProductionAdmissionGate.PowerLossQualification,
        ProductionAdmissionGate.ProductionCycle
    };

    private static readonly ReadOnlyCollection<ProductionAdmissionGate> RequiredGateCollection =
        Array.AsReadOnly(RequiredGateValues);

    internal ProductionAdmissionReport(Guid runtimeEpoch, long snapshotRevision,
        long admissionGeneration, DateTimeOffset observedAtUtc,
        string? performanceConfigurationFingerprint,
        string? stationAcceptanceFingerprint,
        string? frameworkQualificationFingerprint,
        string? providerQualificationFingerprint,
        string? profileHash,
        IEnumerable<ProductionAdmissionGateResult> gates)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("ProductionAdmissionRuntimeEpochInvalid", nameof(runtimeEpoch));
        if (snapshotRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshotRevision));
        if (admissionGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(admissionGeneration));
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("ProductionAdmissionObservedAtInvalid", nameof(observedAtUtc));

        var copied = AlgorithmContractValidation.Copy(gates, nameof(gates), RequiredGateValues.Length).ToArray();
        if (copied.Length != RequiredGateValues.Length)
            throw new ArgumentException("ProductionAdmissionGateSetIncomplete", nameof(gates));

        var byGate = copied.ToDictionary(value => value.Gate);
        if (byGate.Count != RequiredGateValues.Length ||
            RequiredGateValues.Any(gate => !byGate.ContainsKey(gate)))
            throw new ArgumentException("ProductionAdmissionGateSetInvalid", nameof(gates));

        RuntimeEpoch = runtimeEpoch;
        SnapshotRevision = snapshotRevision;
        AdmissionGeneration = admissionGeneration;
        ObservedAtUtc = observedAtUtc;
        PerformanceConfigurationFingerprint = Fingerprint(performanceConfigurationFingerprint,
            nameof(performanceConfigurationFingerprint));
        StationAcceptanceFingerprint = Fingerprint(stationAcceptanceFingerprint,
            nameof(stationAcceptanceFingerprint));
        FrameworkQualificationFingerprint = Fingerprint(frameworkQualificationFingerprint,
            nameof(frameworkQualificationFingerprint));
        ProviderQualificationFingerprint = Fingerprint(providerQualificationFingerprint,
            nameof(providerQualificationFingerprint));
        ProfileHash = Fingerprint(profileHash, nameof(profileHash));
        Gates = new ReadOnlyCollection<ProductionAdmissionGateResult>(
            RequiredGateValues.Select(gate => byGate[gate]).ToArray());
        ContentHash = ComputeContentHash();
    }

    /// <summary>The immutable fixed gate set expected in every report.</summary>
    public static IReadOnlyList<ProductionAdmissionGate> RequiredGates => RequiredGateCollection;

    public Guid RuntimeEpoch { get; }
    public long SnapshotRevision { get; }
    public long AdmissionGeneration { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string? PerformanceConfigurationFingerprint { get; }
    public string? StationAcceptanceFingerprint { get; }
    public string? FrameworkQualificationFingerprint { get; }
    public string? ProviderQualificationFingerprint { get; }
    public string? ProfileHash { get; }
    public ReadOnlyCollection<ProductionAdmissionGateResult> Gates { get; }
    public string ContentHash { get; }

    /// <summary>
    /// Derived solely from the complete fixed gate set.  A Power-Loss gate may
    /// be explicitly NotApplicable; every other gate must be Passed.
    /// </summary>
    public bool CanArm => Gates.All(value => value.Status == ProductionAdmissionGateStatus.Passed ||
        (value.Gate == ProductionAdmissionGate.PowerLossQualification &&
            value.Status == ProductionAdmissionGateStatus.NotApplicable));

    private string ComputeContentHash()
    {
        var fields = new List<string?>(16 + Gates.Count * 6)
        {
            "sharpinspect-production-admission-v1",
            RuntimeEpoch.ToString("D"),
            SnapshotRevision.ToString(CultureInfo.InvariantCulture),
            AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
            ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            PerformanceConfigurationFingerprint,
            StationAcceptanceFingerprint,
            FrameworkQualificationFingerprint,
            ProviderQualificationFingerprint,
            ProfileHash
        };
        foreach (var gate in Gates)
        {
            fields.Add(gate.Gate.ToString());
            fields.Add(gate.Status.ToString());
            fields.Add(gate.ReasonCode);
            fields.Add(gate.ExpectedFingerprint);
            fields.Add(gate.ObservedFingerprint);
            fields.Add(gate.EvidenceRecordHashes.Count.ToString(CultureInfo.InvariantCulture));
            fields.AddRange(gate.EvidenceRecordHashes);
        }

        return AlgorithmContractValidation.HashParts(fields);
    }

    private static string? Fingerprint(string? value, string parameterName) => value is null
        ? null
        : AlgorithmConfigurationValidation.Hash(value, parameterName).ToUpperInvariant();
}
