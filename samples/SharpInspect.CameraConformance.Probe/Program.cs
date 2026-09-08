using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;
using SharpInspect.Cameras.Conformance;
using SharpInspect.Cameras.Virtual;
using SharpInspect.Runtime.Conformance;

namespace SharpInspect.CameraConformance.Probe;

internal static class Program
{
    private const string KeyName = "camera-conformance-development";
    private const string QualificationStatus = "IncompleteDevelopmentEvidence";
    private const string ScopeLimitations = "Virtual public-contract observations only. Callback stack, callback budget and native allocation measurements are Blocked. Real vendor/model/interface/runtime/OS/trigger combinations require independent Hardware Qualification.";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] is not ("run" or "query") ||
                !Path.IsPathFullyQualified(args[1]))
                throw new ArgumentException("Usage: run|query absolute-evidence-directory source-revision");
            var root = Path.GetFullPath(args[1]);
            if (args[0] == "run") await RunAsync(root, args[2]).ConfigureAwait(false);
            else Query(root, args[2]);
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            { result = "Fail", reason = exception.Message, hardwareQualification = "NotRun", canIssueQualification = false }));
            return 1;
        }
    }

    private static async Task RunAsync(string root, string revision)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException("CameraConformanceEvidenceDirectoryMustBeEmpty");
        Directory.CreateDirectory(root);
        var factory = new VirtualConformanceFixtureFactory();
        var suite = new CameraConformanceSuite(factory);
        var scenarios = suite.Scenarios;
        var inputs = suite.CreateInputs();
        var profile = ConformanceDocuments.FreezeProfile(suite.CreateProfile());
        var files = new List<ConformanceFileBinding>();
        var candidate = CreateCandidate(root, revision, files);
        var frozenCandidate = ConformanceDocuments.FreezeCandidate(candidate);
        var datasets = inputs.Select(input => WriteBinding(root, files, "context/Datasets",
            input.Name, "context/" + input.Name + ".json", input.GetBytes())).ToArray();
        var seeds = new[] { WriteBinding(root, files, "context/Seeds", "virtual-seed", "context/seed.json",
            JsonSerializer.SerializeToUtf8Bytes(new { factory.Declaration.Seed })) };
        var thresholds = new[] { WriteBinding(root, files, "context/Thresholds", "camera-limits", "context/limits.json",
            JsonSerializer.SerializeToUtf8Bytes(new { factory.Declaration.PoolCapacity, frameDelayTicks = factory.Declaration.FrameDelay.Ticks,
                scenarioDeadlineSeconds = 20, callbackInstrumentation = "Unavailable", nativeAllocationInstrumentation = "Unavailable" })) };
        var rules = new[] { WriteBinding(root, files, "context/CalculationRules", "camera-rules", "context/rules.json",
            Encoding.UTF8.GetBytes("{\"comparison\":\"exact-text-v1\",\"pixels\":\"sha256-valid-canonical-rows\",\"scope\":\"DevelopmentOnly\"}")) };

        // Load managed dependencies, including SQLite used by the facility, before freezing the harness.
        // No device or scenario executes before the reservation; these are assembly-load operations only.
        LoadManagedClosure(typeof(CameraConformanceSuite).Assembly, typeof(VirtualCameraProvider).Assembly,
            typeof(VirtualConformanceFixtureFactory).Assembly);
        using var facility = new ConformanceFacility(Options(root), scenarios, TimeSpan.FromSeconds(20));
        var context = ConformanceDocuments.FreezeContext(new QualificationContextDefinition(profile.Sha256,
            ConformanceBindings.CaptureHarnesses(scenarios), scenarios.Select(ConformanceBindings.DescribeScenario).ToArray(),
            datasets, seeds, thresholds, rules, new[] { new FingerprintComponent("observed-environment",
                ConformanceBindings.HashText(ConformanceBindings.CaptureEnvironmentJson())) }));
        facility.Freeze(profile, frozenCandidate, context);
        WriteJson(root, "frozen-profile.json", profile);
        WriteJson(root, "frozen-candidate.json", frozenCandidate);
        WriteJson(root, "frozen-context.json", context);
        var bindings = new ConformanceBindings(root, files, Path.Combine(root, "candidate"));
        var initial = facility.Aggregate(frozenCandidate.Sha256, context.Sha256, QualificationLayer.Provider);
        if (initial.Gates.Any(gate => gate.Outcome != ConformanceOutcome.NotRun))
            throw new InvalidOperationException("CameraConformanceInitialGateNotRunRequired");
        var records = new List<TestExecutionRecord>();
        foreach (var scenario in scenarios)
        {
            var record = await facility.ExecuteAsync(new ConformanceExecutionRequest(frozenCandidate.Sha256,
                context.Sha256, scenario.TestId, bindings, inputs)).ConfigureAwait(false);
            records.Add(record);
            var rawDirectory = Path.Combine(root, "raw", scenario.TestId);
            Directory.CreateDirectory(rawDirectory);
            foreach (var output in record.Outputs)
                File.WriteAllBytes(Path.Combine(rawDirectory, output.Name + ".json"), facility.ReadArtifact(output.Sha256));
            Console.WriteLine($"{scenario.TestId} {record.Outcome} {record.Observed} {record.ReasonCode}");
        }
        var aggregate = facility.Aggregate(frozenCandidate.Sha256, context.Sha256, QualificationLayer.Provider);
        var report = new ProbeReport("camera-provider-conformance-v1", revision, CameraConformanceSuite.Version,
            profile.Sha256, frozenCandidate.Sha256, context.Sha256, factory.Declaration.ContentHash,
            false, "NotRun", QualificationStatus, ScopeLimitations,
            CameraConformanceSuite.Cases.Select(definition => new ProbeCase(definition.TestId, definition.RequirementId,
                definition.SourceReference, records.Single(record => record.TestExecutionId ==
                    aggregate.Gates.Single(gate => gate.TestId == definition.TestId).TestExecutionId))).ToArray());
        WriteJson(root, "report.json", report);
        ValidateExpectedVirtualReport(report);
        Console.WriteLine("V122-P01 evidence-run PASS cases=15 publicContractPass=12 instrumentationBlocked=3 hardwareQualification=NotRun canIssueQualification=false");
    }

    private static void Query(string root, string revision)
    {
        var report = JsonSerializer.Deserialize<ProbeReport>(File.ReadAllBytes(Path.Combine(root, "report.json")))
            ?? throw new InvalidOperationException("CameraConformanceReportMissing");
        if (report.SourceRevision != revision) throw new InvalidOperationException("CameraConformanceRevisionMismatch");
        ValidateExpectedVirtualReport(report);
        var frozenProfile = JsonSerializer.Deserialize<FrozenConformanceDocument>(File.ReadAllBytes(Path.Combine(root, "frozen-profile.json")))
            ?? throw new InvalidOperationException("CameraConformanceProfileMissing");
        var profile = ConformanceDocuments.ReadProfile(frozenProfile);
        if (frozenProfile.Sha256 != report.ProfileHash)
            throw new InvalidOperationException("CameraConformanceProfileBindingMismatch");
        var frozenCandidate = JsonSerializer.Deserialize<FrozenConformanceDocument>(File.ReadAllBytes(Path.Combine(root, "frozen-candidate.json")))
            ?? throw new InvalidOperationException("CameraConformanceCandidateMissing");
        var candidate = ConformanceDocuments.ReadCandidate(frozenCandidate);
        if (frozenCandidate.Sha256 != report.CandidateHash || candidate.SourceRevision != report.SourceRevision)
            throw new InvalidOperationException("CameraConformanceCandidateBindingMismatch");
        var frozenContext = JsonSerializer.Deserialize<FrozenConformanceDocument>(File.ReadAllBytes(Path.Combine(root, "frozen-context.json")))
            ?? throw new InvalidOperationException("CameraConformanceContextMissing");
        var context = ConformanceDocuments.ReadContext(frozenContext);
        if (frozenContext.Sha256 != report.ContextHash || context.ProfileHash != report.ProfileHash ||
            context.Datasets.Single(value => value.Name == CameraConformanceSuite.FixtureInputName).Sha256 != report.FixtureHash)
            throw new InvalidOperationException("CameraConformanceContextBindingMismatch");
        var paths = Directory.EnumerateFiles(Path.Combine(root, "keys"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "executions.sqlite*")).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var before = paths.Select(ConformanceBindings.HashFile).ToArray();
        using (var query = new ConformanceQuery(Options(root)))
        {
            var executions = query.GetExecutions(0, 200);
            if (executions.Count != report.Cases.Length) throw new InvalidOperationException("CameraConformanceRecordCountMismatch");
            foreach (var item in report.Cases)
            {
                var actual = executions.Single(value => value.Reservation.TestId == item.TestId);
                if (actual.Record is null || actual.Record.TestExecutionId != item.Record.TestExecutionId ||
                    JsonSerializer.Serialize(actual.Record) != JsonSerializer.Serialize(item.Record) ||
                    actual.Reservation.CandidateHash != report.CandidateHash || actual.Reservation.ContextHash != report.ContextHash ||
                    actual.Reservation.ProfileHash != report.ProfileHash || !actual.Reservation.RequirementIds.Contains(item.RequirementId) ||
                    actual.Reservation.Inputs.Single(value => value.Name == CameraConformanceSuite.FixtureInputName).Sha256 != report.FixtureHash)
                    throw new InvalidOperationException("CameraConformanceRecordBindingMismatch");
                if (profile.Requirements.Single(value => value.RequirementId == item.RequirementId).SourceReference != item.SourceReference)
                    throw new InvalidOperationException("CameraConformanceRequirementBindingMismatch");
                foreach (var artifact in actual.Record.Outputs.Concat(actual.Reservation.Inputs))
                {
                    var bytes = query.ReadArtifact(artifact.Sha256);
                    if (Convert.ToHexString(SHA256.HashData(bytes)) != artifact.Sha256 || bytes.Length != artifact.Length)
                        throw new InvalidOperationException("CameraConformanceArtifactMismatch");
                }
                foreach (var output in actual.Record.Outputs)
                {
                    var raw = File.ReadAllBytes(Path.Combine(root, "raw", item.TestId, output.Name + ".json"));
                    if (raw.Length != output.Length || Convert.ToHexString(SHA256.HashData(raw)) != output.Sha256)
                        throw new InvalidOperationException("CameraConformanceRawExportMismatch");
                }
            }
            if (query.Aggregate(report.CandidateHash, report.ContextHash, QualificationLayer.Provider).CanIssueQualification)
                throw new InvalidOperationException("CameraConformanceQualificationBoundaryViolation");
        }
        if (!before.SequenceEqual(paths.Select(ConformanceBindings.HashFile)))
            throw new InvalidOperationException("CameraConformanceReadOnlyChangedEvidence");
        Console.WriteLine("V122-P02 independent-query PASS immutableRecords=15 artifactsVerified=true ledgerUnchanged=true hardwareQualification=NotRun");
    }

    private static void ValidateExpectedVirtualReport(ProbeReport report)
    {
        if (!report.Cases.Select(value => value.TestId).SequenceEqual(CameraConformanceSuite.Cases.Select(value => value.TestId)) ||
            report.Schema != "camera-provider-conformance-v1" || report.SuiteVersion != CameraConformanceSuite.Version ||
            report.QualificationStatus != QualificationStatus || report.ScopeLimitations != ScopeLimitations ||
            report.CanIssueQualification || report.HardwareQualification != "NotRun")
            throw new InvalidOperationException("CameraConformanceReportScopeInvalid");
        foreach (var item in report.Cases)
        {
            var expected = string.CompareOrdinal(item.TestId, "V122-C13") < 0 ? ConformanceOutcome.Pass : ConformanceOutcome.Blocked;
            if (item.Record.Outcome != expected)
                throw new InvalidOperationException($"CameraConformanceUnexpectedOutcome:{item.TestId}:{item.Record.Outcome}:{item.Record.Observed}");
        }
    }

    private static ReleaseCandidateDefinition CreateCandidate(string root, string revision, List<ConformanceFileBinding> files)
    {
        var assemblies = new[] { typeof(ICameraProvider).Assembly, typeof(ConformanceFacility).Assembly,
            typeof(VirtualCameraProvider).Assembly };
        var packages = assemblies.Select(assembly => CopyBinding(root, files, "candidate/Packages",
            assembly.GetName().Name!, assembly.Location, "candidate/packages/" + Path.GetFileName(assembly.Location))).ToArray();
        var locks = new[]
        {
            CopyBinding(root, files, "candidate/DependencyLocks", "consumer-deps", typeof(Program).Assembly.Location.Replace(".dll", ".deps.json"), "candidate/dependencies/consumer.deps.json"),
            CopyBinding(root, files, "candidate/DependencyLocks", "consumer-lock", Path.Combine(AppContext.BaseDirectory, "packages.lock.json"), "candidate/dependencies/packages.lock.json")
        };
        var build = new[] { WriteBinding(root, files, "candidate/BuildConfiguration", "build-configuration", "candidate/build.json",
            JsonSerializer.SerializeToUtf8Bytes(new { sourceRevision = revision, targetFramework = "net6.0",
                consumerAssemblyHash = ConformanceBindings.HashFile(typeof(Program).Assembly.Location), configuration = "Release" })) };
        var manifest = WriteBinding(root, files, "candidate/EmbeddedAssets", "candidate-file-manifest", "candidate-manifest.json",
            Encoding.UTF8.GetBytes(ConformanceBindings.CaptureCandidateManifest(Path.Combine(root, "candidate"))));
        return new ReleaseCandidateDefinition(revision, Array.Empty<FingerprintComponent>(), Array.Empty<FingerprintComponent>(),
            packages, Array.Empty<FingerprintComponent>(), new[] { manifest }, locks, build);
    }

    private static void LoadManagedClosure(params Assembly[] roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>(roots);
        while (queue.TryDequeue(out var assembly))
        {
            if (!seen.Add(assembly.FullName!)) continue;
            foreach (var name in assembly.GetReferencedAssemblies()) queue.Enqueue(Assembly.Load(name));
        }
    }

    private static ConformanceLedgerOptions Options(string root) => new(Path.Combine(root, "executions.sqlite"),
        Path.Combine(root, "keys"), KeyName) { MaxEntries = 10000, MaxTotalBytes = 128L * 1024 * 1024 };

    private static FingerprintComponent CopyBinding(string root, List<ConformanceFileBinding> files,
        string category, string name, string source, string relative) =>
        WriteBinding(root, files, category, name, relative, File.ReadAllBytes(source));

    private static FingerprintComponent WriteBinding(string root, List<ConformanceFileBinding> files,
        string category, string name, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes);
        files.Add(new ConformanceFileBinding(category, name, path));
        return new FingerprintComponent(name, ConformanceBindings.HashFile(path));
    }
    private static void WriteJson<T>(string root, string name, T value) => File.WriteAllBytes(Path.Combine(root, name), JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private sealed record ProbeCase(string TestId, string RequirementId, string SourceReference, TestExecutionRecord Record);
    private sealed record ProbeReport(string Schema, string SourceRevision, string SuiteVersion, string ProfileHash,
        string CandidateHash, string ContextHash, string FixtureHash, bool CanIssueQualification,
        string HardwareQualification, string QualificationStatus, string ScopeLimitations, ProbeCase[] Cases);
}
