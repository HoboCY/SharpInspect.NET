using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationResolverTests
{
    [Fact]
    public async Task V123_C01_NoRequirementNeedsNoProfileOrDefaultCoefficients()
    {
        var rig = new Rig();
        var resolver = new CalibrationRequirementResolver(rig, rig);
        var result = await resolver.CheckAsync(CalibrationRequirementTests.Content(null),
            Array.Empty<CalibrationProfileSelection>(), new(CommandSource.PhysicalConsole));
        Assert.Equal("CalibrationNotRequired", result.ReasonCode);
        Assert.True(result.CalibrationAllowsNonProductionDebug);
        Assert.True(result.CalibrationActivationGateSatisfied);
        Assert.False(result.CanActivate);
        Assert.Empty(result.Observations);
        Assert.Equal(0, rig.Reads);
    }

    [Theory]
    [InlineData(CalibrationFixtureProvenance.Candidate)]
    [InlineData(CalibrationFixtureProvenance.Historical)]
    public async Task V123_C02_ExactHistoricalSelectionNeverBecomesProductionAuthority(CalibrationFixtureProvenance source)
    {
        var rig = new Rig();
        var content = rig.ProfileContent();
        var selected = new CalibrationFixtureProfile(Guid.NewGuid(), 1, content, source);
        var newer = new CalibrationFixtureProfile(selected.Reference.ProfileId, 2, content, CalibrationFixtureProvenance.Candidate);
        var resolver = new CalibrationRequirementResolver(rig, rig, new(new[] { newer, selected }));
        var result = await resolver.CheckAsync(rig.Recipe,
            new[] { new CalibrationProfileSelection(rig.Requirement.ContentHash, selected.Reference) }, rig.Invocation);
        Assert.True(result.Compatible);
        Assert.False(result.CalibrationAllowsNonProductionDebug);
        Assert.False(result.CalibrationActivationGateSatisfied);
        Assert.False(result.ProductionAuthority);
        Assert.False(result.CanActivate);
        Assert.Equal("DevelopmentOnly", result.EvidencePurpose);
        Assert.Equal("CalibrationProfileAuthorityUnavailable", result.ReasonCode);
        Assert.Equal(selected.Reference, Assert.Single(result.Observations).SelectedProfile);
    }

    [Theory]
    [InlineData("Missing", "CalibrationProfileSelectionRequired")]
    [InlineData("Version", "CalibrationProfileExactVersionMissing")]
    [InlineData("Hash", "CalibrationProfileContentHashMismatch")]
    [InlineData("Device", "CalibrationDeviceIdentityMismatch")]
    [InlineData("Revision", "CalibrationImagingSetupRevisionMismatch")]
    [InlineData("RequestedRoi", "CalibrationRequestedGeometryMismatch")]
    [InlineData("EffectiveRoi", "CalibrationEffectiveGeometryMismatch")]
    [InlineData("RequestedFormat", "CalibrationRequestedGeometryMismatch")]
    [InlineData("EffectiveValidBits", "CalibrationEffectiveGeometryMismatch")]
    [InlineData("CoefficientFormat", "CalibrationCoefficientContractMismatch")]
    [InlineData("Policy", "CalibrationAcceptancePolicyMismatch")]
    public async Task V123_C03_MismatchBlocksDependentDebugWithoutFallback(string mutation, string expectedReason)
    {
        var rig = new Rig();
        var profile = new CalibrationFixtureProfile(Guid.NewGuid(), 1, rig.ProfileContent(mutation),
            CalibrationFixtureProvenance.Historical);
        var resolver = new CalibrationRequirementResolver(rig, rig, new(new[] { profile }));
        var reference = mutation == "Version" ? new CalibrationProfileReference(profile.Reference.ProfileId, 2, profile.Content.ContentHash)
            : mutation == "Hash" ? new CalibrationProfileReference(profile.Reference.ProfileId, 1, new string('F', 64))
            : profile.Reference;
        var selection = mutation == "Missing" ? Array.Empty<CalibrationProfileSelection>()
            : new[] { new CalibrationProfileSelection(rig.Requirement.ContentHash, reference) };
        var result = await resolver.CheckAsync(rig.Recipe, selection, rig.Invocation);
        Assert.False(result.Compatible);
        Assert.False(result.CalibrationAllowsNonProductionDebug);
        Assert.False(result.CalibrationActivationGateSatisfied);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(expectedReason, Assert.Single(result.Observations).ReasonCode);
    }

    [Fact]
    public async Task V123_C04_CurrentBindingAndUnavailableImagingCannotBorrowOldProfile()
    {
        var rig = new Rig();
        var profile = new CalibrationFixtureProfile(Guid.NewGuid(), 1, rig.ProfileContent(), CalibrationFixtureProvenance.Historical);
        var resolver = new CalibrationRequirementResolver(rig, rig, new(new[] { profile }));
        rig.Binding = rig.Binding with { Revision = 2, RevisionHash = new string('E', 64) };
        var result = await resolver.CheckAsync(rig.Recipe,
            new[] { new CalibrationProfileSelection(rig.Requirement.ContentHash, profile.Reference) }, rig.Invocation);
        Assert.Equal("CalibrationCurrentBindingMismatch", result.ReasonCode);
        Assert.False(result.CalibrationAllowsNonProductionDebug);
        rig.ImagingAvailable = false;
        result = await resolver.CheckAsync(rig.Recipe, Array.Empty<CalibrationProfileSelection>(), rig.Invocation);
        Assert.Equal("ImagingSetupRevisionMissing", result.ReasonCode);
    }

    [Fact]
    public void V123_C05_CoefficientsAreIndependentImmutableBoundedBytes()
    {
        var format = new RecipeContractReference("Fixture.Coefficients", "1", new string('A', 64));
        var bytes = new byte[] { 1, 2, 3 };
        var payload = new CalibrationCoefficientPayload(format, bytes);
        var hash = payload.ContentHash;
        bytes[0] = 99;
        var returned = payload.GetBytes();
        returned[1] = 99;
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.GetBytes());
        Assert.Equal(hash, payload.ContentHash);
        Assert.Throws<ArgumentException>(() => new CalibrationCoefficientPayload(format, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => new CalibrationCoefficientPayload(format, new byte[CalibrationCoefficientPayload.MaximumBytes + 1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V123_C06_CurrentRequestedGeometryMustMatchEvenWhenEffectiveStillMatches(bool changeFormat)
    {
        var rig = new Rig();
        var camera = rig.Recipe.Camera;
        rig.CurrentRequested = new(camera.ProductionAcquisitionMode, camera.ExposureTimeUs,
            camera.GainDb, changeFormat ? camera.RegionOfInterest : new(1, 0, 16, 12),
            changeFormat ? VisionPixelFormat.Mono16 : camera.PixelFormat,
            changeFormat ? 10 : camera.ValidBits, camera.AcquisitionTimeoutMs,
            camera.TriggerDelayUs, camera.WhiteBalanceRgb);
        var profile = new CalibrationFixtureProfile(Guid.NewGuid(), 1, rig.ProfileContent(),
            CalibrationFixtureProvenance.Historical);
        var resolver = new CalibrationRequirementResolver(rig, rig, new(new[] { profile }));
        var result = await resolver.CheckAsync(rig.Recipe,
            new[] { new CalibrationProfileSelection(rig.Requirement.ContentHash, profile.Reference) }, rig.Invocation);
        Assert.False(result.Available);
        Assert.False(result.Compatible);
        Assert.False(result.CalibrationAllowsNonProductionDebug);
        Assert.Equal("CalibrationCurrentRequestedGeometryMismatch", result.ReasonCode);
        Assert.Empty(result.Observations);
    }

    private sealed class Rig : ICameraSetupRuntime, IImagingSetupRuntime
    {
        internal CalibrationRequirement Requirement { get; } = CalibrationRequirementTests.Requirement(CalibrationKind.Intrinsic, "Undistortion");
        internal RecipeDraftContent Recipe => CalibrationRequirementTests.Content(new[] { Requirement });
        internal CommandInvocation Invocation { get; } = new(CommandSource.PhysicalConsole, Guid.NewGuid().ToString("D"), Guid.NewGuid());
        internal CameraBindingRevision Binding { get; set; }
        internal ImagingSetupRevision Imaging { get; }
        internal bool ImagingAvailable { get; set; } = true;
        internal RequestedCameraConfiguration? CurrentRequested { get; set; }
        internal int Reads { get; private set; }
        internal Rig()
        {
            Binding = new(1, "TopCamera", 1, Guid.NewGuid(), null, new string('B', 64),
                new(new("Fixture.Provider", "1", "Fixture.Package", "1"), "FixtureDevice"),
                Guid.NewGuid(), Guid.NewGuid(), 1, "Fixture camera binding", DateTimeOffset.UnixEpoch);
            Imaging = new(1, "TopCamera", 1, Guid.NewGuid(), null, Binding,
                new("FixtureLens", "FocusLocked", "MountA", 250, "SensorUp"), ImagingSetupChangeOrigin.OperatorDeclared,
                Guid.NewGuid(), Guid.NewGuid(), 1, "Fixture optical setup", DateTimeOffset.UnixEpoch);
        }
        internal CalibrationProfileContent ProfileContent(string mutation = "")
        {
            var geometry = CalibrationFrameGeometry.FromRequested(Recipe.Camera);
            var otherGeometry = new CalibrationFrameGeometry(new(1, 0, 16, 12), 16, 12, VisionPixelFormat.Mono8, null);
            var otherFormat = new CalibrationFrameGeometry(new(0, 0, 16, 12), 16, 12, VisionPixelFormat.Mono16, 10);
            var requirement = mutation == "CoefficientFormat" ? new CalibrationRequirement("TopCamera", Requirement.Kind,
                Requirement.LogicalPurpose, new("Changed.Coefficients", "2", new string('C', 64)), Requirement.AcceptancePolicy)
                : mutation == "Policy" ? new CalibrationRequirement("TopCamera", Requirement.Kind, Requirement.LogicalPurpose,
                    Requirement.CoefficientContract, new("Changed.Policy", "2", new string('C', 64))) : Requirement;
            return new(requirement, mutation == "Device" ? new(Binding.Target.Provider, "OtherDevice") : Binding.Target,
                mutation == "Revision" ? new("TopCamera", Guid.NewGuid(), 2, new string('D', 64)) : ImagingSetupRevisionReference.FromRevision(Imaging),
                mutation == "RequestedRoi" ? otherGeometry : mutation == "RequestedFormat" ? otherFormat : geometry,
                mutation == "EffectiveRoi" ? otherGeometry : mutation == "EffectiveValidBits" ? otherFormat : geometry,
                new(requirement.CoefficientContract, new byte[] { 1, 2, 3 }), new("Fixture.Procedure", "1", new string('C', 64)),
                new string('D', 64), Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        }
        public IReadOnlyList<CameraProviderIdentity> Providers => new[] { Binding.Target.Provider };
        public ValueTask<CameraSetupQueryResult> GetSetupAsync(string logicalRole, CommandInvocation invocation, CancellationToken cancellationToken = default)
        {
            Reads++;
            var camera = Recipe.Camera;
            var effective = new EffectiveCameraConfiguration(camera.ProductionAcquisitionMode, camera.ExposureTimeUs,
                camera.GainDb, camera.RegionOfInterest, camera.PixelFormat, camera.ValidBits, camera.AcquisitionTimeoutMs,
                camera.TriggerDelayUs, camera.WhiteBalanceRgb);
            return ValueTask.FromResult(new CameraSetupQueryResult(true, "FixtureAvailable", new("TopCamera", Binding,
                new(CameraProviderAvailability.Available, CameraConnectionState.Open, CameraConfigurationState.Applied,
                    CameraAcquisitionState.Stopped, new(DateTimeOffset.UnixEpoch, 0)), CurrentRequested ?? camera, effective)));
        }
        public ValueTask<ImagingSetupQueryResult> GetImagingSetupAsync(string logicalCameraRole, CommandInvocation invocation, CancellationToken cancellationToken = default)
        { Reads++; return ValueTask.FromResult(new ImagingSetupQueryResult(ImagingAvailable, "FixtureAvailable", ImagingAvailable ? Imaging : null)); }
        public ValueTask<CameraDiscoveryResult> DiscoverAsync(CameraProviderIdentity provider, CommandInvocation invocation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<CameraSetupOperationResult> RebindAsync(CameraRebindRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<CameraSetupOperationResult> ApplyDebugConfigurationAsync(CameraDebugConfigurationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ImagingSetupChangeResult> DeclareImagingSetupAsync(ImagingSetupChangeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ImagingSetupHistoryResult> QueryImagingSetupHistoryAsync(string logicalCameraRole, CommandInvocation invocation, long afterPosition = 0, long? throughPosition = null, int pageSize = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
