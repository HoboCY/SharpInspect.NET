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
/// Process-boundary acceptance for the public T31 PLC result-contract
/// consumer. The child creates a real draft and release, changes exactly one
/// immutable contract revision with fresh Step-Up and then cold-queries it
/// through the independent read-only capability.
/// </summary>
public sealed class PlcResultContractConsumerAcceptanceTests
{
    private const string UserName = "v131-plc-result-contract-admin";
    private const string RunOutput =
        "V131-C01 plc-result-contract PASS released=true changed=true invalidGrantRejected=true changedIntentRejected=true ready=false active=false armed=false";
    private const string QueryOutput =
        "V131-C02 plc-result-contract-query PASS readOnly=true databaseUnchanged=true revisions=1 bindings=1";

    [Fact]
    public async Task V131_C01_RealNuGetConsumerChangesContractAndColdReadsSameBinding()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(true);
        try
        {
            var run = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[]
                {
                    "--plc-result-contract-check", fixture.Directory,
                    "--user-name", UserName,
                    "--expected-principal", fixture.Principal.ToString("D")
                }.Concat(fixture.CommonArguments()), fixture.Password).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, run.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "process.log"), run.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(RunOutput, run.Output, StringComparison.Ordinal);

            using var evidence = ReadJson(Path.Combine(fixture.Directory, "contract-evidence.json"));
            var runRoot = evidence.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("Result").GetString());
            Assert.Equal("V131-C01", runRoot.GetProperty("CaseId").GetString());
            Assert.Equal(fixture.ExternalNuGet,
                runRoot.GetProperty("ExternalNuGetConsumer").GetBoolean());
            Assert.Equal(HashFile(fixture.Consumer), runRoot.GetProperty("ConsumerSha256").GetString());
            Assert.True(runRoot.GetProperty("Available").GetBoolean());
            Assert.Equal(1, runRoot.GetProperty("ReleasedVersions").GetInt32());
            Assert.Equal(1, runRoot.GetProperty("ContractRevisionCount").GetInt32());
            Assert.Equal(1, runRoot.GetProperty("ContractRevisionPosition").GetInt64());
            Assert.Equal(1, runRoot.GetProperty("SchemaValidationCount").GetInt32());
            Assert.Equal(1, runRoot.GetProperty("BindingCount").GetInt32());
            Assert.Equal(1, runRoot.GetProperty("ReleaseHighWatermark").GetInt64());
            Assert.True(runRoot.GetProperty("InvalidGrantRejected").GetBoolean());
            Assert.Equal("StepUpInvalid", runRoot.GetProperty("InvalidGrantReasonCode").GetString());
            Assert.Equal(0, runRoot.GetProperty("InvalidGrantRevisionCount").GetInt32());
            Assert.True(runRoot.GetProperty("ChangedIntentRejected").GetBoolean());
            Assert.Equal("StepUpInvalid", runRoot.GetProperty("ChangedIntentReasonCode").GetString());
            Assert.Equal(0, runRoot.GetProperty("ChangedIntentRevisionCount").GetInt32());
            Assert.False(runRoot.GetProperty("ReadyAfter").GetBoolean());
            Assert.False(runRoot.GetProperty("ArmedAfter").GetBoolean());
            Assert.Equal("NotRun", runRoot.GetProperty("Production").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("PlcConnection").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("PayloadExecution").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("Activation").GetString());
            Assert.Equal("Sample.PlcResultContract",
                runRoot.GetProperty("Contract").GetProperty("Id").GetString());
            Assert.Equal("Sample.PlcContract.Result",
                runRoot.GetProperty("Schema").GetProperty("Id").GetString());
            Assert.Equal(64, runRoot.GetProperty("Contract").GetProperty("ContentHash").GetString()!.Length);
            Assert.Equal(64, runRoot.GetProperty("ContractRevisionContentHash").GetString()!.Length);
            var binding = runRoot.GetProperty("Binding");
            Assert.Equal("Sample.PlcContract.Result", binding.GetProperty("ResultSchemaId").GetString());
            Assert.Equal(runRoot.GetProperty("Release").GetProperty("RecipeContentHash").GetString(),
                binding.GetProperty("RecipeContentHash").GetString());
            Assert.NotEqual(runRoot.GetProperty("Release").GetProperty("RecordContentHash").GetString(),
                binding.GetProperty("RecipeContentHash").GetString());

            var beforeQuery = HashFile(fixture.Options.DatabasePath);
            var query = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[] { "--plc-result-contract-query", fixture.Directory }
                    .Concat(fixture.CommonArguments()), null).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, query.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "restart.log"), query.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, query.ExitCode);
            Assert.Contains(QueryOutput, query.Output, StringComparison.Ordinal);
            Assert.Equal(beforeQuery, HashFile(fixture.Options.DatabasePath));

            using var restart = ReadJson(Path.Combine(fixture.Directory, "contract-restart.json"));
            var restartRoot = restart.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("Result").GetString());
            Assert.True(restartRoot.GetProperty("ReadOnlyQuery").GetBoolean());
            Assert.True(restartRoot.GetProperty("DatabaseUnchanged").GetBoolean());
            Assert.Equal(1, restartRoot.GetProperty("RevisionCount").GetInt32());
            Assert.Equal(runRoot.GetProperty("ContractRevisionContentHash").GetString(),
                restartRoot.GetProperty("ContractRevisionContentHash").GetString());
            Assert.Equal(runRoot.GetProperty("Contract").GetProperty("ContentHash").GetString(),
                restartRoot.GetProperty("ContractReference").GetProperty("ContentHash").GetString());
            Assert.Equal(runRoot.GetProperty("Binding").GetProperty("BindingContentHash").GetString(),
                restartRoot.GetProperty("BindingContentHash").GetString());
            Assert.Equal(0, restartRoot.GetProperty("OpenedDevices").GetInt32());
            Assert.False(restartRoot.GetProperty("Ready").GetBoolean());

            using var summary = ReadJson(Path.Combine(fixture.Directory, "evidence.json"));
            Assert.Equal("Pass", summary.RootElement.GetProperty("Result").GetString());
            Assert.True(summary.RootElement.GetProperty("IndependentRestart").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("DatabaseUnchangedByRestart").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("Available").GetBoolean());
            Assert.False(summary.RootElement.GetProperty("Active").GetBoolean());
            Assert.False(summary.RootElement.GetProperty("Armed").GetBoolean());
            Assert.False(summary.RootElement.GetProperty("Ready").GetBoolean());
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static async Task<ConsumerFixture> CreateFixtureAsync()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_PLC_RESULT_CONTRACT_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the SampleHost PLC result-contract consumer before acceptance.");
        var configuredRoot = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_PLC_RESULT_CONTRACT_EVIDENCE_ROOT");
        var evidenceRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket31-contract",
                Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredRoot);
        var directory = evidenceRoot;
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "plc-result-contract.sqlite");
        Assert.False(File.Exists(databasePath), "PLC result-contract acceptance requires a fresh directory.");

        var blocklist = PasswordBlocklist.Create("plc-result-contract-development", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.PlcResultContract." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = RecipeDraftTestPolicies.Authoring;
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
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization)
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "契约";
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
                "T31 PLC 结果契约验收管理员", password)).ConfigureAwait(true);
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
            return (-1, "PlcResultContractConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
    }

    private static JsonDocument ReadJson(string path)
    {
        Assert.True(new FileInfo(path).Length > 0, "Missing PLC result-contract artifact: " + path);
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
