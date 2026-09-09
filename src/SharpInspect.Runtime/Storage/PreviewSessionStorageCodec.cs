using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;

namespace SharpInspect.Runtime.Storage;

/// <summary>Canonical, bounded wire representation for Preview history.</summary>
internal static class PreviewSessionStorageCodec
{
    internal const int MaximumPayloadBytes = PreviewSessionStoreOptions.MaximumPayloadBytesHardLimit;
    private const int Magic = 0x31535050; // PPS1
    private const byte FormatVersion = 1;
    private const int MaximumStringBytes = 256 * 1024;

    internal static byte[] Encode(PreviewSessionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteHeader(writer, value.Header);
            writer.Write(value.Position);
            WriteGuid(writer, value.AttemptId);
            WriteGuid(writer, value.CommandCorrelationId);
            writer.Write((byte)value.CommandKind);
            writer.Write((byte)value.Phase);
            writer.Write((byte)value.Restoration);
            WriteString(writer, value.ReasonCode, MaximumStringBytes);
            writer.Write(value.Terminal);
            WriteGuid(writer, value.ActorPrincipalId);
            WriteGuid(writer, value.ActorSessionId);
            writer.Write(value.ActorAuthorizationRevision);
            WriteString(writer, value.AuthorizationTarget, 64);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            WriteConfiguration(writer, value.Configuration);
            WriteDraft(writer, value.SavedDraft);
            WriteString(writer, value.FrozenSettingsContentHash, 64);
            WriteNullableInt64(writer, value.CommandAuditSequence);
            WriteString(writer, value.CommandAuditHash, 64);
            WriteNullableInt64(writer, value.AuthorizationAuditSequence);
            WriteString(writer, value.AuthorizationAuditHash, 64);
            WriteString(writer, value.PayloadHash, 64);
            writer.Write(value.AuditSequence);
            WriteString(writer, value.AuditHash, 64);
            WriteString(writer, value.ContentHash, 64);
        }
        if (stream.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("PreviewSessionPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static PreviewSessionEvent Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("PreviewSessionPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("PreviewSessionPayloadVersionUnsupported");
            var header = ReadHeader(reader);
            var position = reader.ReadInt64();
            var attemptId = ReadGuid(reader);
            var correlationId = ReadGuid(reader);
            var commandKind = ReadEnum<AuditedCommandKind>(reader.ReadByte(),
                "PreviewSessionCommandKindInvalid");
            var phase = ReadEnum<PreviewSessionPhase>(reader.ReadByte(), "PreviewSessionPhaseInvalid");
            var restoration = ReadEnum<PreviewRestorationState>(reader.ReadByte(),
                "PreviewSessionRestorationInvalid");
            var reason = ReadString(reader, MaximumStringBytes) ??
                throw new InvalidOperationException("PreviewSessionReasonMissing");
            var terminal = reader.ReadBoolean();
            var actorPrincipalId = ReadGuid(reader);
            var actorSessionId = ReadGuid(reader);
            var actorRevision = reader.ReadInt64();
            var target = ReadString(reader, 64) ??
                throw new InvalidOperationException("PreviewSessionAuthorizationTargetMissing");
            var recordedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var configuration = ReadConfiguration(reader);
            var savedDraft = ReadDraft(reader);
            var frozenHash = ReadString(reader, 64);
            var commandSequence = ReadNullableInt64(reader);
            var commandHash = ReadString(reader, 64);
            var authorizationSequence = ReadNullableInt64(reader);
            var authorizationHash = ReadString(reader, 64);
            var payloadHash = ReadString(reader, 64);
            var auditSequence = reader.ReadInt64();
            var auditHash = ReadString(reader, 64);
            var contentHash = ReadString(reader, 64) ??
                throw new InvalidOperationException("PreviewSessionContentHashMissing");
            var value = new PreviewSessionEvent(position, header, attemptId, correlationId, commandKind,
                phase, restoration, reason, terminal, actorPrincipalId, actorSessionId, actorRevision,
                target, recordedAt, configuration, savedDraft, frozenHash, commandSequence, commandHash,
                authorizationSequence, authorizationHash, payloadHash, auditSequence, auditHash);
            if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PreviewSessionContentHashMismatch");
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("PreviewSessionPayloadTrailingBytes");
            if (!payload.Span.SequenceEqual(Encode(value)))
                throw new InvalidOperationException("PreviewSessionPayloadCanonicalMismatch");
            return value;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not InvalidOperationException
            { Message: "PreviewSessionPayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("PreviewSessionPayloadInvalid", exception);
        }
    }

    internal static string PayloadHash(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private static void WriteHeader(BinaryWriter writer, PreviewSessionHeader value)
    {
        writer.Write(value.Position);
        WriteGuid(writer, value.SessionId);
        WriteGuid(writer, value.RuntimeEpoch);
        WriteGuid(writer, value.StartCorrelationId);
        WriteGuid(writer, value.AttemptId);
        WriteDraft(writer, value.Draft);
        WriteString(writer, value.LogicalCameraRole, 128);
        WriteBinding(writer, value.CurrentBinding);
        WriteReference(writer, value.ActiveActivation);
        WriteString(writer, value.ActiveSnapshotContentHash, 64);
        WriteString(writer, value.ActiveCameraContentHash, 64);
        WriteGuid(writer, value.ActorPrincipalId);
        WriteGuid(writer, value.ActorSessionId);
        writer.Write(value.ActorAuthorizationRevision);
        WriteContract(writer, value.AuthorizationPolicy);
        WriteString(writer, value.AuthorizationTarget, 64);
        WriteString(writer, value.ChangeReason, MaximumStringBytes);
        writer.Write(value.StartedAtUtc.UtcTicks);
        writer.Write((byte)value.Phase);
        writer.Write((byte)value.Restoration);
        WriteString(writer, value.ReasonCode, MaximumStringBytes);
        writer.Write(value.RecoveryRequired);
        WriteString(writer, value.FrozenSettingsContentHash, 64);
        WriteDraft(writer, value.LastSavedDraft);
        WriteString(writer, value.ContentHash, 64);
    }

    private static PreviewSessionHeader ReadHeader(BinaryReader reader)
    {
        var position = reader.ReadInt64();
        var sessionId = ReadGuid(reader);
        var epoch = ReadGuid(reader);
        var startCorrelation = ReadGuid(reader);
        var attemptId = ReadGuid(reader);
        var draft = ReadDraft(reader) ?? throw new InvalidOperationException("PreviewSessionDraftMissing");
        var role = ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionRoleMissing");
        var binding = ReadBinding(reader) ?? throw new InvalidOperationException("PreviewSessionBindingMissing");
        var active = ReadReference(reader);
        var activeSnapshot = ReadString(reader, 64);
        var activeCamera = ReadString(reader, 64);
        var principal = ReadGuid(reader);
        var session = ReadGuid(reader);
        var revision = reader.ReadInt64();
        var policy = ReadContract(reader) ?? throw new InvalidOperationException("PreviewSessionPolicyMissing");
        var target = ReadString(reader, 64) ?? throw new InvalidOperationException("PreviewSessionTargetMissing");
        var reason = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("PreviewSessionReasonMissing");
        var started = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var phase = ReadEnum<PreviewSessionPhase>(reader.ReadByte(), "PreviewSessionPhaseInvalid");
        var restoration = ReadEnum<PreviewRestorationState>(reader.ReadByte(), "PreviewSessionRestorationInvalid");
        var code = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("PreviewSessionReasonMissing");
        var recovery = reader.ReadBoolean();
        var frozen = ReadString(reader, 64);
        var lastSaved = ReadDraft(reader);
        var contentHash = ReadString(reader, 64) ?? throw new InvalidOperationException("PreviewSessionContentHashMissing");
        var value = new PreviewSessionHeader(position, sessionId, epoch, startCorrelation, attemptId,
            draft, role, binding, active, activeSnapshot, activeCamera, principal, session, revision,
            policy, target, reason, started, phase, restoration, code, recovery, frozen, lastSaved);
        if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("PreviewSessionHeaderContentHashMismatch");
        return value;
    }

    private static void WriteBinding(BinaryWriter writer, CameraBindingRevision value)
    {
        writer.Write(value.Position);
        WriteString(writer, value.LogicalRole, 128);
        writer.Write(value.Revision);
        WriteGuid(writer, value.OperationId);
        WriteString(writer, value.PreviousRevisionHash, 64);
        WriteString(writer, value.RevisionHash, 64);
        WriteString(writer, value.Target.Provider.Id, 128);
        WriteString(writer, value.Target.Provider.Version, 128);
        WriteString(writer, value.Target.Provider.AdapterPackageId, 128);
        WriteString(writer, value.Target.Provider.AdapterVersion, 128);
        WriteString(writer, value.Target.StableDeviceIdentity, 128);
        WriteGuid(writer, value.AuthorPrincipalId);
        WriteGuid(writer, value.AuthorSessionId);
        writer.Write(value.AuthorAuthorizationRevision);
        WriteString(writer, value.ChangeReason, MaximumStringBytes);
        writer.Write(value.RecordedAtUtc.UtcTicks);
    }

    private static CameraBindingRevision? ReadBinding(BinaryReader reader)
    {
        var position = reader.ReadInt64();
        var role = ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionRoleMissing");
        var revision = reader.ReadInt64();
        var operationId = ReadGuid(reader);
        var previousHash = ReadString(reader, 64);
        var revisionHash = ReadString(reader, 64) ?? throw new InvalidOperationException("PreviewSessionBindingHashMissing");
        var provider = new CameraProviderIdentity(
            ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionProviderMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionProviderMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionProviderMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionProviderMissing"));
        var target = new CameraBindingTarget(provider,
            ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionDeviceMissing"));
        var principal = ReadGuid(reader);
        var session = ReadGuid(reader);
        var authorRevision = reader.ReadInt64();
        var reason = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("PreviewSessionReasonMissing");
        var recorded = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        return new CameraBindingRevision(position, role, revision, operationId, previousHash,
            revisionHash, target, principal, session, authorRevision, reason, recorded);
    }

    private static void WriteReference(BinaryWriter writer, RecipeActivationReference? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.Position);
        WriteGuid(writer, value.ActivationId);
        WriteString(writer, value.ContentHash, 64);
    }

    private static RecipeActivationReference? ReadReference(BinaryReader reader) => !reader.ReadBoolean()
        ? null : new RecipeActivationReference(reader.ReadInt64(), ReadGuid(reader),
            ReadString(reader, 64) ?? throw new InvalidOperationException("PreviewSessionReferenceHashMissing"));

    private static void WriteContract(BinaryWriter writer, RecipeContractReference value)
    {
        WriteString(writer, value.Id, 128);
        WriteString(writer, value.Version, 128);
        WriteString(writer, value.ContentHash, 64);
    }

    private static RecipeContractReference? ReadContract(BinaryReader reader)
    {
        var id = ReadString(reader, 128);
        if (id is null) return null;
        return new RecipeContractReference(id,
            ReadString(reader, 128) ?? throw new InvalidOperationException("PreviewSessionPolicyMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("PreviewSessionPolicyMissing"));
    }

    private static void WriteDraft(BinaryWriter writer, PreviewDraftReference? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.DraftId.ToByteArray());
        writer.Write(value.Revision);
        WriteString(writer, value.RevisionContentHash, 64);
    }

    private static PreviewDraftReference? ReadDraft(BinaryReader reader) => !reader.ReadBoolean()
        ? null : new PreviewDraftReference(new Guid(reader.ReadBytes(16)), reader.ReadInt64(),
            ReadString(reader, 64) ?? throw new InvalidOperationException("PreviewSessionDraftHashMissing"));

    private static void WriteConfiguration(BinaryWriter writer, PreviewTuningConfiguration? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        var process = value.ProcessSettings;
        writer.Write(process.ExposureTimeUs);
        writer.Write(process.GainDb);
        writer.Write(process.RegionOfInterest.OffsetX);
        writer.Write(process.RegionOfInterest.OffsetY);
        writer.Write(process.RegionOfInterest.Width);
        writer.Write(process.RegionOfInterest.Height);
        writer.Write((byte)process.PixelFormat);
        WriteNullableInt32(writer, process.ValidBits);
        writer.Write(process.WhiteBalanceRgb is not null);
        if (process.WhiteBalanceRgb is { } whiteBalance)
        {
            writer.Write(whiteBalance.Red);
            writer.Write(whiteBalance.Green);
            writer.Write(whiteBalance.Blue);
        }
        writer.Write((byte)value.ExposureMode);
        writer.Write((byte)value.GainMode);
        writer.Write((byte)value.WhiteBalanceMode);
    }

    private static PreviewTuningConfiguration? ReadConfiguration(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var process = new PreviewCameraProcessSettings(reader.ReadDouble(), reader.ReadDouble(),
            new RegionOfInterest(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
            ReadEnum<VisionPixelFormat>(reader.ReadByte(), "PreviewSessionPixelFormatInvalid"),
            ReadNullableInt32(reader), reader.ReadBoolean() ? new WhiteBalanceRgb(reader.ReadDouble(),
                reader.ReadDouble(), reader.ReadDouble()) : null);
        return new PreviewTuningConfiguration(process,
            ReadEnum<PreviewAutomaticControlMode>(reader.ReadByte(), "PreviewSessionControlModeInvalid"),
            ReadEnum<PreviewAutomaticControlMode>(reader.ReadByte(), "PreviewSessionControlModeInvalid"),
            ReadEnum<PreviewAutomaticControlMode>(reader.ReadByte(), "PreviewSessionControlModeInvalid"));
    }

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new InvalidOperationException("PreviewSessionGuidInvalid");
        return new Guid(bytes);
    }

    private static void WriteNullableInt32(BinaryWriter writer, int? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static int? ReadNullableInt32(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;

    private static void WriteNullableInt64(BinaryWriter writer, long? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;

    private static void WriteString(BinaryWriter writer, string? value, int maximumCharacters)
    {
        writer.Write(value is not null);
        if (value is null) return;
        if (value.Length > maximumCharacters) throw new InvalidOperationException("PreviewSessionStringCapacityExceeded");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > checked(maximumCharacters * 4))
            throw new InvalidOperationException("PreviewSessionStringCapacityExceeded");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader, int maximumCharacters)
    {
        if (!reader.ReadBoolean()) return null;
        var length = reader.ReadInt32();
        if (length is < 0 or > 1_048_576) throw new InvalidOperationException("PreviewSessionStringCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("PreviewSessionStringInvalid");
        var value = new UTF8Encoding(false, true).GetString(bytes);
        if (value.Length > maximumCharacters) throw new InvalidOperationException("PreviewSessionStringCapacityExceeded");
        return value;
    }

    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum
    {
        var decoded = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(typeof(T), decoded) ? decoded :
            throw new InvalidOperationException(reason);
    }
}

/// <summary>Writer-side verified inputs; callers cannot manufacture these from public projections.</summary>
internal sealed record PreviewSessionAdmissionInput(RecipeDraftRevision Draft,
    CameraSetupStoreSnapshot CurrentBinding, RecipeActivationRecord? ActiveBaseline);

/// <summary>Details from the Runtime after a Preview device operation or safe close.</summary>
internal sealed record PreviewSessionCompletion(bool Succeeded, string ReasonCode,
    PreviewSessionPhase Phase, PreviewRestorationState Restoration,
    PreviewTuningConfiguration? EffectiveConfiguration = null,
    PreviewDraftReference? SavedDraft = null, string? FrozenSettingsContentHash = null,
    bool CompleteOriginalStart = true, bool CompleteCommand = true);

internal sealed record PreviewSessionCommandResult(RuntimeCommandOutcome Outcome,
    PreviewSessionHeader? Header = null, PreviewSessionEvent? Event = null,
    CommandAuditFact? CommandFact = null);

internal sealed record PreviewSessionMutation(PreviewSessionHeader Header,
    PreviewSessionEvent Event,
    IReadOnlyList<CommandAuditFact>? AdditionalTerminalFacts = null);

internal sealed record PreviewSessionState(bool Enabled, PreviewSessionHeader? Header,
    IReadOnlyList<PreviewSessionEvent> Events, RecipeDraftRevision? Draft,
    RecipeActivationRecord? ActiveBaseline);
