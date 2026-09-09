using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused storage checks for the schema-16 released-recipe ledger.  The codec
/// tests use a complete record; the query test uses the real draft, identity,
/// audit and release writer before closing that writer and reading again.
/// </summary>
public sealed class RecipeReleaseStorageTests
{
    private static readonly Guid AuthorA = Guid.Parse("a1100000-0000-0000-0000-000000000001");
    private static readonly Guid AuthorB = Guid.Parse("b2200000-0000-0000-0000-000000000002");

    [Fact]
    public void V130_S01_CodecRoundTripIsCanonicalAndRejectsTruncationTailHashAndReordering()
    {
        var record = Record();
        var payload = RecipeReleaseStorageCodec.Encode(record);
        var decoded = RecipeReleaseStorageCodec.Decode(payload);

        Assert.Equal(record.ContentHash, decoded.ContentHash);
        Assert.Equal(record.Source.RevisionContentHash, decoded.Source.RevisionContentHash);
        Assert.Equal(record.Checks.Select(value => value.ContentHash),
            decoded.Checks.Select(value => value.ContentHash));
        Assert.Equal(record.Changes.Select(value => value.ContentHash),
            decoded.Changes.Select(value => value.ContentHash));
        Assert.Equal(payload, RecipeReleaseStorageCodec.Encode(decoded));

        AssertPayloadRejected(payload[..^1]);
        AssertPayloadRejected(payload.Concat(new byte[] { 0x7f }).ToArray());

        var wrongHash = payload.ToArray();
        wrongHash[^1] = wrongHash[^1] == (byte)'A' ? (byte)'B' : (byte)'A';
        AssertPayloadRejected(wrongHash);

        // Keep the original record hash while moving two encoded check blocks.
        // Decode must reject the resulting non-canonical evidence rather than
        // accepting the reordered checks as a new release.
        AssertPayloadRejected(SwapFirstTwoChecks(payload));
    }

    [Fact]
    public async Task V130_S02_ReleaseLedgerRowsAndConfigurationAreImmutable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(connection, SqliteCommandStore.RecipeReleaseSchemaSql);

