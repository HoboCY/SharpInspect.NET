using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("VerificationId", "V153_M03")]
    public async Task V153_M03_GovernedMigrationPreservesNonemptyOutboxRowsAndFrozenBytes(bool permanent)
    {
        // This is the current assembly's exact schema-36 compatibility profile. The separate
        // NuGet consumer probe proves the preserved historical binary path independently.
        await using var fixture = await OutboxCoreFixture.CreateAsync();
        await fixture.CommitAsync();
        var delivery = fixture.Batch.Deliveries[0];
        if (permanent) await PermanentlyBlockAsync(fixture, delivery.DeliveryId);
        // The storage fixture supplies the acknowledged local publication/reset sequence.
        // Pending external delivery remains an independent obligation and is never discarded.
        foreach (var kind in new[] { ProductionInspectionEventKind.PublicationPrepared,
                     ProductionInspectionEventKind.ResultValidRaised, ProductionInspectionEventKind.ResultAcknowledged,
                     ProductionInspectionEventKind.ResultValidCleared, ProductionInspectionEventKind.AcknowledgementReset })
        {
            var appended = await fixture.Store.AppendProductionInspectionEventAsync(
                new(fixture.Core.Admission.InspectionId, kind, "V153MigrationLocalCycleClosure",
                    DateTimeOffset.UtcNow, Stopwatch.GetTimestamp()), fixture.Deadline());
            Assert.True(appended.Committed, appended.ReasonCode);
        }
        var history = await fixture.Query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.True(history.Available, history.ReasonCode);
        await fixture.Store.DisposeAsync();
        const long budget = 64L * 1024 * 1024;
        MigrationDatabaseFingerprint source;
        using (var connection = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
            source = StoreMigrationFingerprint.Read(connection.Handle!, budget,
                new StoreDeadline(TimeSpan.FromSeconds(30)));
        Assert.Equal(36, source.SchemaVersion);
        Assert.Contains(source.Tables, table => table.Name == "production_outbox_events" && table.RowCount > 0);
        var oldOutbox = fixture.Options.Outbox!;
        var target = ProductionOutboxRecoveryTests.CopyWithOutbox(fixture.Options,
            new ProductionOutboxStoreOptions(oldOutbox.Routes, oldOutbox.RecipeLifecycle,
                oldOutbox.ImageEvidence, oldOutbox.ImageFinalization)
            {
                MaximumAttempts = oldOutbox.MaximumAttempts, MaximumRetryDelay = oldOutbox.MaximumRetryDelay,
                AttemptTimeout = oldOutbox.AttemptTimeout, MaximumEvents = oldOutbox.MaximumEvents,
                MaximumPayloadBytes = oldOutbox.MaximumPayloadBytes, MaximumTotalBytes = oldOutbox.MaximumTotalBytes,
                MaximumPageSize = oldOutbox.MaximumPageSize, ManualRecovery = new()
            });
        var opened = await SqliteStartupMaintenance.OpenAsync(target,
            new StoreStartupMaintenanceOptions(typeof(SqliteStartupMaintenance).Assembly.Location)
            { MaximumDatabaseBytes = budget, OperationTimeout = TimeSpan.FromSeconds(30) });
        Assert.True(opened.Available, opened.Status.ReasonCode);
        await using (var session = opened.Session!)
        {
            var completed = await StoreStartupMaintenanceTests.Finish(session);
            Assert.True(completed.Completed, completed.ReasonCode);
            Assert.False(completed.Ready);
            Assert.Equal(36, completed.SourceSchemaVersion);
            Assert.Equal(37, completed.TargetSchemaVersion);
            Assert.Equal(source.ContentHash, completed.Backup!.SourceFingerprint);
        }
        using (var connection = SqliteNative.Open(target.DatabasePath, readOnly: true))
            Assert.True(StoreMigrationFingerprint.ExistingRowsUnchanged(connection.Handle!, source.Tables,
                budget, new StoreDeadline(TimeSpan.FromSeconds(30))));
        await using var writer = new SqliteCommandStore(target);
        var initialized = await writer.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        var query = new SqliteProductionOutboxQuery(target);
        var migratedHistory = await query.ReadHistoryAsync(deliveryId: delivery.DeliveryId);
        Assert.True(migratedHistory.Available, migratedHistory.ReasonCode);
        Assert.Equal(history.Events.Select(x => x.ContentHash), migratedHistory.Events.Select(x => x.ContentHash));
        var pending = await query.ReadPendingAsync();
        Assert.True(pending.Available, pending.ReasonCode);
        Assert.True(pending.Items.Count == 1, "Migrated pending delivery missing");
        var preserved = Assert.Single(pending.Items);
        Assert.Equal(permanent, preserved.PermanentBlock);
        Assert.Equal(delivery.ContentHash, preserved.Delivery.ContentHash);
        Assert.Equal(delivery.Payload!.CopyBytes(), preserved.Delivery.Payload!.CopyBytes());
        // The new constraint supports a real persistent handler block while all old facts
        // (including their NULL fields, rowids and signed hashes) keep their original values.
        var blocked = await writer.BlockOutboxHandlerAsync(new(delivery.DeliveryId, preserved.StateRevisionHash,
            DateTimeOffset.UtcNow, "OutboxRequiredHistoricalHandlerMissing"), fixture.Deadline());
        Assert.True(blocked.Committed, blocked.ReasonCode);
        var afterBlock = await query.ReadPendingAsync();
        Assert.True(afterBlock.Available, afterBlock.ReasonCode);
        Assert.True(Assert.Single(afterBlock.Items).PermanentBlock);
    }
}
