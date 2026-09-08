using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Cameras;

/// <summary>Phases in the signed schema 12 network ledger.</summary>
internal enum CameraNetworkEventPhase
{
    Admission,
    Terminal,
    Rejected
}

/// <summary>
/// One immutable network maintenance record.  Position is assigned by the
/// single writer; every other field is part of the signed canonical payload.
/// </summary>
internal sealed record CameraNetworkEvent(
    long Position,
    Guid EventId,
    Guid OperationId,
    Guid AttemptId,
    Guid CorrelationId,
    Guid RuntimeEpoch,
    CameraNetworkEventPhase Phase,
    CameraBindingTarget Target,
    CameraStationNetwork? StationNetwork,
    CameraIpv4Configuration? Previous,
    CameraIpv4Configuration Requested,
    CameraIpv4Configuration? Observed,
    CameraNetworkMaintenanceState State,
    bool IdentityVerified,
    string ReasonCode,
    string ChangeReason,
    Guid? ActorPrincipalId,
    Guid? SessionId,
    long AuthorizationRevision,
    DateTimeOffset RecordedAtUtc);

/// <summary>Canonical bounded bytes for the network event stream.</summary>
internal static class CameraNetworkStorageCodec
{
    private const int FieldCount = 34;
    private const int MaximumTextLength = 1024;
    internal const int MaximumPayloadBytes = CameraNetworkStoreOptions.MaximumPayloadBytesHardLimit;
    internal const int MaximumEncodedPayloadChars = MaximumPayloadBytes * 2;

