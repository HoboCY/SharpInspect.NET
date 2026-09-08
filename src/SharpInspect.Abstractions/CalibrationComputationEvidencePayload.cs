using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>
/// Immutable, bounded evidence bytes emitted alongside a calibration computation.
/// The format contract defines how the bytes are interpreted; the payload identity
/// binds both that contract and the exact canonical bytes.
/// </summary>
public sealed class CalibrationComputationEvidencePayload
{
    public const int MaximumBytes = 64 * 1024;

    private readonly byte[] _canonicalBytes;

    public CalibrationComputationEvidencePayload(RecipeContractReference format,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        Format = format ?? throw new ArgumentNullException(nameof(format));
        if (canonicalBytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("CalibrationComputationEvidencePayloadSizeInvalid",
                nameof(canonicalBytes));

        _canonicalBytes = canonicalBytes.ToArray();
        CanonicalBytesHash = Convert.ToHexString(SHA256.HashData(_canonicalBytes));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-computation-evidence-payload-v1",
            Format.Id, Format.Version, Format.ContentHash, CanonicalBytesHash
        });
    }

    public RecipeContractReference Format { get; }
    public int Length => _canonicalBytes.Length;

    /// <summary>SHA-256 of the exact canonical bytes, independent of their contract.</summary>
    public string CanonicalBytesHash { get; }

    /// <summary>SHA-256 identity of the exact evidence format contract and canonical bytes.</summary>
    public string ContentHash { get; }

    /// <summary>Returns a fresh defensive copy of the exact canonical bytes.</summary>
    public byte[] GetBytes() => (byte[])_canonicalBytes.Clone();
}
