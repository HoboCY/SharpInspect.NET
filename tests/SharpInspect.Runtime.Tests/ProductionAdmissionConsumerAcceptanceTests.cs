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
/// Process-boundary acceptance for the public schema-22 production-admission
/// consumer.  The child uses a real local identity and WPF shell, while its
/// composition deliberately has no qualification, PLC or production-cycle
/// authority.  A second child uses only the independent read-only history
/// query and must observe the same immutable report without changing SQLite.
/// </summary>
public sealed class ProductionAdmissionConsumerAcceptanceTests
{
    private const string UserName = "v136-production-admission-admin";
    private const string RunOutput =
        "V136_N01 production-admission PASS schema=22 authenticated=true armPermission=true rejected=true gates=24 ready=false disarmed=true";
    private const string QueryOutput =
        "V136_N02 production-admission-query PASS readOnly=true writerStarted=false databaseUnchanged=true gates=24";

    [Fact]
    public async Task V136_N01_RealNuGetConsumerPersistsCompleteRejectionAndV136_N02_ColdReadsIt()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(true);
        try
        {
            var run = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[]
                {
                    "--production-admission-check", fixture.Directory,
                    "--user-name", UserName,
                    "--expected-principal", fixture.Principal.ToString("D")
                }.Concat(fixture.CommonArguments()), fixture.Password).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, run.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "process.log"), run.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(RunOutput, run.Output, StringComparison.Ordinal);

            using var detail = ReadJson(Path.Combine(fixture.Directory,
                "production-admission-evidence.json"));
            var root = detail.RootElement;
            Assert.Equal("Pass", root.GetProperty("Result").GetString());
            Assert.Equal("V136_N01", root.GetProperty("CaseId").GetString());
            Assert.Equal(fixture.ExternalNuGet,
                root.GetProperty("ExternalNuGetConsumer").GetBoolean());
            Assert.Equal(HashFile(fixture.Consumer), root.GetProperty("ConsumerSha256").GetString());
            Assert.Equal(22, root.GetProperty("SchemaVersion").GetInt32());
            Assert.True(root.GetProperty("StartupFenceCleared").GetBoolean());
            Assert.True(root.GetProperty("AuthenticationSucceeded").GetBoolean());
            Assert.True(root.GetProperty("ArmPermissionGranted").GetBoolean());
            Assert.False(root.GetProperty("ArmRequiresStepUp").GetBoolean());
            Assert.True(root.GetProperty("StepUpServiceRegistered").GetBoolean());
            Assert.Equal("Rejected", root.GetProperty("ArmDisposition").GetString());
            Assert.Equal("Persisted", root.GetProperty("ArmAudit").GetString());
            Assert.NotEqual(string.Empty, root.GetProperty("ArmReasonCode").GetString());
            Assert.NotEqual(Guid.Empty, root.GetProperty("CorrelationId").GetGuid());
            Assert.NotEqual(Guid.Empty, root.GetProperty("AttemptId").GetGuid());
            Assert.Equal(fixture.Principal, root.GetProperty("PrincipalId").GetGuid());
            Assert.NotEqual(Guid.Empty, root.GetProperty("SessionId").GetGuid());
            Assert.Equal(24, root.GetProperty("GateCount").GetInt32());
            Assert.True(root.GetProperty("BlockerCount").GetInt32() > 0);
            Assert.False(root.GetProperty("CanArm").GetBoolean());
            Assert.False(root.GetProperty("ReadyBefore").GetBoolean());
            Assert.False(root.GetProperty("ReadyAfter").GetBoolean());
            Assert.False(root.GetProperty("ArmedBefore").GetBoolean());
            Assert.False(root.GetProperty("ArmedAfter").GetBoolean());
            Assert.False(root.GetProperty("ActiveBefore").GetBoolean());
            Assert.False(root.GetProperty("ActiveAfter").GetBoolean());
            Assert.False(root.GetProperty("ProductionFactsCreated").GetBoolean());
            Assert.False(root.GetProperty("PhysicalIoStarted").GetBoolean());
            Assert.True(root.GetProperty("DatabaseChangedByAdmission").GetBoolean());
            Assert.Equal(64, root.GetProperty("ReportContentHash").GetString()!.Length);

            var gates = root.GetProperty("Gates");
            Assert.Equal(24, gates.GetArrayLength());
            Assert.Equal(root.GetProperty("BlockerCount").GetInt32(),
                root.GetProperty("Blockers").GetArrayLength());
            foreach (var required in new[]
            {
                "FrameworkQualification", "ProviderQualification", "PlcCommunication", "ProductionCycle"
            })
            {
                Assert.NotEqual("Passed", FindGate(gates, required).GetProperty("Status").GetString());
            }
            Assert.All(gates.EnumerateArray(), gate =>
            {
                Assert.False(string.IsNullOrWhiteSpace(gate.GetProperty("Gate").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(gate.GetProperty("Status").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(gate.GetProperty("ReasonCode").GetString()));
            });

            var databaseBeforeQuery = HashFile(fixture.Options.DatabasePath);
            var query = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[] { "--production-admission-query", fixture.Directory }
                    .Concat(fixture.CommonArguments()), null).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, query.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "restart.log"), query.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, query.ExitCode);
            Assert.Contains(QueryOutput, query.Output, StringComparison.Ordinal);
            Assert.Equal(databaseBeforeQuery, HashFile(fixture.Options.DatabasePath));

            using var restart = ReadJson(Path.Combine(fixture.Directory,
                "production-admission-restart.json"));
            var cold = restart.RootElement;
            Assert.Equal("Pass", cold.GetProperty("Result").GetString());
            Assert.Equal("V136_N02", cold.GetProperty("CaseId").GetString());
            Assert.True(cold.GetProperty("ReadOnlyQuery").GetBoolean());
            Assert.False(cold.GetProperty("WriterStarted").GetBoolean());
            Assert.False(cold.GetProperty("AlgorithmFactoryCreated").GetBoolean());
            Assert.False(cold.GetProperty("ProviderFactoryCreated").GetBoolean());
            Assert.False(cold.GetProperty("ProductionFactsCreated").GetBoolean());
            Assert.True(cold.GetProperty("DatabaseUnchanged").GetBoolean());
            Assert.Equal(1, cold.GetProperty("RecordCount").GetInt32());
            Assert.Equal("Rejected", cold.GetProperty("Kind").GetString());
            Assert.Equal(root.GetProperty("CorrelationId").GetGuid(),
                cold.GetProperty("CorrelationId").GetGuid());
            Assert.Equal(root.GetProperty("PrincipalId").GetGuid(),
                cold.GetProperty("ActorPrincipalId").GetGuid());
            Assert.Equal(root.GetProperty("SessionId").GetGuid(),
                cold.GetProperty("ActorSessionId").GetGuid());
            Assert.Equal(root.GetProperty("ReportContentHash").GetString(),
                cold.GetProperty("ReportContentHash").GetString());
            Assert.Equal(64, cold.GetProperty("HistoryContentHash").GetString()!.Length);
            Assert.Equal(24, cold.GetProperty("GateCount").GetInt32());
            Assert.False(cold.GetProperty("Ready").GetBoolean());
            Assert.False(cold.GetProperty("Active").GetBoolean());
            Assert.Equal("Disarmed", cold.GetProperty("ArmState").GetString());

            using var summary = ReadJson(Path.Combine(fixture.Directory, "evidence.json"));
            var summaryRoot = summary.RootElement;
            Assert.Equal("Pass", summaryRoot.GetProperty("Result").GetString());
            Assert.True(summaryRoot.GetProperty("IndependentColdRead").GetBoolean());
            Assert.True(summaryRoot.GetProperty("DatabaseUnchangedByColdRead").GetBoolean());
            Assert.False(summaryRoot.GetProperty("Ready").GetBoolean());
            Assert.False(summaryRoot.GetProperty("Active").GetBoolean());
            Assert.False(summaryRoot.GetProperty("Armed").GetBoolean());
            Assert.False(summaryRoot.GetProperty("ProductionFactsCreated").GetBoolean());
            Assert.Equal(root.GetProperty("ReportContentHash").GetString(),
                summaryRoot.GetProperty("ReportContentHash").GetString());
            Assert.Equal(cold.GetProperty("HistoryContentHash").GetString(),
                summaryRoot.GetProperty("HistoryContentHash").GetString());

            using var finalDetail = ReadJson(Path.Combine(fixture.Directory,
                "production-admission-evidence.json"));
            Assert.True(finalDetail.RootElement.GetProperty("IndependentColdRead").GetBoolean());
            Assert.True(finalDetail.RootElement.GetProperty("DatabaseUnchangedByColdRead").GetBoolean());
            Assert.Equal(finalDetail.RootElement.GetProperty("ReportContentHash").GetString(),
                finalDetail.RootElement.GetProperty("HistoryReportContentHash").GetString());
        }
        finally
        {
            await fixture.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static JsonElement FindGate(JsonElement gates, string name) => gates.EnumerateArray()
        .Single(value => value.GetProperty("Gate").GetString() == name);

    private static async Task<ConsumerFixture> CreateFixtureAsync()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_PRODUCTION_ADMISSION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the SampleHost production-admission NuGet consumer before acceptance.");
        var configuredRoot = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_PRODUCTION_ADMISSION_EVIDENCE_ROOT");
        var directory = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket36",
                "production-admission-consumer-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "production-admission.sqlite");
        Assert.False(File.Exists(databasePath), "Production admission acceptance requires a fresh directory.");

        var blocklist = PasswordBlocklist.Create("production-admission-development", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.ProductionAdmission." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = new AuthorizationPolicy(
            "production-admission-development", "production-admission-development-2026-09-v1",
            AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
                pair => pair.Value.AsEnumerable()),
            AuthorizationPolicy.Development.StepUpPermissions);
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization),
            ProductionAdmission = new ProductionAdmissionStoreOptions()
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "准入";
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
                "T36 production admission 验收管理员", password)).ConfigureAwait(true);
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
        return new ConsumerFixture(repository, consumer, directory, options, audit, password,
            principal, policyPath, !string.IsNullOrWhiteSpace(configuredConsumer));
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
            return (-1, "ProductionAdmissionConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
    }

    private static JsonDocument ReadJson(string path)
    {
        Assert.True(File.Exists(path), "Production admission consumer evidence is missing: " + path);
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
            Guid principal, string identityPolicyPath, bool externalNuGet)
        {
            Repository = repository;
            Consumer = consumer;
            Directory = directory;
            Options = options;
            Audit = audit;
            Password = password;
            Principal = principal;
            IdentityPolicyPath = identityPolicyPath;
            ExternalNuGet = externalNuGet;
        }

        internal DirectoryInfo Repository { get; }
        internal string Consumer { get; }
        internal string Directory { get; }
        internal ProductionStoreOptions Options { get; }
        internal AuditIntegrityPolicy Audit { get; }
        internal string Password { get; }
        internal Guid Principal { get; }
        internal string IdentityPolicyPath { get; }
        internal bool ExternalNuGet { get; }

        internal IEnumerable<string> CommonArguments() => new[]
        {
            "--trace-db", Options.DatabasePath,
            "--audit-key", Audit.SigningKeyName,
            "--audit-key-directory", Audit.KeyDirectory,
            "--identity-policy", IdentityPolicyPath
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
