using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record ImageFinalizationAttemptStartRequest(Guid WorkId, Guid AttemptId,
    int AttemptNumber, Guid RuntimeEpoch, DateTimeOffset RecordedAtUtc, bool IntegrityRecovery,
    Func<string?>? FinalGuard = null);

internal sealed record ImageFinalizationFailureRequest(Guid WorkId, Guid AttemptId,
    Guid RuntimeEpoch, DateTimeOffset RecordedAtUtc, string ReasonCode,
    ProductionImageFailureCategory Category, DateTimeOffset? RetryAfterUtc, bool IntegrityRecovery,
    Func<string?>? FinalGuard = null);

internal sealed record ImageFinalizationSuccessRequest(Guid WorkId, Guid RuntimeEpoch,
    DateTimeOffset RecordedAtUtc, ProductionImageFinalizer.VerifiedPngCommitClaim Claim,
    Func<string?>? FinalGuard = null);

internal sealed record ImageFinalizationReleaseRequest(Guid WorkId, Guid RuntimeEpoch,
    DateTimeOffset RecordedAtUtc, string ReasonCode, Func<string?>? FinalGuard = null);

internal sealed record ImageFinalizationWriteResult(bool Committed, string ReasonCode,
    ProductionImageFinalizationEvent? Event = null,
    ProductionImageFinalizationState? State = null,
    ProductionImageCleanupState? CleanupState = null,
    ImageBacklogSnapshot? Backlog = null);

internal sealed class ImageFinalizationWork
{
    internal ImageFinalizationWork(ImageFinalizationAttemptStartRequest request) =>
        Start = request ?? throw new ArgumentNullException(nameof(request));
    internal ImageFinalizationWork(ImageFinalizationFailureRequest request) =>
        Failure = request ?? throw new ArgumentNullException(nameof(request));
    internal ImageFinalizationWork(ImageFinalizationSuccessRequest request) =>
        Success = request ?? throw new ArgumentNullException(nameof(request));
    internal ImageFinalizationWork(ImageFinalizationReleaseRequest request) =>
        Release = request ?? throw new ArgumentNullException(nameof(request));

    internal ImageFinalizationAttemptStartRequest? Start { get; }
    internal ImageFinalizationFailureRequest? Failure { get; }
    internal ImageFinalizationSuccessRequest? Success { get; }
    internal ImageFinalizationReleaseRequest? Release { get; }
    internal TaskCompletionSource<ImageFinalizationWriteResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Schema-35 image finalization writer. Every fact is written through the one existing
/// SqliteCommandStore queue and transaction: no second writer connection exists, no file is
/// opened here, and the main-owned verified commit claim is only verified near COMMIT and
/// consumed after the final guard immediately before COMMIT.
/// </summary>
internal sealed partial class SqliteCommandStore
{
    private sealed record ImageFinalizationWorkFacts(Guid WorkId, Guid InspectionId, Guid ManifestId,
        string WorkContentHash, string ManifestContentHash, PendingImageFinalizationWork Work);

