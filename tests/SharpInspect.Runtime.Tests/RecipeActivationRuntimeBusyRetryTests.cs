using System.Diagnostics;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeActivationRuntimeBusyRetryTests
{
    [Fact]
    public async Task V142_L01_RuntimeLeaseRetriesOnlyExactRuntimeBusy()
    {
        var calls = 0;
        using var lease = new RecipeActivationRuntimeLease(Guid.NewGuid(), null,
            CancellationToken.None, release: () => { }, blocker: () =>
                Interlocked.Increment(ref calls) < 3 ? "RecipeActivationRuntimeBusy" : null);

        var blocker = await lease.WaitForBlockerAsync(TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.Null(blocker);
        Assert.True(calls >= 3);
    }

    [Fact]
    public async Task V142_L02_RuntimeLeaseDoesNotRetryOtherBlocker()
    {
        var calls = 0;
        using var lease = new RecipeActivationRuntimeLease(Guid.NewGuid(), null,
            CancellationToken.None, release: () => { }, blocker: () =>
                Interlocked.Increment(ref calls) == 1 ? "RecipeActivationExecutionConflict" : null);

        var blocker = await lease.WaitForBlockerAsync(TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.Equal("RecipeActivationExecutionConflict", blocker);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task V142_L03_RuntimeLeaseStopsAtBusyRetryBudget()
    {
        using var lease = new RecipeActivationRuntimeLease(Guid.NewGuid(), null,
            CancellationToken.None, release: () => { }, blocker: () =>
                "RecipeActivationRuntimeBusy");
        var started = Stopwatch.GetTimestamp();

        var blocker = await lease.WaitForBlockerAsync(TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        var elapsed = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) /
            (double)Stopwatch.Frequency);
        Assert.Equal("RecipeActivationRuntimeBusy", blocker);
        Assert.True(elapsed < TimeSpan.FromSeconds(1));
    }
}
