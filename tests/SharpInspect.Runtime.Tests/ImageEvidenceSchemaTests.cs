using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Focused T50 schema checks for the schema-34 production image evidence opt-in. The
/// cases open real SQLite stores and the real signed central-audit chain, but they never
/// start a Runtime, camera, PLC or authorization fixture and never insert a pending
/// manifest or work fact: only the declared configuration, the signed activation
/// metadata, the immutable table shape and option/table matching are exercised.
/// </summary>
public sealed class ImageEvidenceSchemaTests
{
    [Fact]
    [Trait("VerificationId", "V150_Schema01")]
    public async Task V150_Schema01_MinimalSchema34InitializesAndColdReadsThePendingStore()
    {
        await using var fixture = await ImageEvidenceFixture.CreateAsync(34);

        Assert.Equal(34, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(3, fixture.EvidenceTableCount());
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM image_evidence_store_config;"));
        Assert.Equal(1, fixture.Scalar(
            "SELECT COUNT(*) FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM pending_image_manifests;"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM pending_image_work;"));

        // The declared configuration is bound byte-for-byte, including the qualified
        // stage binding, and the empty pending store is cold-readable without inventing
        // any manifest, work or image byte.
        Assert.Equal(fixture.Options.ImageEvidence!.BindingHash,
            fixture.Text("SELECT BindingHash FROM image_evidence_store_config WHERE Id=1;"));
        Assert.Equal(fixture.Options.ImageEvidence.StageRootBindingHash,
            fixture.Text("SELECT StageRootBindingHash FROM image_evidence_store_config WHERE Id=1;"));
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
        {
            SqliteCommandStore.RequireConfiguredImageEvidence(read.Handle!, fixture.Options.ImageEvidence,
                fixture.Deadline);
            SqliteCommandStore.VerifyImageEvidenceReadGuard(read.Handle!, fixture.Options, fixture.Deadline);
        }

        var report = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Verified, report.State);
        Assert.Equal(1, report.VerifiedFromSequence);
        Assert.Equal(fixture.Scalar("SELECT COALESCE(MAX(Sequence),0) FROM audit_entries;"),
            report.VerifiedThroughSequence);

        // Every declared initial fact is immutable: the declared update and delete
        // triggers exist and the immutable configuration refuses an edit.
        Assert.Equal(6, fixture.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN "
            + "('image_evidence_config_immutable_update','image_evidence_config_immutable_delete',"
            + "'pending_image_manifests_immutable_update','pending_image_manifests_immutable_delete',"
            + "'pending_image_work_immutable_update','pending_image_work_immutable_delete');"));
        await fixture.Store.DisposeAsync();
        var rejected = Assert.ThrowsAny<Exception>(() =>
            fixture.Execute("UPDATE image_evidence_store_config SET MaxImages=MaxImages+1 WHERE Id=1;"));
        Assert.Contains("ImmutableImageEvidenceConfiguration", rejected.Message, StringComparison.Ordinal);
        await fixture.StartAsync();

        await fixture.RestartAsync();

        Assert.Equal(34, fixture.Scalar("PRAGMA user_version;"));
        Assert.Equal(AuditIntegrityState.Verified, (await fixture.VerifyAsync()).State);
    }

    [Fact]
    [Trait("VerificationId", "V150_Schema02")]
    public async Task V150_Schema02_OptInRequiresItsPrerequisitesAndTheExactDeclaredTableSet()
    {
        var directory = ImageEvidenceFixture.NewDirectory("V150-Schema02");
        var unqualified = new ProductionStoreOptions(Path.Combine(directory, "unused.sqlite"))
        {
            ImageEvidence = new ProductionImageEvidenceStoreOptions(ImageEvidenceFixture.Stage(directory))
        };

        // The image evidence opt-in alone is rejected before any file is opened; it
        // requires production inspections, drafts, releases, the lifecycle ledger,
        // production arming, local identity and central audit integrity.
        Assert.StartsWith("ImageEvidenceRequiresInspectionsDraftsReleasesLifecycleArmingIdentityAndAudit",
            Assert.Throws<ArgumentException>(() => new SqliteCommandStore(unqualified)).Message);

        // The same opt-in validates every declared capacity and the stage binding.
        var complete = ImageEvidenceFixture.BuildOptions(directory, ImageEvidenceFixture.Station, 34);
        var stage = complete.ImageEvidence!.Stage;
        Assert.Contains("ImageEvidenceEntryCapacityInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageEvidenceFixture.WithEvidence(complete,
                new ProductionImageEvidenceStoreOptions(stage) { MaxImages = 0 }))).Message);
        Assert.Contains("ImageEvidencePayloadCapacityInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageEvidenceFixture.WithEvidence(complete,
                new ProductionImageEvidenceStoreOptions(stage)
                {
                    MaximumPayloadBytes = ProductionImageEvidenceStoreOptions.MaximumPayloadBytesHardLimit + 1
                }))).Message);
        Assert.Contains("ImageEvidenceTotalCapacityInvalid", Assert.Throws<ArgumentOutOfRangeException>(
            () => new SqliteCommandStore(ImageEvidenceFixture.WithEvidence(complete,
                new ProductionImageEvidenceStoreOptions(stage) { MaxTotalBytes = 4096 }))).Message);

        // A declared option whose canonical tables are not exactly the declared set
        // never reaches a projection, in either direction.
        await using var fixture = await ImageEvidenceFixture.CreateAsync(34, "V150-Schema02Store");
        await fixture.Store.DisposeAsync();
        fixture.Execute("CREATE TABLE image_evidence_extra(Id INTEGER PRIMARY KEY);");
        await using (var rejected = new SqliteCommandStore(fixture.Options))
        {
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreSchemaMismatch", initialized.ReasonCode);
        }

        fixture.Execute("DROP TABLE image_evidence_extra;");
        fixture.Execute("DROP TABLE pending_image_work;");
        await using (var rejected = new SqliteCommandStore(fixture.Options))
        {
            var initialized = await rejected.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(initialized.Committed);
            Assert.Equal("StoreSchemaMismatch", initialized.ReasonCode);
        }
    }

    [Fact]
    [Trait("VerificationId", "V150_Schema03")]
    public async Task V150_Schema03_SignedActivationBindsTheOptionExactlyOnceAndTamperFailsClosed()
    {
        await using var fixture = await ImageEvidenceFixture.CreateAsync(34, "V150-Schema03");

        // The activation payload is the exact option binding, and a second activation
        // can never be signed into the same store.
        Assert.Equal(Convert.ToBase64String(fixture.Options.ImageEvidence!.EncodeActivationPayload()),
            fixture.Text("SELECT Payload FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';"));
        await fixture.Store.DisposeAsync();
        using (var connection = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: false))
        {
            SqliteNative.Execute(connection.Handle!, "BEGIN IMMEDIATE;", fixture.Deadline);
            using var key = WindowsMachineAuditKey.Open(fixture.Options.AuditIntegrityPolicy!, false, out _);
            var repeated = Assert.Throws<InvalidOperationException>(() =>
                AuditChainDatabase.AppendImageEvidenceMetadata(connection.Handle!,
                    fixture.Options.AuditIntegrityPolicy!, key, SqliteCommandStore.ImageEvidenceActivationKind,
                    fixture.Options.ImageEvidence.EncodeActivationPayload(), fixture.Options.ImageEvidence,
                    fixture.Deadline));
            Assert.Equal("ImageEvidenceActivationConflict", repeated.Message);
            SqliteNative.Execute(connection.Handle!, "ROLLBACK;", fixture.Deadline);
        }

        // An offline edit of the immutable configuration row is detected by the signed
        // binding before any pending evidence is returned.
        fixture.Execute("DROP TRIGGER image_evidence_config_immutable_update;");
        fixture.Execute("UPDATE image_evidence_store_config SET MaxImages=MaxImages+1 WHERE Id=1;");
        var edited = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, edited.State);
        Assert.Contains("ImageEvidenceConfigurationMismatch", edited.ReasonCode, StringComparison.Ordinal);
        using (var read = SqliteNative.Open(fixture.Options.DatabasePath, readOnly: true))
        {
            var editedRead = Assert.Throws<InvalidOperationException>(() =>
                SqliteCommandStore.VerifyImageEvidenceReadGuard(read.Handle!, fixture.Options, fixture.Deadline));
            Assert.Contains("ImageEvidenceConfigurationMismatch", editedRead.Message, StringComparison.Ordinal);
        }
        fixture.Execute("UPDATE image_evidence_store_config SET MaxImages=MaxImages-1 WHERE Id=1;");

        // Removing the signed activation entry fails the exact once-presence check.
        fixture.Execute("DROP TRIGGER audit_entries_immutable_delete;");
        fixture.Execute("DELETE FROM audit_entries WHERE Kind='ImageEvidenceStoreActivated';");
        var removed = await fixture.VerifyAsync();
        Assert.Equal(AuditIntegrityState.Faulted, removed.State);
        Assert.Contains("ImageEvidenceActivationMissing", removed.ReasonCode, StringComparison.Ordinal);
    }
}

