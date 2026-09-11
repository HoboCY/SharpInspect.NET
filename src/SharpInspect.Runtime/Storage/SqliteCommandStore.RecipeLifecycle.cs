using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal ValueTask<IdentityWriteResult> UpdateRecipeLifecycleCommandAsync(RuntimeCommand command,
        Func<IdentityAuthorityState, RecipeLifecycleCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    internal static RecipeLifecycleCommandState ReadRecipeLifecycleCommandState(sqlite3 database,
        ProductionStoreOptions options, StoreDeadline deadline)
    {
        if (options.RecipeLifecycle is not { } lifecycle)
            return new(false, Array.Empty<RecipeDraftRevision>(), Array.Empty<RecipeReleaseRecord>(),
                Array.Empty<RecipeActivationRecord>(), Array.Empty<RecipeLifecycleRecord>(), Array.Empty<RecipeSelectionRevision>());
        VerifyRecipeLifecycleReadGuard(database, options, deadline);
        var draftsOption = options.RecipeDrafts ?? throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        return ReadRecipeLifecycleBusinessState(database, lifecycle, draftsOption, options.RecipeReleases,
            options.RecipeActivations, options.PlcResultContracts, options.CalibrationGovernance, options.RecipeSelections, deadline);
    }

    internal static RecipeLifecycleCommandState ReadRecipeLifecycleBusinessState(sqlite3 database,
        RecipeLifecycleStoreOptions lifecycle, RecipeDraftStoreOptions draftsOption,
        RecipeReleaseStoreOptions? releaseOptions, RecipeActivationStoreOptions? activationOptions,
        PlcResultContractStoreOptions? contractOptions, CalibrationGovernanceStoreOptions? governanceOptions,
        RecipeSelectionStoreOptions? selectionOptions, StoreDeadline deadline)
    {
        RequireConfiguredRecipeLifecycle(database, lifecycle, deadline);
        ValidateRecipeDraftHistory(database, draftsOption, deadline);
        var drafts = ReadAllRecipeDraftHistory(database, deadline);
        var releases = Array.Empty<RecipeReleaseRecord>();
        if (releaseOptions is not null)
        {
            ValidateRecipeReleaseHistory(database, releaseOptions, draftsOption,
                ReadCalibrationAcceptancePolicies(database, governanceOptions, deadline), drafts,
                deadline, governanceOptions);
            releases = ReadRecipeReleaseRows(database, releaseOptions, deadline).Select(value => value.Record).ToArray();
        }
        var activations = Array.Empty<RecipeActivationRecord>();
        if (activationOptions is not null)
        {
            RequireConfiguredRecipeActivations(database, activationOptions, deadline);
            var contractsOption = contractOptions ?? throw new InvalidOperationException("PlcResultContractConfigurationRequired");
            ValidatePlcResultContractHistory(database, contractsOption, deadline);
            var contracts = ReadPlcResultContractRows(database, contractsOption, deadline).Select(value => value.Revision).ToArray();
            var rows = ReadRecipeActivationRows(database, activationOptions, deadline,
                CreateCalibrationProfileResolver(database, governanceOptions, deadline));
            ValidateRecipeActivationHistory(database, activationOptions, rows, releases, contracts, deadline, lifecycle);
            activations = rows.Select(value => value.Record).ToArray();
        }
        var selections = Array.Empty<RecipeSelectionRevision>();
        if (selectionOptions is not null)
        {
            RequireConfiguredRecipeSelections(database, selectionOptions, deadline);
            selections = ReadRecipeSelectionRows(database, selectionOptions, deadline).Select(value => value.Revision).ToArray();
        }
        var lifecycleRows = ReadRecipeLifecycleRows(database, lifecycle, deadline);
        ValidateRecipeLifecycleHistory(lifecycleRows, drafts, releases, activations, selections);
        ValidateRecipeLifecycleAuthority(database, lifecycleRows, drafts, releases, activations, selections, deadline);
        return new(true, drafts, releases, activations, lifecycleRows.Select(value => value.Record).ToArray(), selections);
    }

    internal static IReadOnlyList<RecipeLifecycleRecord> ReadRecipeLifecycleRecords(sqlite3 database,
        RecipeLifecycleStoreOptions? options, StoreDeadline deadline)
    {
        if (options is null) return Array.Empty<RecipeLifecycleRecord>();
        RequireConfiguredRecipeLifecycle(database, options, deadline);
        return ReadRecipeLifecycleRows(database, options, deadline).Select(value => value.Record).ToArray();
    }

    private void AppendRecipeLifecycleIdentityMutation(sqlite3 database, IdentityUpdate update,
        RecipeLifecycleCommandState state, RuntimeCommand command, StoreDeadline deadline)
    {
        var record = update.RecipeLifecycle?.Record ?? throw new InvalidOperationException("RecipeLifecycleMutationMissing");
        var options = _options.RecipeLifecycle ?? throw new InvalidOperationException("RecipeLifecycleConfigurationRequired");
        var abandon = command as AbandonRecipeDraftCommand;
        var retire = command as RetireReleasedRecipeCommand;
        if (!state.Enabled || update.Events.Count != 1 || update.CommandFacts is not { Count: 1 } ||
            record.Position != state.Lifecycle.Count + 1L || record.PreviousHash != state.Lifecycle.LastOrDefault()?.ContentHash ||
            record.OperationId != command.CorrelationId || record.AuthorizationTarget !=
                (abandon?.AuthorizationTarget ?? retire?.AuthorizationTarget) ||
            record.Reason != (abandon?.Reason ?? retire?.Reason) ||
            abandon is not null && (record.Kind != RecipeLifecycleKind.DraftAbandoned ||
                record.SourceDraft.DraftId != abandon.DraftId || record.SourceDraft.Revision != abandon.ExpectedRevision ||
                record.SourceDraft.RevisionContentHash != abandon.ExpectedRevisionContentHash) ||
            retire is not null && (record.Kind != RecipeLifecycleKind.ReleasedRetired || record.Recipe != retire.Recipe ||
                record.ReleaseId != retire.ReleaseId || record.ReleaseRecordContentHash != retire.ReleaseRecordContentHash ||
                record.ObservedActive != retire.ExpectedActive))
            throw new InvalidOperationException("RecipeLifecycleIdentityMutationMismatch");
        var payload = RecipeLifecycleStorageCodec.Encode(record);
        if (payload.Length > options.MaximumPayloadBytes) throw new InvalidOperationException("RecipeLifecyclePayloadCapacityExceeded");
        var previous = ReadRecipeLifecycleRows(database, options, deadline);
        if (previous.Count >= options.MaxEvents) throw new InvalidOperationException("RecipeLifecycleEntryCapacityExceeded");
        if (checked(previous.Sum(value => (long)value.Payload.Length) + payload.Length) > options.MaxTotalBytes)
            throw new InvalidOperationException("RecipeLifecycleTotalCapacityExceeded");
        var fact = ReadLifecycleCommandReference(database, update.CommandFacts[0].EventId, record, deadline);
        var authorization = ReadLifecycleAuthorizationReference(database, update.Events[0].EventId, record, deadline);
        var row = new RecipeLifecycleStoredRow(record, Convert.ToHexString(SHA256.HashData(payload)), payload,
            update.CommandFacts[0].EventId, fact.Sequence, fact.Hash, update.Events[0].EventId,
            authorization.Sequence, authorization.Hash, 0, string.Empty);
        var audit = AuditChainDatabase.AppendRecipeLifecycleMetadata(database, _policy!, _signingKey!,
            RecipeLifecycleEventAuditKind, EncodeAuditBinding(row), options, deadline);
        row = row with { AuditSequence = audit.Sequence, AuditHash = audit.Hash };
        AuditChainDatabase.Execute(database, @"INSERT INTO recipe_lifecycle_events
            (Position,PreviousHash,TransitionId,OperationId,Kind,DraftId,SourceRevision,ReleaseId,
             RecordContentHash,PayloadHash,Payload,CommandEventId,CommandAuditSequence,CommandAuditHash,
             AuthorizationEventId,AuthorizationAuditSequence,AuthorizationAuditHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            LifecycleNumber(record.Position), record.PreviousHash, record.TransitionId.ToString("D"), record.OperationId.ToString("D"),
            LifecycleNumber((int)record.Kind), record.SourceDraft.DraftId.ToString("D"), LifecycleNumber(record.SourceDraft.Revision),
            record.ReleaseId?.ToString("D"), record.ContentHash, row.PayloadHash, Convert.ToBase64String(payload),
            row.CommandEventId.ToString("D"), LifecycleNumber(row.CommandAuditSequence), row.CommandAuditHash,
            row.AuthorizationEventId.ToString("D"), LifecycleNumber(row.AuthorizationAuditSequence), row.AuthorizationAuditHash,
            LifecycleNumber(row.AuditSequence), row.AuditHash);
        var all = previous.Append(row).ToArray();
        ValidateRecipeLifecycleHistory(all, state.DraftHistory, state.Releases, state.Activations, state.Selections);
        ValidateRecipeLifecycleAuthority(database, all, state.DraftHistory, state.Releases, state.Activations, state.Selections, deadline);
    }

    private static (long Sequence, string Hash) ReadLifecycleCommandReference(sqlite3 database,
        Guid eventId, RecipeLifecycleRecord record, StoreDeadline deadline)
    {
        var facts = AuditChainDatabase.Read(database, @"SELECT Position,CorrelationId,RuntimeEpoch,CommandKind,Phase,Disposition,
            AuthenticatedHumanPrincipalId,ClaimedSessionId,ClaimedStepUpGrantId,OccurredAtUtc,ReasonCode
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline,
            row => Enumerable.Range(0, 11).Select(column => SqliteNative.ColumnText(row, column)).ToArray(), eventId.ToString("D"));
        var expected = new[] { record.OperationId.ToString("D"), record.RuntimeEpoch.ToString("D"),
            LifecycleNumber((int)LifecycleCommandKind(record.Kind)), LifecycleNumber((int)CommandAuditPhase.Outcome),
            LifecycleNumber((int)CommandDisposition.Accepted), record.ActorPrincipalId.ToString("D"),
            record.ActorSessionId.ToString("D"), record.StepUpGrantId.ToString("D"),
            record.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), LifecycleReason(record.Kind) };
        if (facts.Count != 1 || !facts[0].Skip(1).SequenceEqual(expected))
            throw new InvalidOperationException("RecipeLifecycleCommandBindingMismatch");
        var audits = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", deadline,
            row => (Sequence: SqliteNative.ColumnInt64(row, 0), Hash: SqliteNative.ColumnText(row, 1)!), facts[0][0]);
        if (audits.Count != 1) throw new InvalidOperationException("RecipeLifecycleCommandAuditMissing");
        return audits[0];
    }

    private static (long Sequence, string Hash) ReadLifecycleAuthorizationReference(sqlite3 database,
        Guid eventId, RecipeLifecycleRecord record, StoreDeadline deadline, long? auditSequence = null)
    {
        var station = ReadContractStationId(database, deadline);
        // Replay resolves its exact immutable central reference. During append,
        // this transaction contains one newly appended identity event; its event
        // id and complete authority fields must still match the record below.
        var sequence = auditSequence ?? AuditChainDatabase.Scalar(database,
            "SELECT Sequence FROM audit_entries WHERE Kind='IdentityEvent' ORDER BY Sequence DESC LIMIT 1;", deadline);
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' AND Sequence=? LIMIT 2;", deadline,
            row => (Sequence: SqliteNative.ColumnInt64(row, 0), Position: SqliteNative.ColumnInt64(row, 1),
                Payload: SqliteNative.ColumnText(row, 2)!, Hash: SqliteNative.ColumnText(row, 3)!), LifecycleNumber(sequence));
        var matches = new List<(long Sequence, string Hash)>();
        foreach (var row in rows)
        {
            var payload = Convert.FromBase64String(row.Payload);
            var fields = DecodeContractIdentityFieldsForSelection(payload);
            if (fields is null || fields[1] != eventId.ToString("D")) continue;
            IdentityAuditEvent.VerifyPayload(payload, row.Position, station, RecipeLifecycleStoreOptions.SchemaVersion);
            var permission = record.Kind == RecipeLifecycleKind.DraftAbandoned ? Permission.AbandonRecipeDraft : Permission.RetireRecipe;
            var kind = record.Kind == RecipeLifecycleKind.DraftAbandoned ? IdentityEventKind.RecipeDraftAbandoned : IdentityEventKind.RecipeRetired;
            if (fields[2] != kind.ToString() || fields[3] != record.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture) ||
                fields[4] != station || fields[5] != record.ActorPrincipalId.ToString("D") || fields[9] != LifecycleReason(record.Kind) ||
                fields[25] != record.ActorSessionId.ToString("D") || fields[27] != record.AuthorizationPolicy.Id ||
                fields[28] != record.AuthorizationPolicy.Version || fields[29] != record.AuthorizationPolicy.ContentHash ||
                fields[30] != record.ActorPrincipalId.ToString("D") || fields[31] != record.OperationId.ToString("D") ||
                fields[32] != record.StepUpGrantId.ToString("D") || fields[33] != permission.ToString() ||
                fields[35] != LifecycleNumber(record.ActorAuthorizationRevision) || fields[37] != record.AuthorizationTarget ||
                fields[38] != record.OperationId.ToString("D") || fields[39] != LifecycleCommandKind(record.Kind).ToString() ||
                fields[42] != record.OperationId.ToString("D"))
                throw new InvalidOperationException("RecipeLifecycleAuthorizationBindingMismatch");
            matches.Add((row.Sequence, row.Hash));
        }
        if (matches.Count != 1) throw new InvalidOperationException("RecipeLifecycleAuthorizationAuditMissing");
        return matches[0];
    }

    private static AuditedCommandKind LifecycleCommandKind(RecipeLifecycleKind kind) => kind == RecipeLifecycleKind.DraftAbandoned
        ? AuditedCommandKind.AbandonRecipeDraft : AuditedCommandKind.RetireReleasedRecipe;
    private static string LifecycleReason(RecipeLifecycleKind kind) => kind == RecipeLifecycleKind.DraftAbandoned
        ? "RecipeDraftAbandoned" : "RecipeRetired";

    private static void ValidateRecipeLifecycleAuthority(sqlite3 database, IReadOnlyList<RecipeLifecycleStoredRow> rows,
        IReadOnlyList<RecipeDraftRevision> drafts, IReadOnlyList<RecipeReleaseRecord> releases,
        IReadOnlyList<RecipeActivationRecord> activations, IReadOnlyList<RecipeSelectionRevision> selections,
        StoreDeadline deadline)
    {
        // Ordering comes from the signed central chain, never equal wall-clock times.
        var draftAudit = AuditChainDatabase.Read(database, @"SELECT DraftPosition,Sequence FROM audit_entries
            WHERE Kind='RecipeDraftRevision';", deadline, value =>
            (Position: SqliteNative.ColumnInt64(value, 0), Sequence: SqliteNative.ColumnInt64(value, 1)))
            .ToDictionary(value => value.Position, value => value.Sequence);
        Dictionary<long, long> Positions(string table) => AuditChainDatabase.Read(database,
            $"SELECT Position,AuditSequence FROM {table};", deadline, value =>
                (Position: SqliteNative.ColumnInt64(value, 0), Sequence: SqliteNative.ColumnInt64(value, 1)))
            .ToDictionary(value => value.Position, value => value.Sequence);
        var releaseAudit = releases.Count == 0 ? new Dictionary<long, long>() : Positions("recipe_release_events");
        var activationAudit = activations.Count == 0 ? new Dictionary<long, long>() : Positions("recipe_activation_events");
        var selectionAudit = selections.Count == 0 ? new Dictionary<long, long>() : Positions("recipe_selection_revisions");
        foreach (var row in rows)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var record = row.Record;
            if (ReadLifecycleCommandReference(database, row.CommandEventId, record, deadline) != (row.CommandAuditSequence, row.CommandAuditHash) ||
                ReadLifecycleAuthorizationReference(database, row.AuthorizationEventId, record, deadline,
                    row.AuthorizationAuditSequence) != (row.AuthorizationAuditSequence, row.AuthorizationAuditHash))
                throw new InvalidOperationException("RecipeLifecycleAuthorityReferenceMismatch");
            var prior = rows.Where(value => value.AuditSequence < row.AuditSequence).Select(value => value.Record).ToArray();
            var earlierActivations = activations.Where(value => activationAudit[value.Position] < row.AuthorizationAuditSequence).ToArray();
            var current = RecipeLifecycleProjection.EffectiveCurrent(earlierActivations, prior);
            if (current?.Reference != record.ObservedActive)
                throw new InvalidOperationException("RecipeLifecycleObservedActiveMismatch");
            var mustClear = record.Kind == RecipeLifecycleKind.ReleasedRetired && current?.ReleaseId == record.ReleaseId;
            if (record.ClearedActive != (mustClear ? current!.Reference : null))
                throw new InvalidOperationException("RecipeLifecycleActiveClearMismatch");
            var source = drafts.Single(value => value.DraftId == record.SourceDraft.DraftId && value.Revision == record.SourceDraft.Revision);
            if (draftAudit[source.Position] >= row.AuthorizationAuditSequence)
                throw new InvalidOperationException("RecipeLifecycleSourceOrderInvalid");
            if (record.Kind == RecipeLifecycleKind.DraftAbandoned)
            {
                if (drafts.Any(value => value.DraftId == source.DraftId && value.Revision > source.Revision) ||
                    releases.Any(value => value.Source.DraftId == source.DraftId))
                    throw new InvalidOperationException("RecipeLifecycleAbandonedDraftUsed");
            }
            else
            {
                var release = releases.Single(value => value.ReleaseId == record.ReleaseId);
                if (releaseAudit[release.Position] >= row.AuthorizationAuditSequence ||
                    activations.Any(value => value.ReleaseId == record.ReleaseId &&
                        activationAudit[value.Position] > row.AuditSequence &&
                        (value.CanBeActive || value.Outcome.State == RecipeActivationOutcomeState.Admitted)))
                    throw new InvalidOperationException("RecipeLifecycleRetiredReleaseUsed");
                var impacts = RecipeLifecycleProjection.MapImpacts(selections.Where(value =>
                    selectionAudit[value.Position] < row.AuthorizationAuditSequence).ToArray(), record.Recipe!,
                    record.ReleaseId!.Value, record.ReleaseRecordContentHash!);
                if (impacts.Count != record.MapImpacts.Count || impacts.Where((value, index) =>
                    value.Selection != record.MapImpacts[index].Selection || value.Map != record.MapImpacts[index].Map ||
                    !value.AffectedCodes.SequenceEqual(record.MapImpacts[index].AffectedCodes)).Any())
                    throw new InvalidOperationException("RecipeLifecycleMapImpactIncomplete");
            }
        }
    }
}
