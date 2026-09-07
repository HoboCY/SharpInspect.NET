using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Identity;

/// <summary>Explicit offline credential configuration. It never grants production qualification.</summary>
public sealed class LocalIdentityOptions
{
    public LocalIdentityOptions(string stationId, LocalPasswordPolicy passwordPolicy, IPasswordHasher passwordHasher,
        AuthenticationPolicy authenticationPolicy, AuthorizationPolicy authorizationPolicy)
    { StationId = stationId; PasswordPolicy = passwordPolicy; PasswordHasher = passwordHasher;
        AuthenticationPolicy = authenticationPolicy; AuthorizationPolicy = authorizationPolicy; }
    public string StationId { get; }
    public LocalPasswordPolicy PasswordPolicy { get; }
    public IPasswordHasher PasswordHasher { get; }
    public AuthenticationPolicy AuthenticationPolicy { get; }
    public AuthorizationPolicy AuthorizationPolicy { get; }
    public string HashBaselineVersion => Baseline.Version;
    internal PasswordHashBaseline Baseline => PasswordHasher is Pbkdf2PasswordHasher hasher
        ? hasher.Baseline : throw new ArgumentException("IdentityHasherUnsupported");
    public TimeSpan BootstrapTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public int RecoveryCodeCount { get; init; } = 8;
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate(AuditIntegrityPolicy? auditPolicy)
    {
        AuditIntegrityPolicy.ValidateIdentifier(StationId, nameof(StationId), true);
        ArgumentNullException.ThrowIfNull(PasswordPolicy);
        ArgumentNullException.ThrowIfNull(PasswordHasher);
        Baseline.Validate();
        ArgumentNullException.ThrowIfNull(AuthenticationPolicy);
        AuthenticationPolicy.Validate();
        ArgumentNullException.ThrowIfNull(AuthorizationPolicy);
        AuthorizationPolicy.Validate();
        var initialPermissions = AuthorizationPolicy.GetPermissions(HumanRoleBundle.Administrator);
        if (!new[] { Permission.ManageAccounts, Permission.ManagePermissions, Permission.UnlockCredential, Permission.RebindCredential }
                .All(initialPermissions.Contains)) throw new ArgumentException("BootstrapAdministratorPermissionsRequired");
        PasswordPolicy.Validate();
        AuditIntegrityPolicy.ValidateIdentifier(HashBaselineVersion, nameof(HashBaselineVersion), true);
        if (auditPolicy is null || auditPolicy.StationId != StationId)
            throw new ArgumentException("IdentityRequiresMatchingAuditStation");
        if (BootstrapTokenLifetime < TimeSpan.FromMinutes(1) || BootstrapTokenLifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(BootstrapTokenLifetime));
        if (RecoveryCodeCount is < 2 or > 32) throw new ArgumentOutOfRangeException(nameof(RecoveryCodeCount));
        if (OperationTimeout < TimeSpan.FromSeconds(1) || OperationTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(OperationTimeout));
    }

    internal string PolicyContentHash => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(AuditCanonical.Encode(
        "LocalIdentityPolicy", StationId, PasswordPolicy.Version, PasswordPolicy.MinimumCodePoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordPolicy.MaximumCodePoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordPolicy.MaximumRawCodeUnits.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordPolicy.Blocklist!.Id, PasswordPolicy.Blocklist.Version, PasswordPolicy.Blocklist.ContentHash,
        Pbkdf2PasswordHasher.Algorithm, Pbkdf2PasswordHasher.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordHashBaseline.CurrentParameterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordHashBaseline.SecurityFloorIterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordHashBaseline.MinimumSaltBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PasswordHashBaseline.MinimumDerivedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), AuthenticationPolicy.ContentHash,
        AuthorizationPolicy.ContentHash)));
}
