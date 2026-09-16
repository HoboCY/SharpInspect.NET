using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record EvidenceRetentionReadState(RetentionConfiguration Configuration,
    IReadOnlyList<EvidenceRetentionStoredRow> Rows, EvidenceRetentionReplay Replay,
    long AuditSequence, string AuditHash, long PayloadBytes, long? ControlFactsUsed = null);

internal sealed class EvidenceRetentionWriteProof : IDisposable
{
    private Action? _dispose;
    private readonly Action<StoreDeadline> _verify;
    internal EvidenceRetentionWriteProof(EvidenceRetentionReadState state, Action<StoreDeadline> verify, Action dispose)
    { State = state; _verify = verify; _dispose = dispose; }
    internal EvidenceRetentionReadState State { get; }
    internal void RequireUnchanged(StoreDeadline deadline)
    {
        if (_dispose is null) throw new ObjectDisposedException(nameof(EvidenceRetentionWriteProof));
        _verify(deadline);
    }
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}

/// <summary>Bounded read-only retention history, verified against its immutable sources and signed central audit.</summary>
public sealed partial class SqliteEvidenceRetentionQuery : IEvidenceRetentionQuery
{
    private readonly ProductionStoreOptions _options;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _outstanding;
    internal Action? AfterSnapshotRead { get; set; }
    public SqliteEvidenceRetentionQuery(ProductionStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<EvidenceRetentionSnapshot> ReadAsync(EvidenceRetentionFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (_options.StorageRetention is not { } options) return Unavailable("RetentionConfigurationRequired");
        if (filter.AfterPosition < 0 || filter.ThroughPosition is < 0 || filter.PageSize < 1 ||
            filter.PageSize > options.MaximumPageSize || filter.Owner is { } owner &&
            (!Enum.IsDefined(owner.Kind) || owner.OwnerId == Guid.Empty)) return Unavailable("RetentionPageInvalid");
        try
        {
            using var proof = await AcquireSnapshotAsync(writerAuthority: false, cancellationToken).ConfigureAwait(false);
            var state = proof.State;
            var through = filter.ThroughPosition ?? state.Rows.Count;
            if (through > state.Rows.Count || filter.AfterPosition > through) return Unavailable("RetentionCursorInvalid");
            var replay = new EvidenceRetentionReplay();
            foreach (var row in state.Rows.Where(x => x.Position <= through)) replay.Apply(row);
            var selected = state.Rows.Where(x => x.Position > filter.AfterPosition && x.Position <= through &&
                (filter.Owner is null || filter.Owner == x.Payload.Owner)).Take(filter.PageSize + 1).ToArray();
            var records = selected.Take(filter.PageSize).Select(x => x.ToRecord()).ToArray();
            var owners = filter.Owner is { } requested ? new[] { requested } : records.Select(x => x.Owner).Distinct().ToArray();
            var subjects = owners.Where(replay.Subjects.ContainsKey).Select(x => replay.Subjects[x].ToStatus()).ToArray();
            return new(true, "RetentionHistoryAvailable", Array.AsReadOnly(records), Array.AsReadOnly(subjects),
                through, selected.Length > filter.PageSize ? records[^1].Position : null,
                replay.Subjects.Values.Sum(x => (long)x.ActiveHolds.Count),
                replay.Subjects.Values.LongCount(x => x.DeleteIntent is not null),
                replay.Subjects.Values.LongCount(x => x.DeleteUnknown),
                replay.Subjects.Values.LongCount(x => x.Tombstone is not null),
                replay.Subjects.Values.Where(x => x.Tombstone is not null).Sum(x => x.Obligation.ByteLength),
                state.Configuration.ExecutionPolicy(), state.Configuration.RecoveryBudget(), state.ControlFactsUsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return Unavailable(SqliteAuditIntegrityQuery.FaultReason(error, "RetentionQueryUnavailable")); }
    }

    internal ValueTask<EvidenceRetentionWriteProof> AcquireWriteProofAsync(CancellationToken token) =>
        AcquireSnapshotAsync(writerAuthority: true, token);

    private async ValueTask<EvidenceRetentionWriteProof> AcquireSnapshotAsync(bool writerAuthority, CancellationToken token)
    {
        if (Interlocked.Increment(ref _outstanding) > 32)
        { Interlocked.Decrement(ref _outstanding); throw new InvalidOperationException("RetentionQueryCapacityExceeded"); }
        var entered = false; var transferred = false;
        try
        {
            var deadline = new StoreDeadline(_options.QueryTimeout);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("RetentionQueryDeadlineExceeded");
            entered = await Slots.WaitAsync(remaining, token).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("RetentionQueryDeadlineExceeded");
            var proof = await Task.Run(() => CreateProof(deadline, token, writerAuthority), CancellationToken.None).ConfigureAwait(false);
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

    private EvidenceRetentionWriteProof CreateProof(StoreDeadline deadline, CancellationToken token, bool writerAuthority)
    {
        if (_options.StorageRetention is null) throw new InvalidOperationException("RetentionConfigurationRequired");
        if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
        var connection = SqliteNative.Open(path, readOnly: true);
        var transferred = false;
        try
        {
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            var witness = AuditChainDatabase.Scalar(database, "PRAGMA data_version;", deadline);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, token);
            SqliteCommandStore.RequireConfiguredRetention(database, _options, deadline);
            SqliteCommandStore.VerifyEvidenceReconciliationReadGuard(database, _options, deadline);
            var config = SqliteCommandStore.ReadRetentionConfiguration(database, deadline);
            var rows = SqliteCommandStore.ReadRetentionRows(database, config, deadline);
            var replay = new EvidenceRetentionReplay();
            foreach (var row in rows) replay.Apply(row);
            var tail = AuditChainDatabase.Tail(database, deadline);
            var state = new EvidenceRetentionReadState(config, rows, replay, tail.Sequence, tail.Hash,
                rows.Sum(x => (long)EvidenceRetentionCodec.Encode(x.Payload, config.MaximumPayloadBytes).Length),
                SqliteCommandStore.ReadStorageRecoveryControlFacts(database, deadline));
            AfterSnapshotRead?.Invoke();
            SqliteNative.Execute(database, "COMMIT;", deadline, token);
            void Verify(StoreDeadline bounded)
            {
                EvidenceRetentionCodec.Require(writerAuthority, "ReadSnapshotNotWriteAuthority");
                EvidenceRetentionCodec.Require(
                    AuditChainDatabase.Scalar(database, "PRAGMA data_version;", bounded) == witness, "VerifiedSnapshotChanged");
            }
            if (writerAuthority) Verify(deadline);
            var proof = new EvidenceRetentionWriteProof(state, Verify, () =>
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

    private static EvidenceRetentionSnapshot Unavailable(string reason) =>
        new(false, reason, Array.Empty<EvidenceRetentionRecord>(), Array.Empty<EvidenceRetentionStatus>(),
            0, null, 0, 0, 0, 0, 0);
}
