using System.Globalization;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Alarms;
using SharpInspect.Runtime.Diagnostics;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// T58 storage slice: the schema-40 diagnostic-operation ledger, its exact 39-to-40 migration,
/// the four-fact audit reserve, immutable transition replay and the restart interruption proof.
/// </summary>
public sealed partial class ProductionOutboxMigrationTests
{
    [Fact, Trait("VerificationId", "V158_S01")]
    public void V158_S01_OptionBindingRequiresRetentionTracePolicyIdentityAndLogging()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S01");
        try
        {
            var logging = Logging(directory);
            var policy = SupportPolicy(logging);
            var option = new DiagnosticSupportStoreOptions(policy, logging.SupportBundles!.BindingHash);
            Assert.Matches("^[0-9A-F]{64}$", option.BindingHash);
            Assert.Equal(policy.LoggingPolicyHash, logging.Policy.ContentHash);
            Assert.Throws<ArgumentException>(() => new DiagnosticSupportStoreOptions(policy, "not-a-hash"));
            Assert.NotEqual(option.BindingHash,
                new DiagnosticSupportStoreOptions(policy, new string('B', 64)).BindingHash);
            Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticSupportStoreOptions(policy,
                new string('A', 64)) { MaximumTotalBytes = 4096 }.BindingHash);
            Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticSupportStoreOptions(policy,
                new string('A', 64)) { MaximumOperations = 1 }.BindingHash);

            var source = WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S01", 35, TracePolicies("V158.S01")), directory));
            Assert.Throws<ArgumentException>(() => option.ValidateProfile(source));
            var target = DiagnosticTarget(source, logging: logging);
            option.ValidateProfile(target);
            var configuration = SqliteCommandStore.DiagnosticSupportConfigurationFor(target);
            Assert.Equal(option.BindingHash, configuration.OptionsHash);
            Assert.Equal(logging.Policy.ContentHash, configuration.LoggingPolicyHash);
            Assert.Equal(SqliteCommandStore.RetentionConfigurationFor(target).BindingHash,
                configuration.RetentionConfigurationHash);
            var rebound = DiagnosticTarget(source, new string('B', 64), logging);
            Assert.Throws<ArgumentException>(() => SqliteCommandStore.DiagnosticSupportConfigurationFor(rebound));
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V158_S02")]
    public async Task V158_S02_Schema39To40MigrationPreservesHistoryAndRefusesThePreviousReader()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S02");
        try
        {
            var trace = TracePolicies("V158.S02");
            var source = WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S02", 35, trace), directory));
            await using (var writer = new SqliteCommandStore(source))
                Assert.True((await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(30))).Committed);
            var before = Fingerprint(source);
            Assert.Equal(39, before.SchemaVersion);
            var target = DiagnosticTarget(source);
            var opened = await OpenAsync(target);
            Assert.True(opened.Available, opened.Status.ReasonCode);
            await using (var session = opened.Session!)
            {
                var completed = await StoreStartupMaintenanceTests.Finish(session);
                Assert.True(completed.Completed, completed.ReasonCode);
                Assert.Equal(39, completed.SourceSchemaVersion);
                Assert.Equal(40, completed.TargetSchemaVersion);
                Assert.False(completed.Ready);
            }
            using (var connection = SqliteNative.Open(source.DatabasePath, readOnly: true))
                Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, before.Tables,
                    Budget, Deadline()));
            var last = StoreMigrationJournal.ReadChain(
                StoreMigrationJournalGuard.JournalPath(source.DatabasePath), 4L * 1024 * 1024)[^1].Data;
            Assert.Equal(StoreMigrationJournal.DiagnosticSupportPlanId, last.PlanId);
            Assert.Equal(SqliteCommandStore.DiagnosticSupportConfigurationFor(target).BindingHash,
                last.DiagnosticSupportConfigurationHash);
            Assert.Equal(SqliteCommandStore.RetentionConfigurationFor(target).BindingHash,
                last.StorageRetentionConfigurationHash);
            StoreMigrationJournalGuard.RequireCompletedLineage(last, opened.Status.OperationId, target,
                target.DatabasePath);
            await using (var reopened = new SqliteCommandStore(target))
            {
                var result = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(result.Committed, result.ReasonCode);
                Assert.True(reopened.DiagnosticSupportEnabled);
                await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(reopened);
                var state = await reopened.ReadDiagnosticSupportStateAsync();
                Assert.True(state.Available, state.ReasonCode);
                Assert.Empty(state.Operations);
            }
            Assert.Equal(40L, Scalar(target.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(1L, Scalar(target.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticSupportActivated';"));
            // The schema-39 reader refuses the migrated store without any global version relaxation.
            await using var previous = new SqliteCommandStore(source);
            Assert.False((await previous.Initialization).Committed);
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V158_S03")]
    public async Task V158_S03_ImmutableFactsRefuseMutationAndTheAuditReserveIsExact()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S03");
        try
        {
            var options = DiagnosticTarget(WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S03", 35, TracePolicies("V158.S03")), directory)));
            await using var store = await OpenStoreAsync(options);
            var principal = Guid.NewGuid();
            var session = Guid.NewGuid();
            var grant = Guid.NewGuid();
            var admitted = await AdmitCaptureAsync(store, options, principal, session, grant);
            Assert.Equal(3L, Reserve(options));
            var commandHistory = await new SqliteCommandTraceQuery(options).QueryAsync(new CommandTraceFilter());
            Assert.Contains(commandHistory.Records, command => command.CommandKind == AuditedCommandKind.StartDiagnosticCapture &&
                command.Disposition == CommandDisposition.Accepted);
            using (var connection = SqliteNative.Open(options.DatabasePath, readOnly: true))
            {
                Assert.Equal(1L, AuditChainDatabase.Scalar(connection.Handle!,
                    "SELECT COUNT(*) FROM diagnostic_operation_facts WHERE Phase=1;", Deadline()));
                Assert.Equal(1L, AuditChainDatabase.Scalar(connection.Handle!,
                    "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticOperationEvent';", Deadline()));
            }
            using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                connection.Open();
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE diagnostic_operation_facts SET Phase=3 WHERE Position=1;";
                Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM diagnostic_support_config;";
                Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
            }
            var terminal = await store.AppendDiagnosticOperationTerminalAsync(
                new DiagnosticOperationTerminal(admitted.Payload.OperationId, DiagnosticOperationPhase.Failed,
                    DateTimeOffset.UtcNow, "DiagnosticCaptureFailed", null, null, null, null), Deadline());
            Assert.True(terminal.Committed, terminal.ReasonCode);
            Assert.Equal(0L, Reserve(options));
            var second = await store.AppendDiagnosticOperationTerminalAsync(
                new DiagnosticOperationTerminal(admitted.Payload.OperationId, DiagnosticOperationPhase.Completed,
                    DateTimeOffset.UtcNow, "DiagnosticCaptureCompleted", null, null, null, null), Deadline());
            Assert.False(second.Committed);
            Assert.Contains("Transition", second.ReasonCode);
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V158_S04")]
    public async Task V158_S04_RestartRecordsInterruptedForEveryPreexistingOpenOperation()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S04");
        try
        {
            var options = DiagnosticTarget(WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S04", 35, TracePolicies("V158.S04")), directory)));
            Guid operationId;
            await using (var store = await OpenStoreAsync(options))
            {
                var admitted = await AdmitCaptureAsync(store, options, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
                operationId = admitted.Payload.OperationId;
                Assert.Equal(3L, Reserve(options));
            }
            await using (var restarted = await OpenStoreAsync(options))
            {
                var state = await restarted.ReadDiagnosticSupportStateAsync();
                Assert.True(state.Available, state.ReasonCode);
                var operation = Assert.Single(state.Operations);
                Assert.Equal(operationId, operation.OperationId);
                Assert.Equal(DiagnosticOperationPhase.Interrupted, operation.Phase);
                Assert.Equal("DiagnosticSupportRuntimeRestarted", operation.ReasonCode);
                Assert.Equal(2, operation.AggregateSequence);
                Assert.Equal(0L, Reserve(options));
                Assert.Equal(2L, Scalar(options.DatabasePath,
                    "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticOperationEvent';"));
            }
            await using var resumed = await OpenStoreAsync(options);
            var again = await AdmitCaptureAsync(resumed, options, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(3L, Reserve(options));
            Assert.NotEqual(operationId, again.Payload.OperationId);
        }
        finally { Remove(directory); }
    }

    [Theory, InlineData(false), InlineData(true), Trait("VerificationId", "V158_S05")]
    public async Task V158_S05_CaptureStopAndCompletionStayBoundToOneAcceptedCommandChain(bool restartBeforeSeal)
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S05");
        try
        {
            var options = DiagnosticTarget(WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S05", 35, TracePolicies("V158.S05")), directory)));
            await using var store = await OpenStoreAsync(options);
            var principal = Guid.NewGuid();
            var session = Guid.NewGuid();
            var grant = Guid.NewGuid();
            var admitted = await AdmitCaptureAsync(store, options, principal, session, grant);
            var operationId = admitted.Payload.OperationId;
            var stopTarget = AlgorithmContractValidation.HashParts(new[]
            {
                "diagnostic-support-command-v1", "StopCapture", operationId.ToString("D"),
                admitted.Payload.Reason.ToString()
            });

            grant = Guid.NewGuid(); // Stopping requires its own fresh grant.
            var stopCommand = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), DateTimeOffset.UtcNow, AuditedCommandKind.StopDiagnosticCapture,
                CommandSource.PhysicalConsole, principal.ToString("D"), session, grant,
                CommandAuditPhase.Outcome, CommandDisposition.Accepted, "DiagnosticCaptureStopAuthorized",
                principal.ToString("D"));
            var stopEvent = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.DiagnosticOperationAuthorized,
                DateTimeOffset.UtcNow, options.LocalIdentity!.StationId, principal, null, null, null,
                stopCommand.ReasonCode, SessionId: session, ActorPrincipalId: principal,
                CommandCorrelationId: stopCommand.CorrelationId, StepUpGrantId: grant,
                RequiredPermission: nameof(Permission.StartDiagnosticCapture),
                ActionTargetId: stopTarget, AuthorizationRevision: 1,
                OperationId: operationId, BoundCommandCorrelationId: stopCommand.CorrelationId,
                ActionCommandKind: nameof(AuditedCommandKind.StopDiagnosticCapture));
            var stopResult = await store.UpdateIdentityCommandAsync(stopCommand.CorrelationId,
                (_, _) => new IdentityUpdate(new object(), new[] { stopEvent }, new[] { stopCommand }), CancellationToken.None, Deadline());
            Assert.True(stopResult.Committed, stopResult.ReasonCode);

            if (restartBeforeSeal)
            {
                await store.DisposeAsync();
                await using var restarted = await OpenStoreAsync(options);
                var restartedState = await restarted.ReadDiagnosticSupportStateAsync();
                Assert.True(restartedState.Available, restartedState.ReasonCode);
                var interrupted = Assert.Single(restartedState.Operations);
                Assert.Equal(DiagnosticOperationPhase.Interrupted, interrupted.Phase);
                using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
                var rows = SqliteCommandStore.ReadDiagnosticOperationRows(connection.Handle!,
                    SqliteCommandStore.DiagnosticSupportConfigurationFor(options), Deadline());
                Assert.Equal(stopCommand.EventId, rows[^1].Payload.StopCommand!.EventId);
                Assert.Equal(stopEvent.EventId, rows[^1].Payload.StopAuthorization!.EventId);
                Assert.Null(rows[^1].Payload.ObservedEvents);
                return;
            }

            var sealedResult = await store.AppendDiagnosticOperationSealedAsync(
                new DiagnosticOperationSeal(operationId, DateTimeOffset.UtcNow, "DiagnosticCaptureStopped",
                    null, null, null, null, stopCommand.EventId, stopEvent.EventId)
                    { ObservedEvents = 0, StopReason = admitted.Payload.Reason }, Deadline());
            Assert.True(sealedResult.Committed, sealedResult.ReasonCode);
            Assert.Equal(2L, Reserve(options));
            Assert.Equal(2L, Scalar(options.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='DiagnosticOperationEvent';"));
            var completed = await store.AppendDiagnosticOperationTerminalAsync(
                new DiagnosticOperationTerminal(operationId, DiagnosticOperationPhase.Completed,
                    DateTimeOffset.UtcNow, "DiagnosticCaptureCompleted", null, null, null, null), Deadline());
            Assert.True(completed.Committed, completed.ReasonCode);
            Assert.Equal(0L, Reserve(options));
            var state = await store.ReadDiagnosticSupportStateAsync();
            Assert.True(state.Available, state.ReasonCode);
            Assert.Equal(DiagnosticOperationPhase.Completed, Assert.Single(state.Operations).Phase);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V158_S06")]
    public async Task V158_S06_TheFourFactFootprintNeverConsumesTheFinalAuditCapacity()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S06");
        try
        {
            var options = DiagnosticTarget(WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S06", 35, TracePolicies("V158.S06")), directory)));
            await using var store = await OpenStoreAsync(options);
            using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
            var tight = new AuditIntegrityPolicy("V158S06Tight", "v1", "V158.S06.tight")
            {
                MaximumVerificationEntries = 201,
                BackgroundVerificationEntries = 200,
                CheckpointEveryEntries = 1
            };
            tight.Validate();
            var error = Assert.Throws<InvalidOperationException>(() =>
                AuditChainDatabase.EnsureDiagnosticSupportTransactionCapacity(connection.Handle!, tight, 1000, 3,
                    Deadline()));
            Assert.Equal("DiagnosticSupportAuditCapacityExceeded", error.Message);
            var admitted = await AdmitCaptureAsync(store, options, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(3L, Reserve(options));
            AuditChainDatabase.EnsureDiagnosticSupportTransactionCapacity(connection.Handle!,
                options.AuditIntegrityPolicy!, 1, 3, Deadline());
            // An automatic deadline/event-cap close seals without a stop command and still
            // consumes exactly one reserved terminal fact.
            var seal = await store.AppendDiagnosticOperationSealedAsync(
                new DiagnosticOperationSeal(admitted.Payload.OperationId, admitted.Payload.AdmittedDeadlineUtc!.Value,
                    "DiagnosticCaptureTimeLimit", null, null, null, null) { ObservedEvents = 0 }, Deadline());
            Assert.True(seal.Committed, seal.ReasonCode);
            Assert.Equal(2L, Reserve(options));
            var completed = await store.AppendDiagnosticOperationTerminalAsync(
                new DiagnosticOperationTerminal(admitted.Payload.OperationId, DiagnosticOperationPhase.Completed,
                    DateTimeOffset.UtcNow, "DiagnosticCaptureTimeLimit", null, null, null, null), Deadline());
            Assert.True(completed.Committed, completed.ReasonCode);
            Assert.Equal(0L, Reserve(options));
            var extra = await store.AppendDiagnosticOperationTerminalAsync(
                new DiagnosticOperationTerminal(admitted.Payload.OperationId, DiagnosticOperationPhase.Failed,
                    DateTimeOffset.UtcNow, "DiagnosticCaptureFailed", null, null, null, null), Deadline());
            Assert.False(extra.Committed);
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V158_S07")]
    public async Task V158_S07_FreshSchema40PreservesOutboxActivationAndSignedAlarmReadWrite()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S07");
        try
        {
            var trace = TracePolicies("V158.S07");
            var source = SourceProfile(directory, "V158S07", 35, trace);
            var outbox = OutboxFor(source);
            outbox = new(outbox.Routes, outbox.RecipeLifecycle, outbox.ImageEvidence, outbox.ImageFinalization)
                { ManualRecovery = new() };
            source = Copy(source, trace, outbox);
            var options = DiagnosticTarget(WithRetention(WithReconciliation(source, directory)));
            var alarmPolicy = new AlarmPolicy("V158.S07.Alarms", "1", new[]
            {
                new AlarmPolicyRule("CAMERA_DISCONNECTED", "Camera", AlarmSeverity.Error,
                    ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null)
            }, TimeSpan.FromSeconds(30));
            typeof(ProductionStoreOptions).GetProperty(nameof(ProductionStoreOptions.AlarmPolicy))!
                .SetValue(options, alarmPolicy);
            await using var store = await OpenStoreAsync(options);
            Assert.Equal(40, Scalar(options.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(1, Scalar(options.DatabasePath,
                "SELECT COUNT(*) FROM audit_entries WHERE Kind='ProductionOutboxRecoveryActivated';"));
            Assert.True((await store.ReadDiagnosticSupportStateAsync()).Available);
            var epoch = Guid.NewGuid();
            var alarmState = await store.ReadAlarmStateAsync(epoch);
            Assert.True(alarmState.Available, alarmState.ReasonCode);
            var now = DateTimeOffset.UtcNow;
            var written = await store.UpdateAlarmObservationAsync(epoch, state =>
            {
                var decision = AlarmTransitions.Observe(state, new AlarmObservation(epoch,
                    1, "CAMERA_DISCONNECTED", "Camera", true, now), epoch, now);
                Assert.True(decision.Succeeded, decision.ReasonCode);
                return new AlarmObservationUpdate(decision, decision.Events);
            }, CancellationToken.None);
            Assert.True(written.Committed, written.ReasonCode);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
            var history = await new SqliteAlarmHistoryQuery(options)
                .QueryAsync(new AlarmHistoryFilter(code: "CAMERA_DISCONNECTED"));
            Assert.True(history.Available, history.ReasonCode);
            Assert.Equal(AlarmTransitionKind.Observed, Assert.Single(history.Records).Transition);
            alarmState = await store.ReadAlarmStateAsync(epoch);
            Assert.True(alarmState.Available, alarmState.ReasonCode);
        }
        finally { Remove(directory); }
    }

    [Fact, Trait("VerificationId", "V158_S08")]
    public async Task V158_S08_RestartWriterCannotResignAHistoryTamperedAfterEarlierVerification()
    {
        RequireWindowsMigrationFixture();
        var directory = ImageEvidenceFixture.NewDirectory("V158-S08");
        try
        {
            var options = DiagnosticTarget(WithRetention(WithReconciliation(
                SourceProfile(directory, "V158S08", 35, TracePolicies("V158.S08")), directory)));
            await using var store = await OpenStoreAsync(options);
            await AdmitCaptureAsync(store, options, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                connection.Open();
                var triggers = new List<(string Name, string Sql)>();
                using (var lookup = connection.CreateCommand())
                {
                    lookup.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='audit_entries';";
                    using var reader = lookup.ExecuteReader();
                    while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
                }
                using var mutation = connection.CreateCommand();
                foreach (var trigger in triggers)
                {
                    Assert.Matches("^[A-Za-z0-9_]+$", trigger.Name);
                    mutation.CommandText = "DROP TRIGGER " + trigger.Name + ";";
                    mutation.ExecuteNonQuery();
                }
                mutation.CommandText = "UPDATE audit_entries SET Payload='AAAA' WHERE Kind='DiagnosticOperationEvent';";
                Assert.Equal(1, mutation.ExecuteNonQuery());
                foreach (var trigger in triggers)
                {
                    mutation.CommandText = trigger.Sql;
                    mutation.ExecuteNonQuery();
                }
            }
            // Call the restart transaction itself after the earlier verification. This
            // isolates its own proof obligation from the separate startup preflight.
            using var native = SqliteNative.Open(options.DatabasePath, readOnly: false);
            var restart = typeof(SqliteCommandStore).GetMethod("RecordDiagnosticSupportInterruptions",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                restart.Invoke(store, new object[] { native.Handle!, Deadline() }));
            Assert.Equal(1, Scalar(options.DatabasePath, "SELECT COUNT(*) FROM diagnostic_operation_facts;"));
            Assert.Equal(0, Scalar(options.DatabasePath, "SELECT COUNT(*) FROM diagnostic_operation_facts WHERE Phase=5;"));
        }
        finally { Remove(directory); }
    }

    private static long Reserve(ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        return SqliteCommandStore.ReadDiagnosticSupportAuditReserve(connection.Handle!, Deadline());
    }

    private static async Task<SqliteCommandStore> OpenStoreAsync(ProductionStoreOptions options)
    {
        var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        Assert.True(store.DiagnosticSupportEnabled);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
        return store;
    }

    private static async Task<DiagnosticOperationStoredRow> AdmitCaptureAsync(SqliteCommandStore store,
        ProductionStoreOptions options, Guid principal, Guid session, Guid grant)
    {
        var configuration = SqliteCommandStore.DiagnosticSupportConfigurationFor(options);
        var profile = new DiagnosticCaptureProfile(configuration.LoggingPolicyHash, new[] { "Runtime" },
            DiagnosticLevel.Debug, TimeSpan.FromMinutes(1), 100);
        var operationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var reason = DiagnosticSupportReason.MaintenanceInvestigation;
        const string reasonCode = "DiagnosticCaptureAdmitted";
        var target = AlgorithmContractValidation.HashParts(new[]
        {
            "diagnostic-support-command-v1", "StartCapture", operationId.ToString("D"), reason.ToString(),
            profile.ContentHash
        });
        var mutation = new DiagnosticOperationMutation(operationId, DiagnosticOperationKind.Capture, runtimeEpoch,
            reason, reasonCode, target, DiagnosticCaptureProfileFields.From(profile), null,
            DateTimeOffset.UtcNow.AddMinutes(1), profile.MaximumEvents);
        var command = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), correlationId, runtimeEpoch,
            DateTimeOffset.UtcNow, AuditedCommandKind.StartDiagnosticCapture, CommandSource.PhysicalConsole,
            principal.ToString("D"), session, grant, CommandAuditPhase.Outcome, CommandDisposition.Accepted,
            reasonCode, principal.ToString("D"));
        var result = await store.UpdateIdentityCommandAsync(correlationId, (state, _) =>
        {
            var authorization = new IdentityAuditEvent(Guid.NewGuid(), IdentityEventKind.DiagnosticOperationAuthorized,
                DateTimeOffset.UtcNow, state.StationId, principal, null, null, null, reasonCode,
                SessionId: session, ActorPrincipalId: principal, CommandCorrelationId: correlationId,
                StepUpGrantId: grant, RequiredPermission: nameof(Permission.StartDiagnosticCapture),
                ActionTargetId: target, AuthorizationRevision: 1, OperationId: operationId,
                BoundCommandCorrelationId: correlationId,
                ActionCommandKind: nameof(AuditedCommandKind.StartDiagnosticCapture));
            return new IdentityUpdate(new object(), new[] { authorization }, new[] { command },
                DiagnosticOperation: mutation);
        }, CancellationToken.None, Deadline());
        Assert.True(result.Committed, result.ReasonCode);
        return Assert.IsType<DiagnosticOperationStoredRow>(result.Result);
    }

    private static ProductionStoreOptions DiagnosticTarget(ProductionStoreOptions source,
        string? supportRootHash = null, LoggingDiagnosticsOptions? logging = null)
    {
        logging ??= Logging(Path.GetDirectoryName(source.DatabasePath)!);
        var target = new ProductionStoreOptions
        {
            LoggingDiagnostics = logging,
            DiagnosticSupport = new DiagnosticSupportStoreOptions(SupportPolicy(logging),
                supportRootHash ?? logging.SupportBundles!.BindingHash)
        };
        foreach (var property in typeof(ProductionStoreOptions).GetProperties())
            if (property.Name is not (nameof(ProductionStoreOptions.LoggingDiagnostics) or
                nameof(ProductionStoreOptions.DiagnosticSupport)))
                property.SetValue(target, property.GetValue(source));
        return target;
    }

    private static DiagnosticSupportPolicy SupportPolicy(LoggingDiagnosticsOptions logging) => new(
        "V158.DiagnosticSupport", "1", "V158-approval", logging.Policy.ContentHash, logging.Policy.TracePolicySnapshotHash,
        TimeSpan.FromDays(1), 100, 4096, 64 * 1024, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromDays(30), 64, 16 * 1024, 32L * 1024 * 1024);

    private static LoggingDiagnosticsOptions Logging(string directory, LoggingDiagnosticsPolicy? deployedPolicy = null)
    {
        var policy = deployedPolicy ?? DiagnosticPipelineTests.Policy();
        var safe = DiagnosticLocal(Path.Combine(directory, "diag-safe"), false, policy.SafeFiles);
        var protectedStore = DiagnosticLocal(Path.Combine(directory, "diag-protected"), true, policy.ProtectedFiles);
        var safeBinding = DiagnosticDirectoryInstallation.Install(safe);
        var protectedBinding = DiagnosticDirectoryInstallation.Install(protectedStore);
        var logging = new LoggingDiagnosticsOptions(policy, safe, safeBinding, protectedStore, protectedBinding);
        var support = SupportPolicy(logging);
        var files = new DiagnosticLocalStoreOptions(Path.Combine(directory, "support"), true,
            support.MaximumBundleBytes, support.MaximumBundleBytes, 4, 4L * support.MaximumBundleBytes,
            support.ExportTimeout, support.Retention);
        return new LoggingDiagnosticsOptions(policy, safe, safeBinding, protectedStore, protectedBinding)
            { SupportBundles = new(support, files, DiagnosticDirectoryInstallation.Install(files)) };
    }

    private static DiagnosticLocalStoreOptions DiagnosticLocal(string path, bool protectedChannel,
        DiagnosticFileBudget budget) => new(path, protectedChannel, budget.MaximumRecordBytes,
        budget.MaximumFileBytes, budget.MaximumFiles, budget.MaximumTotalBytes, budget.RollAfter, budget.Retention);
}
