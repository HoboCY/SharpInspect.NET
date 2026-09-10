using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.SampleHost;

/// <summary>
/// Public-package consumer for the schema 20 calibration-import boundary. The
/// package is treated as untrusted input and is retained as a local candidate;
/// this demo never turns it into a published Profile or a production Recipe.
/// </summary>
internal static class CalibrationImportDemo
{
    internal const string CaseId = "V134_N01";
    internal const string PackageFileName = "calibration-export-package.bin";
    internal const string EvidenceFileName = "calibration-import-evidence.json";

    internal static int Run(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal)
    {
        try
        {
            var password = JsonSerializer.Deserialize<string>(Console.ReadLine() ?? "null")
                ?? throw new CalibrationImportCheckException("CalibrationImportConsumerPasswordRequired");
            RunCoreAsync(options, Path.GetFullPath(directory), configuredUserName,
                expectedPrincipal, password).GetAwaiter().GetResult();
            Console.WriteLine($"{CaseId} calibration-import PASS imported=true candidate=true " +
                "tampered=true retained=true ready=false active=false armed=false");
            return 0;
        }
        catch (CalibrationImportCheckException exception)
        {
            Console.Error.WriteLine($"{CaseId} calibration-import FAIL reason={exception.ReasonCode}");
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"{CaseId} calibration-import FAIL reason=CalibrationImportConsumerCheckFailed");
            Console.Error.WriteLine("exceptionType=" + exception.GetType().Name);
            return 1;
        }
    }

    private static async Task RunCoreAsync(ProductionStoreOptions options, string directory,
        string? configuredUserName, string? expectedPrincipal, string password)
    {
        RequireConfiguredSchema20(options);
        Require(!string.IsNullOrWhiteSpace(configuredUserName), "CalibrationImportConsumerUserRequired");
        Require(Guid.TryParse(expectedPrincipal, out var expectedPrincipalId) &&
            expectedPrincipalId != Guid.Empty, "CalibrationImportConsumerPrincipalRequired");
        Require(Directory.Exists(directory), "CalibrationImportConsumerDirectoryMissing");

        var packagePath = Path.Combine(directory, PackageFileName);
        Require(File.Exists(packagePath), "CalibrationImportPackageMissing");
        var packageBytes = await File.ReadAllBytesAsync(packagePath).ConfigureAwait(true);
        var package = new CalibrationExportPackage(packageBytes);

        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(options, TimeSpan.FromMilliseconds(100));
        await using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IStationRuntime>();
        var startup = await WaitForStartupAsync(runtime).ConfigureAwait(true);
        Require(startup.Lifecycle == RuntimeLifecycle.Running &&
            startup.AuditIntegrity?.State == AuditIntegrityState.Verified,
            "CalibrationImportStartupUnavailable");
        var initial = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!initial.Ready && initial.ArmState == ProductionArmState.Disarmed &&
            initial.ActiveRecipe is null, "CalibrationImportInitialStationNotDisarmed");

        var sessions = provider.GetRequiredService<IInteractiveSessionService>();
        var login = await sessions.SignInAsync(new PasswordSignInRequest(
            configuredUserName!, password)).ConfigureAwait(true);
        Require(login.Succeeded && login.Identity?.PrincipalId == expectedPrincipalId,
            "CalibrationImportConsumerAuthenticationFailed");
        var session = sessions.Current;
        Require(session.State == InteractiveSessionState.Authenticated &&
            session.SessionId is not null && session.PrincipalId == expectedPrincipalId.ToString("D"),
            "CalibrationImportConsumerSessionUnavailable");
        var invocation = new CommandInvocation(CommandSource.PhysicalConsole,
            session.PrincipalId, session.SessionId);

        var importRuntime = provider.GetRequiredService<ICalibrationImportRuntime>();
        var importQuery = provider.GetRequiredService<ICalibrationImportQuery>();
        var stepUp = provider.GetRequiredService<IStepUpAuthentication>();

        var importCommand = new ImportCalibrationPackageCommand(Guid.NewGuid(), invocation,
            package, "V134 retain untrusted calibration package");
        var authorizedInvocation = await AuthorizeImportAsync(stepUp, importCommand,
            invocation, password).ConfigureAwait(true);
        var imported = await importRuntime.ImportAsync(importCommand with
        {
            Invocation = authorizedInvocation
        }).ConfigureAwait(true);
        Require(imported.Outcome.Disposition == CommandDisposition.Accepted,
            "CalibrationImportRejected_" + imported.Outcome.ReasonCode);
        Require(imported.Outcome.Audit == AuditPersistence.Persisted,
            "CalibrationImportAuditNotPersisted");
        var candidate = imported.Record as ImportedCalibrationCandidate ??
            throw new CalibrationImportCheckException("CalibrationImportCandidateMissing");
        Require(candidate.PackageHash == package.ContentHash && candidate.PackageLength == package.Length,
            "CalibrationImportPackageBindingMismatch");
        Require(!candidate.CanPublish && !candidate.CanActivate,
            "CalibrationImportCandidateHasProductionAuthority");
        Require(candidate.SourcePackageId != Guid.Empty && candidate.SourceStationId.Length > 0 &&
            candidate.SourceManifestHash.Length == 64,
            "CalibrationImportSourceProvenanceMissing");

        var exact = await importQuery.ReadImportOperationAsync(importCommand.CorrelationId,
            authorizedInvocation).ConfigureAwait(true);
        Require(exact.Available && exact.Value is ImportedCalibrationCandidate exactCandidate &&
            exactCandidate.ContentHash == candidate.ContentHash &&
            exactCandidate.Reference == candidate.Reference &&
            exactCandidate.PackageHash == package.ContentHash,
            "CalibrationImportExactQueryMismatch");

        var tamperedBytes = package.GetBytes();
        tamperedBytes[^1] ^= 0x01;
        var tamperedPackage = new CalibrationExportPackage(tamperedBytes);
        var tamperedCommand = new ImportCalibrationPackageCommand(Guid.NewGuid(), invocation,
            tamperedPackage, "V134 reject tampered calibration package");
        var tamperedInvocation = await AuthorizeImportAsync(stepUp, tamperedCommand,
            invocation, password).ConfigureAwait(true);
        var tampered = await importRuntime.ImportAsync(tamperedCommand with
        {
            Invocation = tamperedInvocation
        }).ConfigureAwait(true);
        Require(tampered.Outcome.Disposition == CommandDisposition.Rejected,
            "CalibrationImportTamperAccepted");
        Require(tampered.Outcome.Audit == AuditPersistence.Persisted,
            "CalibrationImportTamperAuditNotPersisted");

        var retained = await importQuery.ReadImportOperationAsync(importCommand.CorrelationId,
            authorizedInvocation).ConfigureAwait(true);
        Require(retained.Available && retained.Value is ImportedCalibrationCandidate retainedCandidate &&
            retainedCandidate.ContentHash == candidate.ContentHash &&
            retainedCandidate.Reference == candidate.Reference,
            "CalibrationImportCandidateNotRetainedAfterTamper");
        var final = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!final.Ready && final.ArmState == ProductionArmState.Disarmed &&
            final.ActiveRecipe is null, "CalibrationImportConsumerGrantedProductionState");

        var externalNuGetConsumer = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("SHARPINSPECT_CALIBRATION_IMPORT_CONSUMER"));
        var evidence = new
        {
            Result = "Pass",
            CaseId,
            ExternalNuGetConsumer = externalNuGetConsumer,
            ExternalPackageReference = externalNuGetConsumer,
            NoProjectReferences = externalNuGetConsumer,
            ConsumerSha256 = HashFile(typeof(CalibrationImportDemo).Assembly.Location),
            PackageHash = package.ContentHash,
            PackageLength = package.Length,
            CandidateId = candidate.CandidateId,
            CandidateContentHash = candidate.ContentHash,
            CandidateReferenceHash = candidate.Reference.ContentHash,
            SourcePackageId = candidate.SourcePackageId,
            SourceStationId = candidate.SourceStationId,
            SourceManifestHash = candidate.SourceManifestHash,
            CandidateCanPublish = candidate.CanPublish,
            CandidateCanActivate = candidate.CanActivate,
            ImportDisposition = imported.Outcome.Disposition.ToString(),
            ImportReason = imported.Outcome.ReasonCode,
            TamperedDisposition = tampered.Outcome.Disposition.ToString(),
            TamperedReason = tampered.Outcome.ReasonCode,
            FirstCandidateRetained = retained.Value?.ContentHash == candidate.ContentHash,
            ExactQueryAvailable = exact.Available,
            StartupFenceVerified = true,
            Ready = final.Ready,
            Active = final.ActiveRecipe is not null,
            ArmState = final.ArmState.ToString(),
            Inspection = "NotRun",
            Algorithm = "NotRun",
            Plc = "NotRun"
        };
        await File.WriteAllTextAsync(Path.Combine(directory, EvidenceFileName),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(true);
    }

    private static async Task<CommandInvocation> AuthorizeImportAsync(
        IStepUpAuthentication stepUp, ImportCalibrationPackageCommand command,
        CommandInvocation invocation, string password)
    {
        var grant = await stepUp.ReauthenticateAsync(new StepUpRequest(Guid.NewGuid(), invocation,
            new StepUpBinding(Permission.PublishCalibration, command.CorrelationId,
                command.AuthorizationTarget, AuditedCommandKind.ImportCalibrationPackage), password))
            .ConfigureAwait(true);
        if (!grant.Succeeded || grant.GrantId is not { } grantId)
            throw new CalibrationImportCheckException("CalibrationImportStepUpFailed");
        return invocation with { StepUpGrantId = grantId };
    }

    private static async Task<StationStateSnapshot> WaitForStartupAsync(IStationRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var snapshot = await runtime.GetSnapshotAsync(timeout.Token).ConfigureAwait(true);
            if (snapshot.Lifecycle == RuntimeLifecycle.Running &&
                snapshot.AuditIntegrity?.State == AuditIntegrityState.Verified)
                return snapshot;
            if (snapshot.Lifecycle == RuntimeLifecycle.Stopped ||
                snapshot.AuditIntegrity?.State == AuditIntegrityState.Faulted)
                throw new CalibrationImportCheckException("CalibrationImportStartupUnavailable");
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static void RequireConfiguredSchema20(ProductionStoreOptions options)
    {
        Require(options.AuditIntegrityPolicy is not null && options.LocalIdentity is not null,
            "CalibrationImportIdentityOrAuditUnavailable");
        Require(options.PreviewSessions is not null && options.RecipeActivations is not null &&
            options.PlcResultContracts is not null && options.RecipeReleases is not null &&
            options.RecipeDrafts is not null && options.CameraSetup is not null &&
            options.CameraRecovery is not null && options.ImagingSetup is not null &&
            options.CalibrationSessions is not null && options.CalibrationGovernance is not null &&
            options.CalibrationImports is not null,
            "CalibrationImportSchema20OptionsIncomplete");
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string reason)
    {
        if (!condition) throw new CalibrationImportCheckException(reason);
    }

    private sealed class CalibrationImportCheckException : Exception
    {
        internal CalibrationImportCheckException(string reasonCode) => ReasonCode = reasonCode;
        internal string ReasonCode { get; }
    }
}
