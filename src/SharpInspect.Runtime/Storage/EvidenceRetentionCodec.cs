using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceRetentionPayload(int Version, Guid EventId, Guid OperationId,
    Guid RuntimeEpoch, EvidenceRetentionEventKind Kind, EvidenceRetentionOwner Owner,
    long AggregateSequence, string? PreviousContentHash, string ObligationHash, DateTimeOffset RecordedAtUtc,
    string ReasonCode, string? Reason, string SystemPrincipalId, Guid? HumanPrincipalId,
    Guid? SessionId, Guid? StepUpGrantId, string? AuthorizationTarget, long? AuthorizationRevision,
    Guid? HoldId, DateTimeOffset? ExtendedUntilUtc, EvidenceDeletionFile? File,
    EvidenceRetentionObligation? EstablishedObligation, string ExecutionPolicyHash,
    RetentionAuthorityEvidence? Authority = null);

internal sealed record EvidenceRetentionStoredRow(long Position, EvidenceRetentionPayload Payload,
    string ContentHash, long AuditSequence, string AuditHash)
{
    internal EvidenceRetentionRecord ToRecord() => new(Position, Payload.EventId, Payload.OperationId,
        Payload.RuntimeEpoch, Payload.Kind, Payload.Owner, Payload.AggregateSequence,
        Payload.PreviousContentHash, Payload.ObligationHash, Payload.RecordedAtUtc, Payload.ReasonCode,
        Payload.SystemPrincipalId, Payload.HumanPrincipalId, Payload.HoldId, Payload.ExtendedUntilUtc,
        Payload.File, AuditSequence, ContentHash, Payload.Reason, Payload.SessionId, Payload.StepUpGrantId);
}

