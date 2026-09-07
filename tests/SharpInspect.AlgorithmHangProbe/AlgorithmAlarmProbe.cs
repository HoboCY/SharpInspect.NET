using System.Diagnostics;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.AlgorithmHangProbe;

/// <summary>
/// Child-process-only alarm integration probe. The process-wide guard is deliberately
/// latched once, so this helper must never be called from the test runner process.
/// </summary>
public static class AlgorithmAlarmProbe
{
    private const string HungCode = "AlgorithmHung";
    private const string HungSource = "Runtime.AlgorithmExecution";

    private enum HungMapping
    {
        NoPolicy,
        Missing,
        WrongSource,
        NotLatched,
        NotificationOnly,
        Valid
    }

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
            return Write(false, "WindowsMachineAuditKeyRequired");

        var root = Path.Combine(Path.GetTempPath(), "SharpInspect.AlgorithmAlarmProbe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stage = "start";
        var missingMappingBlocked = false;
        var wrongSourceBlocked = false;
        var nonLatchedBlocked = false;
        var notificationOnlyBlocked = false;
        var noPolicyBlocked = false;
        var signedHungAlarm = false;
        var observedAtBound = false;
        var authorizedAcknowledge = false;
        var resetRejected = false;
        string? resetReasonCode = null;
        var defaultRuntimeBlocked = false;
        try
        {
            stage = "missing-mapping";
            await using (var missing = await AlarmFixture.OpenAsync(root, HungMapping.Missing,
                registerExecution: true, withAdministrator: false).ConfigureAwait(false))
            {
                var snapshot = await WaitForSnapshotAsync(missing.Runtime,
                    state => state.AlarmState is { Available: false } &&
                        state.AdmissionBlockers.Contains("AlarmAuthorityUnavailable"))
                    .ConfigureAwait(false);
                if (snapshot.Ready || !snapshot.AdmissionBlockers.Contains("AlarmAuthorityUnavailable") ||
                    snapshot.AlarmState?.Available != false)
                    return Write(false, "MissingAlgorithmHungMappingWasAdmitted");
                missingMappingBlocked = true;
            }

            stage = "wrong-source-mapping";
            await using (var wrongSource = await AlarmFixture.OpenAsync(root, HungMapping.WrongSource,
                registerExecution: true, withAdministrator: false).ConfigureAwait(false))
            {
                var snapshot = await WaitForSnapshotAsync(wrongSource.Runtime,
                    state => state.AlarmState is { Available: false } &&
                        state.AdmissionBlockers.Contains("AlarmAuthorityUnavailable"))
                    .ConfigureAwait(false);
                if (snapshot.Ready || snapshot.AlarmState?.Available != false)
                    return Write(false, "WrongAlgorithmHungSourceWasAdmitted");
                wrongSourceBlocked = true;
            }

            stage = "non-latched-mapping";
            await using (var nonLatched = await AlarmFixture.OpenAsync(root, HungMapping.NotLatched,
                registerExecution: true, withAdministrator: false).ConfigureAwait(false))
            {
                var snapshot = await WaitForSnapshotAsync(nonLatched.Runtime,
                    state => state.AlarmState is { Available: false } &&
                        state.AdmissionBlockers.Contains("AlarmAuthorityUnavailable"))
                    .ConfigureAwait(false);
                if (snapshot.Ready || snapshot.AlarmState?.Available != false)
                    return Write(false, "NonLatchedAlgorithmHungRuleWasAdmitted");
                nonLatchedBlocked = true;
            }

            stage = "notification-only-mapping";
            await using (var notificationOnly = await AlarmFixture.OpenAsync(root,
                HungMapping.NotificationOnly, registerExecution: true, withAdministrator: false)
                .ConfigureAwait(false))
            {
                var snapshot = await WaitForSnapshotAsync(notificationOnly.Runtime,
                    state => state.AlarmState is { Available: false } &&
                        state.AdmissionBlockers.Contains("AlarmAuthorityUnavailable"))
                    .ConfigureAwait(false);
                if (snapshot.Ready || snapshot.AlarmState?.Available != false)
                    return Write(false, "NotificationOnlyAlgorithmHungRuleWasAdmitted");
                notificationOnlyBlocked = true;
            }

            stage = "no-policy";
            await using (var noPolicy = await AlarmFixture.OpenAsync(root, HungMapping.NoPolicy,
                registerExecution: true, withAdministrator: false).ConfigureAwait(false))
            {
                var snapshot = await WaitForSnapshotAsync(noPolicy.Runtime,
                    state => state.AlarmState is { Available: false } &&
                        state.AdmissionBlockers.Contains("AlarmAuthorityUnavailable"))
                    .ConfigureAwait(false);
                if (snapshot.Ready || snapshot.AlarmState?.Available != false)
                    return Write(false, "MissingAlgorithmAlarmPolicyWasAdmitted");
                noPolicyBlocked = true;
            }

            stage = "configured-mapping";
            await using (var configured = await AlarmFixture.OpenAsync(root, HungMapping.Valid,
                registerExecution: true, withAdministrator: true).ConfigureAwait(false))
            {
                var initial = await WaitForSnapshotAsync(configured.Runtime,
                    state => state.AlarmState is not null).ConfigureAwait(false);
                if (initial.AlarmState is null || !initial.AlarmState.Available)
                    return Write(false, "ConfiguredAlarmAuthorityUnavailable");

                var guard = AlgorithmExecutionGuard.CurrentProcess;
                stage = "guard-latch";
                var executionPolicy = new AlgorithmExecutionPolicy("Probe.Execution", "v1",
                    TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(1));
                var request = new AlgorithmExecutionRequest(
                    new RecipeReference("Probe.Recipe", "v1", new string('A', 64)),
                    TimeSpan.FromMilliseconds(1));
                if (!executionPolicy.TryBind(request, out var timing, out _))
                    return Write(false, "TimingBindingFailed");

                var correlation = new ExecutionCorrelationId(ExecutionKind.Manual, Guid.NewGuid());
                guard.RegisterGrace(correlation, Guid.NewGuid(), Guid.NewGuid(), ExecutionStatus.Timeout,
                    timing!, Stopwatch.GetTimestamp() - Stopwatch.Frequency);
                if (!guard.IsHung)
                    return Write(false, "GuardDidNotLatch");

                var alarmSnapshot = await WaitForSnapshotAsync(configured.Runtime,
                    state => state.AdmissionBlockers.Contains("AlgorithmHung") &&
                        state.AlarmState?.Instances.Any(instance =>
                            instance.Code == HungCode && instance.Lifecycle != AlarmLifecycle.Cleared) == true)
                    .ConfigureAwait(false);
                if (alarmSnapshot.Ready || alarmSnapshot.ArmState != ProductionArmState.Disarmed)
                    return Write(false, "HungRuntimeWasAdmitted");

                var history = new SqliteAlarmHistoryQuery(configured.Options);
                var page = await history.QueryAsync(new AlarmHistoryFilter(code: HungCode, pageSize: 100))
                    .ConfigureAwait(false);
                var hungRecord = page.Records.FirstOrDefault(record => record.Code == HungCode &&
                    record.Source == HungSource);
                if (!page.Available || hungRecord is null || hungRecord.ObservedAtUtc == default ||
                    hungRecord.Instance is null || hungRecord.Instance.Source == HungSource &&
                    hungRecord.Instance.SourceRuntimeEpoch != configured.RuntimeEpoch)
                    return Write(false, "SignedHungAlarmMissing");
                signedHungAlarm = true;
                observedAtBound = hungRecord.ObservedAtUtc == guard.Snapshot!.ObservedAtUtc;
                if (!observedAtBound)
                    return Write(false, "HungObservationTimeWasRewritten");

                var actor = configured.Administrator ?? throw new InvalidOperationException("ProbeAdministratorMissing");
                var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                    actor.PrincipalId.ToString("D"), actor.SessionId);
                var acknowledge = await configured.Runtime.SubmitAsync(new AcknowledgeAlarmCommand(
                    Guid.NewGuid(), invocation, hungRecord.Instance.InstanceId)).ConfigureAwait(false);
                await configured.WaitForVerifiedAsync().ConfigureAwait(false);
                var acknowledged = await WaitForSnapshotAsync(configured.Runtime,
                    state => state.AlarmState?.Instances.Any(instance =>
                        instance.InstanceId == hungRecord.Instance!.InstanceId && instance.Acknowledged) == true)
                    .ConfigureAwait(false);
                if (acknowledge.Disposition != CommandDisposition.Accepted || acknowledged.Ready ||
                    !AlgorithmExecutionGuard.CurrentProcess.IsHung)
                    return Write(false, "AuthorizedAcknowledgeClearedHungGuard");
                authorizedAcknowledge = true;

                var resetCorrelation = Guid.NewGuid();
                var stepUp = await configured.Authorization!.ReauthenticateAsync(new StepUpRequest(
                    resetCorrelation, invocation,
                    new StepUpBinding(Permission.ResetAlarm, resetCorrelation,
                        hungRecord.Instance.InstanceId.ToString("D"), AuditedCommandKind.ResetAlarm),
                    actor.Password)).ConfigureAwait(false);
                if (!stepUp.Succeeded || !stepUp.GrantId.HasValue)
                    return Write(false, "ResetStepUpFailed");
                await configured.WaitForVerifiedAsync().ConfigureAwait(false);
                var reset = await configured.Runtime.SubmitAsync(new ResetAlarmCommand(
                    resetCorrelation, invocation with { StepUpGrantId = stepUp.GrantId },
                    hungRecord.Instance.InstanceId)).ConfigureAwait(false);
                await configured.WaitForVerifiedAsync().ConfigureAwait(false);
                var afterCommands = await configured.Runtime.GetSnapshotAsync().ConfigureAwait(false);
                resetReasonCode = reset.ReasonCode;
                if (reset.Disposition == CommandDisposition.Accepted || !guard.IsHung || afterCommands.Ready ||
                    !afterCommands.AdmissionBlockers.Contains("AlgorithmHung") ||
                    !string.Equals(reset.ReasonCode, "AlarmSourceNotHealthy", StringComparison.Ordinal))
                    return Write(false, "AlarmCommandClearedHungGuard");
                resetRejected = true;
            }

            stage = "default-runtime";
            await using (var noExecution = await AlarmFixture.OpenAsync(root, HungMapping.NoPolicy,
                registerExecution: false, withAdministrator: false).ConfigureAwait(false))
            {
                var snapshot = await WaitForSnapshotAsync(noExecution.Runtime,
                    state => state.AdmissionBlockers.Contains("AlgorithmHung")).ConfigureAwait(false);
                if (snapshot.Ready || snapshot.ArmState != ProductionArmState.Disarmed ||
                    !snapshot.AdmissionBlockers.Contains("AlgorithmHung") ||
                    snapshot.AdmissionBlockers.Contains("AlgorithmExecutionAlarmMappingUnavailable"))
                    return Write(false, "DefaultRuntimeDidNotObserveGuard");
                defaultRuntimeBlocked = true;
            }

            return Write(true, "Verified", new
            {
                missingMappingBlocked,
                wrongSourceBlocked,
                nonLatchedBlocked,
                notificationOnlyBlocked,
                noPolicyBlocked,
                signedHungAlarm,
                observedAtBound,
                authorizedAcknowledge,
                resetRejected,
                resetReasonCode,
                defaultRuntimeBlocked,
                guardHung = AlgorithmExecutionGuard.CurrentProcess.IsHung
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Keep the probe contract free of exception details, paths and key material.
            return Write(false, exception switch
            {
                TimeoutException => "ProbeDeadlineExceeded",
                InvalidOperationException invalid when invalid.Message.StartsWith("ProbeStoreUnavailable:", StringComparison.Ordinal)
                    => invalid.Message,
                InvalidOperationException => "ProbeInvalidOperation:" + stage,
                ArgumentException => "ProbeArgumentInvalid:" + stage,
                _ => "ProbeFailure:" + stage
            });
        }
        finally
        {
            try
            {
                var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                    "SharpInspect.AlgorithmAlarmProbe"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(IStationRuntime runtime,
        Func<StationStateSnapshot, bool> predicate)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 8;
        StationStateSnapshot last = await runtime.GetSnapshotAsync().ConfigureAwait(false);
        while (!predicate(last))
        {
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException();
            await Task.Delay(20).ConfigureAwait(false);
            last = await runtime.GetSnapshotAsync().ConfigureAwait(false);
        }

        return last;
    }

    private static int Write(bool passed, string reason, object? evidence = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            eventName = "alarm-governance",
            passed,
            reason,
            evidence
        }));
        Console.Out.Flush();
        return passed ? 0 : 1;
    }

