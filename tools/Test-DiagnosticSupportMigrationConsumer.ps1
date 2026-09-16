param(
    [Parameter(Mandatory)][string]$Run,
    [Parameter(Mandatory)][string]$PackageFeed,
    [Parameter(Mandatory)][string]$SourcePackageFeed,
    [Parameter(Mandatory)][ValidateSet(39)][int]$SourceSchema,
    [int[]]$Phases = @(1..14),
    [string]$DependencyPackageFeed = 'https://api.nuget.org/v3/index.json'
)
$ErrorActionPreference = 'Stop'
if ($Phases.Count -eq 0 -or @($Phases | Sort-Object -Unique).Count -ne $Phases.Count) {
    throw 'At least one distinct public migration phase is required.'
}
$migrationRun = [IO.Path]::GetFullPath($Run)
# Keep the SQLite backup, journal and lock suffixes within the Windows VFS path budget even
# under the timestamped full-validation root.
$migrationRoot = Join-Path $migrationRun ('diagnostic-support-' + $SourceSchema)
if (Test-Path -LiteralPath $migrationRoot) { throw 'Use a fresh diagnostic support migration consumer directory.' }
[void][IO.Directory]::CreateDirectory($migrationRoot)
$migrationCurrentFeed = [IO.Path]::GetFullPath($PackageFeed)
$migrationOldFeed = [IO.Path]::GetFullPath($SourcePackageFeed)
if ($migrationCurrentFeed -eq $migrationOldFeed) { throw 'The old writer must come from independently preserved packages.' }
foreach ($migrationPhase in $Phases) {
    if ($migrationPhase -lt 1 -or $migrationPhase -gt 14) { throw 'Unsupported public migration phase.' }
}
# The preserved schema-39 writer must reject the newer schema through its public writer boundary.
$migrationOldRefusalReason = 'TraceStoreUnavailable'
$migrationSourceSchema = $SourceSchema
$migrationTargetSchema = 40
$migrationVerificationPrefix = 'V158'
$migrationCompletedPhase = 14

# The probe is compiled independently against the preserved schema-39 packages (seed and old
# reader) and against the schema-40 candidate (migration, current reader). It uses only public
# package APIs and read-only SQLite projections; it never references source projects, imports a
# private fixture, or manufactures a version downgrade. CURRENT_MIGRATION guards every schema-40
# API so the preserved writer stays a real schema-39 application.
$migrationProbeSource = @'
// Compiled independently against the preserved schema-39 packages and the schema-40 candidate.
// The seed and old-reader modes keep the exact schema-39 profile; the migration and current
// reader add the installed logging/support bindings and the schema-40 diagnostic ledger.
// No project reference, private fixture import, synthetic user_version downgrade, or production
// qualification is used by this probe.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Outbox;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;

