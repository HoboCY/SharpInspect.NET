using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>The closed durable lifecycle of one station-maintenance operation.</summary>
internal enum StationMaintenanceFactPhase
{
    Admitted = 1,
    Prepared = 2,
    Failed = 3
}

/// <summary>
/// One durable immutable station-maintenance fact. Every field is a bounded primitive or a
/// bounded primitive projection; no public abstraction type is serialized directly.
/// </summary>
internal sealed record StationMaintenancePayload
{
    public int Version { get; init; } = 1;
    public Guid EventId { get; init; }
    public Guid OperationId { get; init; }
    public Guid CommandCorrelationId { get; init; }
    public Guid RuntimeEpoch { get; init; }
    public StationMaintenanceFactPhase Phase { get; init; }
    public int AggregateSequence { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public StationMaintenanceReason Reason { get; init; }
    public string ReasonCode { get; init; } = string.Empty;
    public Guid ActorPrincipalId { get; init; }
    public Guid SessionId { get; init; }
    public Guid StepUpGrantId { get; init; }
    public long AuthorizationRevision { get; init; }
    public string AuthorizationTarget { get; init; } = string.Empty;
    public string AuthorizationPolicyId { get; init; } = string.Empty;
    public string AuthorizationPolicyVersion { get; init; } = string.Empty;
    public string AuthorizationPolicyHash { get; init; } = string.Empty;
    public string PolicyHash { get; init; } = string.Empty;
    public string ConfigurationHash { get; init; } = string.Empty;
    public string InstallationBindingHash { get; init; } = string.Empty;
    public string RuntimeApplicationHash { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public DateTimeOffset ProcessStartedUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset AdmittedDeadlineUtc { get; init; }
    public StationMaintenanceAuditReference Command { get; init; } = new();
    public StationMaintenanceAuditReference Authorization { get; init; } = new();
    public StationMaintenanceAuditReference? Previous { get; init; }
    public string? PreviousContentHash { get; init; }
}

/// <summary>One exact signed audit reference of the authorizing command or identity event.</summary>
internal sealed record StationMaintenanceAuditReference
{
    public Guid EventId { get; init; }
    public long Sequence { get; init; }
    public string Hash { get; init; } = string.Empty;

    internal void Validate()
    {
        if (EventId == Guid.Empty || Sequence <= 0 || Hash is not { Length: 64 })
            throw new InvalidOperationException("StationMaintenanceAuditReferenceInvalid");
    }
}

internal sealed record StationMaintenanceStoredRow(long Position, StationMaintenancePayload Payload,
    byte[] PayloadBytes, string ContentHash, long AuditSequence, string AuditHash)
{
    internal StationMaintenanceOperationState ToState() => new(Payload.OperationId, Payload.Phase,
        Payload.AggregateSequence, Payload.ObservedAtUtc, Payload.ReasonCode, Payload);
}

internal sealed record StationMaintenanceOperationState(Guid OperationId, StationMaintenanceFactPhase Phase,
    int AggregateSequence, DateTimeOffset ObservedAtUtc, string ReasonCode, StationMaintenancePayload Payload);

internal sealed record StationMaintenanceConfiguration(string OptionsHash, string PolicyHash,
    string InstallationBindingHash, string RuntimeApplicationHash, string AuditPolicyHash,
    string DiagnosticSupportConfigurationHash, int SourceSchemaVersion, long RetirementTimeoutTicks,
    long HandoffLifetimeTicks, long HostExitTimeoutTicks, int MaximumOperations, long MaximumTotalBytes)
{
    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);
    internal string BindingHash => Convert.ToHexString(SHA256.HashData(Encode()));
}

/// <summary>
/// The bounded signed handoff artifact produced by one successful seal. The document is
/// self-describing; the signature over its canonical unsigned bytes is the only proof of
/// origin. The embedded public key is never a trust anchor: a verifier compares it against a
/// separately provisioned key.
/// </summary>
internal sealed record StationMaintenancePreparedHandoff
{
    internal const int Version = 1;
    internal const int MaximumArtifactBytes = 64 * 1024;
    internal const int SignatureBytes = 64;
    internal const string EncodeLabel = "station-maintenance-prepared-handoff-v1";

