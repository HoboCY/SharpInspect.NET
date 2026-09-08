using SharpInspect.Abstractions;
using SharpInspect.Runtime;
using SharpInspect.Runtime.Calibration;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class CalibrationGovernanceBindingTests
{
    [Fact]
    public void V127_B01_CommandRecordAuthorizationAndAuditBindingsRejectEveryMutation()
    {
        var fixture = CalibrationGovernanceCodecTests.CreateFixture();
        var command = CreatePolicyCommand(fixture, Guid.NewGuid());
        var proof = CreateProof(fixture.PolicyRevision, command);

        // The exact command, record, identity authorization and audit fact form one
        // immutable binding and must pass together.
        SqliteCommandStore.ValidateGovernanceCommandBinding(
            command, fixture.PolicyRevision, proof.Authorization, proof.Fact);

        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            command, fixture.PolicyRevision,
            proof.Authorization with { ActionTargetId = "wrong-action-target" }, proof.Fact));

        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            command, fixture.PolicyRevision,
            proof.Authorization with { PrincipalId = Guid.NewGuid() }, proof.Fact));

        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            command, fixture.PolicyRevision,
            proof.Authorization with
            {
                AuthorizationRevision = fixture.PolicyRevision.Actor.AuthorizationRevision + 1
            }, proof.Fact));

        var timestampDrift = new CalibrationAcceptancePolicyRevision(
            fixture.PolicyRevision.Position, fixture.PolicyRevision.OperationId,
            fixture.PolicyRevision.Policy, fixture.PolicyRevision.Previous,
            fixture.PolicyRevision.Actor, fixture.PolicyRevision.RecordedAtUtc.AddTicks(1));
        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            command, timestampDrift, proof.Authorization, proof.Fact));

        var wrongPolicyCommand = CreatePolicyCommand(fixture, command.Invocation.StepUpGrantId!.Value,
            DifferentPolicy(fixture.Policy));
        var wrongPolicyProof = CreateProof(fixture.PolicyRevision, wrongPolicyCommand);
        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            wrongPolicyCommand, fixture.PolicyRevision,
            wrongPolicyProof.Authorization, wrongPolicyProof.Fact));

        var wrongPrevious = new RecipeContractReference(
            fixture.Policy.Reference.Id, fixture.Policy.Reference.Version,
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        var wrongPreviousCommand = CreatePolicyCommand(fixture,
            command.Invocation.StepUpGrantId!.Value, fixture.Policy, wrongPrevious);
        var wrongPreviousProof = CreateProof(fixture.PolicyRevision, wrongPreviousCommand);
        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            wrongPreviousCommand, fixture.PolicyRevision,
            wrongPreviousProof.Authorization, wrongPreviousProof.Fact));

        var noGrantCommand = CreatePolicyCommand(fixture, grantId: null);
        var noGrantProof = CreateProof(fixture.PolicyRevision, noGrantCommand);
        AssertBindingMismatch(() => SqliteCommandStore.ValidateGovernanceCommandBinding(
            noGrantCommand, fixture.PolicyRevision,
            noGrantProof.Authorization, noGrantProof.Fact));
    }

    [Fact]
    public void V127_B02_LiveProjectionRejectsAliasReplayByEvidenceAndIndependentContentHashes()
    {
        var fixture = CalibrationGovernanceCodecTests.CreateFixture();
        var original = fixture.Verification.Submission;
        var aliasIndependent = new RecipeContractReference(
            "independent-reference-alias", "2", original.IndependentReference.ContentHash);
        var aliasSubmission = new PhysicalCalibrationVerificationSubmission(
            original.ProcedureContract, aliasIndependent,
            original.PerformedAtUtc.AddMinutes(1), original.Metrics,
            new PhysicalCalibrationVerificationEvidencePayload(
                original.Evidence.Format, original.Evidence.GetBytes()));

        Assert.NotEqual(original.IndependentReference, aliasSubmission.IndependentReference);
        Assert.Equal(original.IndependentReference.ContentHash,
            aliasSubmission.IndependentReference.ContentHash);
        Assert.Equal(original.Evidence.ContentHash, aliasSubmission.Evidence.ContentHash);

        var actor = fixture.PolicyRevision.Actor;
        var command = new RecordPhysicalCalibrationVerificationCommand(
            Guid.NewGuid(), new CommandInvocation(CommandSource.PhysicalConsole,
                actor.PrincipalId.ToString("D"), actor.SessionId, Guid.NewGuid()),
            fixture.Verification.Profile, aliasSubmission, "V127 alias replay");
        var decision = CalibrationGovernanceProjection.Decide(command,
            new object[] { fixture.PolicyRevision, fixture.Profile, fixture.Verification },
            session: null, actor: actor, nowUtc: aliasSubmission.PerformedAtUtc.AddMinutes(1),
            sourceImagesFailure: null, computation: null);

        Assert.Null(decision.Record);
        Assert.Equal("PhysicalVerificationEvidenceAlreadyRecorded", decision.ReasonCode);

        // The codec fixture intentionally has no full CalibrationSessionEvidence for the
        // evaluation's candidate. ValidateRecords replay coverage is therefore NOT_RUN here;
        // it belongs in the real schema-15 runtime fixture rather than a fabricated loader.
    }

    private static PublishCalibrationAcceptancePolicyCommand CreatePolicyCommand(
        CalibrationGovernanceCodecTests.Fixture fixture, Guid? grantId,
        CalibrationAcceptancePolicy? policy = null,
        RecipeContractReference? expectedPrevious = null) =>
        new(fixture.PolicyRevision.OperationId,
            new CommandInvocation(CommandSource.PhysicalConsole,
                fixture.PolicyRevision.Actor.PrincipalId.ToString("D"),
                fixture.PolicyRevision.Actor.SessionId, grantId),
            policy ?? fixture.Policy, expectedPrevious, "V127 binding proof");

    private static (IdentityAuditEvent Authorization, CommandAuditFact Fact) CreateProof(
        CalibrationAcceptancePolicyRevision record, CalibrationGovernanceCommand command)
    {
        var actor = record.Actor;
        var time = record.RecordedAtUtc;
        var authorization = new IdentityAuditEvent(
            Guid.NewGuid(), IdentityEventKind.CalibrationGovernanceActionAuthorized,
            time, "V127-binding-station", actor.PrincipalId, null, null, null,
            "CalibrationAcceptancePolicyPublished",
            ActorPrincipalId: actor.PrincipalId,
            CommandCorrelationId: command.CorrelationId,
            StepUpGrantId: command.Invocation.StepUpGrantId,
            RequiredPermission: Permission.ManageCalibrationAcceptancePolicy.ToString(),
            AuthorizationRevision: actor.AuthorizationRevision,
            ActionTargetId: command.AuthorizationTarget,
            BoundCommandCorrelationId: command.CorrelationId,
            ActionCommandKind: AuditedCommandKind.PublishCalibrationAcceptancePolicy.ToString(),
            OperationId: command.CorrelationId,
            SessionId: actor.SessionId);
        var fact = new CommandAuditFact(
            Guid.NewGuid(), Guid.NewGuid(), command.CorrelationId, Guid.NewGuid(), time,
            AuditedCommandKind.PublishCalibrationAcceptancePolicy, CommandSource.PhysicalConsole,
            actor.PrincipalId.ToString("D"), actor.SessionId, command.Invocation.StepUpGrantId,
            CommandAuditPhase.Outcome, CommandDisposition.Accepted,
            "CalibrationAcceptancePolicyPublished", actor.PrincipalId.ToString("D"));
        return (authorization, fact);
    }

    private static CalibrationAcceptancePolicy DifferentPolicy(CalibrationAcceptancePolicy source) =>
        new("binding-other-policy", source.Version, source.Kind, source.LogicalPurpose,
            source.ProcedureContract, source.InputContract, source.CoefficientContract,
            source.ExtractionReceiptContract, source.ComputationEvidenceContract,
            source.Sample, source.Coverage, source.PoseDiversity,
            source.MaximumPerImageResidual, source.MaximumPerPointResidual,
            source.InvalidObservation, source.PhysicalVerification);

    private static void AssertBindingMismatch(Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal("CalibrationGovernanceCommandBindingMismatch", error.Message);
    }
}
