using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T48 schema checks for the schema-33 Recipe lifecycle opt-in. The cases open
/// real SQLite stores and the real signed central-audit chain, but they never start a
/// Runtime, PLC, device or authorization fixture and never claim a lifecycle transition:
/// the lifecycle ledger stays empty and only its declared configuration, signed
/// activation metadata and table/option matching are exercised.
/// </summary>
public sealed class RecipeLifecycleSchemaTests
{
    [Fact]
    [Trait("VerificationId", "V148_S12")]
    public void V148_S12_RecipeLifecycleOptInRequiresDraftsIdentityAndAudit()
    {
        var path = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            "V148-S12-unused-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var policy = new AuditIntegrityPolicy("V148S12Station", "v1", "V148.S12." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true
        };
        var identity = new LocalIdentityOptions("V148S12Station",
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v148-s12-blocklist", "v1",
                    new[] { "known-compromised-value" })
            },
            new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, RecipeDraftTestPolicies.Authoring);
        var drafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V148.S12.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));

        // The lifecycle opt-in alone is rejected; it requires the draft ledger, the local
        // identity and the central audit policy before any file is opened.
        Assert.StartsWith("RecipeLifecycleRequiresDraftsIdentityAndAudit", Assert.Throws<ArgumentException>(
            () => new SqliteCommandStore(new ProductionStoreOptions(path)
            {
                RecipeLifecycle = new RecipeLifecycleStoreOptions()
            })).Message);
        Assert.StartsWith("RecipeLifecycleRequiresDraftsIdentityAndAudit", Assert.Throws<ArgumentException>(
            () => new SqliteCommandStore(new ProductionStoreOptions(path)
            {
                AuditIntegrityPolicy = policy, LocalIdentity = identity,
                RecipeLifecycle = new RecipeLifecycleStoreOptions()
            })).Message);
        Assert.StartsWith("RecipeLifecycleRequiresDraftsIdentityAndAudit", Assert.Throws<ArgumentException>(
            () => new SqliteCommandStore(new ProductionStoreOptions(path)
            {
                AuditIntegrityPolicy = policy, RecipeDrafts = drafts,
                RecipeLifecycle = new RecipeLifecycleStoreOptions()
            })).Message);

        // The same opt-in validation bounds every declared capacity.
        var invalid = Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteCommandStore(
            new ProductionStoreOptions(path)
            {
                AuditIntegrityPolicy = policy, LocalIdentity = identity, RecipeDrafts = drafts,
                RecipeLifecycle = new RecipeLifecycleStoreOptions { MaxEvents = 0 }
            }));
        Assert.Contains("RecipeLifecycleEntryCapacityInvalid", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("VerificationId", "V148_S13")]
    public async Task V148_S13_OldSchemasStayUnchangedWithoutTheLifecycleOption()
    {
        // A schema-9 draft store and a schema-32 armed store keep their original
        // version, tables and verification when no lifecycle option is declared.
        await using (var draftsOnly = await StoreFixture.CreateAsync(lifecycle: false))
        {
            Assert.Equal(9, draftsOnly.Scalar("PRAGMA user_version;"));
            Assert.Equal(0, draftsOnly.LifecycleTableCount());
            Assert.Equal(AuditIntegrityState.Verified, (await draftsOnly.VerifyAsync()).State);

            await draftsOnly.RestartAsync();

            Assert.Equal(9, draftsOnly.Scalar("PRAGMA user_version;"));
            Assert.Equal(0, draftsOnly.LifecycleTableCount());
            Assert.Equal(AuditIntegrityState.Verified, (await draftsOnly.VerifyAsync()).State);
        }

        await using (var armed = await StoreFixture.CreateAsync(lifecycle: false, arming: true))
        {
            Assert.Equal(32, armed.Scalar("PRAGMA user_version;"));
            Assert.Equal(0, armed.LifecycleTableCount());
            Assert.Equal(AuditIntegrityState.Verified, (await armed.VerifyAsync()).State);

            await armed.RestartAsync();

            Assert.Equal(32, armed.Scalar("PRAGMA user_version;"));
            Assert.Equal(0, armed.LifecycleTableCount());
            Assert.Equal(AuditIntegrityState.Verified, (await armed.VerifyAsync()).State);
        }
    }

    [Fact]
    [Trait("VerificationId", "V148_S14")]
    public async Task V148_S14_MinimalSchema33InitializesAndColdReadsEmptyLifecycleLedger()
    {
        await using var fixture = await StoreFixture.CreateAsync(lifecycle: true);

        Assert.Equal(33, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(2, fixture.LifecycleTableCount());
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM recipe_lifecycle_store_config;"));
        Assert.Equal(1, fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';"));
        Assert.Equal(0, fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleEvent';"));

        // The declared configuration is bound byte-for-byte and the empty lifecycle
        // ledger is cold-readable without inventing any transition.
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
        {
            SqliteCommandStore.RequireConfiguredRecipeLifecycle(read.Handle!,
                fixture.Options.RecipeLifecycle!, fixture.Deadline);
            Assert.Empty(SqliteCommandStore.ReadRecipeLifecycleRows(read.Handle!,
                fixture.Options.RecipeLifecycle!, fixture.Deadline));
        }

        var report = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Verified, report.State);
        Assert.Equal(1, report.VerifiedFromSequence);
        Assert.Equal(fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;"),
            report.VerifiedThroughSequence);
        Assert.Empty(fixture.VerifiedHistory());

        await fixture.RestartAsync();

        Assert.Equal(33, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(AuditIntegrityState.Verified, (await fixture.VerifyAsync()).State);
        Assert.Empty(fixture.VerifiedHistory());
    }

    [Fact]
    [Trait("VerificationId", "V148_S15")]
    public async Task V148_S15_Schema32ArmingRejectsLifecycleOptInAndSchema33RejectsMissingOption()
    {
        // Opening an earlier governed store with the new opt-in is a governed migration,
        // never an automatic upgrade, and the files stay byte-identical.
        await using (var armed = await StoreFixture.CreateAsync(lifecycle: false, arming: true))
        {
            Assert.Equal(32, armed.Scalar("PRAGMA user_version;"));
            await armed.Store.DisposeAsync();
            var before = Snapshot(armed.Options.DatabasePath);

            await using var rejected = new SqliteCommandStore(Clone(armed.Options,
                lifecycle: new RecipeLifecycleStoreOptions()));
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("RecipeLifecycleGovernedMigrationRequired", initialized.ReasonCode);
            AssertFilesEqual(before, Snapshot(armed.Options.DatabasePath));
            Assert.Equal(32, Scalar(armed.Options.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(0, LifecycleTableCount(armed.Options.DatabasePath));
        }

        // A schema-33 store opened without its mandatory opt-in fails closed.
        await using (var minimal = await StoreFixture.CreateAsync(lifecycle: true))
        {
            await minimal.Store.DisposeAsync();
            var before = Snapshot(minimal.Options.DatabasePath);

            await using var rejected = new SqliteCommandStore(Clone(minimal.Options, lifecycle: null));
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("RecipeLifecycleConfigurationRequired", initialized.ReasonCode);
            AssertFilesEqual(before, Snapshot(minimal.Options.DatabasePath));
            Assert.Equal(33, Scalar(minimal.Options.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(2, LifecycleTableCount(minimal.Options.DatabasePath));
        }
    }

    [Fact]
    [Trait("VerificationId", "V148_S16")]
    public async Task V148_S16_UnexpectedOrMissingLifecycleTablesFailClosed()
    {
        await using var fixture = await StoreFixture.CreateAsync(lifecycle: true);
        await fixture.Store.DisposeAsync();

        // A declared option whose canonical tables are not exactly the declared set
        // never reaches a projection.
        fixture.Execute("CREATE TABLE recipe_lifecycle_extra(Id INTEGER PRIMARY KEY);");
        await using (var rejected = new SqliteCommandStore(fixture.Options))
        {
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreSchemaMismatch", initialized.ReasonCode);
        }
        fixture.Execute("DROP TABLE recipe_lifecycle_extra;");

        // A declared opt-in against an earlier user_version is a governed migration even
        // when the lifecycle tables are still present.
        fixture.Execute("PRAGMA user_version=32;");
        await using (var rejected = new SqliteCommandStore(fixture.Options))
        {
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("RecipeLifecycleGovernedMigrationRequired", initialized.ReasonCode);
        }

        // The reverse direction: the declared read tables are missing at schema 33.
        fixture.Execute("PRAGMA user_version=33;");
        fixture.Execute("DROP TABLE recipe_lifecycle_events;");
        await using (var rejected = new SqliteCommandStore(fixture.Options))
        {
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreSchemaMismatch", initialized.ReasonCode);
        }
    }

    [Fact]
    [Trait("VerificationId", "V148_S17")]
    public async Task V148_S17_SignedActivationMetadataBindsTheOptionAndTamperFailsClosed()
    {
        await using var fixture = await StoreFixture.CreateAsync(lifecycle: true);

        // Exactly one signed activation entry binds the declared option, and the empty
        // ledger adds no lifecycle event metadata.
        Assert.Equal(1, fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';"));
        Assert.Equal(Convert.ToBase64String(fixture.Options.RecipeLifecycle!.EncodeActivationPayload()),
            fixture.Text("SELECT Payload FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';"));
        Assert.Equal(AuditIntegrityState.Verified, (await fixture.VerifyAsync()).State);
        Assert.Empty(fixture.VerifiedHistory());

        // An offline edit of the immutable configuration row is detected by the signed
        // binding before any lifecycle evidence is returned.
        await fixture.Store.DisposeAsync();
        fixture.Execute("DROP TRIGGER recipe_lifecycle_config_immutable_update;");
        fixture.Execute("UPDATE recipe_lifecycle_store_config SET MaxEvents=MaxEvents+1 WHERE Id=1;");

        var edited = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, edited.State);
        Assert.Contains("RecipeLifecycleConfigurationMismatch", edited.ReasonCode, StringComparison.Ordinal);
        var editedRead = Assert.Throws<InvalidOperationException>(() => fixture.VerifiedHistory());
        Assert.Contains("RecipeLifecycle", editedRead.Message, StringComparison.Ordinal);

        // Removing the signed activation entry instead fails the reverse
        // row-to-metadata membership check.
        fixture.Execute("UPDATE recipe_lifecycle_store_config SET MaxEvents=MaxEvents-1 WHERE Id=1;");
        fixture.Execute("DROP TRIGGER audit_entries_immutable_delete;");
        fixture.Execute("DELETE FROM audit_entries WHERE Kind='RecipeLifecycleStoreActivated';");

        var removed = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, removed.State);
        Assert.Contains("RecipeLifecycleAuditCountMismatch", removed.ReasonCode, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => fixture.VerifiedHistory());
    }

    private static ProductionStoreOptions Clone(ProductionStoreOptions source,
        RecipeLifecycleStoreOptions? lifecycle) => new(source.DatabasePath)
        {
            AuditIntegrityPolicy = source.AuditIntegrityPolicy,
            LocalIdentity = source.LocalIdentity,
            RecipeDrafts = source.RecipeDrafts,
            ProductionAdmission = source.ProductionAdmission,
            ProductionArming = source.ProductionArming,
            RecipeLifecycle = lifecycle,
            CommitTimeout = source.CommitTimeout,
            QueryTimeout = source.QueryTimeout,
            QueueCapacity = source.QueueCapacity
        };

    private static long LifecycleTableCount(string path) => Scalar(path,
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'recipe_lifecycle_%';");

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static Dictionary<string, byte[]> Snapshot(string databasePath)
    {
        // Normalize the WAL state first, so two snapshots of an untouched store are
        // byte-identical instead of depending on journal handling.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) snapshot[path] = File.ReadAllBytes(path);
        }
        return snapshot;
    }

    private static void AssertFilesEqual(Dictionary<string, byte[]> expected,
        Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(value => value, StringComparer.Ordinal),
            actual.Keys.OrderBy(value => value, StringComparer.Ordinal));
        foreach (var pair in expected)
            Assert.Equal(pair.Value, actual[pair.Key]);
    }

    /// <summary>
    /// One bounded schema store on the Windows machine-audit key. The fixture never
    /// provisions a human account, so every case observes only the initialization
    /// chain, the declared ledger shape and the signed central audit.
    /// </summary>
    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private bool _disposed;

        private StoreFixture(string directory, ProductionStoreOptions options)
        {
            _directory = directory;
            Options = options;
        }

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; private set; } = null!;
        internal StoreDeadline Deadline { get; } = new(TimeSpan.FromSeconds(10));

        internal static async Task<StoreFixture> CreateAsync(bool lifecycle, bool arming = false)
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Recipe lifecycle schema storage requires Windows machine protection.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V148-LifecycleSchema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            const string station = "V148LifecycleSchemaStation";
            var policy = new AuditIntegrityPolicy(station, "v1",
                "SharpInspect.Test.V148.Lifecycle." + Guid.NewGuid().ToString("N"))
            {
                AllowInitialKeyCreation = true,
                KeyDirectory = Path.Combine(directory, "keys"),
                CheckpointEveryEntries = 2,
                MaximumVerificationEntries = 10_000,
                VerificationInterval = TimeSpan.FromSeconds(1)
            };
            var identity = new LocalIdentityOptions(station,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v148-lifecycle-blocklist", "v1",
                        new[] { "known-compromised-value" })
                },
                new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                RecipeDraftTestPolicies.Authoring);
            var execution = new AlgorithmExecutionPolicy("V148.Lifecycle.Execution", "1",
                TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
            var options = new ProductionStoreOptions(Path.Combine(directory, "lifecycle.sqlite"))
            {
                AuditIntegrityPolicy = policy,
                LocalIdentity = identity,
                RecipeDrafts = new RecipeDraftStoreOptions(execution),
                ProductionAdmission = arming ? new ProductionAdmissionStoreOptions() : null,
                ProductionArming = arming ? new ProductionArmStoreOptions() : null,
                RecipeLifecycle = lifecycle ? new RecipeLifecycleStoreOptions() : null,
                CommitTimeout = TimeSpan.FromSeconds(4),
                QueryTimeout = TimeSpan.FromSeconds(4),
                QueueCapacity = 8
            };

            var fixture = new StoreFixture(directory, options);
            try
            {
                await fixture.StartAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal async Task StartAsync()
        {
            Store = new SqliteCommandStore(Options);
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
        }

        internal async Task RestartAsync()
        {
            await Store.DisposeAsync();
            await StartAsync();
        }

        internal long Scalar(string sql) => RecipeLifecycleSchemaTests.Scalar(Options.DatabasePath, sql);

        internal string Text(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Options.DatabasePath
                + ";Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar())!;
        }

        internal long LifecycleTableCount() => RecipeLifecycleSchemaTests.LifecycleTableCount(
            Options.DatabasePath);

        internal ValueTask<AuditIntegrityReport> VerifyAsync() => new SqliteAuditIntegrityQuery(Options)
            .VerifyAsync(new AuditVerificationRequest(0, 9_900));

        internal IReadOnlyList<SqliteCommandStore.RecipeLifecycleStoredRow> VerifiedHistory()
        {
            using var read = SqliteNative.Open(Options.DatabasePath, readOnly: true);
            return SqliteCommandStore.ReadRecipeLifecycleVerifiedHistory(read.Handle!, Options, Deadline);
        }

        internal void Execute(string sql)
        {
            using var connection = new SqliteConnection("Data Source=" + Options.DatabasePath
                + ";Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (Store is not null) await Store.DisposeAsync();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }
}
