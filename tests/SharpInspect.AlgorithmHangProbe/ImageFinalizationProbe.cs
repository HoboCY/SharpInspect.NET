using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.AlgorithmHangProbe;

/// <summary>Test-only process host: the parent kills it at a reported physical file boundary.</summary>
internal static class ImageFinalizationProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5) return 2;
        var workPath = args[0];
        if (new FileInfo(workPath).Length > 1024 * 1024) return 2;
        var work = ProductionInspectionStorageCodec.DecodeImageWork(await File.ReadAllBytesAsync(workPath));
        var stage = new ProductionImageStageOptions(args[1], 1024 * 1024, 16 * 1024 * 1024, 100);
        var final = new ProductionImageFinalizationRootOptions(args[2], 2 * 1024 * 1024, 32 * 1024 * 1024, 100);
        var attempt = Guid.Parse(args[3]);
        var boundary = Enum.Parse<ImageFinalizationBoundary>(args[4]);
        var worker = new ProductionImageFinalizer(stage, final.FinalRoot, final.ContentHash,
            new(new(1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024), 32 * 1024 * 1024, 100), current =>
            {
                if (current != boundary) return;
                Console.WriteLine("V151-PROCESS-BOUNDARY:" + current);
                Console.Out.Flush();
                using var block = new ManualResetEvent(false);
                block.WaitOne(TimeSpan.FromMinutes(1));
                throw new TimeoutException("ProbeParentDidNotTerminate");
            });
        using var claim = await worker.FinalizeAsync(work, attempt,
            work.Manifest.ManifestId.ToString("N") + "." + attempt.ToString("N") + ".tmp",
            work.Manifest.ManifestId.ToString("N") + ".png", false, TimeSpan.FromSeconds(30));
        return 3; // The selected boundary must be reached and the parent must terminate us.
    }
}
