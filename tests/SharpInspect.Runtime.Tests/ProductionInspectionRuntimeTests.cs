using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_R01_RealProductionEntryCommitsCoreBeforePayloadAndRepeatsUnderOneArm()
    {
        using var diagnostics = new ProductionExceptionObservation();
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        await using var stateObservation = new ProductionStateObservation(harness.Runtime);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        for (var index = 0; index < 2; index++)
        {
            var expected = index + 1;
            await WaitProductionAsync(harness, state => state.Ready && state.ArmState == ProductionArmState.Armed,
                "Production ready before cycle " + expected);
            peer.RaiseTrigger(61, (uint)expected);
            try { await WaitForProductionResultAsync(harness, peer, expected); }
            catch (XunitException)
            {
                var failedState = await harness.Runtime.GetSnapshotAsync();
                var failedHistory = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
                throw new XunitException($"Production result publication: {failedState.ArmState}/{failedState.Recovery}/{failedState.CurrentExecution}; " +
                    $"history={failedHistory.ReasonCode}:" + string.Join(";", failedHistory.Events.Select(value => value.Kind + ":" + value.ReasonCode)) +
                    ";gates=" + string.Join(";", failedState.ProductionAdmission!.Gates.Where(gate => gate.Status != ProductionAdmissionGateStatus.Passed)
                        .Select(gate => gate.Gate + ":" + gate.ReasonCode)) + ";exceptions=" + diagnostics.Read());
            }
            // The physical publication edge is observed independently by the Modbus peer.
            // A fresh SQLite reader must already reconstruct the exact durable result.
            var history = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
            var current = await history.ReadCurrentAsync();
            Assert.True(current.Available, current.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(current.Latest!.Core);
            Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
            Assert.Equal((uint)expected, core.Admission.ControllerCycle.CycleSequence);
            Assert.NotEqual(Guid.Empty, core.Admission.InspectionId);
            Assert.Equal(core.Admission.InspectionId, core.Admission.CorrelationId);
            Assert.NotNull(core.Result);
            Assert.NotNull(core.FrameMetadata);
            Assert.NotNull(core.FrameProvenance);
            Assert.NotNull(core.PlcPayload);
            Assert.Empty(core.RetentionObligations);
            peer.SetTrigger(false);
            await WaitConditionAsync(() => peer.AckLowCount >= expected, "Production ACK reset");
            try { await WaitProductionAsync(harness, state => state.CurrentExecution is null && !state.Busy && state.Ready,
                "Production ready after ACK reset"); }
            catch (XunitException error) { throw new XunitException(error.Message + " / " + stateObservation.Read()); }
        }
        var page = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var cores = page.Events.Where(entry => entry.Kind == ProductionInspectionEventKind.CoreCommitted).ToArray();
        Assert.Equal(2, cores.Length);
        Assert.Equal(2, cores.Select(entry => entry.Admission.InspectionId).Distinct().Count());
        Assert.Equal(1, harness.Factory.Created);
        Assert.Equal(0, peer.ReadyWriteCount);
        Assert.True(peer.ProductionReadyWriteCount >= 2);
    }

    [Fact]
    public async Task V142_R02_RealAcquisitionTimeoutCommitsTypedUnknownWithoutInvokingAlgorithm()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(timeout: true, activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Production ready for acquisition failure");
        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount == 1, "Typed acquisition timeout result");
        var current = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(current.Latest!.Core);
        Assert.Equal(ExecutionStatus.Timeout, core.ExecutionStatus);
        Assert.Equal(InspectionDecision.Unknown, core.Decision);
        Assert.NotNull(core.AcquisitionFailureKind);
        Assert.NotNull(core.AcquisitionFailureReasonCode);
        Assert.Null(core.Result);
        Assert.Null(core.FrameMetadata);
        Assert.Equal("CameraAcquisitionTimeout", core.PlcPayload!.ReasonCode);
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Acquisition failure ACK reset");
    }

    private static async Task PrepareProductionAsync(ManualHarness harness, ProductionTestIssuer issuer)
    {
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        runtime.ConfigureProductionInspectionQualificationEvidenceProvider(issuer);
        var fixture = harness.Fixture;
        var release = new ReleaseRecipeCommand(Guid.NewGuid(), harness.Invocation(), harness.Draft.DraftId,
            harness.Draft.Revision, harness.Draft.RevisionContentHash, fixture.Options.RecipeReleases!.Policy.Reference,
            "V142 release isolated production recipe");
        var releaseGrant = await ProductionGrantAsync(harness, Permission.ReleaseRecipe, release.CorrelationId,
            release.AuthorizationTarget, AuditedCommandKind.ReleaseRecipe);
        var released = await harness.Service<IRecipeReleaseService>().ReleaseAsync(release with
            { Invocation = harness.Invocation() with { StepUpGrantId = releaseGrant } });
        AssertAccepted(released.Outcome, "Release production candidate");
        var recipe = Assert.IsType<ReleasedRecipe>(released.Recipe);

        var contract = PlcResultContractTestSupport.Contract(harness.Factory.Descriptor.ResultSchema,
            frameworkReasons: PlcResultContract.ProductionFailureReasonCatalogV2);
        var change = new ChangePlcResultContractCommand(Guid.NewGuid(), harness.Invocation(), contract, null,
            "V142 bind complete production failure catalog");
        var contractGrant = await ProductionGrantAsync(harness, Permission.ManagePlcResultContract,
            change.CorrelationId, change.AuthorizationTarget, AuditedCommandKind.ChangePlcResultContract);
        AssertAccepted(await runtime.SubmitAsync(change with
            { Invocation = harness.Invocation() with { StepUpGrantId = contractGrant } }), "Production PLC contract");
        await WaitProductionAsync(harness, state => state.Recovery == RecoveryState.None &&
            state.PlcCommunication is { Healthy: true }, "Production startup and communication synchronization");

        var activate = new ActivateRecipeCommand(Guid.NewGuid(), harness.Invocation(), recipe.Reference,
            recipe.Record.ReleaseId, recipe.Record.ContentHash, null, null, "V142 activate isolated production recipe");
        var activationGrant = await ProductionGrantAsync(harness, Permission.ActivateRecipe,
            activate.CorrelationId, activate.AuthorizationTarget, AuditedCommandKind.ActivateRecipe);
        var activation = await harness.Service<IRecipeActivationService>().ActivateAsync(activate with
            { Invocation = harness.Invocation() with { StepUpGrantId = activationGrant } });
        Assert.True(activation.Outcome.Disposition == CommandDisposition.Accepted && activation.Record?.Outcome.Succeeded == true,
            activation.Outcome.ReasonCode + " / " + issuer.LastMissing + " / " + string.Join(";", activation.Record?.Checks
                .Where(check => check.Status == RecipeActivationCheckStatus.Failed).Select(check => check.CheckId + ":" + check.ReasonCode)
                ?? Array.Empty<string>()));
        Assert.True(activation.Record!.SuccessfulSnapshot!.ProductionAuthority);
        Assert.DoesNotContain(activation.Record.Checks, check => check.ReasonCode == "InternalContractFixtureAssumption");
    }

    private static async Task ArmProductionAsync(ManualHarness harness)
    {
        var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
        // Initial startup warnings use the ordinary latched alarm policy and must
        // be acknowledged/reset through real authorization after recovery completes.
        await WaitProductionAsync(harness, state => state.AlarmState is { Available: true }, "Alarm authority");
        var state = await runtime.GetSnapshotAsync();
        foreach (var alarm in state.AlarmState!.Instances.Where(alarm => alarm.Lifecycle == AlarmLifecycle.RecoveredLatched))
        {
            AssertAccepted(await runtime.SubmitAsync(new AcknowledgeAlarmCommand(Guid.NewGuid(), harness.Invocation(), alarm.InstanceId)),
                "Acknowledge recovered startup alarm");
            var correlation = Guid.NewGuid();
            var grant = await ProductionGrantAsync(harness, Permission.ResetAlarm, correlation, alarm.InstanceId.ToString("D"), AuditedCommandKind.ResetAlarm);
            AssertAccepted(await runtime.SubmitAsync(new ResetAlarmCommand(correlation,
                harness.Invocation() with { StepUpGrantId = grant }, alarm.InstanceId)), "Reset recovered startup alarm");
        }
        await runtime.RefreshProductionAdmissionAsync();
        await WaitProductionAsync(harness, value => value.ProductionAdmission?.CanArm == true, "Real production admission");
        var command = new ArmProductionCommand(Guid.NewGuid(), harness.Invocation());
        AssertAccepted(await runtime.SubmitAsync(command), "Production Arm");
    }

    private static async Task<Guid?> ProductionGrantAsync(ManualHarness harness, Permission permission,
        Guid correlation, string target, AuditedCommandKind kind)
    {
        var grant = await harness.Fixture.Authorization.ReauthenticateAsync(new(Guid.NewGuid(), harness.Invocation(),
            new(permission, correlation, target, kind), harness.Fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        return grant.GrantId;
    }

    private static async Task WaitProductionAsync(ManualHarness harness, Func<StationStateSnapshot, bool> predicate, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        StationStateSnapshot? state = null;
        while (DateTime.UtcNow < deadline)
        {
            state = await harness.Runtime.GetSnapshotAsync();
            if (predicate(state)) return;
            await Task.Delay(25);
        }
        throw new XunitException(reason + ": " + state?.ArmState + "/" + state?.Recovery + "/" +
            string.Join(";", state?.ProductionAdmission?.Gates.Where(gate => gate.Status != ProductionAdmissionGateStatus.Passed)
                .Select(gate => gate.Gate + ":" + gate.ReasonCode) ?? Array.Empty<string>()));
    }

    private static async Task WaitForProductionResultAsync(ManualHarness harness, ModbusQualificationTestServer peer, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (peer.ResultValidHighCount < expected)
        {
            var state = await harness.Runtime.GetSnapshotAsync();
            if (state.Recovery == RecoveryState.Required || DateTime.UtcNow >= deadline)
                throw new XunitException("Production result publication");
            await Task.Delay(20);
        }
    }

    private static async Task WaitConditionAsync(Func<bool> condition, string reason)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new XunitException(reason);
            await Task.Delay(20);
        }
    }

    private sealed class ProductionStateObservation : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _changes = new();
        internal ProductionStateObservation(IStationRuntime runtime)
        {
            _worker = Task.Run(async () =>
            {
                StationStateSnapshot? previous = null;
                try
                {
                    await foreach (var state in runtime.WatchSnapshotsAsync(_stop.Token))
                    {
                        if (previous?.ArmState == ProductionArmState.Armed && state.ArmState != ProductionArmState.Armed)
                        {
                            var oldGates = previous.ProductionAdmission!.Gates.ToDictionary(gate => gate.Gate);
                            var changes = state.ProductionAdmission!.Gates.Where(gate => gate.Status != oldGates[gate.Gate].Status ||
                                gate.ReasonCode != oldGates[gate.Gate].ReasonCode || gate.ObservedFingerprint != oldGates[gate.Gate].ObservedFingerprint ||
                                gate.EvidenceRecordHash != oldGates[gate.Gate].EvidenceRecordHash)
                                .Select(gate => gate.Gate + ":" + oldGates[gate.Gate].ReasonCode + "->" + gate.ReasonCode);
                            _changes.Enqueue($"DISARM generation {previous.ProductionAdmission.AdmissionGeneration}->{state.ProductionAdmission.AdmissionGeneration}, " +
                                $"busy {previous.Busy}->{state.Busy}, execution {previous.CurrentExecution}->{state.CurrentExecution}, " +
                                $"handshake {previous.Handshake}->{state.Handshake}, camera {previous.Camera}->{state.Camera}, " +
                                $"store {previous.Store}->{state.Store}, evidence {previous.Evidence}->{state.Evidence}, " +
                                $"blockers {string.Join(",", previous.AdmissionBlockers)}->{string.Join(",", state.AdmissionBlockers)}, gates {string.Join(";", changes)}");
                        }
                        previous = state;
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }
        internal string Read() => string.Join("\n", _changes);
        public async ValueTask DisposeAsync() { _stop.Cancel(); await _worker; _stop.Dispose(); }
    }

    private sealed class ProductionExceptionObservation : IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _exceptions = new();
        internal ProductionExceptionObservation() => AppDomain.CurrentDomain.FirstChanceException += Observe;
        private void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception.StackTrace?.Contains("ProductionInspection", StringComparison.Ordinal) == true && _exceptions.Count < 32)
                _exceptions.Enqueue(args.Exception.ToString());
        }
        internal string Read() => string.Join("\n", _exceptions);
        public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= Observe;
    }

    private sealed class ProductionTestIssuer : IProductionInspectionQualificationEvidenceProvider, IDisposable
    {
        private readonly ProductionAdmissionTestFixture _issuer = new();
        private readonly Dictionary<string, ProductionQualificationInputs> _targets = new(StringComparer.Ordinal);
        internal string LastMissing { get; private set; } = string.Empty;
        public ValueTask<ProductionQualificationInputs> CaptureAsync(ProductionConfiguration observedConfiguration,
            StationStateSnapshot state, RecipeActivationSnapshot? activation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_targets)
            {
                LastMissing = string.Join(",", observedConfiguration.MissingBindings);
                if (activation is null || observedConfiguration.MissingBindings.Count != 0)
                    return ValueTask.FromResult(ProductionQualificationInputs.Unconfigured);
                if (_targets.TryGetValue(observedConfiguration.ObservedBindingsHash, out var found)) return ValueTask.FromResult(found);
                var proofs = Enum.GetValues<ProductionQualificationLayer>()
                    .Where(layer => layer != ProductionQualificationLayer.StationAcceptance)
                    .Select(layer => _issuer.Issue(layer, observedConfiguration)).ToList();
                proofs.Add(_issuer.Issue(ProductionQualificationLayer.StationAcceptance, observedConfiguration,
                    references: proofs.ToDictionary(proof => proof.Layer, proof => proof.ContentHash)));
                var inputs = new ProductionQualificationInputs(_issuer.Authority, _issuer.Requirements, proofs,
                    proofs.ToDictionary(proof => proof.Layer, proof => proof.ContentHash), true, null);
                _targets.Add(observedConfiguration.ObservedBindingsHash, inputs);
                return ValueTask.FromResult(inputs);
            }
        }
        public void Dispose() => _issuer.Dispose();
    }
}
