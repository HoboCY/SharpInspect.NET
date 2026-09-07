using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Algorithms;

/// <summary>Outcome of recording development computation history; it is not publication admission.</summary>
public sealed record AlgorithmResultArchiveResult(bool Recorded, string ReasonCode, Guid RecordId);

/// <summary>
/// Archives only an engine-produced, successful development computation. The single store writer
/// assigns its durable position and timestamp. Register through AddSharpInspectSqliteRuntime.
/// </summary>
public sealed class AlgorithmResultArchive
{
    public const int MaximumOutstandingRequests = 4;
    private readonly SqliteCommandStore _store;
    private readonly SemaphoreSlim _admission = new(MaximumOutstandingRequests, MaximumOutstandingRequests);
    internal AlgorithmResultArchive(SqliteCommandStore store) => _store = store;

    public async ValueTask<AlgorithmResultArchiveResult> AppendAsync(Guid recordId,
        AlgorithmExecutionOutcome outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = new StoreDeadline(_store.CommitTimeout);
        if (!_admission.Wait(0))
            return new(false, "AlgorithmResultArchiveCapacityExceeded", recordId);
        try
        {
            if (!AlgorithmResultStorageCodec.TryEncode(recordId, outcome, out var document, out var reasonCode))
                return new(false, reasonCode, recordId);
            var result = await _store.AppendAlgorithmResultAsync(document!,
                deadline, cancellationToken).ConfigureAwait(false);
            return new(result.Committed, result.ReasonCode, recordId);
        }
        finally { _admission.Release(); }
    }
}
