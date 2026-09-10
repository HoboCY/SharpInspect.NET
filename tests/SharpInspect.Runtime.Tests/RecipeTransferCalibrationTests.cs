#pragma warning disable CA1416

using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Schema-24 co-enablement regression. Calibration reads and writes must use
/// the complete transfer guard when the transfer ledger is present alongside
/// the calibration ledger.
/// </summary>
public sealed class RecipeTransferCalibrationTests
{
    [Fact]
    public async Task V138_G05_TamperedTransferConfigurationBlocksCalibrationReadAndAppend()
    {
        var seed = RecipeTransferContentCodecTests.Fixture();
        var transferOptions = new RecipeTransferStoreOptions
        {
            PortablePolicy = seed.Policy
        };
        await using var fixture = await CalibrationSessionRuntimeTests.Fixture.CreateAsync(
            withDevelopmentFixture: true, recipeTransfers: transferOptions);
        await fixture.WaitForHealthySourceAsync();

        var (_, accepted) = await fixture.StartSessionWhenCameraIdleAsync();
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        var sessionId = await WaitForCollectingSessionAsync(fixture);
        var healthy = await fixture.Store.ReadCalibrationSessionAsync(sessionId);
        Assert.True(healthy.Available, healthy.ReasonCode);
        Assert.Equal(CalibrationSessionPhase.Collecting, healthy.Evidence!.State.Phase);

        // Finish the real calibration owner before the offline config mutation;
        // the session evidence and the subsequent direct append remain durable
        // store operations, while no runtime task can race the tamper.
        await fixture.Runtime.DisposeAsync();
        await CalibrationSessionRuntimeTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        var sessionCount = await ScalarAsync(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM calibration_sessions;");
        var eventCount = await ScalarAsync(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM calibration_session_events;");
        var auditCount = await ScalarAsync(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM audit_entries;");

        await ExecuteSqlAsync(fixture.Options.DatabasePath,
            "DROP TRIGGER recipe_transfer_config_immutable_update;");
        try
        {
            await ExecuteSqlAsync(fixture.Options.DatabasePath, @"
                UPDATE recipe_transfer_store_config
                SET BindingHash=$bindingHash WHERE Id=1;",
                ("$bindingHash", new string('B', 64)));
        }
        finally
        {
            await ExecuteSqlAsync(fixture.Options.DatabasePath, @"
                CREATE TRIGGER recipe_transfer_config_immutable_update
                BEFORE UPDATE ON recipe_transfer_store_config BEGIN
                    SELECT RAISE(ABORT,'ImmutableRecipeTransferConfiguration');
                END;");
        }

        var cold = await fixture.Store.ReadCalibrationSessionAsync(sessionId);
        Assert.False(cold.Available);
        Assert.NotEqual("CalibrationSessionAvailable", cold.ReasonCode);

        var attempted = new CalibrationSessionEvent(Guid.NewGuid(), sessionId, Guid.NewGuid(),
            CalibrationSessionPhase.Collecting, CalibrationSessionOutcome.Pending,
            "transfer configuration tampered", DateTimeOffset.UtcNow);
        var append = await fixture.Store.AppendCalibrationEventAsync(attempted, null,
            CancellationToken.None, new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.False(append.Committed);

        Assert.Equal(sessionCount, await ScalarAsync(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM calibration_sessions;"));
        Assert.Equal(eventCount, await ScalarAsync(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM calibration_session_events;"));
        Assert.Equal(auditCount, await ScalarAsync(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM audit_entries;"));
    }

    private static async Task<Guid> WaitForCollectingSessionAsync(
        CalibrationSessionRuntimeTests.Fixture fixture)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = await fixture.Runtime.GetSnapshotAsync();
            if (snapshot.CalibrationSession is
                { Phase: CalibrationSessionPhase.Collecting, OperationInProgress: false } session)
                return session.SessionId;
            if (snapshot.Store.State == HealthState.Faulted)
                throw new XunitException(snapshot.Store.ReasonCode);
            await Task.Delay(20);
        }

        throw new XunitException("Calibration collecting session did not settle.");
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
