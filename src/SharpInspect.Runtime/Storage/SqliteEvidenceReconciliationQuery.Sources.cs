using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Storage;

public sealed partial class SqliteEvidenceReconciliationQuery
{
    internal ValueTask<IReadOnlyList<EvidenceReconciliationSubject>> ReadSourcesAsync(
        EvidenceReconciliationSubjectKind kind, long after, long through, int limit, CancellationToken token, bool pendingOnly = false)
    {
        if (kind is not (EvidenceReconciliationSubjectKind.Image or EvidenceReconciliationSubjectKind.Outbox) ||
            after < 0 || through < after || limit is < 1 or > 512) throw new ArgumentException("EvidenceSourcePageInvalid");
        return ReadVerifiedAsync((database, deadline, state) =>
            SqliteCommandStore.ReadReconciliationSources(database, state.Configuration, kind, after, through,
                long.MaxValue, limit, deadline, pendingOnly ? long.MaxValue : null), token);
    }
    internal ValueTask<long> ReadSourceTailAsync(EvidenceReconciliationSubjectKind kind, CancellationToken token) =>
        ReadVerifiedAsync((database, deadline, state) =>
            SqliteCommandStore.ReconciliationSourceTail(database, state.Configuration, kind, long.MaxValue, deadline), token);
}
