using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PlcResultContractQueryConfigurationTests
{
    [Theory]
    [InlineData("Drafts")]
    [InlineData("Releases")]
    [InlineData("Identity")]
    [InlineData("Audit")]
    public async Task V131_Q02_EachRequiredConfigurationIsEnforcedByAllColdReadPaths(string omitted)
    {
        await using var harness = await PlcResultContractGovernanceTests.Harness.CreateAsync();
        var original = harness.Storage.Options;
        var options = new ProductionStoreOptions(original.DatabasePath)
        {
            AuditIntegrityPolicy = omitted == "Audit" ? null : original.AuditIntegrityPolicy,
            LocalIdentity = omitted == "Identity" ? null : original.LocalIdentity,
            RecipeDrafts = omitted == "Drafts" ? null : original.RecipeDrafts,
            RecipeReleases = omitted == "Releases" ? null : original.RecipeReleases,
            PlcResultContracts = original.PlcResultContracts
        };
        var query = new SqlitePlcResultContractQuery(options);
        var reference = PlcResultContractTestSupport.Contract(harness.PrimarySchema).Reference;
        var current = await query.ReadCurrentAsync();
        var exact = await query.ReadAsync(reference);
        var page = await query.QueryAsync(new());
        const string reason = "PlcResultContractsRequiresDraftsReleasesIdentityAndAudit";
        Assert.False(current.Available);
        Assert.Equal(reason, current.ReasonCode);
        Assert.False(exact.Available);
        Assert.Equal(reason, exact.ReasonCode);
        Assert.False(page.Available);
        Assert.Equal(reason, page.ReasonCode);
        var trace = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(options).QueryAsync(new()).AsTask());
        Assert.Equal(reason, trace.Message);
    }

    [Fact]
    public async Task V131_Q03_EmptyReleaseLedgerIsStillRequiredAfterZeroHighwatermarkContract()
    {
        await using var harness = await PlcResultContractGovernanceTests.Harness.CreateAsync();
        var contract = PlcResultContractTestSupport.Contract(harness.PrimarySchema);
        var outcome = await harness.Runtime.SubmitAsync(await harness.AuthorizeChangeAsync(
            harness.Change(contract, null, "zero-release contract before cold integrity check")));
        Assert.True(outcome.Disposition == CommandDisposition.Accepted, outcome.ReasonCode);
        var original = await harness.Contracts.ReadCurrentAsync();
        Assert.True(original.Available, original.ReasonCode);
        Assert.Equal(0, original.Revision!.ReleaseHighWatermark);
        Assert.Empty(original.Revision.Bindings);
        await harness.StopCapabilitiesAsync();
        await harness.Storage.Store.DisposeAsync();
        await using (var connection = new SqliteConnection("Data Source=" + harness.Storage.Options.DatabasePath))
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TABLE recipe_release_events; DROP TABLE recipe_release_store_config;";
            await drop.ExecuteNonQueryAsync();
        }
        var query = new SqlitePlcResultContractQuery(harness.Storage.Options);
        var current = await query.ReadCurrentAsync();
        var exact = await query.ReadAsync(contract.Reference);
        var page = await query.QueryAsync(new());
        Assert.False(current.Available);
        Assert.Null(current.Revision);
        Assert.False(exact.Available);
        Assert.Null(exact.Revision);
        Assert.False(page.Available);
        Assert.Empty(page.Revisions);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(harness.Storage.Options).QueryAsync(new()).AsTask());
    }
}
