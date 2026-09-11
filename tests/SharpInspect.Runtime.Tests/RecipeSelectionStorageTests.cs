using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeSelectionStorageTests
{
    [Fact]
    public async Task V146_D01_Schema31InitializesReopensAndDefaultsToLocalOperatorOnly()
    {
        await using var fixture = await CreateFixtureAsync();
        Assert.Equal(31, fixture.Scalar("PRAGMA user_version;"));
        var query = new SqliteRecipeSelectionQuery(fixture.Options);
        var read = await query.ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(RecipeSelectionMode.LocalOperatorOnly, read.Mode);
        Assert.Null(read.Revision);
        var history = await query.QueryAsync(new RecipeChangeHistoryFilter());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Events);
        await fixture.RestartStoreAsync();
        read = await query.ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        var activation = await new SqliteRecipeActivationQuery(fixture.Options).ReadCurrentAsync();
        Assert.True(activation.Available, activation.ReasonCode);
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);
    }

    [Fact]
    public async Task V146_D02_StableIdentityIsDeduplicatedAcrossRuntimeRestartAndCodeChange()
    {
        await using var fixture = await CreateFixtureAsync();
        var request = Request(17, 3, 12);
        Assert.True((await Write(fixture, request, RecipeChangeEventKind.RequestObserved)).Committed);
        await CompleteRejectedAsync(fixture, request);
        await fixture.RestartStoreAsync();
        var duplicate = await Write(fixture, Request(17, 3, 99), RecipeChangeEventKind.RequestObserved);
        Assert.False(duplicate.Committed);
        Assert.True(duplicate.Duplicate);
        var nextEpoch = await Write(fixture, Request(18, 3, 12), RecipeChangeEventKind.RequestObserved);
        Assert.True(nextEpoch.Committed, nextEpoch.ReasonCode);
        var page = await new SqliteRecipeSelectionQuery(fixture.Options).QueryAsync(new RecipeChangeHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(7, page.Events.Count);
        Assert.Equal(2, page.Events.Count(value => value.Kind == RecipeChangeEventKind.RequestObserved));
    }

    [Fact]
    public async Task V146_D03_PendingHandshakesReserveEveryRemainingFactBeforeAdmission()
    {
        await using var fixture = await CreateFixtureAsync(new RecipeSelectionStoreOptions { MaximumHandshakeEvents = 16 });
        var first = Request(1, 1, 1); var second = Request(1, 2, 1); var third = Request(1, 3, 1);
        Assert.True((await Write(fixture, first, RecipeChangeEventKind.RequestObserved)).Committed);
        Assert.True((await Write(fixture, second, RecipeChangeEventKind.RequestObserved)).Committed);
        var rejected = await Write(fixture, third, RecipeChangeEventKind.RequestObserved);
        Assert.False(rejected.Committed);
        Assert.Equal("RecipeChangeEventCapacityExceeded", rejected.ReasonCode);
        await CompleteRejectedAsync(fixture, first);
        await CompleteRejectedAsync(fixture, second);
        var page = await new SqliteRecipeSelectionQuery(fixture.Options).QueryAsync(new RecipeChangeHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(12, page.Events.Count);
        Assert.Equal(2, page.Events.Count(value => value.Kind == RecipeChangeEventKind.ResetObserved));
    }

    [Fact]
    public async Task V146_D04_FaultBeforeActivationRetirementKeepsTheDecisionSlotWithoutForgingAcknowledgement()
    {
        await using var fixture = await CreateFixtureAsync();
        var request = Request(4, 8, 16);
        Assert.True((await Write(fixture, request, RecipeChangeEventKind.RequestObserved)).Committed);
        var fault = await Write(fixture, request, RecipeChangeEventKind.ProtocolFault);
        Assert.True(fault.Committed, fault.ReasonCode);
        var decision = await Write(fixture, request, RecipeChangeEventKind.DecisionCommitted);
        Assert.True(decision.Committed, decision.ReasonCode);
        var response = await Write(fixture, request, RecipeChangeEventKind.ResponsePublished);
        Assert.False(response.Committed);
        var page = await new SqliteRecipeSelectionQuery(fixture.Options).QueryAsync(new RecipeChangeHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(3, page.Events.Count);
        Assert.DoesNotContain(page.Events, value => value.Kind is RecipeChangeEventKind.AcknowledgementObserved or RecipeChangeEventKind.ResetObserved);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task V146_D05_Schema31PreservesOptionalPartIdentityAndRecoveryHistory(bool partIdentity, bool recovery)
    {
        await using var fixture = await CreateFixtureAsync(partIdentity: partIdentity, recovery: recovery);
        if (partIdentity)
        {
            var part = await new SqlitePartIdentityHistoryQuery(fixture.Options).ReadCurrentAsync();
            Assert.True(part.Available, part.ReasonCode);
        }
        if (recovery)
        {
            var result = await new SqliteProductionRecoveryHistoryQuery(fixture.Options).ReadCurrentAsync();
            Assert.True(result.Available, result.ReasonCode);
        }
        await fixture.RestartStoreAsync();
        var selection = await new SqliteRecipeSelectionQuery(fixture.Options).ReadCurrentAsync();
        Assert.True(selection.Available, selection.ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task V146_D06_OptInAndMissingConfigurationRequireExplicitMigrationWithoutChangingDatabase(bool initiallyEnabled)
    {
        await using var fixture = await CreateFixtureAsync(enabled: initiallyEnabled);
        await fixture.Store.DisposeAsync();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);
        var source = fixture.Options;
        var changed = new ProductionStoreOptions(source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy, LocalIdentity = source.LocalIdentity,
            RecipeDrafts = source.RecipeDrafts, RecipeReleases = source.RecipeReleases,
            PlcResultContracts = source.PlcResultContracts, CameraSetup = source.CameraSetup,
            RecipeActivations = source.RecipeActivations, PlcCommunication = source.PlcCommunication,
            RecipeSelections = initiallyEnabled ? null : new(), CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout, QueueCapacity = source.QueueCapacity
        };
        await using var rejected = new SqliteCommandStore(changed);
        var result = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.Committed);
        Assert.Equal(initiallyEnabled ? "RecipeSelectionConfigurationRequired" : "RecipeSelectionGovernedMigrationRequired", result.ReasonCode);
        await rejected.DisposeAsync();
        Assert.Equal(before, await File.ReadAllBytesAsync(source.DatabasePath));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("index")]
    [InlineData("delete")]
    public async Task V146_D07_FullHistoryAndWriterRejectOfflineHandshakeCorruption(string corruption)
    {
        await using var fixture = await CreateFixtureAsync();
        var request = Request(31, 7, 7);
        Assert.True((await Write(fixture, request, RecipeChangeEventKind.RequestObserved)).Committed);
        await CompleteRejectedAsync(fixture, request);
        await fixture.Store.DisposeAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            var trigger = corruption == "delete" ? "recipe_change_event_immutable_delete" : "recipe_change_event_immutable_update";
            await using var lookup = connection.CreateCommand();
            lookup.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name;";
            lookup.Parameters.AddWithValue("$name", trigger);
            var original = Assert.IsType<string>(await lookup.ExecuteScalarAsync());
            await using var mutate = connection.CreateCommand();
            var change = corruption switch
            {
                "payload" => "UPDATE recipe_change_events SET Payload='AAAA' WHERE Position=1;",
                "index" => "UPDATE recipe_change_events SET RequestIdentityHash='" + new string('C', 64) + "' WHERE Position=1;",
                _ => "DELETE FROM recipe_change_events WHERE Position=6;"
            };
            mutate.CommandText = "DROP TRIGGER " + trigger + ";" + change + original;
            await mutate.ExecuteNonQueryAsync();
        }
        var query = new SqliteRecipeSelectionQuery(fixture.Options);
        var history = await query.QueryAsync(new RecipeChangeHistoryFilter());
        Assert.False(history.Available);
        Assert.Empty(history.Events);
        Assert.False((await query.ReadCurrentAsync()).Available);
        await using var reopened = new SqliteCommandStore(fixture.Options);
        Assert.False((await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15))).Committed);
    }

    [Fact]
    public async Task V146_D08_OrdinaryCommandWriterCannotConsumePendingHandshakeCompletionReserve()
    {
        await using var fixture = await CreateFixtureAsync(maximumAuditEntries: 202);
        var request = Request(41, 9, 7);
        Assert.True((await Write(fixture, request, RecipeChangeEventKind.RequestObserved)).Committed);
        await fixture.WaitForVerifiedAsync();
        StoreWriteResult? last = null;
        var committed = 0;
        var fillDeadline = DateTime.UtcNow.AddMinutes(4);
        for (var index = 0; index < 202; index++)
        {
            Assert.True(DateTime.UtcNow < fillDeadline, "Audit capacity fixture did not reach its bounded limit");
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow, AuditedCommandKind.GracefulProductionStop, CommandSource.Integration,
                "V146.UntrustedCaller", null, null, CommandAuditPhase.Outcome, CommandDisposition.Rejected, "PermissionDenied");
            last = await fixture.Store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(4)));
            if (last.ReasonCode == "AuditRecheckPending")
            {
                await fixture.WaitForVerifiedAsync();
                index--;
                continue;
            }
            if (!last.Committed) break;
            committed++;
        }
        Assert.True(committed > 0, last?.ReasonCode);
        Assert.NotNull(last);
        Assert.False(last.Committed);
        Assert.Equal("AuditVerificationCapacityExceeded", last.ReasonCode);
        // An unrelated writer reaching its limit cannot consume the five normal
        // remaining facts (or poison their already admitted reserve).
        await CompleteRejectedAsync(fixture, request);
        var page = await new SqliteRecipeSelectionQuery(fixture.Options).QueryAsync(new RecipeChangeHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(6, page.Events.Count);
        Assert.Equal(RecipeChangeEventKind.ResetObserved, page.Events.Last().Kind);
    }

    internal static Task<RecipeDraftStorageTests.Fixture> CreateFixtureAsync(RecipeSelectionStoreOptions? selections = null,
        bool partIdentity = false, bool recovery = false, bool enabled = true, int? maximumAuditEntries = null) =>
        RecipeDraftStorageTests.Fixture.CreateAsync(recipeReleases: new(new RecipeGovernancePolicy("V146.Release", "1",
            RecipeGovernanceMode.SingleApproverRelease)), plcResultContracts: new(), cameraSetup: new(),
            recipeActivations: new(), plcCommunication: new(), recipeSelections: enabled ? selections ?? new() : null,
            productionInspections: recovery ? new() : null, partIdentities: partIdentity ? new() : null,
            productionRecovery: recovery ? new() : null, maximumAuditEntries: maximumAuditEntries);

    private static RecipeChangeRequestEvidence Request(uint epoch, uint sequence, uint code) => new(Guid.NewGuid(),
        new string('A', 64), new("V146.Protocol", "1", new string('B', 64)), epoch, sequence, code, null,
        RecipeSelectionPolicy.Default.Reference, null, null, DateTimeOffset.UtcNow);

    private static ValueTask<RecipeChangeWriteResult> Write(RecipeDraftStorageTests.Fixture fixture,
        RecipeChangeRequestEvidence request, RecipeChangeEventKind kind) => fixture.Store.AppendRecipeChangeEventAsync(request,
            kind, kind == RecipeChangeEventKind.RequestObserved ? null : RecipeChangeOutcome.RejectedUnknownCode,
            kind == RecipeChangeEventKind.RequestObserved ? null : RecipeChangeReason.LocalOperatorOnly,
            kind == RecipeChangeEventKind.RequestObserved ? "RecipeChangeRequestObserved" : "RecipeChangeLocalOperatorOnly",
            null, new StoreDeadline(TimeSpan.FromSeconds(4)), CancellationToken.None);

    private static async Task CompleteRejectedAsync(RecipeDraftStorageTests.Fixture fixture, RecipeChangeRequestEvidence request)
    {
        foreach (var kind in new[] { RecipeChangeEventKind.DecisionCommitted, RecipeChangeEventKind.ResponsePublished,
            RecipeChangeEventKind.AcknowledgementObserved, RecipeChangeEventKind.ResponseCleared, RecipeChangeEventKind.ResetObserved })
        {
            var result = await Write(fixture, request, kind);
            Assert.True(result.Committed, result.ReasonCode);
        }
    }
}
