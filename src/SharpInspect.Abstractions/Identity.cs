using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SharpInspect.Abstractions;

/// <summary>Display-only information. Claims do not confer an action permission.</summary>
public sealed record IdentityDisplayClaim(string Type, string Value);

public sealed class HumanIdentity
{
    public HumanIdentity(Guid principalId, string userName, string displayName,
        IEnumerable<IdentityDisplayClaim>? displayClaims = null)
    {
        if (principalId == Guid.Empty) throw new ArgumentException("An immutable principal identifier is required.", nameof(principalId));
        PrincipalId = principalId;
        UserName = userName;
        DisplayName = displayName;
        DisplayClaims = new ReadOnlyCollection<IdentityDisplayClaim>((displayClaims ?? Array.Empty<IdentityDisplayClaim>()).ToArray());
    }
    public Guid PrincipalId { get; }
    public string UserName { get; }
    public string DisplayName { get; }
    public IReadOnlyList<IdentityDisplayClaim> DisplayClaims { get; }
}

public sealed class PasswordSignInRequest
{
    public PasswordSignInRequest(string userName, string password) { UserName = userName; Password = password; }
    public string UserName { get; }
    [JsonIgnore] public string Password { get; }
    public override string ToString() => nameof(PasswordSignInRequest);
}

public sealed record AuthenticationResult(bool Succeeded, string ReasonCode, HumanIdentity? Identity = null);

/// <summary>Authenticates a person; Recipe, recovery, equipment and production authorization remain separate.</summary>
public interface IIdentityProvider
{
    ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default);
}

public sealed class BootstrapAdministratorRequest
{
    public BootstrapAdministratorRequest(string stationId, string bootstrapToken, string userName, string displayName, string password)
    { StationId = stationId; BootstrapToken = bootstrapToken; UserName = userName; DisplayName = displayName; Password = password; }
    public string StationId { get; }
    [JsonIgnore] public string BootstrapToken { get; }
    public string UserName { get; }
    public string DisplayName { get; }
    [JsonIgnore] public string Password { get; }
    public override string ToString() => nameof(BootstrapAdministratorRequest);
}

/// <summary>A transient delivery that can be read once. It has no serializable secret property.</summary>
public sealed class OneTimeSecret : IDisposable
{
    private char[]? _value;
    public OneTimeSecret(string value) => _value = value.ToCharArray();
    public string TakeForDisplay()
    {
        var value = Interlocked.Exchange(ref _value, null) ?? throw new InvalidOperationException("SecretAlreadyDelivered");
        try { return new string(value); }
        finally { Array.Clear(value, 0, value.Length); }
    }
    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null) Array.Clear(value, 0, value.Length);
    }
    public override string ToString() => "[redacted one-time delivery]";
}

public sealed record BootstrapTokenResult(bool Succeeded, string ReasonCode, Guid? TokenId = null,
    DateTimeOffset? ExpiresAtUtc = null, [property: JsonIgnore] OneTimeSecret? Token = null);

public sealed record BootstrapAdministratorResult(bool Succeeded, string ReasonCode, HumanIdentity? Identity = null,
    Guid? RecoveryKitId = null, [property: JsonIgnore] OneTimeSecret? RecoveryKit = null);

/// <summary>Identity prerequisites are only one subset of production admission.</summary>
public sealed record StationIdentityStatus(string StationId, bool BootstrapRequired, int UsableAdministratorCount,
    int ValidRecoveryCodeCount, string ReasonCode);

public interface ILocalAdministratorBootstrap
{
    /// <summary>Requires independently verified local Windows administrator and physical-console authority.</summary>
    ValueTask<BootstrapTokenResult> ProvisionBootstrapTokenAsync(CancellationToken cancellationToken = default);
    ValueTask<BootstrapAdministratorResult> CreateFirstAdministratorAsync(BootstrapAdministratorRequest request,
        CancellationToken cancellationToken = default);
    ValueTask<StationIdentityStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
