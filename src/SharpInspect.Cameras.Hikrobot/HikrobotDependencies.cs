using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

using SharpInspect.Abstractions;

namespace SharpInspect.Cameras.Hikrobot;

/// <summary>
/// A read-only snapshot of the local files that could satisfy the Hikrobot MVS
/// dependency.  The snapshot deliberately contains no native handle or device
/// state.  A true file/version observation is also not a qualification result:
/// the production compatibility catalog is kept separately by the provider.
/// </summary>
public sealed class HikrobotDependencyReport
{
    internal HikrobotDependencyReport(
        string platform,
        string architecture,
        IEnumerable<string> candidatePaths,
        string? detectedFileVersion,
        string? peMachine,
        IReadOnlyDictionary<string, bool> sdkComponentsPresent,
        string driverServiceStatus,
        bool productionCompatible,
        CameraProviderAvailability availability,
        string reasonCode)
    {
        Platform = RequireText(platform, nameof(platform));
        Architecture = RequireText(architecture, nameof(architecture));
        ArgumentNullException.ThrowIfNull(candidatePaths);

        var paths = candidatePaths.ToList();
        if (paths.Count == 0 || paths.Any(path => string.IsNullOrWhiteSpace(path) ||
                !Path.IsPathRooted(path)))
            throw new ArgumentException("HikrobotDependencyCandidatePathsInvalid",
                nameof(candidatePaths));
        CandidatePaths = new ReadOnlyCollection<string>(paths);

        DetectedFileVersion = detectedFileVersion;
        PeMachine = peMachine;

        ArgumentNullException.ThrowIfNull(sdkComponentsPresent);
        var components = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in sdkComponentsPresent)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || !components.TryAdd(item.Key, item.Value))
                throw new ArgumentException("HikrobotDependencyComponentsInvalid",
                    nameof(sdkComponentsPresent));
        }
        SdkComponentsPresent = new ReadOnlyDictionary<string, bool>(components);

        DriverServiceStatus = RequireText(driverServiceStatus, nameof(driverServiceStatus));
        if (!Enum.IsDefined(typeof(CameraProviderAvailability), availability))
            throw new ArgumentOutOfRangeException(nameof(availability));
        ProductionCompatible = productionCompatible;
        Availability = availability;
        ReasonCode = RequireText(reasonCode, nameof(reasonCode));
    }

    public string Platform { get; }
    public string Architecture { get; }
    // Absolute candidate paths are retained for the friend-only diagnostic
    // seam.  They are intentionally not public so ordinary report serialization
    // cannot disclose local installation layout.
    internal IReadOnlyList<string> CandidatePaths { get; }
    public int CandidateLocationCount => CandidatePaths.Count;
    public string? DetectedFileVersion { get; }
    public string? PeMachine { get; }
    public IReadOnlyDictionary<string, bool> SdkComponentsPresent { get; }
    public string DriverServiceStatus { get; }
    public bool ProductionCompatible { get; }
    public CameraProviderAvailability Availability { get; }
    public string ReasonCode { get; }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("HikrobotDependencyTextInvalid", parameterName);
        return value;
    }
}

/// <summary>Stable reason codes for local dependency inspection.</summary>
internal static class HikrobotDependencyReasonCodes
{
    internal const string WrongOs = "HikrobotDependencyWrongOS";
    internal const string X86 = "HikrobotDependencyX86";
    internal const string Missing = "HikrobotDependencyMissing";
    internal const string ComponentsMissing = "HikrobotDependencyComponentsMissing";
    internal const string ComponentsInvalid = "HikrobotDependencyComponentsInvalid";
    internal const string Unqualified = "HikrobotDependencyUnqualified";
    internal const string Corrupt = "HikrobotDependencyCorrupt";
    internal const string NativeRequired = "HikrobotDependencyNativeRequired";
    internal const string ArchitectureMismatch = "HikrobotDependencyArchitectureMismatch";
}

/// <summary>
/// File-only MVS dependency inspection.  It never loads a native library,
/// enumerates cameras, queries a driver/service, or accesses a network adapter.
/// </summary>
internal static class HikrobotDependencies
{
    internal const string DriverServiceNotVerified = "NotVerified";
    internal const string MainComponent = "MvCameraControl.dll";
    internal const string GigEComponent = "MVGigEVisionSDK.dll";
    internal const string UsbComponent = "MvUsb3vTL.dll";

    private static readonly string[] RequiredComponents =
    {
        MainComponent,
        GigEComponent,
        UsbComponent
    };

