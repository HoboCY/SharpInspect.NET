using System.Text.Json;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class CalibrationConsumerAcceptanceTests
{
    [Fact]
    public async Task V127_N01_IndependentConsumerRunsGovernedPlanarCalibrationAndReadOnlyRestart()
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
            ? Path.Combine(configuredEvidence, "governance")
            : Path.Combine(Path.GetTempPath(), "SharpInspect.NET-validation-artifacts",
                "ticket27-process", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var images = Path.Combine(directory, "planar-images");
        var imageSet = await PreparePlanarImagesAsync(repository, images)
            .ConfigureAwait(true);
        var databasePath = Path.Combine(directory, "calibration-session.sqlite");
        Assert.False(File.Exists(databasePath),
            "Governance acceptance requires a fresh schema-15 evidence directory.");

        var audit = new AuditIntegrityPolicy("SampleDevelopmentStation", "development-v1",
            "SharpInspect.CalibrationConsumer." + Guid.NewGuid().ToString("N"))
        {
            AllowInitialKeyCreation = true,
            CheckpointEveryEntries = 2,
            VerificationInterval = TimeSpan.FromSeconds(1),
            KeyDirectory = Path.Combine(directory, "private-keys")
        };
        var options = CreateStoreOptions(directory, databasePath, audit,
            maximumFrames: 6, governance: true);
        var password = "V127 isolated fixture " + Guid.NewGuid().ToString("N") + "!";
        Guid principal;
        var bootstrapLog = Path.Combine(directory, "calibration-governance-bootstrap.json");
        var runLog = Path.Combine(directory, "calibration-governance-process-run.json");
        var restartLog = Path.Combine(directory, "calibration-governance-process-restart.json");
        try
        {
            principal = await BootstrapInFriendHostAsync(options, password)
                .ConfigureAwait(true);
            await WriteJsonAsync(bootstrapLog, new
            {
                result = "Pass",
                schema = 15,
                stationId = audit.StationId,
                userName = UserName,
                principalId = principal,
                freshSchema15 = true,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun",
                production = "NotRun"
            }).ConfigureAwait(true);

            var common = new[]
            {
                "--mode", "run",
                "--directory", directory,
                "--planar-images", images,
                "--governance",
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
                governance = true,
                exitCode = run.ExitCode,
                output = run.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("V127-N01 calibration-governance-consumer PASS schema=15 " +
                "developmentOnly=true ready=false", run.Output);

            var runGovernancePath = Path.Combine(directory, "calibration-governance.json");
            Assert.True(new FileInfo(runGovernancePath).Length > 0);
            using var runGovernance = JsonDocument.Parse(
                await File.ReadAllTextAsync(runGovernancePath).ConfigureAwait(true));
            var runRoot = runGovernance.RootElement;
            Assert.Equal("Pass", runRoot.GetProperty("result").GetString());
            Assert.Equal(15, runRoot.GetProperty("schema").GetInt32());
            Assert.Equal(new[] { "V127-N01" }, runRoot.GetProperty("validationIds")
                .EnumerateArray().Select(value => value.GetString()).ToArray());
            var governanceProfile = runRoot.GetProperty("profile");
            AssertProfileReference(governanceProfile);
            var governanceCandidate = runRoot.GetProperty("candidate");
            AssertCandidateReference(governanceCandidate);
            AssertRecipeReference(runRoot.GetProperty("policy"),
                "development.planar.acceptance");
            AssertEvaluationReference(runRoot.GetProperty("evaluation"));
            AssertVerificationReference(runRoot.GetProperty("passingVerification"));
            AssertVerificationReference(runRoot.GetProperty("failingVerification"));
            Assert.NotEqual(runRoot.GetProperty("passingVerification").GetProperty("contentHash")
                .GetString(), runRoot.GetProperty("failingVerification").GetProperty("contentHash")
                .GetString());
            Assert.Matches("^[0-9A-Fa-f]{64}$",
                runRoot.GetProperty("coefficientHash").GetString() ?? string.Empty);
            Assert.Equal(new[] { "Missing", "Current", "Failed" }, runRoot.GetProperty("states")
                .EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.True(runRoot.GetProperty("replayRejected").GetBoolean());
            Assert.True(runRoot.GetProperty("developmentOnly").GetBoolean());
            Assert.False(runRoot.GetProperty("productionAuthority").GetBoolean());
            Assert.False(runRoot.GetProperty("canActivate").GetBoolean());
            Assert.False(runRoot.GetProperty("ready").GetBoolean());
            Assert.False(runRoot.GetProperty("canAdmitNewProductionTrigger").GetBoolean());
            Assert.Equal("NotRun", runRoot.GetProperty("actualProductionTrigger").GetString());
            Assert.Equal("NotRun", runRoot.GetProperty("physicalMetrology").GetString());
            Assert.True(DateTimeOffset.TryParse(
                runRoot.GetProperty("validUntilUtc").GetString(), out _));

            var sessionEvidencePath = Path.Combine(directory,
                "calibration-session-evidence.json");
            Assert.True(new FileInfo(sessionEvidencePath).Length > 0);
            using var sessionEvidence = JsonDocument.Parse(
                await File.ReadAllTextAsync(sessionEvidencePath).ConfigureAwait(true));
            var sessionRoot = sessionEvidence.RootElement;
            Assert.Equal("Pass", sessionRoot.GetProperty("result").GetString());
            Assert.Equal(15, sessionRoot.GetProperty("schema").GetInt32());
            Assert.Equal("planar", sessionRoot.GetProperty("calibrationMode").GetString());
            Assert.Equal(imageSet.ManifestHash,
                sessionRoot.GetProperty("source").GetProperty("imageManifestHash").GetString());
            var sessionContracts = sessionRoot.GetProperty("contracts");
            Assert.Equal("development.planar.acceptance",
                sessionContracts.GetProperty("acceptance").GetProperty("id").GetString());
            Assert.Equal("1", sessionContracts.GetProperty("acceptance").GetProperty("version").GetString());
            Assert.Equal(sessionContracts.GetProperty("acceptance").GetProperty("contentHash").GetString(),
                runRoot.GetProperty("policy").GetProperty("contentHash").GetString());
            Assert.Equal(6, sessionRoot.GetProperty("store")
                .GetProperty("maximumFramesPerSession").GetInt32());
            Assert.Equal(2, sessionRoot.GetProperty("extractionReceiptCount").GetInt32());
            var receipts = sessionRoot.GetProperty("extractionReceipts").EnumerateArray().ToArray();
            Assert.Equal(2, receipts.Length);
            Assert.All(receipts, receipt =>
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
            });

            var evidenceCandidate = sessionRoot.GetProperty("evidenceAfterExit")
                .GetProperty("candidate");
            Assert.Equal(sessionRoot.GetProperty("sessionId").GetString(),
                governanceCandidate.GetProperty("sessionId").GetString());
            Assert.Equal(evidenceCandidate.GetProperty("contentHash").GetString(),
                governanceCandidate.GetProperty("candidateContentHash").GetString());
            Assert.Equal(evidenceCandidate.GetProperty("candidateId").GetString(),
                governanceCandidate.GetProperty("candidateId").GetString());
            Assert.Equal(evidenceCandidate.GetProperty("coefficientHash").GetString(),
                runRoot.GetProperty("coefficientHash").GetString());
            Assert.True(evidenceCandidate.GetProperty("developmentOnly").GetBoolean());
            Assert.False(evidenceCandidate.GetProperty("canPublish").GetBoolean());
            Assert.False(evidenceCandidate.GetProperty("canActivate").GetBoolean());
            Assert.True(sessionRoot.GetProperty("candidate").GetProperty("immutable").GetBoolean());

            var ledgerAfterRun = ReadGovernanceLedgerSummary(databasePath);
            Assert.Equal(15, ledgerAfterRun.SchemaVersion);
            Assert.Equal(5, ledgerAfterRun.GovernanceEventCount);
            Assert.Equal(new[]
            {
                "AcceptancePolicyPublished", "CandidatePolicyEvaluated",
                "CalibrationProfilePublished", "PhysicalVerificationRecorded",
                "PhysicalVerificationRecorded"
            }, ledgerAfterRun.Kinds);
            var restartArguments = new[]
            {
                "--mode", "restart",
                "--directory", directory,
                "--planar-images", images,
                "--governance",
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
                governance = true,
                exitCode = restart.ExitCode,
                output = restart.Output,
                consumer,
                externalNuGetConsumer = !string.IsNullOrWhiteSpace(configuredConsumer),
                consumerSha256,
                windowsAdminBootstrap = "FixtureOnly",
                productionWindowsAdministratorValidation = "NotRun"
            }).ConfigureAwait(true);
            Assert.True(restart.ExitCode == 0, restart.Output);
            Assert.Contains("V127-N02 calibration-governance-restart PASS schema=15 " +
                "readOnly=true ready=false", restart.Output);

            var restartGovernancePath = Path.Combine(directory,
                "calibration-governance-restart.json");
            Assert.True(new FileInfo(restartGovernancePath).Length > 0);
            using var restartGovernance = JsonDocument.Parse(
                await File.ReadAllTextAsync(restartGovernancePath).ConfigureAwait(true));
            var restartRoot = restartGovernance.RootElement;
            Assert.Equal("Pass", restartRoot.GetProperty("result").GetString());
            Assert.Equal(15, restartRoot.GetProperty("schema").GetInt32());
            Assert.Equal(new[] { "V127-N02" }, restartRoot.GetProperty("validationIds")
                .EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal(governanceProfile.GetRawText(),
                restartRoot.GetProperty("profile").GetRawText());
            Assert.Equal(runRoot.GetProperty("coefficientHash").GetString(),
                restartRoot.GetProperty("coefficientHash").GetString());
            Assert.Equal("Failed", restartRoot.GetProperty("verificationState").GetString());
            Assert.True(restartRoot.GetProperty("readOnlyQueryDatabaseUnchanged").GetBoolean());
            Assert.Equal(0, restartRoot.GetProperty("openedDevices").GetInt32());
            Assert.True(restartRoot.GetProperty("developmentOnly").GetBoolean());
            Assert.False(restartRoot.GetProperty("ready").GetBoolean());
            Assert.False(restartRoot.GetProperty("productionAuthority").GetBoolean());
            Assert.False(restartRoot.GetProperty("canAdmitNewProductionTrigger").GetBoolean());
            Assert.Equal("NotRun", restartRoot.GetProperty("actualProductionTrigger").GetString());
            Assert.Equal("NotRun", restartRoot.GetProperty("physicalMetrology").GetString());

            var ledgerAfterRestart = ReadGovernanceLedgerSummary(databasePath);
            Assert.Equal(ledgerAfterRun.SchemaVersion, ledgerAfterRestart.SchemaVersion);
            Assert.Equal(ledgerAfterRun.GovernanceEventCount,
                ledgerAfterRestart.GovernanceEventCount);
            Assert.Equal(ledgerAfterRun.Kinds, ledgerAfterRestart.Kinds);

            var finalConsumerHash = await ConsumerHashAsync(consumer).ConfigureAwait(true);
            Assert.Equal(consumerSha256, finalConsumerHash);
            Assert.Equal(64, finalConsumerHash.Length);

            foreach (var jsonPath in Directory.EnumerateFiles(directory, "*.json",
                         SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(password,
                    await File.ReadAllTextAsync(jsonPath).ConfigureAwait(true),
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            var key = WindowsMachineAuditKey.GetKeyPath(audit);
            if (File.Exists(key)) File.Delete(key);
            if (Directory.Exists(audit.KeyDirectory))
                Directory.Delete(audit.KeyDirectory, recursive: true);
        }
    }

    private static void AssertProfileReference(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.True(Guid.TryParse(value.GetProperty("profileId").GetString(), out var profileId));
        Assert.NotEqual(Guid.Empty, profileId);
        Assert.Equal(1, value.GetProperty("version").GetInt64());
        Assert.Matches("^[0-9A-Fa-f]{64}$",
            value.GetProperty("contentHash").GetString() ?? string.Empty);
    }

    private static void AssertCandidateReference(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.True(Guid.TryParse(value.GetProperty("sessionId").GetString(), out var sessionId));
        Assert.NotEqual(Guid.Empty, sessionId);
        Assert.True(Guid.TryParse(value.GetProperty("candidateId").GetString(), out var candidateId));
        Assert.NotEqual(Guid.Empty, candidateId);
        Assert.Matches("^[0-9A-Fa-f]{64}$",
            value.GetProperty("candidateContentHash").GetString() ?? string.Empty);
        Assert.Matches("^[0-9A-Fa-f]{64}$",
            value.GetProperty("contentHash").GetString() ?? string.Empty);
    }

    private static void AssertRecipeReference(JsonElement value, string expectedId)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal(expectedId, value.GetProperty("id").GetString());
        Assert.Equal("1", value.GetProperty("version").GetString());
        Assert.Matches("^[0-9A-Fa-f]{64}$",
            value.GetProperty("contentHash").GetString() ?? string.Empty);
    }

    private static void AssertEvaluationReference(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.True(Guid.TryParse(value.GetProperty("evaluationId").GetString(), out var evaluationId));
        Assert.NotEqual(Guid.Empty, evaluationId);
        Assert.Matches("^[0-9A-Fa-f]{64}$",
            value.GetProperty("contentHash").GetString() ?? string.Empty);
    }

    private static void AssertVerificationReference(JsonElement value)
    {
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.True(Guid.TryParse(value.GetProperty("verificationId").GetString(), out var verificationId));
        Assert.NotEqual(Guid.Empty, verificationId);
        Assert.Matches("^[0-9A-Fa-f]{64}$",
            value.GetProperty("contentHash").GetString() ?? string.Empty);
    }

    private static (int SchemaVersion, int GovernanceEventCount, string[] Kinds)
        ReadGovernanceLedgerSummary(string databasePath)
    {
        using var connection = SqliteNative.Open(databasePath, readOnly: true);
        var database = connection.Handle!;
        var deadline = new StoreDeadline(TimeSpan.FromSeconds(5));
        var schema = checked((int)AuditChainDatabase.Scalar(
            database, "PRAGMA user_version;", deadline));
        var kinds = AuditChainDatabase.Read(database,
            "SELECT Kind FROM calibration_governance_events ORDER BY Position;",
            deadline, statement => SqliteNative.ColumnText(statement, 0) ?? string.Empty);
        return (schema, kinds.Count, kinds.ToArray());
    }
}
