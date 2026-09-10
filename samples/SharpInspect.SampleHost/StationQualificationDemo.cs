using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Qualification;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// Independent package boundary check with no logged-in operator or activated
/// target. Positive physical recovery is covered by the controlled Runtime tests.
/// </summary>
internal static class StationQualificationDemo
{
    internal static int Run(string directory, bool queryOnly)
    {
        try
        {
            RunAsync(Path.GetFullPath(directory), queryOnly).GetAwaiter().GetResult();
            Console.WriteLine(queryOnly
                ? "V137_N02 station-qualification-query PASS readOnly=true databaseUnchanged=true"
                : "V137_N01 station-qualification PASS rejected=true facilityOpen=0 productionAuthority=false");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"StationQualificationConsumerCheckFailed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task RunAsync(string directory, bool queryOnly)
    {
        Directory.CreateDirectory(directory);
        var options = Options(directory);
        if (queryOnly)
        {
            Require(File.Exists(options.DatabasePath), "QualificationConsumerDatabaseMissing");
            var before = HashFile(options.DatabasePath);
            var query = new SqliteStationQualificationHistoryQuery(options);
            var current = await query.ReadCurrentAsync().ConfigureAwait(false);
            var page = await query.QueryAsync(new(PageSize: 20)).ConfigureAwait(false);
            Require(current.Available && page.Available && !current.RecoveryRequired &&
                current.Header is null && page.Events.Count == 0 && page.Runs.Count == 0,
                "QualificationConsumerUnexpectedHistory");
            Require(before == HashFile(options.DatabasePath), "QualificationConsumerReadChangedDatabase");
            await WriteEvidenceAsync(directory, "station-qualification-restart.json", new
            {
                CaseId = "V137_N02", Result = "Pass", ReadOnly = true, WriterStarted = false,
                DatabaseUnchanged = true, EventCount = page.Events.Count, RunCount = page.Runs.Count,
                Ready = false, ProductionAuthority = false, PhysicalHardwareQualification = "NotRun"
            }).ConfigureAwait(false);
            return;
        }

        Require(!File.Exists(options.DatabasePath), "QualificationConsumerRequiresFreshDirectory");
        var plan = Plan();
        var facility = new UnusedFacility(plan.QualificationHarnessIdentity);
        var services = new ServiceCollection();
        services.AddSharpInspectStationQualificationSessions(plan, facility);
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        RuntimeCommandOutcome outcome;
        StationStateSnapshot after;
        await using (var provider = services.BuildServiceProvider())
        {
            var runtime = provider.GetRequiredService<IStationRuntime>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var state = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(false);
                Require(state.AuditIntegrity?.State != AuditIntegrityState.Faulted,
                    state.AuditIntegrity?.ReasonCode ?? "QualificationConsumerAuditFaulted");
                if (state.AuditIntegrity?.State == AuditIntegrityState.Verified &&
                    !state.AdmissionBlockers.Contains("StationQualificationStartupRecoveryPending")) break;
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
            }
            var invocation = new CommandInvocation(CommandSource.PhysicalConsole);
            var session = provider.GetRequiredService<IStationQualificationSessionService>();
            var access = await session.GetAccessAsync(invocation, timeout.Token).ConfigureAwait(false);
            Require(!access.CanRun && access.RequiresStepUp, "QualificationConsumerAnonymousAccessGranted");
            outcome = await runtime.SubmitAsync(new StartStationQualificationSessionCommand(
                Guid.NewGuid(), invocation, plan, "V137 independent public consumer rejection"), timeout.Token)
                .ConfigureAwait(false);
            Require(outcome.Disposition == CommandDisposition.Rejected &&
                outcome.Audit == AuditPersistence.Persisted, outcome.ReasonCode);
            after = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(false);
            Require(!after.Ready && after.ArmState == ProductionArmState.Disarmed && facility.OpenCount == 0,
                "QualificationConsumerUnexpectedPhysicalAuthority");
            var history = await provider.GetRequiredService<IStationQualificationHistoryQuery>()
                .QueryAsync(new(PageSize: 20), timeout.Token).ConfigureAwait(false);
            Require(history.Available && history.Events.Count == 0 && history.Runs.Count == 0,
                "QualificationConsumerRejectedSessionPersisted");
        }
        await WriteEvidenceAsync(directory, "station-qualification-evidence.json", new
        {
            CaseId = "V137_N01", Result = "Pass", SchemaVersion = 23,
            ConsumerSha256 = HashFile(typeof(StationQualificationDemo).Assembly.Location),
            StartDisposition = outcome.Disposition.ToString(), Audit = outcome.Audit.ToString(),
            outcome.ReasonCode, outcome.CorrelationId, outcome.AttemptId,
            FacilityOpenCount = facility.OpenCount, SessionEventCount = 0, QualificationRunCount = 0,
            Ready = after.Ready, Armed = after.ArmState == ProductionArmState.Armed,
            ProductionAuthority = false, plan.QualificationContextHash,
            TargetActivationPresent = false, OperatorAuthenticated = false,
            PhysicalHardwareQualification = "NotRun", PositiveFacilityRecovery = "NotRun"
        }).ConfigureAwait(false);
    }

    private static ProductionStoreOptions Options(string directory)
    {
        var audit = new AuditIntegrityPolicy("SampleQualificationDevelopment", "development-v1",
            "SharpInspect.SampleQualification")
        {
            AllowInitialKeyCreation = true, KeyDirectory = Path.Combine(directory, "private-keys"),
            CheckpointEveryEntries = 2, VerificationInterval = TimeSpan.FromSeconds(1)
        };
        return new(Path.Combine(directory, "station-qualification.sqlite"))
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = new LocalIdentityOptions(audit.StationId, new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create("qualification-development", "1", new[] { "passwordpassword" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development, AuthorizationPolicy.Development),
            StationQualifications = new StationQualificationStoreOptions()
        };
    }

    private static StationQualificationPlan Plan() => new(
        new RecipeActivationReference(1, Guid.Parse("b22c439b-1c0a-4a63-98da-c04392b13b72"), Hash('9')),
        Hash('A'), Hash('B'), Hash('C'), Hash('D'),
        new QualificationHarnessIdentity("Sample.UnusedQualificationFacility", "1", Hash('E'), Hash('F'), true),
        new QualificationControllerConfiguration(Hash('1'), Hash('2'), new byte[] { 1 }),
        new QualificationControllerConfiguration(Hash('1'), Hash('2'), new byte[] { 2 }),
        new[]
        {
            new QualificationDestinationBinding(QualificationDestinationKind.ProductionPlcOutput, "plc", Hash('1')),
            new QualificationDestinationBinding(QualificationDestinationKind.OrdinaryOutbox, "outbox", Hash('2')),
            new QualificationDestinationBinding(QualificationDestinationKind.Mes, "mes", Hash('3')),
            new QualificationDestinationBinding(QualificationDestinationKind.Spc, "spc", Hash('4')),
            new QualificationDestinationBinding(QualificationDestinationKind.Yield, "yield", Hash('5'))
        }, new[] { "PublicBoundary.Rejection" });

    private sealed class UnusedFacility : IStationQualificationFacility
    {
        internal UnusedFacility(QualificationHarnessIdentity identity) => Identity = identity;
        public QualificationHarnessIdentity Identity { get; }
        internal int OpenCount { get; private set; }
        public ValueTask<IStationQualificationFacilityLease> OpenAsync(QualificationFacilityRequest request,
            CancellationToken cancellationToken = default)
        {
            OpenCount++;
            throw new InvalidOperationException("QualificationConsumerMustNotOpenFacility");
        }
    }

    private static string Hash(char value) => new(value, 64);
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private static Task WriteEvidenceAsync(string directory, string name, object value) =>
        File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));
}
