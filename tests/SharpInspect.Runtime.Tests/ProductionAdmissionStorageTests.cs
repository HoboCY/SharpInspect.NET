using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Admission;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;
using Xunit.Sdk;

#pragma warning disable CA1416

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Schema-22 storage regression tests.  The first group starts a real rejected
/// Arm so the rows are produced by the authorization transaction; only the
/// isolated database is opened for the deliberate tamper.
/// </summary>
public sealed class ProductionAdmissionStorageTests
{
    [Theory]
    [InlineData("payload")]
    [InlineData("scalar")]
    [Trait("VerificationId", "V136_S01")]
    public async Task V136_S01_TamperedAdmissionProjectionIsRejectedByColdReaders(
        string tamperKind)
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync(
            requireStepUp: true);
        using var qualification = new ProductionAdmissionTestFixture();
        var command = station.Command();
        var source = qualification.Facts();
        var heads = await station.Store.ReadProductionAdmissionDurableHeadsAsync(
            CancellationToken.None);
        var facts = new ProductionAdmissionFacts(source.Configuration,
            source.Qualifications, source.RuntimeGates.Values.ToArray(), heads);
        var report = qualification.Evaluate(facts);

        var outcome = await station.Authorization.HandleProductionArmAsync(command,
            report.RuntimeEpoch, Guid.NewGuid(), report.AdmissionGeneration, facts, report,
            null, new StoreDeadline(TimeSpan.FromSeconds(3)), CancellationToken.None);
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("StepUpRequired", outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        await station.WaitVerifiedAsync();
        var healthy = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(healthy.Available, healthy.ReasonCode);
        Assert.Equal(ProductionAdmissionEventKind.Rejected, healthy.Latest!.Kind);
        Assert.True(healthy.Latest.AuditSequence > 0);
        await station.Store.DisposeAsync();

        TamperAdmissionProjection(station.Options.DatabasePath, tamperKind);
        var beforeRead = Fingerprint(station.Options.DatabasePath);

        var history = await new SqliteProductionAdmissionHistoryQuery(station.Options)
            .ReadAsync(command.CorrelationId);
        Assert.False(history.Available, history.ReasonCode);
        Assert.Contains("ProductionAdmission", history.ReasonCode,
            StringComparison.Ordinal);

        var page = await new SqliteProductionAdmissionHistoryQuery(station.Options)
            .QueryAsync(new ProductionAdmissionHistoryFilter(PageSize: 20));
        Assert.False(page.Available, page.ReasonCode);
        Assert.Contains("ProductionAdmission", page.ReasonCode,
            StringComparison.Ordinal);
        Assert.Empty(page.Events);

        var audit = await new SqliteAuditIntegrityQuery(station.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.Contains("ProductionAdmission", audit.ReasonCode,
            StringComparison.Ordinal);

        // Cold readers never repair or rewrite a replaced projection.
        Assert.Equal(beforeRead, Fingerprint(station.Options.DatabasePath));
    }

    [Theory]
    [InlineData("authorization-ref")]
    [InlineData("central")]
    [Trait("VerificationId", "V136_S02")]
    public async Task V136_S02_BrokenAdmissionReverseBindingIsRejectedByColdReaders(
        string tamperKind)
    {
        await using var station = await ProductionAdmissionArmFixture.CreateAsync(
            requireStepUp: true);
        using var qualification = new ProductionAdmissionTestFixture();
        var command = station.Command();
        var source = qualification.Facts();
        var heads = await station.Store.ReadProductionAdmissionDurableHeadsAsync(
            CancellationToken.None);
        var facts = new ProductionAdmissionFacts(source.Configuration,
            source.Qualifications, source.RuntimeGates.Values.ToArray(), heads);
        var report = qualification.Evaluate(facts);

        var outcome = await station.Authorization.HandleProductionArmAsync(command,
            report.RuntimeEpoch, Guid.NewGuid(), report.AdmissionGeneration, facts, report,
            null, new StoreDeadline(TimeSpan.FromSeconds(3)), CancellationToken.None);
        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, outcome.Audit);
        await station.WaitVerifiedAsync();
        var healthy = await station.History.ReadAsync(command.CorrelationId);
        Assert.True(healthy.Available, healthy.ReasonCode);
        Assert.Equal(ProductionAdmissionEventKind.Rejected, healthy.Latest!.Kind);
        Assert.True(healthy.Latest.AuditSequence > 0);
        await station.Store.DisposeAsync();

        TamperAdmissionProjection(station.Options.DatabasePath, tamperKind);
        var beforeRead = Fingerprint(station.Options.DatabasePath);

        var history = await new SqliteProductionAdmissionHistoryQuery(station.Options)
            .ReadCurrentAsync();
        Assert.False(history.Available, history.ReasonCode);
        Assert.NotEqual("ProductionAdmissionHistoryRead", history.ReasonCode);

        var audit = await new SqliteAuditIntegrityQuery(station.Options)
            .VerifyAsync(new AuditVerificationRequest(0, 200));
        Assert.Equal(AuditIntegrityState.Faulted, audit.State);
        Assert.NotEqual("AuditVerificationComplete", audit.ReasonCode);
        Assert.Equal(beforeRead, Fingerprint(station.Options.DatabasePath));
    }

