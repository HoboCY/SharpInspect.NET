using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Integrity;

namespace SharpInspect.Runtime.Storage;

/// <summary>
/// The one-writer command fact coordinator. It is intentionally internal; hosts receive the
/// read-only <see cref="ICommandTraceQuery"/> capability instead of a mutation surface.
/// </summary>
internal sealed partial class SqliteCommandStore : ICommandAuditWriter, IAsyncDisposable
{
    internal const string SystemPrincipal = "SharpInspect.Runtime";
    private bool CameraSetupEnabled => _options.CameraSetup is not null;
    internal bool CameraRecoveryEnabled => _options.CameraRecovery is not null;
    internal bool CameraNetworkEnabled => _options.CameraNetwork is not null;
    internal bool ImagingSetupEnabled => _options.ImagingSetup is not null;
    internal bool CalibrationSessionsEnabled => _options.CalibrationSessions is not null;
    internal bool CalibrationEnabled => CalibrationSessionsEnabled;
    private bool RecipeDraftEnabled => _options.RecipeDrafts is not null;
    private bool AlgorithmArchiveEnabled => _options.AlgorithmResultArchive is not null;
    private int SchemaVersion => CalibrationGovernanceEnabled ? CalibrationGovernanceStoreOptions.SchemaVersion :
        CalibrationSessionsEnabled ? CalibrationSessionStoreOptions.SchemaVersion :
        ImagingSetupEnabled ? ImagingSetupStoreOptions.SchemaVersion :
        CameraNetworkEnabled ? CameraNetworkStoreOptions.SchemaVersion :
        CameraRecoveryEnabled ? CameraRecoveryStoreOptions.SchemaVersion :
        CameraSetupEnabled ? CameraSetupStoreOptions.SchemaVersion :
        RecipeDraftEnabled ? RecipeDraftStoreOptions.SchemaVersion :
        AlgorithmArchiveEnabled ? AlgorithmResultArchiveOptions.SchemaVersion :
        _options.LocalIdentity is not null ? 7 : _policy is null ? 1 : 2;
    private const int EventVersion = 1;
    private const int MaximumReasonLength = 256;
    private const int MaximumPrincipalLength = 256;
    private const long MaximumWalBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(5);

    private readonly string? _databasePath;
    private readonly string? _lockPath;
    private readonly string _initializationReason;
    private readonly Channel<WriteRequest>? _queue;
    private readonly SemaphoreSlim? _queueSlots;
    private readonly Task _worker;
    private readonly TaskCompletionSource<StoreWriteResult> _initializationCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _commitTimeout;
    private readonly Func<string, long> _readWalLength;
    private long _lastCheckpointTimestamp;
    private bool _walLimitExceeded;
    private int _disposed;

    internal VerifiedSqliteProfile? VerifiedProfile { get; private set; }
    internal RecipeDraftStoreOptions? RecipeDraftOptions => _options.RecipeDrafts;
    internal CameraSetupStoreOptions? CameraSetupOptions => _options.CameraSetup;
    internal CameraRecoveryStoreOptions? CameraRecoveryOptions => _options.CameraRecovery;
    internal ImagingSetupStoreOptions? ImagingSetupOptions => _options.ImagingSetup;
    internal CalibrationSessionStoreOptions? CalibrationSessionOptions => _options.CalibrationSessions;

    public SqliteCommandStore(ProductionStoreOptions options) : this(options, ReadFileLength) { }

    internal SqliteCommandStore(ProductionStoreOptions options, Func<string, long> readWalLength)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _policy = options.AuditIntegrityPolicy;
        _policy?.Validate();
        options.AlgorithmResultArchive?.Validate();
        options.RecipeDrafts?.Validate();
        options.CameraSetup?.Validate();
        options.CameraRecovery?.Validate();
        options.CameraNetwork?.Validate();
        options.ImagingSetup?.Validate();
        options.CalibrationSessions?.Validate();
        options.CalibrationGovernance?.Validate();
        if (options.CalibrationGovernance is not null && options.CalibrationSessions is null)
            throw new ArgumentException("CalibrationGovernanceRequiresCalibrationSessions", nameof(options));
        if (options.AlgorithmResultArchive is not null &&
            (options.LocalIdentity is null || _policy is null))
            throw new ArgumentException("AlgorithmResultArchiveRequiresIdentityAndAudit", nameof(options));
        if (options.RecipeDrafts is not null &&
            (options.LocalIdentity is null || _policy is null))
            throw new ArgumentException("RecipeDraftRequiresIdentityAndAudit", nameof(options));
        if (options.CameraSetup is not null &&
            (options.LocalIdentity is null || _policy is null))
            throw new ArgumentException("CameraSetupRequiresIdentityAndAudit", nameof(options));
        if (options.CameraRecovery is not null &&
            (options.CameraSetup is null || options.LocalIdentity is null || _policy is null ||
                options.AlarmPolicy is null))
            throw new ArgumentException("CameraRecoveryRequiresCameraSetupIdentityAuditAndAlarm", nameof(options));
        if (options.CameraNetwork is not null &&
            (options.CameraSetup is null || options.LocalIdentity is null || _policy is null))
            throw new ArgumentException("CameraNetworkRequiresCameraSetupIdentityAndAudit", nameof(options));
        if (options.ImagingSetup is not null &&
            (options.CameraSetup is null || options.LocalIdentity is null || _policy is null))
            throw new ArgumentException("ImagingSetupRequiresCameraSetupIdentityAndAudit", nameof(options));
        if (options.CalibrationSessions is not null &&
            (options.CameraSetup is null || options.ImagingSetup is null || options.CameraRecovery is null ||
                options.LocalIdentity is null || _policy is null))
            throw new ArgumentException(
                "CalibrationSessionRequiresCameraSetupImagingSetupCameraRecoveryIdentityAndAudit", nameof(options));
        if (options.AlarmPolicy is not null)
        {
            options.AlarmPolicy.Validate();
            if (options.LocalIdentity is null || _policy is null)
                throw new ArgumentException("AlarmPolicyRequiresIdentityAndAudit", nameof(options));
        }
        options.LocalIdentity?.Validate(_policy);
        _integrity = SqliteAuditIntegrityQuery.Report(_policy, _policy is null ? AuditIntegrityState.NotConfigured :
            AuditIntegrityState.Verifying, _policy is null ? "AuditPolicyNotConfigured" : "AuditStartupVerificationPending");
        _readWalLength = readWalLength ?? throw new ArgumentNullException(nameof(readWalLength));
        if (!StoragePathValidator.TryValidate(options, out var path, out var reason))
        {
            _initializationReason = reason;
            SetIntegrityFault(reason);
            _worker = Task.CompletedTask;
            _initializationCompletion.TrySetResult(new StoreWriteResult(false, reason));
            return;
        }

