using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Virtual;

/// <summary>
/// An immutable, bounded image owned by the deterministic virtual camera.
/// The backing bytes are top-down and include canonical zero row padding. This
/// type contains image data only; it does not retain a source path or a clock.
/// </summary>
public sealed class VirtualCameraImage
{
    public const int MaximumLayoutBytes = 16 * 1024 * 1024;

    private const string ContentHashVersion = "SharpInspect.VirtualCameraImage.Content.v1";
    private const string PixelHashVersion = "SharpInspect.VirtualCameraImage.Pixels.v1";
    private const string SyntheticPrngVersion = "xorshift32-v1";

    // Fixed non-zero domain separator for the deterministic xorshift32 stream.
    private const uint SyntheticSeedDomain = 0xA341316Cu;

    private readonly byte[] _pixels;
    private readonly int _validRowBytes;

    public VirtualCameraImage(string id, int width, int height, int strideBytes,
        VisionPixelFormat pixelFormat, int? validBits, ReadOnlySpan<byte> pixels)
        : this(id, width, height, strideBytes, pixelFormat, validBits, pixels, null)
    {
    }

    private VirtualCameraImage(string id, int width, int height, int strideBytes,
        VisionPixelFormat pixelFormat, int? validBits, ReadOnlySpan<byte> pixels,
        string? sourceDataHash)
    {
        Id = ValidateIdentifier(id);
        var layout = ValidateLayout(width, height, strideBytes, pixelFormat, validBits);
        if (pixels.Length < layout.RequiredBufferLength)
            throw new ArgumentException("VirtualCameraPixelBufferTooShort", nameof(pixels));

        ValidateMono16Pixels(layout, pixels);

        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
        ValidBits = validBits;
        _validRowBytes = layout.ValidRowBytes;
        _pixels = new byte[layout.LayoutBytes];
        for (var row = 0; row < height; row++)
        {
            pixels.Slice(row * strideBytes, _validRowBytes)
                .CopyTo(_pixels.AsSpan(row * strideBytes, _validRowBytes));
        }

        SourceDataHash = sourceDataHash;
        PixelDataHash = ComputePixelDataHash();
        ContentHash = ComputeContentHash();
    }

    public string Id { get; }
    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
    public VisionPixelFormat PixelFormat { get; }
    public int? ValidBits { get; }
    public string ContentHash { get; }
    public string PixelDataHash { get; }

    /// <summary>
    /// The SHA-256 of the explicitly supplied recorded raw file, when this image
    /// was loaded from a file. It is not an absolute path and is not persisted by
    /// the image's content hash.
    /// </summary>
    public string? SourceDataHash { get; }

    /// <summary>
    /// Full canonical backing bytes, including zero padding. This is intentionally
    /// internal so a provider can copy from the image without exposing a mutable
    /// backing array to consumers.
    /// </summary>
    internal ReadOnlySpan<byte> PixelBytes => _pixels;

    /// <summary>Returns one valid row, excluding its canonical padding.</summary>
    public ReadOnlySpan<byte> GetRowSpan(int row)
    {
        if ((uint)row >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(row));
        return _pixels.AsSpan(row * StrideBytes, _validRowBytes);
    }

    /// <summary>Returns a defensive copy of the full canonical layout.</summary>
    public byte[] CopyPixels() => (byte[])_pixels.Clone();

    /// <summary>
    /// Creates deterministic bytes using the fixed xorshift32-v1 generator. The
    /// generator has no wall-clock or process-global state.
    /// </summary>
    public static VirtualCameraImage CreateSynthetic(string id, int width, int height,
        VisionPixelFormat pixelFormat, int? validBits, uint seed, int rowPaddingBytes = 0)
    {
        if (rowPaddingBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(rowPaddingBytes));

        var bytesPerPixel = BytesPerPixel(pixelFormat, nameof(pixelFormat));
        var validRowBytes = checked(width * bytesPerPixel);
        var strideLong = (long)validRowBytes + rowPaddingBytes;
        if (strideLong > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rowPaddingBytes));

