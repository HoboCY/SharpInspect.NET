using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>A public signing identity and exact local import scope. Package bytes cannot provision this trust.</summary>
public sealed record RecipeTrustedSigner
{
    public const string SupportedScheme = "ECDSA-P256-SHA256-P1363";

    public RecipeTrustedSigner(string keyId, string publicKeyBase64, string scope,
        DateTimeOffset notBeforeUtc, DateTimeOffset notAfterUtc)
    {
        KeyId = AlgorithmConfigurationValidation.Identifier(keyId, nameof(keyId));
        Scope = AlgorithmConfigurationValidation.Identifier(scope, nameof(scope));
        if (publicKeyBase64 is null || publicKeyBase64.Length != 124)
            throw new ArgumentException("RecipeTrustPublicKeyInvalid");
        var bytes = Convert.FromBase64String(publicKeyBase64);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(bytes, out var consumed);
        var parameters = verifier.ExportParameters(false);
        if (consumed != bytes.Length || parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
            Convert.ToBase64String(bytes) != publicKeyBase64)
            throw new ArgumentException("RecipeTrustPublicKeyInvalid");
        if (notBeforeUtc.Offset != TimeSpan.Zero || notAfterUtc.Offset != TimeSpan.Zero ||
            notAfterUtc <= notBeforeUtc) throw new ArgumentException("RecipeTrustValidityInvalid");
        PublicKeyBase64 = publicKeyBase64;
        PublicKeyFingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        NotBeforeUtc = notBeforeUtc; NotAfterUtc = notAfterUtc;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-trusted-signer-v1",
            KeyId, PublicKeyFingerprint, Scheme, Scope, NotBeforeUtc.ToString("O", CultureInfo.InvariantCulture),
            NotAfterUtc.ToString("O", CultureInfo.InvariantCulture) });
    }

    public string KeyId { get; }
    public string PublicKeyBase64 { get; }
    public string PublicKeyFingerprint { get; }
    public string Scheme => SupportedScheme;
    public string Scope { get; }
    public DateTimeOffset NotBeforeUtc { get; }
    public DateTimeOffset NotAfterUtc { get; }
    public string ContentHash { get; }
}

/// <summary>One immutable local trust version, attributed to its approving human.</summary>
public sealed class RecipeTrustStoreVersion
{
    internal RecipeTrustStoreVersion(long version, IEnumerable<RecipeTrustedSigner> signers, Guid operationId,
        Guid principalId, Guid sessionId, DateTimeOffset recordedAtUtc)
    {
        Version = version;
        Signers = new ReadOnlyCollection<RecipeTrustedSigner>(signers.OrderBy(item => item.KeyId,
            StringComparer.Ordinal).ToArray());
        OperationId = operationId; PrincipalId = principalId; SessionId = sessionId; RecordedAtUtc = recordedAtUtc;
        ContentHash = AlgorithmContractValidation.HashParts(new[] { "sharpinspect-recipe-trust-store-v1",
            version.ToString(CultureInfo.InvariantCulture), operationId.ToString("D"), principalId.ToString("D"),
            sessionId.ToString("D"), recordedAtUtc.ToString("O", CultureInfo.InvariantCulture) }
            .Concat(Signers.Select(item => item.ContentHash)));
    }
    public long Version { get; }
    public ReadOnlyCollection<RecipeTrustedSigner> Signers { get; }
    public Guid OperationId { get; }
    public Guid PrincipalId { get; }
    public Guid SessionId { get; }
    public DateTimeOffset RecordedAtUtc { get; }
    public string ContentHash { get; }
}

/// <summary>Public key status only; private material is excluded from every public query.</summary>
public sealed record RecipeSigningKeyRecord(string KeyId, RecipeTrustedSigner Signer, bool Retired,
    Guid OperationId, Guid PrincipalId, DateTimeOffset RecordedAtUtc);
