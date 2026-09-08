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
/// Runs the T19 sample as a separate process. The sample can be pointed at a
/// packaged NuGet consumer through SHARPINSPECT_CAMERA_RECOVERY_CONSUMER; the
/// fallback is useful for ordinary local development and is never labelled as
/// an external package result in the evidence.
/// </summary>
public sealed class CameraRecoveryConsumerAcceptanceTests
{
    [Fact]
    public async Task V119_N01_IndependentConsumerRunsBoundedRecoveryAndReadOnlyRestartProof()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "SharpInspect.NET.sln"))) root = root.Parent;
        Assert.NotNull(root);

        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CAMERA_RECOVERY_CONSUMER");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = configuredConsumer ?? Path.Combine(root!.FullName, "samples",
            "SharpInspect.SampleHost", "bin", configuration, "net6.0-windows",
            "SharpInspect.SampleHost.dll");
        Assert.True(File.Exists(consumer),
            "Build the solution before the camera recovery process acceptance test.");

        var directory = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CAMERA_RECOVERY_EVIDENCE_ROOT") ?? Path.Combine(
            Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket19-preflight",
            "consumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var passwordPolicy = new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("camera-recovery-development-fixture", "1",
                new[] { "passwordpassword" })
        };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CameraRecoveryConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = AuthorizationPolicy.Development;
        var options = new ProductionStoreOptions(Path.Combine(directory,
            "camera-recovery.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            AlarmPolicy = RecoveryAlarmPolicy()
        };
        var password = "A8!" + Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(24)) + "相机恢复";

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
                        token.Token!.TakeForDisplay(), "camera.recovery.engineer", "相机恢复工程师",
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
            await File.WriteAllTextAsync(alarmPolicyPath, JsonSerializer.Serialize(
                new
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
                "--user-name", "camera.recovery.engineer",
                "--expected-principal", principal.ToString("D")
            };
            var process = await RunAsync(consumer, root!.FullName,
                new[] { "--camera-recovery-check", directory }.Concat(common), password)
                ;
            Assert.DoesNotContain(password, process.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"),
                process.Output);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("V119-N01 camera-recovery-consumer PASS", process.Output);

            var childEvidence = Path.Combine(directory, "camera-recovery-evidence.json");
            var childSummary = Path.Combine(directory, "summary.json");
            Assert.True(new FileInfo(childEvidence).Length > 0);
            Assert.True(new FileInfo(childSummary).Length > 0);
            using (var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(childEvidence)
                       ))
            {
                Assert.Equal("Pass", evidence.RootElement.GetProperty("result").GetString());
                Assert.False(evidence.RootElement.GetProperty("ready").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("sameIdentity").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("readBackFailureObserved").GetBoolean());
                Assert.True(evidence.RootElement.GetProperty("appearanceAfterExhaustionDidNotRetry")
                    .GetBoolean());
                Assert.Equal(20, evidence.RootElement.GetProperty("exhaustionMaximumAttempts")
                    .GetInt32());
                Assert.True(evidence.RootElement.GetProperty("cameraRecoveryFailedLatched")
                    .GetBoolean());
            }
            using (var summary = JsonDocument.Parse(await File.ReadAllTextAsync(childSummary)
                       ))
            {
                Assert.Equal("Pass", summary.RootElement.GetProperty("result").GetString());
                Assert.Equal(24, summary.RootElement.GetProperty("recoveryAttempts").GetInt32());
                Assert.Equal(4, summary.RootElement.GetProperty("acquisitionAttempts").GetInt32());
                Assert.True(summary.RootElement.GetProperty("cycleIds").GetArrayLength() >= 3);
                Assert.False(summary.RootElement.GetProperty("ready").GetBoolean());
                Assert.True(summary.RootElement.GetProperty("alarmLatched").GetBoolean());
            }

            var beforeRestart = await DatabaseHashAsync(options.DatabasePath);
            var restarted = await RunAsync(consumer, root.FullName,
                new[] { "--camera-recovery-query", directory }.Concat(common), password)
                ;
            Assert.DoesNotContain(password, restarted.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "restart.log"),
                restarted.Output);
            Assert.Equal(0, restarted.ExitCode);
            Assert.Contains("V119-N02 camera-recovery-restart PASS", restarted.Output);
            Assert.Equal(beforeRestart, await DatabaseHashAsync(options.DatabasePath)
                );
            Assert.True(new FileInfo(Path.Combine(directory,
                "camera-recovery-restart.json")).Length > 0);

            var acceptance = new
            {
                Result = "Pass",
                Consumer = consumer,
                ExternalNuGetConsumer = configuredConsumer is not null,
                ConsumerSha256 = Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(consumer))),
                IndependentRestart = true,
                DatabaseReadOnlyByRestart = true,
                RecoveryCycleAndAttemptEvidence = childEvidence,
                PhysicalHardwareQualification = "NotRun",
                StationAcceptance = "NotRun",
                Production = "NotRun",
                NativeCrashIsolation = "NotRun",
                Principal = principal
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"),
                JsonSerializer.Serialize(acceptance, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    private static AlarmPolicy RecoveryAlarmPolicy() => new("V119-development", "1",
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
        catch (Exception exception) when (exception is TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (-1, "CameraRecoveryConsumerProcessDeadlineExceeded" + Environment.NewLine +
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
            var isWalSidecar = candidate.EndsWith("-wal", StringComparison.Ordinal);
            if (!File.Exists(candidate) ||
                (isWalSidecar && new FileInfo(candidate).Length == 0))
            {
                // A read-only SQLite WAL connection may materialize an empty
                // sidecar while initializing shared memory. Treat absent and
                // empty WAL alike; any WAL payload still changes the digest.
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes(
                    isWalSidecar ? "AbsentOrEmptyWal" : "Absent"));
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
