using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The dedicated Runtime writer lane for the schema-32 arm ledger. The append
/// runs inside the single writer transaction, re-verifies the signed chain,
/// re-reads the durable heads it is about to bind, and validates every source
/// gate before the row and its metadata audit entry commit together.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    internal async ValueTask<ProductionArmWriteResult> AppendProductionArmEventAsync(
        ProductionArmWriteRequest request, StoreDeadline deadline, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deadline);
        if (!ProductionArmingEnabled) return new(false, "ProductionArmConfigurationRequired");
        if (Volatile.Read(ref _disposed) != 0) return new(false, "TraceStoreDisposed");
        if (_queue is null || _queueSlots is null) return new(false, _initializationReason);
        if (_worker.IsCompleted) return new(false, "TraceStoreUnavailable");
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new(false, integrity?.ReasonCode ?? "ProductionArmAuditUnavailable");
        SqliteNative.EnsureDeadline(deadline, token);
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) return new(false, "ProductionArmCommitDeadlineExceeded");
        if (!await _queueSlots.WaitAsync(remaining, token).ConfigureAwait(false))
            return new(false, "ProductionArmCommitDeadlineExceeded");
        var work = new ProductionArmWork(request);
        if (!_queue.Writer.TryWrite(new WriteRequest(null, deadline, ProductionArm: work)))
        {
            _queueSlots.Release();
            return new(false, "ProductionArmWriterUnavailable");
        }

        // Once admitted, cancellation cannot turn a committed append into a
        // caller-visible failure.
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendProductionArmCore(sqlite3 database, ProductionArmWork work,
        StoreDeadline deadline)
    {
        var options = _options.ProductionArming ??
            throw new InvalidOperationException("ProductionArmConfigurationRequired");
        if (Integrity?.State == AuditIntegrityState.Faulted) return new(false, Integrity.ReasonCode);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes) return new(false, "TraceStoreWalLimit");
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            VerifyProductionArmReadGuard(database, _options, deadline);
            var rows = ReadProductionArmRows(database, options, deadline);
            var value = BuildProductionArmEvent(database, rows, work.Request, deadline);
            var remaining = ProductionArmRemaining(rows.Select(row => row.Event).Append(value).ToArray());
            if (checked(rows.Count + 1L + remaining) > options.MaxEvents)
                throw new InvalidOperationException("ProductionArmEntryCapacityExceeded");
            var payload = ProductionArmStorageCodec.Encode(value);
            if (payload.Length > options.MaximumPayloadBytes)
                throw new InvalidOperationException("ProductionArmPayloadCapacityExceeded");
            var used = rows.Aggregate(0L, (sum, row) => checked(sum + row.Payload.Length));
            if (checked(used + payload.Length + remaining * (long)options.MaximumPayloadBytes) > options.MaxTotalBytes)
                throw new InvalidOperationException("ProductionArmTotalCapacityExceeded");
            var provisional = new ProductionArmStoredRow(value, rows.LastOrDefault()?.AuditHash,
                Convert.ToHexString(SHA256.HashData(payload)), payload, 0, string.Empty);
            var audit = AuditChainDatabase.AppendProductionArmMetadata(database, _policy!, _signingKey!,
                ProductionArmEventAuditKind, ProductionArmStorageCodec.EncodeAuditBinding(provisional), options, deadline,
                remaining);
            var row = provisional with { AuditSequence = audit.Sequence, AuditHash = audit.Hash };
            AuditChainDatabase.Execute(database, @"INSERT INTO production_arm_events
                (Position,PreviousHash,EventId,AttemptId,RuntimeEpoch,Cause,Kind,RequestIdentityHash,HumanCommandId,
                 ContentHash,PayloadHash,Payload,AuditSequence,AuditHash) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
                N(value.Position), row.PreviousHash, value.EventId.ToString("D"), value.AttemptId.ToString("D"),
                value.RuntimeEpoch.ToString("D"), ((int)value.Cause).ToString(CultureInfo.InvariantCulture),
                ((int)value.Kind).ToString(CultureInfo.InvariantCulture), value.PlcRequest?.RequestIdentityHash,
                value.HumanCommandId?.ToString("D"), value.ContentHash, row.PayloadHash,
                Convert.ToBase64String(payload), N(row.AuditSequence), row.AuditHash);
            ValidateProductionArmHistory(database, ProductionArmSource(_options), options,
                rows.Append(row).ToArray(), deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new ProductionArmWriteResult(true, "ProductionArmEventPersisted", value));
            Interlocked.Exchange(ref _lastCommittedAuditSequence, audit.Sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying,
                _policy!.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, "ProductionArmEventPersisted");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = SqliteAuditIntegrityQuery.FaultReason(exception, "ProductionArmCommitFailed");
            work.Completion.TrySetResult(new ProductionArmWriteResult(false, reason));
            return new(false, reason);
        }
        finally { if (started && !committed) Rollback(database); }
    }

    private ProductionArmHistoryEvent BuildProductionArmEvent(sqlite3 database,
        IReadOnlyList<ProductionArmStoredRow> rows, ProductionArmWriteRequest request, StoreDeadline deadline)
    {
        var events = rows.Select(row => row.Event).ToArray();
        if (request.AttemptId == Guid.Empty || request.RuntimeEpoch == Guid.Empty || request.AdmissionGeneration < 0 ||
            !Enum.IsDefined(request.Cause) || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Reason))
            throw new InvalidOperationException("ProductionArmAttemptIdentityInvalid");
        if (!string.Equals(ReadContractStationId(database, deadline), request.StationId, StringComparison.Ordinal))
            throw new InvalidOperationException("ProductionArmStationMismatch");
        if (request.StartupPolicy is null || request.PostActivationPolicy is null)
            throw new InvalidOperationException("ProductionArmPolicyRequired");
        var manual = request.Cause == ProductionArmCause.ManualMaintenanceArm;
        if (manual)
        {
            if (request.HumanCommandId is null || request.HumanPrincipalId is null || request.HumanSessionId is null)
                throw new InvalidOperationException("ProductionArmHumanActorRequired");
        }
        else if (request.HumanCommandId is not null || request.HumanPrincipalId is not null ||
            request.HumanSessionId is not null)
        {
            throw new InvalidOperationException("ProductionArmHumanActorConflict");
        }
            if (request.Cause == ProductionArmCause.PlcActivation)
            {
                if (request.PlcRequest is null)
                    throw new InvalidOperationException("ProductionArmPlcRequestRequired");
            if (request.PlcRequest.RuntimeEpoch != request.RuntimeEpoch)
                throw new InvalidOperationException("ProductionArmPlcRequestEpochMismatch");
            if ((request.Kind is ProductionArmEventKind.Authorized or ProductionArmEventKind.ReadyConfirmed) &&
                request.Activation is null)
                throw new InvalidOperationException("ProductionArmPlcActivationRequired");
        }
        else if (request.PlcRequest is not null || request.Activation is not null)
        {
            throw new InvalidOperationException("ProductionArmSourceConflict");
        }

        var attempt = events.Where(value => value.AttemptId == request.AttemptId).ToArray();
        if (request.Kind == ProductionArmEventKind.Attempted)
        {
            if (attempt.Length != 0) throw new InvalidOperationException("ProductionArmAttemptAlreadyRecorded");
            // A restart never retries the same cause identity: the unique
            // start-up epoch, PLC request identity, and human command id are
            // checked across the complete ledger, not only the same attempt.
            if (request.Cause == ProductionArmCause.Startup && events.Any(value =>
                    value.Cause == ProductionArmCause.Startup &&
                    value.Kind == ProductionArmEventKind.Attempted && value.RuntimeEpoch == request.RuntimeEpoch))
                throw new InvalidOperationException("ProductionArmStartupEpochReused");
            if (request.Cause == ProductionArmCause.PlcActivation && events.Any(value =>
                    value.Cause == ProductionArmCause.PlcActivation &&
                    value.Kind == ProductionArmEventKind.Attempted && string.Equals(
                        value.PlcRequest?.RequestIdentityHash, request.PlcRequest!.RequestIdentityHash,
                        StringComparison.Ordinal)))
                throw new InvalidOperationException("ProductionArmPlcRequestReused");
            if (manual && events.Any(value => value.Kind == ProductionArmEventKind.Attempted &&
                    value.HumanCommandId == request.HumanCommandId))
                throw new InvalidOperationException("ProductionArmHumanCommandReused");
        }
        else
        {
            if (attempt.Length == 0 || attempt[0].Kind != ProductionArmEventKind.Attempted)
                throw new InvalidOperationException("ProductionArmAttemptMissing");
            var first = attempt[0];
            if (first.Cause != request.Cause || first.RuntimeEpoch != request.RuntimeEpoch ||
                first.StartupPolicy != request.StartupPolicy ||
                first.PostActivationPolicy != request.PostActivationPolicy ||
                first.DeploymentHash != request.DeploymentHash ||
                first.AdmissionGeneration != request.AdmissionGeneration ||
                !string.Equals(first.StationId, request.StationId, StringComparison.Ordinal) ||
                !string.Equals(first.PlcRequest?.ContentHash, request.PlcRequest?.ContentHash,
                    StringComparison.Ordinal) ||
                !string.Equals(first.PlcRequest?.RequestIdentityHash, request.PlcRequest?.RequestIdentityHash,
                    StringComparison.Ordinal) ||
                first.HumanCommandId != request.HumanCommandId ||
                first.HumanPrincipalId != request.HumanPrincipalId ||
                first.HumanSessionId != request.HumanSessionId)
                throw new InvalidOperationException("ProductionArmAttemptContextChanged");
            var transitionError = ProductionArmTransitionError(attempt, request.Kind, request.Reason);
            if (transitionError is not null) throw new InvalidOperationException(transitionError);
            switch (request.Kind)
            {
                case ProductionArmEventKind.Authorized:
                    ValidateProductionArmAuthorization(database, ProductionArmSource(_options), request, events, deadline);
                    break;
                case ProductionArmEventKind.ReadyConfirmed:
                    var authorized = attempt.Last(value => value.Kind == ProductionArmEventKind.Authorized);
                    if (request.MaintenanceHeadHash is null || !string.Equals(request.MaintenanceHeadHash,
                            authorized.MaintenanceHeadHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("ProductionArmMaintenanceEvidenceChanged");
                    if (request.Report is not null && request.Report.ContentHash != authorized.Report?.ContentHash)
                        throw new InvalidOperationException("ProductionArmReportChanged");
                    ValidateProductionArmHeads(database, request.CurrentDurableHeads, deadline);
                    if (request.Cause == ProductionArmCause.PlcActivation)
                        ValidateProductionArmPlcHandshake(database, ProductionArmSource(_options), request.RuntimeEpoch,
                            request.PlcRequest!, request.Activation!, deadline);
                    break;
                case ProductionArmEventKind.Rejected:
                case ProductionArmEventKind.Failed:
                case ProductionArmEventKind.PlcStatusDelivered:
                case ProductionArmEventKind.PlcStatusUndelivered:
                    break;
                default:
                    throw new InvalidOperationException("ProductionArmTransitionInvalid");
            }
        }

        var time = DateTimeOffset.UtcNow;
        if (events.Length > 0 && time < events[^1].RecordedAtUtc) time = events[^1].RecordedAtUtc;
        return new ProductionArmHistoryEvent(rows.Count + 1L, Guid.NewGuid(), request.AttemptId, request.RuntimeEpoch,
            request.Cause, request.Kind, request.StationId, request.StartupPolicy, request.PostActivationPolicy,
            request.DeploymentHash, request.PlcRequest, request.Activation, request.MaintenanceHeadHash,
            request.AdmissionGeneration, request.Report, request.ExpectedDurableHeads, request.CurrentDurableHeads,
            request.Reason, request.ReasonCode, request.HumanCommandId, request.HumanPrincipalId,
            request.HumanSessionId, time);
    }

    /// <summary>
    /// The bounded attempt state machine. A terminal state is closed for state
    /// changes; exactly one optional status-delivery observation may follow it.
    /// </summary>
    internal static string? ProductionArmTransitionError(IReadOnlyList<ProductionArmHistoryEvent> attempt,
        ProductionArmEventKind next, ProductionArmReason reason)
    {
        if (attempt.Count == 0)
            return next == ProductionArmEventKind.Attempted ? null : "ProductionArmAttemptMissing";
        var last = attempt[^1];
        if (next == ProductionArmEventKind.Attempted) return "ProductionArmAttemptAlreadyRecorded";
        if (last.Terminal && next is not (ProductionArmEventKind.PlcStatusDelivered or
                ProductionArmEventKind.PlcStatusUndelivered))
            return "ProductionArmTerminalClosed";
        switch (next)
        {
            case ProductionArmEventKind.Authorized:
                if (last.Kind != ProductionArmEventKind.Attempted ||
                    attempt.Any(value => value.Kind == ProductionArmEventKind.Authorized))
                    return "ProductionArmTransitionInvalid";
                return null;
            case ProductionArmEventKind.ReadyConfirmed:
                if (last.Kind != ProductionArmEventKind.Authorized ||
                    attempt.Any(value => value.Kind == ProductionArmEventKind.ReadyConfirmed))
                    return "ProductionArmTransitionInvalid";
                return null;
            case ProductionArmEventKind.Rejected:
            case ProductionArmEventKind.Failed:
                if (last.Kind is not (ProductionArmEventKind.Attempted or ProductionArmEventKind.Authorized) ||
                    reason == ProductionArmReason.None)
                    return "ProductionArmReasonRequired";
                return null;
            case ProductionArmEventKind.PlcStatusDelivered:
            case ProductionArmEventKind.PlcStatusUndelivered:
                if (!last.Terminal || attempt.Any(value => value.StatusDelivery))
                    return "ProductionArmStatusWithoutTerminal";
                return null;
            default:
                return "ProductionArmTransitionInvalid";
        }
    }

    private static void ValidateProductionArmAuthorization(sqlite3 database, ProductionArmSourceOptions source,
        ProductionArmWriteRequest request, IReadOnlyList<ProductionArmHistoryEvent> events, StoreDeadline deadline)
    {
        if (request.Report is null || !request.Report.CanArm)
            throw new InvalidOperationException("ProductionArmAdmissionGateBlocked");
        if (request.Report.RuntimeEpoch != request.RuntimeEpoch ||
            request.Report.AdmissionGeneration != request.AdmissionGeneration)
            throw new InvalidOperationException("ProductionArmReportContextMismatch");
        if (request.MaintenanceHeadHash is null)
            throw new InvalidOperationException("ProductionArmMaintenanceEvidenceUnavailable");
        if (!ProductionArmHeads.Equal(request.ExpectedDurableHeads, request.CurrentDurableHeads))
            throw new InvalidOperationException("ProductionArmDurableHeadsMismatch");
        ValidateProductionArmHeads(database, request.CurrentDurableHeads, deadline);
        if (request.Cause == ProductionArmCause.Startup)
        {
            if (events.Any(value => value.Cause == ProductionArmCause.Startup &&
                    value.RuntimeEpoch == request.RuntimeEpoch &&
                    value.Kind == ProductionArmEventKind.Authorized))
                throw new InvalidOperationException("ProductionArmStartupEpochReused");
            return;
        }
        if (request.Cause == ProductionArmCause.PlcActivation)
        {
            ValidateProductionArmPlcHandshake(database, source, request.RuntimeEpoch, request.PlcRequest!,
                request.Activation!, deadline);
            return;
        }
        ValidateProductionArmHumanAuthorization(database, request.HumanCommandId!.Value,
            request.HumanPrincipalId!.Value, request.HumanSessionId!.Value, deadline);
    }

    /// <summary>
    /// Authorized durable heads are re-read under the writer transaction; a
    /// material source change between evaluation and append fails closed.
    /// </summary>
    private static void ValidateProductionArmHeads(sqlite3 database,
        IReadOnlyDictionary<string, string> expected, StoreDeadline deadline)
    {
        var actual = ReadProductionAdmissionDurableHeads(database, ReadIdentityState(database, deadline), deadline);
        if (!ProductionArmHeads.Equal(expected, actual))
            throw new InvalidOperationException("ProductionArmDurableHeadsChanged");
    }

    /// <summary>
    /// A PLC-caused arm is authorized only by the actual signed schema-31
    /// handshake: one observed request, one successful DecisionCommitted for
    /// the same exact request and activation, one ResetObserved, and the exact
    /// terminal succeeded activation record. A bare request observation is
    /// never sufficient.
    /// </summary>
    internal static void ValidateProductionArmPlcHandshake(sqlite3 database, ProductionArmSourceOptions source,
        Guid runtimeEpoch, RecipeChangeRequestEvidence evidence, RecipeActivationReference activation,
        StoreDeadline deadline)
    {
        if (evidence.RuntimeEpoch != runtimeEpoch)
            throw new InvalidOperationException("ProductionArmPlcRequestEpochMismatch");
        var selectionOptions = source.Selections ??
            throw new InvalidOperationException("ProductionArmPlcHandshakeUnavailable");
        var activationOptions = source.Activations ??
            throw new InvalidOperationException("ProductionArmPlcHandshakeUnavailable");
        var episode = ReadRecipeChangeRows(database, selectionOptions, deadline).Select(row => row.Event)
            .Where(value => string.Equals(value.Request.RequestIdentityHash, evidence.RequestIdentityHash,
                StringComparison.Ordinal)).ToArray();
        var observed = episode.Where(value => value.Kind == RecipeChangeEventKind.RequestObserved).ToArray();
        if (observed.Length != 1 || observed[0].Request.ContentHash != evidence.ContentHash ||
            observed[0].Request.OperationId != evidence.OperationId)
            throw new InvalidOperationException("ProductionArmPlcRequestMismatch");
        var decision = episode.SingleOrDefault(value => value.Kind == RecipeChangeEventKind.DecisionCommitted);
        if (decision is null || decision.Outcome != RecipeChangeOutcome.Succeeded ||
            decision.Reason != RecipeChangeReason.None || decision.Activation != activation)
            throw new InvalidOperationException("ProductionArmHandshakeIncomplete");
        if (!episode.Any(value => value.Kind == RecipeChangeEventKind.ResetObserved))
            throw new InvalidOperationException("ProductionArmHandshakeIncomplete");
        var record = ReadRecipeActivationRows(database, activationOptions, deadline,
                CreateCalibrationProfileResolver(database, source.Governance, deadline))
            .Select(row => row.Record)
            .SingleOrDefault(value => value.Position == activation.Position &&
                value.ActivationId == activation.ActivationId &&
                string.Equals(value.ContentHash, activation.ContentHash, StringComparison.Ordinal));
        if (record is null || !record.IsTerminal || !record.Outcome.Succeeded ||
            record.OperationId != evidence.OperationId ||
            record.Actor is not { IsPlcAdapter: true, PlcRequestContext: { } context } ||
            !context.Matches(evidence.ActivationContext()))
            throw new InvalidOperationException("ProductionArmHandshakeIncomplete");
    }

    /// <summary>
    /// A manual maintenance arm carries the actual authenticated human. The
    /// binding is the completed schema-22 Arm command and its signed identity
    /// event; a session or principal is never fabricated for a Runtime arm.
    /// </summary>
    private static void ValidateProductionArmHumanAuthorization(sqlite3 database, Guid commandId,
        Guid principalId, Guid sessionId, StoreDeadline deadline)
    {
        var station = ReadContractStationId(database, deadline);
        var facts = AuditChainDatabase.Read(database, @"SELECT Position,EventId,AttemptId,CorrelationId,RuntimeEpoch,
            CommandKind,Phase,Disposition,ClaimedPrincipalId,ClaimedSessionId,SystemPrincipalId
            FROM command_facts WHERE EventId=? LIMIT 2;", deadline, value => new
        {
            Position = SqliteNative.ColumnInt64(value, 0),
            EventId = SqliteNative.ColumnText(value, 1),
            CommandKind = SqliteNative.ColumnInt64(value, 5),
            Phase = SqliteNative.ColumnInt64(value, 6),
            Disposition = SqliteNative.ColumnText(value, 7),
            ClaimedPrincipalId = SqliteNative.ColumnText(value, 8),
            ClaimedSessionId = SqliteNative.ColumnText(value, 9),
            SystemPrincipalId = SqliteNative.ColumnText(value, 10)
        }, commandId.ToString("D"));
        if (facts.Count != 1 || facts[0].CommandKind != (long)AuditedCommandKind.ArmProduction ||
            facts[0].Phase != (long)CommandAuditPhase.Completed || facts[0].Disposition is not null ||
            !string.Equals(facts[0].ClaimedPrincipalId, principalId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(facts[0].ClaimedSessionId, sessionId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(facts[0].SystemPrincipalId, SystemPrincipalId.Runtime, StringComparison.Ordinal))
            throw new InvalidOperationException("ProductionArmHumanAuthorizationMissing");
        var commandAudit = AuditChainDatabase.Read(database, @"SELECT Sequence,Hash FROM audit_entries
            WHERE Kind='CommandFact' AND FactPosition=? LIMIT 2;", deadline,
            value => (Sequence: SqliteNative.ColumnInt64(value, 0),
                Hash: SqliteNative.ColumnText(value, 1) ?? string.Empty), N(facts[0].Position));
        if (commandAudit.Count != 1)
            throw new InvalidOperationException("ProductionArmHumanAuthorizationMissing");
        var identities = AuditChainDatabase.Read(database, @"SELECT Sequence,IdentityPosition,Payload FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL ORDER BY Sequence;", deadline,
            value => (Position: SqliteNative.ColumnInt64(value, 1),
                Sequence: SqliteNative.ColumnInt64(value, 0),
                Payload: SqliteNative.ColumnText(value, 2) ?? string.Empty));
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        var matches = 0;
        foreach (var identity in identities)
        {
            string?[]? fields;
            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(identity.Payload);
                fields = DecodeContractIdentityFields(payload);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or
                EndOfStreamException or DecoderFallbackException) { continue; }
            if (fields is not { Length: 49 } ||
                !string.Equals(fields[1], commandId.ToString("D"), StringComparison.Ordinal)) continue;
            IdentityAuditEvent.VerifyPayload(payload, identity.Position, station, schemaVersion);
            if (fields[2] != IdentityEventKind.ProductionAdmissionCompleted.ToString() ||
                !string.Equals(fields[4], station, StringComparison.Ordinal) ||
                !string.Equals(fields[25], sessionId.ToString("D"), StringComparison.Ordinal) ||
                !string.Equals(fields[30], principalId.ToString("D"), StringComparison.Ordinal))
                throw new InvalidOperationException("ProductionArmHumanAuthorizationMismatch");
            matches++;
        }
        if (matches != 1) throw new InvalidOperationException("ProductionArmHumanAuthorizationMissing");
    }
}
