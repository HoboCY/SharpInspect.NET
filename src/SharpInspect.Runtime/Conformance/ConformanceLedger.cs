using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Conformance;

/// <summary>Configuration for the isolated, append-only conformance evidence ledger.</summary>
public sealed class ConformanceLedgerOptions
{
    public ConformanceLedgerOptions(string databasePath, string keyDirectory, string signingKeyName)
    {
        DatabasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        KeyDirectory = keyDirectory ?? throw new ArgumentNullException(nameof(keyDirectory));
        SigningKeyName = signingKeyName ?? throw new ArgumentNullException(nameof(signingKeyName));
    }

    public string DatabasePath { get; }
    public string KeyDirectory { get; }
    public string SigningKeyName { get; }
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxEntries { get; init; } = 10_000;
    public long MaxTotalBytes { get; init; } = 128L * 1024 * 1024;

    // Descriptive aliases keep the limits unambiguous for callers while the short names
    // remain the stable construction surface used by the conformance runner.
    public int MaximumEntries => MaxEntries;
    public long MaximumTotalBytes => MaxTotalBytes;
}

internal sealed record ConformanceLedgerItem(string Kind, string Id, string Payload);

internal sealed record ConformanceLedgerEntry(long Sequence, string Kind, string Id, string Payload,
    string Sha256, string PreviousHash);

/// <summary>
/// A separate local evidence store. It is intentionally not an implementation of the
/// production trace store and exposes no update, delete, import, or overwrite operation.
/// </summary>
internal sealed class ConformanceLedger : IDisposable
{
    private const int ApplicationId = 0x53434C31; // "SCL1"
    private const int SchemaVersion = 1;
    private const int MaximumBatchEntries = 20;
    private const int MaximumKindLength = 32;
    private const int MaximumIdLength = 128;
    private const int MaximumPayloadBytes = 48 * 1024;
    private const int MaximumArtifactBytes = 32 * 1024;
    private const int MaximumAnchorBytes = 16 * 1024;
    private const string LedgerStationId = "SharpInspect.ConformanceLedger";
    private const string AnchorSuffix = ".anchor";
    private const string LockSuffix = ".lock";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedKinds = new(StringComparer.Ordinal)
    {
        "profile", "candidate", "context", "reservation", "result", "artifact"
    };

    private readonly object _sync = new();
    private readonly ConformanceLedgerOptions _options;
    private readonly bool _readOnly;
    private readonly string _databasePath;
    private readonly string _anchorPath;
    private readonly string _lockPath;
    private readonly FileStream? _writerLock;
    private readonly WindowsMachineAuditKey? _signingKey;
    private readonly SqliteConnection? _connection;
    private string _ledgerId = string.Empty;
    private bool _blocked;
    private bool _disposed;

    /// <summary>Internal synchronization seam for the read-snapshot race test.</summary>
    internal Action? AfterReadSnapshot { get; set; }

    internal ConformanceLedger(ConformanceLedgerOptions options, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _readOnly = readOnly;

        var pathOptions = new ProductionStoreOptions(options.DatabasePath)
        {
            CommitTimeout = options.OperationTimeout,
            QueryTimeout = options.OperationTimeout,
            QueueCapacity = 1
        };
        if (!StoragePathValidator.TryValidate(pathOptions, out var databasePath, out var pathReason))
            throw new InvalidOperationException(pathReason);
        if (Directory.Exists(databasePath))
            throw new InvalidOperationException("ConformanceLedgerDatabasePathInvalid");

        _databasePath = databasePath;
        _anchorPath = databasePath + AnchorSuffix;
        _lockPath = databasePath + LockSuffix;
        var databaseExisted = File.Exists(databasePath);
        ValidateAnchorPath(_anchorPath, options.OperationTimeout);
        if (!databaseExisted && (File.Exists(_anchorPath) || Directory.Exists(_anchorPath)))
            throw new InvalidOperationException("ConformanceLedgerOrphanedAnchor");

        try
        {
            if (!readOnly)
            {
                _writerLock = OpenWriterLock(_lockPath);
                // A key may be created only while a brand-new ledger database is being
                // provisioned. An existing database never repairs or replaces its key.
                _signingKey = WindowsMachineAuditKey.Open(CreateKeyPolicy(options, !databaseExisted),
                    !databaseExisted, out _);
            }
            else
            {
                _signingKey = WindowsMachineAuditKey.Open(CreateKeyPolicy(options, allowCreation: false),
                    allowCreation: false, out _);
            }

            _connection = SqliteNative.Open(databasePath, readOnly);
            var deadline = NewDeadline();
            ConfigureConnection(_connection.Handle!, deadline, readOnly, databaseExisted);
            if (databaseExisted)
            {
                ValidateExistingDatabase(_connection.Handle!, deadline);
                VerifyHead(_connection.Handle!, deadline);
                if (!readOnly)
                    SqliteNative.Execute(_connection.Handle!, "PRAGMA query_only=OFF;", deadline);
            }
            else
            {
                if (readOnly) throw new InvalidOperationException("ConformanceLedgerMissing");
                _ledgerId = Guid.NewGuid().ToString("D");
                InitializeDatabase(_connection.Handle!, deadline);
                WriteAnchor(0, AuditCanonical.GenesisHash, deadline);
            }
        }
        catch
        {
            _connection?.Dispose();
            _signingKey?.Dispose();
            _writerLock?.Dispose();
            throw;
        }
    }

