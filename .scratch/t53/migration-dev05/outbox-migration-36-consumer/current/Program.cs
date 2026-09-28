// Compiled independently against preserved schema-36 packages and the schema-37 candidate.
// Both writers retain the exact old route, image and identity configuration.
// No project reference, reflection into internals, synthetic
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
using SharpInspect.Runtime.StoragePolicies;
using SharpInspect.Runtime.Outbox;

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
    object? preservedImages = null;
    object? operations = null;
    long? postWriteAuditSequence = null;
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
#if CURRENT_MIGRATION
        if (snapshot.Store.State == HealthState.Healthy)
        {
            var recovery = provider.GetRequiredService<IProductionOutboxRecoveryService>();
            var governance = provider.GetRequiredService<IProductionOutboxGovernanceQuery>();
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole);
            var id = Guid.NewGuid();
            var hash = new string('A', 64);
            var recoverCommand = new RecoverOutboxDeliveryCommand(Guid.NewGuid(), invocation,
                id, hash, hash, 1, "isolated unauthenticated recovery refusal");
            var rawPayload = new byte[] { 123, 125 };
            var correction = new CreateCorrectiveOutboxDeliveryCommand(Guid.NewGuid(), invocation,
                id, hash, hash, "application/json", rawPayload, "isolated unauthenticated correction refusal");
            rawPayload[0] = 0;
            var copy = correction.CopyPayload();
            copy[1] = 0;
            if (!correction.CopyPayload().SequenceEqual(new byte[] { 123, 125 }))
                throw new InvalidOperationException("ProbeCorrectionBytesNotOwned");
            var recoveryResult = await recovery.RecoverAsync(recoverCommand);
            var correctionResult = await recovery.CreateCorrectiveDeliveryAsync(correction);
            foreach (var rejected in new[] { recoveryResult.Outcome, correctionResult.Outcome })
                if (rejected.Disposition != CommandDisposition.Rejected || rejected.Audit != AuditPersistence.Persisted)
                    throw new InvalidOperationException("ProbeUnauthenticatedOutboxOperationNotAudited:" +
                        rejected.ReasonCode + ":" + rejected.Audit);
            var governanceSnapshot = await governance.ReadAsync(id);
            if (!governanceSnapshot.Available || governanceSnapshot.Recoveries.Count != 0 || governanceSnapshot.Corrections.Count != 0)
                throw new InvalidOperationException("ProbeRefusedOperationChangedGovernance");
            var commandQuery = provider.GetRequiredService<ICommandTraceQuery>();
            foreach (var expected in new[] { (recoverCommand.CorrelationId, AuditedCommandKind.RecoverOutboxDelivery),
                         (correction.CorrelationId, AuditedCommandKind.CreateCorrectiveOutboxDelivery) })
            {
                var facts = await commandQuery.QueryAsync(new CommandTraceFilter(CorrelationId: expected.Item1));
                if (facts.Records.Count != 1 || facts.Records[0].CommandKind != expected.Item2 ||
                    facts.Records[0].Disposition != CommandDisposition.Rejected ||
                    facts.Records[0].AuthenticatedHumanPrincipalId is not null)
                    throw new InvalidOperationException("ProbeOperationRefusalFactMismatch");
            }
            operations = new { verificationId = "V153_N03", serviceResolved = true, queryAvailable = true,
                recoverDisposition = recoveryResult.Outcome.Disposition.ToString(),
                recoverReason = recoveryResult.Outcome.ReasonCode,
                correctionDisposition = correctionResult.Outcome.Disposition.ToString(),
                correctionReason = correctionResult.Outcome.ReasonCode,
                rejectionAuditsPersisted = true, immutablePayload = true, governanceUnchanged = true };
        }
