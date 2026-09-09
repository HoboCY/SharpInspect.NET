using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

#pragma warning disable CA1416
public sealed class PlcResultContractQueryTests
{
    [Fact]
    public async Task V131_Q01_RequiredExternalAnchorIsAppliedToCurrentExactAndPageQueries()
    {
        var anchor = new QueryAnchor();
        await using var fixture = await CreateSchema17FixtureAsync(anchor, requireExternalAnchor: true);
        await StopFixtureAsync(fixture);

        var withoutAdapter = new SqlitePlcResultContractQuery(Rebind(fixture.Options,
            setAnchor: true, anchor: null));
        await AssertUnavailableAsync(withoutAdapter, "AuditRequiredAnchorUnavailable");

        anchor.Mode = QueryAnchorMode.MissingReceipt;
        var withoutReceipt = new SqlitePlcResultContractQuery(Rebind(fixture.Options,
            setAnchor: true, anchor));
        await AssertUnavailableAsync(withoutReceipt, "AuditExternalAnchorMismatch");

        anchor.Mode = QueryAnchorMode.Mismatch;
        var mismatchedReceipt = new SqlitePlcResultContractQuery(Rebind(fixture.Options,
            setAnchor: true, anchor));
        await AssertUnavailableAsync(mismatchedReceipt, "AuditExternalAnchorMismatch");

        anchor.Mode = QueryAnchorMode.Valid;
        var matchingReceipt = new SqlitePlcResultContractQuery(Rebind(fixture.Options,
            setAnchor: true, anchor));
        await AssertAvailableAsync(matchingReceipt);
    }

    private static async Task<RecipeDraftStorageTests.Fixture> CreateSchema17FixtureAsync(
        QueryAnchor? anchor = null, bool requireExternalAnchor = false)
    {
        var releaseOptions = new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
            "V131.Query.Release", "1", RecipeGovernanceMode.SingleApproverRelease));
        return await RecipeDraftStorageTests.Fixture.CreateAsync(
            recipeReleases: releaseOptions,
            plcResultContracts: new PlcResultContractStoreOptions(),
            externalAuditAnchor: anchor,
            requireExternalAnchor: requireExternalAnchor);
    }

    private static ProductionStoreOptions Rebind(ProductionStoreOptions source,
        bool setAnchor = false, IExternalAuditAnchor? anchor = null) =>
        new(source.DatabasePath)
        {
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity,
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = source.AlarmPolicy,
            ExternalAuditAnchor = setAnchor ? anchor : source.ExternalAuditAnchor,
            AlgorithmResultArchive = source.AlgorithmResultArchive,
            RecipeDrafts = source.RecipeDrafts,
            CameraSetup = source.CameraSetup,
            CameraRecovery = source.CameraRecovery,
            CameraNetwork = source.CameraNetwork,
            ImagingSetup = source.ImagingSetup,
            CalibrationSessions = source.CalibrationSessions,
            CalibrationGovernance = source.CalibrationGovernance,
            RecipeReleases = source.RecipeReleases,
            PlcResultContracts = source.PlcResultContracts
        };

    private static async Task AssertUnavailableAsync(IPlcResultContractQuery query,
        string expectedReason)
    {
        var current = await query.ReadCurrentAsync();
        Assert.False(current.Available);
        Assert.Equal(expectedReason, current.ReasonCode);
        Assert.Null(current.Revision);

        var exact = await query.ReadAsync(new RecipeContractReference(
            "V131.Query.Unknown", "1", new string('A', 64)));
        Assert.False(exact.Available);
        Assert.Equal(expectedReason, exact.ReasonCode);
        Assert.Null(exact.Revision);

        var page = await query.QueryAsync(new PlcResultContractFilter(PageSize: 20));
        Assert.False(page.Available);
        Assert.Equal(expectedReason, page.ReasonCode);
        Assert.Empty(page.Revisions);
    }

    private static async Task AssertAvailableAsync(IPlcResultContractQuery query)
    {
        var current = await query.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal("PlcResultContractHistoryVerified", current.ReasonCode);
        Assert.Null(current.Revision);

        var exact = await query.ReadAsync(new RecipeContractReference(
            "V131.Query.Unknown", "1", new string('A', 64)));
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.Equal("PlcResultContractHistoryVerified", exact.ReasonCode);
        Assert.Null(exact.Revision);

        var page = await query.QueryAsync(new PlcResultContractFilter(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal("PlcResultContractHistoryVerified", page.ReasonCode);
        Assert.Empty(page.Revisions);
        Assert.Equal(0, page.ThroughPosition);
        Assert.Null(page.NextAfterPosition);
    }

    private static async Task StopFixtureAsync(RecipeDraftStorageTests.Fixture fixture)
    {
        fixture.Authorization.Dispose();
        await fixture.Sessions.DisposeAsync();
        await fixture.Store.DisposeAsync();
    }

    private enum QueryAnchorMode
    {
        Valid,
        MissingReceipt,
        Mismatch
    }

    private sealed class QueryAnchor : IExternalAuditAnchor
    {
        private const string RouteId = "v131-query-anchor";

        internal ConcurrentDictionary<Guid, AuditAnchorReceipt> Receipts { get; } = new();
        internal QueryAnchorMode Mode { get; set; } = QueryAnchorMode.Valid;

        public ValueTask<AuditAnchorReceipt> DeliverAsync(AuditCheckpoint checkpoint,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(checkpoint.StationId + "/" + RouteId + "/" +
                checkpoint.CheckpointId.ToString("D"), idempotencyKey);
            var receipt = Receipts.GetOrAdd(checkpoint.CheckpointId, _ => new AuditAnchorReceipt(
                checkpoint.CheckpointId, checkpoint.StationId, checkpoint.Sequence,
                checkpoint.HeadHash, RouteId, "receipt-" + checkpoint.CheckpointId.ToString("N"),
                checkpoint.PolicyHash, checkpoint.SigningKeyId,
                AuditChainDatabase.CheckpointDigest(checkpoint), DateTimeOffset.UtcNow));
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<AuditAnchorReceipt?> ReadLatestAsync(string stationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var latest = Receipts.Values.Where(value => value.StationId == stationId)
                .OrderByDescending(value => value.Sequence).FirstOrDefault();
            AuditAnchorReceipt? result = Mode switch
            {
                QueryAnchorMode.MissingReceipt => null,
                QueryAnchorMode.Mismatch when latest is not null =>
                    latest with { HeadHash = new string('0', 64) },
                _ => latest
            };
            return ValueTask.FromResult(result);
        }
    }
}
#pragma warning restore CA1416
