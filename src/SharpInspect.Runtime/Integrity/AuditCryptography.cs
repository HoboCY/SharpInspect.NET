using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Integrity;

/// <summary>The small signing surface owned by the Runtime key protector.</summary>
internal interface IAuditSigningKey : IDisposable
{
    string KeyId { get; }
    string PublicKeyBase64 { get; }
    byte[] Sign(byte[] data);
}

/// <summary>
/// Versioned, culture-independent audit bytes.  Each value is encoded as a
/// one-byte null marker; non-null values have a four-byte big-endian UTF-8 byte
/// length followed by strict UTF-8 bytes.  Empty and null values are therefore
/// distinct.  The stream starts with the canonicalization version, the encoded
/// kind, and a field count.
/// </summary>
internal static class AuditCanonical
{
    internal const int CanonicalizationVersion = 1;
    internal const int HashSchemeVersion = 1;
    internal static readonly string GenesisHash = new('0', 64);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Encode(string kind, params string?[] fields)
    {
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("Canonical kind is required.", nameof(kind));
        if (fields is null)
            throw new ArgumentNullException(nameof(fields));
        if (fields.Length > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(fields));

        // 签名依赖规范字节，不能用本地化字符串或普通 JSON 替换；版本、字段顺序及 null/空串区别都属于契约。
        using var stream = new MemoryStream();
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, CanonicalizationVersion);
        stream.Write(integer);
        WriteValue(stream, kind);
        BinaryPrimitives.WriteInt32BigEndian(integer, fields.Length);
        stream.Write(integer);
        foreach (var field in fields)
            WriteValue(stream, field);
        return stream.ToArray();
    }

    /// <summary>
    /// Returns the uppercase SHA-256 chain head for one ordered audit payload.
    /// The version fields, station, sequence, predecessor, and exact payload
    /// bytes are all included in the hash domain.
    /// </summary>
    internal static string Hash(string stationId, long sequence, string previousHash, byte[] payload)
    {
        AuditIntegrityPolicy.ValidateIdentifier(stationId, nameof(stationId), required: true);
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!IsHash(previousHash))
            throw new ArgumentException("The predecessor hash must be 64 hexadecimal characters.",
                nameof(previousHash));
        if (payload is null)
            throw new ArgumentNullException(nameof(payload));

        return Convert.ToHexString(SHA256.HashData(Encode(
            "audit-chain-hash",
            CanonicalizationVersion.ToString(CultureInfo.InvariantCulture),
            HashSchemeVersion.ToString(CultureInfo.InvariantCulture),
            stationId,
            sequence.ToString(CultureInfo.InvariantCulture),
            previousHash,
            Convert.ToBase64String(payload))));
    }

    internal static bool IsHash(string? value) => value is not null && value.Length == 64 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static void WriteValue(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        var bytes = StrictUtf8.GetBytes(value);
        stream.WriteByte(1);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>Creates and verifies signed, self-describing audit checkpoints.</summary>
internal static class AuditCheckpointCrypto
{
    private const int P256SignatureLength = 64;

    internal static AuditCheckpoint Create(AuditIntegrityPolicy policy, IAuditSigningKey signingKey,
        long sequence, string headHash, DateTimeOffset auditTime)
    {
        if (policy is null)
            throw new ArgumentNullException(nameof(policy));
        if (signingKey is null)
            throw new ArgumentNullException(nameof(signingKey));
        policy.Validate();
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!AuditCanonical.IsHash(headHash))
            throw new ArgumentException("The audit head hash must be 64 hexadecimal characters.",
                nameof(headHash));
        if (auditTime == default)
            throw new ArgumentException("The audit time is required.", nameof(auditTime));

        var publicKey = DecodePublicKey(signingKey.PublicKeyBase64);
        var expectedKeyId = Convert.ToHexString(SHA256.HashData(publicKey));
        if (!string.Equals(expectedKeyId, signingKey.KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("AuditSigningKeyIdentityMismatch");

        var unsigned = new AuditCheckpoint(
            Guid.NewGuid(),
            policy.StationId,
            sequence,
            headHash,
            AuditCanonical.CanonicalizationVersion,
            AuditCanonical.HashSchemeVersion,
            policy.Version,
            policy.ContentHash,
            signingKey.KeyId,
            signingKey.PublicKeyBase64,
            auditTime,
            string.Empty);

        var signature = signingKey.Sign(EncodeCheckpoint(unsigned));
        if (signature is null || signature.Length != P256SignatureLength)
            throw new InvalidOperationException("AuditSignatureFormatInvalid");
        return unsigned with { SignatureBase64 = Convert.ToBase64String(signature) };
    }

    internal static bool Verify(AuditCheckpoint checkpoint)
    {
        try
        {
            if (!HasValidUnsignedFields(checkpoint, out var publicKey, out var signature))
                return false;

            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || verifier.KeySize != 256)
                return false;

            var unsigned = checkpoint with { SignatureBase64 = string.Empty };
            return verifier.VerifyData(
                EncodeCheckpoint(unsigned),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Canonical unsigned checkpoint bytes; the signature is excluded.</summary>
    internal static byte[] EncodeCheckpoint(AuditCheckpoint checkpoint) => AuditCanonical.Encode(
        "audit-checkpoint",
        checkpoint.CheckpointId.ToString("D"),
        checkpoint.StationId,
        checkpoint.Sequence.ToString(CultureInfo.InvariantCulture),
        checkpoint.HeadHash,
        checkpoint.CanonicalizationVersion.ToString(CultureInfo.InvariantCulture),
        checkpoint.HashSchemeVersion.ToString(CultureInfo.InvariantCulture),
        checkpoint.PolicyVersion,
        checkpoint.PolicyHash,
        checkpoint.SigningKeyId,
        checkpoint.PublicKeyBase64,
        checkpoint.AuditTime.ToString("O", CultureInfo.InvariantCulture));

    private static bool HasValidUnsignedFields(AuditCheckpoint checkpoint, out byte[] publicKey,
        out byte[] signature)
    {
        publicKey = Array.Empty<byte>();
        signature = Array.Empty<byte>();
        if (checkpoint.CheckpointId == Guid.Empty || checkpoint.Sequence < 0 ||
            checkpoint.CanonicalizationVersion != AuditCanonical.CanonicalizationVersion ||
            checkpoint.HashSchemeVersion != AuditCanonical.HashSchemeVersion ||
            !IsIdentifier(checkpoint.StationId) || !IsIdentifier(checkpoint.PolicyVersion) ||
            !AuditCanonical.IsHash(checkpoint.HeadHash) || !AuditCanonical.IsHash(checkpoint.PolicyHash) ||
            !AuditCanonical.IsHash(checkpoint.SigningKeyId) || checkpoint.AuditTime == default ||
            string.IsNullOrEmpty(checkpoint.PublicKeyBase64) || string.IsNullOrEmpty(checkpoint.SignatureBase64))
            return false;

        try
        {
            publicKey = DecodePublicKey(checkpoint.PublicKeyBase64);
            if (!string.Equals(Convert.ToBase64String(publicKey), checkpoint.PublicKeyBase64,
                    StringComparison.Ordinal))
                return false;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(publicKey)), checkpoint.SigningKeyId,
                    StringComparison.Ordinal))
                return false;

            signature = Convert.FromBase64String(checkpoint.SignatureBase64);
            return signature.Length == P256SignatureLength &&
                string.Equals(Convert.ToBase64String(signature), checkpoint.SignatureBase64,
                    StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DecodePublicKey(string? publicKeyBase64)
    {
        if (string.IsNullOrEmpty(publicKeyBase64))
            throw new ArgumentException("The public key is required.", nameof(publicKeyBase64));
        var publicKey = Convert.FromBase64String(publicKeyBase64);
        if (publicKey.Length == 0 ||
            !string.Equals(Convert.ToBase64String(publicKey), publicKeyBase64, StringComparison.Ordinal))
            throw new InvalidOperationException("AuditPublicKeyFormatInvalid");

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
        if (bytesRead != publicKey.Length || key.KeySize != 256)
            throw new InvalidOperationException("AuditPublicKeyFormatInvalid");
        return publicKey;
    }

    private static bool IsIdentifier(string? value) => value is not null && value.Length is > 0 and <= 128 &&
        value.Trim() == value && value.All(static character => !char.IsControl(character));
}
