using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Strict wire representation for one recipe activation event.  The event keeps
/// the complete immutable admission/terminal evidence; the SQL columns are only
/// indexes and audit links.  This codec deliberately does not use a serializer
/// whose constructor selection could silently omit Runtime-only evidence.
/// </summary>
internal static class RecipeActivationStorageCodec
{
    internal const int MaximumPayloadBytes = RecipeActivationStoreOptions.MaximumPayloadBytesHardLimit;
    private const int Magic = 0x31414152; // RAA1
    private const byte FormatVersion = 1;
    private const byte HistoricalFormatVersion = 2;
    private const int MaximumStringBytes = 256 * 1024;
    private const int MaximumIdentifierBytes = 1024;
    private const int MaximumCheckCount = 256;
    private const int MaximumSelectionCount = 8;

    /// <summary>
    /// Encodes the exact activation snapshot using the already versioned
    /// activation wire representation. This wrapper intentionally adds no new
    /// snapshot format; production Core stores the resulting bytes as an
    /// opaque immutable baseline.
    /// </summary>
    internal static byte[] EncodeSnapshot(RecipeActivationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
            WriteSnapshot(writer, snapshot);
        return stream.ToArray();
    }

    internal static RecipeActivationSnapshot DecodeSnapshot(ReadOnlyMemory<byte> payload,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver = null)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeActivationSnapshotPayloadCapacityExceeded");
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
        var value = ReadSnapshot(reader, profileResolver);
        if (value is null || stream.Position != stream.Length)
            throw new InvalidOperationException("RecipeActivationSnapshotPayloadInvalid");
        return value;
    }

    internal static byte[] Encode(RecipeActivationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Magic);
            var version = record.HistoricalSelection is null ? FormatVersion : HistoricalFormatVersion;
            writer.Write(version);
            WriteRecord(writer, record, version);
            WriteString(writer, record.ContentHash, 64);
        }
        if (stream.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeActivationPayloadCapacityExceeded");
        return stream.ToArray();
    }

    internal static RecipeActivationRecord Decode(ReadOnlyMemory<byte> payload,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver = null)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeActivationPayloadCapacityExceeded");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
            if (reader.ReadInt32() != Magic)
                throw new InvalidOperationException("RecipeActivationPayloadVersionUnsupported");
            var version = reader.ReadByte();
            if (version is not (FormatVersion or HistoricalFormatVersion))
                throw new InvalidOperationException("RecipeActivationPayloadVersionUnsupported");
            var record = ReadRecord(reader, profileResolver, version);
            if ((version == HistoricalFormatVersion) != (record.HistoricalSelection is not null))
                throw new InvalidOperationException("RecipeActivationHistoricalSelectionVersionMismatch");
            var savedHash = ReadString(reader, 64);
            if (!string.Equals(savedHash, record.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeActivationContentHashMismatch");
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("RecipeActivationPayloadTrailingBytes");
            if (!payload.Span.SequenceEqual(Encode(record)))
                throw new InvalidOperationException("RecipeActivationPayloadCanonicalMismatch");
            return record;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not InvalidOperationException
            { Message: "RecipeActivationPayloadCapacityExceeded" })
        {
            throw new InvalidOperationException("RecipeActivationPayloadInvalid", exception);
        }
    }

    private static void WriteRecord(BinaryWriter writer, RecipeActivationRecord record, byte version)
    {
        writer.Write(record.Position);
        WriteGuid(writer, record.ActivationId);
        WriteGuid(writer, record.AttemptId);
        WriteGuid(writer, record.OperationId);
        WriteReference(writer, record.AdmissionReference);
        WriteReference(writer, record.PreviousActivation);
        WriteRecipe(writer, record.PreviousRecipe);
        WriteString(writer, record.PreviousSnapshotContentHash, 64);
        WriteRecipe(writer, record.Candidate);
        WriteGuid(writer, record.ReleaseId);
        WriteString(writer, record.ReleaseRecordContentHash, 64);
        WriteRecipe(writer, record.ResultingRecipe);
        WriteOutcome(writer, record.Outcome);
        WriteChecks(writer, record.Checks);
        WriteRestoration(writer, record.Restoration);
        WriteSnapshot(writer, record.SuccessfulSnapshot);
        writer.Write((byte)record.EvidenceKind);
        WriteNullableGuid(writer, record.ActorPrincipalId);
        WriteNullableGuid(writer, record.ActorSessionId);
        WriteNullableInt64(writer, record.ActorAuthorizationRevision);
        WriteContractReference(writer, record.AuthorizationPolicy);
        WriteString(writer, record.ChangeReason, MaximumStringBytes);
        WriteString(writer, record.AuthorizationTarget, 64);
        writer.Write(record.RecordedAtUtc.UtcTicks);
        if (version >= HistoricalFormatVersion) WriteHistoricalSelection(writer, record.HistoricalSelection);
        WriteAdmission(writer, record.Admission, version);
    }

    private static RecipeActivationRecord ReadRecord(BinaryReader reader,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver, byte version)
    {
        var position = reader.ReadInt64();
        var activationId = ReadGuid(reader);
        var attemptId = ReadGuid(reader);
        var operationId = ReadGuid(reader);
        var admissionReference = ReadReference(reader);
        var previousActivation = ReadReference(reader);
        var previousRecipe = ReadRecipe(reader);
        var previousSnapshotHash = ReadString(reader, 64);
        var candidate = ReadRecipe(reader) ?? throw new InvalidOperationException("RecipeActivationCandidateMissing");
        var releaseId = ReadGuid(reader);
        var releaseHash = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationReleaseHashMissing");
        var resultingRecipe = ReadRecipe(reader);
        var outcome = ReadOutcome(reader);
        var checks = ReadChecks(reader);
        var restoration = ReadRestoration(reader);
        var snapshot = ReadSnapshot(reader, profileResolver);
        var evidenceKind = ReadEnum<RecipeActivationEvidenceKind>(reader.ReadByte(),
            "RecipeActivationEvidenceKindInvalid");
        var actorPrincipal = ReadNullableGuid(reader);
        var actorSession = ReadNullableGuid(reader);
        var actorRevision = ReadNullableInt64(reader);
        var authorizationPolicy = ReadContractReference(reader);
        var changeReason = ReadString(reader, MaximumStringBytes) ??
            throw new InvalidOperationException("RecipeActivationChangeReasonMissing");
        var authorizationTarget = ReadString(reader, 64) ??
            throw new InvalidOperationException("RecipeActivationAuthorizationTargetMissing");
        var recordedAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var historicalSelection = version >= HistoricalFormatVersion ? ReadHistoricalSelection(reader) : null;
        var admission = ReadAdmission(reader, profileResolver, version);
        return new RecipeActivationRecord(position, activationId, attemptId, operationId,
            admissionReference, previousActivation, previousRecipe, previousSnapshotHash,
            candidate, releaseId, releaseHash, resultingRecipe, outcome, checks, restoration,
            snapshot, evidenceKind, actorPrincipal, actorSession, actorRevision, authorizationPolicy,
            changeReason, authorizationTarget, recordedAtUtc, admission, historicalSelection);
    }

    private static void WriteAdmission(BinaryWriter writer, RecipeActivationAdmission? admission, byte version)
    {
        writer.Write(admission is not null);
        if (admission is null) return;
        writer.Write(admission.Position);
        WriteGuid(writer, admission.ActivationId);
        WriteGuid(writer, admission.AttemptId);
        WriteGuid(writer, admission.OperationId);
        WriteRecipe(writer, admission.Candidate);
        WriteGuid(writer, admission.ReleaseId);
        WriteString(writer, admission.ReleaseRecordContentHash, 64);
        WriteReference(writer, admission.ExpectedActive);
        WriteReference(writer, admission.PreviousActivation);
        WriteRecipe(writer, admission.PreviousRecipe);
        WriteString(writer, admission.PreviousSnapshotContentHash, 64);
        WriteSelections(writer, admission.CalibrationSelections);
        WriteString(writer, admission.ChangeReason, MaximumStringBytes);
        WriteGuid(writer, admission.ActorPrincipalId);
        WriteGuid(writer, admission.ActorSessionId);
        writer.Write(admission.ActorAuthorizationRevision);
        WriteContractReference(writer, admission.AuthorizationPolicy);
        WriteString(writer, admission.AuthorizationTarget, 64);
        writer.Write((byte)admission.EvidenceKind);
        writer.Write(admission.AdmittedAtUtc.UtcTicks);
        if (version >= HistoricalFormatVersion) WriteHistoricalSelection(writer, admission.HistoricalSelection);
        WriteString(writer, admission.ContentHash, 64);
    }

    private static RecipeActivationAdmission? ReadAdmission(BinaryReader reader,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver, byte version)
    {
        if (!reader.ReadBoolean()) return null;
        var position = reader.ReadInt64();
        var activationId = ReadGuid(reader);
        var attemptId = ReadGuid(reader);
        var operationId = ReadGuid(reader);
        var candidate = ReadRecipe(reader) ?? throw new InvalidOperationException("RecipeActivationAdmissionCandidateMissing");
        var releaseId = ReadGuid(reader);
        var releaseHash = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationAdmissionReleaseHashMissing");
        var expectedActive = ReadReference(reader);
        var previousActivation = ReadReference(reader);
        var previousRecipe = ReadRecipe(reader);
        var previousSnapshotHash = ReadString(reader, 64);
        var selections = ReadSelections(reader);
        var reason = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("RecipeActivationAdmissionReasonMissing");
        var actorPrincipal = ReadGuid(reader);
        var actorSession = ReadGuid(reader);
        var actorRevision = reader.ReadInt64();
        var policy = ReadContractReference(reader) ?? throw new InvalidOperationException("RecipeActivationAdmissionPolicyMissing");
        var target = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationAdmissionTargetMissing");
        var evidenceKind = ReadEnum<RecipeActivationEvidenceKind>(reader.ReadByte(), "RecipeActivationEvidenceKindInvalid");
        var admittedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var historicalSelection = version >= HistoricalFormatVersion ? ReadHistoricalSelection(reader) : null;
        var contentHash = ReadString(reader, 64);
        var admission = new RecipeActivationAdmission(position, activationId, attemptId, operationId,
            candidate, releaseId, releaseHash, expectedActive, previousActivation, previousRecipe,
            previousSnapshotHash, selections, reason, actorPrincipal, actorSession, actorRevision,
            policy, target, evidenceKind, admittedAt, historicalSelection);
        if (!string.Equals(contentHash, admission.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeActivationAdmissionContentHashMismatch");
        return admission;
    }

    private static void WriteOutcome(BinaryWriter writer, RecipeActivationOutcome value)
    {
        writer.Write((byte)value.State);
        WriteString(writer, value.ReasonCode, MaximumIdentifierBytes);
        WriteString(writer, value.ContentHash, 64);
    }

    private static RecipeActivationOutcome ReadOutcome(BinaryReader reader)
    {
        var state = ReadEnum<RecipeActivationOutcomeState>(reader.ReadByte(), "RecipeActivationOutcomeStateInvalid");
        var reason = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationOutcomeReasonMissing");
        var hash = ReadString(reader, 64);
        var result = new RecipeActivationOutcome(state, reason);
        if (!string.Equals(hash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeActivationOutcomeContentHashMismatch");
        return result;
    }

    private static void WriteChecks(BinaryWriter writer, IReadOnlyList<RecipeActivationCheck> checks)
    {
        WriteCount(writer, checks.Count, MaximumCheckCount);
        foreach (var check in checks)
        {
            WriteString(writer, check.CheckId, MaximumIdentifierBytes);
            WriteString(writer, check.Subject, MaximumStringBytes);
            writer.Write((byte)check.Status);
            WriteString(writer, check.ReasonCode, MaximumIdentifierBytes);
            WriteString(writer, check.RequestedEvidenceHash, 64);
            WriteString(writer, check.EffectiveEvidenceHash, 64);
            WriteString(writer, check.ContentHash, 64);
        }
    }

    private static IReadOnlyList<RecipeActivationCheck> ReadChecks(BinaryReader reader)
    {
        var count = ReadCount(reader, MaximumCheckCount);
        var checks = new List<RecipeActivationCheck>(count);
        for (var index = 0; index < count; index++)
        {
            var id = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCheckIdMissing");
            var subject = ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("RecipeActivationCheckSubjectMissing");
            var status = ReadEnum<RecipeActivationCheckStatus>(reader.ReadByte(), "RecipeActivationCheckStatusInvalid");
            var reason = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCheckReasonMissing");
            var requested = ReadString(reader, 64);
            var effective = ReadString(reader, 64);
            var hash = ReadString(reader, 64);
            var check = new RecipeActivationCheck(id, subject, status, reason, requested, effective);
            if (!string.Equals(hash, check.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeActivationCheckContentHashMismatch");
            checks.Add(check);
        }
        return checks;
    }

    private static void WriteRestoration(BinaryWriter writer, RecipeActivationRestoration value)
    {
        writer.Write((byte)value.State);
        WriteString(writer, value.ReasonCode, MaximumIdentifierBytes);
        WriteString(writer, value.RequestedEvidenceHash, 64);
        WriteString(writer, value.EffectiveEvidenceHash, 64);
        WriteCamera(writer, value.ActualCamera);
        WriteString(writer, value.ContentHash, 64);
    }

    private static RecipeActivationRestoration ReadRestoration(BinaryReader reader)
    {
        var state = ReadEnum<RecipeActivationRestorationState>(reader.ReadByte(), "RecipeActivationRestorationStateInvalid");
        var reason = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationRestorationReasonMissing");
        var requested = ReadString(reader, 64);
        var effective = ReadString(reader, 64);
        var camera = ReadCamera(reader);
        var hash = ReadString(reader, 64);
        var restoration = new RecipeActivationRestoration(state, reason, requested, effective, camera);
        if (!string.Equals(hash, restoration.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeActivationRestorationContentHashMismatch");
        return restoration;
    }

    private static void WriteSnapshot(BinaryWriter writer, RecipeActivationSnapshot? snapshot)
    {
        writer.Write(snapshot is not null);
        if (snapshot is null) return;
        writer.Write((byte)snapshot.EvidenceKind);
        var releasePayload = RecipeReleaseStorageCodec.Encode(snapshot.Release);
        WriteBytes(writer, releasePayload, RecipeReleaseStorageCodec.MaximumPayloadBytes);
        WriteGuid(writer, snapshot.PreparedAlgorithmInstanceId);
        WriteString(writer, snapshot.AlgorithmExecutionPolicy.Id, MaximumIdentifierBytes);
        WriteString(writer, snapshot.AlgorithmExecutionPolicy.Version, MaximumIdentifierBytes);
        writer.Write(snapshot.AlgorithmExecutionPolicy.MinimumExecutionTimeout.Ticks);
        writer.Write(snapshot.AlgorithmExecutionPolicy.MaximumExecutionTimeout.Ticks);
        writer.Write(snapshot.AlgorithmExecutionPolicy.CancellationGracePeriod.Ticks);
        WriteCamera(writer, snapshot.CameraSetup);
        var plcPayload = EncodePlcBinding(snapshot.Release, snapshot.PlcResultContract);
        WriteBytes(writer, plcPayload, PlcResultContractStorageCodec.MaximumPayloadBytes);
        WriteCount(writer, snapshot.CalibrationBindings.Count, MaximumSelectionCount);
        foreach (var binding in snapshot.CalibrationBindings)
        {
            WriteString(writer, binding.RequirementContentHash, 64);
            WriteString(writer, binding.Profile.ProfileId.ToString("D"), MaximumIdentifierBytes);
            writer.Write(binding.Profile.Version);
            WriteString(writer, binding.Profile.ContentHash, 64);
            WriteVerificationReference(writer, binding.Verification);
            WriteNullableDateTime(writer, binding.ValidUntilUtc);
            WriteString(writer, binding.ContentHash, 64);
        }
        writer.Write(snapshot.FramePoolCapacity);
        writer.Write(snapshot.FramePoolMaximumBytes);
        WriteString(writer, snapshot.ContentHash, 64);
    }

    private static RecipeActivationSnapshot? ReadSnapshot(BinaryReader reader,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver)
    {
        if (!reader.ReadBoolean()) return null;
        var evidenceKind = ReadEnum<RecipeActivationEvidenceKind>(reader.ReadByte(), "RecipeActivationEvidenceKindInvalid");
        var releasePayload = ReadBytes(reader, RecipeReleaseStorageCodec.MaximumPayloadBytes);
        var release = RecipeReleaseStorageCodec.Decode(releasePayload);
        var prepared = ReadGuid(reader);
        var policyId = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationExecutionPolicyIdMissing");
        var policyVersion = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationExecutionPolicyVersionMissing");
        var policy = new AlgorithmExecutionPolicy(policyId, policyVersion,
            TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64()));
        var camera = ReadCamera(reader) ?? throw new InvalidOperationException("RecipeActivationCameraSnapshotMissing");
        var plcPayload = ReadBytes(reader, PlcResultContractStorageCodec.MaximumPayloadBytes);
        var plcRevision = PlcResultContractStorageCodec.Decode(plcPayload);
        if (plcRevision.Bindings.Count != 1)
            throw new InvalidOperationException("RecipeActivationPlcBindingInvalid");
        var plc = plcRevision.Bindings[0].Binding;
        var count = ReadCount(reader, MaximumSelectionCount);
        var calibration = new List<CalibrationRunProfileBinding>(count);
        for (var index = 0; index < count; index++)
        {
            var requirementHash = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationCalibrationRequirementMissing");
            var profileId = ParseGuid(ReadString(reader, MaximumIdentifierBytes), "RecipeActivationCalibrationProfileInvalid");
            var version = reader.ReadInt64();
            var profileHash = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationCalibrationProfileHashMissing");
            var verification = ReadVerificationReference(reader);
            var validUntil = ReadNullableDateTime(reader);
            var savedBindingHash = ReadString(reader, 64);
            if (profileResolver is null)
                throw new InvalidOperationException("RecipeActivationCalibrationBindingResolverRequired");
            var profile = profileResolver(new CalibrationProfileReference(profileId, version, profileHash))
                ?? throw new InvalidOperationException("RecipeActivationCalibrationProfileUnavailable");
            var binding = new CalibrationRunProfileBinding(requirementHash, profile, verification, validUntil);
            if (!string.Equals(savedBindingHash, binding.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeActivationCalibrationBindingHashMismatch");
            calibration.Add(binding);
        }
        var framePoolCapacity = reader.ReadInt32();
        var framePoolMaximumBytes = reader.ReadInt64();
        var savedHash = ReadString(reader, 64);
        var result = new RecipeActivationSnapshot(evidenceKind, release, prepared, policy, camera, plc,
            calibration, framePoolCapacity, framePoolMaximumBytes);
        if (!string.Equals(savedHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeActivationSnapshotContentHashMismatch");
        return result;
    }

    private static byte[] EncodePlcBinding(RecipeReleaseRecord release, PlcResultContractBinding binding)
    {
        var bindingRecord = new PlcReleasedRecipeBinding(release.ReleaseId, release.ContentHash, binding);
        var policyHash = AlgorithmContractValidation.HashParts(new[] { "activation-codec-policy-v1" });
        var policy = new RecipeContractReference("activation-codec-policy", "1", policyHash);
        var revisionId = DeterministicGuid("activation-codec-revision", binding.ContentHash);
        var operationId = DeterministicGuid("activation-codec-operation", binding.ContentHash);
        var actor = DeterministicGuid("activation-codec-actor", binding.ContentHash);
        var target = ChangePlcResultContractCommand.ComputeAuthorizationTarget(binding.Contract, null,
            "activation-codec");
        var revision = new PlcResultContractRevision(1, revisionId, operationId, binding.Contract, null, 1,
            new[] { binding.Validation }, new[] { bindingRecord }, actor, actor, 0, actor, policy,
            "activation-codec", target, DateTimeOffset.UnixEpoch.AddTicks(1));
        return PlcResultContractStorageCodec.Encode(revision);
    }

    private static Guid DeterministicGuid(string domain, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(domain + "\0" + value));
        var guidBytes = bytes[..16].ToArray();
        var result = new Guid(guidBytes);
        return result == Guid.Empty ? Guid.Parse("00000000-0000-0000-0000-000000000001") : result;
    }

    private static void WriteSelections(BinaryWriter writer, IReadOnlyList<CalibrationProfileSelection> selections)
    {
        WriteCount(writer, selections.Count, MaximumSelectionCount);
        foreach (var selection in selections)
        {
            WriteString(writer, selection.RequirementContentHash, 64);
            WriteGuid(writer, selection.Profile.ProfileId);
            writer.Write(selection.Profile.Version);
            WriteString(writer, selection.Profile.ContentHash, 64);
        }
    }

    private static IReadOnlyList<CalibrationProfileSelection> ReadSelections(BinaryReader reader)
    {
        var count = ReadCount(reader, MaximumSelectionCount);
        var result = new List<CalibrationProfileSelection>(count);
        for (var index = 0; index < count; index++)
        {
            var requirement = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationSelectionRequirementMissing");
            var profile = new CalibrationProfileReference(ReadGuid(reader), reader.ReadInt64(),
                ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationSelectionProfileMissing"));
            result.Add(new CalibrationProfileSelection(requirement, profile));
        }
        return result;
    }

    private static void WriteHistoricalSelection(BinaryWriter writer,
        HistoricalCalibrationSelectionIntent? selection)
    {
        writer.Write(selection is not null);
        if (selection is null) return;
        WriteString(writer, selection.Source, MaximumIdentifierBytes);
        WriteProfileReference(writer, selection.PreviousExactProfile);
        WriteString(writer, selection.Reason, MaximumStringBytes);
        WriteString(writer, selection.ContentHash, 64);
    }

    private static HistoricalCalibrationSelectionIntent? ReadHistoricalSelection(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var source = ReadString(reader, MaximumIdentifierBytes) ??
            throw new InvalidOperationException("HistoricalCalibrationSelectionSourceMissing");
        var previous = ReadProfileReference(reader);
        var reason = ReadString(reader, MaximumStringBytes) ??
            throw new InvalidOperationException("HistoricalCalibrationSelectionReasonMissing");
        var contentHash = ReadString(reader, 64) ??
            throw new InvalidOperationException("HistoricalCalibrationSelectionHashMissing");
        var result = new HistoricalCalibrationSelectionIntent(source, previous, reason);
        if (!string.Equals(contentHash, result.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("HistoricalCalibrationSelectionHashMismatch");
        return result;
    }

    private static void WriteProfileReference(BinaryWriter writer, CalibrationProfileReference? profile)
    {
        writer.Write(profile is not null);
        if (profile is null) return;
        WriteGuid(writer, profile.ProfileId);
        writer.Write(profile.Version);
        WriteString(writer, profile.ContentHash, 64);
    }

    private static CalibrationProfileReference? ReadProfileReference(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new CalibrationProfileReference(ReadGuid(reader), reader.ReadInt64(),
            ReadString(reader, 64) ?? throw new InvalidOperationException(
                "HistoricalCalibrationSelectionProfileHashMissing"));
    }

    private static void WriteRecipe(BinaryWriter writer, RecipeReference? recipe)
    {
        writer.Write(recipe is not null);
        if (recipe is null) return;
        WriteString(writer, recipe.Id, MaximumIdentifierBytes);
        WriteString(writer, recipe.Version, MaximumIdentifierBytes);
        WriteString(writer, recipe.ContentHash, 64);
    }

    private static RecipeReference? ReadRecipe(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var id = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationRecipeIdMissing");
        var version = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationRecipeVersionMissing");
        var hash = ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationRecipeHashMissing");
        return new RecipeReference(id, version, hash);
    }

    private static void WriteReference(BinaryWriter writer, RecipeActivationReference? reference)
    {
        writer.Write(reference is not null);
        if (reference is null) return;
        writer.Write(reference.Position);
        WriteGuid(writer, reference.ActivationId);
        WriteString(writer, reference.ContentHash, 64);
    }

    private static RecipeActivationReference? ReadReference(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new RecipeActivationReference(reader.ReadInt64(), ReadGuid(reader),
            ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationReferenceHashMissing"));
    }

    private static void WriteContractReference(BinaryWriter writer, RecipeContractReference? reference)
    {
        writer.Write(reference is not null);
        if (reference is null) return;
        WriteString(writer, reference.Id, MaximumIdentifierBytes);
        WriteString(writer, reference.Version, MaximumIdentifierBytes);
        WriteString(writer, reference.ContentHash, 64);
    }

    private static RecipeContractReference? ReadContractReference(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new RecipeContractReference(
            ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationContractIdMissing"),
            ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationContractVersionMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationContractHashMissing"));
    }

    private static void WriteCamera(BinaryWriter writer, CameraSetupSnapshot? snapshot)
    {
        writer.Write(snapshot is not null);
        if (snapshot is null) return;
        WriteString(writer, snapshot.LogicalRole, MaximumIdentifierBytes);
        writer.Write(snapshot.Binding is not null);
        if (snapshot.Binding is not null) WriteCameraBinding(writer, snapshot.Binding);
        WriteHealth(writer, snapshot.Health);
        WriteRequested(writer, snapshot.Requested);
        WriteEffective(writer, snapshot.Effective);
        WriteCount(writer, snapshot.Differences.Count, 6);
        foreach (var difference in snapshot.Differences)
        {
            writer.Write((byte)difference.Setting);
            writer.Write(difference.Requested);
            writer.Write(difference.Effective);
        }
        WriteExtension(writer, snapshot.Extension);
        WriteString(writer, snapshot.ReasonCode, MaximumIdentifierBytes);
        WriteCapabilities(writer, snapshot.Capabilities);
    }

    private static CameraSetupSnapshot? ReadCamera(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var role = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraRoleMissing");
        CameraBindingRevision? binding = null;
        if (reader.ReadBoolean()) binding = ReadCameraBinding(reader);
        var health = ReadHealth(reader);
        var requested = ReadRequested(reader);
        var effective = ReadEffective(reader);
        var differences = new List<CameraConfigurationDifference>(ReadCount(reader, 6));
        for (var index = 0; index < differences.Capacity; index++)
            differences.Add(new CameraConfigurationDifference(ReadEnum<CameraNumericSetting>(reader.ReadByte(), "RecipeActivationCameraDifferenceInvalid"),
                reader.ReadDouble(), reader.ReadDouble()));
        var extension = ReadExtension(reader);
        var reason = ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraReasonMissing");
        var capabilities = ReadCapabilities(reader);
        return new CameraSetupSnapshot(role, binding, health, requested, effective, differences, extension, reason, capabilities);
    }

    private static void WriteCameraBinding(BinaryWriter writer, CameraBindingRevision value)
    {
        writer.Write(value.Position);
        WriteString(writer, value.LogicalRole, MaximumIdentifierBytes);
        writer.Write(value.Revision);
        WriteGuid(writer, value.OperationId);
        WriteString(writer, value.PreviousRevisionHash, 64);
        WriteString(writer, value.RevisionHash, 64);
        WriteBindingTarget(writer, value.Target);
        WriteGuid(writer, value.AuthorPrincipalId);
        WriteGuid(writer, value.AuthorSessionId);
        writer.Write(value.AuthorAuthorizationRevision);
        WriteString(writer, value.ChangeReason, MaximumStringBytes);
        writer.Write(value.RecordedAtUtc.UtcTicks);
    }

    private static CameraBindingRevision ReadCameraBinding(BinaryReader reader) => new(reader.ReadInt64(),
        ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraBindingRoleMissing"),
        reader.ReadInt64(), ReadGuid(reader), ReadString(reader, 64),
        ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationCameraBindingHashMissing"),
        ReadBindingTarget(reader), ReadGuid(reader), ReadGuid(reader), reader.ReadInt64(),
        ReadString(reader, MaximumStringBytes) ?? throw new InvalidOperationException("RecipeActivationCameraBindingReasonMissing"),
        new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));

    private static void WriteBindingTarget(BinaryWriter writer, CameraBindingTarget value)
    {
        WriteProvider(writer, value.Provider);
        WriteString(writer, value.StableDeviceIdentity, MaximumIdentifierBytes);
    }

    private static CameraBindingTarget ReadBindingTarget(BinaryReader reader) => new(ReadProvider(reader),
        ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraDeviceMissing"));

    private static void WriteProvider(BinaryWriter writer, CameraProviderIdentity value)
    {
        WriteString(writer, value.Id, MaximumIdentifierBytes);
        WriteString(writer, value.Version, MaximumIdentifierBytes);
        WriteString(writer, value.AdapterPackageId, MaximumIdentifierBytes);
        WriteString(writer, value.AdapterVersion, MaximumIdentifierBytes);
    }

    private static CameraProviderIdentity ReadProvider(BinaryReader reader) => new(
        ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraProviderIdMissing"),
        ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraProviderVersionMissing"),
        ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraAdapterIdMissing"),
        ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraAdapterVersionMissing"));

    private static void WriteHealth(BinaryWriter writer, CameraHealthSnapshot value)
    {
        writer.Write((byte)value.ProviderAvailability);
        writer.Write((byte)value.Connection);
        writer.Write((byte)value.Configuration);
        writer.Write((byte)value.Acquisition);
        writer.Write(value.ObservedAt.HostObservedAtUtc.UtcTicks);
        writer.Write(value.ObservedAt.MonotonicTimestamp);
        writer.Write(value.LastFault is not null);
        if (value.LastFault is not null)
        {
            writer.Write((byte)value.LastFault.Classification);
            WriteString(writer, value.LastFault.ReasonCode, MaximumIdentifierBytes);
            WriteString(writer, value.LastFault.DiagnosticCode, MaximumIdentifierBytes);
        }
    }

    private static CameraHealthSnapshot ReadHealth(BinaryReader reader)
    {
        var provider = ReadEnum<CameraProviderAvailability>(reader.ReadByte(), "RecipeActivationCameraProviderAvailabilityInvalid");
        var connection = ReadEnum<CameraConnectionState>(reader.ReadByte(), "RecipeActivationCameraConnectionInvalid");
        var configuration = ReadEnum<CameraConfigurationState>(reader.ReadByte(), "RecipeActivationCameraConfigurationInvalid");
        var acquisition = ReadEnum<CameraAcquisitionState>(reader.ReadByte(), "RecipeActivationCameraAcquisitionInvalid");
        var observed = new FrameTimePoint(new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero), reader.ReadInt64());
        CameraFault? fault = null;
        if (reader.ReadBoolean()) fault = new CameraFault(
            ReadEnum<CameraFaultClassification>(reader.ReadByte(), "RecipeActivationCameraFaultClassificationInvalid"),
            ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraFaultReasonMissing"),
            ReadString(reader, MaximumIdentifierBytes));
        return new CameraHealthSnapshot(provider, connection, configuration, acquisition, observed, fault);
    }

    private static void WriteRequested(BinaryWriter writer, RequestedCameraConfiguration? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteConfiguration(writer, value.ProductionAcquisitionMode, value.ExposureTimeUs, value.GainDb,
            value.RegionOfInterest, value.PixelFormat, value.ValidBits, value.AcquisitionTimeoutMs,
            value.TriggerDelayUs, value.WhiteBalanceRgb);
    }

    private static RequestedCameraConfiguration? ReadRequested(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new RequestedCameraConfiguration(ReadEnum<ProductionAcquisitionMode>(reader.ReadByte(), "RecipeActivationCameraModeInvalid"),
            reader.ReadDouble(), reader.ReadDouble(), ReadRoi(reader),
            ReadEnum<VisionPixelFormat>(reader.ReadByte(), "RecipeActivationCameraPixelFormatInvalid"),
            ReadNullableInt32(reader), reader.ReadInt32(), reader.ReadDouble(), ReadWhiteBalance(reader));
    }

    private static void WriteEffective(BinaryWriter writer, EffectiveCameraConfiguration? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteConfiguration(writer, value.ProductionAcquisitionMode, value.ExposureTimeUs, value.GainDb,
            value.RegionOfInterest, value.PixelFormat, value.ValidBits, value.AcquisitionTimeoutMs,
            value.TriggerDelayUs, value.WhiteBalanceRgb);
    }

    private static EffectiveCameraConfiguration? ReadEffective(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new EffectiveCameraConfiguration(ReadEnum<ProductionAcquisitionMode>(reader.ReadByte(), "RecipeActivationCameraModeInvalid"),
            reader.ReadDouble(), reader.ReadDouble(), ReadRoi(reader),
            ReadEnum<VisionPixelFormat>(reader.ReadByte(), "RecipeActivationCameraPixelFormatInvalid"),
            ReadNullableInt32(reader), reader.ReadInt32(), reader.ReadDouble(), ReadWhiteBalance(reader));
    }

    private static void WriteConfiguration(BinaryWriter writer, ProductionAcquisitionMode mode, double exposure,
        double gain, RegionOfInterest roi, VisionPixelFormat pixelFormat, int? validBits, int timeout,
        double triggerDelay, WhiteBalanceRgb? whiteBalance)
    {
        writer.Write((byte)mode); writer.Write(exposure); writer.Write(gain); WriteRoi(writer, roi);
        writer.Write((byte)pixelFormat); WriteNullableInt32(writer, validBits); writer.Write(timeout);
        writer.Write(triggerDelay); WriteWhiteBalance(writer, whiteBalance);
    }

    private static void WriteRoi(BinaryWriter writer, RegionOfInterest value)
    { writer.Write(value.OffsetX); writer.Write(value.OffsetY); writer.Write(value.Width); writer.Write(value.Height); }
    private static RegionOfInterest ReadRoi(BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

    private static void WriteWhiteBalance(BinaryWriter writer, WhiteBalanceRgb? value)
    { writer.Write(value is not null); if (value is not null) { writer.Write(value.Red); writer.Write(value.Green); writer.Write(value.Blue); } }
    private static WhiteBalanceRgb? ReadWhiteBalance(BinaryReader reader) => reader.ReadBoolean()
        ? new WhiteBalanceRgb(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()) : null;

    private static void WriteExtension(BinaryWriter writer, CameraProviderExtensionRequirement? value)
    {
        writer.Write(value is not null); if (value is null) return;
        WriteProvider(writer, value.Provider); WriteString(writer, value.ContractId, MaximumIdentifierBytes);
        WriteString(writer, value.ContractVersion, MaximumIdentifierBytes); WriteString(writer, value.ConfigurationContentHash, 64);
    }
    private static CameraProviderExtensionRequirement? ReadExtension(BinaryReader reader) => !reader.ReadBoolean() ? null :
        new CameraProviderExtensionRequirement(ReadProvider(reader),
            ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraExtensionIdMissing"),
            ReadString(reader, MaximumIdentifierBytes) ?? throw new InvalidOperationException("RecipeActivationCameraExtensionVersionMissing"),
            ReadString(reader, 64) ?? throw new InvalidOperationException("RecipeActivationCameraExtensionHashMissing"));

    private static void WriteCapabilities(BinaryWriter writer, CameraCapabilities? value)
    {
        writer.Write(value is not null); if (value is null) return;
        WriteEnums(writer, value.AcquisitionModes); WriteEnums(writer, value.PixelFormats); WriteInts(writer, value.Mono16ValidBits);
        WriteDoubleCapability(writer, value.ExposureTimeUs); WriteDoubleCapability(writer, value.GainDb);
        WriteDoubleCapability(writer, value.TriggerDelayUs); WriteRoiCapabilities(writer, value.RegionOfInterest);
        writer.Write(value.WhiteBalanceRgb is not null);
        if (value.WhiteBalanceRgb is not null)
        { WriteDoubleCapability(writer, value.WhiteBalanceRgb.Red); WriteDoubleCapability(writer, value.WhiteBalanceRgb.Green); WriteDoubleCapability(writer, value.WhiteBalanceRgb.Blue); }
    }

    private static CameraCapabilities? ReadCapabilities(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        var modes = ReadEnums<ProductionAcquisitionMode>(reader, 2);
        var formats = ReadEnums<VisionPixelFormat>(reader, 3);
        var validBits = ReadInts(reader, 3);
        var exposure = ReadDoubleCapability(reader); var gain = ReadDoubleCapability(reader);
        var trigger = ReadDoubleCapability(reader); var roi = ReadRoiCapabilities(reader);
        CameraWhiteBalanceCapabilities? white = null;
        if (reader.ReadBoolean()) white = new CameraWhiteBalanceCapabilities(ReadDoubleCapability(reader), ReadDoubleCapability(reader), ReadDoubleCapability(reader));
        return new CameraCapabilities(modes, formats, validBits, exposure, gain, trigger, roi, white);
    }

    private static void WriteEnums<T>(BinaryWriter writer, IReadOnlyList<T> values) where T : struct, Enum
    { WriteCount(writer, values.Count, 8); foreach (var value in values) writer.Write(Convert.ToByte(value, CultureInfo.InvariantCulture)); }
    private static T[] ReadEnums<T>(BinaryReader reader, int maximum) where T : struct, Enum
    { var count = ReadCount(reader, maximum); var values = new T[count]; for (var i = 0; i < count; i++) values[i] = ReadEnum<T>(reader.ReadByte(), "RecipeActivationCameraEnumInvalid"); return values; }
    private static void WriteInts(BinaryWriter writer, IReadOnlyList<int> values) { WriteCount(writer, values.Count, 8); foreach (var value in values) writer.Write(value); }
    private static int[] ReadInts(BinaryReader reader, int maximum) { var count = ReadCount(reader, maximum); var values = new int[count]; for (var i = 0; i < count; i++) values[i] = reader.ReadInt32(); return values; }
    private static void WriteDoubleCapability(BinaryWriter writer, CameraDoubleCapability value)
    { writer.Write(value.Minimum); writer.Write(value.Maximum); writer.Write(value.Increment); writer.Write((byte)value.QuantizationMode); writer.Write(value.QuantizationTolerance); writer.Write(value.Readable); writer.Write(value.Writable); }
    private static CameraDoubleCapability ReadDoubleCapability(BinaryReader reader) => new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), ReadEnum<CameraQuantizationMode>(reader.ReadByte(), "RecipeActivationCameraQuantizationInvalid"), reader.ReadDouble(), reader.ReadBoolean(), reader.ReadBoolean());
    private static void WriteIntCapability(BinaryWriter writer, CameraIntCapability value)
    { writer.Write(value.Minimum); writer.Write(value.Maximum); writer.Write(value.Increment); writer.Write(value.Readable); writer.Write(value.Writable); }
    private static CameraIntCapability ReadIntCapability(BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBoolean());
    private static void WriteRoiCapabilities(BinaryWriter writer, CameraRoiCapabilities value)
    { writer.Write(value.SensorWidth); writer.Write(value.SensorHeight); WriteIntCapability(writer, value.OffsetX); WriteIntCapability(writer, value.OffsetY); WriteIntCapability(writer, value.Width); WriteIntCapability(writer, value.Height); }
    private static CameraRoiCapabilities ReadRoiCapabilities(BinaryReader reader) => new(reader.ReadInt32(), reader.ReadInt32(), ReadIntCapability(reader), ReadIntCapability(reader), ReadIntCapability(reader), ReadIntCapability(reader));

    private static void WriteGuid(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());
    private static Guid ReadGuid(BinaryReader reader) { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new InvalidOperationException("RecipeActivationGuidInvalid"); var value = new Guid(bytes); if (value == Guid.Empty) throw new InvalidOperationException("RecipeActivationGuidInvalid"); return value; }
    private static void WriteNullableGuid(BinaryWriter writer, Guid? value) { writer.Write(value.HasValue); if (value.HasValue) WriteGuid(writer, value.Value); }
    private static Guid? ReadNullableGuid(BinaryReader reader) => reader.ReadBoolean() ? ReadGuid(reader) : null;
    private static void WriteNullableInt64(BinaryWriter writer, long? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static long? ReadNullableInt64(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt64() : null;
    private static void WriteNullableInt32(BinaryWriter writer, int? value) { writer.Write(value.HasValue); if (value.HasValue) writer.Write(value.Value); }
    private static int? ReadNullableInt32(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadInt32() : null;
    private static Guid ParseGuid(string? value, string reason) => Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty ? result : throw new InvalidOperationException(reason);

    private static void WriteVerificationReference(BinaryWriter writer,
        PhysicalCalibrationVerificationReference? value)
    {
        writer.Write(value is not null);
        if (value is null) return;
        WriteGuid(writer, value.VerificationId);
        WriteString(writer, value.ContentHash, 64);
    }

    private static PhysicalCalibrationVerificationReference? ReadVerificationReference(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new PhysicalCalibrationVerificationReference(ReadGuid(reader),
            ReadString(reader, 64) ?? throw new InvalidOperationException(
                "RecipeActivationCalibrationVerificationHashMissing"));
    }

    private static void WriteNullableDateTime(BinaryWriter writer, DateTimeOffset? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value.Value.UtcTicks);
    }

    private static DateTimeOffset? ReadNullableDateTime(BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        try { return new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException exception)
        { throw new InvalidOperationException("RecipeActivationCalibrationTimestampInvalid", exception); }
    }

    private static void WriteBytes(BinaryWriter writer, byte[] value, int maximum)
    { if (value.Length < 1 || value.Length > maximum) throw new InvalidOperationException("RecipeActivationPayloadCapacityExceeded"); writer.Write(value.Length); writer.Write(value); }
    private static byte[] ReadBytes(BinaryReader reader, int maximum)
    { var length = reader.ReadInt32(); if (length < 1 || length > maximum) throw new InvalidOperationException("RecipeActivationPayloadCapacityExceeded"); var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException(); return bytes; }
    private static void WriteCount(BinaryWriter writer, int value, int maximum) { if (value < 0 || value > maximum) throw new InvalidOperationException("RecipeActivationCollectionCapacityExceeded"); writer.Write(value); }
    private static int ReadCount(BinaryReader reader, int maximum) { var value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new InvalidOperationException("RecipeActivationCollectionCapacityExceeded"); return value; }

    private static void WriteString(BinaryWriter writer, string? value, int maximumBytes)
    {
        if (value is null) { writer.Write(-1); return; }
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        if (bytes.Length > maximumBytes) throw new InvalidOperationException("RecipeActivationStringCapacityExceeded");
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string? ReadString(BinaryReader reader, int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length == -1) return null;
        if (length < 0 || length > maximumBytes) throw new InvalidOperationException("RecipeActivationStringCapacityExceeded");
        var bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    private static T ReadEnum<T>(byte value, string reason) where T : struct, Enum
    {
        var typed = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(typeof(T), typed) ? typed : throw new InvalidOperationException(reason);
    }
}
