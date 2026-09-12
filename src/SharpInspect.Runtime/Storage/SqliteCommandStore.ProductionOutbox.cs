using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Outbox;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record OutboxAttemptStartRequest(Guid DeliveryId, Guid AttemptId, int AttemptNumber,
    Guid RuntimeEpoch, DateTimeOffset RecordedAtUtc, string ConnectionBindingHash,
    Func<string?>? FinalGuard = null);

internal sealed record OutboxFailureRequest(Guid DeliveryId, Guid AttemptId, Guid RuntimeEpoch,
    DateTimeOffset RecordedAtUtc, string ReasonCode, OutboxFailureCategory Category,
    DateTimeOffset? RetryAfterUtc, Func<string?>? FinalGuard = null);

internal sealed record OutboxSuccessRequest(Guid DeliveryId, Guid RuntimeEpoch,
    DateTimeOffset RecordedAtUtc, OutboxAcceptanceVerifier.VerifiedAcceptanceClaim Claim,
    Func<string?>? FinalGuard = null);

internal sealed record OutboxWriteResult(bool Committed, string ReasonCode,
    ProductionOutboxEvent? Event = null, OutboxBacklogSnapshot? Backlog = null);

internal sealed class ProductionOutboxWork
{
    internal ProductionOutboxWork(OutboxAttemptStartRequest request) =>
        Start = request ?? throw new ArgumentNullException(nameof(request));
    internal ProductionOutboxWork(OutboxFailureRequest request) =>
        Failure = request ?? throw new ArgumentNullException(nameof(request));
    internal ProductionOutboxWork(OutboxSuccessRequest request) =>
        Success = request ?? throw new ArgumentNullException(nameof(request));

