using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;

namespace SharpInspect.Runtime.Recipes;

/// <summary>A reservation, not ownership of the command gate across provider calls.</summary>
internal sealed class RecipeActivationRuntimeLease : IDisposable
{
    private Action? _release;
    private readonly Func<CancellationToken, ValueTask<RecipeActivationCommitLease>>? _enterCommit;
    private readonly Func<RecipeActivationSnapshot, PreparedAlgorithm, RecipeActivationResourceInstallResult>? _install;
    private readonly Action<string, bool>? _publishTerminal;
    private readonly Func<string?>? _blocker;
    private readonly Func<RecipeActivationPhysicalPhaseClaim>? _beginPhysicalPhase;
    private int _disposed;
    internal RecipeActivationRuntimeLease(Guid epoch, string? failure, CancellationToken token,
        CameraSetupRuntime? camera = null,
        Func<CancellationToken, ValueTask<RecipeActivationCommitLease>>? enterCommit = null,
        Func<RecipeActivationSnapshot, PreparedAlgorithm, RecipeActivationResourceInstallResult>? install = null,
        Action<string, bool>? publishTerminal = null, Action? release = null, Func<string?>? blocker = null,
        Func<RecipeActivationPhysicalPhaseClaim>? beginPhysicalPhase = null)
    {
        RuntimeEpoch = epoch; Failure = failure; Token = token; Camera = camera;
        _enterCommit = enterCommit; _install = install; _publishTerminal = publishTerminal; _release = release;
        _blocker = blocker; _beginPhysicalPhase = beginPhysicalPhase;
    }
    internal Guid RuntimeEpoch { get; }
    internal string? Failure { get; }
    internal bool Available => Volatile.Read(ref _disposed) == 0 && Failure is null && _release is not null;
    internal CancellationToken Token { get; }
    internal CameraSetupRuntime? Camera { get; }
    internal string? GetBlocker() => !Available ? Failure ?? "RecipeActivationReservationUnavailable" : _blocker?.Invoke();
    internal async ValueTask<string?> WaitForBlockerAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocker = GetBlocker();
            if (!IsRuntimeBusy(blocker)) return blocker;
            var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) /
                (double)Stopwatch.Frequency);
            var remaining = budget - elapsed;
            if (remaining <= TimeSpan.Zero) return blocker;
            var delay = remaining > TimeSpan.FromMilliseconds(10)
                ? TimeSpan.FromMilliseconds(10) : remaining;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
    internal static bool IsRuntimeBusy(string? reason) =>
        string.Equals(reason, "RecipeActivationRuntimeBusy", StringComparison.Ordinal);
    internal RecipeActivationPhysicalPhaseClaim TryBeginPhysicalPhase() =>
        !Available || _beginPhysicalPhase is null
            ? RecipeActivationPhysicalPhaseClaim.Unavailable(Failure ?? "RecipeActivationReservationUnavailable")
            : _beginPhysicalPhase();
    internal ValueTask<RecipeActivationCommitLease> EnterCommitAsync(CancellationToken token) =>
        !Available || _enterCommit is null ? ValueTask.FromResult(new RecipeActivationCommitLease(() => Failure ??
            "RecipeActivationReservationUnavailable")) : _enterCommit(token);
    internal RecipeActivationResourceInstallResult Install(RecipeActivationSnapshot snapshot, PreparedAlgorithm prepared) =>
        !Available || _install is null ? new(false, null) : _install(snapshot, prepared);
    internal void PublishTerminal(string reason, bool recoveryRequired)
    { if (Available) _publishTerminal?.Invoke(reason, recoveryRequired); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// A short, station-owned claim covering one candidate physical phase.  The
/// station controls admission and releases only the matching phase id.
/// </summary>
internal sealed class RecipeActivationPhysicalPhaseClaim : IDisposable
{
    private Action? _release;

    private RecipeActivationPhysicalPhaseClaim(bool available, string? failure, long phaseId,
        Action? release)
    {
        Available = available;
        Failure = failure;
        PhaseId = phaseId;
        _release = release;
    }

    internal bool Available { get; }
    internal string? Failure { get; }
    internal long PhaseId { get; }

    internal static RecipeActivationPhysicalPhaseClaim Granted(long phaseId, Action release) =>
        new(true, null, phaseId, release);

    internal static RecipeActivationPhysicalPhaseClaim Unavailable(string reason) =>
        new(false, reason, 0, null);

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal sealed record RecipeActivationResourceInstallResult(bool Installed, PreparedAlgorithm? Previous);

internal sealed class RecipeActivationCommitLease : IDisposable
{
    private readonly Func<string?> _blocker;
    private readonly Func<string?>? _claim;
    private Action? _release;
    private int _disposed;
    internal RecipeActivationCommitLease(Func<string?> blocker, Action? release = null, Func<string?>? claim = null)
    { _blocker = blocker; _release = release; _claim = claim; }
    internal string? GetBlocker() => Volatile.Read(ref _disposed) != 0 ? "RecipeActivationCommitLeaseReleased" : _blocker();
    // The writer calls this at its final commit guard. Whichever wins the Runtime lock,
    // this claim or local Stop, establishes the order before durable publication.
    internal string? TryBeginCommit() => Volatile.Read(ref _disposed) != 0 ? "RecipeActivationCommitLeaseReleased" :
        _claim is null ? _blocker() : _claim();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
