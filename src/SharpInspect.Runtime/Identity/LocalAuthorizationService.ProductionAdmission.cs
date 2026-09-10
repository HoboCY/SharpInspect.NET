using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Identity;

/// <summary>
/// The production-arm authorization transaction is deliberately kept next to the
/// other identity transactions.  The storage writer recognizes the result marker and
/// appends the schema-22 admission row while it still owns the same SQLite transaction.
/// There is no separate "arm succeeded" callback that could publish a report without
/// its identity and command facts.
/// </summary>
internal sealed partial class LocalAuthorizationService
{
    internal async ValueTask<RuntimeCommandOutcome> HandleProductionArmAsync(
        ArmProductionCommand command, Guid runtimeEpoch, Guid attemptId, long admissionGeneration,
        ProductionAdmissionFacts facts, ProductionAdmissionReport report, string? forcedRejection, StoreDeadline deadline,
        CancellationToken callerCancellation)
    {
        RuntimeCommandOutcome Unavailable(string reason) => new(command.CorrelationId,
            CommandDisposition.Rejected, reason, AuditPersistence.Unavailable, attemptId)
        {
            ProductionAdmission = report
        };

        if (command.CorrelationId == Guid.Empty || facts is null || report is null)
            return Unavailable("InvalidCommandContext");

        try
        {
            // The writer callback receives a fresh signed identity state.  The caller
            // token is observed inside that callback, while the queue/transaction token
            // remains independent so an accepted arm cannot be erased by late UI cancel.
            var written = await _store.UpdateIdentityCommandAsync(command.CorrelationId,
                (state, duplicate) => AuthorizeProductionArm(state, command, runtimeEpoch,
                    attemptId, admissionGeneration, facts, report, forcedRejection, duplicate,
                    callerCancellation), CancellationToken.None, deadline).ConfigureAwait(false);

            if (!written.Committed)
                return Unavailable(written.ReasonCode.StartsWith("ProductionAdmission", StringComparison.Ordinal)
                    ? written.ReasonCode : "ProductionAdmissionAuditUnavailable");
            if (written.Result is not ProductionAdmissionTransactionResult result)
                return Unavailable("ProductionAdmissionAuditUnavailable");
            return result.Outcome;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Unavailable(exception.Message.StartsWith("ProductionAdmission", StringComparison.Ordinal)
                ? exception.Message : "ProductionAdmissionAuditUnavailable");
        }
    }

