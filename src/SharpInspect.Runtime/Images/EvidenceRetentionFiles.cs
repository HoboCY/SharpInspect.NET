using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

internal enum EvidenceRetentionFileBoundary { BeforePrepare, BeforeDelete, AfterDeleteDisposition, AfterDelete }

/// <summary>Deletes only an already verified, still-open file. Retention eligibility remains owned by the ledger.</summary>
internal sealed class EvidenceRetentionFiles
{
    private static readonly SemaphoreSlim PhysicalOwner = new(1, 1);
    private readonly ProductionStoreOptions _options;
    private readonly Action<EvidenceRetentionFileBoundary>? _hook;
    private Task _physicalCompletion = Task.CompletedTask;
    internal Task PhysicalCompletion => Volatile.Read(ref _physicalCompletion);

    internal EvidenceRetentionFiles(ProductionStoreOptions options, Action<EvidenceRetentionFileBoundary>? hook = null)
    { _options = options; _hook = hook; }

    internal async Task<EvidenceDeletionClaim> PrepareAsync(EvidenceRetentionObligation obligation,
        PendingImageManifest? manifest, EvidenceDeletionFile? expectedIntent, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Deadline(timeout);
        if (!await PhysicalOwner.WaitAsync(Remaining(deadline), token).ConfigureAwait(false))
            throw new TimeoutException("RetentionPhysicalOwnerUnavailable");
        var task = Task.Run(() =>
        {
            try { return Prepare(obligation, manifest, expectedIntent, deadline, token); }
            catch { PhysicalOwner.Release(); throw; }
        }, CancellationToken.None);
        Volatile.Write(ref _physicalCompletion, task);
        try { return await task.WaitAsync(Remaining(deadline), token).ConfigureAwait(false); }
        catch
        {
            var retirement = task.ContinueWith(done =>
            {
                if (done.Status == TaskStatus.RanToCompletion) done.Result.Dispose();
                else _ = done.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Volatile.Write(ref _physicalCompletion, retirement);
            throw;
        }
    }

    internal async Task DeleteAsync(EvidenceDeletionClaim claim, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Deadline(timeout);
        claim.RequireOwner(this);
        var lease = claim.EnterPhysical();
        var task = Task.Run(() =>
        {
            try
            {
                _hook?.Invoke(EvidenceRetentionFileBoundary.BeforeDelete);
                SqliteNative.EnsureDeadline(deadline, token);
                EvidenceQuarantine.RequireUniqueFile(claim.File);
                if (EvidenceQuarantine.IdentityHash(claim.File) != claim.Descriptor.FileIdentityHash)
                    throw new InvalidOperationException("RetentionOpenedIdentityChanged");
                // POSIX disposition removes this exact name when our handle closes,
                // even if another read-sharing handle remains. Classic disposition
                // only marks delete-pending and cannot prove a tombstone yet.
                // Unsupported filesystems/OS versions fail closed; never fall back.
                var disposition = new FileDisposition { Flags = 0x3 }; // DELETE | POSIX_SEMANTICS
                if (!SetFileInformationByHandle(claim.File.SafeFileHandle, 21, ref disposition, 4))
                    throw new IOException("RetentionNativeDeleteFailed", new Win32Exception(Marshal.GetLastWin32Error()));
                claim.MarkDeleteStarted();
                try { _hook?.Invoke(EvidenceRetentionFileBoundary.AfterDeleteDisposition); }
                finally { claim.CloseDeletedFile(); }
                _hook?.Invoke(EvidenceRetentionFileBoundary.AfterDelete);
                // The confirmed outcome survives a cancellation/deadline that races the
                // syscall. The scheduler must still persist the exact completion fact.
            }
            finally { lease.Dispose(); }
        }, CancellationToken.None);
        Volatile.Write(ref _physicalCompletion, task);
        _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await task.WaitAsync(Remaining(deadline), token).ConfigureAwait(false);
    }

    private EvidenceDeletionClaim Prepare(EvidenceRetentionObligation obligation, PendingImageManifest? manifest,
        EvidenceDeletionFile? expected, StoreDeadline deadline, CancellationToken token)
    {
        EvidenceRetentionCodec.ValidateObligation(obligation);
        EvidenceQuarantine.RequireName(obligation.FileName);
        var (root, maximumBytes) = ResolveRoot(obligation);
        var roots = EvidenceQuarantine.ProtectRoots(root);
        FileStream? file = null;
        try
        {
            _hook?.Invoke(EvidenceRetentionFileBoundary.BeforePrepare);
            SqliteNative.EnsureDeadline(deadline, token);
            var path = Path.Combine(root, obligation.FileName);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException) when (expected is not null)
            {
                RequireExpected(obligation, expected);
                var missing = new EvidenceDeletionClaim(this, obligation, expected, null, roots, () => PhysicalOwner.Release());
                roots = null!;
                return missing;
            }
            file = EvidenceQuarantine.OpenFile(path);
            if (file.Length != obligation.ByteLength || file.Length > maximumBytes)
                throw new InvalidOperationException("RetentionFileLengthMismatch");
            var identity = EvidenceQuarantine.IdentityHash(file);
            if (obligation.Owner.Kind == EvidenceRetentionOwnerKind.ImageManifest)
            {
                if (manifest is null || manifest.ManifestId != obligation.Owner.OwnerId ||
                    manifest.ContentHash != obligation.SourceContentHash || manifest.CanonicalPixelHash != obligation.ArtifactContentHash ||
                    obligation.FileName != manifest.ManifestId.ToString("N") + ".png")
                    throw new InvalidOperationException("RetentionManifestMismatch");
                var stage = _options.ImageFinalization!.ImageEvidence.Stage;
                _ = CanonicalPngCodec.Verify(file, ProductionImageFinalizer.Descriptor(manifest),
                    new(stage.MaximumStageBytes, maximumBytes, 8 * 1024 * 1024), deadline, token);
                file.Position = 0;
            }
            else if (manifest is not null) throw new InvalidOperationException("RetentionUnexpectedManifest");
            var hash = EvidenceQuarantine.HashFile(file, obligation.ByteLength, deadline, token);
            if (obligation.Owner.Kind == EvidenceRetentionOwnerKind.QuarantinedFile && hash != obligation.ArtifactContentHash)
                throw new InvalidOperationException("RetentionQuarantineContentChanged");
            var descriptor = new EvidenceDeletionFile(obligation.RootBindingHash, obligation.FileName,
                identity, obligation.ByteLength, hash);
            if (expected is not null && descriptor != expected)
                throw new InvalidOperationException("RetentionIntentFileChanged");
            var claim = new EvidenceDeletionClaim(this, obligation, descriptor, file, roots, () => PhysicalOwner.Release());
            file = null; roots = null!;
            return claim;
        }
        finally { file?.Dispose(); if (roots is not null) foreach (var handle in roots) handle.Dispose(); }
    }

    private (string Root, long MaximumBytes) ResolveRoot(EvidenceRetentionObligation obligation)
    {
        if (_options.StorageRetention is null || _options.EvidenceReconciliation is not { } reconciliation)
            throw new InvalidOperationException("RetentionFileProfileMissing");
        if (obligation.Owner.Kind == EvidenceRetentionOwnerKind.ImageManifest && _options.ImageFinalization is { } images &&
            obligation.RootBindingHash == images.FinalRootBindingHash)
            return (images.FinalRoot.RequireValidatedRoot(), images.FinalRoot.MaximumFinalFileBytes);
        if (obligation.Owner.Kind == EvidenceRetentionOwnerKind.QuarantinedFile)
        {
            var quarantine = obligation.EvidenceClass switch
            {
                TraceRetentionClass.QuarantineEvidence => reconciliation.FinalQuarantine,
                TraceRetentionClass.OrphanImageStage => reconciliation.StageQuarantine,
                _ => null
            };
            if (quarantine is not null && obligation.RootBindingHash == quarantine.BindingHash &&
                StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(quarantine.Root, ".probe")), out _, out _))
                return (quarantine.Root, quarantine.MaximumFileBytes);
        }
        throw new InvalidOperationException("RetentionFileRootBindingMismatch");
    }

    private static void RequireExpected(EvidenceRetentionObligation obligation, EvidenceDeletionFile expected)
    {
        if (expected.RootBindingHash != obligation.RootBindingHash || expected.FileName != obligation.FileName ||
            expected.ByteLength != obligation.ByteLength)
            throw new InvalidOperationException("RetentionIntentFileBindingMismatch");
    }
    private static StoreDeadline Deadline(TimeSpan timeout) => timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)
        ? throw new ArgumentOutOfRangeException(nameof(timeout)) : new(timeout);

    private static TimeSpan Remaining(StoreDeadline deadline)
    {
        var value = deadline.Remaining;
        return value > TimeSpan.Zero ? value : throw new TimeoutException("RetentionPhysicalDeadlineExceeded");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { internal uint Flags; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);
}