var mode = args[0];
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(directory);
var options = Options(directory);
object evidence;
#if CURRENT_MIGRATION
if (mode is "migrate" or "pause")
{
    // Isolated probe diagnostics keep the original validation cause when the public
    // maintenance boundary deliberately returns a normalized failure reason.
    var diagnosticCount = 0;
    AppDomain.CurrentDomain.FirstChanceException += (_, observation) =>
    {
        var error = observation.Exception;
        if (Interlocked.Increment(ref diagnosticCount) <= 16)
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace);
    };
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
    evidence = new { phase = (int)session.Status.Phase, session.Status.ReasonCode, maintenanceReady = session.Status.Ready,
        session.Status.Completed, session.Status.OperationId, session.Status.JournalHeadHash, session.Status.Backup,
        sourceSchemaVersion = session.Status.SourceSchemaVersion,
        targetSchemaVersion = session.Status.TargetSchemaVersion, schema = Schema(options.DatabasePath),
        // A paused phase may still hold the exclusive maintenance transaction,
        // so the ledger is only read in the process that completed the resume.
        auditLedger = mode == "migrate" ? AuditLedgerDigest(options.DatabasePath) : null,
        sourceRowsPreserved = mode == "migrate" ? VerifySourceRows(directory, options.DatabasePath) : (bool?)null,
        diagnosticSupportConfigured = mode == "migrate" ? DiagnosticSupportFootprint(options.DatabasePath) : null,
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
    long? postWriteAuditSequence = null;
    object? diagnosticSupport = null;
    object? retention = null;
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
#if CURRENT_MIGRATION
        if (mode == "current-reopen" && snapshot.Store.State == HealthState.Healthy)
        {
            // The reopened schema-40 store must expose the configured diagnostic support
            // state and the preserved retention capability through public read contracts.
            var query = provider.GetRequiredService<IDiagnosticSupportQuery>();
            var capture = query.ReadCapture();
            var bundle = query.ReadBundle();
            var read = await provider.GetRequiredService<ISupportBundleReader>().ReadAsync(
                new SupportBundleReadRequest(Guid.NewGuid(), new CommandInvocation(CommandSource.PhysicalConsole)));
            var retained = await provider.GetRequiredService<IEvidenceRetentionService>().ReadAsync(new());
            var capacity = await provider.GetRequiredService<ITraceStorageCapacityQuery>().ReadAsync();
            diagnosticSupport = new { queryResolved = true, captureConfigured = capture.Configured,
                capturePhase = capture.Phase.ToString(), captureReason = capture.ReasonCode, elevated = capture.Elevated,
                bundleConfigured = bundle.Configured, bundlePhase = bundle.Phase.ToString(),
                readAvailable = read.Available, readReason = read.ReasonCode };
            retention = new { available = retained.Available, records = retained.Records.Count,
                capacityAvailable = capacity.Available, capacityReason = capacity.ReasonCode };
        }
#endif
        // An unsupported command produces a rejected audit fact through the
        // actual public writer path. It requests no device or production action.
        result = await runtime.SubmitAsync(new ProbeUnsupportedCommand(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole)));
        if (result.Audit == AuditPersistence.Persisted)
        {
            // Command completion proves persistence, not completion of the
            // asynchronous integrity recheck. Bind this observation to the
            // real post-command ledger tail; an older Verified is insufficient.
            var ledger = AuditLedgerDigest(options.DatabasePath).Split('|');
            if (ledger.Length != 3 || !long.TryParse(ledger[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out var requiredSequence) || requiredSequence <= 0)
                throw new InvalidOperationException("ProbePostWriteAuditTailUnavailable");
            postWriteAuditSequence = requiredSequence;
            do
            {
                snapshot = await runtime.GetSnapshotAsync();
                if (snapshot.Store.State == HealthState.Faulted ||
                    snapshot.AuditIntegrity?.State == AuditIntegrityState.Faulted)
                    throw new InvalidOperationException("ProbePostWriteAuditFaulted: " +
                        snapshot.Store.ReasonCode + "/" + snapshot.AuditIntegrity?.ReasonCode);
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("ProbePostWriteAuditTimeout: " +
                        snapshot.AuditIntegrity?.State + "/" + snapshot.AuditIntegrity?.VerifiedThroughSequence +
                        "/" + requiredSequence);
                if (snapshot.Store.State == HealthState.Healthy &&
                    snapshot.AuditIntegrity is { State: AuditIntegrityState.Verified } verified &&
                    verified.VerifiedThroughSequence >= requiredSequence) break;
                await Task.Delay(25);
            } while (true);
        }
        if (snapshot.Ready) throw new InvalidOperationException("ProbeUnexpectedProductionReady");
    }
    var auditLedgerAfter = AuditLedgerDigest(options.DatabasePath);
    evidence = new { phase = 0, Ready = snapshot.Ready, state = snapshot.Store.State.ToString(),
        reason = snapshot.Store.ReasonCode, audit = result.Audit.ToString(), result.ReasonCode,
        auditIntegrity = snapshot.AuditIntegrity?.State.ToString(), schema = Schema(options.DatabasePath),
        postWriteAuditSequence, auditVerifiedThroughSequence = snapshot.AuditIntegrity?.VerifiedThroughSequence,
        assemblyHash = AssemblyHash(), auditLedgerBefore, auditLedgerAfter,
        sourceRows = mode == "seed" ? RowDigests(options.DatabasePath) : null,
        auditLedgerChanged = auditLedgerBefore != auditLedgerAfter, diagnosticSupport, retention };
    WriteEvidence(Path.Combine(directory, mode + "-writer.json"), evidence);
}
Console.WriteLine(JsonSerializer.Serialize(evidence));

