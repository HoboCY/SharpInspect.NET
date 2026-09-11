using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cycles;
using SharpInspect.Runtime.PartIdentity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V143_R01_ProductionLatchesPlcIdentityAndAllowsRepeatedBusinessValueWithNewReadToken()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var plan = new ModbusPartIdentityReadPlan("V143.Part.Plc", "1", 600, 64);
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.StablePlc, plan.ContentHash);
        var provider = new ModbusPartIdentityProvider(binding, plan);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Required, binding),
            partIdentityReadPlan: plan, partIdentityStore: new(),
            configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        var ids = new HashSet<Guid>();
        var evidenceHashes = new HashSet<string>();
        for (var index = 1; index <= 2; index++)
        {
            await WaitProductionAsync(harness, state => state.Ready, "Identity production Ready");
            peer.SetPartIdentity((uint)(index * 2), 61, (uint)index, " A-001 ");
            peer.RaiseTrigger(61, (uint)index);
            await WaitForProductionResultAsync(harness, peer, index);
            var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
            Assert.True(read.Available, read.ReasonCode);
            var core = Assert.IsType<ProductionInspectionCore>(read.Latest!.Core);
            var identity = Assert.IsType<PartIdentityEvidence>(core.Admission.PartIdentityEvidence);
            Assert.Equal(PartIdentityEvidenceState.Provided, identity.State);
            Assert.Equal(" A-001 ", core.PartIdentity);
            Assert.Equal(" A-001 ", identity.Value);
            Assert.Equal((uint)index, identity.Cycle.CycleSequence);
            Assert.Equal(binding.ContentHash, identity.BindingHash);
            Assert.NotNull(identity.StablePlcSnapshot);
            Assert.True(ids.Add(core.Admission.InspectionId));
            Assert.True(evidenceHashes.Add(identity.ContentHash));
            var coreHash = core.ContentHash;
            peer.SetPartIdentity((uint)(100 + index * 2), 61, (uint)index, "B-002");
            var unchanged = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
                .ReadAsync(core.Admission.InspectionId);
            Assert.Equal(coreHash, unchanged.Latest!.Core!.ContentHash);
            Assert.Equal(" A-001 ", unchanged.Latest.Core.PartIdentity);
            peer.SetTrigger(false);
            await WaitConditionAsync(() => peer.AckLowCount >= index, "Identity ACK reset");
        }
    }

    [Theory]
    [InlineData(PartIdentityRequirementMode.Required, 2, null, "PartIdentityRequiredMissing")]
    [InlineData(PartIdentityRequirementMode.Optional, 3, null, "PartIdentityAmbiguous")]
    [InlineData(PartIdentityRequirementMode.Required, 1, "bad", "PartIdentityValueCharacterInvalid")]
    public async Task V143_R02_IdentityRejectionRecordsSourceWithoutCreatingInspection(
        PartIdentityRequirementMode mode, ushort sourceState, string? value, string reason)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var plan = new ModbusPartIdentityReadPlan("V143.Part.Plc", "1", 600, 64);
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.StablePlc, plan.ContentHash);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(mode, binding), partIdentityReadPlan: plan,
            partIdentityStore: new(), configureAdditionalServices: services =>
                services.AddSingleton<IPartIdentityProvider>(new ModbusPartIdentityProvider(binding, plan)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Identity rejection Ready");
        peer.SetPartIdentity(2, 61, 1, value, sourceState);
        peer.RaiseTrigger(61, 1);
        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        await WaitIdentityConditionAsync(async () => (await query.ReadCurrentAsync()).Latest?.ReasonCode == reason,
            "Durable rejected identity trigger");
        var rejected = (await query.ReadCurrentAsync()).Latest!;
        Assert.Equal(PartIdentityHistoryEventKind.RejectedTrigger, rejected.Kind);
        Assert.Null(rejected.InspectionId);
        Assert.Equal((uint)61, rejected.ControllerEpoch);
        Assert.Equal((uint)1, rejected.CycleSequence);
        var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Events);
        Assert.Equal(0, peer.ResultValidHighCount);
        Assert.Null((await harness.Runtime.GetSnapshotAsync()).CurrentExecution);
        // The same high edge cannot silently enqueue another attempt or append unbounded failures.
        await Task.Delay(100);
        var page = await query.QueryAsync(new());
        Assert.Single(page.Events);
    }

    [Fact]
    public async Task V143_R03_OptionalMissingCommitsExplicitNotProvidedEvidence()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64));
        var provider = new StagedPartIdentityProvider(binding);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Optional, binding),
            partIdentityStore: new(), configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Optional source Ready");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);
        var read = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.True(read.Available, read.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(read.Latest!.Core);
        var identity = Assert.IsType<PartIdentityEvidence>(core.Admission.PartIdentityEvidence);
        Assert.Equal(PartIdentityEvidenceState.NotProvided, identity.State);
        Assert.Equal(PartIdentityObservationStatus.Missing, identity.Observation.Status);
        Assert.Null(identity.Value);
        Assert.Null(core.PartIdentity);
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount == 1, "Optional identity ACK reset");
    }

    [Fact]
    public async Task V143_R04_SourceRestartRevokesArmWithoutWaitingForPollingRefresh()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64));
        var provider = new StagedPartIdentityProvider(binding);
        var unrelatedBinding = new PartIdentityProviderBinding("V143.Unrelated.Binding", "1", "Unrelated.Code",
            PartIdentityProviderSourceKind.Staged, "Unrelated.Provider", "1", new string('B', 64), binding.Format,
            binding.FreshnessLimit, binding.LatchTimeout, binding.MaximumCallsPerCycle);
        var unrelated = new StagedPartIdentityProvider(unrelatedBinding);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Required, binding),
            partIdentityStore: new(), configureAdditionalServices: services =>
            {
                services.AddSingleton<IPartIdentityProvider>(provider);
                services.AddSingleton<IPartIdentityProvider>(unrelated);
            });
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        Assert.True((await harness.Service<PartIdentityBindingRegistry>().CaptureAsync(
            RuntimeIdentityRequirement(PartIdentityRequirementMode.Required, unrelatedBinding), CancellationToken.None)).Available);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Scanner source Ready");
        unrelated.RestartSource();
        var unrelatedChange = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Armed, unrelatedChange.ArmState);
        Assert.True(unrelatedChange.Ready);
        var before = await harness.Runtime.GetSnapshotAsync();
        var cycle = new PartIdentityCycleBinding(before.RuntimeEpoch, harness.Service<ProductionInspectionOptions>().Profile.EndpointBindingHash,
            before.PlcCommunication!.ConnectionGeneration, 61, 1);
        Assert.True(provider.Stage(new(Guid.NewGuid(), cycle, "A-001")).Accepted);
        Assert.Equal(ProductionArmState.Armed, (await harness.Runtime.GetSnapshotAsync()).ArmState);
        provider.RestartSource();
        var after = await harness.Runtime.GetSnapshotAsync();
        Assert.Equal(ProductionArmState.Disarmed, after.ArmState);
        Assert.False(after.Ready);
        Assert.Null(after.CurrentExecution);
    }

    [Fact]
    public async Task V143_R05_ProviderObservationFromAnotherSourceEpochIsRejectedBeforeInspectionAllocation()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64));
        var provider = new WrongEpochIdentityProvider(new StagedPartIdentityProvider(binding));
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Optional, binding),
            partIdentityStore: new(), configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Source epoch rejection Ready");
        peer.RaiseTrigger(61, 1);
        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        await WaitIdentityConditionAsync(async () => (await query.ReadCurrentAsync()).Latest?.ReasonCode ==
            "PartIdentityObservationSourceGenerationMismatch", "Source mismatch rejection persisted");
        var rejected = (await query.ReadCurrentAsync()).Latest!;
        Assert.Null(rejected.InspectionId);
        Assert.NotEqual(provider.Capabilities.SourceEpoch, rejected.RejectionContext!.Observation!.SourceEpoch);
        var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Events);
        Assert.Equal(0, peer.ResultValidHighCount);
    }

    [Fact]
    public async Task V143_R06_StagedCodeExpiresBeforeTriggerWithoutCreatingRun()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64),
            freshness: TimeSpan.FromMilliseconds(30));
        var provider = new StagedPartIdentityProvider(binding);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Required, binding),
            partIdentityStore: new(), configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Stale source Ready");
        Assert.True(provider.Stage(new(Guid.NewGuid(), await RuntimeIdentityCycleAsync(harness), "A-001")).Accepted);
        await Task.Delay(80);
        peer.RaiseTrigger(61, 1);
        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        await WaitIdentityConditionAsync(async () => (await query.ReadCurrentAsync()).Latest?.ReasonCode ==
            "PartIdentityObservationStale", "Stale source rejection persisted");
        Assert.Empty((await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new())).Events);
        Assert.Equal(0, peer.ResultValidHighCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V143_R07_StopOrDeadlineAfterConsumptionNeverAllocatesLateInspection(bool timeout)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64),
            latchTimeout: timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10));
        var source = new StagedPartIdentityProvider(binding);
        var provider = new HeldIdentityProvider(source);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Required, binding),
            partIdentityStore: new(), configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Held source Ready");
        var cycle = await RuntimeIdentityCycleAsync(harness);
        var token = Guid.NewGuid();
        Assert.True(source.Stage(new(token, cycle, "A-001")).Accepted);
        try
        {
            peer.RaiseTrigger(61, 1);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var history = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
            if (timeout)
            {
                await WaitIdentityConditionAsync(async () => (await history.ReadCurrentAsync()).Latest?.ReasonCode ==
                    "PartIdentityLatchTimeout", "Unretired provider timeout rejection persisted");
                await WaitProductionAsync(harness, state => !state.Ready && state.ArmState == ProductionArmState.Disarmed,
                    "Unretired source cannot retain Ready or Arm");
            }
            else
            {
                var stop = await harness.Runtime.SubmitAsync(new GracefulProductionStopCommand(Guid.NewGuid(), harness.Invocation()));
                AssertAccepted(stop, "Stop while identity provider holds latch response");
                await WaitProductionAsync(harness, state => state.ArmState == ProductionArmState.Disarmed,
                    "Stop disarms without waiting for source response");
            }
            provider.Release();
            await WaitIdentityConditionAsync(async () => (await history.ReadCurrentAsync()).Latest is not null,
                "Late source remains a rejected trigger");
            var production = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new());
            Assert.True(production.Available, production.ReasonCode);
            Assert.Empty(production.Events);
            Assert.Equal(0, peer.ResultValidHighCount);
            Assert.Equal(0, harness.CameraProvider.GetDiagnostics().Devices.Single().FramesProduced);
            var caps = source.Capabilities;
            var replay = await source.TryLatchAsync(new(binding, cycle, 1, caps.SourceEpoch, caps.SourceGeneration, token));
            Assert.Equal(PartIdentityObservationStatus.Missing, replay.Status);
        }
        finally { provider.Release(); }
    }

    [Fact]
    public async Task V143_R08_IdentityIsRecheckedForExpiryAfterAdmissionGateWait()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.Staged, new string('A', 64),
            freshness: TimeSpan.FromSeconds(1), latchTimeout: TimeSpan.FromSeconds(5));
        var source = new StagedPartIdentityProvider(binding);
        var provider = new HeldIdentityProvider(source);
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(PartIdentityRequirementMode.Required, binding),
            partIdentityStore: new(), configureAdditionalServices: services => services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Freshness fence Ready");
        var gate = GetCommandGate(Assert.IsType<StationRuntime>(harness.Runtime));
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.True(source.Stage(new(Guid.NewGuid(), await RuntimeIdentityCycleAsync(harness), "A-001")).Accepted);
            peer.RaiseTrigger(61, 1);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            provider.Release();
            await provider.CapabilitiesAfterLatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
        }
        finally { provider.Release(); gate.Release(); }
        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        await WaitIdentityConditionAsync(async () => (await query.ReadCurrentAsync()).Latest?.ReasonCode ==
            "PartIdentityObservationStale", "Expired identity cannot cross final allocation fence");
        var history = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Events);
        Assert.Equal(0, peer.ResultValidHighCount);
    }

    [Fact]
    public async Task V143_R09_ProtocolFailureDrainsPreviouslyRejectedBusyTriggerToLedger()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer,
            partIdentityStore: new());
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready, "Busy rejection Ready");
        harness.Factory.HoldExecution();
        try
        {
            peer.RaiseTrigger(61, 1);
            await harness.Factory.ExecutionEntered.WaitAsync(TimeSpan.FromSeconds(10));
            var runtime = Assert.IsType<StationRuntime>(harness.Runtime);
            var owner = typeof(StationRuntime).GetField("_productionInspectionOwner", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(runtime)!;
            var observer = Assert.IsType<InspectionCycleRequestObserver>(owner.GetType()
                .GetProperty("Observer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
            peer.SetTrigger(false);
            await WaitConditionAsync(() => observer.Latest.Signals?.Trigger == false, "Observer sees low before busy edge");
            peer.RaiseTrigger(61, 2);
            await WaitConditionAsync(() => observer.Latest.Signals?.CycleSequence == 2, "Observer rejects busy edge");
            peer.RaiseTrigger(62, 3);
            await WaitProductionAsync(harness, state => state.ArmState == ProductionArmState.Disarmed,
                "Controller epoch failure aborts current cycle");
        }
        finally { harness.Factory.ReleaseExecution(); }
        var query = new SqlitePartIdentityHistoryQuery(harness.Fixture.Options);
        await WaitIdentityConditionAsync(async () => (await query.QueryAsync(new())).Events.Any(value =>
            value.ControllerEpoch == 61 && value.CycleSequence == 2), "Rejected busy edge survives exceptional retirement");
        var page = await query.QueryAsync(new());
        Assert.True(page.Available, page.ReasonCode);
        var rejected = Assert.Single(page.Events, value => value.ControllerEpoch == 61 && value.CycleSequence == 2);
        Assert.Null(rejected.InspectionId);
        Assert.Equal("QualificationTriggerRejectedWhileNotReady", rejected.ReasonCode);
        var production = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).QueryAsync(new(PageSize: 128));
        Assert.DoesNotContain(production.Events, value => value.Admission.ControllerCycle.CycleSequence == 2);
    }

    private static PartIdentityProviderBinding RuntimeIdentityBinding(PartIdentityProviderSourceKind kind, string contractHash,
        TimeSpan? freshness = null, TimeSpan? latchTimeout = null) =>
        new("V143.Part.Binding", "1", "Part.Code", kind, "V143.Part.Provider", "1", contractHash,
            new("V143.Part.Format", "1", 1, 32, " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-"),
            freshness ?? TimeSpan.FromSeconds(5), latchTimeout ?? TimeSpan.FromSeconds(1), 1);

    private static PartIdentityRequirement RuntimeIdentityRequirement(PartIdentityRequirementMode mode,
        PartIdentityProviderBinding binding) => new(mode, binding.LogicalRole, binding.Format.ToContractReference());

    private static async Task WaitIdentityConditionAsync(Func<Task<bool>> condition, string reason)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail(reason);
    }

    private static async Task<PartIdentityCycleBinding> RuntimeIdentityCycleAsync(ManualHarness harness)
    {
        var snapshot = await harness.Runtime.GetSnapshotAsync();
        return new(snapshot.RuntimeEpoch, harness.Service<ProductionInspectionOptions>().Profile.EndpointBindingHash,
            snapshot.PlcCommunication!.ConnectionGeneration, 61, 1);
    }

    private sealed class HeldIdentityProvider : IPartIdentityProvider
    {
        private readonly IPartIdentityProvider _inner;
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CapabilitiesAfterLatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _returned;
        internal HeldIdentityProvider(IPartIdentityProvider inner) => _inner = inner;
        public PartIdentityProviderBinding Binding => _inner.Binding;
        public PartIdentityProviderCapabilities Capabilities => _inner.Capabilities;
        public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged
        { add => _inner.SourceChanged += value; remove => _inner.SourceChanged -= value; }
        public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _returned) != 0) CapabilitiesAfterLatch.TrySetResult(true);
            return _inner.GetCapabilitiesAsync(cancellationToken);
        }
        public async ValueTask<PartIdentityProviderObservation> TryLatchAsync(PartIdentityLatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.TryLatchAsync(request, cancellationToken);
            Entered.TrySetResult(true);
            await _release.Task;
            Volatile.Write(ref _returned, 1);
            return result;
        }
        internal void Release() => _release.TrySetResult(true);
    }

    private sealed class WrongEpochIdentityProvider : IPartIdentityProvider
    {
        private readonly IPartIdentityProvider _inner;
        internal WrongEpochIdentityProvider(IPartIdentityProvider inner) => _inner = inner;
        public PartIdentityProviderBinding Binding => _inner.Binding;
        public PartIdentityProviderCapabilities Capabilities => _inner.Capabilities;
        public event EventHandler<PartIdentitySourceChangedEventArgs>? SourceChanged
        { add => _inner.SourceChanged += value; remove => _inner.SourceChanged -= value; }
        public ValueTask<PartIdentityProviderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            _inner.GetCapabilitiesAsync(cancellationToken);
        public async ValueTask<PartIdentityProviderObservation> TryLatchAsync(PartIdentityLatchRequest request,
            CancellationToken cancellationToken = default)
        {
            var observed = await _inner.TryLatchAsync(request, cancellationToken);
            return new(observed.Binding, observed.Cycle, observed.Status, observed.Value, observed.ReasonCode,
                observed.SourceSequence, observed.ObservedAtUtc, observed.MonotonicTimestamp, observed.MonotonicFrequency,
                Guid.NewGuid(), observed.SourceGeneration, observed.StageToken, observed.StablePlcSnapshot);
        }
    }
}
