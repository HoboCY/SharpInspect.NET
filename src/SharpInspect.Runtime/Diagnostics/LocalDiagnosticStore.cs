using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Integrity;
using RootIdentity = SharpInspect.Runtime.Diagnostics.DiagnosticDirectoryInstallation.RootIdentity;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Diagnostics;

/// <summary>
/// One serialized local diagnostic channel: rolling UTF-8 JSON Lines with sealed per-file expiry,
/// exact byte/file/total budgets and an installation-verified root. The store accepts only already
/// classified complete JSON object lines from its owner; it is not a public logger. Every physical
/// retirement happens under the same writer lease and on the exact opened object, never by path.
/// Expiry is sealed into the file name at creation (<c>creation + rollAfter + retention</c>), so
/// file-system timestamps are never the retention authority. A restarted store always opens a new
/// file instead of adopting an existing one.
/// </summary>
internal sealed class LocalDiagnosticStore : IAsyncDisposable
{
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint ReadData = 0x00000001;
    private const uint ReadEa = 0x00000008;
    private const uint ReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;
    private const uint DeleteAccess = 0x00010000;
    private const int ReadChunkBytes = 64 * 1024;
    private const int DispositionInformationEx = 21;
    private const uint DispositionDeleteWithPosixSemantics = 0x3;
    private const long MaximumNameTimestamp = 9_999_999_999_999;
    private static readonly byte[] NamePrefix = Encoding.ASCII.GetBytes("diag-");
    private static readonly byte[] NameSuffix = Encoding.ASCII.GetBytes(".jsonl");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly DiagnosticLocalStoreOptions _options;
    private readonly string _installationBinding;
    private readonly string _databasePath;
    private readonly long _minimumReserveBytes;
    private readonly decimal _minimumReservePercent;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action? _beforeInstallationVerificationForTesting;
    private readonly long _rollMilliseconds;
    private readonly long _retentionMilliseconds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string WriterLeaseName = ".diagnostic-writer";
    private FileStream? _writerLease;
    private RootProtection? _rootProtection;
    private ActiveFile? _active;
    private bool _disposed;

