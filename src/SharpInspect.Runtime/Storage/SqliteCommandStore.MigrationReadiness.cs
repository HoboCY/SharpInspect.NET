using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Stored migration-readiness replay for the bundled schema-32 to schema-33
/// startup migration. It answers one question from durable evidence only: does
/// a configured subsystem still own active, unresolved or recovery-required
/// station work that a table rebuild must not be allowed to run through?
///
/// Nothing here repairs, retires, rewrites or invents a fact. Every check
/// reads the same ledgers, reuses the same complete history/replay readers and
/// applies the same release rule the owning subsystem already uses, so
/// deliberately retained closed work (a terminal arm attempt, a cleared
/// inspection cycle, a released recipe activation, a finished qualification
/// session) never blocks the migration.
///
/// Covered subsystems: production inspection cycles (including production
/// recovery, whose completion is an inspection event), station qualification,
/// qualification cycles, calibration sessions, preview sessions, manual
/// inspection sessions, recipe activation, camera recovery cycles, camera
/// network maintenance, the recipe change handshake and production arming.
///
/// Not covered because the ticket's contract keeps them out of the readiness
/// gate: append-only classification ledgers that hold no in-flight state
/// (algorithm archive, recipe drafts, releases, result contracts, calibration
/// governance and imports, trace storage policies, part identities), the
/// imaging setup and identity changes whose authorization and effects commit
/// atomically. Production admission and camera setup have separate durable
/// pending projections and are checked below. PLC recovery tails must also be
/// closed; no cross-epoch completion is invented for an old unresolved tail.
/// A configured subsystem whose ledger is
/// absent is not treated as idle: the replay fails closed with its own
/// StoreMigration...EvidenceMissing reason.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// Refuses the migration while any configured subsystem still holds durable
    /// evidence of unfinished work. Callers invoke this on the exclusive
    /// maintenance connection after the external journal, the source
    /// fingerprint, the audit chain and the stored feature configuration have
    /// already been verified; the method only replays stored facts. A refusal
    /// is an <see cref="InvalidOperationException"/> whose message is the exact
    /// StoreMigration reason code.
    /// </summary>
    internal void RequireMigrationQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(deadline);
        RequireProductionInspectionQuiescent(database, deadline);
        RequireProductionAdmissionQuiescent(database, deadline);
        RequireCameraSetupQuiescent(database, deadline);
        RequirePlcCommunicationQuiescent(database, deadline);
        RequireStationQualificationQuiescent(database, deadline);
        RequireQualificationCycleQuiescent(database, deadline);
        RequireCalibrationSessionQuiescent(database, deadline);
        RequirePreviewSessionQuiescent(database, deadline);
        RequireManualInspectionQuiescent(database, deadline);
        RequireRecipeActivationQuiescent(database, deadline);
        RequireCameraRecoveryQuiescent(database, deadline);
        RequireCameraNetworkQuiescent(database, deadline);
        RequireRecipeChangeQuiescent(database, deadline);
        RequireProductionArmQuiescent(database, deadline);
    }

    private void RequireCameraSetupQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.CameraSetup is null) return;
        RequireMigrationEvidence(database, "camera_setup_events", "StoreMigrationCameraSetupEvidenceMissing", deadline);
        // PendingOperationCount covers every logical role and signed rebind
        // admission, including admissions with no camera row yet.
        AuditChainDatabase.Require(ReadCameraSetupState(database, string.Empty, deadline).PendingOperationCount == 0,
            "StoreMigrationCameraSetupNotQuiescent");
    }

    private void RequireProductionAdmissionQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.ProductionAdmission is null) return;
        RequireMigrationEvidence(database, "production_admission_events", "StoreMigrationProductionAdmissionEvidenceMissing", deadline);
        AuditChainDatabase.Require(ReadProductionAdmissionAuditReserve(database, deadline) == 0,
            "StoreMigrationProductionAdmissionNotQuiescent");
    }

    private void RequirePlcCommunicationQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.PlcCommunication is null) return;
        var pending = new HashSet<(string Endpoint, Guid Epoch, Guid? Cycle)>();
        foreach (var row in ReadPlcCommunicationRows(database, _options.PlcCommunication, deadline))
        {
            var value = row.Event;
            var key = (value.EndpointBindingHash, value.RuntimeEpoch, value.RecoveryCycleId);
            switch (value.Kind)
            {
                case PlcCommunicationEventKind.HeartbeatStale:
                case PlcCommunicationEventKind.ControllerEpochChanged:
                case PlcCommunicationEventKind.CommunicationLost:
                case PlcCommunicationEventKind.ReconnectFailed:
                case PlcCommunicationEventKind.RecoveryExhausted:
                    pending.Add(key); break;
                case PlcCommunicationEventKind.RecoveryCompleted:
                    pending.Remove(key); break;
            }
        }
        AuditChainDatabase.Require(pending.Count == 0, "StoreMigrationPlcRecoveryClosureRequired");
    }

    /// <summary>
    /// Replays every stored inspection cycle with the writer's own
    /// reservation: a cycle is released only by AcknowledgementReset or
    /// RecoveryCompleted. FaultTerminated, RecoveryRequired and every
    /// intermediate phase still own a recovery tail, and a retired cycle is
    /// never persisted as an event of its own, so it is not inferred here.
    /// </summary>
    private void RequireProductionInspectionQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.ProductionInspections is null) return;
        RequireMigrationEvidence(database, "production_inspection_events",
            "StoreMigrationProductionInspectionEvidenceMissing", deadline);
        var reservation = ReadProductionInspectionReservation(database, deadline);
        AuditChainDatabase.Require(reservation.Rows == 0, "StoreMigrationProductionInspectionNotQuiescent");
    }

    /// <summary>
    /// A station qualification session whose last stored event is not terminal
    /// still owns the physical facility retirement tail that admission
    /// reserved; only a completed session releases it.
    /// </summary>
    private void RequireStationQualificationQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.StationQualifications;
        if (options is null) return;
        RequireMigrationEvidence(database, "station_qualification_events",
            "StoreMigrationStationQualificationEvidenceMissing", deadline);
        var rows = ReadStationQualificationRows(database, options, deadline);
        AuditChainDatabase.Require(ReadStationQualificationAuditReserveAfter(rows, null) == 0,
            "StoreMigrationStationQualificationNotQuiescent");
    }

    /// <summary>
    /// A qualification cycle run is released only by its acknowledged reset;
    /// the ledger itself refuses a new admission while any run still ends in
    /// another kind, so a remaining tail is unresolved work here as well.
    /// </summary>
    private void RequireQualificationCycleQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.QualificationCycles is null) return;
        RequireMigrationEvidence(database, "qualification_cycle_events",
            "StoreMigrationQualificationCycleEvidenceMissing", deadline);
        AuditChainDatabase.Require(ReadQualificationCycleAuditReserve(database, deadline) == 0,
            "StoreMigrationQualificationCycleNotQuiescent");
    }

    /// <summary>
    /// Replays the open-session projection of the calibration ledger with the
    /// same rule restart recovery uses: a session is open while its last event
    /// is still pending, or while it retains an accepted action without a
    /// durable terminal fact.
    /// </summary>
    private void RequireCalibrationSessionQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.CalibrationSessions is null) return;
        RequireMigrationEvidence(database, "calibration_sessions",
            "StoreMigrationCalibrationSessionEvidenceMissing", deadline);
        RequireMigrationEvidence(database, "calibration_session_events",
            "StoreMigrationCalibrationSessionEvidenceMissing", deadline);
        foreach (var session in ReadCalibrationSessionRows(database, null, deadline))
        {
            var events = ReadCalibrationEventRows(database, session.Header.SessionId, deadline);
            if (events.Count == 0) continue;
            var pendingActions = ReadPendingCalibrationFactRows(database, session.Header, deadline);
            AuditChainDatabase.Require(
                events[^1].Value.Outcome != CalibrationSessionOutcome.Pending && pendingActions.Count == 0,
                "StoreMigrationCalibrationSessionNotQuiescent");
        }
    }

    /// <summary>
    /// The current preview session is projected from its complete verified
    /// history: an active session, a recovery-required header and a
    /// recovery-blocked restoration all keep the exclusive camera owner.
    /// </summary>
    private void RequirePreviewSessionQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.PreviewSessions;
        if (options is null) return;
        RequireMigrationEvidence(database, "preview_session_events",
            "StoreMigrationPreviewSessionEvidenceMissing", deadline);
        var current = ReadCurrentPreviewSession(database, options, deadline);
        AuditChainDatabase.Require(string.Equals(current.ReasonCode, "PreviewSessionIdle", StringComparison.Ordinal),
            "StoreMigrationPreviewSessionNotQuiescent");
    }

    /// <summary>
    /// The current manual inspection session is projected from its complete
    /// verified history: an active session, a recovery-required header and a
    /// recovery-blocked restoration all keep the manual run owner.
    /// </summary>
    private void RequireManualInspectionQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.ManualInspections;
        if (options is null) return;
        RequireMigrationEvidence(database, "manual_inspection_events",
            "StoreMigrationManualInspectionEvidenceMissing", deadline);
        var current = ReadCurrentManualInspectionSession(database, options, deadline);
        AuditChainDatabase.Require(string.Equals(current.ReasonCode, "ManualInspectionIdle", StringComparison.Ordinal),
            "StoreMigrationManualInspectionNotQuiescent");
    }

    /// <summary>
    /// An activation that was admitted without its matching terminal record is
    /// an unfinished activation command. A terminal activation is deliberate
    /// retained station state and stays migratable.
    /// </summary>
    private void RequireRecipeActivationQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.RecipeActivations is null) return;
        RequireMigrationEvidence(database, "recipe_activation_events",
            "StoreMigrationRecipeActivationEvidenceMissing", deadline);
        var state = ReadRecipeActivationCommandState(database, deadline);
        AuditChainDatabase.Require(state.PendingAdmission is null, "StoreMigrationRecipeActivationNotQuiescent");
    }

    /// <summary>
    /// Every accepted StartCameraRecoveryCycle admission must be closed by its
    /// terminal row, which the writer only commits together with the terminal
    /// Completed/Failed command fact. An admission without a terminal row is
    /// exactly the crash-between-admission-and-terminal state, so it blocks.
    /// </summary>
    private void RequireCameraRecoveryQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.CameraRecovery is null) return;
        RequireMigrationEvidence(database, "camera_recovery_terminal_events",
            "StoreMigrationCameraRecoveryEvidenceMissing", deadline);
        var terminals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ReadCameraRecoveryRows(database, "ORDER BY Position", deadline))
        {
            // Re-proves the stored terminal record shape before it is trusted
            // as the closure of an admission.
            _ = DecodeRow(row);
            terminals.Add(row.AttemptId);
        }

        var admissions = AuditChainDatabase.Read(database, @"SELECT Position,EventId,AttemptId,CorrelationId,
            RuntimeEpoch,CommandKind,Source,ClaimedPrincipalId,ClaimedSessionId,ClaimedStepUpGrantId,
            Phase,Disposition,ReasonCode,AuthenticatedHumanPrincipalId FROM command_facts
            WHERE CommandKind=? ORDER BY Position;", deadline, ReadRecoveryCommandAudit,
            ((int)AuditedCommandKind.StartCameraRecoveryCycle).ToString(CultureInfo.InvariantCulture));
        foreach (var admission in admissions)
        {
            if (admission.Phase != CommandAuditPhase.Outcome ||
                admission.Disposition != CommandDisposition.Accepted) continue;
            AuditChainDatabase.Require(terminals.Contains(admission.AttemptId.ToString("D")),
                "StoreMigrationCameraRecoveryNotQuiescent");
        }
    }

    /// <summary>
    /// The network ledger is projected with the same rule the runtime uses: an
    /// admission without its terminal or rejected record is pending work, and
    /// an Unknown terminal is the durable marker that the station network state
    /// was never established and still needs reconciliation.
    /// </summary>
    private void RequireCameraNetworkQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.CameraNetwork;
        if (options is null) return;
        RequireMigrationEvidence(database, "camera_network_events",
            "StoreMigrationCameraNetworkEvidenceMissing", deadline);
        var events = ReadCameraNetworkEvents(database, options, deadline);
        var pending = FindPendingNetworkAdmissions(events);
        var unknown = events.Any(value => value.Phase == CameraNetworkEventPhase.Terminal &&
            value.State == CameraNetworkMaintenanceState.Unknown);
        AuditChainDatabase.Require(pending.Count == 0 && !unknown,
            "StoreMigrationCameraNetworkNotQuiescent");
    }

    /// <summary>
    /// One recipe change request reserves its decision, response,
    /// acknowledgement, clear, reset and fault facts. The stored reserve is
    /// zero exactly when every episode is closed by a reset, or by a committed
    /// decision together with its recorded protocol fault; a bare observation
    /// or a bare protocol fault keeps the episode unresolved.
    /// </summary>
    private void RequireRecipeChangeQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        if (_options.RecipeSelections is null) return;
        RequireMigrationEvidence(database, "recipe_change_events",
            "StoreMigrationRecipeChangeEvidenceMissing", deadline);
        AuditChainDatabase.Require(ReadRecipeChangeAuditReserve(database, deadline) == 0,
            "StoreMigrationRecipeChangeNotQuiescent");
    }

    /// <summary>
    /// An arm attempt is closed by exactly one terminal event (ReadyConfirmed,
    /// Rejected or Failed). The optional status-delivery observation may follow
    /// a terminal attempt, so its retained reserve entry is not unfinished
    /// work; only an attempt without any terminal event is an open arm action.
    /// </summary>
    private void RequireProductionArmQuiescent(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.ProductionArming;
        if (options is null) return;
        RequireMigrationEvidence(database, "production_arm_events",
            "StoreMigrationProductionArmEvidenceMissing", deadline);
        foreach (var attempt in ReadProductionArmRows(database, options, deadline)
            .GroupBy(row => row.Event.AttemptId))
        {
            AuditChainDatabase.Require(attempt.Any(row => row.Event.Terminal),
                "StoreMigrationProductionArmNotQuiescent");
        }
    }

    /// <summary>
    /// A configured subsystem whose ledger is absent has no durable evidence to
    /// replay. Readiness fails closed with the subsystem's own reason instead
    /// of silently treating that subsystem as idle.
    /// </summary>
    private static void RequireMigrationEvidence(sqlite3 database, string table, string reason,
        StoreDeadline deadline)
    {
        AuditChainDatabase.Require(AuditChainDatabase.TableExists(database, table, deadline), reason);
    }
}
