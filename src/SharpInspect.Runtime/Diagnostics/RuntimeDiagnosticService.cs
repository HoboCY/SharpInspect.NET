using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Diagnostics;

internal sealed class RuntimeDiagnosticService : IDiagnosticPipelineHealthQuery, IDiagnosticHistoryQuery, IAsyncDisposable
{
    private readonly ProductionStoreOptions? _storeOptions;
    private readonly LoggingDiagnosticsOptions? _options;
    private readonly LocalAuthorizationService? _authorization;
    private readonly Guid _epoch;
    private readonly Task _initialization;
    private readonly ISafeDiagnosticSink? _forwarded;
    private readonly Action? _beforeInstallationVerificationForTesting;
    private Task? _activation;
    private readonly object _sync = new();
    private DiagnosticPipeline? _pipeline;
    private LocalDiagnosticStore? _safe, _protected;
    private int _queryActive;
    private bool _stopping;
    private string _reason = "DiagnosticPolicyMissing";

    internal RuntimeDiagnosticService(ProductionStoreOptions? options, Guid epoch, Task storeInitialization,
        LocalAuthorizationService? authorization, ISafeDiagnosticSink? forwarded = null,
        Action? beforeInstallationVerificationForTesting = null)
    {
        _storeOptions = options; _options = options?.LoggingDiagnostics; _epoch = epoch; _authorization = authorization;
        _forwarded = forwarded;
        _beforeInstallationVerificationForTesting = beforeInstallationVerificationForTesting;
        _initialization = _options is null ? Task.CompletedTask : Task.Run(async () =>
        {
            try
            {
                _reason = "DiagnosticInitializationPending";
                await storeInitialization.ConfigureAwait(false);
                var trace = await new SqliteTraceStoragePolicyQuery(options!).ReadAsync().ConfigureAwait(false);
                await BeginActivation(trace.Snapshot).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { _reason = "DiagnosticInitializationFailed"; }
        });
    }

    internal DiagnosticPipeline? Pipeline => Volatile.Read(ref _pipeline);
    internal Task Initialization => _initialization;
    internal bool Matches(TraceStoragePolicySnapshot? trace)
    {
        if (Volatile.Read(ref _stopping) || _options is null || !_options.Matches(trace)) return false;
        // Initial installation may publish its approved Trace policy after Runtime startup.
        // Reconcile that exact publication; never replace a failed physical output worker.
        if (Pipeline is null) _ = BeginActivation(trace);
        // Installation was physically verified before publication. Ongoing filesystem IO
        // belongs to the fixed output/query owners, never to the production polling loop.
        return Pipeline is not null;
    }

    private bool VerifyInstallation()
    {
        _beforeInstallationVerificationForTesting?.Invoke();
        return _options!.VerifyInstallation(_storeOptions!.DatabasePath,
            _storeOptions.CalibrationSessions?.EvidenceRoot, _storeOptions.ImageEvidence?.Stage.StageRoot,
            _storeOptions.ImageFinalization?.FinalRoot.FinalRoot, _storeOptions.EvidenceReconciliation?.FinalQuarantine?.Root);
    }

    private Task BeginActivation(TraceStoragePolicySnapshot? trace)
    {
        if (!_options!.Matches(trace)) { _reason = "DiagnosticTracePolicyMismatch"; return Task.CompletedTask; }
        lock (_sync)
        {
            if (_stopping || _pipeline is not null) return Task.CompletedTask;
            return _activation ??= Task.Run(async () =>
            {
                LocalDiagnosticStore? safe = null, protectedStore = null;
                try
                {
                    if (!VerifyInstallation()) { _reason = "DiagnosticInstallationInvalid"; return; }
                    var policy = trace!.Policy;
                    safe = new(_options.Safe, _options.SafeInstallationBinding, _storeOptions!.DatabasePath,
                        policy.MinimumReserveBytes, policy.MinimumReservePercent,
                        beforeInstallationVerificationForTesting: _beforeInstallationVerificationForTesting);
                    protectedStore = new(_options.Protected, _options.ProtectedInstallationBinding, _storeOptions.DatabasePath,
                        policy.MinimumReserveBytes, policy.MinimumReservePercent,
                        beforeInstallationVerificationForTesting: _beforeInstallationVerificationForTesting);
                    // The activation worker must actually own and validate both directories.
                    // A descriptor/boolean alone cannot announce a healthy physical writer.
                    await safe.ReadLinesAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
                    await protectedStore.ReadLinesAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_stopping) return;
                        _safe = safe; _protected = protectedStore;
                        _pipeline = new(_options.Policy, _epoch,
                            item => _safe.WriteAsync(item.Line, CancellationToken.None),
                            item => _protected.WriteAsync(item.Line, CancellationToken.None), _forwarded,
                            () => _safe.DisposeAsync(), () => _protected.DisposeAsync());
                        safe = protectedStore = null; _reason = "DiagnosticPipelineHealthy";
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { _reason = "DiagnosticActivationFailed"; }
                finally
                {
                    if (safe is not null) await safe.DisposeAsync().ConfigureAwait(false);
                    if (protectedStore is not null) await protectedStore.DisposeAsync().ConfigureAwait(false);
                }
            });
        }
    }

    public DiagnosticPipelineHealthSnapshot ReadHealth() => Pipeline?.ReadHealth() ?? new(false, _epoch,
        _options?.Policy.ContentHash, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, null, null, null,
        _options is not null, _reason);

    public ValueTask<DiagnosticHistoryPage> ReadAsync(DiagnosticHistoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options is null || Pipeline is null) return ValueTask.FromResult(Unavailable(_reason));
        if (request.MaximumRecords < 1 || request.MaximumRecords > _options.Policy.MaximumQueryRecords ||
            request.MaximumBytes < 1 || request.MaximumBytes > _options.Policy.MaximumQueryBytes)
            return ValueTask.FromResult(Unavailable("DiagnosticQueryBoundsInvalid"));
        return _authorization is null ? ValueTask.FromResult(Unavailable("AuthorizationUnavailable")) :
            _authorization.ReadDiagnosticHistoryAsync(request, token => ReadOwnedAsync(request, token), cancellationToken);
    }

