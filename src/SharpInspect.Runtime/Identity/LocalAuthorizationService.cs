using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>Local human authorization. Provider claims and caller-supplied IDs are never permission assignments.</summary>
internal sealed partial class LocalAuthorizationService : IIdentityAdministrationQuery, IStepUpAuthentication, IDisposable
{
    private const int MaximumGrants = 32;
    private readonly SqliteCommandStore _store;
    private readonly LocalIdentityOptions _options;
    private readonly IIdentityProvider _provider;
    private readonly InteractiveSessionService? _sessions;
    private readonly Func<long> _timestamp;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _grantSync = new();
    private readonly Dictionary<Guid, StepUpGrant> _grants = new();
    private readonly SemaphoreSlim _authenticationSlots = new(16, 16);
    private readonly SemaphoreSlim _querySlots = new(4, 4);
    private readonly SemaphoreSlim _preparationSlots = new(2, 2);
    private readonly Action? _beforeCredentialDerivation;
    private int _outstandingPreparations;
    private int _outstandingQueries;
    private int _disposed;

    internal LocalAuthorizationService(SqliteCommandStore store, LocalIdentityOptions options,
        IIdentityProvider provider, IInteractiveSessionService? sessions, Func<long>? timestamp = null,
        Func<DateTimeOffset>? utcNow = null, Action? beforeCredentialDerivation = null)
    {
        _store = store; _options = options; _provider = provider;
        _sessions = sessions as InteractiveSessionService;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeCredentialDerivation = beforeCredentialDerivation;
        if (_sessions is not null) _sessions.Changed += SessionChanged;
    }

    public ValueTask<HumanAuthorizationSnapshot> GetCurrentAuthorizationAsync(Guid? sessionId,
        CancellationToken cancellationToken = default) => QueryAsync(
            token => ReadCurrentAuthorizationAsync(sessionId, token),
            reason => new HumanAuthorizationSnapshot(false, reason, null, null), cancellationToken);

    private async ValueTask<HumanAuthorizationSnapshot> ReadCurrentAuthorizationAsync(Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (_sessions is null || sessionId is null || Volatile.Read(ref _disposed) != 0)
            return new(false, "AuthenticationRequired", null, null);
        try
        {
            var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            var current = _sessions.Current;
            if (current.SessionId != sessionId || !Guid.TryParse(current.PrincipalId, out var principalId) ||
                !_sessions.TryAcquireAuthorizationLease(principalId, sessionId.Value, out var lease, out var reason))
                return new(false, "SessionInvalid", null, null);
            using (lease)
            {
                var account = Find(state, principalId);
                return account is { Enabled: true }
                    ? new(true, "AuthorizationAvailable", sessionId, Summary(account))
                    : new(false, "CredentialUnavailable", null, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, "AuthorizationUnavailable", null, null); }
    }

    public ValueTask<HumanDirectorySnapshot> GetAccountsAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default) => QueryAsync(
            token => ReadAccountsAsync(invocation, token), reason => new HumanDirectorySnapshot(false, reason), cancellationToken);

