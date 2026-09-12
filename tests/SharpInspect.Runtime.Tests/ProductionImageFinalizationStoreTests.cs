using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T51 schema-35 checks for the production image finalization lifecycle store. Every
/// case opens a real SQLite store and the real signed central-audit chain, but never starts a
/// Runtime, camera, PLC, stager or authorization fixture: no image bytes, no Core commit and no
/// verified commit claim can be produced here, so the reachable cases prove the declared
/// generation, the immutable facts, the configuration binding, the codec, the query capability,
/// the governed migration and the fail-closed tamper behaviour. The Succeeded and StageReleased
/// transitions require the main-owned verified claim and are exercised by the primary.
/// </summary>
public sealed class ProductionImageFinalizationStoreTests
{
    [Fact]
    [Trait("VerificationId", "V151_Schema01")]
    public async Task V151_Schema01_MinimalSchema35InitializesColdReadsAndRestarts()
    {
        await using var fixture = await ImageFinalizationFixture.CreateAsync("V151-Schema01");

        Assert.Equal(35, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(3, fixture.FinalizationTableCount());
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM image_finalization_store_config;"));
        Assert.Equal(1, fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM image_finalization_events;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM image_finalization_work;"));
        Assert.Equal(fixture.Options.ImageFinalization!.BindingHash,
            fixture.Text("SELECT BindingHash FROM image_finalization_store_config WHERE Id=1;"));
        Assert.Equal(fixture.Options.ImageFinalization.FinalRootBindingHash,
            fixture.Text("SELECT FinalRootBindingHash FROM image_finalization_store_config WHERE Id=1;"));
        Assert.Equal("1", fixture.Text("SELECT WorkerCount FROM image_finalization_store_config WHERE Id=1;"));

        // The declared initial facts are immutable and the cold read proves the whole chain.
        Assert.Equal(5, fixture.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN "
            + "('image_finalization_config_immutable_update','image_finalization_config_immutable_delete',"
            + "'image_finalization_event_immutable_update','image_finalization_event_immutable_delete',"
            + "'image_finalization_event_insert');"));
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
        {
            SqliteCommandStore.RequireConfiguredImageFinalization(read.Handle!,
                fixture.Options.ImageFinalization, fixture.Deadline);
            SqliteCommandStore.VerifyImageFinalizationReadGuard(read.Handle!, fixture.Options, fixture.Deadline);
        }

        var report = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Verified, report.State);
        Assert.Equal(1, report.VerifiedFromSequence);
        Assert.Equal(fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;"),
            report.VerifiedThroughSequence);

        // The verified query capability exposes an empty, watermarked backlog and queue.
        var backlog = await fixture.Query.ReadBacklogAsync();
        Assert.Equal(0, backlog.Count);
        Assert.Equal(0, backlog.Bytes);
        Assert.Null(backlog.OldestCreatedAtUtc);
        Assert.Equal(report.VerifiedThroughSequence, backlog.ThroughAuditSequence);
        var queue = await fixture.Query.ReadWorkQueueAsync();
        Assert.True(queue.Available, queue.ReasonCode);
        Assert.Empty(queue.Pending);
        Assert.Empty(queue.SucceededNotReleased);
        Assert.Equal(backlog.ThroughAuditSequence, queue.ThroughAuditSequence);
        var page = await fixture.Query.QueryAsync(new ProductionImageEvidenceFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Items);

        // The immutable configuration refuses an edit even while the store is open.
        await fixture.Store.DisposeAsync();
        var rejected = Assert.ThrowsAny<Exception>(() =>
            fixture.Execute("UPDATE image_finalization_store_config SET MaximumEvents=MaximumEvents+1 WHERE Id=1;"));
        Assert.Contains("ImmutableImageFinalizationConfiguration", rejected.Message, StringComparison.Ordinal);
        await fixture.StartAsync();
        await fixture.RestartAsync();
        Assert.Equal(35, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(AuditIntegrityState.Verified, (await fixture.VerifyAsync()).State);
    }

    [Fact]
    [Trait("VerificationId", "V151_Schema02")]
    public async Task V151_Schema02_OptInRequiresImageEvidenceAndTheExactDeclaredTableSet()
    {
        var directory = ImageEvidenceFixture.NewDirectory("V151-Schema02");

        // The finalization opt-in alone is rejected before any file is opened; it requires
        // the image evidence store and therefore the whole production image stack.
        var unqualified = new ProductionStoreOptions(Path.Combine(directory, "unused.sqlite"))
        {
            ImageFinalization = ImageFinalizationFixture.Finalization(directory,
                ImageEvidenceFixture.BuildOptions(directory, "V151Schema02Unqualified", 34)
                    .ImageEvidence!)
        };
        Assert.StartsWith("ImageFinalizationRequiresImageEvidenceAndProductionStack",
            Assert.Throws<ArgumentException>(() => new SqliteCommandStore(unqualified)).Message);

        // Every declared limit is validated, and the root must be an existing qualified root.
        var complete = ImageFinalizationFixture.BuildOptions(directory, "V151Schema02Station");
        var root = complete.ImageFinalization!.FinalRoot;
        Assert.Contains("ImageFinalizationAttemptCapacityInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageFinalizationFixture.WithFinalization(complete,
                new ProductionImageFinalizationStoreOptions(root, complete.ImageEvidence!)
                { MaximumAttempts = 0 }))).Message);
        Assert.Contains("ImageFinalizationEntryCapacityInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageFinalizationFixture.WithFinalization(complete,
                new ProductionImageFinalizationStoreOptions(root, complete.ImageEvidence!)
                { MaximumEvents = 0 }))).Message);
        Assert.Contains("ImageFinalizationPageCapacityInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageFinalizationFixture.WithFinalization(complete,
                new ProductionImageFinalizationStoreOptions(root, complete.ImageEvidence!)
                { MaximumPageSize = 0 }))).Message);
        Assert.Contains("ImageFinalizationRetryDelayInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageFinalizationFixture.WithFinalization(complete,
                new ProductionImageFinalizationStoreOptions(root, complete.ImageEvidence!)
                { MaximumRetryDelay = TimeSpan.Zero }))).Message);
        Assert.Contains("ProductionImageFinalizationRootMissing", Assert.Throws<ArgumentException>(
            () => new ProductionImageFinalizationRootOptions(Path.Combine(directory, "missing-root"),
                1024, 2048, 8)).Message);

        // A declared option whose canonical tables are not exactly the declared set never
        // reaches a projection, in either direction.
        await using var fixture = await ImageFinalizationFixture.CreateAsync("V151-Schema02Store");
        await fixture.Store.DisposeAsync();
        fixture.Execute("CREATE TABLE image_finalization_extra(Id INTEGER PRIMARY KEY);");
        await using (var rejected = new SqliteCommandStore(fixture.Options))
        {
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreSchemaMismatch", initialized.ReasonCode);
        }
        fixture.Execute("DROP TABLE image_finalization_extra;");
        fixture.Execute("DROP TABLE image_finalization_work;");
        var missingProjection = await fixture.TryOpenAsync();
        Assert.False(missingProjection.Committed, missingProjection.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V151_Schema03")]
    public async Task V151_Schema03_Schema34StoresStayLetteredAndFailClosedBothWays()
    {
        var directory = ImageEvidenceFixture.NewDirectory("V151-Schema03");
        var options34 = ImageEvidenceFixture.BuildOptions(directory, "V151Schema03Station", 34);
        await using (var store = new SqliteCommandStore(options34))
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        using (var connection = SqliteNative.Open(options34.DatabasePath, readOnly: true))
        {
            Assert.Equal(34, AuditChainDatabase.Scalar(connection.Handle!, "PRAGMA user_version;",
                new StoreDeadline(TimeSpan.FromSeconds(30))));
            Assert.Equal(0, AuditChainDatabase.Scalar(connection.Handle!,
                "SELECT COUNT(*) FROM sqlite_master WHERE name IN "
                + "('image_finalization_store_config','image_finalization_events','image_finalization_work');",
                new StoreDeadline(TimeSpan.FromSeconds(30))));
        }
        var sourceBytes = File.ReadAllBytes(options34.DatabasePath);

        // The same store declared with the finalization option is a governed migration, never
        // an automatic table addition, and the file stays byte-identical.
        var finalization = ImageFinalizationFixture.Finalization(directory, options34.ImageEvidence!);
        var target35 = ImageFinalizationFixture.WithFinalization(options34, finalization);
        await using (var denied = new SqliteCommandStore(target35))
        {
            var initialized = await denied.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("ImageFinalizationGovernedMigrationRequired", initialized.ReasonCode);
        }
        Assert.Equal(sourceBytes, File.ReadAllBytes(options34.DatabasePath));

        // The schema-34 store remains fully usable without the option.
        await using (var store = new SqliteCommandStore(options34))
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);

        // A schema-35 store refuses a caller that only declares image evidence.
        await using var migrated = await ImageFinalizationFixture.CreateAsync("V151-Schema03Migrated");
        await migrated.Store.DisposeAsync();
        var evidenceOnly = ImageFinalizationFixture.WithFinalization(migrated.Options, null);
        await using (var denied = new SqliteCommandStore(evidenceOnly))
        {
            var initialized = await denied.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("ImageFinalizationConfigurationRequired", initialized.ReasonCode);
        }

        // The verified query capability refuses the older generation instead of returning
        // an empty projection.
        var page = await new SqliteProductionImageEvidenceQuery(target35)
            .QueryAsync(new ProductionImageEvidenceFilter());
        Assert.False(page.Available);
        Assert.Equal("ImageFinalizationGovernedMigrationRequired", page.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V151_Schema04")]
    public async Task V151_Schema04_GovernedMigrationAddsTheFinalizationLedgerAndPreservesOldFacts()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        var directory = ImageEvidenceFixture.NewDirectory("V151-Schema04");
        var options34 = ImageEvidenceFixture.BuildOptions(directory, "V151Schema04Station", 34);
        await using (var store = new SqliteCommandStore(options34))
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        var fingerprint = Fingerprint(options34);
        var sourceFacts = Scalar(options34.DatabasePath, "SELECT COUNT(*) FROM command_facts;");

        var target35 = ImageFinalizationFixture.WithFinalization(options34,
            ImageFinalizationFixture.Finalization(directory, options34.ImageEvidence!));
        var opened = await ImageFinalizationFixture.OpenAsync(target35);
        Assert.True(opened.Available, opened.Status.ReasonCode);
        Assert.Equal(34, opened.Status.SourceSchemaVersion);
        Assert.Equal(35, opened.Status.TargetSchemaVersion);
        Guid operationId;
        await using (var session = opened.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.False(completed.Ready);
            Assert.Equal("SchemaMigratedStartupReconciliationRequired", completed.ReasonCode);
            Assert.Equal(34, completed.SourceSchemaVersion);
            Assert.Equal(35, completed.TargetSchemaVersion);
            Assert.Equal(34, Assert.IsType<VerifiedStoreMigrationBackup>(completed.Backup).SourceSchemaVersion);
            operationId = completed.OperationId;
        }

        // The target generation carries exactly the declared tables, the immutable
        // configuration row and the single signed activation evidence, and every old fact
        // survived the constraint rebuild unchanged.
        Assert.Equal(35, Scalar(options34.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(sourceFacts, Scalar(options34.DatabasePath, "SELECT COUNT(*) FROM command_facts;"));
        Assert.Equal(3, Scalar(options34.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' "
            + "AND name IN ('image_finalization_store_config','image_finalization_events',"
            + "'image_finalization_work');"));
        Assert.Equal(1, Scalar(options34.DatabasePath,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';"));
        Assert.Equal(1, Scalar(options34.DatabasePath,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';"));
        using (var connection = SqliteNative.Open(options34.DatabasePath, readOnly: true))
        {
            var deadline = new StoreDeadline(TimeSpan.FromSeconds(30));
            Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, fingerprint.Tables,
                ImageEvidenceFixture.Budget, deadline));
            // The finalization facts are signed metadata entries with all typed positions NULL.
            Assert.Equal(0, AuditChainDatabase.Scalar(connection.Handle!,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated' "
                + "AND (FactPosition IS NOT NULL OR IdentityPosition IS NOT NULL OR "
                + "ProductionInspectionPosition IS NOT NULL OR PartIdentityPosition IS NOT NULL);", deadline));
        }
        Assert.Equal(operationId, StoreMigrationJournalGuard.ReadMarker(options34.DatabasePath));
        StoreMigrationJournalGuard.RequireCompletedLineage(
            StoreMigrationJournal.Read(StoreMigrationJournalGuard.JournalPath(options34.DatabasePath),
                4L * 1024 * 1024)!.Data,
            operationId, target35, options34.DatabasePath);

        // The migrated store opens normally and its verified query capability is available;
        // the previous generation profile refuses the new store.
        await using (var writer = new SqliteCommandStore(target35))
        {
            Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
            Assert.Equal(AuditIntegrityState.Verified, (await new SqliteAuditIntegrityQuery(target35)
                .VerifyAsync(new AuditVerificationRequest(0, 9_900))).State);
        }
        await using (var stale = new SqliteCommandStore(options34))
        {
            var initialized = await stale.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("ImageFinalizationConfigurationRequired", initialized.ReasonCode);
        }
    }

    [Fact]
    [Trait("VerificationId", "V151_Schema05")]
    public async Task V151_Schema05_CompletedSchema34OperationIsContinuedByALinkedOperation()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        var directory = ImageEvidenceFixture.NewDirectory("V151-Schema05");
        var options34 = ImageEvidenceFixture.BuildOptions(directory, "V151Schema05Station", 34);
        await using (var store = new SqliteCommandStore(options34))
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        var journalPath = StoreMigrationJournalGuard.JournalPath(options34.DatabasePath);
        var markerPath = StoreMigrationJournalGuard.MarkerPath(options34.DatabasePath);

        // The schema-34 source itself starts no migration: the marker is created only by the
        // 34-to-35 operation.
        Assert.False(StoreMigrationJournalGuard.Exists(journalPath));
        var target35 = ImageFinalizationFixture.WithFinalization(options34,
            ImageFinalizationFixture.Finalization(directory, options34.ImageEvidence!));
        var opened = await ImageFinalizationFixture.OpenAsync(target35);
        Assert.True(opened.Available, opened.Status.ReasonCode);
        Guid firstOperation;
        await using (var session = opened.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.True(completed.Completed, completed.ReasonCode);
            firstOperation = completed.OperationId;
        }
        var journal = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024);
        Assert.All(journal, entry => Assert.Equal(StoreMigrationJournal.ImageFinalizationPlanId,
            entry.Data.PlanId));
        Assert.Equal(firstOperation, StoreMigrationJournalGuard.ReadMarker(options34.DatabasePath));
        Assert.Equal(35, Scalar(options34.DatabasePath, "PRAGMA user_version;"));

        // The completed operation cannot be repeated: the same generation is already reached.
        var repeated = await ImageFinalizationFixture.OpenAsync(target35);
        Assert.True(repeated.Available, repeated.Status.ReasonCode);
        Assert.Equal(StoreMigrationPhase.Completed, repeated.Status.Phase);
        await repeated.Session!.DisposeAsync();
    }

    [Fact]
    [Trait("VerificationId", "V151_Schema06")]
    public async Task V151_Schema06_InterruptedMigrationResumesFromItsOwnDurableEvidence()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        var directory = ImageEvidenceFixture.NewDirectory("V151-Schema06");
        var options34 = ImageEvidenceFixture.BuildOptions(directory, "V151Schema06Station", 34);
        await using (var store = new SqliteCommandStore(options34))
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        var fingerprint = Fingerprint(options34);
        var target35 = ImageFinalizationFixture.WithFinalization(options34,
            ImageFinalizationFixture.Finalization(directory, options34.ImageEvidence!));

        var opened = await ImageFinalizationFixture.OpenAsync(target35);
        Assert.True(opened.Available, opened.Status.ReasonCode);
        var operation = opened.Status.OperationId;
        await StoreStartupMaintenanceTests.AdvanceTo(opened.Session!, StoreMigrationPhase.BackupVerified);
        await opened.Session!.DisposeAsync();

        // No source mutation precedes the verified transaction: the interrupted source is
        // still the exact schema-34 source and ordinary opens are refused.
        Assert.Equal(34, Fingerprint(options34).SchemaVersion);
        Assert.Equal(fingerprint.ContentHash, Fingerprint(options34).ContentHash);
        await using (var denied = new SqliteCommandStore(options34))
        {
            var initialized = await denied.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreMigrationStartupMaintenanceRequired", initialized.ReasonCode);
        }

        var resumed = await ImageFinalizationFixture.OpenAsync(target35);
        Assert.True(resumed.Available, resumed.Status.ReasonCode);
        Assert.Equal(operation, resumed.Status.OperationId);
        await using (var session = resumed.Session!)
            Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
        Assert.Equal(35, Fingerprint(target35).SchemaVersion);

        // An interrupted operation cannot resume with a different finalization binding.
        var otherDirectory = ImageEvidenceFixture.NewDirectory("V151-Schema06b");
        var other34 = ImageEvidenceFixture.BuildOptions(otherDirectory, "V151Schema06bStation", 34);
        await using (var store = new SqliteCommandStore(other34))
            Assert.True((await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20))).Committed);
        var other35 = ImageFinalizationFixture.WithFinalization(other34,
            ImageFinalizationFixture.Finalization(otherDirectory, other34.ImageEvidence!));
        var first = await ImageFinalizationFixture.OpenAsync(other35);
        Assert.True(first.Available, first.Status.ReasonCode);
        await first.Session!.DisposeAsync();
        var journalBefore = File.ReadAllBytes(StoreMigrationJournalGuard.JournalPath(other34.DatabasePath));
        var different = ImageFinalizationFixture.WithFinalization(other34,
            ImageFinalizationFixture.Finalization(otherDirectory, other34.ImageEvidence!,
                maximumAttempts: 9));
        var denied2 = await ImageFinalizationFixture.OpenAsync(different);
        Assert.False(denied2.Available);
        Assert.Equal("StoreMigrationResumeContextMismatch", denied2.Status.ReasonCode);
        Assert.Equal(journalBefore,
            File.ReadAllBytes(StoreMigrationJournalGuard.JournalPath(other34.DatabasePath)));
    }

    [Fact]
    [Trait("VerificationId", "V151_Schema07")]
    public async Task V151_Schema07_ConfigurationAndActivationTamperFailsClosed()
    {
        await using var fixture = await ImageFinalizationFixture.CreateAsync("V151-Schema07");

        // An offline edit of the immutable configuration row is detected by the signed binding
        // before any fact is returned.
        await fixture.Store.DisposeAsync();
        fixture.Execute("DROP TRIGGER image_finalization_config_immutable_update;");
        fixture.Execute("UPDATE image_finalization_store_config SET MaximumEvents=MaximumEvents+1 WHERE Id=1;");
        var edited = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, edited.State);
        Assert.Contains("ImageFinalizationConfigurationMismatch", edited.ReasonCode, StringComparison.Ordinal);
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
        {
            var rejected = Assert.Throws<InvalidOperationException>(() =>
                SqliteCommandStore.VerifyImageFinalizationReadGuard(read.Handle!, fixture.Options,
                    fixture.Deadline));
            Assert.Contains("ImageFinalizationConfigurationMismatch", rejected.Message, StringComparison.Ordinal);
        }
        fixture.Execute("UPDATE image_finalization_store_config SET MaximumEvents=MaximumEvents-1 WHERE Id=1;");
        Assert.Equal(AuditIntegrityState.Verified, (await fixture.VerifyAsync()).State);

        // Removing the single signed activation entry fails the exact once-presence proof, and
        // the read guard refuses the store.
        fixture.Execute("DROP TRIGGER audit_entries_immutable_delete;");
        fixture.Execute("DELETE FROM audit_entries WHERE Kind='ImageFinalizationStoreActivated';");
        var removed = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, removed.State);
        Assert.Contains("ImageFinalizationActivationMissing", removed.ReasonCode, StringComparison.Ordinal);
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
            Assert.Throws<InvalidOperationException>(() =>
                SqliteCommandStore.VerifyImageFinalizationReadGuard(read.Handle!, fixture.Options,
                    fixture.Deadline));
    }

    [Fact]
    [Trait("VerificationId", "V151_Finalization01")]
    public async Task V151_Finalization01_MissingWorkAndMissingClaimAreRejectedWithoutBlessingFiles()
    {
        await using var fixture = await ImageFinalizationFixture.CreateAsync("V151-Finalization01");
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(30));
        var workId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // An attempt for an obligation that was never committed by a Core is refused after the
        // full chain proof, so no caller can invent finalization work.
        var missing = await fixture.Store.BeginImageFinalizationAttemptAsync(
            new ImageFinalizationAttemptStartRequest(workId, Guid.NewGuid(), 1, epoch, now, false),
            deadline);
        Assert.False(missing.Committed);
        Assert.Equal("ImageFinalizationWorkRequired", missing.ReasonCode);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM image_finalization_events;"));

        // An invalid request identity is refused before any transaction is opened.
        var invalid = await fixture.Store.BeginImageFinalizationAttemptAsync(
            new ImageFinalizationAttemptStartRequest(Guid.Empty, Guid.NewGuid(), 1, epoch, now, false),
            deadline);
        Assert.False(invalid.Committed);
        Assert.Equal("ImageFinalizationRequestIdentityInvalid", invalid.ReasonCode);

        // A failure outcome without its started attempt is refused.
        var failure = await fixture.Store.AppendImageFinalizationOutcomeAsync(
            new ImageFinalizationFailureRequest(workId, Guid.NewGuid(), epoch, now,
                "ImageFinalizationTransientWriteFailure", ProductionImageFailureCategory.Temporary,
                now.AddSeconds(5), false), deadline);
        Assert.False(failure.Committed);
        Assert.Equal("ImageFinalizationWorkRequired", failure.ReasonCode);

        // A succeeded outcome without the main-owned verified commit claim is refused: there is
        // deliberately no stub or factory that could bless an arbitrary file.
        var blessing = await fixture.Store.AppendImageFinalizationOutcomeAsync(
            new ImageFinalizationSuccessRequest(workId, epoch, now, null!), deadline);
        Assert.False(blessing.Committed);
        Assert.Equal("ImageFinalizationCommitClaimRequired", blessing.ReasonCode);

        // A release without Succeeded is refused and never reverts a projection.
        var release = await fixture.Store.RecordImageStageReleasedAsync(
            new ImageFinalizationReleaseRequest(workId, epoch, now, "ImageFinalizationStageRetained"),
            deadline);
        Assert.False(release.Committed);
        Assert.Equal("ImageFinalizationWorkRequired", release.ReasonCode);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM image_finalization_events;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM image_finalization_work;"));
    }

    [Fact]
    [Trait("VerificationId", "V151_Finalization02")]
    public void V151_Finalization02_EventCodecIsCanonicalBoundedAndTamperEvident()
    {
        var manifestId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var inspectionId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var recorded = new DateTimeOffset(2026, 9, 12, 1, 2, 3, TimeSpan.Zero);
        var attempt = new ProductionImageAttemptDescriptor(attemptId, 1, 5, 4,
            new string('a', 64), ProductionImageFinalizationStorageCodec.StableFinalFileName(manifestId),
            ProductionImageFinalizationStorageCodec.UniqueTemporaryFileName(manifestId, attemptId));
        Assert.Equal(manifestId.ToString("N") + ".png", attempt.FinalFileName);
        Assert.EndsWith(".tmp", attempt.TemporaryFileName);

        var failure = new ProductionImageFailureDescriptor(ProductionImageFailureCategory.Temporary,
            recorded.AddSeconds(3));
        var eventId = ProductionImageFinalizationStorageCodec.DeriveEventId(1, workId, manifestId,
            inspectionId, new string('b', 64), new string('c', 64), 1, attemptId, 1, epoch, recorded,
            ProductionImageFinalizationKind.AttemptFailed, "ImageFinalizationEncodeFailed", attempt, failure, null);
        var value = new ProductionImageFinalizationEvent(1, eventId, workId, manifestId, inspectionId,
            new string('b', 64), new string('c', 64), 1, attemptId, 1, epoch, recorded,
            ProductionImageFinalizationKind.AttemptFailed, "ImageFinalizationEncodeFailed", attempt,
            failure, null, 7, new string('d', 64));
        var payload = ProductionImageFinalizationStorageCodec.EncodeEvent(value);
        var decoded = ProductionImageFinalizationStorageCodec.DecodeEvent(payload);
        Assert.Equal(value.ContentHash, decoded.ContentHash);
        Assert.Equal(value.EventId, decoded.EventId);
        Assert.Equal(value.AttemptId, decoded.AttemptId);
        Assert.Equal(ProductionImageFinalizationKind.AttemptFailed, decoded.Kind);
        Assert.Equal(ProductionImageFailureCategory.Temporary, decoded.Failure!.Category);
        Assert.Equal(recorded.AddSeconds(3), decoded.Failure.RetryAfterUtc);
        Assert.Equal(7, decoded.AuditSequence);
        Assert.Equal(SystemPrincipalId.Runtime, decoded.SystemPrincipalId);

        // The audit binding excludes the audit reference but includes the exact content hash.
        var binding = ProductionImageFinalizationStorageCodec.EncodeAuditPayload(decoded);
        var sameBinding = ProductionImageFinalizationStorageCodec.EncodeAuditPayload(
            ProductionImageFinalizationStorageCodec.DecodeEvent(payload));
        Assert.Equal(binding, sameBinding);

        // A tampered byte fails the deterministic identity and content-hash proof.
        var tampered = payload.ToArray();
        tampered[^1] ^= 0x01;
        Assert.ThrowsAny<ArgumentException>(() =>
            ProductionImageFinalizationStorageCodec.DecodeEvent(tampered));

        // An event can never be persisted without its audit reference, and a failure with an
        // integrity category can never carry a retry instant.
        var provisional = new ProductionImageFinalizationEvent(1, eventId, workId, manifestId,
            inspectionId, new string('b', 64), new string('c', 64), 1, attemptId, 1, epoch, recorded,
            ProductionImageFinalizationKind.AttemptFailed, "ImageFinalizationEncodeFailed", attempt,
            failure, null);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionImageFinalizationStorageCodec.EncodeEvent(provisional));
        Assert.Throws<ArgumentException>(() => new ProductionImageFailureDescriptor(
            ProductionImageFailureCategory.Integrity, recorded.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => new ProductionImageAttemptDescriptor(attemptId, 5, 5, 5,
            new string('a', 64), attempt.FinalFileName, attempt.TemporaryFileName));
    }

    [Fact]
    [Trait("VerificationId", "V151_Finalization03")]
    public async Task V151_Finalization03_AuditMembershipAndBacklogStayBoundedAtOneWatermark()
    {
        await using var fixture = await ImageFinalizationFixture.CreateAsync("V151-Finalization03");

        // An extra signed metadata entry without a stored fact fails the exact bidirectional
        // count proof: the ledger can never claim a finalization fact it cannot show.
        await fixture.Store.DisposeAsync();
        fixture.Execute("INSERT INTO audit_entries(Sequence,Kind,Payload,PreviousHash,Hash) "
            + "VALUES(901,'ImageFinalizationSucceeded','AAAA','" + new string('0', 64) + "','"
            + new string('1', 64) + "');");
        var tampered = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, tampered.State);
        Assert.False(string.IsNullOrEmpty(tampered.ReasonCode));
        fixture.Execute("DROP TRIGGER audit_entries_immutable_delete;");
        fixture.Execute("DELETE FROM audit_entries WHERE Sequence=901;");

        // The restored store verifies and its watermark advances with every committed fact.
        Assert.Equal(AuditIntegrityState.Verified, (await fixture.VerifyAsync()).State);
        var first = await fixture.Query.ReadBacklogAsync();
        Assert.Equal(
            fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;"),
            first.ThroughAuditSequence);

        // The worker queue is bounded by the caller page size and never returns more than the
        // declared bound.
        var queue = await fixture.Query.ReadWorkQueueAsync(1);
        Assert.True(queue.Available, queue.ReasonCode);
        Assert.True(queue.Pending.Count <= 1);
        Assert.True(queue.SucceededNotReleased.Count <= 1);
        Assert.Equal(first.ThroughAuditSequence, queue.ThroughAuditSequence);
    }

    private static long Scalar(string databasePath, string sql) =>
        ImageEvidenceFixture.Scalar(databasePath, sql);

    private static MigrationDatabaseFingerprint Fingerprint(ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, options);
        return StoreMigrationFingerprint.Read(connection.Handle!, ImageEvidenceFixture.Budget,
            new StoreDeadline(TimeSpan.FromSeconds(30)));
    }
}

