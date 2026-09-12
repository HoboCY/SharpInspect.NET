using System.Security.Cryptography;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Published Canonical Pixel Content vectors. Every envelope below is written out byte by byte
/// from the documented wire layout, and every frozen digest is SHA-256 over those hand-built
/// bytes followed by the listed pixel bytes, obtained independently of the production code.
/// </summary>
public sealed class CanonicalImagePixelContentTests
{
    private const string Mono8EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000002" + // width
        "00000002" + // height
        "01" + // Mono8 wire code
        "00" + // Valid Bits absent
        "00" + // no Valid Bits value
        "00" + // reserved
        "0000000000000004"; // 2 * 2 * 1 canonical pixels

    private const string Mono16_10EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000002" + // width
        "00000002" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "0A" + // 10 valid bits
        "00" + // reserved
        "0000000000000008"; // 2 * 2 * 2 canonical pixels

    private const string Mono16_12EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000003" + // width
        "00000001" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "0C" + // 12 valid bits
        "00" + // reserved
        "0000000000000006"; // 3 * 1 * 2 canonical pixels

    private const string Mono16_16EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000001" + // width
        "00000001" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "10" + // 16 valid bits
        "00" + // reserved
        "0000000000000002"; // 1 * 1 * 2 canonical pixels

    private const string Bgr24EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000001" + // width
        "00000002" + // height
        "03" + // Bgr24 wire code
        "00" + // Valid Bits absent
        "00" + // no Valid Bits value
        "00" + // reserved
        "0000000000000006"; // 1 * 2 * 3 canonical pixels

    private const string Mono8_4x2EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000004" + // width
        "00000002" + // height
        "01" + // Mono8 wire code
        "00" + // Valid Bits absent
        "00" + // no Valid Bits value
        "00" + // reserved
        "0000000000000008"; // 4 * 2 * 1 canonical pixels

    private const string Mono16_10_2x1EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000002" + // width
        "00000001" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "0A" + // 10 valid bits
        "00" + // reserved
        "0000000000000004"; // 2 * 1 * 2 canonical pixels

    private const string Mono16_12_1x1EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000001" + // width
        "00000001" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "0C" + // 12 valid bits
        "00" + // reserved
        "0000000000000002"; // 1 * 1 * 2 canonical pixels

    private const string Mono16_16_32768x2EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00008000" + // width 32768
        "00000002" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "10" + // 16 valid bits
        "00" + // reserved
        "0000000000020000"; // 32768 * 2 * 2 canonical pixels

    private const string Mono16_16_2x1EnvelopeHex =
        "5349504958454C31" + // ASCII "SIPIXEL1"
        "00000001" + // envelope version
        "00000002" + // width
        "00000001" + // height
        "02" + // Mono16 wire code
        "01" + // Valid Bits present
        "10" + // 16 valid bits
        "00" + // reserved
        "0000000000000004"; // 2 * 1 * 2 canonical pixels

