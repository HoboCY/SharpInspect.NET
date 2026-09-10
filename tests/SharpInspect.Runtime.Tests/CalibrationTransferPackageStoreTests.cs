using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationTransferPackageStoreTests
{
    [Fact]
    public async Task V134_P01_PreservesAndReadsExactOpaqueBytesByCanonicalHash()
    {
        using var root = TemporaryRoot.Create();
        await using var store = CreateStore(root.Path);
        var bytes = new byte[] { 1, 9, 2, 8, 3, 7 };
        var package = new CalibrationExportPackage(bytes);

        var receipt = await store.PreserveAsync(package);

        Assert.Equal(package.ContentHash, receipt.Sha256);
        Assert.Equal(bytes.Length, receipt.Length);
        Assert.Equal(receipt.Sha256, receipt.ContentHash);
        Assert.Equal(receipt.Sha256 + ".bin", receipt.RelativePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root.Path, receipt.RelativePath)));
        Assert.Equal(new[] { receipt.RelativePath }, Directory.GetFiles(root.Path)
            .Select(path => Path.GetFileName(path)!));

        var read = await store.ReadAsync(receipt.Sha256.ToLowerInvariant(), receipt.Length);
        Assert.Equal(bytes, read.GetBytes());
        var exposed = read.GetBytes();
        exposed[0] ^= 0xFF;
        Assert.Equal(bytes, read.GetBytes());
    }

    [Fact]
    public async Task V134_P02_ReusesOnlyAnExistingFileThatStillMatchesHashAndLength()
    {
        using var root = TemporaryRoot.Create();
        await using var store = CreateStore(root.Path);
        var package = new CalibrationExportPackage(new byte[] { 4, 5, 6, 7 });

        var first = await store.PreserveAsync(package);
        var second = await store.PreserveAsync(package);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(root.Path, "*.bin"));

        var path = Path.Combine(root.Path, first.RelativePath);
        var tampered = await File.ReadAllBytesAsync(path);
        tampered[^1] ^= 0x20;
        await File.WriteAllBytesAsync(path, tampered);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PreserveAsync(package));
        Assert.Equal("CalibrationTransferArtifactContentHashMismatch", failure.Message);
    }

    [Fact]
    public async Task V134_P03_ReadRejectsTamperedLengthAndContent()
    {
        using var root = TemporaryRoot.Create();
        await using var store = CreateStore(root.Path);
        var package = new CalibrationExportPackage(new byte[] { 11, 12, 13 });
        var receipt = await store.PreserveAsync(package);
        var path = Path.Combine(root.Path, receipt.RelativePath);

        await File.WriteAllBytesAsync(path, new byte[] { 11, 12 });
        var lengthFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAsync(receipt.Sha256, receipt.Length));
        Assert.Equal("CalibrationTransferArtifactLengthMismatch", lengthFailure.Message);

        await File.WriteAllBytesAsync(path, new byte[] { 11, 12, 99 });
        var hashFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAsync(receipt.Sha256, receipt.Length));
        Assert.Equal("CalibrationTransferArtifactContentHashMismatch", hashFailure.Message);
    }

    [Fact]
    public async Task V134_P04_HashIdentityRejectsMemberAndTraversalPaths()
    {
        using var root = TemporaryRoot.Create();
        await using var store = CreateStore(root.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("member/package.bin", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync("..\\package.bin", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(new string('A', 63) + ".", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAsync(new string('A', 64), 1));
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public async Task V134_P05_OrphanAndPartialFilesConsumeAggregateQuota()
    {
        using var root = TemporaryRoot.Create();
        await using var store = CreateStore(root.Path, maximumPackageBytes: 4, maximumTotalBytes: 6);
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "orphan.bin"), new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "unfinished.partial"), new byte[] { 4, 5 });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.PreserveAsync(new CalibrationExportPackage(new byte[] { 6, 7, 8, 9 })));

        Assert.Equal("CalibrationTransferArtifactTotalCapacityExceeded", failure.Message);
        Assert.False(File.Exists(Path.Combine(root.Path,
            Convert.ToHexString(SHA256.HashData(new byte[] { 6, 7, 8, 9 })) + ".bin")));
        Assert.True(File.Exists(Path.Combine(root.Path, "orphan.bin")));
        Assert.True(File.Exists(Path.Combine(root.Path, "unfinished.partial")));
    }

    [Fact]
    public async Task V134_P06_CrossInstanceSameHashRaceNeverOverwritesBlob()
    {
        using var root = TemporaryRoot.Create();
        await using var first = CreateStore(root.Path);
        await using var second = CreateStore(root.Path);
        var package = new CalibrationExportPackage(Enumerable.Range(0, 32)
            .Select(value => (byte)value).ToArray());

        var receipts = await Task.WhenAll(first.PreserveAsync(package), second.PreserveAsync(package));

        Assert.Equal(receipts[0], receipts[1]);
        Assert.Single(Directory.GetFiles(root.Path, "*.bin"));
        Assert.Equal(package.GetBytes(), await File.ReadAllBytesAsync(
            Path.Combine(root.Path, receipts[0].RelativePath)));
    }

    [Fact]
    public async Task V134_P07_PreCancelledOperationDoesNotCreateArtifactOrReturnCapacity()
    {
        using var root = TemporaryRoot.Create();
        await using var store = CreateStore(root.Path, maximumPackageBytes: 32,
            maximumTotalBytes: 64);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.PreserveAsync(
            new CalibrationExportPackage(new byte[] { 1, 2, 3 }), cancellation.Token));

        Assert.Empty(Directory.GetFiles(root.Path));
    }

    [Fact]
    public void V134_P08_OptionsRequireAnExplicitRootAndRespectHardLimits()
    {
        Assert.Throws<ArgumentException>(() =>
            new CalibrationTransferArtifactOptions().Validate());

        using var root = TemporaryRoot.Create();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateStore(root.Path, maximumPackageBytes: CalibrationExportPackage.MaximumBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateStore(root.Path, maximumTotalBytes: CalibrationTransferArtifactOptions.MaximumTotalBytesHardLimit + 1));
    }

    private static CalibrationTransferPackageStore CreateStore(string root,
        int? maximumPackageBytes = null, long? maximumTotalBytes = null) =>
        new(new CalibrationTransferArtifactOptions(root)
        {
            MaximumPackageBytes = maximumPackageBytes ?? CalibrationTransferArtifactOptions.DefaultMaximumPackageBytes,
            MaximumTotalBytes = maximumTotalBytes ?? CalibrationTransferArtifactOptions.DefaultMaximumTotalBytes,
            OperationTimeout = TimeSpan.FromSeconds(10)
        });

    private sealed class TemporaryRoot : IDisposable
    {
        private TemporaryRoot(string path) => Path = path;

        internal string Path { get; }

        internal static TemporaryRoot Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "SharpInspect-CalibrationTransfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryRoot(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