    private IdentityUpdate AuthorizeProductionArm(IdentityAuthorityState state,
        ArmProductionCommand command, Guid runtimeEpoch, Guid attemptId, long admissionGeneration,
        ProductionAdmissionFacts facts, ProductionAdmissionReport report, string? forcedRejection, bool duplicate,
        CancellationToken callerCancellation)
    {
        SessionAuthorizationLease? lease = null;
        LocalAdministratorState? actor = null;
        StepUpGrant? reserved = null;
        var transferred = false;
        try
        {
            var reason = _store.ProductionAdmissionEnabled
                ? "Authorized" : "ProductionAdmissionConfigurationRequired";
            if (reason == "Authorized" && !TryLease(command.Invocation, out lease, out var leaseReason))
                reason = leaseReason;
            if (lease is not null) actor = Find(state, lease.Identity.PrincipalId);
            if (reason == "Authorized" && command.Invocation.Source != CommandSource.PhysicalConsole)
                reason = "PhysicalConsoleRequired";
            if (reason == "Authorized" && (actor is not { Enabled: true } ||
                    !actor.Permissions.Contains(Permission.ArmProduction)))
                reason = "PermissionDenied";
            if (reason == "Authorized" && duplicate) reason = "DuplicateCorrelationId";
            if (reason == "Authorized" && runtimeEpoch == Guid.Empty)
                reason = "ProductionAdmissionRuntimeUnavailable";
            if (reason == "Authorized" && report.RuntimeEpoch != runtimeEpoch)
                reason = "ProductionAdmissionReportStale";
            if (reason == "Authorized" && report.AdmissionGeneration != admissionGeneration)
                reason = "ProductionAdmissionGenerationChanged";
            if (reason == "Authorized" && forcedRejection is not null) reason = forcedRejection;
            if (reason == "Authorized" && callerCancellation.IsCancellationRequested)
                reason = "ProductionAdmissionCancelled";
            if (reason == "Authorized" && !report.CanArm) reason = ReportBlockReason(report);
            if (reason == "Authorized")
                reason = CheckGrant(command, actor!, lease!.SessionId, reserve: false, out _);

            if (reason == "Authorized")
                reason = CheckGrant(command, actor!, lease!.SessionId, reserve: true, out reserved);

            var accepted = reason == "Authorized";
            var observedAt = _utcNow().ToUniversalTime();
            if (observedAt < state.LastObservedUtc) observedAt = state.LastObservedUtc;
            var fact = new CommandAuditFact(Guid.NewGuid(), attemptId, command.CorrelationId,
                runtimeEpoch, observedAt, AuditedCommandKind.ArmProduction,
                Enum.IsDefined(command.Invocation.Source) ? command.Invocation.Source : null,
                command.Invocation.PrincipalId is { Length: <= 256 } claimed ? claimed : null,
                command.Invocation.SessionId, command.Invocation.StepUpGrantId,
                CommandAuditPhase.Outcome,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                accepted ? "ArmAdmitted" : reason, actor?.PrincipalId.ToString("D"));
            var binding = Binding(command);
            var identity = AuthorizationEvent(state,
                accepted ? IdentityEventKind.ProductionAdmissionArmAuthorized : IdentityEventKind.ManagementRejected,
                accepted ? "ArmAdmitted" : reason,
                ValidBinding(binding) ? binding : null,
                actor?.PrincipalId, lease?.SessionId, command.Invocation.StepUpGrantId,
                command.CorrelationId, actor?.AuthorizationRevision ?? 0,
                capturedTime: observedAt) with
            {
                EventId = fact.EventId,
                OperationId = accepted ? command.CorrelationId : null
            };
            var outcome = new RuntimeCommandOutcome(command.CorrelationId,
                accepted ? CommandDisposition.Accepted : CommandDisposition.Rejected,
                accepted ? "ArmAdmitted" : reason, AuditPersistence.Persisted, attemptId)
            {
                ProductionAdmission = report
            };
            var result = new ProductionAdmissionTransactionResult(outcome, report, accepted,
                runtimeEpoch, admissionGeneration, attemptId, facts.DurableHeads,
                facts.ObservationHash);
            if (!accepted)
                return new IdentityUpdate(result, new[] { identity }, new[] { fact });

            var guard = new AuthorizationCommitGuard(lease!, () =>
            {
                lock (_grantSync)
                {
                    if (reserved is not null) reserved.State = GrantState.Consumed;
                }
            }, () =>
            {
                lock (_grantSync)
                {
                    if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                }
            });
            transferred = true;
            return new IdentityUpdate(result, new[] { identity }, new[] { fact }, guard);
        }
        finally
        {
            if (!transferred)
            {
                lock (_grantSync)
                {
                    if (reserved is { State: GrantState.Reserved }) reserved.State = GrantState.Active;
                }
                lease?.Dispose();
            }
        }
    }

    private static string ReportBlockReason(ProductionAdmissionReport report) =>
        report.Gates.FirstOrDefault(gate => gate.Status is not ProductionAdmissionGateStatus.Passed &&
            !(gate.Gate == ProductionAdmissionGate.PowerLossQualification &&
                gate.Status == ProductionAdmissionGateStatus.NotApplicable))?.ReasonCode
        ?? "ProductionAdmissionBlocked";
}

/// <summary>
/// Marker carried through <see cref="IdentityUpdate.Result"/>.  The SQLite writer
/// persists the report, identity event and command fact in one transaction; Runtime
/// uses the same object as the post-commit source of truth.
/// </summary>
internal sealed record ProductionAdmissionTransactionResult(
    RuntimeCommandOutcome Outcome,
    ProductionAdmissionReport Report,
    bool Accepted,
    Guid RuntimeEpoch,
    long AdmissionGeneration,
    Guid AttemptId,
    IReadOnlyDictionary<string, string> ExpectedDurableHeads,
    string FactsObservationHash);

/// <summary>Completes or fails a durable admission authorization. Completion does
/// not assert that the live station reached Armed; Runtime checks its current state separately.</summary>
internal interface IProductionAdmissionTerminalWriter
{
    ValueTask<IdentityWriteResult> CompleteProductionAdmissionAsync(Guid correlationId,
        Guid attemptId, Guid runtimeEpoch, long admissionGeneration, string reasonCode,
        StoreDeadline deadline);
}
