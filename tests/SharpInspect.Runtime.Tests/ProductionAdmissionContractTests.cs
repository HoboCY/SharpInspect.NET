using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionAdmissionContractTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    [Trait("VerificationId", "V136_C01")]
    public void CompleteFixedGateSetDerivesCanArmAndCanonicalHash()
    {
        var report = CreateReport();

        Assert.True(report.CanArm);
        Assert.Equal(ProductionAdmissionReport.RequiredGates.Count, report.Gates.Count);
        Assert.Equal(ProductionAdmissionReport.RequiredGates.ToArray(),
            report.Gates.Select(value => value.Gate).ToArray());
        Assert.All(report.Gates, value => Assert.Equal(Hash, value.EvidenceRecordHash));
        Assert.Equal(64, report.ContentHash.Length);
    }

    [Fact]
    [Trait("VerificationId", "V136_C02")]
    public void NonPassingGateBlocksCanArmAndReportDefensivelyCopiesRows()
    {
        var rows = CreateRows();
        var report = new ProductionAdmissionReport(Guid.NewGuid(), 12, 4, DateTimeOffset.UtcNow,
            Hash, Hash, Hash, Hash, Hash, rows);
        var originalHash = report.ContentHash;

        rows[0] = new ProductionAdmissionGateResult(ProductionAdmissionGate.DeploymentPolicies,
            ProductionAdmissionGateStatus.Mismatch, "DeploymentPoliciesMismatch", Hash, Hash, Hash);

        Assert.True(report.CanArm);
        Assert.Equal(originalHash, report.ContentHash);

        var blocked = CreateReport(new Dictionary<ProductionAdmissionGate,
            ProductionAdmissionGateStatus>
        {
            [ProductionAdmissionGate.CameraBinding] = ProductionAdmissionGateStatus.Mismatch
        });

        Assert.False(blocked.CanArm);
        Assert.Equal(ProductionAdmissionGateStatus.Mismatch,
            blocked.Gates.Single(value => value.Gate == ProductionAdmissionGate.CameraBinding).Status);
        Assert.NotEqual(report.ContentHash, blocked.ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V136_C03")]
    public void ReportRejectsIncompleteOrInvalidGateProjection()
    {
        var rows = CreateRows();
        Assert.Throws<ArgumentException>(() => new ProductionAdmissionReport(Guid.NewGuid(), 1, 1,
            DateTimeOffset.UtcNow, null, null, null, null, null, rows.Take(rows.Count - 1)));

        Assert.Throws<ArgumentException>(() => new ProductionAdmissionGateResult(
            ProductionAdmissionGate.CameraHealth, ProductionAdmissionGateStatus.NotApplicable,
            "CameraNotApplicable"));
    }

    [Fact]
    [Trait("VerificationId", "V136_C04")]
    public void GateEvidenceReferencesAreOrderedDeduplicatedAndBounded()
    {
        const string secondHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var rows = CreateRows();
        var providerIndex = rows.FindIndex(value =>
            value.Gate == ProductionAdmissionGate.ProviderQualification);
        rows[providerIndex] = new ProductionAdmissionGateResult(
            ProductionAdmissionGate.ProviderQualification, ProductionAdmissionGateStatus.Passed,
            "GatePassed", Hash, Hash, Hash, new[] { secondHash, Hash });

        var epoch = Guid.NewGuid();
        var observedAt = DateTimeOffset.UtcNow;
        var report = new ProductionAdmissionReport(epoch, 2, 1, observedAt,
            null, null, null, null, null, rows);
        var baseline = new ProductionAdmissionReport(epoch, 2, 1, observedAt,
            null, null, null, null, null, CreateRows());
        var evidence = report.Gates.Single(value =>
            value.Gate == ProductionAdmissionGate.ProviderQualification).EvidenceRecordHashes;

        Assert.Equal(new[] { Hash, secondHash }, evidence);
        Assert.Equal(Hash, report.Gates.Single(value =>
            value.Gate == ProductionAdmissionGate.ProviderQualification).EvidenceRecordHash);
        Assert.NotEqual(baseline.ContentHash, report.ContentHash);

        var tooMany = Enumerable.Range(0, 6)
            .Select(value => new string((char)('0' + value), 64));
        Assert.Throws<ArgumentException>(() => new ProductionAdmissionGateResult(
            ProductionAdmissionGate.ProviderQualification, ProductionAdmissionGateStatus.Passed,
            "GatePassed", Hash, Hash, Hash, tooMany));
    }

    [Fact]
    [Trait("VerificationId", "V136_C05")]
    public void PrimaryEvidenceReferenceIsDerivedFromTheCanonicalReferenceList()
    {
        var row = new ProductionAdmissionGateResult(ProductionAdmissionGate.ProviderQualification,
            ProductionAdmissionGateStatus.Missing, "ProviderContractMissing", additionalEvidenceRecordHashes: new[] { Hash });
        Assert.Equal(Hash, row.EvidenceRecordHash);
        Assert.Equal(row.EvidenceRecordHashes[0], row.EvidenceRecordHash);
    }

    private static ProductionAdmissionReport CreateReport(
        IReadOnlyDictionary<ProductionAdmissionGate, ProductionAdmissionGateStatus>? overrides = null)
    {
        return new ProductionAdmissionReport(Guid.NewGuid(), 7, 3, DateTimeOffset.UtcNow,
            Hash, Hash, Hash, Hash, Hash, CreateRows(overrides));
    }

    private static List<ProductionAdmissionGateResult> CreateRows(
        IReadOnlyDictionary<ProductionAdmissionGate, ProductionAdmissionGateStatus>? overrides = null)
    {
        return ProductionAdmissionReport.RequiredGates.Select(gate =>
        {
            var status = overrides is not null && overrides.TryGetValue(gate, out var value)
                ? value
                : gate == ProductionAdmissionGate.PowerLossQualification
                    ? ProductionAdmissionGateStatus.NotApplicable
                    : ProductionAdmissionGateStatus.Passed;
            var reason = status == ProductionAdmissionGateStatus.NotApplicable
                ? "PowerLossQualificationNotApplicable"
                : status == ProductionAdmissionGateStatus.Passed
                    ? "GatePassed"
                    : "GateBlocked";
            return new ProductionAdmissionGateResult(gate, status, reason, Hash, Hash, Hash);
        }).ToList();
    }
}
