using System.Text;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Recipes;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Real schema-9 storage boundaries.  The fixture uses the signed SQLite writer,
/// LocalIdentityService, InteractiveSessionService, LocalAuthorizationService and
/// RecipeDraftService; no test bypasses the identity or draft service boundary.
/// </summary>
public sealed class RecipeDraftStorageBoundaryTests
{
    [Fact]
    public async Task V115_P01_RevisionCapacityRejectsWholeSaveAndPreservesHistory()
    {
        var execution = ExecutionPolicy();
        var factory = DraftFactory.Create(execution, fieldCount: 1, textLength: 8);
        await using var fixture = await BoundaryFixture.CreateAsync(factory, maximumRevisionCount: 1,
            maximumRecordBytes: 64 * 1024, maximumTotalBytes: 128 * 1024);
        var draftId = Guid.NewGuid();
        var first = await fixture.SaveAsync(draftId, 0, null, "initial");
        var before = await fixture.Query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 20));
        Assert.True(before.Available, before.ReasonCode);
        Assert.Single(before.Revisions);

        var rejected = await fixture.Drafts.SaveAsync(fixture.Request(draftId, 1,
            first.RevisionContentHash, "over-revision-limit"));

        Assert.False(rejected.Saved);
        Assert.Equal("RecipeDraftRevisionCapacityExceeded", rejected.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var after = await fixture.Query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 20));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Single(after.Revisions);
        Assert.Equal(first.RevisionContentHash, after.Revisions[0].RevisionContentHash);
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);
    }

    [Fact]
    public async Task V115_P02_TotalByteCapacityRejectsWholeSaveAndPreservesHistory()
    {
        var execution = ExecutionPolicy();
        var factory = DraftFactory.Create(execution, fieldCount: 1, textLength: 8);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(factory.Content, out var document, out var reason), reason);
        var payloadBytes = Encoding.UTF8.GetByteCount(document!.PayloadJson);
        var totalCapacity = checked(payloadBytes + payloadBytes / 2);
        await using var fixture = await BoundaryFixture.CreateAsync(factory, maximumRevisionCount: 10,
            maximumRecordBytes: payloadBytes, maximumTotalBytes: totalCapacity);
        var draftId = Guid.NewGuid();
        var first = await fixture.SaveAsync(draftId, 0, null, "initial");
        var before = await fixture.Query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 20));
        Assert.True(before.Available, before.ReasonCode);
        Assert.Single(before.Revisions);

        var rejected = await fixture.Drafts.SaveAsync(fixture.Request(draftId, 1,
            first.RevisionContentHash, "over-total-byte-limit"));

        Assert.False(rejected.Saved);
        Assert.Equal("RecipeDraftTotalCapacityExceeded", rejected.ReasonCode);
        await fixture.WaitForVerifiedAsync();
        var after = await fixture.Query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 20));
        Assert.True(after.Available, after.ReasonCode);
        Assert.Single(after.Revisions);
        Assert.Equal(first.RevisionContentHash, after.Revisions[0].RevisionContentHash);
        Assert.Equal(AuditIntegrityState.Verified, fixture.Store.Integrity?.State);
    }

    [Fact]
    public async Task V115_P03_NearTwoMiBDraftReopensThroughReadOnlyQueryWithStableHash()
    {
        var execution = ExecutionPolicy();
        var factory = DraftFactory.Create(execution, fieldCount: 256, textLength: 3500);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(factory.Content, out var document, out var reason), reason);
        Assert.NotNull(document);
        var payloadBytes = Encoding.UTF8.GetByteCount(document!.PayloadJson);
        Assert.InRange(payloadBytes, 1_800_000, 2 * 1024 * 1024);

        await using var fixture = await BoundaryFixture.CreateAsync(factory, maximumRevisionCount: 4,
            maximumRecordBytes: 2 * 1024 * 1024, maximumTotalBytes: 4 * 1024 * 1024);
        var draftId = Guid.NewGuid();
        var saved = await fixture.SaveAsync(draftId, 0, null, "near-two-megabytes");
        var expectedHash = saved.Content.ContentHash;
        await fixture.CloseWriterAsync();

        var integrity = await new SqliteAuditIntegrityQuery(fixture.Options).VerifyAsync(new());
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);
        var query = new SqliteRecipeDraftQuery(fixture.Options);
        var page = await query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 1));
        Assert.True(page.Available, page.ReasonCode);
        var row = Assert.Single(page.Revisions);
        var read = await query.ReadAsync(draftId, 1);
        Assert.True(read.Available, read.ReasonCode);
        Assert.NotNull(read.Revision);
        Assert.Equal(expectedHash, row.Content.ContentHash);
        Assert.Equal(expectedHash, read.Revision!.Content.ContentHash);
        Assert.Equal(saved.RevisionContentHash, row.RevisionContentHash);
    }

    [Fact]
    public async Task V115_P04_PagePayloadIndexAndPredecessorTamperingMakeReadOnlyHistoryUnavailable()
    {
        var execution = ExecutionPolicy();
        var factory = DraftFactory.Create(execution, fieldCount: 1, textLength: 8);
        await using var fixture = await BoundaryFixture.CreateAsync(factory, maximumRevisionCount: 8,
            maximumRecordBytes: 64 * 1024, maximumTotalBytes: 256 * 1024);
        var draftId = Guid.NewGuid();
        var first = await fixture.SaveAsync(draftId, 0, null, "first");
        await fixture.SaveAsync(draftId, 1, first.RevisionContentHash, "second");
        await fixture.CloseWriterAsync();

        var cases = new[]
        {
            (Name: "payload", Mutate: (Action<string>)(path => Mutate(path,
                "UPDATE recipe_draft_revisions SET PayloadJson=PayloadJson || ' ' WHERE Position=2;"))),
            (Name: "index", Mutate: (Action<string>)(path => Mutate(path,
                "UPDATE recipe_draft_revisions SET Position=3 WHERE Position=2;"))),
            (Name: "predecessor", Mutate: (Action<string>)(path => Mutate(path,
                "UPDATE recipe_draft_revisions SET PreviousRevisionContentHash=$p0 WHERE Position=2;",
                new string('0', 64))))
        };

        foreach (var testCase in cases)
        {
            var path = Path.Combine(fixture.DirectoryPath, "tampered-" + testCase.Name + ".sqlite");
            File.Copy(fixture.Options.DatabasePath, path, overwrite: true);
            try
            {
                testCase.Mutate(path);
                var options = fixture.OptionsFor(path);
                var query = new SqliteRecipeDraftQuery(options);
                var page = await query.QueryAsync(new RecipeDraftFilter(DraftId: draftId,
                    ThroughPosition: 1, PageSize: 1));
                Assert.False(page.Available, testCase.Name + ": " + page.ReasonCode);
                Assert.Empty(page.Revisions);
                Assert.DoesNotContain("Exception", page.ReasonCode, StringComparison.Ordinal);
                var read = await query.ReadAsync(draftId, 1);
                Assert.False(read.Available, testCase.Name + ": read unexpectedly available");
                Assert.Null(read.Revision);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task V115_P05_CancelAfterCommitBeforeWriterCompletionReturnsCommittedResult()
    {
        var execution = ExecutionPolicy();
        var factory = DraftFactory.Create(execution, fieldCount: 1, textLength: 8);
        await using var fixture = await BoundaryFixture.CreateAsync(factory, maximumRevisionCount: 4,
            maximumRecordBytes: 64 * 1024, maximumTotalBytes: 256 * 1024);
        var draftId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        // The existing authorization commit guard runs after SQLite COMMIT. Hold
        // its synchronization object so the test can cancel after the row is
        // durable while the service task still awaits the definitive writer result.
        var grantSync = typeof(LocalAuthorizationService).GetField("_grantSync",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fixture.Authorization);
        Assert.NotNull(grantSync);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            Monitor.Enter(grantSync!);
            acquired.Set();
            try { release.Wait(TimeSpan.FromSeconds(10)); }
            finally { Monitor.Exit(grantSync!); }
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            var save = fixture.Drafts.SaveAsync(fixture.Request(draftId, 0, null, "cancel-after-commit"),
                cancellation.Token).AsTask();
            await WaitForRevisionRowAsync(fixture.Options.DatabasePath, draftId);
            Assert.False(save.IsCompleted);

            cancellation.Cancel();
            await Task.Delay(50);
            Assert.False(save.IsCompleted);
            release.Set();

            var result = await save.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result.Saved, result.ReasonCode);
            Assert.NotNull(result.Revision);
            await fixture.WaitForVerifiedAsync();
            var page = await fixture.Query.QueryAsync(new RecipeDraftFilter(DraftId: draftId, PageSize: 4));
            Assert.True(page.Available, page.ReasonCode);
            Assert.Single(page.Revisions);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task WaitForRevisionRowAsync(string databasePath, Guid draftId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM recipe_draft_revisions WHERE DraftId=$draftId;";
                command.Parameters.AddWithValue("$draftId", draftId.ToString("D"));
                if (Convert.ToInt64(command.ExecuteScalar()) == 1) return;
            }
            catch (SqliteException) { }
            await Task.Delay(5, timeout.Token);
        }
    }

    private static void Mutate(string path, string sql, params string?[] args)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TRIGGER IF EXISTS recipe_draft_revision_immutable_update;";
        drop.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var arg in args)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$p" + command.Parameters.Count;
            parameter.Value = arg is null ? DBNull.Value : arg;
            command.Parameters.Add(parameter);
        }
        command.ExecuteNonQuery();
    }

    private static AlgorithmExecutionPolicy ExecutionPolicy() =>
        new("V115.Boundary.Execution", "1", TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

    private sealed class BoundaryFixture : IAsyncDisposable
    {
        private readonly AuditIntegrityPolicy _auditPolicy;
        private bool _writerClosed;
        private bool _disposed;

        private BoundaryFixture(string directoryPath, ProductionStoreOptions options,
            SqliteCommandStore store, LocalIdentityService identity, InteractiveSessionService sessions,
            LocalAuthorizationService authorization, RecipeDraftService drafts,
            SqliteRecipeDraftQuery query, DraftFactory factory, string userName, string password,
            CommandInvocation invocation)
        {
            DirectoryPath = directoryPath;
            Options = options;
            _auditPolicy = options.AuditIntegrityPolicy!;
            Store = store;
            Identity = identity;
            Sessions = sessions;
            Authorization = authorization;
            Drafts = drafts;
            Query = query;
            Factory = factory;
            UserName = userName;
            Password = password;
            Invocation = invocation;
        }

        internal string DirectoryPath { get; }
        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; }
        internal LocalIdentityService Identity { get; }
        internal InteractiveSessionService Sessions { get; }
        internal LocalAuthorizationService Authorization { get; }
        internal RecipeDraftService Drafts { get; }
        internal SqliteRecipeDraftQuery Query { get; }
        internal DraftFactory Factory { get; }
        internal string UserName { get; }
        internal string Password { get; }
        internal CommandInvocation Invocation { get; }

        internal static async Task<BoundaryFixture> CreateAsync(DraftFactory factory,
            int maximumRevisionCount, int maximumRecordBytes, long maximumTotalBytes)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Recipe Draft boundaries require Windows machine key protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V115-DraftBoundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var station = "V115DraftBoundaryStation";
            var audit = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V115.Boundary." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identityOptions = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v115-boundary-blocklist", "1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                RecipeDraftTestPolicies.Authoring);
            var draftOptions = new RecipeDraftStoreOptions(factory.ExecutionPolicy)
            {
                MaximumRecordBytes = maximumRecordBytes,
                MaximumPageBytes = maximumRecordBytes,
                MaximumTotalBytes = maximumTotalBytes,
                MaximumRevisionCount = maximumRevisionCount
            };
            var options = new ProductionStoreOptions(Path.Combine(directory, "drafts.sqlite"))
            {
                AuditIntegrityPolicy = audit,
                LocalIdentity = identityOptions,
                RecipeDrafts = draftOptions,
                CommitTimeout = TimeSpan.FromSeconds(8),
                QueryTimeout = TimeSpan.FromSeconds(8),
                QueueCapacity = 8
            };

            SqliteCommandStore? store = null;
            InteractiveSessionService? sessions = null;
            LocalAuthorizationService? authorization = null;
            try
            {
                store = new SqliteCommandStore(options);
                var initialized = await store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(initialized.Committed, initialized.ReasonCode);
                await WaitForVerifiedAsync(store);

                var identity = new LocalIdentityService(store, identityOptions, new FixtureConsole());
                const string userName = "v115.boundary.admin";
                const string password = "V115 boundary storage password 2026!";
                var token = await identity.ProvisionBootstrapTokenAsync();
                Assert.True(token.Succeeded, token.ReasonCode);
                var bootstrap = token.Token!.TakeForDisplay();
                token.Token.Dispose();
                await WaitForVerifiedAsync(store);
                var created = await identity.CreateFirstAdministratorAsync(
                    new BootstrapAdministratorRequest(station, bootstrap, userName,
                        "V115 Boundary Administrator", password));
                Assert.True(created.Succeeded, created.ReasonCode);
                created.RecoveryKit?.Dispose();
                await WaitForVerifiedAsync(store);

                sessions = new InteractiveSessionService(identity,
                    identityOptions.AuthenticationPolicy, identity.PersistSessionEventAsync);
                authorization = new LocalAuthorizationService(store, identityOptions, identity, sessions);
                var login = await sessions.SignInAsync(new PasswordSignInRequest(userName, password));
                Assert.True(login.Succeeded, login.ReasonCode);
                await WaitForVerifiedAsync(store);
                var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
                    login.Identity!.PrincipalId.ToString("D"), login.Session.SessionId);
                var query = new SqliteRecipeDraftQuery(options);
                var drafts = new RecipeDraftService(new[] { factory }, options, authorization, query);
                return new BoundaryFixture(directory, options, store, identity, sessions,
                    authorization, drafts, query, factory, userName, password, invocation);
            }
            catch
            {
                authorization?.Dispose();
                if (sessions is not null) await sessions.DisposeAsync();
                if (store is not null) await store.DisposeAsync();
                DeleteOwnedDirectory(directory, audit);
                throw;
            }
        }

        internal RecipeDraftSaveRequest Request(Guid draftId, long revision, string? hash,
            string reason) => new(Guid.NewGuid(), draftId, revision, hash, Factory.Content,
                reason, Invocation);

        internal ProductionStoreOptions OptionsFor(string databasePath) => new(databasePath)
        {
            AuditIntegrityPolicy = Options.AuditIntegrityPolicy,
            LocalIdentity = Options.LocalIdentity,
            RecipeDrafts = Options.RecipeDrafts,
            CommitTimeout = Options.CommitTimeout,
            QueryTimeout = Options.QueryTimeout,
            QueueCapacity = Options.QueueCapacity
        };

        internal async Task<RecipeDraftRevision> SaveAsync(Guid draftId, long revision,
            string? hash, string reason)
        {
            var result = await Drafts.SaveAsync(Request(draftId, revision, hash, reason));
            Assert.True(result.Saved, result.ReasonCode);
            Assert.NotNull(result.Revision);
            await WaitForVerifiedAsync();
            return result.Revision!;
        }

        internal async Task WaitForVerifiedAsync() => await WaitForVerifiedAsync(Store);

        internal async Task CloseWriterAsync()
        {
            if (_writerClosed) return;
            _writerClosed = true;
            await Drafts.DisposeAsync();
            Authorization.Dispose();
            await Sessions.DisposeAsync();
            await Store.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await CloseWriterAsync();
            DeleteOwnedDirectory(DirectoryPath, _auditPolicy);
        }

        private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (store.Integrity is { State: AuditIntegrityState.Verified }) return;
                if (store.Integrity is { State: AuditIntegrityState.Faulted } fault)
                    throw new XunitException(fault.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Audit integrity did not become Verified: " +
                store.Integrity?.ReasonCode);
        }

        private static void DeleteOwnedDirectory(string directory, AuditIntegrityPolicy policy)
        {
            try
            {
                var keyPath = WindowsMachineAuditKey.GetKeyPath(policy);
                if (File.Exists(keyPath)) File.Delete(keyPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class FixtureConsole : IPhysicalConsoleAuthority
        {
            public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-V115-BOUNDARY");
        }
    }

    private sealed class DraftFactory : IVisionAlgorithmFactory
    {
        private DraftFactory(AlgorithmExecutionPolicy executionPolicy,
            AlgorithmDescriptor descriptor, RecipeDraftContent content)
        {
            ExecutionPolicy = executionPolicy;
            Descriptor = descriptor;
            Content = content;
        }

        internal AlgorithmExecutionPolicy ExecutionPolicy { get; }
        internal RecipeDraftContent Content { get; }
        public AlgorithmDescriptor Descriptor { get; }

        internal static DraftFactory Create(AlgorithmExecutionPolicy executionPolicy,
            int fieldCount, int textLength)
        {
            var text = new string('x', textLength);
            var fields = Enumerable.Range(0, fieldCount)
                .Select(index => new AlgorithmFieldDefinition("Field" + index.ToString("D3"),
                    AlgorithmScalarType.String, "text", true,
                    new AlgorithmScalarConstraints(maxLength: textLength), authoringDefault: null,
                    helpText: text))
                .ToArray();
            var schema = new AlgorithmConfigurationSchema("V115.Boundary.Config", "1", fields);
            var values = fields.Select(field => new AlgorithmConfigurationEntry(field.Key, field.Unit,
                AlgorithmScalarValue.FromString(text))).ToArray();
            var configuration = AlgorithmConfigurationSnapshot.Create(schema, values);
            var overlay = new OverlayContract("V115.Boundary.Overlay", "1", 0, 64, 16);
            var result = new AlgorithmResultSchema("V115.Boundary.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), new[] { "NoDefect" }, overlay);
            var descriptor = new AlgorithmDescriptor(new AlgorithmIdentity("V115.Boundary.Algorithm", "1"),
                schema, result);
            var binding = RecipeAlgorithmBinding.FromDescriptor(descriptor);
            var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger,
                100, 0, new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8,
                null, 1000, 0, null);
            var requirement = new RecipePolicyRequirement(RecipePolicyKind.AlgorithmExecution,
                new RecipeContractReference(executionPolicy.Id, executionPolicy.Version,
                    executionPolicy.ContentHash));
            var content = new RecipeDraftContent("V115.Boundary.Recipe", "边界草稿", binding,
                configuration, "TopCamera", camera, TimeSpan.FromMilliseconds(100), null,
                new[] { requirement });
            return new DraftFactory(executionPolicy, descriptor, content);
        }

        public ValueTask<IReadOnlyList<AlgorithmValidationIssue>> ValidateConfigurationAsync(
            AlgorithmConfigurationSnapshot configuration, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AlgorithmValidationIssue>>(
                Array.Empty<AlgorithmValidationIssue>());

        public ValueTask<IVisionAlgorithm> CreateAsync(AlgorithmConfigurationSnapshot configuration,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IVisionAlgorithm>(new NoopAlgorithm());

        private sealed class NoopAlgorithm : IVisionAlgorithm
        {
            public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
                ValueTask.CompletedTask;

            public ValueTask<AlgorithmResult> ExecuteAsync(AlgorithmExecutionContext context,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