    private sealed class AlarmFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly SqliteCommandStore _store;
        private readonly StationRuntime _runtime;
        private readonly InteractiveSessionService? _sessions;
        private readonly LocalAuthorizationService? _authorization;

        private AlarmFixture(string directory, ProductionStoreOptions options,
            SqliteCommandStore store, StationRuntime runtime,
            InteractiveSessionService? sessions, LocalAuthorizationService? authorization,
            AdministratorIdentity? administrator)
        {
            _directory = directory;
            Options = options;
            _store = store;
            _runtime = runtime;
            _sessions = sessions;
            _authorization = authorization;
            Administrator = administrator;
        }

        public ProductionStoreOptions Options { get; }
        public StationRuntime Runtime => _runtime;
        internal LocalAuthorizationService? Authorization => _authorization;
        internal AdministratorIdentity? Administrator { get; }
        public Guid RuntimeEpoch => _runtime.GetSnapshotAsync().AsTask().GetAwaiter().GetResult().RuntimeEpoch;

        public async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(_store).ConfigureAwait(false);

        public static async Task<AlarmFixture> OpenAsync(string root, HungMapping mapping,
            bool registerExecution, bool withAdministrator)
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V109AlarmStation";
            var alarmPolicy = CreateAlarmPolicy(mapping);
            var integrity = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Probe." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("probe-blocklist", "v1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "alarm.sqlite"))
            {
                AuditIntegrityPolicy = integrity,
                LocalIdentity = identity,
                AlarmPolicy = alarmPolicy,
                CommitTimeout = TimeSpan.FromSeconds(2),
                QueryTimeout = TimeSpan.FromSeconds(2),
                QueueCapacity = 16
            };
            var store = new SqliteCommandStore(options);
            InteractiveSessionService? sessions = null;
            LocalAuthorizationService? authorization = null;
            StationRuntime? runtime = null;
            try
            {
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(8))
                    .ConfigureAwait(false);
                if (!initialized.Committed)
                    throw new InvalidOperationException("ProbeStoreUnavailable:" + initialized.ReasonCode);
                await WaitForVerifiedAsync(store).ConfigureAwait(false);
                if (withAdministrator)
                {
                    var bootstrapIdentity = new LocalIdentityService(store, identity,
                        new ControlledConsoleAuthority());
                    var tokenResult = await bootstrapIdentity.ProvisionBootstrapTokenAsync()
                        .ConfigureAwait(false);
                    if (!tokenResult.Succeeded || tokenResult.Token is null)
                        throw new InvalidOperationException("ProbeBootstrapTokenFailed");
                    var created = await bootstrapIdentity.CreateFirstAdministratorAsync(
                        new BootstrapAdministratorRequest(station, tokenResult.Token.TakeForDisplay(),
                            "alarm-probe-admin", "Alarm Probe Administrator",
                            "Alarm probe administrator secret 2026!")).ConfigureAwait(false);
                    created.RecoveryKit?.Dispose();
                    if (!created.Succeeded)
                        throw new InvalidOperationException("ProbeAdministratorCreateFailed");
                    await WaitForVerifiedAsync(store).ConfigureAwait(false);
                    sessions = new InteractiveSessionService(bootstrapIdentity,
                        identity.AuthenticationPolicy, bootstrapIdentity.PersistSessionEventAsync);
                    authorization = new LocalAuthorizationService(store, identity, bootstrapIdentity, sessions);
                }

                var executionOptions = registerExecution
                    ? new AlgorithmExecutionOptions(new AlgorithmExecutionPolicy("Probe.Execution", "v1",
                        TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1)),
                        TimeSpan.FromSeconds(2))
                    : null;
                runtime = new StationRuntime(store, TimeSpan.FromMilliseconds(20), sessions, authorization,
                    null, AlgorithmExecutionGuard.CurrentProcess, executionOptions);
                AdministratorIdentity? administrator = null;
                if (sessions is not null)
                {
                    var signIn = await sessions.SignInAsync(new PasswordSignInRequest(
                        "alarm-probe-admin", "Alarm probe administrator secret 2026!"))
                        .ConfigureAwait(false);
                    if (!signIn.Succeeded || signIn.Identity is null || !signIn.Session.SessionId.HasValue)
                        throw new InvalidOperationException("ProbeAdministratorSignInFailed");
                    administrator = new AdministratorIdentity(signIn.Identity.PrincipalId,
                        signIn.Session.SessionId.Value, "Alarm probe administrator secret 2026!");
                    await WaitForVerifiedAsync(store).ConfigureAwait(false);
                }

                return new AlarmFixture(directory, options, store, runtime, sessions, authorization,
                    administrator);
            }
            catch
            {
                if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
                authorization?.Dispose();
                if (sessions is not null) await sessions.DisposeAsync().ConfigureAwait(false);
                await store.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _runtime.DisposeAsync().ConfigureAwait(false);
            _authorization?.Dispose();
            if (_sessions is not null) await _sessions.DisposeAsync().ConfigureAwait(false);
            await _store.DisposeAsync().ConfigureAwait(false);
            DeleteOwnedDirectory(_directory, Path.GetDirectoryName(_directory)!);
        }

        private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 8;
            while (store.Integrity?.State == AuditIntegrityState.Verifying)
            {
                if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException();
                await Task.Delay(20).ConfigureAwait(false);
            }

            if (store.Integrity?.State != AuditIntegrityState.Verified)
                throw new InvalidOperationException("ProbeAuditUnavailable");
        }

        private static AlarmPolicy? CreateAlarmPolicy(HungMapping mapping)
        {
            if (mapping == HungMapping.NoPolicy) return null;
            var rules = new List<AlarmPolicyRule>
            {
                new("StartupRecoveryRequired", "Runtime.StartupRecovery", AlarmSeverity.Warning,
                    ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null, 100,
                    AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery)
            };
            switch (mapping)
            {
                case HungMapping.Valid:
                    rules.Add(new AlarmPolicyRule(HungCode, HungSource, AlarmSeverity.Critical,
                        ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null, 110));
                    break;
                case HungMapping.WrongSource:
                    rules.Add(new AlarmPolicyRule(HungCode, "Runtime.Other", AlarmSeverity.Critical,
                        ProductionImpact.BlockNewTriggers, true, AlarmNotification.UntilCleared, null, 110));
                    break;
                case HungMapping.NotLatched:
                    rules.Add(new AlarmPolicyRule(HungCode, HungSource, AlarmSeverity.Critical,
                        ProductionImpact.BlockNewTriggers, false, AlarmNotification.UntilCleared, null, 110));
                    break;
                case HungMapping.NotificationOnly:
                    rules.Add(new AlarmPolicyRule(HungCode, HungSource, AlarmSeverity.Critical,
                        ProductionImpact.None, false, AlarmNotification.UntilAcknowledged, null, 110));
                    break;
            }
            rules.Add(new AlarmPolicyRule("Notice", "Device.Status", AlarmSeverity.Critical,
                ProductionImpact.None, false, AlarmNotification.UntilAcknowledged, null));

            return new AlarmPolicy("probe-alarm-policy", "v1", rules,
                TimeSpan.FromSeconds(5), maximumActiveInstances: 16, maximumPlcEntries: 1);
        }

        private static void DeleteOwnedDirectory(string directory, string root)
        {
            try
            {
                var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var targetPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (targetPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }

        internal sealed record AdministratorIdentity(Guid PrincipalId, Guid SessionId, string Password);

        private sealed class ControlledConsoleAuthority : IPhysicalConsoleAuthority
        {
            public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-SHARPINSPECT-PROBE");
        }
    }
}
