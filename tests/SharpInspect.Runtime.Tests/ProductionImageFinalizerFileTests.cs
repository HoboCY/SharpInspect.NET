using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>Real file protocol and private claim boundaries; SQL/Host crash replay is separate.</summary>
public sealed class ProductionImageFinalizerFileTests
{
    [Theory]
    [Trait("VerificationId", "V151_F01")]
    [InlineData((int)ImageFinalizationBoundary.BeforeStageOpen)]
    [InlineData((int)ImageFinalizationBoundary.BeforeEncode)]
    [InlineData((int)ImageFinalizationBoundary.AfterEncode)]
    [InlineData((int)ImageFinalizationBoundary.BeforeFlush)]
    [InlineData((int)ImageFinalizationBoundary.AfterFlush)]
    [InlineData((int)ImageFinalizationBoundary.BeforeTemporaryVerify)]
    [InlineData((int)ImageFinalizationBoundary.AfterTemporaryVerify)]
    [InlineData((int)ImageFinalizationBoundary.BeforeRename)]
    [InlineData((int)ImageFinalizationBoundary.AfterRename)]
    [InlineData((int)ImageFinalizationBoundary.BeforeFinalVerify)]
    [InlineData((int)ImageFinalizationBoundary.AfterFinalVerify)]
    [InlineData((int)ImageFinalizationBoundary.BeforeClaim)]
    public async Task V151_F01_EveryFileBoundaryFailurePreservesStageAndYieldsNoCommitProof(int target)
    {
        using var fixture = new Fixture();
        var reason = "V151.FileBoundary." + target;
        var worker = fixture.Worker(boundary => { if ((int)boundary == target) throw new IOException(reason); });
        var exception = await Assert.ThrowsAsync<IOException>(() => fixture.Run(worker));
        Assert.Equal(reason, exception.Message);
        await worker.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, worker.ActiveOperationCount);
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F02")]
    public async Task V151_F02_ProtectedFinalIsExactAndClaimIsBoundAndSingleUse()
    {
        using var fixture = new Fixture();
        using var claim = await fixture.Run(fixture.Worker());
        Assert.Equal(fixture.Work.WorkId, claim.WorkId);
        Assert.Equal(fixture.Work.Manifest.ContentHash, claim.ManifestContentHash);
        Assert.Equal(fixture.Work.Manifest.CanonicalPixelHash, claim.CanonicalPixelHash);
        Assert.Equal(new FileInfo(fixture.FinalPath).Length, claim.EncodedByteLength);
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
        Assert.ThrowsAny<IOException>(() => File.Delete(fixture.FinalPath));
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(fixture.FinalPath, new byte[] { 0 }));
        Assert.Throws<InvalidOperationException>(() => claim.ConsumeForCommit(fixture.Work, Guid.NewGuid(),
            Fixture.RootHash, fixture.FinalName));
        Assert.False(claim.IsConsumed);
        claim.ConsumeForCommit(fixture.Work, fixture.AttemptId, Fixture.RootHash, fixture.FinalName);
        Assert.True(claim.IsConsumed);
        claim.VerifyCommitProtection();
        Assert.Throws<InvalidOperationException>(() => claim.ConsumeForCommit(fixture.Work, fixture.AttemptId,
            Fixture.RootHash, fixture.FinalName));
        claim.Dispose();
        Assert.Throws<InvalidOperationException>(claim.VerifyCommitProtection);
        File.Delete(fixture.FinalPath);
        Assert.True(File.Exists(fixture.StagePath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F03")]
    public async Task V151_F03_PreviouslyBoundFinalCanBeReverifiedAfterRenameWithoutSecondImage()
    {
        using var fixture = new Fixture();
        var first = fixture.Worker(boundary =>
        {
            if (boundary == ImageFinalizationBoundary.AfterRename) throw new IOException("V151.ExitAfterRename");
        });
        await Assert.ThrowsAsync<IOException>(() => fixture.Run(first));
        var firstBytes = File.ReadAllBytes(fixture.FinalPath);
        // The SQL integration supplies allowPreviouslyBoundFinal only for a durable Started.
        File.Delete(fixture.StagePath);
        using var recovered = await fixture.Run(fixture.Worker(), previouslyBound: true);
        Assert.Equal(fixture.Work.Manifest.CanonicalPixelHash, recovered.CanonicalPixelHash);
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.FinalPath));
        Assert.Single(Directory.GetFiles(fixture.FinalRoot, "*.png"));
    }

