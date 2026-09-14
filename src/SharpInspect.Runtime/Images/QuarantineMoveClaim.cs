using Microsoft.Win32.SafeHandles;

namespace SharpInspect.Runtime.Images;

/// <summary>Retains the opened file and its ancestor protections until every physical call retires.</summary>
internal sealed class QuarantineMoveClaim : IDisposable
{
    private readonly object _gate = new();
    private readonly EvidenceQuarantine _owner;
    private FileStream? _file;
    private List<SafeFileHandle>? _roots;
    private Action? _release;
    private bool _disposeRequested;
    private int _physical;
    private bool _moved;

    internal QuarantineMoveClaim(EvidenceQuarantine owner, QuarantinedFileDescriptor descriptor,
        FileStream file, List<SafeFileHandle> roots, bool moved, Action release)
    {
        _owner = owner; Descriptor = descriptor; _file = file; _roots = roots; _moved = moved; _release = release;
    }
    internal QuarantinedFileDescriptor Descriptor { get; }
    internal FileStream File { get { lock (_gate) return _file ?? throw new ObjectDisposedException(nameof(QuarantineMoveClaim)); } }
    internal bool Moved { get { lock (_gate) return _moved; } }

    internal void RequireOwner(EvidenceQuarantine owner)
    {
        if (!ReferenceEquals(owner, _owner)) throw new InvalidOperationException("EvidenceQuarantineClaimOwnerMismatch");
        VerifyCommitProtection();
    }
    // This is the only callback supplied to the SQLite writer; it performs no filesystem IO.
    internal void VerifyCommitProtection()
    {
        lock (_gate)
        {
            if (_disposeRequested || _file is null || _file.SafeFileHandle.IsClosed ||
                _roots is null || _roots.Any(x => x.IsClosed || x.IsInvalid))
                throw new InvalidOperationException("EvidenceQuarantineProtectionLost");
        }
    }
    internal void MarkMoved() { lock (_gate) _moved = true; }
    internal IDisposable EnterPhysical()
    {
        lock (_gate)
        {
            VerifyCommitProtection();
            if (_physical != 0) throw new InvalidOperationException("EvidenceQuarantinePhysicalOperationPending");
            _physical = 1;
            return new Lease(this);
        }
    }
    public void Dispose()
    {
        Action? cleanup;
        lock (_gate) { _disposeRequested = true; cleanup = TakeCleanup(); }
        cleanup?.Invoke();
    }
    private void Retire()
    {
        Action? cleanup;
        lock (_gate) { _physical--; cleanup = TakeCleanup(); }
        cleanup?.Invoke();
    }
    private Action? TakeCleanup()
    {
        if (!_disposeRequested || _physical != 0 || _release is null) return null;
        var file = _file; var roots = _roots!; var release = _release;
        _file = null; _roots = null; _release = null;
        return () =>
        {
            try { file?.Dispose(); foreach (var root in roots) root.Dispose(); }
            finally { release(); }
        };
    }
    private sealed class Lease : IDisposable
    {
        private QuarantineMoveClaim? _owner;
        internal Lease(QuarantineMoveClaim owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Retire();
    }
}