#endif
#if SOURCE_36
        if (snapshot.Store.State == HealthState.Healthy)
        {
            // A healthy store alone cannot prove a preserved background feature works.
            // Exercise all image-query entry points; a schema guard omission must fail here
            // even when an alarm policy allows the store itself to remain healthy.
            var imageQuery = new SqliteProductionImageEvidenceQuery(options);
            var imagePage = await imageQuery.QueryAsync(new ProductionImageEvidenceFilter());
            var imageQueue = await imageQuery.ReadWorkQueueAsync();
            var imageBacklog = await imageQuery.ReadBacklogAsync();
            if (!imagePage.Available || !imageQueue.Available || imagePage.Items.Count != 0 || imageBacklog.Count != 0)
                throw new InvalidOperationException("ProbePreservedImagesUnavailable: " + imagePage.ReasonCode);
            preservedImages = new { available = imagePage.Available, queueAvailable = imageQueue.Available,
                items = imagePage.Items.Count, backlog = imageBacklog.Count,
                throughAuditSequence = imageBacklog.ThroughAuditSequence };
        }
#endif
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
        if (snapshot.Store.State == HealthState.Healthy && snapshot.Evidence.State == HealthState.Faulted)
            throw new InvalidOperationException("ProbePreservedEvidenceFaulted");
        if (snapshot.Ready) throw new InvalidOperationException("ProbeUnexpectedProductionReady");
    }
    var auditLedgerAfter = AuditLedgerDigest(options.DatabasePath);
    object? outboxWork = null;
#if CURRENT_MIGRATION
    var query = new SqliteProductionOutboxQuery(options);
    var page = await query.ReadPendingAsync();
    var backlog = await query.ReadBacklogAsync();
    outboxWork = new { available = page.Available, reason = page.ReasonCode, items = page.Items.Count,
        routeCount = options.Outbox!.Routes.Count, routeSetHash = options.Outbox.RouteSetHash,
        backlogRouteCount = backlog.Routes.Count, throughAuditSequence = backlog.ThroughAuditSequence,
        recipeLifecycle = options.RecipeLifecycle is not null, imageEvidence = options.ImageEvidence is not null,
        imageFinalization = options.ImageFinalization is not null };
#endif
    evidence = new { phase = 0, Ready = snapshot.Ready, state = snapshot.Store.State.ToString(),
        reason = snapshot.Store.ReasonCode, audit = result.Audit.ToString(), result.ReasonCode,
        auditIntegrity = snapshot.AuditIntegrity?.State.ToString(), schema = Schema(options.DatabasePath),
        postWriteAuditSequence, auditVerifiedThroughSequence = snapshot.AuditIntegrity?.VerifiedThroughSequence,
        assemblyHash = AssemblyHash(), auditLedgerBefore, auditLedgerAfter,
        auditLedgerChanged = auditLedgerBefore != auditLedgerAfter, outbox = outboxWork,
        evidenceState = snapshot.Evidence.State.ToString(), preservedImages, operations };
    WriteEvidence(Path.Combine(directory, mode + "-writer.json"), evidence);
}
Console.WriteLine(JsonSerializer.Serialize(evidence));

static ProductionStoreOptions Options(string directory)
{
    const string station = "V153.PackageProbe";
    var name = "SharpInspect.Test.V153.Probe." + Convert.ToHexString(SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(directory))).Substring(0, 24);
    var policy = new AuditIntegrityPolicy(station, "1", name)
    {
        AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
        CheckpointEveryEntries = 2, MaximumVerificationEntries = 10_000,
        VerificationInterval = TimeSpan.FromSeconds(1)
    };
    // Explicit source policy: newly introduced optional permissions must not
    // change the configuration of a store created by the preserved application.
    var authoring = new AuthorizationPolicy("V153.Probe.Authoring", "1",
        new Dictionary<HumanRoleBundle, IEnumerable<Permission>>
        {
            [HumanRoleBundle.Operator] = new[] { 5, 6, 29 }.Select(value => (Permission)value),
            [HumanRoleBundle.Technician] = new[] { 5, 6, 7, 11, 13, 23, 29, 30, 31 }.Select(value => (Permission)value),
            [HumanRoleBundle.Administrator] = Enumerable.Range(1, 31).Select(value => (Permission)value)
        }, Enumerable.Range(1, 30).Where(value => value is not 5 and not 6 and not 29)
            .Select(value => (Permission)value));
    var identity = new LocalIdentityOptions(station, new LocalPasswordPolicy
    {
        Blocklist = PasswordBlocklist.Create("V153.Probe.Blocklist", "1", new[] { "known-compromised-value" })
    }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, authoring);
