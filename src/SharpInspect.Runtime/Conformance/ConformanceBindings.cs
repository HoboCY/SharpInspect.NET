using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Conformance;

public sealed record ConformanceFileBinding(string Category, string Name, string Path);

internal sealed class ConformanceBindingMismatchException : InvalidOperationException
{
    public ConformanceBindingMismatchException(string code, object expected, object actual) : base(code) =>
        EvidenceJson = JsonSerializer.Serialize(new { Schema = "binding-mismatch-v1", Code = code,
            ExpectedManifestHash = ConformanceBindings.HashText(JsonSerializer.Serialize(expected)),
            ActualManifestHash = ConformanceBindings.HashText(JsonSerializer.Serialize(actual)) });
    public string EvidenceJson { get; }
}

/// <summary>Actual local files used by this execution, checked against both frozen manifests.</summary>
public sealed class ConformanceBindings
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    // Identify the CLR's own anonymous DynamicMethod container by object identity, not by a spoofable name.
    // JSON/reflection use this platform container; user-defined dynamic assemblies remain rejected.
    private static readonly Assembly RuntimeDynamicMethods = new System.Reflection.Emit.DynamicMethod(
        "SharpInspectConformancePlatformIdentity", typeof(void), Type.EmptyTypes).Module.Assembly;
    private readonly ConformanceFileBinding[] _files;
    public ConformanceBindings(string rootDirectory, IReadOnlyList<ConformanceFileBinding> files, string candidateDirectory)
    {
        if (!Path.IsPathFullyQualified(rootDirectory) || files.Count > 512)
            throw new ArgumentException("ConformanceBindingsInvalid");
        RootDirectory = Path.GetFullPath(rootDirectory);
        CandidateDirectory = Path.GetFullPath(candidateDirectory);
        if (!CandidateDirectory.StartsWith(RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("ConformanceCandidateRootOutsideBindings");
        _files = files.ToArray();
        if (_files.DistinctBy(f => (f.Category, f.Name)).Count() != _files.Length)
            throw new ArgumentException("ConformanceBindingDuplicate");
    }
    public string RootDirectory { get; }
    public string CandidateDirectory { get; }

    /// <summary>All files in the selected candidate directory, including unlisted additions, are bound.</summary>
    public static string CaptureCandidateManifest(string candidateDirectory)
    {
        if (!Path.IsPathFullyQualified(candidateDirectory) || !Directory.Exists(candidateDirectory))
            throw new ArgumentException("ConformanceCandidateDirectoryRequired");
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(candidateDirectory));
        var files = new List<object>();
        long totalLength = 0;
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            if (++visited > 128 || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("ConformanceCandidateDirectoryInvalid");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("ConformanceCandidateDirectoryInvalid");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (pending.Count + visited >= 128) throw new InvalidOperationException("ConformanceCandidateDirectoryInvalid");
                    pending.Push(entry); continue;
                }
                var length = new FileInfo(entry).Length;
                totalLength = checked(totalLength + length);
                if (files.Count >= 256 || totalLength > 256L * 1024 * 1024)
                    throw new InvalidOperationException("ConformanceCandidateManifestCapacity");
                files.Add(new { Path = Path.GetRelativePath(candidateDirectory, entry).Replace('\\', '/'), Length = length, Sha256 = HashFile(entry) });
            }
        }
        return JsonSerializer.Serialize(new { Schema = "candidate-file-manifest-v1",
            Files = files.OrderBy(f => JsonSerializer.Serialize(f), StringComparer.Ordinal).ToArray() });
    }

    public static string CaptureEnvironmentJson() => JsonSerializer.Serialize(new
    {
        Schema = "development-environment-v1", OS = RuntimeInformation.OSDescription,
        Runtime = RuntimeInformation.FrameworkDescription, RuntimeVersion = Environment.Version.ToString(),
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        LogicalProcessors = Environment.ProcessorCount, Is64BitProcess = Environment.Is64BitProcess,
        CoreLibraryHash = HashFile(typeof(object).Assembly.Location),
        HardwareQualification = "NotRun", CommandContract = "explicit-in-process-development-scenario-v1"
    });

    public static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(value)));

    public static FingerprintComponent DescribeScenario(IConformanceScenario scenario)
    {
        var type = scenario.GetType();
        if (type.Assembly.IsDynamic || string.IsNullOrEmpty(type.Assembly.Location) || string.IsNullOrEmpty(type.FullName))
            throw new InvalidOperationException("ConformanceDynamicHarnessRejected");
        if (!Path.GetFullPath(type.Assembly.Location).StartsWith(Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ConformanceScenarioOutsideApplicationRoot");
        return new FingerprintComponent(scenario.TestId, HashText(JsonSerializer.Serialize(new
        {
            Schema = "scenario-descriptor-v1", scenario.TestId, Type = type.FullName,
            AssemblyIdentity = type.Assembly.FullName, AssemblyHash = HashFile(type.Assembly.Location),
            DeclaredConfigurationHash = scenario.ScenarioHash
        })));
    }

    public static string HashFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("ConformanceLocalFileRequired");
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) &&
                (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("ConformanceBindingReparsePoint");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 256 * 1024 * 1024) throw new InvalidOperationException("ConformanceBindingTooLarge");
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(stream));
    }

    /// <summary>Loaded application assemblies, including the facility and explicitly registered scenarios.</summary>
    public static IReadOnlyList<FingerprintComponent> CaptureHarnesses(IEnumerable<IConformanceScenario> scenarios)
    {
        var scenarioAssemblies = scenarios.Select(s => s.GetType().Assembly).ToArray();
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !ReferenceEquals(a, RuntimeDynamicMethods)).ToArray();
        if (loaded.Any(a => a.IsDynamic || string.IsNullOrEmpty(a.Location)))
            throw new InvalidOperationException("ConformanceDynamicHarnessRejected");
        var sharedRoot = Path.GetDirectoryName(Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory()
            .TrimEnd(Path.DirectorySeparatorChar)))!;
        var platformRoots = new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App" }
            .Select(name => Path.Combine(sharedRoot, name) + Path.DirectorySeparatorChar).ToArray();
        // Dependencies outside the application root remain material qualification inputs.
        var required = loaded.Where(a => !platformRoots.Any(root =>
            Path.GetFullPath(a.Location).StartsWith(root, StringComparison.OrdinalIgnoreCase)));
        var assemblies = required.Concat(scenarioAssemblies).Append(typeof(ConformanceBindings).Assembly)
            .Append(typeof(ConformanceOutcome).Assembly).Distinct().ToArray();
        if (assemblies.Length > 128 || assemblies.Any(a => a.IsDynamic || string.IsNullOrEmpty(a.Location)))
            throw new InvalidOperationException("ConformanceDynamicHarnessRejected");
        return Array.AsReadOnly(assemblies.Select(a => new FingerprintComponent(
                (a.GetName().Name ?? throw new InvalidOperationException("ConformanceAssemblyIdentityMissing")) +
                "." + (string.IsNullOrEmpty(a.GetName().CultureName) ? "neutral" : a.GetName().CultureName) + "." +
                HashText(Path.GetFullPath(a.Location).ToUpperInvariant())[..16], HashFile(a.Location)))
            .DistinctBy(a => (a.Name, a.Sha256)).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray());
    }

    internal string Verify(ReleaseCandidateDefinition candidate, QualificationContextDefinition context,
        IReadOnlyList<IConformanceScenario> scenarios)
    {
        var manifest = candidate.EmbeddedAssets.SingleOrDefault(c => c.Name == "candidate-file-manifest")
            ?? throw new InvalidOperationException("ConformanceCandidateManifestMissing");
        var actualManifest = HashText(CaptureCandidateManifest(CandidateDirectory));
        if (manifest.Sha256 != actualManifest)
            throw new ConformanceBindingMismatchException("ConformanceCandidateBytesChanged", manifest.Sha256, actualManifest);
        var expected = new List<(string Category, FingerprintComponent Component)>();
        void Add(string category, IReadOnlyList<FingerprintComponent> values) =>
            expected.AddRange(values.Select(v => (category, v)));
        Add("candidate/PublicApi", candidate.PublicApi); Add("candidate/Schemas", candidate.Schemas);
        Add("candidate/Packages", candidate.Packages); Add("candidate/Migrations", candidate.Migrations);
        Add("candidate/EmbeddedAssets", candidate.EmbeddedAssets); Add("candidate/DependencyLocks", candidate.DependencyLocks);
        Add("candidate/BuildConfiguration", candidate.BuildConfiguration);
        Add("context/Datasets", context.Datasets); Add("context/Seeds", context.Seeds);
        Add("context/Thresholds", context.Thresholds); Add("context/CalculationRules", context.CalculationRules);
        Add("context/EnvironmentConfiguration", context.EnvironmentConfiguration.Where(c => c.Name != "observed-environment").ToArray());
        if (_files.Length != expected.Count) throw new InvalidOperationException("ConformanceFileBindingIncomplete");
        foreach (var item in expected)
        {
            var file = _files.SingleOrDefault(f => f.Category == item.Category && f.Name == item.Component.Name)
                ?? throw new InvalidOperationException("ConformanceFileBindingMissing");
            var absolute = Path.GetFullPath(file.Path);
            if (!absolute.StartsWith(RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ConformanceBindingOutsideRoot");
            if (item.Category.StartsWith("candidate/", StringComparison.Ordinal) &&
                !(item.Category == "candidate/EmbeddedAssets" && item.Component.Name == "candidate-file-manifest") &&
                !absolute.StartsWith(CandidateDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ConformanceCandidateBindingOutsideDirectory");
            if (item.Category.StartsWith("context/", StringComparison.Ordinal) &&
                absolute.StartsWith(CandidateDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ConformanceContextBindingInsideCandidateDirectory");
            var actualHash = HashFile(absolute);
            if (actualHash != item.Component.Sha256)
                throw new ConformanceBindingMismatchException(item.Category.StartsWith("candidate/", StringComparison.Ordinal)
                    ? "ConformanceCandidateBytesChanged" : "ConformanceContextBytesChanged", item.Component.Sha256, actualHash);
        }
        var actualHarnesses = CaptureHarnesses(scenarios);
        if (actualHarnesses.Count != context.Harnesses.Count || actualHarnesses.Any(a =>
                !context.Harnesses.Any(e => e.Name == a.Name && e.Sha256 == a.Sha256)))
            throw new ConformanceBindingMismatchException("ConformanceHarnessChanged", context.Harnesses, actualHarnesses);
        var actualEnvironment = CaptureEnvironmentJson();
        if (!context.EnvironmentConfiguration.Any(c => c.Name == "observed-environment" && c.Sha256 == HashText(actualEnvironment)))
            throw new ConformanceBindingMismatchException("ConformanceEnvironmentChanged", context.EnvironmentConfiguration,
                new FingerprintComponent("observed-environment", HashText(actualEnvironment)));
        if (context.Scenarios.Count != scenarios.Count)
            throw new ConformanceBindingMismatchException("ConformanceScenarioChanged", context.Scenarios,
                scenarios.Select(DescribeScenario).ToArray());
        foreach (var scenario in scenarios)
            if (!context.Scenarios.Contains(DescribeScenario(scenario)))
                throw new ConformanceBindingMismatchException("ConformanceScenarioChanged", context.Scenarios,
                    DescribeScenario(scenario));
        return actualEnvironment;
    }
}
