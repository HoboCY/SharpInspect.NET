using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionRecoverySchemaTests
{
    [Fact]
    public void V144_D01_ProductionRecoveryOptInRequiresInspectionIdentityAndAudit()
    {
        var options = new ProductionStoreOptions(Path.Combine(Path.GetTempPath(),
            "SharpInspect.Runtime.Tests", "V144-D01-unused.sqlite"))
        {
            ProductionRecovery = new ProductionRecoveryStoreOptions()
        };

        var exception = Assert.Throws<ArgumentException>(() => new SqliteCommandStore(options));

        Assert.StartsWith("ProductionRecoveryRequiresInspectionIdentityAndAudit", exception.Message);
        Assert.Equal("options", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V144_D02_Schema30InitializesReopensAndColdReadsWithOptionalPartIdentity(
        bool partIdentityEnabled)
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionInspections: new ProductionInspectionStoreOptions(),
            partIdentities: partIdentityEnabled ? new PartIdentityStoreOptions() : null,
            productionRecovery: new ProductionRecoveryStoreOptions());

        Assert.Equal(30, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(partIdentityEnabled ? 1 : 0, fixture.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='part_identity_events';"));
        await AssertSchema30ShapeAsync(fixture.Options.DatabasePath);
        await AssertReadableProductionIdentityAndAuditAsync(fixture, recoveryEnabled: true);

        await fixture.RestartStoreAsync();

        Assert.Equal(30, fixture.Scalar("PRAGMA user_version;"));
        await AssertReadableProductionIdentityAndAuditAsync(fixture, recoveryEnabled: true);
    }

    [Theory]
    [InlineData(false, 28)]
    [InlineData(true, 29)]
    public async Task V144_D03_LegacySchemaReopensWithOriginalOptionsAndRecoveryMigrationIsRejected(
        bool partIdentityEnabled, int expectedSchemaVersion)
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionInspections: new ProductionInspectionStoreOptions(),
            partIdentities: partIdentityEnabled ? new PartIdentityStoreOptions() : null);

        Assert.Equal(expectedSchemaVersion, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(partIdentityEnabled, fixture.Options.PartIdentities is not null);

        await fixture.RestartStoreAsync();

        Assert.Equal(expectedSchemaVersion, fixture.Scalar("PRAGMA user_version;"));
        await AssertReadableProductionIdentityAndAuditAsync(fixture, recoveryEnabled: false);

        await fixture.Store.DisposeAsync();
        var before = await SnapshotSqliteFilesAsync(fixture.Options.DatabasePath);
        await using var rejected = new SqliteCommandStore(CloneOptions(fixture.Options,
            productionRecovery: new ProductionRecoveryStoreOptions()));

        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("ProductionRecoveryGovernedMigrationRequired", initialized.ReasonCode);
        await rejected.DisposeAsync();

        var after = await SnapshotSqliteFilesAsync(fixture.Options.DatabasePath);
        AssertSqliteFilesEqual(before, after);
        Assert.Equal(expectedSchemaVersion, await ScalarAsync(fixture.Options.DatabasePath,
            "PRAGMA user_version;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V144_D04_Schema30RequiresRecoveryConfigurationWithoutMutatingFiles(
        bool partIdentityEnabled)
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionInspections: new ProductionInspectionStoreOptions(),
            partIdentities: partIdentityEnabled ? new PartIdentityStoreOptions() : null,
            productionRecovery: new ProductionRecoveryStoreOptions());

        await fixture.Store.DisposeAsync();
        var before = await SnapshotSqliteFilesAsync(fixture.Options.DatabasePath);
        await using var rejected = new SqliteCommandStore(CloneOptions(fixture.Options,
            productionRecovery: null));

        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("ProductionRecoveryConfigurationRequired", initialized.ReasonCode);
        await rejected.DisposeAsync();

        var after = await SnapshotSqliteFilesAsync(fixture.Options.DatabasePath);
        AssertSqliteFilesEqual(before, after);
        Assert.Equal(30, await ScalarAsync(fixture.Options.DatabasePath, "PRAGMA user_version;"));
    }

    private static async Task AssertReadableProductionIdentityAndAuditAsync(
        RecipeDraftStorageTests.Fixture fixture, bool recoveryEnabled)
    {
        var production = await new SqliteProductionInspectionHistoryQuery(fixture.Options)
            .ReadCurrentAsync();
        Assert.True(production.Available, production.ReasonCode);
        Assert.Equal("ProductionInspectionHistoryEmpty", production.ReasonCode);
        Assert.Null(production.Latest);

        var identity = await fixture.Store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(fixture.Options.LocalIdentity!.StationId, identity.StationId);
        Assert.NotNull(identity.Administrator);
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);

        var recovery = await new SqliteProductionRecoveryHistoryQuery(fixture.Options)
            .ReadCurrentAsync();
        if (recoveryEnabled)
        {
            Assert.True(recovery.Available, recovery.ReasonCode);
            Assert.Equal("ProductionInspectionHistoryEmpty", recovery.ReasonCode);
            Assert.Null(recovery.Latest);
            Assert.False(recovery.RecoveryRequired);

            var pending = await new SqliteProductionRecoveryHistoryQuery(fixture.Options)
                .QueryPendingAsync();
            Assert.True(pending.Available, pending.ReasonCode);
            Assert.Empty(pending.Items);
            Assert.Equal(0, pending.PendingCount);
        }
        else
        {
            Assert.False(recovery.Available);
            Assert.Equal("ProductionRecoveryConfigurationRequired", recovery.ReasonCode);
        }
    }

    private static async Task AssertSchema30ShapeAsync(string databasePath)
    {
        var auditSql = await ScalarTextAsync(databasePath,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='audit_entries';");
        Assert.Contains("PartIdentityPosition INTEGER UNIQUE", auditSql, StringComparison.Ordinal);

        var commandSql = await ScalarTextAsync(databasePath,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='command_facts';");
        Assert.Contains("CHECK(CommandKind IN", commandSql, StringComparison.Ordinal);
        Assert.Contains("53", commandSql, StringComparison.Ordinal);
    }

    private static ProductionStoreOptions CloneOptions(ProductionStoreOptions source,
        ProductionRecoveryStoreOptions? productionRecovery) => new(source.DatabasePath)
    {
        CommitTimeout = source.CommitTimeout,
        QueryTimeout = source.QueryTimeout,
        QueueCapacity = source.QueueCapacity,
        AuditIntegrityPolicy = source.AuditIntegrityPolicy,
        LocalIdentity = source.LocalIdentity,
        AlarmPolicy = source.AlarmPolicy,
        ExternalAuditAnchor = source.ExternalAuditAnchor,
        AlgorithmResultArchive = source.AlgorithmResultArchive,
        RecipeDrafts = source.RecipeDrafts,
        CameraSetup = source.CameraSetup,
        CameraRecovery = source.CameraRecovery,
        CameraNetwork = source.CameraNetwork,
        ImagingSetup = source.ImagingSetup,
        CalibrationSessions = source.CalibrationSessions,
        CalibrationGovernance = source.CalibrationGovernance,
        RecipeReleases = source.RecipeReleases,
        PlcResultContracts = source.PlcResultContracts,
        RecipeActivations = source.RecipeActivations,
        PreviewSessions = source.PreviewSessions,
        CalibrationImports = source.CalibrationImports,
        ManualInspections = source.ManualInspections,
        ProductionAdmission = source.ProductionAdmission,
        StationQualifications = source.StationQualifications,
        RecipeTransfers = source.RecipeTransfers,
        TraceStoragePolicies = source.TraceStoragePolicies,
        QualificationCycles = source.QualificationCycles,
        PlcCommunication = source.PlcCommunication,
        ProductionInspections = source.ProductionInspections,
        PartIdentities = source.PartIdentities,
        ProductionRecovery = productionRecovery
    };

    private static async Task<IReadOnlyDictionary<string, byte[]>> SnapshotSqliteFilesAsync(
        string databasePath)
    {
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
                files[path] = await File.ReadAllBytesAsync(path);
        }
        return files;
    }

    private static void AssertSqliteFilesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(path => path, StringComparer.Ordinal),
            actual.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (var path in expected.Keys)
            Assert.Equal(expected[path], actual[path]);
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
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
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(string databasePath, string sql)
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
        return (string?)await command.ExecuteScalarAsync() ?? string.Empty;
    }
}
