#pragma warning disable CA1416

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraNetworkStorageTests
{
    [Fact]
    public async Task V120_S01_Schema12NetworkLedgerIsExplicitlyActivatedAndReadOnlyVerified()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();

        Assert.Equal(12L, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_store_config;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraNetworkStoreActivated';"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_network_events;"));

        var latest = await fixture.Persistence.ReadLatestAsync(fixture.Target);
        Assert.Null(latest);
        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Verified, report.State);
    }

    [Fact]
    public async Task V120_S02_PendingAdmissionSurvivesRestartWithoutReplay()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var admitted = await fixture.AdmitAsync();

        Assert.True(await fixture.Persistence.HasUnresolvedAsync());
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE Phase=0;"));
        await fixture.StopStoreAsync();

        await using var reopened = new SqliteCommandStore(fixture.Options);
        var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await CameraNetworkTestFixture.WaitForVerifiedAsync(reopened);
        var persistence = new SqliteCameraNetworkPersistence(reopened);

        var pending = await persistence.ReadLatestAsync(fixture.Target);
        Assert.NotNull(pending);
        Assert.Equal(admitted.Request.OperationId, pending!.OperationId);
        Assert.Equal(CameraNetworkMaintenanceState.Pending, pending.State);
        Assert.Equal(admitted.Request.Requested, pending.Requested);
        Assert.Equal(admitted.Previous, pending.Previous);
        Assert.Null(pending.Observed);
        Assert.False(pending.IdentityVerified);
        Assert.True(await persistence.HasUnresolvedAsync());
    }

    [Fact]
    public async Task V120_S03_TerminalBindsPersistedAdmissionAfterLogout()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var admitted = await fixture.AdmitAsync();
        var logout = await fixture.Sessions.LogoutAsync(admitted.Invocation.SessionId!.Value);
        Assert.True(logout.Succeeded, logout.ReasonCode);

        var observed = admitted.Request.Requested;
        var terminalSnapshot = new CameraNetworkSnapshot(admitted.Request.OperationId,
            admitted.Request.Target, CameraNetworkMaintenanceState.Succeeded,
            admitted.Previous, admitted.Request.Requested, observed, true,
            "CameraNetworkChanged", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));

        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var latest = await fixture.Persistence.ReadLatestAsync(fixture.Target);
        Assert.Equal(terminalSnapshot, latest);
        Assert.False(await fixture.Persistence.HasUnresolvedAsync());
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE Phase=1 AND State=1;"));
    }

    [Fact]
    public async Task V120_S04_DuplicateOperationIdIsRejectedWithoutAnotherLedgerRow()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var admitted = await fixture.AdmitAsync();
        var duplicate = await fixture.Persistence.AppendAdmissionAsync(admitted.Request,
            fixture.StationNetwork, fixture.Previous, admitted.Authorization,
            admitted.Admission.RuntimeEpoch, new StoreDeadline(fixture.Options.CommitTimeout));

        Assert.False(duplicate.Result.Committed);
        Assert.Equal("CameraNetworkOperationConflict", duplicate.Result.ReasonCode);
        Assert.Null(duplicate.Admission);
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE OperationId=$operation;",
            ("$operation", admitted.Request.OperationId.ToString("D"))));
    }

    [Fact]
    public async Task V120_S05_TamperedNetworkIndexFailsReadOnlyIntegrityVerification()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        _ = await fixture.AdmitAsync();
        await fixture.StopStoreAsync();
        await TamperNetworkTargetAsync(fixture.Options.DatabasePath);

        var report = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(
            new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        Assert.Equal("CameraNetworkBindingMismatch", report.ReasonCode);
    }

    [Fact]
    public async Task V120_S06_NetworkOptInRefusesSchema11WithoutChangingCanonicalBytes()
    {
        await using var legacy = await CameraRecoveryTestFixture.CreateAsync();
        await legacy.StopStoreAsync();
        var before = await File.ReadAllBytesAsync(legacy.Options.DatabasePath);
        var optedIn = new ProductionStoreOptions(legacy.Options.DatabasePath)
        {
            AuditIntegrityPolicy = legacy.Options.AuditIntegrityPolicy,
            LocalIdentity = legacy.Options.LocalIdentity,
            AlarmPolicy = legacy.Options.AlarmPolicy,
            CameraSetup = legacy.Options.CameraSetup,
            CameraRecovery = legacy.Options.CameraRecovery,
            CameraNetwork = new CameraNetworkStoreOptions(),
            CommitTimeout = legacy.Options.CommitTimeout,
            QueryTimeout = legacy.Options.QueryTimeout,
            QueueCapacity = legacy.Options.QueueCapacity
        };

        await using var rejected = new SqliteCommandStore(optedIn);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CameraNetworkGovernedMigrationRequired", initialized.ReasonCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(legacy.Options.DatabasePath));
    }

    [Fact]
    public async Task V120_S07_TerminalUsesPersistedAdmissionAfterActorAuthorizationRevisionChanges()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var admitted = await fixture.AdmitAsync();
        var principalId = Guid.Parse(admitted.Invocation.PrincipalId!);
        var actorInvocation = admitted.Invocation with { StepUpGrantId = null };
        var permissions = fixture.Options.LocalIdentity!.AuthorizationPolicy
            .GetPermissions(HumanRoleBundle.Administrator)
            .Where(permission => permission != Permission.ManageCameraBindings)
            .ToArray();
        var change = new SetHumanPermissionsCommand(Guid.NewGuid(), actorInvocation, principalId,
            permissions, IdentityManagementReason.AccessChange);
        var stepUp = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(
            Guid.NewGuid(), actorInvocation,
            new StepUpBinding(Permission.ManagePermissions, change.CorrelationId,
                principalId.ToString("D"), AuditedCommandKind.SetHumanPermissions),
            fixture.PasswordForTests));
        Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
        Assert.NotNull(stepUp.GrantId);
        await fixture.WaitForVerifiedAsync();

        await using (var runtime = new StationRuntime(fixture.Store,
            TimeSpan.FromMilliseconds(20), fixture.Sessions, fixture.Authorization))
        {
            var changed = await runtime.SubmitAsync(change with
            {
                Invocation = actorInvocation with { StepUpGrantId = stepUp.GrantId }
            });
            Assert.Equal(CommandDisposition.Accepted, changed.Disposition);
            Assert.Equal(AuditPersistence.Persisted, changed.Audit);
        }

        await fixture.WaitForVerifiedAsync();
        var current = await fixture.Authorization.GetCurrentAuthorizationAsync(
            admitted.Invocation.SessionId);
        Assert.True(current.Available, current.ReasonCode);
        Assert.NotNull(current.Account);
        Assert.True(current.Account!.AuthorizationRevision > admitted.Authorization.AuthorizationRevision);
        Assert.DoesNotContain(Permission.ManageCameraBindings, current.Account.Permissions);

        var terminalSnapshot = new CameraNetworkSnapshot(admitted.Request.OperationId,
            admitted.Request.Target, CameraNetworkMaintenanceState.Succeeded,
            admitted.Previous, admitted.Request.Requested, admitted.Request.Requested, true,
            "CameraNetworkChanged", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));

        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        Assert.Equal(terminalSnapshot, await fixture.Persistence.ReadLatestAsync(fixture.Target));
    }

    private static async Task TamperNetworkTargetAsync(string databasePath)
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
            await ExecuteAsync(connection, "DROP TRIGGER camera_network_event_immutable_update;");
            await ExecuteAsync(connection, "DROP TRIGGER camera_network_event_immutable_delete;");
            await using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE camera_network_events SET TargetStableDeviceIdentity=$target WHERE Position=1;";
                update.Parameters.AddWithValue("$target", "tampered-device");
                await update.ExecuteNonQueryAsync();
            }
            await ExecuteAsync(connection, @"
                CREATE TRIGGER camera_network_event_immutable_update BEFORE UPDATE ON camera_network_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
                END;");
            await ExecuteAsync(connection, @"
                CREATE TRIGGER camera_network_event_immutable_delete BEFORE DELETE ON camera_network_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
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

/// <summary>Real Windows SQLite/identity fixture for schema-12 network persistence.</summary>
internal sealed class CameraNetworkTestFixture : IAsyncDisposable
{
    private const string UserName = "camera-network-admin";
    private const string Password = "V120 camera network 26! test secret";
    private readonly string _directory;
    private readonly AuditIntegrityPolicy _auditPolicy;
    private readonly ServiceProvider _provider;
    private bool _storeDisposed;

    private CameraNetworkTestFixture(string directory, AuditIntegrityPolicy auditPolicy,
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
        Persistence = new SqliteCameraNetworkPersistence(store);
    }

    internal ProductionStoreOptions Options { get; }
    internal string PasswordForTests => Password;
    internal SqliteCommandStore Store { get; private set; }
    internal InteractiveSessionService Sessions { get; }
    internal LocalAuthorizationService Authorization { get; }
    internal ICameraNetworkPersistence Persistence { get; private set; }
    internal CameraBindingTarget Target { get; } = new(
        new CameraProviderIdentity("V120.Provider", "1", "V120.Adapter", "1"), "device-001");
    internal CameraBindingTarget OtherTarget { get; } = new(
        new CameraProviderIdentity("V120.Provider", "1", "V120.Adapter", "1"), "device-002");
    internal CameraStationNetwork StationNetwork { get; } = new(
        "eth0", new CameraIpv4Configuration("192.168.20.2", 24, "192.168.20.1"));
    internal CameraIpv4Configuration Previous { get; } =
        new("192.168.20.10", 24, "192.168.20.1");
    internal CameraIpv4Configuration Requested { get; } =
        new("192.168.20.20", 24, "192.168.20.1");

    internal string StationId => _auditPolicy.StationId;

    internal static async Task<CameraNetworkTestFixture> CreateAsync(
        CameraNetworkStoreOptions? networkOptions = null,
        AlgorithmResultArchiveOptions? archiveOptions = null,
        RecipeDraftStoreOptions? recipeOptions = null,
        int? maximumVerificationEntries = null,
        AuthorizationPolicy? authorizationPolicy = null)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Camera network signed storage requires Windows machine protection.");

        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            "V120-CameraNetwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var station = "V120CameraNetworkStation";
        var compactBudget = maximumVerificationEntries is not null;
        var audit = new AuditIntegrityPolicy(station, "v1",
            "SharpInspect.Test.V120.CameraNetwork." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = compactBudget ? 1 : 2,
            MaximumVerificationEntries = maximumVerificationEntries ?? 10_000,
            BackgroundVerificationEntries = compactBudget ? 1 : 200,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identity = new LocalIdentityOptions(station,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v120-camera-network-blocklist", "v1",
                    new[] { "known-compromised-network-password" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            authorizationPolicy ?? AuthorizationPolicy.Development);
        var options = new ProductionStoreOptions(Path.Combine(directory, "camera-network.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = identity,
            CameraSetup = new CameraSetupStoreOptions(),
            CameraNetwork = networkOptions ?? new CameraNetworkStoreOptions(),
            AlgorithmResultArchive = archiveOptions,
            RecipeDrafts = recipeOptions,
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

            var bootstrap = new LocalIdentityService(store, identity, new TestConsoleAuthority());
            var tokenResult = await bootstrap.ProvisionBootstrapTokenAsync();
            Assert.True(tokenResult.Succeeded, tokenResult.ReasonCode);
            var token = tokenResult.Token!.TakeForDisplay();
            var created = await bootstrap.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(station, token, UserName,
                    "V120 Camera Network Administrator", Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();

            var fixture = new CameraNetworkTestFixture(directory, audit, options, provider, store,
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

    internal async Task<CameraNetworkAdmissionAttempt> PrepareAdmissionAsync(
        CameraBindingTarget? target = null)
    {
        target ??= Target;
        var signedIn = await SignInAsync();
        var operationId = Guid.NewGuid();
        var grant = await IssueGrantAsync(signedIn.Invocation, operationId, target);
        var authorization = await Authorization.AuthorizeCameraSetupAsync(
            signedIn.Invocation with { StepUpGrantId = grant }, false, operationId,
            target.ContentHash, AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.True(authorization.Authorized, authorization.ReasonCode);
        Assert.NotNull(authorization.Reservation);
        var request = Request(operationId, target, signedIn.Invocation with { StepUpGrantId = grant });
        return new CameraNetworkAdmissionAttempt(request, signedIn.Invocation with { StepUpGrantId = grant },
            authorization, grant, Guid.NewGuid());
    }

    internal async Task<CameraNetworkAdmissionHandle> AdmitAsync(
        CameraBindingTarget? target = null)
    {
        var attempt = await PrepareAdmissionAsync(target);
        var result = await Persistence.AppendAdmissionAsync(attempt.Request, StationNetwork,
            Previous, attempt.Authorization, attempt.RuntimeEpoch,
            new StoreDeadline(Options.CommitTimeout));
        Assert.True(result.Result.Committed, result.Result.ReasonCode);
        Assert.NotNull(result.Admission);
        attempt.Authorization.Reservation!.Commit();
        await WaitForVerifiedAsync();
        return new CameraNetworkAdmissionHandle(attempt.Request, result.Admission!, attempt.Authorization,
            attempt.Invocation, attempt.GrantId);
    }

    internal CameraNetworkChangeRequest Request(Guid operationId, CameraBindingTarget target,
        CommandInvocation invocation) => new(operationId, invocation, target, Requested,
            "V120NetworkMaintenance");

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

    internal async Task<Guid> IssueGrantAsync(CommandInvocation invocation, Guid operationId,
        CameraBindingTarget target)
    {
        var result = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.ManageCameraBindings, operationId, target.ContentHash,
                AuditedCommandKind.ChangeCameraNetworkConfiguration), Password));
        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.True(result.GrantId.HasValue);
        await WaitForVerifiedAsync();
        return result.GrantId!.Value;
    }

    internal async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    internal async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(Store);

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
        throw new XunitException("Camera network audit did not become Verified: " +
            store.Integrity?.ReasonCode);
    }

    internal async Task StopStoreAsync()
    {
        if (_storeDisposed) return;
        _storeDisposed = true;
        await Store.DisposeAsync();
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
            var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "SharpInspect.Runtime.Tests"));
            if (fullDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TestConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V120-CameraNetwork");
    }
}

internal sealed record CameraNetworkAdmissionHandle(CameraNetworkChangeRequest Request,
    CameraNetworkAdmission Admission, CameraSetupAuthorization Authorization,
    CommandInvocation Invocation, Guid GrantId)
{
    internal CameraIpv4Configuration Previous => Admission.ActualPrevious;
}

internal sealed record CameraNetworkAdmissionAttempt(CameraNetworkChangeRequest Request,
    CommandInvocation Invocation, CameraSetupAuthorization Authorization, Guid GrantId,
    Guid RuntimeEpoch);

#pragma warning restore CA1416
