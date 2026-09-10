using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.StoragePolicies;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    internal ValueTask<TraceStoragePolicyResult> ExecuteTraceStoragePolicyAsync(
        PublishTraceStoragePolicyCommand command,
        Func<IdentityAuthorityState, bool, TraceStoragePolicyEvaluation> evaluate,
        StoreDeadline deadline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (!TraceStoragePolicyEnabled)
            return ValueTask.FromResult(TraceStoragePolicyFailure(command, "TraceStoragePolicyConfigurationRequired"));
        if (command.CorrelationId == Guid.Empty)
            return ValueTask.FromResult(TraceStoragePolicyFailure(command, "TraceStoragePolicyCorrelationRequired"));
        return EnqueueTraceStoragePolicyAsync(new TraceStoragePolicyWork(command, evaluate, cancellationToken),
            deadline, cancellationToken);
    }

    private async ValueTask<TraceStoragePolicyResult> EnqueueTraceStoragePolicyAsync(
        TraceStoragePolicyWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        if (_queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
            return TraceStoragePolicyFailure(work.Command, "TraceStoragePolicyUnavailable");
        try
        {
            var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                    TraceStoragePolicy: work), deadline, cancellationToken,
                "TraceStoragePolicyUnavailable", "TraceStoragePolicyCommitDeadlineExceeded")
                .ConfigureAwait(false);
            return result.Committed && work.Result is not null
                ? work.Result
                : TraceStoragePolicyFailure(work.Command, result.ReasonCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return TraceStoragePolicyFailure(work.Command, exception is InvalidOperationException ? exception.Message : "TraceStoragePolicyUnavailable"); }
    }

    private StoreWriteResult AppendTraceStoragePolicyCore(sqlite3 database,
        TraceStoragePolicyWork work, StoreDeadline deadline)
    {
        if (work.CancellationToken.IsCancellationRequested)
            return new(false, "TraceStoragePolicyCancelled");
        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new(false, integrity?.ReasonCode ?? "TraceStoragePolicyAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new(false, "TraceStoreWalLimit");

        var committed = false;
        IIdentityTransactionGuard? guard = null;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline, work.CancellationToken);
            if (work.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("TraceStoragePolicyCancelled");
            VerifyTraceStoragePolicyReadGuard(database, _options, deadline);
            var options = _options.TraceStoragePolicies ?? throw new InvalidOperationException(
                "TraceStoragePolicyConfigurationRequired");
            RequireConfiguredTraceStoragePolicies(database, options, deadline);
            var identity = ReadIdentityState(database, deadline);
            identity.Revision = checked(identity.Revision + 1);
            var duplicate = Exists(database,
                "SELECT 1 FROM trace_storage_policy_events WHERE OperationId=? LIMIT 1;",
                work.Command.CorrelationId, deadline);
            var evaluated = work.Evaluate(identity, duplicate) ?? throw new InvalidOperationException(
                "TraceStoragePolicyEvaluationMissing");
            guard = evaluated.Guard;
            if (work.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("TraceStoragePolicyCancelled");

            if (duplicate && evaluated.Result.Outcome.Disposition == CommandDisposition.Accepted &&
                evaluated.Events.Count == 0 && evaluated.CommandFacts is null)
            {
                var replay = ReadPersistedTraceStoragePolicyResult(database, work.Command, options,
                    evaluated.VerifiedActor ?? throw new InvalidOperationException("TraceStoragePolicyVerifiedActorRequired"), deadline);
                SqliteNative.Execute(database, "ROLLBACK;", deadline);
                committed = true;
                work.Result = replay;
                guard?.Commit();
                return new(true, replay.Outcome.ReasonCode);
            }

            AuditChainDatabase.Require(evaluated.Events.Count is > 0 and <= 8,
                "TraceStoragePolicyIdentityEventRequired");
            if (evaluated.CommandFacts is { Count: > 0 })
                AuditChainDatabase.Require(evaluated.CommandFacts.Count <= 2,
                    "TraceStoragePolicyCommandFactsLimit");

            TraceStoragePolicyPublication? publication = null;
            TraceStoragePolicySnapshot? snapshot = null;
            if (evaluated.Result.Outcome.Disposition == CommandDisposition.Accepted)
            {
                var actor = evaluated.VerifiedActor ?? throw new InvalidOperationException(
                    "TraceStoragePolicyVerifiedActorRequired");
                var observed = TraceStoragePreflightEvaluator.Observe(_options);
                var validation = TraceStoragePolicyValidator.Validate(work.Command.Policy,
                    options.DeploymentScope, observed.TotalBytes);
                if (validation.Count > 0)
                    throw new InvalidOperationException(validation[0]);
                var prior = ReadTraceStoragePolicyPublications(database, options, deadline);
                var current = prior.Count == 0 ? null : prior[^1];
                AuditChainDatabase.Require(work.Command.ExpectedVersion == (current?.Version ?? 0),
                    "TraceStoragePolicyVersionChanged");
                var nextVersion = checked(work.Command.ExpectedVersion + 1);
                foreach (var item in prior)
                {
                    if (item.Policy.PolicyId != work.Command.Policy.PolicyId ||
                        item.Policy.Version != work.Command.Policy.Version) continue;
                    AuditChainDatabase.Require(item.Policy.ContentHash == work.Command.Policy.ContentHash,
                        "TraceStoragePolicyIdentityReuse");
                    throw new InvalidOperationException("TraceStoragePolicyVersionAlreadyPublished");
                }
                publication = new TraceStoragePolicyPublication(nextVersion, work.Command.Policy,
                    work.Command.CorrelationId, actor.PrincipalId, actor.SessionId,
                    actor.AuthorizationRevision, work.Command.Invocation.StepUpGrantId ??
                        throw new InvalidOperationException("TraceStoragePolicyStepUpGrantRequired"),
                    actor.VerifiedAtUtc, current?.ContentHash);
                snapshot = new TraceStoragePolicySnapshot(publication);
                AuditChainDatabase.EnsureTraceStoragePolicyTransactionCapacity(database, _policy!, options,
                    TraceStoragePolicyStorageCodec.EncodePublication(publication).Length, deadline);
            }

            long identitySequence = 0;
            foreach (var identityEvent in evaluated.Events)
                identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey!,
                    BindAuthenticationPolicy(identityEvent with { StateRevision = identity.Revision }), deadline);
            AppendIdentityCommandFacts(database, evaluated.CommandFacts, work.Command.CorrelationId, deadline);
            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline) ??
                throw new InvalidOperationException("TraceStoragePolicyIdentityAuditMismatch");
            AuditChainDatabase.Require(identitySequence > 0 && identityTail.Sequence == identitySequence,
                "TraceStoragePolicyIdentityAuditMismatch");
            identity.LastIdentityAuditHash = identityTail.Hash;
            var protectedState = IdentityStateProtection.Protect(identity);
            AuditChainDatabase.Execute(database,
                "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;",
                deadline, identity.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, identity.StationId, identity.Revision,
                    identitySequence, _signingKey!));

            var result = evaluated.Result;
            if (publication is not null)
            {
                var commandReference = ReadTracePolicyCommandReference(database,
                    evaluated.Result.Outcome.AttemptId ?? throw new InvalidOperationException("TraceStoragePolicyAttemptRequired"), deadline)
                    ?? throw new InvalidOperationException("TraceStoragePolicyAuthorizationAuditMismatch");
                AppendTraceStoragePolicyEvent(database, _policy!, _signingKey!, publication,
                    checked(ReadLastTraceStoragePolicyPosition(database, deadline) + 1),
                    commandReference.Sequence, commandReference.Hash,
                    identityTail.Sequence, identityTail.Hash, options, deadline);
                result = new(result.Outcome with { Audit = AuditPersistence.Persisted }, publication,
                    snapshot);
            }
            else
            {
                result = new(result.Outcome with { Audit = AuditPersistence.Persisted }, null, null);
            }
            if (work.CancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("TraceStoragePolicyCancelled");
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.Execute(database, "COMMIT;", deadline, work.CancellationToken);
            committed = true;
            work.Result = result;
            try { guard?.Commit(); } catch { }
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying,
                "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new(true, result.Outcome.ReasonCode, evaluated.CommandFacts?.FirstOrDefault());
        }
        catch (InvalidOperationException exception) when (AuditChainDatabase.IsCapacityReason(exception.Message))
        { return new(false, exception.Message); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var reason = exception is InvalidOperationException invalid &&
                (invalid.Message.StartsWith("TraceStoragePolicy", StringComparison.Ordinal) ||
                 invalid.Message.StartsWith("Identity", StringComparison.Ordinal) ||
                 invalid.Message.StartsWith("Audit", StringComparison.Ordinal))
                ? invalid.Message : SqliteAuditIntegrityQuery.FaultReason(exception,
                    "TraceStoragePolicyCommitFailed");
            if (reason.StartsWith("Audit", StringComparison.Ordinal) &&
                !AuditChainDatabase.IsCapacityReason(reason)) SetIntegrityFault(reason, IsStructuralFault(reason));
            return new(false, reason);
        }
        finally
        {
            try { guard?.Dispose(); } catch { }
            if (!committed) Rollback(database);
        }
    }

    private static TraceStoragePolicyResult ReadPersistedTraceStoragePolicyResult(sqlite3 database,
        PublishTraceStoragePolicyCommand command, TraceStoragePolicyStoreOptions options,
        TraceStoragePolicyVerifiedActor currentActor, StoreDeadline deadline)
    {
        var row = AuditChainDatabase.Read(database, @"
            SELECT Position,Kind,Version,OperationId,PrincipalId,SessionId,AuthorizationRevision,
                StepUpGrantId,PolicyHash,PublicationHash,SnapshotHash,RecordedAtUtc,PublicationPayload,
                CommandSequence,CommandHash,IdentitySequence,IdentityHash,CentralSequence,CentralHash,PayloadHash,
                PolicyId,PolicyVersion,PreviousContentHash
            FROM trace_storage_policy_events WHERE OperationId=? LIMIT 2;", deadline,
            statement => ReadStoredRow(statement, options), command.CorrelationId.ToString("D")).SingleOrDefault();
        AuditChainDatabase.Require(row is not null && row.Publication.Policy.ContentHash == command.Policy.ContentHash &&
            row.Publication.Version - 1 == command.ExpectedVersion &&
            row.Publication.Policy.PolicyId == command.Policy.PolicyId &&
            row.Publication.Policy.Version == command.Policy.Version &&
            Guid.TryParse(command.Invocation.PrincipalId, out var principal) &&
            principal == row.Publication.PrincipalId && command.Invocation.SessionId == row.Publication.SessionId &&
            command.Invocation.StepUpGrantId == row.Publication.StepUpGrantId &&
            currentActor.PrincipalId == row.Publication.PrincipalId && currentActor.SessionId == row.Publication.SessionId &&
            currentActor.AuthorizationRevision == row.Publication.AuthorizationRevision,
            "TraceStoragePolicyReplayBindingMismatch");
        var identityPayload = AuditChainDatabase.Read(database, @"
            SELECT Payload FROM audit_entries WHERE Sequence=? AND Kind='IdentityEvent' LIMIT 2;",
            deadline, statement => SqliteNative.ColumnText(statement, 0),
            row!.IdentitySequence.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(identityPayload is not null,
            "TraceStoragePolicyReplayBindingMismatch");
        string?[] fields;
        try { fields = DecodeRecipeTransferIdentityFields(Convert.FromBase64String(identityPayload!)); }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        { throw new InvalidOperationException("TraceStoragePolicyReplayBindingMismatch", exception); }
        AuditChainDatabase.Require(fields.Length == 49 && fields[37] == command.AuthorizationTarget &&
            fields[27] == currentActor.AuthorizationPolicy.Id && fields[28] == currentActor.AuthorizationPolicy.Version &&
            fields[29] == currentActor.AuthorizationPolicy.ContentHash,
            "TraceStoragePolicyReplayBindingMismatch");
        var attempt = AuditChainDatabase.Read(database, @"
            SELECT f.AttemptId FROM audit_entries a JOIN command_facts f ON f.Position=a.FactPosition
            WHERE a.Sequence=? AND a.Hash=? AND a.Kind='CommandFact' AND f.AggregateSequence=1 LIMIT 2;",
            deadline, statement => ParseGuid(SqliteNative.ColumnText(statement, 0)),
            row.CommandSequence.ToString(CultureInfo.InvariantCulture), row.CommandHash).SingleOrDefault();
        var fact = ReadFact(database, attempt, 1, deadline) ??
            throw new InvalidOperationException("TraceStoragePolicyReplayCommandMissing");
        AuditChainDatabase.Require(fact.Disposition == CommandDisposition.Accepted &&
            fact.CorrelationId == command.CorrelationId && fact.CommandKind == AuditedCommandKind.PublishTraceStoragePolicy &&
            fact.Source == command.Invocation.Source,
            "TraceStoragePolicyReplayBindingMismatch");
        return new(new(fact.CorrelationId, fact.Disposition!.Value, fact.ReasonCode, AuditPersistence.Persisted,
            fact.AttemptId), row.Publication, new TraceStoragePolicySnapshot(row.Publication));
    }

    private static TraceStoragePolicyResult TraceStoragePolicyFailure(
        PublishTraceStoragePolicyCommand command, string reason) =>
        new(new(command.CorrelationId, CommandDisposition.Rejected, reason,
            AuditPersistence.Unavailable, Guid.NewGuid()));

    private static long ReadLastTraceStoragePolicyPosition(sqlite3 database, StoreDeadline deadline) =>
        AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0) FROM trace_storage_policy_events;", deadline);

    private static (long Sequence, string Hash)? ReadTracePolicyCommandReference(sqlite3 database,
        Guid attemptId, StoreDeadline deadline) => AuditChainDatabase.Read(database, @"
            SELECT a.Sequence,a.Hash FROM audit_entries a
            WHERE a.Kind='CommandFact' AND a.FactPosition=(
                SELECT Position FROM command_facts WHERE AttemptId=? AND AggregateSequence=1
                ORDER BY Position DESC LIMIT 1) LIMIT 2;", deadline,
        statement => (SqliteNative.ColumnInt64(statement, 0), SqliteNative.ColumnText(statement, 1)!),
        attemptId.ToString("D")).SingleOrDefault();
}
