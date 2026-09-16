using System.Collections.ObjectModel;

namespace SharpInspect.Abstractions;

/// <summary>Runtime-observed spans. None of these engineering budgets is a hard deadline.</summary>
public enum PerformanceSpan
{
    TriggerToBusy, Acquisition, AlgorithmQueue, AlgorithmExecution, ResultValidation,
    PlcEncoding, DurableImageStage, CoreTraceCommit, RuntimeResultLatency,
    ResultAcknowledgement, AcknowledgementResetToReady, ReadyToReady
}

public enum PerformanceScenarioKind { ColdStart, SteadyState, Burst, Stress, Recovery, Soak }
public enum PerformancePercentileRule { NearestRankV1 }
public enum PerformanceJitterRule { ObservedMaximumMinusMinimumV1 }
public enum PerformanceObservationOutcome { Observed, Failed, TimedOut, Cancelled, NotApplicable, Unknown }
public enum PerformanceEvidenceState { Capturing, Sealed, Incomplete }

/// <summary>Closed resource identities; the unit and sampling semantics are part of the collector contract.</summary>
public enum PerformanceResource
{
    ProcessCpuPercent, WorkingSetBytes, PrivateBytes, ManagedHeapBytes, ManagedCommittedBytes,
    NativeMemoryBytes, NonGcPrivateBytesEstimate, TotalAllocatedBytes, AllocatedBytesPerCycle,
    Gen0Collections, Gen1Collections, Gen2Collections, GcPauseMilliseconds,
    ThreadCount, HandleCount, FramePoolCapacity, OutstandingFrameLeases, PeakFrameLeases,
    FramePoolExhaustions, SqliteDatabaseBytes, SqliteWalBytes, SqliteQueuedWrites,
    SqliteCheckpointCount, ImagePendingCount, ImageActiveOperations, OutboxPendingCount,
    OutboxActiveOperations, DiagnosticQueuedEvents, DiagnosticQueuedBytes, DiagnosticDrops,
    WpfSnapshotsReceived, WpfSnapshotsApplied, WpfSnapshotsCoalesced,
    WpfSnapshotApplyMilliseconds, WpfImageCopies, WpfImageCopyBytes, WpfImageCopyMilliseconds,
    WpfRenderCount, WpfRenderMilliseconds, WpfSnapshotApplyFailures, WpfImageCopyFailures, WpfRenderFailures
}

/// <summary>Only the Runtime and its fixed adapters produce these events. UTC is never used to subtract spans.</summary>
public enum PerformanceEventKind
{
    ReadyAsserted, TriggerObserved, TriggerAccepted, BusyAsserted, FrameReady,
    AlgorithmQueued, AlgorithmStarted, AlgorithmReturned, ResultValidationStarted, ResultValidationCompleted,
    PlcEncodingStarted, PlcEncodingCompleted, ImageStageStarted, ImageStageCompleted,
    CoreCommitStarted, CoreCommitCompleted, ResultValidAsserted, ResultAckObserved,
    ResultValidCleared, AcknowledgementReset, CycleCompleted, CycleFaulted,
    StopRequested, StopCompleted, RecoveryCompleted, TriggerRejected, AlgorithmOutcomeFixed, BudgetViolation
}

/// <summary>A bounded scalar observation, never an image, part identity, payload, path or object dump.</summary>
public sealed record PerformanceEvent(long Sequence, Guid RuntimeEpoch, long Timestamp,
    PerformanceEventKind Kind, ExecutionCorrelationId? Correlation, uint? ControllerEpoch,
    uint? ControllerSequence, PerformanceObservationOutcome Outcome, string? ReasonCode);

public sealed record PerformanceResourceValue(PerformanceResource Resource, double? Value,
    PerformanceObservationOutcome Outcome, string ReasonCode);

public sealed class PerformanceResourceSample
{
    public PerformanceResourceSample(long sequence, long timestamp, IEnumerable<PerformanceResourceValue> values)
    {
        if (sequence < 1 || timestamp < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.Take(65).ToArray();
        if (copy.Length > 64 || copy.Any(value => value is null || !Enum.IsDefined(value.Resource) ||
                !Enum.IsDefined(value.Outcome) || value.Value is { } number && (!double.IsFinite(number) || number < 0) ||
                (value.Outcome == PerformanceObservationOutcome.Observed) != value.Value.HasValue ||
                string.IsNullOrEmpty(value.ReasonCode) || value.ReasonCode.Length > 128) ||
            copy.Select(value => value.Resource).Distinct().Count() != copy.Length)
            throw new ArgumentException("PerformanceResourceSampleInvalid", nameof(values));
        Sequence = sequence; Timestamp = timestamp; Values = Array.AsReadOnly(copy);
    }
    public long Sequence { get; }
    public long Timestamp { get; }
    public ReadOnlyCollection<PerformanceResourceValue> Values { get; }
}

/// <summary>Read-only UI work counters. Reading them does not invoke a dispatcher or render an image.</summary>
public sealed record PresentationPerformanceSnapshot(long SnapshotsReceived, long SnapshotsApplied,
    long SnapshotsCoalesced, long SnapshotApplyTicks, long ImageCopies, long ImageCopyBytes,
    long ImageCopyTicks, long RenderCount, long RenderTicks, long MonotonicFrequency)
{
    public long SnapshotApplyFailures { get; init; }
    public long ImageCopyFailures { get; init; }
    public long RenderFailures { get; init; }
}

public interface IPresentationPerformanceQuery
{
    PresentationPerformanceSnapshot ReadPerformance();
}
