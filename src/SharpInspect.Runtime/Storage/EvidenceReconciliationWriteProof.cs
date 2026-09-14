namespace SharpInspect.Runtime.Storage;

// The same read connection observes data_version before verification and again while the
// sole writer owns BEGIN IMMEDIATE. Comparing versions from different connections is invalid.
internal sealed class EvidenceReconciliationWriteProof : IDisposable
{
    private readonly object _gate = new();
    private Action<StoreDeadline>? _verify;
    private Action? _release;
    internal EvidenceReconciliationWriteProof(EvidenceReconciliationReadState state,
        Action<StoreDeadline> verify, Action release)
    { State = state; _verify = verify; _release = release; }
    internal EvidenceReconciliationReadState State { get; }
    internal void RequireUnchanged(StoreDeadline deadline)
    {
        lock (_gate)
            (_verify ?? throw new InvalidOperationException("EvidenceReconciliationReadWitnessLost"))(deadline);
    }
    public void Dispose()
    {
        Action? release;
        lock (_gate) { release = _release; _release = null; _verify = null; }
        release?.Invoke();
    }
}