        _databasePath = path;
        _lockPath = path + ".lock";
        _initializationReason = string.Empty;
        _commitTimeout = options.CommitTimeout;
        _queue = Channel.CreateBounded<WriteRequest>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _queueSlots = new SemaphoreSlim(options.QueueCapacity, options.QueueCapacity);
        _worker = Task.Run(RunAsync);
    }

    public Task<StoreWriteResult> Initialization => _initializationCompletion.Task;
    public AuditIntegrityReport? Integrity => Volatile.Read(ref _integrity);

    public TimeSpan CommitTimeout => _commitTimeout == default ? TimeSpan.FromSeconds(2) : _commitTimeout;

    public async ValueTask<StoreWriteResult> AppendAsync(CommandAuditFact fact, StoreDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(deadline);
        if (Volatile.Read(ref _disposed) != 0)
            return new StoreWriteResult(false, "TraceStoreDisposed");
        if (_queue is null || _queueSlots is null)
            return new StoreWriteResult(false, _initializationReason);
        if (_worker.IsCompleted) return new StoreWriteResult(false, "TraceStoreUnavailable");

        SqliteNative.EnsureDeadline(deadline, cancellationToken);
        var remaining = deadline.Remaining;
        if (remaining <= TimeSpan.Zero) return new StoreWriteResult(false, "TraceCommitDeadlineExceeded");
        var acquired = await _queueSlots.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        if (!acquired) return new StoreWriteResult(false, "TraceCommitDeadlineExceeded");

        var request = new WriteRequest(fact, deadline);
        if (!_queue.Writer.TryWrite(request))
        {
            _queueSlots.Release();
            return new StoreWriteResult(false, Volatile.Read(ref _disposed) != 0
                ? "TraceStoreDisposed" : "TraceStoreUnavailable");
        }

        // Once admitted to the coordinator, cancellation cannot turn a committed transaction
        // into a caller-visible failure. The request has its original monotonic deadline.
        return await request.Completion.Task.ConfigureAwait(false);
    }

    private async ValueTask<StoreWriteResult> EnqueueVerifiedWorkAsync(Func<WriteRequest> createRequest,
        StoreDeadline deadline, CancellationToken cancellationToken, string unavailableReason, string deadlineReason)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || _worker.IsCompleted)
                return new(false, unavailableReason);
            var remaining = deadline.Remaining;
            if (remaining <= TimeSpan.Zero || !await _queueSlots!.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
                return new(false, deadlineReason);
            var request = createRequest();
            if (!_queue!.Writer.TryWrite(request))
            {
                _queueSlots.Release();
                return new(false, unavailableReason);
            }

            // Await the definitive writer result even if the caller cancels after admission.
            // Only the pre-BEGIN Verifying branch can request a retry: no callback or mutation ran.
            var result = await request.Completion.Task.ConfigureAwait(false);
            if (!result.RetryAfterIntegrityRecheck) return result;

            // Wait outside the writer and without holding a queue slot. In particular,
            // external anchor receipts must remain able to advance the verifier.
            while (true)
            {
                var integrity = Integrity;
                if (integrity?.State == AuditIntegrityState.Verified) break;
                if (integrity?.State != AuditIntegrityState.Verifying)
                    return new(false, integrity?.ReasonCode ?? unavailableReason);
                if (Volatile.Read(ref _disposed) != 0 || _worker.IsCompleted)
                    return new(false, unavailableReason);
                remaining = deadline.Remaining;
                if (remaining <= TimeSpan.Zero) return new(false, deadlineReason);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(20, remaining.TotalMilliseconds)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_queue is null || _queueSlots is null)
        {
            _queueSlots?.Dispose();
            _integrityLifetime.Dispose();
            _integrityWake.Dispose();
            return;
        }

        _queue.Writer.TryComplete();
        _integrityLifetime.Cancel();
        await _worker.ConfigureAwait(false);
        if (_integrityMonitor is not null) await _integrityMonitor.ConfigureAwait(false);
        _integrityLifetime.Dispose();
        _integrityWake.Dispose();
        _queueSlots.Dispose();
    }

    private async Task RunAsync()
    {
        SqliteConnection? connection = null;
        FileStream? lease = null;
        var initialized = false;
        var initializationResult = new StoreWriteResult(false, "TraceStoreUnavailable");
        try
        {
            try
            {
                PreflightAuditVersion();
                lease = AcquireLease();
                connection = SqliteNative.Open(_databasePath!, readOnly: false);
                SqliteNative.ConfigureSqliteLimit(connection.Handle!, _options);
                initializationResult = InitializeDatabase(connection);
                initialized = initializationResult.Committed;
                if (initialized && _policy is not null)
                {
                    _integrityMonitor = Task.Run(MonitorIntegrityAsync);
                }
            }
            catch (TimeoutException)
            {
                initializationResult = new StoreWriteResult(false, "TraceCommitDeadlineExceeded");
            }
            catch (SqliteNativeException ex)
            {
                initializationResult = new StoreWriteResult(false, ex.ReasonCode);
            }
            catch (SqliteException)
            {
                initializationResult = new StoreWriteResult(false, "TraceStoreUnavailable");
            }
            catch (IOException)
            {
                initializationResult = new StoreWriteResult(false, "StoreWriterAlreadyOpen");
            }
            catch (UnauthorizedAccessException)
            {
                initializationResult = new StoreWriteResult(false, "StorePathAccessDenied");
            }
            catch (Exception ex)
            {
                var reason = ex is InvalidOperationException && (ex.Message.StartsWith("Audit", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("Identity", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("RecoveryOperation", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("Alarm", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("AlgorithmResultArchive", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("RecipeDraft", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("CameraSetup", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("CameraRecovery", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("CameraNetwork", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("ImagingSetup", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("Calibration", StringComparison.Ordinal) ||
                    ex.Message.StartsWith("GovernedAlarm", StringComparison.Ordinal))
                    ? ex.Message : "TraceStoreUnavailable";
                SetIntegrityFault(reason);
                initializationResult = new StoreWriteResult(false, reason);
            }

            if (!initialized)
            {
                SetIntegrityFault(initializationResult.ReasonCode);
                connection?.Dispose();
                connection = null;
                lease?.Dispose();
                lease = null;
            }
            _initializationCompletion.TrySetResult(initializationResult);
            if (_queue is null || _queueSlots is null) return;

            await foreach (var request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                _queueSlots.Release();
                StoreWriteResult result;
                if (!initialized || connection is null)
                {
                    result = new StoreWriteResult(false, initializationResult.ReasonCode);
                }
                else
                {
                    try
                    {
                        result = request.NetworkAdmission is { } networkAdmission ? AppendCameraNetworkAdmissionCore(connection.Handle!, networkAdmission, request.Deadline) :
                            request.NetworkTerminal is { } networkTerminal ? AppendCameraNetworkTerminalCore(connection.Handle!, networkTerminal, request.Deadline) :
                            request.NetworkRejected is { } networkRejected ? AppendCameraNetworkRejectedCore(connection.Handle!, networkRejected, request.Deadline) :
                            request.CalibrationEvent is { } calibrationEvent ? AppendCalibrationEventCore(connection.Handle!, calibrationEvent, request.Deadline) :
                            request.AlgorithmResult is { } algorithm ? AppendAlgorithmResultCore(connection.Handle!, algorithm, request.Deadline) :
                            request.RecipeDraft is { } draft ? SaveRecipeDraftCore(connection.Handle!, draft, request.Deadline) :
                            request.RecoveryTerminal is { } recoveryTerminal ? AppendCameraRecoveryTerminalCore(connection.Handle!, recoveryTerminal, request.Deadline) :
                            request.Identity is { } identity ? UpdateIdentityCore(connection.Handle!, identity, request.Deadline) :
                            request.AlarmObservation is { } observation ? UpdateAlarmObservationCore(connection.Handle!, observation, request.Deadline) :
                            request.Receipt is { } receipt ? AppendReceiptCore(connection, receipt, request.Deadline) :
                            AppendCore(connection, request.Fact!, request.Deadline);
                    }
                    catch (TimeoutException)
                    {
                        result = new StoreWriteResult(false, "TraceCommitDeadlineExceeded");
                    }
                    catch (SqliteNativeException ex)
                    {
                        result = new StoreWriteResult(false, ex.ReasonCode);
                    }
                    catch (SqliteException)
                    {
                        result = new StoreWriteResult(false, "TraceStoreUnavailable");
                    }
                    catch (Exception)
                    {
                        result = new StoreWriteResult(false, "TraceStoreUnavailable");
                    }
                }

                request.Completion.TrySetResult(result);
                if (initialized && connection is not null && _queue.Reader.Count == 0)
                    MaybeCheckpoint(connection);
            }
        }
        catch (Exception ex)
        {
            var reason = ex is TimeoutException ? "TraceCommitDeadlineExceeded" : "TraceStoreUnavailable";
            _initializationCompletion.TrySetResult(new StoreWriteResult(false, reason));
            if (_queue is not null && _queueSlots is not null)
            {
                while (_queue.Reader.TryRead(out var pending))
                {
                    _queueSlots.Release();
                    pending.Completion.TrySetResult(new StoreWriteResult(false, reason));
                }
            }
        }
        finally
        {
            // No request can be accepted after its only consumer has exited.
            _queue?.Writer.TryComplete();
            connection?.Dispose();
            lease?.Dispose();
            _signingKey?.Dispose();
            if (_queue is not null && _queueSlots is not null)
            {
                while (_queue.Reader.TryRead(out var pending))
                {
                    _queueSlots.Release();
                    pending.Completion.TrySetResult(new StoreWriteResult(false, "TraceStoreDisposed"));
                }
            }
        }
    }

    private FileStream AcquireLease()
    {
        try
        {
            return new FileStream(_lockPath!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 1, options: FileOptions.SequentialScan);
        }
        catch (IOException)
        {
            throw new IOException("The command trace writer is already open.");
        }
    }

    private string MigrationReason(int existingVersion) => CalibrationGovernanceEnabled
        ? "CalibrationGovernanceMigrationRequired"
        : CalibrationSessionsEnabled
        ? "CalibrationGovernedMigrationRequired"
        : ImagingSetupEnabled
        ? "ImagingSetupGovernedMigrationRequired"
        : CameraNetworkEnabled
        ? "CameraNetworkGovernedMigrationRequired"
        : CameraRecoveryEnabled
        ? "CameraRecoveryGovernedMigrationRequired"
        : CameraSetupEnabled
        ? "CameraSetupGovernedMigrationRequired"
        : RecipeDraftEnabled
        ? "RecipeDraftGovernedMigrationRequired"
        : AlgorithmArchiveEnabled
        ? "AlgorithmResultArchiveGovernedMigrationRequired"
        : SchemaVersion == 7
        ? existingVersion switch
        {
            6 => "GovernedAlarmMigrationRequired",
            5 => "IdentityRecoveryGovernedMigrationRequired",
            4 => "IdentityAuthorizationGovernedMigrationRequired",
            3 => "IdentityAuthenticationGovernedMigrationRequired",
            _ => "AuditGovernedMigrationRequired"
        }
        : SchemaVersion == 6
        ? existingVersion switch
        {
            5 => "IdentityRecoveryGovernedMigrationRequired",
            4 => "IdentityAuthorizationGovernedMigrationRequired",
            3 => "IdentityAuthenticationGovernedMigrationRequired",
            _ => "AuditGovernedMigrationRequired"
        }
        : "AuditGovernedMigrationRequired";

    private StoreWriteResult InitializeDatabase(SqliteConnection connection)
    {
        var deadline = new StoreDeadline(CommitTimeout);
        var database = connection.Handle!;
        var version = ReadUserVersion(database, deadline);
        var objects = ReadSchemaObjects(database, deadline, includeInternalObjects: version == 0);
        if (version < 0) return new StoreWriteResult(false, "StoreSchemaUnsupported");
        if (version > SchemaVersion)
            return new StoreWriteResult(false,
                !CalibrationGovernanceEnabled && version == CalibrationGovernanceStoreOptions.SchemaVersion
                    ? "CalibrationGovernanceConfigurationRequired"
                    : !CalibrationSessionsEnabled && version == CalibrationSessionStoreOptions.SchemaVersion
                    ? "CalibrationConfigurationRequired"
                    : !ImagingSetupEnabled && version == ImagingSetupStoreOptions.SchemaVersion
                    ? "ImagingSetupConfigurationRequired"
                    : !CameraNetworkEnabled && version == CameraNetworkStoreOptions.SchemaVersion
                    ? "CameraNetworkConfigurationRequired"
                    : !CameraRecoveryEnabled && version == CameraRecoveryStoreOptions.SchemaVersion
                    ? "CameraRecoveryConfigurationRequired"
                    : !CameraSetupEnabled && version == CameraSetupStoreOptions.SchemaVersion
                    ? "CameraSetupConfigurationRequired"
                    : !CameraSetupEnabled && !RecipeDraftEnabled && version == RecipeDraftStoreOptions.SchemaVersion
                        ? "RecipeDraftConfigurationRequired"
                        : !CameraSetupEnabled && !RecipeDraftEnabled && !AlgorithmArchiveEnabled &&
                          version == AlgorithmResultArchiveOptions.SchemaVersion
                            ? "AlgorithmResultArchiveConfigurationRequired" : "StoreSchemaTooNew");
        if (version > 0 && version < SchemaVersion)
            return new StoreWriteResult(false, MigrationReason(version));
        if (version == 0 && objects.Count != 0) return new StoreWriteResult(false, "StoreForeignSchema");
        if (version == SchemaVersion && !ValidateSchemaShape(database, deadline))
            return new StoreWriteResult(false, "StoreSchemaMismatch");

        if (_policy is not null)
        {
            _signingKey = WindowsMachineAuditKey.Open(_policy, version == 0, out _);
            if (version >= 2)
            {
                var archiveSchema = AlgorithmArchiveEnabled &&
                    (version == SchemaVersion || version is RecipeDraftStoreOptions.SchemaVersion or
                        CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                        CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                        CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion);
                var alarmSchema = version >= 7 && _options.AlarmPolicy is not null;
                var draftSchema = RecipeDraftEnabled &&
                    (version is RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
                        CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                        ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion);
                var cameraSchema = CameraSetupEnabled &&
                    (version is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
                        CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                        CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion);
                var recoverySchema = CameraRecoveryEnabled &&
                    (version is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
                        ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion);
                var networkSchema = CameraNetworkEnabled &&
                    (version is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
                        CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion);
                var imagingSchema = ImagingSetupEnabled &&
                    (version is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion);
                var calibrationSchema = CalibrationSessionsEnabled &&
                    version is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion;
                var verification = AuditChainDatabase.Verify(database, _policy, _signingKey.KeyId,
                    _signingKey.PublicKeyBase64,
                    archiveSchema || alarmSchema || draftSchema || cameraSchema || recoverySchema || networkSchema || imagingSchema || calibrationSchema ? new AuditVerificationRequest(0, _policy.MaximumVerificationEntries) :
                    new AuditVerificationRequest(), !archiveSchema && !alarmSchema && !draftSchema && !cameraSchema && !recoverySchema && !networkSchema && !imagingSchema && !calibrationSchema, deadline,
                    validateAnchorReceipt: false, archiveOptions: archiveSchema ? _options.AlgorithmResultArchive : null,
                    recipeDraftOptions: draftSchema ? _options.RecipeDrafts : null,
                    cameraSetupOptions: cameraSchema ? _options.CameraSetup : null,
                    cameraRecoveryOptions: recoverySchema ? _options.CameraRecovery : null,
                    cameraNetworkOptions: networkSchema ? _options.CameraNetwork : null,
                    imagingSetupOptions: imagingSchema ? _options.ImagingSetup : null,
                    calibrationSessionOptions: calibrationSchema ? _options.CalibrationSessions : null,
                    governanceOptions: _options.CalibrationGovernance);
                if (alarmSchema) AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
                if (archiveSchema) AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
                if (draftSchema) AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                    _options.RecipeDrafts);
                if (cameraSchema) AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                    _options.CameraSetup);
                if (recoverySchema) AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification,
                    deadline, _options.CameraRecovery);
                if (networkSchema) AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification,
                    deadline, _options.CameraNetwork);
                if (imagingSchema) AuditChainDatabase.RequireFullImagingSetupVerification(database, verification,
                    deadline, _options.ImagingSetup);
            }
            if (version >= 7) _ = ReadIdentityState(database, deadline);
        }

        if (version >= 7)
        {
            var persistedAlarmPolicy = AlarmStorageCodec.ReadPersistedPolicy(database, deadline);
            AlarmStorageCodec.RequireConfiguredPolicy(persistedAlarmPolicy, _options.AlarmPolicy);
        }
        if (version == AlgorithmResultArchiveOptions.SchemaVersion || version == RecipeDraftStoreOptions.SchemaVersion ||
            version == CameraSetupStoreOptions.SchemaVersion || version == CameraRecoveryStoreOptions.SchemaVersion ||
            version == CameraNetworkStoreOptions.SchemaVersion || version == ImagingSetupStoreOptions.SchemaVersion ||
            version is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion)
        {
            if (AlgorithmArchiveEnabled)
                SqliteCommandStore.RequireConfiguredArchive(database, _options.AlgorithmResultArchive!, deadline);
        }
        if ((version is RecipeDraftStoreOptions.SchemaVersion or CameraSetupStoreOptions.SchemaVersion or
            CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
            ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion) &&
            RecipeDraftEnabled)
            SqliteCommandStore.RequireConfiguredRecipeDrafts(database, _options.RecipeDrafts!, deadline);
        if (version is CameraSetupStoreOptions.SchemaVersion or CameraRecoveryStoreOptions.SchemaVersion or
            CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion)
            SqliteCommandStore.RequireConfiguredCameraSetup(database, _options.CameraSetup!, deadline);
        if (CameraRecoveryEnabled && (version is CameraRecoveryStoreOptions.SchemaVersion or CameraNetworkStoreOptions.SchemaVersion or
            ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion))
            SqliteCommandStore.RequireConfiguredCameraRecovery(database, _options.CameraRecovery!, deadline);
        if (CameraNetworkEnabled && (version is CameraNetworkStoreOptions.SchemaVersion or ImagingSetupStoreOptions.SchemaVersion or
            CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion))
            SqliteCommandStore.RequireConfiguredCameraNetwork(database, _options.CameraNetwork!, deadline);
        if (version is ImagingSetupStoreOptions.SchemaVersion or CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion)
            SqliteCommandStore.RequireConfiguredImagingSetup(database, _options.ImagingSetup!, deadline);
        if (version is CalibrationSessionStoreOptions.SchemaVersion or CalibrationGovernanceStoreOptions.SchemaVersion)
        {
            SqliteCommandStore.RequireConfiguredCalibrationSessions(database, _options.CalibrationSessions!, deadline);
            SqliteCommandStore.ValidateCalibrationSessionHistory(database, _options.CalibrationSessions!, deadline,
                _policy?.StationId ?? string.Empty);
        }

        if (version == CalibrationGovernanceStoreOptions.SchemaVersion)
            ValidateCalibrationGovernanceHistory(database, _options.CalibrationGovernance!, deadline);

        if (!ConfigureProductionProfile(database, deadline))
            return new StoreWriteResult(false, "TraceStoreProfileUnsupported");
        if (version == SchemaVersion) return new StoreWriteResult(true, "TraceStoreReady");

        var transactionStarted = false;
        var committed = false;
        try
        {
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            transactionStarted = true;
            SqliteNative.Execute(database, SchemaSql, deadline);
            if (_policy is not null)
            {
                SqliteNative.Execute(database, AuditChainDatabase.SchemaSqlFor(SchemaVersion), deadline);
                AuditChainDatabase.CreateGenesis(database, _policy, _signingKey!, deadline);
            }
            if (_options.LocalIdentity is not null) InitializeIdentitySchema(database, deadline);
            if (SchemaVersion >= 7)
            {
                SqliteNative.Execute(database, AlarmSchemaSql, deadline);
                InitializeAlarmSchema(database, deadline);
            }
            if (AlgorithmArchiveEnabled)
            {
                SqliteNative.Execute(database, AlgorithmResultSchemaSql, deadline);
                InitializeAlgorithmResultSchema(database, _options.AlgorithmResultArchive!, deadline);
            }
            if (RecipeDraftEnabled)
            {
                SqliteNative.Execute(database, RecipeDraftSchemaSql, deadline);
                InitializeRecipeDraftSchema(database, _options.RecipeDrafts!, deadline);
            }
            if (CameraSetupEnabled)
            {
                SqliteNative.Execute(database, CameraSetupSchemaSql, deadline);
                InitializeCameraSetupSchema(database, _options.CameraSetup!, deadline);
            }
            if (CameraRecoveryEnabled)
            {
                SqliteNative.Execute(database, CameraRecoverySchemaSql, deadline);
                InitializeCameraRecoverySchema(database, _options.CameraRecovery!, deadline);
            }
            if (CameraNetworkEnabled)
            {
                SqliteNative.Execute(database, CameraNetworkSchemaSql, deadline);
                InitializeCameraNetworkSchema(database, _options.CameraNetwork!, deadline);
            }
            if (ImagingSetupEnabled)
            {
                SqliteNative.Execute(database, ImagingSetupSchemaSql, deadline);
                InitializeImagingSetupSchema(database, _options.ImagingSetup!, deadline);
            }
            if (CalibrationSessionsEnabled)
            {
                SqliteNative.Execute(database, CalibrationSessionSchemaSql, deadline);
                InitializeCalibrationSessionSchema(database, _options.CalibrationSessions!, deadline);
            }
            if (CalibrationGovernanceEnabled)
                InitializeCalibrationGovernanceSchema(database, _options.CalibrationGovernance!, deadline,
                    _policy!, _signingKey!);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            return new StoreWriteResult(true, "TraceStoreReady");
        }
        finally
        {
            if (transactionStarted && !committed) Rollback(database);
        }
    }

    private bool ConfigureProductionProfile(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        SqliteNative.Execute(database, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA wal_autocheckpoint=0;",
            deadline);
        var journalMode = SqliteNative.WithStatement(database, "PRAGMA journal_mode;", deadline,
            statement => SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW
                ? SqliteNative.ColumnText(statement, 0) : null);
        var synchronous = SqliteNative.WithStatement(database, "PRAGMA synchronous;", deadline,
            statement => SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW
                ? SqliteNative.ColumnInt64(statement, 0) : -1);
        var foreignKeys = SqliteNative.WithStatement(database, "PRAGMA foreign_keys;", deadline,
            statement => SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW
                ? SqliteNative.ColumnInt64(statement, 0) : -1);
        var autoCheckpoint = SqliteNative.WithStatement(database, "PRAGMA wal_autocheckpoint;", deadline,
            statement => SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW
                ? SqliteNative.ColumnInt64(statement, 0) : -1);
        VerifiedProfile = new VerifiedSqliteProfile(journalMode ?? string.Empty, synchronous, foreignKeys, autoCheckpoint);
        return string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase) && synchronous == 2 &&
            foreignKeys == 1 && autoCheckpoint == 0;
    }

    private StoreWriteResult AppendCore(SqliteConnection connection, CommandAuditFact input, StoreDeadline deadline)
    {
        ValidateFact(input);
        var localStop = input.CommandKind == AuditedCommandKind.GracefulProductionStop &&
            input.Source == CommandSource.PhysicalConsole;
        if (_policy is not null && (Integrity?.State == AuditIntegrityState.Faulted ||
            input.Phase == CommandAuditPhase.Outcome && !localStop && Integrity?.State != AuditIntegrityState.Verified))
            return new StoreWriteResult(false, Integrity?.ReasonCode ?? "AuditIntegrityUnavailable");
        // A local Stop may arrive while the last committed startup/observation event is
        // awaiting background verification. The transaction below still verifies the actual
        // signed chain before writing; that advisory poll cannot reject the dedicated Stop lane.
        if (_walLimitExceeded || GetWalLength() > MaximumWalBytes)
            return new StoreWriteResult(false, "TraceStoreWalLimit");
        var database = connection.Handle!;
        var started = false;
        var committed = false;
        try
        {
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "BEGIN IMMEDIATE;", deadline);
            started = true;

            if (_policy is not null)
            {
                try
                {
                    // Recheck the current signed checkpoint through the tail under the writer
                    // lock. A previous background observation cannot authorize a changed tail.
                    var alarmStore = _options.AlarmPolicy is not null;
                    var archiveStore = _options.AlgorithmResultArchive is not null;
                    var draftStore = _options.RecipeDrafts is not null;
                    var cameraStore = _options.CameraSetup is not null;
                    var recoveryStore = _options.CameraRecovery is not null;
                    var networkStore = _options.CameraNetwork is not null;
                    var imagingStore = _options.ImagingSetup is not null;
                    var calibrationStore = _options.CalibrationSessions is not null;
                    var verification = AuditChainDatabase.Verify(database, _policy, _signingKey!.KeyId,
                        _signingKey.PublicKeyBase64,
                        archiveStore || alarmStore || draftStore || cameraStore || recoveryStore || networkStore || imagingStore || calibrationStore ?
                            new AuditVerificationRequest(0, _policy.MaximumVerificationEntries) :
                            new AuditVerificationRequest(), !archiveStore && !alarmStore && !draftStore && !cameraStore && !recoveryStore && !networkStore && !imagingStore && !calibrationStore, deadline,
                        validateAnchorReceipt: false, archiveOptions: archiveStore ? _options.AlgorithmResultArchive : null,
                        recipeDraftOptions: draftStore ? _options.RecipeDrafts : null,
                        cameraSetupOptions: cameraStore ? _options.CameraSetup : null,
                        cameraRecoveryOptions: recoveryStore ? _options.CameraRecovery : null,
                        cameraNetworkOptions: networkStore ? _options.CameraNetwork : null,
                        imagingSetupOptions: imagingStore ? _options.ImagingSetup : null,
                        calibrationSessionOptions: calibrationStore ? _options.CalibrationSessions : null,
                        governanceOptions: _options.CalibrationGovernance);
                    if (alarmStore)
                        AuditChainDatabase.RequireFullAlarmVerification(database, verification, deadline);
                    if (archiveStore)
                        AuditChainDatabase.RequireFullAlgorithmResultVerification(database, verification, deadline);
                    if (draftStore)
                        AuditChainDatabase.RequireFullRecipeDraftVerification(database, verification, deadline,
                            _options.RecipeDrafts);
                    if (cameraStore)
                        AuditChainDatabase.RequireFullCameraSetupVerification(database, verification, deadline,
                            _options.CameraSetup);
                    if (recoveryStore)
                        AuditChainDatabase.RequireFullCameraRecoveryVerification(database, verification, deadline,
                            _options.CameraRecovery);
                    if (networkStore)
                        AuditChainDatabase.RequireFullCameraNetworkVerification(database, verification, deadline,
                            _options.CameraNetwork);
                    if (imagingStore)
                        AuditChainDatabase.RequireFullImagingSetupVerification(database, verification, deadline,
                            _options.ImagingSetup);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var reason = SqliteAuditIntegrityQuery.FaultReason(ex, "AuditVerificationUnavailable");
                    if (!AuditChainDatabase.IsCapacityReason(reason))
                        SetIntegrityFault(reason, IsStructuralFault(reason));
                    return new StoreWriteResult(false, reason);
                }
            }

            var fact = input;
            if (input.Phase == CommandAuditPhase.Outcome)
            {
                if (Exists(database, "SELECT 1 FROM command_attempts WHERE AttemptId=? LIMIT 1;", input.AttemptId,
                    deadline))
                    return new StoreWriteResult(false, "DuplicateAttemptId");
                if (Exists(database, "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", input.EventId, deadline))
                    return new StoreWriteResult(false, "DuplicateEventId");

                if (input.CorrelationId != Guid.Empty && Exists(database,
                        "SELECT 1 FROM command_attempts WHERE CorrelationId=? LIMIT 1;", input.CorrelationId, deadline))
                {
                    fact = input with { Disposition = CommandDisposition.Rejected, ReasonCode = "DuplicateCorrelationId" };
                }

                InsertAttempt(database, fact, deadline);
                InsertFact(database, fact, aggregateSequence: 1, deadline);
            }
            else
            {
                var attempt = ReadAttempt(database, input.AttemptId, deadline);
                if (attempt is null) return new StoreWriteResult(false, "LifecycleOutcomeMissing");
                if (attempt.Value.OutcomeDisposition != CommandDisposition.Accepted)
                    return new StoreWriteResult(false, "LifecycleOutcomeNotAccepted");
                if (!attempt.Value.Matches(input)) return new StoreWriteResult(false, "LifecycleContextMismatch");

                var existing = ReadFact(database, input.AttemptId, aggregateSequence: 2, deadline);
                if (existing is not null)
                {
                    if (existing.EventId == input.EventId && existing.Phase == input.Phase &&
                        existing.ReasonCode == input.ReasonCode && existing.OccurredAtUtc == input.OccurredAtUtc &&
                        existing.CommandKind == input.CommandKind && existing.Source == input.Source &&
                        existing.ClaimedPrincipalId == input.ClaimedPrincipalId &&
                        existing.ClaimedSessionId == input.ClaimedSessionId &&
                        existing.ClaimedStepUpGrantId == input.ClaimedStepUpGrantId &&
                        existing.AuthenticatedHumanPrincipalId == input.AuthenticatedHumanPrincipalId)
                        return new StoreWriteResult(true, "DuplicateTerminal", existing);
                    return new StoreWriteResult(false, "DuplicateTerminalConflict");
                }
                if (Exists(database, "SELECT 1 FROM command_facts WHERE EventId=? LIMIT 1;", input.EventId, deadline))
                    return new StoreWriteResult(false, "DuplicateEventId");
                InsertFact(database, input, aggregateSequence: 2, deadline);
            }

            if (_policy is not null)
                AuditChainDatabase.AppendCommand(database, _policy, _signingKey!, fact.EventId, deadline);
            var committedAuditSequence = _policy is null ? 0 : AuditChainDatabase.Tail(database, deadline).Sequence;
            SqliteNative.EnsureDeadline(deadline, default);
            SqliteNative.Execute(database, "COMMIT;", deadline);
            committed = true;
            if (_policy is not null) Interlocked.Exchange(ref _lastCommittedAuditSequence, committedAuditSequence);
            if (_policy is not null)
            {
                PublishIntegrity(SqliteAuditIntegrityQuery.Report(_policy, AuditIntegrityState.Verifying,
                    _policy.RequireExternalAnchor ? "AuditAnchorRecheckPending" : "AuditRecheckPending"));
                WakeIntegrityMonitor();
            }
            return new StoreWriteResult(true, "TraceFactPersisted", fact);
        }
        catch (InvalidOperationException ex) when (AuditChainDatabase.IsCapacityReason(ex.Message))
        {
            return new StoreWriteResult(false, ex.Message);
        }
        finally
        {
            if (started && !committed) Rollback(database);
        }
    }

    private long GetWalLength()
    {
        try
        {
            return _readWalLength(_databasePath! + "-wal");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return MaximumWalBytes + 1;
        }
    }

    private static long ReadFileLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    private void MaybeCheckpoint(SqliteConnection connection)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastCheckpointTimestamp);
        if (last != 0 && TimeSpan.FromSeconds((now - last) / (double)Stopwatch.Frequency) < CheckpointInterval) return;
        Volatile.Write(ref _lastCheckpointTimestamp, now);
        try
        {
            var deadline = new StoreDeadline(TimeSpan.FromMilliseconds(100));
            SqliteNative.Execute(connection.Handle!, "PRAGMA wal_checkpoint(PASSIVE);", deadline);
            _walLimitExceeded = GetWalLength() > MaximumWalBytes;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The committed response has already been delivered. Maintenance failure cannot
            // lose it or terminate the writer and strand subsequent callers.
            _walLimitExceeded = true;
        }
    }

    private static void ValidateFact(CommandAuditFact fact)
    {
        if (fact.EventId == Guid.Empty) throw new InvalidOperationException("EventIdRequired");
        if (fact.AttemptId == Guid.Empty) throw new InvalidOperationException("AttemptIdRequired");
        if (fact.RuntimeEpoch == Guid.Empty) throw new InvalidOperationException("RuntimeEpochRequired");
        if (string.IsNullOrWhiteSpace(fact.ReasonCode) || fact.ReasonCode.Length > MaximumReasonLength)
            throw new InvalidOperationException("ReasonCodeInvalid");
        if (fact.ClaimedPrincipalId?.Length > MaximumPrincipalLength) throw new InvalidOperationException("PrincipalInvalid");
        if (fact.AuthenticatedHumanPrincipalId is not null &&
            (!Guid.TryParseExact(fact.AuthenticatedHumanPrincipalId, "D", out var authenticatedPrincipal) ||
             authenticatedPrincipal == Guid.Empty))
            throw new InvalidOperationException("AuthenticatedPrincipalInvalid");
        if (fact.Source is not null && !Enum.IsDefined(typeof(CommandSource), fact.Source.Value))
            throw new InvalidOperationException("CommandSourceInvalid");
        if (!Enum.IsDefined(typeof(AuditedCommandKind), fact.CommandKind)) throw new InvalidOperationException("CommandKindInvalid");
        if (!Enum.IsDefined(typeof(CommandAuditPhase), fact.Phase)) throw new InvalidOperationException("AuditPhaseInvalid");
        if (fact.Phase == CommandAuditPhase.Outcome && fact.Disposition is null)
            throw new InvalidOperationException("OutcomeDispositionRequired");
        if (fact.Phase != CommandAuditPhase.Outcome && fact.Disposition is not null)
            throw new InvalidOperationException("TerminalDispositionForbidden");
    }

    private static void InsertAttempt(SQLitePCL.sqlite3 database, CommandAuditFact fact, StoreDeadline deadline)
    {
        const string sql = @"
            INSERT INTO command_attempts(
                AttemptId, CorrelationId, RuntimeEpoch, CommandKind, Source, ClaimedPrincipalId,
                ClaimedSessionId, ClaimedStepUpGrantId, OutcomeDisposition, OutcomeReasonCode,
                OutcomeEventId, OutcomeOccurredAtUtc, SystemPrincipalId, AuthenticatedHumanPrincipalId)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SharpInspect.Runtime', ?);
            ";
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            SqliteNative.BindGuid(database, statement, 1, fact.AttemptId);
            SqliteNative.BindGuid(database, statement, 2, fact.CorrelationId);
            SqliteNative.BindGuid(database, statement, 3, fact.RuntimeEpoch);
            SqliteNative.BindInt(database, statement, 4, (int)fact.CommandKind);
            BindEnum(database, statement, 5, fact.Source);
            SqliteNative.BindText(database, statement, 6, fact.ClaimedPrincipalId);
            BindGuid(database, statement, 7, fact.ClaimedSessionId);
            BindGuid(database, statement, 8, fact.ClaimedStepUpGrantId);
            SqliteNative.BindInt(database, statement, 9, (int)fact.Disposition!.Value);
            SqliteNative.BindText(database, statement, 10, fact.ReasonCode);
            SqliteNative.BindGuid(database, statement, 11, fact.EventId);
            SqliteNative.BindText(database, statement, 12, FormatTime(fact.OccurredAtUtc));
            SqliteNative.BindText(database, statement, 13, fact.AuthenticatedHumanPrincipalId);
            SqliteNative.Step(database, statement, deadline);
            return 0;
        });
    }

    private static void InsertFact(SQLitePCL.sqlite3 database, CommandAuditFact fact, int aggregateSequence,
        StoreDeadline deadline)
    {
        const string sql = @"
            INSERT INTO command_facts(
                EventId, AttemptId, CorrelationId, RuntimeEpoch, EventVersion, AggregateSequence,
                OccurredAtUtc, SystemPrincipalId, AuthenticatedHumanPrincipalId, CommandKind, Source,
                ClaimedPrincipalId, ClaimedSessionId, ClaimedStepUpGrantId, Phase, Disposition, ReasonCode)
            VALUES (?, ?, ?, ?, 1, ?, ?, 'SharpInspect.Runtime', ?, ?, ?, ?, ?, ?, ?, ?, ?);
            ";
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            SqliteNative.BindGuid(database, statement, 1, fact.EventId);
            SqliteNative.BindGuid(database, statement, 2, fact.AttemptId);
            SqliteNative.BindGuid(database, statement, 3, fact.CorrelationId);
            SqliteNative.BindGuid(database, statement, 4, fact.RuntimeEpoch);
            SqliteNative.BindInt(database, statement, 5, aggregateSequence);
            SqliteNative.BindText(database, statement, 6, FormatTime(fact.OccurredAtUtc));
            SqliteNative.BindText(database, statement, 7, fact.AuthenticatedHumanPrincipalId);
            SqliteNative.BindInt(database, statement, 8, (int)fact.CommandKind);
            BindEnum(database, statement, 9, fact.Source);
            SqliteNative.BindText(database, statement, 10, fact.ClaimedPrincipalId);
            BindGuid(database, statement, 11, fact.ClaimedSessionId);
            BindGuid(database, statement, 12, fact.ClaimedStepUpGrantId);
            SqliteNative.BindInt(database, statement, 13, (int)fact.Phase);
            BindEnum(database, statement, 14, fact.Disposition);
            SqliteNative.BindText(database, statement, 15, fact.ReasonCode);
            SqliteNative.Step(database, statement, deadline);
            return 0;
        });
    }

    private static bool Exists(SQLitePCL.sqlite3 database, string sql, Guid value, StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, sql, deadline, statement =>
        {
            SqliteNative.BindGuid(database, statement, 1, value);
            return SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW;
        });

    private static AttemptContext? ReadAttempt(SQLitePCL.sqlite3 database, Guid attemptId, StoreDeadline deadline) =>
        SqliteNative.WithStatement<AttemptContext?>(database, @"SELECT CorrelationId, RuntimeEpoch, CommandKind, Source,
            ClaimedPrincipalId, ClaimedSessionId, ClaimedStepUpGrantId, OutcomeDisposition,
            AuthenticatedHumanPrincipalId
            FROM command_attempts WHERE AttemptId=? LIMIT 1;", deadline, statement =>
        {
            SqliteNative.BindGuid(database, statement, 1, attemptId);
            if (SqliteNative.Step(database, statement, deadline) != SQLitePCL.raw.SQLITE_ROW) return null;
            return new AttemptContext(ParseGuid(SqliteNative.ColumnText(statement, 0)),
                ParseGuid(SqliteNative.ColumnText(statement, 1)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 2),
                ParseNullableEnum<CommandSource>(SqliteNative.ColumnText(statement, 3)),
                SqliteNative.ColumnText(statement, 4), ParseNullableGuid(SqliteNative.ColumnText(statement, 5)),
                ParseNullableGuid(SqliteNative.ColumnText(statement, 6)),
                (CommandDisposition)SqliteNative.ColumnInt64(statement, 7), SqliteNative.ColumnText(statement, 8));
        });

    private static CommandAuditFact? ReadFact(SQLitePCL.sqlite3 database, Guid attemptId, int aggregateSequence,
        StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, @"SELECT EventId, AttemptId, CorrelationId, RuntimeEpoch,
            OccurredAtUtc, AuthenticatedHumanPrincipalId, CommandKind, Source, ClaimedPrincipalId, ClaimedSessionId, ClaimedStepUpGrantId,
            Phase, Disposition, ReasonCode FROM command_facts WHERE AttemptId=? AND AggregateSequence=? LIMIT 1;",
            deadline, statement =>
        {
            SqliteNative.BindGuid(database, statement, 1, attemptId);
            SqliteNative.BindInt(database, statement, 2, aggregateSequence);
            if (SqliteNative.Step(database, statement, deadline) != SQLitePCL.raw.SQLITE_ROW) return null;
            var authenticatedPrincipal = SqliteNative.ColumnText(statement, 5);
            return new CommandAuditFact(ParseGuid(SqliteNative.ColumnText(statement, 0)),
                ParseGuid(SqliteNative.ColumnText(statement, 1)), ParseGuid(SqliteNative.ColumnText(statement, 2)),
                ParseGuid(SqliteNative.ColumnText(statement, 3)), ParseTime(SqliteNative.ColumnText(statement, 4)),
                (AuditedCommandKind)SqliteNative.ColumnInt64(statement, 6),
                ParseNullableEnum<CommandSource>(SqliteNative.ColumnText(statement, 7)), SqliteNative.ColumnText(statement, 8),
                ParseNullableGuid(SqliteNative.ColumnText(statement, 9)), ParseNullableGuid(SqliteNative.ColumnText(statement, 10)),
                (CommandAuditPhase)SqliteNative.ColumnInt64(statement, 11),
                ParseNullableEnum<CommandDisposition>(SqliteNative.ColumnText(statement, 12)),
                SqliteNative.ColumnText(statement, 13)!, authenticatedPrincipal);
        });

    private static HashSet<string> ReadSchemaObjects(SQLitePCL.sqlite3 database, StoreDeadline deadline,
        bool includeInternalObjects = false) =>
        SqliteNative.WithStatement(database, includeInternalObjects
            ? "SELECT type || ':' || name FROM sqlite_master ORDER BY type, name;"
            : "SELECT type || ':' || name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;",
            deadline, statement =>
        {
            var objects = new HashSet<string>(StringComparer.Ordinal);
            while (SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW)
                objects.Add(SqliteNative.ColumnText(statement, 0)!);
            return objects;
        });

    private bool ValidateSchemaShape(SQLitePCL.sqlite3 database, StoreDeadline deadline)
    {
        using var canonical = SqliteNative.Open(":memory:", readOnly: false);
        var canonicalDatabase = canonical.Handle!;
        SqliteNative.Execute(canonicalDatabase, SchemaSql, deadline);
        if (_policy is not null) SqliteNative.Execute(canonicalDatabase, AuditChainDatabase.SchemaSqlFor(SchemaVersion), deadline);
        if (_options.LocalIdentity is not null) SqliteNative.Execute(canonicalDatabase, IdentitySchemaSql, deadline);
        if (SchemaVersion >= 7) SqliteNative.Execute(canonicalDatabase, AlarmSchemaSql, deadline);
        if (AlgorithmArchiveEnabled) SqliteNative.Execute(canonicalDatabase, AlgorithmResultSchemaSql, deadline);
        if (RecipeDraftEnabled) SqliteNative.Execute(canonicalDatabase, RecipeDraftSchemaSql, deadline);
        if (CameraSetupEnabled) SqliteNative.Execute(canonicalDatabase, CameraSetupSchemaSql, deadline);
        if (CameraRecoveryEnabled) SqliteNative.Execute(canonicalDatabase, CameraRecoverySchemaSql, deadline);
        if (CameraNetworkEnabled) SqliteNative.Execute(canonicalDatabase, CameraNetworkSchemaSql, deadline);
        if (ImagingSetupEnabled) SqliteNative.Execute(canonicalDatabase, ImagingSetupSchemaSql, deadline);
        if (CalibrationSessionsEnabled) SqliteNative.Execute(canonicalDatabase, CalibrationSessionSchemaSql, deadline);
        if (CalibrationGovernanceEnabled) SqliteNative.Execute(canonicalDatabase, CalibrationGovernanceSchemaSql, deadline);
        var actualDefinitions = ReadSchemaDefinitions(database, deadline);
        var expectedDefinitions = ReadSchemaDefinitions(canonicalDatabase, deadline);
        if (actualDefinitions.Count != expectedDefinitions.Count) return false;
        foreach (var pair in expectedDefinitions)
        {
            if (!actualDefinitions.TryGetValue(pair.Key, out var actual) ||
                !string.Equals(NormalizeSql(actual), NormalizeSql(pair.Value), StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static Dictionary<string, string> ReadSchemaDefinitions(SQLitePCL.sqlite3 database, StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, @"SELECT type || ':' || name, sql FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;", deadline, statement =>
        {
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            while (SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW)
            {
                var name = SqliteNative.ColumnText(statement, 0);
                var sql = SqliteNative.ColumnText(statement, 1);
                if (name is null || sql is null) throw new InvalidOperationException("StoreSchemaMismatch");
                definitions[name] = sql;
            }

            return definitions;
        });

    private static string NormalizeSql(string sql) => string.Join(" ",
        sql.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static int ReadUserVersion(SQLitePCL.sqlite3 database, StoreDeadline deadline) =>
        SqliteNative.WithStatement(database, "PRAGMA user_version;", deadline, statement =>
            SqliteNative.Step(database, statement, deadline) == SQLitePCL.raw.SQLITE_ROW
                ? checked((int)SqliteNative.ColumnInt64(statement, 0)) : 0);

    private static void Rollback(SQLitePCL.sqlite3 database)
    {
        try { SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK;"); }
        catch { }
    }

    private static void BindGuid(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, int index, Guid? value)
    {
        if (value is null) SQLitePCL.raw.sqlite3_bind_null(statement, index);
        else SqliteNative.BindGuid(database, statement, index, value.Value);
    }

    private static void BindEnum<T>(SQLitePCL.sqlite3 database, SQLitePCL.sqlite3_stmt statement, int index, T? value)
        where T : struct, Enum
    {
        if (value is null) SQLitePCL.raw.sqlite3_bind_null(statement, index);
        else SqliteNative.BindInt(database, statement, index, Convert.ToInt32(value.Value, CultureInfo.InvariantCulture));
    }

    private static Guid ParseGuid(string? value) => Guid.TryParse(value, out var parsed)
        ? parsed : throw new InvalidOperationException("TraceStoreCorrupt");

    private static Guid? ParseNullableGuid(string? value) => string.IsNullOrEmpty(value) ? null : ParseGuid(value);

    private static T ParseEnum<T>(string? value) where T : struct, Enum =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
        Enum.IsDefined(typeof(T), number) ? (T)Enum.ToObject(typeof(T), number) : throw new InvalidOperationException("TraceStoreCorrupt");

    private static T? ParseNullableEnum<T>(string? value) where T : struct, Enum =>
        string.IsNullOrEmpty(value) ? null : ParseEnum<T>(value);

    private static string FormatTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string? value) => DateTimeOffset.TryParse(value,
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
        ? parsed : throw new InvalidOperationException("TraceStoreCorrupt");

    private readonly record struct AttemptContext(Guid CorrelationId, Guid RuntimeEpoch, AuditedCommandKind CommandKind,
        CommandSource? Source, string? ClaimedPrincipalId, Guid? ClaimedSessionId, Guid? ClaimedStepUpGrantId,
        CommandDisposition OutcomeDisposition, string? AuthenticatedHumanPrincipalId)
    {
        public bool Matches(CommandAuditFact fact) => CorrelationId == fact.CorrelationId && RuntimeEpoch == fact.RuntimeEpoch &&
            CommandKind == fact.CommandKind && Source == fact.Source && ClaimedPrincipalId == fact.ClaimedPrincipalId &&
            ClaimedSessionId == fact.ClaimedSessionId && ClaimedStepUpGrantId == fact.ClaimedStepUpGrantId &&
            AuthenticatedHumanPrincipalId == fact.AuthenticatedHumanPrincipalId;
    }

    private sealed record WriteRequest(CommandAuditFact? Fact, StoreDeadline Deadline, AuditAnchorReceipt? Receipt = null,
        IdentityWork? Identity = null, AlarmObservationWork? AlarmObservation = null,
        AlgorithmResultArchiveDocument? AlgorithmResult = null, RecipeDraftWork? RecipeDraft = null,
        CameraRecoveryTerminalWork? RecoveryTerminal = null,
        CameraNetworkAdmissionWork? NetworkAdmission = null,
        CameraNetworkTerminalWork? NetworkTerminal = null,
        CameraNetworkRejectedWork? NetworkRejected = null,
        CalibrationEventWork? CalibrationEvent = null)
    {
        public TaskCompletionSource<StoreWriteResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed record VerifiedSqliteProfile(string JournalMode, long Synchronous,
        long ForeignKeys, long WalAutoCheckpoint);

    private string SchemaSql
    {
        get
        {
            var sql = _policy is null
                ? LegacySchemaSql
                : LegacySchemaSql.Replace("CHECK(CommandKind IN (0,1,2))",
                    "CHECK(CommandKind IN (0,1,2,3,4,5,6))", StringComparison.Ordinal);
            if (SchemaVersion >= 5)
            {
                sql = sql.Replace("CHECK(CommandKind IN (0,1,2,3,4,5,6))",
                    SchemaVersion >= 15 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28))" :
                    SchemaVersion >= 14 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24))" :
                    SchemaVersion >= 13 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19))" :
                    SchemaVersion >= 12 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18))" :
                    SchemaVersion >= 11 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17))" :
                    SchemaVersion >= 10 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16))" :
                    SchemaVersion >= 9 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13,14))" :
                    SchemaVersion >= 7 ? "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11,12,13))" :
                        "CHECK(CommandKind IN (0,1,2,3,4,5,6,7,8,9,10,11))", StringComparison.Ordinal)
                    .Replace("AuthenticatedHumanPrincipalId TEXT NULL CHECK(AuthenticatedHumanPrincipalId IS NULL)",
                        "AuthenticatedHumanPrincipalId TEXT NULL CHECK(AuthenticatedHumanPrincipalId IS NULL OR length(AuthenticatedHumanPrincipalId)=36)",
                        StringComparison.Ordinal);
            }

            return sql;
        }
    }

    private const string LegacySchemaSql = @"
        CREATE TABLE command_attempts(
            AttemptId TEXT NOT NULL PRIMARY KEY,
            CorrelationId TEXT NOT NULL,
            RuntimeEpoch TEXT NOT NULL,
            CommandKind INTEGER NOT NULL CHECK(CommandKind IN (0,1,2)),
            Source INTEGER NULL CHECK(Source IS NULL OR Source IN (0,1)),
            ClaimedPrincipalId TEXT NULL,
            ClaimedSessionId TEXT NULL,
            ClaimedStepUpGrantId TEXT NULL,
            OutcomeDisposition INTEGER NOT NULL CHECK(OutcomeDisposition IN (0,1)),
            OutcomeReasonCode TEXT NOT NULL,
            OutcomeEventId TEXT NOT NULL UNIQUE,
            OutcomeOccurredAtUtc TEXT NOT NULL,
            SystemPrincipalId TEXT NOT NULL CHECK(SystemPrincipalId='SharpInspect.Runtime'),
            AuthenticatedHumanPrincipalId TEXT NULL CHECK(AuthenticatedHumanPrincipalId IS NULL)
        );
        CREATE TABLE command_facts(
            Position INTEGER NOT NULL PRIMARY KEY,
            EventId TEXT NOT NULL UNIQUE,
            AttemptId TEXT NOT NULL,
            CorrelationId TEXT NOT NULL,
            RuntimeEpoch TEXT NOT NULL,
            EventVersion INTEGER NOT NULL CHECK(EventVersion=1),
            AggregateSequence INTEGER NOT NULL CHECK(AggregateSequence IN (1,2)),
            OccurredAtUtc TEXT NOT NULL,
            SystemPrincipalId TEXT NOT NULL CHECK(SystemPrincipalId='SharpInspect.Runtime'),
            AuthenticatedHumanPrincipalId TEXT NULL CHECK(AuthenticatedHumanPrincipalId IS NULL),
            CommandKind INTEGER NOT NULL CHECK(CommandKind IN (0,1,2)),
            Source INTEGER NULL CHECK(Source IS NULL OR Source IN (0,1)),
            ClaimedPrincipalId TEXT NULL,
            ClaimedSessionId TEXT NULL,
            ClaimedStepUpGrantId TEXT NULL,
            Phase INTEGER NOT NULL CHECK(Phase IN (0,1,2)),
            Disposition INTEGER NULL CHECK(Disposition IS NULL OR Disposition IN (0,1)),
            ReasonCode TEXT NOT NULL,
            FOREIGN KEY(AttemptId) REFERENCES command_attempts(AttemptId),
            UNIQUE(AttemptId, AggregateSequence),
            CHECK((AggregateSequence=1 AND Phase=0 AND Disposition IS NOT NULL) OR
                  (AggregateSequence=2 AND Phase IN (1,2) AND Disposition IS NULL))
        );
        CREATE INDEX ix_command_attempts_correlation ON command_attempts(CorrelationId);
        CREATE INDEX ix_command_facts_correlation ON command_facts(CorrelationId, Position);
        CREATE INDEX ix_command_facts_principal ON command_facts(ClaimedPrincipalId, Position);
        CREATE TRIGGER command_attempts_immutable_update BEFORE UPDATE ON command_attempts BEGIN
            SELECT RAISE(ABORT, 'ImmutableCommandAttempt');
        END;
        CREATE TRIGGER command_attempts_immutable_delete BEFORE DELETE ON command_attempts BEGIN
            SELECT RAISE(ABORT, 'ImmutableCommandAttempt');
        END;
        CREATE TRIGGER command_facts_immutable_update BEFORE UPDATE ON command_facts BEGIN
            SELECT RAISE(ABORT, 'ImmutableCommandFact');
        END;
        CREATE TRIGGER command_facts_immutable_delete BEFORE DELETE ON command_facts BEGIN
            SELECT RAISE(ABORT, 'ImmutableCommandFact');
        END;
        CREATE TRIGGER command_facts_require_accepted_outcome BEFORE INSERT ON command_facts
        WHEN NEW.AggregateSequence=2 AND NOT EXISTS(
            SELECT 1 FROM command_facts WHERE AttemptId=NEW.AttemptId AND AggregateSequence=1
              AND Phase=0 AND Disposition=0)
        BEGIN
            SELECT RAISE(ABORT, 'LifecycleRequiresAcceptedOutcome');
        END;
        PRAGMA user_version=1;
        ";
}
