using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Calibration.OpenCvSharp.Tests;
using SharpInspect.Runtime.Cameras;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// Process-boundary acceptance for the T24 calibration consumer.  The friend
/// test host creates the fresh schema-14 identity store with an isolated
/// console authority; the packaged child is exercised only through its public
/// run and restart modes, so the production Windows administrator gate is not
/// weakened by this test.
/// </summary>
public sealed partial class CalibrationConsumerAcceptanceTests
{
    private const int SchemaVersion = 14;
    private const string UserName = "v124-validation-admin";
    private const string CheckerboardFrozenManifestHash =
        "1329D4C19AD2F781E47599710A5D200831A33CEB54E30CD97D837CC3EB13CC16";
    private const string CheckerboardExtractionReceiptContractId =
        "sharpinspect.checkerboard-extraction-receipt";
    private const string CheckerboardExtractionReceiptContractVersion = "1";
    private const string PlanarFrozenManifestHash =
        "17C6A4410AB30C56A524E9995080E3AD29C8ED3B8D3F5020FDB134E6A91B03EC";
    private const string PlanarFixtureSourceHash =
        "C474D0EBABFAE730B02B0864376368665EC8ECFCBDD1B91551CBDBA4CBEB2976";
    private const string PlanarExtractionReceiptContractId =
        "sharpinspect.planar-extraction-receipt";
    private const string PlanarExtractionReceiptContractVersion = "1";

    [Fact]
    public async Task V124_N01_IndependentConsumerRunsCalibrationAndReadOnlyRestart()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the calibration consumer before running the process acceptance test.");

