using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private static void ValidateProductionInspectionProjections(sqlite3 database,
        IReadOnlyList<ProductionInspectionStoredRow> rows, StoreDeadline deadline)
    {
        var admissions = new Dictionary<Guid, ProductionInspectionStoredRow>();
        var cores = new Dictionary<Guid, ProductionInspectionStoredRow>();
        foreach (var row in rows)
        {
            var value = row.Event;
            if (value.Kind == ProductionInspectionEventKind.Admitted)
            {
                AuditChainDatabase.Require(admissions.TryAdd(value.InspectionId, row) && value.Core is null,
                    "ProductionInspectionAdmissionBindingMismatch");
                VerifyProductionAdmissionProjection(database, row, deadline);
            }
            AuditChainDatabase.Require(admissions.TryGetValue(value.InspectionId, out var admitted) &&
                admitted.Event.Admission.ContentHash == value.Admission.ContentHash,
                "ProductionInspectionFrozenAdmissionMismatch");
            if (value.Kind == ProductionInspectionEventKind.CoreCommitted)
            {
                AuditChainDatabase.Require(value.Core is not null && cores.TryAdd(value.InspectionId, row),
                    "ProductionInspectionCoreBindingMismatch");
                VerifyProductionCoreProjection(database, row, admitted!.Position, deadline);
            }
            var expectedCore = cores.TryGetValue(value.InspectionId, out var committed)
                ? committed.Event.Core!.ContentHash : null;
            AuditChainDatabase.Require(value.Core?.ContentHash == expectedCore,
                "ProductionInspectionFrozenCoreMismatch");
        }
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_inspection_admissions;", deadline) == admissions.Count,
            "ProductionInspectionAdmissionProjectionCountMismatch");
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_inspection_cores;", deadline) == cores.Count,
            "ProductionInspectionCoreProjectionCountMismatch");
    }

    private static void VerifyProductionAdmissionProjection(sqlite3 database,
        ProductionInspectionStoredRow row, StoreDeadline deadline)
    {
        var value = row.Event;
        var admission = value.Admission;
        RequireProductionProjection(database, "production_inspection_admissions", new[]
        {
            "Position", "InspectionId", "CorrelationId", "RuntimeEpoch", "StationId", "AdmissionGeneration",
            "ControllerEpoch", "CycleSequence", "EvidenceRequirement", "ActivationPosition", "ActivationId",
            "ActivationHash", "ActivationSnapshotHash", "EndpointBindingHash", "PlcProfileHash", "PlcPolicyHash",
            "ConnectionGeneration", "ConnectionAttempt", "AcceptedAtUtc", "AcceptedMonotonicTimestamp",
            "TracePolicyVersion", "TracePolicyHash", "RetentionObligationsHash", "ContentHash", "PayloadHash",
            "Payload", "AuditSequence", "AuditHash"
        }, new string?[]
        {
            Invariant(row.Position), admission.InspectionId.ToString("D"), admission.CorrelationId.ToString("D"),
            admission.RuntimeEpoch.ToString("D"), admission.StationId, Invariant(admission.AdmissionGeneration),
            Invariant(admission.ControllerCycle.ControllerEpoch), Invariant(admission.ControllerCycle.CycleSequence),
            Invariant((int)admission.EvidenceRequirement), Invariant(admission.ActivationReference.Position),
            admission.ActivationReference.ActivationId.ToString("D"), admission.ActivationReference.ContentHash,
            admission.ActivationSnapshot.ContentHash, admission.EndpointBindingHash, admission.PlcProfileHash,
            admission.PlcPolicyHash, Invariant(admission.ConnectionGeneration), Invariant(admission.ConnectionAttempt),
            admission.AcceptedAtUtc.ToString("O", CultureInfo.InvariantCulture), Invariant(admission.AcceptedMonotonicTimestamp),
            Invariant(admission.TracePolicySnapshot.Version), admission.TracePolicySnapshot.ContentHash,
            RetentionHash(admission.RetentionObligations), admission.ContentHash, row.PayloadHash,
            Convert.ToBase64String(row.Payload), Invariant(value.AuditSequence), value.AuditHash
        }, admission.InspectionId, deadline, "ProductionInspectionAdmissionProjectionMismatch");
    }

    private static void VerifyProductionCoreProjection(sqlite3 database,
        ProductionInspectionStoredRow row, long admissionPosition, StoreDeadline deadline)
    {
        var value = row.Event;
        var core = value.Core!;
        RequireProductionProjection(database, "production_inspection_cores", new[]
        {
            "InspectionId", "AdmissionPosition", "AdmissionContentHash", "State", "ExecutionStatus", "Decision",
            "ReasonCode", "AcquisitionFailureKind", "AcquisitionFailureReasonCode", "PreparedAlgorithmInstanceId",
            "ContentHash", "PayloadHash", "Payload", "CommittedAtUtc", "CommittedMonotonicTimestamp",
            "AuditSequence", "AuditHash"
        }, new string?[]
        {
            core.Admission.InspectionId.ToString("D"), Invariant(admissionPosition), core.Admission.ContentHash,
            Invariant((int)core.State), Invariant((int)core.ExecutionStatus), Invariant((int)core.Decision),
            core.ReasonCode, core.AcquisitionFailureKind is { } kind ? Invariant((int)kind) : null,
            core.AcquisitionFailureReasonCode, core.PreparedAlgorithmInstanceId.ToString("D"), core.ContentHash,
            row.PayloadHash, Convert.ToBase64String(row.Payload), core.CommittedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Invariant(core.CommittedMonotonicTimestamp), Invariant(value.AuditSequence), value.AuditHash
        }, core.Admission.InspectionId, deadline, "ProductionInspectionCoreProjectionMismatch");
    }

    private static void RequireProductionProjection(sqlite3 database, string table, string[] columns,
        string?[] expected, Guid inspectionId, StoreDeadline deadline, string reason)
    {
        // Table and column names above are fixed implementation constants; all values are bound.
        var matches = AuditChainDatabase.Read(database,
            $"SELECT {string.Join(",", columns)} FROM {table} WHERE InspectionId=? LIMIT 2;", deadline,
            statement => Enumerable.Range(0, columns.Length)
                .Select(index => SqliteNative.ColumnText(statement, index)).ToArray(), inspectionId.ToString("D"));
        AuditChainDatabase.Require(matches.Count == 1 && matches[0].SequenceEqual(expected, StringComparer.Ordinal), reason);
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
