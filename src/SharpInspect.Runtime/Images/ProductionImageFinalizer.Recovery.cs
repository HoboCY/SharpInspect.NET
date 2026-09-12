using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

internal sealed partial class ProductionImageFinalizer
{
    internal Task<bool> VerifyPersistedFilesAsync(PendingImageFinalizationWork work,
        ProductionImageSuccessDescriptor? success, bool hasBoundAttempt, TimeSpan timeout,
        CancellationToken token) => RunMaintenanceAsync(deadline =>
    {
        RequireWork(work);
        var stagePath = Path.Combine(_stage.StageRoot, work.Manifest.StageFileName);
        if (File.Exists(stagePath))
        {
            using var stage = OpenProtected(stagePath);
            CanonicalPngCodec.VerifyStage(stage, Descriptor(work.Manifest), _limits.Codec, deadline, token);
        }
        else if (success is null) throw new InvalidOperationException("ProductionImageReferencedStageMissing");
        var finalPath = Path.Combine(_finalRoot, work.Manifest.ManifestId.ToString("N") + ".png");
        if (!File.Exists(finalPath))
        {
            if (success is not null) throw new InvalidOperationException("ProductionImageReferencedFinalMissing");
            return false;
        }
        if (!hasBoundAttempt) throw new InvalidOperationException("ProductionImageUnownedFinalFile");
        using var final = OpenProtected(finalPath);
        var verified = CanonicalPngCodec.Verify(final, Descriptor(work.Manifest), _limits.Codec, deadline, token);
        if (success is not null) RequireSuccess(work, success, verified);
        return true;
    }, timeout, token);

    // Called only with a persisted attempt locator. A valid canonical stage protects
    // the recoverable input while the interrupted encoder's temporary file is removed.
    internal Task<bool> DiscardKnownTemporaryAsync(PendingImageFinalizationWork work, Guid attemptId,
        TimeSpan timeout, CancellationToken token) => RunMaintenanceAsync(deadline =>
    {
        RequireWork(work);
        if (attemptId == Guid.Empty) throw new ArgumentException("ProductionImageAttemptRequired");
        var path = Path.Combine(_finalRoot, work.Manifest.ManifestId.ToString("N") + "." +
            attemptId.ToString("N") + ".tmp");
        if (!File.Exists(path)) return false;
        using var stage = OpenProtected(Path.Combine(_stage.StageRoot, work.Manifest.StageFileName));
        CanonicalPngCodec.VerifyStage(stage, Descriptor(work.Manifest), _limits.Codec, deadline, token);
        using var temporary = OpenForDeletion(path);
        SqliteNative.EnsureDeadline(deadline, token);
        DeleteOpenedFile(temporary);
        return true;
    }, timeout, token);

    // Runtime calls this only after Succeeded is durable. It verifies and protects
    // that exact final PNG throughout removal of the stage, including restart replay.
    internal Task<bool> ReleaseSucceededStageAsync(PendingImageFinalizationWork work,
        ProductionImageSuccessDescriptor success, TimeSpan timeout, CancellationToken token) =>
        RunMaintenanceAsync(deadline =>
    {
        RequireWork(work);
        using var final = OpenProtected(Path.Combine(_finalRoot, work.Manifest.ManifestId.ToString("N") + ".png"));
        var verified = CanonicalPngCodec.Verify(final, Descriptor(work.Manifest), _limits.Codec, deadline, token);
        RequireSuccess(work, success, verified);
        var stagePath = Path.Combine(_stage.StageRoot, work.Manifest.StageFileName);
        if (!File.Exists(stagePath)) return false;
        using var stage = OpenForDeletion(stagePath);
        CanonicalPngCodec.VerifyStage(stage, Descriptor(work.Manifest), _limits.Codec, deadline, token);
        SqliteNative.EnsureDeadline(deadline, token);
        DeleteOpenedFile(stage);
        return true;
    }, timeout, token);

    internal Task VerifyKnownInventoryAsync(IReadOnlySet<string> stageNames,
        IReadOnlySet<string> finalNames, TimeSpan timeout, CancellationToken token) =>
        RunMaintenanceAsync(deadline =>
        {
            RequireRoots();
            VerifyInventory(_stage.StageRoot, stageNames, _stage.MaximumStageFiles,
                _stage.MaximumTotalStageBytes, deadline, token);
            VerifyInventory(_finalRoot, finalNames, _limits.MaximumFinalFiles,
                _limits.MaximumFinalBytes, deadline, token);
            return true;
        }, timeout, token);

    private static void VerifyInventory(string root, IReadOnlySet<string> known, int maximumFiles,
        long maximumBytes, StoreDeadline deadline, CancellationToken token)
    {
        var count = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            SqliteNative.EnsureDeadline(deadline, token);
            if (++count > maximumFiles || !known.Contains(Path.GetFileName(path)))
                throw new InvalidOperationException("ProductionImageStartupReconciliationRequired");
            using var file = OpenProtected(path);
            bytes = checked(bytes + file.Length);
            if (bytes > maximumBytes) throw new InvalidOperationException("ProductionImageRootCapacityExceeded");
        }
    }

    private void RequireWork(PendingImageFinalizationWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        RequireRoots();
        RequireRelativeName(work.Manifest.StageFileName, ".stage");
        if (work.Manifest.StageRootBindingHash != _stage.ContentHash)
            throw new InvalidOperationException("ProductionImageStageRootBindingMismatch");
    }

    private void RequireSuccess(PendingImageFinalizationWork work,
        ProductionImageSuccessDescriptor success, VerifiedCanonicalPng verified)
    {
        if (success.FinalRootBindingHash != _finalRootBindingHash ||
            success.FinalFileName != work.Manifest.ManifestId.ToString("N") + ".png" ||
            success.EncodedByteLength != verified.EncodedByteLength ||
            success.CanonicalPixelHash != verified.CanonicalPixelHash)
            throw new InvalidOperationException("ProductionImageSucceededFileMismatch");
    }

    private async Task<T> RunMaintenanceAsync<T>(Func<StoreDeadline, T> action, TimeSpan timeout,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var deadline = new StoreDeadline(timeout);
        if (Interlocked.CompareExchange(ref _operationSlot, 1, 0) != 0)
            throw new InvalidOperationException("ProductionImageFinalizationPhysicalOperationPending");
        var worker = Task.Run(() =>
        {
            try { SqliteNative.EnsureDeadline(deadline, token); return action(deadline); }
            finally { Volatile.Write(ref _operationSlot, 0); }
        }, CancellationToken.None);
        Volatile.Write(ref _physicalCompletion, worker);
        // Observe faults even after a semantic timeout. No second operation may enter
        // until the physical task's finally releases the one shared file slot.
        _ = worker.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await worker.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
    }

    private static FileStream OpenForDeletion(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("ProductionImageFileKindInvalid");
        var handle = CreateFileForDeletion(path, 0x80010000, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("ProductionImageDeleteHandleUnavailable", new Win32Exception(error));
        }
        FileStream? stream = null;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, CanonicalPngCodec.ChunkBufferBytes);
            RequireHandlePath(stream, path);
            return stream;
        }
        catch { if (stream is null) handle.Dispose(); else stream.Dispose(); throw; }
    }

    private static void DeleteOpenedFile(FileStream file)
    {
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, 1))
            throw new IOException("ProductionImageDeleteFailed", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileForDeletion(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDisposition information, uint size);
}
