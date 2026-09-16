using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;

#pragma warning disable CA1416
namespace SharpInspect.Runtime.Diagnostics;

/// <summary>
/// Private immutable artifacts in one installation-bound root. A file name never publishes an
/// export: only Runtime's audited completion releases a read capability. Incomplete files remain
/// restricted and charged until their sealed expiry, including after process restart.
/// </summary>
internal sealed class SupportBundleStore : IDisposable
{
    internal sealed record RetentionSeal(Guid BundleId, string ContentHash, long Bytes, DateTimeOffset Expires);
    private const string OwnerName = ".support-writer";
    private readonly SupportBundleOptions _options;
    private readonly string _databasePath;
    private readonly long _reserveBytes;
    private readonly decimal _reservePercent;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IReadOnlyDictionary<Guid, RetentionSeal> _seals;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<SafeFileHandle>? _roots;
    private FileStream? _owner;
    private bool _disposed;

    internal SupportBundleStore(SupportBundleOptions options, string databasePath, long reserveBytes,
        decimal reservePercent, IReadOnlyList<RetentionSeal> seals, Func<DateTimeOffset>? clock = null)
    {
        if (!Path.IsPathFullyQualified(databasePath) || reserveBytes < 0 || reservePercent is < 0 or > 100)
            throw new ArgumentException("SupportBundleStoreConfigurationInvalid");
        _options = options; _databasePath = Path.GetFullPath(databasePath);
        _reserveBytes = reserveBytes; _reservePercent = reservePercent; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (seals.Count > options.Policy.MaximumOperationFacts || seals.Any(value => value.BundleId == Guid.Empty ||
            value.ContentHash is not { Length: 64 } || !value.ContentHash.All(Uri.IsHexDigit) || value.Bytes <= 0 ||
            value.Bytes > options.Policy.MaximumBundleBytes || value.Expires.Offset != TimeSpan.Zero))
            throw new ArgumentException("SupportBundleRetentionSealsInvalid");
        _seals = seals.ToDictionary(value => value.BundleId);
    }