static ProductionStoreOptions Options(string directory)
{
    Directory.CreateDirectory(Path.Combine(directory, "database"));
    Directory.CreateDirectory(Path.Combine(directory, "quarantine-stage"));
    Directory.CreateDirectory(Path.Combine(directory, "quarantine-final"));
    const string station = "V158.PackageProbe";
    var name = "SharpInspect.Test.V158.Probe." + Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(directory))).Substring(0, 24);
    var policy = new AuditIntegrityPolicy(station, "1", name)
    {
        AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
        CheckpointEveryEntries = 2, MaximumVerificationEntries = 10_000,
        VerificationInterval = TimeSpan.FromSeconds(1)
    };
    // Explicit source policy: newly introduced optional permissions must not
    // change the configuration of a store created by the preserved application.
    var authoring = new AuthorizationPolicy("V158.Probe.Authoring", "1",
        new Dictionary<HumanRoleBundle, IEnumerable<Permission>>
        {
            [HumanRoleBundle.Operator] = new[] { 5, 6, 29 }.Select(value => (Permission)value),
            [HumanRoleBundle.Technician] = new[] { 5, 6, 7, 11, 13, 23, 29, 30, 31 }.Select(value => (Permission)value),
            [HumanRoleBundle.Administrator] = Enumerable.Range(1, 31).Select(value => (Permission)value)
        }, Enumerable.Range(1, 30).Where(value => value is not 5 and not 6 and not 29)
            .Select(value => (Permission)value));
    var identity = new LocalIdentityOptions(station, new LocalPasswordPolicy
    {
        Blocklist = PasswordBlocklist.Create("V158.Probe.Blocklist", "1", new[] { "known-compromised-value" })
    }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authoring);
    var stage = Path.Combine(directory, "image-stage");
    Directory.CreateDirectory(stage);
    var stageOptions = new SharpInspect.Runtime.Images.ProductionImageStageOptions(stage,
        512 * 1024, 32 * 1024 * 1024, 200);
    var imageEvidence = new ProductionImageEvidenceStoreOptions(stageOptions);
    var finalRoot = Path.Combine(directory, "image-final");
    Directory.CreateDirectory(finalRoot);
    var finalization = new ProductionImageFinalizationStoreOptions(
        new ProductionImageFinalizationRootOptions(finalRoot, 1024 * 1024, 64 * 1024 * 1024, 400), imageEvidence);
    var receiverKeyPath = Path.Combine(directory, "isolated-receiver-public-key.txt");
    if (!File.Exists(receiverKeyPath))
    {
        using var ephemeral = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(receiverKeyPath, Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()));
    }
    var route = new OutboxRouteDefinition("isolated-result", "1", OutboxRouteCriticality.BestEffort,
        "isolated-receiver", OutboxReceiverProtocol.CorePayloadContract, OutboxReceiverProtocol.ContentType,
        OutboxReceiverProtocol.ReceiverContract, File.ReadAllText(receiverKeyPath),
        new OutboxContractReference("Isolated.NoNetwork", "1", Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("isolated-no-network-v1")))), 1024 * 1024);
    var lifecycle = new RecipeLifecycleStoreOptions();
    var outbox = new ProductionOutboxStoreOptions(new[] { route }, lifecycle, imageEvidence, finalization)
    { ManualRecovery = new ProductionOutboxRecoveryOptions() };
    // The schema-39 source carries the complete previous retention generation; the
    // target adds only the installed logging/support bindings and the schema-40 ledger.
    var storageRetention = new TraceStorageRetentionOptions(new TraceRetentionExecutionPolicy("V158.Probe.Retention", "1",
            "isolated package verification", "No deletion class enabled in the migration consumer.",
            Array.Empty<TraceRetentionClass>(),
            new TraceStorageMaintenanceBudget(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), 1024 * 1024, 4), 2),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        new TraceStorageRecoveryBudget(128L << 20, 10000, 1L << 20));
#if CURRENT_MIGRATION
    var (logging, diagnosticSupport) = DiagnosticSupportOptions(directory);
#endif
    return new ProductionStoreOptions(Path.Combine(directory, "database", "station.sqlite"))
    {
        AuditIntegrityPolicy = policy, LocalIdentity = identity,
        RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V158.Probe.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
        RecipeReleases = new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
            "V158.Probe.ReleaseGovernance", "1", RecipeGovernanceMode.MakerCheckerRelease)),
        ProductionInspections = new ProductionInspectionStoreOptions(),
        TraceStoragePolicies = new TraceStoragePolicyStoreOptions
        {
            DeploymentScope = new TraceStorageDeploymentScope("V158.Probe.Deployment", "1",
                Array.Empty<TraceStorageRouteIdentity>())
        },
        // The arm ledger's own store guard requires the admission ledger, so the
        // seed and target profiles both keep it (T49 carried it as well).
        ProductionAdmission = new ProductionAdmissionStoreOptions(),
        ProductionArming = new ProductionArmStoreOptions(),
        RecipeLifecycle = lifecycle,
        ImageEvidence = imageEvidence,
        ImageFinalization = finalization,
        // The same alarm mapping is compiled into both preserved and current
        // writers; finalization startup requires its recovery-bound integrity rule.
        AlarmPolicy = new AlarmPolicy("V158.Probe.Images.Alarm", "1", new[]
        {
            new AlarmPolicyRule("EvidenceIntegrityFault", "Runtime.ImageEvidence", AlarmSeverity.Error,
                ProductionImpact.BlockNewTriggers, true, AlarmNotification.None, null,
                ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoActiveExecution),
            new AlarmPolicyRule("ImageEvidenceBacklog", "Runtime.ImageEvidence", AlarmSeverity.Warning,
                ProductionImpact.BlockNewTriggers, false, AlarmNotification.None, null)
        }, TimeSpan.FromSeconds(5)),
        Outbox = outbox,
        EvidenceReconciliation = new(new TraceStorageMaintenanceBudget(TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(5), 2 * 1024 * 1024, 16))
        {
            StageQuarantine = new(Path.Combine(directory, "quarantine-stage"), 512 * 1024, 32 * 1024 * 1024, 200),
            FinalQuarantine = new(Path.Combine(directory, "quarantine-final"), 1024 * 1024, 64 * 1024 * 1024, 400)
        },
        StorageRetention = storageRetention,
