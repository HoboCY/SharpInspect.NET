using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Runtime.StoragePolicies;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Behavioral regression coverage for the bounded storage-recovery helper
/// (SqliteCommandStore.StorageRecovery) and its Identity / TraceStoragePolicy
/// transaction integration. The controlled WAL observation is an admission
/// waterline: it neither caps the physical WAL nor predicts SQLite page growth,
/// and only a real native truncation with a real readback may claim completion.
/// </summary>
public sealed class TraceStorageRecoveryTests
{
    [Fact]
    [Trait("VerificationId", "V155_R01")]
    public async Task V155_R01_ExactPublishStepUpAdmitsCorrectivePolicyWhileObservedWalBlocksOrdinaryWork()
    {
        long wal = 0;
        await using var fixture = await CreateFixtureAsync(_ => Volatile.Read(ref wal));
        var initial = await PublishAsync(fixture, Policy("1"));
        Assert.Equal(1, initial.Publication!.Version);

        // 65 MiB observed is over the approved 64 MiB limit, but below the bounded
        // 128 MiB control admission waterline of the recovery budget.
        Volatile.Write(ref wal, 65L << 20);

        var operation = Guid.NewGuid();
        var corrective = Policy("2", days: 45, checkpointBytes: 128L << 20);
        var command = new PublishTraceStoragePolicyCommand(operation, fixture.Invocation(), 1, corrective,
            "corrective checkpoint budget while the observed WAL is over the old limit");

        // A fresh password reauthentication bound to exactly this publication is
        // admitted by the recovery classification even though ordinary work is not.
        var grant = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(operation,
            command.Invocation,
            new StepUpBinding(Permission.ManageProductionPolicy, operation, command.AuthorizationTarget,
                AuditedCommandKind.PublishTraceStoragePolicy), fixture.Password));
        Assert.True(grant.Succeeded, grant.ReasonCode);

