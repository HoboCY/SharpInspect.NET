using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record RecipeSelectionCommandState(bool Enabled, IReadOnlyList<RecipeSelectionRevision> Revisions,
    IReadOnlyList<RecipeReleaseRecord> Releases, long ReleaseHighWatermark, RecipeContractReference? PlcContract);
internal sealed record RecipeSelectionMutation(RecipeSelectionRevision Revision);

internal sealed partial class SqliteCommandStore
{
    internal const string RecipeSelectionSchemaSql = @"
        CREATE TABLE recipe_selection_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumRevisions INTEGER NOT NULL CHECK(MaximumRevisions>0),
            MaximumHandshakeEvents INTEGER NOT NULL CHECK(MaximumHandshakeEvents>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE recipe_selection_revisions(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            RevisionId TEXT NOT NULL UNIQUE CHECK(length(RevisionId)=36),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64), Payload TEXT NOT NULL,
            CommandEventId TEXT NOT NULL UNIQUE CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL UNIQUE CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64));
        CREATE TABLE recipe_change_events(
            Position INTEGER PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            EventId TEXT NOT NULL UNIQUE CHECK(length(EventId)=36),
            RequestIdentityHash TEXT NOT NULL CHECK(length(RequestIdentityHash)=64),
            RequestContextHash TEXT NOT NULL CHECK(length(RequestContextHash)=64),
            Kind INTEGER NOT NULL CHECK(Kind BETWEEN 1 AND 7),
            ContentHash TEXT NOT NULL UNIQUE CHECK(length(ContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64), Payload TEXT NOT NULL,
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64));
        CREATE INDEX ix_recipe_change_request ON recipe_change_events(RequestIdentityHash,Position);
        CREATE INDEX ix_recipe_change_episode ON recipe_change_events(RequestContextHash,Kind);
        CREATE TRIGGER recipe_selection_config_immutable_update BEFORE UPDATE ON recipe_selection_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeSelectionConfiguration'); END;
        CREATE TRIGGER recipe_selection_config_immutable_delete BEFORE DELETE ON recipe_selection_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeSelectionConfiguration'); END;
        CREATE TRIGGER recipe_selection_revision_immutable_update BEFORE UPDATE ON recipe_selection_revisions BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeSelectionRevision'); END;
        CREATE TRIGGER recipe_selection_revision_immutable_delete BEFORE DELETE ON recipe_selection_revisions BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeSelectionRevision'); END;
        CREATE TRIGGER recipe_change_event_immutable_update BEFORE UPDATE ON recipe_change_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeChangeEvent'); END;
        CREATE TRIGGER recipe_change_event_immutable_delete BEFORE DELETE ON recipe_change_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeChangeEvent'); END;";

    internal ValueTask<IdentityWriteResult> UpdateRecipeSelectionCommandAsync(ChangeRecipeSelectionCommand command,
        Func<IdentityAuthorityState, RecipeSelectionCommandState, bool, IdentityUpdate> update,
        CancellationToken token, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), token, deadline);

    internal static void InitializeRecipeSelectionSchema(sqlite3 database, RecipeSelectionStoreOptions options,
        StoreDeadline deadline, AuditIntegrityPolicy policy, IAuditSigningKey key)
    {
        options.Validate();
        SqliteNative.Execute(database, RecipeSelectionSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"INSERT INTO recipe_selection_store_config
            (Id,FormatVersion,MaximumRevisions,MaximumHandshakeEvents,MaximumTotalBytes,BindingHash)
            VALUES(1,1,?,?,?,?);", deadline, N(options.MaximumRevisions), N(options.MaximumHandshakeEvents),
            N(options.MaximumTotalBytes), options.BindingHash);
        AuditChainDatabase.AppendRecipeSelectionMetadata(database, policy, key,
            "RecipeSelectionStoreActivated", options.EncodeActivationPayload(), options, deadline);
    }

    internal static void RequireConfiguredRecipeSelections(sqlite3 database, RecipeSelectionStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"SELECT FormatVersion,MaximumRevisions,
            MaximumHandshakeEvents,MaximumTotalBytes,BindingHash FROM recipe_selection_store_config
            WHERE Id=1 LIMIT 2;", deadline, value => new[]
            { SqliteNative.ColumnText(value, 0), SqliteNative.ColumnText(value, 1), SqliteNative.ColumnText(value, 2),
                SqliteNative.ColumnText(value, 3), SqliteNative.ColumnText(value, 4) });
        AuditChainDatabase.Require(rows.Count == 1 && rows[0].SequenceEqual(new[]
            { "1", N(options.MaximumRevisions), N(options.MaximumHandshakeEvents), N(options.MaximumTotalBytes), options.BindingHash }),
            "RecipeSelectionConfigurationMismatch");
    }

    internal RecipeSelectionCommandState ReadRecipeSelectionCommandState(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.RecipeSelections is not { } options)
            return new(false, Array.Empty<RecipeSelectionRevision>(), Array.Empty<RecipeReleaseRecord>(), 0, null);
        RequireConfiguredRecipeSelections(database, options, deadline);
        var rows = ReadRecipeSelectionRows(database, options, deadline);
        var releases = ReadRecipeReleaseRows(database, _options.RecipeReleases!, deadline).Select(value => value.Record).ToArray();
        var contract = ReadPlcResultContractRows(database, _options.PlcResultContracts!, deadline).LastOrDefault()?.Revision.Reference;
        return new(true, rows.Select(value => value.Revision).ToArray(), releases,
            releases.LastOrDefault()?.Position ?? 0, contract);
    }

    private void AppendRecipeSelectionIdentityMutation(sqlite3 database, IdentityUpdate update,
        RecipeSelectionCommandState state, ChangeRecipeSelectionCommand command, StoreDeadline deadline)
    {
        var revision = update.RecipeSelection?.Revision ?? throw new InvalidOperationException("RecipeSelectionMutationMissing");
        var options = _options.RecipeSelections ?? throw new InvalidOperationException("RecipeSelectionConfigurationRequired");
        if (!state.Enabled || update.CommandFacts is not { Count: 1 } || update.Events.Count != 1 ||
            update.CommandFacts[0].CommandKind != AuditedCommandKind.ChangeRecipeSelection ||
            update.CommandFacts[0].Phase != CommandAuditPhase.Outcome ||
            update.CommandFacts[0].Disposition != CommandDisposition.Accepted ||
            update.Events[0].Kind != IdentityEventKind.RecipeSelectionChanged ||
            revision.OperationId != command.CorrelationId || revision.AuthorizationTarget != command.AuthorizationTarget ||
            revision.Policy.ContentHash != command.Policy.ContentHash || revision.Map?.ContentHash != command.Map?.ContentHash ||
            revision.Position != state.Revisions.Count + 1L || revision.Previous != state.Revisions.LastOrDefault()?.Reference ||
            revision.ReleaseHighWatermark != state.ReleaseHighWatermark)
            throw new InvalidOperationException("RecipeSelectionIdentityMutationMismatch");
        ValidateRecipeSelectionRevision(state.Revisions.LastOrDefault(), revision, state.Releases);
        var rows = ReadRecipeSelectionRows(database, options, deadline);
        var payload = RecipeSelectionStorageCodec.Encode(revision);
        if (rows.Count >= options.MaximumRevisions) throw new InvalidOperationException("RecipeSelectionRevisionCapacityExceeded");
        RequireRecipeSelectionTotalCapacity(database, options, payload.Length, deadline);
        var commandAudit = ReadRecipeSelectionCommandReference(database, update.CommandFacts[0].EventId, revision, deadline);
        var authorization = ReadRecipeSelectionAuthorizationReference(database, update.Events[0].EventId, revision, deadline);
        var provisional = new RecipeSelectionStoredRevision(revision, rows.LastOrDefault()?.AuditHash,
            Convert.ToHexString(SHA256.HashData(payload)), payload, update.CommandFacts[0].EventId,
            commandAudit.Sequence, commandAudit.Hash, update.Events[0].EventId, authorization.Sequence, authorization.Hash, 0, "");
        var audit = AuditChainDatabase.AppendRecipeSelectionMetadata(database, _policy!, _signingKey!,
            "RecipeSelectionRevision", EncodeRecipeSelectionAudit(provisional), options, deadline);
        var row = provisional with { AuditSequence = audit.Sequence, AuditHash = audit.Hash };
        AuditChainDatabase.Execute(database, @"INSERT INTO recipe_selection_revisions
            (Position,PreviousHash,RevisionId,OperationId,ContentHash,PayloadHash,Payload,CommandEventId,
             CommandAuditSequence,CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,AuthorizationAuditHash,
             AuditSequence,AuditHash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            N(revision.Position), row.PreviousHash, revision.RevisionId.ToString("D"), revision.OperationId.ToString("D"),
            revision.ContentHash, row.PayloadHash, Convert.ToBase64String(payload), row.CommandEventId.ToString("D"),
            N(row.CommandAuditSequence), row.CommandAuditHash, row.AuthorizationEventId.ToString("D"),
            N(row.AuthorizationAuditSequence), row.AuthorizationAuditHash, N(row.AuditSequence), row.AuditHash);
    }

    internal static IReadOnlyList<RecipeSelectionStoredRevision> ReadRecipeSelectionRows(sqlite3 database,
        RecipeSelectionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        if (AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM recipe_selection_revisions;", deadline) > options.MaximumRevisions)
            throw new InvalidOperationException("RecipeSelectionRevisionCapacityExceeded");
        var rows = AuditChainDatabase.Read(database, @"SELECT Position,PreviousHash,RevisionId,OperationId,ContentHash,
            PayloadHash,Payload,CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
            AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash FROM recipe_selection_revisions
            ORDER BY Position;", deadline, value =>
        {
            string Text(int column) => SqliteNative.ColumnText(value, column) ?? throw new InvalidOperationException("RecipeSelectionColumnMissing");
            var payload = DecodeRecipeSelectionPayload(Text(6));
            var revision = RecipeSelectionStorageCodec.Decode(payload);
            if (revision.Position != SqliteNative.ColumnInt64(value, 0) || revision.RevisionId.ToString("D") != Text(2) ||
                revision.OperationId.ToString("D") != Text(3) || revision.ContentHash != Text(4) ||
                Convert.ToHexString(SHA256.HashData(payload)) != Text(5))
                throw new InvalidOperationException("RecipeSelectionIndexedPayloadMismatch");
            return new RecipeSelectionStoredRevision(revision, SqliteNative.ColumnText(value, 1), Text(5), payload,
                Guid.ParseExact(Text(7), "D"), SqliteNative.ColumnInt64(value, 8), Text(9), Guid.ParseExact(Text(10), "D"),
                SqliteNative.ColumnInt64(value, 11), Text(12), SqliteNative.ColumnInt64(value, 13), Text(14));
        });
        if (rows.Sum(value => (long)value.Payload.Length) > options.MaximumTotalBytes)
            throw new InvalidOperationException("RecipeSelectionTotalCapacityExceeded");
        return rows;
    }

    internal static byte[] DecodeRecipeSelectionPayload(string text)
    {
        if (text.Length > ((RecipeSelectionStoreOptions.MaximumPayloadBytes + 2) / 3) * 4)
            throw new InvalidOperationException("RecipeSelectionPayloadCapacityExceeded");
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length is < 1 or > RecipeSelectionStoreOptions.MaximumPayloadBytes || Convert.ToBase64String(bytes) != text)
            throw new InvalidOperationException("RecipeSelectionPayloadEncodingInvalid");
        return bytes;
    }

    private static byte[] EncodeRecipeSelectionAudit(RecipeSelectionStoredRevision row) => AuditCanonical.Encode(
        "RecipeSelectionRevisionAuditV1", N(row.Revision.Position), row.Revision.ContentHash, row.PreviousHash,
        row.PayloadHash, row.CommandEventId.ToString("D"), N(row.CommandAuditSequence), row.CommandAuditHash,
        row.AuthorizationEventId.ToString("D"), N(row.AuthorizationAuditSequence), row.AuthorizationAuditHash,
        Convert.ToBase64String(row.Payload));

    private static void RequireRecipeSelectionTotalCapacity(sqlite3 database, RecipeSelectionStoreOptions options,
        int additionalBytes, StoreDeadline deadline, long? reservedEvents = null)
    {
        // Base64 length is an upper bound on stored binary payload size; capacity
        // admission deliberately keeps this conservative, including both streams.
        var used = AuditChainDatabase.Scalar(database, @"SELECT
            COALESCE((SELECT SUM(length(Payload)) FROM recipe_selection_revisions),0) +
            COALESCE((SELECT SUM(length(Payload)) FROM recipe_change_events),0);", deadline);
        var reserve = reservedEvents ?? ReadRecipeChangeAuditReserve(database, deadline);
        var encodedAdditional = checked((additionalBytes + 2L) / 3 * 4);
        var perEvent = (RecipeSelectionStorageCodec.MaximumHandshakePayloadBytes + 2L) / 3 * 4;
        if (checked(used + encodedAdditional + reserve * perEvent) > options.MaximumTotalBytes)
            throw new InvalidOperationException("RecipeSelectionTotalCapacityExceeded");
    }

    private static void ValidateRecipeSelectionRevision(RecipeSelectionRevision? previous, RecipeSelectionRevision revision,
        IReadOnlyList<RecipeReleaseRecord> releases)
    {
        var known = releases.Where(value => value.Position <= revision.ReleaseHighWatermark).ToDictionary(value => value.ReleaseId);
        if (revision.ReleaseHighWatermark != known.Values.Select(value => value.Position).DefaultIfEmpty(0).Max())
            throw new InvalidOperationException("RecipeSelectionReleaseSnapshotMismatch");
        foreach (var proof in revision.Validations)
            if (!known.TryGetValue(proof.ReleaseId, out var release) || release.Recipe != proof.Recipe ||
                release.ContentHash != proof.ReleaseRecordContentHash)
                throw new InvalidOperationException("RecipeSelectionValidationReleaseMismatch");
        var affected = (previous?.Map?.Entries ?? Array.AsReadOnly(Array.Empty<RecipeSelectionMapEntry>()))
            .Concat(revision.Map?.Entries ?? Array.AsReadOnly(Array.Empty<RecipeSelectionMapEntry>())).ToArray();
        var actual = revision.Validations.Select(value => value.ReleaseId).ToHashSet();
        if (!actual.SetEquals(affected.Select(value => value.ReleaseId)))
            throw new InvalidOperationException("RecipeSelectionAffectedReleaseCoverageMismatch");
        foreach (var entry in affected)
            if (!known.TryGetValue(entry.ReleaseId, out var release) || release.Recipe != entry.Recipe ||
                release.ContentHash != entry.ReleaseRecordContentHash)
                throw new InvalidOperationException("RecipeSelectionExactReleaseMissing");
    }

    private static (long Sequence, string Hash) ReadRecipeSelectionCommandReference(sqlite3 database,
        Guid eventId, RecipeSelectionRevision revision, StoreDeadline deadline)
    {
        var facts = AuditChainDatabase.Read(database, @"SELECT Position,CorrelationId,CommandKind,Phase,Disposition,
            AuthenticatedHumanPrincipalId,ClaimedSessionId,ClaimedStepUpGrantId,Source,OccurredAtUtc
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline,
            row => Enumerable.Range(0, 10).Select(column => SqliteNative.ColumnText(row, column)).ToArray(), eventId.ToString("D"));
        var expected = new[] { revision.OperationId.ToString("D"), N((int)AuditedCommandKind.ChangeRecipeSelection),
            N((int)CommandAuditPhase.Outcome), N((int)CommandDisposition.Accepted), revision.ActorPrincipalId.ToString("D"),
            revision.ActorSessionId.ToString("D"), revision.StepUpGrantId.ToString("D"), N((int)CommandSource.PhysicalConsole),
            revision.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) };
        if (facts.Count != 1 || !facts[0].Skip(1).SequenceEqual(expected))
            throw new InvalidOperationException("RecipeSelectionCommandBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", deadline,
            row => (Sequence: SqliteNative.ColumnInt64(row, 0), Hash: SqliteNative.ColumnText(row, 1)!), facts[0][0]);
        if (audit.Count != 1) throw new InvalidOperationException("RecipeSelectionCommandAuditMissing");
        return audit[0];
    }

    private static (long Sequence, string Hash) ReadRecipeSelectionAuthorizationReference(sqlite3 database,
        Guid eventId, RecipeSelectionRevision revision, StoreDeadline deadline)
    {
        var station = ReadContractStationId(database, deadline);
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' ORDER BY Sequence;", deadline,
            row => (Sequence: SqliteNative.ColumnInt64(row, 0), Position: SqliteNative.ColumnInt64(row, 1),
                Payload: SqliteNative.ColumnText(row, 2)!, Hash: SqliteNative.ColumnText(row, 3)!));
        var matches = new List<(long Sequence, string Hash)>();
        foreach (var row in rows)
        {
            var payload = Convert.FromBase64String(row.Payload);
            var fields = DecodeContractIdentityFieldsForSelection(payload);
            if (fields is null || fields[1] != eventId.ToString("D")) continue;
            IdentityAuditEvent.VerifyPayload(payload, row.Position, station, RecipeSelectionStoreOptions.SchemaVersion);
            if (fields[2] != IdentityEventKind.RecipeSelectionChanged.ToString() || fields[3] != revision.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) ||
                fields[4] != station || fields[25] != revision.ActorSessionId.ToString("D") ||
                fields[27] != revision.AuthorizationPolicy.Id || fields[28] != revision.AuthorizationPolicy.Version ||
                fields[29] != revision.AuthorizationPolicy.ContentHash || fields[30] != revision.ActorPrincipalId.ToString("D") ||
                fields[31] != revision.OperationId.ToString("D") || fields[32] != revision.StepUpGrantId.ToString("D") ||
                fields[33] != Permission.ManageRecipeSelectionMap.ToString() || fields[35] != N(revision.ActorAuthorizationRevision) ||
                fields[37] != revision.AuthorizationTarget || fields[38] != revision.OperationId.ToString("D") ||
                fields[39] != AuditedCommandKind.ChangeRecipeSelection.ToString() || fields[42] != revision.OperationId.ToString("D"))
                throw new InvalidOperationException("RecipeSelectionAuthorizationBindingMismatch");
            matches.Add((row.Sequence, row.Hash));
        }
        if (matches.Count != 1) throw new InvalidOperationException("RecipeSelectionAuthorizationAuditMissing");
        return matches[0];
    }

    private static string?[]? DecodeContractIdentityFieldsForSelection(byte[] payload)
    {
        try { return DecodeContractIdentityFields(payload); }
        catch (InvalidOperationException) { return null; } // A PLC v3 envelope is not a human Map authorization.
    }

    internal sealed record RecipeSelectionStoredRevision(RecipeSelectionRevision Revision, string? PreviousHash,
        string PayloadHash, byte[] Payload, Guid CommandEventId, long CommandAuditSequence, string CommandAuditHash,
        Guid AuthorizationEventId, long AuthorizationAuditSequence, string AuthorizationAuditHash,
        long AuditSequence, string AuditHash);
}
