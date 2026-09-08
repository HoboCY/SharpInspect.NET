using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraSetupQueryCompatibilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task V117_Q01_CameraSchemaKeepsConfiguredHistoryQueriesAvailable(bool archive, bool drafts)
    {
        using var fixture = new Fixture();
        var options = fixture.Options(camera: true, archive, drafts);
        await Initialize(options);
        var before = await Hash(options.DatabasePath);

        var trace = await new SqliteCommandTraceQuery(options).QueryAsync(new());
        Assert.Empty(trace.Records);
        var integrity = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);
        var alarms = await new SqliteAlarmHistoryQuery(options).QueryAsync(new());
        Assert.True(alarms.Available, alarms.ReasonCode);
        var activation = Assert.Single(alarms.Records);
        Assert.Equal(AlarmTransitionKind.PolicyActivated, activation.Transition);
        Assert.Null(activation.Instance);
        if (archive)
        {
            var results = await new SqliteAlgorithmResultQuery(options).QueryAsync(new());
            Assert.True(results.Available, results.ReasonCode);
            Assert.Empty(results.Records);
        }
        if (drafts)
        {
            var history = await new SqliteRecipeDraftQuery(options).QueryAsync(new());
            Assert.True(history.Available, history.ReasonCode);
            Assert.Empty(history.Revisions);
        }
        Assert.Equal(before, await Hash(options.DatabasePath));
    }

    [Fact]
    public async Task V117_Q02_CameraSchemaCannotBeReadWithoutExplicitCameraConfiguration()
    {
        using var fixture = new Fixture();
        await Initialize(fixture.Options(camera: true, archive: true, drafts: true));
        var options = fixture.Options(camera: false, archive: true, drafts: true);
        await AssertQueriesRejected(options, "CameraSetupConfigurationRequired");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task V117_Q03_EnablingCameraOnAnExistingSchemaRequiresGovernedMigration(bool archive, bool drafts)
    {
        using var fixture = new Fixture();
        await Initialize(fixture.Options(camera: false, archive, drafts));
        var options = fixture.Options(camera: true, archive, drafts);
        var before = await Hash(options.DatabasePath);
        await AssertQueriesRejected(options, "CameraSetupGovernedMigrationRequired");
        await using (var rejected = new SqliteCommandStore(options))
        {
            var initialization = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(initialization.Committed);
            Assert.Equal("CameraSetupGovernedMigrationRequired", initialization.ReasonCode);
        }
        Assert.Equal(before, await Hash(options.DatabasePath));
    }

    [Fact]
    public async Task V117_Q04_CameraAuditDoesNotReinterpretOrdinaryClaimedPrincipalText()
    {
        using var fixture = new Fixture();
        var options = fixture.Options(camera: true, archive: true, drafts: true);
        await using (var store = new SqliteCommandStore(options))
        {
            var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await Verified(store);
            var fact = new CommandAuditFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                DateTimeOffset.UtcNow, AuditedCommandKind.ArmProduction, CommandSource.PhysicalConsole,
                "claimed/operator", null, null, CommandAuditPhase.Outcome,
                CommandDisposition.Rejected, "SessionRequired");
            var appended = await store.AppendAsync(fact, new StoreDeadline(TimeSpan.FromSeconds(4)));
            Assert.True(appended.Committed, appended.ReasonCode);
            await Verified(store);
        }

        var before = await Hash(options.DatabasePath);
        var trace = await new SqliteCommandTraceQuery(options).QueryAsync(new());
        Assert.Equal("claimed/operator", Assert.Single(trace.Records).ClaimedPrincipalId);
        var integrity = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);
        var alarms = await new SqliteAlarmHistoryQuery(options).QueryAsync(new());
        Assert.True(alarms.Available, alarms.ReasonCode);
        var results = await new SqliteAlgorithmResultQuery(options).QueryAsync(new());
        Assert.True(results.Available, results.ReasonCode);
        var drafts = await new SqliteRecipeDraftQuery(options).QueryAsync(new());
        Assert.True(drafts.Available, drafts.ReasonCode);
        Assert.Equal(before, await Hash(options.DatabasePath));
    }

    private static async Task AssertQueriesRejected(ProductionStoreOptions options, string reason)
    {
        var before = await Hash(options.DatabasePath);
        var trace = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteCommandTraceQuery(options).QueryAsync(new()));
        Assert.Equal(reason, trace.Message);
        var integrity = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Faulted, integrity.State);
        Assert.Equal(reason, integrity.ReasonCode);
        var alarms = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteAlarmHistoryQuery(options).QueryAsync(new()));
        Assert.Equal(reason, alarms.Message);
        if (options.AlgorithmResultArchive is not null)
        {
            var results = await new SqliteAlgorithmResultQuery(options).QueryAsync(new());
            Assert.False(results.Available);
            Assert.Equal(reason, results.ReasonCode);
        }
        if (options.RecipeDrafts is not null)
        {
            var drafts = await new SqliteRecipeDraftQuery(options).QueryAsync(new());
            Assert.False(drafts.Available);
            Assert.Equal(reason, drafts.ReasonCode);
        }
        Assert.Equal(before, await Hash(options.DatabasePath));
    }

    private static async Task Initialize(ProductionStoreOptions options)
    {
        await using var store = new SqliteCommandStore(options);
        var result = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.Committed, result.ReasonCode);
        await Verified(store);
    }

    private static async Task Verified(SqliteCommandStore store)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, deadline.Token);
        }
    }

    private static async Task<string> Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            "V117-Query-" + Guid.NewGuid().ToString("N"));
        private readonly AuditIntegrityPolicy _audit;
        private readonly LocalIdentityOptions _identity;
        private readonly AlarmPolicy _alarms = new("V117.Query.Alarms", "1", new[] {
            new AlarmPolicyRule("CAMERA_DISCONNECTED", "Camera", AlarmSeverity.Error,
                ProductionImpact.FaultAbort, true, AlarmNotification.UntilCleared, null)
        }, TimeSpan.FromSeconds(30));
        private readonly AlgorithmExecutionPolicy _execution = new("V117.Query.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            _audit = new("V117QueryStation", "1", "SharpInspect.Test.V117.Query." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(_directory, "private-keys"),
                CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1)
            };
            _identity = new(_audit.StationId, new LocalPasswordPolicy {
                Blocklist = PasswordBlocklist.Create("V117.Query.Blocklist", "1", new[] { "known-compromised-value" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development);
        }

        internal ProductionStoreOptions Options(bool camera, bool archive, bool drafts) =>
            new(Path.Combine(_directory, "query.sqlite")) {
                AuditIntegrityPolicy = _audit, LocalIdentity = _identity, AlarmPolicy = _alarms,
                CameraSetup = camera ? new CameraSetupStoreOptions() : null,
                AlgorithmResultArchive = archive ? new AlgorithmResultArchiveOptions() : null,
                RecipeDrafts = drafts ? new RecipeDraftStoreOptions(_execution) : null,
                CommitTimeout = TimeSpan.FromSeconds(4), QueryTimeout = TimeSpan.FromSeconds(4)
            };

        public void Dispose()
        {
            var key = WindowsMachineAuditKey.GetKeyPath(_audit);
            if (File.Exists(key)) File.Delete(key);
        }
    }
}
