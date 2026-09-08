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
                SqliteNative.ConfigureSqliteLimit(db, _options);
                SqliteNative.Execute(db, "PRAGMA query_only=ON; BEGIN;", deadline, lifetime.Token);
                var schema = AuditChainDatabase.Scalar(db, "PRAGMA user_version;", deadline);
                SqliteNative.ConfigureSqliteLimit(db, _options, schema);
                if (_options.LocalIdentity is not null && schema < 7)
                {
                    var migrationReason = schema switch
                    {
                        6 => "GovernedAlarmMigrationRequired",
                        5 => "IdentityRecoveryGovernedMigrationRequired",
                        4 => "IdentityAuthorizationGovernedMigrationRequired",
                        3 => "IdentityAuthenticationGovernedMigrationRequired",
                        _ => "AuditGovernedMigrationRequired"
                    };
                    throw new InvalidOperationException(migrationReason);
                }
                if (_options.AlgorithmResultArchive is null && schema == AlgorithmResultArchiveOptions.SchemaVersion)
                    throw new InvalidOperationException("AlgorithmResultArchiveConfigurationRequired");
                if (_options.AlgorithmResultArchive is not null && schema < AlgorithmResultArchiveOptions.SchemaVersion)
                    throw new InvalidOperationException("AlgorithmResultArchiveGovernedMigrationRequired");
                if (_options.RecipeDrafts is not null && schema < RecipeDraftStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("RecipeDraftGovernedMigrationRequired");
                if (_options.RecipeDrafts is null && schema == RecipeDraftStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("RecipeDraftConfigurationRequired");
                if (_options.CameraSetup is not null && schema < CameraSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraSetupGovernedMigrationRequired");
                if (_options.CameraSetup is null && schema == CameraSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraSetupConfigurationRequired");
                if (_options.CameraRecovery is not null && schema < CameraRecoveryStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraRecoveryGovernedMigrationRequired");
                if (_options.CameraRecovery is null && schema == CameraRecoveryStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraRecoveryConfigurationRequired");
                if (_options.CameraNetwork is not null && schema < CameraNetworkStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraNetworkGovernedMigrationRequired");
                if (_options.CameraNetwork is null && schema == CameraNetworkStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("CameraNetworkConfigurationRequired");
                if (_options.ImagingSetup is not null && schema < ImagingSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ImagingSetupGovernedMigrationRequired");
                if (_options.ImagingSetup is null && schema == ImagingSetupStoreOptions.SchemaVersion)
                    throw new InvalidOperationException("ImagingSetupConfigurationRequired");
                AuditChainDatabase.Require(schema is 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13,
                    "AuditGovernedMigrationRequired");
                var archiveSchema = schema == AlgorithmResultArchiveOptions.SchemaVersion ||
                    schema >= RecipeDraftStoreOptions.SchemaVersion && _options.AlgorithmResultArchive is not null;
                var draftSchema = schema == RecipeDraftStoreOptions.SchemaVersion ||
                    (schema is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                        CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
                        _options.RecipeDrafts is not null;
                var cameraSchema = schema is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                    CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion;
                var recoverySchema = schema == CameraRecoveryStoreOptions.SchemaVersion ||
                    (schema is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
                    _options.CameraRecovery is not null;
                var networkSchema = (schema is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion) &&
                    _options.CameraNetwork is not null;
                var imagingSchema = schema == ImagingSetupStoreOptions.SchemaVersion;
                var archiveVerification = archiveSchema;
                var draftVerification = draftSchema;
                var alarmStartup = schema >= 7 && startup && _options.AlarmPolicy is not null;
                var networkVerification = networkSchema;
                var fullVerification = archiveVerification || draftVerification || cameraSchema || recoverySchema || networkVerification ||
                    imagingSchema || alarmStartup;
                var verificationRequest = fullVerification
                    ? new AuditVerificationRequest(0, policy.MaximumVerificationEntries)
                    : request;
                var report = AuditChainDatabase.Verify(db, policy, key.KeyId, key.PublicKeyBase64,
                    verificationRequest, fullVerification ? false : startup, deadline,
                    archiveOptions: archiveSchema ? _options.AlgorithmResultArchive : null,
                    recipeDraftOptions: draftSchema ? _options.RecipeDrafts : null,
                    cameraSetupOptions: cameraSchema ? _options.CameraSetup : null,
                    cameraRecoveryOptions: recoverySchema ? _options.CameraRecovery : null,
                    cameraNetworkOptions: networkSchema ? _options.CameraNetwork : null,
                    imagingSetupOptions: imagingSchema ? _options.ImagingSetup : null);
                if (alarmStartup) AuditChainDatabase.RequireFullAlarmVerification(db, report, deadline);
                if (archiveSchema) AuditChainDatabase.RequireFullAlgorithmResultVerification(db, report, deadline);
                if (draftSchema) AuditChainDatabase.RequireFullRecipeDraftVerification(db, report, deadline,
                    _options.RecipeDrafts);
                if (cameraSchema) AuditChainDatabase.RequireFullCameraSetupVerification(db, report, deadline,
                    _options.CameraSetup);
                if (recoverySchema) AuditChainDatabase.RequireFullCameraRecoveryVerification(db, report, deadline,
                    _options.CameraRecovery);
                if (networkSchema) AuditChainDatabase.RequireFullCameraNetworkVerification(db, report, deadline,
                    _options.CameraNetwork);
                if (imagingSchema) AuditChainDatabase.RequireFullImagingSetupVerification(db, report, deadline,
                    _options.ImagingSetup);
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
        if (exception is InvalidOperationException { Message: var cameraReason } &&
            cameraReason.StartsWith("CameraSetup", StringComparison.Ordinal))
            return cameraReason;
        if (exception is InvalidOperationException { Message: var recoveryReason } &&
            recoveryReason.StartsWith("CameraRecovery", StringComparison.Ordinal))
            return recoveryReason;
        if (exception is InvalidOperationException { Message: var networkReason } &&
            networkReason.StartsWith("CameraNetwork", StringComparison.Ordinal))
            return networkReason;
        if (exception is InvalidOperationException { Message: var imagingReason } &&
            imagingReason.StartsWith("ImagingSetup", StringComparison.Ordinal))
            return imagingReason;
        if (exception is InvalidOperationException { Message: "IdentityAuthenticationGovernedMigrationRequired" })
            return "IdentityAuthenticationGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "IdentityAuthorizationGovernedMigrationRequired" })
            return "IdentityAuthorizationGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "IdentityRecoveryGovernedMigrationRequired" })
            return "IdentityRecoveryGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "GovernedAlarmMigrationRequired" })
            return "GovernedAlarmMigrationRequired";
        if (exception is InvalidOperationException { Message: "AlgorithmResultArchiveConfigurationRequired" })
            return "AlgorithmResultArchiveConfigurationRequired";
        if (exception is InvalidOperationException { Message: "AlgorithmResultArchiveGovernedMigrationRequired" })
            return "AlgorithmResultArchiveGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "RecipeDraftConfigurationRequired" })
            return "RecipeDraftConfigurationRequired";
        if (exception is InvalidOperationException { Message: "RecipeDraftGovernedMigrationRequired" })
            return "RecipeDraftGovernedMigrationRequired";
        if (exception is InvalidOperationException { Message: "RecipeDraftRequiresIdentityAndAudit" })
            return "RecipeDraftRequiresIdentityAndAudit";
        if (exception is InvalidOperationException { Message: var draftReason } &&
            draftReason.StartsWith("RecipeDraft", StringComparison.Ordinal))
            return draftReason;
        if (exception is InvalidOperationException { Message: var archiveReason } &&
            archiveReason.StartsWith("AlgorithmResult", StringComparison.Ordinal))
            return archiveReason;
        if (exception is InvalidOperationException { Message: var message } &&
            (message.StartsWith("RecoveryOperation", StringComparison.Ordinal)))
            return message;
        if (exception is InvalidOperationException { Message: var reason } &&
            (reason.StartsWith("Audit", StringComparison.Ordinal) || reason.StartsWith("Alarm", StringComparison.Ordinal)))
            return reason;
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
