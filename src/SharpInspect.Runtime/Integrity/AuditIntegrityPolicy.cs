using System.Globalization;
using System.Security.Cryptography;

namespace SharpInspect.Runtime.Integrity;

/// <summary>
/// The bounded, versioned policy that governs local audit-chain verification and
/// checkpoint production.  The record is immutable after construction and the
/// <see cref="Validate"/> method is the boundary at which an application accepts
/// a policy for use.
/// </summary>
public sealed record AuditIntegrityPolicy(string StationId, string Version, string SigningKeyName)
{
    public string KeyDirectory { get; init; } = GetDefaultKeyDirectory();
    public bool AllowInitialKeyCreation { get; init; }
    public int CheckpointEveryEntries { get; init; } = 100;
    public int MaximumVerificationEntries { get; init; } = 10_000;
    public int BackgroundVerificationEntries { get; init; } = 200;
    public TimeSpan VerificationInterval { get; init; } = TimeSpan.FromSeconds(15);
    public bool RequireExternalAnchor { get; init; }
    public string? ExternalAnchorRouteId { get; init; }
    public TimeSpan AnchorTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A stable SHA-256 uppercase hexadecimal hash of every policy field.</summary>
    public string ContentHash
    {
        get
        {
            Validate();
            return Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
                "audit-integrity-policy",
                StationId,
                Version,
                SigningKeyName,
                AllowInitialKeyCreation ? "true" : "false",
                CheckpointEveryEntries.ToString(CultureInfo.InvariantCulture),
                MaximumVerificationEntries.ToString(CultureInfo.InvariantCulture),
                BackgroundVerificationEntries.ToString(CultureInfo.InvariantCulture),
                VerificationInterval.Ticks.ToString(CultureInfo.InvariantCulture),
                RequireExternalAnchor ? "true" : "false",
                ExternalAnchorRouteId,
                AnchorTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
                GetAbsoluteKeyDirectory(),
                "DPAPI-LocalMachine",
                "ECDSA_P256",
                "PKCS8",
                "restricted-service-account-ACL",
                "SHA256-P1363",
                AuditCanonical.CanonicalizationVersion.ToString(CultureInfo.InvariantCulture),
                AuditCanonical.HashSchemeVersion.ToString(CultureInfo.InvariantCulture))));
        }
    }

    public void Validate()
    {
        ValidateIdentifier(StationId, nameof(StationId), required: true);
        ValidateIdentifier(Version, nameof(Version), required: true);
        ValidateIdentifier(SigningKeyName, nameof(SigningKeyName), required: true);
        _ = GetAbsoluteKeyDirectory();

        if (CheckpointEveryEntries is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(CheckpointEveryEntries));
        if (MaximumVerificationEntries is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(MaximumVerificationEntries));
        if (BackgroundVerificationEntries is < 1 ||
            BackgroundVerificationEntries > MaximumVerificationEntries)
            throw new ArgumentOutOfRangeException(nameof(BackgroundVerificationEntries));
        if (CheckpointEveryEntries > MaximumVerificationEntries - Math.Max(200, BackgroundVerificationEntries))
            throw new ArgumentOutOfRangeException(nameof(CheckpointEveryEntries));
        if (VerificationInterval < TimeSpan.FromSeconds(1) ||
            VerificationInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(VerificationInterval));
        if (AnchorTimeout < TimeSpan.FromMilliseconds(1) ||
            AnchorTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(AnchorTimeout));

        if (ExternalAnchorRouteId is not null)
            ValidateIdentifier(ExternalAnchorRouteId, nameof(ExternalAnchorRouteId), required: true);
        if (RequireExternalAnchor && string.IsNullOrWhiteSpace(ExternalAnchorRouteId))
            throw new ArgumentException("An external anchor route is required by this policy.",
                nameof(ExternalAnchorRouteId));
    }

    internal static void ValidateIdentifier(string? value, string parameterName, bool required)
    {
        if (value is null || (required && value.Length == 0) ||
            (!required && value.Length == 0) || value.Length > 128)
            throw new ArgumentException("The identifier is outside the allowed bounds.", parameterName);

        if (value.Trim() != value || value.Any(char.IsControl))
            throw new ArgumentException("The identifier contains unsupported characters.", parameterName);
    }

    internal string GetAbsoluteKeyDirectory()
    {
        if (string.IsNullOrWhiteSpace(KeyDirectory) || !Path.IsPathFullyQualified(KeyDirectory) ||
            KeyDirectory.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            KeyDirectory.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            KeyDirectory.StartsWith(@"\??\", StringComparison.Ordinal) ||
            KeyDirectory.StartsWith(@"\\", StringComparison.Ordinal) ||
            KeyDirectory.Length > 240)
            throw new ArgumentException("The key directory must be an explicit local path.", nameof(KeyDirectory));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(KeyDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The key directory is invalid.", nameof(KeyDirectory));
        }

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var normalized = string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0 || normalized.Length > 240)
            throw new ArgumentException("The key directory is outside the allowed bounds.", nameof(KeyDirectory));

        foreach (var part in normalized[root.Length..].Split(Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith(' ') || part.EndsWith('.') || part.Contains(':', StringComparison.Ordinal))
                throw new ArgumentException("The key directory contains an invalid path component.", nameof(KeyDirectory));
        }

        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string GetDefaultKeyDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "SharpInspect.AuditKeys");
    }
}
