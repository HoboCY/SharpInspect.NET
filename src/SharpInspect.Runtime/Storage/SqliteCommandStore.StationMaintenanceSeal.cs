using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SQLitePCL;

namespace SharpInspect.Runtime.Storage;

internal sealed record StationMaintenanceSealResult(bool Prepared, string ReasonCode, byte[]? HandoffBytes);

internal sealed class StationMaintenanceSealWork
{
    internal StationMaintenanceSealWork(StationMaintenanceAuthority authority)
    {
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    internal StationMaintenanceAuthority Authority { get; }
    internal StationMaintenanceSealResult? Result { get; set; }
    internal TaskCompletionSource<StationMaintenanceSealResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed partial class SqliteCommandStore
{
    private readonly object _stationMaintenanceSealSync = new();
    private StationMaintenanceSealWork? _stationMaintenanceSeal;
    private int _sealed;

    /// <summary>The physical writer task; completed only after every owned resource is closed.</summary>
    internal Task PhysicalCompletion => _worker;

    /// <summary>
    /// Atomically closes channel write admission, drains every already queued write, writes the
    /// final signed Prepared fact and the bounded signed handoff, closes SQLite/.lock/signing
    /// key/integrity monitor and awaits the physical writer before returning. The ordinary
    /// disposal path is deliberately not used: its storage runtime-retirement callback can
    /// deadlock against the caller that owns this seal.
    /// </summary>
    internal async ValueTask<StationMaintenanceSealResult> SealStationMaintenanceAsync(
        StationMaintenanceAuthority authority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (_options.StationMaintenance is null)
            return new StationMaintenanceSealResult(false, "StationMaintenanceConfigurationRequired", null);
        if (!authority.Valid)
            return new StationMaintenanceSealResult(false, "StationMaintenanceAuthorityRevoked", null);
        var initialized = await Initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!initialized.Committed)
            return new StationMaintenanceSealResult(false, initialized.ReasonCode, null);

        StationMaintenanceSealWork work;
        lock (_stationMaintenanceSealSync)
        {
            if (_stationMaintenanceSeal is { } existing) work = existing;
            else
            {
                work = new StationMaintenanceSealWork(authority);
                _stationMaintenanceSeal = work;
            }
        }
        // One caller owns the single admitted seal; every other caller observes its result.
        if (Interlocked.CompareExchange(ref _sealed, 1, 0) == 0)
        {
            try
            {
                if (_queue is null || _queueSlots is null || Volatile.Read(ref _disposed) != 0)
                    throw new InvalidOperationException("StationMaintenanceStoreUnavailable");
                // The single seal request is published first and the writer is completed
                // immediately afterwards, so every later writer fails without a fake completion
                // and the worker drains the whole channel before it executes the final work.
                if (!_queue.Writer.TryWrite(new WriteRequest(null, new StoreDeadline(CommitTimeout),
                        StationMaintenanceSeal: work)))
                    throw new InvalidOperationException("StationMaintenanceStoreUnavailable");
                _queue.Writer.TryComplete();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                work.Result = new StationMaintenanceSealResult(false, error is InvalidOperationException
                    ? error.Message : "StationMaintenanceStoreUnavailable", null);
                work.Completion.TrySetResult(work.Result);
            }
        }
        var result = await work.Completion.Task.ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The final serialized writer work: recheck the live authority, append the exactly bound
    /// Prepared fact, sign the bounded handoff with the existing station audit key and commit.
    /// Anything before the commit leaves the admitted fact open; the authority is consumed
    /// either way because the store channel is already closed.
    /// </summary>
    private async Task ExecuteStationMaintenanceSealAsync(sqlite3 database, StationMaintenanceSealWork work)
    {
        var option = _options.StationMaintenance!;
        var authority = work.Authority;
        var deadline = new StoreDeadline(option.Policy.RetirementTimeout);
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;
            RequireConfiguredStationMaintenance(database, _options, deadline);
            AuditChainDatabase.RequireFullStationMaintenanceVerification(database,
                VerifyForProtocolLedger(database, deadline), deadline, _options);
            var configuration = ReadStationMaintenanceConfiguration(database, deadline);
            var rows = ReadStationMaintenanceRows(database, configuration, deadline);
            RequireStationMaintenanceAuthorityCoverage(database, rows, deadline);
            var history = rows.Where(row => row.Payload.OperationId == authority.OperationId)
                .OrderBy(row => row.Payload.AggregateSequence).ToArray();
            StationMaintenanceStorageCodec.Require(history.Length > 0 &&
                history[^1].Payload.Phase == StationMaintenanceFactPhase.Admitted &&
                authority.OperationId != Guid.Empty, "OperationNotOpen");
            var admitted = history[^1];
            var admittedPayload = admitted.Payload;
            StationMaintenanceStorageCodec.Require(
                admittedPayload.ActorPrincipalId == authority.PrincipalId &&
                admittedPayload.SessionId == authority.SessionId &&
                admittedPayload.RuntimeEpoch == authority.RuntimeEpoch &&
                admittedPayload.ProcessId == authority.ProcessId &&
                admittedPayload.ProcessStartedUtc == authority.ProcessStartedUtc,
                "AuthorityBindingMismatch");
            // The live identity and session-lease recheck happens inside the serialized seal,
            // after the drain and before the Prepared commit, so a revocation that wins the
            // race refuses the handoff instead of activating a stale grant.
            var liveCheck = authority.LiveCheck;
            StationMaintenanceStorageCodec.Require(liveCheck is not null &&
                await liveCheck(CancellationToken.None).ConfigureAwait(false) && authority.Valid,
                "AuthorityRevoked");
            StationMaintenanceStorageCodec.Require(authority.TryBeginCommit(), "AuthorityRevoked");
            var observed = DateTimeOffset.UtcNow;
            var payload = BuildStationMaintenanceTerminalPayload(admitted,
                StationMaintenanceFactPhase.Prepared, StationMaintenancePreparedReason, observed);
            var historyWithCandidate = rows.Append(new StationMaintenanceStoredRow(rows.Count + 1, payload,
                Array.Empty<byte>(), new string('0', 64), 0, new string('0', 64))).ToArray();
            ValidateStationMaintenanceHistory(configuration, historyWithCandidate);
            AuditChainDatabase.EnsureStationMaintenanceTransactionCapacity(database, _policy!, 1, 0, deadline);
            var row = AppendStationMaintenanceInTransaction(database, configuration, payload, 0, deadline);
            var handoff = new StationMaintenancePreparedHandoff
            {
                OperationId = payload.OperationId,
                EventId = payload.EventId,
                ConfigurationHash = configuration.BindingHash,
                OptionsHash = configuration.OptionsHash,
                PolicyHash = configuration.PolicyHash,
                InstallationBindingHash = configuration.InstallationBindingHash,
                RuntimeApplicationHash = configuration.RuntimeApplicationHash,
                StationId = _policy!.StationId,
                KeyId = _signingKey!.KeyId,
                PublicKeyBase64 = _signingKey.PublicKeyBase64,
                PreparedPosition = row.Position,
                PreparedContentHash = row.ContentHash,
                PreparedAuditSequence = row.AuditSequence,
                PreparedAuditHash = row.AuditHash,
                AdmittedAuditSequence = admitted.AuditSequence,
                AdmittedAuditHash = admitted.AuditHash,
                AdmittedContentHash = admitted.ContentHash,
                CommandEventId = payload.Command.EventId,
                CommandSequence = payload.Command.Sequence,
                CommandHash = payload.Command.Hash,
                AuthorizationEventId = payload.Authorization.EventId,
                AuthorizationSequence = payload.Authorization.Sequence,
                AuthorizationHash = payload.Authorization.Hash,
                RuntimeEpoch = payload.RuntimeEpoch,
                ProcessId = payload.ProcessId,
                ProcessStartedUtc = payload.ProcessStartedUtc.ToString("O", CultureInfo.InvariantCulture),
                Reason = payload.Reason.ToString(),
                ExpiresAtUtc = payload.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture),
                PreparedAtUtc = payload.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            };
            var artifact = StationMaintenanceStorageCodec.CreateHandoffArtifact(handoff, _signingKey);
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            authority.CompleteCommit();
            work.Result = new StationMaintenanceSealResult(true, "StationMaintenancePrepared", artifact);
        }
        catch (TimeoutException)
        { work.Result = new StationMaintenanceSealResult(false, "StationMaintenanceSealDeadlineExceeded", null); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { work.Result = new StationMaintenanceSealResult(false, SealFailureReason(error), null); }
        finally
        {
            authority.Revoke();
            if (started && !committed) Rollback(database);
        }
    }

    private static string SealFailureReason(Exception error) => error switch
    {
        InvalidOperationException message when message.Message.StartsWith("StationMaintenance", StringComparison.Ordinal) =>
            message.Message,
        SqliteNativeException native => native.ReasonCode,
        _ => "StationMaintenanceSealFailed"
    };
}
