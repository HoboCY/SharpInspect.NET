using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Bounded evidence for the schema-32 to schema-33 migration-readiness replay.
///
/// Most cases drive the real bounded store created by the shared
/// RecipeDraftStorageTests fixture (real machine audit key, real signed chain,
/// real writer lane) and call the readiness replay on a real read-only SQLite
/// snapshot of that database. The production-admission tail uses the existing
/// arm fixture, and one case additionally enters the public startup-maintenance
/// coordinator to prove the same gate refuses that entry without changing the
/// source bytes. Every policy, qualification, provider and endpoint value here
/// is a test fixture input, never presented as authorized production or
/// physical evidence: no schema migration, camera, Modbus transport or
/// production command is executed, and every camera setup, PLC communication,
/// admission and arm row is software evidence written through the real signed
/// store writers.
/// </summary>
public sealed class StoreMigrationReadinessTests
{
    [Fact]
    [Trait("VerificationId", "V149_S01")]
    public async Task V149_S01_EmptyConfiguredArmLedgerIsMigrationQuiescent()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionAdmission: new ProductionAdmissionStoreOptions(),
            productionArming: new ProductionArmStoreOptions());

        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        RequireMigrationQuiescent(fixture);
    }

    [Fact]
    [Trait("VerificationId", "V149_S02")]
    public async Task V149_S02_OpenArmAttemptBlocksReadinessUntilItsTerminalEvent()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionAdmission: new ProductionAdmissionStoreOptions(),
            productionArming: new ProductionArmStoreOptions());

        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var attemptId = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var stationId = fixture.Options.AuditIntegrityPolicy!.StationId;
        var startupPolicy = new RecipeContractReference("V149.Migration.Startup", "1", new string('A', 64));
        var postActivationPolicy = new RecipeContractReference("V149.Migration.Post", "1", new string('B', 64));
        var deploymentHash = new string('C', 64);

        var attempted = await fixture.Store.AppendProductionArmEventAsync(new ProductionArmWriteRequest(
            AttemptId: attemptId, RuntimeEpoch: runtimeEpoch, Cause: ProductionArmCause.Startup,
            Kind: ProductionArmEventKind.Attempted, StationId: stationId, StartupPolicy: startupPolicy,
            PostActivationPolicy: postActivationPolicy, DeploymentHash: deploymentHash, PlcRequest: null,
            Activation: null, MaintenanceHeadHash: null, AdmissionGeneration: 0, Report: null,
            ExpectedDurableHeads: new Dictionary<string, string>(),
            CurrentDurableHeads: new Dictionary<string, string>(), Reason: ProductionArmReason.None,
            ReasonCode: "V149MigrationReadinessAttempted"), new StoreDeadline(TimeSpan.FromSeconds(5)),
            CancellationToken.None);
        Assert.True(attempted.Committed, attempted.ReasonCode);

        var blocked = Assert.Throws<InvalidOperationException>(() => RequireMigrationQuiescent(fixture));
        Assert.Equal("StoreMigrationProductionArmNotQuiescent", blocked.Message);

        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var failed = await fixture.Store.AppendProductionArmEventAsync(new ProductionArmWriteRequest(
            AttemptId: attemptId, RuntimeEpoch: runtimeEpoch, Cause: ProductionArmCause.Startup,
            Kind: ProductionArmEventKind.Failed, StationId: stationId, StartupPolicy: startupPolicy,
            PostActivationPolicy: postActivationPolicy, DeploymentHash: deploymentHash, PlcRequest: null,
            Activation: null, MaintenanceHeadHash: null, AdmissionGeneration: 0, Report: null,
            ExpectedDurableHeads: new Dictionary<string, string>(),
            CurrentDurableHeads: new Dictionary<string, string>(), Reason: ProductionArmReason.Interrupted,
            ReasonCode: "V149MigrationReadinessInterrupted"), new StoreDeadline(TimeSpan.FromSeconds(5)),
            CancellationToken.None);
        Assert.True(failed.Committed, failed.ReasonCode);

        // A terminal attempt retains one optional status-delivery reservation
        // entry, but the attempt itself is closed work: readiness must accept
        // it instead of requiring an empty reserve.
        RequireMigrationQuiescent(fixture);
    }

    [Fact]
    [Trait("VerificationId", "V149_S03")]
    public async Task V149_S03_AnyRolePendingCameraOperationBlocksReadinessUntilItsTerminalEvent()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            cameraSetup: new CameraSetupStoreOptions());
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var stationId = fixture.Options.AuditIntegrityPolicy!.StationId;
        const string role = "SideCam01";
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var target = CameraTarget();
        var recordedAt = DateTimeOffset.UtcNow;

        // A completed rebind is deliberate retained station state: the role has
        // one binding and no pending operation, so readiness must accept it.
        var bindingOperation = Guid.NewGuid();
        var bindingAttempt = Guid.NewGuid();
        var binding = new CameraSetupEvent(0, Guid.NewGuid(), bindingOperation, role,
            AuditedCommandKind.RebindCamera, CameraSetupEventPhase.Completed, 1,
            target: target, succeeded: true, reasonCode: "CameraRebindCompleted",
            changeReason: "V149MigrationReadinessBinding", actorPrincipalId: actor,
            sessionId: session, authorAuthorizationRevision: 1, recordedAtUtc: recordedAt);
        binding = binding with
        {
            RevisionHash = CameraSetupStorageCodec.ComputeRevisionHash(binding, 1, null, target)
        };
        var bindingAdmission = CameraAdmissionIdentity(bindingOperation, actor, session, grant, 1, role,
            AuditedCommandKind.RebindCamera, "CameraRebindAdmitted", stationId, recordedAt);
        var bindingOutcome = CameraCommandFact(bindingAttempt, bindingOperation, runtimeEpoch,
            AuditedCommandKind.RebindCamera, actor, session, grant, CommandAuditPhase.Outcome,
            CommandDisposition.Accepted, "CameraRebindAdmitted", recordedAt);
        var bindingTerminal = bindingOutcome with
        {
            EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed, Disposition = null,
            ReasonCode = "CameraRebindCompleted", OccurredAtUtc = recordedAt.AddMilliseconds(1)
        };
        var bindingTerminalIdentity = bindingAdmission with
        {
            EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
            ReasonCode = "CameraRebindCompleted", OccurredAtUtc = bindingTerminal.OccurredAtUtc
        };
        await WriteCameraSetupAsync(fixture.Store, bindingOperation, role,
            new[] { bindingAdmission, bindingTerminalIdentity },
            new[] { bindingOutcome, bindingTerminal }, new[] { binding });
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        RequireMigrationQuiescent(fixture);

        // An Apply admission without its terminal event is unfinished work for
        // that role, even though the role itself is not the default one.
        var applyOperation = Guid.NewGuid();
        var applyRequested = CameraRequestedConfiguration();
        const string applyChangeReason = "V149MigrationReadinessApply";
        const string applyAdmittedReason = "CameraDebugConfigurationAdmitted";
        const string applyFailedReason = "CameraConfigurationApplyFailed";
        var applyAdmittedAt = recordedAt.AddSeconds(1);
        var applyAdmission = new CameraSetupEvent(0, Guid.NewGuid(), applyOperation, role,
            AuditedCommandKind.ApplyCameraDebugConfiguration, CameraSetupEventPhase.Admission, 1,
            previousRevisionHash: binding.RevisionHash, previousTarget: target, target: target,
            requested: applyRequested, succeeded: true, reasonCode: applyAdmittedReason,
            changeReason: applyChangeReason, actorPrincipalId: actor, sessionId: session,
            authorAuthorizationRevision: 1, recordedAtUtc: applyAdmittedAt);
        var applyOutcome = CameraCommandFact(Guid.NewGuid(), applyOperation, runtimeEpoch,
            AuditedCommandKind.ApplyCameraDebugConfiguration, actor, session, grant,
            CommandAuditPhase.Outcome, CommandDisposition.Accepted, applyAdmittedReason,
            applyAdmittedAt);
        await WriteCameraSetupAsync(fixture.Store, applyOperation, role,
            new[]
            {
                CameraAdmissionIdentity(applyOperation, actor, session, grant, 1, role,
                    AuditedCommandKind.ApplyCameraDebugConfiguration, applyAdmittedReason, stationId,
                    applyAdmittedAt)
            }, new[] { applyOutcome }, new[] { applyAdmission });
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        var pendingOnRole = await fixture.Store.ReadCameraSetupAsync(role);
        Assert.Equal(1, pendingOnRole.State.PendingOperationCount);
        Assert.NotNull(pendingOnRole.State.Pending);
        var pendingEvent = pendingOnRole.State.Pending!;
        Assert.Equal(applyOperation, pendingEvent.OperationId);
        // The projection counts every logical role, so an unrelated role sees
        // the same global pending operation even though it has none of its own.
        var unrelatedRole = await fixture.Store.ReadCameraSetupAsync("TopCamera");
        Assert.Equal(1, unrelatedRole.State.PendingOperationCount);
        Assert.Null(unrelatedRole.State.Pending);
        Assert.Null(unrelatedRole.State.PendingAdmission);

        var blocked = Assert.Throws<InvalidOperationException>(() => RequireMigrationQuiescent(fixture));
        Assert.Equal("StoreMigrationCameraSetupNotQuiescent", blocked.Message);

        // The terminal event is the only closure for that admission.
        var applyFailedAt = applyAdmittedAt.AddSeconds(1);
        var applyTerminal = new CameraSetupEvent(0, Guid.NewGuid(), applyOperation, role,
            AuditedCommandKind.ApplyCameraDebugConfiguration, CameraSetupEventPhase.Terminal, 1,
            previousRevisionHash: binding.RevisionHash, previousTarget: target, target: target,
            requested: applyRequested, health: CameraFailureHealth(applyFailedAt), succeeded: false,
            reasonCode: applyFailedReason, changeReason: applyChangeReason, actorPrincipalId: actor,
            sessionId: session, authorAuthorizationRevision: 1, recordedAtUtc: applyFailedAt);
        var applyTerminalIdentity = CameraAdmissionIdentity(applyOperation, actor, session, grant, 1,
            role, AuditedCommandKind.ApplyCameraDebugConfiguration, applyFailedReason, stationId,
            applyFailedAt) with { Kind = IdentityEventKind.CameraSetupOperationCompleted };
        var applyTerminalFact = applyOutcome with
        {
            EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Failed, Disposition = null,
            ReasonCode = applyFailedReason, OccurredAtUtc = applyFailedAt
        };
        await WriteCameraSetupAsync(fixture.Store, applyOperation, role,
            new[] { applyTerminalIdentity }, new[] { applyTerminalFact }, new[] { applyTerminal });
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        Assert.Equal(0, (await fixture.Store.ReadCameraSetupAsync(role)).State.PendingOperationCount);
        Assert.Equal(0, (await fixture.Store.ReadCameraSetupAsync("TopCamera")).State.PendingOperationCount);
        RequireMigrationQuiescent(fixture);
    }

    [Fact]
    [Trait("VerificationId", "V149_S04")]
    public async Task V149_S04_IdentityOnlyRebindAdmissionBlocksReadinessUntilItsCameraRowCompletes()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            cameraSetup: new CameraSetupStoreOptions());
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var stationId = fixture.Options.AuditIntegrityPolicy!.StationId;
        const string role = "TopCamera";
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var runtimeEpoch = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var target = CameraTarget();

        // A signed Rebind admission writes no camera row until the hardware
        // work finishes; the durable identity/command pair alone is pending
        // work that the readiness replay must not mistake for a closed role.
        var admission = CameraAdmissionIdentity(operationId, actor, session, grant, 1, role,
            AuditedCommandKind.RebindCamera, "CameraRebindAdmitted", stationId, recordedAt);
        var outcome = CameraCommandFact(attemptId, operationId, runtimeEpoch,
            AuditedCommandKind.RebindCamera, actor, session, grant, CommandAuditPhase.Outcome,
            CommandDisposition.Accepted, "CameraRebindAdmitted", recordedAt);
        await WriteCameraSetupAsync(fixture.Store, operationId, role,
            new[] { admission }, new[] { outcome }, Array.Empty<CameraSetupEvent>());
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        var pending = await fixture.Store.ReadCameraSetupAsync(role);
        Assert.Equal(1, pending.State.PendingOperationCount);
        Assert.NotNull(pending.State.PendingAdmission);
        var pendingAdmission = pending.State.PendingAdmission!;
        Assert.Equal(operationId, pendingAdmission.OperationId);
        Assert.Equal(role, pendingAdmission.LogicalRole);
        Assert.Equal(AuditedCommandKind.RebindCamera, pendingAdmission.CommandKind);

        var blocked = Assert.Throws<InvalidOperationException>(() => RequireMigrationQuiescent(fixture));
        Assert.Equal("StoreMigrationCameraSetupNotQuiescent", blocked.Message);

        // Only the completed rebind row closes that admission.
        var completedAt = recordedAt.AddSeconds(1);
        var completed = new CameraSetupEvent(0, Guid.NewGuid(), operationId, role,
            AuditedCommandKind.RebindCamera, CameraSetupEventPhase.Completed, 1,
            target: target, succeeded: true, reasonCode: "CameraRebindCompleted",
            changeReason: "V149MigrationReadinessRebind", actorPrincipalId: actor,
            sessionId: session, authorAuthorizationRevision: 1, recordedAtUtc: completedAt);
        completed = completed with
        {
            RevisionHash = CameraSetupStorageCodec.ComputeRevisionHash(completed, 1, null, target)
        };
        var terminalIdentity = admission with
        {
            EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
            ReasonCode = "CameraRebindCompleted", OccurredAtUtc = completedAt
        };
        var terminalFact = outcome with
        {
            EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed, Disposition = null,
            ReasonCode = "CameraRebindCompleted", OccurredAtUtc = completedAt
        };
        await WriteCameraSetupAsync(fixture.Store, operationId, role,
            new[] { terminalIdentity }, new[] { terminalFact }, new[] { completed });
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        var closed = await fixture.Store.ReadCameraSetupAsync(role);
        Assert.Equal(0, closed.State.PendingOperationCount);
        Assert.Null(closed.State.PendingAdmission);
        Assert.NotNull(closed.State.Binding);
        RequireMigrationQuiescent(fixture);
    }

    [Fact]
    [Trait("VerificationId", "V149_S05")]
    public async Task V149_S05_AdmittedProductionAdmissionBlocksReadinessUntilItIsCompleted()
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync();
        using var qualification = new ProductionAdmissionTestFixture();
        var virtualFacts = qualification.Facts();
        var heads = await station.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        var facts = new ProductionAdmissionFacts(virtualFacts.Configuration, virtualFacts.Qualifications,
            virtualFacts.RuntimeGates.Values.ToArray(), heads);
        var report = qualification.Evaluate(facts);
        Assert.True(report.CanArm);
        var command = station.Command();

        // The accepted admission commits its signed command/identity facts and
        // the schema-22 report row in one transaction. The qualification inputs
        // are test-only fixture values, never production authority.
        var admitted = await station.Authorization.HandleProductionArmAsync(command,
            report.RuntimeEpoch, Guid.NewGuid(), report.AdmissionGeneration, facts, report, null,
            new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, admitted.Disposition);
        Assert.Equal(AuditPersistence.Persisted, admitted.Audit);
        await station.WaitVerifiedAsync();
        var pending = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.Equal(ProductionAdmissionEventKind.Admitted, pending.Latest!.Kind);

        var blocked = Assert.Throws<InvalidOperationException>(
            () => RequireMigrationQuiescent(station.Options, station.Store));
        Assert.Equal("StoreMigrationProductionAdmissionNotQuiescent", blocked.Message);

        var terminalWriter = Assert.IsAssignableFrom<IProductionAdmissionTerminalWriter>(station.Store);
        var completed = await terminalWriter.CompleteProductionAdmissionAsync(command.CorrelationId,
            admitted.AttemptId!.Value, report.RuntimeEpoch, report.AdmissionGeneration,
            "ProductionAdmissionFinalized", new StoreDeadline(TimeSpan.FromSeconds(5)));
        Assert.True(completed.Committed, completed.ReasonCode);
        await station.WaitVerifiedAsync();
        var closed = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(closed.Available, closed.ReasonCode);
        Assert.Equal(ProductionAdmissionEventKind.Completed, closed.Latest!.Kind);

        RequireMigrationQuiescent(station.Options, station.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S06")]
    public async Task V149_S06_PlcRecoveryTailBlocksReadinessUntilItsExactCycleCompletes()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            plcCommunication: new PlcCommunicationStoreOptions());
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var recoveredEpoch = Guid.NewGuid();
        var freshEpoch = Guid.NewGuid();
        var firstCycle = Guid.NewGuid();
        var secondCycle = Guid.NewGuid();
        var freshCycle = Guid.NewGuid();

        // Two durable recovery-required tails on the same endpoint, runtime
        // epoch and monitor, but different recovery cycles.
        await AppendPlcObservationAsync(fixture.Store, recoveredEpoch, firstCycle,
            PlcCommunicationEventKind.CommunicationLost, "PlcTransportLost", 1);
        await AppendPlcObservationAsync(fixture.Store, recoveredEpoch, secondCycle,
            PlcCommunicationEventKind.HeartbeatStale, "PlcControllerHeartbeatStale", 2);
        var blocked = Assert.Throws<InvalidOperationException>(() => RequireMigrationQuiescent(fixture));
        Assert.Equal("StoreMigrationPlcRecoveryClosureRequired", blocked.Message);

        // A complete recovery in a newer runtime epoch is real work, not a
        // closure of the older unresolved tail.
        await AppendPlcObservationAsync(fixture.Store, freshEpoch, freshCycle,
            PlcCommunicationEventKind.RecoveryCompleted, "PlcCommunicationRecoveredArmRequired", 3);
        var stillBlocked = Assert.Throws<InvalidOperationException>(() => RequireMigrationQuiescent(fixture));
        Assert.Equal("StoreMigrationPlcRecoveryClosureRequired", stillBlocked.Message);

        // Closing one cycle leaves the sibling cycle's tail unresolved.
        await AppendPlcObservationAsync(fixture.Store, recoveredEpoch, firstCycle,
            PlcCommunicationEventKind.RecoveryCompleted, "PlcCommunicationRecoveredArmRequired", 4);
        Assert.Throws<InvalidOperationException>(() => RequireMigrationQuiescent(fixture));

        // The tail is released only by the completion carrying its own exact
        // endpoint, runtime epoch and recovery cycle.
        await AppendPlcObservationAsync(fixture.Store, recoveredEpoch, secondCycle,
            PlcCommunicationEventKind.RecoveryCompleted, "PlcCommunicationRecoveredArmRequired", 5);
        RequireMigrationQuiescent(fixture);
    }

    [Fact]
    [Trait("VerificationId", "V149_S07")]
    public async Task V149_S07_PlcObservationsWithoutARecoveryTailStayMigrationQuiescent()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            plcCommunication: new PlcCommunicationStoreOptions());
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        var runtimeEpoch = Guid.NewGuid();
        var recoveryCycle = Guid.NewGuid();
        var observations = new (PlcCommunicationEventKind Kind, string Reason)[]
        {
            (PlcCommunicationEventKind.ConnectionEstablished, "PlcTransportConnected"),
            (PlcCommunicationEventKind.SynchronizationWindowObserved, "PlcSynchronizationWindowObserved"),
            (PlcCommunicationEventKind.ControllerEpochObserved, "PlcControllerEpochObserved"),
            (PlcCommunicationEventKind.RecoveryCycleStarted, "PlcRecoveryCycleStarted"),
            (PlcCommunicationEventKind.ReconnectAttempt, "PlcReconnectAttempted"),
            (PlcCommunicationEventKind.CommunicationStopped, "PlcCommunicationStopped"),
            (PlcCommunicationEventKind.RecoveryCompleted, "PlcCommunicationRecoveredArmRequired")
        };
        var sequence = 1L;
        foreach (var (kind, reason) in observations)
            await AppendPlcObservationAsync(fixture.Store, runtimeEpoch, recoveryCycle, kind, reason,
                sequence++);

        // Only the five recovery-required kinds add a durable tail; ordinary
        // observations and a completion without a tail must stay migratable.
        RequireMigrationQuiescent(fixture);
    }

    [Fact]
    [Trait("VerificationId", "V149_S08")]
    public async Task V149_S08_PublicMigrationEntryRefusesAnUnclosedAdmissionWithoutTouchingTheSource()
    {
        var fixture = await MigrationFixture.CreateAsync();
        var correlationId = Guid.NewGuid();
        const string userName = "v149.migration.admin";
        const string password = "V149 migration maintenance secret 2026!";
        await using (var source = new SqliteCommandStore(fixture.Source))
        {
            var initialized = await source.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(source);
            var identity = new LocalIdentityService(source, fixture.Source.LocalIdentity!,
                new RecipeDraftStorageTests.Fixture.FixtureConsole());
            var token = await identity.ProvisionBootstrapTokenAsync();
            Assert.True(token.Succeeded, token.ReasonCode);
            var displayed = token.Token!.TakeForDisplay();
            token.Token.Dispose();
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(source);
            var created = await identity.CreateFirstAdministratorAsync(new BootstrapAdministratorRequest(
                fixture.Source.AuditIntegrityPolicy!.StationId, displayed, userName,
                "V149 Migration Administrator", password));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(source);
            await using var sessions = new InteractiveSessionService(identity,
                fixture.Source.LocalIdentity!.AuthenticationPolicy, identity.PersistSessionEventAsync);
            var login = await sessions.SignInAsync(new PasswordSignInRequest(userName, password));
            Assert.True(login.Succeeded, login.ReasonCode);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(source);
            using var authorization = new LocalAuthorizationService(source,
                fixture.Source.LocalIdentity!, identity, sessions);
            using var qualification = new ProductionAdmissionTestFixture();
            var virtualFacts = qualification.Facts();
            var heads = await source.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
            var facts = new ProductionAdmissionFacts(virtualFacts.Configuration,
                virtualFacts.Qualifications, virtualFacts.RuntimeGates.Values.ToArray(), heads);
            var report = qualification.Evaluate(facts);
            Assert.True(report.CanArm);
            var command = new ArmProductionCommand(correlationId, new CommandInvocation(
                CommandSource.PhysicalConsole, login.Identity!.PrincipalId.ToString("D"),
                login.Session.SessionId));
            var outcome = await authorization.HandleProductionArmAsync(command, report.RuntimeEpoch,
                Guid.NewGuid(), report.AdmissionGeneration, facts, report, null,
                new StoreDeadline(TimeSpan.FromSeconds(10)), CancellationToken.None);
            Assert.Equal(CommandDisposition.Accepted, outcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
            await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(source);
        }

        var sourceFingerprint = fixture.Fingerprint();
        var opened = await fixture.OpenAsync();
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using var session = opened.Session!;
        var refused = await session.AdvanceAsync();
        Assert.Equal(StoreMigrationPhase.MaintenanceRequired, refused.Phase);
        Assert.Equal("StoreMigrationProductionAdmissionNotQuiescent", refused.ReasonCode);
        Assert.Equal(sourceFingerprint.ContentHash, fixture.Fingerprint().ContentHash);
    }

    private static void RequireMigrationQuiescent(RecipeDraftStorageTests.Fixture fixture) =>
        RequireMigrationQuiescent(fixture.Options, fixture.Store);

    private static void RequireMigrationQuiescent(ProductionStoreOptions options, SqliteCommandStore store)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        store.RequireMigrationQuiescent(read.Handle!, new StoreDeadline(TimeSpan.FromSeconds(10)));
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static CameraBindingTarget CameraTarget() => new(
        new CameraProviderIdentity("V149.Migration.Provider", "1", "V149.Migration.Adapter", "1"),
        "v149-migration-device-001");

    private static RequestedCameraConfiguration CameraRequestedConfiguration() => new(
        ProductionAcquisitionMode.SoftwareTrigger, 120.0, 0.0, new RegionOfInterest(0, 0, 32, 24),
        VisionPixelFormat.Mono8, null, 400, 0.0, null);

    private static CameraHealthSnapshot CameraFailureHealth(DateTimeOffset observedAt) => new(
        CameraProviderAvailability.Faulted, CameraConnectionState.Closed,
        CameraConfigurationState.Unknown, CameraAcquisitionState.Stopped,
        new FrameTimePoint(observedAt, 0),
        new CameraFault(CameraFaultClassification.DeviceFault, "CameraConfigurationApplyFailed"));

    private static IdentityAuditEvent CameraAdmissionIdentity(Guid operationId, Guid actor, Guid session,
        Guid grantId, long authorizationRevision, string role, AuditedCommandKind commandKind,
        string reasonCode, string stationId, DateTimeOffset occurredAt) => new(Guid.NewGuid(),
        IdentityEventKind.CameraSetupActionAuthorized, occurredAt, stationId, actor, null, null, null,
        reasonCode, ActorPrincipalId: actor, CommandCorrelationId: operationId, StepUpGrantId: grantId,
        RequiredPermission: Permission.ManageCameraBindings.ToString(),
        AuthorizationRevision: authorizationRevision, ManagementReason: null, ActionTargetId: role,
        BoundCommandCorrelationId: operationId, ActionCommandKind: commandKind.ToString(),
        OperationId: operationId, SessionId: session);

    private static CommandAuditFact CameraCommandFact(Guid attemptId, Guid operationId, Guid runtimeEpoch,
        AuditedCommandKind commandKind, Guid actor, Guid session, Guid grantId, CommandAuditPhase phase,
        CommandDisposition? disposition, string reasonCode, DateTimeOffset occurredAt) => new(Guid.NewGuid(),
        attemptId, operationId, runtimeEpoch, occurredAt, commandKind, CommandSource.PhysicalConsole,
        actor.ToString("D"), session, grantId, phase, disposition, reasonCode, actor.ToString("D"));

    /// <summary>
    /// Writes through the real camera-setup writer lane: the same signed
    /// identity/command transaction the authorization service uses, including
    /// the terminal continuation of an already durable admission.
    /// </summary>
    private static async Task WriteCameraSetupAsync(SqliteCommandStore store, Guid operationId,
        string logicalRole, IReadOnlyList<IdentityAuditEvent> identities,
        IReadOnlyList<CommandAuditFact> facts, IReadOnlyList<CameraSetupEvent> cameraEvents)
    {
        var result = await store.UpdateCameraSetupAsync(operationId, logicalRole,
            (_, _, _) => new IdentityUpdate(new object(), identities, facts, CameraEvents: cameraEvents),
            CancellationToken.None, new StoreDeadline(TimeSpan.FromSeconds(10)));
        Assert.True(result.Committed, result.ReasonCode);
    }

    /// <summary>
    /// Appends one signed PLC communication observation through the real
    /// protocol ledger writer: no Modbus transport and no hardware are touched.
    /// </summary>
    private static async Task AppendPlcObservationAsync(SqliteCommandStore store, Guid runtimeEpoch,
        Guid recoveryCycleId, PlcCommunicationEventKind kind, string reasonCode, long sequence)
    {
        var result = await store.AppendPlcCommunicationEventAsync(new PlcCommunicationWriteRequest(
            RuntimeEpoch: runtimeEpoch, EndpointBindingHash: PlcEndpoint, ProfileHash: PlcProfileHash,
            PolicyHash: PlcPolicyHash, Generation: 0, Attempt: 0, Kind: kind, ReasonCode: reasonCode,
            ControllerEpoch: 1, ObservedAtUtc: PlcObservedAt.AddSeconds(sequence),
            MonotonicTimestamp: sequence, RecoveryCycleId: recoveryCycleId,
            QualificationSessionId: PlcSessionId, RunId: PlcRunId, CycleSequence: 1),
            new StoreDeadline(TimeSpan.FromSeconds(10)), CancellationToken.None);
        Assert.True(result.Committed, result.ReasonCode);
    }

    private static readonly string PlcEndpoint = Hash("v149-migration-plc-endpoint");
    private static readonly string PlcProfileHash = Hash("v149-migration-plc-profile");
    private static readonly string PlcPolicyHash = Hash("v149-migration-plc-policy");
    private static readonly Guid PlcSessionId = Guid.Parse("14900000-0000-0000-0000-000000000001");
    private static readonly Guid PlcRunId = Guid.Parse("14900000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset PlcObservedAt =
        new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
}
