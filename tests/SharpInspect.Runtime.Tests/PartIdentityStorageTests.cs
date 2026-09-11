using System.Security.Cryptography;
using System.Text;
using SharpInspect.Abstractions;
using SharpInspect.Runtime.Storage;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed class PartIdentityStorageTests
{
    [Fact]
    public void V143_S01_OptionalMissingEvidenceCanBeReferencedByNullableCorrection()
    {
        var evidence = OptionalMissingEvidence();
        var command = CorrectionCommand(evidence, previousValue: null);
        var correction = CorrectionEvent(evidence, command, oldValue: null);

        Assert.Equal(PartIdentityEvidenceState.NotProvided, correction.Evidence!.State);
        Assert.Null(correction.Evidence.Value);
        Assert.Null(correction.OldValue);
        Assert.Equal(evidence.ContentHash, correction.Evidence.ContentHash);
        Assert.Equal(command.AuthorizationTarget, correction.AuthorizationTarget);
        Assert.Equal(command.AdmissionContentHash, correction.AdmissionContentHash);
    }

    [Fact]
    public void V143_A01_CorrectionCannotInventPreviousValueForOptionalMissingEvidence()
    {
        var evidence = OptionalMissingEvidence();
        var command = CorrectionCommand(evidence, previousValue: null);

        var exception = Assert.Throws<ArgumentException>(() =>
            CorrectionEvent(evidence, command, oldValue: "FORGED"));

        Assert.Equal("PartIdentityCorrectionEvidenceMismatch", exception.Message);
    }

    [Fact]
    public void V143_A02_CorrectionTargetCannotBeSuppliedForAnotherPreviousValue()
    {
        var evidence = OptionalMissingEvidence();
        var exception = Assert.Throws<ArgumentException>(() =>
            CorrectionCommand(evidence, previousValue: null, authorizationTarget: Hash("wrong")));

        Assert.StartsWith("PartIdentityCorrectionAuthorizationTargetMismatch", exception.Message);
        Assert.Equal("authorizationTarget", exception.ParamName);
    }

    [Fact]
    public void V143_S02_RejectionContextRoundTripsNullRequirementAndReadEvidenceHash()
    {
        var context = new PartIdentityRejectionContext(requirement: null,
            connectionGeneration: 7, rawProofBytes: new byte[] { 1, 2, 3 },
            readEvidenceHash: Hash("complete-plc-read"));
        var rejected = new PartIdentityHistoryEvent(
            position: 1, previousHash: null, eventId: Guid.NewGuid(),
            kind: PartIdentityHistoryEventKind.RejectedTrigger, correlationId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(), runtimeEpoch: Guid.NewGuid(),
            stationId: "V143PartIdentityStation", controllerEpoch: 0, cycleSequence: 0,
            endpointBindingHash: Hash("endpoint"), evidence: null,
            reasonCode: "PartIdentityRequiredMissing",
            recordedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 2, TimeSpan.Zero),
            rejectionContext: context);

        var decoded = PartIdentityStorageCodec.Decode(PartIdentityStorageCodec.Encode(rejected));

        Assert.Equal(rejected.ContentHash, decoded.ContentHash);
        Assert.NotNull(decoded.RejectionContext);
        Assert.Null(decoded.RejectionContext!.Requirement);
        Assert.Equal(context.ReadEvidenceHash, decoded.RejectionContext.ReadEvidenceHash);
        Assert.Equal(context.RawProofBytes, decoded.RejectionContext.RawProofBytes);
    }

    [Fact]
    public void V143_A03_RejectionCannotUseAcceptedEvidenceProjection()
    {
        var evidence = OptionalMissingEvidence();
        var context = new PartIdentityRejectionContext(requirement: null,
            connectionGeneration: 1);

        var exception = Assert.Throws<ArgumentException>(() => new PartIdentityHistoryEvent(
            position: 1, previousHash: null, eventId: Guid.NewGuid(),
            kind: PartIdentityHistoryEventKind.RejectedTrigger, correlationId: Guid.NewGuid(),
            attemptId: Guid.NewGuid(), runtimeEpoch: Guid.NewGuid(),
            stationId: "V143PartIdentityStation", controllerEpoch: 0, cycleSequence: 0,
            endpointBindingHash: Hash("endpoint"), evidence: evidence,
            reasonCode: "PartIdentityRequiredMissing",
            recordedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 3, TimeSpan.Zero),
            rejectionContext: context));

        Assert.StartsWith("PartIdentityRejectedAcceptedEvidenceForbidden", exception.Message);
        Assert.Equal("evidence", exception.ParamName);
    }

    private static PartIdentityEvidence OptionalMissingEvidence()
    {
        var format = new PartIdentityFormat("V143.Part.Format", "1", 1, 32,
            " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-");
        var binding = new PartIdentityProviderBinding("V143.Part.Binding", "1", "Part.Code",
            PartIdentityProviderSourceKind.Staged, "V143.Part.Provider", "1", Hash("source"),
            format, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 1);
        var cycle = new PartIdentityCycleBinding(Guid.NewGuid(), Hash("endpoint"), 1, 61, 1);
        var observation = new PartIdentityProviderObservation(binding, cycle,
            PartIdentityObservationStatus.Missing, null, "PartIdentityMissing", 1,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 10, 1,
            Guid.NewGuid(), 1);
        return new PartIdentityEvidence(PartIdentityEvidenceState.NotProvided, null, observation);
    }

    private static CorrectProductionPartIdentityCommand CorrectionCommand(
        PartIdentityEvidence evidence, string? previousValue, string? authorizationTarget = null) =>
        new(Guid.NewGuid(), new CommandInvocation(CommandSource.PhysicalConsole,
                Guid.NewGuid().ToString("D"), Guid.NewGuid(), Guid.NewGuid()),
            Guid.NewGuid(), Hash("admission"), null, previousValue, "A-001",
            "PartIdentityCorrection", authorizationTarget);

    private static PartIdentityHistoryEvent CorrectionEvent(PartIdentityEvidence evidence,
        CorrectProductionPartIdentityCommand command, string? oldValue) =>
        new(position: 1, previousHash: null, eventId: Guid.NewGuid(),
            kind: PartIdentityHistoryEventKind.Correction, correlationId: command.CorrelationId,
            attemptId: Guid.NewGuid(), runtimeEpoch: evidence.Cycle.RuntimeEpoch,
            stationId: "V143PartIdentityStation", controllerEpoch: evidence.Cycle.ControllerEpoch,
            cycleSequence: evidence.Cycle.CycleSequence,
            endpointBindingHash: evidence.Cycle.EndpointBindingHash, evidence: evidence,
            reasonCode: command.ReasonCode, inspectionId: command.InspectionId,
            admissionContentHash: command.AdmissionContentHash,
            expectedPreviousCorrectionHash: command.ExpectedPreviousCorrectionHash,
            oldValue: oldValue, newValue: command.CorrectedValue,
            actorPrincipalId: Guid.NewGuid(), actorSessionId: Guid.NewGuid(),
            authorizationRevision: 1, stepUpGrantId: Guid.NewGuid(),
            authorizationTarget: command.AuthorizationTarget,
            recordedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
