using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Abstractions;

/// <summary>
/// Public, versioned wire contract for Canonical Pixel Content: the exact bytes of one
/// normalized <see cref="VisionFrame"/> that an Evidence Content Hash covers.
/// </summary>
/// <remarks>
/// <para>
/// The digest is uppercase SHA-256 over a fixed 32-byte envelope followed by the valid pixels
/// of every row in ascending row order. Each row contributes exactly
/// width * bytes-per-pixel bytes measured from the row origin, so stride padding and any
/// buffered bytes beyond that prefix are excluded. The encoded PNG, compressor settings, Overlay
/// Sets, Rendered Evidence Previews, correlation, time, provenance, and filesystem metadata are
/// excluded as well: two frames with equal declared width, height, Vision Pixel Format,
/// conditional Valid Bits, and valid pixels hash identically however their buffers are laid out.
/// </para>
/// <para>Envelope layout, fixed at 32 bytes with every multibyte field big-endian:</para>
/// <list type="number">
/// <item><description>bytes 0..7: ASCII magic <c>SIPIXEL1</c>.</description></item>
/// <item><description>bytes 8..11: uint32 envelope version, currently <see cref="HashSchemeVersion"/>.</description></item>
/// <item><description>bytes 12..15: uint32 width.</description></item>
/// <item><description>bytes 16..19: uint32 height.</description></item>
/// <item><description>byte 20: pixel format wire code, Mono8 = 1, Mono16 = 2, Bgr24 = 3; never the .NET enum ordinal.</description></item>
/// <item><description>byte 21: Valid Bits present flag, 1 when present and 0 otherwise.</description></item>
/// <item><description>byte 22: Valid Bits value, 10, 12, or 16 when present and 0 otherwise.</description></item>
/// <item><description>byte 23: reserved, always zero.</description></item>
/// <item><description>bytes 24..31: uint64 canonical pixel count, width * height * bytes-per-pixel.</description></item>
/// </list>
/// <para>
/// Canonical pixel rules: Mono8 rows hold one gray byte per pixel; Mono16 rows hold unsigned
/// samples that are little-endian and right-aligned, with every bit above the declared Valid Bits
/// zero; Bgr24 rows hold B, G, R byte triples in memory order. Width and height are 1..32768, and
/// Valid Bits is exactly 10, 12, or 16 for Mono16 and absent for the other formats.
/// </para>
/// <para>
/// The envelope is descriptive bytes only. It confers no staging, publication, or commit
/// authority, and this type performs no file or network input/output.
/// </para>
/// </remarks>
public static class CanonicalImagePixelContent
{
    /// <summary>Stable hash-scheme identity a reader records beside a digest.</summary>
    public const string HashScheme = "SharpInspect.CanonicalImagePixels";

    /// <summary>Version of the envelope and canonical pixel rules bound into a digest.</summary>
    public const int HashSchemeVersion = 1;

    private const string Magic = "SIPIXEL1";
    private const int EnvelopeLength = 32;
    private const int MinimumDimension = 1;
    private const int MaximumDimension = 32_768;
    private const int Mono16CancellationInterval = 4_096;

