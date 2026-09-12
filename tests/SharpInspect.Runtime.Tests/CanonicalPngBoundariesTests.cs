using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CanonicalPngBoundariesTests
{
    private static readonly PngCodecLimits Limits = new(4 * 1024 * 1024, 8 * 1024 * 1024, 4 * 1024 * 1024);
    private static readonly byte[] Pixels = { 1, 2, 4, 9, 10, 12 };

    [Theory]
    [Trait("VerificationId", "V151_C20")]
    [InlineData(0, new byte[] { 9, 10, 12 })]
    [InlineData(1, new byte[] { 9, 1, 2 })]
    [InlineData(2, new byte[] { 8, 8, 8 })]
    [InlineData(3, new byte[] { 9, 5, 5 })]
    [InlineData(4, new byte[] { 8, 1, 2 })]
    public void V151_C20_HandCalculatedFiltersAndByteSizedIdatBoundariesPreservePixels(int filter, byte[] filtered)
    {
        var raw = new byte[] { 0, 1, 2, 4, (byte)filter }.Concat(filtered).ToArray();
        var compressed = Compress(raw);
        using var png = Fixture(compressed.Select(b => new byte[] { b }), ancillary: true);
        var result = CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline());
        Assert.Equal(Descriptor().CanonicalPixelHash, result.CanonicalPixelHash);
        Assert.Equal(png.Length, result.EncodedByteLength);
        Assert.True(png.CanRead);
    }

    [Theory]
    [Trait("VerificationId", "V151_C21")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void V151_C21_TruncatedZlibWithValidChunkCrcAndIendNeverVerifies(int removedBytes)
    {
        var compressed = Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12 });
        using var png = Fixture(new[] { compressed[..^removedBytes] });
        Assert.ThrowsAny<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline()));
    }

    [Fact]
    [Trait("VerificationId", "V151_C22")]
    public void V151_C22_InvalidZlibChecksumCannotHideBehindValidPngCrc()
    {
        var compressed = Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12 });
        compressed[^1] ^= 1;
        using var png = Fixture(new[] { compressed });
        Assert.ThrowsAny<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline()));
    }

    [Fact]
    [Trait("VerificationId", "V151_C23")]
    public void V151_C23_ExtraDecodedPixelsRejectedEvenIfExpectedPrefixMatches()
    {
        using var png = Fixture(new[] { Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12, 0 }) });
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline()));
    }

    [Theory]
    [Trait("VerificationId", "V151_C24")]
    [InlineData("tRNS")]
    [InlineData("acTL")]
    [InlineData("fcTL")]
    [InlineData("fdAT")]
    [InlineData("ABCD")]
    public void V151_C24_TransparencyAnimationAndUnknownCriticalChunksRejected(string kind)
    {
        using var png = Fixture(new[] { Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12 }) }, forbidden: kind);
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline()));
    }

    [Fact]
    [Trait("VerificationId", "V151_C25")]
    public void V151_C25_EncodedChunkAndWorkingCapsFailClosed()
    {
        using var stage = Stage();
        using var destination = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Encode(stage, destination, Descriptor(),
            Limits with { MaximumEncodedBytes = 40 }, Deadline()));
        Assert.InRange(destination.Length, 0, 40);
        using var png = Fixture(Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12 }).Select(b => new byte[] { b }));
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(),
            Limits with { MaximumChunks = 3 }, Deadline()));
        png.Position = 0;
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(),
            Limits with { MaximumWorkingBytes = 1024 }, Deadline()));
        Assert.Equal(0, png.Position);
    }

    [Fact]
    [Trait("VerificationId", "V151_C26")]
    public void V151_C26_PngTrailingBytesAreRejectedButIdatUnusedBytesDoNotChangePixels()
    {
        var compressed = Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12 });
        using var png = Fixture(new[] { compressed.Concat(new byte[] { 201, 202, 203 }).ToArray() });
        Assert.Equal(Descriptor().CanonicalPixelHash,
            CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline()).CanonicalPixelHash);
        png.Position = png.Length;
        png.WriteByte(0);
        png.Position = 0;
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Verify(png, Descriptor(), Limits, Deadline()));
    }

    [Fact]
    [Trait("VerificationId", "V151_C27")]
    public void V151_C27_MultipleIdatOutputUsesBoundedChunksAndExactDescriptorHash()
    {
        var pixels = new byte[32768 * 3];
        new Random(51203).NextBytes(pixels);
        using var stage = Stage(32768, 3, pixels);
        var descriptor = new CanonicalPngDescriptor(32768, 3, VisionPixelFormat.Mono8, null,
            Convert.ToHexString(SHA256.HashData(stage.ToArray())));
        using var png = new MemoryStream();
        var length = CanonicalPngCodec.Encode(stage, png, descriptor, Limits, Deadline());
        Assert.Equal(length, png.Length);
        var chunks = ReadChunks(png.ToArray());
        Assert.True(chunks.Count(c => c.Type == "IDAT") > 1);
        Assert.All(chunks.Where(c => c.Type == "IDAT"), c => Assert.InRange(c.Length, 1, 65536));
        png.Position = 0;
        Assert.Equal(descriptor.CanonicalPixelHash,
            CanonicalPngCodec.Verify(png, descriptor, Limits, Deadline()).CanonicalPixelHash);
    }

    [Fact]
    [Trait("VerificationId", "V151_C28")]
    public void V151_C28_HostileChunkLengthRejectedWithoutAllocatingFromIt()
    {
        using var png = Fixture(new[] { Compress(new byte[] { 0, 1, 2, 4, 0, 9, 10, 12 }) });
        var bytes = png.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33), uint.MaxValue);
        using var hostile = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Verify(hostile, Descriptor(), Limits, Deadline()));
    }

    [Fact]
    [Trait("VerificationId", "V151_C29")]
    public void V151_C29_EncodingHonorsChunkCapAndRequiresCallerStreamOrigin()
    {
        var pixels = new byte[32768 * 3];
        new Random(51203).NextBytes(pixels);
        using var stage = Stage(32768, 3, pixels);
        var descriptor = new CanonicalPngDescriptor(32768, 3, VisionPixelFormat.Mono8, null,
            Convert.ToHexString(SHA256.HashData(stage.ToArray())));
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Encode(stage, output, descriptor,
            Limits with { MaximumChunks = 3 }, Deadline()));
        using var fresh = new MemoryStream();
        stage.Position = 1;
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Encode(stage, fresh, descriptor, Limits, Deadline()));
        Assert.Equal(0, fresh.Length);
        stage.Position = 0;
        fresh.WriteByte(0);
        fresh.Position = 0;
        Assert.Throws<InvalidDataException>(() => CanonicalPngCodec.Encode(stage, fresh, descriptor, Limits, Deadline()));
    }

    private static StoreDeadline Deadline() => new(TimeSpan.FromSeconds(10));
    private static CanonicalPngDescriptor Descriptor()
    {
        using var stage = Stage();
        return new(3, 2, VisionPixelFormat.Mono8, null, Convert.ToHexString(SHA256.HashData(stage.ToArray())));
    }
    private static MemoryStream Stage() => Stage(3, 2, Pixels);
    private static MemoryStream Stage(int width, int height, byte[] pixels)
    {
        var stream = new MemoryStream();
        stream.Write(CanonicalImagePixelContent.CreateEnvelope(width, height, VisionPixelFormat.Mono8, null));
        stream.Write(pixels);
        stream.Position = 0;
        return stream;
    }
    private static byte[] Compress(byte[] raw)
    {
        using var stream = new MemoryStream();
        using (var zlib = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        return stream.ToArray();
    }
    private static MemoryStream Fixture(IEnumerable<byte[]> idats, bool ancillary = false, string? forbidden = null)
    {
        var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk(stream, "IHDR", new byte[] { 0, 0, 0, 3, 0, 0, 0, 2, 8, 0, 0, 0, 0 });
        if (ancillary) Chunk(stream, "tEXt", Encoding.ASCII.GetBytes("fixture\0independent filters"));
        if (forbidden is not null) Chunk(stream, forbidden, Array.Empty<byte>());
        foreach (var bytes in idats) Chunk(stream, "IDAT", bytes);
        if (ancillary) Chunk(stream, "tEXt", Encoding.ASCII.GetBytes("after\0idat"));
        Chunk(stream, "IEND", Array.Empty<byte>());
        stream.Position = 0;
        return stream;
    }
    private static void Chunk(Stream output, string type, byte[] bytes)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)bytes.Length);
        output.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (var value in typeBytes.Concat(bytes))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }
    private static IReadOnlyList<(string Type, int Length)> ReadChunks(byte[] bytes)
    {
        var result = new List<(string, int)>();
        for (var offset = 8; offset < bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
            result.Add((Encoding.ASCII.GetString(bytes, offset + 4, 4), length));
            offset = checked(offset + 12 + length);
        }
        return result;
    }
}
