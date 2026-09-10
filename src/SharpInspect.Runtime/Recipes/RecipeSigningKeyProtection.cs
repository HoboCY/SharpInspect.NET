using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>Machine-protected material for the independent Recipe signing domain, never an audit signing key.</summary>
internal sealed record RecipeSigningKeyMaterial(RecipeTrustedSigner Signer, string ProtectedPrivateKeyBase64)
{
    public override string ToString() => "[protected recipe signing key]";
}

internal static class RecipeSigningKeyProtection
{
    internal static RecipeSigningKeyMaterial Generate(CreateRecipeSigningKeyCommand command, string stationId)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("RecipeSigningKeyProtectionUnavailable");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new RecipeTrustedSigner(command.KeyId, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            command.Scope, command.NotBeforeUtc, command.NotAfterUtc);
        var plain = key.ExportPkcs8PrivateKey();
        var entropy = Entropy(stationId, signer);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(plain, entropy, DataProtectionScope.LocalMachine);
            return new(signer, Convert.ToBase64String(protectedBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(entropy);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    internal static byte[] Sign(string protectedPrivateKeyBase64, RecipeTrustedSigner signer, string stationId,
        ReadOnlySpan<byte> signedBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("RecipeSigningKeyProtectionUnavailable");
        if (protectedPrivateKeyBase64.Length is < 1 or > 8192)
            throw new InvalidOperationException("RecipeSigningKeyProtectionInvalid");
        var protectedBytes = Convert.FromBase64String(protectedPrivateKeyBase64);
        var entropy = Entropy(stationId, signer);
        byte[]? plain = null;
        try
        {
            plain = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.LocalMachine);
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(plain, out var consumed);
            if (consumed != plain.Length || Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != signer.PublicKeyBase64)
                throw new InvalidOperationException("RecipeSigningKeyIdentityMismatch");
            return key.SignHash(SHA256.HashData(signedBytes), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static byte[] Entropy(string stationId, RecipeTrustedSigner signer) => SHA256.HashData(Encoding.UTF8.GetBytes(
        "sharpinspect-recipe-signing-key-v1\n" + stationId + "\n" + signer.KeyId + "\n" + signer.PublicKeyFingerprint));
}