        var published = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
            new(operation, fixture.Invocation(grant.GrantId), 1, corrective, command.Reason));
        Assert.True(published.Succeeded, published.Outcome.ReasonCode);
        Assert.Equal(2, published.Publication!.Version);
        Assert.Equal(64L << 20, published.Publication.Policy.MaximumWalBytes);
        Assert.Equal(128L << 20, published.Publication.Policy.Checkpoint.MaximumBytes);
        await fixture.WaitForVerifiedAsync();

        // Nothing completes the checkpoint or reopens the admission gate implicitly:
        // the corrective policy only enables a future bounded stopped checkpoint.
        Assert.Equal(TraceCheckpointStatus.Awaiting, fixture.Store.Checkpoint.Status);
        Assert.Empty(ReadCheckpoints(fixture.Options));
        var blockedDraft = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("ordinary work stays gated"), "blocked while the observed WAL is over the limit");
        Assert.False(blockedDraft.Saved);
        Assert.Equal("TraceStoreWalLimit", blockedDraft.ReasonCode);
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));

        // A step-up for any other action is not part of the recovery path.
        var auditBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var wrongOperation = Guid.NewGuid();
        var wrongAction = await fixture.Authorization.ReauthenticateAsync(new StepUpRequest(wrongOperation,
            fixture.Invocation(),
            new StepUpBinding(Permission.ManagePermissions, wrongOperation,
                fixture.Sessions.Current.PrincipalId!, AuditedCommandKind.SetHumanPermissions), fixture.Password));
        Assert.False(wrongAction.Succeeded);
        Assert.Equal("StepUpAuditUnavailable", wrongAction.ReasonCode);
        // The valid password authentication persists; issuance of the unrelated
        // action grant is rejected and adds no second identity event.
        Assert.Equal(auditBefore + 1, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));

        // A changed policy bound to a different exact target cannot publish; the
        // committed corrective version stays current.
        var altered = Policy("3", days: 60, checkpointBytes: 128L << 20);
        var changed = Policy("3", days: 45, checkpointBytes: 128L << 20);
        var authorized = await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 2, changed);
        var refused = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
            new(authorized.CorrelationId, authorized.Invocation, 2, altered, authorized.Reason));
        Assert.False(refused.Succeeded);
        Assert.Equal("StepUpInvalid", refused.Outcome.ReasonCode);
        var current = await new SqliteTraceStoragePolicyQuery(fixture.Options).ReadAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(2, current.Publication!.Version);
        Assert.Equal(published.Snapshot!.ContentHash, current.Snapshot!.ContentHash);
    }

    [Fact]
    [Trait("VerificationId", "V155_R02")]
    public async Task V155_R02_ExhaustedFactQuotaAndAdmissionEqualityBlockAuthWithoutMutationOrReset()
    {
        long wal = 0;
        var budget = new TraceStorageRecoveryBudget(128L << 20, 8, 1L << 20);
        await using var fixture = await CreateFixtureAsync(_ => Volatile.Read(ref wal), budget);
        await PublishAsync(fixture, Policy("1"));
        Volatile.Write(ref wal, 65L << 20);

        // The quota counts every IdentityEvent / TraceStoragePolicyEvent since the
        // signed retention activation, including ordinary authentication; the fixture
        // bootstrap plus the first publication must already exhaust it.
        var exhausted = await ReadRetentionAsync(fixture.Options);
        Assert.Equal(budget.ContentHash, exhausted.RecoveryBudget!.ContentHash);
        var used = exhausted.ControlFactsUsed!.Value;
        Assert.True(used >= budget.MaximumControlFacts,
            $"bootstrap and the first publication must exhaust a quota of {budget.MaximumControlFacts}; observed {used}.");

        var auditBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var policyEventsBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM trace_storage_policy_events;");

        // Ordinary authentication is classified as admissible recovery activity, so the
        // exhausted fact quota is the boundary that denies it.
        await using (var exhaustedSessions = NewSessions(fixture))
        {
            var denied = await exhaustedSessions.SignInAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.False(denied.Succeeded);
            Assert.Equal("IdentityStorageRecoveryFactLimit", denied.ReasonCode);
        }

        // Ordinary control work is denied by the observed-WAL gate.
        var blockedDraft = await fixture.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 0, null,
            fixture.Document("quota exhausted"), "ordinary control work stays gated");
        Assert.False(blockedDraft.Saved);
        Assert.Equal("TraceStoreWalLimit", blockedDraft.ReasonCode);

        // Equality with the admission waterline must block, not only exceedance.
        Volatile.Write(ref wal, budget.ControlWalAdmissionBytes);
        await using (var equalitySessions = NewSessions(fixture))
        {
            var denied = await equalitySessions.SignInAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.False(denied.Succeeded);
            Assert.Equal("IdentityStorageRecoveryWalLimit", denied.ReasonCode);
        }

        Assert.Equal(auditBefore, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));
        Assert.Equal(policyEventsBefore, await fixture.ScalarAsync("SELECT COUNT(*) FROM trace_storage_policy_events;"));
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM recipe_draft_revisions;"));

        // The budget and the consumed quota are durable: a cold restart neither
        // resets the count nor rebinds the retention configuration.
        Volatile.Write(ref wal, 65L << 20);
        await using var restarted = await ReopenAsync(fixture, _ => Volatile.Read(ref wal));
        var afterRestart = await ReadRetentionAsync(fixture.Options);
        Assert.Equal(budget.ContentHash, afterRestart.RecoveryBudget!.ContentHash);
        Assert.Equal(budget.ControlWalAdmissionBytes, afterRestart.RecoveryBudget.ControlWalAdmissionBytes);
        Assert.Equal(budget.MaximumControlFacts, afterRestart.RecoveryBudget.MaximumControlFacts);
        Assert.Equal(budget.CheckpointPlanningReserveBytes, afterRestart.RecoveryBudget.CheckpointPlanningReserveBytes);
        Assert.Equal(used, afterRestart.ControlFactsUsed!.Value);
        var stillDenied = await restarted.Sessions.SignInAsync(new(fixture.UserName, fixture.Password));
        Assert.False(stillDenied.Succeeded);
        Assert.Equal("IdentityStorageRecoveryFactLimit", stillDenied.ReasonCode);
        Assert.Equal(used, (await ReadRetentionAsync(fixture.Options)).ControlFactsUsed);
    }

    [Fact]
    [Trait("VerificationId", "V155_R03")]
    public async Task V155_R03_InsufficientCheckpointPlanLeavesPriorPublicationVersion()
    {
        long wal = 0;
        await using var fixture = await CreateFixtureAsync(_ => Volatile.Read(ref wal));
        var initial = await PublishAsync(fixture, Policy("1"));
        Assert.Equal(1, initial.Publication!.Version);

        // Checkpoint bytes below observed WAL plus the 1 MiB planning reserve.
        var tooSmall = Policy("2", checkpointBytes: 512 * 1024);
        var byteCommand = await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 1, tooSmall);
        var auditBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var rejectedBytes = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(byteCommand);
        Assert.False(rejectedBytes.Succeeded);
        Assert.Equal("TraceStoragePolicyRecoveryCheckpointBytesInsufficient", rejectedBytes.Outcome.ReasonCode);
        Assert.Equal(auditBefore, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));

        // Bytes are sufficient but the frame budget is not; the value is below the
        // required frame count for every permitted SQLite page size.
        var tooFewFrames = Policy("3", checkpointBytes: 64L << 20, checkpointFrames: 8);
        var frameCommand = await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, 1, tooFewFrames);
        auditBefore = await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;");
        var rejectedFrames = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(frameCommand);
        Assert.False(rejectedFrames.Succeeded);
        Assert.Equal("TraceStoragePolicyRecoveryCheckpointFramesInsufficient", rejectedFrames.Outcome.ReasonCode);
        Assert.Equal(auditBefore, await fixture.ScalarAsync("SELECT COUNT(*) FROM audit_entries;"));

        // Rejected plans leave the prior publication version and snapshot untouched.
        var current = await new SqliteTraceStoragePolicyQuery(fixture.Options).ReadAsync();
        Assert.True(current.Available, current.ReasonCode);
        Assert.Equal(1, current.Publication!.Version);
        Assert.Equal(initial.Snapshot!.ContentHash, current.Snapshot!.ContentHash);
        Assert.Equal(1, await fixture.ScalarAsync("SELECT COUNT(*) FROM trace_storage_policy_events;"));

        // A feasible successor is still publishable afterwards: the rejections neither
        // consumed the prior publication nor blocked fresh exact-publish authority.
        var successor = await PublishAsync(fixture, Policy("2", checkpointBytes: 128L << 20), expectedVersion: 1);
        Assert.Equal(2, successor.Publication!.Version);
    }

    [Fact]
    [Trait("VerificationId", "V155_R04")]
    public async Task V155_R04_StartupCheckpointClaimsCompletionOnlyFromActualNativeTruncation()
    {
        long wal = 0;
        await using var fixture = await CreateFixtureAsync(_ => Volatile.Read(ref wal));
        await PublishAsync(fixture, Policy("1"));
        Volatile.Write(ref wal, 65L << 20); // fixed observation for the whole recovery window

        var now = DateTimeOffset.UtcNow;
        await using (var recovery = await ReopenAsync(fixture, _ => Volatile.Read(ref wal)))
        {
            // The initial 64 MiB checkpoint budget cannot plan the observed 65 MiB WAL.
            Assert.Equal(TraceCheckpointStatus.BudgetExceeded, recovery.Store.Checkpoint.Status);

            // Authentication and the corrective publication still commit while the
            // observed WAL is over the old limit.
            var signIn = await recovery.Sessions.SignInAsync(
                new PasswordSignInRequest(fixture.UserName, fixture.Password));
            Assert.True(signIn.Succeeded, signIn.ReasonCode);
            var invocation = new CommandInvocation(CommandSource.Integration,
                signIn.Identity!.PrincipalId.ToString("D"), signIn.Session.SessionId);
            var operation = Guid.NewGuid();
            var corrective = Policy("2", days: 45, checkpointBytes: 128L << 20);
            var command = new PublishTraceStoragePolicyCommand(operation, invocation, 1, corrective,
                "corrective checkpoint budget for the next stopped maintenance");
            var grant = await recovery.Authorization.ReauthenticateAsync(new StepUpRequest(operation, invocation,
                new StepUpBinding(Permission.ManageProductionPolicy, operation, command.AuthorizationTarget,
                    AuditedCommandKind.PublishTraceStoragePolicy), fixture.Password));
            Assert.True(grant.Succeeded, grant.ReasonCode);
            var service = new TraceStoragePolicyService(fixture.Options, recovery.Authorization, recovery.Store,
                new SqliteTraceStoragePolicyQuery(fixture.Options));
            var published = await service.PublishAsync(new(operation,
                invocation with { StepUpGrantId = grant.GrantId }, 1, corrective, command.Reason));
            Assert.True(published.Succeeded, published.Outcome.ReasonCode);
            Assert.Equal(2, published.Publication!.Version);
            Assert.Equal(TraceCheckpointStatus.BudgetExceeded, recovery.Store.Checkpoint.Status);
        }

        // Negative control: the same physical truncation observed through a fixed
        // (wrong) readback must not be claimed as completed.
        await using (var unobserved = await ReopenAsync(fixture, _ => Volatile.Read(ref wal),
            new TraceCheckpointTestHooks(UtcNow: () => now.AddMinutes(2))))
        {
            Assert.Equal(TraceCheckpointStatus.Busy, unobserved.Store.Checkpoint.Status);
        }

        // Only a readback that follows the actual file may claim completion.
        var switched = false;
        await using (var recovered = await ReopenAsync(fixture,
            path => switched ? Length(path) : 65L << 20,
            new TraceCheckpointTestHooks(UtcNow: () => now.AddMinutes(4),
                Phase: phase => { if (phase == "BeforeNative") switched = true; })))
        {
            Assert.True(switched);
            Assert.Equal(TraceCheckpointStatus.Completed, recovered.Store.Checkpoint.Status);
            Assert.Equal(0, recovered.Store.Checkpoint.WalBytes);
        }

        // The prior budget-exceeded outcome stays in the signed history.
        var rows = ReadCheckpoints(fixture.Options);
        Assert.Contains(rows, row => row.Record.Status == TraceCheckpointStatus.BudgetExceeded);
        Assert.Equal(TraceCheckpointStatus.Completed, rows[^1].Record.Status);
    }

    private static Task<RecipeDraftStorageTests.Fixture> CreateFixtureAsync(Func<string, long> readWalLength,
        TraceStorageRecoveryBudget? recoveryBudget = null) =>
        TraceCheckpointTests.CreateFixtureAsync(readWalLength: readWalLength,
            recoveryBudget: recoveryBudget ?? new TraceStorageRecoveryBudget(128L << 20, 10000, 1L << 20));

    private static async Task<TraceStoragePolicyResult> PublishAsync(RecipeDraftStorageTests.Fixture fixture,
        TraceStoragePolicyDefinition policy, long expectedVersion = 0)
    {
        var result = await TraceStoragePolicyRuntimeTests.Service(fixture).PublishAsync(
            await TraceStoragePolicyRuntimeTests.AuthorizedCommand(fixture, expectedVersion, policy));
        Assert.True(result.Succeeded, result.Outcome.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        return result;
    }

    // The shared tiny checkpoint budget (1 MiB / 100 frames) cannot plan even the
    // 1 MiB reserve, so every policy here states an explicit checkpoint budget.
    private static TraceStoragePolicyDefinition Policy(string version = "1", int days = 30,
        long maximumWalBytes = 64L << 20, long checkpointBytes = 64L << 20, int checkpointFrames = 100000)
    {
        var policy = TraceStoragePolicyRuntimeTests.Policy(version, days);
        return new(policy.PolicyId, policy.Version, policy.ApprovalReference, policy.ApprovalVersion, policy.Rationale,
            policy.RetentionRules, policy.MinimumReserveBytes, policy.MinimumReservePercent, policy.RequiredRoutes,
            policy.ImageBacklog, policy.EvidenceStageTimeout, policy.TraceCommitTimeout, policy.Scrubber,
            new(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(2), checkpointBytes, checkpointFrames), maximumWalBytes);
    }

    private static InteractiveSessionService NewSessions(RecipeDraftStorageTests.Fixture fixture) =>
        new(fixture.Identity, fixture.IdentityOptions.AuthenticationPolicy, fixture.Identity.PersistSessionEventAsync);

    // Physically retires the fixture writer and reopens the same database with the
    // requested controlled observation, mirroring fixture.RestartStoreAsync(restartIdentity: true)
    // while keeping the WAL readback and checkpoint hooks under test control.
    private static async Task<Reopened> ReopenAsync(RecipeDraftStorageTests.Fixture fixture,
        Func<string, long> readWalLength, TraceCheckpointTestHooks? hooks = null)
    {
        fixture.Authorization.Dispose();
        await fixture.Sessions.DisposeAsync();
        await fixture.Store.DisposeAsync();
        var store = new SqliteCommandStore(fixture.Options, readWalLength, hooks);
        var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await RecipeDraftStorageTests.Fixture.WaitForVerifiedAsync(store);
        var identity = new LocalIdentityService(store, fixture.IdentityOptions,
            new RecipeDraftStorageTests.Fixture.FixtureConsole());
        var sessions = new InteractiveSessionService(identity, fixture.IdentityOptions.AuthenticationPolicy,
            identity.PersistSessionEventAsync);
        var authorization = new LocalAuthorizationService(store, fixture.IdentityOptions, identity, sessions);
        return new Reopened(store, sessions, authorization);
    }

    private static async Task<EvidenceRetentionSnapshot> ReadRetentionAsync(ProductionStoreOptions options)
    {
        var snapshot = await new SqliteEvidenceRetentionQuery(options).ReadAsync(new EvidenceRetentionFilter());
        Assert.True(snapshot.Available, snapshot.ReasonCode);
        return snapshot;
    }

    private static IReadOnlyList<TraceCheckpointRow> ReadCheckpoints(ProductionStoreOptions options)
    {
        using var connection = SqliteNative.Open(options.DatabasePath, readOnly: true);
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(10));
        return AuditChainDatabase.ReadTraceCheckpoints(connection.Handle!,
            SqliteCommandStore.ReadRetentionConfiguration(connection.Handle!, deadline), deadline);
    }

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private sealed class Reopened : IAsyncDisposable
    {
        private readonly InteractiveSessionService _sessions;
        private readonly LocalAuthorizationService _authorization;

        internal Reopened(SqliteCommandStore store, InteractiveSessionService sessions,
            LocalAuthorizationService authorization)
        {
            Store = store;
            _sessions = sessions;
            _authorization = authorization;
        }

        internal SqliteCommandStore Store { get; }
        internal InteractiveSessionService Sessions => _sessions;
        internal LocalAuthorizationService Authorization => _authorization;

        public async ValueTask DisposeAsync()
        {
            _authorization.Dispose();
            await _sessions.DisposeAsync();
            await Store.DisposeAsync();
        }
    }
}
