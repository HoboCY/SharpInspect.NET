using System.Runtime.CompilerServices;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Integrity;

/// <summary>One in-flight call per adapter, including callbacks that ignore cancellation.</summary>
internal static class AuditAnchorClient
{
    private static readonly ConditionalWeakTable<IExternalAuditAnchor, SemaphoreSlim> Gates = new();

    internal static async Task<T> InvokeAsync<T>(IExternalAuditAnchor anchor, Func<CancellationToken, ValueTask<T>> operation,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var gate = Gates.GetValue(anchor, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("AuditAnchorBusy");
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout);
        var invocation = Task.Run(async () =>
        {
            try { return await operation(lifetime.Token).ConfigureAwait(false); }
            finally { lifetime.Dispose(); gate.Release(); }
        }, CancellationToken.None);
        // Observe a late exception after the bounded caller has already returned.
        _ = invocation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await invocation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}
