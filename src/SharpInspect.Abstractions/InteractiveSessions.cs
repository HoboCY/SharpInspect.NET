namespace SharpInspect.Abstractions;

/// <summary>Why an authenticated interactive session was locked.</summary>
public enum SessionLockReason
{
    UserRequested,
    WindowHidden,
    IdleExpired,
    OperatingSystemLock
}

/// <summary>Outcome of one identity-provider-backed session sign-in.</summary>
public sealed record SessionSignInResult(
    bool Succeeded,
    string ReasonCode,
    HumanIdentity? Identity,
    InteractiveSession Session);

/// <summary>Outcome of a session lifecycle or activity action.</summary>
public sealed record SessionActionResult(bool Succeeded, string ReasonCode, bool AuditPersisted);

/// <summary>Immutable session projection delivered after a lifecycle transition.</summary>
public sealed class InteractiveSessionChangedEventArgs : EventArgs
{
    public InteractiveSessionChangedEventArgs(InteractiveSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public InteractiveSession Session { get; }
}

// Changed 是生命周期投影通知；敏感操作应重新读取服务当前会话，不能把事件参数当作授权证明。
/// <summary>
/// Runtime-owned interactive authentication state. The service never accepts a caller-supplied
/// HumanIdentity as proof of authentication; only the configured identity provider can create a
/// session.
/// </summary>
public interface IInteractiveSessionService : IAsyncDisposable
{
    /// <summary>Current immutable projection. Reading it never renews idle activity.</summary>
    InteractiveSession Current { get; }

    /// <summary>Raised after a lifecycle projection changes; handlers run outside service locks.</summary>
    event EventHandler<InteractiveSessionChangedEventArgs>? Changed;

    ValueTask<SessionSignInResult> SignInAsync(
        PasswordSignInRequest request, CancellationToken cancellationToken = default);

    ValueTask<InteractiveSession> GetSessionAsync(CancellationToken cancellationToken = default);

    // 锁屏和注销只改变交互会话，不直接停止 Runtime 或改写生产状态。
    ValueTask<SessionActionResult> LockAsync(
        Guid? expectedSessionId,
        SessionLockReason reason,
        CancellationToken cancellationToken = default);

    ValueTask<SessionActionResult> LogoutAsync(
        Guid? expectedSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Renews idle lifetime only for the exact current authenticated session.</summary>
    ValueTask<SessionActionResult> ReportActivityAsync(
        Guid sessionId, CancellationToken cancellationToken = default);
}
