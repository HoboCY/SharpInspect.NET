#pragma warning disable CA1416

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Acceptance coverage for the schema-11 recovery authorization seam. The
/// tests use the real identity provider, interactive session, Step-Up store
/// and SQLite writer; a successful admission deliberately has no terminal
/// result until the caller submits one.
/// </summary>
public sealed class CameraRecoveryAuthorizationTests
{
    private const string LogicalRole = "TopCamera";

    [Fact]
    public async Task V119_A01_ExactStepUpAdmissionIsDurableAndTerminalIsExplicit()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var correlation = Guid.NewGuid();
        var expectedCycle = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, LogicalRole);
        var command = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, LogicalRole, expectedCycle, "CycleExhausted");

        var admission = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(command,
            epoch, attempt, null, new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);

        Assert.Equal(CommandDisposition.Accepted, admission.Outcome.Disposition);
        Assert.Equal("CameraRecoveryCycleStartAuthorized", admission.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, admission.Outcome.Audit);
        Assert.Equal(attempt, admission.Outcome.AttemptId);
        Assert.NotNull(admission.Admission);
        Assert.Equal(AuditedCommandKind.StartCameraRecoveryCycle, admission.Admission!.CommandKind);
        Assert.Equal(CommandAuditPhase.Outcome, admission.Admission.Phase);
        Assert.Equal(CommandDisposition.Accepted, admission.Admission.Disposition);
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_terminal_events;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CommandKind=17 AND Phase=0 AND Disposition=0;"));

        await fixture.WaitForVerifiedAsync();
        var terminal = await fixture.Authorization.CompleteCameraRecoveryCycleStartAsync(
            admission.Admission, Guid.NewGuid(), started: true, "RecoveryStarted",
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_terminal_events;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CommandKind=17 AND Phase=1 AND Disposition IS NULL;"));

        // A terminal continuation is idempotent only by refusing a second
        // immutable row; a second call never creates another completion fact.
        var duplicate = await fixture.Authorization.CompleteCameraRecoveryCycleStartAsync(
            admission.Admission, Guid.NewGuid(), started: true, "RecoveryStarted",
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.False(duplicate.Committed);
        Assert.Equal("CameraRecoveryTerminalDuplicate", duplicate.ReasonCode);
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_terminal_events;"));
    }

    [Fact]
    public async Task V119_A02_BadBindingAndConsumedGrantCannotAuthorizeAnotherStart()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var correlation = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, LogicalRole);

        var wrongRole = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, "SideCamera", Guid.NewGuid(), "CycleExhausted");
        var rejected = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(wrongRole,
            Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("StepUpInvalid", rejected.Outcome.ReasonCode);
        Assert.Null(rejected.Admission);

        // The failed binding check does not consume the grant. The exact
        // command can still be admitted once, and only once.
        var exact = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, LogicalRole, Guid.NewGuid(), "CycleExhausted");
        var admitted = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(exact,
            Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, admitted.Outcome.Disposition);
        Assert.NotNull(admitted.Admission);

        var reused = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            Guid.NewGuid(), LogicalRole, Guid.NewGuid(), "CycleExhausted");
        var reuseResult = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(reused,
            Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Rejected, reuseResult.Outcome.Disposition);
        Assert.Equal("StepUpInvalid", reuseResult.Outcome.ReasonCode);
        Assert.Null(reuseResult.Admission);
    }

    [Fact]
    public async Task V119_A03_StaleSessionCannotUseAPreviouslyIssuedGrant()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var correlation = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, LogicalRole);

        var loggedOut = await fixture.Sessions.LogoutAsync(signedIn.Invocation.SessionId!.Value);
        Assert.True(loggedOut.Succeeded, loggedOut.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var stale = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, LogicalRole, Guid.NewGuid(), "CycleExhausted");
        var result = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(stale,
            Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal("SessionMismatch", result.Outcome.ReasonCode);
        Assert.Null(result.Admission);
    }

    [Fact]
    public async Task V119_A04_WriterFailureLeavesStepUpGrantReusable()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var correlation = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, LogicalRole);
        var command = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, LogicalRole, Guid.NewGuid(), "CycleExhausted");

        // Hold the SQLite writer lock so the real command writer fails before
        // it can commit an outcome. The guard must not consume the one-time
        // proof when the writer cannot accept the transaction.
        await using var lockConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await lockConnection.OpenAsync();
        await using (var begin = lockConnection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        var failed = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(command,
            Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(TimeSpan.FromMilliseconds(500)), CancellationToken.None);
        Assert.Equal(CommandDisposition.Rejected, failed.Outcome.Disposition);
        Assert.Equal("TraceAuditUnavailable", failed.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, failed.Outcome.Audit);
        Assert.Null(failed.Admission);

        await using (var rollback = lockConnection.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        // No outcome was committed, so the exact binding remains usable. A
        // fresh attempt/epoch keeps the retry context distinct while reusing
        // the same correlation and Step-Up grant binding.
        var retry = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(command,
            Guid.NewGuid(), Guid.NewGuid(), null,
            new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, retry.Outcome.Disposition);
        Assert.NotNull(retry.Admission);
        await fixture.WaitForVerifiedAsync();
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CommandKind=17 AND Phase=0 AND Disposition=0;"));
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CommandKind=17 AND Phase=0 AND Disposition=1;"));
    }
}

/// <summary>Shared real SQLite/identity fixture for recovery authorization and storage tests.</summary>
internal sealed class CameraRecoveryTestFixture : IAsyncDisposable
{
    private const string UserName = "camera-recovery-admin";
    private const string Password = "V119 camera recovery 26! test secret";
    private readonly string _directory;
    private readonly AuditIntegrityPolicy _auditPolicy;
    private readonly ServiceProvider _provider;
    private bool _storeDisposed;

