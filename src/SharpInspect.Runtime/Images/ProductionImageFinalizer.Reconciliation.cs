using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Images;

internal sealed partial class ProductionImageFinalizer
{
    internal Task<ReconciledImageClaim> ReconcileAsync(PendingImageFinalizationWork work,
        ProductionImageSuccessDescriptor? success, bool hasBoundAttempt, TimeSpan timeout,
        CancellationToken token) => RunMaintenanceAsync(deadline =>
    {
        RequireWork(work);
        var roots = EvidenceQuarantine.ProtectRoots(_stage.StageRoot, _finalRoot);
        var files = new List<FileStream>();
        try
        {
            var stagePath = Path.Combine(_stage.StageRoot, work.Manifest.StageFileName);
            var hasStage = File.Exists(stagePath);
            long bytes = 0;
            if (hasStage)
            {
                var stage = OpenProtected(stagePath); files.Add(stage);
                EvidenceQuarantine.RequireUniqueFile(stage);
                CanonicalPngCodec.VerifyStage(stage, Descriptor(work.Manifest), _limits.Codec, deadline, token);
                bytes = checked(bytes + stage.Length);
            }
            var finalName = work.Manifest.ManifestId.ToString("N") + ".png";
            var finalPath = Path.Combine(_finalRoot, finalName);
            var hasFinal = File.Exists(finalPath);
            long observedLength;
            if (hasFinal)
            {
                if (!hasBoundAttempt) throw new InvalidOperationException("ProductionImageUnownedFinalFile");
                var final = OpenProtected(finalPath); files.Add(final);
                EvidenceQuarantine.RequireUniqueFile(final);
                var verified = CanonicalPngCodec.Verify(final, Descriptor(work.Manifest), _limits.Codec, deadline, token);
                if (success is not null) RequireSuccess(work, success, verified);
                bytes = checked(bytes + final.Length);
                observedLength = final.Length;
            }
            else
            {
                if (success is not null) throw new InvalidOperationException("ProductionImageReferencedFinalMissing");
                if (!hasStage) throw new InvalidOperationException("ProductionImageReferencedPixelsMissing");
                observedLength = files[0].Length;
            }
            // A bound valid final can recover missing stage bytes. Present contradictory
            // stage bytes always fail the verification above, including with a valid final.
            var claim = new ReconciledImageClaim(files, roots, hasFinal, bytes, observedLength,
                hasFinal ? finalName : work.Manifest.StageFileName, work.Manifest.CanonicalPixelHash,
                hasFinal ? _finalRootBindingHash : _stage.ContentHash);
            files = null!; roots = null!;
            return claim;
        }
        finally
        {
            try { if (files is not null) foreach (var file in files) file.Dispose(); }
            finally { if (roots is not null) foreach (var root in roots) root.Dispose(); }
        }
    }, timeout, token);

    internal sealed class ReconciledImageClaim : IDisposable
    {
        private readonly object _gate = new();
        private List<FileStream>? _files;
        private List<SafeFileHandle>? _roots;
        internal ReconciledImageClaim(List<FileStream> files, List<SafeFileHandle> roots,
            bool hasFinal, long readBytes, long observedLength, string name, string hash, string rootHash)
        {
            _files = files; _roots = roots; HasFinal = hasFinal; ReadBytes = readBytes;
            ObservedLength = observedLength; FileName = name; CanonicalPixelHash = hash; RootBindingHash = rootHash;
        }
        internal bool HasFinal { get; }
        internal long ReadBytes { get; }
        internal long ObservedLength { get; }
        internal string FileName { get; }
        internal string CanonicalPixelHash { get; }
        internal string RootBindingHash { get; }
        internal void VerifyCommitProtection()
        {
            lock (_gate)
                if (_files is null || _roots is null || _files.Any(x => x.SafeFileHandle.IsClosed) ||
                    _roots.Any(x => x.IsClosed || x.IsInvalid))
                    throw new InvalidOperationException("EvidenceReconciliationFileProtectionLost");
        }
        public void Dispose()
        {
            List<FileStream>? files; List<SafeFileHandle>? roots;
            lock (_gate) { files = _files; roots = _roots; _files = null; _roots = null; }
            try { if (files is not null) foreach (var file in files) file.Dispose(); }
            finally { if (roots is not null) foreach (var root in roots) root.Dispose(); }
        }
    }
}
