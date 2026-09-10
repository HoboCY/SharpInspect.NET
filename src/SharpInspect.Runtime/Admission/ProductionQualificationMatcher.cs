using System.Collections.ObjectModel;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Admission;

internal sealed record ProductionQualificationExpectedCheck(bool Mandatory, bool Applicable, string? ExclusionProofHash = null);

internal sealed class ProductionQualificationRequirement
{
    internal ProductionQualificationRequirement(ProductionQualificationLayer layer, string scopeHash, string contextHash,
        IReadOnlyDictionary<string, ProductionQualificationExpectedCheck> checks)
    {
        if (!Enum.IsDefined(layer) || checks is null || checks.Count is < 1 or > 256)
            throw new ArgumentException("ProductionQualificationRequirementInvalid");
        Layer = layer;
        ScopeHash = ProductionAdmissionCanonical.RequireHash(scopeHash);
        ContextHash = ProductionAdmissionCanonical.RequireHash(contextHash);
        var copied = new SortedDictionary<string, ProductionQualificationExpectedCheck>(StringComparer.Ordinal);
        foreach (var pair in checks)
        {
            var check = pair.Value ?? throw new ArgumentException("ProductionQualificationRequirementInvalid");
            if (check.Mandatory && !check.Applicable || !check.Applicable && check.ExclusionProofHash is null ||
                check.Applicable && check.ExclusionProofHash is not null)
                throw new ArgumentException("ProductionQualificationApplicabilityInvalid");
            if (check.ExclusionProofHash is not null) ProductionAdmissionCanonical.RequireHash(check.ExclusionProofHash);
            copied.Add(ProductionAdmissionCanonical.RequireId(pair.Key), check);
        }
        Checks = new ReadOnlyDictionary<string, ProductionQualificationExpectedCheck>(copied);
    }
    internal ProductionQualificationLayer Layer { get; }
    internal string ScopeHash { get; }
    internal string ContextHash { get; }
    internal IReadOnlyDictionary<string, ProductionQualificationExpectedCheck> Checks { get; }
}

internal sealed class ProductionQualificationInputs
{
    internal static ProductionQualificationInputs Unconfigured { get; } = new(
        ProductionQualificationAuthority.Unconfigured, Array.Empty<ProductionQualificationRequirement>(),
        Array.Empty<ProductionQualificationProof>(), new Dictionary<ProductionQualificationLayer, string>(),
        powerLossRequired: true, powerLossExclusionHash: null);

    internal ProductionQualificationInputs(ProductionQualificationAuthority authority,
        IReadOnlyList<ProductionQualificationRequirement> requirements,
        IReadOnlyList<ProductionQualificationProof> records,
        IReadOnlyDictionary<ProductionQualificationLayer, string> currentRecordHeads,
        bool powerLossRequired, string? powerLossExclusionHash)
    {
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        if (requirements is null || requirements.Count > 6 || records is null || records.Count > 64 ||
            currentRecordHeads is null || currentRecordHeads.Count > 6 ||
            requirements.Select(r => r.Layer).Distinct().Count() != requirements.Count ||
            records.Select(r => r.ContentHash).Distinct(StringComparer.Ordinal).Count() != records.Count)
            throw new ArgumentException("ProductionQualificationInputsInvalid");
        if (!powerLossRequired && powerLossExclusionHash is null || powerLossRequired && powerLossExclusionHash is not null)
            throw new ArgumentException("ProductionPowerLossApplicabilityInvalid");
        if (powerLossExclusionHash is not null) ProductionAdmissionCanonical.RequireHash(powerLossExclusionHash);
        Requirements = new ReadOnlyDictionary<ProductionQualificationLayer, ProductionQualificationRequirement>(
            requirements.ToDictionary(requirement => requirement.Layer));
        Records = new ReadOnlyDictionary<string, ProductionQualificationProof>(
            records.ToDictionary(record => record.ContentHash, StringComparer.Ordinal));
        var heads = new SortedDictionary<ProductionQualificationLayer, string>();
        foreach (var pair in currentRecordHeads)
        {
            if (!Enum.IsDefined(pair.Key)) throw new ArgumentException("ProductionQualificationHeadInvalid");
            heads.Add(pair.Key, ProductionAdmissionCanonical.RequireHash(pair.Value));
        }
        CurrentRecordHeads = new ReadOnlyDictionary<ProductionQualificationLayer, string>(heads);
        PowerLossRequired = powerLossRequired;
        PowerLossExclusionHash = powerLossExclusionHash;
    }
    internal ProductionQualificationAuthority Authority { get; }
    internal IReadOnlyDictionary<ProductionQualificationLayer, ProductionQualificationRequirement> Requirements { get; }
    internal IReadOnlyDictionary<string, ProductionQualificationProof> Records { get; }
    internal IReadOnlyDictionary<ProductionQualificationLayer, string> CurrentRecordHeads { get; }
    internal bool PowerLossRequired { get; }
    internal string? PowerLossExclusionHash { get; }
}

