namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    // Schema 39 uses the approved publication as its admission threshold. Existing
    // exact, accepted work retains its separately bounded settlement reservations.
    private long _retentionWalLimit;

    private bool WalCapacityBlocksNewWork()
    {
        if (_options.StorageRetention is null) return _walLimitExceeded || GetWalLength() > MaximumWalBytes;
        var limit = Volatile.Read(ref _retentionWalLimit);
        if (limit == 0) return false; // Governance may install the first policy; production still lacks its mandatory gate.
        try
        {
            var bytes = _readWalLength(_databasePath! + "-wal");
            return bytes < 0 || bytes >= limit;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return true; }
    }
}