    private async ValueTask<HumanDirectorySnapshot> ReadAccountsAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        if (invocation is null || _sessions is null || Volatile.Read(ref _disposed) != 0)
            return new(false, "AuthenticationRequired");
        try
        {
            var state = await _store.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (!TryLease(invocation, out var lease, out var reason)) return new(false, reason);
            using (lease)
            {
                var actor = Find(state, lease!.Identity.PrincipalId);
                if (actor is not { Enabled: true } || !actor.Permissions.Contains(Permission.ManageAccounts))
                    return new(false, "PermissionDenied");
                return new(true, "HumanDirectoryAvailable", state.EnumerateAccounts().Select(Summary));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, "AuthorizationUnavailable"); }
    }

    private async ValueTask<T> QueryAsync<T>(Func<CancellationToken, ValueTask<T>> query, Func<string, T> unavailable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _outstandingQueries) > 16)
        {
            Interlocked.Decrement(ref _outstandingQueries);
            return unavailable("AuthorizationQueryCapacityExceeded");
        }
        var entered = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        try
        {
            await _querySlots.WaitAsync(timeout.Token).ConfigureAwait(false);
            entered = true;
            return await query(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return unavailable("AuthorizationQueryDeadlineExceeded"); }
        finally
        {
            if (entered) _querySlots.Release();
            Interlocked.Decrement(ref _outstandingQueries);
        }
    }

    private bool TryLease(CommandInvocation invocation, out SessionAuthorizationLease? lease,
        out string reason)
    {
        lease = null;
        reason = "AuthenticationRequired";
        if (Volatile.Read(ref _disposed) != 0 || _sessions is null || invocation is null ||
            !Enum.IsDefined(invocation.Source) || !Guid.TryParseExact(invocation.PrincipalId, "D", out var principalId) ||
            principalId == Guid.Empty || invocation.SessionId is not { } sessionId || sessionId == Guid.Empty)
            return false;
        return _sessions.TryAcquireAuthorizationLease(principalId, sessionId, out lease, out reason);
    }

    private static LocalAdministratorState? Find(IdentityAuthorityState state, Guid principalId) =>
        state.EnumerateAccounts().SingleOrDefault(account => account.PrincipalId == principalId);

    private static HumanAccountSummary Summary(LocalAdministratorState account) => new(account.PrincipalId,
        account.UserName, account.DisplayName, account.Enabled, account.AuthorizationRevision, account.Permissions, account.RoleBundle);

    private static Permission RequiredPermission(RuntimeCommand command) => command switch
    {
        CreateHumanAccountCommand or DisableHumanCredentialCommand => Permission.ManageAccounts,
        UnlockHumanCredentialCommand => Permission.UnlockCredential,
        RebindHumanCredentialCommand => Permission.RebindCredential,
        SetHumanPermissionsCommand => Permission.ManagePermissions,
        ArmProductionCommand => Permission.ArmProduction,
        AcknowledgeAlarmCommand => Permission.AcknowledgeAlarm,
        ResetAlarmCommand => Permission.ResetAlarm,
        StartCameraRecoveryCycleCommand => Permission.ManageCameraBindings,
        StartCalibrationSessionCommand or CalibrationSessionCommand => Permission.RunCalibration,
        PublishCalibrationAcceptancePolicyCommand => Permission.ManageCalibrationAcceptancePolicy,
        EvaluateCalibrationCandidateCommand or PublishCalibrationProfileCommand => Permission.PublishCalibration,
        RecordPhysicalCalibrationVerificationCommand => Permission.RecordPhysicalCalibrationVerification,
        ChangePlcResultContractCommand => Permission.ManagePlcResultContract,
        ReleaseRecipeCommand => Permission.ReleaseRecipe,
        GovernedAuditChangeCommand change => change.Change switch
        {
            GovernedAuditChangeKind.RotateSigningKey or GovernedAuditChangeKind.RetireSigningKey => Permission.ManageAuditSigningKeys,
            GovernedAuditChangeKind.CorrectHistoricalFact => Permission.CorrectHistoricalFact,
            GovernedAuditChangeKind.DeleteEvidence => Permission.DeleteEvidence,
            _ => Permission.None
        },
        _ => Permission.None
    };

    private StepUpBinding Binding(RuntimeCommand command) => new(RequiredPermission(command), command.CorrelationId,
        command switch
        {
            IdentityManagementCommand management => management.TargetPrincipalId.ToString("D"),
            AcknowledgeAlarmCommand acknowledge => acknowledge.AlarmInstanceId.ToString("D"),
            ResetAlarmCommand reset => reset.AlarmInstanceId.ToString("D"),
            StartCameraRecoveryCycleCommand recovery => recovery.LogicalRole,
            StartCalibrationSessionCommand calibration => calibration.AuthorizationTarget,
            CalibrationSessionCommand calibration => calibration.AuthorizationTarget,
            CalibrationGovernanceCommand calibration => calibration.AuthorizationTarget,
            ChangePlcResultContractCommand change => change.AuthorizationTarget,
            ReleaseRecipeCommand release => release.AuthorizationTarget,
            _ => _options.StationId
        },
        CommandKind(command));

    private static bool ValidBinding(StepUpBinding? binding) => binding is not null &&
        binding.Permission != Permission.None && Enum.IsDefined(binding.Permission) && binding.CommandCorrelationId != Guid.Empty &&
        binding.Permission == PermissionForKind(binding.CommandKind) &&
        binding.TargetId is { Length: > 0 and <= 128 } && binding.TargetId.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    private static Permission PermissionForKind(AuditedCommandKind kind) => kind switch
    {
        AuditedCommandKind.CreateHumanAccount or AuditedCommandKind.DisableHumanCredential => Permission.ManageAccounts,
        AuditedCommandKind.UnlockHumanCredential => Permission.UnlockCredential,
        AuditedCommandKind.RebindHumanCredential => Permission.RebindCredential,
        AuditedCommandKind.SetHumanPermissions => Permission.ManagePermissions,
        AuditedCommandKind.ArmProduction => Permission.ArmProduction,
        AuditedCommandKind.AcknowledgeAlarm => Permission.AcknowledgeAlarm,
        AuditedCommandKind.ResetAlarm => Permission.ResetAlarm,
        AuditedCommandKind.RebindCamera or AuditedCommandKind.ApplyCameraDebugConfiguration or
            AuditedCommandKind.ChangeCameraNetworkConfiguration or AuditedCommandKind.DeclareImagingSetup =>
            Permission.ManageCameraBindings,
        AuditedCommandKind.StartCameraRecoveryCycle => Permission.ManageCameraBindings,
        AuditedCommandKind.StartCalibrationSession or AuditedCommandKind.CaptureCalibrationFrame or
            AuditedCommandKind.ExcludeCalibrationFrame or AuditedCommandKind.ComputeCalibrationCandidate or
            AuditedCommandKind.ExitCalibrationSession => Permission.RunCalibration,
        AuditedCommandKind.PublishCalibrationAcceptancePolicy => Permission.ManageCalibrationAcceptancePolicy,
        AuditedCommandKind.EvaluateCalibrationCandidate or AuditedCommandKind.PublishCalibrationProfile =>
            Permission.PublishCalibration,
        AuditedCommandKind.RecordPhysicalCalibrationVerification => Permission.RecordPhysicalCalibrationVerification,
        AuditedCommandKind.ChangePlcResultContract => Permission.ManagePlcResultContract,
        AuditedCommandKind.SaveRecipeDraft => Permission.EditRecipeDraft,
        AuditedCommandKind.MigrateAlgorithmConfiguration => Permission.EditRecipeDraft,
        AuditedCommandKind.ReleaseRecipe => Permission.ReleaseRecipe,
        AuditedCommandKind.RotateSigningKey or AuditedCommandKind.RetireSigningKey => Permission.ManageAuditSigningKeys,
        AuditedCommandKind.CorrectHistoricalFact => Permission.CorrectHistoricalFact,
        AuditedCommandKind.DeleteEvidence => Permission.DeleteEvidence,
        _ => Permission.None
    };

    private static AuditedCommandKind CommandKind(RuntimeCommand command) => command switch
    {
        CreateHumanAccountCommand => AuditedCommandKind.CreateHumanAccount,
        DisableHumanCredentialCommand => AuditedCommandKind.DisableHumanCredential,
        UnlockHumanCredentialCommand => AuditedCommandKind.UnlockHumanCredential,
        RebindHumanCredentialCommand => AuditedCommandKind.RebindHumanCredential,
        SetHumanPermissionsCommand => AuditedCommandKind.SetHumanPermissions,
        ArmProductionCommand => AuditedCommandKind.ArmProduction,
        AcknowledgeAlarmCommand => AuditedCommandKind.AcknowledgeAlarm,
        ResetAlarmCommand => AuditedCommandKind.ResetAlarm,
        StartCameraRecoveryCycleCommand => AuditedCommandKind.StartCameraRecoveryCycle,
        StartCalibrationSessionCommand => AuditedCommandKind.StartCalibrationSession,
        CaptureCalibrationFrameCommand => AuditedCommandKind.CaptureCalibrationFrame,
        ExcludeCalibrationFrameCommand => AuditedCommandKind.ExcludeCalibrationFrame,
        ComputeCalibrationCandidateCommand => AuditedCommandKind.ComputeCalibrationCandidate,
        ExitCalibrationSessionCommand => AuditedCommandKind.ExitCalibrationSession,
        PublishCalibrationAcceptancePolicyCommand => AuditedCommandKind.PublishCalibrationAcceptancePolicy,
        EvaluateCalibrationCandidateCommand => AuditedCommandKind.EvaluateCalibrationCandidate,
        PublishCalibrationProfileCommand => AuditedCommandKind.PublishCalibrationProfile,
        RecordPhysicalCalibrationVerificationCommand => AuditedCommandKind.RecordPhysicalCalibrationVerification,
        ChangePlcResultContractCommand => AuditedCommandKind.ChangePlcResultContract,
        ReleaseRecipeCommand => AuditedCommandKind.ReleaseRecipe,
        GovernedAuditChangeCommand change => change.Change switch
        {
            GovernedAuditChangeKind.RotateSigningKey => AuditedCommandKind.RotateSigningKey,
            GovernedAuditChangeKind.RetireSigningKey => AuditedCommandKind.RetireSigningKey,
            GovernedAuditChangeKind.CorrectHistoricalFact => AuditedCommandKind.CorrectHistoricalFact,
            GovernedAuditChangeKind.DeleteEvidence => AuditedCommandKind.DeleteEvidence,
            _ => AuditedCommandKind.Unsupported
        },
        _ => AuditedCommandKind.Unsupported
    };

    private long NowTicks() => _timestamp();
    private bool IsExpired(StepUpGrant grant)
    {
        var now = NowTicks();
        return now < grant.IssuedTimestamp ||
            (now - grant.IssuedTimestamp) / (double)Stopwatch.Frequency >= _options.AuthenticationPolicy.StepUpFreshness.TotalSeconds;
    }

    private void PurgeGrantsLocked()
    {
        foreach (var id in _grants.Where(pair => pair.Value.State == GrantState.Consumed || IsExpired(pair.Value))
                     .Select(pair => pair.Key).ToArray()) _grants.Remove(id);
    }

    private void SessionChanged(object? sender, InteractiveSessionChangedEventArgs args)
    {
        // Never acquire the Session lock while holding the grant dictionary lock.
        var current = _sessions?.Current;
        lock (_grantSync)
        {
            foreach (var id in _grants.Where(pair => current?.State != InteractiveSessionState.Authenticated ||
                         pair.Value.SessionId != current.SessionId).Select(pair => pair.Key).ToArray())
                _grants.Remove(id);
        }
    }

    internal async Task WaitForAuditAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.OperationTimeout);
        while (_store.Integrity?.State == AuditIntegrityState.Verifying)
            await Task.Delay(20, timeout.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_sessions is not null) _sessions.Changed -= SessionChanged;
        lock (_grantSync) _grants.Clear();
    }

    private enum GrantState { Pending, Active, Reserved, Consumed }
    private sealed class StepUpGrant
    {
        internal Guid Id { get; init; }
        internal Guid PrincipalId { get; init; }
        internal Guid SessionId { get; init; }
        internal Guid CredentialId { get; init; }
        internal long AuthorizationRevision { get; init; }
        internal string PolicyHash { get; init; } = "";
        internal StepUpBinding Binding { get; init; } = null!;
        internal long IssuedTimestamp { get; init; }
        internal DateTimeOffset ExpiresAtUtc { get; init; }
        internal GrantState State { get; set; }
    }
}
