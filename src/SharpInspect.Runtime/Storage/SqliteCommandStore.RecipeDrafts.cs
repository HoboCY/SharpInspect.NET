using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record RecipeDraftHead(long Position, Guid DraftId, long Revision, Guid OperationId,
    string? PreviousRevisionContentHash, string RevisionContentHash, string PayloadHash,
    string PayloadJson, Guid AuthorPrincipalId, Guid AuthorSessionId, long AuthorAuthorizationRevision,
    string ChangeReason, DateTimeOffset RecordedAtUtc);

internal sealed record RecipeDraftMutation(Guid AuthorPrincipalId, Guid AuthorSessionId,
    long AuthorAuthorizationRevision, string ChangeReason);

internal sealed record RecipeDraftEvaluation(RecipeDraftSaveResult Result, RecipeDraftMutation? Mutation,
    IReadOnlyList<IdentityAuditEvent> Events, IIdentityTransactionGuard? CommitGuard = null);

internal sealed class RecipeDraftWork
{
    internal RecipeDraftWork(RecipeDraftSaveRequest request, RecipeDraftDocument document,
        Func<IdentityAuthorityState, RecipeDraftHead?, bool, RecipeDraftEvaluation> evaluate,
        CancellationToken cancellationToken)
    {
        Request = request; Document = document; Evaluate = evaluate; CancellationToken = cancellationToken;
    }

    internal RecipeDraftSaveRequest Request { get; }
    internal RecipeDraftDocument Document { get; }
    internal Func<IdentityAuthorityState, RecipeDraftHead?, bool, RecipeDraftEvaluation> Evaluate { get; }
    internal CancellationToken CancellationToken { get; }
    internal object? Result { get; set; }
}

