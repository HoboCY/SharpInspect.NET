using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationFrameEvidenceStoreTests
{
    [Fact]
    public async Task V124_E01_PreservesTightRowsAndOriginalStrideProvenanceAndSourceHash()
    {
        using var fixture = EvidenceFixture.Create();
        var header = TestContract.Header();
        var expectedPixels = new byte[] { 1, 2, 3, 4 };
        var expectedPixelHash = Convert.ToHexString(SHA256.HashData(expectedPixels));
        CalibrationFrameEvidence evidence;

        using (var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4))
        {
            evidence = await fixture.Store.PreserveAsync(header, source.FrameId,
                source.Lease, CancellationToken.None);

            Assert.Equal(expectedPixelHash, evidence.PixelHash);
            Assert.Equal(4, evidence.ByteLength);
            Assert.Equal(4, evidence.SourceStrideBytes);
            Assert.Equal(2, evidence.Metadata.StrideBytes);
            Assert.Equal(2, evidence.Metadata.ValidRowBytes);
            Assert.Same(source.Provenance, evidence.Provenance);
            Assert.Equal(source.FrameId, evidence.FrameId);
            Assert.Equal(header.Binding.LogicalRole, evidence.Metadata.LogicalCameraRole);
            Assert.Equal(new ExecutionCorrelationId(ExecutionKind.Calibration, source.FrameId),
                evidence.Metadata.Correlation);
            Assert.Equal(64, evidence.SourceHash.Length);
        }

        var image = await fixture.Store.ReadAsync(evidence, CancellationToken.None);
        Assert.Equal(expectedPixels, image.GetBytes());
        Assert.Equal(evidence.SourceHash, image.Frame.SourceHash);
    }

    [Fact]
    public async Task V124_E02_HashTamperingIsRejectedByContentAddressedRead()
    {
        using var fixture = EvidenceFixture.Create();
        var header = TestContract.Header();
        CalibrationFrameEvidence evidence;
        using (var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4))
            evidence = await fixture.Store.PreserveAsync(header, source.FrameId,
                source.Lease, CancellationToken.None);

        var path = Path.Combine(fixture.Root, evidence.RelativePath);
        var tampered = await File.ReadAllBytesAsync(path);
        tampered[0] ^= 0xFF;
        await File.WriteAllBytesAsync(path, tampered);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.ReadAsync(evidence, CancellationToken.None));
        Assert.Equal("CalibrationFrameContentHashMismatch", failure.Message);
    }

    [Fact]
    public async Task V124_E03_LengthTamperingIsRejectedBeforeImagePublication()
    {
        using var fixture = EvidenceFixture.Create();
        var header = TestContract.Header();
        CalibrationFrameEvidence evidence;
        using (var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4))
            evidence = await fixture.Store.PreserveAsync(header, source.FrameId,
                source.Lease, CancellationToken.None);

        var path = Path.Combine(fixture.Root, evidence.RelativePath);
        var tampered = (await File.ReadAllBytesAsync(path)).Concat(new byte[] { 5 }).ToArray();
        await File.WriteAllBytesAsync(path, tampered);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ReadAsync(evidence, CancellationToken.None));
        Assert.Equal("CalibrationFrameLengthMismatch", failure.Message);
    }

    [Fact]
    public async Task V124_E04_OrphanAndPartialFilesConsumeTheTotalFrameQuota()
    {
        using var fixture = EvidenceFixture.Create(maximumFrameBytes: 4,
            maximumTotalFrameBytes: 8);
        var header = TestContract.Header();
        var orphanPath = Path.Combine(fixture.Root, "orphan.bin");
        var partialPath = Path.Combine(fixture.Root, "unfinished.partial");
        await File.WriteAllBytesAsync(orphanPath, new byte[3]);
        await File.WriteAllBytesAsync(partialPath, new byte[2]);

        using var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.PreserveAsync(header, source.FrameId, source.Lease,
                CancellationToken.None));

        Assert.Equal("CalibrationTotalFrameCapacityExceeded", failure.Message);
        Assert.True(File.Exists(orphanPath));
        Assert.True(File.Exists(partialPath));
        var expectedPath = Path.Combine(fixture.Root,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })) + ".bin");
        Assert.False(File.Exists(expectedPath));
    }

    [Fact]
    public async Task V124_E05_DisposedBorrowedFrameRejectsRowsAfterVerifiedRead()
    {
        using var fixture = EvidenceFixture.Create();
        var header = TestContract.Header();
        CalibrationFrameEvidence evidence;
        using (var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4))
            evidence = await fixture.Store.PreserveAsync(header, source.FrameId,
                source.Lease, CancellationToken.None);

        var image = await fixture.Store.ReadAsync(evidence, CancellationToken.None);
        var borrowed = new CalibrationBorrowedFrame(image);
        Assert.True(borrowed.IsLoanActive);
        Assert.Equal(new byte[] { 1, 2 }, ReadRow(borrowed));

        borrowed.Dispose();
        borrowed.Dispose();
        Assert.False(borrowed.IsLoanActive);
        Assert.Throws<ObjectDisposedException>(() => ReadRow(borrowed));
    }

    [Fact]
    public async Task V124_E06_RepeatedRealPoolLeasesRemainContentAddressedAndIndependentOfSourceLease()
    {
        using var fixture = EvidenceFixture.Create();
        var header = TestContract.Header();
        CalibrationFrameEvidence first;
        using (var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4))
            first = await fixture.Store.PreserveAsync(header, source.FrameId,
                source.Lease, CancellationToken.None);

        CalibrationFrameEvidence second;
        using (var source = FrameLeaseFixture.Create(header, sourceStrideBytes: 4,
                   frameId: first.FrameId))
            second = await fixture.Store.PreserveAsync(header, source.FrameId,
                source.Lease, CancellationToken.None);

        Assert.Equal(first.PixelHash, second.PixelHash);
        Assert.Equal(first.SourceHash, second.SourceHash);
        Assert.Equal(first.RelativePath, second.RelativePath);
        var image = await fixture.Store.ReadAsync(second, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.GetBytes());
    }

    private static byte[] ReadRow(VisionFrame frame) => frame.GetRowSpan(0).ToArray();

    private sealed class EvidenceFixture : IDisposable
    {
        private EvidenceFixture(string root, CalibrationFrameEvidenceStore store)
        {
            Root = root;
            Store = store;
        }

        internal string Root { get; }
        internal CalibrationFrameEvidenceStore Store { get; }

        internal static EvidenceFixture Create(long maximumFrameBytes = 64,
            long maximumTotalFrameBytes = 256)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Calibration evidence requires Windows NTFS.");

            DriveInfo drive;
            try
            {
                drive = new DriveInfo(@"E:\");
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed ||
                    !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw SkipException.ForSkip("E:\\ is not a ready fixed NTFS volume.");
            }
            catch (SkipException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               ArgumentException)
            {
                throw SkipException.ForSkip("E:\\ volume is unavailable for isolated evidence.");
            }

            var root = Path.Combine(@"E:\temp", "SharpInspect.Runtime.Tests",
                "V124-CalibrationEvidence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var options = new CalibrationSessionStoreOptions
                {
                    EvidenceRoot = root,
                    MaximumSessions = 4,
                    MaximumEvents = 64,
                    MaximumEventPayloadBytes = 4 * 1024,
                    MaximumFramesPerSession = 4,
                    MaximumFrameBytes = maximumFrameBytes,
                    MaximumTotalFrameBytes = maximumTotalFrameBytes
                };
                return new EvidenceFixture(root,
                    new CalibrationFrameEvidenceStore(options, TimeSpan.FromSeconds(2)));
            }
            catch
            {
                TryDelete(root);
                throw;
            }
        }

        public void Dispose() => TryDelete(Root);

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FrameLeaseFixture : IDisposable
    {
        private FrameLeaseFixture(FrameBufferPool pool, FrameBufferLease lease, Guid frameId,
            FrameProvenance provenance)
        {
            Pool = pool;
            Lease = lease;
            FrameId = frameId;
            Provenance = provenance;
        }

        private FrameBufferPool Pool { get; }
        internal FrameBufferLease Lease { get; }
        internal Guid FrameId { get; }
        internal FrameProvenance Provenance { get; }

        internal static FrameLeaseFixture Create(CalibrationSessionHeader header,
            int sourceStrideBytes, Guid? frameId = null)
        {
            const int width = 2;
            const int height = 2;
            var id = frameId ?? Guid.NewGuid();
            var correlation = new ExecutionCorrelationId(ExecutionKind.Calibration, id);
            var effective = TestContract.Effective;
            var metadata = new FrameMetadata(correlation, header.Binding.LogicalRole, width, height,
                sourceStrideBytes, VisionPixelFormat.Mono8, null, DateTimeOffset.UnixEpoch,
                effective);
            var provenance = new FrameProvenance(correlation, header.Binding.Target.Provider.Id,
                header.Binding.Target.Provider.Version,
                header.Binding.Target.Provider.AdapterPackageId,
                header.Binding.Target.Provider.AdapterVersion, "virtual-sdk", "1",
                "virtual-runtime", header.Binding.Target.StableDeviceIdentity,
                "Virtual Camera", null, "Mono8", "CanonicalRows", false, false, null, 1,
                new FrameAcquisitionMilestones(1_000,
                    new FrameTimePoint(DateTimeOffset.UnixEpoch, 0),
                    new FrameTimePoint(DateTimeOffset.UnixEpoch, 1),
                    new FrameTimePoint(DateTimeOffset.UnixEpoch, 2),
                    new FrameTimePoint(DateTimeOffset.UnixEpoch, 3)));
            var pool = new FrameBufferPool(new FrameBufferPoolOptions(1, 64,
                TimeSpan.FromSeconds(1)));
            var source = sourceStrideBytes == 4
                ? new byte[] { 1, 2, 0xEE, 0xEE, 3, 4 }
                : new byte[] { 1, 2, 3, 4 };
            var copied = pool.TryCopyFrame(metadata, provenance, source);
            var lease = copied.Lease;
            if (!copied.Succeeded || lease is null)
            {
                pool.Dispose();
                throw new XunitException("FrameBufferPool did not produce a test lease: " +
                    copied.ReasonCode);
            }

            return new FrameLeaseFixture(pool, lease, id, lease.Provenance);
        }

        public void Dispose()
        {
            Lease.Dispose();
            Pool.Dispose();
        }
    }

    private static class TestContract
    {
        private const string Hash =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        internal const string Role = "TopCamera";
        internal static readonly CameraProviderIdentity Provider = new(
            "SharpInspect.Virtual", "1", "SharpInspect.NET.Cameras.Virtual", "0.1.0-dev.1");
        internal static readonly CameraBindingTarget Target = new(Provider, "Virtual:One");
        internal static readonly EffectiveCameraConfiguration Effective = new(
            ProductionAcquisitionMode.SoftwareTrigger, 10, 0, new RegionOfInterest(0, 0, 2, 2),
            VisionPixelFormat.Mono8, null, 100, 0, null);

        private static RequestedCameraConfiguration Requested => new(
            Effective.ProductionAcquisitionMode, Effective.ExposureTimeUs, Effective.GainDb,
            Effective.RegionOfInterest, Effective.PixelFormat, Effective.ValidBits,
            Effective.AcquisitionTimeoutMs, Effective.TriggerDelayUs, Effective.WhiteBalanceRgb);

        internal static CalibrationSessionHeader Header()
        {
            var actor = Guid.NewGuid();
            var interactiveSession = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var inputContract = new RecipeContractReference("fixture-input", "1", Hash);
            var requirement = new CalibrationRequirement(Role, CalibrationKind.Intrinsic,
                "geometry", new RecipeContractReference("fixture-coefficients", "1", Hash),
                new RecipeContractReference("fixture-acceptance", "1", Hash));
            var procedure = new CalibrationProcedureDescriptor(
                new RecipeContractReference("fixture-procedure", "1", Hash), inputContract,
                CalibrationKind.Intrinsic);
            var input = new CalibrationProcedureInputPayload(inputContract, new byte[] { 1 });
            var plan = new CalibrationSessionPlan(requirement, procedure, input, Requested,
                new CalibrationEvidenceSelectionPolicy("fixture-selection", "1", 1, 1, 0));
            var binding = new CameraBindingRevision(1, Role, 1, Guid.NewGuid(), null, Hash,
                Target, actor, interactiveSession, 0, "Fixture binding", DateTimeOffset.UnixEpoch);
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                actor.ToString("D"), interactiveSession, Guid.NewGuid());
            var command = new StartCalibrationSessionCommand(Guid.NewGuid(), invocation, plan,
                binding.Revision, binding.RevisionHash,
                new ImagingSetupRevisionReference(Role, Guid.NewGuid(), 1, Hash),
                "Preserve calibration evidence");
            return new CalibrationSessionHeader(sessionId, Guid.NewGuid(), Guid.NewGuid(), actor,
                interactiveSession, 0, DateTimeOffset.UnixEpoch, command, binding, Requested,
                Effective, Hash);
        }
    }
}
