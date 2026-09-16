using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record DiagnosticOperationWriteResult(bool Committed, string ReasonCode,
    DiagnosticOperationStoredRow? Record = null);

/// <summary>One admitted operation request, bound by root to its accepted command facts.</summary>
internal sealed record DiagnosticOperationMutation(Guid OperationId, DiagnosticOperationKind Kind,
    Guid RuntimeEpoch, DiagnosticSupportReason Reason, string ReasonCode, string AuthorizationTarget,
    DiagnosticCaptureProfileFields? CaptureProfile, SupportBundleScopeFields? SupportScope,
    DateTimeOffset? AdmittedDeadlineUtc, int? AdmittedMaximumEvents);

/// <summary>One nonhuman seal request: capture stop evidence or the staged bundle artifact.</summary>
internal sealed record DiagnosticOperationSeal(Guid OperationId, DateTimeOffset ObservedAtUtc, string ReasonCode,
    Guid? BundleId, string? BundleContentHash, long? BundleBytes, DateTimeOffset? BundleExpiresAtUtc,
    Guid? StopCommandEventId = null, Guid? StopAuthorizationEventId = null)
{
    public int? ObservedEvents { get; init; }
    public DiagnosticSupportReason? StopReason { get; init; }
}

/// <summary>One nonhuman terminal request: completion, failure or interruption.</summary>
internal sealed record DiagnosticOperationTerminal(Guid OperationId, DiagnosticOperationPhase Phase,
    DateTimeOffset ObservedAtUtc, string ReasonCode, Guid? BundleId, string? BundleContentHash,
    long? BundleBytes, DateTimeOffset? BundleExpiresAtUtc)
{
    public int? ObservedEvents { get; init; }
}

/// <summary>Bounded durable read projection; never exposes an audit connection or a writer.</summary>
internal sealed record DiagnosticSupportReadState(bool Available, string ReasonCode,
    DiagnosticSupportConfiguration? Configuration, IReadOnlyList<DiagnosticOperationState> Operations,
    long ThroughAuditSequence);

internal sealed class DiagnosticOperationWork
{
    internal DiagnosticOperationWork(DiagnosticOperationSeal seal)
    {
        Seal = seal ?? throw new ArgumentNullException(nameof(seal));
    }

