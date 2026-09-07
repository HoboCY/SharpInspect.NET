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
        var sequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey, fact, deadline);
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
        var latest = AuditChainDatabase.Read(database, "SELECT Sequence,IdentityPosition,Payload,Hash FROM audit_entries WHERE IdentityPosition IS NOT NULL ORDER BY Sequence DESC LIMIT 1;",
            deadline, s => (Sequence: SqliteNative.ColumnInt64(s, 0), Ordinal: SqliteNative.ColumnInt64(s, 1), Payload: SqliteNative.ColumnText(s, 2)!,
                Hash: SqliteNative.ColumnText(s, 3)!)).SingleOrDefault();
        AuditChainDatabase.Require(latest.Sequence == row.Sequence && state.LastIdentityAuditHash == latest.Hash && IdentityAuditEvent.VerifyPayload(
            Convert.FromBase64String(latest.Payload), latest.Ordinal, options.StationId) == state.Revision, "IdentityAuthorityAuditMismatch");
        return state;
    }

    internal async ValueTask<IdentityWriteResult> UpdateIdentityAsync(Func<IdentityAuthorityState, IdentityUpdate> update,
        CancellationToken cancellationToken)
    {
        if (_options.LocalIdentity is null || _queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
            return new(false, "IdentityStoreUnavailable");
        var deadline = new StoreDeadline(CommitTimeout);
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false)) return new(false, "IdentityCommitDeadlineExceeded");
        var work = new IdentityWork(update);
        var request = new WriteRequest(null, deadline, Identity: work);
        if (!_queue.Writer.TryWrite(request)) { _queueSlots.Release(); return new(false, "IdentityStoreUnavailable"); }
        var result = await request.Completion.Task.ConfigureAwait(false);
        return new(result.Committed, result.ReasonCode, result.Committed ? work.Result : null);
    }

    private StoreWriteResult UpdateIdentityCore(sqlite3 database, IdentityWork work, StoreDeadline deadline)
    {
        if (Integrity?.State != AuditIntegrityState.Verified) return new(false, Integrity?.ReasonCode ?? "IdentityAuditUnavailable");
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes) return new(false, "TraceStoreWalLimit");
        var committed = false;
        SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
        try
        {
            AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId, _signingKey.PublicKeyBase64,
                new AuditVerificationRequest(), true, deadline, validateAnchorReceipt: false);
            var state = ReadIdentityState(database, deadline);
            state.Revision = checked(state.Revision + 1);
            var decision = work.Update(state);
            AuditChainDatabase.Require(decision.Events.Count is > 0 and <= 8, "IdentityAuditEventRequired");
            long sequence = 0;
            foreach (var fact in decision.Events)
                sequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey,
                    fact with { StateRevision = state.Revision }, deadline);
            state.LastIdentityAuditHash = AuditChainDatabase.Tail(database, deadline).Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;", deadline,
                state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), protectedState,
                sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision, sequence, _signingKey));
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Result = decision.Result;
            Interlocked.Exchange(ref _lastCommittedAuditSequence, sequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying, "AuditRecheckPending"));
            WakeIntegrityMonitor();
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
        finally { if (!committed) Rollback(database); }
    }

    private sealed class IdentityWork
    {
        internal IdentityWork(Func<IdentityAuthorityState, IdentityUpdate> update) => Update = update;
        internal Func<IdentityAuthorityState, IdentityUpdate> Update { get; }
        internal object? Result { get; set; }
    }
}

internal sealed record IdentityUpdate(object Result, IReadOnlyList<IdentityAuditEvent> Events);
internal sealed record IdentityWriteResult(bool Committed, string ReasonCode, object? Result = null);
