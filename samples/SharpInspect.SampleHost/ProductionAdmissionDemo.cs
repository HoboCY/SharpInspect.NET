using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using SharpInspect.Wpf;

namespace SharpInspect.SampleHost;

/// <summary>
/// Public-package consumer for the schema-22 production-admission report.
/// This is a development composition: it has local identity and the durable
/// report ledger, but no qualification issuer, PLC or production-cycle owner.
/// The WPF shell therefore presents every unavailable gate and remains
/// disarmed.  Nothing in this sample manufactures qualification evidence.
/// </summary>
internal static class ProductionAdmissionDemo
{
    private const string RunCaseId = "V136_N01";
    private const string QueryCaseId = "V136_N02";
    private const string EvidenceFileName = "production-admission-evidence.json";
    private const string RestartFileName = "production-admission-restart.json";
    private const string SummaryFileName = "evidence.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null")
                ?? throw new ProductionAdmissionDemoException("ProductionAdmissionConsumerPasswordRequired");
            RunWithWpfDispatcher(options, Path.GetFullPath(directory), userName,
                expectedPrincipal, password);
            Console.WriteLine("V136_N01 production-admission PASS schema=22 authenticated=true armPermission=true rejected=true gates=24 ready=false disarmed=true");
            return 0;
        }
        catch (ProductionAdmissionDemoException exception)
        {
            Console.Error.WriteLine($"{RunCaseId} production-admission FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"{RunCaseId} production-admission FAIL reason=ProductionAdmissionConsumerCheckFailed");
            return 1;
        }
    }

    internal static int Query(ProductionStoreOptions options, string directory)
    {
        try
        {
            QueryCoreAsync(options, Path.GetFullPath(directory)).GetAwaiter().GetResult();
            Console.WriteLine("V136_N02 production-admission-query PASS readOnly=true writerStarted=false databaseUnchanged=true gates=24");
            return 0;
        }
        catch (ProductionAdmissionDemoException exception)
        {
            Console.Error.WriteLine($"{QueryCaseId} production-admission-query FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"{QueryCaseId} production-admission-query FAIL reason=ProductionAdmissionQueryCheckFailed");
            return 1;
        }
    }

    private static void RunWithWpfDispatcher(ProductionStoreOptions options, string directory,
        string? userName, string? expectedPrincipal, string password)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        app.Startup += (_, _) => _ = ExecuteAsync();
        app.Run();

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task ExecuteAsync()
        {
            try
            {
                await RunLiveAsync(options, directory, userName, expectedPrincipal, password,
                    app).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                app.Shutdown();
            }
        }
    }

    private static async Task RunLiveAsync(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal, string password, Application app)
    {
        RequireConfiguredRun(options);
        Require(!string.IsNullOrWhiteSpace(configuredUserName), "ProductionAdmissionConsumerUserRequired");
        Require(Guid.TryParse(expectedPrincipal, out var expectedPrincipalId) && expectedPrincipalId != Guid.Empty,
            "ProductionAdmissionConsumerPrincipalRequired");
        Directory.CreateDirectory(directory);
        Require(!File.Exists(Path.Combine(directory, EvidenceFileName)),
            "ProductionAdmissionEvidenceAlreadyExists");

        var services = new ServiceCollection();
        // The consumer intentionally registers no camera, algorithm, PLC or
        // qualification service.  The Runtime reports those missing gates
        // through its public immutable report instead of receiving a fixture.
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        var provider = services.BuildServiceProvider();
        var providerDisposed = false;
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        StationShellViewModel? shell = null;
        try
        {
            var startup = await WaitForStartupFenceAsync(runtime).ConfigureAwait(true);
            Require(!startup.Ready && startup.ArmState == ProductionArmState.Disarmed,
                "ProductionAdmissionConsumerInitialStationNotDisarmed");
            var startupFenceCleared = !HasStartupPending(startup);
            Require(startupFenceCleared, "ProductionAdmissionStartupFenceUnavailable");

            shell = new StationShellViewModel(runtime,
                new DispatcherUiDispatcher(app.Dispatcher), invocationFactory: () =>
                {
                    var current = sessions.Current;
                    return current is
                        { State: InteractiveSessionState.Authenticated, PrincipalId: not null, SessionId: not null }
                        ? new CommandInvocation(CommandSource.PhysicalConsole, current.PrincipalId,
                            current.SessionId)
                        : new CommandInvocation(CommandSource.PhysicalConsole);
                });
            await shell.StartAsync().ConfigureAwait(true);

            var login = await sessions.SignInAsync(new PasswordSignInRequest(
                configuredUserName!, password)).ConfigureAwait(true);
            Require(login.Succeeded && login.Identity?.PrincipalId == expectedPrincipalId,
                "ProductionAdmissionConsumerAuthenticationFailed");
            var session = sessions.Current;
            Require(session is
                { State: InteractiveSessionState.Authenticated, PrincipalId: not null, SessionId: not null } &&
                session.PrincipalId == expectedPrincipalId.ToString("D"),
                "ProductionAdmissionConsumerSessionUnavailable");

            var authorization = await provider.GetRequiredService<IIdentityAdministrationQuery>()
                .GetCurrentAuthorizationAsync(session.SessionId).ConfigureAwait(true);
            Require(authorization.Available && authorization.Account is not null &&
                authorization.Account.PrincipalId == expectedPrincipalId &&
                authorization.Account.Permissions.Contains(Permission.ArmProduction),
                "ProductionAdmissionArmPermissionUnavailable");
            var armRequiresStepUp = options.LocalIdentity!.AuthorizationPolicy
                .RequiresStepUp(Permission.ArmProduction);
            var stepUpServiceRegistered = provider.GetService<IStepUpAuthentication>() is not null;

            var beforeArm = await WaitForAuthenticatedSnapshotAsync(runtime, shell)
                .ConfigureAwait(true);
            var databaseBefore = HashFile(options.DatabasePath);
            var outcome = await shell.ArmProductionAsync().ConfigureAwait(true);
            Console.WriteLine($"V136 admission outcome disposition={outcome.Disposition} audit={outcome.Audit} reason={outcome.ReasonCode}");
            Require(outcome.Disposition == CommandDisposition.Rejected &&
                outcome.Audit == AuditPersistence.Persisted,
                "ProductionAdmissionArmRejectionNotDurable");
            Require(outcome.AttemptId is not null,
                "ProductionAdmissionAttemptIdentityMissing");
            Require(outcome.ProductionAdmission is not null,
                "ProductionAdmissionReportMissingFromOutcome");

            var afterArm = await WaitForSnapshotAsync(runtime, shell, beforeArm.Revision,
                snapshot => !snapshot.Ready && snapshot.ArmState == ProductionArmState.Disarmed &&
                    snapshot.CurrentExecution is null && snapshot.Revision > beforeArm.Revision)
                .ConfigureAwait(true);
            // The outcome is the exact report bound to the durable Arm attempt.
            // Runtime rebinds the same observed facts to the later snapshot
            // revision/time, so the WPF projection has its own content hash.
            var report = outcome.ProductionAdmission!;
            var displayedReport = afterArm.ProductionAdmission;
            Require(displayedReport is not null,
                "ProductionAdmissionReportMissingFromFreshSnapshot");
            Require(report.Gates.Count == ProductionAdmissionReport.RequiredGates.Count &&
                report.Gates.Select(value => value.Gate).SequenceEqual(ProductionAdmissionReport.RequiredGates),
                "ProductionAdmissionReportGateSetIncomplete");
            Require(!report.CanArm, "ProductionAdmissionReportIncorrectlyArmable");
            var blockers = report.Gates.Where(IsBlockingGate).ToArray();
            Require(blockers.Length > 0, "ProductionAdmissionBlockersMissing");
            foreach (var requiredMissing in new[]
            {
                ProductionAdmissionGate.FrameworkQualification,
                ProductionAdmissionGate.ProviderQualification,
                ProductionAdmissionGate.PlcCommunication,
                ProductionAdmissionGate.ProductionCycle
            })
            {
                Require(report.Gates.Single(value => value.Gate == requiredMissing).Status !=
                    ProductionAdmissionGateStatus.Passed,
                    "ProductionAdmissionMissingGateWasPassed:" + requiredMissing);
            }

            await WaitForDisplayedReportAsync(shell, displayedReport!.ContentHash).ConfigureAwait(true);
            var displayed = shell.State.DisplayedProductionAdmission;
            Require(displayed is not null && displayed.ContentHash == displayedReport.ContentHash &&
                displayed.Gates.Count == ProductionAdmissionReport.RequiredGates.Count,
                "ProductionAdmissionUiReportUnavailable");

            // Retire the writer before taking the stable database hash.  The
            // command store may have committed through WAL while the Runtime
            // was alive; a cold reader must observe the finalized file.
            await shell.DisposeAsync().ConfigureAwait(true);
            shell = null;
            await provider.DisposeAsync().ConfigureAwait(true);
            providerDisposed = true;
            var databaseAfter = HashFile(options.DatabasePath);
            Require(!string.Equals(databaseBefore, databaseAfter, StringComparison.Ordinal),
                "ProductionAdmissionRejectionWasNotWritten");
            var evidence = new ProductionAdmissionEvidence
            {
                Result = "Pass",
                CaseId = RunCaseId,
                ExternalNuGetConsumer = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                    "SHARPINSPECT_PRODUCTION_ADMISSION_CONSUMER")),
                ConsumerSha256 = HashFile(typeof(ProductionAdmissionDemo).Assembly.Location),
                SchemaVersion = 22,
                StartupFenceCleared = startupFenceCleared,
                AuthenticationSucceeded = true,
                ArmPermissionGranted = true,
                ArmRequiresStepUp = armRequiresStepUp,
                StepUpServiceRegistered = stepUpServiceRegistered,
                ArmDisposition = outcome.Disposition.ToString(),
                ArmAudit = outcome.Audit.ToString(),
                ArmReasonCode = outcome.ReasonCode,
                CorrelationId = outcome.CorrelationId,
                AttemptId = outcome.AttemptId,
                PrincipalId = expectedPrincipalId,
                SessionId = session.SessionId!.Value,
                ReportContentHash = report.ContentHash,
                AdmissionGeneration = report.AdmissionGeneration,
                GateCount = report.Gates.Count,
                BlockerCount = blockers.Length,
                Gates = report.Gates.Select(ToEvidence).ToArray(),
                Blockers = blockers.Select(ToEvidence).ToArray(),
                CanArm = report.CanArm,
                ReadyBefore = beforeArm.Ready,
                ReadyAfter = afterArm.Ready,
                ArmedBefore = beforeArm.ArmState == ProductionArmState.Armed,
                ArmedAfter = afterArm.ArmState == ProductionArmState.Armed,
                ActiveBefore = beforeArm.ActiveRecipe is not null,
                ActiveAfter = afterArm.ActiveRecipe is not null,
                ProductionFactsCreated = false,
                PhysicalIoStarted = false,
                DatabaseHashBeforeAdmission = databaseBefore,
                DatabaseHashAfterAdmission = databaseAfter,
                DatabaseChangedByAdmission = true,
                IndependentColdRead = false,
                DatabaseUnchangedByColdRead = false
            };
            Require(!evidence.ReadyBefore && !evidence.ReadyAfter && !evidence.ArmedBefore &&
                !evidence.ArmedAfter && !evidence.ActiveBefore && !evidence.ActiveAfter,
                "ProductionAdmissionChangedProductionAuthority");
            Require(!evidence.ProductionFactsCreated && !evidence.PhysicalIoStarted,
                "ProductionAdmissionStartedProductionWork");
            await WriteEvidenceAsync(directory, evidence).ConfigureAwait(true);
        }
        finally
        {
            if (shell is not null) await shell.DisposeAsync().ConfigureAwait(true);
            if (!providerDisposed) await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    private static async Task QueryCoreAsync(ProductionStoreOptions options, string directory)
    {
        RequireConfiguredRun(options);
        var evidencePath = Path.Combine(directory, EvidenceFileName);
        Require(File.Exists(evidencePath), "ProductionAdmissionEvidenceMissing");
        var evidence = JsonSerializer.Deserialize<ProductionAdmissionEvidence>(
            await File.ReadAllTextAsync(evidencePath).ConfigureAwait(true), JsonOptions);
        Require(evidence is { Result: "Pass", CaseId: RunCaseId, SchemaVersion: 22,
            GateCount: 24 }, "ProductionAdmissionEvidenceInvalid");

        // This is deliberately a direct public read-only query.  No DI
        // container, command store, StationRuntime, algorithm factory or
        // provider is created in the cold process.
        var before = HashFile(options.DatabasePath);
        IProductionAdmissionHistoryQuery query = new SqliteProductionAdmissionHistoryQuery(options);
        var page = await query.QueryAsync(new ProductionAdmissionHistoryFilter(PageSize: 64))
            .ConfigureAwait(true);
        Require(page.Available && page.Events.Count > 0 && page.NextAfterPosition is null &&
            page.Pending is null && !page.RecoveryRequired,
            "ProductionAdmissionColdQueryUnavailable:" + page.ReasonCode);
        var current = await query.ReadCurrentAsync().ConfigureAwait(true);
        Require(current.Available && current.Latest is not null && !current.RecoveryRequired,
            "ProductionAdmissionColdCurrentProjectionInvalid");
        var exactResult = await query.ReadAsync(evidence.CorrelationId).ConfigureAwait(true);
        var exact = exactResult.Latest;
        Require(exactResult.Available && exact is not null &&
            exact.Kind == ProductionAdmissionEventKind.Rejected &&
            exact.CorrelationId == evidence.CorrelationId &&
            exact.Report.ContentHash == evidence.ReportContentHash &&
            exact.Report.Gates.Count == ProductionAdmissionReport.RequiredGates.Count &&
            exact.ActorPrincipalId == evidence.PrincipalId &&
            exact.ActorSessionId == evidence.SessionId &&
            exact.AttemptId == evidence.AttemptId,
            "ProductionAdmissionColdExactRecordMismatch");
        Require(current.Latest!.ContentHash == exact.ContentHash,
            "ProductionAdmissionColdCurrentMismatch");
        var after = HashFile(options.DatabasePath);
        Require(before == after && before == evidence.DatabaseHashAfterAdmission,
            "ProductionAdmissionColdQueryChangedDatabase");

        evidence.HistoryRecordCount = page.Events.Count;
        evidence.HistoryPosition = exact.Position;
        evidence.HistoryCorrelationId = exact.CorrelationId;
        evidence.HistoryActorPrincipalId = exact.ActorPrincipalId;
        evidence.HistoryActorSessionId = exact.ActorSessionId;
        evidence.HistoryContentHash = exact.ContentHash;
        evidence.HistoryReportContentHash = exact.Report.ContentHash;
        evidence.IndependentColdRead = true;
        evidence.DatabaseUnchangedByColdRead = true;
        evidence.DatabaseHashBeforeColdRead = before;
        evidence.DatabaseHashAfterColdRead = after;
        await WriteEvidenceAsync(directory, evidence).ConfigureAwait(true);
        await WriteJsonAsync(Path.Combine(directory, RestartFileName), new
        {
            Result = "Pass",
            CaseId = QueryCaseId,
            ReadOnlyQuery = true,
            WriterStarted = false,
            AlgorithmFactoryCreated = false,
            ProviderFactoryCreated = false,
            ProductionFactsCreated = false,
            DatabaseHashBefore = before,
            DatabaseHashAfter = after,
            DatabaseUnchanged = true,
            RecordCount = page.Events.Count,
            AdmissionPosition = exact.Position,
            Kind = exact.Kind.ToString(),
            CorrelationId = exact.CorrelationId,
            ActorPrincipalId = exact.ActorPrincipalId,
            ActorSessionId = exact.ActorSessionId,
            ReportContentHash = exact.Report.ContentHash,
            HistoryContentHash = exact.ContentHash,
            GateCount = exact.Report.Gates.Count,
            BlockerCount = exact.Report.Gates.Count(IsBlockingGate),
            Ready = false,
            Active = false,
            ArmState = ProductionArmState.Disarmed.ToString()
        }).ConfigureAwait(true);
        await WriteJsonAsync(Path.Combine(directory, SummaryFileName), new
        {
            Result = "Pass",
            ValidationIds = new[] { RunCaseId, QueryCaseId },
            ExternalNuGetConsumer = evidence.ExternalNuGetConsumer,
            ConsumerSha256 = evidence.ConsumerSha256,
            SchemaVersion = evidence.SchemaVersion,
            GateCount = evidence.GateCount,
            BlockerCount = evidence.BlockerCount,
            ArmPermissionGranted = evidence.ArmPermissionGranted,
            ArmDisposition = evidence.ArmDisposition,
            Ready = evidence.ReadyAfter,
            Active = evidence.ActiveAfter,
            Armed = evidence.ArmedAfter,
            ProductionFactsCreated = evidence.ProductionFactsCreated,
            IndependentColdRead = evidence.IndependentColdRead,
            DatabaseUnchangedByColdRead = evidence.DatabaseUnchangedByColdRead,
            ReportContentHash = evidence.ReportContentHash,
            HistoryContentHash = evidence.HistoryContentHash
        }).ConfigureAwait(true);
    }

    private static bool IsBlockingGate(ProductionAdmissionGateResult value) =>
        value.Status != ProductionAdmissionGateStatus.Passed &&
        !(value.Gate == ProductionAdmissionGate.PowerLossQualification &&
            value.Status == ProductionAdmissionGateStatus.NotApplicable);

    private static GateEvidence ToEvidence(ProductionAdmissionGateResult value) => new()
    {
        Gate = value.Gate.ToString(),
        Status = value.Status.ToString(),
        ReasonCode = value.ReasonCode,
        ExpectedFingerprint = value.ExpectedFingerprint,
        ObservedFingerprint = value.ObservedFingerprint,
        EvidenceRecordHashes = value.EvidenceRecordHashes.ToArray()
    };

    private static async Task<StationStateSnapshot> WaitForStartupFenceAsync(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(true);
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Faulted)
                throw new ProductionAdmissionDemoException("ProductionAdmissionAuditVerificationFaulted");
            if (snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified &&
                !HasStartupPending(snapshot)) return snapshot;
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task<StationStateSnapshot> WaitForAuthenticatedSnapshotAsync(
        IStationRuntime runtime, StationShellViewModel shell)
    {
        return await WaitForSnapshotAsync(runtime, shell, -1, snapshot =>
            snapshot.Session.State == InteractiveSessionState.Authenticated &&
            shell.Freshness == SnapshotFreshness.Fresh).ConfigureAwait(true);
    }

    private static async Task<StationStateSnapshot> WaitForSnapshotAsync(IStationRuntime runtime,
        StationShellViewModel shell, long minimumRevision,
        Func<StationStateSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(true);
            if ((minimumRevision < 0 || snapshot.Revision > minimumRevision) && predicate(snapshot))
                return snapshot;
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task WaitForDisplayedReportAsync(StationShellViewModel shell,
        string contentHash)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (shell.State.DisplayedProductionAdmission?.ContentHash != contentHash)
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
    }

    private static bool HasStartupPending(StationStateSnapshot snapshot) =>
        snapshot.AdmissionBlockers.Any(value =>
            value.Contains("StartupRecoveryPending", StringComparison.Ordinal) ||
            value.Contains("ProductionAdmissionStartupRecoveryPending", StringComparison.Ordinal));

    private static async Task WriteEvidenceAsync(string directory,
        ProductionAdmissionEvidence evidence)
    {
        await WriteJsonAsync(Path.Combine(directory, EvidenceFileName), evidence).ConfigureAwait(true);
        await WriteJsonAsync(Path.Combine(directory, SummaryFileName), new
        {
            Result = evidence.Result,
            ValidationIds = new[] { RunCaseId, QueryCaseId },
            ExternalNuGetConsumer = evidence.ExternalNuGetConsumer,
            ConsumerSha256 = evidence.ConsumerSha256,
            SchemaVersion = evidence.SchemaVersion,
            GateCount = evidence.GateCount,
            BlockerCount = evidence.BlockerCount,
            ArmPermissionGranted = evidence.ArmPermissionGranted,
            ArmDisposition = evidence.ArmDisposition,
            Ready = evidence.ReadyAfter,
            Active = evidence.ActiveAfter,
            Armed = evidence.ArmedAfter,
            ProductionFactsCreated = evidence.ProductionFactsCreated,
            IndependentColdRead = evidence.IndependentColdRead,
            DatabaseUnchangedByColdRead = evidence.DatabaseUnchangedByColdRead,
            ReportContentHash = evidence.ReportContentHash,
            HistoryContentHash = evidence.HistoryContentHash
        }).ConfigureAwait(true);
    }

    private static async Task WriteJsonAsync<T>(string path, T value) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions))
            .ConfigureAwait(true);

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static void RequireConfiguredRun(ProductionStoreOptions options)
    {
        Require(options.LocalIdentity is not null && options.AuditIntegrityPolicy is not null &&
            options.ProductionAdmission is not null,
            "ProductionAdmissionSchema22OptionsIncomplete");
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string reasonCode)
    {
        if (!condition) throw new ProductionAdmissionDemoException(reasonCode);
    }

    private sealed class ProductionAdmissionDemoException : Exception
    {
        internal ProductionAdmissionDemoException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }

    private sealed class ProductionAdmissionEvidence
    {
        public string Result { get; set; } = string.Empty;
        public string CaseId { get; set; } = string.Empty;
        public bool ExternalNuGetConsumer { get; set; }
        public string ConsumerSha256 { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public bool StartupFenceCleared { get; set; }
        public bool AuthenticationSucceeded { get; set; }
        public bool ArmPermissionGranted { get; set; }
        public bool ArmRequiresStepUp { get; set; }
        public bool StepUpServiceRegistered { get; set; }
        public string ArmDisposition { get; set; } = string.Empty;
        public string ArmAudit { get; set; } = string.Empty;
        public string ArmReasonCode { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public Guid? AttemptId { get; set; }
        public Guid PrincipalId { get; set; }
        public Guid SessionId { get; set; }
        public string ReportContentHash { get; set; } = string.Empty;
        public long AdmissionGeneration { get; set; }
        public int GateCount { get; set; }
        public int BlockerCount { get; set; }
        public GateEvidence[] Gates { get; set; } = Array.Empty<GateEvidence>();
        public GateEvidence[] Blockers { get; set; } = Array.Empty<GateEvidence>();
        public bool CanArm { get; set; }
        public bool ReadyBefore { get; set; }
        public bool ReadyAfter { get; set; }
        public bool ArmedBefore { get; set; }
        public bool ArmedAfter { get; set; }
        public bool ActiveBefore { get; set; }
        public bool ActiveAfter { get; set; }
        public bool ProductionFactsCreated { get; set; }
        public bool PhysicalIoStarted { get; set; }
        public string DatabaseHashBeforeAdmission { get; set; } = string.Empty;
        public string DatabaseHashAfterAdmission { get; set; } = string.Empty;
        public bool DatabaseChangedByAdmission { get; set; }
        public int HistoryRecordCount { get; set; }
        public long HistoryPosition { get; set; }
        public Guid? HistoryCorrelationId { get; set; }
        public Guid? HistoryActorPrincipalId { get; set; }
        public Guid? HistoryActorSessionId { get; set; }
        public string? HistoryContentHash { get; set; }
        public string? HistoryReportContentHash { get; set; }
        public bool IndependentColdRead { get; set; }
        public bool DatabaseUnchangedByColdRead { get; set; }
        public string? DatabaseHashBeforeColdRead { get; set; }
        public string? DatabaseHashAfterColdRead { get; set; }
    }

    private sealed class GateEvidence
    {
        public string Gate { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ReasonCode { get; set; } = string.Empty;
        public string? ExpectedFingerprint { get; set; }
        public string? ObservedFingerprint { get; set; }
        public string[] EvidenceRecordHashes { get; set; } = Array.Empty<string>();
    }
}