#if CURRENT_MIGRATION
        LoggingDiagnostics = logging,
        DiagnosticSupport = diagnosticSupport,
#endif
        CommitTimeout = TimeSpan.FromSeconds(5), QueryTimeout = TimeSpan.FromSeconds(5), QueueCapacity = 8
    };
}

#if CURRENT_MIGRATION
static (LoggingDiagnosticsOptions Logging, DiagnosticSupportStoreOptions Support) DiagnosticSupportOptions(string directory)
{
    // The probe's own installed logging and support policy. The support policy binds the
    // installed support root and the exact logging policy; the fixed trace hash is legal
    // because this consumer validates the migration and configuration contract only.
    var traceHash = new string('B', 64);
    var sample = new DiagnosticEventContract("V158.Probe.Measurement", 1, "Runtime", DiagnosticLevel.Information, false,
        new[]
        {
            new DiagnosticFieldContract("State", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Safe, true,
                null, null, new[] { "Idle", "Busy" }),
            new DiagnosticFieldContract("Count", DiagnosticScalarKind.Int64, DiagnosticDataClass.Safe, false, 0, 100, null),
            new DiagnosticFieldContract("VendorState", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Protected,
                false, null, null, new[] { "Idle", "Busy" }),
            new DiagnosticFieldContract("Forbidden", DiagnosticScalarKind.Symbol, DiagnosticDataClass.Prohibited,
                false, null, null, new[] { "Bait" })
        });
    var policy = new LoggingDiagnosticsPolicy("V158.Probe.Logging", "1", "V158-probe", "1", traceHash,
        DiagnosticLevel.Information, DiagnosticStandardContracts.All.Concat(new[] { sample }),
        new DiagnosticProducerBudget(1000, 1_000_000, 32, 128, 1000, TimeSpan.FromSeconds(10), 4),
        new DiagnosticQueueBudget(4, 4 * 8192, 2, 2 * 8192, DiagnosticLevel.Warning, TimeSpan.FromSeconds(2)),
        new DiagnosticQueueBudget(4, 4 * 8192, 2, 2 * 8192, DiagnosticLevel.Warning, TimeSpan.FromSeconds(2)),
        new DiagnosticQueueBudget(4, 4 * 8192, 2, 2 * 8192, DiagnosticLevel.Warning, TimeSpan.FromSeconds(2)),
        new DiagnosticFileBudget(4096, 65536, 4, 262144, TimeSpan.FromSeconds(1), TimeSpan.FromDays(7)),
        new DiagnosticFileBudget(4096, 65536, 4, 262144, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1)),
        10, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), 100, 65536, TimeSpan.FromMinutes(5), 1000);
    var safe = new DiagnosticLocalStoreOptions(Path.Combine(directory, "diag-safe"), false,
        policy.SafeFiles.MaximumRecordBytes, policy.SafeFiles.MaximumFileBytes, policy.SafeFiles.MaximumFiles,
        policy.SafeFiles.MaximumTotalBytes, policy.SafeFiles.RollAfter, policy.SafeFiles.Retention);
    var protectedStore = new DiagnosticLocalStoreOptions(Path.Combine(directory, "diag-protected"), true,
        policy.ProtectedFiles.MaximumRecordBytes, policy.ProtectedFiles.MaximumFileBytes, policy.ProtectedFiles.MaximumFiles,
        policy.ProtectedFiles.MaximumTotalBytes, policy.ProtectedFiles.RollAfter, policy.ProtectedFiles.Retention);
    var safeBinding = DiagnosticDirectoryInstallation.Install(safe);
    var protectedBinding = DiagnosticDirectoryInstallation.Install(protectedStore);
    var supportPolicy = new DiagnosticSupportPolicy("V158.DiagnosticSupport", "1", "V158-probe-approval",
        policy.ContentHash, policy.TracePolicySnapshotHash, TimeSpan.FromDays(1), 100, 4096, 64 * 1024,
        TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100), TimeSpan.FromDays(30), 64, 16 * 1024,
        32L * 1024 * 1024);
    var supportFiles = new DiagnosticLocalStoreOptions(Path.Combine(directory, "support"), true,
        supportPolicy.MaximumBundleBytes, supportPolicy.MaximumBundleBytes, 4, 4L * supportPolicy.MaximumBundleBytes,
        supportPolicy.ExportTimeout, supportPolicy.Retention);
    var logging = new LoggingDiagnosticsOptions(policy, safe, safeBinding, protectedStore, protectedBinding)
    { SupportBundles = new(supportPolicy, supportFiles, DiagnosticDirectoryInstallation.Install(supportFiles)) };
    return (logging, new DiagnosticSupportStoreOptions(supportPolicy, logging.SupportBundles!.BindingHash));
}

