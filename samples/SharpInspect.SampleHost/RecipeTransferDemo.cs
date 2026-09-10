using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

internal static class RecipeTransferDemo
{
    internal static int Run(string directory, bool queryOnly)
    {
        try
        {
            RunAsync(Path.GetFullPath(directory), queryOnly).GetAwaiter().GetResult();
            Console.WriteLine(queryOnly ? "V138_N02 recipe-transfer-query PASS readOnly=true databaseUnchanged=true" :
                "V138_N01 recipe-transfer PASS anonymousRejected=true importedDrafts=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"RecipeTransferConsumerCheckFailed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunAsync(string directory, bool queryOnly)
    {
        Directory.CreateDirectory(directory);
        var options = Options(directory);
        if (queryOnly)
        {
            Require(File.Exists(options.DatabasePath), "RecipeTransferConsumerDatabaseMissing");
            var before = HashFile(options.DatabasePath);
            var query = new SqliteRecipeTransferQuery(options);
            var trust = await query.ReadTrustAsync().ConfigureAwait(false);
            var keys = await query.ReadSigningKeysAsync().ConfigureAwait(false);
            var history = await query.QueryAsync(new()).ConfigureAwait(false);
            Require(trust.Available && trust.Trust is null && keys.Available && keys.Keys.Count == 0 &&
                history.Available && history.Records.Count == 0, "RecipeTransferConsumerUnexpectedHistory");
            Require(before == HashFile(options.DatabasePath), "RecipeTransferConsumerReadChangedDatabase");
            await WriteAsync(directory, "recipe-transfer-restart.json", new
            { CaseId = "V138_N02", Result = "Pass", ReadOnly = true, WriterStarted = false, DatabaseUnchanged = true }).ConfigureAwait(false);
            return;
        }
        Require(!File.Exists(options.DatabasePath), "RecipeTransferConsumerRequiresFreshDirectory");
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        RecipeTransferResult denied;
        await using (var provider = services.BuildServiceProvider())
        {
            var runtime = provider.GetRequiredService<IStationRuntime>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var snapshot = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(false);
                Require(snapshot.AuditIntegrity?.State != AuditIntegrityState.Faulted, snapshot.AuditIntegrity?.ReasonCode ?? "AuditFault");
                if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified) break;
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
            }
            var transfer = provider.GetRequiredService<IRecipeTransferService>();
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole);
            var access = await transfer.GetAccessAsync(invocation, Permission.ImportRecipe, timeout.Token).ConfigureAwait(false);
            Require(!access.Allowed, "RecipeTransferConsumerAnonymousAccessGranted");
            var input = new byte[] { 0 };
            denied = await transfer.ImportAsync(new(Guid.NewGuid(), invocation, Convert.ToHexString(SHA256.HashData(input)),
                "V138 independent consumer anonymous boundary"), input, timeout.Token).ConfigureAwait(false);
            Require(!denied.Succeeded && denied.Outcome.Disposition == CommandDisposition.Rejected &&
                denied.Outcome.Audit == AuditPersistence.Persisted && denied.Draft is null && denied.Import is null,
                denied.Outcome.ReasonCode);
            var drafts = await provider.GetRequiredService<IRecipeDraftHistoryQuery>().QueryAsync(new(), timeout.Token).ConfigureAwait(false);
            Require(drafts.Available && drafts.Revisions.Count == 0, "RecipeTransferConsumerUnexpectedDraft");
        }
        await WriteAsync(directory, "recipe-transfer-evidence.json", new
        {
            CaseId = "V138_N01", Result = "Pass", SchemaVersion = 24,
            ConsumerSha256 = HashFile(typeof(RecipeTransferDemo).Assembly.Location),
            Disposition = denied.Outcome.Disposition.ToString(), Audit = denied.Outcome.Audit.ToString(),
            denied.Outcome.ReasonCode, ImportedDraftCount = 0, ProductionAuthority = false,
            PositiveTransfer = "CoveredByControlledRuntimeTests"
        }).ConfigureAwait(false);
    }

    private static ProductionStoreOptions Options(string directory)
    {
        var audit = new AuditIntegrityPolicy("SampleRecipeTransfer", "development-v1", "SharpInspect.SampleRecipeTransfer")
        {
            AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "private-keys"),
            CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1)
        };
        return new(Path.Combine(directory, "recipe-transfer.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, new LocalPasswordPolicy
            { Blocklist = PasswordBlocklist.Create("transfer-development", "1", new[] { "passwordpassword" }) },
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development),
            RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("Sample.Transfer.Execution", "1",
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
            RecipeTransfers = new RecipeTransferStoreOptions
            { PortablePolicy = new("Sample.Transfer.Portable", "1", Array.Empty<RecipeTransferPortableContract>()) }
        };
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Task WriteAsync(string directory, string name, object value) => File.WriteAllTextAsync(Path.Combine(directory, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
