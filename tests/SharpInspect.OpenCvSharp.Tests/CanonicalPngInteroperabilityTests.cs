using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenCvSharp;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Images;
using Xunit;

namespace SharpInspect.OpenCvSharp.Tests;

/// <summary>
/// V151 C-series: the interoperability contract of <c>CanonicalPngCodec</c> with OpenCV/libpng
/// over caller-owned seekable streams.
///
/// Scope: one physical encode or verify per call. These tests make no claim about the T51 PNG
/// finalizer pipeline, Pending-to-Finalized transitions, crash or physical power-loss
/// qualification, retention, or a full ticket-51 acceptance run; they neither require nor
/// replace one. Every expected canonical hash is SHA-256 over stage bytes built here (the
/// 32-byte envelope plus tight valid rows), never through the codec under test, and the OpenCV
/// side is an independent producer and consumer that the codec must interoperate with.
///
/// Contract notes these tests pin:
/// <list type="bullet">
/// <item>refusals are deliberate failures: corrupted, truncated, over-budget or unsupported
/// input, metadata or hash mismatches and Valid Bits violations never return a result;</item>
/// <item>a canceled token raises <see cref="OperationCanceledException"/> and an already expired
/// <see cref="StoreDeadline"/> raises <see cref="TimeoutException"/>, the repository's monotonic
/// deadline convention;</item>
/// <item><c>Encode</c> returns the encoded PNG byte length and <c>Verify</c> reports that same
/// length together with the recomputed canonical hash;</item>
/// <item>Valid Bits are declaration-only: Mono16 samples stay little-endian and right-aligned in
/// the canonical stage, and are unscaled 16-bit big-endian samples in the PNG16 payload.</item>
/// </list>
/// Callers hand over fresh streams positioned at zero with an empty output stream, so that
/// precondition is exercised but never asserted here. OpenCV may encode with any filter strategy
/// (including none), so deterministic filter 0..4 coverage uses hand-built fixtures instead of
/// relying on libpng's adaptive choice.
/// </summary>
public sealed class CanonicalPngInteroperabilityTests
{
    private const int NormalWorkingBytes = 4 * 1024 * 1024;
    private const int BelowFloorWorkingBytes = 1 * 1024 * 1024;
    private const int DefaultMaximumChunks = 65_536;

    private static readonly TimeSpan NormalDeadline = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------- our Encode -> OpenCV

