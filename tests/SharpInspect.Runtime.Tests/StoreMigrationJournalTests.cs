using System.Buffers.Binary;
using System.Text.Json;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class StoreMigrationJournalTests
{
    [Theory]
    [InlineData(StoreMigrationPhase.CommitIntent, true)]
    [InlineData(StoreMigrationPhase.TargetVerified, false)]
    [Trait("VerificationId", "V149_J01")]
    public async Task V149_J01_CommittedTargetRequiresTheDurableCommitIntent(StoreMigrationPhase lastDurable, bool recoverable)
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await StoreStartupMaintenanceTests.AdvanceTo(opened.Session!, StoreMigrationPhase.DatabaseCommitted);
        await opened.Session!.DisposeAsync();
        // Controlled fault model: retain only complete frames through the named
        // point, while the committed DB is preserved. Actual process killing at
        // every public phase is separately exercised by V149_N02.
        TruncateAfter(fixture.Target.DatabasePath, lastDurable);
        var target = fixture.Fingerprint();
        Assert.Equal(33, target.SchemaVersion);
        var resumed = await fixture.OpenAsync();
        Assert.Equal(recoverable, resumed.Available);
        Assert.False(resumed.Status.Ready);
        if (recoverable)
        {
            await using var session = resumed.Session!;
            Assert.True((await StoreStartupMaintenanceTests.Finish(session)).Completed);
        }
        else
        {
            Assert.Equal("StoreMigrationCommitOutcomeAmbiguous", resumed.Status.ReasonCode);
            Assert.Equal(target.ContentHash, fixture.Fingerprint().ContentHash);
        }
    }

    [Theory]
    [InlineData("source-audit")]
    [InlineData("source-tables")]
    [InlineData("backup")]
    [InlineData("commit-observed")]
    [Trait("VerificationId", "V149_J02")]
    public async Task V149_J02_JournalCannotChangeAnAlreadyBoundProof(string changed)
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await StoreStartupMaintenanceTests.AdvanceTo(opened.Session!, StoreMigrationPhase.BackupVerified);
        await opened.Session!.DisposeAsync();
        var path = StoreMigrationJournalGuard.JournalPath(fixture.Target.DatabasePath);
        var before = File.ReadAllBytes(path);
        using var journal = new StoreMigrationJournal(path, 4L * 1024 * 1024, create: false);
        var next = journal.Last!.Data with { Phase = StoreMigrationPhase.TransactionStarted };
        next = changed switch
        {
            "source-audit" => next with { SourceAuditHash = new string('F', 64) },
            "source-tables" => next with { SourceTables = next.SourceTables!.Skip(1).ToArray() },
            "backup" => next with { BackupSha256 = new string('F', 64) },
            "commit-observed" => next with { DatabaseCommitObserved = true },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidOperationException>(() => journal.Append(next));
        journal.Dispose();
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    [Trait("VerificationId", "V149_J03")]
    public async Task V149_J03_CompletedJournalRejectsRestoredOldGeneration()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        var completed = await StoreStartupMaintenanceTests.Finish(opened.Session!);
        await opened.Session!.DisposeAsync();
        // Deliberate manual-restore fault fixture. The maintenance implementation
        // itself has no automatic restore or downgrade capability.
        using (var backup = SqliteNative.Open(completed.Backup!.Path, readOnly: true))
        using (var destination = SqliteNative.Open(fixture.Target.DatabasePath, readOnly: false))
            backup.BackupDatabase(destination);
        var result = await fixture.OpenAsync();
        Assert.False(result.Available);
        Assert.Equal("StoreMigrationJournalDatabaseGenerationMismatch", result.Status.ReasonCode);
        Assert.Equal(32, fixture.Fingerprint().SchemaVersion);
        Assert.True(StoreMigrationJournal.Read(StoreMigrationJournalGuard.JournalPath(fixture.Target.DatabasePath),
            4L * 1024 * 1024)!.Data.Phase == StoreMigrationPhase.Completed);
    }

    private static void TruncateAfter(string databasePath, StoreMigrationPhase phase)
    {
        var path = StoreMigrationJournalGuard.JournalPath(databasePath);
        var bytes = File.ReadAllBytes(path);
        var offset = 8;
        while (offset < bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            var data = JsonSerializer.Deserialize<StoreMigrationJournalData>(bytes.AsSpan(offset + 4, length))!;
            offset += 4 + length + 32;
            if (data.Phase != phase) continue;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
            file.SetLength(offset); file.Flush(true);
            return;
        }
        throw new InvalidOperationException("Named phase missing from the original journal.");
    }
}
