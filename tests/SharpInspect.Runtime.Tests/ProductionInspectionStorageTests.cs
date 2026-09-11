using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_S01_CapacityRejectsWhenOneCompleteCycleCannotFit()
    {
        await using var fixture = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionInspections: new ProductionInspectionStoreOptions
            {
                MaximumEntries = 6,
                MaximumPayloadBytes = 4096,
                MaximumTotalBytes = 7L * 4096
            });

        var capacity = await fixture.Store.ReadProductionInspectionCapacityAsync();

        Assert.True(capacity.Available, capacity.ReasonCode);
        Assert.False(capacity.CanAdmit);
        Assert.Equal("ProductionInspectionEntryCapacityExceeded", capacity.ReasonCode);
        Assert.Equal(0, capacity.EntryCount);
        Assert.Equal(0, capacity.PayloadBytes);
        Assert.Equal(6, capacity.RemainingEntries);
        Assert.Equal(9, capacity.ReservedCycleEntries);
    }

    [Fact]
    public async Task V142_S02_FinalGuardRejectsAdmissionWithoutPersistingRows()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var admission = await BuildAdmissionAsync(harness);
        var beforeAdmissions = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;");
        var beforeCores = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_cores;");
        var beforeEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");

        var result = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission,
                () => "V142FinalGuardRejected"),
            new StoreDeadline(TimeSpan.FromSeconds(4)));

        Assert.False(result.Committed);
        Assert.Equal("V142FinalGuardRejected", result.ReasonCode);
        Assert.Equal(beforeAdmissions, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.Equal(beforeCores, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_cores;"));
        Assert.Equal(beforeEvents, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;"));
    }

    [Fact]
    public async Task V142_S03_ColdReaderReconstructsCommittedCoreAndAdmission()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        peer.HoldFirstPayloadWrite = false;
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await ArmProductionAsync(harness);
        await WaitProductionAsync(harness,
            state => state.Ready && state.ArmState == ProductionArmState.Armed,
            "Production ready before cold Core read");

        peer.RaiseTrigger(61, 1);
        await WaitConditionAsync(() => peer.ResultValidHighCount >= 1,
            "Production result publication before cold Core read");
        peer.SetTrigger(false);
        await WaitConditionAsync(() => peer.AckLowCount >= 1,
            "Production ACK reset before cold Core read");

        // The history query opens a new read-only SQLite connection and fully
        // decodes the immutable envelope, which is the cold-reader boundary.
        var query = new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options);
        var page = await query.QueryAsync(new(PageSize: 128));
        Assert.True(page.Available, page.ReasonCode);
        var committed = Assert.Single(page.Events, value =>
            value.Kind == ProductionInspectionEventKind.CoreCommitted);
        var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadAsync(committed.InspectionId);

        Assert.True(cold.Available, cold.ReasonCode);
        var core = Assert.IsType<ProductionInspectionCore>(cold.Latest!.Core);
        Assert.Equal(committed.InspectionId, core.Admission.InspectionId);
        Assert.Equal(ExecutionStatus.Success, core.ExecutionStatus);
        Assert.Equal(InspectionDecision.Pass, core.Decision);
        Assert.Equal(core.Admission.ContentHash, cold.Latest.Admission.ContentHash);
        Assert.NotNull(core.FrameMetadata);
        Assert.NotNull(core.FrameProvenance);
        Assert.NotNull(core.Result);
        Assert.NotNull(core.Overlay);
        Assert.NotNull(core.PlcPayload);
        Assert.Empty(core.RetentionObligations);
    }

    private static async Task<ProductionInspectionAdmission> BuildAdmissionAsync(
        ManualHarness harness)
    {
        var activation = await harness.Activations.ReadCurrentAsync();
        Assert.True(activation.Available, activation.ReasonCode);
        var record = Assert.IsType<RecipeActivationRecord>(activation.Record);
        var snapshot = Assert.IsType<RecipeActivationSnapshot>(record.SuccessfulSnapshot);
        Assert.True(record.ProductionAuthority);

        var policy = await new SqliteTraceStoragePolicyQuery(harness.Fixture.Options).ReadAsync();
        Assert.True(policy.Available, policy.ReasonCode);
        var traceSnapshot = Assert.IsType<TraceStoragePolicySnapshot>(policy.Snapshot);
        var options = harness.Service<ProductionInspectionOptions>();

        return new ProductionInspectionAdmission(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), options.StationId, 0,
            new PlcControllerCycle(61, 1), options.EvidenceRequirement,
            record.Reference, snapshot, options.Profile.EndpointBindingHash,
            options.Profile.ContentHash, options.Profile.CommunicationBinding.Policy.ContentHash,
            0, 0, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), traceSnapshot);
    }
}
