using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

/// <summary>Canonical bounded payload for one immutable imaging setup revision.</summary>
internal static class ImagingSetupRevisionStorageCodec
{
    internal const int FormatVersion = 1;
    internal const int MaximumPayloadBytes = ImagingSetupStoreOptions.MaximumPayloadBytesHardLimit;
    internal const int MaximumEncodedPayloadChars = 180_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    internal static byte[] Encode(ImagingSetupRevision value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value, value.Position);
        var dto = ToDto(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (payload.Length > MaximumPayloadBytes)
            throw new InvalidOperationException("ImagingSetupEvidenceOversized");
        return payload;
    }

    internal static ImagingSetupRevision Decode(byte[] payload, long expectedPosition)
    {
        if (payload is null || payload.Length is < 1 or > MaximumPayloadBytes)
            throw new InvalidOperationException("ImagingSetupPayloadInvalid");
        RevisionDto? dto;
        try { dto = JsonSerializer.Deserialize<RevisionDto>(payload, Json); }
        catch (JsonException) { throw new InvalidOperationException("ImagingSetupPayloadInvalid"); }
        if (dto is null) throw new InvalidOperationException("ImagingSetupPayloadInvalid");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(dto, Json);
        if (!payload.SequenceEqual(canonical))
            throw new InvalidOperationException("ImagingSetupPayloadCanonicalMismatch");
        try
        {
            var value = FromDto(dto);
            Validate(value, expectedPosition);
            return value;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { throw new InvalidOperationException("ImagingSetupPayloadInvalid", ex); }
    }

    internal static void ValidatePayload(byte[] payload, long expectedPosition) =>
        _ = Decode(payload, expectedPosition);

    internal static string PayloadHash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));

    internal static string ComputeAuthorizationTarget(ImagingSetupRevision value,
        long expectedBindingRevision, string expectedBindingRevisionHash,
        long expectedRevision, string? expectedRevisionHash)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-imaging-setup-change-v1", value.LogicalCameraRole,
            expectedBindingRevision.ToString(CultureInfo.InvariantCulture), expectedBindingRevisionHash,
            expectedRevision.ToString(CultureInfo.InvariantCulture), expectedRevisionHash,
            value.Definition.ContentHash, value.ChangeReason
        });
    }

    internal static void Validate(ImagingSetupRevision value, long expectedPosition)
    {
        if (value.Position != expectedPosition || expectedPosition < 1 || value.Revision < 1 ||
            value.OperationId == Guid.Empty || value.ActorPrincipalId == Guid.Empty || value.SessionId == Guid.Empty ||
            value.AuthorizationRevision < 0 || value.RecordedAtUtc == default ||
            !IsIdentifier(value.LogicalCameraRole) || value.Binding.LogicalRole != value.LogicalCameraRole ||
            value.Binding.Revision < 1 || !IsHash(value.Binding.RevisionHash) ||
            !IsHash(value.Binding.Target.ContentHash) || !IsHash(value.RevisionHash) ||
            (value.Revision == 1 ? value.PreviousRevisionHash is not null : !IsHash(value.PreviousRevisionHash)))
            throw new InvalidOperationException("ImagingSetupRevisionShapeInvalid");
        if (!Enum.IsDefined(value.Origin) || value.ChangeReason.Length is < 1 or > 512 ||
            value.ChangeReason.Any(char.IsControl))
            throw new InvalidOperationException("ImagingSetupRevisionShapeInvalid");
        var expected = new ImagingSetupRevision(value.Position, value.LogicalCameraRole, value.Revision,
            value.OperationId, value.PreviousRevisionHash, value.Binding, value.Definition, value.Origin,
            value.ActorPrincipalId, value.SessionId, value.AuthorizationRevision, value.ChangeReason,
            value.RecordedAtUtc);
        if (expected.RevisionHash != value.RevisionHash)
            throw new InvalidOperationException("ImagingSetupRevisionHashMismatch");
    }

    private static RevisionDto ToDto(ImagingSetupRevision value) => new()
    {
        FormatVersion = FormatVersion,
        Position = value.Position,
        LogicalCameraRole = value.LogicalCameraRole,
        Revision = value.Revision,
        OperationId = value.OperationId.ToString("D"),
        PreviousRevisionHash = value.PreviousRevisionHash,
        RevisionHash = value.RevisionHash,
        Binding = new BindingDto
        {
            Position = value.Binding.Position,
            LogicalRole = value.Binding.LogicalRole,
            Revision = value.Binding.Revision,
            OperationId = value.Binding.OperationId.ToString("D"),
            PreviousRevisionHash = value.Binding.PreviousRevisionHash,
            RevisionHash = value.Binding.RevisionHash,
            Target = new TargetDto
            {
                ProviderId = value.Binding.Target.Provider.Id,
                ProviderVersion = value.Binding.Target.Provider.Version,
                AdapterPackageId = value.Binding.Target.Provider.AdapterPackageId,
                AdapterVersion = value.Binding.Target.Provider.AdapterVersion,
                StableDeviceIdentity = value.Binding.Target.StableDeviceIdentity,
                ContentHash = value.Binding.Target.ContentHash
            },
            AuthorPrincipalId = value.Binding.AuthorPrincipalId.ToString("D"),
            AuthorSessionId = value.Binding.AuthorSessionId.ToString("D"),
            AuthorAuthorizationRevision = value.Binding.AuthorAuthorizationRevision,
            ChangeReason = value.Binding.ChangeReason,
            RecordedAtUtc = value.Binding.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        },
        Definition = new DefinitionDto
        {
            LensIdentity = value.Definition.LensIdentity,
            FocusOrFocalLengthState = value.Definition.FocusOrFocalLengthState,
            MountingPose = value.Definition.MountingPose,
            WorkingDistanceMm = value.Definition.WorkingDistanceMm,
            SensorOrientation = value.Definition.SensorOrientation,
            ContentHash = value.Definition.ContentHash
        },
        Origin = (int)value.Origin,
        ActorPrincipalId = value.ActorPrincipalId.ToString("D"),
        SessionId = value.SessionId.ToString("D"),
        AuthorizationRevision = value.AuthorizationRevision,
        ChangeReason = value.ChangeReason,
        RecordedAtUtc = value.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    };

    private static ImagingSetupRevision FromDto(RevisionDto dto)
    {
        if (dto.FormatVersion != FormatVersion || dto.Binding is null || dto.Definition is null ||
            !Guid.TryParseExact(dto.OperationId, "D", out var operationId) ||
            !Guid.TryParseExact(dto.ActorPrincipalId, "D", out var actor) || actor == Guid.Empty ||
            !Guid.TryParseExact(dto.SessionId, "D", out var session) || session == Guid.Empty ||
            !Enum.IsDefined(typeof(ImagingSetupChangeOrigin), dto.Origin) ||
            !DateTimeOffset.TryParseExact(dto.RecordedAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var recorded))
            throw new InvalidOperationException("ImagingSetupPayloadInvalid");
        var binding = FromBindingDto(dto.Binding);
        var definition = new ImagingSetupDefinition(dto.Definition.LensIdentity ?? string.Empty,
            dto.Definition.FocusOrFocalLengthState ?? string.Empty, dto.Definition.MountingPose ?? string.Empty,
            dto.Definition.WorkingDistanceMm, dto.Definition.SensorOrientation ?? string.Empty);
        if (definition.ContentHash != dto.Definition.ContentHash)
            throw new InvalidOperationException("ImagingSetupDefinitionHashMismatch");
        var value = new ImagingSetupRevision(dto.Position, dto.LogicalCameraRole ?? string.Empty, dto.Revision,
            operationId, dto.PreviousRevisionHash, binding, definition, (ImagingSetupChangeOrigin)dto.Origin,
            actor, session, dto.AuthorizationRevision, dto.ChangeReason ?? string.Empty, recorded);
        if (!string.Equals(value.RevisionHash, dto.RevisionHash, StringComparison.Ordinal))
            throw new InvalidOperationException("ImagingSetupRevisionHashMismatch");
        return value;
    }

    private static CameraBindingRevision FromBindingDto(BindingDto dto)
    {
        if (!Guid.TryParseExact(dto.OperationId, "D", out var operationId) || operationId == Guid.Empty ||
            !Guid.TryParseExact(dto.AuthorPrincipalId, "D", out var principal) || principal == Guid.Empty ||
            !Guid.TryParseExact(dto.AuthorSessionId, "D", out var session) || session == Guid.Empty ||
            !DateTimeOffset.TryParseExact(dto.RecordedAtUtc, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var recorded))
            throw new InvalidOperationException("ImagingSetupBindingInvalid");
        var target = new CameraBindingTarget(new CameraProviderIdentity(dto.Target?.ProviderId ?? string.Empty,
            dto.Target?.ProviderVersion ?? string.Empty, dto.Target?.AdapterPackageId ?? string.Empty,
            dto.Target?.AdapterVersion ?? string.Empty), dto.Target?.StableDeviceIdentity ?? string.Empty);
        if (dto.Target is null || target.ContentHash != dto.Target.ContentHash)
            throw new InvalidOperationException("ImagingSetupBindingTargetInvalid");
        return new CameraBindingRevision(dto.Position, dto.LogicalRole ?? string.Empty, dto.Revision,
            operationId, dto.PreviousRevisionHash, dto.RevisionHash ?? string.Empty, target, principal, session,
            dto.AuthorAuthorizationRevision, dto.ChangeReason ?? string.Empty, recorded);
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(static c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private sealed class RevisionDto
    {
        public int FormatVersion { get; set; }
        public long Position { get; set; }
        public string? LogicalCameraRole { get; set; }
        public long Revision { get; set; }
        public string? OperationId { get; set; }
        public string? PreviousRevisionHash { get; set; }
        public string? RevisionHash { get; set; }
        public BindingDto? Binding { get; set; }
        public DefinitionDto? Definition { get; set; }
        public int Origin { get; set; }
        public string? ActorPrincipalId { get; set; }
        public string? SessionId { get; set; }
        public long AuthorizationRevision { get; set; }
        public string? ChangeReason { get; set; }
        public string? RecordedAtUtc { get; set; }
    }

    private sealed class BindingDto
    {
        public long Position { get; set; }
        public string? LogicalRole { get; set; }
        public long Revision { get; set; }
        public string? OperationId { get; set; }
        public string? PreviousRevisionHash { get; set; }
        public string? RevisionHash { get; set; }
        public TargetDto? Target { get; set; }
        public string? AuthorPrincipalId { get; set; }
        public string? AuthorSessionId { get; set; }
        public long AuthorAuthorizationRevision { get; set; }
        public string? ChangeReason { get; set; }
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

    private sealed class DefinitionDto
    {
        public string? LensIdentity { get; set; }
        public string? FocusOrFocalLengthState { get; set; }
        public string? MountingPose { get; set; }
        public double WorkingDistanceMm { get; set; }
        public string? SensorOrientation { get; set; }
        public string? ContentHash { get; set; }
    }
}
