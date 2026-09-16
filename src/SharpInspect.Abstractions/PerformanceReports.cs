using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>
/// Purpose of a recomputed report. The framework baseline is the only V1 purpose and it never
/// grants production qualification.
/// </summary>
public enum PerformanceReportPurpose { FrameworkBaseline }

/// <summary>
/// Cadence verification state. An omitted frozen deviation bound remains Unproven and fails
/// the scenario, instead of silently inventing a threshold.
/// </summary>
public enum PerformanceCadenceState { Unproven, Unsatisfied, Satisfied }

public enum PerformanceReportStatus { NotRun, Passed, Failed }

/// <summary>The four durable identities compared with raw observations during reconciliation.</summary>
public enum PerformanceIdentityKind { Accepted, CoreCommit, AcknowledgementReset, FaultTerminated }

/// <summary>Unit of a resource series; the collector contract fixes it per resource identity.</summary>
public enum PerformanceResourceUnit { Count, Bytes, Milliseconds, Percent }

/// <summary>One bounded failure or evidence-defect entry. It never carries payload or part identity.</summary>
public sealed class PerformanceFailure
{
    internal PerformanceFailure(string code, string detail, int? cycleOrdinal = null,
        PerformanceSpan? span = null, PerformanceResource? resource = null)
    {
        Code = AlgorithmContractValidation.Identifier(code, nameof(code));
        Detail = AlgorithmContractValidation.BoundedText(detail, nameof(detail), 512);
        if (cycleOrdinal is < 1) throw new ArgumentOutOfRangeException(nameof(cycleOrdinal));
        if (span is { } spanValue && !Enum.IsDefined(spanValue))
            throw new ArgumentException("PerformanceReportSpanInvalid", nameof(span));
        if (resource is { } resourceValue && !Enum.IsDefined(resourceValue))
            throw new ArgumentException("PerformanceReportResourceInvalid", nameof(resource));
        CycleOrdinal = cycleOrdinal;
        Span = span;
        Resource = resource;
    }

    public string Code { get; }
    public string Detail { get; }
    /// <summary>Null when the failure is not bound to an accepted ordinal.</summary>
    public int? CycleOrdinal { get; }
    public PerformanceSpan? Span { get; }
    public PerformanceResource? Resource { get; }
}

