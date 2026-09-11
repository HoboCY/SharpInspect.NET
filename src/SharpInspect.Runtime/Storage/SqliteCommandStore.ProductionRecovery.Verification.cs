using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// Replays recovery rows against the immutable production ledger and the
    /// exact command/identity audit references stored in each record.  The
    /// ordinary production projection verifier proves the event chain; this
    /// additional pass proves that a recovery row cannot borrow another
    /// inspection's authorization or a later lifecycle predecessor.
    /// </summary>
    internal static void ValidateProductionRecoveryHistory(sqlite3 database,
        IReadOnlyList<ProductionInspectionStoredRow> rows,
        ProductionInspectionStoreOptions options, StoreDeadline deadline)
    {
        var ordered = rows.OrderBy(value => value.Position).ToArray();
        if (ReadUserVersion(database, deadline) < ProductionRecoveryStoreOptions.SchemaVersion)
        {
            AuditChainDatabase.Require(ordered.All(value => value.Event.Recovery is null),
                "ProductionRecoverySchemaVersionRequired");
            return;
        }
        foreach (var row in ordered)
        {
            var value = row.Event;
            if (value.Kind is not (ProductionInspectionEventKind.RecoveryRequired or
                ProductionInspectionEventKind.RecoveryCompleted))
            {
                AuditChainDatabase.Require(value.Recovery is null,
                    "ProductionRecoveryUnexpectedMetadata");
                continue;
            }

            var recovery = value.Recovery ?? throw new InvalidOperationException(
                "ProductionRecoveryRecordMissing");
            AuditChainDatabase.Require(recovery.InspectionId == value.InspectionId &&
                recovery.PreviousEventPosition > 0 && recovery.PreviousEventPosition < value.Position &&
                recovery.RecoveryRuntimeEpoch != Guid.Empty && recovery.SafetyEvidence.Observation.IsSafeLineStopped,
                "ProductionRecoveryRecordBindingMismatch");
            var previous = ordered.SingleOrDefault(candidate =>
                candidate.Position == recovery.PreviousEventPosition);
            if (previous is null)
                throw new InvalidOperationException("ProductionRecoveryPreviousEventMismatch");
            AuditChainDatabase.Require(previous.Event.InspectionId == value.InspectionId &&
                previous.Event.ContentHash == recovery.PreviousEventHash,
                "ProductionRecoveryPreviousEventMismatch");
            var immediate = ordered.Where(candidate => candidate.Event.InspectionId == value.InspectionId &&
                candidate.Position < value.Position).LastOrDefault();
            if (immediate is null)
                throw new InvalidOperationException("ProductionRecoveryLifecyclePredecessorMissing");
            if (value.Kind == ProductionInspectionEventKind.RecoveryRequired)
            {
                AuditChainDatabase.Require(immediate.Position == recovery.PreviousEventPosition &&
                    recovery.Outcome is ProductionRecoveryOutcome.Pending or ProductionRecoveryOutcome.Blocked,
                    "ProductionRecoveryLifecyclePredecessorInvalid");
            }
            else
            {
                AuditChainDatabase.Require(immediate.Event.Kind == ProductionInspectionEventKind.RecoveryRequired &&
                    immediate.Event.Recovery?.RecoveryAttemptId == recovery.RecoveryAttemptId &&
                    recovery.Outcome == ProductionRecoveryOutcome.Completed &&
                    recovery.CompletionReceipt is { CleanupCompleted: true },
                    "ProductionRecoveryCompletionPredecessorInvalid");
                var receipt = recovery.CompletionReceipt!;
                AuditChainDatabase.Require(receipt.Health.Healthy && receipt.TriggerLowObserved &&
                    receipt.AckLowObserved && receipt.RuntimeOutputsClear &&
                    receipt.RuntimeEpoch == recovery.RecoveryRuntimeEpoch &&
                    receipt.Health.RuntimeEpoch == receipt.RuntimeEpoch &&
                    receipt.Health.ControllerEpoch == receipt.ControllerEpoch &&
                    receipt.ControllerEpoch > 0,
                    "ProductionRecoveryCompletionEvidenceInvalid");
            }

            AuditChainDatabase.Require(recovery.Observation.RuntimeEpoch == value.Admission.RuntimeEpoch &&
                recovery.Observation.EndpointBindingHash == value.Admission.EndpointBindingHash &&
                recovery.Observation.PlcProfileHash == value.Admission.PlcProfileHash &&
                recovery.Observation.PlcPolicyHash == value.Admission.PlcPolicyHash &&
                recovery.Observation.CoreContentHash == value.Core?.ContentHash &&
                recovery.Observation.PayloadContentHash == value.Core?.PlcPayload?.ContentHash &&
                recovery.Observation.PayloadWireContentHash == value.Core?.PlcPayload?.WireContentHash,
                "ProductionRecoveryObservationBindingMismatch");
            var expectedRecoveryTargetHash = value.Kind == ProductionInspectionEventKind.RecoveryCompleted &&
                recovery.CommandAttemptId != recovery.RecoveryAttemptId
                ? immediate.Event.ContentHash
                : recovery.PreviousEventHash;
            ValidateRecoveryAuditReference(database, recovery, value,
                expectedRecoveryTargetHash, options, deadline);
        }
        ValidateProductionRecoveryFailureAudits(database, ordered, options, deadline);
    }

    private static void ValidateRecoveryAuditReference(sqlite3 database,
        ProductionRecoveryRecord recovery, ProductionInspectionHistoryEvent value,
        string expectedRecoveryTargetHash, ProductionInspectionStoreOptions options,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(recovery.CommandAuditSequence > 0 &&
            recovery.CommandAuditHash is { Length: 64 } && recovery.AuthorizationAuditSequence > 0 &&
            recovery.AuthorizationAuditHash is { Length: 64 },
            "ProductionRecoveryAuthorizationAuditMissing");
        var command = AuditChainDatabase.Read(database, @"
            SELECT e.Kind,e.Hash,e.Payload,e.FactPosition,c.AttemptId,c.CorrelationId,
                c.RuntimeEpoch,c.CommandKind,c.Disposition
            FROM audit_entries e JOIN command_facts c ON c.Position=e.FactPosition
            WHERE e.Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                FactPosition: SqliteNative.ColumnInt64(statement, 3),
                AttemptId: SqliteNative.ColumnText(statement, 4) ?? string.Empty,
                CorrelationId: SqliteNative.ColumnText(statement, 5) ?? string.Empty,
                RuntimeEpoch: SqliteNative.ColumnText(statement, 6) ?? string.Empty,
                CommandKind: SqliteNative.ColumnInt64(statement, 7),
                Disposition: SqliteNative.ColumnInt64(statement, 8)),
            recovery.CommandAuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(command.Kind == "CommandFact" &&
            command.Hash == recovery.CommandAuditHash && command.FactPosition > 0 &&
            Guid.TryParse(command.AttemptId, out var attempt) && attempt == recovery.CommandAttemptId &&
            Guid.TryParse(command.CorrelationId, out var correlation) &&
            correlation == recovery.CommandCorrelationId &&
            Guid.TryParse(command.RuntimeEpoch, out var runtimeEpoch) &&
            runtimeEpoch == recovery.RecoveryRuntimeEpoch &&
            command.CommandKind == (long)AuditedCommandKind.ManualProductionRecovery &&
            command.Disposition == (long)CommandDisposition.Accepted,
            "ProductionRecoveryCommandAuditMismatch");

        var identity = AuditChainDatabase.Read(database, @"
            SELECT Kind,Hash,Payload,IdentityPosition
            FROM audit_entries WHERE Sequence=? LIMIT 2;", deadline,
            statement => (Kind: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                IdentityPosition: SqliteNative.ColumnInt64(statement, 3)),
            recovery.AuthorizationAuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(identity.Kind == "IdentityEvent" &&
            identity.Hash == recovery.AuthorizationAuditHash && identity.IdentityPosition > 0,
            "ProductionRecoveryIdentityAuditMismatch");
        byte[] identityPayload;
        try { identityPayload = Convert.FromBase64String(identity.Payload); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ProductionRecoveryIdentityAuditMismatch", exception); }
        ProductionRecoveryAuthorizationAudit? authorization = null;
        var decoded = IdentityAuditEvent.TryReadProductionRecoveryAuthorization(
            identityPayload, identity.IdentityPosition, value.Admission.StationId,
            out authorization, ProductionRecoveryStoreOptions.SchemaVersion);
        AuditChainDatabase.Require(decoded && authorization is not null &&
                authorization.Kind == (value.Kind == ProductionInspectionEventKind.RecoveryRequired
                    ? IdentityEventKind.ProductionRecoveryAuthorized : IdentityEventKind.ProductionRecoveryCompleted) &&
                authorization.AuthorizationPolicy == recovery.AuthorizationPolicy &&
                authorization.InspectionId == value.InspectionId &&
                authorization.CommandCorrelationId == recovery.CommandCorrelationId &&
                authorization.BoundCommandCorrelationId == recovery.CommandCorrelationId &&
                authorization.PrincipalId == recovery.ActorPrincipalId &&
                authorization.ActorPrincipalId == recovery.ActorPrincipalId &&
                authorization.SessionId == recovery.ActorSessionId &&
                authorization.AuthorizationRevision == recovery.AuthorizationRevision &&
                authorization.ActionTargetId == recovery.AuthorizationTarget &&
                authorization.StepUpGrantId == recovery.StepUpGrantId &&
                recovery.AuthorizationSafetyEvidence is { Length: > 0 } &&
                authorization.SafetyEvidence == recovery.AuthorizationSafetyEvidence,
            "ProductionRecoveryAuthorizationAuditMismatch");
        ProductionRecoverySafetyAuditEvidence? safety = null;
        AuditChainDatabase.Require(IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(
                recovery.AuthorizationSafetyEvidence, out safety) && safety is not null &&
            safety.AttemptId == recovery.CommandAttemptId &&
            safety.InspectionId == recovery.InspectionId &&
            safety.ExpectedEventHash == expectedRecoveryTargetHash &&
            safety.AuthorizationTarget == recovery.AuthorizationTarget &&
            safety.RuntimeEpoch == recovery.RecoveryRuntimeEpoch,
            "ProductionRecoverySafetyAuditMismatch");
        var typedObservation = recovery.SafetyEvidence.Observation;
        AuditChainDatabase.Require(safety!.ObservationBindingHash == typedObservation.Binding.ContentHash &&
            safety.ObservationStatus == typedObservation.Status &&
            safety.ObservationReasonCode == typedObservation.ReasonCode &&
            safety.ObservationSourceEpoch == typedObservation.SourceEpoch &&
            safety.ObservationSourceGeneration == typedObservation.SourceGeneration &&
            safety.ObservationObservedAtUtc == typedObservation.ObservedAtUtc &&
            safety.ObservationMonotonicTimestamp == typedObservation.MonotonicTimestamp &&
            safety.ObservationMonotonicFrequency == typedObservation.MonotonicFrequency,
            "ProductionRecoverySafetyObservationMismatch");
    }

    /// <summary>
    /// Replays the physical-failure continuation facts which have no new
    /// production-inspection row.  A failed identity event is only a terminal
    /// continuation when its matching Failed command fact reuses the accepted
    /// recovery attempt and its v3 safety envelope points to the immutable
    /// RecoveryRequired target.  Ordinary rejected authorizations also use the
    /// ProductionRecoveryFailed identity kind, but have no Failed command fact
    /// and are intentionally handled by the normal command/identity verifier.
    /// </summary>
    private static void ValidateProductionRecoveryFailureAudits(sqlite3 database,
        IReadOnlyList<ProductionInspectionStoredRow> rows,
        ProductionInspectionStoreOptions options, StoreDeadline deadline)
    {
        var recoveries = rows.Select(value => value.Event)
            .Where(value => value.Kind == ProductionInspectionEventKind.RecoveryRequired &&
                value.Recovery is not null)
            .ToArray();
        if (recoveries.Length == 0) return;

        var identityRows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL
            ORDER BY IdentityPosition;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Payload: SqliteNative.ColumnText(statement, 2) ?? string.Empty));
        foreach (var row in identityRows)
        {
            if (row.Ordinal <= 0 || row.Payload.Length == 0) continue;
            byte[] payload;
            try { payload = Convert.FromBase64String(row.Payload); }
            catch (FormatException exception)
            { throw new InvalidOperationException("ProductionRecoveryFailureIdentityAuditMismatch", exception); }
            if (!IdentityAuditEvent.TryReadProductionRecoveryAuthorization(payload, row.Ordinal,
                    recoveries[0].Admission.StationId, out var audit,
                    ProductionRecoveryStoreOptions.SchemaVersion) || audit is null ||
                audit.Kind != IdentityEventKind.ProductionRecoveryFailed)
                continue;

            ProductionRecoverySafetyAuditEvidence? safety = null;
            if (!IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(
                    audit.SafetyEvidence, out safety) || safety is null)
                throw new InvalidOperationException("ProductionRecoveryFailureSafetyAuditMismatch");
            var failed = ReadRecoveryCommandFact(database, audit.CommandCorrelationId,
                safety.AttemptId, CommandAuditPhase.Failed, deadline);
            // A rejected authorization is represented by the same identity kind
            // but has only an Outcome command fact. It is not a physical failure
            // continuation and must not be forced into this validator.
            if (failed is null) continue;
            var recovery = recoveries.Where(value => value.InspectionId == audit.InspectionId &&
                    (value.Recovery!.PreviousEventHash == safety.ExpectedEventHash ||
                     value.ContentHash == safety.ExpectedEventHash))
                .OrderByDescending(value => value.Position).FirstOrDefault();
            if (recovery is null)
                throw new InvalidOperationException("ProductionRecoveryFailureTargetMissing");
            AuditChainDatabase.Require(failed.Disposition is null &&
                failed.CommandKind == AuditedCommandKind.ManualProductionRecovery &&
                failed.AttemptId == safety.AttemptId &&
                failed.CorrelationId == audit.CommandCorrelationId &&
                failed.RuntimeEpoch == safety.RuntimeEpoch &&
                failed.ReasonCode == audit.ReasonCode &&
                audit.InspectionId == recovery.InspectionId &&
                audit.ActionTargetId == safety.AuthorizationTarget,
                "ProductionRecoveryFailureCommandMismatch");
            var accepted = ReadRecoveryCommandFact(database, audit.CommandCorrelationId,
                safety.AttemptId, CommandAuditPhase.Outcome, deadline);
            AuditChainDatabase.Require(accepted is not null &&
                accepted.Disposition == CommandDisposition.Accepted &&
                accepted.CommandKind == AuditedCommandKind.ManualProductionRecovery &&
                accepted.RuntimeEpoch == safety.RuntimeEpoch &&
                accepted.AttemptId == safety.AttemptId,
                "ProductionRecoveryFailureAdmissionMissing");
            AuditChainDatabase.Require(recovery.Recovery!.Disposition == safety.Disposition,
                "ProductionRecoveryFailureDispositionMismatch");
        }
    }

    private static CommandAuditFact? ReadRecoveryCommandFact(sqlite3 database,
        Guid correlationId, Guid attemptId, CommandAuditPhase phase, StoreDeadline deadline)
    {
        return AuditChainDatabase.Read(database, @"
            SELECT EventId,AttemptId,CorrelationId,RuntimeEpoch,OccurredAtUtc,
                AuthenticatedHumanPrincipalId,CommandKind,Source,ClaimedPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition,ReasonCode
            FROM command_facts
            WHERE CorrelationId=? AND AttemptId=? AND CommandKind=? AND Phase=?
            ORDER BY Position DESC LIMIT 2;", deadline,
            statement => new CommandAuditFact(ParseGuid(SqliteNative.ColumnText(statement, 0)),
                ParseGuid(SqliteNative.ColumnText(statement, 1)),
                ParseGuid(SqliteNative.ColumnText(statement, 2)),
                ParseGuid(SqliteNative.ColumnText(statement, 3)),
                ParseTime(SqliteNative.ColumnText(statement, 4)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 6),
                ParseNullableEnum<CommandSource>(SqliteNative.ColumnText(statement, 7)),
                SqliteNative.ColumnText(statement, 8),
                ParseNullableGuid(SqliteNative.ColumnText(statement, 9)),
                ParseNullableGuid(SqliteNative.ColumnText(statement, 10)),
                (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 11),
                ParseNullableEnum<CommandDisposition>(SqliteNative.ColumnText(statement, 12)),
                SqliteNative.ColumnText(statement, 13)!,
                SqliteNative.ColumnText(statement, 5)),
            correlationId.ToString("D"), attemptId.ToString("D"),
            ((int)AuditedCommandKind.ManualProductionRecovery).ToString(CultureInfo.InvariantCulture),
            ((int)phase).ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
    }
}
