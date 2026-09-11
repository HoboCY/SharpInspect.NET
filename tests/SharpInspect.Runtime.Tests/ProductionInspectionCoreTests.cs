using System.Diagnostics;
using SharpInspect.Abstractions;
using Xunit;

namespace SharpInspect.Runtime.Tests;

public sealed partial class ManualInspectionRuntimeTests
{
    [Fact]
    public async Task V142_C06_CoreUsesTheFrozenActivationAndExecutionClock()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var admission = await BuildAdmissionAsync(harness);
        var core = TimeoutCore(admission, executionAdmitted: 123, executionFrequency: Stopwatch.Frequency);
        var content = admission.ActivationSnapshot.Release.Source.Content;

        Assert.Equal(admission.ActivationSnapshot.PreparedAlgorithmInstanceId,
            core.PreparedAlgorithmInstanceId);
        Assert.Equal(content.Algorithm.Algorithm, core.Algorithm);
        Assert.Equal(content.Configuration.ContentHash, core.Configuration!.ContentHash);
        Assert.Equal(content.Algorithm.ResultSchema.ContentHash, core.ResultSchema!.ContentHash);
        Assert.Equal(123, core.ExecutionAdmittedMonotonicTimestamp);
        Assert.Equal(Stopwatch.Frequency, core.ExecutionMonotonicFrequency);

        var preparedMismatch = Assert.Throws<ArgumentException>(() => TimeoutCore(admission,
            prepared: Guid.NewGuid()));
        Assert.StartsWith("ProductionInspectionPreparedAlgorithmActivationMismatch", preparedMismatch.Message, StringComparison.Ordinal);

