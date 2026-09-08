#pragma warning disable CA1416

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Schema-14 integrity tests use the real SQLite writer and signed audit chain.
/// The direct SQLite edits are deliberately performed only after the writer has
/// stopped, modelling an offline tamper without making a fake valid session.
/// </summary>
public sealed class CalibrationSessionIntegrityTests
{
    [Fact]
    public async Task V124_I01_RecomputedOrdinaryEventHashesCannotReplaceSignedPayload()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        var header = await fixture.AdmitSessionAsync();

        await fixture.StopStoreAsync();
        await RewriteEventWithRecomputedOrdinaryHashesAsync(fixture.Options,
            header.SessionId);

        var report = await VerifyAsync(fixture.Options);
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        Assert.Equal("CalibrationEventAuditBindingMismatch", report.ReasonCode);
    }

    [Theory]
    [InlineData("missing-event")]
    [InlineData("orphan-manifest")]
    public async Task V124_I02_MissingOrOrphanCalibrationRowsAreRejected(string mutation)
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        var header = await fixture.AdmitSessionAsync();

        await fixture.StopStoreAsync();
        await ApplyGraphMutationAsync(fixture.Options, header.SessionId, mutation);

        var report = await VerifyAsync(fixture.Options);
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        // The signed audit extrema and the durable calibration tables no longer
        // have a one-to-one position graph after either mutation.
        Assert.Equal("AuditUnchainedFact", report.ReasonCode);
    }

    [Fact]
    public async Task V124_I03_AcceptedActionBeforeFirstPhaseRemainsPendingAfterRestoredSession()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        var header = await fixture.AdmitSessionAsync();
        var operationId = Guid.NewGuid();
        var exit = new ExitCalibrationSessionCommand(operationId, fixture.User.Invocation,
            header.SessionId, "V124 integrity restart recovery", Cancel: true);

        var action = await fixture.AuthorizeActionAsync(exit, header);
        Assert.Equal(CommandDisposition.Accepted, action.Outcome.Disposition);

        // Simulate a crash after the signed authorization fact but before the
        // first phase event. The recovery query must find that exact operation.
        var beforeFirstPhase = await fixture.Store.ReadPendingCalibrationActionsAsync(
            header.SessionId);
        var pendingBeforePhase = Assert.Single(beforeFirstPhase);
        Assert.Equal(operationId, pendingBeforePhase.CorrelationId);
        Assert.Equal(AuditedCommandKind.ExitCalibrationSession,
            pendingBeforePhase.CommandKind);

        await fixture.RestartStoreAsync();
        var pendingAfterRestartBeforePhase =
            await fixture.Store.ReadPendingCalibrationActionsAsync(header.SessionId);
        var restartedPending = Assert.Single(pendingAfterRestartBeforePhase);
        Assert.Equal(operationId, restartedPending.CorrelationId);

        var restoring = new CalibrationSessionEvent(Guid.NewGuid(), header.SessionId,
            operationId, CalibrationSessionPhase.Restoring, CalibrationSessionOutcome.Pending,
            "CalibrationRestoreStarted", DateTimeOffset.UtcNow,
            authorizationCommand: exit);
        var restoringWrite = await fixture.Store.AppendCalibrationEventAsync(restoring, null,
            CancellationToken.None, new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(restoringWrite.Committed, restoringWrite.ReasonCode);

        // The initial Start fact is terminalized only after physical restoration;
        // the Exit action intentionally remains pending for the recovery worker.
        var startTerminal = new CommandAuditFact(Guid.NewGuid(), header.AdmissionAttemptId,
            header.Command.CorrelationId, header.RuntimeEpoch, DateTimeOffset.UtcNow,
            AuditedCommandKind.StartCalibrationSession, header.Command.Invocation.Source,
            header.ActorPrincipalId.ToString("D"), header.InteractiveSessionId,
            header.Command.Invocation.StepUpGrantId, CommandAuditPhase.Failed, null,
            "CalibrationSessionRestartAborted", header.ActorPrincipalId.ToString("D"));
        var restored = new CalibrationSessionEvent(Guid.NewGuid(), header.SessionId,
            operationId, CalibrationSessionPhase.Restored, CalibrationSessionOutcome.Cancelled,
            "CalibrationSessionRestartAborted", DateTimeOffset.UtcNow,
            authorizationCommand: exit);
        var restoredWrite = await fixture.Store.AppendCalibrationEventAsync(restored,
            startTerminal, CancellationToken.None,
            new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(restoredWrite.Committed, restoredWrite.ReasonCode);

        await fixture.RestartStoreAsync();
        await fixture.WaitForVerifiedAsync();
        var pendingAfterRestore = await fixture.Store.ReadPendingCalibrationActionsAsync(
            header.SessionId);
        var pendingExit = Assert.Single(pendingAfterRestore);
        Assert.Equal(operationId, pendingExit.CorrelationId);
        Assert.Equal(AuditedCommandKind.ExitCalibrationSession, pendingExit.CommandKind);

        // ReadOpen includes a physically restored session when its exact signed
        // action still lacks a terminal command fact, so restart can close it.
        var open = await fixture.Store.ReadOpenCalibrationSessionsAsync();
        var openEvidence = Assert.Single(open);
        Assert.Equal(header.SessionId, openEvidence.State.SessionId);
        Assert.Equal(CalibrationSessionPhase.Restored, openEvidence.State.Phase);
        Assert.True(openEvidence.State.RestorationVerified);

        var query = await fixture.Store.ReadCalibrationSessionAsync(header.SessionId);
        Assert.True(query.Available, query.ReasonCode);
        Assert.Equal(CalibrationSessionPhase.Restored, query.Evidence!.State.Phase);

        // A restored session needs only the outstanding action terminal. No camera
        // provider or recovery owner is constructed for this startup path.
        await using var runtime = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20),
            calibrationSessionOptions: new CalibrationSessionOptions(),
            productionStoreOptions: fixture.Options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        StationStateSnapshot snapshot;
        do
        {
            snapshot = await runtime.GetSnapshotAsync(timeout.Token);
            Assert.NotEqual(HealthState.Faulted, snapshot.Store.State);
            if (snapshot.Store.State == HealthState.Healthy) break;
            await Task.Delay(10, timeout.Token);
        } while (true);
        Assert.False(snapshot.Ready);
        Assert.True(snapshot.CalibrationSession?.RestorationVerified);
        Assert.Empty(await fixture.Store.ReadOpenCalibrationSessionsAsync());
        Assert.Empty(await fixture.Store.ReadPendingCalibrationActionsAsync(header.SessionId));
    }

    [Fact]
    public async Task V124_I04_SignedActionWithMismatchedRuntimeEpochIsRejectedBeforeAndAfterRestart()
    {
        await using var fixture = await IntegrityFixture.CreateAsync();
        var header = await fixture.AdmitSessionAsync();
        var operationId = Guid.NewGuid();
        var exit = new ExitCalibrationSessionCommand(operationId, fixture.User.Invocation,
            header.SessionId, "V124 mismatched runtime epoch", Cancel: true);
        var wrongEpoch = Guid.NewGuid();
        Assert.NotEqual(header.RuntimeEpoch, wrongEpoch);

        // The normal authorization writer creates both records and signs the
        // IdentityEvent. Only the runtime authority supplied to that writer is
        // deliberately different from the immutable session header.
        var authorization = await fixture.AuthorizeActionAsync(exit, header, wrongEpoch);
        Assert.Equal(CommandDisposition.Accepted, authorization.Outcome.Disposition);

        var beforeRestart = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ReadPendingCalibrationActionsAsync(header.SessionId).AsTask());
        Assert.Equal("CalibrationActionAuthorizationMismatch", beforeRestart.Message);
        var openBeforeRestart = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ReadOpenCalibrationSessionsAsync().AsTask());
        Assert.Equal("CalibrationActionAuthorizationMismatch", openBeforeRestart.Message);

        await fixture.RestartStoreAsync();

        var afterRestart = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ReadPendingCalibrationActionsAsync(header.SessionId).AsTask());
        Assert.Equal("CalibrationActionAuthorizationMismatch", afterRestart.Message);
        var openAfterRestart = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Store.ReadOpenCalibrationSessionsAsync().AsTask());
        Assert.Equal("CalibrationActionAuthorizationMismatch", openAfterRestart.Message);
    }

    private static async Task<AuditIntegrityReport> VerifyAsync(ProductionStoreOptions options) =>
        await new SqliteAuditIntegrityQuery(options).VerifyAsync(new AuditVerificationRequest());

    private static async Task RewriteEventWithRecomputedOrdinaryHashesAsync(
        ProductionStoreOptions options, Guid sessionId)
    {
        await using var connection = await OpenWritableAsync(options.DatabasePath);
        await ExecuteAsync(connection,
            "DROP TRIGGER IF EXISTS calibration_session_event_immutable_update;");

        await using var read = connection.CreateCommand();
        read.CommandText = @"
            SELECT Sequence,EventId,SessionId,OperationId,Kind,PreviousHash,Payload,
                   AuthorizationAuditSequence,AuthorizationAuditHash
            FROM calibration_session_events
            WHERE SessionId=$session AND Position=1;";
        read.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var sequence = reader.GetInt64(0);
        var storedSessionId = Guid.Parse(reader.GetString(2));
        var operationId = Guid.Parse(reader.GetString(3));
        var previousHash = reader.IsDBNull(5) ? null : reader.GetString(5);
        var originalPayload = reader.GetString(6);
        var authorizationSequence = reader.GetInt64(7);
        var authorizationHash = reader.GetString(8);
        var original = CalibrationSessionStorageCodec.DecodeEvent(
            Convert.FromBase64String(originalPayload));
        Assert.True(original.IsAdmission);

        // The replacement is a valid canonical event and both ordinary hashes
        // are recomputed. Only the separately signed ledger payload remains old.
        var replacement = new CalibrationSessionEvent(original.EventId, storedSessionId,
            operationId, original.Phase, original.Outcome, "TamperedOrdinaryPayload",
            original.OccurredAtUtc);
        var replacementBytes = CalibrationSessionStorageCodec.EncodeEvent(replacement);
        var replacementContentHash = CalibrationSessionStorageCodec.EventContentHash(
            replacement, sequence, previousHash, replacementBytes, authorizationSequence,
            authorizationHash);

        await reader.DisposeAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = @"
            UPDATE calibration_session_events
            SET Payload=$payload,PayloadHash=$payloadHash,ContentHash=$contentHash
            WHERE SessionId=$session AND Position=1;";
        update.Parameters.AddWithValue("$payload", Convert.ToBase64String(replacementBytes));
        update.Parameters.AddWithValue("$payloadHash",
            CalibrationSessionStorageCodec.PayloadHash(replacementBytes));
        update.Parameters.AddWithValue("$contentHash", replacementContentHash);
        update.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    private static async Task ApplyGraphMutationAsync(ProductionStoreOptions options,
        Guid sessionId, string mutation)
    {
        await using var connection = await OpenWritableAsync(options.DatabasePath);
        switch (mutation)
        {
            case "missing-event":
                await ExecuteAsync(connection,
                    "DROP TRIGGER IF EXISTS calibration_session_event_immutable_delete;");
                await using (var delete = connection.CreateCommand())
                {
                    delete.CommandText =
                        "DELETE FROM calibration_session_events WHERE SessionId=$session AND Position=1;";
                    delete.Parameters.AddWithValue("$session", sessionId.ToString("D"));
                    Assert.Equal(1, await delete.ExecuteNonQueryAsync());
                }
                break;

            case "orphan-manifest":
                await using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = @"
                        INSERT INTO calibration_frame_manifests
                            (FrameId,SessionId,Position,SourceHash,RelativePath,ByteLength,PixelHash,
                             Payload,PayloadHash,AuthorizationAuditSequence,AuthorizationAuditHash,
                             AuditSequence,AuditHash)
                        VALUES($frame,$session,1,$source,'orphan.bin',1,$pixel,'AA==',$payloadHash,
                               1,$authorizationHash,1,$auditHash);";
                    insert.Parameters.AddWithValue("$frame", Guid.NewGuid().ToString("D"));
                    insert.Parameters.AddWithValue("$session", sessionId.ToString("D"));
                    insert.Parameters.AddWithValue("$source", new string('1', 64));
                    insert.Parameters.AddWithValue("$pixel", new string('2', 64));
                    insert.Parameters.AddWithValue("$payloadHash", new string('3', 64));
                    insert.Parameters.AddWithValue("$authorizationHash", new string('4', 64));
                    insert.Parameters.AddWithValue("$auditHash", new string('5', 64));
                    Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static async Task<SqliteConnection> OpenWritableAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class IntegrityFixture : IAsyncDisposable
    {
        private const string LogicalRole = "TopCamera";
        private const string UserName = "calibration-integrity-admin";
        private const string Password = "V124 calibration integrity test secret 26!";
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _audit;
        private bool _storeStopped;
        private bool _disposed;

        private IntegrityFixture(string directory, AuditIntegrityPolicy audit,
            ProductionStoreOptions options, SqliteCommandStore store,
            LocalIdentityService identity, InteractiveSessionService sessions,
            LocalAuthorizationService authorization, SignedIn user,
            CameraBindingRevision binding, ImagingSetupRevision imaging,
            CalibrationSessionPlan plan, RequestedCameraConfiguration baselineRequested,
            EffectiveCameraConfiguration baselineEffective)
        {
            _directory = directory;
            _audit = audit;
            Options = options;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            User = user;
            Binding = binding;
            Imaging = imaging;
            Plan = plan;
            BaselineRequested = baselineRequested;
            BaselineEffective = baselineEffective;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; private set; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal SignedIn User { get; }
        internal CameraBindingRevision Binding { get; }
        internal ImagingSetupRevision Imaging { get; }
        internal CalibrationSessionPlan Plan { get; }
        internal RequestedCameraConfiguration BaselineRequested { get; }
        internal EffectiveCameraConfiguration BaselineEffective { get; }

        internal static async Task<IntegrityFixture> CreateAsync()
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Schema-14 signed storage requires Windows machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V124-CalibrationIntegrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var audit = new AuditIntegrityPolicy("V124CalibrationIntegrityStation", "1",
                "SharpInspect.Test.V124.CalibrationIntegrity." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "audit-keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1),
                MaximumVerificationEntries = 10_000
            };
            var authorizationPolicy = CreateAuthorizationPolicy();
            var identityOptions = new LocalIdentityOptions(audit.StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v124-calibration-integrity-blocklist", "1",
                        new[] { "known-compromised-calibration-password" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                authorizationPolicy);
            var evidenceRoot = Path.Combine(directory, "calibration-evidence");
            Directory.CreateDirectory(evidenceRoot);
            var options = new ProductionStoreOptions(Path.Combine(directory, "calibration.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                AlarmPolicy = CalibrationSchemaFixture.RecoveryAlarmPolicy(),
                CameraSetup = new CameraSetupStoreOptions(),
                CameraRecovery = new CameraRecoveryStoreOptions(),
                ImagingSetup = new ImagingSetupStoreOptions(),
                CalibrationSessions = new CalibrationSessionStoreOptions
                {
                    EvidenceRoot = evidenceRoot,
                    MaximumSessions = 4,
                    MaximumEvents = 256,
                    MaximumFramesPerSession = 8,
                    MaximumFrameBytes = 1024 * 1024,
                    MaximumTotalFrameBytes = 8 * 1024 * 1024
                },
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 16
            };

            SqliteCommandStore? store = null;
            InteractiveSessionService? sessions = null;
            LocalAuthorizationService? authorization = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);

                var identity = new LocalIdentityService(store, identityOptions,
                    new FixtureConsoleAuthority());
                sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                authorization = new LocalAuthorizationService(store, identityOptions, identity, sessions);
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(audit.StationId,
                        token.Token!.TakeForDisplay(), UserName,
                        "V124 Calibration Integrity Administrator", Password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await WaitForVerifiedAsync(store);

                var signIn = await sessions.SignInAsync(
                    new PasswordSignInRequest(UserName, Password));
                Assert.True(signIn.Succeeded, signIn.ReasonCode);
                var account = (await store.ReadIdentityAsync(CancellationToken.None))
                    .EnumerateAccounts().Single(item => item.PrincipalId == signIn.Identity!.PrincipalId);
                var user = new SignedIn(signIn.Identity!.PrincipalId,
                    signIn.Session.SessionId!.Value, account.AuthorizationRevision);
                await WaitForVerifiedAsync(store);

                var target = new CameraBindingTarget(
                    new CameraProviderIdentity("V124.Integrity.Provider", "1",
                        "V124.Integrity.Adapter", "1"), "device-001");
                var binding = await AppendCompletedBindingAsync(store, audit, user, target);
                var imaging = await AppendImagingAsync(store, options, authorization, user, binding);
                var baselineRequested = Configuration(10);
                var baselineEffective = new EffectiveCameraConfiguration(
                    ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                    new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 100, 0,
                    null);
                var plan = CreatePlan(baselineRequested);
                return new IntegrityFixture(directory, audit, options, store, identity,
                    sessions, authorization, user, binding, imaging, plan,
                    baselineRequested, baselineEffective);
            }
            catch
            {
                authorization?.Dispose();
                if (sessions is not null) await sessions.DisposeAsync();
                if (store is not null) await store.DisposeAsync();
                Cleanup(directory, audit);
                throw;
            }
        }

        internal async Task<CalibrationSessionHeader> AdmitSessionAsync()
        {
            var command = new StartCalibrationSessionCommand(Guid.NewGuid(), User.Invocation,
                Plan, Binding.Revision, Binding.RevisionHash,
                ImagingSetupRevisionReference.FromRevision(Imaging),
                "V124 integrity admission");
            var stepUp = await Authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                User.Invocation, new StepUpBinding(Permission.RunCalibration,
                    command.CorrelationId, command.AuthorizationTarget,
                    AuditedCommandKind.StartCalibrationSession), Password));
            Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
            Assert.NotNull(stepUp.GrantId);
            var authorized = command with
            {
                Invocation = User.Invocation with { StepUpGrantId = stepUp.GrantId }
            };
            var epoch = Guid.NewGuid();
            var attempt = Guid.NewGuid();
            var result = await Authorization.HandleCalibrationCommandAsync(authorized,
                epoch, attempt, new CalibrationSessionAdmissionInput(Binding,
                    BaselineRequested, BaselineEffective, new string('C', 64)), null, null,
                new StoreDeadline(Options.CommitTimeout), CancellationToken.None);
            Assert.Equal(CommandDisposition.Accepted, result.Outcome.Disposition);
            Assert.NotNull(result.Header);
            Assert.NotNull(result.Admission);
            await WaitForVerifiedAsync();
            return result.Header!;
        }

        internal async Task<CalibrationCommandAuthorization> AuthorizeActionAsync(
            CalibrationSessionCommand command, CalibrationSessionHeader header,
            Guid? runtimeEpoch = null) =>
            await Authorization.HandleCalibrationCommandAsync(command, runtimeEpoch ?? header.RuntimeEpoch,
                Guid.NewGuid(), null, header, null,
                new StoreDeadline(Options.CommitTimeout), CancellationToken.None);

        internal async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(Store);

        internal async Task StopStoreAsync()
        {
            if (_storeStopped) return;
            _storeStopped = true;
            await Store.DisposeAsync();
        }

        internal async Task RestartStoreAsync()
        {
            await StopStoreAsync();
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            _storeStopped = false;
            await WaitForVerifiedAsync(Store);
        }

        private static async Task<CameraBindingRevision> AppendCompletedBindingAsync(
            SqliteCommandStore store, AuditIntegrityPolicy audit, SignedIn user,
            CameraBindingTarget target)
        {
            var operationId = Guid.NewGuid();
            var recordedAt = DateTimeOffset.UtcNow;
            var cameraEvent = new CameraSetupEvent(0, Guid.NewGuid(), operationId, LogicalRole,
                AuditedCommandKind.RebindCamera, CameraSetupEventPhase.Completed, 1,
                target: target, succeeded: true, reasonCode: "CameraRebindCompleted",
                changeReason: "V124 calibration integrity binding",
                actorPrincipalId: user.PrincipalId, sessionId: user.SessionId,
                authorAuthorizationRevision: user.AuthorizationRevision, recordedAtUtc: recordedAt);
            cameraEvent = cameraEvent with
            {
                RevisionHash = CameraSetupStorageCodec.ComputeRevisionHash(
                    cameraEvent, 1, null, target)
            };
            var grant = Guid.NewGuid();
            var admission = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupActionAuthorized, recordedAt, audit.StationId,
                user.PrincipalId, null, null, null, "CameraRebindAdmitted",
                ActorPrincipalId: user.PrincipalId, CommandCorrelationId: operationId,
                StepUpGrantId: grant, RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: user.AuthorizationRevision,
                ManagementReason: IdentityManagementReason.AccessChange.ToString(),
                ActionTargetId: LogicalRole, BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(),
                OperationId: operationId, SessionId: user.SessionId);
            var completedIdentity = admission with
            {
                EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
                ReasonCode = "CameraRebindCompleted"
            };
            var attempt = Guid.NewGuid();
            var epoch = Guid.NewGuid();
            var outcome = new CommandAuditFact(Guid.NewGuid(), attempt, operationId, epoch,
                recordedAt, AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
                user.PrincipalId.ToString("D"), user.SessionId, grant, CommandAuditPhase.Outcome,
                CommandDisposition.Accepted, "CameraRebindAdmitted", user.PrincipalId.ToString("D"));
            var terminal = outcome with
            {
                EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed,
                Disposition = null, ReasonCode = "CameraRebindCompleted"
            };
            var write = await store.UpdateIdentityAsync(state => new IdentityUpdate(
                new object(), new[] { admission, completedIdentity },
                new[] { outcome, terminal }, CameraEvents: new[] { cameraEvent }),
                CancellationToken.None);
            Assert.True(write.Committed, write.ReasonCode);
            await WaitForVerifiedAsync(store);
            var persisted = await store.ReadCameraSetupAsync(LogicalRole);
            Assert.NotNull(persisted.State.Binding);
            return persisted.State.Binding!;
        }

        private static async Task<ImagingSetupRevision> AppendImagingAsync(
            SqliteCommandStore store, ProductionStoreOptions options,
            LocalAuthorizationService authorization, SignedIn user,
            CameraBindingRevision binding)
        {
            var persistence = new SqliteImagingSetupRevisionPersistence(store);
            var operationId = Guid.NewGuid();
            var definition = new ImagingSetupDefinition("V124-Integrity-Lens", "Focus-100",
                "Mount-Top", 250, "SensorUp");
            var provisional = new ImagingSetupChangeRequest(operationId, user.Invocation,
                LogicalRole, binding.Revision, binding.RevisionHash, 0, null, definition,
                "V124 imaging integrity baseline");
            var stepUp = await authorization.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(),
                user.Invocation, new StepUpBinding(Permission.ManageCameraBindings, operationId,
                    provisional.AuthorizationTarget, AuditedCommandKind.DeclareImagingSetup), Password));
            Assert.True(stepUp.Succeeded, stepUp.ReasonCode);
            var grant = stepUp.GrantId!.Value;
            var changed = new ImagingSetupChangeRequest(operationId,
                user.Invocation with { StepUpGrantId = grant }, LogicalRole,
                binding.Revision, binding.RevisionHash, 0, null, definition,
                provisional.ChangeReason);
            var cameraAuthorization = await authorization.AuthorizeCameraSetupAsync(
                changed.Invocation, readOnly: false, operationId,
                changed.AuthorizationTarget, AuditedCommandKind.DeclareImagingSetup);
            Assert.True(cameraAuthorization.Authorized, cameraAuthorization.ReasonCode);
            try
            {
                var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId,
                    Guid.NewGuid(), DateTimeOffset.UtcNow,
                    AuditedCommandKind.DeclareImagingSetup, CommandSource.PhysicalConsole,
                    user.PrincipalId.ToString("D"), user.SessionId, grant,
                    CommandAuditPhase.Outcome, CommandDisposition.Accepted,
                    "ImagingSetupRevisionPersisted", user.PrincipalId.ToString("D"));
                var write = await persistence.AppendAsync(new ImagingSetupPersistenceRequest(
                    fact, changed, cameraAuthorization), new StoreDeadline(options.CommitTimeout));
                Assert.True(write.Committed, write.ReasonCode);
            }
            finally
            {
                cameraAuthorization.Reservation?.Dispose();
            }
            await WaitForVerifiedAsync(store);
            return (await persistence.ReadAsync(LogicalRole)).Current!;
        }

        private static CalibrationSessionPlan CreatePlan(
            RequestedCameraConfiguration temporary)
        {
            var hash = new string('A', 64);
            var inputContract = new RecipeContractReference("v124-integrity-input", "1", hash);
            var descriptor = new CalibrationProcedureDescriptor(
                new RecipeContractReference("v124-integrity-procedure", "1", hash),
                inputContract, CalibrationKind.Intrinsic);
            return new CalibrationSessionPlan(
                new CalibrationRequirement(LogicalRole, CalibrationKind.Intrinsic, "geometry",
                    new RecipeContractReference("v124-integrity-coefficients", "1", hash),
                    new RecipeContractReference("v124-integrity-acceptance", "1", hash)),
                descriptor, new CalibrationProcedureInputPayload(inputContract, new byte[] { 1 }),
                temporary, new CalibrationEvidenceSelectionPolicy("v124-integrity-selection", "1",
                    minimumFrames: 1, minimumFeaturesPerFrame: 1, minimumImageCoverage: 0));
        }

        private static RequestedCameraConfiguration Configuration(double exposure) => new(
            ProductionAcquisitionMode.SoftwareTrigger, exposure, 0,
            new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 100, 0, null);

        private static AuthorizationPolicy CreateAuthorizationPolicy()
        {
            var development = AuthorizationPolicy.Development;
            var roles = development.RoleBundles.ToDictionary(item => item.Key,
                item => item.Key == HumanRoleBundle.Administrator
                    ? item.Value.Append(Permission.RunCalibration)
                    : item.Value.AsEnumerable());
            return new AuthorizationPolicy("v124-calibration-integrity", "1", roles);
        }

        private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
            while (store.Integrity?.State != AuditIntegrityState.Verified)
            {
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new XunitException("Calibration schema-14 audit did not become Verified.");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Authorization.Dispose();
                await Sessions.DisposeAsync();
                await Store.DisposeAsync();
            }
            finally
            {
                Cleanup(_directory, _audit);
            }
        }

        private static void Cleanup(string directory, AuditIntegrityPolicy audit)
        {
            try
            {
                var key = WindowsMachineAuditKey.GetKeyPath(audit);
                if (File.Exists(key)) File.Delete(key);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            try
            {
                var full = Path.GetFullPath(directory);
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                    "SharpInspect.Runtime.Tests"));
                var prefix = root.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(full)) Directory.Delete(full, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record SignedIn(Guid PrincipalId, Guid SessionId,
        long AuthorizationRevision)
    {
        internal CommandInvocation Invocation => new(CommandSource.PhysicalConsole,
            PrincipalId.ToString("D"), SessionId);
    }

    private sealed class FixtureConsoleAuthority : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true,
            "S-1-5-21-V124-CalibrationIntegrity");
    }

}

#pragma warning restore CA1416
