using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionAdmissionQualificationTests
{
    public static IEnumerable<object[]> Layers() => Enum.GetValues<ProductionQualificationLayer>().Select(layer => new object[] { (int)layer });

    [Theory]
    [MemberData(nameof(Layers))]
    public void V136_Q01_EachMissingQualificationLayerBlocksArmAndCannotBeReplacedByAnotherLayer(int value)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var layer = (ProductionQualificationLayer)value;
        var input = fixture.Inputs(missing: layer);
        var results = ProductionQualificationMatcher.Evaluate(fixture.Configuration, input, fixture.Now);
        Assert.Equal(QualificationEvidenceStatus.Missing, results.Single(result => result.Layer == layer).Status);
        Assert.False(fixture.Evaluate(fixture.Facts(inputs: input)).CanArm);
    }

    [Theory]
    [InlineData("DevelopmentOnly", false, "test-only-issuer")]
    [InlineData("Production", true, "test-only-issuer")]
    [InlineData("Production", false, "unknown-issuer")]
    public void V136_Q02_DevelopmentPurposeTamperedSignatureOrUnknownIssuerNeverGrantsProductionAuthority(
        string purpose, bool corruptSignature, string issuerId)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var proof = fixture.Issue(ProductionQualificationLayer.Performance, purpose: purpose,
            corruptSignature: corruptSignature, issuerId: issuerId);
        var results = ProductionQualificationMatcher.Evaluate(fixture.Configuration, fixture.Inputs(proof), fixture.Now);
        Assert.Equal(QualificationEvidenceStatus.Untrusted, results.Single(result => result.Layer == ProductionQualificationLayer.Performance).Status);
    }

    [Theory]
    [InlineData(ConformanceOutcome.Fail)]
    [InlineData(ConformanceOutcome.NotRun)]
    [InlineData(ConformanceOutcome.Blocked)]
    [InlineData(ConformanceOutcome.InvalidHarness)]
    [InlineData(ConformanceOutcome.NotApplicable)]
    public void V136_Q03_ApprovalCannotOverrideAnyNonPassingMandatoryOutcome(ConformanceOutcome outcome)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var proof = fixture.Issue(ProductionQualificationLayer.StationAcceptance,
            checks: new[] { new ProductionQualificationCheck("StationAcceptance.required", outcome, true, true) },
            references: fixture.Heads.Where(pair => pair.Key != ProductionQualificationLayer.StationAcceptance).ToDictionary(pair => pair.Key, pair => pair.Value),
            approval: true);
        var results = ProductionQualificationMatcher.Evaluate(fixture.Configuration, fixture.Inputs(proof), fixture.Now);
        Assert.Equal(QualificationEvidenceStatus.Failed, results.Single(result => result.Layer == ProductionQualificationLayer.StationAcceptance).Status);
    }

    [Fact]
    public void V136_Q04_ChangingMandatoryApplicabilityOrOmittingMappedVerificationDoesNotPass()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var downgraded = fixture.Issue(ProductionQualificationLayer.Framework,
            checks: new[] { new ProductionQualificationCheck("Framework.required", ConformanceOutcome.NotApplicable,
                false, false, ProductionAdmissionTestFixture.Hash("waiver")) });
        var omitted = fixture.Issue(ProductionQualificationLayer.Framework,
            checks: new[] { new ProductionQualificationCheck("different.unmapped", ConformanceOutcome.Pass, true, true) });
        foreach (var proof in new[] { downgraded, omitted })
            Assert.Equal(QualificationEvidenceStatus.Failed, ProductionQualificationMatcher.Evaluate(
                fixture.Configuration, fixture.Inputs(proof), fixture.Now).Single(result => result.Layer == proof.Layer).Status);
    }

    [Fact]
    public void V136_Q05_ExactExpiryFutureIssueAndUnapprovedAcceptanceAreNonPassing()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var expired = fixture.Issue(ProductionQualificationLayer.Performance, expiresAt: fixture.Now);
        var future = fixture.Issue(ProductionQualificationLayer.Performance, issuedAt: fixture.Now.AddSeconds(1));
        foreach (var proof in new[] { expired, future })
            Assert.Equal(QualificationEvidenceStatus.Expired, ProductionQualificationMatcher.Evaluate(
                fixture.Configuration, fixture.Inputs(proof), fixture.Now).Single(result => result.Layer == proof.Layer).Status);
        var unapproved = fixture.Issue(ProductionQualificationLayer.StationAcceptance, approval: false);
        Assert.Equal("StationAcceptanceApprovalMissing", ProductionQualificationMatcher.Evaluate(
            fixture.Configuration, fixture.Inputs(unapproved), fixture.Now).Single(result => result.Layer == unapproved.Layer).ReasonCode);
    }

    [Fact]
    public void V136_Q06_NewestSignedApprovedRecordCannotSubstituteForExactScopeOrReferences()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var wrongScope = fixture.Issue(ProductionQualificationLayer.Performance,
            scopeHash: ProductionAdmissionTestFixture.Hash("different-scope"), issuedAt: fixture.Now);
        Assert.Equal(QualificationEvidenceStatus.Mismatch, ProductionQualificationMatcher.Evaluate(
            fixture.Configuration, fixture.Inputs(wrongScope), fixture.Now).Single(result => result.Layer == wrongScope.Layer).Status);
        var replacement = fixture.Issue(ProductionQualificationLayer.Performance, issuedAt: fixture.Now);
        var results = ProductionQualificationMatcher.Evaluate(fixture.Configuration, fixture.Inputs(replacement), fixture.Now);
        Assert.Equal(QualificationEvidenceStatus.Passed, results.Single(result => result.Layer == replacement.Layer).Status);
        Assert.Equal("StationAcceptanceEvidenceReferenceMismatch", results.Single(result => result.Layer == ProductionQualificationLayer.StationAcceptance).ReasonCode);
        Assert.Equal(6, fixture.Records.Count); // Earlier immutable records remain retained.
    }

    [Fact]
    public void V136_Q07_ProductionCompositionHasNoEmbeddedAuthorityForEvenCryptographicallyValidTestRecords()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var input = fixture.Inputs(authority: ProductionQualificationAuthority.Unconfigured);
        var results = ProductionQualificationMatcher.Evaluate(fixture.Configuration, input, fixture.Now);
        Assert.All(results, result => Assert.Equal(QualificationEvidenceStatus.NotConfigured, result.Status));
        Assert.False(fixture.Evaluate(fixture.Facts(inputs: input)).CanArm);
    }

    [Fact]
    public void V136_Q08_CandidateProductFailureRemainsFailedDespiteLaterPassingRows()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var proof = fixture.Issue(ProductionQualificationLayer.Framework, candidateFailure: true);
        var result = ProductionQualificationMatcher.Evaluate(fixture.Configuration, fixture.Inputs(proof), fixture.Now)
            .Single(result => result.Layer == proof.Layer);
        Assert.Equal(QualificationEvidenceStatus.Failed, result.Status);
    }

    [Fact]
    public void V136_Q09_FrozenOptionalPowerLossExclusionDoesNotRemoveOtherRequiredLayers()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var references = fixture.Heads.Where(pair => pair.Key is not
            (ProductionQualificationLayer.StationAcceptance or ProductionQualificationLayer.PowerLoss)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var proof = fixture.Issue(ProductionQualificationLayer.StationAcceptance, references: references);
        var exclusionHash = ProductionAdmissionTestFixture.Hash("frozen-no-power-loss-claim");
        var exclusionProof = fixture.Issue(ProductionQualificationLayer.PowerLoss,
            checks: new[] { new ProductionQualificationCheck("PowerLoss.claim", ConformanceOutcome.NotApplicable, false, false, exclusionHash) });
        var requirements = fixture.Requirements.Where(requirement => requirement.Layer != ProductionQualificationLayer.PowerLoss)
            .Append(new ProductionQualificationRequirement(ProductionQualificationLayer.PowerLoss,
                ProductionAdmissionTestFixture.Hash("scope:PowerLoss"), ProductionAdmissionTestFixture.Hash("context:PowerLoss"),
                new Dictionary<string, ProductionQualificationExpectedCheck> { ["PowerLoss.claim"] = new(false, false, exclusionHash) })).ToArray();
        var records = fixture.Records.Concat(new[] { proof, exclusionProof }).ToArray();
        var heads = new Dictionary<ProductionQualificationLayer, string>(fixture.Heads)
            { [ProductionQualificationLayer.StationAcceptance] = proof.ContentHash, [ProductionQualificationLayer.PowerLoss] = exclusionProof.ContentHash };
        var input = new ProductionQualificationInputs(fixture.Authority, requirements, records, heads, false, exclusionHash);
        var report = fixture.Evaluate(fixture.Facts(inputs: input));
        Assert.True(report.CanArm);
        var excluded = report.Gates.Single(gate => gate.Gate == ProductionAdmissionGate.PowerLossQualification);
        Assert.Equal(ProductionAdmissionGateStatus.NotApplicable, excluded.Status);
        Assert.NotNull(excluded.EvidenceRecordHash);
        var tampered = new ProductionQualificationInputs(fixture.Authority, requirements, records, heads, false,
            ProductionAdmissionTestFixture.Hash("arbitrary-unsigned-exclusion"));
        Assert.False(fixture.Evaluate(fixture.Facts(inputs: tampered)).CanArm);
        var contradicted = new ProductionQualificationInputs(fixture.Authority, requirements, records, heads, true, null);
        Assert.Equal("PowerLossRequiredClaimExcluded", ProductionQualificationMatcher.Evaluate(fixture.Configuration,
            contradicted, fixture.Now).Single(result => result.Layer == ProductionQualificationLayer.PowerLoss).ReasonCode);
    }

    [Fact]
    public void V136_Q10_AuthorityScopeCannotBeBroadenedByMutatingItsCallerOwnedLayerSet()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var allowed = new HashSet<ProductionQualificationLayer> { ProductionQualificationLayer.Framework };
        var authority = fixture.RestrictAuthority(allowed);
        Assert.True(authority.Verify(fixture.Records.Single(record => record.Layer == ProductionQualificationLayer.Framework)));
        var identity = authority.ContentHash;
        allowed.Add(ProductionQualificationLayer.StationAcceptance);
        Assert.Equal(identity, authority.ContentHash);
        Assert.False(authority.Verify(fixture.Records.Single(record => record.Layer == ProductionQualificationLayer.StationAcceptance)));
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void V136_Q11_DifferentSignedQualificationContextCannotReuseTheFrozenRequirement(int value)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var layer = (ProductionQualificationLayer)value;
        var proof = fixture.Issue(layer, contextHash: ProductionAdmissionTestFixture.Hash("changed-harness-threshold-dataset"));
        var inputs = fixture.Inputs(proof);
        var result = ProductionQualificationMatcher.Evaluate(fixture.Configuration, inputs, fixture.Now).Single(result => result.Layer == layer);
        Assert.True(fixture.Authority.Verify(proof));
        Assert.Equal(QualificationEvidenceStatus.Mismatch, result.Status);
        Assert.False(fixture.Evaluate(fixture.Facts(inputs: inputs)).CanArm);
    }

    [Fact]
    public void V136_Q12_BarePowerLossFlagAndHashCannotClaimFrozenOptionalExclusion()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var references = fixture.Heads.Where(pair => pair.Key is not
            (ProductionQualificationLayer.StationAcceptance or ProductionQualificationLayer.PowerLoss)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var proof = fixture.Issue(ProductionQualificationLayer.StationAcceptance, references: references);
        var input = fixture.Inputs(proof, powerLossRequired: false);
        var report = fixture.Evaluate(fixture.Facts(inputs: input));
        Assert.False(report.CanArm);
        Assert.Equal("PowerLossExclusionEvidenceMismatch", report.Gates.Single(gate => gate.Gate == ProductionAdmissionGate.PowerLossQualification).ReasonCode);
    }

    [Fact]
    public void V136_Q13_DefaultTimestampCannotServeAsQualificationIssuanceEvidence()
    {
        using var fixture = new ProductionAdmissionTestFixture();
        Assert.Throws<ArgumentException>(() => fixture.Issue(ProductionQualificationLayer.Framework,
            issuedAt: default(DateTimeOffset)));
        Assert.Throws<ArgumentException>(() => fixture.Issue(ProductionQualificationLayer.Framework,
            expiresAt: default(DateTimeOffset)));
    }

    [Theory]
    [InlineData("Target")]
    [InlineData("Profile")]
    [InlineData("Scope")]
    [InlineData("Context")]
    public void V136_Q14_MismatchReportShowsTheActualDifferingFingerprintAndSpecificReason(string binding)
    {
        using var fixture = new ProductionAdmissionTestFixture();
        var changed = ProductionAdmissionTestFixture.Hash("different:" + binding);
        var proof = fixture.Issue(ProductionQualificationLayer.Performance,
            targetFingerprint: binding == "Target" ? changed : null, profileHash: binding == "Profile" ? changed : null,
            scopeHash: binding == "Scope" ? changed : null, contextHash: binding == "Context" ? changed : null);
        var report = fixture.Evaluate(fixture.Facts(inputs: fixture.Inputs(proof)));
        var row = report.Gates.Single(gate => gate.Gate == ProductionAdmissionGate.PerformanceQualification);
        Assert.Equal("ProductionQualification" + binding + "Mismatch", row.ReasonCode);
        Assert.Equal(changed, row.ObservedFingerprint);
        Assert.NotEqual(row.ExpectedFingerprint, row.ObservedFingerprint);
        Assert.False(report.CanArm);
    }
}
