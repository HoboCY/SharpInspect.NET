using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Recipes;

/// <summary>
/// A bounded, station-owned reservation for one governed Recipe selection change.
/// The reservation only reports the live station blocker, only publishes the
/// durably committed revision, and never touches the Ready or arm axes.
/// </summary>
internal sealed class RecipeSelectionRuntimeLease : IDisposable
{
    private readonly Func<string?>? _blocker;
    private readonly Func<RecipeSelectionRevision, bool>? _publish;
    private Action? _release;
    private int _disposed;

    internal RecipeSelectionRuntimeLease(Guid epoch, string? failure, Func<string?>? blocker = null,
        Func<RecipeSelectionRevision, bool>? publish = null, Action? release = null)
    {
        RuntimeEpoch = epoch;
        Failure = failure;
        _blocker = blocker;
        _publish = publish;
        _release = release;
    }

    internal Guid RuntimeEpoch { get; }

    /// <summary>The admission failure; a reserved lease keeps this null.</summary>
    internal string? Failure { get; }

    internal bool Available => Volatile.Read(ref _disposed) == 0 && Failure is null && _release is not null;

    internal string? GetBlocker() => !Available
        ? Failure ?? "RecipeSelectionReservationUnavailable"
        : _blocker?.Invoke();

    /// <summary>
    /// Installs the single durable revision into the runtime cache. The station
    /// callback owns the exact-predecessor check and reports false when the cache
    /// cannot be reconciled; a released reservation never publishes.
    /// </summary>
    internal bool Publish(RecipeSelectionRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return Available && _publish is not null && _publish(revision);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
