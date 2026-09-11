using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Bounded canonical evidence for one PLC-requested recipe activation. The
/// evidence carries the complete immutable request context: the dedicated
/// Recipe Change Handshake request identity, the deployment selection policy and
/// selection map versions that resolved the request, and the exact Released
/// Recipe the map selected. It is decoded evidence only, never authority; only
/// the Runtime can observe the context that produces it.
/// </summary>
internal static class PlcRecipeActivationIdentityCodec
{
    /// <summary>Largest accepted evidence value; the identity envelope reads no more than this.</summary>
    internal const int MaximumEvidenceLength = 2048;

    // "SPC1": domain separated binary evidence, layout version one.
    private static readonly byte[] Magic = { 0x53, 0x50, 0x43, 0x31 };
    private const byte FormatVersion = 1;
    private const string Prefix = "v1.";
    private const int MaximumIdentifierLength = 64;
    private const int HashLength = 32;
    private const int GuidLength = 16;
    private const string InvalidReason = "PlcRecipeActivationEvidenceInvalid";

    internal static string Encode(PlcRecipeActivationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return EncodeCore(context);
    }

    internal static PlcRecipeActivationRequestContext Decode(string evidence)
    {
        try
        {
            return DecodeCore(evidence);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
            DecoderFallbackException or EndOfStreamException or IOException or OverflowException or
            NotSupportedException)
        {
            throw new InvalidOperationException(InvalidReason);
        }
    }

    private static string EncodeCore(PlcRecipeActivationRequestContext context)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteIdentifier(writer, SystemPrincipalId.PlcAdapter);
            WriteIdentifier(writer, SystemPermission.RequestMappedRecipeActivation.ToString());
            writer.Write(context.RuntimeEpoch.ToByteArray());
            WriteHash(writer, context.EndpointContentHash);
            WriteIdentifier(writer, context.ProtocolProfile.Id);
            WriteIdentifier(writer, context.ProtocolProfile.Version);
            WriteHash(writer, context.ProtocolProfile.ContentHash);
            writer.Write(context.ControllerEpoch);
            writer.Write(context.RequestSequence);
            writer.Write(context.SelectionCode);
            WriteIdentifier(writer, context.SelectionPolicy.Id);
            WriteIdentifier(writer, context.SelectionPolicy.Version);
            WriteHash(writer, context.SelectionPolicy.ContentHash);
            WriteIdentifier(writer, context.SelectionMap.Id);
            WriteIdentifier(writer, context.SelectionMap.Version);
            WriteHash(writer, context.SelectionMap.ContentHash);
            WriteIdentifier(writer, context.Candidate.Id);
            WriteIdentifier(writer, context.Candidate.Version);
            WriteHash(writer, context.Candidate.ContentHash);
            writer.Write(context.ReleaseId.ToByteArray());
            WriteHash(writer, context.ReleaseRecordContentHash);
            WriteHash(writer, context.ContentHash);
            WriteHash(writer, context.RequestIdentityHash);
        }

        var encoded = Prefix + ToBase64Url(stream.ToArray());
        if (encoded.Length > MaximumEvidenceLength)
            throw new InvalidOperationException("PlcRecipeActivationEvidenceTooLong");
        return encoded;
    }

    private static PlcRecipeActivationRequestContext DecodeCore(string evidence)
    {
        if (evidence is null || evidence.Length <= Prefix.Length || evidence.Length > MaximumEvidenceLength ||
            !evidence.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException(InvalidReason);
        var payload = evidence[Prefix.Length..];
        var padded = payload.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        var bytes = Convert.FromBase64String(padded);
        // The exact alphabet, padding, and trailing bits are part of the canonical
        // form, so only the one encoding of these bytes is accepted.
        if (!string.Equals(ToBase64Url(bytes), payload, StringComparison.Ordinal))
            throw new InvalidOperationException(InvalidReason);

        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        if (!ReadFixed(reader, Magic.Length).AsSpan().SequenceEqual(Magic) ||
            reader.ReadByte() != FormatVersion)
            throw new InvalidOperationException(InvalidReason);
        if (ReadIdentifier(reader) != SystemPrincipalId.PlcAdapter ||
            ReadIdentifier(reader) != SystemPermission.RequestMappedRecipeActivation.ToString())
            throw new InvalidOperationException(InvalidReason);
        var runtimeEpoch = new Guid(ReadFixed(reader, GuidLength));
        var endpointContentHash = ReadHash(reader);
        var protocolProfile = new RecipeContractReference(ReadIdentifier(reader), ReadIdentifier(reader),
            ReadHash(reader));
        var controllerEpoch = reader.ReadUInt32();
        var requestSequence = reader.ReadUInt32();
        var selectionCode = reader.ReadUInt32();
        var selectionPolicy = new RecipeContractReference(ReadIdentifier(reader), ReadIdentifier(reader),
            ReadHash(reader));
        var selectionMap = new RecipeContractReference(ReadIdentifier(reader), ReadIdentifier(reader),
            ReadHash(reader));
        var candidate = new RecipeReference(ReadIdentifier(reader), ReadIdentifier(reader), ReadHash(reader));
        var releaseId = new Guid(ReadFixed(reader, GuidLength));
        var releaseRecordContentHash = ReadHash(reader);
        var contentHash = ReadHash(reader);
        var requestIdentityHash = ReadHash(reader);
        if (input.Position != input.Length) throw new InvalidOperationException(InvalidReason);

        var context = new PlcRecipeActivationRequestContext(runtimeEpoch, endpointContentHash, protocolProfile,
            controllerEpoch, requestSequence, selectionCode, selectionPolicy, selectionMap, candidate, releaseId,
            releaseRecordContentHash);
        // The carried derived hashes must be exactly the hashes this context
        // derives, and a fixed-size re-encode rejects any non-canonical layout.
        if (!string.Equals(context.ContentHash, contentHash, StringComparison.Ordinal) ||
            !string.Equals(context.RequestIdentityHash, requestIdentityHash, StringComparison.Ordinal) ||
            !string.Equals(EncodeCore(context), evidence, StringComparison.Ordinal))
            throw new InvalidOperationException(InvalidReason);
        return context;
    }

    private static void WriteIdentifier(BinaryWriter writer, string value)
    {
        if (value is null || value.Length is < 1 or > MaximumIdentifierLength)
            throw new InvalidOperationException(InvalidReason);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length != value.Length || bytes.Length > MaximumIdentifierLength)
            throw new InvalidOperationException(InvalidReason);
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadIdentifier(BinaryReader reader)
    {
        var length = reader.ReadByte();
        if (length is < 1 or > MaximumIdentifierLength) throw new InvalidOperationException(InvalidReason);
        var bytes = ReadFixed(reader, length);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void WriteHash(BinaryWriter writer, string value)
    {
        if (value is not { Length: 64 } ||
            !value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new InvalidOperationException(InvalidReason);
        writer.Write(Convert.FromHexString(value));
    }

    private static string ReadHash(BinaryReader reader) => Convert.ToHexString(ReadFixed(reader, HashLength));

    private static byte[] ReadFixed(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new InvalidOperationException(InvalidReason);
        return bytes;
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
