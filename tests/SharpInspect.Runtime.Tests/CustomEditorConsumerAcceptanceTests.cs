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
/// Process-boundary acceptance for the small trusted custom configuration editor
/// shipped by the sample host. The child is exercised through its public run and
/// read-only query modes; this test does not grant the editor production authority.
/// </summary>
public sealed class CustomEditorConsumerAcceptanceTests
{
    private const string UserName = "v129-custom-editor-admin";
    private const string RunOutput =
        "V129-N01 custom-editor PASS revisions=3 exactSchema=true invalidRejected=true semanticRejected=true fallback=true contextRevoked=true activeUnchanged=true ready=false";
    private const string RestartOutput =
        "V129-N02 custom-editor-restart PASS revisions=3 originalSchema=true factoryRegistered=false customEditorRegistered=false canRelease=false";

    [Fact]
    public async Task V129_N01_RealCustomEditorConsumerWritesAndReopensThreeDraftRevisions()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable("SHARPINSPECT_DRAFT_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the sample host before running the custom editor process acceptance test.");

        var configuredEvidence = Environment.GetEnvironmentVariable("SHARPINSPECT_DRAFT_EVIDENCE_ROOT");
        var directory = ResolveEvidenceDirectory(configuredEvidence);
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "drafts.sqlite");
        Assert.False(File.Exists(databasePath), "Custom editor acceptance requires a fresh evidence directory.");

        var blocklist = PasswordBlocklist.Create("draft-custom-editor-blocklist", "1",
            new[] { "passwordpassword" });
        var passwordPolicy = new LocalPasswordPolicy { Blocklist = blocklist };
        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.DraftCustomEditor." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var authorization = RecipeDraftTestPolicies.Authoring;
        var options = new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy(
                "Sample.DraftExecution", "1", TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1))),
            LocalIdentity = new LocalIdentityOptions(audit.StationId, passwordPolicy,
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authorization)
        };
        var password = "A8!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) + "工艺";
        try
        {
            var principal = await BootstrapAsync(options, password).ConfigureAwait(true);
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
            })).ConfigureAwait(true);

            var common = new[]
            {
                "--trace-db", options.DatabasePath,
                "--audit-key", audit.SigningKeyName,
                "--audit-key-directory", audit.KeyDirectory,
                "--identity-policy", policyPath
            };
            var run = await RunProcessAsync(consumer, repository.FullName,
                new[]
                {
                    "--recipe-draft-check", directory,
                    "--user-name", UserName,
                    "--expected-principal", principal.ToString("D"),
                    "--custom-editor"
                }.Concat(common), password).ConfigureAwait(true);
            Assert.DoesNotContain(password, run.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "process.log"), run.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(RunOutput, run.Output, StringComparison.Ordinal);

            var evidencePath = Path.Combine(directory, "custom-editor-evidence.json");
            Assert.True(new FileInfo(evidencePath).Length > 0);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath)
                .ConfigureAwait(true));
            var evidenceRoot = evidence.RootElement;
            Assert.Equal("Pass", evidenceRoot.GetProperty("result").GetString());
            Assert.Equal(9, evidenceRoot.GetProperty("schema").GetInt32());
            Assert.Equal(3, evidenceRoot.GetProperty("revisions").GetInt32());
            Assert.False(evidenceRoot.GetProperty("published").GetBoolean());
            Assert.False(evidenceRoot.GetProperty("active").GetBoolean());
            Assert.False(evidenceRoot.GetProperty("canRelease").GetBoolean());
            Assert.Equal(principal, evidenceRoot.GetProperty("authorPrincipalId").GetGuid());
            Assert.Equal(3, evidenceRoot.GetProperty("authors").GetArrayLength());
            Assert.All(evidenceRoot.GetProperty("authors").EnumerateArray(), author =>
                Assert.Equal(principal, author.GetGuid()));
            Assert.True(evidenceRoot.GetProperty("invalidSchemaRejected").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("semanticRejected").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("contextRevoked").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("factoryInitializationFallback").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("explicitReselectAfterRefresh").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("unrelatedFieldsPreserved").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("activeUnchanged").GetBoolean());
            Assert.True(evidenceRoot.GetProperty("readyUnchanged").GetBoolean());
            Assert.False(evidenceRoot.GetProperty("productionReady").GetBoolean());
            Assert.Equal("NotRun", evidenceRoot.GetProperty("dependencyValidation").GetString());
            Assert.False(evidenceRoot.GetProperty("migratorRegistered").GetBoolean());
            Assert.Equal(3, evidenceRoot.GetProperty("revisionHashes").GetArrayLength());
            Assert.Equal(3, evidenceRoot.GetProperty("configurationHashes").GetArrayLength());
            Assert.Equal("Sample.DraftCustomEditor",
                evidenceRoot.GetProperty("editorContract").GetProperty("id").GetString());
            Assert.True(new FileInfo(Path.Combine(directory, "custom-editor.png")).Length > 0);

            var beforeRestart = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath)));
            var restart = await RunProcessAsync(consumer, repository.FullName,
                new[] { "--recipe-draft-query", directory, "--custom-editor" }.Concat(common), null)
                .ConfigureAwait(true);
            Assert.DoesNotContain(password, restart.Output, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "restart.log"), restart.Output)
                .ConfigureAwait(true);
            Assert.Equal(0, restart.ExitCode);
            Assert.Contains(RestartOutput, restart.Output, StringComparison.Ordinal);
            Assert.Equal(beforeRestart,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath))));

            var restartPath = Path.Combine(directory, "custom-editor-restart.json");
            Assert.True(new FileInfo(restartPath).Length > 0);
            using var restartEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(restartPath)
                .ConfigureAwait(true));
            var restartRoot = restartEvidence.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("result").GetString());
            Assert.Equal(9, restartRoot.GetProperty("schema").GetInt32());
            Assert.Equal(3, restartRoot.GetProperty("revisions").GetInt32());
            Assert.True(restartRoot.GetProperty("originalSchema").GetBoolean());
            Assert.False(restartRoot.GetProperty("factoryRegistered").GetBoolean());
            Assert.False(restartRoot.GetProperty("customEditorRegistered").GetBoolean());
            Assert.False(restartRoot.GetProperty("migratorRegistered").GetBoolean());
            Assert.True(restartRoot.GetProperty("readOnlyQuery").GetBoolean());
            Assert.False(restartRoot.GetProperty("canRelease").GetBoolean());
            Assert.False(restartRoot.GetProperty("active").GetBoolean());
            Assert.False(restartRoot.GetProperty("published").GetBoolean());
            Assert.False(restartRoot.GetProperty("productionReady").GetBoolean());

            var query = new SqliteRecipeDraftQuery(options);
            var page = await query.QueryAsync(new(PageSize: 20)).ConfigureAwait(true);
            Assert.True(page.Available, page.ReasonCode);
            Assert.Null(page.NextAfterPosition);
            var revisions = page.Revisions.OrderBy(item => item.Revision).ToArray();
            Assert.Equal(3, revisions.Length);
            Assert.Equal(new[] { 1L, 2L, 3L }, revisions.Select(item => item.Revision));
            var actualRevisionHashes = revisions.Select(revision => revision.RevisionContentHash).ToArray();
            var actualConfigurationHashes = revisions.Select(revision => revision.Content.Configuration.ContentHash).ToArray();
            foreach (var recorded in new[] { evidenceRoot, restartRoot })
            {
                Assert.Equal(actualRevisionHashes, recorded.GetProperty("revisionHashes").EnumerateArray().Select(value => value.GetString()));
                Assert.Equal(actualConfigurationHashes, recorded.GetProperty("configurationHashes").EnumerateArray().Select(value => value.GetString()));
            }
            Assert.All(revisions, revision =>
            {
                Assert.Equal(principal, revision.AuthorPrincipalId);
                Assert.False(revision.Published);
                Assert.False(revision.Active);
                Assert.False(revision.CanRelease);
                Assert.Equal("NotRun", revision.DependencyValidation);
                Assert.Empty(revision.Content.Configuration.Validate(
                    revision.Content.Algorithm.ConfigurationSchema));
            });
            Assert.Equal(20, Count(revisions[0]));
            Assert.Equal(30, Count(revisions[1]));
            Assert.Equal(30, Count(revisions[2]));
            Assert.Equal("通用回退保存", Label(revisions[2]));
            Assert.Equal(revisions[0].RevisionContentHash, revisions[1].PreviousRevisionContentHash);
            Assert.Equal(revisions[1].RevisionContentHash, revisions[2].PreviousRevisionContentHash);
            Assert.Equal(revisions[0].Content.Algorithm.ConfigurationSchema.ContentHash,
                revisions[2].Content.Algorithm.ConfigurationSchema.ContentHash);
            foreach (var revision in revisions)
            {
                foreach (var entry in revision.Content.Configuration.Values)
                {
                    var field = revision.Content.Algorithm.ConfigurationSchema.Fields
                        .Single(item => item.Key == entry.Key);
                    Assert.Equal(field.Type, entry.Value.Type);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
            {
                Result = "Pass", Consumer = consumer,
                ConsumerSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer))),
                ExternalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                Revisions = revisions.Length, AuthorPrincipalId = principal,
                IndependentRestart = true, DatabaseUnchangedByRestart = true,
                Published = revisions.Any(revision => revision.Published),
                Active = revisions.Any(revision => revision.Active),
                CanRelease = revisions.Any(revision => revision.CanRelease),
                Production = "NotRun", StationAcceptance = "NotRun"
            })).ConfigureAwait(true);
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory)) Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    private static long Count(RecipeDraftRevision revision) => revision.Content.Configuration.Values
        .Single(item => item.Key == "count").Value.AsInt64();

    private static string Label(RecipeDraftRevision revision) => revision.Content.Configuration.Values
        .Single(item => item.Key == "label").Value.AsString();

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

    private static string ResolveEvidenceDirectory(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            return string.Equals(new DirectoryInfo(full).Name, "custom-editor",
                StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, "custom-editor");
        }
        return Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket29-process",
            Guid.NewGuid().ToString("N"));
    }

    private static async Task<Guid> BootstrapAsync(ProductionStoreOptions options, string password)
    {
        await using var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization.ConfigureAwait(true);
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await WaitForVerifiedAsync(store).ConfigureAwait(true);
        var identity = new LocalIdentityService(store, options.LocalIdentity!, new FixtureConsole());
        var token = await identity.ProvisionBootstrapTokenAsync().ConfigureAwait(true);
        Assert.True(token.Succeeded, token.ReasonCode);
        Assert.NotNull(token.Token);
        var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
            options.AuditIntegrityPolicy!.StationId, token.Token!.TakeForDisplay(), UserName,
            "T29 自定义编辑器验收管理员", password)).ConfigureAwait(true);
        Assert.True(created.Succeeded, created.ReasonCode);
        Assert.NotNull(created.Identity);
        var principal = created.Identity!.PrincipalId;
        created.RecoveryKit?.Dispose();
        await WaitForVerifiedAsync(store).ConfigureAwait(true);
        return principal;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string consumer,
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
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password)).ConfigureAwait(true);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            return (-1, "CustomEditorConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
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

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
