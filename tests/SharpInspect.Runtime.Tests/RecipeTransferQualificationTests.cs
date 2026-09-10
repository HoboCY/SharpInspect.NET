using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V138_G08_Schema24QualificationRetainsTerminalReserveWithTransferLedger()
    {
        var transferOptions = new RecipeTransferStoreOptions
        {
            PortablePolicy = new RecipeTransferPortablePolicy(
                "V138.Qualification.Transfer", "1", Array.Empty<RecipeTransferPortableContract>())
        };
        await using var harness = await QualificationHarness.CreateAsync(
            maximumRuns: 1, blockStimulus: true, recipeTransfers: transferOptions, maximumAuditEntries: 512);

        Assert.Equal(RecipeTransferStoreOptions.SchemaVersion,
            await ReadQualificationTransferScalarAsync(harness.Fixture, "PRAGMA user_version;"));
        Assert.Equal(1, await ReadQualificationTransferScalarAsync(harness.Fixture,
            "SELECT COUNT(*) FROM recipe_transfer_store_config;"));

        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "schema-24 qualification start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "schema-24 qualification did not reach stimulus wait");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        var reservedTerminalAudit = await ReadQualificationAuditReserveAsync(harness.Fixture);
        Assert.True(reservedTerminalAudit > 0);

        // A nonterminal qualification event reserves its recovery and terminal
        // audit tail. Exhausting the generic audit budget must therefore stop
        // ordinary writes while leaving this accepted session closable.
        var exhausted = await ExhaustQualificationGenericAuditCapacityAsync(harness);
        Assert.Contains("CapacityExceeded", exhausted.ReasonCode, StringComparison.Ordinal);

        var exit = await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, abort: true));
        AssertAccepted(exit, "schema-24 qualification abort after generic capacity exhaustion");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.SessionId == sessionId &&
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "schema-24 qualification could not spend its reserved terminal tail");

        Assert.Equal(StationQualificationRestorationState.Restored, closed.Restoration);
        Assert.False(closed.RecoveryRequired);
        var station = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        Assert.Equal(ExclusiveMode.None, station.Mode);
        Assert.False(station.Ready);
        Assert.Equal(0, await ReadQualificationAuditReserveAsync(harness.Fixture));

        var history = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(history.Available, history.ReasonCode);
        Assert.Contains(history.Events, value => value.Phase == StationQualificationSessionPhase.Closed &&
            value.Terminal && value.Restoration == StationQualificationRestorationState.Restored);

        var transfers = await new SqliteRecipeTransferQuery(harness.Fixture.Options).QueryAsync(new());
        Assert.True(transfers.Available, transfers.ReasonCode);
        Assert.Empty(transfers.Records);
        var integrity = await new SqliteAuditIntegrityQuery(harness.Fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0,
                harness.Fixture.Options.AuditIntegrityPolicy!.MaximumVerificationEntries -
                harness.Fixture.Options.AuditIntegrityPolicy.CheckpointEveryEntries));
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);
        Assert.Equal(0, await ReadQualificationTransferScalarAsync(harness.Fixture,
            "SELECT COUNT(*) FROM recipe_transfer_events;"));
    }

    private static async Task<long> ReadQualificationTransferScalarAsync(
        QualificationFixture fixture, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<StoreWriteResult> ExhaustQualificationGenericAuditCapacityAsync(
        QualificationHarness harness)
    {
        StoreWriteResult? exhausted = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
        for (var index = 0; index < 1024 && DateTime.UtcNow < deadline; index++)
        {
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(harness.Fixture.Store);
            var result = await harness.Fixture.Store.AppendAsync(QualificationPaddingFact(),
                new StoreDeadline(harness.Fixture.Options.CommitTimeout));
            // The periodic verifier can begin between the observed Verified
            // snapshot and enqueue. This refusal consumes no audit capacity.
            if (!result.Committed && result.ReasonCode == "AuditRecheckPending") continue;
            if (!result.Committed)
            {
                exhausted = result;
                break;
            }
        }

        Assert.NotNull(exhausted);
        return exhausted!;
    }

    private static async Task<long> ReadQualificationAuditReserveAsync(
        QualificationFixture fixture)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return SqliteCommandStore.ReadStationQualificationAuditReserve(connection.Handle!,
            new StoreDeadline(fixture.Options.QueryTimeout));
    }

    private static CommandAuditFact QualificationPaddingFact() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        AuditedCommandKind.ArmProduction, CommandSource.PhysicalConsole,
        "V138-qualification-budget-padding", null, null, CommandAuditPhase.Outcome,
        CommandDisposition.Rejected, "V138QualificationBudgetPadding");
}
