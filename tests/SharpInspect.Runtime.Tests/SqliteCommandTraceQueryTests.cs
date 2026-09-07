using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class SqliteCommandTraceQueryTests
{
    [Fact]
    public async Task V102_Q01_KeysetPagesKeepCapturedUpperBoundAndFiltersWithConcurrentReaders()
    {
        await using var directory = new TemporaryStoreDirectory();
        var options = directory.Options();
        await using var store = new SqliteCommandStore(options);
        Assert.True((await store.Initialization).Committed);
        var correlation = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), correlation, Guid.NewGuid(),
                DateTimeOffset.UtcNow.AddMilliseconds(i), AuditedCommandKind.ArmProduction, CommandSource.PhysicalConsole,
                "operator", null, null, CommandAuditPhase.Outcome, CommandDisposition.Rejected, "Blocked");
            var result = await store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(2)));
            Assert.True(result.Committed);
        }

        var query = new SqliteCommandTraceQuery(options);
        var first = await query.QueryAsync(new CommandTraceFilter(CorrelationId: correlation,
            ClaimedPrincipalId: "operator", PageSize: 2));
        Assert.Equal(5, first.ThroughPosition);
        Assert.Equal(2, first.Records.Count);
        Assert.Equal(2, first.NextAfterPosition);

        var later = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, AuditedCommandKind.ArmProduction, CommandSource.PhysicalConsole,
            "operator", null, null, CommandAuditPhase.Outcome, CommandDisposition.Rejected, "Later");
        Assert.True((await store.AppendAsync(later, new StoreDeadline(TimeSpan.FromSeconds(2)))).Committed);

        var second = await query.QueryAsync(new CommandTraceFilter(CorrelationId: correlation,
            ClaimedPrincipalId: "operator", AfterPosition: first.NextAfterPosition!.Value,
            ThroughPosition: first.ThroughPosition, PageSize: 2));
        Assert.Equal(2, second.Records.Count);
        Assert.Equal(4, second.NextAfterPosition);
        Assert.All(second.Records, record => Assert.Equal(correlation, record.CorrelationId));

        var third = await query.QueryAsync(new CommandTraceFilter(CorrelationId: correlation,
            ClaimedPrincipalId: "operator", AfterPosition: second.NextAfterPosition!.Value,
            ThroughPosition: first.ThroughPosition, PageSize: 2));
        Assert.Single(third.Records);
        Assert.Null(third.NextAfterPosition);

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.QueryAsync(
            new CommandTraceFilter(ThroughPosition: first.ThroughPosition + 100, PageSize: 1)).AsTask());

        var reads = Enumerable.Range(0, 16).Select(_ => query.QueryAsync(
            new CommandTraceFilter(CorrelationId: correlation, PageSize: 20)).AsTask());
        var pages = await Task.WhenAll(reads);
        Assert.All(pages, page => Assert.Equal(5, page.Records.Count));
    }

    [Fact]
    public async Task V102_Q02_QueryRejectsBoundedPageOutsideAllowedRange()
    {
        await using var directory = new TemporaryStoreDirectory();
        var query = new SqliteCommandTraceQuery(directory.Options());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.QueryAsync(
            new CommandTraceFilter(PageSize: 0)).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.QueryAsync(
            new CommandTraceFilter(PageSize: 201)).AsTask());
    }

    private sealed class TemporaryStoreDirectory : IAsyncDisposable
    {
        public TemporaryStoreDirectory()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "trace.db");
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public ProductionStoreOptions Options() => new(DatabasePath);

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(DirectoryPath, recursive: true); }
            catch { }
            return ValueTask.CompletedTask;
        }
    }
}
