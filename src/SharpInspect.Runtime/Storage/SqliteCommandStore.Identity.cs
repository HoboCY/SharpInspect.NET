using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private const string IdentitySchemaSql = @"
        CREATE TABLE identity_policy_binding(Id INTEGER PRIMARY KEY CHECK(Id=1), StationId TEXT NOT NULL,
            InstallationKeyId TEXT NOT NULL, PolicyContentHash TEXT NOT NULL);
        CREATE TABLE identity_authority(Id INTEGER PRIMARY KEY CHECK(Id=1), Revision INTEGER NOT NULL CHECK(Revision>=0),
            ProtectedState TEXT NOT NULL, LastAuditSequence INTEGER NOT NULL, StateSignature TEXT NOT NULL);
        CREATE TRIGGER identity_policy_immutable_update BEFORE UPDATE ON identity_policy_binding BEGIN SELECT RAISE(ABORT,'ImmutableIdentityPolicy'); END;
        CREATE TRIGGER identity_policy_immutable_delete BEFORE DELETE ON identity_policy_binding BEGIN SELECT RAISE(ABORT,'ImmutableIdentityPolicy'); END;
        CREATE TRIGGER identity_authority_no_delete BEFORE DELETE ON identity_authority BEGIN SELECT RAISE(ABORT,'IdentityAuthorityRequired'); END;";

    private void InitializeIdentitySchema(sqlite3 database, StoreDeadline deadline)
    {
        SqliteNative.Execute(database, IdentitySchemaSql, deadline);
        var identity = _options.LocalIdentity!;
        AuditChainDatabase.Execute(database, "INSERT INTO identity_policy_binding VALUES(1,?,?,?);", deadline,
            identity.StationId, _signingKey!.KeyId, identity.PolicyContentHash);
        var state = new IdentityAuthorityState { StationId = identity.StationId, InstallationKeyId = _signingKey.KeyId,
            PolicyContentHash = identity.PolicyContentHash };
        var fact = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.IdentityConfigured, DateTimeOffset.UtcNow,
            state.StationId, null, null, null, null, "IdentityBootstrapRequired", PasswordPolicyVersion: identity.PasswordPolicy.Version,
            BlocklistId: identity.PasswordPolicy.Blocklist!.Id, BlocklistVersion: identity.PasswordPolicy.Blocklist.Version,
            HashBaselineVersion: identity.HashBaselineVersion, HashTargetCost: identity.Baseline.TargetIterations);
        var sequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey, BindAuthenticationPolicy(fact), deadline);
        state.LastIdentityAuditHash = AuditChainDatabase.Tail(database, deadline).Hash;
        var protectedState = IdentityStateProtection.Protect(state);
        AuditChainDatabase.Execute(database, "INSERT INTO identity_authority VALUES(1,0,?,?,?);", deadline,
            protectedState, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision, sequence, _signingKey));
    }

    internal async ValueTask<IdentityAuthorityState> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed || _options.LocalIdentity is null || _signingKey is null)
            throw new InvalidOperationException("IdentityStoreUnavailable");
        return await Task.Run(() =>
        {
            using var connection = SqliteNative.Open(_databasePath!, true);
            var database = connection.Handle!;
            var deadline = new StoreDeadline(_options.QueryTimeout);
            SqliteNative.Execute(database, "PRAGMA query_only=ON; BEGIN;", deadline, cancellationToken);
            AuditChainDatabase.Verify(database, _policy!, _signingKey.KeyId, _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(), true, deadline, validateAnchorReceipt: false);
            var state = ReadIdentityState(database, deadline);
            SqliteNative.Execute(database, "COMMIT;", deadline, cancellationToken);
            return state;
        }, cancellationToken).ConfigureAwait(false);
    }

    private IdentityAuthorityState ReadIdentityState(sqlite3 database, StoreDeadline deadline)
    {
        var options = _options.LocalIdentity!;
        var binding = AuditChainDatabase.Read(database, "SELECT StationId,InstallationKeyId,PolicyContentHash FROM identity_policy_binding WHERE Id=1;",
            deadline, s => Enumerable.Range(0, 3).Select(i => SqliteNative.ColumnText(s, i)).ToArray()).SingleOrDefault();
        AuditChainDatabase.Require(binding is not null && binding.SequenceEqual(new[] { options.StationId, _signingKey!.KeyId,
            options.PolicyContentHash }), "IdentityPolicyBindingMismatch");
        var row = AuditChainDatabase.Read(database, "SELECT Revision,ProtectedState,LastAuditSequence,StateSignature FROM identity_authority WHERE Id=1;", deadline,
            s => (Revision: SqliteNative.ColumnInt64(s, 0), Protected: SqliteNative.ColumnText(s, 1)!, Sequence: SqliteNative.ColumnInt64(s, 2),
                Signature: SqliteNative.ColumnText(s, 3)!)).SingleOrDefault();
        AuditChainDatabase.Require(row.Protected is not null, "IdentityAuthorityMissing");
        IdentityStateProtection.VerifySignature(row.Protected!, row.Signature, options.StationId, row.Revision, row.Sequence, _signingKey!);
        var state = IdentityStateProtection.Unprotect(row.Protected!, options.StationId, _signingKey!.KeyId, row.Revision);
        // The policy is inside the signed ciphertext as well as the lookup binding;
        // changing a database binding and deployment settings cannot retarget old credentials.
        AuditChainDatabase.Require(state.PolicyContentHash == options.PolicyContentHash, "IdentityPolicyBindingMismatch");
        var schemaVersion = checked((int)AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline));
        var latest = AuditChainDatabase.Read(database, "SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries WHERE IdentityPosition IS NOT NULL ORDER BY IdentityPosition DESC LIMIT 1;",
            deadline, s => (Sequence: SqliteNative.ColumnInt64(s, 0), Ordinal: SqliteNative.ColumnInt64(s, 1), Payload: SqliteNative.ColumnText(s, 2)!,
                Hash: SqliteNative.ColumnText(s, 3)!)).SingleOrDefault();
        AuditChainDatabase.Require(latest.Sequence == row.Sequence && state.LastIdentityAuditHash == latest.Hash && IdentityAuditEvent.VerifyPayload(
            Convert.FromBase64String(latest.Payload), latest.Ordinal, options.StationId, schemaVersion) == state.Revision, "IdentityAuthorityAuditMismatch");
        return state;
    }

    internal ValueTask<IdentityWriteResult> UpdateIdentityAsync(Func<IdentityAuthorityState, IdentityUpdate> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        return EnqueueIdentityAsync(new IdentityWork(update), cancellationToken);
    }

    /// <summary>
    /// Runs a command-aware identity update on the same bounded writer queue. The callback is
    /// invoked after BEGIN IMMEDIATE and a fresh signed state read. Its second argument reports
    /// whether an accepted outcome for the correlation id already exists; it is deliberately a
    /// bounded indexed lookup rather than an in-memory history of successful commands.
    /// </summary>
    internal ValueTask<IdentityWriteResult> UpdateIdentityCommandAsync(Guid correlationId,
        Func<IdentityAuthorityState, bool, IdentityUpdate> update, CancellationToken cancellationToken,
        StoreDeadline? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (correlationId == Guid.Empty)
            return ValueTask.FromResult(new IdentityWriteResult(false, "CorrelationIdRequired"));
        return EnqueueIdentityAsync(new IdentityWork(correlationId, update), cancellationToken, deadline);
    }

    private async ValueTask<IdentityWriteResult> EnqueueIdentityAsync(IdentityWork work,
        CancellationToken cancellationToken, StoreDeadline? commandDeadline = null)
    {
        if (_options.LocalIdentity is null || _queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
            return new(false, "IdentityStoreUnavailable");
        var deadline = commandDeadline ?? new StoreDeadline(CommitTimeout);
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero || !await _queueSlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "IdentityCommitDeadlineExceeded");
        var request = new WriteRequest(null, deadline, Identity: work);
        if (!_queue.Writer.TryWrite(request))
        {
            _queueSlots.Release();
            return new(false, "IdentityStoreUnavailable");
        }

        var result = await request.Completion.Task.ConfigureAwait(false);
        return new(result.Committed, result.ReasonCode, result.Committed ? work.Result : null);
    }

    private StoreWriteResult UpdateIdentityCore(sqlite3 database, IdentityWork work, StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified) return new(false, Integrity?.ReasonCode ?? "IdentityAuditUnavailable");
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes) return new(false, "TraceStoreWalLimit");
        var committed = false;
        IdentityUpdate? decision = null;
        IIdentityTransactionGuard? guard = null;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        try
        {
            AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId, _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(), true, deadline, validateAnchorReceipt: false);
            var state = ReadIdentityState(database, deadline);
            state.Revision = checked(state.Revision + 1);
            var duplicateCorrelation = work.CommandUpdate is not null &&
                Exists(database, "SELECT 1 FROM command_attempts WHERE CorrelationId=? AND OutcomeDisposition=0 LIMIT 1;",
                    work.CommandCorrelationId!.Value, deadline);
            var evaluated = work.Evaluate(state, duplicateCorrelation);
            decision = evaluated;
            guard = evaluated.CommitGuard;
            AuditChainDatabase.Require(evaluated.Events.Count is > 0 and <= 8, "IdentityAuditEventRequired");
            long identitySequence = 0;
            foreach (var fact in evaluated.Events)
                identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey,
                    BindAuthenticationPolicy(fact with { StateRevision = state.Revision }), deadline);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            if (identityTail is null || identityTail.Value.Sequence != identitySequence)
                throw new InvalidOperationException("IdentityAuthorityAuditMismatch");
            state.LastIdentityAuditHash = identityTail.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;", deadline,
                state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision, identitySequence, _signingKey));
            AppendIdentityCommandFacts(database, evaluated.CommandFacts, work.CommandCorrelationId, deadline);
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Result = evaluated.Result;
            try { guard?.Commit(); }
            catch (Exception) { /* The transaction is durable; the internal guard contract is no-throw. */ }
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            try
            {
                PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying, "AuditRecheckPending"));
                WakeIntegrityMonitor();
            }
            catch (Exception) { /* Post-commit monitoring is advisory and cannot change the result. */ }
            return new(true, "IdentityTransactionPersisted");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is InvalidOperationException && ex.Message.StartsWith("Identity", StringComparison.Ordinal)
                ? ex.Message : SqliteAuditIntegrityQuery.FaultReason(ex, "IdentityCommitFailed");
            if (reason.StartsWith("Audit", StringComparison.Ordinal)) SetIntegrityFault(reason, IsStructuralFault(reason));
            if (reason is "IdentityStateInvalid" or "IdentityPolicyBindingMismatch" or "IdentityAuthorityAuditMismatch" or
                "IdentityAuthorityMissing" or "IdentityStateBindingMismatch" or "IdentityStateSignatureInvalid") SetIntegrityFault(reason, true);
            return new(false, reason);
        }
        finally
        {
            try { guard?.Dispose(); }
            catch (Exception) { /* Disposal must not prevent rollback or rewrite a durable result. */ }
            finally
            {
                if (!committed) Rollback(database);
            }
        }
    }

    private IdentityAuditEvent BindAuthenticationPolicy(IdentityAuditEvent fact) => fact with
    { AuthenticationPolicyId = _options.LocalIdentity!.AuthenticationPolicy.Id,
        AuthenticationPolicyVersion = _options.LocalIdentity.AuthenticationPolicy.Version,
        AuthenticationPolicyHash = _options.LocalIdentity.AuthenticationPolicy.ContentHash,
        AuthorizationPolicyId = _options.LocalIdentity.AuthorizationPolicy.Id,
        AuthorizationPolicyVersion = _options.LocalIdentity.AuthorizationPolicy.Version,
        AuthorizationPolicyHash = _options.LocalIdentity.AuthorizationPolicy.ContentHash };

    private void AppendIdentityCommandFacts(sqlite3 database, IReadOnlyList<CommandAuditFact>? facts,
        Guid? expectedCorrelationId, StoreDeadline deadline)
    {
        if (facts is null || facts.Count == 0)
        {
            AuditChainDatabase.Require(expectedCorrelationId is null, "IdentityCommandFactsRequired");
            return;
        }
        AuditChainDatabase.Require(facts.Count <= 2, "IdentityCommandFactsLimit");

        var outcome = facts[0];
        ValidateFact(outcome);
        AuditChainDatabase.Require(outcome.Phase == CommandAuditPhase.Outcome,
            "IdentityCommandOutcomeRequired");
        AuditChainDatabase.Require(expectedCorrelationId is null || outcome.CorrelationId == expectedCorrelationId.Value,
            "IdentityCommandCorrelationMismatch");
        AuditChainDatabase.Require(!Exists(database, "SELECT 1 FROM command_attempts WHERE AttemptId=? LIMIT 1;",
            outcome.AttemptId, deadline), "DuplicateAttemptId");
        AuditChainDatabase.Require(!Exists(database, "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;",
            outcome.EventId, deadline), "DuplicateEventId");

        var terminal = facts.Count == 2 ? facts[1] : null;
        if (outcome.Disposition == CommandDisposition.Rejected)
            AuditChainDatabase.Require(terminal is null, "IdentityCommandRejectedTerminalForbidden");
        if (terminal is not null)
        {
            ValidateFact(terminal);
            AuditChainDatabase.Require(terminal.Phase is CommandAuditPhase.Completed or CommandAuditPhase.Failed &&
                terminal.Disposition is null && SameCommandContext(outcome, terminal),
                "IdentityCommandTerminalInvalid");
            AuditChainDatabase.Require(!Exists(database, "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;",
                terminal.EventId, deadline), "DuplicateEventId");
        }

        InsertAttempt(database, outcome, deadline);
        InsertFact(database, outcome, aggregateSequence: 1, deadline);
        AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, outcome.EventId, deadline);
        if (terminal is not null)
        {
            InsertFact(database, terminal, aggregateSequence: 2, deadline);
            AuditChainDatabase.AppendCommand(database, _policy!, _signingKey!, terminal.EventId, deadline);
        }
    }

    private static bool SameCommandContext(CommandAuditFact first, CommandAuditFact second) =>
        first.AttemptId == second.AttemptId && first.CorrelationId == second.CorrelationId &&
        first.RuntimeEpoch == second.RuntimeEpoch && first.CommandKind == second.CommandKind &&
        first.Source == second.Source && first.ClaimedPrincipalId == second.ClaimedPrincipalId &&
        first.ClaimedSessionId == second.ClaimedSessionId &&
        first.ClaimedStepUpGrantId == second.ClaimedStepUpGrantId &&
        first.AuthenticatedHumanPrincipalId == second.AuthenticatedHumanPrincipalId;

    private sealed class IdentityWork
    {
        internal IdentityWork(Func<IdentityAuthorityState, IdentityUpdate> update) => Update = update;

        internal IdentityWork(Guid commandCorrelationId,
            Func<IdentityAuthorityState, bool, IdentityUpdate> commandUpdate)
        {
            CommandCorrelationId = commandCorrelationId;
            CommandUpdate = commandUpdate;
        }

        internal Func<IdentityAuthorityState, IdentityUpdate>? Update { get; }
        internal Guid? CommandCorrelationId { get; }
        internal Func<IdentityAuthorityState, bool, IdentityUpdate>? CommandUpdate { get; }
        internal object? Result { get; set; }

        internal IdentityUpdate Evaluate(IdentityAuthorityState state, bool duplicateCorrelation) =>
            CommandUpdate is null ? Update!(state) : CommandUpdate(state, duplicateCorrelation);
    }
}

internal interface IIdentityTransactionGuard : IDisposable
{
    void Commit();
}

internal sealed record IdentityUpdate(
    object Result,
    IReadOnlyList<IdentityAuditEvent> Events,
    IReadOnlyList<CommandAuditFact>? CommandFacts = null,
    IIdentityTransactionGuard? CommitGuard = null);
internal sealed record IdentityWriteResult(bool Committed, string ReasonCode, object? Result = null);
