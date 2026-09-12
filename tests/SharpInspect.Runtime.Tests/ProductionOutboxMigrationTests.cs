using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T52 focused migration tests: the governed schema-32/33/34/35 to schema-36 plans, the exact
/// per-plan journal binding and a real source-profile migration fixture for every governed
/// generation. A migration preserves exactly the optional profile its source carried and adds
/// only the outbox; a source generation without a governed plan (schema 28 to 31 uses an earlier
/// audit envelope) fails closed without any rewrite.
/// </summary>
public sealed class ProductionOutboxMigrationTests
{
    private const long Budget = 64L * 1024 * 1024;

    private static StoreDeadline Deadline() => new(TimeSpan.FromSeconds(30));

    [Fact]
    [Trait("VerificationId", "V152_M01")]
    public void V152_M01_EachGovernedPlanBindsItsExactSourceGenerationAndOptionalFeatures()
    {
        // The seven supported operations are one exact (plan id, source, target, feature set)
        // tuple each; no other combination is a plan.
        AssertPlan(32, 33, StoreMigrationJournal.LifecyclePlanId, new(true, false, false, false));
        AssertPlan(33, 34, StoreMigrationJournal.ImageEvidencePlanId, new(true, true, false, false));
        AssertPlan(34, 35, StoreMigrationJournal.ImageFinalizationPlanId, new(true, true, true, false));
        AssertPlan(32, 36, StoreMigrationJournal.ProductionOutbox32PlanId, new(false, false, false, true));
        AssertPlan(33, 36, StoreMigrationJournal.ProductionOutbox33PlanId, new(true, false, false, true));
        AssertPlan(34, 36, StoreMigrationJournal.ProductionOutbox34PlanId, new(true, true, false, true));
        AssertPlan(35, 36, StoreMigrationJournal.ProductionOutboxPlanId, new(true, true, true, true));
        // A schema-36 operation can never be claimed by another source generation, and a source
        // generation that only an earlier audit envelope describes has no governed plan at all.
        Assert.False(StoreMigrationJournal.TryResolvePlan(33, 36,
            StoreMigrationJournal.ProductionOutbox32PlanId, out _));
        Assert.False(StoreMigrationJournal.TryResolvePlan(32, 36,
            StoreMigrationJournal.ProductionOutboxPlanId, out _));
        Assert.False(StoreMigrationJournal.TryResolvePlan(35, 35,
            StoreMigrationJournal.ProductionOutboxPlanId, out _));
        Assert.False(StoreMigrationJournal.TryResolvePlan(36, 36,
            StoreMigrationJournal.ProductionOutboxPlanId, out _));
        foreach (var generation in new[] { 28, 29, 30, 31 })
        {
            Assert.False(StoreMigrationJournal.TryResolvePlan(generation, 36,
                StoreMigrationJournal.ProductionOutbox32PlanId, out _));
            Assert.False(StoreMigrationJournal.TryResolvePlan(generation, 36,
                StoreMigrationJournal.ProductionOutbox33PlanId, out _));
        }

        // A schema-32 source declares the production Core, the trace policy and the arm ledger
        // and nothing else: neither the lifecycle ledger nor the draft ledger is forced on it.
        var directory = ProductionOutboxStorageTests.TestDirectory("V152-m01");
        try
        {
            var databasePath = Path.Combine(directory, "store.sqlite");
            var policy = Policy("V152M01Station", directory);
            var identity = Identity("V152M01Station");
            var admission = new ProductionAdmissionStoreOptions();
            var arm = new ProductionArmStoreOptions();
            var inspections = new ProductionInspectionStoreOptions();
            var tracePolicies = TracePolicies("V152.M01");
            var outbox = Outbox();
            var target32 = new ProductionStoreOptions(databasePath)
            {
                AuditIntegrityPolicy = policy,
                LocalIdentity = identity,
                ProductionAdmission = admission,
                ProductionArming = arm,
                ProductionInspections = inspections,
                TraceStoragePolicies = tracePolicies,
                Outbox = outbox
            };
            using (var profile = new SqliteCommandStore.StartupMaintenanceSchema(target32,
                ProductionOutboxStoreOptions.SchemaVersion))
                Assert.Equal(36, profile.Version);
            var source32 = SqliteCommandStore.MigrationProductionOutboxSourceOptions(target32);
            Assert.Null(source32.Outbox);
            Assert.Same(policy, source32.AuditIntegrityPolicy);
            Assert.Same(identity, source32.LocalIdentity);
            Assert.Same(arm, source32.ProductionArming);
            Assert.Same(admission, source32.ProductionAdmission);
            Assert.Same(inspections, source32.ProductionInspections);
            Assert.Same(tracePolicies, source32.TraceStoragePolicies);
            Assert.Null(source32.RecipeDrafts);
            Assert.Null(source32.RecipeLifecycle);
            Assert.Null(source32.ImageEvidence);
            Assert.Null(source32.ImageFinalization);
            using (var source = new SqliteCommandStore.StartupMaintenanceSchema(source32,
                ProductionOutboxStoreOptions.SchemaVersion))
                Assert.Equal(32, source.Version);

            // A schema-33 source keeps its lifecycle ledger and still forces nothing else.
            var legacy33 = LegacyProfile(directory, "V152M01Station", 33);
            var target33 = Copy(legacy33, tracePolicies, Outbox(legacy33.RecipeLifecycle));
            var source33 = SqliteCommandStore.MigrationProductionOutboxSourceOptions(target33);
            Assert.Same(target33.RecipeLifecycle, source33.RecipeLifecycle);
            using (var source = new SqliteCommandStore.StartupMaintenanceSchema(source33,
                ProductionOutboxStoreOptions.SchemaVersion))
                Assert.Equal(33, source.Version);

            // An outbox target on the schema-28 production Core foundation declares no lifecycle,
            // no image feature, no draft ledger and no arm ledger; the schema-36 target model
            // still constructs, and only the migration plan refuses the too-old source.
            var coreTarget = new ProductionStoreOptions(databasePath)
            {
                AuditIntegrityPolicy = policy,
                LocalIdentity = identity,
                ProductionInspections = inspections,
                TraceStoragePolicies = tracePolicies,
                Outbox = outbox
            };
            using (var profile = new SqliteCommandStore.StartupMaintenanceSchema(coreTarget,
                ProductionOutboxStoreOptions.SchemaVersion))
                Assert.Equal(36, profile.Version);
            var coreSource = SqliteCommandStore.MigrationProductionOutboxSourceOptions(coreTarget);
            using (var source = new SqliteCommandStore.StartupMaintenanceSchema(coreSource,
                ProductionOutboxStoreOptions.SchemaVersion))
                Assert.Equal(28, source.Version);
        }
        finally
        {
            Remove(directory);
        }
    }

