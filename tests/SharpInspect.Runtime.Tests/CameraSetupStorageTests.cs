#pragma warning disable CA1416

using System.Globalization;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraSetupStorageTests
{
    [Fact]
    public async Task V117_S01_Schema10InitializesReopensAndReadsUnconfiguredRole()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        Assert.Equal(10, await fixture.ScalarAsync("PRAGMA user_version;"));
        Assert.Equal(1, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraSetupStoreActivated';"));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));

        var first = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.False(first.Result.Available);
        Assert.Equal("CameraSetupUnconfigured", first.Result.ReasonCode);
        Assert.Null(first.Result.Snapshot!.Binding);

        await fixture.RestartAsync();
        var reopened = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.False(reopened.Result.Available);
        Assert.Equal("CameraSetupUnconfigured", reopened.Result.ReasonCode);
        Assert.Null(reopened.State.Binding);
    }

    [Fact]
    public async Task V117_S02_CompletedBindingIsSignedReopenedAndProjected()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        var operationId = await fixture.AppendCompletedBindingAsync();
        var target = fixture.Target();

        var current = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.True(current.Result.Available, current.Result.ReasonCode);
        Assert.NotNull(current.State.Binding);
        Assert.Equal(operationId, current.State.Binding!.OperationId);
        Assert.Equal(1, current.State.Binding.Revision);
        Assert.Equal(target.ContentHash, current.State.Binding.Target.ContentHash);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));
        Assert.Equal(1, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='CameraSetupEvent';"));

        await fixture.RestartAsync();
        var reopened = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.True(reopened.Result.Available, reopened.Result.ReasonCode);
        Assert.Equal(operationId, reopened.State.Binding!.OperationId);
        Assert.Equal(target.ContentHash, reopened.State.Binding.Target.ContentHash);
    }

    [Fact]
    public async Task V117_S03_TamperedCameraIndexIsRejectedOnReopen()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        await fixture.AppendCompletedBindingAsync();
        await fixture.Store.DisposeAsync();
        fixture.MarkStoreDisposed();

        await fixture.ExecuteAsync(@"
            DROP TRIGGER camera_setup_event_immutable_update;
            UPDATE camera_setup_events SET LogicalRole='TamperedRole' WHERE Position=1;
            CREATE TRIGGER camera_setup_event_immutable_update BEFORE UPDATE ON camera_setup_events BEGIN
                SELECT RAISE(ABORT,'ImmutableCameraSetupEvent');
            END;");

        await using var reopened = new SqliteCommandStore(fixture.Options);
        var initialized = await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CameraSetupRowBindingMismatch", initialized.ReasonCode);
        Assert.Equal(AuditIntegrityState.Faulted, reopened.Integrity!.State);
    }

    [Fact]
    public async Task V117_S04_CameraOptInRejectsOlderSchemaWithoutChangingDatabase()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: false);
        await fixture.Store.DisposeAsync();
        fixture.MarkStoreDisposed();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);

        var cameraOptions = fixture.WithCameraSetup();
        await using var rejected = new SqliteCommandStore(cameraOptions);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CameraSetupGovernedMigrationRequired", initialized.ReasonCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));
    }

    [Fact]
    public async Task V117_S05_ChangedCameraCapacityRejectsWithoutChangingDatabase()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        await fixture.Store.DisposeAsync();
        fixture.MarkStoreDisposed();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);

        var changed = fixture.WithCameraSetup(new CameraSetupStoreOptions { MaximumEvents = 9_999 });
        await using var rejected = new SqliteCommandStore(changed);
        var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(initialized.Committed);
        Assert.Equal("CameraSetupConfigurationMismatch", initialized.ReasonCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));
    }

    [Fact]
    public async Task V117_S06_TamperedCameraEventBlocksAlarmReadAndObservationWithoutWriting()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true, alarmPolicy: true);
        await fixture.AppendCompletedBindingAsync();

        await fixture.ExecuteAsync(@"
            DROP TRIGGER camera_setup_event_immutable_update;
            UPDATE camera_setup_events SET LogicalRole='TamperedRole' WHERE Position=1;
            CREATE TRIGGER camera_setup_event_immutable_update BEFORE UPDATE ON camera_setup_events BEGIN
                SELECT RAISE(ABORT,'ImmutableCameraSetupEvent');
            END;");

        var read = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.ReadAlarmStateAsync(fixture.RuntimeEpoch));
        Assert.Equal("CameraSetupRowBindingMismatch", read.Message);

        var observation = await fixture.Store.UpdateAlarmObservationAsync(fixture.RuntimeEpoch, _ =>
            new AlarmObservationUpdate("tampered-camera", Array.Empty<AlarmHistoryRecord>()),
            CancellationToken.None);
        Assert.False(observation.Committed);
        Assert.Equal("AlarmCommitFailed", observation.ReasonCode);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM alarm_events;"));
    }

    [Fact]
    public async Task V117_S07_FailedApplyClearsPriorEffectiveConfigurationOnRestart()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        await fixture.AppendCompletedBindingAsync();
        var binding = (await fixture.Store.ReadCameraSetupAsync("TopCamera")).State.Binding!;

        await fixture.AppendFailedApplyAsync(binding);
        var current = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.True(current.Result.Available, current.Result.ReasonCode);
        Assert.Equal(CameraConnectionState.Closed, current.State.Health!.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, current.State.Health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, current.State.Health.Acquisition);
        Assert.NotNull(current.State.Requested);
        Assert.Null(current.State.Effective);
        Assert.Empty(current.State.Differences);

        await fixture.RestartAsync();
        var reopened = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.True(reopened.Result.Available, reopened.Result.ReasonCode);
        Assert.Equal(CameraConnectionState.Closed, reopened.State.Health!.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, reopened.State.Health.Configuration);
        Assert.Equal(CameraAcquisitionState.Stopped, reopened.State.Health.Acquisition);
        Assert.NotNull(reopened.State.Requested);
        Assert.Null(reopened.State.Effective);
        Assert.Empty(reopened.State.Differences);
    }

    [Fact]
    public async Task V117_S08_RepeatedCompletedOperationAddsRejectedFactWithoutCorruptingHistory()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        var operationId = await fixture.AppendCompletedBindingAsync();
        var binding = (await fixture.Store.ReadCameraSetupAsync("TopCamera")).State.Binding!;

        var duplicate = await fixture.AppendDuplicateRebindAsync(operationId, binding);
        Assert.True(duplicate.Committed, duplicate.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var current = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.True(current.Result.Available, current.Result.ReasonCode);
        Assert.Equal(operationId, current.State.Binding!.OperationId);
        Assert.Equal(binding.RevisionHash, current.State.Binding.RevisionHash);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CorrelationId = '" + operationId.ToString("D") + "' AND Disposition = 0;"));
        Assert.Equal(3L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM command_facts WHERE CorrelationId = '" + operationId.ToString("D") + "';"));
    }

    [Fact]
    public async Task V117_S09_RebindAdmissionWithoutCameraRowRemainsPendingAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        var operationId = await fixture.AppendRebindAdmissionAsync();

        var beforeRestart = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.False(beforeRestart.Result.Available);
        Assert.Equal("CameraSetupOperationPending", beforeRestart.Result.ReasonCode);
        Assert.Null(beforeRestart.State.Pending);
        Assert.True(beforeRestart.State.HasPending);
        Assert.Equal(1, beforeRestart.State.PendingOperationCount);
        Assert.NotNull(beforeRestart.State.PendingAdmission);
        Assert.Equal(operationId, beforeRestart.State.PendingAdmission!.OperationId);
        Assert.Equal("TopCamera", beforeRestart.State.PendingAdmission.LogicalRole);
        Assert.Equal(AuditedCommandKind.RebindCamera,
            beforeRestart.State.PendingAdmission.CommandKind);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));

        var callbackCalled = false;
        var blockedWrite = await fixture.Store.UpdateCameraSetupAsync(Guid.NewGuid(), "TopCamera",
            (state, cameraState, duplicate) =>
            {
                callbackCalled = true;
                return new IdentityUpdate(new object(), Array.Empty<IdentityAuditEvent>());
            }, CancellationToken.None);
        Assert.False(blockedWrite.Committed);
        Assert.Equal("CameraSetupOperationPending", blockedWrite.ReasonCode);
        Assert.False(callbackCalled);
        await fixture.WaitForVerifiedAsync();

        await fixture.RestartAsync();

        var afterRestart = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.False(afterRestart.Result.Available);
        Assert.Equal("CameraSetupOperationPending", afterRestart.Result.ReasonCode);
        Assert.Null(afterRestart.State.Pending);
        Assert.True(afterRestart.State.HasPending);
        Assert.Equal(1, afterRestart.State.PendingOperationCount);
        Assert.Equal(operationId, afterRestart.State.PendingAdmission!.OperationId);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));
    }

    [Fact]
    public async Task V117_S10_TotalCapacityCountsDecodedPayloadBytesAcrossMultipleEvents()
    {
        await using var measured = await Fixture.CreateAsync(cameraSetup: true);
        await measured.AppendCompletedBindingAsync();
        var firstPayloadBytes = await measured.DecodedPayloadBytesAsync();
        var limit = firstPayloadBytes * 2 + 64;
        await using var limited = await Fixture.CreateAsync(cameraSetup: true,
            cameraOptions: new CameraSetupStoreOptions { MaximumTotalBytes = limit });

        await limited.AppendCompletedBindingAsync("TopCamera");
        await limited.AppendCompletedBindingAsync("SideCam01");

        Assert.Equal(2L, await limited.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));
        var actualBytes = await limited.DecodedPayloadBytesAsync();
        Assert.InRange(actualBytes, firstPayloadBytes * 2 - 64, limit);
        await limited.RestartAsync();
        var first = await limited.Store.ReadCameraSetupAsync("TopCamera");
        var second = await limited.Store.ReadCameraSetupAsync("SideCam01");
        Assert.True(first.Result.Available, first.Result.ReasonCode);
        Assert.True(second.Result.Available, second.Result.ReasonCode);
        Assert.Equal(1, second.State.Binding!.Revision);
        Assert.Equal(2, second.State.Binding.Position);
    }

    [Fact]
    public async Task V117_S11_OrphanRebindAdmissionConsumesGlobalPendingCapacity()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true,
            cameraOptions: new CameraSetupStoreOptions { MaximumPendingOperations = 1 });
        await fixture.AppendRebindAdmissionAsync();
        var callbackCalled = false;
        var rejected = await fixture.Store.UpdateCameraSetupAsync(Guid.NewGuid(), "OtherCamera",
            (_, _, _) =>
            {
                callbackCalled = true;
                return new IdentityUpdate(new object(), Array.Empty<IdentityAuditEvent>());
            }, CancellationToken.None);
        Assert.False(rejected.Committed);
        Assert.Equal("CameraSetupPendingCapacityExceeded", rejected.ReasonCode);
        Assert.False(callbackCalled);
        await fixture.WaitForVerifiedAsync();
        var other = await fixture.Store.ReadCameraSetupAsync("OtherCamera");
        Assert.False(other.State.HasPending);
        Assert.Equal(1, other.State.PendingOperationCount);
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_setup_events;"));
    }

    [Fact]
    public async Task V117_S12_FailedRebindTerminalRemainsVerifiableWithoutInventingABinding()
    {
        await using var fixture = await Fixture.CreateAsync(cameraSetup: true);
        await fixture.AppendFailedBindingAsync();
        var failed = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.False(failed.Result.Available);
        Assert.Null(failed.State.Binding);
        Assert.False(failed.State.HasPending);
        Assert.Equal(CameraConnectionState.Closed, failed.State.Health!.Connection);
        Assert.Equal(CameraConfigurationState.Unknown, failed.State.Health.Configuration);
        Assert.Null(failed.State.Effective);
        Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM command_facts;"));

        await fixture.RestartAsync();
        var restarted = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.False(restarted.Result.Available);
        Assert.Null(restarted.State.Binding);
        Assert.False(restarted.State.HasPending);
        Assert.Equal(CameraConfigurationState.Unknown, restarted.State.Health!.Configuration);
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity!.State);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly AuditIntegrityPolicy _auditPolicy;
        private bool _storeDisposed;

        private Fixture(string directory, ProductionStoreOptions options, AuditIntegrityPolicy auditPolicy,
            SqliteCommandStore store)
        {
            _directory = directory;
            Options = options;
            _auditPolicy = auditPolicy;
            Store = store;
        }

        internal ProductionStoreOptions Options { get; private set; }
        internal SqliteCommandStore Store { get; private set; }
        internal string StationId => _auditPolicy.StationId;

        internal static async Task<Fixture> CreateAsync(bool cameraSetup, bool alarmPolicy = false,
            CameraSetupStoreOptions? cameraOptions = null)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Camera setup signed storage requires Windows machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V117-CameraStorage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V117CameraStorageStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V117.Camera." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v117-camera-blocklist", "v1",
                        new[] { "known-compromised" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "camera.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identity,
                AlarmPolicy = alarmPolicy ? new AlarmPolicy("V117CameraAlarmPolicy", "1", new[]
                {
                    new AlarmPolicyRule("CAMERA_TAMPER_TEST", "Camera", AlarmSeverity.Error,
                        ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null)
                }, TimeSpan.FromSeconds(30)) : null,
                CameraSetup = cameraSetup ? cameraOptions ?? new CameraSetupStoreOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(4),
                QueryTimeout = TimeSpan.FromSeconds(4),
                QueueCapacity = 8
            };

            SqliteCommandStore? store = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                var fixture = new Fixture(directory, options, audit, store);
                await fixture.WaitForVerifiedAsync();
                return fixture;
            }
            catch
            {
                if (store is not null) await store.DisposeAsync();
                Cleanup(directory, audit);
                throw;
            }
        }

        internal ProductionStoreOptions WithCameraSetup(CameraSetupStoreOptions? camera = null) =>
            new(Options.DatabasePath)
            {
                AuditIntegrityPolicy = Options.AuditIntegrityPolicy,
                LocalIdentity = Options.LocalIdentity,
                CameraSetup = camera ?? new CameraSetupStoreOptions(),
                CommitTimeout = Options.CommitTimeout,
                QueryTimeout = Options.QueryTimeout,
                QueueCapacity = Options.QueueCapacity
            };

        internal CameraBindingTarget Target() => new(
            new CameraProviderIdentity("V117.Provider", "1", "V117.Adapter", "1"), "device-001");

        internal Guid RuntimeEpoch { get; } = Guid.NewGuid();

        internal Task<Guid> AppendCompletedBindingAsync(string logicalRole = "TopCamera") =>
            AppendBindingOutcomeAsync(logicalRole, succeeded: true);

        internal Task<Guid> AppendFailedBindingAsync() =>
            AppendBindingOutcomeAsync("TopCamera", succeeded: false);

        private async Task<Guid> AppendBindingOutcomeAsync(string logicalRole, bool succeeded)
        {
            var operationId = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var session = Guid.NewGuid();
            var target = Target();
            var recordedAt = DateTimeOffset.UtcNow;
            var reason = succeeded ? "CameraRebindCompleted" : "CameraOpenFailed";
            var health = succeeded ? null : new CameraHealthSnapshot(CameraProviderAvailability.Faulted,
                CameraConnectionState.Closed, CameraConfigurationState.Unknown,
                CameraAcquisitionState.Stopped, new FrameTimePoint(recordedAt, 0),
                new CameraFault(CameraFaultClassification.DeviceFault, reason));
            var cameraEvent = new CameraSetupEvent(0, Guid.NewGuid(), operationId, logicalRole,
                AuditedCommandKind.RebindCamera,
                succeeded ? CameraSetupEventPhase.Completed : CameraSetupEventPhase.Rejected, succeeded ? 1 : 0,
                target: target, health: health, succeeded: succeeded, reasonCode: reason,
                changeReason: "V117StorageBinding", actorPrincipalId: actor,
                sessionId: session, authorAuthorizationRevision: 1, recordedAtUtc: recordedAt);
            cameraEvent = cameraEvent with
            {
                RevisionHash = succeeded ? CameraSetupStorageCodec.ComputeRevisionHash(cameraEvent, 1, null, target) : null
            };
            var grantId = Guid.NewGuid();
            var admissionIdentity = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupActionAuthorized, recordedAt, StationId, actor,
                null, null, null, "CameraRebindCompleted",
                ActorPrincipalId: actor, CommandCorrelationId: operationId,
                StepUpGrantId: grantId,
                RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: 1, ManagementReason: IdentityManagementReason.AccessChange.ToString(),
                ActionTargetId: logicalRole, BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(), OperationId: operationId,
                SessionId: session);
            admissionIdentity = admissionIdentity with { ReasonCode = "CameraRebindAdmitted" };
            var terminalIdentity = admissionIdentity with
            {
                EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
                ReasonCode = reason, OccurredAtUtc = recordedAt
            };
            var attemptId = Guid.NewGuid();
            var runtimeEpoch = Guid.NewGuid();
            var outcome = new CommandAuditFact(Guid.NewGuid(), attemptId, operationId, runtimeEpoch,
                recordedAt, AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
                actor.ToString("D"), session, grantId, CommandAuditPhase.Outcome,
                CommandDisposition.Accepted, "CameraRebindAdmitted", actor.ToString("D"));
            var terminal = outcome with
            {
                EventId = Guid.NewGuid(), Phase = succeeded ? CommandAuditPhase.Completed : CommandAuditPhase.Failed,
                Disposition = null, ReasonCode = reason
            };
            var write = await Store.UpdateIdentityAsync(state => new IdentityUpdate(new object(),
                new[] { admissionIdentity, terminalIdentity },
                new[] { outcome, terminal }, CameraEvents: new[] { cameraEvent }), CancellationToken.None);
            Assert.True(write.Committed, write.ReasonCode);
            await WaitForVerifiedAsync();
            return operationId;
        }

        internal async Task<long> DecodedPayloadBytesAsync()
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM camera_setup_events ORDER BY Position;";
            await using var reader = await command.ExecuteReaderAsync();
            long total = 0;
            while (await reader.ReadAsync())
                total += Convert.FromBase64String(reader.GetString(0)).LongLength;
            return total;
        }

        internal async Task<Guid> AppendRebindAdmissionAsync()
        {
            var operationId = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var session = Guid.NewGuid();
            var grant = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var identity = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupActionAuthorized, now, StationId, actor,
                null, null, null, "CameraRebindAdmitted", ActorPrincipalId: actor,
                CommandCorrelationId: operationId, StepUpGrantId: grant,
                RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: 1, ManagementReason: null,
                ActionTargetId: "TopCamera", BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(),
                OperationId: operationId, SessionId: session);
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId,
                Guid.NewGuid(), now, AuditedCommandKind.RebindCamera,
                CommandSource.PhysicalConsole, actor.ToString("D"), session, grant,
                CommandAuditPhase.Outcome, CommandDisposition.Accepted,
                "CameraRebindAdmitted", actor.ToString("D"));
            var write = await Store.UpdateIdentityAsync(state => new IdentityUpdate(
                new object(), new[] { identity }, new[] { fact }), CancellationToken.None);
            Assert.True(write.Committed, write.ReasonCode);
            await WaitForVerifiedAsync();
            return operationId;
        }

        internal async Task AppendFailedApplyAsync(CameraBindingRevision binding)
        {
            var operationId = Guid.NewGuid();
            var actor = binding.AuthorPrincipalId;
            var session = binding.AuthorSessionId;
            var grantId = Guid.NewGuid();
            var runtimeEpoch = Guid.NewGuid();
            var recordedAt = DateTimeOffset.UtcNow;
            var requested = new RequestedCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 100.4, 0,
                new RegionOfInterest(0, 0, 64, 48), VisionPixelFormat.Mono8, null,
                500, 0, null);
            var health = new CameraHealthSnapshot(CameraProviderAvailability.Faulted,
                CameraConnectionState.Closed, CameraConfigurationState.Unknown,
                CameraAcquisitionState.Stopped, new FrameTimePoint(recordedAt, 0),
                new CameraFault(CameraFaultClassification.DeviceFault, "CameraConfigurationApplyFailed"));
            var admission = new CameraSetupEvent(0, Guid.NewGuid(), operationId, "TopCamera",
                AuditedCommandKind.ApplyCameraDebugConfiguration, CameraSetupEventPhase.Admission,
                binding.Revision, binding.RevisionHash, binding.RevisionHash, binding.Target, binding.Target,
                requested: requested, succeeded: true, reasonCode: "CameraDebugConfigurationAdmitted",
                changeReason: "V117StorageFailedApply", actorPrincipalId: actor, sessionId: session,
                authorAuthorizationRevision: binding.AuthorAuthorizationRevision, recordedAtUtc: recordedAt);
            var terminal = new CameraSetupEvent(0, Guid.NewGuid(), operationId, "TopCamera",
                AuditedCommandKind.ApplyCameraDebugConfiguration, CameraSetupEventPhase.Terminal,
                binding.Revision, binding.RevisionHash, revisionHash: null, binding.Target, binding.Target,
                requested: requested, health: health, succeeded: false,
                reasonCode: "CameraConfigurationApplyFailed", changeReason: "V117StorageFailedApply",
                actorPrincipalId: actor, sessionId: session,
                authorAuthorizationRevision: binding.AuthorAuthorizationRevision,
                recordedAtUtc: recordedAt.AddMilliseconds(1));

            var admissionIdentity = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupActionAuthorized, recordedAt, StationId, actor,
                null, null, null, admission.ReasonCode, ActorPrincipalId: actor,
                CommandCorrelationId: operationId, StepUpGrantId: grantId,
                RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: binding.AuthorAuthorizationRevision,
                ManagementReason: null, ActionTargetId: "TopCamera",
                BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.ApplyCameraDebugConfiguration.ToString(),
                OperationId: operationId, SessionId: session);
            var terminalIdentity = admissionIdentity with
            {
                EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
                OccurredAtUtc = terminal.RecordedAtUtc, ReasonCode = terminal.ReasonCode
            };
            var outcome = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId,
                runtimeEpoch, recordedAt, AuditedCommandKind.ApplyCameraDebugConfiguration,
                CommandSource.PhysicalConsole, actor.ToString("D"), session, grantId,
                CommandAuditPhase.Outcome, CommandDisposition.Accepted, admission.ReasonCode,
                actor.ToString("D"));
            var terminalFact = outcome with
            {
                EventId = Guid.NewGuid(), OccurredAtUtc = terminal.RecordedAtUtc,
                Phase = CommandAuditPhase.Failed, Disposition = null, ReasonCode = terminal.ReasonCode
            };
            var write = await Store.UpdateIdentityAsync(state => new IdentityUpdate(
                new object(), new[] { admissionIdentity, terminalIdentity },
                new[] { outcome, terminalFact },
                CameraEvents: new[] { admission, terminal }), CancellationToken.None);
            Assert.True(write.Committed, write.ReasonCode);
            await WaitForVerifiedAsync();
        }

        internal async Task<IdentityWriteResult> AppendDuplicateRebindAsync(Guid operationId,
            CameraBindingRevision binding)
        {
            var actor = binding.AuthorPrincipalId;
            var session = binding.AuthorSessionId;
            var now = DateTimeOffset.UtcNow;
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), operationId,
                Guid.NewGuid(), now, AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
                actor.ToString("D"), session, Guid.NewGuid(), CommandAuditPhase.Outcome,
                Disposition: CommandDisposition.Rejected, ReasonCode: "CameraSetupOperationConflict",
                actor.ToString("D"));
            var identity = new IdentityAuditEvent(Guid.NewGuid(),
                IdentityEventKind.CameraSetupOperationCompleted, now, StationId, actor,
                null, null, null, fact.ReasonCode, ActorPrincipalId: actor,
                CommandCorrelationId: operationId, StepUpGrantId: fact.ClaimedStepUpGrantId,
                RequiredPermission: Permission.ManageCameraBindings.ToString(),
                AuthorizationRevision: binding.AuthorAuthorizationRevision, ManagementReason: null,
                ActionTargetId: "TopCamera", BoundCommandCorrelationId: operationId,
                ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(),
                OperationId: operationId, SessionId: session);
            return await Store.UpdateIdentityAsync(state => new IdentityUpdate(
                new object(), new[] { identity }, new[] { fact }), CancellationToken.None);
        }

        internal async Task RestartAsync()
        {
            if (!_storeDisposed) await Store.DisposeAsync();
            _storeDisposed = false;
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync();
        }

        internal async Task WaitForVerifiedAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (Store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (Store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Camera setup audit did not become Verified: " + Store.Integrity?.ReasonCode);
        }

        internal async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal void MarkStoreDisposed() => _storeDisposed = true;

        public async ValueTask DisposeAsync()
        {
            if (!_storeDisposed)
            {
                _storeDisposed = true;
                await Store.DisposeAsync();
            }
            Cleanup(_directory, _auditPolicy);
        }

        private static void Cleanup(string directory, AuditIntegrityPolicy policy)
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                var fullDirectory = Path.GetFullPath(directory);
                var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests"));
                if (fullDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

#pragma warning restore CA1416
