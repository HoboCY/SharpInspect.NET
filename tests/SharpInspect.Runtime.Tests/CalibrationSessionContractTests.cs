using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationSessionContractTests
{
    [Fact]
    public void V124_M01_StartAuthorizationBindsConfigurationInputAndReason()
    {
        var fixture = new Fixture();
        var original = fixture.Command();
        Assert.NotEqual(original.AuthorizationTarget, fixture.Command(exposure: 20).AuthorizationTarget);
        Assert.NotEqual(original.AuthorizationTarget, fixture.Command(input: 2).AuthorizationTarget);
        Assert.NotEqual(original.AuthorizationTarget, fixture.Command(reason: "New acquisition purpose").AuthorizationTarget);
        Assert.Equal(original.AuthorizationTarget, fixture.Command().AuthorizationTarget);
        Assert.NotEqual(original.CorrelationId, fixture.Command().CorrelationId);
    }

    [Fact]
    public void V124_M02_NewPermissionDoesNotRewriteOldPolicyAndMustBeExplicitlyGranted()
    {
        var old = AuthorizationPolicy.Development;
        Assert.Equal("D7202FE18DE53788D5CC9A8E898F796E345CB5B2EBB265E5615C669282825C50", old.ContentHash);
        Assert.All(old.RoleBundles.Values, permissions => Assert.DoesNotContain(Permission.RunCalibration, permissions));
        Assert.DoesNotContain(Permission.RunCalibration, old.StepUpPermissions);
        Assert.True(old.RequiresStepUp(Permission.RunCalibration));
        var enabled = new AuthorizationPolicy("calibration-development", "1",
            old.RoleBundles.ToDictionary(pair => pair.Key, pair => pair.Key == HumanRoleBundle.Technician
                ? pair.Value.Append(Permission.RunCalibration) : pair.Value.AsEnumerable()));
        Assert.Contains(Permission.RunCalibration, enabled.GetPermissions(HumanRoleBundle.Technician));
        Assert.Contains(Permission.RunCalibration, enabled.StepUpPermissions);
    }

    [Fact]
    public void V124_M03_SourceIdentityIncludesFullEffectiveConfiguration()
    {
        var fixture = new Fixture();
        var id = Guid.NewGuid();
        var first = fixture.Frame(id);
        var other = fixture.Frame(id, exposure: 20);
        Assert.Equal(first.PixelHash, other.PixelHash);
        Assert.NotEqual(first.SourceHash, other.SourceHash);
        var bytes = new byte[100];
        var image = new CalibrationFrameImage(first, bytes);
        bytes[0] = 99;
        var copy = image.GetBytes();
        copy[1] = 99;
        Assert.All(image.GetBytes(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void V124_M04_WholeFrameExclusionReevaluatesCountAndCoverageWithoutDeletingEvidence()
    {
        var fixture = new Fixture();
        var header = fixture.Header();
        var frame1 = fixture.Frame(Guid.NewGuid());
        var frame2 = fixture.Frame(Guid.NewGuid());
        var frames = new[] { frame1, frame2 };
        var observations = new[] { fixture.Observation(frame1), fixture.Observation(frame2) };
        var complete = CalibrationEvidenceSelection.Evaluate(header, frames, observations,
            Array.Empty<CalibrationEvidenceExclusion>());
        Assert.True(complete.Sufficient, complete.ReasonCode);
        var excluded = new[] { new CalibrationEvidenceExclusion(frame1.FrameId,
            fixture.Actor, fixture.InteractiveSession, "Visibly occluded source", DateTimeOffset.UnixEpoch) };
        var insufficient = CalibrationEvidenceSelection.Evaluate(header, frames, observations, excluded);
        Assert.False(insufficient.Sufficient);
        Assert.Equal("CalibrationInsufficientFrames", insufficient.ReasonCode);
        Assert.Equal(1, insufficient.IncludedFrameCount);
        Assert.NotEqual(complete.SelectionHash, insufficient.SelectionHash);
        Assert.Equal(2, frames.Length);
        Assert.Equal(2, observations.Length);
    }

    [Fact]
    public void V124_M05_BadIncludedFrameCannotBeSilentlyDiscarded()
    {
        var fixture = new Fixture();
        var frames = new[] { fixture.Frame(Guid.NewGuid()), fixture.Frame(Guid.NewGuid()), fixture.Frame(Guid.NewGuid()) };
        var observations = new[] { fixture.Observation(frames[0]), fixture.Observation(frames[1]),
            fixture.Observation(frames[2], empty: true) };
        var result = CalibrationEvidenceSelection.Evaluate(fixture.Header(), frames, observations,
            Array.Empty<CalibrationEvidenceExclusion>());
        Assert.False(result.Sufficient);
        Assert.Equal("CalibrationInsufficientFeatures", result.ReasonCode);
        Assert.Equal(3, result.IncludedFrameCount);
        Assert.Equal(2, result.SufficientFeatureFrameCount);
    }

    [Fact]
    public void V124_M06_ObservationRejectsCoordinatesOutsidePreservedSource()
    {
        var fixture = new Fixture();
        var frame = fixture.Frame(Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => new CalibrationObservationEvidence(Guid.NewGuid(), frame,
            fixture.Plan.Procedure, fixture.Plan.Input.ContentHash,
            new CalibrationExtractionResult(new[] { new CalibrationImageFeature("outside", 10, 1) })));
    }

    private sealed class Fixture
    {
        internal Guid Actor { get; } = Guid.NewGuid();
        internal Guid InteractiveSession { get; } = Guid.NewGuid();
        internal Guid SessionId { get; } = Guid.NewGuid();
        private readonly Guid _imagingId = Guid.NewGuid();
        private readonly string _hash = new('A', 64);
        internal CalibrationSessionPlan Plan => MakePlan(10, 1);
        private static RequestedCameraConfiguration Configuration(double exposure) => new(
            ProductionAcquisitionMode.SoftwareTrigger, exposure, 0, new(0, 0, 10, 10), VisionPixelFormat.Mono8, null, 100, 0, null);
        private CalibrationSessionPlan MakePlan(double exposure, byte input)
        {
            var inputContract = new RecipeContractReference("fixture-input", "1", _hash);
            return new(new("TopCamera", CalibrationKind.Intrinsic, "geometry",
                    new("fixture-coefficients", "1", _hash), new("fixture-acceptance", "1", _hash)),
                new(new("fixture-procedure", "1", _hash), inputContract, CalibrationKind.Intrinsic),
                new(inputContract, new[] { input }), Configuration(exposure),
                new("fixture-selection", "1", 2, 2, 0.5));
        }
        internal StartCalibrationSessionCommand Command(double exposure = 10, byte input = 1,
            string reason = "Acquire calibration evidence") => new(Guid.NewGuid(),
            new(CommandSource.PhysicalConsole, Actor.ToString("D"), InteractiveSession, Guid.NewGuid()),
            MakePlan(exposure, input), 1, _hash, new("TopCamera", _imagingId, 1, _hash), reason);
        internal CalibrationSessionHeader Header()
        {
            var target = new CameraBindingTarget(new("fixture-provider", "1", "fixture-package", "1"), "device-1");
            var binding = new CameraBindingRevision(1, "TopCamera", 1, Guid.NewGuid(), null, _hash,
                target, Actor, InteractiveSession, 0, "Fixture binding", DateTimeOffset.UnixEpoch);
            return new(SessionId, Guid.NewGuid(), Guid.NewGuid(), Actor, InteractiveSession, 0,
                DateTimeOffset.UnixEpoch, Command(), binding, Configuration(10), Effective(10), _hash);
        }
        private static EffectiveCameraConfiguration Effective(double exposure) => new(
            ProductionAcquisitionMode.SoftwareTrigger, exposure, 0, new(0, 0, 10, 10), VisionPixelFormat.Mono8, null, 100, 0, null);
        internal CalibrationFrameEvidence Frame(Guid frameId, double exposure = 10)
        {
            var pixelHash = Convert.ToHexString(SHA256.HashData(new byte[100]));
            var provenance = new FrameProvenance(new(ExecutionKind.Calibration, frameId), "fixture-provider", "1",
                "fixture-package", "1", "fixture-sdk", "1", null, "device-1", null, null, "Mono8", "CanonicalRows",
                false, false, null, null, new(1, null, null, null, null));
            return new(SessionId, frameId, new(new(ExecutionKind.Calibration, frameId), "TopCamera", 10, 10, 10,
                VisionPixelFormat.Mono8, null, DateTimeOffset.UnixEpoch, Effective(exposure)), provenance, pixelHash, 100, pixelHash + ".bin");
        }
        internal CalibrationObservationEvidence Observation(CalibrationFrameEvidence frame, bool empty = false) =>
            new(Guid.NewGuid(), frame, Plan.Procedure, Plan.Input.ContentHash, new(empty
                ? Array.Empty<CalibrationImageFeature>() : new[] { new CalibrationImageFeature("a", 0, 0),
                    new CalibrationImageFeature("b", 9, 9) }));
    }
}
