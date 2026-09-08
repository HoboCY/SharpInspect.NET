#pragma warning disable CA1416

using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraRecoveryStorageTests
{
    [Fact]
    public async Task V119_S01_Schema11IsExplicitlyActivatedAndAllReadPathsVerifyIt()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();

        Assert.Equal(11L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_store_config;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraRecoveryStoreActivated';"));
        var commandSchema = await fixture.TextAsync(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='command_facts';");
        Assert.Contains("17", commandSchema, StringComparison.Ordinal);

        var audit = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, audit.State);

        var trace = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(
            new CommandTraceFilter(PageSize: 100));
        Assert.Empty(trace.Records);

        var alarms = await new SqliteAlarmHistoryQuery(fixture.Options).QueryAsync(
            new AlarmHistoryFilter(pageSize: 10));
        Assert.True(alarms.Available, alarms.ReasonCode);
    }

    [Fact]
    public async Task V119_S02_PendingAdmissionSurvivesRestartAndTerminalIsASeparateCommit()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var correlation = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var expectedCycle = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, "TopCamera");
        var command = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, "TopCamera", expectedCycle, "CycleExhausted");
        var admission = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(command,
            epoch, attempt, null, new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, admission.Outcome.Disposition);
        Assert.NotNull(admission.Admission);
        await fixture.WaitForVerifiedAsync();

        var reopened = await fixture.ReopenStoreAsync();
        Assert.Equal(11L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_terminal_events;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CommandKind=17 AND Phase=0 AND Disposition=0;"));

        // The durable admission carries the continuation authority. The
        // restarted writer does not require a new human login to record the
        // hardware result, and it never infers that the cycle already started.
        var terminal = await reopened.AppendCameraRecoveryTerminalAsync(admission.Admission!,
            newCycleId: null, started: false, "RecoveryStartFailed",
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_terminal_events;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CommandKind=17 AND Phase=2 AND Disposition IS NULL;"));
    }

    [Fact]
    public async Task V119_S03_RecoveryOptInRefusesSchema10WithoutWritingBytes()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync(includeRecovery: false);
        Assert.Equal(10L, await fixture.ScalarAsync("PRAGMA user_version;"));
        await fixture.StopStoreAsync();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);
        // ProductionStoreOptions is a class with init-only properties; create
        // the recovery configuration explicitly so the old bytes remain the
        // only source of truth for this negative migration check.
        var optedIn = new ProductionStoreOptions(fixture.Options.DatabasePath)
        {
            AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy,
            LocalIdentity = fixture.Options.LocalIdentity,
            AlarmPolicy = fixture.Options.AlarmPolicy,
            CameraSetup = fixture.Options.CameraSetup,
            CameraRecovery = new CameraRecoveryStoreOptions(),
            CommitTimeout = fixture.Options.CommitTimeout,
            QueryTimeout = fixture.Options.QueryTimeout,
            QueueCapacity = fixture.Options.QueueCapacity
        };
        await using var migration = new SqliteCommandStore(optedIn);
        var initialized = await migration.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CameraRecoveryGovernedMigrationRequired", initialized.ReasonCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));
    }

    [Fact]
    public void V119_S04_RecoveryOptInRequiresTheBoundedAlarmAndSetupBundle()
    {
        var options = new ProductionStoreOptions("recovery-required.sqlite")
        {
            AuditIntegrityPolicy = null,
            LocalIdentity = null,
            CameraRecovery = new CameraRecoveryStoreOptions()
        };
        var error = Assert.Throws<ArgumentException>(() => new SqliteCommandStore(options));
        Assert.StartsWith("CameraRecoveryRequiresCameraSetupIdentityAuditAndAlarm", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V119_S05_Schema11WithoutRecoveryOptInIsRejectedWithoutWritingBytes()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        await fixture.StopStoreAsync();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);

        var noRecoveryOptIn = new ProductionStoreOptions(fixture.Options.DatabasePath)
        {
            AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy,
            LocalIdentity = fixture.Options.LocalIdentity,
            AlarmPolicy = fixture.Options.AlarmPolicy,
            CameraSetup = fixture.Options.CameraSetup,
            CommitTimeout = fixture.Options.CommitTimeout,
            QueryTimeout = fixture.Options.QueryTimeout,
            QueueCapacity = fixture.Options.QueueCapacity
        };
        await using var rejected = new SqliteCommandStore(noRecoveryOptIn);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(initialized.Committed);
        Assert.Equal("CameraRecoveryConfigurationRequired", initialized.ReasonCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));
    }

    [Fact]
    public async Task V119_S06_TamperedTerminalPayloadIsRejectedByAuditVerifier()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var correlation = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, "TopCamera");
        var admission = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(
            fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant }, correlation,
                "TopCamera", Guid.NewGuid(), "CycleExhausted"), Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, admission.Outcome.Disposition);
        Assert.NotNull(admission.Admission);
        await fixture.WaitForVerifiedAsync();

        var terminal = await fixture.Authorization.CompleteCameraRecoveryCycleStartAsync(
            admission.Admission!, Guid.NewGuid(), started: true, "RecoveryStarted",
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        await fixture.StopStoreAsync();

        await TamperTerminalPayloadAsync(fixture.Options.DatabasePath);
        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(
            new AuditVerificationRequest());

        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        Assert.Equal("CameraRecoveryTerminalBindingMismatch", report.ReasonCode);
    }

    private static async Task TamperTerminalPayloadAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;");
        try
        {
            await ExecuteAsync(connection, "DROP TRIGGER camera_recovery_terminal_immutable_update;");
            await ExecuteAsync(connection, "DROP TRIGGER camera_recovery_terminal_immutable_delete;");
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE camera_recovery_terminal_events SET Payload=$payload WHERE Position=1;";
                update.Parameters.AddWithValue("$payload", "dGFtcGVyZWQ=");
                await update.ExecuteNonQueryAsync();
            }
            await ExecuteAsync(connection, @"
                CREATE TRIGGER camera_recovery_terminal_immutable_update BEFORE UPDATE ON camera_recovery_terminal_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableCameraRecoveryTerminal');
                END;");
            await ExecuteAsync(connection, @"
                CREATE TRIGGER camera_recovery_terminal_immutable_delete BEFORE DELETE ON camera_recovery_terminal_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableCameraRecoveryTerminal');
                END;");
            await ExecuteAsync(connection, "COMMIT;");
        }
        catch
        {
            try { await ExecuteAsync(connection, "ROLLBACK;"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

#pragma warning restore CA1416
