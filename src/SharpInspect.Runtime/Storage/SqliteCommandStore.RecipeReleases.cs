using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The schema-16 released-recipe ledger.  All methods in this file are called
/// from the authoritative writer transaction or from a read-only verification
/// transaction.  The release payload is a complete immutable snapshot; the
/// normalized columns are indexes and binding evidence, never a second source
/// of recipe truth.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal const string RecipeReleaseEventKind = "RecipeReleaseEvent";

    internal const string RecipeReleaseSchemaSql = @"
        CREATE TABLE recipe_release_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            GovernancePolicyId TEXT NOT NULL,
            GovernancePolicyVersion TEXT NOT NULL,
            GovernancePolicyMode INTEGER NOT NULL CHECK(GovernancePolicyMode IN (0,1)),
            GovernancePolicyHash TEXT NOT NULL CHECK(length(GovernancePolicyHash)=64),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE recipe_release_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            ReleaseId TEXT NOT NULL UNIQUE CHECK(length(ReleaseId)=36),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            RecipeKey TEXT NOT NULL,
            RecipeVersion INTEGER NOT NULL CHECK(RecipeVersion>0),
            SourceDraftId TEXT NOT NULL CHECK(length(SourceDraftId)=36),
            SourceRevision INTEGER NOT NULL CHECK(SourceRevision>0),
            SourceRevisionContentHash TEXT NOT NULL CHECK(length(SourceRevisionContentHash)=64),
            SourceContentHash TEXT NOT NULL CHECK(length(SourceContentHash)=64),
            GovernancePolicyId TEXT NOT NULL,
            GovernancePolicyVersion TEXT NOT NULL,
            GovernancePolicyMode INTEGER NOT NULL CHECK(GovernancePolicyMode IN (0,1)),
            GovernancePolicyHash TEXT NOT NULL CHECK(length(GovernancePolicyHash)=64),
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
            UNIQUE(RecipeKey,RecipeVersion),
            UNIQUE(SourceDraftId,SourceRevision));
        CREATE INDEX ix_recipe_release_recipe ON recipe_release_events(RecipeKey,RecipeVersion);
        CREATE INDEX ix_recipe_release_source ON recipe_release_events(SourceDraftId,SourceRevision);
        CREATE INDEX ix_recipe_release_audit ON recipe_release_events(AuditSequence);
        CREATE TRIGGER recipe_release_config_immutable_update BEFORE UPDATE
            ON recipe_release_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeReleaseConfiguration');
        END;
        CREATE TRIGGER recipe_release_config_immutable_delete BEFORE DELETE
            ON recipe_release_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeReleaseConfiguration');
        END;
        CREATE TRIGGER recipe_release_event_immutable_update BEFORE UPDATE
            ON recipe_release_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeReleaseEvent');
        END;
        CREATE TRIGGER recipe_release_event_immutable_delete BEFORE DELETE
            ON recipe_release_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeReleaseEvent');
        END;";

    internal ValueTask<IdentityWriteResult> UpdateRecipeReleaseCommandAsync(ReleaseRecipeCommand command,
        Func<IdentityAuthorityState, RecipeReleaseCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    internal static void InitializeRecipeReleaseSchema(sqlite3 database,
        RecipeReleaseStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, RecipeReleaseSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO recipe_release_store_config
                (Id,FormatVersion,GovernancePolicyId,GovernancePolicyVersion,GovernancePolicyMode,
                 GovernancePolicyHash,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?,?,?,?,?);", deadline,
            RecipeReleaseStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.Policy.Id, options.Policy.Version, ((int)options.Policy.Mode).ToString(CultureInfo.InvariantCulture),
            options.Policy.ContentHash, options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendRecipeReleaseStoreActivation(database, policy, signingKey, options, deadline);
    }

    internal static void RequireConfiguredRecipeReleases(sqlite3 database,
        RecipeReleaseStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,GovernancePolicyId,GovernancePolicyVersion,GovernancePolicyMode,
                GovernancePolicyHash,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM recipe_release_store_config WHERE Id=1 LIMIT 2;", deadline, statement =>
            new RecipeReleaseConfig(
                SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                SqliteNative.ColumnText(statement, 2) ?? string.Empty, SqliteNative.ColumnInt64(statement, 3),
                SqliteNative.ColumnText(statement, 4) ?? string.Empty, SqliteNative.ColumnInt64(statement, 5),
                SqliteNative.ColumnInt64(statement, 6), SqliteNative.ColumnInt64(statement, 7),
                SqliteNative.ColumnText(statement, 8) ?? string.Empty)).SingleOrDefault();
        AuditChainDatabase.Require(configured is not null &&
            configured.FormatVersion == RecipeReleaseStoreOptions.FormatVersion &&
            configured.PolicyId == options.Policy.Id && configured.PolicyVersion == options.Policy.Version &&
            configured.PolicyMode == (int)options.Policy.Mode && configured.PolicyHash == options.Policy.ContentHash &&
            configured.MaximumEntries == options.MaximumEntries &&
            configured.MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured.MaximumTotalBytes == options.MaximumTotalBytes &&
            configured.BindingHash == options.BindingHash, "RecipeReleaseConfigurationMismatch");
    }

    internal static void VerifyRecipeReleaseActivationPayload(sqlite3 database, byte[] payload,
        RecipeReleaseStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()))
            throw new InvalidOperationException("RecipeReleaseActivationBindingMismatch");
        RequireConfiguredRecipeReleases(database, options, deadline);
    }

    internal RecipeReleaseCommandState ReadRecipeReleaseCommandState(sqlite3 database,
        ReleaseRecipeCommand command, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(command);
        var releaseOptions = _options.RecipeReleases;
        if (releaseOptions is null)
            return new(false, Array.Empty<RecipeDraftRevision>(), Array.Empty<RecipeReleaseRecord>(),
                Array.Empty<CalibrationAcceptancePolicyRevision>());
        releaseOptions.Validate();
        RequireConfiguredRecipeReleases(database, releaseOptions, deadline);
        var draftOptions = _options.RecipeDrafts ??
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        ValidateRecipeDraftHistory(database, draftOptions, deadline);
        var history = ReadAllRecipeDraftHistory(database, deadline);
        var policies = ReadCalibrationAcceptancePolicies(database, _options.CalibrationGovernance, deadline);
        ValidateRecipeReleaseHistory(database, releaseOptions, draftOptions, policies, history, deadline,
            _options.CalibrationGovernance);
        var records = ReadRecipeReleaseRows(database, releaseOptions, deadline)
            .Select(value => value.Record).ToArray();
        return new(true, history, records, policies, ReadRecipeLifecycleRecords(database, _options.RecipeLifecycle, deadline));
    }

    private void AppendRecipeReleaseIdentityMutation(sqlite3 database, IdentityUpdate update,
        RecipeReleaseCommandState state, ReleaseRecipeCommand command, StoreDeadline deadline)
    {
        var mutation = update.RecipeRelease ?? throw new InvalidOperationException("RecipeReleaseMutationMissing");
        var record = mutation.Record ?? throw new InvalidOperationException("RecipeReleaseMutationMissing");
        var options = _options.RecipeReleases ?? throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
        var draftOptions = _options.RecipeDrafts ?? throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (!state.Enabled || update.CommandFacts is not { Count: 1 } || update.Events.Count != 1 ||
            update.CommandFacts[0].Disposition != CommandDisposition.Accepted ||
            update.CommandFacts[0].Phase != CommandAuditPhase.Outcome ||
            update.CommandFacts[0].CorrelationId != record.OperationId ||
            update.Events[0].OperationId != record.OperationId ||
            record.Position != state.Releases.Count + 1L)
            throw new InvalidOperationException("RecipeReleaseIdentityMutationMismatch");

        ValidateRecipeReleaseCommandBinding(command, record, update.Events[0], update.CommandFacts[0]);
        var history = state.DraftHistory;
        RecipeReleaseProjection.ValidateRecord(record, history, options, draftOptions,
            state.CalibrationPolicies);
        var payload = RecipeReleaseStorageCodec.Encode(record);
        if (payload.Length > options.MaximumPayloadBytes || payload.Length > RecipeReleaseStorageCodec.MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeReleasePayloadCapacityExceeded");

        var entries = ReadRecipeReleaseRows(database, options, deadline);
        if (entries.Count >= options.MaximumEntries)
            throw new InvalidOperationException("RecipeReleaseEntryCapacityExceeded");
        var total = entries.Aggregate(0L, (sum, value) => checked(sum + value.Payload.Length));
        if (checked(total + payload.Length) > options.MaximumTotalBytes)
            throw new InvalidOperationException("RecipeReleaseTotalCapacityExceeded");
        if (entries.Any(value => value.Record.Source.DraftId == record.Source.DraftId &&
                value.Record.Source.Revision == record.Source.Revision))
            throw new InvalidOperationException("RecipeReleaseDraftAlreadyReleased");
        if (entries.Any(value => value.Record.Recipe.Id == record.Recipe.Id &&
                value.Record.Recipe.Version == record.Recipe.Version))
            throw new InvalidOperationException("RecipeReleaseRecipeVersionConflict");

        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        var commandReference = ReadReleaseCommandReference(database, update.CommandFacts[0].EventId,
            record, deadline);
        var authorizationReference = ReadReleaseAuthorizationReference(database, update.Events[0].EventId,
            record, _policy!.StationId, deadline);
        var previousHash = entries.Count == 0 ? null : entries[^1].AuditHash;
        var binding = EncodeRecipeReleaseAuditBinding(record.Position, record, previousHash, payloadHash,
            update.CommandFacts[0].EventId, commandReference.Sequence, commandReference.Hash,
            update.Events[0].EventId, authorizationReference.Sequence, authorizationReference.Hash, payload);
        var auditSequence = AuditChainDatabase.AppendRecipeReleaseLedgerEntry(database, _policy!, _signingKey!,
            record.Position, binding, deadline);
        var auditHash = ReadRecipeReleaseAuditHash(database, auditSequence, record.Position, deadline);
        var stored = new RecipeReleaseStoredEvent(record.Position, record, previousHash, payloadHash, payload,
            update.CommandFacts[0].EventId, commandReference.Sequence, commandReference.Hash,
            update.Events[0].EventId, authorizationReference.Sequence, authorizationReference.Hash,
            auditSequence, auditHash);
        InsertRecipeReleaseEvent(database, stored, deadline);
        ValidateRecipeReleaseAuditReferences(database, stored, deadline);
    }

    internal static void ValidateRecipeReleaseCommandBinding(ReleaseRecipeCommand command,
        RecipeReleaseRecord record, IdentityAuditEvent authorization, CommandAuditFact fact)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(fact);
        var fieldsMatch = record.OperationId == command.CorrelationId &&
            record.Source.DraftId == command.DraftId &&
            record.Source.Revision == command.ExpectedRevision &&
            record.Source.RevisionContentHash == command.ExpectedRevisionContentHash &&
            record.GovernancePolicy.Reference == command.GovernancePolicy &&
            record.AuthorizationTarget == command.AuthorizationTarget;
        if (!fieldsMatch || authorization.Kind != IdentityEventKind.RecipeReleased ||
            authorization.ActionTargetId != command.AuthorizationTarget ||
            authorization.OperationId != command.CorrelationId ||
            authorization.CommandCorrelationId != command.CorrelationId ||
            authorization.BoundCommandCorrelationId != command.CorrelationId ||
            authorization.PrincipalId != record.ApproverPrincipalId ||
            authorization.ActorPrincipalId != record.ApproverPrincipalId ||
            authorization.SessionId != record.ApproverSessionId ||
            authorization.AuthorizationRevision != record.ApproverAuthorizationRevision ||
            authorization.OccurredAtUtc != record.ReleasedAtUtc ||
            authorization.StepUpGrantId != command.Invocation.StepUpGrantId ||
            fact.CorrelationId != command.CorrelationId ||
            fact.CommandKind != AuditedCommandKind.ReleaseRecipe ||
            fact.Disposition != CommandDisposition.Accepted ||
            fact.Phase != CommandAuditPhase.Outcome ||
            fact.AuthenticatedHumanPrincipalId != record.ApproverPrincipalId.ToString("D") ||
            fact.ClaimedSessionId != record.ApproverSessionId ||
            fact.ClaimedStepUpGrantId != command.Invocation.StepUpGrantId ||
            command.Invocation.StepUpGrantId is null)
            throw new InvalidOperationException("RecipeReleaseCommandBindingMismatch");
    }

    internal static void VerifyRecipeReleaseAuditPayload(sqlite3 database, long position, byte[] auditPayload,
        RecipeReleaseStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditPayload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var stored = ReadRecipeReleaseRows(database, options, deadline)
            .SingleOrDefault(value => value.Record.Position == position);
        if (stored is null || stored.Record.Position != position)
            throw new InvalidOperationException("RecipeReleaseAuditBindingMismatch");
        var expected = EncodeRecipeReleaseAuditBinding(stored.Record.Position, stored.Record,
            stored.PreviousHash, stored.PayloadHash, stored.CommandEventId, stored.CommandAuditSequence,
            stored.CommandAuditHash, stored.AuthorizationEventId, stored.AuthorizationAuditSequence,
            stored.AuthorizationAuditHash, stored.Payload);
        if (!auditPayload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("RecipeReleaseAuditPayloadMismatch");
        ValidateRecipeReleaseAuditReferences(database, stored, deadline);
    }

    internal static void ValidateRecipeReleaseHistory(sqlite3 database, RecipeReleaseStoreOptions options,
        RecipeDraftStoreOptions draftOptions, IReadOnlyList<CalibrationAcceptancePolicyRevision> policies,
        IReadOnlyList<RecipeDraftRevision>? history, StoreDeadline deadline,
        CalibrationGovernanceStoreOptions? governanceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(draftOptions);
        ArgumentNullException.ThrowIfNull(policies);
        options.Validate();
        RequireConfiguredRecipeReleases(database, options, deadline);
        var actualHistory = history ?? ReadAllRecipeDraftHistory(database, deadline);
        var rows = ReadRecipeReleaseRows(database, options, deadline);
        var previous = (string?)null;
        var seenSources = new HashSet<(Guid DraftId, long Revision)>();
        var seenReferences = new HashSet<(string Key, long Version)>();
        var total = 0L;
        var governedPolicyRows = governanceOptions is not null &&
            AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline)
            ? ReadCalibrationGovernanceRows(database, governanceOptions, deadline)
            : null;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Record.Position != index + 1L)
                throw new InvalidOperationException("RecipeReleasePositionGap");
            if (!string.Equals(row.PreviousHash, previous, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeReleasePreviousHashMismatch");
            if (!seenSources.Add((row.Record.Source.DraftId, row.Record.Source.Revision)))
                throw new InvalidOperationException("RecipeReleaseDraftAlreadyReleased");
            if (!long.TryParse(row.Record.Recipe.Version, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var recipeVersion) || recipeVersion < 1 ||
                !seenReferences.Add((row.Record.Recipe.Id, recipeVersion)))
                throw new InvalidOperationException("RecipeReleaseRecipeVersionConflict");
            total = checked(total + row.Payload.Length);
            if (total > options.MaximumTotalBytes)
                throw new InvalidOperationException("RecipeReleaseTotalCapacityExceeded");
            var policiesAtRelease = governedPolicyRows is null
                ? policies
                : governedPolicyRows.Where(value => value.AuditSequence < row.AuditSequence)
                    .Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload))
                    .OfType<CalibrationAcceptancePolicyRevision>().ToArray();
            RecipeReleaseProjection.ValidateRecord(row.Record, actualHistory, options, draftOptions,
                policiesAtRelease);
            ValidateRecipeReleaseAuditReferences(database, row, deadline);
            previous = row.AuditHash;
        }
    }

    internal static IReadOnlyList<RecipeDraftRevision> ReadAllRecipeDraftHistory(sqlite3 database,
        StoreDeadline deadline)
    {
        var rows = ReadRecipeDraftRow(database, "ORDER BY Position", deadline);
        return rows.Select(row => ToPublicRevision(database, row, deadline)).ToArray();
    }

    internal static IReadOnlyList<CalibrationAcceptancePolicyRevision> ReadCalibrationAcceptancePolicies(
        sqlite3 database, CalibrationGovernanceStoreOptions? governanceOptions, StoreDeadline deadline)
    {
        var schema = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline);
        if (!AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline))
            return Array.Empty<CalibrationAcceptancePolicyRevision>();
        if (governanceOptions is null)
            throw new InvalidOperationException("CalibrationGovernanceConfigurationRequired");
        RequireConfiguredCalibrationGovernance(database, governanceOptions, deadline);
        ValidateCalibrationGovernanceHistory(database, governanceOptions, deadline);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Kind,Payload FROM calibration_governance_events ORDER BY Position;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Payload: Convert.FromBase64String(SqliteNative.ColumnText(statement, 1) ?? string.Empty)));
        // The governance ledger is optional in schema 16 through schema 18.  Only a configured
        // and already integrity-checked ledger contributes dependency facts.
        if (schema is not (RecipeReleaseStoreOptions.SchemaVersion or PlcResultContractStoreOptions.SchemaVersion
            or RecipeActivationStoreOptions.SchemaVersion or PreviewSessionStoreOptions.SchemaVersion or CalibrationImportStoreOptions.SchemaVersion or
            ManualInspectionStoreOptions.SchemaVersion or ProductionAdmissionStoreOptions.SchemaVersion or StationQualificationStoreOptions.SchemaVersion or RecipeTransferStoreOptions.SchemaVersion or TraceStoragePolicyStoreOptions.SchemaVersion or QualificationCycleStoreOptions.SchemaVersion or PlcCommunicationStoreOptions.SchemaVersion or ProductionInspectionStoreOptions.SchemaVersion or ProductionRecoveryStoreOptions.SchemaVersion or RecipeSelectionStoreOptions.SchemaVersion or PartIdentityStoreOptions.SchemaVersion or ProductionArmStoreOptions.SchemaVersion or RecipeLifecycleStoreOptions.SchemaVersion))
            throw new InvalidOperationException("CalibrationGovernanceSchemaInvalid");
        return rows.Select(row => CalibrationGovernanceCodec.Decode(row.Kind, row.Payload))
            .OfType<CalibrationAcceptancePolicyRevision>().ToArray();
    }

    internal static List<RecipeReleaseStoredEvent> ReadRecipeReleaseRows(sqlite3 database,
        RecipeReleaseStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,ReleaseId,OperationId,RecipeKey,RecipeVersion,SourceDraftId,SourceRevision,
                SourceRevisionContentHash,SourceContentHash,GovernancePolicyId,GovernancePolicyVersion,
                GovernancePolicyMode,GovernancePolicyHash,RecordContentHash,PayloadHash,Payload,
                CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash
            FROM recipe_release_events ORDER BY Position;", deadline, statement =>
        {
            var encoded = SqliteNative.ColumnText(statement, 16);
            if (encoded is null || encoded.Length == 0)
                throw new InvalidOperationException("RecipeReleasePayloadInvalid");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded);
                if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                    throw new InvalidOperationException("RecipeReleasePayloadCanonicalMismatch");
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("RecipeReleasePayloadInvalid", exception); }
            if (payload.Length < 1 || payload.Length > options.MaximumPayloadBytes ||
                payload.Length > RecipeReleaseStorageCodec.MaximumPayloadBytes)
                throw new InvalidOperationException("RecipeReleasePayloadCapacityExceeded");
            var record = RecipeReleaseStorageCodec.Decode(payload);
            var position = SqliteNative.ColumnInt64(statement, 0);
            var previousHash = SqliteNative.ColumnText(statement, 1);
            var commandEventId = ParseReleaseGuid(statement, 17);
            var commandAuditSequence = SqliteNative.ColumnInt64(statement, 18);
            var commandAuditHash = SqliteNative.ColumnText(statement, 19) ?? string.Empty;
            var authorizationEventId = ParseReleaseGuid(statement, 20);
            var authorizationAuditSequence = SqliteNative.ColumnInt64(statement, 21);
            var authorizationAuditHash = SqliteNative.ColumnText(statement, 22) ?? string.Empty;
            var auditSequence = SqliteNative.ColumnInt64(statement, 23);
            var auditHash = SqliteNative.ColumnText(statement, 24) ?? string.Empty;
            if (record.Position != position || record.ReleaseId != ParseReleaseGuid(statement, 2) ||
                record.OperationId != ParseReleaseGuid(statement, 3) ||
                record.Recipe.Id != (SqliteNative.ColumnText(statement, 4) ?? string.Empty) ||
                record.RecipeVersion != SqliteNative.ColumnInt64(statement, 5) ||
                record.Source.DraftId != ParseReleaseGuid(statement, 6) ||
                record.Source.Revision != SqliteNative.ColumnInt64(statement, 7) ||
                record.Source.RevisionContentHash != (SqliteNative.ColumnText(statement, 8) ?? string.Empty) ||
                record.Source.Content.ContentHash != (SqliteNative.ColumnText(statement, 9) ?? string.Empty) ||
                record.GovernancePolicy.Id != (SqliteNative.ColumnText(statement, 10) ?? string.Empty) ||
                record.GovernancePolicy.Version != (SqliteNative.ColumnText(statement, 11) ?? string.Empty) ||
                (int)record.GovernancePolicy.Mode != SqliteNative.ColumnInt64(statement, 12) ||
                record.GovernancePolicy.ContentHash != (SqliteNative.ColumnText(statement, 13) ?? string.Empty) ||
                record.ContentHash != (SqliteNative.ColumnText(statement, 14) ?? string.Empty) ||
                position < 1 ||
                (previousHash is not null && !IsReleaseHash(previousHash)) ||
                !IsReleaseHash(record.Source.RevisionContentHash) ||
                !IsReleaseHash(record.Source.Content.ContentHash) ||
                !IsReleaseHash(record.GovernancePolicy.ContentHash) ||
                !IsReleaseHash(record.ContentHash) ||
                commandAuditSequence < 1 || !IsReleaseHash(commandAuditHash) ||
                authorizationAuditSequence < 1 || !IsReleaseHash(authorizationAuditHash) ||
                auditSequence < 1 || !IsReleaseHash(auditHash))
                throw new InvalidOperationException("RecipeReleaseRecordBindingMismatch");
            var payloadHash = SqliteNative.ColumnText(statement, 15) ?? string.Empty;
            if (!IsReleaseHash(payloadHash) || payloadHash != Convert.ToHexString(SHA256.HashData(payload)))
                throw new InvalidOperationException("RecipeReleasePayloadHashMismatch");
            return new RecipeReleaseStoredEvent(position, record, previousHash, payloadHash, payload,
                commandEventId, commandAuditSequence, commandAuditHash, authorizationEventId,
                authorizationAuditSequence, authorizationAuditHash, auditSequence, auditHash);
        });
        if (rows.Count > options.MaximumEntries)
            throw new InvalidOperationException("RecipeReleaseEntryCapacityExceeded");
        return rows;
    }

    private static Guid ParseReleaseGuid(sqlite3_stmt statement, int index) =>
        Guid.TryParseExact(SqliteNative.ColumnText(statement, index), "D", out var result) && result != Guid.Empty
            ? result : throw new InvalidOperationException("RecipeReleaseIdentityInvalid");

    private static void InsertRecipeReleaseEvent(sqlite3 database, RecipeReleaseStoredEvent stored,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Execute(database, @"
            INSERT INTO recipe_release_events
                (Position,PreviousHash,ReleaseId,OperationId,RecipeKey,RecipeVersion,SourceDraftId,SourceRevision,
                 SourceRevisionContentHash,SourceContentHash,GovernancePolicyId,GovernancePolicyVersion,
                 GovernancePolicyMode,GovernancePolicyHash,RecordContentHash,PayloadHash,Payload,
                 CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,
                 AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            Number(stored.Position), stored.PreviousHash, stored.Record.ReleaseId.ToString("D"),
            stored.Record.OperationId.ToString("D"), stored.Record.Recipe.Id, Number(stored.Record.RecipeVersion),
            stored.Record.Source.DraftId.ToString("D"), Number(stored.Record.Source.Revision),
            stored.Record.Source.RevisionContentHash, stored.Record.Source.Content.ContentHash,
            stored.Record.GovernancePolicy.Id, stored.Record.GovernancePolicy.Version,
            Number((int)stored.Record.GovernancePolicy.Mode), stored.Record.GovernancePolicy.ContentHash,
            stored.Record.ContentHash, stored.PayloadHash, Convert.ToBase64String(stored.Payload),
            stored.CommandEventId.ToString("D"), Number(stored.CommandAuditSequence), stored.CommandAuditHash,
            stored.AuthorizationEventId.ToString("D"), Number(stored.AuthorizationAuditSequence),
            stored.AuthorizationAuditHash, Number(stored.AuditSequence), stored.AuditHash);
    }

    private static void ValidateRecipeReleaseAuditReferences(sqlite3 database,
        RecipeReleaseStoredEvent row, StoreDeadline deadline)
    {
        var command = ReadReleaseCommandReference(database, row.CommandEventId, row.Record, deadline);
        if (command.Sequence != row.CommandAuditSequence || command.Hash != row.CommandAuditHash)
            throw new InvalidOperationException("RecipeReleaseCommandAuditBindingMismatch");
        var authorization = ReadReleaseAuthorizationReference(database, row.AuthorizationEventId, row.Record,
            ReadReleaseStationId(database, deadline), deadline);
        if (authorization.Sequence != row.AuthorizationAuditSequence || authorization.Hash != row.AuthorizationAuditHash)
            throw new InvalidOperationException("RecipeReleaseAuthorizationAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Kind,ReleasePosition,Payload,Hash
            FROM audit_entries WHERE Sequence=? AND Kind=? AND ReleasePosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Kind: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                Position: SqliteNative.ColumnInt64(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 4) ?? string.Empty),
            Number(row.AuditSequence), RecipeReleaseEventKind, Number(row.Position)).SingleOrDefault();
        if (audit == default || audit.Sequence != row.AuditSequence || audit.Position != row.Position ||
            audit.Hash != row.AuditHash)
            throw new InvalidOperationException("RecipeReleaseAuditBindingMismatch");
        byte[] auditPayload;
        try { auditPayload = Convert.FromBase64String(audit.Payload); }
        catch (FormatException exception) { throw new InvalidOperationException("RecipeReleaseAuditPayloadInvalid", exception); }
        var expected = EncodeRecipeReleaseAuditBinding(row.Position, row.Record, row.PreviousHash,
            row.PayloadHash, row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash,
            row.AuthorizationEventId, row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.Payload);
        if (!auditPayload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("RecipeReleaseAuditPayloadMismatch");
    }

    private static ReleaseCommandAuditReference ReadReleaseCommandReference(sqlite3 database,
        Guid eventId, RecipeReleaseRecord record, StoreDeadline deadline)
    {
        var command = AuditChainDatabase.Read(database, @"
            SELECT Position,CorrelationId,CommandKind,OccurredAtUtc,AuthenticatedHumanPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline, statement =>
        {
            var kind = ReadReleaseEnum<AuditedCommandKind>(SqliteNative.ColumnInt64(statement, 2),
                "RecipeReleaseCommandKindInvalid");
            var phase = ReadReleaseEnum<CommandAuditPhase>(SqliteNative.ColumnInt64(statement, 7),
                "RecipeReleaseCommandPhaseInvalid");
            var disposition = SqliteNative.ColumnInt64Nullable(statement, 8) is { } rawDisposition
                ? ReadReleaseEnum<CommandDisposition>(rawDisposition, "RecipeReleaseCommandDispositionInvalid")
                : throw new InvalidOperationException("RecipeReleaseCommandDispositionInvalid");
            return new ReleaseCommandAuditReference(SqliteNative.ColumnInt64(statement, 0),
                ParseReleaseGuidText(SqliteNative.ColumnText(statement, 1), "RecipeReleaseCommandCorrelationInvalid"),
                kind, phase, disposition, ParseReleaseGuidText(SqliteNative.ColumnText(statement, 4),
                    "RecipeReleaseCommandPrincipalInvalid"), ParseReleaseGuidText(SqliteNative.ColumnText(statement, 5),
                    "RecipeReleaseCommandSessionInvalid"), ParseReleaseGuidText(SqliteNative.ColumnText(statement, 6),
                    "RecipeReleaseCommandGrantInvalid"));
        }, eventId.ToString("D")).SingleOrDefault();
        if (command == default || command.CorrelationId != record.OperationId ||
            command.CommandKind != AuditedCommandKind.ReleaseRecipe || command.Phase != CommandAuditPhase.Outcome ||
            command.Disposition != CommandDisposition.Accepted ||
            command.PrincipalId != record.ApproverPrincipalId || command.SessionId != record.ApproverSessionId ||
            command.StepUpGrantId != record.StepUpGrantId)
            throw new InvalidOperationException("RecipeReleaseCommandAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Hash FROM audit_entries WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty), Number(command.Position)).SingleOrDefault();
        if (audit == default || !IsReleaseHash(audit.Hash))
            throw new InvalidOperationException("RecipeReleaseCommandAuditMissing");
        return command with { Sequence = audit.Sequence, Hash = audit.Hash };
    }

    private static ReleaseAuthorizationAuditReference ReadReleaseAuthorizationReference(sqlite3 database,
        Guid eventId, RecipeReleaseRecord record, string stationId, StoreDeadline deadline)
    {
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty));
        var matches = new List<ReleaseAuthorizationAuditReference>();
        foreach (var row in rows)
        {
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(row.Payload);
                if (!string.Equals(Convert.ToBase64String(payload), row.Payload, StringComparison.Ordinal) ||
                    !IsReleaseHash(row.Hash)) continue;
                IdentityAuditEvent.VerifyPayload(payload, row.Ordinal, stationId, schemaVersion);
                var fields = DecodeReleaseIdentityFields(payload);
                if (fields.Length != 49 || !Guid.TryParseExact(fields[1], "D", out var parsedEvent) ||
                    parsedEvent != eventId) continue;
                if (!IdentityAuditEvent.MatchesRecipeReleaseAuthorization(payload, row.Ordinal, stationId, record))
                    continue;
                matches.Add(new ReleaseAuthorizationAuditReference(row.Sequence, row.Hash));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or
                InvalidOperationException or EndOfStreamException or DecoderFallbackException)
            { }
        }
        if (matches.Count != 1)
            throw new InvalidOperationException("RecipeReleaseAuthorizationAuditMissing");
        return matches[0];
    }

    private static byte[] EncodeRecipeReleaseAuditBinding(long position, RecipeReleaseRecord record,
        string? previousHash, string payloadHash, Guid commandEventId, long commandAuditSequence,
        string commandAuditHash, Guid authorizationEventId, long authorizationAuditSequence,
        string authorizationAuditHash, byte[] payload) => AuditCanonical.Encode("RecipeReleaseLedgerEntryV1",
        Number(position), record.ReleaseId.ToString("D"), record.OperationId.ToString("D"),
        record.Recipe.Id, record.Recipe.Version, record.Source.DraftId.ToString("D"),
        Number(record.Source.Revision), record.Source.RevisionContentHash, record.ContentHash,
        previousHash, payloadHash, commandEventId.ToString("D"), Number(commandAuditSequence), commandAuditHash,
        authorizationEventId.ToString("D"), Number(authorizationAuditSequence), authorizationAuditHash,
        Convert.ToBase64String(payload));

    private static string ReadRecipeReleaseAuditHash(sqlite3 database, long sequence, long position,
        StoreDeadline deadline) => AuditChainDatabase.Read(database, @"
        SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND ReleasePosition=? LIMIT 2;", deadline,
        statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
        Number(sequence), RecipeReleaseEventKind, Number(position)).SingleOrDefault()
        ?? throw new InvalidOperationException("RecipeReleaseAuditEntryMissing");

    private static string ReadReleaseStationId(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1 LIMIT 1;", deadline)
        ?? throw new InvalidOperationException("RecipeReleaseStationIdentityMissing");

    private static Guid ParseReleaseGuidText(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty
            ? result : throw new InvalidOperationException(reason);

    private static T ReadReleaseEnum<T>(long value, string reason) where T : struct, Enum =>
        value is >= int.MinValue and <= int.MaxValue && Enum.IsDefined(typeof(T), (int)value)
            ? (T)Enum.ToObject(typeof(T), (int)value) : throw new InvalidOperationException(reason);

    private static bool IsReleaseHash(string? value) => value is { Length: 64 } &&
        value.All(character => Uri.IsHexDigit(character));

    private static string?[] DecodeReleaseIdentityFields(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var version = reader.ReadBytes(4);
        if (version.Length != 4 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(version) !=
            AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("RecipeReleaseAuthorizationPayloadInvalid");
        string? ReadValue()
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            if (marker != 1) throw new InvalidOperationException("RecipeReleaseAuthorizationPayloadInvalid");
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length != 4) throw new EndOfStreamException();
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > 2048) throw new InvalidOperationException("RecipeReleaseAuthorizationPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        if (ReadValue() != "IdentityEvent") throw new InvalidOperationException("RecipeReleaseAuthorizationPayloadInvalid");
        var countBytes = reader.ReadBytes(4);
        if (countBytes.Length != 4) throw new EndOfStreamException();
        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count != 49) throw new InvalidOperationException("RecipeReleaseAuthorizationPayloadInvalid");
        var fields = new string?[count];
        for (var index = 0; index < count; index++) fields[index] = ReadValue();
        if (stream.Position != stream.Length)
            throw new InvalidOperationException("RecipeReleaseAuthorizationPayloadTrailingBytes");
        return fields;
    }

    private sealed record RecipeReleaseConfig(long FormatVersion, string PolicyId, string PolicyVersion,
        long PolicyMode, string PolicyHash, long MaximumEntries, long MaximumPayloadBytes,
        long MaximumTotalBytes, string BindingHash);
    internal sealed record RecipeReleaseStoredEvent(long Position, RecipeReleaseRecord Record,
        string? PreviousHash, string PayloadHash, byte[] Payload, Guid CommandEventId,
        long CommandAuditSequence, string CommandAuditHash, Guid AuthorizationEventId,
        long AuthorizationAuditSequence, string AuthorizationAuditHash, long AuditSequence, string AuditHash);
    private sealed record ReleaseCommandAuditReference(long Position, Guid CorrelationId,
        AuditedCommandKind CommandKind, CommandAuditPhase Phase, CommandDisposition Disposition,
        Guid PrincipalId, Guid SessionId, Guid StepUpGrantId, long Sequence = 0, string Hash = "");
    private sealed record ReleaseAuthorizationAuditReference(long Sequence, string Hash);
}

internal sealed record RecipeReleaseCommandState(bool Enabled,
    IReadOnlyList<RecipeDraftRevision> DraftHistory, IReadOnlyList<RecipeReleaseRecord> Releases,
    IReadOnlyList<CalibrationAcceptancePolicyRevision> CalibrationPolicies,
    IReadOnlyList<RecipeLifecycleRecord>? Lifecycle = null);
internal sealed record RecipeReleaseMutation(RecipeReleaseRecord Record);