static object DiagnosticSupportFootprint(string database)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN " +
        "('diagnostic_support_config','diagnostic_operation_facts');";
    var tables = Convert.ToInt64(command.ExecuteScalar());
    long facts = -1;
    long activation = -1;
    if (tables == 2)
    {
        command.CommandText = "SELECT COUNT(*) FROM diagnostic_operation_facts;";
        facts = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticSupportActivated';";
        activation = Convert.ToInt64(command.ExecuteScalar());
    }
    return new { configTables = tables, facts, activation };
}

static bool VerifySourceRows(string directory, string database)
{
    using var seed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "seed-writer.json")));
    var expected = seed.RootElement.GetProperty("sourceRows").Deserialize<Dictionary<string, RowsDigest>>()!;
    var actual = RowDigests(database, expected);
    if (expected.Count == 0 || actual.Count != expected.Count ||
        expected.Any(pair => !actual.TryGetValue(pair.Key, out var found) || found != pair.Value))
        throw new InvalidOperationException("ProbeOriginalRowsChanged");
    return true;
}
#endif

static Dictionary<string, RowsDigest> RowDigests(string database, Dictionary<string, RowsDigest>? limits = null)
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
    connection.Open();
    var tables = new List<string>();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));
    }
    var result = new Dictionary<string, RowsDigest>();
    foreach (var table in tables)
    {
        if (limits is not null && !limits.ContainsKey(table)) continue;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid,* FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid" +
            (limits is null ? ";" : " LIMIT " + limits[table].Rows.ToString(CultureInfo.InvariantCulture) + ";");
        using var reader = command.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
            count++;
        }
        result.Add(table, new(count, Convert.ToHexString(hash.GetHashAndReset())));
    }
    return result;
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

sealed record RowsDigest(long Rows, string Hash);
'@

function Get-MigrationDatabaseBytes([string]$Database) {
    $wal = $Database + '-wal'
    # SHM holds transient read locks, not committed database content. A missing
    # or empty WAL contributes the same empty byte sequence to this comparison.
    $walBytes = [byte[]]@()
    if (Test-Path -LiteralPath $wal) { $walBytes = [IO.File]::ReadAllBytes($wal) }
    [ordered]@{ databaseSha256=(Get-FileHash -LiteralPath $Database -Algorithm SHA256).Hash;
        walLength=$walBytes.Length; walSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$walBytes)) }
}

