using System.Globalization;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// Replays the qualification ledger from its immutable rows and the
    /// central command/identity/audit ledgers.  The qualification payload is
    /// never accepted as a self-authenticating substitute for those records.
    /// </summary>
    internal static void VerifyAllStationQualifications(sqlite3 database,
        StationQualificationStoreOptions options, StoreDeadline deadline)
    {
        var rows = ReadStationQualificationRows(database, options, deadline);
        AuditChainDatabase.Require(rows.Count <= options.MaximumEntries,
            "StationQualificationEntryCapacityExceeded");
        AuditChainDatabase.Require(rows.Sum(value => (long)value.Payload.Length) <=
            options.MaximumTotalBytes, "StationQualificationTotalCapacityExceeded");
        if (rows.Count == 0) return;

        var station = AuditChainDatabase.Text(database,
            "SELECT StationId FROM audit_policy WHERE Id=1 LIMIT 1;", deadline);
        AuditChainDatabase.Require(station is { Length: > 0 },
            "StationQualificationStationMissing");

        var lastBySession = new Dictionary<Guid, StationQualificationSessionEvent>();
        var runs = new Dictionary<Guid, StationQualificationRunRecord>();
        var attempts = new Dictionary<Guid, (Guid CorrelationId, Guid SessionId, string Target)>();
        var correlations = new Dictionary<Guid, Guid>();
        Guid? activeSession = null;
        StationQualificationSessionEvent? previousGlobal = null;
        var expectedPosition = 0L;
        var priorEvents = new List<StationQualificationSessionEvent>();

        foreach (var row in rows)
        {
            SqliteNative.EnsureDeadline(deadline, CancellationToken.None);
            var value = row.Event;
            var semanticFailure = ValidateStationQualificationSemantics(priorEvents, value);
            if (semanticFailure is not null) throw new InvalidOperationException(semanticFailure);
            AuditChainDatabase.Require(row.Position == ++expectedPosition &&
                value.Position == expectedPosition && value.AuditSequence > 0 &&
                value.AuditHash is { Length: 64 }, "StationQualificationPositionGap");
            AuditChainDatabase.Require(previousGlobal is null ||
                value.PreviousHash == previousGlobal.ContentHash,
                "StationQualificationReplayChainMismatch");
            RequireStationQualificationCentralBinding(database, value, station!, deadline);
            var accepted = ReadStationQualificationAcceptedCommand(database, value, deadline);
            RequireStationQualificationCommandContext(value, accepted, attempts, correlations);
            RequireStationQualificationAuthorization(database, station!, value, accepted, deadline);
            VerifyStationQualificationHistoricalTarget(database, value.Header, deadline);

            if (!lastBySession.TryGetValue(value.SessionId, out var previousSession))
            {
                AuditChainDatabase.Require(activeSession is null &&
                    value.CommandKind == AuditedCommandKind.StartStationQualificationSession &&
                    !value.Terminal && value.Phase == StationQualificationSessionPhase.Admitted &&
                    value.CommandCorrelationId == value.Header.StartCorrelationId &&
                    value.AttemptId == value.Header.StartAttemptId &&
                    value.CommandAuthorizationTarget == value.Header.AuthorizationTarget,
                    "StationQualificationStartMissing");
                activeSession = value.SessionId;
            }
            else
            {
                AuditChainDatabase.Require(activeSession == value.SessionId &&
                    SameStationQualificationHeader(previousSession.Header, value.Header) &&
                    value.RecordedAtUtc >= previousSession.RecordedAtUtc,
                    "StationQualificationSessionReplayInvalid");
                RequireStationQualificationPhaseTransition(previousSession, value);
            }

            VerifyStationQualificationRun(previousSession, value, runs);
            RequireStationQualificationRecoveryBinding(previousSession, value);
            if (value.Terminal)
            {
                AuditChainDatabase.Require(value.Phase == StationQualificationSessionPhase.Closed &&
                    value.Restoration == StationQualificationRestorationState.Restored &&
                    runs.Values.Where(item => item.SessionId == value.SessionId).All(item => item.Completed),
                    "StationQualificationTerminalInvalid");
                var startAccepted = ReadFact(database, value.Header.StartAttemptId, 1, deadline);
                RequireStationQualificationTerminalFact(startAccepted,
                    ReadFact(database, value.Header.StartAttemptId, 2, deadline), value.ReasonCode,
                    "StationQualificationStartTerminalMissing");
                if (value.CommandKind == AuditedCommandKind.ExitStationQualificationSession)
                {
                    var exitAccepted = ReadFact(database, value.AttemptId, 1, deadline);
                    RequireStationQualificationTerminalFact(exitAccepted,
                        ReadFact(database, value.AttemptId, 2, deadline), value.ReasonCode,
                        "StationQualificationExitTerminalMissing");
                }
                activeSession = null;
            }
            else
            {
                AuditChainDatabase.Require(value.Phase != StationQualificationSessionPhase.Closed,
                    "StationQualificationClosedWithoutTerminal");
                activeSession = value.SessionId;
            }
            lastBySession[value.SessionId] = value;
            previousGlobal = value;
            priorEvents.Add(value);
        }

        AuditChainDatabase.Require(lastBySession.Values.Count(value => !value.Terminal) <= 1,
            "StationQualificationMultipleActiveSessions");
        foreach (var value in lastBySession.Values.Where(value => value.Terminal))
            AuditChainDatabase.Require(value.Phase == StationQualificationSessionPhase.Closed &&
                value.Restoration == StationQualificationRestorationState.Restored,
                "StationQualificationTerminalInvalid");
        foreach (var run in runs.Values)
            AuditChainDatabase.Require(run.Completed ||
                (lastBySession.TryGetValue(run.SessionId, out var last) && !last.Terminal),
                "StationQualificationPendingRunOrphaned");
    }

    private static void RequireStationQualificationCentralBinding(sqlite3 database,
        StationQualificationSessionEvent value, string station, StoreDeadline deadline)
    {
        var central = AuditChainDatabase.Read(database, @"
            SELECT Kind,StationQualificationPosition,Hash,Payload
            FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3)),
            value.AuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        var expectedPayload = EncodeStationQualificationAuditPayload(value);
        AuditChainDatabase.Require(central.Kind == StationQualificationEventKind &&
            central.Position == value.Position && central.Hash == value.AuditHash &&
            central.Payload == Convert.ToBase64String(expectedPayload),
            "StationQualificationAuditBindingMismatch");
        AuditChainDatabase.Require(value.AuditSequence > value.AuthorizationAuditSequence &&
            value.AuditSequence > value.CommandAuditSequence,
            "StationQualificationAuditReferenceOrderInvalid");
    }

    private static CommandAuditFact ReadStationQualificationAcceptedCommand(sqlite3 database,
        StationQualificationSessionEvent value, StoreDeadline deadline)
    {
        var sequence = value.CommandAuditSequence ??
            throw new InvalidOperationException("StationQualificationCommandAuditMissing");
        var central = AuditChainDatabase.Read(database, @"
            SELECT Kind,FactPosition,Hash FROM audit_entries WHERE Sequence=? LIMIT 2;",
            deadline, statement => (Kind: SqliteNative.ColumnText(statement, 0),
                FactPosition: SqliteNative.ColumnInt64Nullable(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2)),
            sequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        var accepted = ReadFact(database, value.AttemptId, 1, deadline);
        AuditChainDatabase.Require(accepted is not null &&
            central.Kind == "CommandFact" && central.Hash == value.CommandAuditHash &&
            central.FactPosition is > 0 && accepted.Phase == CommandAuditPhase.Outcome &&
            accepted.Disposition == CommandDisposition.Accepted &&
            accepted.AttemptId == value.AttemptId && accepted.CorrelationId == value.CommandCorrelationId &&
            accepted.RuntimeEpoch == value.RuntimeEpoch && accepted.CommandKind == value.CommandKind,
            "StationQualificationCommandAuditMismatch");
        var factPosition = AuditChainDatabase.Scalar(database,
            "SELECT Position FROM command_facts WHERE EventId=? LIMIT 2;", deadline,
            accepted!.EventId.ToString("D"));
        AuditChainDatabase.Require(factPosition == central.FactPosition,
            "StationQualificationCommandFactBindingMismatch");
        return accepted;
    }

    private static void RequireStationQualificationCommandContext(
        StationQualificationSessionEvent value, CommandAuditFact accepted,
        IDictionary<Guid, (Guid CorrelationId, Guid SessionId, string Target)> attempts,
        IDictionary<Guid, Guid> correlations)
    {
        var header = value.Header;
        AuditChainDatabase.Require(accepted.Source == CommandSource.PhysicalConsole &&
            accepted.ClaimedPrincipalId == header.ActorPrincipalId.ToString("D") &&
            accepted.ClaimedSessionId == header.ActorSessionId &&
            accepted.AuthenticatedHumanPrincipalId == header.ActorPrincipalId.ToString("D"),
            "StationQualificationCommandActorMismatch");
        if (attempts.TryGetValue(value.AttemptId, out var attempt))
            AuditChainDatabase.Require(attempt.CorrelationId == value.CommandCorrelationId &&
                attempt.SessionId == value.SessionId && attempt.Target == value.CommandAuthorizationTarget,
                "StationQualificationCommandReplayInvalid");
        else
            attempts.Add(value.AttemptId, (value.CommandCorrelationId, value.SessionId,
                value.CommandAuthorizationTarget));
        if (correlations.TryGetValue(value.CommandCorrelationId, out var previousAttempt))
            AuditChainDatabase.Require(previousAttempt == value.AttemptId,
                "StationQualificationCorrelationReused");
        else correlations.Add(value.CommandCorrelationId, value.AttemptId);
    }

    private static void RequireStationQualificationAuthorization(sqlite3 database,
        string station, StationQualificationSessionEvent value, CommandAuditFact accepted,
        StoreDeadline deadline)
    {
        var sequence = value.AuthorizationAuditSequence ??
            throw new InvalidOperationException("StationQualificationAuthorizationAuditMissing");
        var identity = AuditChainDatabase.Read(database, @"
            SELECT Kind,IdentityPosition,Hash,Payload FROM audit_entries
            WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0),
                Position: SqliteNative.ColumnInt64Nullable(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2),
                Payload: SqliteNative.ColumnText(statement, 3)),
            sequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(identity.Kind == "IdentityEvent" && identity.Position is > 0 &&
            identity.Hash == value.AuthorizationAuditHash && identity.Payload is { Length: > 0 } &&
            sequence < value.AuditSequence, "StationQualificationAuthorizationAuditMissing");
        var payload = Convert.FromBase64String(identity.Payload!);
        AuditChainDatabase.Require(IdentityAuditEvent.MatchesStationQualificationAuthorization(
            payload, identity.Position!.Value, station, value, accepted),
            "StationQualificationAuthorizationAuditMismatch");
    }

    private static void VerifyStationQualificationHistoricalTarget(sqlite3 database,
        StationQualificationSessionHeader header, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(header.TargetBaseline.Outcome.State ==
            RecipeActivationOutcomeState.Succeeded &&
            header.TargetBaseline.Reference == header.Plan.TargetActivation &&
            header.TargetBaseline.SuccessfulSnapshot is not null,
            "StationQualificationTargetBaselineInvalid");
        AuditChainDatabase.Require(AuditChainDatabase.TableExists(database,
            "recipe_activation_events", deadline), "StationQualificationActivationHistoryMissing");
        var row = AuditChainDatabase.Read(database, @"
            SELECT ActivationId,OutcomeState,RecordContentHash,PayloadHash,Payload
            FROM recipe_activation_events
            WHERE Position=? AND ActivationId=? AND RecordContentHash=? LIMIT 2;", deadline,
            statement => (ActivationId: SqliteNative.ColumnText(statement, 0),
                OutcomeState: SqliteNative.ColumnInt64(statement, 1),
                RecordContentHash: SqliteNative.ColumnText(statement, 2),
                PayloadHash: SqliteNative.ColumnText(statement, 3),
                Payload: SqliteNative.ColumnText(statement, 4)),
            header.Plan.TargetActivation.Position.ToString(CultureInfo.InvariantCulture),
            header.Plan.TargetActivation.ActivationId.ToString("D"),
            header.Plan.TargetActivation.ContentHash).SingleOrDefault();
        AuditChainDatabase.Require(row.ActivationId == header.Plan.TargetActivation.ActivationId.ToString("D") &&
            row.OutcomeState == (long)RecipeActivationOutcomeState.Succeeded &&
            row.RecordContentHash == header.TargetBaseline.ContentHash &&
            row.PayloadHash is { Length: 64 } && row.Payload is { Length: > 0 },
            "StationQualificationHistoricalActivationMissing");
        var payload = Convert.FromBase64String(row.Payload!);
        AuditChainDatabase.Require(Convert.ToHexString(SHA256.HashData(payload)) == row.PayloadHash &&
            payload.AsSpan().SequenceEqual(RecipeActivationStorageCodec.Encode(header.TargetBaseline)),
            "StationQualificationHistoricalActivationMismatch");
    }

    private static void RequireStationQualificationPhaseTransition(
        StationQualificationSessionEvent previous, StationQualificationSessionEvent current)
    {
        AuditChainDatabase.Require(!previous.Terminal, "StationQualificationSessionAlreadyTerminal");
        var allowed = previous.Phase switch
        {
            StationQualificationSessionPhase.Admitted => current.Phase is
                StationQualificationSessionPhase.Isolating or StationQualificationSessionPhase.ReadyForStimulus or
                StationQualificationSessionPhase.Running or StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked,
            StationQualificationSessionPhase.Isolating => current.Phase is
                StationQualificationSessionPhase.Isolating or StationQualificationSessionPhase.ReadyForStimulus or
                StationQualificationSessionPhase.Running or StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked,
            StationQualificationSessionPhase.ReadyForStimulus => current.Phase is
                StationQualificationSessionPhase.ReadyForStimulus or StationQualificationSessionPhase.Running or
                StationQualificationSessionPhase.Restoring or StationQualificationSessionPhase.RecoveryBlocked,
            StationQualificationSessionPhase.Running => current.Phase is
                StationQualificationSessionPhase.Running or StationQualificationSessionPhase.ReadyForStimulus or
                StationQualificationSessionPhase.Restoring or StationQualificationSessionPhase.RecoveryBlocked,
            StationQualificationSessionPhase.Restoring or StationQualificationSessionPhase.RecoveryBlocked =>
                current.Phase is StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked or StationQualificationSessionPhase.Closed,
            _ => false
        };
        AuditChainDatabase.Require(allowed, "StationQualificationPhaseTransitionInvalid");
        AuditChainDatabase.Require(current.CommandKind == AuditedCommandKind.StartStationQualificationSession ||
            current.Phase is not StationQualificationSessionPhase.Admitted,
            "StationQualificationExitAdmissionInvalid");
    }

    private static void VerifyStationQualificationRun(
        StationQualificationSessionEvent? previous, StationQualificationSessionEvent value,
        IDictionary<Guid, StationQualificationRunRecord> runs)
    {
        if (value.Run is not { } run) return;
        AuditChainDatabase.Require(run.SessionId == value.SessionId &&
            run.ContextHash == value.Header.Plan.QualificationContextHash,
            "StationQualificationRunBindingMismatch");
        if (!runs.TryGetValue(run.RunId.Value, out var prior))
        {
            AuditChainDatabase.Require(!run.Completed && value.Phase == StationQualificationSessionPhase.Running &&
                (previous?.Phase is StationQualificationSessionPhase.ReadyForStimulus or
                    StationQualificationSessionPhase.Running) &&
                runs.Values.Where(item => item.SessionId == run.SessionId).All(item => item.Completed),
                "StationQualificationRunAdmissionInvalid");
        }
        else
        {
            AuditChainDatabase.Require(!prior.Completed && run.Position == prior.Position &&
                run.RunId.Value == prior.RunId.Value && run.SessionId == prior.SessionId &&
                run.StimulusSequence == prior.StimulusSequence && run.ScenarioId == prior.ScenarioId &&
                run.ContextHash == prior.ContextHash && run.ControllerEpoch == prior.ControllerEpoch &&
                run.CycleSequence == prior.CycleSequence && run.AdmittedAtUtc == prior.AdmittedAtUtc &&
                run.Completed, "StationQualificationRunReplayInvalid");
            if (value.Phase is StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked)
                AuditChainDatabase.Require(run.ExecutionStatus == ExecutionStatus.Cancelled &&
                    run.Decision == InspectionDecision.Unknown && run.ResultPayloadJson is null &&
                    run.ResultPayloadHash is null && run.QualificationPayload is null,
                    "StationQualificationRecoveryRunInvalid");
        }
        runs[run.RunId.Value] = run;
    }

    private static void RequireStationQualificationRecoveryBinding(
        StationQualificationSessionEvent? previous, StationQualificationSessionEvent current)
    {
        RequireStationQualificationRecoveryContinuation(previous, current.Header, current.Phase,
            current.RecoveryAttempt, current.Observation, current.Run);
        if (current.RecoveryAttempt is { } attempt)
        {
            AuditChainDatabase.Require(current.Phase is StationQualificationSessionPhase.Restoring or
                StationQualificationSessionPhase.RecoveryBlocked or StationQualificationSessionPhase.Closed,
                "StationQualificationRecoveryAttemptPhaseInvalid");
            if (previous?.RecoveryAttempt is { } prior)
                AuditChainDatabase.Require(prior.ContentHash == attempt.ContentHash ||
                    (current.Phase == StationQualificationSessionPhase.Restoring &&
                     prior.RuntimeEpoch != attempt.RuntimeEpoch && prior.LeaseNonce != attempt.LeaseNonce),
                    "StationQualificationRecoveryAttemptMismatch");
        }
        if (current.Phase == StationQualificationSessionPhase.RecoveryBlocked)
            AuditChainDatabase.Require(!current.Terminal,
                "StationQualificationRecoveryBlockedTerminalInvalid");
    }

    private static void RequireStationQualificationTerminalFact(CommandAuditFact? accepted,
        CommandAuditFact? terminal, string reason, string failureReason)
    {
        AuditChainDatabase.Require(accepted is not null &&
            terminal is not null && terminal.Disposition is null &&
            terminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
            SameCommandContext(accepted, terminal) && terminal.ReasonCode == reason,
            failureReason);
        ValidateFact(terminal!);
    }

    private static bool SameStationQualificationHeader(
        StationQualificationSessionHeader left, StationQualificationSessionHeader right) =>
        left.ContentHash == right.ContentHash && left.SessionId == right.SessionId &&
        left.RuntimeEpoch == right.RuntimeEpoch && left.LeaseNonce == right.LeaseNonce &&
        left.StartCorrelationId == right.StartCorrelationId && left.StartAttemptId == right.StartAttemptId &&
        left.Plan.ContentHash == right.Plan.ContentHash &&
        left.TargetBaseline.ContentHash == right.TargetBaseline.ContentHash &&
        left.ActorPrincipalId == right.ActorPrincipalId && left.ActorSessionId == right.ActorSessionId &&
        left.ActorAuthorizationRevision == right.ActorAuthorizationRevision &&
        left.AuthorizationPolicy.ContentHash == right.AuthorizationPolicy.ContentHash &&
        left.StepUpGrantId == right.StepUpGrantId && left.AuthorizationTarget == right.AuthorizationTarget;
}