internal static class EvidenceRetentionCodec
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12 };
    internal static void Require(bool valid, string reason)
    { if (!valid) throw new InvalidOperationException("Retention" + reason); }
    internal static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    internal static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    internal static string ObligationHash(EvidenceRetentionObligation value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value with { ContentHash = string.Empty }, Json)));

    internal static void ValidateObligation(EvidenceRetentionObligation value)
    {
        ValidateOwner(value.Owner);
        Require(Enum.IsDefined(value.EvidenceClass) && Enum.IsDefined(value.StartsAt) &&
            value.SourceAuditSequence > 0 && value.ByteLength >= 0 &&
            Utc(value.StartedAtUtc) && Utc(value.RetainUntilUtc) && value.RetainUntilUtc > value.StartedAtUtc,
            "ObligationInvalid");
        Require(new[] { value.SourceContentHash, value.ArtifactContentHash, value.PolicySnapshotHash,
            value.PublicationHash, value.RuleHash, value.RootBindingHash, value.ContentHash }.All(Hash),
            "ObligationHashInvalid");
        Require(value.ContentHash == ObligationHash(value), "ObligationHashMismatch");
        Require(value.Owner.Kind == EvidenceRetentionOwnerKind.ImageManifest
            ? value.InspectionId is { } id && id != Guid.Empty && value.ByteLength > 0 && value.EvidenceClass == TraceRetentionClass.AuthoritativeImage
            : value.InspectionId is null && value.EvidenceClass is
                TraceRetentionClass.QuarantineEvidence or TraceRetentionClass.OrphanImageStage, "ObligationOwnerMismatch");
        ValidateName(value.FileName);
    }
    private static void ValidateOwner(EvidenceRetentionOwner value) =>
        Require(value is not null && Enum.IsDefined(value.Kind) && value.OwnerId != Guid.Empty, "OwnerInvalid");
    private static void ValidateName(string value) =>
        Require(value is { Length: > 0 and <= 255 } && value != "." && value != ".." &&
            value.TrimEnd(' ', '.') == value &&
            !value.Any(c => c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'),
            "FileNameInvalid");
    internal static void ValidateFile(EvidenceDeletionFile value)
    {
        ValidateName(value.FileName);
        Require(Hash(value.RootBindingHash) && Hash(value.FileIdentityHash) && Hash(value.RawContentHash) &&
            value.ByteLength >= 0, "FileIdentityInvalid");
    }
    internal static void Validate(EvidenceRetentionPayload value)
    {
        ValidateOwner(value.Owner);
        Require(value.Version == 1 && value.EventId != Guid.Empty && value.OperationId != Guid.Empty &&
            value.RuntimeEpoch != Guid.Empty && Enum.IsDefined(value.Kind) && value.AggregateSequence > 0 &&
            Utc(value.RecordedAtUtc) && Hash(value.ObligationHash) && Hash(value.ExecutionPolicyHash), "EventIdentityInvalid");
        Require(value.ReasonCode is { Length: > 0 and <= 128 } &&
            value.ReasonCode.All(c => c <= 127 && (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')),
            "ReasonCodeInvalid");
        Require(value.PreviousContentHash is null || Hash(value.PreviousContentHash), "PreviousHashInvalid");
        var governance = value.Kind is EvidenceRetentionEventKind.Extended or
            EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased;
        Require(value.SystemPrincipalId == Abstractions.SystemPrincipalId.RetentionCleanup, "SystemPrincipalInvalid");
        Require(governance == (value.Authority is not null), "AuthorityReferenceRequired");
        if (value.Authority is { } authority)
        {
            Require(authority.CommandEventId != Guid.Empty && authority.AuthorizationEventId != Guid.Empty &&
                authority.CommandAuditSequence > 0 && authority.AuthorizationAuditSequence > 0 &&
                Hash(authority.CommandAuditHash) && Hash(authority.AuthorizationAuditHash) &&
                Hash(authority.AuthorizationPolicyHash) &&
                authority.AuthorizationPolicyId is { Length: > 0 and <= 128 } &&
                authority.AuthorizationPolicyVersion is { Length: > 0 and <= 128 }, "AuthorityReferenceInvalid");
        }
        if (governance)
        {
            Require(value.HumanPrincipalId is { } human && human != Guid.Empty &&
                value.SessionId is { } session && session != Guid.Empty &&
                value.StepUpGrantId is { } grant && grant != Guid.Empty && Hash(value.AuthorizationTarget) &&
                value.AuthorizationRevision is >= 0 && value.Reason is { Length: > 0 and <= 512 }, "ActorRequired");
        }
        else Require(value.HumanPrincipalId is null && value.SessionId is null && value.StepUpGrantId is null &&
            value.AuthorizationTarget is null && value.AuthorizationRevision is null && value.Reason is null,
            "UnexpectedActor");
        Require((value.Kind is EvidenceRetentionEventKind.HoldPlaced or EvidenceRetentionEventKind.HoldReleased) ==
            (value.HoldId is not null) && value.HoldId != Guid.Empty, "HoldPayloadInvalid");
        Require((value.Kind == EvidenceRetentionEventKind.Extended) == (value.ExtendedUntilUtc is not null) &&
            (value.ExtendedUntilUtc is null || Utc(value.ExtendedUntilUtc.Value)), "ExtensionPayloadInvalid");
        Require((value.Kind == EvidenceRetentionEventKind.ObligationEstablished) == (value.EstablishedObligation is not null),
            "EstablishmentPayloadInvalid");
        if (value.EstablishedObligation is { } obligation)
        {
            ValidateObligation(obligation);
            Require(value.Owner == obligation.Owner && value.ObligationHash == obligation.ContentHash,
                "EstablishmentBindingMismatch");
        }
        var deleting = value.Kind is EvidenceRetentionEventKind.DeleteIntent or EvidenceRetentionEventKind.DeleteFailed or
            EvidenceRetentionEventKind.DeleteOutcomeUnknown or EvidenceRetentionEventKind.Tombstone;
        Require(deleting == (value.File is not null), "DeletionPayloadInvalid");
        if (value.File is not null) ValidateFile(value.File);
    }
    internal static byte[] Encode(EvidenceRetentionPayload value, int maximumBytes)
    {
        Validate(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Require(bytes.Length <= maximumBytes, "PayloadCapacityExceeded");
        return bytes;
    }
    internal static EvidenceRetentionPayload Decode(byte[] bytes, int maximumBytes)
    {
        Require(bytes.Length is >= 2 && bytes.Length <= maximumBytes, "PayloadSizeInvalid");
        var value = JsonSerializer.Deserialize<EvidenceRetentionPayload>(bytes, Json) ??
            throw new InvalidOperationException("RetentionPayloadMissing");
        Require(bytes.AsSpan().SequenceEqual(Encode(value, maximumBytes)), "PayloadNotCanonical");
        return value;
    }
    internal static string ContentHash(long position, string configurationHash, byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("EvidenceRetentionRecordV1",
            position.ToString(CultureInfo.InvariantCulture), configurationHash, Convert.ToBase64String(payload))));
    internal static byte[] AuditBinding(long position, string configurationHash, byte[] payload) =>
        AuditCanonical.Encode("EvidenceRetentionAuditV1", Abstractions.SystemPrincipalId.RetentionCleanup,
            position.ToString(CultureInfo.InvariantCulture), configurationHash, ContentHash(position, configurationHash, payload),
            Convert.ToBase64String(payload));
}
