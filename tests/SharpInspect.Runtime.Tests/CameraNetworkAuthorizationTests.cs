#pragma warning disable CA1416

using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V120 authorization acceptance against the real SQLite writer, interactive
/// session authority and LocalAuthorizationService. Provider calls are only
/// used by the anonymous rejection case to prove that authorization fails
/// before physical ownership is entered.
/// </summary>
public sealed class CameraNetworkAuthorizationTests
{
    [Fact]
    public async Task V120_A01_ExactStepUpBindingPersistsOperationTargetKindAndPermission()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueGrantAsync(signedIn.Invocation, operationId,
            fixture.Target);
        var invocation = signedIn.Invocation with { StepUpGrantId = grantId };

        Assert.Equal(13, (int)Permission.ManageCameraBindings);
        Assert.Equal(18, (int)AuditedCommandKind.ChangeCameraNetworkConfiguration);

        var authorization = await fixture.Authorization.AuthorizeCameraSetupAsync(
            invocation, readOnly: false, operationId, fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.True(authorization.Authorized, authorization.ReasonCode);
        Assert.Equal(signedIn.PrincipalId, authorization.PrincipalId);
        Assert.Equal(signedIn.Invocation.SessionId, authorization.SessionId);
        Assert.True(authorization.AuthorizationRevision > 0);
        Assert.NotNull(authorization.Reservation);

        var request = fixture.Request(operationId, fixture.Target, invocation);
        var appended = await fixture.Persistence.AppendAdmissionAsync(request,
            fixture.StationNetwork, fixture.Previous, authorization, Guid.NewGuid(),
            new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(appended.Result.Committed, appended.Result.ReasonCode);
        Assert.NotNull(appended.Admission);
        authorization.Reservation!.Commit();
        await fixture.WaitForVerifiedAsync();

        var fact = Assert.Single((await new SqliteCommandTraceQuery(fixture.Options)
            .QueryAsync(new CommandTraceFilter(CorrelationId: operationId))).Records);
        Assert.Equal(AuditedCommandKind.ChangeCameraNetworkConfiguration, fact.CommandKind);
        Assert.Equal(CommandAuditPhase.Outcome, fact.Phase);
        Assert.Equal(CommandDisposition.Accepted, fact.Disposition);
        Assert.Equal(invocation.PrincipalId, fact.ClaimedPrincipalId);
        Assert.Equal(invocation.SessionId, fact.ClaimedSessionId);
        Assert.Equal(grantId, fact.ClaimedStepUpGrantId);
        Assert.Equal(invocation.PrincipalId, fact.AuthenticatedHumanPrincipalId);

        var identity = await ReadNetworkAuthorizationAsync(fixture.Options.DatabasePath,
            fixture.StationId, operationId);
        Assert.NotNull(identity);
        Assert.Equal(Permission.ManageCameraBindings.ToString(), identity!.RequiredPermission);
        Assert.Equal(fixture.Target.ContentHash, identity.ActionTargetId);
        Assert.Equal(operationId, identity.CommandCorrelationId);
        Assert.Equal(operationId, identity.BoundCommandCorrelationId);
        Assert.Equal(AuditedCommandKind.ChangeCameraNetworkConfiguration, identity.CommandKind);
        Assert.Equal(operationId, identity.OperationId);
        Assert.Equal(grantId, identity.StepUpGrantId);
        Assert.Equal(signedIn.PrincipalId, identity.PrincipalId);
        Assert.Equal(signedIn.PrincipalId, identity.ActorPrincipalId);
        Assert.Equal(signedIn.Invocation.SessionId!.Value, identity.SessionId);
        Assert.Equal(authorization.AuthorizationRevision, identity.AuthorizationRevision);

        var network = await ReadNetworkEventAsync(fixture.Options.DatabasePath, operationId);
        Assert.NotNull(network);
        Assert.Equal(0L, network!.Phase);
        Assert.Equal(fixture.Target.ContentHash, network.TargetContentHash);
        Assert.Equal(signedIn.PrincipalId.ToString("D"), network.ActorPrincipalId);
        Assert.Equal(signedIn.Invocation.SessionId!.Value.ToString("D"), network.SessionId);
        Assert.Equal(authorization.AuthorizationRevision, network.AuthorizationRevision);
    }

    [Fact]
    public async Task V120_A02_MismatchedBindingDoesNotConsumeGrantAndReuseIsRejected()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueGrantAsync(signedIn.Invocation, operationId,
            fixture.Target);
        var invocation = signedIn.Invocation with { StepUpGrantId = grantId };

        var wrongTarget = await fixture.Authorization.AuthorizeCameraSetupAsync(invocation,
            readOnly: false, operationId, fixture.OtherTarget.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.False(wrongTarget.Authorized);
        Assert.Equal("StepUpInvalid", wrongTarget.ReasonCode);
        Assert.Null(wrongTarget.Reservation);

        var wrongOperation = await fixture.Authorization.AuthorizeCameraSetupAsync(invocation,
            readOnly: false, Guid.NewGuid(), fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.False(wrongOperation.Authorized);
        Assert.Equal("StepUpInvalid", wrongOperation.ReasonCode);
        Assert.Null(wrongOperation.Reservation);

        var wrongKind = await fixture.Authorization.AuthorizeCameraSetupAsync(invocation,
            readOnly: false, operationId, fixture.Target.ContentHash,
            AuditedCommandKind.RebindCamera);
        Assert.False(wrongKind.Authorized);
        Assert.Equal("StepUpInvalid", wrongKind.ReasonCode);
        Assert.Null(wrongKind.Reservation);

        // The failed binding checks leave the grant active. The exact request
        // can be admitted once, after which the same grant is one-way consumed.
        var exact = await fixture.Authorization.AuthorizeCameraSetupAsync(invocation,
            readOnly: false, operationId, fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.True(exact.Authorized, exact.ReasonCode);
        Assert.NotNull(exact.Reservation);
        var request = fixture.Request(operationId, fixture.Target, invocation);
        var appended = await fixture.Persistence.AppendAdmissionAsync(request,
            fixture.StationNetwork, fixture.Previous, exact, Guid.NewGuid(),
            new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(appended.Result.Committed, appended.Result.ReasonCode);
        exact.Reservation!.Commit();
        await fixture.WaitForVerifiedAsync();

        var reused = await fixture.Authorization.AuthorizeCameraSetupAsync(invocation,
            readOnly: false, operationId, fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.False(reused.Authorized);
        Assert.Equal("StepUpInvalid", reused.ReasonCode);
        Assert.Null(reused.Reservation);
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE OperationId=$operation;",
            ("$operation", operationId.ToString("D"))));
    }

    [Fact]
    public async Task V120_A03_LoggedOutSessionRejectsPreviouslyIssuedGrant()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueGrantAsync(signedIn.Invocation, operationId,
            fixture.Target);

        var logout = await fixture.Sessions.LogoutAsync(signedIn.Invocation.SessionId!.Value);
        Assert.True(logout.Succeeded, logout.ReasonCode);
        await fixture.WaitForVerifiedAsync();

        var stale = await fixture.Authorization.AuthorizeCameraSetupAsync(
            signedIn.Invocation with { StepUpGrantId = grantId }, readOnly: false,
            operationId, fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.False(stale.Authorized);
        Assert.Equal("SessionMismatch", stale.ReasonCode);
        Assert.Null(stale.Reservation);
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events;"));
    }

    [Fact]
    public async Task V120_A04_AuditWriterFailureLeavesGrantReusable()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var signedIn = await fixture.SignInAsync();
        var operationId = Guid.NewGuid();
        var grantId = await fixture.IssueGrantAsync(signedIn.Invocation, operationId,
            fixture.Target);
        var invocation = signedIn.Invocation with { StepUpGrantId = grantId };
        var authorization = await fixture.Authorization.AuthorizeCameraSetupAsync(
            invocation, readOnly: false, operationId, fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.True(authorization.Authorized, authorization.ReasonCode);
        Assert.NotNull(authorization.Reservation);
        var request = fixture.Request(operationId, fixture.Target, invocation);

        await using (var lockConnection = OpenReadWriteConnection(fixture.Options.DatabasePath))
        {
            await lockConnection.OpenAsync();
            await ExecuteAsync(lockConnection, "BEGIN IMMEDIATE;");
            var failed = await fixture.Persistence.AppendAdmissionAsync(request,
                fixture.StationNetwork, fixture.Previous, authorization, Guid.NewGuid(),
                new StoreDeadline(TimeSpan.FromMilliseconds(500)));
            Assert.False(failed.Result.Committed);
            Assert.Contains(failed.Result.ReasonCode, new[]
                { "TraceCommitDeadlineExceeded", "TraceStoreBusy",
                    "CameraNetworkCommitDeadlineExceeded", "AuditVerificationDeadlineExceeded" });
            await ExecuteAsync(lockConnection, "ROLLBACK;");
        }

        // The caller-owned reservation is released after a failed writer call;
        // a fresh authorization reservation can use the still-active grant.
        authorization.Reservation!.Dispose();
        Assert.Equal(0L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE OperationId=$operation;",
            ("$operation", operationId.ToString("D"))));
        var retryAuthorization = await fixture.Authorization.AuthorizeCameraSetupAsync(
            invocation, readOnly: false, operationId, fixture.Target.ContentHash,
            AuditedCommandKind.ChangeCameraNetworkConfiguration);
        Assert.True(retryAuthorization.Authorized, retryAuthorization.ReasonCode);
        Assert.NotNull(retryAuthorization.Reservation);
        var retry = await fixture.Persistence.AppendAdmissionAsync(request,
            fixture.StationNetwork, fixture.Previous, retryAuthorization, Guid.NewGuid(),
            new StoreDeadline(fixture.Options.CommitTimeout));
        Assert.True(retry.Result.Committed, retry.Result.ReasonCode);
        retryAuthorization.Reservation!.Commit();
        await fixture.WaitForVerifiedAsync();
        Assert.Equal(1L, await fixture.ScalarAsync(
            "SELECT COUNT(*) FROM camera_network_events WHERE OperationId=$operation;",
            ("$operation", operationId.ToString("D"))));
    }

    [Fact]
    public async Task V120_A05_FabricatedGuidInvocationIsUnauthenticatedAndUnattributedInAudit()
    {
        await using var fixture = await CameraNetworkTestFixture.CreateAsync();
        var provider = new NoOpNetworkProvider(fixture.Target.Provider);
        var options = new CameraSetupOptions
        {
            OperationTimeout = TimeSpan.FromSeconds(2),
            ShutdownTimeout = TimeSpan.FromSeconds(2),
            StationNetwork = fixture.StationNetwork
        };
        var operationId = Guid.NewGuid();
        var claimedPrincipal = Guid.NewGuid();
        var claimedSession = Guid.NewGuid();
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            claimedPrincipal.ToString("D"), claimedSession);
        var request = fixture.Request(operationId, fixture.Target, invocation);

        await using (var runtime = new StationRuntime(fixture.Store,
            heartbeatInterval: TimeSpan.FromMilliseconds(20), sessions: fixture.Sessions,
            authorization: fixture.Authorization, cameraProviders: new[] { provider },
            cameraSetupOptions: options))
        {
            var result = await runtime.ChangeNetworkConfigurationAsync(request);
            Assert.False(result.Succeeded);
            Assert.Equal("AuthenticationRequired", result.ReasonCode);
            Assert.Equal(AuditPersistence.Persisted, result.Audit);
            Assert.NotNull(result.Snapshot);
            Assert.Equal(CameraNetworkMaintenanceState.Failed, result.Snapshot!.State);
            Assert.False(result.Snapshot.IdentityVerified);
            var station = await runtime.GetSnapshotAsync();
            Assert.False(station.Ready);
            Assert.Equal(ProductionArmState.Disarmed, station.ArmState);
        }

        await fixture.WaitForVerifiedAsync();
        Assert.Equal(0, provider.BeginCalls);
        Assert.Equal(0, provider.OpenCalls);
        Assert.Equal(0, provider.DiscoverCalls);

        var network = await ReadNetworkEventAsync(fixture.Options.DatabasePath, operationId);
        Assert.NotNull(network);
        Assert.Equal(2L, network!.Phase);
        Assert.Equal(fixture.Target.ContentHash, network.TargetContentHash);
        Assert.Null(network.ActorPrincipalId);
        Assert.Null(network.SessionId);
        Assert.Equal(0L, network.AuthorizationRevision);

        var fact = Assert.Single((await new SqliteCommandTraceQuery(fixture.Options)
            .QueryAsync(new CommandTraceFilter(CorrelationId: operationId))).Records);
        Assert.Equal(AuditedCommandKind.ChangeCameraNetworkConfiguration, fact.CommandKind);
        Assert.Equal(CommandAuditPhase.Outcome, fact.Phase);
        Assert.Equal(CommandDisposition.Rejected, fact.Disposition);
        Assert.Equal(CommandSource.PhysicalConsole, fact.Source);
        Assert.Null(fact.AuthenticatedHumanPrincipalId);
        Assert.Equal(claimedPrincipal.ToString("D"), fact.ClaimedPrincipalId);
        Assert.Equal(claimedSession, fact.ClaimedSessionId);
        Assert.Null(fact.ClaimedStepUpGrantId);
        Assert.Equal("AuthenticationRequired", fact.ReasonCode);
        Assert.Equal(network.ReasonCode, fact.ReasonCode);
    }

    private static SqliteConnection OpenReadWriteConnection(string databasePath) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<NetworkEvidenceRow?> ReadNetworkEventAsync(string databasePath,
        Guid operationId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT OperationId,TargetContentHash,Phase,ActorPrincipalId,SessionId,
                   AuthorizationRevision,ReasonCode
            FROM camera_network_events
            WHERE OperationId=$operation
            ORDER BY Position DESC LIMIT 1;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new NetworkEvidenceRow(reader.GetString(0), reader.GetString(1),
            reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
            reader.GetString(6));
    }

    private static async Task<CameraAuthorizationAudit?> ReadNetworkAuthorizationAsync(
        string databasePath, string stationId, Guid operationId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT IdentityPosition,Payload
            FROM audit_entries
            WHERE Kind='IdentityEvent' AND IdentityPosition IS NOT NULL
            ORDER BY IdentityPosition;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var ordinal = reader.GetInt64(0);
            var payload = Convert.FromBase64String(reader.GetString(1));
            if (IdentityAuditEvent.TryReadCameraNetworkAuthorization(payload, ordinal,
                    stationId, out var binding) && binding.OperationId == operationId)
                return binding;
        }
        return null;
    }

