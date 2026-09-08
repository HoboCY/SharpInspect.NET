using System.Text.Json.Nodes;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationRequirementTests
{
    [Fact]
    public void V123_R01_EmptyRequirementsPreserveLegacyDraftIdentityAndEncoding()
    {
        var original = Content(null);
        var empty = Content(Array.Empty<CalibrationRequirement>());
        Assert.Equal(original.ContentHash, empty.ContentHash);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(original, out var before, out var reason), reason);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(empty, out var after, out reason), reason);
        Assert.Equal(before!.PayloadJson, after!.PayloadJson);
        Assert.Contains("\"FormatVersion\":1", before.PayloadJson);
        Assert.DoesNotContain("CalibrationRequirements", before.PayloadJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V123_R02_TypedRequirementsAndOptionalProviderExtensionRoundTrip(bool extension)
    {
        var intrinsic = Requirement(CalibrationKind.Intrinsic, "Undistortion");
        var plane = Requirement(CalibrationKind.PlanarHomography, "WorkPlane");
        var source = Content(new[] { plane, intrinsic }, extension);
        var reordered = Content(new[] { intrinsic, plane }, extension);
        Assert.Equal(source.ContentHash, reordered.ContentHash);
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(source, out var document, out var reason), reason);
        Assert.Contains("\"FormatVersion\":3", document!.PayloadJson);
        Assert.True(RecipeDraftStorageCodec.TryDecodeContent(document.PayloadJson, document.PayloadHash,
            out var decoded, out reason), reason);
        Assert.Equal(source.ContentHash, decoded!.ContentHash);
        Assert.Equal(new[] { intrinsic, plane }, decoded.CalibrationRequirements);
        Assert.Equal(source.CameraProviderExtension, decoded.CameraProviderExtension);
        Assert.Empty(decoded.AssetRequirements);
        Assert.NotEqual(source.ContentHash, Content(new[] { intrinsic }, extension).ContentHash);
    }

    [Fact]
    public void V123_R03_RequirementRoleUniquenessCapacityAndPolicyAreExact()
    {
        var requirement = Requirement(CalibrationKind.Intrinsic, "Undistortion");
        Assert.Throws<ArgumentException>(() => Content(new[] { requirement, requirement }));
        Assert.Throws<ArgumentException>(() => Content(new[] { new CalibrationRequirement("OtherCamera",
            requirement.Kind, requirement.LogicalPurpose, requirement.CoefficientContract, requirement.AcceptancePolicy) }));
        Assert.Throws<ArgumentException>(() => Content(Enumerable.Range(0, 9)
            .Select(index => Requirement(CalibrationKind.Intrinsic, "Purpose" + index))));
        var changedPolicy = new CalibrationRequirement("TopCamera", requirement.Kind, requirement.LogicalPurpose,
            requirement.CoefficientContract,
            new RecipeContractReference("CalibrationPolicy", "2", new string('B', 64)));
        Assert.NotEqual(Content(new[] { requirement }).ContentHash, Content(new[] { changedPolicy }).ContentHash);
    }

    [Theory]
    [InlineData("ProfileId")]
    [InlineData("Coefficients")]
    [InlineData("StableDeviceIdentity")]
    public void V123_R04_RecipeCodecRejectsStationCalibrationAuthorityFields(string field)
    {
        var source = Content(new[] { Requirement(CalibrationKind.Intrinsic, "Undistortion") });
        Assert.True(RecipeDraftStorageCodec.TryEncodeContent(source, out var document, out var reason), reason);
        var root = JsonNode.Parse(document!.PayloadJson)!.AsObject();
        root["CalibrationRequirements"]!.AsArray()[0]!.AsObject()[field] = "forged";
        Assert.False(RecipeDraftStorageCodec.TryDecodeContent(root.ToJsonString(), out _, out _));
    }

    internal static CalibrationRequirement Requirement(CalibrationKind kind, string purpose) =>
        new("TopCamera", kind, purpose, new RecipeContractReference("CalibrationCoefficients." + kind, "1", new string('D', 64)),
            new RecipeContractReference("CalibrationPolicy", "1", new string('A', 64)));

    internal static RecipeDraftContent Content(IEnumerable<CalibrationRequirement>? requirements,
        bool extension = false)
    {
        var schema = new AlgorithmConfigurationSchema("CalibrationTest.Config", "1", Array.Empty<AlgorithmFieldDefinition>());
        var configuration = AlgorithmConfigurationSnapshot.Create(schema, Array.Empty<AlgorithmConfigurationEntry>());
        var overlay = new OverlayContract("CalibrationTest.Overlay", "1", 0, 0, 0);
        var result = new AlgorithmResultSchema("CalibrationTest.Result", "1", Array.Empty<AlgorithmFieldDefinition>(),
            Array.Empty<string>(), overlay);
        var binding = new RecipeAlgorithmBinding(new AlgorithmIdentity("CalibrationTest.Algorithm", "1"), schema,
            new(result.Id, result.Version, result.ContentHash), new(overlay.Id, overlay.Version, overlay.ContentHash));
        var camera = new RequestedCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
            new RegionOfInterest(0, 0, 16, 12), VisionPixelFormat.Mono8, null, 1000, 0, null);
        return new RecipeDraftContent("CalibrationTestRecipe", "Calibration test recipe", binding, configuration,
            "TopCamera", camera, TimeSpan.FromMilliseconds(250), null, null,
            cameraProviderExtension: extension ? new CameraProviderExtensionRequirement(
                new CameraProviderIdentity("Test.Provider", "1", "Test.Package", "1"), "Test.Extension", "1",
                new string('C', 64)) : null,
            calibrationRequirements: requirements);
    }
}