    /// <summary>Path used by the external head. It is internal for focused fault-injection tests.</summary>
    internal string AnchorPath => _anchorPath;

    internal void Append(IReadOnlyList<ConformanceLedgerItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var deadline = NewDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        if (items.Count == 0) throw new ArgumentException("ConformanceLedgerBatchEmpty", nameof(items));
        if (items.Count > MaximumBatchEntries)
            throw new ArgumentException("ConformanceLedgerBatchCapacityExceeded", nameof(items));

        var copied = items.ToArray();
        var batchBytes = ValidateItems(copied);
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var lockTaken = false;
        try
        {
            EnterSync(deadline, cancellationToken, ref lockTaken);
            EnsureUsable();
            if (_readOnly) throw new InvalidOperationException("ConformanceLedgerReadOnly");
            cancellationToken.ThrowIfCancellationRequested();
            var database = _connection!.Handle!;
            var committed = false;
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline, cancellationToken);
            try
            {
                ValidateDatabaseIdentity(database, deadline);
                ValidateSchemaDefinitions(database, deadline);
                ValidateMetadata(database, deadline, cancellationToken);
                var current = ReadAndVerifyChain(database, deadline, cancellationToken);
                try
                {
                    EnsureAnchor(current.Sequence, current.HeadHash, deadline);
                }
                catch (AnchorMismatchException mismatch)
                {
                    throw new InvalidOperationException(mismatch.Message, mismatch);
                }
                if (current.Sequence > _options.MaxEntries - copied.Length)
                    throw new InvalidOperationException("ConformanceLedgerEntryCapacityExceeded");
                if (current.TotalPayloadBytes > _options.MaxTotalBytes - batchBytes)
                    throw new InvalidOperationException("ConformanceLedgerByteCapacityExceeded");

                var sequence = current.Sequence;
                var previousHash = current.HeadHash;
                foreach (var item in copied)
                {
                    if (Exists(database, item.Kind, item.Id, deadline, cancellationToken))
                        throw new InvalidOperationException("ConformanceLedgerDuplicateId");

                    sequence = checked(sequence + 1);
                    var payloadBytes = StrictUtf8.GetBytes(item.Payload);
                    var payloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
                    var chainHash = ComputeChainHash(sequence, item.Kind, item.Id, payloadHash, previousHash);
                    Insert(database, sequence, item, payloadHash, previousHash, deadline, cancellationToken);
                    previousHash = chainHash;
                }

                SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                committed = true;
                try
                {
                    // Once SQLite has committed, cancellation cannot abandon the external
                    // head update. A failed head write makes this ledger evidence blocked.
                    WriteAnchor(sequence, previousHash, deadline);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _blocked = true;
                    throw new InvalidOperationException("ConformanceLedgerAnchorWriteFailed", ex);
                }
            }
            finally
            {
                if (!committed) Rollback(database);
            }
        }
        finally
        {
            if (lockTaken) Monitor.Exit(_sync);
        }
    }

    internal IReadOnlyList<ConformanceLedgerEntry> ReadAll(CancellationToken cancellationToken = default)
    {
        var deadline = NewDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        var lockTaken = false;
        try
        {
            EnterSync(deadline, cancellationToken, ref lockTaken);
            EnsureUsable();
            var database = _connection!.Handle!;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var current = ReadSnapshot(database, deadline, cancellationToken);
                AfterReadSnapshot?.Invoke();
                try
                {
                    EnsureAnchor(current.Sequence, current.HeadHash, deadline);
                    return current.Entries;
                }
                catch (AnchorMismatchException mismatch) when
                    (attempt == 0 && mismatch.ActualSequence > current.Sequence)
                {
                    // A writer may have committed and advanced the external head after
                    // this read transaction closed. Re-read once under the same deadline;
                    // an older or malformed head remains fail-closed.
                }
                catch (AnchorMismatchException mismatch)
                {
                    throw new InvalidOperationException(mismatch.Message, mismatch);
                }
            }

            throw new InvalidOperationException("ConformanceLedgerAnchorMismatch");
        }
        finally
        {
            if (lockTaken) Monitor.Exit(_sync);
        }
    }

    public void Dispose()
    {
        var lockTaken = false;
        try
        {
            EnterSync(NewDeadline(), CancellationToken.None, ref lockTaken);
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _signingKey?.Dispose();
            _writerLock?.Dispose();
        }
        finally
        {
            if (lockTaken) Monitor.Exit(_sync);
        }
    }

    private static void ValidateOptions(ConformanceLedgerOptions options)
    {
        if (options.OperationTimeout < TimeSpan.FromMilliseconds(1) ||
            options.OperationTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options.OperationTimeout));
        if (options.MaxEntries is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(options.MaxEntries));
        if (options.MaxTotalBytes is < 1 or > 128L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTotalBytes));
    }

    private static AuditIntegrityPolicy CreateKeyPolicy(ConformanceLedgerOptions options, bool allowCreation) =>
        new(LedgerStationId, "v1", options.SigningKeyName)
        {
            KeyDirectory = options.KeyDirectory,
            AllowInitialKeyCreation = allowCreation
        };

    private static void ValidateAnchorPath(string anchorPath, TimeSpan timeout)
    {
        if (Directory.Exists(anchorPath))
            throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
        var pathOptions = new ProductionStoreOptions(anchorPath)
        {
            CommitTimeout = timeout,
            QueryTimeout = timeout,
            QueueCapacity = 1
        };
        if (!StoragePathValidator.TryValidate(pathOptions, out _, out var reason))
            throw new InvalidOperationException(reason);
    }

    private static FileStream OpenWriterLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.WriteThrough);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("ConformanceLedgerWriterBusy", ex);
        }
    }

    private StoreDeadline NewDeadline() => new(_options.OperationTimeout);

    private void EnterSync(StoreDeadline deadline, CancellationToken cancellationToken, ref bool lockTaken)
    {
        while (!lockTaken)
        {
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var milliseconds = (int)Math.Clamp(Math.Ceiling(deadline.Remaining.TotalMilliseconds), 1, 100);
            lockTaken = Monitor.TryEnter(_sync, milliseconds);
        }
    }

    private void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConformanceLedger));
        if (_blocked) throw new InvalidOperationException("ConformanceLedgerBlocked");
        if (_connection is null || _signingKey is null) throw new InvalidOperationException("ConformanceLedgerUnavailable");
    }

    private void ConfigureConnection(SQLitePCL.sqlite3 database, StoreDeadline deadline,
        bool readOnly, bool databaseExisted)
    {
        if (!readOnly && !databaseExisted)
        {
            SqliteNative.Execute(database,
                $"PRAGMA application_id={ApplicationId}; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;",
                deadline);
        }
        else
        {
            SqliteNative.Execute(database, "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;", deadline);
            var journal = ReadText(database, "PRAGMA journal_mode;", deadline);
            if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ConformanceLedgerJournalModeInvalid");
            if (ReadInt64(database, "PRAGMA synchronous;", deadline) != 2)
                throw new InvalidOperationException("ConformanceLedgerSynchronousModeInvalid");
        }
    }

    private void InitializeDatabase(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        var committed = false;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        try
        {
            SqliteNative.Execute(database, SchemaSql, deadline);
            InsertMetadata(database, deadline);
            SqliteNative.Execute(database, $"PRAGMA user_version={SchemaVersion}; COMMIT;", deadline);
            committed = true;
        }
        finally
        {
            if (!committed) Rollback(database);
        }
    }

    private void ValidateExistingDatabase(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        ValidateDatabaseIdentity(database, deadline);
        ValidateSchemaDefinitions(database, deadline);
        ValidateMetadata(database, deadline);
    }

    private static void ValidateDatabaseIdentity(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        if (ReadInt64(database, "PRAGMA application_id;", deadline) != ApplicationId)
            throw new InvalidOperationException("ConformanceLedgerApplicationIdMismatch");
        if (ReadInt64(database, "PRAGMA user_version;", deadline) != SchemaVersion)
            throw new InvalidOperationException("ConformanceLedgerSchemaVersionUnsupported");
    }

    private static void ValidateSchemaDefinitions(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        var definitions = ReadSchemaDefinitions(database, deadline);
        var expected = new HashSet<string>(new[]
        {
            "table:conformance_ledger_meta", "table:conformance_ledger_entries",
            "index:ix_conformance_ledger_kind_id",
            "trigger:conformance_ledger_entries_immutable_update",
            "trigger:conformance_ledger_entries_immutable_delete",
            "trigger:conformance_ledger_meta_immutable_update",
            "trigger:conformance_ledger_meta_immutable_delete"
        }, StringComparer.Ordinal);
        if (!definitions.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidOperationException("ConformanceLedgerSchemaMismatch");
        foreach (var definition in definitions)
        {
            if (!SchemaDefinitionMatches(definition.Key, definition.Value))
                throw new InvalidOperationException("ConformanceLedgerSchemaMismatch");
        }
        if (!definitions["table:conformance_ledger_meta"].Contains("KeyId TEXT NOT NULL", StringComparison.Ordinal) ||
            !definitions["table:conformance_ledger_meta"].Contains("LedgerId TEXT NOT NULL", StringComparison.Ordinal) ||
            !definitions["table:conformance_ledger_entries"].Contains("UNIQUE(Kind,Id)", StringComparison.Ordinal))
            throw new InvalidOperationException("ConformanceLedgerSchemaMismatch");

    }

    private static bool SchemaDefinitionMatches(string name, string actual)
    {
        var expected = name switch
        {
            "table:conformance_ledger_meta" =>
                "CREATE TABLE conformance_ledger_meta(Id INTEGER PRIMARY KEY CHECK(Id=1), LedgerId TEXT NOT NULL, " +
                "KeyId TEXT NOT NULL, PublicKey TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL)",
            "table:conformance_ledger_entries" =>
                "CREATE TABLE conformance_ledger_entries(Sequence INTEGER NOT NULL PRIMARY KEY CHECK(Sequence>0), " +
                "Kind TEXT NOT NULL, Id TEXT NOT NULL, Payload TEXT NOT NULL, Sha256 TEXT NOT NULL CHECK(length(Sha256)=64), " +
                "PreviousHash TEXT NOT NULL CHECK(length(PreviousHash)=64), UNIQUE(Kind,Id))",
            "index:ix_conformance_ledger_kind_id" =>
                "CREATE INDEX ix_conformance_ledger_kind_id ON conformance_ledger_entries(Kind,Id)",
            "trigger:conformance_ledger_entries_immutable_update" =>
                "CREATE TRIGGER conformance_ledger_entries_immutable_update BEFORE UPDATE ON conformance_ledger_entries BEGIN " +
                "SELECT RAISE(ABORT,'ConformanceLedgerImmutable'); END",
            "trigger:conformance_ledger_entries_immutable_delete" =>
                "CREATE TRIGGER conformance_ledger_entries_immutable_delete BEFORE DELETE ON conformance_ledger_entries BEGIN " +
                "SELECT RAISE(ABORT,'ConformanceLedgerImmutable'); END",
            "trigger:conformance_ledger_meta_immutable_update" =>
                "CREATE TRIGGER conformance_ledger_meta_immutable_update BEFORE UPDATE ON conformance_ledger_meta BEGIN " +
                "SELECT RAISE(ABORT,'ConformanceLedgerImmutable'); END",
            "trigger:conformance_ledger_meta_immutable_delete" =>
                "CREATE TRIGGER conformance_ledger_meta_immutable_delete BEFORE DELETE ON conformance_ledger_meta BEGIN " +
                "SELECT RAISE(ABORT,'ConformanceLedgerImmutable'); END",
            _ => null
        };
        return expected is not null && string.Equals(NormalizeSql(actual), NormalizeSql(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSql(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private void ValidateMetadata(SQLitePCL.sqlite3 database, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        if (ReadInt64(database, "SELECT COUNT(*) FROM conformance_ledger_meta;", deadline) != 1)
            throw new InvalidOperationException("ConformanceLedgerMetadataInvalid");
        var metadata = SqliteNative.WithStatement(database,
            "SELECT LedgerId,KeyId,PublicKey FROM conformance_ledger_meta WHERE Id=1;", deadline,
            statement =>
            {
                if (SqliteNative.Step(database, statement, deadline, cancellationToken) != SQLitePCL.raw.SQLITE_ROW)
                    return (LedgerId: (string?)null, KeyId: (string?)null, PublicKey: (string?)null);
                return (LedgerId: SqliteNative.ColumnText(statement, 0),
                    KeyId: SqliteNative.ColumnText(statement, 1),
                    PublicKey: SqliteNative.ColumnText(statement, 2));
            }, cancellationToken);
        if (!Guid.TryParseExact(metadata.LedgerId, "D", out var ledgerId) || ledgerId == Guid.Empty ||
            metadata.KeyId != _signingKey!.KeyId || metadata.PublicKey != _signingKey.PublicKeyBase64 ||
            (_ledgerId.Length > 0 && !string.Equals(metadata.LedgerId, _ledgerId, StringComparison.Ordinal)))
            throw new InvalidOperationException("ConformanceLedgerSigningKeyMismatch");
        _ledgerId = ledgerId.ToString("D");
    }

    private void InsertMetadata(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        SqliteNative.WithStatement(database,
            "INSERT INTO conformance_ledger_meta(Id,LedgerId,KeyId,PublicKey,CreatedAtUtc) VALUES(1,?,?,?,?);",
            deadline, statement =>
            {
                SqliteNative.BindText(database, statement, 1, _ledgerId);
                SqliteNative.BindText(database, statement, 2, _signingKey!.KeyId);
                SqliteNative.BindText(database, statement, 3, _signingKey.PublicKeyBase64);
                SqliteNative.BindText(database, statement, 4, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                SqliteNative.Step(database, statement, deadline);
                return 0;
            });
    }

    private (long Sequence, string HeadHash, long TotalPayloadBytes, IReadOnlyList<ConformanceLedgerEntry> Entries)
        ReadAndVerifyChain(SQLitePCL.sqlite3 database, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var entries = new List<ConformanceLedgerEntry>();
        var previousHash = AuditCanonical.GenesisHash;
        var expectedSequence = 1L;
        long totalPayloadBytes = 0;
        SqliteNative.WithStatement(database,
            "SELECT Sequence,Kind,Id,Payload,Sha256,PreviousHash FROM conformance_ledger_entries ORDER BY Sequence;",
            deadline, statement =>
            {
                while (SqliteNative.Step(database, statement, deadline, cancellationToken) == SQLitePCL.raw.SQLITE_ROW)
                {
                    var sequence = SqliteNative.ColumnInt64(statement, 0);
                    var kind = SqliteNative.ColumnText(statement, 1);
                    var id = SqliteNative.ColumnText(statement, 2);
                    var payload = SqliteNative.ColumnText(statement, 3);
                    var sha256 = SqliteNative.ColumnText(statement, 4);
                    var storedPrevious = SqliteNative.ColumnText(statement, 5);
                    if (sequence != expectedSequence || kind is null || id is null || payload is null ||
                        sha256 is null || storedPrevious is null)
                        throw new InvalidOperationException("ConformanceLedgerChainInvalid");
                    var payloadBytes = ValidateStoredItem(kind, id, payload);
                    var expectedPayloadHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
                    if (!string.Equals(sha256, expectedPayloadHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("ConformanceLedgerPayloadHashMismatch");
                    if (!string.Equals(storedPrevious, previousHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("ConformanceLedgerPreviousHashMismatch");
                    var chainHash = ComputeChainHash(sequence, kind, id, sha256, previousHash);
                    entries.Add(new ConformanceLedgerEntry(sequence, kind, id, payload, sha256, storedPrevious));
                    previousHash = chainHash;
                    expectedSequence = checked(expectedSequence + 1);
                    totalPayloadBytes = checked(totalPayloadBytes + payloadBytes.LongLength);
                    if (entries.Count > _options.MaxEntries || totalPayloadBytes > _options.MaxTotalBytes)
                        throw new InvalidOperationException("ConformanceLedgerCapacityExceeded");
                }
                return 0;
            });

        return (entries.Count, previousHash, totalPayloadBytes, entries.AsReadOnly());
    }

    private void VerifyHead(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = ReadSnapshot(database, deadline, CancellationToken.None);
            try
            {
                EnsureAnchor(current.Sequence, current.HeadHash, deadline);
                return;
            }
            catch (AnchorMismatchException mismatch)
                when (attempt == 0 && mismatch.ActualSequence > current.Sequence)
            {
                // A writer may have advanced the head after this constructor's
                // read-only snapshot closed. Re-open once under the same deadline.
            }
            catch (AnchorMismatchException mismatch)
            {
                throw new InvalidOperationException(mismatch.Message, mismatch);
            }
        }

        throw new InvalidOperationException("ConformanceLedgerAnchorMismatch");
    }

    private void EnsureAnchor(long sequence, string headHash, StoreDeadline? deadline = null)
    {
        if (deadline is not null) SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
        if (!File.Exists(_anchorPath)) throw new InvalidOperationException("ConformanceLedgerAnchorMissing");
        ValidateAnchorPath(_anchorPath, _options.OperationTimeout);
        AnchorDocument anchor;
        try
        {
            anchor = ReadAnchorDocument(_anchorPath, deadline);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("ConformanceLedgerAnchorUnavailable", ex);
        }

        if (anchor.Sequence < 0 || !Guid.TryParseExact(anchor.LedgerId, "D", out var ledgerId) || ledgerId == Guid.Empty ||
            !string.Equals(anchor.LedgerId, _ledgerId, StringComparison.Ordinal) || !AuditCanonical.IsHash(anchor.HeadHash) ||
            !string.Equals(anchor.KeyId, _signingKey!.KeyId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(anchor.Signature))
            throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
        if (!VerifySignature(anchor)) throw new InvalidOperationException("ConformanceLedgerAnchorSignatureInvalid");
        if (anchor.Sequence != sequence || !string.Equals(anchor.HeadHash, headHash, StringComparison.Ordinal))
            throw new AnchorMismatchException(anchor.Sequence, anchor.HeadHash);
        if (deadline is not null) SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
    }

    private bool VerifySignature(AnchorDocument anchor)
    {
        try
        {
            using var verifier = ECDsa.Create();
            var publicKey = Convert.FromBase64String(_signingKey!.PublicKeyBase64);
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length) return false;
            var signature = Convert.FromBase64String(anchor.Signature);
            if (signature.Length != 64) return false;
            return verifier.VerifyData(AnchorBytes(anchor.LedgerId, anchor.Sequence, anchor.HeadHash, anchor.KeyId),
                signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static AnchorDocument ReadAnchorDocument(string path, StoreDeadline? deadline)
    {
        if (deadline is not null) SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumAnchorBytes)
                throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
            bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
                offset += read;
            }
            if (stream.Length != bytes.LongLength)
                throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
            if (deadline is not null) SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("ConformanceLedgerAnchorUnavailable", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? ledgerId = null;
            long sequence = 0;
            var hasSequence = false;
            string? headHash = null;
            string? keyId = null;
            string? signature = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
                switch (property.Name)
                {
                    case "LedgerId" when property.Value.ValueKind == JsonValueKind.String:
                        ledgerId = property.Value.GetString();
                        break;
                    case "Sequence" when property.Value.ValueKind == JsonValueKind.Number &&
                        property.Value.TryGetInt64(out sequence):
                        hasSequence = true;
                        break;
                    case "HeadHash" when property.Value.ValueKind == JsonValueKind.String:
                        headHash = property.Value.GetString();
                        break;
                    case "KeyId" when property.Value.ValueKind == JsonValueKind.String:
                        keyId = property.Value.GetString();
                        break;
                    case "Signature" when property.Value.ValueKind == JsonValueKind.String:
                        signature = property.Value.GetString();
                        break;
                    default:
                        throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
                }
            }

            if (!seen.SetEquals(new[] { "LedgerId", "Sequence", "HeadHash", "KeyId", "Signature" }) ||
                !hasSequence || ledgerId is null || headHash is null || keyId is null || signature is null)
                throw new InvalidOperationException("ConformanceLedgerAnchorInvalid");
            return new AnchorDocument(ledgerId, sequence, headHash, keyId, signature);
        }
        catch (InvalidOperationException) { throw; }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("ConformanceLedgerAnchorInvalid", ex);
        }
    }

    private void WriteAnchor(long sequence, string headHash, StoreDeadline? deadline = null)
    {
        if (deadline is not null) SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
        ValidateAnchorPath(_anchorPath, _options.OperationTimeout);
        var document = new AnchorDocument(_ledgerId, sequence, headHash, _signingKey!.KeyId,
            Convert.ToBase64String(_signingKey.Sign(AnchorBytes(_ledgerId, sequence, headHash, _signingKey.KeyId))));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > MaximumAnchorBytes) throw new InvalidOperationException("ConformanceLedgerAnchorOversized");

        var temporaryPath = _anchorPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (deadline is not null) SqliteNative.EnsureDeadline(deadline, CancellationToken.None);

            if (File.Exists(_anchorPath)) ReplaceAnchor(temporaryPath, _anchorPath, bytes, deadline);
            else File.Move(temporaryPath, _anchorPath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReplaceAnchor(string temporaryPath, string anchorPath, byte[] expectedBytes,
        StoreDeadline? deadline)
    {
        while (true)
        {
            if (deadline is not null)
                SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            try
            {
                // Keep the same prepared, signed temporary file for every attempt. A retry
                // may wait for a transient reader to release the existing external head,
                // but it must never re-sign or replay the committed ledger transaction.
                File.Replace(temporaryPath, anchorPath, null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException exception) when
                (IsTransientAnchorReplaceFailure(exception) &&
                 AnchorFilesStillMatch(temporaryPath, anchorPath, expectedBytes))
            {
                if (deadline is null || deadline.Remaining <= TimeSpan.Zero)
                    throw;

                var delay = TimeSpan.FromMilliseconds(Math.Min(25, deadline.Remaining.TotalMilliseconds));
                if (delay > TimeSpan.Zero) Thread.Sleep(delay);
            }
        }
    }

    private static bool AnchorFilesStillMatch(string temporaryPath, string anchorPath, byte[] expectedBytes)
    {
        if (!File.Exists(temporaryPath) || !File.Exists(anchorPath)) return false;
        try
        {
            if (expectedBytes.Length > MaximumAnchorBytes) return false;
            using var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            if (stream.Length != expectedBytes.Length) return false;
            var actual = new byte[expectedBytes.Length];
            var offset = 0;
            while (offset < actual.Length)
            {
                var read = stream.Read(actual, offset, actual.Length - offset);
                if (read == 0) return false;
                offset += read;
            }
            return actual.AsSpan().SequenceEqual(expectedBytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsTransientAnchorReplaceFailure(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33 or 1175; // sharing/lock violation or ERROR_UNABLE_TO_REMOVE_REPLACED
    }

    private static byte[] AnchorBytes(string ledgerId, long sequence, string headHash, string keyId) =>
        AuditCanonical.Encode("conformance-ledger-anchor", ApplicationId.ToString(CultureInfo.InvariantCulture),
            SchemaVersion.ToString(CultureInfo.InvariantCulture), ledgerId,
            sequence.ToString(CultureInfo.InvariantCulture),
            headHash, keyId);

    private static string ComputeChainHash(long sequence, string kind, string id, string payloadHash,
        string previousHash) => Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode(
            "conformance-ledger-entry", ApplicationId.ToString(CultureInfo.InvariantCulture),
            SchemaVersion.ToString(CultureInfo.InvariantCulture), sequence.ToString(CultureInfo.InvariantCulture),
            kind, id, payloadHash, previousHash)));

    private static long ValidateItems(IReadOnlyList<ConformanceLedgerItem> items)
    {
        var seen = new HashSet<(string Kind, string Id)>();
        long total = 0;
        foreach (var item in items)
        {
            if (item is null) throw new ArgumentException("ConformanceLedgerItemRequired", nameof(items));
            ValidateKindAndId(item.Kind, item.Id);
            if (!SupportedKinds.Contains(item.Kind))
                throw new ArgumentException("ConformanceLedgerKindInvalid", nameof(items));
            if (item.Payload is null) throw new ArgumentException("ConformanceLedgerPayloadRequired", nameof(items));
            var payloadByteCount = StrictUtf8.GetByteCount(item.Payload);
            if (payloadByteCount > MaximumPayloadBytes)
                throw new ArgumentException("ConformanceLedgerPayloadTooLarge", nameof(items));
            if (item.Kind == "artifact") ValidateArtifactPayload(item.Id, item.Payload);
            if (!seen.Add((item.Kind, item.Id)))
                throw new ArgumentException("ConformanceLedgerDuplicateId", nameof(items));
            total = checked(total + payloadByteCount);
        }
        return total;
    }

    private static byte[] ValidateStoredItem(string kind, string id, string payload)
    {
        ValidateKindAndId(kind, id);
        if (!SupportedKinds.Contains(kind)) throw new InvalidOperationException("ConformanceLedgerKindInvalid");
        var payloadByteCount = StrictUtf8.GetByteCount(payload);
        if (payloadByteCount > MaximumPayloadBytes) throw new InvalidOperationException("ConformanceLedgerPayloadTooLarge");
        var bytes = StrictUtf8.GetBytes(payload);
        if (kind == "artifact")
        {
            try
            {
                ValidateArtifactPayload(id, payload);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
        return bytes;
    }

    private static void ValidateKindAndId(string kind, string id)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > MaximumKindLength ||
            string.IsNullOrWhiteSpace(id) || id.Length > MaximumIdLength ||
            !IsSafeToken(kind) || !IsSafeToken(id))
            throw new ArgumentException("ConformanceLedgerIdentifierInvalid");
    }

    private static bool IsSafeToken(string value) => value.All(character =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static void ValidateArtifactPayload(string id, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("ConformanceArtifactPayloadInvalid");

            var hasLength = false;
            var hasBytes = false;
            var hasClassification = false;
            var hasContentType = false;
            var length = 0;
            string? base64 = null;
            string? classification = null;
            string? contentType = null;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "Length":
                        if (hasLength || property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out length))
                            throw new ArgumentException("ConformanceArtifactPayloadInvalid");
                        hasLength = true;
                        break;
                    case "BytesBase64":
                        if (hasBytes || property.Value.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("ConformanceArtifactPayloadInvalid");
                        base64 = property.Value.GetString();
                        hasBytes = true;
                        break;
                    case "Classification":
                        if (hasClassification || property.Value.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("ConformanceArtifactPayloadInvalid");
                        classification = property.Value.GetString();
                        hasClassification = true;
                        break;
                    case "ContentType":
                        if (hasContentType || property.Value.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("ConformanceArtifactPayloadInvalid");
                        contentType = property.Value.GetString();
                        hasContentType = true;
                        break;
                    default:
                        throw new ArgumentException("ConformanceArtifactPayloadInvalid");
                }
            }

            if (!hasLength || !hasBytes || !hasClassification || !hasContentType || length < 0 ||
                length > MaximumArtifactBytes || base64 is null || classification != "PublicTestData" ||
                contentType != "application/octet-stream")
                throw new ArgumentException("ConformanceArtifactPayloadInvalid");

            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException ex) { throw new ArgumentException("ConformanceArtifactPayloadInvalid", ex); }
            if (bytes.Length != length || bytes.Length > MaximumArtifactBytes ||
                !string.Equals(Convert.ToBase64String(bytes), base64, StringComparison.Ordinal))
                throw new ArgumentException("ConformanceArtifactPayloadInvalid");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), id, StringComparison.Ordinal))
                throw new ArgumentException("ConformanceArtifactPayloadHashMismatch");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("ConformanceArtifactPayloadInvalid", ex);
        }
    }

    private static bool Exists(SQLitePCL.sqlite3 database, string kind, string id, StoreDeadline deadline,
        CancellationToken cancellationToken) =>
        SqliteNative.WithStatement(database,
            "SELECT 1 FROM conformance_ledger_entries WHERE Kind=? AND Id=? LIMIT 1;", deadline, statement =>
            {
                SqliteNative.BindText(database, statement, 1, kind);
                SqliteNative.BindText(database, statement, 2, id);
                return SqliteNative.Step(database, statement, deadline, cancellationToken) == SQLitePCL.raw.SQLITE_ROW;
            }, cancellationToken);

    private static void Insert(SQLitePCL.sqlite3 database, long sequence, ConformanceLedgerItem item,
        string payloadHash, string previousHash, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        SqliteNative.WithStatement(database,
            "INSERT INTO conformance_ledger_entries(Sequence,Kind,Id,Payload,Sha256,PreviousHash) VALUES(?,?,?,?,?,?);",
            deadline, statement =>
            {
                SqliteNative.BindInt64(database, statement, 1, sequence);
                SqliteNative.BindText(database, statement, 2, item.Kind);
                SqliteNative.BindText(database, statement, 3, item.Id);
                SqliteNative.BindText(database, statement, 4, item.Payload);
                SqliteNative.BindText(database, statement, 5, payloadHash);
                SqliteNative.BindText(database, statement, 6, previousHash);
                SqliteNative.Step(database, statement, deadline, cancellationToken);
                return 0;
            }, cancellationToken);
    }

    private static Dictionary<string, string> ReadSchemaDefinitions(SQLitePCL.sqlite3 database, StoreDeadline deadline) =>
        SqliteNative.WithStatement(database,
            "SELECT type || ':' || name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name;",
            deadline, statement =>
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                while (SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW)
                {
                    var name = SqliteNative.ColumnText(statement, 0);
                    var sql = SqliteNative.ColumnText(statement, 1);
                    if (name is null || sql is null) throw new InvalidOperationException("ConformanceLedgerSchemaMismatch");
                    result[name] = sql;
                }
                return result;
            });

    private static long ReadInt64(SQLitePCL.sqlite3 database, string sql, StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            if (SqliteNative.Step(database, statement, deadline) != SQLitePCL.raw.SQLITE_ROW)
                throw new InvalidOperationException("ConformanceLedgerPragmaUnavailable");
            return SqliteNative.ColumnInt64(statement, 0);
        });

    private static string? ReadText(SQLitePCL.sqlite3 database, string sql, StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            if (SqliteNative.Step(database, statement, deadline) != SQLitePCL.raw.SQLITE_ROW)
                throw new InvalidOperationException("ConformanceLedgerPragmaUnavailable");
            return SqliteNative.ColumnText(statement, 0);
        });

    private static void Rollback(SQLitePCL.sqlite3 database)
    {
        try { SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK;"); }
        catch { }
    }

    private (long Sequence, string HeadHash, long TotalPayloadBytes, IReadOnlyList<ConformanceLedgerEntry> Entries)
        ReadSnapshot(SQLitePCL.sqlite3 database, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        var transactionCompleted = false;
        SqliteNative.Execute(database, "BEGIN;", deadline, cancellationToken);
        try
        {
            ValidateDatabaseIdentity(database, deadline);
            ValidateSchemaDefinitions(database, deadline);
            ValidateMetadata(database, deadline, cancellationToken);
            var current = ReadAndVerifyChain(database, deadline, cancellationToken);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            transactionCompleted = true;
            return current;
        }
        finally
        {
            if (!transactionCompleted) Rollback(database);
        }
    }

    private sealed record AnchorDocument(string LedgerId, long Sequence, string HeadHash, string KeyId, string Signature);

    private sealed class AnchorMismatchException : Exception
    {
        internal AnchorMismatchException(long actualSequence, string actualHeadHash)
            : base("ConformanceLedgerAnchorMismatch")
        {
            ActualSequence = actualSequence;
            ActualHeadHash = actualHeadHash;
        }

        internal long ActualSequence { get; }
        internal string ActualHeadHash { get; }
    }

    private const string SchemaSql = @"
        CREATE TABLE conformance_ledger_meta(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            LedgerId TEXT NOT NULL,
            KeyId TEXT NOT NULL,
            PublicKey TEXT NOT NULL,
            CreatedAtUtc TEXT NOT NULL
        );
        CREATE TABLE conformance_ledger_entries(
            Sequence INTEGER NOT NULL PRIMARY KEY CHECK(Sequence>0),
            Kind TEXT NOT NULL,
            Id TEXT NOT NULL,
            Payload TEXT NOT NULL,
            Sha256 TEXT NOT NULL CHECK(length(Sha256)=64),
            PreviousHash TEXT NOT NULL CHECK(length(PreviousHash)=64),
            UNIQUE(Kind,Id)
        );
        CREATE INDEX ix_conformance_ledger_kind_id ON conformance_ledger_entries(Kind,Id);
        CREATE TRIGGER conformance_ledger_entries_immutable_update BEFORE UPDATE ON conformance_ledger_entries BEGIN
            SELECT RAISE(ABORT,'ConformanceLedgerImmutable');
        END;
        CREATE TRIGGER conformance_ledger_entries_immutable_delete BEFORE DELETE ON conformance_ledger_entries BEGIN
            SELECT RAISE(ABORT,'ConformanceLedgerImmutable');
        END;
        CREATE TRIGGER conformance_ledger_meta_immutable_update BEFORE UPDATE ON conformance_ledger_meta BEGIN
            SELECT RAISE(ABORT,'ConformanceLedgerImmutable');
        END;
        CREATE TRIGGER conformance_ledger_meta_immutable_delete BEFORE DELETE ON conformance_ledger_meta BEGIN
            SELECT RAISE(ABORT,'ConformanceLedgerImmutable');
        END;";
}

#pragma warning restore CA1416
