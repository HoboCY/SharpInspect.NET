using System.Diagnostics;
using System.Globalization;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(ProductionInspectionEventKind.Admitted)]
    [InlineData(ProductionInspectionEventKind.FaultTerminated)]
    [InlineData(ProductionInspectionEventKind.RecoveryRequired)]
    [InlineData(ProductionInspectionEventKind.RecoveryCompleted)]
    public async Task V144_D05_LegacyEventEnvelopePreservesFrozenV1Hash(
        ProductionInspectionEventKind kind)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var admission = await BuildAdmissionAsync(harness);
        var recorded = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
        // Frozen T43 v1 hash contract: exactly ten fields, with no trailing
        // recovery placeholder. Both old recovery markers also use this domain.
        var legacyHash = AlgorithmContractValidation.HashParts(new string?[]
        {
            "sharpinspect-production-inspection-history-v1", "3", kind.ToString(),
            admission.ContentHash, null, "LegacyProductionEvent", recorded.ToString("O", CultureInfo.InvariantCulture),
            "123456", "0", null
        });
        var legacy = new ProductionInspectionHistoryEvent(3, kind, admission, null,
            "LegacyProductionEvent", recorded, 123456, contentHash: legacyHash);
        var payload = ProductionInspectionStorageCodec.Encode(legacy);

        var decoded = ProductionInspectionStorageCodec.DecodeEventEnvelope(payload);

        Assert.Equal(legacyHash, decoded.ContentHash);
        Assert.Equal(kind, decoded.Kind);
        Assert.Null(decoded.Recovery);
        Assert.Equal(payload, ProductionInspectionStorageCodec.Encode(decoded));
    }

    [Theory]
    [InlineData(ProductionInspectionEventKind.RecoveryRequired)]
    [InlineData(ProductionInspectionEventKind.RecoveryCompleted)]
    public async Task V144_S02_Schema30RejectsUnauditedRecoveryMarkersWithoutChangingReserve(
        ProductionInspectionEventKind kind)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, enableProductionRecovery: true);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await harness.StopRuntimePreservingFixtureAsync();
        var admission = await BuildAdmissionAsync(harness);
        var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(new(admission),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.True(admitted.Committed, admitted.ReasonCode);
        var beforeEvents = harness.Fixture.Scalar("SELECT COUNT(*) FROM production_inspection_events;");
        var beforeAudit = harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;");
        var beforeReserve = ReadAuditReserve(harness.Fixture.Options);

        var rejected = await AppendProductionEventAsync(harness, admission.InspectionId, kind,
            "UnauditedRecoveryMarker");

        Assert.False(rejected.Committed);
        Assert.Equal("ProductionRecoveryDedicatedWriterRequired", rejected.ReasonCode);
        Assert.Equal(beforeEvents, harness.Fixture.Scalar("SELECT COUNT(*) FROM production_inspection_events;"));
        Assert.Equal(beforeAudit, harness.Fixture.Scalar("SELECT COUNT(*) FROM audit_entries;"));
        Assert.Equal(beforeReserve, ReadAuditReserve(harness.Fixture.Options));
    }

    [Fact]
    public async Task V144_S01_PendingCountCoversEveryInspectionBeforePaging()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true,
            productionPeer: peer, enableProductionRecovery: true,
            productionStore: new ProductionInspectionStoreOptions { MaximumPayloadBytes = 128 * 1024 });
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        await harness.StopRuntimePreservingFixtureAsync();
        var template = await BuildAdmissionAsync(harness);
        var expected = new HashSet<Guid>();
        for (uint cycle = 1; cycle <= 129; cycle++)
        {
            var admission = new ProductionInspectionAdmission(Guid.NewGuid(), Guid.NewGuid(),
                template.RuntimeEpoch, template.StationId, template.AdmissionGeneration,
                new PlcControllerCycle(61, cycle), template.EvidenceRequirement,
                template.ActivationReference, template.ActivationSnapshot,
                template.EndpointBindingHash, template.PlcProfileHash, template.PlcPolicyHash,
                template.ConnectionGeneration, template.ConnectionAttempt,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), template.TracePolicySnapshot);
            var saved = await harness.Fixture.Store.AdmitProductionInspectionAsync(new(admission),
                new StoreDeadline(TimeSpan.FromSeconds(4)));
            Assert.True(saved.Committed, saved.ReasonCode);
            expected.Add(admission.InspectionId);
        }

        var query = new SqliteProductionRecoveryHistoryQuery(harness.Fixture.Options);
        var first = await query.QueryPendingAsync(pageSize: 128);
        Assert.True(first.Available, first.ReasonCode);
        Assert.Equal(129, first.PendingCount);
        Assert.Equal(128, first.Items.Count);
        Assert.True(first.NextAfterPosition.HasValue);
        var last = await query.QueryPendingAsync(pageSize: 128,
            afterPosition: first.NextAfterPosition.Value);
        Assert.True(last.Available, last.ReasonCode);
        Assert.Equal(129, last.PendingCount);
        Assert.Single(last.Items);
        Assert.Null(last.NextAfterPosition);
        Assert.True(expected.SetEquals(first.Items.Concat(last.Items).Select(value => value.InspectionId)));
    }
}
