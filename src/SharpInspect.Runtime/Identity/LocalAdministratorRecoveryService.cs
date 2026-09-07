using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>Local recovery authority. All operational and identity decisions are rechecked
/// inside the identity transaction. Recovery never creates an interactive session.</summary>
internal sealed class LocalAdministratorRecoveryService : ILocalAdministratorRecovery
{
    private const string Recover = "RecoverAdministrator";
    private const string Rotate = "RotateRecoveryKit";
    private const string Confirm = "ConfirmRecoveryKitCustody";
    private static readonly SemaphoreSlim HashSlots = new(2, 2);
    private static int _requests;
    private readonly SqliteCommandStore _store;
    private readonly LocalIdentityOptions _options;
    private readonly LocalIdentityService _identity;
    private readonly InteractiveSessionService? _sessions;
    private readonly IAdministratorRecoveryRuntimeGate _runtime;
    private readonly IPhysicalConsoleAuthority _console;
    private readonly Func<DateTimeOffset> _utcNow;

    internal LocalAdministratorRecoveryService(SqliteCommandStore store, LocalIdentityOptions options,
        LocalIdentityService identity, IInteractiveSessionService? sessions,
        IAdministratorRecoveryRuntimeGate runtime, IPhysicalConsoleAuthority? console = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store; _options = options; _identity = identity;
        _sessions = sessions as InteractiveSessionService; _runtime = runtime;
        _console = console ?? new PhysicalConsoleAuthority(); _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    // Development tests can pause before final checks; this is never a public host capability.
    internal Action? BeforeTransactionChecks { get; set; }

    public ValueTask<AdministratorRecoveryStatus> GetRecoveryStatusAsync(CancellationToken cancellationToken = default) =>
        BoundedAsync(async token =>
        {
            var state = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var administrators = state.EnumerateAccounts().Count(IdentityAuthorityState.IsUsableAdministrator);
            var codes = state.KitState == RecoveryKitState.Available
                ? state.RecoveryCodes.Count(code => !code.Consumed && !code.Revoked) : 0;
            var initialized = state.Bootstrap is { State: "Consumed" };
            var reason = _sessions is null ? "RecoverySessionAuthorityUnavailable" :
                !_console.Observe().PhysicalConsole ? "PhysicalConsoleRequired" : _runtime.GetBlocker() ??
                (!initialized ? "BootstrapRequired" :
                state.KitState == RecoveryKitState.RotationRequired ? "RecoveryKitRotationRequired" :
                state.KitState == RecoveryKitState.CustodyConfirmationRequired ? "RecoveryKitCustodyConfirmationRequired" :
                administrators > 0 ? "UsableAdministratorPresent" : codes == 0 ? "GovernedRestoreOrReinitializationRequired" :
                _sessions.Current.State != InteractiveSessionState.Unauthenticated ? "RecoverySessionConflict" : "RecoveryAvailable");
            return new AdministratorRecoveryStatus(state.StationId, reason == "RecoveryAvailable", reason,
                state.KitState, state.RecoveryKitId, state.RecoveredPrincipalId, administrators, codes,
                initialized && administrators > 0 && codes > 0 && state.KitState == RecoveryKitState.Available);
        }, reason => new(_options.StationId, false, reason, RecoveryKitState.Unavailable, null, null, 0, 0, false),
            cancellationToken);

    public ValueTask<AdministratorRecoveryResult> RecoverAdministratorAsync(RecoverAdministratorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(request.OperationId, Recover, async token =>
        {
            CheckStation(request.StationId);
            CheckOperationalPreflight();
            var parsed = LocalIdentityService.ParseCode(request.RecoveryCode);
            RejectUnless(parsed is not null, "RecoveryCodeInvalid");
            var initial = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            CheckRecoverable(initial, parsed!.Value);
            string name, key, display, password;
            try
            {
                (name, key) = LocalIdentityService.ValidateUserName(request.UserName, creation: true);
                display = LocalIdentityService.ValidateDisplayName(request.DisplayName);
                password = _options.PasswordPolicy.NormalizeAndValidate(request.NewPassword, name, "SharpInspect.NET", _options.StationId);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { throw new RecoveryRejectedException(LocalIdentityService.CreationRejection(exception)); }
            var hash = await DeriveAsync(password, token).ConfigureAwait(false);
            return await CommitAsync(request.OperationId, Recover, null, null, state =>
            {
                CheckRecoverable(state, parsed.Value);
                var account = state.EnumerateAccounts().SingleOrDefault(item => item.UserNameKey == key);
                RejectUnless(account is null || account.DisplayName == display, "DisplayNameMismatch");
                RejectUnless(account is not null || state.EnumerateAccounts().Count() < AuthorizationPolicy.MaxHumanAccounts,
                    "HumanAccountCapacityExceeded");
                if (account is null)
                {
                    account = new LocalAdministratorState { PrincipalId = Guid.NewGuid(), UserName = name, UserNameKey = key,
                        DisplayName = display, CredentialRevision = 1, AuthorizationRevision = 1 };
                    state.AdditionalAccounts.Add(account);
                }
                else
                {
                    account.CredentialRevision = checked(account.CredentialRevision + 1);
                    account.AuthorizationRevision = checked(account.AuthorizationRevision + 1);
                }
                account.CredentialId = Guid.NewGuid(); account.Password = PasswordVerifierState.From(hash);
                account.Enabled = true; account.DisabledAtUtc = null; account.Throttle = new();
                account.RoleBundle = HumanRoleBundle.Administrator;
                account.Permissions = _options.AuthorizationPolicy.GetPermissions(HumanRoleBundle.Administrator).ToList();
                foreach (var code in state.RecoveryCodes)
                {
                    if (code.CodeId == parsed.Value.Id) code.Consumed = true;
                    code.Revoked = true;
                }
                state.KitState = RecoveryKitState.RotationRequired;
                state.RecoveredPrincipalId = account.PrincipalId; state.RecoveryOwnerPrincipalId = account.PrincipalId;
                state.StationThrottle = new();
                var result = new AdministratorRecoveryResult(true, "AdministratorRecovered", request.OperationId,
                    AuditPersistence.Persisted, account.ToIdentity());
                return Successful(state, request.OperationId, Recover, result, account, null,
                    parsed.Value.Id, state.RecoveryKitId, state.RecoveryKitId);
            }, RecoveryFailure(request.OperationId), token).ConfigureAwait(false);
        }, RecoveryFailure(request.OperationId), cancellationToken);
    }

    public ValueTask<RecoveryKitRotationResult> RotateRecoveryKitAsync(RotateRecoveryKitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(request.OperationId, Rotate, async token =>
        {
            CheckStation(request.StationId); CheckOperationalPreflight();
            var actor = ActualSession(request.Invocation);
            var before = await _store.ReadIdentityAsync(token).ConfigureAwait(false);
            var beforeAccount = CheckKitOwner(before, actor.PrincipalId);
            var credentialId = beforeAccount.CredentialId;
            var authorizationRevision = beforeAccount.AuthorizationRevision;
            var authentication = await _identity.AuthenticateAsync(new(beforeAccount.UserName, request.Password), token).ConfigureAwait(false);
            RejectUnless(authentication.Succeeded && authentication.Identity?.PrincipalId == actor.PrincipalId,
                "ReauthenticationRejected");
            var authenticatedAt = Stopwatch.GetTimestamp();
            token.ThrowIfCancellationRequested();
            var kitId = Guid.NewGuid();
            var codes = Enumerable.Range(0, _options.RecoveryCodeCount)
                .Select(_ => (Id: Guid.NewGuid(), Secret: LocalIdentityService.NewSecret())).ToArray();
            var result = await CommitAsync(request.OperationId, Rotate, actor.PrincipalId, actor.SessionId, state =>
            {
                var account = CheckKitOwner(state, actor.PrincipalId);
                RejectUnless(account.CredentialId == credentialId && account.AuthorizationRevision == authorizationRevision,
                    "RecoveryIdentityChanged");
                var elapsed = (Stopwatch.GetTimestamp() - authenticatedAt) / (double)Stopwatch.Frequency;
                RejectUnless(elapsed >= 0 && elapsed < _options.AuthenticationPolicy.StepUpFreshness.TotalSeconds,
                    "RecoveryReauthenticationExpired");
                var previousKit = state.RecoveryKitId;
                state.RecoveryKitId = kitId;
                state.RecoveryKitIssuedAtUtc = ObserveTime(state);
                state.RecoveryKitVersion = checked(state.RecoveryKitVersion + 1);
                state.RecoveryCodes = codes.Select(code => new RecoveryCodeState { CodeId = code.Id, Purpose = "RecoveryKit",
                    Verifier = IdentityStateProtection.SecretVerifier("RecoveryKit", state.StationId,
                        state.InstallationKeyId, code.Id, code.Secret) }).ToList();
                state.KitState = RecoveryKitState.CustodyConfirmationRequired; state.RecoveryOwnerPrincipalId = actor.PrincipalId;
                return Successful(state, request.OperationId, Rotate,
                    new RecoveryKitRotationResult(true, "RecoveryKitRotated", request.OperationId, AuditPersistence.Persisted, kitId),
                    account, actor.SessionId, null, previousKit, kitId);
            }, RotationFailure(request.OperationId), token).ConfigureAwait(false);
            // Only the invocation that actually created this exact kit may deliver these bytes.
            if (result.Succeeded && result.KitId == kitId)
                return result with { RecoveryKit = new OneTimeSecret("Station: " + _options.StationId + Environment.NewLine +
                    "Recovery Kit: " + kitId.ToString("D") + Environment.NewLine +
                    string.Join(Environment.NewLine, codes.Select(code => code.Id.ToString("N") + "." + code.Secret))) };
            return result;
        }, RotationFailure(request.OperationId), cancellationToken);
    }

    public ValueTask<AdministratorRecoveryResult> ConfirmRecoveryKitCustodyAsync(ConfirmRecoveryKitCustodyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(request.OperationId, Confirm, async token =>
        {
            CheckStation(request.StationId); CheckOperationalPreflight();
            var actor = ActualSession(request.Invocation);
            var parsed = LocalIdentityService.ParseCode(request.ConfirmationCode);
            RejectUnless(parsed is not null, "RecoveryConfirmationCodeInvalid");
            return await CommitAsync(request.OperationId, Confirm, actor.PrincipalId, actor.SessionId, state =>
            {
                var account = CheckKitOwner(state, actor.PrincipalId);
                RejectUnless(request.KitId != Guid.Empty && state.RecoveryKitId == request.KitId &&
                    state.KitState == RecoveryKitState.CustodyConfirmationRequired, "RecoveryKitStateMismatch");
                var code = FindCode(state, parsed!.Value);
                RejectUnless(code is not null, "RecoveryConfirmationCodeInvalid");
                RejectUnless(state.RecoveryCodes.Count(item => !item.Consumed && !item.Revoked) >= 2,
                    "RecoveryCodeMinimumRemaining");
                code!.Consumed = true; state.KitState = RecoveryKitState.Available; state.RecoveryOwnerPrincipalId = null;
                return Successful(state, request.OperationId, Confirm,
                    new AdministratorRecoveryResult(true, "RecoveryKitCustodyConfirmed", request.OperationId,
                        AuditPersistence.Persisted, account.ToIdentity()), account, actor.SessionId,
                    code.CodeId, null, request.KitId);
            }, RecoveryFailure(request.OperationId), token).ConfigureAwait(false);
        }, RecoveryFailure(request.OperationId), cancellationToken);
    }

    private async Task<T> CommitAsync<T>(Guid operationId, string kind, Guid? principalId, Guid? sessionId,
        Func<IdentityAuthorityState, IdentityUpdate> apply, Func<string, AuditPersistence, T> failure, CancellationToken token)
    {
        await WaitForAuditAsync(token).ConfigureAwait(false);
        using var runtimeLease = await _runtime.EnterAsync(token).ConfigureAwait(false);
        RejectUnless(runtimeLease.Check() is null, runtimeLease.Check() ?? "RecoveryRuntimeAuthorityUnavailable");
        var write = await _store.UpdateRecoveryIdentityAsync(operationId, (state, previous) =>
        {
            BeforeTransactionChecks?.Invoke();
            var rejection = token.IsCancellationRequested ? "RecoveryCancelled" : runtimeLease.Check();
            if (rejection is null && !_console.Observe().PhysicalConsole) rejection = "PhysicalConsoleRequired";
            if (previous is not null) rejection = previous.Kind == kind ? "OperationAlreadyCompleted" : "OperationIdConflict";
            if (rejection is not null)
                return Denied(state, operationId, kind, rejection, failure(rejection, AuditPersistence.Persisted));
            if (_sessions is null || !_sessions.TryAcquireRecoveryLease(principalId, sessionId, out var lease, out _))
                return Denied(state, operationId, kind, "RecoverySessionConflict",
                    failure("RecoverySessionConflict", AuditPersistence.Persisted));
            var transferred = false;
            try
            {
                IdentityUpdate update;
                try { update = apply(state); }
                catch (RecoveryRejectedException exception)
                { update = Denied(state, operationId, kind, exception.Code, failure(exception.Code, AuditPersistence.Persisted)); }
                var events = update.Events.Select(fact => update.CompletedRecoveryOperation is null ? fact : fact with
                    { RecoverySafetyEvidence = "PhysicalConsole.RuntimeLease." + runtimeLease.RuntimeEpoch.ToString("N") }).ToArray();
                transferred = true;
                return update with { Events = events, CommitGuard = new RecoverySessionCommitGuard(lease!) };
            }
            finally { if (!transferred) lease?.Dispose(); }
        }, token).ConfigureAwait(false);
        return write.Committed && write.Result is T result ? result : failure("RecoveryAuditUnavailable", AuditPersistence.Unavailable);
    }

    private IdentityUpdate Successful<T>(IdentityAuthorityState state, Guid operationId, string kind, T result,
        LocalAdministratorState account, Guid? sessionId, Guid? codeId, Guid? previousKitId, Guid? kitId)
    {
        var reason = kind switch { Recover => "AdministratorRecovered", Rotate => "RecoveryKitRotated", _ => "RecoveryKitCustodyConfirmed" };
        var eventKind = kind switch { Recover => IdentityEventKind.AdministratorRecovered,
            Rotate => IdentityEventKind.RecoveryKitRotated, _ => IdentityEventKind.RecoveryKitCustodyConfirmed };
        var fact = Fact(state, operationId, eventKind, reason) with
        {
            PrincipalId = account.PrincipalId, CredentialId = account.CredentialId,
            ActorPrincipalId = kind == Recover ? null : account.PrincipalId,
            TargetPrincipalId = account.PrincipalId, SessionId = sessionId, RecoveryCodeId = codeId,
            PreviousRecoveryKitId = previousKitId, RecoveryKitId = kitId, AuthorizationRevision = account.AuthorizationRevision
        };
        return new IdentityUpdate(result!, new[] { fact }, CompletedRecoveryOperation: new RecoveryOperationState
            { OperationId = operationId, Kind = kind, Succeeded = true, ReasonCode = reason, KitId = kitId,
                PrincipalId = account.PrincipalId, DeliveryCommitted = kind == Rotate });
    }

    private IdentityUpdate Denied<T>(IdentityAuthorityState state, Guid operationId, string kind, string reason, T result) =>
        new(result!, new[] { Fact(state, operationId, kind switch { Recover => IdentityEventKind.RecoveryRejected,
            Rotate => IdentityEventKind.RecoveryKitRotationRejected, _ => IdentityEventKind.RecoveryKitCustodyRejected }, reason) });

    private IdentityAuditEvent Fact(IdentityAuthorityState state, Guid operationId, IdentityEventKind kind, string reason) =>
        new(Guid.NewGuid(), kind, ObserveTime(state), state.StationId, null, null, null, null, reason,
            PasswordPolicyVersion: _options.PasswordPolicy.Version, BlocklistId: _options.PasswordPolicy.Blocklist!.Id,
            BlocklistVersion: _options.PasswordPolicy.Blocklist.Version, HashBaselineVersion: _options.HashBaselineVersion,
            HashTargetCost: _options.Baseline.TargetIterations, OperationId: operationId, CommandCorrelationId: operationId);

    private async ValueTask<T> RunAsync<T>(Guid operationId, string kind, Func<CancellationToken, Task<T>> body,
        Func<string, AuditPersistence, T> failure, CancellationToken caller)
    {
        if (operationId == Guid.Empty) return failure("OperationIdRequired", AuditPersistence.NotAttempted);
        return await BoundedAsync(async token =>
        {
            try
            {
                var previous = await _store.ReadRecoveryOperationAsync(operationId, token).ConfigureAwait(false);
                if (previous is not null)
                    return failure(previous.Kind == kind ? "OperationAlreadyCompleted" : "OperationIdConflict", AuditPersistence.Persisted);
                return await body(token).ConfigureAwait(false);
            }
            catch (RecoveryRejectedException exception)
            {
                await WaitForAuditAsync(token).ConfigureAwait(false);
                var write = await _store.UpdateIdentityAsync(state => Denied(state, operationId, kind, exception.Code,
                    failure(exception.Code, AuditPersistence.Persisted)), token).ConfigureAwait(false);
                return write.Committed && write.Result is T result ? result : failure("RecoveryAuditUnavailable", AuditPersistence.Unavailable);
            }
        }, reason => failure(reason, AuditPersistence.Unavailable), caller).ConfigureAwait(false);
    }

    private async ValueTask<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> body, Func<string, T> failure, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _requests) > 16)
        { Interlocked.Decrement(ref _requests); return failure("RecoveryCapacityExceeded"); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller);
        lifetime.CancelAfter(_options.OperationTimeout);
        try { return await body(lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return failure(caller.IsCancellationRequested ? "RecoveryCancelled" : "RecoveryOperationDeadlineExceeded"); }
        catch (TimeoutException) { return failure("RecoveryOperationDeadlineExceeded"); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return failure("RecoveryUnavailable"); }
        finally { Interlocked.Decrement(ref _requests); }
    }

