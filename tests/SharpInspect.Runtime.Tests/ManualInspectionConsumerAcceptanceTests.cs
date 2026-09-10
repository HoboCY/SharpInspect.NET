using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ManualInspectionConsumerAcceptanceTests
{
    private const string UserName = "v135-manual-admin";

    [Fact]
    public async Task V135_N01_PublicConsumerRunsSavedDraftThroughWpfAndReadsIndependentHistory()
    {
        await using var fixture = await CreateFixtureAsync();
        var run = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
            new[] { "--manual-inspection-check", fixture.Directory, "--user-name", UserName,
                "--expected-principal", fixture.Principal.ToString("D") }.Concat(fixture.CommonArguments()), fixture.Password);
        Assert.DoesNotContain(fixture.Password, run.Output, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "process.log"), run.Output);
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("V135_N01 manual-inspection PASS", run.Output, StringComparison.Ordinal);
        using var evidence = ReadJson(Path.Combine(fixture.Directory, "manual-inspection-evidence.json"));
        var root = evidence.RootElement;
        Assert.Equal("Pass", root.GetProperty("Result").GetString());
        Assert.Equal("V135_N01", root.GetProperty("CaseId").GetString());
        Assert.Equal(fixture.Principal, root.GetProperty("PrincipalId").GetGuid());
        Assert.Equal(HashFile(fixture.Consumer), root.GetProperty("ConsumerAssemblySha256").GetString());
        Assert.Equal("Draft", root.GetProperty("Source").GetProperty("Kind").GetString());
        Assert.Equal("Accepted", root.GetProperty("StartDisposition").GetString());
        Assert.Equal("Accepted", root.GetProperty("ExitDisposition").GetString());
        Assert.Equal("NoActiveBaselineClosed", root.GetProperty("Restoration").GetString());
        Assert.False(root.GetProperty("Ready").GetBoolean());
        Assert.False(root.GetProperty("Active").GetBoolean());
        Assert.False(root.GetProperty("CameraOpenAfterExit").GetBoolean());
        Assert.Equal(3, root.GetProperty("HistoryRunCount").GetInt32());
        Assert.Equal(4, root.GetProperty("CameraFramesProduced").GetInt64());
        var edges = root.GetProperty("EdgeCases");
        Assert.Equal("ManualInspectionRunInProgress", edges.GetProperty("BusyReason").GetString());
        Assert.Equal("ManualInspectionSessionInProgress", edges.GetProperty("PreviewConflictReason").GetString());
        var failureRuns = edges.GetProperty("Runs").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Timeout", "Error" }, failureRuns.Select(row => row.GetProperty("ExecutionStatus").GetString()));
        Assert.All(failureRuns, row =>
        {
            Assert.Equal("NoActiveBaselineClosed", row.GetProperty("Restoration").GetString());
            Assert.Equal(64, row.GetProperty("ContentHash").GetString()!.Length);
        });
        var timeoutFailure = failureRuns
            .Single(row => row.GetProperty("ExecutionStatus").GetString() == "Timeout");
        Assert.Equal(64, timeoutFailure.GetProperty("ResultSchemaContentHash").GetString()!.Length);
        var rows = root.GetProperty("Runs").EnumerateArray().ToArray();
        Assert.Equal(root.GetProperty("AcceptedRunCorrelations").EnumerateArray().Select(value => value.GetGuid()),
            rows.Select(row => row.GetProperty("CommandCorrelationId").GetGuid()));
        Assert.Equal(3, failureRuns.Select(row => row.GetProperty("SessionId").GetGuid())
            .Append(root.GetProperty("SessionId").GetGuid()).Distinct().Count());
        Assert.Equal(5, failureRuns.Concat(rows).Select(row => row.GetProperty("RunId").GetGuid()).Distinct().Count());
        Assert.Equal(5, failureRuns.Concat(rows).Select(row => row.GetProperty("CommandCorrelationId").GetGuid()).Distinct().Count());
        Assert.Equal(new[] { "Pass", "Fail", "Unknown" }, rows.Select(row => row.GetProperty("Decision").GetString()));
        Assert.Equal(3, rows.Select(row => row.GetProperty("RunId").GetGuid()).Distinct().Count());
        Assert.Single(rows.Select(row => row.GetProperty("PreparedInstanceId").GetGuid()).Distinct());
        Assert.All(rows, row =>
        {
            Assert.Equal("Success", row.GetProperty("ExecutionStatus").GetString());
            Assert.Equal(64, row.GetProperty("ResultContentHash").GetString()!.Length);
            Assert.Equal(64, row.GetProperty("FrameOverlayContentHash").GetString()!.Length);
        });
        var successResultSchemaHash = rows
            .Select(row => row.GetProperty("ResultSchemaContentHash").GetString())
            .Distinct(StringComparer.Ordinal).Single();
        Assert.Equal(successResultSchemaHash, timeoutFailure.GetProperty("ResultSchemaContentHash").GetString());
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "SELECT count(*) FROM recipe_release_events";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT count(*) FROM recipe_activation_events";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        Assert.DoesNotContain(tables, name => name.Contains("outbox", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, name => name.Contains("algorithm_result", StringComparison.OrdinalIgnoreCase));
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "acceptance.json"), JsonSerializer.Serialize(new
        {
            Result = "Pass", ValidationId = "V135_N01", ExternalNuGetConsumer = fixture.ExternalNuGet,
            ConsumerSha256 = HashFile(fixture.Consumer), ManualRuns = rows.Length + failureRuns.Length,
            BusyRejected = true, PreviewCompetitionRejected = true, TimeoutAndAcquisitionFailure = true,
            SourceKind = "Draft", ReleaseEvents = 0, ActivationEvents = 0,
            SqliteTables = tables, PlcIngressImplemented = false, ProductionQualification = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task<ConsumerFixture> CreateFixtureAsync()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_MANUAL_INSPECTION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the SampleHost ManualInspection NuGet consumer before acceptance.");

        var configuredRoot = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_MANUAL_INSPECTION_EVIDENCE_ROOT");
        var evidenceRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "SharpInspect.ManualInspectionConsumer",
                "manual-inspection-consumer-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredRoot);
        Directory.CreateDirectory(evidenceRoot);
        var databasePath = Path.Combine(evidenceRoot, "manual-inspection.sqlite");
        Assert.False(File.Exists(databasePath),
            "ManualInspection consumer acceptance requires a fresh database.");

        var blocklist = PasswordBlocklist.Create("manual-inspection-development", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.ManualInspection." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(evidenceRoot, "private-keys")
        };
        var authorization = new AuthorizationPolicy(
            "manual-inspection-development", "manual-inspection-development-2026-09-v1",
            AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
                pair => pair.Key == HumanRoleBundle.Administrator
                    ? pair.Value.Concat(new[] { Permission.EditRecipeDraft, Permission.RunPreview, Permission.RunManualInspection })
                    : pair.Value.AsEnumerable()),
            AuthorizationPolicy.Development.StepUpPermissions);
        var execution = new AlgorithmExecutionPolicy("Sample.DraftExecution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var releasePolicy = new RecipeGovernancePolicy("Sample.RecipeRelease.SingleApprover", "1",
            RecipeGovernanceMode.SingleApproverRelease);
        var alarmPolicy = ManualInspectionAlarmPolicy();
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            AlarmPolicy = alarmPolicy,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization),
            RecipeDrafts = new RecipeDraftStoreOptions(execution),
            CameraSetup = new CameraSetupStoreOptions(),
            RecipeReleases = new RecipeReleaseStoreOptions(releasePolicy),
            PlcResultContracts = new PlcResultContractStoreOptions(),
            RecipeActivations = new RecipeActivationStoreOptions(),
            PreviewSessions = new PreviewSessionStoreOptions(),
            ManualInspections = new ManualInspectionStoreOptions()
        };

        var password = "A8!" + Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(24)) + "手动";
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
            var created = await identity.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(audit.StationId,
                    token.Token!.TakeForDisplay(), UserName,
                    "T35 public ManualInspection consumer 验收管理员", password))
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

    private static AlarmPolicy ManualInspectionAlarmPolicy() => new("Sample.ManualInspection", "development-v1",
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
                    AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution),
            new AlarmPolicyRule("AlgorithmHung", "Runtime.AlgorithmExecution",
                AlarmSeverity.Critical, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 110),
            new AlarmPolicyRule("FrameBufferExhausted", "Runtime.FrameBufferPool",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 200,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution),
            new AlarmPolicyRule("ManualInspectionRecoveryRequired", "Runtime.ManualInspection",
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
            return (-1, "ManualInspectionConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) +
            await stderr.ConfigureAwait(true));
    }

    private static JsonDocument ReadJson(string path)
    {
        var file = new FileInfo(path);
        Assert.True(file.Exists && file.Length > 0,
            "Missing ManualInspection consumer evidence: " + path);
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