/// <summary>Recomputed statistics and frozen-budget gate for one span of one scenario.</summary>
public sealed class PerformanceSpanReport
{
    internal PerformanceSpanReport(PerformanceSpan span, bool applicable, string applicability,
        int minimumSamples, int maximumFailures, int observedCount, int failureCount, int unknownCount,
        int notApplicableCount, int defectCount, double? p50Milliseconds, double? p95Milliseconds,
        double? p99Milliseconds, double? observedMaxMilliseconds, double? jitterMilliseconds, bool passed)
    {
        if (!Enum.IsDefined(span)) throw new ArgumentException("PerformanceReportSpanInvalid", nameof(span));
        if (minimumSamples < 0 || maximumFailures < 0 || observedCount < 0 || failureCount < 0 ||
            unknownCount < 0 || notApplicableCount < 0 || defectCount < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        Applicability = AlgorithmContractValidation.BoundedText(applicability, nameof(applicability), 512);
        var thresholds = new[] { p50Milliseconds, p95Milliseconds, p99Milliseconds, observedMaxMilliseconds, jitterMilliseconds };
        if (thresholds.Any(value => value is { } number && (!double.IsFinite(number) || number < 0)))
            throw new ArgumentException("PerformanceReportThresholdInvalid", nameof(p50Milliseconds));
        if (thresholds.Any(value => value is null) && thresholds.Any(value => value is not null))
            throw new ArgumentException("PerformanceReportThresholdIncomplete", nameof(p50Milliseconds));
        if (p50Milliseconds > p95Milliseconds || p95Milliseconds > p99Milliseconds ||
            p99Milliseconds > observedMaxMilliseconds ||
            jitterMilliseconds > observedMaxMilliseconds)
            throw new ArgumentException("PerformanceReportThresholdOrderInvalid", nameof(p50Milliseconds));
        Span = span;
        Applicable = applicable;
        MinimumSamples = minimumSamples;
        MaximumFailures = maximumFailures;
        ObservedCount = observedCount;
        FailureCount = failureCount;
        UnknownCount = unknownCount;
        NotApplicableCount = notApplicableCount;
        DefectCount = defectCount;
        P50Milliseconds = p50Milliseconds;
        P95Milliseconds = p95Milliseconds;
        P99Milliseconds = p99Milliseconds;
        ObservedMaxMilliseconds = observedMaxMilliseconds;
        JitterMilliseconds = jitterMilliseconds;
        Passed = passed;
    }

    public PerformanceSpan Span { get; }
    public bool Applicable { get; }
    public string Applicability { get; }
    public int MinimumSamples { get; }
    public int MaximumFailures { get; }
    /// <summary>Measured cycles with a successful complete duration. Warm-up cycles are excluded.</summary>
    public int ObservedCount { get; }
    /// <summary>Measured cycles where the span failed (including frozen expected failures).</summary>
    public int FailureCount { get; }
    public int UnknownCount { get; }
    public int NotApplicableCount { get; }
    /// <summary>Duplicate phase, cross-epoch, negative duration or unexplained missing endpoint.</summary>
    public int DefectCount { get; }
    /// <summary>Nearest-rank p50; null when the successful sample set is empty.</summary>
    public double? P50Milliseconds { get; }
    public double? P95Milliseconds { get; }
    public double? P99Milliseconds { get; }
    public double? ObservedMaxMilliseconds { get; }
    /// <summary>Observed maximum minus minimum of the successful samples.</summary>
    public double? JitterMilliseconds { get; }
    public bool Passed { get; }
}

/// <summary>Recomputed series and required-budget gate for one contract resource.</summary>
public sealed class PerformanceResourceReport
{
    internal PerformanceResourceReport(PerformanceResource resource, bool required, string applicability,
        PerformanceResourceUnit unit, int minimumSamples, double? maximum, double? maximumGrowth,
        int observedCount, int unknownCount, int unavailableCount, int missingCount, int invalidCount,
        double? first, double? last, double? observedMaximum, double? growth, bool resetObserved, bool passed)
    {
        if (!Enum.IsDefined(resource)) throw new ArgumentException("PerformanceReportResourceInvalid", nameof(resource));
        if (!Enum.IsDefined(unit)) throw new ArgumentException("PerformanceReportUnitInvalid", nameof(unit));
        if (minimumSamples < 0 || observedCount < 0 || unknownCount < 0 || unavailableCount < 0 ||
            missingCount < 0 || invalidCount < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        if (new[] { maximum, maximumGrowth, first, last, observedMaximum }
            .Any(value => value is { } number && (!double.IsFinite(number) || number < 0)))
            throw new ArgumentException("PerformanceReportValueInvalid", nameof(maximum));
        if (growth is { } growthValue && !double.IsFinite(growthValue))
            throw new ArgumentException("PerformanceReportValueInvalid", nameof(growth));
        if (observedCount == 0 && new[] { first, last, observedMaximum, growth }.Any(value => value is not null))
            throw new ArgumentException("PerformanceReportSeriesIncomplete", nameof(first));
        if (observedCount > 0 && new[] { first, last, observedMaximum, growth }.Any(value => value is null))
            throw new ArgumentException("PerformanceReportSeriesIncomplete", nameof(first));
        Applicability = AlgorithmContractValidation.BoundedText(applicability, nameof(applicability), 512);
        Resource = resource;
        Required = required;
        Unit = unit;
        MinimumSamples = minimumSamples;
        Maximum = maximum;
        MaximumGrowth = maximumGrowth;
        ObservedCount = observedCount;
        UnknownCount = unknownCount;
        UnavailableCount = unavailableCount;
        MissingCount = missingCount;
        InvalidCount = invalidCount;
        First = first;
        Last = last;
        ObservedMaximum = observedMaximum;
        Growth = growth;
        ResetObserved = resetObserved;
        Passed = passed;
    }

    public PerformanceResource Resource { get; }
    public bool Required { get; }
    public string Applicability { get; }
    public PerformanceResourceUnit Unit { get; }
    public int MinimumSamples { get; }
    public double? Maximum { get; }
    /// <summary>Permitted observed last-minus-first delta of a cumulative counter.</summary>
    public double? MaximumGrowth { get; }
    /// <summary>In-window samples where this resource was observed with a value.</summary>
    public int ObservedCount { get; }
    public int UnknownCount { get; }
    /// <summary>In-window samples with an explicit non-observed, non-unknown outcome.</summary>
    public int UnavailableCount { get; }
    /// <summary>In-window samples that did not carry this resource identity at all.</summary>
    public int MissingCount { get; }
    public int InvalidCount { get; }
    public double? First { get; }
    public double? Last { get; }
    public double? ObservedMaximum { get; }
    /// <summary>Observed last minus first; cumulative counters must stay nonnegative.</summary>
    public double? Growth { get; }
    /// <summary>A declared counter decreased; a reset is unknown, never a zero.</summary>
    public bool ResetObserved { get; }
    public bool Passed { get; }
}

/// <summary>One accepted durable cycle and its observed execution outcome.</summary>
public sealed class PerformanceCycleReport
{
    internal PerformanceCycleReport(int ordinal, bool warmup, Guid correlationId, Guid inspectionId,
        uint controllerEpoch, uint controllerSequence, long admittedPosition, long admittedTimestamp,
        long? triggerObservedTimestamp, long? completedTimestamp, double? elapsedMilliseconds,
        string? expectedFailureReason, IEnumerable<string> observedFailureReasons, bool executionFailed,
        ExecutionStatus? executionStatus, InspectionDecision? decision, string? targetFingerprint,
        string? expectedFrameHash, string? observedFrameHash, PerformanceObservationOutcome? frameOutcome)
    {
        if (ordinal < 1) throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (admittedPosition < 0 || admittedTimestamp < 0)
            throw new ArgumentOutOfRangeException(nameof(admittedPosition));
        if (correlationId == Guid.Empty || inspectionId == Guid.Empty)
            throw new ArgumentException("PerformanceReportCycleIdentityInvalid", nameof(correlationId));
        if (elapsedMilliseconds is { } elapsed && (!double.IsFinite(elapsed) || elapsed < 0))
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        if (executionStatus is { } status && !Enum.IsDefined(status))
            throw new ArgumentException("PerformanceReportExecutionStatusInvalid", nameof(executionStatus));
        if (decision is { } decisionValue && !Enum.IsDefined(decisionValue))
            throw new ArgumentException("PerformanceReportDecisionInvalid", nameof(decisionValue));
        if (frameOutcome is { } outcome && !Enum.IsDefined(outcome))
            throw new ArgumentException("PerformanceReportOutcomeInvalid", nameof(frameOutcome));
        var reasons = AlgorithmContractValidation.Copy(observedFailureReasons, nameof(observedFailureReasons), 32);
        Ordinal = ordinal;
        Warmup = warmup;
        CorrelationId = correlationId;
        InspectionId = inspectionId;
        ControllerEpoch = controllerEpoch;
        ControllerSequence = controllerSequence;
        AdmittedPosition = admittedPosition;
        AdmittedTimestamp = admittedTimestamp;
        TriggerObservedTimestamp = triggerObservedTimestamp;
        CompletedTimestamp = completedTimestamp;
        ElapsedMilliseconds = elapsedMilliseconds;
        ExpectedFailureReason = expectedFailureReason is null ? null
            : AlgorithmContractValidation.BoundedText(expectedFailureReason, nameof(expectedFailureReason), 128);
        ObservedFailureReasons = reasons;
        ExecutionFailed = executionFailed;
        ExecutionStatus = executionStatus;
        Decision = decision;
        TargetFingerprint = targetFingerprint is null ? null
            : AlgorithmContractValidation.BoundedText(targetFingerprint, nameof(targetFingerprint), 256);
        ExpectedFrameHash = expectedFrameHash is null ? null
            : AlgorithmContractValidation.BoundedText(expectedFrameHash, nameof(expectedFrameHash), 128);
        ObservedFrameHash = observedFrameHash is null ? null
            : AlgorithmContractValidation.BoundedText(observedFrameHash, nameof(observedFrameHash), 128);
        FrameOutcome = frameOutcome;
    }

