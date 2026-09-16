using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime : IManagedFaultBoundary
{
    private RuntimeDiagnosticService? _diagnostics;
    private int _fatalBoundaryEntered;
    internal IDiagnosticPipelineHealthQuery DiagnosticHealthQuery => _diagnostics!;
    internal IDiagnosticHistoryQuery DiagnosticHistoryQuery => _diagnostics!;

    private void ConfigureDiagnostics(ProductionStoreOptions? options)
    {
        _diagnosticSupportOptions = options;
        _diagnostics = new(options, _snapshot.RuntimeEpoch, _storeInitialization, _authorization);
    }

    private AlarmObservation? DiagnosticObservationLocked(AlarmPolicy policy)
    {
        const string code = "DiagnosticPipelineUnhealthy", source = "Runtime.Diagnostics";
        if (_diagnostics is null || !policy.TryGetRule(code, out var rule) || rule?.Source != source) return null;
        var health = _diagnostics.ReadHealth();
        var healthy = health.Configured && !health.Unhealthy;
        if (_alarmObservations.TryGetValue(code, out var prior) && prior.Healthy == healthy && IsFresh(prior, policy)) return null;
        _registeredAlarmSources.Add(source);
        var sequence = NextAlarmObservationSequenceLocked(code);
        if (healthy && _snapshot.AlarmState?.Instances.Any(instance => instance.Code == code && instance.Lifecycle != AlarmLifecycle.Cleared) != true)
        {
            _alarmObservations[code] = new(_snapshot.RuntimeEpoch, sequence, System.Diagnostics.Stopwatch.GetTimestamp(), true);
            return null;
        }
        return new(_snapshot.RuntimeEpoch, sequence, code, source, healthy, DateTimeOffset.UtcNow);
    }

    public async ValueTask HandleAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (Interlocked.Exchange(ref _fatalBoundaryEntered, 1) != 0) return;
        // Fix shutdown/Fault Abort before best-effort diagnostics. UI handling is never permission
        // to resume. The existing owner settles accepted work under its existing retirement policy.
        var shutdown = DisposeAsync().AsTask();
        try
        {
            _diagnostics?.Pipeline?.ObserveException(exception, "ManagedHost");
            if (_diagnostics?.Pipeline is { } pipeline) await pipeline.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { await shutdown.ConfigureAwait(false); }
    }
}
