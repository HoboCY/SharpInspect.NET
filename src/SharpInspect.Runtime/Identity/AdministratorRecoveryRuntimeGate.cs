namespace SharpInspect.Runtime.Identity;

/// <summary>Internal operational authority; no request or UI-supplied safety assertion is accepted.</summary>
internal interface IAdministratorRecoveryRuntimeGate
{
    string? GetBlocker();
    ValueTask<AdministratorRecoveryRuntimeLease> EnterAsync(CancellationToken cancellationToken);
}

internal sealed class UnavailableAdministratorRecoveryRuntimeGate : IAdministratorRecoveryRuntimeGate
{
    public string? GetBlocker() => "RecoveryRuntimeAuthorityUnavailable";
    public ValueTask<AdministratorRecoveryRuntimeLease> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AdministratorRecoveryRuntimeLease(Guid.Empty, GetBlocker));
    }
}

internal sealed class AdministratorRecoveryRuntimeLease : IDisposable
{
    private Action? _release;
    private int _disposed;
    private readonly Func<string?> _recheck;
    internal AdministratorRecoveryRuntimeLease(Guid runtimeEpoch, Func<string?> recheck, Action? release = null)
    { RuntimeEpoch = runtimeEpoch; _recheck = recheck; _release = release; }
    internal Guid RuntimeEpoch { get; }
    internal string? Check() => Volatile.Read(ref _disposed) != 0 ? "RecoveryRuntimeLeaseExpired" : _recheck();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
