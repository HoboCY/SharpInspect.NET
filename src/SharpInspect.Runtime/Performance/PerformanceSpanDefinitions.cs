using SharpInspect.Abstractions;

namespace SharpInspect.Runtime.Performance;

internal static class PerformanceSpanDefinitions
{
    internal static readonly (PerformanceSpan Span, PerformanceEventKind Start, PerformanceEventKind End)[] All =
    {
        (PerformanceSpan.TriggerToBusy, PerformanceEventKind.TriggerObserved, PerformanceEventKind.BusyAsserted),
        (PerformanceSpan.Acquisition, PerformanceEventKind.BusyAsserted, PerformanceEventKind.FrameReady),
        (PerformanceSpan.AlgorithmQueue, PerformanceEventKind.AlgorithmQueued, PerformanceEventKind.AlgorithmStarted),
        (PerformanceSpan.AlgorithmExecution, PerformanceEventKind.AlgorithmStarted, PerformanceEventKind.AlgorithmReturned),
        (PerformanceSpan.ResultValidation, PerformanceEventKind.ResultValidationStarted, PerformanceEventKind.ResultValidationCompleted),
        (PerformanceSpan.PlcEncoding, PerformanceEventKind.PlcEncodingStarted, PerformanceEventKind.PlcEncodingCompleted),
        (PerformanceSpan.DurableImageStage, PerformanceEventKind.ImageStageStarted, PerformanceEventKind.ImageStageCompleted),
        (PerformanceSpan.CoreTraceCommit, PerformanceEventKind.CoreCommitStarted, PerformanceEventKind.CoreCommitCompleted),
        (PerformanceSpan.RuntimeResultLatency, PerformanceEventKind.BusyAsserted, PerformanceEventKind.ResultValidAsserted),
        (PerformanceSpan.ResultAcknowledgement, PerformanceEventKind.ResultValidAsserted, PerformanceEventKind.ResultAckObserved),
        (PerformanceSpan.AcknowledgementResetToReady, PerformanceEventKind.AcknowledgementReset, PerformanceEventKind.ReadyAsserted),
        (PerformanceSpan.ReadyToReady, PerformanceEventKind.ReadyAsserted, PerformanceEventKind.ReadyAsserted)
    };
}
