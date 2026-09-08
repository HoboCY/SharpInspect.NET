using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Runs the T20 consumer in an independent process. A configured consumer path
/// represents the external NuGet consumer; the local SampleHost fallback keeps
/// the contract executable during ordinary repository development.
/// </summary>
public sealed class CameraNetworkConsumerAcceptanceTests
{
    [Fact]
    public async Task V120_N01_IndependentConsumerRunsNetworkMaintenanceAndReadOnlyRestartProof()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "SharpInspect.NET.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CAMERA_NETWORK_CONSUMER");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = configuredConsumer ?? Path.Combine(root!.FullName, "samples",
            "SharpInspect.SampleHost", "bin", configuration, "net6.0-windows",
            "SharpInspect.SampleHost.dll");
        Assert.True(File.Exists(consumer),
            "Build the solution before the camera network process acceptance test.");

        var directory = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CAMERA_NETWORK_EVIDENCE_ROOT") ?? Path.Combine(
            Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket20-preflight",
            "consumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var passwordPolicy = new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("camera-network-development-fixture", "1",
                new[] { "passwordpassword" })
        };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CameraNetworkConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = AuthorizationPolicy.Development;
        var options = new ProductionStoreOptions(Path.Combine(directory,
            "camera-network.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            CameraNetwork = new CameraNetworkStoreOptions(),
            AlarmPolicy = NetworkAlarmPolicy()
        };
        var password = "A8!" + Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(24)) + "相机网络";

        Guid principal;
        try
        {
            await using (var store = new SqliteCommandStore(options))
            {
                var initialized = await store.Initialization;
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);
                var identity = new LocalIdentityService(store, options.LocalIdentity!,
                    new FixtureConsole());
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                await WaitForVerifiedAsync(store);
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(audit.StationId,
                        token.Token!.TakeForDisplay(), "camera.network.engineer", "相机网络工程师",
                        password));
                Assert.True(created.Succeeded, created.ReasonCode);
                principal = created.Identity!.PrincipalId;
                created.RecoveryKit!.Dispose();
                await WaitForVerifiedAsync(store);
            }

            var identityPolicyPath = Path.Combine(directory, "identity-policy.json");
            await File.WriteAllTextAsync(identityPolicyPath, JsonSerializer.Serialize(new
            {
                PasswordPolicyVersion = passwordPolicy.Version,
                BlocklistId = passwordPolicy.Blocklist.Id,
                BlocklistVersion = passwordPolicy.Blocklist.Version,
                BlocklistContentHash = passwordPolicy.Blocklist.ContentHash,
                BlocklistValues = passwordPolicy.Blocklist.Values,
                HashBaselineVersion = PasswordHashBaseline.DevelopmentVersion,
                WorkFactor = PasswordHashBaseline.SecurityFloorIterations,
                AuthenticationPolicy = AuthenticationPolicy.Development,
                AuthorizationPolicy = new
                {
                    authorization.Id, authorization.Version,
                    authorization.RoleBundles, authorization.StepUpPermissions
                }
            }));
            var alarmPolicyPath = Path.Combine(directory, "alarm-policy.json");
            await File.WriteAllTextAsync(alarmPolicyPath, JsonSerializer.Serialize(new
            {
                Id = options.AlarmPolicy!.Id,
                Version = options.AlarmPolicy.Version,
                Rules = options.AlarmPolicy.Rules,
                SourceObservationFreshness = options.AlarmPolicy.SourceObservationFreshness,
                MaximumActiveInstances = options.AlarmPolicy.MaximumActiveInstances,
                MaximumPlcEntries = options.AlarmPolicy.MaximumPlcEntries
            }));

            var common = new[]
            {
                "--trace-db", options.DatabasePath,
                "--audit-key", audit.SigningKeyName,
                "--audit-key-directory", audit.KeyDirectory,
                "--identity-policy", identityPolicyPath,
                "--alarm-policy", alarmPolicyPath,
                "--user-name", "camera.network.engineer",
                "--expected-principal", principal.ToString("D")
            };
            var process = await RunAsync(consumer, root!.FullName,
                new[] { "--camera-network-check", directory }.Concat(common), password);
            Assert.DoesNotContain(password, process.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), process.Output);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("V120-N01 camera-network-consumer PASS", process.Output);

            var childEvidencePath = Path.Combine(directory, "camera-network-evidence.json");
            var childSummaryPath = Path.Combine(directory, "summary.json");
            var armRestartEvidencePath = Path.Combine(directory, "camera-network-arm-restart.json");
            Assert.True(new FileInfo(childEvidencePath).Length > 0);
            Assert.True(new FileInfo(childSummaryPath).Length > 0);
            Assert.True(new FileInfo(armRestartEvidencePath).Length > 0);
            using (var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(childEvidencePath)))
            {
                Assert.Equal("Pass", evidence.RootElement.GetProperty("result").GetString());
                Assert.False(evidence.RootElement.GetProperty("ready").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("requiresRecipeActivation").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("identityVerified").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("sameIdentity").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("previousRequestedObserved").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("auditBeforePhysicalChange").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("startupQualificationOnly").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("anonymousRejectedWithoutProjection").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("missingStepUpRejectedWithoutProjection").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("unsupportedProviderRejectedWithoutProjection").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("unsupportedProviderAuthorityUnchanged").GetBoolean());
                Assert.Equal("CameraNetworkMaintenanceUnsupported",
                    evidence.RootElement.GetProperty("unsupportedProviderReason").GetString());
                Assert.Equal("Persisted",
                    evidence.RootElement.GetProperty("unsupportedProviderAudit").GetString());
                var unsupportedBefore = evidence.RootElement.GetProperty("unsupportedProviderBefore");
                var unsupportedAfter = evidence.RootElement.GetProperty("unsupportedProviderAfter");
                Assert.False(unsupportedBefore.GetProperty("ready").GetBoolean());
                Assert.False(unsupportedAfter.GetProperty("ready").GetBoolean());
                Assert.Equal("Disarmed", unsupportedBefore.GetProperty("armState").GetString());
                Assert.Equal("Disarmed", unsupportedAfter.GetProperty("armState").GetString());
                Assert.True(unsupportedBefore.GetProperty("admissionBlockers").GetArrayLength() > 0);
                Assert.True(unsupportedAfter.GetProperty("admissionBlockers").GetArrayLength() > 0);
                Assert.True(evidence.RootElement.GetProperty("armRestartRejectedByReconciliation").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("armRestartReadyFalse").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("armRestartNoProviderRegistered").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("armRestartNoProviderOpened").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("armRestartRequiresRecipeActivation").GetBoolean());
                Assert.Equal("192.168.10.2", evidence.RootElement.GetProperty("previousAddress").GetString());
                Assert.Equal("192.168.10.42", evidence.RootElement.GetProperty("requestedAddress").GetString());
                Assert.Equal("192.168.10.42", evidence.RootElement.GetProperty("observedAddress").GetString());
                Assert.Equal("NotRun", evidence.RootElement.GetProperty("physicalHardwareQualification").GetString());
                Assert.Equal("NotRun", evidence.RootElement.GetProperty("hostNetworkMutation").GetString());
            }
            using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(childSummaryPath)))
            {
                Assert.Equal("Pass", summary.RootElement.GetProperty("result").GetString());
                Assert.Equal(1, summary.RootElement.GetProperty("appliedCount").GetInt32());
                Assert.False(summary.RootElement.GetProperty("maintenanceLeaseHeld").GetBoolean());
                Assert.False(summary.RootElement.GetProperty("ready").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("requiresRecipeActivation").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("startupQualificationOnly").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("armRestartRejectedByReconciliation").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("armRestartReadyFalse").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("armRestartNoProviderRegistered").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("armRestartNoProviderOpened").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("armRestartRequiresRecipeActivation").GetBoolean());
            }
            using (var armRestart = JsonDocument.Parse(
                await File.ReadAllTextAsync(armRestartEvidencePath)))
            {
                Assert.Equal("Pass", armRestart.RootElement.GetProperty("result").GetString());
                Assert.Equal("Unknown", armRestart.RootElement.GetProperty("startupHandshake").GetString());
                Assert.Equal("Required", armRestart.RootElement.GetProperty("startupRecovery").GetString());
                Assert.Equal(0, armRestart.RootElement.GetProperty("startupPendingDeliveries").GetInt32());
                Assert.True(armRestart.RootElement.GetProperty("startupQualificationOnly").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("emptyCorrelationRejected").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("malformedInvocationRejected").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("anonymousRejected").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("armRejectedByReconciliation").GetBoolean());
                Assert.Equal("CameraNetworkReconciliationRequired",
                    armRestart.RootElement.GetProperty("armReasonCode").GetString());
                Assert.True(armRestart.RootElement.GetProperty("armAuditPersisted").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("armAuthorityUnchanged").GetBoolean());
                Assert.False(armRestart.RootElement.GetProperty("ready").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("requiresRecipeActivation").GetBoolean());
                Assert.Equal("Disarmed", armRestart.RootElement.GetProperty("armState").GetString());
                Assert.True(armRestart.RootElement.GetProperty("noProviderRegistered").GetBoolean());
                Assert.True(armRestart.RootElement.GetProperty("noProviderOpened").GetBoolean());
                Assert.Equal(0, armRestart.RootElement.GetProperty("openedDevices").GetInt32());
                Assert.True(armRestart.RootElement.GetProperty("registeredWriter").GetBoolean());
                Assert.False(armRestart.RootElement.GetProperty("databaseReadOnly").GetBoolean());
            }

            var beforeRestart = await DatabaseHashAsync(options.DatabasePath);
            var restarted = await RunAsync(consumer, root.FullName,
                new[] { "--camera-network-query", directory }.Concat(common), password);
            Assert.DoesNotContain(password, restarted.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "restart.log"), restarted.Output);
            Assert.Equal(0, restarted.ExitCode);
            Assert.Contains("V120-N02 camera-network-restart PASS", restarted.Output);
            Assert.Equal(beforeRestart, await DatabaseHashAsync(options.DatabasePath));

            var restartEvidencePath = Path.Combine(directory, "camera-network-restart.json");
            Assert.True(new FileInfo(restartEvidencePath).Length > 0);
            using (var restart = JsonDocument.Parse(await File.ReadAllTextAsync(restartEvidencePath)))
            {
                Assert.Equal("Pass", restart.RootElement.GetProperty("result").GetString());
                Assert.True(restart.RootElement.GetProperty("typedChangeAudited").GetBoolean());
                Assert.Equal(0, restart.RootElement.GetProperty("openedDevices").GetInt32());
                Assert.Equal(0, restart.RootElement.GetProperty("registeredProviders").GetInt32());
                Assert.False(restart.RootElement.GetProperty("registeredWriter").GetBoolean());
                Assert.True(restart.RootElement.GetProperty("databaseReadOnly").GetBoolean());
                Assert.True(restart.RootElement.GetProperty("mainAndNonEmptyWalHashStable").GetBoolean());
                Assert.True(restart.RootElement.GetProperty("walAbsentOrEmptyEquivalent").GetBoolean());
                Assert.Equal("NotRun", restart.RootElement.GetProperty("production").GetString());
            }

            var acceptance = new
            {
                Result = "Pass",
                Consumer = consumer,
                ExternalNuGetConsumer = configuredConsumer is not null,
                ConsumerSha256 = Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(consumer))),
                IndependentRestart = true,
                DatabaseReadOnlyByRestart = true,
                MainAndNonEmptyWalHashVerified = true,
                CameraNetworkEvidence = childEvidencePath,
                CameraNetworkArmRestartEvidence = armRestartEvidencePath,
                CameraNetworkRestartEvidence = restartEvidencePath,
                ArmRestartRejectedByReconciliation = true,
                ArmRestartReadyFalse = true,
                ArmRestartNoProviderRegistered = true,
                ArmRestartNoProviderOpened = true,
                ArmRestartRequiresRecipeActivation = true,
                PhysicalHardwareQualification = "NotRun",
                StationAcceptance = "NotRun",
                Production = "NotRun",
                NativeCrashIsolation = "NotRun",
                HostNetworkMutation = "NotRun",
                Principal = principal
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"),
                JsonSerializer.Serialize(acceptance, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    private static AlarmPolicy NetworkAlarmPolicy() => new("V120-development", "1",
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

    private static async Task<(int ExitCode, string Output)> RunAsync(string consumer,
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
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(150));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (-1, "CameraNetworkConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout + await stderr);
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<string> DatabaseHashAsync(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var candidate in new[] { path, path + "-wal" })
        {
            var wal = candidate.EndsWith("-wal", StringComparison.Ordinal);
            if (!File.Exists(candidate) || (wal && new FileInfo(candidate).Length == 0))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(wal ? "AbsentOrEmptyWal" : "Absent"));
                continue;
            }
            hash.AppendData(SHA256.HashData(await File.ReadAllBytesAsync(candidate)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