    internal DiagnosticOperationWork(DiagnosticOperationTerminal terminal)
    {
        Terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    internal DiagnosticOperationSeal? Seal { get; }
    internal DiagnosticOperationTerminal? Terminal { get; }
    internal TaskCompletionSource<DiagnosticOperationWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed partial class SqliteCommandStore
{
    private sealed record DiagnosticSupportAdmissionPlan(long IdentityReserve, long CommandReserve,
        long AdmissionReserve);

    /// <summary>
    /// Validates one admitted mutation against its accepted command fact, its
    /// DiagnosticOperationAuthorized identity event, the deployed option binding and the
    /// complete four-fact ledger footprint, all before any fact of the transaction is written.
    /// </summary>
    private DiagnosticSupportAdmissionPlan PrepareDiagnosticSupportAdmission(sqlite3 database,
        IdentityUpdate update, StoreDeadline deadline)
    {
        var mutation = update.DiagnosticOperation!;
        DiagnosticOperationStorageCodec.Require(_options.DiagnosticSupport is not null, "ConfigurationRequired");
        var eventFact = update.Events.Count == 1 ? update.Events[0] : null;
        var commandFact = update.CommandFacts is { Count: 1 } ? update.CommandFacts[0] : null;
        var (permission, commandKind) = mutation.Kind == DiagnosticOperationKind.Capture
            ? (Permission.StartDiagnosticCapture, AuditedCommandKind.StartDiagnosticCapture)
            : (Permission.ExportSupportBundle, AuditedCommandKind.CreateSupportBundle);
        var principal = eventFact?.PrincipalId ?? Guid.Empty;
        var session = eventFact?.SessionId ?? Guid.Empty;
        var grant = eventFact?.StepUpGrantId ?? Guid.Empty;
        DiagnosticOperationStorageCodec.Require(eventFact is not null && commandFact is not null &&
            eventFact.Kind == IdentityEventKind.DiagnosticOperationAuthorized &&
            eventFact.OperationId == mutation.OperationId && principal != Guid.Empty &&
            session != Guid.Empty && grant != Guid.Empty &&
            eventFact.RequiredPermission == permission.ToString() &&
            eventFact.CommandCorrelationId == commandFact.CorrelationId &&
            eventFact.ActionTargetId == mutation.AuthorizationTarget &&
            eventFact.AuthorizationRevision >= 0 &&
            commandFact.Phase == CommandAuditPhase.Outcome &&
            commandFact.Disposition == CommandDisposition.Accepted && commandFact.CommandKind == commandKind &&
            commandFact.ReasonCode == mutation.ReasonCode &&
            commandFact.ClaimedPrincipalId == principal.ToString("D") &&
            commandFact.ClaimedSessionId == session && commandFact.ClaimedStepUpGrantId == grant &&
            commandFact.AuthenticatedHumanPrincipalId == principal.ToString("D") &&
            commandFact.CorrelationId == update.CommandFacts![0].CorrelationId, "AdmissionShapeInvalid");
        RequireConfiguredDiagnosticSupport(database, _options, deadline);
        var configuration = DiagnosticSupportConfigurationFor(_options);
        DiagnosticOperationStorageCodec.Require(configuration.BindingHash ==
            ReadDiagnosticSupportConfiguration(database, deadline).BindingHash, "AdmissionConfigurationMismatch");
        DiagnosticOperationStorageCodec.Require(mutation.ReasonCode is { Length: > 0 and <= 128 } &&
            mutation.ReasonCode.All(c => c <= 127 && (char.IsLetterOrDigit(c) || c is '.' or '_' or '-')) &&
            DiagnosticOperationStorageCodec.Hash(mutation.AuthorizationTarget), "AdmissionReasonInvalid");
        DiagnosticOperationStorageCodec.Require(mutation.AdmittedDeadlineUtc is { } admittedDeadline &&
            DiagnosticOperationStorageCodec.Utc(admittedDeadline) && mutation.RuntimeEpoch != Guid.Empty,
            "AdmissionDeadlineInvalid");
        if (mutation.Kind == DiagnosticOperationKind.Capture)
        {
            DiagnosticOperationStorageCodec.Require(mutation.CaptureProfile is not null &&
                mutation.SupportScope is null &&
                mutation.AdmittedMaximumEvents == mutation.CaptureProfile.MaximumEvents &&
                mutation.CaptureProfile.LoggingPolicyHash == configuration.LoggingPolicyHash,
                "AdmissionCaptureInvalid");
        }
        else
        {
            DiagnosticOperationStorageCodec.Require(mutation.SupportScope is not null &&
                mutation.CaptureProfile is null && mutation.AdmittedMaximumEvents is null &&
                mutation.SupportScope.RuntimeEpoch == mutation.RuntimeEpoch, "AdmissionBundleInvalid");
        }
        var rows = ReadDiagnosticOperationRows(database, configuration, deadline);
        DiagnosticOperationStorageCodec.Require(!rows.GroupBy(row => row.Payload.OperationId)
            .Any(group => group.Last().Payload.Phase is DiagnosticOperationPhase.Admitted or DiagnosticOperationPhase.Sealed),
            "OperationBusy");
        DiagnosticOperationStorageCodec.Require(rows.Count + 1 + DiagnosticSupportStoreOptions.AdmittedTerminalReserve <=
            Math.Min(configuration.MaximumOperations, configuration.MaximumOperationFacts), "OperationCapacityExceeded");
        var usedBytes = rows.Aggregate(0L, (sum, row) => checked(sum + row.PayloadBytes.Length));
        DiagnosticOperationStorageCodec.Require(checked(usedBytes +
            checked((long)(1 + DiagnosticSupportStoreOptions.AdmittedTerminalReserve) *
                configuration.MaximumAuditPayloadBytes)) <= Math.Min(configuration.MaximumTotalBytes, configuration.MaximumAuditBytes), "TotalCapacityExceeded");
        AuditChainDatabase.EnsureDiagnosticSupportTransactionCapacity(database, _policy!, 3,
            DiagnosticSupportStoreOptions.AdmittedTerminalReserve, deadline);
        return new DiagnosticSupportAdmissionPlan(
            IdentityReserve: DiagnosticSupportStoreOptions.AdmittedTerminalReserve + 2,
            CommandReserve: DiagnosticSupportStoreOptions.AdmittedTerminalReserve + 1,
            AdmissionReserve: DiagnosticSupportStoreOptions.AdmittedTerminalReserve);
    }

    /// <summary>
    /// Appends the admitted fact after the accepted command fact and its signed authorization
    /// event. The caller already proved the four-fact reserve inside this transaction.
    /// </summary>
    private void AppendDiagnosticSupportIdentityMutation(sqlite3 database, IdentityUpdate update, IdentityWork work,
        long identitySequence, StoreDeadline deadline)
    {
        var mutation = update.DiagnosticOperation!;
        var commandFact = update.CommandFacts![0];
        var eventFact = update.Events[0];
        var configuration = ReadDiagnosticSupportConfiguration(database, deadline);
        var commandReference = ReadCommandAuditReference(database, commandFact.EventId, deadline);
        var authorizationReference = ReadIdentityAuditReference(database, identitySequence, deadline);
        DiagnosticOperationStorageCodec.Require(commandReference.Sequence > 0 &&
            commandReference.Hash is { Length: 64 } && authorizationReference.Sequence > 0 &&
            authorizationReference.Hash is { Length: 64 }, "AdmissionAuditReferenceMissing");
        var payload = new DiagnosticOperationPayload
        {
            EventId = Guid.NewGuid(), OperationId = mutation.OperationId,
            CommandCorrelationId = commandFact.CorrelationId, RuntimeEpoch = mutation.RuntimeEpoch,
            Kind = mutation.Kind, Phase = DiagnosticOperationPhase.Admitted, AggregateSequence = 1,
            ObservedAtUtc = eventFact.OccurredAtUtc, Reason = mutation.Reason, ReasonCode = mutation.ReasonCode,
            ActorPrincipalId = eventFact.PrincipalId!.Value, SessionId = eventFact.SessionId!.Value,
            StepUpGrantId = eventFact.StepUpGrantId!.Value,
            AuthorizationRevision = eventFact.AuthorizationRevision, AuthorizationTarget = mutation.AuthorizationTarget,
            AuthorizationPolicyId = _options.LocalIdentity!.AuthorizationPolicy.Id,
            AuthorizationPolicyVersion = _options.LocalIdentity.AuthorizationPolicy.Version,
            AuthorizationPolicyHash = _options.LocalIdentity.AuthorizationPolicy.ContentHash,
            LoggingPolicyHash = configuration.LoggingPolicyHash, SupportPolicyHash = configuration.SupportPolicyHash,
            ConfigurationHash = configuration.OptionsHash,
            Command = new DiagnosticAuditReference { EventId = commandFact.EventId,
                Sequence = commandReference.Sequence, Hash = commandReference.Hash! },
            Authorization = new DiagnosticAuditReference { EventId = eventFact.EventId,
                Sequence = authorizationReference.Sequence, Hash = authorizationReference.Hash! },
            AdmittedDeadlineUtc = mutation.AdmittedDeadlineUtc, AdmittedMaximumEvents = mutation.AdmittedMaximumEvents,
            CaptureProfile = mutation.CaptureProfile, SupportScope = mutation.SupportScope
        };
        var row = AppendDiagnosticSupportInTransaction(database, configuration, payload,
            DiagnosticSupportStoreOptions.AdmittedTerminalReserve, deadline);
        work.Result = row;
    }

    internal ValueTask<DiagnosticOperationWriteResult> AppendDiagnosticOperationSealedAsync(
        DiagnosticOperationSeal seal, StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seal);
        ArgumentNullException.ThrowIfNull(deadline);
        return AppendDiagnosticOperationAsync(new DiagnosticOperationWork(seal), deadline, cancellationToken);
    }

    internal ValueTask<DiagnosticOperationWriteResult> AppendDiagnosticOperationTerminalAsync(
        DiagnosticOperationTerminal terminal, StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(deadline);
        return AppendDiagnosticOperationAsync(new DiagnosticOperationWork(terminal), deadline, cancellationToken);
    }

    private async ValueTask<DiagnosticOperationWriteResult> AppendDiagnosticOperationAsync(
        DiagnosticOperationWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        if (_options.DiagnosticSupport is null || _queue is null || _queueSlots is null ||
            Volatile.Read(ref _disposed) != 0 || _worker.IsCompleted)
            return new(false, "DiagnosticSupportStoreUnavailable");
        try
        {
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero ||
                !await _queueSlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                return new(false, "DiagnosticSupportCommitDeadlineExceeded");
            if (!_queue.Writer.TryWrite(new WriteRequest(null, deadline, DiagnosticOperation: work)))
            {
                _queueSlots.Release();
                return new(false, "DiagnosticSupportStoreUnavailable");
            }
            return await work.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return new(false, "DiagnosticSupportCommitDeadlineExceeded"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "DiagnosticSupportWriteUnavailable")); }
    }