    internal PreparedBundle Stage(Guid bundleId, SupportBundleScope scope, LoggingDiagnosticsPolicy logging,
        IReadOnlyList<string> source, CancellationToken cancellationToken)
    {
        if (bundleId == Guid.Empty) throw new ArgumentException("SupportBundleIdentityInvalid");
        _gate.Wait(cancellationToken); FileStream? stream = null; var transferred = false;
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SupportBundleStore));
            AcquireOwner(); var now = _clock().ToUniversalTime();
            var expires = now + _options.Policy.ExportTimeout + _options.Policy.Retention;
            var inventory = Inventory();
            foreach (var entry in inventory.Where(item => item.Seal is not null && item.Expires <= now).ToArray())
            { Retire(entry); inventory.Remove(entry); }
            if (inventory.Count >= _options.Files.MaximumFiles ||
                inventory.Sum(item => item.Length) > _options.Files.MaximumTotalBytes - _options.Policy.MaximumBundleBytes)
                throw new InvalidOperationException("SupportBundleCapacityExceeded");
            EnsureReserve(_options.Policy.MaximumBundleBytes); cancellationToken.ThrowIfCancellationRequested();
            var projection = SupportBundleProjection.Build(bundleId, scope, _options.Policy, logging, source, now, expires);
            if (projection.Bytes.Length > _options.Policy.MaximumBundleBytes ||
                Convert.ToHexString(SHA256.HashData(projection.Bytes)) != projection.ContentHash)
                throw new InvalidOperationException("SupportBundleContentInvalid");
            var name = Name(bundleId, now, expires); var path = Path.Combine(_options.Files.Directory, name);
            RequireInstallation(); cancellationToken.ThrowIfCancellationRequested();
            stream = OpenOwned(path, create: true); Validate(stream.SafeFileHandle, path);
            stream.Write(projection.Bytes); stream.Flush(flushToDisk: true);
            // A late return never becomes publication: the caller still checks cancellation,
            // live authority and its durable completion before exposing any read capability.
            RequireInstallation(); Validate(stream.SafeFileHandle, path);
            stream.Position = 0;
            if (stream.Length != projection.Bytes.Length || StreamHash(stream) != projection.ContentHash)
                throw new InvalidOperationException("SupportBundleWrittenHashMismatch");
            var prepared = new PreparedBundle(bundleId, now, expires, projection, stream, _gate);
            transferred = true; return prepared;
        }
        finally { if (!transferred) { stream?.Dispose(); _gate.Release(); } }
    }

    private void AcquireOwner()
    {
        RequireInstallation();
        if (_owner is not null) return;
        var roots = EvidenceQuarantine.ProtectRoots(_options.Files.Directory);
        FileStream? owner = null;
        try
        {
            RequireInstallation(); var path = Path.Combine(_options.Files.Directory, OwnerName);
            owner = OpenOwned(path, create: false); Validate(owner.SafeFileHandle, path);
            if (owner.Length != 0) throw new InvalidOperationException("SupportBundleOwnerInvalid");
            _roots = roots; _owner = owner;
        }
        catch { owner?.Dispose(); foreach (var handle in roots) handle.Dispose(); throw; }
    }

    private void RequireInstallation()
    {
        var identity = DiagnosticDirectoryInstallation.Describe(_options.Files);
        if (identity.Binding != _options.InstallationBinding)
            throw new InvalidOperationException("SupportBundleInstallationChanged");
        using var databaseDirectory = Open(Path.GetDirectoryName(_databasePath)!, 0x80, 7, IntPtr.Zero, 3);
        var actual = Info(databaseDirectory);
        if (actual.Volume != identity.Volume || (actual.Attributes & 0x410) != 0x10)
            throw new InvalidOperationException("SupportBundleDatabaseVolumeMismatch");
    }
    private void EnsureReserve(int required)
    {
        RequireInstallation();
        var volume = new DriveInfo(Path.GetPathRoot(_options.Files.Directory)!);
        var reserve = Math.Max(_reserveBytes, decimal.ToInt64(decimal.Ceiling(volume.TotalSize * _reservePercent / 100m)));
        if (volume.AvailableFreeSpace - required < reserve) throw new InvalidOperationException("SupportBundleStorageReserveViolated");
    }
    private sealed record Entry(string Path, DateTimeOffset Expires, long Length, string Identity, RetentionSeal? Seal);
    private List<Entry> Inventory()
    {
        var entries = new List<Entry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(_options.Files.Directory))
        {
            if (Path.GetFileName(path) == OwnerName && _owner is not null) continue;
            if (entries.Count >= _options.Files.MaximumFiles) throw new InvalidOperationException("SupportBundleInventoryLimit");
            if (!ParseName(Path.GetFileName(path), out var expiry, out var bundleId)) throw new InvalidOperationException("SupportBundleUnknownEntry");
            using var handle = Open(path, 0x80, 1, IntPtr.Zero, 3);
            var information = Validate(handle, path); var length = Length(information);
            if (length > _options.Policy.MaximumBundleBytes) throw new InvalidOperationException("SupportBundleExistingSizeExceeded");
            _seals.TryGetValue(bundleId, out var seal);
            if (seal is not null && (seal.Expires != expiry || seal.Bytes != length))
                throw new InvalidOperationException("SupportBundleRetentionSealMismatch");
            // An interrupted file without a signed seal remains private and charged.
            // A syntactically valid filename never authorizes its automatic deletion.
            entries.Add(new(path, expiry, length, Identity(information), seal));
        }
        return entries;
    }
    private void Retire(Entry entry)
    {
        RequireInstallation();
        using (var handle = Open(entry.Path, 0x10081, 1, IntPtr.Zero, 3))
        {
            var current = Validate(handle, entry.Path);
            if (entry.Seal is null || entry.Seal.Expires != entry.Expires || Identity(current) != entry.Identity ||
                Length(current) != entry.Length || entry.Expires > _clock().ToUniversalTime())
                throw new InvalidOperationException("SupportBundleRetirementIdentityChanged");
            using var stream = new FileStream(handle, FileAccess.Read, 4096);
            if (StreamHash(stream) != entry.Seal.ContentHash)
                throw new InvalidOperationException("SupportBundleRetentionContentChanged");
            var disposition = 3u; // FILE_DISPOSITION_DELETE | POSIX_SEMANTICS, on this exact opened object.
            if (!SetFileInformationByHandle(handle, 21, ref disposition, 4))
                throw new IOException("SupportBundleRetirementFailed");
        }
        if (File.Exists(entry.Path)) throw new IOException("SupportBundleRetirementIncomplete");
    }

    private static string StreamHash(Stream stream)
    { using var hash = SHA256.Create(); return Convert.ToHexString(hash.ComputeHash(stream)); }

    private static string Name(Guid id, DateTimeOffset created, DateTimeOffset expires) =>
        "support-" + created.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" +
        expires.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + id.ToString("N") + ".sib";
    private bool ParseName(string name, out DateTimeOffset expiry, out Guid bundleId)
    {
        expiry = default; bundleId = Guid.Empty;
        if (name.Length != 84 || !name.StartsWith("support-", StringComparison.Ordinal) ||
            !name.EndsWith(".sib", StringComparison.Ordinal) || name[27] != '-' || name[47] != '-' ||
            !long.TryParse(name.AsSpan(8, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var created) ||
            !long.TryParse(name.AsSpan(28, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
            !Guid.TryParseExact(name.AsSpan(48, 32), "N", out var id) || id == Guid.Empty || created < 0 ||
            expires > DateTimeOffset.MaxValue.Ticks || expires <= created ||
            expires - created != _options.Policy.ExportTimeout.Ticks + _options.Policy.Retention.Ticks) return false;
        expiry = new DateTimeOffset(expires, TimeSpan.Zero);
        bundleId = id;
        return name == Name(id, new DateTimeOffset(created, TimeSpan.Zero), expiry);
    }

    internal sealed class PreparedBundle : IDisposable
    {
        private FileStream? _file; private readonly SemaphoreSlim _gate;
        internal PreparedBundle(Guid id, DateTimeOffset created, DateTimeOffset expires,
            SupportBundleProjectionResult projection, FileStream file, SemaphoreSlim gate)
        { Id = id; Created = created; Expires = expires; Projection = projection; _file = file; _gate = gate; }
        internal Guid Id { get; }
        internal DateTimeOffset Created { get; }
        internal DateTimeOffset Expires { get; }
        internal SupportBundleProjectionResult Projection { get; }
        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _file, null);
            if (stream is null) return;
            try { stream.Dispose(); } finally { _gate.Release(); }
        }
    }
    public void Dispose()
    {
        // The owner invokes this only after its physical stage task has retired.
        _gate.Wait();
        try
        {
            _disposed = true; _owner?.Dispose(); _owner = null;
            if (_roots is not null) foreach (var handle in _roots) handle.Dispose();
            _roots = null;
        }
        finally { _gate.Release(); }
    }

    private static FileInformation Validate(SafeFileHandle handle, string path)
    {
        var value = Info(handle);
        if (value.Links != 1 || (value.Attributes & 0x410) != 0 ||
            !DiagnosticDirectoryInstallation.HandlePathMatches(handle, path) ||
            !DiagnosticDirectoryInstallation.ValidateRestrictedAcl(path, false))
            throw new InvalidOperationException("SupportBundleFileIdentityInvalid");
        return value;
    }
    private static long Length(FileInformation value) => ((long)value.SizeHigh << 32) | value.SizeLow;
    private static string Identity(FileInformation value) => string.Join("-", value.Volume, value.IndexHigh,
        value.IndexLow, value.CreationHigh, value.CreationLow);
    private static FileInformation Info(SafeFileHandle handle) => !handle.IsInvalid && GetFileInformationByHandle(handle, out var value)
        ? value : throw new IOException("SupportBundleFileUnavailable");

    private static FileStream OpenOwned(string path, bool create)
    {
        var bytes = DiagnosticDirectoryInstallation.CreateRestrictedFileSecurity().GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            Marshal.StructureToPtr(new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = pinned.AddrOfPinnedObject() }, pointer, false);
            var handle = Open(path, (uint)FileSystemRights.FullControl, 1, pointer, create ? 1u : 4u);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("SupportBundleWriterUnavailable"); }
            try { return new FileStream(handle, FileAccess.ReadWrite, 4096); }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(pointer); pinned.Free(); }
    }
    private static SafeFileHandle Open(string path, uint access, uint share, IntPtr security, uint creation) =>
        CreateFileNative(@"\\?\" + Path.GetFullPath(path), access, share, security, creation, 0x02200000, IntPtr.Zero);
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref uint information, uint size);
}
