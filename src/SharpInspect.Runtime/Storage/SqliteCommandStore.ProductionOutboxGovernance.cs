using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The fresh, transaction-local state the governed outbox mutation callback may trust: the
/// declared recovery extension, the immutable deliveries/recovery/correction rows and the
/// re-derived obligation states. It is read after BEGIN IMMEDIATE on the one writer connection.
/// </summary>
internal sealed record OutboxGovernanceCommandState(bool Enabled, ProductionOutboxStoreOptions? Options,
    ProductionOutboxRecoveryOptions? Recovery, IReadOnlyList<ProductionOutboxStoredDelivery> Deliveries,
    IReadOnlyList<SqliteCommandStore.ProductionOutboxRecoveryStoredRow> Recoveries,
    IReadOnlyList<SqliteCommandStore.ProductionOutboxCorrectionStoredRow> Corrections,
    IReadOnlyDictionary<Guid, ProductionOutboxWorkState> States);

/// <summary>
/// One accepted governed outbox operation handed to the same transaction that already appended
/// the identity authorization event and the command fact. The mutation carries the exact frozen
/// values the store must persist and never rebuilds them from current state.
/// </summary>
internal sealed record OutboxRecoveryMutation(Guid RecoveryId, Guid DeliveryId, int GrantedAttempts,
    string Reason, Guid ActorPrincipalId, Guid ActorSessionId, Guid StepUpGrantId,
    string AuthorizationPolicyId, string AuthorizationPolicyVersion, string AuthorizationPolicyHash,
    Guid CommandCorrelationId, DateTimeOffset RecordedAtUtc, string TargetBinding);

internal sealed record OutboxCorrectionMutation(Guid DeliveryId, Guid SourceDeliveryId, string Reason,
    ReadOnlyMemory<byte> Payload, Guid ActorPrincipalId, Guid ActorSessionId, Guid StepUpGrantId,
    string AuthorizationPolicyId, string AuthorizationPolicyVersion, string AuthorizationPolicyHash,
    Guid CommandCorrelationId, DateTimeOffset RecordedAtUtc, string TargetBinding);

internal sealed partial class SqliteCommandStore
{
    /// <summary>
    /// Runs one governed outbox command against the fresh identity and outbox state inside the
    /// single writer transaction. The callback receives the exact state the transaction will
    /// mutate, so a stale caller value can never be reinterpreted into an accepted operation.
    /// </summary>
    internal ValueTask<IdentityWriteResult> UpdateOutboxGovernanceCommandAsync(RuntimeCommand command,
        Func<IdentityAuthorityState, OutboxGovernanceCommandState, bool, IdentityUpdate> update,
        CancellationToken cancellationToken, StoreDeadline deadline) =>
        EnqueueIdentityAsync(new IdentityWork(command, update), cancellationToken, deadline);

