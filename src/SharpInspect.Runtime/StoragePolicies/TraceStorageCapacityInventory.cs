using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.StoragePolicies;

/// <summary>Bounded metadata inventory. It never follows an untrusted entry or reads image pixels.</summary>
internal static class TraceStorageCapacityInventory
{
    internal static TraceStoragePhysicalObservation Observe(ProductionStoreOptions options,
        TraceStoragePolicyDefinition policy, StoreDeadline deadline, CancellationToken token)
    {
        if (!StoragePathValidator.TryValidate(options, out var database, out var reason))
            throw new InvalidOperationException(reason);
        var roots = new List<Root>
        {
            new(TraceStorageArea.Database, Path.GetDirectoryName(database)!,
                AlgorithmContractValidation.HashParts(new[] { "trace-capacity-database-v1", database.ToUpperInvariant() }),
                null, null, null)
        };
        if (options.ImageFinalization is { } images)
        {
            var stage = images.ImageEvidence.Stage;
            roots.Add(new(TraceStorageArea.ImageStage, stage.StageRoot, stage.ContentHash,
                stage.MaximumTotalStageBytes, stage.MaximumStageFiles, stage.MaximumStageBytes));
            var final = images.FinalRoot;
            roots.Add(new(TraceStorageArea.FinalImages, final.FinalRoot, final.ContentHash,
                final.MaximumTotalFinalBytes, final.MaximumFinalFiles, final.MaximumFinalFileBytes));
            AddQuarantine(options.EvidenceReconciliation!.StageQuarantine!, TraceStorageArea.StageQuarantine);
            AddQuarantine(options.EvidenceReconciliation.FinalQuarantine!, TraceStorageArea.FinalQuarantine);
        }
        var protection = EvidenceQuarantine.ProtectRoots(roots.Select(value => value.Path).ToArray());
        try
        {
            var observations = new List<TraceStorageAreaObservation>();
            long dbBytes = 0, walBytes = 0;
            foreach (var root in roots)
            {
                Check();
                var volume = new DriveInfo(Path.GetPathRoot(root.Path)!);
                var total = volume.TotalSize;
                var available = volume.AvailableFreeSpace;
                var reserve = TraceStoragePolicyValidator.RequiredReserve(policy, total);
                long bytes = 0, count = 0;
                var complete = true;
                if (root.Area == TraceStorageArea.Database)
                {
                    dbBytes = ReadLength(database, false);
                    walBytes = ReadLength(database + "-wal", true);
                    bytes = checked(dbBytes + walBytes);
                    count = walBytes > 0 ? 2 : 1;
                }
                else
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(root.Path))
                    {
                        Check();
                        var length = ReadLength(path, false);
                        bytes = checked(bytes + length);
                        count++;
                        if (length > root.MaximumFileBytes)
                            throw new InvalidOperationException("TraceStorageFileExceedsDeclaredBound:" + root.Area);
                        // A lower bound is sufficient to stop admission. Never claim an exact
                        // inventory after truncation, and never enumerate an unbounded directory.
                        if (count > root.MaximumFiles || bytes > root.MaximumBytes)
                        { complete = false; break; }
                    }
                }
                observations.Add(new(root.Area, root.Hash, total, available, reserve, bytes, count,
                    root.MaximumBytes, root.MaximumFiles, complete));
            }
            Check();
            return new(dbBytes, walBytes, observations.AsReadOnly());
        }
        finally { foreach (var handle in protection) handle.Dispose(); }

        void Check()
        {
            token.ThrowIfCancellationRequested();
            SqliteNative.EnsureDeadline(deadline, token);
        }
        void AddQuarantine(EvidenceQuarantineRootOptions root, TraceStorageArea area) =>
            roots.Add(new(area, root.Root, root.BindingHash, root.MaximumTotalBytes, root.MaximumFiles, root.MaximumFileBytes));
        long ReadLength(string path, bool allowMissing)
        {
            Check();
            using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (allowMissing && error == 2) return 0;
                throw new IOException("TraceStorageEntryObservationUnavailable:" + error);
            }
            if (!GetFileInformationByHandle(handle, out var information))
                throw new IOException("TraceStorageEntryIdentityUnavailable");
            if ((information.Attributes & 0x410) != 0 || information.Links != 1)
                throw new InvalidOperationException("TraceStorageEntryKindOrLinksInvalid");
            return checked(((long)information.SizeHigh << 32) | information.SizeLow);
        }
    }

    private sealed record Root(TraceStorageArea Area, string Path, string Hash,
        long? MaximumBytes, long? MaximumFiles, long? MaximumFileBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information information);
}