    private sealed record NetworkEvidenceRow(string OperationId, string TargetContentHash,
        long Phase, string? ActorPrincipalId, string? SessionId, long AuthorizationRevision,
        string ReasonCode);

    private sealed class NoOpNetworkProvider : ICameraProvider, ICameraNetworkConfigurator
    {
        private int _beginCalls;
        private int _discoverCalls;
        private int _openCalls;

        internal NoOpNetworkProvider(CameraProviderIdentity identity) => Identity = identity;

        public CameraProviderIdentity Identity { get; }
        internal int BeginCalls => Volatile.Read(ref _beginCalls);
        internal int DiscoverCalls => Volatile.Read(ref _discoverCalls);
        internal int OpenCalls => Volatile.Read(ref _openCalls);

        public ValueTask<CameraNetworkMaintenanceLeaseResult> TryBeginMaintenanceAsync(
            string stableDeviceIdentity, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _beginCalls);
            return ValueTask.FromResult(CameraNetworkMaintenanceLeaseResult.Failure(
                "UnexpectedProviderBegin"));
        }

        public ValueTask<CameraDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _discoverCalls);
            return ValueTask.FromResult(CameraDiscoveryResult.Success(
                Array.Empty<CameraDeviceDescriptor>()));
        }

        public ValueTask<CameraOpenResult> OpenAsync(string stableDeviceIdentity,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCalls);
            return ValueTask.FromResult(CameraOpenResult.Failure("UnexpectedProviderOpen"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

#pragma warning restore CA1416
