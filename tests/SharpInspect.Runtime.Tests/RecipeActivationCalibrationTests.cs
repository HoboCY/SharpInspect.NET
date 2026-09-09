using SharpInspect.Abstractions;
using SharpInspect.Runtime.Recipes;
using Xunit;

namespace SharpInspect.Runtime.Tests;

/// <summary>
/// V132 activation-time calibration checks.  The records are assembled through the same
/// immutable governance constructors used by the store; the evaluator is deliberately tested
/// without a production admission or device side effect.
/// </summary>
public sealed class RecipeActivationCalibrationTests
{
    [Fact]
    public void V132_K01_K09BaselinePassesAndK10RejectsDevelopmentProfile()
    {
        var fixture = ActivationFixture.Create();

        var result = fixture.Evaluate();

        Assert.False(result.Allowed);
        Assert.Equal("CalibrationProfileProductionAuthorityUnavailable", result.ReasonCode);
        Assert.Empty(result.Bindings);
        Assert.Equal(10, result.Observations.Count);
        AssertChecks(result, "V132.K01", "V132.K02", "V132.K03", "V132.K04", "V132.K05",
            "V132.K06", "V132.K07", "V132.K08", "V132.K09");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
        Assert.Equal(fixture.Profile.ContentHash,
            Observation(result, "V132.K10").EvidenceHash);
    }

