using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class TraceStoragePolicyRuntimeTests
{
    [Fact]
    public async Task V139_R01_ApprovedPublicationFreezesRulesAndSurvivesColdRead()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var missing = await service.GetPreflightAsync();
        Assert.False(missing.PolicyValid);
        Assert.False(missing.CanAdmitProduction);
        var command = await AuthorizedCommand(fixture, 0, Policy());
        var published = await service.PublishAsync(command);
        Assert.True(published.Succeeded, published.Outcome.ReasonCode);
        Assert.Equal(1, published.Publication!.Version);
        Assert.Equal(command.Policy.ContentHash, published.Publication.Policy.ContentHash);
        Assert.Equal(Guid.Parse(fixture.Invocation().PrincipalId!), published.Publication.PrincipalId);
        Assert.Equal(command.Invocation.StepUpGrantId, published.Publication.StepUpGrantId);
        Assert.Equal(command.Invocation.SessionId, published.Publication.SessionId);
        Assert.Equal(command.Policy.ApprovalReference, published.Publication.Policy.ApprovalReference);
        Assert.Equal(command.Policy.ApprovalVersion, published.Publication.Policy.ApprovalVersion);
        Assert.Equal(command.Policy.RetentionRules.Select(rule => rule.ContentHash),
            published.Snapshot!.RetentionRules.Select(rule => rule.ContentHash));
        var before = await new SqliteTraceStoragePolicyQuery(fixture.Options).ReadAsync(1);
        Assert.True(before.Available, before.ReasonCode);
        await fixture.RestartStoreAsync();
        var cold = await new SqliteTraceStoragePolicyQuery(fixture.Options).ReadAsync(1);
        Assert.True(cold.Available, cold.ReasonCode);
        Assert.Equal(published.Publication.ContentHash, cold.Publication!.ContentHash);
        Assert.Equal(published.Snapshot.ContentHash, cold.Snapshot!.ContentHash);
        var restartedPreflight = await Service(fixture).GetPreflightAsync();
        Assert.True(restartedPreflight.PolicyValid);
        Assert.Equal(published.Snapshot.ContentHash, restartedPreflight.PolicySnapshotHash);
        Assert.False(restartedPreflight.CanAdmitProduction);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));
    }

    [Fact]
    public async Task V139_R02_NewerShorterPolicyDoesNotRewriteOldSnapshot()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var first = await service.PublishAsync(await AuthorizedCommand(fixture, 0, Policy("1", 30)));
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var original = first.Snapshot!.ContentHash;
        var second = await service.PublishAsync(await AuthorizedCommand(fixture, 1, Policy("2", 1)));
        Assert.True(second.Succeeded, second.Outcome.ReasonCode);
        Assert.Equal(first.Publication!.ContentHash, second.Publication!.PreviousContentHash);
        var old = await service.ReadAsync(1);
        Assert.Equal(original, old.Snapshot!.ContentHash);
        Assert.All(old.Snapshot.RetentionRules, rule => Assert.Equal(TimeSpan.FromDays(30), rule.MinimumRetention));
        Assert.All(second.Snapshot!.RetentionRules, rule => Assert.Equal(TimeSpan.FromDays(1), rule.MinimumRetention));
        var page = await service.QueryAsync(new(PageSize: 1));
        Assert.True(page.Available, page.ReasonCode);
        Assert.Single(page.Records);
        Assert.Equal(2, page.ThroughVersion);
        var next = await service.QueryAsync(new(page.NextAfterVersion!.Value, page.ThroughVersion, 1));
        Assert.Equal(2, Assert.Single(next.Records).Version);
    }

    [Fact]
    public async Task V139_R03_AnonymousPermissionAndMandatoryStepUpCannotBeBypassed()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var anonymous = await service.PublishAsync(new(Guid.NewGuid(), new(CommandSource.PhysicalConsole), 0, Policy(), "anonymous attempt"));
        Assert.False(anonymous.Succeeded);
        var noGrant = await service.PublishAsync(new(Guid.NewGuid(), fixture.Invocation(), 0, Policy(), "missing step up"));
        Assert.False(noGrant.Succeeded);
        Assert.Equal("StepUpRequired", noGrant.Outcome.ReasonCode);
        Assert.Null((await service.ReadAsync()).Publication);
        await using var noPermission = await CreateFixtureAsync(allowPublish: false);
        var denied = await Service(noPermission).PublishAsync(new(Guid.NewGuid(), noPermission.Invocation(), 0, Policy(), "no policy permission"));
        Assert.False(denied.Succeeded);
        Assert.Equal("PermissionDenied", denied.Outcome.ReasonCode);
    }

    [Fact]
    public async Task V139_R04_ExactReplayReturnsOriginalOutcomeAndChangedReasonCannotReplay()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var command = await AuthorizedCommand(fixture, 0, Policy());
        var first = await service.PublishAsync(command);
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var auditCount = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var replay = await service.PublishAsync(command);
        Assert.True(replay.Succeeded, replay.Outcome.ReasonCode);
        Assert.Equal(first.Outcome, replay.Outcome);
        Assert.Equal(first.Publication!.ContentHash, replay.Publication!.ContentHash);
        Assert.Equal(first.Snapshot!.ContentHash, replay.Snapshot!.ContentHash);
        Assert.Equal(auditCount, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
        var changed = await service.PublishAsync(new(command.CorrelationId, command.Invocation, 0, command.Policy, "different reason"));
        Assert.False(changed.Succeeded);
        var changedSource = await service.PublishAsync(new(command.CorrelationId,
            command.Invocation with { Source = CommandSource.PhysicalConsole }, 0, command.Policy, command.Reason));
        Assert.False(changedSource.Succeeded);
        Assert.Equal(auditCount, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
        Assert.Single((await service.QueryAsync(new())).Records);
    }

    [Fact]
    public async Task V139_R05_StaleVersionAndWrongBoundGrantLeaveOneVersion()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var first = await service.PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var stale = await service.PublishAsync(await AuthorizedCommand(fixture, 0, Policy("2")));
        Assert.False(stale.Succeeded);
        var exact = await AuthorizedCommand(fixture, 1, Policy("2"));
        var altered = await service.PublishAsync(new(exact.CorrelationId, exact.Invocation, 1, Policy("3"), exact.Reason));
        Assert.False(altered.Succeeded);
        Assert.Equal("StepUpInvalid", altered.Outcome.ReasonCode);
        Assert.Single((await service.QueryAsync(new())).Records);
    }

    [Fact]
    public async Task V139_R06_PreCancelledAndLoggedOutRequestsLeaveNoPublication()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var command = await AuthorizedCommand(fixture, 0, Policy());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PublishAsync(command, cancelled.Token).AsTask());
        Assert.Null((await service.ReadAsync()).Publication);
        await fixture.Sessions.LogoutAsync(fixture.Sessions.Current.SessionId!.Value);
        var result = await service.PublishAsync(command);
        Assert.False(result.Succeeded);
        Assert.Null((await service.ReadAsync()).Publication);
    }

    [Fact]
    public async Task V139_R07_PhysicallyImpossibleReserveAndMissingRequiredRouteAreRejected()
    {
        await using var fixture = await CreateFixtureAsync();
        var impossible = Policy(minimumReserveBytes: long.MaxValue);
        var outcome = await Service(fixture).PublishAsync(await AuthorizedCommand(fixture, 0, impossible));
        Assert.False(outcome.Succeeded);
        Assert.Equal("TraceStorageReservePhysicallyImpossible", outcome.Outcome.ReasonCode);
        Assert.Null((await Service(fixture).ReadAsync()).Publication);
        var route = new TraceStorageRouteIdentity("Mes", "1", new string('A', 64));
        await using var required = await CreateFixtureAsync(scope: new("deployment", "1", new[] { route }));
        var missing = await Service(required).PublishAsync(await AuthorizedCommand(required, 0, Policy()));
        Assert.False(missing.Succeeded);
        Assert.Equal("TraceStorageRequiredRouteSetMismatch", missing.Outcome.ReasonCode);
    }

    [Fact]
    public async Task V139_R08_ValidPolicyPreflightCannotPassUnimplementedProductionGates()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var publication = await service.PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(publication.Succeeded, publication.Outcome.ReasonCode);
        var report = await service.GetPreflightAsync();
        Assert.True(report.PolicyValid);
        Assert.False(report.CanAdmitProduction);
        Assert.Equal(publication.Snapshot!.ContentHash, report.PolicySnapshotHash);
        Assert.Equal(TraceStoragePreflightStatus.Passed, report.Rows.Single(row => row.Gate == TraceStoragePreflightGate.SqliteProfile).Status);
        foreach (var gate in new[] { TraceStoragePreflightGate.ImageBacklog, TraceStoragePreflightGate.RequiredRouteBacklog,
            TraceStoragePreflightGate.EvidenceReconciliation, TraceStoragePreflightGate.Scrubber,
            TraceStoragePreflightGate.Checkpoint, TraceStoragePreflightGate.OtherDeploymentPolicies, TraceStoragePreflightGate.ProductionCycle })
            Assert.Equal(TraceStoragePreflightStatus.NotImplemented, report.Rows.Single(row => row.Gate == gate).Status);
    }

    [Fact]
    public void V139_R09_ReserveUsesLargerConstraintAndUnknownSpaceIsNeverZero()
    {
        var policy = Policy(minimumReserveBytes: 300, minimumReservePercent: 20);
        Assert.Equal(300, TraceStoragePolicyValidator.RequiredReserve(policy, 1000));
        Assert.Equal(400, TraceStoragePolicyValidator.RequiredReserve(policy, 2000));
        var report = TraceStoragePreflightEvaluator.Evaluate(new(false, "TraceStoragePolicyMissing"), null,
            new(false, "TraceStorageVolumeObservationUnavailable"), null, DateTimeOffset.UtcNow);
        Assert.False(report.CanAdmitProduction);
        var reserve = report.Rows.Single(row => row.Gate == TraceStoragePreflightGate.StorageReserve);
        Assert.Equal(TraceStoragePreflightStatus.Missing, reserve.Status);
        Assert.Null(reserve.Observed);
    }

    [Fact]
    public async Task V139_R10_CapacityRejectionPreservesPublishedVersionAndSnapshot()
    {
        await using var fixture = await CreateFixtureAsync(maximumEntries: 1);
        var service = Service(fixture);
        var first = await service.PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var second = await service.PublishAsync(await AuthorizedCommand(fixture, 1, Policy("2")));
        Assert.False(second.Succeeded);
        var current = await service.ReadAsync();
        Assert.Equal(first.Publication!.ContentHash, current.Publication!.ContentHash);
        Assert.Equal(first.Snapshot!.ContentHash, current.Snapshot!.ContentHash);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM trace_storage_policy_events;"));
    }

    [Theory]
    [InlineData("SnapshotHash")]
    [InlineData("PolicyId")]
    [InlineData("PolicyVersion")]
    [InlineData("PreviousContentHash")]
    public async Task V139_R11_TamperedStoredColumnClosesPolicyAndExistingDraftQueries(string column)
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var first = await service.PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name='trace_storage_policy_events' AND sql LIKE '%BEFORE UPDATE%';";
            var name = (string?)await trigger.ExecuteScalarAsync();
            Assert.NotNull(name);
            trigger.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name;";
            trigger.Parameters.AddWithValue("$name", name);
            var definition = (string?)await trigger.ExecuteScalarAsync();
            Assert.NotNull(definition);
            await using var tamper = connection.CreateCommand();
            Assert.Contains(column, new[] { "SnapshotHash", "PolicyId", "PolicyVersion", "PreviousContentHash" });
            tamper.CommandText = "DROP TRIGGER \"" + name!.Replace("\"", "\"\"") + "\"; UPDATE trace_storage_policy_events SET \"" + column + "\"=$hash WHERE Version=1;" + definition;
            tamper.Parameters.AddWithValue("$hash", new string('B', 64));
            await tamper.ExecuteNonQueryAsync();
        }
        Assert.False((await new SqliteTraceStoragePolicyQuery(fixture.Options).ReadAsync()).Available);
        Assert.False((await new SqliteRecipeDraftQuery(fixture.Options).QueryAsync(new())).Available);
    }

    [Fact]
    public async Task V139_R12_PolicyPublicationChangesProductionAdmissionDurableHeadBinding()
    {
        await using var fixture = await CreateFixtureAsync(productionAdmission: true);
        var before = await fixture.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        var result = await Service(fixture).PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(result.Succeeded, result.Outcome.ReasonCode);
        var after = await fixture.Store.ReadProductionAdmissionDurableHeadsAsync(CancellationToken.None);
        Assert.NotEqual(string.Join("|", before.OrderBy(pair => pair.Key)), string.Join("|", after.OrderBy(pair => pair.Key)));
        Assert.Contains(after.Keys, key => key.Contains("TraceStoragePolicy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V139_R13_ConcurrentExpectedVersionPublishesOnlyOneCompleteVersion()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var first = await AuthorizedCommand(fixture, 0, Policy("1"));
        var second = await AuthorizedCommand(fixture, 0, Policy("2"));
        var outcomes = await Task.WhenAll(service.PublishAsync(first).AsTask(), service.PublishAsync(second).AsTask());
        Assert.Single(outcomes, result => result.Succeeded);
        Assert.Single(outcomes, result => !result.Succeeded);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM trace_storage_policy_events;"));
        Assert.Single((await service.QueryAsync(new())).Records);
    }

    [Fact]
    public async Task V139_R14_CancelWhileSqliteWriterIsLockedLeavesNoPolicy()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = Service(fixture);
        var command = await AuthorizedCommand(fixture, 0, Policy());
        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Options.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await blocker.OpenAsync();
        using var transaction = blocker.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        var publishing = service.PublishAsync(command, cancellation.Token).AsTask();
        await Task.Delay(50);
        Assert.False(publishing.IsCompleted);
        cancellation.Cancel();
        // Once queued, the store awaits its definitive writer result. Cancellation
        // while this external lock prevents BEGIN must return a rejection or end
        // the caller's wait, and must never leave a publication behind.
        try { Assert.False((await publishing).Succeeded); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        transaction.Rollback();
        Assert.Null((await service.ReadAsync()).Publication);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM trace_storage_policy_events;"));
    }

    [Fact]
    public void V139_R15_WalMetadataFailureRemainsUnknownAndReachedLimitFails()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "-wal");
        Assert.Equal(0, TraceStoragePreflightEvaluator.ReadWalLength(path));
        Assert.Null(TraceStoragePreflightEvaluator.ReadWalLength(path, _ => throw new UnauthorizedAccessException()));
        Assert.Null(TraceStoragePreflightEvaluator.ReadWalLength(path, _ => throw new DirectoryNotFoundException()));
        var policy = Policy();
        var publication = new TraceStoragePolicyPublication(1, policy, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var current = new TraceStoragePolicyReadResult(true, "TraceStoragePolicyAvailable", publication, new(publication));
        TraceStoragePreflightRow Wal(long? bytes) => TraceStoragePreflightEvaluator.Evaluate(current,
            new("test", "1", Array.Empty<TraceStorageRouteIdentity>()), new(true, "TraceStorageVolumeObserved", path,
                1L << 40, 1L << 39, bytes), null, DateTimeOffset.UtcNow).Rows.Single(row => row.Gate == TraceStoragePreflightGate.WalCapacity);
        Assert.Equal(TraceStoragePreflightStatus.Passed, Wal(0).Status);
        Assert.Equal(TraceStoragePreflightStatus.Failed, Wal(policy.MaximumWalBytes).Status);
        Assert.Equal(TraceStoragePreflightStatus.Missing, Wal(null).Status);
        Assert.Null(Wal(null).Observed);
    }

    [Fact]
    public void V139_R16_PreflightCannotPassWithoutAnExactPolicyReference()
    {
        var rows = Enum.GetValues<TraceStoragePreflightGate>().Select(gate =>
            new TraceStoragePreflightRow(gate, TraceStoragePreflightStatus.Passed, "Observed")).ToArray();
        Assert.Throws<ArgumentException>(() => new TraceStoragePreflightReport(DateTimeOffset.UtcNow, null, null, rows));
        rows[0] = new(TraceStoragePreflightGate.Policy, TraceStoragePreflightStatus.Missing, "PolicyMissing");
        var missing = new TraceStoragePreflightReport(DateTimeOffset.UtcNow, null, null, rows);
        Assert.False(missing.PolicyValid);
        Assert.False(missing.CanAdmitProduction);
        var invalid = new TraceStoragePreflightReport(DateTimeOffset.UtcNow, 1, new string('A', 64), rows);
        Assert.False(invalid.CanAdmitProduction);
    }

    [Fact]
    public void V139_R17_InsufficientCurrentFreeSpaceDoesNotInvalidateAFeasiblePolicy()
    {
        var policy = Policy(minimumReserveBytes: 10L << 30);
        var scope = new TraceStorageDeploymentScope("test", "1", Array.Empty<TraceStorageRouteIdentity>());
        Assert.Empty(TraceStoragePolicyValidator.Validate(policy, scope, 100L << 30));
        var publication = new TraceStoragePolicyPublication(1, policy, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            1, Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var report = TraceStoragePreflightEvaluator.Evaluate(new(true, "TraceStoragePolicyAvailable", publication, new(publication)),
            scope, new(true, "TraceStorageVolumeObserved", "controlled observation", 100L << 30, 1L << 30, 0), null, DateTimeOffset.UtcNow);
        Assert.True(report.PolicyValid);
        Assert.Equal(TraceStoragePreflightStatus.Failed,
            report.Rows.Single(row => row.Gate == TraceStoragePreflightGate.StorageReserve).Status);
        Assert.False(report.CanAdmitProduction);
    }

    [Fact]
    public async Task V139_R18_CurrentRouteInventoryMayChangeWithoutReinterpretingHistoricalPolicies()
    {
        await using var fixture = await CreateFixtureAsync();
        var first = await Service(fixture).PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        await fixture.Store.DisposeAsync();
        var route = new TraceStorageRouteIdentity("Mes", "2", new string('B', 64));
        ProductionStoreOptions Options(int maximumEntries = 1000) => new(fixture.Options.DatabasePath)
        {
            AuditIntegrityPolicy = fixture.Options.AuditIntegrityPolicy, LocalIdentity = fixture.Options.LocalIdentity,
            RecipeDrafts = fixture.Options.RecipeDrafts, CommitTimeout = fixture.Options.CommitTimeout,
            QueryTimeout = fixture.Options.QueryTimeout,
            TraceStoragePolicies = new()
            {
                MaximumEntries = maximumEntries,
                DeploymentScope = new("V139.Test.Deployment", "2", new[] { route })
            }
        };
        var changedOptions = Options();
        await using (var store = new SqliteCommandStore(changedOptions))
        {
            var initialized = await store.Initialization;
            Assert.True(initialized.Committed, initialized.ReasonCode);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (store.Integrity?.State != AuditIntegrityState.Verified)
            {
                Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
                await Task.Delay(20, timeout.Token);
            }
            var identity = new LocalIdentityService(store, changedOptions.LocalIdentity!);
            await using var sessions = new InteractiveSessionService(identity,
                changedOptions.LocalIdentity!.AuthenticationPolicy, identity.PersistSessionEventAsync);
            var login = await sessions.SignInAsync(new(fixture.UserName, fixture.Password));
            Assert.True(login.Succeeded, login.ReasonCode);
            using var authorization = new LocalAuthorizationService(store, changedOptions.LocalIdentity, identity, sessions);
            var service = new TraceStoragePolicyService(changedOptions, authorization, store, new SqliteTraceStoragePolicyQuery(changedOptions));
            Assert.Equal(first.Snapshot!.ContentHash, (await service.ReadAsync(1)).Snapshot!.ContentHash);
            Assert.Equal(TraceStoragePreflightStatus.Mismatch,
                (await service.GetPreflightAsync()).Rows.Single(row => row.Gate == TraceStoragePreflightGate.RouteInventory).Status);
            async Task<PublishTraceStoragePolicyCommand> Command(TraceStoragePolicyDefinition policy)
            {
                var operation = Guid.NewGuid();
                var invocation = new CommandInvocation(CommandSource.Integration, sessions.Current.PrincipalId, sessions.Current.SessionId);
                var command = new PublishTraceStoragePolicyCommand(operation, invocation, 1, policy, "approve changed route inventory");
                var grant = await authorization.ReauthenticateAsync(new(operation, invocation,
                    new(Permission.ManageProductionPolicy, operation, command.AuthorizationTarget, AuditedCommandKind.PublishTraceStoragePolicy), fixture.Password));
                Assert.True(grant.Succeeded, grant.ReasonCode);
                return new(operation, invocation with { StepUpGrantId = grant.GrantId }, 1, policy, command.Reason);
            }
            var staleRoutes = await service.PublishAsync(await Command(Policy("2")));
            Assert.False(staleRoutes.Succeeded);
            Assert.Equal("TraceStorageRequiredRouteSetMismatch", staleRoutes.Outcome.ReasonCode);
            var nextPolicy = Policy("2", routes: new[] { new TraceStorageRouteLimit(route.RouteId,
                route.ContractVersion, route.ContractHash, new(100, 16 * 1024 * 1024, TimeSpan.FromHours(1))) });
            var next = await service.PublishAsync(await Command(nextPolicy));
            Assert.True(next.Succeeded, next.Outcome.ReasonCode);
            Assert.Equal(first.Snapshot.ContentHash, (await service.ReadAsync(1)).Snapshot!.ContentHash);
            Assert.Equal(TraceStoragePreflightStatus.Passed,
                (await service.GetPreflightAsync()).Rows.Single(row => row.Gate == TraceStoragePreflightGate.RouteInventory).Status);
        }
        await using var invalidCapacity = new SqliteCommandStore(Options(maximumEntries: 2));
        Assert.False((await invalidCapacity.Initialization).Committed);
    }

    [Fact]
    public async Task V139_R19_ExistingDraftWriterAndQueriesCoexistWithPublishedTracePolicy()
    {
        await using var fixture = await CreateFixtureAsync();
        var publication = await Service(fixture).PublishAsync(await AuthorizedCommand(fixture, 0, Policy()));
        Assert.True(publication.Succeeded, publication.Outcome.ReasonCode);
        var draft = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("policy coexistence"), "existing draft writer remains governed");
        Assert.True(draft.Saved, draft.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var history = await new SqliteRecipeDraftQuery(fixture.Options).QueryAsync(new());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Equal(draft.Revision!.RevisionContentHash, Assert.Single(history.Revisions).RevisionContentHash);
        var commands = await new SqliteCommandTraceQuery(fixture.Options).QueryAsync(new());
        Assert.Contains(commands.Records, record => record.CorrelationId == publication.Publication!.OperationId &&
            record.CommandKind == AuditedCommandKind.PublishTraceStoragePolicy);
        var policy = await Service(fixture).ReadAsync();
        Assert.True(policy.Available, policy.ReasonCode);
        Assert.Equal(publication.Snapshot!.ContentHash, policy.Snapshot!.ContentHash);
    }

    [Fact]
    public async Task V139_R20_ReplayRejectsChangedAuthorizationRevisionEvenWhenPermissionAndSessionRemain()
    {
        await using var fixture = await CreateFixtureAsync();
        var command = await AuthorizedCommand(fixture, 0, Policy());
        var first = await Service(fixture).PublishAsync(command);
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var state = await fixture.Store.ReadIdentityAsync(CancellationToken.None);
        var actor = state.EnumerateAccounts().Single(value => value.PrincipalId == first.Publication!.PrincipalId);
        var operation = Guid.NewGuid();
        var grant = await fixture.Authorization.ReauthenticateAsync(new(operation, fixture.Invocation(),
            new(Permission.ManagePermissions, operation, actor.PrincipalId.ToString("D"), AuditedCommandKind.SetHumanPermissions), fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        var management = new SetHumanPermissionsCommand(operation, fixture.Invocation(grant.GrantId), actor.PrincipalId, actor.Permissions);
        var prepared = await fixture.Authorization.PrepareCommandAsync(management, CancellationToken.None);
        var changed = await fixture.Authorization.HandleCommandAsync(management, Guid.NewGuid(), Guid.NewGuid(), prepared,
            null, new StoreDeadline(fixture.Options.CommitTimeout), CancellationToken.None);
        Assert.Equal(CommandDisposition.Accepted, changed.Disposition);
        Assert.Equal(command.Invocation.SessionId, fixture.Sessions.Current.SessionId);
        state = await fixture.Store.ReadIdentityAsync(CancellationToken.None);
        actor = state.EnumerateAccounts().Single(value => value.PrincipalId == first.Publication!.PrincipalId);
        Assert.True(actor.AuthorizationRevision > first.Publication!.AuthorizationRevision);
        Assert.Contains(Permission.ManageProductionPolicy, actor.Permissions);
        var count = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var replay = await Service(fixture).PublishAsync(command);
        Assert.False(replay.Succeeded);
        Assert.Equal(count, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
        Assert.Equal(first.Snapshot!.ContentHash, (await Service(fixture).ReadAsync()).Snapshot!.ContentHash);
    }

    [Fact]
    public async Task V139_R21_ReplayRejectsAChangedAuthorizationPolicyWithTheSameActorRevision()
    {
        await using var fixture = await CreateFixtureAsync();
        var command = await AuthorizedCommand(fixture, 0, Policy());
        var first = await Service(fixture).PublishAsync(command);
        Assert.True(first.Succeeded, first.Outcome.ReasonCode);
        var original = fixture.IdentityOptions.AuthorizationPolicy;
        var changedPolicy = new AuthorizationPolicy(original.Id, original.Version + ".changed",
            original.RoleBundles.ToDictionary(pair => pair.Key, pair => pair.Value.AsEnumerable()), original.StepUpPermissions);
        var options = new LocalIdentityOptions(fixture.IdentityOptions.StationId, fixture.IdentityOptions.PasswordPolicy,
            new Pbkdf2PasswordHasher(), fixture.IdentityOptions.AuthenticationPolicy, changedPolicy);
        using var authorization = new LocalAuthorizationService(fixture.Store, options, fixture.Identity, fixture.Sessions);
        var service = new TraceStoragePolicyService(fixture.Options, authorization, fixture.Store, new SqliteTraceStoragePolicyQuery(fixture.Options));
        var count = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        Assert.False((await service.PublishAsync(command)).Succeeded);
        Assert.Equal(count, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
        Assert.Equal(first.Snapshot!.ContentHash, (await service.ReadAsync()).Snapshot!.ContentHash);
    }

    internal static TraceStoragePolicyDefinition Policy(string version = "1", int days = 30,
        long minimumReserveBytes = 1024 * 1024, decimal minimumReservePercent = 1,
        IEnumerable<TraceStorageRouteLimit>? routes = null) => new("V139.Test.TraceStorage", version,
            "controlled verification only", "1", "Explicit test values; no production approval",
            Enum.GetValues<TraceRetentionClass>().Select(kind => new TraceRetentionRule(kind,
                kind == TraceRetentionClass.CompletedExport ? RetentionStartEvent.ExportCompleted :
                kind is TraceRetentionClass.QuarantineEvidence or TraceRetentionClass.RejectedRecipePackage ? RetentionStartEvent.Quarantined :
                RetentionStartEvent.ArtifactCreated, TimeSpan.FromDays(days))), minimumReserveBytes, minimumReservePercent,
            routes ?? Array.Empty<TraceStorageRouteLimit>(), new(100, 16 * 1024 * 1024, TimeSpan.FromHours(1)),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), 1024 * 1024, 100),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), 1024 * 1024, 100), 64 * 1024 * 1024);

    internal static Task<RecipeDraftStorageTests.Fixture> CreateFixtureAsync(bool allowPublish = true,
        TraceStorageDeploymentScope? scope = null, int maximumEntries = 1000, bool productionAdmission = false)
    {
        var roles = RecipeDraftTestPolicies.Authoring.RoleBundles.ToDictionary(pair => pair.Key, pair =>
            pair.Key == HumanRoleBundle.Administrator && allowPublish
                ? pair.Value.Append(Permission.ManageProductionPolicy).Distinct()
                : pair.Value.Where(permission => permission != Permission.ManageProductionPolicy));
        // Step-Up is deliberately absent from the configurable set: the command still mandates it.
        var authorization = new AuthorizationPolicy("trace-storage-tests", "v139", roles,
            RecipeDraftTestPolicies.Authoring.StepUpPermissions.Where(permission => permission != Permission.ManageProductionPolicy));
        return RecipeDraftStorageTests.Fixture.CreateAsync(authorizationPolicy: authorization,
            productionAdmission: productionAdmission ? new ProductionAdmissionStoreOptions() : null,
            traceStoragePolicies: new TraceStoragePolicyStoreOptions
            {
                DeploymentScope = scope ?? new("V139.Test.Deployment", "1", Array.Empty<TraceStorageRouteIdentity>()),
                MaximumEntries = maximumEntries
            });
    }

    internal static TraceStoragePolicyService Service(RecipeDraftStorageTests.Fixture fixture) => new(fixture.Options,
        fixture.Authorization, fixture.Store, new SqliteTraceStoragePolicyQuery(fixture.Options));

    internal static async Task<PublishTraceStoragePolicyCommand> AuthorizedCommand(RecipeDraftStorageTests.Fixture fixture,
        long expectedVersion, TraceStoragePolicyDefinition policy, Guid? operationId = null)
    {
        var correlation = operationId ?? Guid.NewGuid();
        var command = new PublishTraceStoragePolicyCommand(correlation, fixture.Invocation(), expectedVersion, policy, "approve explicit storage policy");
        var grant = await fixture.Authorization.ReauthenticateAsync(new(correlation, command.Invocation,
            new(Permission.ManageProductionPolicy, correlation, command.AuthorizationTarget, AuditedCommandKind.PublishTraceStoragePolicy), fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);
        return new(correlation, fixture.Invocation(grant.GrantId), expectedVersion, policy, command.Reason);
    }
}