    /// <summary>Ordinal assigned at durable admission; never renumbered by outcomes.</summary>
    public int Ordinal { get; }
    public bool Warmup { get; }
    public Guid CorrelationId { get; }
    public Guid InspectionId { get; }
    public uint ControllerEpoch { get; }
    public uint ControllerSequence { get; }
    public long AdmittedPosition { get; }
    public long AdmittedTimestamp { get; }
    public long? TriggerObservedTimestamp { get; }
    public long? CompletedTimestamp { get; }
    public double? ElapsedMilliseconds { get; }
    public string? ExpectedFailureReason { get; }
    public ReadOnlyCollection<string> ObservedFailureReasons { get; }
    /// <summary>CycleFaulted or a failing AlgorithmOutcomeFixed; a product Decision.Fail with Success is not one.</summary>
    public bool ExecutionFailed { get; }
    public ExecutionStatus? ExecutionStatus { get; }
    public InspectionDecision? Decision { get; }
    public string? TargetFingerprint { get; }
    public string? ExpectedFrameHash { get; }
    public string? ObservedFrameHash { get; }
    public PerformanceObservationOutcome? FrameOutcome { get; }
}

/// <summary>
/// Recomputed intervals compared with the frozen per-interval absolute deviation bound.
/// </summary>
public sealed class PerformanceCadenceReport
{
    internal PerformanceCadenceReport(PerformanceCadenceState state, string reasonCode, int triggerCount,
        double declaredTriggerIntervalMilliseconds, int declaredBurstSize, double declaredBurstIntervalMilliseconds,
        double? minimumIntervalMilliseconds, double? maximumIntervalMilliseconds, double? meanIntervalMilliseconds,
        int observedBurstCount, int maximumObservedBurstLength)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentException("PerformanceReportCadenceStateInvalid", nameof(state));
        if (triggerCount < 0 || declaredBurstSize < 0 || observedBurstCount < 0 || maximumObservedBurstLength < 0)
            throw new ArgumentOutOfRangeException(nameof(triggerCount));
        foreach (var value in new[] { declaredTriggerIntervalMilliseconds, declaredBurstIntervalMilliseconds,
                     minimumIntervalMilliseconds, maximumIntervalMilliseconds, meanIntervalMilliseconds })
            if (value is { } number && (!double.IsFinite(number) || number < 0))
                throw new ArgumentException("PerformanceReportCadenceValueInvalid", nameof(declaredTriggerIntervalMilliseconds));
        State = state;
        ReasonCode = AlgorithmContractValidation.Identifier(reasonCode, nameof(reasonCode));
        TriggerCount = triggerCount;
        DeclaredTriggerIntervalMilliseconds = declaredTriggerIntervalMilliseconds;
        DeclaredBurstSize = declaredBurstSize;
        DeclaredBurstIntervalMilliseconds = declaredBurstIntervalMilliseconds;
        MinimumIntervalMilliseconds = minimumIntervalMilliseconds;
        MaximumIntervalMilliseconds = maximumIntervalMilliseconds;
        MeanIntervalMilliseconds = meanIntervalMilliseconds;
        ObservedBurstCount = observedBurstCount;
        MaximumObservedBurstLength = maximumObservedBurstLength;
    }

