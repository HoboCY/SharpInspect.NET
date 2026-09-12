using System.Security.Cryptography;
using System.Text;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal enum MigrationMarkerBoundary { AfterOperationWrite, BeforeFlush, AfterFlush, BeforeReplace, AfterReplace }

internal static class StoreMigrationJournalGuard
{
    private static readonly byte[] MarkerMagic = Encoding.ASCII.GetBytes("SI-MM01\n");
    internal const long MaximumReadableJournalBytes = 16L * 1024 * 1024;
    internal const int MarkerByteLength = 56;
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

    /// <summary>
    /// Atomically replaces the permanent marker with the next operation of the same
    /// database. The previous marker bytes must already be archived in the journal's
    /// linked provenance; this method never removes the file and never rewrites the
    /// journal.
    /// </summary>
    internal static void ReplaceMarker(string databasePath, Guid operationId,
        Action<MigrationMarkerBoundary>? observer = null)
    {
        if (operationId == Guid.Empty) throw new InvalidOperationException("StoreMigrationOperationRequired");
        var path = MarkerPath(databasePath);
        ValidatePath(path);
        var operation = operationId.ToByteArray();
        var hash = MarkerHash(databasePath, operation);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        ValidatePath(temporary);
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            file.Write(MarkerMagic);
            file.Write(operation);
            observer?.Invoke(MigrationMarkerBoundary.AfterOperationWrite);
            file.Write(hash);
            observer?.Invoke(MigrationMarkerBoundary.BeforeFlush);
            file.Flush(flushToDisk: true);
            observer?.Invoke(MigrationMarkerBoundary.AfterFlush);
        }
        // The durable journal already binds both operation IDs. After any process
        // interruption the permanent name therefore holds either complete marker.
        observer?.Invoke(MigrationMarkerBoundary.BeforeReplace);
        File.Replace(temporary, path, destinationBackupFileName: null);
        observer?.Invoke(MigrationMarkerBoundary.AfterReplace);
    }

    /// <summary>Reads the complete marker file bytes after re-proving their binding.</summary>
    internal static byte[] ReadMarkerBytes(string databasePath)
    {
        var path = MarkerPath(databasePath);
        ValidatePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != MarkerByteLength) throw new InvalidOperationException("StoreMigrationMarkerInvalid");
        var bytes = new byte[MarkerByteLength];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = file.Read(bytes, read, bytes.Length - read);
            if (count == 0) throw new InvalidOperationException("StoreMigrationMarkerInvalid");
            read += count;
        }
        var operation = bytes.AsSpan(MarkerMagic.Length, 16);
        if (!bytes.AsSpan(0, MarkerMagic.Length).SequenceEqual(MarkerMagic) ||
            !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(MarkerMagic.Length + 16),
                MarkerHash(databasePath, operation)))
            throw new InvalidOperationException("StoreMigrationMarkerInvalid");
        return bytes;
    }

    /// <summary>
    /// Re-proves that archived marker bytes are the exact, valid marker of one named
    /// operation of this database. The archived copy is the evidence that the chained
    /// operation did not silently replace a foreign generation's marker.
    /// </summary>
    internal static void RequireArchivedMarker(string base64, string databasePath, Guid operationId)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new InvalidOperationException("StoreMigrationJournalChainLinkInvalid"); }
        if (bytes.Length != MarkerByteLength ||
            !bytes.AsSpan(0, MarkerMagic.Length).SequenceEqual(MarkerMagic))
            throw new InvalidOperationException("StoreMigrationJournalChainLinkInvalid");
        var archived = new Guid(bytes.AsSpan(MarkerMagic.Length, 16));
        if (archived != operationId || !CryptographicOperations.FixedTimeEquals(
                bytes.AsSpan(MarkerMagic.Length + 16), MarkerHash(databasePath, archived.ToByteArray())))
            throw new InvalidOperationException("StoreMigrationJournalChainLinkInvalid");
    }

    internal static Guid ReadMarker(string databasePath)
    {
        var path = MarkerPath(databasePath);
        ValidatePath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != MarkerByteLength)
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

    /// <summary>
    /// Re-proves that the completed operation named by the durable frame is exactly the
    /// generation and optional feature profile the caller now opens. Every feature the
    /// operation bound must still be present with the recorded hash, and a schema-36
    /// operation must still carry its recorded outbox binding, so a store can never be
    /// opened as a different profile than the one the operation migrated. A feature the
    /// operation did not bind is not inspected here: its own frame already proved that
    /// feature absent, and any later feature is bound by the operation that adds it.
    /// </summary>
    internal static void RequireCompletedLineage(StoreMigrationJournalData data, Guid markerId,
        ProductionStoreOptions options, string databasePath)
    {
        if (data.OperationId != markerId || !string.Equals(data.DatabasePath, databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("StoreMigrationJournalDatabaseBindingMismatch");
        if (data.Phase != StoreMigrationPhase.Completed || !data.CommitIntentDurable)
            throw new InvalidOperationException("StoreMigrationStartupMaintenanceRequired");
        if (!StoreMigrationJournal.TryResolvePlan(data.SourceSchemaVersion, data.TargetSchemaVersion,
                data.PlanId, out var plan))
            throw new InvalidOperationException("StoreMigrationJournalRecordInvalid");
        if (plan.Lifecycle && (options.RecipeLifecycle is null ||
                LifecycleHash(options.RecipeLifecycle) != data.LifecycleConfigurationHash) ||
            plan.ImageEvidence && (options.ImageEvidence is null ||
                options.ImageEvidence.BindingHash != data.ImageEvidenceConfigurationHash) ||
            plan.ImageFinalization && (options.ImageFinalization is null ||
                options.ImageFinalization.BindingHash != data.ImageFinalizationConfigurationHash) ||
            plan.ProductionOutbox && (options.Outbox is null ||
                options.Outbox.BindingHash != data.ProductionOutboxConfigurationHash))
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

    /// <summary>
    /// The lifecycle binding hash of a target that declares the ledger, or null when the
    /// operation's source generation never carried it.
    /// </summary>
    internal static string? LifecycleHashOrNull(RecipeLifecycleStoreOptions? options) =>
        options is null ? null : LifecycleHash(options);

    private static byte[] MarkerHash(string databasePath, ReadOnlySpan<byte> operation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MarkerMagic);
        hash.AppendData(Encoding.UTF8.GetBytes(databasePath.ToUpperInvariant()));
        hash.AppendData(operation);
        return hash.GetHashAndReset();
    }
}
