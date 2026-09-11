using System.Diagnostics;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Identity;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class ProductionRecoveryAuthorizationTests
{
    [Fact]
    public void V144_I01_SafetyEvidenceRoundTripsTargetAndIndependentCaptureBinding()
    {
        var runtimeEpoch = Guid.NewGuid();
        var command = NewCommand();
        var capture = NewCapture(runtimeEpoch);
        var attemptId = Guid.NewGuid();

        var encoded = IdentityAuditEvent.CreateProductionRecoverySafetyEvidence(attemptId,
            command.CorrelationId, command.InspectionId, command.ExpectedEventHash,
            command.ReasonCode, command.Disposition.ToString(), command.DispositionNote, capture);

        Assert.StartsWith("v3.", encoded);
        Assert.InRange(encoded.Length, 4, 2048);
        Assert.True(IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(encoded,
            out var evidence));
        Assert.Equal(attemptId, evidence.AttemptId);
        Assert.Equal(command.InspectionId, evidence.InspectionId);
        Assert.Equal(command.ExpectedEventHash, evidence.ExpectedEventHash);
        Assert.Equal(command.ReasonCode, evidence.ReasonCode);
        Assert.Equal(command.Disposition, evidence.Disposition);
        Assert.Equal(command.AuthorizationTarget, evidence.AuthorizationTarget);
        Assert.Equal(runtimeEpoch, evidence.RuntimeEpoch);
        Assert.Equal(capture.Source.SourceEpoch, evidence.SourceEpoch);
        Assert.Equal(capture.Source.SourceGeneration, evidence.SourceGeneration);
        Assert.Equal(capture.Observation!.Binding.ContentHash, evidence.ObservationBindingHash);
        Assert.Equal(capture.Observation.Status, evidence.ObservationStatus!.Value);
        Assert.Equal(capture.Observation.SourceEpoch, evidence.ObservationSourceEpoch!.Value);
        Assert.Equal(capture.Observation.SourceGeneration, evidence.ObservationSourceGeneration!.Value);
        Assert.Equal(capture.Observation.ObservedAtUtc, evidence.ObservationObservedAtUtc!.Value);
        Assert.Equal(capture.Observation.MonotonicTimestamp, evidence.ObservationMonotonicTimestamp!.Value);
        Assert.Equal(capture.Observation.MonotonicFrequency, evidence.ObservationMonotonicFrequency!.Value);
    }

    [Fact]
    public void V144_I02_HashOnlyOrMalformedSafetyEvidenceIsRejected()
    {
        Assert.False(IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(
            "v3." + new string('A', 64), out _));
        Assert.False(IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(
            "v3.not-base64", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V144_I03_RecoveryAuthorizationEnvelopeVerifiesAtSchema30(bool fullUnicodeReason)
    {
        var original = NewCommand();
        var command = fullUnicodeReason ? new ManualProductionRecoveryCommand(original.CorrelationId,
            original.Invocation, original.InspectionId, original.ExpectedEventHash,
            new string('停', 256), original.Disposition, original.DispositionNote) : original;
        var principalId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var capture = NewCapture(Guid.NewGuid());
        var evidence = IdentityAuditEvent.CreateProductionRecoverySafetyEvidence(
            Guid.NewGuid(), command.CorrelationId, command.InspectionId,
            command.ExpectedEventHash, command.ReasonCode, command.Disposition.ToString(),
            command.DispositionNote, capture);
        var identity = new IdentityAuditEvent(Guid.NewGuid(),
            IdentityEventKind.ProductionRecoveryAuthorized, DateTimeOffset.UtcNow,
            "recovery-station", principalId, null, null, null,
            "ProductionRecoveryAuthorized", PasswordPolicyVersion: "password-v1",
            BlocklistId: "blocklist", BlocklistVersion: "v1", HashBaselineVersion: "hash-v1",
            HashTargetCost: 1, AuthenticationPolicyId: "auth-policy",
            AuthenticationPolicyVersion: "v1", AuthenticationPolicyHash: new string('F', 64),
            ProtectedAttemptIdentifier: new string('A', 64), SessionId: sessionId,
            AuthorizationPolicyId: "policy", AuthorizationPolicyVersion: "v1",
            AuthorizationPolicyHash: new string('B', 64), ActorPrincipalId: principalId,
            CommandCorrelationId: command.CorrelationId, StepUpGrantId: grantId,
            RequiredPermission: Permission.ManualRecovery.ToString(), AuthorizationRevision: 4,
            ActionTargetId: command.AuthorizationTarget,
            BoundCommandCorrelationId: command.CorrelationId,
            ActionCommandKind: AuditedCommandKind.ManualProductionRecovery.ToString(),
            OperationId: command.InspectionId, RecoverySafetyEvidence: evidence);

        var payload = identity.Encode(1, ProductionRecoveryStoreOptions.SchemaVersion);

        Assert.Throws<InvalidOperationException>(() => IdentityAuditEvent.VerifyPayload(
            payload, 1, "recovery-station", PartIdentityStoreOptions.SchemaVersion));

        Assert.Equal(0, IdentityAuditEvent.VerifyPayload(payload, 1, "recovery-station",
            ProductionRecoveryStoreOptions.SchemaVersion));
        Assert.True(IdentityAuditEvent.TryReadProductionRecoveryAuthorization(payload, 1,
            "recovery-station", out var binding));
        Assert.Equal(IdentityEventKind.ProductionRecoveryAuthorized, binding.Kind);
        Assert.Equal(principalId, binding.PrincipalId);
        Assert.Equal(sessionId, binding.SessionId);
        Assert.Equal(command.InspectionId, binding.InspectionId);
        Assert.Equal(Permission.ManualRecovery.ToString(), binding.RequiredPermission);
        Assert.Equal(AuditedCommandKind.ManualProductionRecovery, binding.CommandKind);
        Assert.Equal(evidence, binding.SafetyEvidence);
    }

    [Fact]
    public void V144_I04_ManualRecoveryCommandTargetBindsEveryMutableInput()
    {
        var command = NewCommand();
        var changedReason = new ManualProductionRecoveryCommand(command.CorrelationId,
            command.Invocation, command.InspectionId, command.ExpectedEventHash,
            "DifferentReason", command.Disposition, command.DispositionNote);
        var changedDisposition = new ManualProductionRecoveryCommand(command.CorrelationId,
            command.Invocation, command.InspectionId, command.ExpectedEventHash,
            command.ReasonCode, PartDisposition.Scrapped, command.DispositionNote);
        var changedNote = new ManualProductionRecoveryCommand(command.CorrelationId,
            command.Invocation, command.InspectionId, command.ExpectedEventHash,
            command.ReasonCode, command.Disposition, "different-note");

        Assert.Equal(53, (int)AuditedCommandKind.ManualProductionRecovery);
        Assert.NotEqual(command.AuthorizationTarget, changedReason.AuthorizationTarget);
        Assert.NotEqual(command.AuthorizationTarget, changedDisposition.AuthorizationTarget);
        Assert.NotEqual(command.AuthorizationTarget, changedNote.AuthorizationTarget);
    }

    [Fact]
    public void V144_I05_CompleteUnicodeReasonRoundTripsWithoutSilentNarrowing()
    {
        var reason = new string('停', 256);
        var template = NewCommand();
        var command = new ManualProductionRecoveryCommand(template.CorrelationId,
            template.Invocation, template.InspectionId, template.ExpectedEventHash,
            reason, template.Disposition, template.DispositionNote);
        var encoded = IdentityAuditEvent.CreateProductionRecoverySafetyEvidence(Guid.NewGuid(),
            command.CorrelationId, command.InspectionId, command.ExpectedEventHash,
            reason, command.Disposition.ToString(), command.DispositionNote, NewCapture(Guid.NewGuid()));

        Assert.True(IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(encoded, out var evidence));
        Assert.Equal(reason, evidence.ReasonCode);
        Assert.Equal(command.AuthorizationTarget, evidence.AuthorizationTarget);
    }

    [Fact]
    public void V144_I06_TruncatedCanonicalBinaryIsARejectedEvidenceValue()
    {
        var bytes = new byte[34];
        bytes[0] = 3;
        Guid.NewGuid().ToByteArray().CopyTo(bytes, 1);
        Guid.NewGuid().ToByteArray().CopyTo(bytes, 17);
        var encoded = "v3." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(IdentityAuditEvent.TryDecodeProductionRecoverySafetyEvidence(encoded, out _));
    }

    private static ManualProductionRecoveryCommand NewCommand() =>
        new(Guid.NewGuid(), new CommandInvocation(CommandSource.PhysicalConsole,
            Guid.NewGuid().ToString("D"), Guid.NewGuid()), Guid.NewGuid(),
            new string('A', 64), "UnresolvedDelivery", PartDisposition.Isolated, "hold-for-review");

    private static ProductionRecoverySafetyCapture NewCapture(Guid runtimeEpoch)
    {
        var source = new ProductionRecoverySafetySourceState(Guid.NewGuid(), 17, true);
        var binding = new ProductionRecoverySafetyProviderBinding("test-safety", "v1",
            "recovery-station", new string('C', 64), "physical-stop", "v1",
            new string('D', 64), "SharpInspect.Tests.PhysicalStopProvider",
            new string('E', 64), TimeSpan.FromSeconds(2));
        var observation = new ProductionRecoverySafetyObservation(binding,
            ProductionRecoverySafetyObservationStatus.SafeLineStopped,
            "PhysicalLineStopped", source.SourceEpoch, source.SourceGeneration,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        return new ProductionRecoverySafetyCapture(runtimeEpoch, true,
            "PhysicalLineStopped", 3, observation, source);
    }
}
