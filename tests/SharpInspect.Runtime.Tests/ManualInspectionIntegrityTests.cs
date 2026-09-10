using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Theory]
    [InlineData("payload")]
    [InlineData("scalar")]
    public async Task V135_Q07_TamperedManualRowIsRejectedByHistoryAndAudit(
        string tamperKind)
    {
        await using var harness = await ManualHarness.CreateAsync();

        AssertAccepted(await harness.StartAsync(), "Manual integrity start");
        var ready = await harness.WaitForSnapshotAsync(value =>
            value.Phase == ManualInspectionSessionPhase.ReadyForRun,
            "Manual integrity session did not become ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        AssertAccepted(await harness.ExitAsync(sessionId,
            ManualInspectionExitMode.Graceful), "Manual integrity exit");
        await harness.WaitForSnapshotAsync(value =>
            value.SessionId == sessionId &&
            value.Phase == ManualInspectionSessionPhase.Closed,
            "Manual integrity session did not close");

        var healthy = await harness.History.QueryAsync(new(PageSize: 128));
        Assert.True(healthy.Available, healthy.ReasonCode);
        Assert.NotEmpty(healthy.Events);
        await harness.Fixture.WaitForVerifiedAsync();

        // The mutation is performed only after all writers are stopped.  The
        // immutable trigger is restored immediately, while the signed audit
        // row is deliberately left untouched.
        await ((StationRuntime)harness.Runtime).DisposeAsync();
        await harness.Fixture.Store.DisposeAsync();
        TamperManualRow(harness.Fixture.Options.DatabasePath, tamperKind);
        var beforeRead = Fingerprint(harness.Fixture.Options.DatabasePath);

        var history = new SqliteManualInspectionQuery(harness.Fixture.Options);
        var current = await history.ReadCurrentAsync();
        Assert.False(current.Available, current.ReasonCode);
        Assert.Contains("ManualInspection", current.ReasonCode, StringComparison.Ordinal);

        var page = await history.QueryAsync(new ManualInspectionHistoryFilter(PageSize: 128));
        Assert.False(page.Available, page.ReasonCode);
        Assert.Contains("ManualInspection", page.ReasonCode, StringComparison.Ordinal);
        Assert.Empty(page.Events);
        Assert.Empty(page.Runs);

        var audit = await new SqliteAuditIntegrityQuery(harness.Fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Contains("ManualInspection", audit.ReasonCode, StringComparison.Ordinal);

        // Both consumers are read-only; a failed verification must not repair
        // or rewrite the tampered database.
        Assert.Equal(beforeRead, Fingerprint(harness.Fixture.Options.DatabasePath));
    }

    private static void TamperManualRow(string databasePath, string tamperKind)
    {
        var sql = tamperKind switch
        {
            "payload" => @"
                DROP TRIGGER manual_inspection_event_immutable_update;
                UPDATE manual_inspection_events
                SET Payload='dGFtcGVyZWQ='
                WHERE Position=1;
                CREATE TRIGGER manual_inspection_event_immutable_update
                BEFORE UPDATE ON manual_inspection_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableManualInspectionEvent');
                END;",
            "scalar" => @"
                DROP TRIGGER manual_inspection_event_immutable_update;
                UPDATE manual_inspection_events
                SET ReasonCode='ManualTamperedReason'
                WHERE Position=1;
                CREATE TRIGGER manual_inspection_event_immutable_update
                BEFORE UPDATE ON manual_inspection_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableManualInspectionEvent');
                END;",
            _ => throw new ArgumentOutOfRangeException(nameof(tamperKind), tamperKind, null)
        };

        using var connection = SqliteNative.Open(databasePath, readOnly: false);
        SqliteNative.Execute(connection.Handle!, sql,
            new StoreDeadline(TimeSpan.FromSeconds(5)));
    }

    private static DatabaseFingerprint Fingerprint(string databasePath)
    {
        var info = new FileInfo(databasePath);
        using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        return new DatabaseFingerprint(info.Length, info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(sha.ComputeHash(stream)));
    }

    private sealed record DatabaseFingerprint(long Length, long LastWriteTicks, string Hash);
}