/// <summary>
/// A bounded production store on the Windows machine-audit key at schema 35: the schema-34
/// image evidence stack plus the schema-35 finalization opt-in. It never provisions a human
/// account, a camera, a stage claim or a Core commit, so every case observes only the declared
/// generation, the signed central audit and the fail-closed write and query behaviour.
/// </summary>
internal sealed class ImageFinalizationFixture : IAsyncDisposable
{
    internal const string Station = "V151ImageFinalizationStation";
    private readonly string _directory;
    private bool _disposed;

    private ImageFinalizationFixture(string directory, ProductionStoreOptions options)
    {
        _directory = directory;
        Options = options;
        Query = new SqliteProductionImageEvidenceQuery(options);
    }

    internal ProductionStoreOptions Options { get; }
    internal IProductionImageEvidenceQuery Query { get; }
    internal SqliteCommandStore Store { get; private set; } = null!;
    internal StoreDeadline Deadline { get; } = new(TimeSpan.FromSeconds(30));

    internal static ProductionImageFinalizationRootOptions Root(string directory,
        int maximumAttempts = 5)
    {
        var root = Path.Combine(directory, "final");
        Directory.CreateDirectory(root);
        return new ProductionImageFinalizationRootOptions(root, 512L * 1024 * 1024,
            1024L * 1024 * 1024, 10_000);
    }

