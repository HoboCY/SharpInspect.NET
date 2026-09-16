using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Evidence;

internal sealed partial class EvidenceReconciliationWorker
{
    private EvidenceRetentionReadState? _retentionState;
    private EvidenceRetentionFiles? _retentionFiles;

    private async Task ReadRetentionStateAsync(CancellationToken token)
    {
        if (_storeOptions.StorageRetention is null) return;
        _retentionState = await new SqliteEvidenceRetentionQuery(_storeOptions).ReadStateAsync(token).ConfigureAwait(false);
        _retentionFiles ??= new(_storeOptions);
    }

    private async Task<EvidenceDeletionClaim?> VerifyRetainedAbsenceAsync(EvidenceRetentionOwner owner,
        PendingImageManifest? manifest, CancellationToken token)
    {
        if (_retentionState is null || !_retentionState.Replay.Subjects.TryGetValue(owner, out var subject) || subject.Tombstone is null)
            return null;
        var claim = await _retentionFiles!.PrepareAsync(subject.Obligation, manifest, subject.Tombstone.Payload.File,
            _options.FileTimeout, token).ConfigureAwait(false);
        if (!claim.Missing)
        {
            claim.Dispose();
            throw new InvalidOperationException("RetentionTombstonedFileReappeared");
        }
        return claim;
    }

    private async Task RetireEvidenceFilesAsync()
    {
        var tasks = _quarantines.Select(value => value.PhysicalCompletion)
            .Concat(_files is null ? Array.Empty<Task>() : new[] { _files.PhysicalCompletion })
            .Concat(_retentionFiles is null ? Array.Empty<Task>() : new[] { _retentionFiles.PhysicalCompletion });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}
