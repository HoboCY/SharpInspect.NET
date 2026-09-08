using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal enum CameraSetupEventPhase
{
    Admission,
    Terminal,
    Completed,
    Rejected
}

/// <summary>One immutable camera setup fact. Position and revision hashes are assigned by the writer.</summary>
internal sealed record CameraSetupEvent
{
    internal CameraSetupEvent(long position, Guid eventId, Guid operationId, string logicalRole,
        AuditedCommandKind commandKind, CameraSetupEventPhase phase, long bindingRevision,
        string? previousRevisionHash = null, string? revisionHash = null,
        CameraBindingTarget? previousTarget = null, CameraBindingTarget? target = null,
        RequestedCameraConfiguration? requested = null, EffectiveCameraConfiguration? effective = null,
        IEnumerable<CameraConfigurationDifference>? differences = null,
        CameraProviderExtensionRequirement? extension = null, CameraHealthSnapshot? health = null,
        bool succeeded = false, string reasonCode = "CameraSetupRejected", string changeReason = "",
        Guid? actorPrincipalId = null, Guid? sessionId = null, long authorAuthorizationRevision = 0,
        DateTimeOffset recordedAtUtc = default)
    {
        Position = position;
        EventId = eventId;
        OperationId = operationId;
        LogicalRole = logicalRole;
        CommandKind = commandKind;
        Phase = phase;
        BindingRevision = bindingRevision;
        PreviousRevisionHash = previousRevisionHash;
        RevisionHash = revisionHash;
        PreviousTarget = previousTarget;
        Target = target;
        Requested = requested;
        Effective = effective;
        Differences = CameraSetupStorageCodec.CopyDifferences(differences);
        Extension = extension;
        Health = health;
        Succeeded = succeeded;
        ReasonCode = reasonCode;
        ChangeReason = changeReason;
        ActorPrincipalId = actorPrincipalId;
        SessionId = sessionId;
        AuthorAuthorizationRevision = authorAuthorizationRevision;
        RecordedAtUtc = recordedAtUtc;
    }

    internal long Position { get; init; }
    internal Guid EventId { get; init; }
    internal Guid OperationId { get; init; }
    internal string LogicalRole { get; init; }
    internal AuditedCommandKind CommandKind { get; init; }
    internal CameraSetupEventPhase Phase { get; init; }
    internal long BindingRevision { get; init; }
    internal string? PreviousRevisionHash { get; init; }
    internal string? RevisionHash { get; init; }
    internal CameraBindingTarget? PreviousTarget { get; init; }
    internal CameraBindingTarget? Target { get; init; }
    internal RequestedCameraConfiguration? Requested { get; init; }
    internal EffectiveCameraConfiguration? Effective { get; init; }
    internal IReadOnlyList<CameraConfigurationDifference> Differences { get; init; }
    internal CameraProviderExtensionRequirement? Extension { get; init; }
    internal CameraHealthSnapshot? Health { get; init; }
    internal bool Succeeded { get; init; }
    internal string ReasonCode { get; init; }
    internal string ChangeReason { get; init; }
    internal Guid? ActorPrincipalId { get; init; }
    internal Guid? SessionId { get; init; }
    internal long AuthorAuthorizationRevision { get; init; }
    internal DateTimeOffset RecordedAtUtc { get; init; }
}

/// <summary>
/// The durable camera stream has no row for a Rebind admission: its row is
/// written only after the hardware operation completes.  This projection keeps
/// the verified identity/command admission visible across a restart without
/// manufacturing a CameraSetupEvent (and therefore without making it look like
/// a durable camera fact).
/// </summary>
internal sealed record CameraSetupPendingAdmission(
    Guid OperationId,
    string LogicalRole,
    AuditedCommandKind CommandKind,
    Guid ActorPrincipalId,
    Guid SessionId,
    Guid StepUpGrantId,
    long AuthorizationRevision,
    string ReasonCode);

