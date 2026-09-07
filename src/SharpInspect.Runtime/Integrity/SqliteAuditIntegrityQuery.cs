using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Integrity;

/// <summary>Read-only bounded verification against a machine key independent of database trust rows.</summary>
public sealed class SqliteAuditIntegrityQuery : IAuditIntegrityQuery
{
    private readonly ProductionStoreOptions _options;
    private readonly Func<AuditIntegrityPolicy, IAuditSigningKey> _openKey;
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private static int _waiting;

    public SqliteAuditIntegrityQuery(ProductionStoreOptions options) : this(options,
        policy => WindowsMachineAuditKey.Open(policy, false, out _)) { }

    internal SqliteAuditIntegrityQuery(ProductionStoreOptions options, Func<AuditIntegrityPolicy, IAuditSigningKey> openKey)
    { _options = options; _openKey = openKey; }

    public ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request, CancellationToken cancellationToken = default) =>
        VerifyAsync(request, false, cancellationToken);

    internal async ValueTask<AuditIntegrityReport> VerifyAsync(AuditVerificationRequest request, bool startup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AfterSequence < 0 || request.MaximumEntries is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var policy = _options.AuditIntegrityPolicy;
        if (policy is null) return Report(null, AuditIntegrityState.NotConfigured, "AuditPolicyNotConfigured");
        policy.Validate();
        if (request.MaximumEntries > policy.MaximumVerificationEntries - policy.CheckpointEveryEntries)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (Interlocked.Increment(ref _waiting) > 32)
        { Interlocked.Decrement(ref _waiting); return Report(policy, AuditIntegrityState.Faulted, "AuditVerificationCapacityExceeded"); }
        var entered = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(_options.QueryTimeout);
        try
        {
            entered = await Slots.WaitAsync(_options.QueryTimeout, lifetime.Token).ConfigureAwait(false);
            if (!entered) throw new TimeoutException("AuditVerificationDeadlineExceeded");
            var deadline = new StoreDeadline(_options.QueryTimeout);
            var result = await Task.Run(() =>
            {
                SqliteNative.EnsureDeadline(deadline, lifetime.Token);
                if (!StoragePathValidator.TryValidate(_options, out var path, out var reason)) throw new InvalidOperationException(reason);
                using var key = _openKey(policy);
                using var connection = SqliteNative.Open(path, readOnly: true);
                var db = connection.Handle!;
                SQLitePCL.raw.sqlite3_limit(db, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 65536);
                SqliteNative.Execute(db, "PRAGMA query_only=ON; BEGIN;", deadline, lifetime.Token);
                AuditChainDatabase.Require(AuditChainDatabase.Scalar(db, "PRAGMA user_version;", deadline) is 2 or 3, "AuditGovernedMigrationRequired");
                var report = AuditChainDatabase.Verify(db, policy, key.KeyId, key.PublicKeyBase64, request, startup, deadline);
                var checkpoint = AuditChainDatabase.LatestCheckpoint(db, deadline)!;
                SqliteNative.Execute(db, "COMMIT;", deadline, lifetime.Token);
                return (Report: report, Checkpoint: checkpoint);
            }, CancellationToken.None).ConfigureAwait(false);
            if (policy.RequireExternalAnchor)
            {
                var anchor = _options.ExternalAuditAnchor ?? throw new InvalidOperationException("AuditRequiredAnchorUnavailable");
                var latest = await AuditAnchorClient.InvokeAsync(anchor, token => anchor.ReadLatestAsync(policy.StationId, token),
                    policy.AnchorTimeout, lifetime.Token).ConfigureAwait(false);
                AuditChainDatabase.Require(latest is not null && AuditChainDatabase.ReceiptMatches(policy, result.Checkpoint, latest),
                    "AuditExternalAnchorMismatch");
            }
            return result.Report;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var reason = FaultReason(ex, "AuditVerificationUnavailable");
            return Report(policy, reason == "AuditAnchorBusy" ? AuditIntegrityState.Verifying : AuditIntegrityState.Faulted, reason);
        }
        finally { if (entered) Slots.Release(); Interlocked.Decrement(ref _waiting); }
    }

    internal static AuditIntegrityReport Report(AuditIntegrityPolicy? policy, AuditIntegrityState state, string reason) =>
        new(state, reason, policy?.StationId, policy?.Version, 0, 0, 0, null, null, DateTimeOffset.UtcNow);

    internal static string FaultReason(Exception exception, string unavailable)
    {
        if (exception is InvalidOperationException && exception.Message.StartsWith("Audit", StringComparison.Ordinal)) return exception.Message;
        var sqliteCode = exception is SqliteNativeException native ? native.SqliteErrorCode & 255 :
            exception is Microsoft.Data.Sqlite.SqliteException managed ? managed.SqliteErrorCode : 0;
        return sqliteCode switch
        {
            SQLitePCL.raw.SQLITE_TOOBIG => "AuditEvidenceOversized",
            SQLitePCL.raw.SQLITE_CORRUPT or SQLitePCL.raw.SQLITE_NOTADB => "AuditEvidenceCorrupt",
            SQLitePCL.raw.SQLITE_ERROR or SQLitePCL.raw.SQLITE_SCHEMA => "AuditSchemaInvalid",
            _ => exception switch
            {
                TimeoutException or OperationCanceledException => "AuditVerificationDeadlineExceeded",
                JsonException or FormatException or ArgumentException or OverflowException => "AuditEvidenceMalformed",
                _ => unavailable
            }
        };
    }
}
