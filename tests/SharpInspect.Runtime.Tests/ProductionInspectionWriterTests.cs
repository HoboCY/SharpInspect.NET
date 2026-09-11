using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_W01_WriterReturnsDurableEnvelopeValuesAndRejectsHistoricalCycleReuse()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var admission = await BuildAdmissionAsync(harness);
        var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission),
            new StoreDeadline(TimeSpan.FromSeconds(4)));

        Assert.True(admitted.Committed, admitted.ReasonCode);
        var durableAdmission = Assert.IsType<ProductionInspectionAdmission>(admitted.Admission);
        var durableAdmissionEvent = Assert.IsType<ProductionInspectionHistoryEvent>(admitted.Event);
        Assert.NotSame(admission, durableAdmission);
        Assert.NotSame(admission, durableAdmissionEvent.Admission);
        Assert.Equal(admission.ContentHash, durableAdmission.ContentHash);
        Assert.Equal(durableAdmission.ContentHash, durableAdmissionEvent.Admission.ContentHash);

        var core = TimeoutCore(durableAdmission);
        var committed = await harness.Fixture.Store.CommitProductionInspectionCoreAsync(
            new ProductionInspectionCoreWriteRequest(core), new StoreDeadline(TimeSpan.FromSeconds(4)));

        Assert.True(committed.Committed, committed.ReasonCode);
        var durableCore = Assert.IsType<ProductionInspectionCore>(committed.Core);
        var durableCoreEvent = Assert.IsType<ProductionInspectionHistoryEvent>(committed.Event);
        Assert.NotSame(core, durableCore);
        Assert.NotSame(core, durableCoreEvent.Core);
        Assert.Equal(core.ContentHash, durableCore.ContentHash);
        Assert.Equal(durableAdmission.ContentHash, durableCore.Admission.ContentHash);

        var coreOnly = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        Assert.True(coreOnly.Available, coreOnly.ReasonCode);
        Assert.True(coreOnly.RecoveryRequired);
        Assert.Equal(ProductionInspectionEventKind.CoreCommitted, coreOnly.Latest!.Kind);

        var outOfOrder = await harness.Fixture.Store.AppendProductionInspectionEventAsync(
            new ProductionInspectionEventWriteRequest(durableAdmission.InspectionId,
                ProductionInspectionEventKind.ResultValidRaised, "V142WriterOutOfOrder",
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.False(outOfOrder.Committed);
        Assert.Equal("ProductionInspectionEventTransitionInvalid", outOfOrder.ReasonCode);

        var appended = await harness.Fixture.Store.AppendProductionInspectionEventAsync(
            new ProductionInspectionEventWriteRequest(durableAdmission.InspectionId,
                ProductionInspectionEventKind.FaultTerminated, "V142WriterFaultTerminated",
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
            new StoreDeadline(TimeSpan.FromSeconds(4)));

        Assert.True(appended.Committed, appended.ReasonCode);
        var durableEvent = Assert.IsType<ProductionInspectionHistoryEvent>(appended.Event);
        Assert.Equal(ProductionInspectionEventKind.FaultTerminated, durableEvent.Kind);
        Assert.NotSame(core, durableEvent.Core);
        Assert.Equal(core.ContentHash, durableEvent.Core!.ContentHash);

        var faulted = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadCurrentAsync();
        Assert.True(faulted.Available, faulted.ReasonCode);
        Assert.True(faulted.RecoveryRequired);
        Assert.Equal(ProductionInspectionEventKind.FaultTerminated, faulted.Latest!.Kind);

        var duplicateCycle = await BuildAdmissionAsync(harness);
        var duplicate = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(duplicateCycle),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.False(duplicate.Committed);
        Assert.Equal("ProductionInspectionControllerCycleDuplicate", duplicate.ReasonCode);
    }

    [Fact]
    public async Task V142_W02_OrphanCoreAndEventAreRejectedWithoutRows()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var unpersistedAdmission = await BuildAdmissionAsync(harness);
        var orphanCore = TimeoutCore(unpersistedAdmission);
        var beforeEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        var beforeAdmissions = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;");
        var beforeCores = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_cores;");

        var coreResult = await harness.Fixture.Store.CommitProductionInspectionCoreAsync(
            new ProductionInspectionCoreWriteRequest(orphanCore),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.False(coreResult.Committed);
        Assert.Equal("ProductionInspectionAdmissionMissing", coreResult.ReasonCode);

        var eventResult = await harness.Fixture.Store.AppendProductionInspectionEventAsync(
            new ProductionInspectionEventWriteRequest(Guid.NewGuid(),
                ProductionInspectionEventKind.FaultTerminated, "V142OrphanEvent",
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.False(eventResult.Committed);
        Assert.Equal("ProductionInspectionAdmissionMissing", eventResult.ReasonCode);

        Assert.Equal(beforeEvents, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;"));
        Assert.Equal(beforeAdmissions, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.Equal(beforeCores, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_cores;"));
    }

    private static ProductionInspectionCore TimeoutCore(ProductionInspectionAdmission admission) =>
        new(admission,
            state: ProductionInspectionState.CoreCommitted,
            executionStatus: ExecutionStatus.Timeout,
            decision: InspectionDecision.Unknown,
            reasonCode: "CameraAcquisitionTimeout",
            acquisitionFailureKind: CameraAcquisitionFailureKind.TimedOut,
            acquisitionFailureReasonCode: "CameraAcquisitionTimeout",
            frameMetadata: null,
            frameProvenance: null,
            preparedAlgorithmInstanceId: admission.ActivationSnapshot.PreparedAlgorithmInstanceId,
            algorithm: admission.ActivationSnapshot.Release.Source.Content.Algorithm.Algorithm,
            configuration: admission.ActivationSnapshot.Release.Source.Content.Configuration,
            resultSchema: admission.ActivationSnapshot.PlcResultContract.ResultSchema,
            result: null,
            overlay: null,
            timing: null,
            plcPayload: null,
            structuredResultJson: null,
            structuredResultHash: null,
            partIdentity: null,
            committedAtUtc: DateTimeOffset.UtcNow,
            committedMonotonicTimestamp: Stopwatch.GetTimestamp());
}