    internal static byte[] Encode(CameraNetworkEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        var station = value.StationNetwork;
        return AuditCanonical.Encode("CameraNetworkEvent",
            value.Position.ToString(CultureInfo.InvariantCulture), value.EventId.ToString("D"),
            value.OperationId.ToString("D"), value.AttemptId.ToString("D"),
            value.CorrelationId.ToString("D"), value.RuntimeEpoch.ToString("D"),
            value.Phase.ToString(), value.Target.Provider.Id,
            value.Target.Provider.Version, value.Target.Provider.AdapterPackageId,
            value.Target.Provider.AdapterVersion, value.Target.StableDeviceIdentity,
            value.Target.ContentHash, station?.InterfaceId, station?.Configuration.Address,
            station?.Configuration.PrefixLength.ToString(CultureInfo.InvariantCulture),
            station?.Configuration.Gateway, value.Previous?.Address,
            value.Previous?.PrefixLength.ToString(CultureInfo.InvariantCulture), value.Previous?.Gateway,
            value.Requested.Address, value.Requested.PrefixLength.ToString(CultureInfo.InvariantCulture),
            value.Requested.Gateway, value.Observed?.Address,
            value.Observed?.PrefixLength.ToString(CultureInfo.InvariantCulture), value.Observed?.Gateway,
            value.State.ToString(), value.IdentityVerified ? "1" : "0", value.ReasonCode,
            value.ChangeReason, value.ActorPrincipalId?.ToString("D"), value.SessionId?.ToString("D"),
            value.AuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    internal static CameraNetworkEvent Decode(byte[] payload, long expectedPosition = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            var fields = DecodeFields(payload);
            if (fields.Length != FieldCount || fields[1] is null || fields[2] is null)
                throw new InvalidOperationException("CameraNetworkPayloadInvalid");

            var position = ParseLong(fields[0], "CameraNetworkPositionInvalid");
            if (expectedPosition != 0 && position != expectedPosition)
                throw new InvalidOperationException("CameraNetworkPositionMismatch");
            var eventId = ParseGuid(fields[1], "CameraNetworkEventInvalid");
            var operationId = ParseGuid(fields[2], "CameraNetworkOperationInvalid");
            var attemptId = ParseGuid(fields[3], "CameraNetworkAttemptInvalid");
            var correlationId = ParseGuid(fields[4], "CameraNetworkCorrelationInvalid");
            var runtimeEpoch = ParseGuid(fields[5], "CameraNetworkEpochInvalid");
            var phase = ParseEnum<CameraNetworkEventPhase>(fields[6], "CameraNetworkPhaseInvalid");
            var provider = new CameraProviderIdentity(Required(fields[7], "CameraNetworkProviderInvalid"),
                Required(fields[8], "CameraNetworkProviderInvalid"),
                Required(fields[9], "CameraNetworkProviderInvalid"),
                Required(fields[10], "CameraNetworkProviderInvalid"));
            var target = new CameraBindingTarget(provider,
                Required(fields[11], "CameraNetworkTargetInvalid"));
            if (!string.Equals(target.ContentHash, fields[12], StringComparison.Ordinal))
                throw new InvalidOperationException("CameraNetworkTargetBindingMismatch");

            var station = ParseStation(fields[13], fields[14], fields[15], fields[16]);
            var previous = ParseConfiguration(fields[17], fields[18], fields[19], "CameraNetworkPreviousInvalid");
            var requested = ParseConfiguration(fields[20], fields[21], fields[22], "CameraNetworkRequestedInvalid")
                ?? throw new InvalidOperationException("CameraNetworkRequestedInvalid");
            var observed = ParseConfiguration(fields[23], fields[24], fields[25], "CameraNetworkObservedInvalid");
            var state = ParseEnum<CameraNetworkMaintenanceState>(fields[26], "CameraNetworkStateInvalid");
            if (fields[27] is not ("0" or "1"))
                throw new InvalidOperationException("CameraNetworkIdentityVerificationInvalid");
            var identityVerified = fields[27] == "1";
            var reason = Required(fields[28], "CameraNetworkReasonInvalid");
            var changeReason = Required(fields[29], "CameraNetworkChangeReasonInvalid");
            var actor = ParseNullableGuid(fields[30], "CameraNetworkActorInvalid");
            var session = ParseNullableGuid(fields[31], "CameraNetworkSessionInvalid");
            var authorizationRevision = ParseLong(fields[32], "CameraNetworkAuthorizationRevisionInvalid");
            if (!DateTimeOffset.TryParseExact(fields[33], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var recorded) || recorded == default)
                throw new InvalidOperationException("CameraNetworkRecordedAtInvalid");
            var value = new CameraNetworkEvent(position, eventId, operationId, attemptId, correlationId,
                runtimeEpoch, phase, target, station, previous, requested, observed, state,
                identityVerified, reason, changeReason, actor, session, authorizationRevision, recorded);
            Validate(value);
            return value;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or FormatException or
            OverflowException or EndOfStreamException or DecoderFallbackException)
        { throw new InvalidOperationException("CameraNetworkPayloadInvalid", exception); }
    }

    internal static void Validate(CameraNetworkEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Require(value.Position >= 0, "CameraNetworkPositionInvalid");
        Require(value.EventId != Guid.Empty && value.OperationId != Guid.Empty &&
            value.AttemptId != Guid.Empty && value.CorrelationId != Guid.Empty &&
            value.RuntimeEpoch != Guid.Empty, "CameraNetworkIdentityInvalid");
        Require(Enum.IsDefined(value.Phase), "CameraNetworkPhaseInvalid");
        ArgumentNullException.ThrowIfNull(value.Target);
        ArgumentNullException.ThrowIfNull(value.Target.Provider);
        Require(value.Target.ContentHash == new CameraBindingTarget(value.Target.Provider,
            value.Target.StableDeviceIdentity).ContentHash, "CameraNetworkTargetBindingMismatch");
        Require(value.Requested is not null, "CameraNetworkRequestedInvalid");
        ValidateConfiguration(value.Requested!, "CameraNetworkRequestedInvalid");
        if (value.Previous is not null) ValidateConfiguration(value.Previous, "CameraNetworkPreviousInvalid");
        if (value.Observed is not null) ValidateConfiguration(value.Observed, "CameraNetworkObservedInvalid");
        ValidateStation(value.StationNetwork);
        Require(Enum.IsDefined(value.State), "CameraNetworkStateInvalid");
        Require(value.ReasonCode is { Length: > 0 and <= 128 } && IsSafeText(value.ReasonCode),
            "CameraNetworkReasonInvalid");
        Require(value.ChangeReason is { Length: > 0 and <= 256 } && !value.ChangeReason.Any(char.IsControl),
            "CameraNetworkChangeReasonInvalid");
        Require(value.AuthorizationRevision >= 0, "CameraNetworkAuthorizationRevisionInvalid");
        Require(value.RecordedAtUtc != default, "CameraNetworkRecordedAtInvalid");
        if (value.Phase != CameraNetworkEventPhase.Rejected &&
            (!value.ActorPrincipalId.HasValue || value.ActorPrincipalId == Guid.Empty ||
             !value.SessionId.HasValue || value.SessionId == Guid.Empty))
            throw new InvalidOperationException("CameraNetworkIdentityInvalid");
        if (value.Phase == CameraNetworkEventPhase.Rejected)
        {
            Require(value.State == CameraNetworkMaintenanceState.Failed,
                "CameraNetworkRejectedStateInvalid");
            // Rejected commands may lack an actual previous configuration, but
            // they still retain a requested target and a safe reason.
        }
        else
        {
            Require(value.Previous is not null, "CameraNetworkPreviousRequired");
            if (value.Phase == CameraNetworkEventPhase.Admission)
                Require(value.State == CameraNetworkMaintenanceState.Pending,
                    "CameraNetworkStateInvalid");
            else
                Require(value.State is CameraNetworkMaintenanceState.Succeeded or
                    CameraNetworkMaintenanceState.Failed or CameraNetworkMaintenanceState.Unknown,
                    "CameraNetworkStateInvalid");
        }
        if (value.Phase == CameraNetworkEventPhase.Admission)
            Require(!value.IdentityVerified, "CameraNetworkAdmissionIdentityInvalid");
        if (value.Phase == CameraNetworkEventPhase.Terminal &&
            value.State == CameraNetworkMaintenanceState.Succeeded)
            Require(value.IdentityVerified && value.Observed is not null && value.Observed == value.Requested,
                "CameraNetworkTerminalIdentityInvalid");
    }

    internal static string PayloadHash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload ?? throw new ArgumentNullException(nameof(payload))));

    internal static long ReadPosition(byte[] payload) => Decode(payload).Position;

    private static void ValidateConfiguration(CameraIpv4Configuration value, string reason)
    {
        try
        {
            _ = new CameraIpv4Configuration(value.Address, value.PrefixLength, value.Gateway);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        { throw new InvalidOperationException(reason, exception); }
    }

    private static void ValidateStation(CameraStationNetwork? value)
    {
        if (value is null) return;
        Require(value.InterfaceId is { Length: > 0 and <= 128 } && IsSafeText(value.InterfaceId),
            "CameraNetworkStationNetworkInvalid");
        ValidateConfiguration(value.Configuration, "CameraNetworkStationNetworkInvalid");
    }

    private static CameraStationNetwork? ParseStation(string? interfaceId, string? address,
        string? prefix, string? gateway)
    {
        if (interfaceId is null && address is null && prefix is null && gateway is null) return null;
        if (interfaceId is null || address is null || prefix is null)
            throw new InvalidOperationException("CameraNetworkStationNetworkInvalid");
        var configuration = ParseConfiguration(address, prefix, gateway, "CameraNetworkStationNetworkInvalid")
            ?? throw new InvalidOperationException("CameraNetworkStationNetworkInvalid");
        return new CameraStationNetwork(interfaceId, configuration);
    }

    private static CameraIpv4Configuration? ParseConfiguration(string? address, string? prefix,
        string? gateway, string reason)
    {
        if (address is null && prefix is null && gateway is null) return null;
        if (address is null || prefix is null || !int.TryParse(prefix, NumberStyles.None,
                CultureInfo.InvariantCulture, out var prefixValue))
            throw new InvalidOperationException(reason);
        try { return new CameraIpv4Configuration(address, prefixValue, gateway); }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        { throw new InvalidOperationException(reason, exception); }
    }

    private static string Required(string? value, string reason) => value is { Length: > 0 and <= MaximumTextLength }
        ? value : throw new InvalidOperationException(reason);

    private static Guid ParseGuid(string? value, string reason) => Guid.TryParseExact(value, "D", out var result) &&
        result != Guid.Empty ? result : throw new InvalidOperationException(reason);

    private static Guid? ParseNullableGuid(string? value, string reason) => value is null ? null : ParseGuid(value, reason);

    private static long ParseLong(string? value, string reason) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : throw new InvalidOperationException(reason);

    private static T ParseEnum<T>(string? value, string reason) where T : struct, Enum =>
        Enum.TryParse<T>(value, out var result) && Enum.IsDefined(result) && value == result.ToString()
            ? result : throw new InvalidOperationException(reason);

    private static bool IsSafeText(string value) => value.Trim() == value && !value.Any(char.IsControl);

    private static string?[] DecodeFields(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        var version = reader.ReadBytes(4);
        if (version.Length != 4 || BinaryPrimitives.ReadInt32BigEndian(version) != AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var kind = ReadValue(reader);
        if (kind != "CameraNetworkEvent") throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var countBytes = reader.ReadBytes(4);
        if (countBytes.Length != 4) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var count = BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count != FieldCount) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var fields = Enumerable.Range(0, count).Select(_ => ReadValue(reader)).ToArray();
        if (input.Position != input.Length) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        return fields;
    }

    private static string? ReadValue(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        if (marker == 0) return null;
        if (marker != 1) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var lengthBytes = reader.ReadBytes(4);
        if (lengthBytes.Length != 4) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length is < 0 or > MaximumTextLength) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidOperationException("CameraNetworkPayloadInvalid");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