    internal ValueTask<ImageFinalizationWriteResult> BeginImageFinalizationAttemptAsync(
        ImageFinalizationAttemptStartRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) =>
        EnqueueImageFinalizationAsync(new ImageFinalizationWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    internal ValueTask<ImageFinalizationWriteResult> AppendImageFinalizationOutcomeAsync(
        ImageFinalizationFailureRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) =>
        EnqueueImageFinalizationAsync(new ImageFinalizationWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    internal ValueTask<ImageFinalizationWriteResult> AppendImageFinalizationOutcomeAsync(
        ImageFinalizationSuccessRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) =>
        EnqueueImageFinalizationAsync(new ImageFinalizationWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    internal ValueTask<ImageFinalizationWriteResult> RecordImageStageReleasedAsync(
        ImageFinalizationReleaseRequest request, StoreDeadline deadline,
        CancellationToken cancellationToken = default) =>
        EnqueueImageFinalizationAsync(new ImageFinalizationWork(
            request ?? throw new ArgumentNullException(nameof(request))), deadline, cancellationToken);

    private async ValueTask<ImageFinalizationWriteResult> EnqueueImageFinalizationAsync(
        ImageFinalizationWork work, StoreDeadline deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ImageFinalizationEnabled || Volatile.Read(ref _disposed) != 0 || _queue is null ||
            _queueSlots is null || _worker.IsCompleted)
            return new(false, ImageFinalizationEnabled ? "ImageFinalizationUnavailable" :
                "ImageFinalizationConfigurationRequired");
        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false))
            return new(false, "ImageFinalizationCommitDeadlineExceeded");
        var queued = new WriteRequest(null, deadline, ImageFinalization: work);
        if (!_queue.Writer.TryWrite(queued))
        {
            _queueSlots.Release();
            return new(false, Volatile.Read(ref _disposed) != 0 ? "TraceStoreDisposed" :
                "ImageFinalizationUnavailable");
        }
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private StoreWriteResult AppendImageFinalizationCore(sqlite3 database,
        ImageFinalizationWork work, StoreDeadline deadline)
    {
        if (work.Start?.IntegrityRecovery == true || work.Failure?.IntegrityRecovery == true)
            return ImageFinalizationRejected(work, "ImageFinalizationGovernedRecoveryUnavailable");
        if (Integrity?.State == AuditIntegrityState.Faulted)
            return ImageFinalizationRejected(work, Integrity.ReasonCode);
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return ImageFinalizationRejected(work, "TraceStoreWalLimit");
        if (work.Start is not null) return AppendImageFinalizationAttemptStart(database, work, deadline);
        if (work.Failure is not null) return AppendImageFinalizationFailure(database, work, deadline);
        if (work.Success is not null) return AppendImageFinalizationSuccess(database, work, deadline);
        if (work.Release is not null) return AppendImageFinalizationRelease(database, work, deadline);
        throw new InvalidOperationException("ImageFinalizationWriteInvalid");
    }

    private StoreWriteResult AppendImageFinalizationAttemptStart(sqlite3 database,
        ImageFinalizationWork work, StoreDeadline deadline)
    {
        var request = work.Start ?? throw new InvalidOperationException("ImageFinalizationWriteInvalid");
        var options = _options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            RequireImageFinalizationRequestIdentity(request.WorkId, request.AttemptId,
                request.AttemptNumber, request.RuntimeEpoch, request.RecordedAtUtc);
            // The qualified final root is revalidated outside the mutation transaction.
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredImageFinalization(database, options, deadline);
            AuditChainDatabase.RequireFullImageFinalizationVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadImageFinalizationWorkFacts(database, request.WorkId, deadline);
            var events = ReadImageFinalizationWorkEvents(database, options, facts.WorkId, deadline);
            var existing = events.SingleOrDefault(value =>
                value.Kind == ProductionImageFinalizationKind.AttemptStarted &&
                value.AttemptId == request.AttemptId);
            if (existing is not null)
                return existing.AttemptNumber == request.AttemptNumber &&
                    existing.RuntimeEpoch == request.RuntimeEpoch &&
                    existing.WorkContentHash == facts.WorkContentHash &&
                    existing.ManifestContentHash == facts.ManifestContentHash
                    ? ImageFinalizationIdempotent(work, existing, database, deadline)
                    : ImageFinalizationRejected(work, "ImageFinalizationEventConflict");
            var state = ReadImageFinalizationWorkState(database, facts, deadline);
            if (state is not null)
            {
                if (state.State == ProductionImageFinalizationState.Succeeded)
                    return ImageFinalizationRejected(work, "ImageFinalizationAlreadySucceeded");
                if (state.ActiveAttemptId is not null)
                    return ImageFinalizationRejected(work, "ImageFinalizationAttemptAlreadyActive");
                if (!state.RetryEligible)
                    return ImageFinalizationRejected(work, "ImageFinalizationIntegrityConflictRecorded");
                if (state.RetryAfterUtc is { } retryAfter && DateTimeOffset.UtcNow < retryAfter)
                    return ImageFinalizationRejected(work, "ImageFinalizationRetryWindowOpen");
                if (request.AttemptNumber != state.NextAttemptNumber)
                    return ImageFinalizationRejected(work, "ImageFinalizationAttemptNumberInvalid");
            }
            else if (request.AttemptNumber != 1)
            {
                return ImageFinalizationRejected(work, "ImageFinalizationAttemptNumberInvalid");
            }
            if (request.AttemptNumber > options.MaximumAttempts)
                return ImageFinalizationRejected(work, "ImageFinalizationAttemptBudgetExhausted");
            var reserve = ReadImageFinalizationReserveRows(database, deadline);
            // This attempt adds one active attempt, whose terminal fact is reserved; the
            // AttemptStarted fact itself is the write being admitted.
            var futureReserve = checked(reserve.Events +
                ProductionImageFinalizationStoreOptions.ReserveEventsPerActiveAttempt);
            var position = NextImageFinalizationPosition(database, deadline);
            var aggregate = NextImageFinalizationAggregate(database, facts.WorkId, deadline);
            var attempt = new ProductionImageAttemptDescriptor(request.AttemptId,
                request.AttemptNumber, options.MaximumAttempts,
                options.MaximumAttempts - request.AttemptNumber,
                options.FinalRootBindingHash,
                ProductionImageFinalizationStorageCodec.StableFinalFileName(facts.ManifestId),
                ProductionImageFinalizationStorageCodec.UniqueTemporaryFileName(facts.ManifestId,
                    request.AttemptId));
            var provisional = new ProductionImageFinalizationEvent(position,
                ProductionImageFinalizationStorageCodec.DeriveEventId(position, facts.WorkId,
                    facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                    facts.ManifestContentHash, aggregate, request.AttemptId, request.AttemptNumber,
                    request.RuntimeEpoch, request.RecordedAtUtc,
                    ProductionImageFinalizationKind.AttemptStarted, "ImageFinalizationAttemptStarted",
                    attempt, null, null),
                facts.WorkId, facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                facts.ManifestContentHash, aggregate, request.AttemptId, request.AttemptNumber,
                request.RuntimeEpoch, request.RecordedAtUtc,
                ProductionImageFinalizationKind.AttemptStarted, "ImageFinalizationAttemptStarted",
                attempt, null, null);
            var auditPayload = ProductionImageFinalizationStorageCodec.EncodeAuditPayload(provisional);
            EnsureImageFinalizationEventCapacity(database, options, futureReserve,
                EncodedImageFinalizationEventLength(provisional), deadline);
            var audit = AuditChainDatabase.AppendImageFinalizationEvent(database, policy, signingKey,
                provisional.Kind, auditPayload, options, futureReserve, deadline);
            var persisted = Persisted(provisional, audit.Sequence, audit.Hash);
            InsertImageFinalizationEvent(database, options, persisted, deadline);
            UpsertImageFinalizationWorkState(database, facts,
                events.Append(persisted).ToArray(), deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ImageFinalizationRejected(work, guardReason);
            var backlog = ReadImageBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "ImageFinalizationAttemptStarted",
                persisted, ProductionImageFinalizationState.Pending,
                ProductionImageCleanupState.Pending, backlog));
            PublishProductionInspectionIntegrity(policy, audit.Sequence);
            return new(true, "ImageFinalizationAttemptStarted");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ImageFinalization",
            StringComparison.Ordinal))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ImageFinalizationRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ImageFinalizationAttemptFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendImageFinalizationFailure(sqlite3 database,
        ImageFinalizationWork work, StoreDeadline deadline)
    {
        var request = work.Failure ?? throw new InvalidOperationException("ImageFinalizationWriteInvalid");
        var options = _options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            RequireImageFinalizationRequestIdentity(request.WorkId, request.AttemptId, null,
                request.RuntimeEpoch, request.RecordedAtUtc);
            var failure = new ProductionImageFailureDescriptor(request.Category,
                request.Category == ProductionImageFailureCategory.Temporary
                    ? request.RetryAfterUtc ?? request.RecordedAtUtc
                    : null);
            if (failure.RetryAfterUtc is { } retryAfter &&
                (retryAfter < request.RecordedAtUtc ||
                 retryAfter > request.RecordedAtUtc.Add(options.MaximumRetryDelay)))
                return ImageFinalizationRejected(work, "ImageFinalizationRetryDelayInvalid");
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredImageFinalization(database, options, deadline);
            AuditChainDatabase.RequireFullImageFinalizationVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadImageFinalizationWorkFacts(database, request.WorkId, deadline);
            var events = ReadImageFinalizationWorkEvents(database, options, facts.WorkId, deadline);
            var existing = events.SingleOrDefault(value =>
                value.Kind == ProductionImageFinalizationKind.AttemptFailed &&
                value.AttemptId == request.AttemptId);
            if (existing is not null)
                return string.Equals(existing.ReasonCode, request.ReasonCode, StringComparison.Ordinal) &&
                    existing.Failure!.Category == request.Category &&
                    existing.Failure.RetryAfterUtc == failure.RetryAfterUtc &&
                    existing.RuntimeEpoch == request.RuntimeEpoch
                    ? ImageFinalizationIdempotent(work, existing, database, deadline)
                    : ImageFinalizationRejected(work, "ImageFinalizationEventConflict");
            var state = ReadImageFinalizationWorkState(database, facts, deadline);
            if (state?.ActiveAttemptId != request.AttemptId)
                return ImageFinalizationRejected(work, "ImageFinalizationAttemptRequired");
            var attemptNumber = state!.NextAttemptNumber - 1;
            var attempt = new ProductionImageAttemptDescriptor(request.AttemptId, attemptNumber,
                options.MaximumAttempts, options.MaximumAttempts - attemptNumber,
                options.FinalRootBindingHash,
                ProductionImageFinalizationStorageCodec.StableFinalFileName(facts.ManifestId),
                ProductionImageFinalizationStorageCodec.UniqueTemporaryFileName(facts.ManifestId,
                    request.AttemptId));
            var reserve = ReadImageFinalizationReserveRows(database, deadline);
            // The active attempt ends here; its terminal fact is this one.
            var futureReserve = checked(reserve.Events - 1);
            var position = NextImageFinalizationPosition(database, deadline);
            var aggregate = NextImageFinalizationAggregate(database, facts.WorkId, deadline);
            var provisional = new ProductionImageFinalizationEvent(position,
                ProductionImageFinalizationStorageCodec.DeriveEventId(position, facts.WorkId,
                    facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                    facts.ManifestContentHash, aggregate, request.AttemptId, attemptNumber,
                    request.RuntimeEpoch, request.RecordedAtUtc,
                    ProductionImageFinalizationKind.AttemptFailed, request.ReasonCode,
                    attempt, failure, null),
                facts.WorkId, facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                facts.ManifestContentHash, aggregate, request.AttemptId, attemptNumber,
                request.RuntimeEpoch, request.RecordedAtUtc,
                ProductionImageFinalizationKind.AttemptFailed, request.ReasonCode,
                attempt, failure, null);
            var auditPayload = ProductionImageFinalizationStorageCodec.EncodeAuditPayload(provisional);
            EnsureImageFinalizationEventCapacity(database, options, futureReserve,
                EncodedImageFinalizationEventLength(provisional), deadline);
            var audit = AuditChainDatabase.AppendImageFinalizationEvent(database, policy, signingKey,
                provisional.Kind, auditPayload, options, futureReserve, deadline);
            var persisted = Persisted(provisional, audit.Sequence, audit.Hash);
            InsertImageFinalizationEvent(database, options, persisted, deadline);
            UpsertImageFinalizationWorkState(database, facts,
                events.Append(persisted).ToArray(), deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ImageFinalizationRejected(work, guardReason);
            var backlog = ReadImageBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "ImageFinalizationAttemptFailed",
                persisted, ProductionImageFinalizationState.Failed,
                ProductionImageCleanupState.Pending, backlog));
            PublishProductionInspectionIntegrity(policy, audit.Sequence);
            return new(true, "ImageFinalizationAttemptFailed");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ImageFinalization",
            StringComparison.Ordinal))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ImageFinalizationRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ImageFinalizationOutcomeFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendImageFinalizationSuccess(sqlite3 database,
        ImageFinalizationWork work, StoreDeadline deadline)
    {
        var request = work.Success ?? throw new InvalidOperationException("ImageFinalizationWriteInvalid");
        var options = _options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            var claim = request.Claim ?? throw new InvalidOperationException(
                "ImageFinalizationCommitClaimRequired");
            if (claim.WorkId == Guid.Empty || request.RuntimeEpoch == Guid.Empty)
                throw new InvalidOperationException("ImageFinalizationRequestIdentityInvalid");
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredImageFinalization(database, options, deadline);
            AuditChainDatabase.RequireFullImageFinalizationVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadImageFinalizationWorkFacts(database, claim.WorkId, deadline);
            var events = ReadImageFinalizationWorkEvents(database, options, facts.WorkId, deadline);
            var existing = events.SingleOrDefault(value =>
                value.Kind == ProductionImageFinalizationKind.Succeeded);
            if (existing is not null)
                return existing.AttemptId == claim.AttemptId &&
                    existing.Success!.EncodedByteLength == claim.EncodedByteLength &&
                    string.Equals(existing.Success.CanonicalPixelHash, claim.CanonicalPixelHash,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Success.FinalRootBindingHash, claim.FinalRootBindingHash,
                        StringComparison.OrdinalIgnoreCase) &&
                    existing.Success.FinalFileName == claim.FinalFileName
                    ? ImageFinalizationIdempotent(work, existing, database, deadline)
                    : ImageFinalizationRejected(work, "ImageFinalizationEventConflict");
            RequireImageFinalizationClaimBindings(options, facts, claim);
            var state = ReadImageFinalizationWorkState(database, facts, deadline) ??
                throw new InvalidOperationException("ImageFinalizationAttemptRequired");
            if (state.IntegrityConflict)
                return ImageFinalizationRejected(work, "ImageFinalizationIntegrityConflictRecorded");
            var failedAttempt = events.LastOrDefault(value =>
                value.Kind == ProductionImageFinalizationKind.AttemptFailed &&
                value.AttemptId == claim.AttemptId);
            var normal = state.ActiveAttemptId == claim.AttemptId;
            var recovered = !normal && failedAttempt is not null &&
                events.LastOrDefault(value => value.AttemptId is not null)?.AttemptId == claim.AttemptId;
            if (!normal && !recovered)
                return ImageFinalizationRejected(work, "ImageFinalizationAttemptRequired");
            var attemptNumber = events.LastOrDefault(value =>
                value.Kind == ProductionImageFinalizationKind.AttemptStarted &&
                value.AttemptId == claim.AttemptId)?.AttemptNumber ??
                throw new InvalidOperationException("ImageFinalizationAttemptRequired");
            var manifest = facts.Work.Manifest;
            var success = new ProductionImageSuccessDescriptor(claim.FinalRootBindingHash,
                claim.FinalFileName, claim.EncodedByteLength, manifest.Width, manifest.Height,
                manifest.PixelFormat, manifest.ValidBits, manifest.HashScheme,
                manifest.HashSchemeVersion, claim.CanonicalPixelHash);
            var reserve = ReadImageFinalizationReserveRows(database, deadline);
            // One open obligation becomes a succeeded-but-unreleased work: the completion event
            // (and, for a successfully finishing active attempt, its terminal reserve) is
            // consumed while the release event stays reserved.
            var futureReserve = checked(reserve.Events - (normal ? 2 : 1));
            var position = NextImageFinalizationPosition(database, deadline);
            var aggregate = NextImageFinalizationAggregate(database, facts.WorkId, deadline);
            var attempt = new ProductionImageAttemptDescriptor(claim.AttemptId, attemptNumber,
                options.MaximumAttempts, options.MaximumAttempts - attemptNumber,
                options.FinalRootBindingHash, success.FinalFileName,
                ProductionImageFinalizationStorageCodec.UniqueTemporaryFileName(facts.ManifestId,
                    claim.AttemptId));
            var provisional = new ProductionImageFinalizationEvent(position,
                ProductionImageFinalizationStorageCodec.DeriveEventId(position, facts.WorkId,
                    facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                    facts.ManifestContentHash, aggregate, claim.AttemptId, attemptNumber,
                    request.RuntimeEpoch, request.RecordedAtUtc,
                    ProductionImageFinalizationKind.Succeeded, "ImageFinalizationSucceeded",
                    attempt, null, success),
                facts.WorkId, facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                facts.ManifestContentHash, aggregate, claim.AttemptId, attemptNumber,
                request.RuntimeEpoch, request.RecordedAtUtc,
                ProductionImageFinalizationKind.Succeeded, "ImageFinalizationSucceeded",
                attempt, null, success);
            var auditPayload = ProductionImageFinalizationStorageCodec.EncodeAuditPayload(provisional);
            EnsureImageFinalizationEventCapacity(database, options, futureReserve,
                EncodedImageFinalizationEventLength(provisional), deadline);
            var audit = AuditChainDatabase.AppendImageFinalizationEvent(database, policy, signingKey,
                provisional.Kind, auditPayload, options, futureReserve, deadline);
            var persisted = Persisted(provisional, audit.Sequence, audit.Hash);
            InsertImageFinalizationEvent(database, options, persisted, deadline);
            UpsertImageFinalizationWorkState(database, facts,
                events.Append(persisted).ToArray(), deadline);
            // The main-owned claim is verified and consumed only after every binding and row is
            // durable in this transaction, and only after the caller's final guard.
            claim.VerifyCommitProtection();
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ImageFinalizationRejected(work, guardReason);
            claim.ConsumeForCommit(facts.Work, claim.AttemptId, options.FinalRootBindingHash,
                success.FinalFileName);
            var backlog = ReadImageBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "ImageFinalizationSucceeded",
                persisted, ProductionImageFinalizationState.Succeeded,
                ProductionImageCleanupState.Pending, backlog));
            PublishProductionInspectionIntegrity(policy, audit.Sequence);
            return new(true, "ImageFinalizationSucceeded");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ImageFinalization",
            StringComparison.Ordinal))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ImageFinalizationRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ImageFinalizationSucceededFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private StoreWriteResult AppendImageFinalizationRelease(sqlite3 database,
        ImageFinalizationWork work, StoreDeadline deadline)
    {
        var request = work.Release ?? throw new InvalidOperationException("ImageFinalizationWriteInvalid");
        var options = _options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        var signingKey = _signingKey ?? throw new InvalidOperationException("AuditSigningKeyUnavailable");
        options.Validate();
        var started = false;
        var committed = false;
        try
        {
            RequireImageFinalizationRequestIdentity(request.WorkId, null, null, request.RuntimeEpoch,
                request.RecordedAtUtc);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredImageFinalization(database, options, deadline);
            AuditChainDatabase.RequireFullImageFinalizationVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, options);
            var facts = ReadImageFinalizationWorkFacts(database, request.WorkId, deadline);
            var events = ReadImageFinalizationWorkEvents(database, options, facts.WorkId, deadline);
            var existing = events.SingleOrDefault(value =>
                value.Kind == ProductionImageFinalizationKind.StageReleased);
            if (existing is not null)
                return string.Equals(existing.ReasonCode, request.ReasonCode, StringComparison.Ordinal) &&
                    existing.RuntimeEpoch == request.RuntimeEpoch
                    ? ImageFinalizationIdempotent(work, existing, database, deadline)
                    : ImageFinalizationRejected(work, "ImageFinalizationEventConflict");
            var state = ReadImageFinalizationWorkState(database, facts, deadline);
            if (state?.State != ProductionImageFinalizationState.Succeeded)
                return ImageFinalizationRejected(work, "ImageFinalizationSucceededRequired");
            if (state.CleanupState == ProductionImageCleanupState.Released)
                return ImageFinalizationRejected(work, "ImageFinalizationAlreadyReleased");
            var reserve = ReadImageFinalizationReserveRows(database, deadline);
            var futureReserve = checked(reserve.Events - 1);
            var position = NextImageFinalizationPosition(database, deadline);
            var aggregate = NextImageFinalizationAggregate(database, facts.WorkId, deadline);
            var provisional = new ProductionImageFinalizationEvent(position,
                ProductionImageFinalizationStorageCodec.DeriveEventId(position, facts.WorkId,
                    facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                    facts.ManifestContentHash, aggregate, null, null, request.RuntimeEpoch,
                    request.RecordedAtUtc, ProductionImageFinalizationKind.StageReleased,
                    request.ReasonCode, null, null, null),
                facts.WorkId, facts.ManifestId, facts.InspectionId, facts.WorkContentHash,
                facts.ManifestContentHash, aggregate, null, null, request.RuntimeEpoch,
                request.RecordedAtUtc, ProductionImageFinalizationKind.StageReleased,
                request.ReasonCode, null, null, null);
            var auditPayload = ProductionImageFinalizationStorageCodec.EncodeAuditPayload(provisional);
            EnsureImageFinalizationEventCapacity(database, options, futureReserve,
                EncodedImageFinalizationEventLength(provisional), deadline);
            var audit = AuditChainDatabase.AppendImageFinalizationEvent(database, policy, signingKey,
                provisional.Kind, auditPayload, options, futureReserve, deadline);
            var persisted = Persisted(provisional, audit.Sequence, audit.Hash);
            InsertImageFinalizationEvent(database, options, persisted, deadline);
            // A cleanup failure never reverts Succeeded: this write either commits the release
            // or leaves the succeeded projection untouched.
            UpsertImageFinalizationWorkState(database, facts,
                events.Append(persisted).ToArray(), deadline);
            var guardReason = EvaluateProductionFinalGuard(request.FinalGuard);
            if (guardReason is not null)
                return ImageFinalizationRejected(work, guardReason);
            var backlog = ReadImageBacklogSnapshot(database, audit.Sequence, deadline);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            work.Completion.TrySetResult(new(true, "ImageFinalizationStageReleased",
                persisted, ProductionImageFinalizationState.Succeeded,
                ProductionImageCleanupState.Released, backlog));
            PublishProductionInspectionIntegrity(policy, audit.Sequence);
            return new(true, "ImageFinalizationStageReleased");
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("ImageFinalization",
            StringComparison.Ordinal))
        { return ImageFinalizationRejected(work, ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return ImageFinalizationRejected(work, SqliteAuditIntegrityQuery.FaultReason(ex,
            "ImageFinalizationReleaseFailed")); }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private static StoreWriteResult ImageFinalizationRejected(ImageFinalizationWork work, string reason)
    {
        work.Completion.TrySetResult(new(false, reason));
        return new(false, reason);
    }

    private static StoreWriteResult ImageFinalizationIdempotent(ImageFinalizationWork work,
        ProductionImageFinalizationEvent existing, sqlite3 database, StoreDeadline deadline)
    {
        var state = existing.Kind == ProductionImageFinalizationKind.StageReleased
            ? ProductionImageCleanupState.Released : ProductionImageCleanupState.Pending;
        var finalization = existing.Kind switch
        {
            ProductionImageFinalizationKind.AttemptFailed => ProductionImageFinalizationState.Failed,
            ProductionImageFinalizationKind.Succeeded or ProductionImageFinalizationKind.StageReleased =>
                ProductionImageFinalizationState.Succeeded,
            _ => ProductionImageFinalizationState.Pending
        };
        var backlog = ReadImageBacklogSnapshot(database,
            AuditChainDatabase.Tail(database, deadline).Sequence, deadline);
        work.Completion.TrySetResult(new(true, "ImageFinalizationEventDuplicate", existing,
            finalization, state, backlog));
        return new(true, "ImageFinalizationEventDuplicate");
    }

    private static void RequireImageFinalizationRequestIdentity(Guid workId, Guid? attemptId,
        int? attemptNumber, Guid runtimeEpoch, DateTimeOffset recordedAtUtc)
    {
        if (workId == Guid.Empty || runtimeEpoch == Guid.Empty ||
            attemptId == Guid.Empty || attemptNumber is < 1)
            throw new InvalidOperationException("ImageFinalizationRequestIdentityInvalid");
        if (recordedAtUtc == default || recordedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("ImageFinalizationRecordedAtInvalid");
    }

    private static void RequireImageFinalizationClaimBindings(
        ProductionImageFinalizationStoreOptions options, ImageFinalizationWorkFacts facts,
        ProductionImageFinalizer.VerifiedPngCommitClaim claim)
    {
        if (claim.IsConsumed)
            throw new InvalidOperationException("ImageFinalizationCommitClaimConsumed");
        var manifest = facts.Work.Manifest;
        if (claim.ManifestId != facts.ManifestId || claim.InspectionId != facts.InspectionId ||
            !string.Equals(claim.WorkContentHash, facts.WorkContentHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(claim.ManifestContentHash, facts.ManifestContentHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(claim.FinalRootBindingHash, options.FinalRootBindingHash,
                StringComparison.OrdinalIgnoreCase) ||
            claim.FinalFileName != ProductionImageFinalizationStorageCodec.StableFinalFileName(
                facts.ManifestId) ||
            !string.Equals(claim.CanonicalPixelHash, manifest.CanonicalPixelHash,
                StringComparison.OrdinalIgnoreCase) ||
            claim.EncodedByteLength < 1 || claim.AttemptId == Guid.Empty)
            throw new InvalidOperationException("ImageFinalizationCommitClaimMismatch");
    }

    private static long EncodedImageFinalizationEventLength(
        ProductionImageFinalizationEvent provisional) =>
        ProductionImageFinalizationStorageCodec.EncodeEvent(
            Persisted(provisional, 1, new string('0', 64))).Length;

    private static ImageFinalizationWorkFacts ReadImageFinalizationWorkFacts(
        sqlite3 database, Guid workId, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT w.WorkId,w.InspectionId,w.ManifestId,w.ContentHash,w.Payload,m.ContentHash,m.Payload
            FROM pending_image_work w JOIN pending_image_manifests m ON m.ManifestId=w.ManifestId
            WHERE w.WorkId=? LIMIT 2;", deadline, statement => new[]
        {
            SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            SqliteNative.ColumnText(statement, 1) ?? string.Empty,
            SqliteNative.ColumnText(statement, 2) ?? string.Empty,
            SqliteNative.ColumnText(statement, 3) ?? string.Empty,
            SqliteNative.ColumnText(statement, 4) ?? string.Empty,
            SqliteNative.ColumnText(statement, 5) ?? string.Empty,
            SqliteNative.ColumnText(statement, 6) ?? string.Empty
        }, workId.ToString("D"));
        AuditChainDatabase.Require(rows.Count == 1 && rows[0][0] == workId.ToString("D"),
            "ImageFinalizationWorkRequired");
        var row = rows[0];
        PendingImageFinalizationWork work;
        PendingImageManifest manifest;
        try
        {
            work = ProductionInspectionStorageCodec.DecodeImageWork(
                Convert.FromBase64String(row[4]));
            manifest = ProductionInspectionStorageCodec.DecodeImageManifest(
                Convert.FromBase64String(row[6]));
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        { throw new InvalidOperationException("ImageFinalizationWorkBindingMismatch", exception); }
        AuditChainDatabase.Require(work.WorkId == workId && work.ContentHash == row[3] &&
            work.Manifest.ContentHash == row[5] && manifest.ContentHash == row[5] &&
            manifest.ManifestId.ToString("D") == row[2] &&
            manifest.InspectionId.ToString("D") == row[1] && work.Manifest.ContentHash == row[5],
            "ImageFinalizationWorkBindingMismatch");
        return new(workId, manifest.InspectionId, manifest.ManifestId, work.ContentHash,
            manifest.ContentHash, work);
    }

    private static IReadOnlyList<ProductionImageFinalizationEvent> ReadImageFinalizationWorkEvents(
        sqlite3 database, ProductionImageFinalizationStoreOptions options, Guid workId,
        StoreDeadline deadline) =>
        ReadImageFinalizationRows(database, options, deadline)
            .Where(row => row.Event.WorkId == workId)
            .Select(row => row.Event)
            .ToArray();

    private static ProductionImageFinalizationWorkState? ReadImageFinalizationWorkState(
        sqlite3 database, ImageFinalizationWorkFacts facts, StoreDeadline deadline)
    {
        var rows = AuditChainDatabase.Read(database, @"
            SELECT State,CleanupState,AttemptCount,NextAttemptNumber,RetryEligible,IntegrityConflict,
                LastFailureReasonCode,LastFailureCategory,RetryAfterUtc,SuccessFinalRootBindingHash,
                SuccessFinalFileName,SuccessEncodedByteLength,SuccessWidth,SuccessHeight,
                SuccessPixelFormat,SuccessValidBits,SuccessCanonicalHashScheme,
                SuccessCanonicalHashSchemeVersion,SuccessCanonicalPixelHash,SuccessContentHash,
                LastEventPosition,LastEventContentHash,ActiveAttemptId
            FROM image_finalization_work WHERE WorkId=? LIMIT 2;", deadline, statement =>
                (State: SqliteNative.ColumnText(statement, 0),
                    Cleanup: SqliteNative.ColumnText(statement, 1),
                    AttemptCount: SqliteNative.ColumnInt64(statement, 2),
                    NextAttempt: SqliteNative.ColumnInt64(statement, 3),
                    RetryEligible: SqliteNative.ColumnInt64(statement, 4),
                    Conflict: SqliteNative.ColumnInt64(statement, 5),
                    Reason: SqliteNative.ColumnText(statement, 6),
                    Category: SqliteNative.ColumnText(statement, 7),
                    RetryAfter: SqliteNative.ColumnText(statement, 8),
                    SuccessRoot: SqliteNative.ColumnText(statement, 9),
                    SuccessFileName: SqliteNative.ColumnText(statement, 10),
                    SuccessLength: SqliteNative.ColumnInt64Nullable(statement, 11),
                    SuccessWidth: SqliteNative.ColumnInt64Nullable(statement, 12),
                    SuccessHeight: SqliteNative.ColumnInt64Nullable(statement, 13),
                    SuccessFormat: SqliteNative.ColumnInt64Nullable(statement, 14),
                    SuccessValidBits: SqliteNative.ColumnInt64Nullable(statement, 15),
                    SuccessScheme: SqliteNative.ColumnText(statement, 16),
                    SuccessSchemeVersion: SqliteNative.ColumnInt64Nullable(statement, 17),
                    SuccessHash: SqliteNative.ColumnText(statement, 18),
                    SuccessContentHash: SqliteNative.ColumnText(statement, 19),
                    LastPosition: SqliteNative.ColumnInt64(statement, 20),
                    LastHash: SqliteNative.ColumnText(statement, 21) ?? string.Empty,
                    Active: SqliteNative.ColumnText(statement, 22)),
            facts.WorkId.ToString("D"));
        if (rows.Count == 0) return null;
        var row = rows[0];
        var state = ParseImageFinalizationState(row.State);
        var cleanup = row.Cleanup switch
        {
            "Pending" => ProductionImageCleanupState.Pending,
            "Released" => ProductionImageCleanupState.Released,
            _ => throw new InvalidOperationException("ImageFinalizationWorkStateInvalid")
        };
        ProductionImageSuccessDescriptor? success = null;
        if (row.SuccessFileName is not null)
        {
            if (row.SuccessRoot is null || row.SuccessLength is null or < 1 ||
                row.SuccessWidth is null or < 1 || row.SuccessHeight is null or < 1 ||
                row.SuccessFormat is null || row.SuccessScheme is null ||
                row.SuccessSchemeVersion is null || row.SuccessHash is null ||
                row.SuccessContentHash is null)
                throw new InvalidOperationException("ImageFinalizationWorkStateInvalid");
            success = new ProductionImageSuccessDescriptor(row.SuccessRoot, row.SuccessFileName,
                row.SuccessLength.Value, checked((int)row.SuccessWidth.Value),
                checked((int)row.SuccessHeight.Value),
                (VisionPixelFormat)checked((byte)row.SuccessFormat.Value),
                row.SuccessValidBits is { } bits ? checked((int)bits) : null, row.SuccessScheme,
                checked((int)row.SuccessSchemeVersion.Value), row.SuccessHash);
            if (success.ContentHash != row.SuccessContentHash!.ToUpperInvariant())
                throw new InvalidOperationException("ImageFinalizationWorkStateInvalid");
        }
        var failure = row.Category switch
        {
            "Temporary" => ProductionImageFailureCategory.Temporary,
            "Integrity" => ProductionImageFailureCategory.Integrity,
            null => (ProductionImageFailureCategory?)null,
            _ => throw new InvalidOperationException("ImageFinalizationWorkStateInvalid")
        };
        DateTimeOffset? retryAfter = null;
        if (row.RetryAfter is not null)
        {
            if (!DateTimeOffset.TryParse(row.RetryAfter, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed) ||
                parsed.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("ImageFinalizationWorkStateInvalid");
            retryAfter = parsed;
        }
        return new ProductionImageFinalizationWorkState(facts.WorkId, facts.ManifestId,
            facts.InspectionId, facts.WorkContentHash, facts.ManifestContentHash, state, cleanup,
            checked((int)row.AttemptCount), checked((int)row.NextAttempt), row.RetryEligible == 1,
            row.Reason, failure, retryAfter, success, row.LastPosition, row.LastHash,
            row.Active is null ? null : ParseImageFinalizationGuid(row.Active), row.Conflict == 1);
    }

    private static ProductionImageFinalizationState ParseImageFinalizationState(string? value) =>
        value switch
        {
            "Pending" => ProductionImageFinalizationState.Pending,
            "Failed" => ProductionImageFinalizationState.Failed,
            "Succeeded" => ProductionImageFinalizationState.Succeeded,
            _ => throw new InvalidOperationException("ImageFinalizationWorkStateInvalid")
        };

    private static long NextImageFinalizationPosition(sqlite3 database, StoreDeadline deadline) =>
        checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(Position),0)+1 FROM image_finalization_events;", deadline));

    private static long NextImageFinalizationAggregate(sqlite3 database, Guid workId,
        StoreDeadline deadline) =>
        checked(AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(MAX(AggregateSequence),0)+1 FROM image_finalization_events WHERE WorkId=?;",
            deadline, workId.ToString("D")));

    private static ProductionImageFinalizationEvent Persisted(
        ProductionImageFinalizationEvent provisional, long auditSequence, string auditHash) =>
        new(provisional.Position, provisional.EventId, provisional.WorkId, provisional.ManifestId,
            provisional.InspectionId, provisional.WorkContentHash, provisional.ManifestContentHash,
            provisional.AggregateSequence, provisional.AttemptId, provisional.AttemptNumber,
            provisional.RuntimeEpoch, provisional.RecordedAtUtc, provisional.Kind,
            provisional.ReasonCode, provisional.Attempt, provisional.Failure, provisional.Success,
            auditSequence, auditHash, provisional.ContentHash);

    private static void InsertImageFinalizationEvent(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, ProductionImageFinalizationEvent value,
        StoreDeadline deadline)
    {
        var payload = ProductionImageFinalizationStorageCodec.EncodeEvent(value);
        AuditChainDatabase.Require(payload.Length <= options.MaximumPayloadBytes &&
            payload.Length <= ProductionImageFinalizationStoreOptions.MaximumPayloadBytesHardLimit,
            "ImageFinalizationPayloadCapacityExceeded");
        AuditChainDatabase.Execute(database, @"
            INSERT INTO image_finalization_events(
                Position,EventId,WorkId,ManifestId,InspectionId,WorkContentHash,ManifestContentHash,
                AggregateSequence,AttemptId,AttemptNumber,RuntimeEpoch,RecordedAtUtc,PrincipalId,Kind,
                ReasonCode,ContentHash,PayloadHash,Payload,AuditSequence,AuditHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?);", deadline,
            ImageFinalizationNumber(value.Position), value.EventId.ToString("D"),
            value.WorkId.ToString("D"), value.ManifestId.ToString("D"),
            value.InspectionId.ToString("D"), value.WorkContentHash, value.ManifestContentHash,
            ImageFinalizationNumber(value.AggregateSequence), value.AttemptId?.ToString("D"),
            value.AttemptNumber?.ToString(CultureInfo.InvariantCulture), value.RuntimeEpoch.ToString("D"),
            value.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture), value.SystemPrincipalId,
            value.Kind.ToString(), value.ReasonCode, value.ContentHash,
            ProductionImageFinalizationStorageCodec.PayloadHash(payload),
            Convert.ToBase64String(payload), ImageFinalizationNumber(value.AuditSequence),
            value.AuditHash!);
    }

    private static void UpsertImageFinalizationWorkState(sqlite3 database,
        ImageFinalizationWorkFacts facts, IReadOnlyList<ProductionImageFinalizationEvent> events,
        StoreDeadline deadline)
    {
        // The projection is exactly the replay of the immutable facts, so the validator can
        // always re-derive it and an edited projection row fails closed.
        var derived = DeriveImageFinalizationWorkState(events);
        var lastEvent = events[^1];
        var effectiveSuccess = derived.Success;
        AuditChainDatabase.Execute(database, @"
            INSERT INTO image_finalization_work(
                WorkId,ManifestId,InspectionId,WorkContentHash,ManifestContentHash,State,CleanupState,
                AttemptCount,NextAttemptNumber,RetryEligible,IntegrityConflict,ActiveAttemptId,
                LastFailureReasonCode,LastFailureCategory,RetryAfterUtc,SuccessFinalRootBindingHash,
                SuccessFinalFileName,SuccessEncodedByteLength,SuccessWidth,SuccessHeight,
                SuccessPixelFormat,SuccessValidBits,SuccessCanonicalHashScheme,
                SuccessCanonicalHashSchemeVersion,SuccessCanonicalPixelHash,SuccessContentHash,
                LastEventPosition,LastEventContentHash)
            VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            ON CONFLICT(WorkId) DO UPDATE SET
                State=excluded.State,CleanupState=excluded.CleanupState,
                AttemptCount=excluded.AttemptCount,NextAttemptNumber=excluded.NextAttemptNumber,
                RetryEligible=excluded.RetryEligible,IntegrityConflict=excluded.IntegrityConflict,
                ActiveAttemptId=excluded.ActiveAttemptId,
                LastFailureReasonCode=excluded.LastFailureReasonCode,
                LastFailureCategory=excluded.LastFailureCategory,RetryAfterUtc=excluded.RetryAfterUtc,
                SuccessFinalRootBindingHash=excluded.SuccessFinalRootBindingHash,
                SuccessFinalFileName=excluded.SuccessFinalFileName,
                SuccessEncodedByteLength=excluded.SuccessEncodedByteLength,
                SuccessWidth=excluded.SuccessWidth,SuccessHeight=excluded.SuccessHeight,
                SuccessPixelFormat=excluded.SuccessPixelFormat,SuccessValidBits=excluded.SuccessValidBits,
                SuccessCanonicalHashScheme=excluded.SuccessCanonicalHashScheme,
                SuccessCanonicalHashSchemeVersion=excluded.SuccessCanonicalHashSchemeVersion,
                SuccessCanonicalPixelHash=excluded.SuccessCanonicalPixelHash,
                SuccessContentHash=excluded.SuccessContentHash,
                LastEventPosition=excluded.LastEventPosition,
                LastEventContentHash=excluded.LastEventContentHash;", deadline,
            facts.WorkId.ToString("D"), facts.ManifestId.ToString("D"),
            facts.InspectionId.ToString("D"), facts.WorkContentHash, facts.ManifestContentHash,
            derived.State.ToString(), derived.CleanupState.ToString(),
            ImageFinalizationNumber(derived.AttemptCount),
            ImageFinalizationNumber(derived.NextAttemptNumber),
            derived.RetryEligible ? "1" : "0", derived.IntegrityConflict ? "1" : "0",
            derived.ActiveAttemptId?.ToString("D"),
            derived.LastFailure?.ReasonCode, derived.LastFailure?.Category.ToString(),
            derived.LastFailure?.RetryAfterUtc?.ToString("O", CultureInfo.InvariantCulture),
            effectiveSuccess?.FinalRootBindingHash, effectiveSuccess?.FinalFileName,
            effectiveSuccess is null ? null :
                ImageFinalizationNumber(effectiveSuccess.EncodedByteLength),
            effectiveSuccess is null ? null : ImageFinalizationNumber(effectiveSuccess.Width),
            effectiveSuccess is null ? null : ImageFinalizationNumber(effectiveSuccess.Height),
            effectiveSuccess is null ? null :
                ImageFinalizationNumber((byte)effectiveSuccess.PixelFormat),
            effectiveSuccess?.ValidBits is { } bits ? ImageFinalizationNumber(bits) : null,
            effectiveSuccess?.CanonicalHashScheme,
            effectiveSuccess is null ? null :
                ImageFinalizationNumber(effectiveSuccess.CanonicalHashSchemeVersion),
            effectiveSuccess?.CanonicalPixelHash, effectiveSuccess?.ContentHash,
            ImageFinalizationNumber(lastEvent.Position), lastEvent.ContentHash);
    }

    /// <summary>
    /// The verified backlog watermark: ThroughAuditSequence is the committed audit sequence at
    /// the moment of the projection and Count/Bytes cover every obligation without Succeeded.
    /// </summary>
    internal static ImageBacklogSnapshot ReadImageBacklogSnapshot(sqlite3 database,
        long throughAuditSequence, StoreDeadline deadline)
    {
        var payloads = AuditChainDatabase.Read(database, @"
            SELECT m.Payload FROM pending_image_manifests m JOIN pending_image_work w
                ON w.ManifestId=m.ManifestId
            WHERE NOT EXISTS(SELECT 1 FROM image_finalization_events e
                WHERE e.WorkId=w.WorkId AND e.Kind='Succeeded') ORDER BY m.ManifestId LIMIT ?;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty,
            ProductionImageEvidenceStoreOptions.MaximumImagesHardLimit.ToString(
                CultureInfo.InvariantCulture));
        long count = 0;
        long bytes = 0;
        DateTimeOffset? oldest = null;
        foreach (var encoded in payloads)
        {
            PendingImageManifest manifest;
            try
            {
                manifest = ProductionInspectionStorageCodec.DecodeImageManifest(
                    Convert.FromBase64String(encoded));
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            { throw new InvalidOperationException("ImageFinalizationPendingManifestInvalid", exception); }
            count = checked(count + 1);
            bytes = checked(bytes + manifest.CanonicalByteLength);
            if (oldest is null || manifest.CreatedAtUtc < oldest) oldest = manifest.CreatedAtUtc;
        }
        return new ImageBacklogSnapshot(throughAuditSequence, count, bytes, oldest);
    }

    /// <summary>The outstanding local finalization events the ledger still owes.</summary>
    internal static long ReadImageFinalizationAuditReserve(sqlite3 database, StoreDeadline deadline)
    {
        if (AuditChainDatabase.Scalar(database, @"SELECT COUNT(*) FROM sqlite_master WHERE type='table'
                AND name IN ('image_finalization_store_config','image_finalization_work');", deadline) != 2)
            return 0;
        return ReadImageFinalizationReserveRows(database, deadline).Events;
    }

    /// <summary>
    /// The verified projection of every obligated image: the persisted work state when one
    /// exists and the derived pending state for an obligation that was never attempted. Only
    /// bounded, immutable facts are read; no caller cursor or path is accepted.
    /// </summary>
    internal static IReadOnlyList<ProductionImageFinalizationWorkState> ReadImageFinalizationObligationStates(
        sqlite3 database, ProductionImageFinalizationStoreOptions options, StoreDeadline deadline)
    {
        var persisted = AuditChainDatabase.Read(database, @"
            SELECT s.WorkId,w.ManifestId,w.InspectionId,w.ContentHash,m.ContentHash,
                s.State,s.CleanupState,s.AttemptCount,s.NextAttemptNumber,s.RetryEligible,
                s.IntegrityConflict,s.LastFailureReasonCode,s.LastFailureCategory,s.RetryAfterUtc,
                s.SuccessFinalRootBindingHash,s.SuccessFinalFileName,s.SuccessEncodedByteLength,
                s.SuccessWidth,s.SuccessHeight,s.SuccessPixelFormat,s.SuccessValidBits,
                s.SuccessCanonicalHashScheme,s.SuccessCanonicalHashSchemeVersion,
                s.SuccessCanonicalPixelHash,s.SuccessContentHash,s.LastEventPosition,
                s.LastEventContentHash,s.ActiveAttemptId
            FROM image_finalization_work s
            JOIN pending_image_work w ON w.WorkId=s.WorkId
            JOIN pending_image_manifests m ON m.ManifestId=w.ManifestId
            ORDER BY s.WorkId LIMIT ?;", deadline, statement => new
        {
            WorkId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 0)),
            ManifestId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 1)),
            InspectionId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 2)),
            WorkHash = SqliteNative.ColumnText(statement, 3) ?? string.Empty,
            ManifestHash = SqliteNative.ColumnText(statement, 4) ?? string.Empty,
            State = SqliteNative.ColumnText(statement, 5),
            Cleanup = SqliteNative.ColumnText(statement, 6),
            AttemptCount = SqliteNative.ColumnInt64(statement, 7),
            NextAttempt = SqliteNative.ColumnInt64(statement, 8),
            RetryEligible = SqliteNative.ColumnInt64(statement, 9),
            Conflict = SqliteNative.ColumnInt64(statement, 10),
            Reason = SqliteNative.ColumnText(statement, 11),
            Category = SqliteNative.ColumnText(statement, 12),
            RetryAfter = SqliteNative.ColumnText(statement, 13),
            SuccessRoot = SqliteNative.ColumnText(statement, 14),
            SuccessFileName = SqliteNative.ColumnText(statement, 15),
            SuccessLength = SqliteNative.ColumnInt64Nullable(statement, 16),
            SuccessWidth = SqliteNative.ColumnInt64Nullable(statement, 17),
            SuccessHeight = SqliteNative.ColumnInt64Nullable(statement, 18),
            SuccessFormat = SqliteNative.ColumnInt64Nullable(statement, 19),
            SuccessValidBits = SqliteNative.ColumnInt64Nullable(statement, 20),
            SuccessScheme = SqliteNative.ColumnText(statement, 21),
            SuccessSchemeVersion = SqliteNative.ColumnInt64Nullable(statement, 22),
            SuccessHash = SqliteNative.ColumnText(statement, 23),
            SuccessContentHash = SqliteNative.ColumnText(statement, 24),
            LastPosition = SqliteNative.ColumnInt64(statement, 25),
            LastHash = SqliteNative.ColumnText(statement, 26) ?? string.Empty,
            Active = SqliteNative.ColumnText(statement, 27)
        }, checked(options.MaximumEvents + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(persisted.Count <= options.MaximumEvents,
            "ImageFinalizationEntryCapacityExceeded");
        var states = new Dictionary<Guid, ProductionImageFinalizationWorkState>();
        foreach (var row in persisted)
        {
            var state = ParseImageFinalizationState(row.State);
            var cleanup = row.Cleanup switch
            {
                "Pending" => ProductionImageCleanupState.Pending,
                "Released" => ProductionImageCleanupState.Released,
                _ => throw new InvalidOperationException("ImageFinalizationWorkStateInvalid")
            };
            ProductionImageSuccessDescriptor? success = null;
            if (row.SuccessFileName is not null)
            {
                if (row.SuccessRoot is null || row.SuccessLength is null or < 1 ||
                    row.SuccessWidth is null or < 1 || row.SuccessHeight is null or < 1 ||
                    row.SuccessFormat is null || row.SuccessScheme is null ||
                    row.SuccessSchemeVersion is null || row.SuccessHash is null ||
                    row.SuccessContentHash is null)
                    throw new InvalidOperationException("ImageFinalizationWorkStateInvalid");
                success = new ProductionImageSuccessDescriptor(row.SuccessRoot, row.SuccessFileName,
                    row.SuccessLength.Value, checked((int)row.SuccessWidth.Value),
                    checked((int)row.SuccessHeight.Value),
                    (VisionPixelFormat)checked((byte)row.SuccessFormat.Value),
                    row.SuccessValidBits is { } bits ? checked((int)bits) : null, row.SuccessScheme,
                    checked((int)row.SuccessSchemeVersion.Value), row.SuccessHash);
                if (success.ContentHash != row.SuccessContentHash.ToUpperInvariant())
                    throw new InvalidOperationException("ImageFinalizationWorkStateInvalid");
            }
            var category = row.Category switch
            {
                "Temporary" => ProductionImageFailureCategory.Temporary,
                "Integrity" => ProductionImageFailureCategory.Integrity,
                null => (ProductionImageFailureCategory?)null,
                _ => throw new InvalidOperationException("ImageFinalizationWorkStateInvalid")
            };
            DateTimeOffset? retryAfter = null;
            if (row.RetryAfter is not null)
            {
                if (!DateTimeOffset.TryParse(row.RetryAfter, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed) ||
                    parsed.Offset != TimeSpan.Zero)
                    throw new InvalidOperationException("ImageFinalizationWorkStateInvalid");
                retryAfter = parsed;
            }
            states[row.WorkId] = new ProductionImageFinalizationWorkState(row.WorkId,
                row.ManifestId, row.InspectionId, row.WorkHash, row.ManifestHash, state, cleanup,
                checked((int)row.AttemptCount), checked((int)row.NextAttempt),
                row.RetryEligible == 1, row.Reason, category, retryAfter, success,
                row.LastPosition, row.LastHash,
                row.Active is null ? null : ParseImageFinalizationGuid(row.Active), row.Conflict == 1);
        }
        // An obligation that was never attempted has no mutable row yet; its derived projection
        // is the empty history.
        var obligations = AuditChainDatabase.Read(database, @"
            SELECT w.WorkId,w.ManifestId,w.InspectionId,w.ContentHash,m.ContentHash
            FROM pending_image_work w JOIN pending_image_manifests m ON m.ManifestId=w.ManifestId
            ORDER BY w.WorkId LIMIT ?;", deadline, statement => new
        {
            WorkId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 0)),
            ManifestId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 1)),
            InspectionId = ParseImageFinalizationGuid(SqliteNative.ColumnText(statement, 2)),
            WorkHash = SqliteNative.ColumnText(statement, 3) ?? string.Empty,
            ManifestHash = SqliteNative.ColumnText(statement, 4) ?? string.Empty
        }, checked(options.MaximumEvents + 1).ToString(CultureInfo.InvariantCulture));
        AuditChainDatabase.Require(obligations.Count <= options.MaximumEvents,
            "ImageFinalizationEntryCapacityExceeded");
        foreach (var obligation in obligations)
        {
            if (states.ContainsKey(obligation.WorkId)) continue;
            states[obligation.WorkId] = new ProductionImageFinalizationWorkState(obligation.WorkId,
                obligation.ManifestId, obligation.InspectionId, obligation.WorkHash,
                obligation.ManifestHash, ProductionImageFinalizationState.Pending,
                ProductionImageCleanupState.Pending, 0, 1, retryEligible: true, lastFailureReasonCode: null,
                lastFailureCategory: null, retryAfterUtc: null, success: null,
                lastEventPosition: 0, lastEventContentHash: null);
        }
        return states.Values.OrderBy(value => value.WorkId).ToArray();
    }

    /// <summary>
    /// Fail-closed finalization capacity proof before a new Core or attempt is admitted: the
    /// future Succeeded and StageReleased facts of every open obligation, the release fact of
    /// every succeeded-but-unreleased work and the terminal fact of every active attempt are
    /// reserved in both the local ledger and the shared central audit chain.
    /// </summary>
    private void EnsureImageFinalizationReserveCapacity(sqlite3 database, long admittedReservations,
        StoreDeadline deadline)
    {
        if (!ImageFinalizationEnabled) return;
        if (admittedReservations < 0)
            throw new InvalidOperationException("ImageFinalizationReservationInvalid");
        var options = _options.ImageFinalization ??
            throw new InvalidOperationException("ImageFinalizationConfigurationRequired");
        var reserve = ReadImageFinalizationReserveRows(database, deadline);
        var openObligations = checked(reserve.OpenObligations + admittedReservations);
        var future = checked(
            ProductionImageFinalizationStoreOptions.ReserveEventsPerOpenObligation * openObligations +
            ProductionImageFinalizationStoreOptions.ReserveEventsPerUnreleasedSuccess *
                reserve.UnreleasedSuccesses +
            ProductionImageFinalizationStoreOptions.ReserveEventsPerActiveAttempt * reserve.ActiveAttempts);
        var usedRows = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM image_finalization_events;", deadline);
        AuditChainDatabase.Require(checked(usedRows + future) <= options.MaximumEvents,
            "ImageFinalizationReserveCapacityExceeded");
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM image_finalization_events;",
            deadline);
        AuditChainDatabase.Require(checked(usedBytes + future *
            ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <=
            options.MaximumTotalBytes, "ImageFinalizationReserveTotalCapacityExceeded");
        var policy = _policy ?? throw new InvalidOperationException("AuditPolicyNotConfigured");
        AuditChainDatabase.EnsureImageFinalizationTransactionCapacity(database, policy, future,
            deadline);
    }

    private static void EnsureImageFinalizationEventCapacity(sqlite3 database,
        ProductionImageFinalizationStoreOptions options, long futureEvents, long payloadLength,
        StoreDeadline deadline)
    {
        var usedRows = AuditChainDatabase.Scalar(database,
            "SELECT COUNT(*) FROM image_finalization_events;", deadline);
        AuditChainDatabase.Require(payloadLength > 0 && payloadLength <= options.MaximumPayloadBytes,
            "ImageFinalizationPayloadCapacityExceeded");
        AuditChainDatabase.Require(checked(usedRows + 1 + futureEvents) <= options.MaximumEvents,
            "ImageFinalizationEntryCapacityExceeded");
        var usedBytes = AuditChainDatabase.Scalar(database,
            "SELECT COALESCE(SUM(length(CAST(Payload AS BLOB))),0) FROM image_finalization_events;",
            deadline);
        AuditChainDatabase.Require(checked(usedBytes +
            ProductionInspectionStoredPayloadBytes(payloadLength) + futureEvents *
            ProductionInspectionStoredPayloadBytes(options.MaximumPayloadBytes)) <=
            options.MaximumTotalBytes, "ImageFinalizationTotalCapacityExceeded");
    }
}
