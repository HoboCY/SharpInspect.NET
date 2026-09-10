using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStoragePolicyConsumerAcceptanceTests
{
    [Fact]
    public async Task V139_N01_RealUiPublishesTwoPoliciesAndIndependentRestartPreservesOldSnapshot()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "SharpInspect.NET.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var configured = Environment.GetEnvironmentVariable("SHARPINSPECT_TRACE_STORAGE_CONSUMER");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = string.IsNullOrWhiteSpace(configured) ? Path.Combine(repository!.FullName,
            "samples", "SharpInspect.SampleHost", "bin", configuration, "net6.0-windows", "SharpInspect.SampleHost.dll") : Path.GetFullPath(configured);
        Assert.True(File.Exists(consumer), "Build SampleHost before running the trace storage UI consumer.");
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPINSPECT_TRACE_STORAGE_EVIDENCE_ROOT");
        var directory = string.IsNullOrWhiteSpace(configuredRoot) ? Path.Combine(Path.GetTempPath(),
            "SharpInspect.NET-validation-artifacts", "ticket39-consumer", Guid.NewGuid().ToString("N")) : Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(directory);
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1", "SharpInspect.TraceStorage." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true, CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1), KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var blocklist = PasswordBlocklist.Create("trace-storage-consumer", "1", new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var authorization = AuthorizationPolicy.Development;
        var options = new ProductionStoreOptions(Path.Combine(directory, "trace-storage-policy.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new(audit.StationId, passwordPolicy, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization),
            TraceStoragePolicies = new() { DeploymentScope = new("Sample.TraceStorage.Deployment", "1", Array.Empty<TraceStorageRouteIdentity>()) }
        };
        Assert.False(File.Exists(options.DatabasePath), "The consumer requires a fresh evidence directory.");
        const string userName = "trace.storage.author";
        var password = "P8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "存储";
        Guid principal;
        try
        {
            await using (var store = new SqliteCommandStore(options))
            {
                var initialized = await store.Initialization;
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerified(store);
                var identity = new LocalIdentityService(store, options.LocalIdentity, new FixtureConsole());
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                var created = await identity.CreateFirstAdministratorAsync(new(audit.StationId, token.Token!.TakeForDisplay(),
                    userName, "Trace Storage Test Author", password));
                token.Token.Dispose();
                Assert.True(created.Succeeded, created.ReasonCode);
                principal = created.Identity!.PrincipalId;
                created.RecoveryKit?.Dispose();
                await WaitForVerified(store);
            }
            var identityPath = Path.Combine(directory, "identity-policy.json");
            await File.WriteAllTextAsync(identityPath, JsonSerializer.Serialize(new
            {
                PasswordPolicyVersion = passwordPolicy.Version, BlocklistId = blocklist.Id,
                BlocklistVersion = blocklist.Version, BlocklistContentHash = blocklist.ContentHash,
                BlocklistValues = blocklist.Values, HashBaselineVersion = PasswordHashBaseline.DevelopmentVersion,
                WorkFactor = PasswordHashBaseline.SecurityFloorIterations, AuthenticationPolicy = AuthenticationPolicy.Development,
                AuthorizationPolicy = new { authorization.Id, authorization.Version, authorization.RoleBundles, authorization.StepUpPermissions }
            }));
            var common = new[] { "--trace-db", options.DatabasePath, "--audit-key", audit.SigningKeyName,
                "--audit-key-directory", audit.KeyDirectory, "--identity-policy", identityPath };
            var run = await Run(consumer, repository!.FullName, common.Concat(new[] { "--trace-storage-policy-check", directory,
                "--user-name", userName, "--expected-principal", principal.ToString("D") }), password);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), run.Output);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("V139_N01 trace-storage-policy PASS", run.Output);
            var before = Hash(options.DatabasePath);
            var cold = await Run(consumer, repository.FullName, common.Concat(new[] { "--trace-storage-policy-query", directory }), null);
            await File.WriteAllTextAsync(Path.Combine(directory, "query.log"), cold.Output);
            Assert.True(cold.ExitCode == 0, cold.Output);
            Assert.Contains("V139_N02 trace-storage-policy-query PASS", cold.Output);
            Assert.Equal(before, Hash(options.DatabasePath));
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "trace-storage-policy-evidence.json")));
            var details = evidence.RootElement;
            Assert.Equal("Pass", details.GetProperty("Result").GetString());
            Assert.True(details.GetProperty("UiPublished").GetBoolean());
            Assert.True(details.GetProperty("NoImplicitEditorDefaults").GetBoolean());
            Assert.True(details.GetProperty("OldSnapshotUnchanged").GetBoolean());
            Assert.Equal(2, details.GetProperty("PublishedVersions").GetInt32());
            Assert.False(details.GetProperty("ProductionReady").GetBoolean());
            Assert.False(details.GetProperty("PreflightCanAdmit").GetBoolean());
            Assert.Equal(Hash(consumer), details.GetProperty("ConsumerSha256").GetString());
            Assert.True(new FileInfo(Path.Combine(directory, "trace-storage-policy-editor.png")).Length > 0);
            using var restart = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "trace-storage-policy-restart.json")));
            Assert.True(restart.RootElement.GetProperty("ReadOnly").GetBoolean());
            Assert.False(restart.RootElement.GetProperty("WriterStarted").GetBoolean());
            Assert.True(restart.RootElement.GetProperty("DatabaseUnchanged").GetBoolean());
            Assert.True(restart.RootElement.GetProperty("MainDatabaseUnchanged").GetBoolean());
            var artifactsBefore = restart.RootElement.GetProperty("SqliteArtifactsBefore");
            var artifactsAfter = restart.RootElement.GetProperty("SqliteArtifactsAfter");
            var artifactSetUnchanged = new[] { "Database", "Wal", "Shm", "Journal" }.All(name =>
                artifactsBefore.GetProperty(name).GetString() == artifactsAfter.GetProperty(name).GetString());
            Assert.Equal(artifactSetUnchanged, restart.RootElement.GetProperty("SqliteArtifactSetUnchanged").GetBoolean());
            Assert.Equal(before, artifactsAfter.GetProperty("Database").GetString());
        }
        finally
        {
            password = string.Empty;
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }

    private static async Task WaitForVerified(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(string consumer, string workingDirectory,
        IEnumerable<string> arguments, string? password)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(consumer);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (password is not null) await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
        catch (TimeoutException)
        {
            process.Kill(true);
            await process.WaitForExitAsync();
            return (-1, "TraceStorageConsumerDeadlineExceeded\n" + await output + await error);
        }
        return (process.ExitCode, await output + await error);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    { public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001"); }
}
