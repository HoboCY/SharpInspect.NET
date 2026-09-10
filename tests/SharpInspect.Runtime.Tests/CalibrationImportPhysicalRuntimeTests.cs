using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class RecipeActivationServiceTests
{
    [Fact]
    public async Task V134_M01_RequiredLocalPhysicalVerificationPrecedesDevelopmentPublication()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true, importPhysicalRequired: true);
        var (candidate, evaluation) = await RevalidatePhysicalImportAsync(harness);
        var premature = new PublishImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(), Guid.NewGuid(),
            candidate.Reference, evaluation.Reference, null, "physical evidence still missing");
        var grant = await ImportGrantAsync(harness, premature, AuditedCommandKind.PublishImportedCalibration);
        var rejected = await harness.CalibrationImports.PublishImportAsync(premature with
        { Invocation = premature.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
        Assert.Equal("CalibrationImportLocalPhysicalVerificationRequired", rejected.Outcome.ReasonCode);
        var command = await CreatePhysicalImportCommandAsync(harness, candidate, evaluation);
        var verified = await harness.CalibrationImports.VerifyImportAsync(command);
        Assert.True(verified.Outcome.Disposition == CommandDisposition.Accepted, verified.Outcome.ReasonCode);
        var physical = Assert.IsType<ImportedCalibrationPhysicalVerification>(verified.Record);
        Assert.True(physical.Passed, string.Join(",", physical.Failures));
        Assert.NotNull(physical.Witness);
        Assert.Equal(command.CorrelationId, physical.Witness!.OperationId);
        Assert.Equal((await harness.Runtime.GetSnapshotAsync()).RuntimeEpoch, physical.Witness.RuntimeEpoch);
        var publish = new PublishImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(), Guid.NewGuid(),
            candidate.Reference, evaluation.Reference, physical.Reference, "local physical evidence passed");
        grant = await ImportGrantAsync(harness, publish, AuditedCommandKind.PublishImportedCalibration);
        var result = await harness.CalibrationImports.PublishImportAsync(publish with
        { Invocation = publish.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted, result.Outcome.ReasonCode);
        var profile = Assert.IsType<PublishedImportedCalibrationProfile>(result.Record);
        Assert.Equal(physical.Reference, profile.PhysicalVerification);
        Assert.False(profile.CanActivate);
        Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
    }

    [Fact]
    public async Task V134_M02_CallerCancellationPersistsRejectionWhileActualVerifierKeepsItsReservation()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true, importPhysicalRequired: true);
        var (candidate, evaluation) = await RevalidatePhysicalImportAsync(harness);
        var command = await CreatePhysicalImportCommandAsync(harness, candidate, evaluation);
        var procedure = harness.ImportPhysicalProcedure!;
        procedure.Block = true;
        using var caller = new CancellationTokenSource();
        var pending = harness.CalibrationImports.VerifyImportAsync(command, caller.Token).AsTask();
        try
        {
            await procedure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            await procedure.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var trace = await new SqliteCommandTraceQuery(harness.Options).QueryAsync(new(CorrelationId: command.CorrelationId));
            Assert.Contains(trace.Records, value => value.CommandKind == AuditedCommandKind.VerifyImportedCalibration &&
                value.Disposition == CommandDisposition.Rejected && value.ReasonCode == "CalibrationImportCancelled");
            var before = harness.CameraProvider.ApplyCount;
            var camera = (await harness.Camera.GetSetupAsync("TopCamera", harness.ActivationInvocation())).Snapshot!;
            var request = new CameraDebugConfigurationRequest(Guid.NewGuid(), harness.ActivationInvocation(), "TopCamera",
                camera.Binding!.Revision, camera.Binding.RevisionHash, camera.Requested!, "actual verifier still running");
            var grant = await harness.IssueGrantAsync(Permission.ManageCameraBindings, request.OperationId,
                "TopCamera", AuditedCommandKind.ApplyCameraDebugConfiguration);
            var apply = await harness.Camera.ApplyDebugConfigurationAsync(request with
            { Invocation = request.Invocation with { StepUpGrantId = grant.GrantId } });
            Assert.False(apply.Succeeded);
            Assert.Equal(before, harness.CameraProvider.ApplyCount);
        }
        finally { procedure.Release(); }
    }

    [Fact]
    public async Task V134_M03_LocalStopRemainsPendingUntilActualPhysicalVerificationRetires()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true, importPhysicalRequired: true);
        var (candidate, evaluation) = await RevalidatePhysicalImportAsync(harness);
        var command = await CreatePhysicalImportCommandAsync(harness, candidate, evaluation);
        var procedure = harness.ImportPhysicalProcedure!;
        procedure.Block = true;
        var pending = harness.CalibrationImports.VerifyImportAsync(command).AsTask();
        var stop = new GracefulProductionStopCommand(Guid.NewGuid(), harness.ActivationInvocation());
        try
        {
            await procedure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = await harness.Runtime.SubmitAsync(stop);
            Assert.True(stopped.Disposition == CommandDisposition.Accepted, stopped.ReasonCode);
            await procedure.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var rejected = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, rejected.Outcome.Audit);
            var snapshot = await harness.Runtime.GetSnapshotAsync();
            Assert.Equal(stop.CorrelationId, snapshot.LastCommand!.CorrelationId);
            Assert.Equal(OperationState.Pending, snapshot.LastCommand.State);
            Assert.False(snapshot.Ready);
            var trace = await new SqliteCommandTraceQuery(harness.Options).QueryAsync(new(CorrelationId: stop.CorrelationId));
            Assert.DoesNotContain(trace.Records, value => value.Phase == CommandAuditPhase.Completed);
        }
        finally { procedure.Release(); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await harness.Runtime.GetSnapshotAsync()).LastCommand?.State == OperationState.Pending)
            await Task.Delay(20, timeout.Token);
        Assert.Equal("LocallyDisarmed", (await harness.Runtime.GetSnapshotAsync()).LastCommand!.ReasonCode);
    }

    [Fact]
    public async Task V134_M04_SessionLockCancelsActualVerificationAndKeepsARejectedCommandFact()
    {
        await using var harness = await ActivationHarness.CreateAsync(enableImports: true, importPhysicalRequired: true);
        var (candidate, evaluation) = await RevalidatePhysicalImportAsync(harness);
        var command = await CreatePhysicalImportCommandAsync(harness, candidate, evaluation);
        var procedure = harness.ImportPhysicalProcedure!;
        procedure.Block = true;
        var pending = harness.CalibrationImports.VerifyImportAsync(command).AsTask();
        try
        {
            await procedure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var locked = await harness.Sessions.LockAsync(harness.Sessions.Current.SessionId, SessionLockReason.UserRequested);
            Assert.True(locked.Succeeded, locked.ReasonCode);
            await procedure.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var rejected = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CommandDisposition.Rejected, rejected.Outcome.Disposition);
            Assert.Equal(AuditPersistence.Persisted, rejected.Outcome.Audit);
            var trace = await new SqliteCommandTraceQuery(harness.Options).QueryAsync(new(CorrelationId: command.CorrelationId));
            Assert.Contains(trace.Records, value => value.Disposition == CommandDisposition.Rejected);
            Assert.False((await harness.Runtime.GetSnapshotAsync()).Ready);
        }
        finally { procedure.Release(); }
    }

    private static async Task<(ImportedCalibrationCandidate Candidate, ImportedCalibrationEvaluation Evaluation)>
        RevalidatePhysicalImportAsync(ActivationHarness harness)
    {
        var context = await PrepareImportContextAsync(harness);
        var (_, candidate) = await ImportCandidateAsync(harness, context.Package);
        var command = new RevalidateImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(), candidate.Reference,
            context.Requirement, context.Camera.Binding!, context.Imaging, "local policy requires physical verification");
        var grant = await ImportGrantAsync(harness, command, AuditedCommandKind.RevalidateImportedCalibration);
        var result = await harness.CalibrationImports.RevalidateImportAsync(command with
        { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } });
        Assert.True(result.Outcome.Disposition == CommandDisposition.Accepted, result.Outcome.ReasonCode);
        var evaluation = Assert.IsType<ImportedCalibrationEvaluation>(result.Record);
        Assert.True(evaluation.Passed, string.Join(",", evaluation.Failures));
        return (candidate, evaluation);
    }

    private static async Task<VerifyImportedCalibrationCommand> CreatePhysicalImportCommandAsync(ActivationHarness harness,
        ImportedCalibrationCandidate candidate, ImportedCalibrationEvaluation evaluation)
    {
        var policy = harness.ImportProcedure!.Policy.PhysicalVerification;
        var submission = new PhysicalCalibrationVerificationSubmission(policy.ProcedureContract!, policy.IndependentReference!,
            DateTimeOffset.UtcNow, new[] { new CalibrationQualityMetric("Scale", 0.75, "mm/pixel") },
            new PhysicalCalibrationVerificationEvidencePayload(policy.EvidenceContract!, new byte[] { 75 }));
        var command = new VerifyImportedCalibrationCommand(Guid.NewGuid(), harness.ActivationInvocation(), candidate.Reference,
            evaluation.Reference, submission, "verify against independent local evidence");
        var grant = await harness.IssueGrantAsync(Permission.RecordPhysicalCalibrationVerification, command.CorrelationId,
            command.AuthorizationTarget, AuditedCommandKind.VerifyImportedCalibration);
        return command with { Invocation = command.Invocation with { StepUpGrantId = grant.GrantId } };
    }
}