    public string FormatVersion { get; init; } = Version.ToString(CultureInfo.InvariantCulture);
    public Guid OperationId { get; init; }
    public Guid EventId { get; init; }
    public string ConfigurationHash { get; init; } = string.Empty;
    public string OptionsHash { get; init; } = string.Empty;
    public string PolicyHash { get; init; } = string.Empty;
    public string InstallationBindingHash { get; init; } = string.Empty;
    public string RuntimeApplicationHash { get; init; } = string.Empty;
    public string StationId { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string PublicKeyBase64 { get; init; } = string.Empty;
    public long PreparedPosition { get; init; }
    public string PreparedContentHash { get; init; } = string.Empty;
    public long PreparedAuditSequence { get; init; }
    public string PreparedAuditHash { get; init; } = string.Empty;
    public long AdmittedAuditSequence { get; init; }
    public string AdmittedAuditHash { get; init; } = string.Empty;
    public string AdmittedContentHash { get; init; } = string.Empty;
    public Guid CommandEventId { get; init; }
    public long CommandSequence { get; init; }
    public string CommandHash { get; init; } = string.Empty;
    public Guid AuthorizationEventId { get; init; }
    public long AuthorizationSequence { get; init; }
    public string AuthorizationHash { get; init; } = string.Empty;
    public Guid RuntimeEpoch { get; init; }
    public int ProcessId { get; init; }
    public string ProcessStartedUtc { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string ExpiresAtUtc { get; init; } = string.Empty;
    public string PreparedAtUtc { get; init; } = string.Empty;
    public string SignatureBase64 { get; init; } = string.Empty;

    internal byte[] EncodeUnsigned() => Encode(this with { SignatureBase64 = string.Empty });

    internal static byte[] Encode(StationMaintenancePreparedHandoff value) => AuditCanonical.Encode(EncodeLabel,
        value.FormatVersion, value.OperationId.ToString("D"), value.EventId.ToString("D"),
        value.ConfigurationHash, value.OptionsHash, value.PolicyHash, value.InstallationBindingHash,
        value.RuntimeApplicationHash, value.StationId, value.KeyId, value.PublicKeyBase64,
        value.PreparedPosition.ToString(CultureInfo.InvariantCulture), value.PreparedContentHash,
        value.PreparedAuditSequence.ToString(CultureInfo.InvariantCulture), value.PreparedAuditHash,
        value.AdmittedAuditSequence.ToString(CultureInfo.InvariantCulture), value.AdmittedAuditHash,
        value.AdmittedContentHash, value.CommandEventId.ToString("D"),
        value.CommandSequence.ToString(CultureInfo.InvariantCulture), value.CommandHash,
        value.AuthorizationEventId.ToString("D"), value.AuthorizationSequence.ToString(CultureInfo.InvariantCulture),
        value.AuthorizationHash, value.RuntimeEpoch.ToString("D"),
        value.ProcessId.ToString(CultureInfo.InvariantCulture), value.ProcessStartedUtc, value.Reason,
        value.ExpiresAtUtc, value.PreparedAtUtc, value.SignatureBase64);
}

internal static class StationMaintenanceStorageCodec
{
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12 };
    private static readonly JsonSerializerOptions ConfigurationJson = new() { MaxDepth = 12 };

    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException("StationMaintenance" + reason);
    }

    internal static bool Hash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    internal static bool SafeReason(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => c <= 127 && (char.IsLetterOrDigit(c) || c is '.' or '_' or '-'));

    internal static void ValidateConfiguration(StationMaintenanceConfiguration value)
    {
        Require(value is not null && Hash(value.OptionsHash) && Hash(value.PolicyHash) &&
            Hash(value.InstallationBindingHash) && Hash(value.RuntimeApplicationHash) &&
            Hash(value.AuditPolicyHash) && Hash(value.DiagnosticSupportConfigurationHash) &&
            value.SourceSchemaVersion == DiagnosticSupportStoreOptions.SchemaVersion &&
            value.RetirementTimeoutTicks >= TimeSpan.FromMilliseconds(100).Ticks &&
            value.RetirementTimeoutTicks <= TimeSpan.FromMinutes(5).Ticks &&
            value.HandoffLifetimeTicks >= TimeSpan.FromSeconds(1).Ticks &&
            value.HandoffLifetimeTicks <= TimeSpan.FromMinutes(15).Ticks &&
            value.HandoffLifetimeTicks > value.RetirementTimeoutTicks &&
            value.HostExitTimeoutTicks >= TimeSpan.FromMilliseconds(100).Ticks &&
            value.HostExitTimeoutTicks <= TimeSpan.FromMinutes(1).Ticks &&
            value.HostExitTimeoutTicks < value.HandoffLifetimeTicks &&
            value.MaximumOperations is >= StationMaintenanceStoreOptions
                .MaximumOperationFactsHardLimit and <= StationMaintenanceStoreOptions.MaximumOperationsHardLimit &&
            value.MaximumTotalBytes >= StationMaintenanceStoreOptions.MaximumPayloadBytesHardLimit * 2 &&
            value.MaximumTotalBytes <= StationMaintenanceStoreOptions.MaximumTotalBytesHardLimit,
            "ConfigurationInvalid");
    }

    internal static byte[] EncodeConfiguration(StationMaintenanceConfiguration value)
    {
        ValidateConfiguration(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ConfigurationJson);
        Require(bytes.Length is >= 2 and <= 65536, "ConfigurationCapacityExceeded");
        return bytes;
    }

