using System.Security.Cryptography;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V134_R01_PublicImportRequiresFreshGrantAndRetainsOnlyLocalCandidate()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true);
        var context = await PrepareImportContextAsync(harness);
        var denied = await harness.CalibrationImports.ImportAsync(new ImportCalibrationPackageCommand(Guid.NewGuid(),
            harness.ActivationInvocation(), context.Package, "missing step-up"));
        Assert.Equal(CommandDisposition.Rejected, denied.Outcome.Disposition);
        Assert.Equal("StepUpRequired", denied.Outcome.ReasonCode);
        Assert.Equal(AuditPersistence.Persisted, denied.Outcome.Audit);
        var (command, candidate) = await ImportCandidateAsync(harness, context.Package);
        Assert.NotEqual(context.SourceSessionId, candidate.CandidateId);
        Assert.NotEqual(context.SourceCandidateId, candidate.CandidateId);
        Assert.Equal("V134-External-Station", candidate.SourceStationId);
        Assert.Equal(context.Package.ContentHash, candidate.PackageHash);
        Assert.False(candidate.CanPublish);
        Assert.False(candidate.CanActivate);
        var query = await harness.CalibrationImportQuery.ReadImportOperationAsync(command.CorrelationId,
            harness.ActivationInvocation());
        Assert.True(query.Available, query.ReasonCode);
        Assert.Equal(candidate.ContentHash, query.Value!.ContentHash);
        var replay = await harness.CalibrationImports.ImportAsync(command);
        Assert.Equal(CommandDisposition.Rejected, replay.Outcome.Disposition);
        Assert.Equal("DuplicateCorrelationId", replay.Outcome.ReasonCode);
        var session = await harness.Store.ReadCalibrationSessionAsync(context.SourceSessionId);
        Assert.False(session.Available); // External source IDs never create a local session admission.
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V134_R02_PublicRevalidationAndPublicationPreservePriorActivationAndSourceLineage()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true);
        var activated = await harness.CreateFixtureService().ActivateAsync(await harness.AuthorizedActivationCommand());
        Assert.True(activated.Outcome.Disposition == CommandDisposition.Accepted,
            activated.Outcome.ReasonCode + ": " + string.Join(",", activated.Record?.Checks
                .Where(check => !check.Satisfied).Select(check => check.ReasonCode)
                ?? Array.Empty<string>()));
        var active = activated.Record!;
        var activeBefore = (await harness.Runtime.GetSnapshotAsync()).ActiveRecipe;
        var context = await PrepareImportContextAsync(harness);
        var (_, candidate) = await ImportCandidateAsync(harness, context.Package);
        var command = new RevalidateImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            candidate.Reference, context.Requirement, context.Camera.Binding!, context.Imaging, "local image recomputation");
        var grant = await ImportGrantAsync(harness, command, AuditedCommandKind.RevalidateImportedCalibration);
        var evaluated = await harness.CalibrationImports.RevalidateImportAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Accepted, evaluated.Outcome.Disposition);
        var evaluation = Assert.IsType<ImportedCalibrationEvaluation>(evaluated.Record);
        Assert.True(evaluation.Passed, string.Join(",", evaluation.Failures));
        Assert.Equal(context.CoefficientsHash, evaluation.Content.Coefficients.ContentHash);
        Assert.Equal(1, harness.ImportProcedure!.Extractions);
        Assert.Equal(1, harness.ImportProcedure.Computations);
        var publish = new PublishImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(), Guid.NewGuid(),
            candidate.Reference, evaluation.Reference, null, "publish local development result");
        grant = await ImportGrantAsync(harness, publish, AuditedCommandKind.PublishImportedCalibration);
        var published = await harness.CalibrationImports.PublishImportAsync(publish with
        { Invocation = publish.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Accepted, published.Outcome.Disposition);
        var profile = Assert.IsType<PublishedImportedCalibrationProfile>(published.Record);
        Assert.Equal(candidate.Reference, profile.Candidate);
        Assert.Equal(evaluation.Reference, profile.Evaluation);
        Assert.True(profile.DevelopmentOnly);
        Assert.False(profile.ProductionAuthority);
        Assert.False(profile.CanActivate);
        Assert.NotEqual(context.SourceCandidateId, profile.ProfileId);
        // Internal contract fixtures remain readable by exact reference, but the public
        // current-Active query deliberately never promotes them to local production authority.
        var retained = await harness.ActivationHistory.ReadAsync(active.Reference);
        Assert.True(retained.Available, retained.ReasonCode);
        Assert.Equal(active.ContentHash, retained.Record!.ContentHash);
        Assert.Equal(activeBefore, (await harness.Runtime.GetSnapshotAsync()).ActiveRecipe);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        await harness.WaitForVerifiedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V134_R03_MissingOrTamperedPersistedPackageFailsIndependentColdRead(bool missing)
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true);
        var context = await PrepareImportContextAsync(harness);
        var (command, candidate) = await ImportCandidateAsync(harness, context.Package);
        var path = Path.Combine(harness.Options.CalibrationImports!.Artifacts.ArtifactRoot, candidate.PackageHash + ".bin");
        var original = await File.ReadAllBytesAsync(path);
        try
        {
            if (missing) File.Delete(path);
            else
            {
                var changed = (byte[])original.Clone();
                changed[^1] ^= 1;
                await File.WriteAllBytesAsync(path, changed);
            }
            var read = await harness.CalibrationImportQuery.ReadImportOperationAsync(command.CorrelationId,
                harness.ActivationInvocation());
            Assert.False(read.Available);
            var audit = await new SharpInspect.Runtime.Integrity.SqliteAuditIntegrityQuery(harness.Options).VerifyAsync(new());
            Assert.Equal(AuditIntegrityState.Faulted, audit.State);
            Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        }
        finally { await File.WriteAllBytesAsync(path, original); }
    }

    [Fact]
    public async Task V134_R04_ForeignHardwareRevalidationIsAuditedWithoutChangingActive()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true);
        var activated = await harness.CreateFixtureService().ActivateAsync(await harness.AuthorizedActivationCommand());
        Assert.True(activated.Outcome.Disposition == CommandDisposition.Accepted,
            activated.Outcome.ReasonCode + ": " + string.Join(",", activated.Record?.Checks
                .Where(check => !check.Satisfied).Select(check => check.ReasonCode)
                ?? Array.Empty<string>()));
        var context = await PrepareImportContextAsync(harness, foreignDevice: true);
        var (_, candidate) = await ImportCandidateAsync(harness, context.Package);
        var command = new RevalidateImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(),
            candidate.Reference, context.Requirement, context.Camera.Binding!, context.Imaging, "reject foreign device");
        var grant = await ImportGrantAsync(harness, command, AuditedCommandKind.RevalidateImportedCalibration);
        var result = await harness.CalibrationImports.RevalidateImportAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, result.Outcome.Disposition);
        Assert.Equal(AuditPersistence.Persisted, result.Outcome.Audit);
        Assert.Null(result.Record);
        Assert.Equal(0, harness.ImportProcedure!.Extractions);
        var retained = await harness.ActivationHistory.ReadAsync(activated.Record!.Reference);
        Assert.True(retained.Available, retained.ReasonCode);
        Assert.Equal(activated.Record.ContentHash, retained.Record!.ContentHash);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
    }

    private static async Task<(ImportCalibrationPackageCommand Command, ImportedCalibrationCandidate Candidate)>
        ImportCandidateAsync(ActivationHarness harness, CalibrationExportPackage package)
    {
        var command = new ImportCalibrationPackageCommand(Guid.NewGuid(), harness.ActivationInvocation(), package,
            "retain untrusted external calibration");
        var grant = await ImportGrantAsync(harness, command, AuditedCommandKind.ImportCalibrationPackage);
        command = command with { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } };
        var result = await harness.CalibrationImports.ImportAsync(command);
        Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted, result.Outcome.ReasonCode);
        return (command, Assert.IsType<ImportedCalibrationCandidate>(result.Record));
    }

    private static Task<StepUpResult> ImportGrantAsync(ActivationHarness harness, CalibrationImportCommand command,
        AuditedCommandKind kind) => harness.IssueGrantAsync(Permission.PublishCalibration, command.CorrelationId,
            command.AuthorizationTarget, kind);

    private static async Task<ImportRuntimeContext> PrepareImportContextAsync(ActivationHarness harness, bool foreignDevice = false)
    {
        var invocation = harness.ActivationInvocation();
        var setup = await harness.Camera.GetSetupAsync("TopCamera", invocation);
        Assert.True(setup.Available, setup.ReasonCode);
        var camera = setup.Snapshot!;
        var binding = camera.Binding!;
        var declaration = new ImagingSetupChangeRequest(Guid.NewGuid(), invocation, "TopCamera", binding.Revision,
            binding.RevisionHash, 0, null, new("V134-lens", "fixed", "top", 100, "normal"), "local setup declared");
        var grant = await harness.IssueGrantAsync(Permission.ManageCameraBindings, declaration.OperationId,
            declaration.AuthorizationTarget, AuditedCommandKind.DeclareImagingSetup);
        var imagingResult = await harness.ImagingSetup.DeclareImagingSetupAsync(new(declaration.OperationId,
            invocation with { StepUpGrantId = grant.GrantId }, "TopCamera", binding.Revision, binding.RevisionHash,
            0, null, declaration.Definition, declaration.ChangeReason));
        Assert.True(imagingResult.Succeeded, imagingResult.ReasonCode);
        var imaging = ImagingSetupRevisionReference.FromRevision(imagingResult.Revision!);
        var policy = harness.ImportProcedure!.Policy;
        var policyCommand = new PublishCalibrationAcceptancePolicyCommand(Guid.NewGuid(), invocation, policy, null, "local policy");
        grant = await harness.IssueGrantAsync(Permission.ManageCalibrationAcceptancePolicy, policyCommand.CorrelationId,
            policyCommand.AuthorizationTarget, AuditedCommandKind.PublishCalibrationAcceptancePolicy);
        var policyResult = await harness.CalibrationGovernance.PublishPolicyAsync(policyCommand with
        { Invocation = invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(policyResult.Outcome.Disposition == CommandDisposition.Accepted, policyResult.Outcome.ReasonCode);
        var requirement = new CalibrationRequirement("TopCamera", policy.Kind, policy.LogicalPurpose,
            policy.CoefficientContract, policy.Reference);
        var sessionId = Guid.NewGuid();
        var frameId = Guid.NewGuid();
        var sourceCandidateId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var actorSession = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        var sourceBinding = foreignDevice ? new CameraBindingRevision(binding.Position, binding.LogicalRole, binding.Revision,
            binding.OperationId, binding.PreviousRevisionHash, binding.RevisionHash,
            new(binding.Target.Provider, "different-physical-device"), binding.AuthorPrincipalId, binding.AuthorSessionId,
            binding.AuthorAuthorizationRevision, binding.ChangeReason, binding.RecordedAtUtc) : binding;
        var plan = new CalibrationSessionPlan(requirement, harness.ImportProcedure.Descriptor,
            harness.ImportProcedure.InputCodec.EncodePayload(new byte[] { 1, 2, 3 }), camera.Requested!,
            new CalibrationEvidenceSelectionPolicy("external-selection", "1", 1, 1, 0));
        var start = new StartCalibrationSessionCommand(Guid.NewGuid(),
            new(CommandSource.PhysicalConsole, actor.ToString("D"), actorSession, Guid.NewGuid()), plan,
            sourceBinding.Revision, sourceBinding.RevisionHash, imaging, "external package evidence");
        var header = new CalibrationSessionHeader(sessionId, Guid.NewGuid(), Guid.NewGuid(), actor, actorSession, 1, now,
            start, sourceBinding, camera.Requested!, camera.Effective!, new string('A', 64));
        var geometry = camera.Effective!.RegionOfInterest;
        var pixels = Enumerable.Range(0, geometry.Width * geometry.Height).Select(value => (byte)value).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(pixels));
        var correlation = new ExecutionCorrelationId(ExecutionKind.Calibration, frameId);
        var metadata = new FrameMetadata(correlation, "TopCamera", geometry.Width, geometry.Height, geometry.Width,
            VisionPixelFormat.Mono8, null, now, camera.Effective);
        var provider = sourceBinding.Target.Provider;
        var provenance = new FrameProvenance(correlation, provider.Id, provider.Version, provider.AdapterPackageId,
            provider.AdapterVersion, "external-sdk", "1", null, sourceBinding.Target.StableDeviceIdentity, null, null,
            "Mono8", "CanonicalRows", false, false, null, null, new FrameAcquisitionMilestones(1, null, null, null, null));
        var frame = new CalibrationFrameEvidence(sessionId, frameId, metadata, provenance, hash, pixels.Length, hash + ".bin");
        var observation = new CalibrationObservationEvidence(Guid.NewGuid(), frame, plan.Procedure, plan.Input.ContentHash,
            new(new[] { new CalibrationImageFeature("corner", 1, 1) }, Array.Empty<CalibrationProcedureDiagnostic>()));
        var selection = CalibrationEvidenceSelection.Evaluate(header, new[] { frame }, new[] { observation },
            Array.Empty<CalibrationEvidenceExclusion>());
        var computed = harness.ImportProcedure.ResultFor(pixels);
        var candidate = new CalibrationCandidateEvidence(sourceCandidateId, sessionId, header.ContentHash,
            selection.SelectionHash, computed, now.AddSeconds(1));
        var evidence = new CalibrationSessionEvidence(header,
            new(sessionId, CalibrationSessionPhase.CandidateRetained, CalibrationSessionOutcome.Pending,
                1, 1, 0, sourceCandidateId, "CalibrationEvidenceSufficient", false),
            new[] { frame }, new[] { observation }, Array.Empty<CalibrationEvidenceExclusion>(), candidate, selection,
            new(camera.Requested!, camera.Effective));
        var package = CalibrationExportPackageCodec.Encode("V134-External-Station", evidence, policy,
            new[] { new CalibrationFrameImage(frame, pixels) });
        return new(package, requirement, camera, imaging, sessionId, sourceCandidateId, computed.Coefficients.ContentHash);
    }

    private sealed record ImportRuntimeContext(CalibrationExportPackage Package, CalibrationRequirement Requirement,
        CameraSetupSnapshot Camera, ImagingSetupRevisionReference Imaging, Guid SourceSessionId,
        Guid SourceCandidateId, string CoefficientsHash);
}