        var configuredEvidence = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT");
        var directory = Path.GetFullPath(configuredEvidence ?? Path.Combine(
            Path.GetTempPath(), "SharpInspect.NET-validation-artifacts", "ticket24-process",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "calibration-session.sqlite");
        Assert.False(File.Exists(databasePath),
            "Calibration acceptance requires a fresh evidence directory.");

        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CalibrationConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = CreateStoreOptions(directory, databasePath, audit);
        var password = "V124 isolated fixture " + Guid.NewGuid().ToString("N") + "!";
        Guid principal;
        var bootstrapLog = Path.Combine(directory, "calibration-bootstrap.json");
        var runLog = Path.Combine(directory, "calibration-run.json");
        var restartLog = Path.Combine(directory, "calibration-restart.json");

        try
        {
            principal = await BootstrapInFriendHostAsync(options, password)
                .ConfigureAwait(true);
            await WriteJsonAsync(bootstrapLog, new
            {
                result = "Pass",
                schema = SchemaVersion,
                stationId = audit.StationId,
                userName = UserName,
                principalId = principal,
                freshSchema14 = true,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun",
                production = "NotRun"
            }).ConfigureAwait(true);

            var common = new[]
            {
                "--mode", "run",
                "--directory", directory,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var consumerSha256 = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            var run = await RunProcessAsync(consumer, repository.FullName, common, password)
                .ConfigureAwait(true);
            Assert.DoesNotContain(password, run.Output, StringComparison.Ordinal);
            await WriteJsonAsync(runLog, new
            {
                mode = "run",
                exitCode = run.ExitCode,
                output = run.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("V124-N01 calibration-session-consumer PASS", run.Output);

            var runEvidencePath = Path.Combine(directory, "calibration-session-evidence.json");
            Assert.True(new FileInfo(runEvidencePath).Length > 0);
            using var runEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(runEvidencePath).ConfigureAwait(true));
            var runRoot = runEvidence.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("result").GetString());
            Assert.Equal(SchemaVersion, runRoot.GetProperty("schema").GetInt32());
            Assert.True(runRoot.GetProperty("fixture").GetProperty("developmentOnly").GetBoolean());
            Assert.False(runRoot.GetProperty("fixture").GetProperty("productionAuthority").GetBoolean());
            Assert.True(runRoot.GetProperty("start").GetProperty("accepted").GetBoolean());
            Assert.True(runRoot.GetProperty("start").GetProperty("acceptedBeforeTerminal").GetBoolean());
            Assert.True(runRoot.GetProperty("exit").GetProperty("restorationVerified").GetBoolean());
            Assert.False(runRoot.GetProperty("global").GetProperty("ready").GetBoolean());
            Assert.Equal("NotRun", runRoot.GetProperty("global")
                .GetProperty("physicalHardwareQualification").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("global")
                .GetProperty("stationAcceptance").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("global")
                .GetProperty("production").GetString());

            var afterExit = runRoot.GetProperty("evidenceAfterExit");
            Assert.Equal(3, afterExit.GetProperty("frameCount").GetInt32());
            Assert.Equal(3, afterExit.GetProperty("observationCount").GetInt32());
            Assert.Equal(1, afterExit.GetProperty("exclusionCount").GetInt32());
            var candidate = runRoot.GetProperty("candidate");
            var candidateHash = candidate.GetProperty("contentHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(candidateHash));
            Assert.True(candidate.GetProperty("developmentOnly").GetBoolean());
            Assert.False(candidate.GetProperty("canPublish").GetBoolean());
            Assert.False(candidate.GetProperty("canActivate").GetBoolean());
            Assert.True(runRoot.GetProperty("rawFrameRead").GetProperty("tightStride").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(runRoot.GetProperty("start")
                .GetProperty("authorizationTarget").GetString()));

            var screenshots = runRoot.GetProperty("screenshots");
            Assert.True(screenshots.GetArrayLength() >= 1);
            foreach (var screenshot in screenshots.EnumerateArray())
            {
                var name = screenshot.GetString();
                Assert.NotNull(name);
                Assert.NotEmpty(name!);
                var screenshotPath = Path.GetFullPath(Path.Combine(directory, name!));
                Assert.StartsWith(directory + Path.DirectorySeparatorChar, screenshotPath,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(new FileInfo(screenshotPath).Length > 0);
            }

            var restartArguments = new[]
            {
                "--mode", "restart",
                "--directory", directory,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var restart = await RunProcessAsync(consumer, repository.FullName,
                restartArguments, password).ConfigureAwait(true);
            Assert.DoesNotContain(password, restart.Output, StringComparison.Ordinal);
            await WriteJsonAsync(restartLog, new
            {
                mode = "restart",
                exitCode = restart.ExitCode,
                output = restart.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(restart.ExitCode == 0, restart.Output);
            Assert.Contains("V124-N02 calibration-session-restart PASS", restart.Output);

            var restartEvidencePath = Path.Combine(directory, "calibration-session-restart.json");
            Assert.True(new FileInfo(restartEvidencePath).Length > 0);
            using var restartEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(restartEvidencePath).ConfigureAwait(true));
            var restartRoot = restartEvidence.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("result").GetString());
            Assert.Equal(SchemaVersion, restartRoot.GetProperty("schema").GetInt32());
            Assert.Equal(runRoot.GetProperty("sessionId").GetString(),
                restartRoot.GetProperty("sessionId").GetString());
            Assert.Equal(candidateHash, restartRoot.GetProperty("candidateHash").GetString());
            Assert.True(restartRoot.GetProperty("readOnlyQueryDatabaseUnchanged").GetBoolean());
            Assert.Equal(0, restartRoot.GetProperty("openedDevices").GetInt32());
            Assert.False(restartRoot.GetProperty("ready").GetBoolean());
            Assert.True(restartRoot.GetProperty("productionOutputsAbsent").GetBoolean());
            Assert.Equal("NotRun", restartRoot.GetProperty("physicalHardwareQualification").GetString());
            Assert.Equal("NotRun", restartRoot.GetProperty("stationAcceptance").GetString());
            Assert.Equal("NotRun", restartRoot.GetProperty("production").GetString());

            var hash = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            Assert.Equal(consumerSha256, hash);
            Assert.Equal(64, hash.Length);
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task V125_N01_IndependentConsumerRunsCheckerboardCalibrationAndReadOnlyRestart()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the calibration consumer before running the process acceptance test.");

        var configuredEvidence = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT");
        var directory = Path.GetFullPath(configuredEvidence is { Length: > 0 }
            ? Path.Combine(configuredEvidence, "checkerboard")
            : Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts",
                "ticket25-process", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var images = Path.Combine(directory, "checkerboard-images");
        var imageSet = await PrepareCheckerboardImagesAsync(repository, images)
            .ConfigureAwait(true);
        var databasePath = Path.Combine(directory, "calibration-session.sqlite");
        Assert.False(File.Exists(databasePath),
            "Calibration acceptance requires a fresh evidence directory.");

        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CalibrationConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = CreateStoreOptions(directory, databasePath, audit,
            maximumFrames: SyntheticCheckerboardFixture.Poses.Count + 4);
        var password = "V125 isolated fixture " + Guid.NewGuid().ToString("N") + "!";
        Guid principal;
        var runLog = Path.Combine(directory, "calibration-run.json");
        var restartLog = Path.Combine(directory, "calibration-restart.json");
        try
        {
            principal = await BootstrapInFriendHostAsync(options, password)
                .ConfigureAwait(true);

            var common = new[]
            {
                "--mode", "run",
                "--directory", directory,
                "--checkerboard-images", images,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var consumerSha256 = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            var run = await RunProcessAsync(consumer, repository.FullName, common, password)
                .ConfigureAwait(true);
            Assert.DoesNotContain(password, run.Output, StringComparison.Ordinal);
            await WriteJsonAsync(runLog, new
            {
                mode = "run",
                exitCode = run.ExitCode,
                output = run.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("V125-N01 checkerboard-calibration-consumer PASS", run.Output);

            var runEvidencePath = Path.Combine(directory, "calibration-session-evidence.json");
            Assert.True(new FileInfo(runEvidencePath).Length > 0);
            using var runEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(runEvidencePath).ConfigureAwait(true));
            var runRoot = runEvidence.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("result").GetString());
            Assert.Equal(SchemaVersion, runRoot.GetProperty("schema").GetInt32());
            Assert.Equal("checkerboard", runRoot.GetProperty("calibrationMode").GetString());

            var checkerboard = runRoot.GetProperty("checkerboard");
            Assert.Equal("checkerboard-v1", checkerboard.GetProperty("datasetVersion").GetString());
            Assert.Equal(CheckerboardFrozenManifestHash,
                checkerboard.GetProperty("frozenManifestHash").GetString());
            Assert.Equal(imageSet.ManifestHash,
                checkerboard.GetProperty("imageManifestHash").GetString());
            Assert.True(checkerboard.GetProperty("pathsContained").GetBoolean());
            Assert.True(checkerboard.GetProperty("manifestBound").GetBoolean());
            Assert.Equal(20, checkerboard.GetProperty("imageCount").GetInt32());
            Assert.Equal(24, runRoot.GetProperty("store")
                .GetProperty("maximumFramesPerSession").GetInt32());
            Assert.Equal(20, runRoot.GetProperty("extractionReceiptCount").GetInt32());
            var runReceipts = runRoot.GetProperty("extractionReceipts")
                .EnumerateArray().ToArray();
            Assert.Equal(20, runReceipts.Length);
            Assert.Equal(20, runReceipts.Select(item => item.GetProperty("frameId")
                .GetString()).Distinct(StringComparer.Ordinal).Count());
            foreach (var receipt in runReceipts)
            {
                Assert.Equal(32, receipt.GetProperty("length").GetInt32());
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    receipt.GetProperty("receiptContentHash").GetString() ?? string.Empty);
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    receipt.GetProperty("receiptCanonicalBytesHash").GetString() ?? string.Empty);
                var format = receipt.GetProperty("format");
                Assert.Equal(CheckerboardExtractionReceiptContractId,
                    format.GetProperty("id").GetString());
                Assert.Equal(CheckerboardExtractionReceiptContractVersion,
                    format.GetProperty("version").GetString());
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    format.GetProperty("contentHash").GetString() ?? string.Empty);
            }

            var input = runRoot.GetProperty("checkerboardInput");
            Assert.Equal(9, input.GetProperty("innerColumns").GetInt32());
            Assert.Equal(6, input.GetProperty("innerRows").GetInt32());
            Assert.Equal(25, input.GetProperty("squareSizeMillimeters").GetDouble());
            Assert.Equal("TopCamera", input.GetProperty("logicalCameraRole").GetString());
            Assert.Equal(640, input.GetProperty("effectiveConfiguration")
                .GetProperty("regionOfInterest").GetProperty("width").GetInt32());
            Assert.Equal(480, input.GetProperty("effectiveConfiguration")
                .GetProperty("regionOfInterest").GetProperty("height").GetInt32());
            Assert.Equal("Mono8", input.GetProperty("effectiveConfiguration")
                .GetProperty("pixelFormat").GetString());

            var procedure = runRoot.GetProperty("procedure");
            Assert.Equal("sharpinspect.checkerboard-intrinsics",
                procedure.GetProperty("id").GetString());
            Assert.Equal("1", procedure.GetProperty("version").GetString());
            Assert.Equal(64, procedure.GetProperty("contentHash").GetString()!.Length);

            var afterExit = runRoot.GetProperty("evidenceAfterExit");
            Assert.Equal(20, afterExit.GetProperty("frameCount").GetInt32());
            Assert.Equal(20, afterExit.GetProperty("observationCount").GetInt32());
            Assert.Equal(1, afterExit.GetProperty("exclusionCount").GetInt32());
            Assert.Equal(19, afterExit.GetProperty("selection")
                .GetProperty("includedFrameCount").GetInt32());
            Assert.Equal(19, afterExit.GetProperty("selection")
                .GetProperty("sufficientFeatureFrameCount").GetInt32());

            var candidate = runRoot.GetProperty("candidate");
            var candidateHash = candidate.GetProperty("contentHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(candidateHash));
            Assert.True(candidate.GetProperty("developmentOnly").GetBoolean());
            Assert.False(candidate.GetProperty("canPublish").GetBoolean());
            Assert.False(candidate.GetProperty("canActivate").GetBoolean());
            Assert.True(candidate.GetProperty("immutable").GetBoolean());
            Assert.Equal("CannotPublish", candidate.GetProperty("publication").GetString());
            Assert.True(candidate.GetProperty("typedCoefficientsDecoded").GetBoolean());
            Assert.True(candidate.GetProperty("typedEvidenceDecoded").GetBoolean());
            Assert.Equal(19, candidate.GetProperty("decodedViewCount").GetInt32());
            Assert.Equal(1026, candidate.GetProperty("decodedPointCount").GetInt32());
            Assert.True(candidate.GetProperty("evidencePayloadBytes").GetInt32() <=
                CalibrationComputationEvidencePayload.MaximumBytes);
            var candidateEvidenceHash = candidate.GetProperty("evidenceContentHash").GetString();
            Assert.Equal(64, candidateEvidenceHash!.Length);

            var identities = runRoot.GetProperty("rawFrameIdentities").EnumerateArray().ToArray();
            Assert.Equal(20, identities.Length);
            Assert.Equal(20, identities.Select(item => item.GetProperty("frameId").GetString())
                .Distinct(StringComparer.Ordinal).Count());
            Assert.All(identities, item =>
            {
                Assert.Equal(64, item.GetProperty("sourceHash").GetString()!.Length);
                Assert.Equal(64, item.GetProperty("pixelHash").GetString()!.Length);
                Assert.Equal(640, item.GetProperty("width").GetInt32());
                Assert.Equal(480, item.GetProperty("height").GetInt32());
            });

            var wpf = runRoot.GetProperty("wpfSummary");
            Assert.True(wpf.GetProperty("rendered").GetBoolean());
            Assert.True(wpf.GetProperty("framesReadOnly").GetBoolean());
            Assert.True(wpf.GetProperty("observationsReadOnly").GetBoolean());
            Assert.True(wpf.GetProperty("passwordEmpty").GetBoolean());
            Assert.True(wpf.GetProperty("coordinateEditingUnavailable").GetBoolean());
            var screenshots = runRoot.GetProperty("screenshots").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToArray();
            Assert.Contains("calibration-session-candidate.png", screenshots);
            Assert.True(new FileInfo(Path.Combine(directory,
                "calibration-session-candidate.png")).Length > 0);

            var restartArguments = new[]
            {
                "--mode", "restart",
                "--directory", directory,
                "--checkerboard-images", images,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var restart = await RunProcessAsync(consumer, repository.FullName,
                restartArguments, password).ConfigureAwait(true);
            Assert.DoesNotContain(password, restart.Output, StringComparison.Ordinal);
            await WriteJsonAsync(restartLog, new
            {
                mode = "restart",
                exitCode = restart.ExitCode,
                output = restart.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(restart.ExitCode == 0, restart.Output);
            Assert.Contains("V125-N02 checkerboard-calibration-restart PASS", restart.Output);

            var restartEvidencePath = Path.Combine(directory, "calibration-session-restart.json");
            Assert.True(new FileInfo(restartEvidencePath).Length > 0);
            using var restartEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(restartEvidencePath).ConfigureAwait(true));
            var restartRoot = restartEvidence.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("result").GetString());
            Assert.Equal("checkerboard", restartRoot.GetProperty("calibrationMode").GetString());
            Assert.Equal(runRoot.GetProperty("sessionId").GetString(),
                restartRoot.GetProperty("sessionId").GetString());
            Assert.Equal(candidateHash, restartRoot.GetProperty("candidateHash").GetString());
            Assert.Equal(candidateEvidenceHash,
                restartRoot.GetProperty("candidateEvidenceHash").GetString());
            Assert.True(restartRoot.GetProperty("readOnlyQueryDatabaseUnchanged").GetBoolean());
            Assert.Equal(20, restartRoot.GetProperty("frameCount").GetInt32());
            Assert.Equal(19, restartRoot.GetProperty("validViewCount").GetInt32());
            Assert.Equal(1026, restartRoot.GetProperty("validPointCount").GetInt32());
            Assert.Equal(20, restartRoot.GetProperty("extractionReceiptCount").GetInt32());
            var retainedReceipts = restartRoot.GetProperty("retainedExtractionReceipts")
                .EnumerateArray().ToArray();
            Assert.Equal(20, retainedReceipts.Length);
            var runReceiptsByFrame = runReceipts.ToDictionary(item =>
                item.GetProperty("frameId").GetString()!, StringComparer.Ordinal);
            var retainedReceiptsByFrame = retainedReceipts.ToDictionary(item =>
                item.GetProperty("frameId").GetString()!, StringComparer.Ordinal);
            Assert.Equal(runReceiptsByFrame.Keys.OrderBy(value => value, StringComparer.Ordinal),
                retainedReceiptsByFrame.Keys.OrderBy(value => value, StringComparer.Ordinal));
            foreach (var pair in runReceiptsByFrame)
            {
                var retained = retainedReceiptsByFrame[pair.Key];
                Assert.Equal(pair.Value.GetProperty("receiptContentHash").GetString(),
                    retained.GetProperty("receiptContentHash").GetString());
                Assert.Equal(pair.Value.GetProperty("receiptCanonicalBytesHash").GetString(),
                    retained.GetProperty("receiptCanonicalBytesHash").GetString());
                Assert.Equal(pair.Value.GetProperty("length").GetInt32(),
                    retained.GetProperty("length").GetInt32());
                Assert.Equal(pair.Value.GetProperty("format").GetRawText(),
                    retained.GetProperty("format").GetRawText());
            }
            Assert.Equal(0, restartRoot.GetProperty("openedDevices").GetInt32());
            Assert.False(restartRoot.GetProperty("ready").GetBoolean());
            Assert.True(restartRoot.GetProperty("productionOutputsAbsent").GetBoolean());

            var hash = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            Assert.Equal(consumerSha256, hash);
            Assert.Equal(64, hash.Length);
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task V126_N01_IndependentConsumerRunsPlanarCalibrationAndReadOnlyRestart()
    {
        var repository = FindRepositoryRoot();
        var configuredConsumer = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_CONSUMER");
        var consumer = ResolveConsumer(repository, configuredConsumer);
        Assert.True(File.Exists(consumer),
            "Build the calibration consumer before running the process acceptance test.");

        var configuredEvidence = Environment.GetEnvironmentVariable(
            "SHARPINSPECT_CALIBRATION_EVIDENCE_ROOT");
        var directory = Path.GetFullPath(configuredEvidence is { Length: > 0 }
            ? Path.Combine(configuredEvidence, "planar")
            : Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts",
                "ticket26-process", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var images = Path.Combine(directory, "planar-images");
        var imageSet = await PreparePlanarImagesAsync(repository, images)
            .ConfigureAwait(true);
        var databasePath = Path.Combine(directory, "calibration-session.sqlite");
        Assert.False(File.Exists(databasePath),
            "Calibration acceptance requires a fresh evidence directory.");

        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CalibrationConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = CreateStoreOptions(directory, databasePath, audit,
            maximumFrames: 6);
        var password = "V126 isolated fixture " + Guid.NewGuid().ToString("N") + "!";
        Guid principal;
        var runLog = Path.Combine(directory, "calibration-run.json");
        var restartLog = Path.Combine(directory, "calibration-restart.json");
        try
        {
            principal = await BootstrapInFriendHostAsync(options, password)
                .ConfigureAwait(true);

            var common = new[]
            {
                "--mode", "run",
                "--directory", directory,
                "--planar-images", images,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var consumerSha256 = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            var run = await RunProcessAsync(consumer, repository.FullName, common, password)
                .ConfigureAwait(true);
            Assert.DoesNotContain(password, run.Output, StringComparison.Ordinal);
            await WriteJsonAsync(runLog, new
            {
                mode = "run",
                exitCode = run.ExitCode,
                output = run.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("V126-N01 planar-homography-consumer PASS", run.Output);

            var runEvidencePath = Path.Combine(directory, "calibration-session-evidence.json");
            Assert.True(new FileInfo(runEvidencePath).Length > 0);
            using var runEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(runEvidencePath).ConfigureAwait(true));
            var runRoot = runEvidence.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("result").GetString());
            Assert.Equal(SchemaVersion, runRoot.GetProperty("schema").GetInt32());
            Assert.Equal("planar", runRoot.GetProperty("calibrationMode").GetString());

            var planar = runRoot.GetProperty("planar");
            Assert.Equal("planar-v1", planar.GetProperty("datasetVersion").GetString());
            Assert.Equal(PlanarFrozenManifestHash,
                planar.GetProperty("frozenManifestHash").GetString());
            Assert.Equal(imageSet.ManifestHash,
                planar.GetProperty("imageManifestHash").GetString());
            Assert.True(planar.GetProperty("pathsContained").GetBoolean());
            Assert.True(planar.GetProperty("manifestBound").GetBoolean());
            Assert.Equal(2, planar.GetProperty("imageCount").GetInt32());
            Assert.Equal(6, runRoot.GetProperty("store")
                .GetProperty("maximumFramesPerSession").GetInt32());

            Assert.Equal(2, runRoot.GetProperty("extractionReceiptCount").GetInt32());
            var runReceipts = runRoot.GetProperty("extractionReceipts")
                .EnumerateArray().ToArray();
            Assert.Equal(2, runReceipts.Length);
            Assert.Equal(2, runReceipts.Select(item => item.GetProperty("frameId")
                .GetString()).Distinct(StringComparer.Ordinal).Count());
            foreach (var receipt in runReceipts)
            {
                Assert.Equal(32, receipt.GetProperty("length").GetInt32());
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    receipt.GetProperty("receiptContentHash").GetString() ?? string.Empty);
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    receipt.GetProperty("receiptCanonicalBytesHash").GetString() ?? string.Empty);
                var format = receipt.GetProperty("format");
                Assert.Equal(PlanarExtractionReceiptContractId,
                    format.GetProperty("id").GetString());
                Assert.Equal(PlanarExtractionReceiptContractVersion,
                    format.GetProperty("version").GetString());
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    format.GetProperty("contentHash").GetString() ?? string.Empty);
            }

            var input = runRoot.GetProperty("planarInput");
            Assert.Equal("TopCamera", input.GetProperty("logicalCameraRole").GetString());
            Assert.Equal("RawRoiPixelCentersCorrectionDeclaredNotRequired",
                input.GetProperty("pixelDomain").GetString());
            Assert.Equal("planar-target-v1",
                input.GetProperty("targetDefinitionId").GetString());
            Assert.Equal("plane-frame-v1",
                input.GetProperty("planeCoordinateFrameId").GetString());
            Assert.Equal("Millimeter", input.GetProperty("physicalUnit").GetString());
            Assert.Equal("DICT_4X4_50", input.GetProperty("dictionaryName").GetString());
            Assert.Equal(1, input.GetProperty("borderBits").GetInt32());
            Assert.Equal(640, input.GetProperty("effectiveConfiguration")
                .GetProperty("regionOfInterest").GetProperty("width").GetInt32());
            Assert.Equal(480, input.GetProperty("effectiveConfiguration")
                .GetProperty("regionOfInterest").GetProperty("height").GetInt32());
            Assert.Equal("Mono8", input.GetProperty("effectiveConfiguration")
                .GetProperty("pixelFormat").GetString());
            var markers = input.GetProperty("markers").EnumerateArray().ToArray();
            Assert.Equal(4, markers.Length);
            Assert.Equal(new[] { 0, 1, 2, 3 }, markers.Select(item =>
                item.GetProperty("markerId").GetInt32()).ToArray());
            Assert.All(markers, marker => Assert.Equal(4,
                marker.GetProperty("physicalCornersMillimeters").GetArrayLength()));

            var procedure = runRoot.GetProperty("procedure");
            Assert.Equal("sharpinspect.planar-homography",
                procedure.GetProperty("id").GetString());
            Assert.Equal("1", procedure.GetProperty("version").GetString());
            Assert.Matches("^[0-9A-Fa-f]{64}$",
                procedure.GetProperty("contentHash").GetString() ?? string.Empty);

            var afterExit = runRoot.GetProperty("evidenceAfterExit");
            Assert.Equal(2, afterExit.GetProperty("frameCount").GetInt32());
            Assert.Equal(2, afterExit.GetProperty("observationCount").GetInt32());
            Assert.Equal(1, afterExit.GetProperty("exclusionCount").GetInt32());
            Assert.Equal(1, afterExit.GetProperty("selection")
                .GetProperty("includedFrameCount").GetInt32());
            Assert.Equal(1, afterExit.GetProperty("selection")
                .GetProperty("sufficientFeatureFrameCount").GetInt32());
            var exclusions = afterExit.GetProperty("exclusions").EnumerateArray().ToArray();
            Assert.Single(exclusions);
            Assert.Contains("空白", exclusions[0].GetProperty("reason").GetString()!);
            var observations = afterExit.GetProperty("observations").EnumerateArray().ToArray();
            Assert.Equal(2, observations.Length);
            Assert.Contains(observations, item =>
                item.GetProperty("features").GetArrayLength() == 0);
            Assert.Contains(observations, item =>
                item.GetProperty("features").GetArrayLength() == 16);

            var candidate = runRoot.GetProperty("candidate");
            var candidateHash = candidate.GetProperty("contentHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(candidateHash));
            Assert.True(candidate.GetProperty("developmentOnly").GetBoolean());
            Assert.False(candidate.GetProperty("canPublish").GetBoolean());
            Assert.False(candidate.GetProperty("canActivate").GetBoolean());
            Assert.True(candidate.GetProperty("immutable").GetBoolean());
            Assert.Equal("CannotPublish", candidate.GetProperty("publication").GetString());
            Assert.True(candidate.GetProperty("typedCoefficientsDecoded").GetBoolean());
            Assert.True(candidate.GetProperty("typedEvidenceDecoded").GetBoolean());
            Assert.Equal(1, candidate.GetProperty("decodedViewCount").GetInt32());
            Assert.Equal(16, candidate.GetProperty("decodedPointCount").GetInt32());
            Assert.InRange(candidate.GetProperty("evidencePayloadBytes").GetInt32(), 1,
                CalibrationComputationEvidencePayload.MaximumBytes);
            var candidateEvidenceHash = candidate.GetProperty("evidenceContentHash").GetString();
            Assert.Matches("^[0-9A-Fa-f]{64}$", candidateEvidenceHash ?? string.Empty);
            var coefficients = candidate.GetProperty("coefficients");
            Assert.Equal(9, coefficients.GetProperty("imageToPlane").GetArrayLength());
            Assert.Equal(9, coefficients.GetProperty("planeToImage").GetArrayLength());
            var imageHull = coefficients.GetProperty("imageHull").EnumerateArray().ToArray();
            Assert.InRange(imageHull.Length, 4, 16);
            Assert.Equal(4, coefficients.GetProperty("physicalHull").GetArrayLength());
            var candidateEvidence = candidate.GetProperty("evidence");
            Assert.Equal(16, candidateEvidence.GetProperty("residualCount").GetInt32());
            var retainedPoints = candidateEvidence.GetProperty("correspondences").EnumerateArray()
                .Select(point => point.GetProperty("observedPixels"))
                .Select(point => (point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble())).ToArray();
            Assert.All(imageHull, vertex => Assert.Contains((vertex.GetProperty("x").GetDouble(),
                vertex.GetProperty("y").GetDouble()), retainedPoints));
            Assert.Empty(candidateEvidence.GetProperty("missingMarkerIds").EnumerateArray());
            Assert.True(double.IsFinite(candidateEvidence.GetProperty("rmsMillimeters").GetDouble()));
            Assert.True(double.IsFinite(candidateEvidence.GetProperty("rmsPixels").GetDouble()));
            Assert.True(double.IsFinite(candidateEvidence.GetProperty("constraintRankRatio").GetDouble()));
            Assert.InRange(candidateEvidence.GetProperty("inverseClosureError").GetDouble(), 0, 1e-12);

            var identities = runRoot.GetProperty("rawFrameIdentities").EnumerateArray().ToArray();
            Assert.Equal(2, identities.Length);
            Assert.Equal(2, identities.Select(item => item.GetProperty("frameId").GetString())
                .Distinct(StringComparer.Ordinal).Count());
            Assert.All(identities, item =>
            {
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    item.GetProperty("sourceHash").GetString() ?? string.Empty);
                Assert.Matches("^[0-9A-Fa-f]{64}$",
                    item.GetProperty("pixelHash").GetString() ?? string.Empty);
                Assert.Equal(640, item.GetProperty("width").GetInt32());
                Assert.Equal(480, item.GetProperty("height").GetInt32());
            });

            var wpf = runRoot.GetProperty("wpfSummary");
            Assert.True(wpf.GetProperty("rendered").GetBoolean());
            Assert.True(wpf.GetProperty("framesReadOnly").GetBoolean());
            Assert.True(wpf.GetProperty("observationsReadOnly").GetBoolean());
            Assert.True(wpf.GetProperty("passwordEmpty").GetBoolean());
            Assert.True(wpf.GetProperty("coordinateEditingUnavailable").GetBoolean());
            var screenshots = runRoot.GetProperty("screenshots").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToArray();
            Assert.Contains("calibration-session-candidate.png", screenshots);
            Assert.True(new FileInfo(Path.Combine(directory,
                "calibration-session-candidate.png")).Length > 0);

            var restartArguments = new[]
            {
                "--mode", "restart",
                "--directory", directory,
                "--planar-images", images,
                "--user-name", UserName,
                "--expected-principal", principal.ToString("D"),
                "--audit-key", audit.SigningKeyName
            };
            var restart = await RunProcessAsync(consumer, repository.FullName,
                restartArguments, password).ConfigureAwait(true);
            Assert.DoesNotContain(password, restart.Output, StringComparison.Ordinal);
            await WriteJsonAsync(restartLog, new
            {
                mode = "restart",
                exitCode = restart.ExitCode,
                output = restart.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(restart.ExitCode == 0, restart.Output);
            Assert.Contains("V126-N02 planar-homography-restart PASS", restart.Output);

            var restartEvidencePath = Path.Combine(directory, "calibration-session-restart.json");
            Assert.True(new FileInfo(restartEvidencePath).Length > 0);
            using var restartEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(restartEvidencePath).ConfigureAwait(true));
            var restartRoot = restartEvidence.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("result").GetString());
            Assert.Equal("planar", restartRoot.GetProperty("calibrationMode").GetString());
            Assert.Equal(runRoot.GetProperty("sessionId").GetString(),
                restartRoot.GetProperty("sessionId").GetString());
            Assert.Equal(candidateHash, restartRoot.GetProperty("candidateHash").GetString());
            Assert.Equal(candidateEvidenceHash,
                restartRoot.GetProperty("candidateEvidenceHash").GetString());
            Assert.True(restartRoot.GetProperty("readOnlyQueryDatabaseUnchanged").GetBoolean());
            Assert.Equal(2, restartRoot.GetProperty("frameCount").GetInt32());
            Assert.Equal(1, restartRoot.GetProperty("validViewCount").GetInt32());
            Assert.Equal(16, restartRoot.GetProperty("validPointCount").GetInt32());
            Assert.Equal(2, restartRoot.GetProperty("extractionReceiptCount").GetInt32());
            var retainedReceipts = restartRoot.GetProperty("retainedExtractionReceipts")
                .EnumerateArray().ToArray();
            Assert.Equal(2, retainedReceipts.Length);
            var runReceiptsByFrame = runReceipts.ToDictionary(item =>
                item.GetProperty("frameId").GetString()!, StringComparer.Ordinal);
            var retainedReceiptsByFrame = retainedReceipts.ToDictionary(item =>
                item.GetProperty("frameId").GetString()!, StringComparer.Ordinal);
            Assert.Equal(runReceiptsByFrame.Keys.OrderBy(value => value, StringComparer.Ordinal),
                retainedReceiptsByFrame.Keys.OrderBy(value => value, StringComparer.Ordinal));
            foreach (var pair in runReceiptsByFrame)
            {
                var retained = retainedReceiptsByFrame[pair.Key];
                Assert.Equal(pair.Value.GetProperty("receiptContentHash").GetString(),
                    retained.GetProperty("receiptContentHash").GetString());
                Assert.Equal(pair.Value.GetProperty("receiptCanonicalBytesHash").GetString(),
                    retained.GetProperty("receiptCanonicalBytesHash").GetString());
                Assert.Equal(pair.Value.GetProperty("length").GetInt32(),
                    retained.GetProperty("length").GetInt32());
                Assert.Equal(pair.Value.GetProperty("format").GetRawText(),
                    retained.GetProperty("format").GetRawText());
            }
            Assert.Equal(0, restartRoot.GetProperty("openedDevices").GetInt32());
            Assert.False(restartRoot.GetProperty("ready").GetBoolean());
            Assert.True(restartRoot.GetProperty("productionOutputsAbsent").GetBoolean());

            var hash = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            Assert.Equal(consumerSha256, hash);
            Assert.Equal(64, hash.Length);
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    private static async Task<(string ManifestHash, string[] RawHashes)> PrepareCheckerboardImagesAsync(
        DirectoryInfo repository, string directory)
    {
        Directory.CreateDirectory(directory);
        var frozenSource = Path.Combine(repository.FullName, "tests",
            "SharpInspect.Calibration.OpenCvSharp.Tests", "Fixtures", "checkerboard-v1.json");
        var frozenManifest = Path.Combine(directory, "checkerboard-v1.json");
        File.Copy(frozenSource, frozenManifest, overwrite: false);
        var frozenHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(frozenManifest).ConfigureAwait(true)));
        Assert.Equal(CheckerboardFrozenManifestHash, frozenHash);

        var rawHashes = new List<string>(SyntheticCheckerboardFixture.Poses.Count);
        var entries = new List<object>(SyntheticCheckerboardFixture.Poses.Count);
        for (var index = 0; index < SyntheticCheckerboardFixture.Poses.Count; index++)
        {
            var pixels = SyntheticCheckerboardFixture.Render(index);
            var fileName = $"frame-{index:D2}.raw";
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), pixels)
                .ConfigureAwait(true);
            var hash = Convert.ToHexString(SHA256.HashData(pixels));
            rawHashes.Add(hash);
            entries.Add(new
            {
                index,
                file = fileName,
                sha256 = hash,
                width = SyntheticCheckerboardFixture.ImageWidth,
                height = SyntheticCheckerboardFixture.ImageHeight,
                strideBytes = SyntheticCheckerboardFixture.ImageWidth,
                pixelFormat = "Mono8"
            });
        }

        var manifestPath = Path.Combine(directory, "checkerboard-images.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            schema = 1,
            datasetVersion = SyntheticCheckerboardFixture.DatasetVersion,
            frozenManifest = "checkerboard-v1.json",
            frozenManifestSha256 = frozenHash,
            images = entries
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(true);
        var manifestHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(true)));
        return (manifestHash, rawHashes.ToArray());
    }

    private static async Task<(string ManifestHash, string[] RawHashes)> PreparePlanarImagesAsync(
        DirectoryInfo repository, string directory)
    {
        Directory.CreateDirectory(directory);
        var frozenSource = Path.Combine(repository.FullName, "tests",
            "SharpInspect.Calibration.OpenCvSharp.Tests", "Fixtures", "planar-v1.json");
        var fixtureSource = Path.Combine(repository.FullName, "tests",
            "SharpInspect.Calibration.OpenCvSharp.Tests", "SyntheticPlanarFixture.cs");
        Assert.Equal(PlanarFixtureSourceHash, Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(fixtureSource)
                .ConfigureAwait(true))));
        var frozenManifest = Path.Combine(directory, "planar-v1.json");
        File.Copy(frozenSource, frozenManifest, overwrite: false);
        var frozenHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(frozenManifest).ConfigureAwait(true)));
        Assert.Equal(PlanarFrozenManifestHash, frozenHash);

        var pixels = new[]
        {
            SyntheticPlanarFixture.BlankImage(),
            SyntheticPlanarFixture.Render()
        };
        var rawHashes = new string[pixels.Length];
        var entries = new List<object>(pixels.Length);
        for (var index = 0; index < pixels.Length; index++)
        {
            var fileName = $"frame-{index:D2}.raw";
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), pixels[index])
                .ConfigureAwait(true);
            var hash = Convert.ToHexString(SHA256.HashData(pixels[index]));
            rawHashes[index] = hash;
            entries.Add(new
            {
                index,
                file = fileName,
                sha256 = hash,
                width = SyntheticPlanarFixture.ImageWidth,
                height = SyntheticPlanarFixture.ImageHeight,
                strideBytes = SyntheticPlanarFixture.ImageWidth,
                pixelFormat = "Mono8"
            });
        }

        var manifestPath = Path.Combine(directory, "planar-images.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            schema = 1,
            datasetVersion = SyntheticPlanarFixture.DatasetVersion,
            frozenManifest = "planar-v1.json",
            frozenManifestSha256 = frozenHash,
            images = entries
        }, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(true);
        var manifestHash = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(true)));
        return (manifestHash, rawHashes);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
            "SharpInspect.NET.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return root!;
    }

    private static string ResolveConsumer(DirectoryInfo repository, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var candidates = new[]
        {
            Path.Combine(repository.FullName, "samples", "SharpInspect.CalibrationConsumer",
                "bin", configuration, "net6.0-windows", "SharpInspect.CalibrationConsumer.dll"),
            Path.Combine(repository.FullName, "samples", "SharpInspect.CalibrationConsumer",
                "bin", "x64", configuration, "net6.0-windows", "SharpInspect.CalibrationConsumer.dll")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static async Task<Guid> BootstrapInFriendHostAsync(ProductionStoreOptions options,
        string password)
    {
        await using var store = new SqliteCommandStore(options);
        var initialized = await store.Initialization.ConfigureAwait(true);
        Assert.True(initialized.Committed, initialized.ReasonCode);
        await WaitForVerifiedAsync(store).ConfigureAwait(true);

        var identityOptions = options.LocalIdentity!;
        Assert.Contains(Permission.RunCalibration,
            identityOptions.AuthorizationPolicy.GetPermissions(HumanRoleBundle.Administrator));
        var identity = new LocalIdentityService(store, identityOptions, new FixtureConsole());
        var issued = await identity.ProvisionBootstrapTokenAsync().ConfigureAwait(true);
        Assert.True(issued.Succeeded, issued.ReasonCode);
        Assert.NotNull(issued.Token);
        var created = await identity.CreateFirstAdministratorAsync(
            new BootstrapAdministratorRequest(options.AuditIntegrityPolicy!.StationId,
                issued.Token!.TakeForDisplay(), UserName,
                "Calibration Consumer Validation Administrator", password))
            .ConfigureAwait(true);
        Assert.True(created.Succeeded, created.ReasonCode);
        Assert.NotNull(created.Identity);
        var principal = created.Identity!.PrincipalId;
        created.RecoveryKit?.Dispose();
        await WaitForVerifiedAsync(store).ConfigureAwait(true);
        return principal;
    }

    private static ProductionStoreOptions CreateStoreOptions(string directory,
        string databasePath, AuditIntegrityPolicy audit, int maximumFrames = 4, bool governance = false)
    {
        var evidenceRoot = Path.GetFullPath(Path.Combine(directory, "calibration-evidence"));
        Directory.CreateDirectory(evidenceRoot);
        return new ProductionStoreOptions(databasePath)
        {
            AuditIntegrityPolicy = audit,
            LocalIdentity = CreateIdentityOptions(audit.StationId, governance),
            AlarmPolicy = RecoveryAlarmPolicy(),
            CameraSetup = new CameraSetupStoreOptions(),
            CameraRecovery = new CameraRecoveryStoreOptions(),
            ImagingSetup = new ImagingSetupStoreOptions(),
            CalibrationGovernance = governance ? new CalibrationGovernanceStoreOptions
            { MaximumEntries = 32, MaximumPayloadBytes = 256 * 1024, MaximumTotalBytes = 8 * 1024 * 1024 } : null,
            CalibrationSessions = new CalibrationSessionStoreOptions
            {
                EvidenceRoot = evidenceRoot,
                MaximumSessions = 4,
                MaximumEvents = 256,
                MaximumEventPayloadBytes = 256 * 1024,
                MaximumFramesPerSession = maximumFrames,
                MaximumFrameBytes = 16L * 1024 * 1024,
                MaximumTotalFrameBytes = 64L * 1024 * 1024
            }
        };
    }

    private static LocalIdentityOptions CreateIdentityOptions(string stationId, bool governance = false)
    {
        var roles = AuthorizationPolicy.Development.RoleBundles.ToDictionary(
            pair => pair.Key, pair => (IEnumerable<Permission>)pair.Value);
        roles[HumanRoleBundle.Technician] = roles[HumanRoleBundle.Technician]
            .Append(Permission.RunCalibration);
        roles[HumanRoleBundle.Administrator] = roles[HumanRoleBundle.Administrator]
            .Append(Permission.RunCalibration);
        if (governance)
            roles[HumanRoleBundle.Administrator] = roles[HumanRoleBundle.Administrator]
                .Append(Permission.ManageCalibrationAcceptancePolicy)
                .Append(Permission.RecordPhysicalCalibrationVerification);
        return new LocalIdentityOptions(stationId,
            new LocalPasswordPolicy
            {
                Blocklist = PasswordBlocklist.Create(
                    "calibration-consumer-blocklist", "1", new[] { "passwordpassword" })
            }, new Pbkdf2PasswordHasher(), AuthenticationPolicy.Development,
            new AuthorizationPolicy("calibration-consumer", "calibration-consumer-2026-09",
                roles));
    }

    private static AlarmPolicy RecoveryAlarmPolicy() => new("V124-calibration-consumer", "1",
        new[]
        {
            new AlarmPolicyRule("StartupRecoveryRequired", "Runtime.StartupRecovery",
                AlarmSeverity.Warning, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraDisconnected", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, false,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery),
            new AlarmPolicyRule("CameraRecoveryFailed", "Runtime.CameraRecovery",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete | AlarmResetPrerequisites.NoActiveExecution |
                AlarmResetPrerequisites.NoPendingDelivery)
        }.Concat(Enum.GetValues<CameraProtocolViolationKind>().Select(kind =>
            new AlarmPolicyRule(CameraAcquisitionAlarmCode(kind), "Runtime.CameraAcquisition",
                AlarmSeverity.Error, ProductionImpact.BlockNewTriggers, true,
                AlarmNotification.UntilCleared, null, 0,
                AlarmResetPrerequisites.RecoveryComplete |
                AlarmResetPrerequisites.NoActiveExecution))).ToArray(),
        TimeSpan.FromSeconds(10), maximumActiveInstances: 256, maximumPlcEntries: 16);

    private static string CameraAcquisitionAlarmCode(CameraProtocolViolationKind kind) => kind switch
    {
        CameraProtocolViolationKind.EarlyFrame => "CameraEarlyFrame",
        CameraProtocolViolationKind.ExtraFrame => "CameraExtraFrame",
        CameraProtocolViolationKind.LateFrame => "CameraLateFrame",
        CameraProtocolViolationKind.CorrelationMismatch => "CameraCorrelationMismatch",
        CameraProtocolViolationKind.EarlyHardwarePulse => "CameraEarlyHardwarePulse",
        CameraProtocolViolationKind.DuplicateHardwarePulse => "CameraDuplicateHardwarePulse",
        CameraProtocolViolationKind.TriggerWhileBusy => "CameraTriggerWhileBusy",
        CameraProtocolViolationKind.InvalidFrame => "CameraInvalidFrame",
        CameraProtocolViolationKind.ObservationGap => "CameraObservationGap",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string consumer,
        string workingDirectory, IEnumerable<string> arguments, string password)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        start.ArgumentList.Add(consumer);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(password))
            .ConfigureAwait(true);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180))
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);
            return (-1, "CalibrationConsumerProcessDeadlineExceeded" + Environment.NewLine +
                await stdout.ConfigureAwait(true) + await stderr.ConfigureAwait(true));
        }
        return (process.ExitCode, await stdout.ConfigureAwait(true) +
            await stderr.ConfigureAwait(true));
    }

    private static async Task WaitForVerifiedAsync(SqliteCommandStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (store.Integrity?.State != AuditIntegrityState.Verified)
        {
            Assert.NotEqual(AuditIntegrityState.Faulted, store.Integrity?.State);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
    }

    private static async Task<string> ConsumerHashAsync(string consumer) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(consumer)
            .ConfigureAwait(true)));

    private static Task WriteJsonAsync(string path, object value) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private sealed class FixtureConsole : IPhysicalConsoleAuthority
    {
        public ConsoleAuthority Observe() => new(true, true, "S-1-5-21-1-2-3-1001");
    }
}