    [Theory]
    [InlineData(StoreMigrationJournal.ProductionOutbox32PlanId, 32, false, false, false)]
    [InlineData(StoreMigrationJournal.ProductionOutbox33PlanId, 33, true, false, false)]
    [InlineData(StoreMigrationJournal.ProductionOutbox34PlanId, 34, true, true, false)]
    [InlineData(StoreMigrationJournal.ProductionOutboxPlanId, 35, true, true, true)]
    [Trait("VerificationId", "V152_M02")]
    public void V152_M02_JournalRequiresTheExactOptionalBindingsOfItsPlan(string planId, int sourceVersion,
        bool lifecycle, bool evidence, bool finalization)
    {
        var directory = ProductionOutboxStorageTests.TestDirectory("V152-m02");
        try
        {
            var databasePath = Path.Combine(directory, "store.sqlite");
            var applicationPath = typeof(SqliteCommandStore).Assembly.Location;
            var record = new StoreMigrationJournalData
            {
                OperationId = Guid.NewGuid(),
                Attempt = 1,
                Phase = StoreMigrationPhase.Opened,
                PlanId = planId,
                DatabasePath = databasePath,
                SourceSchemaVersion = sourceVersion,
                TargetSchemaVersion = ProductionOutboxStoreOptions.SchemaVersion,
                SourceApplicationPath = applicationPath,
                SourceApplicationVersion = "1.0.0.0",
                SourceApplicationSha256 = new string('A', 64),
                TargetApplicationVersion = "1.0.0.0",
                TargetApplicationSha256 = new string('B', 64),
                LifecycleConfigurationHash = lifecycle ? new string('C', 64) : null,
                ImageEvidenceConfigurationHash = evidence ? new string('E', 64) : null,
                ImageFinalizationConfigurationHash = finalization ? new string('F', 64) : null,
                ProductionOutboxConfigurationHash = new string('D', 64),
                ReasonCode = "StoreMigrationOpened"
            };
            using var journal = new StoreMigrationJournal(Path.Combine(directory, "outbox.journal"),
                4L * 1024 * 1024, create: true);
            var appended = journal.Append(record);
            Assert.Equal(planId, appended.Data.PlanId);
            Assert.Equal(sourceVersion, appended.Data.SourceSchemaVersion);
            Assert.Equal(ProductionOutboxStoreOptions.SchemaVersion, appended.Data.TargetSchemaVersion);
            Assert.Equal(new string('D', 64), appended.Data.ProductionOutboxConfigurationHash);
            // The outbox binding is mandatory for every schema-36 plan.
            Assert.Throws<InvalidOperationException>(() => journal.Append(record with
            { ProductionOutboxConfigurationHash = null }));
            // No plan may claim an optional binding it does not carry, or omit one it declares.
            Assert.Throws<InvalidOperationException>(() => journal.Append(record with
            { LifecycleConfigurationHash = lifecycle ? null : new string('C', 64) }));
            Assert.Throws<InvalidOperationException>(() => journal.Append(record with
            { ImageEvidenceConfigurationHash = evidence ? null : new string('E', 64) }));
            Assert.Throws<InvalidOperationException>(() => journal.Append(record with
            { ImageFinalizationConfigurationHash = finalization ? null : new string('F', 64) }));
            // An operation cannot change its own outbox binding later in the same attempt, and no
            // earlier generation may claim an outbox binding at all.
            Assert.Throws<InvalidOperationException>(() => journal.Append(record with
            { ProductionOutboxConfigurationHash = new string('9', 64) }));
            Assert.Throws<InvalidOperationException>(() => journal.Append(new StoreMigrationJournalData
            {
                OperationId = Guid.NewGuid(),
                Attempt = 1,
                Phase = StoreMigrationPhase.Opened,
                PlanId = StoreMigrationJournal.ImageFinalizationPlanId,
                DatabasePath = databasePath,
                SourceSchemaVersion = 34,
                TargetSchemaVersion = 35,
                SourceApplicationPath = applicationPath,
                SourceApplicationVersion = "1.0.0.0",
                SourceApplicationSha256 = new string('A', 64),
                TargetApplicationVersion = "1.0.0.0",
                TargetApplicationSha256 = new string('B', 64),
                LifecycleConfigurationHash = new string('C', 64),
                ImageEvidenceConfigurationHash = new string('E', 64),
                ImageFinalizationConfigurationHash = new string('F', 64),
                ProductionOutboxConfigurationHash = new string('D', 64),
                ReasonCode = "StoreMigrationOpened"
            }));
            // The plan id and the schema pair stay one exact operation.
            Assert.Throws<InvalidOperationException>(() => journal.Append(record with
            { PlanId = StoreMigrationJournal.ImageFinalizationPlanId }));
        }
        finally
        {
            Remove(directory);
        }
    }

