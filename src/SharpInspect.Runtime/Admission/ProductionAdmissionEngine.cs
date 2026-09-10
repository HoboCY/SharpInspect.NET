using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Admission;

// Only internal composition can supply observed facts. A fact source has no Evaluate,
// Allow, SetReady or policy callback: every path uses the same fixed evaluator below.
internal interface IProductionAdmissionFactsSource
{
    ValueTask<ProductionAdmissionFacts> CaptureAsync(CancellationToken cancellationToken);
}

internal sealed class ProductionAdmissionFacts
{
    internal ProductionAdmissionFacts(ProductionConfiguration configuration,
        ProductionQualificationInputs qualifications, IReadOnlyList<ProductionAdmissionGateResult> runtimeGates,
        IReadOnlyDictionary<string, string> durableHeads)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Qualifications = qualifications ?? throw new ArgumentNullException(nameof(qualifications));
        if (runtimeGates is null || runtimeGates.Count > ProductionAdmissionReport.RequiredGates.Count ||
            durableHeads is null || durableHeads.Count > 64 ||
            runtimeGates.Any(gate => gate is null || ProductionAdmissionEngine.IsQualificationGate(gate.Gate)) ||
            runtimeGates.Select(gate => gate.Gate).Distinct().Count() != runtimeGates.Count)
            throw new ArgumentException("ProductionAdmissionFactsInvalid");
        RuntimeGates = new ReadOnlyDictionary<ProductionAdmissionGate, ProductionAdmissionGateResult>(
            runtimeGates.ToDictionary(gate => gate.Gate));
        var copiedHeads = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in durableHeads) copiedHeads.Add(ProductionAdmissionCanonical.RequireId(pair.Key),
            ProductionAdmissionCanonical.RequireHash(pair.Value));
        DurableHeads = new ReadOnlyDictionary<string, string>(copiedHeads);
        var fields = new List<string?> { Configuration.ObservedBindingsHash, qualifications.Authority.ContentHash,
            qualifications.PowerLossRequired ? "1" : "0", qualifications.PowerLossExclusionHash };
        foreach (var gate in RuntimeGates.Values.OrderBy(gate => gate.Gate)) fields.AddRange(ProductionAdmissionEngine.GateFields(gate));
        fields.Add("durable-heads");
        foreach (var pair in DurableHeads) fields.AddRange(new[] { pair.Key, pair.Value });
        fields.Add("qualification-heads");
        foreach (var pair in qualifications.CurrentRecordHeads.OrderBy(pair => pair.Key))
            fields.AddRange(new[] { pair.Key.ToString(), pair.Value });
        foreach (var record in qualifications.Records.Values.OrderBy(record => record.ContentHash, StringComparer.Ordinal))
            fields.AddRange(new[] { record.ContentHash, Convert.ToHexString(SHA256.HashData(record.Signature)) });
        foreach (var requirement in qualifications.Requirements.Values.OrderBy(requirement => requirement.Layer))
        {
            fields.AddRange(new[] { requirement.Layer.ToString(), requirement.ScopeHash, requirement.ContextHash });
            foreach (var pair in requirement.Checks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                fields.AddRange(new[] { pair.Key, pair.Value.Mandatory ? "1" : "0", pair.Value.Applicable ? "1" : "0",
                    pair.Value.ExclusionProofHash });
        }
        ObservationHash = ProductionAdmissionCanonical.Hash("production-admission-observed-facts-v1", fields.ToArray());
    }
    internal ProductionConfiguration Configuration { get; }
    internal ProductionQualificationInputs Qualifications { get; }
    internal IReadOnlyDictionary<ProductionAdmissionGate, ProductionAdmissionGateResult> RuntimeGates { get; }
    internal IReadOnlyDictionary<string, string> DurableHeads { get; }
    internal string ObservationHash { get; }

    internal string MaterialObservationHash(ProductionAdmissionGateResult? verifiedStoreGate)
    {
        if (verifiedStoreGate is null || !RuntimeGates.TryGetValue(ProductionAdmissionGate.StoreIntegrity, out var gate) ||
            !ProductionAdmissionEngine.IsTransientAuditRecheck(gate)) return ObservationHash;
        return new ProductionAdmissionFacts(Configuration, Qualifications, RuntimeGates.Values
            .Select(value => value.Gate == ProductionAdmissionGate.StoreIntegrity ? verifiedStoreGate : value).ToArray(),
            DurableHeads).ObservationHash;
    }
}

