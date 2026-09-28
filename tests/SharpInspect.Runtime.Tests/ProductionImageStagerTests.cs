using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Images;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionImageStagerTests
{
    private const string AdmissionHash = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string PolicyHash = "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    [Trait("VerificationId", "V150_F01")]
    public void V150_F01_RetainedFrameKeepsPoolSlotAndPixelsAfterOriginalLeaseDispose()
    {
        var metadata = Metadata(ExecutionKind.Manual, width: 3, height: 2, stride: 5);
        var provenance = Provenance(metadata);
        using var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 16, TimeSpan.FromSeconds(1)));
        var copied = pool.TryCopyFrame(metadata, provenance,
            new byte[] { 1, 2, 3, 0xA1, 0xA2, 4, 5, 6 });
        var lease = Assert.IsType<FrameBufferLease>(copied.Lease);
        var leaseProvenance = lease.Provenance;

        var retained = RetainedProductionFrame.Capture(lease);

        Assert.Equal(lease.LeaseId, retained.LeaseId);
        Assert.Same(leaseProvenance, retained.Provenance);
        Assert.Throws<InvalidOperationException>(() => RetainedProductionFrame.Capture(lease));
        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);

        lease.Dispose();

        Assert.Equal(1, pool.GetSnapshot().OutstandingLeases);
        Assert.True(retained.IsLoanActive);
        Assert.Equal(new byte[] { 1, 2, 3 }, retained.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 4, 5, 6 }, retained.GetRowSpan(1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => retained.GetRowSpan(2).ToArray());

        var blocked = pool.TryCopyFrame(metadata, provenance,
            new byte[] { 9, 9, 9, 0xB1, 0xB2, 9, 9, 9 });
        Assert.False(blocked.Succeeded);
        Assert.Equal("FrameBufferExhausted", blocked.ReasonCode);
        Assert.Null(blocked.Lease);

        retained.Dispose();
        retained.Dispose();
        Assert.False(retained.IsLoanActive);
        Assert.Throws<ObjectDisposedException>(() => retained.GetRowSpan(0).ToArray());
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);

        var reused = pool.TryCopyFrame(metadata, provenance,
            new byte[] { 7, 7, 7, 0xC1, 0xC2, 8, 8, 8 });
        Assert.True(reused.Succeeded, reused.ReasonCode);
        Assert.Equal(new byte[] { 7, 7, 7 }, reused.Lease!.Frame.GetRowSpan(0).ToArray());
        reused.Lease.Dispose();
        Assert.Equal(0, pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    [Trait("VerificationId", "V150_F02")]
    public void V150_F02_PublicSurfaceCannotMintNativeReadHolds()
    {
        Assert.True(typeof(ProductionImageStageOptions).IsPublic);
        Assert.False(typeof(ProductionImageStager).IsPublic);
        Assert.False(typeof(RetainedProductionFrame).IsPublic);
        Assert.False(typeof(NativeFrameReadLease).IsPublic);
        var acquire = typeof(VisionFrame).GetMethod("AcquireNativeRead",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(acquire);
        Assert.True(acquire!.IsAssembly);
        Assert.False(acquire.IsPublic);

        var frame = new PublicSurfaceFrame(
            Metadata(ExecutionKind.Manual, width: 2, height: 1, stride: 2), new byte[] { 1, 2 });
        var failure = Assert.Throws<InvalidOperationException>(() => frame.AcquireNativeRead());
        Assert.Equal("VisionFrameNativeBorrowUnavailable", failure.Message);
    }

    [Fact]
    [Trait("VerificationId", "V150_F03")]
    public async Task V150_F03_StageWritesCanonicalEnvelopeAndValidRowsOnly()
    {
        using var fixture = StageFixture.Create();
        var source = fixture.CreateFrame(width: 3, height: 2, stride: 5);
        var metadata = source.Metadata;
        var provenance = source.Provenance;
        var frame = RetainedProductionFrame.Capture(source.Lease);
        var inspectionId = Guid.NewGuid();

        var claim = await fixture.Stager.StageAsync(inspectionId, AdmissionHash, PolicyHash, frame,
            TimeSpan.FromSeconds(5));

        Assert.Equal(inspectionId, claim.InspectionId);
        Assert.Equal(AdmissionHash, claim.AdmissionContentHash);
        Assert.Equal(PolicyHash, claim.EvidencePolicyContentHash);
        Assert.Equal(source.Lease.LeaseId, claim.InputLeaseId);
        Assert.Same(metadata, claim.Metadata);
        Assert.Same(provenance, claim.Provenance);
        Assert.Equal(source.CanonicalHash, claim.CanonicalPixelHash);
        Assert.Equal(32 + metadata.ValidRowBytes * metadata.Height, claim.CanonicalByteLength);
        Assert.Equal(fixture.Options.ContentHash, claim.StageRootBindingHash);
        Assert.Equal(claim.StageId.ToString("N") + ".stage", claim.StageFileName);
        Assert.Equal(Path.Combine(fixture.Root, claim.StageFileName), claim.StageFilePath);

        var expected = source.Envelope.Concat(source.ValidRows).ToArray();
        Assert.Equal(expected, await File.ReadAllBytesAsync(claim.StageFilePath));
        Assert.Equal(38, claim.CanonicalByteLength);
        Assert.False(frame.IsLoanActive);
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Stager.ActiveOperationCount);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);

        claim.VerifyCommitProtection();
        claim.ConsumeForCommit(inspectionId, AdmissionHash, PolicyHash, fixture.Options.ContentHash);
        Assert.True(claim.IsConsumed);
        claim.Dispose();
        Assert.True(claim.IsDisposed);
    }

    [Theory]
    [InlineData((int)StageBoundary.BeforeWrite)]
    [InlineData((int)StageBoundary.AfterWrite)]
    [InlineData((int)StageBoundary.BeforeFlush)]
    [InlineData((int)StageBoundary.AfterFlush)]
    [InlineData((int)StageBoundary.BeforeVerify)]
    [InlineData((int)StageBoundary.AfterVerify)]
    [InlineData((int)StageBoundary.BeforeRename)]
    [InlineData((int)StageBoundary.AfterRename)]
    [Trait("VerificationId", "V150_F04")]
    public async Task V150_F04_BoundaryFaultsProduceNoClaimAndDisposeTheFrame(int boundaryValue)
    {
        var boundary = (StageBoundary)boundaryValue;
        using var fixture = StageFixture.Create(faultHook: (_, observed) =>
        {
            if (observed == boundary)
                throw new InvalidOperationException("TestFault:" + observed);
        });
        var source = fixture.CreateFrame(width: 3, height: 2, stride: 5);
        var frame = RetainedProductionFrame.Capture(source.Lease);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                TimeSpan.FromSeconds(5)));

        Assert.Equal("TestFault:" + boundary, failure.Message);
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Stager.ActiveOperationCount);
        Assert.False(frame.IsLoanActive);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        var orphans = Directory.GetFiles(fixture.Root);
        if (boundary == StageBoundary.BeforeWrite)
        {
            Assert.Empty(orphans);
        }
        else
        {
            // Orphans are never cleaned automatically; audited reconciliation owns them.
            Assert.Equal(boundary == StageBoundary.AfterRename ? ".stage" : ".partial",
                Path.GetExtension(Assert.Single(orphans)));
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F05")]
    public async Task V150_F05_ReadbackCorruptionIsRejectedBeforeTheClaimExists()
    {
        using var fixture = StageFixture.Create(faultHook: (root, boundary) =>
        {
            if (boundary != StageBoundary.BeforeVerify) return;
            var partial = Assert.Single(Directory.GetFiles(root, "*.partial"));
            var bytes = File.ReadAllBytes(partial);
            bytes[^1] ^= 0xFF;
            File.WriteAllBytes(partial, bytes);
        });
        var source = fixture.CreateFrame(width: 3, height: 2, stride: 5);
        var frame = RetainedProductionFrame.Capture(source.Lease);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                TimeSpan.FromSeconds(5)));

        Assert.Equal("ProductionStageHashMismatch", failure.Message);
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(frame.IsLoanActive);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(".partial", Path.GetExtension(Assert.Single(Directory.GetFiles(fixture.Root))));
    }

    [Fact]
    [Trait("VerificationId", "V150_F06")]
    public async Task V150_F06_Mono16HighBitsAreRevalidatedThroughTheProtectedHandle()
    {
        using var fixture = StageFixture.Create(faultHook: (root, boundary) =>
        {
            if (boundary != StageBoundary.AfterRename) return;
            var staged = Assert.Single(Directory.GetFiles(root, "*.stage"));
            var bytes = File.ReadAllBytes(staged);
            bytes[32] = 0xFF;
            bytes[33] = 0xFF;
            File.WriteAllBytes(staged, bytes);
        });
        var source = fixture.CreateFrame(width: 2, height: 1, stride: 4,
            format: VisionPixelFormat.Mono16, validBits: 10, source: UInt16Bytes(5, 6));
        var frame = RetainedProductionFrame.Capture(source.Lease);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                TimeSpan.FromSeconds(5)));

        Assert.Equal("ProductionStageMono16HighBitsInvalid", failure.Message);
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(frame.IsLoanActive);
        Assert.Equal(".stage", Path.GetExtension(Assert.Single(Directory.GetFiles(fixture.Root))));
    }

    [Fact]
    [Trait("VerificationId", "V150_F07")]
    public async Task V150_F07_ClaimProtectsTheStagedFileUntilDispose()
    {
        using var fixture = StageFixture.Create();
        var source = fixture.CreateFrame(width: 2, height: 2, stride: 2);
        var frame = RetainedProductionFrame.Capture(source.Lease);

        var claim = await fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
            TimeSpan.FromSeconds(5));

        claim.VerifyCommitProtection();
        Assert.Throws<IOException>(() => new FileStream(claim.StageFilePath, FileMode.Open,
            FileAccess.Write, FileShare.None));
        Assert.Throws<IOException>(() => File.Delete(claim.StageFilePath));
        using (var reader = new FileStream(claim.StageFilePath, FileMode.Open, FileAccess.Read,
                   FileShare.Read))
        {
            Assert.Equal(claim.CanonicalByteLength, reader.Length);
        }

        claim.Dispose();
        claim.Dispose();
        Assert.Throws<InvalidOperationException>(() => claim.VerifyCommitProtection());
        File.Delete(claim.StageFilePath);
        Assert.False(File.Exists(claim.StageFilePath));
    }

    [Fact]
    [Trait("VerificationId", "V150_F08")]
    public async Task V150_F08_ConsumeValidatesBindingAndIsSingleUse()
    {
        using var fixture = StageFixture.Create();
        var source = fixture.CreateFrame(width: 2, height: 1, stride: 2);
        var frame = RetainedProductionFrame.Capture(source.Lease);
        var inspectionId = Guid.NewGuid();
        var claim = await fixture.Stager.StageAsync(inspectionId, AdmissionHash, PolicyHash, frame,
            TimeSpan.FromSeconds(5));

        var wrongRun = Assert.Throws<InvalidOperationException>(() =>
            claim.ConsumeForCommit(Guid.NewGuid(), AdmissionHash, PolicyHash, fixture.Options.ContentHash));
        Assert.Equal("ProductionStageClaimBindingMismatch", wrongRun.Message);
        var wrongAdmission = Assert.Throws<InvalidOperationException>(() =>
            claim.ConsumeForCommit(inspectionId, PolicyHash, PolicyHash, fixture.Options.ContentHash));
        Assert.Equal("ProductionStageClaimBindingMismatch", wrongAdmission.Message);
        var wrongPolicy = Assert.Throws<InvalidOperationException>(() =>
            claim.ConsumeForCommit(inspectionId, AdmissionHash, AdmissionHash, fixture.Options.ContentHash));
        Assert.Equal("ProductionStageClaimBindingMismatch", wrongPolicy.Message);
        var wrongRoot = Assert.Throws<InvalidOperationException>(() =>
            claim.ConsumeForCommit(inspectionId, AdmissionHash, PolicyHash, PolicyHash));
        Assert.Equal("ProductionStageClaimBindingMismatch", wrongRoot.Message);
        Assert.False(claim.IsConsumed);

        claim.ConsumeForCommit(inspectionId, AdmissionHash, PolicyHash, fixture.Options.ContentHash);
        Assert.True(claim.IsConsumed);
        var second = Assert.Throws<InvalidOperationException>(() =>
            claim.ConsumeForCommit(inspectionId, AdmissionHash, PolicyHash, fixture.Options.ContentHash));
        Assert.Equal("ProductionStageClaimConsumed", second.Message);

        claim.Dispose();
        var afterDispose = Assert.Throws<InvalidOperationException>(() =>
            claim.ConsumeForCommit(inspectionId, AdmissionHash, PolicyHash, fixture.Options.ContentHash));
        Assert.Equal("ProductionStageClaimDisposed", afterDispose.Message);
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Stager.ActiveOperationCount);
    }

    [Fact]
    [Trait("VerificationId", "V150_F09")]
    public async Task V150_F09_TimeoutReturnsPromptlyAndLateCompletionCannotMintAClaim()
    {
        using var hookEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var fixture = StageFixture.Create(faultHook: (_, boundary) =>
        {
            if (boundary != StageBoundary.BeforeFlush) return;
            hookEntered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        var source = fixture.CreateFrame(width: 2, height: 2, stride: 2);
        var frame = RetainedProductionFrame.Capture(source.Lease);

        var stopwatch = Stopwatch.StartNew();
        var staging = fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
            TimeSpan.FromMilliseconds(200));
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => staging);
        stopwatch.Stop();

        Assert.Equal("ProductionStageDeadlineExceeded", failure.Message);
        Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), stopwatch.Elapsed.ToString());
        // The outer await never disposes the hold while the worker still reads.
        Assert.True(frame.IsLoanActive);
        Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);
        Assert.Equal(1, fixture.Stager.ActiveOperationCount);

        release.Set();
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Stager.ActiveOperationCount);
        Assert.False(frame.IsLoanActive);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
        // The late worker finished as an orphan: its unclaimed file is not protected.
        var orphan = Assert.Single(Directory.GetFiles(fixture.Root, "*.stage"));
        File.Delete(orphan);
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    [Trait("VerificationId", "V150_F10")]
    public async Task V150_F10_SecondOperationIsRejectedImmediatelyAndDisposed()
    {
        using var hookEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var fixture = StageFixture.Create(faultHook: (_, boundary) =>
        {
            if (boundary != StageBoundary.BeforeWrite) return;
            hookEntered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        var first = fixture.CreateFrame(width: 2, height: 1, stride: 2);
        var firstFrame = RetainedProductionFrame.Capture(first.Lease);
        var firstStaging = fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash,
            firstFrame, TimeSpan.FromSeconds(30));
        Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Stager.ActiveOperationCount);

        var second = fixture.CreateFrame(width: 2, height: 1, stride: 2);
        var secondFrame = RetainedProductionFrame.Capture(second.Lease);
        var rejected = Assert.Throws<InvalidOperationException>(() =>
            fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, secondFrame,
                TimeSpan.FromSeconds(30)));

        Assert.Equal("ProductionStageOperationInProgress", rejected.Message);
        Assert.False(secondFrame.IsLoanActive);
        Assert.True(firstFrame.IsLoanActive);
        Assert.Equal(1, fixture.Pool.GetSnapshot().OutstandingLeases);

        release.Set();
        var claim = await firstStaging;
        claim.Dispose();
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fixture.Stager.ActiveOperationCount);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
    }

    [Fact]
    [Trait("VerificationId", "V150_F11")]
    public async Task V150_F11_CapacityRefusalsLeaveNoStageAndReleaseTheFrame()
    {
        using (var fixture = StageFixture.Create(maximumStageBytes: 37, maximumTotalStageBytes: 4096,
                   maximumStageFiles: 8))
        {
            var source = fixture.CreateFrame(width: 3, height: 2, stride: 3);
            var frame = RetainedProductionFrame.Capture(source.Lease);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                    TimeSpan.FromSeconds(5)));
            Assert.Equal("ProductionStageFileSizeExceeded", failure.Message);
            await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(frame.IsLoanActive);
            Assert.Empty(Directory.GetFiles(fixture.Root));
        }

        using (var fixture = StageFixture.Create(maximumStageBytes: 4096, maximumTotalStageBytes: 40,
                   maximumStageFiles: 8))
        {
            fixture.WriteOrphan("existing.bin", 10);
            var source = fixture.CreateFrame(width: 3, height: 2, stride: 3);
            var frame = RetainedProductionFrame.Capture(source.Lease);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                    TimeSpan.FromSeconds(5)));
            Assert.Equal("ProductionStageTotalCapacityExceeded", failure.Message);
            await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(frame.IsLoanActive);
            Assert.Equal(1, Directory.GetFiles(fixture.Root).Length);
        }

        using (var fixture = StageFixture.Create(maximumStageBytes: 4096, maximumTotalStageBytes: 4096,
                   maximumStageFiles: 2))
        {
            fixture.WriteOrphan("one.bin", 1);
            fixture.WriteOrphan("two.bin", 1);
            var source = fixture.CreateFrame(width: 3, height: 2, stride: 3);
            var frame = RetainedProductionFrame.Capture(source.Lease);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                    TimeSpan.FromSeconds(5)));
            Assert.Equal("ProductionStageFileCapacityExceeded", failure.Message);
            await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(frame.IsLoanActive);
            Assert.Equal(2, Directory.GetFiles(fixture.Root).Length);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_F12")]
    public async Task V150_F12_RootRefusalAndRootBindingHashRevalidation()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Production image staging requires local NTFS.");
        // Establishes that TEMP itself is a qualified local root before the refusal probes.
        using var fixture = StageFixture.Create();
        Assert.Throws<ArgumentException>(() =>
            new ProductionImageStageOptions("relative-stage", 1024, 4096, 8));
        Assert.Throws<ArgumentException>(() =>
            new ProductionImageStageOptions(@"\\?\F:\stage", 1024, 4096, 8));
        var missing = Path.Combine(TempRoot(), "T50-Missing-" + Guid.NewGuid().ToString("N"));
        var notFound = Assert.Throws<ArgumentException>(() =>
            new ProductionImageStageOptions(missing, 1024, 4096, 8));
        Assert.Equal("ProductionImageStageRootMissing", notFound.Message);

        var cloudParent = Path.Combine(TempRoot(), "OneDrive");
        var cloudRoot = Path.Combine(cloudParent, "T50-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cloudRoot);
        try
        {
            var cloud = Assert.Throws<InvalidOperationException>(() =>
                new ProductionImageStageOptions(cloudRoot, 1024, 4096, 8));
            Assert.Equal("ProductionImageStageRootRejected:StorePathCloudSynchronized", cloud.Message);
        }
        finally
        {
            if (Directory.Exists(cloudRoot)) Directory.Delete(cloudRoot, recursive: true);
            if (Directory.Exists(cloudParent) && !Directory.EnumerateFileSystemEntries(cloudParent).Any())
                Directory.Delete(cloudParent);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductionImageStageOptions(fixture.Root, 0, 4096, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductionImageStageOptions(fixture.Root, 1024, 1023, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductionImageStageOptions(fixture.Root, 1024, 4096, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductionImageStageOptions(fixture.Root,
                ProductionImageStageOptions.MaximumAllowedStageBytes + 1,
                ProductionImageStageOptions.MaximumAllowedTotalStageBytes, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductionImageStageOptions(fixture.Root, 1024,
                ProductionImageStageOptions.MaximumAllowedTotalStageBytes + 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProductionImageStageOptions(fixture.Root, 1024, 4096,
                ProductionImageStageOptions.MaximumAllowedStageFiles + 1));

        var binding = fixture.Options.ContentHash;
        Assert.Equal(64, binding.Length);
        Assert.Equal(binding.ToUpperInvariant(), binding);
        var repeated = new ProductionImageStageOptions(fixture.Root, fixture.Options.MaximumStageBytes,
            fixture.Options.MaximumTotalStageBytes, fixture.Options.MaximumStageFiles);
        Assert.Equal(binding, repeated.ContentHash);
        var changed = new ProductionImageStageOptions(fixture.Root, fixture.Options.MaximumStageBytes,
            fixture.Options.MaximumTotalStageBytes, fixture.Options.MaximumStageFiles - 1);
        Assert.NotEqual(binding, changed.ContentHash);
        var sibling = Path.Combine(fixture.Root, "sibling");
        Directory.CreateDirectory(sibling);
        var siblingOptions = new ProductionImageStageOptions(sibling + Path.DirectorySeparatorChar,
            fixture.Options.MaximumStageBytes, fixture.Options.MaximumTotalStageBytes,
            fixture.Options.MaximumStageFiles);
        Assert.Equal(sibling, siblingOptions.StageRoot);
        Assert.NotEqual(binding, siblingOptions.ContentHash);

        // The root is revalidated before every write, not trusted from construction time.
        var source = fixture.CreateFrame(width: 2, height: 1, stride: 2);
        var frame = RetainedProductionFrame.Capture(source.Lease);
        Directory.Delete(fixture.Root, recursive: true);
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Stager.StageAsync(Guid.NewGuid(), AdmissionHash, PolicyHash, frame,
                TimeSpan.FromSeconds(5)));
        Assert.Equal("ProductionImageStageRootRejected:StorePathDirectoryMissing", rejection.Message);
        await fixture.Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(frame.IsLoanActive);
        Assert.Equal(0, fixture.Pool.GetSnapshot().OutstandingLeases);
    }

    private static string TempRoot()
    {
        var temp = Environment.GetEnvironmentVariable("TEMP");
        return string.IsNullOrWhiteSpace(temp) ? Path.GetTempPath() : temp;
    }

    private static void TryDelete(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var allowed = Path.GetFullPath(TempRoot());
            if (full.Length <= allowed.Length + 1 ||
                !full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static FrameMetadata Metadata(ExecutionKind kind, int width, int height, int stride,
        VisionPixelFormat pixelFormat = VisionPixelFormat.Mono8, int? validBits = null)
    {
        var correlation = new ExecutionCorrelationId(kind, Guid.NewGuid());
        var configuration = new EffectiveCameraConfiguration(
            ProductionAcquisitionMode.HardwareTrigger, 500, 1.5,
            new RegionOfInterest(0, 0, width, height), pixelFormat, validBits, 500, 0, null);
        return new FrameMetadata(correlation, "TopCamera", width, height, stride, pixelFormat,
            validBits, Utc(10), configuration);
    }

    private static FrameProvenance Provenance(FrameMetadata metadata) =>
        new(metadata.Correlation, "vendor-a", "1", "adapter-a", "1", "sdk-a", "1", null,
            "device-1", null, null, metadata.PixelFormat.ToString(), "normalized-v1", false,
            false, null, null,
            new FrameAcquisitionMilestones(1_000_000, new FrameTimePoint(Utc(1), 10),
                new FrameTimePoint(Utc(2), 12), new FrameTimePoint(Utc(3), 14),
                new FrameTimePoint(Utc(4), 16)));

    private static byte[] DefaultSource(int width, int height, int stride,
        VisionPixelFormat pixelFormat)
    {
        var bytesPerPixel = pixelFormat switch
        {
            VisionPixelFormat.Mono8 => 1,
            VisionPixelFormat.Mono16 => 2,
            VisionPixelFormat.Bgr24 => 3,
            _ => 1
        };
        var bytes = new byte[checked(stride * height)];
        bytes.AsSpan().Fill(0xEE);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var value = checked((ushort)(1 + (row * width + column) % 1000));
                var offset = row * stride + column * bytesPerPixel;
                if (pixelFormat == VisionPixelFormat.Mono16)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
                else
                    bytes[offset] = (byte)value;
            }
        }
        return bytes;
    }

    private static byte[] ValidRows(VisionFrame frame)
    {
        var rows = new byte[checked(frame.Metadata.ValidRowBytes * frame.Height)];
        for (var row = 0; row < frame.Height; row++)
            frame.GetRowSpan(row).CopyTo(rows.AsSpan(row * frame.Metadata.ValidRowBytes));
        return rows;
    }

    private static byte[] UInt16Bytes(params ushort[] values)
    {
        var bytes = new byte[checked(values.Length * 2)];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2, 2), values[index]);
        return bytes;
    }

    private static DateTimeOffset Utc(int second) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(second);

    private sealed record FrameSource(FrameBufferLease Lease, FrameMetadata Metadata,
        FrameProvenance Provenance, byte[] Envelope, byte[] ValidRows, string CanonicalHash);

    private sealed class PublicSurfaceFrame : VisionFrame
    {
        private readonly byte[] _pixels;

        internal PublicSurfaceFrame(FrameMetadata metadata, byte[] pixels) : base(metadata) =>
            _pixels = pixels;

        public override bool IsLoanActive => true;

        public override ReadOnlySpan<byte> GetRowSpan(int row) =>
            _pixels.AsSpan(row * Metadata.ValidRowBytes, Metadata.ValidRowBytes);
    }

    private sealed class StageFixture : IDisposable
    {
        private StageFixture(string root, ProductionImageStageOptions options,
            ProductionImageStager stager, FrameBufferPool pool)
        {
            Root = root;
            Options = options;
            Stager = stager;
            Pool = pool;
        }

        internal string Root { get; }
        internal ProductionImageStageOptions Options { get; }
        internal ProductionImageStager Stager { get; }
        internal FrameBufferPool Pool { get; }

        internal static StageFixture Create(long maximumStageBytes = 1 << 20,
            long maximumTotalStageBytes = 1 << 22, int maximumStageFiles = 64,
            Action<string, StageBoundary>? faultHook = null)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Production image staging requires local NTFS.");
            var root = Path.Combine(TempRoot(), "SharpInspect.Runtime.Tests",
                "T50-ProductionImageStage-" + Guid.NewGuid().ToString("N"));
            ProductionImageStageOptions options;
            try
            {
                Directory.CreateDirectory(root);
                options = new ProductionImageStageOptions(root, maximumStageBytes,
                    maximumTotalStageBytes, maximumStageFiles);
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                                               ArgumentException or IOException or
                                               UnauthorizedAccessException)
            {
                TryDelete(root);
                throw SkipException.ForSkip(
                    "TEMP is not a qualified local stage root: " + exception.Message);
            }
            var stager = new ProductionImageStager(options, faultHook is null
                ? null
                : boundary => faultHook(root, boundary));
            var pool = new FrameBufferPool(new FrameBufferPoolOptions(4, 1024,
                TimeSpan.FromSeconds(1)));
            return new StageFixture(root, options, stager, pool);
        }

        internal FrameSource CreateFrame(int width, int height, int stride,
            VisionPixelFormat format = VisionPixelFormat.Mono8, int? validBits = null,
            byte[]? source = null)
        {
            var metadata = Metadata(ExecutionKind.Manual, width, height, stride, format, validBits);
            var copied = Pool.TryCopyFrame(metadata, Provenance(metadata),
                source ?? DefaultSource(width, height, stride, format));
            if (!copied.Succeeded || copied.Lease is not FrameBufferLease lease)
                throw new XunitException("FrameBufferPool did not produce a test lease: " +
                    copied.ReasonCode);
            var frame = lease.Frame;
            return new FrameSource(lease, frame.Metadata, lease.Provenance,
                CanonicalImagePixelContent.CreateEnvelope(frame.Width, frame.Height,
                    frame.PixelFormat, frame.ValidBits),
                ValidRows(frame), CanonicalImagePixelContent.ComputeHash(frame));
        }

        internal string WriteOrphan(string name, int length)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        public void Dispose()
        {
            try
            {
                _ = Stager.PhysicalCompletion.WaitAsync(TimeSpan.FromSeconds(10))
                    .GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
            Pool.Dispose();
            TryDelete(Root);
        }
    }
}
