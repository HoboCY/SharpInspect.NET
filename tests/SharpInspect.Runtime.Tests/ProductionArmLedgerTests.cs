using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Schema-32 production-arm ledger storage acceptance. Every case uses a real
/// SQLite store: the lifecycle rows are written through the actual writer lane
/// (which re-verifies the signed chain and re-reads the durable heads), and the
/// corruption cases tamper with the isolated database only.
/// </summary>
public sealed partial class ProductionArmLedgerTests
{
    [Fact]
    [Trait("VerificationId", "V147_S01")]
    public async Task V147_S01_Schema32InitializesReopensAndStaysEmptyAndVerified()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        Assert.Equal(32L, fixture.Scalar("PRAGMA user_version;"));
        var query = new SqliteProductionArmHistoryQuery(fixture.Options);
        var page = await query.QueryAsync(new ProductionArmHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Events);
        Assert.Equal(0L, page.ThroughPosition);
        Assert.Null(page.NextAfterPosition);
        var current = await query.ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Current);
        Assert.Null(current.Pending);
        Assert.False(current.RecoveryRequired);

        await fixture.ReopenAsync();
        Assert.Equal(32L, fixture.Scalar("PRAGMA user_version;"));
        Assert.True((await query.QueryAsync(new ProductionArmHistoryFilter())).Available);
        await fixture.WaitVerifiedAsync();
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);
    }

    [Fact]
    [Trait("VerificationId", "V147_S02")]
    public async Task V147_S02_MissingOrChangedConfigurationFailsClosedWithoutTouchingTheDatabase()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var attempt = await fixture.AttemptedAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.True(attempt.Committed, attempt.ReasonCode);
        await fixture.Store.DisposeAsync();
        var before = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);

        var misbound = fixture.Rebuild(arming => new ProductionArmStoreOptions
        {
            MaxEvents = arming.MaxEvents,
            MaximumPayloadBytes = arming.MaximumPayloadBytes,
            MaxTotalBytes = arming.MaxTotalBytes + 4096
        });
        await using (var rejected = new SqliteCommandStore(misbound))
        {
            var initialization = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialization.Committed);
            Assert.Equal("ProductionArmConfigurationMismatch", initialization.ReasonCode);
        }

        var missing = fixture.Rebuild(_ => null);
        await using (var rejected = new SqliteCommandStore(missing))
        {
            var initialization = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialization.Committed);
            Assert.Equal("ProductionArmConfigurationRequired", initialization.ReasonCode);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));
    }

    [Fact]
    [Trait("VerificationId", "V147_S03")]
    public async Task V147_S03_AttemptedBindsOneImmutableContextAndTerminalClosesTheAttempt()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var attemptId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(attemptId, epoch)).Committed);
        // A duplicate attempt token can never re-open the same attempt.
        var duplicateToken = await fixture.AttemptedAsync(attemptId, epoch);
        Assert.False(duplicateToken.Committed);
        Assert.Equal("ProductionArmAttemptAlreadyRecorded", duplicateToken.ReasonCode);
        // A restart never retries the same start-up epoch.
        var duplicateEpoch = await fixture.AttemptedAsync(Guid.NewGuid(), epoch);
        Assert.False(duplicateEpoch.Committed);
        Assert.Equal("ProductionArmStartupEpochReused", duplicateEpoch.ReasonCode);
        // The immutable attempt context cannot be re-pointed.
        var repointed = await fixture.WriteAsync(new ProductionArmWriteRequest(attemptId, epoch,
            ProductionArmCause.Startup, ProductionArmEventKind.Failed, fixture.StationId,
            new RecipeContractReference("V147.Startup", "1", ArmFixture.Hash('C')),
            new RecipeContractReference("V147.Post", "1", ArmFixture.Hash('D')),
            ArmFixture.Hash('E'), null, null, null, 0, null, new Dictionary<string, string>(),
            new Dictionary<string, string>(), ProductionArmReason.Interrupted, "V147.Interrupted"));
        Assert.False(repointed.Committed);
        Assert.Equal("ProductionArmAttemptContextChanged", repointed.ReasonCode);
        // ReadyConfirmed is only valid immediately after Authorized.
        var premature = await fixture.WriteAsync(new ProductionArmWriteRequest(attemptId, epoch,
            ProductionArmCause.Startup, ProductionArmEventKind.ReadyConfirmed, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null,
            null, 0, ArmFixture.CanArmReport(epoch, 0), fixture.Heads, fixture.Heads,
            ProductionArmReason.None, "V147.Ready"));
        Assert.False(premature.Committed);
        Assert.Equal("ProductionArmTransitionInvalid", premature.ReasonCode);

        Assert.True((await fixture.FailedAsync(attemptId, epoch, ProductionArmReason.Interrupted)).Committed);
        // A terminal state is closed for a later Authorized.
        var lateAuthorized = await fixture.WriteAsync(new ProductionArmWriteRequest(attemptId, epoch,
            ProductionArmCause.Startup, ProductionArmEventKind.Authorized, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null,
            null, 0, ArmFixture.CanArmReport(epoch, 0), fixture.Heads, fixture.Heads,
            ProductionArmReason.None, "V147.Authorized"));
        Assert.False(lateAuthorized.Committed);
        Assert.Equal("ProductionArmTerminalClosed", lateAuthorized.ReasonCode);
        // Exactly one optional status-delivery observation follows the terminal.
        Assert.True((await fixture.UndeliveredAsync(attemptId, epoch)).Committed);
        var duplicateStatus = await fixture.UndeliveredAsync(attemptId, epoch);
        Assert.False(duplicateStatus.Committed);
        Assert.Equal("ProductionArmStatusWithoutTerminal", duplicateStatus.ReasonCode);

        var page = await fixture.QueryAsync(new ProductionArmHistoryFilter(AttemptId: attemptId));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Equal(3, page.Events.Count);
        Assert.Equal(ProductionArmEventKind.Attempted, page.Events[0].Kind);
        Assert.Equal(ProductionArmEventKind.Failed, page.Events[1].Kind);
        Assert.Equal(ProductionArmEventKind.PlcStatusUndelivered, page.Events[2].Kind);
        Assert.All(page.Events, value => Assert.Equal(SystemPrincipalId.Runtime, value.ActorPrincipalId));
        Assert.All(page.Events, value => Assert.True(value.RuntimeActor));
        Assert.All(page.Events, value => Assert.Null(value.ActorSessionId));
        Assert.All(page.Events, value => Assert.Null(value.HumanPrincipalId));
        var current = await new SqliteProductionArmHistoryQuery(fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(ProductionArmEventKind.PlcStatusUndelivered, current.Current!.Kind);
        Assert.Null(current.Pending);
        Assert.False(current.RecoveryRequired);
    }

    [Fact]
    [Trait("VerificationId", "V147_S04")]
    public async Task V147_S04_AuthorizedRequiresCanArmReportMaintenanceEvidenceAndCurrentHeads()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var attemptId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(attemptId, epoch, ArmFixture.Hash('F'))).Committed);

        var blocked = await fixture.WriteAsync(fixture.AuthorizationRequest(attemptId, epoch,
            report: ArmFixture.CannotArmReport(epoch, 0), expected: fixture.Heads, current: fixture.Heads,
            maintenanceHead: ArmFixture.Hash('F')));
        Assert.False(blocked.Committed);
        Assert.Equal("ProductionArmAdmissionGateBlocked", blocked.ReasonCode);

        var missingMaintenanceAttempt = Guid.NewGuid();
        var missingMaintenanceEpoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(missingMaintenanceAttempt, missingMaintenanceEpoch)).Committed);
        var unavailable = await fixture.WriteAsync(fixture.AuthorizationRequest(missingMaintenanceAttempt, missingMaintenanceEpoch,
            report: ArmFixture.CanArmReport(missingMaintenanceEpoch, 0), expected: fixture.Heads, current: fixture.Heads,
            maintenanceHead: null));
        Assert.False(unavailable.Committed);
        Assert.Equal("ProductionArmMaintenanceEvidenceUnavailable", unavailable.ReasonCode);

        var mismatched = await fixture.WriteAsync(fixture.AuthorizationRequest(attemptId, epoch,
            report: ArmFixture.CanArmReport(epoch, 0), expected: fixture.Heads,
            current: ArmFixture.HeadsWithExtra(fixture.Heads), maintenanceHead: ArmFixture.Hash('F')));
        Assert.False(mismatched.Committed);
        Assert.Equal("ProductionArmDurableHeadsMismatch", mismatched.ReasonCode);

        var stale = await fixture.WriteAsync(fixture.AuthorizationRequest(attemptId, epoch,
            report: ArmFixture.CanArmReport(epoch, 0), expected: ArmFixture.HeadsWithExtra(fixture.Heads),
            current: ArmFixture.HeadsWithExtra(fixture.Heads), maintenanceHead: ArmFixture.Hash('F')));
        Assert.False(stale.Committed);
        Assert.Equal("ProductionArmDurableHeadsChanged", stale.ReasonCode);

        var wrongEpoch = await fixture.WriteAsync(fixture.AuthorizationRequest(attemptId, epoch,
            report: ArmFixture.CanArmReport(Guid.NewGuid(), 0), expected: fixture.Heads, current: fixture.Heads,
            maintenanceHead: ArmFixture.Hash('F')));
        Assert.False(wrongEpoch.Committed);
        Assert.Equal("ProductionArmReportContextMismatch", wrongEpoch.ReasonCode);

        var missingAttempt = await fixture.WriteAsync(fixture.AuthorizationRequest(Guid.NewGuid(), epoch,
            report: ArmFixture.CanArmReport(epoch, 0), expected: fixture.Heads, current: fixture.Heads,
            maintenanceHead: ArmFixture.Hash('F')));
        Assert.False(missingAttempt.Committed);
        Assert.Equal("ProductionArmAttemptMissing", missingAttempt.ReasonCode);

        var report = ArmFixture.CanArmReport(epoch, 0);
        var authorized = await fixture.WriteAsync(fixture.AuthorizationRequest(attemptId, epoch,
            report: report, expected: fixture.Heads, current: fixture.Heads,
            maintenanceHead: ArmFixture.Hash('F')));
        Assert.True(authorized.Committed, authorized.ReasonCode);
        Assert.Equal(ProductionArmEventKind.Authorized, authorized.Event!.Kind);
        Assert.NotNull(authorized.Event.Report);
        Assert.Equal(ArmFixture.Hash('F'), authorized.Event.MaintenanceHeadHash);
        Assert.Equal(fixture.Heads.Count, authorized.Event.CurrentDurableHeads.Count);

        var readyRequest = new ProductionArmWriteRequest(attemptId, epoch,
            ProductionArmCause.Startup, ProductionArmEventKind.ReadyConfirmed, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null,
            ArmFixture.Hash('F'), 0, report, fixture.Heads, fixture.Heads,
            ProductionArmReason.None, "V147.ReadyConfirmed", InputStability: authorized.Event.InputStability);
        var withoutReceipt = await fixture.WriteAsync(readyRequest);
        Assert.False(withoutReceipt.Committed);
        Assert.Equal("ProductionArmPhysicalReadyReceiptRequired", withoutReceipt.ReasonCode);
        var receipt = new ProductionArmReadyReceipt(attemptId, epoch, authorized.Event.ContentHash,
            null, ArmFixture.Hash('F'), 0, 1, 1, DateTimeOffset.UtcNow);
        var ready = await fixture.WriteAsync(readyRequest with { ReadyReceipt = receipt });
        Assert.True(ready.Committed, ready.ReasonCode);
        var changedMaintenance = await fixture.WriteAsync(fixture.AuthorizationRequest(attemptId, epoch,
            report: report, expected: fixture.Heads, current: fixture.Heads,
            maintenanceHead: ArmFixture.Hash('F')));
        Assert.False(changedMaintenance.Committed);
        Assert.Equal("ProductionArmTerminalClosed", changedMaintenance.ReasonCode);
        var delivered = await fixture.DeliveredAsync(attemptId, epoch);
        Assert.True(delivered.Committed, delivered.ReasonCode);
    }

    [Fact]
    [Trait("VerificationId", "V147_S05")]
    public async Task V147_S05_HumanAttributionExistsOnlyForTheManualMaintenanceCause()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var attemptId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var conflicted = await fixture.WriteAsync(new ProductionArmWriteRequest(attemptId, epoch,
            ProductionArmCause.Startup, ProductionArmEventKind.Attempted, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null, null, 0,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            ProductionArmReason.None, "V147.Attempted", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.False(conflicted.Committed);
        Assert.Equal("ProductionArmHumanActorConflict", conflicted.ReasonCode);

        var missingHuman = await fixture.WriteAsync(new ProductionArmWriteRequest(Guid.NewGuid(), epoch,
            ProductionArmCause.ManualMaintenanceArm, ProductionArmEventKind.Attempted, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null, null, 0,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            ProductionArmReason.PolicyManual, "V147.ManualAttempted"));
        Assert.False(missingHuman.Committed);
        Assert.Equal("ProductionArmHumanActorRequired", missingHuman.ReasonCode);

        var manualAttemptId = Guid.NewGuid();
        var manualCommand = Guid.NewGuid();
        var manualPrincipal = Guid.NewGuid();
        var manualSession = Guid.NewGuid();
        Assert.True((await fixture.WriteAsync(new ProductionArmWriteRequest(manualAttemptId, epoch,
            ProductionArmCause.ManualMaintenanceArm, ProductionArmEventKind.Attempted, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null, ArmFixture.Hash('F'), 0,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            ProductionArmReason.PolicyManual, "V147.ManualAttempted", manualCommand, manualPrincipal,
            manualSession))).Committed);
        // The manual arm requires the actual completed schema-22 human command;
        // no fabricated session can authorize it.
        var manualAuthorized = await fixture.WriteAsync(new ProductionArmWriteRequest(manualAttemptId, epoch,
            ProductionArmCause.ManualMaintenanceArm, ProductionArmEventKind.Authorized, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, null, null,
            ArmFixture.Hash('F'), 0, ArmFixture.CanArmReport(epoch, 0), fixture.Heads, fixture.Heads,
            ProductionArmReason.PolicyManual, "V147.ManualAuthorized", manualCommand, manualPrincipal,
            manualSession));
        Assert.False(manualAuthorized.Committed);
        Assert.Equal("ProductionArmHumanAuthorizationMismatch", manualAuthorized.ReasonCode);

        var page = await fixture.QueryAsync(new ProductionArmHistoryFilter(AttemptId: manualAttemptId));
        Assert.True(page.Available, page.ReasonCode);
        var attempted = Assert.Single(page.Events);
        Assert.False(attempted.RuntimeActor);
        Assert.Equal(manualPrincipal.ToString("D"), attempted.ActorPrincipalId);
        Assert.Equal(manualSession, attempted.ActorSessionId!.Value);
        Assert.Equal(manualPrincipal, attempted.HumanPrincipalId!.Value);
        Assert.Equal(manualSession, attempted.HumanSessionId!.Value);
    }

    [Fact]
    [Trait("VerificationId", "V147_S06")]
    public async Task V147_S06_PlcCauseCannotRecordAnAttemptFromUnbackedRequestClaims()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var request = ArmFixture.PlcEvidence(Guid.NewGuid(), 11, 3);
        var attemptId = Guid.NewGuid();
        var missingActivation = await fixture.WriteAsync(new ProductionArmWriteRequest(attemptId, request.RuntimeEpoch,
            ProductionArmCause.PlcActivation, ProductionArmEventKind.Attempted, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, request, null, null, 0,
            null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            ProductionArmReason.None, "V147.PlcAttempted"));
        Assert.False(missingActivation.Committed);
        Assert.Equal("ProductionArmPlcActivationRequired", missingActivation.ReasonCode);
        // Without the actual signed schema-31 handshake and succeeded activation
        // record, no PLC arm can be authorized.
        var attempted = await fixture.WriteAsync(new ProductionArmWriteRequest(attemptId, request.RuntimeEpoch,
            ProductionArmCause.PlcActivation, ProductionArmEventKind.Attempted, fixture.StationId,
            fixture.StartupPolicy, fixture.PostActivationPolicy, fixture.DeploymentHash, request,
            new RecipeActivationReference(1, Guid.NewGuid(), ArmFixture.Hash('A')),
            ArmFixture.Hash('F'), 0, ArmFixture.CanArmReport(request.RuntimeEpoch, 0), fixture.Heads,
            fixture.Heads, ProductionArmReason.None, "V147.PlcAuthorized"));
        Assert.False(attempted.Committed);
        Assert.Equal("ProductionArmPlcHandshakeUnavailable", attempted.ReasonCode);
        await fixture.WaitVerifiedAsync();
        var page = await fixture.QueryAsync(new ProductionArmHistoryFilter());
        Assert.True(page.Available, page.ReasonCode);
        Assert.Empty(page.Events);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("index")]
    [InlineData("delete")]
    [Trait("VerificationId", "V147_S07")]
    public async Task V147_S07_OfflineCorruptionIsRejectedByColdReadersAndByReopen(string corruption)
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var attemptId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(attemptId, epoch)).Committed);
        Assert.True((await fixture.FailedAsync(attemptId, epoch, ProductionArmReason.Interrupted)).Committed);
        await fixture.Store.DisposeAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            var trigger = corruption == "delete"
                ? "production_arm_event_immutable_delete" : "production_arm_event_immutable_update";
            await using var lookup = connection.CreateCommand();
            lookup.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name;";
            lookup.Parameters.AddWithValue("$name", trigger);
            var original = Assert.IsType<string>(await lookup.ExecuteScalarAsync());
            await using var mutate = connection.CreateCommand();
            var change = corruption switch
            {
                "payload" => "UPDATE production_arm_events SET Payload='AAAA' WHERE Position=1;",
                "index" => "UPDATE production_arm_events SET RequestIdentityHash='" + new string('C', 64) +
                    "' WHERE Position=1;",
                _ => "DELETE FROM production_arm_events WHERE Position=2;"
            };
            mutate.CommandText = "DROP TRIGGER " + trigger + ";" + change + original;
            await mutate.ExecuteNonQueryAsync();
        }

        var beforeRead = await File.ReadAllBytesAsync(fixture.Options.DatabasePath);
        var query = new SqliteProductionArmHistoryQuery(fixture.Options);
        var page = await query.QueryAsync(new ProductionArmHistoryFilter());
        Assert.False(page.Available);
        Assert.Empty(page.Events);
        Assert.Contains("ProductionArm", page.ReasonCode, StringComparison.Ordinal);
        Assert.False((await query.ReadCurrentAsync()).Available);
        var audit = await new SqliteAuditIntegrityQuery(fixture.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Contains("ProductionArm", audit.ReasonCode, StringComparison.Ordinal);
        Assert.Equal(beforeRead, await File.ReadAllBytesAsync(fixture.Options.DatabasePath));

        await using var reopened = new SqliteCommandStore(fixture.Options);
        Assert.False((await reopened.Initialization.WaitAsync(TimeSpan.FromSeconds(15))).Committed);
    }

    [Fact]
    [Trait("VerificationId", "V147_S08")]
    public async Task V147_S08_OpenAttemptsReserveTheirTerminalAndStatusCapacity()
    {
        await using var fixture = await ArmFixture.CreateAsync(new ProductionArmStoreOptions { MaxEvents = 4 });
        var firstAttempt = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(firstAttempt, Guid.NewGuid())).Committed);
        // The open attempt already reserves Authorized, ReadyConfirmed and one
        // status observation, so a second attempt cannot consume that budget.
        var second = await fixture.AttemptedAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.False(second.Committed);
        Assert.Equal("ProductionArmEntryCapacityExceeded", second.ReasonCode);
        // The reserved slots remain usable by the pending attempt itself.
        Assert.True((await fixture.FailedAsync(firstAttempt, fixture.Epoch(firstAttempt),
            ProductionArmReason.Interrupted)).Committed);
        Assert.True((await fixture.UndeliveredAsync(firstAttempt, fixture.Epoch(firstAttempt))).Committed);
        var current = await new SqliteProductionArmHistoryQuery(fixture.Options).ReadCurrentAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Null(current.Pending);
    }

    [Fact]
    [Trait("VerificationId", "V147_S09")]
    public async Task V147_S09_QueryPagesByAttemptAndPositionAndReopensVerified()
    {
        await using var fixture = await ArmFixture.CreateAsync();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        Assert.True((await fixture.AttemptedAsync(first, Guid.NewGuid())).Committed);
        Assert.True((await fixture.FailedAsync(first, fixture.Epoch(first),
            ProductionArmReason.Cancelled)).Committed);
        Assert.True((await fixture.AttemptedAsync(second, Guid.NewGuid())).Committed);

        var query = new SqliteProductionArmHistoryQuery(fixture.Options);
        var all = await query.QueryAsync(new ProductionArmHistoryFilter(PageSize: 2));
        Assert.True(all.Available, all.ReasonCode);
        Assert.Equal(3L, all.ThroughPosition);
        Assert.Equal(2, all.Events.Count);
        Assert.Equal(2L, all.NextAfterPosition!.Value);
        var next = await query.QueryAsync(new ProductionArmHistoryFilter(AfterPosition: all.NextAfterPosition!.Value,
            PageSize: 2));
        Assert.True(next.Available, next.ReasonCode);
        Assert.Single(next.Events);
        Assert.Equal(second, next.Events[0].AttemptId);
        var through = await query.QueryAsync(new ProductionArmHistoryFilter(ThroughPosition: 1));
        Assert.True(through.Available, through.ReasonCode);
        Assert.Single(through.Events);
        Assert.Equal(first, through.Events[0].AttemptId);
        var filtered = await query.QueryAsync(new ProductionArmHistoryFilter(AttemptId: second));
        Assert.True(filtered.Available, filtered.ReasonCode);
        Assert.Single(filtered.Events);

        await fixture.ReopenAsync();
        var reopened = await query.QueryAsync(new ProductionArmHistoryFilter());
        Assert.True(reopened.Available, reopened.ReasonCode);
        Assert.Equal(3, reopened.Events.Count);
    }

    private sealed class ArmFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private ArmFixture(string directory, ProductionStoreOptions options, SqliteCommandStore store)
        {
            _directory = directory;
            Options = options;
            Store = store;
            StartupPolicy = new RecipeContractReference("V147.Startup", "1", Hash('A'));
            PostActivationPolicy = new RecipeContractReference("V147.PostActivation", "1", Hash('B'));
            DeploymentHash = Hash('E');
            StationId = "V147ArmLedgerStation";
            Heads = new Dictionary<string, string>();
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; private set; }
        internal RecipeContractReference StartupPolicy { get; }
        internal RecipeContractReference PostActivationPolicy { get; }
        internal string DeploymentHash { get; }
        internal string StationId { get; }
        internal Dictionary<string, string> Heads { get; private set; }

        internal static async Task<ArmFixture> CreateAsync(ProductionArmStoreOptions? arming = null,
            ArmQueryAnchor? anchor = null)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Production arm storage requires Windows machine key protection.");
            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V147-ArmLedger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V147ArmLedgerStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V147.Arm." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                MaximumVerificationEntries = 10_000,
                VerificationInterval = TimeSpan.FromSeconds(1),
                RequireExternalAnchor = anchor is not null,
                ExternalAnchorRouteId = anchor is null ? null : ArmQueryAnchor.RouteId
            };
            var identity = new LocalIdentityOptions(station, new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v147-arm-blocklist", "v1",
                    new[] { "known-compromised-value" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, RecipeDraftTestPolicies.Authoring);
            var options = new ProductionStoreOptions(Path.Combine(directory, "arm.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                ExternalAuditAnchor = anchor,
                LocalIdentity = identity,
                ProductionAdmission = new ProductionAdmissionStoreOptions(),
                ProductionArming = arming ?? new ProductionArmStoreOptions(),
                CommitTimeout = TimeSpan.FromSeconds(5),
                QueryTimeout = TimeSpan.FromSeconds(5),
                QueueCapacity = 16
            };
            var store = new SqliteCommandStore(options);
            var fixture = new ArmFixture(directory, options, store);
            var initialization = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(initialization.Committed, initialization.ReasonCode);
            await fixture.WaitVerifiedAsync();
            fixture.Heads = new Dictionary<string, string>(
                await store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None));
            return fixture;
        }

        internal ProductionStoreOptions Rebuild(Func<ProductionArmStoreOptions, ProductionArmStoreOptions?> arming) =>
            new(Options.DatabasePath)
            {
                AuditIntegrityPolicy = Options.AuditIntegrityPolicy,
                ExternalAuditAnchor = Options.ExternalAuditAnchor,
                LocalIdentity = Options.LocalIdentity,
                ProductionAdmission = Options.ProductionAdmission,
                ProductionArming = arming(Options.ProductionArming!),
                CommitTimeout = Options.CommitTimeout,
                QueryTimeout = Options.QueryTimeout,
                QueueCapacity = Options.QueueCapacity
            };

        internal long Scalar(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Options.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }

        internal async Task ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = new SqliteCommandStore(Options);
            var initialization = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(initialization.Committed, initialization.ReasonCode);
            await WaitVerifiedAsync();
        }

        internal async Task WaitVerifiedAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (Store.Integrity?.State == AuditIntegrityState.Verified) return;
                await Task.Delay(25);
            }
            throw new XunitException("Store integrity did not reach Verified: " + Store.Integrity?.ReasonCode);
        }

        internal Guid Epoch(Guid attemptId)
        {
            var page = QueryAsync(new ProductionArmHistoryFilter(AttemptId: attemptId)).GetAwaiter().GetResult();
            Assert.True(page.Available, page.ReasonCode);
            return page.Events[0].RuntimeEpoch;
        }

        internal ValueTask<ProductionArmHistoryPage> QueryAsync(ProductionArmHistoryFilter filter) =>
            new SqliteProductionArmHistoryQuery(Options).QueryAsync(filter);

        internal async ValueTask<ProductionArmWriteResult> AttemptedAsync(Guid attemptId, Guid epoch, string? maintenanceHead = null)
        {
            await WaitVerifiedAsync();
            return await Store.AppendProductionArmEventAsync(new ProductionArmWriteRequest(attemptId, epoch,
                ProductionArmCause.Startup, ProductionArmEventKind.Attempted, StationId, StartupPolicy,
                PostActivationPolicy, DeploymentHash, null, null, maintenanceHead, 0, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                ProductionArmReason.None, "V147.Attempted"), new StoreDeadline(TimeSpan.FromSeconds(5)),
                CancellationToken.None);
        }

        internal async ValueTask<ProductionArmWriteResult> FailedAsync(Guid attemptId, Guid epoch,
            ProductionArmReason reason)
        {
            await WaitVerifiedAsync();
            return await Store.AppendProductionArmEventAsync(new ProductionArmWriteRequest(attemptId, epoch,
                ProductionArmCause.Startup, ProductionArmEventKind.Failed, StationId, StartupPolicy,
                PostActivationPolicy, DeploymentHash, null, null, null, 0, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(), reason,
                "V147." + reason), new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
        }

        internal async ValueTask<ProductionArmWriteResult> DeliveredAsync(Guid attemptId, Guid epoch)
        {
            await WaitVerifiedAsync();
            var page = await QueryAsync(new ProductionArmHistoryFilter(AttemptId: attemptId));
            Assert.True(page.Available, page.ReasonCode);
            var terminal = page.Events.Last();
            return await Store.AppendProductionArmEventAsync(new ProductionArmWriteRequest(attemptId, epoch,
                ProductionArmCause.Startup, ProductionArmEventKind.PlcStatusDelivered, StationId, StartupPolicy,
                PostActivationPolicy, DeploymentHash, null, terminal.Activation, terminal.MaintenanceHeadHash, terminal.AdmissionGeneration, terminal.Report,
                terminal.ExpectedDurableHeads, terminal.CurrentDurableHeads,
                ProductionArmReason.None, "V147.PlcStatusDelivered", ReadyReceipt: terminal.ReadyReceipt,
                InputStability: terminal.InputStability), new StoreDeadline(TimeSpan.FromSeconds(5)),
                CancellationToken.None);
        }

        internal async ValueTask<ProductionArmWriteResult> UndeliveredAsync(Guid attemptId, Guid epoch)
        {
            await WaitVerifiedAsync();
            return await Store.AppendProductionArmEventAsync(new ProductionArmWriteRequest(attemptId, epoch,
                ProductionArmCause.Startup, ProductionArmEventKind.PlcStatusUndelivered, StationId, StartupPolicy,
                PostActivationPolicy, DeploymentHash, null, null, null, 0, null,
                new Dictionary<string, string>(), new Dictionary<string, string>(),
                ProductionArmReason.ReadyWriteUncertain, "V147.PlcStatusUndelivered"),
                new StoreDeadline(TimeSpan.FromSeconds(5)), CancellationToken.None);
        }

        internal async ValueTask<ProductionArmWriteResult> WriteAsync(ProductionArmWriteRequest request)
        {
            await WaitVerifiedAsync();
            return await Store.AppendProductionArmEventAsync(request, new StoreDeadline(TimeSpan.FromSeconds(5)),
                CancellationToken.None);
        }

        internal ProductionArmWriteRequest AuthorizationRequest(Guid attemptId, Guid epoch,
            ProductionAdmissionReport report, IReadOnlyDictionary<string, string> expected,
            IReadOnlyDictionary<string, string> current, string? maintenanceHead) =>
            new(attemptId, epoch, ProductionArmCause.Startup, ProductionArmEventKind.Authorized, StationId,
                StartupPolicy, PostActivationPolicy, DeploymentHash, null, null, maintenanceHead, 0, report,
                expected, current, ProductionArmReason.None, "V147.Authorized", InputStability: StabilityEvidence());

        private static ProductionArmInputStabilityEvidence StabilityEvidence()
        {
            var policy = new PlcCommunicationPolicy("V147.LedgerInputs", "1",
                TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(40), 2, TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(1));
            var window = policy.SynchronizationStabilityWindow.Ticks;
            return new ProductionArmInputStabilityEvidence(policy, TimeSpan.TicksPerSecond,
                0, 1, 2, 2, window, window, 0, 0, 0);
        }

        internal static ProductionAdmissionReport CanArmReport(Guid runtimeEpoch, long generation) =>
            Report(runtimeEpoch, generation, ProductionAdmissionGateStatus.Passed);

        internal static ProductionAdmissionReport CannotArmReport(Guid runtimeEpoch, long generation) =>
            Report(runtimeEpoch, generation, ProductionAdmissionGateStatus.Blocked);

        internal static Dictionary<string, string> HeadsWithExtra(IReadOnlyDictionary<string, string> heads)
        {
            var copy = new Dictionary<string, string>(heads) { ["v147-material"] = Hash('7') };
            return copy;
        }

        private static ProductionAdmissionReport Report(Guid runtimeEpoch, long generation,
            ProductionAdmissionGateStatus status)
        {
            var gates = ProductionAdmissionReport.RequiredGates.Select(gate =>
                new ProductionAdmissionGateResult(gate, status, "V147.Gate")).ToArray();
            return new ProductionAdmissionReport(runtimeEpoch, 1, generation, DateTimeOffset.UtcNow,
                null, null, null, null, null, gates);
        }

        internal static RecipeChangeRequestEvidence PlcEvidence(Guid runtimeEpoch, uint sequence, uint code) =>
            new(runtimeEpoch, Hash('1'), new RecipeContractReference("V147.Protocol", "1", Hash('2')),
                1, sequence, code, null, RecipeSelectionPolicy.Default.Reference, null, null,
                DateTimeOffset.UtcNow);

        internal static string Hash(char value) => new(value, 64);

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