internal static class ProductionAdmissionEngine
{
    internal static ProductionAdmissionReport Evaluate(Guid runtimeEpoch, long snapshotRevision,
        long admissionGeneration, DateTimeOffset observedAtUtc, ProductionAdmissionFacts facts)
    {
        var qualification = ProductionQualificationMatcher.Evaluate(facts.Configuration, facts.Qualifications, observedAtUtc);
        var gates = new List<ProductionAdmissionGateResult>(ProductionAdmissionReport.RequiredGates.Count);
        foreach (var gate in ProductionAdmissionReport.RequiredGates)
        {
            if (IsQualificationGate(gate))
            {
                var layers = gate switch
                {
                    ProductionAdmissionGate.FrameworkQualification => new[] { ProductionQualificationLayer.Framework },
                    ProductionAdmissionGate.ProviderQualification => new[] { ProductionQualificationLayer.Provider, ProductionQualificationLayer.ProviderHardware },
                    ProductionAdmissionGate.PerformanceQualification => new[] { ProductionQualificationLayer.Performance },
                    ProductionAdmissionGate.StationAcceptance => new[] { ProductionQualificationLayer.StationAcceptance },
                    _ => new[] { ProductionQualificationLayer.PowerLoss }
                };
                var rows = qualification.Where(row => layers.Contains(row.Layer)).ToArray();
                var selected = rows.FirstOrDefault(row => row.Status is not (QualificationEvidenceStatus.Passed or QualificationEvidenceStatus.NotApplicable)) ?? rows[0];
                var status = selected.Status switch
                {
                    QualificationEvidenceStatus.Passed => ProductionAdmissionGateStatus.Passed,
                    QualificationEvidenceStatus.Missing => ProductionAdmissionGateStatus.Missing,
                    QualificationEvidenceStatus.NotConfigured => ProductionAdmissionGateStatus.NotConfigured,
                    QualificationEvidenceStatus.Mismatch => ProductionAdmissionGateStatus.Mismatch,
                    QualificationEvidenceStatus.Expired => ProductionAdmissionGateStatus.Expired,
                    QualificationEvidenceStatus.NotApplicable => ProductionAdmissionGateStatus.NotApplicable,
                    _ => ProductionAdmissionGateStatus.Failed
                };
                // Provider health requires both contract and exact hardware qualification.
                // Preserve the real record references rather than inventing a composite record.
                var additionalHashes = rows.Select(row => row.EvidenceRecordHash).Where(hash => hash is not null &&
                    hash != selected.EvidenceRecordHash).Select(hash => hash!).ToArray();
                gates.Add(new(gate, status, selected.ReasonCode, selected.ExpectedFingerprint, selected.ObservedFingerprint,
                    selected.EvidenceRecordHash, additionalHashes));
            }
            else if (facts.RuntimeGates.TryGetValue(gate, out var observation)) gates.Add(observation);
            else gates.Add(new(gate, ProductionAdmissionGateStatus.NotConfigured, MissingReason(gate)));
        }
        return new(runtimeEpoch, snapshotRevision, admissionGeneration, observedAtUtc,
            facts.Configuration.PerformanceFingerprint, facts.Configuration.StationAcceptanceFingerprint,
            facts.Configuration.FrameworkFingerprint, facts.Configuration.ProviderFingerprint,
            facts.Configuration.ProfileHash, gates);
    }

