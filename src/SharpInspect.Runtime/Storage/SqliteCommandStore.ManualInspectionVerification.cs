using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>Replays a complete bounded ledger after verifying the central chain in the same snapshot.</summary>
    internal static void VerifyAllManualInspections(sqlite3 database,
        ManualInspectionStoreOptions options, StoreDeadline deadline)
    {
        var rows = ReadManualInspectionRows(database, options, deadline);
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries, "ManualInspectionEntryCapacityExceeded");
        AuditChainDatabase.Require(rows.Sum(row => (long)row.Payload.Length) <= options.MaximumTotalBytes,
            "ManualInspectionTotalCapacityExceeded");
        var sessions = new Dictionary<Guid, ManualInspectionSessionEvent>();
        var runs = new Dictionary<Guid, ManualInspectionRunRecord>();
        var attempts = new Dictionary<Guid, ManualInspectionSessionEvent>();
        var correlations = new Dictionary<Guid, Guid>();
        Guid? activeSession = null;
        long position = 0;
        long previousAudit = 0;
        var station = AuditChainDatabase.Text(database, "SELECT StationId FROM audit_policy WHERE Id=1;", deadline)
            ?? throw new InvalidOperationException("ManualInspectionStationMissing");
        foreach (var row in rows)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var value = row.Event;
            var header = value.Header;
            AuditChainDatabase.Require(row.Position == ++position && value.Position == position &&
                value.AuditSequence > previousAudit && value.AuditHash is not null,
                "ManualInspectionPositionGap");
            previousAudit = value.AuditSequence;
            AuditChainDatabase.Require(value.Phase == header.Phase && value.Restoration == header.Restoration &&
                value.ActorPrincipalId == header.ActorPrincipalId && value.ActorSessionId == header.ActorSessionId &&
                value.ActorAuthorizationRevision == header.ActorAuthorizationRevision &&
                value.RecordedAtUtc >= header.StartedAtUtc && value.RuntimeEpoch == header.RuntimeEpoch,
                "ManualInspectionHeaderBindingMismatch");
            var central = AuditChainDatabase.Read(database,
                "SELECT Kind,ManualInspectionPosition,Hash,Payload FROM audit_entries WHERE Sequence=?;", deadline,
                statement => (Kind: SqliteNative.ColumnText(statement, 0), Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                    Hash: SqliteNative.ColumnText(statement, 2), Payload: SqliteNative.ColumnText(statement, 3)),
                value.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(central.Kind == ManualInspectionEventKind && central.Position == position &&
                central.Hash == value.AuditHash && central.Payload == Convert.ToBase64String(EncodeManualAuditPayload(value, row.Run)),
                "ManualInspectionAuditBindingMismatch");
            var reference = ReadManualCommandAuditReference(database, value, deadline);
            AuditChainDatabase.Require(reference.Sequence == value.CommandAuditSequence && reference.Hash == value.CommandAuditHash &&
                reference.Sequence < value.AuditSequence, "ManualInspectionCommandAuditMismatch");
            var accepted = ReadFact(database, value.AttemptId, 1, deadline);
            AuditChainDatabase.Require(accepted is not null && accepted.Phase == CommandAuditPhase.Outcome &&
                accepted.Disposition == CommandDisposition.Accepted && accepted.CommandKind == value.CommandKind &&
                accepted.CorrelationId == value.CommandCorrelationId && accepted.RuntimeEpoch == header.RuntimeEpoch &&
                accepted.Source == CommandSource.PhysicalConsole && accepted.ClaimedSessionId == header.ActorSessionId &&
                accepted.ClaimedPrincipalId == header.ActorPrincipalId.ToString("D") &&
                accepted.AuthenticatedHumanPrincipalId == header.ActorPrincipalId.ToString("D"),
                "ManualInspectionAcceptedCommandMissing");
            if (correlations.TryGetValue(value.CommandCorrelationId, out var previousAttempt))
                AuditChainDatabase.Require(previousAttempt == value.AttemptId, "ManualInspectionCorrelationReused");
            else correlations.Add(value.CommandCorrelationId, value.AttemptId);
            if (attempts.TryGetValue(value.AttemptId, out var previousEvent))
                AuditChainDatabase.Require(!previousEvent.Terminal && previousEvent.SessionId == value.SessionId &&
                    previousEvent.CommandKind == value.CommandKind && previousEvent.AuthorizationTarget == value.AuthorizationTarget,
                    "ManualInspectionCommandReplayInvalid");
            attempts[value.AttemptId] = value;
            var terminal = ReadFact(database, value.AttemptId, 2, deadline);
            if (value.Terminal)
                RequireManualTerminal(accepted!, terminal, value.ReasonCode);
            var identity = AuditChainDatabase.Read(database,
                "SELECT Hash,Payload,IdentityPosition FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent';", deadline,
                statement => (Hash: SqliteNative.ColumnText(statement, 0), Payload: SqliteNative.ColumnText(statement, 1),
                    Position: SqliteNative.ColumnInt64(statement, 2)),
                value.AuthorizationAuditSequence?.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
            AuditChainDatabase.Require(identity.Hash == value.AuthorizationAuditHash && identity.Payload is not null &&
                value.AuthorizationAuditSequence > 0 && value.AuthorizationAuditSequence < value.AuditSequence,
                "ManualInspectionAuthorizationAuditMissing");
            var identityPayload = Convert.FromBase64String(identity.Payload!);
            var expectedIdentityKind = value.Terminal
                ? terminal!.Phase == CommandAuditPhase.Completed ? IdentityEventKind.ManualInspectionSessionCompleted :
                    IdentityEventKind.ManualInspectionSessionFailed
                : value.CommandKind == AuditedCommandKind.StartManualInspectionSession &&
                  value.Phase == ManualInspectionSessionPhase.Admitted ? IdentityEventKind.ManualInspectionSessionStartAuthorized :
                    IdentityEventKind.ManualInspectionSessionActionAuthorized;
            AuditChainDatabase.Require(IdentityAuditEvent.MatchesManualInspectionAuthorization(identityPayload,
                identity.Position, station, value, accepted) && IdentityAuditEvent.TryReadEventKind(identityPayload, out var kind) &&
                kind == expectedIdentityKind, "ManualInspectionAuthorizationAuditMismatch");

            if (!sessions.TryGetValue(value.SessionId, out var previousSession))
            {
                AuditChainDatabase.Require(activeSession is null && value.CommandKind == AuditedCommandKind.StartManualInspectionSession &&
                    !value.Terminal && value.Phase == ManualInspectionSessionPhase.Admitted &&
                    value.AttemptId == header.AttemptId && value.CommandCorrelationId == header.StartCorrelationId &&
                    value.AuthorizationTarget == header.AuthorizationTarget,
                    "ManualInspectionStartMissing");
                VerifyManualHistoricalSource(database, header, deadline);
                activeSession = value.SessionId;
            }
            else
            {
                AuditChainDatabase.Require(activeSession == value.SessionId && previousSession.Header.IsActive &&
                    SameManualSessionBinding(previousSession.Header, header) &&
                    value.RecordedAtUtc >= previousSession.RecordedAtUtc,
                    "ManualInspectionSessionReplayInvalid");
            }
            if (value.CommandKind == AuditedCommandKind.RunManualInspection)
            {
                var run = row.Run ?? throw new InvalidOperationException("ManualInspectionRunMissing");
                AuditChainDatabase.Require(run.RunId == value.ManualRunId && run.SessionId == value.SessionId &&
                    run.AttemptId == value.AttemptId && run.CommandCorrelationId == value.CommandCorrelationId &&
                    run.RuntimeEpoch == value.RuntimeEpoch && run.Terminal == value.Terminal &&
                    run.CommandAuditSequence == value.CommandAuditSequence && run.CommandAuditHash == value.CommandAuditHash &&
                    run.AuditSequence == value.AuditSequence && run.AuditHash == value.AuditHash &&
                    CloneManualRun(run, run.CommandAuditSequence, run.CommandAuditHash, 0, null).ContentHash == value.OutcomeContentHash,
                    "ManualInspectionRunBindingMismatch");
                if (runs.TryGetValue(run.RunId, out var priorRun))
                    AuditChainDatabase.Require(!priorRun.Terminal && priorRun.SessionId == run.SessionId &&
                        priorRun.AttemptId == run.AttemptId && priorRun.CommandCorrelationId == run.CommandCorrelationId &&
                        priorRun.AdmittedAtUtc == run.AdmittedAtUtc && priorRun.PartIdentity == run.PartIdentity &&
                        priorRun.PartIdentitySource == run.PartIdentitySource &&
                        priorRun.PartIdentityActorPrincipalId == run.PartIdentityActorPrincipalId &&
                        priorRun.PartIdentityActorSessionId == run.PartIdentityActorSessionId && run.Status >= priorRun.Status,
                        "ManualInspectionRunReplayInvalid");
                else
                    AuditChainDatabase.Require(run.Status == ManualInspectionRunStatus.Admitted && !run.Terminal &&
                        run.AdmittedAtUtc == value.RecordedAtUtc && previousSession?.Phase == ManualInspectionSessionPhase.ReadyForRun &&
                        runs.Values.All(item => item.Terminal), "ManualInspectionRunAdmissionInvalid");
                if (run.PartIdentity is not null)
                    AuditChainDatabase.Require(run.PartIdentityActorPrincipalId == header.ActorPrincipalId &&
                        run.PartIdentityActorSessionId == header.ActorSessionId, "ManualInspectionPartActorMismatch");
                if (run.FrameMetadata is not null)
                    AuditChainDatabase.Require(run.FrameMetadata.Correlation == new ExecutionCorrelationId(ExecutionKind.Manual, run.RunId) &&
                        run.FrameMetadata.LogicalCameraRole == header.CurrentBinding.LogicalRole,
                        "ManualInspectionFrameBindingMismatch");
                runs[run.RunId] = run;
            }
            else AuditChainDatabase.Require(row.Run is null && value.ManualRunId is null && value.OutcomeContentHash is null,
                "ManualInspectionUnexpectedRunEvidence");
            if (!header.IsActive)
            {
                AuditChainDatabase.Require(value.Terminal && value.CommandKind != AuditedCommandKind.RunManualInspection &&
                    header.Phase is ManualInspectionSessionPhase.Closed or ManualInspectionSessionPhase.RecoveryBlocked &&
                    header.Restoration is not ManualInspectionRestorationState.Pending &&
                    header.RecoveryRequired == (header.Phase == ManualInspectionSessionPhase.RecoveryBlocked) &&
                    runs.Values.Where(item => item.SessionId == value.SessionId).All(item => item.Terminal),
                    "ManualInspectionSessionTerminalInvalid");
                var startFact = ReadFact(database, header.AttemptId, 1, deadline);
                AuditChainDatabase.Require(startFact is not null, "ManualInspectionStartAuditMissing");
                RequireManualTerminal(startFact!, ReadFact(database, header.AttemptId, 2, deadline), value.ReasonCode);
                activeSession = null;
            }
            sessions[value.SessionId] = value;
        }
        foreach (var pair in sessions.Where(pair => pair.Value.Header.IsActive))
            AuditChainDatabase.Require(ReadFact(database, pair.Value.Header.AttemptId, 2, deadline) is null,
                "ManualInspectionUnexpectedStartTerminal");
        foreach (var value in attempts.Values.Where(value => value.CommandKind != AuditedCommandKind.StartManualInspectionSession))
            AuditChainDatabase.Require(value.Terminal == (ReadFact(database, value.AttemptId, 2, deadline) is not null),
                "ManualInspectionCommandTerminalMismatch");
    }

    private static void RequireManualTerminal(CommandAuditFact accepted, CommandAuditFact? terminal, string reason)
    {
        AuditChainDatabase.Require(terminal is not null && terminal.Disposition is null &&
            terminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
            SameCommandContext(accepted, terminal) && terminal.ReasonCode == reason,
            "ManualInspectionCommandTerminalMissing");
        ValidateFact(terminal!);
    }

    private static bool SameManualSessionBinding(ManualInspectionSessionHeader left, ManualInspectionSessionHeader right) =>
        left.Position == right.Position && left.SessionId == right.SessionId && left.RuntimeEpoch == right.RuntimeEpoch &&
        left.StartCorrelationId == right.StartCorrelationId && left.AttemptId == right.AttemptId &&
        left.Selection == right.Selection && left.SourceContentHash == right.SourceContentHash &&
        left.CurrentBinding == right.CurrentBinding && left.ActiveActivation == right.ActiveActivation &&
        left.ActiveSnapshotContentHash == right.ActiveSnapshotContentHash && left.ActiveCameraContentHash == right.ActiveCameraContentHash &&
        left.ActorPrincipalId == right.ActorPrincipalId && left.ActorSessionId == right.ActorSessionId &&
        left.ActorAuthorizationRevision == right.ActorAuthorizationRevision && left.AuthorizationPolicy == right.AuthorizationPolicy &&
        left.AuthorizationTarget == right.AuthorizationTarget && left.ChangeReason == right.ChangeReason &&
        left.StartedAtUtc == right.StartedAtUtc;

    private static void VerifyManualHistoricalSource(sqlite3 database, ManualInspectionSessionHeader header, StoreDeadline deadline)
    {
        var selection = header.Selection;
        var draftId = selection.DraftId;
        var revision = selection.DraftRevision;
        var revisionHash = selection.DraftRevisionContentHash;
        if (selection.Kind == ManualRecipeSourceKind.Released)
        {
            var release = AuditChainDatabase.Read(database,
                "SELECT SourceDraftId,SourceRevision,SourceRevisionContentHash,SourceContentHash,RecipeKey,RecipeVersion " +
                "FROM recipe_release_events WHERE ReleaseId=? AND RecordContentHash=?;", deadline,
                statement => Enumerable.Range(0, 6).Select(index => SqliteNative.ColumnText(statement, index)).ToArray(),
                selection.ReleaseId?.ToString("D"), selection.ReleaseRecordContentHash).SingleOrDefault();
            AuditChainDatabase.Require(release is not null && release[3] == header.SourceContentHash &&
                release[3] == selection.Recipe!.ContentHash && release[4] == selection.Recipe.Id && release[5] == selection.Recipe.Version,
                "ManualInspectionReleasedSourceMismatch");
            draftId = ParseManualGuid(release![0]);
            revision = long.Parse(release[1]!, CultureInfo.InvariantCulture);
            revisionHash = release[2];
        }
        var draft = ReadRecipeDraftRow(database, "WHERE DraftId=? AND Revision=? LIMIT 2", deadline,
            draftId?.ToString("D"), revision?.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(draft is not null && draft.RevisionContentHash == revisionHash,
            "ManualInspectionDraftSourceMismatch");
        AuditChainDatabase.Require(RecipeDraftStorageCodec.TryDecodeContent(draft!.PayloadJson, draft.PayloadHash,
            out var content, out _) && content is not null && content.ContentHash == header.SourceContentHash &&
            content.CameraRole == header.CurrentBinding.LogicalRole, "ManualInspectionSourceContentMismatch");
        var binding = ReadCameraSetupRows(database, "WHERE Position=? LIMIT 2", deadline,
            header.CurrentBinding.Position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(binding is not null && binding.RevisionHash == header.CurrentBinding.RevisionHash &&
            binding.BindingRevision == header.CurrentBinding.Revision && binding.LogicalRole == header.CurrentBinding.LogicalRole,
            "ManualInspectionCameraBindingMismatch");
        var historicalBinding = CameraSetupStorageCodec.Decode(DecodePayload(binding!.Payload, binding.Position), binding.Position);
        var capturedBinding = header.CurrentBinding;
        AuditChainDatabase.Require(historicalBinding.Target == capturedBinding.Target &&
            historicalBinding.OperationId == capturedBinding.OperationId &&
            historicalBinding.PreviousRevisionHash == capturedBinding.PreviousRevisionHash &&
            historicalBinding.ActorPrincipalId == capturedBinding.AuthorPrincipalId &&
            historicalBinding.SessionId == capturedBinding.AuthorSessionId &&
            historicalBinding.AuthorAuthorizationRevision == capturedBinding.AuthorAuthorizationRevision &&
            historicalBinding.ChangeReason == capturedBinding.ChangeReason &&
            historicalBinding.RecordedAtUtc == capturedBinding.RecordedAtUtc,
            "ManualInspectionCameraBindingMismatch");
        if (header.ActiveActivation is { } active)
        {
            var payload = AuditChainDatabase.Text(database,
                "SELECT Payload FROM recipe_activation_events WHERE Position=? AND ActivationId=? AND RecordContentHash=?;",
                deadline, active.Position.ToString(CultureInfo.InvariantCulture), active.ActivationId.ToString("D"), active.ContentHash);
            AuditChainDatabase.Require(payload is not null, "ManualInspectionActiveBaselineMissing");
            PublishedCalibrationProfileVersion? ResolveProfile(CalibrationProfileReference reference)
            {
                if (!AuditChainDatabase.TableExists(database, "calibration_governance_events", deadline)) return null;
                var profilePayload = AuditChainDatabase.Text(database,
                    "SELECT Payload FROM calibration_governance_events WHERE Kind='CalibrationProfilePublished' AND RecordContentHash=?;",
                    deadline, reference.ContentHash);
                if (profilePayload is null) return null;
                var profile = CalibrationGovernanceCodec.Decode("CalibrationProfilePublished",
                    Convert.FromBase64String(profilePayload)) as PublishedCalibrationProfileVersion;
                return profile?.Reference == reference ? profile : null;
            }
            var record = RecipeActivationStorageCodec.Decode(Convert.FromBase64String(payload!), ResolveProfile);
            AuditChainDatabase.Require(record.Reference == active && record.Outcome.Succeeded &&
                record.SuccessfulSnapshot is { } snapshot && snapshot.ContentHash == header.ActiveSnapshotContentHash &&
                RecipeActivationValidation.CameraHash(snapshot.CameraSetup) == header.ActiveCameraContentHash,
                "ManualInspectionActiveBaselineMismatch");
        }
    }
}
