using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime;

public sealed partial class StationRuntime
{
    private async Task RunSupportBundleAsync(SupportOperation operation, SqliteCommandStore store)
    {
        var command = (CreateSupportBundleCommand)operation.Command;
        var options = _diagnosticSupportOptions!.LoggingDiagnostics!.SupportBundles!;
        var success = false; var terminalWritten = false; var reason = "SupportBundleInterrupted";
        SupportBundleStore.PreparedBundle? prepared = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        var remaining = options.Policy.ExportTimeout - TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - operation.Started) / (double)Stopwatch.Frequency);
        if (remaining <= TimeSpan.Zero) cancellation.Cancel(); else cancellation.CancelAfter(remaining);
        try
        {
            lock (_sync) { _bundleStatus = _bundleStatus! with { Phase = SupportBundlePhase.Collecting, ReasonCode = "SupportBundleCollecting" }; PublishLocked(_snapshot); }
            // The physical task owns the file store until its actual return. Neither a
            // deadline nor a lock event releases the Runtime production exclusion early.
            var physical = Task.Run(async () =>
            {
                var trace = await new SqliteTraceStoragePolicyQuery(_diagnosticSupportOptions).ReadAsync(cancellationToken: cancellation.Token).ConfigureAwait(false);
                if (!options.Matches(trace.Snapshot)) throw new InvalidOperationException("SupportBundleTracePolicyMismatch");
                var lines = await _diagnostics!.ReadSupportLinesAsync(options.Policy.MaximumSourceRecords,
                    options.Policy.MaximumSourceBytes, cancellation.Token).ConfigureAwait(false);
                var retained = await store.ReadDiagnosticSupportStateAsync(cancellation.Token).ConfigureAwait(false);
                if (!retained.Available) throw new InvalidOperationException("SupportBundleRetentionAuthorityUnavailable");
                var seals = retained.Operations.Select(value => value.Payload).Where(value => value.BundleId is not null &&
                    value.BundleContentHash is not null && value.BundleBytes is not null && value.BundleExpiresAtUtc is not null)
                    .Select(value => new SupportBundleStore.RetentionSeal(value.BundleId!.Value, value.BundleContentHash!,
                        value.BundleBytes!.Value, value.BundleExpiresAtUtc!.Value)).ToArray();
                var files = new SupportBundleStore(options, _diagnosticSupportOptions.DatabasePath,
                    trace.Snapshot!.Policy.MinimumReserveBytes, trace.Snapshot.Policy.MinimumReservePercent, seals);
                try
                {
                    var bundleId = Guid.NewGuid(); // No caller-selected file identity or destination.
                    var stage = files.Stage(bundleId, command.Scope, _diagnosticSupportOptions.LoggingDiagnostics!.Policy,
                        lines, cancellation.Token);
                    return (Files: files, Stage: stage);
                }
                catch { files.Dispose(); throw; }
            }, CancellationToken.None);
            while (!physical.IsCompleted)
            {
                if (operation.StopRequested != 0 || _shutdownRequested || !operation.Authority!.Valid ||
                    !cancellation.IsCancellationRequested && !await _authorization!.CheckDiagnosticAuthorityAsync(operation.Authority,
                        Permission.ExportSupportBundle, cancellation.Token).ConfigureAwait(false)) cancellation.Cancel();
                if (cancellation.IsCancellationRequested)
                {
                    lock (_sync) { _bundleStatus = _bundleStatus! with { Phase = SupportBundlePhase.Interrupted,
                        ReasonCode = "SupportBundlePhysicalRetirementPending" }; PublishLocked(_snapshot); }
                }
                await Task.WhenAny(physical, Task.Delay(options.Policy.AuthorizationCheckInterval)).ConfigureAwait(false);
            }
            var result = await physical.ConfigureAwait(false);
            prepared = result.Stage;
            using var filesOwner = result.Files;
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!await _authorization!.CheckDiagnosticAuthorityAsync(operation.Authority!, Permission.ExportSupportBundle,
                    cancellation.Token).ConfigureAwait(false)) throw new InvalidOperationException("SupportBundleAuthorityLost");
                var projection = prepared.Projection;
                var seal = await store.AppendDiagnosticOperationSealedAsync(new(command.OperationId, DateTimeOffset.UtcNow,
                    "SupportBundleStaged", prepared.Id, projection.ContentHash, projection.Bytes.Length, prepared.Expires),
                    new StoreDeadline(store.CommitTimeout), cancellation.Token).ConfigureAwait(false);
                if (!seal.Committed) throw new InvalidOperationException("SupportBundleSealAuditUnavailable");
                lock (_sync) { _bundleStatus = new(true, _snapshot.RuntimeEpoch, prepared.Id, SupportBundlePhase.Staged,
                    "SupportBundleStaged", projection.ContentHash, projection.Bytes.Length, prepared.Expires); PublishLocked(_snapshot); }
                if (!await _authorization.CheckDiagnosticAuthorityAsync(operation.Authority!, Permission.ExportSupportBundle,
                    cancellation.Token).ConfigureAwait(false)) throw new InvalidOperationException("SupportBundleAuthorityLost");
                cancellation.Token.ThrowIfCancellationRequested();
                var completed = await store.AppendDiagnosticOperationTerminalAsync(new(command.OperationId, DiagnosticOperationPhase.Completed,
                    DateTimeOffset.UtcNow, "SupportBundleCompleted", prepared.Id, projection.ContentHash, projection.Bytes.Length, prepared.Expires),
                    new StoreDeadline(store.CommitTimeout), cancellation.Token).ConfigureAwait(false);
                if (!completed.Committed) throw new InvalidOperationException("SupportBundleCompletionAuditUnavailable");
                terminalWritten = true; success = true;
                lock (_sync)
                {
                    if (!operation.Authority!.Valid || cancellation.IsCancellationRequested || _shutdownRequested ||
                        operation.StopRequested != 0 || DateTimeOffset.UtcNow >= prepared.Expires)
                        reason = "SupportBundleCompletedDeliveryRevoked";
                    else
                    {
                        _publishedBundle = new(prepared.Id, projection.Bytes, projection.ContentHash, prepared.Expires, operation.Authority);
                        reason = "SupportBundleCompleted";
                    }
                }
            }
            finally { prepared.Dispose(); prepared = null; }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { reason = cancellation.IsCancellationRequested ? "SupportBundleCancelledOrExpired" : "SupportBundleFailed"; }
        finally
        {
            prepared?.Dispose();
            if (!terminalWritten)
            {
                operation.Authority?.Revoke();
                try
                {
                    var terminal = await store.AppendDiagnosticOperationTerminalAsync(new(command.OperationId, DiagnosticOperationPhase.Interrupted,
                        DateTimeOffset.UtcNow, reason, null, null, null, null), new StoreDeadline(store.CommitTimeout)).ConfigureAwait(false);
                    if (!terminal.Committed) { reason = "SupportBundleTerminalAuditUnavailable"; MarkAuditFault(reason); }
                }
                catch (Exception error) when (error is not OutOfMemoryException) { reason = "SupportBundleTerminalAuditUnavailable"; MarkAuditFault(reason); }
            }
            FinishDiagnosticSupport(operation, reason, success);
        }
    }

    public async ValueTask<SupportBundleReadResult> ReadAsync(SupportBundleReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SupportBundleReadResult Unavailable() => new(false, "SupportBundleReadUnavailable", null, ReadOnlyMemory<byte>.Empty);
        PublishedBundle? bundle; TaskCompletionSource<bool> retired;
        lock (_sync)
        {
            bundle = _publishedBundle;
            if (bundle is null || request.BundleId != bundle.Id || request.Invocation is null ||
            request.Invocation.SessionId != bundle.Authority.SessionId || request.Invocation.PrincipalId != bundle.Authority.PrincipalId.ToString("D") ||
                DateTimeOffset.UtcNow >= bundle.Expires || DiagnosticSupportStartRejectionLocked() is not null) return Unavailable();
            retired = new(TaskCreationOptions.RunContinuationsAsynchronously); _diagnosticDeliveryRetired = retired;
            PublishLocked(_snapshot);
        }
        try
        {
            if (!await _authorization!.CheckDiagnosticAuthorityAsync(bundle.Authority, Permission.ExportSupportBundle,
                cancellationToken).ConfigureAwait(false)) return Unavailable();
            BeforeSupportBundleDeliveryForTesting?.Invoke();
            // The bounded copy is physical delivery work, never performed while holding _sync.
            var bytes = (byte[])bundle.Bytes.Clone();
            lock (_sync)
            {
                if (_disposed || _shutdownRequested || !ReferenceEquals(bundle, _publishedBundle) || !bundle.Authority.Valid ||
                    DateTimeOffset.UtcNow >= bundle.Expires || cancellationToken.IsCancellationRequested) return Unavailable();
                return new(true, "SupportBundleAvailable", bundle.Hash, bytes);
            }
        }
        finally
        {
            lock (_sync)
            {
                _diagnosticDeliveryRetired = null; retired.TrySetResult(true);
                if (!_disposed) PublishLocked(_snapshot);
            }
        }
    }
}
