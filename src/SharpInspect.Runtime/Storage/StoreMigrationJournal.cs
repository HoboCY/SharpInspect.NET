using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpInspect.Runtime.Storage;

internal sealed record StoreMigrationJournalData
{
    public int FormatVersion { get; init; } = 1;
    public long Sequence { get; init; }
    public string PreviousHash { get; init; } = string.Empty;
    public Guid OperationId { get; init; }
    public int Attempt { get; init; } = 1;
    public StoreMigrationPhase Phase { get; init; }
    public DateTimeOffset RecordedAtUtc { get; init; }
    public string PlanId { get; init; } = "SharpInspect.StoreMigration.32-33.v1";
    public string SystemPrincipalId { get; init; } = SharpInspect.Abstractions.SystemPrincipalId.Runtime;
    public string DatabasePath { get; init; } = string.Empty;
    public int SourceSchemaVersion { get; init; } = 32;
    public int TargetSchemaVersion { get; init; } = 33;
    public string SourceApplicationPath { get; init; } = string.Empty;
    public string SourceApplicationVersion { get; init; } = string.Empty;
    public string SourceApplicationSha256 { get; init; } = string.Empty;
    public string TargetApplicationVersion { get; init; } = string.Empty;
    public string TargetApplicationSha256 { get; init; } = string.Empty;
    public string LifecycleConfigurationHash { get; init; } = string.Empty;
    public string? SourceFingerprint { get; init; }
    public MigrationTableFingerprint[]? SourceTables { get; init; }
    public long? SourceAuditSequence { get; init; }
    public string? SourceAuditHash { get; init; }
    public string? BackupPath { get; init; }
    public DateTimeOffset? BackupCreatedAtUtc { get; init; }
    public long? BackupByteLength { get; init; }
    public string? BackupSha256 { get; init; }
    public bool BackupVerified { get; init; }
    public string? TargetFingerprint { get; init; }
    public long? TargetAuditSequence { get; init; }
    public string? TargetAuditHash { get; init; }
    public bool CommitIntentDurable { get; init; }
    public bool DatabaseCommitObserved { get; init; }
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>
    /// Linked provenance of a chained operation: the completed operation this
    /// operation continues, its exact last journal frame hash and its archived
    /// permanent marker bytes. All three stay null for the first operation of a
    /// journal and are omitted from the canonical payload when null, so every
    /// journal written before a chained generation stays byte-identical.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid? PreviousOperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PreviousOperationJournalHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PreviousMarkerBase64 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ImageEvidenceConfigurationHash { get; init; }
}

internal sealed record StoreMigrationJournalEntry(StoreMigrationJournalData Data, string ContentHash);

/// <summary>
/// A framed, hash-linked external journal. Only a complete frame followed by a
/// successful Flush(true) advances the in-memory head. A partial frame is kept
/// as evidence and is never truncated or interpreted as a completed phase.
/// This is an operational integrity check, not an OS security boundary.
/// </summary>
internal sealed class StoreMigrationJournal : IDisposable
{
    internal const string LifecyclePlanId = "SharpInspect.StoreMigration.32-33.v1";
    internal const string ImageEvidencePlanId = "SharpInspect.StoreMigration.33-34.v1";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SI-MJ01\n");
    private static readonly byte[] HashDomain = Encoding.ASCII.GetBytes("SharpInspect.StoreMigrationJournal.v1\0");
    private const int MaximumRecordBytes = 128 * 1024;
    private const int MaximumRecords = 1024;
    private readonly FileStream _stream;
    private readonly long _maximumBytes;
    private long _verifiedLength;
    private bool _faulted;

