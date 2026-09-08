using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpInspect.Abstractions;
using SharpInspect.Calibration.OpenCvSharp;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Integrity;
using SharpInspect.Runtime.Storage;

namespace SharpInspect.CalibrationConsumer;

internal static partial class CalibrationConsumer
{
    private static CalibrationGovernanceStoreOptions GovernanceStoreOptions() => new()
    { MaximumEntries = 32, MaximumPayloadBytes = 256 * 1024, MaximumTotalBytes = 8 * 1024 * 1024 };

    private static RecipeContractReference GovernanceContract(string id) => new(id, "1", Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("T27 explicit development fixture only: " + id))));
    private static readonly RecipeContractReference PhysicalProcedure = GovernanceContract("development.planar.reference-check");
    private static readonly RecipeContractReference PhysicalEvidence = GovernanceContract("development.planar.reference-evidence");
    private static readonly RecipeContractReference IndependentReference = GovernanceContract("development.planar.independent-reference");

    private static CalibrationAcceptancePolicy CreateGovernancePolicy()
    {
        static CalibrationGateSection Required(CalibrationAcceptanceGateCategory category, params CalibrationMetricGate[] gates) =>
            new(category, CalibrationPolicyApplicability.Required, gates, null);
        static CalibrationMetricGate Min(string id, CalibrationPolicyFactReference fact, double threshold) =>
            new(id, fact, CalibrationGateComparison.MinimumInclusive, threshold);
        static CalibrationMetricGate Max(string id, CalibrationPolicyFactReference fact, double threshold) =>
            new(id, fact, CalibrationGateComparison.MaximumInclusive, threshold);
        return new("development.planar.acceptance", "1", CalibrationKind.PlanarHomography, "PlanarHomographyFixtureOnly",
            PlanarHomographyContracts.Procedure, PlanarHomographyContracts.Input, PlanarHomographyContracts.Coefficients,
            PlanarHomographyContracts.ExtractionReceipt, PlanarHomographyContracts.Evidence,
            Required(CalibrationAcceptanceGateCategory.Sample, Min("sample", CalibrationPolicyFactReference.IncludedFrameCount, 1)),
            Required(CalibrationAcceptanceGateCategory.Coverage, Min("coverage", CalibrationPolicyFactReference.SelectionImageCoverage, .1)),
            new(CalibrationAcceptanceGateCategory.PoseDiversity, CalibrationPolicyApplicability.NotApplicable, null,
                "This single-plane development fixture has one retained image; no intrinsic pose fit is performed."),
            new(CalibrationAcceptanceGateCategory.MaximumPerImageResidual, CalibrationPolicyApplicability.NotApplicable, null,
                "The only retained image is fully covered by the explicit maximum per-point residual gates."),
            Required(CalibrationAcceptanceGateCategory.MaximumPerPointResidual,
                Max("point-mm", CalibrationPolicyFactReference.ProcedureMetric("MaxMillimeters", "millimeters"), .5),
                Max("point-pixels", CalibrationPolicyFactReference.ProcedureMetric("MaxPixels", "pixels"), 1.5)),
            Required(CalibrationAcceptanceGateCategory.InvalidObservation,
                Max("excluded", CalibrationPolicyFactReference.ExcludedFrameCount, 1)),
            new(CalibrationPolicyApplicability.Required, PhysicalProcedure, PhysicalEvidence, IndependentReference,
                TimeSpan.FromHours(12), new[]
                {
                    Max("independent-error", CalibrationPolicyFactReference.PhysicalVerificationMetric("MaximumError", "millimeters"), .5),
                    Min("independent-count", CalibrationPolicyFactReference.PhysicalVerificationMetric("PointCount", "points"), 3)
                }, null));
    }

    private static async Task RunGovernanceAsync(CalibrationConsumerArguments arguments, string password,
        CalibrationSessionEvidence session)
    {
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(CreateStoreOptions(arguments), TimeSpan.FromMilliseconds(20));
        services.AddSharpInspectPhysicalCalibrationVerification(new PhysicalCalibrationVerificationRegistry(
            new[] { new DevelopmentPhysicalVerifier() }));
        await using var container = services.BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var stepUp = container.GetRequiredService<IStepUpAuthentication>();
        var governance = container.GetRequiredService<ICalibrationGovernanceRuntime>();
        var query = container.GetRequiredService<ICalibrationGovernanceQuery>();
        await WaitForRuntimeAsync(runtime, value => value.AuditIntegrity?.State == AuditIntegrityState.Verified).ConfigureAwait(true);
        await SignInAsync(sessions, runtime, arguments.UserName, arguments.ExpectedPrincipal, password).ConfigureAwait(true);
        async Task<CommandInvocation> Authorize(CalibrationGovernanceCommand command, Permission permission, AuditedCommandKind kind)
        {
            var grant = await GrantAsync(stepUp, sessions, permission, command.CorrelationId, command.AuthorizationTarget, kind, password)
                .ConfigureAwait(true);
            return CurrentInvocation(sessions) with { StepUpGrantId = grant.GrantId };
        }
        var policy = CreateGovernancePolicy();
        var policyCommand = new PublishCalibrationAcceptancePolicyCommand(Guid.NewGuid(), CurrentInvocation(sessions), policy,
            null, "Freeze explicit development fixture policy");
        var publishedPolicy = await governance.PublishPolicyAsync(policyCommand with { Invocation = await Authorize(policyCommand,
            Permission.ManageCalibrationAcceptancePolicy, AuditedCommandKind.PublishCalibrationAcceptancePolicy) }).ConfigureAwait(true);
        Require(publishedPolicy.Revision is not null, "governance-policy-" + publishedPolicy.Outcome.ReasonCode);
        var candidate = new CalibrationCandidateReference(session.Header.SessionId, session.Candidate!.CandidateId, session.Candidate.ContentHash);
        var evaluate = new EvaluateCalibrationCandidateCommand(Guid.NewGuid(), CurrentInvocation(sessions), candidate, policy.Reference);
        var evaluated = await governance.EvaluateCandidateAsync(evaluate with { Invocation = await Authorize(evaluate,
            Permission.PublishCalibration, AuditedCommandKind.EvaluateCalibrationCandidate) }).ConfigureAwait(true);
        Require(evaluated.Evaluation is { Passed: true }, "governance-evaluate-" + evaluated.Outcome.ReasonCode);
        var publish = new PublishCalibrationProfileCommand(Guid.NewGuid(), CurrentInvocation(sessions), Guid.NewGuid(), null,
            candidate, evaluated.Evaluation!.Reference, "Publish an immutable development Profile");
        var published = await governance.PublishProfileAsync(publish with { Invocation = await Authorize(publish,
            Permission.PublishCalibration, AuditedCommandKind.PublishCalibrationProfile) }).ConfigureAwait(true);
        Require(published.Profile is not null, "governance-publish-" + published.Outcome.ReasonCode);
        var profile = published.Profile!;
        Require(profile.DevelopmentOnly && !profile.ProductionAuthority && !profile.CanActivate, "governance-development-boundary");
        var before = await query.GetValidityAsync(profile.Reference, CurrentInvocation(sessions)).ConfigureAwait(true);
        Require(before.Value?.Verification == CalibrationVerificationState.Missing, "governance-verification-missing");
        var payload = DevelopmentPhysicalVerifier.CreateEvidence(profile, referenceOffset: 0);
        var metrics = DevelopmentPhysicalVerifier.Measure(profile, payload);
        var submission = new PhysicalCalibrationVerificationSubmission(PhysicalProcedure, IndependentReference,
            DateTimeOffset.UtcNow.AddSeconds(-1), metrics, payload);
        var verify = new RecordPhysicalCalibrationVerificationCommand(Guid.NewGuid(), CurrentInvocation(sessions), profile.Reference,
            submission, "Check independent synthetic reference points");
        var verified = await governance.RecordPhysicalVerificationAsync(verify with { Invocation = await Authorize(verify,
            Permission.RecordPhysicalCalibrationVerification, AuditedCommandKind.RecordPhysicalCalibrationVerification) }).ConfigureAwait(true);
        Require(verified.Verification is { Passed: true }, "governance-physical-" + verified.Outcome.ReasonCode);
        var current = await query.GetValidityAsync(profile.Reference, CurrentInvocation(sessions)).ConfigureAwait(true);
        Require(current.Value?.Verification == CalibrationVerificationState.Current && !current.Value.CanAdmitNewProductionTrigger,
            "governance-current-state");
        var replaySubmission = new PhysicalCalibrationVerificationSubmission(PhysicalProcedure, IndependentReference,
            DateTimeOffset.UtcNow, metrics, payload);
        var replay = new RecordPhysicalCalibrationVerificationCommand(Guid.NewGuid(), CurrentInvocation(sessions), profile.Reference,
            replaySubmission, "Verify repeated bytes cannot renew the interval");
        var replayResult = await governance.RecordPhysicalVerificationAsync(replay with { Invocation = await Authorize(replay,
            Permission.RecordPhysicalCalibrationVerification, AuditedCommandKind.RecordPhysicalCalibrationVerification) }).ConfigureAwait(true);
        Require(replayResult.Outcome.Disposition == CommandDisposition.Rejected &&
            replayResult.Outcome.ReasonCode == "PhysicalVerificationEvidenceAlreadyRecorded", "governance-replay-not-rejected");
        var badPayload = DevelopmentPhysicalVerifier.CreateEvidence(profile, referenceOffset: 2);
        var badSubmission = new PhysicalCalibrationVerificationSubmission(PhysicalProcedure, IndependentReference,
            DateTimeOffset.UtcNow, DevelopmentPhysicalVerifier.Measure(profile, badPayload), badPayload);
        var fail = new RecordPhysicalCalibrationVerificationCommand(Guid.NewGuid(), CurrentInvocation(sessions), profile.Reference,
            badSubmission, "Retain a deliberately failing independent development reference");
        var failed = await governance.RecordPhysicalVerificationAsync(fail with { Invocation = await Authorize(fail,
            Permission.RecordPhysicalCalibrationVerification, AuditedCommandKind.RecordPhysicalCalibrationVerification) }).ConfigureAwait(true);
        Require(failed.Verification is { Passed: false, ValidUntilUtc: null }, "governance-failed-record-missing");
        var final = await query.GetValidityAsync(profile.Reference, CurrentInvocation(sessions)).ConfigureAwait(true);
        Require(final.Value?.Verification == CalibrationVerificationState.Failed, "governance-failed-state");
        var exact = await query.ReadProfileAsync(profile.Reference, CurrentInvocation(sessions)).ConfigureAwait(true);
        Require(exact.Value?.Content.Coefficients.ContentHash == profile.Content.Coefficients.ContentHash, "governance-coefficients-changed");
        var runtimeState = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!runtimeState.Ready && !final.Value!.CanAdmitNewProductionTrigger, "governance-production-boundary");
        await File.WriteAllTextAsync(Path.Combine(arguments.Directory, "calibration-governance.json"), JsonSerializer.Serialize(new
        {
            result = "Pass", schema = 15, validationIds = new[] { "V127-N01" }, profile = profile.Reference,
            coefficientHash = profile.Content.Coefficients.ContentHash, candidate, policy = policy.Reference,
            evaluation = evaluated.Evaluation.Reference, passingVerification = verified.Verification!.Reference,
            failingVerification = failed.Verification!.Reference, validUntilUtc = verified.Verification.ValidUntilUtc,
            states = new[] { "Missing", "Current", "Failed" }, replayRejected = true,
            developmentOnly = profile.DevelopmentOnly, productionAuthority = profile.ProductionAuthority,
            canActivate = profile.CanActivate, ready = runtimeState.Ready,
            canAdmitNewProductionTrigger = final.Value!.CanAdmitNewProductionTrigger,
            actualProductionTrigger = "NotRun", physicalMetrology = "NotRun"
        }, JsonOptions())).ConfigureAwait(true);
    }

    internal static async Task RestartGovernanceAsync(CalibrationConsumerArguments arguments, string password)
    {
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(arguments.Directory, "calibration-governance.json")));
        var saved = report.RootElement;
        var selected = saved.GetProperty("profile");
        var reference = new CalibrationProfileReference(selected.GetProperty("profileId").GetGuid(),
            selected.GetProperty("version").GetInt64(), selected.GetProperty("contentHash").GetString()!);
        var services = new ServiceCollection();
        services.AddSharpInspectSqliteRuntime(CreateStoreOptions(arguments), TimeSpan.FromMilliseconds(20));
        await using var container = services.BuildServiceProvider();
        var runtime = container.GetRequiredService<IStationRuntime>();
        var sessions = container.GetRequiredService<IInteractiveSessionService>();
        var query = container.GetRequiredService<ICalibrationGovernanceQuery>();
        await WaitForRuntimeAsync(runtime, value => value.AuditIntegrity?.State == AuditIntegrityState.Verified).ConfigureAwait(true);
        await SignInAsync(sessions, runtime, arguments.UserName, arguments.ExpectedPrincipal, password).ConfigureAwait(true);
        var before = await DatabaseHashAsync(arguments.DatabasePath).ConfigureAwait(true);
        var profile = await query.ReadProfileAsync(reference, CurrentInvocation(sessions)).ConfigureAwait(true);
        var validity = await query.GetValidityAsync(reference, CurrentInvocation(sessions)).ConfigureAwait(true);
        var after = await DatabaseHashAsync(arguments.DatabasePath).ConfigureAwait(true);
        Require(profile.Value?.Content.Coefficients.ContentHash == saved.GetProperty("coefficientHash").GetString() &&
            validity.Value?.Verification == CalibrationVerificationState.Failed && before == after,
            "governance-restart-evidence-mismatch");
        var runtimeState = await runtime.GetSnapshotAsync().ConfigureAwait(true);
        Require(!runtimeState.Ready && !validity.Value!.CanAdmitNewProductionTrigger, "governance-restart-production-boundary");
        await File.WriteAllTextAsync(Path.Combine(arguments.Directory, "calibration-governance-restart.json"), JsonSerializer.Serialize(new
        {
            result = "Pass", schema = 15, validationIds = new[] { "V127-N02" }, profile = reference,
            coefficientHash = profile.Value!.Content.Coefficients.ContentHash, verificationState = "Failed",
            readOnlyQueryDatabaseUnchanged = before == after, openedDevices = 0,
            developmentOnly = profile.Value.DevelopmentOnly, ready = runtimeState.Ready,
            productionAuthority = profile.Value.ProductionAuthority,
            canAdmitNewProductionTrigger = validity.Value!.CanAdmitNewProductionTrigger,
            actualProductionTrigger = "NotRun", physicalMetrology = "NotRun"
        }, JsonOptions())).ConfigureAwait(true);
    }

    /// <summary>Pure synthetic fixture; it is not a physical instrument or a production-qualified verifier.</summary>
    private sealed class DevelopmentPhysicalVerifier : IPhysicalCalibrationVerificationProcedure
    {
        public RecipeContractReference ProcedureContract => PhysicalProcedure;
        public RecipeContractReference EvidenceContract => PhysicalEvidence;
        public IReadOnlyList<CalibrationQualityMetric> Evaluate(PhysicalCalibrationVerificationContext context,
            CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Measure(context.Profile, context.Evidence); }

        internal static PhysicalCalibrationVerificationEvidencePayload CreateEvidence(PublishedCalibrationProfileVersion profile,
            double referenceOffset)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(1); writer.Write(profile.ContentHash); writer.Write(3);
            foreach (var point in new[] { new PlanarPoint(30, 30), new PlanarPoint(50, 40), new PlanarPoint(70, 50) })
            {
                // The frozen renderer's independent plane-to-image truth, not the fitted Profile.
                var denominator = .0012 * point.X + .0008 * point.Y + 1;
                writer.Write((4.2 * point.X + .35 * point.Y + 100) / denominator);
                writer.Write((.15 * point.X + 3.8 * point.Y + 75) / denominator);
                writer.Write(point.X + referenceOffset); writer.Write(point.Y);
            }
            writer.Flush();
            return new(PhysicalEvidence, stream.ToArray());
        }
        internal static IReadOnlyList<CalibrationQualityMetric> Measure(PublishedCalibrationProfileVersion profile,
            PhysicalCalibrationVerificationEvidencePayload evidence)
        {
            if (evidence.Format != PhysicalEvidence) throw new InvalidOperationException("DevelopmentPhysicalFormatMismatch");
            using var stream = new MemoryStream(evidence.GetBytes(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadInt32() != 1 || reader.ReadString() != profile.ContentHash || reader.ReadInt32() != 3)
                throw new InvalidOperationException("DevelopmentPhysicalProfileMismatch");
            var coefficients = PlanarHomographyResultCodec.DecodeCoefficients(profile.Content.Coefficients);
            var maximum = 0d;
            for (var i = 0; i < 3; i++)
            {
                var image = new PlanarPoint(reader.ReadDouble(), reader.ReadDouble());
                var reference = new PlanarPoint(reader.ReadDouble(), reader.ReadDouble());
                if (!coefficients.TryImageToPlane(image, out var measured)) throw new InvalidOperationException("DevelopmentPhysicalSupportMismatch");
                maximum = Math.Max(maximum, Math.Sqrt(Math.Pow(measured.X - reference.X, 2) + Math.Pow(measured.Y - reference.Y, 2)));
            }
            if (stream.Position != stream.Length) throw new InvalidOperationException("DevelopmentPhysicalTrailingBytes");
            return new[] { new CalibrationQualityMetric("MaximumError", maximum, "millimeters"), new CalibrationQualityMetric("PointCount", 3, "points") };
        }
    }
}
