using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Production;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.Runtime.Admission;

/// <summary>Fixed sources for production target bindings; no caller-supplied gate or hash dictionary.</summary>
internal static class ProductionConfigurationBuilder
{
    private static readonly ConcurrentDictionary<Assembly, string> AssemblyHashes = new();

    internal static ProductionConfiguration Build(ProductionInspectionOptions options,
        ProductionStoreOptions store, ProductionDeploymentManifest? manifest,
        RecipeActivationSnapshot? activation, TraceStoragePolicySnapshot? trace,
        string? installationKeyId, string? providerBinaryHash, string? algorithmBinaryHash,
        PartIdentityBindingObservation? partIdentity = null)
    {
        var values = new Dictionary<ProductionConfigurationBinding, string>();
        void Add(ProductionConfigurationBinding key, string? value)
        { if (value is not null) values.Add(key, value); }
        string Hash(string kind, params string?[] parts) => ProductionAdmissionCanonical.Hash(kind, parts);
        string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        Add(ProductionConfigurationBinding.Station, Hash("production-station-v1", options.StationId,
            store.LocalIdentity?.StationId, store.AuditIntegrityPolicy?.StationId));
        if (!string.IsNullOrEmpty(installationKeyId))
            Add(ProductionConfigurationBinding.Installation, Hash("production-installation-v1", installationKeyId));
        Add(ProductionConfigurationBinding.TargetHardware, Hash("production-machine-v1", Environment.MachineName,
            RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            Number(Environment.ProcessorCount), Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")));
        Add(ProductionConfigurationBinding.OperatingSystem, Hash("production-os-v1", RuntimeInformation.OSDescription,
            Environment.OSVersion.VersionString));
        Add(ProductionConfigurationBinding.DotNetRuntime, Hash("production-clr-v1", RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier, AssemblyHash(typeof(object).Assembly)));
        Add(ProductionConfigurationBinding.FrameworkCandidate, Hash("production-framework-candidate-v1",
            AssemblyHash(typeof(StationRuntime).Assembly), AssemblyHash(typeof(IStationRuntime).Assembly)));
        Add(ProductionConfigurationBinding.BuildConfiguration, Hash("production-build-v1",
            typeof(StationRuntime).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            typeof(StationRuntime).Assembly.GetCustomAttribute<DebuggableAttribute>()?.DebuggingFlags.ToString()));
        Add(ProductionConfigurationBinding.PowerPlan, ReadPowerPlanHash());
        try
        {
            var path = Path.GetFullPath(store.DatabasePath);
            var volume = new DriveInfo(Path.GetPathRoot(path)!);
            Add(ProductionConfigurationBinding.Storage, Hash("production-storage-target-v1", path,
                volume.Name, volume.DriveFormat, volume.VolumeLabel, Number(volume.TotalSize)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        Add(ProductionConfigurationBinding.PlcEndpoint, options.Profile.EndpointBindingHash);
        Add(ProductionConfigurationBinding.PlcPolicy, options.Profile.ContentHash);
        if (store.ProductionInspections is { } core && store.TraceStoragePolicies is { } storagePolicy)
            Add(ProductionConfigurationBinding.StoreProfile, store.PartIdentities is null ? Hash("production-store-profile-v1",
                core.BindingHash, storagePolicy.BindingHash, store.AuditIntegrityPolicy?.ContentHash,
                Number(store.QueueCapacity), store.CommitTimeout.ToString("c", CultureInfo.InvariantCulture)) :
                Hash("production-store-profile-v2", core.BindingHash, storagePolicy.BindingHash,
                    store.PartIdentities.BindingHash, store.AuditIntegrityPolicy?.ContentHash,
                    Number(store.QueueCapacity), store.CommitTimeout.ToString("c", CultureInfo.InvariantCulture)));
        Add(ProductionConfigurationBinding.AlarmPolicy, store.AlarmPolicy?.ContentHash);
        if (store.ProductionArming is { } arming && values.TryGetValue(ProductionConfigurationBinding.StoreProfile, out var priorStore))
            values[ProductionConfigurationBinding.StoreProfile] = Hash("production-store-profile-v3", priorStore, arming.BindingHash);
        if (store.ImageEvidence is { } images && values.TryGetValue(ProductionConfigurationBinding.StoreProfile, out var imagePrior))
            values[ProductionConfigurationBinding.StoreProfile] = Hash("production-store-profile-v4", imagePrior, images.BindingHash);
        Add(ProductionConfigurationBinding.IdentityPolicy, store.LocalIdentity?.PolicyContentHash);
        if (trace is not null)
            Add(ProductionConfigurationBinding.EvidencePolicy,
                Images.ProductionImageEvidenceBinding.PolicyFingerprint(options, store, trace));
        if (manifest is not null)
        {
            Add(ProductionConfigurationBinding.LoggingPolicy, manifest.Logging.ContentHash);
            Add(ProductionConfigurationBinding.DiagnosticPolicy, manifest.Diagnostics.ContentHash);
            Add(ProductionConfigurationBinding.BackupPolicy, manifest.Backup.ContentHash);
            Add(ProductionConfigurationBinding.StartupPolicy, manifest.Startup.ContentHash);
            Add(ProductionConfigurationBinding.PerformanceContract, manifest.Performance.ContentHash);
            Add(ProductionConfigurationBinding.ConformanceProfile, manifest.Conformance.ContentHash);
            Add(ProductionConfigurationBinding.UiWorkload, manifest.UiWorkload.ContentHash);
            Add(ProductionConfigurationBinding.DeploymentPolicy, manifest.ContentHash);
            try
            {
                Add(ProductionConfigurationBinding.VendorRuntime, Hash("production-vendor-dependencies-v1",
                    manifest.VendorDependencyPaths.SelectMany(path => new[] { path, FileHash(path) }).ToArray()));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        if (activation is not null)
        {
            var content = activation.Release.Source.Content;
            var camera = activation.CameraSetup;
            Add(ProductionConfigurationBinding.ProviderCandidate, providerBinaryHash);
            Add(ProductionConfigurationBinding.Camera, camera.Binding?.Target.ContentHash);
            Add(ProductionConfigurationBinding.CameraConfiguration, RecipeActivationValidation.CameraHash(camera));
            Add(ProductionConfigurationBinding.ReleasedRecipe, activation.Release.ContentHash);
            if (algorithmBinaryHash is not null)
                Add(ProductionConfigurationBinding.Algorithm, Hash("production-algorithm-binding-v1", algorithmBinaryHash,
                content.Algorithm.Algorithm.Id, content.Algorithm.Algorithm.Version,
                content.Configuration.ContentHash, content.Algorithm.ConfigurationSchema.ContentHash,
                content.Algorithm.ResultSchema.ContentHash, content.Algorithm.OverlayContract.ContentHash,
                activation.AlgorithmExecutionPolicy.ContentHash));
            Add(ProductionConfigurationBinding.Model, Hash("production-recipe-assets-v1",
                content.AssetRequirements.OrderBy(asset => asset.Role, StringComparer.Ordinal).SelectMany(asset =>
                    new[] { asset.Kind.ToString(), asset.Role, asset.Contract.ContentHash }).ToArray()));
            if (camera.Effective is { } effective)
                Add(ProductionConfigurationBinding.ImageSpecification, Hash("production-image-specification-v1",
                    effective.PixelFormat.ToString(), effective.ValidBits?.ToString(CultureInfo.InvariantCulture),
                    Number(effective.RegionOfInterest.OffsetX), Number(effective.RegionOfInterest.OffsetY),
                    Number(effective.RegionOfInterest.Width), Number(effective.RegionOfInterest.Height),
                    Number(activation.FramePoolCapacity), Number(activation.FramePoolMaximumBytes)));
            Add(ProductionConfigurationBinding.Calibration, Hash("production-calibration-bindings-v1",
                activation.CalibrationBindings.OrderBy(binding => binding.RequirementContentHash, StringComparer.Ordinal)
                    .Select(binding => binding.ContentHash).ToArray()));
            Add(ProductionConfigurationBinding.PartIdentity,
                content.PartIdentityRequirement?.Mode == PartIdentityRequirementMode.None
                    ? content.PartIdentityRequirement.ContentHash
                    : partIdentity?.RequirementHash == content.PartIdentityRequirement?.ContentHash
                        ? partIdentity?.StaticHash : null);
            Add(ProductionConfigurationBinding.PlcResultContract, activation.PlcResultContract.ContentHash);
        }
        return new(values);
    }

    internal static string AssemblyHash(Assembly assembly) => AssemblyHashes.GetOrAdd(assembly,
        value => ProductionAdmissionCanonical.Hash("production-loaded-assembly-v1", value.FullName,
            value.ManifestModule.ModuleVersionId.ToString("D"), FileHash(value.Location)));

    private static string FileHash(string path)
    {
        using var input = File.OpenRead(path);
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(input));
    }

    private static string? ReadPowerPlanHash()
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out pointer) != 0 || pointer == IntPtr.Zero) return null;
            return ProductionAdmissionCanonical.Hash("production-power-plan-v1", Marshal.PtrToStructure<Guid>(pointer).ToString("D"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
        finally { if (pointer != IntPtr.Zero) _ = LocalFree(pointer); }
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