internal sealed record CameraSetupStoreSnapshot(
    string LogicalRole,
    CameraBindingRevision? Binding,
    CameraHealthSnapshot? Health,
    RequestedCameraConfiguration? Requested,
    EffectiveCameraConfiguration? Effective,
    IReadOnlyList<CameraConfigurationDifference> Differences,
    CameraProviderExtensionRequirement? Extension,
    CameraSetupEvent? Pending,
    IReadOnlyList<CameraSetupEvent> OperationEvents,
    CameraSetupPendingAdmission? PendingAdmission = null,
    int PendingOperationCount = 0)
{
    /// <summary>True when either the camera stream or verified audit stream has pending work.</summary>
    internal bool HasPending => Pending is not null || PendingAdmission is not null;

    internal CameraSetupQueryResult ToPublicResult()
    {
        var health = Health ?? new CameraHealthSnapshot(CameraProviderAvailability.DependencyMissing,
            CameraConnectionState.Closed, CameraConfigurationState.Unconfigured,
            CameraAcquisitionState.Stopped, new FrameTimePoint(DateTimeOffset.UnixEpoch, 0));
        var reason = HasPending ? "CameraSetupOperationPending" :
            Binding is null ? "CameraSetupUnconfigured" : "CameraSetupAvailable";
        var snapshot = new CameraSetupSnapshot(LogicalRole, Binding, health, Requested, Effective,
            Differences, Extension, reason);
        return new CameraSetupQueryResult(Binding is not null && !HasPending, snapshot.ReasonCode, snapshot);
    }
}

internal sealed record CameraSetupReadResult(CameraSetupQueryResult Result,
    CameraSetupStoreSnapshot State);

