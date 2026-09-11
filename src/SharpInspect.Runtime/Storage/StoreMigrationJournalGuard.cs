using System.Security.Cryptography;
using System.Text;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal static class StoreMigrationJournalGuard
{
    private static readonly byte[] MarkerMagic = Encoding.ASCII.GetBytes("SI-MM01\n");
    internal const long MaximumReadableJournalBytes = 16L * 1024 * 1024;
    internal static string JournalPath(string databasePath) => DerivedPath(databasePath, "journal");
    internal static string MarkerPath(string databasePath) => DerivedPath(databasePath, "marker");
    internal static string BackupPath(string databasePath, Guid operationId, int attempt) =>
        DerivedPath(databasePath, operationId.ToString("N") + "." + attempt + ".backup.db");

    private static string DerivedPath(string databasePath, string suffix)
    {
        // A fixed-length sibling name also works for valid database names close
        // to the NTFS component limit. The marker binds the full source path.
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant())));
        return Path.Combine(Path.GetDirectoryName(databasePath)!, ".sharpinspect-migration-" + pathHash + "." + suffix);
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void ValidatePath(string path)
    {
        if (!StoragePathValidator.TryValidate(new ProductionStoreOptions(path), out _, out var reason))
            throw new InvalidOperationException(reason);
    }

    internal static void CreateMarker(string databasePath, Guid operationId)
    {
        if (operationId == Guid.Empty) throw new InvalidOperationException("StoreMigrationOperationRequired");
        var path = MarkerPath(databasePath);
        ValidatePath(path);
        var operation = operationId.ToByteArray();
        var hash = MarkerHash(databasePath, operation);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        file.Write(MarkerMagic);
        file.Write(operation);
        file.Write(hash);
        file.Flush(flushToDisk: true);
    }

    internal static Guid ReadMarker(string databasePath)
    {
        var path = MarkerPath(databasePath);
        ValidatePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != MarkerMagic.Length + 16 + 32)
            throw new InvalidOperationException("StoreMigrationMarkerInvalid");
        var bytes = new byte[checked((int)file.Length)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = file.Read(bytes, read, bytes.Length - read);
            if (count == 0) throw new InvalidOperationException("StoreMigrationMarkerInvalid");
            read += count;
        }
        var operation = bytes.AsSpan(MarkerMagic.Length, 16);
        if (!bytes.AsSpan(0, MarkerMagic.Length).SequenceEqual(MarkerMagic) ||
            !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(MarkerMagic.Length + 16), MarkerHash(databasePath, operation)))
            throw new InvalidOperationException("StoreMigrationMarkerInvalid");
        var id = new Guid(operation);
        if (id == Guid.Empty) throw new InvalidOperationException("StoreMigrationMarkerInvalid");
        return id;
    }

    internal static void RequireWriterReady(ProductionStoreOptions options, string databasePath)
    {
        try
        {
            var marker = MarkerPath(databasePath);
            var journal = JournalPath(databasePath);
            ValidatePath(marker);
            ValidatePath(journal);
            var markerExists = Exists(marker);
            var journalExists = Exists(journal);
            if (!markerExists && !journalExists) return;
            if (!markerExists || !journalExists)
                throw new InvalidOperationException("StoreMigrationJournalOrMarkerMissing");
            var id = ReadMarker(databasePath);
            var entry = StoreMigrationJournal.Read(journal, MaximumReadableJournalBytes) ??
                throw new InvalidOperationException("StoreMigrationJournalEmpty");
            RequireCompletedLineage(entry.Data, id, options, databasePath);
        }
        catch (IOException ex) { throw new InvalidOperationException("StoreMigrationJournalUnavailable", ex); }
    }

    internal static void RequireCompletedLineage(StoreMigrationJournalData data, Guid markerId,
        ProductionStoreOptions options, string databasePath)
    {
        if (data.OperationId != markerId || !string.Equals(data.DatabasePath, databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("StoreMigrationJournalDatabaseBindingMismatch");
        if (data.Phase != StoreMigrationPhase.Completed || !data.CommitIntentDurable)
            throw new InvalidOperationException("StoreMigrationStartupMaintenanceRequired");
        if (options.RecipeLifecycle is null || LifecycleHash(options.RecipeLifecycle) != data.LifecycleConfigurationHash)
            throw new InvalidOperationException("StoreMigrationJournalConfigurationMismatch");
        using var connection = SqliteNative.Open(databasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, options);
        var deadline = new StoreDeadline(options.QueryTimeout);
        var database = connection.Handle!;
        if (AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) != data.TargetSchemaVersion)
            throw new InvalidOperationException("StoreMigrationJournalDatabaseGenerationMismatch");
        var hashes = AuditChainDatabase.Read(database, "SELECT Hash FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => SqliteNative.ColumnText(statement, 0),
            data.TargetAuditSequence!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (hashes.Count != 1 || hashes[0] != data.TargetAuditHash)
            throw new InvalidOperationException("StoreMigrationJournalAuditLineageMismatch");
        // Normal startup still verifies the whole applicable audit chain. Later
        // legitimate appends do not have to equal the migration-time whole DB hash.
    }

    internal static string LifecycleHash(RecipeLifecycleStoreOptions options) =>
        Convert.ToHexString(SHA256.HashData(options.EncodeActivationPayload()));

    private static byte[] MarkerHash(string databasePath, ReadOnlySpan<byte> operation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MarkerMagic);
        hash.AppendData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant()));
        hash.AppendData(operation);
        return hash.GetHashAndReset();
    }
}
