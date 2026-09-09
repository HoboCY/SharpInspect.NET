using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V132_H01_InternalFixtureSuccessPersistsCompleteReferencesAndColdHistory()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var result = await harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand());

        Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
        Assert.Equal("RecipeActivated", result.Outcome.ReasonCode);
        var success = Assert.IsType<RecipeActivationRecord>(result.Record);
        Assert.Equal(RecipeActivationOutcomeState.Succeeded, success.Outcome.State);
        Assert.Equal(RecipeActivationEvidenceKind.InternalContractFixture, success.EvidenceKind);
        Assert.False(success.CanBeActive);

        await harness.WaitForVerifiedAsync();
        var history = await harness.ActivationHistory.QueryAsync(new(PageSize: 20));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(2, history.Records.Count);
        Assert.Contains(history.Records, value => value.Outcome.State == RecipeActivationOutcomeState.Admitted);
        Assert.Contains(history.Records, value => value.Reference == success.Reference);

        var current = await harness.ActivationHistory.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Record);
        Assert.False(current.RecoveryRequired);

        Assert.Equal(2, CountRowsWithCompleteAuditReferences(harness.Options.DatabasePath));
    }

    [Fact]
    public async Task V132_H02_TamperedAuthorizationAuditReferenceMakesColdQueryUnavailable()
    {
        await using var harness = await ActivationHarness.CreateAsync();
        var result = await harness.CreateFixtureService().ActivateAsync(
            await harness.AuthorizedActivationCommand());
        Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
        var success = Assert.IsType<RecipeActivationRecord>(result.Record);
        await harness.WaitForVerifiedAsync();

        // Stop every writer before simulating an offline SQL tamper.  The central
        // audit payload is deliberately left unchanged, so no new signature is
        // created and the cold query must reject the inconsistent row binding.
        await harness.Runtime.DisposeAsync();
        await harness.Store.DisposeAsync();
        TamperAuthorizationAuditHash(harness.Options.DatabasePath);

        var cold = new SqliteRecipeActivationQuery(harness.Options);
        var current = await cold.ReadCurrentAsync();
        Assert.False(current.Available, current.ReasonCode);
        Assert.Equal("RecipeActivationAuditPayloadMismatch", current.ReasonCode);
        Assert.Null(current.Record);

        var page = await cold.QueryAsync(new(PageSize: 20));
        Assert.False(page.Available, page.ReasonCode);
        Assert.Equal("RecipeActivationAuditPayloadMismatch", page.ReasonCode);
        Assert.Empty(page.Records);

        var exact = await cold.ReadAsync(success.Reference);
        Assert.False(exact.Available, exact.ReasonCode);
        Assert.Equal("RecipeActivationAuditPayloadMismatch", exact.ReasonCode);
        Assert.Null(exact.Record);
    }

    private static long CountRowsWithCompleteAuditReferences(string databasePath)
    {
        using var connection = SqliteNative.Open(databasePath, readOnly: true);
        return AuditChainDatabase.Scalar(connection.Handle!, @"
            SELECT COUNT(*) FROM recipe_activation_events
            WHERE CommandEventId IS NOT NULL AND CommandAuditSequence > 0 AND length(CommandAuditHash)=64
              AND AuthorizationEventId IS NOT NULL AND AuthorizationAuditSequence > 0
              AND length(AuthorizationAuditHash)=64;", new StoreDeadline(TimeSpan.FromSeconds(5)));
    }

    private static void TamperAuthorizationAuditHash(string databasePath)
    {
        using var connection = SqliteNative.Open(databasePath, readOnly: false);
        SqliteNative.Execute(connection.Handle!, @"
            DROP TRIGGER recipe_activation_event_immutable_update;
            UPDATE recipe_activation_events
            SET AuthorizationAuditHash='0000000000000000000000000000000000000000000000000000000000000000'
            WHERE OutcomeState=2;
            CREATE TRIGGER recipe_activation_event_immutable_update BEFORE UPDATE
                ON recipe_activation_events BEGIN
                SELECT RAISE(ABORT,'ImmutableRecipeActivationEvent');
            END;", new StoreDeadline(TimeSpan.FromSeconds(5)));
    }
}
