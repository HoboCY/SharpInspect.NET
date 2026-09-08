#pragma warning disable CA1416

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Schema-13 admission tests for the immutable imaging setup ledger.  These
/// tests exercise the signed local store and its independent read boundaries;
/// they do not create a camera provider or touch physical hardware.
/// </summary>
public sealed class ImagingSetupSchemaCompatibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task V123_S01_Schema13InitializesForEachOptionalLedgerCombination(
        bool includeRecovery, bool includeNetwork)
    {
        await using var fixture = await SchemaFixture.CreateAsync(
            imaging: true, includeRecovery, includeNetwork);

        Assert.Equal(13L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_setup_store_config;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM imaging_setup_store_config;"));
        Assert.Equal(includeRecovery ? 1L : 0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_recovery_store_config';"));
        Assert.Equal(includeNetwork ? 1L : 0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_network_store_config';"));
        if (includeRecovery)
            Assert.Equal(1L, await fixture.ScalarAsync(
                "SELECT COUNT(*) FROM camera_recovery_store_config;"));
        if (includeNetwork)
            Assert.Equal(1L, await fixture.ScalarAsync(
                "SELECT COUNT(*) FROM camera_network_store_config;"));

        var audit = await VerifyAsync(fixture.Options);
        Assert.Equal(AuditIntegrityState.Verified, audit.State);
        var imaging = await fixture.Store.ReadImagingSetupAsync("TopCamera");
        Assert.Empty(imaging.State.Revisions);
        var trace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(PageSize: 100));
        Assert.NotNull(trace);
        Assert.True(trace.ThroughPosition >= 0);

        await fixture.StopStoreAsync();
        await using var restarted = new SqliteCommandStore(fixture.Options);
        var initialized = await restarted.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await SchemaFixture.WaitForVerifiedAsync(restarted);

        var restartedAudit = await VerifyAsync(fixture.Options);
        Assert.Equal(AuditIntegrityState.Verified, restartedAudit.State);
        var restartedImaging = await restarted.ReadImagingSetupAsync("TopCamera");
        Assert.Empty(restartedImaging.State.Revisions);
        var restartedTrace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(PageSize: 100));
        Assert.True(restartedTrace.ThroughPosition >= 0);
    }

    [Theory]
    [InlineData("missing-imaging")]
    [InlineData("missing-camera")]
    [InlineData("missing-recovery")]
    [InlineData("missing-network")]
    [InlineData("extra-recovery")]
    [InlineData("extra-network")]
    public async Task V123_S02_Schema13RejectsMissingOrMismatchedLedgerConfiguration(
        string mismatch)
    {
        var initialRecovery = mismatch is "missing-recovery";
        var initialNetwork = mismatch is "missing-network";
        var forceAlarmPolicy = mismatch == "extra-recovery";
        await using var fixture = await SchemaFixture.CreateAsync(
            imaging: true, initialRecovery, initialNetwork, forceAlarmPolicy);
        await fixture.StopStoreAsync();

        // A read-only Microsoft.Data.Sqlite connection may materialize an empty
        // WAL while opening the database.  Complete every baseline observation
        // before taking the byte fingerprint so that the fingerprint includes
        // the actual DB and WAL state being compared.
        var beforeVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var reopened = OptionsFor(fixture.Options,
            imaging: mismatch != "missing-imaging",
            camera: mismatch != "missing-camera",
            recovery: mismatch switch
            {
                "missing-recovery" => false,
                "extra-recovery" => true,
                _ => initialRecovery
            },
            network: mismatch switch
            {
                "missing-network" => false,
                "extra-network" => true,
                _ => initialNetwork
            });

        if (mismatch == "missing-camera")
        {
            var error = Assert.Throws<ArgumentException>(() => new SqliteCommandStore(reopened));
            Assert.StartsWith("ImagingSetupRequiresCameraSetupIdentityAndAudit", error.Message,
                StringComparison.Ordinal);
        }
        else
        {
            await using var rejected = new SqliteCommandStore(reopened);
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal(ExpectedReason(mismatch), initialized.ReasonCode);
        }

        var afterVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var after = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        Assert.Equal(before, after);
        Assert.Equal(beforeVersion, afterVersion);
    }

    [Theory]
    [InlineData(10, false, false)]
    [InlineData(11, true, false)]
    [InlineData(12, false, true)]
    public async Task V123_S03_ExistingSchemaCannotAutoMigrateToImagingSetup(
        int expectedLegacySchema, bool includeRecovery, bool includeNetwork)
    {
        await using var fixture = await SchemaFixture.CreateAsync(
            imaging: false, includeRecovery, includeNetwork);
        await fixture.StopStoreAsync();

        // Normalize the read-only observation before hashing.  This preserves
        // the real DB and WAL bytes for the before/after comparison.
        var beforeVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        Assert.Equal(expectedLegacySchema, beforeVersion);
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var reopened = OptionsFor(fixture.Options, imaging: true, camera: true,
            recovery: includeRecovery, network: includeNetwork);
        await using var rejected = new SqliteCommandStore(reopened);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(initialized.Committed);
        Assert.Equal("ImagingSetupGovernedMigrationRequired", initialized.ReasonCode);
        var afterVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var after = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        Assert.Equal(before, after);
        Assert.Equal(beforeVersion, afterVersion);
    }

    [Theory]
    [InlineData(true, false, false, false, "CameraRecoveryConfigurationRequired")]
    [InlineData(false, false, true, false, "CameraRecoveryConfigurationRequired")]
    [InlineData(false, true, false, false, "CameraNetworkConfigurationRequired")]
    [InlineData(false, false, false, true, "CameraNetworkConfigurationRequired")]
    public async Task V123_S04_TraceRejectsOptionalLedgerMismatchWithoutChangingDatabase(
        bool storedRecovery, bool storedNetwork, bool queryRecovery, bool queryNetwork,
        string expectedReason)
    {
        await using var fixture = await SchemaFixture.CreateAsync(
            imaging: true, storedRecovery, storedNetwork);
        await fixture.StopStoreAsync();
        Assert.Equal(13L, await fixture.ScalarAsync("PRAGMA user_version;"));
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var queryOptions = OptionsFor(fixture.Options, imaging: true, camera: true,
            recovery: queryRecovery, network: queryNetwork);

        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(queryOptions).QueryAsync(new CommandTraceFilter()).AsTask());

        Assert.Equal(expectedReason, rejected.Message);
        Assert.Equal(before, await DatabaseFingerprintAsync(fixture.Options.DatabasePath));
    }

    private static string ExpectedReason(string mismatch) => mismatch switch
    {
        "missing-imaging" => "ImagingSetupConfigurationRequired",
        // Schema-13 uses an exact shape gate before AuditChainDatabase's
        // per-ledger configuration checks.  Changing optional Recovery or
        // Network table presence therefore has the stable, earlier rejection
        // reason StoreSchemaMismatch.
        "missing-recovery" or "extra-recovery" or
            "missing-network" or "extra-network" => "StoreSchemaMismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
    };

    private static ProductionStoreOptions OptionsFor(ProductionStoreOptions source,
        bool imaging, bool camera, bool recovery, bool network) => new(source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = recovery || source.AlarmPolicy is not null
                ? source.AlarmPolicy ?? SchemaFixture.RecoveryAlarmPolicy()
                : null,
            CameraSetup = camera ? new CameraSetupStoreOptions() : null,
            CameraRecovery = recovery ? new CameraRecoveryStoreOptions() : null,
            CameraNetwork = network ? new CameraNetworkStoreOptions() : null,
            ImagingSetup = imaging ? new ImagingSetupStoreOptions() : null,
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity
        };

    private static async Task<AuditIntegrityReport> VerifyAsync(ProductionStoreOptions options)
    {
        var report = await new SqliteAuditIntegrityQuery(options).VerifyAsync(
            new AuditVerificationRequest());
        return report;
    }

    private static async Task<string> DatabaseFingerprintAsync(string databasePath)
    {
        var values = new List<string>();
        foreach (var path in new[] { databasePath, databasePath + "-wal" })
        {
            if (!File.Exists(path))
            {
                values.Add(path + ":Absent");
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var algorithm = SHA256.Create();
            values.Add(path + ":" + Convert.ToHexString(await algorithm.ComputeHashAsync(stream)));
        }

        return string.Join("|", values);
    }
}