    internal OutboxAttemptStartRequest? Start { get; }
    internal OutboxFailureRequest? Failure { get; }
    internal OutboxSuccessRequest? Success { get; }
    internal TaskCompletionSource<OutboxWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Schema-36 outbox writer. Every fact is written through the one existing SqliteCommandStore
/// queue and transaction: no second writer connection exists, no file/socket/formatter is
/// opened here, the main-owned verified acceptance claim is verified against the exact
/// persisted attempt and the frozen delivery, and the same Core transaction that inserts the
/// obligations also inserts their pending projection and signed Created events.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    private string? ProductionOutboxAdmissionFailure(sqlite3 database,
        ProductionInspectionAdmission admission, StoreDeadline deadline)
    {
        if (_options.Outbox is not { } options)
            return admission.TracePolicySnapshot.Policy.RequiredRoutes.Count == 0 ? null :
                "ProductionInspectionOutboxConfigurationRequired";
        if (!ProductionOutboxBinding.RoutesMatch(_options, admission.TracePolicySnapshot))
            return "ProductionInspectionOutboxRouteBindingMismatch";
        // The enclosing admission transaction has fully verified the ledgers. Read its
        // current backlog here so a delayed Runtime projection cannot admit a new Trigger.
        var backlog = ProductionOutboxBinding.CompleteBacklog(options,
            ReadProductionOutboxBacklogSnapshot(database, AuditChainDatabase.Tail(database, deadline).Sequence, deadline));
        return ProductionOutboxBinding.RequiredBacklogFailure(admission.TracePolicySnapshot,
            backlog, DateTimeOffset.UtcNow);
    }

    private sealed record ProductionOutboxDeliveryFacts(ProductionOutboxStoredDelivery Stored,
        IReadOnlyList<ProductionOutboxEvent> Events, ProductionOutboxWorkState State);

    internal ValueTask<OutboxWriteResult> BeginOutboxAttemptAsync(OutboxAttemptStartRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default) =>
        EnqueueProductionOutboxAsync(new ProductionOutboxWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    internal ValueTask<OutboxWriteResult> AppendOutboxOutcomeAsync(OutboxFailureRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default) =>
        EnqueueProductionOutboxAsync(new ProductionOutboxWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    internal ValueTask<OutboxWriteResult> AppendOutboxOutcomeAsync(OutboxSuccessRequest request,
        StoreDeadline deadline, CancellationToken cancellationToken = default) =>
        EnqueueProductionOutboxAsync(new ProductionOutboxWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    private async ValueTask<OutboxWriteResult> EnqueueProductionOutboxAsync(ProductionOutboxWork work,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ProductionOutboxEnabled || Volatile.Read(ref _disposed) != 0 || _queue is null ||
            _queueSlots is null || _worker.IsCompleted)
            return new(false, ProductionOutboxEnabled ? "ProductionOutboxUnavailable" :
                "ProductionOutboxConfigurationRequired");
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "ProductionOutboxCommitDeadlineExceeded");
        var queued = new WriteRequest(null, deadline, ProductionOutbox: work);
        if (!_queue.Writer.TryWrite(queued))
        {
            _queueSlots.Release();
            return new(false, Volatile.Read(ref _disposed) != 0 ? "TraceStoreDisposed" :
                "ProductionOutboxUnavailable");
        }
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendProductionOutboxCore(sqlite3 database, ProductionOutboxWork work,
        StoreDeadline deadline)
    {
        if (Integrity?.State == AuditIntegrityState.Faulted)
            return ProductionOutboxRejected(work, Integrity.ReasonCode);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return ProductionOutboxRejected(work, "TraceStoreWalLimit");
        if (work.Start is not null)
            return AppendProductionOutboxAttemptStart(database, work, deadline);
        if (work.Failure is not null)
            return AppendProductionOutboxFailure(database, work, deadline);
        if (work.Success is not null)
            return AppendProductionOutboxSuccess(database, work, deadline);
        throw new InvalidOperationException("ProductionOutboxWriteInvalid");
    }

    private StoreWriteResult AppendProductionOutboxAttemptStart(sqlite3 database,
        ProductionOutboxWork work, StoreDeadline deadline)
    {
        var request = work.Start ?? throw new InvalidOperationException("ProductionOutboxWriteInvalid");
        var options = _options.Outbox ?? throw new InvalidOperationException(
            "ProductionOutboxConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var transactionStarted = false;
        var committed = false;
        try
        {
            RequireProductionOutboxAttemptIdentity(request);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            RequireConfiguredProductionOutbox(database, options, deadline);
            AuditChainDatabase.RequireFullProductionOutboxVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadProductionOutboxDeliveryFacts(database, options, request.DeliveryId, deadline);
            var connectionHash = NormalizedProductionOutboxHash(request.ConnectionBindingHash);
            var recordedAt = request.RecordedAtUtc.ToUniversalTime();
            var existing = facts.Events.SingleOrDefault(value =>
                value.Kind == OutboxEventKind.AttemptStarted && value.AttemptId == request.AttemptId);
            if (existing is not null)
                return existing.AttemptNumber == request.AttemptNumber &&
                    existing.RuntimeEpoch == request.RuntimeEpoch &&
                    existing.ConnectionBindingHash == connectionHash &&
                    existing.RecordedAtUtc == recordedAt
                    ? ProductionOutboxIdempotent(work, existing, database, deadline)
                    : ProductionOutboxRejected(work, "ProductionOutboxEventConflict");
            var state = facts.State;
            if (state.State == OutboxDeliveryState.Succeeded)
                return ProductionOutboxRejected(work, "ProductionOutboxAlreadySucceeded");
            if (state.ActiveAttemptId is not null)
                return ProductionOutboxRejected(work, "ProductionOutboxAttemptAlreadyActive");
            if (state.PermanentBlock)
                return ProductionOutboxRejected(work, "ProductionOutboxPermanentBlockRecorded");
            if (!state.RetryEligible)
                return ProductionOutboxRejected(work, "ProductionOutboxAttemptBudgetExhausted");
            if (state.RetryAfterUtc is { } windowOpen && recordedAt < windowOpen)
                return ProductionOutboxRejected(work, "ProductionOutboxRetryWindowOpen");
            if (request.AttemptNumber != state.NextAttemptNumber)
                return ProductionOutboxRejected(work, "ProductionOutboxAttemptNumberInvalid");
            if (request.AttemptNumber > facts.Stored.Delivery.MaximumAttempts)
                return ProductionOutboxRejected(work, "ProductionOutboxAttemptBudgetExhausted");
            if (facts.Stored.Delivery.Payload is null)
                return ProductionOutboxRejected(work, "ProductionOutboxPreparationFailureTerminal");
            var reserve = ReadProductionOutboxReserveRows(database, deadline).Events;
            // The reserved terminal fact of this new active attempt is still owed, while the
            // AttemptStarted fact itself is the write being admitted.
            var futureReserve = Math.Max(0, reserve - 1);
            var eventPosition = NextProductionOutboxEventPosition(database, deadline);
            var aggregate = facts.Events[^1].AggregateSequence + 1;
            var kind = OutboxEventKind.AttemptStarted;
            var bindings = ProductionOutboxBindings(eventPosition, facts.Stored, options.RouteSetHash, aggregate, kind,
                request.AttemptId, request.AttemptNumber, request.RuntimeEpoch, recordedAt,
                "ProductionOutboxAttemptStarted", null, null, connectionHash);
            EnsureProductionOutboxEventCapacity(database, options, futureReserve, deadline);
            var auditPayload = ProductionOutboxStorageCodec.EncodeAuditBinding(bindings);
            var audit = AuditChainDatabase.AppendProductionOutboxEvent(database, policy, signingKey,
                kind, auditPayload, options, futureReserve, deadline);
            var persisted = PersistedProductionOutboxEvent(bindings, null, audit.Sequence, audit.Hash);
            InsertProductionOutboxEventRow(database, persisted, null, deadline);
            UpsertProductionOutboxWork(database, facts.Stored.Delivery,
                facts.Events.Append(persisted).ToArray(), deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ProductionOutboxRejected(work, guardReason);
            var backlog = ReadProductionOutboxBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            PublishProductionOutboxIntegrity(policy, audit.Sequence);
            work.Completion.TrySetResult(new(true, "ProductionOutboxAttemptStarted", persisted, backlog));
            return new(true, "ProductionOutboxAttemptStarted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ProductionOutboxRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ProductionOutbox",
            StringComparison.Ordinal))
        { return ProductionOutboxRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ProductionOutboxRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ProductionOutboxAttemptFailed")); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendProductionOutboxFailure(sqlite3 database, ProductionOutboxWork work,
        StoreDeadline deadline)
    {
        var request = work.Failure ?? throw new InvalidOperationException("ProductionOutboxWriteInvalid");
        var options = _options.Outbox ?? throw new InvalidOperationException(
            "ProductionOutboxConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var transactionStarted = false;
        var committed = false;
        try
        {
            RequireProductionOutboxFailureIdentity(request);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            RequireConfiguredProductionOutbox(database, options, deadline);
            AuditChainDatabase.RequireFullProductionOutboxVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadProductionOutboxDeliveryFacts(database, options, request.DeliveryId, deadline);
            var recordedAt = request.RecordedAtUtc.ToUniversalTime();
            var retryAfter = request.RetryAfterUtc?.ToUniversalTime();
            var existing = facts.Events.SingleOrDefault(value =>
                value.Kind == OutboxEventKind.AttemptFailed && value.AttemptId == request.AttemptId);
            if (existing is not null)
                return existing.ReasonCode == request.ReasonCode &&
                    existing.FailureCategory == request.Category &&
                    existing.RetryAfterUtc == retryAfter && existing.RuntimeEpoch == request.RuntimeEpoch
                    ? ProductionOutboxIdempotent(work, existing, database, deadline)
                    : ProductionOutboxRejected(work, "ProductionOutboxEventConflict");
            var state = facts.State;
            if (state.State == OutboxDeliveryState.Succeeded)
                return ProductionOutboxRejected(work, "ProductionOutboxAlreadySucceeded");
            if (state.ActiveAttemptId != request.AttemptId || state.ActiveRuntimeEpoch != request.RuntimeEpoch)
                return ProductionOutboxRejected(work, "ProductionOutboxAttemptBindingMismatch");
            if (request.Category != OutboxFailureCategory.Permanent &&
                retryAfter is { } boundedRetry && boundedRetry - recordedAt > options.MaximumRetryDelay)
                return ProductionOutboxRejected(work, "ProductionOutboxRetryDelayInvalid");
            var reserve = ReadProductionOutboxReserveRows(database, deadline).Events;
            // A permanent outcome ends this delivery's automatic attempt budget.
            var ownRemaining = Math.Max(0, 2L * facts.Stored.Delivery.MaximumAttempts -
                facts.Events.Count(entry => entry.Kind != OutboxEventKind.Created));
            var futureReserve = Math.Max(0, reserve -
                (request.Category == OutboxFailureCategory.Permanent ? ownRemaining : 1));
            var eventPosition = NextProductionOutboxEventPosition(database, deadline);
            var aggregate = facts.Events[^1].AggregateSequence + 1;
            var kind = OutboxEventKind.AttemptFailed;
            var bindings = ProductionOutboxBindings(eventPosition, facts.Stored, options.RouteSetHash, aggregate, kind,
                request.AttemptId, facts.State.AttemptCount, request.RuntimeEpoch, recordedAt,
                request.ReasonCode, request.Category, retryAfter, null);
            EnsureProductionOutboxEventCapacity(database, options, futureReserve, deadline);
            var auditPayload = ProductionOutboxStorageCodec.EncodeAuditBinding(bindings);
            var audit = AuditChainDatabase.AppendProductionOutboxEvent(database, policy, signingKey,
                kind, auditPayload, options, futureReserve, deadline);
            var persisted = PersistedProductionOutboxEvent(bindings, null, audit.Sequence, audit.Hash);
            InsertProductionOutboxEventRow(database, persisted, null, deadline);
            UpsertProductionOutboxWork(database, facts.Stored.Delivery,
                facts.Events.Append(persisted).ToArray(), deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ProductionOutboxRejected(work, guardReason);
            var backlog = ReadProductionOutboxBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            PublishProductionOutboxIntegrity(policy, audit.Sequence);
            work.Completion.TrySetResult(new(true, "ProductionOutboxAttemptFailed", persisted, backlog));
            return new(true, "ProductionOutboxAttemptFailed");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ProductionOutboxRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ProductionOutbox",
            StringComparison.Ordinal))
        { return ProductionOutboxRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ProductionOutboxRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ProductionOutboxFailureFailed")); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendProductionOutboxSuccess(sqlite3 database, ProductionOutboxWork work,
        StoreDeadline deadline)
    {
        var request = work.Success ?? throw new InvalidOperationException("ProductionOutboxWriteInvalid");
        var options = _options.Outbox ?? throw new InvalidOperationException(
            "ProductionOutboxConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var transactionStarted = false;
        var committed = false;
        try
        {
            var claim = request.Claim ?? throw new InvalidOperationException(
                "ProductionOutboxAcceptanceClaimRequired");
            if (request.DeliveryId == Guid.Empty || request.DeliveryId != claim.DeliveryId ||
                request.RuntimeEpoch == Guid.Empty || request.RuntimeEpoch != claim.RuntimeEpoch ||
                request.RecordedAtUtc == default || request.RecordedAtUtc.Offset != TimeSpan.Zero)
                return ProductionOutboxRejected(work, "ProductionOutboxRequestIdentityInvalid");
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            RequireConfiguredProductionOutbox(database, options, deadline);
            AuditChainDatabase.RequireFullProductionOutboxVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadProductionOutboxDeliveryFacts(database, options, request.DeliveryId, deadline);
            var delivery = facts.Stored.Delivery;
            if (delivery.Payload is null)
                return ProductionOutboxRejected(work, "ProductionOutboxPreparationFailureTerminal");
            var attemptId = claim.AttemptId;
            var existing = facts.Events.SingleOrDefault(value => value.Kind == OutboxEventKind.Succeeded);
            var recordedAt = request.RecordedAtUtc.ToUniversalTime();
            if (existing is not null)
            {
                var sameClaim = existing.AttemptId == attemptId &&
                    existing.ReceiptId == claim.ReceiptId && existing.ReceiptHash == claim.ReceiptHash;
                return sameClaim
                    ? ProductionOutboxIdempotent(work, existing, database, deadline)
                    : ProductionOutboxRejected(work, "ProductionOutboxAlreadySucceeded");
            }
            if (facts.State.ActiveAttemptId != attemptId ||
                facts.State.ActiveRuntimeEpoch != request.RuntimeEpoch)
                return ProductionOutboxRejected(work, "ProductionOutboxAttemptBindingMismatch");
            if (!claim.Matches(delivery, request.RuntimeEpoch, attemptId) ||
                claim.PayloadHash != delivery.Payload.ContentHash ||
                claim.RouteHash != delivery.Route.ContentHash)
                return ProductionOutboxRejected(work, "ProductionOutboxAcceptanceClaimMismatch");
            if (!claim.IsCurrent)
                return ProductionOutboxRejected(work, "ProductionOutboxAcceptanceClaimRetired");
            var receipt = claim.CopyReceipt() ?? throw new InvalidOperationException(
                "ProductionOutboxAcceptanceClaimMismatch");
            if (receipt.Length is < 1 or > ProductionOutboxStoreOptions.MaximumReceiptBytes)
                return ProductionOutboxRejected(work, "ProductionOutboxReceiptInvalid");
            try
            {
                var verifiedReceipt = OutboxAcceptanceVerifier.VerifyReceipt(delivery, receipt);
                if (verifiedReceipt.ReceiptId != claim.ReceiptId ||
                    verifiedReceipt.ReceiptHash != claim.ReceiptHash ||
                    verifiedReceipt.AcceptedAtUtc != claim.AcceptedAtUtc)
                    return ProductionOutboxRejected(work, "ProductionOutboxReceiptBindingMismatch");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return ProductionOutboxRejected(work, "ProductionOutboxReceiptVerificationFailed"); }
            var receiptContentHash = ProductionOutboxStorageCodec.Hash(receipt);
            var reserve = ReadProductionOutboxReserveRows(database, deadline).Events;
            var remaining = RemainingProductionOutboxReserve(delivery, facts.Events);
            var futureReserve = Math.Max(0, reserve - remaining);
            var eventPosition = NextProductionOutboxEventPosition(database, deadline);
            var aggregate = facts.Events[^1].AggregateSequence + 1;
            var kind = OutboxEventKind.Succeeded;
            var bindings = ProductionOutboxBindings(eventPosition, facts.Stored, options.RouteSetHash, aggregate, kind,
                attemptId, facts.State.AttemptCount, request.RuntimeEpoch, recordedAt,
                "ProductionOutboxSucceeded", null, null, null);
            bindings = bindings with { ReceiptId = claim.ReceiptId, ReceiptHash = claim.ReceiptHash,
                AcceptedAtUtc = claim.AcceptedAtUtc, ReceiptContentHash = receiptContentHash,
                ContentHash = string.Empty };
            bindings = bindings with
            { ContentHash = ProductionOutboxStorageCodec.EventContentHash(bindings) };
            EnsureProductionOutboxEventCapacity(database, options, futureReserve, deadline);
            var auditPayload = ProductionOutboxStorageCodec.EncodeAuditBinding(bindings);
            var audit = AuditChainDatabase.AppendProductionOutboxEvent(database, policy, signingKey,
                kind, auditPayload, options, futureReserve, deadline);
            var persisted = PersistedProductionOutboxEvent(bindings, receipt, audit.Sequence, audit.Hash);
            InsertProductionOutboxEventRow(database, persisted, receipt, deadline);
            UpsertProductionOutboxWork(database, delivery, facts.Events.Append(persisted).ToArray(), deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ProductionOutboxRejected(work, guardReason);
            var backlog = ReadProductionOutboxBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            // The final fence has accepted this attempt. Claim consumption and owner
            // retirement share one linearization point; an expired/retired owner must
            // roll back every candidate success row before the durable COMMIT.
            if (!claim.TryConsume())
                return ProductionOutboxRejected(work, "ProductionOutboxAcceptanceClaimRetired");
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            PublishProductionOutboxIntegrity(policy, audit.Sequence);
            work.Completion.TrySetResult(new(true, "ProductionOutboxSucceeded", persisted, backlog));
            return new(true, "ProductionOutboxSucceeded");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ProductionOutboxRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ProductionOutbox",
            StringComparison.Ordinal))
        { return ProductionOutboxRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ProductionOutboxRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ProductionOutboxSuccessFailed")); }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    /// <summary>
    /// Inserts the complete frozen obligation batch of one production Core inside the same
    /// Core transaction, before the final guard: the immutable deliveries, the derived pending
    /// projections and the signed Created facts. With Outbox configured every configured route
    /// must be present exactly once and a missing payload is only permitted for a BestEffort
    /// preparation failure; a Required route is therefore never silently dropped.
    /// </summary>
    private void InsertProductionOutboxBatch(sqlite3 database, ProductionInspectionCoreWriteRequest request,
        StoreDeadline deadline)
    {
        var options = _options.Outbox;
        var batch = request.OutboxBatch;
        if (options is null)
        {
            AuditChainDatabase.Require(batch is null && request.Core.Admission.TracePolicySnapshot.Policy.RequiredRoutes.Count == 0,
                "ProductionOutboxConfigurationRequired");
            return;
        }
        AuditChainDatabase.Require(ProductionOutboxBinding.RoutesMatch(_options,
            request.Core.Admission.TracePolicySnapshot), "ProductionInspectionOutboxRouteBindingMismatch");
        RequireConfiguredProductionOutbox(database, options, deadline);
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        var coreHash = request.Core.ContentHash;
        if (batch is null)
            throw new InvalidOperationException("ProductionOutboxBatchRequired");
        AuditChainDatabase.Require(batch.CoreHash == coreHash, "ProductionOutboxBatchCoreMismatch");
        AuditChainDatabase.Require(batch.RouteSetHash == options.RouteSetHash,
            "ProductionOutboxBatchRouteSetMismatch");
        var deliveries = batch.Deliveries.ToArray();
        AuditChainDatabase.Require(deliveries.Length == options.Routes.Count,
            "ProductionOutboxBatchIncomplete");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long newPayloadBytes = 0;
        long newReserve = 0;
        foreach (var delivery in deliveries)
        {
            var route = options.Routes.FirstOrDefault(value =>
                string.Equals(value.RouteId, delivery.Route.RouteId, StringComparison.Ordinal)) ??
                throw new InvalidOperationException("ProductionOutboxBatchRouteMismatch");
            AuditChainDatabase.Require(delivery.Route.Version == route.Version &&
                delivery.Route.ContentHash == route.ContentHash &&
                delivery.Route.Criticality == route.Criticality && seen.Add(route.RouteId),
                "ProductionOutboxBatchRouteMismatch");
            AuditChainDatabase.Require(delivery.InspectionId == request.Core.Admission.InspectionId &&
                delivery.CoreHash == coreHash && delivery.MaximumAttempts <= options.MaximumAttempts,
                "ProductionOutboxBatchDeliveryMismatch");
            if (delivery.Payload is { } payload)
            {
                AuditChainDatabase.Require(payload.ByteLength <= route.MaximumPayloadBytes &&
                    payload.ByteLength <= options.MaximumPayloadBytes,
                    "ProductionOutboxBatchPayloadMismatch");
                newPayloadBytes = checked(newPayloadBytes + ProductionInspectionStoredPayloadBytes(payload.ByteLength));
            }
            else
            {
                AuditChainDatabase.Require(delivery.PreparationFailure is not null &&
                    route.Criticality == OutboxRouteCriticality.BestEffort,
                    "ProductionOutboxBatchPreparationInvalid");
            }
            if (delivery.Payload is not null)
                newReserve = checked(newReserve + ProductionOutboxStoreOptions.ReserveEventsPerAttempt *
                    (long)delivery.MaximumAttempts);
        }
        AuditChainDatabase.Require(seen.Count == options.Routes.Count, "ProductionOutboxBatchIncomplete");
        // This cycle already owns its Core row inside this transaction, so it is no longer part
        // of the uncreated pre-Core reserve: the batch rows and its own attempt reserve replace
        // that reservation exactly. Every other cycle still without a Core stays reserved.
        var preCore = ReadProductionOutboxPreCoreReserve(database, options, deadline);
        var existingReserve = ReadProductionOutboxReserveRows(database, deadline).Events;
        var futureReserveAfterBatch = checked(existingReserve + newReserve);
        var eventCount = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_events;", deadline);
        var deliveryCount = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM production_outbox_deliveries;", deadline);
        AuditChainDatabase.Require(checked(eventCount + deliveryCount + 2L * deliveries.Length +
            futureReserveAfterBatch + preCore.Rows) <= options.MaximumEvents,
            "ProductionOutboxEntryCapacityExceeded");
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(ReceiptBase64)),0) FROM production_outbox_events;", deadline);
        var storedPayloadBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(PayloadBase64)),0) FROM production_outbox_deliveries;", deadline);
        var maximumStored = ProductionOutboxStoreOptions.MaximumStoredReceiptBytes;
        AuditChainDatabase.Require(checked(usedBytes + storedPayloadBytes + newPayloadBytes +
            futureReserveAfterBatch * maximumStored + preCore.PayloadBytes) <= options.MaximumTotalBytes,
            "ProductionOutboxTotalCapacityExceeded");
        for (var index = 0; index < deliveries.Length; index++)
        {
            var delivery = deliveries[index];
            var position = NextProductionOutboxDeliveryPosition(database, deadline);
            var eventPosition = NextProductionOutboxEventPosition(database, deadline);
            var kind = OutboxEventKind.Created;
            var reason = delivery.Payload is null ? "ProductionOutboxPreparationFailed" :
                "ProductionOutboxCreated";
            var bindings = ProductionOutboxBindings(eventPosition, delivery, options.RouteSetHash, 1,
                kind, null, null, null, delivery.CreatedAtUtc.ToUniversalTime(), reason, null, null, null,
                delivery.Payload?.ContentHash, delivery.MaximumAttempts);
            var auditPayload = ProductionOutboxStorageCodec.EncodeAuditBinding(bindings);
            var appendReserve = checked(futureReserveAfterBatch + deliveries.Length - index - 1);
            var audit = AuditChainDatabase.AppendProductionOutboxEvent(database, policy, signingKey,
                kind, auditPayload, options, appendReserve, deadline);
            var persisted = PersistedProductionOutboxEvent(bindings, null, audit.Sequence, audit.Hash);
            InsertProductionOutboxDeliveryRow(database, position, delivery, options.RouteSetHash,
                persisted.EventId, audit.Sequence, audit.Hash, deadline);
            InsertProductionOutboxEventRow(database, persisted, null, deadline);
            UpsertProductionOutboxWork(database, delivery, new[] { persisted }, deadline);
        }
    }

    private static void RequireProductionOutboxAttemptIdentity(OutboxAttemptStartRequest request)
    {
        if (request.DeliveryId == Guid.Empty || request.AttemptId == Guid.Empty ||
            request.AttemptNumber < 1 || request.RuntimeEpoch == Guid.Empty ||
            request.RecordedAtUtc == default || request.RecordedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("ProductionOutboxRequestIdentityInvalid");
        _ = NormalizedProductionOutboxHash(request.ConnectionBindingHash);
    }

    private static void RequireProductionOutboxFailureIdentity(OutboxFailureRequest request)
    {
        if (request.DeliveryId == Guid.Empty || request.AttemptId == Guid.Empty ||
            request.RuntimeEpoch == Guid.Empty || request.RecordedAtUtc == default ||
            request.RecordedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("ProductionOutboxRequestIdentityInvalid");
        _ = AlgorithmConfigurationValidation.Identifier(request.ReasonCode, nameof(request.ReasonCode));
        if (!Enum.IsDefined(request.Category))
            throw new InvalidOperationException("ProductionOutboxRequestIdentityInvalid");
        var recordedAt = request.RecordedAtUtc.ToUniversalTime();
        if (request.Category == OutboxFailureCategory.Transient)
        {
            if (request.RetryAfterUtc is not { } retryAfter || retryAfter.Offset != TimeSpan.Zero ||
                retryAfter <= recordedAt)
                throw new InvalidOperationException("ProductionOutboxRetryDelayInvalid");
        }
        else if (request.RetryAfterUtc is { } optionalRetryAfter)
        {
            // A permanent failure can never carry an automatic retry; an unknown outcome may
            // carry the same bounded delay as a transient failure so the replacement attempt
            // is deferred instead of racing the unresolved send.
            if (request.Category == OutboxFailureCategory.Permanent ||
                optionalRetryAfter.Offset != TimeSpan.Zero || optionalRetryAfter <= recordedAt)
                throw new InvalidOperationException("ProductionOutboxRetryDelayInvalid");
        }
    }

    private static string NormalizedProductionOutboxHash(string value)
    {
        var normalized = AlgorithmConfigurationValidation.Hash(value, nameof(value)).ToUpperInvariant();
        return normalized;
    }

    private static ProductionOutboxEventBindings ProductionOutboxBindings(long position,
        ProductionOutboxStoredDelivery stored, string routeSetHash, long aggregate, OutboxEventKind kind,
        Guid? attemptId, int? attemptNumber, Guid? runtimeEpoch, DateTimeOffset recordedAt,
        string reasonCode, OutboxFailureCategory? failureCategory, DateTimeOffset? retryAfterUtc,
        string? connectionBindingHash)
    {
        var delivery = stored.Delivery;
        var bindings = new ProductionOutboxEventBindings(position,
            ProductionOutboxStorageCodec.DeriveEventId(position, delivery.DeliveryId, aggregate, kind,
                attemptId), delivery.DeliveryId, delivery.InspectionId, delivery.CoreHash,
            routeSetHash, delivery.Route.RouteId, delivery.Route.Version,
            delivery.Route.ContentHash, aggregate, kind, attemptId, attemptNumber, runtimeEpoch,
            recordedAt, reasonCode, failureCategory, retryAfterUtc, connectionBindingHash,
            delivery.MaximumAttempts, delivery.Payload?.ContentHash, null, null, null, null, string.Empty);
        return bindings with { ContentHash = ProductionOutboxStorageCodec.EventContentHash(bindings) };
    }

    /// <summary>The Core-transaction form: the batch supplies its own route-set hash.</summary>
    private static ProductionOutboxEventBindings ProductionOutboxBindings(long position,
        OutboxDelivery delivery, string routeSetHash, long aggregate, OutboxEventKind kind,
        Guid? attemptId, int? attemptNumber, Guid? runtimeEpoch, DateTimeOffset recordedAt,
        string reasonCode, OutboxFailureCategory? failureCategory, DateTimeOffset? retryAfterUtc,
        string? connectionBindingHash, string? payloadHash, int maximumAttempts)
    {
        var bindings = new ProductionOutboxEventBindings(position,
            ProductionOutboxStorageCodec.DeriveEventId(position, delivery.DeliveryId, aggregate, kind,
                attemptId), delivery.DeliveryId, delivery.InspectionId, delivery.CoreHash, routeSetHash,
            delivery.Route.RouteId, delivery.Route.Version, delivery.Route.ContentHash, aggregate, kind,
            attemptId, attemptNumber, runtimeEpoch, recordedAt, reasonCode, failureCategory,
            retryAfterUtc, connectionBindingHash, maximumAttempts, payloadHash, null, null, null, null,
            string.Empty);
        return bindings with { ContentHash = ProductionOutboxStorageCodec.EventContentHash(bindings) };
    }

    private static ProductionOutboxEvent PersistedProductionOutboxEvent(
        ProductionOutboxEventBindings bindings, byte[]? receipt, long auditSequence, string auditHash)
    {
        var receiptSpan = receipt is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(receipt);
        return new ProductionOutboxEvent(bindings.Position, bindings.EventId, bindings.DeliveryId,
            bindings.InspectionId, bindings.CoreHash, bindings.RouteSetHash, bindings.RouteId,
            bindings.RouteVersion, bindings.RouteContentHash, bindings.AggregateSequence, bindings.Kind,
            bindings.AttemptId, bindings.AttemptNumber, bindings.RuntimeEpoch, bindings.RecordedAtUtc,
            bindings.ReasonCode, bindings.FailureCategory, bindings.RetryAfterUtc,
            bindings.ConnectionBindingHash, bindings.AttemptBudget, bindings.ReceiptId,
            bindings.ReceiptHash, bindings.AcceptedAtUtc, receiptSpan, bindings.PayloadHash,
            bindings.ContentHash, auditSequence, auditHash);
    }

    private static long RemainingProductionOutboxReserve(OutboxDelivery delivery,
        IReadOnlyList<ProductionOutboxEvent> events)
    {
        var recorded = events.Count(value => value.Kind != OutboxEventKind.Created);
        return Math.Max(0, ProductionOutboxStoreOptions.ReserveEventsPerAttempt *
            (long)delivery.MaximumAttempts - recorded);
    }

    private ProductionOutboxDeliveryFacts ReadProductionOutboxDeliveryFacts(sqlite3 database,
        ProductionOutboxStoreOptions options, Guid deliveryId, StoreDeadline deadline)
    {
        var stored = ReadProductionOutboxDeliveries(database, options, deadline)
            .SingleOrDefault(value => value.Delivery.DeliveryId == deliveryId) ??
            throw new InvalidOperationException("ProductionOutboxDeliveryMissing");
        var events = ReadProductionOutboxRows(database, options, deadline)
            .Where(value => value.Event.DeliveryId == deliveryId)
            .Select(value => value.Event)
            .OrderBy(value => value.Position)
            .ToArray();
        if (events.Length == 0 || events[0].Kind != OutboxEventKind.Created)
            throw new InvalidOperationException("ProductionOutboxCreatedEventMissing");
        return new ProductionOutboxDeliveryFacts(stored, events,
            DeriveProductionOutboxState(stored.Delivery, events));
    }

    private static long NextProductionOutboxDeliveryPosition(sqlite3 database, StoreDeadline deadline) =>
        checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM production_outbox_deliveries;", deadline));

    private static long NextProductionOutboxEventPosition(sqlite3 database, StoreDeadline deadline) =>
        checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM production_outbox_events;", deadline));

    private static void InsertProductionOutboxDeliveryRow(sqlite3 database, long position,
        OutboxDelivery delivery, string routeSetHash, Guid createdEventId, long auditSequence,
        string auditHash, StoreDeadline deadline)
    {
        var route = delivery.Route;
        var payload = delivery.Payload?.CopyBytes();
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_outbox_deliveries(
                Position,DeliveryId,InspectionId,CoreHash,RouteSetHash,RouteId,RouteVersion,
                RouteContentHash,Criticality,DestinationIdentity,PayloadContractId,PayloadContractVersion,
                PayloadContractContentHash,ContentType,ReceiverContractId,ReceiverContractVersion,
                ReceiverContractContentHash,ReceiverPublicKeyBase64,AdapterContractId,AdapterContractVersion,
                AdapterContractContentHash,RouteMaximumPayloadBytes,PayloadBase64,PayloadContentHash,
                PayloadByteLength,PreparationFailure,CreatedAtUtc,MaximumAttempts,ContentHash,CreatedEventId,
                CreatedAuditSequence,CreatedAuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), delivery.DeliveryId.ToString("D"),
            delivery.InspectionId.ToString("D"), delivery.CoreHash, routeSetHash, route.RouteId,
            route.Version, route.ContentHash, ((int)route.Criticality).ToString(CultureInfo.InvariantCulture),
            route.DestinationIdentity, route.PayloadContract.Id, route.PayloadContract.Version,
            route.PayloadContract.ContentHash, route.ContentType, route.ReceiverContract.Id,
            route.ReceiverContract.Version, route.ReceiverContract.ContentHash,
            route.ReceiverPublicKeyBase64, route.AdapterContract.Id, route.AdapterContract.Version,
            route.AdapterContract.ContentHash,
            route.MaximumPayloadBytes.ToString(CultureInfo.InvariantCulture),
            payload is null ? null : Convert.ToBase64String(payload),
            delivery.Payload?.ContentHash,
            delivery.Payload is null ? null : delivery.Payload.ByteLength.ToString(CultureInfo.InvariantCulture),
            delivery.PreparationFailure,
            delivery.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            delivery.MaximumAttempts.ToString(CultureInfo.InvariantCulture), delivery.ContentHash,
            createdEventId.ToString("D"), auditSequence.ToString(CultureInfo.InvariantCulture), auditHash);
    }

    private static void InsertProductionOutboxEventRow(sqlite3 database, ProductionOutboxEvent value,
        byte[]? receipt, StoreDeadline deadline) =>
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_outbox_events(
                Position,EventId,DeliveryId,InspectionId,CoreHash,RouteSetHash,RouteId,RouteVersion,
                RouteContentHash,AggregateSequence,Kind,AttemptId,AttemptNumber,RuntimeEpoch,RecordedAtUtc,
                ReasonCode,FailureCategory,RetryAfterUtc,ConnectionBindingHash,AttemptBudget,PayloadHash,
                ReceiptId,ReceiptHash,AcceptedAtUtc,ReceiptBase64,ReceiptLength,ContentHash,AuditSequence,
                AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            value.Position.ToString(CultureInfo.InvariantCulture), value.EventId.ToString("D"),
            value.DeliveryId.ToString("D"), value.InspectionId.ToString("D"), value.CoreHash,
            value.RouteSetHash, value.RouteId, value.RouteVersion, value.RouteContentHash,
            value.AggregateSequence.ToString(CultureInfo.InvariantCulture), value.Kind.ToString(),
            value.AttemptId?.ToString("D"),
            value.AttemptNumber?.ToString(CultureInfo.InvariantCulture),
            value.RuntimeEpoch?.ToString("D"),
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), value.ReasonCode,
            value.FailureCategory?.ToString(),
            value.RetryAfterUtc?.ToString("O", CultureInfo.InvariantCulture), value.ConnectionBindingHash,
            value.AttemptBudget?.ToString(CultureInfo.InvariantCulture), value.PayloadHash,
            value.ReceiptId, value.ReceiptHash,
            value.AcceptedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            receipt is null ? null : Convert.ToBase64String(receipt),
            receipt is null ? null : receipt.Length.ToString(CultureInfo.InvariantCulture),
            value.ContentHash, value.AuditSequence.ToString(CultureInfo.InvariantCulture), value.AuditHash);

    private static void UpsertProductionOutboxWork(sqlite3 database, OutboxDelivery delivery,
        IReadOnlyList<ProductionOutboxEvent> events, StoreDeadline deadline)
    {
        var derived = DeriveProductionOutboxState(delivery, events);
        var lastFailure = derived.LastFailureCategory;
        AuditChainDatabase.Execute(database, @"
            INSERT INTO production_outbox_work(
                DeliveryId,State,AttemptCount,NextAttemptNumber,RetryEligible,PermanentBlock,
                ActiveAttemptId,ActiveRuntimeEpoch,LastFailureReasonCode,LastFailureCategory,RetryAfterUtc,
                LastEventPosition,LastEventContentHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(DeliveryId) DO UPDATE SET
                State=excluded.State,AttemptCount=excluded.AttemptCount,
                NextAttemptNumber=excluded.NextAttemptNumber,RetryEligible=excluded.RetryEligible,
                PermanentBlock=excluded.PermanentBlock,ActiveAttemptId=excluded.ActiveAttemptId,
                ActiveRuntimeEpoch=excluded.ActiveRuntimeEpoch,
                LastFailureReasonCode=excluded.LastFailureReasonCode,
                LastFailureCategory=excluded.LastFailureCategory,RetryAfterUtc=excluded.RetryAfterUtc,
                LastEventPosition=excluded.LastEventPosition,
                LastEventContentHash=excluded.LastEventContentHash;", deadline,
            delivery.DeliveryId.ToString("D"), derived.State.ToString(),
            derived.AttemptCount.ToString(CultureInfo.InvariantCulture),
            derived.NextAttemptNumber.ToString(CultureInfo.InvariantCulture),
            derived.RetryEligible ? "1" : "0", derived.PermanentBlock ? "1" : "0",
            derived.ActiveAttemptId?.ToString("D"), derived.ActiveRuntimeEpoch?.ToString("D"),
            derived.LastFailureReasonCode,
            lastFailure?.ToString(),
            derived.LastFailureCategory is OutboxFailureCategory.Transient or
                OutboxFailureCategory.UnknownOutcome
                ? derived.RetryAfterUtc?.ToString("O", CultureInfo.InvariantCulture) : null,
            derived.LastEventPosition.ToString(CultureInfo.InvariantCulture),
            derived.LastEventContentHash);
    }

    /// <summary>
    /// The verified backlog watermark: ThroughAuditSequence is the committed audit sequence at
    /// the moment of the projection and each route reports its still-pending obligations.
    /// </summary>
    internal static OutboxBacklogSnapshot ReadProductionOutboxBacklogSnapshot(sqlite3 database,
        long throughAuditSequence, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT d.RouteId,d.RouteContentHash,d.Criticality,COUNT(*),
                COALESCE(SUM(d.PayloadByteLength),0),MIN(d.CreatedAtUtc),SUM(w.PermanentBlock),
                COALESCE(SUM(CASE WHEN w.State='Failed' OR d.PreparationFailure IS NOT NULL THEN 1 ELSE 0 END),0)
            FROM production_outbox_deliveries d
            JOIN production_outbox_work w ON w.DeliveryId=d.DeliveryId
            WHERE w.State<>'Succeeded'
            GROUP BY d.RouteId,d.RouteContentHash,d.Criticality
            ORDER BY d.RouteId LIMIT 65;", deadline, statement =>
                (RouteId: SqliteNative.ColumnText(statement, 0) ?? string.Empty,
                    RouteHash: SqliteNative.ColumnText(statement, 1) ?? string.Empty,
                    Criticality: checked((int)SqliteNative.ColumnInt64(statement, 2)),
                    Count: SqliteNative.ColumnInt64(statement, 3),
                    Bytes: SqliteNative.ColumnInt64(statement, 4),
                    Oldest: SqliteNative.ColumnText(statement, 5),
                    Permanent: SqliteNative.ColumnInt64(statement, 6),
                    Failed: SqliteNative.ColumnInt64(statement, 7)));
        AuditChainDatabase.Require(rows.Count <= 64, "ProductionOutboxRouteCapacityExceeded");
        var routes = new List<OutboxRouteBacklog>(rows.Count);
        foreach (var row in rows)
        {
            if (!Enum.IsDefined((OutboxRouteCriticality)row.Criticality))
                throw new InvalidOperationException("ProductionOutboxBacklogInvalid");
            routes.Add(new OutboxRouteBacklog(row.RouteId, row.RouteHash,
                (OutboxRouteCriticality)row.Criticality, row.Count, row.Bytes,
                row.Oldest is null ? null : ParseProductionOutboxTime(row.Oldest),
                row.Permanent > 0, row.Failed));
        }
        return new OutboxBacklogSnapshot(throughAuditSequence, routes);
    }

    private void PublishProductionOutboxIntegrity(AuditIntegrityPolicy policy, long sequence)
    {
        Interlocked.Exchange(ref _lastCommittedAuditSequence, sequence);
        PublishIntegrity(SqliteAuditIntegrityQuery.Report(policy, AuditIntegrityState.Verifying,
            policy.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
        WakeIntegrityMonitor();
    }

    private StoreWriteResult ProductionOutboxIdempotent(ProductionOutboxWork work,
        ProductionOutboxEvent existing, sqlite3 database, StoreDeadline deadline)
    {
        var backlog = ReadProductionOutboxBacklogSnapshot(database, TailProductionOutboxAuditSequence(database, deadline), deadline);
        work.Completion.TrySetResult(new(true, existing.Kind == OutboxEventKind.Succeeded ?
            "ProductionOutboxSucceeded" : "ProductionOutboxAttemptOutcomeRecorded", existing, backlog));
        return new(true, "ProductionOutboxIdempotent");
    }

    private static StoreWriteResult ProductionOutboxRejected(ProductionOutboxWork work, string reason)
    {
        work.Completion.TrySetResult(new(false, reason));
        return new(false, reason);
    }

    private static long TailProductionOutboxAuditSequence(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;", deadline);
}