    // V1 intentionally has no production-admitted MVS versions.  This is an
    // internal immutable fact; no public runtime path or override is exposed.
    internal static IReadOnlyList<string> ProductionCompatibilityCatalog { get; } =
        Array.Empty<string>();

    internal static HikrobotDependencyReport Detect(string? explicitPath = null) =>
        Inspect(explicitPath);

    internal static HikrobotDependencyReport Inspect(string? explicitPath = null)
    {
        var candidates = explicitPath is null
            ? DefaultCandidatePaths()
            : new[] { NormalizeAbsolutePath(explicitPath, nameof(explicitPath)) };

        return Inspect(candidates,
            RuntimeInformation.ProcessArchitecture.ToString(),
            OperatingSystem.IsWindows() ? "Windows" : "NonWindows");
    }

    /// <summary>
    /// Deterministic file-test seam.  The platform and process architecture are
    /// test inputs only; this overload is internal and cannot configure the
    /// public provider or its production compatibility catalog.
    /// </summary>
    internal static HikrobotDependencyReport Inspect(
        IEnumerable<string> candidatePaths, string processArchitecture, string platform)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        var candidates = candidatePaths.Select(path => NormalizeAbsolutePath(path,
                nameof(candidatePaths))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException("HikrobotDependencyCandidatePathsInvalid",
                nameof(candidatePaths));
        if (string.IsNullOrWhiteSpace(processArchitecture))
            throw new ArgumentException("HikrobotDependencyArchitectureInvalid",
                nameof(processArchitecture));
        if (string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("HikrobotDependencyPlatformInvalid", nameof(platform));

        var existing = candidates.Where(File.Exists).ToArray();
        var inspections = existing.Select(ReadFile).ToArray();

        // A host may retain stale x86/ARM/managed files beside a later x64
        // install. Prefer a parseable native AMD64 image whose required
        // components are all in that same directory, then a parseable native
        // AMD64 image. A managed AMD64 image remains a useful diagnostic only
        // after native candidates are exhausted. Only then is a non-AMD64 image
        // selected for diagnosis.
        var selectedIndex = SelectCandidate(existing, inspections);

        var selectedPath = selectedIndex < 0 ? null : existing[selectedIndex];
        var selected = selectedIndex < 0 ? default : inspections[selectedIndex];
        var componentInspection = DetectComponents(selectedPath);
        var reason = ResolveReason(platform, processArchitecture, selected, existing.Length,
            componentInspection);
        var availability = ResolveAvailability(reason);

        return new HikrobotDependencyReport(
            platform,
            processArchitecture,
            candidates,
            selected.HasVersion ? selected.FileVersion : null,
            selected.IsPe ? selected.PeMachine : null,
            componentInspection.Present,
            DriverServiceNotVerified,
            productionCompatible: false,
            availability,
            reason);
    }

    private static int SelectCandidate(IReadOnlyList<string> existing,
        IReadOnlyList<FileInspection> inspections)
    {
        if (inspections.Count == 0) return -1;

        for (var index = 0; index < inspections.Count; index++)
        {
            var inspection = inspections[index];
            if (inspection.IsNativeAmd64 && inspection.HasVersion &&
                ComponentsComplete(existing[index])) return index;
        }

        for (var index = 0; index < inspections.Count; index++)
        {
            var inspection = inspections[index];
            if (inspection.IsNativeAmd64 && inspection.HasVersion) return index;
        }

        for (var index = 0; index < inspections.Count; index++)
        {
            var inspection = inspections[index];
            if (inspection.IsNativeAmd64) return index;
        }

        for (var index = 0; index < inspections.Count; index++)
        {
            var inspection = inspections[index];
            if (inspection.IsAmd64) return index;
        }

        // Retain the first existing non-AMD64 or malformed file so its actual
        // machine (when readable) is reported instead of being hidden by a
        // later candidate from another installation root.
        return 0;
    }

    private static string ResolveReason(string platform, string architecture,
        FileInspection selected, int existingFileCount,
        ComponentInspection components)
    {
        if (platform != "Windows") return HikrobotDependencyReasonCodes.WrongOs;
        if (existingFileCount == 0) return HikrobotDependencyReasonCodes.Missing;
        if (!selected.IsPe)
            return HikrobotDependencyReasonCodes.Corrupt;
        if (!selected.IsAmd64)
            return HikrobotDependencyReasonCodes.ArchitectureMismatch;
        if (selected.HasMetadata)
            return HikrobotDependencyReasonCodes.NativeRequired;
        if (!string.Equals(architecture, Architecture.X64.ToString(), StringComparison.Ordinal))
            return HikrobotDependencyReasonCodes.X86;
        if (!selected.HasVersion)
        {
            return HikrobotDependencyReasonCodes.Corrupt;
        }
        if (!components.Present.Values.All(value => value))
            return HikrobotDependencyReasonCodes.ComponentsMissing;
        if (!components.NativeAmd64.Values.All(value => value))
            return HikrobotDependencyReasonCodes.ComponentsInvalid;

        // A locally present and parseable runtime remains outside the empty
        // production catalog.  It is diagnosable, never production-compatible.
        return HikrobotDependencyReasonCodes.Unqualified;
    }

    private static CameraProviderAvailability ResolveAvailability(string reason) => reason switch
    {
        HikrobotDependencyReasonCodes.Missing or
            HikrobotDependencyReasonCodes.ComponentsMissing =>
            CameraProviderAvailability.DependencyMissing,
        HikrobotDependencyReasonCodes.Corrupt or
            HikrobotDependencyReasonCodes.NativeRequired => CameraProviderAvailability.Faulted,
        _ => CameraProviderAvailability.Incompatible
    };

    private static ComponentInspection DetectComponents(string? selectedPath)
    {
        var directories = new List<string>();
        if (selectedPath is not null)
        {
            var selectedDirectory = Path.GetDirectoryName(selectedPath);
            if (!string.IsNullOrWhiteSpace(selectedDirectory)) directories.Add(selectedDirectory);
        }

        var present = new Dictionary<string, bool>(StringComparer.Ordinal);
        var nativeAmd64 = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var component in RequiredComponents)
        {
            var path = directories.Count == 0
                ? null
                : Path.Combine(directories[0], component);
            var exists = path is not null && File.Exists(path);
            present[component] = exists;
            nativeAmd64[component] = exists && ReadFile(path!).IsNativeAmd64;
        }
        return new ComponentInspection(
            new ReadOnlyDictionary<string, bool>(present),
            new ReadOnlyDictionary<string, bool>(nativeAmd64));
    }

