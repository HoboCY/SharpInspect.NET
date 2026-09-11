using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeSelectionIntegrityTests
{
    [Theory]
    [InlineData("payload")]
    [InlineData("index")]
    [InlineData("delete")]
    [InlineData("central")]
    public async Task V146_D09_GovernedSelectionCannotBeReadAfterLocalOrCentralAuditCorruption(string corruption)
    {
        await using var harness = await RecipeActivationServiceTests.ActivationHarness.CreateAsync(enableRecipeSelections: true);
        await harness.WaitForRecipeSelectionStartupAsync();
        var changed = await RecipeSelectionIntegrationSupport.ChangeAsync(harness,
            RecipeSelectionIntegrationSupport.PlcPolicy("1"), RecipeSelectionIntegrationSupport.Map("1",
                RecipeSelectionIntegrationSupport.Entry(7, harness.Released)), null, "V146 integrity fixture exact selection");
        Assert.Equal(CommandDisposition.Accepted, changed.Outcome.Disposition);
        await harness.Store.DisposeAsync();
        var table = corruption == "central" ? "audit_entries" : "recipe_selection_revisions";
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = harness.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            var triggers = new List<(string Name, string Sql)>();
            await using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name=$table;";
                lookup.Parameters.AddWithValue("$table", table);
                await using var reader = await lookup.ExecuteReaderAsync();
                while (await reader.ReadAsync()) triggers.Add((reader.GetString(0), reader.GetString(1)));
            }
            Assert.NotEmpty(triggers);
            await using var mutation = connection.CreateCommand();
            foreach (var trigger in triggers)
            {
                Assert.Matches("^[A-Za-z0-9_]+$", trigger.Name);
                mutation.CommandText = "DROP TRIGGER " + trigger.Name + ";";
                await mutation.ExecuteNonQueryAsync();
            }
            mutation.CommandText = corruption switch
            {
                "payload" => "UPDATE recipe_selection_revisions SET Payload='AAAA' WHERE Position=1;",
                "index" => "UPDATE recipe_selection_revisions SET OperationId='e9d24b7b-ccbb-47d6-8014-275b8ccfbd93' WHERE Position=1;",
                "delete" => "DELETE FROM recipe_selection_revisions WHERE Position=1;",
                _ => "UPDATE audit_entries SET Payload='AAAA' WHERE Kind='RecipeSelectionRevision';"
            };
            Assert.Equal(1, await mutation.ExecuteNonQueryAsync());
            foreach (var trigger in triggers)
            {
                mutation.CommandText = trigger.Sql;
                await mutation.ExecuteNonQueryAsync();
            }
        }
        var query = new SqliteRecipeSelectionQuery(harness.Options);
        var current = await query.ReadCurrentAsync();
        Assert.False(current.Available);
        Assert.Null(current.Revision);
        var history = await query.QueryAsync(new RecipeSelectionFilter());
        Assert.False(history.Available);
        Assert.Empty(history.Revisions);
        var activations = await new SqliteRecipeActivationQuery(harness.Options).ReadCurrentAsync();
        Assert.False(activations.Available);
    }
}
