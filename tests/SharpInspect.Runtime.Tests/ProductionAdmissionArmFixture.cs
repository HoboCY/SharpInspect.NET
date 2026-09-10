using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>Real SQLite and identity transactions; console authority and qualification
/// issuer are confined to the test assembly. This fixture cannot start production.</summary>
internal sealed class ProductionAdmissionArmFixture : IAsyncDisposable
{
    private ServiceProvider? _provider;
    private const string UserName = "admission-admin";
    private const string Password = "V136 independent admission administrator secret!";
    internal const string StationId = "V136ArmTransactionStation";

    private ProductionAdmissionArmFixture(ProductionStoreOptions options) => Options = options;

    internal ProductionStoreOptions Options { get; }
    internal SqliteCommandStore Store { get; private set; } = null!;
    internal LocalAuthorizationService Authorization { get; private set; } = null!;
    internal IInteractiveSessionService Sessions { get; private set; } = null!;
    internal IStepUpAuthentication StepUp { get; private set; } = null!;
    internal ICommandTraceQuery Trace { get; private set; } = null!;
    internal IProductionAdmissionHistoryQuery History { get; private set; } = null!;
    internal StationRuntime Runtime => (StationRuntime)_provider!.GetRequiredService<IStationRuntime>();
    internal Guid PrincipalId { get; private set; }
    internal Guid SessionId { get; private set; }

    internal static async Task<ProductionAdmissionArmFixture> CreateAsync(bool requireStepUp = false,
        int maximumEntries = 10000)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Production admission identity requires Windows DPAPI.");
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V136Arm",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var authorization = requireStepUp
            ? new AuthorizationPolicy("v136-arm-step-up", "v1",
                AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
                    pair => (IEnumerable<Permission>)pair.Value), new[] { Permission.ArmProduction })
            : AuthorizationPolicy.Development;
        var identity = new LocalIdentityOptions(StationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v136-arm-blocklist", "v1",
                    new[] { "known-compromised-value" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization);
        var options = new ProductionStoreOptions(Path.Combine(directory, "admission.sqlite"))
        {
            LocalIdentity = identity,
            AuditIntegrityPolicy = new AuditIntegrityPolicy(StationId, "v1",
                "SharpInspect.Test.V136.Arm." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            },
            ProductionAdmission = new ProductionAdmissionStoreOptions { MaximumEntries = maximumEntries },
            CommitTimeout = TimeSpan.FromSeconds(3),
            QueryTimeout = TimeSpan.FromSeconds(3),
            QueueCapacity = 8
        };
        var fixture = new ProductionAdmissionArmFixture(options);
        try
        {
            await fixture.InitializeAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(Options, TimeSpan.FromMilliseconds(20));
        _provider = services.BuildServiceProvider();
        Store = _provider.GetRequiredService<SqliteCommandStore>();
        var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await WaitVerifiedAsync();
        var bootstrap = new LocalIdentityService(Store, Options.LocalIdentity!, new TestConsole());
        var token = await bootstrap.ProvisionBootstrapTokenAsync();
        Assert.True(token.Succeeded, token.ReasonCode);
        var displayed = token.Token!.TakeForDisplay();
        await WaitVerifiedAsync();
        var created = await bootstrap.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
            StationId, displayed, UserName, "V136 Transaction Administrator", Password));
        Assert.True(created.Succeeded, created.ReasonCode);
        created.RecoveryKit?.Dispose();
        await WaitVerifiedAsync();

        Sessions = _provider.GetRequiredService<IInteractiveSessionService>();
        Authorization = _provider.GetRequiredService<LocalAuthorizationService>();
        StepUp = _provider.GetRequiredService<IStepUpAuthentication>();
        Trace = _provider.GetRequiredService<ICommandTraceQuery>();
        History = _provider.GetRequiredService<IProductionAdmissionHistoryQuery>();
        var signedIn = await Sessions.SignInAsync(new PasswordSignInRequest(UserName, Password));
        Assert.True(signedIn.Succeeded, signedIn.ReasonCode);
        PrincipalId = signedIn.Identity!.PrincipalId;
        SessionId = signedIn.Session.SessionId!.Value;
        await WaitVerifiedAsync();
    }

    internal ArmProductionCommand Command(Guid? correlationId = null) => new(
        correlationId ?? Guid.NewGuid(),
        new CommandInvocation(CommandSource.PhysicalConsole, PrincipalId.ToString("D"), SessionId));

    internal async Task<ArmProductionCommand> WithStepUpAsync(ArmProductionCommand command)
    {
        var grant = await StepUp.ReauthenticateAsync(new StepUpRequest(command.CorrelationId,
            command.Invocation, new StepUpBinding(Permission.ArmProduction, command.CorrelationId,
                StationId, AuditedCommandKind.ArmProduction), Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        Assert.NotNull(grant.GrantId);
        await WaitVerifiedAsync();
        return command with { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } };
    }

    internal async Task WaitVerifiedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var report = Store.Integrity;
            if (report?.State == AuditIntegrityState.Verified) return;
            if (report?.State == AuditIntegrityState.Faulted)
                throw new XunitException("Admission audit faulted: " + report.ReasonCode);
            await Task.Delay(25);
        }
        throw new XunitException("Admission audit verification timed out: " + Store.Integrity?.ReasonCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
        var key = WindowsMachineAuditKey.GetKeyPath(Options.AuditIntegrityPolicy!);
        try { if (File.Exists(key)) File.Delete(key); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TestConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V136-ARM-TEST");
    }
}

#pragma warning restore CA1416
