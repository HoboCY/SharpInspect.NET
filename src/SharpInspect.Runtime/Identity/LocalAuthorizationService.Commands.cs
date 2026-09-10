using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<RuntimeCommandOutcome> HandleCommandAsync(RuntimeCommand command, Guid runtimeEpoch,
        Guid attemptId, PreparedManagement prepared, string? forcedRejection, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId, CommandDisposition.Rejected,
            reason, AuditPersistence.Unavailable, attemptId);
        if (command.CorrelationId == Guid.Empty) return Unavailable("InvalidCommandContext");
        try
        {
            var result = await _store.UpdateIdentityCommandAsync(command.CorrelationId, (state, duplicate) =>
                ApplyCommand(state, command, runtimeEpoch, attemptId, duplicate, prepared, forcedRejection, cancellationToken),
                cancellationToken, deadline).ConfigureAwait(false);
            if (!result.Committed || result.Result is not RuntimeCommandOutcome outcome)
                return Unavailable("TraceAuditUnavailable");
            if (outcome.Disposition == CommandDisposition.Accepted && command is IdentityManagementCommand management)
            {
                lock (_grantSync)
                    foreach (var id in _grants.Where(pair => pair.Value.PrincipalId == management.TargetPrincipalId)
                                 .Select(pair => pair.Key).ToArray()) _grants.Remove(id);
                // Credential changes revoke interactive authority after the atomic account transaction.
                // Every command already rechecks the sealed account, so no stale projection can bypass it.
                var current = _sessions?.Current;
                if (command is DisableHumanCredentialCommand or RebindHumanCredentialCommand &&
                    current?.PrincipalId == management.TargetPrincipalId.ToString("D"))
                {
                    try
                    {
                        // Logout clears the authoritative session synchronously before its first await.
                        // Its bounded persistence/retry protocol continues without holding the command gate.
                        var revocation = _sessions!.LogoutAsync(current.SessionId, CancellationToken.None).AsTask();
                        _ = revocation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { /* The account transaction already committed. */ }
                }
            }
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Unavailable("TraceAuditUnavailable"); }
    }

    // Input/KDF preparation owns no Runtime command gate, grant consumption, or durable state.
    // If the caller's deadline expires, these actual slots remain occupied until preparation stops.
    internal async Task<PreparedManagement> PrepareCommandAsync(RuntimeCommand command, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _outstandingPreparations) > 16)
        {
            Interlocked.Decrement(ref _outstandingPreparations);
            return new(Reason: "ManagementPreparationCapacityExceeded");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        var entered = false;
        try
        {
            await _preparationSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            var prepared = await PrepareAsync(command, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            await WaitForAuditAsync(timeout.Token).ConfigureAwait(false);
            return prepared;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(Reason: "ManagementPreparationDeadlineExceeded"); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        { return new(Reason: "ManagementPreparationUnavailable"); }
        finally
        {
            if (entered) _preparationSlots.Release();
            Interlocked.Decrement(ref _outstandingPreparations);
        }
    }

    private async Task<PreparedManagement> PrepareAsync(RuntimeCommand command, CancellationToken cancellationToken)
    {
        if (command is not IdentityManagementCommand management) return new();
        if (management.TargetPrincipalId == Guid.Empty || !Enum.IsDefined(management.Reason))
            return new(Reason: "ManagementInputInvalid");
        if (command is SetHumanPermissionsCommand permissions &&
            permissions.Permissions.Any(permission => permission == Permission.None || !Enum.IsDefined(permission)))
            return new(Reason: "PermissionSetInvalid");
        if (command is not (CreateHumanAccountCommand or RebindHumanCredentialCommand)) return new();
        // Credential derivation uses its own bounded slots and never holds a Runtime/Session/SQLite lock.
        // Cheap authorization before hashing prevents unauthenticated requests from consuming KDF work.
        var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (!TryLease(command.Invocation, out var lease, out var reason)) return new(Reason: reason);
        using (lease)
        {
            var actor = Find(state, lease!.Identity.PrincipalId);
            if (actor is not { Enabled: true } || !actor.Permissions.Contains(RequiredPermission(command)))
                return new(Reason: "PermissionDenied");
            reason = CheckGrant(command, actor, lease.SessionId, reserve: false, out _);
            if (reason != "Authorized") return new(Reason: reason);
        }
        try
        {
            if (command is CreateHumanAccountCommand create)
            {
                if (!Enum.IsDefined(create.RoleBundle)) return new(Reason: "RoleBundleInvalid");
                var (name, key) = LocalIdentityService.ValidateUserName(create.UserName, creation: true);
                var display = LocalIdentityService.ValidateDisplayName(create.DisplayName);
                var normalized = _options.PasswordPolicy.NormalizeAndValidate(create.Password, name, "SharpInspect.NET", _options.StationId);
                var hash = await DeriveAsync(normalized, cancellationToken).ConfigureAwait(false);
                return new(new LocalAdministratorState { PrincipalId = create.TargetPrincipalId, UserName = name,
                    UserNameKey = key, DisplayName = display, CredentialId = Guid.NewGuid(), CredentialRevision = 1,
                    AuthorizationRevision = 1, RoleBundle = create.RoleBundle,
                    Permissions = _options.AuthorizationPolicy.GetPermissions(create.RoleBundle).ToList(),
                    Password = PasswordVerifierState.From(hash) });
            }
            var target = Find(state, management.TargetPrincipalId);
            if (target is null) return new(Reason: "HumanAccountNotFound");
            var rebind = (RebindHumanCredentialCommand)command;
            var password = _options.PasswordPolicy.NormalizeAndValidate(rebind.NewPassword, target.UserName, "SharpInspect.NET", _options.StationId);
            return new(Password: PasswordVerifierState.From(await DeriveAsync(password, cancellationToken)
                .ConfigureAwait(false)));
        }
        catch (ArgumentException ex) { return new(Reason: LocalIdentityService.CreationRejection(ex)); }
    }

    private IdentityUpdate ApplyCommand(IdentityAuthorityState state, RuntimeCommand command, Guid epoch, Guid attempt,
        bool duplicate, PreparedManagement prepared, string? forcedRejection, CancellationToken cancellationToken)
    {
        LocalAdministratorState? actor = null;
        SessionAuthorizationLease? lease = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = TryLease(command.Invocation, out lease, out var leaseReason) ? "Authorized" : leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && (actor is not { Enabled: true } || !actor.Permissions.Contains(RequiredPermission(command))))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
            if (reason == "Authorized" && cancellationToken.IsCancellationRequested) reason = "ManagementCancelled";
            if (reason == "Authorized" && prepared.Reason is not null) reason = prepared.Reason;
            if (reason == "Authorized") reason = CheckGrant(command, actor!, lease!.SessionId, reserve: false, out _);
            if (reason == "Authorized" && command is not IdentityManagementCommand)
                reason = command is ArmProductionCommand ? "DeploymentPoliciesMissing" : "GovernedCapabilityUnavailable";
            if (reason == "Authorized") reason = ValidateMutation(state, (IdentityManagementCommand)command, actor!, prepared);
            if (reason != "Authorized")
                return CommandDecision(state, command, epoch, attempt, actor, lease?.SessionId, reason, accepted: false);

            reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);
            if (reason != "Authorized")
                return CommandDecision(state, command, epoch, attempt, actor, lease.SessionId, reason, accepted: false);
            var guard = new AuthorizationCommitGuard(lease, () =>
            {
                lock (_grantSync) if (reserved is not null) reserved.State = GrantState.Consumed;
            }, () =>
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
            });
            var management = (IdentityManagementCommand)command;
            var authorizedRevision = actor!.AuthorizationRevision;
            var previousPermissions = command is SetHumanPermissionsCommand
                ? PermissionList(Find(state, management.TargetPrincipalId)!.Permissions) : null;
            var eventKind = Mutate(state, management, prepared);
            var target = Find(state, management.TargetPrincipalId)!;
            var resultingPermissions = command is CreateHumanAccountCommand or SetHumanPermissionsCommand
                ? PermissionList(target.Permissions) : null;
            var update = CommandDecision(state, command, epoch, attempt, actor, lease.SessionId,
                "ManagementCompleted", accepted: true, eventKind, guard, authorizedRevision,
                previousPermissions, resultingPermissions, target.CredentialId);
            transferred = true;
            return update;
        }
        finally
        {
            if (!transferred)
            {
                lock (_grantSync) if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                lease?.Dispose();
            }
        }
    }

    private string CheckGrant(RuntimeCommand command, LocalAdministratorState actor, Guid sessionId, bool reserve,
        out StepUpGrant? grant)
    {
        grant = null;
        if (command is not StartStationQualificationSessionCommand &&
            !_options.AuthorizationPolicy.RequiresStepUp(RequiredPermission(command))) return "Authorized";
        if (command.Invocation.StepUpGrantId is not { } id) return "StepUpRequired";
        lock (_grantSync)
        {
            PurgeGrantsLocked();
            if (!_grants.TryGetValue(id, out var candidate) || candidate.State != GrantState.Active ||
                candidate.PrincipalId != actor.PrincipalId || candidate.SessionId != sessionId ||
                candidate.CredentialId != actor.CredentialId || candidate.AuthorizationRevision != actor.AuthorizationRevision ||
                candidate.PolicyHash != _options.AuthorizationPolicy.ContentHash || candidate.Binding != Binding(command))
                return "StepUpInvalid";
            grant = candidate;
            if (reserve) candidate.State = GrantState.Reserved;
            return "Authorized";
        }
    }

    private static string ValidateMutation(IdentityAuthorityState state, IdentityManagementCommand command,
        LocalAdministratorState actor, PreparedManagement prepared)
    {
        if (command is CreateHumanAccountCommand)
        {
            // Creating a role-bearing account is also a permission assignment. Account lifecycle
            // permission alone must not create a new, more privileged principal.
            if (!actor.Permissions.Contains(Permission.ManagePermissions)) return "PermissionAssignmentDenied";
            if (prepared.Account is null) return "ManagementInputInvalid";
            if (state.EnumerateAccounts().Count() >= AuthorizationPolicy.MaxHumanAccounts) return "HumanAccountCapacityExceeded";
            return state.EnumerateAccounts().Any(account => account.PrincipalId == command.TargetPrincipalId ||
                account.UserNameKey == prepared.Account.UserNameKey) ? "HumanAccountAlreadyExists" : "Authorized";
        }
        var target = Find(state, command.TargetPrincipalId);
        if (target is null) return "HumanAccountNotFound";
        if (command is UnlockHumanCredentialCommand or RebindHumanCredentialCommand && target.PrincipalId == actor.PrincipalId)
            return "DifferentAdministratorRequired";
        if (command is RebindHumanCredentialCommand && prepared.Password is null) return "ManagementInputInvalid";
        if (IdentityAuthorityState.IsUsableAdministrator(target) &&
            !state.EnumerateAccounts().Any(account => account.PrincipalId != target.PrincipalId && IdentityAuthorityState.IsUsableAdministrator(account)))
        {
            if (command is DisableHumanCredentialCommand) return "LastAdministratorRequired";
            if (command is SetHumanPermissionsCommand set &&
                !new[] { Permission.ManageAccounts, Permission.ManagePermissions, Permission.UnlockCredential, Permission.RebindCredential }
                    .All(set.Permissions.Contains)) return "LastAdministratorRequired";
        }
        return "Authorized";
    }

    private IdentityEventKind Mutate(IdentityAuthorityState state, IdentityManagementCommand command, PreparedManagement prepared)
    {
        if (command is CreateHumanAccountCommand)
        {
            state.AdditionalAccounts.Add(prepared.Account!);
            return IdentityEventKind.HumanAccountCreated;
        }
        var target = Find(state, command.TargetPrincipalId)!;
        target.AuthorizationRevision = checked(target.AuthorizationRevision + 1);
        switch (command)
        {
            case DisableHumanCredentialCommand:
                target.Enabled = false; target.DisabledAtUtc = _utcNow();
                return IdentityEventKind.CredentialDisabled;
            case UnlockHumanCredentialCommand:
                target.Enabled = true; target.DisabledAtUtc = null; target.Throttle = new();
                return IdentityEventKind.CredentialUnlocked;
            case RebindHumanCredentialCommand:
                target.CredentialId = Guid.NewGuid(); target.CredentialRevision = checked(target.CredentialRevision + 1);
                target.Password = prepared.Password!; target.Enabled = true; target.DisabledAtUtc = null; target.Throttle = new();
                return IdentityEventKind.CredentialRebound;
            case SetHumanPermissionsCommand set:
                target.Permissions = set.Permissions.Distinct().OrderBy(permission => (int)permission).ToList(); target.RoleBundle = null;
                return IdentityEventKind.HumanPermissionsChanged;
            default: throw new InvalidOperationException("UnsupportedManagementCommand");
        }
    }

    private IdentityUpdate CommandDecision(IdentityAuthorityState state, RuntimeCommand command, Guid epoch, Guid attempt,
        LocalAdministratorState? actor, Guid? sessionId, string reason, bool accepted,
        IdentityEventKind kind = IdentityEventKind.ManagementRejected, IIdentityTransactionGuard? guard = null,
        long? authorizedRevision = null, string? previousPermissions = null, string? resultingPermissions = null,
        Guid? credentialId = null)
    {
        var disposition = accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected;
        var management = command as IdentityManagementCommand;
        var binding = Binding(command);
        if (!ValidBinding(binding)) binding = null;
        var fact = new CommandAuditFact(Guid.NewGuid(), attempt, command.CorrelationId, epoch, _utcNow(), CommandKind(command),
            command.Invocation is { } invocation && Enum.IsDefined(invocation.Source) ? invocation.Source : null,
            command.Invocation?.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
            command.Invocation?.SessionId, command.Invocation?.StepUpGrantId, CommandAuditPhase.Outcome, disposition, reason,
            actor?.PrincipalId.ToString("D"));
        var facts = accepted ? new[] { fact, fact with { EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed, Disposition = null } }
            : new[] { fact };
        var identity = AuthorizationEvent(state, kind, reason, binding, actor?.PrincipalId, sessionId,
            command.Invocation?.StepUpGrantId, command.CorrelationId, authorizedRevision ?? actor?.AuthorizationRevision ?? 0,
            management?.TargetPrincipalId is { } target && target != Guid.Empty ? target : null,
            management is not null && Enum.IsDefined(management.Reason) ? management.Reason : null) with
            { PreviousPermissions = previousPermissions, ResultingPermissions = resultingPermissions, CredentialId = credentialId };
        var events = accepted && command.Invocation?.StepUpGrantId is not null
            ? new[] { identity with { EventId = Guid.NewGuid(), Kind = IdentityEventKind.StepUpConsumed, ReasonCode = "StepUpConsumed" }, identity }
            : new[] { identity };
        return new(new RuntimeCommandOutcome(command.CorrelationId, disposition, reason, AuditPersistence.Persisted, attempt),
            events, facts, guard);
    }

    internal sealed record PreparedManagement(LocalAdministratorState? Account = null, PasswordVerifierState? Password = null,
        string? Reason = null);

    private Task<PasswordHashRecord> DeriveAsync(string normalized, CancellationToken cancellationToken) => Task.Run(() =>
    {
        _beforeCredentialDerivation?.Invoke(); // Internal deterministic scheduling seam; never exposed in the public host API.
        cancellationToken.ThrowIfCancellationRequested();
        return _options.PasswordHasher.Hash(normalized);
    }, cancellationToken);

    private static string PermissionList(IEnumerable<Permission> permissions) =>
        string.Join(",", permissions.Distinct().OrderBy(permission => (int)permission));
}
