using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

internal sealed partial class SqliteCommandStore
{
    private readonly ProductionStoreOptions _options;
    private readonly AuditIntegrityPolicy? _policy;
    private readonly CancellationTokenSource _integrityLifetime = new();
    private readonly SemaphoreSlim _integrityWake = new(0, 1);
    private IAuditSigningKey? _signingKey;
    private AuditIntegrityReport _integrity;
    private Task? _integrityMonitor;
    private long _lastCommittedAuditSequence;
    private long _backgroundAfter;
    private long _lastHistoricalVerification;
    private bool _integrityFaultLatched;
    private readonly object _integrityGate = new();

    private void PreflightAuditVersion()
    {
        if (_policy is null || !File.Exists(_databasePath) || new FileInfo(_databasePath!).Length == 0) return;
        using var read = SqliteNative.Open(_databasePath!, readOnly: true);
        var version = AuditChainDatabase.Scalar(read.Handle!, "PRAGMA user_version;", new StoreDeadline(CommitTimeout));
        if (_options.CameraNetwork is null && version == CameraNetworkStoreOptions.SchemaVersion)
            throw new InvalidOperationException("CameraNetworkConfigurationRequired");
        if (_options.CameraRecovery is null && version == CameraRecoveryStoreOptions.SchemaVersion)
            throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
        if (_options.RecipeDrafts is null && version == RecipeDraftStoreOptions.SchemaVersion)
            throw new InvalidOperationException("RecipeDraftConfigurationRequired");
        if (_options.RecipeDrafts is null && _options.AlgorithmResultArchive is null &&
            version == AlgorithmResultArchiveOptions.SchemaVersion)
            throw new InvalidOperationException("AlgorithmResultArchiveConfigurationRequired");
        if (version > 0 && version < SchemaVersion)
            throw new InvalidOperationException(MigrationReason((int)version));
        if (version > SchemaVersion) throw new InvalidOperationException("StoreSchemaTooNew");
    }

    private void SetIntegrityFault(string reason, bool latch = false)
    {
        if (_policy is not null)
            PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Faulted, reason), latch);
    }

    private void PublishIntegrity(AuditIntegrityReport report, bool latch = false)
    {
        lock (_integrityGate)
        {
            if (_integrityFaultLatched) return;
            if (report.State == AuditIntegrityState.Verified &&
                report.ThroughSequence < Interlocked.Read(ref _lastCommittedAuditSequence))
                report = SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying, "AuditConcurrentAppendRecheckPending");
            _integrityFaultLatched = latch;
            Volatile.Write(ref _integrity, report);
        }
    }

    private void WakeIntegrityMonitor()
    {
        if (_policy is null) return;
        try { _integrityWake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task MonitorIntegrityAsync()
    {
        var token = _integrityLifetime.Token;
        try
        {
            await Initialization.ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await RefreshIntegrityAsync().ConfigureAwait(false);
                await _integrityWake.WaitAsync(_policy!.VerificationInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetIntegrityFault("AuditBackgroundVerifierUnavailable"); }
    }

    private async Task RefreshIntegrityAsync()
    {
        if (Volatile.Read(ref _integrityFaultLatched)) return;
        var token = _integrityLifetime.Token;
        try
        {
            if (_policy!.RequireExternalAnchor) await DeliverLatestCheckpointAsync(token).ConfigureAwait(false);
            var query = new SqliteAuditIntegrityQuery(_options);
            var report = await query.VerifyAsync(new AuditVerificationRequest(), true, token).ConfigureAwait(false);
            var now = Environment.TickCount64;
            if (report.State == AuditIntegrityState.Verified && (_lastHistoricalVerification == 0 ||
                now - _lastHistoricalVerification >= _policy.VerificationInterval.TotalMilliseconds))
            {
                _lastHistoricalVerification = now;
                if (_backgroundAfter >= report.ThroughSequence) _backgroundAfter = 0;
                var older = await query.VerifyAsync(new AuditVerificationRequest(_backgroundAfter,
                    _policy.BackgroundVerificationEntries), token).ConfigureAwait(false);
                if (older.State != AuditIntegrityState.Verified) report = older;
                else _backgroundAfter = older.VerifiedThroughSequence;
            }
            PublishIntegrity(report, report.State == AuditIntegrityState.Faulted && IsStructuralFault(report.ReasonCode));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = SqliteAuditIntegrityQuery.FaultReason(ex, "AuditAnchorDeliveryUnavailable");
            SetIntegrityFault(reason, IsStructuralFault(reason));
        }
    }

    private static bool IsStructuralFault(string reason) => reason is not
        ("AuditRequiredAnchorPending" or "AuditRequiredAnchorUnavailable" or "AuditExternalAnchorMismatch" or
         "AuditVerificationDeadlineExceeded" or "AuditVerificationCapacityExceeded" or "AuditVerificationUnavailable" or "AuditAnchorBusy" or
         "AuditAnchorDeliveryUnavailable" or "AuditReceiptCommitFailed" or "AuditReceiptStoreUnavailable" or "AuditReceiptCommitDeadlineExceeded");

    private async Task DeliverLatestCheckpointAsync(CancellationToken cancellationToken)
    {
        var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
        var checkpoint = await Task.Run(() =>
        {
            using var read = SqliteNative.Open(_databasePath!, readOnly: true);
            return AuditChainDatabase.NextCheckpointForDelivery(read.Handle!, new StoreDeadline(_options.QueryTimeout));
        }, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) return;
        AuditChainDatabase.VerifyCheckpoint(_policy!, checkpoint, _signingKey!.KeyId, _signingKey.PublicKeyBase64);
        var receipt = await AuditAnchorClient.InvokeAsync(anchor, token => anchor.DeliverAsync(checkpoint,
            _policy!.StationId + "/" + _policy.ExternalAnchorRouteId + "/" + checkpoint.CheckpointId.ToString("D"), token),
            _policy!.AnchorTimeout, cancellationToken).ConfigureAwait(false);
        AuditChainDatabase.Require(AuditChainDatabase.ReceiptMatches(_policy, checkpoint, receipt), "AuditAnchorReceiptMismatch");
        var deadline = new StoreDeadline(CommitTimeout);
        if (_queue is null || _queueSlots is null || _worker.IsCompleted) throw new InvalidOperationException("AuditReceiptStoreUnavailable");
        if (!await _queueSlots.WaitAsync(deadline.Remaining, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("AuditReceiptCommitDeadlineExceeded");
        var request = new WriteRequest(null, deadline, receipt);
        if (!_queue.Writer.TryWrite(request))
        { _queueSlots.Release(); throw new InvalidOperationException("AuditReceiptStoreUnavailable"); }
        var result = await request.Completion.Task.ConfigureAwait(false);
        AuditChainDatabase.Require(result.Committed, "AuditReceiptCommitFailed");
    }

    private StoreWriteResult AppendReceiptCore(SqliteConnection connection, AuditAnchorReceipt receipt, StoreDeadline deadline)
    {
        var db = connection.Handle!;
        var committed = false;
        SqliteNative.Execute(db, "BEGIN IMMEDIATE;", deadline);
        try
        {
            AuditChainDatabase.StoreReceipt(db, _policy!, receipt, deadline);
            SqliteNative.Execute(db, "COMMIT;", deadline);
            committed = true;
            return new StoreWriteResult(true, "AuditAnchorReceiptPersisted");
        }
        finally { if (!committed) Rollback(db); }
    }
}
