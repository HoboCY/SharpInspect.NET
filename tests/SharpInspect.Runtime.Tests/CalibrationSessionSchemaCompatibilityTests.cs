#pragma warning disable CA1416

using System.Globalization;
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
/// Schema-14 admission tests for the empty calibration-session evidence store.
/// The tests cover configuration and schema compatibility only; session events
/// and frame manifests are written by the later session layer.
/// </summary>
public sealed class CalibrationSessionSchemaCompatibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task V127_S01_Schema15RestartsWithOptionalLedgers(bool network, bool archiveAndDraft)
    {
        await using var fixture = await CalibrationSchemaFixture.CreateAsync(network, archiveAndDraft,
            calibration: true, governance: true);
        Assert.Equal(15L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationGovernanceStoreActivated';"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM calibration_governance_events;"));
        Assert.Equal(AuditIntegrityState.Verified, (await VerifyAsync(fixture.Options)).State);
        await fixture.RestartStoreAsync();
        Assert.Equal(AuditIntegrityState.Verified, (await VerifyAsync(fixture.Options)).State);
        _ = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(new CommandTraceFilter());
    }

    [Theory]
    [InlineData(false, "CalibrationGovernanceMigrationRequired")]
    [InlineData(true, "CalibrationGovernanceConfigurationRequired")]
    public async Task V127_S02_GovernanceOptInCannotMigrateOrDowngradeExistingStore(bool governance,
        string expectedReason)
    {
        await using var fixture = await CalibrationSchemaFixture.CreateAsync(false, false,
            calibration: true, governance: governance);
        await fixture.StopStoreAsync();
        _ = await fixture.ScalarAsync("PRAGMA user_version;");
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var mismatched = CalibrationSchemaFixture.OptionsFor(fixture.Options, calibration: true,
            governance: governance ? null : new CalibrationGovernanceStoreOptions());
        await using var rejected = new SqliteCommandStore(mismatched);
        var initialization = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialization.Committed);
        Assert.Equal(expectedReason, initialization.ReasonCode);
        var audit = await VerifyAsync(mismatched);
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Equal(expectedReason, audit.ReasonCode);
        var trace = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(mismatched).QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal(expectedReason, trace.Message);
        Assert.Equal(before, await DatabaseFingerprintAsync(fixture.Options.DatabasePath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task V124_S01_Schema14InitializesAndRestartsForOptionalLedgerCombinations(
        bool includeNetwork, bool includeArchiveAndDraft)
    {
        await using var fixture = await CalibrationSchemaFixture.CreateAsync(
            includeNetwork, includeArchiveAndDraft, calibration: true);

        Assert.Equal(14L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM calibration_store_config;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='calibration_sessions';"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='calibration_session_events';"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='calibration_frame_manifests';"));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM calibration_sessions;"));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM calibration_session_events;"));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM calibration_frame_manifests;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_setup_store_config;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_store_config;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM imaging_setup_store_config;"));
        Assert.Equal(includeNetwork ? 1L : 0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='camera_network_store_config';"));
        Assert.Equal(includeArchiveAndDraft ? 1L : 0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='algorithm_result_archive_config';"));
        Assert.Equal(includeArchiveAndDraft ? 1L : 0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recipe_draft_store_config';"));

        var eventDefinition = await fixture.TextAsync(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='calibration_session_events';");
        Assert.NotNull(eventDefinition);
        Assert.Contains("UNIQUE(SessionId,Sequence)", eventDefinition!, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUE(OperationId)", eventDefinition!, StringComparison.Ordinal);
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CalibrationStoreActivated';"));

        var firstAudit = await VerifyAsync(fixture.Options);
        Assert.Equal(AuditIntegrityState.Verified, firstAudit.State);
        var firstTrace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(PageSize: 20));
        Assert.True(firstTrace.ThroughPosition >= 0);

        await fixture.RestartStoreAsync();
        var restartedAudit = await VerifyAsync(fixture.Options);
        Assert.Equal(AuditIntegrityState.Verified, restartedAudit.State);
        var restartedTrace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(PageSize: 20));
        Assert.True(restartedTrace.ThroughPosition >= 0);
    }

    [Fact]
    public async Task V124_S02_Schema14WithoutCalibrationConfigurationIsRejectedWithoutMutation()
    {
        await using var fixture = await CalibrationSchemaFixture.CreateAsync(
            includeNetwork: false, includeArchiveAndDraft: false, calibration: true);
        await fixture.StopStoreAsync();

        // Finish all read-only observations before taking the fingerprint. A
        // Microsoft.Data.Sqlite read-only open may materialize an empty WAL.
        var beforeVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var missing = CalibrationSchemaFixture.OptionsFor(fixture.Options, calibration: false);

        await using var rejected = new SqliteCommandStore(missing);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CalibrationConfigurationRequired", initialized.ReasonCode);

        var audit = await new SqliteAuditIntegrityQuery(missing).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Equal("CalibrationConfigurationRequired", audit.ReasonCode);
        var trace = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(missing).QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal("CalibrationConfigurationRequired", trace.Message);

        var afterVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var after = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        Assert.Equal(beforeVersion, afterVersion);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task V124_S03_ExistingSchema13CannotAutoMigrateToCalibrationSessions()
    {
        await using var fixture = await CalibrationSchemaFixture.CreateAsync(
            includeNetwork: false, includeArchiveAndDraft: false, calibration: false);
        Assert.Equal(13L, await fixture.ScalarAsync("PRAGMA user_version;"));
        await fixture.StopStoreAsync();

        // Keep the database/WAL state stable across every read-only rejection
        // observation. The fingerprint includes both files.
        var beforeVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var governed = CalibrationSchemaFixture.OptionsFor(fixture.Options, calibration: true);

        await using var rejected = new SqliteCommandStore(governed);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CalibrationGovernedMigrationRequired", initialized.ReasonCode);

        var audit = await new SqliteAuditIntegrityQuery(governed).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Equal("CalibrationGovernedMigrationRequired", audit.ReasonCode);
        var trace = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(governed).QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal("CalibrationGovernedMigrationRequired", trace.Message);

        var afterVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var after = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        Assert.Equal(beforeVersion, afterVersion);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task V124_S04_ChangedCalibrationBindingIsRejectedByIndependentReaders()
    {
        await using var fixture = await CalibrationSchemaFixture.CreateAsync(
            includeNetwork: false, includeArchiveAndDraft: false, calibration: true);
        await fixture.StopStoreAsync();

        var beforeVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var before = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        var changed = CalibrationSchemaFixture.OptionsFor(fixture.Options,
            calibration: true, calibrationEvidenceRoot: Path.Combine(
                Path.GetDirectoryName(fixture.Options.DatabasePath)!, "different-evidence"));

        var audit = await new SqliteAuditIntegrityQuery(changed).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Equal("CalibrationConfigurationMismatch", audit.ReasonCode);
        var trace = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(changed).QueryAsync(new CommandTraceFilter()).AsTask());
        Assert.Equal("CalibrationConfigurationMismatch", trace.Message);

        var afterVersion = await fixture.ScalarAsync("PRAGMA user_version;");
        var after = await DatabaseFingerprintAsync(fixture.Options.DatabasePath);
        Assert.Equal(beforeVersion, afterVersion);
        Assert.Equal(before, after);
    }

    private static async Task<AuditIntegrityReport> VerifyAsync(ProductionStoreOptions options) =>
        await new SqliteAuditIntegrityQuery(options).VerifyAsync(new AuditVerificationRequest());

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

/// <summary>Minimal Windows protected-key fixture for schema-14 compatibility tests.</summary>
internal sealed class CalibrationSchemaFixture : IAsyncDisposable
{
    private readonly string _directory;
    private readonly AuditIntegrityPolicy _audit;
    private bool _storeDisposed;

    private CalibrationSchemaFixture(string directory, AuditIntegrityPolicy audit,
        ProductionStoreOptions options, SqliteCommandStore store)
    {
        _directory = directory;
        _audit = audit;
        Options = options;
        Store = store;
    }

    internal ProductionStoreOptions Options { get; }
    internal SqliteCommandStore Store { get; private set; }

    internal static async Task<CalibrationSchemaFixture> CreateAsync(
        bool includeNetwork, bool includeArchiveAndDraft, bool calibration, bool governance = false)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Schema-14 signed storage requires Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            "V124-CalibrationSchema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audit = new AuditIntegrityPolicy("V124CalibrationSchemaStation", "v1",
            "SharpInspect.Test.V124.CalibrationSchema." + Guid.NewGuid().ToString("N"))
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
                Blocklist = PasswordBlocklist.Create("v124-calibration-schema-blocklist", "1",
                    new[] { "known-compromised-calibration-password" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            AuthorizationPolicy.Development);
        var execution = new AlgorithmExecutionPolicy("V124.Calibration.SchemaExecution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var options = new ProductionStoreOptions(Path.Combine(directory, "calibration-schema.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = identity,
            AlarmPolicy = RecoveryAlarmPolicy(),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            CameraNetwork = includeNetwork ? new CameraNetworkStoreOptions() : null,
            ImagingSetup = new ImagingSetupStoreOptions(),
            AlgorithmResultArchive = includeArchiveAndDraft ? new AlgorithmResultArchiveOptions() : null,
            RecipeDrafts = includeArchiveAndDraft ? new RecipeDraftStoreOptions(execution) : null,
            CalibrationGovernance = governance ? new CalibrationGovernanceStoreOptions() : null,
            CalibrationSessions = calibration ? new CalibrationSessionStoreOptions
            {
                EvidenceRoot = Path.Combine(directory, "calibration-evidence")
            } : null,
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
            return new CalibrationSchemaFixture(directory, audit, options, store);
        }
        catch
        {
            if (store is not null) await store.DisposeAsync();
            Cleanup(directory, audit);
            throw;
        }
    }

    internal static ProductionStoreOptions OptionsFor(ProductionStoreOptions source,
        bool calibration, string? calibrationEvidenceRoot = null,
        CalibrationGovernanceStoreOptions? governance = null) => new(source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = source.AlarmPolicy,
            AlgorithmResultArchive = source.AlgorithmResultArchive,
            RecipeDrafts = source.RecipeDrafts,
            CameraSetup = source.CameraSetup,
            CameraRecovery = source.CameraRecovery,
            CameraNetwork = source.CameraNetwork,
            ImagingSetup = source.ImagingSetup,
            CalibrationGovernance = governance,
            CalibrationSessions = calibration
                ? new CalibrationSessionStoreOptions
                {
                    EvidenceRoot = calibrationEvidenceRoot ??
                        source.CalibrationSessions?.EvidenceRoot ??
                        Path.Combine(Path.GetDirectoryName(source.DatabasePath)!, "calibration-evidence"),
                    MaximumSessions = source.CalibrationSessions?.MaximumSessions ?? 100,
                    MaximumEvents = source.CalibrationSessions?.MaximumEvents ?? 10_000,
                    MaximumEventPayloadBytes = source.CalibrationSessions?.MaximumEventPayloadBytes ?? 256 * 1024,
                    MaximumFramesPerSession = source.CalibrationSessions?.MaximumFramesPerSession ?? 64,
                    MaximumFrameBytes = source.CalibrationSessions?.MaximumFrameBytes ?? 16L * 1024 * 1024,
                    MaximumTotalFrameBytes = source.CalibrationSessions?.MaximumTotalFrameBytes ??
                        256L * 1024 * 1024
                } : null,
            ExternalAuditAnchor = source.ExternalAuditAnchor,
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity
        };

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
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    internal async Task<string?> TextAsync(string sql)
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
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    internal async Task StopStoreAsync()
    {
        if (_storeDisposed) return;
        _storeDisposed = true;
        await Store.DisposeAsync();
    }

    internal async Task RestartStoreAsync()
    {
        await StopStoreAsync();
        Store = new SqliteCommandStore(Options);
        var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        _storeDisposed = false;
        await WaitForVerifiedAsync(Store);
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
            if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                throw new XunitException(fault.ReasonCode);
            await Task.Delay(25);
        }

        throw new XunitException("Calibration schema audit did not become Verified: " +
            store.Integrity?.ReasonCode);
    }

    internal static AlarmPolicy RecoveryAlarmPolicy() => new("V124-calibration-recovery", "1",
        new[]
        {
            new AlarmPolicyRule("CameraDisconnected", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, false,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution)
        }, TimeSpan.FromSeconds(10), maximumActiveInstances: 256, maximumPlcEntries: 16);

    public async ValueTask DisposeAsync()
    {
        if (!_storeDisposed)
        {
            _storeDisposed = true;
            await Store.DisposeAsync();
        }

        Cleanup(_directory, _audit);
    }

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
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (fullDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullDirectory))
                Directory.Delete(fullDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

#pragma warning restore CA1416
