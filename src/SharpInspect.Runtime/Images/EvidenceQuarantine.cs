using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

internal sealed record QuarantinedFileDescriptor(Guid OrphanId, string SourceFileName,
    string QuarantineFileName, string SourceRootBindingHash, string QuarantineRootBindingHash,
    string FileIdentityHash, long ByteLength, string RawContentHash);
internal enum EvidenceQuarantineBoundary { BeforeRename }

/// <summary>Protected identity, byte-preserving rename, and recovery of one durable orphan intent.</summary>
internal sealed class EvidenceQuarantine
{
    // A claim retains this physical ownership through the two database commits. Serializing
    // the very small quarantine workload also makes all in-process capacity reservations exact.
    private static readonly SemaphoreSlim CapacityOwner = new(1, 1);
    private readonly string _sourceRoot;
    private readonly string _sourceBinding;
    private readonly EvidenceQuarantineRootOptions _target;
    private readonly Action<EvidenceQuarantineBoundary>? _faultHook;
    private Task _physicalCompletion = Task.CompletedTask;
    internal Task PhysicalCompletion => Volatile.Read(ref _physicalCompletion);

    internal EvidenceQuarantine(string sourceRoot, string sourceRootBindingHash, EvidenceQuarantineRootOptions quarantine,
        Action<EvidenceQuarantineBoundary>? faultHook = null)
    {
        _sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        _sourceBinding = sourceRootBindingHash;
        _target = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _faultHook = faultHook;
        if (!EvidenceReconciliationStorageCodec.IsHash(sourceRootBindingHash))
            throw new ArgumentException("EvidenceQuarantineSourceHashInvalid");
        RequireRoots();
    }

    internal Task<QuarantineMoveClaim> PrepareAsync(Guid orphanId, string relativeFileName,
        TimeSpan timeout, CancellationToken cancellationToken) => AcquireAsync(deadline =>
            Prepare(orphanId, relativeFileName, null, deadline, cancellationToken), timeout, cancellationToken);

