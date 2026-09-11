using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class StoreStartupMaintenanceTests
{
    [Fact]
    [Trait("VerificationId", "V149_M01")]
    public async Task V149_M01_PublicMigrationPreservesFactsVerifiesBackupAndNeverArmsProduction()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var original = fixture.Fingerprint();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using (var session = opened.Session!)
        {
            var completed = await Finish(session);
            Assert.False(completed.Ready);
            Assert.Equal("SchemaMigratedStartupReconciliationRequired", completed.ReasonCode);
            var backup = Assert.IsType<VerifiedStoreMigrationBackup>(completed.Backup);
            Assert.Equal(32, backup.SourceSchemaVersion);
            Assert.Equal(original.ContentHash, backup.SourceFingerprint);
            Assert.Equal(new FileInfo(backup.Path).Length, backup.ByteLength);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup.Path))), backup.Sha256);
            Assert.Equal(original.ContentHash, fixture.Fingerprint(backup.Path).ContentHash);
            Assert.Equal(33, fixture.Fingerprint().SchemaVersion);
            using var connection = SqliteNative.Open(fixture.Target.DatabasePath, readOnly: true);
            Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, original.Tables,
                MigrationFixture.Budget, MigrationFixture.Deadline()));
        }
        await using var writer = new SqliteCommandStore(fixture.Target);
        var ready = await writer.Initialization;
        Assert.True(ready.Committed, ready.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(writer);
        Assert.Equal("wal", writer.VerifiedProfile!.JournalMode);
        Assert.Equal(2, writer.VerifiedProfile.Synchronous);
        Assert.Equal(1, writer.VerifiedProfile.ForeignKeys);
        Assert.Equal(0, writer.VerifiedProfile.WalAutoCheckpoint);
        // These are real rejected command facts, not production authority.
        var appended = await writer.AppendAsync(MigrationFixture.RejectedFact(), MigrationFixture.Deadline());
        Assert.True(appended.Committed, appended.ReasonCode);
        await writer.DisposeAsync();
        var reopened = await fixture.OpenAsync();
        Assert.True(reopened.Available, reopened.Status.ReasonCode);
        Assert.True(reopened.Status.Completed);
        Assert.False(reopened.Status.Ready);
        await reopened.Session!.DisposeAsync();
        // Ordinary startup must also accept the verified prefix after a later append.
        await using var nextWriter = new SqliteCommandStore(fixture.Target);
        var nextInitialization = await nextWriter.Initialization;
        Assert.True(nextInitialization.Committed, nextInitialization.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(nextWriter);
        var nextAppend = await nextWriter.AppendAsync(MigrationFixture.RejectedFact(), MigrationFixture.Deadline());
        Assert.True(nextAppend.Committed, nextAppend.ReasonCode);
    }

    [Theory]
    [InlineData(StoreMigrationPhase.Opened)]
    [InlineData(StoreMigrationPhase.SourceVerified)]
    [InlineData(StoreMigrationPhase.CheckpointVerified)]
    [InlineData(StoreMigrationPhase.BackupStarted)]
    [InlineData(StoreMigrationPhase.BackupCreated)]
    [InlineData(StoreMigrationPhase.BackupVerified)]
    [InlineData(StoreMigrationPhase.TransactionStarted)]
    [InlineData(StoreMigrationPhase.TablesCaptured)]
    [InlineData(StoreMigrationPhase.TablesRebuilt)]
    [InlineData(StoreMigrationPhase.FeatureInitialized)]
    [InlineData(StoreMigrationPhase.TargetVerified)]
    [InlineData(StoreMigrationPhase.CommitIntent)]
    [InlineData(StoreMigrationPhase.DatabaseCommitted)]
    [InlineData(StoreMigrationPhase.Completed)]
    [Trait("VerificationId", "V149_M02")]
    public async Task V149_M02_EveryPublicPhaseDisposesAndReopensWithExactGenerationProof(StoreMigrationPhase phase)
    {
        var fixture = await MigrationFixture.CreateAsync();
        var source = fixture.Fingerprint();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        var session = opened.Session!;
        await AdvanceTo(session, phase);
        var operation = session.Status.OperationId;
        await session.DisposeAsync();
        var disk = fixture.Fingerprint();
        Assert.Equal(phase < StoreMigrationPhase.DatabaseCommitted ? 32 : 33, disk.SchemaVersion);
        if (phase < StoreMigrationPhase.DatabaseCommitted) Assert.Equal(source.ContentHash, disk.ContentHash);
        if (phase != StoreMigrationPhase.Completed)
        {
            await using var denied = new SqliteCommandStore(disk.SchemaVersion == 32 ? fixture.Source : fixture.Target);
            var result = await denied.Initialization;
            Assert.False(result.Committed);
            Assert.Equal("StoreMigrationStartupMaintenanceRequired", result.ReasonCode);
        }
        var resumed = await fixture.OpenAsync();
        Assert.True(resumed.Available, resumed.Status.ReasonCode);
        Assert.Equal(operation, resumed.Status.OperationId);
        await using var finish = resumed.Session!;
        Assert.True((await Finish(finish)).Completed);
    }

    [Fact]
    [Trait("VerificationId", "V149_M03")]
    public async Task V149_M03_LiveWriterOrMaintenanceOwnerExcludesAnotherOwner()
    {
        var fixture = await MigrationFixture.CreateAsync();
        await using (var writer = new SqliteCommandStore(fixture.Source))
        {
            Assert.True((await writer.Initialization).Committed);
            var denied = await fixture.OpenAsync();
            Assert.False(denied.Available);
            Assert.False(File.Exists(StoreMigrationJournalGuard.MarkerPath(fixture.Target.DatabasePath)));
        }
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using var session = opened.Session!;
        Assert.False((await fixture.OpenAsync()).Available);
        await using var sourceWriter = new SqliteCommandStore(fixture.Source);
        Assert.False((await sourceWriter.Initialization).Committed);
    }

    [Theory]
    [InlineData("truncated-journal")]
    [InlineData("missing-journal")]
    [InlineData("missing-marker")]
    [InlineData("corrupt-marker")]
    [InlineData("corrupt-journal")]
    [Trait("VerificationId", "V149_M04")]
    public async Task V149_M04_DamagedOrMissingExternalEvidenceNeverRecreatesACleanOperation(string damage)
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await AdvanceTo(opened.Session!, StoreMigrationPhase.BackupVerified);
        await opened.Session!.DisposeAsync();
        var journal = StoreMigrationJournalGuard.JournalPath(fixture.Target.DatabasePath);
        var marker = StoreMigrationJournalGuard.MarkerPath(fixture.Target.DatabasePath);
        switch (damage)
        {
            case "truncated-journal":
                using (var file = new FileStream(journal, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 7);
                break;
            case "missing-journal": File.Move(journal, journal + ".preserved"); break;
            case "missing-marker": File.Move(marker, marker + ".preserved"); break;
            case "corrupt-marker": Damage(marker); break;
            case "corrupt-journal": Damage(journal); break;
        }
        var snapshot = fixture.Fingerprint();
        var denied = await fixture.OpenAsync();
        Assert.False(denied.Available);
        Assert.Equal(StoreMigrationPhase.MaintenanceRequired, denied.Status.Phase);
        Assert.Equal(snapshot.ContentHash, fixture.Fingerprint().ContentHash);
        await using var writer = new SqliteCommandStore(fixture.Target);
        Assert.False((await writer.Initialization).Committed);
    }

    [Fact]
    [Trait("VerificationId", "V149_M05")]
    public async Task V149_M05_VerifiedBackupCorruptionBlocksResumeAndPreservesSource()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var original = fixture.Fingerprint();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await AdvanceTo(opened.Session!, StoreMigrationPhase.BackupVerified);
        var backup = opened.Session!.Status.Backup!;
        await opened.Session.DisposeAsync();
        Damage(backup.Path);
        var denied = await fixture.OpenAsync();
        Assert.False(denied.Available);
        Assert.Equal("StoreMigrationBackupHashMismatch", denied.Status.ReasonCode);
        Assert.Equal(original.ContentHash, fixture.Fingerprint().ContentHash);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(34)]
    [InlineData(999)]
    [Trait("VerificationId", "V149_M06")]
    public async Task V149_M06_UnknownPathOrNewerVersionIsRefusedWithoutInventingMigration(int version)
    {
        // Deliberately incompatible negative source; never used as migration success evidence.
        var fixture = await MigrationFixture.CreateAsync();
        fixture.Execute("PRAGMA user_version=" + version + ";");
        var before = File.ReadAllBytes(fixture.Target.DatabasePath);
        var denied = await fixture.OpenAsync();
        Assert.False(denied.Available);
        Assert.Equal(version > 33 ? "StoreMigrationNewerSchemaUnsupported" : "StoreMigrationPathUnsupported", denied.Status.ReasonCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.Target.DatabasePath));
        Assert.False(File.Exists(StoreMigrationJournalGuard.MarkerPath(fixture.Target.DatabasePath)));
    }

    [Fact]
    [Trait("VerificationId", "V149_M07")]
    public async Task V149_M07_CancellationRollsBackAndLeavesExplicitResumableMaintenance()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var before = fixture.Fingerprint();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await AdvanceTo(opened.Session!, StoreMigrationPhase.FeatureInitialized);
        var cancelled = await opened.Session!.AdvanceAsync(new CancellationToken(canceled: true));
        Assert.Equal("StoreMigrationCancelled", cancelled.ReasonCode);
        Assert.False(cancelled.Ready);
        await opened.Session.DisposeAsync();
        Assert.Equal(before.ContentHash, fixture.Fingerprint().ContentHash);
        var resumed = await fixture.OpenAsync();
        Assert.True(resumed.Available, resumed.Status.ReasonCode);
        await using var session = resumed.Session!;
        Assert.True((await Finish(session)).Completed);
    }

    [Fact]
    [Trait("VerificationId", "V149_M08")]
    public async Task V149_M08_SourceChangedAfterInterruptionIsNotSilentlyMigrated()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await AdvanceTo(opened.Session!, StoreMigrationPhase.SourceVerified);
        await opened.Session!.DisposeAsync();
        // This copies no schema. It emulates the documented legacy-writer boundary:
        // older binaries cannot read the new sidecar. Its legitimate appended fact
        // must still be detected by resume, even though its audit remains valid.
        var journal = StoreMigrationJournalGuard.JournalPath(fixture.Source.DatabasePath);
        var marker = StoreMigrationJournalGuard.MarkerPath(fixture.Source.DatabasePath);
        File.Move(journal, journal + ".held"); File.Move(marker, marker + ".held");
        await using (var writer = new SqliteCommandStore(fixture.Source))
        {
            Assert.True((await writer.Initialization).Committed);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(writer);
            var appended = await writer.AppendAsync(MigrationFixture.RejectedFact(), MigrationFixture.Deadline());
            Assert.True(appended.Committed, appended.ReasonCode);
        }
        File.Move(journal + ".held", journal); File.Move(marker + ".held", marker);
        var result = await fixture.OpenAsync();
        Assert.False(result.Available);
        Assert.Equal("StoreMigrationSourceChangedDuringInterruption", result.Status.ReasonCode);
        Assert.Equal(32, fixture.Fingerprint().SchemaVersion);
    }

    internal static async Task AdvanceTo(IStoreStartupMaintenanceSession session, StoreMigrationPhase phase)
    {
        while (session.Status.Phase < phase)
        {
            var result = await session.AdvanceAsync();
            Assert.True(result.Phase != StoreMigrationPhase.MaintenanceRequired, result.ReasonCode);
            Assert.True(result.Phase <= phase, result.ReasonCode);
            Assert.False(result.Ready);
        }
        Assert.True(session.Status.Phase == phase, session.Status.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V149_M09")]
    public async Task V149_M09_RealAuthorizedDraftHistoryAndIdentityFactsSurviveMigration()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionAdmission: new ProductionAdmissionStoreOptions(), productionArming: new ProductionArmStoreOptions());
        var draft = Guid.NewGuid();
        var first = await fixture.SaveAsync(Guid.NewGuid(), draft, 0, null, fixture.Document("Migration source first"), "first");
        Assert.True(first.Saved, first.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var second = await fixture.SaveAsync(Guid.NewGuid(), draft, 1, first.Revision!.RevisionContentHash,
            fixture.Document("Migration source second"), "second");
        Assert.True(second.Saved, second.ReasonCode);
        await fixture.Store.DisposeAsync();
        var target = TargetWithLifecycle(fixture.Options, new RecipeLifecycleStoreOptions());
        var opened = await SqliteStartupMaintenance.OpenAsync(target,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location));
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using (var session = opened.Session!) Assert.True((await Finish(session)).Completed);
        var read = await new SqliteRecipeDraftQuery(target).QueryAsync(new RecipeDraftFilter(PageSize: 20));
        Assert.True(read.Available, read.ReasonCode);
        Assert.Equal(new[] { first.Revision.RevisionContentHash, second.Revision!.RevisionContentHash },
            read.Revisions.Select(value => value.RevisionContentHash));
    }

    [Fact]
    [Trait("VerificationId", "V149_M10")]
    public async Task V149_M10_CorruptDatabaseIsPreservedAndNeverRecreated()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var bytes = File.ReadAllBytes(fixture.Target.DatabasePath);
        bytes[0] ^= 1;
        File.WriteAllBytes(fixture.Target.DatabasePath, bytes);
        var denied = await fixture.OpenAsync();
        Assert.False(denied.Available);
        Assert.False(denied.Status.Ready);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Target.DatabasePath));
        Assert.False(File.Exists(StoreMigrationJournalGuard.MarkerPath(fixture.Target.DatabasePath)));
    }

    [Fact]
    [Trait("VerificationId", "V149_M11")]
    public async Task V149_M11_ChangedTargetConfigurationCannotAppendToAnExistingJournal()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await AdvanceTo(opened.Session!, StoreMigrationPhase.SourceVerified);
        await opened.Session!.DisposeAsync();
        var journalPath = StoreMigrationJournalGuard.JournalPath(fixture.Target.DatabasePath);
        var journal = File.ReadAllBytes(journalPath);
        var changed = TargetWithLifecycle(fixture.Target,
            new RecipeLifecycleStoreOptions { MaxEvents = fixture.Target.RecipeLifecycle!.MaxEvents + 1 });
        var result = await SqliteStartupMaintenance.OpenAsync(changed,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location));
        Assert.False(result.Available);
        Assert.Equal("StoreMigrationResumeContextMismatch", result.Status.ReasonCode);
        Assert.Equal(journal, File.ReadAllBytes(journalPath));
    }

    [Fact]
    [Trait("VerificationId", "V149_M12")]
    public async Task V149_M12_ExplicitQuickCheckStillRequiresAllAuditAndGenerationProofs()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await SqliteStartupMaintenance.OpenAsync(fixture.Target,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location)
            { IntegrityCheck = StoreMigrationIntegrityCheck.Quick });
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using var session = opened.Session!;
        var result = await Finish(session);
        Assert.NotNull(result.Backup);
        Assert.False(result.Ready);
    }

    private static ProductionStoreOptions TargetWithLifecycle(ProductionStoreOptions source, RecipeLifecycleStoreOptions lifecycle) =>
        new(source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy, LocalIdentity = source.LocalIdentity,
            RecipeDrafts = source.RecipeDrafts, ProductionAdmission = source.ProductionAdmission,
            ProductionArming = source.ProductionArming, RecipeLifecycle = lifecycle,
            CommitTimeout = source.CommitTimeout, QueryTimeout = source.QueryTimeout, QueueCapacity = source.QueueCapacity
        };

    internal static async Task<StoreMigrationStatus> Finish(IStoreStartupMaintenanceSession session)
    { await AdvanceTo(session, StoreMigrationPhase.Completed); return session.Status; }

    private static void Damage(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        file.Position = file.Length - 1;
        var value = file.ReadByte();
        file.Position--;
        file.WriteByte((byte)(value ^ 1)); file.Flush(true);
    }
}

