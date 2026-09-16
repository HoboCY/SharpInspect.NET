using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Images;

/// <summary>The protected exact file survives both ledger commits and every outstanding native call.</summary>
internal sealed class EvidenceDeletionClaim : IDisposable
{
    private readonly object _sync = new();
    private readonly EvidenceRetentionFiles _issuer;
    private FileStream? _file;
    private List<SafeFileHandle>? _roots;
    private Action? _release;
    private bool _disposeRequested;
    private bool _physical;
    private bool _deleted;
    private bool _deleteStarted;

    internal EvidenceDeletionClaim(EvidenceRetentionFiles issuer, EvidenceRetentionObligation obligation,
        EvidenceDeletionFile descriptor, FileStream? file, List<SafeFileHandle> roots, Action release)
    {
        _issuer = issuer; Obligation = obligation; Descriptor = descriptor; _file = file; _roots = roots;
        _release = release; Missing = file is null;
    }
    internal EvidenceRetentionObligation Obligation { get; }
    internal EvidenceDeletionFile Descriptor { get; }
    internal bool Missing { get; }
    internal bool Deleted { get { lock (_sync) return _deleted; } }
    internal bool DeleteStarted { get { lock (_sync) return _deleteStarted; } }
    internal FileStream File { get { lock (_sync) return _file ?? throw new InvalidOperationException("RetentionFileNotOpen"); } }

    internal void RequireOwner(EvidenceRetentionFiles owner)
    {
        if (!ReferenceEquals(owner, _issuer)) throw new InvalidOperationException("RetentionFileClaimOwnerMismatch");
        VerifyCommitProtection();
    }

    // Writer callback: memory and retained-handle state only. No filesystem IO.
    internal void VerifyCommitProtection()
    {
        lock (_sync)
        {
            if (_disposeRequested || _roots is null || _roots.Any(value => value.IsClosed || value.IsInvalid) ||
                (!Missing && !_deleted && (_file is null || _file.SafeFileHandle.IsClosed)))
                throw new InvalidOperationException("RetentionFileProtectionLost");
        }
    }

    internal IDisposable EnterPhysical()
    {
        lock (_sync)
        {
            VerifyCommitProtection();
            if (_physical || _deleteStarted || Missing) throw new InvalidOperationException("RetentionDeletionNotStartable");
            _physical = true;
            return new Lease(this);
        }
    }

    internal void MarkDeleteStarted() { lock (_sync) _deleteStarted = true; }

    internal void CloseDeletedFile()
    {
        // Close the handle that received the native delete disposition. No pathname
        // lookup can replace the subject between authorization and this operation.
        FileStream file;
        lock (_sync) file = _file ?? throw new InvalidOperationException("RetentionFileNotOpen");
        file.Dispose();
        lock (_sync) { _file = null; _deleted = true; }
    }

    public void Dispose()
    {
        Action? cleanup;
        lock (_sync) { _disposeRequested = true; cleanup = TakeCleanup(); }
        cleanup?.Invoke();
    }
    private void Retire()
    {
        Action? cleanup;
        lock (_sync) { _physical = false; cleanup = TakeCleanup(); }
        cleanup?.Invoke();
    }
    private Action? TakeCleanup()
    {
        if (!_disposeRequested || _physical || _release is null) return null;
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
        private EvidenceDeletionClaim? _owner;
        internal Lease(EvidenceDeletionClaim owner) => _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Retire();
    }
}