    private static FileInspection ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var machine = reader.PEHeaders.CoffHeader.Machine;
            var machineText = machine.ToString();
            var hasMetadata = reader.HasMetadata;
            string? version;
            try
            {
                version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and
                not StackOverflowException)
            {
                // Keep the parseable PE and actual machine in the report even
                // when its optional file-version resource cannot be read.
                version = null;
            }
            var validVersion = !string.IsNullOrWhiteSpace(version);
            return new FileInspection(true, validVersion,
                machine == Machine.Amd64, hasMetadata, machineText,
                validVersion ? version : null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException)
        {
            return new FileInspection(false, false, false, false, null, null);
        }
    }

    private static bool ComponentsComplete(string mainPath)
    {
        return DetectComponents(mainPath).NativeAmd64.Values.All(value => value);
    }

    private static IReadOnlyList<string> DefaultCandidatePaths()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("CommonProgramFiles"),
            Environment.GetEnvironmentVariable("CommonProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var result = new List<string>();
        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            AddCandidate(result, root!, "MVS", "Runtime", "Win64_x64", MainComponent);
            AddCandidate(result, root!, "MVS", "Runtime", "Win32_x86", MainComponent);
            AddCandidate(result, root!, "MVS", MainComponent);
        }

        if (result.Count == 0)
        {
            // Keep the report shape absolute even on a non-Windows host where
            // Windows special-folder roots are unavailable.
            AddCandidate(result, Environment.CurrentDirectory, "MVS", "Runtime",
                "Win64_x64", MainComponent);
        }
        return new ReadOnlyCollection<string>(result);
    }

    private static void AddCandidate(ICollection<string> result, string root,
        params string[] parts)
    {
        var path = NormalizeAbsolutePath(Path.Combine(new[] { root }.Concat(parts).ToArray()),
            nameof(root));
        if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
    }

    private static string NormalizeAbsolutePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(
            "HikrobotDependencyPathInvalid", parameterName);
        try
        {
            var path = Path.GetFullPath(value);
            if (!Path.IsPathRooted(path)) throw new ArgumentException(
                "HikrobotDependencyPathNotAbsolute", parameterName);
            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("HikrobotDependencyPathInvalid", parameterName,
                exception);
        }
    }

    private readonly record struct ComponentInspection(
        IReadOnlyDictionary<string, bool> Present,
        IReadOnlyDictionary<string, bool> NativeAmd64);

    private readonly record struct FileInspection(bool IsPe, bool HasVersion,
        bool IsAmd64, bool HasMetadata, string? PeMachine, string? FileVersion)
    {
        internal bool IsNativeAmd64 => IsAmd64 && !HasMetadata;
    }
}
