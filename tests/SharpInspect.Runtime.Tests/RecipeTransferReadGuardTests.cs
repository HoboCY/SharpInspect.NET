using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeTransferReadGuardTests
{
    [Fact]
    public async Task V138_G01_OptionalTransferStoreKeepsDraftAlarmArchiveAndIdentityQueriesVerified()
    {
        var alarms = new AlarmPolicy("Transfer.Alarm", "1", new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery", AlarmSeverity.Warning,
                ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null)
        }, TimeSpan.FromSeconds(30));
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(enableArchive: true,
            alarmPolicy: alarms, recipeTransfers: Options());
        Assert.Equal(24, fixture.Scalar("PRAGMA user_version;"));
        var saved = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("普通本地草稿"), "中文审计原因");
        Assert.True(saved.Saved, saved.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var draft = await new SqliteRecipeDraftQuery(fixture.Options).ReadAsync(saved.Revision!.DraftId);
        Assert.True(draft.Available, draft.ReasonCode);
        var commands = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(new());
        Assert.Equal(fixture.Scalar("SELECT COALESCE(MAX(Position),0) FROM command_facts;"), commands.ThroughPosition);
        var alarmHistory = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(new());
        Assert.True(alarmHistory.Available, alarmHistory.ReasonCode);
        var results = await new SqliteAlgorithmResultQuery(fixture.Options).QueryAsync(new());
        Assert.True(results.Available, results.ReasonCode);
        var transfers = await new SqliteRecipeTransferQuery(fixture.Options).QueryAsync(new());
        Assert.True(transfers.Available, transfers.ReasonCode);
        Assert.Empty(transfers.Records);
        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(new(0, 1000));
        Assert.Equal(AuditIntegrityState.Verified, report.State);
    }

    [Fact]
    public async Task V138_G02_TamperedTransferConfigurationBlocksUnrelatedWriterAndColdQueries()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            verificationInterval: TimeSpan.FromSeconds(30), recipeTransfers: Options());
        var first = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("before tamper"), "before tamper");
        Assert.True(first.Saved, first.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var revisions = fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;");
        var audit = fixture.Scalar("SELECT COUNT(*) FROM audit_entries;");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var lookup = connection.CreateCommand();
            lookup.CommandText = "SELECT sql FROM sqlite_master WHERE name='recipe_transfer_config_immutable_update';";
            var trigger = (string?)await lookup.ExecuteScalarAsync();
            Assert.NotNull(trigger);
            await using var tamper = connection.CreateCommand();
            tamper.CommandText = "DROP TRIGGER recipe_transfer_config_immutable_update; " +
                "UPDATE recipe_transfer_store_config SET PortablePolicyVersion='tampered'; " + trigger;
            await tamper.ExecuteNonQueryAsync();
        }
        var history = await new SqliteRecipeTransferQuery(fixture.Options).QueryAsync(new());
        Assert.False(history.Available);
        var draft = await new SqliteRecipeDraftQuery(fixture.Options).ReadAsync(first.Revision!.DraftId);
        Assert.False(draft.Available);
        var rejected = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("after tamper"), "must reject corrupted co-enabled history");
        Assert.False(rejected.Saved);
        Assert.Equal(revisions, fixture.Scalar("SELECT COUNT(*) FROM recipe_draft_revisions;"));
        Assert.Equal(audit, fixture.Scalar("SELECT COUNT(*) FROM audit_entries;"));
    }

    private static RecipeTransferStoreOptions Options() => new()
    { PortablePolicy = new("Transfer.ReadGuard", "1", Array.Empty<RecipeTransferPortableContract>()) };

    [Theory]
    [InlineData(false, "RecipeTransferGovernedMigrationRequired")]
    [InlineData(true, "RecipeTransferConfigurationRequired")]
    public async Task V138_G04_ColdQueriesPreserveConfigurationAndMigrationReasons(bool enabled, string expectedReason)
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            recipeTransfers: enabled ? Options() : null);
        var altered = new ProductionStoreOptions(fixture.Options.DatabasePath)
        {
            AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy,
            LocalIdentity = fixture.Options.LocalIdentity,
            RecipeDrafts = fixture.Options.RecipeDrafts,
            RecipeTransfers = enabled ? null : Options(),
            QueryTimeout = fixture.Options.QueryTimeout
        };
        var transfers = await new SqliteRecipeTransferQuery(altered).QueryAsync(new());
        Assert.False(transfers.Available);
        Assert.Equal(expectedReason, transfers.ReasonCode);
        var integrity = await new SqliteAuditIntegrityQuery(altered).VerifyAsync(new(0, 1000));
        Assert.Equal(AuditIntegrityState.Faulted, integrity.State);
        Assert.Equal(expectedReason, integrity.ReasonCode);
    }

    [Fact]
    public async Task V138_G03_ColdTransferQueriesHonorRequiredExternalAnchor()
    {
        var anchor = new QueryAnchor();
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(recipeTransfers: Options(),
            externalAuditAnchor: anchor, requireExternalAnchor: true);
        fixture.Authorization.Dispose();
        await fixture.Sessions.DisposeAsync();
        await fixture.Store.DisposeAsync();
        var query = new SqliteRecipeTransferQuery(fixture.Options);
        anchor.ReturnReceipt = false;
        Assert.Equal("AuditExternalAnchorMismatch", (await query.ReadTrustAsync()).ReasonCode);
        Assert.Equal("AuditExternalAnchorMismatch", (await query.ReadSigningKeysAsync()).ReasonCode);
        Assert.Equal("AuditExternalAnchorMismatch", (await query.ReadImportAsync(Guid.NewGuid())).ReasonCode);
        Assert.Equal("AuditExternalAnchorMismatch", (await query.QueryAsync(new())).ReasonCode);
        anchor.ReturnReceipt = true;
        Assert.True((await query.ReadTrustAsync()).Available);
        Assert.True((await query.ReadSigningKeysAsync()).Available);
        Assert.True((await query.QueryAsync(new())).Available);
    }

    private sealed class QueryAnchor : IExternalAuditAnchor
    {
        private readonly ConcurrentDictionary<Guid, AuditAnchorReceipt> _receipts = new();
        internal bool ReturnReceipt { get; set; } = true;
        public ValueTask<AuditAnchorReceipt> DeliverAsync(AuditCheckpoint checkpoint, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = _receipts.GetOrAdd(checkpoint.CheckpointId, _ => new AuditAnchorReceipt(
                checkpoint.CheckpointId, checkpoint.StationId, checkpoint.Sequence, checkpoint.HeadHash,
                "v131-query-anchor", "receipt-" + checkpoint.CheckpointId.ToString("N"),
                checkpoint.PolicyHash, checkpoint.SigningKeyId, AuditChainDatabase.CheckpointDigest(checkpoint),
                DateTimeOffset.UtcNow));
            return ValueTask.FromResult(receipt);
        }

        public ValueTask<AuditAnchorReceipt?> ReadLatestAsync(string stationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReturnReceipt ? _receipts.Values.Where(item => item.StationId == stationId)
                .OrderByDescending(item => item.Sequence).FirstOrDefault() : null);
        }
    }
}
