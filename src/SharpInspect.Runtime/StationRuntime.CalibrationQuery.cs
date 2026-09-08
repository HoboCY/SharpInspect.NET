using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    public async ValueTask<CalibrationSessionQueryResult> QueryCalibrationSessionAsync(Guid sessionId,
        CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return new(false, "CalibrationSessionIdRequired");
        if (Interlocked.Increment(ref _calibrationQueries) > 16)
        {
            Interlocked.Decrement(ref _calibrationQueries);
            return new(false, "CalibrationQueryCapacityExceeded");
        }
        try
        {
            if (_authorization is null || _audit is not SqliteCommandStore { CalibrationEnabled: true } store ||
                _calibrationImages is null) return new(false, "CalibrationSessionQueryUnavailable");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_calibrationOptions?.OperationTimeout ?? TimeSpan.FromSeconds(5));
            var reason = await _authorization.CheckCalibrationQueryAuthorizationAsync(invocation, deadline.Token).ConfigureAwait(false);
            if (reason is not null) return new(false, reason);
            var query = await store.ReadCalibrationSessionAsync(sessionId, deadline.Token).ConfigureAwait(false);
            if (!query.Available || query.Evidence is not { } evidence) return new(false, query.ReasonCode);
            // A valid manifest alone does not prove the retained source bytes are still intact.
            foreach (var frame in evidence.Frames)
                _ = await _calibrationImages.ReadAsync(frame, deadline.Token).ConfigureAwait(false);
            reason = await _authorization.CheckCalibrationQueryAuthorizationAsync(invocation, deadline.Token).ConfigureAwait(false);
            return reason is null ? new(true, "CalibrationSessionEvidenceAvailable", evidence) : new(false, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, "CalibrationSessionEvidenceUnavailable"); }
        finally { Interlocked.Decrement(ref _calibrationQueries); }
    }

    public async ValueTask<CalibrationFrameQueryResult> ReadCalibrationFrameAsync(Guid sessionId, Guid frameId,
        string expectedSourceHash, CommandInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty || frameId == Guid.Empty || expectedSourceHash is not { Length: 64 })
            return new(false, "CalibrationFrameIdentityInvalid");
        if (Interlocked.Increment(ref _calibrationQueries) > 16)
        {
            Interlocked.Decrement(ref _calibrationQueries);
            return new(false, "CalibrationQueryCapacityExceeded");
        }
        try
        {
            if (_authorization is null || _audit is not SqliteCommandStore { CalibrationEnabled: true } store ||
                _calibrationImages is null) return new(false, "CalibrationFrameQueryUnavailable");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_calibrationOptions?.OperationTimeout ?? TimeSpan.FromSeconds(5));
            var reason = await _authorization.CheckCalibrationQueryAuthorizationAsync(invocation, deadline.Token).ConfigureAwait(false);
            if (reason is not null) return new(false, reason);
            var query = await store.ReadCalibrationSessionAsync(sessionId, deadline.Token).ConfigureAwait(false);
            if (!query.Available || query.Evidence is null) return new(false, query.ReasonCode);
            var frame = query.Evidence.Frames.SingleOrDefault(item => item.FrameId == frameId);
            if (frame is null || frame.SourceHash != expectedSourceHash) return new(false, "CalibrationFrameManifestMismatch");
            var image = await _calibrationImages.ReadAsync(frame, deadline.Token).ConfigureAwait(false);
            reason = await _authorization.CheckCalibrationQueryAuthorizationAsync(invocation, deadline.Token).ConfigureAwait(false);
            return reason is null ? new(true, "CalibrationFrameAvailable", image) : new(false, reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, "CalibrationFrameEvidenceUnavailable"); }
        finally { Interlocked.Decrement(ref _calibrationQueries); }
    }
}
