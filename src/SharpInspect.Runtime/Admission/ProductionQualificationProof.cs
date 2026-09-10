using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Admission;

internal enum ProductionQualificationLayer { Framework, Provider, ProviderHardware, Performance, StationAcceptance, PowerLoss }

internal sealed record ProductionQualificationCheck(string VerificationId, ConformanceOutcome Outcome,
    bool Mandatory, bool Applicable, string? ExclusionProofHash = null);

/// <summary>Untrusted data until the fixed verifier validates it against independent authority.</summary>
internal sealed class ProductionQualificationProof
{
    internal ProductionQualificationProof(string recordId, ProductionQualificationLayer layer,
        string issuerId, string purpose, string targetFingerprint, string profileHash, string scopeHash,
        string contextHash, DateTimeOffset issuedAtUtc, DateTimeOffset? expiresAtUtc,
        IReadOnlyList<ProductionQualificationCheck> checks, IReadOnlyList<string> rawEvidenceHashes,
        IReadOnlyDictionary<ProductionQualificationLayer, string> references,
        string? approvingPrincipalId, string? approvalEvidenceHash, bool candidateHasProductFailure,
        byte[] signature)
    {
        RecordId = ProductionAdmissionCanonical.RequireId(recordId);
        if (!Enum.IsDefined(layer)) throw new ArgumentException("ProductionQualificationLayerInvalid");
        Layer = layer;
        IssuerId = ProductionAdmissionCanonical.RequireId(issuerId);
        Purpose = ProductionAdmissionCanonical.RequireId(purpose);
        TargetFingerprint = ProductionAdmissionCanonical.RequireHash(targetFingerprint);
        ProfileHash = ProductionAdmissionCanonical.RequireHash(profileHash);
        ScopeHash = ProductionAdmissionCanonical.RequireHash(scopeHash);
        ContextHash = ProductionAdmissionCanonical.RequireHash(contextHash);
        if (issuedAtUtc == default || expiresAtUtc is { } expiry && expiry == default)
            throw new ArgumentException("ProductionQualificationTimestampMissing");
        IssuedAtUtc = issuedAtUtc.ToUniversalTime();
        ExpiresAtUtc = expiresAtUtc?.ToUniversalTime();
        if (checks is null || checks.Count is < 1 or > 256 || rawEvidenceHashes is null ||
            rawEvidenceHashes.Count is < 1 or > 256 || references is null || references.Count > 6 ||
            signature is null || signature.Length > 128)
            throw new ArgumentException("ProductionQualificationCapacityInvalid");
        var copiedChecks = checks.Select(check =>
        {
            if (check is null || !Enum.IsDefined(check.Outcome)) throw new ArgumentException("ProductionQualificationCheckInvalid");
            ProductionAdmissionCanonical.RequireId(check.VerificationId);
            if (check.ExclusionProofHash is not null) ProductionAdmissionCanonical.RequireHash(check.ExclusionProofHash);
            return check;
        }).OrderBy(check => check.VerificationId, StringComparer.Ordinal).ToArray();
        if (copiedChecks.Select(check => check.VerificationId).Distinct(StringComparer.Ordinal).Count() != copiedChecks.Length)
            throw new ArgumentException("ProductionQualificationDuplicateCheck");
        Checks = Array.AsReadOnly(copiedChecks);
        RawEvidenceHashes = Array.AsReadOnly(rawEvidenceHashes.Select(ProductionAdmissionCanonical.RequireHash)
            .Distinct(StringComparer.Ordinal).OrderBy(hash => hash, StringComparer.Ordinal).ToArray());
        var copiedReferences = new SortedDictionary<ProductionQualificationLayer, string>();
        foreach (var pair in references)
        {
            if (!Enum.IsDefined(pair.Key) || pair.Key == layer) throw new ArgumentException("ProductionQualificationReferenceInvalid");
            copiedReferences.Add(pair.Key, ProductionAdmissionCanonical.RequireHash(pair.Value));
        }
        References = new ReadOnlyDictionary<ProductionQualificationLayer, string>(copiedReferences);
        if (approvingPrincipalId is not null) ProductionAdmissionCanonical.RequireId(approvingPrincipalId);
        if (approvalEvidenceHash is not null) ProductionAdmissionCanonical.RequireHash(approvalEvidenceHash);
        ApprovingPrincipalId = approvingPrincipalId;
        ApprovalEvidenceHash = approvalEvidenceHash;
        CandidateHasProductFailure = candidateHasProductFailure;
        _signature = signature.ToArray();
        ContentHash = Convert.ToHexString(SHA256.HashData(Encode()));
    }

