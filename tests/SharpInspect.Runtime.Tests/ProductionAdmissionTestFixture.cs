using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;

namespace SharpInspect.Runtime.Tests;

// This issuer, trust root and virtual station composition exist only in the test
// assembly. Nothing in the deployable Runtime or public DI registers them.
internal sealed class ProductionAdmissionTestFixture : IDisposable
{
    private readonly ECDsa _issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _recordNumber;
    internal ProductionAdmissionTestFixture()
    {
        Now = DateTimeOffset.UtcNow;
        Configuration = new ProductionConfiguration(Enum.GetValues<ProductionConfigurationBinding>()
            .ToDictionary(binding => binding, binding => Hash("virtual-test-station:" + binding)));
        Authority = new ProductionQualificationAuthority(new Dictionary<string, byte[]>
            { ["test-only-issuer"] = _issuer.ExportSubjectPublicKeyInfo() },
            new Dictionary<string, IReadOnlySet<ProductionQualificationLayer>>
            { ["test-only-issuer"] = new HashSet<ProductionQualificationLayer>(Enum.GetValues<ProductionQualificationLayer>()) });
        Requirements = Enum.GetValues<ProductionQualificationLayer>().Select(layer =>
            new ProductionQualificationRequirement(layer, Hash("scope:" + layer), Hash("context:" + layer),
                new Dictionary<string, ProductionQualificationExpectedCheck>
                { [layer + ".required"] = new(true, true) })).ToArray();
        Records = new List<ProductionQualificationProof>();
        foreach (var layer in Enum.GetValues<ProductionQualificationLayer>().Where(layer => layer != ProductionQualificationLayer.StationAcceptance))
            Records.Add(Issue(layer));
        Records.Add(Issue(ProductionQualificationLayer.StationAcceptance, references: Records.ToDictionary(record => record.Layer, record => record.ContentHash)));
        Heads = Records.ToDictionary(record => record.Layer, record => record.ContentHash);
    }

    internal DateTimeOffset Now { get; }
    internal ProductionConfiguration Configuration { get; }
    internal ProductionQualificationAuthority Authority { get; }
    internal IReadOnlyList<ProductionQualificationRequirement> Requirements { get; }
    internal List<ProductionQualificationProof> Records { get; }
    internal Dictionary<ProductionQualificationLayer, string> Heads { get; }
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal ProductionQualificationAuthority RestrictAuthority(IReadOnlySet<ProductionQualificationLayer> allowed) =>
        new(new Dictionary<string, byte[]> { ["test-only-issuer"] = _issuer.ExportSubjectPublicKeyInfo() },
            new Dictionary<string, IReadOnlySet<ProductionQualificationLayer>> { ["test-only-issuer"] = allowed });

    internal ProductionQualificationProof Issue(ProductionQualificationLayer layer,
        ProductionConfiguration? configuration = null, string? scopeHash = null, string? contextHash = null,
        string? targetFingerprint = null, string? profileHash = null,
        IReadOnlyList<ProductionQualificationCheck>? checks = null,
        IReadOnlyDictionary<ProductionQualificationLayer, string>? references = null,
        string purpose = "Production", string issuerId = "test-only-issuer", DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null, bool approval = true, bool candidateFailure = false,
        bool corruptSignature = false)
    {
        configuration ??= Configuration;
        var target = layer switch
        {
            ProductionQualificationLayer.Framework => configuration.FrameworkFingerprint,
            ProductionQualificationLayer.Provider or ProductionQualificationLayer.ProviderHardware => configuration.ProviderFingerprint,
            ProductionQualificationLayer.Performance => configuration.PerformanceFingerprint,
            _ => configuration.StationAcceptanceFingerprint
        };
        var id = "test-record-" + ++_recordNumber;
        var actualChecks = checks ?? new[] { new ProductionQualificationCheck(layer + ".required", ConformanceOutcome.Pass, true, true) };
        var actualReferences = references ?? new Dictionary<ProductionQualificationLayer, string>();
        ProductionQualificationProof Make(byte[] signature) => new(id, layer, issuerId, purpose,
            targetFingerprint ?? target!, profileHash ?? configuration.ProfileHash!,
            scopeHash ?? Hash("scope:" + layer), contextHash ?? Hash("context:" + layer),
            issuedAt ?? Now.AddMinutes(-1), expiresAt ?? Now.AddDays(1), actualChecks,
            new[] { Hash("raw-observation:" + id) }, actualReferences,
            layer == ProductionQualificationLayer.StationAcceptance && approval ? "test-human" : null,
            layer == ProductionQualificationLayer.StationAcceptance && approval ? Hash("step-up-approval:" + id) : null,
            candidateFailure, signature);
        var unsigned = Make(Array.Empty<byte>());
        var signature = _issuer.SignData(unsigned.Encode(), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (corruptSignature) signature[0] ^= 1;
        return Make(signature);
    }

    internal ProductionQualificationInputs Inputs(ProductionQualificationProof? replacement = null,
        ProductionQualificationLayer? missing = null, ProductionQualificationAuthority? authority = null,
        bool powerLossRequired = true)
    {
        var heads = new Dictionary<ProductionQualificationLayer, string>(Heads);
        var records = Records.ToList();
        if (replacement is not null) { heads[replacement.Layer] = replacement.ContentHash; records.Add(replacement); }
        if (missing is { } layer) heads.Remove(layer);
        return new(authority ?? Authority, Requirements, records, heads, powerLossRequired,
            powerLossRequired ? null : Hash("frozen-no-power-loss-claim"));
    }

    internal ProductionAdmissionFacts Facts(ProductionConfiguration? configuration = null,
        ProductionQualificationInputs? inputs = null, ProductionAdmissionGate? missingGate = null,
        ProductionAdmissionGateResult? replacementGate = null)
    {
        var gates = ProductionAdmissionReport.RequiredGates.Where(gate => !ProductionAdmissionEngine.IsQualificationGate(gate) && gate != missingGate)
            .Select(gate => replacementGate?.Gate == gate ? replacementGate :
                new ProductionAdmissionGateResult(gate, ProductionAdmissionGateStatus.Passed, "VirtualGateLogicObserved"))
            .ToArray();
        return new(configuration ?? Configuration, inputs ?? Inputs(), gates,
            new Dictionary<string, string> { ["virtual-station-head"] = Hash("virtual-station-generation-1") });
    }

    internal ProductionAdmissionReport Evaluate(ProductionAdmissionFacts? facts = null, DateTimeOffset? now = null,
        long generation = 1, long revision = 1) => ProductionAdmissionEngine.Evaluate(
        Guid.Parse("80df9f89-f9bf-44b2-bdc0-d10ec17ab001"), revision, generation, now ?? Now, facts ?? Facts());

    public void Dispose() => _issuer.Dispose();
}