    /// <summary>
    /// Builds the 32-byte envelope that precedes Canonical Pixel Content in a hash input.
    /// </summary>
    /// <param name="width">Frame width in pixels, 1..32768.</param>
    /// <param name="height">Frame height in pixels, 1..32768.</param>
    /// <param name="pixelFormat">Declared Vision Pixel Format.</param>
    /// <param name="validBits">10, 12, or 16 for Mono16; null for the other formats.</param>
    /// <returns>A new 32-byte envelope in the documented wire layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Width, height, pixel format, or Valid Bits is outside the contract.
    /// </exception>
    /// <exception cref="ArgumentException">Valid Bits is present for a format that cannot carry it.</exception>
    public static byte[] CreateEnvelope(int width, int height, VisionPixelFormat pixelFormat,
        int? validBits)
    {
        var bytesPerPixel = Validate(width, height, pixelFormat, validBits);
        var envelope = new byte[EnvelopeLength];
        _ = Encoding.ASCII.GetBytes(Magic, envelope);
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(8, 4), (uint)HashSchemeVersion);
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(12, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(16, 4), (uint)height);
        envelope[20] = FormatCode(pixelFormat);
        envelope[21] = validBits is null ? (byte)0 : (byte)1;
        envelope[22] = validBits is null ? (byte)0 : (byte)validBits.Value;
        envelope[23] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(envelope.AsSpan(24, 8),
            (ulong)width * (ulong)height * (ulong)bytesPerPixel);
        return envelope;
    }

    /// <summary>
    /// Computes the uppercase SHA-256 Evidence Content Hash of the Canonical Pixel Content
    /// borrowed from <paramref name="frame"/>.
    /// </summary>
    /// <remarks>
    /// The hash input is the 32-byte envelope followed by the valid pixels of each row in
    /// ascending row order. Only the first width * bytes-per-pixel bytes of a row are covered, so
    /// a row may expose stride padding or any other buffered bytes after that prefix. Metadata is
    /// read once as a frozen reference, the loan is re-checked before every row, and no whole-image
    /// copy is made.
    /// </remarks>
    /// <param name="frame">Active loan of the normalized frame to hash.</param>
    /// <param name="cancellationToken">
    /// Checked before the first row, before every row, periodically inside large Mono16 rows, and
    /// once after the last row.
    /// </param>
    /// <returns>The uppercase SHA-256 digest of the envelope followed by the valid pixels.</returns>
    /// <exception cref="ArgumentNullException">The frame is null.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    /// <exception cref="InvalidOperationException">
    /// The loan is inactive, a row is shorter than width * bytes-per-pixel, or a Mono16 sample
    /// uses bits above the declared Valid Bits.
    /// </exception>
    public static string ComputeHash(VisionFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = frame.Metadata;
        var envelope = CreateEnvelope(metadata.Width, metadata.Height, metadata.PixelFormat,
            metadata.ValidBits);
        var rowBytes = checked(metadata.Width * BytesPerPixel(metadata.PixelFormat));
        var maximumSample = metadata.PixelFormat == VisionPixelFormat.Mono16
            ? (uint)((1 << metadata.ValidBits!.Value) - 1)
            : 0u;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(envelope);
        for (var row = 0; row < metadata.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!frame.IsLoanActive)
                throw new InvalidOperationException("CanonicalImagePixelLoanInactive");
            var source = frame.GetRowSpan(row);
            if (source.Length < rowBytes)
                throw new InvalidOperationException("CanonicalImagePixelRowCoverageInvalid");
            var valid = source[..rowBytes];
            if (maximumSample != 0)
                RequireCanonicalMono16Samples(valid, maximumSample, cancellationToken);
            hash.AppendData(valid);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static int Validate(int width, int height, VisionPixelFormat pixelFormat, int? validBits)
    {
        if (width < MinimumDimension || width > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height < MinimumDimension || height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(height));
        var bytesPerPixel = BytesPerPixel(pixelFormat);
        if (pixelFormat == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(nameof(validBits));
        }
        else if (validBits is not null)
        {
            throw new ArgumentException("CanonicalImagePixelValidBitsAbsentForPixelFormat",
                nameof(validBits));
        }

        return bytesPerPixel;
    }

    private static int BytesPerPixel(VisionPixelFormat pixelFormat) => pixelFormat switch
    {
        VisionPixelFormat.Mono8 => 1,
        VisionPixelFormat.Mono16 => 2,
        VisionPixelFormat.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
    };

    private static byte FormatCode(VisionPixelFormat pixelFormat) => pixelFormat switch
    {
        VisionPixelFormat.Mono8 => (byte)1,
        VisionPixelFormat.Mono16 => (byte)2,
        VisionPixelFormat.Bgr24 => (byte)3,
        _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat))
    };

    private static void RequireCanonicalMono16Samples(ReadOnlySpan<byte> row, uint maximumSample,
        CancellationToken cancellationToken)
    {
        for (var sample = 0; sample < row.Length / sizeof(ushort); sample++)
        {
            if ((sample & (Mono16CancellationInterval - 1)) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var offset = sample * sizeof(ushort);
            if (BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(offset, sizeof(ushort))) > maximumSample)
                throw new InvalidOperationException("CanonicalImagePixelMono16HighBitsInvalid");
        }
    }
}
