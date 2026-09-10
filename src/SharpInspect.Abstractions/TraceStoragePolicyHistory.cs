namespace SharpInspect.Abstractions;

public sealed record TraceStoragePolicyFilter(long AfterVersion = 0,
    long? ThroughVersion = null, int PageSize = 20);

public sealed record TraceStoragePolicyHistoryPage(bool Available, string ReasonCode,
    IReadOnlyList<TraceStoragePolicyPublication> Records, long ThroughVersion,
    long? NextAfterVersion);

/// <summary>Independent bounded policy history; reads never create a writer or a production obligation.</summary>
public interface ITraceStoragePolicyHistoryQuery
{
    ValueTask<TraceStoragePolicyReadResult> ReadAsync(long? version = null,
        CancellationToken cancellationToken = default);
    ValueTask<TraceStoragePolicyHistoryPage> QueryAsync(TraceStoragePolicyFilter filter,
        CancellationToken cancellationToken = default);
}

public interface ITraceStoragePolicyService : ITraceStoragePolicyHistoryQuery
{
    ValueTask<TraceStoragePolicyAccess> GetAccessAsync(CommandInvocation invocation,
        CancellationToken cancellationToken = default);
    ValueTask<TraceStoragePolicyResult> PublishAsync(PublishTraceStoragePolicyCommand command,
        CancellationToken cancellationToken = default);
    ValueTask<TraceStoragePreflightReport> GetPreflightAsync(
        CancellationToken cancellationToken = default);
}
