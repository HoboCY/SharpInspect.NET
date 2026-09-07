using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Identity;

// Confidential authoritative state is separate from the non-secret audit payload. V1-04
// contains one first administrator; future account management must extend this schema explicitly.
internal sealed class IdentityAuthorityState
{
    public int FormatVersion { get; set; } = 2;
    public long Revision { get; set; }
    public string StationId { get; set; } = "";
    public string InstallationKeyId { get; set; } = "";
    public string PolicyContentHash { get; set; } = "";
    public string LastIdentityAuditHash { get; set; } = "";
    public DateTimeOffset LastObservedUtc { get; set; }
    public LocalAdministratorState? Administrator { get; set; }
    public BootstrapSecretState? Bootstrap { get; set; }
    public Guid? RecoveryKitId { get; set; }
    public List<RecoveryCodeState> RecoveryCodes { get; set; } = new();
    public AuthenticationThrottleState StationThrottle { get; set; } = new();
    public AuthenticationThrottleState UnknownAccountThrottle { get; set; } = new();
    public string AttemptIdentifierKey { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public override string ToString() => "[confidential identity state]";
}

internal sealed class LocalAdministratorState
{
    public Guid PrincipalId { get; set; }
    public string UserName { get; set; } = "";
    public string UserNameKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Guid CredentialId { get; set; }
    public AuthenticationThrottleState Throttle { get; set; } = new();
    public DateTimeOffset? DisabledAtUtc { get; set; }
    public long CredentialRevision { get; set; }
    public bool Enabled { get; set; } = true;
    public PasswordVerifierState Password { get; set; } = new();
    public HumanIdentity ToIdentity() => new(PrincipalId, UserName, DisplayName,
        new[] { new IdentityDisplayClaim("local-role-display", "Administrator") });
    public override string ToString() => "[confidential account state]";
}

internal sealed class PasswordVerifierState
{
    public string Algorithm { get; set; } = "";
    public int FormatVersion { get; set; }
    public int ParameterVersion { get; set; }
    public int Cost { get; set; }
    public string Salt { get; set; } = "";
    public string Derived { get; set; } = "";
    public PasswordHashRecord ToRecord() => new(Algorithm, FormatVersion, ParameterVersion, Cost, Salt, Derived);
    public static PasswordVerifierState From(PasswordHashRecord record) => new()
    { Algorithm = record.Algorithm, FormatVersion = record.FormatVersion, ParameterVersion = record.ParameterVersion,
        Cost = record.Cost, Salt = record.SaltBase64, Derived = record.DerivedBase64 };
    public override string ToString() => "[confidential verifier]";
}

internal sealed class BootstrapSecretState
{
    public Guid TokenId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string Verifier { get; set; } = "";
    public string State { get; set; } = "Pending";
    public override string ToString() => "[confidential bootstrap state]";
}

internal sealed class RecoveryCodeState
{
    public Guid CodeId { get; set; }
    public string Verifier { get; set; } = "";
    public bool Consumed { get; set; }
    public bool Revoked { get; set; }
    public override string ToString() => "[confidential recovery state]";
}

internal sealed class AuthenticationThrottleState
{
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset NextAllowedAtUtc { get; set; }
    public long DelayTicks { get; set; }
}

internal static class IdentityStateProtection
{
    internal static string Sign(string encoded, string station, long revision, long auditSequence, IAuditSigningKey key)
    {
        var data = StateSignatureBytes(encoded, station, key.KeyId, revision, auditSequence);
        try { return Convert.ToBase64String(key.Sign(data)); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    internal static void VerifySignature(string encoded, string signature, string station, long revision,
        long auditSequence, IAuditSigningKey key)
    {
        var data = StateSignatureBytes(encoded, station, key.KeyId, revision, auditSequence);
        try
        {
            using var verifier = ECDsa.Create();
            var publicBytes = Convert.FromBase64String(key.PublicKeyBase64);
            verifier.ImportSubjectPublicKeyInfo(publicBytes, out var bytesRead);
            if (bytesRead != publicBytes.Length || signature.Length != 88 || !verifier.VerifyData(data,
                Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new InvalidOperationException("IdentityStateSignatureInvalid");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new InvalidOperationException("IdentityStateSignatureInvalid"); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static byte[] StateSignatureBytes(string encoded, string station, string keyId, long revision, long auditSequence) =>
        AuditCanonical.Encode("IdentityAuthorityStateSeal", station, keyId, revision.ToString(CultureInfo.InvariantCulture),
            auditSequence.ToString(CultureInfo.InvariantCulture), encoded);

    internal static string Protect(IdentityAuthorityState state)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("IdentityProtectionUnavailable");
        var clear = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            if (clear.Length > 32768) throw new InvalidOperationException("IdentityStateCapacityExceeded");
            return Convert.ToBase64String(ProtectedData.Protect(clear, Entropy(state.StationId, state.InstallationKeyId, state.Revision),
                DataProtectionScope.LocalMachine));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal static IdentityAuthorityState Unprotect(string encoded, string station, string keyId, long revision)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("IdentityProtectionUnavailable");
        byte[]? clear = null;
        try
        {
            if (encoded.Length > 60000) throw new InvalidOperationException("IdentityStateCapacityExceeded");
            clear = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy(station, keyId, revision), DataProtectionScope.LocalMachine);
            if (clear.Length > 32768) throw new InvalidOperationException("IdentityStateCapacityExceeded");
            var state = JsonSerializer.Deserialize<IdentityAuthorityState>(clear) ?? throw new InvalidOperationException("IdentityStateInvalid");
            if (state.FormatVersion != 2 || state.Revision != revision || state.StationId != station ||
                state.InstallationKeyId != keyId || state.RecoveryCodes is null || state.RecoveryCodes.Count > 32 ||
                state.StationThrottle is null || state.UnknownAccountThrottle is null || state.AttemptIdentifierKey.Length != 44 ||
                state.Administrator is { Throttle: null })
                throw new InvalidOperationException("IdentityStateBindingMismatch");
            return state;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        { throw new InvalidOperationException("IdentityStateInvalid"); }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    internal static string SecretVerifier(string purpose, string station, string installation, Guid recordId, string secret)
    {
        var data = AuditCanonical.Encode("IdentityOneTimeVerifier", purpose, station, installation, recordId.ToString("D"), secret);
        try { return Convert.ToBase64String(SHA256.HashData(data)); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    internal static bool Matches(string expected, string actual)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expected), Convert.FromBase64String(actual)); }
        catch (FormatException) { return false; }
    }

    private static byte[] Entropy(string station, string installation, long revision) => SHA256.HashData(
        Encoding.UTF8.GetBytes("SharpInspect.IdentityAuthority/v1/" + station + "/" + installation + "/" + revision.ToString(CultureInfo.InvariantCulture)));
}
