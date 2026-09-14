using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class EvidenceQuarantineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact, Trait("VerificationId", "V154_Q01")]
    public async Task V154_Q01_RenamePreservesOpenedIdentityAndBytesWithoutOverwrite()
    {
        using var fixture = new Fixture();
        var bytes = Enumerable.Range(0, 513).Select(x => (byte)x).ToArray();
        var source = fixture.Write("orphan.bin", bytes);
        string target;
        using (var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "orphan.bin", Timeout, default))
        {
            Assert.Equal(bytes.Length, claim.Descriptor.ByteLength);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), claim.Descriptor.RawContentHash);
            target = fixture.TargetPath(claim.Descriptor);
            await fixture.Quarantine.MoveAsync(claim, Timeout, default);
            Assert.True(claim.Moved);
            claim.VerifyCommitProtection();
            Assert.False(File.Exists(source));
            Assert.Throws<IOException>(() => File.WriteAllText(target, "replacement"));
        }
        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact, Trait("VerificationId", "V154_Q02")]
    public async Task V154_Q02_ClaimProtectsSourceAndAncestors()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("orphan.bin", new byte[] { 1, 2 });
        using var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "orphan.bin", Timeout, default);
        Assert.Throws<IOException>(() => File.WriteAllText(source, "replacement"));
        Assert.Throws<IOException>(() => File.Move(source, source + ".moved"));
        Assert.Throws<IOException>(() => Directory.Move(fixture.Source, fixture.Source + "-moved"));
        claim.VerifyCommitProtection();
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V154_Q03")]
    public async Task V154_Q03_IntentRecoversBothCrashLocations(bool movedBeforeCrash)
    {
        using var fixture = new Fixture();
        var source = fixture.Write("orphan.bin", new byte[] { 1, 2 });
        QuarantinedFileDescriptor intent;
        using (var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "orphan.bin", Timeout, default))
        {
            intent = claim.Descriptor;
            if (movedBeforeCrash) await fixture.Quarantine.MoveAsync(claim, Timeout, default);
        }
        using var recovered = await fixture.Quarantine.RecoverAsync(intent, Timeout, default);
        Assert.True(recovered.Moved);
        Assert.Equal(intent, recovered.Descriptor);
        Assert.False(File.Exists(source));
        recovered.VerifyCommitProtection();
    }

    [Theory, InlineData("both"), InlineData("missing"), InlineData("changed"), Trait("VerificationId", "V154_Q04")]
    public async Task V154_Q04_ConflictingRecoveryNeverCertifiesQuarantine(string state)
    {
        using var fixture = new Fixture();
        var source = fixture.Write("orphan.bin", new byte[] { 1, 2 });
        QuarantinedFileDescriptor intent;
        using (var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "orphan.bin", Timeout, default))
            intent = claim.Descriptor;
        if (state == "both") File.Copy(source, fixture.TargetPath(intent));
        else if (state == "missing") File.Delete(source);
        else File.WriteAllBytes(source, new byte[] { 3, 4 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Quarantine.RecoverAsync(intent, Timeout, default));
    }

    [Fact, Trait("VerificationId", "V154_Q05")]
    public async Task V154_Q05_TargetCollisionCannotOverwriteEitherFile()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("orphan.bin", new byte[] { 1, 2 });
        using var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "orphan.bin", Timeout, default);
        var target = fixture.TargetPath(claim.Descriptor);
        File.WriteAllBytes(target, new byte[] { 99 });
        await Assert.ThrowsAsync<IOException>(() => fixture.Quarantine.MoveAsync(claim, Timeout, default));
        Assert.False(claim.Moved);
        Assert.True(File.Exists(source));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(target));
    }

    [Fact, Trait("VerificationId", "V154_Q06")]
    public async Task V154_Q06_HardLinkDuplicateIsRejected()
    {
        using var fixture = new Fixture();
        var source = fixture.Write("orphan.bin", new byte[] { 1, 2 });
        Assert.True(CreateHardLink(Path.Combine(fixture.Source, "duplicate.bin"), source, IntPtr.Zero),
            Marshal.GetLastWin32Error().ToString());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "orphan.bin", Timeout, default));
        Assert.Equal("EvidenceQuarantineFileKindOrUniquenessInvalid", error.Message);
    }

    [Fact, Trait("VerificationId", "V154_Q07")]
    public async Task V154_Q07_ReservationAndActualCapacityPreserveNextSource()
    {
        using var fixture = new Fixture(maximumFiles: 1);
        fixture.Write("first.bin", new byte[] { 1 });
        var second = fixture.Write("second.bin", new byte[] { 2 });
        using (var first = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "first.bin", Timeout, default))
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "second.bin", Timeout, cancelled.Token));
            await fixture.Quarantine.MoveAsync(first, Timeout, default);
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "second.bin", Timeout, default));
        Assert.Equal("EvidenceQuarantineCapacityExceeded", error.Message);
        Assert.True(File.Exists(second));
    }

    [Fact, Trait("VerificationId", "V154_Q08")]
    public async Task V154_Q08_LogicalTimeoutRetainsPhysicalOwnershipUntilActualExit()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(hook: _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("TestHookNotReleased");
        });
        var source = fixture.Write("first.bin", new byte[] { 1 });
        fixture.Write("second.bin", new byte[] { 2 });
        var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "first.bin", Timeout, default);
        var moving = fixture.Quarantine.MoveAsync(claim, TimeSpan.FromMilliseconds(250), default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await Assert.ThrowsAsync<TimeoutException>(() => moving);
            claim.Dispose();
            Assert.Throws<IOException>(() => File.WriteAllText(source, "replacement"));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "second.bin", TimeSpan.FromMilliseconds(50), default));
        }
        finally { claim.Dispose(); release.Set(); }
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Quarantine.PhysicalCompletion);
        using var next = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), "second.bin", Timeout, default);
        next.VerifyCommitProtection();
    }

    [Theory, InlineData(129), InlineData(255), Trait("VerificationId", "V154_Q09")]
    public async Task V154_Q09_LongOrdinarySourceNameRemainsIdentifiableAndQuarantinable(int length)
    {
        using var fixture = new Fixture();
        var name = new string('a', length - 4) + ".bin";
        fixture.Write(name, new byte[] { 1, 2, 3 });
        using var claim = await fixture.Quarantine.PrepareAsync(Guid.NewGuid(), name, Timeout, default);
        Assert.Equal(name, claim.Descriptor.SourceFileName);
        await fixture.Quarantine.MoveAsync(claim, Timeout, default);
        Assert.True(claim.Moved);
        claim.VerifyCommitProtection();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = ProductionOutboxStorageTests.TestDirectory("V154-quarantine");
        internal Fixture(int maximumFiles = 8, Action<EvidenceQuarantineBoundary>? hook = null)
        {
            Source = Path.Combine(_root, "source"); Target = Path.Combine(_root, "quarantine");
            Directory.CreateDirectory(Source); Directory.CreateDirectory(Target);
            Quarantine = new(Source, new string('A', 64),
                new EvidenceQuarantineRootOptions(Target, 1024 * 1024, 8 * 1024 * 1024, maximumFiles), hook);
        }
        internal string Source { get; }
        internal string Target { get; }
        internal EvidenceQuarantine Quarantine { get; }
        internal string Write(string name, byte[] bytes)
        { var path = Path.Combine(Source, name); File.WriteAllBytes(path, bytes); return path; }
        internal string TargetPath(QuarantinedFileDescriptor descriptor) => Path.Combine(Target, descriptor.QuarantineFileName);
        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr security);
}
