using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// Codec for the bounded, data-only Recipe Transfer Package envelope.
/// Trust, authorization, persistence, extraction and recipe semantic checks are
/// deliberately outside this type.
/// </summary>
internal static class RecipeTransferPackageCodec
{
    private const int EnvelopeHeaderBytes = 16;
    private const int MemberHeaderBytes = 10;
    private const ushort EnvelopeFlags = 0;
    private const byte CompressionNone = 0;
    private const string ContentHashDomain = "sharpinspect-recipe-transfer-content-v1";
    private const string SignatureInputDomain = "sharpinspect-recipe-transfer-signature-v1";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SIRTPKG1");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryEncode(RecipeTransferPackageManifest manifest,
        ReadOnlySpan<byte> recipeJson, ReadOnlySpan<byte> signature,
        out byte[] packageBytes, out string reasonCode)
    {
        packageBytes = Array.Empty<byte>();
        try
        {
            packageBytes = Encode(manifest, recipeJson, signature);
            reasonCode = "Accepted";
            return true;
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            reasonCode = Reason(exception);
            return false;
        }
    }

    internal static byte[] Encode(RecipeTransferPackageManifest manifest,
        ReadOnlySpan<byte> recipeJson, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateRecipeJson(recipeJson);
        ValidateRecipeMember(manifest, recipeJson);
        if (signature.Length != RecipeTransferPackageLimits.SignatureBytes)
            throw new InvalidDataException("RecipeTransferSignatureLengthInvalid");
        if (!string.Equals(manifest.Signature.Scheme, RecipeTransferPackageLimits.SignatureScheme,
                StringComparison.Ordinal))
            throw new InvalidDataException("RecipeTransferSignatureSchemeUnsupported");

        var finalized = FinalizeManifest(manifest, recipeJson);
        var manifestBytes = CanonicalManifestBytes(finalized);
        if (manifestBytes.Length > RecipeTransferPackageLimits.MaximumManifestBytes)
            throw new InvalidDataException("RecipeTransferManifestTooLarge");

        var recipeBytes = recipeJson.ToArray();
        var signatureBytes = signature.ToArray();
        var packageLength = checked(EnvelopeHeaderBytes +
            MemberEnvelopeBytes(RecipeTransferPackageLimits.ManifestMemberPath, manifestBytes.Length) +
            MemberEnvelopeBytes(RecipeTransferPackageLimits.RecipeMemberPath, recipeBytes.Length) +
            MemberEnvelopeBytes(RecipeTransferPackageLimits.SignatureMemberPath, signatureBytes.Length));
        if (packageLength > RecipeTransferPackageLimits.MaximumPackageBytes)
            throw new InvalidDataException("RecipeTransferPackageTooLarge");

        using var stream = new MemoryStream(packageLength);
        stream.Write(Magic);
        WriteUInt16(stream, RecipeTransferPackageLimits.CurrentFormatVersion);
        WriteUInt16(stream, EnvelopeFlags);
        WriteUInt16(stream, RecipeTransferPackageLimits.EnvelopeMemberCount);
        WriteUInt16(stream, 0);
        WriteMember(stream, RecipeTransferPackageLimits.ManifestMemberPath, manifestBytes);
        WriteMember(stream, RecipeTransferPackageLimits.RecipeMemberPath, recipeBytes);
        WriteMember(stream, RecipeTransferPackageLimits.SignatureMemberPath, signatureBytes);
        return stream.ToArray();
    }

    internal static RecipeTransferPackageManifest FinalizeManifest(
        RecipeTransferPackageManifest manifest, ReadOnlySpan<byte> recipeJson)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateRecipeJson(recipeJson);
        ValidateRecipeMember(manifest, recipeJson);
        var expected = ComputeContentHash(manifest, recipeJson);
        if (string.Equals(manifest.ContentHash, RecipeTransferPackageManifest.ZeroContentHash,
                StringComparison.Ordinal))
            return manifest.WithContentHash(expected);
        if (!string.Equals(manifest.ContentHash, expected, StringComparison.Ordinal))
            throw new InvalidDataException("RecipeTransferPackageHashMismatch");
        return manifest;
    }

    internal static string ComputeContentHash(RecipeTransferPackageManifest manifest,
        ReadOnlySpan<byte> recipeJson)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateRecipeJson(recipeJson);
        ValidateRecipeMember(manifest, recipeJson);
        var zeroManifest = manifest.WithContentHash(RecipeTransferPackageManifest.ZeroContentHash);
        var manifestBytes = CanonicalManifestBytes(zeroManifest);
        using var stream = new MemoryStream(manifestBytes.Length + recipeJson.Length + 96);
        WriteDomain(stream, ContentHashDomain);
        WriteLengthAndBytes(stream, manifestBytes);
        WriteLengthAndBytes(stream, Encoding.UTF8.GetBytes(RecipeTransferPackageLimits.RecipeMemberPath));
        WriteLengthAndBytes(stream, recipeJson);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal static byte[] GetSignatureInput(RecipeTransferPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.Equals(manifest.ContentHash, RecipeTransferPackageManifest.ZeroContentHash,
                StringComparison.Ordinal))
            throw new InvalidDataException("RecipeTransferPackageHashRequired");
        var manifestBytes = CanonicalManifestBytes(manifest);
        using var stream = new MemoryStream(manifestBytes.Length + 128);
        WriteDomain(stream, SignatureInputDomain);
        WriteLengthAndBytes(stream, manifestBytes);
        WriteLengthAndBytes(stream, Encoding.UTF8.GetBytes(manifest.ContentHash));
        return stream.ToArray();
    }

    internal static bool TryRead(ReadOnlyMemory<byte> packageBytes,
        out RecipeTransferPackage? package, out string reasonCode)
    {
        package = null;
        try
        {
            package = Read(packageBytes.Span);
            reasonCode = "Accepted";
            return true;
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            reasonCode = Reason(exception);
            return false;
        }
    }

    internal static bool TryVerifySignature(RecipeTransferPackage package,
        ECDsa trustedPublicKey, out string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(trustedPublicKey);
        try
        {
            if (!string.Equals(package.Manifest.Signature.Scheme,
                    RecipeTransferPackageLimits.SignatureScheme, StringComparison.Ordinal))
            {
                reasonCode = "RecipeTransferSignatureSchemeUnsupported";
                return false;
            }
            if (package.SignatureBytes.Length != RecipeTransferPackageLimits.SignatureBytes)
            {
                reasonCode = "RecipeTransferSignatureLengthInvalid";
                return false;
            }
            var parameters = trustedPublicKey.ExportParameters(false);
            if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                reasonCode = "RecipeTransferTrustedKeyCurveInvalid";
                return false;
            }
            var valid = trustedPublicKey.VerifyData(GetSignatureInput(package.Manifest),
                package.SignatureBytes, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            reasonCode = valid ? "Accepted" : "RecipeTransferSignatureInvalid";
            return valid;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or InvalidDataException)
        {
            reasonCode = Reason(exception);
            return false;
        }
    }

    private static RecipeTransferPackage Read(ReadOnlySpan<byte> input)
    {
        if (input.Length < EnvelopeHeaderBytes)
            throw new InvalidDataException("RecipeTransferEnvelopeTruncated");
        if (input.Length > RecipeTransferPackageLimits.MaximumPackageBytes)
            throw new InvalidDataException("RecipeTransferPackageTooLarge");
        if (!input[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("RecipeTransferMagicInvalid");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(8, 2));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(10, 2));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(12, 2));
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(14, 2));
        if (version != RecipeTransferPackageLimits.CurrentFormatVersion)
            throw new InvalidDataException("RecipeTransferFormatVersionUnsupported");
        if (flags != EnvelopeFlags || reserved != 0)
            throw new InvalidDataException("RecipeTransferEnvelopeFlagsInvalid");
        if (count != RecipeTransferPackageLimits.EnvelopeMemberCount)
            throw new InvalidDataException("RecipeTransferMemberCountInvalid");

        byte[]? manifestBytes = null;
        byte[]? recipeBytes = null;
        byte[]? signatureBytes = null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = EnvelopeHeaderBytes;
        for (var index = 0; index < count; index++)
        {
            if (input.Length - offset < MemberHeaderBytes)
                throw new InvalidDataException("RecipeTransferMemberHeaderTruncated");
            var pathLength = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(offset, 2));
            var memberFlags = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(offset + 2, 2));
            var compression = input[offset + 4];
            var memberReserved = input[offset + 5];
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(input.Slice(offset + 6, 4));
            offset += MemberHeaderBytes;
            if (pathLength == 0 || pathLength > 128 || memberFlags != 0 || compression != CompressionNone ||
                memberReserved != 0 || payloadLength < 0)
                throw new InvalidDataException("RecipeTransferMemberHeaderInvalid");
            if (input.Length - offset < pathLength)
                throw new InvalidDataException("RecipeTransferMemberPathTruncated");
            string path;
            try
            {
                path = StrictUtf8.GetString(input.Slice(offset, pathLength));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("RecipeTransferMemberPathInvalid", exception);
            }
            offset += pathLength;
            ValidateEnvelopePath(path);
            if (!paths.Add(path))
                throw new InvalidDataException("RecipeTransferMemberDuplicate");
            var maximumLength = path switch
            {
                RecipeTransferPackageLimits.ManifestMemberPath => RecipeTransferPackageLimits.MaximumManifestBytes,
                RecipeTransferPackageLimits.RecipeMemberPath => RecipeTransferPackageLimits.MaximumRecipeJsonBytes,
                RecipeTransferPackageLimits.SignatureMemberPath => RecipeTransferPackageLimits.SignatureBytes,
                _ => 0
            };
            if (maximumLength == 0 || payloadLength > maximumLength ||
                path == RecipeTransferPackageLimits.SignatureMemberPath &&
                payloadLength != RecipeTransferPackageLimits.SignatureBytes)
                throw new InvalidDataException("RecipeTransferMemberLengthInvalid");
            if (input.Length - offset < payloadLength)
                throw new InvalidDataException("RecipeTransferMemberPayloadTruncated");
            var payload = input.Slice(offset, payloadLength).ToArray();
            offset += payloadLength;
            switch (path)
            {
                case RecipeTransferPackageLimits.ManifestMemberPath:
                    manifestBytes = payload;
                    break;
                case RecipeTransferPackageLimits.RecipeMemberPath:
                    recipeBytes = payload;
                    break;
                case RecipeTransferPackageLimits.SignatureMemberPath:
                    signatureBytes = payload;
                    break;
            }
        }
        if (offset != input.Length || manifestBytes is null || recipeBytes is null || signatureBytes is null)
            throw new InvalidDataException("RecipeTransferEnvelopeMembersIncomplete");

        var manifest = ParseManifest(manifestBytes);
        var canonicalManifest = CanonicalManifestBytes(manifest);
        if (!canonicalManifest.AsSpan().SequenceEqual(manifestBytes))
            throw new InvalidDataException("RecipeTransferManifestNonCanonical");
        ValidateRecipeJson(recipeBytes);
        ValidateRecipeMember(manifest, recipeBytes);
        if (string.Equals(manifest.ContentHash, RecipeTransferPackageManifest.ZeroContentHash,
                StringComparison.Ordinal) ||
            !string.Equals(ComputeContentHash(manifest, recipeBytes), manifest.ContentHash,
                StringComparison.Ordinal))
            throw new InvalidDataException("RecipeTransferPackageHashMismatch");
        return new RecipeTransferPackage(manifest, recipeBytes, signatureBytes);
    }

    private static RecipeTransferPackageManifest ParseManifest(byte[] bytes)
    {
        ValidateJson(bytes, true);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = RecipeTransferPackageLimits.MaximumJsonDepth,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        var root = document.RootElement;
        RequireObjectFields(root, "formatVersion", "canonicalizationVersion", "packageId", "source",
            "exportedAtUtc", "signature", "dependencies", "members", "contentHash");
        var formatVersion = RequiredInt(root, "formatVersion");
        var canonicalizationVersion = RequiredInt(root, "canonicalizationVersion");
        if (formatVersion != RecipeTransferPackageLimits.CurrentFormatVersion ||
            canonicalizationVersion != RecipeTransferPackageLimits.CurrentCanonicalizationVersion)
            throw new InvalidDataException("RecipeTransferManifestVersionUnsupported");
        if (!Guid.TryParseExact(RequiredString(root, "packageId"), "D", out var packageId) || packageId == Guid.Empty)
            throw new InvalidDataException("RecipeTransferPackageIdentityInvalid");

        var sourceElement = RequiredObject(root, "source");
        RequireObjectFields(sourceElement, "sourceId", "recipeKey", "revision", "revisionContentHash",
            "lifecycle", "sourceStationId");
        if (!Guid.TryParseExact(RequiredString(sourceElement, "sourceId"), "D", out var sourceId))
            throw new InvalidDataException("RecipeTransferSourceIdentityInvalid");
        if (!Enum.TryParse<RecipeTransferSourceLifecycle>(RequiredString(sourceElement, "lifecycle"),
                ignoreCase: false, out var lifecycle) || !Enum.IsDefined(lifecycle))
            throw new InvalidDataException("RecipeTransferSourceLifecycleInvalid");
        var source = new RecipeTransferSource(sourceId, RequiredString(sourceElement, "recipeKey"),
            RequiredLong(sourceElement, "revision"), RequiredString(sourceElement, "revisionContentHash"),
            lifecycle, OptionalString(sourceElement, "sourceStationId"));
        if (!DateTimeOffset.TryParse(RequiredString(root, "exportedAtUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exportedAtUtc) || exportedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("RecipeTransferExportTimeInvalid");

        var signatureElement = RequiredObject(root, "signature");
        RequireObjectFields(signatureElement, "keyId", "scope", "scheme");
        var signature = new RecipeTransferSignatureInfo(RequiredString(signatureElement, "keyId"),
            RequiredString(signatureElement, "scope"), RequiredString(signatureElement, "scheme"));

        var dependenciesElement = RequiredArray(root, "dependencies");
        if (dependenciesElement.GetArrayLength() > RecipeTransferPackageLimits.MaximumDependencyCount)
            throw new InvalidDataException("RecipeTransferDependencyCountExceeded");
        var dependencies = new List<RecipeTransferDependency>(dependenciesElement.GetArrayLength());
        foreach (var dependencyElement in dependenciesElement.EnumerateArray())
        {
            RequireObjectFields(dependencyElement, "kind", "id", "version", "contentHash");
            dependencies.Add(new RecipeTransferDependency(RequiredString(dependencyElement, "kind"),
                new RecipeContractReference(RequiredString(dependencyElement, "id"),
                    RequiredString(dependencyElement, "version"), RequiredString(dependencyElement, "contentHash"))));
        }

        var membersElement = RequiredArray(root, "members");
        if (membersElement.GetArrayLength() != RecipeTransferPackageLimits.MaximumManifestDataMemberCount)
            throw new InvalidDataException("RecipeTransferMemberSetInvalid");
        var members = new List<RecipeTransferMemberManifest>(membersElement.GetArrayLength());
        foreach (var memberElement in membersElement.EnumerateArray())
        {
            RequireObjectFields(memberElement, "relativePath", "length", "contentHash");
            members.Add(new RecipeTransferMemberManifest(RequiredString(memberElement, "relativePath"),
                RequiredInt(memberElement, "length"), RequiredString(memberElement, "contentHash")));
        }
        return new RecipeTransferPackageManifest(packageId, source, exportedAtUtc, signature, dependencies,
            members, RequiredString(root, "contentHash"));
    }

    private static byte[] CanonicalManifestBytes(RecipeTransferPackageManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteNumber("canonicalizationVersion", manifest.CanonicalizationVersion);
            writer.WriteString("packageId", manifest.PackageId.ToString("D"));
            writer.WriteStartObject("source");
            writer.WriteString("sourceId", manifest.Source.SourceId.ToString("D"));
            writer.WriteString("recipeKey", manifest.Source.RecipeKey);
            writer.WriteNumber("revision", manifest.Source.Revision);
            writer.WriteString("revisionContentHash", manifest.Source.RevisionContentHash);
            writer.WriteString("lifecycle", manifest.Source.Lifecycle.ToString());
            if (manifest.Source.SourceStationId is null) writer.WriteNull("sourceStationId");
            else writer.WriteString("sourceStationId", manifest.Source.SourceStationId);
            writer.WriteEndObject();
            writer.WriteString("exportedAtUtc", manifest.ExportedAtUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartObject("signature");
            writer.WriteString("keyId", manifest.Signature.KeyId);
            writer.WriteString("scope", manifest.Signature.Scope);
            writer.WriteString("scheme", manifest.Signature.Scheme);
            writer.WriteEndObject();
            writer.WriteStartArray("dependencies");
            foreach (var dependency in manifest.Dependencies
                         .OrderBy(item => item.Kind, StringComparer.Ordinal)
                         .ThenBy(item => item.Contract.Id, StringComparer.Ordinal)
                         .ThenBy(item => item.Contract.Version, StringComparer.Ordinal)
                         .ThenBy(item => item.Contract.ContentHash, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", dependency.Kind);
                writer.WriteString("id", dependency.Contract.Id);
                writer.WriteString("version", dependency.Contract.Version);
                writer.WriteString("contentHash", dependency.Contract.ContentHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("members");
            foreach (var member in manifest.Members.OrderBy(item => item.RelativePath,
                         StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", member.RelativePath);
                writer.WriteNumber("length", member.Length);
                writer.WriteString("contentHash", member.ContentHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("contentHash", manifest.ContentHash);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void ValidateRecipeMember(RecipeTransferPackageManifest manifest,
        ReadOnlySpan<byte> recipeJson)
    {
        if (manifest.Members.Count != RecipeTransferPackageLimits.MaximumManifestDataMemberCount)
            throw new InvalidDataException("RecipeTransferMemberSetInvalid");
        var member = manifest.Members[0];
        if (!string.Equals(member.RelativePath, RecipeTransferPackageLimits.RecipeMemberPath,
                StringComparison.Ordinal) || member.Length != recipeJson.Length ||
            !string.Equals(member.ContentHash, Convert.ToHexString(SHA256.HashData(recipeJson)),
                StringComparison.Ordinal))
            throw new InvalidDataException("RecipeTransferMemberHashMismatch");
    }

    private static void ValidateRecipeJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes.Length > RecipeTransferPackageLimits.MaximumRecipeJsonBytes)
            throw new InvalidDataException("RecipeTransferRecipeJsonLengthInvalid");
        ValidateJson(bytes, true);
    }

    private static void ValidateJson(ReadOnlySpan<byte> bytes, bool objectRoot)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = RecipeTransferPackageLimits.MaximumJsonDepth
            });
            var containers = new Stack<HashSet<string>?>();
            var hasToken = false;
            var rootCompleted = false;
            while (reader.Read())
            {
                if (!hasToken)
                {
                    hasToken = true;
                    if (objectRoot && reader.TokenType != JsonTokenType.StartObject)
                        throw new InvalidDataException("RecipeTransferRecipeJsonObjectRequired");
                }
                else if (rootCompleted)
                {
                    throw new InvalidDataException("RecipeTransferJsonTrailingContent");
                }
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        containers.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        containers.Push(null);
                        break;
                    case JsonTokenType.PropertyName:
                        if (containers.Count == 0 || containers.Peek() is not { } properties ||
                            !properties.Add(reader.GetString() ?? throw new InvalidDataException(
                                "RecipeTransferJsonPropertyInvalid")))
                            throw new InvalidDataException("RecipeTransferJsonDuplicateProperty");
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (containers.Count == 0) throw new InvalidDataException("RecipeTransferJsonStructureInvalid");
                        containers.Pop();
                        if (containers.Count == 0) rootCompleted = true;
                        break;
                }
            }
            if (!hasToken || containers.Count != 0 || !rootCompleted)
                throw new InvalidDataException("RecipeTransferJsonStructureInvalid");
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = RecipeTransferPackageLimits.MaximumJsonDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            if (objectRoot && document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("RecipeTransferRecipeJsonObjectRequired");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("RecipeTransferJsonInvalid", exception);
        }
    }

    private static void ValidateEnvelopePath(string path)
    {
        if (path.Length == 0 || path.Length > 128 || path.Contains('\0') || path.Contains(':') ||
            path.Contains('/') || path.Contains('\\') || path is "." or ".." ||
            path.StartsWith(".", StringComparison.Ordinal))
            throw new InvalidDataException("RecipeTransferMemberPathInvalid");
        if (path != RecipeTransferPackageLimits.ManifestMemberPath &&
            path != RecipeTransferPackageLimits.RecipeMemberPath &&
            path != RecipeTransferPackageLimits.SignatureMemberPath)
            throw new InvalidDataException("RecipeTransferMemberPathNotAllowed");
    }

    private static void RequireObjectFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("RecipeTransferJsonObjectRequired");
        var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("RecipeTransferManifestPropertyInvalid");
        }
        if (seen.Count != allowed.Count)
            throw new InvalidDataException("RecipeTransferManifestPropertyMissing");
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("RecipeTransferJsonObjectRequired");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("RecipeTransferJsonArrayRequired");
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("RecipeTransferJsonStringRequired");
        return value.GetString() ?? throw new InvalidDataException("RecipeTransferJsonStringRequired");
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind == JsonValueKind.Null ? null : RequiredString(parent, name);
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (!value.TryGetInt32(out var result)) throw new InvalidDataException("RecipeTransferJsonIntegerRequired");
        return result;
    }

    private static long RequiredLong(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        if (!value.TryGetInt64(out var result)) throw new InvalidDataException("RecipeTransferJsonIntegerRequired");
        return result;
    }

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException("RecipeTransferManifestPropertyMissing");

    private static void WriteMember(Stream stream, string path, ReadOnlySpan<byte> payload)
    {
        var pathBytes = StrictUtf8.GetBytes(path);
        if (pathBytes.Length > ushort.MaxValue) throw new InvalidDataException("RecipeTransferMemberPathInvalid");
        WriteUInt16(stream, checked((ushort)pathBytes.Length));
        WriteUInt16(stream, 0);
        stream.WriteByte(CompressionNone);
        stream.WriteByte(0);
        WriteInt32(stream, payload.Length);
        stream.Write(pathBytes);
        stream.Write(payload);
    }

    private static int MemberEnvelopeBytes(string path, int payloadLength) =>
        checked(MemberHeaderBytes + StrictUtf8.GetByteCount(path) + payloadLength);

    private static void WriteDomain(Stream stream, string domain) =>
        WriteLengthAndBytes(stream, Encoding.UTF8.GetBytes(domain));

    private static void WriteLengthAndBytes(Stream stream, ReadOnlySpan<byte> bytes)
    {
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static bool IsValidationException(Exception exception) => exception is ArgumentException or
        InvalidDataException or JsonException or DecoderFallbackException or OverflowException or
        FormatException or CryptographicException;

    private static string Reason(Exception exception) => exception.Message is { Length: > 0 } message
        ? message : "RecipeTransferPackageInvalid";
}