    private readonly byte[] _signature;
    internal string RecordId { get; }
    internal ProductionQualificationLayer Layer { get; }
    internal string IssuerId { get; }
    internal string Purpose { get; }
    internal string TargetFingerprint { get; }
    internal string ProfileHash { get; }
    internal string ScopeHash { get; }
    internal string ContextHash { get; }
    internal DateTimeOffset IssuedAtUtc { get; }
    internal DateTimeOffset? ExpiresAtUtc { get; }
    internal IReadOnlyList<ProductionQualificationCheck> Checks { get; }
    internal IReadOnlyList<string> RawEvidenceHashes { get; }
    internal IReadOnlyDictionary<ProductionQualificationLayer, string> References { get; }
    internal string? ApprovingPrincipalId { get; }
    internal string? ApprovalEvidenceHash { get; }
    internal bool CandidateHasProductFailure { get; }
    internal string ContentHash { get; }
    internal byte[] Signature => _signature.ToArray();

    internal byte[] Encode()
    {
        var values = new List<string?> { RecordId, ((int)Layer).ToString(CultureInfo.InvariantCulture), IssuerId,
            Purpose, TargetFingerprint, ProfileHash, ScopeHash, ContextHash,
            IssuedAtUtc.ToString("O", CultureInfo.InvariantCulture), ExpiresAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            ApprovingPrincipalId, ApprovalEvidenceHash, CandidateHasProductFailure ? "1" : "0",
            Checks.Count.ToString(CultureInfo.InvariantCulture) };
        foreach (var check in Checks)
            values.AddRange(new[] { check.VerificationId, ((int)check.Outcome).ToString(CultureInfo.InvariantCulture),
                check.Mandatory ? "1" : "0", check.Applicable ? "1" : "0", check.ExclusionProofHash });
        values.Add(RawEvidenceHashes.Count.ToString(CultureInfo.InvariantCulture));
        values.AddRange(RawEvidenceHashes);
        values.Add(References.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in References) values.AddRange(new[] { ((int)pair.Key).ToString(CultureInfo.InvariantCulture), pair.Value });
        return ProductionAdmissionCanonical.Encode("production-qualification-proof-v1", values.ToArray());
    }
}

internal sealed class ProductionQualificationAuthority
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<ProductionQualificationLayer>> _layers;

    // No default or embedded signing root exists. A later governed deployment trust loader
    // supplies public keys. Test issuers and their keys live exclusively in test assemblies.
    internal static ProductionQualificationAuthority Unconfigured { get; } = new(
        new Dictionary<string, byte[]>(), new Dictionary<string, IReadOnlySet<ProductionQualificationLayer>>());

    internal ProductionQualificationAuthority(IReadOnlyDictionary<string, byte[]> keys,
        IReadOnlyDictionary<string, IReadOnlySet<ProductionQualificationLayer>> layers)
    {
        if (keys is null || layers is null || keys.Count > 32 || keys.Count != layers.Count ||
            keys.Keys.Any(key => !layers.ContainsKey(key))) throw new ArgumentException("ProductionQualificationAuthorityInvalid");
        _keys = new ReadOnlyDictionary<string, byte[]>(keys.ToDictionary(pair =>
            ProductionAdmissionCanonical.RequireId(pair.Key), pair => pair.Value.Length is > 0 and <= 1024
                ? pair.Value.ToArray() : throw new ArgumentException("ProductionQualificationPublicKeyInvalid"), StringComparer.Ordinal));
        _layers = new ReadOnlyDictionary<string, IReadOnlySet<ProductionQualificationLayer>>(layers.ToDictionary(pair => pair.Key,
            pair => (IReadOnlySet<ProductionQualificationLayer>)new HashSet<ProductionQualificationLayer>(pair.Value), StringComparer.Ordinal));
        ContentHash = ProductionAdmissionCanonical.Hash("production-qualification-authority-v1", _keys.Keys
            .OrderBy(key => key, StringComparer.Ordinal).SelectMany(key => new[] { key,
                Convert.ToHexString(SHA256.HashData(_keys[key])), string.Join(",", _layers[key].OrderBy(layer => (int)layer)) }).ToArray());
    }

    internal bool Configured => _keys.Count > 0;
    internal string ContentHash { get; }
    internal bool Verify(ProductionQualificationProof proof)
    {
        if (proof.Purpose != "Production" || !_keys.TryGetValue(proof.IssuerId, out var bytes) ||
            !_layers.TryGetValue(proof.IssuerId, out var layers) || !layers.Contains(proof.Layer)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            return consumed == bytes.Length && key.KeySize == 256 && proof.Signature.Length == 64 &&
                key.VerifyData(proof.Encode(), proof.Signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
