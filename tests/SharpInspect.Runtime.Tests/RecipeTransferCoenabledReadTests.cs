using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V138-G07 exercises the schema-24 read/startup seam with the other bounded
/// ledgers enabled.  The databases are intentionally empty beyond their
/// immutable store activation rows: this isolates configuration, central
/// verification, read-only projection, and writer restart compatibility from
/// hardware or workflow actions.
/// </summary>
public sealed class RecipeTransferCoenabledReadTests
{
    [Fact]
    public async Task V138_G07_TransferManualCameraDraftColdReadAndWriterRestart()
    {
        var options = CreateOptions("manual-camera-draft", includeManual: true);
        await ExerciseColdReadAndRestartAsync(options, async () =>
        {
            var transfer = await new SqliteRecipeTransferQuery(options)
                .QueryAsync(new RecipeTransferFilter(PageSize: 20));
            AssertAvailable(transfer.Available, transfer.ReasonCode);
            Assert.Empty(transfer.Records);

            var manual = await new SqliteManualInspectionQuery(options).ReadCurrentAsync();
            AssertAvailable(manual.Available, manual.ReasonCode);

            var drafts = await new SqliteRecipeDraftQuery(options)
                .QueryAsync(new RecipeDraftFilter(PageSize: 20));
            AssertAvailable(drafts.Available, drafts.ReasonCode);
            Assert.Empty(drafts.Revisions);
        });
    }

    [Fact]
    public async Task V138_G07_TransferProductionAdmissionQualificationColdReadAndWriterRestart()
    {
        var options = CreateOptions("production-admission-qualification", includeProductionAdmission: true,
            includeStationQualification: true);
        await ExerciseColdReadAndRestartAsync(options, async () =>
        {
            var transfer = await new SqliteRecipeTransferQuery(options)
                .QueryAsync(new RecipeTransferFilter(PageSize: 20));
            AssertAvailable(transfer.Available, transfer.ReasonCode);
            Assert.Empty(transfer.Records);

            var admission = await new SqliteProductionAdmissionHistoryQuery(options)
                .ReadCurrentAsync();
            AssertAvailable(admission.Available, admission.ReasonCode);

            var qualification = await new SqliteStationQualificationHistoryQuery(options)
                .ReadCurrentAsync();
            AssertAvailable(qualification.Available, qualification.ReasonCode);
            Assert.Null(qualification.Header);
        });
    }

    [Fact]
    public async Task V138_G07_TransferReleasePlcActivationPreviewColdReadAndWriterRestart()
    {
        var options = CreateOptions("release-plc-activation-preview", includeReleaseStack: true,
            includePreview: true);
        await ExerciseColdReadAndRestartAsync(options, async () =>
        {
            var transfer = await new SqliteRecipeTransferQuery(options)
                .QueryAsync(new RecipeTransferFilter(PageSize: 20));
            AssertAvailable(transfer.Available, transfer.ReasonCode);
            Assert.Empty(transfer.Records);

            var releases = await new SqliteReleasedRecipeQuery(options)
                .QueryAsync(new ReleasedRecipeFilter(PageSize: 20));
            AssertAvailable(releases.Available, releases.ReasonCode);
            Assert.Empty(releases.Recipes);

            var contracts = await new SqlitePlcResultContractQuery(options)
                .QueryAsync(new PlcResultContractFilter(PageSize: 20));
            AssertAvailable(contracts.Available, contracts.ReasonCode);
            Assert.Empty(contracts.Revisions);

            var activation = await new SqliteRecipeActivationQuery(options).ReadCurrentAsync();
            AssertAvailable(activation.Available, activation.ReasonCode);

            var preview = await new SqlitePreviewSessionQuery(options).ReadCurrentAsync();
            AssertAvailable(preview.Available, preview.ReasonCode);
        });
    }