/// <summary>
/// One bounded production store on the Windows machine-audit key at the requested
/// generation: 32 carries drafts, releases, admission, arming and production
/// inspections, 33 adds the recipe lifecycle ledger and 34 adds the image evidence
/// store. The fixture never provisions a human account, so every case observes only
/// the initialization chain, the declared tables and the signed central audit.
/// </summary>
internal sealed class ImageEvidenceFixture : IAsyncDisposable
{
    internal const string Station = "V150ImageEvidenceStation";
    internal const long Budget = 64L * 1024 * 1024;
    private readonly string _directory;
    private bool _disposed;

    private ImageEvidenceFixture(string directory, ProductionStoreOptions options)
    {
        _directory = directory;
        Options = options;
    }

    internal ProductionStoreOptions Options { get; }
    internal SqliteCommandStore Store { get; private set; } = null!;
    internal StoreDeadline Deadline { get; } = new(TimeSpan.FromSeconds(30));

    internal static string NewDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
            label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static Images.ProductionImageStageOptions Stage(string directory)
    {
        var root = Path.Combine(directory, "stage");
        Directory.CreateDirectory(root);
        return new Images.ProductionImageStageOptions(root, 512L * 1024 * 1024, 1024L * 1024 * 1024, 10_000);
    }

    internal static ProductionStoreOptions BuildOptions(string directory, string station, int generation,
        AuditIntegrityPolicy? auditPolicy = null) => new(
        Path.Combine(directory, "evidence.sqlite"))
    {
        AuditIntegrityPolicy = auditPolicy ?? new AuditIntegrityPolicy(station, "v1",
            "SharpInspect.Test.V150.ImageEvidence." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(directory, "keys"),
            CheckpointEveryEntries = 2,
            MaximumVerificationEntries = 10_000,
            VerificationInterval = TimeSpan.FromSeconds(1)
        },
        LocalIdentity = new LocalIdentityOptions(station, new LocalPasswordPolicy
        {
            Blocklist = PasswordBlocklist.Create("v150-image-evidence-blocklist", "v1",
                new[] { "known-compromised-value" })
        }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, RecipeDraftTestPolicies.Authoring),
        RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy("V150.ImageEvidence.Execution",
            "1", TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1))),
        RecipeReleases = new RecipeReleaseStoreOptions(
            new RecipeGovernancePolicy("V150.ImageEvidence.Governance", "1", RecipeGovernanceMode.SingleApproverRelease)),
        ProductionAdmission = new ProductionAdmissionStoreOptions(),
        ProductionArming = new ProductionArmStoreOptions(),
        ProductionInspections = new ProductionInspectionStoreOptions(),
        RecipeLifecycle = generation >= 33 ? new RecipeLifecycleStoreOptions() : null,
        ImageEvidence = generation >= 34
            ? new ProductionImageEvidenceStoreOptions(Stage(directory))
            : null,
        CommitTimeout = TimeSpan.FromSeconds(5),
        QueryTimeout = TimeSpan.FromSeconds(5),
        QueueCapacity = 8
    };

    internal static ProductionStoreOptions WithEvidence(ProductionStoreOptions source,
        ProductionImageEvidenceStoreOptions evidence) => new(source.DatabasePath)
    {
        AuditIntegrityPolicy = source.AuditIntegrityPolicy,
        LocalIdentity = source.LocalIdentity,
        RecipeDrafts = source.RecipeDrafts,
        RecipeReleases = source.RecipeReleases,
        ProductionAdmission = source.ProductionAdmission,
        ProductionArming = source.ProductionArming,
        ProductionInspections = source.ProductionInspections,
        RecipeLifecycle = source.RecipeLifecycle,
        ImageEvidence = evidence,
        CommitTimeout = source.CommitTimeout,
        QueryTimeout = source.QueryTimeout,
        QueueCapacity = source.QueueCapacity
    };

    internal static async Task<ImageEvidenceFixture> CreateAsync(int generation, string label = "V150-Schema")
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Image evidence schema storage requires Windows machine protection.");

        var directory = NewDirectory(label);
        var fixture = new ImageEvidenceFixture(directory, BuildOptions(directory, Station, generation));
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

    internal ProductionStoreOptions Profile(int generation) => BuildOptions(_directory, Station, generation, Options.AuditIntegrityPolicy);

    internal async Task StartAsync()
    {
        Store = new SqliteCommandStore(Options);
        var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(initialized.Committed, initialized.ReasonCode);
    }

    internal async Task RestartAsync()
    {
        await Store.DisposeAsync();
        await StartAsync();
    }

    internal long Scalar(string sql) => ImageEvidenceFixture.Scalar(Options.DatabasePath, sql);

    internal static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal string Text(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Options.DatabasePath
            + ";Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    internal long EvidenceTableCount() => Scalar(Options.DatabasePath,
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN "
        + "('image_evidence_store_config','pending_image_manifests','pending_image_work');");

    internal MigrationDatabaseFingerprint Fingerprint(ProductionStoreOptions? options = null, string? path = null)
    {
        var effective = options ?? Options;
        using var connection = SqliteNative.Open(path ?? effective.DatabasePath, readOnly: true);
        SqliteNative.ConfigureSqliteLimit(connection.Handle!, effective);
        return StoreMigrationFingerprint.Read(connection.Handle!, Budget, Deadline);
    }

    internal ValueTask<AuditIntegrityReport> VerifyAsync() => new SqliteAuditIntegrityQuery(Options)
        .VerifyAsync(new AuditVerificationRequest(0, 9_900));

    internal void Execute(string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + Options.DatabasePath + ";Pooling=False");
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