    internal static StationMaintenanceConfiguration DecodeConfiguration(byte[] bytes)
    {
        Require(bytes.Length is >= 2 and <= 65536, "ConfigurationSizeInvalid");
        StationMaintenanceConfiguration? value;
        try { value = JsonSerializer.Deserialize<StationMaintenanceConfiguration>(bytes, ConfigurationJson); }
        catch (JsonException) { throw new InvalidOperationException("StationMaintenanceConfigurationInvalid"); }
        Require(value is not null, "ConfigurationMissing");
        ValidateConfiguration(value!);
        Require(bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value!, ConfigurationJson)),
            "ConfigurationNoncanonical");
        return value!;
    }

    internal static void ValidatePayload(StationMaintenancePayload value, StationMaintenanceConfiguration configuration)
    {
        Require(value is not null && value.Version == 1 && value.EventId != Guid.Empty &&
            value.OperationId != Guid.Empty && value.CommandCorrelationId != Guid.Empty &&
            value.RuntimeEpoch != Guid.Empty && Enum.IsDefined(value.Phase) &&
            value.AggregateSequence == (int)value.Phase && Enum.IsDefined(value.Reason) &&
            Utc(value.ObservedAtUtc) && SafeReason(value.ReasonCode) &&
            value.ActorPrincipalId != Guid.Empty && value.SessionId != Guid.Empty && value.StepUpGrantId != Guid.Empty &&
            value.AuthorizationRevision >= 0 && Hash(value.AuthorizationTarget) &&
            value.AuthorizationPolicyId is { Length: > 0 and <= 128 } &&
            value.AuthorizationPolicyVersion is { Length: > 0 and <= 128 } &&
            Hash(value.AuthorizationPolicyHash) && Hash(value.PolicyHash) && Hash(value.ConfigurationHash) &&
            value.PolicyHash == configuration.PolicyHash && value.ConfigurationHash == configuration.OptionsHash &&
            value.InstallationBindingHash == configuration.InstallationBindingHash &&
            value.RuntimeApplicationHash == configuration.RuntimeApplicationHash &&
            value.ProcessId > 0 && Utc(value.ProcessStartedUtc) && Utc(value.ExpiresAtUtc) &&
            Utc(value.AdmittedDeadlineUtc) && value.ExpiresAtUtc > value.AdmittedDeadlineUtc &&
            value.Command is not null && value.Authorization is not null, "PayloadIdentityInvalid");
        value.Command.Validate();
        value.Authorization.Validate();
        Require(value.Phase switch
        {
            StationMaintenanceFactPhase.Admitted => value.Previous is null && value.PreviousContentHash is null,
            StationMaintenanceFactPhase.Prepared or StationMaintenanceFactPhase.Failed =>
                value.Previous is not null && value.Previous.EventId != Guid.Empty &&
                Hash(value.PreviousContentHash) && value.Previous.Sequence > 0 && Hash(value.Previous.Hash),
            _ => false
        }, "PayloadPhaseInvalid");
        value.Previous?.Validate();
    }

    internal static byte[] Encode(StationMaintenancePayload value, StationMaintenanceConfiguration configuration)
    {
        ValidatePayload(value, configuration);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        Require(bytes.Length <= StationMaintenanceStoreOptions.MaximumPayloadBytesHardLimit,
            "PayloadCapacityExceeded");
        return bytes;
    }

    internal static StationMaintenancePayload Decode(byte[] bytes, StationMaintenanceConfiguration configuration)
    {
        Require(bytes.Length is >= 2 and <= StationMaintenanceStoreOptions.MaximumPayloadBytesHardLimit,
            "PayloadSizeInvalid");
        StationMaintenancePayload? value;
        try { value = JsonSerializer.Deserialize<StationMaintenancePayload>(bytes, Json); }
        catch (JsonException) { throw new InvalidOperationException("StationMaintenancePayloadInvalid"); }
        Require(value is not null && bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value!, Json)),
            "PayloadNoncanonical");
        ValidatePayload(value!, configuration);
        return value!;
    }

    internal static string ContentHash(long position, string configurationHash, byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("StationMaintenanceOperationRecordV1",
            position.ToString(CultureInfo.InvariantCulture), configurationHash, Convert.ToBase64String(payload))));

    internal static byte[] AuditBinding(long position, string configurationHash, byte[] payload) =>
        AuditCanonical.Encode("StationMaintenanceAuditV1", SystemPrincipalId.Runtime,
            position.ToString(CultureInfo.InvariantCulture), configurationHash,
            ContentHash(position, configurationHash, payload), Convert.ToBase64String(payload));

    /// <summary>Signs the canonical unsigned handoff bytes with the existing station audit key.</summary>
    internal static byte[] CreateHandoffArtifact(StationMaintenancePreparedHandoff value, IAuditSigningKey key)
    {
        var signature = key.Sign(value.EncodeUnsigned());
        Require(signature is { Length: StationMaintenancePreparedHandoff.SignatureBytes },
            "HandoffSignatureInvalid");
        var artifact = StationMaintenancePreparedHandoff.Encode(value with
        {
            SignatureBase64 = Convert.ToBase64String(signature)
        });
        Require(artifact.Length is > 0 and <= StationMaintenancePreparedHandoff.MaximumArtifactBytes,
            "HandoffCapacityExceeded");
        return artifact;
    }

    internal static bool IsHash(string? value) => Hash(value);
}
