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
/// Process boundary acceptance for the public imaging setup and calibration
/// consumer.  The child process receives only the fixture password over stdin;
/// no hardware SDK, network adapter, or PLC is involved.
/// </summary>
public sealed class ImagingSetupConsumerAcceptanceTests
{
    [Fact]
    public async Task V123_N01_IndependentConsumerPersistsExactImagingHistoryAndRejectsOldProfiles()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "SharpInspect.NET.sln")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = Environment.GetEnvironmentVariable("SHARPINSPECT_IMAGING_CALIBRATION_CONSUMER") ??
            Path.Combine(repository!.FullName, "samples", "SharpInspect.SampleHost", "bin", configuration,
                "net6.0-windows", "SharpInspect.SampleHost.dll");
        Assert.True(File.Exists(consumer), "Build the solution before the imaging calibration process acceptance test.");
        var directory = Environment.GetEnvironmentVariable("SHARPINSPECT_IMAGING_CALIBRATION_EVIDENCE_ROOT") ??
            Path.Combine(Path.GetTempPath(), "SharpInspect.ImagingCalibrationConsumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var blocklist = PasswordBlocklist.Create("imaging-calibration-development-fixture", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var authorization = AuthorizationPolicy.Development;
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.ImagingCalibrationConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true, CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1), KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = new ProductionStoreOptions(Path.Combine(directory, "imaging-calibration.sqlite"))
        {
            AuditIntegrityPolicy = audit, CameraSetup = new CameraSetupStoreOptions(),
            ImagingSetup = new ImagingSetupStoreOptions(),
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization)
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "成像";
        try
        {
            Guid principal;
            await using (var store = new SqliteCommandStore(options))
            {
                var initialized = await store.Initialization;
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await Verified(store);
                var identity = new LocalIdentityService(store, options.LocalIdentity, new FixtureConsole());
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                await Verified(store);
                var created = await identity.CreateFirstAdministratorAsync(new(audit.StationId,
                    token.Token!.TakeForDisplay(), "imaging.engineer", "成像校准工程师", password));
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
                "--user-name", "imaging.engineer", "--expected-principal", principal.ToString("D") };
            var process = await Run(consumer, repository!.FullName,
                new[] { "--imaging-calibration-check", directory }.Concat(common), password);
            Assert.DoesNotContain(password, process.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), process.Output);
            Assert.True(process.ExitCode == 0,
                "Imaging calibration consumer failed; inspect process.log in " + directory);
            Assert.Contains("V123-N01 imaging-calibration-consumer PASS", process.Output);
            foreach (var name in new[] { "imaging-calibration-evidence.json",
                "imaging-setup-panel.png", "imaging-setup-panel-form.png" })
                Assert.True(new FileInfo(Path.Combine(directory, name)).Length > 0);

            using (var runEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "imaging-calibration-evidence.json"))))
            {
                var root = runEvidence.RootElement;
                Assert.Equal("Pass", root.GetProperty("Result").GetString());
                Assert.Equal(2, root.GetProperty("ImagingRevision2").GetInt64());
                Assert.Equal(principal.ToString("D"), root.GetProperty("Revision1Actor").GetString());
                Assert.Equal(principal.ToString("D"), root.GetProperty("Revision2Actor").GetString());
                Assert.Equal("Declare initial imaging setup", root.GetProperty("Revision1Reason").GetString());
                Assert.Equal("Declare lens replacement before recalibration", root.GetProperty("Revision2Reason").GetString());
                Assert.True(root.GetProperty("CandidateCompatible").GetBoolean());
                Assert.True(root.GetProperty("HistoricalCompatible").GetBoolean());
                Assert.Equal("DevelopmentOnly", root.GetProperty("CandidateEvidencePurpose").GetString());
                Assert.Equal("DevelopmentOnly", root.GetProperty("HistoricalEvidencePurpose").GetString());
                Assert.True(root.GetProperty("OldProfileRejected").GetBoolean());
                Assert.Equal("CalibrationImagingSetupRevisionMismatch",
                    root.GetProperty("OldProfileReason").GetString());
                Assert.True(root.GetProperty("MissingStepUpRejected").GetBoolean());
                Assert.True(root.GetProperty("WrongExpectedRevisionRejected").GetBoolean());
                Assert.True(root.GetProperty("WrongExpectedHashRejected").GetBoolean());
                Assert.True(root.GetProperty("ExactOperationReplayPreserved").GetBoolean());
                Assert.True(root.GetProperty("ConflictingOperationReplayRejected").GetBoolean());
                Assert.Equal("ImagingSetupOperationConflict",
                    root.GetProperty("ConflictingOperationReplayReason").GetString());
                Assert.True(root.GetProperty("NoAutomaticPhysicalDetection").GetBoolean());
                Assert.True(root.GetProperty("NoDefaultCoefficientContract").GetBoolean());
                Assert.True(root.GetProperty("UiPanelShowsBindingAndHistory").GetBoolean());
                Assert.True(root.GetProperty("UiPanelSubmissionFormEnabled").GetBoolean());
                Assert.True(root.GetProperty("UiPasswordBoxEmpty").GetBoolean());
                Assert.False(root.GetProperty("ProductionReady").GetBoolean());
                Assert.False(root.GetProperty("CanActivate").GetBoolean());
                Assert.False(root.GetProperty("Ready").GetBoolean());
                Assert.Equal(2, root.GetProperty("OpenCount").GetInt32());
                Assert.Equal(0, root.GetProperty("FramesProduced").GetInt64());
            }

            var restarted = await Run(consumer, repository.FullName,
                new[] { "--imaging-calibration-query", directory }.Concat(common), password);
            Assert.DoesNotContain(password, restarted.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "restart.log"), restarted.Output);
            Assert.True(restarted.ExitCode == 0,
                "Imaging calibration restart query failed; inspect restart.log in " + directory);
            Assert.Contains("V123-N02 imaging-calibration-restart PASS", restarted.Output);
            foreach (var name in new[] { "imaging-calibration-restart.json" })
                Assert.True(new FileInfo(Path.Combine(directory, name)).Length > 0);

            using (var restartEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, "imaging-calibration-restart.json"))))
            {
                var root = restartEvidence.RootElement;
                Assert.Equal("Pass", root.GetProperty("Result").GetString());
                Assert.Equal(2, root.GetProperty("HistoryCount").GetInt32());
                Assert.True(root.GetProperty("HashesPersisted").GetBoolean());
                Assert.Equal(principal.ToString("D"), root.GetProperty("Revision1Actor").GetString());
                Assert.Equal(principal.ToString("D"), root.GetProperty("Revision2Actor").GetString());
                Assert.Equal("Declare initial imaging setup", root.GetProperty("Revision1Reason").GetString());
                Assert.Equal("Declare lens replacement before recalibration", root.GetProperty("Revision2Reason").GetString());
                Assert.True(root.GetProperty("ReadOnlyQueryDatabaseBytesUnchanged").GetBoolean());
                Assert.True(root.GetProperty("NoDefaultCoefficientContract").GetBoolean());
                Assert.Equal(0, root.GetProperty("OpenedDevices").GetInt32());
                Assert.False(root.GetProperty("ProductionReady").GetBoolean());
                Assert.False(root.GetProperty("CanActivate").GetBoolean());
                Assert.False(root.GetProperty("Ready").GetBoolean());
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
            {
                Result = "Pass", Consumer = consumer,
                ConsumerSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer))),
                ExternalNuGetConsumer = true, IndependentRestart = true,
                DatabaseReadOnlyByRestart = true, ImagingHistoryRevisions = 2,
                ExactProfileSelection = true, OldProfileRejected = true,
                NoAutomaticPhysicalDetection = true, NoDefaultCoefficientContract = true,
                UiPanelScreenshot = "imaging-setup-panel.png",
                UiPanelFormScreenshot = "imaging-setup-panel-form.png",
                UiPanelShowsBindingAndHistory = true, UiPanelSubmissionFormEnabled = true,
                UiPasswordBoxEmpty = true,
                ProductionReady = false, CanActivate = false, Ready = false,
                PhysicalHardwareQualification = "NotRun", StationAcceptance = "NotRun",
                Production = "NotRun", NativeCrashIsolation = "NotRun", Principal = principal
            }));
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(string consumer,
        string workingDirectory, IEnumerable<string> arguments, string password)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add(consumer);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (-1, "ImagingCalibrationConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout + await stderr);
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
