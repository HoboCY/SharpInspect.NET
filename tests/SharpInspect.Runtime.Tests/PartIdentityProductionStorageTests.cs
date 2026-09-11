using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.PartIdentity;
using SharpInspect.Runtime.Plc;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V143_D01_NoneAdmissionKeepsV1EnvelopeAndRoundTrips()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var admission = await BuildAdmissionAsync(harness);
        Assert.Null(admission.PartIdentityEvidence);
        var encoded = ProductionInspectionStorageCodec.EncodeAdmissionEnvelope(admission);
        var versionOffset = System.Text.Encoding.ASCII.GetByteCount("SI-PROD-CORE");
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(
            encoded.AsSpan(versionOffset, sizeof(int))));

        var decoded = ProductionInspectionStorageCodec.DecodeAdmissionEnvelope(encoded);
        Assert.Equal(admission.ContentHash, decoded.ContentHash);
        Assert.Null(decoded.PartIdentityEvidence);
    }

    [Fact]
    public async Task V143_D02_RequiredEvidenceUsesV2EnvelopeAndColdReadPreservesValue()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var plan = new ModbusPartIdentityReadPlan("V143.D02.Part", "1", 600, 64);
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.StablePlc,
            plan.ContentHash);
        var provider = new ModbusPartIdentityProvider(binding, plan);
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(
                PartIdentityRequirementMode.Required, binding),
            partIdentityReadPlan: plan, partIdentityStore: new(),
            configureAdditionalServices: services =>
                services.AddSingleton<IPartIdentityProvider>(provider));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready,
            "V143_D02 production arm");

        peer.SetPartIdentity(2, 61, 1, "P-001");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);

        var page = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        Assert.True(page.Available, page.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(page.Latest!.Core);
        var evidence = Assert.IsType<PartIdentityEvidence>(core.Admission.PartIdentityEvidence);
        Assert.Equal("P-001", core.PartIdentity);
        Assert.Equal("P-001", evidence.Value);

        var encoded = ProductionInspectionStorageCodec.EncodeAdmissionEnvelope(core.Admission);
        var versionOffset = System.Text.Encoding.ASCII.GetByteCount("SI-PROD-CORE");
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(
            encoded.AsSpan(versionOffset, sizeof(int))));
        var decoded = ProductionInspectionStorageCodec.DecodeAdmissionEnvelope(encoded);
        Assert.Equal(core.Admission.ContentHash, decoded.ContentHash);
        Assert.Equal(evidence.ContentHash, decoded.PartIdentityEvidence!.ContentHash);
        Assert.Equal("P-001", decoded.PartIdentityEvidence.Value);
        Assert.Equal(evidence.StablePlcSnapshot!.ReadEvidenceHash,
            decoded.PartIdentityEvidence.StablePlcSnapshot!.ReadEvidenceHash);
    }

    [Fact]
    public async Task V143_D03_DuplicatePlcSourceTokenIsRejectedBeforeAnyNewRow()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var plan = new ModbusPartIdentityReadPlan("V143.D03.Part", "1", 600, 64);
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.StablePlc,
            plan.ContentHash);
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(
                PartIdentityRequirementMode.Required, binding),
            partIdentityReadPlan: plan, partIdentityStore: new(),
            configureAdditionalServices: services =>
                services.AddSingleton<IPartIdentityProvider>(
                    new ModbusPartIdentityProvider(binding, plan)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready,
            "V143_D03 production arm");
        peer.SetPartIdentity(2, 61, 1, "P-001");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);

        var page = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        var original = Assert.IsType<ProductionInspectionCore>(page.Latest!.Core).Admission;
        var originalIdentity = Assert.IsType<PartIdentityEvidence>(original.PartIdentityEvidence);
        var duplicate = CloneAdmission(original, originalIdentity,
            original.ControllerCycle, originalIdentity.StablePlcSnapshot!.Revision);
        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM production_inspection_admissions;");

        var result = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(duplicate),
            new StoreDeadline(TimeSpan.FromSeconds(4)));

        Assert.False(result.Committed);
        Assert.Equal("ProductionInspectionPartIdentitySourceTokenDuplicate", result.ReasonCode);
        Assert.Equal(before, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;"));
    }

    [Fact]
    public async Task V143_D04_SameBusinessValueWithNewPlcTokenIsAccepted()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        var plan = new ModbusPartIdentityReadPlan("V143.D04.Part", "1", 600, 64);
        var binding = RuntimeIdentityBinding(PartIdentityProviderSourceKind.StablePlc,
            plan.ContentHash);
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer,
            productionPartRequirement: RuntimeIdentityRequirement(
                PartIdentityRequirementMode.Required, binding),
            partIdentityReadPlan: plan, partIdentityStore: new(),
            configureAdditionalServices: services =>
                services.AddSingleton<IPartIdentityProvider>(
                    new ModbusPartIdentityProvider(binding, plan)));
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness, state => state.Ready,
            "V143_D04 production arm");
        peer.SetPartIdentity(2, 61, 1, "P-001");
        peer.RaiseTrigger(61, 1);
        await WaitForProductionResultAsync(harness, peer, 1);

        var page = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        var original = Assert.IsType<ProductionInspectionCore>(page.Latest!.Core).Admission;
        var originalIdentity = Assert.IsType<PartIdentityEvidence>(original.PartIdentityEvidence);
        var nextCycle = new PlcControllerCycle(original.ControllerCycle.ControllerEpoch, 2);
        var nextEvidence = CloneEvidence(originalIdentity,
            new PartIdentityCycleBinding(original.RuntimeEpoch, original.EndpointBindingHash,
                original.ConnectionGeneration, nextCycle.ControllerEpoch, nextCycle.CycleSequence),
            checked(originalIdentity.StablePlcSnapshot!.Revision + 2));
        var next = CloneAdmission(original, nextEvidence, nextCycle,
            nextEvidence.StablePlcSnapshot!.Revision);

        var result = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(next),
            new StoreDeadline(TimeSpan.FromSeconds(4)));

        Assert.True(result.Committed, result.ReasonCode);
        Assert.Equal(next.ContentHash, result.Admission!.ContentHash);
        Assert.Equal("P-001", result.Admission.PartIdentityEvidence!.Value);
    }

    private static ProductionInspectionAdmission CloneAdmission(
        ProductionInspectionAdmission source, PartIdentityEvidence evidence,
        PlcControllerCycle cycle, uint revision) => new(
            Guid.NewGuid(), Guid.NewGuid(), source.RuntimeEpoch, source.StationId,
            checked(source.AdmissionGeneration + 1), cycle, source.EvidenceRequirement,
            source.ActivationReference, source.ActivationSnapshot, source.EndpointBindingHash,
            source.PlcProfileHash, source.PlcPolicyHash, source.ConnectionGeneration,
            checked(source.ConnectionAttempt + 1), DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), source.TracePolicySnapshot, source.RetentionObligations,
            CloneEvidence(evidence,
                new PartIdentityCycleBinding(source.RuntimeEpoch, source.EndpointBindingHash,
                    source.ConnectionGeneration, cycle.ControllerEpoch, cycle.CycleSequence),
                revision));

    private static PartIdentityEvidence CloneEvidence(PartIdentityEvidence source,
        PartIdentityCycleBinding cycle, uint revision)
    {
        PartIdentityStablePlcSnapshot? stable = source.StablePlcSnapshot is { } prior
            ? new PartIdentityStablePlcSnapshot(cycle, prior.SourceContractHash, revision,
                prior.State, prior.Status, prior.GetRawUtf8Bytes(), prior.SourceSequence,
                prior.ObservedAtUtc, prior.MonotonicTimestamp, prior.MonotonicFrequency,
                prior.ReadEvidenceHash)
            : null;
        var observation = new PartIdentityProviderObservation(source.Observation.Binding, cycle,
            source.Observation.Status, source.Observation.Value, source.Observation.ReasonCode,
            source.Observation.SourceSequence, source.Observation.ObservedAtUtc,
            source.Observation.MonotonicTimestamp, source.Observation.MonotonicFrequency,
            source.SourceEpoch, source.SourceGeneration, source.StageToken, stable);
        return new PartIdentityEvidence(source.State, source.Value, observation);
    }
}
