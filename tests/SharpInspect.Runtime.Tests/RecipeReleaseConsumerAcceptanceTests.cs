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
/// Process-boundary acceptance for the public Recipe Release consumer shipped
/// by SampleHost.  The positive case uses the real WPF VM, Runtime command,
/// Step-Up and schema-16 ledger; the query process is read-only.
/// </summary>
public sealed class RecipeReleaseConsumerAcceptanceTests
{
    private const string UserName = "v130-release-author";
    private const string SingleOutput =
        "V130-C01 recipe-release PASS released=true available=true activeUnchanged=true ready=false executionCount=1";
    private const string MakerOutput =
        "V130-C02 recipe-release-maker PASS released=false reason=RecipeReleaseMakerCheckerConflict activeUnchanged=true ready=false executionCount=1";
    private const string ConcurrentOutput =
        "V130-C03 recipe-release-concurrent PASS released=false reason=RecipeReleaseDraftRevisionConflict activeUnchanged=true ready=false executionCount=1";
    private const string DependencyOutput =
        "V130-C04 recipe-release-dependency PASS released=false reason=RecipeReleaseAssetAuthorityUnavailable activeUnchanged=true ready=false executionCount=1";

    [Theory]
    [InlineData("")]
    [InlineData("unsupported-release-policy")]
    public async Task V130_C05_ReleaseConsumerRejectsMissingOrUnknownExplicitMode(string mode)
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable("SHARPINSPECT_RELEASE_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build SampleHost before running the Recipe Release mode validation test.");
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts",
            "ticket30-release-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var result = await RunProcessAsync(consumer, repository.FullName,
                new[]
                {
                    "--trace-db", Path.Combine(directory, "unused.sqlite"),
                    "--recipe-release-check", directory,
                    "--recipe-release-mode", mode,
                    "--recipe-release-scenario", "single"
                }, null);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(string.IsNullOrEmpty(mode)
                ? "RecipeReleaseGovernanceModeRequired" : "RecipeReleaseGovernanceModeInvalid",
                result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("recipe-release PASS", result.Output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "unused.sqlite")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task V130_C01_RealSingleApproverConsumerCreatesAvailableAndColdReadsSameHash()
    {
        await using var fixture = await CreateFixtureAsync("single", RecipeGovernanceMode.SingleApproverRelease);
        var run = await RunConsumerAsync(fixture, "single");
        AssertRun(run, SingleOutput);
        using var evidence = ReadJson(Path.Combine(fixture.Directory, "release-evidence.json"));
        Assert.Equal("Pass", evidence.RootElement.GetProperty("result").GetString());
        Assert.Equal("V130-C01", evidence.RootElement.GetProperty("caseId").GetString());
        Assert.True(evidence.RootElement.GetProperty("released").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("available").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("published").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("productionReady").GetBoolean());
        Assert.Equal("SingleApproverRelease", evidence.RootElement.GetProperty("policyMode").GetString());
        Assert.True(evidence.RootElement.GetProperty("activeRecipeUnchanged").GetBoolean());
        Assert.True(evidence.RootElement.GetProperty("armStateUnchanged").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("readyAfter").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(1, evidence.RootElement.GetProperty("executionCount").GetInt32());
        Assert.True(new FileInfo(Path.Combine(fixture.Directory, "release-editor.png")).Length > 0);

        await AssertColdQueryAsync(fixture, "single", expectedReleaseCount: 1);
        var queryEvidence = ReadJson(Path.Combine(fixture.Directory, "release-restart.json"));
        Assert.True(queryEvidence.RootElement.GetProperty("readOnlyQuery").GetBoolean());
        Assert.True(queryEvidence.RootElement.GetProperty("databaseUnchanged").GetBoolean());
        await WriteSummaryAsync(fixture, "V130-C01", 1);
        using var summary = ReadJson(Path.Combine(fixture.Directory, "evidence.json"));
        Assert.Equal("Pass", summary.RootElement.GetProperty("Result").GetString());
        Assert.Equal(HashFile(fixture.Consumer), summary.RootElement.GetProperty("ConsumerSha256").GetString());
        Assert.True(summary.RootElement.GetProperty("IndependentRestart").GetBoolean());
        Assert.True(summary.RootElement.GetProperty("DatabaseUnchangedByRestart").GetBoolean());
        Assert.True(summary.RootElement.GetProperty("ReleasedVersions").GetInt32() >= 1);
        Assert.True(summary.RootElement.GetProperty("Available").GetBoolean());
        Assert.False(summary.RootElement.GetProperty("Active").GetBoolean());
        Assert.False(summary.RootElement.GetProperty("Armed").GetBoolean());
    }

    [Fact]
    public async Task V130_C02_RealMakerCheckerConsumerRejectsSameAuthorInNewSession()
    {
        await using var fixture = await CreateFixtureAsync("maker-checker", RecipeGovernanceMode.MakerCheckerRelease);
        var run = await RunConsumerAsync(fixture, "maker-checker");
        AssertRun(run, MakerOutput);
        using var evidence = ReadJson(Path.Combine(fixture.Directory, "release-evidence.json"));
        Assert.Equal("V130-C02", evidence.RootElement.GetProperty("caseId").GetString());
        Assert.False(evidence.RootElement.GetProperty("released").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("MakerCheckerRelease", evidence.RootElement.GetProperty("policyMode").GetString());
        Assert.Equal("RecipeReleaseMakerCheckerConflict",
            evidence.RootElement.GetProperty("expectedRejection").GetString());
        Assert.True(evidence.RootElement.GetProperty("independentSession").GetBoolean());
        Assert.NotEqual(evidence.RootElement.GetProperty("authorSessionId").GetString(),
            evidence.RootElement.GetProperty("releaseSessionId").GetString());
        Assert.Equal(JsonValueKind.Null,
            evidence.RootElement.GetProperty("approverPrincipalId").ValueKind);
        Assert.False(evidence.RootElement.GetProperty("readyAfter").GetBoolean());
        await AssertColdQueryAsync(fixture, "maker-checker", expectedReleaseCount: 0);
        await WriteSummaryAsync(fixture, "V130-C02", 0);
    }

    [Fact]
    public async Task V130_C03_RealConsumerConcurrentDraftChangeIsRejectedWithoutRelease()
    {
        await using var fixture = await CreateFixtureAsync("concurrent", RecipeGovernanceMode.SingleApproverRelease);
        var run = await RunConsumerAsync(fixture, "concurrent");
        AssertRun(run, ConcurrentOutput);
        using var evidence = ReadJson(Path.Combine(fixture.Directory, "release-evidence.json"));
        Assert.Equal("RecipeReleaseDraftRevisionConflict",
            evidence.RootElement.GetProperty("expectedRejection").GetString());
        Assert.False(evidence.RootElement.GetProperty("released").GetBoolean());
        Assert.Equal(0, evidence.RootElement.GetProperty("releaseCount").GetInt32());
        await AssertColdQueryAsync(fixture, "concurrent", expectedReleaseCount: 0);

        var drafts = new SqliteRecipeDraftQuery(fixture.Options);
        var page = await drafts.QueryAsync(new(PageSize: 20));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(2, page.Revisions.Count);
        await WriteSummaryAsync(fixture, "V130-C03", 0);
    }

    [Fact]
    public async Task V130_C04_RealConsumerMissingDependencyIsRejectedWithoutRelease()
    {
        await using var fixture = await CreateFixtureAsync("dependency", RecipeGovernanceMode.SingleApproverRelease);
        var run = await RunConsumerAsync(fixture, "dependency");
        AssertRun(run, DependencyOutput);
        using var evidence = ReadJson(Path.Combine(fixture.Directory, "release-evidence.json"));
        Assert.Equal("RecipeReleaseAssetAuthorityUnavailable",
            evidence.RootElement.GetProperty("expectedRejection").GetString());
        Assert.False(evidence.RootElement.GetProperty("released").GetBoolean());
        Assert.Equal(0, evidence.RootElement.GetProperty("releaseCount").GetInt32());
        await AssertColdQueryAsync(fixture, "dependency", expectedReleaseCount: 0);
        await WriteSummaryAsync(fixture, "V130-C04", 0);
    }

    private static async Task<ReleaseFixture> CreateFixtureAsync(string caseName,
        RecipeGovernanceMode mode)
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable("SHARPINSPECT_RELEASE_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build SampleHost before running the Recipe Release process acceptance test.");
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPINSPECT_RELEASE_EVIDENCE_ROOT");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket30-release",
                Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(configuredRoot);
        var directory = Path.Combine(root, caseName);
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "recipe-releases.sqlite");
        Assert.False(File.Exists(databasePath), "Recipe Release acceptance requires a fresh evidence directory.");

        var blocklist = PasswordBlocklist.Create("recipe-release-development", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.RecipeRelease." + caseName + "." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = RecipeDraftTestPolicies.Authoring;
        var execution = new AlgorithmExecutionPolicy("Sample.DraftExecution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var governance = new RecipeGovernancePolicy(
            mode == RecipeGovernanceMode.MakerCheckerRelease
                ? "Sample.RecipeRelease.MakerChecker" : "Sample.RecipeRelease.SingleApprover", "1", mode);
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            RecipeDrafts = new RecipeDraftStoreOptions(execution),
            RecipeReleases = new RecipeReleaseStoreOptions(governance),
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization)
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "发布";
        Guid principal;
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(store);
            var identity = new LocalIdentityService(store, options.LocalIdentity!, new FixtureConsole());
            var token = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(token.Succeeded, token.ReasonCode);
            var created = await identity.CreateFirstAdministratorAsync(new(
                audit.StationId, token.Token!.TakeForDisplay(), UserName, "T30 配方发布作者", password));
            Assert.True(created.Succeeded, created.ReasonCode);
            principal = created.Identity!.PrincipalId;
            created.RecoveryKit?.Dispose();
            await WaitForVerifiedAsync(store);
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
        }));
        return new ReleaseFixture(repository, consumer, directory, options, audit, password, principal,
            policyPath, !string.IsNullOrWhiteSpace(configuredConsumer));
    }

    private static async Task AssertColdQueryAsync(ReleaseFixture fixture, string scenario,
        int expectedReleaseCount)
    {
        var before = HashFile(fixture.Options.DatabasePath);
        var query = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
            new[]
            {
                "--recipe-release-query", fixture.Directory,
                "--recipe-release-mode", fixture.Options.RecipeReleases!.Policy.Mode == RecipeGovernanceMode.MakerCheckerRelease
                    ? "maker-checker" : "single",
                "--recipe-release-scenario", scenario
            }.Concat(fixture.CommonArguments()), null);
        Assert.DoesNotContain(fixture.Password, query.Output, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "restart.log"), query.Output);
        Assert.Equal(0, query.ExitCode);
        Assert.Contains($"{CaseId(scenario)}-Q recipe-release-query PASS readOnly=true databaseUnchanged=true",
            query.Output, StringComparison.Ordinal);
        Assert.Equal(before, HashFile(fixture.Options.DatabasePath));
        using var restart = ReadJson(Path.Combine(fixture.Directory, "release-restart.json"));
        Assert.Equal(expectedReleaseCount, restart.RootElement.GetProperty("releaseCount").GetInt32());
        Assert.True(restart.RootElement.GetProperty("databaseUnchanged").GetBoolean());
    }

    private static async Task WriteSummaryAsync(ReleaseFixture fixture, string caseId,
        int releaseCount)
    {
        using var evidence = ReadJson(Path.Combine(fixture.Directory, "release-evidence.json"));
        var consumerHash = HashFile(fixture.Consumer);
        var observedReleaseCount = evidence.RootElement.GetProperty("releaseCount").GetInt32();
        Assert.Equal(releaseCount, observedReleaseCount);
        Assert.Equal(observedReleaseCount > 0, evidence.RootElement.GetProperty("published").GetBoolean());
        Assert.False(evidence.RootElement.GetProperty("productionReady").GetBoolean());
        Assert.Equal(1, evidence.RootElement.GetProperty("executionCount").GetInt32());
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "evidence.json"),
            JsonSerializer.Serialize(new
            {
                Result = "Pass",
                CaseId = caseId,
                Consumer = fixture.Consumer,
                ExternalNuGetConsumer = fixture.ExternalNuGet,
                ConsumerSha256 = consumerHash,
                IndependentRestart = true,
                DatabaseUnchangedByRestart = true,
                ReleasedVersions = observedReleaseCount,
                Available = evidence.RootElement.GetProperty("available").GetBoolean(),
                Active = evidence.RootElement.GetProperty("active").GetBoolean(),
                Armed = evidence.RootElement.GetProperty("armed").GetBoolean(),
                Published = evidence.RootElement.GetProperty("published").GetBoolean(),
                ProductionReady = evidence.RootElement.GetProperty("productionReady").GetBoolean(),
                ReleaseRecordContentHash = evidence.RootElement.GetProperty("recordContentHash").GetString(),
                RecipeContentHash = evidence.RootElement.GetProperty("recipeContentHash").GetString(),
                RevisionContentHash = evidence.RootElement.GetProperty("revisionContentHash").GetString(),
                ReleaseEvidence = "release-evidence.json",
                RestartEvidence = "release-restart.json",
                Screenshot = "release-editor.png",
                ProcessLog = "process.log",
                RestartLog = "restart.log",
                Scenario = evidence.RootElement.GetProperty("scenario").GetString()
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<ProcessResult> RunConsumerAsync(ReleaseFixture fixture, string scenario)
    {
        var mode = fixture.Options.RecipeReleases!.Policy.Mode == RecipeGovernanceMode.MakerCheckerRelease
            ? "maker-checker" : "single";
        var process = await RunProcessAsync(fixture.Consumer, fixture.Repository.FullName,
            new[]
            {
                "--recipe-release-check", fixture.Directory,
                "--user-name", UserName,
                "--expected-principal", fixture.Principal.ToString("D"),
                "--recipe-release-mode", mode,
                "--recipe-release-scenario", scenario
            }.Concat(fixture.CommonArguments()), fixture.Password);
        Assert.DoesNotContain(fixture.Password, process.Output, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "process.log"), process.Output);
        return process;
    }

    private static void AssertRun(ProcessResult result, string expectedOutput)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expectedOutput, result.Output, StringComparison.Ordinal);
    }

    private static JsonDocument ReadJson(string path)
    {
        Assert.True(new FileInfo(path).Length > 0, "Missing consumer artifact: " + path);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string CaseId(string scenario) => scenario switch
    {
        "single" => "V130-C01",
        "maker-checker" => "V130-C02",
        "concurrent" => "V130-C03",
        "dependency" => "V130-C04",
        _ => "V130-C00"
    };

    private static string HashFile(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)));

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

    private static async Task<ProcessResult> RunProcessAsync(string consumer,
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
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return new(-1, "RecipeReleaseConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout + await stderr);
        }
        return new(process.ExitCode, await stdout + await stderr);
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

    private sealed class ReleaseFixture : IAsyncDisposable
    {
        internal ReleaseFixture(DirectoryInfo repository, string consumer, string directory,
            ProductionStoreOptions options, AuditIntegrityPolicy audit, string password,
            Guid principal, string policyPath, bool externalNuGet)
        {
            Repository = repository; Consumer = consumer; Directory = directory; Options = options;
            Audit = audit; Password = password; Principal = principal; PolicyPath = policyPath;
            ExternalNuGet = externalNuGet;
        }

        internal DirectoryInfo Repository { get; }
        internal string Consumer { get; }
        internal string Directory { get; }
        internal ProductionStoreOptions Options { get; }
        internal AuditIntegrityPolicy Audit { get; }
        internal string Password { get; }
        internal Guid Principal { get; }
        internal string PolicyPath { get; }
        internal bool ExternalNuGet { get; }

        internal IEnumerable<string> CommonArguments() => new[]
        {
            "--trace-db", Options.DatabasePath,
            "--audit-key", Audit.SigningKeyName,
            "--audit-key-directory", Audit.KeyDirectory,
            "--identity-policy", PolicyPath
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

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