    [Theory]
    [InlineData(32, StoreMigrationJournal.ProductionOutbox32PlanId)]
    [InlineData(33, StoreMigrationJournal.ProductionOutbox33PlanId)]
    [InlineData(34, StoreMigrationJournal.ProductionOutbox34PlanId)]
    [InlineData(35, StoreMigrationJournal.ProductionOutboxPlanId)]
    [Trait("VerificationId", "V152_M03")]
    public async Task V152_M03_GovernedSourceGenerationMigratesTo36WithItsExactOptionalProfile(
        int generation, string planId)
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V152-M03-" + generation);
        try
        {
            var tracePolicies = TracePolicies("V152.M03");
            var source = SourceProfile(directory, "V152M03" + generation, generation, tracePolicies);
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            var fingerprint = Fingerprint(source);
            Assert.Equal(generation, fingerprint.SchemaVersion);

            var outbox = OutboxFor(source);
            var target = Copy(source, tracePolicies, outbox);
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            Assert.Equal(generation, opened.Status.SourceSchemaVersion);
            Assert.Equal(36, opened.Status.TargetSchemaVersion);
            Guid operationId;
            await using (var session = opened.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                Assert.Equal(generation, completed.SourceSchemaVersion);
                Assert.Equal(36, completed.TargetSchemaVersion);
                operationId = completed.OperationId;
            }

            // The target generation owns the outbox ledger and nothing else: the exact optional
            // profile of the source survives and every old row and rowid prefix is unchanged.
            Assert.Equal(36L, Scalar(target.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(4L, Scalar(target.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE "
                + "type='table' AND name IN ('production_outbox_store_config','production_outbox_deliveries',"
                + "'production_outbox_events','production_outbox_work');"));
            Assert.Equal(1L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionOutboxStoreActivated';"));
            Assert.Equal(generation >= 33 ? 1L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';"));
            Assert.Equal(generation >= 34 ? 1L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';"));
            Assert.Equal(generation >= 35 ? 1L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';"));
            Assert.Equal(generation >= 33 ? 2L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
                + "('recipe_lifecycle_store_config','recipe_lifecycle_events');"));
            Assert.Equal(generation >= 34 ? 3L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
                + "('image_evidence_store_config','pending_image_manifests','pending_image_work');"));
            Assert.Equal(generation >= 35 ? 3L : 0L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
                + "('image_finalization_store_config','image_finalization_events','image_finalization_work');"));
            using (var connection = SqliteNative.Open(target.DatabasePath, readOnly: true))
                Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!,
                    fingerprint.Tables, Budget, Deadline()));

            // The durable journal binds this exact plan and the exact optional profile of the
            // source generation, never a forced lifecycle or image binding.
            var journalPath = StoreMigrationJournalGuard.JournalPath(target.DatabasePath);
            var last = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024)[^1].Data;
            Assert.Equal(planId, last.PlanId);
            Assert.Equal(generation, last.SourceSchemaVersion);
            Assert.Equal(36, last.TargetSchemaVersion);
            Assert.Equal(outbox.BindingHash, last.ProductionOutboxConfigurationHash);
            Assert.Equal(generation >= 33
                    ? StoreMigrationJournalGuard.LifecycleHash(target.RecipeLifecycle!)
                    : null,
                last.LifecycleConfigurationHash);
            Assert.Equal(generation >= 34 ? target.ImageEvidence!.BindingHash : null,
                last.ImageEvidenceConfigurationHash);
            Assert.Equal(generation >= 35 ? target.ImageFinalization!.BindingHash : null,
                last.ImageFinalizationConfigurationHash);
            Assert.Equal(operationId, StoreMigrationJournalGuard.ReadMarker(target.DatabasePath));
            StoreMigrationJournalGuard.RequireCompletedLineage(last, operationId, target,
                target.DatabasePath);
            var foreign = Copy(target, tracePolicies, OutboxFor(target));
            Assert.Throws<InvalidOperationException>(() => StoreMigrationJournalGuard
                .RequireCompletedLineage(last, operationId, foreign, target.DatabasePath));

            // The migrated generation opens normally with its declared optional profile and its
            // verified outbox query reads an available, empty backlog.
            await using (var writer = new SqliteCommandStore(target))
            {
                var initialized = await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(writer);
            }
            var query = new SqliteProductionOutboxQuery(target);
            var pending = await query.ReadPendingAsync();
            Assert.True(pending.Available, pending.ReasonCode);
            Assert.Empty(pending.Items);
            var backlog = await query.ReadBacklogAsync();
            Assert.Empty(backlog.Routes); // No delivery obligations were invented by migration.
            Assert.True(backlog.ThroughAuditSequence > 0);
            if (target.ImageEvidence is not null)
            {
                using var imageConnection = SqliteNative.Open(target.DatabasePath, readOnly: true);
                var withoutOutbox = Copy(target, tracePolicies, null);
                SqliteCommandStore.VerifyImageEvidenceReadGuard(imageConnection.Handle!, target, Deadline());
                Assert.Equal("ProductionOutboxConfigurationRequired", Assert.Throws<InvalidOperationException>(() =>
                    SqliteCommandStore.VerifyImageEvidenceReadGuard(imageConnection.Handle!, withoutOutbox, Deadline())).Message);
                if (target.ImageFinalization is not null)
                {
                    SqliteCommandStore.VerifyImageFinalizationReadGuard(imageConnection.Handle!, target, Deadline());
                    Assert.Equal("ProductionOutboxConfigurationRequired", Assert.Throws<InvalidOperationException>(() =>
                        SqliteCommandStore.VerifyImageFinalizationReadGuard(imageConnection.Handle!, withoutOutbox, Deadline())).Message);
                }
            }
            if (target.ImageFinalization is not null)
            {
                // Preserving tables is insufficient: the old feature's public reader and
                // actual replay worker must accept the new schema and verify its Outbox too.
                var images = new SqliteProductionImageEvidenceQuery(target);
                var imagePage = await images.QueryAsync(new ProductionImageEvidenceFilter());
                Assert.True(imagePage.Available, imagePage.ReasonCode);
                Assert.Empty(imagePage.Items);
                var imageQueue = await images.ReadWorkQueueAsync();
                Assert.True(imageQueue.Available, imageQueue.ReasonCode);
                Assert.Equal(0, (await images.ReadBacklogAsync()).Count);
                await using var imageStore = new SqliteCommandStore(target);
                var imageWorker = new ProductionImageFinalizationWorker(imageStore, target, Guid.NewGuid(),
                    imageStore.Initialization, _ => { }, _ => Task.CompletedTask);
                try
                {
                    await imageWorker.Startup.WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.Null(imageWorker.FailureReason);
                }
                finally { Assert.True(await imageWorker.StopAsync()); }
            }
        }
        finally
        {
            Remove(directory);
        }
    }

    [Theory]
    [InlineData(StoreMigrationPhase.Opened)]
    [InlineData(StoreMigrationPhase.BackupVerified)]
    [InlineData(StoreMigrationPhase.FeatureInitialized)]
    [InlineData(StoreMigrationPhase.DatabaseCommitted)]
    [Trait("VerificationId", "V152_M04")]
    public async Task V152_M04_InterruptedSchema33To36OperationResumesWithItsOwnPlan(StoreMigrationPhase phase)
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V152-M04");
        try
        {
            var tracePolicies = TracePolicies("V152.M04");
            var source = SourceProfile(directory, "V152M04Station", 33, tracePolicies);
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            var target = Copy(source, tracePolicies, OutboxFor(source));
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            var operation = opened.Status.OperationId;
            Assert.Equal(StoreMigrationJournal.ProductionOutbox33PlanId, LastPlanId(target));
            await StoreStartupMaintenanceTests.AdvanceTo(opened.Session!, phase);
            await opened.Session!.DisposeAsync();

            var resumed = await OpenAsync(target);
            Assert.True(resumed.Available, resumed.Status.ReasonCode);
            Assert.Equal(operation, resumed.Status.OperationId);
            Assert.Equal(33, resumed.Status.SourceSchemaVersion);
            Assert.Equal(36, resumed.Status.TargetSchemaVersion);
            await using (var session = resumed.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                Assert.Equal(operation, completed.OperationId);
            }
            Assert.Equal(36L, Scalar(target.DatabasePath, "PRAGMA user_version;"));
            var frames = StoreMigrationJournal.ReadChain(
                StoreMigrationJournalGuard.JournalPath(target.DatabasePath), 4L * 1024 * 1024);
            Assert.All(frames, frame => Assert.Equal(StoreMigrationJournal.ProductionOutbox33PlanId,
                frame.Data.PlanId));
            StoreMigrationJournalGuard.RequireCompletedLineage(frames[^1].Data, operation, target,
                target.DatabasePath);
        }
        finally
        {
            Remove(directory);
        }
    }

    [Fact]
    [Trait("VerificationId", "V152_M05")]
    public async Task V152_M05_ChangedSourceIdentityOrOptionalBindingNeverContinuesTheOperation()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V152-M05");
        try
        {
            var tracePolicies = TracePolicies("V152.M05");
            var source = SourceProfile(directory, "V152M05Station", 33, tracePolicies);
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            var target = Copy(source, tracePolicies, OutboxFor(source));
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            var operation = opened.Status.OperationId;
            await StoreStartupMaintenanceTests.AdvanceTo(opened.Session!, StoreMigrationPhase.BackupVerified);
            await opened.Session!.DisposeAsync();
            var journalPath = StoreMigrationJournalGuard.JournalPath(target.DatabasePath);
            var before = File.ReadAllBytes(journalPath);

            // Another copy of the same runtime assembly is a different source application
            // identity and never continues the durable operation.
            var otherSource = Path.Combine(directory, "other-runtime.dll");
            File.Copy(typeof(SqliteStartupMaintenance).Assembly.Location, otherSource);
            var deniedIdentity = await SqliteStartupMaintenance.OpenAsync(target, Maintenance(otherSource));
            Assert.False(deniedIdentity.Available);
            Assert.Equal("StoreMigrationResumeContextMismatch", deniedIdentity.Status.ReasonCode);
            Assert.Equal(before, File.ReadAllBytes(journalPath));

            // A changed lifecycle binding of the same generation, rebound consistently in the
            // declared outbox, is refused by the exact optional lineage of the durable operation.
            var changedLifecycle = new RecipeLifecycleStoreOptions
            {
                MaxEvents = target.RecipeLifecycle!.MaxEvents + 1
            };
            var deniedBinding = await OpenAsync(Copy(target, target.TraceStoragePolicies!,
                Outbox(changedLifecycle, target.ImageEvidence, target.ImageFinalization),
                replaceLifecycle: true, lifecycle: changedLifecycle));
            Assert.False(deniedBinding.Available);
            Assert.Equal("StoreMigrationResumeContextMismatch", deniedBinding.Status.ReasonCode);
            Assert.Equal(before, File.ReadAllBytes(journalPath));

            var resumed = await OpenAsync(target);
            Assert.True(resumed.Available, resumed.Status.ReasonCode);
            Assert.Equal(operation, resumed.Status.OperationId);
            Assert.Equal(33, resumed.Status.SourceSchemaVersion);
            Assert.Equal(36, resumed.Status.TargetSchemaVersion);
            await using (var session = resumed.Session!)
                Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
            Assert.Equal(36L, Scalar(target.DatabasePath, "PRAGMA user_version;"));
        }
        finally
        {
            Remove(directory);
        }
    }

    [Fact]
    [Trait("VerificationId", "V152_M07")]
    public async Task V152_M07_CompletedPriorGenerationIsContinuedOnlyByItsExactSchema36Plan()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V152-M07");
        try
        {
            var tracePolicies = TracePolicies("V152.M07");
            var source = Copy(LegacyProfile(directory, "V152M07Station", 32), tracePolicies, null);
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            // The bundled schema-32 to schema-33 operation completes first on its own journal.
            var target33 = Copy(source, tracePolicies, null, replaceLifecycle: true,
                lifecycle: new RecipeLifecycleStoreOptions());
            var prior = await OpenAsync(target33);
            Assert.True(prior.Available, prior.Status.ReasonCode);
            Guid priorOperation;
            await using (var session = prior.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                priorOperation = completed.OperationId;
            }
            Assert.Equal(33L, Scalar(source.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(StoreMigrationJournal.LifecyclePlanId, LastPlanId(source));
            var journalPath = StoreMigrationJournalGuard.JournalPath(source.DatabasePath);
            var beforeContinue = File.ReadAllBytes(journalPath);

            // A schema-36 operation continues only that completed generation, and only with the
            // exact lifecycle binding the durable record proved: a changed ledger is refused.
            var withOutbox = Copy(target33, tracePolicies, OutboxFor(target33));
            var changedLifecycle = new RecipeLifecycleStoreOptions
            {
                MaxEvents = target33.RecipeLifecycle!.MaxEvents + 1
            };
            var denied = await OpenAsync(Copy(withOutbox, tracePolicies,
                Outbox(changedLifecycle), replaceLifecycle: true, lifecycle: changedLifecycle));
            Assert.False(denied.Available);
            Assert.Equal("StoreMigrationJournalConfigurationMismatch", denied.Status.ReasonCode);
            Assert.Equal(beforeContinue, File.ReadAllBytes(journalPath));

            // The exact continuation appends one linked operation to the same append-only file.
            var next = await OpenAsync(withOutbox);
            Assert.True(next.Available, next.Status.ReasonCode);
            Assert.NotEqual(priorOperation, next.Status.OperationId);
            Assert.Equal(33, next.Status.SourceSchemaVersion);
            Assert.Equal(36, next.Status.TargetSchemaVersion);
            var linked = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024);
            Assert.Contains(linked, frame => frame.Data.OperationId == next.Status.OperationId &&
                frame.Data.PreviousOperationId == priorOperation &&
                frame.Data.PlanId == StoreMigrationJournal.ProductionOutbox33PlanId);
            await using (var session = next.Session!)
                Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
            Assert.Equal(36L, Scalar(source.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(1L, Scalar(source.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';"));
            Assert.Equal(1L, Scalar(source.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionOutboxStoreActivated';"));
            StoreMigrationJournalGuard.RequireCompletedLineage(
                StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024)[^1].Data,
                next.Status.OperationId, withOutbox, source.DatabasePath);
        }
        finally
        {
            Remove(directory);
        }
    }

    [Fact]
    [Trait("VerificationId", "V152_M06")]
    public async Task V152_M06_SourceGenerationWithoutAGoverningPlanIsRefusedWithoutARewrite()
    {
        var directory = ProductionOutboxStorageTests.TestDirectory("V152-m06");
        try
        {
            var databasePath = Path.Combine(directory, "store.sqlite");
            var source = new ProductionStoreOptions(databasePath)
            {
                AuditIntegrityPolicy = Policy("V152M06Station", directory),
                LocalIdentity = Identity("V152M06Station"),
                ProductionInspections = new ProductionInspectionStoreOptions(),
                TraceStoragePolicies = TracePolicies("V152.M06"),
                CommitTimeout = TimeSpan.FromSeconds(3),
                QueryTimeout = TimeSpan.FromSeconds(3),
                QueueCapacity = 8
            };
            // The schema-28 production Core foundation is only reachable outside Windows through
            // a real store, so the fixture needs the Windows NTFS and machine-key profile.
            RequireWindowsMigrationFixture();
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            Assert.Equal(28L, Scalar(databasePath, "PRAGMA user_version;"));
            var before = File.ReadAllBytes(databasePath);

            // The schema-28 source uses an earlier audit envelope: the governed plan refuses the
            // generation explicitly and the store is left exactly as it was.
            var denied = await OpenAsync(Copy(source, TracePolicies("V152.M06"), Outbox()));
            Assert.False(denied.Available);
            Assert.Equal("StoreMigrationSourceGenerationUnsupported", denied.Status.ReasonCode);
            Assert.Equal(36, denied.Status.TargetSchemaVersion);
            Assert.Equal(before, File.ReadAllBytes(databasePath));
            Assert.False(StoreMigrationJournalGuard.Exists(
                StoreMigrationJournalGuard.JournalPath(databasePath)));
            Assert.False(StoreMigrationJournalGuard.Exists(
                StoreMigrationJournalGuard.MarkerPath(databasePath)));
        }
        finally
        {
            Remove(directory);
        }
    }

    private static void RequireWindowsMigrationFixture()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip(
                "Migration fixtures require the Windows NTFS and machine-key store profile.");
    }

    private static void AssertPlan(int sourceVersion, int targetVersion, string planId,
        StoreMigrationPlanBinding expected)
    {
        Assert.True(StoreMigrationJournal.TryResolvePlan(sourceVersion, targetVersion, planId,
            out var binding), planId);
        Assert.Equal(expected, binding);
    }

    private static string LastPlanId(ProductionStoreOptions target) => StoreMigrationJournal
        .ReadChain(StoreMigrationJournalGuard.JournalPath(target.DatabasePath), 4L * 1024 * 1024)[^1]
        .Data.PlanId;

    /// <summary>
    /// The frozen deployment route set of this fixture, bound to the same optional legacy feature
    /// presence the declaring store carries: the outbox option must re-prove exactly that.
    /// </summary>
    private static ProductionOutboxStoreOptions Outbox(RecipeLifecycleStoreOptions? lifecycle = null,
        ProductionImageEvidenceStoreOptions? evidence = null,
        ProductionImageFinalizationStoreOptions? finalization = null) => new(new[]
    {
        ProductionOutboxStorageTests.Route("route.primary", OutboxRouteCriticality.Required, 4096)
    }, lifecycle, evidence, finalization);

    private static ProductionOutboxStoreOptions OutboxFor(ProductionStoreOptions profile) =>
        Outbox(profile.RecipeLifecycle, profile.ImageEvidence, profile.ImageFinalization);

    private static TraceStoragePolicyStoreOptions TracePolicies(string label) => new()
    {
        DeploymentScope = new TraceStorageDeploymentScope(label + ".Deployment", "1",
            Array.Empty<TraceStorageRouteIdentity>())
    };

    private static AuditIntegrityPolicy Policy(string station, string directory) =>
        new(station, "v1", "SharpInspect.Test.V152.Migration." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            MaximumVerificationEntries = 10_000,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };

    private static LocalIdentityOptions Identity(string station) => new(station,
        new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("v152-migration-blocklist", "v1",
                new[] { "known-compromised" })
        }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
        RecipeDraftTestPolicies.Authoring);

    /// <summary>
    /// The real source profile of one governed generation: the production Core plus exactly the
    /// optional features that generation carries, the trace policy a schema-36 target requires
    /// and nothing else. Every feature above the generation stays absent.
    /// </summary>
    private static ProductionStoreOptions SourceProfile(string directory, string station, int generation,
        TraceStoragePolicyStoreOptions tracePolicies)
    {
        var options = generation >= 34
            ? ImageEvidenceFixture.BuildOptions(directory, station, generation)
            : LegacyProfile(directory, station, generation);
        var withImages = generation >= 35
            ? ImageFinalizationFixture.WithFinalization(options,
                ImageFinalizationFixture.Finalization(directory, options.ImageEvidence!))
            : options;
        return Copy(withImages, tracePolicies, null);
    }

    private static ProductionStoreOptions LegacyProfile(string directory, string station, int generation) =>
        new(Path.Combine(directory, "outbox-migration.sqlite"))
        {
            AuditIntegrityPolicy = Policy(station, directory),
            LocalIdentity = Identity(station),
            RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V152.Migration",
                "1", TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
            ProductionAdmission = new ProductionAdmissionStoreOptions(),
            ProductionArming = new ProductionArmStoreOptions(),
            ProductionInspections = new ProductionInspectionStoreOptions(),
            RecipeLifecycle = generation >= 33 ? new RecipeLifecycleStoreOptions() : null,
            CommitTimeout = TimeSpan.FromSeconds(5),
            QueryTimeout = TimeSpan.FromSeconds(5),
            QueueCapacity = 8
        };

    /// <summary>
    /// The declared target of one migration: the exact source profile with the trace policy the
    /// outbox requires, the declared outbox and, when asked, a rebound lifecycle ledger.
    /// </summary>
    private static ProductionStoreOptions Copy(ProductionStoreOptions source,
        TraceStoragePolicyStoreOptions tracePolicies, ProductionOutboxStoreOptions? outbox,
        bool replaceLifecycle = false, RecipeLifecycleStoreOptions? lifecycle = null) =>
        new(source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            AlarmPolicy = source.AlarmPolicy,
            ExternalAuditAnchor = source.ExternalAuditAnchor,
            AlgorithmResultArchive = source.AlgorithmResultArchive,
            RecipeDrafts = source.RecipeDrafts,
            CameraSetup = source.CameraSetup,
            CameraRecovery = source.CameraRecovery,
            CameraNetwork = source.CameraNetwork,
            ImagingSetup = source.ImagingSetup,
            CalibrationSessions = source.CalibrationSessions,
            CalibrationGovernance = source.CalibrationGovernance,
            RecipeReleases = source.RecipeReleases,
            PlcResultContracts = source.PlcResultContracts,
            RecipeActivations = source.RecipeActivations,
            PreviewSessions = source.PreviewSessions,
            CalibrationImports = source.CalibrationImports,
            ManualInspections = source.ManualInspections,
            ProductionAdmission = source.ProductionAdmission,
            StationQualifications = source.StationQualifications,
            RecipeTransfers = source.RecipeTransfers,
            TraceStoragePolicies = tracePolicies,
            QualificationCycles = source.QualificationCycles,
            PlcCommunication = source.PlcCommunication,
            ProductionInspections = source.ProductionInspections,
            PartIdentities = source.PartIdentities,
            ProductionRecovery = source.ProductionRecovery,
            RecipeSelections = source.RecipeSelections,
            ProductionArming = source.ProductionArming,
            RecipeLifecycle = replaceLifecycle ? lifecycle : source.RecipeLifecycle,
            ImageEvidence = source.ImageEvidence,
            ImageFinalization = source.ImageFinalization,
            Outbox = outbox,
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity
        };

    private static StoreStartupMaintenanceOptions Maintenance(string sourceAssemblyPath) =>
        new(sourceAssemblyPath)
        {
            MaximumDatabaseBytes = Budget,
            OperationTimeout = TimeSpan.FromSeconds(30)
        };

    private static ValueTask<StoreStartupMaintenanceOpenResult> OpenAsync(ProductionStoreOptions target) =>
        SqliteStartupMaintenance.OpenAsync(target,
            Maintenance(typeof(SqliteStartupMaintenance).Assembly.Location));

    private static MigrationDatabaseFingerprint Fingerprint(ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, options);
        return StoreMigrationFingerprint.Read(connection.Handle!, Budget, Deadline());
    }

    private static long Scalar(string path, string sql) => ImageEvidenceFixture.Scalar(path, sql);

    private static void Remove(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