/// <summary>Small signed-store fixture shared by the schema compatibility theories.</summary>
internal sealed class SchemaFixture : IAsyncDisposable
{
    private const string UserName = "imaging-schema-admin";
    private const string Password = "V123 imaging schema 26! test secret";
    private readonly string _directory;
    private readonly AuditIntegrityPolicy _audit;
    private bool _storeDisposed;

    private SchemaFixture(string directory, AuditIntegrityPolicy audit,
        ProductionStoreOptions options, SqliteCommandStore store)
    {
        _directory = directory;
        _audit = audit;
        Options = options;
        Store = store;
    }

    internal ProductionStoreOptions Options { get; }
    internal SqliteCommandStore Store { get; private set; }

    internal static async Task<SchemaFixture> CreateAsync(bool imaging,
        bool includeRecovery, bool includeNetwork, bool forceAlarmPolicy = false)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema-13 signed storage requires Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            "V123-ImagingSchema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audit = new AuditIntegrityPolicy("V123ImagingSchemaStation", "v1",
            "SharpInspect.Test.V123.ImagingSchema." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            MaximumVerificationEntries = 10_000
        };
        var identity = new LocalIdentityOptions(audit.StationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v123-imaging-schema-blocklist", "1",
                    new[] { "known-compromised-imaging-password" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(Path.Combine(directory, "imaging-schema.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = identity,
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = includeRecovery ? new CameraRecoveryStoreOptions() : null,
            CameraNetwork = includeNetwork ? new CameraNetworkStoreOptions() : null,
            ImagingSetup = imaging ? new ImagingSetupStoreOptions() : null,
            AlarmPolicy = includeRecovery || forceAlarmPolicy ? RecoveryAlarmPolicy() : null,
            CommitTimeout = TimeSpan.FromSeconds(5),
            QueryTimeout = TimeSpan.FromSeconds(5),
            QueueCapacity = 8
        };

        SqliteCommandStore? store = null;
        try
        {
            store = new SqliteCommandStore(options);
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);

            var identityService = new LocalIdentityService(store, identity, new TestConsoleAuthority());
            var token = await identityService.ProvisionBootstrapTokenAsync();
            Assert.True(token.Succeeded, token.ReasonCode);
            await WaitForVerifiedAsync(store);
            var created = await identityService.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(audit.StationId, token.Token!.TakeForDisplay(),
                    UserName, "V123 imaging schema administrator", Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store);
            return new SchemaFixture(directory, audit, options, store);
        }
        catch
        {
            if (store is not null) await store.DisposeAsync();
            Cleanup(directory, audit);
            throw;
        }
    }

    internal async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    internal async Task StopStoreAsync()
    {
        if (_storeDisposed) return;
        _storeDisposed = true;
        await Store.DisposeAsync();
    }

    internal static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
            if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                throw new XunitException(fault.ReasonCode);
            await Task.Delay(25);
        }

        throw new XunitException("Imaging schema audit did not become Verified: " +
            store.Integrity?.ReasonCode);
    }

    internal static AlarmPolicy RecoveryAlarmPolicy() => new("V123-development", "1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraDisconnected", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, false,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraRecoveryFailed", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery)
        }.Concat(Enum.GetValues<CameraProtocolViolationKind>().Select(kind =>
            new AlarmPolicyRule(CameraAcquisitionAlarmCode(kind), "Runtime.CameraAcquisition",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution))).ToArray(),
        TimeSpan.FromSeconds(10), maximumActiveInstances: 256, maximumPlcEntries: 16);

    public async ValueTask DisposeAsync()
    {
        if (!_storeDisposed)
        {
            _storeDisposed = true;
            await Store.DisposeAsync();
        }

        Cleanup(_directory, _audit);
    }

    private static string CameraAcquisitionAlarmCode(CameraProtocolViolationKind kind) => kind switch
    {
        CameraProtocolViolationKind.EarlyFrame => "CameraEarlyFrame",
        CameraProtocolViolationKind.ExtraFrame => "CameraExtraFrame",
        CameraProtocolViolationKind.LateFrame => "CameraLateFrame",
        CameraProtocolViolationKind.CorrelationMismatch => "CameraCorrelationMismatch",
        CameraProtocolViolationKind.EarlyHardwarePulse => "CameraEarlyHardwarePulse",
        CameraProtocolViolationKind.DuplicateHardwarePulse => "CameraDuplicateHardwarePulse",
        CameraProtocolViolationKind.TriggerWhileBusy => "CameraTriggerWhileBusy",
        CameraProtocolViolationKind.InvalidFrame => "CameraInvalidFrame",
        CameraProtocolViolationKind.ObservationGap => "CameraObservationGap",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static void Cleanup(string directory, AuditIntegrityPolicy audit)
    {
        try
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "SharpInspect.Runtime.Tests"));
            if (fullDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TestConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true,
            "S-1-5-21-V123-ImagingSchema");
    }
}

#pragma warning restore CA1416
