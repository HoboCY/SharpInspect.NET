using System.Text;
using System.Security.Cryptography;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Canonical bounded binary representation of one Manual ledger row.</summary>
internal static class ManualInspectionStorageCodec
{
    private const int Magic = 0x314D4E4D; // MMN1
    private const byte FormatVersion = 1;
    private const int MaximumStringBytes = 16 * 1024 * 1024;

    internal static byte[] Encode(ManualInspectionSessionEvent value,
        ManualInspectionRunRecord? run = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (run is not null && (run.SessionId != value.SessionId ||
            run.RuntimeEpoch != value.RuntimeEpoch))
            throw new InvalidOperationException("ManualInspectionRunSessionMismatch");
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
            WriteNullableGuid(writer, value.ManualRunId);
            WriteString(writer, value.OutcomeContentHash, 64);
            WriteNullableInt64(writer, value.CommandAuditSequence);
            WriteString(writer, value.CommandAuditHash, 64);
            WriteNullableInt64(writer, value.AuthorizationAuditSequence);
            WriteString(writer, value.AuthorizationAuditHash, 64);
            WriteString(writer, value.PayloadHash, 64);
            writer.Write(value.AuditSequence);
            WriteString(writer, value.AuditHash, 64);
            WriteString(writer, value.ContentHash, 64);
            writer.Write(run is not null);
            if (run is not null) WriteRun(writer, run);
        }
        if (stream.Length is < 1 or > ManualInspectionStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("ManualInspectionPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static ManualInspectionStorageValue Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length is < 1 or > ManualInspectionStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("ManualInspectionPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != FormatVersion)
                throw new InvalidOperationException("ManualInspectionPayloadVersionUnsupported");
            var header = ReadHeader(reader);
            var position = reader.ReadInt64();
            var attemptId = ReadGuid(reader);
            var correlationId = ReadGuid(reader);
            var commandKind = ReadEnum<AuditedCommandKind>(reader.ReadByte(),
                "ManualInspectionCommandKindInvalid");
            var phase = ReadEnum<ManualInspectionSessionPhase>(reader.ReadByte(),
                "ManualInspectionPhaseInvalid");
            var restoration = ReadEnum<ManualInspectionRestorationState>(reader.ReadByte(),
                "ManualInspectionRestorationInvalid");
            var reason = ReadString(reader, MaximumStringBytes) ??
                throw new InvalidOperationException("ManualInspectionReasonMissing");
            var terminal = reader.ReadBoolean();
            var actorPrincipal = ReadGuid(reader);
            var actorSession = ReadGuid(reader);
            var actorRevision = reader.ReadInt64();
            var authorizationTarget = ReadString(reader, 64) ??
                throw new InvalidOperationException("ManualInspectionAuthorizationTargetMissing");
            var recordedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var manualRunId = ReadNullableGuid(reader);
            var outcomeHash = ReadString(reader, 64);
            var commandSequence = ReadNullableInt64(reader);
            var commandHash = ReadString(reader, 64);
            var authorizationSequence = ReadNullableInt64(reader);
            var authorizationHash = ReadString(reader, 64);
            var payloadHash = ReadString(reader, 64);
            var auditSequence = reader.ReadInt64();
            var auditHash = ReadString(reader, 64);
            var contentHash = ReadString(reader, 64) ??
                throw new InvalidOperationException("ManualInspectionContentHashMissing");
            var hasRun = reader.ReadBoolean();
            var run = hasRun ? ReadRun(reader) : null;
            var value = new ManualInspectionSessionEvent(position, header, attemptId, correlationId,
                commandKind, phase, restoration, reason, terminal, actorPrincipal, actorSession,
                actorRevision, authorizationTarget, recordedAt, manualRunId, outcomeHash,
                commandSequence, commandHash, authorizationSequence, authorizationHash, payloadHash,
                auditSequence, auditHash);
            if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("ManualInspectionContentHashMismatch");
            if (run is not null && (run.SessionId != value.SessionId ||
                run.RuntimeEpoch != value.RuntimeEpoch))
                throw new InvalidOperationException("ManualInspectionRunSessionMismatch");
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("ManualInspectionPayloadTrailingBytes");
            var canonical = Encode(value, run);
            if (!payload.Span.SequenceEqual(canonical))
                throw new InvalidOperationException("ManualInspectionPayloadCanonicalMismatch");
            return new ManualInspectionStorageValue(value, run);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not InvalidOperationException
            { Message: "ManualInspectionPayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("ManualInspectionPayloadInvalid", exception);
        }
    }

