using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalIdentityService
{
    internal async ValueTask<bool> PersistSessionEventAsync(SessionAuditEvent fact, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<IdentityEventKind>(fact.Kind, out var kind) || kind is not (IdentityEventKind.SessionStarted or
            IdentityEventKind.SessionLocked or IdentityEventKind.SessionLoggedOut or IdentityEventKind.SessionSignInCancelled)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        try
        {
            while (_store.Integrity?.State == AuditIntegrityState.Verifying)
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
            var result = await _store.UpdateIdentityAsync(state =>
            {
                ObserveTime(state);
                if (kind == IdentityEventKind.SessionStarted && (state.Administrator is not { Enabled: true } administrator ||
                    administrator.PrincipalId != fact.PrincipalId))
                    return Update("SessionCredentialUnavailable", Event(state, IdentityEventKind.SessionSignInCancelled,
                        "SessionCredentialUnavailable", fact.PrincipalId) with { SessionId = fact.SessionId });
                return Update("SessionEvidencePersisted", Event(state, kind, fact.ReasonCode,
                    fact.PrincipalId) with { SessionId = fact.SessionId });
            }, timeout.Token).ConfigureAwait(false);
            return result.Committed && result.Result is "SessionEvidencePersisted";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    public ValueTask<AuthenticationResult> AuthenticateAsync(PasswordSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BoundedAsync(async token =>
        {
            string key;
            string password;
            var inputValid = true;
            try
            {
                (_, key) = ValidateUserName(request.UserName, creation: false);
                if (request.Password is null || request.Password.Length > LocalPasswordPolicy.MaximumRawPasswordCodeUnits)
                    throw new ArgumentException("PasswordInvalid");
                password = LocalPasswordPolicy.ValidateNormalizedPassword(request.Password.Normalize(NormalizationForm.FormC));
            }
            catch (ArgumentException) { key = "InvalidInput"; password = "Invalid credential input"; inputValid = false; }
            var before = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var account = before.Administrator is { } candidate && candidate.UserNameKey == key ? candidate : null;
            var observedBefore = _utcNow();
            var now = observedBefore > before.LastObservedUtc ? observedBefore : before.LastObservedUtc;
            var eligible = now >= before.StationThrottle.NextAllowedAtUtc &&
                now >= (account?.Throttle ?? before.UnknownAccountThrottle).NextAllowedAtUtc;
            var verifier = account?.Password.ToRecord();
            var fullVerification = inputValid && eligible && account is { Enabled: true };
            var verified = await Task.Run(() =>
            {
                // The station's sole credential defines a common work profile. Unknown,
                // disabled and cooling attempts do the same old-cost and padding passes.
                var work = fullVerification ? verifier! : DummyVerifier(before.Administrator?.Password);
                var result = VerifyPasswordWork(password, work);
                if (work.Cost < _options.Baseline.TargetIterations)
                    _ = VerifyPasswordWork(password, DummyVerifier(null));
                return fullVerification && result;
            }, token).ConfigureAwait(false);
            var replacement = verified && _options.PasswordHasher.NeedsRehash(verifier!)
                ? await Task.Run(() => UpgradeVerifier(password, verifier!), token).ConfigureAwait(false) : null;
            var committed = await _store.UpdateIdentityAsync(state =>
            {
                var observed = ObserveTime(state);
                var current = state.Administrator is { } administrator && administrator.UserNameKey == key ? administrator : null;
                var throttle = current?.Throttle ?? state.UnknownAccountThrottle;
                var identifier = ProtectAttemptIdentifier(state, key);
                if (!eligible || observed < state.StationThrottle.NextAllowedAtUtc || observed < throttle.NextAllowedAtUtc)
                    return Rejected(state, current, throttle, identifier, IdentityEventKind.AuthenticationThrottled);
                if (verified && current is { Enabled: true } && (current.CredentialId != account!.CredentialId ||
                    current.CredentialRevision != account.CredentialRevision))
                    // A concurrent successful upgrade invalidates the earlier proof, but
                    // it is not a wrong-password failure and cannot disable the credential.
                    return Rejected(state, current, throttle, identifier, IdentityEventKind.AuthenticationRejected);
                if (!verified || current is null || !current.Enabled)
                {
                    var events = new List<IdentityAuditEvent>();
                    RecordFailure(state.StationThrottle, observed, _options.AuthenticationPolicy.StationFailureLimit, station: true);
                    if (current is null || current.Enabled)
                        RecordFailure(throttle, observed, _options.AuthenticationPolicy.AccountFailureLimit, station: false);
                    if (current is { Enabled: true } && throttle.ConsecutiveFailures >= _options.AuthenticationPolicy.AccountFailureLimit)
                    {
                        current.Enabled = false;
                        current.DisabledAtUtc = observed;
                        events.Add(AuthenticationEvent(state, current, throttle, identifier, IdentityEventKind.CredentialDisabled));
                    }
                    events.Add(AuthenticationEvent(state, current, throttle, identifier, IdentityEventKind.AuthenticationRejected));
                    return new IdentityUpdate(new AuthenticationResult(false, "AuthenticationRejected"), events);
                }
                Reset(state.StationThrottle);
                Reset(throttle);
                var successEvents = new List<IdentityAuditEvent>();
                if (replacement is not null)
                {
                    current.Password = PasswordVerifierState.From(replacement);
                    current.CredentialRevision = checked(current.CredentialRevision + 1);
                    successEvents.Add(Event(state, IdentityEventKind.PasswordVerifierUpgraded, "PasswordVerifierUpgraded",
                        current.PrincipalId, current.CredentialId));
                }
                successEvents.Add(AuthenticationEvent(state, current, throttle, identifier, IdentityEventKind.AuthenticationSucceeded));
                return new IdentityUpdate(new AuthenticationResult(true, "Authenticated", current.ToIdentity()), successEvents);
            }, token).ConfigureAwait(false);
            return Committed<AuthenticationResult>(committed) ?? new(false, committed.ReasonCode);
        }, reason => new AuthenticationResult(false, reason), cancellationToken);
    }

    private PasswordHashRecord DummyVerifier(PasswordVerifierState? current)
    {
        var saltLength = current is null ? _options.Baseline.SaltBytes : Convert.FromBase64String(current.Salt).Length;
        var outputLength = current is null ? _options.Baseline.DerivedBytes : Convert.FromBase64String(current.Derived).Length;
        return new(Pbkdf2PasswordHasher.Algorithm, Pbkdf2PasswordHasher.FormatVersion, _options.Baseline.ParameterVersion,
            current?.Cost ?? _options.Baseline.TargetIterations, Convert.ToBase64String(RandomNumberGenerator.GetBytes(saltLength)),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(outputLength)));
    }

    private bool VerifyPasswordWork(string password, PasswordHashRecord record)
    {
        _verificationObserver?.Invoke(new(record.Cost, Convert.FromBase64String(record.SaltBase64).Length,
            Convert.FromBase64String(record.DerivedBase64).Length));
        return _options.PasswordHasher.Verify(password, record);
    }

    private void RecordFailure(AuthenticationThrottleState throttle, DateTimeOffset now, int limit, bool station)
    {
        throttle.ConsecutiveFailures = Math.Min(AuthenticationPolicy.ReleaseFailureLimitCeiling, throttle.ConsecutiveFailures + 1);
        var delay = station && throttle.ConsecutiveFailures >= limit ? _options.AuthenticationPolicy.MaximumDelay :
            _options.AuthenticationPolicy.DelayForFailures(throttle.ConsecutiveFailures);
        throttle.DelayTicks = delay.Ticks;
        throttle.NextAllowedAtUtc = now + delay;
    }

    private static void Reset(AuthenticationThrottleState throttle)
    { throttle.ConsecutiveFailures = 0; throttle.DelayTicks = 0; throttle.NextAllowedAtUtc = default; }

    private IdentityUpdate Rejected(IdentityAuthorityState state, LocalAdministratorState? account, AuthenticationThrottleState throttle,
        string identifier, IdentityEventKind kind) => new(new AuthenticationResult(false, "AuthenticationRejected"),
            new[] { AuthenticationEvent(state, account, throttle, identifier, kind) });

    private IdentityAuditEvent AuthenticationEvent(IdentityAuthorityState state, LocalAdministratorState? account,
        AuthenticationThrottleState throttle, string identifier, IdentityEventKind kind) =>
        Event(state, kind, kind == IdentityEventKind.AuthenticationSucceeded ? "Authenticated" :
            kind == IdentityEventKind.CredentialDisabled ? "CredentialFailureLimitReached" : "AuthenticationRejected",
            account?.PrincipalId, account?.CredentialId) with
        { ProtectedAttemptIdentifier = identifier, AccountFailures = throttle.ConsecutiveFailures,
            StationFailures = state.StationThrottle.ConsecutiveFailures, DelayTicks = Math.Max(throttle.DelayTicks, state.StationThrottle.DelayTicks),
            NextAllowedAtUtc = throttle.NextAllowedAtUtc > state.StationThrottle.NextAllowedAtUtc ? throttle.NextAllowedAtUtc : state.StationThrottle.NextAllowedAtUtc };

    private static string ProtectAttemptIdentifier(IdentityAuthorityState state, string normalizedIdentifier)
    {
        var key = Convert.FromBase64String(state.AttemptIdentifierKey);
        var bytes = Encoding.UTF8.GetBytes("SharpInspect.AttemptIdentifier/v1/" + normalizedIdentifier);
        try { using var hmac = new HMACSHA256(key); return Convert.ToHexString(hmac.ComputeHash(bytes)); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed record PasswordVerificationWork(int Cost, int SaltBytes, int OutputBytes);
