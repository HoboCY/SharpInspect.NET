using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Strict binary codec for the schema-29 rejection/correction ledger.  The
/// central audit payload is a projection of the event without its own central
/// sequence/hash, so no self-referential digest is possible.
/// </summary>
internal static class PartIdentityStorageCodec
{
    private const int FormatVersion = 1;
    private const int MaximumTextBytes = 16 * 1024;
    private const int MaximumEvidenceBytes = 512 * 1024;

    internal static byte[] EncodeAuditPayload(PartIdentityHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using var writer = NewWriter(stream);
        writer.Write(FormatVersion);
        WriteEvent(writer, value, includeAuditReferences: false, includeContentHash: true);
        return stream.ToArray();
    }

    internal static byte[] Encode(PartIdentityHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using var writer = NewWriter(stream);
        writer.Write(FormatVersion);
        WriteEvent(writer, value, includeAuditReferences: true, includeContentHash: true);
        var bytes = stream.ToArray();
        if (bytes.Length > PartIdentityStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("PartIdentityPayloadTooLarge");
        return bytes;
    }

    internal static PartIdentityHistoryEvent Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > PartIdentityStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("PartIdentityPayloadInvalid");
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        try
        {
            if (reader.ReadInt32() != FormatVersion)
                throw new InvalidOperationException("PartIdentityFormatInvalid");
            var value = ReadEvent(reader, includeAuditReferences: true, includeContentHash: true);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("PartIdentityTrailingBytes");
            return value;
        }
        catch (EndOfStreamException exception)
        { throw new InvalidOperationException("PartIdentityPayloadTruncated", exception); }
        catch (DecoderFallbackException exception)
        { throw new InvalidOperationException("PartIdentityPayloadUtf8Invalid", exception); }
        catch (OverflowException exception)
        { throw new InvalidOperationException("PartIdentityPayloadInvalid", exception); }
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteEvent(BinaryWriter writer, PartIdentityHistoryEvent value,
        bool includeAuditReferences, bool includeContentHash)
    {
        writer.Write(value.Position);
        WriteNullableText(writer, value.PreviousHash);
        WriteGuid(writer, value.EventId);
        writer.Write((byte)value.Kind);
        WriteGuid(writer, value.CorrelationId);
        WriteGuid(writer, value.AttemptId);
        WriteGuid(writer, value.RuntimeEpoch);
        WriteText(writer, value.StationId);
        writer.Write(value.ControllerEpoch);
        writer.Write(value.CycleSequence);
        WriteText(writer, value.EndpointBindingHash);
        WriteEvidence(writer, value.Evidence);
        WriteRejectionContext(writer, value.RejectionContext);
        WriteText(writer, value.ReasonCode);
        WriteNullableGuid(writer, value.InspectionId);
        WriteNullableText(writer, value.AdmissionContentHash);
        WriteNullableText(writer, value.ExpectedPreviousCorrectionHash);
        WriteNullableText(writer, value.OldValue);
        WriteNullableText(writer, value.NewValue);
        WriteNullableGuid(writer, value.ActorPrincipalId);
        WriteNullableGuid(writer, value.ActorSessionId);
        writer.Write(value.AuthorizationRevision);
        WriteNullableGuid(writer, value.StepUpGrantId);
        WriteNullableText(writer, value.AuthorizationTarget);
        writer.Write(value.RecordedAtUtc.UtcTicks);
        WriteNullableInt64(writer, includeAuditReferences ? value.AuditSequence : null);
        WriteNullableText(writer, includeAuditReferences ? value.AuditHash : null);
        WriteNullableInt64(writer, value.CommandAuditSequence);
        WriteNullableText(writer, value.CommandAuditHash);
        WriteNullableInt64(writer, value.AuthorizationAuditSequence);
        WriteNullableText(writer, value.AuthorizationAuditHash);
        if (includeContentHash) WriteText(writer, value.ContentHash);
    }

    private static PartIdentityHistoryEvent ReadEvent(BinaryReader reader,
        bool includeAuditReferences, bool includeContentHash)
    {
        var position = reader.ReadInt64();
        var previous = ReadNullableText(reader);
        var eventId = ReadGuid(reader);
        var kind = (PartIdentityHistoryEventKind)reader.ReadByte();
        var correlation = ReadGuid(reader);
        var attempt = ReadGuid(reader);
        var runtime = ReadGuid(reader);
        var station = ReadText(reader);
        var controllerEpoch = reader.ReadUInt32();
        var cycleSequence = reader.ReadUInt32();
        var endpoint = ReadText(reader);
        var evidence = ReadEvidence(reader);
        var rejectionContext = ReadRejectionContext(reader);
        var reason = ReadText(reader);
        var inspection = ReadNullableGuid(reader);
        var admissionHash = ReadNullableText(reader);
        var previousCorrectionHash = ReadNullableText(reader);
        var oldValue = ReadNullableText(reader);
        var newValue = ReadNullableText(reader);
        var principal = ReadNullableGuid(reader);
        var session = ReadNullableGuid(reader);
        var authorizationRevision = reader.ReadInt64();
        var grant = ReadNullableGuid(reader);
        var target = ReadNullableText(reader);
        var recorded = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var auditSequence = includeAuditReferences ? ReadNullableInt64(reader) ?? 0 : 0;
        var auditHash = includeAuditReferences ? ReadNullableText(reader) : null;
        var commandSequence = ReadNullableInt64(reader) ?? 0;
        var commandHash = ReadNullableText(reader);
        var authorizationSequence = ReadNullableInt64(reader) ?? 0;
        var authorizationHash = ReadNullableText(reader);
        var expectedContentHash = includeContentHash ? ReadText(reader) : null;
        var value = new PartIdentityHistoryEvent(position, previous, eventId, kind, correlation,
            attempt, runtime, station, controllerEpoch, cycleSequence, endpoint, evidence, reason,
            inspection, admissionHash, previousCorrectionHash, oldValue, newValue, principal, session,
            authorizationRevision, grant, target, recorded, auditSequence, auditHash, expectedContentHash,
            commandSequence, commandHash, authorizationSequence, authorizationHash, rejectionContext);
        if (expectedContentHash is not null && !string.Equals(value.ContentHash, expectedContentHash,
                StringComparison.Ordinal))
            throw new InvalidOperationException("PartIdentityContentHashMismatch");
        return value;
    }

    private static void WriteEvidence(BinaryWriter writer, PartIdentityEvidence? evidence)
    {
        writer.Write(evidence is not null);
        if (evidence is null) return;
        writer.Write((byte)evidence.State);
        WriteNullableText(writer, evidence.Value);
        WriteObservation(writer, evidence.Observation);
        WriteText(writer, evidence.ContentHash);
    }

    private static void WriteRejectionContext(BinaryWriter writer,
        PartIdentityRejectionContext? context)
    {
        writer.Write(context is not null);
        if (context is null) return;
        writer.Write(context.Requirement is not null);
        if (context.Requirement is { } requirement)
        {
            writer.Write((byte)requirement.Mode);
            WriteNullableText(writer, requirement.LogicalRole);
            writer.Write(requirement.Format is not null);
            if (requirement.Format is { } format)
            {
                WriteText(writer, format.Id);
                WriteText(writer, format.Version);
                WriteText(writer, format.ContentHash);
            }
        }
        writer.Write(context.Binding is not null);
        if (context.Binding is { } binding) WriteBinding(writer, binding);
        writer.Write(context.Observation is not null);
        if (context.Observation is { } observation) WriteObservation(writer, observation);
        writer.Write(context.ConnectionGeneration);
        WriteBytes(writer, context.RawProofBytes.ToArray(), 4096);
        WriteNullableText(writer, context.RawProofHash);
        WriteNullableText(writer, context.ReadEvidenceHash);
        WriteText(writer, context.ContentHash);
    }

    private static PartIdentityRejectionContext? ReadRejectionContext(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        PartIdentityRequirement? requirement = null;
        if (reader.ReadBoolean())
        {
            var mode = (PartIdentityRequirementMode)reader.ReadByte();
            var logicalRole = ReadNullableText(reader);
            RecipeContractReference? format = null;
            if (reader.ReadBoolean())
                format = new RecipeContractReference(ReadText(reader), ReadText(reader), ReadText(reader));
            requirement = new PartIdentityRequirement(mode, logicalRole, format);
        }
        var binding = reader.ReadBoolean() ? ReadBinding(reader) : null;
        var observation = reader.ReadBoolean() ? ReadObservation(reader, binding) : null;
        var connectionGeneration = reader.ReadInt64();
        var rawProof = ReadBytes(reader, 4096);
        var rawProofHash = ReadNullableText(reader);
        var readEvidenceHash = ReadNullableText(reader);
        var expectedContentHash = ReadText(reader);
        var context = new PartIdentityRejectionContext(requirement, binding, observation,
            connectionGeneration, rawProof, rawProofHash, readEvidenceHash);
        if (!string.Equals(context.ContentHash, expectedContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PartIdentityRejectionContextHashMismatch");
        return context;
    }

    private static void WriteBinding(BinaryWriter writer, PartIdentityProviderBinding binding)
    {
        WriteText(writer, binding.BindingId);
        WriteText(writer, binding.BindingVersion);
        WriteText(writer, binding.LogicalRole);
        writer.Write((byte)binding.SourceKind);
        WriteText(writer, binding.ProviderId);
        WriteText(writer, binding.ProviderVersion);
        WriteText(writer, binding.SourceContractHash);
        WriteText(writer, binding.Format.Id);
        WriteText(writer, binding.Format.Version);
        writer.Write(binding.Format.MinimumLength);
        writer.Write(binding.Format.MaximumLength);
        WriteText(writer, binding.Format.AllowedCharacters);
        WriteNullableText(writer, binding.Format.RequiredPrefix);
        WriteNullableText(writer, binding.Format.RequiredSuffix);
        writer.Write(binding.FreshnessLimit.Ticks);
        writer.Write(binding.LatchTimeout.Ticks);
        writer.Write(binding.MaximumCallsPerCycle);
    }

    private static PartIdentityProviderBinding ReadBinding(BinaryReader reader)
    {
        var bindingId = ReadText(reader);
        var bindingVersion = ReadText(reader);
        var logicalRole = ReadText(reader);
        var sourceKind = (PartIdentityProviderSourceKind)reader.ReadByte();
        var providerId = ReadText(reader);
        var providerVersion = ReadText(reader);
        var sourceContractHash = ReadText(reader);
        var format = new PartIdentityFormat(ReadText(reader), ReadText(reader), reader.ReadInt32(),
            reader.ReadInt32(), ReadText(reader), ReadNullableText(reader), ReadNullableText(reader));
        return new PartIdentityProviderBinding(bindingId, bindingVersion, logicalRole, sourceKind,
            providerId, providerVersion, sourceContractHash, format,
            TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64()),
            reader.ReadInt32());
    }

    private static void WriteObservation(BinaryWriter writer,
        PartIdentityProviderObservation observation)
    {
        WriteBinding(writer, observation.Binding);
        WriteGuid(writer, observation.Cycle.RuntimeEpoch);
        WriteText(writer, observation.Cycle.EndpointBindingHash);
        writer.Write(observation.Cycle.ConnectionGeneration);
        writer.Write(observation.Cycle.ControllerEpoch);
        writer.Write(observation.Cycle.CycleSequence);
        writer.Write((byte)observation.Status);
        WriteNullableText(writer, observation.Value);
        WriteText(writer, observation.ReasonCode);
        writer.Write(observation.SourceSequence);
        writer.Write(observation.ObservedAtUtc.UtcTicks);
        writer.Write(observation.MonotonicTimestamp);
        writer.Write(observation.MonotonicFrequency);
        WriteGuid(writer, observation.SourceEpoch);
        writer.Write(observation.SourceGeneration);
        WriteNullableGuid(writer, observation.StageToken);
        writer.Write(observation.StablePlcSnapshot is not null);
        if (observation.StablePlcSnapshot is { } stable)
        {
            WriteText(writer, stable.SourceContractHash);
            writer.Write(stable.Revision);
            writer.Write(stable.State);
            writer.Write((byte)stable.Status);
            WriteBytes(writer, stable.GetRawUtf8Bytes(), MaximumEvidenceBytes);
            writer.Write(stable.SourceSequence);
            writer.Write(stable.ObservedAtUtc.UtcTicks);
            writer.Write(stable.MonotonicTimestamp);
            writer.Write(stable.MonotonicFrequency);
            WriteText(writer, stable.ReadEvidenceHash);
        }
        WriteText(writer, observation.ContentHash);
    }

    private static PartIdentityProviderObservation ReadObservation(BinaryReader reader,
        PartIdentityProviderBinding? expectedBinding)
    {
        var binding = ReadBinding(reader);
        if (expectedBinding is not null && !string.Equals(binding.ContentHash,
                expectedBinding.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PartIdentityRejectedBindingMismatch");
        var cycle = new PartIdentityCycleBinding(ReadGuid(reader), ReadText(reader),
            reader.ReadInt64(), reader.ReadUInt32(), reader.ReadUInt32());
        var status = (PartIdentityObservationStatus)reader.ReadByte();
        var value = ReadNullableText(reader);
        var reason = ReadText(reader);
        var sequence = reader.ReadInt64();
        var observedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var monotonic = reader.ReadInt64();
        var frequency = reader.ReadInt64();
        var sourceEpoch = ReadGuid(reader);
        var sourceGeneration = reader.ReadInt64();
        var stageToken = ReadNullableGuid(reader);
        PartIdentityStablePlcSnapshot? stable = null;
        if (reader.ReadBoolean())
            stable = new PartIdentityStablePlcSnapshot(cycle, ReadText(reader), reader.ReadUInt32(),
                reader.ReadUInt16(), (PartIdentityObservationStatus)reader.ReadByte(),
                ReadBytes(reader, MaximumEvidenceBytes), reader.ReadInt64(),
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero), reader.ReadInt64(), reader.ReadInt64(),
                ReadText(reader));
        var observation = new PartIdentityProviderObservation(binding, cycle, status, value, reason,
            sequence, observedAt, monotonic, frequency, sourceEpoch, sourceGeneration, stageToken, stable);
        if (!string.Equals(observation.ContentHash, ReadText(reader), StringComparison.Ordinal))
            throw new InvalidOperationException("PartIdentityObservationHashMismatch");
        return observation;
    }

    private static PartIdentityEvidence? ReadEvidence(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var state = (PartIdentityEvidenceState)reader.ReadByte();
        var value = ReadNullableText(reader);
        var observation = ReadObservation(reader, expectedBinding: null);
        var evidence = new PartIdentityEvidence(state, value, observation);
        var expectedHash = ReadText(reader);
        if (!string.Equals(evidence.ContentHash, expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PartIdentityEvidenceHashMismatch");
        return evidence;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static BinaryWriter NewWriter(Stream stream) => new(stream, StrictUtf8, leaveOpen: true);
    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static void WriteNullableGuid(BinaryWriter writer, Guid? value)
    { writer.Write(value is not null); if (value is { } guid) WriteGuid(writer, guid); }
    private static Guid ReadGuid(BinaryReader reader)
    { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); var value = new Guid(bytes); if (value == Guid.Empty) throw new InvalidOperationException("PartIdentityGuidInvalid"); return value; }
    private static Guid? ReadNullableGuid(BinaryReader reader) => reader.ReadBoolean() ? ReadGuid(reader) : null;
    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    { writer.Write(value is not null); if (value is { } number) writer.Write(number); }
    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;
    private static void WriteText(BinaryWriter writer, string? value)
    { if (value is null) throw new ArgumentNullException(nameof(value)); var bytes = StrictUtf8.GetBytes(value); if (bytes.Length > MaximumTextBytes) throw new InvalidOperationException("PartIdentityTextTooLarge"); writer.Write(bytes.Length); writer.Write(bytes); }
    private static void WriteNullableText(BinaryWriter writer, string? value)
    { writer.Write(value is not null); if (value is not null) WriteText(writer, value); }
    private static string ReadText(BinaryReader reader)
    { var length = reader.ReadInt32(); if (length is < 0 or > MaximumTextBytes) throw new InvalidOperationException("PartIdentityTextInvalid"); var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return StrictUtf8.GetString(bytes); }
    private static string? ReadNullableText(BinaryReader reader) => reader.ReadBoolean() ? ReadText(reader) : null;
    private static void WriteBytes(BinaryWriter writer, byte[] bytes, int max)
    { if (bytes.Length > max) throw new InvalidOperationException("PartIdentityBytesTooLarge"); writer.Write(bytes.Length); writer.Write(bytes); }
    private static byte[] ReadBytes(BinaryReader reader, int max)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > max)
            throw new InvalidOperationException("PartIdentityBytesInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}
