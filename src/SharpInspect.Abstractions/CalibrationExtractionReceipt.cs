using System.Security.Cryptography;

namespace SharpInspect.Abstractions;

/// <summary>
/// Opaque, bounded provenance bytes emitted by a calibration feature extractor.
/// The format contract is retained beside the exact bytes; Runtime does not
/// interpret, decrypt, or authorize this receipt.
/// </summary>
public sealed class CalibrationExtractionReceipt
{
    public const int MaximumBytes = 256;

    private readonly byte[] _canonicalBytes;

    public CalibrationExtractionReceipt(RecipeContractReference format,
        ReadOnlyMemory<byte> canonicalBytes)
    {
        Format = format ?? throw new ArgumentNullException(nameof(format));
        if (canonicalBytes.Length is < 1 or > MaximumBytes)
            throw new ArgumentException("CalibrationExtractionReceiptSizeInvalid", nameof(canonicalBytes));

        _canonicalBytes = canonicalBytes.ToArray();
        CanonicalBytesHash = Convert.ToHexString(SHA256.HashData(_canonicalBytes));
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-calibration-extraction-receipt-v1",
            Format.Id, Format.Version, Format.ContentHash, CanonicalBytesHash
        });
    }

    public RecipeContractReference Format { get; }
    public int Length => _canonicalBytes.Length;

    /// <summary>SHA-256 of the exact opaque receipt bytes, independent of format.</summary>
    public string CanonicalBytesHash { get; }

    /// <summary>SHA-256 identity of the receipt format contract and exact bytes.</summary>
    public string ContentHash { get; }

    /// <summary>Returns a fresh defensive copy of the exact opaque receipt bytes.</summary>
    public byte[] GetBytes() => (byte[])_canonicalBytes.Clone();
}