function Build-MigrationConsumer([string]$Name, [string]$Feed, [bool]$Current) {
    $consumer = Join-Path $migrationRoot $Name
    [void][IO.Directory]::CreateDirectory($consumer)
    [IO.File]::WriteAllText((Join-Path $consumer 'Program.cs'), $migrationProbeSource, [Text.UTF8Encoding]::new($false))
    $define = if ($Current) { 'CURRENT_MIGRATION' } else { 'SOURCESCHEMA_39' }
    $xml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <DefineConstants>$define</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SharpInspect.NET.Runtime" Version="0.1.0-dev.1" />
    <PackageReference Include="SharpInspect.NET.Abstractions" Version="0.1.0-dev.1" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="6.0.0" />
  </ItemGroup>
</Project>
"@
    $project = Join-Path $consumer 'MigrationConsumer.csproj'
    [IO.File]::WriteAllText($project, $xml, [Text.UTF8Encoding]::new($false))
    $config = Join-Path $consumer 'NuGet.Config'
    $feedXml = '<configuration><packageSources><clear/><add key="migration-snapshot" value="' +
        [Security.SecurityElement]::Escape($Feed) +
        '"/><add key="dependencies" value="' + [Security.SecurityElement]::Escape($DependencyPackageFeed) +
        '"/></packageSources><packageSourceMapping><packageSource key="migration-snapshot"><package pattern="SharpInspect.NET.*"/>' +
        '</packageSource><packageSource key="dependencies"><package pattern="*"/></packageSource></packageSourceMapping></configuration>'
    [IO.File]::WriteAllText($config, $feedXml, [Text.UTF8Encoding]::new($false))
    & dotnet restore $project --configfile $config --packages (Join-Path $consumer 'cache') *> (Join-Path $consumer 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Migration consumer restore failed: $consumer" }
    & dotnet build $project -c Release --no-restore -m:1 *> (Join-Path $consumer 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "Migration consumer build failed: $consumer" }
    $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
    if (@($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'project' }).Count -ne 0) {
        throw 'Migration consumer unexpectedly references source projects.'
    }
    $runtime = Join-Path $consumer 'bin/Release/net6.0/SharpInspect.Runtime.dll'
    $package = Join-Path $Feed 'SharpInspect.NET.Runtime.0.1.0-dev.1.nupkg'
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entry = $archive.GetEntry('lib/net6.0/SharpInspect.Runtime.dll')
        if (-not $entry) { throw 'Runtime DLL absent from preserved package.' }
        $stream = $entry.Open()
        try { $packagedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ((Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -cne $packagedHash) {
            throw 'The running consumer runtime does not match its declared package.'
        }
    }
    finally { $archive.Dispose() }
    return [ordered]@{ dll=(Join-Path $consumer 'bin/Release/net6.0/MigrationConsumer.dll'); runtime=$runtime;
        runtimeHash=$packagedHash; packageHash=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash }
}

function Start-MigrationProbe([string]$Dll, [string[]]$Arguments, [string]$LogPrefix) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($Dll)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    return @{ process=$process; stdout=$process.StandardOutput.ReadToEndAsync();
        stderr=$process.StandardError.ReadToEndAsync(); log=$LogPrefix }
}

function Finish-MigrationProbe($Probe, [bool]$Killed = $false) {
    try {
        if (-not $Probe.process.WaitForExit(30000)) { $Probe.process.Kill($true); throw 'Migration probe process deadline exceeded.' }
        $Probe.stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath ($Probe.log + '.stdout.log') -Encoding utf8
        $Probe.stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath ($Probe.log + '.stderr.log') -Encoding utf8
        if (-not $Killed -and $Probe.process.ExitCode -ne 0) { throw ('Migration probe failed: ' + $Probe.log) }
    }
    finally { $Probe.process.Dispose() }
}

$migrationOld = Build-MigrationConsumer 'old' $migrationOldFeed $false
$migrationNew = Build-MigrationConsumer 'current' $migrationCurrentFeed $true
if ($migrationOld.runtimeHash -ceq $migrationNew.runtimeHash) { throw 'Old and current runtime binaries are identical.' }
Write-Output ('V158 schema 39 preserved writer and schema 40 candidate consumers built; old=' +
    $migrationOld.runtimeHash.Substring(0, 12) + ' current=' + $migrationNew.runtimeHash.Substring(0, 12))
