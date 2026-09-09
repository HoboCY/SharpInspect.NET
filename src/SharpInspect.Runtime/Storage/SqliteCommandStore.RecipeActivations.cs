using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>Schema-18 append-only activation intent and terminal ledger.</summary>
internal sealed partial class SqliteCommandStore
{
    internal const string RecipeActivationStoreActivatedKind = "RecipeActivationStoreActivated";
    internal const string RecipeActivationEventKind = "RecipeActivationEvent";

    internal const string RecipeActivationSchemaSql = @"
        CREATE TABLE recipe_activation_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1),
            FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            MaximumEntries INTEGER NOT NULL CHECK(MaximumEntries>0),
            MaximumPayloadBytes INTEGER NOT NULL CHECK(MaximumPayloadBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE recipe_activation_events(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            PreviousHash TEXT NULL CHECK(PreviousHash IS NULL OR length(PreviousHash)=64),
            ActivationId TEXT NOT NULL UNIQUE CHECK(length(ActivationId)=36),
            AttemptId TEXT NOT NULL CHECK(length(AttemptId)=36),
            OperationId TEXT NOT NULL CHECK(length(OperationId)=36),
            OutcomeState INTEGER NOT NULL CHECK(OutcomeState IN (1,2,3,4)),
            AdmissionPosition INTEGER NULL CHECK(AdmissionPosition IS NULL OR AdmissionPosition>0),
            AdmissionActivationId TEXT NULL CHECK(AdmissionActivationId IS NULL OR length(AdmissionActivationId)=36),
            AdmissionContentHash TEXT NULL CHECK(AdmissionContentHash IS NULL OR length(AdmissionContentHash)=64),
            PreviousActivationPosition INTEGER NULL CHECK(PreviousActivationPosition IS NULL OR PreviousActivationPosition>0),
            PreviousActivationId TEXT NULL CHECK(PreviousActivationId IS NULL OR length(PreviousActivationId)=36),
            PreviousActivationContentHash TEXT NULL CHECK(PreviousActivationContentHash IS NULL OR length(PreviousActivationContentHash)=64),
            CandidateKey TEXT NOT NULL,
            CandidateVersion TEXT NOT NULL,
            CandidateContentHash TEXT NOT NULL CHECK(length(CandidateContentHash)=64),
            ReleaseId TEXT NOT NULL CHECK(length(ReleaseId)=36),
            ReleaseRecordContentHash TEXT NOT NULL CHECK(length(ReleaseRecordContentHash)=64),
            ResultingRecipeKey TEXT NULL,
            ResultingRecipeVersion TEXT NULL,
            ResultingRecipeContentHash TEXT NULL CHECK(ResultingRecipeContentHash IS NULL OR length(ResultingRecipeContentHash)=64),
            EvidenceKind INTEGER NOT NULL CHECK(EvidenceKind IN (1,2)),
            ActorPrincipalId TEXT NULL CHECK(ActorPrincipalId IS NULL OR length(ActorPrincipalId)=36),
            ActorSessionId TEXT NULL CHECK(ActorSessionId IS NULL OR length(ActorSessionId)=36),
            ActorAuthorizationRevision INTEGER NULL CHECK(ActorAuthorizationRevision IS NULL OR ActorAuthorizationRevision>=0),
            AuthorizationPolicyId TEXT NULL,
            AuthorizationPolicyVersion TEXT NULL,
            AuthorizationPolicyHash TEXT NULL CHECK(AuthorizationPolicyHash IS NULL OR length(AuthorizationPolicyHash)=64),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0),
            AuthorizationTarget TEXT NOT NULL CHECK(length(AuthorizationTarget)=64),
            RecordedAtUtc TEXT NOT NULL,
            RecordContentHash TEXT NOT NULL UNIQUE CHECK(length(RecordContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            Payload TEXT NOT NULL CHECK(length(Payload)>0),
            CommandEventId TEXT NULL UNIQUE CHECK(CommandEventId IS NULL OR length(CommandEventId)=36),
            CommandAuditSequence INTEGER NULL CHECK(CommandAuditSequence IS NULL OR CommandAuditSequence>0),
            CommandAuditHash TEXT NULL CHECK(CommandAuditHash IS NULL OR length(CommandAuditHash)=64),
            AuthorizationEventId TEXT NULL UNIQUE CHECK(AuthorizationEventId IS NULL OR length(AuthorizationEventId)=36),
            AuthorizationAuditSequence INTEGER NULL CHECK(AuthorizationAuditSequence IS NULL OR AuthorizationAuditSequence>0),
            AuthorizationAuditHash TEXT NULL CHECK(AuthorizationAuditHash IS NULL OR length(AuthorizationAuditHash)=64),
            AuditSequence INTEGER NOT NULL UNIQUE CHECK(AuditSequence>0),
            AuditHash TEXT NOT NULL UNIQUE CHECK(length(AuditHash)=64),
            UNIQUE(AttemptId,OutcomeState));
        CREATE INDEX ix_recipe_activation_attempt ON recipe_activation_events(AttemptId,Position);
        CREATE INDEX ix_recipe_activation_audit ON recipe_activation_events(AuditSequence);
        CREATE INDEX ix_recipe_activation_reference ON recipe_activation_events(ActivationId,RecordContentHash);
        CREATE TRIGGER recipe_activation_config_immutable_update BEFORE UPDATE
            ON recipe_activation_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeActivationConfiguration');
        END;
        CREATE TRIGGER recipe_activation_config_immutable_delete BEFORE DELETE
            ON recipe_activation_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeActivationConfiguration');
        END;
        CREATE TRIGGER recipe_activation_event_immutable_update BEFORE UPDATE
            ON recipe_activation_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeActivationEvent');
        END;
        CREATE TRIGGER recipe_activation_event_immutable_delete BEFORE DELETE
            ON recipe_activation_events BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeActivationEvent');
        END;";

    internal ValueTask<IdentityWriteResult> UpdateRecipeActivationCommandAsync(
        ActivateRecipeCommand command,
        Func<IdentityAuthorityState, RecipeActivationCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    internal static void InitializeRecipeActivationSchema(sqlite3 database,
        RecipeActivationStoreOptions options, StoreDeadline deadline,
        AuditIntegrityPolicy policy, IAuditSigningKey signingKey)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(signingKey);
        options.Validate();
        SqliteNative.Execute(database, RecipeActivationSchemaSql, deadline);
        AuditChainDatabase.Execute(database, @"
            INSERT INTO recipe_activation_store_config
                (Id,FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash)
            VALUES(1,?,?,?,?,?);", deadline,
            RecipeActivationStoreOptions.FormatVersion.ToString(CultureInfo.InvariantCulture),
            options.MaximumEntries.ToString(CultureInfo.InvariantCulture),
            options.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture), options.BindingHash);
        AuditChainDatabase.AppendRecipeActivationStoreActivation(database, policy, signingKey, options, deadline);
    }

    internal static void RequireConfiguredRecipeActivations(sqlite3 database,
        RecipeActivationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var configured = AuditChainDatabase.Read(database, @"
            SELECT FormatVersion,MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes,BindingHash
            FROM recipe_activation_store_config WHERE Id=1 LIMIT 2;", deadline, statement =>
            (FormatVersion: SqliteNative.ColumnInt64(statement, 0),
                MaximumEntries: SqliteNative.ColumnInt64(statement, 1),
                MaximumPayloadBytes: SqliteNative.ColumnInt64(statement, 2),
                MaximumTotalBytes: SqliteNative.ColumnInt64(statement, 3),
                BindingHash: SqliteNative.ColumnText(statement, 4) ?? string.Empty)).ToArray();
        AuditChainDatabase.Require(configured.Length == 1 &&
            configured[0].FormatVersion == RecipeActivationStoreOptions.FormatVersion &&
            configured[0].MaximumEntries == options.MaximumEntries &&
            configured[0].MaximumPayloadBytes == options.MaximumPayloadBytes &&
            configured[0].MaximumTotalBytes == options.MaximumTotalBytes &&
            configured[0].BindingHash == options.BindingHash, "RecipeActivationConfigurationMismatch");
    }

    internal static void VerifyRecipeActivationActivationPayload(sqlite3 database, byte[] payload,
        RecipeActivationStoreOptions options, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!payload.AsSpan().SequenceEqual(options.EncodeActivationPayload()))
            throw new InvalidOperationException("RecipeActivationActivationBindingMismatch");
        RequireConfiguredRecipeActivations(database, options, deadline);
    }

    internal RecipeActivationCommandState ReadRecipeActivationCommandState(sqlite3 database,
        StoreDeadline deadline)
    {
        var options = _options.RecipeActivations;
        if (options is null)
            return new(false, Array.Empty<RecipeDraftRevision>(), Array.Empty<RecipeReleaseRecord>(),
                Array.Empty<PlcResultContractRevision>(), Array.Empty<RecipeActivationRecord>(), null, null);
        options.Validate();
        RequireConfiguredRecipeActivations(database, options, deadline);
        var draftOptions = _options.RecipeDrafts ??
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        var releaseOptions = _options.RecipeReleases ??
            throw new InvalidOperationException("RecipeReleaseConfigurationRequired");
        var contractOptions = _options.PlcResultContracts ??
            throw new InvalidOperationException("PlcResultContractConfigurationRequired");
        ValidateRecipeDraftHistory(database, draftOptions, deadline);
        var drafts = ReadAllRecipeDraftHistory(database, deadline);
        ValidateRecipeReleaseHistory(database, releaseOptions, draftOptions,
            ReadCalibrationAcceptancePolicies(database, _options.CalibrationGovernance, deadline), drafts,
            deadline, _options.CalibrationGovernance);
        var releases = ReadRecipeReleaseRows(database, releaseOptions, deadline)
            .Select(value => value.Record).ToArray();
        ValidatePlcResultContractHistory(database, contractOptions, deadline);
        var contracts = ReadPlcResultContractRows(database, contractOptions, deadline)
            .Select(value => value.Revision).ToArray();
        var profileResolver = CreateCalibrationProfileResolver(database, _options.CalibrationGovernance, deadline);
        var rows = ReadRecipeActivationRows(database, options, deadline, profileResolver);
        ValidateRecipeActivationHistory(database, options, rows, releases, contracts, deadline);
        var records = rows.Select(value => value.Record).ToArray();
        var current = records.Where(value => value.CanBeActive).OrderBy(value => value.Position).LastOrDefault();
        var admitted = records.Where(value => value.Outcome.State == RecipeActivationOutcomeState.Admitted)
            .ToDictionary(value => value.Reference, value => value);
        var terminalAdmissionReferences = records.Where(value => value.IsTerminal)
            .Select(value => value.AdmissionReference).Where(value => value is not null)
            .Select(value => value!).ToHashSet();
        var pending = admitted.Values.Where(value => !terminalAdmissionReferences.Contains(value.Reference))
            .OrderBy(value => value.Position).LastOrDefault();
        var cameras = ReadCameraSetupStates(database, releases, deadline);
        var imagingSetups = ReadImagingSetupStates(database, releases, deadline);
        var governance = ReadCalibrationGovernanceRecords(database, _options.CalibrationGovernance, deadline);
        var admissionCommands = admitted.Values.ToDictionary(value => value.AttemptId,
            value => ReadFact(database, value.AttemptId, aggregateSequence: 1, deadline) ??
                throw new InvalidOperationException("RecipeActivationAdmissionCommandMissing"));
        return new(true, drafts, releases, contracts, records, current, pending, cameras, imagingSetups,
            governance.Records, governance.Position, governance.ContentHash, admissionCommands);
    }

    private void AppendRecipeActivationIdentityMutation(sqlite3 database, IdentityUpdate update,
        RecipeActivationCommandState state, ActivateRecipeCommand command, StoreDeadline deadline)
    {
        var mutation = update.RecipeActivation ?? throw new InvalidOperationException("RecipeActivationMutationMissing");
        var record = mutation.Record ?? throw new InvalidOperationException("RecipeActivationMutationMissing");
        var options = _options.RecipeActivations ?? throw new InvalidOperationException("RecipeActivationConfigurationRequired");
        if (!state.Enabled || update.Events.Count != 1 || update.CommandFacts is not { Count: 1 } ||
            record.Position != state.Records.Count + 1L || record.OperationId != command.OperationId ||
            record.Candidate != command.Candidate || record.ReleaseId != command.ReleaseId ||
            record.ReleaseRecordContentHash != command.ReleaseRecordContentHash ||
            record.AuthorizationTarget != command.AuthorizationTarget ||
            update.Events[0].OperationId != command.OperationId)
            throw new InvalidOperationException("RecipeActivationIdentityMutationMismatch");
        var fact = update.CommandFacts[0];
        AuditChainDatabase.Require(fact.CommandKind == AuditedCommandKind.ActivateRecipe &&
            fact.CorrelationId == command.CorrelationId, "RecipeActivationCommandBindingMismatch");
        if (record.Outcome.State == RecipeActivationOutcomeState.Admitted)
            AuditChainDatabase.Require(fact.Phase == CommandAuditPhase.Outcome &&
                fact.Disposition == CommandDisposition.Accepted, "RecipeActivationAdmissionFactInvalid");
        else
            AuditChainDatabase.Require(fact.Phase == CommandAuditPhase.Outcome ||
                (fact.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed && fact.Disposition is null),
                "RecipeActivationTerminalFactInvalid");
        var payload = RecipeActivationStorageCodec.Encode(record);
        if (payload.Length > options.MaximumPayloadBytes || payload.Length > RecipeActivationStorageCodec.MaximumPayloadBytes)
            throw new InvalidOperationException("RecipeActivationPayloadCapacityExceeded");
        var rows = ReadRecipeActivationRows(database, options, deadline,
            CreateCalibrationProfileResolver(database, _options.CalibrationGovernance, deadline));
        if (rows.Count >= options.MaximumEntries)
            throw new InvalidOperationException("RecipeActivationEntryCapacityExceeded");
        var total = rows.Aggregate(0L, (sum, value) => checked(sum + value.Payload.Length));
        if (checked(total + payload.Length) > options.MaximumTotalBytes)
            throw new InvalidOperationException("RecipeActivationTotalCapacityExceeded");
        var previousHash = rows.Count == 0 ? null : rows[^1].AuditHash;
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        var commandReference = ReadActivationCommandReference(database, fact.EventId, record, deadline);
        var authorizationEventId = update.Events[0].EventId;
        var authorizationReference = ReadActivationAuthorizationReference(database, authorizationEventId,
            record, deadline) ?? throw new InvalidOperationException("RecipeActivationAuthorizationAuditMissing");
        var binding = EncodeRecipeActivationAuditBinding(record.Position, record, previousHash, payloadHash,
            fact.EventId, commandReference.Sequence, commandReference.Hash, authorizationEventId,
            authorizationReference.Sequence, authorizationReference.Hash, payload);
        var auditSequence = AuditChainDatabase.AppendRecipeActivationLedgerEntry(database, _policy!, _signingKey!,
            record.Position, binding, options, deadline);
        var auditHash = ReadRecipeActivationAuditHash(database, auditSequence, record.Position, deadline);
        var stored = new RecipeActivationStoredEvent(record.Position, record, previousHash, payloadHash, payload,
            fact.EventId, commandReference.Sequence, commandReference.Hash, authorizationEventId,
            authorizationReference.Sequence, authorizationReference.Hash, auditSequence, auditHash);
        InsertRecipeActivationEvent(database, stored, deadline);
        ValidateRecipeActivationAuditReferences(database, stored, deadline);
    }

    internal static List<RecipeActivationStoredEvent> ReadRecipeActivationRows(sqlite3 database,
        RecipeActivationStoreOptions options, StoreDeadline deadline,
        Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>? profileResolver = null)
    {
        options.Validate();
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,PreviousHash,ActivationId,AttemptId,OperationId,OutcomeState,
                AdmissionPosition,AdmissionActivationId,AdmissionContentHash,
                PreviousActivationPosition,PreviousActivationId,PreviousActivationContentHash,
                CandidateKey,CandidateVersion,CandidateContentHash,ReleaseId,ReleaseRecordContentHash,
                ResultingRecipeKey,ResultingRecipeVersion,ResultingRecipeContentHash,EvidenceKind,
                ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,AuthorizationPolicyId,
                AuthorizationPolicyVersion,AuthorizationPolicyHash,ChangeReason,AuthorizationTarget,
                RecordedAtUtc,RecordContentHash,PayloadHash,Payload,CommandEventId,CommandAuditSequence,
                CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,AuthorizationAuditHash,
                AuditSequence,AuditHash FROM recipe_activation_events ORDER BY Position;", deadline, statement =>
        {
            var encoded = SqliteNative.ColumnText(statement, 32);
            if (encoded is null || encoded.Length == 0)
                throw new InvalidOperationException("RecipeActivationPayloadInvalid");
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded);
                if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                    throw new InvalidOperationException("RecipeActivationPayloadCanonicalMismatch");
            }
            catch (FormatException exception)
            { throw new InvalidOperationException("RecipeActivationPayloadInvalid", exception); }
            if (payload.Length < 1 || payload.Length > options.MaximumPayloadBytes ||
                payload.Length > RecipeActivationStorageCodec.MaximumPayloadBytes)
                throw new InvalidOperationException("RecipeActivationPayloadCapacityExceeded");
            var record = RecipeActivationStorageCodec.Decode(payload, profileResolver);
            var position = SqliteNative.ColumnInt64(statement, 0);
            var activationId = ParseActivationGuid(SqliteNative.ColumnText(statement, 2), "RecipeActivationIdentityInvalid");
            var attemptId = ParseActivationGuid(SqliteNative.ColumnText(statement, 3), "RecipeActivationIdentityInvalid");
            var operationId = ParseActivationGuid(SqliteNative.ColumnText(statement, 4), "RecipeActivationIdentityInvalid");
            if (record.Position != position || record.ActivationId != activationId || record.AttemptId != attemptId ||
                record.OperationId != operationId || (int)record.Outcome.State != SqliteNative.ColumnInt64(statement, 5) ||
                !ActivationReferenceColumnsEquals(record.AdmissionReference, statement, 6, 7, 8) ||
                !ActivationReferenceColumnsEquals(record.PreviousActivation, statement, 9, 10, 11) ||
                record.Candidate.Id != (SqliteNative.ColumnText(statement, 12) ?? string.Empty) ||
                record.Candidate.Version != (SqliteNative.ColumnText(statement, 13) ?? string.Empty) ||
                record.Candidate.ContentHash != (SqliteNative.ColumnText(statement, 14) ?? string.Empty) ||
                record.ReleaseId != ParseActivationGuid(SqliteNative.ColumnText(statement, 15), "RecipeActivationReleaseInvalid") ||
                record.ReleaseRecordContentHash != (SqliteNative.ColumnText(statement, 16) ?? string.Empty) ||
                !RecipeEquals(record.ResultingRecipe, ReadRecipeColumns(statement, 17)) ||
                (int)record.EvidenceKind != SqliteNative.ColumnInt64(statement, 20) ||
                !GuidColumnEquals(record.ActorPrincipalId, statement, 21) ||
                !GuidColumnEquals(record.ActorSessionId, statement, 22) ||
                !NullableLongEquals(record.ActorAuthorizationRevision, statement, 23) ||
                !ContractReferenceEquals(record.AuthorizationPolicy, statement, 24, 25, 26) ||
                record.ChangeReason != (SqliteNative.ColumnText(statement, 27) ?? string.Empty) ||
                record.AuthorizationTarget != (SqliteNative.ColumnText(statement, 28) ?? string.Empty) ||
                record.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) != SqliteNative.ColumnText(statement, 29) ||
                record.ContentHash != (SqliteNative.ColumnText(statement, 30) ?? string.Empty))
                throw new InvalidOperationException("RecipeActivationRecordBindingMismatch");
            var payloadHash = SqliteNative.ColumnText(statement, 31) ?? string.Empty;
            if (!IsActivationHash(payloadHash) || payloadHash != Convert.ToHexString(SHA256.HashData(payload)))
                throw new InvalidOperationException("RecipeActivationPayloadHashMismatch");
            var previousHash = SqliteNative.ColumnText(statement, 1);
            var commandEventId = ParseNullableActivationGuid(SqliteNative.ColumnText(statement, 33), "RecipeActivationCommandEventInvalid");
            var commandSequence = SqliteNative.ColumnInt64Nullable(statement, 34);
            var commandHash = SqliteNative.ColumnText(statement, 35);
            var authorizationEventId = ParseNullableActivationGuid(SqliteNative.ColumnText(statement, 36), "RecipeActivationAuthorizationEventInvalid");
            var authorizationSequence = SqliteNative.ColumnInt64Nullable(statement, 37);
            var authorizationHash = SqliteNative.ColumnText(statement, 38);
            var auditSequence = SqliteNative.ColumnInt64(statement, 39);
            var auditHash = SqliteNative.ColumnText(statement, 40) ?? string.Empty;
            var commandReferenceComplete = commandEventId is not null && commandSequence is not null &&
                commandSequence.Value > 0 && IsActivationHash(commandHash);
            var authorizationReferenceComplete = authorizationEventId is not null &&
                authorizationSequence is not null && authorizationSequence.Value > 0 &&
                IsActivationHash(authorizationHash);
            if (position < 1 || (previousHash is not null && !IsActivationHash(previousHash)) ||
                auditSequence < 1 || !IsActivationHash(auditHash) ||
                !commandReferenceComplete || !authorizationReferenceComplete)
                throw new InvalidOperationException("RecipeActivationRecordBindingMismatch");
            return new RecipeActivationStoredEvent(position, record, previousHash, payloadHash, payload,
                commandEventId, commandSequence, commandHash, authorizationEventId, authorizationSequence,
                authorizationHash, auditSequence, auditHash);
        });
        if (rows.Count > options.MaximumEntries)
            throw new InvalidOperationException("RecipeActivationEntryCapacityExceeded");
        return rows;
    }

    internal static void ValidateRecipeActivationHistory(sqlite3 database,
        RecipeActivationStoreOptions options, IReadOnlyList<RecipeActivationStoredEvent> rows,
        IReadOnlyList<RecipeReleaseRecord> releases, IReadOnlyList<PlcResultContractRevision> contracts,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(contracts);
        options.Validate();
        RequireConfiguredRecipeActivations(database, options, deadline);
        var releaseFacts = ReadActivationReleaseFacts(database, releases, deadline);
        var contractFacts = ReadActivationContractFacts(database, contracts, deadline);
        var cameraFacts = ReadActivationCameraFacts(database, deadline);
        var imagingFacts = ReadActivationImagingFacts(database, deadline);
        var governanceFacts = ReadActivationGovernanceFacts(database, deadline);
        var previousHash = (string?)null;
        var previousAuditSequence = 0L;
        var total = 0L;
        var byAttempt = new Dictionary<Guid, (bool Admission, bool Terminal)>();
        var admissions = new Dictionary<RecipeActivationReference, RecipeActivationStoredEvent>();
        var unfinishedAdmissions = new HashSet<RecipeActivationReference>();
        var terminalAdmissionReferences = new HashSet<RecipeActivationReference>();
        var successfulHeads = new Dictionary<RecipeActivationEvidenceKind, RecipeActivationStoredEvent>();
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (row.Record.Position != rowIndex + 1L || row.PreviousHash != previousHash)
                throw new InvalidOperationException("RecipeActivationPositionGap");
            if (row.AuditSequence <= previousAuditSequence)
                throw new InvalidOperationException("RecipeActivationAuditSequenceGap");
            total = checked(total + row.Payload.Length);
            if (total > options.MaximumTotalBytes)
                throw new InvalidOperationException("RecipeActivationTotalCapacityExceeded");
            if (!byAttempt.TryGetValue(row.Record.AttemptId, out var attempt)) attempt = default;
            if (row.Record.Outcome.State == RecipeActivationOutcomeState.Admitted)
            {
                if (attempt.Admission || attempt.Terminal)
                    throw new InvalidOperationException("RecipeActivationDuplicateAdmission");
                attempt.Admission = true;
                if (row.Record.Admission is null || row.Record.AdmissionReference is null)
                    throw new InvalidOperationException("RecipeActivationAdmissionEvidenceInvalid");
                if (unfinishedAdmissions.Count != 0)
                    throw new InvalidOperationException("RecipeActivationUnfinishedAdmission");
                if (admissions.ContainsKey(row.Record.Reference))
                    throw new InvalidOperationException("RecipeActivationDuplicateAdmission");
                ValidateActivationAdmissionBaseline(row.Record,
                    successfulHeads.GetValueOrDefault(row.Record.EvidenceKind)?.Record);
                admissions.Add(row.Record.Reference, row);
                unfinishedAdmissions.Add(row.Record.Reference);
            }
            else
            {
                if (attempt.Terminal) throw new InvalidOperationException("RecipeActivationDuplicateTerminal");
                if (attempt.Admission && row.Record.AdmissionReference is null)
                    throw new InvalidOperationException("RecipeActivationAdmissionReferenceInvalid");
                attempt.Terminal = true;
                if (row.Record.AdmissionReference is not null)
                {
                    if (!admissions.TryGetValue(row.Record.AdmissionReference, out var admission) ||
                        admission.Record.AttemptId != row.Record.AttemptId)
                        throw new InvalidOperationException("RecipeActivationAdmissionReferenceInvalid");
                    ValidateActivationTerminalAgainstAdmission(admission.Record, row.Record);
                    if (!unfinishedAdmissions.Remove(row.Record.AdmissionReference))
                        throw new InvalidOperationException("RecipeActivationAdmissionReferenceInvalid");
                    terminalAdmissionReferences.Add(row.Record.AdmissionReference);
                }
                else
                {
                    ValidateActivationPreAdmissionBaseline(row.Record);
                    if (row.Record.Outcome.State == RecipeActivationOutcomeState.Succeeded)
                        throw new InvalidOperationException("RecipeActivationSuccessAdmissionRequired");
                }
            }
            byAttempt[row.Record.AttemptId] = attempt;
            // A pre-admission rejection can be recorded before the candidate/release lookup
            // succeeds. Once an admission exists (and for every terminal continuation), the
            // release identity and recipe are mandatory cross-ledger bindings.
            if (row.Record.Outcome.State == RecipeActivationOutcomeState.Admitted ||
                row.Record.AdmissionReference is not null || row.Record.Outcome.Succeeded)
            {
                var release = FindActivationReleaseFact(row.Record, releaseFacts);
                if (!RecipeEquals(release.Record.Recipe, row.Record.Candidate))
                    throw new InvalidOperationException("RecipeActivationReleaseBindingMismatch");
                if (release.AuditSequence >= row.AuditSequence)
                    throw new InvalidOperationException("RecipeActivationReleaseOrderInvalid");
                if (row.Record.SuccessfulSnapshot is not null)
                    ValidateActivationSuccessfulSnapshot(row, release, contractFacts, cameraFacts,
                        imagingFacts, governanceFacts, admissions);
            }
            ValidateRecipeActivationAuditReferences(database, row, deadline);
            if (row.Record.Outcome.State == RecipeActivationOutcomeState.Succeeded)
                successfulHeads[row.Record.EvidenceKind] = row;
            previousHash = row.AuditHash;
            previousAuditSequence = row.AuditSequence;
        }
        if (terminalAdmissionReferences.Any(reference => !admissions.ContainsKey(reference)))
            throw new InvalidOperationException("RecipeActivationAdmissionReferenceInvalid");
        if (unfinishedAdmissions.Count > 1)
            throw new InvalidOperationException("RecipeActivationUnfinishedAdmission");
    }

    private static void ValidateActivationAdmissionBaseline(RecipeActivationRecord record,
        RecipeActivationRecord? previousHead)
    {
        var admission = record.Admission ?? throw new InvalidOperationException(
            "RecipeActivationAdmissionEvidenceInvalid");
        var expectedReference = previousHead?.Reference;
        var expectedRecipe = previousHead?.SuccessfulSnapshot?.Recipe;
        var expectedSnapshotHash = previousHead?.SuccessfulSnapshot?.ContentHash;
        if (admission.Position != record.Position || admission.ActivationId != record.ActivationId ||
            admission.EvidenceKind != record.EvidenceKind ||
            !ActivationReferenceEquals(record.AdmissionReference, record.Reference) ||
            !ActivationReferenceEquals(admission.ExpectedActive, expectedReference) ||
            !ActivationReferenceEquals(admission.PreviousActivation, expectedReference) ||
            !ActivationReferenceEquals(record.PreviousActivation, expectedReference) ||
            !RecipeEquals(admission.PreviousRecipe, expectedRecipe) ||
            !RecipeEquals(record.PreviousRecipe, expectedRecipe) ||
            !string.Equals(admission.PreviousSnapshotContentHash, expectedSnapshotHash,
                StringComparison.Ordinal) ||
            !string.Equals(record.PreviousSnapshotContentHash, expectedSnapshotHash,
                StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeActivationPreviousBaselineMismatch");

        if (previousHead is null && (admission.ExpectedActive is not null ||
            admission.PreviousActivation is not null || admission.PreviousRecipe is not null ||
            admission.PreviousSnapshotContentHash is not null || record.PreviousActivation is not null ||
            record.PreviousRecipe is not null || record.PreviousSnapshotContentHash is not null))
            throw new InvalidOperationException("RecipeActivationPreviousBaselineMismatch");
    }

    private static void ValidateActivationPreAdmissionBaseline(RecipeActivationRecord record)
    {
        if (record.Admission is not null || record.PreviousActivation is not null ||
            record.PreviousRecipe is not null || record.PreviousSnapshotContentHash is not null)
            throw new InvalidOperationException("RecipeActivationPreAdmissionBaselineInvalid");
    }

    private static void ValidateActivationTerminalAgainstAdmission(RecipeActivationRecord admission,
        RecipeActivationRecord terminal)
    {
        if (admission.Outcome.State != RecipeActivationOutcomeState.Admitted ||
            terminal.AdmissionReference is null || terminal.AdmissionReference != admission.Reference ||
            terminal.AttemptId != admission.AttemptId || terminal.OperationId != admission.OperationId ||
            !RecipeEquals(terminal.Candidate, admission.Candidate) || terminal.ReleaseId != admission.ReleaseId ||
            terminal.ReleaseRecordContentHash != admission.ReleaseRecordContentHash ||
            !ActivationReferenceEquals(terminal.PreviousActivation, admission.PreviousActivation) ||
            !RecipeEquals(terminal.PreviousRecipe, admission.PreviousRecipe) ||
            terminal.PreviousSnapshotContentHash != admission.PreviousSnapshotContentHash ||
            terminal.EvidenceKind != admission.EvidenceKind ||
            terminal.ActorPrincipalId != admission.ActorPrincipalId || terminal.ActorSessionId != admission.ActorSessionId ||
            terminal.ActorAuthorizationRevision != admission.ActorAuthorizationRevision ||
            !ContractReferenceEquals(terminal.AuthorizationPolicy, admission.AuthorizationPolicy) ||
            terminal.AuthorizationTarget != admission.AuthorizationTarget ||
            terminal.ChangeReason != admission.ChangeReason)
            throw new InvalidOperationException("RecipeActivationTerminalAdmissionMismatch");
    }

    private static void ValidateActivationSuccessfulSnapshot(RecipeActivationStoredEvent row,
        ActivationReleaseFact release, IReadOnlyList<ActivationContractFact> contracts,
        IReadOnlyList<ActivationCameraFact> cameras, IReadOnlyList<ActivationImagingFact> imaging,
        IReadOnlyList<ActivationGovernanceFact> governance,
        IReadOnlyDictionary<RecipeActivationReference, RecipeActivationStoredEvent> admissions)
    {
        var record = row.Record;
        if (record.Outcome.State != RecipeActivationOutcomeState.Succeeded ||
            record.AdmissionReference is null ||
            !admissions.TryGetValue(record.AdmissionReference, out var admission) ||
            record.SuccessfulSnapshot is not { } snapshot)
            throw new InvalidOperationException("RecipeActivationSuccessAdmissionRequired");
        if (snapshot.EvidenceKind != record.EvidenceKind ||
            snapshot.Release.ReleaseId != release.Record.ReleaseId ||
            snapshot.Release.ContentHash != release.Record.ContentHash ||
            snapshot.Release.Position != release.Record.Position ||
            !RecipeEquals(snapshot.Release.Recipe, release.Record.Recipe) ||
            snapshot.Recipe != record.Candidate)
            throw new InvalidOperationException("RecipeActivationSnapshotReleaseMismatch");

        var contract = contracts.Where(value => value.AuditSequence < row.AuditSequence)
            .OrderBy(value => value.AuditSequence).LastOrDefault();
        if (contract is null || !ContractReferenceEquals(contract.Revision.Reference,
                new RecipeContractReference(snapshot.PlcResultContract.Contract.Id,
                    snapshot.PlcResultContract.Contract.Version, snapshot.PlcResultContract.Contract.ContentHash)) ||
            !RecipeEquals(snapshot.PlcResultContract.Recipe, record.Candidate) ||
            snapshot.PlcResultContract.Algorithm.Id != release.Record.Source.Content.Algorithm.Algorithm.Id ||
            snapshot.PlcResultContract.Algorithm.Version != release.Record.Source.Content.Algorithm.Algorithm.Version ||
            !contract.Revision.Bindings.Any(value => value.ReleaseId == release.Record.ReleaseId &&
                value.ReleaseRecordContentHash == release.Record.ContentHash &&
                value.Binding.ContentHash == snapshot.PlcResultContract.ContentHash))
            throw new InvalidOperationException("RecipeActivationPlcContractBindingMismatch");

        ValidateActivationCameraSnapshot(record, release.Record, snapshot.CameraSetup, row.AuditSequence, cameras);
        ValidateActivationCalibrationSnapshot(record, release.Record, snapshot, admission.Record,
            row.AuditSequence, imaging, governance);
    }

    private static void ValidateActivationCameraSnapshot(RecipeActivationRecord record,
        RecipeReleaseRecord release, CameraSetupSnapshot snapshot, long activationAuditSequence,
        IReadOnlyList<ActivationCameraFact> cameraFacts)
    {
        if (snapshot.LogicalRole != release.Source.Content.CameraRole || snapshot.Binding is null)
            throw new InvalidOperationException("RecipeActivationCameraBindingMismatch");
        var latest = cameraFacts.Where(value => value.AuditSequence < activationAuditSequence &&
                value.Event.LogicalRole == snapshot.LogicalRole && value.Event.Phase == CameraSetupEventPhase.Completed &&
                value.Event.CommandKind == AuditedCommandKind.RebindCamera && value.Event.Succeeded &&
                value.Event.Target is not null && value.Event.RevisionHash is not null)
            .OrderBy(value => value.AuditSequence).LastOrDefault();
        if (latest is null || !CameraBindingEquals(snapshot.Binding, latest.Event))
            throw new InvalidOperationException("RecipeActivationCameraBindingMismatch");
        if (snapshot.Requested is null || snapshot.Requested != release.Source.Content.Camera)
            throw new InvalidOperationException("RecipeActivationCameraConfigurationMismatch");
    }

    private static void ValidateActivationCalibrationSnapshot(RecipeActivationRecord record,
        RecipeReleaseRecord release, RecipeActivationSnapshot snapshot, RecipeActivationRecord admission,
        long activationAuditSequence, IReadOnlyList<ActivationImagingFact> imaging,
        IReadOnlyList<ActivationGovernanceFact> governance)
    {
        var historicalRecords = governance.Where(value => value.AuditSequence < activationAuditSequence)
            .OrderBy(value => value.AuditSequence).Select(value => value.Record).ToArray();
        var imagingRevision = imaging.Where(value => value.AuditSequence < activationAuditSequence &&
                value.Revision.LogicalCameraRole == release.Source.Content.CameraRole)
            .OrderBy(value => value.AuditSequence).LastOrDefault()?.Revision;
        var lastGovernance = governance.Where(value => value.AuditSequence < activationAuditSequence)
            .OrderBy(value => value.AuditSequence).LastOrDefault();
        var evaluation = RecipeActivationCalibrationEvaluator.EvaluateRecords(
            release.Source.Content, admission.Admission!.CalibrationSelections, snapshot.CameraSetup,
            imagingRevision, historicalRecords, record.RecordedAtUtc,
            lastGovernance?.Position ?? 0, lastGovernance?.ContentHash);
        if (!evaluation.Allowed || !evaluation.Bindings.Select(value => value.ContentHash)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(snapshot.CalibrationBindings.Select(value => value.ContentHash)
                    .OrderBy(value => value, StringComparer.Ordinal)))
            throw new InvalidOperationException("RecipeActivationCalibrationBindingMismatch");
    }

    private static bool CameraBindingEquals(CameraBindingRevision binding, CameraSetupEvent value) =>
        value.Target is not null && value.RevisionHash is not null && value.ActorPrincipalId is { } principal &&
        value.SessionId is { } session && binding.Position == value.Position &&
        binding.LogicalRole == value.LogicalRole && binding.Revision == value.BindingRevision &&
        binding.OperationId == value.OperationId && binding.PreviousRevisionHash == value.PreviousRevisionHash &&
        binding.RevisionHash == value.RevisionHash && binding.Target == value.Target &&
        binding.AuthorPrincipalId == principal && binding.AuthorSessionId == session &&
        binding.AuthorAuthorizationRevision == value.AuthorAuthorizationRevision &&
        binding.ChangeReason == value.ChangeReason && binding.RecordedAtUtc == value.RecordedAtUtc;

    private static bool ActivationReferenceEquals(RecipeActivationReference? left,
        RecipeActivationReference? right) => left is null && right is null ||
        left is not null && right is not null && left.Position == right.Position &&
        left.ActivationId == right.ActivationId && left.ContentHash == right.ContentHash;

    private static bool ContractReferenceEquals(RecipeContractReference? left,
        RecipeContractReference? right) => left is null && right is null ||
        left is not null && right is not null && left.Id == right.Id && left.Version == right.Version &&
        left.ContentHash == right.ContentHash;

    private static ActivationReleaseFact FindActivationReleaseFact(RecipeActivationRecord record,
        IReadOnlyList<ActivationReleaseFact> releaseFacts) => releaseFacts.SingleOrDefault(value =>
            value.Record.ReleaseId == record.ReleaseId && value.Record.ContentHash == record.ReleaseRecordContentHash)
        ?? throw new InvalidOperationException("RecipeActivationReleaseBindingMismatch");

    private static IReadOnlyList<ActivationReleaseFact> ReadActivationReleaseFacts(sqlite3 database,
        IReadOnlyList<RecipeReleaseRecord> releases, StoreDeadline deadline)
    {
        if (releases.Count == 0 || !AuditChainDatabase.TableExists(database, "recipe_release_events", deadline))
            return Array.Empty<ActivationReleaseFact>();
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,ReleaseId,RecordContentHash,AuditSequence
            FROM recipe_release_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var releaseId = ParseActivationGuid(SqliteNative.ColumnText(statement, 1),
                "RecipeActivationReleaseInvalid");
            var contentHash = SqliteNative.ColumnText(statement, 2) ?? string.Empty;
            var auditSequence = SqliteNative.ColumnInt64(statement, 3);
            if (position < 1 || auditSequence < 1 || !IsActivationHash(contentHash))
                throw new InvalidOperationException("RecipeActivationReleaseBindingMismatch");
            return (Position: position, ReleaseId: releaseId, ContentHash: contentHash,
                AuditSequence: auditSequence);
        });
        if (rows.Select(value => value.Position).Distinct().Count() != rows.Count)
            throw new InvalidOperationException("RecipeActivationReleaseBindingMismatch");
        var rowsByPosition = rows.ToDictionary(value => value.Position);
        var facts = new List<ActivationReleaseFact>(releases.Count);
        foreach (var record in releases)
        {
            if (!rowsByPosition.TryGetValue(record.Position, out var row) ||
                row.ReleaseId != record.ReleaseId || row.ContentHash != record.ContentHash)
                throw new InvalidOperationException("RecipeActivationReleaseBindingMismatch");
            facts.Add(new ActivationReleaseFact(record, row.AuditSequence));
        }
        return facts;
    }

    private static IReadOnlyList<ActivationContractFact> ReadActivationContractFacts(sqlite3 database,
        IReadOnlyList<PlcResultContractRevision> contracts, StoreDeadline deadline)
    {
        if (contracts.Count == 0 || !AuditChainDatabase.TableExists(database,
                "plc_result_contract_events", deadline))
            return Array.Empty<ActivationContractFact>();
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,RevisionContentHash,ContractId,ContractVersion,ContractHash,AuditSequence
            FROM plc_result_contract_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var revisionHash = SqliteNative.ColumnText(statement, 1) ?? string.Empty;
            var contractId = SqliteNative.ColumnText(statement, 2) ?? string.Empty;
            var contractVersion = SqliteNative.ColumnText(statement, 3) ?? string.Empty;
            var contractHash = SqliteNative.ColumnText(statement, 4) ?? string.Empty;
            var auditSequence = SqliteNative.ColumnInt64(statement, 5);
            if (position < 1 || auditSequence < 1 || !IsActivationHash(revisionHash) ||
                !IsActivationHash(contractHash))
                throw new InvalidOperationException("RecipeActivationPlcContractBindingMismatch");
            return (Position: position, RevisionHash: revisionHash, ContractId: contractId,
                ContractVersion: contractVersion, ContractHash: contractHash, AuditSequence: auditSequence);
        });
        if (rows.Select(value => value.Position).Distinct().Count() != rows.Count)
            throw new InvalidOperationException("RecipeActivationPlcContractBindingMismatch");
        var rowsByPosition = rows.ToDictionary(value => value.Position);
        var facts = new List<ActivationContractFact>(contracts.Count);
        foreach (var revision in contracts)
        {
            if (!rowsByPosition.TryGetValue(revision.Position, out var row) ||
                row.RevisionHash != revision.ContentHash || row.ContractId != revision.Contract.Id ||
                row.ContractVersion != revision.Contract.Version || row.ContractHash != revision.Contract.ContentHash)
                throw new InvalidOperationException("RecipeActivationPlcContractBindingMismatch");
            facts.Add(new ActivationContractFact(revision, row.AuditSequence));
        }
        return facts;
    }

    private static IReadOnlyList<ActivationCameraFact> ReadActivationCameraFacts(sqlite3 database,
        StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "camera_setup_events", deadline))
            return Array.Empty<ActivationCameraFact>();
        var sequences = AuditChainDatabase.Read(database, @"
            SELECT CameraPosition,Sequence FROM audit_entries
            WHERE CameraPosition IS NOT NULL ORDER BY Sequence;", deadline, statement =>
            (Position: SqliteNative.ColumnInt64(statement, 0),
                AuditSequence: SqliteNative.ColumnInt64(statement, 1)));
        if (sequences.Select(value => value.Position).Distinct().Count() != sequences.Count ||
            sequences.Any(value => value.Position < 1 || value.AuditSequence < 1))
            throw new InvalidOperationException("RecipeActivationCameraAuditBindingMismatch");
        var sequenceByPosition = sequences.ToDictionary(value => value.Position);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Payload FROM camera_setup_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 1) ?? string.Empty;
            if (encoded.Length == 0)
                throw new InvalidOperationException("RecipeActivationCameraBindingMismatch");
            byte[] payload;
            try { payload = Convert.FromBase64String(encoded); }
            catch (FormatException exception)
            { throw new InvalidOperationException("RecipeActivationCameraBindingMismatch", exception); }
            if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeActivationCameraBindingMismatch");
            return (Position: position, Payload: payload);
        });
        var result = new List<ActivationCameraFact>(rows.Count);
        foreach (var row in rows)
        {
            if (!sequenceByPosition.TryGetValue(row.Position, out var sequence))
                throw new InvalidOperationException("RecipeActivationCameraAuditBindingMismatch");
            var value = CameraSetupStorageCodec.Decode(row.Payload, row.Position);
            result.Add(new ActivationCameraFact(value, sequence.AuditSequence));
        }
        return result;
    }

    private static IReadOnlyList<ActivationImagingFact> ReadActivationImagingFacts(sqlite3 database,
        StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "imaging_setup_revisions", deadline))
            return Array.Empty<ActivationImagingFact>();
        var sequences = AuditChainDatabase.Read(database, @"
            SELECT ImagingPosition,Sequence FROM audit_entries
            WHERE ImagingPosition IS NOT NULL ORDER BY Sequence;", deadline, statement =>
            (Position: SqliteNative.ColumnInt64(statement, 0),
                AuditSequence: SqliteNative.ColumnInt64(statement, 1)));
        if (sequences.Select(value => value.Position).Distinct().Count() != sequences.Count ||
            sequences.Any(value => value.Position < 1 || value.AuditSequence < 1))
            throw new InvalidOperationException("RecipeActivationImagingAuditBindingMismatch");
        var sequenceByPosition = sequences.ToDictionary(value => value.Position);
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Payload FROM imaging_setup_revisions ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var encoded = SqliteNative.ColumnText(statement, 1) ?? string.Empty;
            if (encoded.Length == 0)
                throw new InvalidOperationException("RecipeActivationImagingBindingMismatch");
            byte[] payload;
            try { payload = Convert.FromBase64String(encoded); }
            catch (FormatException exception)
            { throw new InvalidOperationException("RecipeActivationImagingBindingMismatch", exception); }
            if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeActivationImagingBindingMismatch");
            return (Position: position, Payload: payload);
        });
        var result = new List<ActivationImagingFact>(rows.Count);
        foreach (var row in rows)
        {
            if (!sequenceByPosition.TryGetValue(row.Position, out var sequence))
                throw new InvalidOperationException("RecipeActivationImagingAuditBindingMismatch");
            var value = ImagingSetupRevisionStorageCodec.Decode(row.Payload, row.Position);
            result.Add(new ActivationImagingFact(value, sequence.AuditSequence));
        }
        return result;
    }

    private static IReadOnlyList<ActivationGovernanceFact> ReadActivationGovernanceFacts(
        sqlite3 database, StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline))
            return Array.Empty<ActivationGovernanceFact>();
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,RecordContentHash,Payload,AuditSequence
            FROM calibration_governance_events ORDER BY Position;", deadline, statement =>
        {
            var position = SqliteNative.ColumnInt64(statement, 0);
            var kind = SqliteNative.ColumnText(statement, 1) ?? string.Empty;
            var contentHash = SqliteNative.ColumnText(statement, 2) ?? string.Empty;
            var encoded = SqliteNative.ColumnText(statement, 3) ?? string.Empty;
            var auditSequence = SqliteNative.ColumnInt64(statement, 4);
            if (position < 1 || kind.Length == 0 || !IsActivationHash(contentHash) || encoded.Length == 0 ||
                auditSequence < 1)
                throw new InvalidOperationException("RecipeActivationCalibrationBindingMismatch");
            byte[] payload;
            try { payload = Convert.FromBase64String(encoded); }
            catch (FormatException exception)
            { throw new InvalidOperationException("RecipeActivationCalibrationBindingMismatch", exception); }
            if (!string.Equals(Convert.ToBase64String(payload), encoded, StringComparison.Ordinal))
                throw new InvalidOperationException("RecipeActivationCalibrationBindingMismatch");
            return (Position: position, Kind: kind, ContentHash: contentHash, Payload: payload,
                AuditSequence: auditSequence);
        });
        var result = new List<ActivationGovernanceFact>(rows.Count);
        foreach (var row in rows)
        {
            var value = CalibrationGovernanceCodec.Decode(row.Kind, row.Payload);
            if (CalibrationGovernanceCodec.ContentHash(value) != row.ContentHash)
                throw new InvalidOperationException("RecipeActivationCalibrationBindingMismatch");
            result.Add(new ActivationGovernanceFact(row.Position, row.AuditSequence, row.ContentHash, value));
        }
        return result;
    }

    internal static void VerifyRecipeActivationAuditPayload(sqlite3 database, long position,
        byte[] auditPayload, RecipeActivationStoreOptions options, StoreDeadline deadline,
        CalibrationGovernanceStoreOptions? governanceOptions = null)
    {
        var row = ReadRecipeActivationRows(database, options, deadline,
            CreateCalibrationProfileResolver(database, governanceOptions, deadline))
            .SingleOrDefault(value => value.Position == position);
        if (row is null) throw new InvalidOperationException("RecipeActivationAuditBindingMismatch");
        var expected = EncodeRecipeActivationAuditBinding(row.Position, row.Record, row.PreviousHash,
            row.PayloadHash, row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash,
            row.AuthorizationEventId, row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.Payload);
        if (!auditPayload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("RecipeActivationAuditPayloadMismatch");
        ValidateRecipeActivationAuditReferences(database, row, deadline);
    }

    internal static void VerifyRecipeActivationHistory(sqlite3 database, RecipeActivationStoreOptions options,
        RecipeReleaseStoreOptions releaseOptions, PlcResultContractStoreOptions contractOptions,
        StoreDeadline deadline, CalibrationGovernanceStoreOptions? governanceOptions = null)
    {
        var rows = ReadRecipeActivationRows(database, options, deadline,
            CreateCalibrationProfileResolver(database, governanceOptions, deadline));
        releaseOptions.Validate();
        contractOptions.Validate();
        var releases = ReadRecipeReleaseRows(database, releaseOptions, deadline)
            .Select(value => value.Record).ToArray();
        var contracts = ReadPlcResultContractRows(database, contractOptions, deadline)
            .Select(value => value.Revision).ToArray();
        ValidateRecipeActivationHistory(database, options, rows, releases, contracts, deadline);
    }

    internal static Func<CalibrationProfileReference, PublishedCalibrationProfileVersion?>?
        CreateCalibrationProfileResolver(sqlite3 database, CalibrationGovernanceStoreOptions? options,
            StoreDeadline deadline)
    {
        if (options is null || !AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline))
            return null;
        var profiles = ReadCalibrationGovernanceLedgerUnchecked(database, options, deadline)
            .Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload))
            .OfType<PublishedCalibrationProfileVersion>().ToArray();
        return reference => profiles.SingleOrDefault(value => value.Reference == reference);
    }

    internal static (IReadOnlyList<object> Records, long Position, string? ContentHash)
        ReadCalibrationGovernanceRecords(sqlite3 database, CalibrationGovernanceStoreOptions? options,
            StoreDeadline deadline)
    {
        if (options is null || !AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline))
            return (Array.Empty<object>(), 0, null);
        var stored = ReadCalibrationGovernanceLedgerUnchecked(database, options, deadline);
        var records = stored.Select(value => CalibrationGovernanceCodec.Decode(value.Kind, value.Payload))
            .ToArray();
        var last = stored.LastOrDefault();
        return (records, last?.Position ?? 0, last?.RecordContentHash);
    }

    internal static IReadOnlyDictionary<string, CameraSetupStoreSnapshot> ReadCameraSetupStates(
        sqlite3 database, IReadOnlyList<RecipeReleaseRecord> releases, StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "camera_setup_events", deadline))
            return new Dictionary<string, CameraSetupStoreSnapshot>(StringComparer.Ordinal);
        var roles = AuditChainDatabase.Read(database,
            "SELECT DISTINCT LogicalRole FROM camera_setup_events ORDER BY LogicalRole;", deadline,
            statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty)
            .Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
        foreach (var release in releases)
            if (release.Source.Content.CameraRole is { Length: > 0 } role) roles.Add(role);
        return roles.ToDictionary(value => value, value => ReadCameraSetupState(database, value, deadline),
            StringComparer.Ordinal);
    }

    internal static IReadOnlyDictionary<string, ImagingSetupStoreSnapshot> ReadImagingSetupStates(
        sqlite3 database, IReadOnlyList<RecipeReleaseRecord> releases, StoreDeadline deadline)
    {
        if (!AuditChainDatabase.TableExists(database, "imaging_setup_revisions", deadline))
            return new Dictionary<string, ImagingSetupStoreSnapshot>(StringComparer.Ordinal);
        var roles = AuditChainDatabase.Read(database,
            "SELECT DISTINCT LogicalRole FROM imaging_setup_revisions ORDER BY LogicalRole;", deadline,
            statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty)
            .Where(value => value.Length != 0).ToHashSet(StringComparer.Ordinal);
        foreach (var release in releases)
            if (release.Source.Content.CameraRole is { Length: > 0 } role) roles.Add(role);
        return roles.ToDictionary(value => value, value => ReadImagingSetupState(database, value, deadline),
            StringComparer.Ordinal);
    }

    private static void InsertRecipeActivationEvent(sqlite3 database, RecipeActivationStoredEvent stored,
        StoreDeadline deadline)
    {
        var record = stored.Record;
        AuditChainDatabase.Execute(database, @"
            INSERT INTO recipe_activation_events
                (Position,PreviousHash,ActivationId,AttemptId,OperationId,OutcomeState,
                 AdmissionPosition,AdmissionActivationId,AdmissionContentHash,
                 PreviousActivationPosition,PreviousActivationId,PreviousActivationContentHash,
                 CandidateKey,CandidateVersion,CandidateContentHash,ReleaseId,ReleaseRecordContentHash,
                 ResultingRecipeKey,ResultingRecipeVersion,ResultingRecipeContentHash,EvidenceKind,
                 ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,AuthorizationPolicyId,
                 AuthorizationPolicyVersion,AuthorizationPolicyHash,ChangeReason,AuthorizationTarget,
                 RecordedAtUtc,RecordContentHash,PayloadHash,Payload,CommandEventId,CommandAuditSequence,
                 CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,AuthorizationAuditHash,
                 AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            Number(stored.Position), stored.PreviousHash, record.ActivationId.ToString("D"), record.AttemptId.ToString("D"),
            record.OperationId.ToString("D"), Number((int)record.Outcome.State), record.AdmissionReference?.Position.ToString(CultureInfo.InvariantCulture),
            record.AdmissionReference?.ActivationId.ToString("D"), record.AdmissionReference?.ContentHash,
            record.PreviousActivation?.Position.ToString(CultureInfo.InvariantCulture), record.PreviousActivation?.ActivationId.ToString("D"),
            record.PreviousActivation?.ContentHash, record.Candidate.Id, record.Candidate.Version, record.Candidate.ContentHash,
            record.ReleaseId.ToString("D"), record.ReleaseRecordContentHash, record.ResultingRecipe?.Id, record.ResultingRecipe?.Version,
            record.ResultingRecipe?.ContentHash, Number((int)record.EvidenceKind), record.ActorPrincipalId?.ToString("D"),
            record.ActorSessionId?.ToString("D"), record.ActorAuthorizationRevision?.ToString(CultureInfo.InvariantCulture),
            record.AuthorizationPolicy?.Id, record.AuthorizationPolicy?.Version, record.AuthorizationPolicy?.ContentHash,
            record.ChangeReason, record.AuthorizationTarget, record.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            record.ContentHash, stored.PayloadHash, Convert.ToBase64String(stored.Payload), stored.CommandEventId?.ToString("D"),
            stored.CommandAuditSequence?.ToString(CultureInfo.InvariantCulture), stored.CommandAuditHash,
            stored.AuthorizationEventId?.ToString("D"), stored.AuthorizationAuditSequence?.ToString(CultureInfo.InvariantCulture),
            stored.AuthorizationAuditHash, Number(stored.AuditSequence), stored.AuditHash);
    }

    private static void ValidateRecipeActivationAuditReferences(sqlite3 database,
        RecipeActivationStoredEvent row, StoreDeadline deadline)
    {
        if (row.CommandEventId is null || row.CommandAuditSequence is null ||
            row.CommandAuditSequence.Value < 1 || !IsActivationHash(row.CommandAuditHash))
            throw new InvalidOperationException("RecipeActivationCommandAuditMissing");
        var command = ReadActivationCommandReference(database, row.CommandEventId.Value, row.Record, deadline);
        if (command.Sequence != row.CommandAuditSequence.Value || command.Hash != row.CommandAuditHash)
            throw new InvalidOperationException("RecipeActivationCommandAuditBindingMismatch");

        if (row.AuthorizationEventId is null || row.AuthorizationAuditSequence is null ||
            row.AuthorizationAuditSequence.Value < 1 || !IsActivationHash(row.AuthorizationAuditHash))
            throw new InvalidOperationException("RecipeActivationAuthorizationAuditMissing");
        var authorization = ReadActivationAuthorizationReference(database, row.AuthorizationEventId.Value,
            row.Record, deadline);
        if (authorization is null || authorization.Sequence != row.AuthorizationAuditSequence.Value ||
            authorization.Hash != row.AuthorizationAuditHash)
            throw new InvalidOperationException("RecipeActivationAuthorizationAuditBindingMismatch");

        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Kind,ActivationPosition,Payload,Hash FROM audit_entries
            WHERE Sequence=? AND Kind=? AND ActivationPosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Kind: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                Position: SqliteNative.ColumnInt64(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 4) ?? string.Empty),
            Number(row.AuditSequence), RecipeActivationEventKind, Number(row.Position)).SingleOrDefault();
        if (audit == default || audit.Sequence != row.AuditSequence || audit.Position != row.Position ||
            audit.Hash != row.AuditHash)
            throw new InvalidOperationException("RecipeActivationAuditBindingMismatch");
        byte[] payload;
        try { payload = Convert.FromBase64String(audit.Payload); }
        catch (FormatException exception) { throw new InvalidOperationException("RecipeActivationAuditPayloadInvalid", exception); }
        var expected = EncodeRecipeActivationAuditBinding(row.Position, row.Record, row.PreviousHash,
            row.PayloadHash, row.CommandEventId, row.CommandAuditSequence, row.CommandAuditHash,
            row.AuthorizationEventId, row.AuthorizationAuditSequence, row.AuthorizationAuditHash, row.Payload);
        if (!payload.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("RecipeActivationAuditPayloadMismatch");
    }

    private static ActivationCommandAuditReference ReadActivationCommandReference(sqlite3 database,
        Guid eventId, RecipeActivationRecord record, StoreDeadline deadline)
    {
        var result = AuditChainDatabase.Read(database, @"
            SELECT Position,CorrelationId,CommandKind,Phase,Disposition FROM command_facts
            WHERE EventId=? LIMIT 2;", deadline, statement =>
        {
            var disposition = SqliteNative.ColumnInt64Nullable(statement, 4);
            return new ActivationCommandAuditReference(SqliteNative.ColumnInt64(statement, 0),
                ParseActivationGuid(SqliteNative.ColumnText(statement, 1), "RecipeActivationCommandCorrelationInvalid"),
                ReadActivationEnum<AuditedCommandKind>(SqliteNative.ColumnInt64(statement, 2), "RecipeActivationCommandKindInvalid"),
                ReadActivationEnum<CommandAuditPhase>(SqliteNative.ColumnInt64(statement, 3), "RecipeActivationCommandPhaseInvalid"),
                disposition is null ? null : ReadActivationEnum<CommandDisposition>(disposition.Value, "RecipeActivationCommandDispositionInvalid"));
        }, eventId.ToString("D")).SingleOrDefault();
        if (result is null || result.CorrelationId != record.OperationId ||
            result.CommandKind != AuditedCommandKind.ActivateRecipe ||
            (record.Outcome.State == RecipeActivationOutcomeState.Admitted &&
                (result.Phase != CommandAuditPhase.Outcome || result.Disposition != CommandDisposition.Accepted)))
            throw new InvalidOperationException("RecipeActivationCommandAuditBindingMismatch");
        var audit = AuditChainDatabase.Read(database, @"
            SELECT Sequence,Hash FROM audit_entries WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0), Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            Number(result.Position)).SingleOrDefault();
        if (audit == default || !IsActivationHash(audit.Hash))
            throw new InvalidOperationException("RecipeActivationCommandAuditMissing");
        return result with { Sequence = audit.Sequence, Hash = audit.Hash };
    }

    private static ActivationAuthorizationAuditReference? ReadActivationAuthorizationReference(sqlite3 database,
        Guid eventId, RecipeActivationRecord record, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0), Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty, Hash: SqliteNative.ColumnText(statement, 3) ?? string.Empty));
        var matches = new List<ActivationAuthorizationAuditReference>();
        var schema = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        var stationId = ReadActivationStationId(database, deadline);
        foreach (var row in rows)
        {
            try
            {
                var payload = Convert.FromBase64String(row.Payload);
                if (!IsActivationHash(row.Hash)) continue;
                IdentityAuditEvent.VerifyPayload(payload, row.Ordinal, stationId, schema);
                if (Guid.TryParseExact(IdentityAuditEvent.DecodeEventId(payload), "D", out var parsed) && parsed == eventId &&
                    IdentityAuditEvent.MatchesRecipeActivationAuthorization(payload, row.Ordinal,
                        stationId, record))
                    matches.Add(new ActivationAuthorizationAuditReference(row.Sequence, row.Hash));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
            { }
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string ReadActivationStationId(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1 LIMIT 1;", deadline) ??
        throw new InvalidOperationException("RecipeActivationStationIdentityMissing");

    private static byte[] EncodeRecipeActivationAuditBinding(long position, RecipeActivationRecord record,
        string? previousHash, string payloadHash, Guid? commandEventId, long? commandSequence, string? commandHash,
        Guid? authorizationEventId, long? authorizationSequence, string? authorizationHash, byte[] payload) =>
        AuditCanonical.Encode("RecipeActivationLedgerEntryV1", Number(position), record.ActivationId.ToString("D"),
            record.AttemptId.ToString("D"), record.OperationId.ToString("D"), record.Outcome.ContentHash,
            record.ContentHash, previousHash, payloadHash, commandEventId?.ToString("D"),
            commandSequence?.ToString(CultureInfo.InvariantCulture), commandHash, authorizationEventId?.ToString("D"),
            authorizationSequence?.ToString(CultureInfo.InvariantCulture), authorizationHash, Convert.ToBase64String(payload));

    private static string ReadRecipeActivationAuditHash(sqlite3 database, long sequence, long position,
        StoreDeadline deadline) => AuditChainDatabase.Read(database, @"
            SELECT Hash FROM audit_entries WHERE Sequence=? AND Kind=? AND ActivationPosition=? LIMIT 2;", deadline,
        statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty, Number(sequence), RecipeActivationEventKind,
        Number(position)).SingleOrDefault() ?? throw new InvalidOperationException("RecipeActivationAuditEntryMissing");

    private static bool IsActivationHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static Guid ParseActivationGuid(string? value, string reason) =>
        Guid.TryParseExact(value, "D", out var result) && result != Guid.Empty ? result : throw new InvalidOperationException(reason);
    private static Guid? ParseNullableActivationGuid(string? value, string reason) => value is null ? null : ParseActivationGuid(value, reason);
    private static T ReadActivationEnum<T>(long value, string reason) where T : struct, Enum =>
        value is >= 0 and <= int.MaxValue && Enum.IsDefined(typeof(T), (int)value)
            ? (T)Enum.ToObject(typeof(T), (int)value) : throw new InvalidOperationException(reason);
    private static bool GuidColumnEquals(Guid? value, sqlite3_stmt statement, int index) =>
        value is null ? SqliteNative.ColumnText(statement, index) is null :
        value.Value == ParseActivationGuid(SqliteNative.ColumnText(statement, index), "RecipeActivationGuidInvalid");
    private static bool ActivationReferenceColumnsEquals(RecipeActivationReference? value,
        sqlite3_stmt statement, int positionIndex, int activationIdIndex, int contentHashIndex)
    {
        var position = SqliteNative.ColumnInt64Nullable(statement, positionIndex);
        var activationId = SqliteNative.ColumnText(statement, activationIdIndex);
        var contentHash = SqliteNative.ColumnText(statement, contentHashIndex);
        if (value is null)
            return position is null && activationId is null && contentHash is null;
        return position == value.Position && activationId is not null &&
            Guid.TryParseExact(activationId, "D", out var parsed) && parsed == value.ActivationId &&
            contentHash == value.ContentHash;
    }
    private static bool NullableLongEquals(long? value, sqlite3_stmt statement, int index) =>
        value == SqliteNative.ColumnInt64Nullable(statement, index);
    private static bool ContractReferenceEquals(RecipeContractReference? value, sqlite3_stmt statement, int id, int version, int hash) =>
        value is null ? SqliteNative.ColumnText(statement, id) is null && SqliteNative.ColumnText(statement, version) is null && SqliteNative.ColumnText(statement, hash) is null :
        value.Id == SqliteNative.ColumnText(statement, id) && value.Version == SqliteNative.ColumnText(statement, version) && value.ContentHash == SqliteNative.ColumnText(statement, hash);
    private static RecipeReference? ReadRecipeColumns(sqlite3_stmt statement, int index) =>
        SqliteNative.ColumnText(statement, index) is not { } id ? null :
        new RecipeReference(id, SqliteNative.ColumnText(statement, index + 1) ?? throw new InvalidOperationException("RecipeActivationRecipeVersionMissing"),
            SqliteNative.ColumnText(statement, index + 2) ?? throw new InvalidOperationException("RecipeActivationRecipeHashMissing"));
    private static bool RecipeEquals(RecipeReference? left, RecipeReference? right) => left is null && right is null ||
        left is not null && right is not null && left.Id == right.Id && left.Version == right.Version && left.ContentHash == right.ContentHash;

    private sealed record ActivationReleaseFact(RecipeReleaseRecord Record, long AuditSequence);
    private sealed record ActivationContractFact(PlcResultContractRevision Revision, long AuditSequence);
    private sealed record ActivationCameraFact(CameraSetupEvent Event, long AuditSequence);
    private sealed record ActivationImagingFact(ImagingSetupRevision Revision, long AuditSequence);
    private sealed record ActivationGovernanceFact(long Position, long AuditSequence,
        string ContentHash, object Record);

    private sealed record ActivationCommandAuditReference(long Position, Guid CorrelationId,
        AuditedCommandKind CommandKind, CommandAuditPhase Phase, CommandDisposition? Disposition,
        long Sequence = 0, string Hash = "");
    private sealed record ActivationAuthorizationAuditReference(long Sequence, string Hash);
    internal sealed record RecipeActivationStoredEvent(long Position, RecipeActivationRecord Record,
        string? PreviousHash, string PayloadHash, byte[] Payload, Guid? CommandEventId,
        long? CommandAuditSequence, string? CommandAuditHash, Guid? AuthorizationEventId,
        long? AuthorizationAuditSequence, string? AuthorizationAuditHash, long AuditSequence, string AuditHash);
}

internal sealed record RecipeActivationCommandState(bool Enabled,
    IReadOnlyList<RecipeDraftRevision> DraftHistory, IReadOnlyList<RecipeReleaseRecord> Releases,
    IReadOnlyList<PlcResultContractRevision> PlcResultContracts,
    IReadOnlyList<RecipeActivationRecord> Records, RecipeActivationRecord? Current,
    RecipeActivationRecord? PendingAdmission,
    IReadOnlyDictionary<string, CameraSetupStoreSnapshot>? Cameras = null,
    IReadOnlyDictionary<string, ImagingSetupStoreSnapshot>? ImagingSetups = null,
    IReadOnlyList<object>? CalibrationGovernanceRecords = null,
    long CalibrationGovernancePosition = 0,
    string? CalibrationGovernanceContentHash = null,
    IReadOnlyDictionary<Guid, CommandAuditFact>? AdmissionCommands = null);

internal sealed record RecipeActivationMutation(RecipeActivationRecord Record);
