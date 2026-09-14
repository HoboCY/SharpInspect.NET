using SharpInspect.Abstractions;
using SharpInspect.Runtime.Images;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Evidence;

// Local evidence reconciliation owns no PLC result or external delivery mutation authority.
// Startup never depends on a receiver response; historical turns never send or republish.
internal sealed partial class EvidenceReconciliationWorker
{
    internal const string ExecutionProfile = "evidence-reconciliation-v1:workers=1:source-page=1:retire-ms=5000";
    private readonly SqliteCommandStore _store;
    private readonly ProductionStoreOptions _storeOptions;
    private readonly EvidenceReconciliationStoreOptions _options;
    private readonly SqliteEvidenceReconciliationQuery _query;
    private readonly SqliteProductionImageEvidenceQuery? _images;
    private readonly ProductionImageFinalizer? _files;
    private readonly Guid _epoch;
    private readonly Func<string, Task> _fault;
    private readonly Func<bool> _canScrub;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _run;
    private readonly List<EvidenceQuarantine> _quarantines = new();

    internal EvidenceReconciliationWorker(SqliteCommandStore store, ProductionStoreOptions options, Guid epoch,
        Task initialization, Task outboxLocalStartup, Func<string, Task> fault, Func<bool>? canScrub = null,
        bool startScrubber = true)
    {
        _store = store; _storeOptions = options; _epoch = epoch; _fault = fault;
        _options = options.EvidenceReconciliation ?? throw new ArgumentException("EvidenceReconciliationConfigurationRequired");
        _canScrub = canScrub ?? (() => true); _query = new(options);
        if (options.ImageFinalization is { } images)
        {
            _images = new(options);
            _files = new(images.ImageEvidence.Stage, images.FinalRoot.FinalRoot, images.FinalRootBindingHash,
                new(new(images.ImageEvidence.Stage.MaximumStageBytes, images.FinalRoot.MaximumFinalFileBytes,
                    8 * 1024 * 1024), images.FinalRoot.MaximumTotalFinalBytes, images.FinalRoot.MaximumFinalFiles));
        }
        _run = Task.Run(() => RunAsync(initialization, outboxLocalStartup, startScrubber));
    }
    internal Task Startup => _startup.Task;
    internal Task Completion => _run;
    internal string? FailureReason { get; private set; }
    internal async Task<bool> StopAsync()
    {
        _stop.Cancel();
        try { await _run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }
    private async Task RunAsync(Task initialization, Task outboxLocalStartup, bool startScrubber)
    {
        try
        {
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                startup.CancelAfter(_options.StartupTimeout);
                await initialization.WaitAsync(startup.Token).ConfigureAwait(false);
                var prior = await _query.ReadStateAsync(startup.Token).ConfigureAwait(false);
                var run = await StartRunAsync(null, 0, startup.Token).ConfigureAwait(false);
                await ReconcileQuarantinesAsync(run, prior, startup.Token).ConfigureAwait(false);
                if (prior.Replay.IntegrityFault)
                    throw new InvalidOperationException("EvidenceReconciliationIntegrityFaultRecorded");
                if (_files is not null)
                {
                    await ReconcileInventoryAsync(run, startup.Token).ConfigureAwait(false);
                    await ReconcileStartupImagesAsync(run, startup.Token).ConfigureAwait(false);
                }
                await outboxLocalStartup.WaitAsync(startup.Token).ConfigureAwait(false);
                if (_storeOptions.Outbox is not null)
                {
                    long after = 0;
                    while (true)
                    {
                        var sources = await _query.ReadSourcesAsync(EvidenceReconciliationSubjectKind.Outbox,
                            after, long.MaxValue, 1, startup.Token, pendingOnly: true).ConfigureAwait(false);
                        if (sources.Count == 0) break;
                        var source = sources[0];
                        await AppendPageAsync(run, source, EvidenceReconciliationEventKind.OutboxVerified,
                            "OutboxLocalLifecycleVerified", 0, null, startup.Token).ConfigureAwait(false);
                        after = source.SourcePosition!.Value;
                    }
                }
                await CompleteRunAsync(run, startup.Token).ConfigureAwait(false);
            }
            _startup.TrySetResult(true);
            while (true)
            {
                await Task.Delay(_options.Scrubber.Interval, _stop.Token).ConfigureAwait(false);
                if (startScrubber) await RunScrubTurnAsync(_stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { _startup.TrySetCanceled(_stop.Token); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FailureReason = Reason(error);
            _startup.TrySetException(error);
            await _fault(FailureReason).ConfigureAwait(false);
        }
        finally
        {
            var physical = _quarantines.Select(x => x.PhysicalCompletion)
                .Concat(_files is null ? Array.Empty<Task>() : new[] { _files.PhysicalCompletion });
            try { await Task.WhenAll(physical).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    private async Task<RunCursor> StartRunAsync(EvidenceReconciliationStream? stream, long through, CancellationToken token)
    {
        var fact = new EvidenceReconciliationPayload(1, Guid.NewGuid(), Guid.NewGuid(), _epoch,
            stream is null ? EvidenceReconciliationPhase.Startup : EvidenceReconciliationPhase.HistoricalScrub,
            EvidenceReconciliationEventKind.RunStarted, stream, DateTimeOffset.UtcNow, "EvidenceRunStarted",
            null, null, through, 0, 0, 0, 0, 0);
        var result = await AppendAsync(new[] { fact }, null, token).ConfigureAwait(false);
        return new(fact, result[0].ContentHash, 0);
    }
    private async Task AppendPageAsync(RunCursor run, EvidenceReconciliationSubject subject,
        EvidenceReconciliationEventKind kind, string reason, long after, Action? protection, CancellationToken token)
    {
        var observation = run.Template with { EventId = Guid.NewGuid(), RuntimeEpoch = _epoch, Kind = kind,
            RecordedAtUtc = DateTimeOffset.UtcNow, ReasonCode = reason, Subject = subject, AfterSourcePosition = run.After };
        var verified = kind is EvidenceReconciliationEventKind.ImageVerified or
            EvidenceReconciliationEventKind.ImageFinalRecovered or EvidenceReconciliationEventKind.OutboxVerified;
        var production = subject.Kind != EvidenceReconciliationSubjectKind.Orphan;
        var checkpoint = run.Template with { EventId = Guid.NewGuid(), RuntimeEpoch = _epoch,
            Kind = EvidenceReconciliationEventKind.PageCompleted, RecordedAtUtc = observation.RecordedAtUtc,
            ReasonCode = "EvidencePageCheckpointed", PreviousCheckpointHash = run.Hash, AfterSourcePosition = after,
            ScannedItems = production ? 1 : 0, VerifiedItems = verified ? 1 : 0,
            DeferredItems = kind == EvidenceReconciliationEventKind.WorkDeferred ? 1 : 0,
            VerifiedBytes = verified ? subject.ByteLength ?? 0 : 0 };
        var rows = await AppendAsync(new[] { observation, checkpoint }, protection, token).ConfigureAwait(false);
        run.Hash = rows[^1].ContentHash; run.After = after;
    }
    private async Task CompleteRunAsync(RunCursor run, CancellationToken token)
    {
        var fact = run.Template with { EventId = Guid.NewGuid(), RuntimeEpoch = _epoch,
            Kind = EvidenceReconciliationEventKind.RunCompleted, RecordedAtUtc = DateTimeOffset.UtcNow,
            ReasonCode = "EvidenceRunCompleted", PreviousCheckpointHash = run.Hash,
            AfterSourcePosition = run.Template.ThroughSourcePosition };
        var rows = await AppendAsync(new[] { fact }, null, token).ConfigureAwait(false);
        run.Hash = rows[^1].ContentHash; run.After = fact.AfterSourcePosition;
    }
    private async Task<IReadOnlyList<EvidenceReconciliationStoredRow>> AppendAsync(
        IReadOnlyList<EvidenceReconciliationPayload> facts, Action? protection, CancellationToken token)
    {
        var deadline = new StoreDeadline(_storeOptions.CommitTimeout);
        for (var attempt = 0; ; attempt++)
        {
            var result = await _store.AppendEvidenceReconciliationAsync(facts, protection, deadline, token).ConfigureAwait(false);
            if (result.Committed) return result.Records!;
            if (result.ReasonCode is not ("EvidenceReconciliationVerifiedTailChanged" or "EvidenceReconciliationVerifiedSnapshotChanged") || attempt >= 2)
            {
                if (result.ReasonCode == "EvidenceReconciliationSourceTailChangedBeforeStart" &&
                    facts.All(x => x.Kind == EvidenceReconciliationEventKind.RunStarted))
                    throw new EvidenceStartCompetitionException();
                throw new EvidencePersistenceException(result.ReasonCode);
            }
            await Task.Yield();
        }
    }
    private async Task RecordFaultAsync(RunCursor run, EvidenceReconciliationSubject subject,
        string reason, long after, CancellationToken token)
    {
        // Admission closes before attempting persistence. Capacity, IO or a changed source
        // cannot turn a failed fault append into authority to admit another trigger.
        await _fault(reason).ConfigureAwait(false);
        if (subject.Kind == EvidenceReconciliationSubjectKind.Orphan)
            await AppendAsync(new[] { run.Template with { EventId = Guid.NewGuid(),
                Kind = EvidenceReconciliationEventKind.IntegrityFault, RecordedAtUtc = DateTimeOffset.UtcNow,
                Subject = subject, ReasonCode = reason, AfterSourcePosition = run.After } }, null, token).ConfigureAwait(false);
        else
            await AppendPageAsync(run, subject, EvidenceReconciliationEventKind.IntegrityFault,
                reason, after, null, token).ConfigureAwait(false);
    }
    private static string Reason(Exception error) => ProductionImageFinalizationWorker.Reason(error);
    private static bool IsYield(Exception error) => error is TimeoutException or EvidenceStartCompetitionException ||
        error is EvidencePersistenceException && error.Message is "EvidenceReconciliationVerifiedTailChanged" or
            "EvidenceReconciliationVerifiedSnapshotChanged" or "AuditVerificationDeadlineExceeded" or
            "EvidenceReconciliationQueryDeadlineExceeded" or "EvidenceReconciliationCommitDeadlineExceeded" or
            "EvidenceReconciliationSourceChanged" or "TraceStoreBusy" or "TraceStoreWalLimit" ||
        error is InvalidOperationException && error.Message is "ProductionImageFinalizationPhysicalOperationPending" or
            "ProductionImageFinalizationInProgress";
    private sealed class EvidenceStartCompetitionException : Exception { }
    private sealed class EvidencePersistenceException : IOException
    { internal EvidencePersistenceException(string reason) : base(reason) { } }
    private sealed class RunCursor
    {
        internal RunCursor(EvidenceReconciliationPayload template, string hash, long after)
        { Template = template; Hash = hash; After = after; }
        internal EvidenceReconciliationPayload Template { get; }
        internal string Hash { get; set; }
        internal long After { get; set; }
    }
}