        var strideBytes = (int)strideLong;
        var layout = ValidateLayout(width, height, strideBytes, pixelFormat, validBits);
        var generated = new byte[layout.LayoutBytes];
        var state = seed ^ SyntheticSeedDomain;
        if (state == 0)
            state = 1;

        for (var row = 0; row < height; row++)
        {
            var destination = generated.AsSpan(row * strideBytes, validRowBytes);
            if (pixelFormat == VisionPixelFormat.Mono16)
            {
                var maximum = (1u << validBits!.Value) - 1u;
                for (var offset = 0; offset < destination.Length; offset += 2)
                {
                    var value = (uint)NextByte(ref state) | ((uint)NextByte(ref state) << 8);
                    value &= maximum;
                    destination[offset] = (byte)value;
                    destination[offset + 1] = (byte)(value >> 8);
                }
            }
            else
            {
                for (var offset = 0; offset < destination.Length; offset++)
                    destination[offset] = NextByte(ref state);
            }
        }

        // SyntheticPrngVersion is deliberately fixed; the generated bytes and
        // resulting content hash are the evidence emitted for each image.
        return new VirtualCameraImage(id, width, height, strideBytes, pixelFormat,
            validBits, generated);
    }

    /// <summary>
    /// Loads one explicitly described raw file. The file must cover every valid
    /// row byte and may omit only the final row's padding; the resulting image
    /// always owns the full canonical layout. Its expected SHA-256 must match
    /// before the image is normalized. File-system failures are deliberately
    /// mapped to stable, path-free reasons.
    /// </summary>
    public static VirtualCameraImage LoadRecordedRaw(string id, string path, int width,
        int height, int strideBytes, VisionPixelFormat pixelFormat, int? validBits,
        string expectedSha256)
    {
        if (path is null)
            throw new ArgumentNullException(nameof(path));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("VirtualCameraRecordedPathMustBeAbsolute", nameof(path));

        var expected = ParseExpectedSha256(expectedSha256);
        var layout = ValidateLayout(width, height, strideBytes, pixelFormat, validBits);

        long fileLength;
        try
        {
            fileLength = new FileInfo(path).Length;
        }
        catch
        {
            throw new InvalidDataException("VirtualCameraRecordedReadFailed");
        }

        if (fileLength < layout.RequiredBufferLength || fileLength > layout.LayoutBytes)
            throw new InvalidDataException("VirtualCameraRecordedLengthInvalid");

        byte[] bytes;
        try
        {
            // Allocate only after opening the file and rechecking its bounded
            // declared length. Read into that exact allocation so a concurrent
            // file growth cannot make File.ReadAllBytes allocate an unbounded
            // buffer between the length check and the read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length != fileLength)
                throw new InvalidDataException("VirtualCameraRecordedLengthInvalid");

            bytes = new byte[checked((int)fileLength)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0)
                    throw new InvalidDataException("VirtualCameraRecordedLengthInvalid");
                offset += read;
            }

            if (stream.ReadByte() != -1)
                throw new InvalidDataException("VirtualCameraRecordedLengthInvalid");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("VirtualCameraRecordedReadFailed");
        }

        if (bytes.LongLength != fileLength)
            throw new InvalidDataException("VirtualCameraRecordedLengthInvalid");

        var actual = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("VirtualCameraRecordedHashMismatch");

        return new VirtualCameraImage(id, width, height, strideBytes, pixelFormat,
            validBits, bytes, Convert.ToHexString(actual));
    }

    private static byte NextByte(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (byte)(state >> 24);
    }

    private string ComputeContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, ContentHashVersion);
        AppendString(hash, Id);
        AppendInt32(hash, Width);
        AppendInt32(hash, Height);
        AppendInt32(hash, StrideBytes);
        AppendInt32(hash, (int)PixelFormat);
        AppendInt32(hash, ValidBits ?? -1);
        AppendBytes(hash, _pixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private string ComputePixelDataHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, PixelHashVersion);
        AppendInt32(hash, Width);
        AppendInt32(hash, Height);
        AppendInt32(hash, (int)PixelFormat);
        AppendInt32(hash, ValidBits ?? -1);
        AppendInt32(hash, _validRowBytes);
        for (var row = 0; row < Height; row++)
            AppendBytes(hash, _pixels.AsSpan(row * StrideBytes, _validRowBytes));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendBytes(hash, bytes);
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static string ValidateIdentifier(string id)
    {
        if (id is null)
            throw new ArgumentNullException(nameof(id));
        if (id.Length == 0 || id.Length > 64)
            throw new ArgumentException("VirtualCameraImageIdentifierInvalid", nameof(id));

        foreach (var character in id)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException("VirtualCameraImageIdentifierInvalid", nameof(id));
        }

        return id;
    }

    private static Layout ValidateLayout(int width, int height, int strideBytes,
        VisionPixelFormat pixelFormat, int? validBits)
    {
        if (width is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(height));

        var bytesPerPixel = BytesPerPixel(pixelFormat, nameof(pixelFormat));
        var validRowBytes = checked(width * bytesPerPixel);
        if (strideBytes < validRowBytes || strideBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(strideBytes));

        ValidateValidBits(pixelFormat, validBits);
        var layoutBytes = checked((long)strideBytes * height);
        if (layoutBytes > MaximumLayoutBytes)
            throw new ArgumentException("VirtualCameraImageLayoutTooLarge");

        var requiredBufferLength = checked((long)strideBytes * (height - 1) + validRowBytes);
        return new Layout(width, height, strideBytes, pixelFormat, validBits,
            validRowBytes, checked((int)layoutBytes), requiredBufferLength);
    }

    private static int BytesPerPixel(VisionPixelFormat pixelFormat, string parameterName)
    {
        return pixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => throw new ArgumentOutOfRangeException(parameterName)
        };
    }

    private static void ValidateValidBits(VisionPixelFormat pixelFormat, int? validBits)
    {
        if (pixelFormat == VisionPixelFormat.Mono16)
        {
            if (validBits is not 10 and not 12 and not 16)
                throw new ArgumentOutOfRangeException(nameof(validBits));
        }
        else if (validBits.HasValue)
        {
            throw new ArgumentException("VirtualCameraValidBitsAbsentForPixelFormat",
                nameof(validBits));
        }
    }

    private static void ValidateMono16Pixels(Layout layout, ReadOnlySpan<byte> pixels)
    {
        if (layout.PixelFormat != VisionPixelFormat.Mono16)
            return;

        var maximum = (1u << layout.ValidBits!.Value) - 1u;
        for (var row = 0; row < layout.Height; row++)
        {
            var source = pixels.Slice(row * layout.StrideBytes, layout.ValidRowBytes);
            for (var offset = 0; offset < source.Length; offset += 2)
            {
                var value = (uint)(source[offset] | (source[offset + 1] << 8));
                if ((value & ~maximum) != 0)
                    throw new ArgumentException("VirtualCameraMono16HighBitsNonZero", nameof(pixels));
            }
        }
    }

    private static byte[] ParseExpectedSha256(string expectedSha256)
    {
        if (expectedSha256 is null)
            throw new ArgumentNullException(nameof(expectedSha256));
        if (expectedSha256.Length != 64)
            throw new ArgumentException("VirtualCameraExpectedSha256Invalid", nameof(expectedSha256));

        var result = new byte[32];
        for (var i = 0; i < result.Length; i++)
        {
            var high = HexValue(expectedSha256[i * 2]);
            var low = HexValue(expectedSha256[i * 2 + 1]);
            if (high < 0 || low < 0)
                throw new ArgumentException("VirtualCameraExpectedSha256Invalid", nameof(expectedSha256));
            result[i] = (byte)((high << 4) | low);
        }

        return result;
    }

    private static int HexValue(char value)
    {
        return value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => -1
        };
    }

    private readonly record struct Layout(int Width, int Height, int StrideBytes,
        VisionPixelFormat PixelFormat, int? ValidBits, int ValidRowBytes, int LayoutBytes,
        long RequiredBufferLength);
}
