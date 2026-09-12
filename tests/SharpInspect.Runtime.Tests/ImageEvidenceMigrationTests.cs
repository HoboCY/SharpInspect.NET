using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T50 governed-migration checks for the schema-33 to schema-34 operation.
/// Every case uses the real exclusive startup-maintenance session, the real backup and
/// fingerprint proofs and the real signed audit chain, and never starts a Runtime,
/// camera, PLC or authorization fixture.
/// </summary>
public sealed class ImageEvidenceMigrationTests
{
    [Fact]
    [Trait("VerificationId", "V150_Schema04")]
    public async Task V150_Schema04_GovernedMigrationAddsTheImageEvidenceStoreAndPreservesOldFacts()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        await using var fixture = await ImageEvidenceFixture.CreateAsync(33, "V150-Schema04");
        var source = fixture.Fingerprint();
        var target34 = fixture.Profile(34);

        // An ordinary schema-33 open with the image evidence opt-in is a governed
        // migration, never an automatic table addition, and the files stay byte-identical.
        var sourceFacts = fixture.Scalar("SELECT COUNT(*) FROM command_facts;");
        await fixture.Store.DisposeAsync();
        var sourceBytes = File.ReadAllBytes(fixture.Options.DatabasePath);
        await using (var denied = new SqliteCommandStore(target34))
        {
            var initialized = await denied.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(initialized.Committed);
            Assert.Equal("ImageEvidenceGovernedMigrationRequired", initialized.ReasonCode);
            Assert.Equal(33, ImageEvidenceFixture.Scalar(fixture.Options.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(0, ImageEvidenceFixture.Scalar(fixture.Options.DatabasePath,
                "SELECT COUNT(*) FROM sqlite_master WHERE name='image_evidence_store_config';"));
            Assert.Equal(sourceBytes, File.ReadAllBytes(fixture.Options.DatabasePath));
        }

        var opened = await OpenAsync(target34);
        Assert.True(opened.Available, opened.Status.ReasonCode);
        Assert.Equal(33, opened.Status.SourceSchemaVersion);
        Assert.Equal(34, opened.Status.TargetSchemaVersion);
        Assert.False(opened.Status.Ready);
        var completedId = Guid.Empty;
        await using (var session = opened.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.False(completed.Ready);
            Assert.Equal("SchemaMigratedStartupReconciliationRequired", completed.ReasonCode);
            Assert.Equal(33, completed.SourceSchemaVersion);
            Assert.Equal(34, completed.TargetSchemaVersion);
            var backup = Assert.IsType<VerifiedStoreMigrationBackup>(completed.Backup);
            Assert.Equal(33, backup.SourceSchemaVersion);
            Assert.Equal(source.ContentHash, backup.SourceFingerprint);
            completedId = completed.OperationId;
        }

        // The declared target carries the exact three tables, the immutable configuration
        // row and exactly one signed activation entry, and every old row and rowid prefix
        // survived the constraint rebuild.
        Assert.Equal(34, ImageEvidenceFixture.Scalar(fixture.Options.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(sourceFacts, ImageEvidenceFixture.Scalar(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM command_facts;"));
        Assert.Equal(3, ImageEvidenceFixture.Scalar(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
            + "('image_evidence_store_config','pending_image_manifests','pending_image_work');"));
        Assert.Equal(1, ImageEvidenceFixture.Scalar(fixture.Options.DatabasePath,
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';"));
        using (var connection = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
            Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, source.Tables,
                ImageEvidenceFixture.Budget, fixture.Deadline));
        Assert.Equal(completedId, StoreMigrationJournalGuard.ReadMarker(fixture.Options.DatabasePath));
        StoreMigrationJournalGuard.RequireCompletedLineage(
            StoreMigrationJournal.Read(StoreMigrationJournalGuard.JournalPath(fixture.Options.DatabasePath),
                4L * 1024 * 1024)!.Data,
            completedId, target34, fixture.Options.DatabasePath);

        // The migrated generation now opens normally, verifies from the real store and
        // keeps the image evidence option mandatory; the schema-33 profile refuses it.
        await using (var writer = new SqliteCommandStore(target34))
        {
            var initialized = await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(writer);
        }

        await using (var stale = new SqliteCommandStore(fixture.Profile(33)))
        {
            var initialized = await stale.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(initialized.Committed);
            Assert.Equal("ImageEvidenceConfigurationRequired", initialized.ReasonCode);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_Schema05")]
    public async Task V150_Schema05_CompletedEarlierOperationIsContinuedByALinkedAppendOnlyOperation()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        await using var fixture = await ImageEvidenceFixture.CreateAsync(32, "V150-Schema05");
        await fixture.Store.DisposeAsync();
        var journalPath = StoreMigrationJournalGuard.JournalPath(fixture.Options.DatabasePath);
        var markerPath = StoreMigrationJournalGuard.MarkerPath(fixture.Options.DatabasePath);

        // The bundled prior operation (32 to 33) completes first.
        var opened = await OpenAsync(fixture.Profile(33));
        Assert.True(opened.Available, opened.Status.ReasonCode);
        var priorOperation = Guid.Empty;
        await using (var session = opened.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.True(completed.Completed, completed.ReasonCode);
            priorOperation = completed.OperationId;
        }
        var journalBefore = File.ReadAllBytes(journalPath);
        var markerBefore = File.ReadAllBytes(markerPath);
        var before = fixture.Fingerprint(fixture.Profile(33));
        var priorChain = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024);
        Assert.Equal(before.ContentHash, priorChain[^1].Data.TargetFingerprint);

        // The schema-33 source keeps the prior journal and marker exactly as they are,
        // and the next operation is appended to the same bounded file.
        var next = await OpenAsync(fixture.Profile(34));
        Assert.True(next.Available, next.Status.ReasonCode);
        Assert.NotEqual(priorOperation, next.Status.OperationId);
        Assert.Equal(33, next.Status.SourceSchemaVersion);
        Assert.Equal(34, next.Status.TargetSchemaVersion);
        var nextOperation = next.Status.OperationId;
        await using (var session = next.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.True(completed.Completed, completed.ReasonCode);
            Assert.Equal(33, completed.Backup!.SourceSchemaVersion);
        }

        // Old evidence is append-only: the first operation's frames are a byte-identical
        // prefix of the enlarged journal.
        var journalAfter = File.ReadAllBytes(journalPath);
        Assert.True(journalAfter.Length > journalBefore.Length);
        Assert.Equal(journalBefore, journalAfter.Take(journalBefore.Length).ToArray());
        var chain = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024);
        var openedFrames = chain.Where(value => value.Data.Phase == StoreMigrationPhase.Opened).ToArray();
        Assert.Equal(new[] { priorOperation, nextOperation }, openedFrames.Select(value => value.Data.OperationId));
        var linked = Assert.Single(chain, value => value.Data.OperationId == nextOperation &&
            value.Data.PreviousOperationId == priorOperation && value.Data.Phase == StoreMigrationPhase.Opened);
        Assert.Equal(priorOperation, linked.Data.PreviousOperationId);
        Assert.Equal(priorChain[^1].ContentHash, linked.Data.PreviousOperationJournalHash);
        StoreMigrationJournalGuard.RequireArchivedMarker(linked.Data.PreviousMarkerBase64!,
            fixture.Options.DatabasePath, priorOperation);
        Assert.NotEqual(Convert.ToHexString(markerBefore),
            Convert.ToHexString(File.ReadAllBytes(markerPath)));
        Assert.Equal(nextOperation, StoreMigrationJournalGuard.ReadMarker(fixture.Options.DatabasePath));

        // The chained operation's own source proof is the completed operation's target
        // generation, and the migrated generation opens normally afterwards.
        var sourceProof = chain.Last(value => value.Data.OperationId == nextOperation &&
            value.Data.SourceFingerprint is not null);
        Assert.Equal(before.ContentHash, sourceProof.Data.SourceFingerprint);
        await using var writer = new SqliteCommandStore(fixture.Profile(34));
        var initialized = await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(initialized.Committed, initialized.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V150_Schema06")]
    public async Task V150_Schema06_InterruptedOperationResumesFromItsOwnDurableEvidence()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        await using var fixture = await ImageEvidenceFixture.CreateAsync(33, "V150-Schema06");
        var source = fixture.Fingerprint();
        await fixture.Store.DisposeAsync();

        var opened = await OpenAsync(fixture.Profile(34));
        Assert.True(opened.Available, opened.Status.ReasonCode);
        var operation = opened.Status.OperationId;
        await StoreStartupMaintenanceTests.AdvanceTo(opened.Session!, StoreMigrationPhase.BackupVerified);
        await opened.Session!.DisposeAsync();

        // No database mutation precedes the verified transaction: the interrupted source
        // is still the exact schema-33 source and every ordinary open is refused.
        Assert.Equal(33, fixture.Fingerprint().SchemaVersion);
        Assert.Equal(source.ContentHash, fixture.Fingerprint().ContentHash);
        await using (var denied = new SqliteCommandStore(fixture.Profile(33)))
        {
            var initialized = await denied.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreMigrationStartupMaintenanceRequired", initialized.ReasonCode);
        }

        var resumed = await OpenAsync(fixture.Profile(34));
        Assert.True(resumed.Available, resumed.Status.ReasonCode);
        Assert.Equal(operation, resumed.Status.OperationId);
        await using (var session = resumed.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.True(completed.Completed, completed.ReasonCode);
            Assert.Equal(operation, completed.OperationId);
        }

        Assert.Equal(34, fixture.Fingerprint(fixture.Profile(34)).SchemaVersion);
        await using var writer = new SqliteCommandStore(fixture.Profile(34));
        Assert.True((await writer.Initialization).Committed);
    }

    [Fact]
    [Trait("VerificationId", "V150_Schema07")]
    public async Task V150_Schema07_ChainedOperationCompletesTheMarkerSwitchAfterACrashBeforeIt()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        await using var fixture = await ImageEvidenceFixture.CreateAsync(32, "V150-Schema07");
        await fixture.Store.DisposeAsync();

        // The prior schema-32 to schema-33 operation must complete, because only one
        // completed operation of the immediately preceding generation can be continued.
        var prior = await OpenAsync(fixture.Profile(33));
        Assert.True(prior.Available, prior.Status.ReasonCode);
        await using (var priorSession = prior.Session!)
            Assert.True((await StoreStartupMaintenanceTests.Finish(priorSession)).Completed);

        // The chained operation is opened once: its durable link frame is appended and
        // the permanent marker moves to the new operation.
        var opened = await OpenAsync(fixture.Profile(34));
        Assert.True(opened.Available, opened.Status.ReasonCode);
        var operation = opened.Status.OperationId;
        var journalPath = StoreMigrationJournalGuard.JournalPath(fixture.Options.DatabasePath);
        var markerPath = StoreMigrationJournalGuard.MarkerPath(fixture.Options.DatabasePath);
        await opened.Session!.DisposeAsync();

        // Fault model for a crash between the durable link frame and the marker switch:
        // the exact archived predecessor bytes of the marker are restored.
        var chain = StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024);
        var linked = chain.Single(value => value.Data.OperationId == operation);
        Assert.Equal(StoreMigrationPhase.Opened, linked.Data.Phase);
        File.WriteAllBytes(markerPath, Convert.FromBase64String(linked.Data.PreviousMarkerBase64!));
        Assert.NotEqual(operation, StoreMigrationJournalGuard.ReadMarker(fixture.Options.DatabasePath));

        // The durable provenance is re-proved and the switch is completed; no second
        // opened frame is invented and the operation finishes normally.
        var recovered = await OpenAsync(fixture.Profile(34));
        Assert.True(recovered.Available, recovered.Status.ReasonCode);
        Assert.Equal(operation, recovered.Status.OperationId);
        Assert.Equal(StoreMigrationPhase.SourceVerified, recovered.Status.Phase);
        Assert.Equal(operation, StoreMigrationJournalGuard.ReadMarker(fixture.Options.DatabasePath));
        await using (var session = recovered.Session!)
            Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
        Assert.Single(StoreMigrationJournal.ReadChain(journalPath, 4L * 1024 * 1024),
            value => value.Data.OperationId == operation && value.Data.Phase == StoreMigrationPhase.Opened);
        Assert.Equal(34, fixture.Fingerprint(fixture.Profile(34)).SchemaVersion);
    }

    [Theory]
    [InlineData((int)MigrationMarkerBoundary.AfterOperationWrite)]
    [InlineData((int)MigrationMarkerBoundary.BeforeFlush)]
    [InlineData((int)MigrationMarkerBoundary.AfterFlush)]
    [InlineData((int)MigrationMarkerBoundary.BeforeReplace)]
    [InlineData((int)MigrationMarkerBoundary.AfterReplace)]
    [Trait("VerificationId", "V150_Schema08")]
    public void V150_Schema08_InterruptedMarkerReplacementAlwaysRetainsACompleteMarker(int boundaryValue)
    {
        var directory = ImageEvidenceFixture.NewDirectory("V150-Schema08");
        try
        {
            var path = Path.Combine(directory, "marker-probe.sqlite");
            var oldOperation = Guid.NewGuid();
            var nextOperation = Guid.NewGuid();
            StoreMigrationJournalGuard.CreateMarker(path, oldOperation);
            var previous = StoreMigrationJournalGuard.ReadMarkerBytes(path);
            Assert.Throws<IOException>(() => StoreMigrationJournalGuard.ReplaceMarker(path, nextOperation,
                boundary => { if ((int)boundary == boundaryValue) throw new IOException("InjectedMarkerInterruption"); }));
            var expected = boundaryValue == (int)MigrationMarkerBoundary.AfterReplace ? nextOperation : oldOperation;
            Assert.Equal(expected, StoreMigrationJournalGuard.ReadMarker(path));
            StoreMigrationJournalGuard.RequireArchivedMarker(Convert.ToBase64String(previous), path, oldOperation);
            StoreMigrationJournalGuard.ReplaceMarker(path, nextOperation);
            Assert.Equal(nextOperation, StoreMigrationJournalGuard.ReadMarker(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    [Trait("VerificationId", "V150_Schema09")]
    public async Task V150_Schema09_InterruptedMigrationCannotResumeWithDifferentImageBindings()
    {
        await using var fixture = await ImageEvidenceFixture.CreateAsync(33, "V150-Schema09");
        await fixture.Store.DisposeAsync();
        var target = fixture.Profile(34);
        var open = await OpenAsync(target);
        Assert.True(open.Available, open.Status.ReasonCode);
        await open.Session!.DisposeAsync();
        var path = StoreMigrationJournalGuard.JournalPath(target.DatabasePath);
        var before = File.ReadAllBytes(path);
        var different = ImageEvidenceFixture.WithEvidence(target,
            new ProductionImageEvidenceStoreOptions(target.ImageEvidence!.Stage) { MaxImages = 9_999 });
        var denied = await OpenAsync(different);
        Assert.False(denied.Available);
        Assert.Equal("StoreMigrationResumeContextMismatch", denied.Status.ReasonCode);
        Assert.Equal(before, File.ReadAllBytes(path));
        var resume = await OpenAsync(target);
        Assert.True(resume.Available, resume.Status.ReasonCode);
        await using var session = resume.Session!;
        Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
    }

    private static ValueTask<StoreStartupMaintenanceOpenResult> OpenAsync(ProductionStoreOptions target) =>
        SqliteStartupMaintenance.OpenAsync(target,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location)
            {
                MaximumDatabaseBytes = ImageEvidenceFixture.Budget,
                OperationTimeout = TimeSpan.FromSeconds(30)
            });
}
