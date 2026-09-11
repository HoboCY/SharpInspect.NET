using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Production;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The recovery marker is produced by the identity authorization callback and is
/// consumed by the same SQLite writer transaction as the identity/command facts.
/// It is deliberately internal: callers cannot append a recovery row by creating
/// a public history event.
/// </summary>
internal sealed record ProductionRecoveryWriteRequest(
    ManualProductionRecoveryCommand Command,
    Guid RuntimeEpoch,
    Guid RecoveryAttemptId,
    ProductionRecoverySafetyCapture SafetyCapture,
    ProductionRecoveryObservation Observation,
    Guid ActorPrincipalId,
    Guid ActorSessionId,
    long AuthorizationRevision,
    Guid StepUpGrantId,
    RecipeContractReference AuthorizationPolicy,
    DateTimeOffset AuthorizedAtUtc,
    Func<string?>? FinalGuard = null)
{
    internal ProductionRecoverySafetyEvidence CreateSafetyEvidence()
    {
        if (!SafetyCapture.Available || SafetyCapture.Observation is null)
            throw new InvalidOperationException("ProductionRecoverySafetyCaptureUnavailable");
        return new ProductionRecoverySafetyEvidence(SafetyCapture.Observation);
    }
}

internal sealed record ProductionRecoveryAuthorizationResult(
    RuntimeCommandOutcome Outcome,
    CommandAuditFact? CommandFact = null,
    ProductionInspectionHistoryEvent? Event = null,
    Guid? RecoveryAttemptId = null);

internal sealed record ProductionRecoveryCompletionWriteRequest(
    Guid CorrelationId,
    Guid RecoveryAttemptId,
    Guid RuntimeEpoch,
    string ExpectedRecoveryContentHash,
    ProductionRecoveryCleanupReceipt Receipt,
    Func<string?>? FinalGuard = null,
    ProductionRecoverySafetyCapture? SafetyCapture = null)
{
    internal string NormalizedExpectedRecoveryContentHash =>
        RecipeActivationValidation.Hash(ExpectedRecoveryContentHash,
            nameof(ExpectedRecoveryContentHash));
}

/// <summary>
/// Records a failed physical recovery attempt in the identity/command ledgers
/// while leaving the immutable production RecoveryRequired row pending.  The
/// production row is deliberately never changed by this marker.
/// </summary>
internal sealed record ProductionRecoveryFailureWriteRequest(
    Guid CorrelationId,
    Guid InspectionId,
    Guid RecoveryAttemptId,
    Guid RuntimeEpoch,
    string ExpectedRecoveryContentHash,
    string ReasonCode,
    Func<string?>? FinalGuard = null)
{
    internal string NormalizedExpectedRecoveryContentHash =>
        RecipeActivationValidation.Hash(ExpectedRecoveryContentHash,
            nameof(ExpectedRecoveryContentHash));
}

internal sealed record ProductionRecoveryWriteResult(
    bool Committed,
    string ReasonCode,
    ProductionInspectionHistoryEvent? Event = null,
    Guid? RecoveryAttemptId = null);

internal sealed record ProductionRecoveryWriteContext(
    ProductionInspectionHistoryEvent Provisional,
    ProductionRecoveryRecord Recovery,
    ProductionInspectionHistoryEvent? Existing,
    bool Replay)
{
    internal ProductionInspectionHistoryEvent EffectiveEvent => Existing ?? Provisional;
}

internal sealed record ProductionRecoveryFailureWork(
    CommandAuditFact Admission,
    Guid RecoveryAttemptId,
    string ExpectedRecoveryContentHash,
    string ReasonCode);

