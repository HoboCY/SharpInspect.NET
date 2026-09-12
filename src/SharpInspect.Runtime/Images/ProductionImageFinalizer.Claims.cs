using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Images;

internal sealed partial class ProductionImageFinalizer
{
    /// <summary>Only this finalizer can issue the single-use protected-file commit proof.</summary>
    internal sealed class VerifiedPngCommitClaim : IDisposable
    {
        private readonly object _sync = new();
        private FileStream? _protection;
        private bool _consumed;

        internal static VerifiedPngCommitClaim Create(ProductionImageFinalizer owner, object issuer,
            PendingImageFinalizationWork work, Guid attemptId, string finalFileName,
            VerifiedCanonicalPng verified, FileStream protection)
        {
            if (!ReferenceEquals(owner._claimIssuer, issuer))
                throw new InvalidOperationException("ProductionImagePngClaimIssuerInvalid");
            return new VerifiedPngCommitClaim(owner, work, attemptId, finalFileName, verified, protection);
        }
        private VerifiedPngCommitClaim(ProductionImageFinalizer owner, PendingImageFinalizationWork work,
            Guid attemptId, string finalFileName, VerifiedCanonicalPng verified, FileStream protection)
        {
            WorkId = work.WorkId;
            ManifestId = work.Manifest.ManifestId;
            InspectionId = work.Manifest.InspectionId;
            WorkContentHash = work.ContentHash;
            ManifestContentHash = work.Manifest.ContentHash;
            AttemptId = attemptId;
            FinalRootBindingHash = owner._finalRootBindingHash;
            FinalFileName = finalFileName;
            EncodedByteLength = verified.EncodedByteLength;
            CanonicalPixelHash = verified.CanonicalPixelHash;
            _protection = protection;
        }
        internal Guid WorkId { get; }
        internal Guid ManifestId { get; }
        internal Guid InspectionId { get; }
        internal Guid AttemptId { get; }
        internal string WorkContentHash { get; }
        internal string ManifestContentHash { get; }
        internal string FinalRootBindingHash { get; }
        internal string FinalFileName { get; }
        internal long EncodedByteLength { get; }
        internal string CanonicalPixelHash { get; }
        internal bool IsConsumed { get { lock (_sync) return _consumed; } }

        // Does not read filesystem metadata or perform I/O on the single SQLite thread.
        internal void VerifyCommitProtection()
        {
            lock (_sync)
            {
                if (_protection is null || _protection.SafeFileHandle.IsClosed)
                    throw new InvalidOperationException("ProductionImagePngCommitProtectionLost");
            }
        }
        internal void ConsumeForCommit(PendingImageFinalizationWork work, Guid attemptId,
            string finalRootBindingHash, string finalFileName)
        {
            lock (_sync)
            {
                VerifyCommitProtection();
                if (_consumed) throw new InvalidOperationException("ProductionImagePngClaimConsumed");
                if (work.WorkId != WorkId || work.Manifest.ManifestId != ManifestId ||
                    work.Manifest.InspectionId != InspectionId || work.ContentHash != WorkContentHash ||
                    work.Manifest.ContentHash != ManifestContentHash || work.Manifest.CanonicalPixelHash != CanonicalPixelHash ||
                    attemptId != AttemptId || finalRootBindingHash != FinalRootBindingHash || finalFileName != FinalFileName)
                    throw new InvalidOperationException("ProductionImagePngClaimBindingMismatch");
                _consumed = true;
            }
        }
        public void Dispose()
        {
            FileStream? protection;
            lock (_sync) { protection = _protection; _protection = null; }
            protection?.Dispose();
        }
    }
}
