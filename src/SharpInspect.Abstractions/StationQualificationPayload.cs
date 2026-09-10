using System.Collections.ObjectModel;
using System.Globalization;

namespace SharpInspect.Abstractions;

/// <summary>Encoded qualification evidence for an isolated facility. It is never a production payload.</summary>
public sealed class StationQualificationPayload
{
    internal StationQualificationPayload(Guid sessionId, QualificationRunId runId, string contextHash,
        PlcResultContractBinding binding, uint controllerEpoch, uint cycleSequence,
        ExecutionStatus executionStatus, InspectionDecision decision, string? reasonCode,
        IEnumerable<PlcRegisterSegment> segments)
    {
        SessionId = RecipeActivationValidation.RequiredGuid(sessionId, nameof(sessionId));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        ContextHash = RecipeActivationValidation.Hash(contextHash, nameof(contextHash));
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ControllerEpoch = controllerEpoch;
        CycleSequence = cycleSequence;
        ExecutionStatus = AlgorithmConfigurationValidation.Enum(executionStatus, nameof(executionStatus));
        Decision = AlgorithmConfigurationValidation.Enum(decision, nameof(decision));
        ReasonCode = reasonCode;
        Segments = AlgorithmContractValidation.Copy(segments.OrderBy(value => value.StartRegister),
            nameof(segments), 2048);
        if (Segments.Count == 0) throw new ArgumentException("PlcCompletePayloadRequired");
        for (var index = 1; index < Segments.Count; index++)
            if (Segments[index - 1].StartRegister + Segments[index - 1].RegisterCount > Segments[index].StartRegister)
                throw new ArgumentException("PlcRegisterOverlap");
        WireContentHash = PlcResultPayloadSnapshot.ComputeWireHash(Segments);
        ContentHash = AlgorithmContractValidation.HashParts(new[]
        {
            "sharpinspect-station-qualification-payload-v1", SessionId.ToString("D"), RunId.Value.ToString("D"),
            ContextHash, Binding.ContentHash, ControllerEpoch.ToString(CultureInfo.InvariantCulture),
            CycleSequence.ToString(CultureInfo.InvariantCulture), ExecutionStatus.ToString(), Decision.ToString(),
            ReasonCode, WireContentHash
        });
    }

    public Guid SessionId { get; }
    public QualificationRunId RunId { get; }
    public string ContextHash { get; }
    public PlcResultContractBinding Binding { get; }
    public uint ControllerEpoch { get; }
    public uint CycleSequence { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public InspectionDecision Decision { get; }
    public string? ReasonCode { get; }
    public ReadOnlyCollection<PlcRegisterSegment> Segments { get; }
    public string WireContentHash { get; }
    public string ContentHash { get; }
    public bool ProductionAuthority => false;
}
