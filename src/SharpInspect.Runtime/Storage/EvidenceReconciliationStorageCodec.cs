using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal enum EvidenceReconciliationStream : byte { Images = 1, Outbox = 2 }

/// <summary>
/// Versioned signed fact material. Page counters are deltas, while cursor positions are
/// absolute within the run's frozen stream. The store checks transitions and source facts.
/// </summary>
internal sealed record EvidenceReconciliationPayload(int Version, Guid EventId, Guid RunId,
    Guid RuntimeEpoch, EvidenceReconciliationPhase Phase, EvidenceReconciliationEventKind Kind,
    EvidenceReconciliationStream? Stream, DateTimeOffset RecordedAtUtc, string ReasonCode,
    EvidenceReconciliationSubject? Subject, string? PreviousCheckpointHash,
    long ThroughSourcePosition, long AfterSourcePosition, long ScannedItems, long VerifiedItems,
    long DeferredItems, long VerifiedBytes);

internal sealed record EvidenceReconciliationStoredRow(long Position,
    EvidenceReconciliationPayload Payload, string ContentHash, long AuditSequence, string AuditHash)
{
    internal EvidenceReconciliationRecord ToRecord() => new(Position, Payload.EventId, Payload.RunId,
        Payload.RuntimeEpoch, Payload.Phase, Payload.Kind, Payload.Subject, Payload.RecordedAtUtc,
        Payload.ReasonCode, AuditSequence, ContentHash);
}