internal enum QualificationEvidenceStatus { Passed, Missing, NotConfigured, Mismatch, Expired, Failed, Untrusted, NotApplicable }
internal sealed record QualificationEvidenceResult(ProductionQualificationLayer Layer, QualificationEvidenceStatus Status,
    string ReasonCode, string? ExpectedFingerprint, string? ObservedFingerprint, string? EvidenceRecordHash);

internal static class ProductionQualificationMatcher
{
    internal static IReadOnlyList<QualificationEvidenceResult> Evaluate(ProductionConfiguration configuration,
        ProductionQualificationInputs inputs, DateTimeOffset now)
    {
        var results = Enum.GetValues<ProductionQualificationLayer>()
            .Select(layer => EvaluateLayer(configuration, inputs, layer, now)).ToArray();
        var stationIndex = Array.FindIndex(results, result => result.Layer == ProductionQualificationLayer.StationAcceptance);
        if (results[stationIndex].Status == QualificationEvidenceStatus.Passed)
        {
            var station = inputs.Records[results[stationIndex].EvidenceRecordHash!];
            var requiredLayers = Enum.GetValues<ProductionQualificationLayer>().Where(layer =>
                layer != ProductionQualificationLayer.StationAcceptance &&
                (layer != ProductionQualificationLayer.PowerLoss || inputs.PowerLossRequired)).ToArray();
            var exactReferences = station.References.Count == requiredLayers.Length && requiredLayers.All(layer =>
                results.Single(result => result.Layer == layer).Status == QualificationEvidenceStatus.Passed &&
                inputs.CurrentRecordHeads.TryGetValue(layer, out var hash) &&
                station.References.TryGetValue(layer, out var referenced) && referenced == hash);
            if (!exactReferences)
                results[stationIndex] = results[stationIndex] with { Status = QualificationEvidenceStatus.Mismatch,
                    ReasonCode = "StationAcceptanceEvidenceReferenceMismatch" };
        }
        return Array.AsReadOnly(results);
    }