    public PerformanceCadenceState State { get; }
    public string ReasonCode { get; }
    public int TriggerCount { get; }
    public double DeclaredTriggerIntervalMilliseconds { get; }
    public int DeclaredBurstSize { get; }
    public double DeclaredBurstIntervalMilliseconds { get; }
    public double? MinimumIntervalMilliseconds { get; }
    public double? MaximumIntervalMilliseconds { get; }
    public double? MeanIntervalMilliseconds { get; }
    /// <summary>Burst split at gaps of at least the declared trigger interval; no tolerance applied.</summary>
    public int ObservedBurstCount { get; }
    public int MaximumObservedBurstLength { get; }
}

/// <summary>Reconciliation counts for one durable identity set.</summary>
public sealed class PerformanceIdentityReconciliation
{
    internal PerformanceIdentityReconciliation(PerformanceIdentityKind kind, int businessLossCount,
        int businessDuplicateCount, int observationMissingCount, int observationDuplicateCount)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("PerformanceReportIdentityInvalid", nameof(kind));
        if (businessLossCount < 0 || businessDuplicateCount < 0 || observationMissingCount < 0 ||
            observationDuplicateCount < 0)
            throw new ArgumentOutOfRangeException(nameof(businessLossCount));
        Kind = kind;
        BusinessLossCount = businessLossCount;
        BusinessDuplicateCount = businessDuplicateCount;
        ObservationMissingCount = observationMissingCount;
        ObservationDuplicateCount = observationDuplicateCount;
    }

