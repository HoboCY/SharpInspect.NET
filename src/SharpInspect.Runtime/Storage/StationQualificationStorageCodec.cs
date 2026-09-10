using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Canonical bounded wire representation for the independent qualification
/// ledger.  This codec is deliberately binary and self describing: the
/// SQLite scalar columns are indexes only and are never used as a replacement
/// for the signed payload.
/// </summary>
internal static class StationQualificationStorageCodec
{
    internal const int MaximumPayloadBytes = StationQualificationStoreOptions.MaximumPayloadBytesHardLimit;
    private const int Magic = 0x31535153; // SQS1
    private const byte FormatVersion = 2;
    private const int MaximumStringBytes = 16 * 1024 * 1024;
    private const int MaximumSmallStringBytes = 4096;
    private const int MaximumDestinationCount = 5;
    private const int MaximumScenarioCount = 64;
    private const int MaximumSegmentCount = 2048;

    internal static byte[] Encode(StationQualificationSessionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            WriteHeader(writer, value.Header);
            writer.Write(value.Position);
            WriteString(writer, value.PreviousHash, 64);
            WriteGuid(writer, value.CommandCorrelationId);
            WriteGuid(writer, value.AttemptId);
            writer.Write((byte)value.CommandKind);
            WriteString(writer, value.CommandAuthorizationTarget, 64);
            writer.Write((byte)value.Phase);
            writer.Write((byte)value.Restoration);
            WriteString(writer, value.ReasonCode, MaximumSmallStringBytes);
            writer.Write(value.Terminal);
            writer.Write(value.RecordedAtUtc.UtcTicks);
            WriteRecoveryAttempt(writer, value.RecoveryAttempt);
            WriteObservation(writer, value.Observation);
            WriteRun(writer, value.Run);
            WriteNullableInt64(writer, value.CommandAuditSequence);
            WriteString(writer, value.CommandAuditHash, 64);
            WriteNullableInt64(writer, value.AuthorizationAuditSequence);
            WriteString(writer, value.AuthorizationAuditHash, 64);
            writer.Write(value.AuditSequence);
            WriteString(writer, value.AuditHash, 64);
            WriteString(writer, value.ContentHash, 64);
        }
        if (stream.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("StationQualificationPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static StationQualificationSessionEvent Decode(ReadOnlyMemory<byte> payload,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver = null)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("StationQualificationPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("StationQualificationPayloadVersionUnsupported");
            var header = ReadHeader(reader, profileResolver);
            var position = reader.ReadInt64();
            var previous = ReadString(reader, 64);
            var correlation = ReadGuid(reader);
            var attempt = ReadGuid(reader);
            var commandKind = ReadEnum<AuditedCommandKind>(reader.ReadByte(), "StationQualificationCommandKindInvalid");
            var commandTarget = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationCommandTargetMissing");
            var phase = ReadEnum<StationQualificationSessionPhase>(reader.ReadByte(), "StationQualificationPhaseInvalid");
            var restoration = ReadEnum<StationQualificationRestorationState>(reader.ReadByte(), "StationQualificationRestorationInvalid");
            var reason = ReadString(reader, MaximumSmallStringBytes) ?? throw new InvalidOperationException("StationQualificationReasonMissing");
            var terminal = reader.ReadBoolean();
            var recorded = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var recoveryAttempt = ReadRecoveryAttempt(reader);
            var observation = ReadObservation(reader);
            // A cold reader resolves the wire contract from the immutable target
            // snapshot embedded in this same event, never from today's contract.
            var targetBinding = header.TargetBaseline.SuccessfulSnapshot?.PlcResultContract;
            var run = ReadRun(reader, hash => targetBinding?.ContentHash == hash
                ? targetBinding : null);
            var commandSequence = ReadNullableInt64(reader);
            var commandHash = ReadString(reader, 64);
            var authorizationSequence = ReadNullableInt64(reader);
            var authorizationHash = ReadString(reader, 64);
            var auditSequence = reader.ReadInt64();
            var auditHash = ReadString(reader, 64);
            var contentHash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationContentHashMissing");
            var value = new StationQualificationSessionEvent(position, previous, header, correlation, attempt,
                commandKind, phase, restoration, reason, terminal, recorded, observation, run,
                commandSequence, commandHash, authorizationSequence, authorizationHash, auditSequence, auditHash,
                recoveryAttempt, commandTarget);
            if (!string.Equals(contentHash, value.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("StationQualificationContentHashMismatch");
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("StationQualificationPayloadTrailingBytes");
            if (!payload.Span.SequenceEqual(Encode(value)))
                throw new InvalidOperationException("StationQualificationPayloadCanonicalMismatch");
            return value;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not InvalidOperationException
            { Message: "StationQualificationPayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("StationQualificationPayloadInvalid", exception);
        }
    }

    internal static string PayloadHash(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private static void WriteHeader(BinaryWriter writer, StationQualificationSessionHeader value)
    {
        WriteGuid(writer, value.SessionId);
        WriteGuid(writer, value.RuntimeEpoch);
        WriteGuid(writer, value.LeaseNonce);
        WriteGuid(writer, value.StartCorrelationId);
        WriteGuid(writer, value.StartAttemptId);
        WritePlan(writer, value.Plan);
        var baseline = RecipeActivationStorageCodec.Encode(value.TargetBaseline);
        WriteBytes(writer, baseline, RecipeActivationStoreOptions.MaximumPayloadBytesHardLimit);
        WriteGuid(writer, value.ActorPrincipalId);
        WriteGuid(writer, value.ActorSessionId);
        writer.Write(value.ActorAuthorizationRevision);
        WriteContract(writer, value.AuthorizationPolicy);
        WriteNullableGuid(writer, value.StepUpGrantId);
        WriteString(writer, value.AuthorizationTarget, 64);
        WriteString(writer, value.ChangeReason, MaximumSmallStringBytes);
        writer.Write(value.StartedAtUtc.UtcTicks);
        WriteString(writer, value.ContentHash, 64);
    }

    private static StationQualificationSessionHeader ReadHeader(BinaryReader reader,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver)
    {
        var session = ReadGuid(reader);
        var epoch = ReadGuid(reader);
        var lease = ReadGuid(reader);
        var correlation = ReadGuid(reader);
        var attempt = ReadGuid(reader);
        var plan = ReadPlan(reader);
        var baseline = RecipeActivationStorageCodec.Decode(ReadBytes(reader,
            RecipeActivationStoreOptions.MaximumPayloadBytesHardLimit), profileResolver);
        var principal = ReadGuid(reader);
        var actorSession = ReadGuid(reader);
        var revision = reader.ReadInt64();
        var policy = ReadContract(reader) ?? throw new InvalidOperationException("StationQualificationPolicyMissing");
        var grant = ReadNullableGuid(reader);
        var target = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationTargetMissing");
        var reason = ReadString(reader, MaximumSmallStringBytes) ?? throw new InvalidOperationException("StationQualificationReasonMissing");
        var started = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var savedHash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationHeaderHashMissing");
        var value = new StationQualificationSessionHeader(session, epoch, lease, correlation, attempt,
            plan, baseline, principal, actorSession, revision, policy, grant, target, reason, started);
        if (!string.Equals(savedHash, value.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("StationQualificationHeaderHashMismatch");
        return value;
    }

    private static void WritePlan(BinaryWriter writer, StationQualificationPlan value)
    {
        WriteReference(writer, value.TargetActivation);
        WriteString(writer, value.TargetStationFingerprint, 64);
        WriteString(writer, value.ReleaseCandidateFingerprint, 64);
        WriteString(writer, value.ProfileHash, 64);
        WriteString(writer, value.QualificationContextHash, 64);
        WriteHarness(writer, value.QualificationHarnessIdentity);
        WriteController(writer, value.TargetControllerConfiguration);
        WriteController(writer, value.TransientControllerConfiguration);
        WriteCount(writer, value.DestinationBindings.Count, MaximumDestinationCount);
        foreach (var destination in value.DestinationBindings)
        {
            writer.Write((byte)destination.Kind);
            WriteString(writer, destination.DestinationId, 256);
            WriteString(writer, destination.TargetBindingHash, 64);
            WriteString(writer, destination.ContentHash, 64);
        }
        WriteCount(writer, value.ScenarioIds.Count, MaximumScenarioCount);
        foreach (var scenario in value.ScenarioIds) WriteString(writer, scenario, 128);
    }

    private static StationQualificationPlan ReadPlan(BinaryReader reader)
    {
        var activation = ReadReference(reader) ?? throw new InvalidOperationException("StationQualificationTargetActivationMissing");
        var station = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationStationFingerprintMissing");
        var release = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationReleaseFingerprintMissing");
        var profile = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationProfileHashMissing");
        var context = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationContextHashMissing");
        var harness = ReadHarness(reader);
        var targetController = ReadController(reader);
        var transientController = ReadController(reader);
        var destinations = new List<QualificationDestinationBinding>(ReadCount(reader, MaximumDestinationCount));
        foreach (var _ in Enumerable.Range(0, destinations.Capacity))
        {
            var kind = ReadEnum<QualificationDestinationKind>(reader.ReadByte(), "StationQualificationDestinationKindInvalid");
            var id = ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationDestinationIdMissing");
            var hash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationDestinationHashMissing");
            var saved = ReadString(reader, 64);
            var value = new QualificationDestinationBinding(kind, id, hash);
            if (saved != value.ContentHash) throw new InvalidOperationException("StationQualificationDestinationHashMismatch");
            destinations.Add(value);
        }
        var scenarios = new List<string>(ReadCount(reader, MaximumScenarioCount));
        foreach (var _ in Enumerable.Range(0, scenarios.Capacity))
            scenarios.Add(ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationScenarioMissing"));
        return new StationQualificationPlan(activation, station, release, profile, context, harness,
            targetController, transientController, destinations, scenarios);
    }

    private static void WriteHarness(BinaryWriter writer, QualificationHarnessIdentity value)
    {
        WriteString(writer, value.Id, 256); WriteString(writer, value.Version, 128);
        WriteString(writer, value.ContentHash, 64); WriteString(writer, value.CoveredPathHash, 64);
        writer.Write(value.DevelopmentOnly);
    }

    private static QualificationHarnessIdentity ReadHarness(BinaryReader reader) =>
        new(ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationHarnessMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationHarnessVersionMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationHarnessHashMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationHarnessCoveredPathMissing"),
            reader.ReadBoolean());

    private static void WriteController(BinaryWriter writer, QualificationControllerConfiguration value)
    {
        WriteString(writer, value.EndpointBindingHash, 64);
        WriteString(writer, value.ProtocolBindingHash, 64);
        WriteBytes(writer, value.GetConfigurationBytes(), QualificationControllerConfiguration.MaximumConfigurationBytes);
        WriteString(writer, value.ContentHash, 64);
    }

    private static QualificationControllerConfiguration ReadController(BinaryReader reader)
    {
        var endpoint = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationEndpointHashMissing");
        var protocol = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationProtocolHashMissing");
        var bytes = ReadBytes(reader, QualificationControllerConfiguration.MaximumConfigurationBytes);
        var saved = ReadString(reader, 64);
        var value = new QualificationControllerConfiguration(endpoint, protocol, bytes);
        if (saved != value.ContentHash) throw new InvalidOperationException("StationQualificationControllerHashMismatch");
        return value;
    }

    private static void WriteObservation(BinaryWriter writer, QualificationFacilityObservation? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteHarness(writer, value.Identity);
        WriteGuid(writer, value.SessionId); WriteGuid(writer, value.LeaseNonce); WriteGuid(writer, value.RuntimeEpoch);
        writer.Write(value.Sequence); writer.Write(value.ObservedAtUtc.UtcTicks); writer.Write(value.Connected); writer.Write(value.LineStopped);
        WriteString(writer, value.EffectiveControllerConfigurationHash, 64);
        WriteCount(writer, value.Destinations.Count, MaximumDestinationCount);
        foreach (var destination in value.Destinations)
        {
            writer.Write((byte)destination.Kind); WriteString(writer, destination.DestinationId, 256);
            WriteString(writer, destination.TargetBindingHash, 64); writer.Write((byte)destination.Route);
            writer.Write(destination.OutputEnabled); WriteString(writer, destination.ContentHash, 64);
        }
        WriteString(writer, value.ContentHash, 64);
    }

    private static void WriteRecoveryAttempt(BinaryWriter writer,
        StationQualificationRecoveryAttempt? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteGuid(writer, value.RuntimeEpoch);
        WriteGuid(writer, value.LeaseNonce);
        WriteString(writer, value.ContentHash, 64);
    }

    private static StationQualificationRecoveryAttempt? ReadRecoveryAttempt(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var runtimeEpoch = ReadGuid(reader);
        var leaseNonce = ReadGuid(reader);
        var saved = ReadString(reader, 64);
        var value = new StationQualificationRecoveryAttempt(runtimeEpoch, leaseNonce);
        if (saved != value.ContentHash)
            throw new InvalidOperationException("StationQualificationRecoveryAttemptHashMismatch");
        return value;
    }

    private static QualificationFacilityObservation? ReadObservation(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var identity = ReadHarness(reader);
        var session = ReadGuid(reader); var lease = ReadGuid(reader); var epoch = ReadGuid(reader);
        var sequence = reader.ReadInt64(); var observed = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var connected = reader.ReadBoolean(); var stopped = reader.ReadBoolean();
        var controller = ReadString(reader, 64);
        var destinations = new List<QualificationDestinationObservation>(ReadCount(reader, MaximumDestinationCount));
        foreach (var _ in Enumerable.Range(0, destinations.Capacity))
        {
            var kind = ReadEnum<QualificationDestinationKind>(reader.ReadByte(), "StationQualificationDestinationKindInvalid");
            var id = ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationDestinationIdMissing");
            var binding = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationDestinationHashMissing");
            var route = ReadEnum<QualificationDestinationRoute>(reader.ReadByte(), "StationQualificationDestinationRouteInvalid");
            var output = reader.ReadBoolean(); var saved = ReadString(reader, 64);
            var value = new QualificationDestinationObservation(kind, id, binding, route, output);
            if (saved != value.ContentHash) throw new InvalidOperationException("StationQualificationDestinationHashMismatch");
            destinations.Add(value);
        }
        var savedObservation = ReadString(reader, 64);
        var result = new QualificationFacilityObservation(identity, session, lease, epoch, sequence,
            observed, connected, stopped, controller, destinations);
        if (savedObservation != result.ContentHash) throw new InvalidOperationException("StationQualificationObservationHashMismatch");
        return result;
    }

    private static void WriteRun(BinaryWriter writer, StationQualificationRunRecord? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.Position); WriteGuid(writer, value.RunId.Value); WriteGuid(writer, value.SessionId);
        writer.Write(value.StimulusSequence); WriteString(writer, value.ScenarioId, 128); WriteString(writer, value.ContextHash, 64);
        writer.Write(value.ControllerEpoch); writer.Write(value.CycleSequence); writer.Write(value.AdmittedAtUtc.UtcTicks);
        WriteNullableTime(writer, value.CompletedAtUtc); WriteNullableEnum(writer, value.ExecutionStatus);
        writer.Write((byte)value.Decision); WriteString(writer, value.ReasonCode, MaximumSmallStringBytes);
        WriteFrameMetadata(writer, value.FrameMetadata); WriteFrameProvenance(writer, value.FrameProvenance);
        WriteString(writer, value.ResultPayloadJson, MaximumStringBytes); WriteString(writer, value.ResultPayloadHash, 64);
        WriteQualificationPayload(writer, value.QualificationPayload);
        WriteTiming(writer, value.Timing); WriteString(writer, value.ContentHash, 64);
    }

    private static StationQualificationRunRecord? ReadRun(BinaryReader reader,
        Func<string, PlcResultContractBinding?>? bindingResolver)
    {
        if (!reader.ReadBoolean()) return null;
        var position = reader.ReadInt64(); var runId = new QualificationRunId(ReadGuid(reader)); var session = ReadGuid(reader);
        var stimulus = reader.ReadInt64(); var scenario = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationScenarioMissing");
        var context = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationContextHashMissing");
        var controllerEpoch = reader.ReadUInt32(); var cycle = reader.ReadUInt32(); var admitted = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var completed = ReadNullableTime(reader); var status = ReadNullableEnum<ExecutionStatus>(reader);
        var decision = ReadEnum<InspectionDecision>(reader.ReadByte(), "StationQualificationDecisionInvalid");
        var reason = ReadString(reader, MaximumSmallStringBytes) ?? throw new InvalidOperationException("StationQualificationRunReasonMissing");
        var metadata = ReadFrameMetadata(reader); var provenance = ReadFrameProvenance(reader);
        var json = ReadString(reader, MaximumStringBytes); var hash = ReadString(reader, 64);
        var qualification = ReadQualificationPayload(reader, bindingResolver); var timing = ReadTiming(reader);
        var savedHash = ReadString(reader, 64);
        var result = new StationQualificationRunRecord(position, runId, session, stimulus, scenario, context,
            controllerEpoch, cycle, admitted, completed, status, decision, reason, metadata, provenance,
            json, hash, qualification, timing);
        if (savedHash != result.ContentHash) throw new InvalidOperationException("StationQualificationRunHashMismatch");
        return result;
    }

    private static void WriteQualificationPayload(BinaryWriter writer, StationQualificationPayload? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteGuid(writer, value.SessionId); WriteGuid(writer, value.RunId.Value); WriteString(writer, value.ContextHash, 64);
        WriteString(writer, value.Binding.ContentHash, 64); WriteString(writer, value.Binding.Recipe.Id, 256);
        WriteString(writer, value.Binding.Recipe.Version, 128); WriteString(writer, value.Binding.Recipe.ContentHash, 64);
        WriteString(writer, value.Binding.Algorithm.Id, 256); WriteString(writer, value.Binding.Algorithm.Version, 128);
        WriteString(writer, value.Binding.Validation.ContentHash, 64); WriteString(writer, value.Binding.Contract.ContentHash, 64);
        writer.Write(value.ControllerEpoch); writer.Write(value.CycleSequence); writer.Write((byte)value.ExecutionStatus);
        writer.Write((byte)value.Decision); WriteString(writer, value.ReasonCode, MaximumSmallStringBytes);
        WriteCount(writer, value.Segments.Count, MaximumSegmentCount);
        foreach (var segment in value.Segments)
        { writer.Write(segment.StartRegister); WriteBytes(writer, segment.RegisterBytes.ToArray(), 131072); }
        WriteString(writer, value.WireContentHash, 64); WriteString(writer, value.ContentHash, 64);
    }

    private static StationQualificationPayload? ReadQualificationPayload(BinaryReader reader,
        Func<string, PlcResultContractBinding?>? bindingResolver)
    {
        if (!reader.ReadBoolean()) return null;
        var session = ReadGuid(reader); var run = new QualificationRunId(ReadGuid(reader)); var context = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationContextHashMissing");
        var bindingHash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationBindingHashMissing");
        var recipeId = ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var recipeVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var recipeHash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var algorithmId = ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var algorithmVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var validationHash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var contractHash = ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationBindingMissing");
        var binding = bindingResolver?.Invoke(bindingHash) ?? throw new InvalidOperationException("StationQualificationBindingResolverRequired");
        if (binding.ContentHash != bindingHash || binding.Recipe.Id != recipeId || binding.Recipe.Version != recipeVersion ||
            binding.Recipe.ContentHash != recipeHash || binding.Algorithm.Id != algorithmId || binding.Algorithm.Version != algorithmVersion ||
            binding.Validation.ContentHash != validationHash || binding.Contract.ContentHash != contractHash)
            throw new InvalidOperationException("StationQualificationBindingMismatch");
        var epoch = reader.ReadUInt32(); var cycle = reader.ReadUInt32();
        var execution = ReadEnum<ExecutionStatus>(reader.ReadByte(), "StationQualificationExecutionStatusInvalid");
        var decision = ReadEnum<InspectionDecision>(reader.ReadByte(), "StationQualificationDecisionInvalid");
        var reason = ReadString(reader, MaximumSmallStringBytes);
        var segments = new List<PlcRegisterSegment>(ReadCount(reader, MaximumSegmentCount));
        foreach (var _ in Enumerable.Range(0, segments.Capacity))
            segments.Add(new PlcRegisterSegment(reader.ReadInt32(), ReadBytes(reader, 131072)));
        var wire = ReadString(reader, 64); var content = ReadString(reader, 64);
        var result = new StationQualificationPayload(session, run, context, binding, epoch, cycle, execution, decision, reason, segments);
        if (wire != result.WireContentHash || content != result.ContentHash) throw new InvalidOperationException("StationQualificationPayloadHashMismatch");
        return result;
    }

    private static void WriteTiming(BinaryWriter writer, AlgorithmExecutionTimingSnapshot? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteString(writer, value.Recipe.Id, 256); WriteString(writer, value.Recipe.Version, 128); WriteString(writer, value.Recipe.ContentHash, 64);
        WriteString(writer, value.PolicyId, 128); WriteString(writer, value.PolicyVersion, 128); WriteString(writer, value.PolicyContentHash, 64);
        writer.Write(value.AlgorithmExecutionTimeout.Ticks); writer.Write(value.CancellationGracePeriod.Ticks);
    }

    private static AlgorithmExecutionTimingSnapshot? ReadTiming(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var recipe = new RecipeReference(ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationTimingRecipeMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationTimingRecipeMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationTimingRecipeMissing"));
        return new AlgorithmExecutionTimingSnapshot(recipe,
            ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationTimingPolicyMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationTimingPolicyMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationTimingPolicyMissing"),
            TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64()));
    }

    private static void WriteFrameMetadata(BinaryWriter writer, FrameMetadata? value)
    {
        writer.Write(value is not null); if (value is null) return;
        WriteCorrelation(writer, value.Correlation); WriteString(writer, value.LogicalCameraRole, 128);
        writer.Write(value.Width); writer.Write(value.Height); writer.Write(value.StrideBytes); writer.Write((byte)value.PixelFormat);
        WriteNullableInt32(writer, value.ValidBits); writer.Write(value.HostCaptureUtc.UtcTicks); WriteEffectiveConfiguration(writer, value.EffectiveCameraConfiguration);
    }

    private static FrameMetadata? ReadFrameMetadata(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var correlation = ReadCorrelation(reader); var role = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationFrameRoleMissing");
        var width = reader.ReadInt32(); var height = reader.ReadInt32(); var stride = reader.ReadInt32();
        var format = ReadEnum<VisionPixelFormat>(reader.ReadByte(), "StationQualificationFramePixelFormatInvalid");
        var bits = ReadNullableInt32(reader); var captured = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        return new FrameMetadata(correlation, role, width, height, stride, format, bits, captured, ReadEffectiveConfiguration(reader));
    }

    private static void WriteEffectiveConfiguration(BinaryWriter writer, EffectiveCameraConfiguration value)
    {
        writer.Write((byte)value.ProductionAcquisitionMode); writer.Write(value.ExposureTimeUs); writer.Write(value.GainDb);
        writer.Write(value.RegionOfInterest.OffsetX); writer.Write(value.RegionOfInterest.OffsetY); writer.Write(value.RegionOfInterest.Width); writer.Write(value.RegionOfInterest.Height);
        writer.Write((byte)value.PixelFormat); WriteNullableInt32(writer, value.ValidBits); writer.Write(value.AcquisitionTimeoutMs); writer.Write(value.TriggerDelayUs);
        writer.Write(value.WhiteBalanceRgb is not null); if (value.WhiteBalanceRgb is { } rgb) { writer.Write(rgb.Red); writer.Write(rgb.Green); writer.Write(rgb.Blue); }
    }

    private static EffectiveCameraConfiguration ReadEffectiveConfiguration(BinaryReader reader) =>
        new(ReadEnum<ProductionAcquisitionMode>(reader.ReadByte(), "StationQualificationAcquisitionModeInvalid"), reader.ReadDouble(), reader.ReadDouble(),
            new RegionOfInterest(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
            ReadEnum<VisionPixelFormat>(reader.ReadByte(), "StationQualificationPixelFormatInvalid"), ReadNullableInt32(reader), reader.ReadInt32(), reader.ReadDouble(),
            reader.ReadBoolean() ? new WhiteBalanceRgb(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()) : null);

    private static void WriteFrameProvenance(BinaryWriter writer, FrameProvenance? value)
    {
        writer.Write(value is not null); if (value is null) return;
        WriteCorrelation(writer, value.Correlation); WriteString(writer, value.ProviderId, 128); WriteString(writer, value.ProviderVersion, 128);
        WriteString(writer, value.AdapterId, 128); WriteString(writer, value.AdapterVersion, 128); WriteString(writer, value.SdkId, 128); WriteString(writer, value.SdkVersion, 128);
        WriteString(writer, value.NativeRuntimeVersion, 128); WriteString(writer, value.StableDeviceIdentity, 128); WriteString(writer, value.ReportedModel, 128); WriteString(writer, value.FirmwareVersion, 128);
        WriteString(writer, value.NativePixelFormatDescription, 256); WriteString(writer, value.NormalizationDetails, 2048); writer.Write(value.NormalizationAllocated); writer.Write(value.NormalizationTransformed);
        WriteDeviceTimestamp(writer, value.DeviceTimestamp); WriteNullableUInt64(writer, value.FrameCounter); WriteMilestones(writer, value.Milestones);
        writer.Write(value.PoolCopyEvidence is not null); if (value.PoolCopyEvidence is { } copy) { writer.Write(copy.SourceStrideBytes); writer.Write(copy.DestinationStrideBytes); }
    }

    private static FrameProvenance? ReadFrameProvenance(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var correlation = ReadCorrelation(reader); var provider = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationProviderMissing"); var providerVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationProviderMissing");
        var adapter = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationAdapterMissing"); var adapterVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationAdapterMissing");
        var sdk = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationSdkMissing"); var sdkVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationSdkMissing");
        var native = ReadString(reader, 128); var device = ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationDeviceMissing"); var model = ReadString(reader, 128); var firmware = ReadString(reader, 128);
        var nativeFormat = ReadString(reader, 256) ?? throw new InvalidOperationException("StationQualificationPixelDescriptionMissing"); var normalization = ReadString(reader, 2048) ?? throw new InvalidOperationException("StationQualificationNormalizationMissing");
        var allocated = reader.ReadBoolean(); var transformed = reader.ReadBoolean(); var timestamp = ReadDeviceTimestamp(reader); var frameCounter = ReadNullableUInt64(reader); var milestones = ReadMilestones(reader);
        var hasCopy = reader.ReadBoolean(); var sourceStride = hasCopy ? reader.ReadInt32() : 0; var destinationStride = hasCopy ? reader.ReadInt32() : 0;
        var result = new FrameProvenance(correlation, provider, providerVersion, adapter, adapterVersion, sdk, sdkVersion, native, device, model, firmware, nativeFormat, normalization, allocated, transformed, timestamp, frameCounter, milestones);
        return hasCopy ? result.WithPoolCopyEvidence(sourceStride, destinationStride) : result;
    }

    private static void WriteDeviceTimestamp(BinaryWriter writer, DeviceTimestamp? value)
    { writer.Write(value is not null); if (value is null) return; writer.Write(value.Value); WriteNullableInt64(writer, value.TickFrequency); WriteString(writer, value.Unit, 64); WriteString(writer, value.ClockDomain, 64); WriteNullableInt64(writer, value.CounterRollover); writer.Write((byte)value.Synchronization); }
    private static DeviceTimestamp? ReadDeviceTimestamp(BinaryReader reader) => !reader.ReadBoolean() ? null : new DeviceTimestamp(reader.ReadInt64(), ReadNullableInt64(reader), ReadString(reader, 64), ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationClockDomainMissing"), ReadNullableInt64(reader), ReadEnum<DeviceClockSynchronization>(reader.ReadByte(), "StationQualificationClockSyncInvalid"));
    private static void WriteMilestones(BinaryWriter writer, FrameAcquisitionMilestones value) { writer.Write(value.MonotonicFrequency); WriteTimePoint(writer, value.TriggerAccepted); WriteTimePoint(writer, value.AcquisitionStarted); WriteTimePoint(writer, value.NativeFrameReceived); WriteTimePoint(writer, value.NormalizedFrameReady); }
    private static FrameAcquisitionMilestones ReadMilestones(BinaryReader reader) => new(reader.ReadInt64(), ReadTimePoint(reader), ReadTimePoint(reader), ReadTimePoint(reader), ReadTimePoint(reader));
    private static void WriteTimePoint(BinaryWriter writer, FrameTimePoint? value) { writer.Write(value is not null); if (value is not null) { writer.Write(value.HostObservedAtUtc.UtcTicks); writer.Write(value.MonotonicTimestamp); } }
    private static FrameTimePoint? ReadTimePoint(BinaryReader reader) => !reader.ReadBoolean() ? null : new FrameTimePoint(new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero), reader.ReadInt64());
    private static void WriteCorrelation(BinaryWriter writer, ExecutionCorrelationId value) { writer.Write((byte)value.Kind); WriteGuid(writer, value.Value); }
    private static ExecutionCorrelationId ReadCorrelation(BinaryReader reader) => new(ReadEnum<ExecutionKind>(reader.ReadByte(), "StationQualificationExecutionKindInvalid"), ReadGuid(reader));
    private static void WriteReference(BinaryWriter writer, RecipeActivationReference? value) { writer.Write(value is not null); if (value is not null) { writer.Write(value.Position); WriteGuid(writer, value.ActivationId); WriteString(writer, value.ContentHash, 64); } }
    private static RecipeActivationReference? ReadReference(BinaryReader reader) => !reader.ReadBoolean() ? null : new RecipeActivationReference(reader.ReadInt64(), ReadGuid(reader), ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationReferenceHashMissing"));
    private static void WriteContract(BinaryWriter writer, RecipeContractReference value) { WriteString(writer, value.Id, 128); WriteString(writer, value.Version, 128); WriteString(writer, value.ContentHash, 64); }
    private static RecipeContractReference? ReadContract(BinaryReader reader) { var id = ReadString(reader, 128); return id is null ? null : new RecipeContractReference(id, ReadString(reader, 128) ?? throw new InvalidOperationException("StationQualificationPolicyMissing"), ReadString(reader, 64) ?? throw new InvalidOperationException("StationQualificationPolicyMissing")); }
    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static Guid ReadGuid(BinaryReader reader) { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new InvalidOperationException("StationQualificationGuidInvalid"); return new Guid(bytes); }
    private static void WriteNullableGuid(BinaryWriter writer, Guid? value) { writer.Write(value.HasValue); if (value.HasValue) WriteGuid(writer, value.Value); }
    private static Guid? ReadNullableGuid(BinaryReader reader) => reader.ReadBoolean() ? ReadGuid(reader) : null;
    private static void WriteNullableInt64(BinaryWriter writer, long? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;
    private static void WriteNullableUInt64(BinaryWriter writer, ulong? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static ulong? ReadNullableUInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadUInt64() : null;
    private static void WriteNullableInt32(BinaryWriter writer, int? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static int? ReadNullableInt32(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;
    private static void WriteNullableTime(BinaryWriter writer, DateTimeOffset? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value.UtcTicks); }
    private static DateTimeOffset? ReadNullableTime(BinaryReader reader) => reader.ReadBoolean() ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero) : null;
    private static void WriteNullableEnum<T>(BinaryWriter writer, T? value) where T : struct, Enum { writer.Write(value.HasValue); if (value.HasValue) writer.Write(Convert.ToByte(value.Value, CultureInfo.InvariantCulture)); }
    private static T? ReadNullableEnum<T>(BinaryReader reader) where T : struct, Enum => reader.ReadBoolean() ? ReadEnum<T>(reader.ReadByte(), "StationQualificationEnumInvalid") : null;
    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum
    {
        var decoded = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(typeof(T), decoded) ? decoded : throw new InvalidOperationException(reason);
    }
    private static void WriteCount(BinaryWriter writer, int value, int maximum) { if (value < 0 || value > maximum) throw new InvalidOperationException("StationQualificationCollectionCapacityExceeded"); writer.Write(value); }
    private static int ReadCount(BinaryReader reader, int maximum) { var value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new InvalidOperationException("StationQualificationCollectionCapacityExceeded"); return value; }
    private static void WriteString(BinaryWriter writer, string? value, int maximumBytes)
    { writer.Write(value is not null); if (value is null) return; var bytes = new UTF8Encoding(false, true).GetBytes(value); if (bytes.Length > maximumBytes) throw new InvalidOperationException("StationQualificationStringCapacityExceeded"); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string? ReadString(BinaryReader reader, int maximumBytes)
    { if (!reader.ReadBoolean()) return null; var length = reader.ReadInt32(); if (length < 0 || length > maximumBytes) throw new InvalidOperationException("StationQualificationStringCapacityExceeded"); var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new InvalidOperationException("StationQualificationPayloadInvalid"); return new UTF8Encoding(false, true).GetString(bytes); }
    private static void WriteBytes(BinaryWriter writer, byte[] value, int maximumBytes) { ArgumentNullException.ThrowIfNull(value); if (value.Length > maximumBytes) throw new InvalidOperationException("StationQualificationPayloadCapacityExceeded"); writer.Write(value.Length); writer.Write(value); }
    private static byte[] ReadBytes(BinaryReader reader, int maximumBytes) { var length = reader.ReadInt32(); if (length < 0 || length > maximumBytes) throw new InvalidOperationException("StationQualificationPayloadCapacityExceeded"); var value = reader.ReadBytes(length); if (value.Length != length) throw new InvalidOperationException("StationQualificationPayloadInvalid"); return value; }
}

internal sealed record StationQualificationStorageValue(StationQualificationSessionEvent Event,
    StationQualificationRunRecord? Run);
