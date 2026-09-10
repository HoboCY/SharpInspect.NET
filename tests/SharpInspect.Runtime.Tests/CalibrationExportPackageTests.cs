using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationExportPackageTests
{
    [Fact]
    public void V134_E01_FullEvidenceAndProfileRoundTripThroughClosedPackage()
    {
        var fixture = ExportFixture.Create();
        var package = CalibrationExportPackageCodec.Encode(fixture.StationId,
            fixture.Evidence, fixture.Policy, fixture.Images, fixture.Profile);

        var decoded = CalibrationExportPackageCodec.Decode(package);

        Assert.Equal(fixture.Evidence.Header.SessionId, decoded.Manifest.Source.SessionId);
        Assert.Equal(fixture.Evidence.Candidate!.CandidateId,
            decoded.Manifest.Source.Candidate!.CandidateId);
        Assert.Equal(fixture.Profile!.Reference, decoded.Manifest.Source.Profile);
        Assert.Equal(fixture.Policy.Reference, decoded.Manifest.AcceptancePolicy);
        Assert.Equal(fixture.Policy.ProcedureContract, decoded.Manifest.Procedure);
        Assert.Equal(fixture.Evidence.Header.Binding.Target.ContentHash,
            decoded.Manifest.Device.ContentHash);
        Assert.Equal(fixture.Evidence.Header.Command.ExpectedImagingSetup,
            decoded.Manifest.ImagingSetup);
        Assert.Equal(fixture.Evidence.TemporaryConfiguration!.Requested.RegionOfInterest,
            decoded.Manifest.RequestedGeometry.RegionOfInterest);
        Assert.Equal(fixture.Evidence.TemporaryConfiguration.Effective.RegionOfInterest,
            decoded.Manifest.EffectiveGeometry.RegionOfInterest);
        Assert.NotEqual(fixture.Evidence.Header.BaselineRequested.RegionOfInterest,
            decoded.Manifest.RequestedGeometry.RegionOfInterest);
        Assert.Equal(fixture.Evidence.Frames.Count, decoded.Images.Count);
        Assert.Equal(fixture.Evidence.Frames[0].PixelHash, decoded.Images[0].Frame.PixelHash);
        Assert.Equal(fixture.Evidence.Candidate!.ContentHash,
            decoded.Evidence.Candidate!.ContentHash);
        Assert.Equal(fixture.Policy.ContentHash, decoded.Policy.ContentHash);
        Assert.Equal(fixture.Profile.ContentHash, decoded.Profile!.ContentHash);
        Assert.Equal(package.GetBytes(), new CalibrationExportPackage(package.GetBytes()).GetBytes());
    }

    [Fact]
    public void V134_E02_ExternalBytesAreDefensiveAndDoNotCarryProductionAuthority()
    {
        var fixture = ExportFixture.Create();
        var package = CalibrationExportPackageCodec.Encode(fixture.StationId,
            fixture.Evidence, fixture.Policy, fixture.Images, fixture.Profile);
        var bytes = package.GetBytes();
        var imported = new CalibrationExportPackage(bytes);
        bytes[0] ^= 0xFF;

        Assert.NotEqual(bytes[0], imported.GetBytes()[0]);
        var decoded = CalibrationExportPackageCodec.Decode(imported);
        Assert.False(decoded.Manifest.ProductionAuthority);
        Assert.False(decoded.Evidence.Candidate!.CanActivate);
        Assert.False(decoded.Profile!.ProductionAuthority);
        Assert.False(decoded.Profile.CanActivate);
    }

    [Fact]
    public void V134_E03_TamperedMemberIsRejectedBeforeEvidenceDecode()
    {
        var fixture = ExportFixture.Create();
        var package = CalibrationExportPackageCodec.Encode(fixture.StationId,
            fixture.Evidence, fixture.Policy, fixture.Images, fixture.Profile);
        var tampered = package.GetBytes();
        tampered[^1] ^= 0x01;

        var imported = new CalibrationExportPackage(tampered);
        Assert.Throws<ArgumentException>(() => CalibrationExportPackageCodec.Decode(imported));
    }

    [Fact]
    public void V134_E04_TruncatedPackageAndTrailingBytesAreRejected()
    {
        var fixture = ExportFixture.Create();
        var package = CalibrationExportPackageCodec.Encode(fixture.StationId,
            fixture.Evidence, fixture.Policy, fixture.Images, fixture.Profile);

        var truncated = new CalibrationExportPackage(package.GetBytes()[..^1]);
        Assert.Throws<ArgumentException>(() => CalibrationExportPackageCodec.Decode(truncated));

        var trailingBytes = package.GetBytes().Concat(new byte[] { 0x42 }).ToArray();
        var withTrailing = new CalibrationExportPackage(trailingBytes);
        Assert.Throws<ArgumentException>(() => CalibrationExportPackageCodec.Decode(withTrailing));
    }

    [Fact]
    public void V134_E05_MissingFrameEvidenceIsRejectedAtExportBoundary()
    {
        var fixture = ExportFixture.Create();
        Assert.Throws<ArgumentException>(() => CalibrationExportPackageCodec.Encode(
            fixture.StationId, fixture.Evidence, fixture.Policy, Array.Empty<CalibrationFrameImage>(),
            fixture.Profile));
    }

    [Fact]
    public void V134_E06_DeclaredMissingMemberIsRejectedByContainerBoundary()
    {
        var fixture = ExportFixture.Create();
        var package = CalibrationExportPackageCodec.Encode(fixture.StationId,
            fixture.Evidence, fixture.Policy, fixture.Images);
        var bytes = package.GetBytes();

        // Header member count is after magic, version, and flags. Claiming an
        // additional payload without supplying it must fail before decoding evidence.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 4);
        Assert.Throws<ArgumentException>(() => CalibrationExportPackageCodec.Decode(
            new CalibrationExportPackage(bytes)));
    }

    internal sealed class ExportFixture
    {
        private const string Hash =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private static readonly Guid SessionId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid ActorId =
            Guid.Parse("22222222-2222-2222-2222-222222222222");
        private static readonly Guid InteractiveSessionId =
            Guid.Parse("33333333-3333-3333-3333-333333333333");
        private static readonly Guid FrameId =
            Guid.Parse("44444444-4444-4444-4444-444444444444");
        private static readonly Guid CandidateId =
            Guid.Parse("55555555-5555-5555-5555-555555555555");

        private ExportFixture(string stationId, CalibrationAcceptancePolicy policy,
            CalibrationSessionEvidence evidence, CalibrationFrameImage image,
            PublishedCalibrationProfileVersion profile)
        {
            StationId = stationId;
            Policy = policy;
            Evidence = evidence;
            Images = new[] { image };
            Profile = profile;
        }

        internal string StationId { get; }
        internal CalibrationAcceptancePolicy Policy { get; }
        internal CalibrationSessionEvidence Evidence { get; }
        internal IReadOnlyList<CalibrationFrameImage> Images { get; }
        internal PublishedCalibrationProfileVersion Profile { get; }

        internal static ExportFixture Create()
        {
            var policy = CreatePolicy();
            var requested = new RequestedCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null);
            var baselineRequested = new RequestedCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 5, 0,
                new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 100, 0, null);
            var baselineEffective = new EffectiveCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 5, 0,
                new RegionOfInterest(0, 0, 2, 2), VisionPixelFormat.Mono8, null, 100, 0, null);
            var input = new CalibrationProcedureInputPayload(policy.InputContract,
                new byte[] { 1, 2, 3 });
            var procedure = new CalibrationProcedureDescriptor(policy.ProcedureContract,
                policy.InputContract, policy.Kind);
            var plan = new CalibrationSessionPlan(
                new CalibrationRequirement("TopCamera", policy.Kind, policy.LogicalPurpose,
                    policy.CoefficientContract, policy.Reference), procedure, input, requested,
                new CalibrationEvidenceSelectionPolicy("selection", "1", 1, 1, 0));
            var target = new CameraBindingTarget(
                new CameraProviderIdentity("fixture-provider", "1", "fixture-adapter", "1"),
                "fixture-camera");
            var binding = new CameraBindingRevision(1, "TopCamera", 1,
                Guid.Parse("66666666-6666-6666-6666-666666666666"), null, Hash, target,
                ActorId, InteractiveSessionId, 0, "fixture binding", DateTimeOffset.UnixEpoch);
            var imaging = new ImagingSetupRevisionReference("TopCamera",
                Guid.Parse("77777777-7777-7777-7777-777777777777"), 1, Hash);
            var command = new StartCalibrationSessionCommand(
                Guid.Parse("88888888-8888-8888-8888-888888888888"),
                new CommandInvocation(CommandSource.PhysicalConsole, ActorId.ToString("D"),
                    InteractiveSessionId, Guid.Parse("99999999-9999-9999-9999-999999999999")),
                plan, binding.Revision, binding.RevisionHash, imaging, "export fixture");
            var header = new CalibrationSessionHeader(SessionId,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ActorId,
                InteractiveSessionId, 0, DateTimeOffset.UnixEpoch.AddSeconds(1), command,
                binding, baselineRequested, baselineEffective, Hash);

            var pixels = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
            var temporaryEffective = new EffectiveCameraConfiguration(
                ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null);
            var frame = CreateFrame(temporaryEffective, pixels);
            var image = new CalibrationFrameImage(frame, pixels);
            var observation = new CalibrationObservationEvidence(Guid.Parse(
                "cccccccc-cccc-cccc-cccc-cccccccccccc"), frame, procedure, input.ContentHash,
                new CalibrationExtractionResult(new[] { new CalibrationImageFeature("corner", 1, 1) },
                    Array.Empty<CalibrationProcedureDiagnostic>()));
            var selection = CalibrationEvidenceSelection.Evaluate(header, new[] { frame },
                new[] { observation }, Array.Empty<CalibrationEvidenceExclusion>());
            var computation = new CalibrationProcedureComputationResult(
                new CalibrationCoefficientPayload(policy.CoefficientContract, new byte[] { 4, 5, 6 }),
                new[] { new CalibrationQualityMetric("PointResidual", 0.25, "pixels") },
                Array.Empty<CalibrationProcedureDiagnostic>(),
                new CalibrationComputationEvidencePayload(policy.ComputationEvidenceContract,
                    new byte[] { 7, 8, 9 }));
            var candidateEvidence = new CalibrationCandidateEvidence(CandidateId, SessionId,
                header.ContentHash, selection.SelectionHash, computation,
                DateTimeOffset.UnixEpoch.AddSeconds(2));
            var state = new CalibrationSessionState(SessionId,
                CalibrationSessionPhase.CandidateRetained, CalibrationSessionOutcome.Pending,
                1, 1, 0, CandidateId, "CalibrationEvidenceSufficient", false);
            var evidence = new CalibrationSessionEvidence(header, state, new[] { frame },
                new[] { observation }, Array.Empty<CalibrationEvidenceExclusion>(), candidateEvidence,
                selection, new CalibrationTemporaryConfigurationEvidence(requested,
                    new EffectiveCameraConfiguration(ProductionAcquisitionMode.SoftwareTrigger, 10, 0,
                        new RegionOfInterest(0, 0, 4, 4), VisionPixelFormat.Mono8, null, 100, 0, null)));

            var candidate = new CalibrationCandidateReference(SessionId, CandidateId,
                candidateEvidence.ContentHash);
            var profileContent = new CalibrationProfileContent(
                plan.Requirement, target, imaging,
                CalibrationFrameGeometry.FromRequested(requested),
                CalibrationFrameGeometry.FromEffective(temporaryEffective),
                computation.Coefficients, policy.ProcedureContract,
                candidateEvidence.ContentHash, ActorId, DateTimeOffset.UnixEpoch.AddSeconds(3));
            var actor = new CalibrationGovernanceActor(ActorId, InteractiveSessionId, 0);
            var profile = new PublishedCalibrationProfileVersion(
                1, Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), 1, null, profileContent,
                candidate, new CalibrationPolicyEvaluationReference(
                    Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Hash),
                policy.Reference, actor, DateTimeOffset.UnixEpoch.AddSeconds(4));
            return new ExportFixture("fixture-station", policy, evidence, image, profile);
        }

        private static CalibrationFrameEvidence CreateFrame(
            EffectiveCameraConfiguration effective, byte[] pixels)
        {
            var pixelHash = Convert.ToHexString(SHA256.HashData(pixels));
            var correlation = new ExecutionCorrelationId(ExecutionKind.Calibration, FrameId);
            var metadata = new FrameMetadata(correlation, "TopCamera", 4, 4, 4,
                VisionPixelFormat.Mono8, null, DateTimeOffset.UnixEpoch, effective);
            var provenance = new FrameProvenance(correlation, "fixture-provider", "1",
                "fixture-adapter", "1", "fixture-sdk", "1", null, "fixture-camera", null, null,
                "Mono8", "CanonicalRows", false, false, null, null,
                new FrameAcquisitionMilestones(1, null, null, null, null));
            return new CalibrationFrameEvidence(SessionId, FrameId, metadata, provenance,
                pixelHash, pixels.Length, pixelHash + ".bin");
        }

        private static CalibrationAcceptancePolicy CreatePolicy()
        {
            var procedure = Contract("procedure");
            var input = Contract("input");
            var coefficient = Contract("coefficient");
            var receipt = Contract("receipt");
            var computation = Contract("computation");
            return new CalibrationAcceptancePolicy("acceptance", "1", CalibrationKind.Intrinsic,
                "geometry", procedure, input, coefficient, receipt, computation,
                Required(CalibrationAcceptanceGateCategory.Sample,
                    new CalibrationMetricGate("sample", CalibrationPolicyFactReference.IncludedFrameCount,
                        CalibrationGateComparison.MinimumInclusive, 1)),
                Required(CalibrationAcceptanceGateCategory.Coverage,
                    new CalibrationMetricGate("coverage", CalibrationPolicyFactReference.SelectionImageCoverage,
                        CalibrationGateComparison.MinimumInclusive, 0)),
                NotApplicable(CalibrationAcceptanceGateCategory.PoseDiversity, "not measured"),
                NotApplicable(CalibrationAcceptanceGateCategory.MaximumPerImageResidual, "not measured"),
                Required(CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
                    new CalibrationMetricGate("point", CalibrationPolicyFactReference.ProcedureMetric(
                        "PointResidual", "pixels"), CalibrationGateComparison.MaximumInclusive, 1)),
                Required(CalibrationAcceptanceGateCategory.InvalidObservation,
                    new CalibrationMetricGate("excluded", CalibrationPolicyFactReference.ExcludedFrameCount,
                        CalibrationGateComparison.MaximumInclusive, 0)),
                new PhysicalCalibrationVerificationRequirement(
                    CalibrationPolicyApplicability.NotApplicable, null, null, null, null, null,
                    "not required"));
        }

        private static CalibrationGateSection Required(CalibrationAcceptanceGateCategory category,
            CalibrationMetricGate gate) => new(category, CalibrationPolicyApplicability.Required,
                new[] { gate });

        private static CalibrationGateSection NotApplicable(CalibrationAcceptanceGateCategory category,
            string reason) => new(category, CalibrationPolicyApplicability.NotApplicable,
                notApplicableReason: reason);

        private static RecipeContractReference Contract(string id) => new(id, "1", Hash);
    }
}