    internal static ProductionImageFinalizationStoreOptions Finalization(string directory,
        ProductionImageEvidenceStoreOptions imageEvidence, int maximumAttempts = 5) =>
        new(Root(directory), imageEvidence) { MaximumAttempts = maximumAttempts };

    internal static ProductionStoreOptions BuildOptions(string directory, string station)
    {
        var evidence = ImageEvidenceFixture.BuildOptions(directory, station, 34);
        return WithFinalization(evidence, Finalization(directory, evidence.ImageEvidence!));
    }

    internal static ProductionStoreOptions WithFinalization(ProductionStoreOptions source,
        ProductionImageFinalizationStoreOptions? finalization) => new(source.DatabasePath)
    {
        AuditIntegrityPolicy = source.AuditIntegrityPolicy,
        LocalIdentity = source.LocalIdentity,
        RecipeDrafts = source.RecipeDrafts,
        RecipeReleases = source.RecipeReleases,
        ProductionAdmission = source.ProductionAdmission,
        ProductionArming = source.ProductionArming,
        ProductionInspections = source.ProductionInspections,
        RecipeLifecycle = source.RecipeLifecycle,
        ImageEvidence = source.ImageEvidence,
        ImageFinalization = finalization,
        CommitTimeout = source.CommitTimeout,
        QueryTimeout = source.QueryTimeout,
        QueueCapacity = source.QueueCapacity
    };