    private CameraRecoveryTestFixture(string directory, AuditIntegrityPolicy auditPolicy,
        ProductionStoreOptions options, ServiceProvider provider, SqliteCommandStore store,
        InteractiveSessionService sessions, LocalAuthorizationService authorization)
    {
        _directory = directory;
        _auditPolicy = auditPolicy;
        Options = options;
        _provider = provider;
        Store = store;
        Sessions = sessions;
        Authorization = authorization;
    }

    internal ProductionStoreOptions Options { get; }
    internal SqliteCommandStore Store { get; private set; }
    internal InteractiveSessionService Sessions { get; }
    internal LocalAuthorizationService Authorization { get; }
    internal string StationId => _auditPolicy.StationId;

    internal static async Task<CameraRecoveryTestFixture> CreateAsync(bool includeRecovery = true)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Camera recovery signed storage requires Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            "V119-CameraRecovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var station = "V119CameraRecoveryStation";
        var audit = new AuditIntegrityPolicy(station, "v1",
            "SharpInspect.Test.V119.CameraRecovery." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identity = new LocalIdentityOptions(station,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v119-camera-recovery-blocklist", "v1",
                    new[] { "known-compromised" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(Path.Combine(directory, "camera-recovery.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = identity,
            AlarmPolicy = new AlarmPolicy("V119CameraRecoveryAlarmPolicy", "1", new[]
            {
                new AlarmPolicyRule("CAMERA_RECOVERY_TEST", "Camera", AlarmSeverity.Error,
                    ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null)
            }, TimeSpan.FromSeconds(30)),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = includeRecovery ? new CameraRecoveryStoreOptions() : null,
            CommitTimeout = TimeSpan.FromSeconds(5),
            QueryTimeout = TimeSpan.FromSeconds(5),
            QueueCapacity = 8
        };

        ServiceProvider? provider = null;
        SqliteCommandStore? store = null;
        try
        {
            var services = new ServiceCollection();
            services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(20));
            provider = services.BuildServiceProvider();
            store = provider.GetRequiredService<SqliteCommandStore>();
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);

            // Bootstrap uses an explicit test authority because CI is not
            // required to run the test process as a Windows administrator.
            var bootstrap = new LocalIdentityService(store, identity, new TestConsoleAuthority());
            var tokenResult = await bootstrap.ProvisionBootstrapTokenAsync();
            Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
            var token = tokenResult.Token!.TakeForDisplay();
            var created = await bootstrap.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(station, token, UserName,
                    "V119 Camera Recovery Administrator", Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();

            var fixture = new CameraRecoveryTestFixture(directory, audit, options, provider, store,
                (InteractiveSessionService)provider.GetRequiredService<IInteractiveSessionService>(),
                provider.GetRequiredService<LocalAuthorizationService>());
            await fixture.WaitForVerifiedAsync();
            return fixture;
        }
        catch
        {
            if (provider is not null) await provider.DisposeAsync();
            else if (store is not null) await store.DisposeAsync();
            Cleanup(directory, audit);
            throw;
        }
    }

    internal async Task<(CommandInvocation Invocation, Guid PrincipalId)> SignInAsync()
    {
        var result = await Sessions.SignInAsync(new PasswordSignInRequest(UserName, Password));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.NotNull(result.Identity);
        await WaitForVerifiedAsync();
        var principal = result.Identity!.PrincipalId;
        return (new CommandInvocation(CommandSource.PhysicalConsole,
            principal.ToString("D"), result.Session.SessionId), principal);
    }

    internal async Task<Guid> IssueRecoveryGrantAsync(CommandInvocation invocation,
        Guid commandCorrelation, string logicalRole)
    {
        var result = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.ManageCameraBindings, commandCorrelation, logicalRole,
                AuditedCommandKind.StartCameraRecoveryCycle), Password));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(result.GrantId.HasValue);
        await WaitForVerifiedAsync();
        return result.GrantId!.Value;
    }

    internal StartCameraRecoveryCycleCommand RecoveryCommand(CommandInvocation invocation,
        Guid correlation, string logicalRole, Guid expectedCycleId, string reasonCode) =>
        new(correlation, invocation, logicalRole, expectedCycleId, reasonCode);

    internal async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    internal async Task WaitForVerifiedAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (Store.Integrity is { State: AuditIntegrityState.Verified }) return;
            if (Store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                throw new XunitException(fault.ReasonCode);
            await Task.Delay(25);
        }
        throw new XunitException("Camera recovery audit did not become Verified: " + Store.Integrity?.ReasonCode);
    }

    internal async Task StopStoreAsync()
    {
        if (_storeDisposed) return;
        _storeDisposed = true;
        await Store.DisposeAsync();
    }

    internal async Task<SqliteCommandStore> ReopenStoreAsync()
    {
        await StopStoreAsync();
        var reopened = new SqliteCommandStore(Options);
        var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        Store = reopened;
        _storeDisposed = false;
        await WaitForVerifiedAsync();
        return reopened;
    }

    internal async Task<string?> TextAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_storeDisposed)
        {
            _storeDisposed = true;
            await Store.DisposeAsync();
        }
        await _provider.DisposeAsync();
        Cleanup(_directory, _auditPolicy);
    }

    private static void Cleanup(string directory, AuditIntegrityPolicy policy)
    {
        try
        {
            var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests"));
            if (fullDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TestConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V119-CameraRecovery");
    }
}

#pragma warning restore CA1416
