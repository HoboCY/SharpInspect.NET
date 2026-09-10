using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Process-boundary acceptance for the public schema 20 calibration-import
/// consumer. The child is resolved from a package-only SampleHost build and
/// receives only an opaque export package plus a real local identity store.
/// </summary>
public sealed class CalibrationImportConsumerAcceptanceTests
{
    private const string UserName = "v134-calibration-import-admin";
    private const string PackageFileName = "calibration-export-package.bin";
    private const string RunOutput =
        "V134_N01 calibration-import PASS imported=true candidate=true tampered=true retained=true ready=false active=false armed=false";

    [Fact]
    public async Task V134_N01_RealNuGetConsumerRetainsCandidateAndRejectsTampering()
    {
        var fixture = await CreateFixtureAsync().ConfigureAwait(true);
        try
        {
            var run = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
                new[]
                {
                    "--calibration-import-check", fixture.Directory,
                    "--user-name", UserName,
                    "--expected-principal", fixture.Principal.ToString("D")
                }.Concat(fixture.CommonArguments()), fixture.Password).ConfigureAwait(true);
            Assert.DoesNotContain(fixture.Password, run.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "process.log"), run.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(RunOutput, run.Output, StringComparison.Ordinal);

            using var evidence = ReadJson(Path.Combine(fixture.Directory,
                "calibration-import-evidence.json"));
            var root = evidence.RootElement;
            Assert.Equal("Pass", root.GetProperty("Result").GetString());
            Assert.Equal("V134_N01", root.GetProperty("CaseId").GetString());
            Assert.Equal(fixture.ExternalNuGet,
                root.GetProperty("ExternalNuGetConsumer").GetBoolean());
            Assert.Equal(fixture.ExternalNuGet,
                root.GetProperty("ExternalPackageReference").GetBoolean());
            Assert.Equal(fixture.ExternalNuGet,
                root.GetProperty("NoProjectReferences").GetBoolean());
            Assert.Equal(HashFile(fixture.Consumer), root.GetProperty("ConsumerSha256").GetString());
            Assert.True(root.GetProperty("StartupFenceVerified").GetBoolean());
            Assert.Equal(64, root.GetProperty("PackageHash").GetString()!.Length);
            Assert.Equal(64, root.GetProperty("CandidateContentHash").GetString()!.Length);
            Assert.Equal(64, root.GetProperty("CandidateReferenceHash").GetString()!.Length);
            Assert.Equal(64, root.GetProperty("SourceManifestHash").GetString()!.Length);
            Assert.False(root.GetProperty("CandidateCanPublish").GetBoolean());
            Assert.False(root.GetProperty("CandidateCanActivate").GetBoolean());
            Assert.True(root.GetProperty("ExactQueryAvailable").GetBoolean());
            Assert.True(root.GetProperty("FirstCandidateRetained").GetBoolean());
            Assert.Equal("Accepted", root.GetProperty("ImportDisposition").GetString());
            Assert.Equal("Rejected", root.GetProperty("TamperedDisposition").GetString());
            Assert.False(root.GetProperty("Ready").GetBoolean());
            Assert.False(root.GetProperty("Active").GetBoolean());
            Assert.Equal("Disarmed", root.GetProperty("ArmState").GetString());
            Assert.Equal("NotRun", root.GetProperty("Inspection").GetString());
            Assert.Equal("NotRun", root.GetProperty("Algorithm").GetString());
            Assert.Equal("NotRun", root.GetProperty("Plc").GetString());

            await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "acceptance.json"),
                JsonSerializer.Serialize(new
                {
                    Result = "Pass",
                    ValidationId = "V134_N01",
                    ExternalNuGetConsumer = fixture.ExternalNuGet,
                    ExternalPackageReference = fixture.ExternalNuGet,
                    NoProjectReferences = fixture.ExternalNuGet,
                    ConsumerSha256 = HashFile(fixture.Consumer),
                    Imported = true,
                    CandidateRetained = true,
                    ExactQuery = true,
                    TamperedPackageRejected = true,
                    CandidateCanPublish = false,
                    CandidateCanActivate = false,
                    Ready = false,
                    Active = false,
                    ArmState = "Disarmed",
                    Inspection = "NotRun",
                    Algorithm = "NotRun",
                    Plc = "NotRun"
                }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(true);
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
            "SHARPINSPECT_CALIBRATION_IMPORT_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the SampleHost calibration-import NuGet consumer before acceptance.");

        var configuredRoot = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_IMPORT_EVIDENCE_ROOT");
        var evidenceRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "SharpInspect.CalibrationImportConsumer",
                "calibration-import-consumer-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(evidenceRoot);
        Directory.CreateDirectory(Path.Combine(evidenceRoot, "calibration-evidence"));
        Directory.CreateDirectory(Path.Combine(evidenceRoot, "calibration-artifacts"));
        var databasePath = Path.Combine(evidenceRoot, "calibration-import.sqlite");
        Assert.False(File.Exists(databasePath),
            "Calibration import consumer acceptance requires a fresh database.");