/// <summary>Canonical, strictly bounded representation of camera setup facts.</summary>
internal static class CameraSetupStorageCodec
{
    internal const int FormatVersion = 1;
    internal const int MaximumPayloadBytes = 128 * 1024;
    internal const int MaximumEncodedPayloadChars = 180_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    internal static byte[] Encode(CameraSetupEvent value)
    {
        ValidateEvent(value, value.Position);
        var dto = ToDto(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (payload.Length > MaximumPayloadBytes) throw new InvalidOperationException("CameraSetupEvidenceOversized");
        return payload;
    }

    internal static CameraSetupEvent Decode(byte[] payload, long expectedPosition)
    {
        if (payload is null || payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("CameraSetupPayloadInvalid");
        CameraEventDto? dto;
        try { dto = JsonSerializer.Deserialize<CameraEventDto>(payload, Json); }
        catch (JsonException) { throw new InvalidOperationException("CameraSetupPayloadInvalid"); }
        if (dto is null) throw new InvalidOperationException("CameraSetupPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical))
            throw new InvalidOperationException("CameraSetupPayloadCanonicalMismatch");
        try
        {
            var value = FromDto(dto);
            ValidateEvent(value, expectedPosition);
            return value;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { throw new InvalidOperationException("CameraSetupPayloadInvalid", ex); }
    }

    internal static void ValidatePayload(byte[] payload, long expectedPosition) =>
        _ = Decode(payload, expectedPosition);

    internal static string PayloadHash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));

    internal static string ComputeRevisionHash(CameraSetupEvent value, long revision,
        string? previousRevisionHash, CameraBindingTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // CameraSetupRuntime creates the public binding revision before handing
        // the event to storage.  Keep this verifier byte-compatible with that
        // established HashParts contract; adding fields here would make every
        // legitimate rebind unverifiable after restart.
        return HashParts("camera-binding-revision-v1", value.LogicalRole,
            revision.ToString(CultureInfo.InvariantCulture), value.OperationId.ToString("D"),
            previousRevisionHash, target.ContentHash, value.ActorPrincipalId?.ToString("D"),
            value.SessionId?.ToString("D"), value.AuthorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            value.ChangeReason, value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    private static string HashParts(params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            var bytes = value is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ComputeOperationFingerprint(CameraSetupEvent value) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("CameraSetupOperation", value.OperationId.ToString("D"),
            value.LogicalRole, ((int)value.CommandKind).ToString(CultureInfo.InvariantCulture),
            ((int)value.Phase).ToString(CultureInfo.InvariantCulture), value.BindingRevision.ToString(CultureInfo.InvariantCulture),
            value.PreviousRevisionHash, value.Target?.ContentHash, value.Requested is null ? null : EncodeConfiguration(value.Requested),
            value.Effective is null ? null : EncodeConfiguration(value.Effective),
            string.Join(";", value.Differences.Select(EncodeDifference)), value.Extension is null ? null : EncodeExtension(value.Extension),
            value.ReasonCode, value.ChangeReason, value.ActorPrincipalId?.ToString("D"), value.SessionId?.ToString("D"),
            value.AuthorAuthorizationRevision.ToString(CultureInfo.InvariantCulture))));

    internal static void ValidateEvent(CameraSetupEvent value, long expectedPosition)
    {
        if (value.Position != expectedPosition || expectedPosition <= 0 || value.EventId == Guid.Empty ||
            value.OperationId == Guid.Empty || !Enum.IsDefined(value.CommandKind) ||
            value.CommandKind is not (AuditedCommandKind.RebindCamera or AuditedCommandKind.ApplyCameraDebugConfiguration) ||
            !Enum.IsDefined(value.Phase) || value.BindingRevision < 0 || value.RecordedAtUtc == default)
            throw new InvalidOperationException("CameraSetupEventShapeInvalid");
        if (!IsLogicalRole(value.LogicalRole))
            throw new InvalidOperationException("CameraSetupLogicalRoleInvalid");
        ValidateHash(value.PreviousRevisionHash, nullable: true, "CameraSetupPreviousRevisionHashInvalid");
        ValidateHash(value.RevisionHash, nullable: true, "CameraSetupRevisionHashInvalid");
        if (value.ChangeReason is null || value.ChangeReason.Length is < 1 or > 256 ||
            value.ChangeReason.Any(char.IsControl)) throw new InvalidOperationException("CameraSetupChangeReasonInvalid");
        if (value.ReasonCode is null || value.ReasonCode.Length is < 1 or > 128 ||
            !IsReasonCode(value.ReasonCode))
            throw new InvalidOperationException("CameraSetupReasonCodeInvalid");
        if (value.ActorPrincipalId is not { } actor || actor == Guid.Empty ||
            value.SessionId is not { } session || session == Guid.Empty ||
            value.AuthorAuthorizationRevision < 0)
            throw new InvalidOperationException("CameraSetupAuthorInvalid");
        if (value.Target is null && value.CommandKind == AuditedCommandKind.RebindCamera && value.Succeeded)
            throw new InvalidOperationException("CameraSetupTargetRequired");
        if (value.CommandKind == AuditedCommandKind.ApplyCameraDebugConfiguration && value.Target is null)
            throw new InvalidOperationException("CameraSetupTargetRequired");
        if (value.Differences.Count > 6 || value.Differences.Select(item => item.Setting).Distinct().Count() != value.Differences.Count)
            throw new InvalidOperationException("CameraSetupDifferenceInvalid");
        if (value.Effective is null && value.Differences.Count != 0)
            throw new InvalidOperationException("CameraSetupDifferenceWithoutEffective");
        if (value.Effective is not null && value.Requested is null)
            throw new InvalidOperationException("CameraSetupRequestedRequired");
        if (value.CommandKind == AuditedCommandKind.RebindCamera &&
            (value.Requested is not null || value.Effective is not null || value.Differences.Count != 0 ||
             value.Extension is not null))
            throw new InvalidOperationException("CameraSetupRebindConfigurationForbidden");
        if (value.Phase == CameraSetupEventPhase.Admission)
        {
            if (value.CommandKind != AuditedCommandKind.ApplyCameraDebugConfiguration || !value.Succeeded ||
                value.Requested is null || value.Effective is not null || value.Differences.Count != 0)
                throw new InvalidOperationException("CameraSetupAdmissionInvalid");
        }
        else if (value.Phase == CameraSetupEventPhase.Terminal)
        {
            if (value.CommandKind != AuditedCommandKind.ApplyCameraDebugConfiguration ||
                value.Requested is null || (!value.Succeeded && value.Effective is not null) ||
                (!value.Succeeded && value.Differences.Count != 0) ||
                (value.Succeeded && value.Effective is null))
                throw new InvalidOperationException("CameraSetupTerminalInvalid");
            if (!value.Succeeded && value.Health is not
                { Connection: CameraConnectionState.Closed,
                    Configuration: CameraConfigurationState.Unknown,
                    Acquisition: CameraAcquisitionState.Stopped })
                throw new InvalidOperationException("CameraSetupFailureHealthInvalid");
        }
        else if (value.Phase == CameraSetupEventPhase.Completed)
        {
            if (value.CommandKind != AuditedCommandKind.RebindCamera || !value.Succeeded)
                throw new InvalidOperationException("CameraSetupCompletedInvalid");
        }
        else if (value.Phase == CameraSetupEventPhase.Rejected)
        {
            if (value.CommandKind != AuditedCommandKind.RebindCamera || value.Succeeded)
                throw new InvalidOperationException("CameraSetupRejectedInvalid");
        }
        else throw new InvalidOperationException("CameraSetupEventPhaseInvalid");
        if (value.Target is not null) ValidateTarget(value.Target, "CameraSetupTargetInvalid");
        if (value.PreviousTarget is not null) ValidateTarget(value.PreviousTarget, "CameraSetupPreviousTargetInvalid");
        if (value.Extension is not null && value.Target is null)
            throw new InvalidOperationException("CameraSetupExtensionTargetRequired");
        if (value.Extension is not null && value.Target is not null &&
            !SameProvider(value.Extension.Provider, value.Target.Provider))
            throw new InvalidOperationException("CameraSetupExtensionProviderMismatch");
        if (value.Health is not null) _ = value.Health;
    }

    private static void ValidateHash(string? value, bool nullable, string reason)
    {
        if (value is null && nullable) return;
        if (value is null || value.Length != 64 || value.Any(c =>
                !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))))
            throw new InvalidOperationException(reason);
    }

    private static void ValidateTarget(CameraBindingTarget target, string reason)
    {
        if (!string.Equals(target.ContentHash, new CameraBindingTarget(target.Provider,
                target.StableDeviceIdentity).ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException(reason);
    }

    internal static bool IsLogicalRole(string? value) => value is { Length: > 0 and <= 64 } &&
        IsIdentifier(value);

    private static bool IsIdentifier(string value) => value.All(static c =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool IsReasonCode(string value) => value.All(static c =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool SameProvider(CameraProviderIdentity left, CameraProviderIdentity right) =>
        left.Id == right.Id && left.Version == right.Version &&
        left.AdapterPackageId == right.AdapterPackageId && left.AdapterVersion == right.AdapterVersion;

    internal static IReadOnlyList<CameraConfigurationDifference> CopyDifferences(
        IEnumerable<CameraConfigurationDifference>? differences)
    {
        if (differences is null) return Array.Empty<CameraConfigurationDifference>();
        var copied = new List<CameraConfigurationDifference>(capacity: 6);
        foreach (var difference in differences)
        {
            if (copied.Count == 6)
                throw new InvalidOperationException("CameraSetupDifferenceInvalid");
            copied.Add(difference);
        }
        return new ReadOnlyCollection<CameraConfigurationDifference>(copied.ToArray());
    }

    private static CameraEventDto ToDto(CameraSetupEvent value) => new()
    {
        FormatVersion = FormatVersion,
        Position = value.Position,
        EventId = value.EventId.ToString("D"),
        OperationId = value.OperationId.ToString("D"),
        LogicalRole = value.LogicalRole,
        CommandKind = (int)value.CommandKind,
        Phase = (int)value.Phase,
        BindingRevision = value.BindingRevision,
        PreviousRevisionHash = value.PreviousRevisionHash,
        RevisionHash = value.RevisionHash,
        PreviousTarget = ToDto(value.PreviousTarget),
        Target = ToDto(value.Target),
        Requested = ToDto(value.Requested),
        Effective = ToDto(value.Effective),
        Differences = value.Differences.Select(ToDto).ToList(),
        Extension = ToDto(value.Extension),
        Health = ToDto(value.Health),
        Succeeded = value.Succeeded,
        ReasonCode = value.ReasonCode,
        ChangeReason = value.ChangeReason,
        ActorPrincipalId = value.ActorPrincipalId?.ToString("D"),
        SessionId = value.SessionId?.ToString("D"),
        AuthorAuthorizationRevision = value.AuthorAuthorizationRevision,
        RecordedAtUtc = value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    };

    private static CameraSetupEvent FromDto(CameraEventDto dto)
    {
        if (dto.FormatVersion != FormatVersion || !Guid.TryParseExact(dto.EventId, "D", out var eventId) ||
            !Guid.TryParseExact(dto.OperationId, "D", out var operationId) || eventId == Guid.Empty || operationId == Guid.Empty ||
            !Enum.IsDefined(typeof(AuditedCommandKind), dto.CommandKind) ||
            !Enum.IsDefined(typeof(CameraSetupEventPhase), dto.Phase) ||
            !DateTimeOffset.TryParseExact(dto.RecordedAtUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var recorded))
            throw new InvalidOperationException("CameraSetupPayloadInvalid");
        return new CameraSetupEvent(dto.Position, eventId, operationId, dto.LogicalRole ?? string.Empty,
            (AuditedCommandKind)dto.CommandKind, (CameraSetupEventPhase)dto.Phase, dto.BindingRevision,
            dto.PreviousRevisionHash, dto.RevisionHash, FromDto(dto.PreviousTarget), FromDto(dto.Target),
            FromDto(dto.Requested), FromDto(dto.Effective), dto.Differences?.Select(FromDto), FromDto(dto.Extension),
            FromDto(dto.Health), dto.Succeeded, dto.ReasonCode ?? string.Empty, dto.ChangeReason ?? string.Empty,
            ParseNullableGuid(dto.ActorPrincipalId), ParseNullableGuid(dto.SessionId), dto.AuthorAuthorizationRevision, recorded);
    }

    private static string EncodeConfiguration(RequestedCameraConfiguration value) =>
        string.Join("|", (int)value.ProductionAcquisitionMode, value.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture),
            value.GainDb.ToString("R", CultureInfo.InvariantCulture), EncodeRoi(value.RegionOfInterest), (int)value.PixelFormat,
            value.ValidBits?.ToString(CultureInfo.InvariantCulture), value.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture),
            value.TriggerDelayUs.ToString("R", CultureInfo.InvariantCulture), EncodeWhiteBalance(value.WhiteBalanceRgb));

    private static string EncodeConfiguration(EffectiveCameraConfiguration value) =>
        string.Join("|", (int)value.ProductionAcquisitionMode, value.ExposureTimeUs.ToString("R", CultureInfo.InvariantCulture),
            value.GainDb.ToString("R", CultureInfo.InvariantCulture), EncodeRoi(value.RegionOfInterest), (int)value.PixelFormat,
            value.ValidBits?.ToString(CultureInfo.InvariantCulture), value.AcquisitionTimeoutMs.ToString(CultureInfo.InvariantCulture),
            value.TriggerDelayUs.ToString("R", CultureInfo.InvariantCulture), EncodeWhiteBalance(value.WhiteBalanceRgb));

    private static string EncodeRoi(RegionOfInterest roi) => string.Join(",", roi.OffsetX, roi.OffsetY, roi.Width, roi.Height);
    private static string? EncodeWhiteBalance(WhiteBalanceRgb? value) => value is null ? null :
        string.Join(",", value.Red.ToString("R", CultureInfo.InvariantCulture), value.Green.ToString("R", CultureInfo.InvariantCulture), value.Blue.ToString("R", CultureInfo.InvariantCulture));
    private static string EncodeDifference(CameraConfigurationDifference value) =>
        string.Join("|", (int)value.Setting, value.Requested.ToString("R", CultureInfo.InvariantCulture), value.Effective.ToString("R", CultureInfo.InvariantCulture));
    private static string EncodeExtension(CameraProviderExtensionRequirement value) =>
        string.Join("|", value.Provider.Id, value.Provider.Version, value.Provider.AdapterPackageId, value.Provider.AdapterVersion,
            value.ContractId, value.ContractVersion, value.ConfigurationContentHash);

    private static TargetDto? ToDto(CameraBindingTarget? value) => value is null ? null : new()
    {
        ProviderId = value.Provider.Id, ProviderVersion = value.Provider.Version,
        AdapterPackageId = value.Provider.AdapterPackageId, AdapterVersion = value.Provider.AdapterVersion,
        StableDeviceIdentity = value.StableDeviceIdentity, ContentHash = value.ContentHash
    };
    private static CameraBindingTarget? FromDto(TargetDto? value) => value is null ? null :
        new(new CameraProviderIdentity(value.ProviderId ?? string.Empty, value.ProviderVersion ?? string.Empty,
            value.AdapterPackageId ?? string.Empty, value.AdapterVersion ?? string.Empty), value.StableDeviceIdentity ?? string.Empty);
    private static RequestedDto? ToDto(RequestedCameraConfiguration? value) => value is null ? null : new()
    {
        ProductionAcquisitionMode = (int)value.ProductionAcquisitionMode, ExposureTimeUs = value.ExposureTimeUs,
        GainDb = value.GainDb, RegionOfInterest = ToDto(value.RegionOfInterest), PixelFormat = (int)value.PixelFormat,
        ValidBits = value.ValidBits, AcquisitionTimeoutMs = value.AcquisitionTimeoutMs, TriggerDelayUs = value.TriggerDelayUs,
        WhiteBalanceRgb = ToDto(value.WhiteBalanceRgb)
    };
    private static EffectiveDto? ToDto(EffectiveCameraConfiguration? value) => value is null ? null : new()
    {
        ProductionAcquisitionMode = (int)value.ProductionAcquisitionMode, ExposureTimeUs = value.ExposureTimeUs,
        GainDb = value.GainDb, RegionOfInterest = ToDto(value.RegionOfInterest), PixelFormat = (int)value.PixelFormat,
        ValidBits = value.ValidBits, AcquisitionTimeoutMs = value.AcquisitionTimeoutMs, TriggerDelayUs = value.TriggerDelayUs,
        WhiteBalanceRgb = ToDto(value.WhiteBalanceRgb)
    };
    private static RequestedCameraConfiguration? FromDto(RequestedDto? value) => value is null ? null :
        new((ProductionAcquisitionMode)value.ProductionAcquisitionMode, value.ExposureTimeUs, value.GainDb,
            FromDto(value.RegionOfInterest), (VisionPixelFormat)value.PixelFormat, value.ValidBits,
            value.AcquisitionTimeoutMs, value.TriggerDelayUs, FromDto(value.WhiteBalanceRgb));
    private static EffectiveCameraConfiguration? FromDto(EffectiveDto? value) => value is null ? null :
        new((ProductionAcquisitionMode)value.ProductionAcquisitionMode, value.ExposureTimeUs, value.GainDb,
            FromDto(value.RegionOfInterest), (VisionPixelFormat)value.PixelFormat, value.ValidBits,
            value.AcquisitionTimeoutMs, value.TriggerDelayUs, FromDto(value.WhiteBalanceRgb));
    private static RoiDto? ToDto(RegionOfInterest? value) => value is null ? null : new()
    { OffsetX = value.OffsetX, OffsetY = value.OffsetY, Width = value.Width, Height = value.Height };
    private static RegionOfInterest FromDto(RoiDto? value) => value is null ?
        throw new InvalidOperationException("CameraSetupRoiMissing") : new(value.OffsetX, value.OffsetY, value.Width, value.Height);
    private static WhiteBalanceDto? ToDto(WhiteBalanceRgb? value) => value is null ? null : new()
    { Red = value.Red, Green = value.Green, Blue = value.Blue };
    private static WhiteBalanceRgb? FromDto(WhiteBalanceDto? value) => value is null ? null : new(value.Red, value.Green, value.Blue);
    private static DifferenceDto ToDto(CameraConfigurationDifference value) => new()
    { Setting = (int)value.Setting, Requested = value.Requested, Effective = value.Effective };
    private static CameraConfigurationDifference FromDto(DifferenceDto value) => new(
        (CameraNumericSetting)value.Setting, value.Requested, value.Effective);
    private static ExtensionDto? ToDto(CameraProviderExtensionRequirement? value) => value is null ? null : new()
    { Provider = ToDto(value.Provider), ContractId = value.ContractId, ContractVersion = value.ContractVersion,
        ConfigurationContentHash = value.ConfigurationContentHash };
    private static CameraProviderExtensionRequirement? FromDto(ExtensionDto? value) => value is null ? null :
        new(FromProviderDto(value.Provider),
            value.ContractId ?? string.Empty, value.ContractVersion ?? string.Empty, value.ConfigurationContentHash ?? string.Empty);

    private static TargetDto ToDto(CameraProviderIdentity value) => new()
    {
        ProviderId = value.Id, ProviderVersion = value.Version,
        AdapterPackageId = value.AdapterPackageId, AdapterVersion = value.AdapterVersion
    };

    private static CameraProviderIdentity FromProviderDto(TargetDto? value) => value is null
        ? throw new InvalidOperationException("CameraSetupExtensionProviderMissing")
        : new(value.ProviderId ?? string.Empty, value.ProviderVersion ?? string.Empty,
            value.AdapterPackageId ?? string.Empty, value.AdapterVersion ?? string.Empty);
    private static HealthDto? ToDto(CameraHealthSnapshot? value) => value is null ? null : new()
    {
        ProviderAvailability = (int)value.ProviderAvailability, Connection = (int)value.Connection,
        Configuration = (int)value.Configuration, Acquisition = (int)value.Acquisition,
        ObservedAtUtc = value.ObservedAt.HostObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        ObservedMonotonicTimestamp = value.ObservedAt.MonotonicTimestamp,
        FaultClassification = value.LastFault is null ? null : (int)value.LastFault.Classification,
        FaultReasonCode = value.LastFault?.ReasonCode, FaultDiagnosticCode = value.LastFault?.DiagnosticCode
    };
    private static CameraHealthSnapshot? FromDto(HealthDto? value)
    {
        if (value is null) return null;
        if (!DateTimeOffset.TryParseExact(value.ObservedAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var observed)) throw new InvalidOperationException("CameraSetupHealthTimeInvalid");
        CameraFault? fault = value.FaultClassification is null ? null : new CameraFault(
            (CameraFaultClassification)value.FaultClassification.Value, value.FaultReasonCode ?? string.Empty,
            value.FaultDiagnosticCode);
        return new CameraHealthSnapshot((CameraProviderAvailability)value.ProviderAvailability,
            (CameraConnectionState)value.Connection, (CameraConfigurationState)value.Configuration,
            (CameraAcquisitionState)value.Acquisition, new FrameTimePoint(observed, value.ObservedMonotonicTimestamp), fault);
    }

    private static Guid? ParseNullableGuid(string? value) => value is null ? null :
        Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty ? parsed :
        throw new InvalidOperationException("CameraSetupGuidInvalid");

    private sealed class CameraEventDto
    {
        public int FormatVersion { get; set; }
        public long Position { get; set; }
        public string? EventId { get; set; }
        public string? OperationId { get; set; }
        public string? LogicalRole { get; set; }
        public int CommandKind { get; set; }
        public int Phase { get; set; }
        public long BindingRevision { get; set; }
        public string? PreviousRevisionHash { get; set; }
        public string? RevisionHash { get; set; }
        public TargetDto? PreviousTarget { get; set; }
        public TargetDto? Target { get; set; }
        public RequestedDto? Requested { get; set; }
        public EffectiveDto? Effective { get; set; }
        public List<DifferenceDto>? Differences { get; set; }
        public ExtensionDto? Extension { get; set; }
        public HealthDto? Health { get; set; }
        public bool Succeeded { get; set; }
        public string? ReasonCode { get; set; }
        public string? ChangeReason { get; set; }
        public string? ActorPrincipalId { get; set; }
        public string? SessionId { get; set; }
        public long AuthorAuthorizationRevision { get; set; }
        public string? RecordedAtUtc { get; set; }
    }

    private sealed class TargetDto
    {
        public string? ProviderId { get; set; }
        public string? ProviderVersion { get; set; }
        public string? AdapterPackageId { get; set; }
        public string? AdapterVersion { get; set; }
        public string? StableDeviceIdentity { get; set; }
        public string? ContentHash { get; set; }
    }
    private class RequestedDto
    {
        public int ProductionAcquisitionMode { get; set; }
        public double ExposureTimeUs { get; set; }
        public double GainDb { get; set; }
        public RoiDto? RegionOfInterest { get; set; }
        public int PixelFormat { get; set; }
        public int? ValidBits { get; set; }
        public int AcquisitionTimeoutMs { get; set; }
        public double TriggerDelayUs { get; set; }
        public WhiteBalanceDto? WhiteBalanceRgb { get; set; }
    }
    private sealed class EffectiveDto : RequestedDto { }
    private sealed class RoiDto { public int OffsetX { get; set; } public int OffsetY { get; set; } public int Width { get; set; } public int Height { get; set; } }
    private sealed class WhiteBalanceDto { public double Red { get; set; } public double Green { get; set; } public double Blue { get; set; } }
    private sealed class DifferenceDto { public int Setting { get; set; } public double Requested { get; set; } public double Effective { get; set; } }
    private sealed class ExtensionDto { public TargetDto? Provider { get; set; } public string? ContractId { get; set; } public string? ContractVersion { get; set; } public string? ConfigurationContentHash { get; set; } }
    private sealed class HealthDto
    {
        public int ProviderAvailability { get; set; }
        public int Connection { get; set; }
        public int Configuration { get; set; }
        public int Acquisition { get; set; }
        public string? ObservedAtUtc { get; set; }
        public long ObservedMonotonicTimestamp { get; set; }
        public int? FaultClassification { get; set; }
        public string? FaultReasonCode { get; set; }
        public string? FaultDiagnosticCode { get; set; }
    }
}