    [Fact]
    [Trait("VerificationId", "V151_F04")]
    public async Task V151_F04_NewAttemptCannotAdoptPreviouslyExistingFileByName()
    {
        using var fixture = new Fixture();
        using (await fixture.Run(fixture.Worker())) { }
        var bytes = File.ReadAllBytes(fixture.FinalPath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(fixture.Worker()));
        Assert.Equal("ProductionImageFinalPathAlreadyOccupied", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.FinalPath));
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
    }

    [Theory]
    [Trait("VerificationId", "V151_F05")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V151_F05_CorruptFinalOrStageCannotProduceProof(bool corruptFinal)
    {
        using var fixture = new Fixture();
        if (corruptFinal)
        {
            using (await fixture.Run(fixture.Worker())) { }
            File.WriteAllBytes(fixture.FinalPath, new byte[] { 1, 2, 3 });
        }
        else
        {
            var bytes = fixture.StageBytes.ToArray();
            bytes[^1] ^= 1;
            File.WriteAllBytes(fixture.StagePath, bytes);
        }
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => fixture.Run(fixture.Worker(), corruptFinal));
        Assert.True(File.Exists(fixture.StagePath));
        if (corruptFinal) Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(fixture.FinalPath));
        else Assert.False(File.Exists(fixture.FinalPath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F06")]
    public async Task V151_F06_TimeoutKeepsPhysicalSlotUntilBlockedIoReallyReturns()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = fixture.Worker(boundary =>
        {
            if (boundary != ImageFinalizationBoundary.BeforeFlush) return;
            entered.TrySetResult(null);
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var first = fixture.Run(worker, timeout: TimeSpan.FromMilliseconds(400));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            Assert.Equal(1, worker.ActiveOperationCount);
            Assert.False(worker.PhysicalCompletion.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(worker));
            Assert.False(File.Exists(fixture.FinalPath));
            Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
        }
        finally
        {
            release.Set();
            await worker.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, worker.ActiveOperationCount);
        Assert.False(File.Exists(fixture.FinalPath));
        using var replacement = await fixture.Run(fixture.Worker(), attemptId: Guid.NewGuid());
        Assert.Equal(fixture.Work.Manifest.CanonicalPixelHash, replacement.CanonicalPixelHash);
    }

    [Fact]
    [Trait("VerificationId", "V151_F07")]
    public async Task V151_F07_ExistingAttemptTemporaryFileIsPreservedWithoutOverwrite()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.FinalRoot, fixture.TemporaryName);
        var marker = new byte[] { 4, 3, 2, 1 };
        File.WriteAllBytes(path, marker);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(fixture.Worker()));
        Assert.Equal("ProductionImageTemporaryPathAlreadyOccupied", error.Message);
        Assert.Equal(marker, File.ReadAllBytes(path));
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F08")]
    public async Task V151_F08_CapacityRefusalDoesNotStartTemporaryOutput()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.FinalRoot, "existing.png"), new byte[] { 1 });
        var worker = fixture.Worker(maximumFiles: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(worker));
        Assert.False(File.Exists(Path.Combine(fixture.FinalRoot, fixture.TemporaryName)));
        Assert.True(File.Exists(fixture.StagePath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F09")]
    public async Task V151_F09_CancellationCannotIssueLateClaimAndRetainsPhysicalOwnership()
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = fixture.Worker(boundary =>
        {
            if (boundary != ImageFinalizationBoundary.AfterRename) return;
            entered.TrySetResult(null);
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var first = fixture.Run(worker, token: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(1, worker.ActiveOperationCount);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(worker));
        }
        finally
        {
            release.Set();
            await worker.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(File.Exists(fixture.StagePath));
        using var recovered = await fixture.Run(fixture.Worker(), previouslyBound: true);
        Assert.Equal(fixture.Work.ContentHash, recovered.WorkContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V151_F10")]
    public async Task V151_F10_ExpiredPhysicalClaimCannotPublishBeforeLogicalTimeoutAbandons()
    {
        using var fixture = new Fixture();
        using var releasePhysical = new ManualResetEventSlim();
        using var releaseLogical = new ManualResetEventSlim();
        var publication = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutDecision = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = fixture.Worker(boundary =>
        {
            if (boundary == ImageFinalizationBoundary.BeforeClaimPublication)
            {
                publication.TrySetResult(null);
                Assert.True(releasePhysical.Wait(TimeSpan.FromSeconds(10)));
            }
            if (boundary == ImageFinalizationBoundary.BeforeTimeoutDecision)
            {
                timeoutDecision.TrySetResult(null);
                Assert.True(releaseLogical.Wait(TimeSpan.FromSeconds(10)));
            }
        });
        var result = fixture.Run(worker, timeout: TimeSpan.FromMilliseconds(600));
        try
        {
            await publication.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await timeoutDecision.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The logical waiter has not called Abandon. Only the monotonic expiry can
            // prevent this already-created physical claim from becoming publishable.
            releasePhysical.Set();
            await worker.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            releaseLogical.Set();
            await Assert.ThrowsAsync<TimeoutException>(() => result);
            Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
            File.Delete(fixture.FinalPath); // The rejected late claim released its handle.
        }
        finally
        {
            releasePhysical.Set();
            releaseLogical.Set();
            await worker.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            try { (await result).Dispose(); } catch (TimeoutException) { }
        }
    }

    [Fact]
    [Trait("VerificationId", "V151_F13")]
    public async Task V151_F13_KnownInterruptedTemporaryIsDiscardedOnlyWithVerifiedStage()
    {
        using var fixture = new Fixture();
        var temporary = Path.Combine(fixture.FinalRoot, fixture.TemporaryName);
        File.WriteAllBytes(temporary, new byte[] { 1, 2, 3 });
        var worker = fixture.Worker();
        Assert.True(await worker.DiscardKnownTemporaryAsync(fixture.Work, fixture.AttemptId,
            TimeSpan.FromSeconds(5), default));
        Assert.False(File.Exists(temporary));
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
        File.WriteAllBytes(temporary, new byte[] { 4, 5, 6 });
        File.WriteAllBytes(fixture.StagePath, new byte[] { 7, 8 });
        await Assert.ThrowsAnyAsync<Exception>(() => worker.DiscardKnownTemporaryAsync(fixture.Work,
            fixture.AttemptId, TimeSpan.FromSeconds(5), default));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(temporary));
    }

    [Fact]
    [Trait("VerificationId", "V151_F14")]
    public async Task V151_F14_ProtectedFinalAllowsStageReleaseAndAbsentStageReplay()
    {
        using var fixture = new Fixture();
        var worker = fixture.Worker();
        using var claim = await fixture.Run(worker);
        var manifest = fixture.Work.Manifest;
        var success = new ProductionImageSuccessDescriptor(Fixture.RootHash, fixture.FinalName,
            claim.EncodedByteLength, manifest.Width, manifest.Height, manifest.PixelFormat, manifest.ValidBits,
            manifest.HashScheme, manifest.HashSchemeVersion, manifest.CanonicalPixelHash);
        Assert.True(await worker.ReleaseSucceededStageAsync(fixture.Work, success, TimeSpan.FromSeconds(5), default));
        Assert.False(File.Exists(fixture.StagePath));
        Assert.False(await worker.ReleaseSucceededStageAsync(fixture.Work, success, TimeSpan.FromSeconds(5), default));
        Assert.True(File.Exists(fixture.FinalPath));
        Assert.Single(Directory.GetFileSystemEntries(fixture.FinalRoot));
    }

    [Fact]
    [Trait("VerificationId", "V151_F15")]
    public async Task V151_F15_CorruptSucceededFinalNeverReleasesStage()
    {
        using var fixture = new Fixture();
        var worker = fixture.Worker();
        long length;
        using (var claim = await fixture.Run(worker)) length = claim.EncodedByteLength;
        var manifest = fixture.Work.Manifest;
        var success = new ProductionImageSuccessDescriptor(Fixture.RootHash, fixture.FinalName, length,
            manifest.Width, manifest.Height, manifest.PixelFormat, manifest.ValidBits,
            manifest.HashScheme, manifest.HashSchemeVersion, manifest.CanonicalPixelHash);
        File.WriteAllBytes(fixture.FinalPath, new byte[] { 1, 2, 3 });
        await Assert.ThrowsAnyAsync<Exception>(() => worker.ReleaseSucceededStageAsync(fixture.Work,
            success, TimeSpan.FromSeconds(5), default));
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F16")]
    public async Task V151_F16_OrphanInventoryIsPreservedAndRequiresReconciliation()
    {
        using var fixture = new Fixture();
        var orphan = Path.Combine(fixture.FinalRoot, "orphan.tmp");
        File.WriteAllBytes(orphan, new byte[] { 10, 20 });
        var reason = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Worker().VerifyKnownInventoryAsync(
            new HashSet<string> { fixture.Work.Manifest.StageFileName }, new HashSet<string>(),
            TimeSpan.FromSeconds(5), default));
        Assert.Equal("ProductionImageStartupReconciliationRequired", reason.Message);
        Assert.Equal(new byte[] { 10, 20 }, File.ReadAllBytes(orphan));
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
    }

    [Theory]
    [Trait("VerificationId", "V151_F11")]
    [InlineData("../outside.tmp")]
    [InlineData("sub/file.tmp")]
    [InlineData("sub\\file.tmp")]
    [InlineData("C:outside.tmp")]
    [InlineData("foreign-attempt.tmp")]
    public async Task V151_F11_TemporaryLocatorCannotEscapeOrChangeItsPersistedAttempt(string name)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(fixture.Worker(), temporary: name));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.FinalRoot));
        Assert.Equal(fixture.StageBytes, File.ReadAllBytes(fixture.StagePath));
    }

    [Fact]
    [Trait("VerificationId", "V151_F12")]
    public async Task V151_F12_ByteBudgetAndRootSeparationFailBeforeCreatingOutput()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.FinalRoot, "existing.png"), new byte[] { 1 });
        var limits = new ProductionPngFileLimits(new(1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024),
            2 * 1024 * 1024, 100);
        var worker = new ProductionImageFinalizer(fixture.StageOptions, fixture.FinalRoot, Fixture.RootHash, limits);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Run(worker));
        Assert.Equal("ProductionImageFinalByteCapacityExceeded", failure.Message);
        Assert.Single(Directory.GetFileSystemEntries(fixture.FinalRoot));
        var overlap = Assert.Throws<InvalidOperationException>(() =>
            new ProductionImageFinalizer(fixture.StageOptions, fixture.StageRoot, Fixture.RootHash, limits));
        Assert.Equal("ProductionImageRootsMustBeSeparate", overlap.Message);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string RootHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpInspect-T51-file", Guid.NewGuid().ToString("N"));
        internal Fixture()
        {
            StageRoot = Path.Combine(_root, "stage");
            FinalRoot = Path.Combine(_root, "final");
            Directory.CreateDirectory(StageRoot);
            Directory.CreateDirectory(FinalRoot);
            StageOptions = new(StageRoot, 1024 * 1024, 16 * 1024 * 1024, 100);
            var envelope = CanonicalImagePixelContent.CreateEnvelope(3, 2, VisionPixelFormat.Mono8, null);
            StageBytes = envelope.Concat(new byte[] { 1, 2, 4, 9, 10, 12 }).ToArray();
            var stageId = Guid.NewGuid();
            var manifest = new PendingImageManifest(Guid.NewGuid(), Guid.NewGuid(), Hash, Hash, stageId,
                Guid.NewGuid(), StageOptions.ContentHash, stageId.ToString("N") + ".stage", 3, 2,
                VisionPixelFormat.Mono8, null, Convert.ToHexString(SHA256.HashData(StageBytes)), StageBytes.Length,
                Hash, Hash, Hash, Hash, DateTimeOffset.UtcNow);
            Work = new PendingImageFinalizationWork(Guid.NewGuid(), manifest);
            File.WriteAllBytes(StagePath, StageBytes);
        }
        internal string StageRoot { get; }
        internal string FinalRoot { get; }
        internal ProductionImageStageOptions StageOptions { get; }
        internal PendingImageFinalizationWork Work { get; }
        internal Guid AttemptId { get; } = Guid.NewGuid();
        internal string FinalName => Work.Manifest.ManifestId.ToString("N") + ".png";
        internal string TemporaryName => Work.Manifest.ManifestId.ToString("N") + "." + AttemptId.ToString("N") + ".tmp";
        internal string FinalPath => Path.Combine(FinalRoot, FinalName);
        internal string StagePath => Path.Combine(StageRoot, Work.Manifest.StageFileName);
        internal byte[] StageBytes { get; }
        internal ProductionImageFinalizer Worker(Action<ImageFinalizationBoundary>? hook = null, int maximumFiles = 100) =>
            new(StageOptions, FinalRoot, RootHash, new(new(1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024),
                32 * 1024 * 1024, maximumFiles), hook);
        internal Task<ProductionImageFinalizer.VerifiedPngCommitClaim> Run(ProductionImageFinalizer worker,
            bool previouslyBound = false, TimeSpan? timeout = null, string? temporary = null,
            CancellationToken token = default, Guid? attemptId = null) => worker.FinalizeAsync(Work, attemptId ?? AttemptId,
                temporary ?? (Work.Manifest.ManifestId.ToString("N") + "." + (attemptId ?? AttemptId).ToString("N") + ".tmp"),
                FinalName, previouslyBound, timeout ?? TimeSpan.FromSeconds(5), token);
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
