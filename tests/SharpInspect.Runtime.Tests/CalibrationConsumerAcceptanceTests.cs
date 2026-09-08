using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Process-boundary acceptance for the T24 calibration consumer.  The friend
/// test host creates the fresh schema-14 identity store with an isolated
/// console authority; the packaged child is exercised only through its public
/// run and restart modes, so the production Windows administrator gate is not
/// weakened by this test.
/// </summary>
public sealed class CalibrationConsumerAcceptanceTests
{
    private const int SchemaVersion = 14;
    private const string UserName = "v124-validation-admin";

    [Fact]
    public async Task V124_N01_IndependentConsumerRunsCalibrationAndReadOnlyRestart()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the calibration consumer before running the process acceptance test.");

        var configuredEvidence = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT");
        var directory = Path.GetFullPath(configuredEvidence ?? Path.Combine(
            Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket24-process",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "calibration-session.sqlite");
        Assert.False(File.Exists(databasePath),
            "Calibration acceptance requires a fresh evidence directory.");

        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CalibrationConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = CreateStoreOptions(directory, databasePath, audit);
        var password = "V124 isolated fixture " + Guid.NewGuid().ToString("N") + "!";
        Guid principal;
        var bootstrapLog = Path.Combine(directory, "calibration-bootstrap.json");
        var runLog = Path.Combine(directory, "calibration-run.json");
        var restartLog = Path.Combine(directory, "calibration-restart.json");

        try
        {
            principal = await BootstrapInFriendHostAsync(options, password)
                .ConfigureAwait(true);
            await WriteJsonAsync(bootstrapLog, new
            {
                result = "Pass",
                schema = SchemaVersion,
                stationId = audit.StationId,
                userName = UserName,
                principalId = principal,
                freshSchema14 = true,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun",
                production = "NotRun"
            }).ConfigureAwait(true);

            var common = new[]
            {
                "--mode", "run",
                "--directory", directory,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var consumerSha256 = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            var run = await RunProcessAsync(consumer, repository.FullName, common, password)
                .ConfigureAwait(true);
            Assert.DoesNotContain(password, run.Output, StringComparison.Ordinal);
            await WriteJsonAsync(runLog, new
            {
                mode = "run",
                exitCode = run.ExitCode,
                output = run.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("V124-N01 calibration-session-consumer PASS", run.Output);

            var runEvidencePath = Path.Combine(directory, "calibration-session-evidence.json");
            Assert.True(new FileInfo(runEvidencePath).Length > 0);
            using var runEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(runEvidencePath).ConfigureAwait(true));
            var runRoot = runEvidence.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("result").GetString());
            Assert.Equal(SchemaVersion, runRoot.GetProperty("schema").GetInt32());
            Assert.True(runRoot.GetProperty("fixture").GetProperty("developmentOnly").GetBoolean());
            Assert.False(runRoot.GetProperty("fixture").GetProperty("productionAuthority").GetBoolean());
            Assert.True(runRoot.GetProperty("start").GetProperty("accepted").GetBoolean());
            Assert.True(runRoot.GetProperty("start").GetProperty("acceptedBeforeTerminal").GetBoolean());
            Assert.True(runRoot.GetProperty("exit").GetProperty("restorationVerified").GetBoolean());
            Assert.False(runRoot.GetProperty("global").GetProperty("ready").GetBoolean());
            Assert.Equal("NotRun", runRoot.GetProperty("global")
                .GetProperty("physicalHardwareQualification").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("global")
                .GetProperty("stationAcceptance").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("global")
                .GetProperty("production").GetString());

            var afterExit = runRoot.GetProperty("evidenceAfterExit");
            Assert.Equal(3, afterExit.GetProperty("frameCount").GetInt32());
            Assert.Equal(3, afterExit.GetProperty("observationCount").GetInt32());
            Assert.Equal(1, afterExit.GetProperty("exclusionCount").GetInt32());
            var candidate = runRoot.GetProperty("candidate");
            var candidateHash = candidate.GetProperty("contentHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(candidateHash));
            Assert.True(candidate.GetProperty("developmentOnly").GetBoolean());
            Assert.False(candidate.GetProperty("canPublish").GetBoolean());
            Assert.False(candidate.GetProperty("canActivate").GetBoolean());
            Assert.True(runRoot.GetProperty("rawFrameRead").GetProperty("tightStride").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(runRoot.GetProperty("start")
                .GetProperty("authorizationTarget").GetString()));

            var screenshots = runRoot.GetProperty("screenshots");
            Assert.True(screenshots.GetArrayLength() >= 1);
            foreach (var screenshot in screenshots.EnumerateArray())
            {
                var name = screenshot.GetString();
                Assert.NotNull(name);
                Assert.NotEmpty(name!);
                var screenshotPath = Path.GetFullPath(Path.Combine(directory, name!));
                Assert.StartsWith(directory + Path.DirectorySeparatorChar, screenshotPath,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(new FileInfo(screenshotPath).Length > 0);
            }

            var restartArguments = new[]
            {
                "--mode", "restart",
                "--directory", directory,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var restart = await RunProcessAsync(consumer, repository.FullName,
                restartArguments, password).ConfigureAwait(true);
            Assert.DoesNotContain(password, restart.Output, StringComparison.Ordinal);
            await WriteJsonAsync(restartLog, new
            {
                mode = "restart",
                exitCode = restart.ExitCode,
                output = restart.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(restart.ExitCode == 0, restart.Output);
            Assert.Contains("V124-N02 calibration-session-restart PASS", restart.Output);

            var restartEvidencePath = Path.Combine(directory, "calibration-session-restart.json");
            Assert.True(new FileInfo(restartEvidencePath).Length > 0);
            using var restartEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(restartEvidencePath).ConfigureAwait(true));
            var restartRoot = restartEvidence.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("result").GetString());
            Assert.Equal(SchemaVersion, restartRoot.GetProperty("schema").GetInt32());
            Assert.Equal(runRoot.GetProperty("sessionId").GetString(),
                restartRoot.GetProperty("sessionId").GetString());
            Assert.Equal(candidateHash, restartRoot.GetProperty("candidateHash").GetString());
            Assert.True(restartRoot.GetProperty("readOnlyQueryDatabaseUnchanged").GetBoolean());
            Assert.Equal(0, restartRoot.GetProperty("openedDevices").GetInt32());
            Assert.False(restartRoot.GetProperty("ready").GetBoolean());
            Assert.True(restartRoot.GetProperty("productionOutputsAbsent").GetBoolean());
            Assert.Equal("NotRun", restartRoot.GetProperty("physicalHardwareQualification").GetString());
            Assert.Equal("NotRun", restartRoot.GetProperty("stationAcceptance").GetString());
            Assert.Equal("NotRun", restartRoot.GetProperty("production").GetString());

            var hash = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            Assert.Equal(consumerSha256, hash);
            Assert.Equal(64, hash.Length);
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "SharpInspect.NET.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!;
    }

    private static string ResolveConsumer(DirectoryInfo repository, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var candidates = new[]
        {
            Path.Combine(repository.FullName, "samples", "SharpInspect.CalibrationConsumer",
                "bin", configuration, "net6.0-windows", "SharpInspect.CalibrationConsumer.dll"),
            Path.Combine(repository.FullName, "samples", "SharpInspect.CalibrationConsumer",
                "bin", "x64", configuration, "net6.0-windows", "SharpInspect.CalibrationConsumer.dll")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static async Task<Guid> BootstrapInFriendHostAsync(ProductionStoreOptions options,
        string password)
    {
        await using var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization.ConfigureAwait(true);
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await WaitForVerifiedAsync(store).ConfigureAwait(true);

        var identityOptions = options.LocalIdentity!;
        Assert.Contains(Permission.RunCalibration,
            identityOptions.AuthorizationPolicy.GetPermissions(HumanRoleBundle.Administrator));
        var identity = new LocalIdentityService(store, identityOptions, new FixtureConsole());
        var issued = await identity.ProvisionBootstrapTokenAsync().ConfigureAwait(true);
        Assert.True(issued.Succeeded, issued.ReasonCode);
        Assert.NotNull(issued.Token);
        var created = await identity.CreateFirstAdministratorAsync(
            new BootstrapAdministratorRequest(options.AuditIntegrityPolicy!.StationId,
                issued.Token!.TakeForDisplay(), UserName,
                "Calibration Consumer Validation Administrator", password))
            .ConfigureAwait(true);
        Assert.True(created.Succeeded, created.ReasonCode);
        Assert.NotNull(created.Identity);
        var principal = created.Identity!.PrincipalId;
        created.RecoveryKit?.Dispose();
        await WaitForVerifiedAsync(store).ConfigureAwait(true);
        return principal;
    }

    private static ProductionStoreOptions CreateStoreOptions(string directory,
        string databasePath, AuditIntegrityPolicy audit)
    {
        var evidenceRoot = Path.GetFullPath(Path.Combine(directory, "calibration-evidence"));
        Directory.CreateDirectory(evidenceRoot);
        return new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = CreateIdentityOptions(audit.StationId),
            AlarmPolicy = RecoveryAlarmPolicy(),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            ImagingSetup = new ImagingSetupStoreOptions(),
            CalibrationSessions = new CalibrationSessionStoreOptions
            {
                EvidenceRoot = evidenceRoot,
                MaximumSessions = 4,
                MaximumEvents = 256,
                MaximumEventPayloadBytes = 256 * 1024,
                MaximumFramesPerSession = 4,
                MaximumFrameBytes = 16L * 1024 * 1024,
                MaximumTotalFrameBytes = 64L * 1024 * 1024
            }
        };
    }

    private static LocalIdentityOptions CreateIdentityOptions(string stationId)
    {
        var roles = AuthorizationPolicy.Development.RoleBundles.ToDictionary(
            pair => pair.Key, pair => (IEnumerable<Permission>)pair.Value);
        roles[HumanRoleBundle.Technician] = roles[HumanRoleBundle.Technician]
            .Append(Permission.RunCalibration);
        roles[HumanRoleBundle.Administrator] = roles[HumanRoleBundle.Administrator]
            .Append(Permission.RunCalibration);
        return new LocalIdentityOptions(stationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create(
                    "calibration-consumer-blocklist", "1", new[] { "passwordpassword" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            new AuthorizationPolicy("calibration-consumer", "calibration-consumer-2026-09",
                roles));
    }

    private static AlarmPolicy RecoveryAlarmPolicy() => new("V124-calibration-consumer", "1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraDisconnected", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, false,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraRecoveryFailed", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery)
        }.Concat(Enum.GetValues<CameraProtocolViolationKind>().Select(kind =>
            new AlarmPolicyRule(CameraAcquisitionAlarmCode(kind), "Runtime.CameraAcquisition",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution))).ToArray(),
        TimeSpan.FromSeconds(10), maximumActiveInstances: 256, maximumPlcEntries: 16);

    private static string CameraAcquisitionAlarmCode(CameraProtocolViolationKind kind) => kind switch
    {
        CameraProtocolViolationKind.EarlyFrame => "CameraEarlyFrame",
        CameraProtocolViolationKind.ExtraFrame => "CameraExtraFrame",
        CameraProtocolViolationKind.LateFrame => "CameraLateFrame",
        CameraProtocolViolationKind.CorrelationMismatch => "CameraCorrelationMismatch",
        CameraProtocolViolationKind.EarlyHardwarePulse => "CameraEarlyHardwarePulse",
        CameraProtocolViolationKind.DuplicateHardwarePulse => "CameraDuplicateHardwarePulse",
        CameraProtocolViolationKind.TriggerWhileBusy => "CameraTriggerWhileBusy",
        CameraProtocolViolationKind.InvalidFrame => "CameraInvalidFrame",
        CameraProtocolViolationKind.ObservationGap => "CameraObservationGap",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string consumer,
        string workingDirectory, IEnumerable<string> arguments, string password)
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
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password))
            .ConfigureAwait(true);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180))
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
            return (-1, "CalibrationConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) +
            await stderr.ConfigureAwait(true));
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task<string> ConsumerHashAsync(string consumer) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer)
            .ConfigureAwait(true)));

    private static Task WriteJsonAsync(string path, object value) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
