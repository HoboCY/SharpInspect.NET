using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Identity;

internal enum IdentityEventKind
{
    IdentityConfigured,
    BootstrapIssued,
    BootstrapRejected,
    BootstrapExpired,
    AdministratorCreated,
    RecoveryKitIssued,
    AuthenticationSucceeded,
    AuthenticationRejected,
    PasswordVerifierUpgraded,
    AuthenticationThrottled,
    CredentialDisabled,
    SessionStarted,
    SessionLocked,
    SessionLoggedOut,
    SessionSignInCancelled,
    HumanAccountCreated,
    HumanPermissionsChanged,
    CredentialUnlocked,
    CredentialRebound,
    StepUpIssued,
    StepUpRejected,
    StepUpCancelled,
    StepUpConsumed,
    ManagementRejected,
    AdministratorRecovered,
    RecoveryRejected,
    RecoveryKitRotated,
    RecoveryKitRotationRejected,
    RecoveryKitCustodyConfirmed,
    RecoveryKitCustodyRejected,
    AlarmActionAuthorized
}

/// <summary>Closed, non-secret identity evidence. Credential material never belongs in this type.</summary>
internal sealed record IdentityAuditEvent(Guid EventId, IdentityEventKind Kind, DateTimeOffset OccurredAtUtc,
    string StationId, Guid? PrincipalId, Guid? CredentialId, Guid? TokenId, Guid? RecoveryKitId,
    string ReasonCode, string? WindowsSid = null, string? PasswordPolicyVersion = null,
    string? BlocklistId = null, string? BlocklistVersion = null, string? HashBaselineVersion = null, long StateRevision = 0,
    int HashTargetCost = 0, int RecordCost = 0, string? AuthenticationPolicyId = null,
    string? AuthenticationPolicyVersion = null, string? AuthenticationPolicyHash = null,
    string? ProtectedAttemptIdentifier = null, int AccountFailures = 0, int StationFailures = 0,
    DateTimeOffset? NextAllowedAtUtc = null, Guid? SessionId = null, long DelayTicks = 0,
    string? AuthorizationPolicyId = null, string? AuthorizationPolicyVersion = null,
    string? AuthorizationPolicyHash = null, Guid? ActorPrincipalId = null,
    Guid? CommandCorrelationId = null, Guid? StepUpGrantId = null,
    string? RequiredPermission = null, Guid? TargetPrincipalId = null,
    long AuthorizationRevision = 0, string? ManagementReason = null, string? ActionTargetId = null,
    Guid? BoundCommandCorrelationId = null, string? ActionCommandKind = null,
    string? PreviousPermissions = null, string? ResultingPermissions = null,
    Guid? OperationId = null, Guid? RecoveryCodeId = null, Guid? PreviousRecoveryKitId = null,
    string? RecoverySafetyEvidence = null)
{
    internal byte[] Encode(long ordinal, int schemaVersion = 6)
    {
        var fields = new List<string?>
        {
            ordinal.ToString(CultureInfo.InvariantCulture), EventId.ToString("D"), Kind.ToString(),
            OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture), StationId, PrincipalId?.ToString("D"),
            CredentialId?.ToString("D"), TokenId?.ToString("D"), RecoveryKitId?.ToString("D"), ReasonCode,
            WindowsSid, PasswordPolicyVersion, BlocklistId, BlocklistVersion, HashBaselineVersion,
            StateRevision.ToString(CultureInfo.InvariantCulture), HashTargetCost.ToString(CultureInfo.InvariantCulture),
            RecordCost.ToString(CultureInfo.InvariantCulture)
        };

        if (schemaVersion >= 4)
        {
            fields.AddRange(new string?[]
            {
                AuthenticationPolicyId, AuthenticationPolicyVersion, AuthenticationPolicyHash,
                ProtectedAttemptIdentifier, AccountFailures.ToString(CultureInfo.InvariantCulture),
                StationFailures.ToString(CultureInfo.InvariantCulture),
                NextAllowedAtUtc?.ToString("O", CultureInfo.InvariantCulture), SessionId?.ToString("D"),
                DelayTicks.ToString(CultureInfo.InvariantCulture)
            });
        }

        if (schemaVersion >= 5)
        {
            fields.AddRange(new string?[]
            {
                AuthorizationPolicyId, AuthorizationPolicyVersion, AuthorizationPolicyHash,
                ActorPrincipalId?.ToString("D"), CommandCorrelationId?.ToString("D"),
                StepUpGrantId?.ToString("D"), RequiredPermission, TargetPrincipalId?.ToString("D"),
                AuthorizationRevision.ToString(CultureInfo.InvariantCulture), ManagementReason, ActionTargetId,
                BoundCommandCorrelationId?.ToString("D"), ActionCommandKind,
                PreviousPermissions, ResultingPermissions
            });
        }

        if (schemaVersion >= 6)
        {
            fields.AddRange(new string?[]
            {
                OperationId?.ToString("D"), RecoveryCodeId?.ToString("D"),
                PreviousRecoveryKitId?.ToString("D"), RecoverySafetyEvidence
            });
        }

        if (schemaVersion is < 3 or > 7)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        return AuditCanonical.Encode("IdentityEvent", fields.ToArray());
    }

    internal static long VerifyPayload(byte[] payload, long ordinal, string stationId, int schemaVersion = 6)
    {
        if (schemaVersion is < 3 or > 7)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));

        using var input = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
        int ReadInteger()
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4) throw new InvalidOperationException("AuditIdentityPayloadInvalid");
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }
        string? ReadValue()
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            AuditChainDatabase.Require(marker == 1, "AuditIdentityPayloadInvalid");
            var length = ReadInteger();
            AuditChainDatabase.Require(length is >= 0 and <= 1024, "AuditIdentityPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            AuditChainDatabase.Require(bytes.Length == length, "AuditIdentityPayloadInvalid");
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        try
        {
            var expectedCount = schemaVersion switch { 3 => 18, 4 => 27, 5 => 42, 6 or 7 => 46, _ => 0 };
            AuditChainDatabase.Require(ReadInteger() == AuditCanonical.CanonicalizationVersion &&
                ReadValue() == "IdentityEvent" && ReadInteger() == expectedCount,
                "AuditIdentityPayloadInvalid");
            var fields = Enumerable.Range(0, expectedCount).Select(_ => ReadValue()).ToArray();
            AuditChainDatabase.Require(input.Position == input.Length &&
                fields[0] == ordinal.ToString(CultureInfo.InvariantCulture) && fields[4] == stationId &&
                Guid.TryParseExact(fields[1], "D", out var eventId) && eventId != Guid.Empty &&
                Enum.TryParse<IdentityEventKind>(fields[2], out var kind) && Enum.IsDefined(kind) &&
                fields[2] == kind.ToString() &&
                DateTimeOffset.TryParseExact(fields[3], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var occurred) && occurred != default &&
                fields[9] is { Length: > 0 and <= 128 }, "AuditIdentityPayloadInvalid");
            if (Enum.TryParse<IdentityEventKind>(fields[2], out var legacyKind))
            {
                AuditChainDatabase.Require(schemaVersion >= 5
                    ? schemaVersion >= 6 || (int)legacyKind < (int)IdentityEventKind.AdministratorRecovered
                    : (int)legacyKind <= (int)IdentityEventKind.SessionSignInCancelled,
                    "AuditIdentityPayloadInvalid");
                AuditChainDatabase.Require(schemaVersion >= 7 || legacyKind != IdentityEventKind.AlarmActionAuthorized,
                    "AuditIdentityPayloadInvalid");
            }
            for (var index = 5; index <= 8; index++)
                AuditChainDatabase.Require(fields[index] is null ||
                    Guid.TryParseExact(fields[index], "D", out _), "AuditIdentityPayloadInvalid");
            AuditChainDatabase.Require(long.TryParse(fields[15], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var revision) && revision >= 0 &&
                fields[15] == revision.ToString(CultureInfo.InvariantCulture),
                "AuditIdentityPayloadInvalid");
            for (var index = 16; index <= 17; index++)
                AuditChainDatabase.Require(int.TryParse(fields[index], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var cost) &&
                    cost is >= 0 and <= PasswordHashBaseline.MaximumSupportedIterations,
                    "AuditIdentityPayloadInvalid");

            if (schemaVersion >= 4)
            {
                AuditChainDatabase.Require(fields[18] is { Length: > 0 and <= 128 } &&
                    fields[19] is { Length: > 0 and <= 128 } && IsHash(fields[20]) &&
                    (fields[21] is null || IsHash(fields[21])), "AuditAuthenticationPayloadInvalid");
                for (var index = 22; index <= 23; index++)
                    AuditChainDatabase.Require(int.TryParse(fields[index], NumberStyles.None,
                            CultureInfo.InvariantCulture, out var failures) &&
                        failures is >= 0 and <= AuthenticationPolicy.ReleaseFailureLimitCeiling,
                        "AuditAuthenticationPayloadInvalid");
                AuditChainDatabase.Require((fields[24] is null ||
                        DateTimeOffset.TryParseExact(fields[24], "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out _)) &&
                    (fields[25] is null || Guid.TryParseExact(fields[25], "D", out _)) &&
                    long.TryParse(fields[26], NumberStyles.None, CultureInfo.InvariantCulture,
                        out var delay) && delay >= 0 && delay <= TimeSpan.FromHours(1).Ticks,
                    "AuditAuthenticationPayloadInvalid");
            }

            if (schemaVersion >= 5)
            {
                AuditChainDatabase.Require(fields[27] is { Length: > 0 and <= 128 } &&
                    fields[28] is { Length: > 0 and <= 128 } && IsHash(fields[29]),
                    "AuditAuthorizationPayloadInvalid");
                foreach (var index in new[] { 30, 31, 32, 34 })
                    AuditChainDatabase.Require(fields[index] is null ||
                        Guid.TryParseExact(fields[index], "D", out _),
                        "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[33] is null || IsSafeIdentifier(fields[33]),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(long.TryParse(fields[35], NumberStyles.None,
                        CultureInfo.InvariantCulture, out var authorizationRevision) &&
                    authorizationRevision >= 0 &&
                    fields[35] == authorizationRevision.ToString(CultureInfo.InvariantCulture),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[36] is null ||
                    (Enum.TryParse<IdentityManagementReason>(fields[36], out var managementReason) &&
                     Enum.IsDefined(managementReason) && fields[36] == managementReason.ToString()),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[37] is null || IsSafeIdentifier(fields[37]),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[38] is null ||
                    Guid.TryParseExact(fields[38], "D", out _), "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(fields[39] is null ||
                    (Enum.TryParse<AuditedCommandKind>(fields[39], out var actionKind) &&
                     Enum.IsDefined(actionKind) && actionKind is not AuditedCommandKind.Unsupported and
                     not AuditedCommandKind.GracefulProductionStop && fields[39] == actionKind.ToString()),
                    "AuditAuthorizationPayloadInvalid");
                AuditChainDatabase.Require(IsPermissionSet(fields[40], schemaVersion >= 7 ? 30 : 28) &&
                    IsPermissionSet(fields[41], schemaVersion >= 7 ? 30 : 28),
                    "AuditAuthorizationPayloadInvalid");
            }

            if (schemaVersion >= 6)
            {
                foreach (var index in new[] { 42, 43, 44 })
                    AuditChainDatabase.Require(fields[index] is null ||
                        Guid.TryParseExact(fields[index], "D", out _), "AuditRecoveryPayloadInvalid");
                AuditChainDatabase.Require(fields[45] is null || IsSafeIdentifier(fields[45]),
                    "AuditRecoveryPayloadInvalid");
            }

            return revision;
        }
        catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidOperationException("AuditIdentityPayloadInvalid");
        }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsSafeIdentifier(string? value) => value is { Length: > 0 and <= 128 } &&
        value.Trim() == value && !value.Any(char.IsControl);

    private static bool IsPermissionSet(string? value, int maximumPermissions)
    {
        if (value is null or { Length: 0 }) return true;
        if (value.Length > 1024 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)) return false;
        var previous = 0;
        var seen = new HashSet<Permission>();
        var names = value.Split(',', StringSplitOptions.None);
        if (names.Length is < 1 || names.Length > maximumPermissions) return false;
        foreach (var name in names)
        {
            if (!Enum.TryParse<Permission>(name, out var permission) || !Enum.IsDefined(permission) ||
                permission == Permission.None || name != permission.ToString() || !seen.Add(permission) ||
                (int)permission <= previous)
                return false;
            previous = (int)permission;
        }

        return true;
    }
}
