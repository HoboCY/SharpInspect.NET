using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CameraSetupContractTests
{
    [Fact]
    public void V117_A01_ExtensionDependencyChangesDraftIdentityAndPortability()
    {
        var common = Draft();
        var extension = Extension();
        var providerBound = Draft(extension);
        Assert.True(common.IsCameraConfigurationPortable);
        Assert.False(providerBound.IsCameraConfigurationPortable);
        Assert.False(extension.IsPortable);
        Assert.NotEqual(common.ContentHash, providerBound.ContentHash);
        Assert.Equal(common.Configuration.ContentHash, providerBound.Configuration.ContentHash);
        Assert.Equal(common.Camera, providerBound.Camera);
        Assert.NotEqual(providerBound.ContentHash, Draft(Extension(providerVersion: "2")).ContentHash);
        Assert.NotEqual(providerBound.ContentHash, Draft(Extension(contractVersion: "2")).ContentHash);
        Assert.NotEqual(providerBound.ContentHash, Draft(Extension(configurationHash: new string('B', 64))).ContentHash);
        foreach (var provider in new[] { Provider(id: "Other.Provider"), Provider(package: "Other.Package"),
            Provider(adapterVersion: "2") })
            Assert.NotEqual(providerBound.ContentHash, Draft(new CameraProviderExtensionRequirement(provider,
                extension.ContractId, extension.ContractVersion, extension.ConfigurationContentHash)).ContentHash);
        Assert.NotEqual(providerBound.ContentHash, Draft(new CameraProviderExtensionRequirement(Provider(),
            "Camera.OtherExtension", extension.ContractVersion, extension.ConfigurationContentHash)).ContentHash);
        Assert.Equal(providerBound.ContentHash, Draft(Extension()).ContentHash);
    }

    [Fact]
    public void V117_A02_BindingIdentityIncludesAllProviderAndStableDeviceFields()
    {
        var target = new CameraBindingTarget(Provider(), "Virtual:One");
        var variants = new[] {
            new CameraBindingTarget(Provider(id: "Other.Provider"), "Virtual:One"),
            new CameraBindingTarget(Provider(version: "2"), "Virtual:One"),
            new CameraBindingTarget(Provider(package: "Other.Package"), "Virtual:One"),
            new CameraBindingTarget(Provider(adapterVersion: "2"), "Virtual:One"),
            new CameraBindingTarget(Provider(), "Virtual:Two") };
        Assert.All(variants, variant => Assert.NotEqual(target.ContentHash, variant.ContentHash));
        Assert.Equal(target.ContentHash, new CameraBindingTarget(Provider(), "Virtual:One").ContentHash);
        Assert.Throws<ArgumentException>(() => new CameraBindingTarget(Provider(), "../../device"));
    }

    [Fact]
    public void V117_A03_ProviderExtensionRequiresExactBoundedIdentityAndContentHash()
    {
        Assert.Throws<ArgumentException>(() => Extension(configurationHash: string.Empty));
        Assert.Throws<ArgumentException>(() => Extension(configurationHash: new string('G', 64)));
        Assert.Throws<ArgumentException>(() => Extension(configurationHash: new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new CameraProviderExtensionRequirement(Provider(),
            "../../extension", "1", new string('A', 64)));
    }

    [Fact]
    public void V117_A04_SetupViewCannotPresentHalfAnEffectiveConfiguration()
    {
        var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
            CameraConnectionState.Closed, CameraConfigurationState.Unknown,
            CameraAcquisitionState.Stopped, new FrameTimePoint(DateTimeOffset.UtcNow, 0));
        Assert.Throws<ArgumentException>(() => new CameraSetupSnapshot("TopCamera", null, health,
            differences: new[] { new CameraConfigurationDifference(CameraNumericSetting.GainDb, 1, 2) }));
        var request = Draft().Camera;
        var effective = new EffectiveCameraConfiguration(request.ProductionAcquisitionMode, request.ExposureTimeUs,
            request.GainDb, request.RegionOfInterest, request.PixelFormat, request.ValidBits,
            request.AcquisitionTimeoutMs, request.TriggerDelayUs, request.WhiteBalanceRgb);
        Assert.Throws<ArgumentException>(() => new CameraSetupSnapshot("TopCamera", null, health,
            effective: effective));
        var failed = new CameraSetupSnapshot("TopCamera", null, health, request);
        Assert.False(failed.ProductionReady);
        Assert.True(failed.RequiresRecipeActivation);
        Assert.Null(failed.Effective);
        Assert.Empty(failed.Differences);
    }

    [Fact]
    public void V117_A05_CommonOnlyDraftRetainsTicket16PackagedHash()
    {
        // Obtained from the independently packed T16 consumer Abstractions assembly,
        // artifacts ticket16/20260908-102442-231, before extension metadata existed.
        Assert.Equal("CAEAF0D417F8338BF7B82F18DB885677F3D0FA5638A03043E9E82FF7AB37FBE8", Draft().ContentHash);
    }

    private static CameraProviderIdentity Provider(string id = "Camera.Provider", string version = "1",
        string package = "Camera.Package", string adapterVersion = "1") => new(id, version, package, adapterVersion);

    private static CameraProviderExtensionRequirement Extension(string providerVersion = "1",
        string contractVersion = "1", string? configurationHash = null) =>
        new(Provider(version: providerVersion), "Camera.FixedGamma", contractVersion,
            configurationHash ?? new string('A', 64));

    private static RecipeDraftContent Draft(CameraProviderExtensionRequirement? extension = null)
    {
        var schema = new AlgorithmConfigurationSchema("CameraSetup.Config", "1", new[] {
            new AlgorithmFieldDefinition("threshold", AlgorithmScalarType.Int64, "level", true) });
        var reference = new RecipeContractReference("Result", "1", new string('A', 64));
        return new("CameraSetupRecipe", "相机配置草稿",
            new(new("CameraSetup.Algorithm", "1"), schema, reference, reference),
            AlgorithmConfigurationSnapshot.Create(schema, new[] {
                new AlgorithmConfigurationEntry("threshold", "level", AlgorithmScalarValue.FromInt64(3)) }),
            "TopCamera", new(ProductionAcquisitionMode.SoftwareTrigger, 100, 0,
                new(0, 0, 64, 48), VisionPixelFormat.Mono8, null, 500, 0, null),
            TimeSpan.FromMilliseconds(100), null, null, cameraProviderExtension: extension);
    }
}