        var algorithmMismatch = Assert.Throws<ArgumentException>(() => TimeoutCore(admission,
            algorithm: new AlgorithmIdentity("other.algorithm", "1")));
        Assert.StartsWith("ProductionInspectionAlgorithmActivationMismatch", algorithmMismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V142_C07_CoreRejectsPayloadCycleAndOutcomeDrift()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var admission = await BuildAdmissionAsync(harness);
        var binding = admission.ActivationSnapshot.PlcResultContract;
        var wrongCycle = new PlcControllerCycle(admission.ControllerCycle.ControllerEpoch,
            checked(admission.ControllerCycle.CycleSequence + 1));
        var payload = new PlcResultPayloadSnapshot(binding, admission.InspectionId, wrongCycle,
            ExecutionStatus.Timeout, InspectionDecision.Unknown, "CameraAcquisitionTimeout",
            new[] { new PlcRegisterSegment(0, new byte[] { 0, 0 }) });

        var cycleMismatch = Assert.Throws<ArgumentException>(() => TimeoutCore(admission,
            plcPayload: payload));
        Assert.StartsWith("ProductionInspectionPayloadCycleMismatch", cycleMismatch.Message, StringComparison.Ordinal);

        var outcomePayload = new PlcResultPayloadSnapshot(binding, admission.InspectionId,
            admission.ControllerCycle, ExecutionStatus.Error, InspectionDecision.Unknown,
            "CameraAcquisitionTimeout", new[] { new PlcRegisterSegment(0, new byte[] { 0, 0 }) });
        var outcomeMismatch = Assert.Throws<ArgumentException>(() => TimeoutCore(admission,
            plcPayload: outcomePayload));
        Assert.StartsWith("ProductionInspectionPayloadOutcomeMismatch", outcomeMismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V142_C08_CoreRejectsNonProductionFrameProvenanceAndIncompleteSuccess()
    {
        await using var peer = ModbusQualificationTestServer.Start();
        await using var harness = await ManualHarness.CreateAsync(
            activationReadyDraft: true, productionPeer: peer);
        using var issuer = new ProductionTestIssuer();
        await PrepareProductionAsync(harness, issuer);

        var admission = await BuildAdmissionAsync(harness);
        var manualProvenance = new FrameProvenance(
            new ExecutionCorrelationId(ExecutionKind.Manual, admission.CorrelationId),
            "fixture-provider", "1", "fixture-adapter", "1", "fixture-sdk", "1", null,
            "fixture-device", null, null, "Mono8", "identity", false, false, null, null,
            new FrameAcquisitionMilestones(1, null, null, null, null));
        var provenanceMismatch = Assert.Throws<ArgumentException>(() => TimeoutCore(admission,
            frameProvenance: manualProvenance));
        Assert.StartsWith("ProductionInspectionProvenanceCorrelationMismatch", provenanceMismatch.Message, StringComparison.Ordinal);

        var content = admission.ActivationSnapshot.Release.Source.Content;
        var incompleteSuccess = Assert.Throws<ArgumentException>(() => new ProductionInspectionCore(
            admission: admission, state: ProductionInspectionState.CoreCommitted,
            executionStatus: ExecutionStatus.Success, decision: InspectionDecision.Pass,
            reasonCode: "AlgorithmPassed", acquisitionFailureKind: null,
            acquisitionFailureReasonCode: null, frameMetadata: null, frameProvenance: null,
            preparedAlgorithmInstanceId: admission.ActivationSnapshot.PreparedAlgorithmInstanceId,
            algorithm: content.Algorithm.Algorithm, configuration: content.Configuration,
            resultSchema: admission.ActivationSnapshot.PlcResultContract.ResultSchema,
            result: null, overlay: null, timing: null, plcPayload: null,
            structuredResultJson: null, structuredResultHash: null, partIdentity: null,
            committedAtUtc: DateTimeOffset.UtcNow,
            committedMonotonicTimestamp: Stopwatch.GetTimestamp()));
        Assert.StartsWith("ProductionInspectionSuccessEvidenceIncomplete", incompleteSuccess.Message, StringComparison.Ordinal);

        var missingFrequency = Assert.Throws<ArgumentException>(() => TimeoutCore(admission,
            executionAdmitted: 123, executionFrequency: null));
        Assert.StartsWith("ProductionInspectionExecutionTimingReferenceInvalid", missingFrequency.Message, StringComparison.Ordinal);
    }

    private static ProductionInspectionCore TimeoutCore(ProductionInspectionAdmission admission,
        Guid? prepared = null, AlgorithmIdentity? algorithm = null,
        AlgorithmConfigurationSnapshot? configuration = null,
        AlgorithmResultSchema? resultSchema = null,
        FrameProvenance? frameProvenance = null,
        PlcResultPayloadSnapshot? plcPayload = null,
        long? executionAdmitted = null, long? executionFrequency = null)
    {
        var content = admission.ActivationSnapshot.Release.Source.Content;
        return new ProductionInspectionCore(
            admission: admission, state: ProductionInspectionState.CoreCommitted,
            executionStatus: ExecutionStatus.Timeout, decision: InspectionDecision.Unknown,
            reasonCode: "CameraAcquisitionTimeout",
            acquisitionFailureKind: CameraAcquisitionFailureKind.TimedOut,
            acquisitionFailureReasonCode: "CameraAcquisitionTimeout", frameMetadata: null,
            frameProvenance: frameProvenance,
            preparedAlgorithmInstanceId: prepared ?? admission.ActivationSnapshot.PreparedAlgorithmInstanceId,
            algorithm: algorithm ?? content.Algorithm.Algorithm,
            configuration: configuration ?? content.Configuration,
            resultSchema: resultSchema ?? admission.ActivationSnapshot.PlcResultContract.ResultSchema,
            result: null, overlay: null, timing: null, plcPayload: plcPayload,
            structuredResultJson: null, structuredResultHash: null, partIdentity: null,
            committedAtUtc: DateTimeOffset.UtcNow,
            committedMonotonicTimestamp: Stopwatch.GetTimestamp(),
            retentionObligations: Array.Empty<TraceRetentionObligation>(), acquisitionStart: null,
            executionAdmittedMonotonicTimestamp: executionAdmitted,
            executionMonotonicFrequency: executionFrequency);
    }
}
