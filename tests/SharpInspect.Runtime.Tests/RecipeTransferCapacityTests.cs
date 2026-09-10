using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V138-G06 covers the byte boundary of the real recipe-transfer ledger.  The
/// limit is deliberately derived from a persisted event so the test does not
/// duplicate the writer's canonicalization or Base64 accounting.
/// </summary>
public sealed class RecipeTransferCapacityTests
{
    private const string OperationReason = "V138 G06 fixed capacity probe";

    [Fact]
    public async Task V138_G06_PersistedPayloadBoundaryAcceptsOneEventThenRejectsAtomically()
    {
        var seed = RecipeTransferContentCodecTests.Fixture();
        var probeOptions = new RecipeTransferStoreOptions
        {
            PortablePolicy = seed.Policy
        };

        PayloadMeasure measure;
        await using (var probe = await CreateFixtureAsync(probeOptions))
        {
            var probeService = CreateService(probe, seed.Descriptor);
            var probeResult = await ReplaceEmptyTrustAsync(probe, probeService);
            Assert.True(probeResult.Succeeded, probeResult.Outcome.ReasonCode);
            await probe.WaitForVerifiedAsync();

            measure = await ReadPayloadMeasureAsync(probe);
            Assert.True(measure.RawPayloadBytes > 0);
            Assert.True(measure.BindingPayloadBytes > 0);
            Assert.True(measure.PersistedPayloadBytes >= measure.MaximumPayloadBytes,
                "The total-byte option must also satisfy the per-payload lower bound.");
        }

        var constrainedOptions = new RecipeTransferStoreOptions
        {
            PortablePolicy = seed.Policy,
            MaximumEntries = 8,
            MaximumPayloadBytes = measure.MaximumPayloadBytes,
            MaximumTotalBytes = measure.PersistedPayloadBytes
        };

        await using var fixture = await CreateFixtureAsync(constrainedOptions);
        var service = CreateService(fixture, seed.Descriptor);

        var first = await ReplaceEmptyTrustAsync(fixture, service);
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, first.Outcome.Audit);
        await fixture.WaitForVerifiedAsync();

        var afterFirst = await ReadLedgerStateAsync(fixture);
        Assert.Equal(1, afterFirst.TrustRows);
        Assert.Equal(1, afterFirst.TrustVersion);
        Assert.Equal(0, afterFirst.SigningKeyRows);
        Assert.Equal(0, afterFirst.DraftRows);
        Assert.Equal(1, afterFirst.EventRows);
        Assert.Equal(1, afterFirst.TransferAuditRows);
        Assert.Equal(measure.PersistedPayloadBytes, afterFirst.TransferAuditBytes);

        // Step-Up itself is an identity event.  Establish the transfer state
        // after that grant and compare the rejected writer against this exact
        // pre-write state, so the assertion isolates transfer capacity from
        // the authorization audit that was just issued.
        var secondCommand = await BuildAuthorizedEmptyTrustAsync(fixture, service, 1);
        await fixture.WaitForVerifiedAsync();
        var beforeRejectedWrite = await ReadLedgerStateAsync(fixture);
        Assert.Equal(afterFirst, beforeRejectedWrite);
        var auditSequence = fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;");
        var auditHash = await ReadTextAsync(fixture.Options.DatabasePath,
            "SELECT Hash FROM audit_entries ORDER BY Sequence DESC LIMIT 1;");
        var identityRevision = fixture.Scalar("SELECT Revision FROM identity_authority WHERE Id=1;");

        var second = await service.ReplaceTrustAsync(secondCommand);
        Assert.False(second.Succeeded);
        Assert.Equal("RecipeTransferEntryCapacityExceeded", second.Outcome.ReasonCode);

        var afterRejectedWrite = await ReadLedgerStateAsync(fixture);
        Assert.Equal(beforeRejectedWrite, afterRejectedWrite);
        Assert.Equal(afterFirst, afterRejectedWrite);
        Assert.Equal(auditSequence, fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;"));
        Assert.Equal(auditHash, await ReadTextAsync(fixture.Options.DatabasePath,
            "SELECT Hash FROM audit_entries ORDER BY Sequence DESC LIMIT 1;"));
        Assert.Equal(identityRevision, fixture.Scalar("SELECT Revision FROM identity_authority WHERE Id=1;"));
    }

    private static async Task<RecipeDraftStorageTests.Fixture> CreateFixtureAsync(
        RecipeTransferStoreOptions transferOptions)
    {
        return await RecipeDraftStorageTests.Fixture.CreateAsync(
            authorizationPolicy: TransferAuthorizationPolicy(),
            recipeTransfers: transferOptions);
    }

    private static IRecipeTransferService CreateService(RecipeDraftStorageTests.Fixture fixture,
        AlgorithmDescriptor descriptor)
    {
        return new RecipeTransferService(fixture.Options, fixture.Authorization, fixture.Store,
            new SqliteRecipeDraftQuery(fixture.Options), null,
            new SqliteRecipeTransferQuery(fixture.Options), new[] { descriptor });
    }

    private static async Task<RecipeTransferResult> ReplaceEmptyTrustAsync(
        RecipeDraftStorageTests.Fixture fixture, IRecipeTransferService service)
    {
        var command = await BuildAuthorizedEmptyTrustAsync(fixture, service, 0);
        return await service.ReplaceTrustAsync(command);
    }

