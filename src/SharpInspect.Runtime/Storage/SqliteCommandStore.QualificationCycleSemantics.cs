using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    // Both the writer and cold verifier replay this contract. A signed row is
    // not sufficient if its qualification reference or lifecycle is invalid.
    internal static void ValidateQualificationCycleTransition(QualificationCycleEvent candidate,
        IReadOnlyList<QualificationCycleEvent> prior, StationQualificationSessionEvent qualification,
        TraceStoragePolicySnapshot? policyAtAdmission)
    {
        void Require(bool condition, string reason) => AuditChainDatabase.Require(condition, reason);
        Require(candidate.QualificationPosition == qualification.Position &&
            candidate.QualificationHash == qualification.ContentHash &&
            candidate.SessionId == qualification.SessionId &&
            candidate.ProfileHash == qualification.Header.Plan.ProfileHash &&
            candidate.EndpointBindingHash == qualification.Header.Plan.TransientControllerConfiguration.EndpointBindingHash &&
            candidate.RecordedAtUtc == qualification.RecordedAtUtc &&
            candidate.ReasonCode == qualification.ReasonCode && qualification.Header.Plan.QualificationHarnessIdentity.DevelopmentOnly,
            "QualificationCycleQualificationBindingMismatch");
        Require(candidate.Terminal == (candidate.Kind is QualificationCycleEventKind.AckReset or
            QualificationCycleEventKind.AckTimeout or QualificationCycleEventKind.CycleFaultTerminated),
            "QualificationCycleTerminalKindMismatch");
        var lifecycle = prior.Where(value => value.RunId?.Value == candidate.RunId?.Value &&
            value.SessionId == candidate.SessionId && value.Kind != QualificationCycleEventKind.ProtocolRequestRejected).ToArray();
        var previous = lifecycle.LastOrDefault();
        if (candidate.Kind == QualificationCycleEventKind.ProtocolRequestRejected)
        {
            Require(candidate.ControllerEpoch.HasValue && candidate.CycleSequence.HasValue &&
                qualification.Run is null && (candidate.RunId is null || previous is not null),
                "QualificationCycleRejectionBindingInvalid");
            Require(candidate.RunContentHash == previous?.RunContentHash &&
                candidate.PolicySnapshot?.ContentHash == previous?.PolicySnapshot?.ContentHash,
                "QualificationCycleRejectionRunBindingInvalid");
            return;
        }
        Require(candidate.RunId is not null && candidate.ControllerEpoch is > 0 && candidate.CycleSequence is > 0,
            "QualificationCycleRunIdentityInvalid");
        var run = qualification.Run;
        if (candidate.Kind == QualificationCycleEventKind.Admitted)
        {
            Require(previous is null && run is { Completed: false } && run.RunId.Value == candidate.RunId?.Value &&
                run.ControllerEpoch == candidate.ControllerEpoch && run.CycleSequence == candidate.CycleSequence &&
                candidate.RunContentHash == run.ContentHash, "QualificationCycleAdmissionInvalid");
            Require(!prior.Any(value => value.EndpointBindingHash == candidate.EndpointBindingHash &&
                value.ControllerEpoch == candidate.ControllerEpoch && value.CycleSequence == candidate.CycleSequence),
                "QualificationCycleDuplicateControllerKey");
            Require(!prior.Where(value => value.RunId is not null && value.Kind != QualificationCycleEventKind.ProtocolRequestRejected)
                .GroupBy(value => value.RunId!.Value).Any(group => group.Last().Kind != QualificationCycleEventKind.AckReset),
                "QualificationCycleUnresolvedRunBlocksAdmission");
            Require(candidate.PolicySnapshot is not null && policyAtAdmission is not null &&
                candidate.PolicySnapshot.ContentHash == policyAtAdmission.ContentHash &&
                candidate.PolicySnapshot.Policy.RequiredRoutes.Count == 0,
                "QualificationCyclePolicyUnavailable");
            return;
        }
        Require(previous is not null && lifecycle[0].Kind == QualificationCycleEventKind.Admitted,
            "QualificationCycleAdmissionMissing");
        Require(candidate.ControllerEpoch == previous!.ControllerEpoch && candidate.CycleSequence == previous.CycleSequence &&
            candidate.PolicySnapshot?.ContentHash == previous.PolicySnapshot?.ContentHash && candidate.PolicySnapshot is not null,
            "QualificationCycleFrozenBindingChanged");
        var legal = candidate.Kind switch
        {
            QualificationCycleEventKind.CoreCommitted => previous.Kind == QualificationCycleEventKind.Admitted,
            QualificationCycleEventKind.PublicationPrepared => previous.Kind == QualificationCycleEventKind.CoreCommitted,
            QualificationCycleEventKind.ResultValidPublished => previous.Kind == QualificationCycleEventKind.PublicationPrepared,
            QualificationCycleEventKind.ResultAckObserved => previous.Kind == QualificationCycleEventKind.ResultValidPublished,
            QualificationCycleEventKind.ResultValidCleared => previous.Kind == QualificationCycleEventKind.ResultAckObserved,
            QualificationCycleEventKind.AckReset => previous.Kind == QualificationCycleEventKind.ResultValidCleared,
            QualificationCycleEventKind.AckTimeout => previous.Kind is QualificationCycleEventKind.ResultValidPublished or
                QualificationCycleEventKind.ResultValidCleared,
            QualificationCycleEventKind.CycleFaultTerminated => previous.Kind != QualificationCycleEventKind.AckReset,
            _ => false
        };
        Require(legal, "QualificationCycleTransitionInvalid");
        if (candidate.Kind == QualificationCycleEventKind.CoreCommitted)
        {
            Require(run is { Completed: true, QualificationPayload: not null } && run.RunId.Value == candidate.RunId?.Value &&
                run.ControllerEpoch == candidate.ControllerEpoch && run.CycleSequence == candidate.CycleSequence &&
                candidate.RunContentHash == run.ContentHash, "QualificationCycleCoreIncomplete");
            Require(ValidateCompletedRun(qualification.Header, run!) is null, "QualificationCycleCoreResultInvalid");
        }
        else if (run is not null)
        {
            Require(candidate.Kind == QualificationCycleEventKind.CycleFaultTerminated && run.Completed &&
                run.RunId.Value == candidate.RunId?.Value && run.ControllerEpoch == candidate.ControllerEpoch &&
                run.CycleSequence == candidate.CycleSequence && candidate.RunContentHash == run.ContentHash &&
                run.QualificationPayload is null && ValidateCompletedRun(qualification.Header, run) is null,
                "QualificationCycleFaultRunInvalid");
        }
        else Require(candidate.RunContentHash == previous.RunContentHash,
            "QualificationCycleCoreReferenceChanged");
    }

    private static TraceStoragePolicySnapshot? ReadQualificationCyclePolicyAt(sqlite3 database,
        long qualificationAuditSequence, StoreDeadline deadline)
    {
        var encoded = AuditChainDatabase.Read(database,
            "SELECT PublicationPayload FROM trace_storage_policy_events WHERE CentralSequence<? ORDER BY CentralSequence DESC LIMIT 1;",
            deadline, statement => SqliteNative.ColumnText(statement, 0),
            qualificationAuditSequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        if (encoded is null) return null;
        AuditChainDatabase.Require(encoded.Length <= TraceStoragePolicyStoreOptions.MaximumPayloadBytesHardLimit * 2,
            "QualificationCyclePolicyPayloadTooLarge");
        return new(TraceStoragePolicyStorageCodec.DecodePublication(Convert.FromBase64String(encoded)));
    }

    private static IReadOnlyList<StationQualificationStoredRow> ReadQualificationCycleDependencyRows(
        sqlite3 database, StoreDeadline deadline)
    {
        var options = AuditChainDatabase.Read(database,
            "SELECT MaximumEntries,MaximumPayloadBytes,MaximumTotalBytes FROM station_qualification_store_config WHERE Id=1 LIMIT 2;",
            deadline, statement => new StationQualificationStoreOptions
            {
                MaximumEntries = checked((int)SqliteNative.ColumnInt64(statement, 0)),
                MaximumPayloadBytes = checked((int)SqliteNative.ColumnInt64(statement, 1)),
                MaximumTotalBytes = SqliteNative.ColumnInt64(statement, 2)
            }).SingleOrDefault() ?? throw new InvalidOperationException("QualificationCycleSessionStoreRequired");
        return ReadStationQualificationRows(database, options, deadline);
    }

    internal static int QualificationCycleRemainingRows(QualificationCycleEventKind kind) => kind switch
    {
        QualificationCycleEventKind.Admitted => 6,
        QualificationCycleEventKind.CoreCommitted => 5,
        QualificationCycleEventKind.PublicationPrepared => 4,
        QualificationCycleEventKind.ResultValidPublished => 3,
        QualificationCycleEventKind.ResultAckObserved => 2,
        QualificationCycleEventKind.ResultValidCleared => 1,
        QualificationCycleEventKind.AckReset => 0,
        QualificationCycleEventKind.AckTimeout or QualificationCycleEventKind.CycleFaultTerminated => 1,
        _ => throw new InvalidOperationException("QualificationCycleLifecycleKindInvalid")
    };

    private static long QualificationCycleFutureRows(IEnumerable<QualificationCycleEvent> events) => events
        .Where(value => value.RunId is not null && value.Kind != QualificationCycleEventKind.ProtocolRequestRejected)
        .GroupBy(value => value.RunId!.Value).Sum(group => (long)QualificationCycleRemainingRows(group.Last().Kind));
}