        var blocklist = PasswordBlocklist.Create("calibration-import-development", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CalibrationImport." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(evidenceRoot, "private-keys")
        };
        var authorization = AuthorizationPolicy.Development;
        var execution = new AlgorithmExecutionPolicy("Sample.DraftExecution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var releasePolicy = new RecipeGovernancePolicy("Sample.RecipeRelease.SingleApprover", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var alarmPolicy = PreviewAlarmPolicy();
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            AlarmPolicy = alarmPolicy,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization),
            RecipeDrafts = new RecipeDraftStoreOptions(execution),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            ImagingSetup = new ImagingSetupStoreOptions(),
            CalibrationSessions = new CalibrationSessionStoreOptions
            {
                EvidenceRoot = Path.Combine(evidenceRoot, "calibration-evidence")
            },
            CalibrationGovernance = new CalibrationGovernanceStoreOptions(),
            RecipeReleases = new RecipeReleaseStoreOptions(releasePolicy),
            PlcResultContracts = new PlcResultContractStoreOptions(),
            RecipeActivations = new RecipeActivationStoreOptions(),
            PreviewSessions = new PreviewSessionStoreOptions(),
            CalibrationImports = new CalibrationImportStoreOptions
            {
                Artifacts = new CalibrationTransferArtifactOptions(
                    Path.Combine(evidenceRoot, "calibration-artifacts"))
            }
        };

        var export = CalibrationExportPackageTests.ExportFixture.Create();
        var package = CalibrationExportPackageCodec.Encode(export.StationId, export.Evidence,
            export.Policy, export.Images, export.Profile);
        await File.WriteAllBytesAsync(Path.Combine(evidenceRoot, PackageFileName), package.GetBytes())
            .ConfigureAwait(true);

        var password = "A9!" + Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(24)) + "导入";
        Guid principal;
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization.ConfigureAwait(true);
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store).ConfigureAwait(true);
            var identity = new LocalIdentityService(store, options.LocalIdentity!,
                new FixtureConsole());
            var token = await identity.ProvisionBootstrapTokenAsync().ConfigureAwait(true);
            Assert.True(token.Succeeded, token.ReasonCode);
            Assert.NotNull(token.Token);
            var bootstrap = token.Token!.TakeForDisplay();
            token.Token!.Dispose();
            var created = await identity.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(audit.StationId, bootstrap, UserName,
                    "T34 public calibration import consumer 验收管理员", password))
                .ConfigureAwait(true);
            Assert.True(created.Succeeded, created.ReasonCode);
            Assert.NotNull(created.Identity);
            principal = created.Identity!.PrincipalId;
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store).ConfigureAwait(true);
        }

        var identityPolicyPath = Path.Combine(evidenceRoot, "identity-policy.json");
        await File.WriteAllTextAsync(identityPolicyPath, JsonSerializer.Serialize(new
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
        var alarmPolicyPath = Path.Combine(evidenceRoot, "alarm-policy.json");
        await File.WriteAllTextAsync(alarmPolicyPath, JsonSerializer.Serialize(new
        {
            Id = alarmPolicy.Id,
            Version = alarmPolicy.Version,
            Rules = alarmPolicy.Rules,
            SourceObservationFreshness = alarmPolicy.SourceObservationFreshness,
            MaximumActiveInstances = alarmPolicy.MaximumActiveInstances,
            MaximumPlcEntries = alarmPolicy.MaximumPlcEntries
        })).ConfigureAwait(true);

        return new ConsumerFixture(repository, consumer, evidenceRoot, options, audit,
            password, principal, identityPolicyPath, alarmPolicyPath,
            !string.IsNullOrWhiteSpace(configuredConsumer));
    }

    private static AlarmPolicy PreviewAlarmPolicy() => new("Sample.Preview", "development-v1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, ResetPrerequisites:
                    AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("PreviewRecoveryRequired", "Runtime.Preview",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, ResetPrerequisites:
                    AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoActiveExecution)
        }, TimeSpan.FromMinutes(1));

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "SharpInspect.NET.sln")))
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
            Path.Combine(repository.FullName, "samples", "SharpInspect.SampleHost", "bin",
                configuration, "net6.0-windows", "SharpInspect.SampleHost.dll"),
            Path.Combine(repository.FullName, "samples", "SharpInspect.SampleHost", "bin", "x64",
                configuration, "net6.0-windows", "SharpInspect.SampleHost.dll")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

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
            return (-1, "CalibrationImportConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) +
            await stderr.ConfigureAwait(true));
    }

    private static JsonDocument ReadJson(string path)
    {
        var file = new FileInfo(path);
        Assert.True(file.Exists && file.Length > 0,
            "Missing calibration import consumer evidence: " + path);
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
            Guid principal, string identityPolicyPath, string alarmPolicyPath,
            bool externalNuGet)
        {
            Repository = repository;
            Consumer = consumer;
            Directory = directory;
            Options = options;
            Audit = audit;
            Password = password;
            Principal = principal;
            IdentityPolicyPath = identityPolicyPath;
            AlarmPolicyPath = alarmPolicyPath;
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
        internal string AlarmPolicyPath { get; }
        internal bool ExternalNuGet { get; }

        internal IEnumerable<string> CommonArguments() => new[]
        {
            "--trace-db", Options.DatabasePath,
            "--audit-key", Audit.SigningKeyName,
            "--audit-key-directory", Audit.KeyDirectory,
            "--identity-policy", IdentityPolicyPath,
            "--alarm-policy", AlarmPolicyPath
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
