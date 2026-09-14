using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

internal sealed partial class ProductionImageFinalizer
{
    internal Task VerifyReconciliationMetadataAsync(ImageFinalizationReplayItem item,
        TimeSpan timeout, CancellationToken token) => RunMaintenanceAsync(deadline =>
    {
        RequireWork(item.Work);
        var names = item.Events.Where(x => x.Kind == ProductionImageFinalizationKind.AttemptStarted)
            .Select(x => x.Attempt!.TemporaryFileName).Append(item.Work.Manifest.ManifestId.ToString("N") + ".png");
        foreach (var path in names.Select(name => Path.Combine(_finalRoot, name))
            .Append(Path.Combine(_stage.StageRoot, item.Work.Manifest.StageFileName)))
        {
            SqliteNative.EnsureDeadline(deadline, token);
            if (!File.Exists(path)) continue;
            using var file = OpenProtected(path);
            EvidenceQuarantine.RequireUniqueFile(file);
        }
        if (item.State.Success is not null &&
            !File.Exists(Path.Combine(_finalRoot, item.Work.Manifest.ManifestId.ToString("N") + ".png")))
            throw new InvalidOperationException("ProductionImageReferencedFinalMissing");
        return true;
    }, timeout, token);

    internal Task<IReadOnlyList<string>> FindUnownedFilesAsync(string root, IReadOnlySet<string> known,
        int maximumFiles, long maximumBytes, TimeSpan timeout, CancellationToken token) =>
        RunMaintenanceAsync<IReadOnlyList<string>>(deadline =>
        {
            RequireRoots();
            var roots = EvidenceQuarantine.ProtectRoots(root);
            try
            {
                var unknown = new List<string>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long bytes = 0; var count = 0;
                foreach (var path in Directory.EnumerateFileSystemEntries(root))
                {
                    SqliteNative.EnsureDeadline(deadline, token);
                    if (++count > maximumFiles) throw new InvalidOperationException("EvidenceReconciliationInventoryCapacityExceeded");
                    var name = Path.GetFileName(path);
                    if (!names.Add(name)) throw new InvalidOperationException("EvidenceReconciliationDuplicateFileName");
                    if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                        bytes = checked(bytes + new FileInfo(path).Length);
                    if (bytes > maximumBytes) throw new InvalidOperationException("EvidenceReconciliationInventoryCapacityExceeded");
                    if (!known.Contains(name)) unknown.Add(name);
                }
                return unknown;
            }
            finally { foreach (var handle in roots) handle.Dispose(); }
        }, timeout, token);
}