        var policy = new RecipeGovernancePolicy("Storage.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        await ExecuteAsync(connection, @"
            INSERT INTO recipe_release_store_config
                (Id,FormatVersion,GovernancePolicyId,GovernancePolicyVersion,GovernancePolicyMode,
                 GovernancePolicyHash,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES (1,1,$policyId,$policyVersion,$mode,$policyHash,100,8388608,536870912,$bindingHash);",
            ("$policyId", (object)policy.Id), ("$policyVersion", policy.Version),
            ("$mode", (int)policy.Mode), ("$policyHash", policy.ContentHash),
            ("$bindingHash", Hash("binding")));

        var releaseId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var sourceDraftId = Guid.NewGuid();
        var commandEventId = Guid.NewGuid();
        var authorizationEventId = Guid.NewGuid();
        var payload = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        await ExecuteAsync(connection, @"
            INSERT INTO recipe_release_events
                (Position,PreviousHash,ReleaseId,OperationId,RecipeKey,RecipeVersion,SourceDraftId,
                 SourceRevision,SourceRevisionContentHash,SourceContentHash,GovernancePolicyId,
                 GovernancePolicyVersion,GovernancePolicyMode,GovernancePolicyHash,RecordContentHash,
                 PayloadHash,Payload,CommandEventId,CommandAuditSequence,CommandAuditHash,
                 AuthorizationEventId,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES (1,NULL,$releaseId,$operationId,'Storage.Recipe',1,$sourceDraftId,1,$sourceRevisionHash,
                    $sourceContentHash,$policyId,$policyVersion,$mode,$policyHash,$recordHash,$payloadHash,
                    $payload,$commandEventId,1,$commandHash,$authorizationEventId,2,$authorizationHash,3,$auditHash);",
            ("$releaseId", releaseId.ToString("D")), ("$operationId", operationId.ToString("D")),
            ("$sourceDraftId", sourceDraftId.ToString("D")), ("$sourceRevisionHash", Hash("revision")),
            ("$sourceContentHash", Hash("content")), ("$policyId", policy.Id),
            ("$policyVersion", policy.Version), ("$mode", (int)policy.Mode),
            ("$policyHash", policy.ContentHash), ("$recordHash", Hash("record")),
            ("$payloadHash", Hash("payload")), ("$payload", payload),
            ("$commandEventId", commandEventId.ToString("D")), ("$commandHash", Hash("command")),
            ("$authorizationEventId", authorizationEventId.ToString("D")),
            ("$authorizationHash", Hash("authorization")), ("$auditHash", Hash("audit")));

        await AssertImmutableAsync(connection,
            "UPDATE recipe_release_store_config SET BindingHash=$value WHERE Id=1;",
            "ImmutableRecipeReleaseConfiguration", ("$value", Hash("changed-config")));
        await AssertImmutableAsync(connection,
            "DELETE FROM recipe_release_store_config WHERE Id=1;",
            "ImmutableRecipeReleaseConfiguration");
        await AssertImmutableAsync(connection,
            "UPDATE recipe_release_events SET PayloadHash=$value WHERE Position=1;",
            "ImmutableRecipeReleaseEvent", ("$value", Hash("changed-payload")));
        await AssertImmutableAsync(connection,
            "DELETE FROM recipe_release_events WHERE Position=1;",
            "ImmutableRecipeReleaseEvent");

        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM recipe_release_store_config;"));
        Assert.Equal(1, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM recipe_release_events;"));
    }

    [Fact]
    public async Task V130_S03_QueryPagesAndExactReadsThenSurvivesWriterRestart()
    {
        await using var harness = await ReleaseHarness.CreateAsync();
        var first = await harness.ReleaseAsync(harness.Source);

        var secondDocument = harness.Storage.Document("Second release");
        var secondSaved = await harness.Storage.SaveAsync(Guid.NewGuid(), harness.Source.DraftId,
            harness.Source.Revision, harness.Source.RevisionContentHash, secondDocument,
            "second release");
        Assert.True(secondSaved.Saved, secondSaved.ReasonCode);
        await harness.Storage.WaitForVerifiedAsync();
        harness.Source = secondSaved.Revision!;
        var second = await harness.ReleaseAsync(harness.Source);

        var firstPage = await harness.Query.QueryAsync(new ReleasedRecipeFilter(
            first.Reference.Id, PageSize: 1));
        Assert.True(firstPage.Available, firstPage.ReasonCode);
        Assert.Single(firstPage.Recipes);
        Assert.NotNull(firstPage.NextAfterPosition);

        var secondPage = await harness.Query.QueryAsync(new ReleasedRecipeFilter(
            first.Reference.Id, AfterPosition: firstPage.NextAfterPosition!.Value, PageSize: 1));
        Assert.True(secondPage.Available, secondPage.ReasonCode);
        var pagedSecond = Assert.Single(secondPage.Recipes);
        Assert.Equal(second.Reference, pagedSecond.Reference);

        // ReadAsync supplies an exact reference to one verification snapshot;
        // this remains correct when the requested row is after the first page.
        var exact = await harness.Query.ReadAsync(second.Reference);
        Assert.True(exact.Available, exact.ReasonCode);
        Assert.NotNull(exact.Recipe);
        Assert.Equal(second.Record.ContentHash, exact.Recipe!.Record.ContentHash);

        await harness.CloseWriterAndRestartAsync();
        var restarted = await harness.Query.QueryAsync(new ReleasedRecipeFilter(
            first.Reference.Id, PageSize: 20));
        Assert.True(restarted.Available, restarted.ReasonCode);
        Assert.Equal(2, restarted.Recipes.Count);
        var restartedExact = await harness.Query.ReadAsync(second.Reference);
        Assert.True(restartedExact.Available, restartedExact.ReasonCode);
        Assert.Equal(second.Record.ContentHash, restartedExact.Recipe!.Record.ContentHash);
    }

    private static RecipeReleaseRecord Record()
    {
        var source = Revision(Guid.Parse("d4400000-0000-0000-0000-000000000004"), 1, 1,
            AuthorA, Content(6));
        var policy = new RecipeGovernancePolicy("Storage.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var checks = new[]
        {
            new RecipeReleaseValidationCheck("Structure", "Recipe", true, "Passed"),
            new RecipeReleaseValidationCheck("Configuration", "count", true, "Passed",
                new RecipeContractReference("Count.Config", "1", Hash("schema")))
        };
        var changes = new[]
        {
            new RecipeReleaseChange("Configuration/count", "5", "6", AuthorA,
                source.DraftId, source.Revision),
            new RecipeReleaseChange("Camera/GainDb", "0", "1", AuthorB,
                source.DraftId, source.Revision)
        };
        return new RecipeReleaseRecord(1, Guid.Parse("e5500000-0000-0000-0000-000000000005"),
            Guid.Parse("f6600000-0000-0000-0000-000000000006"), 1, source, policy, checks, changes,
            AuthorB, Guid.Parse("a7700000-0000-0000-0000-000000000007"), 1,
            Guid.Parse("b8800000-0000-0000-0000-000000000008"),
            new RecipeContractReference("Authorization", "1", Hash("authorization")),
            "storage codec test", Hash("target"), source.RecordedAtUtc.AddSeconds(1));
    }

    private static RecipeDraftContent Content(long count, string? label = null)
    {
        var schema = new AlgorithmConfigurationSchema("Count.Config", "1", new[]
        {
            new AlgorithmFieldDefinition("count", AlgorithmScalarType.Int64, "items", true,
                authoringDefault: AlgorithmScalarValue.FromInt64(5)),
            new AlgorithmFieldDefinition("label", AlgorithmScalarType.String, "text", false)
        });
        var entries = new List<AlgorithmConfigurationEntry>
        {
            new("count", "items", AlgorithmScalarValue.FromInt64(count))
        };
        if (label is not null)
            entries.Add(new("label", "text", AlgorithmScalarValue.FromString(label)));
        var contract = new RecipeContractReference("Result", "1", Hash("result"));
        return new(null, "Storage.Example", "Storage recipe",
            new(new("Count.Algorithm", "1"), schema, contract, contract),
            AlgorithmConfigurationSnapshot.Create(schema, entries), "TopCamera",
            new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            TimeSpan.FromMilliseconds(100), null, null,
            entries.Select(value => new RecipeDraftFieldOrigin(value.Key,
                RecipeDraftValueOrigin.Explicit)));
    }

    private static RecipeDraftRevision Revision(Guid id, long revision, long position,
        Guid author, RecipeDraftContent content, RecipeDraftRevision? previous = null) =>
        new(position, id, revision, Guid.NewGuid(), previous?.RevisionContentHash,
            Hash(id + "/" + revision + "/" + content.ContentHash + "/" + author), content,
            author, Guid.NewGuid(), 1, "storage test", DateTimeOffset.Parse(
                "2026-09-09T00:00:00Z").AddSeconds(position));

    private static byte[] SwapFirstTwoChecks(byte[] payload)
    {
        var offset = 5; // magic + format
        offset += 8 + 16 + 16 + 8; // release identity
        offset += 8 + 16 + 8 + 16; // source identity
        SkipOptionalString(payload, ref offset); // previous revision hash
        SkipString(payload, ref offset); // revision hash
        SkipString(payload, ref offset); // source payload hash
        SkipString(payload, ref offset); // source payload JSON
        offset += 16 + 16 + 8; // source author/session/revision
        SkipString(payload, ref offset); // source change reason
        offset += 8; // source timestamp
        SkipString(payload, ref offset); // policy id
        SkipString(payload, ref offset); // policy version
        offset += 4; // policy mode
        SkipString(payload, ref offset); // policy hash
        var count = ReadInt32(payload, ref offset);
        Assert.True(count >= 2);
        var firstStart = offset;
        SkipCheck(payload, ref offset);
        var firstEnd = offset;
        var secondStart = offset;
        SkipCheck(payload, ref offset);
        var secondEnd = offset;

        var first = payload[firstStart..firstEnd];
        var second = payload[secondStart..secondEnd];
        return payload[..firstStart].Concat(second).Concat(payload[firstEnd..secondStart])
            .Concat(first).Concat(payload[secondEnd..]).ToArray();
    }

    private static void SkipCheck(byte[] payload, ref int offset)
    {
        SkipString(payload, ref offset);
        SkipString(payload, ref offset);
        offset += 1; // passed
        SkipString(payload, ref offset);
        var hasContract = payload[offset++] != 0;
        if (hasContract)
        {
            SkipString(payload, ref offset);
            SkipString(payload, ref offset);
            SkipString(payload, ref offset);
        }
        SkipString(payload, ref offset); // check content hash
    }

    private static void SkipOptionalString(byte[] payload, ref int offset)
    {
        var present = payload[offset++] != 0;
        if (present) SkipString(payload, ref offset);
    }

    private static void SkipString(byte[] payload, ref int offset)
    {
        var length = ReadInt32(payload, ref offset);
        Assert.True(length >= 0 && offset + length <= payload.Length);
        offset += length;
    }

    private static int ReadInt32(byte[] payload, ref int offset)
    {
        Assert.True(offset >= 0 && offset + 4 <= payload.Length);
        var result = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
        offset += 4;
        return result;
    }

    private static void AssertPayloadRejected(byte[] payload) =>
        Assert.Throws<InvalidOperationException>(() => RecipeReleaseStorageCodec.Decode(payload));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertImmutableAsync(SqliteConnection connection, string sql,
        string expected, params (string Name, object Value)[] parameters)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, sql, parameters));
        Assert.Contains(expected, exception.Message);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class ReleaseHarness : IAsyncDisposable
    {
        private ReleaseHarness(RecipeDraftStorageTests.Fixture storage, RecipeDraftService drafts,
            StationRuntime runtime, RecipeReleaseService releases, RecipeDraftRevision source)
        {
            Storage = storage;
            Drafts = drafts;
            Runtime = runtime;
            Releases = releases;
            Source = source;
            Query = new SqliteReleasedRecipeQuery(storage.Options);
        }

        internal RecipeDraftStorageTests.Fixture Storage { get; }
        internal RecipeDraftService Drafts { get; }
        internal StationRuntime Runtime { get; }
        internal RecipeReleaseService Releases { get; }
        internal RecipeDraftRevision Source { get; set; }
        internal SqliteReleasedRecipeQuery Query { get; private set; }

        internal static async Task<ReleaseHarness> CreateAsync()
        {
            var storage = await RecipeDraftStorageTests.Fixture.CreateAsync(recipeReleases:
                new RecipeReleaseStoreOptions(new("V130.Storage.Release", "1",
                    RecipeGovernanceMode.SingleApproverRelease)));
            var saved = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
                storage.Document("First release"), "first release");
            Assert.True(saved.Saved, saved.ReasonCode);
            await storage.WaitForVerifiedAsync();
            var drafts = new RecipeDraftService(new[] { new Factory(saved.Revision!.Content) },
                storage.Options, storage.Authorization, new SqliteRecipeDraftQuery(storage.Options));
            var runtime = new StationRuntime(storage.Store, TimeSpan.FromMilliseconds(500),
                storage.Sessions, storage.Authorization);
            var releases = new RecipeReleaseService(drafts, storage.Authorization,
                new SqliteReleasedRecipeQuery(storage.Options), storage.Options,
                () => runtime.GetSnapshotAsync());
            runtime.ConfigureRecipeReleaseService(releases);
            return new ReleaseHarness(storage, drafts, runtime, releases, saved.Revision!);
        }

        internal async Task<ReleasedRecipe> ReleaseAsync(RecipeDraftRevision source)
        {
            var command = new ReleaseRecipeCommand(Guid.NewGuid(), Storage.Invocation(), source.DraftId,
                source.Revision, source.RevisionContentHash,
                Storage.Options.RecipeReleases!.Policy.Reference, "storage query test");
            var grant = await Storage.Authorization.ReauthenticateAsync(new(Guid.NewGuid(),
                Storage.Invocation(), new(Permission.ReleaseRecipe, command.CorrelationId,
                    command.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe), Storage.Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            await Storage.WaitForVerifiedAsync();
            var result = await Releases.ReleaseAsync(command with
            {
                Invocation = Storage.Invocation(grant.GrantId)
            });
            Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted, result.Outcome.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
            Assert.NotNull(result.Recipe);
            await Storage.WaitForVerifiedAsync();
            return result.Recipe!;
        }

        internal async Task CloseWriterAndRestartAsync()
        {
            await Runtime.DisposeAsync();
            await Drafts.DisposeAsync();
            await Storage.RestartStoreAsync();
            Query = new SqliteReleasedRecipeQuery(Storage.Options);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Drafts.DisposeAsync();
            await Storage.DisposeAsync();
        }
    }

    private sealed class Factory : IVisionAlgorithmFactory
    {
        internal Factory(RecipeDraftContent content) => Descriptor = new(content.Algorithm.Algorithm,
            content.Algorithm.ConfigurationSchema, new("V115.Draft.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" },
                new("V115.Draft.Overlay", "1", 0, 64, 16)));

        public AlgorithmDescriptor Descriptor { get; }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("RecipeReleaseStorageTestMustNotCreateAlgorithm");
    }
}
