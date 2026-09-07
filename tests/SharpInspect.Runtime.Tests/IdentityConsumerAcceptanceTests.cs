using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class IdentityConsumerAcceptanceTests
{
    [Fact]
    public async Task V104_P01_IndependentWpfProcessAuthenticatesRealVerifierAndPreservesIdentity()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharpInspect.NET.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = Environment.GetEnvironmentVariable("SHARPINSPECT_IDENTITY_CONSUMER") ?? Path.Combine(root!.FullName,
            "samples", "SharpInspect.SampleHost", "bin", configuration, "net6.0-windows", "SharpInspect.SampleHost.dll");
        Assert.True(File.Exists(consumer), "Build the solution before running the process acceptance test.");
        var directory = Path.Combine(root.FullName, "artifacts", "ticket04-process-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocklist = PasswordBlocklist.Create("process-development-fixture", "v1", new[] { "passwordpassword" });
        var policy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1", "SharpInspect.IdentityConsumer." + Guid.NewGuid().ToString("N"))
        { AllowInitialKeyCreation = true, CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys") };
        var options = new ProductionStoreOptions(Path.Combine(directory, "identity.sqlite"))
        { AuditIntegrityPolicy = audit, LocalIdentity = new LocalIdentityOptions(audit.StationId, policy, new Pbkdf2PasswordHasher()) };
        var password = "  S4@" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + " 汉字𝄞  ";
        Guid expectedPrincipal;
        try
        {
            await using (var store = new SqliteCommandStore(options))
            {
                Assert.True((await store.Initialization).Committed);
                await Verified(store);
                var service = new LocalIdentityService(store, options.LocalIdentity, new FixtureConsole());
                var issued = await service.ProvisionBootstrapTokenAsync();
                Assert.True(issued.Succeeded, issued.ReasonCode);
                await Verified(store);
                var created = await service.CreateFirstAdministratorAsync(new(audit.StationId, issued.Token!.TakeForDisplay(),
                    "lin.xia", "林晓", password));
                Assert.True(created.Succeeded, created.ReasonCode);
                expectedPrincipal = created.Identity!.PrincipalId;
                created.RecoveryKit!.Dispose();
                await Verified(store);
            }
            var policyPath = Path.Combine(directory, "development-identity-policy.json");
            await File.WriteAllTextAsync(policyPath, JsonSerializer.Serialize(new
            { PasswordPolicyVersion = policy.Version, BlocklistId = blocklist.Id, BlocklistVersion = blocklist.Version,
                BlocklistContentHash = blocklist.ContentHash, BlocklistValues = blocklist.Values,
                HashBaselineVersion = PasswordHashBaseline.DevelopmentVersion, WorkFactor = PasswordHashBaseline.SecurityFloorIterations }));
            var screenshot = Path.Combine(directory, "identity-window.png");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root.FullName };
            foreach (var argument in new[] { consumer, "--identity-login-smoke", "--trace-db", options.DatabasePath,
                "--audit-key", audit.SigningKeyName, "--audit-key-directory", audit.KeyDirectory, "--identity-policy", policyPath,
                "--user-name", "lin.xia", "--expected-principal", expectedPrincipal.ToString("D"), "--screenshot", screenshot })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            var outputTask = child.StandardOutput.ReadToEndAsync();
            var errorTask = child.StandardError.ReadToEndAsync();
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
            child.StandardInput.Close();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { child.Kill(entireProcessTree: true); throw; }
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(!output.Contains(password, StringComparison.Ordinal) && !error.Contains(password, StringComparison.Ordinal), "Secret appeared in child output.");
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), output + error);
            Assert.True(child.ExitCode == 0, "The independent WPF identity smoke failed; inspect its non-secret process log.");
            Assert.Contains("V104-P01 independent-process WPF password login/immutable identity/privacy PASS", output);
            Assert.True(File.Exists(screenshot));
            var verified = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new());
            Assert.Equal(AuditIntegrityState.Verified, verified.State);
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
            { Result = "Pass", Consumer = consumer, ConsumerSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer))),
                PrincipalId = expectedPrincipal, VerifiedThrough = verified.VerifiedThroughSequence, Screenshot = screenshot }));
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }

    private static async Task Verified(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