    internal StoreMigrationJournal(string path, long maximumBytes, bool create)
    {
        if (maximumBytes < MaximumRecordBytes || maximumBytes > 16L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        _stream = new FileStream(path, create ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        try
        {
            if (create)
            {
                _stream.Write(Magic);
                _stream.Flush(flushToDisk: true);
            }
            _stream.Position = 0;
            Last = ReadCore(_stream, maximumBytes, null);
            _verifiedLength = _stream.Length;
            _stream.Position = _verifiedLength;
        }
        catch { _stream.Dispose(); throw; }
    }

    internal StoreMigrationJournalEntry? Last { get; private set; }

    internal static StoreMigrationJournalEntry? Read(string path, long maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        return ReadCore(stream, maximumBytes, null);
    }

    /// <summary>
    /// Reads the complete framed chain. The caller uses it only to re-prove that a
    /// chained operation follows a completed operation in the same file; the chain is
    /// bounded by the journal byte budget and by the frame count limit.
    /// </summary>
    internal static IReadOnlyList<StoreMigrationJournalEntry> ReadChain(string path, long maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
        var entries = new List<StoreMigrationJournalEntry>();
        _ = ReadCore(stream, maximumBytes, entries);
        return entries;
    }

    internal StoreMigrationJournalEntry Append(StoreMigrationJournalData value)
    {
        if (_faulted || _stream.Length != _verifiedLength)
            throw new InvalidOperationException("StoreMigrationJournalConcurrentChange");
        var data = value with
        {
            Sequence = (Last?.Data.Sequence ?? 0) + 1,
            PreviousHash = Last?.ContentHash ?? new string('0', 64),
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        Validate(data, Last);
        var payload = JsonSerializer.SerializeToUtf8Bytes(data);
        if (payload.Length > MaximumRecordBytes || _verifiedLength + payload.Length + 36 > _maximumBytes)
            throw new InvalidOperationException("StoreMigrationJournalCapacityExceeded");
        var hash = Hash(payload);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        try
        {
            _stream.Write(length);
            _stream.Write(payload);
            _stream.Write(hash);
            _stream.Flush(flushToDisk: true);
        }
        catch { _faulted = true; throw; }
        _verifiedLength = _stream.Position;
        return Last = new StoreMigrationJournalEntry(data, Convert.ToHexString(hash));
    }

    public void Dispose() => _stream.Dispose();

    private static StoreMigrationJournalEntry? ReadCore(Stream stream, long maximumBytes,
        List<StoreMigrationJournalEntry>? chain)
    {
        if (maximumBytes < MaximumRecordBytes || maximumBytes > 16L * 1024 * 1024 ||
            stream.Length < Magic.Length || stream.Length > maximumBytes)
            throw new InvalidOperationException("StoreMigrationJournalLengthInvalid");
        Span<byte> header = stackalloc byte[Magic.Length];
        ReadFully(stream, header);
        if (!header.SequenceEqual(Magic)) throw new InvalidOperationException("StoreMigrationJournalFormatInvalid");
        StoreMigrationJournalEntry? previous = null;
        Span<byte> lengthBytes = stackalloc byte[4];
        Span<byte> storedHash = stackalloc byte[32];
        while (stream.Position < stream.Length)
        {
            ReadFully(stream, lengthBytes);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 1 or > MaximumRecordBytes || stream.Length - stream.Position < length + 32L)
                throw new InvalidOperationException("StoreMigrationJournalIncompleteFrame");
            var payload = new byte[length];
            ReadFully(stream, payload);
            ReadFully(stream, storedHash);
            var actualHash = Hash(payload);
            if (!CryptographicOperations.FixedTimeEquals(storedHash, actualHash))
                throw new InvalidOperationException("StoreMigrationJournalHashMismatch");
            StoreMigrationJournalData? data;
            try { data = JsonSerializer.Deserialize<StoreMigrationJournalData>(payload); }
            catch (JsonException ex) { throw new InvalidOperationException("StoreMigrationJournalPayloadInvalid", ex); }
            if (data is null || !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(data)))
                throw new InvalidOperationException("StoreMigrationJournalPayloadNoncanonical");
            Validate(data, previous);
            previous = new StoreMigrationJournalEntry(data, Convert.ToHexString(actualHash));
            chain?.Add(previous);
        }
        return previous;
    }

    private static void Validate(StoreMigrationJournalData data, StoreMigrationJournalEntry? previous)
    {
        if (data.FormatVersion != 1 || data.OperationId == Guid.Empty || data.Attempt is < 1 or > 64 ||
            data.Sequence != (previous?.Data.Sequence ?? 0) + 1 || data.Sequence > MaximumRecords ||
            data.PreviousHash != (previous?.ContentHash ?? new string('0', 64)) ||
            !Enum.IsDefined(typeof(StoreMigrationPhase), data.Phase) ||
            !IsSupportedPlan(data.SourceSchemaVersion, data.TargetSchemaVersion, data.PlanId) ||
            data.SystemPrincipalId != SharpInspect.Abstractions.SystemPrincipalId.Runtime ||
            data.RecordedAtUtc.Offset != TimeSpan.Zero || data.RecordedAtUtc == default ||
            !RequiredText(data.DatabasePath, 32768) || !Path.IsPathFullyQualified(data.DatabasePath) ||
            !RequiredText(data.SourceApplicationPath, 32768) || !Path.IsPathFullyQualified(data.SourceApplicationPath) ||
            !RequiredText(data.SourceApplicationVersion, 256) || !RequiredText(data.TargetApplicationVersion, 256) ||
            !RequiredText(data.ReasonCode, 256) || !IsHash(data.SourceApplicationSha256) ||
            !IsHash(data.TargetApplicationSha256) || !IsHash(data.LifecycleConfigurationHash))
            throw new InvalidOperationException("StoreMigrationJournalRecordInvalid");
        if (data.TargetSchemaVersion == ProductionImageEvidenceStoreOptions.SchemaVersion
                ? !IsHash(data.ImageEvidenceConfigurationHash)
                : data.ImageEvidenceConfigurationHash is not null)
            throw new InvalidOperationException("StoreMigrationJournalImageConfigurationInvalid");
        if ((data.PreviousOperationId is null) != (data.PreviousOperationJournalHash is null) ||
            (data.PreviousOperationId is null) != (data.PreviousMarkerBase64 is null) ||
            data.PreviousOperationId == Guid.Empty ||
            data.PreviousOperationId == data.OperationId ||
            data.Phase == StoreMigrationPhase.Opened && data.Attempt == 1 &&
                data.PreviousOperationJournalHash is not null &&
                data.PreviousOperationJournalHash != data.PreviousHash ||
            data.PreviousMarkerBase64 is not null && !IsMarker(data.PreviousMarkerBase64))
            throw new InvalidOperationException("StoreMigrationJournalChainLinkInvalid");
        if (data.SourceFingerprint is not null && (!IsHash(data.SourceFingerprint) || data.SourceTables is null ||
            data.SourceTables.Length is < 1 or > 256 || data.SourceAuditSequence is null or < 1 || !IsHash(data.SourceAuditHash)))
            throw new InvalidOperationException("StoreMigrationJournalSourceBindingInvalid");
        if (data.SourceTables is not null && (data.SourceTables.Any(item => item is null) ||
            data.SourceTables.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != data.SourceTables.Length ||
            data.SourceTables.Any(item => !RequiredText(item.Name, 256) || item.RowCount < 0 ||
                (item.RowCount == 0) != (item.MaximumRowId is null) || !IsHash(item.ContentHash))))
            throw new InvalidOperationException("StoreMigrationJournalSourceTablesInvalid");
        if (data.BackupVerified && (data.SourceFingerprint is null || !IsHash(data.BackupSha256) ||
            data.BackupByteLength is null or < 1 || data.BackupCreatedAtUtc is null ||
            !RequiredText(data.BackupPath, 32768) || !Path.IsPathFullyQualified(data.BackupPath!)))
            throw new InvalidOperationException("StoreMigrationJournalBackupBindingInvalid");
        if (data.TargetFingerprint is not null && (!IsHash(data.TargetFingerprint) ||
            data.TargetAuditSequence is null || data.SourceAuditSequence is null ||
            data.TargetAuditSequence <= data.SourceAuditSequence || !IsHash(data.TargetAuditHash) || !data.BackupVerified))
            throw new InvalidOperationException("StoreMigrationJournalTargetBindingInvalid");
        if (data.Phase is >= StoreMigrationPhase.SourceVerified and <= StoreMigrationPhase.Completed &&
            data.SourceFingerprint is null)
            throw new InvalidOperationException("StoreMigrationJournalSourceProofMissing");
        if (data.Phase is >= StoreMigrationPhase.BackupVerified and <= StoreMigrationPhase.Completed && !data.BackupVerified)
            throw new InvalidOperationException("StoreMigrationJournalBackupProofMissing");
        if (data.Phase is >= StoreMigrationPhase.TargetVerified and <= StoreMigrationPhase.Completed && data.TargetFingerprint is null)
            throw new InvalidOperationException("StoreMigrationJournalTargetProofMissing");
        if (data.Phase is >= StoreMigrationPhase.CommitIntent and <= StoreMigrationPhase.Completed &&
            !data.CommitIntentDurable)
            throw new InvalidOperationException("StoreMigrationJournalCommitProofMissing");
        if (data.CommitIntentDurable && data.Phase < StoreMigrationPhase.CommitIntent)
            throw new InvalidOperationException("StoreMigrationJournalCommitPhaseInvalid");
        if (data.DatabaseCommitObserved && (!data.CommitIntentDurable || data.Phase < StoreMigrationPhase.DatabaseCommitted) ||
            data.Phase is StoreMigrationPhase.DatabaseCommitted or StoreMigrationPhase.Completed && !data.DatabaseCommitObserved)
            throw new InvalidOperationException("StoreMigrationJournalDatabaseCommitProofInvalid");
        if (previous is null)
        {
            if (data.Phase != StoreMigrationPhase.Opened || data.Attempt != 1)
                throw new InvalidOperationException("StoreMigrationJournalInitialPhaseInvalid");
            return;
        }
        var old = previous.Data;
        // A chained operation continues one completed operation of the same journal
        // file: its provenance fields bind the exact predecessor frame and its
        // archived marker bytes, and it becomes the new operation instead of a
        // successor phase of the old one.
        if (data.Attempt == 1 && data.Phase == StoreMigrationPhase.Opened &&
            old.Phase == StoreMigrationPhase.Completed &&
            data.SourceSchemaVersion == old.TargetSchemaVersion &&
            data.OperationId != old.OperationId && data.PreviousOperationId == old.OperationId &&
            data.PreviousOperationJournalHash == previous.ContentHash && data.PreviousMarkerBase64 is not null)
            return;
        if (old.SourceFingerprint is not null && (data.SourceAuditSequence != old.SourceAuditSequence ||
            data.SourceAuditHash != old.SourceAuditHash || data.SourceTables is null ||
            !data.SourceTables.SequenceEqual(old.SourceTables!)))
            throw new InvalidOperationException("StoreMigrationJournalSourceProofChanged");
        if (old.TargetFingerprint is not null && data.Attempt == old.Attempt &&
            (data.TargetAuditSequence != old.TargetAuditSequence || data.TargetAuditHash != old.TargetAuditHash))
            throw new InvalidOperationException("StoreMigrationJournalTargetProofChanged");
        if (old.BackupVerified && data.Attempt == old.Attempt && (!data.BackupVerified ||
            data.BackupPath != old.BackupPath || data.BackupCreatedAtUtc != old.BackupCreatedAtUtc ||
            data.BackupByteLength != old.BackupByteLength || data.BackupSha256 != old.BackupSha256))
            throw new InvalidOperationException("StoreMigrationJournalBackupProofChanged");
        if (old.Phase == StoreMigrationPhase.Completed || data.OperationId != old.OperationId ||
            data.DatabasePath != old.DatabasePath || data.SourceApplicationPath != old.SourceApplicationPath ||
            data.SourceApplicationVersion != old.SourceApplicationVersion || data.SourceApplicationSha256 != old.SourceApplicationSha256 ||
            data.TargetApplicationVersion != old.TargetApplicationVersion || data.TargetApplicationSha256 != old.TargetApplicationSha256 ||
            data.LifecycleConfigurationHash != old.LifecycleConfigurationHash ||
            data.ImageEvidenceConfigurationHash != old.ImageEvidenceConfigurationHash ||
            old.SourceFingerprint is not null && data.SourceFingerprint != old.SourceFingerprint ||
            old.TargetFingerprint is not null && data.Attempt == old.Attempt && data.TargetFingerprint != old.TargetFingerprint ||
            old.CommitIntentDurable && data.Attempt == old.Attempt && !data.CommitIntentDurable ||
            old.DatabaseCommitObserved && !data.DatabaseCommitObserved)
            throw new InvalidOperationException("StoreMigrationJournalContextChanged");
        var next = data.Attempt == old.Attempt && (int)data.Phase == (int)old.Phase + 1;
        var failure = data.Attempt == old.Attempt && data.Phase == StoreMigrationPhase.MaintenanceRequired;
        var restartSource = data.Attempt == old.Attempt + 1 && data.Phase == StoreMigrationPhase.SourceVerified &&
            data.SourceFingerprint is not null && data.TargetFingerprint is null && !data.CommitIntentDurable;
        var recoverCommit = data.Attempt == old.Attempt && old.CommitIntentDurable &&
            old.Phase is StoreMigrationPhase.CommitIntent or StoreMigrationPhase.MaintenanceRequired &&
            data.Phase == StoreMigrationPhase.DatabaseCommitted && data.TargetFingerprint == old.TargetFingerprint;
        if (!next && !failure && !restartSource && !recoverCommit)
            throw new InvalidOperationException("StoreMigrationJournalPhaseInvalid");
    }

    private static bool RequiredText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value.IndexOf('\0') < 0;

    private static bool IsSupportedPlan(int sourceSchemaVersion, int targetSchemaVersion, string planId) =>
        sourceSchemaVersion == 32 && targetSchemaVersion == RecipeLifecycleStoreOptions.SchemaVersion &&
            planId == LifecyclePlanId ||
        sourceSchemaVersion == RecipeLifecycleStoreOptions.SchemaVersion &&
            targetSchemaVersion == ProductionImageEvidenceStoreOptions.SchemaVersion &&
            planId == ImageEvidencePlanId;

    private static bool IsMarker(string? value)
    {
        if (value is null || value.Length > 256) return false;
        try
        {
            // Marker format: the 8-byte magic, the 16-byte operation id and the 32-byte
            // path binding hash. The full binding is re-proved by the journal guard.
            return value.Length % 4 == 0 && Convert.FromBase64String(value).Length ==
                StoreMigrationJournalGuard.MarkerByteLength;
        }
        catch (FormatException) { return false; }
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static byte[] Hash(ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(HashDomain);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    private static void ReadFully(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0) throw new InvalidOperationException("StoreMigrationJournalIncompleteFrame");
            read += count;
        }
    }
}
