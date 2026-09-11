using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// Runtime-owned authority to drain and clear one exact Active selection. It grants no
/// identity or storage authority, and never owns the command gate while awaiting devices.
/// </summary>
internal sealed class RecipeRetirementRuntimeLease : IDisposable
{
    private Action? _release;
    private readonly Func<TimeSpan, CancellationToken, ValueTask<string?>>? _drain;
    private readonly Func<CancellationToken, ValueTask<RecipeActivationCommitLease>>? _enterCommit;
    private readonly Func<RecipeLifecycleRecord, bool>? _clear;
    private readonly Func<TimeSpan, ValueTask<string?>>? _cleanup;
    private readonly Action<string, bool>? _terminal;
    private int _disposed;

    internal RecipeRetirementRuntimeLease(Guid runtimeEpoch, string? failure, CancellationToken token,
        RecipeActivationReference? active = null,
        Func<TimeSpan, CancellationToken, ValueTask<string?>>? drain = null,
        Func<CancellationToken, ValueTask<RecipeActivationCommitLease>>? enterCommit = null,
        Func<RecipeLifecycleRecord, bool>? clear = null,
        Func<TimeSpan, ValueTask<string?>>? cleanup = null,
        Action<string, bool>? terminal = null, Action? release = null)
    {
        RuntimeEpoch = runtimeEpoch; Failure = failure; Token = token; Active = active;
        _drain = drain; _enterCommit = enterCommit; _clear = clear;
        _cleanup = cleanup; _terminal = terminal; _release = release;
    }

    internal Guid RuntimeEpoch { get; }
    internal RecipeActivationReference? Active { get; }
    internal string? Failure { get; }
    internal CancellationToken Token { get; }
    internal bool Available => Volatile.Read(ref _disposed) == 0 && Failure is null && _release is not null;

    internal ValueTask<string?> DrainAsync(TimeSpan budget, CancellationToken token) =>
        Available && _drain is not null ? _drain(budget, token) :
            ValueTask.FromResult<string?>(Failure ?? "RecipeRetirementReservationUnavailable");

    internal ValueTask<RecipeActivationCommitLease> EnterCommitAsync(CancellationToken token) =>
        Available && _enterCommit is not null ? _enterCommit(token) :
            ValueTask.FromResult(new RecipeActivationCommitLease(() =>
                Failure ?? "RecipeRetirementReservationUnavailable"));

    internal bool ClearCommitted(RecipeLifecycleRecord record) => Available && _clear?.Invoke(record) == true;

    internal ValueTask<string?> CleanupAsync(TimeSpan budget) =>
        Available && _cleanup is not null ? _cleanup(budget) :
            ValueTask.FromResult<string?>(Failure ?? "RecipeRetirementReservationUnavailable");

    internal void PublishTerminal(string reason, bool recoveryRequired)
    { if (Available) _terminal?.Invoke(reason, recoveryRequired); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
