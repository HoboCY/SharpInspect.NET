using System.Diagnostics;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionImageFinalizerProcessTests
{
    [Theory]
    [Trait("VerificationId", "V151_P01")]
    [InlineData("BeforeFlush", false)]
    [InlineData("AfterFlush", false)]
    [InlineData("AfterRename", true)]
    [InlineData("BeforeClaim", true)]
    public async Task V151_P01_ActualProcessExitRetainsStageAndReplayProducesOneAuthoritativePng(
        string boundary, bool finalWasRenamed)
    {
        var root = Path.Combine(Path.GetTempPath(), "SharpInspect-T51-process", Guid.NewGuid().ToString("N"));
        var stageRoot = Directory.CreateDirectory(Path.Combine(root, "stage")).FullName;
        var finalRoot = Directory.CreateDirectory(Path.Combine(root, "final")).FullName;
        var stage = new ProductionImageStageOptions(stageRoot, 1024 * 1024, 16 * 1024 * 1024, 100);
        var final = new ProductionImageFinalizationRootOptions(finalRoot, 2 * 1024 * 1024, 32 * 1024 * 1024, 100);
        var content = CanonicalImagePixelContent.CreateEnvelope(3, 2, VisionPixelFormat.Mono16, 12)
            .Concat(new byte[] { 1, 0, 255, 15, 128, 1, 0, 0, 0, 8, 20, 0 }).ToArray();
        var hash = new string('A', 64);
        var stageId = Guid.NewGuid();
        var manifest = new PendingImageManifest(Guid.NewGuid(), Guid.NewGuid(), hash, hash, stageId, Guid.NewGuid(),
            stage.ContentHash, stageId.ToString("N") + ".stage", 3, 2, VisionPixelFormat.Mono16, 12,
            Convert.ToHexString(SHA256.HashData(content)), content.Length, hash, hash, hash, hash, DateTimeOffset.UtcNow);
        var work = new PendingImageFinalizationWork(Guid.NewGuid(), manifest);
        var stagePath = Path.Combine(stageRoot, manifest.StageFileName);
        var workPath = Path.Combine(root, "work.bin");
        File.WriteAllBytes(stagePath, content);
        File.WriteAllBytes(workPath, ProductionInspectionStorageCodec.EncodeImageWork(work));
        var attempt = Guid.NewGuid();
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(ProbePath());
        foreach (var argument in new[] { "image-finalize", workPath, stageRoot, finalRoot, attempt.ToString("D"), boundary })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("V151-PROCESS-BOUNDARY:" + boundary, line);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(0, process.ExitCode);
            Assert.Equal(content, File.ReadAllBytes(stagePath));
            var finalName = manifest.ManifestId.ToString("N") + ".png";
            var temporaryName = manifest.ManifestId.ToString("N") + "." + attempt.ToString("N") + ".tmp";
            Assert.Equal(finalWasRenamed, File.Exists(Path.Combine(finalRoot, finalName)));
            var worker = new ProductionImageFinalizer(stage, finalRoot, final.ContentHash,
                new(new(1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024), 32 * 1024 * 1024, 100));
            if (!finalWasRenamed)
                Assert.True(await worker.DiscardKnownTemporaryAsync(work, attempt, TimeSpan.FromSeconds(5), default));
            using (var proof = await worker.FinalizeAsync(work, attempt, temporaryName, finalName,
                finalWasRenamed, TimeSpan.FromSeconds(5)))
                Assert.Equal(manifest.CanonicalPixelHash, proof.CanonicalPixelHash);
            using (var repeated = await worker.FinalizeAsync(work, attempt, temporaryName, finalName,
                true, TimeSpan.FromSeconds(5)))
                Assert.Equal(manifest.CanonicalPixelHash, repeated.CanonicalPixelHash);
            Assert.Single(Directory.GetFileSystemEntries(finalRoot));
            Assert.Equal(content, File.ReadAllBytes(stagePath)); // No SQL Succeeded authority in this file-only probe.
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            _ = await stderr;
            // This freshly allocated fixture root owns all of these paths.
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ProbePath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AlgorithmHangProbe", "SharpInspect.AlgorithmHangProbe.dll");
        Assert.True(File.Exists(path), path);
        return path;
    }
}
