using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal const string PlcResultContractEventKind = "PlcResultContractEvent";

    internal const string PlcResultContractSchemaSql = @"
        CREATE TABLE plc_result_contract_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumRevisions INTEGER NOT NULL CHECK(MaximumRevisions>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE plc_result_contract_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            RevisionId TEXT NOT NULL UNIQUE CHECK(length(RevisionId)=36),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            ContractId TEXT NOT NULL,
            ContractVersion TEXT NOT NULL,
            ContractHash TEXT NOT NULL CHECK(length(ContractHash)=64),
            PreviousContractId TEXT NULL,
            PreviousContractVersion TEXT NULL,
            PreviousContractHash TEXT NULL CHECK(PreviousContractHash IS NULL OR length(PreviousContractHash)=64),
            ReleaseHighWatermark INTEGER NOT NULL CHECK(ReleaseHighWatermark>=0),
            ActorPrincipalId TEXT NOT NULL CHECK(length(ActorPrincipalId)=36),
            ActorSessionId TEXT NOT NULL CHECK(length(ActorSessionId)=36),
            ActorAuthorizationRevision INTEGER NOT NULL CHECK(ActorAuthorizationRevision>=0),
            StepUpGrantId TEXT NOT NULL CHECK(length(StepUpGrantId)=36),
            AuthorizationPolicyId TEXT NOT NULL,
            AuthorizationPolicyVersion TEXT NOT NULL,
            AuthorizationPolicyHash TEXT NOT NULL CHECK(length(AuthorizationPolicyHash)=64),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0),
            AuthorizationTarget TEXT NOT NULL CHECK(length(AuthorizationTarget)=64),
            RevisionContentHash TEXT NOT NULL UNIQUE CHECK(length(RevisionContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandEventId TEXT NOT NULL UNIQUE CHECK(length(CommandEventId)=36),
            CommandAuditSequence INTEGER NOT NULL CHECK(CommandAuditSequence>0),
            CommandAuditHash TEXT NOT NULL CHECK(length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NOT NULL UNIQUE CHECK(length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NOT NULL CHECK(AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NOT NULL CHECK(length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64));
        CREATE INDEX ix_plc_result_contract_audit ON plc_result_contract_events(AuditSequence);
        CREATE INDEX ix_plc_result_contract_reference ON plc_result_contract_events(ContractId,ContractVersion,ContractHash);
        CREATE TRIGGER plc_result_contract_config_immutable_update BEFORE UPDATE
            ON plc_result_contract_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcResultContractConfiguration');
        END;
        CREATE TRIGGER plc_result_contract_config_immutable_delete BEFORE DELETE
            ON plc_result_contract_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcResultContractConfiguration');
        END;
        CREATE TRIGGER plc_result_contract_event_immutable_update BEFORE UPDATE
            ON plc_result_contract_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcResultContractEvent');
        END;
        CREATE TRIGGER plc_result_contract_event_immutable_delete BEFORE DELETE
            ON plc_result_contract_events BEGIN
            SELECT RAISE(ABORT,'ImmutablePlcResultContractEvent');
        END;";

    internal ValueTask<IdentityWriteResult> UpdatePlcResultContractCommandAsync(
        ChangePlcResultContractCommand command,
        Func<IdentityAuthorityState, PlcResultContractCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    internal static void InitializePlcResultContractSchema(sqlite3 database,
        PlcResultContractStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, PlcResultContractSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO plc_result_contract_store_config
                (Id,FormatVersion,MaximumRevisions,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            PlcResultContractStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumRevisions.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendPlcResultContractStoreActivation(database, policy, signingKey, options, deadline);
    }

    internal static void RequireConfiguredPlcResultContracts(sqlite3 database,
        PlcResultContractStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumRevisions,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM plc_result_contract_store_config WHERE Id=1 LIMIT 2;", deadline, statement =>
            new PlcResultContractStoreConfig(SqliteNative.ColumnInt64(statement, 0),
                SqliteNative.ColumnInt64(statement, 1), SqliteNative.ColumnInt64(statement, 2),
                SqliteNative.ColumnInt64(statement, 3), SqliteNative.ColumnText(statement, 4) ?? string.Empty))
            .SingleOrDefault();
        AuditChainDatabase.Require(configured is not null &&
            configured.FormatVersion == PlcResultContractStoreOptions.FormatVersion &&
            configured.MaximumRevisions == options.MaximumRevisions &&
            configured.MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured.MaximumTotalBytes == options.MaximumTotalBytes &&
            configured.BindingHash == options.BindingHash, "PlcResultContractConfigurationMismatch");
    }

    internal static void VerifyPlcResultContractActivationPayload(sqlite3 database, byte[] payload,
        PlcResultContractStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()))
            throw new InvalidOperationException("PlcResultContractActivationBindingMismatch");
        RequireConfiguredPlcResultContracts(database, options, deadline);
    }

    internal PlcResultContractCommandState ReadPlcResultContractCommandState(sqlite3 database,
        StoreDeadline deadline)
    {
        var options = _options.PlcResultContracts;
        if (options is null)
            return new(false, Array.Empty<RecipeDraftRevision>(), Array.Empty<RecipeReleaseRecord>(),
                Array.Empty<PlcResultContractRevision>(), 0);
        options.Validate();
        RequireConfiguredPlcResultContracts(database, options, deadline);
        var revisions = ReadPlcResultContractRows(database, options, deadline)
            .Select(value => value.Revision).ToArray();
        ValidatePlcResultContractHistory(database, options, deadline);

        var draftOptions = _options.RecipeDrafts;
        var draftHistory = draftOptions is null
            ? Array.Empty<RecipeDraftRevision>()
            : ReadAllRecipeDraftHistory(database, deadline).ToArray();
        var releaseOptions = _options.RecipeReleases;
        var releases = releaseOptions is null
            ? Array.Empty<RecipeReleaseRecord>()
            : ReadRecipeReleaseRows(database, releaseOptions, deadline)
                .Select(value => value.Record).ToArray();
        var highWatermark = releases.Length == 0 ? 0 : releases.Max(value => value.Position);
        return new(true, draftHistory, releases, revisions, highWatermark);
    }

    private void AppendPlcResultContractIdentityMutation(sqlite3 database, IdentityUpdate update,
        PlcResultContractCommandState state, ChangePlcResultContractCommand command, StoreDeadline deadline)
    {
        var mutation = update.PlcResultContract ?? throw new InvalidOperationException("PlcResultContractMutationMissing");
        var revision = mutation.Revision ?? throw new InvalidOperationException("PlcResultContractMutationMissing");
        var options = _options.PlcResultContracts ?? throw new InvalidOperationException("PlcResultContractConfigurationRequired");
        if (!state.Enabled || update.CommandFacts is not { Count: 1 } || update.Events.Count != 1 ||
            update.CommandFacts[0].Disposition != CommandDisposition.Accepted ||
            update.CommandFacts[0].Phase != CommandAuditPhase.Outcome ||
            update.CommandFacts[0].CorrelationId != revision.OperationId ||
            update.CommandFacts[0].CommandKind != AuditedCommandKind.ChangePlcResultContract ||
            update.Events[0].Kind != IdentityEventKind.PlcResultContractChanged ||
            update.Events[0].OperationId != revision.OperationId ||
            revision.Position != state.Revisions.Count + 1L ||
            revision.ReleaseHighWatermark != state.ReleaseHighWatermark ||
            revision.PreviousContract != state.Revisions.LastOrDefault()?.Reference)
            throw new InvalidOperationException("PlcResultContractIdentityMutationMismatch");

        ValidatePlcResultContractCommandBinding(command, revision, update.Events[0], update.CommandFacts[0]);
        options.Validate();
        var payload = PlcResultContractStorageCodec.Encode(revision);
        if (payload.Length > options.MaximumPayloadBytes || payload.Length > PlcResultContractStorageCodec.MaximumPayloadBytes)
            throw new InvalidOperationException("PlcResultContractPayloadCapacityExceeded");
        var rows = ReadPlcResultContractRows(database, options, deadline);
        if (rows.Count >= options.MaximumRevisions)
            throw new InvalidOperationException("PlcResultContractRevisionCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, value) => checked(sum + value.Payload.Length));
        if (checked(total + payload.Length) > options.MaximumTotalBytes)
            throw new InvalidOperationException("PlcResultContractTotalCapacityExceeded");
        if (rows.Any(value => value.Revision.RevisionId == revision.RevisionId ||
                value.Revision.OperationId == revision.OperationId ||
                value.Revision.ContentHash == revision.ContentHash))
            throw new InvalidOperationException("PlcResultContractRevisionConflict");
        if (rows.Any(value => value.Revision.Contract.Id == revision.Contract.Id &&
                value.Revision.Contract.Version == revision.Contract.Version))
            throw new InvalidOperationException("PlcResultContractVersionConflict");

        var commandReference = ReadPlcResultContractCommandReference(database,
            update.CommandFacts[0].EventId, revision, deadline);
        var authorizationReference = ReadPlcResultContractAuthorizationReference(database,
            update.Events[0].EventId, revision, _policy!.StationId, deadline);
        var previousHash = rows.Count == 0 ? null : rows[^1].AuditHash;
        var auditPayload = EncodePlcResultContractAuditBinding(revision.Position, revision, previousHash,
            Convert.ToHexString(SHA256.HashData(payload)), update.CommandFacts[0].EventId,
            commandReference.Sequence, commandReference.Hash, update.Events[0].EventId,
            authorizationReference.Sequence, authorizationReference.Hash, payload);
        var auditSequence = AuditChainDatabase.AppendPlcResultContractLedgerEntry(database, _policy!, _signingKey!,
            revision.Position, auditPayload, options, deadline);
        var auditHash = ReadPlcResultContractAuditHash(database, auditSequence, revision.Position, deadline);
        var stored = new PlcResultContractStoredEvent(revision.Position, revision, previousHash,
            Convert.ToHexString(SHA256.HashData(payload)), payload, update.CommandFacts[0].EventId,
            commandReference.Sequence, commandReference.Hash, update.Events[0].EventId,
            authorizationReference.Sequence, authorizationReference.Hash, auditSequence, auditHash);
        InsertPlcResultContractEvent(database, stored, deadline);
        ValidatePlcResultContractAuditReferences(database, stored, deadline);
    }

    internal static void ValidatePlcResultContractCommandBinding(ChangePlcResultContractCommand command,
        PlcResultContractRevision revision, IdentityAuditEvent authorization, CommandAuditFact fact)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(fact);
        if (revision.OperationId != command.CorrelationId || revision.Contract.Reference != command.Proposal.Reference ||
            revision.PreviousContract != command.ExpectedCurrent || revision.ChangeReason != command.ChangeReason ||
            revision.AuthorizationTarget != command.AuthorizationTarget ||
            authorization.Kind != IdentityEventKind.PlcResultContractChanged ||
            authorization.ActionTargetId != command.AuthorizationTarget || authorization.OperationId != command.CorrelationId ||
            authorization.CommandCorrelationId != command.CorrelationId ||
            authorization.BoundCommandCorrelationId != command.CorrelationId ||
            authorization.PrincipalId != revision.ActorPrincipalId || authorization.ActorPrincipalId != revision.ActorPrincipalId ||
            authorization.SessionId != revision.ActorSessionId ||
            authorization.AuthorizationRevision != revision.ActorAuthorizationRevision ||
            authorization.OccurredAtUtc != revision.RecordedAtUtc || authorization.StepUpGrantId != command.Invocation.StepUpGrantId ||
            fact.CorrelationId != command.CorrelationId || fact.CommandKind != AuditedCommandKind.ChangePlcResultContract ||
            fact.Disposition != CommandDisposition.Accepted || fact.Phase != CommandAuditPhase.Outcome ||
            fact.AuthenticatedHumanPrincipalId != revision.ActorPrincipalId.ToString("D") ||
            fact.ClaimedSessionId != revision.ActorSessionId || fact.ClaimedStepUpGrantId != command.Invocation.StepUpGrantId ||
            command.Invocation.StepUpGrantId is null)
            throw new InvalidOperationException("PlcResultContractCommandBindingMismatch");
    }

    internal static void VerifyPlcResultContractAuditPayload(sqlite3 database, long position, byte[] auditPayload,
        PlcResultContractStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(auditPayload);
        ArgumentNullException.ThrowIfNull(options);
        var stored = ReadPlcResultContractRows(database, options, deadline)
            .SingleOrDefault(value => value.Revision.Position == position);
        if (stored is null) throw new InvalidOperationException("PlcResultContractAuditBindingMismatch");
        var expected = EncodePlcResultContractAuditBinding(stored.Revision.Position, stored.Revision,
            stored.PreviousHash, stored.PayloadHash, stored.CommandEventId, stored.CommandAuditSequence,
            stored.CommandAuditHash, stored.AuthorizationEventId, stored.AuthorizationAuditSequence,
            stored.AuthorizationAuditHash, stored.Payload);
        if (!auditPayload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("PlcResultContractAuditPayloadMismatch");
    }

    internal static void ValidatePlcResultContractHistory(sqlite3 database,
        PlcResultContractStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireConfiguredPlcResultContracts(database, options, deadline);
        var rows = ReadPlcResultContractRows(database, options, deadline);
        if (rows.Count > options.MaximumRevisions)
            throw new InvalidOperationException("PlcResultContractRevisionCapacityExceeded");
        long highWatermark = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Revision.Position != index + 1L || row.PreviousHash != (index == 0 ? null : rows[index - 1].AuditHash) ||
                row.Revision.PreviousContract != (index == 0 ? null : rows[index - 1].Revision.Reference) ||
                row.Revision.ReleaseHighWatermark < highWatermark)
                throw new InvalidOperationException("PlcResultContractRevisionChainInvalid");
            highWatermark = row.Revision.ReleaseHighWatermark;
            ValidatePlcResultContractAuditReferences(database, row, deadline);
        }
        ValidatePlcResultContractReleaseBindings(database, rows, deadline);
    }

    private static void ValidatePlcResultContractReleaseBindings(sqlite3 database,
        IReadOnlyList<PlcResultContractStoredEvent> contractRows, StoreDeadline deadline)
    {
        var hasReleaseLedger = AuditChainDatabase.TableExists(database, "recipe_release_events", deadline);
        if (!hasReleaseLedger)
        {
            if (contractRows.Any(row => row.Revision.ReleaseHighWatermark != 0 || row.Revision.Bindings.Count != 0))
                throw new InvalidOperationException("PlcResultContractReleaseBindingMismatch");
            return;
        }

        // AuditChainDatabase.Verify has already verified the configured release ledger before
        // this history pass. Decode the same immutable payloads here to bind each contract
        // revision to the release snapshot it recorded, without consulting later releases.
        var releases = AuditChainDatabase.Read(database, @"
            SELECT Position,ReleaseId,RecordContentHash,AuditSequence,Payload
            FROM recipe_release_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var releaseIdText = SqliteNative.ColumnText(statement, 1);
            var recordHash = SqliteNative.ColumnText(statement, 2) ?? string.Empty;
            var auditSequence = SqliteNative.ColumnInt64(statement, 3);
            var payloadText = SqliteNative.ColumnText(statement, 4) ?? string.Empty;
            if (position < 1 || !Guid.TryParseExact(releaseIdText, "D", out var releaseId) ||
                releaseId == Guid.Empty || !IsContractHash(recordHash) || auditSequence < 1)
                throw new InvalidOperationException("PlcResultContractReleaseBindingMismatch");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(payloadText);
                if (!string.Equals(Convert.ToBase64String(payload), payloadText, StringComparison.Ordinal))
                    throw new InvalidOperationException("PlcResultContractReleaseBindingMismatch");
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("PlcResultContractReleaseBindingMismatch", exception); }
            var record = RecipeReleaseStorageCodec.Decode(payload);
            if (record.Position != position || record.ReleaseId != releaseId ||
                record.ContentHash != recordHash)
                throw new InvalidOperationException("PlcResultContractReleaseBindingMismatch");
            return new PlcResultContractReleaseHistoryFact(record, auditSequence);
        });
        ValidatePlcResultContractReleaseBindings(
            contractRows.Select(row => new PlcResultContractHistoryFact(row.Revision, row.AuditSequence)).ToArray(),
            releases);
    }

    internal static void ValidatePlcResultContractReleaseBindings(
        IReadOnlyList<PlcResultContractHistoryFact> contractRows,
        IReadOnlyList<PlcResultContractReleaseHistoryFact> releaseFacts)
    {
        ArgumentNullException.ThrowIfNull(contractRows);
        ArgumentNullException.ThrowIfNull(releaseFacts);
        if (contractRows.Count == 0)
            return;
        if (releaseFacts.Count > RecipeReleaseStoreOptions.MaximumEntriesHardLimit)
            throw new InvalidOperationException("PlcResultContractReleaseHistoryInvalid");

        var releases = releaseFacts.OrderBy(value => value.Record.Position).ToArray();
        for (var index = 0; index < releases.Length; index++)
        {
            if (releases[index].Record.Position != index + 1L || releases[index].AuditSequence < 1)
                throw new InvalidOperationException("PlcResultContractReleaseHistoryInvalid");
        }
        if (releases.Select(value => value.Record.ReleaseId).Distinct().Count() != releases.Length ||
            releases.Select(value => value.AuditSequence).Distinct().Count() != releases.Length)
            throw new InvalidOperationException("PlcResultContractReleaseHistoryInvalid");

        var orderedContractRows = contractRows.OrderBy(value => value.Revision.Position).ToArray();
        for (var index = 0; index < orderedContractRows.Length; index++)
        {
            if (orderedContractRows[index].Revision.Position != index + 1L ||
                orderedContractRows[index].AuditSequence < 1)
                throw new InvalidOperationException("PlcResultContractRevisionChainInvalid");
        }
        if (orderedContractRows.Select(value => value.AuditSequence).Distinct().Count() != orderedContractRows.Length)
            throw new InvalidOperationException("PlcResultContractRevisionChainInvalid");

        var byId = releases.ToDictionary(value => value.Record.ReleaseId);
        var previousSchemaReferences = new HashSet<RecipeContractReference>();
        foreach (var contractRow in orderedContractRows)
        {
            var revision = contractRow.Revision;
            var currentSchemaReferences = revision.Contract.SchemaMaps
                .Select(map => map.ResultSchema).ToHashSet();
            var affectedSchemaReferences = previousSchemaReferences
                .Union(currentSchemaReferences).ToHashSet();
            var releasesBeforeRevision = releases
                .Where(value => value.AuditSequence < contractRow.AuditSequence)
                .ToArray();
            var expectedHighWatermark = releasesBeforeRevision.Length == 0
                ? 0L : releasesBeforeRevision.Max(value => value.Record.Position);
            if (revision.ReleaseHighWatermark != expectedHighWatermark)
                throw new InvalidOperationException("PlcResultContractReleaseSnapshotInvalid");

            var snapshot = releases
                .Where(value => value.Record.Position <= revision.ReleaseHighWatermark)
                .ToArray();
            if (snapshot.Any(value => value.AuditSequence >= contractRow.AuditSequence))
                throw new InvalidOperationException("PlcResultContractReleaseSnapshotInvalid");

            var bindingsById = revision.Bindings.ToDictionary(binding => binding.ReleaseId);
            foreach (var binding in revision.Bindings)
            {
                if (!byId.TryGetValue(binding.ReleaseId, out var release) ||
                    release.Record.Position > revision.ReleaseHighWatermark ||
                    release.AuditSequence >= contractRow.AuditSequence ||
                    release.Record.ContentHash != binding.ReleaseRecordContentHash ||
                    binding.Binding.Recipe.Id != release.Record.Recipe.Id ||
                    binding.Binding.Recipe.Version != release.Record.Recipe.Version ||
                    binding.Binding.Recipe.ContentHash != release.Record.Recipe.ContentHash ||
                    binding.Binding.Algorithm.Id != release.Record.Source.Content.Algorithm.Algorithm.Id ||
                    binding.Binding.Algorithm.Version != release.Record.Source.Content.Algorithm.Algorithm.Version ||
                    binding.Binding.ResultSchema.Id != release.Record.Source.Content.Algorithm.ResultSchema.Id ||
                    binding.Binding.ResultSchema.Version != release.Record.Source.Content.Algorithm.ResultSchema.Version ||
                    binding.Binding.ResultSchema.ContentHash != release.Record.Source.Content.Algorithm.ResultSchema.ContentHash ||
                    !currentSchemaReferences.Contains(new RecipeContractReference(
                        binding.Binding.ResultSchema.Id, binding.Binding.ResultSchema.Version,
                        binding.Binding.ResultSchema.ContentHash)))
                    throw new InvalidOperationException("PlcResultContractReleaseBindingMismatch");
            }

            foreach (var release in snapshot)
            {
                var releaseSchema = new RecipeContractReference(
                    release.Record.Source.Content.Algorithm.ResultSchema.Id,
                    release.Record.Source.Content.Algorithm.ResultSchema.Version,
                    release.Record.Source.Content.Algorithm.ResultSchema.ContentHash);
                if (!affectedSchemaReferences.Contains(releaseSchema))
                    continue;
                if (!currentSchemaReferences.Contains(releaseSchema) ||
                    !bindingsById.ContainsKey(release.Record.ReleaseId))
                    throw new InvalidOperationException("PlcResultContractReleaseCoverageMismatch");
            }
            previousSchemaReferences = currentSchemaReferences;
        }
    }

    internal static IReadOnlyList<PlcResultContractStoredEvent> ReadPlcResultContractRows(sqlite3 database,
        PlcResultContractStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,RevisionId,OperationId,ContractId,ContractVersion,ContractHash,
                PreviousContractId,PreviousContractVersion,PreviousContractHash,ReleaseHighWatermark,
                ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,StepUpGrantId,
                AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,ChangeReason,
                AuthorizationTarget,RevisionContentHash,PayloadHash,Payload,CommandEventId,
                CommandAuditSequence,CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,
                AuthorizationAuditHash,AuditSequence,AuditHash
            FROM plc_result_contract_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var previousHash = SqliteNative.ColumnText(statement, 1);
            var revisionId = ParseContractGuid(SqliteNative.ColumnText(statement, 2), "PlcResultContractIdentityInvalid");
            var operationId = ParseContractGuid(SqliteNative.ColumnText(statement, 3), "PlcResultContractIdentityInvalid");
            var contractId = SqliteNative.ColumnText(statement, 4) ?? string.Empty;
            var contractVersion = SqliteNative.ColumnText(statement, 5) ?? string.Empty;
            var contractHash = SqliteNative.ColumnText(statement, 6) ?? string.Empty;
            var previousContractId = SqliteNative.ColumnText(statement, 7);
            var previousContractVersion = SqliteNative.ColumnText(statement, 8);
            var previousContractHash = SqliteNative.ColumnText(statement, 9);
            var releaseHighWatermark = SqliteNative.ColumnInt64(statement, 10);
            var actorPrincipalId = ParseContractGuid(SqliteNative.ColumnText(statement, 11), "PlcResultContractIdentityInvalid");
            var actorSessionId = ParseContractGuid(SqliteNative.ColumnText(statement, 12), "PlcResultContractIdentityInvalid");
            var actorAuthorizationRevision = SqliteNative.ColumnInt64(statement, 13);
            var stepUpGrantId = ParseContractGuid(SqliteNative.ColumnText(statement, 14), "PlcResultContractIdentityInvalid");
            var authorizationPolicyId = SqliteNative.ColumnText(statement, 15) ?? string.Empty;
            var authorizationPolicyVersion = SqliteNative.ColumnText(statement, 16) ?? string.Empty;
            var authorizationPolicyHash = SqliteNative.ColumnText(statement, 17) ?? string.Empty;
            var changeReason = SqliteNative.ColumnText(statement, 18) ?? string.Empty;
            var authorizationTarget = SqliteNative.ColumnText(statement, 19) ?? string.Empty;
            var revisionContentHash = SqliteNative.ColumnText(statement, 20) ?? string.Empty;
            var payloadHash = SqliteNative.ColumnText(statement, 21) ?? string.Empty;
            var payloadText = SqliteNative.ColumnText(statement, 22) ?? string.Empty;
            var commandEventId = ParseContractGuid(SqliteNative.ColumnText(statement, 23), "PlcResultContractAuditBindingInvalid");
            var commandAuditSequence = SqliteNative.ColumnInt64(statement, 24);
            var commandAuditHash = SqliteNative.ColumnText(statement, 25) ?? string.Empty;
            var authorizationEventId = ParseContractGuid(SqliteNative.ColumnText(statement, 26), "PlcResultContractAuditBindingInvalid");
            var authorizationAuditSequence = SqliteNative.ColumnInt64(statement, 27);
            var authorizationAuditHash = SqliteNative.ColumnText(statement, 28) ?? string.Empty;
            var auditSequence = SqliteNative.ColumnInt64(statement, 29);
            var auditHash = SqliteNative.ColumnText(statement, 30) ?? string.Empty;
            if (!IsContractHash(previousHash) && previousHash is not null ||
                !IsContractHash(contractHash) || !IsContractHash(previousContractHash) && previousContractHash is not null ||
                !IsContractHash(authorizationPolicyHash) || !IsContractHash(revisionContentHash) ||
                !IsContractHash(payloadHash) || !IsContractHash(commandAuditHash) ||
                !IsContractHash(authorizationAuditHash) || !IsContractHash(auditHash) ||
                commandAuditSequence < 1 || authorizationAuditSequence < 1 || auditSequence < 1)
                throw new InvalidOperationException("PlcResultContractRecordBindingMismatch");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(payloadText);
                if (!string.Equals(Convert.ToBase64String(payload), payloadText, StringComparison.Ordinal))
                    throw new InvalidOperationException("PlcResultContractPayloadEncodingInvalid");
            }
            catch (FormatException exception) { throw new InvalidOperationException("PlcResultContractPayloadEncodingInvalid", exception); }
            var revision = PlcResultContractStorageCodec.Decode(payload);
            var expectedPrevious = previousContractId is null && previousContractVersion is null && previousContractHash is null
                ? null : new RecipeContractReference(previousContractId ?? string.Empty,
                    previousContractVersion ?? string.Empty, previousContractHash ?? string.Empty);
            if (revision.Position != position || revision.RevisionId != revisionId || revision.OperationId != operationId ||
                revision.Contract.Id != contractId || revision.Contract.Version != contractVersion ||
                revision.Contract.ContentHash != contractHash || revision.PreviousContract != expectedPrevious ||
                revision.ReleaseHighWatermark != releaseHighWatermark || revision.ActorPrincipalId != actorPrincipalId ||
                revision.ActorSessionId != actorSessionId || revision.ActorAuthorizationRevision != actorAuthorizationRevision ||
                revision.StepUpGrantId != stepUpGrantId || revision.AuthorizationPolicy.Id != authorizationPolicyId ||
                revision.AuthorizationPolicy.Version != authorizationPolicyVersion ||
                revision.AuthorizationPolicy.ContentHash != authorizationPolicyHash || revision.ChangeReason != changeReason ||
                revision.AuthorizationTarget != authorizationTarget || revision.ContentHash != revisionContentHash ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(payload)), payloadHash, StringComparison.Ordinal))
                throw new InvalidOperationException("PlcResultContractRecordBindingMismatch");
            return new PlcResultContractStoredEvent(position, revision, previousHash, payloadHash, payload,
                commandEventId, commandAuditSequence, commandAuditHash, authorizationEventId,
                authorizationAuditSequence, authorizationAuditHash, auditSequence, auditHash);
        });
        if (rows.Count > options.MaximumRevisions)
            throw new InvalidOperationException("PlcResultContractRevisionCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        if (total > options.MaximumTotalBytes)
            throw new InvalidOperationException("PlcResultContractTotalCapacityExceeded");
        return rows;
    }

    private static void InsertPlcResultContractEvent(sqlite3 database,
        PlcResultContractStoredEvent stored, StoreDeadline deadline) =>
        AuditChainDatabase.Execute(database, @"
            INSERT INTO plc_result_contract_events
                (Position,PreviousHash,RevisionId,OperationId,ContractId,ContractVersion,ContractHash,
                 PreviousContractId,PreviousContractVersion,PreviousContractHash,ReleaseHighWatermark,
                 ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,StepUpGrantId,
                 AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,ChangeReason,
                 AuthorizationTarget,RevisionContentHash,PayloadHash,Payload,CommandEventId,
                 CommandAuditSequence,CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,
                 AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            N(stored.Position), stored.PreviousHash, stored.Revision.RevisionId.ToString("D"),
            stored.Revision.OperationId.ToString("D"), stored.Revision.Contract.Id, stored.Revision.Contract.Version,
            stored.Revision.Contract.ContentHash, stored.Revision.PreviousContract?.Id,
            stored.Revision.PreviousContract?.Version, stored.Revision.PreviousContract?.ContentHash,
            N(stored.Revision.ReleaseHighWatermark), stored.Revision.ActorPrincipalId.ToString("D"),
            stored.Revision.ActorSessionId.ToString("D"), N(stored.Revision.ActorAuthorizationRevision),
            stored.Revision.StepUpGrantId.ToString("D"), stored.Revision.AuthorizationPolicy.Id,
            stored.Revision.AuthorizationPolicy.Version, stored.Revision.AuthorizationPolicy.ContentHash,
            stored.Revision.ChangeReason, stored.Revision.AuthorizationTarget, stored.Revision.ContentHash,
            stored.PayloadHash, Convert.ToBase64String(stored.Payload), stored.CommandEventId.ToString("D"),
            N(stored.CommandAuditSequence), stored.CommandAuditHash, stored.AuthorizationEventId.ToString("D"),
            N(stored.AuthorizationAuditSequence), stored.AuthorizationAuditHash, N(stored.AuditSequence), stored.AuditHash);

    private static void ValidatePlcResultContractAuditReferences(sqlite3 database,
        PlcResultContractStoredEvent row, StoreDeadline deadline)
    {
        var command = ReadPlcResultContractCommandReference(database, row.CommandEventId, row.Revision, deadline);
        if (command.Sequence != row.CommandAuditSequence || command.Hash != row.CommandAuditHash)
            throw new InvalidOperationException("PlcResultContractCommandAuditBindingMismatch");
        var authorization = ReadPlcResultContractAuthorizationReference(database, row.AuthorizationEventId,
            row.Revision, ReadContractStationId(database, deadline), deadline);
        if (authorization.Sequence != row.AuthorizationAuditSequence || authorization.Hash != row.AuthorizationAuditHash)
            throw new InvalidOperationException("PlcResultContractAuthorizationAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Kind,PlcResultContractPosition,Payload,Hash FROM audit_entries
            WHERE Sequence=? AND Kind=? AND PlcResultContractPosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Kind: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                Position: SqliteNative.ColumnInt64(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 4) ?? string.Empty),
            N(row.AuditSequence), PlcResultContractEventKind, N(row.Revision.Position)).SingleOrDefault();
        if (audit == default || audit.Sequence != row.AuditSequence || audit.Position != row.Revision.Position ||
            audit.Hash != row.AuditHash)
            throw new InvalidOperationException("PlcResultContractAuditBindingMismatch");
        byte[] payload;
        try { payload = Convert.FromBase64String(audit.Payload); }
        catch (FormatException exception) { throw new InvalidOperationException("PlcResultContractAuditPayloadInvalid", exception); }
        var expected = EncodePlcResultContractAuditBinding(row.Revision.Position, row.Revision, row.PreviousHash,
            row.PayloadHash, row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash,
            row.AuthorizationEventId, row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.Payload);
        if (!payload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("PlcResultContractAuditPayloadMismatch");
    }

    private static ContractCommandAuditReference ReadPlcResultContractCommandReference(sqlite3 database,
        Guid eventId, PlcResultContractRevision revision, StoreDeadline deadline)
    {
        var command = AuditChainDatabase.Read(database, @"
            SELECT Position,CorrelationId,CommandKind,Phase,Disposition,AuthenticatedHumanPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline, statement =>
        {
            var phase = ReadContractEnum<CommandAuditPhase>(SqliteNative.ColumnInt64(statement, 3),
                "PlcResultContractCommandPhaseInvalid");
            var disposition = ReadContractEnum<CommandDisposition>(SqliteNative.ColumnInt64(statement, 4),
                "PlcResultContractCommandDispositionInvalid");
            return new ContractCommandAuditReference(SqliteNative.ColumnInt64(statement, 0),
                ParseContractGuid(SqliteNative.ColumnText(statement, 1), "PlcResultContractCommandCorrelationInvalid"),
                ReadContractEnum<AuditedCommandKind>(SqliteNative.ColumnInt64(statement, 2),
                    "PlcResultContractCommandKindInvalid"), phase, disposition,
                ParseContractGuid(SqliteNative.ColumnText(statement, 5), "PlcResultContractCommandPrincipalInvalid"),
                ParseContractGuid(SqliteNative.ColumnText(statement, 6), "PlcResultContractCommandSessionInvalid"),
                ParseContractGuid(SqliteNative.ColumnText(statement, 7), "PlcResultContractCommandGrantInvalid"));
        }, eventId.ToString("D")).SingleOrDefault();
        if (command == default || command.CorrelationId != revision.OperationId ||
            command.CommandKind != AuditedCommandKind.ChangePlcResultContract ||
            command.Phase != CommandAuditPhase.Outcome || command.Disposition != CommandDisposition.Accepted ||
            command.PrincipalId != revision.ActorPrincipalId || command.SessionId != revision.ActorSessionId ||
            command.StepUpGrantId != revision.StepUpGrantId)
            throw new InvalidOperationException("PlcResultContractCommandAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database,
            "SELECT Sequence,Hash FROM audit_entries WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty), N(command.Position)).SingleOrDefault();
        if (audit == default || !IsContractHash(audit.Hash))
            throw new InvalidOperationException("PlcResultContractCommandAuditMissing");
        return command with { Sequence = audit.Sequence, Hash = audit.Hash };
    }

    private static ContractAuthorizationAuditReference ReadPlcResultContractAuthorizationReference(sqlite3 database,
        Guid eventId, PlcResultContractRevision revision, string stationId, StoreDeadline deadline)
    {
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database,
            "PRAGMA user_version;", deadline));
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1), Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty));
        var matches = new List<ContractAuthorizationAuditReference>();
        foreach (var row in rows)
        {
            try
            {
                var payload = Convert.FromBase64String(row.Payload);
                if (!string.Equals(Convert.ToBase64String(payload), row.Payload, StringComparison.Ordinal) ||
                    !IsContractHash(row.Hash)) continue;
                IdentityAuditEvent.VerifyPayload(payload, row.Ordinal, stationId, schemaVersion);
                var fields = DecodeContractIdentityFields(payload);
                if (fields.Length != 49 || !Guid.TryParseExact(fields[1], "D", out var parsedEvent) || parsedEvent != eventId)
                    continue;
                if (IdentityAuditEvent.MatchesPlcResultContractAuthorization(payload, row.Ordinal, stationId, revision))
                    matches.Add(new ContractAuthorizationAuditReference(row.Sequence, row.Hash));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or
                InvalidOperationException or EndOfStreamException or DecoderFallbackException) { }
        }
        if (matches.Count != 1) throw new InvalidOperationException("PlcResultContractAuthorizationAuditMissing");
        return matches[0];
    }

    private static byte[] EncodePlcResultContractAuditBinding(long position, PlcResultContractRevision revision,
        string? previousHash, string payloadHash, Guid commandEventId, long commandAuditSequence,
        string commandAuditHash, Guid authorizationEventId, long authorizationAuditSequence,
        string authorizationAuditHash, byte[] payload) => AuditCanonical.Encode("PlcResultContractLedgerEntryV1",
        N(position), revision.RevisionId.ToString("D"), revision.OperationId.ToString("D"), revision.Contract.Id,
        revision.Contract.Version, revision.Contract.ContentHash, revision.PreviousContract?.ContentHash,
        N(revision.ReleaseHighWatermark), revision.ActorPrincipalId.ToString("D"), revision.ActorSessionId.ToString("D"),
        N(revision.ActorAuthorizationRevision), revision.StepUpGrantId.ToString("D"), revision.AuthorizationPolicy.Id,
        revision.AuthorizationPolicy.Version, revision.AuthorizationPolicy.ContentHash, revision.ChangeReason,
        revision.AuthorizationTarget, revision.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), revision.ContentHash,
        previousHash, payloadHash, commandEventId.ToString("D"), N(commandAuditSequence), commandAuditHash,
        authorizationEventId.ToString("D"), N(authorizationAuditSequence), authorizationAuditHash,
        Convert.ToBase64String(payload));

    private static string ReadPlcResultContractAuditHash(sqlite3 database, long sequence, long position,
        StoreDeadline deadline) => AuditChainDatabase.Read(database, @"
        SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND PlcResultContractPosition=? LIMIT 2;", deadline,
        statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty, N(sequence), PlcResultContractEventKind,
        N(position)).SingleOrDefault() ?? throw new InvalidOperationException("PlcResultContractAuditEntryMissing");

    private static string ReadContractStationId(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1 LIMIT 1;", deadline)
        ?? throw new InvalidOperationException("PlcResultContractStationIdentityMissing");

    private static Guid ParseContractGuid(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty ? result :
        throw new InvalidOperationException(reason);

    private static T ReadContractEnum<T>(long value, string reason) where T : struct, Enum =>
        value is >= int.MinValue and <= int.MaxValue && Enum.IsDefined(typeof(T), (int)value)
            ? (T)Enum.ToObject(typeof(T), (int)value) : throw new InvalidOperationException(reason);

    private static bool IsContractHash(string? value) => value is { Length: 64 } &&
        value.All(character => Uri.IsHexDigit(character));

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string?[] DecodeContractIdentityFields(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        var version = reader.ReadBytes(4);
        if (version.Length != 4 || BinaryPrimitives.ReadInt32BigEndian(version) != AuditCanonical.CanonicalizationVersion)
            throw new InvalidOperationException("PlcResultContractAuthorizationPayloadInvalid");
        string? ReadValue()
        {
            var marker = reader.ReadByte();
            if (marker == 0) return null;
            if (marker != 1) throw new InvalidOperationException("PlcResultContractAuthorizationPayloadInvalid");
            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length != 4) throw new EndOfStreamException();
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > 2048) throw new InvalidOperationException("PlcResultContractAuthorizationPayloadInvalid");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        if (ReadValue() != "IdentityEvent") throw new InvalidOperationException("PlcResultContractAuthorizationPayloadInvalid");
        var countBytes = reader.ReadBytes(4);
        if (countBytes.Length != 4) throw new EndOfStreamException();
        var count = BinaryPrimitives.ReadInt32BigEndian(countBytes);
        if (count != 49) throw new InvalidOperationException("PlcResultContractAuthorizationPayloadInvalid");
        var fields = new string?[count];
        for (var index = 0; index < count; index++) fields[index] = ReadValue();
        if (stream.Position != stream.Length) throw new InvalidOperationException("PlcResultContractAuthorizationPayloadTrailingBytes");
        return fields;
    }

    private sealed record PlcResultContractStoreConfig(long FormatVersion, long MaximumRevisions,
        long MaximumPayloadBytes, long MaximumTotalBytes, string BindingHash);
    internal sealed record PlcResultContractStoredEvent(long Position, PlcResultContractRevision Revision,
        string? PreviousHash, string PayloadHash, byte[] Payload, Guid CommandEventId,
        long CommandAuditSequence, string CommandAuditHash, Guid AuthorizationEventId,
        long AuthorizationAuditSequence, string AuthorizationAuditHash, long AuditSequence, string AuditHash);
    private sealed record ContractCommandAuditReference(long Position, Guid CorrelationId,
        AuditedCommandKind CommandKind, CommandAuditPhase Phase, CommandDisposition Disposition,
        Guid PrincipalId, Guid SessionId, Guid StepUpGrantId, long Sequence = 0, string Hash = "");
    private sealed record ContractAuthorizationAuditReference(long Sequence, string Hash);
}

internal sealed record PlcResultContractCommandState(bool Enabled,
    IReadOnlyList<RecipeDraftRevision> DraftHistory, IReadOnlyList<RecipeReleaseRecord> Releases,
    IReadOnlyList<PlcResultContractRevision> Revisions, long ReleaseHighWatermark);
internal sealed record PlcResultContractMutation(PlcResultContractRevision Revision);
internal sealed record PlcResultContractHistoryFact(PlcResultContractRevision Revision, long AuditSequence);
internal sealed record PlcResultContractReleaseHistoryFact(RecipeReleaseRecord Record, long AuditSequence);
