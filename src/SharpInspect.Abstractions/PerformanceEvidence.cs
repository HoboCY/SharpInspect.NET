using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>Actual process observations. A declared hash is never substituted for an unknown observed value.</summary>
public sealed record PerformanceRunHeader(Guid RunId, Guid RuntimeEpoch, string ContractHash,
    string ScenarioId, string ScenarioHash, long MonotonicFrequency, long StartedAt,
    DateTimeOffset StartedAtUtc, string FrameworkAssemblyHash, string AbstractionsAssemblyHash,
    string BuildConfiguration, string ProcessArchitecture, string? PowerProfileHash,
    bool DebuggerPresent, bool ProfilerPresent, string? DiagnosticProfileHash,
    string CollectorId, string CollectorVersion, string CollectorHash);

/// <summary>Fixed scalar references read from the verified production ledger, excluding payload and part identity.</summary>
public sealed record PerformanceDurableFact(long Position, Guid RuntimeEpoch, Guid InspectionId,
    Guid CorrelationId, uint ControllerEpoch, uint ControllerSequence, ProductionInspectionEventKind Kind,
    long Timestamp, string ContentHash, string? CoreHash, string? PayloadHash,
    ExecutionStatus? ExecutionStatus, InspectionDecision? Decision, string ReasonCode);

public sealed record PerformanceFrameObservation(ExecutionCorrelationId Correlation, long Timestamp,
    int Width, int Height, VisionPixelFormat PixelFormat, int? ValidBits, string? ValidPixelHash,
    PerformanceObservationOutcome Outcome, string ReasonCode);

public sealed record PerformanceCycleBinding(ExecutionCorrelationId Correlation, string? TargetConfigurationFingerprint);

/// <summary>Finite raw evidence. Sealing is independent from a passing scenario and grants no qualification.</summary>
public sealed class PerformanceRawCapture
{
    public PerformanceRawCapture(PerformanceContract contract, PerformanceRunHeader header,
        PerformanceEvidenceState state, long endedAt, long observationDrops, long firstMissingSequence,
        string terminalReason, IEnumerable<PerformanceEvent> events,
        IEnumerable<PerformanceResourceSample> resourceSamples, IEnumerable<PerformanceDurableFact> durableFacts,
        bool ledgerAvailable, string ledgerReasonCode, IEnumerable<PerformanceFrameObservation> frames,
        IEnumerable<PerformanceCycleBinding> cycleBindings)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        Header = header ?? throw new ArgumentNullException(nameof(header));
        if (!Enum.IsDefined(state) || endedAt < header.StartedAt || observationDrops < 0 || firstMissingSequence < 0)
            throw new ArgumentException("PerformanceCaptureTerminalInvalid");
        State = state; EndedAt = endedAt; ObservationDrops = observationDrops; FirstMissingSequence = firstMissingSequence;
        TerminalReason = AlgorithmContractValidation.Identifier(terminalReason, nameof(terminalReason));
        Events = AlgorithmContractValidation.Copy(events, nameof(events), contract.Capture.MaximumEvents);
        ResourceSamples = AlgorithmContractValidation.Copy(resourceSamples, nameof(resourceSamples), contract.Capture.MaximumResourceSamples);
        DurableFacts = AlgorithmContractValidation.Copy(durableFacts, nameof(durableFacts), contract.Capture.MaximumEvents);
        Frames = AlgorithmContractValidation.Copy(frames, nameof(frames), contract.Capture.MaximumEvents);
        CycleBindings = AlgorithmContractValidation.Copy(cycleBindings, nameof(cycleBindings), contract.Capture.MaximumEvents);
        LedgerAvailable = ledgerAvailable;
        LedgerReasonCode = AlgorithmContractValidation.Identifier(ledgerReasonCode, nameof(ledgerReasonCode));
    }
    public PerformanceContract Contract { get; }
    public PerformanceRunHeader Header { get; }
    public PerformanceEvidenceState State { get; }
    public long EndedAt { get; }
    public long ObservationDrops { get; }
    public long FirstMissingSequence { get; }
    public string TerminalReason { get; }
    public ReadOnlyCollection<PerformanceEvent> Events { get; }
    public ReadOnlyCollection<PerformanceResourceSample> ResourceSamples { get; }
    public ReadOnlyCollection<PerformanceDurableFact> DurableFacts { get; }
    public ReadOnlyCollection<PerformanceFrameObservation> Frames { get; }
    public ReadOnlyCollection<PerformanceCycleBinding> CycleBindings { get; }
    public bool LedgerAvailable { get; }
    public string LedgerReasonCode { get; }
}

public sealed record PerformanceMonitorSnapshot(bool Configured, string? ContractHash,
    bool BudgetViolation, long BudgetViolationCount, long ObservationLossCount,
    int SuccessfulRecoveryCycles, Guid? BaselineRunId, PerformanceEvidenceState? CaptureState,
    int CapturedEvents, int CapturedResourceSamples, int PhysicalResourceOperations, string ReasonCode);

public interface IPerformanceMonitoringQuery
{
    PerformanceMonitorSnapshot ReadPerformance();
    /// <summary>Returns immutable finite evidence only after the configured baseline capture terminates.</summary>
    ValueTask<PerformanceRawCapture?> ReadCaptureAsync(CancellationToken cancellationToken = default);
}
