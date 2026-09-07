using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Identity;

internal enum IdentityEventKind { IdentityConfigured, BootstrapIssued, BootstrapRejected, BootstrapExpired, AdministratorCreated,
    RecoveryKitIssued, AuthenticationSucceeded, AuthenticationRejected, PasswordVerifierUpgraded,
    AuthenticationThrottled, CredentialDisabled, SessionStarted, SessionLocked, SessionLoggedOut, SessionSignInCancelled }

/// <summary>Closed, non-secret identity evidence. Credential material never belongs in this type.</summary>
internal sealed record IdentityAuditEvent(Guid EventId, IdentityEventKind Kind, DateTimeOffset OccurredAtUtc,
    string StationId, Guid? PrincipalId, Guid? CredentialId, Guid? TokenId, Guid? RecoveryKitId,
    string ReasonCode, string? WindowsSid = null, string? PasswordPolicyVersion = null,
    string? BlocklistId = null, string? BlocklistVersion = null, string? HashBaselineVersion = null, long StateRevision = 0,
    int HashTargetCost = 0, int RecordCost = 0, string? AuthenticationPolicyId = null,
    string? AuthenticationPolicyVersion = null, string? AuthenticationPolicyHash = null,
    string? ProtectedAttemptIdentifier = null, int AccountFailures = 0, int StationFailures = 0,
    DateTimeOffset? NextAllowedAtUtc = null, Guid? SessionId = null, long DelayTicks = 0)
{
    internal byte[] Encode(long ordinal) => AuditCanonical.Encode("IdentityEvent",
        ordinal.ToString(CultureInfo.InvariantCulture), EventId.ToString("D"), Kind.ToString(),
        OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture), StationId, PrincipalId?.ToString("D"),
        CredentialId?.ToString("D"), TokenId?.ToString("D"), RecoveryKitId?.ToString("D"), ReasonCode,
        WindowsSid, PasswordPolicyVersion, BlocklistId, BlocklistVersion, HashBaselineVersion, StateRevision.ToString(CultureInfo.InvariantCulture),
        HashTargetCost.ToString(CultureInfo.InvariantCulture), RecordCost.ToString(CultureInfo.InvariantCulture),
        AuthenticationPolicyId, AuthenticationPolicyVersion, AuthenticationPolicyHash, ProtectedAttemptIdentifier,
        AccountFailures.ToString(CultureInfo.InvariantCulture), StationFailures.ToString(CultureInfo.InvariantCulture),
        NextAllowedAtUtc?.ToString("O", CultureInfo.InvariantCulture), SessionId?.ToString("D"), DelayTicks.ToString(CultureInfo.InvariantCulture));

    internal static long VerifyPayload(byte[] payload, long ordinal, string stationId, bool authenticationProtection = true)
    {
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
            AuditChainDatabase.Require(ReadInteger() == AuditCanonical.CanonicalizationVersion &&
                ReadValue() == "IdentityEvent" && ReadInteger() == (authenticationProtection ? 27 : 18), "AuditIdentityPayloadInvalid");
            var fields = Enumerable.Range(0, authenticationProtection ? 27 : 18).Select(_ => ReadValue()).ToArray();
            AuditChainDatabase.Require(input.Position == input.Length && fields[0] == ordinal.ToString(CultureInfo.InvariantCulture) &&
                fields[4] == stationId && Guid.TryParseExact(fields[1], "D", out var eventId) && eventId != Guid.Empty &&
                Enum.TryParse<IdentityEventKind>(fields[2], out var kind) && Enum.IsDefined(kind) && fields[2] == kind.ToString() &&
                DateTimeOffset.TryParseExact(fields[3], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurred) &&
                occurred != default && fields[9] is { Length: > 0 and <= 128 }, "AuditIdentityPayloadInvalid");
            for (var index = 5; index <= 8; index++)
                AuditChainDatabase.Require(fields[index] is null || Guid.TryParseExact(fields[index], "D", out _), "AuditIdentityPayloadInvalid");
            AuditChainDatabase.Require(long.TryParse(fields[15], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) &&
                revision >= 0 && fields[15] == revision.ToString(CultureInfo.InvariantCulture), "AuditIdentityPayloadInvalid");
            for (var index = 16; index <= 17; index++)
                AuditChainDatabase.Require(int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var cost) &&
                    cost is >= 0 and <= PasswordHashBaseline.MaximumSupportedIterations, "AuditIdentityPayloadInvalid");
            if (authenticationProtection)
            {
                AuditChainDatabase.Require(fields[18] is { Length: > 0 and <= 128 } && fields[19] is { Length: > 0 and <= 128 } &&
                    IsHash(fields[20]) && (fields[21] is null || IsHash(fields[21])), "AuditAuthenticationPayloadInvalid");
                for (var index = 22; index <= 23; index++)
                    AuditChainDatabase.Require(int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture,
                        out var failures) && failures is >= 0 and <= AuthenticationPolicy.ReleaseFailureLimitCeiling, "AuditAuthenticationPayloadInvalid");
                AuditChainDatabase.Require((fields[24] is null || DateTimeOffset.TryParseExact(fields[24], "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _)) && (fields[25] is null || Guid.TryParseExact(fields[25], "D", out _)) &&
                    long.TryParse(fields[26], NumberStyles.None, CultureInfo.InvariantCulture, out var delay) &&
                    delay >= 0 && delay <= TimeSpan.FromHours(1).Ticks, "AuditAuthenticationPayloadInvalid");
            }
            return revision;
        }
        catch (EndOfStreamException) { throw new InvalidOperationException("AuditIdentityPayloadInvalid"); }
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
