// Compiled independently against the preserved schema-33 packages and against
// current packages. No project reference, reflection into internals, synthetic
// user_version downgrade, or production qualification is used by this probe.
using System.Globalization;
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
        if (status.Phase == StoreMigrationPhase.MaintenanceRequired)
            throw new InvalidOperationException(status.ReasonCode);
    }
    if (session.Status.Phase != expected)
        throw new InvalidOperationException("ProbePhaseMismatch");
    // Maintenance Ready is a fixed API contract; only the writer branch below
    // observes actual Runtime Ready. Do not conflate these evidence fields.
    evidence = new { phase = (int)session.Status.Phase, session.Status.ReasonCode, maintenanceReady = session.Status.Ready,
        session.Status.Completed, session.Status.OperationId, session.Status.JournalHeadHash, session.Status.Backup,
        sourceSchemaVersion = session.Status.SourceSchemaVersion,
        targetSchemaVersion = session.Status.TargetSchemaVersion, schema = Schema(options.DatabasePath),
        // A paused phase may still hold the exclusive maintenance transaction,
        // so the ledger is only read in the process that completed the resume.
        auditLedger = mode == "migrate" ? AuditLedgerDigest(options.DatabasePath) : null,
        assemblyHash = AssemblyHash() };
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
    var auditLedgerBefore = AuditLedgerDigest(options.DatabasePath);
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
    var auditLedgerAfter = AuditLedgerDigest(options.DatabasePath);
    object? imageWork = null;
#if CURRENT_MIGRATION
    // Cold access after the writer lease was released. An available page with
    // zero items is the whole claim: no pending image work is invented here, no
    // image bytes are staged, and no PNG or production qualification is claimed.
    var page = await new SqlitePendingImageWorkQuery(options).QueryAsync();
    imageWork = new { available = page.Available, reason = page.ReasonCode, items = page.Items.Count,
        throughPosition = page.ThroughPosition, nextAfterPosition = page.NextAfterPosition,
        stageRoot = options.ImageEvidence!.Stage.StageRoot,
        stageContentHash = options.ImageEvidence!.Stage.ContentHash };
#endif
    evidence = new { phase = 0, Ready = snapshot.Ready, state = snapshot.Store.State.ToString(),
        reason = snapshot.Store.ReasonCode, audit = result.Audit.ToString(), result.ReasonCode,
        auditIntegrity = snapshot.AuditIntegrity?.State.ToString(), schema = Schema(options.DatabasePath),
        assemblyHash = AssemblyHash(), auditLedgerBefore, auditLedgerAfter,
        auditLedgerChanged = auditLedgerBefore != auditLedgerAfter, pendingImageWork = imageWork };
    WriteEvidence(Path.Combine(directory, mode + "-writer.json"), evidence);
}
Console.WriteLine(JsonSerializer.Serialize(evidence));

static ProductionStoreOptions Options(string directory)
{
    const string station = "V150.PackageProbe";
    var name = "SharpInspect.Test.V150.Probe." + Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(directory))).Substring(0, 24);
    var policy = new AuditIntegrityPolicy(station, "1", name)
    {
        AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
        CheckpointEveryEntries = 2, MaximumVerificationEntries = 10_000,
        VerificationInterval = TimeSpan.FromSeconds(1)
    };
    var authoring = new AuthorizationPolicy("V150.Probe.Authoring", "1",
        AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key is HumanRoleBundle.Technician or HumanRoleBundle.Administrator
                ? pair.Value.Append(Permission.EditRecipeDraft) : pair.Value.AsEnumerable()),
        AuthorizationPolicy.Development.StepUpPermissions);
    var identity = new LocalIdentityOptions(station, new LocalPasswordPolicy
    {
        Blocklist = PasswordBlocklist.Create("V150.Probe.Blocklist", "1", new[] { "known-compromised-value" })
    }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authoring);
#if CURRENT_MIGRATION
    var stage = Path.Combine(directory, "image-stage");
    Directory.CreateDirectory(stage);
    var stageOptions = new SharpInspect.Runtime.Images.ProductionImageStageOptions(stage,
        512 * 1024, 32 * 1024 * 1024, 200);
#endif
    return new ProductionStoreOptions(Path.Combine(directory, "store.sqlite"))
    {
        AuditIntegrityPolicy = policy, LocalIdentity = identity,
        RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V150.Probe.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
        RecipeReleases = new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
            "V150.Probe.ReleaseGovernance", "1", RecipeGovernanceMode.MakerCheckerRelease)),
        ProductionInspections = new ProductionInspectionStoreOptions(),
        // The arm ledger's own store guard requires the admission ledger, so the
        // seed and target profiles both keep it (T49 carried it as well).
        ProductionAdmission = new ProductionAdmissionStoreOptions(),
        ProductionArming = new ProductionArmStoreOptions(), RecipeLifecycle = new RecipeLifecycleStoreOptions(),
#if CURRENT_MIGRATION
        // Schema-34 image evidence: the qualified local stage owns the image
        // bytes, the store keeps only the pending manifest and work facts.
        ImageEvidence = new ProductionImageEvidenceStoreOptions(stageOptions),
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

// A read-only projection of the stored audit ledger: entry count, head
// sequence and head hash. The probe never writes it, and an unreadable or
// absent ledger can never satisfy the accepted "count|sequence|hash" shape.
static string AuditLedgerDigest(string database)
{
    if (!File.Exists(database) || new FileInfo(database).Length == 0) return "absent";
    try
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT COUNT(*), COALESCE(MAX(Sequence),0),
            COALESCE((SELECT Hash FROM audit_entries ORDER BY Sequence DESC LIMIT 1),'') FROM audit_entries;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return "unreadable";
        return reader.GetInt64(0).ToString(CultureInfo.InvariantCulture) + "|" +
            reader.GetInt64(1).ToString(CultureInfo.InvariantCulture) + "|" + reader.GetString(2);
    }
    catch (SqliteException) { return "unreadable"; }
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