    private StoreWriteResult AppendDiagnosticOperationCore(sqlite3 database, DiagnosticOperationWork work,
        StoreDeadline deadline)
    {
        if (Integrity?.State == AuditIntegrityState.Faulted) return new(false, Integrity.ReasonCode);
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredDiagnosticSupport(database, _options, deadline);
            AuditChainDatabase.RequireFullDiagnosticSupportVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadDiagnosticSupportConfiguration(database, deadline);
            var rows = ReadDiagnosticOperationRows(database, configuration, deadline);
            var operationId = work.Seal?.OperationId ?? work.Terminal!.OperationId;
            var history = rows.Where(row => row.Payload.OperationId == operationId)
                .OrderBy(row => row.Payload.AggregateSequence).ToArray();
            DiagnosticOperationStorageCodec.Require(history.Length > 0, "OperationMissing");
            var previous = history[^1].Payload;
            var phase = work.Seal is not null ? DiagnosticOperationPhase.Sealed : work.Terminal!.Phase;
            DiagnosticOperationStorageCodec.Require(phase is DiagnosticOperationPhase.Sealed or
                DiagnosticOperationPhase.Completed or DiagnosticOperationPhase.Failed or
                DiagnosticOperationPhase.Interrupted, "OperationPhaseInvalid");
            var payload = BuildDiagnosticSupportPayload(database, configuration, previous, work, phase, deadline);
            var historyWithCandidate = rows.Append(new DiagnosticOperationStoredRow(rows.Count + 1, payload,
                Array.Empty<byte>(), new string('0', 64), 0, new string('0', 64))).ToArray();
            ValidateDiagnosticOperationHistory(configuration, historyWithCandidate);
            var reserve = phase == DiagnosticOperationPhase.Sealed ? 2L : 0L;
            AuditChainDatabase.EnsureDiagnosticSupportTransactionCapacity(database, _policy!, 1, reserve, deadline);
            var row = AppendDiagnosticSupportInTransaction(database, configuration, payload, reserve, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "DiagnosticOperationRecorded", row));
            return new(true, "DiagnosticOperationRecorded");
        }
        catch (TimeoutException) { return new(false, "DiagnosticSupportCommitDeadlineExceeded"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "DiagnosticSupportWriteFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private DiagnosticOperationPayload BuildDiagnosticSupportPayload(sqlite3 database,
        DiagnosticSupportConfiguration configuration, DiagnosticOperationPayload previous,
        DiagnosticOperationWork work, DiagnosticOperationPhase phase, StoreDeadline deadline)
    {
        var payload = previous with
        {
            EventId = Guid.NewGuid(),
            Phase = phase,
            AggregateSequence = checked(previous.AggregateSequence + 1),
            ObservedAtUtc = work.Seal?.ObservedAtUtc ?? work.Terminal!.ObservedAtUtc,
            ReasonCode = work.Seal?.ReasonCode ?? work.Terminal!.ReasonCode,
            ObservedEvents = work.Seal?.ObservedEvents ?? work.Terminal?.ObservedEvents ?? previous.ObservedEvents
        };
        if (work.Seal is { } seal)
        {
            DiagnosticOperationStorageCodec.Require(previous.Phase == DiagnosticOperationPhase.Admitted,
                "OperationTransitionInvalid");
            if (previous.Kind == DiagnosticOperationKind.Capture)
            {
                DiagnosticOperationStorageCodec.Require((seal.StopCommandEventId is null) ==
                    (seal.StopAuthorizationEventId is null) && seal.BundleId is null &&
                    seal.BundleContentHash is null && seal.BundleBytes is null && seal.BundleExpiresAtUtc is null,
                    "CaptureStopEvidenceInvalid");
                if (seal.StopCommandEventId is { } stopCommandId && seal.StopAuthorizationEventId is { } stopAuthorizationId)
                {
                    DiagnosticOperationStorageCodec.Require(stopCommandId != Guid.Empty &&
                        stopAuthorizationId != Guid.Empty, "CaptureStopEvidenceInvalid");
                    var commandReference = ReadCommandAuditReference(database, stopCommandId, deadline);
                    DiagnosticOperationStorageCodec.Require(commandReference.Sequence > 0 &&
                        commandReference.Hash is { Length: 64 }, "StopCommandMissing");
                    var authorizationReference = ReadDiagnosticIdentityAuditReferenceByEventId(database, stopAuthorizationId,
                        deadline);
                    DiagnosticOperationStorageCodec.Require(authorizationReference is not null,
                        "StopAuthorizationMissing");
                    payload = payload with
                    {
                        StopCommand = new DiagnosticAuditReference { EventId = stopCommandId,
                            Sequence = commandReference.Sequence, Hash = commandReference.Hash! },
                        StopAuthorization = authorizationReference,
                        StopReason = seal.StopReason,
                        StopAuthorizationTarget = BuildCaptureStopTarget(previous, seal.StopReason ?? previous.Reason)
                    };
                }
            }
            else
            {
                DiagnosticOperationStorageCodec.Require(seal.StopCommandEventId is null &&
                    seal.StopAuthorizationEventId is null && seal.ReasonCode is { Length: > 0 and <= 128 } &&
                    seal.BundleId is { } bundleId && bundleId != Guid.Empty &&
                    seal.BundleContentHash is { Length: 64 } && seal.BundleBytes is > 0 &&
                    seal.BundleExpiresAtUtc is { } expiry && DiagnosticOperationStorageCodec.Utc(expiry),
                    "BundleSealEvidenceInvalid");
                payload = payload with
                {
                    BundleId = seal.BundleId, BundleContentHash = seal.BundleContentHash!.ToUpperInvariant(),
                    BundleBytes = seal.BundleBytes, BundleExpiresAtUtc = seal.BundleExpiresAtUtc
                };
            }
        }
        if (work.Terminal is { } terminal)
        {
            DiagnosticOperationStorageCodec.Require(
                (previous.Phase is DiagnosticOperationPhase.Admitted or DiagnosticOperationPhase.Sealed) &&
                phase != DiagnosticOperationPhase.Sealed, "OperationTransitionInvalid");
            if (previous.Kind == DiagnosticOperationKind.Bundle)
            {
                if (phase == DiagnosticOperationPhase.Completed)
                {
                    DiagnosticOperationStorageCodec.Require(terminal.BundleId is { } bundleId &&
                        bundleId != Guid.Empty && terminal.BundleContentHash is { Length: 64 } &&
                        terminal.BundleBytes is > 0 && terminal.BundleExpiresAtUtc is { } expiry &&
                        DiagnosticOperationStorageCodec.Utc(expiry) &&
                        (previous.BundleId is null || (previous.BundleId == terminal.BundleId &&
                            previous.BundleContentHash == terminal.BundleContentHash!.ToUpperInvariant() &&
                            previous.BundleBytes == terminal.BundleBytes &&
                            previous.BundleExpiresAtUtc == terminal.BundleExpiresAtUtc)),
                        "BundleCompletionEvidenceInvalid");
                    payload = payload with
                    {
                        BundleId = terminal.BundleId, BundleContentHash = terminal.BundleContentHash!.ToUpperInvariant(),
                        BundleBytes = terminal.BundleBytes, BundleExpiresAtUtc = terminal.BundleExpiresAtUtc
                    };
                }
                else
                {
                    DiagnosticOperationStorageCodec.Require(terminal.BundleId is null &&
                        terminal.BundleContentHash is null && terminal.BundleBytes is null &&
                        terminal.BundleExpiresAtUtc is null, "BundleFailureEvidenceInvalid");
                }
            }
        }
        if (payload.Kind == DiagnosticOperationKind.Capture)
            payload = BindAcceptedDiagnosticStop(database, payload, deadline);
        DiagnosticOperationStorageCodec.ValidatePayload(payload, configuration);
        return payload;
    }

    /// <summary>
    /// Writes one diagnostic fact together with its signed central-audit metadata entry inside
    /// the caller's transaction. The caller has already reserved the exact remaining footprint.
    /// </summary>
    private DiagnosticOperationStoredRow AppendDiagnosticSupportInTransaction(sqlite3 database,
        DiagnosticSupportConfiguration configuration, DiagnosticOperationPayload payload, long reserve,
        StoreDeadline deadline)
    {
        var payloadBytes = DiagnosticOperationStorageCodec.Encode(payload, configuration);
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM diagnostic_operation_facts;", deadline) + 1);
        DiagnosticOperationStorageCodec.Require(position <= Math.Min(configuration.MaximumOperations, configuration.MaximumOperationFacts),
            "OperationCapacityExceeded");
        var used = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM diagnostic_operation_facts;", deadline);
        DiagnosticOperationStorageCodec.Require(used + payloadBytes.Length <=
            Math.Min(configuration.MaximumTotalBytes, configuration.MaximumAuditBytes), "TotalCapacityExceeded");
        var contentHash = DiagnosticOperationStorageCodec.ContentHash(position, configuration.BindingHash,
            payloadBytes);
        var nextAudit = checked(AuditChainDatabase.Tail(database, deadline).Sequence + 1);
        var audit = AuditChainDatabase.AppendDiagnosticSupportEvent(database, _policy!, _signingKey!,
            DiagnosticOperationStorageCodec.AuditBinding(position, configuration.BindingHash, payloadBytes),
            reserve, deadline);
        DiagnosticOperationStorageCodec.Require(audit.Sequence == nextAudit, "AuditSequenceChanged");
        AuditChainDatabase.Execute(database, @"INSERT INTO diagnostic_operation_facts(Position,EventId,
            OperationId,Kind,AggregateSequence,Phase,Payload,ContentHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?);", deadline,
            DiagnosticSupportNumber(position), payload.EventId.ToString("D"),
            payload.OperationId.ToString("D"), DiagnosticSupportNumber((long)payload.Kind),
            DiagnosticSupportNumber(payload.AggregateSequence), DiagnosticSupportNumber((long)payload.Phase),
            System.Text.Encoding.UTF8.GetString(payloadBytes), contentHash,
            DiagnosticSupportNumber(audit.Sequence), audit.Hash);
        return new DiagnosticOperationStoredRow(position, payload, payloadBytes, contentHash,
            audit.Sequence, audit.Hash);
    }

    /// <summary>One exact stop authorization target for one admitted capture profile.</summary>
    private static string BuildCaptureStopTarget(DiagnosticOperationPayload admitted, DiagnosticSupportReason reason) =>
        AlgorithmContractValidation.HashParts(new[] { "diagnostic-support-command-v1", "StopCapture",
            admitted.OperationId.ToString("D"), reason.ToString() });

    private static DiagnosticOperationPayload BindAcceptedDiagnosticStop(sqlite3 database,
        DiagnosticOperationPayload operation, StoreDeadline deadline)
    {
        var authorizations = AuditChainDatabase.Read(database,
            "SELECT Sequence,Payload,Hash FROM audit_entries WHERE Kind='IdentityEvent' ORDER BY Sequence;", deadline,
            statement => (Sequence: SqliteNative.ColumnInt64(statement, 0),
                Bytes: SqliteNative.ColumnText(statement, 1)!, Hash: SqliteNative.ColumnText(statement, 2)!));
        var matches = authorizations.Select(row => (row, Fields: DecodeContractIdentityFields(Convert.FromBase64String(row.Bytes))))
            .Where(item => item.Fields.Length >= 49 && item.Fields[2] == nameof(IdentityEventKind.DiagnosticOperationAuthorized) &&
                item.Fields[39] == nameof(AuditedCommandKind.StopDiagnosticCapture) &&
                item.Fields[42] == operation.OperationId.ToString("D")).ToArray();
        DiagnosticOperationStorageCodec.Require(matches.Length <= 1, "StopAuthorityAmbiguous");
        if (matches.Length == 0)
        {
            DiagnosticOperationStorageCodec.Require(operation.StopCommand is null, "StopAuthorityMissing");
            return operation;
        }
        var (audit, fields) = matches[0];
        var commands = AuditChainDatabase.Read(database,
            "SELECT EventId FROM command_facts WHERE CorrelationId=? AND CommandKind=? AND Phase=? AND Disposition=? LIMIT 2;",
            deadline, statement => Guid.Parse(SqliteNative.ColumnText(statement, 0)!), fields[31]!,
            ((int)AuditedCommandKind.StopDiagnosticCapture).ToString(), ((int)CommandAuditPhase.Outcome).ToString(),
            ((int)CommandDisposition.Accepted).ToString());
        DiagnosticOperationStorageCodec.Require(commands.Count == 1, "StopCommandMissing");
        var command = ReadCommandAuditReference(database, commands[0], deadline);
        var reasons = Enum.GetValues<DiagnosticSupportReason>().Where(reason => BuildCaptureStopTarget(operation, reason) == fields[37]).ToArray();
        DiagnosticOperationStorageCodec.Require(reasons.Length == 1, "StopReasonInvalid");
        var bound = operation with
        {
            StopCommand = new() { EventId = commands[0], Sequence = command.Sequence, Hash = command.Hash! },
            StopAuthorization = new() { EventId = Guid.Parse(fields[1]!), Sequence = audit.Sequence, Hash = audit.Hash },
            StopAuthorizationTarget = fields[37], StopReason = reasons[0]
        };
        DiagnosticOperationStorageCodec.Require(operation.StopCommand is null ||
            operation.StopCommand == bound.StopCommand && operation.StopAuthorization == bound.StopAuthorization &&
            operation.StopAuthorizationTarget == bound.StopAuthorizationTarget && operation.StopReason == bound.StopReason,
            "StopEvidenceChanged");
        VerifyDiagnosticSupportOperationAuthority(database, Permission.StartDiagnosticCapture,
            AuditedCommandKind.StopDiagnosticCapture, bound, bound.StopCommand!, bound.StopAuthorization!,
            bound.StopAuthorizationTarget!, admitted: false, deadline);
        return bound;
    }

    private static DiagnosticAuditReference? ReadDiagnosticIdentityAuditReferenceByEventId(sqlite3 database, Guid eventId,
        StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload,Hash
            FROM audit_entries WHERE Kind='IdentityEvent' ORDER BY Sequence;", deadline, statement =>
            (Sequence: SqliteNative.ColumnInt64(statement, 0), Payload: SqliteNative.ColumnText(statement, 2),
                Hash: SqliteNative.ColumnText(statement, 3)));
        foreach (var row in rows)
        {
            var payload = row.Payload;
            var hash = row.Hash;
            if (payload is null || hash is not { Length: 64 }) continue;
            string?[] fields;
            try { fields = DecodeContractIdentityFields(Convert.FromBase64String(payload)); }
            catch (Exception error) when (error is FormatException or InvalidOperationException) { continue; }
            if (fields.Length > 1 && string.Equals(fields[1], eventId.ToString("D"), StringComparison.Ordinal))
                return new DiagnosticAuditReference { EventId = eventId, Sequence = row.Sequence, Hash = hash };
        }
        return null;
    }

    /// <summary>Bounded durable read state of the diagnostic ledger for root and tests.</summary>
    internal async ValueTask<DiagnosticSupportReadState> ReadDiagnosticSupportStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_options.DiagnosticSupport is null)
            return new(false, "DiagnosticSupportConfigurationRequired", null,
                Array.Empty<DiagnosticOperationState>(), 0);
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _options.LocalIdentity is null || _signingKey is null)
            return new(false, initialized.ReasonCode, null, Array.Empty<DiagnosticOperationState>(), 0);
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            RequireConfiguredDiagnosticSupport(database, _options, deadline);
            AuditChainDatabase.RequireFullDiagnosticSupportVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadDiagnosticSupportConfiguration(database, deadline);
            var rows = ReadDiagnosticOperationRows(database, configuration, deadline);
            RequireDiagnosticSupportAuthorityCoverage(database, rows, deadline);
            var tail = AuditChainDatabase.Tail(database, deadline);
            var operations = rows.GroupBy(row => row.Payload.OperationId)
                .Select(group => group.OrderBy(row => row.Payload.AggregateSequence).Last().ToState())
                .OrderBy(value => value.OperationId).ToArray();
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return new DiagnosticSupportReadState(true, "DiagnosticSupportStateAvailable", configuration,
                Array.AsReadOnly(operations), tail.Sequence);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Latest durable state of one operation, or null when it was never admitted.</summary>
    internal async ValueTask<DiagnosticOperationState?> ReadDiagnosticOperationAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("OperationIdRequired", nameof(operationId));
        var state = await ReadDiagnosticSupportStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Available) throw new InvalidOperationException(state.ReasonCode);
        return state.Operations.SingleOrDefault(value => value.OperationId == operationId);
    }

    /// <summary>
    /// Records Interrupted for every preexisting open operation before support admission. Runs
    /// once during real startup inside its own exclusive transaction; a non-configured store
    /// and the non-running maintenance model never record an interruption.
    /// </summary>
    private void RecordDiagnosticSupportInterruptions(sqlite3 database, StoreDeadline deadline)
    {
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredDiagnosticSupport(database, _options, deadline);
            AuditChainDatabase.RequireFullDiagnosticSupportVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadDiagnosticSupportConfiguration(database, deadline);
            var rows = ReadDiagnosticOperationRows(database, configuration, deadline);
            var open = rows.GroupBy(row => row.Payload.OperationId)
                .Select(group => group.OrderBy(row => row.Payload.AggregateSequence).Last())
                .Where(row => row.Payload.Phase is DiagnosticOperationPhase.Admitted or DiagnosticOperationPhase.Sealed)
                .ToArray();
            if (open.Length > 1)
                AuditChainDatabase.EnsureDiagnosticSupportTransactionCapacity(database, _policy!, open.Length, 0, deadline);
            foreach (var row in open)
            {
                var payload = row.Payload with
                {
                    EventId = Guid.NewGuid(),
                    Phase = DiagnosticOperationPhase.Interrupted,
                    AggregateSequence = checked(row.Payload.AggregateSequence + 1),
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    ReasonCode = "DiagnosticSupportRuntimeRestarted"
                };
                if (payload.Kind == DiagnosticOperationKind.Capture)
                    payload = BindAcceptedDiagnosticStop(database, payload, deadline);
                DiagnosticOperationStorageCodec.ValidatePayload(payload, configuration);
                AppendDiagnosticSupportInTransaction(database, configuration, payload, 0, deadline);
            }
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
        }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }
}
