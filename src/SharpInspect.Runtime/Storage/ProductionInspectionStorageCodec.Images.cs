using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

internal static partial class ProductionInspectionStorageCodec
{
    private static void WriteCapturePolicy(BinaryWriter writer, EvidenceCapturePolicySnapshot value)
    {
        WriteString(writer, value.Id, 256);
        WriteString(writer, value.Version, 256);
        writer.Write((byte)value.Mode);
        WriteString(writer, value.ContentHash, 64);
    }

    private static EvidenceCapturePolicySnapshot ReadCapturePolicy(BinaryReader reader)
    {
        var value = new EvidenceCapturePolicySnapshot(ReadRequiredImageString(reader, 256),
            ReadRequiredImageString(reader, 256), ReadEnum<EvidenceCaptureMode>(reader.ReadByte(),
                "ProductionImageEvidencePolicyModeInvalid"));
        RequireHash(value.ContentHash, ReadRequiredImageString(reader, 64), "ProductionImageEvidencePolicyHashMismatch");
        return value;
    }

    private static void WriteImageEvidence(BinaryWriter writer, ProductionImageEvidenceSnapshot value)
    {
        writer.Write((byte)value.State);
        WriteString(writer, value.ReasonCode, 256);
        writer.Write(value.Work is not null);
        if (value.Work is { } work)
        {
            WriteGuid(writer, work.WorkId);
            WriteImageManifest(writer, work.Manifest);
            WriteString(writer, work.ContentHash, 64);
        }
        WriteString(writer, value.ContentHash, 64);
    }

    private static ProductionImageEvidenceSnapshot ReadImageEvidence(BinaryReader reader)
    {
        var state = ReadEnum<ProductionImageEvidenceState>(reader.ReadByte(), "ProductionImageEvidenceStateInvalid");
        var reason = ReadRequiredImageString(reader, 256);
        PendingImageFinalizationWork? work = null;
        if (reader.ReadBoolean())
        {
            var id = ReadGuid(reader);
            work = new(id, ReadImageManifest(reader));
            RequireHash(work.ContentHash, ReadRequiredImageString(reader, 64), "ProductionImageWorkHashMismatch");
        }
        var value = new ProductionImageEvidenceSnapshot(state, reason, work);
        RequireHash(value.ContentHash, ReadRequiredImageString(reader, 64), "ProductionImageEvidenceHashMismatch");
        return value;
    }

    internal static byte[] EncodeImageManifest(PendingImageManifest value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            WriteString(writer, "SI-PENDING-IMAGE-1", 32);
            WriteImageManifest(writer, value);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Reconstructs the exact frozen pending manifest from its stored row payload. The caller
    /// re-proves the decoded content hash against the stored column and the Core projection.
    /// </summary>
    internal static PendingImageManifest DecodeImageManifest(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        if (ReadString(reader, 32) != "SI-PENDING-IMAGE-1") throw Corrupt("ProductionImageEnvelopeInvalid");
        var value = ReadImageManifest(reader);
        if (stream.Position != stream.Length) throw Corrupt("ProductionImageEnvelopeTrailingBytes");
        return value;
    }

    internal static byte[] EncodeImageWork(PendingImageFinalizationWork value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            WriteString(writer, "SI-IMAGE-WORK-1", 32);
            WriteGuid(writer, value.WorkId);
            WriteString(writer, value.Kind, 32);
            WriteString(writer, value.State, 32);
            WriteImageManifest(writer, value.Manifest);
            WriteString(writer, value.ContentHash, 64);
        }
        return stream.ToArray();
    }

    /// <summary>Reconstructs the exact frozen pending work from its stored row payload.</summary>
    internal static PendingImageFinalizationWork DecodeImageWork(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        if (ReadString(reader, 32) != "SI-IMAGE-WORK-1") throw Corrupt("ProductionImageEnvelopeInvalid");
        var id = ReadGuid(reader);
        if (ReadString(reader, 32) != "FinalizePng" || ReadString(reader, 32) != "Pending")
            throw Corrupt("ProductionImageWorkKindInvalid");
        var work = new PendingImageFinalizationWork(id, ReadImageManifest(reader));
        RequireHash(work.ContentHash, ReadRequiredImageString(reader, 64), "ProductionImageWorkHashMismatch");
        if (stream.Position != stream.Length) throw Corrupt("ProductionImageEnvelopeTrailingBytes");
        return work;
    }

    private static void WriteImageManifest(BinaryWriter writer, PendingImageManifest value)
    {
        WriteGuid(writer, value.ManifestId);
        WriteGuid(writer, value.InspectionId);
        WriteString(writer, value.AdmissionContentHash, 64);
        WriteString(writer, value.EvidencePolicyContentHash, 64);
        WriteGuid(writer, value.StageId);
        WriteGuid(writer, value.InputLeaseId);
        WriteString(writer, value.StageRootBindingHash, 64);
        WriteString(writer, value.StageFileName, 128);
        writer.Write(value.Width);
        writer.Write(value.Height);
        writer.Write((byte)value.PixelFormat);
        WriteNullableInt32(writer, value.ValidBits);
        WriteString(writer, value.HashScheme, 128);
        writer.Write(value.HashSchemeVersion);
        WriteString(writer, value.CanonicalPixelHash, 64);
        writer.Write(value.CanonicalByteLength);
        WriteString(writer, value.InputMetadataHash, 64);
        WriteString(writer, value.InputProvenanceHash, 64);
        WriteString(writer, value.TracePolicySnapshotHash, 64);
        WriteString(writer, value.RetentionRuleHash, 64);
        writer.Write(value.CreatedAtUtc.UtcTicks);
        WriteString(writer, value.ContentHash, 64);
    }

    private static PendingImageManifest ReadImageManifest(BinaryReader reader)
    {
        var id = ReadGuid(reader);
        var inspection = ReadGuid(reader);
        var admission = ReadRequiredImageString(reader, 64);
        var policy = ReadRequiredImageString(reader, 64);
        var stage = ReadGuid(reader);
        var lease = ReadGuid(reader);
        var root = ReadRequiredImageString(reader, 64);
        var fileName = ReadRequiredImageString(reader, 128);
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var format = ReadEnum<VisionPixelFormat>(reader.ReadByte(), "ProductionImageFormatInvalid");
        var bits = ReadNullableInt32(reader);
        if (ReadRequiredImageString(reader, 128) != CanonicalImagePixelContent.HashScheme ||
            reader.ReadInt32() != CanonicalImagePixelContent.HashSchemeVersion)
            throw Corrupt("ProductionImageHashSchemeUnsupported");
        var value = new PendingImageManifest(id, inspection, admission, policy, stage, lease, root, fileName,
            width, height, format, bits, ReadRequiredImageString(reader, 64), reader.ReadInt64(),
            ReadRequiredImageString(reader, 64), ReadRequiredImageString(reader, 64),
            ReadRequiredImageString(reader, 64), ReadRequiredImageString(reader, 64),
            ReadUtc(reader, "ProductionImageCreatedAtInvalid"));
        RequireHash(value.ContentHash, ReadRequiredImageString(reader, 64), "ProductionImageManifestHashMismatch");
        return value;
    }

    private static string ReadRequiredImageString(BinaryReader reader, int limit) =>
        ReadString(reader, limit) ?? throw Corrupt("ProductionImageRequiredFieldMissing");
}
