using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionAdmissionEngineTests
{
    [Fact]
    public void V136_G01_IndependentVirtualStationPassesEveryFixedGateUsingTestOnlyAuthority()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var report = fixture.Evaluate();
        Assert.True(report.CanArm, string.Join(",", report.Gates.Where(gate => gate.Status != ProductionAdmissionGateStatus.Passed).Select(gate => gate.ReasonCode)));
        Assert.Equal(24, report.Gates.Count);
        Assert.All(report.Gates, gate => Assert.Equal(ProductionAdmissionGateStatus.Passed, gate.Status));
        Assert.Equal(fixture.Configuration.PerformanceFingerprint, report.PerformanceConfigurationFingerprint);
        Assert.Equal(fixture.Configuration.StationAcceptanceFingerprint, report.StationAcceptanceFingerprint);
        Assert.Equal(6, fixture.Records.Count);
    }

    public static IEnumerable<object[]> OrdinaryGates() => ProductionAdmissionReport.RequiredGates
        .Where(gate => !ProductionAdmissionEngine.IsQualificationGate(gate)).Select(gate => new object[] { gate });

    [Theory]
    [MemberData(nameof(OrdinaryGates))]
    public void V136_G02_EachMissingOrdinaryGateRejectsWithoutHidingOtherGates(ProductionAdmissionGate missing)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var report = fixture.Evaluate(fixture.Facts(missingGate: missing));
        Assert.False(report.CanArm);
        Assert.Equal(24, report.Gates.Count);
        Assert.Equal(ProductionAdmissionGateStatus.NotConfigured, report.Gates.Single(gate => gate.Gate == missing).Status);
        Assert.All(report.Gates.Where(gate => gate.Gate != missing), gate => Assert.Equal(ProductionAdmissionGateStatus.Passed, gate.Status));
    }

    [Fact]
    public void V136_G03_EmptyDeploymentReportsEveryMissingGateAndCannotUseDevelopmentEvidence()
    {
        var facts = new ProductionAdmissionFacts(new(new Dictionary<ProductionConfigurationBinding, string>()),
            ProductionQualificationInputs.Unconfigured, Array.Empty<ProductionAdmissionGateResult>(), new Dictionary<string, string>());
        using var fixture = new ProductionAdmissionTestFixture();
        var report = fixture.Evaluate(facts);
        Assert.False(report.CanArm);
        Assert.Null(report.PerformanceConfigurationFingerprint);
        Assert.Null(report.StationAcceptanceFingerprint);
        Assert.All(report.Gates, gate => Assert.NotEqual(ProductionAdmissionGateStatus.Passed, gate.Status));
        Assert.Contains(report.Gates, gate => gate.ReasonCode == "ProductionCycleUnavailable");
    }

    [Fact]
    public void V136_G04_HeartbeatChangesReportIdentityButNotAdmissionEvidenceGeneration()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var initial = fixture.Evaluate();
        var heartbeat = fixture.Evaluate(now: fixture.Now.AddSeconds(1), revision: 2);
        Assert.NotEqual(initial.ContentHash, heartbeat.ContentHash);
        Assert.Equal(ProductionAdmissionEngine.EvidenceHash(initial), ProductionAdmissionEngine.EvidenceHash(heartbeat));
        var expired = fixture.Evaluate(now: fixture.Now.AddDays(2), revision: 3);
        Assert.False(expired.CanArm);
        Assert.NotEqual(ProductionAdmissionEngine.EvidenceHash(initial), ProductionAdmissionEngine.EvidenceHash(expired));
    }

    [Fact]
    public void V136_G05_RuntimeFactsCannotReplaceQualificationGatesWithUnconditionalPass()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        Assert.Throws<ArgumentException>(() => new ProductionAdmissionFacts(fixture.Configuration, fixture.Inputs(),
            new[] { new ProductionAdmissionGateResult(ProductionAdmissionGate.StationAcceptance, ProductionAdmissionGateStatus.Passed, "Forged") },
            new Dictionary<string, string>()));
    }

    public static IEnumerable<object[]> Bindings() => Enum.GetValues<ProductionConfigurationBinding>().Select(binding => new object[] { (int)binding });

    [Theory]
    [MemberData(nameof(Bindings))]
    public void V136_F01_EachMaterialBindingChangeInvalidatesExactQualificationAndRetainsOldRecords(int value)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var originalHashes = fixture.Records.Select(record => record.ContentHash).ToArray();
        var bindings = fixture.Configuration.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
        bindings[(ProductionConfigurationBinding)value] = ProductionAdmissionTestFixture.Hash("changed-binding");
        var changed = new ProductionConfiguration(bindings);
        Assert.NotEqual(fixture.Configuration.PerformanceFingerprint, changed.PerformanceFingerprint);
        Assert.NotEqual(fixture.Configuration.StationAcceptanceFingerprint, changed.StationAcceptanceFingerprint);
        var report = fixture.Evaluate(fixture.Facts(configuration: changed));
        Assert.False(report.CanArm);
        Assert.Equal(ProductionAdmissionGateStatus.Mismatch, report.Gates.Single(gate => gate.Gate == ProductionAdmissionGate.PerformanceQualification).Status);
        Assert.Equal(originalHashes, fixture.Records.Select(record => record.ContentHash));
    }

    [Fact]
    public void V136_F02_MissingMaterialBindingDoesNotInventAnOptionalConfiguration()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var bindings = fixture.Configuration.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
        bindings.Remove(ProductionConfigurationBinding.Model);
        var incomplete = new ProductionConfiguration(bindings);
        Assert.Null(incomplete.PerformanceFingerprint);
        Assert.Null(incomplete.StationAcceptanceFingerprint);
        Assert.Contains(ProductionConfigurationBinding.Model, incomplete.MissingBindings);
        Assert.False(fixture.Evaluate(fixture.Facts(configuration: incomplete)).CanArm);
    }

    [Fact]
    public void V136_F03_ConfigurationOwnsItsBindingsAndOrderDoesNotAlterIdentity()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var source = fixture.Configuration.Bindings.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        var captured = new ProductionConfiguration(source);
        source[ProductionConfigurationBinding.Model] = ProductionAdmissionTestFixture.Hash("later-mutation");
        Assert.Equal(fixture.Configuration.PerformanceFingerprint, captured.PerformanceFingerprint);
        Assert.Equal(fixture.Configuration.ObservedBindingsHash, captured.ObservedBindingsHash);
    }
}