    [Fact]
    [Trait("VerificationId", "V136_S03")]
    public async Task V136_S03_AdmissionPreflightRejectsWhenTotalBytesCannotReserveTerminal()
    {
        await using var station = await ByteBoundAdmissionFixture.CreateAsync();
        using var qualification = new ProductionAdmissionTestFixture();
        var command = station.Command();
        var source = qualification.Facts();
        var heads = await station.Store.ReadProductionAdmissionDurableHeadsAsync(
            CancellationToken.None);
        var facts = new ProductionAdmissionFacts(source.Configuration,
            source.Qualifications, source.RuntimeGates.Values.ToArray(), heads);
        var report = qualification.Evaluate(facts);
        var beforeIdentity = await station.Store.ReadIdentityAsync(CancellationToken.None);

        // MaximumTotalBytes equals one maximum payload.  An accepted Arm must
        // reserve both its admission row and its future terminal row, so this
        // is rejected before command/identity/ledger mutation.
        var outcome = await station.Authorization.HandleProductionArmAsync(command,
            report.RuntimeEpoch, Guid.NewGuid(), report.AdmissionGeneration, facts, report,
            null, new StoreDeadline(TimeSpan.FromSeconds(3)), CancellationToken.None);

        Assert.Equal(CommandDisposition.Rejected, outcome.Disposition);
        Assert.Equal("ProductionAdmissionTotalCapacityExceeded", outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Unavailable, outcome.Audit);
        var afterIdentity = await station.Store.ReadIdentityAsync(CancellationToken.None);
        Assert.Equal(beforeIdentity.Revision, afterIdentity.Revision);
        Assert.Equal(beforeIdentity.LastIdentityAuditHash, afterIdentity.LastIdentityAuditHash);
        var history = await station.History.QueryAsync(new ProductionAdmissionHistoryFilter());
        Assert.True(history.Available, history.ReasonCode);
        Assert.Empty(history.Events);
    }

    private static void TamperAdmissionProjection(string databasePath, string tamperKind)
    {
        var sql = tamperKind switch
        {
            "payload" => @"
                DROP TRIGGER production_admission_event_immutable_update;
                UPDATE production_admission_events
                SET Payload='dGFtcGVyZWQ='
                WHERE Position=1;
                CREATE TRIGGER production_admission_event_immutable_update
                BEFORE UPDATE ON production_admission_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableProductionAdmissionEvent');
                END;",
            "scalar" => @"
                DROP TRIGGER production_admission_event_immutable_update;
                UPDATE production_admission_events
                SET ReasonCode='ProductionAdmissionTamperedReason'
                WHERE Position=1;
                CREATE TRIGGER production_admission_event_immutable_update
                BEFORE UPDATE ON production_admission_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableProductionAdmissionEvent');
                END;",
            "authorization-ref" => @"
                DROP TRIGGER production_admission_event_immutable_update;
                UPDATE production_admission_events
                SET AuthorizationAuditSequence=AuthorizationAuditSequence+1
                WHERE Position=1;
                CREATE TRIGGER production_admission_event_immutable_update
                BEFORE UPDATE ON production_admission_events BEGIN
                    SELECT RAISE(ABORT,'ImmutableProductionAdmissionEvent');
                END;",
            "central" => @"
                DROP TRIGGER audit_entries_immutable_update;
                UPDATE audit_entries
                SET Payload='dGFtcGVyZWQ='
                WHERE Kind='ProductionAdmissionEvent'
                  AND ProductionAdmissionPosition=1;
                CREATE TRIGGER audit_entries_immutable_update
                BEFORE UPDATE ON audit_entries BEGIN
                    SELECT RAISE(ABORT,'ImmutableAuditEvidence');
                END;",
            _ => throw new ArgumentOutOfRangeException(nameof(tamperKind), tamperKind, null)
        };

        using var connection = SqliteNative.Open(databasePath, readOnly: false);
        SqliteNative.Execute(connection.Handle!, sql,
            new StoreDeadline(TimeSpan.FromSeconds(5)));
    }

    private static DatabaseFingerprint Fingerprint(string databasePath)
    {
        var info = new FileInfo(databasePath);
        using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        return new DatabaseFingerprint(info.Length, info.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(sha.ComputeHash(stream)));
    }

    private sealed record DatabaseFingerprint(long Length, long LastWriteTicks, string Hash);

    /// <summary>A tiny real DI host used only to exercise the byte reservation
    /// boundary; the production fixture intentionally exposes entry count only.</summary>
    private sealed class ByteBoundAdmissionFixture : IAsyncDisposable
    {
        private const string UserName = "admission-byte-admin";
        private const string Password = "V136 byte admission administrator secret!";
        private const string StationId = "V136ByteAdmissionStation";
        private ServiceProvider? _provider;