    internal static string PayloadHash(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private static void WriteHeader(BinaryWriter writer, ManualInspectionSessionHeader value)
    {
        writer.Write(value.Position);
        WriteGuid(writer, value.SessionId);
        WriteGuid(writer, value.RuntimeEpoch);
        WriteGuid(writer, value.StartCorrelationId);
        WriteGuid(writer, value.AttemptId);
        WriteSelection(writer, value.Selection);
        WriteString(writer, value.SourceContentHash, 64);
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
        WriteString(writer, value.ContentHash, 64);
    }

    private static ManualInspectionSessionHeader ReadHeader(BinaryReader reader)
    {
        var position = reader.ReadInt64();
        var sessionId = ReadGuid(reader);
        var epoch = ReadGuid(reader);
        var startCorrelation = ReadGuid(reader);
        var attemptId = ReadGuid(reader);
        var selection = ReadSelection(reader);
        var sourceHash = ReadString(reader, 64) ??
            throw new InvalidOperationException("ManualInspectionSourceHashMissing");
        var binding = ReadBinding(reader) ??
            throw new InvalidOperationException("ManualInspectionBindingMissing");
        var active = ReadReference(reader);
        var activeSnapshot = ReadString(reader, 64);
        var activeCamera = ReadString(reader, 64);
        var principal = ReadGuid(reader);
        var session = ReadGuid(reader);
        var revision = reader.ReadInt64();
        var policy = ReadContract(reader) ??
            throw new InvalidOperationException("ManualInspectionPolicyMissing");
        var target = ReadString(reader, 64) ??
            throw new InvalidOperationException("ManualInspectionTargetMissing");
        var changeReason = ReadString(reader, MaximumStringBytes) ??
            throw new InvalidOperationException("ManualInspectionReasonMissing");
        var started = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var phase = ReadEnum<ManualInspectionSessionPhase>(reader.ReadByte(),
            "ManualInspectionPhaseInvalid");
        var restoration = ReadEnum<ManualInspectionRestorationState>(reader.ReadByte(),
            "ManualInspectionRestorationInvalid");
        var reason = ReadString(reader, MaximumStringBytes) ??
            throw new InvalidOperationException("ManualInspectionReasonMissing");
        var recovery = reader.ReadBoolean();
        var contentHash = ReadString(reader, 64) ??
            throw new InvalidOperationException("ManualInspectionContentHashMissing");
        var value = new ManualInspectionSessionHeader(position, sessionId, epoch, startCorrelation,
            attemptId, selection, sourceHash, binding, active, activeSnapshot, activeCamera,
            principal, session, revision, policy, target, changeReason, started, phase, restoration,
            reason, recovery);
        if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("ManualInspectionHeaderContentHashMismatch");
        return value;
    }

    private static void WriteRun(BinaryWriter writer, ManualInspectionRunRecord value)
    {
        writer.Write(value.Position);
        WriteGuid(writer, value.RunId);
        WriteGuid(writer, value.SessionId);
        WriteGuid(writer, value.RuntimeEpoch);
        WriteGuid(writer, value.CommandCorrelationId);
        WriteGuid(writer, value.AttemptId);
        WriteString(writer, value.PartIdentity?.Value, 256);
        writer.Write((byte)value.PartIdentitySource);
        WriteNullableGuid(writer, value.PartIdentityActorPrincipalId);
        WriteNullableGuid(writer, value.PartIdentityActorSessionId);
        writer.Write((byte)value.Status);
        writer.Write((byte)value.Decision);
        WriteNullableByte(writer, value.ExecutionStatus is { } execution ? (byte)execution : null);
        WriteString(writer, value.ReasonCode, MaximumStringBytes);
        writer.Write(value.AdmittedAtUtc.UtcTicks);
        WriteNullableTime(writer, value.StartedAtUtc);
        WriteNullableTime(writer, value.CompletedAtUtc);
        WriteFrameMetadata(writer, value.FrameMetadata);
        WriteFrameProvenance(writer, value.FrameProvenance);
        WriteString(writer, value.AlgorithmResultPayload, MaximumStringBytes);
        WriteString(writer, value.AlgorithmResultContentHash, 64);
        if (value.Evidence.Count > 64) throw new InvalidOperationException("ManualInspectionEvidenceCapacityExceeded");
        writer.Write(value.Evidence.Count);
        foreach (var evidence in value.Evidence)
        {
            WriteString(writer, evidence.Kind, 64);
            WriteString(writer, evidence.ContentHash, 64);
            WriteString(writer, evidence.Reference, 256);
        }
        WriteNullableInt64(writer, value.CommandAuditSequence);
        WriteString(writer, value.CommandAuditHash, 64);
        writer.Write(value.AuditSequence);
        WriteString(writer, value.AuditHash, 64);
        WriteNullableGuid(writer, value.PreparedInstanceId);
        WriteString(writer, value.Algorithm?.Id, 128);
        WriteString(writer, value.Algorithm?.Version, 128);
        WriteString(writer, value.ConfigurationContentHash, 64);
        WriteString(writer, value.ConfigurationSchemaId, 128);
        WriteString(writer, value.ConfigurationSchemaVersion, 128);
        WriteString(writer, value.ConfigurationSchemaContentHash, 64);
        WriteString(writer, value.ResultSchemaContentHash, 64);
        WriteString(writer, value.ResultProjectionContentHash, 64);
        WriteString(writer, value.FrameOverlayContentHash, 64);
        WriteNullableTimeSpanTicks(writer, value.Timing?.AlgorithmExecutionTimeout);
        WriteNullableTimeSpanTicks(writer, value.Timing?.CancellationGracePeriod);
        WriteString(writer, value.Timing?.PolicyId, 128);
        WriteString(writer, value.Timing?.PolicyVersion, 128);
        WriteString(writer, value.Timing?.PolicyContentHash, 64);
        WriteString(writer, value.Timing?.Recipe.Id, 128);
        WriteString(writer, value.Timing?.Recipe.Version, 128);
        WriteString(writer, value.Timing?.Recipe.ContentHash, 64);
        WriteNullableInt64(writer, value.AdmittedMonotonicTimestamp);
        WriteNullableInt64(writer, value.MonotonicFrequency);
        writer.Write(value.DroppedDiagnosticCount);
        WriteString(writer, value.ContentHash, 64);
    }

    private static ManualInspectionRunRecord ReadRun(BinaryReader reader)
    {
        var position = reader.ReadInt64();
        var runId = ReadGuid(reader);
        var sessionId = ReadGuid(reader);
        var epoch = ReadGuid(reader);
        var commandCorrelation = ReadGuid(reader);
        var attemptId = ReadGuid(reader);
        var partValue = ReadString(reader, 256);
        var partSource = ReadEnum<ManualInspectionPartIdentitySource>(reader.ReadByte(),
            "ManualInspectionPartIdentitySourceInvalid");
        var partPrincipal = ReadNullableGuid(reader);
        var partSession = ReadNullableGuid(reader);
        var status = ReadEnum<ManualInspectionRunStatus>(reader.ReadByte(),
            "ManualInspectionRunStatusInvalid");
        var decision = ReadEnum<InspectionDecision>(reader.ReadByte(), "ManualInspectionDecisionInvalid");
        var executionStatus = ReadNullableEnum<ExecutionStatus>(reader,
            "ManualInspectionExecutionStatusInvalid");
        var reason = ReadString(reader, MaximumStringBytes) ??
            throw new InvalidOperationException("ManualInspectionRunReasonMissing");
        var admitted = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var started = ReadNullableTime(reader);
        var completed = ReadNullableTime(reader);
        var frameMetadata = ReadFrameMetadata(reader);
        var frameProvenance = ReadFrameProvenance(reader);
        var resultPayload = ReadString(reader, MaximumStringBytes);
        var resultHash = ReadString(reader, 64);
        var evidenceCount = reader.ReadInt32();
        if (evidenceCount is < 0 or > 64)
            throw new InvalidOperationException("ManualInspectionEvidenceCapacityExceeded");
        var evidence = new List<ManualInspectionEvidenceReference>(evidenceCount);
        for (var index = 0; index < evidenceCount; index++)
        {
            var kind = ReadString(reader, 64) ??
                throw new InvalidOperationException("ManualInspectionEvidenceKindMissing");
            var hash = ReadString(reader, 64) ??
                throw new InvalidOperationException("ManualInspectionEvidenceHashMissing");
            evidence.Add(new ManualInspectionEvidenceReference(kind, hash,
                ReadString(reader, 256)));
        }
        var commandSequence = ReadNullableInt64(reader);
        var commandHash = ReadString(reader, 64);
        var auditSequence = reader.ReadInt64();
        var auditHash = ReadString(reader, 64);
        var prepared = ReadNullableGuid(reader);
        var algorithmId = ReadString(reader, 128);
        var algorithmVersion = ReadString(reader, 128);
        var configHash = ReadString(reader, 64);
        var configSchemaId = ReadString(reader, 128);
        var configSchemaVersion = ReadString(reader, 128);
        var configSchemaHash = ReadString(reader, 64);
        var resultSchemaHash = ReadString(reader, 64);
        var resultProjectionHash = ReadString(reader, 64);
        var overlayHash = ReadString(reader, 64);
        var executionTimeout = ReadNullableTimeSpanTicks(reader);
        var cancellationGrace = ReadNullableTimeSpanTicks(reader);
        var timingPolicyId = ReadString(reader, 128);
        var timingPolicyVersion = ReadString(reader, 128);
        var timingPolicyHash = ReadString(reader, 64);
        var timingRecipeId = ReadString(reader, 128);
        var timingRecipeVersion = ReadString(reader, 128);
        var timingRecipeHash = ReadString(reader, 64);
        var admittedMonotonic = ReadNullableInt64(reader);
        var monotonicFrequency = ReadNullableInt64(reader);
        var droppedDiagnostics = reader.ReadInt64();
        var contentHash = ReadString(reader, 64) ??
            throw new InvalidOperationException("ManualInspectionRunContentHashMissing");
        AlgorithmIdentity? algorithm = algorithmId is null && algorithmVersion is null ? null :
            algorithmId is not null && algorithmVersion is not null ? new AlgorithmIdentity(algorithmId, algorithmVersion) :
            throw new InvalidOperationException("ManualInspectionAlgorithmIdentityInvalid");
        AlgorithmExecutionTimingSnapshot? timing = null;
        if (executionTimeout.HasValue || cancellationGrace.HasValue || timingPolicyId is not null ||
            timingPolicyVersion is not null || timingPolicyHash is not null || timingRecipeId is not null ||
            timingRecipeVersion is not null || timingRecipeHash is not null)
        {
            if (executionTimeout is null || cancellationGrace is null || timingPolicyId is null ||
                timingPolicyVersion is null || timingPolicyHash is null || timingRecipeId is null ||
                timingRecipeVersion is null || timingRecipeHash is null)
                throw new InvalidOperationException("ManualInspectionTimingInvalid");
            timing = new AlgorithmExecutionTimingSnapshot(new RecipeReference(timingRecipeId,
                timingRecipeVersion, timingRecipeHash), timingPolicyId, timingPolicyVersion,
                timingPolicyHash, executionTimeout.Value, cancellationGrace.Value);
        }
        AlgorithmResultSchema? resultSchema = null;
        AlgorithmResult? result = null;
        FrameOverlaySnapshot? frameOverlay = null;
        if (resultPayload is not null)
        {
            if (resultHash is null)
                throw new InvalidOperationException("ManualInspectionResultHashRequired");
            var decoded = AlgorithmResultStorageCodec.Decode(position,
                completed ?? admitted, resultPayload, resultHash);
            if (decoded.RecordId != runId || decoded.PreparedInstanceId != prepared ||
                decoded.Algorithm != algorithm || decoded.FrameMetadata != frameMetadata ||
                decoded.ConfigurationContentHash != configHash ||
                decoded.ConfigurationSchemaId != configSchemaId ||
                decoded.ConfigurationSchemaVersion != configSchemaVersion ||
                decoded.ConfigurationSchemaContentHash != configSchemaHash ||
                decoded.ResultSchema.ContentHash != resultSchemaHash ||
                !SameTiming(decoded.Timing, timing))
                throw new InvalidOperationException("ManualInspectionResultBindingMismatch");
            resultSchema = decoded.ResultSchema;
            result = decoded.Result;
            frameOverlay = decoded.Overlay;
            timing = decoded.Timing;
        }
        var value = new ManualInspectionRunRecord(position, runId, sessionId, epoch,
            commandCorrelation, attemptId, partValue is null ? null : new ManualPartIdentityInput(partValue),
            status, decision, executionStatus, reason, admitted, started, completed, frameMetadata,
            frameProvenance, resultPayload, resultHash, evidence, commandSequence, commandHash,
            auditSequence, auditHash, partSource, partPrincipal, partSession, prepared, algorithm,
            configuration: null, configHash, configSchemaId, configSchemaVersion, configSchemaHash,
            resultSchema, result, frameOverlay, timing, admittedMonotonic,
            monotonicFrequency, droppedDiagnostics, resultProjectionHash, overlayHash,
            resultSchemaContentHash: resultSchemaHash);
        if (!string.Equals(value.ContentHash, contentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("ManualInspectionRunContentHashMismatch");
        return value;
    }

    private static bool SameTiming(AlgorithmExecutionTimingSnapshot left, AlgorithmExecutionTimingSnapshot? right) =>
        right is not null && left.Recipe == right.Recipe && left.PolicyId == right.PolicyId &&
        left.PolicyVersion == right.PolicyVersion && left.PolicyContentHash == right.PolicyContentHash &&
        left.AlgorithmExecutionTimeout == right.AlgorithmExecutionTimeout &&
        left.CancellationGracePeriod == right.CancellationGracePeriod;

    private static void WriteSelection(BinaryWriter writer, ManualRecipeSelection value)
    {
        writer.Write((byte)value.Kind);
        if (value.Kind == ManualRecipeSourceKind.Draft)
        {
            WriteGuid(writer, value.DraftId!.Value);
            writer.Write(value.DraftRevision!.Value);
            WriteString(writer, value.DraftRevisionContentHash, 64);
        }
        else
        {
            WriteString(writer, value.Recipe!.Id, 128);
            WriteString(writer, value.Recipe.Version, 128);
            WriteString(writer, value.Recipe.ContentHash, 64);
            WriteGuid(writer, value.ReleaseId!.Value);
            WriteString(writer, value.ReleaseRecordContentHash, 64);
        }
    }

    private static ManualRecipeSelection ReadSelection(BinaryReader reader)
    {
        var kind = ReadEnum<ManualRecipeSourceKind>(reader.ReadByte(),
            "ManualInspectionRecipeSourceInvalid");
        return kind == ManualRecipeSourceKind.Draft
            ? new ManualRecipeSelection(ReadGuid(reader), reader.ReadInt64(),
                ReadString(reader, 64) ?? throw new InvalidOperationException("ManualInspectionDraftHashMissing"))
            : new ManualRecipeSelection(new RecipeReference(
                ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionRecipeMissing"),
                ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionRecipeMissing"),
                ReadString(reader, 64) ?? throw new InvalidOperationException("ManualInspectionRecipeMissing")),
                ReadGuid(reader), ReadString(reader, 64) ??
                throw new InvalidOperationException("ManualInspectionReleaseHashMissing"));
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
        var role = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionRoleMissing");
        var revision = reader.ReadInt64();
        var operationId = ReadGuid(reader);
        var previousHash = ReadString(reader, 64);
        var revisionHash = ReadString(reader, 64) ?? throw new InvalidOperationException("ManualInspectionBindingHashMissing");
        var provider = new CameraProviderIdentity(
            ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionProviderMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionProviderMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionProviderMissing"),
            ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionProviderMissing"));
        var target = new CameraBindingTarget(provider,
            ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionDeviceMissing"));
        var principal = ReadGuid(reader);
        var session = ReadGuid(reader);
        var authorRevision = reader.ReadInt64();
        var reason = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("ManualInspectionReasonMissing");
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
            ReadString(reader, 64) ?? throw new InvalidOperationException("ManualInspectionReferenceHashMissing"));

    private static void WriteContract(BinaryWriter writer, RecipeContractReference value)
    {
        WriteString(writer, value.Id, 128);
        WriteString(writer, value.Version, 128);
        WriteString(writer, value.ContentHash, 64);
    }

    private static RecipeContractReference? ReadContract(BinaryReader reader)
    {
        var id = ReadString(reader, 128);
        return id is null ? null : new RecipeContractReference(id,
            ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionPolicyMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("ManualInspectionPolicyMissing"));
    }

    private static void WriteFrameMetadata(BinaryWriter writer, FrameMetadata? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteCorrelation(writer, value.Correlation);
        WriteString(writer, value.LogicalCameraRole, 128);
        writer.Write(value.Width);
        writer.Write(value.Height);
        writer.Write(value.StrideBytes);
        writer.Write((byte)value.PixelFormat);
        WriteNullableInt32(writer, value.ValidBits);
        writer.Write(value.HostCaptureUtc.UtcTicks);
        WriteEffectiveConfiguration(writer, value.EffectiveCameraConfiguration);
    }

    private static FrameMetadata? ReadFrameMetadata(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var correlation = ReadCorrelation(reader);
        var role = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionRoleMissing");
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var stride = reader.ReadInt32();
        var format = ReadEnum<VisionPixelFormat>(reader.ReadByte(), "ManualInspectionPixelFormatInvalid");
        var validBits = ReadNullableInt32(reader);
        var captured = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        return new FrameMetadata(correlation, role, width, height, stride, format, validBits,
            captured, ReadEffectiveConfiguration(reader));
    }

    private static void WriteEffectiveConfiguration(BinaryWriter writer, EffectiveCameraConfiguration value)
    {
        writer.Write((byte)value.ProductionAcquisitionMode);
        writer.Write(value.ExposureTimeUs);
        writer.Write(value.GainDb);
        writer.Write(value.RegionOfInterest.OffsetX);
        writer.Write(value.RegionOfInterest.OffsetY);
        writer.Write(value.RegionOfInterest.Width);
        writer.Write(value.RegionOfInterest.Height);
        writer.Write((byte)value.PixelFormat);
        WriteNullableInt32(writer, value.ValidBits);
        writer.Write(value.AcquisitionTimeoutMs);
        writer.Write(value.TriggerDelayUs);
        WriteWhiteBalance(writer, value.WhiteBalanceRgb);
    }

    private static EffectiveCameraConfiguration ReadEffectiveConfiguration(BinaryReader reader) =>
        new(ReadEnum<ProductionAcquisitionMode>(reader.ReadByte(), "ManualInspectionAcquisitionModeInvalid"),
            reader.ReadDouble(), reader.ReadDouble(),
            new RegionOfInterest(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
            ReadEnum<VisionPixelFormat>(reader.ReadByte(), "ManualInspectionPixelFormatInvalid"),
            ReadNullableInt32(reader), reader.ReadInt32(), reader.ReadDouble(), ReadWhiteBalance(reader));

    private static void WriteFrameProvenance(BinaryWriter writer, FrameProvenance? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteCorrelation(writer, value.Correlation);
        WriteString(writer, value.ProviderId, 128);
        WriteString(writer, value.ProviderVersion, 128);
        WriteString(writer, value.AdapterId, 128);
        WriteString(writer, value.AdapterVersion, 128);
        WriteString(writer, value.SdkId, 128);
        WriteString(writer, value.SdkVersion, 128);
        WriteString(writer, value.NativeRuntimeVersion, 128);
        WriteString(writer, value.StableDeviceIdentity, 128);
        WriteString(writer, value.ReportedModel, 128);
        WriteString(writer, value.FirmwareVersion, 128);
        WriteString(writer, value.NativePixelFormatDescription, 256);
        WriteString(writer, value.NormalizationDetails, 2048);
        writer.Write(value.NormalizationAllocated);
        writer.Write(value.NormalizationTransformed);
        WriteDeviceTimestamp(writer, value.DeviceTimestamp);
        writer.Write(value.FrameCounter.HasValue);
        if (value.FrameCounter.HasValue) writer.Write(value.FrameCounter.Value);
        WriteMilestones(writer, value.Milestones);
        writer.Write(value.PoolCopyEvidence is not null);
        if (value.PoolCopyEvidence is { } copy)
        {
            writer.Write(copy.SourceStrideBytes);
            writer.Write(copy.DestinationStrideBytes);
        }
    }

    private static FrameProvenance? ReadFrameProvenance(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var correlation = ReadCorrelation(reader);
        var providerId = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionProviderMissing");
        var providerVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionProviderMissing");
        var adapterId = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionAdapterMissing");
        var adapterVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionAdapterMissing");
        var sdkId = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionSdkMissing");
        var sdkVersion = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionSdkMissing");
        var nativeRuntime = ReadString(reader, 128);
        var device = ReadString(reader, 128) ?? throw new InvalidOperationException("ManualInspectionDeviceMissing");
        var model = ReadString(reader, 128);
        var firmware = ReadString(reader, 128);
        var nativeFormat = ReadString(reader, 256) ?? throw new InvalidOperationException("ManualInspectionPixelDescriptionMissing");
        var normalization = ReadString(reader, 2048) ?? throw new InvalidOperationException("ManualInspectionNormalizationMissing");
        var allocated = reader.ReadBoolean();
        var transformed = reader.ReadBoolean();
        var timestamp = ReadDeviceTimestamp(reader);
        ulong? frameCounter = reader.ReadBoolean() ? reader.ReadUInt64() : null;
        var milestones = ReadMilestones(reader);
        var hasCopy = reader.ReadBoolean();
        var sourceStride = hasCopy ? reader.ReadInt32() : 0;
        var destinationStride = hasCopy ? reader.ReadInt32() : 0;
        var value = new FrameProvenance(correlation, providerId, providerVersion, adapterId,
            adapterVersion, sdkId, sdkVersion, nativeRuntime, device, model, firmware, nativeFormat,
            normalization, allocated, transformed, timestamp, frameCounter, milestones);
        return hasCopy ? value.WithPoolCopyEvidence(sourceStride, destinationStride) : value;
    }

    private static void WriteCorrelation(BinaryWriter writer, ExecutionCorrelationId value)
    {
        writer.Write((byte)value.Kind);
        WriteGuid(writer, value.Value);
    }

    private static ExecutionCorrelationId ReadCorrelation(BinaryReader reader) =>
        new(ReadEnum<ExecutionKind>(reader.ReadByte(), "ManualInspectionExecutionKindInvalid"), ReadGuid(reader));

    private static void WriteWhiteBalance(BinaryWriter writer, WhiteBalanceRgb? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.Red);
        writer.Write(value.Green);
        writer.Write(value.Blue);
    }

    private static WhiteBalanceRgb? ReadWhiteBalance(BinaryReader reader) => !reader.ReadBoolean()
        ? null : new WhiteBalanceRgb(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    private static void WriteDeviceTimestamp(BinaryWriter writer, DeviceTimestamp? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.Value);
        WriteNullableInt64(writer, value.TickFrequency);
        WriteString(writer, value.Unit, 64);
        WriteString(writer, value.ClockDomain, 64);
        WriteNullableInt64(writer, value.CounterRollover);
        writer.Write((byte)value.Synchronization);
    }

    private static DeviceTimestamp? ReadDeviceTimestamp(BinaryReader reader) => !reader.ReadBoolean()
        ? null : new DeviceTimestamp(reader.ReadInt64(), ReadNullableInt64(reader),
            ReadString(reader, 64), ReadString(reader, 64) ??
            throw new InvalidOperationException("ManualInspectionClockDomainMissing"),
            ReadNullableInt64(reader), ReadEnum<DeviceClockSynchronization>(reader.ReadByte(),
                "ManualInspectionClockSynchronizationInvalid"));

    private static void WriteMilestones(BinaryWriter writer, FrameAcquisitionMilestones value)
    {
        writer.Write(value.MonotonicFrequency);
        WriteTimePoint(writer, value.TriggerAccepted);
        WriteTimePoint(writer, value.AcquisitionStarted);
        WriteTimePoint(writer, value.NativeFrameReceived);
        WriteTimePoint(writer, value.NormalizedFrameReady);
    }

    private static FrameAcquisitionMilestones ReadMilestones(BinaryReader reader) =>
        new(reader.ReadInt64(), ReadTimePoint(reader), ReadTimePoint(reader), ReadTimePoint(reader),
            ReadTimePoint(reader));

    private static void WriteTimePoint(BinaryWriter writer, FrameTimePoint? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        writer.Write(value.HostObservedAtUtc.UtcTicks);
        writer.Write(value.MonotonicTimestamp);
    }

    private static FrameTimePoint? ReadTimePoint(BinaryReader reader) => !reader.ReadBoolean()
        ? null : new FrameTimePoint(new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero), reader.ReadInt64());

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static void WriteNullableGuid(BinaryWriter writer, Guid? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) WriteGuid(writer, value.Value);
    }
    private static Guid ReadGuid(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16) throw new InvalidOperationException("ManualInspectionGuidInvalid");
        return new Guid(bytes);
    }
    private static Guid? ReadNullableGuid(BinaryReader reader) => reader.ReadBoolean() ? ReadGuid(reader) : null;
    private static void WriteNullableTime(BinaryWriter writer, DateTimeOffset? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value.UtcTicks);
    }
    private static DateTimeOffset? ReadNullableTime(BinaryReader reader) => reader.ReadBoolean()
        ? new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero) : null;
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
    private static void WriteNullableByte(BinaryWriter writer, byte? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    private static T? ReadNullableEnum<T>(BinaryReader reader, string reason) where T : struct, Enum
    {
        if (!reader.ReadBoolean()) return null;
        return ReadEnum<T>(reader.ReadByte(), reason);
    }

    private static void WriteNullableTimeSpanTicks(BinaryWriter writer, TimeSpan? value) =>
        WriteNullableInt64(writer, value?.Ticks);

    private static TimeSpan? ReadNullableTimeSpanTicks(BinaryReader reader)
    {
        var ticks = ReadNullableInt64(reader);
        if (ticks is null) return null;
        try { return new TimeSpan(ticks.Value); }
        catch (ArgumentOutOfRangeException exception)
        { throw new InvalidOperationException("ManualInspectionTimingInvalid", exception); }
    }

    private static void WriteString(BinaryWriter writer, string? value, int maximumCharacters)
    {
        writer.Write(value is not null);
        if (value is null) return;
        if (value.Length > maximumCharacters) throw new InvalidOperationException("ManualInspectionStringCapacityExceeded");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > checked(maximumCharacters * 4))
            throw new InvalidOperationException("ManualInspectionStringCapacityExceeded");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string? ReadString(BinaryReader reader, int maximumCharacters)
    {
        if (!reader.ReadBoolean()) return null;
        var length = reader.ReadInt32();
        if (length is < 0 or > 64 * 1024 * 1024)
            throw new InvalidOperationException("ManualInspectionStringCapacityExceeded");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("ManualInspectionStringInvalid");
        var value = new UTF8Encoding(false, true).GetString(bytes);
        if (value.Length > maximumCharacters)
            throw new InvalidOperationException("ManualInspectionStringCapacityExceeded");
        return value;
    }

    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum
    {
        var decoded = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(typeof(T), decoded) ? decoded : throw new InvalidOperationException(reason);
    }
}

internal sealed record ManualInspectionStorageValue(ManualInspectionSessionEvent Event,
    ManualInspectionRunRecord? Run);
