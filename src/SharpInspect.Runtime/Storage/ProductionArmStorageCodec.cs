using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Strict, bounded canonical encoding for one schema-32 production-arm event.
/// The payload is self-describing and round-trips byte-for-byte; every stored
/// row is re-encoded during reads so a mutated or truncated payload fails the
/// read instead of being projected. The embedded admission report reuses the
/// unchanged schema-22 report bytes, so both ledgers observe one exact report
/// canonical form.
/// </summary>
internal static class ProductionArmStorageCodec
{
    private const int Magic = 0x31414150; // PAA1
    private const byte FormatVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(ProductionArmHistoryEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(value.Position);
            GuidValue(writer, value.EventId);
            GuidValue(writer, value.AttemptId);
            GuidValue(writer, value.RuntimeEpoch);
            writer.Write((byte)value.Cause);
            writer.Write((byte)value.Kind);
            Text(writer, value.StationId);
            Contract(writer, value.StartupPolicy);
            Contract(writer, value.PostActivationPolicy);
            Text(writer, value.DeploymentHash);
            writer.Write(value.MaintenanceHeadHash is not null);
            if (value.MaintenanceHeadHash is { } maintenance) Text(writer, maintenance);
            writer.Write(value.PlcRequest is not null);
            if (value.PlcRequest is { } plcRequest) WritePlcRequest(writer, plcRequest);
            writer.Write(value.Activation is not null);
            if (value.Activation is { } activation)
            {
                writer.Write(activation.Position);
                GuidValue(writer, activation.ActivationId);
                Text(writer, activation.ContentHash);
            }
            writer.Write(value.AdmissionGeneration);
            writer.Write(value.Report is not null);
            if (value.Report is { } report) WriteReport(writer, report);
            WriteHeads(writer, value.ExpectedDurableHeads);
            WriteHeads(writer, value.CurrentDurableHeads);
            writer.Write((byte)value.Reason);
            Text(writer, value.ReasonCode);
            writer.Write(value.HumanCommandId is not null);
            if (value.HumanCommandId is { } humanCommand) GuidValue(writer, humanCommand);
            writer.Write(value.HumanPrincipalId is not null);
            if (value.HumanPrincipalId is { } humanPrincipal) GuidValue(writer, humanPrincipal);
            writer.Write(value.HumanSessionId is not null);
            if (value.HumanSessionId is { } humanSession) GuidValue(writer, humanSession);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            Text(writer, value.ActorPrincipalId);
            writer.Write(value.ActorSessionId is not null);
            if (value.ActorSessionId is { } actorSession) GuidValue(writer, actorSession);
            writer.Write(value.ReadyReceipt is not null);
            if (value.ReadyReceipt is { } receipt) WriteReadyReceipt(writer, receipt);
            writer.Write(value.InputStability is not null);
            if (value.InputStability is { } stability) WriteStability(writer, stability);
            Text(writer, value.ContentHash);
        }
        if (stream.Length > ProductionArmStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("ProductionArmPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static ProductionArmHistoryEvent Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > ProductionArmStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("ProductionArmPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("ProductionArmPayloadVersionUnsupported");
            var position = reader.ReadInt64();
            var eventId = GuidValue(reader);
            var attemptId = GuidValue(reader);
            var runtimeEpoch = GuidValue(reader);
            var cause = EnumValue<ProductionArmCause>(reader.ReadByte(), "ProductionArmCauseInvalid");
            var kind = EnumValue<ProductionArmEventKind>(reader.ReadByte(), "ProductionArmEventKindInvalid");
            var stationId = Text(reader);
            var startupPolicy = Contract(reader);
            var postActivationPolicy = Contract(reader);
            var deploymentHash = Text(reader);
            var maintenanceHead = Boolean(reader) ? Text(reader) : null;
            var plcRequest = Boolean(reader) ? ReadPlcRequest(reader) : null;
            RecipeActivationReference? activation = Boolean(reader)
                ? new RecipeActivationReference(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;
            var generation = reader.ReadInt64();
            var report = Boolean(reader) ? ProductionAdmissionStorageCodec.ReadReport(reader) : null;
            var expectedHeads = ReadHeads(reader);
            var currentHeads = ReadHeads(reader);
            var reason = EnumValue<ProductionArmReason>(reader.ReadByte(), "ProductionArmReasonInvalid");
            var reasonCode = Text(reader);
            var humanCommand = Boolean(reader) ? GuidValue(reader) : (Guid?)null;
            var humanPrincipal = Boolean(reader) ? GuidValue(reader) : (Guid?)null;
            var humanSession = Boolean(reader) ? GuidValue(reader) : (Guid?)null;
            var recordedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var actorPrincipal = Text(reader);
            var actorSession = Boolean(reader) ? GuidValue(reader) : (Guid?)null;
            var receipt = Boolean(reader) ? ReadReadyReceipt(reader) : null;
            var stability = Boolean(reader) ? ReadStability(reader) : null;
            var hash = Text(reader);
            var value = new ProductionArmHistoryEvent(position, eventId, attemptId, runtimeEpoch, cause, kind,
                stationId, startupPolicy, postActivationPolicy, deploymentHash, plcRequest, activation,
                maintenanceHead, generation, report, expectedHeads, currentHeads, reason, reasonCode,
                humanCommand, humanPrincipal, humanSession, recordedAtUtc, actorPrincipal, actorSession, hash, receipt, stability);
            if (stream.Position != stream.Length || value.ContentHash != hash ||
                !payload.Span.SequenceEqual(Encode(value)))
                throw new InvalidOperationException("ProductionArmPayloadCanonicalMismatch");
            return value;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { throw new InvalidOperationException("ProductionArmPayloadInvalid", exception); }
    }

    /// <summary>Canonical bytes bound into the signed central audit metadata entry.</summary>
    internal static byte[] EncodeAuditBinding(SqliteCommandStore.ProductionArmStoredRow row) => AuditCanonical.Encode(
        "ProductionArmAuditV1", Number(row.Event.Position), row.Event.ContentHash, row.PreviousHash, row.PayloadHash,
        Convert.ToBase64String(row.Payload));

    private static void WriteReadyReceipt(BinaryWriter writer, ProductionArmReadyReceipt receipt)
    {
        GuidValue(writer, receipt.AttemptId); GuidValue(writer, receipt.RuntimeEpoch);
        Text(writer, receipt.AuthorizationEventHash);
        writer.Write(receipt.Activation is not null);
        if (receipt.Activation is { } activation)
        {
            writer.Write(activation.Position); GuidValue(writer, activation.ActivationId); Text(writer, activation.ContentHash);
        }
        Text(writer, receipt.MaintenanceHeadHash); writer.Write(receipt.AdmissionGeneration);
        writer.Write(receipt.ControllerEpoch); writer.Write(receipt.ConnectionGeneration);
        writer.Write(receipt.ObservedAtUtc.UtcTicks); Text(writer, receipt.ContentHash);
    }

    private static void WriteStability(BinaryWriter writer, ProductionArmInputStabilityEvidence value)
    {
        Contract(writer, value.CommunicationPolicy);
        writer.Write(value.RequiredStabilityWindow.Ticks); writer.Write(value.MaximumSampleGap.Ticks);
        writer.Write(value.AttemptInitialSequence); writer.Write(value.FirstSequence); writer.Write(value.LastSequence);
        writer.Write(value.SampleCount); writer.Write(value.StableDuration.Ticks); writer.Write(value.MaximumObservedGap.Ticks);
        writer.Write(value.FreshnessAge.Ticks); writer.Write(value.HighInputResetCount); writer.Write(value.SampleGapResetCount);
        Text(writer, value.ContentHash);
    }

    private static ProductionArmInputStabilityEvidence ReadStability(BinaryReader reader) => new(Contract(reader),
        reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(),
        reader.ReadInt32(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(), Text(reader));

    private static ProductionArmReadyReceipt ReadReadyReceipt(BinaryReader reader)
    {
        var attempt = GuidValue(reader); var epoch = GuidValue(reader); var authorized = Text(reader);
        RecipeActivationReference? activation = Boolean(reader) ? new(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;
        var receipt = new ProductionArmReadyReceipt(attempt, epoch, authorized, activation, Text(reader), reader.ReadInt64(),
            reader.ReadUInt32(), reader.ReadInt64(), new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
        if (receipt.ContentHash != Text(reader)) throw new InvalidOperationException("ProductionArmReadyReceiptHashMismatch");
        return receipt;
    }

    private static void WritePlcRequest(BinaryWriter writer, RecipeChangeRequestEvidence value)
    {
        GuidValue(writer, value.RuntimeEpoch);
        Text(writer, value.EndpointContentHash);
        Contract(writer, value.ProtocolProfile);
        writer.Write(value.ControllerEpoch);
        writer.Write(value.RequestSequence);
        writer.Write(value.SelectionCode);
        writer.Write(value.SelectionRevision is not null);
        if (value.SelectionRevision is { } revision)
        {
            writer.Write(revision.Position);
            GuidValue(writer, revision.RevisionId);
            Text(writer, revision.ContentHash);
        }
        Contract(writer, value.SelectionPolicy);
        writer.Write(value.SelectionMap is not null);
        if (value.SelectionMap is { } map) Contract(writer, map);
        writer.Write(value.Target is not null);
        if (value.Target is { } target)
        {
            writer.Write(target.SelectionCode);
            Recipe(writer, target.Recipe);
            GuidValue(writer, target.ReleaseId);
            Text(writer, target.ReleaseRecordContentHash);
        }
        writer.Write(value.ObservedAtUtc.UtcTicks);
        Text(writer, value.RequestIdentityHash);
        Text(writer, value.ContentHash);
    }

    private static RecipeChangeRequestEvidence ReadPlcRequest(BinaryReader reader)
    {
        var runtimeEpoch = GuidValue(reader);
        var endpoint = Text(reader);
        var profile = Contract(reader);
        var controllerEpoch = reader.ReadUInt32();
        var requestSequence = reader.ReadUInt32();
        var selectionCode = reader.ReadUInt32();
        RecipeSelectionReference? revision = Boolean(reader)
            ? new RecipeSelectionReference(reader.ReadInt64(), GuidValue(reader), Text(reader)) : null;
        var policy = Contract(reader);
        var map = Boolean(reader) ? Contract(reader) : null;
        RecipeSelectionMapEntry? target = Boolean(reader)
            ? new RecipeSelectionMapEntry(reader.ReadUInt32(), Recipe(reader), GuidValue(reader), Text(reader)) : null;
        var observedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var identity = Text(reader);
        var hash = Text(reader);
        var request = new RecipeChangeRequestEvidence(runtimeEpoch, endpoint, profile, controllerEpoch,
            requestSequence, selectionCode, revision, policy, map, target, observedAtUtc);
        if (identity != request.RequestIdentityHash || hash != request.ContentHash)
            throw new InvalidOperationException("ProductionArmPlcRequestEvidenceMismatch");
        return request;
    }

    private static void WriteReport(BinaryWriter writer, ProductionAdmissionReport report) =>
        ProductionAdmissionStorageCodec.WriteReport(writer, report);

    private static void WriteHeads(BinaryWriter writer, IReadOnlyDictionary<string, string> heads)
    {
        writer.Write(heads.Count);
        foreach (var pair in heads)
        {
            Text(writer, pair.Key);
            Text(writer, pair.Value);
        }
    }

    private static SortedDictionary<string, string> ReadHeads(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 64) throw new InvalidOperationException("ProductionArmDurableHeadLimitExceeded");
        var heads = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
            heads.Add(Text(reader), Text(reader));
        return heads;
    }

    private static void Text(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > 4096) throw new InvalidOperationException("ProductionArmStringCapacityExceeded");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string Text(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length is < 0 or > 4096) throw new InvalidOperationException("ProductionArmStringCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Utf8.GetString(bytes);
    }

    private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidOperationException("ProductionArmBooleanInvalid")
    };

    private static void GuidValue(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    private static Guid GuidValue(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new EndOfStreamException();
        return new Guid(bytes);
    }

    private static void Recipe(BinaryWriter writer, RecipeReference value)
    {
        Text(writer, value.Id);
        Text(writer, value.Version);
        Text(writer, value.ContentHash);
    }

    private static RecipeReference Recipe(BinaryReader reader) => new(Text(reader), Text(reader), Text(reader));

    private static void Contract(BinaryWriter writer, RecipeContractReference value)
    {
        Text(writer, value.Id);
        Text(writer, value.Version);
        Text(writer, value.ContentHash);
    }

    private static RecipeContractReference Contract(BinaryReader reader) =>
        new(Text(reader), Text(reader), Text(reader));

    private static T EnumValue<T>(byte value, string reason) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) :
        throw new InvalidOperationException(reason);

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
