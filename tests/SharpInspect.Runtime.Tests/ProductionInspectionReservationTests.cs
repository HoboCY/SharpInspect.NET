using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_S04_AdmissionReservesFutureTailAndColdReadKeepsAdmissionIdentity()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var before = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(before.Available, before.ReasonCode);
        Assert.Equal(9, before.ReservedCycleEntries);
        Assert.Equal(30, before.ReservedAuditEntries);
        var beforeEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        Assert.Equal(0, ReadAuditReserve(harness.Fixture.Options));

        var admission = await BuildAdmissionAsync(harness);
        var committed = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.True(committed.Committed, committed.ReasonCode);

        var after = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(after.Available, after.ReasonCode);
        Assert.Equal(9, after.ReservedCycleEntries);
        Assert.Equal(30, after.ReservedAuditEntries);
        var afterEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        Assert.Equal(8, before.RemainingEntries - after.RemainingEntries -
            (afterEvents - beforeEvents));
        Assert.Equal(11, ReadAuditReserve(harness.Fixture.Options));
        Assert.True(after.ReservedCyclePayloadBytes > 0);

        var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadAsync(admission.InspectionId);
        Assert.True(cold.Available, cold.ReasonCode);
        var eventRow = Assert.IsType<ProductionInspectionHistoryEvent>(cold.Latest);
        Assert.Equal(ProductionInspectionEventKind.Admitted, eventRow.Kind);
        Assert.Equal(admission.ContentHash, eventRow.Admission.ContentHash);
        Assert.NotSame(admission, eventRow.Admission);
    }

    [Fact]
    public async Task V142_S05_FaultAndRecoveryFactsReleaseTheReservedTailInOrder()
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
        var previous = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        var previousEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        Assert.Equal(11, ReadAuditReserve(harness.Fixture.Options));

        var fault = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.FaultTerminated, "V142FaultTerminated");
        Assert.True(fault.Committed, fault.ReasonCode);
        var afterFault = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        var afterFaultEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        Assert.Equal(2, 8 + OutstandingRows(previous, afterFault, previousEvents, afterFaultEvents));
        Assert.Equal(2, ReadAuditReserve(harness.Fixture.Options));

        var recoveryRequired = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.RecoveryRequired, "V142RecoveryRequired");
        Assert.True(recoveryRequired.Committed, recoveryRequired.ReasonCode);
        var afterRequired = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        var afterRequiredEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        Assert.Equal(1, 2 + OutstandingRows(afterFault, afterRequired,
            afterFaultEvents, afterRequiredEvents));
        Assert.Equal(1, ReadAuditReserve(harness.Fixture.Options));

        var recoveryCompleted = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.RecoveryCompleted, "V142RecoveryCompleted");
        Assert.True(recoveryCompleted.Committed, recoveryCompleted.ReasonCode);
        var afterCompleted = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        var afterCompletedEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        Assert.Equal(0, 1 + OutstandingRows(afterRequired, afterCompleted,
            afterRequiredEvents, afterCompletedEvents));
        Assert.Equal(0, ReadAuditReserve(harness.Fixture.Options));

        var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options)
            .ReadAsync(admission.InspectionId);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.False(cold.RecoveryRequired);
        Assert.Equal(ProductionInspectionEventKind.RecoveryCompleted, cold.Latest!.Kind);
    }

    [Fact]
    public async Task V142_S06_OutOfOrderRecoveryCompletionRollsBackWithoutChangingReserve()
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

        var before = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        var beforeEvents = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        var beforeAudit = harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries;");
        var beforeAuditReserve = ReadAuditReserve(harness.Fixture.Options);

        var rejected = await AppendProductionEventAsync(harness, admission.InspectionId,
            ProductionInspectionEventKind.RecoveryCompleted, "V142RecoveryCompletedOutOfOrder");
        Assert.False(rejected.Committed);
        Assert.Equal("ProductionInspectionTransitionInvalid", rejected.ReasonCode);

        var after = await harness.Fixture.Store.ReadProductionInspectionCapacityAsync();
        Assert.Equal(before.ReservedCycleEntries, after.ReservedCycleEntries);
        Assert.Equal(before.ReservedAuditEntries, after.ReservedAuditEntries);
        Assert.Equal(beforeAuditReserve, ReadAuditReserve(harness.Fixture.Options));
        Assert.Equal(beforeEvents, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;"));
        Assert.Equal(beforeAudit, harness.Fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries;"));
    }

    [Fact]
    public async Task V142_S07_AdmissionCapacityRejectsWithoutRowsOrFutureReserve()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var source = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(source, issuer);

        // Keep activation and the real Admission from a healthy default store.
        // The bounded store below only exercises the writer's capacity rollback;
        // it does not need a second activation or camera setup.
        var admission = await BuildAdmissionAsync(source);
        var limitedOptions = new ProductionInspectionStoreOptions
        {
            MaximumEntries = 8,
            MaximumPayloadBytes = 64 * 1024,
            MaximumTotalBytes = 8L * 64 * 1024
        };
        await using var limited = await RecipeDraftStorageTests.Fixture.CreateAsync(
            productionInspections: limitedOptions);

        var capacity = await limited.Store.ReadProductionInspectionCapacityAsync();
        Assert.True(capacity.Available, capacity.ReasonCode);
        Assert.False(capacity.CanAdmit);
        Assert.Equal("ProductionInspectionEntryCapacityExceeded", capacity.ReasonCode);

        var beforeEvents = limited.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;");
        var beforeAdmissions = limited.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;");
        var beforeAudit = limited.Scalar(
            "SELECT COUNT(*) FROM audit_entries;");

        var rejected = await limited.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission),
            new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.False(rejected.Committed);
        Assert.Equal("ProductionInspectionEntryCapacityExceeded", rejected.ReasonCode);

        Assert.Equal(beforeEvents, limited.Scalar(
            "SELECT COUNT(*) FROM production_inspection_events;"));
        Assert.Equal(beforeAdmissions, limited.Scalar(
            "SELECT COUNT(*) FROM production_inspection_admissions;"));
        Assert.Equal(beforeAudit, limited.Scalar(
            "SELECT COUNT(*) FROM audit_entries;"));
        var after = await limited.Store.ReadProductionInspectionCapacityAsync();
        Assert.Equal(0, ReadAuditReserve(limited.Options));
        Assert.Equal(0, after.EntryCount);
    }

    private static ValueTask<ProductionInspectionWriteResult> AppendProductionEventAsync(
        ManualHarness harness, Guid inspectionId, ProductionInspectionEventKind kind,
        string reason) => harness.Fixture.Store.AppendProductionInspectionEventAsync(
            new ProductionInspectionEventWriteRequest(inspectionId, kind, reason,
                DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()),
            new StoreDeadline(TimeSpan.FromSeconds(4)));

    private static long OutstandingRows(ProductionInspectionCapacityResult before,
        ProductionInspectionCapacityResult after, long beforeEvents, long afterEvents) =>
        before.RemainingEntries - after.RemainingEntries - (afterEvents - beforeEvents);

    private static long ReadAuditReserve(ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        return SqliteCommandStore.ReadProductionInspectionAuditReserve(connection.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(4)));
    }
}