    public PerformanceIdentityKind Kind { get; }
    /// <summary>Raw identity with no durable row: the ledger lost a business fact.</summary>
    public int BusinessLossCount { get; }
    /// <summary>Durable duplicate rows: a correctness failure, never an observation defect.</summary>
    public int BusinessDuplicateCount { get; }
    /// <summary>Durable row that the sealed capture never observed.</summary>
    public int ObservationMissingCount { get; }
    /// <summary>Raw duplicate events: an observation defect only.</summary>
    public int ObservationDuplicateCount { get; }
}

/// <summary>Ledger-versus-raw identity reconciliation for the four compared event families.</summary>
public sealed class PerformanceEvidenceReconciliation
{
    internal PerformanceEvidenceReconciliation(bool ledgerAvailable, string ledgerReasonCode,
        IEnumerable<PerformanceIdentityReconciliation> identities)
    {
        var copied = AlgorithmContractValidation.Copy(identities, nameof(identities), 16);
        if (copied.Select(value => value.Kind).Distinct().Count() != copied.Count)
            throw new ArgumentException("PerformanceReportIdentityDuplicate", nameof(identities));
        LedgerAvailable = ledgerAvailable;
        LedgerReasonCode = AlgorithmContractValidation.Identifier(ledgerReasonCode, nameof(ledgerReasonCode));
        Identities = copied;
    }

    public bool LedgerAvailable { get; }
    public string LedgerReasonCode { get; }
    public ReadOnlyCollection<PerformanceIdentityReconciliation> Identities { get; }
    public int BusinessLossCount => Identities.Sum(value => value.BusinessLossCount);
    public int BusinessDuplicateCount => Identities.Sum(value => value.BusinessDuplicateCount);
    public int ObservationMissingCount => Identities.Sum(value => value.ObservationMissingCount);
    public int ObservationDuplicateCount => Identities.Sum(value => value.ObservationDuplicateCount);
}

