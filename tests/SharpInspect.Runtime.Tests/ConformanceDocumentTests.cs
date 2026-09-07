using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Conformance;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ConformanceDocumentTests
{
    [Fact]
    public void V107_C01_ProfileFreezeSortsAndRoundTripsTheBidirectionalMapping()
    {
        var requirements = new[]
        {
            new ConformanceRequirement("REQ-002", "ADR-002", "Optional provider behavior is explicit.", false),
            new ConformanceRequirement("REQ-001", "ADR-001", "The command evidence is attributable.", true)
        };
        var cases = new[]
        {
            Verification("V107-C01-B", QualificationLayer.Provider, VerificationMethod.GovernedInspection, "REQ-002"),
            Verification("V107-C01-A", QualificationLayer.Framework, VerificationMethod.Executable, "REQ-001")
        };
        var platforms = new List<string> { "win-x64", "linux-x64" };
        var profile = new ConformanceProfile(
            "profile-v107", 1, ConformanceClaim.DevelopmentOnly, "foundation",
            platforms, new[] { "trace", "audit" },
            new[] { QualificationLayer.Provider, QualificationLayer.Framework },
            "retain immutable evidence", "ReleaseAuthority", requirements, cases);

        platforms[0] = "mutated-after-construction";
        Assert.Equal("win-x64", profile.SupportedPlatforms[0]);

        var frozen = ConformanceDocuments.FreezeProfile(profile);
        Assert.Equal("ConformanceProfile", frozen.Kind);
        Assert.Contains("\"SchemaVersion\":1,\"CanonicalizationVersion\":1", frozen.CanonicalJson);
        Assert.Equal(64, frozen.Sha256.Length);
        Assert.Equal(frozen.Sha256, frozen.Sha256.ToUpperInvariant());

        var reordered = new ConformanceProfile(
            "profile-v107", 1, ConformanceClaim.DevelopmentOnly, "foundation",
            new[] { "linux-x64", "win-x64" }, new[] { "audit", "trace" },
            new[] { QualificationLayer.Framework, QualificationLayer.Provider },
            "retain immutable evidence", "ReleaseAuthority",
            new[] { requirements[1], requirements[0] }, new[] { cases[1], cases[0] });
        var reorderedFrozen = ConformanceDocuments.FreezeProfile(reordered);

        Assert.Equal(frozen.CanonicalJson, reorderedFrozen.CanonicalJson);
        Assert.Equal(frozen.Sha256, reorderedFrozen.Sha256);

        var read = ConformanceDocuments.ReadProfile(frozen);
        Assert.Equal("REQ-001", read.Cases.Single(item => item.TestId == "V107-C01-A").RequirementIds.Single());
        Assert.Equal("V107-C01-B", read.Cases.Single(item => item.RequirementIds.Contains("REQ-002")).TestId);
        Assert.Equal("V107-C01-A", read.GetCasesForRequirement("REQ-001").Single().TestId);
        Assert.Equal("REQ-002", read.GetRequirementsForCase("V107-C01-B").Single().RequirementId);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)read.Features)[0] = "mutated");
    }

    [Fact]
    public void V107_C02_ProfileRejectsMandatoryNotApplicableAndUnknownMappings()
    {
        var mandatory = new ConformanceRequirement("REQ-001", "ADR-001", "Must be covered.", true);
        var excluded = Verification("V107-C02-NA", QualificationLayer.Framework,
            VerificationMethod.Executable, "REQ-001", applicable: false);
        var applicable = Verification("V107-C02-PASS", QualificationLayer.Framework,
            VerificationMethod.Executable, "REQ-001");

        Assert.Throws<ArgumentException>(() => new ConformanceProfile(
            "profile-v107", 1, ConformanceClaim.DevelopmentOnly, "scope", new[] { "win-x64" },
            new[] { "trace" }, new[] { QualificationLayer.Framework }, "retain", "authority",
            new[] { mandatory }, new[] { excluded, applicable }));

        var unknown = Verification("V107-C02-UNKNOWN", QualificationLayer.Framework,
            VerificationMethod.Executable, "REQ-MISSING");
        Assert.Throws<ArgumentException>(() => new ConformanceProfile(
            "profile-v107", 1, ConformanceClaim.DevelopmentOnly, "scope", new[] { "win-x64" },
            new[] { "trace" }, new[] { QualificationLayer.Framework }, "retain", "authority",
            new[] { new ConformanceRequirement("REQ-001", "ADR-001", "Optional.", false) }, new[] { unknown }));
    }

    [Fact]
    public void V107_C03_CandidatePreservesExplicitEmptyGroupsAndBindsCategoryBytes()
    {
        var candidate = new ReleaseCandidateDefinition(
            "revision-107",
            Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(),
            new[] { Component("package-alt") },
            Array.Empty<FingerprintComponent>(),
            new[] { Component("candidate-file-manifest") },
            new[] { Component("lock") },
            new[] { Component("configuration") });

        var frozen = ConformanceDocuments.FreezeCandidate(candidate);
        var read = ConformanceDocuments.ReadCandidate(frozen);

        Assert.Empty(read.PublicApi);
        Assert.Empty(read.Schemas);
        Assert.Empty(read.Migrations);
        Assert.Equal("candidate-file-manifest", Assert.Single(read.EmbeddedAssets).Name);

        var moved = new ReleaseCandidateDefinition(
            "revision-107",
            Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(),
            new[] { Component("package") },
            Array.Empty<FingerprintComponent>(),
            new[] { Component("candidate-file-manifest") },
            new[] { Component("lock") },
            new[] { Component("configuration") });
        var movedFrozen = ConformanceDocuments.FreezeCandidate(moved);
        Assert.NotEqual(frozen.CanonicalJson, movedFrozen.CanonicalJson);
        Assert.NotEqual(frozen.Sha256, movedFrozen.Sha256);
        Assert.Throws<ArgumentNullException>(() => new ReleaseCandidateDefinition(
            "revision-107", null!, Array.Empty<FingerprintComponent>(), new[] { Component("package") },
            Array.Empty<FingerprintComponent>(), new[] { Component("candidate-file-manifest") }, new[] { Component("lock") },
            new[] { Component("configuration") }));
        var nullGroupJson = frozen.CanonicalJson.Replace("\"PublicApi\":[]", "\"PublicApi\":null", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadCandidate(frozen with
        {
            CanonicalJson = nullGroupJson,
            Sha256 = HashText(nullGroupJson)
        }));
        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadCandidate(
            frozen with { Sha256 = new string('0', 64) }));
    }

    [Fact]
    public void V107_C07_CandidateManifestMustBePresentCorrectlyNamedAndUnique()
    {
        Assert.Throws<ArgumentException>(() => ConformanceDocuments.FreezeCandidate(
            CandidateDefinition(Array.Empty<FingerprintComponent>())));
        Assert.Throws<ArgumentException>(() => ConformanceDocuments.FreezeCandidate(
            CandidateDefinition(new[] { Component("wrong-manifest") })));
        var manifest = Component("candidate-file-manifest");
        Assert.Throws<ArgumentException>(() => CandidateDefinition(new[] { manifest, manifest }));

        var valid = ConformanceDocuments.FreezeCandidate(CandidateDefinition(new[] { manifest }));
        var withAsset = ConformanceDocuments.FreezeCandidate(CandidateDefinition(new[] { manifest, Component("actual-asset") }));
        Assert.Equal(2, ConformanceDocuments.ReadCandidate(withAsset).EmbeddedAssets.Count);
        var manifestJson = $"\"EmbeddedAssets\":[{{\"Name\":\"{manifest.Name}\",\"Sha256\":\"{manifest.Sha256}\"}}]";
        var missingJson = valid.CanonicalJson.Replace(manifestJson, "\"EmbeddedAssets\":[]", StringComparison.Ordinal);
        var wrongJson = valid.CanonicalJson.Replace(manifestJson,
            manifestJson.Replace(manifest.Name, "wrong-manifest", StringComparison.Ordinal), StringComparison.Ordinal);
        var duplicateArray = $"\"EmbeddedAssets\":[{{\"Name\":\"{manifest.Name}\",\"Sha256\":\"{manifest.Sha256}\"}},"
            + $"{{\"Name\":\"{manifest.Name}\",\"Sha256\":\"{manifest.Sha256}\"}}]";
        var duplicateJson = valid.CanonicalJson.Replace(manifestJson, duplicateArray, StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadCandidate(Rehash(valid, missingJson)));
        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadCandidate(Rehash(valid, wrongJson)));
        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadCandidate(Rehash(valid, duplicateJson)));
    }

    [Fact]
    public void V107_C08_Dataset_capacity_matches_execution_eight_input_limit()
    {
        QualificationContextDefinition Create(int count) => new(Hash(1), new[] { Component("harness") },
            new[] { Component("scenario") }, Enumerable.Range(0, count).Select(i => Component($"dataset-{i}")).ToArray(),
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            new[] { Component("environment") });
        Assert.Equal(8, ConformanceDocuments.ReadContext(ConformanceDocuments.FreezeContext(Create(8))).Datasets.Count);
        Assert.Throws<ArgumentException>(() => Create(9));
    }

    [Fact]
    public void V107_C04_ContextFreezeRequiresBoundedRequiredGroupsAndRoundTrips()
    {
        var context = new QualificationContextDefinition(
            Hash(0x11), new[] { Component("harness") }, new[] { Component("scenario") },
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            new[] { Component("environment") });

        var frozen = ConformanceDocuments.FreezeContext(context);
        var read = ConformanceDocuments.ReadContext(frozen);
        Assert.Equal(context.ProfileHash, read.ProfileHash);
        Assert.Single(read.Harnesses);
        Assert.Empty(read.Datasets);
        Assert.Single(read.EnvironmentConfiguration);

        var changedCategory = new QualificationContextDefinition(
            Hash(0x11), new[] { Component("different-harness") }, new[] { Component("scenario") },
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            new[] { Component("environment") });
        Assert.NotEqual(frozen.Sha256, ConformanceDocuments.FreezeContext(changedCategory).Sha256);

        Assert.Throws<ArgumentException>(() => new QualificationContextDefinition(
            Hash(0x11), Array.Empty<FingerprintComponent>(), new[] { Component("scenario") },
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            new[] { Component("environment") }));
    }

    [Fact]
    public void V107_C05_StrictValidationRejectsInvalidUtf8HashesAndBounds()
    {
        Assert.Throws<ArgumentException>(() => new ConformanceRequirement(
            "REQ-001", "ADR-001", "bad\uD800", true));
        Assert.Throws<ArgumentException>(() => new FingerprintComponent("package", new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new ReleaseCandidateDefinition(
            "revision-107", Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(), new[] { Component("candidate-file-manifest") },
            new[] { Component("lock") }, new[] { Component("configuration") }));
        Assert.Throws<ArgumentException>(() => new ReleaseCandidateDefinition(
            "revision-107", new[] { Component("same"), Component("same") }, Array.Empty<FingerprintComponent>(),
            new[] { Component("package") }, Array.Empty<FingerprintComponent>(), new[] { Component("candidate-file-manifest") },
            new[] { Component("lock") },
            new[] { Component("configuration") }));

        var large = new string('x', 8192);
        var applicability = new string('a', 4096);
        var oversized = new ConformanceProfile("profile-v107", 1, ConformanceClaim.DevelopmentOnly, "scope",
            new[] { "win-x64" }, new[] { "trace" }, new[] { QualificationLayer.Framework }, "retain", "authority",
            new[] { new ConformanceRequirement("REQ-001", "ADR-001", "Must be covered.", true) },
            new[] { new VerificationCase("V107-C05-LARGE", QualificationLayer.Framework,
                VerificationMethod.Executable, new[] { "REQ-001" }, true, applicability, string.Empty, large, large,
                large, large, large, large) });
        Assert.Throws<InvalidOperationException>(() => ConformanceDocuments.FreezeProfile(oversized));
    }

    [Fact]
    public void V107_C06_ReadRejectsReorderedJsonEvenWhenEnvelopeHashIsRecomputed()
    {
        var frozen = ConformanceDocuments.FreezeProfile(CreateMinimalProfile());
        var reorderedJson = frozen.CanonicalJson.Replace(
            "\"SchemaVersion\":1,\"CanonicalizationVersion\":1",
            "\"CanonicalizationVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal);
        Assert.NotEqual(frozen.CanonicalJson, reorderedJson);
        var recomputed = frozen with { CanonicalJson = reorderedJson, Sha256 = HashText(reorderedJson) };
        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadProfile(recomputed));

        var unsupportedVersionJson = frozen.CanonicalJson.Replace(
            "\"SchemaVersion\":1", "\"SchemaVersion\":2", StringComparison.Ordinal);
        var unsupportedVersion = frozen with
        {
            CanonicalJson = unsupportedVersionJson,
            Sha256 = HashText(unsupportedVersionJson)
        };
        Assert.NotEqual(frozen.Sha256, unsupportedVersion.Sha256);
        Assert.Throws<InvalidDataException>(() => ConformanceDocuments.ReadProfile(unsupportedVersion));
    }

    private static VerificationCase Verification(string testId, QualificationLayer layer,
        VerificationMethod method, string requirementId, bool applicable = true)
    {
        return new VerificationCase(testId, layer, method, new[] { requirementId }, applicable,
            applicable ? "always" : "outside this claim", applicable ? string.Empty : "excluded by profile scope",
            "isolated test environment", "store initialized", "perform one controlled action",
            "observable evidence is recorded", "immutable evidence with hashes", "expected result matches");
    }

    private static ConformanceProfile CreateMinimalProfile()
    {
        return new ConformanceProfile("profile-v107", 1, ConformanceClaim.DevelopmentOnly, "scope",
            new[] { "win-x64" }, new[] { "trace" }, new[] { QualificationLayer.Framework }, "retain", "authority",
            new[] { new ConformanceRequirement("REQ-001", "ADR-001", "Must be covered.", true) },
            new[] { Verification("V107-C06", QualificationLayer.Framework, VerificationMethod.Executable, "REQ-001") });
    }

    private static ReleaseCandidateDefinition CandidateDefinition(IReadOnlyList<FingerprintComponent> embeddedAssets)
    {
        return new ReleaseCandidateDefinition("revision-107", Array.Empty<FingerprintComponent>(),
            Array.Empty<FingerprintComponent>(), new[] { Component("package") },
            Array.Empty<FingerprintComponent>(), embeddedAssets, new[] { Component("lock") },
            new[] { Component("configuration") });
    }

    private static FrozenConformanceDocument Rehash(FrozenConformanceDocument document, string canonicalJson) =>
        document with { CanonicalJson = canonicalJson, Sha256 = HashText(canonicalJson) };

    private static FingerprintComponent Component(string name) => new(name, Hash(name.Length));

    private static string Hash(int seed) => Convert.ToHexString(SHA256.HashData(new[] { unchecked((byte)seed) }));

    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
