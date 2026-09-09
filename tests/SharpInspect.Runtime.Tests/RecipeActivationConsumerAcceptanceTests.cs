using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Process-boundary acceptance for the public T32 recipe activation consumer.
/// The child creates a real explicit-None release, admits an activation through
/// the public service, and then records the qualification rejection. A second
/// child constructs only the public read-only query and reads those exact events.
/// </summary>
public sealed class RecipeActivationConsumerAcceptanceTests
{
    private const string UserName = "v132-recipe-activation-admin";
    private const string RunOutput =
        "V132_N01 recipe-activation PASS access=true requiresStepUp=false admitted=true terminalRejected=true reason=FrameworkQualificationAuthorityUnavailable providerDiscovery=0 providerOpen=0 ready=false active=false armed=false";
    private const string QueryOutput =
        "V132_N02 recipe-activation-query PASS readOnly=true databaseUnchanged=true records=2 active=false pending=false providerDiscovery=0 providerOpen=0";

    [Fact]
    public async Task V132_N01_RealNuGetConsumerRejectsActivationAndV132_N02_ColdReadsSameHistory()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(true);
        try
        {
            var run = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[]
                {
                    "--recipe-activation-check", fixture.Directory,
                    "--user-name", UserName,
                    "--expected-principal", fixture.Principal.ToString("D")
                }.Concat(fixture.CommonArguments()), fixture.Password).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, run.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "process.log"), run.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(RunOutput, run.Output, StringComparison.Ordinal);

            using var evidence = ReadJson(Path.Combine(fixture.Directory, "activation-evidence.json"));
            var runRoot = evidence.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("Result").GetString());
            Assert.Equal("V132_N01", runRoot.GetProperty("CaseId").GetString());
            Assert.Equal(fixture.ExternalNuGet,
                runRoot.GetProperty("ExternalNuGetConsumer").GetBoolean());
            Assert.Equal(HashFile(fixture.Consumer), runRoot.GetProperty("ConsumerSha256").GetString());
            Assert.True(runRoot.GetProperty("StartupFenceCleared").GetBoolean());
            Assert.True(runRoot.GetProperty("ActivationAccessAvailable").GetBoolean());
            Assert.False(runRoot.GetProperty("ActivationRequiresStepUp").GetBoolean());
            Assert.Equal(1, runRoot.GetProperty("ReleasedVersions").GetInt32());
            Assert.Equal("None", runRoot.GetProperty("PartIdentityMode").GetString());
            Assert.Equal(1, runRoot.GetProperty("AdmissionPosition").GetInt64());
            Assert.Equal(2, runRoot.GetProperty("TerminalPosition").GetInt64());
            Assert.Equal("Failed", runRoot.GetProperty("TerminalOutcome").GetString());
            Assert.Equal("FrameworkQualificationAuthorityUnavailable",
                runRoot.GetProperty("TerminalReasonCode").GetString());
            Assert.Equal("LocalAuthority", runRoot.GetProperty("EvidenceKind").GetString());
            Assert.Equal("NotRequired", runRoot.GetProperty("RestorationState").GetString());
            Assert.False(runRoot.GetProperty("ReadyBefore").GetBoolean());
            Assert.False(runRoot.GetProperty("ReadyAfter").GetBoolean());
            Assert.False(runRoot.GetProperty("ActiveBefore").GetBoolean());
            Assert.False(runRoot.GetProperty("ActiveAfter").GetBoolean());
            Assert.False(runRoot.GetProperty("ArmedBefore").GetBoolean());
            Assert.False(runRoot.GetProperty("ArmedAfter").GetBoolean());
            Assert.True(runRoot.GetProperty("AlgorithmPreparationRegistered").GetBoolean());
            Assert.True(runRoot.GetProperty("FramePoolRegistered").GetBoolean());
            Assert.True(runRoot.GetProperty("CameraProviderRegistered").GetBoolean());
            Assert.Equal(0, runRoot.GetProperty("AlgorithmFactoryCreateCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("AlgorithmWarmUpCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("AlgorithmDisposeCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("ProviderDiscoveryCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("ProviderOpenCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("ProviderApplyCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("ProviderStartCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("ProviderStopCalls").GetInt32());
            Assert.Equal(0, runRoot.GetProperty("FramePoolOutstandingLeases").GetInt32());
            Assert.Equal("NotRun", runRoot.GetProperty("Production").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("PlcConnection").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("PayloadExecution").GetString());
            Assert.Equal("RejectedBeforePhysicalIo", runRoot.GetProperty("Activation").GetString());
            Assert.True(runRoot.GetProperty("DatabaseChangedByActivation").GetBoolean());
            Assert.Equal(runRoot.GetProperty("AdmissionContentHash").GetString(),
                runRoot.GetProperty("TerminalAdmissionContentHash").GetString());
            Assert.Equal(64, runRoot.GetProperty("AdmissionContentHash").GetString()!.Length);
            Assert.Equal(64, runRoot.GetProperty("TerminalContentHash").GetString()!.Length);

            var checks = runRoot.GetProperty("Checks");
            foreach (var expected in new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["V132.A12"] = "FrameworkQualificationAuthorityUnavailable",
                ["V132.A13"] = "ProviderQualificationAuthorityUnavailable",
                ["V132.A14"] = "ProductionAcquisitionAuthorityUnavailable",
                ["V132.A15"] = "PlcDeploymentAuthorityUnavailable",
                ["V132.A16"] = "EvidenceAdmissionAuthorityUnavailable",
                ["V132.A17"] = "DeploymentPolicyAuthorityUnavailable",
                ["V132.A18"] = "ProductionCycleUnavailable"
            })
            {
                var check = FindCheck(checks, expected.Key);
                Assert.Equal("Failed", check.GetProperty("Status").GetString());
                Assert.Equal(expected.Value, check.GetProperty("ReasonCode").GetString());
            }
            Assert.Equal("NotApplicable", FindCheck(checks, "V132.A04").GetProperty("Status").GetString());
            Assert.Equal("NotRun", FindCheck(checks, "V132.A06").GetProperty("Status").GetString());
            Assert.Equal("NotRun", FindCheck(checks, "V132.A11").GetProperty("Status").GetString());

            var beforeQuery = HashFile(fixture.Options.DatabasePath);
            var query = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[] { "--recipe-activation-query", fixture.Directory }
                    .Concat(fixture.CommonArguments()), null).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, query.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "restart.log"), query.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, query.ExitCode);
            Assert.Contains(QueryOutput, query.Output, StringComparison.Ordinal);
            Assert.Equal(beforeQuery, HashFile(fixture.Options.DatabasePath));

            using var restart = ReadJson(Path.Combine(fixture.Directory, "activation-restart.json"));
            var restartRoot = restart.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("Result").GetString());
            Assert.Equal("V132_N02", restartRoot.GetProperty("CaseId").GetString());
            Assert.True(restartRoot.GetProperty("ReadOnlyQuery").GetBoolean());
            Assert.False(restartRoot.GetProperty("WriterStarted").GetBoolean());
            Assert.False(restartRoot.GetProperty("AlgorithmFactoryCreated").GetBoolean());
            Assert.False(restartRoot.GetProperty("ProviderFactoryCreated").GetBoolean());
            Assert.True(restartRoot.GetProperty("DatabaseUnchanged").GetBoolean());
            Assert.Equal(2, restartRoot.GetProperty("RecordCount").GetInt32());
            Assert.Equal(1, restartRoot.GetProperty("AdmissionPosition").GetInt64());
            Assert.Equal(2, restartRoot.GetProperty("TerminalPosition").GetInt64());
            Assert.Equal(runRoot.GetProperty("AdmissionContentHash").GetString(),
                restartRoot.GetProperty("AdmissionContentHash").GetString());
            Assert.Equal(runRoot.GetProperty("TerminalContentHash").GetString(),
                restartRoot.GetProperty("TerminalContentHash").GetString());
            Assert.Equal(runRoot.GetProperty("TerminalAdmissionContentHash").GetString(),
                restartRoot.GetProperty("TerminalAdmissionContentHash").GetString());
            Assert.Equal("RecipeActivationNoActiveRecord",
                restartRoot.GetProperty("CurrentReasonCode").GetString());
            Assert.False(restartRoot.GetProperty("RecoveryRequired").GetBoolean());
            Assert.False(restartRoot.GetProperty("Ready").GetBoolean());
            Assert.False(restartRoot.GetProperty("Active").GetBoolean());
            Assert.Equal("Disarmed", restartRoot.GetProperty("ArmState").GetString());
            Assert.Equal(0, restartRoot.GetProperty("ProviderDiscoveryCalls").GetInt32());
            Assert.Equal(0, restartRoot.GetProperty("ProviderOpenCalls").GetInt32());
            Assert.Equal(0, restartRoot.GetProperty("AlgorithmFactoryCreateCalls").GetInt32());
            Assert.Equal(0, restartRoot.GetProperty("FramePoolOutstandingLeases").GetInt32());

            using var summary = ReadJson(Path.Combine(fixture.Directory, "evidence.json"));
            var summaryRoot = summary.RootElement;
            Assert.Equal("Pass", summaryRoot.GetProperty("Result").GetString());
            Assert.True(summaryRoot.GetProperty("IndependentColdRead").GetBoolean());
            Assert.True(summaryRoot.GetProperty("DatabaseUnchangedByColdRead").GetBoolean());
            Assert.False(summaryRoot.GetProperty("Ready").GetBoolean());
            Assert.False(summaryRoot.GetProperty("Active").GetBoolean());
            Assert.False(summaryRoot.GetProperty("Armed").GetBoolean());
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static JsonElement FindCheck(JsonElement checks, string checkId) => checks.EnumerateArray()
        .Single(value => value.GetProperty("CheckId").GetString() == checkId);

    private static async Task<ConsumerFixture> CreateFixtureAsync()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_RECIPE_ACTIVATION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the SampleHost recipe-activation NuGet consumer before acceptance.");
        var configuredRoot = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_RECIPE_ACTIVATION_EVIDENCE_ROOT");
        var evidenceRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket32",
                "recipe-activation-consumer-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredRoot);
        var directory = evidenceRoot;
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "recipe-activation.sqlite");
        Assert.False(File.Exists(databasePath), "Recipe activation acceptance requires a fresh directory.");

        var blocklist = PasswordBlocklist.Create("recipe-activation-development", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.RecipeActivation." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = new AuthorizationPolicy(
            "recipe-activation-development", "recipe-activation-development-2026-09-v1",
            AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
                pair => pair.Key is HumanRoleBundle.Technician or HumanRoleBundle.Administrator
                    ? pair.Value.Append(Permission.EditRecipeDraft)
                    : pair.Value.AsEnumerable()),
            AuthorizationPolicy.Development.StepUpPermissions);
        var execution = new AlgorithmExecutionPolicy("Sample.DraftExecution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var releasePolicy = new RecipeGovernancePolicy("Sample.RecipeRelease.SingleApprover", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            RecipeDrafts = new RecipeDraftStoreOptions(execution),
            RecipeReleases = new RecipeReleaseStoreOptions(releasePolicy),
            PlcResultContracts = new PlcResultContractStoreOptions(),
            RecipeActivations = new RecipeActivationStoreOptions(),
            CameraSetup = new CameraSetupStoreOptions(),
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization)
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "激活";
        Guid principal;
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization.ConfigureAwait(true);
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store).ConfigureAwait(true);
            var identity = new LocalIdentityService(store, options.LocalIdentity!, new FixtureConsole());
            var token = await identity.ProvisionBootstrapTokenAsync().ConfigureAwait(true);
            Assert.True(token.Succeeded, token.ReasonCode);
            Assert.NotNull(token.Token);
            var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                audit.StationId, token.Token!.TakeForDisplay(), UserName,
                "T32 public activation 验收管理员", password)).ConfigureAwait(true);
            Assert.True(created.Succeeded, created.ReasonCode);
            Assert.NotNull(created.Identity);
            principal = created.Identity!.PrincipalId;
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store).ConfigureAwait(true);
        }

        var policyPath = Path.Combine(directory, "identity-policy.json");
        await File.WriteAllTextAsync(policyPath, JsonSerializer.Serialize(new
        {
            PasswordPolicyVersion = passwordPolicy.Version,
            BlocklistId = blocklist.Id,
            BlocklistVersion = blocklist.Version,
            BlocklistContentHash = blocklist.ContentHash,
            BlocklistValues = blocklist.Values,
            HashBaselineVersion = PasswordHashBaseline.DevelopmentVersion,
            WorkFactor = PasswordHashBaseline.SecurityFloorIterations,
            AuthenticationPolicy = AuthenticationPolicy.Development,
            AuthorizationPolicy = new
            {
                authorization.Id,
                authorization.Version,
                authorization.RoleBundles,
                authorization.StepUpPermissions
            }
        })).ConfigureAwait(true);
        return new ConsumerFixture(repository, consumer, directory, options, audit, password, principal,
            policyPath, !string.IsNullOrWhiteSpace(configuredConsumer));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharpInspect.NET.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!;
    }

    private static string ResolveConsumer(DirectoryInfo repository, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var candidates = new[]
        {
            Path.Combine(repository.FullName, "samples", "SharpInspect.SampleHost", "bin", configuration,
                "net6.0-windows", "SharpInspect.SampleHost.dll"),
            Path.Combine(repository.FullName, "samples", "SharpInspect.SampleHost", "bin", "x64", configuration,
                "net6.0-windows", "SharpInspect.SampleHost.dll")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string consumer,
        string workingDirectory, IEnumerable<string> arguments, string? password)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add(consumer);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (password is not null)
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password)).ConfigureAwait(true);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            return (-1, "RecipeActivationConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
    }

    private static JsonDocument ReadJson(string path)
    {
        Assert.True(new FileInfo(path).Length > 0, "Missing recipe activation artifact: " + path);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private sealed class ConsumerFixture : IAsyncDisposable
    {
        internal ConsumerFixture(DirectoryInfo repository, string consumer, string directory,
            ProductionStoreOptions options, AuditIntegrityPolicy audit, string password,
            Guid principal, string policyPath, bool externalNuGet)
        {
            Repository = repository;
            Consumer = consumer;
            Directory = directory;
            Options = options;
            Audit = audit;
            Password = password;
            Principal = principal;
            PolicyPath = policyPath;
            ExternalNuGet = externalNuGet;
        }

        internal DirectoryInfo Repository { get; }
        internal string Consumer { get; }
        internal string Directory { get; }
        internal ProductionStoreOptions Options { get; }
        internal AuditIntegrityPolicy Audit { get; }
        internal string Password { get; }
        internal Guid Principal { get; }
        internal string PolicyPath { get; }
        internal bool ExternalNuGet { get; }

        internal IEnumerable<string> CommonArguments() => new[]
        {
            "--trace-db", Options.DatabasePath,
            "--audit-key", Audit.SigningKeyName,
            "--audit-key-directory", Audit.KeyDirectory,
            "--identity-policy", PolicyPath
        };

        public ValueTask DisposeAsync()
        {
            var key = WindowsMachineAuditKey.GetKeyPath(Audit);
            if (File.Exists(key)) File.Delete(key);
            if (System.IO.Directory.Exists(Audit.KeyDirectory))
                System.IO.Directory.Delete(Audit.KeyDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