    private static QualificationEvidenceResult EvaluateLayer(ProductionConfiguration configuration,
        ProductionQualificationInputs inputs, ProductionQualificationLayer layer, DateTimeOffset now)
    {
        var expected = layer switch
        {
            ProductionQualificationLayer.Framework => configuration.FrameworkFingerprint,
            ProductionQualificationLayer.Provider or ProductionQualificationLayer.ProviderHardware => configuration.ProviderFingerprint,
            ProductionQualificationLayer.Performance => configuration.PerformanceFingerprint,
            _ => configuration.StationAcceptanceFingerprint
        };
        QualificationEvidenceResult Result(QualificationEvidenceStatus status, string reason,
            ProductionQualificationProof? proof = null) => new(layer, status, reason, expected,
                proof?.TargetFingerprint, proof?.ContentHash);
        if (expected is null || configuration.ProfileHash is null)
            return Result(QualificationEvidenceStatus.Missing, "ProductionTargetFingerprintIncomplete");
        if (!inputs.Authority.Configured)
            return Result(QualificationEvidenceStatus.NotConfigured, "ProductionQualificationAuthorityUnavailable");
        if (!inputs.Requirements.TryGetValue(layer, out var requirement))
            return Result(QualificationEvidenceStatus.Missing, "ProductionQualificationRequirementMappingMissing");
        if (!inputs.CurrentRecordHeads.TryGetValue(layer, out var head) || !inputs.Records.TryGetValue(head, out var proof))
            return Result(QualificationEvidenceStatus.Missing, "ProductionQualificationRecordMissing");
        if (proof.Layer != layer || !inputs.Authority.Verify(proof))
            return Result(QualificationEvidenceStatus.Untrusted, "ProductionQualificationRecordUntrusted", proof);
        QualificationEvidenceResult Mismatch(string reason, string expectedHash, string observedHash) =>
            new(layer, QualificationEvidenceStatus.Mismatch, reason, expectedHash, observedHash, proof.ContentHash);
        if (proof.TargetFingerprint != expected)
            return Mismatch("ProductionQualificationTargetMismatch", expected, proof.TargetFingerprint);
        if (proof.ProfileHash != configuration.ProfileHash)
            return Mismatch("ProductionQualificationProfileMismatch", configuration.ProfileHash, proof.ProfileHash);
        if (proof.ScopeHash != requirement.ScopeHash)
            return Mismatch("ProductionQualificationScopeMismatch", requirement.ScopeHash, proof.ScopeHash);
        if (proof.ContextHash != requirement.ContextHash)
            return Mismatch("ProductionQualificationContextMismatch", requirement.ContextHash, proof.ContextHash);
        if (proof.IssuedAtUtc > now || proof.ExpiresAtUtc is { } expiry && (expiry <= now || expiry <= proof.IssuedAtUtc))
            return Result(QualificationEvidenceStatus.Expired, "ProductionQualificationRecordNotCurrent", proof);
        if (proof.CandidateHasProductFailure || proof.Checks.Count != requirement.Checks.Count || proof.Checks.Any(check =>
            !requirement.Checks.TryGetValue(check.VerificationId, out var expectedCheck) ||
            expectedCheck.Mandatory != check.Mandatory || expectedCheck.Applicable != check.Applicable ||
            expectedCheck.ExclusionProofHash != check.ExclusionProofHash ||
            (expectedCheck.Applicable ? check.Outcome != ConformanceOutcome.Pass :
                check.Outcome != ConformanceOutcome.NotApplicable || check.Mandatory)))
            return Result(QualificationEvidenceStatus.Failed, "ProductionQualificationRequiredGateNotPassed", proof);
        if (layer == ProductionQualificationLayer.PowerLoss && !inputs.PowerLossRequired)
        {
            // NotApplicable is itself an authoritative claim about the frozen profile.
            // A caller's boolean or arbitrary hash is never sufficient: the current,
            // signed layer head must contain the exact optional exclusion observation.
            if (requirement.Checks.Count != 1 || !requirement.Checks.TryGetValue("PowerLoss.claim", out var exclusion) ||
                exclusion.Mandatory || exclusion.Applicable || exclusion.ExclusionProofHash != inputs.PowerLossExclusionHash ||
                proof.Checks[0].VerificationId != "PowerLoss.claim" || proof.Checks[0].Outcome != ConformanceOutcome.NotApplicable)
                return Result(QualificationEvidenceStatus.Failed, "PowerLossExclusionEvidenceMismatch", proof);
            return Result(QualificationEvidenceStatus.NotApplicable, "PowerLossExcludedByFrozenProfile", proof);
        }
        if (layer == ProductionQualificationLayer.PowerLoss && proof.Checks.All(check => !check.Applicable))
            return Result(QualificationEvidenceStatus.Failed, "PowerLossRequiredClaimExcluded", proof);
        if (layer == ProductionQualificationLayer.StationAcceptance &&
            (proof.ApprovingPrincipalId is null || proof.ApprovalEvidenceHash is null))
            return Result(QualificationEvidenceStatus.Missing, "StationAcceptanceApprovalMissing", proof);
        return Result(QualificationEvidenceStatus.Passed, "ProductionQualificationExactlyMatches", proof);
    }
}
