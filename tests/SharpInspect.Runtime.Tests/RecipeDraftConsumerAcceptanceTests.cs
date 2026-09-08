using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class RecipeDraftConsumerAcceptanceTests
{
    [Fact]
    public async Task V115_N01_RealWpfConsumerWritesDraftsAndIndependentProcessReopensOriginalSchema()
        => await RunCase(false);

    [Fact]
    public async Task V128_N01_RealWpfMigrationAndOrdinaryEditRetainSignedLineageAcrossIndependentRestart()
        => await RunCase(true);

    private static async Task RunCase(bool migration)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharpInspect.NET.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var consumer = Environment.GetEnvironmentVariable("SHARPINSPECT_DRAFT_CONSUMER") ?? Path.Combine(root!.FullName,
            "samples", "SharpInspect.SampleHost", "bin", configuration, "net6.0-windows", "SharpInspect.SampleHost.dll");
        Assert.True(File.Exists(consumer), "Build the solution before the Draft process acceptance test.");
        var directory = Environment.GetEnvironmentVariable("SHARPINSPECT_DRAFT_EVIDENCE_ROOT") ??
            Path.Combine(root!.FullName, "artifacts", "ticket15-process-smoke", Guid.NewGuid().ToString("N"));
        if (migration) directory = Path.Combine(directory, "migration");
        Directory.CreateDirectory(directory);
        var blocklist = PasswordBlocklist.Create("draft-process-development-fixture", "1", new[] { "passwordpassword" });
        var policy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1", "SharpInspect.DraftConsumer." + Guid.NewGuid().ToString("N"))
        { AllowInitialKeyCreation = true, CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys") };
        var execution = new AlgorithmExecutionPolicy("Sample.DraftExecution", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        var authorizationPolicy = RecipeDraftTestPolicies.Authoring;
        var options = new ProductionStoreOptions(Path.Combine(directory, "drafts.sqlite"))
        { AuditIntegrityPolicy = audit, RecipeDrafts = new RecipeDraftStoreOptions(execution),
            LocalIdentity = new LocalIdentityOptions(audit.StationId, policy, new Pbkdf2PasswordHasher(),
                AuthenticationPolicy.Development, authorizationPolicy) };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "工艺";
        try
        {
            Guid principal;
            await using (var store = new SqliteCommandStore(options))
            {
                var initialization = await store.Initialization;
                Assert.True(initialization.Committed, initialization.ReasonCode);
                await Verified(store);
                // Only bootstrap physical-console evidence is a fixture. The consumer uses the real
                // protected verifier, session, Permission, Draft API, writer and signed history.
                var identity = new LocalIdentityService(store, options.LocalIdentity, new FixtureConsole());
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                await Verified(store);
                var created = await identity.CreateFirstAdministratorAsync(new(audit.StationId,
                    token.Token!.TakeForDisplay(), "draft.author", "工艺作者", password));
                Assert.True(created.Succeeded, created.ReasonCode);
                principal = created.Identity!.PrincipalId;
                created.RecoveryKit!.Dispose();
                await Verified(store);
            }
            var policyPath = Path.Combine(directory, "identity-policy.json");
            await File.WriteAllTextAsync(policyPath, JsonSerializer.Serialize(new
            { PasswordPolicyVersion = policy.Version, BlocklistId = blocklist.Id, BlocklistVersion = blocklist.Version,
                BlocklistContentHash = blocklist.ContentHash, BlocklistValues = blocklist.Values,
                HashBaselineVersion = PasswordHashBaseline.DevelopmentVersion, WorkFactor = PasswordHashBaseline.SecurityFloorIterations,
                AuthenticationPolicy = AuthenticationPolicy.Development,
                AuthorizationPolicy = new { authorizationPolicy.Id, authorizationPolicy.Version,
                    authorizationPolicy.RoleBundles, authorizationPolicy.StepUpPermissions } }));
            var common = new[] { "--trace-db", options.DatabasePath, "--audit-key", audit.SigningKeyName,
                "--audit-key-directory", audit.KeyDirectory, "--identity-policy", policyPath };
            if (migration) common = common.Concat(new[] { "--configuration-migration" }).ToArray();
            var process = await Run(consumer, root!.FullName,
                new[] { "--recipe-draft-check", directory, "--user-name", "draft.author", "--expected-principal", principal.ToString("D") }
                    .Concat(common), password);
            Assert.DoesNotContain(password, process.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), process.Output);
            Assert.True(process.ExitCode == 0, "Draft WPF consumer failed; inspect process.log in " + directory);
            Assert.Contains(migration ? "V128-N01 draft-migration PASS revisions=3 sourceUnchanged=true lineageRetained=true activeUnchanged=true ready=false" :
                "V115-N01 draft-editor PASS revisions=2 exactSchema=true invalidRejected=true unauthorizedRejected=true activeUnchanged=true ready=false", process.Output);
            Assert.True(new FileInfo(Path.Combine(directory, migration ? "draft-migration-editor.png" : "draft-editor.png")).Length > 0);
            if (!migration) Assert.True(new FileInfo(Path.Combine(directory, "draft-editor-dependencies.png")).Length > 0);
            var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(options.DatabasePath)));
            var restarted = await Run(consumer, root.FullName,
                new[] { "--recipe-draft-query", directory }.Concat(common), null);
            Assert.DoesNotContain(password, restarted.Output);
            await File.WriteAllTextAsync(Path.Combine(directory, "restart.log"), restarted.Output);
            Assert.True(restarted.ExitCode == 0, "Draft restart consumer failed; inspect restart.log in " + directory);
            Assert.Contains(migration ? "V128-N02 draft-migration-restart PASS revisions=3 sourceUnchanged=true lineageRetained=true canRelease=false" :
                "V115-N02 draft-restart PASS revisions=2 originalSchema=true factoryRegistered=false canRelease=false", restarted.Output);
            Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(options.DatabasePath))));
            var query = new SqliteRecipeDraftQuery(options);
            var page = await query.QueryAsync(new(PageSize: 20));
            Assert.True(page.Available, page.ReasonCode);
            Assert.Equal(migration ? 3 : 2, page.Revisions.Count);
            Assert.All(page.Revisions, revision =>
            {
                Assert.Equal(principal, revision.AuthorPrincipalId);
                Assert.False(revision.Published); Assert.False(revision.Active); Assert.False(revision.CanRelease);
                Assert.Equal("NotRun", revision.DependencyValidation);
                Assert.Empty(revision.Content.Configuration.Validate(revision.Content.Algorithm.ConfigurationSchema));
            });
            if (migration)
            {
                Assert.Equal(page.Revisions[1].RevisionContentHash, page.Revisions[2].PreviousRevisionContentHash);
                Assert.Equal(page.Revisions[0].RevisionContentHash,
                    page.Revisions[1].Content.MigrationLineage!.Plan.Source.RevisionContentHash);
                Assert.Equal(page.Revisions[1].Content.MigrationLineage!.ContentHash,
                    page.Revisions[2].Content.MigrationLineage!.ContentHash);
            }
            else Assert.Equal(page.Revisions[0].RevisionContentHash, page.Revisions[1].PreviousRevisionContentHash);
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
            { Result = "Pass", Consumer = consumer,
                ConsumerSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer))),
                Revisions = migration ? 3 : 2, principal, IndependentRestart = true, DatabaseUnchangedByRestart = true,
                PhysicalBootstrapAuthority = "DevelopmentFixture", ProductionReady = false,
                DependencyValidation = "NotRun", Screenshot = migration ? "draft-migration-editor.png" : "draft-editor.png",
                DependenciesScreenshot = migration ? null : "draft-editor-dependencies.png" }));
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }

    private static async Task<(int ExitCode, string Output)> Run(string consumer, string workingDirectory,
        IEnumerable<string> arguments, string? password)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory };
        start.ArgumentList.Add(consumer);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (password is not null) await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password));
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }
    private static async Task Verified(SqliteCommandStore store)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, deadline.Token);
        }
    }
    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    { public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001"); }
}