    // Unlike report.ContentHash, this excludes observation time/epoch/revision. Runtime
    // compares it together with facts.ObservationHash to detect only admission changes,
    // including a record becoming expired while its immutable bytes remain unchanged.
    internal static string EvidenceHash(ProductionAdmissionReport report) => MaterialEvidenceHash(report, null);

    internal static bool IsTransientAuditRecheck(ProductionAdmissionGateResult gate) =>
        gate.Gate == ProductionAdmissionGate.StoreIntegrity && gate.Status == ProductionAdmissionGateStatus.Blocked &&
        gate.ReasonCode == "TraceAuditVerificationPending";

    internal static string MaterialEvidenceHash(ProductionAdmissionReport report,
        ProductionAdmissionGateResult? verifiedStoreGate) => ProductionAdmissionCanonical.Hash(
        "production-admission-effective-evidence-v1", new[] { report.PerformanceConfigurationFingerprint,
            report.StationAcceptanceFingerprint, report.FrameworkQualificationFingerprint,
            report.ProviderQualificationFingerprint, report.ProfileHash }
        .Concat(report.Gates.Select(gate => verifiedStoreGate is not null && IsTransientAuditRecheck(gate)
            ? verifiedStoreGate : gate).SelectMany(GateFields)).ToArray());

    internal static bool IsQualificationGate(ProductionAdmissionGate gate) => gate is
        ProductionAdmissionGate.FrameworkQualification or ProductionAdmissionGate.ProviderQualification or
        ProductionAdmissionGate.PerformanceQualification or ProductionAdmissionGate.StationAcceptance or
        ProductionAdmissionGate.PowerLossQualification;

    internal static string?[] GateFields(ProductionAdmissionGateResult gate) => new[]
    {
        ((int)gate.Gate).ToString(CultureInfo.InvariantCulture), ((int)gate.Status).ToString(CultureInfo.InvariantCulture),
        gate.ReasonCode, gate.ExpectedFingerprint, gate.ObservedFingerprint,
        gate.EvidenceRecordHashes.Count.ToString(CultureInfo.InvariantCulture)
    }.Concat(gate.EvidenceRecordHashes).ToArray();

    internal static string MissingReason(ProductionAdmissionGate gate) => gate switch
    {
        ProductionAdmissionGate.DeploymentPolicies => "DeploymentPoliciesMissing",
        ProductionAdmissionGate.VersionPolicy => "ProductionVersionPolicyMissing",
        ProductionAdmissionGate.ActiveRecipe => "ActiveRecipeMissing",
        ProductionAdmissionGate.PreparedAlgorithm => "ActiveRecipePreparationRequired",
        ProductionAdmissionGate.RecipeAssets => "ProductionAssetVerificationUnavailable",
        ProductionAdmissionGate.CameraBinding => "CameraBindingMissing",
        ProductionAdmissionGate.CameraConfiguration => "CameraConfigurationNotVerified",
        ProductionAdmissionGate.CameraHealth => "CameraHealthUnavailable",
        ProductionAdmissionGate.PlcCommunication => "PlcCommunicationHealthUnavailable",
        ProductionAdmissionGate.ControllerSynchronization => "ControllerSynchronizationUnavailable",
        ProductionAdmissionGate.Recovery => "StartupRecoveryNotVerified",
        ProductionAdmissionGate.ExclusiveWork => "ExclusiveWorkStateUnavailable",
        ProductionAdmissionGate.Alarms => "AlarmAuthorityUnavailable",
        ProductionAdmissionGate.StoreIntegrity => "TraceStoreUnavailable",
        ProductionAdmissionGate.StoreCapacity => "ProductionStoreCapacityUnavailable",
        ProductionAdmissionGate.EvidenceReconciliation => "EvidenceReconciliationUnavailable",
        ProductionAdmissionGate.Backlog => "ProductionBacklogStateUnavailable",
        ProductionAdmissionGate.IdentityRecovery => "AdministratorRecoveryMethodUnavailable",
        ProductionAdmissionGate.ProductionCycle => "ProductionCycleUnavailable",
        _ => "ProductionAdmissionGateUnavailable"
    };
}
