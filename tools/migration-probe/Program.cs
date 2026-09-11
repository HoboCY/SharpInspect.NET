// Compiled independently against the preserved schema-32 packages and against
// current packages. No project reference, reflection into internals, synthetic
// user_version downgrade, or production qualification is used by this probe.
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

var mode = args[0];
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
var options = Options(directory);
object evidence;
#if CURRENT_MIGRATION
if (mode is "migrate" or "pause")
{
    if (!SystemPrincipalCatalog.Runtime.Permissions.Contains(SystemPermission.MigrateStoreAtStartup))
        throw new InvalidOperationException("ProbeStartupMigrationCatalogMissing");
    var open = await SqliteStartupMaintenance.OpenAsync(options,
        new StoreStartupMaintenanceOptions(Path.GetFullPath(args[2]))
        { OperationTimeout = TimeSpan.FromSeconds(30), MaximumDatabaseBytes = 64L * 1024 * 1024 });
    if (!open.Available) throw new InvalidOperationException(open.Status.ReasonCode);
    await using var session = open.Session!;
    var expected = (StoreMigrationPhase)int.Parse(args[3]);
    while (session.Status.Phase < expected)
    {
        var status = await session.AdvanceAsync();
        if (status.Phase == StoreMigrationPhase.MaintenanceRequired || status.Ready)
            throw new InvalidOperationException(status.ReasonCode);
    }
    if (session.Status.Phase != expected || session.Status.Ready)
        throw new InvalidOperationException("ProbePhaseMismatch");
    evidence = new { phase = (int)session.Status.Phase, session.Status.ReasonCode, session.Status.Ready,
        session.Status.Completed, session.Status.OperationId, session.Status.JournalHeadHash, session.Status.Backup,
        assemblyHash = AssemblyHash(), schema = Schema(options.DatabasePath) };
    WriteEvidence(Path.Combine(directory, mode + "-phase.json"), evidence);
    if (mode == "pause")
    {
        // Parent kills the complete process tree after observing the flushed
        // phase file. There is deliberately no Dispose/ROLLBACK on that path.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
else
#endif
{
    var services = new ServiceCollection();
    services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(25));
    StationStateSnapshot snapshot;
    RuntimeCommandOutcome result;
    await using (var provider = services.BuildServiceProvider())
    {
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            snapshot = await runtime.GetSnapshotAsync();
            if (snapshot.Store.State == HealthState.Faulted ||
                snapshot.Store.State == HealthState.Healthy && snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified) break;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("ProbeStoreStartupTimeout: " + snapshot.Store.ReasonCode);
            await Task.Delay(25);
        } while (true);
        // An unsupported command produces a rejected audit fact through the
        // actual public writer path. It requests no device or production action.
        result = await runtime.SubmitAsync(new ProbeUnsupportedCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        if (snapshot.Ready) throw new InvalidOperationException("ProbeUnexpectedProductionReady");
    }
    evidence = new { phase = 0, Ready = snapshot.Ready, state = snapshot.Store.State.ToString(),
        reason = snapshot.Store.ReasonCode, audit = result.Audit.ToString(), result.ReasonCode,
        schema = Schema(options.DatabasePath), assemblyHash = AssemblyHash() };
    WriteEvidence(Path.Combine(directory, mode + "-writer.json"), evidence);
}
Console.WriteLine(JsonSerializer.Serialize(evidence));

static ProductionStoreOptions Options(string directory)
{
    const string station = "V149.PackageProbe";
    var name = "SharpInspect.Test.V149.Probe." + Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(directory))).Substring(0, 24);
    var policy = new AuditIntegrityPolicy(station, "1", name)
    {
        AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
        CheckpointEveryEntries = 2, MaximumVerificationEntries = 10_000,
        VerificationInterval = TimeSpan.FromSeconds(1)
    };
    var authoring = new AuthorizationPolicy("V149.Probe.Authoring", "1",
        AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key is HumanRoleBundle.Technician or HumanRoleBundle.Administrator
                ? pair.Value.Append(Permission.EditRecipeDraft) : pair.Value.AsEnumerable()),
        AuthorizationPolicy.Development.StepUpPermissions);
    var identity = new LocalIdentityOptions(station, new LocalPasswordPolicy
    {
        Blocklist = PasswordBlocklist.Create("V149.Probe.Blocklist", "1", new[] { "known-compromised-value" })
    }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authoring);
    return new ProductionStoreOptions(Path.Combine(directory, "store.sqlite"))
    {
        AuditIntegrityPolicy = policy, LocalIdentity = identity,
        RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V149.Probe.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
        ProductionAdmission = new ProductionAdmissionStoreOptions(), ProductionArming = new ProductionArmStoreOptions(),
#if CURRENT_MIGRATION
        RecipeLifecycle = new RecipeLifecycleStoreOptions(),
#endif
        CommitTimeout = TimeSpan.FromSeconds(5), QueryTimeout = TimeSpan.FromSeconds(5), QueueCapacity = 8
    };
}

static int Schema(string database)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(command.ExecuteScalar());
}

static string AssemblyHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ProductionStoreOptions).Assembly.Location)));

static void WriteEvidence(string path, object value)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    file.Write(bytes); file.Flush(true);
}

sealed record ProbeUnsupportedCommand(Guid CorrelationId, CommandInvocation Invocation)
    : RuntimeCommand(CorrelationId, Invocation);
