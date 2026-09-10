using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeTransferPackageCodecTests
{
    private static readonly byte[] RecipeJson = Encoding.UTF8.GetBytes(
        "{\"algorithm\":\"vision.basic\",\"configuration\":{\"threshold\":0.5}}");

    [Fact]
    public void V138_P01_SignedPackageRoundTripsAndCopiesBytes()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var packageBytes = CreateSignedPackage(signer, out var manifest);

        Assert.True(RecipeTransferPackageCodec.TryRead(packageBytes, out var package, out var readReason), readReason);
        Assert.NotNull(package);
        Assert.Equal(manifest.ContentHash, package!.Manifest.ContentHash);
        Assert.True(RecipeTransferPackageCodec.TryVerifySignature(package, signer, out var verifyReason), verifyReason);

        var recipeCopy = package.GetRecipeJsonBytes();
        recipeCopy[0] ^= 0x20;
        Assert.NotEqual(recipeCopy[0], package.GetRecipeJsonBytes()[0]);
        var signatureCopy = package.GetSignatureBytes();
        signatureCopy[0] ^= 0x20;
        Assert.NotEqual(signatureCopy[0], package.GetSignatureBytes()[0]);
    }

    [Fact]
    public void V138_P02_ContentAndMemberTamperingIsRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var packageBytes = CreateSignedPackage(signer, out var manifest);
        var tamperedRecipe = RewriteMember(packageBytes, RecipeTransferPackageLimits.RecipeMemberPath,
            value =>
            {
                value[0] = (byte)'[';
                return value;
            });
        Assert.False(RecipeTransferPackageCodec.TryRead(tamperedRecipe, out _, out var recipeReason));
        Assert.Contains("RecipeTransfer", recipeReason, StringComparison.Ordinal);

        var tamperedManifest = RewriteMember(packageBytes, RecipeTransferPackageLimits.ManifestMemberPath,
            value => ReplaceAscii(value, manifest.ContentHash,
                "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"));
        Assert.False(RecipeTransferPackageCodec.TryRead(tamperedManifest, out _, out var manifestReason));
        Assert.Contains("RecipeTransfer", manifestReason, StringComparison.Ordinal);
    }

    [Fact]
    public void V138_P03_WrongKeyAndSignatureBytesAreRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var packageBytes = CreateSignedPackage(signer, out _);
        Assert.True(RecipeTransferPackageCodec.TryRead(packageBytes, out var package, out var readReason), readReason);
        Assert.NotNull(package);
        Assert.False(RecipeTransferPackageCodec.TryVerifySignature(package!, wrongSigner, out var wrongReason));
        Assert.Equal("RecipeTransferSignatureInvalid", wrongReason);

        var changedSignature = RewriteMember(packageBytes, RecipeTransferPackageLimits.SignatureMemberPath,
            value =>
            {
                value[^1] ^= 0x01;
                return value;
            });
        Assert.True(RecipeTransferPackageCodec.TryRead(changedSignature, out var changedPackage, out var changedReason),
            changedReason);
        Assert.False(RecipeTransferPackageCodec.TryVerifySignature(changedPackage!, signer, out var changedVerifyReason));
        Assert.Equal("RecipeTransferSignatureInvalid", changedVerifyReason);
    }

    [Theory]
    [InlineData("../recipe.json")]
    [InlineData("/recipe.json")]
    [InlineData("C:recipe.json")]
    [InlineData("recipe:json")]
    [InlineData("CON")]
    public void V138_P04_PathAndDeviceNamesAreRejected(string path)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = CreateSignedPackage(signer, out _);
        var members = ReadRawMembers(valid);
        var recipeIndex = members.FindIndex(item => item.Path == RecipeTransferPackageLimits.RecipeMemberPath);
        members[recipeIndex] = members[recipeIndex] with { Path = path };
        Assert.False(RecipeTransferPackageCodec.TryRead(BuildEnvelope(members), out _, out var reason));
        Assert.Contains("RecipeTransfer", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void V138_P05_DuplicateCasePathFlagsCompressionAndTrailingBytesAreRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = CreateSignedPackage(signer, out _);
        var members = ReadRawMembers(valid);
        var recipe = members.Find(item => item.Path == RecipeTransferPackageLimits.RecipeMemberPath)!;

        var duplicate = new List<RawMember>(members)
        {
            recipe with { Path = RecipeTransferPackageLimits.RecipeMemberPath.ToUpperInvariant() }
        };
        Assert.False(RecipeTransferPackageCodec.TryRead(BuildEnvelope(duplicate), out _, out _));

        var flagged = members.Select(item => item.Path == RecipeTransferPackageLimits.RecipeMemberPath
            ? item with { Flags = 1 } : item).ToList();
        Assert.False(RecipeTransferPackageCodec.TryRead(BuildEnvelope(flagged), out _, out _));

        var compressed = members.Select(item => item.Path == RecipeTransferPackageLimits.RecipeMemberPath
            ? item with { Compression = 1 } : item).ToList();
        Assert.False(RecipeTransferPackageCodec.TryRead(BuildEnvelope(compressed), out _, out _));

        var trailing = valid.Concat(new byte[] { 0x44 }).ToArray();
        Assert.False(RecipeTransferPackageCodec.TryRead(trailing, out _, out _));
    }

    [Fact]
    public void V138_P06_VersionCountTruncationAndOversizeHeadersAreRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = CreateSignedPackage(signer, out _);
        var badVersion = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(badVersion.AsSpan(8, 2), 99);
        Assert.False(RecipeTransferPackageCodec.TryRead(badVersion, out _, out var versionReason));
        Assert.Equal("RecipeTransferFormatVersionUnsupported", versionReason);

        var badCount = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(badCount.AsSpan(12, 2), 2);
        Assert.False(RecipeTransferPackageCodec.TryRead(badCount, out _, out _));

        Assert.False(RecipeTransferPackageCodec.TryRead(valid[..^1], out _, out _));
        var oversized = (byte[])valid.Clone();
        var memberOffset = FindMemberHeader(oversized, RecipeTransferPackageLimits.RecipeMemberPath);
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(memberOffset + 6, 4),
            RecipeTransferPackageLimits.MaximumRecipeJsonBytes + 1);
        Assert.False(RecipeTransferPackageCodec.TryRead(oversized, out _, out _));
    }

    [Fact]
    public void V138_P07_DuplicatePropertiesAndDeepRecipeJsonAreRejected()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var valid = CreateSignedPackage(signer, out _);
        var duplicateRecipe = RewriteMember(valid, RecipeTransferPackageLimits.RecipeMemberPath,
            _ => Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"));
        Assert.False(RecipeTransferPackageCodec.TryRead(duplicateRecipe, out _, out var duplicateReason));
        Assert.Equal("RecipeTransferJsonDuplicateProperty", duplicateReason);

        var deep = new StringBuilder("{");
        for (var i = 0; i < RecipeTransferPackageLimits.MaximumJsonDepth + 1; i++) deep.Append("\"x\":{");
        deep.Append("\"value\":1");
        for (var i = 0; i < RecipeTransferPackageLimits.MaximumJsonDepth + 1; i++) deep.Append('}');
        deep.Append('}');
        var deepRecipe = RewriteMember(valid, RecipeTransferPackageLimits.RecipeMemberPath,
            _ => Encoding.UTF8.GetBytes(deep.ToString()));
        Assert.False(RecipeTransferPackageCodec.TryRead(deepRecipe, out _, out var deepReason));
        Assert.Contains("RecipeTransfer", deepReason, StringComparison.Ordinal);
    }

    [Fact]
    public void V138_P08_AbandonedAndRetiredSourceClaimsRoundTripWithoutAuthority()
    {
        foreach (var lifecycle in new[] { RecipeTransferSourceLifecycle.Abandoned,
                     RecipeTransferSourceLifecycle.Retired })
        {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var packageBytes = CreateSignedPackage(signer, out var manifest, lifecycle);
            Assert.True(RecipeTransferPackageCodec.TryRead(packageBytes, out var package, out var reason), reason);
            Assert.Equal(lifecycle, package!.Manifest.Source.Lifecycle);
            Assert.Equal(manifest.Source.ContentHash, package.Manifest.Source.ContentHash);
        }
    }

    [Fact]
    public void V138_P09_CollectionsAreCopiedAndInvalidDependencyKindsFailClosed()
    {
        var dependency = new RecipeTransferDependency("Algorithm",
            new RecipeContractReference("vision.basic", "1", Hash(0x21)));
        var dependencies = new List<RecipeTransferDependency> { dependency };
        var manifest = CreateManifest(RecipeJson, dependencies);
        dependencies.Clear();
        Assert.Single(manifest.Dependencies);

        Assert.Throws<ArgumentException>(() => new RecipeTransferDependency("UnknownThing",
            new RecipeContractReference("vision.basic", "1", Hash(0x22))));
    }

    private static byte[] CreateSignedPackage(ECDsa signer, out RecipeTransferPackageManifest manifest,
        RecipeTransferSourceLifecycle lifecycle = RecipeTransferSourceLifecycle.Draft)
    {
        manifest = CreateManifest(RecipeJson, lifecycle: lifecycle);
        manifest = RecipeTransferPackageCodec.FinalizeManifest(manifest, RecipeJson);
        var signature = signer.SignData(RecipeTransferPackageCodec.GetSignatureInput(manifest),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return RecipeTransferPackageCodec.Encode(manifest, RecipeJson, signature);
    }

    private static RecipeTransferPackageManifest CreateManifest(byte[] recipe,
        IEnumerable<RecipeTransferDependency>? dependencies = null,
        RecipeTransferSourceLifecycle lifecycle = RecipeTransferSourceLifecycle.Draft)
    {
        var source = new RecipeTransferSource(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "vision.recipe", 7, Hash(0x10), lifecycle, "station-alpha");
        var signature = new RecipeTransferSignatureInfo("signer-one", "recipe.import");
        var member = new RecipeTransferMemberManifest(RecipeTransferPackageLimits.RecipeMemberPath,
            recipe.Length, Convert.ToHexString(SHA256.HashData(recipe)));
        return new RecipeTransferPackageManifest(Guid.Parse("22222222-2222-2222-2222-222222222222"), source,
            new DateTimeOffset(2026, 9, 10, 1, 2, 3, TimeSpan.Zero), signature,
            dependencies ?? new[] { new RecipeTransferDependency("Algorithm",
                new RecipeContractReference("vision.basic", "1", Hash(0x20))) },
            new[] { member }, RecipeTransferPackageManifest.ZeroContentHash);
    }

    private static string Hash(byte value) => Convert.ToHexString(SHA256.HashData(new[] { value }));

    private sealed record RawMember(string Path, byte[] Payload, ushort Flags = 0,
        byte Compression = 0, byte Reserved = 0);

    private static List<RawMember> ReadRawMembers(byte[] bytes)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        var offset = 16;
        var result = new List<RawMember>(count);
        for (var index = 0; index < count; index++)
        {
            var pathLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2, 2));
            var compression = bytes[offset + 4];
            var reserved = bytes[offset + 5];
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 6, 4));
            offset += 10;
            var path = Encoding.UTF8.GetString(bytes, offset, pathLength);
            offset += pathLength;
            var payload = bytes.AsSpan(offset, length).ToArray();
            offset += length;
            result.Add(new RawMember(path, payload, flags, compression, reserved));
        }
        return result;
    }

    private static byte[] BuildEnvelope(IReadOnlyList<RawMember> members)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("SIRTPKG1"));
        WriteUInt16(stream, 1); WriteUInt16(stream, 0); WriteUInt16(stream, checked((ushort)members.Count));
        WriteUInt16(stream, 0);
        foreach (var member in members)
        {
            var path = Encoding.UTF8.GetBytes(member.Path);
            WriteUInt16(stream, checked((ushort)path.Length)); WriteUInt16(stream, member.Flags);
            stream.WriteByte(member.Compression); stream.WriteByte(member.Reserved);
            WriteInt32(stream, member.Payload.Length); stream.Write(path); stream.Write(member.Payload);
        }
        return stream.ToArray();
    }

    private static byte[] RewriteMember(byte[] package, string path, Func<byte[], byte[]> rewrite)
    {
        var members = ReadRawMembers(package);
        var index = members.FindIndex(member => member.Path == path);
        Assert.True(index >= 0);
        members[index] = members[index] with { Payload = rewrite((byte[])members[index].Payload.Clone()) };
        return BuildEnvelope(members);
    }

    private static byte[] ReplaceAscii(byte[] bytes, string oldValue, string newValue)
    {
        var oldBytes = Encoding.UTF8.GetBytes(oldValue);
        var newBytes = Encoding.UTF8.GetBytes(newValue);
        Assert.Equal(oldBytes.Length, newBytes.Length);
        for (var index = 0; index <= bytes.Length - oldBytes.Length; index++)
        {
            if (!bytes.AsSpan(index, oldBytes.Length).SequenceEqual(oldBytes)) continue;
            newBytes.CopyTo(bytes.AsSpan(index));
            return bytes;
        }
        throw new InvalidOperationException("Test fixture marker not found");
    }

    private static int FindMemberHeader(byte[] bytes, string path)
    {
        foreach (var member in ReadRawMemberOffsets(bytes))
            if (member.Path == path) return member.Offset;
        throw new InvalidOperationException("Test fixture member not found");
    }

    private static IEnumerable<(string Path, int Offset)> ReadRawMemberOffsets(byte[] bytes)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
        var offset = 16;
        for (var index = 0; index < count; index++)
        {
            var header = offset;
            var pathLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 6, 4));
            offset += 10;
            var path = Encoding.UTF8.GetString(bytes, offset, pathLength);
            offset += pathLength + length;
            yield return (path, header);
        }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(buffer, value); stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(buffer, value); stream.Write(buffer);
    }
}