    [Fact]
    [Trait("VerificationId", "V151_C01")]
    public void V151_C01_Mono8EncodeDecodesInOpenCvWithExactPixels()
    {
        const int width = 8;
        const int height = 6;
        var raster = Mono8WildRaster(width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono8, null, raster);
        var expectedHash = Sha256Hex(stage);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null, expectedHash);
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);

        var pngBytes = Encode(stage, descriptor, limits);

        AssertPngSignature(pngBytes);
        AssertIhdr(pngBytes, width, height, bitDepth: 8, colorType: PngFixture.ColorTypeGray,
            interlaced: false);

        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        Assert.Equal(MatType.CV_8UC1, decoded.Type());
        Assert.Equal(raster, ReadRasterFlat(decoded, width, height));

        var verified = Verify(pngBytes, descriptor, limits);
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(16)]
    [Trait("VerificationId", "V151_C02")]
    public void V151_C02_Mono16EncodeWritesNetworkOrderSamplesThatOpenCvDecodesExactly(int validBits)
    {
        const int width = 5;
        const int height = 4;
        var samples = Mono16Samples(width, height, (1 << validBits) - 1);
        var littleEndian = SampleBytes(samples, bigEndian: false);
        var networkOrder = SampleBytes(samples, bigEndian: true);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono16, validBits, littleEndian);
        var expectedHash = Sha256Hex(stage);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono16, validBits, expectedHash);
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono16);

        var pngBytes = Encode(stage, descriptor, limits);

        AssertPngSignature(pngBytes);
        AssertIhdr(pngBytes, width, height, bitDepth: 16, colorType: PngFixture.ColorTypeGray,
            interlaced: false);
        // PNG16 stores samples in network order, so the encoded raster is the byte swap of the
        // canonical little-endian stage rows.
        Assert.Equal(networkOrder, UnfilterRaster(pngBytes, width, height, bitDepth: 16, channels: 1));
        Assert.NotEqual(networkOrder, littleEndian);

        // No scaling: OpenCV returns the raw right-aligned samples in host order.
        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        Assert.Equal(MatType.CV_16UC1, decoded.Type());
        Assert.Equal(littleEndian, ReadRasterFlat(decoded, width * 2, height));

        var verified = Verify(pngBytes, descriptor, limits);
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    [Fact]
    [Trait("VerificationId", "V151_C03")]
    public void V151_C03_Bgr24EncodeDecodesInOpenCvInPngRgbOrderAndBgrMatOrder()
    {
        const int width = 4;
        const int height = 3;
        var bgr = BgrRaster(width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Bgr24, null, bgr);
        var expectedHash = Sha256Hex(stage);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Bgr24, null, expectedHash);
        var limits = LimitsOf(width, height, VisionPixelFormat.Bgr24);

        var pngBytes = Encode(stage, descriptor, limits);

        AssertPngSignature(pngBytes);
        AssertIhdr(pngBytes, width, height, bitDepth: 8, colorType: PngFixture.ColorTypeRgb,
            interlaced: false);
        var rgb = SwapRedAndBlue(bgr);
        Assert.NotEqual(bgr, rgb);
        Assert.Equal(rgb, UnfilterRaster(pngBytes, width, height, bitDepth: 8, channels: 3));

        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        Assert.Equal(MatType.CV_8UC3, decoded.Type());
        Assert.Equal(bgr, ReadRasterFlat(decoded, width * 3, height));

        var verified = Verify(pngBytes, descriptor, limits);
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    [Fact]
    [Trait("VerificationId", "V151_C04")]
    public void V151_C04_PaddedMono8RasterEncodesOnlyTightCanonicalRows()
    {
        const int width = 4;
        const int height = 3;
        const int stride = 8;
        // Every tight pixel stays under 0x80 while the source padding is 0xEE, so any padding
        // that leaked into the PNG would be visible in the OpenCV read-back below.
        var padded = PaddedMono8Raster(width, height, stride, padding: 0xEE);
        var tight = TightRaster(padded, stride, width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono8, null, tight);
        var expectedHash = Sha256Hex(stage);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null, expectedHash);
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);

        var pngBytes = Encode(stage, descriptor, limits);

        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        Assert.Equal(tight, ReadRasterFlat(decoded, width, height));

        var verified = Verify(pngBytes, descriptor, limits);
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    [Fact]
    [Trait("VerificationId", "V151_C05")]
    public void V151_C05_PaddedMono16RasterKeepsRightAlignedLittleEndianSamples()
    {
        const int width = 3;
        const int height = 3;
        const int stride = 8;
        const int validBits = 12;
        var samples = new ushort[] { 0, 4095, 4094, 2048, 7, 1023, 256, 17, 4093 };
        // The two source padding bytes per row are 0xFFFF, above the 12-bit ceiling: a padding
        // leak is unrepresentable in canonical content and would surface in the read-back.
        var padded = PaddedMono16Raster(width, height, stride, SampleBytes(samples, bigEndian: false));
        var tight = TightRaster(padded, stride, width * 2, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono16, validBits, tight);
        var expectedHash = Sha256Hex(stage);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono16, validBits, expectedHash);
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono16);

        var pngBytes = Encode(stage, descriptor, limits);

        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        Assert.Equal(MatType.CV_16UC1, decoded.Type());
        Assert.Equal(tight, ReadRasterFlat(decoded, width * 2, height));

        var verified = Verify(pngBytes, descriptor, limits);
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    // ---------------------------------------------------------------- OpenCV Encode -> our Verify

    [Fact]
    [Trait("VerificationId", "V151_C06")]
    public void V151_C06_OpenCvEncodedMono8PngVerifiesWithIndependentHash()
    {
        const int width = 16;
        const int height = 8;
        // A leading ramp with per-row offsets is the content libpng filters hardest; whether
        // OpenCV asks for adaptive filtering or none, the verified bytes must be the same pixels.
        var raster = Mono8RampRaster(width, height);
        var pngBytes = OpenCvEncodePng(width, height, MatType.CV_8UC1, 1, SplitRows(raster, width, height));
        AssertPngSignature(pngBytes);
        AssertIhdr(pngBytes, width, height, bitDepth: 8, colorType: PngFixture.ColorTypeGray,
            interlaced: false);
        Assert.Equal(raster, UnfilterRaster(pngBytes, width, height, bitDepth: 8, channels: 1));

        var expectedHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster));
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null, expectedHash);
        var verified = Verify(pngBytes, descriptor, LimitsOf(width, height, VisionPixelFormat.Mono8));
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);

        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.Equal(raster, ReadRasterFlat(decoded, width, height));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(16)]
    [Trait("VerificationId", "V151_C07")]
    public void V151_C07_OpenCvEncodedMono16PngVerifiesWithIndependentHash(int validBits)
    {
        const int width = 6;
        const int height = 5;
        var samples = Mono16Samples(width, height, (1 << validBits) - 1);
        var littleEndian = SampleBytes(samples, bigEndian: false);
        var networkOrder = SampleBytes(samples, bigEndian: true);
        var pngBytes = OpenCvEncodePng(width, height, MatType.CV_16UC1, 2,
            SplitRows(littleEndian, width * 2, height));
        AssertPngSignature(pngBytes);
        AssertIhdr(pngBytes, width, height, bitDepth: 16, colorType: PngFixture.ColorTypeGray,
            interlaced: false);
        // The fixture really is network order: unfiltering its IDAT reproduces the byte-swapped
        // stage rows, so a passing verification covers the samples rather than a host-order image.
        Assert.Equal(networkOrder, UnfilterRaster(pngBytes, width, height, bitDepth: 16, channels: 1));

        var expectedHash = Sha256Hex(
            BuildStage(width, height, VisionPixelFormat.Mono16, validBits, littleEndian));
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono16, validBits, expectedHash);
        var verified = Verify(pngBytes, descriptor, LimitsOf(width, height, VisionPixelFormat.Mono16));
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    [Fact]
    [Trait("VerificationId", "V151_C08")]
    public void V151_C08_OpenCvEncodedBgr24PngVerifiesWithIndependentHash()
    {
        const int width = 5;
        const int height = 4;
        var bgr = BgrRaster(width, height);
        var pngBytes = OpenCvEncodePng(width, height, MatType.CV_8UC3, 3,
            SplitRows(bgr, width * 3, height));
        AssertPngSignature(pngBytes);
        AssertIhdr(pngBytes, width, height, bitDepth: 8, colorType: PngFixture.ColorTypeRgb,
            interlaced: false);
        // OpenCV writes RGB triples for a BGR Mat, while the canonical stage keeps B, G, R order.
        Assert.Equal(SwapRedAndBlue(bgr), UnfilterRaster(pngBytes, width, height, bitDepth: 8, channels: 3));

        var expectedHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Bgr24, null, bgr));
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Bgr24, null, expectedHash);
        var verified = Verify(pngBytes, descriptor, LimitsOf(width, height, VisionPixelFormat.Bgr24));
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    // ---------------------------------------------------------------- external fixtures

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [Trait("VerificationId", "V151_C09")]
    public void V151_C09_HandBuiltFixedFilterPngVerifiesWithIndependentHash(int filter)
    {
        const int width = 7;
        const int height = 5;
        var raster = Mono8WildRaster(width, height);
        var pngBytes = PngFixture.WriteGray8(width, height, SplitRows(raster, width, height), filter);
        // Fixture proof: this is a real PNG that libpng decodes to the same pixels, so a refusal
        // or a different hash below can only come from the codec, not from a malformed fixture.
        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        Assert.Equal(raster, ReadRasterFlat(decoded, width, height));

        var expectedHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster));
        var verified = Verify(pngBytes,
            DescriptorOf(width, height, VisionPixelFormat.Mono8, null, expectedHash),
            LimitsOf(width, height, VisionPixelFormat.Mono8));
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
        Assert.Equal((long)pngBytes.Length, verified.EncodedByteLength);
    }

    [Fact]
    [Trait("VerificationId", "V151_C19")]
    public void V151_C19_HandBuiltMixedFilterStreamVerifiesWithIndependentHash()
    {
        const int width = 9;
        const int height = 10;
        var raster = Mono8WildRaster(width, height);
        var rows = SplitRows(raster, width, height);
        var pngBytes = PngFixture.WriteGray8(width, height, rows, filter: 0, rowFilters: MixedFilters(height));
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 0, 1, 2, 3, 4 },
            RowFilters(pngBytes, width, height, bitDepth: 8, channels: 1));
        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.Equal(raster, ReadRasterFlat(decoded, width, height));

        var expectedHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster));
        var verified = Verify(pngBytes,
            DescriptorOf(width, height, VisionPixelFormat.Mono8, null, expectedHash),
            LimitsOf(width, height, VisionPixelFormat.Mono8));
        Assert.Equal(expectedHash, verified.CanonicalPixelHash);
    }

    [Theory]
    [InlineData(Corruption.BadIhdrCrc)]
    [InlineData(Corruption.BadIdatCrc)]
    [InlineData(Corruption.CorruptIdatPayload)]
    [InlineData(Corruption.TruncatedSignature)]
    [InlineData(Corruption.TruncatedInsideIdat)]
    [InlineData(Corruption.TruncatedFooter)]
    [InlineData(Corruption.TruncatedInsideIend)]
    [InlineData(Corruption.MissingIdat)]
    [InlineData(Corruption.MissingIend)]
    [InlineData(Corruption.ExtraScanline)]
    [InlineData(Corruption.ShortScanlines)]
    [Trait("VerificationId", "V151_C10")]
    public void V151_C10_CorruptOrTruncatedPngsAreRejected(Corruption corruption)
    {
        const int width = 4;
        const int height = 3;
        var raster = Mono8WildRaster(width, height);
        var rows = SplitRows(raster, width, height);
        var valid = PngFixture.WriteGray8(width, height, rows, filter: 0);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null,
            Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster)));
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);

        // Control: the unmodified fixture verifies, so every case below isolates one mutation.
        Assert.Equal(descriptor.CanonicalPixelHash,
            Verify(valid, descriptor, limits).CanonicalPixelHash);

        var mutated = corruption switch
        {
            Corruption.MissingIdat => PngFixture.WriteGray8(width, height, rows, filter: 0,
                includeIdat: false),
            Corruption.MissingIend => PngFixture.WriteGray8(width, height, rows, filter: 0,
                includeIend: false),
            Corruption.ExtraScanline => PngFixture.WriteGray8(width, height,
                WithExtraRow(rows, width), filter: 0),
            Corruption.ShortScanlines => PngFixture.WriteGray8(width, height, Rows(rows, height - 1),
                filter: 0),
            _ => Corrupt(valid, corruption)
        };

        AssertVerifyRejected(mutated, descriptor, limits);
    }

    [Theory]
    [InlineData(UnsupportedVariant.Rgba)]
    [InlineData(UnsupportedVariant.GrayAlpha)]
    [InlineData(UnsupportedVariant.Palette)]
    [InlineData(UnsupportedVariant.Interlaced)]
    [InlineData(UnsupportedVariant.Gray4Bit)]
    [Trait("VerificationId", "V151_C11")]
    public void V151_C11_UnsupportedPngVariantsAreRejected(UnsupportedVariant variant)
    {
        const int width = 4;
        const int height = 4;
        var raster = Mono8WildRaster(width, height);
        var pngBytes = variant switch
        {
            UnsupportedVariant.Rgba => PngFixture.WriteRgba(width, height, raster, alpha: 0x80),
            UnsupportedVariant.GrayAlpha => PngFixture.WriteGrayAlpha(width, height, raster, alpha: 0x40),
            UnsupportedVariant.Palette => PngFixture.WritePalette(width, height, raster,
                new byte[] { 0, 0, 0, 255, 255, 255 }),
            UnsupportedVariant.Gray4Bit => PngFixture.WriteGray4(width, height, raster),
            _ => PngFixture.WriteInterlacedGray8(width, height, raster)
        };

        // Fixture proof: libpng decodes every variant, including the Adam7 interlaced one, so the
        // refusal below is about the unsupported feature and not about broken image data.
        using var decoded = Cv2.ImDecode(pngBytes, ImreadModes.Unchanged);
        Assert.False(decoded.Empty());
        Assert.Equal(width, decoded.Cols);
        Assert.Equal(height, decoded.Rows);
        if (variant == UnsupportedVariant.Interlaced)
            Assert.Equal(raster, ReadRasterFlat(decoded, width, height));

        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null,
            Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster)));
        AssertVerifyRejected(pngBytes, descriptor, LimitsOf(width, height, VisionPixelFormat.Mono8));
    }

    [Theory]
    [InlineData(MetadataMismatch.Width)]
    [InlineData(MetadataMismatch.Height)]
    [InlineData(MetadataMismatch.Mono16Format)]
    [InlineData(MetadataMismatch.Bgr24Format)]
    [Trait("VerificationId", "V151_C12")]
    public void V151_C12_DescriptorMetadataMismatchIsRejected(MetadataMismatch mismatch)
    {
        const int width = 6;
        const int height = 4;
        var raster = Mono8WildRaster(width, height);
        var pngBytes = OpenCvEncodePng(width, height, MatType.CV_8UC1, 1, SplitRows(raster, width, height));
        var correct = DescriptorOf(width, height, VisionPixelFormat.Mono8, null,
            Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster)));
        Assert.Equal(correct.CanonicalPixelHash,
            Verify(pngBytes, correct, LimitsOf(width, height, VisionPixelFormat.Mono8)).CanonicalPixelHash);

        var descriptor = mismatch switch
        {
            MetadataMismatch.Width => correct with { Width = width + 1 },
            MetadataMismatch.Height => correct with { Height = height + 1 },
            MetadataMismatch.Mono16Format => new CanonicalPngDescriptor(width, height,
                VisionPixelFormat.Mono16, 16, correct.CanonicalPixelHash),
            _ => new CanonicalPngDescriptor(width, height, VisionPixelFormat.Bgr24, null,
                correct.CanonicalPixelHash)
        };
        AssertVerifyRejected(pngBytes, descriptor,
            LimitsOf(descriptor.Width, descriptor.Height, descriptor.PixelFormat));
    }

    [Fact]
    [Trait("VerificationId", "V151_C13")]
    public void V151_C13_DescriptorHashMismatchIsRejected()
    {
        const int width = 6;
        const int height = 4;
        var raster = Mono8WildRaster(width, height);
        var pngBytes = OpenCvEncodePng(width, height, MatType.CV_8UC1, 1, SplitRows(raster, width, height));
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);
        var correct = DescriptorOf(width, height, VisionPixelFormat.Mono8, null,
            Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, raster)));
        Assert.Equal(correct.CanonicalPixelHash, Verify(pngBytes, correct, limits).CanonicalPixelHash);

        // A single flipped pixel anywhere in the covered rows already changes the hash.
        var changed = (byte[])raster.Clone();
        changed[width + 2] ^= 0x01;
        var wrongHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, changed));
        Assert.NotEqual(correct.CanonicalPixelHash, wrongHash);
        AssertVerifyRejected(pngBytes,
            new CanonicalPngDescriptor(width, height, VisionPixelFormat.Mono8, null, wrongHash), limits);

        // A hash that cannot even be a digest never becomes an accepted verification.
        AssertVerifyRejected(pngBytes,
            new CanonicalPngDescriptor(width, height, VisionPixelFormat.Mono8, null, new string('0', 63)),
            limits);
    }

    [Theory]
    [InlineData(10, 0x03FF, 0x0400)]
    [InlineData(12, 0x0FFF, 0x1000)]
    [Trait("VerificationId", "V151_C14")]
    public void V151_C14_Mono16SampleAboveDeclaredValidBitsIsRejected(
        int validBits, int ceiling, int overflow)
    {
        const int width = 2;
        const int height = 1;
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono16);

        // Boundary sample at the ceiling is canonical and verifies.
        var boundary = new ushort[] { (ushort)ceiling, 0 };
        var boundaryHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono16, validBits,
            SampleBytes(boundary, bigEndian: false)));
        Assert.Equal(boundaryHash, Verify(PngFixture.WriteGray16(width, height, boundary),
            DescriptorOf(width, height, VisionPixelFormat.Mono16, validBits, boundaryHash), limits)
            .CanonicalPixelHash);

        // Same shape and declared Valid Bits, but the descriptor hash already covers the
        // overflowing sample, so only the Valid Bits ceiling can refuse this file.
        var overflowing = new ushort[] { (ushort)overflow, 0 };
        var overflowHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono16, validBits,
            SampleBytes(overflowing, bigEndian: false)));
        AssertVerifyRejected(PngFixture.WriteGray16(width, height, overflowing),
            DescriptorOf(width, height, VisionPixelFormat.Mono16, validBits, overflowHash), limits);
    }

    [Theory]
    [InlineData(BudgetCase.CanonicalBytes)]
    [InlineData(BudgetCase.EncodedBytes)]
    [InlineData(BudgetCase.WorkingBytes)]
    [InlineData(BudgetCase.ChunkCount)]
    [Trait("VerificationId", "V151_C15")]
    public void V151_C15_BudgetCapsRefuseBeforeAnyResult(BudgetCase budgetCase)
    {
        const int width = 8;
        const int height = 4;
        var raster = Mono8WildRaster(width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono8, null, raster);
        var expectedHash = Sha256Hex(stage);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null, expectedHash);
        var normal = LimitsOf(width, height, VisionPixelFormat.Mono8);
        var rows = SplitRows(raster, width, height);
        var pngBytes = Encode(stage, descriptor, normal);
        Assert.Equal(expectedHash, Verify(pngBytes, descriptor, normal).CanonicalPixelHash);

        switch (budgetCase)
        {
            case BudgetCase.CanonicalBytes:
            {
                // One byte less than the 32-byte envelope plus tight rows is already refusable.
                var tight = LimitsOf(width, height, VisionPixelFormat.Mono8,
                    maximumCanonicalBytes: stage.Length - 1);
                AssertEncodeRejected(stage, descriptor, tight);
                AssertVerifyRejected(pngBytes, descriptor, tight);
                break;
            }
            case BudgetCase.EncodedBytes:
            {
                // No PNG can fit in 32 bytes, so both directions refuse instead of truncating.
                var tight = LimitsOf(width, height, VisionPixelFormat.Mono8, maximumEncodedBytes: 32);
                AssertEncodeRejected(stage, descriptor, tight);
                AssertVerifyRejected(pngBytes, descriptor, tight);
                break;
            }
            case BudgetCase.WorkingBytes:
            {
                // Below the conservative native reserve (2 MiB + 64 KiB + two rows).
                var tight = LimitsOf(width, height, VisionPixelFormat.Mono8,
                    maximumWorkingBytes: BelowFloorWorkingBytes);
                AssertEncodeRejected(stage, descriptor, tight);
                AssertVerifyRejected(pngBytes, descriptor, tight);
                break;
            }
            case BudgetCase.ChunkCount:
            {
                // The same image split into IHDR + IDAT + IDAT + IEND: four chunks.
                var split = PngFixture.WriteGray8(width, height, rows, filter: 0, idatChunks: 2);
                Assert.Equal(4, ParseChunks(split).Count);
                Assert.Equal(expectedHash, Verify(split, descriptor, normal).CanonicalPixelHash);
                using (var decoded = Cv2.ImDecode(split, ImreadModes.Unchanged))
                    Assert.Equal(raster, ReadRasterFlat(decoded, width, height));
                AssertVerifyRejected(split, descriptor,
                    LimitsOf(width, height, VisionPixelFormat.Mono8, maximumChunks: 3));
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(budgetCase));
        }
    }

    [Fact]
    [Trait("VerificationId", "V151_C16")]
    public void V151_C16_CanceledTokenStopsEncodeAndVerify()
    {
        const int width = 4;
        const int height = 3;
        var raster = Mono8WildRaster(width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono8, null, raster);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null,
            Sha256Hex(stage));
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);
        var pngBytes = Encode(stage, descriptor, limits);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using (var stageStream = new MemoryStream(stage, writable: false))
        using (var png = new MemoryStream())
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                CanonicalPngCodec.Encode(stageStream, png, descriptor, limits,
                    new StoreDeadline(NormalDeadline), canceled.Token);
            });
        }

        using (var pngStream = new MemoryStream(pngBytes, writable: false))
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
            {
                CanonicalPngCodec.Verify(pngStream, descriptor, limits,
                    new StoreDeadline(NormalDeadline), canceled.Token);
            });
        }

        // Control: the same calls complete with a live token.
        Assert.Equal(descriptor.CanonicalPixelHash, Verify(pngBytes, descriptor, limits).CanonicalPixelHash);
    }

    [Fact]
    [Trait("VerificationId", "V151_C17")]
    public void V151_C17_ExpiredDeadlineStopsEncodeAndVerify()
    {
        const int width = 4;
        const int height = 3;
        var raster = Mono8WildRaster(width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono8, null, raster);
        var descriptor = DescriptorOf(width, height, VisionPixelFormat.Mono8, null,
            Sha256Hex(stage));
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);
        var pngBytes = Encode(stage, descriptor, limits);

        // StoreDeadline is monotonic: a zero budget is already expired when the call arrives.
        using (var stageStream = new MemoryStream(stage, writable: false))
        using (var png = new MemoryStream())
        {
            Assert.ThrowsAny<TimeoutException>(() =>
            {
                CanonicalPngCodec.Encode(stageStream, png, descriptor, limits,
                    new StoreDeadline(TimeSpan.Zero));
            });
        }

        using (var pngStream = new MemoryStream(pngBytes, writable: false))
        {
            Assert.ThrowsAny<TimeoutException>(() =>
            {
                CanonicalPngCodec.Verify(pngStream, descriptor, limits,
                    new StoreDeadline(TimeSpan.Zero));
            });
        }

        // Control: the same calls complete with a live budget.
        Assert.Equal(descriptor.CanonicalPixelHash,
            Verify(pngBytes, descriptor, limits).CanonicalPixelHash);
    }

    [Theory]
    [InlineData(StageContradiction.Hash)]
    [InlineData(StageContradiction.Envelope)]
    [InlineData(StageContradiction.Truncated)]
    [Trait("VerificationId", "V151_C18")]
    public void V151_C18_EncodeRefusesStageThatContradictsDescriptor(StageContradiction contradiction)
    {
        const int width = 4;
        const int height = 3;
        var raster = Mono8WildRaster(width, height);
        var stage = BuildStage(width, height, VisionPixelFormat.Mono8, null, raster);
        var limits = LimitsOf(width, height, VisionPixelFormat.Mono8);
        var correct = DescriptorOf(width, height, VisionPixelFormat.Mono8, null, Sha256Hex(stage));
        Assert.True(Encode(stage, correct, limits).Length > 0);

        switch (contradiction)
        {
            case StageContradiction.Hash:
            {
                var changed = (byte[])raster.Clone();
                changed[0] ^= 0x01;
                var wrongHash = Sha256Hex(BuildStage(width, height, VisionPixelFormat.Mono8, null, changed));
                Assert.NotEqual(correct.CanonicalPixelHash, wrongHash);
                AssertEncodeRejected(stage,
                    new CanonicalPngDescriptor(width, height, VisionPixelFormat.Mono8, null, wrongHash),
                    limits);
                break;
            }
            case StageContradiction.Envelope:
                AssertEncodeRejected(stage, correct with { Height = height + 1 },
                    LimitsOf(width, height + 1, VisionPixelFormat.Mono8));
                break;
            case StageContradiction.Truncated:
                AssertEncodeRejected(Rows(stage, stage.Length - 1), correct, limits);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contradiction));
        }
    }

    // ---------------------------------------------------------------- helpers

    private static CanonicalPngDescriptor DescriptorOf(int width, int height,
        VisionPixelFormat format, int? validBits, string canonicalHash) =>
        new(width, height, format, validBits, canonicalHash);

    private static PngCodecLimits LimitsOf(int width, int height, VisionPixelFormat format,
        long? maximumCanonicalBytes = null, long? maximumEncodedBytes = null,
        int? maximumWorkingBytes = null, int? maximumChunks = null) =>
        new(
            MaximumCanonicalBytes: maximumCanonicalBytes ?? (32L + (RowBytesOf(width, format) * height)),
            MaximumEncodedBytes: maximumEncodedBytes ?? (32L + (RowBytesOf(width, format) * height) + (64 * 1024)),
            MaximumWorkingBytes: maximumWorkingBytes ?? NormalWorkingBytes,
            MaximumChunks: maximumChunks ?? DefaultMaximumChunks);

    /// <summary>
    /// Builds the canonical stage exactly as the stager writes it: the documented 32-byte
    /// envelope followed by the tight valid rows, with no stride padding.
    /// </summary>
    private static byte[] BuildStage(int width, int height, VisionPixelFormat format, int? validBits,
        byte[] raster)
    {
        var rowBytes = RowBytesOf(width, format);
        Assert.Equal(checked(rowBytes * height), raster.Length);
        var envelope = CanonicalImagePixelContent.CreateEnvelope(width, height, format, validBits);
        Assert.Equal(32, envelope.Length);
        var stage = new byte[envelope.Length + raster.Length];
        envelope.CopyTo(stage, 0);
        raster.CopyTo(stage, envelope.Length);
        return stage;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] Encode(byte[] stage, CanonicalPngDescriptor descriptor, PngCodecLimits limits,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        using var stageStream = new MemoryStream(stage, writable: false);
        using var png = new MemoryStream();
        var length = CanonicalPngCodec.Encode(stageStream, png, descriptor, limits,
            new StoreDeadline(timeout ?? NormalDeadline), token);
        Assert.Equal((long)png.Length, length);
        return png.ToArray();
    }

    private static VerifiedCanonicalPng Verify(byte[] pngBytes, CanonicalPngDescriptor descriptor,
        PngCodecLimits limits, TimeSpan? timeout = null, CancellationToken token = default)
    {
        using var png = new MemoryStream(pngBytes, writable: false);
        return CanonicalPngCodec.Verify(png, descriptor, limits,
            new StoreDeadline(timeout ?? NormalDeadline), token);
    }

    private static void AssertEncodeRejected(byte[] stage, CanonicalPngDescriptor descriptor,
        PngCodecLimits limits)
    {
        using var stageStream = new MemoryStream(stage, writable: false);
        using var png = new MemoryStream();
        AssertRejected(() => CanonicalPngCodec.Encode(stageStream, png, descriptor, limits,
            new StoreDeadline(NormalDeadline)));
    }

    private static void AssertVerifyRejected(byte[] pngBytes, CanonicalPngDescriptor descriptor,
        PngCodecLimits limits)
    {
        using var png = new MemoryStream(pngBytes, writable: false);
        AssertRejected(() => CanonicalPngCodec.Verify(png, descriptor, limits,
            new StoreDeadline(NormalDeadline)));
    }

    /// <summary>
    /// Fail-closed evidence: the call must refuse instead of returning a result, and it must
    /// refuse deliberately rather than through a crash (null, bounds or cast failures).
    /// </summary>
    private static void AssertRejected(Action call)
    {
        var exception = Assert.ThrowsAny<Exception>(call);
        Assert.False(
            exception is NullReferenceException or IndexOutOfRangeException or InvalidCastException,
            "The codec must refuse with a deliberate failure, not a crash: " + exception);
    }

    private static void AssertPngSignature(byte[] png)
    {
        Assert.True(png.Length >= 8, "the stream must carry the PNG signature");
        var signature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (var index = 0; index < signature.Length; index++)
            Assert.Equal(signature[index], png[index]);
    }

    private static void AssertIhdr(byte[] png, int width, int height, int bitDepth, int colorType,
        bool interlaced)
    {
        var chunks = ParseChunks(png);
        Assert.True(chunks.Count >= 2, "fixture must contain IHDR and further chunks");
        var ihdr = FindChunk(chunks, "IHDR");
        Assert.Equal(13, ihdr.DataLength);
        var data = png.AsSpan(ihdr.DataStart, ihdr.DataLength);
        Assert.Equal((uint)width, BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
        Assert.Equal((uint)height, BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
        Assert.Equal(bitDepth, (int)data[8]);
        Assert.Equal(colorType, (int)data[9]);
        Assert.Equal(0, (int)data[10]);
        Assert.Equal(0, (int)data[11]);
        Assert.Equal(interlaced ? 1 : 0, (int)data[12]);
    }

    private static List<PngChunk> ParseChunks(byte[] png)
    {
        AssertPngSignature(png);
        var chunks = new List<PngChunk>();
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add(new PngChunk(offset, offset + 8, length, type));
            offset += 12 + length;
            if (type == "IEND") break;
        }

        return chunks;
    }

    private static PngChunk FindChunk(List<PngChunk> chunks, string type)
    {
        foreach (var chunk in chunks)
            if (chunk.Type == type) return chunk;
        throw new InvalidOperationException("Fixture is missing the " + type + " chunk.");
    }

    private static byte[] InflateIdat(byte[] png)
    {
        using var compressed = new MemoryStream();
        foreach (var chunk in ParseChunks(png))
            if (chunk.Type == "IDAT")
                compressed.Write(png, chunk.DataStart, chunk.DataLength);
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        return raw.ToArray();
    }

    /// <summary>
    /// Independent decode of a PNG: inflate the IDAT stream, reverse filters 0..4 and return the
    /// unfiltered raster. Used as fixture evidence against OpenCV and against our own encoder.
    /// </summary>
    private static byte[] UnfilterRaster(byte[] png, int width, int height, int bitDepth, int channels)
    {
        var rowBytes = RowBytesOf(width, bitDepth, channels);
        var pixelBytes = bitDepth / 8 * channels;
        var scanlines = InflateIdat(png);
        Assert.Equal(checked(height * (rowBytes + 1)), scanlines.Length);
        var raster = new byte[checked(height * rowBytes)];
        var previous = new byte[rowBytes];
        for (var row = 0; row < height; row++)
        {
            var filter = scanlines[row * (rowBytes + 1)];
            var source = scanlines.AsSpan((row * (rowBytes + 1)) + 1, rowBytes);
            var target = raster.AsSpan(row * rowBytes, rowBytes);
            for (var index = 0; index < rowBytes; index++)
            {
                var left = index >= pixelBytes ? target[index - pixelBytes] : 0;
                var up = previous[index];
                var upLeft = index >= pixelBytes ? previous[index - pixelBytes] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException("PNG filter " + filter + " is not 0..4.")
                };
                target[index] = (byte)(source[index] + predictor);
            }

            target.CopyTo(previous);
        }

        return raster;
    }

    private static byte[] RowFilters(byte[] png, int width, int height, int bitDepth, int channels)
    {
        var rowBytes = RowBytesOf(width, bitDepth, channels);
        var scanlines = InflateIdat(png);
        Assert.Equal(checked(height * (rowBytes + 1)), scanlines.Length);
        var filters = new byte[height];
        for (var row = 0; row < height; row++)
            filters[row] = scanlines[row * (rowBytes + 1)];
        return filters;
    }

    private static byte[] MixedFilters(int height)
    {
        var filters = new byte[height];
        for (var row = 0; row < height; row++)
            filters[row] = (byte)(row % 5);
        return filters;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upLeftDistance = Math.Abs(estimate - upLeft);
        if (leftDistance <= upDistance && leftDistance <= upLeftDistance) return left;
        return upDistance <= upLeftDistance ? up : upLeft;
    }

    private static byte[] Corrupt(byte[] valid, Corruption corruption)
    {
        var chunks = ParseChunks(valid);
        switch (corruption)
        {
            case Corruption.BadIhdrCrc:
            {
                var damaged = (byte[])valid.Clone();
                var chunk = FindChunk(chunks, "IHDR");
                damaged[chunk.DataStart + chunk.DataLength] ^= 0x01;
                return damaged;
            }
            case Corruption.BadIdatCrc:
            {
                var damaged = (byte[])valid.Clone();
                var chunk = FindChunk(chunks, "IDAT");
                damaged[chunk.DataStart + chunk.DataLength] ^= 0x01;
                return damaged;
            }
            case Corruption.CorruptIdatPayload:
            {
                var damaged = (byte[])valid.Clone();
                var chunk = FindChunk(chunks, "IDAT");
                damaged[chunk.DataStart + (chunk.DataLength / 2)] ^= 0x5A;
                return damaged;
            }
            case Corruption.TruncatedSignature:
                return Rows(valid, 4);
            case Corruption.TruncatedInsideIdat:
            {
                var chunk = FindChunk(chunks, "IDAT");
                return Rows(valid, chunk.DataStart + 3);
            }
            case Corruption.TruncatedFooter:
                return Rows(valid, FindChunk(chunks, "IEND").Start);
            case Corruption.TruncatedInsideIend:
                return Rows(valid, FindChunk(chunks, "IEND").Start + 4);
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
    }

    private static Mat MatFromRows(int width, int height, MatType type, int pixelBytes, byte[][] rows)
    {
        Assert.Equal(height, rows.Length);
        var mat = new Mat(height, width, type);
        try
        {
            for (var row = 0; row < height; row++)
            {
                Assert.Equal(checked(width * pixelBytes), rows[row].Length);
                Marshal.Copy(rows[row], 0, mat.Ptr(row), rows[row].Length);
            }

            return mat;
        }
        catch
        {
            mat.Dispose();
            throw;
        }
    }

    private static byte[] OpenCvEncodePng(int width, int height, MatType type, int pixelBytes,
        byte[][] rows)
    {
        using var mat = MatFromRows(width, height, type, pixelBytes, rows);
        Cv2.ImEncode(".png", mat, out var png);
        Assert.True(png.Length > 0);
        return png;
    }

    private static byte[] ReadRasterFlat(Mat mat, int rowBytes, int height)
    {
        var raster = new byte[checked(rowBytes * height)];
        for (var row = 0; row < height; row++)
            Marshal.Copy(mat.Ptr(row), raster, row * rowBytes, rowBytes);
        return raster;
    }

    private static byte[] Mono8WildRaster(int width, int height)
    {
        var raster = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                raster[(y * width) + x] = (byte)((x * 37) + (y * 91) + (x * y * 11) + 3);
        return raster;
    }

    private static byte[] Mono8RampRaster(int width, int height)
    {
        var raster = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                raster[(y * width) + x] = (byte)((x * 9) + (y * 5));
        return raster;
    }

    private static byte[] BgrRaster(int width, int height)
    {
        var raster = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                raster[offset] = (byte)(17 + (x * 9) + (y * 3));
                raster[offset + 1] = (byte)(203 - (x * 11) - (y * 7));
                raster[offset + 2] = (byte)(97 + (x * 5) + (y * 23));
            }
        }

        return raster;
    }

    private static byte[] SwapRedAndBlue(byte[] bgr)
    {
        Assert.Equal(0, bgr.Length % 3);
        var rgb = (byte[])bgr.Clone();
        for (var offset = 0; offset < rgb.Length; offset += 3)
        {
            var blue = rgb[offset];
            rgb[offset] = rgb[offset + 2];
            rgb[offset + 2] = blue;
        }

        return rgb;
    }

    private static ushort[] Mono16Samples(int width, int height, int maximum)
    {
        Assert.True(maximum is >= 255 and <= 65_535);
        var samples = new ushort[checked(width * height)];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (ushort)Math.Min((index * 137) + ((index % 7) * 53) + 1, maximum);
        samples[0] = 0;
        samples[1] = (ushort)maximum;
        samples[2] = (ushort)(maximum - 1);
        samples[3] = (ushort)(maximum / 2);
        return samples;
    }

    private static byte[] SampleBytes(ushort[] samples, bool bigEndian)
    {
        var bytes = new byte[checked(samples.Length * 2)];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            bytes[index * 2] = bigEndian ? (byte)(sample >> 8) : (byte)sample;
            bytes[(index * 2) + 1] = bigEndian ? (byte)sample : (byte)(sample >> 8);
        }

        return bytes;
    }

    private static byte[] PaddedMono8Raster(int width, int height, int stride, byte padding)
    {
        Assert.True(stride > width);
        var padded = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                padded[(y * stride) + x] = (byte)(((x * 17) + (y * 29)) & 0x7F);
            for (var x = width; x < stride; x++)
                padded[(y * stride) + x] = padding;
        }

        return padded;
    }

    private static byte[] PaddedMono16Raster(int width, int height, int stride, byte[] tight)
    {
        var tightRowBytes = width * 2;
        Assert.True(stride > tightRowBytes);
        Assert.Equal(checked(tightRowBytes * height), tight.Length);
        var padded = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(tight, y * tightRowBytes, padded, y * stride, tightRowBytes);
            // 0xFFFF sits above every declared 10/12-bit ceiling, so a padding leak cannot be
            // mistaken for a legitimate sample.
            for (var offset = tightRowBytes; offset < stride; offset++)
                padded[(y * stride) + offset] = 0xFF;
        }

        return padded;
    }

    private static byte[] TightRaster(byte[] padded, int stride, int tightRowBytes, int height)
    {
        var tight = new byte[checked(tightRowBytes * height)];
        for (var y = 0; y < height; y++)
            Array.Copy(padded, y * stride, tight, y * tightRowBytes, tightRowBytes);
        return tight;
    }

    private static byte[][] SplitRows(byte[] raster, int rowBytes, int height)
    {
        Assert.Equal(checked(rowBytes * height), raster.Length);
        var rows = new byte[height][];
        for (var row = 0; row < height; row++)
        {
            var slice = new byte[rowBytes];
            Array.Copy(raster, row * rowBytes, slice, 0, rowBytes);
            rows[row] = slice;
        }

        return rows;
    }

    private static byte[][] WithExtraRow(byte[][] rows, int rowBytes)
    {
        var extended = new byte[rows.Length + 1][];
        Array.Copy(rows, extended, rows.Length);
        var copy = new byte[rowBytes];
        Array.Copy(rows[rows.Length - 1], copy, rowBytes);
        extended[rows.Length] = copy;
        return extended;
    }

    private static byte[] Rows(byte[] bytes, int length)
    {
        var copy = new byte[length];
        Array.Copy(bytes, copy, length);
        return copy;
    }

    private static byte[][] Rows(byte[][] rows, int count)
    {
        var copy = new byte[count][];
        Array.Copy(rows, copy, count);
        return copy;
    }

    private static int RowBytesOf(int width, VisionPixelFormat format) =>
        checked(width * BytesPerPixel(format));

    private static int RowBytesOf(int width, int bitDepth, int channels) =>
        ((width * channels * bitDepth) + 7) / 8;

    private static int BytesPerPixel(VisionPixelFormat format) => format switch
    {
        VisionPixelFormat.Mono8 => 1,
        VisionPixelFormat.Mono16 => 2,
        VisionPixelFormat.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    public enum Corruption
    {
        BadIhdrCrc,
        BadIdatCrc,
        CorruptIdatPayload,
        TruncatedSignature,
        TruncatedInsideIdat,
        TruncatedFooter,
        TruncatedInsideIend,
        MissingIdat,
        MissingIend,
        ExtraScanline,
        ShortScanlines
    }

    public enum UnsupportedVariant
    {
        Rgba,
        GrayAlpha,
        Palette,
        Interlaced,
        Gray4Bit
    }

    public enum MetadataMismatch
    {
        Width,
        Height,
        Mono16Format,
        Bgr24Format
    }

    public enum BudgetCase
    {
        CanonicalBytes,
        EncodedBytes,
        WorkingBytes,
        ChunkCount
    }

    public enum StageContradiction
    {
        Hash,
        Envelope,
        Truncated
    }

    private sealed record PngChunk(int Start, int DataStart, int DataLength, string Type);

    /// <summary>
    /// Minimal, dependency-free PNG producer for fixtures the codec must refuse or decode. It is
    /// deliberately independent of the codec under test: it writes its own chunk layout, CRCs and
    /// zlib stream, and applies filters 0..4 (or the Adam7 pass layout) itself.
    /// </summary>
    private static class PngFixture
    {
        internal const int ColorTypeGray = 0;
        internal const int ColorTypeRgb = 2;
        internal const int ColorTypePalette = 3;
        internal const int ColorTypeGrayAlpha = 4;
        internal const int ColorTypeRgba = 6;

        private static readonly byte[] Signature =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static readonly int[] Adam7StartX = { 0, 4, 0, 2, 0, 1, 0 };
        private static readonly int[] Adam7StartY = { 0, 0, 4, 0, 2, 0, 1 };
        private static readonly int[] Adam7StepX = { 8, 8, 4, 4, 2, 2, 1 };
        private static readonly int[] Adam7StepY = { 8, 8, 8, 4, 4, 2, 2 };

        internal static byte[] Write(int width, int height, int bitDepth, int colorType, int channels,
            byte[][] rasterRows, int filter, bool interlaced = false, byte[]? palette = null,
            bool includeIdat = true, bool includeIend = true, int idatChunks = 1,
            byte[]? rowFilters = null)
        {
            var rowBytes = RowBytesOf(width, bitDepth, channels);
            var pixelBytes = Math.Max(1, channels * (bitDepth / 8));
            var scanlines = interlaced
                ? Adam7Scanlines(width, height, rasterRows, rowBytes, pixelBytes, filter)
                : FilterRows(rasterRows, rowBytes, pixelBytes, filter, rowFilters);
            using var body = new MemoryStream();
            body.Write(Signature, 0, Signature.Length);
            WriteChunk(body, "IHDR", Ihdr(width, height, bitDepth, colorType, interlaced));
            if (palette is not null) WriteChunk(body, "PLTE", palette);
            if (includeIdat) WriteIdatChunks(body, Deflate(scanlines), idatChunks);
            if (includeIend) WriteChunk(body, "IEND", Array.Empty<byte>());
            return body.ToArray();
        }

        internal static byte[] WriteGray8(int width, int height, byte[][] rasterRows, int filter,
            bool includeIdat = true, bool includeIend = true, int idatChunks = 1,
            byte[]? rowFilters = null) =>
            Write(width, height, 8, ColorTypeGray, 1, rasterRows, filter, interlaced: false,
                palette: null, includeIdat: includeIdat, includeIend: includeIend,
                idatChunks: idatChunks, rowFilters: rowFilters);

        internal static byte[] WriteGray16(int width, int height, ushort[] samples)
        {
            Assert.Equal(checked(width * height), samples.Length);
            var networkOrder = SampleBytes(samples, bigEndian: true);
            return Write(width, height, 16, ColorTypeGray, 1, SplitRows(networkOrder, width * 2, height),
                filter: 0, interlaced: false, palette: null);
        }

        internal static byte[] WriteRgba(int width, int height, byte[] raster, byte alpha)
        {
            var rows = new byte[height][];
            for (var y = 0; y < height; y++)
            {
                var row = new byte[width * 4];
                for (var x = 0; x < width; x++)
                {
                    var value = raster[(y * width) + x];
                    row[x * 4] = value;
                    row[(x * 4) + 1] = value;
                    row[(x * 4) + 2] = value;
                    row[(x * 4) + 3] = alpha;
                }

                rows[y] = row;
            }

            return Write(width, height, 8, ColorTypeRgba, 4, rows, filter: 0, interlaced: false,
                palette: null);
        }

        internal static byte[] WriteGrayAlpha(int width, int height, byte[] raster, byte alpha)
        {
            var rows = new byte[height][];
            for (var y = 0; y < height; y++)
            {
                var row = new byte[width * 2];
                for (var x = 0; x < width; x++)
                {
                    row[x * 2] = raster[(y * width) + x];
                    row[(x * 2) + 1] = alpha;
                }

                rows[y] = row;
            }

            return Write(width, height, 8, ColorTypeGrayAlpha, 2, rows, filter: 0, interlaced: false,
                palette: null);
        }

        internal static byte[] WritePalette(int width, int height, byte[] raster, byte[] palette)
        {
            var rows = new byte[height][];
            for (var y = 0; y < height; y++)
            {
                var row = new byte[width];
                for (var x = 0; x < width; x++)
                    row[x] = (byte)(raster[(y * width) + x] & 0x01);
                rows[y] = row;
            }

            return Write(width, height, 8, ColorTypePalette, 1, rows, filter: 0, interlaced: false,
                palette: palette);
        }

        internal static byte[] WriteInterlacedGray8(int width, int height, byte[] raster) =>
            Write(width, height, 8, ColorTypeGray, 1, SplitRows(raster, width, height), filter: 0,
                interlaced: true, palette: null);

        internal static byte[] WriteGray4(int width, int height, byte[] raster)
        {
            Assert.Equal(checked(width * height), raster.Length);
            var rows = new byte[height][];
            for (var y = 0; y < height; y++)
            {
                var row = new byte[RowBytesOf(width, 4, 1)];
                for (var x = 0; x < width; x++)
                {
                    var nibble = (byte)(raster[(y * width) + x] & 0x0F);
                    if ((x & 1) == 0) row[x / 2] = (byte)(nibble << 4);
                    else row[x / 2] |= nibble;
                }

                rows[y] = row;
            }

            return Write(width, height, 4, ColorTypeGray, 1, rows, filter: 0, interlaced: false,
                palette: null);
        }

        private static byte[] Ihdr(int width, int height, int bitDepth, int colorType, bool interlaced)
        {
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
            ihdr[8] = (byte)bitDepth;
            ihdr[9] = (byte)colorType;
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = (byte)(interlaced ? 1 : 0);
            return ihdr;
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)data.Length);
            for (var index = 0; index < 4; index++) header[4 + index] = (byte)type[index];
            stream.Write(header, 0, header.Length);
            stream.Write(data, 0, data.Length);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(type, data));
            stream.Write(crc, 0, crc.Length);
        }

        private static void WriteIdatChunks(Stream stream, byte[] compressed, int chunkCount)
        {
            if (chunkCount < 1) chunkCount = 1;
            var size = Math.Max(1, (compressed.Length + chunkCount - 1) / chunkCount);
            var offset = 0;
            while (offset < compressed.Length)
            {
                var length = Math.Min(size, compressed.Length - offset);
                WriteChunk(stream, "IDAT", Rows(compressed, offset, length));
                offset += length;
            }
        }

        private static byte[] Rows(byte[] bytes, int offset, int length)
        {
            var slice = new byte[length];
            Array.Copy(bytes, offset, slice, 0, length);
            return slice;
        }

        private static byte[] Deflate(byte[] raw)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            return output.ToArray();
        }

        private static byte[] FilterRows(byte[][] rasterRows, int rowBytes, int pixelBytes, int filter,
            byte[]? rowFilters)
        {
            using var output = new MemoryStream();
            byte[]? previous = null;
            for (var row = 0; row < rasterRows.Length; row++)
            {
                Assert.Equal(rowBytes, rasterRows[row].Length);
                var rowFilter = rowFilters is null ? filter : rowFilters[row];
                output.WriteByte((byte)rowFilter);
                var filtered = FilterRow(rowFilter, rasterRows[row], previous, pixelBytes);
                output.Write(filtered, 0, filtered.Length);
                previous = rasterRows[row];
            }

            return output.ToArray();
        }

        private static byte[] Adam7Scanlines(int width, int height, byte[][] rasterRows, int rowBytes,
            int pixelBytes, int filter)
        {
            Assert.Equal(height, rasterRows.Length);
            using var output = new MemoryStream();
            for (var pass = 0; pass < 7; pass++)
            {
                byte[]? previous = null;
                for (var y = Adam7StartY[pass]; y < height; y += Adam7StepY[pass])
                {
                    Assert.Equal(rowBytes, rasterRows[y].Length);
                    var columns = new List<int>();
                    for (var x = Adam7StartX[pass]; x < width; x += Adam7StepX[pass])
                        columns.Add(x);
                    if (columns.Count == 0) continue;
                    var raw = new byte[columns.Count * pixelBytes];
                    for (var index = 0; index < columns.Count; index++)
                        Array.Copy(rasterRows[y], columns[index] * pixelBytes, raw, index * pixelBytes,
                            pixelBytes);
                    output.WriteByte((byte)filter);
                    var filtered = FilterRow(filter, raw, previous, pixelBytes);
                    output.Write(filtered, 0, filtered.Length);
                    previous = raw;
                }
            }

            return output.ToArray();
        }

        private static byte[] FilterRow(int filter, byte[] raw, byte[]? previous, int pixelBytes)
        {
            Assert.InRange(filter, 0, 4);
            var output = new byte[raw.Length];
            for (var index = 0; index < raw.Length; index++)
            {
                var left = index >= pixelBytes ? raw[index - pixelBytes] : 0;
                var up = previous is null ? 0 : previous[index];
                var upLeft = previous is not null && index >= pixelBytes ? previous[index - pixelBytes] : 0;
                var predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) >> 1,
                    _ => Paeth(left, up, upLeft)
                };
                output[index] = (byte)(raw[index] - predictor);
            }

            return output;
        }

        private static uint Crc32(string type, byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var character in type) crc = Crc32Step(crc, (byte)character);
            foreach (var value in data) crc = Crc32Step(crc, value);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint Crc32Step(uint crc, byte value)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            return crc;
        }
    }
}