#if SOURCE_36
    var lifecycle = new RecipeLifecycleStoreOptions();
#endif
#if SOURCE_36
    var stage = Path.Combine(directory, "image-stage");
    Directory.CreateDirectory(stage);
    var stageOptions = new SharpInspect.Runtime.Images.ProductionImageStageOptions(stage,
        512 * 1024, 32 * 1024 * 1024, 200);
    var imageEvidence = new ProductionImageEvidenceStoreOptions(stageOptions);
#endif
#if SOURCE_36
    var finalRoot = Path.Combine(directory, "image-final");
    Directory.CreateDirectory(finalRoot);
    var finalization = new ProductionImageFinalizationStoreOptions(
        new ProductionImageFinalizationRootOptions(finalRoot, 1024 * 1024, 64 * 1024 * 1024, 400), imageEvidence);
#endif
#if SOURCE_36
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
    var outbox = new ProductionOutboxStoreOptions(new[] { route },
#if SOURCE_36
        lifecycle,
#else
        null,
#endif
#if SOURCE_36
        imageEvidence,
#else
        null,
#endif
#if SOURCE_36
        finalization
#else
        null
#endif
    )
#if CURRENT_MIGRATION
    { ManualRecovery = new ProductionOutboxRecoveryOptions() }
#endif
    ;
#endif
    return new ProductionStoreOptions(Path.Combine(directory, "store.sqlite"))
    {
        AuditIntegrityPolicy = policy, LocalIdentity = identity,
        RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V153.Probe.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
        RecipeReleases = new RecipeReleaseStoreOptions(new RecipeGovernancePolicy(
            "V153.Probe.ReleaseGovernance", "1", RecipeGovernanceMode.MakerCheckerRelease)),
        ProductionInspections = new ProductionInspectionStoreOptions(),
        TraceStoragePolicies = new TraceStoragePolicyStoreOptions
        {
            DeploymentScope = new TraceStorageDeploymentScope("V153.Probe.Deployment", "1",
                Array.Empty<TraceStorageRouteIdentity>())
        },
        // The arm ledger's own store guard requires the admission ledger, so the
        // seed and target profiles both keep it (T49 carried it as well).
        ProductionAdmission = new ProductionAdmissionStoreOptions(),
        ProductionArming = new ProductionArmStoreOptions(),
#if SOURCE_36
        RecipeLifecycle = lifecycle,
#endif
#if SOURCE_36
        ImageEvidence = imageEvidence,
#endif
#if SOURCE_36
        ImageFinalization = finalization,
        // The same alarm mapping is compiled into both preserved and current
        // writers; finalization startup requires its recovery-bound integrity rule.
        AlarmPolicy = new AlarmPolicy("V153.Probe.Images.Alarm", "1", new[]
        {
            new AlarmPolicyRule("EvidenceIntegrityFault", "Runtime.ImageEvidence", AlarmSeverity.Error,
                ProductionImpact.BlockNewTriggers, true, AlarmNotification.None, null,
                ResetPrerequisites: AlarmResetPrerequisites.RecoveryComplete |
                    AlarmResetPrerequisites.NoActiveExecution),
            new AlarmPolicyRule("ImageEvidenceBacklog", "Runtime.ImageEvidence", AlarmSeverity.Warning,
                ProductionImpact.BlockNewTriggers, false, AlarmNotification.None, null)
        }, TimeSpan.FromSeconds(5)),
#endif
#if SOURCE_36
        Outbox = outbox,
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
