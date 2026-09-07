using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalIdentityService : IIdentityProvider, ILocalAdministratorBootstrap
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _requests;
    private readonly SqliteCommandStore _store;
    private readonly LocalIdentityOptions _options;
    private readonly IPhysicalConsoleAuthority _console;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<PasswordVerificationWork>? _verificationObserver;
    private readonly Lazy<Task<PasswordHashRecord>> _dummy;

    internal LocalIdentityService(SqliteCommandStore store, LocalIdentityOptions options,
        IPhysicalConsoleAuthority? console = null, Func<DateTimeOffset>? utcNow = null,
        Action<PasswordVerificationWork>? verificationObserver = null)
    {
        _store = store; _options = options; _console = console ?? new PhysicalConsoleAuthority();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _verificationObserver = verificationObserver;
        _dummy = new(() => Task.Run(() => _options.PasswordHasher.Hash(NewSecret())));
    }

    public ValueTask<BootstrapTokenResult> ProvisionBootstrapTokenAsync(CancellationToken cancellationToken = default) =>
        BoundedAsync(async token =>
        {
            var id = Guid.NewGuid();
            var secret = NewSecret();
            var committed = await _store.UpdateIdentityAsync(state =>
            {
                var authority = _console.Observe();
                var now = ObserveTime(state);
                var reason = !authority.PhysicalConsole ? "PhysicalConsoleRequired" : !authority.WindowsAdministrator ?
                    "WindowsAdministratorRequired" : state.Administrator is not null ? "AdministratorAlreadyEstablished" :
                    state.Bootstrap is { State: "Pending" } pending && now < pending.ExpiresAtUtc ? "BootstrapTokenAlreadyIssued" : null;
                if (reason is not null) return Update(new BootstrapTokenResult(false, reason),
                    Event(state, IdentityEventKind.BootstrapRejected, reason, windowsSid: authority.WindowsSid));
                var expires = now + _options.BootstrapTokenLifetime;
                state.Bootstrap = new BootstrapSecretState { TokenId = id, ExpiresAtUtc = expires,
                    Verifier = IdentityStateProtection.SecretVerifier("Bootstrap", state.StationId, state.InstallationKeyId, id, secret) };
                return Update(new BootstrapTokenResult(true, "BootstrapTokenIssued", id, expires),
                    Event(state, IdentityEventKind.BootstrapIssued, "BootstrapTokenIssued", tokenId: id, windowsSid: authority.WindowsSid));
            }, token).ConfigureAwait(false);
            var result = Committed<BootstrapTokenResult>(committed) ?? new(false, committed.ReasonCode);
            return result.Succeeded ? result with { Token = new OneTimeSecret(id.ToString("N") + "." + secret) } : result;
        }, reason => new BootstrapTokenResult(false, reason), cancellationToken);

    public ValueTask<BootstrapAdministratorResult> CreateFirstAdministratorAsync(BootstrapAdministratorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BoundedAsync(async token =>
        {
            string? password = null;
            string? rejection = null;
            string userName = "", userNameKey = "", displayName = "";
            try
            {
                (userName, userNameKey) = ValidateUserName(request.UserName, creation: true);
                displayName = ValidateDisplayName(request.DisplayName);
                password = _options.PasswordPolicy.NormalizeAndValidate(request.Password, userName, "SharpInspect.NET", _options.StationId);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { rejection = CreationRejection(ex); }
            var hash = password is null ? null : await Task.Run(() => _options.PasswordHasher.Hash(password), token).ConfigureAwait(false);
            var parsed = ParseCode(request.BootstrapToken);
            var principalId = Guid.NewGuid();
            var credentialId = Guid.NewGuid();
            var kitId = Guid.NewGuid();
            var codes = Enumerable.Range(0, _options.RecoveryCodeCount).Select(_ => (Id: Guid.NewGuid(), Secret: NewSecret())).ToArray();
            var result = await _store.UpdateIdentityAsync(state =>
            {
                var authority = _console.Observe();
                var now = ObserveTime(state);
                var bootstrap = state.Bootstrap;
                var reason = !authority.PhysicalConsole ? "PhysicalConsoleRequired" : request.StationId != state.StationId ?
                    "BootstrapStationMismatch" : state.Administrator is not null ? "AdministratorAlreadyEstablished" :
                    bootstrap is null || bootstrap.State != "Pending" || parsed is null || parsed.Value.Id != bootstrap.TokenId ?
                    "BootstrapTokenInvalid" : now >= bootstrap.ExpiresAtUtc ? "BootstrapTokenExpired" :
                    !IdentityStateProtection.Matches(bootstrap.Verifier, IdentityStateProtection.SecretVerifier("Bootstrap",
                        state.StationId, state.InstallationKeyId, parsed!.Value.Id, parsed.Value.Secret)) ? "BootstrapTokenInvalid" : rejection;
                if (reason is not null)
                {
                    if (reason == "BootstrapTokenExpired") bootstrap!.State = "Expired";
                    return Update(new BootstrapAdministratorResult(false, reason), Event(state,
                        reason == "BootstrapTokenExpired" ? IdentityEventKind.BootstrapExpired : IdentityEventKind.BootstrapRejected,
                        reason, tokenId: bootstrap?.TokenId, windowsSid: authority.WindowsSid));
                }
                if (hash is null) throw new InvalidOperationException("IdentityVerifierMissing");
                state.Administrator = new LocalAdministratorState { PrincipalId = principalId, UserName = userName,
                    UserNameKey = userNameKey, DisplayName = displayName, CredentialId = credentialId,
                    CredentialRevision = 1, Password = PasswordVerifierState.From(hash) };
                bootstrap!.State = "Consumed";
                state.RecoveryKitId = kitId;
                state.RecoveryCodes = codes.Select(code => new RecoveryCodeState { CodeId = code.Id,
                    Verifier = IdentityStateProtection.SecretVerifier("Recovery", state.StationId, state.InstallationKeyId, code.Id, code.Secret) }).ToList();
                return new IdentityUpdate(new BootstrapAdministratorResult(true, "AdministratorCreated", state.Administrator.ToIdentity(), kitId),
                    new[] { Event(state, IdentityEventKind.AdministratorCreated, "BootstrapConsumedAndAdministratorCreated", principalId, credentialId,
                        bootstrap.TokenId, kitId, authority.WindowsSid), Event(state, IdentityEventKind.RecoveryKitIssued, "InitialRecoveryKitIssued",
                        principalId, credentialId, recoveryKitId: kitId) });
            }, token).ConfigureAwait(false);
            var value = Committed<BootstrapAdministratorResult>(result) ?? new(false, result.ReasonCode);
            if (!value.Succeeded) return value;
            var delivery = "Station: " + _options.StationId + Environment.NewLine + "Recovery Kit: " + kitId.ToString("D") +
                Environment.NewLine + string.Join(Environment.NewLine, codes.Select(code => code.Id.ToString("N") + "." + code.Secret));
            return value with { RecoveryKit = new OneTimeSecret(delivery) };
        }, reason => new BootstrapAdministratorResult(false, reason), cancellationToken);
    }

    public ValueTask<StationIdentityStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        BoundedAsync(async token =>
        {
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var administrators = state.Administrator is { Enabled: true } ? 1 : 0;
            var recovery = state.RecoveryCodes.Count(code => !code.Consumed && !code.Revoked);
            return new StationIdentityStatus(state.StationId, state.Administrator is null, administrators, recovery,
                administrators > 0 && recovery > 0 ? "IdentityPrerequisitesPresent" : "IdentityBootstrapRequired");
        }, reason => new StationIdentityStatus(_options.StationId, false, 0, 0, reason), cancellationToken);

    private async ValueTask<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> operation, Func<string, T> failure, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _requests) > 16) { Interlocked.Decrement(ref _requests); return failure("IdentityCapacityExceeded"); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller);
        lifetime.CancelAfter(_options.OperationTimeout);
        var entered = false;
        try
        {
            await Slots.WaitAsync(lifetime.Token).ConfigureAwait(false);
            entered = true;
            return await operation(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return failure("IdentityOperationDeadlineExceeded"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return failure("IdentityUnavailable"); }
        finally { if (entered) Slots.Release(); Interlocked.Decrement(ref _requests); }
    }

    private DateTimeOffset ObserveTime(IdentityAuthorityState state)
    { var now = _utcNow(); if (now > state.LastObservedUtc) state.LastObservedUtc = now; return state.LastObservedUtc; }

    private IdentityAuditEvent Event(IdentityAuthorityState state, IdentityEventKind kind, string reason, Guid? principalId = null,
        Guid? credentialId = null, Guid? tokenId = null, Guid? recoveryKitId = null, string? windowsSid = null) =>
        new(Guid.NewGuid(), kind, state.LastObservedUtc, state.StationId, principalId, credentialId, tokenId, recoveryKitId, reason,
            windowsSid, _options.PasswordPolicy.Version, _options.PasswordPolicy.Blocklist!.Id,
            _options.PasswordPolicy.Blocklist.Version, _options.HashBaselineVersion, HashTargetCost: _options.Baseline.TargetIterations,
            RecordCost: state.Administrator?.Password.Cost ?? 0);

    private PasswordHashRecord UpgradeVerifier(string password, PasswordHashRecord previous) => new Pbkdf2PasswordHasher(
        _options.Baseline with { TargetIterations = Math.Max(previous.Cost, _options.Baseline.TargetIterations),
            SaltBytes = Math.Max(Convert.FromBase64String(previous.SaltBase64).Length, _options.Baseline.SaltBytes),
            DerivedBytes = Math.Max(Convert.FromBase64String(previous.DerivedBase64).Length, _options.Baseline.DerivedBytes) }).Hash(password);

    private static IdentityUpdate Update(object result, IdentityAuditEvent fact) => new(result, new[] { fact });
    private static T? Committed<T>(IdentityWriteResult result) where T : class => result.Committed ? result.Result as T : null;
    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (Guid Id, string Secret)? ParseCode(string? value)
    {
        if (value is null || value.Length != 76 || value[32] != '.' || !Guid.TryParseExact(value[..32], "N", out var id)) return null;
        var secret = value[33..];
        if (secret.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))) return null;
        return (id, secret);
    }

    private static (string Name, string Key) ValidateUserName(string value, bool creation)
    {
        if (value is null || value.Length is < 3 or > 64 || value.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("IndividualUserNameRequired");
        var name = value.Normalize(NormalizationForm.FormC);
        var key = name.ToUpperInvariant();
        if (creation && new[] { "ADMIN", "ADMINISTRATOR", "OPERATOR", "TECHNICIAN", "SHIFT", "管理员", "操作员", "技术员", "班组" }.Contains(key, StringComparer.Ordinal))
            throw new ArgumentException("IndividualUserNameRequired");
        return (name, key);
    }

    private static string ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) throw new ArgumentException("IndividualDisplayNameRequired");
        return value.Normalize(NormalizationForm.FormC);
    }

    private static string CreationRejection(Exception exception)
    {
        foreach (var code in new[] { "IndividualUserNameRequired", "IndividualDisplayNameRequired", "PasswordBlocklisted",
            "PasswordRawLengthExceeded", "PasswordInvalidUnicode", "PasswordCodePointLengthInvalid" })
            if (exception.Message.StartsWith(code, StringComparison.Ordinal)) return code;
        return "CredentialPolicyRejected";
    }
}
