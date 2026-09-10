using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;
using ProductionAdmissionEventType = SharpInspect.Abstractions.ProductionAdmissionEventKind;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// Transaction-local append for a schema-22 admission report. The caller must
/// already have appended the command and identity facts in the same SQLite
/// transaction. This method derives the ledger position from the actual table
/// tail; a caller cannot reserve or replace a position through the projection.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal ProductionAdmissionHistoryEvent AppendProductionAdmissionEvent(
        sqlite3 database, ProductionAdmissionHistoryEvent input, StoreDeadline deadline,
        long? productionAdmissionReserveOverride = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(input);
        var options = _options.ProductionAdmission ??
            throw new InvalidOperationException("ProductionAdmissionConfigurationRequired");
        options.Validate();
        AuditChainDatabase.Require(input.AuditSequence == 0 && input.AuditHash is null,
            "ProductionAdmissionEventAlreadyPersisted");
        AuditChainDatabase.Require(input.CommandAuditSequence is > 0 &&
            input.CommandAuditHash is { Length: 64 } && input.AuthorizationAuditSequence is > 0 &&
            input.AuthorizationAuditHash is { Length: 64 },
            "ProductionAdmissionAuditReferenceMissing");

        var existing = ReadProductionAdmissionRows(database, options, deadline);
        var attemptStates = existing.GroupBy(row => row.Event.AttemptId)
            .ToDictionary(group => group.Key, group => group.Last().Event);
        var completing = input.Kind is ProductionAdmissionEventType.Completed or
            ProductionAdmissionEventType.Failed;
        if (input.Kind is ProductionAdmissionEventType.Admitted or ProductionAdmissionEventType.Rejected)
            AuditChainDatabase.Require(!attemptStates.ContainsKey(input.AttemptId),
                input.Kind == ProductionAdmissionEventType.Admitted
                    ? "ProductionAdmissionDuplicateAdmission" : "ProductionAdmissionDuplicateRejection");
        else
            AuditChainDatabase.Require(completing && attemptStates.TryGetValue(input.AttemptId, out var admission) &&
                admission.Kind == ProductionAdmissionEventType.Admitted &&
                admission.CorrelationId == input.CorrelationId && admission.RuntimeEpoch == input.RuntimeEpoch,
                "ProductionAdmissionTerminalWithoutAdmission");
        // A repeated correlation is a rejected command fact, not a second
        // admission.  The identity writer has already classified that request
        // as DuplicateCorrelationId and gives it a fresh attempt id, so retain
        // the rejection report while forbidding another admitted/terminal row
        // from changing the original attempt's ownership.
        foreach (var prior in existing.Where(row => row.Event.CorrelationId == input.CorrelationId))
            if (input.Kind != ProductionAdmissionEventType.Rejected)
                AuditChainDatabase.Require(prior.Event.AttemptId == input.AttemptId,
                    "ProductionAdmissionCorrelationReused");
        var pending = attemptStates.Values.Count(value =>
            value.Kind == ProductionAdmissionEventType.Admitted);
        var remainingPending = checked(pending - (completing ? 1 : 0));
        var rowsToReserve = checked(1 + remainingPending +
            (input.Kind == ProductionAdmissionEventType.Admitted ? 1 : 0));
        AuditChainDatabase.Require(checked(existing.Count + rowsToReserve) <= options.MaximumEntries,
            "ProductionAdmissionEntryCapacityExceeded");
        var position = checked(existing.Count + 1L);
        var previousHash = existing.Count == 0 ? null : existing[^1].Event.AuditHash;
        var withoutPayloadHash = NewProductionAdmissionEvent(input, position,
            payloadHash: null, auditSequence: 0, auditHash: null);
        var payloadHash = ProductionAdmissionStorageCodec.PayloadHash(
            ProductionAdmissionStorageCodec.EncodeAuditBinding(withoutPayloadHash));
        var beforeAudit = NewProductionAdmissionEvent(input, position, payloadHash, 0, null);
        var auditPayload = ProductionAdmissionStorageCodec.EncodeAuditBinding(beforeAudit);
        var auditSequence = AuditChainDatabase.AppendProductionAdmissionLedgerEntry(database,
            _policy!, _signingKey!, position, auditPayload, options, deadline,
            productionAdmissionReserveOverride);
        var auditHash = AuditChainDatabase.Read(database, @"
            SELECT Hash FROM audit_entries
            WHERE Sequence=? AND Kind=? AND ProductionAdmissionPosition=? LIMIT 2;", deadline,
            statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            auditSequence.ToString(CultureInfo.InvariantCulture), ProductionAdmissionEventKind,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(auditHash is { Length: 64 },
            "ProductionAdmissionAuditEntryMissing");

        var persisted = NewProductionAdmissionEvent(beforeAudit, position, payloadHash,
            auditSequence, auditHash);
        var payload = ProductionAdmissionStorageCodec.Encode(persisted);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes,
            "ProductionAdmissionPayloadCapacityExceeded");
        var total = existing.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
        AuditChainDatabase.Require(checked(total + payload.Length) <= options.MaximumTotalBytes,
            "ProductionAdmissionTotalCapacityExceeded");
        AuditChainDatabase.Require(checked(total + payload.Length +
            checked((long)(remainingPending +
                (input.Kind == ProductionAdmissionEventType.Admitted ? 1 : 0)) *
                options.MaximumPayloadBytes)) <= options.MaximumTotalBytes,
            "ProductionAdmissionTotalCapacityExceeded");
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_admission_events(
                Position,Kind,PreviousHash,CorrelationId,AttemptId,RuntimeEpoch,
                AdmissionGeneration,ActorPrincipalId,ActorSessionId,ActorAuthorizationRevision,
                AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,StepUpGrantId,
                ExpectedDurableHeads,CurrentDurableHeads,AuthorizationTarget,ReasonCode,
                ReportContentHash,CommandAuditSequence,CommandAuditHash,AuthorizationAuditSequence,
                AuthorizationAuditHash,AuditSequence,AuditHash,PayloadHash,ContentHash,Payload)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            persisted.Position.ToString(CultureInfo.InvariantCulture),
            ((int)persisted.Kind).ToString(CultureInfo.InvariantCulture), previousHash,
            persisted.CorrelationId.ToString("D"), persisted.AttemptId.ToString("D"),
            persisted.RuntimeEpoch.ToString("D"),
            persisted.AdmissionGeneration.ToString(CultureInfo.InvariantCulture),
            persisted.ActorPrincipalId.ToString("D"), persisted.ActorSessionId.ToString("D"),
            persisted.ActorAuthorizationRevision.ToString(CultureInfo.InvariantCulture),
            persisted.AuthorizationPolicy.Id, persisted.AuthorizationPolicy.Version,
            persisted.AuthorizationPolicy.ContentHash, persisted.StepUpGrantId?.ToString("D"),
            EncodeHeads(persisted.ExpectedDurableHeads), EncodeHeads(persisted.CurrentDurableHeads),
            persisted.AuthorizationTarget, persisted.ReasonCode, persisted.Report.ContentHash,
            persisted.CommandAuditSequence!.Value.ToString(CultureInfo.InvariantCulture),
            persisted.CommandAuditHash!,
            persisted.AuthorizationAuditSequence!.Value.ToString(CultureInfo.InvariantCulture),
            persisted.AuthorizationAuditHash!, persisted.AuditSequence.ToString(CultureInfo.InvariantCulture),
            persisted.AuditHash!, persisted.PayloadHash!, persisted.ContentHash,
            Convert.ToBase64String(payload));
        return persisted;
    }

    private static ProductionAdmissionHistoryEvent NewProductionAdmissionEvent(
        ProductionAdmissionHistoryEvent value, long position, string? payloadHash,
        long auditSequence, string? auditHash) => new(position, value.Kind, value.Report,
            value.CorrelationId, value.AttemptId, value.RuntimeEpoch, value.AdmissionGeneration,
            value.ActorPrincipalId, value.ActorSessionId, value.ActorAuthorizationRevision,
            value.AuthorizationPolicy, value.StepUpGrantId, value.ExpectedDurableHeads,
            value.CurrentDurableHeads, value.AuthorizationTarget, value.ReasonCode,
            value.CommandAuditSequence, value.CommandAuditHash,
            value.AuthorizationAuditSequence, value.AuthorizationAuditHash,
            auditSequence, auditHash, payloadHash);

}