    internal OutboxGovernanceCommandState ReadOutboxGovernanceCommandState(sqlite3 database,
        RuntimeCommand command, StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command is not (RecoverOutboxDeliveryCommand or CreateCorrectiveOutboxDeliveryCommand))
            return new(false, null, null, Array.Empty<ProductionOutboxStoredDelivery>(),
                Array.Empty<ProductionOutboxRecoveryStoredRow>(),
                Array.Empty<ProductionOutboxCorrectionStoredRow>(),
                new Dictionary<Guid, ProductionOutboxWorkState>());
        if (_options.Outbox is not { } options)
            throw new InvalidOperationException("OutboxGovernanceConfigurationRequired");
        options.Validate();
        var recovery = options.ManualRecovery ??
            throw new InvalidOperationException("OutboxGovernanceConfigurationRequired");
        recovery.Validate();
        if (AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is not
            (ProductionOutboxRecoveryOptions.SchemaVersion or EvidenceReconciliationStoreOptions.SchemaVersion or TraceStorageRetentionOptions.SchemaVersion))
            throw new InvalidOperationException("OutboxGovernanceGovernedMigrationRequired");
        RequireConfiguredEvidenceReconciliation(database, _options, deadline);
        RequireConfiguredProductionOutbox(database, options, deadline);
        var deliveries = ReadProductionOutboxDeliveries(database, options, deadline);
        var recoveries = ReadProductionOutboxRecoveryRows(database, options, recovery, deadline);
        var corrections = ReadProductionOutboxCorrectionRows(database, options, recovery, deadline);
        var states = ReadProductionOutboxObligationStates(database, options, deadline)
            .ToDictionary(value => value.Delivery.DeliveryId);
        return new(true, options, recovery, deliveries, recoveries, corrections, states);
    }

    /// <summary>
    /// Persists one accepted governed operation in the identity transaction: the exact immutable
    /// recovery/correction rows, the derived work projection and the future attempt reservation.
    /// The caller's command fact and authorization event were appended in this same transaction.
    /// </summary>
    private void AppendOutboxGovernanceIdentityMutation(sqlite3 database, IdentityUpdate update,
        OutboxGovernanceCommandState state, RuntimeCommand command, IdentityWork work, StoreDeadline deadline)
    {
        if (update.OutboxRecovery is { } recovery)
            AppendOutboxRecoveryIdentityMutation(database, update, state, recovery, command, work, deadline);
        else if (update.OutboxCorrection is { } correction)
            AppendOutboxCorrectionIdentityMutation(database, update, state, correction, command, work, deadline);
        else
            throw new InvalidOperationException("OutboxGovernanceMutationMissing");
    }

    private void AppendOutboxRecoveryIdentityMutation(sqlite3 database, IdentityUpdate update,
        OutboxGovernanceCommandState state, OutboxRecoveryMutation recovery, RuntimeCommand command,
        IdentityWork work, StoreDeadline deadline)
    {
        var options = state.Options ?? throw new InvalidOperationException("OutboxGovernanceConfigurationRequired");
        var limits = state.Recovery ?? throw new InvalidOperationException("OutboxGovernanceConfigurationRequired");
        if (command is not RecoverOutboxDeliveryCommand request || update.Events.Count != 1 ||
            update.CommandFacts is not { Count: 1 })
            throw new InvalidOperationException("OutboxGovernanceMutationMismatch");
        var delivery = state.Deliveries.SingleOrDefault(value =>
            value.Delivery.DeliveryId == recovery.DeliveryId) ??
            throw new InvalidOperationException("ProductionOutboxRecoveryDeliveryMissing");
        if (delivery.Delivery.ContentHash != request.ExpectedDeliveryContentHash ||
            delivery.Delivery.Payload is null || recovery.GrantedAttempts != request.RequestedAttempts ||
            recovery.Reason != request.Reason || recovery.ActorPrincipalId != update.Events[0].ActorPrincipalId ||
            recovery.TargetBinding != request.AuthorizationTarget ||
            request.ExpectedStateRevisionHash != ProductionOutboxStateRevision(delivery.Delivery,
                state.States[recovery.DeliveryId].LastEventContentHash, state.Recoveries, state.Corrections) ||
            update.Events[0].RecoverySafetyEvidence != ProductionOutboxRecoveryOperationBinding(recovery))
            throw new InvalidOperationException("OutboxGovernanceMutationMismatch");
        var cumulative = state.Recoveries.Where(value => value.DeliveryId == recovery.DeliveryId)
            .Sum(value => value.GrantedAttempts);
        if (cumulative + recovery.GrantedAttempts > limits.MaximumCumulativeGrantedAttempts ||
            delivery.Delivery.MaximumAttempts + cumulative + recovery.GrantedAttempts >
                ProductionOutboxStoreOptions.MaximumAttemptsHardLimit ||
            state.Recoveries.Count >= limits.MaximumRecoveryOperations)
            throw new InvalidOperationException("OutboxGovernanceCapacityExceeded");
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM production_outbox_recovery;", deadline));
        var commandReference = ReadCommandAuditReference(database, update.CommandFacts[0].EventId, deadline);
        var authorization = ReadOutboxGovernanceAuthorizationReference(database, update.Events[0].EventId, deadline);
        var recordedAt = recovery.RecordedAtUtc.ToUniversalTime();
        var contentHash = ProductionOutboxRecoveryContentHash(recovery.RecoveryId, recovery.DeliveryId,
            recovery.GrantedAttempts, recovery.Reason, recovery.ActorPrincipalId, recovery.ActorSessionId,
            recovery.StepUpGrantId, recovery.AuthorizationPolicyId, recovery.AuthorizationPolicyVersion,
            recovery.AuthorizationPolicyHash, recovery.CommandCorrelationId, update.CommandFacts[0].EventId,
            commandReference.Sequence, commandReference.Hash ?? string.Empty, update.Events[0].EventId,
            authorization.Sequence, authorization.Hash, recordedAt, recovery.TargetBinding);
        AuditChainDatabase.Execute(database, @"INSERT INTO production_outbox_recovery(
            Position,RecoveryId,DeliveryId,GrantedAttempts,Reason,ActorPrincipalId,ActorSessionId,StepUpGrantId,
            AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,CommandCorrelationId,
            CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,
            AuthorizationAuditHash,RecordedAtUtc,TargetBinding,ContentHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            ProductionOutboxNumber(position), recovery.RecoveryId.ToString("D"),
            recovery.DeliveryId.ToString("D"), ProductionOutboxNumber(recovery.GrantedAttempts), recovery.Reason,
            recovery.ActorPrincipalId.ToString("D"), recovery.ActorSessionId.ToString("D"),
            recovery.StepUpGrantId.ToString("D"), recovery.AuthorizationPolicyId,
            recovery.AuthorizationPolicyVersion, recovery.AuthorizationPolicyHash,
            recovery.CommandCorrelationId.ToString("D"), update.CommandFacts[0].EventId.ToString("D"),
            ProductionOutboxNumber(commandReference.Sequence), commandReference.Hash ?? string.Empty,
            update.Events[0].EventId.ToString("D"), ProductionOutboxNumber(authorization.Sequence),
            authorization.Hash, recordedAt.ToString("O", CultureInfo.InvariantCulture), recovery.TargetBinding,
            contentHash);
        var events = ReadProductionOutboxRows(database, options, deadline)
            .Where(value => value.Event.DeliveryId == recovery.DeliveryId)
            .Select(value => value.Event).OrderBy(value => value.Position).ToArray();
        UpsertProductionOutboxWork(database, delivery.Delivery, events, deadline);
        var total = cumulative + recovery.GrantedAttempts;
        work.Result = new ProductionOutboxRecoveryResult(new RuntimeCommandOutcome(command.CorrelationId,
            CommandDisposition.Accepted, "OutboxRecoveryAuthorized", AuditPersistence.Persisted,
            update.CommandFacts[0].AttemptId), recovery.RecoveryId, recovery.DeliveryId,
            recovery.GrantedAttempts, total, recovery.Reason);
    }

    private void AppendOutboxCorrectionIdentityMutation(sqlite3 database, IdentityUpdate update,
        OutboxGovernanceCommandState state, OutboxCorrectionMutation correction, RuntimeCommand command,
        IdentityWork work, StoreDeadline deadline)
    {
        var options = state.Options ?? throw new InvalidOperationException("OutboxGovernanceConfigurationRequired");
        var limits = state.Recovery ?? throw new InvalidOperationException("OutboxGovernanceConfigurationRequired");
        if (command is not CreateCorrectiveOutboxDeliveryCommand request || update.Events.Count != 1 ||
            update.CommandFacts is not { Count: 1 })
            throw new InvalidOperationException("OutboxGovernanceMutationMismatch");
        var source = state.Deliveries.SingleOrDefault(value =>
            value.Delivery.DeliveryId == correction.SourceDeliveryId) ??
            throw new InvalidOperationException("ProductionOutboxCorrectionSourceMissing");
        if (source.Delivery.ContentHash != request.ExpectedSourceDeliveryContentHash ||
            correction.TargetBinding != request.AuthorizationTarget ||
            request.ExpectedStateRevisionHash != ProductionOutboxStateRevision(source.Delivery,
                state.States[correction.SourceDeliveryId].LastEventContentHash, state.Recoveries, state.Corrections) ||
            update.Events[0].RecoverySafetyEvidence != ProductionOutboxCorrectionOperationBinding(correction) ||
            !request.PayloadBytes.Span.SequenceEqual(correction.Payload.Span) ||
            request.ContentType != source.Delivery.Route.ContentType)
            throw new InvalidOperationException("OutboxGovernanceMutationMismatch");
        var payload = correction.Payload.ToArray();
        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var recordedAt = correction.RecordedAtUtc.ToUniversalTime();
        var eventPosition = NextProductionOutboxEventPosition(database, deadline);
        var deliveryPosition = NextProductionOutboxDeliveryPosition(database, deadline);
        var existingReserve = ReadProductionOutboxReserveRows(database, deadline).Events;
        var futureReserve = checked(existingReserve +
            ProductionOutboxStoreOptions.ReserveEventsPerAttempt * (long)source.Delivery.MaximumAttempts);
        var eventCount = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_events;", deadline);
        var deliveryCount = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_deliveries;", deadline);
        var preCore = ReadProductionOutboxPreCoreReserve(database, options, deadline);
        AuditChainDatabase.Require(checked(eventCount + deliveryCount + 2L + futureReserve + preCore.Rows) <=
            options.MaximumEvents, "OutboxGovernanceEntryCapacityExceeded");
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        var storedPayloadBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(PayloadBase64)),0) FROM production_outbox_deliveries;", deadline);
        var maximumStored = ProductionOutboxStoreOptions.MaximumStoredReceiptBytes;
        AuditChainDatabase.Require(checked(usedBytes + storedPayloadBytes +
            ProductionInspectionStoredPayloadBytes(payload.Length) + futureReserve * maximumStored +
            preCore.PayloadBytes) <= options.MaximumTotalBytes, "OutboxGovernanceTotalCapacityExceeded");
        var corrective = new OutboxDelivery(correction.DeliveryId, source.Delivery.InspectionId,
            source.Delivery.CoreHash, source.Delivery.Route,
            new OutboxPayloadSnapshot(source.Delivery.Route.PayloadContract,
                source.Delivery.Route.ContentType, payload), null, recordedAt,
            source.Delivery.MaximumAttempts);
        var kind = OutboxEventKind.Created;
        var bindings = ProductionOutboxBindings(eventPosition, corrective, options.RouteSetHash, 1, kind,
            null, null, null, recordedAt, "ProductionOutboxCorrectiveDelivery", null, null, null,
            payloadHash, corrective.MaximumAttempts);
        var audit = AuditChainDatabase.AppendProductionOutboxEvent(database, _policy!, _signingKey!,
            kind, ProductionOutboxStorageCodec.EncodeAuditBinding(bindings), options, futureReserve, deadline);
        var persisted = PersistedProductionOutboxEvent(bindings, null, audit.Sequence, audit.Hash);
        InsertProductionOutboxDeliveryRow(database, deliveryPosition, corrective, options.RouteSetHash,
            persisted.EventId, audit.Sequence, audit.Hash, deadline);
        InsertProductionOutboxEventRow(database, persisted, null, deadline);
        UpsertProductionOutboxWork(database, corrective, new[] { persisted }, deadline);
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM production_outbox_corrections;", deadline));
        var commandReference = ReadCommandAuditReference(database, update.CommandFacts[0].EventId, deadline);
        var authorization = ReadOutboxGovernanceAuthorizationReference(database, update.Events[0].EventId, deadline);
        var contentHash = ProductionOutboxCorrectionContentHash(correction.DeliveryId, correction.SourceDeliveryId,
            correction.Reason, payloadHash, correction.ActorPrincipalId, correction.ActorSessionId,
            correction.StepUpGrantId, correction.AuthorizationPolicyId, correction.AuthorizationPolicyVersion,
            correction.AuthorizationPolicyHash, correction.CommandCorrelationId, update.CommandFacts[0].EventId,
            commandReference.Sequence, commandReference.Hash ?? string.Empty, update.Events[0].EventId,
            authorization.Sequence, authorization.Hash, recordedAt, correction.TargetBinding);
        AuditChainDatabase.Execute(database, @"INSERT INTO production_outbox_corrections(
            Position,DeliveryId,SourceDeliveryId,Reason,PayloadHash,ActorPrincipalId,ActorSessionId,StepUpGrantId,
            AuthorizationPolicyId,AuthorizationPolicyVersion,AuthorizationPolicyHash,CommandCorrelationId,
            CommandEventId,CommandAuditSequence,CommandAuditHash,AuthorizationEventId,AuthorizationAuditSequence,
            AuthorizationAuditHash,RecordedAtUtc,TargetBinding,ContentHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            ProductionOutboxNumber(position), correction.DeliveryId.ToString("D"),
            correction.SourceDeliveryId.ToString("D"), correction.Reason, payloadHash,
            correction.ActorPrincipalId.ToString("D"), correction.ActorSessionId.ToString("D"),
            correction.StepUpGrantId.ToString("D"), correction.AuthorizationPolicyId,
            correction.AuthorizationPolicyVersion, correction.AuthorizationPolicyHash,
            correction.CommandCorrelationId.ToString("D"), update.CommandFacts[0].EventId.ToString("D"),
            ProductionOutboxNumber(commandReference.Sequence), commandReference.Hash ?? string.Empty,
            update.Events[0].EventId.ToString("D"), ProductionOutboxNumber(authorization.Sequence),
            authorization.Hash, recordedAt.ToString("O", CultureInfo.InvariantCulture), correction.TargetBinding,
            contentHash);
        var result = new ProductionOutboxCorrectionResult(new RuntimeCommandOutcome(command.CorrelationId,
            CommandDisposition.Accepted, "OutboxCorrectionCreated", AuditPersistence.Persisted,
            update.CommandFacts[0].AttemptId), correction.DeliveryId, correction.SourceDeliveryId,
            payloadHash, contentHash);
        work.Result = result;
    }

    private static (long Sequence, string Hash) ReadOutboxGovernanceAuthorizationReference(sqlite3 database,
        Guid eventId, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Kind='IdentityEvent' AND Sequence=(
                SELECT MAX(Sequence) FROM audit_entries WHERE Kind='IdentityEvent') LIMIT 2;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Hash: SqliteNative.ColumnText(statement, 1) ?? string.Empty));
        if (rows.Count != 1 || rows[0].Hash.Length != 64)
            throw new InvalidOperationException("OutboxGovernanceAuthorizationAuditMissing");
        var payload = AuditChainDatabase.Read(database, @"SELECT Payload FROM audit_entries
            WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;", deadline,
            statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            ProductionOutboxNumber(rows[0].Sequence));
        if (payload.Count != 1)
            throw new InvalidOperationException("OutboxGovernanceAuthorizationAuditMissing");
        var decoded = DecodeContractIdentityFields(Convert.FromBase64String(payload[0]));
        if (decoded.Length != 49 || decoded[1] != eventId.ToString("D"))
            throw new InvalidOperationException("OutboxGovernanceAuthorizationAuditMismatch");
        return rows[0];
    }

    /// <summary>
    /// The bounded read model of the schema-37 recovery/correction facts. Every row is verified
    /// against the exact encoded payload and the execution of this method never mutates state.
    /// </summary>
    internal static (IReadOnlyList<ProductionOutboxRecoveryStoredRow> Recoveries,
        IReadOnlyList<ProductionOutboxCorrectionStoredRow> Corrections) ReadProductionOutboxGovernanceRows(
        sqlite3 database, ProductionOutboxStoreOptions outbox, ProductionOutboxRecoveryOptions recovery,
        StoreDeadline deadline) =>
        (ReadProductionOutboxRecoveryRows(database, outbox, recovery, deadline),
            ReadProductionOutboxCorrectionRows(database, outbox, recovery, deadline));
}
