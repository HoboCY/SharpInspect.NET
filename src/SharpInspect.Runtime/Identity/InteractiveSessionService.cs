using System.Diagnostics;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Identity;

/// <summary>Non-secret evidence emitted by the interactive-session authority.</summary>
internal sealed record SessionAuditEvent(Guid? PrincipalId, Guid? SessionId,
    string Kind, string ReasonCode);

/// <summary>
/// Runtime-owned interactive-session authority. It deliberately accepts only an identity
/// provider, never a caller-supplied identity, and keeps session state separate from the
/// provider's credential state.
/// </summary>
internal sealed class InteractiveSessionService : IInteractiveSessionService
{
    private const int MaximumConcurrentOperations = 16;
    private const int MaximumProviderOperations = 16;
    private const int MaximumPersistenceOperations = 16;
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProviderBound = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PersistenceBound = TimeSpan.FromSeconds(1);
    private static readonly InteractiveSession Unauthenticated =
        new(InteractiveSessionState.Unauthenticated, null, null);

    private readonly IIdentityProvider _identityProvider;
    private readonly AuthenticationPolicy _policy;
    private readonly Func<SessionAuditEvent, CancellationToken, ValueTask<bool>> _persist;
    private readonly Func<long> _timestamp;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PeriodicTimer _idleTimer;
    private readonly Task _idleMonitor;
    private readonly TaskCompletionSource<bool> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _idleTimeoutTicks;
    private InteractiveSession _current = Unauthenticated;
    private HumanIdentity? _identity;
    private long _lastActivityTimestamp;
    private long _generation;
    private int _activeOperations;
    private int _activeProviderOperations;
    private int _activePersistenceOperations;
    private PendingRevocation? _pendingRevocation;
    private bool _disposed;
    private int _disposeRequested;

    internal InteractiveSessionService(
        IIdentityProvider identityProvider,
        AuthenticationPolicy policy,
        Func<SessionAuditEvent, CancellationToken, ValueTask<bool>> persist,
        Func<long>? timestamp = null)
    {
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
        _persist = persist ?? throw new ArgumentNullException(nameof(persist));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _idleTimeoutTicks = ToStopwatchTicks(_policy.SessionIdleTimeout);
        _idleTimer = new PeriodicTimer(IdlePollInterval);
        _idleMonitor = MonitorIdleAsync();
    }

    public event EventHandler<InteractiveSessionChangedEventArgs>? Changed;

    public InteractiveSession Current
    {
        get
        {
            TryExpireIdle();
            return Snapshot();
        }
    }

    public async ValueTask<SessionSignInResult> SignInAsync(
        PasswordSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAcquireOperation(out var operationGeneration))
            return Failure(IsDisposed() ? "SessionServiceDisposed" : "SessionCapacityExceeded");

        try
        {
            if (!TryPrepareSignIn(out operationGeneration, out var replacement,
                    out var replacementRevocation, out var notifyReplacement,
                    out var prepareFailure))
                return Failure(prepareFailure);

            if (replacement is not null)
            {
                if (notifyReplacement)
                    NotifyChanged(replacement.Session);
                if (!await PersistRevocationAsync(replacementRevocation, replacement.Event)
                        .ConfigureAwait(false))
                    return Failure("SessionAuditUnavailable");
            }

            AuthenticationResult authentication;
            Task<AuthenticationResult>? providerTask = null;
            CancellationTokenSource? linked = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryAcquireProviderOperation())
                    return Failure("SessionProviderCapacityExceeded");

                linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _lifetime.Token);
                try
                {
                    providerTask = _identityProvider.AuthenticateAsync(request, linked.Token).AsTask();
                }
                catch
                {
                    linked.Dispose();
                    linked = null;
                    ReleaseProviderOperation();
                    throw;
                }

                ObserveProvider(providerTask);
                authentication = await providerTask.WaitAsync(ProviderBound, linked.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                linked?.Cancel();
                return Failure("IdentityProviderTimeout");
            }
            catch (OperationCanceledException)
            {
                return Failure("SessionOperationCancelled");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                authentication = new AuthenticationResult(false, "IdentityProviderUnavailable");
            }
            finally
            {
                if (linked is not null && providerTask is not null)
                    DisposeProviderTokenWhenDone(providerTask, linked);
                else
                    linked?.Dispose();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!authentication.Succeeded)
            {
                var reason = string.IsNullOrWhiteSpace(authentication.ReasonCode)
                    ? "AuthenticationRejected" : authentication.ReasonCode;
                // A rejected credential attempt is an identity-provider concern. The session
                // authority does not manufacture an audit fact for it, and in particular never
                // associates a provider-supplied identity with the failed attempt.
                return Failure(reason);
            }

            if (authentication.Identity is null)
            {
                return Failure("IdentityProviderInvalid");
            }

            var sessionId = Guid.NewGuid();
            cancellationToken.ThrowIfCancellationRequested();
            if (!GenerationStillCurrent(operationGeneration))
            {
                await PersistSignInCancellationAsync(authentication.Identity.PrincipalId, sessionId)
                    .ConfigureAwait(false);
                return Failure("SessionOperationSuperseded");
            }

            var persistedStart = await PersistAuditAsync(new SessionAuditEvent(
                    authentication.Identity.PrincipalId, sessionId, "SessionStarted", "Authenticated"),
                lateSuccess: () => ScheduleLateStartCancellation(
                    authentication.Identity.PrincipalId, sessionId)).ConfigureAwait(false);
            if (!persistedStart)
                return Failure("SessionAuditUnavailable");

            // A lock/logout may have invalidated this authentication while the durable start
            // fact was being written. Close that fact explicitly before returning, while the
            // state remains unauthenticated/locked and no stale identity can be published.
            if (cancellationToken.IsCancellationRequested ||
                !GenerationStillCurrent(operationGeneration))
            {
                await PersistSignInCancellationAsync(authentication.Identity.PrincipalId, sessionId)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                return Failure("SessionOperationSuperseded");
            }

            var session = Unauthenticated;
            var superseded = false;
            lock (_sync)
            {
                if (_disposed || operationGeneration != _generation ||
                    cancellationToken.IsCancellationRequested)
                    superseded = true;
                else
                {
                    _identity = authentication.Identity;
                    _current = session = new InteractiveSession(
                        InteractiveSessionState.Authenticated,
                        authentication.Identity.PrincipalId.ToString("D"), sessionId);
                    _lastActivityTimestamp = SafeTimestamp();
                    _generation = checked(_generation + 1);
                }
            }

            if (superseded)
            {
                await PersistSignInCancellationAsync(authentication.Identity.PrincipalId, sessionId)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                return Failure("SessionOperationSuperseded");
            }

            NotifyChanged(session);
            return new SessionSignInResult(true, "Authenticated", authentication.Identity, session);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryExpireIdle();
        return ValueTask.FromResult(Snapshot());
    }

    public async ValueTask<SessionActionResult> LockAsync(
        Guid? expectedSessionId,
        SessionLockReason reason,
        CancellationToken cancellationToken = default)
    {
        ValidateLockReason(reason);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryTransition(expectedSessionId, InteractiveSessionState.Locked,
                "SessionLocked", reason.ToString(), out var transition))
            return transition.Result;

        NotifyChanged(transition.Session);
        var persisted = await PersistRevocationAsync(transition.Revocation, transition.Event)
            .ConfigureAwait(false);
        return new SessionActionResult(true, "SessionLocked", persisted);
    }

    public async ValueTask<SessionActionResult> LogoutAsync(
        Guid? expectedSessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryTransition(expectedSessionId, InteractiveSessionState.Unauthenticated,
                "SessionLoggedOut", "UserLogout", out var transition))
            return transition.Result;

        NotifyChanged(transition.Session);
        var persisted = await PersistRevocationAsync(transition.Revocation, transition.Event)
            .ConfigureAwait(false);
        return new SessionActionResult(true, "SessionLoggedOut", persisted);
    }

    public async ValueTask<SessionActionResult> ReportActivityAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty)
            return new SessionActionResult(false, "SessionIdRequired", false);
        if (!TryAcquireOperation(out _))
            return new SessionActionResult(false,
                IsDisposed() ? "SessionServiceDisposed" : "SessionCapacityExceeded", false);

        try
        {
            TransitionRecord? expired = null;
            PendingRevocation? revocation = null;
            lock (_sync)
            {
                if (_disposed)
                    return new SessionActionResult(false, "SessionServiceDisposed", false);
                if (_current.State != InteractiveSessionState.Authenticated ||
                    _current.SessionId != sessionId)
                    return new SessionActionResult(false, "SessionMismatch", false);

                var now = SafeTimestamp();
                if (IsExpired(now, _lastActivityTimestamp))
                {
                    expired = TransitionLocked(null, InteractiveSessionState.Locked,
                        "SessionLocked", SessionLockReason.IdleExpired.ToString());
                    revocation = CreatePendingRevocationLocked(expired);
                }
                else
                {
                    _lastActivityTimestamp = Math.Max(_lastActivityTimestamp, now);
                    return new SessionActionResult(true, "ActivityRecorded", true);
                }
            }

            NotifyChanged(expired!.Session);
            var persisted = await PersistRevocationAsync(revocation, expired.Event)
                .ConfigureAwait(false);
            return new SessionActionResult(false, "SessionIdleExpired", persisted);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            InteractiveSession? changed = null;
            lock (_sync)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _generation = checked(_generation + 1);
                    _identity = null;
                    _lastActivityTimestamp = 0;
                    if (_current.State != InteractiveSessionState.Unauthenticated)
                    {
                        _current = Unauthenticated;
                        changed = _current;
                    }
                }
            }

            // Cancellation callbacks belong to provider/persistence implementations and may
            // ignore or delay cancellation. Dispatch cancellation off the Dispose caller so
            // those callbacks cannot extend the bounded shutdown path.
            _ = Task.Run(() =>
            {
                try { _lifetime.Cancel(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            });
            _idleTimer.Dispose();
            if (changed is not null) NotifyChanged(changed);
            _ = FinishDisposeAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await _idleMonitor.WaitAsync(PersistenceBound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A provider or persistence callback may ignore cancellation. The session is
            // already invalidated; disposal remains bounded and does not wait indefinitely.
        }
        catch (OperationCanceledException)
        {
            // The monitor normally exits when the lifetime token is cancelled.
        }
        finally
        {
            _disposeCompletion.TrySetResult(true);
        }
    }

    private async Task MonitorIdleAsync()
    {
        try
        {
            while (await _idleTimer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                TryExpireIdle();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool TryExpireIdle()
    {
        TransitionRecord? transition = null;
        PendingRevocation? revocation = null;
        lock (_sync)
        {
            if (_disposed || _current.State != InteractiveSessionState.Authenticated ||
                !IsExpired(SafeTimestamp(), _lastActivityTimestamp))
                return false;
            transition = TransitionLocked(null, InteractiveSessionState.Locked,
                "SessionLocked", SessionLockReason.IdleExpired.ToString());
            revocation = CreatePendingRevocationLocked(transition);
        }

        NotifyChanged(transition.Session);
        _ = PersistDetachedAsync(transition, revocation);
        return true;
    }

    private bool TryAcquireOperation(out long generation)
    {
        lock (_sync)
        {
            if (_disposed || _activeOperations >= MaximumConcurrentOperations)
            {
                generation = 0;
                return false;
            }

            _activeOperations++;
            generation = _generation;
            return true;
        }
    }

    private bool TryPrepareSignIn(
        out long generation,
        out TransitionRecord? replacement,
        out PendingRevocation? revocation,
        out bool notifyReplacement,
        out string failureReason)
    {
        lock (_sync)
        {
            generation = _generation;
            replacement = null;
            revocation = null;
            notifyReplacement = false;
            failureReason = "SessionServiceDisposed";
            if (_disposed)
                return false;

            if (_pendingRevocation is { InFlight: true })
            {
                failureReason = "SessionRevocationPending";
                return false;
            }

            if (_pendingRevocation is { InFlight: false, ActualResult: false } pending)
            {
                pending.InFlight = true;
                pending.ActualResult = null;
                pending.CommitTask = null;
                replacement = pending.Transition;
                revocation = pending;
                return true;
            }

            // A successful attempt normally clears the pending record from its completion
            // callback. Keep this branch defensive so a stale successful record can never
            // block a fresh provider authentication.
            if (_pendingRevocation is not null)
                _pendingRevocation = null;

            replacement = _current.State == InteractiveSessionState.Authenticated
                ? TransitionLocked(null, InteractiveSessionState.Unauthenticated,
                    "SessionLoggedOut", "SessionReplaced")
                : null;
            if (replacement is not null)
            {
                revocation = CreatePendingRevocationLocked(replacement);
                notifyReplacement = true;
                generation = _generation;
            }
            return true;
        }
    }

    private bool TryAcquireProviderOperation()
    {
        lock (_sync)
        {
            if (_disposed || _activeProviderOperations >= MaximumProviderOperations)
                return false;
            _activeProviderOperations++;
            return true;
        }
    }

    private void ReleaseProviderOperation()
    {
        lock (_sync)
        {
            if (_activeProviderOperations > 0)
                _activeProviderOperations--;
        }
    }

    private void ObserveProvider(Task providerTask)
    {
        _ = providerTask.ContinueWith(completed =>
        {
            _ = completed.Exception;
            ReleaseProviderOperation();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void DisposeProviderTokenWhenDone(
        Task providerTask, CancellationTokenSource linked)
    {
        if (providerTask.IsCompleted)
        {
            linked.Dispose();
            return;
        }

        _ = providerTask.ContinueWith(_ => linked.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ReleaseOperation()
    {
        lock (_sync)
        {
            if (_activeOperations > 0) _activeOperations--;
        }
    }

    private bool GenerationStillCurrent(long generation)
    {
        lock (_sync) return !_disposed && generation == _generation;
    }

    /// <summary>Acquired only inside the durable identity transaction. No asynchronous work
    /// is allowed while this short lease holds the session monitor.</summary>
    internal bool TryAcquireRecoveryLease(Guid? principalId, Guid? sessionId,
        out IDisposable? lease, out string reason)
    {
        lease = null;
        reason = "RecoverySessionConflict";
        if (Monitor.IsEntered(_sync) || !Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(50))) return false;
        var transferred = false;
        try
        {
            if (_disposed || Volatile.Read(ref _disposeRequested) != 0)
            { reason = "SessionServiceDisposed"; return false; }
            if (_activeOperations != 0 || _activeProviderOperations != 0 || _activePersistenceOperations != 0 ||
                _pendingRevocation is not null) return false;
            if (principalId is null && sessionId is null)
            {
                if (_current.State != InteractiveSessionState.Unauthenticated || _identity is not null) return false;
            }
            else if (principalId is null || principalId == Guid.Empty || sessionId is null || sessionId == Guid.Empty ||
                _current.State != InteractiveSessionState.Authenticated || _current.SessionId != sessionId ||
                _current.PrincipalId != principalId.Value.ToString("D") || _identity?.PrincipalId != principalId ||
                IsExpired(SafeTimestamp(), _lastActivityTimestamp)) return false;
            lease = new RecoverySessionLease(_sync);
            transferred = true;
            reason = "RecoverySessionLeaseAcquired";
            return true;
        }
        finally { if (!transferred) Monitor.Exit(_sync); }
    }

    private sealed class RecoverySessionLease : IDisposable
    {
        private object? _monitor;
        internal RecoverySessionLease(object monitor) => _monitor = monitor;
        public void Dispose()
        {
            var monitor = Volatile.Read(ref _monitor);
            if (monitor is null) return;
            if (!Monitor.IsEntered(monitor))
                throw new InvalidOperationException("RecoverySessionLeaseThreadMismatch");
            if (Interlocked.CompareExchange(ref _monitor, null, monitor) == monitor)
                Monitor.Exit(monitor);
        }
    }

    private bool IsDisposed()
    {
        lock (_sync) return _disposed;
    }

    /// <summary>
    /// Takes the session monitor for a short, synchronous authorization critical section.
    /// Callers must already own any durable store transaction before acquiring this lease;
    /// the lease intentionally contains no asynchronous or external work.
    /// </summary>
    internal bool TryAcquireAuthorizationLease(
        Guid principalId,
        Guid sessionId,
        out SessionAuthorizationLease? lease,
        out string reasonCode)
    {
        lease = null;
        reasonCode = "AuthorizationLeaseUnavailable";
        if (principalId == Guid.Empty)
        {
            reasonCode = "PrincipalIdRequired";
            return false;
        }

        if (sessionId == Guid.Empty)
        {
            reasonCode = "SessionIdRequired";
            return false;
        }

        // Monitor is re-entrant. Rejecting this path explicitly prevents a same-thread
        // nested lease from pretending to provide a second independent authorization scope.
        if (Monitor.IsEntered(_sync))
        {
            reasonCode = "AuthorizationLeaseReentrant";
            return false;
        }

        var entered = false;
        try
        {
            entered = Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(50));
            if (!entered)
            {
                reasonCode = "AuthorizationLeaseBusy";
                return false;
            }

            if (_disposed)
            {
                reasonCode = "SessionServiceDisposed";
                return false;
            }

            if (_current.State != InteractiveSessionState.Authenticated ||
                _current.SessionId != sessionId ||
                !string.Equals(_current.PrincipalId, principalId.ToString("D"),
                    StringComparison.Ordinal) ||
                _identity is null || _identity.PrincipalId != principalId)
            {
                reasonCode = "SessionMismatch";
                return false;
            }

            // An expired session is rejected here; the existing 250 ms idle monitor remains
            // responsible for the real Locked transition and its audit fact.
            if (IsExpired(SafeTimestamp(), _lastActivityTimestamp))
            {
                reasonCode = "SessionIdleExpired";
                return false;
            }

            if (_pendingRevocation is not null)
            {
                reasonCode = "SessionRevocationPending";
                return false;
            }

            lease = new SessionAuthorizationLease(_sync, _identity, sessionId);
            reasonCode = "AuthorizationLeaseAcquired";
            entered = false;
            return true;
        }
        finally
        {
            if (entered)
                Monitor.Exit(_sync);
        }
    }

    private bool TryTransition(Guid? expectedSessionId, InteractiveSessionState state,
        string kind, string reason, out Transition transition)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                transition = Transition.Failure(new SessionActionResult(false,
                    "SessionServiceDisposed", false));
                return false;
            }

            var currentId = _current.State == InteractiveSessionState.Authenticated
                ? _current.SessionId : null;
            if (expectedSessionId is not null && currentId != expectedSessionId)
            {
                transition = Transition.Failure(new SessionActionResult(false, "SessionMismatch", false));
                return false;
            }

            var record = TransitionLocked(expectedSessionId, state, kind, reason);
            transition = Transition.Success(record, CreatePendingRevocationLocked(record));
            return true;
        }
    }

    private TransitionRecord TransitionLocked(Guid? expectedSessionId,
        InteractiveSessionState state, string kind, string reason)
    {
        var previousPrincipal = _identity?.PrincipalId;
        var previousSession = _current.State == InteractiveSessionState.Authenticated
            ? _current.SessionId : null;
        _identity = null;
        _lastActivityTimestamp = 0;
        _generation = checked(_generation + 1);
        _current = new InteractiveSession(state, null, null);
        return new TransitionRecord(_current,
            new SessionAuditEvent(previousPrincipal, previousSession, kind, reason));
    }

    private ValueTask<bool> PersistSignInCancellationAsync(Guid principalId, Guid sessionId) =>
        PersistAuditAsync(new SessionAuditEvent(
            principalId, sessionId, "SessionSignInCancelled", "SessionOperationSuperseded"));

    private ValueTask<bool> PersistRevocationAsync(
        PendingRevocation? revocation, SessionAuditEvent audit)
    {
        if (revocation is null)
            return PersistAuditAsync(audit);

        return PersistAuditAsync(
            audit,
            actualCompletion: result => CompleteRevocationAttempt(revocation, result),
            taskCreated: task => SetRevocationTask(revocation, task));
    }

    private void SetRevocationTask(PendingRevocation revocation, Task<bool> task)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_pendingRevocation, revocation))
                revocation.CommitTask = task;
        }
    }

    private void CompleteRevocationAttempt(PendingRevocation revocation, bool succeeded)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pendingRevocation, revocation))
                return;

            revocation.InFlight = false;
            revocation.ActualResult = succeeded;
            if (succeeded || _disposed)
                _pendingRevocation = null;
        }
    }

    private PendingRevocation? CreatePendingRevocationLocked(TransitionRecord transition)
    {
        if (!transition.Event.PrincipalId.HasValue ||
            !transition.Event.SessionId.HasValue ||
            transition.Event.PrincipalId.Value == Guid.Empty ||
            transition.Event.SessionId.Value == Guid.Empty)
            return null;

        if (_pendingRevocation is not null)
            return _pendingRevocation;

        var revocation = new PendingRevocation(transition);
        _pendingRevocation = revocation;
        return revocation;
    }

    private bool TryAcquirePersistence()
    {
        lock (_sync)
        {
            if (_disposed || _activePersistenceOperations >= MaximumPersistenceOperations)
                return false;
            _activePersistenceOperations++;
            return true;
        }
    }

    private void ReleasePersistence()
    {
        lock (_sync)
        {
            if (_activePersistenceOperations > 0)
                _activePersistenceOperations--;
        }
    }

    private async ValueTask<bool> PersistAuditAsync(
        SessionAuditEvent audit,
        Action<bool>? actualCompletion = null,
        Action? lateSuccess = null,
        Action<Task<bool>>? taskCreated = null)
    {
        var observation = new PersistenceObservation(actualCompletion, lateSuccess);
        if (!TryAcquirePersistence())
        {
            observation.Complete(false);
            return false;
        }

        CancellationTokenSource? timeout = null;
        var releaseInContinuation = false;
        try
        {
            timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(PersistenceBound);
            var task = _persist(audit, timeout.Token).AsTask();
            releaseInContinuation = true;
            try
            {
                taskCreated?.Invoke(task);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The observer still owns release and completion. A bookkeeping callback
                // must never turn a real persistence attempt into an unobserved task.
            }

            ObservePersistence(task, timeout, observation);
            var completed = await Task.WhenAny(task, Task.Delay(PersistenceBound)).ConfigureAwait(false);
            if (completed != task)
            {
                observation.MarkTimedOut();
                try { timeout.Cancel(); }
                catch (ObjectDisposedException) { }
                return false;
            }

            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (!releaseInContinuation)
            {
                timeout?.Dispose();
                ReleasePersistence();
                observation.Complete(false);
            }
        }
    }

    private async Task PersistDetachedAsync(
        TransitionRecord transition, PendingRevocation? revocation)
    {
        try
        {
            _ = await PersistRevocationAsync(revocation, transition.Event).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private void ObservePersistence(
        Task<bool> task, CancellationTokenSource timeout, PersistenceObservation observation)
    {
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            var succeeded = false;
            if (completed.Status == TaskStatus.RanToCompletion)
            {
                try { succeeded = completed.Result; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }

            timeout.Dispose();
            ReleasePersistence();
            observation.Complete(succeeded);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ScheduleLateStartCancellation(Guid principalId, Guid sessionId)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
        }

        _ = PersistLateStartCancellationAsync(principalId, sessionId);
    }

    private async Task PersistLateStartCancellationAsync(Guid principalId, Guid sessionId)
    {
        try
        {
            _ = await PersistAuditAsync(new SessionAuditEvent(
                principalId, sessionId, "SessionSignInCancelled",
                "SessionStartPersistenceTimedOut")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private SessionSignInResult Failure(string reason) =>
        new(false, reason, null, Snapshot());

    private InteractiveSession Snapshot()
    {
        lock (_sync) return _current;
    }

    private long SafeTimestamp()
    {
        try { return _timestamp(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Stopwatch.GetTimestamp(); }
    }

    private bool IsExpired(long now, long last)
    {
        // Stopwatch timestamps are monotonic, but a deterministic test clock may start at
        // zero. Treat zero as a valid origin while remaining conservative on backwards jumps.
        if (now < last) return false;
        return now - last >= _idleTimeoutTicks;
    }

    private static long ToStopwatchTicks(TimeSpan timeout)
    {
        var ticks = timeout.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)Math.Ceiling(ticks));
    }

    private void NotifyChanged(InteractiveSession session)
    {
        var handler = Changed;
        if (handler is null) return;
        try { handler(this, new InteractiveSessionChangedEventArgs(session)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static void ValidateLockReason(SessionLockReason reason)
    {
        if (!Enum.IsDefined(typeof(SessionLockReason), reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
    }

    private sealed record TransitionRecord(InteractiveSession Session, SessionAuditEvent Event);

    private sealed class PendingRevocation
    {
        internal PendingRevocation(TransitionRecord transition) => Transition = transition;

        internal TransitionRecord Transition { get; }
        internal Task<bool>? CommitTask { get; set; }
        internal bool InFlight { get; set; } = true;
        internal bool? ActualResult { get; set; }
    }

    private sealed class PersistenceObservation
    {
        private readonly Action<bool>? _actualCompletion;
        private readonly Action? _lateSuccess;
        private int _completed;
        private int _succeeded;
        private int _timedOut;
        private int _lateSignalled;

        internal PersistenceObservation(Action<bool>? actualCompletion, Action? lateSuccess)
        {
            _actualCompletion = actualCompletion;
            _lateSuccess = lateSuccess;
        }

        internal void MarkTimedOut()
        {
            Volatile.Write(ref _timedOut, 1);
            if (Volatile.Read(ref _completed) != 0 &&
                Volatile.Read(ref _succeeded) != 0)
                SignalLateSuccess();
        }

        internal void Complete(bool succeeded)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            Volatile.Write(ref _succeeded, succeeded ? 1 : 0);
            try { _actualCompletion?.Invoke(succeeded); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }

            if (succeeded && Volatile.Read(ref _timedOut) != 0)
                SignalLateSuccess();
        }

        private void SignalLateSuccess()
        {
            if (Interlocked.Exchange(ref _lateSignalled, 1) != 0)
                return;

            try { _lateSuccess?.Invoke(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }

    private sealed record Transition(
        TransitionRecord? Record, PendingRevocation? Revocation, SessionActionResult Result)
    {
        public InteractiveSession Session => Record!.Session;
        public SessionAuditEvent Event => Record!.Event;
        public PendingRevocation? Pending => Revocation;

        public static Transition Success(TransitionRecord record, PendingRevocation? revocation) =>
            new(record, revocation, new SessionActionResult(true, "SessionTransitioned", false));

        public static Transition Failure(SessionActionResult result) => new(null, null, result);
    }

}

/// <summary>
/// Internal synchronous authorization lease. The owning thread must dispose it before
/// yielding; disposing from another thread is rejected so the session monitor cannot be
/// released by an unrelated continuation.
/// </summary>
internal sealed class SessionAuthorizationLease : IDisposable
{
    private readonly object _sync;
    private int _disposed;

    internal SessionAuthorizationLease(object sync, HumanIdentity identity, Guid sessionId)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        SessionId = sessionId;
    }

    internal HumanIdentity Identity { get; }
    internal Guid SessionId { get; }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!Monitor.IsEntered(_sync))
            throw new SynchronizationLockException(
                "The authorization lease must be disposed by its acquiring thread.");

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Monitor.Exit(_sync);
    }
}