internal sealed partial class SqliteCommandStore
{
    internal const string RecipeDraftSchemaSql = @"
        CREATE TABLE recipe_draft_store_config(
            Id INTEGER PRIMARY KEY CHECK(Id=1), FormatVersion INTEGER NOT NULL CHECK(FormatVersion=1),
            ExecutionPolicyId TEXT NOT NULL, ExecutionPolicyVersion TEXT NOT NULL,
            ExecutionPolicyHash TEXT NOT NULL CHECK(length(ExecutionPolicyHash)=64),
            MaximumRecordBytes INTEGER NOT NULL CHECK(MaximumRecordBytes>0),
            MaximumPageBytes INTEGER NOT NULL CHECK(MaximumPageBytes>0),
            MaximumTotalBytes INTEGER NOT NULL CHECK(MaximumTotalBytes>0),
            MaximumRevisionCount INTEGER NOT NULL CHECK(MaximumRevisionCount>0),
            RequireStepUp INTEGER NOT NULL CHECK(RequireStepUp IN (0,1)),
            BindingHash TEXT NOT NULL CHECK(length(BindingHash)=64));
        CREATE TABLE recipe_draft_revisions(
            Position INTEGER NOT NULL PRIMARY KEY CHECK(Position>0),
            DraftId TEXT NOT NULL CHECK(length(DraftId)=36),
            Revision INTEGER NOT NULL CHECK(Revision>0),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=36),
            PreviousRevisionContentHash TEXT NULL CHECK(PreviousRevisionContentHash IS NULL OR length(PreviousRevisionContentHash)=64),
            RevisionContentHash TEXT NOT NULL CHECK(length(RevisionContentHash)=64),
            PayloadHash TEXT NOT NULL CHECK(length(PayloadHash)=64),
            PayloadJson TEXT NOT NULL,
            AuthorPrincipalId TEXT NOT NULL CHECK(length(AuthorPrincipalId)=36),
            AuthorSessionId TEXT NOT NULL CHECK(length(AuthorSessionId)=36),
            AuthorAuthorizationRevision INTEGER NOT NULL CHECK(AuthorAuthorizationRevision>=0),
            ChangeReason TEXT NOT NULL CHECK(length(ChangeReason)>0 AND length(ChangeReason)<=256),
            RecordedAtUtc TEXT NOT NULL,
            UNIQUE(DraftId,Revision));
        CREATE INDEX ix_recipe_draft_position ON recipe_draft_revisions(Position);
        CREATE INDEX ix_recipe_draft_id_revision ON recipe_draft_revisions(DraftId,Revision);
        CREATE TRIGGER recipe_draft_config_immutable_update BEFORE UPDATE ON recipe_draft_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeDraftConfiguration');
        END;
        CREATE TRIGGER recipe_draft_config_immutable_delete BEFORE DELETE ON recipe_draft_store_config BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeDraftConfiguration');
        END;
        CREATE TRIGGER recipe_draft_revision_immutable_update BEFORE UPDATE ON recipe_draft_revisions BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeDraftRevision');
        END;
        CREATE TRIGGER recipe_draft_revision_immutable_delete BEFORE DELETE ON recipe_draft_revisions BEGIN
            SELECT RAISE(ABORT,'ImmutableRecipeDraftRevision');
        END;";

    internal ValueTask<RecipeDraftSaveResult> SaveRecipeDraftAsync(RecipeDraftSaveRequest request,
        RecipeDraftDocument document,
        Func<IdentityAuthorityState, RecipeDraftHead?, bool, RecipeDraftEvaluation> evaluate,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(evaluate);
        if (!RecipeDraftEnabled) return ValueTask.FromResult(new RecipeDraftSaveResult(false,
            "RecipeDraftUnavailable", null, Array.Empty<AlgorithmValidationIssue>()));
        return SaveRecipeDraftQueuedAsync(new RecipeDraftWork(request, document, evaluate, cancellationToken),
            deadline, cancellationToken);
    }

    private async ValueTask<RecipeDraftSaveResult> SaveRecipeDraftQueuedAsync(RecipeDraftWork work,
        StoreDeadline deadline, CancellationToken cancellationToken)
    {
        try
        {
            var result = await EnqueueVerifiedWorkAsync(() => new WriteRequest(null, deadline,
                    RecipeDraft: work), deadline, cancellationToken, "RecipeDraftUnavailable",
                "RecipeDraftCommitDeadlineExceeded").ConfigureAwait(false);
            return result.Committed && work.Result is RecipeDraftSaveResult saved
                ? saved : new RecipeDraftSaveResult(false, result.ReasonCode, null,
                    Array.Empty<AlgorithmValidationIssue>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new RecipeDraftSaveResult(false, "RecipeDraftUnavailable", null, Array.Empty<AlgorithmValidationIssue>()); }
    }

    private StoreWriteResult SaveRecipeDraftCore(sqlite3 database, RecipeDraftWork work,
        StoreDeadline deadline)
    {
        try
        {
            EnsureRecipeDraftNotCancelled(work);
            ValidateRecipeDraftDocument(work.Document, _options.RecipeDrafts!, _options.CameraSetup is not null);
        }
        catch (InvalidOperationException ex) { return new StoreWriteResult(false, ex.Message); }

        var integrity = Integrity;
        if (integrity?.State != AuditIntegrityState.Verified)
            return new StoreWriteResult(false, integrity?.ReasonCode ?? "RecipeDraftAuditUnavailable",
                RetryAfterIntegrityRecheck: integrity?.State == AuditIntegrityState.Verifying);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new StoreWriteResult(false, "TraceStoreWalLimit");

        var committed = false;
        IIdentityTransactionGuard? guard = null;
        var recipeDraftHistoryVerificationActive = false;
        try
        {
            EnsureRecipeDraftNotCancelled(work);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            EnsureRecipeDraftNotCancelled(work);
            // Keep this phase separate from caller validation, CAS evaluation and
            // mutation. A failure here proves that persisted draft history can no
            // longer be trusted and must latch the store; ordinary request and
            // capacity outcomes must remain retryable.
            recipeDraftHistoryVerificationActive = _options.RecipeDrafts is not null;
            var verification = AuditChainDatabase.Verify(database, _policy!, _signingKey!.KeyId,
                _signingKey.PublicKeyBase64, new AuditVerificationRequest(0, _policy!.MaximumVerificationEntries),
                startup: false, deadline, validateAnchorReceipt: false,
                archiveOptions: _options.AlgorithmResultArchive, recipeDraftOptions: _options.RecipeDrafts,
                cameraSetupOptions: _options.CameraSetup, cameraRecoveryOptions: _options.CameraRecovery,
                cameraNetworkOptions: _options.CameraNetwork,
                imagingSetupOptions: _options.ImagingSetup,
                calibrationSessionOptions: _options.CalibrationSessions);
            if (_options.AlarmPolicy is not null) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
            if (_options.AlgorithmResultArchive is not null) AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
            AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                _options.RecipeDrafts);
            if (_options.CameraSetup is not null)
                AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                    _options.CameraSetup);
            if (_options.CameraRecovery is not null)
                AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                    _options.CameraRecovery);
            if (_options.CameraNetwork is not null)
                AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                    _options.CameraNetwork);
            if (_options.ImagingSetup is not null)
                AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                    _options.ImagingSetup);
            recipeDraftHistoryVerificationActive = false;

            var state = ReadIdentityState(database, deadline);
            var existing = ReadRecipeDraftByOperation(database, work.Request.OperationId, deadline);
            if (existing is not null)
            {
                // Idempotency is an exact request replay, never a shortcut around
                // the current lease/permission boundary. The evaluator rechecks
                // the real session and returns no audit mutation on this path.
                var replay = work.Evaluate(state, existing, true);
                if (replay.Result.Saved && replay.Mutation is not null &&
                    ReplayMatches(existing, work.Request, work.Document, replay.Mutation) &&
                    HasRecipeDraftAuthorization(database, existing, work.Request, replay.Mutation, deadline))
                {
                    work.Result = new RecipeDraftSaveResult(true, "RecipeDraftAlreadyPersisted",
                        ToPublicRevision(database, existing, deadline), Array.Empty<AlgorithmValidationIssue>());
                    Rollback(database);
                    committed = true;
                    return new StoreWriteResult(true, "RecipeDraftAlreadyPersisted");
                }

                var replayResult = replay.Result.Saved
                    ? new RecipeDraftSaveResult(false, "RecipeDraftOperationConflict", null,
                        Array.Empty<AlgorithmValidationIssue>())
                    : replay.Result;
                work.Result = replayResult;
                Rollback(database);
                committed = true;
                return new StoreWriteResult(false, replayResult.ReasonCode);
            }

            var head = ReadRecipeDraftHead(database, work.Request.DraftId, deadline);
            state.Revision = checked(state.Revision + 1);
            EnsureRecipeDraftNotCancelled(work);
            var evaluated = work.Evaluate(state, head, false);
            AuditChainDatabase.Require(evaluated.Events.Count is > 0 and <= 8, "IdentityAuditEventRequired");
            guard = evaluated.CommitGuard;

            // A successful draft mutation consumes both one revision row and
            // one signed draft audit position. Check the configured row and
            // audit budgets before any mutation is appended; the transaction
            // still rechecks them under BEGIN IMMEDIATE.
            if (evaluated.Mutation is not null)
            {
                AuditChainDatabase.Require(evaluated.Result.Saved, "RecipeDraftMutationResultInvalid");
                EnsureRecipeDraftCapacity(database, _options.RecipeDrafts!, work.Document, deadline);
            }

            var identitySequence = 0L;
            EnsureRecipeDraftNotCancelled(work);
            foreach (var fact in evaluated.Events)
            {
                identitySequence = AuditChainDatabase.AppendIdentity(database, _policy!, _signingKey!,
                    BindAuthenticationPolicy(fact with { StateRevision = state.Revision }), deadline);
            }

            if (evaluated.Mutation is not null)
                AuditChainDatabase.EnsureNextSequenceAvailable(database, _policy!, deadline,
                    archiveData: true, recipeDraftData: true);

            RecipeDraftRevision? revision = null;
            if (evaluated.Mutation is { } mutation)
            {
                AuditChainDatabase.Require(evaluated.Result.Saved, "RecipeDraftMutationResultInvalid");
                EnsureRecipeDraftNotCancelled(work);
                revision = InsertRecipeDraftRevision(database, work.Request, work.Document, head, mutation,
                    deadline);
                AuditChainDatabase.AppendRecipeDraftRevision(database, _policy!, _signingKey!, revision.Position, deadline);
                work.Result = new RecipeDraftSaveResult(true, "RecipeDraftPersisted", revision,
                    Array.Empty<AlgorithmValidationIssue>());
            }
            else work.Result = evaluated.Result;

            var identityTail = AuditChainDatabase.LastIdentityEntry(database, deadline);
            AuditChainDatabase.Require(identityTail is not null && identityTail.Value.Sequence == identitySequence,
                "IdentityAuthorityAuditMismatch");
            EnsureRecipeDraftNotCancelled(work);
            state.LastIdentityAuditHash = identityTail!.Value.Hash;
            var protectedState = IdentityStateProtection.Protect(state);
            AuditChainDatabase.Execute(database, "UPDATE identity_authority SET Revision=?,ProtectedState=?,LastAuditSequence=?,StateSignature=? WHERE Id=1;",
                deadline, state.Revision.ToString(CultureInfo.InvariantCulture), protectedState,
                identitySequence.ToString(CultureInfo.InvariantCulture),
                IdentityStateProtection.Sign(protectedState, state.StationId, state.Revision, identitySequence, _signingKey));
            var committedAuditSequence = AuditChainDatabase.Tail(database, deadline).Sequence;
            EnsureRecipeDraftNotCancelled(work);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            guard?.Commit();
            Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy!, AuditIntegrityState.Verifying, "AuditRecheckPending"));
            WakeIntegrityMonitor();
            return new StoreWriteResult(true, "RecipeDraftTransactionPersisted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return new StoreWriteResult(false, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = ex is InvalidOperationException invalid &&
                (invalid.Message.StartsWith("RecipeDraft", StringComparison.Ordinal) ||
                 invalid.Message.StartsWith("Identity", StringComparison.Ordinal))
                ? invalid.Message : SqliteAuditIntegrityQuery.FaultReason(ex, "RecipeDraftCommitFailed");
            if (recipeDraftHistoryVerificationActive && IsRecipeDraftHistoryStructuralFailure(reason))
                SetIntegrityFault(reason, latch: true);
            else if (reason.StartsWith("Audit", StringComparison.Ordinal) && !AuditChainDatabase.IsCapacityReason(reason))
                SetIntegrityFault(reason, IsStructuralFault(reason));
            return new StoreWriteResult(false, reason);
        }
        finally
        {
            try { guard?.Dispose(); } catch (Exception) { }
            if (!committed) Rollback(database);
        }
    }

    private static bool IsRecipeDraftHistoryStructuralFailure(string reason) =>
        !AuditChainDatabase.IsCapacityReason(reason) &&
        reason is not "AuditVerificationBudgetExceeded" and not "RecipeDraftVerificationBudgetExceeded" &&
        IsStructuralFault(reason);

    private static void EnsureRecipeDraftNotCancelled(RecipeDraftWork work)
    {
        if (work.CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("RecipeDraftSaveCancelled");
    }

    private static bool ReplayMatches(RecipeDraftHead existing, RecipeDraftSaveRequest request,
        RecipeDraftDocument document, RecipeDraftMutation mutation)
    {
        var expectedRevision = request.ExpectedRevision == long.MaxValue
            ? long.MinValue : request.ExpectedRevision + 1;
        return existing.DraftId == request.DraftId && existing.Revision == expectedRevision &&
            string.Equals(existing.PreviousRevisionContentHash, request.ExpectedRevisionContentHash,
                StringComparison.Ordinal) && string.Equals(existing.PayloadHash, document.PayloadHash,
                StringComparison.Ordinal) && string.Equals(existing.ChangeReason, request.ChangeReason,
                StringComparison.Ordinal) && existing.AuthorPrincipalId == mutation.AuthorPrincipalId &&
            existing.AuthorSessionId == mutation.AuthorSessionId &&
            existing.AuthorAuthorizationRevision == mutation.AuthorAuthorizationRevision;
    }

    private bool HasRecipeDraftAuthorization(sqlite3 database, RecipeDraftHead existing,
        RecipeDraftSaveRequest request, RecipeDraftMutation mutation, StoreDeadline deadline)
    {
        var events = AuditChainDatabase.Read(database,
            "SELECT IdentityPosition,Payload FROM audit_entries WHERE Kind='IdentityEvent' " +
            "AND IdentityPosition IS NOT NULL ORDER BY IdentityPosition;", deadline,
            statement => (Ordinal: SqliteNative.ColumnInt64(statement, 0),
                Payload: Convert.FromBase64String(SqliteNative.ColumnText(statement, 1)!)));
        return events.Any(item => IdentityAuditEvent.MatchesRecipeDraftAuthorization(item.Payload,
            item.Ordinal, _policy!.StationId, mutation.AuthorPrincipalId, mutation.AuthorSessionId,
            mutation.AuthorAuthorizationRevision, existing.OperationId, existing.DraftId,
            request.StepUpGrantId));
    }

    private void InitializeRecipeDraftSchema(sqlite3 database, RecipeDraftStoreOptions options, StoreDeadline deadline)
    {
        AuditChainDatabase.Execute(database, @"INSERT INTO recipe_draft_store_config
            (Id,FormatVersion,ExecutionPolicyId,ExecutionPolicyVersion,ExecutionPolicyHash,
             MaximumRecordBytes,MaximumPageBytes,MaximumTotalBytes,MaximumRevisionCount,RequireStepUp,BindingHash)
            VALUES(1,1,?,?,?,?,?,?,?, ?,?);", deadline, options.ExecutionPolicy.Id,
            options.ExecutionPolicy.Version, options.ExecutionPolicy.ContentHash,
            options.MaximumRecordBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumPageBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumRevisionCount.ToString(CultureInfo.InvariantCulture), options.RequireStepUp ? "1" : "0",
            options.BindingHash);
        AuditChainDatabase.AppendRecipeDraftStoreActivation(database, _policy!, _signingKey!, options, deadline);
    }

    internal static void RequireConfiguredRecipeDrafts(sqlite3 database, RecipeDraftStoreOptions options,
        StoreDeadline deadline)
    {
        options.Validate();
        var config = AuditChainDatabase.Read(database, @"SELECT FormatVersion,ExecutionPolicyId,
            ExecutionPolicyVersion,ExecutionPolicyHash,MaximumRecordBytes,MaximumPageBytes,
            MaximumTotalBytes,MaximumRevisionCount,RequireStepUp,BindingHash
            FROM recipe_draft_store_config WHERE Id=1 LIMIT 2;", deadline, s => new RecipeDraftConfig(
                checked((int)SqliteNative.ColumnInt64(s, 0)), SqliteNative.ColumnText(s, 1)!,
                SqliteNative.ColumnText(s, 2)!, SqliteNative.ColumnText(s, 3)!,
                checked((int)SqliteNative.ColumnInt64(s, 4)), checked((int)SqliteNative.ColumnInt64(s, 5)),
                SqliteNative.ColumnInt64(s, 6), checked((int)SqliteNative.ColumnInt64(s, 7)),
                SqliteNative.ColumnInt64(s, 8), SqliteNative.ColumnText(s, 9)!)).SingleOrDefault();
        AuditChainDatabase.Require(config is not null && config.FormatVersion == RecipeDraftStoreOptions.FormatVersion &&
            config.PolicyId == options.ExecutionPolicy.Id && config.PolicyVersion == options.ExecutionPolicy.Version &&
            config.PolicyHash == options.ExecutionPolicy.ContentHash && config.MaximumRecordBytes == options.MaximumRecordBytes &&
            config.MaximumPageBytes == options.MaximumPageBytes && config.MaximumTotalBytes == options.MaximumTotalBytes &&
            config.MaximumRevisionCount == options.MaximumRevisionCount && config.RequireStepUp == (options.RequireStepUp ? 1 : 0) &&
            config.BindingHash == options.BindingHash, "RecipeDraftConfigurationMismatch");
    }

    internal static void VerifyRecipeDraftActivationPayload(sqlite3 database, byte[] payload,
        RecipeDraftStoreOptions options, StoreDeadline deadline)
    {
        var expected = AuditCanonical.Encode("RecipeDraftStoreActivated", options.BindingHash,
            options.ExecutionPolicy.Id, options.ExecutionPolicy.Version, options.ExecutionPolicy.ContentHash,
            options.MaximumRecordBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumPageBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumTotalBytes.ToString(CultureInfo.InvariantCulture),
            options.MaximumRevisionCount.ToString(CultureInfo.InvariantCulture), options.RequireStepUp ? "1" : "0");
        AuditChainDatabase.Require(payload.SequenceEqual(expected), "RecipeDraftActivationMismatch");
        RequireConfiguredRecipeDrafts(database, options, deadline);
    }

    internal static byte[] ReadRecipeDraftBindingPayload(sqlite3 database, long position, StoreDeadline deadline)
    {
        var row = ReadRecipeDraftRow(database, "WHERE Position=?", deadline, position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "RecipeDraftRevisionMissing");
        return EncodeRecipeDraftBinding(row!);
    }

    internal static void ValidateRecipeDraftHistory(sqlite3 database,
        RecipeDraftStoreOptions options, StoreDeadline deadline)
    {
        options.Validate();
        var count = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM recipe_draft_revisions;", deadline);
        AuditChainDatabase.Require(count <= options.MaximumRevisionCount,
            "RecipeDraftRevisionCapacityExceeded");
        var totalBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(PayloadJson AS BLOB))),0) FROM recipe_draft_revisions;", deadline);
        AuditChainDatabase.Require(totalBytes <= options.MaximumTotalBytes,
            "RecipeDraftTotalCapacityExceeded");
        var minimumPosition = count == 0 ? 0 : AuditChainDatabase.Scalar(database,
            "SELECT MIN(Position) FROM recipe_draft_revisions;", deadline);
        var maximumPosition = count == 0 ? 0 : AuditChainDatabase.Scalar(database,
            "SELECT MAX(Position) FROM recipe_draft_revisions;", deadline);
        var distinctPositions = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(DISTINCT Position) FROM recipe_draft_revisions;", deadline);
        AuditChainDatabase.Require((count == 0 || (minimumPosition == 1 && maximumPosition == count)) &&
            distinctPositions == count, "RecipeDraftPositionGap");
        var cameraSetupEnabled = AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is
            CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion;

        // Stream one draft row at a time. A valid store may contain up to the
        // configured 256 MiB payload budget; materializing that history here
        // would multiply memory usage across concurrent read requests.
        SqliteNative.WithStatement(database, @"SELECT Position,DraftId,Revision,OperationId,
            PreviousRevisionContentHash,RevisionContentHash,PayloadHash,PayloadJson,
            AuthorPrincipalId,AuthorSessionId,AuthorAuthorizationRevision,ChangeReason,RecordedAtUtc
            FROM recipe_draft_revisions ORDER BY DraftId,Revision;", deadline, statement =>
        {
            var currentDraft = Guid.Empty;
            string? previousHash = null;
            long expectedRevision = 1;
            while (SqliteNative.Step(database, statement, deadline) == raw.SQLITE_ROW)
            {
                var row = ReadRecipeDraftRow(statement);
                if (row.DraftId != currentDraft)
                {
                    currentDraft = row.DraftId;
                    previousHash = null;
                    expectedRevision = 1;
                }
                AuditChainDatabase.Require(row.Revision == expectedRevision++, "RecipeDraftRevisionGap");
                AuditChainDatabase.Require(string.Equals(row.PreviousRevisionContentHash, previousHash,
                    StringComparison.Ordinal), "RecipeDraftPreviousRevisionMismatch");
                var content = DecodeAndValidateRecipeDraftRow(row, options, cameraSetupEnabled);
                var expectedHash = ComputeRevisionHash(row.DraftId, row.Revision, row.OperationId,
                    row.PreviousRevisionContentHash, content.ContentHash, row.PayloadHash,
                    row.AuthorPrincipalId, row.AuthorSessionId, row.AuthorAuthorizationRevision,
                    row.ChangeReason, row.RecordedAtUtc);
                AuditChainDatabase.Require(string.Equals(row.RevisionContentHash, expectedHash,
                    StringComparison.Ordinal), "RecipeDraftRevisionHashMismatch");
                previousHash = row.RevisionContentHash;
            }
            return 0;
        });
    }

    internal static byte[] ReadAndValidateRecipeDraft(sqlite3 database, long position, byte[] auditPayload,
        StoreDeadline deadline, RecipeDraftStoreOptions options)
    {
        var row = ReadRecipeDraftRow(database, "WHERE Position=?", deadline, position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "RecipeDraftRevisionMissing");
        AuditChainDatabase.Require(EncodeRecipeDraftBinding(row!).SequenceEqual(auditPayload), "RecipeDraftBindingMismatch");
        var found = row!;
        _ = DecodeAndValidateRecipeDraftRow(found, options,
            cameraSetupEnabled: AuditChainDatabase.Scalar(database, "PRAGMA user_version;", deadline) is
            CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion);
        return auditPayload;
    }

    private static void EnsureRecipeDraftCapacity(sqlite3 database, RecipeDraftStoreOptions options,
        RecipeDraftDocument document, StoreDeadline deadline)
    {
        var payloadBytes = Encoding.UTF8.GetByteCount(document.PayloadJson);
        var count = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM recipe_draft_revisions;", deadline);
        var total = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(PayloadJson AS BLOB))),0) FROM recipe_draft_revisions;", deadline);
        AuditChainDatabase.Require(count < options.MaximumRevisionCount,
            "RecipeDraftRevisionCapacityExceeded");
        AuditChainDatabase.Require(total <= options.MaximumTotalBytes - payloadBytes,
            "RecipeDraftTotalCapacityExceeded");
    }

    private static RecipeDraftContent DecodeAndValidateRecipeDraftRow(RecipeDraftHead row,
        RecipeDraftStoreOptions options, bool cameraSetupEnabled)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(row.PayloadJson);
        AuditChainDatabase.Require(payloadBytes.Length <= options.MaximumRecordBytes,
            "RecipeDraftPayloadOversized");
        AuditChainDatabase.Require(payloadBytes.Length == Encoding.UTF8.GetByteCount(row.PayloadJson),
            "RecipeDraftPayloadLengthMismatch");
        AuditChainDatabase.Require(string.Equals(Convert.ToHexString(SHA256.HashData(payloadBytes)),
            row.PayloadHash, StringComparison.Ordinal), "RecipeDraftPayloadHashMismatch");
        AuditChainDatabase.Require(RecipeDraftStorageCodec.TryDecodeContent(row.PayloadJson, row.PayloadHash,
            out var decoded, out var reason) && decoded is not null && reason == "RecipeDraftContentDecoded",
            reason.Length == 0 ? "RecipeDraftPayloadInvalid" : reason);
        AuditChainDatabase.Require(cameraSetupEnabled || decoded!.CameraProviderExtension is null,
            "RecipeDraftCameraExtensionRequiresCameraSetup");
        return decoded!;
    }

    private static void ValidateRecipeDraftDocument(RecipeDraftDocument document, RecipeDraftStoreOptions options,
        bool cameraSetupEnabled)
    {
        if (document.Content is null || string.IsNullOrWhiteSpace(document.PayloadJson) ||
            document.PayloadHash is null || document.PayloadHash.Length != 64)
            throw new InvalidOperationException("RecipeDraftDocumentInvalid");
        var bytes = Encoding.UTF8.GetBytes(document.PayloadJson);
        if (bytes.Length > options.MaximumRecordBytes) throw new InvalidOperationException("RecipeDraftPayloadOversized");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), document.PayloadHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeDraftPayloadHashMismatch");
        if (!RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
                out var decoded, out var reason) || decoded is null)
            throw new InvalidOperationException(reason.Length == 0 ? "RecipeDraftPayloadInvalid" : reason);
        if (!string.Equals(decoded.ContentHash, document.Content.ContentHash, StringComparison.Ordinal))
            throw new InvalidOperationException("RecipeDraftContentMismatch");
        if (document.Content.AssetRequirements.Any(item => item.Kind == RecipeAssetKind.Calibration))
            throw new InvalidOperationException("RecipeLegacyCalibrationRequirementNeedsExplicitConversion");
        if (document.Content.PolicyRequirements.Any(item => item.Kind == RecipePolicyKind.CalibrationAcceptance))
            throw new InvalidOperationException("RecipeLegacyCalibrationPolicyNeedsExplicitConversion");
        if (!cameraSetupEnabled && decoded.CameraProviderExtension is not null)
            throw new InvalidOperationException("RecipeDraftCameraExtensionRequiresCameraSetup");
        var policy = document.Content.PolicyRequirements.Where(item => item.Kind == RecipePolicyKind.AlgorithmExecution).ToArray();
        if (policy.Length != 1 || policy[0].Contract.Id != options.ExecutionPolicy.Id ||
            policy[0].Contract.Version != options.ExecutionPolicy.Version ||
            policy[0].Contract.ContentHash != options.ExecutionPolicy.ContentHash)
            throw new InvalidOperationException("RecipeDraftExecutionPolicyMismatch");
        if (document.Content.AlgorithmExecutionTimeout < options.ExecutionPolicy.MinimumExecutionTimeout)
            throw new InvalidOperationException("AlgorithmExecutionTimeoutBelowMinimum");
        if (document.Content.AlgorithmExecutionTimeout > options.ExecutionPolicy.MaximumExecutionTimeout)
            throw new InvalidOperationException("AlgorithmExecutionTimeoutAboveMaximum");
    }

    private static RecipeDraftRevision InsertRecipeDraftRevision(sqlite3 database,
        RecipeDraftSaveRequest request, RecipeDraftDocument document, RecipeDraftHead? head,
        RecipeDraftMutation mutation, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(request.ExpectedRevision == (head?.Revision ?? 0), "RecipeDraftRevisionConflict");
        AuditChainDatabase.Require(request.ExpectedRevisionContentHash == (head?.RevisionContentHash),
            "RecipeDraftRevisionContentConflict");
        var revision = checked((head?.Revision ?? 0) + 1);
        var position = checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0) FROM recipe_draft_revisions;", deadline) + 1);
        var recordedAt = DateTimeOffset.UtcNow;
        var revisionHash = ComputeRevisionHash(request.DraftId, revision, request.OperationId,
            head?.RevisionContentHash, document.Content.ContentHash, document.PayloadHash,
            mutation.AuthorPrincipalId, mutation.AuthorSessionId, mutation.AuthorAuthorizationRevision,
            mutation.ChangeReason, recordedAt);
        AuditChainDatabase.Execute(database, @"INSERT INTO recipe_draft_revisions
            (Position,DraftId,Revision,OperationId,PreviousRevisionContentHash,RevisionContentHash,
             PayloadHash,PayloadJson,AuthorPrincipalId,AuthorSessionId,AuthorAuthorizationRevision,
             ChangeReason,RecordedAtUtc) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            position.ToString(CultureInfo.InvariantCulture), request.DraftId.ToString("D"),
            revision.ToString(CultureInfo.InvariantCulture), request.OperationId.ToString("D"),
            head?.RevisionContentHash, revisionHash, document.PayloadHash, document.PayloadJson,
            mutation.AuthorPrincipalId.ToString("D"), mutation.AuthorSessionId.ToString("D"),
            mutation.AuthorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), mutation.ChangeReason,
            recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return new RecipeDraftRevision(position, request.DraftId, revision, request.OperationId,
            head?.RevisionContentHash, revisionHash, document.Content, mutation.AuthorPrincipalId,
            mutation.AuthorSessionId, mutation.AuthorAuthorizationRevision, mutation.ChangeReason, recordedAt);
    }

    private static string ComputeRevisionHash(Guid draftId, long revision, Guid operationId,
        string? previousRevisionContentHash, string contentHash, string payloadHash,
        Guid authorPrincipalId, Guid authorSessionId, long authorAuthorizationRevision,
        string changeReason, DateTimeOffset recordedAt) =>
        Convert.ToHexString(SHA256.HashData(AuditCanonical.Encode("RecipeDraftRevision", draftId.ToString("D"),
            revision.ToString(CultureInfo.InvariantCulture), operationId.ToString("D"),
            previousRevisionContentHash, contentHash, payloadHash,
            authorPrincipalId.ToString("D"), authorSessionId.ToString("D"),
            authorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), changeReason,
            recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))));

    private static RecipeDraftHead? ReadRecipeDraftHead(sqlite3 database, Guid draftId, StoreDeadline deadline) =>
        ReadRecipeDraftRow(database, "WHERE DraftId=? ORDER BY Revision DESC LIMIT 1", deadline, draftId.ToString("D")).SingleOrDefault();

    private static RecipeDraftHead? ReadRecipeDraftByOperation(sqlite3 database, Guid operationId, StoreDeadline deadline) =>
        ReadRecipeDraftRow(database, "WHERE OperationId=? LIMIT 1", deadline, operationId.ToString("D")).SingleOrDefault();

    private static List<RecipeDraftHead> ReadRecipeDraftRow(sqlite3 database, string predicate,
        StoreDeadline deadline, params string?[] args) => AuditChainDatabase.Read(database, @"SELECT Position,DraftId,Revision,
            OperationId,PreviousRevisionContentHash,RevisionContentHash,PayloadHash,PayloadJson,AuthorPrincipalId,
            AuthorSessionId,AuthorAuthorizationRevision,ChangeReason,RecordedAtUtc FROM recipe_draft_revisions " + predicate + ";",
        deadline, s => new RecipeDraftHead(SqliteNative.ColumnInt64(s, 0), ParseGuid(SqliteNative.ColumnText(s, 1)),
            SqliteNative.ColumnInt64(s, 2), ParseGuid(SqliteNative.ColumnText(s, 3)), SqliteNative.ColumnText(s, 4),
            SqliteNative.ColumnText(s, 5)!, SqliteNative.ColumnText(s, 6)!, SqliteNative.ColumnText(s, 7)!,
            ParseGuid(SqliteNative.ColumnText(s, 8)), ParseGuid(SqliteNative.ColumnText(s, 9)),
            SqliteNative.ColumnInt64(s, 10), SqliteNative.ColumnText(s, 11)!, ParseTime(SqliteNative.ColumnText(s, 12))), args);

    private static RecipeDraftHead ReadRecipeDraftRow(sqlite3_stmt statement) => new(
        SqliteNative.ColumnInt64(statement, 0), ParseGuid(SqliteNative.ColumnText(statement, 1)),
        SqliteNative.ColumnInt64(statement, 2), ParseGuid(SqliteNative.ColumnText(statement, 3)),
        SqliteNative.ColumnText(statement, 4), SqliteNative.ColumnText(statement, 5)!,
        SqliteNative.ColumnText(statement, 6)!, SqliteNative.ColumnText(statement, 7)!,
        ParseGuid(SqliteNative.ColumnText(statement, 8)), ParseGuid(SqliteNative.ColumnText(statement, 9)),
        SqliteNative.ColumnInt64(statement, 10), SqliteNative.ColumnText(statement, 11)!,
        ParseTime(SqliteNative.ColumnText(statement, 12)));

    internal static RecipeDraftRevision ReadRecipeDraftRevisionAt(sqlite3 database, long position,
        StoreDeadline deadline, RecipeDraftStoreOptions options)
    {
        var row = ReadRecipeDraftRow(database, "WHERE Position=? LIMIT 1", deadline,
            position.ToString(CultureInfo.InvariantCulture)).SingleOrDefault();
        AuditChainDatabase.Require(row is not null, "RecipeDraftRevisionMissing");
        var found = row!;
        var binding = EncodeRecipeDraftBinding(found);
        _ = ReadAndValidateRecipeDraft(database, position, binding, deadline, options);
        return ToPublicRevision(database, found, deadline);
    }

    private static RecipeDraftRevision ToPublicRevision(sqlite3 database, RecipeDraftHead row, StoreDeadline deadline)
    {
        AuditChainDatabase.Require(RecipeDraftStorageCodec.TryDecodeContent(row.PayloadJson, row.PayloadHash,
            out var content, out var reason) && content is not null, reason);
        return new RecipeDraftRevision(row.Position, row.DraftId, row.Revision, row.OperationId,
            row.PreviousRevisionContentHash, row.RevisionContentHash, content!, row.AuthorPrincipalId,
            row.AuthorSessionId, row.AuthorAuthorizationRevision, row.ChangeReason, row.RecordedAtUtc);
    }

    private static byte[] EncodeRecipeDraftBinding(RecipeDraftHead row) => AuditCanonical.Encode(
        "RecipeDraftRevisionBinding", row.Position.ToString(CultureInfo.InvariantCulture), row.DraftId.ToString("D"),
        row.Revision.ToString(CultureInfo.InvariantCulture), row.OperationId.ToString("D"),
        row.PreviousRevisionContentHash, row.RevisionContentHash, row.PayloadHash,
        Encoding.UTF8.GetByteCount(row.PayloadJson).ToString(CultureInfo.InvariantCulture),
        row.AuthorPrincipalId.ToString("D"), row.AuthorSessionId.ToString("D"),
        row.AuthorAuthorizationRevision.ToString(CultureInfo.InvariantCulture), row.ChangeReason,
        row.RecordedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    private sealed record RecipeDraftConfig(int FormatVersion, string PolicyId, string PolicyVersion,
        string PolicyHash, int MaximumRecordBytes, int MaximumPageBytes, long MaximumTotalBytes,
        int MaximumRevisionCount, long RequireStepUp, string BindingHash);
}
