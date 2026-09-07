using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Conformance;

namespace SharpInspect.SampleHost;

/// <summary>
/// A deliberately small development-only consumer of the public conformance API. It leaves the
/// ledger, key, frozen documents, and public test fixtures in the supplied directory so a second
/// process can inspect the same evidence without reopening the writer.
/// </summary>
internal static class ConformanceDemo
{
    private const string ManifestFileName = "conformance-manifest.json";
    private const string SummaryFileName = "conformance-summary.json";
    private const string DatabaseFileName = "conformance.sqlite";
    private const string KeyDirectoryName = "private-key";
    private const string SigningKeyName = "SharpInspect.SampleHost.ConformanceDevelopment";
    private const string ProfileId = "development-v107-profile";
    private const string TestIdPass = "V107-S01-ExpectedPass";
    private const string TestIdMismatch = "V107-S02-ExpectedPass";
    private const string RequirementPass = "V107-REQ-PASS";
    private const string RequirementMismatch = "V107-REQ-MISMATCH";
    private const string ExpectedText = "expected-pass";

    public static int RunDemo(string suppliedDirectory, string? sourceRevision)
    {
        try
        {
            if (!Path.IsPathFullyQualified(suppliedDirectory))
                throw new ArgumentException("ConformanceDemoDirectoryMustBeAbsolute", nameof(suppliedDirectory));
            if (string.IsNullOrWhiteSpace(sourceRevision))
                throw new ArgumentException("ConformanceSourceRevisionRequired", nameof(sourceRevision));

            var root = Path.GetFullPath(suppliedDirectory);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("ConformanceDemoDirectoryMustBeEmpty");
            Directory.CreateDirectory(root);

            var result = ExecuteDemo(root, sourceRevision);
            Console.WriteLine($"V107-P01 conformance-demo PASS candidate={result.CandidateHash} " +
                $"contexts={result.InitialContextHash},{result.RetryContextHash} attempts={result.AttemptCount} " +
                $"DevelopmentOnly=true preflightNotRun={(result.PreflightNotRun ? "true" : "false")} CanIssueQualification=false");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"V107-P01 conformance-demo FAIL reason={SafeReason(exception)}");
            return 1;
        }
    }

    public static int RunQuery(string suppliedDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(suppliedDirectory))
                throw new ArgumentException("ConformanceQueryDirectoryMustBeAbsolute", nameof(suppliedDirectory));
            var root = Path.GetFullPath(suppliedDirectory);
            var manifestPath = Path.Combine(root, ManifestFileName);
            var summaryPath = Path.Combine(root, SummaryFileName);
            var manifest = ReadJson<DemoManifest>(manifestPath);
            var summary = ReadJson<DemoSummary>(summaryPath);
            if (!string.Equals(manifest.RootDirectory, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ConformanceManifestRootMismatch");
            if (!string.Equals(summary.CandidateHash, manifest.CandidateHash, StringComparison.Ordinal) ||
                !string.Equals(summary.InitialContextHash, manifest.InitialContextHash, StringComparison.Ordinal) ||
                !string.Equals(summary.RetryContextHash, manifest.RetryContextHash, StringComparison.Ordinal))
                throw new InvalidOperationException("ConformanceSummaryManifestMismatch");
            if (!manifest.TestIds.SequenceEqual(new[] { TestIdPass, TestIdMismatch }, StringComparer.Ordinal) ||
                !manifest.RequirementIds.SequenceEqual(new[] { RequirementPass, RequirementMismatch }, StringComparer.Ordinal) ||
                summary.InitialGateOutcomes.Count != 2 ||
                summary.InitialGateOutcomes.Values.Any(value => value != nameof(ConformanceOutcome.NotRun)) ||
                summary.AfterFirstGateOutcomes[TestIdPass] != nameof(ConformanceOutcome.Pass) ||
                summary.AfterFirstGateOutcomes[TestIdMismatch] != nameof(ConformanceOutcome.NotRun) ||
                !summary.FailedAggregateHasProductFailure || !summary.RetryAggregateHasProductFailure ||
                summary.RetryAggregateCanIssueQualification || summary.Attempts.Length != 3 ||
                summary.Attempts[0].Outcome != nameof(ConformanceOutcome.Pass) ||
                summary.Attempts[1].Outcome != nameof(ConformanceOutcome.Fail) ||
                summary.Attempts[2].Outcome != nameof(ConformanceOutcome.Pass))
                throw new InvalidOperationException("ConformanceSummaryEvidenceInvalid");

            VerifyManifestFiles(manifest);
            var databasePath = Path.Combine(root, DatabaseFileName);
            var keyDirectory = Path.Combine(root, KeyDirectoryName);
            var protectedPaths = ProtectedPaths(databasePath, keyDirectory);
            var before = HashPaths(protectedPaths);
            var options = new ConformanceLedgerOptions(databasePath, keyDirectory, manifest.SigningKeyName);
            ConformanceAggregate initialAggregate;
            ConformanceAggregate retryAggregate;
            IReadOnlyList<ConformanceExecutionView> executions;
            var outputHashes = new List<string>();
            using (var query = new ConformanceQuery(options))
            {
                executions = query.GetExecutions(0, 200);
                if (executions.Count != 3)
                    throw new InvalidOperationException("ConformanceAttemptCountInvalid");

                var expectedTests = new[] { TestIdPass, TestIdMismatch, TestIdPass };
                for (var index = 0; index < executions.Count; index++)
                {
                    var execution = executions[index];
                    if (execution.Record is null || execution.Reservation.TestId != expectedTests[index])
                        throw new InvalidOperationException("ConformanceAttemptRecordInvalid");
                    var hashes = execution.Record.Outputs.Select(output =>
                    {
                        var bytes = query.ReadArtifact(output.Sha256);
                        var actual = Convert.ToHexString(SHA256.HashData(bytes));
                        if (!string.Equals(actual, output.Sha256, StringComparison.Ordinal))
                            throw new InvalidOperationException("ConformanceRawOutputHashMismatch");
                        outputHashes.Add(output.Sha256);
                        return output.Sha256;
                    }).ToArray();
                    Console.WriteLine($"V107 attempt={index + 1} test={execution.Reservation.TestId} " +
                        $"execution={execution.Reservation.TestExecutionId:D} outcome={execution.Record.Outcome} " +
                        $"rawOutputHashes={string.Join(',', hashes)}");
                }

                initialAggregate = query.Aggregate(manifest.CandidateHash, manifest.InitialContextHash,
                    QualificationLayer.Framework);
                retryAggregate = query.Aggregate(manifest.CandidateHash, manifest.RetryContextHash,
                    QualificationLayer.Framework);
            }
            var after = HashPaths(protectedPaths);
            if (!before.SequenceEqual(after, StringComparer.Ordinal))
                throw new InvalidOperationException("ConformanceReadOnlyChangedEvidence");

            RequireFailedAggregate(initialAggregate, TestIdMismatch);
            if (!retryAggregate.HasProductFailure || retryAggregate.CanIssueQualification ||
                retryAggregate.Gates.Single(gate => gate.TestId == TestIdPass).Outcome != ConformanceOutcome.Pass)
                throw new InvalidOperationException("ConformanceHistoricalFailureWasWashed");

            Console.WriteLine($"V107-P02 conformance-query PASS attempts={executions.Count} " +
                $"rawOutputs={outputHashes.Count} historicalFail=true CanIssueQualification=false " +
                $"databaseUnchanged=true");
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine($"V107-P02 conformance-query FAIL reason={SafeReason(exception)}");
            return 1;
        }
    }

    private static DemoResult ExecuteDemo(string root, string sourceRevision)
    {
        var scenarios = new IConformanceScenario[] { new ExpectedPassScenario(), new MismatchScenario() };
        var profile = CreateProfile();
        var frozenProfile = ConformanceDocuments.FreezeProfile(profile);
        var candidate = CreateCandidate(root, sourceRevision, out var candidateBindings);
        var frozenCandidate = ConformanceDocuments.FreezeCandidate(candidate);

        var databasePath = Path.Combine(root, DatabaseFileName);
        var keyDirectory = Path.Combine(root, KeyDirectoryName);
        var options = new ConformanceLedgerOptions(databasePath, keyDirectory, SigningKeyName)
        {
            OperationTimeout = TimeSpan.FromSeconds(5),
            MaxEntries = 10_000,
            MaxTotalBytes = 128L * 1024 * 1024
        };
        FrozenConformanceDocument frozenInitialContext;
        FrozenConformanceDocument frozenRetryContext;
        IReadOnlyList<ConformanceFileBinding> initialContextBindings;
        IReadOnlyList<ConformanceFileBinding> retryContextBindings;

        TestExecutionRecord first;
        TestExecutionRecord mismatch;
        TestExecutionRecord retry;
        ConformanceAggregate initialAggregate;
        ConformanceAggregate afterFirstAggregate;
        ConformanceAggregate failedAggregate;
        ConformanceAggregate retryAggregate;
        using (var facility = new ConformanceFacility(options, scenarios))
        {
            var initialContext = CreateContext(root, "initial", frozenProfile.Sha256, scenarios,
                out initialContextBindings);
            frozenInitialContext = ConformanceDocuments.FreezeContext(initialContext);
            var retryContext = CreateContext(root, "retry", frozenProfile.Sha256, scenarios,
                out retryContextBindings);
            frozenRetryContext = ConformanceDocuments.FreezeContext(retryContext);
            facility.Freeze(frozenProfile, frozenCandidate, frozenInitialContext);
            facility.Freeze(frozenProfile, frozenCandidate, frozenRetryContext);

            initialAggregate = facility.Aggregate(frozenCandidate.Sha256, frozenInitialContext.Sha256,
                QualificationLayer.Framework);
            if (initialAggregate.AllSelectedCasesSatisfied ||
                initialAggregate.Gates.Any(gate => gate.Outcome != ConformanceOutcome.NotRun))
                throw new InvalidOperationException("ConformanceMissingEvidenceAppearedPassing");

            var initialBindings = new ConformanceBindings(root,
                candidateBindings.Concat(initialContextBindings).ToArray(), Path.Combine(root, "candidate"));
            first = facility.ExecuteAsync(new ConformanceExecutionRequest(
                frozenCandidate.Sha256, frozenInitialContext.Sha256, TestIdPass, initialBindings,
                new[] { EvidenceFromFile(initialContextBindings, "dataset-pass"),
                    EvidenceFromFile(initialContextBindings, "dataset-mismatch") })).GetAwaiter().GetResult();
            if (first.Outcome != ConformanceOutcome.Pass || first.Observed != ExpectedText)
                throw new InvalidOperationException($"ConformanceExpectedPassMissing:{first.Outcome}:{first.ReasonCode}:{first.Observed}");

            afterFirstAggregate = facility.Aggregate(frozenCandidate.Sha256, frozenInitialContext.Sha256,
                QualificationLayer.Framework);
            if (afterFirstAggregate.AllSelectedCasesSatisfied)
                throw new InvalidOperationException("ConformanceMissingCaseWasAccepted");

            mismatch = facility.ExecuteAsync(new ConformanceExecutionRequest(
                frozenCandidate.Sha256, frozenInitialContext.Sha256, TestIdMismatch, initialBindings,
                new[] { EvidenceFromFile(initialContextBindings, "dataset-pass"),
                    EvidenceFromFile(initialContextBindings, "dataset-mismatch") })).GetAwaiter().GetResult();
            if (mismatch.Outcome != ConformanceOutcome.Fail || mismatch.Observed == ExpectedText)
                throw new InvalidOperationException($"ConformanceExpectedProductFailMissing:{mismatch.Outcome}:{mismatch.ReasonCode}:{mismatch.Observed}");

            failedAggregate = facility.Aggregate(frozenCandidate.Sha256, frozenInitialContext.Sha256,
                QualificationLayer.Framework);
            RequireFailedAggregate(failedAggregate, TestIdMismatch);

            var retryBindings = new ConformanceBindings(root,
                candidateBindings.Concat(retryContextBindings).ToArray(), Path.Combine(root, "candidate"));
            retry = facility.ExecuteAsync(new ConformanceExecutionRequest(
                frozenCandidate.Sha256, frozenRetryContext.Sha256, TestIdPass, retryBindings,
                new[] { EvidenceFromFile(retryContextBindings, "dataset-pass-retry") },
                first.TestExecutionId)).GetAwaiter().GetResult();
            if (retry.Outcome != ConformanceOutcome.Pass || retry.Observed != ExpectedText)
                throw new InvalidOperationException("ConformanceRetryPassMissing");

            retryAggregate = facility.Aggregate(frozenCandidate.Sha256, frozenRetryContext.Sha256,
                QualificationLayer.Framework);
            if (!retryAggregate.HasProductFailure || retryAggregate.CanIssueQualification ||
                retryAggregate.Gates.Single(gate => gate.TestId == TestIdPass).Outcome != ConformanceOutcome.Pass)
                throw new InvalidOperationException("ConformanceHistoricalFailureWashedDuringDemo");
        }

        var manifest = new DemoManifest
        {
            Schema = "v107-conformance-demo-v1",
            RootDirectory = root,
            SourceRevision = sourceRevision,
            DatabaseFile = DatabaseFileName,
            KeyDirectory = KeyDirectoryName,
            SigningKeyName = SigningKeyName,
            ProfileId = ProfileId,
            ProfileHash = frozenProfile.Sha256,
            CandidateHash = frozenCandidate.Sha256,
            InitialContextHash = frozenInitialContext.Sha256,
            RetryContextHash = frozenRetryContext.Sha256,
            TestIds = new[] { TestIdPass, TestIdMismatch },
            RequirementIds = new[] { RequirementPass, RequirementMismatch },
            CandidateBindings = candidateBindings.Select(ToManifestBinding).ToArray(),
            InitialContextBindings = initialContextBindings.Select(ToManifestBinding).ToArray(),
            RetryContextBindings = retryContextBindings.Select(ToManifestBinding).ToArray()
        };
        WriteJson(Path.Combine(root, ManifestFileName), manifest);

        var summary = new DemoSummary
        {
            Schema = "v107-conformance-summary-v1",
            CandidateHash = frozenCandidate.Sha256,
            InitialContextHash = frozenInitialContext.Sha256,
            RetryContextHash = frozenRetryContext.Sha256,
            InitialGateOutcomes = initialAggregate.Gates.ToDictionary(gate => gate.TestId, gate => gate.Outcome.ToString()),
            AfterFirstGateOutcomes = afterFirstAggregate.Gates.ToDictionary(gate => gate.TestId, gate => gate.Outcome.ToString()),
            FailedAggregateHasProductFailure = failedAggregate.HasProductFailure,
            RetryAggregateHasProductFailure = retryAggregate.HasProductFailure,
            RetryAggregateCanIssueQualification = retryAggregate.CanIssueQualification,
            Attempts = new[] { first, mismatch, retry }.Select(ToAttempt).ToArray()
        };
        WriteJson(Path.Combine(root, SummaryFileName), summary);
        return new DemoResult(frozenCandidate.Sha256, frozenInitialContext.Sha256,
            frozenRetryContext.Sha256, summary.Attempts.Length,
            initialAggregate.Gates.All(gate => gate.Outcome == ConformanceOutcome.NotRun));
    }

    private static ConformanceProfile CreateProfile()
    {
        var requirements = new[]
        {
            new ConformanceRequirement(RequirementPass, "ADR-0111", "A registered executable scenario can produce the frozen expected observation.", true),
            new ConformanceRequirement(RequirementMismatch, "ADR-0111", "A valid observation that differs from the frozen text is retained as Product Fail.", true)
        };
        var cases = new[]
        {
            new VerificationCase(TestIdPass, QualificationLayer.Framework, VerificationMethod.Executable,
                new[] { RequirementPass }, true, "Development consumer demo includes the deterministic pass scenario.", "",
                "observed-environment", "Frozen profile, candidate, context, and public dataset are present.",
                "Run the registered pass scenario once.", ExpectedText, "public-test-data-v1", "exact-text-v1"),
            new VerificationCase(TestIdMismatch, QualificationLayer.Framework, VerificationMethod.Executable,
                new[] { RequirementMismatch }, true, "Development consumer demo includes the deterministic mismatch scenario.", "",
                "observed-environment", "Frozen profile, candidate, context, and public dataset are present.",
                "Run the registered mismatch scenario once.", ExpectedText, "public-test-data-v1", "exact-text-v1")
        };
        return new ConformanceProfile(ProfileId, 1, ConformanceClaim.DevelopmentOnly,
            "Development-only consumer demonstration of immutable layered conformance evidence.",
            new[] { "win-x64" }, new[] { "conformance-ledger", "consumer-sample" },
            new[] { QualificationLayer.Framework }, "Retain the private demonstration directory as review evidence.",
            "Development-only; never a release or station qualification authority.", requirements, cases);
    }

    private static ReleaseCandidateDefinition CreateCandidate(string root, string sourceRevision,
        out IReadOnlyList<ConformanceFileBinding> bindings)
    {
        var files = new List<ConformanceFileBinding>();
        var publicApiSource = typeof(IConformanceScenario).Assembly.Location;
        var runtimeSource = typeof(ConformanceFacility).Assembly.Location;
        if (string.IsNullOrEmpty(publicApiSource) || string.IsNullOrEmpty(runtimeSource))
            throw new InvalidOperationException("ConformanceConsumerAssemblyPathMissing");

        var publicApi = new[] { CopyBinding(root, files, "candidate/PublicApi", "abstractions-public-api",
            publicApiSource, "candidate/public-api/SharpInspect.NET.Abstractions.dll") };
        var schemas = new[] { FixtureBinding(root, files, "candidate/Schemas", "runtime-schema",
            "candidate/schema/runtime-schema-v1.json", "{\"schema\":\"development-conformance-v1\"}\n") };
        var packages = new[]
        {
            CopyBinding(root, files, "candidate/Packages", "abstractions-package", publicApiSource,
                "candidate/packages/SharpInspect.NET.Abstractions.dll"),
            CopyBinding(root, files, "candidate/Packages", "runtime-package", runtimeSource,
                "candidate/packages/SharpInspect.NET.Runtime.dll")
        };
        var migrations = new[] { FixtureBinding(root, files, "candidate/Migrations", "migration-fixture",
            "candidate/migrations/development-migration-v1.json", "{\"migration\":\"none-required\"}\n") };
        var embeddedAssets = new[] { FixtureBinding(root, files, "candidate/EmbeddedAssets", "embedded-fixture",
            "candidate/assets/development-asset.txt", "development-only conformance fixture\n") };
        var dependencyLocks = new[] { FixtureBinding(root, files, "candidate/DependencyLocks", "dependency-lock",
            "candidate/locks/development-lock.txt", "consumer-package-lock-v1\n") };
        var buildConfiguration = new[] { FixtureBinding(root, files, "candidate/BuildConfiguration", "build-config",
            "candidate/build/build-configuration.txt", "source-revision=" + sourceRevision + "\ntarget=net6.0-windows\n") };
        var candidateManifestPath = Path.Combine(root, "candidate-manifest.json");
        var candidateDirectory = Path.Combine(root, "candidate");
        var candidateManifestJson = ConformanceBindings.CaptureCandidateManifest(candidateDirectory);
        File.WriteAllText(candidateManifestPath, candidateManifestJson, new UTF8Encoding(false));
        var candidateManifest = new FingerprintComponent("candidate-file-manifest",
            ConformanceBindings.HashText(candidateManifestJson));
        files.Add(new ConformanceFileBinding("candidate/EmbeddedAssets", candidateManifest.Name, candidateManifestPath));
        embeddedAssets = embeddedAssets.Append(candidateManifest).ToArray();
        bindings = files.AsReadOnly();
        return new ReleaseCandidateDefinition(sourceRevision, publicApi, schemas, packages, migrations,
            embeddedAssets, dependencyLocks, buildConfiguration);
    }

    private static QualificationContextDefinition CreateContext(string root, string label, string profileHash,
        IReadOnlyList<IConformanceScenario> scenarios, out IReadOnlyList<ConformanceFileBinding> bindings)
    {
        var files = new List<ConformanceFileBinding>();
        var datasetPassName = label == "initial" ? "dataset-pass" : "dataset-pass-retry";
        var datasetPassPath = $"context/{label}/datasets/{datasetPassName}.txt";
        var datasetPass = FixtureBinding(root, files, "context/Datasets", datasetPassName, datasetPassPath,
            "public dataset: expected-pass\n");
        var datasetMismatch = label == "initial"
            ? new[] { FixtureBinding(root, files, "context/Datasets", "dataset-mismatch",
                "context/initial/datasets/dataset-mismatch.txt", "public dataset: observed-mismatch\n") }
            : Array.Empty<FingerprintComponent>();
        var seed = FixtureBinding(root, files, "context/Seeds", "seed-" + label,
            $"context/{label}/seeds/seed.txt", "seed=" + label + "\n");
        var threshold = FixtureBinding(root, files, "context/Thresholds", "threshold-" + label,
            $"context/{label}/thresholds/threshold.txt", "expected-text=expected-pass\n");
        var rule = FixtureBinding(root, files, "context/CalculationRules", "calculation-" + label,
            $"context/{label}/rules/calculation.txt", "comparison=ordinal-exact\n");
        bindings = files.AsReadOnly();

        var contextFiles = new[] { datasetPass }.Concat(datasetMismatch).ToArray();
        var environment = new FingerprintComponent("observed-environment",
            ConformanceBindings.HashText(ConformanceBindings.CaptureEnvironmentJson()));
        return new QualificationContextDefinition(profileHash,
            ConformanceBindings.CaptureHarnesses(scenarios),
            scenarios.Select(ConformanceBindings.DescribeScenario).ToArray(),
            contextFiles, new[] { seed }, new[] { threshold }, new[] { rule }, new[] { environment });
    }

    private static ConformanceEvidence EvidenceFromFile(IReadOnlyList<ConformanceFileBinding> bindings, string name)
    {
        var binding = bindings.Single(item => item.Category == "context/Datasets" && item.Name == name);
        return new ConformanceEvidence(name, File.ReadAllBytes(binding.Path));
    }

    private static FingerprintComponent CopyBinding(string root, ICollection<ConformanceFileBinding> bindings,
        string category, string name, string sourcePath, string relativePath)
    {
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target, overwrite: false);
        var hash = ConformanceBindings.HashFile(target);
        bindings.Add(new ConformanceFileBinding(category, name, target));
        return new FingerprintComponent(name, hash);
    }

    private static FingerprintComponent FixtureBinding(string root, ICollection<ConformanceFileBinding> bindings,
        string category, string name, string relativePath, string content)
    {
        var target = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content, new UTF8Encoding(false));
        var hash = ConformanceBindings.HashFile(target);
        bindings.Add(new ConformanceFileBinding(category, name, target));
        return new FingerprintComponent(name, hash);
    }

    private static void RequireFailedAggregate(ConformanceAggregate aggregate, string failedTestId)
    {
        if (!aggregate.HasProductFailure || aggregate.CanIssueQualification ||
            aggregate.Gates.Single(gate => gate.TestId == failedTestId).Outcome != ConformanceOutcome.Fail)
            throw new InvalidOperationException("ConformanceProductFailureAggregationInvalid");
    }

    private static void VerifyManifestFiles(DemoManifest manifest)
    {
        var root = manifest.RootDirectory;
        var all = manifest.CandidateBindings.Concat(manifest.InitialContextBindings)
            .Concat(manifest.RetryContextBindings).ToArray();
        if (all.Length == 0 || all.Select(item => item.Category + "\0" + item.Name).Distinct(StringComparer.Ordinal).Count() != all.Length)
            throw new InvalidOperationException("ConformanceManifestBindingsInvalid");
        foreach (var item in all)
        {
            var path = Path.GetFullPath(item.Path);
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ConformanceBindings.HashFile(path), item.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("ConformanceManifestFileChanged");
        }
    }

    private static string[] ProtectedPaths(string databasePath, string keyDirectory)
    {
        if (!File.Exists(databasePath)) throw new InvalidOperationException("ConformanceDatabaseMissing");
        var paths = new List<string> { databasePath, databasePath + ".anchor" };
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = databasePath + suffix;
            if (File.Exists(sidecar)) paths.Add(sidecar);
        }
        var keys = Directory.GetFiles(keyDirectory, "*.key", SearchOption.TopDirectoryOnly);
        if (keys.Length != 1) throw new InvalidOperationException("ConformanceSigningKeyCountInvalid");
        paths.AddRange(keys);
        return paths.ToArray();
    }

    private static string[] HashPaths(IEnumerable<string> paths) => paths.Select(path =>
        path + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("ConformanceManifestMissing");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("ConformanceManifestInvalid");
    }

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

    private static DemoManifestBinding ToManifestBinding(ConformanceFileBinding binding) =>
        new() { Category = binding.Category, Name = binding.Name, Path = binding.Path,
            Sha256 = ConformanceBindings.HashFile(binding.Path) };

    private static DemoAttempt ToAttempt(TestExecutionRecord record) => new()
    {
        TestExecutionId = record.TestExecutionId,
        Outcome = record.Outcome.ToString(),
        Observed = record.Observed,
        ReasonCode = record.ReasonCode,
        OutputHashes = record.Outputs.Select(output => output.Sha256).ToArray()
    };

    private static string SafeReason(Exception exception) =>
        (exception.Message ?? exception.GetType().Name).Replace('\r', ' ').Replace('\n', ' ');

    private sealed class ExpectedPassScenario : IConformanceScenario
    {
        public string TestId => TestIdPass;
        public string ScenarioHash => ConformanceBindings.HashText(
            "SharpInspect.SampleHost.ConformanceDemo.ExpectedPassScenario|v1|expected-pass");

        public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConformanceObservation(ExpectedText, new[]
            {
                new ConformanceEvidence("scenario-pass-output",
                    Encoding.UTF8.GetBytes("scenario=V107-S01;observed=expected-pass;scope=DevelopmentOnly"))
            }));
        }
    }

    private sealed class MismatchScenario : IConformanceScenario
    {
        public string TestId => TestIdMismatch;
        public string ScenarioHash => ConformanceBindings.HashText(
            "SharpInspect.SampleHost.ConformanceDemo.MismatchScenario|v1|observed-mismatch");

        public Task<ConformanceObservation> ObserveAsync(ConformanceExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConformanceObservation("observed-mismatch", new[]
            {
                new ConformanceEvidence("scenario-mismatch-output",
                    Encoding.UTF8.GetBytes("scenario=V107-S02;observed=observed-mismatch;scope=DevelopmentOnly"))
            }));
        }
    }

    private sealed record DemoResult(string CandidateHash, string InitialContextHash,
        string RetryContextHash, int AttemptCount, bool PreflightNotRun);

    private sealed class DemoManifest
    {
        public string Schema { get; set; } = string.Empty;
        public string RootDirectory { get; set; } = string.Empty;
        public string SourceRevision { get; set; } = string.Empty;
        public string DatabaseFile { get; set; } = string.Empty;
        public string KeyDirectory { get; set; } = string.Empty;
        public string SigningKeyName { get; set; } = string.Empty;
        public string ProfileId { get; set; } = string.Empty;
        public string ProfileHash { get; set; } = string.Empty;
        public string CandidateHash { get; set; } = string.Empty;
        public string InitialContextHash { get; set; } = string.Empty;
        public string RetryContextHash { get; set; } = string.Empty;
        public string[] TestIds { get; set; } = Array.Empty<string>();
        public string[] RequirementIds { get; set; } = Array.Empty<string>();
        public DemoManifestBinding[] CandidateBindings { get; set; } = Array.Empty<DemoManifestBinding>();
        public DemoManifestBinding[] InitialContextBindings { get; set; } = Array.Empty<DemoManifestBinding>();
        public DemoManifestBinding[] RetryContextBindings { get; set; } = Array.Empty<DemoManifestBinding>();
    }

    private sealed class DemoManifestBinding
    {
        public string Category { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class DemoSummary
    {
        public string Schema { get; set; } = string.Empty;
        public string CandidateHash { get; set; } = string.Empty;
        public string InitialContextHash { get; set; } = string.Empty;
        public string RetryContextHash { get; set; } = string.Empty;
        public Dictionary<string, string> InitialGateOutcomes { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AfterFirstGateOutcomes { get; set; } = new(StringComparer.Ordinal);
        public bool FailedAggregateHasProductFailure { get; set; }
        public bool RetryAggregateHasProductFailure { get; set; }
        public bool RetryAggregateCanIssueQualification { get; set; }
        public DemoAttempt[] Attempts { get; set; } = Array.Empty<DemoAttempt>();
    }

    private sealed class DemoAttempt
    {
        public Guid TestExecutionId { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public string Observed { get; set; } = string.Empty;
        public string ReasonCode { get; set; } = string.Empty;
        public string[] OutputHashes { get; set; } = Array.Empty<string>();
    }
}
