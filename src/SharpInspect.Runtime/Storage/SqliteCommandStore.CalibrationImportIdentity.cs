using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal ValueTask<IdentityWriteResult> UpdateCalibrationImportCommandAsync(CalibrationImportCommand command,
        Func<IdentityAuthorityState, CalibrationImportCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    private CalibrationImportCommandState ReadCalibrationImportCommandState(sqlite3 database,
        CalibrationImportCommand? command, StoreDeadline deadline)
    {
        if (_options.CalibrationImports is not { } options)
            return new(Array.Empty<CalibrationImportRecord>(), Array.Empty<object>(), null, null, false);
        var records = ReadCalibrationImportRows(database, options, deadline)
            .Select(value => CalibrationGovernanceCodec.DecodeImport(DecodeImportRowPayload(value, options))).ToArray();
        var governance = ReadCalibrationGovernanceRowsForImport(database, deadline);
        // The complete snapshot verifier has already checked each import against
        // the governance prefix preceding its central audit sequence.
        var role = command is RevalidateImportedCalibrationCommand revalidate ? revalidate.Requirement.LogicalCameraRole :
            command is null ? null : records.OfType<ImportedCalibrationEvaluation>().LastOrDefault(value =>
                value.Candidate == CalibrationImportProjection.CommandCandidate(command))?.Content.Requirement.LogicalCameraRole;
        var camera = role is null ? null : ReadCameraSetupState(database, role, deadline);
        var imaging = role is null ? null : ReadImagingSetupState(database, role, deadline).Current;
        return new(records, governance, camera?.Binding, imaging, true,
            camera is { Pending: not null } || camera is { PendingAdmission: not null });
    }

    private void AppendCalibrationImportIdentityMutation(sqlite3 database, IdentityUpdate update,
        CalibrationImportCommandState state, CalibrationImportCommand command, StoreDeadline deadline)
    {
        var record = update.CalibrationImport!.Record;
        if (update.CommandFacts is not { Count: 1 } || update.Events.Count != 1)
            throw new InvalidOperationException("CalibrationImportIdentityMutationInvalid");
        var fact = update.CommandFacts[0];
        var identity = update.Events[0];
        if (record is ImportedCalibrationPhysicalVerification physical)
            AuditChainDatabase.Require(physical.Witness?.RuntimeEpoch == fact.RuntimeEpoch,
                "CalibrationImportPhysicalWitnessEpochMismatch");
        AuditChainDatabase.Require(record.OperationId == command.CorrelationId && record.Position == state.Records.Count + 1L &&
            record.AuthorizationTarget == command.AuthorizationTarget && fact.CorrelationId == command.CorrelationId &&
            fact.Disposition == CommandDisposition.Accepted && fact.Phase == CommandAuditPhase.Outcome &&
            identity.Kind == IdentityEventKind.CalibrationGovernanceActionAuthorized && identity.OperationId == command.CorrelationId &&
            identity.ActionTargetId == command.AuthorizationTarget && identity.BoundCommandCorrelationId == command.CorrelationId &&
            identity.CommandCorrelationId == command.CorrelationId && identity.PrincipalId == record.Actor.PrincipalId &&
            identity.ActorPrincipalId == record.Actor.PrincipalId && identity.SessionId == record.Actor.SessionId &&
            identity.AuthorizationRevision == record.Actor.AuthorizationRevision && identity.StepUpGrantId is not null &&
            identity.StepUpGrantId == command.Invocation.StepUpGrantId && fact.ClaimedStepUpGrantId == identity.StepUpGrantId &&
            fact.AuthenticatedHumanPrincipalId == record.Actor.PrincipalId.ToString("D") &&
            fact.ClaimedSessionId == record.Actor.SessionId && fact.OccurredAtUtc == record.RecordedAtUtc &&
            identity.OccurredAtUtc == record.RecordedAtUtc, "CalibrationImportCommandBindingMismatch");
        AppendCalibrationImport(database, record, fact, identity, deadline);
    }

    internal async ValueTask<CalibrationImportStateQuery> ReadCalibrationImportStateAsync(
        CalibrationImportCommand? command = null, CancellationToken cancellationToken = default)
    {
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _databasePath is null || _options.CalibrationImports is null)
            return new(false, "CalibrationImportUnavailable");
        try
        {
            return await Task.Run(() =>
            {
                using var connection = SqliteNative.Open(_databasePath, readOnly: true);
                var database = connection.Handle!;
                SqliteNative.ConfigureSqliteLimit(database, _options);
                var deadline = new StoreDeadline(_options.QueryTimeout);
                SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
                try
                {
                    VerifyCalibrationReadSnapshot(database, deadline);
                    var state = ReadCalibrationImportCommandState(database, command, deadline);
                    var importTail = AuditChainDatabase.Read(database,
                        "SELECT Position,RecordContentHash FROM calibration_import_events ORDER BY Position DESC LIMIT 1;", deadline,
                        s => (Position: SqliteNative.ColumnInt64(s, 0), Hash: SqliteNative.ColumnText(s, 1))).SingleOrDefault();
                    var governanceTail = AuditChainDatabase.Read(database,
                        "SELECT Position,RecordContentHash FROM calibration_governance_events ORDER BY Position DESC LIMIT 1;", deadline,
                        s => (Position: SqliteNative.ColumnInt64(s, 0), Hash: SqliteNative.ColumnText(s, 1))).SingleOrDefault();
                    SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
                    return new CalibrationImportStateQuery(true, "CalibrationImportStateAvailable", state,
                        importTail.Position, importTail.Hash, governanceTail.Position, governanceTail.Hash);
                }
                catch
                {
                    try { SqliteNative.Execute(database, "ROLLBACK;", deadline); } catch { }
                    throw;
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, exception is InvalidOperationException invalid ? invalid.Message : "CalibrationImportQueryUnavailable");
        }
    }
}

internal sealed record CalibrationImportCommandState(IReadOnlyList<CalibrationImportRecord> Records,
    IReadOnlyList<object> Governance, CameraBindingRevision? Binding, ImagingSetupRevision? Imaging,
    bool Enabled, bool CameraPending = false);
internal sealed record CalibrationImportMutation(CalibrationImportRecord Record);
internal sealed record CalibrationImportStateQuery(bool Available, string ReasonCode,
    CalibrationImportCommandState? State = null, long ImportPosition = 0, string? ImportHash = null,
    long GovernancePosition = 0, string? GovernanceHash = null);
