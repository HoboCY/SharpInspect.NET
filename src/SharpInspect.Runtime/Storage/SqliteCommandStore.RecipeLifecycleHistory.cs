using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Schema-33 Recipe lifecycle ledger storage, entirely separate from the schema-31
/// selection, schema-32 arm and schema-16 release ledgers: one bounded append-only
/// table of immutable abandonment/retirement transitions and one immutable
/// configuration row. This file owns only the declared tables; the signed store
/// activation entry and the per-row central audit metadata entry are appended by the
/// caller inside the same writer transaction, so no audit position, hash or envelope
/// of an earlier schema is rewritten here.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string RecipeLifecycleActivationKind = "RecipeLifecycleStoreActivated";
    internal const string RecipeLifecycleEventAuditKind = "RecipeLifecycleEvent";

    internal const string RecipeLifecycleSchemaSql = @"
        CREATE TABLE recipe_lifecycle_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaxEvents INTEGER NOT NULL CHECK(MaxEvents>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaxTotalBytes INTEGER NOT NULL CHECK(MaxTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE recipe_lifecycle_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            TransitionId TEXT NOT NULL UNIQUE CHECK(length(TransitionId)=36),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            Kind INTEGER NOT NULL CHECK(Kind IN (1,2)),
            DraftId TEXT NOT NULL CHECK(length(DraftId)=36),
            SourceRevision INTEGER NOT NULL CHECK(SourceRevision>0),
            ReleaseId TEXT NULL CHECK(ReleaseId IS NULL OR length(ReleaseId)=36),
            RecordContentHash TEXT NOT NULL UNIQUE CHECK(length(RecordContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandEventId TEXT NOT NULL UNIQUE CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL UNIQUE CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64),
            CHECK((Kind=1 AND ReleaseId IS NULL) OR (Kind=2 AND ReleaseId IS NOT NULL)));
        CREATE UNIQUE INDEX ux_recipe_lifecycle_draft ON recipe_lifecycle_events(DraftId) WHERE Kind=1;
        CREATE UNIQUE INDEX ux_recipe_lifecycle_release ON recipe_lifecycle_events(ReleaseId) WHERE Kind=2;
        CREATE INDEX ix_recipe_lifecycle_draft ON recipe_lifecycle_events(DraftId,Position);
        CREATE INDEX ix_recipe_lifecycle_release ON recipe_lifecycle_events(ReleaseId,Position);
        CREATE INDEX ix_recipe_lifecycle_audit ON recipe_lifecycle_events(AuditSequence);
        CREATE TRIGGER recipe_lifecycle_config_immutable_update BEFORE UPDATE
            ON recipe_lifecycle_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeLifecycleConfiguration');
        END;
        CREATE TRIGGER recipe_lifecycle_config_immutable_delete BEFORE DELETE
            ON recipe_lifecycle_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeLifecycleConfiguration');
        END;
        CREATE TRIGGER recipe_lifecycle_event_immutable_update BEFORE UPDATE
            ON recipe_lifecycle_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeLifecycleEvent');
        END;
        CREATE TRIGGER recipe_lifecycle_event_immutable_delete BEFORE DELETE
            ON recipe_lifecycle_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeLifecycleEvent');
        END;";

    /// <summary>
    /// Creates the declared schema-33 tables and inserts the one immutable configuration
    /// row. The signed store-activation entry is deliberately not appended here: the
    /// caller must append it with <see cref="RecipeLifecycleActivationKind"/> and
    /// <c>RecipeLifecycleStoreOptions.EncodeActivationPayload()</c> inside the same
    /// transaction, so configuration and activation evidence commit together.
    /// </summary>
    internal static void InitializeRecipeLifecycleTables(sqlite3 database, RecipeLifecycleStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        SqliteNative.Execute(database, RecipeLifecycleSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO recipe_lifecycle_store_config
            (Id,FormatVersion,MaxEvents,MaximumPayloadBytes,MaxTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            LifecycleNumber(RecipeLifecycleStoreOptions.FormatVersion), LifecycleNumber(options.MaxEvents),
            LifecycleNumber(options.MaximumPayloadBytes), LifecycleNumber(options.MaxTotalBytes),
            options.BindingHash);
    }

    /// <summary>
    /// Creates the declared schema-33 tables, inserts the one immutable configuration row
    /// and appends the signed store-activation entry inside the caller's writer
    /// transaction, so the configuration and its activation evidence commit together.
    /// </summary>
    internal static void InitializeRecipeLifecycleSchema(sqlite3 database, RecipeLifecycleStoreOptions options,
        StoreDeadline deadline, AuditIntegrityPolicy policy, IAuditSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(key);
        InitializeRecipeLifecycleTables(database, options, deadline);
        AuditChainDatabase.AppendRecipeLifecycleMetadata(database, policy, key, RecipeLifecycleActivationKind,
            options.EncodeActivationPayload(), options, deadline);
    }

    /// <summary>
    /// Full central-audit proof for the schema-33 lifecycle ledger before any stored row is
    /// projected. The signed chain is verified from genesis through the tail with the
    /// lifecycle option supplied, so the reverse metadata membership, the activation
    /// binding, the immutable configuration binding and the metadata sequence/hash
    /// bindings are all re-proved; a missing option, a foreign schema or a tampered
    /// ledger fails closed instead of returning evidence.
    /// </summary>
    internal static void VerifyRecipeLifecycleReadGuard(sqlite3 database, ProductionStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        var lifecycleOptions = options.RecipeLifecycle ??
            throw new InvalidOperationException("RecipeLifecycleConfigurationRequired");
        lifecycleOptions.Validate();
        var policy = options.AuditIntegrityPolicy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is
            RecipeLifecycleStoreOptions.SchemaVersion or ProductionImageEvidenceStoreOptions.SchemaVersion,
            "RecipeLifecycleGovernedMigrationRequired");
        using var key = WindowsMachineAuditKey.Open(policy, false, out _);
        var report = AuditChainDatabase.Verify(database, policy, key.KeyId, key.PublicKeyBase64,
            new AuditVerificationRequest(0, policy.MaximumVerificationEntries), startup: false, deadline,
            validateAnchorReceipt: false, archiveOptions: options.AlgorithmResultArchive,
            recipeDraftOptions: options.RecipeDrafts, cameraSetupOptions: options.CameraSetup,
            cameraRecoveryOptions: options.CameraRecovery, cameraNetworkOptions: options.CameraNetwork,
            imagingSetupOptions: options.ImagingSetup, calibrationSessionOptions: options.CalibrationSessions,
            governanceOptions: options.CalibrationGovernance, releaseOptions: options.RecipeReleases,
            contractOptions: options.PlcResultContracts, activationOptions: options.RecipeActivations,
            previewOptions: options.PreviewSessions, importOptions: options.CalibrationImports,
            manualOptions: options.ManualInspections, productionAdmissionOptions: options.ProductionAdmission,
            stationQualificationOptions: options.StationQualifications, recipeTransferOptions: options.RecipeTransfers,
            traceStoragePolicyOptions: options.TraceStoragePolicies,
            qualificationCycleOptions: options.QualificationCycles,
            plcCommunicationOptions: options.PlcCommunication,
            productionInspectionOptions: options.ProductionInspections,
            productionRecoveryOptions: options.ProductionRecovery, partIdentityOptions: options.PartIdentities,
            recipeSelectionOptions: options.RecipeSelections, productionArmOptions: options.ProductionArming,
            recipeLifecycleOptions: lifecycleOptions, imageEvidenceOptions: options.ImageEvidence);
        AuditChainDatabase.RequireFullRecipeLifecycleVerification(database, report, deadline, lifecycleOptions);
    }

    /// <summary>
    /// The bounded read surface for the lifecycle ledger: the complete signed audit chain is
    /// proved first (no projection is returned when that proof fails), then every stored row
    /// is decoded, re-encoded and re-bound to its own predecessor and immutable columns.
    /// Cross-ledger authority, identity and temporal ordering are owned by the caller's
    /// read guard; this method proves the central audit and the local ledger only.
    /// </summary>
    internal static IReadOnlyList<RecipeLifecycleStoredRow> ReadRecipeLifecycleVerifiedHistory(sqlite3 database,
        ProductionStoreOptions options, StoreDeadline deadline)
    {
        VerifyRecipeLifecycleReadGuard(database, options, deadline);
        return ReadRecipeLifecycleRows(database, options.RecipeLifecycle!, deadline);
    }

    /// <summary>
    /// Requires the single immutable configuration row to equal the caller's explicitly
    /// bounded options. A missing, foreign or edited row fails closed; the option
    /// binding hash covers the format version and every capacity bound.
    /// </summary>
    internal static void RequireConfiguredRecipeLifecycle(sqlite3 database, RecipeLifecycleStoreOptions options,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaxEvents,MaximumPayloadBytes,
            MaxTotalBytes,BindingHash FROM recipe_lifecycle_store_config WHERE Id=1 LIMIT 2;", deadline,
            value => new[]
            {
                SqliteNative.ColumnText(value, 0) ?? string.Empty,
                SqliteNative.ColumnText(value, 1) ?? string.Empty,
                SqliteNative.ColumnText(value, 2) ?? string.Empty,
                SqliteNative.ColumnText(value, 3) ?? string.Empty,
                SqliteNative.ColumnText(value, 4) ?? string.Empty
            });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
        {
            LifecycleNumber(RecipeLifecycleStoreOptions.FormatVersion),
            LifecycleNumber(options.MaxEvents), LifecycleNumber(options.MaximumPayloadBytes),
            LifecycleNumber(options.MaxTotalBytes), options.BindingHash
        }), "RecipeLifecycleConfigurationMismatch");
    }

    /// <summary>
    /// Reads every immutable lifecycle row in position order. Every payload is decoded and
    /// re-encoded, every normalized column is compared with the decoded record, and the
    /// ledger invariants are re-derived from the payloads alone: an edited index column,
    /// a repeated draft abandonment or release retirement, a position gap, a predecessor
    /// that is not the previous record's own content hash (never its audit hash), a
    /// reversed audit time, or any capacity overflow fails the read. Central audit
    /// metadata binding is verified separately by the caller through
    /// <see cref="EncodeAuditBinding"/> and the signed chain.
    /// </summary>
    internal static List<RecipeLifecycleStoredRow> ReadRecipeLifecycleRows(sqlite3 database,
        RecipeLifecycleStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(deadline);
        options.Validate();
        if (AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM recipe_lifecycle_events;", deadline) >
            options.MaxEvents)
            throw new InvalidOperationException("RecipeLifecycleEntryCapacityExceeded");
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,PreviousHash,TransitionId,OperationId,Kind,
            DraftId,SourceRevision,ReleaseId,RecordContentHash,PayloadHash,Payload,CommandEventId,
            CommandAuditSequence,CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,
            AuthorizationAuditHash,AuditSequence,AuditHash FROM recipe_lifecycle_events ORDER BY Position;",
            deadline, value =>
            {
                string Text(int column) => SqliteNative.ColumnText(value, column) ??
                    throw new InvalidOperationException("RecipeLifecycleColumnMissing");
                var payload = DecodeRecipeLifecyclePayload(Text(10));
                if (payload.Length > options.MaximumPayloadBytes)
                    throw new InvalidOperationException("RecipeLifecyclePayloadCapacityExceeded");
                var record = RecipeLifecycleStorageCodec.Decode(payload);
                if (record.Position != SqliteNative.ColumnInt64(value, 0) ||
                    record.TransitionId != LifecycleGuid(value, 2) ||
                    record.OperationId != LifecycleGuid(value, 3) ||
                    (long)record.Kind != SqliteNative.ColumnInt64(value, 4) ||
                    record.SourceDraft.DraftId != LifecycleGuid(value, 5) ||
                    record.SourceDraft.Revision != SqliteNative.ColumnInt64(value, 6) ||
                    !string.Equals(record.ReleaseId?.ToString("D"), SqliteNative.ColumnText(value, 7),
                        StringComparison.Ordinal) ||
                    record.ContentHash != Text(8) ||
                    Convert.ToHexString(SHA256.HashData(payload)) != Text(9))
                    throw new InvalidOperationException("RecipeLifecycleIndexedPayloadMismatch");
                if (!string.Equals(record.PreviousHash, SqliteNative.ColumnText(value, 1), StringComparison.Ordinal))
                    throw new InvalidOperationException("RecipeLifecyclePreviousHashMismatch");
                var row = new RecipeLifecycleStoredRow(record, Text(9), payload, LifecycleGuid(value, 11),
                    SqliteNative.ColumnInt64(value, 12), Text(13), LifecycleGuid(value, 14),
                    SqliteNative.ColumnInt64(value, 15), Text(16), SqliteNative.ColumnInt64(value, 17),
                    Text(18));
                RequireLifecycleAuditColumns(row);
                return row;
            });
        var total = 0L;
        var abandonedDrafts = new HashSet<Guid>();
        var retiredReleases = new HashSet<Guid>();
        RecipeLifecycleRecord? previous = null;
        foreach (var row in rows)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var record = row.Record;
            if (record.Position != (previous?.Position ?? 0) + 1 ||
                !string.Equals(record.PreviousHash, previous?.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeLifecycleHistorySequenceInvalid");
            if (previous is not null && record.RecordedAtUtc < previous.RecordedAtUtc)
                throw new InvalidOperationException("RecipeLifecycleHistoryTimeReversed");
            if (record.Kind == RecipeLifecycleKind.DraftAbandoned)
            {
                if (record.ReleaseId is not null || !abandonedDrafts.Add(record.SourceDraft.DraftId))
                    throw new InvalidOperationException("RecipeLifecycleDraftAlreadyAbandoned");
            }
            else if (record.ReleaseId is not { } releaseId || !retiredReleases.Add(releaseId))
            {
                throw new InvalidOperationException("RecipeLifecycleReleaseAlreadyRetired");
            }
            total = checked(total + row.Payload.Length);
            if (total > options.MaxTotalBytes)
                throw new InvalidOperationException("RecipeLifecycleTotalCapacityExceeded");
            previous = record;
        }
        return rows;
    }

    /// <summary>
    /// Decodes one stored base64 payload. A non-canonical base64 text, an empty value or
    /// any value beyond the hard payload bound is rejected before the payload is decoded.
    /// </summary>
    internal static byte[] DecodeRecipeLifecyclePayload(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is < 1 or > ((RecipeLifecycleStoreOptions.MaximumPayloadBytesHardLimit + 2) / 3) * 4)
            throw new InvalidOperationException("RecipeLifecyclePayloadCapacityExceeded");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("RecipeLifecyclePayloadEncodingInvalid", exception);
        }
        if (bytes.Length is < 1 or > RecipeLifecycleStoreOptions.MaximumPayloadBytesHardLimit)
            throw new InvalidOperationException("RecipeLifecyclePayloadCapacityExceeded");
        if (!string.Equals(Convert.ToBase64String(bytes), text, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeLifecyclePayloadEncodingInvalid");
        return bytes;
    }

    /// <summary>
    /// The canonical bytes bound into the signed central audit metadata entry for one
    /// lifecycle row. The binding covers the ledger position, the record content hash,
    /// the record predecessor, the payload hash, the exact command and authorization
    /// audit references and the payload itself; it deliberately excludes the metadata
    /// entry's own sequence and hash, so no recursion is possible.
    /// </summary>
    internal static byte[] EncodeAuditBinding(RecipeLifecycleStoredRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return AuditCanonical.Encode("RecipeLifecycleLedgerEntryV1",
            LifecycleNumber(row.Record.Position), row.Record.TransitionId.ToString("D"),
            row.Record.OperationId.ToString("D"), ((int)row.Record.Kind).ToString(CultureInfo.InvariantCulture),
            row.Record.SourceDraft.DraftId.ToString("D"),
            LifecycleNumber(row.Record.SourceDraft.Revision), row.Record.SourceDraft.RevisionContentHash,
            row.Record.ReleaseId?.ToString("D"), row.Record.ContentHash, row.Record.PreviousHash, row.PayloadHash,
            row.CommandEventId.ToString("D"), LifecycleNumber(row.CommandAuditSequence), row.CommandAuditHash,
            row.AuthorizationEventId.ToString("D"), LifecycleNumber(row.AuthorizationAuditSequence),
            row.AuthorizationAuditHash, Convert.ToBase64String(row.Payload));
    }

    /// <summary>
    /// Cross-ledger validation of decoded lifecycle rows against the exact draft, release,
    /// activation and selection revisions the caller already read from their own verified
    /// ledgers. Every array is required: an empty array means "the configured ledger holds
    /// no such row", never "skip this check", so absent evidence fails closed instead of
    /// silently downgrading tamper checking. Callers must call
    /// <see cref="RequireConfiguredRecipeLifecycle"/> first and must additionally verify
    /// the central audit metadata entry for each row (see <see cref="EncodeAuditBinding"/>),
    /// the command/authorization audit references, the Runtime quiescence and Active-clear
    /// evidence, and the absence of a Selection Map rewrite; those bindings need the
    /// signed chain and are not derivable from immutable revision arrays alone.
    /// </summary>
    internal static void ValidateRecipeLifecycleHistory(IReadOnlyList<RecipeLifecycleStoredRow> rows,
        IReadOnlyList<RecipeDraftRevision> drafts, IReadOnlyList<RecipeReleaseRecord> releases,
        IReadOnlyList<RecipeActivationRecord> activations, IReadOnlyList<RecipeSelectionRevision> selections)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(activations);
        ArgumentNullException.ThrowIfNull(selections);
        var abandonedDrafts = new HashSet<Guid>();
        var retiredReleases = new HashSet<Guid>();
        RecipeLifecycleRecord? previous = null;
        foreach (var row in rows)
        {
            var record = row.Record;
            if (record.Position != (previous?.Position ?? 0) + 1 ||
                !string.Equals(record.PreviousHash, previous?.ContentHash, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeLifecycleHistorySequenceInvalid");
            if (previous is not null && record.RecordedAtUtc < previous.RecordedAtUtc)
                throw new InvalidOperationException("RecipeLifecycleHistoryTimeReversed");
            if (record.Kind == RecipeLifecycleKind.DraftAbandoned)
            {
                if (record.ReleaseId is not null || !abandonedDrafts.Add(record.SourceDraft.DraftId))
                    throw new InvalidOperationException("RecipeLifecycleDraftAlreadyAbandoned");
            }
            else if (record.ReleaseId is not { } releaseId || !retiredReleases.Add(releaseId))
            {
                throw new InvalidOperationException("RecipeLifecycleReleaseAlreadyRetired");
            }
            ValidateRecipeLifecycleSourceDraft(record, drafts);
            if (record.Kind == RecipeLifecycleKind.ReleasedRetired)
                ValidateRecipeLifecycleRelease(record, drafts, releases);
            ValidateRecipeLifecycleActive(record, activations);
            ValidateRecipeLifecycleMapImpacts(record, selections);
            previous = record;
        }
    }

    /// <summary>
    /// The exact preserved source revision and its complete content hash must exist in the
    /// supplied draft history and must precede the transition.
    /// </summary>
    private static void ValidateRecipeLifecycleSourceDraft(RecipeLifecycleRecord record,
        IReadOnlyList<RecipeDraftRevision> drafts)
    {
        var source = drafts.SingleOrDefault(value => value.DraftId == record.SourceDraft.DraftId &&
            value.Revision == record.SourceDraft.Revision &&
            string.Equals(value.RevisionContentHash, record.SourceDraft.RevisionContentHash, StringComparison.Ordinal));
        if (source is null)
            throw new InvalidOperationException("RecipeLifecycleSourceDraftRevisionMissing");
        if (!string.Equals(source.Content.ContentHash, record.SourceContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeLifecycleSourceContentHashMismatch");
        if (source.RecordedAtUtc > record.RecordedAtUtc)
            throw new InvalidOperationException("RecipeLifecycleSourceDraftTimeReversed");
    }

    /// <summary>
    /// A retirement must name one exact release record whose identity, own content hash,
    /// source revision and source revision content hash all match the transition. The
    /// release is never resolved by Recipe key and version alone, so a retirement can never
    /// be re-pointed at a different release or at a rewritten source.
    /// </summary>
    private static void ValidateRecipeLifecycleRelease(RecipeLifecycleRecord record,
        IReadOnlyList<RecipeDraftRevision> drafts, IReadOnlyList<RecipeReleaseRecord> releases)
    {
        var release = releases.SingleOrDefault(value => value.ReleaseId == record.ReleaseId &&
            string.Equals(value.ContentHash, record.ReleaseRecordContentHash, StringComparison.Ordinal));
        if (release is null)
            throw new InvalidOperationException("RecipeLifecycleReleaseRecordMissing");
        if (release.Recipe != record.Recipe || release.Source.DraftId != record.SourceDraft.DraftId ||
            release.Source.Revision != record.SourceDraft.Revision ||
            !string.Equals(release.Source.RevisionContentHash, record.SourceDraft.RevisionContentHash,
                StringComparison.Ordinal) ||
            !string.Equals(release.Source.Content.ContentHash, record.SourceContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeLifecycleReleaseSourceMismatch");
        if (release.ReleasedAtUtc > record.RecordedAtUtc)
            throw new InvalidOperationException("RecipeLifecycleReleaseTimeReversed");
        if (!drafts.Any(value => value.DraftId == release.Source.DraftId &&
                value.Revision == release.Source.Revision &&
                string.Equals(value.RevisionContentHash, release.Source.RevisionContentHash, StringComparison.Ordinal)))
            throw new InvalidOperationException("RecipeLifecycleSourceDraftRevisionMissing");
    }

    /// <summary>
    /// The observed and cleared Active selection must be one exact effective activation
    /// record that was already recorded when the transition happened. A missing, foreign
    /// or never-effective selection fails closed; the cleared pointer is never allowed to
    /// point at a different activation than the observed one (the contract enforces the
    /// same equality on construction).
    /// </summary>
    private static void ValidateRecipeLifecycleActive(RecipeLifecycleRecord record,
        IReadOnlyList<RecipeActivationRecord> activations)
    {
        if (record.ObservedActive is not { } observed) return;
        var activation = activations.SingleOrDefault(value => value.Position == observed.Position &&
            value.ActivationId == observed.ActivationId &&
            string.Equals(value.ContentHash, observed.ContentHash, StringComparison.Ordinal));
        if (activation is null)
            throw new InvalidOperationException("RecipeLifecycleActiveActivationMissing");
        if (!activation.CanBeActive)
            throw new InvalidOperationException("RecipeLifecycleActiveActivationNotEffective");
        if (activation.RecordedAtUtc > record.RecordedAtUtc)
            throw new InvalidOperationException("RecipeLifecycleActiveTimeReversed");
    }

    /// <summary>
    /// Every map impact must resolve one exact immutable Selection revision whose own map
    /// content hash matches, and the recorded codes must be exactly the codes of that map
    /// revision that still name the retired release and its exact release-record hash.
    /// A rewritten, redirected or silently narrowed map therefore fails the read.
    /// </summary>
    private static void ValidateRecipeLifecycleMapImpacts(RecipeLifecycleRecord record,
        IReadOnlyList<RecipeSelectionRevision> selections)
    {
        foreach (var impact in record.MapImpacts)
        {
            var revision = selections.SingleOrDefault(value => value.Position == impact.Selection.Position &&
                value.RevisionId == impact.Selection.RevisionId &&
                string.Equals(value.ContentHash, impact.Selection.ContentHash, StringComparison.Ordinal));
            if (revision is null)
                throw new InvalidOperationException("RecipeLifecycleSelectionRevisionMissing");
            if (revision.Map is not { } map || map.Reference != impact.Map)
                throw new InvalidOperationException("RecipeLifecycleSelectionMapMismatch");
            if (revision.RecordedAtUtc > record.RecordedAtUtc)
                throw new InvalidOperationException("RecipeLifecycleSelectionTimeReversed");
            var expected = map.Entries.Where(entry => entry.ReleaseId == record.ReleaseId &&
                    entry.Recipe == record.Recipe &&
                    string.Equals(entry.ReleaseRecordContentHash, record.ReleaseRecordContentHash,
                        StringComparison.Ordinal))
                .Select(entry => entry.SelectionCode).OrderBy(code => code).ToArray();
            if (expected.Length == 0 || !expected.SequenceEqual(impact.AffectedCodes))
                throw new InvalidOperationException("RecipeLifecycleRetirementMapImpactMismatch");
        }
    }

    private static void RequireLifecycleAuditColumns(RecipeLifecycleStoredRow row)
    {
        if (row.CommandEventId == Guid.Empty || row.AuthorizationEventId == Guid.Empty ||
            row.CommandEventId == row.AuthorizationEventId ||
            row.CommandAuditSequence < 1 || row.AuthorizationAuditSequence < 1 || row.AuditSequence < 1 ||
            row.CommandAuditSequence == row.AuthorizationAuditSequence ||
            row.CommandAuditSequence == row.AuditSequence ||
            row.AuthorizationAuditSequence == row.AuditSequence ||
            row.CommandAuditSequence > row.AuditSequence ||
            row.AuthorizationAuditSequence > row.AuditSequence ||
            !IsLifecycleHash(row.CommandAuditHash) || !IsLifecycleHash(row.AuthorizationAuditHash) ||
            !IsLifecycleHash(row.AuditHash) || !IsLifecycleHash(row.PayloadHash))
            throw new InvalidOperationException("RecipeLifecycleAuditReferenceInvalid");
    }

    private static Guid LifecycleGuid(sqlite3_stmt statement, int index) =>
        Guid.TryParseExact(SqliteNative.ColumnText(statement, index), "D", out var result) && result != Guid.Empty
            ? result : throw new InvalidOperationException("RecipeLifecycleIdentityInvalid");

    private static bool IsLifecycleHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string LifecycleNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal sealed record RecipeLifecycleStoredRow(RecipeLifecycleRecord Record, string PayloadHash,
        byte[] Payload, Guid CommandEventId, long CommandAuditSequence, string CommandAuditHash,
        Guid AuthorizationEventId, long AuthorizationAuditSequence, string AuthorizationAuditHash,
        long AuditSequence, string AuditHash);
}