    private static async Task<ReplaceRecipeTrustStoreCommand> BuildAuthorizedEmptyTrustAsync(
        RecipeDraftStorageTests.Fixture fixture, IRecipeTransferService service, long expectedVersion)
    {
        var current = await service.ReadTrustAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(expectedVersion, current.Trust?.Version ?? 0);

        var correlation = Guid.NewGuid();
        var bare = new ReplaceRecipeTrustStoreCommand(correlation, fixture.Invocation(), expectedVersion,
            Array.Empty<RecipeTrustedSigner>(), OperationReason);
        var stepUp = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            bare.CorrelationId, bare.Invocation,
            new StepUpBinding(Permission.ManageRecipeTrustStore, bare.CorrelationId,
                bare.AuthorizationTarget, AuditedCommandKind.ReplaceRecipeTrustStore),
            fixture.Password));
        Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
        Assert.NotNull(stepUp.GrantId);
        return bare with { Invocation = fixture.Invocation(stepUp.GrantId) };
    }

    private static async Task<PayloadMeasure> ReadPayloadMeasureAsync(
        RecipeDraftStorageTests.Fixture fixture)
    {
        var payloadText = await ReadTextAsync(fixture.Options.DatabasePath, @"
            SELECT Payload FROM audit_entries
            WHERE RecipeTransferPosition IS NOT NULL
            ORDER BY Sequence LIMIT 1;");
        var bindingText = await ReadTextAsync(fixture.Options.DatabasePath, @"
            SELECT BindingPayload FROM recipe_transfer_events
            ORDER BY Position LIMIT 1;");

        var payload = Convert.FromBase64String(payloadText);
        var binding = Convert.FromBase64String(bindingText);
        var persistedBytes = Encoding.UTF8.GetByteCount(payloadText);
        Assert.Equal(fixture.Scalar(@"
            SELECT length(CAST(Payload AS BLOB)) FROM audit_entries
            WHERE RecipeTransferPosition IS NOT NULL
            ORDER BY Sequence LIMIT 1;"), persistedBytes);

        return new PayloadMeasure(payload.Length, binding.Length, persistedBytes);
    }

    private static async Task<TransferLedgerState> ReadLedgerStateAsync(
        RecipeDraftStorageTests.Fixture fixture)
    {
        return new TransferLedgerState(
            fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_trust_versions;"),
            fixture.Scalar("SELECT COALESCE(MAX(Version),0) FROM recipe_transfer_trust_versions;"),
            fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_signing_keys;"),
            fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"),
            fixture.Scalar("SELECT COUNT(*) FROM recipe_transfer_events;"),
            fixture.Scalar("SELECT COUNT(*) FROM audit_entries WHERE RecipeTransferPosition IS NOT NULL;"),
            fixture.Scalar(@"
                SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0)
                FROM audit_entries WHERE RecipeTransferPosition IS NOT NULL;"),
            fixture.Scalar(@"
                SELECT COALESCE(MAX(CentralSequence),0)
                FROM recipe_transfer_events;"),
            await ReadTextOrNullAsync(fixture.Options.DatabasePath, @"
                SELECT CentralHash FROM recipe_transfer_events
                ORDER BY Position DESC LIMIT 1;"));
    }

    private static async Task<string> ReadTextAsync(string databasePath, string sql)
    {
        var value = await ReadScalarAsync(databasePath, sql);
        return Assert.IsType<string>(value);
    }

    private static async Task<string?> ReadTextOrNullAsync(string databasePath, string sql)
    {
        var value = await ReadScalarAsync(databasePath, sql);
        return value is null || value is DBNull ? null : Assert.IsType<string>(value);
    }

    private static async Task<object?> ReadScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static AuthorizationPolicy TransferAuthorizationPolicy()
    {
        var transferPermissions = new[]
        {
            Permission.ManageRecipeTrustStore, Permission.ManageRecipeSigningKeys,
            Permission.ReleaseRecipe, Permission.ImportRecipe, Permission.ExportRecipe
        };
        var roles = RecipeDraftTestPolicies.Authoring.RoleBundles.ToDictionary(
            pair => pair.Key,
            pair => pair.Key == HumanRoleBundle.Administrator
                ? pair.Value.Concat(transferPermissions).Distinct()
                : pair.Value.AsEnumerable());
        var stepUp = RecipeDraftTestPolicies.Authoring.StepUpPermissions
            .Concat(new[] { Permission.ImportRecipe, Permission.ExportRecipe })
            .Distinct();
        return new AuthorizationPolicy("recipe-transfer-capacity-tests", "v138-g06", roles, stepUp);
    }

    private sealed record PayloadMeasure(int RawPayloadBytes, int BindingPayloadBytes,
        long PersistedPayloadBytes)
    {
        internal int MaximumPayloadBytes => Math.Max(RawPayloadBytes, BindingPayloadBytes);
    }

    private sealed record TransferLedgerState(long TrustRows, long TrustVersion, long SigningKeyRows,
        long DraftRows, long EventRows, long TransferAuditRows, long TransferAuditBytes,
        long CentralSequence, string? CentralHash);
}