    internal LocalDiagnosticStore(DiagnosticLocalStoreOptions options, string installationBinding, string databasePath,
        long minimumReserveBytes, decimal minimumReservePercent, Func<DateTimeOffset>? utcNow = null,
        Action? beforeInstallationVerificationForTesting = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (installationBinding is null || installationBinding.Length != 64 || !IsHex(installationBinding))
            throw new ArgumentException("DiagnosticStoreInstallationBindingInvalid", nameof(installationBinding));
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath) ||
            databasePath.StartsWith(@"\\", StringComparison.Ordinal) || databasePath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("DiagnosticStoreDatabasePathInvalid", nameof(databasePath));
        string database;
        try { database = Path.GetFullPath(databasePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("DiagnosticStoreDatabasePathInvalid", nameof(databasePath), ex); }
        if (minimumReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumReserveBytes), "DiagnosticStoreReserveOutOfRange");
        if (minimumReservePercent < 0m || minimumReservePercent > 100m)
            throw new ArgumentOutOfRangeException(nameof(minimumReservePercent), "DiagnosticStoreReservePercentOutOfRange");
        _installationBinding = installationBinding;
        _databasePath = database;
        _minimumReserveBytes = minimumReserveBytes;
        _minimumReservePercent = minimumReservePercent;
        _clock = utcNow ?? (() => DateTimeOffset.UtcNow);
        _beforeInstallationVerificationForTesting = beforeInstallationVerificationForTesting;
        _rollMilliseconds = checked((long)_options.RollAfter.TotalMilliseconds);
        _retentionMilliseconds = checked((long)_options.Retention.TotalMilliseconds);
    }

    /// <summary>Validates and appends exactly one complete JSON object line. Validation, the actual
    /// installation binding, the bounded inventory, expiry retirement, the Storage Reserve and the
    /// byte/file/total budgets are all checked before any byte reaches the file.</summary>
    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> completeUtf8JsonLine, CancellationToken token)
    {
        var record = ValidateRecord(completeUtf8JsonLine);
        ThrowIfDisposed();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var now = CurrentUnixMilliseconds();
            var root = RequireInstallation();
            EnsureReserve(root, record.Length);
            AcquireWriterLease();
            root = RequireInstallation();
            var inventory = Inventory();
            CleanupExpired(inventory, now);
            EnsureReserve(root, record.Length);
            EnsureActiveUsable();
            var liveBytes = SumLengths(inventory);
            var createNew = _active is null || now >= _active.CreatedMilliseconds + _rollMilliseconds ||
                _active.Length + record.Length > _options.MaximumFileBytes;
            if (createNew)
            {
                // A full unexpired inventory rejects the write; capacity is never bought by
                // retiring a file that is still inside its sealed window.
                if (inventory.Count + 1 > _options.MaximumFiles)
                    throw new InvalidOperationException("DiagnosticStoreFileBudgetExceeded");
                if (checked(liveBytes + record.Length) > _options.MaximumTotalBytes)
                    throw new InvalidOperationException("DiagnosticStoreByteBudgetExceeded");
                DisposeActive();
                _active = CreateActiveFile(now);
            }
            else if (checked(liveBytes + record.Length) > _options.MaximumTotalBytes)
            {
                throw new InvalidOperationException("DiagnosticStoreByteBudgetExceeded");
            }

            Append(_active!, record);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns the newest complete records that fit both bounds, in write order
    /// (oldest first). Files, records and bytes are hard-bounded; a torn trailing fragment is
    /// never returned and no partial line is ever emitted.</summary>
    internal async ValueTask<IReadOnlyList<string>> ReadLinesAsync(int maximumRecords, int maximumTotalBytes,
        CancellationToken token)
    {
        if (maximumRecords < 0) throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        if (maximumTotalBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));
        ThrowIfDisposed();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var now = CurrentUnixMilliseconds();
            RequireInstallation();
            AcquireWriterLease();
            RequireInstallation();
            var inventory = Inventory();
            var ordered = inventory.Where(entry => entry.ExpiryMilliseconds > now)
                .OrderByDescending(entry => entry.CreatedMilliseconds)
                .ThenByDescending(entry => entry.Name, StringComparer.Ordinal)
                .ToList();
            var records = maximumRecords;
            var bytes = maximumTotalBytes;
            var scanRemaining = (long)maximumTotalBytes + _options.MaximumRecordBytes;
            var collected = new List<string>();
            foreach (var entry in ordered)
            {
                if (records == 0 || bytes == 0 || scanRemaining == 0) break;
                var lines = ReadTail(entry, records, bytes, token, out var usedRecords, out var usedBytes,
                    out var limited, ref scanRemaining);
                if (lines.Count > 0) collected.InsertRange(0, lines);
                records -= usedRecords;
                bytes -= usedBytes;
                if (limited) break; // Never fill a newer omitted record's gap with older history.
            }
            return collected;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the active file handle with the writer lease held. Nothing is deleted and
    /// every retired file keeps its final bytes.</summary>
    internal async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed)) return;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            DisposeActive();
            _writerLease?.Dispose(); _writerLease = null;
            _rootProtection?.Dispose(); _rootProtection = null;
        }
        finally { _gate.Release(); }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => DisposeAsync();

    private void AcquireWriterLease()
    {
        if (_writerLease is not null) return;
        var protection = new RootProtection(_options.Directory);
        FileStream? lease = null;
        try
        {
            RequireInstallation();
            var path = Path.Combine(_options.Directory, WriterLeaseName);
            lease = OpenWriterLease(path);
            var info = Information(lease.SafeFileHandle, "DiagnosticStoreWriterLeaseInvalid");
            if (info.Links != 1 || Length(info) != 0 ||
                (info.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 ||
                !DiagnosticDirectoryInstallation.HandlePathMatches(lease.SafeFileHandle, path) ||
                !DiagnosticDirectoryInstallation.ValidateRestrictedAcl(path, false))
                throw new InvalidOperationException("DiagnosticStoreWriterLeaseInvalid");
            _writerLease = lease; _rootProtection = protection;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lease?.Dispose(); protection.Dispose();
            throw new InvalidOperationException("DiagnosticStoreWriterUnavailable");
        }
    }

    private static FileStream OpenWriterLease(string path)
    {
        var descriptor = DiagnosticDirectoryInstallation.CreateRestrictedFileSecurity().GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        var attributesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
        try
        {
            Marshal.StructureToPtr(new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(), InheritHandle = 0 }, attributesPointer, false);
            var handle = CreateFile(path, (uint)FileSystemRights.FullControl, 1 /* read sharing only */,
                attributesPointer, 4 /* OPEN_ALWAYS */, 0x02200000 /* OPEN_REPARSE_POINT | BACKUP_SEMANTICS */, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("DiagnosticStoreWriterUnavailable"); }
            try { return new FileStream(handle, FileAccess.ReadWrite, 1); }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(attributesPointer); pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr SecurityDescriptor; internal int InheritHandle; }

    private RootIdentity RequireInstallation()
    {
        _beforeInstallationVerificationForTesting?.Invoke();
        RootIdentity identity;
        try { identity = DiagnosticDirectoryInstallation.Describe(_options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception
                                    or InvalidOperationException or ArgumentException or NotSupportedException)
        { throw new InvalidOperationException("DiagnosticStoreInstallationInvalid", ex); }
        if (!string.Equals(identity.Binding, _installationBinding, StringComparison.Ordinal))
            throw new InvalidOperationException("DiagnosticStoreInstallationInvalid");
        return identity;
    }

    /// <summary>Only complete single-line UTF-8 JSON objects no larger than the record bound are
    /// accepted. The stored bytes always end with exactly one newline.</summary>
    private byte[] ValidateRecord(ReadOnlyMemory<byte> completeUtf8JsonLine)
    {
        var span = completeUtf8JsonLine.Span;
        if (span.Length == 0) throw new ArgumentException("DiagnosticStoreRecordEmpty");
        var newlines = 0;
        foreach (var value in span) if (value == (byte)'\n') newlines++;
        if (newlines > 1 || newlines == 1 && span[^1] != (byte)'\n' || span.IndexOf((byte)'\r') >= 0)
            throw new ArgumentException("DiagnosticStoreRecordMustBeSingleLine");
        var content = newlines == 1 ? span[..^1] : span;
        try { _ = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException ex) { throw new ArgumentException("DiagnosticStoreRecordUtf8Invalid", ex); }
        if (!IsCompleteJsonObject(content))
            throw new ArgumentException("DiagnosticStoreRecordMustBeJsonObject");
        if (content.Length + 1 > _options.MaximumRecordBytes)
            throw new ArgumentException("DiagnosticStoreRecordExceedsBound");
        var record = new byte[content.Length + 1];
        content.CopyTo(record);
        record[^1] = (byte)'\n';
        return record;
    }

    /// <summary>A complete JSON document whose single top-level value is an object; trailing
    /// non-whitespace content, leading scalars and invalid UTF-8 inside strings are rejected.</summary>
    private static bool IsCompleteJsonObject(ReadOnlySpan<byte> content)
    {
        try
        {
            var reader = new Utf8JsonReader(content, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read()) { }
            for (var index = checked((int)reader.BytesConsumed); index < content.Length; index++)
                if (!IsJsonWhitespace(content[index])) return false;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        { return false; }
    }

    private static bool IsJsonWhitespace(byte value) => value is (byte)' ' or (byte)'\t';

    private void Append(ActiveFile file, byte[] record)
    {
        // The active file handle is unshared for write and bound to its recorded identity, so a
        // path-based replacement can never absorb these bytes. No flush-to-disk is claimed.
        file.Stream.Write(record, 0, record.Length);
        file.Length = checked(file.Length + record.Length);
    }

    /// <summary>Verifies that the still-open active file is the same object, at the same path and
    /// inside its sealed append window. A file that disappeared is dropped and replaced by a new
    /// name; a replaced or tampered file fails closed.</summary>
    private void EnsureActiveUsable()
    {
        var file = _active;
        if (file is null) return;
        if (file.Stream.SafeFileHandle.IsClosed || file.Stream.SafeFileHandle.IsInvalid)
        {
            DisposeActive();
            return;
        }
        var information = Information(file.Stream.SafeFileHandle, "DiagnosticStoreActiveFileIdentityChanged");
        if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 || information.Links != 1 ||
            IdentityHash(information) != file.IdentityHash)
            throw new InvalidOperationException("DiagnosticStoreActiveFileIdentityChanged");
        if (file.Stream.Length != file.Length)
            throw new InvalidOperationException("DiagnosticStoreActiveFileLengthChanged");
        if (!DiagnosticDirectoryInstallation.HandlePathMatches(file.Stream.SafeFileHandle, file.Path))
        {
            if (!EntryExists(file.Path)) { DisposeActive(); return; }
            throw new InvalidOperationException("DiagnosticStoreActiveFileReplaced");
        }
        if (!DiagnosticDirectoryInstallation.ValidateRestrictedAcl(file.Path, isDirectory: false))
            throw new InvalidOperationException("DiagnosticStoreActiveFileAclInvalid");
    }

    private ActiveFile CreateActiveFile(long now)
    {
        var expiry = checked(now + _rollMilliseconds + _retentionMilliseconds);
        if (now < 0 || expiry > MaximumNameTimestamp) throw new InvalidOperationException("DiagnosticStoreClockInvalid");
        var name = "diag-" + now.ToString("D13", CultureInfo.InvariantCulture) + "-" +
            expiry.ToString("D13", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".jsonl";
        var path = Path.Combine(_options.Directory, name);
        FileStream stream;
        try
        {
            stream = FileSystemAclExtensions.Create(new FileInfo(path), FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.Read | FileShare.Delete, 1, FileOptions.None,
                DiagnosticDirectoryInstallation.CreateRestrictedFileSecurity());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                    or Win32Exception or ArgumentException)
        { throw new InvalidOperationException("DiagnosticStoreActiveFileCreationFailed", ex); }
        try
        {
            var information = Information(stream.SafeFileHandle, "DiagnosticStoreActiveFileIdentityChanged");
            if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 || information.Links != 1)
                throw new InvalidOperationException("DiagnosticStoreActiveFileIdentityChanged");
            DiagnosticDirectoryInstallation.RequireHandlePath(stream.SafeFileHandle, path, "DiagnosticStoreEntryPathMismatch");
            if (!DiagnosticDirectoryInstallation.ValidateRestrictedAcl(path, isDirectory: false))
                throw new InvalidOperationException("DiagnosticStoreActiveFileAclInvalid");
            return new ActiveFile(path, now, expiry, IdentityHash(information), stream);
        }
        catch { stream.Dispose(); throw; }
    }

    private void DisposeActive()
    {
        var file = _active;
        _active = null;
        if (file is null) return;
        try { file.Stream.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>Bounded metadata inventory: at most <c>MaximumFiles + 1</c> entries, every entry a
    /// regular single-link non-reparse owned file with the restricted ACL. Anything else fails
    /// closed and is never deleted.</summary>
    private List<LogEntry> Inventory()
    {
        var entries = new List<LogEntry>();
        try
        {
        foreach (var path in Directory.EnumerateFileSystemEntries(_options.Directory))
        {
            if (Path.GetFileName(path) == WriterLeaseName && _writerLease is not null) continue;
            if (entries.Count >= _options.MaximumFiles + 1)
                throw new InvalidOperationException("DiagnosticStoreInventoryExceeded");
            var name = Path.GetFileName(path);
            if (!TryParseName(name, out var created, out var expiry))
                throw new InvalidOperationException("DiagnosticStoreInventoryUnknownEntry");
            var expected = Path.Combine(_options.Directory, name);
            if (!string.Equals(expected, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DiagnosticStoreInventoryUnknownEntry");
            entries.Add(ReadEntry(expected, name, created, expiry));
        }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { throw new InvalidOperationException("DiagnosticStoreInventoryUnavailable"); }
        return entries;
    }

    private LogEntry ReadEntry(string path, string name, long created, long expiry)
    {
        using var handle = CreateFile(path, ReadAttributes, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new InvalidOperationException("DiagnosticStoreEntryUnavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        var information = Information(handle, "DiagnosticStoreEntryUnavailable");
        if ((information.Attributes & FileAttributeDirectory) != 0)
            throw new InvalidOperationException("DiagnosticStoreEntryKindInvalid");
        if ((information.Attributes & FileAttributeReparsePoint) != 0)
            throw new InvalidOperationException("DiagnosticStoreEntryReparsePoint");
        if (information.Links != 1) throw new InvalidOperationException("DiagnosticStoreEntryLinksInvalid");
        DiagnosticDirectoryInstallation.RequireHandlePath(handle, path, "DiagnosticStoreEntryPathMismatch");
        if (!DiagnosticDirectoryInstallation.ValidateRestrictedAcl(path, isDirectory: false))
            throw new InvalidOperationException("DiagnosticStoreEntryAclInvalid");
        var length = Length(information);
        if (length > _options.MaximumFileBytes) throw new InvalidOperationException("DiagnosticStoreEntrySizeExceeded");
        return new LogEntry(name, path, created, expiry, length, IdentityHash(information));
    }

    /// <summary>Retires only expired owned files, on the exact opened objects. Retirement never
    /// depends on file-system timestamps and never touches the active file.</summary>
    private void CleanupExpired(List<LogEntry> entries, long now)
    {
        if (_active is not null && _active.ExpiryMilliseconds <= now) DisposeActive();
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (entry.ExpiryMilliseconds > now) continue;
            RetireExpired(entry);
            entries.RemoveAt(index);
        }
    }

    private void RetireExpired(LogEntry entry)
    {
        using (var handle = CreateFile(entry.Path, DeleteAccess | ReadAttributes | Synchronize, 7, IntPtr.Zero, 3,
                   0x02200000, IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new InvalidOperationException("DiagnosticStoreExpiredRetirementUnavailable",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            var information = Information(handle, "DiagnosticStoreExpiredRetirementUnavailable");
            if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 || information.Links != 1)
                throw new InvalidOperationException("DiagnosticStoreExpiredEntryInvalid");
            DiagnosticDirectoryInstallation.RequireHandlePath(handle, entry.Path, "DiagnosticStoreEntryPathMismatch");
            if (IdentityHash(information) != entry.IdentityHash)
                throw new InvalidOperationException("DiagnosticStoreExpiredIdentityChanged");
            if (!DiagnosticDirectoryInstallation.ValidateRestrictedAcl(entry.Path, isDirectory: false))
                throw new InvalidOperationException("DiagnosticStoreExpiredAclInvalid");
            // POSIX disposition removes this exact name even if another read-sharing handle remains;
            // no path-based delete and no early capacity deletion ever occur.
            var disposition = new FileDisposition { Flags = DispositionDeleteWithPosixSemantics };
            if (!SetFileInformationByHandle(handle, DispositionInformationEx, ref disposition, 4))
                throw new IOException("DiagnosticStoreExpiredRetirementFailed", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (EntryExists(entry.Path))
            throw new InvalidOperationException("DiagnosticStoreExpiredRetirementFailed");
    }

    /// <summary>The Storage Reserve is <c>max(bytes, ceil(volumeTotal * percent / 100))</c> and is
    /// checked before every append. An unreadable or full volume refuses.</summary>
    private void EnsureReserve(RootIdentity root, int recordBytes)
    {
        RequireDatabaseVolume(root);
        long total, available;
        try
        {
            var volume = new DriveInfo(Path.GetPathRoot(_options.Directory)!);
            total = volume.TotalSize;
            available = volume.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidOperationException("DiagnosticStoreVolumeUnavailable", ex); }
        if (total <= 0 || available < 0) throw new InvalidOperationException("DiagnosticStoreVolumeUnavailable");
        long reserve;
        try { reserve = decimal.ToInt64(decimal.Ceiling(total * _minimumReservePercent / 100m)); }
        catch (OverflowException) { throw new InvalidOperationException("DiagnosticStoreReserveViolated"); }
        if (reserve < _minimumReserveBytes) reserve = _minimumReserveBytes;
        if (available < recordBytes || available - recordBytes < reserve)
            throw new InvalidOperationException("DiagnosticStoreReserveViolated");
    }

    /// <summary>The diagnostic root must live on the same fixed NTFS volume as the trace database;
    /// the database itself is never opened or read.</summary>
    private void RequireDatabaseVolume(RootIdentity root)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("DiagnosticStoreDatabaseVolumeUnavailable");
        using var handle = CreateFile(directory, ReadAttributes, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new InvalidOperationException("DiagnosticStoreDatabaseVolumeUnavailable");
        var information = Information(handle, "DiagnosticStoreDatabaseVolumeUnavailable");
        if ((information.Attributes & FileAttributeDirectory) == 0 ||
            (information.Attributes & FileAttributeReparsePoint) != 0)
            throw new InvalidOperationException("DiagnosticStoreDatabaseVolumeUnavailable");
        if (information.Volume != root.Volume) throw new InvalidOperationException("DiagnosticStoreVolumeMismatch");
    }

    /// <summary>Reads the newest complete lines of one file that fit the remaining bounds. Scanning
    /// starts no earlier than the bytes that could still fit, the root is handle-protected by the
    /// caller and a torn trailing fragment is dropped.</summary>
    private List<string> ReadTail(LogEntry entry, int maximumRecords, int maximumBytes, CancellationToken token,
        out int usedRecords, out int usedBytes, out bool limited, ref long scanRemaining)
    {
        token.ThrowIfCancellationRequested();
        using var handle = CreateFile(entry.Path, ReadData | ReadEa | ReadAttributes | ReadControl | Synchronize,
            7, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new InvalidOperationException("DiagnosticStoreReadUnavailable", new Win32Exception(Marshal.GetLastWin32Error()));
        var information = Information(handle, "DiagnosticStoreReadUnavailable");
        if ((information.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0 || information.Links != 1)
            throw new InvalidOperationException("DiagnosticStoreReadEntryInvalid");
        DiagnosticDirectoryInstallation.RequireHandlePath(handle, entry.Path, "DiagnosticStoreEntryPathMismatch");
        if (IdentityHash(information) != entry.IdentityHash)
            throw new InvalidOperationException("DiagnosticStoreReadIdentityChanged");
        if (!DiagnosticDirectoryInstallation.ValidateRestrictedAcl(entry.Path, isDirectory: false))
            throw new InvalidOperationException("DiagnosticStoreReadEntryInvalid");
        if (Length(information) != entry.Length)
            throw new InvalidOperationException("DiagnosticStoreReadIdentityChanged");

        using var stream = new FileStream(handle, FileAccess.Read, ReadChunkBytes);
        var window = Math.Max(0L, entry.Length - ((long)maximumBytes + _options.MaximumRecordBytes));
        if (window > 0) stream.Seek(window, SeekOrigin.Begin);
        var buffer = new byte[ReadChunkBytes];
        var pending = new byte[checked((int)Math.Min(_options.MaximumRecordBytes, int.MaxValue))];
        var pendingLength = 0;
        var discarding = window > 0;
        var queue = new Queue<byte[]>();
        var queuedBytes = 0;
        var discardedForBudget = window > 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var allowedRead = (int)Math.Min(buffer.Length, Math.Min(scanRemaining, entry.Length - stream.Position));
            if (allowedRead <= 0) { if (stream.Position < entry.Length) discardedForBudget = true; break; }
            var read = stream.Read(buffer, 0, allowedRead);
            if (read == 0) break;
            scanRemaining -= read;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (discarding)
                {
                    if (value == (byte)'\n') discarding = false;
                    continue;
                }
                if (value == (byte)'\n')
                {
                    if (pendingLength == 0 || pendingLength + 1 > _options.MaximumRecordBytes)
                        throw new InvalidOperationException("DiagnosticStoreRecordInvalid");
                    Enqueue(pending.AsSpan(0, pendingLength).ToArray());
                    pendingLength = 0;
                    continue;
                }
                if (pendingLength >= pending.Length) throw new InvalidOperationException("DiagnosticStoreRecordInvalid");
                pending[pendingLength++] = value;
            }
        }

        var lines = new List<string>(queue.Count);
        foreach (var line in queue)
        {
            try { lines.Add(StrictUtf8.GetString(line)); }
            catch (DecoderFallbackException ex) { throw new InvalidOperationException("DiagnosticStoreRecordInvalid", ex); }
        }
        usedRecords = lines.Count;
        usedBytes = queuedBytes;
        limited = discardedForBudget;
        return lines;

        void Enqueue(byte[] line)
        {
            // Keep the newest records that fit: a newer line displaces the oldest queued one.
            if (line.Length > maximumBytes || maximumRecords == 0)
            { queue.Clear(); queuedBytes = 0; discardedForBudget = true; return; }
            while (queue.Count > 0 && (queuedBytes + line.Length > maximumBytes || queue.Count + 1 > maximumRecords))
            { queuedBytes -= queue.Dequeue().Length; discardedForBudget = true; }
            if (queuedBytes + line.Length > maximumBytes || queue.Count + 1 > maximumRecords) return;
            queue.Enqueue(line);
            queuedBytes += line.Length;
        }
    }

    private bool TryParseName(string name, out long created, out long expiry)
    {
        created = 0; expiry = 0;
        var expectedLength = NamePrefix.Length + 13 + 1 + 13 + 1 + 32 + NameSuffix.Length;
        if (name.Length != expectedLength) return false;
        if (!name.StartsWith("diag-", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return false;
        if (name[18] != '-' || name[32] != '-') return false;
        if (!TryParseFixedTimestamp(name.AsSpan(5, 13), out created)) return false;
        if (!TryParseFixedTimestamp(name.AsSpan(19, 13), out expiry)) return false;
        for (var index = 33; index < 65; index++)
            if (!IsHexDigit(name[index])) return false;
        return expiry == checked(created + _rollMilliseconds + _retentionMilliseconds) && expiry > created;
    }

    private static bool TryParseFixedTimestamp(ReadOnlySpan<char> value, out long result)
    {
        result = 0;
        foreach (var character in value)
        {
            if (character < '0' || character > '9') { result = 0; return false; }
            result = result * 10 + (character - '0');
        }
        return true;
    }

    private long CurrentUnixMilliseconds()
    {
        var value = _clock().ToUnixTimeMilliseconds();
        if (value < 0 || value > MaximumNameTimestamp) throw new InvalidOperationException("DiagnosticStoreClockInvalid");
        return value;
    }

    private static long SumLengths(List<LogEntry> entries)
    {
        var total = 0L;
        foreach (var entry in entries) total = checked(total + entry.Length);
        return total;
    }

    private static long Length(in FileInformation information) =>
        ((long)information.SizeHigh << 32) | information.SizeLow;

    private static string IdentityHash(in FileInformation information) => Convert.ToHexString(SHA256.HashData(
        AuditCanonical.Encode("DiagnosticStoreFileIdentityV1",
            information.Volume.ToString("X8", CultureInfo.InvariantCulture),
            information.IndexHigh.ToString("X8", CultureInfo.InvariantCulture),
            information.IndexLow.ToString("X8", CultureInfo.InvariantCulture),
            information.CreationLow.ToString("X8", CultureInfo.InvariantCulture),
            information.CreationHigh.ToString("X8", CultureInfo.InvariantCulture))));

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        { throw new InvalidOperationException("DiagnosticStoreEntryUnavailable"); }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value) if (!IsHexDigit(character)) return false;
        return true;
    }

    private static bool IsHexDigit(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(LocalDiagnosticStore));
    }

    private sealed class ActiveFile
    {
        internal ActiveFile(string path, long createdMilliseconds, long expiryMilliseconds, string identityHash,
            FileStream stream)
        {
            Path = path;
            CreatedMilliseconds = createdMilliseconds;
            ExpiryMilliseconds = expiryMilliseconds;
            IdentityHash = identityHash;
            Stream = stream;
        }

        internal string Path { get; }
        internal long CreatedMilliseconds { get; }
        internal long ExpiryMilliseconds { get; }
        internal string IdentityHash { get; }
        internal FileStream Stream { get; }
        internal long Length { get; set; }
    }

    private sealed class LogEntry
    {
        internal LogEntry(string name, string path, long createdMilliseconds, long expiryMilliseconds, long length,
            string identityHash)
        {
            Name = name;
            Path = path;
            CreatedMilliseconds = createdMilliseconds;
            ExpiryMilliseconds = expiryMilliseconds;
            Length = length;
            IdentityHash = identityHash;
        }

        internal string Name { get; }
        internal string Path { get; }
        internal long CreatedMilliseconds { get; }
        internal long ExpiryMilliseconds { get; }
        internal long Length { get; }
        internal string IdentityHash { get; }
    }

    /// <summary>Shares the project root-protection pattern: every ancestor directory is held
    /// without FILE_SHARE_DELETE until the whole physical operation is over.</summary>
    private sealed class RootProtection : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;

        internal RootProtection(string root) => _handles = EvidenceQuarantine.ProtectRoots(root);

        public void Dispose()
        {
            foreach (var handle in _handles) handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition
    {
        public uint Flags;
    }

    private static FileInformation Information(SafeFileHandle handle, string reason)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new InvalidOperationException(reason, new Win32Exception(Marshal.GetLastWin32Error()));
        return information;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint size);
}