    internal async Task<QuarantineMoveClaim> RecoverAsync(QuarantinedFileDescriptor intent,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var claim = await AcquireAsync(deadline => Prepare(intent.OrphanId, intent.SourceFileName, intent,
            deadline, cancellationToken), timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!claim.Moved) await MoveAsync(claim, timeout, cancellationToken).ConfigureAwait(false);
            return claim;
        }
        catch { claim.Dispose(); throw; }
    }

    internal async Task<QuarantineMoveClaim> VerifyCompletedAsync(QuarantinedFileDescriptor completed,
        TimeSpan timeout, CancellationToken token)
    {
        var claim = await AcquireAsync(deadline => Prepare(completed.OrphanId, completed.SourceFileName,
            completed, deadline, token), timeout, token).ConfigureAwait(false);
        if (claim.Moved) return claim;
        claim.Dispose();
        throw new InvalidOperationException("EvidenceQuarantineCompletedTargetMissing");
    }

    private async Task<QuarantineMoveClaim> AcquireAsync(Func<StoreDeadline, QuarantineMoveClaim> action,
        TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var deadline = new StoreDeadline(timeout);
        if (!await CapacityOwner.WaitAsync(deadline.Remaining, token).ConfigureAwait(false))
            throw new TimeoutException("EvidenceQuarantinePhysicalOwnerUnavailable");
        var task = Task.Run(() =>
        {
            try { SqliteNative.EnsureDeadline(deadline, token); return action(deadline); }
            catch { CapacityOwner.Release(); throw; }
        }, CancellationToken.None);
        Volatile.Write(ref _physicalCompletion, task);
        try { return await task.WaitAsync(deadline.Remaining, token).ConfigureAwait(false); }
        catch
        {
            // Cancellation cannot publish an unclaimed, still-protected file or release a
            // slot while physical IO is running. Dispose a late result after its actual exit.
            _ = task.ContinueWith(done =>
            {
                if (done.Status == TaskStatus.RanToCompletion) done.Result.Dispose();
                else _ = done.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal async Task MoveAsync(QuarantineMoveClaim claim, TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        claim.RequireOwner(this);
        var lease = claim.EnterPhysical();
        var deadline = new StoreDeadline(timeout);
        var task = Task.Run(() =>
        {
            try { Move(claim, deadline, token); }
            finally { lease.Dispose(); }
        }, CancellationToken.None);
        Volatile.Write(ref _physicalCompletion, task);
        _ = task.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await task.WaitAsync(deadline.Remaining, token).ConfigureAwait(false);
    }

    private QuarantineMoveClaim Prepare(Guid orphanId, string sourceName, QuarantinedFileDescriptor? intent,
        StoreDeadline deadline, CancellationToken token)
    {
        if (orphanId == Guid.Empty) throw new ArgumentException("EvidenceQuarantineIdentityRequired");
        RequireName(sourceName);
        RequireRoots();
        var targetName = orphanId.ToString("N") + ".quarantine";
        if (intent is not null && (intent.SourceRootBindingHash != _sourceBinding ||
            intent.QuarantineRootBindingHash != _target.BindingHash || intent.QuarantineFileName != targetName))
            throw new InvalidOperationException("EvidenceQuarantineIntentBindingMismatch");
        var roots = ProtectRoots(_sourceRoot, _target.Root);
        FileStream? file = null;
        try
        {
            RequireRoots();
            var sourcePath = Path.Combine(_sourceRoot, sourceName);
            var targetPath = Path.Combine(_target.Root, targetName);
            var sourceExists = EntryExists(sourcePath);
            var targetExists = EntryExists(targetPath);
            if (sourceExists && targetExists) throw new InvalidOperationException("EvidenceQuarantineBothLocationsOccupied");
            if (!sourceExists && (!targetExists || intent is null))
                throw new InvalidOperationException("EvidenceQuarantineSourceMissing");
            if (targetExists && intent is null) throw new InvalidOperationException("EvidenceQuarantineTargetOccupied");
            file = OpenFile(targetExists ? targetPath : sourcePath);
            var info = Information(file.SafeFileHandle);
            var length = checked(((long)info.SizeHigh << 32) + info.SizeLow);
            if (length > _target.MaximumFileBytes) throw new InvalidOperationException("EvidenceQuarantineFileCapacityExceeded");
            var hash = HashFile(file, length, deadline, token);
            var descriptor = new QuarantinedFileDescriptor(orphanId, sourceName, targetName,
                _sourceBinding, _target.BindingHash, IdentityHash(info), length, hash);
            if (intent is not null && descriptor != intent)
                throw new InvalidOperationException("EvidenceQuarantineSourceIdentityChanged");
            RequireCapacity(targetExists ? 0 : length, targetExists ? 0 : 1, deadline, token);
            var claim = new QuarantineMoveClaim(this, descriptor, file, roots, targetExists, () => CapacityOwner.Release());
            file = null; roots = null!;
            return claim;
        }
        finally
        {
            file?.Dispose();
            if (roots is not null) foreach (var root in roots) root.Dispose();
        }
    }

    private void Move(QuarantineMoveClaim claim, StoreDeadline deadline, CancellationToken token)
    {
        claim.RequireOwner(this);
        SqliteNative.EnsureDeadline(deadline, token);
        if (claim.Moved) return;
        RequireCapacity(claim.Descriptor.ByteLength, 1, deadline, token);
        var destination = Path.Combine(_target.Root, claim.Descriptor.QuarantineFileName);
        _faultHook?.Invoke(EvidenceQuarantineBoundary.BeforeRename);
        SqliteNative.EnsureDeadline(deadline, token);
        RenameOpenedFile(claim.File.SafeFileHandle, destination);
        // Keep the same handle; a pathname lookup can never substitute another file.
        RequireHandlePath(claim.File.SafeFileHandle, destination);
        if (IdentityHash(Information(claim.File.SafeFileHandle)) != claim.Descriptor.FileIdentityHash)
            throw new InvalidOperationException("EvidenceQuarantineMovedIdentityMismatch");
        claim.MarkMoved();
    }

    private void RequireRoots()
    {
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(Path.Combine(_sourceRoot, ".probe")),
            out _, out _)) throw new InvalidOperationException("EvidenceQuarantineSourceRootInvalid");
        _target.ValidateAgainst(_sourceRoot);
    }

    private void RequireCapacity(long addedBytes, int addedFiles, StoreDeadline deadline, CancellationToken token)
    {
        long bytes = addedBytes; var count = addedFiles;
        foreach (var path in Directory.EnumerateFileSystemEntries(_target.Root))
        {
            SqliteNative.EnsureDeadline(deadline, token);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException("EvidenceQuarantineInventoryConflict");
            using var metadata = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (metadata.IsInvalid) throw NativeFailure("EvidenceQuarantineInventoryUnavailable");
            var info = Information(metadata);
            if (info.Links != 1 || (info.Attributes & 0x410) != 0)
                throw new InvalidOperationException("EvidenceQuarantineInventoryConflict");
            RequireHandlePath(metadata, path);
            bytes = checked(bytes + new FileInfo(path).Length);
            if (++count > _target.MaximumFiles || bytes > _target.MaximumTotalBytes)
                throw new InvalidOperationException("EvidenceQuarantineCapacityExceeded");
        }
        if (count > _target.MaximumFiles || bytes > _target.MaximumTotalBytes)
            throw new InvalidOperationException("EvidenceQuarantineCapacityExceeded");
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
    }

    internal static void RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." ||
            name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 ||
            name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("EvidenceQuarantineRelativeNameInvalid");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')
            throw new ArgumentException("EvidenceQuarantineRelativeNameInvalid");
    }

    internal static string HashFile(FileStream file, long expectedLength, StoreDeadline deadline, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; long bytes = 0;
        while (true)
        {
            SqliteNative.EnsureDeadline(deadline, token);
            var read = file.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            bytes = checked(bytes + read);
            if (bytes > expectedLength) throw new InvalidOperationException("EvidenceQuarantineFileChanged");
            hash.AppendData(buffer, 0, read);
        }
        if (bytes != expectedLength) throw new InvalidOperationException("EvidenceQuarantineFileChanged");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static List<SafeFileHandle> ProtectRoots(params string[] roots)
    {
        var handles = new List<SafeFileHandle>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in roots)
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                if (!seen.Add(directory.FullName)) continue;
                // FILE_READ_ATTRIBUTES alone does not participate in Windows share-access
                // accounting. Include directory read access so omitting FILE_SHARE_DELETE
                // really prevents a rename after the last child file has been removed.
                var handle = CreateFile(directory.FullName, 0x80000080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw NativeFailure("EvidenceQuarantineRootProtectionUnavailable"); }
                handles.Add(handle);
                var info = Information(handle);
                if ((info.Attributes & 0x410) != 0x10) throw new InvalidOperationException("EvidenceQuarantineRootKindInvalid");
                RequireHandlePath(handle, directory.FullName);
                if (handles.Count > 128) throw new InvalidOperationException("EvidenceQuarantineRootDepthExceeded");
            }
            return handles;
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    internal static void RequireUniqueFile(FileStream file)
    {
        var info = Information(file.SafeFileHandle);
        if ((info.Attributes & 0x410) != 0 || info.Links != 1)
            throw new InvalidOperationException("EvidenceReconciliationFileUniquenessInvalid");
    }

    internal static FileStream OpenFile(string path)
    {
        var handle = CreateFile(path, 0x80010000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw NativeFailure("EvidenceQuarantineFileProtectionUnavailable"); }
        try
        {
            var info = Information(handle);
            if ((info.Attributes & 0x410) != 0 || info.Links != 1)
                throw new InvalidOperationException("EvidenceQuarantineFileKindOrUniquenessInvalid");
            RequireHandlePath(handle, path);
            return new FileStream(handle, FileAccess.Read, 64 * 1024);
        }
        catch { handle.Dispose(); throw; }
    }

    private static string IdentityHash(FileInformation info) => Convert.ToHexString(SHA256.HashData(
        AuditCanonical.Encode("EvidenceQuarantineFileIdentityV1", info.Volume.ToString("X8"),
            info.IndexHigh.ToString("X8"), info.IndexLow.ToString("X8"),
            info.CreationHigh.ToString("X8"), info.CreationLow.ToString("X8"))));

    internal static string IdentityHash(FileStream file) => IdentityHash(Information(file.SafeFileHandle));

    private static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeFailure("EvidenceQuarantineIdentityUnavailable");
        return info;
    }

    private static void RequireHandlePath(SafeFileHandle handle, string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw NativeFailure("EvidenceQuarantineHandlePathUnavailable");
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(Path.TrimEndingDirectorySeparator(actual),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EvidenceQuarantineHandlePathMismatch");
    }

    private static void RenameOpenedFile(SafeFileHandle handle, string destination)
    {
        // FILE_RENAME_INFO: flags/BOOLEAN union, aligned HANDLE, DWORD byte length,
        // then UTF-16 name. Zero flags means the kernel refuses an existing destination.
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var name = Encoding.Unicode.GetBytes(destination + '\0');
        var size = checked(nameOffset + name.Length);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length - 2);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size))
                throw NativeFailure("EvidenceQuarantineRenameFailed");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IOException NativeFailure(string reason) => new(reason, new Win32Exception(Marshal.GetLastWin32Error()));
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    private static SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template) => CreateFileNative(
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + Path.GetFullPath(path),
            access, share, security, creation, flags, template);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        IntPtr information, uint size);
}