/// <summary>
/// Immutable recomputation of one sealed baseline capture. It retains raw-derived failures,
/// span and resource gates, cycle outcomes and ledger reconciliation without IO or qualification.
/// </summary>
public sealed class PerformanceScenarioReport
{
    internal PerformanceScenarioReport(PerformanceReportPurpose purpose, Guid runId, string contractHash,
        string scenarioId, string scenarioHash, bool passed, bool captureSealed,
        PerformanceEvidenceState captureState, long observationDrops, int measuredCycles, int warmupCycles,
        int acceptedCycles, double captureDurationMilliseconds, PerformanceCadenceReport cadence,
        PerformanceEvidenceReconciliation evidence, int failureCount, bool failuresTruncated,
        IEnumerable<PerformanceFailure> failures, IEnumerable<PerformanceSpanReport> spans,
        IEnumerable<PerformanceResourceReport> resources, IEnumerable<PerformanceCycleReport> cycles)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentException("PerformanceReportPurposeInvalid", nameof(purpose));
        if (!Enum.IsDefined(captureState)) throw new ArgumentException("PerformanceReportCaptureStateInvalid", nameof(captureState));
        if (observationDrops < 0 || measuredCycles < 0 || warmupCycles < 0 || acceptedCycles < 0 || failureCount < 0)
            throw new ArgumentOutOfRangeException(nameof(failureCount));
        if (!double.IsFinite(captureDurationMilliseconds) || captureDurationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(captureDurationMilliseconds));
        var failureCopy = AlgorithmContractValidation.Copy(failures, nameof(failures), 1024);
        if (failureCopy.Count > failureCount) throw new ArgumentException("PerformanceReportFailureCountInvalid", nameof(failures));
        if (failuresTruncated != failureCount > failureCopy.Count)
            throw new ArgumentException("PerformanceReportFailureTruncationInvalid", nameof(failuresTruncated));
        Purpose = purpose;
        RunId = runId;
        ContractHash = AlgorithmContractValidation.Identifier(contractHash, nameof(contractHash));
        ScenarioId = AlgorithmContractValidation.Identifier(scenarioId, nameof(scenarioId));
        ScenarioHash = AlgorithmContractValidation.Identifier(scenarioHash, nameof(scenarioHash));
        Passed = passed;
        CaptureSealed = captureSealed;
        CaptureState = captureState;
        ObservationDrops = observationDrops;
        MeasuredCycles = measuredCycles;
        WarmupCycles = warmupCycles;
        AcceptedCycles = acceptedCycles;
        CaptureDurationMilliseconds = captureDurationMilliseconds;
        Cadence = cadence ?? throw new ArgumentNullException(nameof(cadence));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        FailureCount = failureCount;
        FailuresTruncated = failuresTruncated;
        Failures = failureCopy;
        Spans = AlgorithmContractValidation.Copy(spans, nameof(spans), 32);
        Resources = AlgorithmContractValidation.Copy(resources, nameof(resources), 64);
        Cycles = AlgorithmContractValidation.Copy(cycles, nameof(cycles), 100_000);
    }

    /// <summary>The only V1 purpose; a baseline report is observability, never a production permit.</summary>
    public PerformanceReportPurpose Purpose { get; }
    /// <summary>Always false: a framework baseline cannot satisfy production qualification.</summary>
    public bool ProductionQualificationAuthority => false;
    /// <summary>An empty value is retained and recorded as a run-identity failure, never repaired.</summary>
    public Guid RunId { get; }
    public string ContractHash { get; }
    public string ScenarioId { get; }
    public string ScenarioHash { get; }
    public bool Passed { get; }
    public bool CaptureSealed { get; }
    public PerformanceEvidenceState CaptureState { get; }
    public long ObservationDrops { get; }
    public int MeasuredCycles { get; }
    public int WarmupCycles { get; }
    public int AcceptedCycles { get; }
    public double CaptureDurationMilliseconds { get; }
    public PerformanceCadenceReport Cadence { get; }
    public PerformanceEvidenceReconciliation Evidence { get; }
    /// <summary>Total failures including entries beyond the bounded retained detail list.</summary>
    public int FailureCount { get; }
    public bool FailuresTruncated { get; }
    public ReadOnlyCollection<PerformanceFailure> Failures { get; }
    public ReadOnlyCollection<PerformanceSpanReport> Spans { get; }
    public ReadOnlyCollection<PerformanceResourceReport> Resources { get; }
    public ReadOnlyCollection<PerformanceCycleReport> Cycles { get; }
}

