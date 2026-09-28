using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record StationMaintenanceWriteResult(bool Committed, string ReasonCode);

/// <summary>One admitted station-maintenance request, bound by root to its accepted command facts.</summary>
internal sealed record StationMaintenanceAdmission(Guid OperationId, Guid RuntimeEpoch, int ProcessId,
    DateTimeOffset ProcessStartedUtc, StationMaintenanceReason Reason, string ReasonCode,
    string AuthorizationTarget);

/// <summary>One nonhuman failure request: closes an admitted operation that never reached Prepared.</summary>
internal sealed record StationMaintenanceFailure(Guid OperationId, string ReasonCode, DateTimeOffset ObservedAtUtc);

internal sealed class StationMaintenanceWork
{
    internal StationMaintenanceWork(StationMaintenanceFailure failure)
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    internal StationMaintenanceFailure? Failure { get; }
    internal TaskCompletionSource<StationMaintenanceWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Bounded durable read projection; never exposes an audit connection or a writer.</summary>
internal sealed record StationMaintenanceReadState(bool Available, string ReasonCode,
    StationMaintenanceConfiguration? Configuration, IReadOnlyList<StationMaintenanceOperationState> Operations,
    long ThroughAuditSequence);

internal sealed partial class SqliteCommandStore
{
    private sealed record StationMaintenanceAdmissionPlan(long IdentityReserve, long CommandReserve,
        long AdmissionReserve);

    /// <summary>
    /// Validates one admitted mutation against its accepted command fact, its
    /// StationMaintenanceAuthorized identity event, the deployed option binding and the complete
    /// two-fact ledger footprint, all before any fact of the transaction is written.
    /// </summary>
    private StationMaintenanceAdmissionPlan PrepareStationMaintenanceAdmission(sqlite3 database,
        IdentityUpdate update, StoreDeadline deadline)
    {
        var admission = update.StationMaintenance!;
        StationMaintenanceStorageCodec.Require(_options.StationMaintenance is not null, "ConfigurationRequired");
        var eventFact = update.Events.Count == 1 ? update.Events[0] : null;
        var commandFact = update.CommandFacts is { Count: 1 } ? update.CommandFacts[0] : null;
        var principal = eventFact?.PrincipalId ?? Guid.Empty;
        var session = eventFact?.SessionId ?? Guid.Empty;
        var grant = eventFact?.StepUpGrantId ?? Guid.Empty;
        StationMaintenanceStorageCodec.Require(eventFact is not null && commandFact is not null &&
            eventFact.Kind == IdentityEventKind.StationMaintenanceAuthorized &&
            eventFact.OperationId == admission.OperationId && principal != Guid.Empty &&
            session != Guid.Empty && grant != Guid.Empty &&
            eventFact.RequiredPermission == Permission.PrepareStationMaintenance.ToString() &&
            eventFact.CommandCorrelationId == commandFact.CorrelationId &&
            eventFact.ActionTargetId == admission.AuthorizationTarget &&
            eventFact.ActionCommandKind == AuditedCommandKind.PrepareStationMaintenance.ToString() &&
            eventFact.AuthorizationRevision >= 0 &&
            commandFact.Phase == CommandAuditPhase.Outcome &&
            commandFact.Disposition == CommandDisposition.Accepted &&
            commandFact.CommandKind == AuditedCommandKind.PrepareStationMaintenance &&
            commandFact.ReasonCode == admission.ReasonCode &&
            commandFact.ClaimedPrincipalId == principal.ToString("D") &&
            commandFact.ClaimedSessionId == session && commandFact.ClaimedStepUpGrantId == grant &&
            commandFact.AuthenticatedHumanPrincipalId == principal.ToString("D"), "AdmissionShapeInvalid");
        StationMaintenanceStorageCodec.Require(Enum.IsDefined(admission.Reason) &&
            StationMaintenanceStorageCodec.SafeReason(admission.ReasonCode) &&
            StationMaintenanceStorageCodec.Hash(admission.AuthorizationTarget) &&
            admission.RuntimeEpoch != Guid.Empty && admission.OperationId != Guid.Empty &&
            admission.ProcessId == Environment.ProcessId &&
            StationMaintenanceStorageCodec.Utc(admission.ProcessStartedUtc), "AdmissionIdentityInvalid");
        RequireConfiguredStationMaintenance(database, _options, deadline);
        var configuration = StationMaintenanceConfigurationFor(_options);
        StationMaintenanceStorageCodec.Require(configuration.BindingHash ==
            ReadStationMaintenanceConfiguration(database, deadline).BindingHash, "AdmissionConfigurationMismatch");
        StationMaintenanceStorageCodec.Require(admission.ProcessStartedUtc < eventFact!.OccurredAtUtc,
            "AdmissionProcessStartedInvalid");
        var rows = ReadStationMaintenanceRows(database, configuration, deadline);
        StationMaintenanceStorageCodec.Require(!rows.GroupBy(row => row.Payload.OperationId)
            .Any(group => group.Last().Payload.Phase == StationMaintenanceFactPhase.Admitted), "OperationBusy");
        StationMaintenanceStorageCodec.Require(rows.Count + 1 + StationMaintenanceStoreOptions.AdmittedTerminalReserve <=
            configuration.MaximumOperations, "OperationCapacityExceeded");
        var usedBytes = rows.Aggregate(0L, (sum, row) => checked(sum + row.PayloadBytes.Length));
        StationMaintenanceStorageCodec.Require(checked(usedBytes +
            checked((long)(1 + StationMaintenanceStoreOptions.AdmittedTerminalReserve) *
                StationMaintenanceStoreOptions.MaximumPayloadBytesHardLimit)) <= configuration.MaximumTotalBytes,
            "TotalCapacityExceeded");
        AuditChainDatabase.EnsureStationMaintenanceTransactionCapacity(database, _policy!, 3,
            StationMaintenanceStoreOptions.AdmittedTerminalReserve, deadline);
        return new StationMaintenanceAdmissionPlan(
            IdentityReserve: StationMaintenanceStoreOptions.AdmittedTerminalReserve + 2,
            CommandReserve: StationMaintenanceStoreOptions.AdmittedTerminalReserve + 1,
            AdmissionReserve: StationMaintenanceStoreOptions.AdmittedTerminalReserve);
    }

