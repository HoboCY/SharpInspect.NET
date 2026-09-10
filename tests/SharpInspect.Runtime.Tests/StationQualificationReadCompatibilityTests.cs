using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class StationQualificationReadCompatibilityTests
{
    [Fact]
    public async Task V137_Q01_ExistingAuditAndCommandReadersVerifySchema23WithoutWriting()
    {
        var options = Options(qualification: true);
        await InitializeAsync(options);
        var before = Hash(options.DatabasePath);

        var integrity = await new SqliteAuditIntegrityQuery(options).VerifyAsync(new(0, 200));
        Assert.Equal(AuditIntegrityState.Verified, integrity.State);
        var trace = await new SqliteCommandTraceQuery(options).QueryAsync(new(PageSize: 20));
        Assert.Empty(trace.Records);
        var history = await new SqliteStationQualificationHistoryQuery(options).ReadCurrentAsync();
        Assert.True(history.Available, history.ReasonCode);
        Assert.Null(history.Header);
        Assert.False(history.RecoveryRequired);
        Assert.Equal(before, Hash(options.DatabasePath));
    }

    [Fact]
    public async Task V137_Q02_OmittingQualificationConfigurationCannotReadAroundItsLedger()
    {
        var options = Options(qualification: true);
        await InitializeAsync(options);
        var omitted = Copy(options, qualification: null);
        var before = Hash(options.DatabasePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(omitted).QueryAsync(new(PageSize: 20)).AsTask());
        Assert.Equal("StationQualificationConfigurationRequired", exception.Message);
        var audit = await new SqliteAuditIntegrityQuery(omitted).VerifyAsync(new(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Equal("StationQualificationConfigurationRequired", audit.ReasonCode);
        await using (var writer = new SqliteCommandStore(omitted))
        {
            var initialized = await writer.Initialization;
            Assert.False(initialized.Committed);
            Assert.Equal("StationQualificationConfigurationRequired", initialized.ReasonCode);
        }
        Assert.Equal(before, Hash(options.DatabasePath));
    }

    [Fact]
    public async Task V137_Q03_OptInDoesNotSilentlyMigrateExistingSchema22()
    {
        var options = Options(qualification: false);
        await InitializeAsync(options);
        var enabled = Copy(options, new StationQualificationStoreOptions());
        var before = Hash(options.DatabasePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(enabled).QueryAsync(new(PageSize: 20)).AsTask());
        Assert.Equal("StationQualificationGovernedMigrationRequired", exception.Message);
        var history = await new SqliteStationQualificationHistoryQuery(enabled).ReadCurrentAsync();
        Assert.False(history.Available);
        Assert.Equal("StationQualificationGovernedMigrationRequired", history.ReasonCode);
        await using (var writer = new SqliteCommandStore(enabled))
        {
            var initialized = await writer.Initialization;
            Assert.False(initialized.Committed);
            Assert.Equal("StationQualificationGovernedMigrationRequired", initialized.ReasonCode);
        }
        Assert.Equal(before, Hash(options.DatabasePath));
    }

    [Fact]
    public async Task V137_Q04_ChangedQualificationCapacityCannotReusePriorBinding()
    {
        var options = Options(qualification: true);
        await InitializeAsync(options);
        var changed = Copy(options, new StationQualificationStoreOptions { MaximumEntries = 9000 });
        var before = Hash(options.DatabasePath);

        var audit = await new SqliteAuditIntegrityQuery(changed).VerifyAsync(new(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        var history = await new SqliteStationQualificationHistoryQuery(changed).ReadCurrentAsync();
        Assert.False(history.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(changed).QueryAsync(new(PageSize: 20)).AsTask());
        Assert.Equal(before, Hash(options.DatabasePath));
    }

    [Theory]
    [InlineData("draft", "RecipeDraftConfigurationRequired")]
    [InlineData("camera", "CameraSetupConfigurationRequired")]
    public async Task V137_Q05_EmptyManualLedgerStillRequiresItsConfiguredDependencies(
        string omittedDependency, string expectedReason)
    {
        var basis = Options(qualification: true);
        var options = new ProductionStoreOptions(basis.DatabasePath)
        {
            AuditIntegrityPolicy = basis.AuditIntegrityPolicy, LocalIdentity = basis.LocalIdentity,
            StationQualifications = basis.StationQualifications,
            RecipeDrafts = new RecipeDraftStoreOptions(new AlgorithmExecutionPolicy(
                "V137.ReadCompatibility.Execution", "1", TimeSpan.FromMilliseconds(1),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1))),
            CameraSetup = new CameraSetupStoreOptions(), ManualInspections = new ManualInspectionStoreOptions()
        };
        await InitializeAsync(options);
        var before = Hash(options.DatabasePath);
        var valid = await new SqliteCommandTraceQuery(options).QueryAsync(new(PageSize: 20));
        Assert.Empty(valid.Records);
        var omitted = new ProductionStoreOptions(options.DatabasePath)
        {
            AuditIntegrityPolicy = options.AuditIntegrityPolicy, LocalIdentity = options.LocalIdentity,
            StationQualifications = options.StationQualifications, ManualInspections = options.ManualInspections,
            RecipeDrafts = omittedDependency == "draft" ? null : options.RecipeDrafts,
            CameraSetup = omittedDependency == "camera" ? null : options.CameraSetup
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqliteCommandTraceQuery(omitted).QueryAsync(new(PageSize: 20)).AsTask());
        Assert.Equal(expectedReason, exception.Message);
        Assert.Equal(before, Hash(options.DatabasePath));
    }

    private static async Task InitializeAsync(ProductionStoreOptions options)
    {
        await using var store = new SqliteCommandStore(options);
        var result = await store.Initialization;
        Assert.True(result.Committed, result.ReasonCode);
    }

    private static ProductionStoreOptions Options(bool qualification)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts",
            "ticket37", "reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audit = new AuditIntegrityPolicy("QualificationReadCompatibility", "development-v1",
            "SharpInspect.QualificationRead." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "private-keys")
        };
        return new(Path.Combine(directory, "trace.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("qualification-read", "1", new[] { "passwordpassword" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development),
            StationQualifications = qualification ? new StationQualificationStoreOptions() : null,
            ProductionAdmission = qualification ? null : new ProductionAdmissionStoreOptions()
        };
    }

    private static ProductionStoreOptions Copy(ProductionStoreOptions source,
        StationQualificationStoreOptions? qualification) => new(source.DatabasePath)
    {
        AuditIntegrityPolicy = source.AuditIntegrityPolicy, LocalIdentity = source.LocalIdentity,
        ProductionAdmission = source.ProductionAdmission, StationQualifications = qualification
    };

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
