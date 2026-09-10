using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V137_S01_ClosedSessionRejectsFurtherAppendWithoutChangingCursor()
    {
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification storage closed-session start");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "station qualification storage session did not close");
        var sessionId = Assert.IsType<Guid>(closed.SessionId);
        var before = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(before.Available, before.ReasonCode);
        var last = Assert.IsType<StationQualificationSessionEvent>(
            before.Events.OrderBy(value => value.Position).LastOrDefault());
        Assert.True(last.Terminal);

        var rejected = await harness.Fixture.Store.AppendStationQualificationProgressAsync(
            new StationQualificationProgressRequest(
                last.Header, last.Position, last.ContentHash, Guid.NewGuid(), Guid.NewGuid(),
                AuditedCommandKind.ExitStationQualificationSession,
                StationQualificationSessionPhase.Closed,
                StationQualificationRestorationState.Restored,
                "V137 storage append after closed", Terminal: true),
            new StoreDeadline(TimeSpan.FromSeconds(2)));

        Assert.False(rejected.Accepted);
        Assert.Contains("Terminal", rejected.Outcome.ReasonCode,
            StringComparison.OrdinalIgnoreCase);

        var after = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(after.Available, after.ReasonCode);
        var afterLast = Assert.IsType<StationQualificationSessionEvent>(
            after.Events.OrderBy(value => value.Position).LastOrDefault());
        Assert.Equal(last.Position, afterLast.Position);
        Assert.Equal(last.ContentHash, afterLast.ContentHash);
    }

    [Fact]
    public async Task V137_S02_RecoveryAttemptNonceAndEpochAreCheckedAfterRestoring()
    {
        await using var harness = await QualificationHarness.CreateAsync(
            blockStimulus: true, restoreFailure: true, allowDisposeFailure: true);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification storage recovery start");
        var ready = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.ReadyForStimulus,
            "station qualification storage recovery did not reach ready");
        var sessionId = Assert.IsType<Guid>(ready.SessionId);
        AssertAccepted(await harness.Runtime.SubmitAsync(harness.ExitCommand(sessionId, abort: false)),
            "station qualification storage recovery exit");
        var blocked = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.RecoveryBlocked,
            "station qualification storage recovery was not blocked");
        var blockedPage = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(blockedPage.Available, blockedPage.ReasonCode);
        var blockedEvent = Assert.IsType<StationQualificationSessionEvent>(
            blockedPage.Events.OrderBy(value => value.Position).LastOrDefault());
        Assert.Equal(StationQualificationSessionPhase.RecoveryBlocked, blockedEvent.Phase);
        Assert.NotNull(blockedEvent.RecoveryAttempt);

        var recoveryAttempt = new StationQualificationRecoveryAttempt(
            Guid.NewGuid(), Guid.NewGuid());
        var restoring = await harness.Fixture.Store.AppendStationQualificationProgressAsync(
            RecoveryRequest(blockedEvent, recoveryAttempt,
                StationQualificationSessionPhase.Restoring,
                StationQualificationRestorationState.Pending,
                "V137 storage recovery attempt started"),
            new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.True(restoring.Accepted, restoring.Outcome.ReasonCode);

        var restoringPage = await harness.History.QueryAsync(new StationQualificationHistoryFilter(
            SessionId: sessionId, PageSize: 128));
        Assert.True(restoringPage.Available, restoringPage.ReasonCode);
        var restoringEvent = Assert.IsType<StationQualificationSessionEvent>(
            restoringPage.Events.OrderBy(value => value.Position).LastOrDefault());
        Assert.Equal(StationQualificationSessionPhase.Restoring, restoringEvent.Phase);

        foreach (var wrongAttempt in new[]
        {
            new StationQualificationRecoveryAttempt(recoveryAttempt.RuntimeEpoch, Guid.NewGuid()),
            new StationQualificationRecoveryAttempt(Guid.NewGuid(), recoveryAttempt.LeaseNonce)
        })
        {
            var rejected = await harness.Fixture.Store.AppendStationQualificationProgressAsync(
                RecoveryRequest(restoringEvent, wrongAttempt,
                    StationQualificationSessionPhase.Restoring,
                    StationQualificationRestorationState.Pending,
                    "V137 storage wrong recovery attempt"),
                new StoreDeadline(TimeSpan.FromSeconds(2)));
            Assert.False(rejected.Accepted);
            Assert.Contains("RecoveryAttempt", rejected.Outcome.ReasonCode,
                StringComparison.OrdinalIgnoreCase);
        }

        var blockedAgain = await harness.Fixture.Store.AppendStationQualificationProgressAsync(
            RecoveryRequest(restoringEvent, recoveryAttempt,
                StationQualificationSessionPhase.RecoveryBlocked,
                StationQualificationRestorationState.RecoveryBlocked,
                "V137 storage recovery remains blocked"),
            new StoreDeadline(TimeSpan.FromSeconds(2)));
        Assert.True(blockedAgain.Accepted, blockedAgain.Outcome.ReasonCode);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("identity")]
    public async Task V137_S03_ReverseCommandOrIdentityReferenceTamperIsRejected(string reference)
    {
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(),
            "station qualification storage tamper start");
        var closed = await harness.WaitForSnapshotAsync(snapshot =>
            snapshot.Phase == StationQualificationSessionPhase.Closed,
            "station qualification storage tamper session did not close");
        var sessionId = Assert.IsType<Guid>(closed.SessionId);
        TamperReference(harness.Fixture.Options.DatabasePath, sessionId, reference);

        var result = await new SqliteStationQualificationHistoryQuery(harness.Fixture.Options)
            .ReadAsync(sessionId);

        Assert.False(result.Available);
        Assert.Contains("StationQualification", result.ReasonCode,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task V137_S14_GenericCommandAppendCannotExtendCorruptedQualificationHistory()
    {
        await using var harness = await QualificationHarness.CreateAsync(maximumRuns: 1);
        AssertAccepted(await harness.StartWithFreshStepUpAsync(), "generic append integrity start");
        var closed = await harness.WaitForSnapshotAsync(value =>
            value.Phase == StationQualificationSessionPhase.Closed, "qualification did not close");
        TamperReference(harness.Fixture.Options.DatabasePath, closed.SessionId!.Value, "command");
        var before = SharedFileHash(harness.Fixture.Options.DatabasePath);
        using var read = SqliteNative.Open(harness.Fixture.Options.DatabasePath, readOnly: true);
        var beforeTail = SharpInspect.Runtime.Integrity.AuditChainDatabase.Tail(read.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(2)));
        var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), closed.RuntimeEpoch,
            DateTimeOffset.UtcNow, AuditedCommandKind.Unsupported, CommandSource.PhysicalConsole,
            null, null, null, CommandAuditPhase.Outcome, CommandDisposition.Rejected, "UnsupportedCommand");
        var result = await harness.Fixture.Store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(5)));
        Assert.False(result.Committed, result.ReasonCode);
        Assert.Equal(before, SharedFileHash(harness.Fixture.Options.DatabasePath));
        Assert.Equal(beforeTail, SharpInspect.Runtime.Integrity.AuditChainDatabase.Tail(read.Handle!,
            new StoreDeadline(TimeSpan.FromSeconds(2))));

        static byte[] SharedFileHash(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var algorithm = System.Security.Cryptography.SHA256.Create();
            return algorithm.ComputeHash(input);
        }
    }

    private static StationQualificationProgressRequest RecoveryRequest(
        StationQualificationSessionEvent previous,
        StationQualificationRecoveryAttempt attempt,
        StationQualificationSessionPhase phase,
        StationQualificationRestorationState restoration,
        string reason) => new(
            previous.Header, previous.Position, previous.ContentHash,
            previous.CommandCorrelationId, previous.AttemptId, previous.CommandKind,
            phase, restoration, reason, Terminal: false, CompleteCommand: false,
            RecoveryAttempt: attempt, CommandAuthorizationTarget: previous.CommandAuthorizationTarget);

    private static void TamperReference(string databasePath, Guid sessionId, string reference)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        var column = reference switch
        {
            "command" => "CommandAuditSequence=AuthorizationAuditSequence, CommandAuditHash=AuthorizationAuditHash",
            "identity" => "AuthorizationAuditSequence=CommandAuditSequence, AuthorizationAuditHash=CommandAuditHash",
            _ => throw new ArgumentOutOfRangeException(nameof(reference))
        };
        command.CommandText = $"DROP TRIGGER station_qualification_event_immutable_update; " +
            $"UPDATE station_qualification_events SET {column} WHERE SessionId=$session; " +
            "CREATE TRIGGER station_qualification_event_immutable_update BEFORE UPDATE " +
            "ON station_qualification_events BEGIN " +
            "SELECT RAISE(ABORT,'ImmutableStationQualificationEvent'); END;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.ExecuteNonQuery();
    }
}
