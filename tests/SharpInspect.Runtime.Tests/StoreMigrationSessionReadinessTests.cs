#pragma warning disable CA1416

using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Exercises calibration, preview, manual inspection, recovery-command and network
/// migration gates using signed store histories and software fixtures. Each gate
/// rejects its pending history and accepts its legitimate closure; Unknown remains
/// a refusal. These cases do not qualify a physical station.
/// </summary>
public sealed class StoreMigrationSessionReadinessTests
{
    [Fact]
    [Trait("VerificationId", "V149_S14")]
    public async Task V149_S14_CalibrationSessionBlocksReadinessUntilItsTerminalAndItsAcceptedActionTerminal()
    {
        await using var fixture =
            await CalibrationSessionRuntimeTests.Fixture.CreateAsync(withDevelopmentFixture: true);
        Assert.NotNull(fixture.Options.CalibrationSessions);
        StoreDeadline Deadline() => new(fixture.Options.CommitTimeout);
        await fixture.WaitForHealthySourceAsync();

        // The configured gate is entered on an empty ledger and accepts it.
        RequireMigrationQuiescent(fixture.Options, fixture.Store);

        var (_, started) = await fixture.StartSessionWhenCameraIdleAsync();
        AssertAccepted(started, "calibration session start");

        // Real signed open history: the admission event of the session is
        // Pending until its Restored terminal, so readiness must refuse.
        var sessionId = await WaitForOpenCalibrationSessionAsync(fixture);
        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationCalibrationSessionNotQuiescent");

        // The runtime closes the session with its Restored terminal and the
        // exact terminal of the accepted Exit action; only then is it idle.
        await fixture.ExitAsync(sessionId);
        await WaitUntilAsync(async () =>
                (await fixture.Store.ReadPendingCalibrationActionsAsync(sessionId)).Count == 0,
            "The accepted calibration Exit action never received its durable terminal.");
        RequireMigrationQuiescent(fixture.Options, fixture.Store);
        Assert.Empty(await fixture.Store.ReadOpenCalibrationSessionsAsync());

        // A second session proves the other half of the same projection: an
        // admitted session with an accepted Exit action that has no terminal
        // fact is still open work even after its Restored event is stored.
        var runtimeEpoch = Guid.NewGuid();
        var binding = (await fixture.Store.ReadCameraSetupAsync(fixture.Binding.LogicalRole))
            .State.Binding ?? throw new XunitException("CalibrationCameraBindingMissing");
        var start = new StartCalibrationSessionCommand(Guid.NewGuid(), fixture.User.Invocation,
            fixture.Plan, binding.Revision, binding.RevisionHash,
            ImagingSetupRevisionReference.FromRevision(fixture.Imaging),
            "V149 migration readiness pending action");
        var startInvocation = await fixture.GrantAsync(Permission.RunCalibration,
            start.CorrelationId, start.AuthorizationTarget, AuditedCommandKind.StartCalibrationSession);
        var admitted = await fixture.Authorization.HandleCalibrationCommandAsync(
            start with { Invocation = startInvocation }, runtimeEpoch, Guid.NewGuid(),
            new CalibrationSessionAdmissionInput(binding, fixture.BaselineRequested,
                fixture.BaselineEffective, new string('C', 64)),
            null, null, Deadline(), CancellationToken.None);
        AssertAccepted(admitted.Outcome, "calibration pending-action admission");
        var header = admitted.Header ?? throw new XunitException("CalibrationSessionHeaderMissing");

        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationCalibrationSessionNotQuiescent");

        var exit = new ExitCalibrationSessionCommand(Guid.NewGuid(), fixture.User.Invocation,
            header.SessionId, "V149 migration readiness pending action exit", Cancel: true);
        var exitAuthorization = await fixture.Authorization.HandleCalibrationCommandAsync(
            exit, header.RuntimeEpoch, Guid.NewGuid(), null, header, null, Deadline(),
            CancellationToken.None);
        AssertAccepted(exitAuthorization.Outcome, "calibration pending-action exit");
        var exitFact = exitAuthorization.Admission ??
            throw new XunitException("CalibrationExitActionFactMissing");

        // The Restored terminal closes the session itself while the accepted
        // Exit action deliberately stays without a terminal (crash window).
        var startTerminal = new CommandAuditFact(Guid.NewGuid(), header.AdmissionAttemptId,
            header.Command.CorrelationId, header.RuntimeEpoch, DateTimeOffset.UtcNow,
            AuditedCommandKind.StartCalibrationSession, header.Command.Invocation.Source,
            header.Command.Invocation.PrincipalId, header.InteractiveSessionId,
            header.Command.Invocation.StepUpGrantId, CommandAuditPhase.Failed, null,
            "CalibrationSessionCancelled", header.ActorPrincipalId.ToString("D"));
        var restored = new CalibrationSessionEvent(Guid.NewGuid(), header.SessionId,
            exit.CorrelationId, CalibrationSessionPhase.Restored, CalibrationSessionOutcome.Cancelled,
            "CalibrationSessionCancelled", DateTimeOffset.UtcNow, authorizationCommand: exit);
        var restoredWrite = await fixture.Store.AppendCalibrationEventAsync(restored, startTerminal,
            CancellationToken.None, Deadline());
        Assert.True(restoredWrite.Committed, restoredWrite.ReasonCode);

        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationCalibrationSessionNotQuiescent");

        var exitTerminal = exitFact with
        {
            EventId = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow,
            Phase = CommandAuditPhase.Completed, Disposition = null,
            ReasonCode = "CalibrationSessionCancelled"
        };
        var exitTerminalWrite = await fixture.Store.AppendAsync(exitTerminal, Deadline(),
            CancellationToken.None);
        Assert.True(exitTerminalWrite.Committed, exitTerminalWrite.ReasonCode);

        RequireMigrationQuiescent(fixture.Options, fixture.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S15")]
    public async Task V149_S15_PreviewSessionBlocksReadinessUntilItsFullReplayIsIdle()
    {
        await using var harness =
            await RecipeActivationServiceTests.ActivationHarness.CreateAsync(enablePreview: true);
        Assert.NotNull(harness.Options.PreviewSessions);

        var invocation = harness.ActivationInvocation();
        var preview = (IPreviewSessionService)harness.Runtime;
        await WaitForPreviewSnapshotAsync(preview, invocation,
            snapshot => snapshot.Phase == PreviewSessionPhase.Idle && snapshot.PreviewSessionId is null,
            "PreviewStartupFenceTimeout");

        // The configured gate is entered on an empty ledger and accepts it.
        RequireMigrationQuiescent(harness.Options, harness.Store);

        var sessionId = Guid.NewGuid();
        var start = new StartPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            PreviewDraftReference.FromRevision(harness.Source), null,
            "V149 migration readiness preview");
        AssertAccepted(await harness.Runtime.SubmitAsync(start), "preview session start");
        await harness.WaitForVerifiedAsync();
        var streaming = await WaitForPreviewSnapshotAsync(preview, invocation,
            snapshot => snapshot.Phase == PreviewSessionPhase.Streaming &&
                snapshot.LatestFrame is not null,
            "PreviewStreamingUnavailable");
        Assert.Equal(sessionId, streaming.PreviewSessionId);

        // The active session still owns the exclusive camera and is projected
        // as PreviewSessionPending by the same replay the runtime uses.
        AssertGateRefuses(harness.Options, harness.Store,
            "StoreMigrationPreviewSessionNotQuiescent");

        var exit = new ExitPreviewSessionCommand(Guid.NewGuid(), invocation, sessionId,
            cancel: false, "V149 migration readiness preview close");
        AssertAccepted(await harness.Runtime.SubmitAsync(exit), "preview session exit");
        await harness.WaitForVerifiedAsync();
        var closed = await WaitForPreviewSnapshotAsync(preview, invocation,
            snapshot => snapshot.Phase == PreviewSessionPhase.Closed,
            "PreviewSafeCloseUnavailable");
        Assert.False(closed.RecoveryRequired);

        // A Closed full replay has no active or recovery-required header, so
        // the retained terminal session no longer blocks the migration.
        RequireMigrationQuiescent(harness.Options, harness.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S16")]
    public async Task V149_S16_PendingManualInspectionSessionBlocksReadinessUntilItsClosedTerminal()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            authorizationPolicy: ManualInspectionReadinessPolicy(),
            cameraSetup: new CameraSetupStoreOptions(),
            manualInspections: new ManualInspectionStoreOptions());
        Assert.NotNull(fixture.Options.ManualInspections);
        StoreDeadline Deadline() => new(fixture.Options.CommitTimeout);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);

        // The configured gate is entered on an empty ledger and accepts it.
        RequireMigrationQuiescent(fixture.Options, fixture.Store);

        var document = fixture.Document("V149 manual readiness");
        var saved = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null, document,
            "V149 migration readiness manual draft");
        Assert.True(saved.Saved, saved.ReasonCode);
        var draft = Assert.IsType<RecipeDraftRevision>(saved.Revision);

