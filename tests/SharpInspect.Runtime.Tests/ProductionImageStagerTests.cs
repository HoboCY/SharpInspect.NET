using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Images;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// ADR-0069 required-image-pixels staging acceptance (V150). Every case drives the real
/// internal <see cref="ProductionImageStager"/> over a real local staging root created under
/// the configured temp directory, and acquires the retained frame from the real
/// <see cref="FrameBufferPool"/> exactly like FrameBufferPoolTests: the pool copies the
/// fixture pixels into its pinned buffer, <see cref="RetainedProductionFrame.Capture"/> takes
/// the native read hold, and then the ordinary lease is closed. No pixel buffer is fabricated
/// by the test, and the boundary fault hook is used only to raise, pause or corrupt at a
/// deterministic protocol boundary, never to stand in for the production file work.
/// </summary>
public sealed class ProductionImageStagerTests
{
    private const string AdmissionHashA =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string PolicyHashB =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string OtherHashC =
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    // Deployment limits used unless a case deliberately tightens them.
    private const long DefaultMaximumStageBytes = 512L * 1024;
    private const long DefaultMaximumTotalStageBytes = 32L * 1024 * 1024;
    private const int DefaultMaximumStageFiles = 200;

    /// <summary>32-byte canonical envelope plus the 3x2 valid Mono8 pixels of fixture 1.</summary>
    private const long Mono8CanonicalByteLength = 32L + (3 * 2);