$migrationCases = @()
$migrationRefusals = @()
foreach ($migrationPhase in $Phases) {
    $case = Join-Path $migrationRoot ('phase-' + $migrationPhase)
    [void][IO.Directory]::CreateDirectory($case)
    Finish-MigrationProbe (Start-MigrationProbe $migrationOld.dll @('seed',$case) (Join-Path $case 'seed'))
    $seed = Get-Content -LiteralPath (Join-Path $case 'seed-writer.json') -Raw | ConvertFrom-Json
    if ($seed.schema -ne $migrationSourceSchema -or $seed.state -ne 'Healthy' -or $seed.audit -ne 'Persisted' -or
        $seed.auditIntegrity -ne 'Verified' -or $seed.postWriteAuditSequence -le 0 -or
        $seed.auditVerifiedThroughSequence -lt $seed.postWriteAuditSequence -or
        $seed.auditLedgerBefore -cne 'absent' -or $seed.Ready -or
        $seed.assemblyHash -cne $migrationOld.runtimeHash -or -not $seed.sourceRows -or
        @($seed.sourceRows.PSObject.Properties).Count -eq 0) {
        throw "Preserved old writer did not create a real writable schema-$SourceSchema database."
    }

    $probe = Start-MigrationProbe $migrationNew.dll @('pause',$case,$migrationOld.runtime,[string]$migrationPhase) (Join-Path $case 'pause')
    $proofPath = Join-Path $case 'pause-phase.json'
    $phaseProof = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while ([DateTime]::UtcNow -lt $deadline -and -not $probe.process.HasExited) {
            if (Test-Path -LiteralPath $proofPath) {
                try { $phaseProof = Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json }
                catch { $phaseProof = $null }
                if ($phaseProof) { break }
            }
            Start-Sleep -Milliseconds 50
        }
        if (-not $phaseProof -or $phaseProof.phase -ne $migrationPhase -or
            $phaseProof.sourceSchemaVersion -ne $migrationSourceSchema -or
            $phaseProof.targetSchemaVersion -ne $migrationTargetSchema) {
            throw 'The public maintenance phase was not observed before interruption.'
        }
        $probe.process.Kill($true)
    }
    finally {
        if (-not $probe.process.HasExited) { $probe.process.Kill($true) }
        Finish-MigrationProbe $probe $true
    }
    Finish-MigrationProbe (Start-MigrationProbe $migrationNew.dll @('migrate',$case,$migrationOld.runtime,[string]$migrationCompletedPhase) (Join-Path $case 'resume'))
    $resumed = Get-Content -LiteralPath (Join-Path $case 'migrate-phase.json') -Raw | ConvertFrom-Json
    if (-not $resumed.Completed -or $resumed.schema -ne $migrationTargetSchema -or
        $resumed.sourceSchemaVersion -ne $migrationSourceSchema -or
        $resumed.targetSchemaVersion -ne $migrationTargetSchema -or
        $resumed.OperationId -ne $phaseProof.OperationId -or -not $resumed.Backup -or -not $resumed.sourceRowsPreserved -or
        $resumed.auditLedger -notmatch '^[0-9]+\|[0-9]+\|[0-9A-Fa-f]{64}$' -or
        $resumed.diagnosticSupportConfigured.configTables -ne 2 -or
        $resumed.diagnosticSupportConfigured.facts -ne 0 -or
        $resumed.diagnosticSupportConfigured.activation -ne 1) {
        throw "Cold resume did not prove schema $migrationTargetSchema, verified backup, signed history and the configured diagnostic ledger."
    }
    if ($resumed.Backup.SourceSchemaVersion -ne $migrationSourceSchema -or
        $resumed.Backup.SourceApplicationSha256 -cne $migrationOld.runtimeHash -or
        -not (Test-Path -LiteralPath $resumed.Backup.Path -PathType Leaf) -or
        $resumed.Backup.ByteLength -le 0 -or
        (Get-Item -LiteralPath $resumed.Backup.Path).Length -ne $resumed.Backup.ByteLength -or
        (Get-FileHash -LiteralPath $resumed.Backup.Path -Algorithm SHA256).Hash -cne $resumed.Backup.Sha256) {
        throw 'Migration backup bytes or preserved source identity differ from the verified backup.'
    }
    $database = Join-Path $case 'database/station.sqlite'
    $before = Get-MigrationDatabaseBytes $database
    Finish-MigrationProbe (Start-MigrationProbe $migrationOld.dll @('old-reopen',$case) (Join-Path $case 'old-reopen'))
    $denied = Get-Content -LiteralPath (Join-Path $case 'old-reopen-writer.json') -Raw | ConvertFrom-Json
    $after = Get-MigrationDatabaseBytes $database
    if ($denied.state -ne 'Faulted' -or $denied.audit -eq 'Persisted' -or $denied.schema -ne $migrationTargetSchema -or
        $denied.Ready -or $denied.reason -cne $migrationOldRefusalReason -or $denied.auditIntegrity -eq 'Verified' -or
        $denied.auditLedgerBefore -cne $denied.auditLedgerAfter -or
        $denied.auditLedgerBefore -cne $resumed.auditLedger -or
        $denied.assemblyHash -cne $migrationOld.runtimeHash -or
        ($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) {
        throw 'The actual old schema-39 binary did not refuse the migrated database without modifying it.'
    }
    Finish-MigrationProbe (Start-MigrationProbe $migrationNew.dll @('current-reopen',$case) (Join-Path $case 'current-reopen'))
    $current = Get-Content -LiteralPath (Join-Path $case 'current-reopen-writer.json') -Raw | ConvertFrom-Json
    if ($current.state -ne 'Healthy' -or $current.audit -ne 'Persisted' -or $current.schema -ne $migrationTargetSchema -or
        $current.Ready -or $current.auditIntegrity -ne 'Verified' -or $current.postWriteAuditSequence -le 0 -or
        $current.auditVerifiedThroughSequence -lt $current.postWriteAuditSequence -or
        $current.assemblyHash -cne $migrationNew.runtimeHash -or -not $current.auditLedgerChanged -or
        $current.diagnosticSupport.queryResolved -cne $true -or
        $current.diagnosticSupport.captureConfigured -cne $true -or
        $current.diagnosticSupport.capturePhase -cne 'Baseline' -or
        $current.diagnosticSupport.elevated -cne $false -or
        $current.diagnosticSupport.bundleConfigured -cne $true -or
        $current.diagnosticSupport.bundlePhase -cne 'Idle' -or
        $current.diagnosticSupport.readAvailable -cne $false -or
        $current.retention.available -cne $true -or $current.retention.records -ne 0 -or
        $current.retention.capacityAvailable -cne $false) {
        throw 'The current public writer did not reopen the completed migration with the configured diagnostic ledger and verified signed history.'
    }
    $migrationCases += [ordered]@{ id=($migrationVerificationPrefix + '_M02'); phase=$migrationPhase; result='Pass';
        interruptedBy='Process.Kill(entireProcessTree:true)';
        operation=$resumed.OperationId; backup=$resumed.Backup; sourceSchemaVersion=$resumed.sourceSchemaVersion;
        targetSchemaVersion=$resumed.targetSchemaVersion; oldWriterReason=$denied.reason;
        oldWriterExpectedReason=$migrationOldRefusalReason;
        oldWriterAuditUnchanged=($denied.auditLedgerBefore -ceq $denied.auditLedgerAfter); oldDatabaseUnchanged=$true;
        oldDatabaseAndWalUnchanged=$true; databaseBytesBefore=$before; databaseBytesAfter=$after;
        recoveryScope=$(if ($migrationPhase -eq $migrationCompletedPhase) { 'CompletedOperationReopenIdempotency' } else { 'InterruptedOperationResume' });
        diagnosticSupportConfigured=$resumed.diagnosticSupportConfigured; currentDiagnosticSupport=$current.diagnosticSupport;
        retention=$current.retention; sourceRowsPreserved=$resumed.sourceRowsPreserved;
        seedPostWriteAuditSequence=$seed.postWriteAuditSequence; seedVerifiedThroughSequence=$seed.auditVerifiedThroughSequence;
        currentPostWriteAuditSequence=$current.postWriteAuditSequence; currentVerifiedThroughSequence=$current.auditVerifiedThroughSequence }
    $migrationRefusals += [ordered]@{ phase=$migrationPhase; reason=$denied.reason;
        expectedReason=$migrationOldRefusalReason; databaseSha256=$after.databaseSha256;
        walLength=$after.walLength; walSha256=$after.walSha256;
        auditLedgerUnchanged=($denied.auditLedgerBefore -ceq $denied.auditLedgerAfter) }
    Write-Output "$migrationVerificationPrefix schema $SourceSchema phase $migrationPhase crash/resume, old writer refusal and schema $migrationTargetSchema configured ledger PASS"
}
$migrationEvidence = [ordered]@{ id=($migrationVerificationPrefix + '_M01'); result='Pass';
    scope="isolated package consumers, verified SQLite backup, whole-process termination at listed public phases; explicit schema $migrationSourceSchema to $migrationTargetSchema; preserves the complete schema-39 retention profile and adds only the installed logging/support bindings and the schema-40 diagnostic ledger; completed phase checks reopen idempotency; capture/export runtime acceptance and production station qualification NotRun";
    requestedPhases=@($Phases); publicPhaseCount=14; fullPhaseCoverage=($Phases.Count -eq 14);
    sourceSchemaVersion=$migrationSourceSchema; targetSchemaVersion=$migrationTargetSchema;
    oldWriterExpectedReason=$migrationOldRefusalReason; old=$migrationOld; current=$migrationNew; cases=$migrationCases;
    productionQualificationAuthority=$false; captureExportRuntime='NotRun';
    completedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
$migrationRefusalEvidence = [ordered]@{ id=($migrationVerificationPrefix + '_M03'); result='Pass';
    scope='the preserved schema-39 public writer refused every migrated schema-40 database without changing a byte or appending an audit fact';
    oldWriterExpectedReason=$migrationOldRefusalReason; cases=$migrationRefusals;
    productionQualificationAuthority=$false }
[ordered]@{ migration=$migrationEvidence; previousWriterRefusal=$migrationRefusalEvidence } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $migrationRun ('diagnostic-support-migration-' + $SourceSchema + '-consumer.json')) -Encoding utf8
Write-Output "$migrationVerificationPrefix schema $SourceSchema -> $migrationTargetSchema migration consumer PASS phases=$($Phases.Count) previousWriterRefusal=$migrationOldRefusalReason"