    private static async Task ExerciseColdReadAndRestartAsync(ProductionStoreOptions options,
        Func<Task> readOnlyProjections)
    {
        if (!OperatingSystem.IsWindows())
            throw SkipException.ForSkip("Recipe transfer storage requires Windows machine-key protection.");

        await using (var initial = new SqliteCommandStore(options))
        {
            var initialized = await initial.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(initial);
        }

        Assert.Equal(24L, await ScalarAsync(options.DatabasePath, "PRAGMA user_version;"));
        var beforeRead = HashDatabase(options.DatabasePath);

        var audit = await new SqliteAuditIntegrityQuery(options)
            .VerifyAsync(new AuditVerificationRequest(0, 1000));
        Assert.Equal(AuditIntegrityState.Verified, audit.State);

        var trace = await new SqliteCommandTraceQuery(options)
            .QueryAsync(new CommandTraceFilter(PageSize: 20));
        Assert.Empty(trace.Records);

        await readOnlyProjections();

        // These readers use query_only snapshots and must not initialize a
        // missing ledger, rotate keys, or alter the existing schema-24 file.
        Assert.Equal(beforeRead, HashDatabase(options.DatabasePath));

        await using (var restarted = new SqliteCommandStore(options))
        {
            var initialized = await restarted.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitForVerifiedAsync(restarted);
        }

        var afterRestartAudit = await new SqliteAuditIntegrityQuery(options)
            .VerifyAsync(new AuditVerificationRequest(0, 1000));
        Assert.Equal(AuditIntegrityState.Verified, afterRestartAudit.State);
        var afterRestartTransfer = await new SqliteRecipeTransferQuery(options)
            .QueryAsync(new RecipeTransferFilter(PageSize: 20));
        AssertAvailable(afterRestartTransfer.Available, afterRestartTransfer.ReasonCode);
        Assert.Empty(afterRestartTransfer.Records);
    }

    private static ProductionStoreOptions CreateOptions(string scenario,
        bool includeManual = false, bool includeProductionAdmission = false,
        bool includeStationQualification = false, bool includeReleaseStack = false,
        bool includePreview = false)
    {
        var evidenceRoot = Path.Combine(Path.GetTempPath(),
            "SharpInspect.NET-validation-artifacts", "ticket38",
            "V138-G07-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceRoot);

        var station = "V138G07" + Guid.NewGuid().ToString("N")[..12];
        var audit = new AuditIntegrityPolicy(station, "development-v1",
            "SharpInspect.V138.G07." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            KeyDirectory = Path.Combine(evidenceRoot, "private-keys"),
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1)
        };
        var identity = new LocalIdentityOptions(station,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("v138-g07-blocklist", "1",
                    new[] { "known-compromised-value" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            AuthorizationPolicy.Development);
        var execution = new AlgorithmExecutionPolicy("V138.G07.Execution", "1",
            TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        var releases = includeReleaseStack || includePreview
            ? new RecipeReleaseStoreOptions(new RecipeGovernancePolicy("V138.G07.Release", "1",
                RecipeGovernanceMode.SingleApproverRelease))
            : null;
        var plc = includeReleaseStack || includePreview
            ? new PlcResultContractStoreOptions()
            : null;
        var camera = includeManual || includePreview
            ? new CameraSetupStoreOptions()
            : null;
        var activation = includePreview ? new RecipeActivationStoreOptions() : null;

        return new ProductionStoreOptions(Path.Combine(evidenceRoot, "trace.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = identity,
            RecipeDrafts = new RecipeDraftStoreOptions(execution),
            CameraSetup = camera,
            RecipeReleases = releases,
            PlcResultContracts = plc,
            RecipeActivations = activation,
            PreviewSessions = includePreview ? new PreviewSessionStoreOptions() : null,
            ManualInspections = includeManual ? new ManualInspectionStoreOptions() : null,
            ProductionAdmission = includeProductionAdmission ? new ProductionAdmissionStoreOptions() : null,
            StationQualifications = includeStationQualification ? new StationQualificationStoreOptions() : null,
            RecipeTransfers = new RecipeTransferStoreOptions
            {
                PortablePolicy = new RecipeTransferPortablePolicy("V138.G07.Portable", "1",
                    Array.Empty<RecipeTransferPortableContract>())
            },
            CommitTimeout = TimeSpan.FromSeconds(8),
            QueryTimeout = TimeSpan.FromSeconds(8),
            QueueCapacity = 16
        };
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
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

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string HashDatabase(string databasePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(databasePath)));

    private static void AssertAvailable(bool available, string reasonCode) =>
        Assert.True(available, reasonCode);
}