internal sealed record ProductionRecoveryCompletionWork(
    ProductionRecoveryCompletionWriteRequest Request)
{
    internal TaskCompletionSource<ProductionRecoveryWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed partial class SqliteCommandStore
{
    private static bool IsRecoverableProductionRecoveryRejection(string reason) =>
        reason.StartsWith("ProductionRecovery", StringComparison.Ordinal) &&
        (reason is "ProductionRecoveryTargetStale" or "ProductionRecoveryAlreadyClosed" or
            "ProductionRecoveryInspectionMissing" or "ProductionRecoveryRecordMissing" or
            "ProductionRecoveryDispositionImmutable" or
            "ProductionRecoveryDispositionNoteImmutable" or
            "ProductionRecoveryAttemptMismatch" or
            "ProductionRecoveryAdmissionEpochMismatch" or
            "ProductionRecoveryBindingMismatch" or
            "ProductionRecoveryCoreBindingMismatch" or
            "ProductionRecoveryPayloadBindingMismatch" or
            "ProductionRecoveryObservationBindingMismatch" ||
            reason.Contains("Guard", StringComparison.Ordinal) ||
            reason.Contains("Owner", StringComparison.Ordinal) ||
            reason.Contains("Target", StringComparison.Ordinal) ||
            reason.Contains("Safety", StringComparison.Ordinal) ||
            reason.Contains("Cancel", StringComparison.Ordinal));

    private static IdentityUpdate RejectProductionRecovery(IdentityUpdate evaluated,
        string reason)
    {
        var normalizedReason = AlgorithmConfigurationValidation.Identifier(reason,
            nameof(reason));
        var facts = evaluated.CommandFacts?.Select(value => value with
        {
            Disposition = CommandDisposition.Rejected,
            ReasonCode = normalizedReason
        }).ToArray();
        var events = evaluated.Events.Select(value => value with
        {
            Kind = IdentityEventKind.ProductionRecoveryFailed,
            ReasonCode = normalizedReason
        }).ToArray();
        var outcome = evaluated.Result switch
        {
            ProductionRecoveryAuthorizationResult result => result.Outcome with
            {
                Disposition = CommandDisposition.Rejected,
                ReasonCode = normalizedReason,
                Audit = AuditPersistence.Persisted
            },
            RuntimeCommandOutcome result => result with
            {
                Disposition = CommandDisposition.Rejected,
                ReasonCode = normalizedReason,
                Audit = AuditPersistence.Persisted
            },
            _ => throw new InvalidOperationException("ProductionRecoveryRejectionResultInvalid")
        };
        return evaluated with
        {
            Result = evaluated.Result switch
            {
                ProductionRecoveryAuthorizationResult result => new ProductionRecoveryAuthorizationResult(
                    outcome, facts?.FirstOrDefault(), null, result.RecoveryAttemptId),
                _ => outcome
            },
            Events = events,
            CommandFacts = facts,
            CommitGuard = null,
            ProductionRecovery = null
        };
    }

    /// <summary>
    /// Dedicated terminal entry point.  The implementation is kept in the
    /// production-inspection writer partial so it can share the exact audit,
    /// payload and capacity transaction.  Generic event append must never call
    /// this implicitly.
    /// </summary>
    internal async ValueTask<ProductionRecoveryWriteResult> CompleteProductionRecoveryAsync(
        ProductionRecoveryCompletionWriteRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.ProductionRecovery is null)
            return new(false, "ProductionRecoveryConfigurationRequired", null,
                request.RecoveryAttemptId);
        ArgumentNullException.ThrowIfNull(deadline);
        var work = new ProductionRecoveryCompletionWork(request);
        var queued = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                ProductionRecoveryCompletion: work), deadline, cancellationToken,
            "ProductionRecoveryCompletionAuditUnavailable", "ProductionRecoveryCompletionDeadlineExceeded")
            .ConfigureAwait(false);
        if (work.Completion.Task.IsCompletedSuccessfully)
            return await work.Completion.Task.ConfigureAwait(false);
        return new(queued.Committed, queued.ReasonCode);
    }

    /// <summary>
    /// Appends the failed terminal command and its ProductionRecoveryFailed
    /// identity event in the same writer transaction. The production recovery
    /// row remains RecoveryRequired so a later authorized attempt can retry.
    /// </summary>
    internal async ValueTask<ProductionRecoveryWriteResult> FailProductionRecoveryAsync(
        CommandAuditFact admission, Guid recoveryAttemptId, string expectedRecoveryContentHash,
        string reasonCode, StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (_options.ProductionRecovery is null)
            return new(false, "ProductionRecoveryConfigurationRequired", null,
                recoveryAttemptId);
        if (recoveryAttemptId == Guid.Empty)
            throw new ArgumentException("ProductionRecoveryAttemptRequired", nameof(recoveryAttemptId));
        var work = new ProductionRecoveryFailureWork(admission, recoveryAttemptId,
            RecipeActivationValidation.Hash(expectedRecoveryContentHash, nameof(expectedRecoveryContentHash)),
            AlgorithmConfigurationValidation.Identifier(reasonCode, nameof(reasonCode)));
        var queued = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
            ProductionRecoveryFailure: work), deadline, cancellationToken,
            "ProductionRecoveryFailureAuditUnavailable", "ProductionRecoveryFailureDeadlineExceeded")
            .ConfigureAwait(false);
        return new(queued.Committed, queued.ReasonCode, null, recoveryAttemptId);
    }

    private StoreWriteResult AppendProductionRecoveryCompletionCore(sqlite3 database,
        ProductionRecoveryCompletionWork work, StoreDeadline deadline) =>
        AppendProductionRecoveryCompletionCoreImpl(database, work, deadline);

    internal static void RequireRecoveryRequestIdentity(ProductionRecoveryWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command.CorrelationId == Guid.Empty || request.RuntimeEpoch == Guid.Empty ||
            request.RecoveryAttemptId == Guid.Empty || request.ActorPrincipalId == Guid.Empty ||
            request.ActorSessionId == Guid.Empty || request.StepUpGrantId == Guid.Empty ||
            request.AuthorizationRevision < 0 || request.Observation is null ||
            request.SafetyCapture is null || request.AuthorizationPolicy is null ||
            request.AuthorizedAtUtc == default || request.AuthorizedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("ProductionRecoveryWriteIdentityInvalid");
        if (request.Command.Invocation.StepUpGrantId != request.StepUpGrantId)
            throw new InvalidOperationException("ProductionRecoveryStepUpBindingInvalid");
    }

    private ProductionRecoveryWriteContext PrepareProductionRecoveryWrite(sqlite3 database,
        ProductionRecoveryWriteRequest request, StoreDeadline deadline)
    {
        RequireRecoveryRequestIdentity(request);
        if (_options.ProductionRecovery is null)
            throw new InvalidOperationException("ProductionRecoveryConfigurationRequired");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var rows = ReadProductionInspectionRows(database, options, deadline);
        var scoped = rows.Where(value => value.Event.InspectionId == request.Command.InspectionId)
            .OrderBy(value => value.Position).ToArray();
        var latest = scoped.LastOrDefault()?.Event ??
            throw new InvalidOperationException("ProductionRecoveryInspectionMissing");
        AuditChainDatabase.Require(string.Equals(latest.ContentHash, request.Command.ExpectedEventHash,
            StringComparison.Ordinal), "ProductionRecoveryTargetStale");
        AuditChainDatabase.Require(latest.Kind is not (ProductionInspectionEventKind.AcknowledgementReset or
            ProductionInspectionEventKind.RecoveryCompleted), "ProductionRecoveryAlreadyClosed");
        var safetyEvidence = request.CreateSafetyEvidence();
        ValidateRecoveryObservation(latest, request.Observation);
        var preflightGuardReason = EvaluateProductionFinalGuard(request.FinalGuard);
        if (preflightGuardReason is not null)
            throw new InvalidOperationException(preflightGuardReason);

        if (latest.Kind == ProductionInspectionEventKind.RecoveryRequired)
        {
            var existing = latest.Recovery ?? throw new InvalidOperationException(
                "ProductionRecoveryRecordMissing");
            AuditChainDatabase.Require(existing.Disposition == request.Command.Disposition,
                "ProductionRecoveryDispositionImmutable");
            AuditChainDatabase.Require(existing.DispositionNote == request.Command.DispositionNote,
                "ProductionRecoveryDispositionNoteImmutable");
            // RecoveryAttemptId is the immutable recovery-session anchor, not
            // the identity command attempt. A retry gets fresh command and
            // identity facts while reusing this single RecoveryRequired row.
            return new(latest, existing, latest, true);
        }

        var position = checked(rows.Count == 0 ? 1 : rows.Max(value => value.Position) + 1);
        var record = new ProductionRecoveryRecord(request.RecoveryAttemptId, request.RuntimeEpoch,
            request.Command.InspectionId, latest.Position, latest.ContentHash, request.Observation,
            safetyEvidence, request.ActorPrincipalId, request.ActorSessionId,
            request.AuthorizationRevision, request.StepUpGrantId, request.AuthorizationPolicy,
            request.Command.CorrelationId, request.RecoveryAttemptId, request.Command.AuthorizationTarget,
            request.Command.Disposition, request.Command.DispositionNote,
            ProductionRecoveryOutcome.Pending);
        var monotonic = Math.Max(1, safetyEvidence.Observation.MonotonicTimestamp);
        var provisional = new ProductionInspectionHistoryEvent(position,
            ProductionInspectionEventKind.RecoveryRequired, latest.Admission, latest.Core,
            request.Command.ReasonCode, request.AuthorizedAtUtc, monotonic, recovery: record);
        return new(provisional, record, null, false);
    }

    private static void ValidateRecoveryObservation(ProductionInspectionHistoryEvent latest,
        ProductionRecoveryObservation observation)
    {
        AuditChainDatabase.Require(observation.RuntimeEpoch == latest.Admission.RuntimeEpoch,
            "ProductionRecoveryAdmissionEpochMismatch");
        AuditChainDatabase.Require(observation.EndpointBindingHash == latest.Admission.EndpointBindingHash &&
            observation.PlcProfileHash == latest.Admission.PlcProfileHash &&
            observation.PlcPolicyHash == latest.Admission.PlcPolicyHash,
            "ProductionRecoveryBindingMismatch");
        AuditChainDatabase.Require(observation.CoreContentHash == latest.Core?.ContentHash,
            "ProductionRecoveryCoreBindingMismatch");
        var payload = latest.Core?.PlcPayload;
        AuditChainDatabase.Require(observation.PayloadContentHash == payload?.ContentHash &&
            observation.PayloadWireContentHash == payload?.WireContentHash,
            "ProductionRecoveryPayloadBindingMismatch");
    }

    private static ProductionRecoverySafetyEvidence ValidateCurrentSafetyEvidence(
        ProductionRecoverySafetyCapture capture, string encodedEvidence, Guid expectedAttemptId,
        Guid expectedInspectionId, string expectedEventHash, string expectedAuthorizationTarget,
        PartDisposition expectedDisposition, Guid expectedRuntimeEpoch)
    {
        if (!capture.Available || capture.Observation is null)
            throw new InvalidOperationException("ProductionRecoverySafetyCaptureUnavailable");
        if (capture.RuntimeEpoch != expectedRuntimeEpoch)
            throw new InvalidOperationException("ProductionRecoverySafetyCaptureStale");
        ProductionRecoverySafetyAuditEvidence? decoded = null;
        AuditChainDatabase.Require(
            IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(encodedEvidence, out decoded) &&
            decoded is not null && decoded.AttemptId == expectedAttemptId &&
            decoded.InspectionId == expectedInspectionId &&
            decoded.ExpectedEventHash == expectedEventHash &&
            decoded.AuthorizationTarget == expectedAuthorizationTarget &&
            decoded.Disposition == expectedDisposition &&
            decoded.RuntimeEpoch == expectedRuntimeEpoch &&
            decoded.CaptureAvailable == capture.Available &&
            decoded.CaptureReasonCode == capture.ReasonCode &&
            decoded.CaptureRevision == capture.Revision &&
            decoded.SourceContentHash == capture.Source.ContentHash &&
            decoded.SourceEpoch == capture.Source.SourceEpoch &&
            decoded.SourceGeneration == capture.Source.SourceGeneration &&
            decoded.SourceAvailable == capture.Source.Available,
            "ProductionRecoverySafetyAuditMismatch");
        var decodedEvidence = decoded!;
        var observation = capture.Observation!;
        AuditChainDatabase.Require(decodedEvidence.ObservationBindingHash == observation.Binding.ContentHash &&
            decodedEvidence.ObservationStatus == observation.Status &&
            decodedEvidence.ObservationReasonCode == observation.ReasonCode &&
            decodedEvidence.ObservationSourceEpoch == observation.SourceEpoch &&
            decodedEvidence.ObservationSourceGeneration == observation.SourceGeneration &&
            decodedEvidence.ObservationObservedAtUtc == observation.ObservedAtUtc &&
            decodedEvidence.ObservationMonotonicTimestamp == observation.MonotonicTimestamp &&
            decodedEvidence.ObservationMonotonicFrequency == observation.MonotonicFrequency,
            "ProductionRecoverySafetyObservationMismatch");
        return new ProductionRecoverySafetyEvidence(observation);
    }

    private ProductionInspectionHistoryEvent AppendProductionRecoveryIdentityMutation(
        sqlite3 database, ProductionRecoveryWriteContext context,
        ProductionRecoveryWriteRequest request, CommandAuditFact? commandFact,
        long commandAuditSequence, string commandAuditHash,
        long authorizationAuditSequence, string authorizationAuditHash,
        StoreDeadline deadline)
    {
        if (context.Replay)
        {
            var replayGuardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (replayGuardReason is not null)
                throw new InvalidOperationException(replayGuardReason);
            return context.EffectiveEvent;
        }
        AuditChainDatabase.Require(commandAuditSequence > 0 && authorizationAuditSequence > 0,
            "ProductionRecoveryAuthorizationAuditMissing");
        var authorization = ReadRecoveryAuthorizationAudit(database,
            authorizationAuditSequence, context.Provisional, deadline);
        AuditChainDatabase.Require(authorization.Kind == IdentityEventKind.ProductionRecoveryAuthorized &&
            authorization.CommandCorrelationId == request.Command.CorrelationId &&
            authorization.InspectionId == context.Recovery.InspectionId &&
            authorization.StepUpGrantId == request.StepUpGrantId,
            "ProductionRecoveryAuthorizationAuditMismatch");
        var commandAttemptId = commandFact?.AttemptId ?? context.Recovery.CommandAttemptId;
        var currentSafetyEvidence = ValidateCurrentSafetyEvidence(request.SafetyCapture,
            authorization.SafetyEvidence, commandAttemptId, context.Recovery.InspectionId,
            request.Command.ExpectedEventHash, request.Command.AuthorizationTarget,
            request.Command.Disposition, request.RuntimeEpoch);
        var record = new ProductionRecoveryRecord(context.Recovery.RecoveryAttemptId,
            request.RuntimeEpoch, context.Recovery.InspectionId,
            context.Recovery.PreviousEventPosition, context.Recovery.PreviousEventHash,
            context.Recovery.Observation, currentSafetyEvidence,
            context.Recovery.ActorPrincipalId, context.Recovery.ActorSessionId,
            context.Recovery.AuthorizationRevision, context.Recovery.StepUpGrantId,
            context.Recovery.AuthorizationPolicy,
            context.Recovery.CommandCorrelationId,
            commandAttemptId,
            context.Recovery.AuthorizationTarget, context.Recovery.Disposition,
            context.Recovery.DispositionNote, context.Recovery.Outcome,
            commandAuditSequence: commandAuditSequence, commandAuditHash: commandAuditHash,
            authorizationAuditSequence: authorizationAuditSequence,
            authorizationAuditHash: authorizationAuditHash,
            authorizationSafetyEvidence: authorization.SafetyEvidence);
        // The central audit binding must hash the exact projection that will
        // be persisted.  In particular, command/identity sequence+hash refs
        // are part of the recovery record; using the pre-audit context here
        // would bind zero refs and make the immediately persisted row fail
        // cold verification.
        var provisional = new ProductionInspectionHistoryEvent(context.Provisional.Position,
            context.Provisional.Kind, context.Provisional.Admission, context.Provisional.Core,
            context.Provisional.ReasonCode, context.Provisional.RecordedAtUtc,
            context.Provisional.MonotonicTimestamp, recovery: record);
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var reservation = ReadProductionInspectionReservation(database, deadline,
            provisional.InspectionId, provisional.Kind);
        var audit = AuditChainDatabase.AppendProductionInspectionLedgerEntry(database, policy,
            signingKey, provisional.Position, ProductionInspectionStorageCodec.EncodeAuditBinding(provisional),
            options, deadline, productionInspectionReserveOverride: reservation.AuditEntries);
        var persisted = new ProductionInspectionHistoryEvent(provisional.Position,
            provisional.Kind, provisional.Admission, provisional.Core, provisional.ReasonCode,
            provisional.RecordedAtUtc, provisional.MonotonicTimestamp, audit.Sequence, audit.Hash,
            recovery: record);
        var payload = ProductionInspectionStorageCodec.Encode(persisted);
        EnsureProductionPayloadCapacity(database, options, payload, reservation, deadline);
        InsertProductionInspectionEvent(database, persisted, payload, deadline);
        var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
        if (guardReason is not null)
            throw new InvalidOperationException(guardReason);
        return ReadPersistedProductionInspectionEvent(database, options, persisted.Position, deadline).Event;
    }

    private ProductionInspectionHistoryEvent AppendProductionRecoveryCompletionMutation(
        sqlite3 database, ProductionRecoveryCompletionWriteRequest request,
        CommandAuditFact? commandFact, long commandAuditSequence, string commandAuditHash,
        long authorizationAuditSequence, string authorizationAuditHash, StoreDeadline deadline)
    {
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var rows = ReadProductionInspectionRows(database, options, deadline);
        var sessionRows = rows.Where(value => value.Event.Recovery is
                { RecoveryAttemptId: var attempt } && attempt == request.RecoveryAttemptId)
            .OrderBy(value => value.Position).ToArray();
        var latestSession = sessionRows.LastOrDefault()?.Event ??
            throw new InvalidOperationException("ProductionRecoveryPendingMissing");
        AuditChainDatabase.Require(latestSession.Kind == ProductionInspectionEventKind.RecoveryRequired,
            "ProductionRecoveryAlreadyClosed");
        var pending = latestSession;
        var recovery = pending.Recovery ?? throw new InvalidOperationException("ProductionRecoveryRecordMissing");
        AuditChainDatabase.Require(recovery.ContentHash == request.NormalizedExpectedRecoveryContentHash,
            "ProductionRecoveryTargetStale");
        AuditChainDatabase.Require(request.Receipt.CleanupCompleted &&
            request.Receipt.RuntimeEpoch == request.RuntimeEpoch &&
            request.Receipt.EndpointBindingHash == pending.Admission.EndpointBindingHash &&
            request.Receipt.PlcProfileHash == pending.Admission.PlcProfileHash &&
            request.Receipt.PlcPolicyHash == pending.Admission.PlcPolicyHash &&
            request.Receipt.Health.PolicyHash == request.Receipt.PlcPolicyHash,
            "ProductionRecoveryCleanupIncomplete");
        AuditChainDatabase.Require(commandFact is not null &&
            commandFact.CorrelationId == request.CorrelationId &&
            commandFact.CommandKind == AuditedCommandKind.ManualProductionRecovery &&
            commandFact.Disposition == CommandDisposition.Accepted,
            "ProductionRecoveryCompletionCommandMismatch");
        var acceptedCommand = commandFact!;
        AuditChainDatabase.Require(commandAuditSequence > 0 && authorizationAuditSequence > 0,
            "ProductionRecoveryAuthorizationAuditMissing");
        var authorization = ReadRecoveryAuthorizationAudit(database,
            authorizationAuditSequence, pending, deadline);
        var authorizationStepUpGrant = authorization.StepUpGrantId ??
            throw new InvalidOperationException("ProductionRecoveryStepUpGrantMissing");
        var currentSafetyEvidence = ValidateCurrentSafetyEvidence(
            request.SafetyCapture ?? throw new InvalidOperationException(
                "ProductionRecoverySafetyCaptureUnavailable"), authorization.SafetyEvidence,
            acceptedCommand.AttemptId, recovery.InspectionId,
            acceptedCommand.AttemptId == recovery.RecoveryAttemptId ? recovery.PreviousEventHash : pending.ContentHash,
            authorization.ActionTargetId, recovery.Disposition, request.RuntimeEpoch);
        var authorizationPolicy = authorization.AuthorizationPolicy;
        var completedRecord = new ProductionRecoveryRecord(recovery.RecoveryAttemptId,
            request.RuntimeEpoch, recovery.InspectionId, recovery.PreviousEventPosition,
            recovery.PreviousEventHash, recovery.Observation, currentSafetyEvidence,
            authorization.ActorPrincipalId, authorization.SessionId,
            authorization.AuthorizationRevision, authorizationStepUpGrant,
            authorizationPolicy,
            request.CorrelationId, acceptedCommand.AttemptId,
            authorization.ActionTargetId, recovery.Disposition, recovery.DispositionNote,
            ProductionRecoveryOutcome.Completed, request.Receipt, commandAuditSequence,
            commandAuditHash, authorizationAuditSequence, authorizationAuditHash,
            authorizationSafetyEvidence: authorization.SafetyEvidence);
        var position = checked(rows.Max(value => value.Position) + 1);
        var provisional = new ProductionInspectionHistoryEvent(position,
            ProductionInspectionEventKind.RecoveryCompleted, pending.Admission, pending.Core,
            "ProductionRecoveryCompleted", request.Receipt.CompletedAtUtc,
            request.Receipt.MonotonicTimestamp, recovery: completedRecord);
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        var reservation = ReadProductionInspectionReservation(database, deadline,
            provisional.InspectionId, provisional.Kind);
        var audit = AuditChainDatabase.AppendProductionInspectionLedgerEntry(database, policy,
            signingKey, position, ProductionInspectionStorageCodec.EncodeAuditBinding(provisional),
            options, deadline, productionInspectionReserveOverride: reservation.AuditEntries);
        var persisted = new ProductionInspectionHistoryEvent(position, provisional.Kind,
            provisional.Admission, provisional.Core, provisional.ReasonCode,
            provisional.RecordedAtUtc, provisional.MonotonicTimestamp, audit.Sequence, audit.Hash,
            recovery: completedRecord);
        var payload = ProductionInspectionStorageCodec.Encode(persisted);
        EnsureProductionPayloadCapacity(database, options, payload, reservation, deadline);
        InsertProductionInspectionEvent(database, persisted, payload, deadline);
        var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
        if (guardReason is not null)
            throw new InvalidOperationException(guardReason);
        return ReadPersistedProductionInspectionEvent(database, options, position, deadline).Event;
    }

    /// <summary>
    /// Completes a recovery through the same single writer transaction used by
    /// identity commands.  The accepted recovery command is extended with its
    /// terminal fact and a dedicated identity event; only after those facts are
    /// durable is the immutable RecoveryCompleted production row appended.
    /// </summary>
    private StoreWriteResult AppendProductionRecoveryCompletionCoreImpl(
        sqlite3 database, ProductionRecoveryCompletionWork work, StoreDeadline deadline)
    {
        var request = work.Request;
        if (_options.ProductionRecovery is null)
            throw new InvalidOperationException("ProductionRecoveryConfigurationRequired");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        var started = false;
        var committed = false;
        try
        {
            if (request.CorrelationId == Guid.Empty || request.RecoveryAttemptId == Guid.Empty ||
                request.RuntimeEpoch == Guid.Empty)
                throw new InvalidOperationException("ProductionRecoveryCompletionIdentityInvalid");
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            AuditChainDatabase.RequireFullProductionInspectionVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);

            var rows = ReadProductionInspectionRows(database, options, deadline);
            var sessionRows = rows.Where(value => value.Event.Recovery is
                    { RecoveryAttemptId: var attempt } && attempt == request.RecoveryAttemptId)
                .OrderBy(value => value.Position).ToArray();
            var latest = sessionRows.LastOrDefault()?.Event ??
                throw new InvalidOperationException("ProductionRecoveryPendingMissing");
            AuditChainDatabase.Require(latest.Kind == ProductionInspectionEventKind.RecoveryRequired,
                "ProductionRecoveryAlreadyClosed");
            var recovery = latest.Recovery ?? throw new InvalidOperationException(
                "ProductionRecoveryRecordMissing");
            AuditChainDatabase.Require(recovery.ContentHash == request.NormalizedExpectedRecoveryContentHash,
                "ProductionRecoveryTargetStale");

            var accepted = ReadAcceptedRecoveryCommand(database, request.CorrelationId, deadline) ??
                throw new InvalidOperationException("ProductionRecoveryCompletionCommandMissing");
            AuditChainDatabase.Require(accepted.RuntimeEpoch == request.RuntimeEpoch &&
                accepted.CommandKind == AuditedCommandKind.ManualProductionRecovery &&
                accepted.Disposition == CommandDisposition.Accepted,
                "ProductionRecoveryCompletionCommandMismatch");
            var currentAuthorization = ReadRecoveryAuthorizationAuditByCorrelation(database,
                request.CorrelationId, latest, deadline);
            AuditChainDatabase.Require(currentAuthorization.Kind == IdentityEventKind.ProductionRecoveryAuthorized,
                "ProductionRecoveryCompletionAuthorizationMismatch");

            var state = ReadIdentityState(database, deadline);
            state.Revision = checked(state.Revision + 1);
            var now = request.Receipt.CompletedAtUtc;
            if (now < state.LastObservedUtc) now = state.LastObservedUtc;
            var terminalFact = new CommandAuditFact(Guid.NewGuid(), accepted.AttemptId,
                accepted.CorrelationId, accepted.RuntimeEpoch, now, accepted.CommandKind,
                accepted.Source, accepted.ClaimedPrincipalId, accepted.ClaimedSessionId,
                accepted.ClaimedStepUpGrantId, CommandAuditPhase.Completed, null,
                "ProductionRecoveryCompleted", accepted.AuthenticatedHumanPrincipalId);
            var identityEvent = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.ProductionRecoveryCompleted, now, state.StationId,
                currentAuthorization.PrincipalId, null, null, null,
                "ProductionRecoveryCompleted",
                PasswordPolicyVersion: _options.LocalIdentity!.PasswordPolicy.Version,
                BlocklistId: _options.LocalIdentity.PasswordPolicy.Blocklist!.Id,
                BlocklistVersion: _options.LocalIdentity.PasswordPolicy.Blocklist.Version,
                HashBaselineVersion: _options.LocalIdentity.HashBaselineVersion,
                HashTargetCost: _options.LocalIdentity.Baseline.TargetIterations,
                SessionId: currentAuthorization.SessionId,
                ActorPrincipalId: currentAuthorization.ActorPrincipalId,
                CommandCorrelationId: request.CorrelationId,
                StepUpGrantId: currentAuthorization.StepUpGrantId,
                RequiredPermission: currentAuthorization.RequiredPermission,
                AuthorizationRevision: currentAuthorization.AuthorizationRevision,
                ActionTargetId: currentAuthorization.ActionTargetId,
                BoundCommandCorrelationId: request.CorrelationId,
                ActionCommandKind: currentAuthorization.CommandKind.ToString(),
                OperationId: currentAuthorization.InspectionId,
                RecoverySafetyEvidence: currentAuthorization.SafetyEvidence);
            var identitySequence = AuditChainDatabase.AppendIdentity(database, policy,
                signingKey, BindAuthenticationPolicy(identityEvent with { StateRevision = state.Revision }),
                deadline);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            if (identityTail is null || identityTail.Value.Sequence != identitySequence)
                throw new InvalidOperationException("ProductionRecoveryCompletionIdentityAuditMismatch");
            state.LastIdentityAuditHash = identityTail.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database,
                "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;",
                deadline, state.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision,
                    identitySequence, signingKey));

            AppendIdentityCommandFacts(database, new[] { terminalFact }, request.CorrelationId,
                deadline, allowTerminalContinuation: true);
            var commandReference = ReadCommandAuditReference(database, accepted.EventId, deadline);
            var authorizationReference = ReadIdentityAuditReference(database, identitySequence, deadline);
            AuditChainDatabase.Require(commandReference.Sequence > 0 && commandReference.Hash is { Length: 64 } &&
                authorizationReference.Sequence > 0 && authorizationReference.Hash is { Length: 64 },
                "ProductionRecoveryCompletionAuditMissing");
            var persisted = AppendProductionRecoveryCompletionMutation(database, request, accepted,
                commandReference.Sequence, commandReference.Hash!, authorizationReference.Sequence,
                authorizationReference.Hash!, deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null) throw new InvalidOperationException(guardReason);
            SqliteNative.EnsureDeadline(deadline, default);
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            var result = new ProductionRecoveryWriteResult(true,
                "ProductionRecoveryCompleted", persisted, request.RecoveryAttemptId);
            work.Completion.TrySetResult(result);
            return new StoreWriteResult(true, result.ReasonCode, accepted);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("ProductionRecovery", StringComparison.Ordinal) ||
            exception.Message.StartsWith("Identity", StringComparison.Ordinal) ||
            AuditChainDatabase.IsCapacityReason(exception.Message))
        {
            var result = new ProductionRecoveryWriteResult(false, exception.Message, null,
                request.RecoveryAttemptId);
            work.Completion.TrySetResult(result);
            return new StoreWriteResult(false, exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = SqliteAuditIntegrityQuery.FaultReason(exception,
                "ProductionRecoveryCompletionAuditUnavailable");
            work.Completion.TrySetResult(new ProductionRecoveryWriteResult(false, reason,
                null, request.RecoveryAttemptId));
            return new StoreWriteResult(false, reason);
        }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private static CommandAuditFact? ReadAcceptedRecoveryCommand(sqlite3 database,
        Guid correlationId, StoreDeadline deadline)
    {
        return AuditChainDatabase.Read(database, @"
            SELECT EventId,AttemptId,CorrelationId,RuntimeEpoch,OccurredAtUtc,
                AuthenticatedHumanPrincipalId,CommandKind,Source,ClaimedPrincipalId,
                ClaimedSessionId,ClaimedStepUpGrantId,Phase,Disposition,ReasonCode
            FROM command_facts
            WHERE CorrelationId=? AND CommandKind=? AND Phase=? AND Disposition=?
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
            correlationId.ToString("D"),
            ((int)AuditedCommandKind.ManualProductionRecovery).ToString(CultureInfo.InvariantCulture),
            ((int)CommandAuditPhase.Outcome).ToString(CultureInfo.InvariantCulture),
            ((int)CommandDisposition.Accepted).ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
    }

    private static ProductionRecoveryAuthorizationAudit ReadRecoveryAuthorizationAuditByCorrelation(
        sqlite3 database, Guid correlationId, ProductionInspectionHistoryEvent pending,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT Sequence,IdentityPosition AS Ordinal,Hash,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL
            ORDER BY IdentityPosition DESC;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Ordinal: SqliteNative.ColumnInt64(statement, 1),
                Hash: SqliteNative.ColumnText(statement, 2) ?? string.Empty,
                Payload: SqliteNative.ColumnText(statement, 3) ?? string.Empty));
        foreach (var row in rows)
        {
            if (row.Ordinal <= 0 || row.Payload.Length == 0) continue;
            byte[] payload;
            try { payload = Convert.FromBase64String(row.Payload); }
            catch (FormatException) { continue; }
            ProductionRecoveryAuthorizationAudit? authorization = null;
            var decoded = IdentityAuditEvent.TryReadProductionRecoveryAuthorization(payload, row.Ordinal,
                pending.Admission.StationId, out authorization,
                ProductionRecoveryStoreOptions.SchemaVersion);
            if (!decoded || authorization is null ||
                authorization.Kind != IdentityEventKind.ProductionRecoveryAuthorized ||
                authorization.CommandCorrelationId != correlationId ||
                authorization.InspectionId != pending.InspectionId)
                continue;
            return authorization!;
        }
        throw new InvalidOperationException("ProductionRecoveryAuthorizationMissing");
    }

    private static ProductionRecoveryAuthorizationAudit ReadRecoveryAuthorizationAudit(
        sqlite3 database, long sequence, ProductionInspectionHistoryEvent pending,
        StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT IdentityPosition AS Ordinal,Payload FROM audit_entries
            WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;", deadline,
            statement => (Ordinal: SqliteNative.ColumnInt64(statement, 0),
                Payload: SqliteNative.ColumnText(statement, 1) ?? string.Empty),
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row.Ordinal > 0 && row.Payload.Length > 0,
            "ProductionRecoveryAuthorizationAuditMissing");
        byte[] payload;
        try { payload = Convert.FromBase64String(row.Payload); }
        catch (FormatException exception)
        { throw new InvalidOperationException("ProductionRecoveryAuthorizationAuditMismatch", exception); }
        ProductionRecoveryAuthorizationAudit? authorization = null;
        var decoded = IdentityAuditEvent.TryReadProductionRecoveryAuthorization(
            payload, row.Ordinal, pending.Admission.StationId, out authorization,
            ProductionRecoveryStoreOptions.SchemaVersion);
        AuditChainDatabase.Require(decoded && authorization is not null &&
            authorization.InspectionId == pending.InspectionId,
            "ProductionRecoveryAuthorizationAuditMismatch");
        return authorization!;
    }

    /// <summary>
    /// Validates a failed recovery attempt after its ordinary command and
    /// identity facts have been appended in the same transaction.  It returns
    /// the unchanged pending row so the caller can expose the durable anchor;
    /// it intentionally appends no production-inspection event.
    /// </summary>
    private ProductionInspectionHistoryEvent AppendProductionRecoveryFailureMutation(
        sqlite3 database, ProductionRecoveryFailureWriteRequest request,
        long commandAuditSequence, string commandAuditHash,
        long authorizationAuditSequence, string authorizationAuditHash,
        StoreDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CorrelationId == Guid.Empty || request.InspectionId == Guid.Empty ||
            request.RecoveryAttemptId == Guid.Empty || request.RuntimeEpoch == Guid.Empty ||
            commandAuditSequence <= 0 || authorizationAuditSequence <= 0 ||
            string.IsNullOrWhiteSpace(commandAuditHash) || string.IsNullOrWhiteSpace(authorizationAuditHash))
            throw new InvalidOperationException("ProductionRecoveryFailureIdentityInvalid");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var pending = ReadProductionInspectionRows(database, options, deadline)
            .Where(value => value.Event.InspectionId == request.InspectionId &&
                value.Event.Kind == ProductionInspectionEventKind.RecoveryRequired &&
                value.Event.Recovery is { RecoveryAttemptId: var attempt } &&
                attempt == request.RecoveryAttemptId)
            .OrderBy(value => value.Position).LastOrDefault()?.Event ??
            throw new InvalidOperationException("ProductionRecoveryPendingMissing");
        var recovery = pending.Recovery ?? throw new InvalidOperationException(
            "ProductionRecoveryRecordMissing");
        AuditChainDatabase.Require(recovery.ContentHash == request.NormalizedExpectedRecoveryContentHash,
            "ProductionRecoveryTargetStale");
        _ = AlgorithmConfigurationValidation.Identifier(request.ReasonCode, nameof(request.ReasonCode));
        var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
        if (guardReason is not null)
            throw new InvalidOperationException(guardReason);
        // Keep the references in the transaction's validation surface.  They
        // are carried by the identity/command facts; the production row stays
        // byte-for-byte unchanged and remains the recovery barrier.
        _ = commandAuditHash;
        _ = authorizationAuditHash;
        return pending;
    }

    private StoreWriteResult AppendProductionRecoveryFailureCore(sqlite3 database,
        ProductionRecoveryFailureWork work, StoreDeadline deadline)
    {
        if (_options.ProductionRecovery is null)
            return new StoreWriteResult(false, "ProductionRecoveryConfigurationRequired");
        var options = _options.ProductionInspections ??
            throw new InvalidOperationException("ProductionInspectionConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        var started = false;
        var committed = false;
        try
        {
            if (work.Admission.CorrelationId == Guid.Empty || work.Admission.RuntimeEpoch == Guid.Empty ||
                work.Admission.CommandKind != AuditedCommandKind.ManualProductionRecovery ||
                work.Admission.Phase != CommandAuditPhase.Outcome ||
                work.Admission.Disposition != CommandDisposition.Accepted ||
                work.Admission.AttemptId == Guid.Empty || work.Admission.EventId == Guid.Empty)
                throw new InvalidOperationException("ProductionRecoveryFailureCommandInvalid");
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            AuditChainDatabase.RequireFullProductionInspectionVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var rows = ReadProductionInspectionRows(database, options, deadline);
            var pendingRow = rows.Where(value => value.Event.Kind == ProductionInspectionEventKind.RecoveryRequired &&
                    value.Event.Recovery is { RecoveryAttemptId: var attempt } &&
                    attempt == work.RecoveryAttemptId)
                .OrderBy(value => value.Position).LastOrDefault();
            var pending = pendingRow?.Event ?? throw new InvalidOperationException(
                "ProductionRecoveryPendingMissing");
            var recovery = pending.Recovery ?? throw new InvalidOperationException(
                "ProductionRecoveryRecordMissing");
            AuditChainDatabase.Require(recovery.ContentHash == work.ExpectedRecoveryContentHash,
                "ProductionRecoveryTargetStale");

            // A physical failure is the terminal continuation of the accepted
            // recovery command that authorized this attempt.  Do not look up
            // the immutable first RecoveryRequired marker: retries have a new
            // correlation/authorization audit while retaining the same
            // recovery-session anchor.
            var accepted = ReadAcceptedRecoveryCommand(database,
                work.Admission.CorrelationId, deadline) ??
                throw new InvalidOperationException("ProductionRecoveryFailureCommandMissing");
            AuditChainDatabase.Require(accepted.EventId == work.Admission.EventId &&
                accepted.AttemptId == work.Admission.AttemptId &&
                SameCommandContext(accepted, work.Admission),
                "ProductionRecoveryFailureCommandMismatch");
            var authorizationAudit = ReadRecoveryAuthorizationAuditByCorrelation(database,
                work.Admission.CorrelationId, pending, deadline);
            AuditChainDatabase.Require(authorizationAudit.Kind ==
                IdentityEventKind.ProductionRecoveryAuthorized &&
                authorizationAudit.InspectionId == pending.InspectionId &&
                authorizationAudit.StepUpGrantId == accepted.ClaimedStepUpGrantId,
                "ProductionRecoveryFailureAuthorizationMismatch");

            var state = ReadIdentityState(database, deadline);
            state.Revision = checked(state.Revision + 1);
            var now = DateTimeOffset.UtcNow;
            if (now < state.LastObservedUtc) now = state.LastObservedUtc;
            // Keep the original accepted command attempt as the aggregate
            // identity.  The failed terminal is a continuation (position 2),
            // so it cannot create a second rejected admission or lose the
            // accepted lifecycle needed by a later retry.
            var terminalFact = new CommandAuditFact(Guid.NewGuid(), accepted.AttemptId,
                accepted.CorrelationId, accepted.RuntimeEpoch, now, accepted.CommandKind,
                accepted.Source, accepted.ClaimedPrincipalId, accepted.ClaimedSessionId,
                accepted.ClaimedStepUpGrantId, CommandAuditPhase.Failed, null,
                work.ReasonCode, accepted.AuthenticatedHumanPrincipalId);
            var identityEvent = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.ProductionRecoveryFailed, now, state.StationId,
                authorizationAudit.PrincipalId, null, null, null, work.ReasonCode,
                PasswordPolicyVersion: _options.LocalIdentity!.PasswordPolicy.Version,
                BlocklistId: _options.LocalIdentity.PasswordPolicy.Blocklist!.Id,
                BlocklistVersion: _options.LocalIdentity.PasswordPolicy.Blocklist.Version,
                HashBaselineVersion: _options.LocalIdentity.HashBaselineVersion,
                HashTargetCost: _options.LocalIdentity.Baseline.TargetIterations,
                SessionId: authorizationAudit.SessionId, ActorPrincipalId: authorizationAudit.ActorPrincipalId,
                CommandCorrelationId: work.Admission.CorrelationId,
                StepUpGrantId: authorizationAudit.StepUpGrantId,
                RequiredPermission: authorizationAudit.RequiredPermission,
                AuthorizationRevision: authorizationAudit.AuthorizationRevision,
                ActionTargetId: authorizationAudit.ActionTargetId,
                BoundCommandCorrelationId: work.Admission.CorrelationId,
                ActionCommandKind: authorizationAudit.CommandKind.ToString(), OperationId: authorizationAudit.InspectionId,
                RecoverySafetyEvidence: authorizationAudit.SafetyEvidence);
            var identitySequence = AuditChainDatabase.AppendIdentity(database, policy,
                signingKey, BindAuthenticationPolicy(identityEvent with { StateRevision = state.Revision }), deadline);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            if (identityTail is null || identityTail.Value.Sequence != identitySequence)
                throw new InvalidOperationException("ProductionRecoveryFailureIdentityAuditMismatch");
            state.LastIdentityAuditHash = identityTail.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;", deadline,
                state.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision,
                    identitySequence, signingKey));
            AppendIdentityCommandFacts(database, new[] { terminalFact },
                work.Admission.CorrelationId, deadline, allowTerminalContinuation: true);
            var commandReference = ReadCommandAuditReference(database, terminalFact.EventId, deadline);
            var authorizationReference = ReadIdentityAuditReference(database, identitySequence, deadline);
            AuditChainDatabase.Require(commandReference.Sequence > 0 && commandReference.Hash is { Length: 64 } &&
                authorizationReference.Sequence > 0 && authorizationReference.Hash is { Length: 64 },
                "ProductionRecoveryFailureAuditMissing");
            _ = AppendProductionRecoveryFailureMutation(database,
                new ProductionRecoveryFailureWriteRequest(work.Admission.CorrelationId,
                    pending.InspectionId, work.RecoveryAttemptId, work.Admission.RuntimeEpoch,
                    recovery.ContentHash, work.ReasonCode), commandReference.Sequence,
                commandReference.Hash!, authorizationReference.Sequence,
                authorizationReference.Hash!, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            return new StoreWriteResult(true, "ProductionRecoveryFailurePersisted",
                terminalFact);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("ProductionRecovery", StringComparison.Ordinal) ||
            exception.Message.StartsWith("Identity", StringComparison.Ordinal) ||
            AuditChainDatabase.IsCapacityReason(exception.Message))
        { return new StoreWriteResult(false, exception.Message); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new StoreWriteResult(false, SqliteAuditIntegrityQuery.FaultReason(exception,
            "ProductionRecoveryFailureAuditUnavailable")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }
}