/// <summary>One frozen scenario inside a full-mix aggregate.</summary>
public sealed class PerformanceAggregateScenario
{
    internal PerformanceAggregateScenario(string scenarioId, PerformanceScenarioKind kind,
        PerformanceReportStatus status, Guid? runId, int reportCount, int failureCount)
    {
        ScenarioId = AlgorithmContractValidation.Identifier(scenarioId, nameof(scenarioId));
        if (!Enum.IsDefined(kind)) throw new ArgumentException("PerformanceReportScenarioKindInvalid", nameof(kind));
        if (!Enum.IsDefined(status)) throw new ArgumentException("PerformanceReportStatusInvalid", nameof(status));
        if (runId == Guid.Empty) throw new ArgumentException("PerformanceReportRunInvalid", nameof(runId));
        if (reportCount < 0 || failureCount < 0) throw new ArgumentOutOfRangeException(nameof(reportCount));
        Kind = kind;
        Status = status;
        RunId = runId;
        ReportCount = reportCount;
        FailureCount = failureCount;
    }

    public string ScenarioId { get; }
    public PerformanceScenarioKind Kind { get; }
    public PerformanceReportStatus Status { get; }
    public Guid? RunId { get; }
    /// <summary>How many reports were supplied for this frozen scenario; more than one is a failure.</summary>
    public int ReportCount { get; }
    public int FailureCount { get; }
}

/// <summary>
/// Full-mix aggregate over one frozen contract. Every frozen scenario id must appear exactly once;
/// a missing scenario is <see cref="PerformanceReportStatus.NotRun"/> and any single-run failure is retained.
/// </summary>
public sealed class PerformanceAggregateReport
{
    internal PerformanceAggregateReport(PerformanceReportPurpose purpose, string contractHash, bool passed,
        int failureCount, bool failuresTruncated, IEnumerable<PerformanceFailure> failures,
        IEnumerable<PerformanceAggregateScenario> scenarios)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentException("PerformanceReportPurposeInvalid", nameof(purpose));
        if (failureCount < 0) throw new ArgumentOutOfRangeException(nameof(failureCount));
        var failureCopy = AlgorithmContractValidation.Copy(failures, nameof(failures), 1024);
        if (failureCopy.Count > failureCount) throw new ArgumentException("PerformanceReportFailureCountInvalid", nameof(failures));
        if (failuresTruncated != failureCount > failureCopy.Count)
            throw new ArgumentException("PerformanceReportFailureTruncationInvalid", nameof(failuresTruncated));
        Purpose = purpose;
        ContractHash = AlgorithmContractValidation.Identifier(contractHash, nameof(contractHash));
        Passed = passed;
        FailureCount = failureCount;
        FailuresTruncated = failuresTruncated;
        Failures = failureCopy;
        Scenarios = AlgorithmContractValidation.Copy(scenarios, nameof(scenarios), 64);
    }

    public PerformanceReportPurpose Purpose { get; }
    /// <summary>Always false: a framework baseline cannot satisfy production qualification.</summary>
    public bool ProductionQualificationAuthority => false;
    public string ContractHash { get; }
    public bool Passed { get; }
    public int FailureCount { get; }
    public bool FailuresTruncated { get; }
    public ReadOnlyCollection<PerformanceFailure> Failures { get; }
    public ReadOnlyCollection<PerformanceAggregateScenario> Scenarios { get; }
}
