using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("event")]
    [InlineData("admission")]
    [InlineData("core")]
    public async Task V142_S08_TamperedProjectionOrRehashedEnvelopeBlocksColdReadAndFurtherWrites(string target)
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);
        var admission = await BuildAdmissionAsync(harness);
        var admitted = await harness.Fixture.Store.AdmitProductionInspectionAsync(
            new ProductionInspectionAdmissionWriteRequest(admission), new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.True(admitted.Committed, admitted.ReasonCode);
        var committed = await harness.Fixture.Store.CommitProductionInspectionCoreAsync(
            new ProductionInspectionCoreWriteRequest(TimeoutCore(admission)), new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.True(committed.Committed, committed.ReasonCode);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = harness.Fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            var triggerName = "production_inspection_" + target + "_immutable_update";
            await using var lookup = connection.CreateCommand();
            lookup.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name;";
            lookup.Parameters.AddWithValue("$name", triggerName);
            var triggerSql = Assert.IsType<string>(await lookup.ExecuteScalarAsync());
            await using var command = connection.CreateCommand();
            if (target == "event")
            {
                var original = committed.Event!;
                var changed = new ProductionInspectionHistoryEvent(original.Position, original.Kind,
                    original.Admission, original.Core, "V142TamperedReason", original.RecordedAtUtc,
                    original.MonotonicTimestamp, original.AuditSequence, original.AuditHash);
                var payload = ProductionInspectionStorageCodec.Encode(changed);
                command.CommandText = $"BEGIN IMMEDIATE; DROP TRIGGER {triggerName}; " +
                    "UPDATE production_inspection_events SET ReasonCode=$reason,ContentHash=$content," +
                    "PayloadHash=$hash,Payload=$payload WHERE Position=$position; " + triggerSql + "; COMMIT;";
                command.Parameters.AddWithValue("$reason", changed.ReasonCode);
                command.Parameters.AddWithValue("$content", changed.ContentHash);
                command.Parameters.AddWithValue("$hash", ProductionInspectionStorageCodec.PayloadHash(payload));
                command.Parameters.AddWithValue("$payload", Convert.ToBase64String(payload));
                command.Parameters.AddWithValue("$position", changed.Position);
            }
            else
            {
                var table = target == "admission" ? "production_inspection_admissions" : "production_inspection_cores";
                var assignment = target == "admission" ? "StationId='V142TamperedStation'" : "ReasonCode='V142TamperedCore'";
                command.CommandText = $"BEGIN IMMEDIATE; DROP TRIGGER {triggerName}; UPDATE {table} SET {assignment}; " +
                    triggerSql + "; COMMIT;";
            }
            await command.ExecuteNonQueryAsync();
        }

        var before = harness.Fixture.Scalar("SELECT COUNT(*) FROM production_inspection_events;");
        var cold = await new SqliteProductionInspectionHistoryQuery(harness.Fixture.Options).ReadCurrentAsync();
        Assert.False(cold.Available);
        Assert.Equal(target switch
        {
            "event" => "ProductionInspectionCentralPayloadBindingMismatch",
            "admission" => "ProductionInspectionAdmissionProjectionMismatch",
            _ => "ProductionInspectionCoreProjectionMismatch"
        }, cold.ReasonCode);
        var rejected = await harness.Fixture.Store.AppendProductionInspectionEventAsync(
            new ProductionInspectionEventWriteRequest(admission.InspectionId,
                ProductionInspectionEventKind.FaultTerminated, "V142AfterTampering", DateTimeOffset.UtcNow,
                Stopwatch.GetTimestamp()), new StoreDeadline(TimeSpan.FromSeconds(4)));
        Assert.False(rejected.Committed);
        Assert.Equal(before, harness.Fixture.Scalar("SELECT COUNT(*) FROM production_inspection_events;"));
    }
}