    [Fact]
    public void V132_K03_PolicyHeadChangeRejectsExactProfile()
    {
        var fixture = ActivationFixture.Create();
        var changed = fixture.WithPolicyHeadChange();

        var result = changed.Evaluate();

        AssertFailed(result, "V132.K03", "CalibrationPolicyHeadChanged");
        AssertFailed(result, "V132.K05", "CalibrationPolicyHeadChanged");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K05_LatestFailedVerificationRejectsOlderPassedVerification()
    {
        var fixture = ActivationFixture.Create();
        var changed = fixture.WithLatestFailedVerification();

        var result = changed.Evaluate();

        AssertFailed(result, "V132.K05", "CalibrationPhysicalVerificationFailed");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K05_ExactVerificationExpiryIsRejected()
    {
        var fixture = ActivationFixture.Create();

        var result = fixture.Evaluate(fixture.Verification.ValidUntilUtc!.Value);

        AssertFailed(result, "V132.K05", "CalibrationVerificationOverdue");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K06_DeviceIdentityMismatchIsRejected()
    {
        var fixture = ActivationFixture.Create();

        var result = fixture.WithDeviceMismatch().Evaluate();

        AssertFailed(result, "V132.K06", "CalibrationDeviceIdentityMismatch");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K07_ImagingRevisionMismatchIsRejected()
    {
        var fixture = ActivationFixture.Create();

        var result = fixture.WithImagingRevisionMismatch().Evaluate();

        AssertFailed(result, "V132.K07", "CalibrationImagingSetupRevisionMismatch");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K08_RequestedGeometryMismatchIsRejected()
    {
        var fixture = ActivationFixture.Create();

        var result = fixture.WithRequestedGeometryMismatch().Evaluate();

        AssertFailed(result, "V132.K08", "CalibrationRequestedGeometryMismatch");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K09_EffectiveGeometryMismatchIsRejected()
    {
        var fixture = ActivationFixture.Create();

        var result = fixture.WithEffectiveGeometryMismatch().Evaluate();

        AssertFailed(result, "V132.K09", "CalibrationEffectiveGeometryMismatch");
        AssertFailed(result, "V132.K10", "CalibrationProfileProductionAuthorityUnavailable");
    }

    [Fact]
    public void V132_K00_NoRequirementsAreNotRequiredAndExtraSelectionFailsClosed()
    {
        var fixture = ActivationFixture.Create();
        var noRequirements = CalibrationRequirementTests.Content(Array.Empty<CalibrationRequirement>());

        var notRequired = RecipeActivationCalibrationEvaluator.EvaluateRecords(
            noRequirements, Array.Empty<CalibrationProfileSelection>(), null, null,
            Array.Empty<object>(), fixture.AtUtcForTest);
        Assert.True(notRequired.Allowed);
        Assert.Equal("CalibrationNotRequired", notRequired.ReasonCode);
        Assert.Empty(notRequired.Observations);

        var extraSelection = RecipeActivationCalibrationEvaluator.EvaluateRecords(
            noRequirements,
            new[] { new CalibrationProfileSelection(fixture.RequirementContentHashForTest,
                fixture.Profile.Reference) },
            null, null, Array.Empty<object>(), fixture.AtUtcForTest);
        Assert.False(extraSelection.Allowed);
        Assert.Equal("CalibrationSelectionRequirementMismatch", extraSelection.ReasonCode);
        AssertFailed(extraSelection, "V132.K00", "CalibrationSelectionRequirementMismatch");

        var legacy = fixture.CreateLegacyRecipeForTest();
        var legacyResult = RecipeActivationCalibrationEvaluator.EvaluateRecords(
            legacy, Array.Empty<CalibrationProfileSelection>(), null, null,
            Array.Empty<object>(), fixture.AtUtcForTest);
        Assert.False(legacyResult.Allowed);
        Assert.Equal("CalibrationLegacyRequirementUnavailable", legacyResult.ReasonCode);
        AssertFailed(legacyResult, "V132.K00", "CalibrationLegacyRequirementUnavailable");
    }

    private static RecipeActivationCalibrationObservation Observation(
        RecipeActivationCalibrationEvaluation result, string checkId) =>
        Assert.Single(result.Observations, value => value.CheckId == checkId);

    private static void AssertChecks(RecipeActivationCalibrationEvaluation result,
        params string[] checkIds)
    {
        foreach (var checkId in checkIds)
            Assert.True(Observation(result, checkId).Passed, checkId);
    }

    private static void AssertFailed(RecipeActivationCalibrationEvaluation result,
        string checkId, string reasonCode)
    {
        var observation = Observation(result, checkId);
        Assert.False(observation.Passed, checkId);
        Assert.Equal(reasonCode, observation.ReasonCode);
    }

    private sealed class ActivationFixture
    {
        private const string BindingHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        private const string AlternateBindingHash = "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE";
        private const string SourceEvidenceHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private ActivationFixture(
            CalibrationValidityTests.Fixture source,
            RecipeDraftContent recipe,
            CalibrationRequirement requirement,
            CameraSetupSnapshot camera,
            ImagingSetupRevision imaging,
            PublishedCalibrationProfileVersion profile,
            PhysicalCalibrationVerificationRecord verification,
            IReadOnlyList<object> records,
            DateTimeOffset atUtc)
        {
            Source = source;
            Recipe = recipe;
            Requirement = requirement;
            Camera = camera;
            Imaging = imaging;
            Profile = profile;
            Verification = verification;
            Records = records;
            AtUtc = atUtc;
        }

        private CalibrationValidityTests.Fixture Source { get; }
        private RecipeDraftContent Recipe { get; }
        private CalibrationRequirement Requirement { get; }
        private CameraSetupSnapshot Camera { get; }
        private ImagingSetupRevision Imaging { get; }
        internal PublishedCalibrationProfileVersion Profile { get; }
        internal PhysicalCalibrationVerificationRecord Verification { get; }
        internal string RequirementContentHashForTest => Requirement.ContentHash;
        internal DateTimeOffset AtUtcForTest => AtUtc;

        internal RecipeDraftContent CreateLegacyRecipeForTest() => CreateRecipe(Requirement,
            Recipe.Camera, new[] { new RecipeAssetRequirement(RecipeAssetKind.Calibration,
                "TopCamera", Source.Policy.InputContract) });
        private IReadOnlyList<object> Records { get; }
        private DateTimeOffset AtUtc { get; }

        internal static ActivationFixture Create()
        {
            var source = CalibrationValidityTests.Fixture.Create();
            var atUtc = new DateTimeOffset(2026, 9, 9, 2, 0, 0, TimeSpan.Zero);
            var device = source.Profile.Content.Device;
            var binding = new CameraBindingRevision(1, "TopCamera", 1,
                Guid.Parse("12121212-1212-1212-1212-121212121212"), null, BindingHash,
                device, source.Actor.PrincipalId, source.Actor.SessionId,
                source.Actor.AuthorizationRevision, "activation fixture binding", atUtc.AddHours(-1));
            var definition = new ImagingSetupDefinition("fixture-lens", "50mm", "front", 250,
                "normal");
            var imaging = new ImagingSetupRevision(1, "TopCamera", 1,
                Guid.Parse("13131313-1313-1313-1313-131313131313"), null, binding, definition,
                ImagingSetupChangeOrigin.OperatorDeclared, source.Actor.PrincipalId,
                source.Actor.SessionId, source.Actor.AuthorizationRevision, "activation fixture imaging",
                atUtc.AddHours(-1));

            var requested = Requested(640, 480);
            var effective = Effective(640, 480);
            var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Open, CameraConfigurationState.Applied,
                CameraAcquisitionState.Stopped, new FrameTimePoint(atUtc, 1));
            var camera = new CameraSetupSnapshot("TopCamera", binding, health, requested, effective,
                reasonCode: "CameraSetupApplied");

            var requirement = new CalibrationRequirement("TopCamera", source.Policy.Kind,
                source.Policy.LogicalPurpose, source.Policy.CoefficientContract,
                source.Policy.Reference);
            var content = new CalibrationProfileContent(requirement, device,
                ImagingSetupRevisionReference.FromRevision(imaging),
                CalibrationFrameGeometry.FromRequested(requested),
                CalibrationFrameGeometry.FromEffective(effective),
                new CalibrationCoefficientPayload(source.Policy.CoefficientContract,
                    new byte[] { 1, 2, 3 }), source.Policy.ProcedureContract, SourceEvidenceHash,
                source.Actor.PrincipalId, atUtc.AddMinutes(-57));
            var profile = new PublishedCalibrationProfileVersion(3,
                Guid.Parse("14141414-1414-1414-1414-141414141414"),
                Guid.Parse("15151515-1515-1515-1515-151515151515"), 1, null, content,
                source.Candidate, source.Evaluation.Reference, source.Policy.Reference,
                source.Actor, atUtc.AddMinutes(-56));

            var performed = atUtc.AddMinutes(-55);
            var submission = source.CreateSubmission(performed, 7);
            var verification = new PhysicalCalibrationVerificationRecord(4,
                Guid.Parse("16161616-1616-1616-1616-161616161616"),
                Guid.Parse("17171717-1717-1717-1717-171717171717"), profile.Reference,
                source.Policy.Reference, submission,
                new[] { new CalibrationMetricGateResult("physical-scale",
                    CalibrationGateOutcome.Passed, 0.75, "mm/pixel", "PhysicalScaleWithinLimit") },
                Array.Empty<string>(), source.Actor, performed.AddMinutes(1),
                performed.Add(source.Policy.PhysicalVerification.ValidityInterval!.Value));

            var recipe = CreateRecipe(requirement, requested);
            var records = new object[] { source.PolicyRevision, source.Evaluation, profile, verification };
            return new(source, recipe, requirement, camera, imaging, profile, verification, records, atUtc);
        }

        internal RecipeActivationCalibrationEvaluation Evaluate(DateTimeOffset? atUtc = null) =>
            RecipeActivationCalibrationEvaluator.EvaluateRecords(Recipe,
                new[] { new CalibrationProfileSelection(Requirement.ContentHash, Profile.Reference) },
                Camera, Imaging, Records, atUtc ?? AtUtc);

        internal ActivationFixture WithPolicyHeadChange()
        {
            var changed = new CalibrationAcceptancePolicy(Source.Policy.Id, "2", Source.Policy.Kind,
                Source.Policy.LogicalPurpose, Source.Policy.ProcedureContract, Source.Policy.InputContract,
                Source.Policy.CoefficientContract, Source.Policy.ExtractionReceiptContract,
                Source.Policy.ComputationEvidenceContract, Source.Policy.Sample, Source.Policy.Coverage,
                Source.Policy.PoseDiversity, Source.Policy.MaximumPerImageResidual,
                Source.Policy.MaximumPerPointResidual, Source.Policy.InvalidObservation,
                Source.Policy.PhysicalVerification);
            var revision = new CalibrationAcceptancePolicyRevision(5,
                Guid.Parse("18181818-1818-1818-1818-181818181818"), changed,
                Source.Policy.Reference, Source.Actor, AtUtc.AddMinutes(-1));
            return Copy(records: Records.Concat(new object[] { revision }).ToArray());
        }

        internal ActivationFixture WithLatestFailedVerification()
        {
            var performed = AtUtc.AddMinutes(-20);
            var submission = Source.CreateSubmission(performed, 8, scale: 1.5);
            var failed = new PhysicalCalibrationVerificationRecord(5,
                Guid.Parse("19191919-1919-1919-1919-191919191919"),
                Guid.Parse("20202020-2020-2020-2020-202020202020"), Profile.Reference,
                Source.Policy.Reference, submission,
                new[] { new CalibrationMetricGateResult("physical-scale",
                    CalibrationGateOutcome.Failed, 1.5, "mm/pixel", "PhysicalVerificationProcedureFailed") },
                new[] { "PhysicalVerificationProcedureFailed" }, Source.Actor,
                performed.AddMinutes(1), null);
            return Copy(records: Records.Concat(new object[] { failed }).ToArray());
        }

        internal ActivationFixture WithDeviceMismatch()
        {
            var target = new CameraBindingTarget(
                new CameraProviderIdentity("fixture-provider", "1", "fixture-adapter", "1"),
                "other-camera");
            var binding = new CameraBindingRevision(1, "TopCamera", 1,
                Guid.Parse("21212121-2121-2121-2121-212121212121"), null, AlternateBindingHash,
                target, Source.Actor.PrincipalId, Source.Actor.SessionId,
                Source.Actor.AuthorizationRevision, "device mismatch", AtUtc.AddHours(-1));
            return Copy(camera: CreateCamera(binding, Imaging, Requested(640, 480), Effective(640, 480)));
        }

        internal ActivationFixture WithImagingRevisionMismatch()
        {
            var alternate = new ImagingSetupRevision(2, "TopCamera", 2,
                Guid.Parse("22222222-2222-2222-2222-222222222222"), Imaging.RevisionHash,
                Camera.Binding!, Imaging.Definition, ImagingSetupChangeOrigin.OperatorDeclared,
                Source.Actor.PrincipalId, Source.Actor.SessionId, Source.Actor.AuthorizationRevision,
                "imaging revision mismatch", AtUtc.AddMinutes(-2));
            return Copy(imaging: alternate);
        }

        internal ActivationFixture WithRequestedGeometryMismatch()
        {
            var requested = Requested(639, 480);
            return Copy(camera: CreateCamera(Camera.Binding!, Imaging, requested, Effective(640, 480)));
        }

        internal ActivationFixture WithEffectiveGeometryMismatch()
        {
            var effective = Effective(639, 480);
            return Copy(camera: CreateCamera(Camera.Binding!, Imaging, Requested(640, 480), effective));
        }

        private ActivationFixture Copy(CameraSetupSnapshot? camera = null,
            ImagingSetupRevision? imaging = null, IReadOnlyList<object>? records = null) =>
            new(Source, Recipe, Requirement, camera ?? Camera, imaging ?? Imaging, Profile,
                Verification, records ?? Records, AtUtc);

        private static RecipeDraftContent CreateRecipe(CalibrationRequirement requirement,
            RequestedCameraConfiguration camera, IEnumerable<RecipeAssetRequirement>? assets = null)
        {
            var schema = new AlgorithmConfigurationSchema("CalibrationActivation.Config", "1",
                Array.Empty<AlgorithmFieldDefinition>());
            var configuration = AlgorithmConfigurationSnapshot.Create(schema,
                Array.Empty<AlgorithmConfigurationEntry>());
            var overlay = new OverlayContract("CalibrationActivation.Overlay", "1", 0, 0, 0);
            var result = new AlgorithmResultSchema("CalibrationActivation.Result", "1",
                Array.Empty<AlgorithmFieldDefinition>(), Array.Empty<string>(), overlay);
            var binding = new RecipeAlgorithmBinding(
                new AlgorithmIdentity("CalibrationActivation.Algorithm", "1"), schema,
                new RecipeContractReference(result.Id, result.Version, result.ContentHash),
                new RecipeContractReference(overlay.Id, overlay.Version, overlay.ContentHash));
            return new RecipeDraftContent("CalibrationActivationRecipe", "Calibration activation fixture",
                binding, configuration, "TopCamera", camera, TimeSpan.FromSeconds(1), assets, null,
                calibrationRequirements: new[] { requirement });
        }

        private static CameraSetupSnapshot CreateCamera(CameraBindingRevision binding,
            ImagingSetupRevision imaging, RequestedCameraConfiguration requested,
            EffectiveCameraConfiguration effective)
        {
            var health = new CameraHealthSnapshot(CameraProviderAvailability.Available,
                CameraConnectionState.Open, CameraConfigurationState.Applied,
                CameraAcquisitionState.Stopped, new FrameTimePoint(imaging.RecordedAtUtc, 2));
            return new CameraSetupSnapshot("TopCamera", binding, health, requested, effective,
                reasonCode: "CameraSetupApplied");
        }

        private static RequestedCameraConfiguration Requested(int width, int height) =>
            new(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
                new RegionOfInterest(0, 0, width, height), VisionPixelFormat.Mono8, null,
                1000, 0, null);

        private static EffectiveCameraConfiguration Effective(int width, int height) =>
            new(ProductionAcquisitionMode.SoftwareTrigger, 1000, 0,
                new RegionOfInterest(0, 0, width, height), VisionPixelFormat.Mono8, null,
                1000, 0, null);
    }
}
