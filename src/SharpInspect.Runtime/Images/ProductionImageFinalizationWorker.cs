using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Images;

/// <summary>One durable queue reader and one physical file owner; independent of PLC/camera sessions.</summary>
internal sealed class ProductionImageFinalizationWorker
{
    // Fixed execution profile, included in the opt-in production policy fingerprint.
    internal const string ExecutionProfile = "png-finalizer-v1:workers=1:wake=1:work=8388608:file-ms=30000:startup-ms=300000:retire-ms=5000:poll-ms=1000";
    internal static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RetirementTimeout = TimeSpan.FromSeconds(5);
    private readonly SqliteCommandStore _store;
    private readonly ProductionStoreOptions _storeOptions;
    private readonly ProductionImageFinalizationStoreOptions _options;
    private readonly SqliteProductionImageEvidenceQuery _query;
    private readonly ProductionImageFinalizer _files;
    private readonly Guid _epoch;
    private readonly Action<ImageBacklogSnapshot> _publish;
    private readonly Func<string, bool, Task> _fault;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _run;

    internal ProductionImageFinalizationWorker(SqliteCommandStore store, ProductionStoreOptions options,
        Guid epoch, Task storeInitialization, Action<ImageBacklogSnapshot> publish, Func<string, Task> fault,
        Action<ImageFinalizationBoundary>? fileFault = null,
        Func<string, bool, Task>? classifiedFault = null)
    {
        _store = store; _storeOptions = options;
        _options = options.ImageFinalization ?? throw new ArgumentException("ImageFinalizationConfigurationRequired");
        _epoch = epoch; _publish = publish; _fault = classifiedFault ?? ((reason, _) => fault(reason));
        _query = new(options);
        _files = new(_options.ImageEvidence.Stage, _options.FinalRoot.FinalRoot,
            _options.FinalRootBindingHash, new(new(_options.ImageEvidence.Stage.MaximumStageBytes,
                _options.FinalRoot.MaximumFinalFileBytes, 8 * 1024 * 1024),
                _options.FinalRoot.MaximumTotalFinalBytes, _options.FinalRoot.MaximumFinalFiles), fileFault);
        _run = Task.Run(() => RunAsync(storeInitialization));
    }

