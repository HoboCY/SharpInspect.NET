using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

public sealed partial class SqliteEvidenceReconciliationQuery
{
    internal async ValueTask<EvidenceReconciliationWriteProof> AcquireWriteProofAsync(CancellationToken token)
    {
        if (Interlocked.Increment(ref _outstanding) > 32)
        {
            Interlocked.Decrement(ref _outstanding);
            throw new InvalidOperationException("EvidenceReconciliationQueryCapacityExceeded");
        }
        var entered = false;
        var transferred = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            entered = await Slots.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("EvidenceReconciliationQueryDeadlineExceeded");
            var proof = await Task.Run(() => CreateWriteProof(deadline, token), CancellationToken.None).ConfigureAwait(false);
            transferred = true;
            return proof;
        }
        finally
        {
            if (!transferred)
            {
                if (entered) Slots.Release();
                Interlocked.Decrement(ref _outstanding);
            }
        }
    }
    private EvidenceReconciliationWriteProof CreateWriteProof(StoreDeadline deadline, CancellationToken token)
    {
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason))
            throw new InvalidOperationException(reason);
        var connection = SqliteNative.Open(path, readOnly: true);
        var transferred = false;
        try
        {
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            var witness = AuditChainDatabase.Scalar(database, "PRAGMA data_version;", deadline);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, token);
            SqliteCommandStore.VerifyEvidenceReconciliationReadGuard(database, _options, deadline);
            var configuration = SqliteCommandStore.ReadEvidenceReconciliationConfiguration(database, deadline);
            var rows = SqliteCommandStore.ReadEvidenceReconciliationRows(database, configuration, deadline);
            var replay = new EvidenceReconciliationReplay();
            foreach (var row in rows) replay.Apply(row);
            var tail = AuditChainDatabase.Tail(database, deadline);
            var state = new EvidenceReconciliationReadState(configuration, rows, replay, tail.Sequence, tail.Hash,
                rows.Sum(x => (long)EvidenceReconciliationStorageCodec.Encode(x.Payload, configuration.MaximumPayloadBytes).Length));
            SqliteNative.Execute(database, "COMMIT;", deadline, token);
            void Verify(StoreDeadline bounded)
            {
                AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA data_version;", bounded) == witness,
                    "EvidenceReconciliationVerifiedSnapshotChanged");
            }
            Verify(deadline);
            var proof = new EvidenceReconciliationWriteProof(state, Verify, () =>
            {
                try { connection.Dispose(); }
                finally { Slots.Release(); Interlocked.Decrement(ref _outstanding); }
            });
            transferred = true;
            return proof;
        }
        finally
        {
            if (!transferred)
            {
                try { raw.sqlite3_exec(connection.Handle!, "ROLLBACK;"); }
                finally { connection.Dispose(); }
            }
        }
    }
}