        // The draft selects exactly one camera role; the manual admission binds
        // the real signed binding revision of that role. No physical camera is
        // opened, configured or triggered by this case.
        _ = await AppendCompletedCameraBindingAsync(fixture, draft.Content.CameraRole);
        var binding = (await fixture.Store.ReadCameraSetupAsync(draft.Content.CameraRole)).State;
        Assert.NotNull(binding.Binding);

        var principalId = Guid.Parse(fixture.Sessions.Current.PrincipalId!);
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            principalId.ToString("D"), fixture.Sessions.Current.SessionId);
        var runtimeEpoch = Guid.NewGuid();
        var start = new StartManualInspectionSessionCommand(Guid.NewGuid(), invocation,
            ManualRecipeSelection.FromDraft(draft), null, "V149 migration readiness manual start");
        var started = await fixture.Authorization.HandleManualInspectionCommandAsync(start,
            runtimeEpoch, Guid.NewGuid(), new ManualInspectionAdmissionInput(draft, null, binding, null),
            null, null, Deadline(), CancellationToken.None);
        AssertAccepted(started.Outcome, "manual inspection start");
        var header = started.Header ?? throw new XunitException("ManualInspectionHeaderMissing");
        var startFact = started.CommandFact ?? throw new XunitException("ManualInspectionStartFactMissing");

        // The admitted session's last event is an active (non-terminal) header,
        // which the shared projection reports as ManualInspectionPending.
        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationManualInspectionNotQuiescent");

        var exit = new ExitManualInspectionSessionCommand(Guid.NewGuid(), invocation,
            header.SessionId, ManualInspectionExitMode.Graceful, "V149 migration readiness manual exit");
        var exited = await fixture.Authorization.HandleManualInspectionCommandAsync(exit,
            runtimeEpoch, Guid.NewGuid(), null, header, null, Deadline(), CancellationToken.None);
        AssertAccepted(exited.Outcome, "manual inspection exit");
        var exitHeader = exited.Header ?? throw new XunitException("ManualInspectionExitHeaderMissing");
        var exitFact = exited.CommandFact ?? throw new XunitException("ManualInspectionExitFactMissing");
        Assert.Equal(startFact.AttemptId, exitHeader.AttemptId);

        // The owning runtime writes this Close terminal after physical
        // restoration; this case uses the identical bounded completion
        // contract directly, without any camera or algorithm instance.
        var closed = await fixture.Authorization.CompleteManualInspectionCommandAsync(exit,
            exitHeader, exitFact, new ManualInspectionCompletion(true, "ManualInspectionExited",
                ManualInspectionSessionPhase.Closed, ManualInspectionRestorationState.NotRequired,
                CompleteOriginalStart: true, CompleteCommand: true), Deadline());
        AssertAccepted(closed.Outcome, "manual inspection close");
        Assert.Equal(ManualInspectionSessionPhase.Closed, closed.Header!.Phase);

        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(fixture.Store);
        RequireMigrationQuiescent(fixture.Options, fixture.Store);
        var current = await new SqliteManualInspectionQuery(fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal("ManualInspectionIdle", current.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V149_S17")]
    public async Task V149_S17_AdmittedCameraRecoveryCycleBlocksReadinessUntilItsTerminal()
    {
        await using var fixture = await CameraRecoveryTestFixture.CreateAsync();
        Assert.NotNull(fixture.Options.CameraRecovery);
        StoreDeadline Deadline() => new(fixture.Options.CommitTimeout);

        // The configured gate is entered on an empty ledger and accepts it.
        RequireMigrationQuiescent(fixture.Options, fixture.Store);

        var signedIn = await fixture.SignInAsync();
        const string logicalRole = "TopCamera";
        var correlation = Guid.NewGuid();
        var expectedCycle = Guid.NewGuid();
        var grant = await fixture.IssueRecoveryGrantAsync(signedIn.Invocation, correlation, logicalRole);
        var command = fixture.RecoveryCommand(signedIn.Invocation with { StepUpGrantId = grant },
            correlation, logicalRole, expectedCycle, "CycleExhausted");

        // The real authorization seam commits one durable accepted admission.
        // The physical recovery work and its terminal belong to the caller, so
        // this exact state is the crash-between-admission-and-terminal case.
        var admission = await fixture.Authorization.HandleCameraRecoveryCycleStartAsync(command,
            Guid.NewGuid(), Guid.NewGuid(), null, Deadline(), CancellationToken.None);
        AssertAccepted(admission.Outcome, "camera recovery cycle admission");
        var admissionFact = admission.Admission ??
            throw new XunitException("CameraRecoveryAdmissionFactMissing");

        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationCameraRecoveryNotQuiescent");

        var terminal = await fixture.Authorization.CompleteCameraRecoveryCycleStartAsync(
            admissionFact, Guid.NewGuid(), started: true, "RecoveryStarted", Deadline(),
            CancellationToken.None);
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        // The terminal row closes the accepted Start command. It does not assert
        // that physical camera recovery has completed.
        RequireMigrationQuiescent(fixture.Options, fixture.Store);
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_recovery_terminal_events;"));
    }

    [Fact]
    [Trait("VerificationId", "V149_S18")]
    public async Task V149_S18_CameraNetworkPendingAdmissionBlocksReadinessUntilItsTerminal()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        Assert.NotNull(fixture.Options.CameraNetwork);
        StoreDeadline Deadline() => new(fixture.Options.CommitTimeout);

        // The configured gate is entered on an empty ledger and accepts it.
        RequireMigrationQuiescent(fixture.Options, fixture.Store);

        var admitted = await fixture.AdmitAsync();
        Assert.Equal(CameraNetworkMaintenanceState.Pending,
            (await fixture.Persistence.ReadLatestAsync(fixture.Target))!.State);

        // An admission without its terminal or rejected record is pending work.
        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationCameraNetworkNotQuiescent");

        var observed = admitted.Request.Requested;
        var succeeded = new CameraNetworkSnapshot(admitted.Request.OperationId, admitted.Request.Target,
            CameraNetworkMaintenanceState.Succeeded, admitted.Previous, admitted.Request.Requested,
            observed, true, "CameraNetworkChanged", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission, succeeded,
            Deadline());
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        RequireMigrationQuiescent(fixture.Options, fixture.Store);
    }

    [Fact]
    [Trait("VerificationId", "V149_S18")]
    public async Task V149_S18_CameraNetworkUnknownTerminalBlocksReadinessWithoutAPendingAdmission()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        StoreDeadline Deadline() => new(fixture.Options.CommitTimeout);

        var admitted = await fixture.AdmitAsync();
        var unknown = new CameraNetworkSnapshot(admitted.Request.OperationId, admitted.Request.Target,
            CameraNetworkMaintenanceState.Unknown, admitted.Previous, admitted.Request.Requested,
            null, false, "CameraNetworkStateUnknown", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission, unknown,
            Deadline());
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        // No admission is pending here, yet an Unknown terminal is the durable
        // marker that the station network state was never established and still
        // needs reconciliation, so the ledger is not treated as idle.
        AssertGateRefuses(fixture.Options, fixture.Store,
            "StoreMigrationCameraNetworkNotQuiescent");
    }

    /// <summary>
    /// Runs the stored readiness replay on a real read-only SQLite snapshot of
    /// the live store, exactly as the public maintenance entry does.
    /// </summary>
    private static void RequireMigrationQuiescent(ProductionStoreOptions options,
        SqliteCommandStore store)
    {
        using var read = SqliteNative.Open(options.DatabasePath, readOnly: true);
        store.RequireMigrationQuiescent(read.Handle!, new StoreDeadline(TimeSpan.FromSeconds(10)));
    }

    private static void AssertGateRefuses(ProductionStoreOptions options,
        SqliteCommandStore store, string reason)
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => RequireMigrationQuiescent(options, store));
        Assert.Equal(reason, refused.Message);
    }

    private static void AssertAccepted(RuntimeCommandOutcome outcome, string operation)
    {
        Assert.True(outcome.Disposition == CommandDisposition.Accepted,
            operation + ": " + outcome.ReasonCode + "; audit=" + outcome.Audit);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string reason)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(Stopwatch.Frequency * 20d);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }
        throw new XunitException(reason);
    }

    private static async Task<Guid> WaitForOpenCalibrationSessionAsync(
        CalibrationSessionRuntimeTests.Fixture fixture)
    {
        Guid? sessionId = null;
        await WaitUntilAsync(async () =>
        {
            var open = await fixture.Store.ReadOpenCalibrationSessionsAsync();
            sessionId = open.Count == 1 ? open[0].State.SessionId : null;
            return sessionId.HasValue;
        }, "The admitted calibration session never became durable open history.");
        return sessionId!.Value;
    }

    private static async Task<PreviewSessionSnapshot> WaitForPreviewSnapshotAsync(
        IPreviewSessionService preview, CommandInvocation invocation,
        Func<PreviewSessionSnapshot, bool> predicate, string reason)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 30d);
        PreviewSessionSnapshot? last = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var read = await preview.GetSnapshotAsync(invocation);
            Assert.True(read.Available, read.ReasonCode);
            var snapshot = read.Snapshot ?? throw new XunitException("PreviewSessionSnapshotMissing");
            last = snapshot;
            if (predicate(snapshot)) return snapshot;
            await Task.Delay(25);
        }
        throw new XunitException(reason + ": " + last?.Phase + "/" + last?.ReasonCode);
    }

    /// <summary>
    /// Writes one completed camera binding through the real signed
    /// identity/command/camera-setup transaction. The target is a test fixture
    /// input; no camera hardware is contacted.
    /// </summary>
    private static async Task<CameraBindingRevision> AppendCompletedCameraBindingAsync(
        RecipeDraftStorageTests.Fixture fixture, string logicalRole)
    {
        var store = fixture.Store;
        var stationId = fixture.Options.AuditIntegrityPolicy!.StationId;
        var principalId = Guid.Parse(fixture.Sessions.Current.PrincipalId!);
        var sessionId = fixture.Sessions.Current.SessionId!.Value;
        var revision = (await store.ReadIdentityAsync(CancellationToken.None)).EnumerateAccounts()
            .Single(account => account.PrincipalId == principalId).AuthorizationRevision;
        var operationId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var target = new CameraBindingTarget(new CameraProviderIdentity("V149.Migration.Provider",
            "1", "V149.Migration.Adapter", "1"), "v149-migration-readiness-device-001");
        var cameraEvent = new CameraSetupEvent(0, Guid.NewGuid(), operationId, logicalRole,
            AuditedCommandKind.RebindCamera, CameraSetupEventPhase.Completed, 1, target: target,
            succeeded: true, reasonCode: "CameraRebindCompleted",
            changeReason: "V149 migration readiness binding", actorPrincipalId: principalId,
            sessionId: sessionId, authorAuthorizationRevision: revision, recordedAtUtc: recordedAt);
        cameraEvent = cameraEvent with
        {
            RevisionHash = CameraSetupStorageCodec.ComputeRevisionHash(cameraEvent, 1, null, target)
        };
        var grant = Guid.NewGuid();
        var admission = new IdentityAuditEvent(Guid.NewGuid(),
            IdentityEventKind.CameraSetupActionAuthorized, recordedAt, stationId, principalId,
            null, null, null, "CameraRebindAdmitted", ActorPrincipalId: principalId,
            CommandCorrelationId: operationId, StepUpGrantId: grant,
            RequiredPermission: Permission.ManageCameraBindings.ToString(),
            AuthorizationRevision: revision, ManagementReason: null, ActionTargetId: logicalRole,
            BoundCommandCorrelationId: operationId,
            ActionCommandKind: AuditedCommandKind.RebindCamera.ToString(), OperationId: operationId,
            SessionId: sessionId);
        var completedIdentity = admission with
        {
            EventId = Guid.NewGuid(), Kind = IdentityEventKind.CameraSetupOperationCompleted,
            ReasonCode = "CameraRebindCompleted"
        };
        var attempt = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var outcome = new CommandAuditFact(Guid.NewGuid(), attempt, operationId, epoch, recordedAt,
            AuditedCommandKind.RebindCamera, CommandSource.PhysicalConsole,
            principalId.ToString("D"), sessionId, grant, CommandAuditPhase.Outcome,
            CommandDisposition.Accepted, "CameraRebindAdmitted", principalId.ToString("D"));
        var terminal = outcome with
        {
            EventId = Guid.NewGuid(), Phase = CommandAuditPhase.Completed, Disposition = null,
            ReasonCode = "CameraRebindCompleted"
        };
        var write = await store.UpdateIdentityAsync(state => new IdentityUpdate(new object(),
            new[] { admission, completedIdentity }, new[] { outcome, terminal },
            CameraEvents: new[] { cameraEvent }), CancellationToken.None);
        Assert.True(write.Committed, write.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
        return (await store.ReadCameraSetupAsync(logicalRole)).State.Binding!;
    }

    /// <summary>
    /// Development role bundles extended with the explicit manual-inspection
    /// and draft-authoring permissions this case needs; manual commands do not
    /// require a Step-Up under this policy.
    /// </summary>
    private static AuthorizationPolicy ManualInspectionReadinessPolicy()
    {
        var roles = AuthorizationPolicy.Development.RoleBundles.ToDictionary(pair => pair.Key,
            pair => pair.Key == HumanRoleBundle.Administrator
                ? pair.Value.Concat(new[] { Permission.EditRecipeDraft, Permission.RunManualInspection })
                : pair.Value.AsEnumerable());
        return new AuthorizationPolicy("V149.MigrationReadiness.Manual", "1", roles,
            AuthorizationPolicy.Development.StepUpPermissions);
    }
}

#pragma warning restore CA1416
