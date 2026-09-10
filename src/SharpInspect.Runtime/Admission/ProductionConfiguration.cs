using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SharpInspect.Runtime.Admission;

// The target identity contains production bindings only. Test harnesses, execution IDs,
// approvals and qualification timestamps never participate in the target fingerprint.
internal enum ProductionConfigurationBinding
{
    Station, Installation, TargetHardware, Storage, OperatingSystem, PowerPlan,
    DotNetRuntime, FrameworkCandidate, BuildConfiguration, ProviderCandidate,
    VendorRuntime, Camera, CameraConfiguration, PlcEndpoint, PlcPolicy,
    ReleasedRecipe, Algorithm, Model, ImageSpecification, StoreProfile,
    EvidencePolicy, LoggingPolicy, DiagnosticPolicy, AlarmPolicy, IdentityPolicy,
    BackupPolicy, DeploymentPolicy, StartupPolicy, PerformanceContract,
    ConformanceProfile, UiWorkload, Calibration, PartIdentity, PlcResultContract
}

internal sealed class ProductionConfiguration
{
    private static readonly ProductionConfigurationBinding[] Required =
        Enum.GetValues<ProductionConfigurationBinding>();
    private static readonly ProductionConfigurationBinding[] ProviderBindings =
    {
        ProductionConfigurationBinding.ProviderCandidate, ProductionConfigurationBinding.VendorRuntime,
        ProductionConfigurationBinding.Camera, ProductionConfigurationBinding.CameraConfiguration,
        ProductionConfigurationBinding.OperatingSystem, ProductionConfigurationBinding.DotNetRuntime,
        ProductionConfigurationBinding.ImageSpecification
    };

    internal ProductionConfiguration(IReadOnlyDictionary<ProductionConfigurationBinding, string> bindings)
    {
        if (bindings is null || bindings.Count > Required.Length)
            throw new ArgumentException("ProductionConfigurationInvalid", nameof(bindings));
        var copied = new SortedDictionary<ProductionConfigurationBinding, string>();
        foreach (var pair in bindings)
        {
            if (!Enum.IsDefined(pair.Key)) throw new ArgumentException("ProductionConfigurationBindingInvalid");
            copied.Add(pair.Key, ProductionAdmissionCanonical.RequireHash(pair.Value));
        }
        Bindings = new ReadOnlyDictionary<ProductionConfigurationBinding, string>(copied);
        MissingBindings = Array.AsReadOnly(Required.Where(key => !copied.ContainsKey(key)).ToArray());
        ObservedBindingsHash = Hash("production-observed-bindings-v1", copied.Keys);
        FrameworkFingerprint = Get(ProductionConfigurationBinding.FrameworkCandidate);
        ProfileHash = Get(ProductionConfigurationBinding.ConformanceProfile);
        ProviderFingerprint = ProviderBindings.All(copied.ContainsKey)
            ? Hash("production-provider-target-v1", ProviderBindings) : null;
        // The V1 target uses every material binding. An absent model/calibration requirement
        // needs its governed no-requirement content hash; absence is never inferred as optional.
        PerformanceFingerprint = MissingBindings.Count == 0
            ? Hash("production-performance-target-v1", Required) : null;
        StationAcceptanceFingerprint = MissingBindings.Count == 0
            ? Hash("production-station-target-v1", Required) : null;
    }

    internal IReadOnlyDictionary<ProductionConfigurationBinding, string> Bindings { get; }
    internal IReadOnlyList<ProductionConfigurationBinding> MissingBindings { get; }
    internal string ObservedBindingsHash { get; }
    internal string? FrameworkFingerprint { get; }
    internal string? ProviderFingerprint { get; }
    internal string? PerformanceFingerprint { get; }
    internal string? StationAcceptanceFingerprint { get; }
    internal string? ProfileHash { get; }
    internal string? Get(ProductionConfigurationBinding binding) => Bindings.TryGetValue(binding, out var value) ? value : null;

    private string Hash(string kind, IEnumerable<ProductionConfigurationBinding> keys) =>
        ProductionAdmissionCanonical.Hash(kind, keys.OrderBy(key => (int)key)
            .SelectMany(key => new[] { ((int)key).ToString(CultureInfo.InvariantCulture), Bindings[key] }).ToArray());
}

internal static class ProductionAdmissionCanonical
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string RequireHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("ProductionAdmissionHashInvalid");
        return value;
    }
    internal static string RequireId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(c =>
                !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_')))
            throw new ArgumentException("ProductionAdmissionIdentifierInvalid");
        return value;
    }
    internal static byte[] Encode(string kind, params string?[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        Write(kind);
        writer.Write(values.Length);
        foreach (var value in values) Write(value);
        writer.Flush();
        return stream.ToArray();

        void Write(string? value)
        {
            if (value is null) { writer.Write(-1); return; }
            var bytes = Utf8.GetBytes(value);
            if (bytes.Length > 65536) throw new ArgumentException("ProductionAdmissionFieldTooLarge");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
    }
    internal static string Hash(string kind, params string?[] values) =>
        Convert.ToHexString(SHA256.HashData(Encode(kind, values)));
}
