using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// Versioned project policy for local authentication throttling and interactive sessions.
/// The defaults are framework development values and are not presented as NIST defaults.
/// </summary>
public sealed record AuthenticationPolicy(string Id, string Version)
{
    public const int ReleaseFailureLimitCeiling = 100;

    public int AccountFailureLimit { get; init; } = 10;
    public int StationFailureLimit { get; init; } = 50;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan StepUpFreshness { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Explicit development policy used by fixtures and local development.</summary>
    public static AuthenticationPolicy Development { get; } = new(
        "development",
        "development-2026-09");

    public static AuthenticationPolicy Current => Development;

    /// <summary>A stable uppercase SHA-256 hash over every policy field.</summary>
    public string ContentHash
    {
        get
        {
            Validate();
            return Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
                "authentication-policy",
                Id,
                Version,
                AccountFailureLimit.ToString(CultureInfo.InvariantCulture),
                StationFailureLimit.ToString(CultureInfo.InvariantCulture),
                InitialDelay.Ticks.ToString(CultureInfo.InvariantCulture),
                MaximumDelay.Ticks.ToString(CultureInfo.InvariantCulture),
                SessionIdleTimeout.Ticks.ToString(CultureInfo.InvariantCulture),
                StepUpFreshness.Ticks.ToString(CultureInfo.InvariantCulture))));
        }
    }

    public void Validate()
    {
        AuditIntegrityPolicy.ValidateIdentifier(Id, nameof(Id), required: true);
        AuditIntegrityPolicy.ValidateIdentifier(Version, nameof(Version), required: true);

        if (AccountFailureLimit is < 1 or > ReleaseFailureLimitCeiling)
            throw new ArgumentOutOfRangeException(nameof(AccountFailureLimit));
        if (StationFailureLimit is < 1 or > ReleaseFailureLimitCeiling)
            throw new ArgumentOutOfRangeException(nameof(StationFailureLimit));
        if (InitialDelay < TimeSpan.FromSeconds(1) || InitialDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (MaximumDelay < InitialDelay || MaximumDelay > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(MaximumDelay));
        if (SessionIdleTimeout < TimeSpan.FromMinutes(1) || SessionIdleTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(SessionIdleTimeout));
        if (StepUpFreshness < TimeSpan.FromSeconds(1) || StepUpFreshness > TimeSpan.FromMinutes(15) ||
            StepUpFreshness > SessionIdleTimeout)
            throw new ArgumentOutOfRangeException(nameof(StepUpFreshness));
    }

    /// <summary>
    /// Returns the bounded delay for a consecutive failure count. Failure one
    /// receives the initial delay and later failures double it until the cap.
    /// </summary>
    public TimeSpan DelayForFailures(int failureCount)
    {
        Validate();
        if (failureCount < 0)
            throw new ArgumentOutOfRangeException(nameof(failureCount));
        if (failureCount == 0)
            return TimeSpan.Zero;

        var delayTicks = InitialDelay.Ticks;
        var maximumTicks = MaximumDelay.Ticks;
        for (var failure = 1; failure < failureCount && delayTicks < maximumTicks; failure++)
        {
            if (delayTicks > maximumTicks / 2)
                return MaximumDelay;
            delayTicks *= 2;
        }

        return delayTicks >= maximumTicks ? MaximumDelay : TimeSpan.FromTicks(delayTicks);
    }
}