internal static class EvidenceReconciliationStorageCodec
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12 };

    internal static byte[] Encode(EvidenceReconciliationPayload payload, int maximumBytes)
    {
        Validate(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        if (bytes.Length > maximumBytes)
            throw new InvalidOperationException("EvidenceReconciliationPayloadTooLarge");
        return bytes;
    }

    internal static EvidenceReconciliationPayload Decode(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length is < 2 || bytes.Length > maximumBytes)
            throw new InvalidOperationException("EvidenceReconciliationPayloadSizeInvalid");
        var value = JsonSerializer.Deserialize<EvidenceReconciliationPayload>(bytes, Json) ??
            throw new InvalidOperationException("EvidenceReconciliationPayloadMissing");
        var canonical = Encode(value, maximumBytes);
        // Reject duplicate/unknown fields, alternative number/time encodings and trailing
        // data instead of letting a permissive JSON read silently redefine signed material.
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new InvalidOperationException("EvidenceReconciliationPayloadNotCanonical");
        return value;
    }

    internal static string ContentHash(long position, string configurationHash, byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("EvidenceReconciliationRecordV1",
            position.ToString(System.Globalization.CultureInfo.InvariantCulture), configurationHash,
            Convert.ToBase64String(payload))));

    internal static byte[] AuditBinding(long position, string configurationHash, byte[] payload) =>
        AuditCanonical.Encode("EvidenceReconciliationAuditV1", SqliteCommandStore.SystemPrincipal,
            position.ToString(System.Globalization.CultureInfo.InvariantCulture), configurationHash,
            ContentHash(position, configurationHash, payload), Convert.ToBase64String(payload));

    internal static void Validate(EvidenceReconciliationPayload value)
    {
        Require(value.Version == 1 && value.EventId != Guid.Empty && value.RunId != Guid.Empty &&
            value.RuntimeEpoch != Guid.Empty && Enum.IsDefined(typeof(EvidenceReconciliationPhase), value.Phase) &&
            Enum.IsDefined(typeof(EvidenceReconciliationEventKind), value.Kind), "IdentityInvalid");
        Require(value.RecordedAtUtc != default && value.RecordedAtUtc.Offset == TimeSpan.Zero,
            "TimestampInvalid");
        Require(value.ReasonCode is { Length: > 0 and <= 128 } &&
            value.ReasonCode.All(c => c <= 0x7f && (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')),
            "ReasonInvalid");
        Require(value.ThroughSourcePosition >= 0 && value.AfterSourcePosition >= 0 &&
            value.AfterSourcePosition <= value.ThroughSourcePosition && value.ScannedItems >= 0 &&
            value.VerifiedItems >= 0 && value.DeferredItems >= 0 && value.VerifiedBytes >= 0 &&
            value.VerifiedItems <= value.ScannedItems && value.DeferredItems <= value.ScannedItems - value.VerifiedItems,
            "ProgressInvalid");
        if (value.Phase == EvidenceReconciliationPhase.HistoricalScrub)
            Require(value.Stream is { } stream && Enum.IsDefined(typeof(EvidenceReconciliationStream), stream),
                "StreamRequired");
        else Require(value.Stream is null && value.ThroughSourcePosition == 0 && value.AfterSourcePosition == 0,
            "StartupCursorInvalid");
        if (value.PreviousCheckpointHash is { } checkpoint) Require(IsHash(checkpoint), "CheckpointHashInvalid");

        var progress = value.Kind is EvidenceReconciliationEventKind.RunStarted or
            EvidenceReconciliationEventKind.PageCompleted or EvidenceReconciliationEventKind.RunCompleted;
        Require(progress == (value.Subject is null), "SubjectKindMismatch");
        if (value.Kind == EvidenceReconciliationEventKind.RunStarted)
            Require(value.PreviousCheckpointHash is null && value.AfterSourcePosition == 0 &&
                value.ScannedItems == 0 && value.VerifiedItems == 0 && value.DeferredItems == 0 &&
                value.VerifiedBytes == 0, "RunStartInvalid");
        if (!progress)
            Require(value.PreviousCheckpointHash is null && value.ScannedItems == 0 && value.VerifiedItems == 0 &&
                value.DeferredItems == 0 && value.VerifiedBytes == 0, "ObservationProgressInvalid");
        if (value.Subject is { } subject) ValidateSubject(subject, value.Kind);
    }

    private static void ValidateSubject(EvidenceReconciliationSubject value, EvidenceReconciliationEventKind kind)
    {
        Require(Enum.IsDefined(typeof(EvidenceReconciliationSubjectKind), value.Kind), "SubjectInvalid");
        foreach (var hash in new[] { value.ReferenceContentHash, value.ObservedLifecycleHash,
                     value.SourceRootBindingHash, value.QuarantineRootBindingHash, value.FileIdentityHash,
                     value.ObservedContentHash })
            if (hash is not null) Require(IsHash(hash), "SubjectHashInvalid");
        Require((value.SourcePosition is null or > 0) && (value.ByteLength is null or >= 0),
            "SubjectSizeInvalid");
        if (value.FileArea is { } area)
            Require(Enum.IsDefined(typeof(EvidenceReconciliationFileArea), area), "FileAreaInvalid");
        if (value.SourceFileName is { } sourceName) Require(IsFileName(sourceName), "FileNameInvalid");
        if (value.QuarantineFileName is { } quarantineName) Require(IsFileName(quarantineName), "QuarantineNameInvalid");
        if (value.Kind == EvidenceReconciliationSubjectKind.Orphan)
        {
            Require(value.OrphanId is { } orphan && orphan != Guid.Empty && value.InspectionId is null &&
                value.ManifestId is null && value.WorkId is null && value.DeliveryId is null &&
                value.SourcePosition is null && value.ReferenceContentHash is null && value.ObservedLifecycleHash is null &&
                value.FileArea is not null && value.SourceFileName is not null && value.SourceRootBindingHash is not null,
                "OrphanProductionReferenceForbidden");
            Require(kind is EvidenceReconciliationEventKind.QuarantineIntent or
                EvidenceReconciliationEventKind.Quarantined or EvidenceReconciliationEventKind.IntegrityFault,
                "OrphanEventInvalid");
            if (kind != EvidenceReconciliationEventKind.IntegrityFault)
                Require(value.QuarantineRootBindingHash is not null && value.QuarantineFileName is not null &&
                    value.FileIdentityHash is not null && value.ByteLength is not null && value.ObservedContentHash is not null,
                    "QuarantineObservationIncomplete");
            return;
        }
        Require(value.OrphanId is null && value.QuarantineFileName is null && value.QuarantineRootBindingHash is null &&
            value.SourcePosition is > 0 && value.ReferenceContentHash is not null && value.ObservedLifecycleHash is not null &&
            value.InspectionId is { } inspection && inspection != Guid.Empty, "ProductionSubjectBindingInvalid");
        if (value.Kind == EvidenceReconciliationSubjectKind.Image)
        {
            Require(value.ManifestId is { } manifest && manifest != Guid.Empty && value.WorkId is { } work &&
                work != Guid.Empty && value.DeliveryId is null, "ImageSubjectInvalid");
            Require(kind is EvidenceReconciliationEventKind.ImageVerified or EvidenceReconciliationEventKind.ImageFinalRecovered or
                EvidenceReconciliationEventKind.IntegrityFault or EvidenceReconciliationEventKind.WorkDeferred, "ImageEventInvalid");
        }
        else
        {
            Require(value.DeliveryId is { } delivery && delivery != Guid.Empty && value.ManifestId is null &&
                value.WorkId is null && value.FileArea is null && value.SourceFileName is null &&
                value.SourceRootBindingHash is null && value.FileIdentityHash is null, "OutboxSubjectInvalid");
            Require(kind is EvidenceReconciliationEventKind.OutboxVerified or EvidenceReconciliationEventKind.IntegrityFault or
                EvidenceReconciliationEventKind.WorkDeferred, "OutboxEventInvalid");
        }
    }

    internal static bool IsHash(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool IsFileName(string value) => value.Length is > 0 and <= 255 && value is not "." and not ".." &&
        value.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0 &&
        value.All(c => !char.IsControl(c)) && !value.EndsWith(' ') && !value.EndsWith('.');
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("EvidenceReconciliation" + reason);
    }
}
