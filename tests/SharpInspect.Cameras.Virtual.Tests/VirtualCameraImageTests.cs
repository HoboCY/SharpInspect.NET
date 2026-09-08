using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using Xunit;

namespace SharpInspect.Cameras.Virtual.Tests;

public sealed class VirtualCameraImageTests
{
    [Fact]
    public void V116_I01_SyntheticImageIsDeterministicAndLayoutBound()
    {
        var first = VirtualCameraImage.CreateSynthetic("cam-01", 3, 2,
            VisionPixelFormat.Mono8, null, 0x12345678u, 2);
        var second = VirtualCameraImage.CreateSynthetic("cam-01", 3, 2,
            VisionPixelFormat.Mono8, null, 0x12345678u, 2);
        var differentPadding = VirtualCameraImage.CreateSynthetic("cam-01", 3, 2,
            VisionPixelFormat.Mono8, null, 0x12345678u, 3);
        var differentSeed = VirtualCameraImage.CreateSynthetic("cam-01", 3, 2,
            VisionPixelFormat.Mono8, null, 0x12345679u, 2);

        Assert.Equal(first.CopyPixels(), second.CopyPixels());
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.PixelDataHash, second.PixelDataHash);
        Assert.NotEqual(first.ContentHash, differentPadding.ContentHash);
        Assert.Equal(first.PixelDataHash, differentPadding.PixelDataHash);
        Assert.NotEqual(first.ContentHash, differentSeed.ContentHash);
        Assert.NotEqual(first.PixelDataHash, differentSeed.PixelDataHash);
        Assert.Matches("^[0-9A-F]{64}$", first.ContentHash);
        Assert.Matches("^[0-9A-F]{64}$", first.PixelDataHash);
    }

    [Fact]
    public void V116_I02_ConstructorCopiesInputAndClearsAllRowPadding()
    {
        var source = new byte[] { 1, 2, 0xA1, 0xA2, 3, 4, 0xB1, 0xB2 };
        var image = new VirtualCameraImage("cam-01", 2, 2, 4,
            VisionPixelFormat.Mono8, null, source);

        source[0] = 99;
        source[2] = 99;
        var copied = image.CopyPixels();
        Assert.Equal(new byte[] { 1, 2, 0, 0, 3, 4, 0, 0 }, copied);
        Assert.Equal(new byte[] { 1, 2 }, image.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 3, 4 }, image.GetRowSpan(1).ToArray());

        copied[0] = 88;
        Assert.Equal(1, image.GetRowSpan(0)[0]);
        Assert.Equal(8, image.CopyPixels().Length);
    }

    [Fact]
    public void V116_I03_Mono16RequiresSupportedValidBitsAndClearedHighBits()
    {
        var tenBit = new VirtualCameraImage("mono16", 2, 1, 4,
            VisionPixelFormat.Mono16, 10, new byte[] { 0xFF, 0x03, 0x00, 0x02 });
        var twelveBit = new VirtualCameraImage("mono16", 1, 1, 2,
            VisionPixelFormat.Mono16, 12, new byte[] { 0xFF, 0x0F });
        var sixteenBit = new VirtualCameraImage("mono16", 1, 1, 2,
            VisionPixelFormat.Mono16, 16, new byte[] { 0xFF, 0xFF });

        Assert.Equal(new byte[] { 0xFF, 0x03, 0x00, 0x02 }, tenBit.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0x0F }, twelveBit.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, sixteenBit.GetRowSpan(0).ToArray());

        var highBits = Assert.Throws<ArgumentException>(() => new VirtualCameraImage(
            "mono16", 1, 1, 2, VisionPixelFormat.Mono16, 10, new byte[] { 0x00, 0x04 }));
        Assert.StartsWith("VirtualCameraMono16HighBitsNonZero", highBits.Message, StringComparison.Ordinal);

        var invalidBits = Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualCameraImage(
            "mono16", 1, 1, 2, VisionPixelFormat.Mono16, 11, new byte[] { 0, 0 }));
        Assert.Equal("validBits", invalidBits.ParamName);
    }

    [Fact]
    public void V116_I04_LayoutAndBufferBoundsFailClosed()
    {
        var shortBuffer = Assert.Throws<ArgumentException>(() => new VirtualCameraImage(
            "short", 2, 2, 4, VisionPixelFormat.Mono8, null, new byte[5]));
        Assert.StartsWith("VirtualCameraPixelBufferTooShort", shortBuffer.Message, StringComparison.Ordinal);

        var invalidStride = Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualCameraImage(
            "stride", 3, 1, 2, VisionPixelFormat.Mono8, null, new byte[2]));
        Assert.Equal("strideBytes", invalidStride.ParamName);

        var overCapacity = Assert.Throws<ArgumentException>(() => new VirtualCameraImage(
            "large", 4097, 4096, 4097, VisionPixelFormat.Mono8, null, ReadOnlySpan<byte>.Empty));
        Assert.Equal("VirtualCameraImageLayoutTooLarge", overCapacity.Message);
    }

    [Fact]
    public void V116_I05_LoadRecordedRawVerifiesFileHashBeforeCanonicalizing()
    {
        var file = Path.Combine(Path.GetTempPath(), "sharpinspect-virtual-" + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            var bytes = new byte[] { 1, 2, 0xA1, 0xA2, 3, 4, 0xB1, 0xB2 };
            File.WriteAllBytes(file, bytes);
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));

            var image = VirtualCameraImage.LoadRecordedRaw("recorded", file, 2, 2, 4,
                VisionPixelFormat.Mono8, null, expectedHash);

            Assert.Equal("recorded", image.Id);
            Assert.Equal(expectedHash, image.SourceDataHash);
            Assert.Equal(new byte[] { 1, 2, 0, 0, 3, 4, 0, 0 }, image.CopyPixels());
            Assert.DoesNotContain(file, image.ContentHash, StringComparison.Ordinal);

            var mismatch = Assert.Throws<InvalidDataException>(() => VirtualCameraImage.LoadRecordedRaw(
                "recorded", file, 2, 2, 4, VisionPixelFormat.Mono8, null, new string('0', 64)));
            Assert.Equal("VirtualCameraRecordedHashMismatch", mismatch.Message);
            Assert.DoesNotContain(file, mismatch.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void V116_I06_LoadRecordedRawRequiresAbsoluteBoundedExactFile()
    {
        var relative = Assert.Throws<ArgumentException>(() => VirtualCameraImage.LoadRecordedRaw(
            "recorded", "relative.raw", 1, 1, 1, VisionPixelFormat.Mono8, null, new string('0', 64)));
        Assert.StartsWith("VirtualCameraRecordedPathMustBeAbsolute", relative.Message, StringComparison.Ordinal);

        var missing = Path.Combine(Path.GetTempPath(), "sharpinspect-missing-" + Guid.NewGuid().ToString("N") + ".raw");
        var readFailure = Assert.Throws<InvalidDataException>(() => VirtualCameraImage.LoadRecordedRaw(
            "recorded", missing, 1, 1, 1, VisionPixelFormat.Mono8, null,
            new string('0', 64)));
        Assert.Equal("VirtualCameraRecordedReadFailed", readFailure.Message);
        Assert.DoesNotContain(missing, readFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V116_I07_SyntheticFormatsUseNormalizedPixelLayouts()
    {
        var mono16 = VirtualCameraImage.CreateSynthetic("mono16", 4, 3,
            VisionPixelFormat.Mono16, 10, 17u, 2);
        var bgr = VirtualCameraImage.CreateSynthetic("bgr", 2, 1,
            VisionPixelFormat.Bgr24, null, 17u, 1);

        Assert.Equal(10, mono16.ValidBits);
        Assert.Equal(5, mono16.StrideBytes / 2);
        for (var i = 0; i < mono16.GetRowSpan(0).Length; i += 2)
        {
            var value = mono16.GetRowSpan(0)[i] | (mono16.GetRowSpan(0)[i + 1] << 8);
            Assert.InRange(value, 0, 1023);
        }

        Assert.Equal(6, bgr.GetRowSpan(0).Length);
        Assert.Equal(7, bgr.StrideBytes);
        Assert.Equal(0, bgr.CopyPixels()[6]);
    }

    [Fact]
    public void V116_I08_IdentityAndRowsHaveStableBounds()
    {
        var nullId = Assert.Throws<ArgumentNullException>(() => new VirtualCameraImage(
            null!, 1, 1, 1, VisionPixelFormat.Mono8, null, new byte[] { 1 }));
        Assert.Equal("id", nullId.ParamName);

        var invalidId = Assert.Throws<ArgumentException>(() => new VirtualCameraImage(
            "bad id", 1, 1, 1, VisionPixelFormat.Mono8, null, new byte[] { 1 }));
        Assert.StartsWith("VirtualCameraImageIdentifierInvalid", invalidId.Message, StringComparison.Ordinal);

        var image = VirtualCameraImage.CreateSynthetic("rows", 1, 2,
            VisionPixelFormat.Mono8, null, 1u);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetRowSpan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetRowSpan(image.Height));
    }

    [Fact]
    public void V116_I09_RecordedRawRejectsMalformedExpectedHashAndExtraBytes()
    {
        var file = Path.Combine(Path.GetTempPath(), "sharpinspect-virtual-extra-" + Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            var bytes = new byte[] { 1, 2 };
            File.WriteAllBytes(file, bytes);
            var actual = Convert.ToHexString(SHA256.HashData(bytes));

            var extra = Assert.Throws<InvalidDataException>(() => VirtualCameraImage.LoadRecordedRaw(
                "recorded", file, 1, 1, 1, VisionPixelFormat.Mono8, null, actual));
            Assert.Equal("VirtualCameraRecordedLengthInvalid", extra.Message);

            var finalPaddingOmitted = new byte[] { 7 };
            File.WriteAllBytes(file, finalPaddingOmitted);
            var shortHash = Convert.ToHexString(SHA256.HashData(finalPaddingOmitted));
            var accepted = VirtualCameraImage.LoadRecordedRaw("recorded", file, 1, 1, 2,
                VisionPixelFormat.Mono8, null, shortHash);
            Assert.Equal(new byte[] { 7, 0 }, accepted.CopyPixels());

            var malformed = Assert.Throws<ArgumentException>(() => VirtualCameraImage.LoadRecordedRaw(
                "recorded", file, 1, 1, 1, VisionPixelFormat.Mono8, null, "not-a-sha"));
            Assert.StartsWith("VirtualCameraExpectedSha256Invalid", malformed.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