        private ByteBoundAdmissionFixture(ProductionStoreOptions options) => Options = options;

        internal ProductionStoreOptions Options { get; }
        internal SqliteCommandStore Store { get; private set; } = null!;
        internal LocalAuthorizationService Authorization { get; private set; } = null!;
        internal IProductionAdmissionHistoryQuery History { get; private set; } = null!;
        internal Guid PrincipalId { get; private set; }
        internal Guid SessionId { get; private set; }

        internal static async Task<ByteBoundAdmissionFixture> CreateAsync()
        {
            if (!OperatingSystem.IsWindows())
                throw SkipException.ForSkip("Production admission identity requires Windows DPAPI.");

            var directory = Path.Combine(Path.GetTempPath(), "SharpInspect.Runtime.Tests",
                "V136ByteAdmission", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var identity = new LocalIdentityOptions(StationId,
                new LocalPasswordPolicy
                {
                    Blocklist = PasswordBlocklist.Create("v136-byte-blocklist", "v1",
                        new[] { "known-compromised-value" })
                }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
                AuthorizationPolicy.Development);
            var options = new ProductionStoreOptions(Path.Combine(directory, "admission.sqlite"))
            {
                LocalIdentity = identity,
                AuditIntegrityPolicy = new AuditIntegrityPolicy(StationId, "v1",
                    "SharpInspect.Test.V136.Byte." + Guid.NewGuid().ToString("N"))
                {
                    AllowInitialKeyCreation = true,
                    KeyDirectory = Path.Combine(directory, "audit-keys"),
                    CheckpointEveryEntries = 2,
                    VerificationInterval = TimeSpan.FromSeconds(1)
                },
                ProductionAdmission = new ProductionAdmissionStoreOptions
                {
                    MaximumPayloadBytes = 64 * 1024,
                    MaximumTotalBytes = 64 * 1024
                },
                CommitTimeout = TimeSpan.FromSeconds(3),
                QueryTimeout = TimeSpan.FromSeconds(3),
                QueueCapacity = 8
            };
            var fixture = new ByteBoundAdmissionFixture(options);
            try
            {
                await fixture.InitializeAsync();
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal ArmProductionCommand Command() => new(Guid.NewGuid(),
            new CommandInvocation(CommandSource.PhysicalConsole,
                PrincipalId.ToString("D"), SessionId));

        internal async Task WaitVerifiedAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var report = Store.Integrity;
                if (report?.State == AuditIntegrityState.Verified) return;
                if (report?.State == AuditIntegrityState.Faulted)
                    throw new XunitException("Admission audit faulted: " + report.ReasonCode);
                await Task.Delay(25);
            }
            throw new XunitException("Admission audit verification timed out: " +
                Store.Integrity?.ReasonCode);
        }

        private async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddSharpInspectSqliteRuntime(Options, TimeSpan.FromMilliseconds(20));
            _provider = services.BuildServiceProvider();
            Store = _provider.GetRequiredService<SqliteCommandStore>();
            var initialized = await Store.Initialization.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(initialized.Committed, initialized.ReasonCode);
            await WaitVerifiedAsync();

            var bootstrap = new LocalIdentityService(Store, Options.LocalIdentity!,
                new TestConsole());
            var token = await bootstrap.ProvisionBootstrapTokenAsync();
            Assert.True(token.Succeeded, token.ReasonCode);
            var displayed = token.Token!.TakeForDisplay();
            await WaitVerifiedAsync();
            var created = await bootstrap.CreateFirstAdministratorAsync(
                new BootstrapAdministratorRequest(StationId, displayed, UserName,
                    "V136 Byte Administrator", Password));
            Assert.True(created.Succeeded, created.ReasonCode);
            created.RecoveryKit?.Dispose();
            await WaitVerifiedAsync();

            var sessions = _provider.GetRequiredService<IInteractiveSessionService>();
            Authorization = _provider.GetRequiredService<LocalAuthorizationService>();
            History = _provider.GetRequiredService<IProductionAdmissionHistoryQuery>();
            var signedIn = await sessions.SignInAsync(new PasswordSignInRequest(UserName,
                Password));
            Assert.True(signedIn.Succeeded, signedIn.ReasonCode);
            PrincipalId = signedIn.Identity!.PrincipalId;
            SessionId = signedIn.Session.SessionId!.Value;
            await WaitVerifiedAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_provider is not null) await _provider.DisposeAsync();
            if (Options.AuditIntegrityPolicy is not null)
            {
                var key = WindowsMachineAuditKey.GetKeyPath(Options.AuditIntegrityPolicy);
                try { if (File.Exists(key)) File.Delete(key); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private sealed class TestConsole : IPhysicalConsoleAuthority
        {
            public ConsoleAuthority Observe() => new(true, true,
                "S-1-5-21-V136-BYTE-TEST");
        }
    }
}

#pragma warning restore CA1416
