using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraSetupConsumerAcceptanceTests
{
    [Fact]
    public async Task V117_N01_IndependentWpfConsumerUsesGovernedCameraSetupAndReopensBinding()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "SharpInspect.NET.sln")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = Environment.GetEnvironmentVariable("SHARPINSPECT_CAMERA_SETUP_CONSUMER") ??
            Path.Combine(repository!.FullName, "samples", "SharpInspect.SampleHost", "bin", configuration,
                "net6.0-windows", "SharpInspect.SampleHost.dll");
        Assert.True(File.Exists(consumer), "Build the solution before the camera setup process acceptance test.");
        var directory = Environment.GetEnvironmentVariable("SHARPINSPECT_CAMERA_SETUP_EVIDENCE_ROOT") ??
            Path.Combine(Path.GetTempPath(), "SharpInspect.CameraSetupConsumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocklist = PasswordBlocklist.Create("camera-setup-development-fixture", "1", new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var authorization = AuthorizationPolicy.Development;
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CameraSetupConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true, CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1), KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = new ProductionStoreOptions(Path.Combine(directory, "camera-setup.sqlite"))
        {
            AuditIntegrityPolicy = audit, CameraSetup = new CameraSetupStoreOptions(),
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy, new Pbkdf2PasswordHasher(),
                AuthenticationPolicy.Development, authorization)
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "相机";
        try
        {
            Guid principal;
            await using (var store = new SqliteCommandStore(options))
            {
                var initialized = await store.Initialization;
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await Verified(store);
                // This fixture supplies bootstrap console evidence only. Consumer login,
                // Step-Up, authorization, Runtime operations, and signed persistence are real.
                var identity = new LocalIdentityService(store, options.LocalIdentity, new FixtureConsole());
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                await Verified(store);
                var created = await identity.CreateFirstAdministratorAsync(new(audit.StationId,
                    token.Token!.TakeForDisplay(), "camera.engineer", "相机调试工程师", password));
                Assert.True(created.Succeeded, created.ReasonCode);
                principal = created.Identity!.PrincipalId;
                created.RecoveryKit!.Dispose();
                await Verified(store);
            }
            var policyPath = Path.Combine(directory, "identity-policy.json");
            await File.WriteAllTextAsync(policyPath, JsonSerializer.Serialize(new
            {
                PasswordPolicyVersion = passwordPolicy.Version, BlocklistId = blocklist.Id,
                BlocklistVersion = blocklist.Version, BlocklistContentHash = blocklist.ContentHash,
                BlocklistValues = blocklist.Values, HashBaselineVersion = PasswordHashBaseline.DevelopmentVersion,
                WorkFactor = PasswordHashBaseline.SecurityFloorIterations,
                AuthenticationPolicy = AuthenticationPolicy.Development,
                AuthorizationPolicy = new { authorization.Id, authorization.Version,
                    authorization.RoleBundles, authorization.StepUpPermissions }
            }));
            var common = new[] { "--trace-db", options.DatabasePath, "--audit-key", audit.SigningKeyName,
                "--audit-key-directory", audit.KeyDirectory, "--identity-policy", policyPath,
                "--user-name", "camera.engineer", "--expected-principal", principal.ToString("D") };
            var process = await Run(consumer, repository!.FullName,
                new[] { "--camera-setup-check", directory }.Concat(common), password);
            Assert.DoesNotContain(password, process.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), process.Output);
            Assert.True(process.ExitCode == 0, "Camera setup WPF consumer failed; inspect process.log in " + directory);
            Assert.Contains("V117-N01 camera-setup-consumer PASS", process.Output);
            foreach (var name in new[] { "camera-setup-applied.png", "camera-setup-failed.png",
                "camera-setup-applied-readback.png", "camera-setup-failed-readback.png", "camera-setup-evidence.json" })
                Assert.True(new FileInfo(Path.Combine(directory, name)).Length > 0);
            var restarted = await Run(consumer, repository.FullName,
                new[] { "--camera-setup-query", directory }.Concat(common), password);
            Assert.DoesNotContain(password, restarted.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "restart.log"), restarted.Output);
            Assert.True(restarted.ExitCode == 0, "Camera setup restart failed; inspect restart.log in " + directory);
            Assert.Contains("V117-N02 camera-setup-restart PASS", restarted.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
            {
                Result = "Pass", Consumer = consumer,
                ConsumerSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer))),
                IndependentRestart = true, BootstrapPhysicalAuthority = "DevelopmentFixture",
                PhysicalDevices = "NotRun", ProviderQualification = "NotRun", StationAcceptance = "NotRun",
                ProductionReady = false, ActiveRecipe = "NotRun", Principal = principal
            }));
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(string consumer, string workingDirectory,
        IEnumerable<string> arguments, string password)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(consumer);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (-1, "CameraSetupConsumerProcessDeadlineExceeded" + Environment.NewLine + await stdout + await stderr);
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static async Task Verified(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    { public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001"); }
}
