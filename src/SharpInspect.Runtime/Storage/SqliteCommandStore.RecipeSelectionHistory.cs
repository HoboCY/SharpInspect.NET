using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static IReadOnlyList<RecipeChangeStoredEvent> ReadRecipeChangeRows(sqlite3 database,
        RecipeSelectionStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        if (AuditChainDatabase.Scalar(database, "SELECT COUNT(*) FROM recipe_change_events;", deadline) > options.MaximumHandshakeEvents)
            throw new InvalidOperationException("RecipeChangeEventCapacityExceeded");
        return AuditChainDatabase.Read(database, @"SELECT Position,PreviousHash,EventId,RequestIdentityHash,Kind,
            ContentHash,PayloadHash,Payload,AuditSequence,AuditHash,RequestContextHash FROM recipe_change_events ORDER BY Position;", deadline,
            row =>
            {
                string Text(int column) => SqliteNative.ColumnText(row, column) ?? throw new InvalidOperationException("RecipeChangeColumnMissing");
                var payload = DecodeRecipeSelectionPayload(Text(7));
                var value = RecipeSelectionStorageCodec.DecodeHandshake(payload);
                if (value.Position != SqliteNative.ColumnInt64(row, 0) || value.EventId.ToString("D") != Text(2) ||
                    value.Request.RequestIdentityHash != Text(3) || (long)value.Kind != SqliteNative.ColumnInt64(row, 4) ||
                    value.ContentHash != Text(5) || Convert.ToHexString(SHA256.HashData(payload)) != Text(6) ||
                    value.Request.ContentHash != Text(10))
                    throw new InvalidOperationException("RecipeChangeIndexedPayloadMismatch");
                return new RecipeChangeStoredEvent(value, SqliteNative.ColumnText(row, 1), Text(6), payload,
                    SqliteNative.ColumnInt64(row, 8), Text(9));
            });
    }

    internal static void ValidateRecipeSelectionHistory(sqlite3 database, RecipeSelectionStoreOptions options,
        RecipeReleaseStoreOptions releasesOptions, RecipeActivationStoreOptions activationOptions,
        CalibrationGovernanceStoreOptions? governanceOptions, StoreDeadline deadline,
        IReadOnlyList<RecipeSelectionStoredRevision>? suppliedRevisions = null,
        IReadOnlyList<RecipeChangeStoredEvent>? suppliedChanges = null)
    {
        RequireConfiguredRecipeSelections(database, options, deadline);
        var revisions = suppliedRevisions ?? ReadRecipeSelectionRows(database, options, deadline);
        var changes = suppliedChanges ?? ReadRecipeChangeRows(database, options, deadline);
        if (changes.Count + ReadRecipeChangeAuditReserve(database, deadline) > options.MaximumHandshakeEvents)
            throw new InvalidOperationException("RecipeChangeEventCapacityExceeded");
        RequireRecipeSelectionTotalCapacity(database, options, 0, deadline);
        var releaseRows = ReadRecipeReleaseRows(database, releasesOptions, deadline);
        var releases = releaseRows.Select(value => value.Record).ToArray();
        var policies = new Dictionary<(string, string), string>();
        var maps = new Dictionary<(string, string), string>();
        RecipeSelectionStoredRevision? previous = null;
        foreach (var row in revisions)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var revision = row.Revision;
            if (revision.Position != (previous?.Revision.Position ?? 0) + 1 ||
                revision.Previous != previous?.Revision.Reference || row.PreviousHash != previous?.AuditHash ||
                row.AuditSequence <= (previous?.AuditSequence ?? 0) ||
                row.CommandAuditSequence <= (previous?.AuditSequence ?? 0) ||
                row.AuthorizationAuditSequence <= (previous?.AuditSequence ?? 0) ||
                row.AuthorizationAuditSequence >= row.CommandAuditSequence || row.CommandAuditSequence >= row.AuditSequence ||
                previous is not null && revision.RecordedAtUtc < previous.Revision.RecordedAtUtc)
                throw new InvalidOperationException("RecipeSelectionHistorySequenceInvalid");
            if (policies.TryGetValue((revision.Policy.Id, revision.Policy.Version), out var policyHash) && policyHash != revision.Policy.ContentHash)
                throw new InvalidOperationException("RecipeSelectionPolicyVersionConflict");
            policies[(revision.Policy.Id, revision.Policy.Version)] = revision.Policy.ContentHash;
            if (revision.Map is { } map)
            {
                if (maps.TryGetValue((map.Id, map.Version), out var mapHash) && mapHash != map.ContentHash)
                    throw new InvalidOperationException("RecipeSelectionMapVersionConflict");
                maps[(map.Id, map.Version)] = map.ContentHash;
            }
            var priorReleases = releaseRows.Where(value => value.AuditSequence < row.AuditSequence).ToArray();
            if (revision.ReleaseHighWatermark != priorReleases.Select(value => value.Record.Position).DefaultIfEmpty(0).Max())
                throw new InvalidOperationException("RecipeSelectionReleaseSnapshotMismatch");
            ValidateRecipeSelectionRevision(previous?.Revision, revision, releases);
            ValidateRecipeSelectionAuditRow(database, row, deadline);
            var command = ReadRecipeSelectionCommandReference(database, row.CommandEventId, revision, deadline);
            var authorization = ReadRecipeSelectionAuthorizationReference(database, row.AuthorizationEventId, revision, deadline);
            if (command != (row.CommandAuditSequence, row.CommandAuditHash) ||
                authorization != (row.AuthorizationAuditSequence, row.AuthorizationAuditHash))
                throw new InvalidOperationException("RecipeSelectionAuditReferenceMismatch");
            previous = row;
        }

        var byReference = revisions.ToDictionary(value => value.Revision.Reference);
        var activations = ReadRecipeActivationRows(database, activationOptions, deadline,
            CreateCalibrationProfileResolver(database, governanceOptions, deadline)).ToDictionary(value => value.Record.Reference);
        foreach (var activation in activations.Values.Where(value => value.Record.Actor?.IsPlcAdapter == true))
        {
            var context = activation.Record.Actor!.PlcRequestContext!;
            var observations = changes.Where(value => value.Event.Kind == RecipeChangeEventKind.RequestObserved &&
                value.Event.Request.RequestIdentityHash == context.RequestIdentityHash &&
                value.AuditSequence < activation.AuditSequence).ToArray();
            if (observations.Length != 1 || observations[0].Event.Request.Target is null ||
                !context.Matches(observations[0].Event.Request.ActivationContext()) ||
                activation.Record.OperationId != observations[0].Event.Request.OperationId)
                throw new InvalidOperationException("RecipeActivationPlcRequestAuditMissing");
            if (activation.Record.Outcome.State == RecipeActivationOutcomeState.Admitted)
            {
                var selected = revisions.LastOrDefault(value => value.AuditSequence < activation.AuditSequence)?.Revision;
                if (selected is null || selected.Reference != observations[0].Event.Request.SelectionRevision ||
                    selected.Policy.Mode != RecipeSelectionMode.PlcRequestedActivation)
                    throw new InvalidOperationException("RecipeActivationPlcSelectionNotCurrent");
            }
        }
        var episodes = new Dictionary<string, List<RecipeChangeHistoryEvent>>(StringComparer.Ordinal);
        var observedIdentities = new HashSet<string>(StringComparer.Ordinal);
        RecipeChangeStoredEvent? previousChange = null;
        foreach (var row in changes)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var value = row.Event;
            if (value.Position != (previousChange?.Event.Position ?? 0) + 1 || row.PreviousHash != previousChange?.AuditHash ||
                row.AuditSequence <= (previousChange?.AuditSequence ?? 0) ||
                previousChange is not null && value.RecordedAtUtc < previousChange.Event.RecordedAtUtc)
                throw new InvalidOperationException("RecipeChangeHistorySequenceInvalid");
            ValidateRecipeChangeSelection(value.Request, row.AuditSequence, byReference);
            if (!episodes.TryGetValue(value.Request.ContentHash, out var episode))
                episodes.Add(value.Request.ContentHash, episode = new());
            if (value.Kind == RecipeChangeEventKind.RequestObserved && !observedIdentities.Add(value.Request.RequestIdentityHash))
                throw new InvalidOperationException("RecipeChangeRequestIdentityReused");
            ValidateRecipeChangeTransition(episode, value);
            if (value.Activation is { } reference)
            {
                if (!activations.TryGetValue(reference, out var activation) || activation.AuditSequence >= row.AuditSequence ||
                    activation.Record.OperationId != value.Request.OperationId ||
                    activation.Record.Actor is not { IsPlcAdapter: true, PlcRequestContext: { } context } ||
                    !context.Matches(value.Request.ActivationContext()) || !activation.Record.IsTerminal ||
                    value.Outcome == RecipeChangeOutcome.Succeeded && !activation.Record.Outcome.Succeeded ||
                    value.Outcome is RecipeChangeOutcome.RejectedBusy or RecipeChangeOutcome.RejectedUnknownCode)
                    throw new InvalidOperationException("RecipeChangeActivationReferenceMismatch");
            }
            ValidateRecipeChangeAuditRow(database, row, deadline);
            episode.Add(value);
            previousChange = row;
        }
    }

    private static void ValidateRecipeChangeSelection(RecipeChangeRequestEvidence request, long auditSequence,
        IReadOnlyDictionary<RecipeSelectionReference, RecipeSelectionStoredRevision> revisions)
    {
        if (request.SelectionRevision is null) return; // Constructor requires the exact immutable built-in LocalOnly policy.
        if (!revisions.TryGetValue(request.SelectionRevision, out var selected) || selected.AuditSequence >= auditSequence ||
            selected.Revision.Policy.Reference != request.SelectionPolicy || selected.Revision.Map?.Reference != request.SelectionMap)
            throw new InvalidOperationException("RecipeChangeSelectionReferenceMismatch");
        var target = selected.Revision.Map?.Entries.SingleOrDefault(value => value.SelectionCode == request.SelectionCode);
        if (target?.ContentHash != request.Target?.ContentHash)
            throw new InvalidOperationException("RecipeChangeExactMappingMismatch");
    }

    internal static void ValidateRecipeChangeTransition(IReadOnlyList<RecipeChangeHistoryEvent> history,
        RecipeChangeHistoryEvent value)
    {
        var last = history.LastOrDefault();
        if (last is null)
        {
            if (value.Kind is not (RecipeChangeEventKind.RequestObserved or RecipeChangeEventKind.ProtocolFault))
                throw new InvalidOperationException("RecipeChangeInitialTransitionInvalid");
            return;
        }
        if (history.Any(prior => prior.Request.ContentHash != value.Request.ContentHash) || value.RecordedAtUtc < last.RecordedAtUtc)
            throw new InvalidOperationException("RecipeChangeRequestContextChanged");
        if (value.Kind == RecipeChangeEventKind.ProtocolFault)
        {
            if (history.Any(prior => prior.Kind == RecipeChangeEventKind.ProtocolFault))
                throw new InvalidOperationException("RecipeChangeProtocolFaultAlreadyRecorded");
            return;
        }
        if (value.Kind == RecipeChangeEventKind.DecisionCommitted)
        {
            if (history[0].Kind != RecipeChangeEventKind.RequestObserved || history.Any(prior =>
                prior.Kind is RecipeChangeEventKind.DecisionCommitted or RecipeChangeEventKind.ResetObserved))
                throw new InvalidOperationException("RecipeChangeDecisionAlreadyRecorded");
            return;
        }
        var expectedPrevious = value.Kind switch
        {
            RecipeChangeEventKind.ResponsePublished => RecipeChangeEventKind.DecisionCommitted,
            RecipeChangeEventKind.AcknowledgementObserved => RecipeChangeEventKind.ResponsePublished,
            RecipeChangeEventKind.ResponseCleared => RecipeChangeEventKind.AcknowledgementObserved,
            RecipeChangeEventKind.ResetObserved => RecipeChangeEventKind.ResponseCleared,
            _ => throw new InvalidOperationException("RecipeChangeTransitionInvalid")
        };
        var decision = history.SingleOrDefault(prior => prior.Kind == RecipeChangeEventKind.DecisionCommitted);
        if (last.Kind != expectedPrevious || history.Any(prior => prior.Kind == RecipeChangeEventKind.ProtocolFault) ||
            decision is null || value.Outcome != decision.Outcome || value.Reason != decision.Reason || value.Activation != decision.Activation)
            throw new InvalidOperationException("RecipeChangeOutcomeChanged");
    }

    internal static void ValidateRecipeSelectionAuditRow(sqlite3 database, RecipeSelectionStoredRevision row, StoreDeadline deadline) =>
        ValidateRecipeSelectionMetadataReference(database, "RecipeSelectionRevision", row.AuditSequence,
            row.AuditHash, EncodeRecipeSelectionAudit(row), deadline);

    internal static void ValidateRecipeChangeAuditRow(sqlite3 database, RecipeChangeStoredEvent row, StoreDeadline deadline) =>
        ValidateRecipeSelectionMetadataReference(database, "RecipeChangeEvent", row.AuditSequence,
            row.AuditHash, EncodeRecipeChangeAudit(row), deadline);

    private static void ValidateRecipeSelectionMetadataReference(sqlite3 database, string kind, long sequence,
        string hash, byte[] payload, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT Kind,Payload,Hash FROM audit_entries WHERE Sequence=? AND
            FactPosition IS NULL AND IdentityPosition IS NULL AND AlarmPosition IS NULL AND ResultPosition IS NULL AND
            DraftPosition IS NULL AND CameraPosition IS NULL AND NetworkPosition IS NULL AND ImagingPosition IS NULL AND
            CalibrationSessionPosition IS NULL AND CalibrationEventPosition IS NULL AND CalibrationManifestPosition IS NULL AND
            GovernancePosition IS NULL AND ReleasePosition IS NULL AND PlcResultContractPosition IS NULL AND ActivationPosition IS NULL AND
            PreviewPosition IS NULL AND CalibrationImportPosition IS NULL AND ManualInspectionPosition IS NULL AND
            ProductionAdmissionPosition IS NULL AND StationQualificationPosition IS NULL AND RecipeTransferPosition IS NULL AND
            TraceStoragePolicyPosition IS NULL AND QualificationCyclePosition IS NULL AND PlcCommunicationPosition IS NULL AND
            ProductionInspectionPosition IS NULL AND PartIdentityPosition IS NULL LIMIT 2;", deadline,
            row => (Kind: SqliteNative.ColumnText(row, 0), Payload: SqliteNative.ColumnText(row, 1), Hash: SqliteNative.ColumnText(row, 2)), N(sequence));
        if (rows.Count != 1 || rows[0].Kind != kind || rows[0].Hash != hash || rows[0].Payload != Convert.ToBase64String(payload))
            throw new InvalidOperationException("RecipeSelectionCentralAuditMismatch");
    }

    private static byte[] EncodeRecipeChangeAudit(RecipeChangeStoredEvent row) => AuditCanonical.Encode(
        "RecipeChangeAuditV1", N(row.Event.Position), row.Event.ContentHash, row.PreviousHash, row.PayloadHash,
        Convert.ToBase64String(row.Payload));

    internal sealed record RecipeChangeStoredEvent(RecipeChangeHistoryEvent Event, string? PreviousHash,
        string PayloadHash, byte[] Payload, long AuditSequence, string AuditHash);
}