    private const string Mono8Digest = "3AEEB51A69115BF280E83997EDDFC9F5580D2963FB8F197311FC17D927D0E054";
    private const string Mono16_12Digest = "D33ED535A717DAD25F0A1B6919567E4B12F58065251E25A1A0F7383F8ECB54A2";
    private const string Mono16_10Digest = "F96E2AC4F787FCDE9C8E4256F3AE3CA93A0921F8A6674F7B3CAF85414B425823";
    private const string Bgr24Digest = "E9C5ADFBF56B03237FD9C391AEF2593C3C2BE46771F6CBC19416F47FB7E38334";
    private const string Mono8_4x2Digest = "CF2E863CF5F14272FEFA0D2BACD13E160032040D9ED6D91C3D3A3D829D99A8C0";

    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("VerificationId", "V150_C01")]
    public void V150_C01_EnvelopeMatchesThePublishedWireLayout()
    {
        Assert.Equal("SharpInspect.CanonicalImagePixels", CanonicalImagePixelContent.HashScheme);
        Assert.Equal(1, CanonicalImagePixelContent.HashSchemeVersion);

        var mono8 = CanonicalImagePixelContent.CreateEnvelope(2, 2, VisionPixelFormat.Mono8, null);
        Assert.Equal(32, mono8.Length);
        Assert.Equal(Convert.FromHexString(Mono8EnvelopeHex), mono8);

        var mono16Ten = CanonicalImagePixelContent.CreateEnvelope(2, 2, VisionPixelFormat.Mono16, 10);
        Assert.Equal(Convert.FromHexString(Mono16_10EnvelopeHex), mono16Ten);

        var mono16Twelve = CanonicalImagePixelContent.CreateEnvelope(3, 1, VisionPixelFormat.Mono16, 12);
        Assert.Equal(Convert.FromHexString(Mono16_12EnvelopeHex), mono16Twelve);

        var mono16Sixteen = CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Mono16, 16);
        Assert.Equal(Convert.FromHexString(Mono16_16EnvelopeHex), mono16Sixteen);

        var bgr24 = CanonicalImagePixelContent.CreateEnvelope(1, 2, VisionPixelFormat.Bgr24, null);
        Assert.Equal(Convert.FromHexString(Bgr24EnvelopeHex), bgr24);

        // The format field is a wire code, not the enum ordinal, and the reserved byte never moves.
        Assert.Equal(1, (int)mono8[20]);
        Assert.Equal(2, (int)mono16Twelve[20]);
        Assert.Equal(3, (int)bgr24[20]);
        Assert.Equal(0, (int)mono8[21]);
        Assert.Equal(1, (int)mono16Twelve[21]);
        Assert.Equal(0, (int)mono8[22]);
        Assert.Equal(12, (int)mono16Twelve[22]);
        Assert.Equal(0, (int)mono8[23]);
        Assert.Equal(0, (int)mono16Twelve[23]);
        Assert.Equal(0, (int)bgr24[23]);

        // The declared boundary accepts 32768 and binds the big-endian count.
        var edge = CanonicalImagePixelContent.CreateEnvelope(32_768, 32_768, VisionPixelFormat.Bgr24, null);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x00 }, edge[12..16]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x00 }, edge[16..20]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00 }, edge[24..32]);
    }

    [Fact]
    [Trait("VerificationId", "V150_C02")]
    public void V150_C02_FrozenDigestsMatchHandBuiltEnvelopesAndPixels()
    {
        var mono8 = Frame(2, 2, VisionPixelFormat.Mono8, null,
            new byte[] { 0x00, 0x7F }, new byte[] { 0x80, 0xFF });
        var mono16 = Frame(3, 1, VisionPixelFormat.Mono16, 12,
            new byte[] { 0x00, 0x00, 0xFF, 0x0F, 0x23, 0x01 });
        var bgr24 = Frame(1, 2, VisionPixelFormat.Bgr24, null,
            new byte[] { 0x11, 0x22, 0x33 }, new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.Equal(Mono8Digest, CanonicalImagePixelContent.ComputeHash(mono8));
        Assert.Equal(Mono16_12Digest, CanonicalImagePixelContent.ComputeHash(mono16));
        Assert.Equal(Bgr24Digest, CanonicalImagePixelContent.ComputeHash(bgr24));

        // Each frozen digest is exactly SHA-256 over the hand-built envelope and row bytes.
        Assert.Equal(Mono8Digest, Oracle(Mono8EnvelopeHex,
            new byte[] { 0x00, 0x7F }, new byte[] { 0x80, 0xFF }));
        Assert.Equal(Mono16_12Digest, Oracle(Mono16_12EnvelopeHex,
            new byte[] { 0x00, 0x00, 0xFF, 0x0F, 0x23, 0x01 }));
        Assert.Equal(Bgr24Digest, Oracle(Bgr24EnvelopeHex,
            new byte[] { 0x11, 0x22, 0x33 }, new byte[] { 0xAA, 0xBB, 0xCC }));

        Assert.Equal(64, Mono8Digest.Length);
        Assert.Equal(CanonicalImagePixelContent.ComputeHash(mono8),
            CanonicalImagePixelContent.ComputeHash(mono8).ToUpperInvariant());
    }

    [Fact]
    [Trait("VerificationId", "V150_C03")]
    public void V150_C03_StridePaddingAndSurplusRowBytesAreExcluded()
    {
        var tight = Frame(4, 2, VisionPixelFormat.Mono8, null,
            new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 });
        var padded = Frame(4, 2, VisionPixelFormat.Mono8, null, 8,
            new byte[] { 1, 2, 3, 4, 0xEE, 0xEE, 0xEE, 0xEE },
            new byte[] { 5, 6, 7, 8, 0x11, 0x22, 0x33, 0x44 });
        var otherPadding = Frame(4, 2, VisionPixelFormat.Mono8, null, 8,
            new byte[] { 1, 2, 3, 4, 0xAA, 0xBB, 0xCC, 0xDD },
            new byte[] { 5, 6, 7, 8, 0x00, 0x00, 0x00, 0x00 });
        var surplusBytes = Frame(4, 2, VisionPixelFormat.Mono8, null, 4,
            new byte[] { 1, 2, 3, 4, 0xEE, 0xEE, 0xEE, 0xEE, 0x01 },
            new byte[] { 5, 6, 7, 8 });
        var changedValidPixel = Frame(4, 2, VisionPixelFormat.Mono8, null, 8,
            new byte[] { 1, 2, 3, 4, 0xEE, 0xEE, 0xEE, 0xEE },
            new byte[] { 5, 6, 7, 9, 0x11, 0x22, 0x33, 0x44 });
        var changedPaddingByte = Frame(4, 2, VisionPixelFormat.Mono8, null, 8,
            new byte[] { 1, 2, 3, 4, 0xEF, 0xEE, 0xEE, 0xEE },
            new byte[] { 5, 6, 7, 8, 0x11, 0x22, 0x33, 0x44 });

        Assert.Equal(Mono8_4x2Digest, CanonicalImagePixelContent.ComputeHash(tight));
        Assert.Equal(CanonicalImagePixelContent.ComputeHash(tight),
            CanonicalImagePixelContent.ComputeHash(padded));
        Assert.Equal(CanonicalImagePixelContent.ComputeHash(tight),
            CanonicalImagePixelContent.ComputeHash(otherPadding));
        Assert.Equal(CanonicalImagePixelContent.ComputeHash(tight),
            CanonicalImagePixelContent.ComputeHash(surplusBytes));
        Assert.Equal(CanonicalImagePixelContent.ComputeHash(tight),
            CanonicalImagePixelContent.ComputeHash(changedPaddingByte));
        Assert.NotEqual(CanonicalImagePixelContent.ComputeHash(tight),
            CanonicalImagePixelContent.ComputeHash(changedValidPixel));
        Assert.Equal(Mono8_4x2Digest, Oracle(Mono8_4x2EnvelopeHex,
            new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 }));
    }

    [Fact]
    [Trait("VerificationId", "V150_C04")]
    public void V150_C04_ShapeFormatAndValidBitsChangeTheHash()
    {
        var mono8 = Frame(2, 2, VisionPixelFormat.Mono8, null,
            new byte[] { 1, 2 }, new byte[] { 3, 4 });
        var reshaped = Frame(4, 1, VisionPixelFormat.Mono8, null, new byte[] { 1, 2, 3, 4 });
        var mono16Ten = Frame(2, 2, VisionPixelFormat.Mono16, 10,
            new byte[] { 0x01, 0x00, 0xFF, 0x03 }, new byte[] { 0x00, 0x00, 0x00, 0x02 });
        var mono16Twelve = Frame(2, 2, VisionPixelFormat.Mono16, 12,
            new byte[] { 0x01, 0x00, 0xFF, 0x03 }, new byte[] { 0x00, 0x00, 0x00, 0x02 });
        var mono16Sixteen = Frame(2, 2, VisionPixelFormat.Mono16, 16,
            new byte[] { 0x01, 0x00, 0xFF, 0x03 }, new byte[] { 0x00, 0x00, 0x00, 0x02 });
        var mono16SixBytes = Frame(3, 1, VisionPixelFormat.Mono16, 16, new byte[] { 1, 2, 3, 4, 5, 6 });
        var bgr24 = Frame(2, 1, VisionPixelFormat.Bgr24, null, new byte[] { 1, 2, 3, 4, 5, 6 });
        var mono16ChangedSample = Frame(2, 2, VisionPixelFormat.Mono16, 12,
            new byte[] { 0x01, 0x00, 0xFE, 0x03 }, new byte[] { 0x00, 0x00, 0x00, 0x02 });

        Assert.Equal(Mono16_10Digest, CanonicalImagePixelContent.ComputeHash(mono16Ten));

        var hashes = new[]
        {
            CanonicalImagePixelContent.ComputeHash(mono8),
            CanonicalImagePixelContent.ComputeHash(reshaped),
            CanonicalImagePixelContent.ComputeHash(mono16Ten),
            CanonicalImagePixelContent.ComputeHash(mono16Twelve),
            CanonicalImagePixelContent.ComputeHash(mono16Sixteen),
            CanonicalImagePixelContent.ComputeHash(mono16SixBytes),
            CanonicalImagePixelContent.ComputeHash(bgr24)
        };
        Assert.Equal(hashes.Length, hashes.Distinct().Count());

        // The same samples, shape, and canonical pixel count under a different Valid Bits still differ.
        var tenBitEnvelope = CanonicalImagePixelContent.CreateEnvelope(2, 2, VisionPixelFormat.Mono16, 10);
        var twelveBitEnvelope = CanonicalImagePixelContent.CreateEnvelope(2, 2, VisionPixelFormat.Mono16, 12);
        Assert.Equal(Convert.FromHexString(Mono16_10EnvelopeHex), tenBitEnvelope);
        Assert.Equal(tenBitEnvelope[..22], twelveBitEnvelope[..22]);
        Assert.Equal(10, (int)tenBitEnvelope[22]);
        Assert.Equal(12, (int)twelveBitEnvelope[22]);
        Assert.Equal(tenBitEnvelope[24..32], twelveBitEnvelope[24..32]);
        Assert.NotEqual(CanonicalImagePixelContent.ComputeHash(mono16Ten),
            CanonicalImagePixelContent.ComputeHash(mono16Twelve));

        // A changed sample byte changes the hash; an identical frame does not.
        Assert.NotEqual(CanonicalImagePixelContent.ComputeHash(mono16Twelve),
            CanonicalImagePixelContent.ComputeHash(mono16ChangedSample));
        Assert.Equal(CanonicalImagePixelContent.ComputeHash(mono8), CanonicalImagePixelContent.ComputeHash(
            Frame(2, 2, VisionPixelFormat.Mono8, null, new byte[] { 1, 2 }, new byte[] { 3, 4 })));
    }

    [Fact]
    [Trait("VerificationId", "V150_C05")]
    public void V150_C05_Mono16SamplesAboveValidBitsAreRejected()
    {
        var tenBitBoundary = Frame(2, 1, VisionPixelFormat.Mono16, 10,
            new byte[] { 0xFF, 0x03, 0x00, 0x02 });
        Assert.Equal(Oracle(Mono16_10_2x1EnvelopeHex, new byte[] { 0xFF, 0x03, 0x00, 0x02 }),
            CanonicalImagePixelContent.ComputeHash(tenBitBoundary));

        var tenBitOverflow = Frame(2, 1, VisionPixelFormat.Mono16, 10,
            new byte[] { 0x00, 0x04, 0x00, 0x00 });
        Assert.Equal("CanonicalImagePixelMono16HighBitsInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalImagePixelContent.ComputeHash(tenBitOverflow)).Message);

        var twelveBitBoundary = Frame(1, 1, VisionPixelFormat.Mono16, 12, new byte[] { 0xFF, 0x0F });
        Assert.Equal(Oracle(Mono16_12_1x1EnvelopeHex, new byte[] { 0xFF, 0x0F }),
            CanonicalImagePixelContent.ComputeHash(twelveBitBoundary));

        var twelveBitOverflow = Frame(2, 1, VisionPixelFormat.Mono16, 12,
            new byte[] { 0xFF, 0x0F, 0x00, 0x10 });
        Assert.Equal("CanonicalImagePixelMono16HighBitsInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalImagePixelContent.ComputeHash(twelveBitOverflow)).Message);

        // Every 16-bit pattern is canonical at 16 valid bits.
        var fullRange = Frame(2, 1, VisionPixelFormat.Mono16, 16, new byte[] { 0xFF, 0xFF, 0x00, 0x80 });
        Assert.Equal(Oracle(Mono16_16_2x1EnvelopeHex, new byte[] { 0xFF, 0xFF, 0x00, 0x80 }),
            CanonicalImagePixelContent.ComputeHash(fullRange));
    }

    [Fact]
    [Trait("VerificationId", "V150_C06")]
    public void V150_C06_InvalidDimensionsFormatsAndValidBitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(0, 1, VisionPixelFormat.Mono8, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(32_769, 1, VisionPixelFormat.Mono8, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 0, VisionPixelFormat.Mono8, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 32_769, VisionPixelFormat.Mono8, null));

        // Undefined formats never receive a wire code.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, (VisionPixelFormat)3, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, (VisionPixelFormat)(-1), null));

        // Mono16 requires exactly 10, 12, or 16 valid bits; the other formats carry none.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Mono16, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Mono16, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Mono16, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Mono16, 24));
        Assert.Throws<ArgumentException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Mono8, 8));
        Assert.Throws<ArgumentException>(() =>
            CanonicalImagePixelContent.CreateEnvelope(1, 1, VisionPixelFormat.Bgr24, 16));
    }

    [Fact]
    [Trait("VerificationId", "V150_C07")]
    public void V150_C07_InvalidFrameMetadataCannotReachTheHash()
    {
        Assert.Throws<ArgumentNullException>(() => CanonicalImagePixelContent.ComputeHash(null!));

        // Metadata is validated where it is built, so a frame can never present a format or
        // Valid Bits combination that the envelope would refuse.
        Assert.Throws<ArgumentOutOfRangeException>(() => Metadata(1, 1, VisionPixelFormat.Mono16, 8));
        Assert.Throws<ArgumentException>(() => Metadata(1, 1, VisionPixelFormat.Mono8, 8));
        Assert.ThrowsAny<ArgumentException>(() => Metadata(1, 1, (VisionPixelFormat)9, null));
        Assert.Throws<ArgumentException>(() => Metadata(1, 1, VisionPixelFormat.Bgr24, 10));
    }

    [Fact]
    [Trait("VerificationId", "V150_C08")]
    public void V150_C08_ExpiredLoanCanceledTokenAndShortRowAreRejected()
    {
        var rows = new[] { new byte[] { 1, 2 }, new byte[] { 3, 4 } };

        // An expired loan is refused before any row is read.
        var expired = new TestFrame(Metadata(2, 2, VisionPixelFormat.Mono8, null), rows,
            loanActive: _ => false);
        Assert.Equal("CanonicalImagePixelLoanInactive",
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalImagePixelContent.ComputeHash(expired)).Message);
        Assert.Equal(0, expired.RowReads);

        // A loan that expires after the first row is caught before the second row is read.
        var expiring = new TestFrame(Metadata(2, 2, VisionPixelFormat.Mono8, null), rows,
            loanActive: reads => reads < 1);
        Assert.Equal("CanonicalImagePixelLoanInactive",
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalImagePixelContent.ComputeHash(expiring)).Message);
        Assert.Equal(1, expiring.RowReads);

        // A token canceled before the call stops the hash.
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CanonicalImagePixelContent.ComputeHash(
            Frame(2, 2, VisionPixelFormat.Mono8, null, new byte[] { 1, 2 }, new byte[] { 3, 4 }),
            canceled.Token));

        // Cancellation observed between rows never reads the remaining rows.
        using var midHash = new CancellationTokenSource();
        var cancelAfterFirstRow = new TestFrame(Metadata(2, 2, VisionPixelFormat.Mono8, null), rows,
            onRow: row => { if (row == 0) midHash.Cancel(); });
        Assert.Throws<OperationCanceledException>(() =>
            CanonicalImagePixelContent.ComputeHash(cancelAfterFirstRow, midHash.Token));
        Assert.Equal(1, cancelAfterFirstRow.RowReads);

        // A row shorter than width * bytes-per-pixel, including a truncated Mono16 row, is refused.
        var shortMono8 = Frame(4, 2, VisionPixelFormat.Mono8, null,
            new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7 });
        Assert.Equal("CanonicalImagePixelRowCoverageInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalImagePixelContent.ComputeHash(shortMono8)).Message);

        var shortMono16 = Frame(2, 1, VisionPixelFormat.Mono16, 16, new byte[] { 1, 2, 3 });
        Assert.Equal("CanonicalImagePixelRowCoverageInvalid",
            Assert.Throws<InvalidOperationException>(() =>
                CanonicalImagePixelContent.ComputeHash(shortMono16)).Message);
    }

    [Fact]
    [Trait("VerificationId", "V150_C09")]
    public void V150_C09_LargeMono16RowsHashCorrectlyAndObserveCancellation()
    {
        var row = new byte[32_768 * 2];
        for (var offset = 0; offset < row.Length; offset += 2)
        {
            row[offset] = 0xEF;
            row[offset + 1] = 0xBE;
        }

        var wide = Frame(32_768, 2, VisionPixelFormat.Mono16, 16, row, row);
        Assert.Equal(Oracle(Mono16_16_32768x2EnvelopeHex, row, row),
            CanonicalImagePixelContent.ComputeHash(wide));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            CanonicalImagePixelContent.ComputeHash(wide, canceled.Token));
    }

    private static TestFrame Frame(int width, int height, VisionPixelFormat format, int? validBits,
        params byte[][] rows) => new(Metadata(width, height, format, validBits), rows);

    private static TestFrame Frame(int width, int height, VisionPixelFormat format, int? validBits,
        int? strideBytes, params byte[][] rows) => new(Metadata(width, height, format, validBits, strideBytes), rows);

    private static FrameMetadata Metadata(int width, int height, VisionPixelFormat format,
        int? validBits, int? strideBytes = null) => new(
            new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid()), "camera.primary",
            width, height, strideBytes ?? checked(width * BytesPerPixel(format)), format, validBits,
            CapturedAt, new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                1000, 0, new RegionOfInterest(0, 0, width, height), format, validBits, 1000, 0, null));

    private static int BytesPerPixel(VisionPixelFormat format) => format switch
    {
        VisionPixelFormat.Mono8 => 1,
        VisionPixelFormat.Mono16 => 2,
        VisionPixelFormat.Bgr24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    /// <summary>Whole-buffer SHA-256 over a hand-built envelope followed by the listed row bytes.</summary>
    private static string Oracle(string envelopeHex, params byte[][] rows)
    {
        var envelope = Convert.FromHexString(envelopeHex);
        var length = envelope.Length;
        foreach (var row in rows) length += row.Length;
        var bytes = new byte[length];
        envelope.CopyTo(bytes, 0);
        var offset = envelope.Length;
        foreach (var row in rows)
        {
            row.CopyTo(bytes, offset);
            offset += row.Length;
        }

        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class TestFrame : VisionFrame
    {
        private readonly byte[][] _rows;
        private readonly Func<int, bool> _loanActive;
        private readonly Action<int>? _onRow;
        private int _reads;

        internal TestFrame(FrameMetadata metadata, byte[][] rows,
            Func<int, bool>? loanActive = null, Action<int>? onRow = null) : base(metadata)
        {
            _rows = rows;
            _loanActive = loanActive ?? (_ => true);
            _onRow = onRow;
        }

        internal int RowReads => _reads;

        public override bool IsLoanActive => _loanActive(_reads);

        public override ReadOnlySpan<byte> GetRowSpan(int row)
        {
            _reads++;
            _onRow?.Invoke(row);
            return _rows[row];
        }
    }
}
