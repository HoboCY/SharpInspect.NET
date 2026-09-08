#pragma warning disable CA1416

using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraNetworkCapacityTests
{
    [Fact]
    public async Task V120_K01_SmallAuditBudgetRejectsAdmissionAtBoundaryWithoutPendingRow()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            maximumVerificationEntries: 201);
        var prepared = await fixture.PrepareAdmissionAsync();
        try
        {
            StoreWriteResult? exhausted = null;
            for (var index = 0; index < 400; index++)
            {
                await fixture.WaitForVerifiedAsync();
                var result = await fixture.Store.AppendAsync(PaddingFact(),
                    new StoreDeadline(fixture.Options.CommitTimeout));
                exhausted = result;
                if (!result.Committed) break;
            }

            Assert.NotNull(exhausted);
            Assert.False(exhausted!.Committed);
            Assert.Contains("CapacityExceeded", exhausted.ReasonCode, StringComparison.Ordinal);
            await fixture.WaitForVerifiedAsync();
            Assert.Equal(0L, await fixture.ScalarAsync(
                "SELECT COUNT(*) FROM camera_network_events WHERE Phase=0;"));

            var admission = await fixture.Persistence.AppendAdmissionAsync(prepared.Request,
                fixture.StationNetwork, fixture.Previous, prepared.Authorization,
                prepared.RuntimeEpoch, new StoreDeadline(fixture.Options.CommitTimeout));
            Assert.False(admission.Result.Committed);
            Assert.Contains("CapacityExceeded", admission.Result.ReasonCode,
                StringComparison.Ordinal);
            Assert.Null(admission.Admission);
            Assert.False(await fixture.Persistence.HasUnresolvedAsync());
            Assert.Equal(0L, await fixture.ScalarAsync(
                "SELECT COUNT(*) FROM camera_network_events WHERE Phase=0;"));
        }
        finally
        {
            prepared.Authorization.Reservation?.Dispose();
        }
    }

    [Fact]
    public async Task V120_K02_PendingAdmissionReservesTerminalAuditCapacityFromGenericFacts()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            maximumVerificationEntries: 201);
        var admitted = await fixture.AdmitAsync();
        var logout = await fixture.Sessions.LogoutAsync(admitted.Invocation.SessionId!.Value);
        Assert.True(logout.Succeeded, logout.ReasonCode);

        StoreWriteResult? exhausted = null;
        for (var index = 0; index < 400; index++)
        {
            await fixture.WaitForVerifiedAsync();
            var result = await fixture.Store.AppendAsync(PaddingFact(),
                new StoreDeadline(fixture.Options.CommitTimeout));
            exhausted = result;
            if (!result.Committed) break;
        }

        Assert.NotNull(exhausted);
        Assert.False(exhausted!.Committed);
        Assert.Contains("CapacityExceeded", exhausted.ReasonCode, StringComparison.Ordinal);
        await fixture.WaitForVerifiedAsync();

        var terminalSnapshot = new CameraNetworkSnapshot(admitted.Request.OperationId,
            admitted.Request.Target, CameraNetworkMaintenanceState.Succeeded,
            admitted.Previous, admitted.Request.Requested, admitted.Request.Requested, true,
            "CameraNetworkChanged", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));

        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        Assert.False(await fixture.Persistence.HasUnresolvedAsync());
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE Phase=1;"));
    }

    [Fact]
    public async Task V120_K03_MaximumEventsReservesPendingTerminalSlotFromAdmissionAndRejectedEvent()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            networkOptions: new CameraNetworkStoreOptions { MaximumEvents = 2 });
        var admitted = await fixture.AdmitAsync();

        var second = await fixture.PrepareAdmissionAsync(fixture.OtherTarget);
        try
        {
            var secondAdmission = await fixture.Persistence.AppendAdmissionAsync(second.Request,
                fixture.StationNetwork, fixture.Previous, second.Authorization,
                second.RuntimeEpoch, new StoreDeadline(fixture.Options.CommitTimeout));
            Assert.False(secondAdmission.Result.Committed);
            Assert.Equal("CameraNetworkEventCapacityExceeded", secondAdmission.Result.ReasonCode);
            Assert.Null(secondAdmission.Admission);
        }
        finally
        {
            second.Authorization.Reservation?.Dispose();
        }

        var rejectedRequest = fixture.Request(Guid.NewGuid(), fixture.OtherTarget,
            new CommandInvocation(CommandSource.Integration, "anonymous"));
        var rejected = await fixture.Persistence.RecordRejectedAsync(rejectedRequest,
            fixture.StationNetwork, null, null, Guid.NewGuid(), "PermissionDenied",
            new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.False(rejected.Committed);
        Assert.Equal("CameraNetworkEventCapacityExceeded", rejected.ReasonCode);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_network_events;"));
        Assert.True(await fixture.Persistence.HasUnresolvedAsync());

        var terminalSnapshot = new CameraNetworkSnapshot(admitted.Request.OperationId,
            admitted.Request.Target, CameraNetworkMaintenanceState.Failed,
            admitted.Previous, admitted.Request.Requested, null, false,
            "CameraNetworkApplyFailed", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        Assert.False(await fixture.Persistence.HasUnresolvedAsync());
    }

    [Fact]
    public async Task V120_K04_MaximumTotalBytesReservesPendingTerminalPayloadFromRejectedEvent()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            networkOptions: new CameraNetworkStoreOptions
            {
                MaximumPayloadBytes = 4096,
                MaximumTotalBytes = 8192,
                MaximumEvents = 64
            });
        var admitted = await fixture.AdmitAsync();

        StoreWriteResult? exhausted = null;
        var acceptedRejectedCount = 0;
        for (var index = 0; index < 64; index++)
        {
            var rejectedRequest = fixture.Request(Guid.NewGuid(), fixture.OtherTarget,
                new CommandInvocation(CommandSource.Integration, "anonymous"));
            var result = await fixture.Persistence.RecordRejectedAsync(rejectedRequest,
                fixture.StationNetwork, null, null, Guid.NewGuid(), "PermissionDenied",
                new StoreDeadline(fixture.Options.CommitTimeout));
            exhausted = result;
            if (!result.Committed) break;
            acceptedRejectedCount++;
        }

        Assert.True(acceptedRejectedCount > 0);
        Assert.NotNull(exhausted);
        Assert.False(exhausted!.Committed);
        Assert.Equal("CameraNetworkTotalCapacityExceeded", exhausted.ReasonCode);
        Assert.Equal(1L + acceptedRejectedCount,
            await fixture.ScalarAsync("SELECT COUNT(*) FROM camera_network_events;"));

        var terminalSnapshot = new CameraNetworkSnapshot(admitted.Request.OperationId,
            admitted.Request.Target, CameraNetworkMaintenanceState.Failed,
            admitted.Previous, admitted.Request.Requested, null, false,
            "CameraNetworkApplyFailed", DateTimeOffset.UtcNow);
        var terminal = await fixture.Persistence.AppendTerminalAsync(admitted.Admission,
            terminalSnapshot, new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(terminal.Committed, terminal.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        Assert.False(await fixture.Persistence.HasUnresolvedAsync());
    }

    [Fact]
    public async Task V120_K05_EncodedPayloadBoundFailsClosedOnTamperedNetworkLedger()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            networkOptions: new CameraNetworkStoreOptions { MaximumEvents = 2 });
        _ = await fixture.AdmitAsync();
        await fixture.StopStoreAsync();
        await TamperWithOversizedNetworkPayloadAsync(fixture.Options.DatabasePath);

        var report = await new SqliteAuditIntegrityQuery(fixture.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
        Assert.Contains("CapacityExceeded", report.ReasonCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V120_K06_RowCountBoundFailsClosedOnTamperedNetworkLedger()
    {
        RequireWindows();
        await using var fixture = await CameraNetworkTestFixture.CreateAsync(
            networkOptions: new CameraNetworkStoreOptions { MaximumEvents = 2 });
        _ = await fixture.AdmitAsync();
        await fixture.StopStoreAsync();
        await TamperWithExtraNetworkRowsAsync(fixture.Options.DatabasePath);

        using var connection = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true);
        CameraNetworkStoreOptions.ConfigureSqliteLimit(connection.Handle!);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteCommandStore.ValidateCameraNetworkHistory(connection.Handle!,
                fixture.Options.CameraNetwork!, new StoreDeadline(fixture.Options.QueryTimeout)));
        Assert.Equal("CameraNetworkEventCapacityExceeded", exception.Message);

        var report = await new SqliteAuditIntegrityQuery(fixture.Options)
            .VerifyAsync(new AuditVerificationRequest());
        Assert.Equal(AuditIntegrityState.Faulted, report.State);
    }

    private static CommandAuditFact PaddingFact() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        AuditedCommandKind.ArmProduction, CommandSource.PhysicalConsole,
        "V120-budget-padding", null, null, CommandAuditPhase.Outcome,
        CommandDisposition.Rejected, "V120BudgetPadding");

    private static async Task TamperWithOversizedNetworkPayloadAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            DROP TRIGGER camera_network_event_immutable_update;
            UPDATE camera_network_events SET Payload=$payload WHERE Position=1;
            CREATE TRIGGER camera_network_event_immutable_update BEFORE UPDATE ON camera_network_events BEGIN
                SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
            END;";
        command.Parameters.AddWithValue("$payload", new string('A', 400_000));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task TamperWithExtraNetworkRowsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT Payload FROM camera_network_events WHERE Position=1;";
        var payload = Convert.FromBase64String((string)(await read.ExecuteScalarAsync())!);
        var original = CameraNetworkStorageCodec.Decode(payload, 1);
        await ExecuteAsync(connection, "DROP TRIGGER camera_network_event_immutable_update;");
        await ExecuteAsync(connection, "DROP TRIGGER camera_network_event_immutable_delete;");
        try
        {
            for (var position = 2; position <= 3; position++)
            {
                var value = original with
                {
                    Position = position,
                    EventId = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    AttemptId = Guid.NewGuid(),
                    CorrelationId = Guid.NewGuid(),
                    RuntimeEpoch = Guid.NewGuid()
                };
                var encoded = CameraNetworkStorageCodec.Encode(value);
                await using var insert = connection.CreateCommand();
                insert.CommandText = @"
                    INSERT INTO camera_network_events
                    SELECT $position,$eventId,$operationId,$attemptId,$correlationId,$runtimeEpoch,
                        Phase,TargetProviderId,TargetProviderVersion,TargetAdapterPackageId,
                        TargetAdapterVersion,TargetStableDeviceIdentity,TargetContentHash,
                        StationInterfaceId,StationAddress,StationPrefixLength,StationGateway,
                        PreviousAddress,PreviousPrefixLength,PreviousGateway,RequestedAddress,
                        RequestedPrefixLength,RequestedGateway,ObservedAddress,ObservedPrefixLength,
                        ObservedGateway,State,IdentityVerified,ReasonCode,ChangeReason,
                        ActorPrincipalId,SessionId,AuthorizationRevision,RecordedAtUtc,$payload,$hash
                    FROM camera_network_events WHERE Position=1;";
                insert.Parameters.AddWithValue("$position", position);
                insert.Parameters.AddWithValue("$eventId", value.EventId.ToString("D"));
                insert.Parameters.AddWithValue("$operationId", value.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$attemptId", value.AttemptId.ToString("D"));
                insert.Parameters.AddWithValue("$correlationId", value.CorrelationId.ToString("D"));
                insert.Parameters.AddWithValue("$runtimeEpoch", value.RuntimeEpoch.ToString("D"));
                insert.Parameters.AddWithValue("$payload", Convert.ToBase64String(encoded));
                insert.Parameters.AddWithValue("$hash", CameraNetworkStorageCodec.PayloadHash(encoded));
                await insert.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await ExecuteAsync(connection, @"
                CREATE TRIGGER camera_network_event_immutable_update BEFORE UPDATE ON camera_network_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
                END;
                CREATE TRIGGER camera_network_event_immutable_delete BEFORE DELETE ON camera_network_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableCameraNetworkEvent');
                END;");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Camera network capacity tests require Windows machine protection.");
    }
}

#pragma warning restore CA1416
