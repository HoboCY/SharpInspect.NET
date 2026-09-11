using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Algorithms;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Frames;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Manual;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V144_R01_ColdRestartRecoveryPersistsCompletionAndLeavesStationDisarmed()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();

        var command = await scenario.CreateCommandAsync("ColdRestartManualRecovery");
        var authorized = await scenario.AuthorizeAsync(command);
        var result = await scenario.Runtime.SubmitAsync(authorized);

        Assert.True(result.Disposition == CommandDisposition.Accepted, result.ReasonCode);
        Assert.Equal("ProductionRecoveryAuthorized", result.ReasonCode);

        var submittedState = await scenario.Runtime.GetSnapshotAsync();
        Assert.True(submittedState.LastCommand?.State != OperationState.Failed,
            submittedState.LastCommand?.ReasonCode);

        await WaitRecoveryStateAsync(scenario.Runtime, state =>
            state.Recovery == RecoveryState.None &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready,
            "Production recovery did not complete after cold restart");

        var history = await new SqliteProductionInspectionHistoryQuery(
            scenario.Harness.Fixture.Options).ReadAsync(scenario.InspectionId);
        Assert.True(history.Available, history.ReasonCode);
        Assert.False(history.RecoveryRequired);
        Assert.NotNull(history.Latest);
        Assert.Equal(ProductionInspectionEventKind.RecoveryCompleted, history.Latest!.Kind);
        Assert.NotNull(history.Latest.Recovery);
        Assert.Equal(ProductionRecoveryOutcome.Completed, history.Latest.Recovery!.Outcome);
        Assert.True(scenario.Safety.Observations > 0);
        Assert.True(scenario.Peer.RequestCount > 0);
        Assert.False(scenario.Peer.RuntimeResultValid);
        Assert.False(scenario.Peer.ControllerResultAck);
    }

    [Fact]
    public async Task V144_R02_MissingGrantIsDurablyRejectedWithoutOpeningPlcConnection()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();
        var command = await scenario.CreateCommandAsync("MissingGrantRecovery");
        var connections = scenario.Peer.ConnectionCount;
        var requests = scenario.Peer.RequestCount;

        var result = await scenario.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Contains(result.ReasonCode, new[] { "StepUpRequired", "StepUpInvalid" });
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(connections, scenario.Peer.ConnectionCount);
        Assert.Equal(requests, scenario.Peer.RequestCount);

        var recovery = await new SqliteProductionRecoveryHistoryQuery(
            scenario.Harness.Fixture.Options).ReadAsync(scenario.InspectionId);
        Assert.True(recovery.Available, recovery.ReasonCode);
        Assert.True(recovery.RecoveryRequired);
    }

    [Fact]
    public async Task V144_R03_UnsafePhysicalSafetyObservationRejectsBeforePlcCleanup()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();
        scenario.Safety.Status = ProductionRecoverySafetyObservationStatus.LineNotStopped;
        var command = await scenario.CreateCommandAsync("UnsafeLineRecovery");
        var authorized = await scenario.AuthorizeAsync(command);
        var connections = scenario.Peer.ConnectionCount;
        var requests = scenario.Peer.RequestCount;

        var result = await scenario.Runtime.SubmitAsync(authorized);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal("ProductionRecoverySafetyLineNotStopped", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(connections, scenario.Peer.ConnectionCount);
        Assert.Equal(requests, scenario.Peer.RequestCount);

        var recovery = await new SqliteProductionRecoveryHistoryQuery(
            scenario.Harness.Fixture.Options).ReadAsync(scenario.InspectionId);
        Assert.True(recovery.Available, recovery.ReasonCode);
        Assert.True(recovery.RecoveryRequired);
    }

    [Fact]
    public async Task V144_R04_MissingPermissionIsDurablyRejectedWithoutOpeningPlcConnection()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(
            allowRecovery: false);
        var command = await scenario.CreateCommandAsync("MissingPermissionRecovery");
        var connections = scenario.Peer.ConnectionCount;
        var requests = scenario.Peer.RequestCount;

        var result = await scenario.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.True(result.ReasonCode == "PermissionDenied", result.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, result.Audit);
        Assert.Equal(connections, scenario.Peer.ConnectionCount);
        Assert.Equal(requests, scenario.Peer.RequestCount);
    }

    [Fact]
    public async Task V144_R05_CallerCancellationAfterAuthorizationDoesNotCancelOwnedCleanup()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();
        var command = await scenario.CreateCommandAsync("CancelledCallerRecovery");
        var authorized = await scenario.AuthorizeAsync(command);
        var requestsBeforeRecovery = scenario.Peer.RequestCount;
        scenario.Peer.HoldInitialRuntimeRead = true;
        using var cancellation = new CancellationTokenSource();

        var pending = scenario.Runtime.SubmitAsync(authorized, cancellation.Token).AsTask();
        await scenario.Peer.WaitForInitialRuntimeReadAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(scenario.Peer.RequestCount > requestsBeforeRecovery,
            "The recovery runtime-read barrier was already completed before this attempt");
        cancellation.Cancel();
        scenario.Peer.ReleaseInitialRuntimeRead();

        var result = await pending;
        Assert.Equal(CommandDisposition.Accepted, result.Disposition);
        Assert.Equal("ProductionRecoveryAuthorized", result.ReasonCode);
        await WaitRecoveryStateAsync(scenario.Runtime, state =>
            state.Recovery == RecoveryState.None &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready,
            "Owned recovery operation was cancelled with its caller");

        var history = await new SqliteProductionInspectionHistoryQuery(
            scenario.Harness.Fixture.Options).ReadAsync(scenario.InspectionId);
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(ProductionInspectionEventKind.RecoveryCompleted, history.Latest?.Kind);
    }

    [Fact]
    public async Task V144_R06_TwoRetainedAckFailuresRemainVerifiableAndFreshGrantRetriesSameSession()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(retainedAck: true);
        scenario.Peer.AutoClearAcknowledge = false;

        var first = await scenario.AuthorizeAsync(
            await scenario.CreateCommandAsync("RetainedAckFirstRecovery"));
        var firstResult = await scenario.Runtime.SubmitAsync(first);
        Assert.True(firstResult.Disposition == CommandDisposition.Accepted, firstResult.ReasonCode);

        await WaitRecoveryStateAsync(scenario.Runtime, state =>
            state.Recovery == RecoveryState.Required &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready,
            "Retained ACK did not keep recovery blocked");
        Assert.True(scenario.Peer.ControllerResultAck);

        var second = await scenario.AuthorizeAsync(
            await scenario.CreateCommandAsync("RetainedAckSecondRecovery"));
        var secondResult = await scenario.Runtime.SubmitAsync(second);
        Assert.True(secondResult.Disposition == CommandDisposition.Accepted, secondResult.ReasonCode);
        var afterSecond = await new SqliteProductionRecoveryHistoryQuery(
            scenario.Harness.Fixture.Options).ReadAsync(scenario.InspectionId);
        Assert.True(afterSecond.Available, afterSecond.ReasonCode);
        Assert.True(afterSecond.RecoveryRequired);
        Assert.True(scenario.Peer.ControllerResultAck);

        scenario.Peer.SetInitialControllerAck(false);
        scenario.Peer.AutoClearAcknowledge = true;
        var retry = await scenario.AuthorizeAsync(
            await scenario.CreateCommandAsync("RetainedAckRetryRecovery"));
        var retryResult = await scenario.Runtime.SubmitAsync(retry);

        Assert.True(retryResult.Disposition == CommandDisposition.Accepted, retryResult.ReasonCode);
        await WaitRecoveryStateAsync(scenario.Runtime, state =>
            state.Recovery == RecoveryState.None &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready,
            "Fresh recovery grant did not complete retry");

        var page = await new SqliteProductionInspectionHistoryQuery(
            scenario.Harness.Fixture.Options).QueryAsync(new(InspectionId: scenario.InspectionId,
                PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Contains(page.Events, value =>
            value.Kind == ProductionInspectionEventKind.RecoveryCompleted &&
            value.Recovery?.Outcome == ProductionRecoveryOutcome.Completed);
        Assert.True(page.Events.Count(value =>
            value.Kind == ProductionInspectionEventKind.RecoveryRequired) == 1);

        var communication = new SqlitePlcCommunicationHistoryQuery(
            scenario.Harness.Fixture.Options);
        var firstCommunication = await communication.QueryAsync(new(
            RecoveryCycleId: firstResult.AttemptId,
            RunId: scenario.InspectionId, PageSize: 128));
        var retryCommunication = await communication.QueryAsync(new(
            RecoveryCycleId: retryResult.AttemptId,
            RunId: scenario.InspectionId, PageSize: 128));
        Assert.True(firstCommunication.Available, firstCommunication.ReasonCode);
        Assert.True(retryCommunication.Available, retryCommunication.ReasonCode);
        Assert.NotEmpty(firstCommunication.Events);
        Assert.NotEmpty(retryCommunication.Events);
        Assert.All(firstCommunication.Events, value =>
            Assert.Equal(firstResult.AttemptId, value.RecoveryCycleId));
        Assert.All(retryCommunication.Events, value =>
            Assert.Equal(retryResult.AttemptId, value.RecoveryCycleId));
        Assert.NotEqual(firstResult.AttemptId, retryResult.AttemptId);
        Assert.NotEqual(firstResult.AttemptId, secondResult.AttemptId);
        Assert.NotEqual(secondResult.AttemptId, retryResult.AttemptId);
    }

    [Fact]
    public async Task V144_R07_ReservedLocalStopRejectsRecoveryBeforeSafetyOrPhysicalWork()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();
        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("StopBeforeRecovery"));
        var gate = GetCommandGate(scenario.Runtime);
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        Task<RuntimeCommandOutcome>? stop = null;
        try
        {
            stop = scenario.Runtime.SubmitAsync(new GracefulProductionStopCommand(
                Guid.NewGuid(), scenario.RuntimeContext.Invocation())).AsTask();
            await WaitConditionAsync(() => ReadPendingLocalStops(scenario.Runtime) == 1,
                "Local stop did not reserve its priority while the command gate was held");
            var requests = scenario.Peer.RequestCount;
            var connections = scenario.Peer.ConnectionCount;
            var observations = scenario.Safety.Observations;

            var rejected = await scenario.Runtime.SubmitAsync(command);

            Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
            Assert.Equal("ProductionRecoveryLocalStopInProgress", rejected.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
            Assert.Equal(observations, scenario.Safety.Observations);
            Assert.Equal(requests, scenario.Peer.RequestCount);
            Assert.Equal(connections, scenario.Peer.ConnectionCount);
        }
        finally
        {
            gate.Release();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    [Theory]
    [InlineData(ProductionInspectionEventKind.Admitted)]
    [InlineData(ProductionInspectionEventKind.CoreCommitted)]
    [InlineData(ProductionInspectionEventKind.PublicationPrepared)]
    [InlineData(ProductionInspectionEventKind.ResultValidRaised)]
    [InlineData(ProductionInspectionEventKind.ResultAcknowledged)]
    [InlineData(ProductionInspectionEventKind.ResultValidCleared)]
    public async Task V144_R08_EachDurableHandshakeBoundaryRequiresManualRecovery(
        ProductionInspectionEventKind boundary)
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(coldBoundary: boundary);
        var query = new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options);
        var before = await query.ReadAsync(scenario.InspectionId);
        Assert.True(before.Available, before.ReasonCode);
        Assert.Equal(boundary, before.Latest!.Kind);
        Assert.True(before.RecoveryRequired);
        Assert.Equal(0, scenario.Peer.ResultValidHighCount);
        var expectedCoreHash = before.Latest.Core?.ContentHash;
        var expectedPayloadHash = before.Latest.Core?.PlcPayload?.ContentHash;

        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("BoundaryManualRecovery"));
        var accepted = await scenario.Runtime.SubmitAsync(command);
        Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
        await WaitRecoveryStateAsync(scenario.Runtime, state => state.Recovery == RecoveryState.None &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready,
            "Manual recovery failed at boundary " + boundary);

        var after = await query.ReadAsync(scenario.InspectionId);
        Assert.True(after.Available, after.ReasonCode);
        Assert.False(after.RecoveryRequired);
        Assert.Equal(ProductionInspectionEventKind.RecoveryCompleted, after.Latest!.Kind);
        Assert.Equal(expectedCoreHash, after.Latest.Core?.ContentHash);
        Assert.Equal(expectedPayloadHash, after.Latest.Core?.PlcPayload?.ContentHash);
        Assert.Equal(0, scenario.Peer.ResultValidHighCount);
        var events = await query.QueryAsync(new(InspectionId: scenario.InspectionId, PageSize: 128));
        Assert.True(events.Available, events.ReasonCode);
        Assert.DoesNotContain(events.Events, value => value.Kind == ProductionInspectionEventKind.FaultTerminated);
        Assert.DoesNotContain(events.Events, value => value.Kind == ProductionInspectionEventKind.AcknowledgementReset);
    }

    [Fact]
    public async Task V144_R09_UnavailableAuditWriterRejectsBeforeAnyPhysicalCleanup()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();
        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("AuditUnavailableRecovery"));
        var requests = scenario.Peer.RequestCount;
        var connections = scenario.Peer.ConnectionCount;
        await scenario.Harness.Fixture.Store.DisposeAsync();

        var result = await scenario.Runtime.SubmitAsync(command);

        Assert.Equal(CommandDisposition.Rejected, result.Disposition);
        Assert.Equal(AuditPersistence.Unavailable, result.Audit);
        Assert.Equal(requests, scenario.Peer.RequestCount);
        Assert.Equal(connections, scenario.Peer.ConnectionCount);
        var state = await scenario.Runtime.GetSnapshotAsync();
        Assert.False(state.Ready);
        Assert.NotEqual(RecoveryState.None, state.Recovery);
        var history = await new SqliteProductionInspectionHistoryQuery(scenario.Harness.Fixture.Options)
            .ReadAsync(scenario.InspectionId);
        Assert.True(history.Available, history.ReasonCode);
        Assert.True(history.RecoveryRequired);
        Assert.NotEqual(ProductionInspectionEventKind.RecoveryCompleted, history.Latest!.Kind);
    }

    [Fact]
    public async Task V144_R10_LogoutAfterAcceptedRecoveryDoesNotRevokeOwnedCleanup()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync();
        var command = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("LogoutAfterRecoveryAdmission"));
        scenario.Peer.HoldInitialRuntimeRead = true;
        var requests = scenario.Peer.RequestCount;
        var pending = scenario.Runtime.SubmitAsync(command).AsTask();
        await scenario.Peer.WaitForInitialRuntimeReadAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(scenario.Peer.RequestCount > requests);
        await scenario.RuntimeContext.LogoutAsync();
        scenario.Peer.ReleaseInitialRuntimeRead();

        var accepted = await pending;
        Assert.True(accepted.Disposition == CommandDisposition.Accepted, accepted.ReasonCode);
        await WaitRecoveryStateAsync(scenario.Runtime, state => state.Recovery == RecoveryState.None &&
            state.ArmState == ProductionArmState.Disarmed && !state.Ready,
            "Logout incorrectly revoked the already accepted recovery");
        var read = await new SqliteProductionRecoveryHistoryQuery(scenario.Harness.Fixture.Options)
            .ReadAsync(scenario.InspectionId);
        Assert.True(read.Available, read.ReasonCode);
        Assert.False(read.RecoveryRequired);
        Assert.Equal(accepted.AttemptId, read.Latest!.Recovery!.CommandAttemptId);
        Assert.Equal(command.Invocation!.SessionId, read.Latest.Recovery.ActorSessionId);
    }

    [Fact]
    public async Task V144_R11_StaleRejectedRetryDoesNotCorruptPendingRecoveryAudit()
    {
        await using var scenario = await ProductionRecoveryScenario.CreateAsync(retainedAck: true);
        var first = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("FirstRetainedAck"));
        var accepted = await scenario.Runtime.SubmitAsync(first);
        Assert.Equal(CommandDisposition.Accepted, accepted.Disposition);
        var current = await scenario.CreateCommandAsync("StaleRecoveryRequest");
        var stale = new ManualProductionRecoveryCommand(current.CorrelationId, current.Invocation,
            current.InspectionId, new string('E', 64), current.ReasonCode,
            current.Disposition, current.DispositionNote);
        var authorized = await scenario.AuthorizeAsync(stale);
        var connections = scenario.Peer.ConnectionCount;

        var rejected = await scenario.Runtime.SubmitAsync(authorized);

        Assert.Equal(CommandDisposition.Rejected, rejected.Disposition);
        Assert.Equal("ProductionRecoveryTargetStale", rejected.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, rejected.Audit);
        Assert.Equal(connections, scenario.Peer.ConnectionCount);
        var cold = await new SqliteProductionRecoveryHistoryQuery(scenario.Harness.Fixture.Options)
            .ReadAsync(scenario.InspectionId);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.True(cold.RecoveryRequired);

        scenario.Peer.SetInitialControllerAck(false);
        scenario.Peer.AutoClearAcknowledge = true;
        var fresh = await scenario.AuthorizeAsync(await scenario.CreateCommandAsync("FreshAfterStaleRetry"));
        var retried = await scenario.Runtime.SubmitAsync(fresh);
        Assert.Equal(CommandDisposition.Accepted, retried.Disposition);
        await WaitRecoveryStateAsync(scenario.Runtime, state => state.Recovery == RecoveryState.None,
            "Rejected stale request poisoned subsequent recovery");
    }

    private static async Task WaitRecoveryStateAsync(
        StationRuntime runtime,
        Func<StationStateSnapshot, bool> predicate,
        string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        StationStateSnapshot? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await runtime.GetSnapshotAsync();
            if (predicate(last))
                return;
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(
            reason + ": " + last?.Recovery + "/" + last?.ArmState + "/" +
            last?.LastCommand?.ReasonCode + "/" +
            string.Join(",", last?.AdmissionBlockers.AsEnumerable() ?? Array.Empty<string>()));
    }

    private sealed class ProductionRecoveryScenario : IAsyncDisposable
    {
        private readonly ProductionTestIssuer _issuer;
        private bool _disposed;

        private ProductionRecoveryScenario(ModbusQualificationTestServer peer,
            ManualHarness harness, ProductionRecoveryRuntimeContext runtime,
            ProductionInspectionOptions productionOptions, Guid inspectionId,
            FixtureRecoverySafetyProvider safety, ProductionTestIssuer issuer)
        {
            Peer = peer;
            Harness = harness;
            RuntimeContext = runtime;
            ProductionOptions = productionOptions;
            InspectionId = inspectionId;
            Safety = safety;
            _issuer = issuer;
        }

        internal ModbusQualificationTestServer Peer { get; }
        internal ManualHarness Harness { get; }
        internal ProductionRecoveryRuntimeContext RuntimeContext { get; }
        internal StationRuntime Runtime => RuntimeContext.Runtime;
        internal ProductionInspectionOptions ProductionOptions { get; }
        internal Guid InspectionId { get; }
        internal FixtureRecoverySafetyProvider Safety { get; }

        internal static async Task<ProductionRecoveryScenario> CreateAsync(
            bool retainedAck = false, bool allowRecovery = true,
            ProductionInspectionEventKind? coldBoundary = null)
        {
            var peer = ModbusQualificationTestServer.Start();
            ManualHarness? harness = null;
            ProductionRecoveryRuntimeContext? runtime = null;
            ProductionTestIssuer? issuer = null;
            try
            {
                peer.HoldFirstPayloadWrite = true;
                harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
                    productionPeer: peer, allowProductionRecovery: allowRecovery,
                    enableProductionRecovery: true);
                issuer = new ProductionTestIssuer();
                await PrepareProductionAsync(harness, issuer);
                Guid inspectionId;
                if (coldBoundary is { } boundary)
                {
                    // Model abrupt process loss at each durable handshake boundary.
                    // Real writer transactions create the fixture; no PLC operation is replayed.
                    await harness.StopRuntimePreservingFixtureAsync();
                    var admission = await BuildAdmissionAsync(harness);
                    var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(
                        new(admission), new StoreDeadline(TimeSpan.FromSeconds(4)));
                    Assert.True(admitted.Committed, admitted.ReasonCode);
                    inspectionId = admission.InspectionId;
                    if (boundary != ProductionInspectionEventKind.Admitted)
                    {
                        var coreWrite = await harness.Fixture.Store.CommitProductionInspectionCoreAsync(
                            new(TimeoutCore(admitted.Admission!)), new StoreDeadline(TimeSpan.FromSeconds(4)));
                        Assert.True(coreWrite.Committed, coreWrite.ReasonCode);
                        if (boundary != ProductionInspectionEventKind.CoreCommitted)
                            foreach (var phase in new[] { ProductionInspectionEventKind.PublicationPrepared,
                                ProductionInspectionEventKind.ResultValidRaised,
                                ProductionInspectionEventKind.ResultAcknowledged,
                                ProductionInspectionEventKind.ResultValidCleared })
                            {
                                var appended = await harness.Fixture.Store.AppendProductionInspectionEventAsync(
                                    new(inspectionId, phase, "V144SyntheticColdBoundary",
                                        DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
                                    new StoreDeadline(TimeSpan.FromSeconds(4)));
                                Assert.True(appended.Committed, appended.ReasonCode);
                                if (phase == boundary) break;
                            }
                    }
                }
                else
                {
                    await ArmProductionAsync(harness);
                    await WaitProductionAsync(harness, state => state.Ready,
                        "Production did not become ready before recovery fault");
                    peer.RaiseTrigger(61, 1);
                    await peer.WaitForPayloadWriteAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    var core = await WaitForProductionCoreAsync(harness);
                    inspectionId = core.InspectionId;
                    await WaitProductionAsync(harness, state =>
                        state.Recovery == RecoveryState.Required &&
                        state.ArmState == ProductionArmState.Disarmed && !state.Ready,
                        "Production did not enter recovery-required state");
                }
                // Let the real payload request timeout close its client socket. The
                // server remains reconnectable for the later manual recovery; the
                // held response is released only after the fault is durable.
                peer.ReleasePayloadWrite();
                // The fixture's independent stopped-line condition includes
                // withdrawing the PLC trigger; Runtime never writes that input.
                peer.SetTrigger(false);
                if (retainedAck)
                {
                    peer.SetInitialControllerAck(true);
                    peer.AutoClearAcknowledge = false;
                }

                var productionOptions = harness.Service<ProductionInspectionOptions>();
                var oldSessionId = harness.Fixture.Sessions.Current.SessionId;
                Assert.True(oldSessionId.HasValue);
                var logout = await harness.Fixture.Sessions.LogoutAsync(oldSessionId.Value);
                Assert.True(logout.Succeeded, logout.ReasonCode);
                await harness.Fixture.WaitForVerifiedAsync();
                await harness.StopRuntimePreservingFixtureAsync();
                await harness.Fixture.RestartStoreAsync();

                var safety = FixtureRecoverySafetyProvider.Create(productionOptions);
                var clock = harness.Service<VirtualCameraClock>();
                runtime = await ProductionRecoveryRuntimeContext.CreateAsync(harness,
                    productionOptions, safety, clock);
                await runtime.WaitForRecoveryBlockerAsync();
                return new ProductionRecoveryScenario(peer, harness, runtime,
                    productionOptions, inspectionId, safety, issuer);
            }
            catch
            {
                if (runtime is not null) await runtime.DisposeAsync();
                if (harness is not null) await harness.DisposeAsync();
                issuer?.Dispose();
                await peer.DisposeAsync();
                throw;
            }
        }

        internal async Task<ManualProductionRecoveryCommand> CreateCommandAsync(
            string reasonCode)
        {
            var current = await new SqliteProductionInspectionHistoryQuery(
                Harness.Fixture.Options).ReadAsync(InspectionId);
            Assert.True(current.Available, current.ReasonCode);
            Assert.True(current.RecoveryRequired, current.ReasonCode);
            var latest = Assert.IsType<ProductionInspectionHistoryEvent>(current.Latest);
            return new ManualProductionRecoveryCommand(Guid.NewGuid(), RuntimeContext.Invocation(),
                InspectionId, latest.ContentHash, reasonCode, PartDisposition.Isolated,
                "V144 recovery test disposition");
        }

        internal async Task<ManualProductionRecoveryCommand> AuthorizeAsync(
            ManualProductionRecoveryCommand command)
        {
            var invocation = RuntimeContext.Invocation();
            var result = await RuntimeContext.Authorization.ReauthenticateAsync(new StepUpRequest(
                Guid.NewGuid(), invocation, new StepUpBinding(Permission.ManualRecovery,
                    command.CorrelationId, command.AuthorizationTarget,
                    AuditedCommandKind.ManualProductionRecovery), Harness.Fixture.Password));
            Assert.True(result.Succeeded, result.ReasonCode);
            Assert.NotNull(result.GrantId);
            await Harness.Fixture.WaitForVerifiedAsync();
            return command with { Invocation = invocation with { StepUpGrantId = result.GrantId } };
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await RuntimeContext.DisposeAsync();
            await Harness.DisposeAsync();
            _issuer.Dispose();
            await Peer.DisposeAsync();
        }
    }

    private sealed class ProductionRecoveryRuntimeContext : IAsyncDisposable
    {
        private readonly InteractiveSessionService _sessions;
        private readonly LocalAuthorizationService _authorization;
        private readonly StationRuntime _runtime;
        private bool _disposed;

        private ProductionRecoveryRuntimeContext(InteractiveSessionService sessions,
            LocalAuthorizationService authorization, StationRuntime runtime)
        {
            _sessions = sessions;
            _authorization = authorization;
            _runtime = runtime;
        }

        internal StationRuntime Runtime => _runtime;
        internal LocalAuthorizationService Authorization => _authorization;

        internal async Task LogoutAsync()
        {
            var session = _sessions.Current.SessionId;
            Assert.True(session.HasValue);
            var result = await _sessions.LogoutAsync(session.Value);
            Assert.True(result.Succeeded, result.ReasonCode);
        }

        internal CommandInvocation Invocation() => new(CommandSource.PhysicalConsole,
            _sessions.Current.PrincipalId, _sessions.Current.SessionId);

        internal static async Task<ProductionRecoveryRuntimeContext> CreateAsync(
            ManualHarness harness, ProductionInspectionOptions originalOptions,
            FixtureRecoverySafetyProvider safety, VirtualCameraClock clock)
        {
            var fixture = harness.Fixture;
            var identity = new LocalIdentityService(fixture.Store, fixture.IdentityOptions,
                new RecipeDraftStorageTests.Fixture.FixtureConsole());
            var sessions = new InteractiveSessionService(identity,
                fixture.IdentityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
            LocalAuthorizationService? authorization = null;
            StationRuntime? runtime = null;
            try
            {
                var login = await sessions.SignInAsync(new PasswordSignInRequest(
                    fixture.UserName, fixture.Password));
                Assert.True(login.Succeeded, login.ReasonCode);
                var newAuthorization = new LocalAuthorizationService(fixture.Store,
                    fixture.IdentityOptions, identity, sessions);
                authorization = newAuthorization;

                var camera = ManualHarness.CreateProvider(clock, timeout: false);
                var restarted = new StationRuntime(fixture.Store, TimeSpan.FromMilliseconds(20),
                    sessions, newAuthorization, harness.Service<FrameBufferPool>(),
                    cameraProviders: new[] { camera },
                    cameraSetupOptions: new CameraSetupOptions(),
                    productionStoreOptions: fixture.Options);
                runtime = restarted;
                var drafts = harness.Service<RecipeDraftService>();
                var preparation = harness.Service<AlgorithmPreparationService>();
                var preparationOptions = harness.Service<AlgorithmPreparationOptions>();
                var releases = new SqliteReleasedRecipeQuery(fixture.Options);
                restarted.ConfigureRecipeActivationService(new RecipeActivationService(drafts, releases,
                    harness.Service<IPlcResultContractQuery>(),
                    new SqliteRecipeActivationQuery(fixture.Options), newAuthorization, fixture.Store,
                    fixture.Options, preparation, preparationOptions,
                    harness.Service<FrameBufferPool>(),
                    (correlation, token) => restarted.ReserveRecipeActivationAsync(correlation, token),
                    () => restarted.GetSnapshotAsync()));
                restarted.ConfigureManualInspectionSessions(new ManualInspectionSessionOptions(), drafts,
                    releases, preparation, preparationOptions,
                    harness.Service<AlgorithmExecutionOptions>(), fixture.Options, clock);

                var binding = new ProductionRecoveryBinding(originalOptions.StationId,
                    originalOptions.Profile.EndpointBindingHash, safety.Binding,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                var recoveryOptions = new ProductionInspectionOptions(originalOptions.StationId,
                    originalOptions.EvidenceRequirement, originalOptions.Profile,
                    originalOptions.TracePolicyVersion, originalOptions.TracePolicySnapshotHash,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), originalOptions.Deployment,
                    binding);
                restarted.ConfigureProductionInspections(recoveryOptions, fixture.Options,
                    harness.Service<AlgorithmExecutionOptions>(), clock,
                    recoverySafetyProviders: new[] { safety });

                await restarted.WaitForRecipeActivationStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                await restarted.WaitForManualInspectionStartupAsync()
                    .WaitAsync(TimeSpan.FromSeconds(30));
                await fixture.WaitForVerifiedAsync();
                return new ProductionRecoveryRuntimeContext(sessions, newAuthorization, restarted);
            }
            catch
            {
                if (runtime is not null) await runtime.DisposeAsync();
                authorization?.Dispose();
                await sessions.DisposeAsync();
                throw;
            }
        }

        internal async Task WaitForRecoveryBlockerAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            StationStateSnapshot? last = null;
            while (DateTime.UtcNow < deadline)
            {
                last = await Runtime.GetSnapshotAsync();
                if (last.AdmissionBlockers.Contains("ProductionInspectionStartupRecoveryRequired"))
                    return;
                await Task.Delay(25);
            }

            throw new Xunit.Sdk.XunitException(
                "Cold recovery runtime did not reach the recovery blocker: " +
                last?.Recovery + "/" + last?.ArmState + "/" +
                string.Join(",", last?.AdmissionBlockers.AsEnumerable() ?? Array.Empty<string>()));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _runtime.DisposeAsync();
            _authorization.Dispose();
            await _sessions.DisposeAsync();
        }
    }

    private sealed class FixtureRecoverySafetyProvider : IProductionRecoverySafetyProvider
    {
        private FixtureRecoverySafetyProvider(ProductionRecoverySafetyProviderBinding binding,
            ProductionRecoverySafetySourceState source)
        {
            Binding = binding;
            Source = source;
        }

        internal ProductionRecoverySafetyObservationStatus Status { get; set; } =
            ProductionRecoverySafetyObservationStatus.SafeLineStopped;
        internal int Observations => Volatile.Read(ref _observations);
        private int _observations;

        public ProductionRecoverySafetyProviderBinding Binding { get; }
        public ProductionRecoverySafetySourceState Source { get; }
        public event EventHandler<ProductionRecoverySafetySourceChangedEventArgs>? SourceChanged;

        internal static FixtureRecoverySafetyProvider Create(
            ProductionInspectionOptions productionOptions)
        {
            var providerType = typeof(FixtureRecoverySafetyProvider);
            var binding = new ProductionRecoverySafetyProviderBinding(
                "v144-test-safety", "1", productionOptions.StationId,
                productionOptions.Profile.EndpointBindingHash, "physical-console-stop", "1",
                new string('A', 64), providerType,
                ProductionRecoverySafetyRegistry.ComputeProviderAssemblyHash(providerType),
                TimeSpan.FromSeconds(30));
            return new FixtureRecoverySafetyProvider(binding,
                new ProductionRecoverySafetySourceState(Guid.NewGuid(), 1, true));
        }

        public ValueTask<ProductionRecoverySafetyObservation> ObserveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _observations);
            var status = Status;
            var reason = status switch
            {
                ProductionRecoverySafetyObservationStatus.SafeLineStopped => "PhysicalLineStopped",
                ProductionRecoverySafetyObservationStatus.LineNotStopped => "PhysicalLineRunning",
                ProductionRecoverySafetyObservationStatus.Unavailable => "PhysicalStopUnavailable",
                ProductionRecoverySafetyObservationStatus.Stale => "PhysicalStopStale",
                _ => "PhysicalStopInvalid"
            };
            return ValueTask.FromResult(new ProductionRecoverySafetyObservation(Binding, status,
                reason, Source.SourceEpoch, Source.SourceGeneration, DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        }

        // Kept as a real provider event surface so the test provider exercises
        // the same source invalidation boundary as a host implementation.
        internal void Invalidate(string reason)
        {
            SourceChanged?.Invoke(this,
                new ProductionRecoverySafetySourceChangedEventArgs(Source, reason));
        }
    }
}