    [Theory]
    [Trait("VerificationId", "V150_F01")]
    [InlineData((int)StageBoundary.BeforeWrite)]
    [InlineData((int)StageBoundary.AfterWrite)]
    [InlineData((int)StageBoundary.BeforeFlush)]
    [InlineData((int)StageBoundary.AfterFlush)]
    [InlineData((int)StageBoundary.BeforeVerify)]
    [InlineData((int)StageBoundary.AfterVerify)]
    [InlineData((int)StageBoundary.BeforeRename)]
    [InlineData((int)StageBoundary.AfterRename)]
    public async Task V150_F01_InjectedBoundaryFaultNeverReturnsAClaim(int boundaryValue)
    {
        var boundary = (StageBoundary)boundaryValue;
        var root = CreateStageRoot();
        try
        {
            var failureMessage = "V150.StageBoundary." + boundary;
            var inject = true;
            var stager = new ProductionImageStager(CreateOptions(root),
                observed => { if (inject && observed == boundary) throw new InvalidOperationException(failureMessage); });
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, _) = Retain(pool, fixture);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, frame,
                    TimeSpan.FromSeconds(10)));

            // The injected fault is the observed failure, so no claim was ever handed out.
            Assert.Equal(failureMessage, thrown.Message);
            Assert.False(frame.IsLoanActive);
            Assert.Equal(0, stager.ActiveOperationCount);
            Assert.True(stager.PhysicalCompletion.IsCompleted);
            AssertPoolIdle(pool);

            // The renamed file exists only after the last boundary; every other boundary
            // leaves the unfinished partial behind and nothing else.
            var entries = Directory.GetFiles(root);
            if (boundary == StageBoundary.BeforeWrite)
            {
                Assert.Empty(entries);
            }
            else if (boundary == StageBoundary.AfterRename)
            {
                Assert.Single(entries);
                Assert.EndsWith(".stage", entries[0], StringComparison.Ordinal);
            }
            else
            {
                Assert.Single(entries);
                Assert.EndsWith(".partial", entries[0], StringComparison.Ordinal);
            }

            // A faulted physical operation releases the single operation slot: the next stage
            // is admitted and completes instead of the stager staying wedged.
            inject = false;
            var (retry, _) = Retain(pool, fixture);
            var claim = await stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, retry,
                TimeSpan.FromSeconds(10));
            Assert.True(claim.CompletedWithinDeadline);
            Assert.Equal(0, stager.ActiveOperationCount);
            Assert.True(stager.PhysicalCompletion.IsCompleted);
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Theory]
    [Trait("VerificationId", "V150_F02")]
    [InlineData(32, "ProductionStageHashMismatch")]
    [InlineData(12, "ProductionStageEnvelopeMismatch")]
    public async Task V150_F02_PartialCorruptionBeforeVerifyIsRejectedByReadback(int corruptOffset,
        string expectedReason)
    {
        var root = CreateStageRoot();
        try
        {
            // BeforeVerify runs with the writer closed, so an out-of-band writer can still
            // reach the finished partial; the readback must then refuse the file.
            var stager = new ProductionImageStager(CreateOptions(root), boundary =>
            {
                if (boundary != StageBoundary.BeforeVerify) return;
                var partial = Assert.Single(Directory.GetFiles(root, "*.partial"));
                using var corrupting = new FileStream(partial, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite);
                corrupting.Position = corruptOffset;
                corrupting.WriteByte(0x7F);
                corrupting.Flush(flushToDisk: true);
            });
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, _) = Retain(pool, fixture);

            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, frame,
                    TimeSpan.FromSeconds(10)));

            Assert.Equal(expectedReason, rejected.Message);
            Assert.False(frame.IsLoanActive);
            Assert.Equal(0, stager.ActiveOperationCount);
            Assert.True(stager.PhysicalCompletion.IsCompleted);
            AssertPoolIdle(pool);

            // The verified-but-rejected file stays behind as an orphan of its full length:
            // it was never renamed and can never be claimed.
            var entries = Directory.GetFiles(root);
            Assert.Single(entries);
            Assert.EndsWith(".partial", entries[0], StringComparison.Ordinal);
            Assert.Equal(Mono8CanonicalByteLength, new FileInfo(entries[0]).Length);
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F03")]
    public async Task V150_F03_TheInProgressPartialIsExclusivelyHeldAndStillStagesIntact()
    {
        var root = CreateStageRoot();
        try
        {
            Exception? externalWriteDenied = null;
            var stager = new ProductionImageStager(CreateOptions(root), boundary =>
            {
                if (boundary != StageBoundary.AfterWrite) return;
                // The write window belongs to the stage alone: the partial is opened with no
                // sharing, so no other writer inside the process can corrupt it there.
                var partial = Assert.Single(Directory.GetFiles(root, "*.partial"));
                try
                {
                    using var external = new FileStream(partial, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite);
                }
                catch (IOException denied)
                {
                    externalWriteDenied = denied;
                }
            });
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, _) = Retain(pool, fixture);

            var claim = await stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(10));

            Assert.NotNull(externalWriteDenied);
            AssertCanonicalFilePayload(claim, fixture, ExpectedCanonicalHash(fixture));
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F04")]
    public void V150_F04_RetentionKeepsPixelsAndThePoolSlotUntilTheRetainedFrameIsDisposed()
    {
        using var pool = CreatePool(capacity: 1, maximumFrameBytes: 16);
        var fixture = PayloadFixture(1);
        var copy = pool.TryCopyFrame(fixture.Metadata, fixture.Provenance, fixture.Source);
        Assert.True(copy.Succeeded, copy.ReasonCode);
        var lease = copy.Lease!;
        var leaseId = lease.LeaseId;

        var retained = RetainedProductionFrame.Capture(lease);
        Assert.Equal(leaseId, retained.LeaseId);
        Assert.Equal(fixture.Metadata, retained.Metadata);
        lease.Dispose();

        // Closing the ordinary lease cannot revoke the pixels the stage still has to read:
        // the pool slot and the native read hold both stay with the retained frame.
        var held = pool.GetSnapshot();
        Assert.Equal(1, held.OutstandingLeases);
        Assert.Equal(1, held.ActiveReaders);
        Assert.True(retained.IsLoanActive);
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, retained.GetRowSpan(1).ToArray());
        Assert.Equal(ExpectedCanonicalHash(fixture), CanonicalImagePixelContent.ComputeHash(retained));
        Assert.Throws<InvalidOperationException>(() => lease.Frame);

        retained.Dispose();
        retained.Dispose();
        Assert.False(retained.IsLoanActive);
        Assert.Throws<ObjectDisposedException>(() => retained.GetRowSpan(0).ToArray());
        AssertPoolIdle(pool);

        var reused = pool.TryCopyFrame(fixture.Metadata, fixture.Provenance, fixture.Source);
        Assert.True(reused.Succeeded, reused.ReasonCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, reused.Lease!.Frame.GetRowSpan(0).ToArray());
        reused.Lease.Dispose();
        AssertPoolIdle(pool);
    }

    [Fact]
    [Trait("VerificationId", "V150_F05")]
    public async Task V150_F05_SuccessfulStageBindsProductionIdentityHashesAndSingleUseConsumption()
    {
        var root = CreateStageRoot();
        try
        {
            var options = CreateOptions(root);
            var stager = new ProductionImageStager(options);
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, leaseId) = Retain(pool, fixture);
            var inspectionId = Guid.NewGuid();

            var claim = await stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(10));

            Assert.NotEqual(Guid.Empty, claim.StageId);
            Assert.Equal(claim.StageId.ToString("N") + ".stage", claim.StageFileName);
            Assert.Equal(Path.Combine(root, claim.StageFileName), claim.StageFilePath);
            Assert.True(File.Exists(claim.StageFilePath));
            Assert.Equal(inspectionId, claim.InspectionId);
            Assert.Equal(AdmissionHashA, claim.AdmissionContentHash);
            Assert.Equal(PolicyHashB, claim.EvidencePolicyContentHash);
            Assert.Equal(leaseId, claim.InputLeaseId);
            Assert.Equal(options.ContentHash, claim.StageRootBindingHash);
            Assert.True(claim.CompletedWithinDeadline);
            Assert.False(claim.IsConsumed);
            Assert.False(claim.IsDisposed);
            Assert.Equal(ExecutionKind.Production, claim.Metadata.Correlation.Kind);
            Assert.Equal(fixture.Metadata, claim.Metadata);
            Assert.Equal(fixture.Provenance.Correlation, claim.Provenance.Correlation);
            Assert.Equal(fixture.Provenance.ProviderId, claim.Provenance.ProviderId);
            var evidence = Assert.IsType<PoolCopyEvidence>(claim.Provenance.PoolCopyEvidence);
            Assert.Equal(fixture.Metadata.StrideBytes, evidence.DestinationStrideBytes);
            Assert.Equal(0, stager.ActiveOperationCount);
            Assert.True(stager.PhysicalCompletion.IsCompleted);
            AssertCanonicalFilePayload(claim, fixture, ExpectedCanonicalHash(fixture));

            claim.VerifyCommitProtection();
            claim.ConsumeForCommit(inspectionId, AdmissionHashA, PolicyHashB, options.ContentHash);
            Assert.True(claim.IsConsumed);
            claim.VerifyCommitProtection();
            claim.Dispose();
            Assert.True(claim.IsDisposed);
            Assert.True(File.Exists(claim.StageFilePath));
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Theory]
    [Trait("VerificationId", "V150_F06")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task V150_F06_StagedFileIsCanonicalEnvelopePlusValidRowsForEveryFormat(int variant)
    {
        var root = CreateStageRoot();
        try
        {
            var options = CreateOptions(root);
            var stager = new ProductionImageStager(options);
            using var pool = CreatePool();
            var fixture = PayloadFixture(variant);
            var (frame, _) = Retain(pool, fixture);
            var expectedHash = ExpectedCanonicalHash(fixture);

            // The public contract implementation and the test oracle must agree before the
            // stage runs, so a claim hash mismatch is never blamed on the oracle.
            Assert.Equal(expectedHash, CanonicalImagePixelContent.ComputeHash(frame));

            var claim = await stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(10));

            Assert.Equal(fixture.Metadata.Width, claim.Metadata.Width);
            Assert.Equal(fixture.Metadata.Height, claim.Metadata.Height);
            Assert.Equal(fixture.Metadata.PixelFormat, claim.Metadata.PixelFormat);
            Assert.Equal(fixture.Metadata.ValidBits, claim.Metadata.ValidBits);
            AssertCanonicalFilePayload(claim, fixture, expectedHash);
            claim.ConsumeForCommit(claim.InspectionId, AdmissionHashA, PolicyHashB, options.ContentHash);
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F07")]
    public async Task V150_F07_TheProtectedStageFileRefusesWriteAndDeleteUntilClaimDispose()
    {
        var root = CreateStageRoot();
        try
        {
            var stager = new ProductionImageStager(CreateOptions(root));
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, _) = Retain(pool, fixture);
            var claim = await stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(10));
            claim.VerifyCommitProtection();

            // The claim's read handle shares only for read, so readers may still verify the
            // file while a writer cannot reach it.
            using (var reader = new FileStream(claim.StageFilePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite))
            {
                Assert.Equal(claim.CanonicalByteLength, reader.Length);
            }
            Assert.Equal(CanonicalBytes(fixture.Metadata, fixture.Source),
                File.ReadAllBytes(claim.StageFilePath));

            var writeRefusal = Record.Exception(() =>
                new FileStream(claim.StageFilePath, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite).Dispose());
            Assert.IsAssignableFrom<IOException>(writeRefusal!);
            Assert.True(File.Exists(claim.StageFilePath));

            if (OperatingSystem.IsWindows())
            {
                var deleteRefusal = Record.Exception(() => File.Delete(claim.StageFilePath));
                Assert.NotNull(deleteRefusal);
                Assert.True(deleteRefusal is IOException or UnauthorizedAccessException,
                    deleteRefusal!.GetType().Name);
                Assert.True(File.Exists(claim.StageFilePath));
            }

            claim.Dispose();
            claim.Dispose();
            Assert.True(claim.IsDisposed);
            var lost = Assert.Throws<InvalidOperationException>(() => claim.VerifyCommitProtection());
            Assert.Equal("ProductionStageCommitProtectionLost", lost.Message);

            // Once the claim is disposed the protection is gone, so the file is ordinary.
            File.Delete(claim.StageFilePath);
            Assert.False(File.Exists(claim.StageFilePath));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F08")]
    public async Task V150_F08_ConsumeRejectsWrongRunAdmissionPolicyAndRootThenRepeats()
    {
        var root = CreateStageRoot();
        try
        {
            var options = CreateOptions(root);
            var stager = new ProductionImageStager(options);
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, _) = Retain(pool, fixture);
            var inspectionId = Guid.NewGuid();
            var claim = await stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(10));

            var wrongRun = Assert.Throws<InvalidOperationException>(() =>
                claim.ConsumeForCommit(Guid.NewGuid(), AdmissionHashA, PolicyHashB, options.ContentHash));
            Assert.Equal("ProductionStageClaimBindingMismatch", wrongRun.Message);
            var wrongAdmission = Assert.Throws<InvalidOperationException>(() =>
                claim.ConsumeForCommit(inspectionId, OtherHashC, PolicyHashB, options.ContentHash));
            Assert.Equal("ProductionStageClaimBindingMismatch", wrongAdmission.Message);
            var wrongPolicy = Assert.Throws<InvalidOperationException>(() =>
                claim.ConsumeForCommit(inspectionId, AdmissionHashA, OtherHashC, options.ContentHash));
            Assert.Equal("ProductionStageClaimBindingMismatch", wrongPolicy.Message);
            var wrongRoot = Assert.Throws<InvalidOperationException>(() =>
                claim.ConsumeForCommit(inspectionId, AdmissionHashA, PolicyHashB, OtherHashC));
            Assert.Equal("ProductionStageClaimBindingMismatch", wrongRoot.Message);

            // A rejected binding never consumes the claim: the matching consumption still works.
            Assert.False(claim.IsConsumed);
            claim.ConsumeForCommit(inspectionId, AdmissionHashA, PolicyHashB, options.ContentHash);
            Assert.True(claim.IsConsumed);
            var repeated = Assert.Throws<InvalidOperationException>(() =>
                claim.ConsumeForCommit(inspectionId, AdmissionHashA, PolicyHashB, options.ContentHash));
            Assert.Equal("ProductionStageClaimConsumed", repeated.Message);
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F09")]
    public async Task V150_F09_AdmissionRejectionsDisposeTheInputAndLeaveTheStagerReusable()
    {
        var root = CreateStageRoot();
        try
        {
            var options = CreateOptions(root);
            var stager = new ProductionImageStager(options);
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var inspectionId = Guid.NewGuid();

            var (emptyRun, _) = Retain(pool, fixture);
            var runRequired = await Assert.ThrowsAsync<ArgumentException>(() =>
                stager.StageAsync(Guid.Empty, AdmissionHashA, PolicyHashB, emptyRun,
                    TimeSpan.FromSeconds(10)));
            Assert.Contains("ProductionStageInspectionIdRequired", runRequired.Message);
            Assert.False(emptyRun.IsLoanActive);
            AssertPoolIdle(pool);

            var (badAdmission, _) = Retain(pool, fixture);
            var admissionHash = await Assert.ThrowsAsync<ArgumentException>(() =>
                stager.StageAsync(inspectionId, new string('a', 64), PolicyHashB, badAdmission,
                    TimeSpan.FromSeconds(10)));
            Assert.Contains("ProductionStageHashInvalid", admissionHash.Message);
            Assert.False(badAdmission.IsLoanActive);

            var (badPolicy, _) = Retain(pool, fixture);
            var policyHash = await Assert.ThrowsAsync<ArgumentException>(() =>
                stager.StageAsync(inspectionId, AdmissionHashA, new string('B', 63), badPolicy,
                    TimeSpan.FromSeconds(10)));
            Assert.Contains("ProductionStageHashInvalid", policyHash.Message);
            Assert.False(badPolicy.IsLoanActive);

            var (zeroTimeout, _) = Retain(pool, fixture);
            var timeoutRange = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, zeroTimeout,
                    TimeSpan.Zero));
            Assert.Equal("timeout", timeoutRange.ParamName);
            Assert.False(zeroTimeout.IsLoanActive);

            var (overTimeout, _) = Retain(pool, fixture);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, overTimeout,
                    TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1)));
            Assert.False(overTimeout.IsLoanActive);

            var (cancelled, _) = Retain(pool, fixture);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelledStage = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, cancelled,
                    TimeSpan.FromSeconds(10), cancellation.Token));
            Assert.Equal("ProductionStageCancelled", cancelledStage.Message);
            Assert.False(cancelled.IsLoanActive);
            AssertPoolIdle(pool);

            // A null frame owns nothing and is rejected before any admission side effect.
            var nullFrame = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, null!,
                    TimeSpan.FromSeconds(10)));
            Assert.Equal("frame", nullFrame.ParamName);

            // Every rejection above left the single operation slot free and the stager usable.
            var (accepted, _) = Retain(pool, fixture);
            var claim = await stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, accepted,
                TimeSpan.FromSeconds(10));
            Assert.Equal(0, stager.ActiveOperationCount);
            AssertCanonicalFilePayload(claim, fixture, ExpectedCanonicalHash(fixture));
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F10")]
    public async Task V150_F10_DeadlineAbandonsAPausedWorkerAndLateCompletionYieldsNoClaim()
    {
        var root = CreateStageRoot();
        try
        {
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var stager = new ProductionImageStager(CreateOptions(root), boundary =>
            {
                if (boundary != StageBoundary.BeforeFlush) return;
                entered.Set();
                // Bounded so a failing case still finishes instead of hanging the suite.
                release.Wait(TimeSpan.FromSeconds(30));
            });
            using var pool = CreatePool(capacity: 2, maximumFrameBytes: 16);
            var fixture = PayloadFixture(1);
            try
            {
                var (paused, _) = Retain(pool, fixture);
                var stopwatch = Stopwatch.StartNew();
                var pausedStage = stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB,
                    paused, TimeSpan.FromMilliseconds(100));
                var timeout = await Assert.ThrowsAsync<TimeoutException>(() => pausedStage);
                stopwatch.Stop();

                Assert.Equal("ProductionStageDeadlineExceeded", timeout.Message);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"stage deadline returned after {stopwatch.Elapsed}");
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

                // The paused worker is still a physical operation: the frame it reads keeps
                // the pool slot, its native read hold and one active operation.
                Assert.Equal(1, stager.ActiveOperationCount);
                Assert.False(stager.PhysicalCompletion.IsCompleted);
                var held = pool.GetSnapshot();
                Assert.Equal(1, held.OutstandingLeases);
                Assert.Equal(1, held.ActiveReaders);

                // A second stage while the physical operation is unfinished is rejected
                // immediately, and its own input is disposed rather than queued or leaked.
                var (rejected, _) = Retain(pool, fixture);
                Assert.Equal(2, pool.GetSnapshot().OutstandingLeases);
                var inProgress = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, rejected,
                        TimeSpan.FromSeconds(10)));
                Assert.Equal("ProductionStageOperationInProgress", inProgress.Message);
                Assert.False(rejected.IsLoanActive);
                Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
                Assert.Equal(1, stager.ActiveOperationCount);

                // Releasing the worker lets the abandoned operation finish its file work.
                // No claim ever becomes available to the timed-out caller, and the late file
                // stays behind as an orphan while the slot is finally released.
                release.Set();
                await stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, stager.ActiveOperationCount);
                AssertPoolIdle(pool);
                var orphan = Assert.Single(Directory.GetFiles(root, "*.stage"));
                Assert.Equal(Mono8CanonicalByteLength, new FileInfo(orphan).Length);

                // The stager takes a fresh operation afterwards and mints a distinct claim;
                // the abandoned file is never handed out again.
                var (fresh, _) = Retain(pool, fixture);
                var claim = await stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB,
                    fresh, TimeSpan.FromSeconds(10));
                Assert.NotEqual(orphan, claim.StageFilePath);
                AssertCanonicalFilePayload(claim, fixture, ExpectedCanonicalHash(fixture));
                claim.Dispose();
                Assert.Equal(2, Directory.GetFiles(root, "*.stage").Length);
            }
            finally
            {
                release.Set();
            }
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F11")]
    public async Task V150_F11_CapacityCountsForeignPartialAndStageEntriesBeforeWriting()
    {
        var root = CreateStageRoot();
        try
        {
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var inspectionId = Guid.NewGuid();

            // A foreign directory is not a stage file and is refused outright.
            var foreign = Path.Combine(root, "foreign-directory");
            Directory.CreateDirectory(foreign);
            var (directoryInput, _) = Retain(pool, fixture);
            var entryInvalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ProductionImageStager(CreateOptions(root)).StageAsync(inspectionId,
                    AdmissionHashA, PolicyHashB, directoryInput, TimeSpan.FromSeconds(10)));
            Assert.Equal("ProductionStageRootEntryInvalid", entryInvalid.Message);
            Assert.False(directoryInput.IsLoanActive);
            Assert.Empty(Directory.GetFiles(root));
            Directory.Delete(foreign);
            AssertPoolIdle(pool);

            // The single-file ceiling counts the canonical envelope, so one byte under the
            // canonical length is refused before the partial is ever created.
            var (sizeInput, _) = Retain(pool, fixture);
            var fileSize = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ProductionImageStager(CreateOptions(root, maximumStageBytes: 37,
                        maximumTotalStageBytes: 512, maximumStageFiles: 8))
                    .StageAsync(inspectionId, AdmissionHashA, PolicyHashB, sizeInput,
                        TimeSpan.FromSeconds(10)));
            Assert.Equal("ProductionStageFileSizeExceeded", fileSize.Message);
            Assert.False(sizeInput.IsLoanActive);
            Assert.Empty(Directory.GetFiles(root));
            AssertPoolIdle(pool);

            // Total capacity counts an orphan already sitting under the root.
            var stageOrphan = Path.Combine(root, "orphan-" + Guid.NewGuid().ToString("N") + ".stage");
            await File.WriteAllBytesAsync(stageOrphan, new byte[60]);
            var (totalInput, _) = Retain(pool, fixture);
            var total = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ProductionImageStager(CreateOptions(root, maximumStageBytes: 38,
                        maximumTotalStageBytes: 64, maximumStageFiles: 8))
                    .StageAsync(inspectionId, AdmissionHashA, PolicyHashB, totalInput,
                        TimeSpan.FromSeconds(10)));
            Assert.Equal("ProductionStageTotalCapacityExceeded", total.Message);
            Assert.False(totalInput.IsLoanActive);
            AssertPoolIdle(pool);

            // File count capacity counts orphanned partial and stage paths alike.
            var partialOrphan = Path.Combine(root,
                "orphan-" + Guid.NewGuid().ToString("N") + ".partial");
            await File.WriteAllBytesAsync(partialOrphan, new byte[1]);
            var (countInput, _) = Retain(pool, fixture);
            var fileCount = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new ProductionImageStager(CreateOptions(root, maximumStageBytes: 512,
                        maximumTotalStageBytes: 4096, maximumStageFiles: 2))
                    .StageAsync(inspectionId, AdmissionHashA, PolicyHashB, countInput,
                        TimeSpan.FromSeconds(10)));
            Assert.Equal("ProductionStageFileCapacityExceeded", fileCount.Message);
            Assert.False(countInput.IsLoanActive);
            Assert.Equal(2, Directory.GetFiles(root).Length);
            AssertPoolIdle(pool);

            // The same orphans stay within the deployment limits, so they are counted, not
            // deleted: the next stage completes and leaves every earlier file in place.
            var (accepted, _) = Retain(pool, fixture);
            var claim = await new ProductionImageStager(CreateOptions(root)).StageAsync(inspectionId,
                AdmissionHashA, PolicyHashB, accepted, TimeSpan.FromSeconds(10));
            Assert.Equal(Mono8CanonicalByteLength, claim.CanonicalByteLength);
            Assert.Equal(3, Directory.GetFiles(root).Length);
            Assert.True(File.Exists(stageOrphan));
            Assert.True(File.Exists(partialOrphan));
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F12")]
    public void V150_F12_IllegalRootsAreRejectedBeforeAnyStagingWork()
    {
        var root = CreateStageRoot();
        try
        {
            var relative = Assert.Throws<ArgumentException>(() =>
                new ProductionImageStageOptions("relative-stage-root", 1024, 2048, 8));
            Assert.Contains("ProductionImageStageRootMustBeExplicitLocalPath", relative.Message);

            // Extended-length and device prefixes are never accepted as a staging root.
            var extended = Assert.Throws<ArgumentException>(() =>
                new ProductionImageStageOptions(@"\\?\" + root, 1024, 2048, 8));
            Assert.Contains("ProductionImageStageRootMustBeExplicitLocalPath", extended.Message);
            var device = Assert.Throws<ArgumentException>(() =>
                new ProductionImageStageOptions(@"\\.\" + root, 1024, 2048, 8));
            Assert.Contains("ProductionImageStageRootMustBeExplicitLocalPath", device.Message);
            var unc = Assert.Throws<ArgumentException>(() =>
                new ProductionImageStageOptions(@"\\?\UNC\localhost\stage-root", 1024, 2048, 8));
            Assert.Contains("ProductionImageStageRootMustBeExplicitLocalPath", unc.Message);

            // A fully qualified local root that does not exist is refused as missing.
            var missingPath = Path.Combine(root, "missing-" + Guid.NewGuid().ToString("N"));
            var missing = Assert.Throws<ArgumentException>(() =>
                new ProductionImageStageOptions(missingPath, 1024, 2048, 8));
            Assert.Contains("ProductionImageStageRootMissing", missing.Message);

            // A path segment naming a cloud-synchronized folder is refused by the storage
            // validator, so it cannot host a stage root even though the directory exists.
            var cloud = Path.Combine(root, "OneDrive");
            Directory.CreateDirectory(cloud);
            var cloudRejected = Assert.Throws<InvalidOperationException>(() =>
                new ProductionImageStageOptions(cloud, 1024, 2048, 8));
            Assert.StartsWith("ProductionImageStageRootRejected", cloudRejected.Message);
            Assert.Contains("StorePathCloudSynchronized", cloudRejected.Message);

            // A directory reparse point (junction or symlink) is refused when this host can
            // create one; without that privilege the case degrades instead of failing.
            if (OperatingSystem.IsWindows())
            {
                var target = Path.Combine(root, "reparse-target");
                Directory.CreateDirectory(target);
                var link = Path.Combine(root, "reparse-link");
                var created = false;
                try
                {
                    Directory.CreateSymbolicLink(link, target);
                    created = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or PlatformNotSupportedException)
                {
                    created = false;
                }

                if (created)
                {
                    var reparse = Assert.Throws<InvalidOperationException>(() =>
                        new ProductionImageStageOptions(link, 1024, 2048, 8));
                    Assert.StartsWith("ProductionImageStageRootRejected", reparse.Message);
                    Assert.Contains("StorePathReparsePoint", reparse.Message);
                }
            }
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F13")]
    public async Task V150_F13_AClaimThatBeatItsDeadlineStaysConsumableAfterLaterLatency()
    {
        var root = CreateStageRoot();
        try
        {
            var options = CreateOptions(root);
            var stager = new ProductionImageStager(options);
            using var pool = CreatePool();
            var fixture = PayloadFixture(1);
            var (frame, _) = Retain(pool, fixture);
            var inspectionId = Guid.NewGuid();

            var claim = await stager.StageAsync(inspectionId, AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(2));
            Assert.True(claim.CompletedWithinDeadline);

            // The deadline measured the file barrier, so caller latency after the claim was
            // minted cannot invalidate it: it is still protected and still single-use.
            await Task.Delay(TimeSpan.FromMilliseconds(2200));
            claim.VerifyCommitProtection();
            claim.ConsumeForCommit(inspectionId, AdmissionHashA, PolicyHashB, options.ContentHash);
            Assert.True(claim.IsConsumed);
            var bytes = await File.ReadAllBytesAsync(claim.StageFilePath);
            Assert.Equal(ExpectedCanonicalHash(fixture), Convert.ToHexString(SHA256.HashData(bytes)));
            claim.Dispose();
        }
        finally
        {
            DeleteStageRoot(root);
        }
    }

    /// <summary>
    /// Asserts that the claim file is exactly the canonical envelope followed by the valid
    /// rows: the recorded length, the staged digest on disk, the public contract hash and the
    /// test oracle must all agree, and stride padding must not appear anywhere in the file.
    /// </summary>
    [Fact]
    [Trait("VerificationId", "V150_F14")]
    public async Task V150_F14_KnownPathsHashesAndAnOpenFileCannotMintACommitClaim()
    {
        var root = CreateStageRoot();
        try
        {
            var stager = new ProductionImageStager(CreateOptions(root));
            using var pool = CreatePool();
            var (frame, _) = Retain(pool, PayloadFixture(1));
            using var real = await stager.StageAsync(Guid.NewGuid(), AdmissionHashA, PolicyHashB, frame,
                TimeSpan.FromSeconds(10));
            using var reader = new FileStream(real.StageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var error = Assert.Throws<InvalidOperationException>(() => ProductionImageStager.StageCommitClaim.Create(
                stager, new object(), real.StageId, real.InspectionId, real.AdmissionContentHash,
                real.EvidencePolicyContentHash, real.InputLeaseId, real.Metadata, real.Provenance,
                real.CanonicalPixelHash, real.CanonicalByteLength, real.StageFileName, real.StageFilePath,
                real.StageRootBindingHash, completedWithinDeadline: true, reader));
            Assert.Equal("ProductionStageClaimIssuerInvalid", error.Message);
            real.ConsumeForCommit(real.InspectionId, AdmissionHashA, PolicyHashB, real.StageRootBindingHash);
        }
        finally { DeleteStageRoot(root); }
    }

    private static void AssertCanonicalFilePayload(ProductionImageStager.StageCommitClaim claim,
        StageFixture fixture, string expectedHash)
    {
        var metadata = fixture.Metadata;
        var rows = checked((int)(metadata.ValidRowBytes * metadata.Height));
        Assert.True(checked(metadata.StrideBytes * metadata.Height) > rows,
            "fixture must carry stride padding so exclusion is observable");
        Assert.Equal(checked(32 + rows), (int)claim.CanonicalByteLength);
        Assert.Equal(expectedHash, claim.CanonicalPixelHash);

        var expected = CanonicalBytes(metadata, fixture.Source);
        var envelope = CanonicalImagePixelContent.CreateEnvelope(metadata.Width, metadata.Height,
            metadata.PixelFormat, metadata.ValidBits);
        var fileBytes = File.ReadAllBytes(claim.StageFilePath);
        Assert.Equal(expected.Length, fileBytes.Length);
        Assert.Equal(envelope, fileBytes.AsSpan(0, envelope.Length).ToArray());
        Assert.Equal(expected, fileBytes);
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(fileBytes)));
    }

    /// <summary>Independent oracle: SHA-256 over the 32-byte envelope and the valid rows.</summary>
    private static string ExpectedCanonicalHash(StageFixture fixture) =>
        Convert.ToHexString(SHA256.HashData(CanonicalBytes(fixture.Metadata, fixture.Source)));

    private static byte[] CanonicalBytes(FrameMetadata metadata, byte[] source)
    {
        var envelope = CanonicalImagePixelContent.CreateEnvelope(metadata.Width, metadata.Height,
            metadata.PixelFormat, metadata.ValidBits);
        var rows = checked((int)(metadata.ValidRowBytes * metadata.Height));
        var bytes = new byte[checked(envelope.Length + rows)];
        Buffer.BlockCopy(envelope, 0, bytes, 0, envelope.Length);
        for (var row = 0; row < metadata.Height; row++)
        {
            var rowOffset = checked(row * metadata.StrideBytes);
            Buffer.BlockCopy(source, rowOffset, bytes, checked(envelope.Length + row * metadata.ValidRowBytes),
                metadata.ValidRowBytes);
        }

        return bytes;
    }

    /// <summary>
    /// Copies the fixture pixels through the real pool, captures the native read hold, and
    /// then closes the ordinary lease: the retained frame owns the slot from here on.
    /// </summary>
    private static (RetainedProductionFrame Frame, Guid LeaseId) Retain(FrameBufferPool pool,
        StageFixture fixture)
    {
        var copy = pool.TryCopyFrame(fixture.Metadata, fixture.Provenance, fixture.Source);
        Assert.True(copy.Succeeded, copy.ReasonCode);
        var lease = copy.Lease!;
        var leaseId = lease.LeaseId;
        var frame = RetainedProductionFrame.Capture(lease);
        lease.Dispose();
        return (frame, leaseId);
    }

    private static void AssertPoolIdle(FrameBufferPool pool)
    {
        var snapshot = pool.GetSnapshot();
        Assert.Equal(0, snapshot.OutstandingLeases);
        Assert.Equal(0, snapshot.ActiveReaders);
    }

    private static FrameBufferPool CreatePool(int capacity = 1, int maximumFrameBytes = 16) =>
        new(new FrameBufferPoolOptions(capacity, maximumFrameBytes, TimeSpan.FromSeconds(1)));

    private static ProductionImageStageOptions CreateOptions(string root,
        long maximumStageBytes = DefaultMaximumStageBytes,
        long maximumTotalStageBytes = DefaultMaximumTotalStageBytes,
        int maximumStageFiles = DefaultMaximumStageFiles) =>
        new(root, maximumStageBytes, maximumTotalStageBytes, maximumStageFiles);

    /// <summary>
    /// 1: 3x2 Mono8 stride 5. 2: 2x2 Mono16 Valid Bits 10. 3: Mono16 Valid Bits 12.
    /// 4: Mono16 Valid Bits 16. 5: 2x1 Bgr24 stride 8. Every fixture carries padding words
    /// that differ from its valid pixels, so padding exclusion is observable.
    /// </summary>
    private static StageFixture PayloadFixture(int variant) => variant switch
    {
        1 => Fixture(3, 2, 5, VisionPixelFormat.Mono8, null,
            new byte[] { 1, 2, 3, 0xEE, 0xEE, 4, 5, 6, 0xEE, 0xEE }),
        2 => Fixture(2, 2, 8, VisionPixelFormat.Mono16, 10,
            UInt16Bytes(0, 1023, 0xEEEE, 0xEEEE, 512, 7, 0xEEEE, 0xEEEE)),
        3 => Fixture(2, 2, 8, VisionPixelFormat.Mono16, 12,
            UInt16Bytes(4095, 2048, 0xEEEE, 0xEEEE, 1, 0, 0xEEEE, 0xEEEE)),
        4 => Fixture(2, 2, 8, VisionPixelFormat.Mono16, 16,
            UInt16Bytes(65535, 32768, 0xEEEE, 0xEEEE, 1, 0, 0xEEEE, 0xEEEE)),
        5 => Fixture(2, 1, 8, VisionPixelFormat.Bgr24, null,
            new byte[] { 9, 8, 7, 6, 5, 4, 0xEE, 0xEE }),
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    private static StageFixture Fixture(int width, int height, int stride,
        VisionPixelFormat pixelFormat, int? validBits, byte[] source)
    {
        var correlation = new ExecutionCorrelationId(ExecutionKind.Production, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(ProductionAcquisitionMode.HardwareTrigger,
            500, 1.5, new RegionOfInterest(0, 0, width, height), pixelFormat, validBits, 500, 0, null);
        var metadata = new FrameMetadata(correlation, "TopCamera", width, height, stride, pixelFormat,
            validBits, Utc(10), configuration);
        var provenance = new FrameProvenance(correlation, "vendor-a", "1", "adapter-a", "1", "sdk-a",
            "1", null, "device-1", null, null, pixelFormat.ToString(), "normalized-v1", false, false,
            null, null, Milestones());
        return new StageFixture(metadata, provenance, source);
    }

    private static byte[] UInt16Bytes(params ushort[] values)
    {
        var bytes = new byte[checked(values.Length * 2)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2, 2), values[index]);
        return bytes;
    }

    private static FrameAcquisitionMilestones Milestones() =>
        new(1_000_000, new FrameTimePoint(Utc(1), 10),
            new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
            new FrameTimePoint(Utc(4), 16));

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private static string CreateStageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests", "V150-ImageStaging",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteStageRoot(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover staging directory is evidence, not a reason to fail a case.
        }
    }

    private sealed record StageFixture(FrameMetadata Metadata, FrameProvenance Provenance,
        byte[] Source);
}