    /// <summary>
    /// Appends the admitted fact after the accepted command fact and its signed authorization
    /// event. The caller already proved the two-fact reserve inside this transaction.
    /// </summary>
    private void AppendStationMaintenanceIdentityMutation(sqlite3 database, IdentityUpdate update,
        IdentityWork work, long identitySequence, StoreDeadline deadline)
    {
        var admission = update.StationMaintenance!;
        var commandFact = update.CommandFacts![0];
        var eventFact = update.Events[0];
        var configuration = ReadStationMaintenanceConfiguration(database, deadline);
        var commandReference = ReadCommandAuditReference(database, commandFact.EventId, deadline);
        var authorizationReference = ReadIdentityAuditReference(database, identitySequence, deadline);
        StationMaintenanceStorageCodec.Require(commandReference.Sequence > 0 &&
            commandReference.Hash is { Length: 64 } && authorizationReference.Sequence > 0 &&
            authorizationReference.Hash is { Length: 64 }, "AdmissionAuditReferenceMissing");
        var observed = eventFact.OccurredAtUtc;
        var payload = new StationMaintenancePayload
        {
            EventId = Guid.NewGuid(), OperationId = admission.OperationId,
            CommandCorrelationId = commandFact.CorrelationId, RuntimeEpoch = admission.RuntimeEpoch,
            Phase = StationMaintenanceFactPhase.Admitted, AggregateSequence = 1, ObservedAtUtc = observed,
            Reason = admission.Reason, ReasonCode = admission.ReasonCode,
            ActorPrincipalId = eventFact.PrincipalId!.Value, SessionId = eventFact.SessionId!.Value,
            StepUpGrantId = eventFact.StepUpGrantId!.Value,
            AuthorizationRevision = eventFact.AuthorizationRevision,
            AuthorizationTarget = admission.AuthorizationTarget,
            AuthorizationPolicyId = _options.LocalIdentity!.AuthorizationPolicy.Id,
            AuthorizationPolicyVersion = _options.LocalIdentity.AuthorizationPolicy.Version,
            AuthorizationPolicyHash = _options.LocalIdentity.AuthorizationPolicy.ContentHash,
            PolicyHash = configuration.PolicyHash, ConfigurationHash = configuration.OptionsHash,
            InstallationBindingHash = configuration.InstallationBindingHash,
            RuntimeApplicationHash = configuration.RuntimeApplicationHash,
            ProcessId = admission.ProcessId, ProcessStartedUtc = admission.ProcessStartedUtc,
            ExpiresAtUtc = observed + _options.StationMaintenance!.Policy.HandoffLifetime,
            AdmittedDeadlineUtc = observed + _options.StationMaintenance.Policy.RetirementTimeout,
            Command = new StationMaintenanceAuditReference { EventId = commandFact.EventId,
                Sequence = commandReference.Sequence, Hash = commandReference.Hash! },
            Authorization = new StationMaintenanceAuditReference { EventId = eventFact.EventId,
                Sequence = authorizationReference.Sequence, Hash = authorizationReference.Hash! }
        };
        var row = AppendStationMaintenanceInTransaction(database, configuration, payload,
            StationMaintenanceStoreOptions.AdmittedTerminalReserve, deadline);
        work.Result = row;
    }