    internal static ValueTask<StoreStartupMaintenanceOpenResult> OpenAsync(
        ProductionStoreOptions target) =>
        SqliteStartupMaintenance.OpenAsync(target,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location)
            {
                MaximumDatabaseBytes = ImageEvidenceFixture.Budget,
                OperationTimeout = TimeSpan.FromSeconds(30)
            });

    internal static async Task<ImageFinalizationFixture> CreateAsync(string label)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Image finalization storage requires Windows machine protection.");
        var directory = ImageEvidenceFixture.NewDirectory(label);
        var fixture = new ImageFinalizationFixture(directory,
            BuildOptions(directory, Station));
        try
        {
            await fixture.StartAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal async Task StartAsync()
    {
        Store = new SqliteCommandStore(Options);
        var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(initialized.Committed, initialized.ReasonCode);
    }

    internal async Task<StoreWriteResult> TryOpenAsync()
    {
        await using var store = new SqliteCommandStore(Options);
        return await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
    }

    internal async Task RestartAsync()
    {
        await Store.DisposeAsync();
        await StartAsync();
    }

    internal long Scalar(string sql) => ImageEvidenceFixture.Scalar(Options.DatabasePath, sql);

    internal string Text(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Options.DatabasePath
            + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    internal long FinalizationTableCount() => Scalar(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
        + "('image_finalization_store_config','image_finalization_events','image_finalization_work');");

    internal ValueTask<AuditIntegrityReport> VerifyAsync() => new SqliteAuditIntegrityQuery(Options)
        .VerifyAsync(new AuditVerificationRequest(0, 9_900));

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Options.DatabasePath + ";Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (Store is not null) await Store.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
