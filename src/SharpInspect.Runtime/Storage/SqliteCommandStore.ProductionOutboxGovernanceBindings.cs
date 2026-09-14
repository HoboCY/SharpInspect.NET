using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal static void VerifyProductionOutboxGovernanceAuditReference(SQLitePCL.sqlite3 database,
        byte[] payload, long sequence, string hash, StoreDeadline deadline)
    {
        var fields = DecodeContractIdentityFields(payload);
        if (fields.Length != 49) return;
        var table = fields[2] switch
        {
            nameof(Identity.IdentityEventKind.OutboxDeliveryRecovered) => "production_outbox_recovery",
            nameof(Identity.IdentityEventKind.OutboxCorrectiveDeliveryCreated) => "production_outbox_corrections",
            _ => null
        };
        if (table is null) return;
        // The table name is one of two fixed literals, never input SQL.
        AuditChainDatabase.Require(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM " + table + " WHERE AuthorizationEventId=? AND AuthorizationAuditSequence=? AND AuthorizationAuditHash=?;",
            deadline, fields[1], ProductionOutboxNumber(sequence), hash) == 1,
            "ProductionOutboxGovernanceAuditRowMissing");
    }

    private static void VerifyProductionOutboxGovernanceTargets(SQLitePCL.sqlite3 database,
        IReadOnlyList<ProductionOutboxStoredDelivery> deliveries, IReadOnlyList<ProductionOutboxStoredEvent> events,
        IReadOnlyList<ProductionOutboxRecoveryStoredRow> recoveries,
        IReadOnlyList<ProductionOutboxCorrectionStoredRow> corrections, StoreDeadline deadline)
    {
        string Revision(OutboxDelivery delivery, long before) => ProductionOutboxStateRevision(delivery,
            events.Where(x => x.Event.DeliveryId == delivery.DeliveryId && x.Event.AuditSequence < before)
                .OrderBy(x => x.Event.AuditSequence).LastOrDefault()?.Event.ContentHash,
            recoveries, corrections, before);
        foreach (var row in recoveries)
        {
            var source = deliveries.Single(x => x.Delivery.DeliveryId == row.DeliveryId);
            AuditChainDatabase.Require(source.CreatedAuditSequence < row.AuthorizationAuditSequence &&
                row.TargetBinding == RecoverOutboxDeliveryCommand.Target(source.Delivery.DeliveryId,
                    source.Delivery.ContentHash, Revision(source.Delivery, row.AuthorizationAuditSequence),
                    row.GrantedAttempts, row.Reason), "ProductionOutboxRecoveryTargetMismatch");
        }
        foreach (var row in corrections)
        {
            var source = deliveries.Single(x => x.Delivery.DeliveryId == row.SourceDeliveryId);
            var corrective = deliveries.Single(x => x.Delivery.DeliveryId == row.DeliveryId);
            AuditChainDatabase.Require(source.CreatedAuditSequence < row.AuthorizationAuditSequence &&
                corrective.CreatedAuditSequence > row.CommandAuditSequence &&
                corrective.Delivery.InspectionId == source.Delivery.InspectionId &&
                corrective.Delivery.CoreHash == source.Delivery.CoreHash &&
                corrective.Delivery.Route.ContentHash == source.Delivery.Route.ContentHash &&
                row.TargetBinding == CreateCorrectiveOutboxDeliveryCommand.Target(source.Delivery.DeliveryId,
                    source.Delivery.ContentHash, Revision(source.Delivery, row.AuthorizationAuditSequence),
                    source.Delivery.Route.ContentType, row.PayloadHash, row.Reason),
                "ProductionOutboxCorrectionTargetMismatch");
        }
        foreach (var created in events.Where(x => x.Event.Kind == OutboxEventKind.Created &&
                     x.Event.ReasonCode == "ProductionOutboxCorrectiveDelivery"))
            AuditChainDatabase.Require(corrections.Count(x => x.DeliveryId == created.Event.DeliveryId) == 1,
                "ProductionOutboxCorrectionLinkMissing");
    }

    internal static string ProductionOutboxStateRevision(OutboxDelivery delivery, string? lastEventHash,
        IEnumerable<ProductionOutboxRecoveryStoredRow> recoveries,
        IEnumerable<ProductionOutboxCorrectionStoredRow> corrections, long beforeAuditSequence = long.MaxValue) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("ProductionOutboxStateRevisionV1",
            delivery.ContentHash, lastEventHash,
            recoveries.Where(x => x.DeliveryId == delivery.DeliveryId && x.AuthorizationAuditSequence < beforeAuditSequence)
                .OrderBy(x => x.AuthorizationAuditSequence).LastOrDefault()?.ContentHash,
            corrections.Where(x => (x.DeliveryId == delivery.DeliveryId || x.SourceDeliveryId == delivery.DeliveryId) &&
                    x.AuthorizationAuditSequence < beforeAuditSequence)
                .OrderBy(x => x.AuthorizationAuditSequence).LastOrDefault()?.ContentHash)));

    // Sign the complete operation before audit references exist; row ContentHash subsequently
    // binds those references. This avoids both circular hashes and unsigned server-generated IDs.
    internal static string ProductionOutboxRecoveryOperationBinding(OutboxRecoveryMutation operation) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("ProductionOutboxRecoveryMutationV1",
            operation.RecoveryId.ToString("D"), operation.DeliveryId.ToString("D"),
            ProductionOutboxNumber(operation.GrantedAttempts), operation.Reason,
            operation.ActorPrincipalId.ToString("D"), operation.ActorSessionId.ToString("D"),
            operation.StepUpGrantId.ToString("D"), operation.AuthorizationPolicyId, operation.AuthorizationPolicyVersion,
            operation.AuthorizationPolicyHash, operation.CommandCorrelationId.ToString("D"),
            operation.RecordedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture), operation.TargetBinding)));

    internal static string ProductionOutboxCorrectionOperationBinding(OutboxCorrectionMutation operation) =>
        CorrectionOperationBinding(operation.DeliveryId, operation.SourceDeliveryId, operation.Reason,
            Convert.ToHexString(SHA256.HashData(operation.Payload.Span)), operation.ActorPrincipalId,
            operation.ActorSessionId, operation.StepUpGrantId, operation.AuthorizationPolicyId,
            operation.AuthorizationPolicyVersion, operation.AuthorizationPolicyHash, operation.CommandCorrelationId,
            operation.RecordedAtUtc, operation.TargetBinding);

    private static string CorrectionOperationBinding(Guid id, Guid sourceId, string reason, string payloadHash,
        Guid actor, Guid session, Guid grant, string policyId, string policyVersion, string policyHash,
        Guid correlation, DateTimeOffset recordedAt, string target) => Convert.ToHexString(SHA256.HashData(
        AuditCanonical.Encode("ProductionOutboxCorrectionMutationV1", id.ToString("D"), sourceId.ToString("D"),
            reason, payloadHash, actor.ToString("D"), session.ToString("D"), grant.ToString("D"),
            policyId, policyVersion, policyHash, correlation.ToString("D"),
            recordedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), target)));

    private static string ProductionOutboxRecoveryOperationBinding(ProductionOutboxRecoveryStoredRow row) =>
        ProductionOutboxRecoveryOperationBinding(new OutboxRecoveryMutation(row.RecoveryId, row.DeliveryId,
            row.GrantedAttempts, row.Reason, row.ActorPrincipalId, row.ActorSessionId, row.StepUpGrantId,
            row.AuthorizationPolicyId, row.AuthorizationPolicyVersion, row.AuthorizationPolicyHash,
            row.CommandCorrelationId, row.RecordedAtUtc, row.TargetBinding));

    private static string ProductionOutboxCorrectionOperationBinding(ProductionOutboxCorrectionStoredRow row) =>
        CorrectionOperationBinding(row.DeliveryId, row.SourceDeliveryId, row.Reason, row.PayloadHash,
            row.ActorPrincipalId, row.ActorSessionId, row.StepUpGrantId, row.AuthorizationPolicyId,
            row.AuthorizationPolicyVersion, row.AuthorizationPolicyHash, row.CommandCorrelationId,
            row.RecordedAtUtc, row.TargetBinding);
}
