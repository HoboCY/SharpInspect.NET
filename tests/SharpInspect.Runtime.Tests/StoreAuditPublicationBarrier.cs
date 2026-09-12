using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

internal static class StoreAuditPublicationBarrier
{
    internal static async Task<T> AssertPublishedBeforeCompletionAsync<T>(SqliteCommandStore store,
        Func<Task<T>> write, Func<long> readCommittedRows, long expectedRows)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (store.Integrity?.State == AuditIntegrityState.Verifying)
            await Task.Delay(20, deadline.Token);
        Assert.Equal(AuditIntegrityState.Verified, store.Integrity?.State);
        var previousThrough = store.Integrity!.ThroughSequence;

        // A scheduling barrier only: the real writer, COMMIT and signed audit
        // remain intact. Hold and release Monitor on the same dedicated thread.
        var gate = typeof(SqliteCommandStore).GetField("_integrityGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Factory.StartNew(() =>
        {
            lock (gate)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Audit publication barrier was not released");
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<T>? pending = null;
        var completedBeforePublication = false;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            pending = write();
            // An independent SQLite reader proves the transaction has committed;
            // task scheduling alone is not the observation under test.
            while (readCommittedRows() < expectedRows)
                await Task.Delay(10, deadline.Token);
            Assert.Equal(expectedRows, readCommittedRows());
            Assert.Equal(AuditIntegrityState.Verified, store.Integrity?.State);
            Assert.Equal(previousThrough, store.Integrity!.ThroughSequence);
            // The negative observation must elapse in full. A cancelled shared
            // setup deadline cannot substitute for observing a pending caller.
            completedBeforePublication = await Task.WhenAny(pending, Task.Delay(1000)) == pending;
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(completedBeforePublication,
            "The committed write returned success while callers could still read the previous Verified audit report.");
        return await pending!;
    }
}