internal sealed class MigrationFixture
{
    internal const long Budget = 64L * 1024 * 1024;
    private MigrationFixture(ProductionStoreOptions target)
    { Target = target; Source = SqliteCommandStore.MigrationSourceOptions(target); }
    internal ProductionStoreOptions Target { get; }
    internal ProductionStoreOptions Source { get; }
    internal static StoreDeadline Deadline() => new(TimeSpan.FromSeconds(30));

    internal static async Task<MigrationFixture> CreateAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Migration fixtures require the Windows NTFS and machine-key store profile.");
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V149-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string station = "V149MigrationStation";
        var policy = new AuditIntegrityPolicy(station, "v1", "SharpInspect.Test.V149." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2, MaximumVerificationEntries = 10_000,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identity = new LocalIdentityOptions(station, new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("v149-blocklist", "v1", new[] { "known-compromised-value" })
        }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, RecipeDraftTestPolicies.Authoring);
        var execution = new AlgorithmExecutionPolicy("V149.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var target = new ProductionStoreOptions(Path.Combine(directory, "migration.sqlite"))
        {
            AuditIntegrityPolicy = policy, LocalIdentity = identity,
            RecipeDrafts = new RecipeDraftStoreOptions(execution),
            ProductionAdmission = new ProductionAdmissionStoreOptions(), ProductionArming = new ProductionArmStoreOptions(),
            RecipeLifecycle = new RecipeLifecycleStoreOptions(), CommitTimeout = TimeSpan.FromSeconds(5),
            QueryTimeout = TimeSpan.FromSeconds(5), QueueCapacity = 8
        };
        var fixture = new MigrationFixture(target);
        await using var writer = new SqliteCommandStore(fixture.Source);
        var initialized = await writer.Initialization;
        Assert.True(initialized.Committed, initialized.ReasonCode);
        for (var i = 0; i < 3; i++)
        {
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(writer);
            var result = await writer.AppendAsync(RejectedFact(), Deadline());
            Assert.True(result.Committed, result.ReasonCode);
        }
        return fixture;
    }

    internal static CommandAuditFact RejectedFact() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), DateTimeOffset.UtcNow, AuditedCommandKind.Unsupported, CommandSource.PhysicalConsole,
        null, null, null, CommandAuditPhase.Outcome, CommandDisposition.Rejected, "UnsupportedCommand");

    internal ValueTask<StoreStartupMaintenanceOpenResult> OpenAsync() => SqliteStartupMaintenance.OpenAsync(Target,
        new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location)
        { MaximumDatabaseBytes = Budget, OperationTimeout = TimeSpan.FromSeconds(30) });

    internal MigrationDatabaseFingerprint Fingerprint(string? path = null)
    {
        using var connection = SqliteNative.Open(path ?? Target.DatabasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, Target);
        return StoreMigrationFingerprint.Read(connection.Handle!, Budget, Deadline());
    }

    internal void Execute(string sql)
    {
        using var connection = SqliteNative.Open(Target.DatabasePath, readOnly: false);
        SqliteNative.Execute(connection.Handle!, sql, Deadline());
    }
}
