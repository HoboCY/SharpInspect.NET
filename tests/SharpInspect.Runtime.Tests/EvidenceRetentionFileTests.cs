using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class EvidenceRetentionFileTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact, Trait("VerificationId", "V155_F06")]
    public async Task V155_F06_ExpiredPhysicalBudgetIsADeadlineAndPreservesTheFile()
    {
        using var fixture = new Fixture(3);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Files.PrepareAsync(fixture.Obligation,
            null, null, TimeSpan.FromTicks(1), default));
        await fixture.Files.PhysicalCompletion;
        Assert.True(File.Exists(fixture.Path));
    }

    [Fact, Trait("VerificationId", "V155_F05")]
    public async Task V155_F05_ConfirmedDeletionRemovesNameWhileAReadSharingHandleSurvives()
    {
        using var fixture = new Fixture(32);
        using var reader = new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var claim = await fixture.Files.PrepareAsync(fixture.Obligation, null, null, Budget, default);
        await fixture.Files.DeleteAsync(claim, Budget, default);
        Assert.True(claim.Deleted);
        Assert.Throws<FileNotFoundException>(() => File.GetAttributes(fixture.Path));
        Assert.Equal(1, reader.ReadByte()); // Logical deletion does not invent reclaimed physical capacity.
    }

    [Theory, InlineData(0), InlineData(513), Trait("VerificationId", "V155_F01")]
    public async Task V155_F01_ExactOpenedFileIsProtectedThenDeletedIncludingEmptyQuarantine(int length)
    {
        using var fixture = new Fixture(length);
        using var claim = await fixture.Files.PrepareAsync(fixture.Obligation, null, null, Budget, default);
        Assert.False(claim.Deleted);
        Assert.Equal(length, new FileInfo(fixture.Path).Length);
        Assert.Throws<IOException>(() => File.WriteAllBytes(fixture.Path, new byte[] { 5 }));
        Assert.Throws<IOException>(() => File.Move(fixture.Path, fixture.Path + ".replacement"));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
        claim.VerifyCommitProtection();
        await fixture.Files.DeleteAsync(claim, Budget, default);
        Assert.True(claim.Deleted);
        Assert.False(File.Exists(fixture.Path));
        claim.VerifyCommitProtection();
        Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
    }

    [Theory, InlineData("missing"), InlineData("changed"), Trait("VerificationId", "V155_F02")]
    public async Task V155_F02_MissingRecoveryIsUnknownAndChangedFileNeverAcquiresClaim(string mode)
    {
        using var fixture = new Fixture(32);
        EvidenceDeletionFile intent;
        using (var claim = await fixture.Files.PrepareAsync(fixture.Obligation, null, null, Budget, default))
            intent = claim.Descriptor;
        if (mode == "missing")
        {
            File.Delete(fixture.Path);
            using var missing = await fixture.Files.PrepareAsync(fixture.Obligation, null, intent, Budget, default);
            Assert.True(missing.Missing);
            Assert.False(missing.Deleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Files.DeleteAsync(missing, Budget, default));
            missing.VerifyCommitProtection();
        }
        else
        {
            File.WriteAllBytes(fixture.Path, new byte[32]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Files.PrepareAsync(fixture.Obligation, null, intent, Budget, default));
            Assert.Equal(new byte[32], File.ReadAllBytes(fixture.Path));
        }
    }

    [Fact, Trait("VerificationId", "V155_F03")]
    public async Task V155_F03_HardLinksAndUnboundRootsCannotBeDeleted()
    {
        using var fixture = new Fixture(32);
        var alias = System.IO.Path.Combine(fixture.Root, "alias.bin");
        Assert.True(CreateHardLink(alias, fixture.Path, IntPtr.Zero));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Files.PrepareAsync(fixture.Obligation, null, null, Budget, default));
        Assert.True(File.Exists(alias));
        var changed = fixture.Obligation with { RootBindingHash = new string('B', 64), ContentHash = "" };
        changed = changed with { ContentHash = EvidenceRetentionCodec.ObligationHash(changed) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Files.PrepareAsync(changed, null, null, Budget, default));
        Assert.True(File.Exists(fixture.Path));
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V155_F04")]
    public async Task V155_F04_TimeoutRetainsPhysicalOwnerAndReportsLateActualOutcome(bool afterDisposition)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(32, boundary =>
        {
            if (boundary == (afterDisposition ? EvidenceRetentionFileBoundary.AfterDeleteDisposition : EvidenceRetentionFileBoundary.BeforeDelete))
            { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); }
        });
        using var claim = await fixture.Files.PrepareAsync(fixture.Obligation, null, null, Budget, default);
        try
        {
            var deletion = fixture.Files.DeleteAsync(claim, TimeSpan.FromMilliseconds(80), default);
            Assert.True(entered.Wait(Budget));
            await Assert.ThrowsAsync<TimeoutException>(() => deletion);
            Assert.False(fixture.Files.PhysicalCompletion.IsCompleted);
            Assert.Throws<IOException>(() => Directory.Move(fixture.Root, fixture.Root + "-moved"));
            using var next = new Fixture(3);
            await Assert.ThrowsAsync<TimeoutException>(() => next.Files.PrepareAsync(next.Obligation, null, null,
                TimeSpan.FromMilliseconds(40), default));
            release.Set();
            try { await fixture.Files.PhysicalCompletion.WaitAsync(Budget); }
            catch (TimeoutException) when (!afterDisposition) { }
            Assert.Equal(afterDisposition, claim.Deleted);
            Assert.Equal(!afterDisposition, File.Exists(fixture.Path));
            claim.VerifyCommitProtection();
        }
        finally { release.Set(); try { await fixture.Files.PhysicalCompletion.WaitAsync(Budget); } catch (TimeoutException) { } }
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(int length, Action<EvidenceRetentionFileBoundary>? hook = null)
        {
            Root = ProductionOutboxStorageTests.TestDirectory("V155-retention-file");
            var quarantine = new EvidenceQuarantineRootOptions(Root, 1024 * 1024, 8 * 1024 * 1024, 16);
            var retention = new TraceStorageRetentionOptions(new("file-fixture", "1", "fixture-approval", "Bounded file tests",
                new[] { TraceRetentionClass.QuarantineEvidence },
                new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), 1024 * 1024, 4), 3),
                TimeSpan.FromSeconds(1), Budget, Budget, new(128L << 20, 10000, 1L << 20));
            Files = new(new(System.IO.Path.Combine(Root, "unused.db"))
            {
                StorageRetention = retention,
                EvidenceReconciliation = new(retention.ExecutionPolicy.Cleanup) { FinalQuarantine = quarantine }
            }, hook);
            var id = Guid.NewGuid();
            var name = id.ToString("N") + ".quarantine";
            Path = System.IO.Path.Combine(Root, name);
            var bytes = Enumerable.Range(0, length).Select(value => (byte)(value + 1)).ToArray();
            File.WriteAllBytes(Path, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var now = DateTimeOffset.UtcNow;
            var obligation = new EvidenceRetentionObligation(new(EvidenceRetentionOwnerKind.QuarantinedFile, id),
                TraceRetentionClass.QuarantineEvidence, null, new string('A', 64), 1, hash,
                new string('C', 64), new string('D', 64), new string('E', 64), RetentionStartEvent.Quarantined,
                now.AddDays(-2), now.AddDays(-1), quarantine.BindingHash, name, length, "");
            Obligation = obligation with { ContentHash = EvidenceRetentionCodec.ObligationHash(obligation) };
        }
        internal string Root { get; }
        internal string Path { get; }
        internal EvidenceRetentionFiles Files { get; }
        internal EvidenceRetentionObligation Obligation { get; }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}