    internal Task Startup => _startup.Task;
    internal Task Completion => _run;
    internal string? FailureReason { get; private set; }
    internal string? LastTransientFailureReason { get; private set; }
    internal void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    internal async Task<bool> StopAsync()
    {
        _stop.Cancel();
        Wake();
        try { await _run.WaitAsync(RetirementTimeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task RunAsync(Task initialization)
    {
        try
        {
            await initialization.WaitAsync(_stop.Token).ConfigureAwait(false);
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                startup.CancelAfter(TimeSpan.FromMinutes(5));
                while (true)
                {
                    try { await VerifyStartupAsync(startup.Token).ConfigureAwait(false); break; }
                    catch (Exception exception) when (IsTransient(exception) && !startup.IsCancellationRequested)
                    {
                        LastTransientFailureReason = Reason(exception);
                        await Task.Delay(TimeSpan.FromSeconds(1), startup.Token).ConfigureAwait(false);
                    }
                }
            }
            _startup.TrySetResult(true);
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                try { await SweepAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception exception) when (IsTransient(exception) && !_stop.IsCancellationRequested)
                {
                    // A refused transaction is not evidence corruption. Re-read the
                    // durable ledger on the next bounded sweep; never invent success.
                    LastTransientFailureReason = Reason(exception);
                }
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _startup.TrySetCanceled(_stop.Token); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Latch once and stop. A later file replacement or process restart cannot
            // silently turn an integrity conflict into production authority.
            FailureReason = Reason(exception);
            _startup.TrySetException(exception);
            await _fault(FailureReason, IsIntegrity(exception)).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation is semantic. An existing physical operation keeps its slot
            // and handles until it actually exits, even if controlled shutdown returns.
            try { await _files.PhysicalCompletion.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private async Task VerifyStartupAsync(CancellationToken token)
    {
        var stageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long after = 0;
        long? through = null;
        do
        {
            var page = await _query.ReadReplayAsync(after, through, true, token).ConfigureAwait(false);
            through ??= page.ThroughPosition;
            _publish(page.Backlog);
            foreach (var item in page.Items)
            {
                token.ThrowIfCancellationRequested();
                if (item.State.IntegrityConflict)
                    throw new InvalidOperationException("ImageFinalizationIntegrityConflictRecorded");
                var attempts = item.Events.Where(e => e.Kind == ProductionImageFinalizationKind.AttemptStarted).ToArray();
                stageNames.Add(item.Work.Manifest.StageFileName);
                if (attempts.Length != 0) finalNames.Add(item.Work.Manifest.ManifestId.ToString("N") + ".png");
                foreach (var attempt in attempts) finalNames.Add(attempt.Attempt!.TemporaryFileName);
                if (stageNames.Count > _options.ImageEvidence.MaxImages ||
                    finalNames.Count > _options.ImageEvidence.MaxImages * (_options.MaximumAttempts + 1L))
                    throw new InvalidOperationException("ImageFinalizationInventoryCapacityExceeded");
                try
                {
                    await _files.VerifyPersistedFilesAsync(item.Work, item.State.Success,
                        attempts.Length != 0, FileTimeout, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsIntegrity(exception))
                {
                    await _fault(Reason(exception), true).ConfigureAwait(false);
                    await RecordIntegrityFailureAsync(item, Reason(exception), token).ConfigureAwait(false);
                    throw;
                }
            }
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after) throw new InvalidOperationException("ImageFinalizationCursorInvalid");
            after = next;
        } while (true);
        await _files.VerifyKnownInventoryAsync(stageNames, finalNames, FileTimeout, token).ConfigureAwait(false);
    }

    private async Task SweepAsync(CancellationToken token)
    {
        long after = 0;
        long? through = null;
        do
        {
            var page = await _query.ReadReplayAsync(after, through, false, token).ConfigureAwait(false);
            through ??= page.ThroughPosition;
            _publish(page.Backlog);
            foreach (var item in page.Items)
            {
                token.ThrowIfCancellationRequested();
                await ProcessAsync(item, token).ConfigureAwait(false);
            }
            if (page.NextAfterPosition is not { } next) break;
            if (next <= after) throw new InvalidOperationException("ImageFinalizationCursorInvalid");
            after = next;
        } while (true);
    }

    private async Task ProcessAsync(ImageFinalizationReplayItem item, CancellationToken token)
    {
        if (item.State.IntegrityConflict) throw new InvalidOperationException("ImageFinalizationIntegrityConflictRecorded");
        if (item.State.Success is { } success)
        {
            // Never write Failed over Succeeded; cleanup is its own replayable operation.
            try { await ReleaseAsync(item.Work, success, token).ConfigureAwait(false); }
            catch (Exception exception) when (!IsIntegrity(exception) && IsOperational(exception) &&
                exception is not ImageFinalizationPersistenceException)
            { LastTransientFailureReason = Reason(exception); }
            finally { await RetireFileOperationAsync(token).ConfigureAwait(false); }
            return;
        }
        var attempt = item.Events.LastOrDefault(e => e.Kind == ProductionImageFinalizationKind.AttemptStarted)?.Attempt;
        var active = item.State.ActiveAttemptId;
        try
        {
            var finalExists = await _files.VerifyPersistedFilesAsync(item.Work, null, attempt is not null,
                FileTimeout, token).ConfigureAwait(false);
            if (active is { } interrupted)
            {
                // No file operation from an earlier sweep remains active: it always
                // retires below. An active durable attempt therefore represents an exit.
                await FailAsync(item.Work.WorkId, interrupted, "ImageFinalizationProcessInterrupted",
                    ProductionImageFailureCategory.Temporary, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                active = null;
            }
            if (finalExists)
            {
                foreach (var previous in item.Events.Where(e => e.Kind == ProductionImageFinalizationKind.AttemptStarted))
                    await _files.DiscardKnownTemporaryAsync(item.Work, previous.AttemptId!.Value,
                        FileTimeout, token).ConfigureAwait(false);
            }
            else
            {
                if (!item.State.RetryEligible || item.State.AttemptCount >= _options.MaximumAttempts ||
                    item.State.RetryAfterUtc is { } retry && retry > DateTimeOffset.UtcNow) return;
                foreach (var previous in item.Events.Where(e => e.Kind == ProductionImageFinalizationKind.AttemptStarted))
                    await _files.DiscardKnownTemporaryAsync(item.Work, previous.AttemptId!.Value,
                        FileTimeout, token).ConfigureAwait(false);
                var begun = await _store.BeginImageFinalizationAttemptAsync(new(item.Work.WorkId, Guid.NewGuid(),
                    item.State.NextAttemptNumber, _epoch, DateTimeOffset.UtcNow, false, Guard),
                    Deadline(), token).ConfigureAwait(false);
                RequireCommit(begun);
                attempt = begun.Event!.Attempt!;
                active = attempt.AttemptId;
            }
            if (attempt is null) throw new InvalidOperationException("ImageFinalizationAttemptRequired");
            using var claim = await _files.FinalizeAsync(item.Work, attempt.AttemptId, attempt.TemporaryFileName,
                attempt.FinalFileName, finalExists, FileTimeout, token).ConfigureAwait(false);
            // Keep the protected final handle alive until the one SQL writer returns
            // its physical transaction result, including cancellation/deadline failure.
            var committed = await _store.AppendImageFinalizationOutcomeAsync(new ImageFinalizationSuccessRequest(
                item.Work.WorkId, _epoch, DateTimeOffset.UtcNow, claim, Guard), Deadline(), token).ConfigureAwait(false);
            RequireCommit(committed);
            active = null;
            await ReleaseAsync(item.Work, committed.Event!.Success!, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsOperational(exception) && exception is not ImageFinalizationPersistenceException)
        {
            var integrity = IsIntegrity(exception);
            if (integrity) await _fault(Reason(exception), true).ConfigureAwait(false);
            if (active is { } failed)
                await FailAsync(item.Work.WorkId, failed, Reason(exception), integrity
                    ? ProductionImageFailureCategory.Integrity : ProductionImageFailureCategory.Temporary,
                    integrity ? null : DateTimeOffset.UtcNow.Add(_options.MaximumRetryDelay), token).ConfigureAwait(false);
            else if (integrity) await RecordIntegrityFailureAsync(item, Reason(exception), token).ConfigureAwait(false);
            if (integrity) throw;
        }
        finally
        {
            // A semantic timeout never starts a second encoder while the first is stuck.
            await RetireFileOperationAsync(token).ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync(PendingImageFinalizationWork work,
        ProductionImageSuccessDescriptor success, CancellationToken token)
    {
        await _files.ReleaseSucceededStageAsync(work, success, FileTimeout, token).ConfigureAwait(false);
        var released = await _store.RecordImageStageReleasedAsync(new(work.WorkId, _epoch,
            DateTimeOffset.UtcNow, "ImageFinalizationStageReleased", Guard), Deadline(), token).ConfigureAwait(false);
        RequireCommit(released);
    }

    private async Task RecordIntegrityFailureAsync(ImageFinalizationReplayItem item, string reason, CancellationToken token)
    {
        if (item.State.Success is not null || item.State.IntegrityConflict) return;
        var attempt = item.State.ActiveAttemptId;
        if (attempt is null)
        {
            if (item.State.AttemptCount >= _options.MaximumAttempts) return;
            var begun = await _store.BeginImageFinalizationAttemptAsync(new(item.Work.WorkId, Guid.NewGuid(),
                item.State.NextAttemptNumber, _epoch, DateTimeOffset.UtcNow, false, Guard), Deadline(), token).ConfigureAwait(false);
            if (!begun.Committed) return; // The latched integrity alarm remains required regardless.
            _publish(begun.Backlog!);
            attempt = begun.Event!.AttemptId;
        }
        await FailAsync(item.Work.WorkId, attempt!.Value, reason, ProductionImageFailureCategory.Integrity,
            null, token).ConfigureAwait(false);
    }

    private async Task FailAsync(Guid work, Guid attempt, string reason, ProductionImageFailureCategory category,
        DateTimeOffset? retry, CancellationToken token)
    {
        var recorded = DateTimeOffset.UtcNow;
        if (retry is { } instant && instant < recorded) retry = recorded;
        var result = await _store.AppendImageFinalizationOutcomeAsync(new ImageFinalizationFailureRequest(work,
            attempt, _epoch, recorded, reason, category, retry, false, Guard), Deadline(), token).ConfigureAwait(false);
        RequireCommit(result);
    }
    private StoreDeadline Deadline() => new(_storeOptions.CommitTimeout);
    private async Task RetireFileOperationAsync(CancellationToken token)
    {
        try { await _files.PhysicalCompletion.WaitAsync(RetirementTimeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException("ImageFinalizationPhysicalRetirementPending"); }
        catch (Exception exception) when (IsOperational(exception))
        {
            // The semantic caller already classified this completed file failure.
            if (!_files.PhysicalCompletion.IsCompleted) throw;
        }
    }
    private string? Guard() => _stop.IsCancellationRequested ? "ImageFinalizationRuntimeStopped" : null;
    private void RequireCommit(ImageFinalizationWriteResult result)
    {
        if (!result.Committed) throw new ImageFinalizationPersistenceException(result.ReasonCode);
        if (result.Backlog is { } backlog) _publish(backlog);
    }
    private static bool IsOperational(Exception exception) =>
        exception is IOException or InvalidDataException or InvalidOperationException or TimeoutException or UnauthorizedAccessException;
    private sealed class ImageFinalizationPersistenceException : IOException
    { internal ImageFinalizationPersistenceException(string reason) : base(reason) { } }
    private static bool IsTransient(Exception exception) => exception is TimeoutException ||
        exception is ImageFinalizationPersistenceException && exception.Message is
            "TraceStoreWalLimit" or "ImageFinalizationCommitDeadlineExceeded" or "ImageFinalizationUnavailable" or
            "TraceCommitDeadlineExceeded" or "TraceStoreBusy" ||
        exception is IOException and not ImageFinalizationPersistenceException && !IsIntegrity(exception);
    private static bool IsIntegrity(Exception exception) => exception is InvalidDataException or FileNotFoundException or
        DirectoryNotFoundException || exception is InvalidOperationException &&
        !exception.Message.Contains("Capacity", StringComparison.Ordinal) &&
        !exception.Message.Contains("InProgress", StringComparison.Ordinal) &&
        exception.Message is not "ImageFinalizationPhysicalRetirementPending" and
            not "ProductionImageStartupReconciliationRequired";
    internal static string Reason(Exception exception)
    {
        var value = exception.Message;
        return value.Length is > 0 and <= 128 && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.')
            ? value : exception is TimeoutException ? "ImageFinalizationDeadlineExceeded" :
            IsIntegrity(exception) ? "ImageFinalizationIntegrityFault" : "ImageFinalizationIoFailed";
    }
}