    /// <summary>
    /// Records one bounded Failed fact for an admitted operation that never reached Prepared.
    /// The call is a durable writer request: a completed channel refuses it without a fake
    /// completion.
    /// </summary>
    internal ValueTask<StationMaintenanceWriteResult> RecordMaintenanceFailureAsync(Guid operationId,
        string reason, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("OperationIdRequired", nameof(operationId));
        ArgumentNullException.ThrowIfNull(reason);
        var work = new StationMaintenanceWork(new StationMaintenanceFailure(operationId, reason, DateTimeOffset.UtcNow));
        return AppendStationMaintenanceAsync(work, cancellationToken);
    }

    private async ValueTask<StationMaintenanceWriteResult> AppendStationMaintenanceAsync(
        StationMaintenanceWork work, CancellationToken cancellationToken)
    {
        if (_options.StationMaintenance is null || _queue is null || _queueSlots is null ||
            Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _sealed) != 0 || _worker.IsCompleted)
            return new(false, "StationMaintenanceStoreUnavailable");
        var deadline = new StoreDeadline(CommitTimeout);
        try
        {
            SqliteNative.EnsureDeadline(deadline, cancellationToken);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero ||
                !await _queueSlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                return new(false, "StationMaintenanceCommitDeadlineExceeded");
            if (!_queue.Writer.TryWrite(new WriteRequest(null, deadline, StationMaintenance: work)))
            {
                _queueSlots.Release();
                return new(false, "StationMaintenanceStoreUnavailable");
            }
            return await work.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TimeoutException) { return new(false, "StationMaintenanceCommitDeadlineExceeded"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "StationMaintenanceWriteUnavailable")); }
    }

    private StoreWriteResult AppendStationMaintenanceFailureCore(sqlite3 database, StationMaintenanceWork work,
        StoreDeadline deadline)
    {
        if (Integrity?.State == AuditIntegrityState.Faulted) return new(false, Integrity.ReasonCode);
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredStationMaintenance(database, _options, deadline);
            AuditChainDatabase.RequireFullStationMaintenanceVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadStationMaintenanceConfiguration(database, deadline);
            var rows = ReadStationMaintenanceRows(database, configuration, deadline);
            var failure = work.Failure!;
            StationMaintenanceStorageCodec.Require(StationMaintenanceStorageCodec.SafeReason(failure.ReasonCode) &&
                StationMaintenanceStorageCodec.Utc(failure.ObservedAtUtc), "FailureRequestInvalid");
            var history = rows.Where(row => row.Payload.OperationId == failure.OperationId)
                .OrderBy(row => row.Payload.AggregateSequence).ToArray();
            StationMaintenanceStorageCodec.Require(history.Length > 0, "OperationMissing");
            var previous = history[^1];
            StationMaintenanceStorageCodec.Require(previous.Payload.Phase ==
                StationMaintenanceFactPhase.Admitted, "OperationTransitionInvalid");
            var payload = BuildStationMaintenanceTerminalPayload(previous, StationMaintenanceFactPhase.Failed,
                failure.ReasonCode, failure.ObservedAtUtc);
            var historyWithCandidate = rows.Append(new StationMaintenanceStoredRow(rows.Count + 1, payload,
                Array.Empty<byte>(), new string('0', 64), 0, new string('0', 64))).ToArray();
            ValidateStationMaintenanceHistory(configuration, historyWithCandidate);
            AuditChainDatabase.EnsureStationMaintenanceTransactionCapacity(database, _policy!, 1, 0, deadline);
            var row = AppendStationMaintenanceInTransaction(database, configuration, payload, 0, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "StationMaintenanceFailureRecorded"));
            return new(true, "StationMaintenanceFailureRecorded");
        }
        catch (TimeoutException) { return new(false, "StationMaintenanceCommitDeadlineExceeded"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(false, SqliteAuditIntegrityQuery.FaultReason(error, "StationMaintenanceWriteFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    /// <summary>One Prepared or Failed successor of an admitted fact; the binding never changes.</summary>
    private static StationMaintenancePayload BuildStationMaintenanceTerminalPayload(
        StationMaintenanceStoredRow previous, StationMaintenanceFactPhase phase, string reasonCode,
        DateTimeOffset observedAtUtc) => previous.Payload with
    {
        EventId = Guid.NewGuid(),
        Phase = phase,
        AggregateSequence = checked(previous.Payload.AggregateSequence + 1),
        ObservedAtUtc = observedAtUtc,
        ReasonCode = reasonCode,
        Previous = new StationMaintenanceAuditReference { EventId = previous.Payload.EventId,
            Sequence = previous.AuditSequence, Hash = previous.AuditHash },
        PreviousContentHash = previous.ContentHash
    };

    /// <summary>
    /// Writes one maintenance fact together with its signed central-audit metadata entry inside
    /// the caller's transaction. The caller has already reserved the exact remaining footprint.
    /// Returns the exact audit sequence and hash of the new fact.
    /// </summary>
    private StationMaintenanceStoredRow AppendStationMaintenanceInTransaction(sqlite3 database,
        StationMaintenanceConfiguration configuration, StationMaintenancePayload payload, long reserve,
        StoreDeadline deadline)
    {
        var payloadBytes = StationMaintenanceStorageCodec.Encode(payload, configuration);
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM station_maintenance_facts;", deadline) + 1);
        StationMaintenanceStorageCodec.Require(position <= configuration.MaximumOperations,
            "OperationCapacityExceeded");
        var used = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM station_maintenance_facts;", deadline);
        StationMaintenanceStorageCodec.Require(used + payloadBytes.Length <= configuration.MaximumTotalBytes,
            "TotalCapacityExceeded");
        var contentHash = StationMaintenanceStorageCodec.ContentHash(position, configuration.BindingHash,
            payloadBytes);
        var nextAudit = checked(AuditChainDatabase.Tail(database, deadline).Sequence + 1);
        var audit = AuditChainDatabase.AppendStationMaintenanceEvent(database, _policy!, _signingKey!,
            StationMaintenanceStorageCodec.AuditBinding(position, configuration.BindingHash, payloadBytes),
            reserve, deadline);
        StationMaintenanceStorageCodec.Require(audit.Sequence == nextAudit, "AuditSequenceChanged");
        AuditChainDatabase.Execute(database, @"INSERT INTO station_maintenance_facts(Position,EventId,
            OperationId,Phase,Payload,ContentHash,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?);", deadline,
            StationMaintenanceNumber(position), payload.EventId.ToString("D"),
            payload.OperationId.ToString("D"), StationMaintenanceNumber((long)payload.Phase),
            System.Text.Encoding.UTF8.GetString(payloadBytes), contentHash,
            StationMaintenanceNumber(audit.Sequence), audit.Hash);
        return new StationMaintenanceStoredRow(position, payload, payloadBytes, contentHash,
            audit.Sequence, audit.Hash);
    }

    /// <summary>Bounded durable read state of the maintenance ledger for root and tests.</summary>
    internal async ValueTask<StationMaintenanceReadState> ReadStationMaintenanceStateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_options.StationMaintenance is null)
            return new(false, "StationMaintenanceConfigurationRequired", null,
                Array.Empty<StationMaintenanceOperationState>(), 0);
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _options.LocalIdentity is null || _signingKey is null)
            return new(false, initialized.ReasonCode, null, Array.Empty<StationMaintenanceOperationState>(), 0);
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            var database = connection.Handle!;
            SqliteNative.ConfigureSqliteLimit(database, _options);
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            RequireConfiguredStationMaintenance(database, _options, deadline);
            AuditChainDatabase.RequireFullStationMaintenanceVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadStationMaintenanceConfiguration(database, deadline);
            var rows = ReadStationMaintenanceRows(database, configuration, deadline);
            RequireStationMaintenanceAuthorityCoverage(database, rows, deadline);
            var tail = AuditChainDatabase.Tail(database, deadline);
            var operations = rows.GroupBy(row => row.Payload.OperationId)
                .Select(group => group.OrderBy(row => row.Payload.AggregateSequence).Last().ToState())
                .OrderBy(value => value.OperationId).ToArray();
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return new StationMaintenanceReadState(true, "StationMaintenanceStateAvailable", configuration,
                Array.AsReadOnly(operations), tail.Sequence);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Latest durable state of one operation, or null when it was never admitted.</summary>
    internal async ValueTask<StationMaintenanceOperationState?> ReadStationMaintenanceOperationAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("OperationIdRequired", nameof(operationId));
        var state = await ReadStationMaintenanceStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Available) throw new InvalidOperationException(state.ReasonCode);
        return state.Operations.SingleOrDefault(value => value.OperationId == operationId);
    }

    /// <summary>
    /// Records Failed for every preexisting admitted operation before maintenance admission.
    /// Runs once during real startup inside its own exclusive transaction; a non-configured
    /// store and the non-running maintenance model never record an interruption.
    /// </summary>
    private void RecordStationMaintenanceInterruptions(sqlite3 database, StoreDeadline deadline)
    {
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredStationMaintenance(database, _options, deadline);
            AuditChainDatabase.RequireFullStationMaintenanceVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadStationMaintenanceConfiguration(database, deadline);
            var rows = ReadStationMaintenanceRows(database, configuration, deadline);
            var open = rows.GroupBy(row => row.Payload.OperationId)
                .Select(group => group.OrderBy(row => row.Payload.AggregateSequence).Last())
                .Where(row => row.Payload.Phase == StationMaintenanceFactPhase.Admitted)
                .ToArray();
            if (open.Length > 1)
                AuditChainDatabase.EnsureStationMaintenanceTransactionCapacity(database, _policy!, open.Length, 0, deadline);
            foreach (var row in open)
            {
                var payload = BuildStationMaintenanceTerminalPayload(row, StationMaintenanceFactPhase.Failed,
                    "StationMaintenanceRuntimeRestarted", DateTimeOffset.UtcNow);
                AppendStationMaintenanceInTransaction(database, configuration, payload, 0, deadline);
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