    private async Task<PasswordHashRecord> DeriveAsync(string password, CancellationToken token)
    {
        await HashSlots.WaitAsync(token).ConfigureAwait(false);
        Task<PasswordHashRecord>? task = null;
        try
        {
            task = Task.Run(() => _options.PasswordHasher.Hash(password), token);
            return await task.WaitAsync(_options.OperationTimeout, token).ConfigureAwait(false);
        }
        finally
        {
            if (task is { IsCompleted: false })
                _ = task.ContinueWith(completed => { _ = completed.Exception; HashSlots.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else HashSlots.Release();
        }
    }

    private async Task WaitForAuditAsync(CancellationToken token)
    {
        while (_store.Integrity?.State == AuditIntegrityState.Verifying)
            await Task.Delay(20, token).ConfigureAwait(false);
        RejectUnless(_store.Integrity?.State == AuditIntegrityState.Verified, "RecoveryAuditUnavailable");
    }

    private void CheckOperationalPreflight()
    {
        RejectUnless(_sessions is not null, "RecoverySessionAuthorityUnavailable");
        RejectUnless(_console.Observe().PhysicalConsole, "PhysicalConsoleRequired");
        if (_runtime.GetBlocker() is { } reason) throw new RecoveryRejectedException(reason);
    }

    private void CheckStation(string stationId) => RejectUnless(stationId == _options.StationId, "RecoveryStationMismatch");

    private (Guid PrincipalId, Guid SessionId) ActualSession(CommandInvocation? invocation)
    {
        var current = _sessions?.Current;
        RejectUnless(invocation?.Source == CommandSource.PhysicalConsole, "PhysicalConsoleRequired");
        RejectUnless(current?.State == InteractiveSessionState.Authenticated && current.SessionId is not null &&
            current.SessionId == invocation!.SessionId && current.PrincipalId == invocation.PrincipalId &&
            Guid.TryParseExact(current.PrincipalId, "D", out _), "RecoverySessionMismatch");
        return (Guid.Parse(current!.PrincipalId!), current.SessionId!.Value);
    }

    private static void CheckRecoverable(IdentityAuthorityState state, (Guid Id, string Secret) code)
    {
        RejectUnless(state.Bootstrap is { State: "Consumed" }, "BootstrapRequired");
        RejectUnless(!state.EnumerateAccounts().Any(IdentityAuthorityState.IsUsableAdministrator), "UsableAdministratorPresent");
        RejectUnless(state.KitState == RecoveryKitState.Available, "RecoveryWorkflowConflict");
        RejectUnless(state.RecoveryCodes.Any(item => !item.Consumed && !item.Revoked), "GovernedRestoreOrReinitializationRequired");
        RejectUnless(FindCode(state, code) is not null, "RecoveryCodeInvalid");
    }

    private static LocalAdministratorState CheckKitOwner(IdentityAuthorityState state, Guid principalId)
    {
        var account = state.EnumerateAccounts().SingleOrDefault(item => item.PrincipalId == principalId);
        RejectUnless(account is not null && IdentityAuthorityState.IsUsableAdministrator(account), "PermissionDenied");
        RejectUnless(state.Bootstrap is { State: "Consumed" } && state.KitState != RecoveryKitState.Unavailable, "RecoveryKitUnavailable");
        RejectUnless(state.KitState == RecoveryKitState.Available || state.RecoveryOwnerPrincipalId == principalId, "RecoveryOwnerRequired");
        return account!;
    }

    private static RecoveryCodeState? FindCode(IdentityAuthorityState state, (Guid Id, string Secret) parsed)
    {
        var code = state.RecoveryCodes.SingleOrDefault(item => item.CodeId == parsed.Id && !item.Consumed && !item.Revoked);
        return code is not null && IdentityStateProtection.Matches(code.Verifier, IdentityStateProtection.SecretVerifier(
            code.Purpose, state.StationId, state.InstallationKeyId, code.CodeId, parsed.Secret)) ? code : null;
    }

    private DateTimeOffset ObserveTime(IdentityAuthorityState state)
    { var now = _utcNow(); if (now > state.LastObservedUtc) state.LastObservedUtc = now; return state.LastObservedUtc; }
    private static void RejectUnless([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string reason)
    { if (!condition) throw new RecoveryRejectedException(reason); }
    private static Func<string, AuditPersistence, AdministratorRecoveryResult> RecoveryFailure(Guid id) =>
        (reason, audit) => new(false, reason, id, audit);
    private static Func<string, AuditPersistence, RecoveryKitRotationResult> RotationFailure(Guid id) =>
        (reason, audit) => new(false, reason, id, audit);

    private sealed class RecoveryRejectedException : Exception
    {
        internal RecoveryRejectedException(string code) : base("RecoveryRejected") => Code = code;
        internal string Code { get; }
    }
    private sealed class RecoverySessionCommitGuard : IIdentityTransactionGuard
    {
        private IDisposable? _lease;
        internal RecoverySessionCommitGuard(IDisposable lease) => _lease = lease;
        public void Commit() { }
        public void Dispose()
        {
            var lease = _lease;
            lease?.Dispose();
            _lease = null;
        }
    }
}