    private async ValueTask<DiagnosticHistoryPage> ReadOwnedAsync(DiagnosticHistoryRequest request, CancellationToken token)
    {
        if (_stopping || Interlocked.CompareExchange(ref _queryActive, 1, 0) != 0)
            return Unavailable("DiagnosticQueryCapacityExceeded");
        // At most one physical query. The slot is released by physical completion, never by
        // caller timeout; no second task can accumulate behind a hung filesystem call.
        var operation = Task.Run(async () =>
        {
            try
            {
                var store = request.Protected ? _protected! : _safe!;
                var lines = await store.ReadLinesAsync(request.MaximumRecords, request.MaximumBytes, token).ConfigureAwait(false);
                var records = new List<DiagnosticRecord>(); var omitted = 0;
                foreach (var line in lines)
                {
                    var record = DiagnosticJson.Decode(line, _options!.Policy, request.Protected);
                    if (record is null) omitted++; else records.Add(record);
                }
                return new DiagnosticHistoryPage(true, omitted == 0 ? "DiagnosticHistoryAvailable" : "DiagnosticHistoryInvalidRecordsOmitted",
                    records.AsReadOnly(), omitted);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { return Unavailable("DiagnosticQueryUnavailable"); }
            finally { Interlocked.Exchange(ref _queryActive, 0); }
        }, CancellationToken.None);
        try { return await operation.WaitAsync(_storeOptions!.QueryTimeout, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        { return Unavailable("DiagnosticQueryDeadlineExceeded"); }
    }

    internal static DiagnosticHistoryPage Unavailable(string reason) => new(false, reason, Array.Empty<DiagnosticRecord>(), 0);

    public async ValueTask DisposeAsync()
    {
        DiagnosticPipeline? pipeline;
        lock (_sync) { _stopping = true; pipeline = _pipeline; }
        if (pipeline is not null) await pipeline.DisposeAsync().ConfigureAwait(false);
        if (_options is not null) await Task.WhenAny(Task.WhenAll(_initialization, _activation ?? Task.CompletedTask),
            Task.Delay(_options.Policy.FlushTimeout)).ConfigureAwait(false);
    }
}
