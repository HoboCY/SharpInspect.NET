using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace SharpInspect.Wpf;

public enum SnapshotFreshness
{
    Unavailable,
    Fresh,
    Stale,
    Discontinuous
}

/// <summary>Safe local presentation result; it is not a Runtime command rejection.</summary>
public sealed class PresentationStateUnavailableException : InvalidOperationException
{
    public const string SafeReasonCode = "PresentationStateUnavailable";

    public PresentationStateUnavailableException() : base(SafeReasonCode) { }

    public string ReasonCode => SafeReasonCode;
}

/// <summary>Uses a local monotonic source for presentation age; wall clock changes cannot make a snapshot fresh.</summary>
public interface IMonotonicClock
{
    long GetTimestamp();
    TimeSpan ElapsedSince(long timestamp);
}

public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan ElapsedSince(long timestamp)
    {
        var elapsed = Stopwatch.GetTimestamp() - timestamp;
        return TimeSpan.FromSeconds((double)elapsed / Stopwatch.Frequency);
    }
}

/// <summary>The only UI-thread dependency of the adapter, replaceable by a controlled dispatcher in tests.</summary>
public interface IUiDispatcher
{
    bool CheckAccess { get; }
    ValueTask InvokeAsync(Action action);
}

public sealed class DispatcherUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public DispatcherUiDispatcher(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public bool CheckAccess => _dispatcher.CheckAccess();

    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess)
        {
            action();
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_dispatcher.InvokeAsync(action).Task);
    }
}

internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess => true;

    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return ValueTask.CompletedTask;
    }
}

public sealed record SnapshotFreshnessPolicy
{
    public SnapshotFreshnessPolicy(TimeSpan maximumAge, TimeSpan? checkInterval = null)
    {
        if (maximumAge < TimeSpan.FromMilliseconds(20) || maximumAge > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Freshness age must be between 20 ms and 60 s.");
        var interval = checkInterval ?? TimeSpan.FromMilliseconds(Math.Min(250, Math.Max(20, maximumAge.TotalMilliseconds / 4)));
        var maximumInterval = TimeSpan.FromMilliseconds(Math.Min(1000, maximumAge.TotalMilliseconds));
        if (interval < TimeSpan.FromMilliseconds(20) || interval > maximumInterval)
            throw new ArgumentOutOfRangeException(nameof(checkInterval));
        MaximumAge = maximumAge;
        CheckInterval = interval;
    }

    public TimeSpan MaximumAge { get; }
    public TimeSpan CheckInterval { get; }

    public static SnapshotFreshnessPolicy Default { get; } =
        new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250));
}
